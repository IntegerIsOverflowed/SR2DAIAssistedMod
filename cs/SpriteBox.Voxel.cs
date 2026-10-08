using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings also brings System.Threading.Timer

namespace Sr2d64CSport
{
    /// <summary>Camera presets of a <see cref="VoxelBox"/>.</summary>
    public enum VoxelCameraView { Free, Isometric, ThreeQuarter, TopDown, Side }

    /// <summary>What the left mouse button does on a <see cref="VoxelBox"/>.</summary>
    public enum VoxelDragMode
    {
        /// <summary>Left drag orbits (yaw / pitch), middle drag or Space + left pans, wheel zooms, right click = menu (default).</summary>
        Orbit,
        /// <summary>Left drag pans; orbit with the middle button.</summary>
        Pan,
        /// <summary>The left button is left to the application (VoxelClick / Pick); middle = pan, Space + left = orbit.</summary>
        None,
    }

    /// <summary>Data of <see cref="VoxelBox.VoxelClick"/> / <see cref="VoxelBox.VoxelHover"/>: which voxel and face is under the pointer.</summary>
    public sealed class VoxelHitEventArgs : EventArgs
    {
        public readonly int Index, Face, X, Y, Z; public readonly MouseButtons Button; public readonly Point Client;
        public bool Hit => Index >= 0;
        public VoxelHitEventArgs(int index, int face, (int x, int y, int z) c, MouseButtons b, Point p) { Index = index; Face = face; X = c.x; Y = c.y; Z = c.z; Button = b; Client = p; }
    }

    /// <summary>
    /// A <see cref="SpriteBox"/> that is a viewport onto a <see cref="VoxelGrid"/>: attach a grid with <see cref="Grid"/> and the
    /// control renders it with the camera / lighting / fade settings that the bench voxel tests use, driven by the mouse
    /// (orbit, pan, zoom), the keyboard and an SR2D-drawn settings menu (right click) - no native ContextMenuStrip.
    ///
    ///     var box = new VoxelBox { Dock = DockStyle.Fill };
    ///     box.Grid = VoxelGrid.LoadVox("house.vox");     // or any grid you build / edit; call box.Redraw() after editing it
    ///     box.View = VoxelCameraView.Isometric; box.Lighting = VoxelLighting.Smooth; box.Fade = VoxelFade.Depth;
    ///     box.VoxelClick += (s, e) => { if (e.Hit) grid.Set(grid.Neighbour(e.Index, e.Face), new Voxel(0xFFFF8000)); box.Redraw(); };
    ///
    /// Rendering: the picture is drawn only when something changed (a camera move, a setting, <see cref="Redraw"/>), never per
    /// paint. Big grids: <see cref="Parallel"/> renders in bands on all cores, <see cref="PreviewWhileDragging"/> renders a
    /// cheaper picture (Points mode, no lighting) while the mouse button is down and the full one once it is released
    /// (or after <see cref="PreviewSettleMs"/> of no movement). The exposed camera (<see cref="Camera"/>) is rebuilt from
    /// the settings each frame; <see cref="CameraChanged"/> lets you tweak it (light direction, shade tables) before use.
    /// </summary>
    [ToolboxBitmap(typeof(VoxelBox), "VoxelBox.bmp")]
    public class VoxelBox : SpriteBox
    {
        VoxelGrid? _grid; bool _ownsGrid;
        VoxelCameraView _view = VoxelCameraView.Free; int _turn; float _yaw = 0.8f, _pitch = 0.55f, _panX, _panY, _zoom = 4f;
        VoxelLighting _lighting = VoxelLighting.Smooth; VoxelMode _mode = VoxelMode.Auto;
        VoxelFade _fade = VoxelFade.None; float _fadeMin = 0.25f, _fadeGamma = 1f;
        int _sky = 15, _lightReach = 15; float _lampEnergy = 1f, _skyEnergy = 1f; bool _night;
        bool _parallel = true, _preview = true, _showInfo, _showAxes, _anim; float _animSpeed = 0.5f;
        VoxelDragMode _dragMode = VoxelDragMode.Orbit;
        static readonly Color DefaultNightBack = Color.FromArgb(5, 7, 12);
        Color _back = Color.FromArgb(0x10, 0x14, 0x18), _nightBack = DefaultNightBack;
        float _light1 = -1f, _light2 = -0.6f, _light3 = 1.6f, _ambient = 0.4f;
        VoxelCamera? _cam; SpriteMenu? _menu; bool _menuEnabled = true;
        // interaction
        enum Drag { None, Orbit, Pan }
        Drag _drag; Point _last; bool _space, _previewing, _moved; Timer? _settle, _animTimer; readonly Stopwatch _clock = Stopwatch.StartNew(); double _animLast;
        int _previewSettleMs = 250;
        double _lastMs, _fullMs, _pickCost, _lastPickMs; int _lastDrawn;
        public VoxelBox()
        {
            AccessibleRole = AccessibleRole.Graphic;      // screen readers: a picture of the voxel scene
            BackColor = _back;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        }

