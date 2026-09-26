// the smallest WinForms look-alike SpriteControls.cs needs to compile and render headlessly
using System; using System.Drawing;
namespace System.Windows.Forms {
  public enum MouseButtons { None, Left, Right, Middle }
  [Flags] public enum Keys { None = 0, Back = 8, Left = 37, Up = 38, Right = 39, Down = 40, Home = 36, End = 35, PageUp = 33, PageDown = 34, Space = 32, Enter = 13, Escape = 27, Delete = 46, Insert = 45, Add = 107, Subtract = 109, Oemplus = 187, OemMinus = 189, Apps = 93, Menu = 18, C = 67, F = 70, I = 73, N = 78, V = 86, X = 88, D0 = 48, NumPad0 = 96, A = 65, Z = 90, Shift = 0x10000, Control = 0x20000, Alt = 0x40000, KeyCode = 0xFFFF, Modifiers = unchecked((int)0xFFFF0000) }
  public enum Orientation { Horizontal, Vertical }
  public class MouseEventArgs : EventArgs { public MouseButtons Button; public int X, Y, Delta; public Point Location => new Point(X, Y); public MouseEventArgs(MouseButtons b, int clicks, int x, int y, int delta) { Button = b; X = x; Y = y; Delta = delta; } }
  public class HandledMouseEventArgs : MouseEventArgs { public bool Handled; public HandledMouseEventArgs(MouseButtons b, int c, int x, int y, int d) : base(b, c, x, y, d) { } }
  public delegate void KeyEventHandler(object? sender, KeyEventArgs e);
  public class KeyEventArgs : EventArgs { public bool SuppressKeyPress; public Keys KeyData; public Keys KeyCode => KeyData & Keys.KeyCode; public Keys Modifiers => KeyData & Keys.Modifiers; public int KeyValue => (int)KeyCode; public bool Shift => (KeyData & Keys.Shift) != 0; public bool Control => (KeyData & Keys.Control) != 0; public bool Alt => (KeyData & Keys.Alt) != 0; public bool Handled; public KeyEventArgs(Keys k) { KeyData = k; } }
  public class KeyPressEventArgs : EventArgs { public char KeyChar; public bool Handled; public KeyPressEventArgs(char c) { KeyChar = c; } }
  public class LayoutEventArgs : EventArgs { public Control? AffectedControl; public string? AffectedProperty; public LayoutEventArgs(Control? c, string? p) { AffectedControl = c; AffectedProperty = p; } }
  public class ControlEventArgs : EventArgs { public Control Control; public ControlEventArgs(Control c) { Control = c; } }
  public struct Padding { public int Left, Top, Right, Bottom; public Padding(int all) { Left = Top = Right = Bottom = all; } public Padding(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; } public int Horizontal => Left + Right; public int Vertical => Top + Bottom; public static readonly Padding Empty = new Padding(0); }
  public static class Clipboard { static string _t = ""; public static void SetText(string t) => _t = t; public static bool ContainsText() => _t.Length > 0; public static string GetText() => _t; }
  [Flags] public enum ControlStyles { ContainerControl = 1, StandardClick = 0x100, StandardDoubleClick = 0x1000, Selectable = 0x200, UserPaint = 2, AllPaintingInWmPaint = 0x2000, Opaque = 4, ResizeRedraw = 0x40, OptimizedDoubleBuffer = 0x20000, DoubleBuffer = 0x10000 }
  public class Timer : IDisposable { public int Interval { get; set; } public bool Enabled { get; private set; } public event EventHandler? Tick; public void Start() { Enabled = true; } public void Stop() { Enabled = false; } public void Dispose() { } public void Fire() => Tick?.Invoke(this, EventArgs.Empty); }
  public class Cursor { public static Point Position { get; set; } public Cursor() { } public Cursor(IntPtr h) { } } public static class Cursors { public static Cursor Hand = new Cursor(), Default = new Cursor(), SizeAll = new Cursor(), Cross = new Cursor(), IBeam = new Cursor(), SizeWE = new Cursor(), SizeNWSE = new Cursor(), SizeNS = new Cursor(), SizeNESW = new Cursor(); }
  public class CreateParams { public int Style, ExStyle; }
  public struct Message { public int Msg; public IntPtr WParam, LParam, Result; }
  public interface IMessageFilter { bool PreFilterMessage(ref Message m); }
  public static class Application { public static System.Collections.Generic.List<IMessageFilter> Filters = new(); public static void AddMessageFilter(IMessageFilter f) => Filters.Add(f); public static void RemoveMessageFilter(IMessageFilter f) => Filters.Remove(f); }
  public enum FormBorderStyle { None, FixedSingle, Sizable } public enum FormStartPosition { Manual, CenterScreen } public enum DockStyle { None, Top, Bottom, Left, Right, Fill }
  public class Screen { public Rectangle WorkingArea = new Rectangle(0, 0, 1920, 1080); public Rectangle Bounds = new Rectangle(0, 0, 1920, 1080); public static Screen FromPoint(Point p) => new Screen(); }
  public class Form : Control {
    public FormBorderStyle FormBorderStyle { get; set; } public bool ShowInTaskbar { get; set; } public FormStartPosition StartPosition { get; set; } public bool TopMost { get; set; }
    protected virtual bool ShowWithoutActivation => false; protected override void WndProc(ref Message m) { }
    public new Size ClientSize { get => Size; set { Size = value; foreach (var c in Controls) if (c.Dock == DockStyle.Fill) c.Size = value; } }
    public void Show() { Visible = true; } public new void Hide() { Visible = false; }
    public Form? Owner { get; set; } public event EventHandler? Deactivate, LocationChanged, SizeChanged;
    public void FireDeactivate() => Deactivate?.Invoke(this, EventArgs.Empty); void Unused() { LocationChanged?.Invoke(this, EventArgs.Empty); SizeChanged?.Invoke(this, EventArgs.Empty); }
  }
  public enum ImageLayout { None, Center, Stretch, Tile, Zoom } public class PaintEventArgs : EventArgs { public System.Drawing.Graphics Graphics => null!; public Rectangle ClipRectangle; public PaintEventArgs(System.Drawing.Graphics g, Rectangle clip) { ClipRectangle = clip; } }public class ContextMenuStrip { }public enum AccessibleRole { None, Client, PushButton, CheckButton, RadioButton, ComboBox, List, ListItem, PageTab, PageTabList, Grouping, StaticText, Text, Graphic, ScrollBar, Slider, ProgressBar }public enum BorderStyle { None, FixedSingle, Fixed3D } public enum HorizontalAlignment { Left, Right, Center }
  public enum TabAlignment { Top, Bottom, Left, Right }
  public class TextBox : Control { public BorderStyle BorderStyle { get; set; } public HorizontalAlignment TextAlign { get; set; } public int PreferredHeight => 20; public void SelectAll() { } public string SelectedText = ""; }
  public class Control {
    protected virtual void WndProc(ref Message m) { }
    Size _size; public Size Size { get => _size; set { if (_size == value) return; _size = value; OnResize(EventArgs.Empty); } } public Size ClientSize => Size; public virtual Color BackColor { get => _back; set { _back = value; OnBackColorChanged(EventArgs.Empty); } } Color _back; public Color ForeColor { get; set; }
    public virtual bool AutoSize { get; set; } Padding _pad; public Padding Padding { get => _pad; set { _pad = value; OnPaddingChanged(EventArgs.Empty); } } public virtual Rectangle DisplayRectangle => ClientRectangle; public virtual Size GetPreferredSize(Size s) => Size;
    protected virtual void OnPaddingChanged(EventArgs e) { } protected virtual void OnTextChanged(EventArgs e) { } protected virtual void OnParentChanged(EventArgs e) { } protected virtual void OnControlAdded(ControlEventArgs e) { } protected virtual void OnControlRemoved(ControlEventArgs e) { } protected virtual void OnKeyPress(KeyPressEventArgs e) { }
    protected virtual void OnVisibleChanged(EventArgs e) { } protected virtual void OnParentVisibleChanged(EventArgs e) { }   // raised by the Visible setter (see above)
    public void Type(string s) { foreach (char c in s) OnKeyPress(new KeyPressEventArgs(c)); }
    public bool Enabled { get; set; } = true; bool _visible = true;   // real-WinForms semantics: the getter walks the ancestor chain (a child of a hidden form
    // reports Visible == false), flips raise OnVisibleChanged here and OnParentVisibleChanged on the children
    public bool Visible { get { var c = this; while (c != null) { if (!c._visible) return false; c = c._parent!; } return true; } set { if (_visible == value) return; _visible = value; OnVisibleChanged(EventArgs.Empty); foreach (Control k in Controls) k.OnParentVisibleChanged(EventArgs.Empty); } } public Rectangle Bounds { get => new Rectangle(Location, Size); set { Location = value.Location; Size = value.Size; } } public Point Location { get; set; }
    protected virtual CreateParams CreateParams => new CreateParams(); protected virtual void OnResize(EventArgs e) { } public Point PointToScreen(Point p) => p; public Point PointToClient(Point p) => p; public Rectangle RectangleToScreen(Rectangle r) { r.Offset(Location); return r; } public bool TabStop { get; set; } public AccessibleRole AccessibleRole { get; set; } public string? AccessibleName { get; set; } public virtual Cursor Cursor { get; set; } = Cursors.Hand; bool _cap; public bool Capture { get => _cap; set { if (_cap == value) return; _cap = value; if (!value) OnMouseCaptureChanged(EventArgs.Empty); } } public bool Focused => false; public bool IsHandleCreated => true; public bool DesignMode => false; public void BeginInvoke(Action a) => a() /* inline: exercises the re-layout re-entry path headless */; public ContextMenuStrip? ContextMenuStrip { get; set; } public MouseButtons MouseButtons => MouseButtons.None; public void Update() { } public void Refresh() { } public Font Font { get => _font!; set => _font = value; } Font? _font;   // never created: GDI+ throws on non-Windows runtimes
    public virtual Image? BackgroundImage { get => null; set { } } public virtual ImageLayout BackgroundImageLayout { get => ImageLayout.None; set { } } protected virtual void OnPaint(PaintEventArgs e) { } protected virtual void OnPaintBackground(PaintEventArgs e) { }
    string _text = ""; public virtual string Text { get => _text; set { _text = value ?? ""; OnTextChanged(EventArgs.Empty); } } public static Keys ModifierKeys; public IntPtr Handle => IntPtr.Zero;
    public void Focus() { }
    public void PerformLayout() { if (_suspend == 0) OnLayout(new LayoutEventArgs(this, null)); }
    int _suspend; public void SuspendLayout() { _suspend++; } public void ResumeLayout(bool perform) { if (_suspend > 0) _suspend--; if (perform) PerformLayout(); }
    protected virtual void OnLayout(LayoutEventArgs e) { }
    public int DeviceDpi => 96;
    protected void SetStyle(ControlStyles s, bool v) { }
    public void Key(Keys k) { OnKeyDown(new KeyEventArgs(k)); OnKeyUp(new KeyEventArgs(k)); }
    public void DoubleClick(int x, int y) => OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, x, y, 0));
    public void Wheel(int x, int y, int delta) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, x, y, delta));
    public Rectangle ClientRectangle => new Rectangle(0, 0, Size.Width, Size.Height);
    Control? _parent; public Control? Parent { get => _parent; set { _parent = value; OnParentChanged(EventArgs.Empty); } } public ControlCollection Controls { get; } public Control() { Controls = new ControlCollection(this); } public DockStyle Dock { get; set; } public int Width { get => Size.Width; set => Size = new Size(value, Size.Height); } public int Height { get => Size.Height; set => Size = new Size(Size.Width, value); } public void SetBounds(int x, int y, int w, int h) { Location = new Point(x, y); Size = new Size(w, h); } public int Left => Location.X; public int Top => Location.Y; public int Right => Location.X + Size.Width; public int Bottom => Location.Y + Size.Height;
    public void Invalidate() { } public void Invalidate(Rectangle r) { } public Form? FindForm() { Control? c = this; while (c != null && !(c is Form)) c = c.Parent; return c as Form; } public void Hide() { Visible = false; } public void Dispose() => Dispose(true); protected virtual void Dispose(bool disposing) { }
    public class ControlCollection : System.Collections.Generic.List<Control> { readonly Control _o; public ControlCollection(Control o) { _o = o; } public new void Add(Control c) { base.Add(c); c.Parent = _o; _o.OnControlAdded(new ControlEventArgs(c)); } public new bool Remove(Control c) { bool r = base.Remove(c); if (r) { c.Parent = null; _o.OnControlRemoved(new ControlEventArgs(c)); } return r; } }
    public event EventHandler? Click; protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);
    public event EventHandler? LostFocus, GotFocus; public event KeyEventHandler? KeyDown; public void BringToFront() { } public void FireLostFocus() { OnLostFocus(EventArgs.Empty); LostFocus?.Invoke(this, EventArgs.Empty); } void UnusedEvents() { GotFocus?.Invoke(this, EventArgs.Empty); KeyDown?.Invoke(this, new KeyEventArgs(Keys.None)); }
    protected virtual void OnKeyUp(KeyEventArgs e) { }
    protected virtual void OnHandleCreated(EventArgs e) { } protected virtual void OnHandleDestroyed(EventArgs e) { }
    public void CreateHandle() => OnHandleCreated(EventArgs.Empty);
    protected virtual bool ShowFocusCues => false;
    protected virtual void OnMouseDown(MouseEventArgs e) { } protected virtual void OnMouseMove(MouseEventArgs e) { } protected virtual void OnMouseUp(MouseEventArgs e) { }
    protected virtual void OnMouseCaptureChanged(EventArgs e) { } protected virtual void OnMouseDoubleClick(MouseEventArgs e) { } protected virtual void OnMouseWheel(MouseEventArgs e) { }
    protected virtual void OnMouseEnter(EventArgs e) { } protected virtual void OnMouseLeave(EventArgs e) { } protected virtual void OnGotFocus(EventArgs e) { } protected virtual void OnLostFocus(EventArgs e) { }
    protected virtual void OnEnabledChanged(EventArgs e) { } protected virtual void OnForeColorChanged(EventArgs e) { } protected virtual void OnBackColorChanged(EventArgs e) { }
    protected virtual bool IsInputKey(Keys k) => false; protected virtual void OnKeyDown(KeyEventArgs e) { KeyDown?.Invoke(this, e); }
    // test hooks
    public void Down(int x, int y) => OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    public void Move(int x, int y) => OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, y, 0));
    public void Up(int x, int y) => OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    public void Tap(int x, int y) { Down(x, y); Up(x, y); }
    public void Enter() => OnMouseEnter(EventArgs.Empty);
  }
}
namespace System.ComponentModel.Design { public interface IDesigner { } }
namespace Microsoft.Win32 { public class RegistryKey : IDisposable { public object? GetValue(string n) => null; public RegistryKey? OpenSubKey(string n) => null; public void Dispose() { } } public static class Registry { public static RegistryKey CurrentUser = new RegistryKey(); } }
namespace System.ComponentModel {
  [AttributeUsage(AttributeTargets.All)] public class DesignerAttribute : Attribute { public DesignerAttribute(string s, Type t) { } }
  [AttributeUsage(AttributeTargets.All)] public class CategoryAttribute : Attribute { public CategoryAttribute(string s) { } }
  [AttributeUsage(AttributeTargets.All)] public class DescriptionAttribute : Attribute { public DescriptionAttribute(string s) { } }
  [AttributeUsage(AttributeTargets.All)] public class BrowsableAttribute : Attribute { public BrowsableAttribute(bool b) { } }
  [AttributeUsage(AttributeTargets.All)] public class BindableAttribute : Attribute { public BindableAttribute(bool b) { } }
  [AttributeUsage(AttributeTargets.All)] public class EditorBrowsableAttribute : Attribute { public EditorBrowsableAttribute(EditorBrowsableState s) { } }
  public enum EditorBrowsableState { Always, Never, Advanced }
  [AttributeUsage(AttributeTargets.All)] public class DesignerSerializationVisibilityAttribute : Attribute { public DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility v) { } }
  public enum DesignerSerializationVisibility { Hidden, Visible, Content }
  [AttributeUsage(AttributeTargets.All)] public class DefaultValueAttribute : Attribute { public DefaultValueAttribute(object? o) { } public DefaultValueAttribute(double d) { } public DefaultValueAttribute(int i) { } public DefaultValueAttribute(bool b) { } public DefaultValueAttribute(string s) { } public DefaultValueAttribute(Type t, string s) { } }
}