        // ------------------------------------------------------------------ model
        /// <summary>The grid shown. The box does not own it unless <see cref="OwnsGrid"/>; call <see cref="SpriteBox.Redraw"/> after editing it.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public VoxelGrid? Grid
        {
            get => _grid;
            set { if (_grid == value) return; if (_ownsGrid) _grid?.Dispose(); _grid = value; _ownsGrid = false; GridChanged?.Invoke(this, EventArgs.Empty); Redraw(); }
        }
        /// <summary>Dispose the grid with the control (and when another grid is attached). Default false.</summary>
        [Category("Behavior"), DefaultValue(false)] public bool OwnsGrid { get => _ownsGrid; set => _ownsGrid = value; }
        /// <summary>Loads a .vox / .obj file into a new grid the box owns. Returns the grid.</summary>
        public VoxelGrid Load(string path, int objResolution = 64)
        {
            var g = path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? VoxelGrid.FromObj(path, objResolution) : VoxelGrid.LoadVox(path);
            Grid = g; _ownsGrid = true; return g;
        }
        /// <summary>The camera used for the last frame (null before the first); rebuilt from the settings each frame.</summary>
        [Browsable(false)] public VoxelCamera? Camera => _cam;
        /// <summary>Raised with the freshly built camera before every frame: adjust Shade / AO / LightCurve / SkyColor here.</summary>
        public event Action<VoxelBox, VoxelCamera>? CameraChanged;
        /// <summary>Raised when another grid was attached.</summary>
        public event EventHandler? GridChanged;
        /// <summary>Raised after every rendered frame (<see cref="LastFrameMs"/>, <see cref="LastDrawnVoxels"/> are current).</summary>
        public event EventHandler? FrameRendered;
        /// <summary>Left click (drag mode None: any click; otherwise a click without movement) on the picture with the voxel under the pointer.</summary>
        public event EventHandler<VoxelHitEventArgs>? VoxelClick;
        /// <summary>Raised on mouse move while <see cref="TrackHover"/> is on. A pick is a (single-threaded, unlit) render of the grid, so the box skips moves while the previous pick is still more recent than the frame time.</summary>
        public event EventHandler<VoxelHitEventArgs>? VoxelHover;
        [Category("Behavior"), DefaultValue(false)] public bool TrackHover { get; set; }
        [Browsable(false)] public double LastFrameMs => _lastMs;
        [Browsable(false)] public int LastDrawnVoxels => _lastDrawn;

        // ------------------------------------------------------------------ camera settings
        [Category("Voxel camera"), DefaultValue(VoxelCameraView.Free), Description("Camera: Free (orbit with the mouse) or a pixel-art preset (Isometric, 3/4, Top-down, Side) turned by Turn.")]
        public new VoxelCameraView View { get => _view; set { if (_view == value) return; _view = value; Changed(); } }
        [Category("Voxel camera"), DefaultValue(0), Description("Quarter turns of a preset view (0..3).")]
        public int Turn { get => _turn; set { value &= 3; if (_turn == value) return; _turn = value; Changed(); } }
        /// <summary>Free camera: compass direction the camera looks along, radians.</summary>
        [Category("Voxel camera"), DefaultValue(0.8f)] public float Yaw { get => _yaw; set { _yaw = value; Changed(); } }
        /// <summary>Free camera: downward tilt, radians (0 = horizontal, pi/2 = straight down).</summary>
        [Category("Voxel camera"), DefaultValue(0.55f)] public float Pitch { get => _pitch; set { _pitch = Math.Clamp(value, -1.55f, 1.55f); Changed(); } }
        /// <summary>Pixels per voxel (presets: 1 = one pixel per voxel, Points mode when Auto).</summary>
        [Category("Voxel camera"), DefaultValue(4f)] public new float Zoom { get => _zoom; set { value = Math.Clamp(value, 0.1f, 256f); if (_zoom == value) return; _zoom = value; Changed(); } }
        [Category("Voxel camera"), DefaultValue(0.1f)] public new float MinZoom { get; set; } = 0.1f;
        [Category("Voxel camera"), DefaultValue(64f)] public new float MaxZoom { get; set; } = 64f;
        /// <summary>Screen offset of the grid centre from the control centre, pixels.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public new PointF Pan { get => new PointF(_panX, _panY); set { _panX = value.X; _panY = value.Y; Changed(); } }
        [Category("Voxel camera"), DefaultValue(VoxelMode.Auto), Description("Points (one pixel per voxel) / Cubes / Auto.")]
        public VoxelMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; Changed(); } }
        [Category("Voxel camera"), DefaultValue(VoxelDragMode.Orbit), Description("What the left button does: Orbit, Pan, or None (left to the application; VoxelClick fires).")]
        public VoxelDragMode DragMode { get => _dragMode; set => _dragMode = value; }
        [Category("Voxel camera"), DefaultValue(false), Description("Spin the free camera slowly (AnimationSpeed radians per second).")]
        public bool Animate { get => _anim; set { if (_anim == value) return; _anim = value; SetupAnim(); } }
        [Category("Voxel camera"), DefaultValue(0.5f)] public float AnimationSpeed { get => _animSpeed; set => _animSpeed = value; }

        // ------------------------------------------------------------------ lighting settings
        [Category("Voxel lighting"), DefaultValue(VoxelLighting.Smooth), Description("Lighting tier: None (flat colours), Faces, Propagated (sky + lamps), Smooth (+ ambient occlusion).")]
        public VoxelLighting Lighting { get => _lighting; set { if (_lighting == value) return; _lighting = value; Changed(); } }
        [Category("Voxel lighting"), DefaultValue(15), Description("Sky light level 0..15 poured in from above (Propagated / Smooth).")]
        public int SkyLight { get => _sky; set { value = Math.Clamp(value, 0, 15); if (_sky == value) return; _sky = value; Changed(); } }
        [Category("Voxel lighting"), DefaultValue(false), Description("Night: dark background, sky level from NightSkyLight; lamps dominate.")]
        public bool Night { get => _night; set { if (_night == value) return; _night = value; Changed(); } }
        [Category("Voxel lighting"), DefaultValue(6)] public int NightSkyLight { get; set; } = 6;
        [Category("Voxel lighting"), DefaultValue(15), Description("Cells a full-strength light travels (15 = Minecraft rule .. 120).")]
        public int LightReach { get => _lightReach; set { value = Math.Clamp(value, 15, 120); if (_lightReach == value) return; _lightReach = value; Changed(); } }
        [Category("Voxel lighting"), DefaultValue(1f), Description("Multiplier on the lamp channels (above 1 lamps over-drive their surroundings).")]
        public float LampEnergy { get => _lampEnergy; set { _lampEnergy = Math.Max(0.05f, value); Changed(); } }
        [Category("Voxel lighting"), DefaultValue(1f)] public float SkyEnergy { get => _skyEnergy; set { _skyEnergy = Math.Max(0.05f, value); Changed(); } }
        [Category("Voxel lighting"), DefaultValue(VoxelFade.None), Description("Depth fade after the lighting tier: Height, Depth, both, or Depth | Fog (towards the background colour).")]
        public VoxelFade Fade { get => _fade; set { if (_fade == value) return; _fade = value; Changed(); } }
        [Category("Voxel lighting"), DefaultValue(0.25f)] public float FadeMin { get => _fadeMin; set { _fadeMin = Math.Clamp(value, 0f, 1f); Changed(); } }
        [Category("Voxel lighting"), DefaultValue(1f)] public float FadeGamma { get => _fadeGamma; set { _fadeGamma = Math.Max(0.05f, value); Changed(); } }
        /// <summary>Light direction (towards the light) and ambient for the face shades. Default (-1, -0.6, 1.6), 0.4.</summary>
        public void SetLight(float x, float y, float z, float ambient = 0.4f) { _light1 = x; _light2 = y; _light3 = z; _ambient = ambient; Changed(); }
        [Category("Voxel lighting")] public Color NightBackColor { get => _nightBack; set { _nightBack = value; Changed(); } }
        bool ShouldSerializeNightBackColor() => _nightBack != DefaultNightBack;

        // ------------------------------------------------------------------ performance / overlay settings
        [Category("Behavior"), DefaultValue(true), Description("Render in horizontal bands on all cores (worth it above ~100k drawn voxels).")]
        public bool Parallel { get => _parallel; set { _parallel = value; Changed(); } }
        [Category("Behavior"), DefaultValue(true), Description("While the mouse button is down draw a cheap preview (Points, no lighting); the full picture follows on release / after PreviewSettleMs.")]
        public bool PreviewWhileDragging { get => _preview; set => _preview = value; }
        [Category("Behavior"), DefaultValue(250)] public int PreviewSettleMs { get => _previewSettleMs; set => _previewSettleMs = Math.Clamp(value, 0, 5000); }
        [Category("Appearance"), DefaultValue(false), Description("Draw the frame statistics (grid size, drawn voxels, ms, camera) in the corner.")]
        public bool ShowInfo { get => _showInfo; set { _showInfo = value; Changed(); } }
        [Category("Appearance"), DefaultValue(false), Description("Draw the grid axes (x red, y green, z blue) at the grid origin.")]
        public bool ShowAxes { get => _showAxes; set { _showAxes = value; Changed(); } }
        [Category("Behavior"), DefaultValue(true), Description("Right click opens the SR2D-drawn settings menu.")]
        public bool SettingsMenu { get => _menuEnabled; set => _menuEnabled = value; }
        /// <summary>The settings menu (built on first use); add your own items to it.</summary>
        [Browsable(false)] public SpriteMenu Menu => _menu ??= BuildMenu();

        // ------------------------------------------------------------------ actions
        /// <summary>Camera back to the default orbit / zoom / pan for the current view.</summary>
        public void ResetCamera() { _yaw = 0.8f; _pitch = 0.55f; _panX = _panY = 0; _turn = 0; _zoom = _view == VoxelCameraView.Free ? 4f : 2f; Changed(); }
        public new void ResetPan() { _panX = _panY = 0; Changed(); }
        /// <summary>Zoom so the whole grid fits the control.</summary>
        public new void FitToView()
        {
            if (_grid == null) return;
            var unit = BuildCamera(1f); var b = unit.Bounds(_grid.Width, _grid.Height, _grid.Depth);
            float s = MathF.Min((ClientSize.Width - 16) / MathF.Max(1f, b.Width), (ClientSize.Height - 16) / MathF.Max(1f, b.Height));
            if (_view != VoxelCameraView.Free) s = MathF.Max(1f, MathF.Floor(s));   // presets: whole pixels per voxel keep the pixel art crisp
            _zoom = Math.Clamp(s, MinZoom, MaxZoom); _panX = _panY = 0; Changed();
        }
        /// <summary>Zoom about a client point (the voxel under the pointer stays put).</summary>
        public void ZoomAt(float zoom, Point client)
        {
            zoom = Math.Clamp(zoom, MinZoom, MaxZoom); if (zoom == _zoom) return;
            float cx = ClientSize.Width / 2f + _panX, cy = ClientSize.Height / 2f + _panY;
            float k = zoom / _zoom;
            _panX = client.X - (client.X - cx) * k - ClientSize.Width / 2f; _panY = client.Y - (client.Y - cy) * k - ClientSize.Height / 2f;
            _zoom = zoom; Changed();
        }
        /// <summary>Voxel under a client point (renders a pick pass). Index -1 = nothing.</summary>
        public (int index, int face) Pick(Point client)
        {
            if (_grid == null || _cam == null) return (-1, -1);
            var s = Surface; if ((uint)client.X >= (uint)s.Width || (uint)client.Y >= (uint)s.Height) return (-1, -1);
            return _grid.Pick(s, _cam, s.Width / 2f + _panX, s.Height / 2f + _panY, client.X, client.Y);
        }
        /// <summary>Screen rectangle the grid covers.</summary>
        public Rectangle GridScreenBounds() => _grid == null || _cam == null ? Rectangle.Empty : _grid.ScreenBounds(_cam, ClientSize.Width / 2f + _panX, ClientSize.Height / 2f + _panY);

        void Changed() { Redraw(); }

        // ------------------------------------------------------------------ rendering
        protected override bool HasRenderer => true;
        VoxelCamera BuildCamera(float scale)
        {
            VoxelCamera cam = _view switch
            {
                VoxelCameraView.Isometric => VoxelCamera.Isometric(scale, _turn),
                VoxelCameraView.ThreeQuarter => VoxelCamera.ThreeQuarter(scale, _turn),
                VoxelCameraView.TopDown => VoxelCamera.TopDown(scale, _turn),
                VoxelCameraView.Side => VoxelCamera.Side(scale, _turn),
                _ => VoxelCamera.Free(_yaw, _pitch, scale),
            };
            cam.Mode = _mode; cam.Anchor = VoxelAnchor.Center;
            cam.SetLight(_light1, _light2, _light3, _ambient);
            for (int i = 0; i < 16; i++) cam.LightCurve[i] = 0.05f + 0.95f * MathF.Pow(0.8f, 15 - i);
            cam.LampEnergy = _lampEnergy; cam.SkyEnergy = _skyEnergy;
            cam.Fade = _fade; cam.FadeMin = _fadeMin; cam.FadeGamma = _fadeGamma; cam.FadeColor = (_night ? _nightBack : BackColor).ToArgb() & 0xFFFFFF;
            return cam;
        }
        protected override void OnRender(Sprite s)
        {
            int bg = (_night ? _nightBack : BackColor).ToArgb();
            s.ClearBuffer(bg);
            if (_grid == null) { DrawInfo(s, "no VoxelGrid attached (VoxelBox.Grid)"); return; }
            var sw = Stopwatch.StartNew();
            var cam = BuildCamera(_zoom);
            var lt = _lighting;
            bool preview = _previewing && _preview;
            if (preview)
            {   // cheap picture while the mouse is down: no propagated light / AO; Points (1 px per voxel) only when the last full frame was slow
                if (lt > VoxelLighting.Faces) lt = VoxelLighting.Faces;
                if (_fullMs > 40 || _grid.Width * (long)_grid.Height * _grid.Depth > 128L * 128 * 128) { cam.Mode = VoxelMode.Points; lt = VoxelLighting.None; }
            }
            CameraChanged?.Invoke(this, cam);
            _cam = cam;
            _grid.SkyLight = _night ? NightSkyLight : _sky; _grid.LightReach = _lightReach;
            float x = s.Width / 2f + _panX, y = s.Height / 2f + _panY;
            _lastDrawn = _parallel ? s.DrawVoxelsParallel(_grid, cam, x, y, lt) : s.DrawVoxels(_grid, cam, x, y, lt);
            if (_showAxes) DrawAxes(s, cam, x, y);
            _lastMs = sw.Elapsed.TotalMilliseconds; if (!preview) _fullMs = _lastMs;
            if (_showInfo)
            {
                string camName = _view == VoxelCameraView.Free ? $"free yaw {_yaw * 180 / MathF.PI:F0} pitch {_pitch * 180 / MathF.PI:F0}" : $"{_view} turn {_turn}";
                DrawInfo(s, $"{_grid.Width}x{_grid.Height}x{_grid.Depth}  {_lastDrawn / 1000} k drawn  {_lastMs:F1} ms{(preview ? " (preview)" : "")}  x{cam.Scale:0.#} {cam.EffectiveMode}  {camName}  {lt}{(_fade != VoxelFade.None ? " " + _fade : "")}{(_night ? " night" : "")}");
            }
            FrameRendered?.Invoke(this, EventArgs.Empty);
        }
        void DrawInfo(Sprite s, string text) => s.DrawText(6, s.Height - 6, text, unchecked((int)0xFFE0E4E8), unchecked((int)0xA0101418), 1, 0, 0, SR2D.LineOp.AlphaBlend, 128, TextAnchor.BottomLeft);
        void DrawAxes(Sprite s, VoxelCamera cam, float x, float y)
        {
            if (_grid == null) return;
            var a = _grid.AnchorOffset(cam); float ox = x - a.X, oy = y - a.Y; float len = MathF.Max(8f, cam.Scale * 6f);
            void Axis(float dx, float dy, float dz, int c) { var p = cam.Project(dx, dy, dz); s.DrawWideLine(ox, oy, ox + p.X / cam.Scale * len, oy + p.Y / cam.Scale * len, c, 2f, true, SR2D.LineOp.Set, true); }
            Axis(1, 0, 0, unchecked((int)0xFFFF4040)); Axis(0, 1, 0, unchecked((int)0xFF40FF40)); Axis(0, 0, 1, unchecked((int)0xFF4080FF));
        }

        // ------------------------------------------------------------------ mouse / keyboard
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right) { if (_menuEnabled) { Menu.Show(this, e.Location, e.Button); } return; }
            Drag d = Drag.None;
            if (e.Button == MouseButtons.Middle) d = _dragMode == VoxelDragMode.Pan ? Drag.Orbit : Drag.Pan;
            else if (e.Button == MouseButtons.Left)
            {
                if (_space) d = _dragMode == VoxelDragMode.None ? Drag.Orbit : Drag.Pan;
                else if (_dragMode == VoxelDragMode.Orbit) d = Drag.Orbit;
                else if (_dragMode == VoxelDragMode.Pan) d = Drag.Pan;
                else { RaiseHit(VoxelClick, e.Location, e.Button); return; }
            }
            if (d == Drag.None) return;
            if (d == Drag.Orbit && _view != VoxelCameraView.Free) { /* presets do not orbit: a left drag pans them */ d = Drag.Pan; }
            _drag = d; _last = e.Location; _moved = false; Capture = true; Cursor = d == Drag.Pan ? SpriteCursors.Get(SpriteCursors.Kind.HandGrab, this) : SpriteCursors.Get(SpriteCursors.Kind.Rotate, this);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag == Drag.None)
            {
                if (TrackHover && VoxelHover != null && _grid != null && _clock.Elapsed.TotalMilliseconds - _lastPickMs > Math.Max(30, _pickCost * 2)) { var sw = Stopwatch.StartNew(); RaiseHit(VoxelHover, e.Location, e.Button); _pickCost = sw.Elapsed.TotalMilliseconds; _lastPickMs = _clock.Elapsed.TotalMilliseconds; }
                return;
            }
            int dx = e.X - _last.X, dy = e.Y - _last.Y; _last = e.Location;
            if (dx == 0 && dy == 0) return;
            _moved = true;
            if (_drag == Drag.Orbit) { _yaw -= dx * 0.01f; _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.55f, 1.55f); }
            else { _panX += dx; _panY += dy; }
            if (_preview) { _previewing = true; RestartSettle(); }
            RedrawNow();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag == Drag.None) return;
            var was = _drag; _drag = Drag.None; Capture = false; Cursor = Cursors.Default;
            if (!_moved && e.Button == MouseButtons.Left && was != Drag.None && _dragMode != VoxelDragMode.None && !_space) RaiseHit(VoxelClick, e.Location, e.Button);
            if (_previewing) { _settle?.Stop(); _previewing = false; Redraw(); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (_drag != Drag.None && !Capture) { _drag = Drag.None; Cursor = Cursors.Default; if (_previewing) { _previewing = false; Redraw(); } } }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float f = MathF.Pow(1.25f, e.Delta / 120f);
            float z = _zoom * f;
            if (_view != VoxelCameraView.Free) { z = e.Delta > 0 ? MathF.Min(MaxZoom, MathF.Floor(_zoom) + 1) : MathF.Max(MinZoom, MathF.Ceiling(_zoom) - 1); if (z < 1f) z = e.Delta > 0 ? 1f : MathF.Max(MinZoom, _zoom / 2f); }
            ZoomAt(z, e.Location);
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }
                protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Add or Keys.Subtract || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Space: _space = true; e.Handled = true; break;
                case Keys.Left: if (_view == VoxelCameraView.Free) _yaw += 0.1f; else Turn = _turn + 1; e.Handled = true; Changed(); break;
                case Keys.Right: if (_view == VoxelCameraView.Free) _yaw -= 0.1f; else Turn = _turn + 3; e.Handled = true; Changed(); break;
                case Keys.Up: if (_view == VoxelCameraView.Free) { Pitch = _pitch + 0.1f; e.Handled = true; } break;
                case Keys.Down: if (_view == VoxelCameraView.Free) { Pitch = _pitch - 0.1f; e.Handled = true; } break;
                case Keys.Add: case Keys.Oemplus: ZoomAt(_view == VoxelCameraView.Free ? _zoom * 1.25f : MathF.Floor(_zoom) + 1, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); e.Handled = true; break;
                case Keys.Subtract: case Keys.OemMinus: ZoomAt(_view == VoxelCameraView.Free ? _zoom / 1.25f : MathF.Max(1, MathF.Ceiling(_zoom) - 1), new Point(ClientSize.Width / 2, ClientSize.Height / 2)); e.Handled = true; break;
                case Keys.Home: ResetCamera(); e.Handled = true; break;
                case Keys.F: FitToView(); e.Handled = true; break;
                case Keys.I: ShowInfo = !ShowInfo; e.Handled = true; break;
                case Keys.N: Night = !Night; e.Handled = true; break;
                case Keys.Apps: if (_menuEnabled) Menu.Show(this, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); e.Handled = true; break;
            }
        }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) _space = false; }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _space = false; }
        void RaiseHit(EventHandler<VoxelHitEventArgs>? ev, Point p, MouseButtons b)
        {
            if (ev == null || _grid == null) return;
            var (idx, face) = Pick(p);
            ev(this, new VoxelHitEventArgs(idx, face, idx >= 0 ? _grid.Coords(idx) : (-1, -1, -1), b, p));
        }
        void RestartSettle()
        {
            if (_settle == null) { _settle = new Timer(); _settle.Tick += (_, _) => { _settle!.Stop(); if (_previewing) { _previewing = false; Redraw(); } }; }
            _settle.Interval = Math.Max(1, _previewSettleMs); _settle.Stop(); _settle.Start();
        }
        void SetupAnim()
        {
            if (_anim)
            {
                if (_animTimer == null) { _animTimer = new Timer { Interval = 16 }; _animTimer.Tick += (_, _) => { double now = _clock.Elapsed.TotalSeconds; float dt = (float)(now - _animLast); _animLast = now; if (_view == VoxelCameraView.Free) { _yaw += _animSpeed * dt; Redraw(); } }; }
                _animLast = _clock.Elapsed.TotalSeconds; _animTimer.Start();
            }
            else _animTimer?.Stop();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }

        // ------------------------------------------------------------------ settings menu (SR2D-drawn)
        static readonly string[] ViewNames = { "Free orbit", "Isometric", "3/4 view", "Top-down", "Side" };
        static readonly string[] LightNames = { "None (flat colours)", "Faces", "Propagated (sky + lamps)", "Smooth + AO" };
        static readonly string[] FadeNames = { "No fade", "Height", "Depth", "Height + depth", "Fog (depth, to background)" };
        static readonly VoxelFade[] FadeValues = { VoxelFade.None, VoxelFade.Height, VoxelFade.Depth, VoxelFade.Height | VoxelFade.Depth, VoxelFade.Depth | VoxelFade.Fog };
        SpriteMenu BuildMenu()
        {
            var m = new SpriteMenu { Title = "Voxel view", MinWidth = 220 };
            var cam = m.AddSub("Camera");
            cam.AddRadioGroup(ViewNames, () => (int)_view, i => { View = (VoxelCameraView)i; if (_view != VoxelCameraView.Free && _zoom < 1f) Zoom = 1f; });
            cam.AddSeparator();
            cam.Add("Turn preset  (Left / Right)", () => Turn = _turn + 1, hint: null, enabled: () => _view != VoxelCameraView.Free).HintProvider = () => _view == VoxelCameraView.Free ? null! : "turn " + _turn;
            cam.AddCheck("Animate (spin)", () => _anim, v => Animate = v);
            cam.AddSeparator();
            cam.AddHeader("Left button");
            cam.AddRadioGroup(new[] { "Orbit (Space = pan)", "Pan (middle = orbit)", "Nothing (clicks pick voxels)" }, () => (int)_dragMode, i => DragMode = (VoxelDragMode)i);
            var mode = m.AddSub("Voxel mode");
            mode.AddRadioGroup(new[] { "Auto", "Points (1 px)", "Cubes" }, () => _mode == VoxelMode.Auto ? 0 : _mode == VoxelMode.Points ? 1 : 2, i => Mode = i == 0 ? VoxelMode.Auto : i == 1 ? VoxelMode.Points : VoxelMode.Cubes);
            var light = m.AddSub("Lighting");
            light.AddRadioGroup(LightNames, () => (int)_lighting, i => Lighting = (VoxelLighting)i);
            light.AddSeparator();
            light.AddCheck("Night  (N)", () => _night, v => Night = v);
            light.AddSlider("Sky light", 0, 15, () => _night ? NightSkyLight : _sky, v => { if (_night) NightSkyLight = (int)v; else SkyLight = (int)v; Changed(); }, 1);
            light.AddSlider("Lamp energy", 0.1, 6, () => _lampEnergy, v => LampEnergy = (float)v, 0.1, 1, " x");
            light.AddSlider("Light reach (cells)", 15, 120, () => _lightReach, v => LightReach = (int)v, 1);
            var fade = m.AddSub("Depth fade");
            fade.AddRadioGroup(FadeNames, () => Array.IndexOf(FadeValues, _fade) < 0 ? 0 : Array.IndexOf(FadeValues, _fade), i => Fade = FadeValues[i]);
            fade.AddSlider("Far brightness", 0, 1, () => _fadeMin, v => FadeMin = (float)v, 0.05, 2);
            fade.AddSlider("Curve (gamma)", 0.25, 3, () => _fadeGamma, v => FadeGamma = (float)v, 0.05, 2);
            m.AddSeparator();
            m.AddSlider("Zoom (px / voxel)", 0.25, 32, () => _zoom, v => Zoom = (float)v, 0.25, 2);
            m.Add("Fit to view  (F)", FitToView);
            m.Add("Reset camera  (Home)", ResetCamera);
            m.Add("Reset position", ResetPan);
            m.AddSeparator();
            m.AddCheck("Parallel render", () => _parallel, v => Parallel = v);
            m.AddCheck("Preview while dragging", () => _preview, v => PreviewWhileDragging = v);
            m.AddCheck("Show info  (I)", () => _showInfo, v => ShowInfo = v);
            m.AddCheck("Show axes", () => _showAxes, v => ShowAxes = v);
            return m;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _settle?.Dispose(); _animTimer?.Dispose(); _menu?.Dispose(); _menu = null; if (_ownsGrid) _grid?.Dispose(); _grid = null; }
            base.Dispose(disposing);
        }
    }
}
