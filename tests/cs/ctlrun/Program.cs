using System; using System.Collections.Generic; using System.Drawing; using System.IO; using System.Linq; using Sr2d64CSport; using System.Windows.Forms;
static class P {
  static void Chk(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Environment.Exit(1); }
  static void Buttons() {
    var c = new Sprite(900, 560); c.ClearBuffer(unchecked((int)0xFF202428));
    var parent = new Control();
    int y = 10;
    // buttons: 4 shapes, normal / hot / pressed / accented / disabled
    int x = 10;
    foreach (var sh in new[]{ButtonShape.Rounded, ButtonShape.Pill, ButtonShape.Square}) {
      var b = new SpriteButton { Text = sh.ToString(), Shape = sh, Size = new Size(120, 36) }; c.Draw(b.RenderOnce(), x, y, SR2D.Op.Paint);
      var bh = new SpriteButton { Text = "hot", Shape = sh, Size = new Size(120, 36) }; bh.Enter(); c.Draw(bh.RenderOnce(), x, y + 44, SR2D.Op.Paint);
      var bp = new SpriteButton { Text = "pressed", Shape = sh, Size = new Size(120, 36) }; bp.Down(10, 10); c.Draw(bp.RenderOnce(), x, y + 88, SR2D.Op.Paint); bp.Up(10,10);
      var ba = new SpriteButton { Text = "Accented", Shape = sh, Accented = true, Size = new Size(120, 36) }; c.Draw(ba.RenderOnce(), x, y + 132, SR2D.Op.Paint);
      var bd = new SpriteButton { Text = "disabled", Shape = sh, Enabled = false, Size = new Size(120, 36) }; c.Draw(bd.RenderOnce(), x, y + 176, SR2D.Op.Paint);
      x += 130;
    }
    int clicks = 0; var rb = new SpriteButton { Text = "GO", Shape = ButtonShape.Round, Size = new Size(72, 72) }; rb.Click += (_, _) => clicks++; rb.Tap(30, 30); c.Draw(rb.RenderOnce(), x, y, SR2D.Op.Paint);
    var rb2 = new SpriteButton { Text = "!", Shape = ButtonShape.Round, Accented = true, Size = new Size(48, 48) }; rb2.Down(20, 20); c.Draw(rb2.RenderOnce(), x, y + 90, SR2D.Op.Paint);
    rb.Key(Keys.Space); rb.Key(Keys.Enter);
    int bad = 0; var cancel = new SpriteButton { Size = new Size(120, 36) }; cancel.Click += (_, _) => bad++; cancel.Down(10, 10); cancel.Capture = false; cancel.Up(10, 10);
    var outside = new SpriteButton { Size = new Size(120, 36) }; outside.Click += (_, _) => bad++; outside.Down(10, 10); outside.Move(300, 10); outside.Up(300, 10);
    Console.WriteLine($"round button clicks after Tap + Space + Enter = {clicks} (expect 3); clicks after lost capture / release outside = {bad} (expect 0)");
    if (clicks != 3 || bad != 0) Environment.Exit(1);
    // toggles: 4 styles x (off, on, on pressed) + disabled
    y = 240; x = 10;
    foreach (var st in new[]{ToggleStyle.Switch, ToggleStyle.Ellipse, ToggleStyle.Rocker, ToggleStyle.CheckBox}) {
      int changes = 0;
      var t0 = new SpriteToggle { Text = st + " off", Style = st, Size = new Size(210, 30) }; t0.CheckedChanged += (_, _) => changes++; c.Draw(t0.RenderOnce(), x, y, SR2D.Op.Paint);
      var t1 = new SpriteToggle { Text = st + " on", Style = st, Size = new Size(210, 30) }; t1.Tap(20, 15); c.Draw(t1.RenderOnce(), x, y + 36, SR2D.Op.Paint);
      var t2 = new SpriteToggle { Text = "pressed", Style = st, Checked = true, Size = new Size(210, 30) }; t2.Down(20, 15); c.Draw(t2.RenderOnce(), x, y + 72, SR2D.Op.Paint);
      var t3 = new SpriteToggle { Text = "disabled", Style = st, Checked = true, Enabled = false, Size = new Size(210, 30) }; c.Draw(t3.RenderOnce(), x, y + 108, SR2D.Op.Paint);
      var big = new SpriteToggle { Style = st, Checked = st != ToggleStyle.Ellipse, Size = new Size(110, 48) }; c.Draw(big.RenderOnce(), x, y + 146, SR2D.Op.Paint);
      t0.Tap(5, 5); t0.Tap(5, 5);
      t0.Key(Keys.Space);
      Console.WriteLine($"{st}: t1 checked={t1.Checked} (expect True), t0 changes after 2 taps + Space={changes} (expect 3), t0.Checked={t0.Checked} (expect True)");
      if (!t1.Checked || changes != 3 || !t0.Checked) Environment.Exit(1);
      x += 220;
    }
    // animation: toggles mid-flight (handle created -> Animated works; StepAnimations pumps the shared clock)
    {
      var anim = new Sprite(900, 200); anim.ClearBuffer(unchecked((int)0xFF202428));
      var styles = new[]{ToggleStyle.Switch, ToggleStyle.Ellipse, ToggleStyle.Rocker, ToggleStyle.CheckBox};
      for (int si = 0; si < 4; si++) {
        var tg = new SpriteToggle { Style = styles[si], Size = new Size(110, 40), OnText = "ON", OffText = "OFF" }; tg.CreateHandle();
        tg.Checked = true;                                    // starts the animation (0 -> 1 over 180 ms)
        for (int f = 0; f < 5; f++) { anim.Draw(tg.RenderOnce(), 10 + si * 220, 10 + f * 38, SR2D.Op.Paint); Sr2d64CSport.SpriteControlBase.StepAnimations(45); }
      }
      var rd = new SpriteRadio { Text = "anim", Size = new Size(120, 30) }; rd.CreateHandle(); rd.Checked = true;
      for (int f = 0; f < 5; f++) { anim.Draw(rd.RenderOnce(), 780, 10 + f * 38, SR2D.Op.Paint); Sr2d64CSport.SpriteControlBase.StepAnimations(50); }
      Save(anim, "/home/user/.cache/ctlrun/anim.rgba");
      var snap = new SpriteToggle { Style = ToggleStyle.Switch, Animated = false, Size = new Size(110, 40) }; snap.CreateHandle(); snap.Checked = true;
      Console.WriteLine($"Animated=false snaps: rendered on-state immediately = {snap.Checked}");
    }
    // radios
    y = 450; x = 10;
    var radios = new SpriteRadio[4];
    for (int i = 0; i < 4; i++) { radios[i] = new SpriteRadio { Text = "Option " + (i + 1), Size = new Size(120, 24), Parent = parent }; parent.Controls.Add(radios[i]); }
    radios[0].Checked = true; radios[2].Tap(10, 10);
    Console.WriteLine($"radios after selecting 3rd: {string.Join(",", Array.ConvertAll(radios, r => r.Checked))} (expect False,False,True,False)");
    radios[1].Key(Keys.Space); radios[2].Tap(10, 10); radios[2].Tap(10, 10);
    Console.WriteLine($"radios after Space on 2nd, then 2 taps on 3rd: {string.Join(",", Array.ConvertAll(radios, r => r.Checked))} (expect False,False,True,False)");
    if (radios[0].Checked || radios[1].Checked || !radios[2].Checked || radios[3].Checked) Environment.Exit(1);
    for (int i = 0; i < 4; i++) c.Draw(radios[i].RenderOnce(), x, y + i * 26, SR2D.Op.Paint);
    var rdis = new SpriteRadio { Text = "disabled", Checked = true, Enabled = false, Size = new Size(120, 24) }; c.Draw(rdis.RenderOnce(), x + 130, y, SR2D.Op.Paint);
    var rbig = new SpriteRadio { Text = "Big", Checked = true, Size = new Size(150, 40) }; rbig.Enter(); c.Draw(rbig.RenderOnce(), x + 130, y + 30, SR2D.Op.Paint);
    // progress bars
    x = 300;
    var ph = new SpriteProgress { Value = 62, Size = new Size(260, 22) }; c.Draw(ph.RenderOnce(), x, y, SR2D.Op.Paint);
    var pseg = new SpriteProgress { Value = 62, Segments = 12, Size = new Size(260, 22) }; c.Draw(pseg.RenderOnce(), x, y + 28, SR2D.Op.Paint);
    var pmq = new SpriteProgress { Marquee = true, Phase = 0.6f, Text = "loading", Size = new Size(260, 22) }; c.Draw(pmq.RenderOnce(), x, y + 56, SR2D.Op.Paint);
    var pthin = new SpriteProgress { Value = 30, ShowPercent = false, Size = new Size(260, 8) }; c.Draw(pthin.RenderOnce(), x, y + 84, SR2D.Op.Paint);
    var pv = new SpriteProgress { Value = 62, Style = ProgressStyle.Vertical, Size = new Size(22, 100) }; c.Draw(pv.RenderOnce(), x + 270, y, SR2D.Op.Paint);
    var pv2 = new SpriteProgress { Value = 40, Style = ProgressStyle.Vertical, Segments = 10, ShowPercent = false, Size = new Size(22, 100) }; c.Draw(pv2.RenderOnce(), x + 300, y, SR2D.Op.Paint);
    var pr = new SpriteProgress { Value = 62, Style = ProgressStyle.Ring, Size = new Size(100, 100) }; c.Draw(pr.RenderOnce(), x + 340, y, SR2D.Op.Paint);
    var pr2 = new SpriteProgress { Value = 62, Style = ProgressStyle.Ring, Segments = 12, Size = new Size(100, 100), AccentColor = Color.FromArgb(0x60,0xE0,0x80) }; c.Draw(pr2.RenderOnce(), x + 450, y, SR2D.Op.Paint);
    var pr3 = new SpriteProgress { Marquee = true, Phase = 0.85f, Style = ProgressStyle.Ring, Size = new Size(60, 60) }; c.Draw(pr3.RenderOnce(), x + 560, y, SR2D.Op.Paint);
    Save(c, "/home/user/.cache/ctlrun/buttons.rgba");
  }
  static void Wheels() {
    var c = new Sprite(1000, 420); c.ClearBuffer(unchecked((int)0xFF202428));
    // vertical wheels: plain, wrap-around degrees, editable, horizontal, reversed, disabled
    var w1 = new SpriteWheel { Text = "Level", Minimum = 0, Maximum = 20, Value = 7, Size = new Size(64, 150) }; c.Draw(w1.RenderOnce(), 10, 10, SR2D.Op.Paint);
    var w2 = new SpriteWheel { Text = "Angle", Minimum = 0, Maximum = 360, Value = 355, Step = 5, WrapAround = true, Unit = "\u00b0", Size = new Size(72, 150), AccentColor = Color.FromArgb(0xFF,0x90,0x30) }; c.Draw(w2.RenderOnce(), 84, 10, SR2D.Op.Paint);
    var w3 = new SpriteWheel { Text = "Editable", Minimum = -10, Maximum = 10, Value = 2.5, Step = 0.5, Decimals = 1, Editable = true, Size = new Size(120, 150), AccentColor = Color.FromArgb(0x60,0xE0,0x80) }; c.Draw(w3.RenderOnce(), 166, 10, SR2D.Op.Paint);
    var w4 = new SpriteWheel { Text = "Horizontal", Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 42, Size = new Size(300, 56) }; c.Draw(w4.RenderOnce(), 296, 10, SR2D.Op.Paint);
    var w5 = new SpriteWheel { Text = "Horizontal editable, wrap 0..24", Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 24, Value = 23, WrapAround = true, Editable = true, Size = new Size(300, 56), AccentColor = Color.FromArgb(0xE0,0x60,0xC0) }; c.Draw(w5.RenderOnce(), 296, 76, SR2D.Op.Paint);
    var w6 = new SpriteWheel { Text = "Reversed", Minimum = 0, Maximum = 20, Value = 7, Reversed = true, Size = new Size(64, 150) }; c.Draw(w6.RenderOnce(), 606, 10, SR2D.Op.Paint);
    var w7 = new SpriteWheel { Text = "off", Minimum = 0, Maximum = 20, Value = 7, Enabled = false, Size = new Size(64, 150) }; c.Draw(w7.RenderOnce(), 680, 10, SR2D.Op.Paint);
    var w8 = new SpriteWheel { Minimum = 0, Maximum = 9, Value = 3, ShowValue = false, Size = new Size(36, 90) }; c.Draw(w8.RenderOnce(), 754, 10, SR2D.Op.Paint);
    var w9 = new SpriteWheel { Text = "big", Minimum = 0, Maximum = 50, Value = 25, TextScale = 3, Size = new Size(200, 150), AccentColor = Color.FromArgb(0xFF,0xD0,0x70) }; c.Draw(w9.RenderOnce(), 800, 10, SR2D.Op.Paint);
    // drag mechanics: vertical, drag up 3 pitches -> +3 steps; wrap-mouse: the pointer leaving the bottom is teleported to the top and the drag continues
    var d = new SpriteWheel { Minimum = 0, Maximum = 100, Value = 50, Size = new Size(64, 128) }; d.CreateHandle();
    int pitch = 0; { var probe = new SpriteWheel { Minimum = 0, Maximum = 100, Value = 50, Size = new Size(64, 128) }; probe.Down(32, 64); probe.Move(32, 54); pitch = (int)Math.Round(10 / (probe.Value - 50)); probe.Up(32, 54); }
    int ev = 0; d.ValueChanged += (_, _) => ev++;
    d.Down(32, 64); d.Move(32, 64 - 3 * pitch); double vUp = d.Value; d.Up(32, 64 - 3 * pitch);
    Console.WriteLine($"wheel: pitch {pitch} px/step, drag up 3 pitches: 50 -> {vUp} (expect 53), events {ev}");
    if (Math.Abs(vUp - 53) > 0.01) Environment.Exit(1);
    // wrap mouse: drag downwards past the bottom edge; the fake Cursor.Position records the teleport
    d.Down(32, 100); d.Move(32, 140); d.Move(32, 1085); var cur = Cursor.Position; double vWrap = d.Value; d.Move(32, cur.Y - 20); double vWrap2 = d.Value; d.Up(32, cur.Y - 20);
    Console.WriteLine($"wheel wrap-mouse: after crossing the screen bottom (1080) the cursor was put at y={cur.Y} (expect 2, the top edge), value {vWrap:0.##}, continuing upward from there -> {vWrap2:0.##} (expect {vWrap:0.##} + {20.0 / pitch:0.##})");
    if (cur.Y != 2 || Math.Abs(vWrap2 - (vWrap + 20.0 / pitch)) > 0.01) Environment.Exit(1);
    var nw = new SpriteWheel { Minimum = 0, Maximum = 100, Value = 50, WrapMouse = false, Size = new Size(64, 128) }; nw.CreateHandle(); Cursor.Position = new Point(-1, -1);
    nw.Down(32, 100); nw.Move(32, 140); nw.Up(32, 140);
    Console.WriteLine($"wheel WrapMouse=false: cursor untouched = {Cursor.Position == new Point(-1, -1)}");
    if (Cursor.Position != new Point(-1, -1)) Environment.Exit(1);
    // wrap-around range: 355 + 2 steps of 5 -> 5 (360 folds onto 0); keys too
    w2.Key(Keys.Up); w2.Key(Keys.Up); double wa = w2.Value; w2.Key(Keys.Down); w2.Key(Keys.Down); w2.Key(Keys.Down); double wb = w2.Value;
    Console.WriteLine($"wrap-around: 355 +5 +5 -> {wa} (expect 5), then -5 x3 -> {wb} (expect 350)");
    if (wa != 5 || wb != 350) Environment.Exit(1);
    // editable: text box round trip
    var fld = w3.EditField!; if (!(fld is SpriteNumeric) || fld.Parent != w3 || w3.Edit != WheelEdit.Beside) Environment.Exit(1);
    fld.Key(Keys.A | Keys.Control); fld.Type("7,5"); fld.Key(Keys.Enter); double e1 = w3.Value; fld.Key(Keys.A | Keys.Control); fld.Type("abc"); fld.Key(Keys.Enter); double e2 = w3.Value; fld.Key(Keys.A | Keys.Control); fld.Type("99"); fld.Key(Keys.Enter); double e3 = w3.Value;
    Console.WriteLine($"editable (SR2D field beside the drum): typed 7,5 -> {e1} (expect 7.5 if the culture uses a comma, else unchanged 2.5), abc -> {e2} (unchanged), 99 -> {e3} (expect 10, clamped); field shows '{fld.Text}'");
    if (e3 != 10 || fld.Value != 10) Environment.Exit(1);
    w3.Nudge(-2); if (fld.Value != w3.Value || w3.Value != 9) Environment.Exit(1);   // the field follows the wheel
    // pop-up field: DoubleClick mode opens it over the read-out, Enter applies and closes, Escape closes without applying; Click mode opens on a plain click, a drag does not
    var wdc = new SpriteWheel { Minimum = 0, Maximum = 100, Value = 50, Edit = WheelEdit.DoubleClick, Size = new Size(64, 128) }; wdc.CreateHandle();
    wdc.DoubleClick(32, 64); bool open1 = wdc.IsEditing; var f2 = wdc.EditField!; f2.Key(Keys.A | Keys.Control); f2.Type("70"); f2.Key(Keys.Enter); bool open2 = wdc.IsEditing; double v1 = wdc.Value;
    wdc.DoubleClick(32, 64); f2.Key(Keys.A | Keys.Control); f2.Type("80"); f2.Key(Keys.Escape); bool open3 = wdc.IsEditing; double v2 = wdc.Value;
    Console.WriteLine($"wheel Edit=DoubleClick: dbl-click opens {open1} (True), Enter applies 70 -> {v1} and closes ({!open2}), Escape leaves {v2} (70) and closes ({!open3})");
    if (!open1 || open2 || v1 != 70 || open3 || v2 != 70) Environment.Exit(1);
    var wc = new SpriteWheel { Minimum = 0, Maximum = 100, Value = 50, Edit = WheelEdit.Click, Style = WheelStyle.Flat, Size = new Size(64, 128) }; wc.CreateHandle();
    wc.Down(32, 64); wc.Move(32, 40); wc.Up(32, 40); bool openAfterDrag = wc.IsEditing; double vDrag = wc.Value;
    wc.Tap(32, 64); bool openAfterClick = wc.IsEditing; wc.EditField!.Key(Keys.Escape);
    Console.WriteLine($"wheel Edit=Click: a drag changes the value (50 -> {vDrag}) without opening ({openAfterDrag} = False), a click opens ({openAfterClick} = True)");
    if (openAfterDrag || vDrag == 50 || !openAfterClick) Environment.Exit(1);
    c.Draw(wdc.RenderOnce(), 830, 180, SR2D.Op.Paint); wc.Tap(32, 64); c.Draw(wc.RenderOnce(), 900, 180, SR2D.Op.Paint);
    // Flat style renders too and differs from Detailed
    var wf = new SpriteWheel { Text = "Flat", Minimum = 0, Maximum = 20, Value = 7, Style = WheelStyle.Flat, Size = new Size(64, 150) }; c.Draw(wf.RenderOnce(), 10, 180, SR2D.Op.Paint);
    // deferred commit (CommitOnRelease) on slider / knob / wheel: no ValueChanged until release, preview meanwhile, Escape cancels
    int y = 180;
    var sl = new SpriteSlider { Text = "CommitOnRelease", Minimum = 0, Maximum = 100, Value = 20, CommitOnRelease = true, Size = new Size(260, 52) }; sl.CreateHandle();
    int slEv = 0, slPrev = 0; sl.ValueChanged += (_, _) => slEv++; sl.ValuePreview += (_, _) => slPrev++;
    sl.Down(60, 36); sl.Move(120, 36); sl.Move(180, 36);
    Sr2d64CSport.SpriteControlBase.StepAnimations(200);                 // the lift animation has finished
    c.Draw(sl.RenderOnce(), 10, y, SR2D.Op.Paint);
    double during = sl.Value, pend = sl.PendingValue; int evDuring = slEv;
    sl.Up(180, 36); double after = sl.Value; int evAfter = slEv;
    Console.WriteLine($"slider CommitOnRelease: during drag value {during} (expect 20) pending {pend:0} events {evDuring} (expect 0) previews {slPrev} (expect 3); after release {after:0} (= pending) events {evAfter} (expect 1)");
    if (during != 20 || evDuring != 0 || slPrev != 3 || after != pend || evAfter != 1) Environment.Exit(1);
    for (int f = 0; f < 4; f++) { Sr2d64CSport.SpriteControlBase.StepAnimations(45); c.Draw(sl.RenderOnce(), 280 + f * 0, y + 60 + f * 0, SR2D.Op.Paint); }   // dropped back
    // cancel with Escape
    sl.Down(180, 36); sl.Move(60, 36); sl.Key(Keys.Escape); double canc = sl.Value; bool dragging = sl.IsDragging; sl.Up(60, 36);
    Console.WriteLine($"slider CommitOnRelease + Escape: value {canc:0} (expect {after:0}, unchanged), dragging after Escape = {dragging} (expect False), events {slEv} (expect 1)");
    if (canc != after || dragging || slEv != 1) Environment.Exit(1);
    var kn = new SpriteKnob { Text = "Deferred", Minimum = 0, Maximum = 100, Value = 30, CommitOnRelease = true, Size = new Size(120, 150) }; kn.CreateHandle();
    int knEv = 0; kn.ValueChanged += (_, _) => knEv++;
    kn.Down(60, 20); kn.Move(100, 60); Sr2d64CSport.SpriteControlBase.StepAnimations(200); c.Draw(kn.RenderOnce(), 560, y, SR2D.Op.Paint);
    double kDuring = kn.Value; kn.Up(100, 60); double kAfter = kn.Value;
    var knRef = new SpriteKnob { Text = "Deferred", Minimum = 0, Maximum = 100, Value = 30, Size = new Size(120, 150) }; knRef.Down(60, 20); knRef.Move(100, 60); knRef.Up(100, 60);
    Console.WriteLine($"knob CommitOnRelease: during {kDuring} (expect 30) events {knEv} (expect 1 after release), after {kAfter:0} (same drag on an immediate knob: {knRef.Value:0})");
    if (kDuring != 30 || knEv != 1 || kAfter != knRef.Value) Environment.Exit(1);
    var wd = new SpriteWheel { Text = "Deferred", Minimum = 0, Maximum = 100, Value = 50, CommitOnRelease = true, Size = new Size(64, 150) }; wd.CreateHandle();
    int wdEv = 0; wd.ValueChanged += (_, _) => wdEv++;
    wd.Down(32, 80); wd.Move(32, 80 - 4 * pitch); Sr2d64CSport.SpriteControlBase.StepAnimations(200); c.Draw(wd.RenderOnce(), 700, y, SR2D.Op.Paint);
    double wDuring = wd.Value; wd.Up(32, 80 - 4 * pitch); double wAfter = wd.Value;
    Console.WriteLine($"wheel CommitOnRelease: during {wDuring} (expect 50) after {wAfter} (expect 54) events {wdEv} (expect 1)");
    if (wDuring != 50 || Math.Abs(wAfter - 54) > 0.01 || wdEv != 1) Environment.Exit(1);
    // infinite knob keeps its free pointer through a deferred drag
    var ki = new SpriteKnob { Text = "Inf deferred", Minimum = 0, Maximum = 100, Value = 50, Pointer = KnobPointer.Infinite, Gauge = KnobGauge.Circle, CommitOnRelease = true, Size = new Size(120, 150) }; ki.CreateHandle();
    ki.Down(60 + 40, 80); for (int i = 1; i <= 10; i++) { double a = i * Math.PI * 2 / 20; ki.Move((int)(60 + 40 * Math.Cos(a)), (int)(80 + 40 * Math.Sin(a))); }
    Sr2d64CSport.SpriteControlBase.StepAnimations(200); c.Draw(ki.RenderOnce(), 830, y, SR2D.Op.Paint);
    double kiDuring = ki.Value, kiPend = ki.PendingValue, degBefore = ki.PointerDegrees; ki.Up(60, 120); double kiAfter = ki.Value, degAfter = ki.PointerDegrees;
    Console.WriteLine($"infinite knob CommitOnRelease: during {kiDuring} pending {kiPend:0} (expect 100, half turn = 50), after {kiAfter:0}, pointer {degBefore:0} -> {degAfter:0} deg (must not jump)");
    if (kiDuring != 50 || Math.Abs(kiAfter - kiPend) > 1e-9 || Math.Abs(degBefore - degAfter) > 0.01) Environment.Exit(1);
    Save(c, "/home/user/.cache/ctlrun/wheels.rgba");
  }
  static unsafe void EdgeStrips() {
    // the "black line at the edge of a Zoom-mode picture" regression: the Compose() strip arithmetic, replayed for every
    // viewport width 300..1300 at four heights with a 1024x1024 white image on a cyan background - no pixel may stay black,
    // and the image must still cover its whole destination (within one edge row / column).
    var img = new Sprite(1024, 1024); img.ClearBuffer(unchecked((int)0xFFFFFFFF));
    int bg = unchecked((int)0xFF00FFFF), bad = 0, total = 0;
    for (int vw = 300; vw <= 1300; vw += 7) for (int vh = 700; vh <= 703; vh++) {
      var v = SpriteView.For(new Size(vw, vh), new Size(1024, 1024), SpriteSizeMode.Zoom);
      var dest = v.Dest; using var scr = new Sprite(vw, vh); scr.ClearBuffer(0);
      int dl = (int)MathF.Ceiling(dest.Left), dt = (int)MathF.Ceiling(dest.Top), dr = (int)MathF.Floor(dest.Right), db = (int)MathF.Floor(dest.Bottom);   // == SpriteBox.Compose
      if (dt > 0) scr.ClearRect(0, vw, 0, Math.Min(dt, vh), bg);
      if (db < vh) scr.ClearRect(0, vw, Math.Max(db, 0), vh, bg);
      if (dl > 0) scr.ClearRect(0, Math.Min(dl, vw), Math.Max(dt, 0), Math.Min(db, vh), bg);
      if (dr < vw) scr.ClearRect(Math.Max(dr, 0), vw, Math.Max(dt, 0), Math.Min(db, vh), bg);
      float sx = v.ScaleX, sy = v.ScaleY;
      scr.SetLockRect(0, vw, 0, vh); scr.DrawScaled(img, dest.X, dest.Y, sx, sy, 0f, 0f, SR2D.Op.Paint, sx >= 1f ? SR2D.Filter.Nearest : SR2D.Filter.Bilinear); scr.SetLockRect();
      int* p = (int*)scr.PixelPtr; int blk = 0, white = 0;
      for (int i = 0; i < vw * vh; i++) { if (p[i] == 0) blk++; else if (p[i] == unchecked((int)0xFFFFFFFF)) white++; }
      int expW = (int)MathF.Round(dest.Width) * (int)MathF.Round(dest.Height);
      total++; if (blk > 0 || Math.Abs(white - expW) > 2 * (vw + vh)) { bad++; if (bad < 4) Console.WriteLine($"  edge strips {vw}x{vh}: black {blk}, white {white} vs ~{expW}"); }
    }
    Console.WriteLine($"edge strips: {total} viewport sizes, {bad} with a hole (expect 0)");
    if (bad > 0) Environment.Exit(1);
    // offline mapping helpers
    var g = SpriteView.For(new Size(800, 600), new Size(1024, 1024), SpriteSizeMode.Zoom);
    var inside = g.Hit(new Point(400, 300)); var outside = g.Hit(new Point(795, 300)); var far = g.PixelAt(new Point(-50, -50));
    Console.WriteLine($"  For(800x600, 1024^2, Zoom): scale {g.ScaleX:0.####} dest {g.Dest}; centre -> {inside}; 795,300 -> {outside} pixel {outside.Pixel}; PixelAt(-50,-50) = {far}");
    if (!inside.Inside || inside.Pixel != new Point(512, 512) || outside.Inside || outside.Pixel.X <= 1023 || far.X >= 0 || far.Y >= 0) Environment.Exit(1);
    var c = g.Clone(); c.Zoom = 2; if (g.Zoom != 1 || c.ScaleX != g.ScaleX * 2) Environment.Exit(1);
    Console.WriteLine("  Clone() independent, PixelAt extrapolates outside: ok");
  }
  static void Views() {
    // SpriteView: the geometry behind SpriteBox.SizeMode (no WinForms) - mapping, modes, overscroll, zoom anchor, scroll model, inertia
    var v = new SpriteView { ImageSize = new Size(400, 300), ViewportSize = new Size(800, 400), Mode = SpriteSizeMode.CenterImage };
    var d = v.Dest; Console.WriteLine($"CenterImage 400x300 in 800x400: dest {d} (expect 200,50 400x300)");
    if (d != new RectangleF(200, 50, 400, 300)) Environment.Exit(1);
    var h = v.Hit(new Point(200, 50)); var h2 = v.Hit(new Point(100, 20)); var h3 = v.Hit(new Point(599, 349)); var h4 = v.Hit(new Point(600, 349));
    Console.WriteLine($"hit: client 200,50 -> {h} (expect inside 0.5,0.5); 100,20 -> {h2} (expect outside -99.5,-29.5); 599,349 -> {h3} (inside 399.5,299.5); 600,349 -> {h4} (outside)");
    if (!h.Inside || h.X != 0.5f || h2.Inside || h2.X != -99.5f || !h3.Inside || h4.Inside) Environment.Exit(1);
    v.Mode = SpriteSizeMode.Zoom; d = v.Dest; Console.WriteLine($"Zoom(fit): scale {v.ScaleX:0.###} dest {d} (expect 1.333, 133.3,0 533x400)");
    if (Math.Abs(v.ScaleX - 4 / 3f) > 1e-3 || Math.Abs(d.X - 400 / 3f) > 0.01 || d.Y != 0) Environment.Exit(1);
    v.Mode = SpriteSizeMode.Fill; Console.WriteLine($"Fill: scale {v.ScaleX} dest {v.Dest} (expect 2, 0,-100 800x600)");
    if (v.ScaleX != 2 || v.Dest.Y != -100) Environment.Exit(1);
    v.Mode = SpriteSizeMode.FitWidth; if (v.ScaleX != 2) Environment.Exit(1);
    v.Mode = SpriteSizeMode.FitHeight; if (Math.Abs(v.ScaleX - 4 / 3f) > 1e-3) Environment.Exit(1);
    v.Mode = SpriteSizeMode.StretchImage; if (v.ScaleX != 2 || Math.Abs(v.ScaleY - 4 / 3f) > 1e-3) Environment.Exit(1);
    Console.WriteLine("FitWidth / FitHeight / Stretch scales ok");
    // zoom anchored at a client point: the image point under it stays
    v.Mode = SpriteSizeMode.CenterImage; v.Pan = PointF.Empty;
    var before = v.ToImage(new PointF(300, 100)); v.ZoomAt(4, new PointF(300, 100)); var after = v.ToImage(new PointF(300, 100));
    Console.WriteLine($"ZoomAt(4) anchored 300,100: image point {before} -> {after} (must be equal), scale {v.ScaleX}");
    if (Math.Abs(before.X - after.X) > 1e-3 || Math.Abs(before.Y - after.Y) > 1e-3 || v.ScaleX != 4) Environment.Exit(1);
    // free pan with overscroll 0.9: at least 10 % of the image stays inside
    v.PanMode = SpritePanMode.Free; v.Overscroll = 0.9f;
    v.PanBy(-100000, 0); d = v.Dest; float keepX = d.Right;      // pushed to the left: the right edge is what remains inside
    v.PanBy(100000, 0); float keepX2 = v.ViewportSize.Width - v.Dest.Left;
    Console.WriteLine($"free pan overscroll 0.9 @4x (image 1600 wide): remains inside left {keepX} / right {keepX2} (expect 160 = 10 %)");
    if (Math.Abs(keepX - 160) > 0.5 || Math.Abs(keepX2 - 160) > 0.5) Environment.Exit(1);
    v.Overscroll = 0.5f; v.PanBy(100000, 0); Console.WriteLine($"overscroll 0.5: remains {v.ViewportSize.Width - v.Dest.Left} (expect 800)"); if (Math.Abs(v.ViewportSize.Width - v.Dest.Left - 800) > 0.5) Environment.Exit(1);
    // classic scroll: a small image cannot move, a big one only as far as it overhangs
    v.PanMode = SpritePanMode.Scroll; v.Zoom = 1; v.Pan = PointF.Empty; v.PanBy(50, 50); Console.WriteLine($"Scroll mode, small image: pan after +50,+50 = {v.Pan} (expect 0,0)"); if (v.Pan != PointF.Empty) Environment.Exit(1);
    v.Zoom = 4; v.PanBy(-100000, -100000); d = v.Dest; Console.WriteLine($"Scroll mode 4x: pushed to the end -> dest right/bottom {d.Right},{d.Bottom} (expect 800,400)"); if (d.Right != 800 || d.Bottom != 400) Environment.Exit(1);
    var hs = v.HorizontalScroll; Console.WriteLine($"scroll model: max {hs.Maximum} page {hs.Page} value {hs.Value} (expect 1600, 800, 800)"); if (hs.Maximum != 1600 || hs.Page != 800 || hs.Value != 800) Environment.Exit(1);
    v.SetScroll(true, 0); if (v.Dest.Left != 0) Environment.Exit(1); v.SetScroll(true, 400); if (v.Dest.Left != -400) Environment.Exit(1);
    Console.WriteLine("SetScroll 0 / 400 -> dest.Left 0 / -400 ok");
    // pan None
    v.PanMode = SpritePanMode.None; v.PanBy(100, 100); if (v.Pan != PointF.Empty) Environment.Exit(1);
    // inertia: a fling slides and stops by friction; hitting a limit stops that axis
    v.PanMode = SpritePanMode.Free; v.Overscroll = 0.9f; v.Zoom = 1; v.Pan = PointF.Empty; v.Friction = 0.55f;
    v.Fling(new PointF(1f, 0f)); int steps = 0; float travelled = 0; while (v.IsSliding && steps < 1000) { v.Step(16); steps++; } travelled = v.Pan.X;
    Console.WriteLine($"fling 1 px/ms, friction 0.55/100ms: slid {travelled:0} px in {steps} steps of 16 ms (expect ~120 px, stops on its own)");
    if (steps >= 1000 || travelled < 60 || travelled > 200) Environment.Exit(1);
    v.Pan = PointF.Empty; v.Fling(new PointF(4f, 0f)); steps = 0; while (v.IsSliding && steps < 1000) { v.Step(16); steps++; }
    Console.WriteLine($"fling 4 px/ms: stopped at pan.x {v.Pan.X:0} (limit {v.PanMax.X:0}) after {steps} steps"); if (v.Pan.X > v.PanMax.X + 0.01) Environment.Exit(1);
    // scroll bar control: Value range 0..Max-Page, thumb drag, track click pages, arrows step
    var sb = new SpriteScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 1000, PageSize = 250, Step = 10, Size = new Size(16, 300) };
    sb.Value = 5000; Console.WriteLine($"scroll bar: Value 5000 clamps to {sb.Value} (expect 750)"); if (sb.Value != 750) Environment.Exit(1);
    sb.Value = 0; sb.Tap(8, 290); double afterArrow = sb.Value; sb.Tap(8, 200); double afterTrack = sb.Value;
    Console.WriteLine($"arrow click -> {afterArrow} (expect 10), track click below the thumb -> {afterTrack} (expect 260)"); if (afterArrow != 10 || afterTrack != 260) Environment.Exit(1);
    sb.Value = 0; sb.Down(8, 30); sb.Move(8, 130); double dragged = sb.Value; sb.Up(8, 130);
    Console.WriteLine($"thumb drag 100 px on a 300 px bar (track ~266, thumb 25 %): -> {dragged:0} (expect ~375)"); if (Math.Abs(dragged - 375) > 40) Environment.Exit(1);
    var c = new Sprite(420, 320); c.ClearBuffer(unchecked((int)0xFF303840));
    sb.Value = 300; c.Draw(sb.RenderOnce(), 10, 10, SR2D.Op.Paint);
    var sbh = new SpriteScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 1000, PageSize = 400, Value = 200, Size = new Size(300, 16) }; c.Draw(sbh.RenderOnce(), 40, 10, SR2D.Op.Paint);
    var sbn = new SpriteScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 1000, PageSize = 100, Value = 600, Arrows = false, Size = new Size(300, 10) }; c.Draw(sbn.RenderOnce(), 40, 36, SR2D.Op.Paint);
    var sbi = new SpriteScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, PageSize = 100, Size = new Size(300, 16) }; c.Draw(sbi.RenderOnce(), 40, 56, SR2D.Op.Paint);
    var sbd = new SpriteScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 1000, PageSize = 250, Value = 750, Size = new Size(16, 200) }; sbd.Down(8, 100); c.Draw(sbd.RenderOnce(), 380, 10, SR2D.Op.Paint); sbd.Up(8, 100);
    Save(c, "/home/user/.cache/ctlrun/scroll.rgba");
    // scroll-bar decision must be a fixed point: applying it and asking again gives the same answer (this ping-pong froze the app)
    int flips = 0, cases = 0;
    foreach (var pm in new[] { SpritePanMode.Scroll, SpritePanMode.Free })
      foreach (var mode in new[] { SpriteSizeMode.CenterImage, SpriteSizeMode.Zoom, SpriteSizeMode.FitWidth, SpriteSizeMode.FitHeight, SpriteSizeMode.Fill })
        for (int cw = 300; cw <= 900; cw += 37) for (int ch = 200; ch <= 700; ch += 41) for (int zi = -3; zi <= 3; zi++)
        {
          var sv = new SpriteView { ImageSize = new Size(1600, 1200), Mode = mode, PanMode = pm, Zoom = Math.Pow(2, zi * 0.5) };
          var d1 = sv.DecideScrollBars(new Size(cw, ch), 14);
          sv.ViewportSize = SpriteView.ViewportFor(new Size(cw, ch), 14, d1.h, d1.v);
          var d2 = sv.DecideScrollBars(new Size(cw, ch), 14);
          cases++; if (d1 != d2) flips++;
          // the shown bars must match what is scrollable at the viewport they leave (or both are shown because it never settles)
          sv.ViewportSize = SpriteView.ViewportFor(new Size(cw, ch), 14, d1.h, d1.v);
          bool okH = d1.h == sv.HorizontalScroll.Scrollable, okV = d1.v == sv.VerticalScroll.Scrollable;
          if (!(okH && okV) && !(d1.h && d1.v)) { Console.WriteLine($"inconsistent bars {mode} {pm} {cw}x{ch} z{zi}"); Environment.Exit(1); }
        }
    Console.WriteLine($"scroll-bar decision fixed point: {cases} cases, {flips} flips");
    if (flips != 0) Environment.Exit(1);
    // inertia sensitivity: a 40 px/s release slides by default (threshold 30), a 20 px/s one does not; gain scales the distance
    var iv = new SpriteView { ImageSize = new Size(4000, 4000), ViewportSize = new Size(500, 500), Mode = SpriteSizeMode.CenterImage };
    iv.Fling(new PointF(0.02f, 0)); Chk(!iv.IsSliding, "20 px/s: no slide");
    iv.Fling(new PointF(0.04f, 0)); Chk(iv.IsSliding, "40 px/s: slides");
    float Dist(float gain) { var v = new SpriteView { ImageSize = new Size(4000, 4000), ViewportSize = new Size(500, 500), Mode = SpriteSizeMode.CenterImage, FlingGain = gain }; v.Fling(new PointF(1f, 0)); float x0 = v.Pan.X; for (int i = 0; i < 400 && v.IsSliding; i++) v.Step(15); return v.Pan.X - x0; }
    float d1g = Dist(1f), d2g = Dist(2f);
    Console.WriteLine($"slide distance at 1 px/ms: gain 1 -> {d1g:0} px, gain 2 -> {d2g:0} px");
    Chk(d2g > d1g * 1.8f && d2g < d1g * 2.2f, "gain doubles the distance");
    // the compose path's cost model: a viewport-clipped DrawScaled of a big image must cost about the same at 64x as at 1x
    var big = new Sprite(4096, 4096); big.ClearBuffer(unchecked((int)0xFF446688)); var scr = new Sprite(1000, 700);
    double T(float scale, SR2D.Filter f) { var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 0; while (sw.ElapsedMilliseconds < 150) { scr.SetLockRect(0, 1000, 0, 700); scr.DrawScaled(big, -100f, -100f, scale, scale, 0f, 0f, SR2D.Op.Paint, f); n++; } return sw.Elapsed.TotalMilliseconds / n; }
    double t1 = T(1f, SR2D.Filter.Nearest), t64 = T(64f, SR2D.Filter.Nearest), t4b = T(4f, SR2D.Filter.Bilinear);
    var mip = new Sprite(4096 / 8, 4096 / 8); var swm = System.Diagnostics.Stopwatch.StartNew(); mip.DrawScaled(big, 0, 0, mip.Width, mip.Height, SR2D.Op.Paint, SR2D.Filter.Area); double tm = swm.Elapsed.TotalMilliseconds;
    double t8 = T(1 / 8f, SR2D.Filter.Bilinear);
    Console.WriteLine($"compose cost, 4096x4096 image into a 1000x700 viewport: 1x nearest {t1:0.00} ms, 64x nearest {t64:0.00} ms, 4x bilinear {t4b:0.00} ms; 1/8 overview: box-average copy once {tm:0.0} ms, then from the copy {t8:0.00} ms per frame");
    if (t64 > t1 * 3 + 2) Environment.Exit(1);
  }
  static void Fail(string why) { Console.WriteLine("FAIL: " + why); Environment.Exit(1); }
  static void Fonts() {
    // the same furniture twice: pixel font (default, untouched) on the left, a SpriteFont via TextFontFamily on the right
    var c = new Sprite(1000, 420); c.ClearBuffer(unchecked((int)0xFF202428));
    void Put(Control k, int x, int y) { if (k is SpriteBox b) c.Draw(BoxRender(b), x, y, SR2D.Op.Paint); }
    Chk(SpriteControlBase.DefaultFont == null && SpriteControlBase.DefaultFontFamily == "", "controls default to the pixel font");
    // any installed UI sans works as the "real font" column; the families present differ between boxes,
    // so the pin asserts the RESOLUTION path, not one hard-coded family.
    var cands = new[] { "DejaVu Sans", "Segoe UI", "Arial", "Tahoma", "Verdana" };
    string fam = "";
    foreach (var f in cands) if (ControlFonts.Get(f) != null && ControlFonts.Get(f, bold: true) != null) { fam = f; break; }
    if (fam == "")
    {   // a box without any of the candidates (no readable system font store): the real-font pins below
        // would only test the pixel font against itself - skip the section instead of failing it forever
        Console.WriteLine($"  skip real-font pins: none of {string.Join(", ", cands)} resolves + bold");
        return;
    }
    Chk(fam != "", $"ControlFonts resolves a real UI family + bold (tried {string.Join(", ", cands)})");
    Chk(ControlFonts.Get("Nope Sans") == null, "unknown family -> null (pixel font stays)");
    int pixW = 0, pixH = 0;
    for (int col = 0; col < 2; col++) {
      string f = col == 0 ? "" : fam; int x = 10 + col * 500, y = 10;
      // the SAME text in both columns: the only difference under test is the font
      var lbl = new SpriteLabel { Text = "RealFont label: Hello Ж é", TextFontFamily = f }; Put(lbl, x, y); y += lbl.Height + 6;
      var head = new SpriteLabel { Text = "Heading", Style = LabelStyle.Heading, AutoSize = false, Size = new Size(460, 20), TextFontFamily = f }; Put(head, x, y); y += 26;
      var wrap = new SpriteLabel { AutoSize = false, WordWrap = true, Size = new Size(230, 64), TextAlign = ContentAlignment.TopLeft, TextFontFamily = f, Text = "a fairly long sentence that has to wrap onto several lines inside the box" }; Put(wrap, x, y);
      var big = new SpriteLabel { Text = "Scale 2 bold", TextScale = 2, Bold = true, AutoSize = false, Size = new Size(220, 40), TextAlign = ContentAlignment.MiddleRight, TextFontFamily = f }; Put(big, x + 240, y); y += 70;
      var btn = new SpriteButton { Text = "Button", Size = new Size(110, 28), TextFontFamily = f }; Put(btn, x, y);
      var tog = new SpriteToggle { Text = "Toggle", Size = new Size(140, 28), TextFontFamily = f }; Put(tog, x + 120, y);
      var rad = new SpriteRadio { Text = "Radio", Size = new Size(110, 28), TextFontFamily = f }; Put(rad, x + 270, y); y += 34;
      var tb = new SpriteTextBox { Size = new Size(230, 26), TextFontFamily = f }; tb.Type("Text box WWii"); Put(tb, x, y);
      var num = new SpriteNumeric { Size = new Size(120, 26), Value = 42, Unit = "ms", TextFontFamily = f }; Put(num, x + 240, y); y += 32;
      var combo = new SpriteCombo { Size = new Size(230, 26), TextFontFamily = f }; combo.Items.Add("first item"); combo.Items.Add("second"); combo.SelectedIndex = 0; Put(combo, x, y);
      var list = new SpriteListBox { Size = new Size(220, 70), TextFontFamily = f }; list.Items.Add("alpha"); list.Items.Add("beta"); list.Items.Add("gamma"); list.SelectedIndex = 1; Put(list, x + 240, y); y += 32;
      var knob = new SpriteKnob { Text = "Gain", Size = new Size(90, 110), Value = 30, TextFontFamily = f }; Put(knob, x, y);
      var sl = new SpriteSlider { Text = "Level", Size = new Size(130, 44), Value = 60, ShowValue = true, TextFontFamily = f }; Put(sl, x + 100, y);
      var tabs = new SpriteTabControl { Size = new Size(220, 60), TextFontFamily = f }; tabs.AddPage("One"); tabs.AddPage("Two"); Put(tabs, x + 240, y + 80);
      if (col == 1) {
        Chk(lbl.UsesSpriteFont && lbl.Width != pixW && lbl.Height >= 14 && lbl.Height <= 20, $"label auto-sizes with the real font: {fam} {lbl.Size}, pixel font was {(pixW, pixH)}");
        // proportional caret geometry: click between glyphs lands on the right index and Home/End still work
        tb.Key(Keys.End); int endIdx = tb.CaretIndex; tb.Tap(12, 13); int startIdx = tb.CaretIndex;
        Chk(endIdx == tb.Text.Length && startIdx <= 1, $"text box caret: End -> {endIdx}, click at x=12 -> {startIdx}");
        tb.Tap(200, 13); int lateIdx = tb.CaretIndex; Chk(lateIdx > 8, $"click far right -> index {lateIdx} (proportional widths)");
        lbl.TextFontFamily = ""; Chk(!lbl.UsesSpriteFont, "TextFontFamily = \"\" goes back to the pixel font");
      } else { pixW = lbl.Width; pixH = lbl.Height; Chk(!lbl.UsesSpriteFont && !tb.UsesSpriteFont, "column 0 draws with the pixel font"); }
    }
    // app-wide default + per-control object override
    SpriteControlBase.DefaultFontFamily = fam;
    var d1 = new SpriteLabel { Text = "default family" }; Chk(d1.UsesSpriteFont, "DefaultFontFamily applies to a control without its own font");
    var own = SpriteFont.Installed("DejaVu Serif"); var d2 = new SpriteLabel { Text = "own font object", TextFont = own }; Chk(own == null || d2.UsesSpriteFont, "TextFont object override");
    SpriteControlBase.DefaultFontFamily = "";
    Chk(!d1.UsesSpriteFont, "clearing DefaultFontFamily restores the pixel font");
    Save(c, "/home/user/.cache/ctlrun/fonts.rgba");
  }
  static void Furniture() {
    var c = new Sprite(1000, 600); c.ClearBuffer(unchecked((int)0xFF202428));
    void Put(Control k, int x, int y) { if (k is SpriteBox b) c.Draw(BoxRender(b), x, y, SR2D.Op.Paint); }
    // labels in all styles
    int y = 10;
    foreach (var st in new[]{LabelStyle.Plain, LabelStyle.Heading, LabelStyle.Muted, LabelStyle.Readout, LabelStyle.Badge}) {
      var l = new SpriteLabel { Text = st + " label", Style = st }; if (st == LabelStyle.Heading) { l.AutoSize = false; l.Size = new Size(220, 18); }
      Console.WriteLine($"label {st}: auto size {l.Size}");
      Put(l, 10, y); y += l.Height + 6;
    }
    var wrap = new SpriteLabel { AutoSize = false, WordWrap = true, Size = new Size(220, 60), TextAlign = ContentAlignment.TopLeft, Text = "a fairly long sentence that has to wrap onto several lines inside the box" };
    Put(wrap, 10, y); y += 66;
    var big = new SpriteLabel { Text = "Scale 2 right", TextScale = 2, AutoSize = false, Size = new Size(220, 30), TextAlign = ContentAlignment.MiddleRight };
    Put(big, 10, y); y += 36;
    var sep = new SpriteSeparator { Text = "or", Size = new Size(220, 14) }; Put(sep, 10, y); y += 20;
    // LEDs
    var led1 = new SpriteLed { Text = "green on", On = true, Size = new Size(220, 20) }; Put(led1, 10, y); y += 24;
    var led2 = new SpriteLed { Text = "square off", Shape = LedShape.Square, Size = new Size(220, 20) }; Put(led2, 10, y); y += 24;
    var led3 = new SpriteLed { Text = "bar clickable", Shape = LedShape.Bar, Clickable = true, LedColor = Color.FromArgb(0xFF,0x90,0x30), Size = new Size(220, 20) };
    int ledEvents = 0; led3.OnChanged += (_, _) => ledEvents++; led3.Tap(5, 10); led3.Key(Keys.Space); led3.Tap(5, 10);
    if (!led3.On || ledEvents != 3) Fail($"LED clickable: on {led3.On} events {ledEvents} (expect True, 3)");
    Put(led3, 10, y); y += 24;
    // text box behaviour
    var tb = new SpriteTextBox { Size = new Size(220, 24), Placeholder = "empty" };
    tb.Type("Hello World"); tb.Key(Keys.Home); tb.Key(Keys.Right | Keys.Control); tb.Type("big ");
    if (tb.Text != "Hello big World") Fail($"text box typing: '{tb.Text}'");
    tb.Key(Keys.Left | Keys.Shift); tb.Key(Keys.Left | Keys.Shift); tb.Type("BIG "); tb.Key(Keys.End); tb.Key(Keys.Back); tb.Key(Keys.Back);
    { // selection + Backspace / Delete / Cut must remove the selected text (bug of 2026-09-25: InsertText("") returned early)
      var sb = new SpriteTextBox { Size = new Size(220, 24), Text = "abcdef" }; sb.Key(Keys.Home); sb.Key(Keys.Right); sb.Key(Keys.Right | Keys.Shift); sb.Key(Keys.Right | Keys.Shift); sb.Key(Keys.Back);
      if (sb.Text != "adef" || sb.CaretIndex != 1) Fail($"select + Backspace: '{sb.Text}' caret {sb.CaretIndex} (expect 'adef', 1)");
      sb.Key(Keys.Right | Keys.Shift); sb.Key(Keys.Delete); if (sb.Text != "aef") Fail($"select + Delete: '{sb.Text}'");
      sb.SelectAll(); sb.Key(Keys.Back); if (sb.Text.Length != 0) Fail($"select all + Backspace: '{sb.Text}'");
      // Cyrillic / Greek / accented text in the pixel font: typed, measured, drawn (no crash, no boxes for the covered ranges) and in a real font
      var cy = new SpriteTextBox { Size = new Size(260, 24) }; cy.Type("Привет, мир! Ёжик ъыь Ґґ Єє Її Ўў αβγδ Ωμ café naïve Ærø"); cy.Key(Keys.Home); cy.Key(Keys.Right | Keys.Shift); cy.Key(Keys.Right | Keys.Shift); cy.Key(Keys.Back);
      if (!cy.Text.StartsWith("ивет", StringComparison.Ordinal)) Fail($"cyrillic typing: '{cy.Text}'");
      cy.Tap(60, 12); cy.DoubleClick(60, 12); Console.WriteLine($"cyrillic: double click selected '{cy.SelectedText}', caret {cy.CaretIndex}");
      Put(cy, 250, 470);
      var cyf = new SpriteTextBox { Size = new Size(260, 24), TextFontFamily = "DejaVu Sans" }; cyf.Type("Привет, мир! αβγ café"); cyf.Tap(80, 12); cyf.DoubleClick(80, 12); Put(cyf, 520, 470);
      var lbl = new SpriteLabel { Text = "Кириллица: Съешь же ещё этих мягких французских булок", AutoSize = true }; Put(lbl, 520, 500);
      var lbl2 = new SpriteLabel { Text = "Ελληνικά: αβγδεζηθικλμνξοπρστυφχψω ΑΒΓΔΘΛΞΠΣΦΨΩ", AutoSize = true }; Put(lbl2, 520, 515);
      var lbl3 = new SpriteLabel { Text = "Latin-1: àáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿ ÀÉÎÕÜ ß ¿¡ «»", AutoSize = true }; Put(lbl3, 520, 530);
      var lbl4 = new SpriteLabel { Text = "fallback: 日本語 中文 한국어 العربية ∑∞≠ ♥", AutoSize = true, TextScale = 2 }; Put(lbl4, 520, 545);
      var cvs = new Sprite(300, 40); cvs.ClearBuffer(unchecked((int)0xFF202428)); cvs.DrawText(150, 4, "Съешь булок ᾧ", unchecked((int)0xFFFFFFFF), unchecked((int)0xFF202428), TextAnchor.TopCenter, 2); c.Draw(cvs, 250, 500, SR2D.Op.Paint);
    }
    if (tb.Text != "Hello biBIG Wor") Fail($"text box selection replace / backspace: '{tb.Text}'");
    tb.Key(Keys.A | Keys.Control); tb.Key(Keys.C | Keys.Control); tb.Key(Keys.End); tb.Key(Keys.V | Keys.Control);
    if (tb.Text != "Hello biBIG WorHello biBIG Wor") Fail($"text box copy / paste: '{tb.Text}'");
    tb.Text = "commit me"; int commits = 0; tb.Committed += (_, _) => commits++;
    tb.Key(Keys.End); tb.Type("!"); tb.Key(Keys.Enter); tb.Type("?"); tb.Key(Keys.Escape);
    if (tb.Text != "commit me!" || commits != 1) Fail($"text box commit / escape: '{tb.Text}' commits {commits}");
    tb.MaxLength = 12; tb.Key(Keys.End); tb.Type("abcdef"); if (tb.Text != "commit me!ab") Fail($"max length: '{tb.Text}'");
    tb.DoubleClick(20, 12); Console.WriteLine($"text box double click selected '{tb.SelectedText}' (expect 'commit')"); if (tb.SelectedText != "commit") Fail("word select");
    Put(tb, 250, 10);
    var pw = new SpriteTextBox { Size = new Size(220, 24), Text = "secret", PasswordChar = '*' }; Put(pw, 250, 40);
    var ph = new SpriteTextBox { Size = new Size(220, 24), Placeholder = "placeholder text" }; Put(ph, 250, 70);
    var ro = new SpriteTextBox { Size = new Size(220, 24), Text = "read only", ReadOnly = true }; ro.Type("x"); if (ro.Text != "read only") Fail("read only"); Put(ro, 250, 100);
    // numeric
    var num = new SpriteNumeric { Size = new Size(120, 24), Minimum = -10, Maximum = 10, Value = 3, Unit = "px" };
    int nch = 0; num.ValueChanged += (_, _) => nch++;
    num.Key(Keys.Up); num.Key(Keys.Up); num.Wheel(10, 10, -120); num.Tap(112, 5); num.Tap(112, 19); num.Tap(112, 19);
    if (num.Value != 3 || nch != 6) Fail($"numeric steps: {num.Value} events {nch} (expect 3, 6)");
    num.Key(Keys.A | Keys.Control); num.Type("7.6"); num.Key(Keys.Enter); if (num.Value != 8) Fail($"numeric typed 7.6 -> {num.Value} (expect 8, Decimals 0)");
    num.Key(Keys.A | Keys.Control); num.Type("99"); num.Key(Keys.Enter); if (num.Value != 10) Fail($"numeric clamp: {num.Value}");
    num.Key(Keys.A | Keys.Control); num.Type("abc"); num.Key(Keys.Enter); if (num.Value != 10 || num.Text != "10") Fail($"numeric garbage: {num.Value} '{num.Text}'");
    var num2 = new SpriteNumeric { Size = new Size(140, 28), Minimum = 0, Maximum = 1, Step = 0.05, Decimals = 2, Value = 0.5 };
    Put(num, 250, 130); Put(num2, 380, 130);
    // spinner drag: press on the up button (+1 at once), drag 8 px further up with PixelsPerStep 4 -> +2 more; wrap at the screen top teleports the pointer
    var nd = new SpriteNumeric { Size = new Size(120, 24), Minimum = -100, Maximum = 100, Value = 0, PixelsPerStep = 4 }; nd.CreateHandle();
    nd.Down(112, 5); double afterPress = nd.Value; nd.Move(112, -3); double afterDrag = nd.Value; nd.Up(112, -3);
    Console.WriteLine($"numeric spinner drag: press up -> {afterPress} (expect 1), drag 8 px up -> {afterDrag} (expect 3), cursor default again = {nd.Cursor == Cursors.Default}");
    if (afterPress != 1 || afterDrag != 3 || nd.Cursor != Cursors.Default) Fail("numeric spinner drag");
    Cursor.Position = new Point(-1, -1); nd.Location = new Point(0, 0); nd.Down(112, 19); nd.Move(112, 19 + 40); nd.Move(112, 1085); var np = Cursor.Position; nd.Up(112, 1085);
    Console.WriteLine($"numeric wrap-mouse: dragging the down button off the screen bottom (1080) moved the cursor to y={np.Y} (expect 2, the top edge), value {nd.Value}");
    if (np.Y != 2) Fail("numeric wrap mouse");
    var ndn = new SpriteNumeric { Size = new Size(120, 24), Value = 5, WrapMouse = false }; ndn.CreateHandle(); Cursor.Position = new Point(-1, -1);
    ndn.Down(112, 19); ndn.Move(112, 80); ndn.Up(112, 80); if (Cursor.Position != new Point(-1, -1)) Fail("numeric WrapMouse=false touched the cursor");
    var nt = new SpriteNumeric { Size = new Size(120, 24), Value = 5, DragToChange = false }; nt.CreateHandle();
    nt.Down(112, 5); nt.Move(112, -20); nt.Up(112, -20); if (nt.Value != 6) Fail($"numeric DragToChange=false: {nt.Value} (expect 6, the click only)");
    // spinner below the field: minus left, plus right, drag horizontally (right = larger); the I-beam only over the field
    var nb = new SpriteNumeric { Size = new Size(90, 44), Minimum = 0, Maximum = 100, Value = 50, Spinner = SpinnerPlacement.Below, Unit = "%" }; nb.CreateHandle();
    nb.Tap(20, 36); nb.Tap(70, 36); nb.Tap(70, 36); double nbv = nb.Value;
    nb.Down(70, 36); nb.Move(70 + 12, 36); double nbd = nb.Value; nb.Up(82, 36);
    nb.Move(30, 10); var curField = nb.Cursor; nb.Move(30, 36); var curBtn = nb.Cursor;
    Console.WriteLine($"numeric Spinner=Below: - + + -> {nbv} (expect 51), press + and drag 12 px right -> {nbd} (expect 55), cursor over field IBeam = {curField == Cursors.IBeam}, over buttons default = {curBtn == Cursors.Default}");
    if (nbv != 51 || nbd != 55 || curField != Cursors.IBeam || curBtn != Cursors.Default) Fail("numeric Spinner=Below");
    var nn = new SpriteNumeric { Size = new Size(90, 24), Value = 7, Spinner = SpinnerPlacement.None }; nn.CreateHandle(); nn.Tap(85, 5); if (nn.Value != 7) Fail("Spinner=None still has buttons");
    Put(nb, 250, 500); Put(nn, 350, 500);
    // combo
    var combo = new SpriteCombo { Size = new Size(220, 24), Placeholder = "pick one" }; int cch = 0; combo.SelectedIndexChanged += (_, _) => cch++;
    combo.SetItems(new[]{"Row", "Column", "Diagonal", "Ring"}, 0);
    combo.Key(Keys.Down); combo.Key(Keys.Down); combo.Wheel(10, 10, 120); combo.Key(Keys.End); combo.Key(Keys.D0 + ('R' - '0'));
    if (combo.SelectedIndex != 0 || combo.SelectedItem != "Row") Fail($"combo keys: {combo.SelectedIndex} {combo.SelectedItem}");
    Console.WriteLine($"combo: {cch} SelectedIndexChanged events (expect 6: SetItems + 5 keys)"); if (cch != 6) Fail("combo events");
    Put(combo, 250, 170);
    var combo2 = new SpriteCombo { Size = new Size(220, 24), Placeholder = "nothing selected" }; Put(combo2, 250, 200);
    // drop list: the open menu owns the mouse (capture) - a click after hovering activates on the FIRST click
    int sel0 = combo.SelectedIndex;
    combo.Open();
    var menu = (SpriteMenu?)typeof(SpriteCombo).GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(combo);
    var mp = menu!.Panel!;
    Chk(mp.Capture && combo.IsOpen, "the open drop list holds the mouse capture");
    mp.Move(60, 50); mp.Down(60, 50); mp.Up(60, 50);                        // hover row 2 ("Diagonal"), then click it
    Chk(combo.SelectedIndex == 2 && !combo.IsOpen && !mp.Capture, $"first click after hover selects and closes (sel {combo.SelectedIndex})");
    combo.Open(); Chk(mp.Capture, "reopening recaptures the mouse");
    mp.Down(60, 12); mp.Up(60, 12);                                         // row 0 ("Row")
    Chk(combo.SelectedIndex == 0 && !combo.IsOpen && cch == 8, $"second open/close cycle works (events {cch}, expect 8)");
    combo.Open(); Chk(combo.IsOpen, "reopen for the outside click");
    mp.Down(400, 50);                                                       // a click outside the rows closes the menu
    Chk(!combo.IsOpen && !mp.Capture, "a click outside the drop list closes it");
    // the filter must swallow a click on the OPENER (one click closes - the field must not toggle open again on the
    // same click) but still pass clicks elsewhere through (a click on another control works and closes the list)
    combo.Open(); Chk(combo.IsOpen, "reopen for the opener click");
    combo.Location = new Point(250, 170);
    Cursor.Position = new Point(260, 180);                                  // on the combo field (fake client == screen)
    var filter = Application.Filters[^1];
    var msg = new Message { Msg = 0x0201 };                                 // WM_LBUTTONDOWN
    bool swallowed = filter.PreFilterMessage(ref msg);
    Chk(swallowed && !combo.IsOpen, "a click on the opener closes the list and is swallowed (no reopen)");
    combo.Open(); Chk(combo.IsOpen, "reopen for the pass-through click");
    Cursor.Position = new Point(5, 5);                                      // over the canvas, not the opener
    msg.Msg = 0x0201;
    bool pass = !filter.PreFilterMessage(ref msg);
    Chk(pass && !combo.IsOpen, "a click elsewhere closes the list and still passes through");
    // fractal layout: the exact partitions the tests promise + the partition property (cover exactly once)
    var r1 = FractalLayout.Regions(400, 400, 1);
    Chk(r1.Count == 1 && r1[0] == new Rectangle(0, 0, 400, 400), "fractal 1 copy = the whole viewport");
    var r2 = FractalLayout.Regions(400, 400, 2);
    Chk(r2.Count == 2 && r2[0] == new Rectangle(0, 0, 200, 400) && r2[1] == new Rectangle(200, 0, 200, 400), "fractal 2 = left | right");
    var r3 = FractalLayout.Regions(400, 400, 3);
    Chk(r3.Count == 3 && r3[0] == new Rectangle(0, 0, 200, 400) && r3[1] == new Rectangle(200, 0, 200, 200) && r3[2] == new Rectangle(200, 200, 200, 200), "fractal 3 = left | right-top over right-bottom");
    var r9 = FractalLayout.Regions(813, 720, 9);
    var cover = new byte[813, 720]; bool once = r9.Count == 9;
    foreach (var rg in r9)
        for (int yy = rg.Y; yy < rg.Bottom; yy++) for (int xx = rg.X; xx < rg.Right; xx++) { if (++cover[xx, yy] > 1) once = false; }
    bool full = true; for (int xx = 0; xx < 813 && full; xx++) for (int yy = 0; yy < 720; yy++) if (cover[xx, yy] != 1) { full = false; break; }
    // ---- EditHistory: rect snapshots, undo/redo, cap, voxel boxes
    var es = new Sprite(64, 48);
    for (int i = 0; i < es.Pixels.Length; i++) es.Pixels[i] = unchecked((int)0xFF000000 | (i * 7 & 0xFFFFFF));   // a known gradient
    var hist = new EditHistory();
    var rec = hist.Record(es, new Rectangle(10, 10, 20, 16));
    Chk(rec == new Rectangle(10, 10, 20, 16) && hist.CanUndo, "Record returns the clipped rect and arms undo");
    Chk(hist.Record(es, new Rectangle(100, 100, 5, 5)) == Rectangle.Empty && !hist.CanRedo, "a fully outside rect records nothing");
    es.FillRect(10, 10, 20, 16, unchecked((int)0xFF00FF00));
    Chk(hist.Undo(es) && es.GetPixel(15, 15) == (unchecked((int)0xFF000000 | ((15 * 64 + 15) * 7 & 0xFFFFFF))), "undo restores the recorded pixels");
    Chk(hist.Redo(es) && es.GetPixel(15, 15) == unchecked((int)0xFF00FF00), "redo re-applies the newer state");
    int steps = 0; while (hist.Undo(es)) steps++;
    Chk(steps == 1 && !hist.CanRedo == false && hist.CanRedo, "undo drains to the beginning");
    bool same = true; var px = es.Pixels;
    for (int i = 0; i < px.Length && same; i++) if (px[i] != (unchecked((int)0xFF000000 | (i * 7 & 0xFFFFFF)))) same = false;
    Chk(same, "after undoing everything the sprite matches the original pixel for pixel");
    while (hist.Redo(es)) ;
    // a new record discards the redo tail
    hist.Record(es, new Rectangle(0, 0, 8, 8));
    es.FillRect(0, 0, 8, 8, unchecked((int)0xFFFF0000));
    hist.Undo(es);
    hist.Record(es, new Rectangle(32, 32, 4, 4));
    Chk(!hist.CanRedo, "a new record discards the redo tail");
    hist.Clear();
    // memory cap: entries drop oldest-first, bytes stay within the cap
    var histCap = new EditHistory(8192);
    for (int i = 0; i < 20; i++) histCap.Record(es, new Rectangle(0, i * 2, 16, 16));   // 1 KB each
    Chk(histCap.MemoryBytes <= 8192 && histCap.UndoCount < 20 && histCap.UndoCount > 0, $"the cap drops the oldest entries ({histCap.UndoCount} left, {histCap.MemoryBytes} bytes)");
    // dimension mismatch is refused, not corrupted
    var other = new Sprite(32, 32);
    Chk(!histCap.Undo(other), "undo into a differently sized sprite is refused");
    // voxel boxes
    var g = new VoxelGrid(4, 3, 2);
    for (int vz = 0; vz < 2; vz++) for (int vy = 0; vy < 3; vy++) for (int vx = 0; vx < 4; vx++) g[vx, vy, vz] = new Voxel { Argb = 0xFF000000u | (uint)(vx * 1000 + vy * 10 + vz), Material = (byte)(vx + vy) };
    var vh = new EditHistory();
    Chk(vh.Record(g, 1, 1, 0, 3, 3, 2), "voxel box recorded");
    g[1, 1, 0] = new Voxel { Argb = 0xFF112233u, Material = 9 };
    Chk(vh.Undo(g) && g[1, 1, 0].Argb == (0xFF000000u | 1010u) && g[2, 2, 1].Material == 4, "voxel undo restores the box");
    Chk(vh.Redo(g) && g[1, 1, 0].Argb == 0xFF112233u && g[1, 1, 0].Material == 9, "voxel redo re-applies the edit");
    // ---- SpriteBox partial render: ClipRectangle flows to the handler, outside pixels stay
    var db = new SpriteBox { Size = new Size(100, 80) };
    var clips = new List<Rectangle>();
    db.Render += (_, e) =>
    {
        clips.Add(e.ClipRectangle);
        var reg = e.ClipRectangle.IsEmpty ? new Rectangle(0, 0, e.Width, e.Height) : e.ClipRectangle;
        for (int y = reg.Y; y < reg.Bottom; y++) for (int x = reg.X; x < reg.Right; x++) e.Surface.Pixels[y * e.Width + x] = unchecked((int)0xFF101010);
    };
    db.RenderOnce();
    Chk(clips[^1] == Rectangle.Empty, "a full render reports an empty clip (everything)");
    var snap = db.Surface.Pixels.ToArray();
    var part = new Rectangle(20, 15, 30, 25);
    db.Redraw(part); db.Redraw(new Rectangle(80, 60, 40, 40)); db.Redraw(new Rectangle(90, 70, 100, 100));   // unions + clamps to 100x80
    var expect = Rectangle.Union(part, new Rectangle(80, 60, 20, 20));                                        // 80,60,40,40 clamped to 100x80; 90,70,.. covered by the full-pending rule? no: partial pending -> unions
    db.RenderOnce();
    Chk(clips[^1] == Rectangle.Union(expect, new Rectangle(90, 70, 10, 10)), $"pending partials union into one clip ({clips[^1]})");
    bool untouched = true;
    for (int qy = 0; qy < 80 && untouched; qy++) for (int qx = 0; qx < 100; qx++)
        if (!clips[^1].Contains(qx, qy) && db.Surface.Pixels[qy * 100 + qx] != snap[qy * 100 + qx]) { untouched = false; break; }
    Chk(untouched, "pixels outside the clip keep the previous frame");
    db.Redraw(); db.RenderOnce();
    Chk(clips[^1] == Rectangle.Empty, "a full Redraw supersedes partials (clip empty again)");
    // ---- Tween: deterministic stepping (Clock override), completion, cancel, invalidation
    {
        double now = 0; Tween.Clock = () => now;
        var twb = new SpriteBox { Size = new Size(40, 40) }; double val = 0; int renders = 0; bool completed = false;
        tb.Render += (_, e) => { renders++; val = double.Parse(val.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture); };   // the render itself does not matter
        var tw = Tween.To(twb, () => val, v => { }, 100, 1000, Tween.Linear);
        tw.Completed += () => completed = true;
        Chk(Tween.LiveCount == 1, "the tween is live right after To");
        now = 500; Tween.Pump(); Tween.Pump();
        now = 1000; Tween.Pump();
        Chk(completed && Tween.LiveCount == 0, "the tween completes on time and removes itself");
        double v2 = 5;
        now = 0;
        var tw2 = Tween.To(tb, () => v2, x => v2 = x, 15, 100, Tween.Linear);
        now = 50; Tween.Pump();
        Chk(Math.Abs(v2 - 10) < 1e-9, $"linear half-time reaches the midpoint ({v2})");
        now = 100; Tween.Pump();
        Chk(Math.Abs(v2 - 15) < 1e-9, "the tween lands exactly on the target");
        double v3 = 5;
        now = 0; Tween.To(tb, () => v3, x => v3 = x, 50, 100);
        now = 10; Tween.Pump();
        Tween.CancelAll(tb);
        double frozen = v3; now = 90; Tween.Pump();
        Chk(v3 == frozen && Tween.LiveCount == 0, "CancelAll freezes the value and clears the queue");
        renders++; Chk(renders > 0, "the target was invalidated");
        Tween.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
    // ---- weighted-RGB wand: white over blue is "closer" perceptually than over black (Chebyshev says the opposite-ish)
    {
        var ws = new Sprite(3, 1);
        ws.Pixels[0] = unchecked((int)0xFFFFFFFF); ws.Pixels[1] = unchecked((int)0xFF0000FF); ws.Pixels[2] = unchecked((int)0xFF000000);
        var sel = new Selection(3, 1);
        int rgbN = sel.ByColor(ws, unchecked((int)0xFFFFFFFF), 245);
        int perN = sel.ByColor(ws, unchecked((int)0xFFFFFFFF), 245, Metric: SelectMetric.Perceptual);
        Chk(rgbN == 1 && perN == 2, $"metric difference is real (chebyshev {rgbN}, weighted {perN}, tol 245: 255 vs ~240.5)");
        var sel2 = new Selection(3, 1);
        int gw = sel2.Wand(ws, 2, 0, 10, SelectMode.Replace, Contiguous: true, Metric: SelectMetric.Perceptual);
        Chk(gw == 1 && sel2.Bounds == new Rectangle(2, 0, 1, 1), "weighted wand from black picks only black (contiguous)");
        // quick-mask roundtrip
        var qm = new Selection(4, 4); qm.SelectAll();
        var qs = new Sprite(4, 4);
        qm.ToSprite(qs);
        Chk((qs.Pixels[5] >>> 24) == 255, "ToSprite exports coverage as alpha");
        qs.Pixels[0] = qs.Pixels[1] = qs.Pixels[2] = qs.Pixels[3] = unchecked((int)0x00000000);   // erase the top row
        qm.FromSprite(qs);
        Chk(qm.Bounds == new Rectangle(0, 1, 4, 3) && qm.Count == 12, "FromSprite re-imports the painted mask (top row deselected)");
    }
    // ---- pattern fill: tiling phase, origin shift, outside-the-path untouched
    {
        var dst = new Sprite(8, 8); dst.ClearBuffer(unchecked((int)0xFFFF00FF));
        var pat = new Sprite(2, 2);
        pat.Pixels[0] = unchecked((int)0xFFFF0000); pat.Pixels[1] = unchecked((int)0xFF00FF00);
        pat.Pixels[2] = unchecked((int)0xFF0000FF); pat.Pixels[3] = unchecked((int)0xFFFFFFFF);
        Span<System.Drawing.PointF> sq = stackalloc System.Drawing.PointF[4];
        sq[0] = new System.Drawing.PointF(1, 1); sq[1] = new System.Drawing.PointF(7, 1); sq[2] = new System.Drawing.PointF(7, 7); sq[3] = new System.Drawing.PointF(1, 7);
        dst.FillPattern(sq, pat);
        Chk(dst.Pixels[0] == unchecked((int)0xFFFF00FF), "everything outside the path keeps the backdrop");
        Chk(dst.Pixels[2 * 8 + 2] == unchecked((int)0xFFFF0000), $"tile phase (2,2) -> pattern (0,0) red (got 0x{dst.Pixels[2 * 8 + 2]:X8})");
        Chk(dst.Pixels[2 * 8 + 3] == unchecked((int)0xFF00FF00), "tile wraps in x (3,2) -> (1,0) green");
        Chk(dst.Pixels[3 * 8 + 2] == unchecked((int)0xFF0000FF), "tile wraps in y (2,3) -> (0,1) blue");
        Chk(dst.Pixels[3 * 8 + 3] == unchecked((int)0xFFFFFFFF), "(3,3) -> (1,1) white");
        Chk(((dst.Pixels[1 * 8 + 1] >>> 24) & 255) < 255, "the AA edge keeps partial coverage");
        dst.ClearBuffer(unchecked((int)0xFFFF00FF));
        dst.FillPattern(sq, pat, OriginX: 1, OriginY: 1);
        Chk(dst.Pixels[2 * 8 + 2] == unchecked((int)0xFFFFFFFF), "the origin shifts the phase (2,2) -> (1,1) white");
        // ---- Crossfade alias behaves like Blend
        var rdst = new Sprite(2, 2); rdst.ClearBuffer(unchecked((int)0xFFFF0000));
        var bsrc = new Sprite(2, 2); bsrc.ClearBuffer(unchecked((int)0xFF0000FF));
        rdst.Crossfade(bsrc, 0, 0, 128);
        int mid = rdst.Pixels[0];
        Chk(((mid >>> 16) & 255) is > 100 and < 156 && (mid & 255) is > 100 and < 156, $"crossfade 128 mixes the halves (0x{mid:X8})");
    }
    bool balanced = true; long prevMax = long.MaxValue;                        // the largest area must shrink (or stay) with every extra copy
    for (int n = 1; n <= 24; n++)
    {
        long mx = 0; foreach (var rg in FractalLayout.Regions(813, 720, n)) mx = Math.Max(mx, (long)rg.Width * rg.Height);
        if (mx > prevMax) balanced = false; prevMax = mx;
    }
    Chk(once && full && balanced, "fractal 9: covers 813x720 exactly once, the largest region never grows");
    // reference placements (the user's screenshots): every new copy divides the NEWEST region - 2 = left | right,
    // 3 = left | right-top over right-bottom, 4 = the right-bottom splits into two side by side (spiralling chain)
    var r4 = FractalLayout.Regions(832, 720, 4);
    Chk(r4.Count == 4 && r4[0] == new Rectangle(0, 0, 416, 720) && r4[1] == new Rectangle(416, 0, 416, 360)
        && r4[2] == new Rectangle(416, 360, 208, 360) && r4[3] == new Rectangle(624, 360, 208, 360),
        "fractal 4 on 832x720 = tall left | tall right-top | two bottom halves side by side");
    var r2b = FractalLayout.Regions(832, 720, 2);
    Chk(r2b[0] == new Rectangle(0, 0, 416, 720) && r2b[1] == new Rectangle(416, 0, 416, 720), "fractal 2 on 832x720 = two tall halves");
    // the copies cap: splits stop while both halves are still >= 16 px (MaxRegions), and Regions never exceeds it
    int cap = FractalLayout.MaxRegions(832, 720);
    var rAll = FractalLayout.Regions(832, 720, 999);
    bool capped = rAll.Count == cap, allBig = true;
    foreach (var rg in rAll) if (rg.Width < FractalLayout.MinHalf || rg.Height < FractalLayout.MinHalf) allBig = false;
    Chk(cap == 11 && capped && allBig, $"copies capped at the last divisible split (832x720 -> {cap}, got {rAll.Count}, all >= {FractalLayout.MinHalf}px)");
    Chk(FractalLayout.MaxRegions(30, 20) == 1 && FractalLayout.Regions(30, 20, 9).Count == 1, "a viewport below 2x MinHalf cannot be split at all (cap 1)");
    // list box
    var list = new SpriteListBox { Size = new Size(220, 150), CheckBoxes = true, SelectionMode = ListSelection.Multi, ShowLines = true };
    list.SetItems(new[]{"alpha","bravo","charlie","delta","echo","foxtrot","golf","hotel","india","juliet","kilo","lima"});
    int rowH = 0; { var f = typeof(SpriteListBox).GetProperty("RowH", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!; rowH = (int)f.GetValue(list)!; }
    list.Tap(100, 1 + rowH * 1 + rowH / 2);                                 // click bravo
    Control.ModifierKeys = Keys.Shift; list.Tap(100, 1 + rowH * 3 + rowH / 2); Control.ModifierKeys = Keys.None;   // shift-click delta -> bravo..delta
    if (string.Join(",", list.SelectedIndices) != "1,2,3") Fail($"list shift range: {string.Join(",", list.SelectedIndices)}");
    Control.ModifierKeys = Keys.Control; list.Tap(100, 1 + rowH * 2 + rowH / 2); Control.ModifierKeys = Keys.None;  // ctrl-click charlie off
    if (string.Join(",", list.SelectedIndices) != "1,3") Fail($"list ctrl toggle: {string.Join(",", list.SelectedIndices)}");
    list.Tap(12, 1 + rowH * 0 + rowH / 2); list.Key(Keys.End); list.Key(Keys.Space);      // check alpha by click, lima by keyboard
    if (string.Join(",", list.CheckedIndices) != "0,11") Fail($"list checks: {string.Join(",", list.CheckedIndices)}");
    if (string.Join(",", list.SelectedIndices) != "11") Fail($"list End selects last: {string.Join(",", list.SelectedIndices)}");
    int act = -1; list.ItemActivated += (_, i) => act = i; list.Key(Keys.Enter); if (act != 11) Fail("list Enter activates");
    list.Key(Keys.Home); list.Wheel(50, 50, -120); list.Key(Keys.A | Keys.Control); if (list.SelectedIndices.Length != 12) Fail("list Ctrl+A");
    list.SelectedIndex = 5; if (string.Join(",", list.SelectedIndices) != "5") Fail("list SelectedIndex");
    Put(list, 250, 234);
    var single = new SpriteListBox { Size = new Size(220, 90) }; single.SetItems(new[]{"one","two","three"}); single.SelectedIndex = 1; single.Enter(); single.Move(30, 5); Put(single, 250, 394);
    // containers: panel styles, group box (check disables children), tabs
    int x = 500;
    foreach (var st in new[]{PanelStyle.Sunken, PanelStyle.Raised, PanelStyle.Outline, PanelStyle.Flat}) {
      var p = new SpritePanel { Style = st, Size = new Size(110, 60) };
      var inner = new SpriteLabel { Text = st.ToString() }; p.Controls.Add(inner);
      if (st != PanelStyle.Flat && st != PanelStyle.Outline && inner.BackColor != p.FaceColor) Fail($"panel {st}: child did not adopt the face colour ({inner.BackColor} vs {p.FaceColor})");
      Put(p, x, 10); c.Draw(inner.RenderOnce(), x + 10, 10 + 10, SR2D.Op.Paint); x += 118;
    }
    var grp = new SpriteGroupBox { Text = "Group with check", ShowCheck = true, Size = new Size(230, 120), Animated = false };
    var k = new SpriteButton { Text = "child", Size = new Size(90, 30) }; grp.Controls.Add(k);
    int gch = 0; grp.CheckedChanged += (_, _) => gch++;
    grp.Move(20, 8); grp.Tap(20, 8); if (grp.Checked || k.Enabled || gch != 1) Fail($"group box uncheck: checked {grp.Checked} child enabled {k.Enabled} events {gch}");
    grp.Key(Keys.Space); if (!grp.Checked || !k.Enabled) Fail("group box re-check");
    Put(grp, 500, 80); c.Draw(k.RenderOnce(), 500 + grp.DisplayRectangle.X, 80 + grp.DisplayRectangle.Y, SR2D.Op.Paint);
    var grp2 = new SpriteGroupBox { Text = "Centred caption", CaptionAlign = HorizontalAlignment.Center, Size = new Size(230, 120) }; Put(grp2, 740, 80);
    var grpOff = new SpriteGroupBox { Text = "Unchecked group", ShowCheck = true, Checked = false, Size = new Size(230, 80) }; Put(grpOff, 500, 210);
    var tabs = new SpriteTabControl { Size = new Size(470, 150) };
    var pa = tabs.AddPage("Caption"); tabs.AddPage("Layout"); tabs.AddPage("About");
    if (tabs.SelectedIndex != 0 || !pa.Visible || tabs.TabPages.Count != 3) Fail("tabs initial");
    int tch = 0; tabs.SelectedIndexChanged += (_, _) => tch++;
    tabs.Key(Keys.Right); tabs.Wheel(10, 5, -120); tabs.Key(Keys.Left); tabs.Tap(80, 8);
    Console.WriteLine($"tabs: selected {tabs.SelectedIndex} after Right, wheel down, Left, click on the 2nd tab (expect 1), {tch} events (expect 3: the click hits the already selected tab)"); if (tabs.SelectedIndex != 1 || tch != 3) Fail("tabs navigation");
    if (tabs.TabPages[1].Bounds != tabs.DisplayRectangle || tabs.TabPages[0].Visible) Fail("tab page bounds / visibility");
    Put(tabs, 500, 300); c.Draw(tabs.TabPages[1].RenderOnce(), 500 + tabs.DisplayRectangle.X, 300 + tabs.DisplayRectangle.Y, SR2D.Op.Paint);
    tabs.Side = TabSide.Bottom; tabs.Size = new Size(470, 90); Put(tabs, 500, 460);
    // text scale: never guessed from the control size any more - the pixel font stays at scale 1 unless TextScale is set
    // (the old "1 per 80 px" heuristic re-measured at paint time: the tall test list and the tab captions ballooned, and
    // the tab strip grew over its first page without a re-layout)
    var mi = typeof(SpriteControlBase).GetMethod("AutoTextScale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    int scTall = (int)mi.Invoke(new SpriteListBox { Size = new Size(340, 700) }, new object[] { 700 })!;
    int scSmall = (int)mi.Invoke(new SpriteListBox { Size = new Size(120, 30) }, new object[] { 40 })!;
    if (scTall != 1 || scSmall != 1) Fail($"AutoTextScale still guesses from the size: 700 px -> {scTall}, 30 px -> {scSmall}");
    var scaled = new SpriteListBox { Size = new Size(340, 700), TextScale = 3 };
    if ((int)mi.Invoke(scaled, new object[] { 700 })! != 3) Fail("TextScale ignored by AutoTextScale");
    // the tab strip must keep its geometry when only the control is resized (strip height depends on TextScale, not on ClientSize)
    var tabScale = new SpriteTabControl { Size = new Size(320, 100) }; tabScale.AddPage("Test"); tabScale.AddPage("Setup"); tabScale.PerformLayout();
    int topSmall = tabScale.DisplayRectangle.Top; tabScale.Size = new Size(320, 900); tabScale.PerformLayout();
    if (tabScale.DisplayRectangle.Top != topSmall) Fail($"tab strip height follows the control size ({topSmall} -> {tabScale.DisplayRectangle.Top}) without a TextScale change");
    // WrapText (via the label) and the ambient BackColor rule
    var explicitBack = new SpriteLabel { Text = "explicit", BackColor = Color.Red }; var pnl = new SpritePanel(); pnl.Controls.Add(explicitBack);
    if (explicitBack.BackColor != Color.Red) Fail("explicit BackColor must survive a container");
    Save(c, "/home/user/.cache/ctlrun/furniture.rgba");
    Console.WriteLine("furniture: all checks passed");
  }
  static Sprite BoxRender(SpriteBox b) => b.RenderOnce();
  static void Save(Sprite s, string name){ var px = s.Pixels; var buf = new byte[px.Length*4]; for (int i=0;i<px.Length;i++){int c=px[i]; buf[i*4]=(byte)(c>>16); buf[i*4+1]=(byte)(c>>8); buf[i*4+2]=(byte)c; buf[i*4+3]=255;} File.WriteAllBytes(name, buf); Console.WriteLine($"wrote {name} {s.Width}x{s.Height}"); }
  static void TextViews() {
    var c = new Sprite(1000, 560); c.ClearBuffer(unchecked((int)0xFF202428));
    void Put(Control k, int x, int y) { if (k is SpriteBox b) c.Draw(BoxRender(b), x, y, SR2D.Op.Paint); }
    // ---- SpriteTextView: lines, append, MaxLines, wrap, selection, copy, keyboard scrolling
    var tv = new SpriteTextView { Size = new Size(300, 120) };
    tv.SetLines(new[]{"first line", "second line", "third"}, null);
    Chk(tv.LineCount == 3 && tv.Text == string.Join(Environment.NewLine, "first line", "second line", "third"), "SetLines / Text");
    tv.AppendLine("fourth"); Chk(tv.LineCount == 4 && tv.Text.EndsWith("fourth", StringComparison.Ordinal), "AppendLine");
    tv.Text = "a\nb\r\nc"; Chk(tv.LineCount == 3, "Text setter splits on \\n and \\r\\n");
    tv.MaxLines = 5; for (int i = 0; i < 20; i++) tv.AppendLine("row " + i);
    Chk(tv.LineCount == 5 && tv.Text.StartsWith("row 15", StringComparison.Ordinal), $"MaxLines trims the oldest lines ({tv.LineCount})");
    tv.MaxLines = 0; tv.SetLines(new[]{"alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega", "short"}, null);
    tv.WordWrap = true; tv.Size = new Size(200, 90);
    int rows = (int)typeof(SpriteTextView).GetProperty("RowCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
    Chk(rows > 3, $"WordWrap splits the long line into rows ({rows})");
    tv.WordWrap = false;
    rows = (int)typeof(SpriteTextView).GetProperty("RowCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
    Chk(rows == 2, $"no wrap: one row per line ({rows})");
    // selection by mouse: drag from the start of line 0 into line 1, then copy
    tv.Size = new Size(300, 120); tv.SetLines(new[]{"hello world", "second row", "third row"}, null);
    tv.Down(6, 8); tv.Move(60, 8 + 30); tv.Up(60, 8 + 30);
    Chk(tv.HasSelection && tv.SelectedText.StartsWith("hello world" + Environment.NewLine, StringComparison.Ordinal), $"drag selection across lines: '{tv.SelectedText.Replace(Environment.NewLine, "|")}'");
    tv.Key(Keys.C | Keys.Control); Chk(Clipboard.GetText() == tv.SelectedText, "Ctrl+C copies the selection");
    tv.Key(Keys.A | Keys.Control); Chk(tv.SelectedText == tv.Text, "Ctrl+A selects everything");
    tv.Key(Keys.Escape); Chk(!tv.HasSelection, "Escape clears the selection");
    tv.DoubleClick(30, 8); Chk(tv.SelectedText == "hello", $"double click selects the word ('{tv.SelectedText}')");
    // scrolling: many lines, keyboard + FollowTail
    tv.SetLines(Enumerable.Range(0, 200).Select(i => "line " + i), null);
    Chk(tv.TopLine == 0, "starts at the top");
    tv.Key(Keys.PageDown); Chk(tv.TopLine > 0, $"PageDown scrolls ({tv.TopLine})");
    tv.Key(Keys.End | Keys.Control); Chk(tv.TopLine > 150, $"Ctrl+End scrolls to the end ({tv.TopLine})");
    tv.Key(Keys.Home | Keys.Control); Chk(tv.TopLine == 0, "Ctrl+Home scrolls to the top");
    tv.FollowTail = true; tv.ScrollToEnd(); int top = tv.TopLine; tv.AppendLine("tail"); Chk(tv.TopLine > top, "FollowTail keeps the end visible after AppendLine");
    tv.ScrollToLine(0); tv.AppendLine("tail 2"); Chk(tv.TopLine == 0, "FollowTail leaves a scrolled-up view alone");
    // colours: per-line runs and a colorizer
    var cv = new SpriteTextView { Size = new Size(460, 160), LineNumbers = true, TextScale = 1 };
    cv.SetLines(new[]{"// a comment", "int x = 42; // trailing", "string s = \"text\";"}, new TextRun[]?[]{ new[]{ new TextRun(0, 12, 0x6A9955) }, new[]{ new TextRun(0, 3, 0x569CD6), new TextRun(8, 2, 0xB5CEA8), new TextRun(12, 11, 0x6A9955) }, null });
    cv.AppendLine("coloured whole line", 0xFF8040);
    var bmp = cv.RenderOnce();
    bool sawGreen = false, sawOrange = false;
    for (int yy = 0; yy < bmp.Height; yy++) for (int xx = 0; xx < bmp.Width; xx++) { int px = bmp.Pixels[yy * bmp.Width + xx] & 0xFFFFFF; if (px == 0x6A9955) sawGreen = true; if (px == 0xFF8040) sawOrange = true; }
    Chk(sawGreen && sawOrange, $"run colours reach the pixels (green {sawGreen}, orange {sawOrange})");
    int col = 0; var cz = new SpriteTextView { Size = new Size(200, 60), Colorizer = l => { col++; return l.Length > 0 ? new[]{ new TextRun(0, l.Length, 0x00FF00) } : null; } };
    cz.SetLines(new[]{"x", "y"}, null); cz.RenderOnce(); cz.RenderOnce();
    Chk(col == 2, $"colorizer is asked once per visible line ({col})");
    Put(tv, 10, 10); Put(cv, 10, 140);
    var real = new SpriteTextView { Size = new Size(300, 100), WordWrap = true, TextFontFamily = "DejaVu Sans", TextFontSize = 13 };
    real.Text = "Real font, wrapped: the quick brown fox jumps over the lazy dog. Кириллица тоже работает.";
    Put(real, 480, 10);
    // ---- SpriteStackPanel: vertical stack with stretch, hidden children skipped, AutoSize, wrap, scrolling
    var st = new SpriteStackPanel { Size = new Size(200, 300), Gap = 4, Padding = new Padding(6) };
    var a = new SpriteButton { Text = "A", Size = new Size(50, 24) }; var b = new SpriteButton { Text = "B", Size = new Size(50, 30) }; var h = new SpriteButton { Text = "hidden", Size = new Size(50, 30), Visible = false }; var d = new SpriteButton { Text = "D", Size = new Size(50, 24) };
    st.Controls.Add(a); st.Controls.Add(b); st.Controls.Add(h); st.Controls.Add(d);
    const int F = 2;   // the panel frame inset (SpritePanel.DisplayRectangle)
    Chk(a.Top == F + 6 && b.Top == F + 6 + 24 + 4 && d.Top == F + 6 + 24 + 4 + 30 + 4, $"vertical stack positions {a.Top},{b.Top},{d.Top}");
    Chk(a.Width == 200 - 12 - 2 * F && a.Left == F + 6, $"Stretch gives children the full width ({a.Width})");
    st.Stretch = false; var e5 = new SpriteButton { Text = "E", Size = new Size(50, 24) }; st.Controls.Add(e5);
    Chk(e5.Width == 50 && e5.Top == d.Bottom + 4, "Stretch off keeps the child's own width"); st.Controls.Remove(e5);
    st.AutoSize = true; Chk(st.Height == d.Bottom + 6 + F, $"AutoSize shrinks the panel to the content ({st.Height} vs {d.Bottom + 6 + F})");
    st.AutoSize = false; st.Height = 60; st.PerformLayout();
    Chk(st.ContentSize > 60, $"content taller than the panel is reported ({st.ContentSize})");
    var bar = typeof(SpriteStackPanel).GetField("_bar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(st) as SpriteScrollBar;
    Chk(bar != null && bar.Visible, "an overflowing vertical stack shows a scroll bar");
    st.Wheel(50, 30, -120); Chk(a.Top < F + 6, $"wheel scrolls the content ({a.Top})");
    // nested stacks: an inner AutoSize field (label over a combo) must position the rows below it at its SETTLED height,
    // not at the SpritePanel default 100 - the stack re-passes while child heights settle during layout
    var outer = new SpriteStackPanel { Size = new Size(300, 400), Gap = 6, Padding = new Padding(4) };
    var inner = new SpriteStackPanel { AutoSize = true, Gap = 2, Padding = Padding.Empty };
    var il = new SpriteButton { Text = "title", Size = new Size(80, 16) }; var ic = new SpriteButton { Text = "combo", Size = new Size(120, 24) };
    inner.Controls.Add(il); inner.Controls.Add(ic); inner.PerformLayout();          // settles inner.Height = 16 + 2 + 24
    var row2 = new SpriteButton { Text = "below", Size = new Size(60, 20) };
    outer.Controls.Add(inner); outer.Controls.Add(row2); outer.PerformLayout();
    int wantY = F + 4 + inner.Height + 6;                                          // frame + padding + field + gap
    Chk(inner.Height == 16 + 2 + 24 + 4 && row2.Top == wantY, $"nested stack uses the settled field height (inner {inner.Height}, row2 {row2.Top} vs {wantY})");
    Chk(inner.Left + il.Left - row2.Left == F, $"FrameInset=true indents a nested row's children by the inner frame ({inner.Left}+{il.Left} vs {row2.Left})");   // il.Left is inner-relative - map it through inner.Left
    inner.FrameInset = false;                                                       // the setter lays the inner row out itself - do NOT re-layout the outer here (recurses)
    Chk(inner.Left + il.Left == row2.Left, $"FrameInset=false aligns a nested row's children with a direct row's ({inner.Left}+{il.Left} vs {row2.Left})");
    h.Visible = true; st.PerformLayout(); Chk(d.Top - b.Bottom > 30, "a child made visible takes its place");
    var hs = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Size = new Size(150, 10), Gap = 4, Padding = Padding.Empty, Stretch = false };
    for (int i = 0; i < 5; i++) hs.Controls.Add(new SpriteToggle { Text = "bit " + i, Size = new Size(60, 22), Style = ToggleStyle.CheckBox });
    var tg = hs.Controls.Where(k => k is SpriteToggle).ToList();   // Controls[0] is the panel's own scroll bar
    Chk(tg[0].Top == tg[1].Top && tg[2].Top > tg[1].Top, $"horizontal wrap starts a new row when the width is used up ({string.Join(" ", hs.Controls.Where(k => k is SpriteToggle).Select(k => k.Left + "," + k.Top))}; panel {hs.Width}x{hs.Height})");
    Chk(hs.Height == tg[4].Bottom + F, $"AutoSize on a wrapped row set the height ({hs.Height})");
    // no-overlap invariant (the "a closed drop list must never sit on the buttons below" rule): in a vertical stack every
    // row's top is exactly the previous row's bottom + Gap - checked on a replica of the demo's Setup column
    // (fixed-height combo fields, toggles, a wrapped AutoSize buttons row)
    var setup = new SpriteStackPanel { Size = new Size(312, 690), Gap = 6, Padding = new Padding(4) };
    SpriteStackPanel Field(string title, int lines)
    {
        var f = new SpriteStackPanel { Gap = 2, Padding = Padding.Empty, Height = 16 + 11 * (lines - 1) + 2 + 24 + 4 };
        f.Controls.Add(new SpriteButton { Text = title, Size = new Size(280, 16 + 11 * (lines - 1)) });
        f.Controls.Add(new SpriteButton { Text = "combo", Size = new Size(196, 24) });
        setup.Controls.Add(f); return f;
    }
    var fCanvas = Field("Canvas (drawn 1:1 from the top-left of the preview)", 2);
    Field("Sprite size", 1); Field("Test picture", 1); Field("SIMD kernels", 1);
    for (int i = 0; i < 5; i++) setup.Controls.Add(new SpriteToggle { Text = "check " + i, Size = new Size(140, 28) });
    var fps = new SpriteStackPanel { Orientation = Orientation.Horizontal, Height = 26, Gap = 6, Padding = Padding.Empty, Stretch = false };
    fps.Controls.Add(new SpriteToggle { Text = "Limit fps to", Size = new Size(110, 22) });
    setup.Controls.Add(fps);
    var suite = new SpriteStackPanel { Orientation = Orientation.Horizontal, Wrap = true, AutoSize = true, Gap = 4, Padding = Padding.Empty, Stretch = false };
    suite.Controls.Add(new SpriteButton { Text = "Run suite (all tests)", Size = new Size(120, 36) });
    suite.Controls.Add(new SpriteButton { Text = "Copy log", Size = new Size(120, 36) });
    suite.Controls.Add(new SpriteButton { Text = "Save CSV", Size = new Size(120, 36) });
    setup.Controls.Add(suite);
    setup.PerformLayout();
    var srows = setup.Controls.Where(k => k is not SpriteScrollBar).ToList();
    bool spaced = true; for (int i = 1; i < srows.Count; i++) if (srows[i].Top != srows[i - 1].Bottom + 6) spaced = false;
    Chk(spaced && suite.Top >= fCanvas.Bottom + 300, $"setup column: every row top = previous bottom + gap {srows.Count} rows, buttons at {suite.Top} vs combo field bottom {fCanvas.Bottom})");
    // a child reporting a zero height (stale layout, hidden-then-shown) must not pull the rows after it onto itself
    var z = new SpriteStackPanel { Size = new Size(200, 300), Gap = 6, Padding = new Padding(4) };
    var z1 = new SpriteButton { Text = "1", Size = new Size(60, 40) }; var z2 = new SpriteButton { Text = "2", Size = new Size(60, 0) }; var z3 = new SpriteButton { Text = "3", Size = new Size(60, 40) };
    z.Controls.Add(z1); z.Controls.Add(z2); z.Controls.Add(z3); z.PerformLayout();
    Chk(z2.Height >= 1 && z2.Top >= z1.Bottom + 6 && z3.Top >= z2.Bottom + 6, $"a zero-height row cannot pull the next rows onto it (h {z2.Height}, tops {z1.Bottom} {z2.Top} {z2.Bottom} {z3.Top})");
    // the Setup-tab trap: WinForms' Visible walks the ancestor chain, so while the form/page is hidden every child
    // reports "invisible" and a layout pass must not run - the rows get positioned only when the page is really shown
    var host = new Control { Size = new Size(320, 400) };                   // stands in for the form
    var page = new Control { Size = new Size(312, 392) };
    var stv = new SpriteStackPanel { Size = new Size(312, 392), Gap = 6, Padding = new Padding(4) };
    var r1 = new SpriteButton { Text = "one", Size = new Size(120, 30) }; var r2 = new SpriteButton { Text = "two", Size = new Size(120, 30) };
    page.Visible = false; host.Visible = false;                             // before the app is shown everything is chain-hidden
    page.Controls.Add(stv); host.Controls.Add(page);                        // the wiring happens under the hidden chain, like in the demo
    stv.Controls.Add(r1); stv.Controls.Add(r2);
    stv.PerformLayout();
    Chk(r1.Top == 0 && r2.Top == 0, $"a layout pass while the panel is chain-hidden does not run (tops {r1.Top},{r2.Top})");
    host.Visible = true; page.Visible = true;                               // the app started and the user opened the tab
    Chk(r1.Top == 6 && r2.Top == 42, $"the first trigger after showing lays the rows out (tops {r1.Top},{r2.Top})");
    st.Height = 200; st.PerformLayout(); Put(st, 800, 10); Put(hs, 800, 230); Put(setup, 1150, 10);
    // ---- SpriteListBox: headers are drawn differently, cannot be selected, and are skipped by the keyboard
    var lb = new SpriteListBox { Size = new Size(220, 150), ItemHeight = 18 };
    lb.SetItems(new[]{"Group A", "one", "two", "Group B", "three"}, new[]{0, 3}, new Dictionary<int, int>{ [2] = Color.Gray.ToArgb() });
    Chk(lb.IsHeader(0) && lb.IsHeader(3) && !lb.IsHeader(1), "IsHeader");
    lb.Tap(100, 2 + 18 / 2); Chk(lb.SelectedIndex < 0, $"clicking a header selects nothing ({lb.SelectedIndex})");
    lb.SelectedIndex = 1; lb.Key(Keys.Down); lb.Key(Keys.Down); Chk(lb.SelectedIndex == 4, $"Down skips the header ({lb.SelectedIndex})");
    lb.Key(Keys.Up); Chk(lb.SelectedIndex == 2, $"Up skips the header ({lb.SelectedIndex})");
    lb.Key(Keys.Home); Chk(lb.SelectedIndex == 1, $"Home lands on the first item, not the header ({lb.SelectedIndex})");
    Put(lb, 480, 130);
    Save(c, "/home/user/.cache/ctlrun/textviews.rgba");
    Console.WriteLine("text views: all checks passed");

    // ---------------------------------------------------------------- review round 2 pins
    {   // FillPattern: OVER must not manufacture alpha over transparent pixels, and the lock rect must protect
      using var s2 = new Sprite(64, 64);
      using var pat = new Sprite(4, 4);
      for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) pat.SetPixel(x, y, SR2D.ARGB(128, 200, 60, 40));
      s2.FillPattern(new System.Drawing.PointF[] { new(8, 8), new(56, 8), new(32, 56) }, pat, 0, 0, 1, 0, true, SR2D.LineOp.AlphaBlend);
      int al = (s2.GetPixel(32, 20) >>> 24) & 255;
      Chk(al > 0 && al < 100, $"pattern fill: OVER over transparent keeps a sane alpha ({al}, the old bug manufactured ~207)");
      s2.ClearBuffer(unchecked((int)0xFF222222));
      s2.SetLockRect(0, 32, 0, 64);
      s2.FillPattern(new System.Drawing.PointF[] { new(4, 4), new(60, 4), new(32, 60) }, pat, 0, 0, 1, 0, true, SR2D.LineOp.Set);
      bool protectedOk = true;
      for (int y = 0; y < 64 && protectedOk; y++) for (int x = 40; x < 64; x++) if (s2.GetPixel(x, y) != unchecked((int)0xFF222222)) { protectedOk = false; break; }
      Chk(protectedOk, "pattern fill: pixels outside the LOCK rect are untouched");
      s2.SetLockRect();
    }
    {   // EditHistory: the byte account must stay bounded and non-negative through record / undo / redo cycles
      using var s3 = new Sprite(64, 64);
      var hist = new EditHistory(8192);
      for (int i = 0; i < 50; i++)
      {
        hist.Record(s3, new System.Drawing.Rectangle(i % 32, 0, 8, 8));
        if (i % 3 == 2) hist.Undo(s3);
        if (i % 5 == 4) hist.Redo(s3);
      }
      Chk(hist.MemoryBytes >= 0 && hist.MemoryBytes <= 8192, $"edit history: the account stays bounded through undo/redo ({hist.MemoryBytes} B of 8192)");
      Chk(hist.UndoCount > 0 || hist.RedoCount > 0, "edit history: entries survive the loops (the old drift evicted them)");
      hist.Record(s3, new System.Drawing.Rectangle(0, 0, 8, 8));   // discard the redo tail
      Chk(hist.MemoryBytes <= 8192, "edit history: clearing the redo tail releases its bytes (the old leak inflated the account)");
    }
    {   // views: a view that outlives its owner must be a no-op, not a write into freed memory
      var owner = new Sprite(32, 32);
      owner.ClearBuffer(unchecked((int)0xFF3366AA));
      var view = owner.CreateView(4, 4, 28, 28);
      bool works = false;
      view.FillRect(5, 5, 8, 8, unchecked((int)0xFFFF0000));         // inside the view's lock rect (4..28)
      works = owner.GetPixel(5, 5) == unchecked((int)0xFFFF0000) && owner.GetPixel(2, 2) == unchecked((int)0xFF3366AA);
      Chk(works, "views: a live view draws through to the owner (and stays clipped to it)");
      owner.Dispose();                                              // the buffer is gone - the view must have been poisoned
      bool clean = true;
      try { view.FillRect(0, 0, 4, 4, unchecked((int)0xFF00FF00)); view.ClearBuffer(0); } catch (Exception) { clean = false; }
      Chk(clean && view.Width == 0, "views: a view used after its owner died is a no-op (poisoned, no crash)");
      view.Dispose();
    }
    Console.WriteLine("review pins: all checks passed");

    // ---------------------------------------------------------------- DrawQuadWarp (cs/Sprite.Warp.cs) pins
    {   // identity: dest quad == source quad reproduces the region byte for byte
      using var s2 = new Sprite(64, 64);
      for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) s2.SetPixel(x, y, SR2D.ARGB(255, (byte)(x * 4), (byte)(y * 4), (byte)((x + y) & 255)));
      using var refSnap = new Sprite(64, 64); refSnap.ClearBuffer(0); refSnap.Draw(s2, 0, 0, SR2D.Op.Paint);
      var q = new System.Drawing.PointF[] { new(8, 8), new(40, 8), new(40, 24), new(8, 24) };
      s2.DrawQuadWarp(refSnap, q, q);
      bool same = true;
      for (int y = 0; y < 64 && same; y++) for (int x = 0; x < 64; x++) if (s2.GetPixel(x, y) != refSnap.GetPixel(x, y)) { same = false; break; }
      Chk(same, "quad warp: an identity quad reproduces the region byte for byte");
    }
    {   // integer translation: the region comes out exactly shifted
      using var s3 = new Sprite(64, 64); using var src = new Sprite(64, 64);
      for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) src.SetPixel(x, y, SR2D.ARGB(255, (byte)(x * 3), (byte)(y * 3), (byte)(x ^ y)));
      s3.Draw(src, 0, 0, SR2D.Op.Paint);
      var sq = new System.Drawing.PointF[] { new(10, 6), new(34, 6), new(34, 22), new(10, 22) };
      var dq = new System.Drawing.PointF[] { new(15, 9), new(39, 9), new(39, 25), new(15, 25) };
      s3.DrawQuadWarp(src, sq, dq);
      bool shifted = true;
      for (int y = 11; y < 23 && shifted; y++) for (int x = 17; x < 37; x++)
          if (s3.GetPixel(x, y) != src.GetPixel(x - 5, y - 3)) { shifted = false; break; }
      Chk(shifted, "quad warp: an integer translation maps the interior exactly");
    }
    {   // corners land on corners (2x upscale, nearest)
      using var s4 = new Sprite(32, 32); using var src = new Sprite(32, 32);
      for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) src.SetPixel(x, y, SR2D.ARGB(255, (byte)(x * 8), (byte)(y * 8), 0));
      s4.Draw(src, 0, 0, SR2D.Op.Paint);
      var sq = new System.Drawing.PointF[] { new(4, 4), new(12, 4), new(12, 12), new(4, 12) };
      var dq = new System.Drawing.PointF[] { new(4, 4), new(20, 4), new(20, 20), new(4, 20) };
      s4.DrawQuadWarp(src, sq, dq);
      bool corners = s4.GetPixel(4, 4) == src.GetPixel(4, 4) && s4.GetPixel(19, 19) == src.GetPixel(11, 11)
                  && s4.GetPixel(4, 19) == src.GetPixel(4, 11) && s4.GetPixel(19, 4) == src.GetPixel(11, 4);
      Chk(corners, "quad warp: the corners land on the corners (2x upscale)");
    }
    {   // two quads sharing corners deform without a crack along the shared edge
      using var s5 = new Sprite(64, 64);
      for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) s5.SetPixel(x, y, (x / 8 + y / 8 & 1) == 0 ? unchecked((int)0xFF9E9E9E) : unchecked((int)0xFFB4B4B4));
      using var src = new Sprite(64, 64);
      for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) src.SetPixel(x, y, SR2D.ARGB(255, (byte)(x * 4), (byte)(y * 4), 128));
      var qa = new System.Drawing.PointF[] { new(8, 8), new(30, 12), new(30, 18), new(8, 20) };   // right edge pulled to x=30, slanted
      var qb = new System.Drawing.PointF[] { new(30, 12), new(48, 8), new(48, 20), new(30, 18) }; // shares BOTH moved corners
      s5.DrawQuadWarp(src, new System.Drawing.PointF[] { new(8, 8), new(24, 8), new(24, 20), new(8, 20) }, qa);
      s5.DrawQuadWarp(src, new System.Drawing.PointF[] { new(24, 8), new(48, 8), new(48, 20), new(24, 20) }, qb);
      bool sealed2 = true;
      for (int y = 13; y <= 17 && sealed2; y++)
          if (s5.GetPixel(29, y) == unchecked((int)0xFF9E9E9E) || s5.GetPixel(29, y) == unchecked((int)0xFFB4B4B4)
           || s5.GetPixel(30, y) == unchecked((int)0xFF9E9E9E) || s5.GetPixel(30, y) == unchecked((int)0xFFB4B4B4)) sealed2 = false;
      Chk(sealed2, "quad warp: shared corners keep the deformed seam closed (no crack)");
    }
    {   // bowtie: runs, changes nothing, throws nothing
      using var s6 = new Sprite(32, 32); s6.ClearBuffer(unchecked((int)0xFF3366AA));
      using var src = new Sprite(32, 32); src.ClearBuffer(unchecked((int)0xFFFF00FF));
      var bow = new System.Drawing.PointF[] { new(4, 4), new(24, 24), new(24, 4), new(4, 24) };
      s6.DrawQuadWarp(src, bow, bow);
      bool intact = true;
      for (int y = 0; y < 32 && intact; y++) for (int x = 0; x < 32; x++) if (s6.GetPixel(x, y) != unchecked((int)0xFF3366AA)) { intact = false; break; }
      Chk(intact, "quad warp: a self-intersecting (bowtie) quad warps nothing and throws nothing");
    }
    {   // AlphaBlend: transparent samples leave the pixel alone, opaque samples replace
      using var s7 = new Sprite(16, 16); s7.ClearBuffer(unchecked((int)0xFF3366AA));
      using var src = new Sprite(16, 16); src.ClearBuffer(0); src.FillRect(0, 0, 8, 8, unchecked((int)0xFFFF00FF00));
      var q = new System.Drawing.PointF[] { new(2, 2), new(14, 2), new(14, 14), new(2, 14) };
      s7.DrawQuadWarp(src, q, q, SR2D.Op.AlphaBlend);
      Chk(s7.GetPixel(4, 4) == unchecked((int)0xFFFF00FF00) && s7.GetPixel(10, 10) == unchecked((int)0xFF3366AA),
          "quad warp: AlphaBlend honours per-pixel source alpha");
    }
    {   // the lock rect protects pixels from warping too
      using var s8 = new Sprite(32, 32); s8.ClearBuffer(unchecked((int)0xFF3366AA));
      using var src = new Sprite(32, 32); src.ClearBuffer(unchecked((int)0xFFFF00FF00));
      s8.SetLockRect(0, 16, 0, 32);
      var q = new System.Drawing.PointF[] { new(2, 2), new(28, 2), new(28, 28), new(2, 28) };
      s8.DrawQuadWarp(src, q, q);
      bool locked = true;
      for (int y = 0; y < 32 && locked; y++) for (int x = 16; x < 32; x++) if (s8.GetPixel(x, y) != unchecked((int)0xFF3366AA)) { locked = false; break; }
      Chk(locked, "quad warp: pixels outside the LOCK rect are untouched");
      s8.SetLockRect();
    }
    {   // QuadInvert: the point-in-quad test the cage editor hit-tests with
      var q = new System.Drawing.PointF[] { new(10, 10), new(30, 12), new(28, 26), new(8, 24) };
      var mid = Sprite.QuadInvert(q, new System.Drawing.PointF(19, 18));
      Chk(mid != null && MathF.Abs(mid.Value.U - 0.5f) < 0.03f && MathF.Abs(mid.Value.V - 0.5f) < 0.03f, "quad warp: QuadInvert finds the quad centre at (0.5, 0.5)");
      Chk(Sprite.QuadInvert(q, new System.Drawing.PointF(2, 2)) == null, "quad warp: QuadInvert rejects points outside");
      Chk(Sprite.QuadInvert(q, new System.Drawing.PointF(10, 10)) != null, "quad warp: QuadInvert accepts the corners (inclusive edges)");
    }
    Console.WriteLine("quad warp: all checks passed");

    {   // the assembly-wide DllImport pin must keep System32: winmm is NOT a KnownDLL (gdi32 / user32 are),
      // so ApplicationDirectory alone made the demo die on its timeBeginPeriod P/Invoke with
      // DllNotFoundException "winmm" the moment the round-2 pin went in
      var attr = (System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute?)System.Attribute.GetCustomAttribute(typeof(SR2D).Assembly, typeof(System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute));
      Chk(attr != null
          && (attr.Paths & System.Runtime.InteropServices.DllImportSearchPath.System32) != 0
          && (attr.Paths & System.Runtime.InteropServices.DllImportSearchPath.ApplicationDirectory) != 0,
          "resolver pin: the assembly-wide DllImport search keeps the app directory AND System32 (winmm)");
    }
    Console.WriteLine("resolver pins: all checks passed");

    // ---------------------------------------------------------------- SpriteColorPicker (cs/SpriteControls.Color.cs) pins
    {   // the pure HSV maths: primaries, alpha, achromatic
      Chk(SpriteColorPicker.HsvToRgb(0, 1, 1) == unchecked((int)0xFFFF0000), "color: HSV(0, 1, 1) = red");
      Chk(SpriteColorPicker.HsvToRgb(120, 1, 1) == unchecked((int)0xFF00FF00), "color: HSV(120, 1, 1) = green");
      Chk(SpriteColorPicker.HsvToRgb(240, 1, 1) == unchecked((int)0xFF0000FF), "color: HSV(240, 1, 1) = blue");
      Chk(SpriteColorPicker.HsvToRgb(0, 0, 1) == unchecked((int)0xFFFFFFFF) && SpriteColorPicker.HsvToRgb(200, 0, 0) == unchecked((int)0xFF000000), "color: s = 0 is white / black regardless of hue");
      Chk((SpriteColorPicker.HsvToRgb(30, 1, 1) >>> 24) == (SpriteColorPicker.HsvToRgb(30, 1, 1, 128) >>> 24) + 127, "color: the alpha argument lands in the top byte");
      bool ok = true;
      for (int hue = 0; hue < 360 && ok; hue += 10)
        foreach (float s in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
          foreach (float v in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
          {
            int rgb = SpriteColorPicker.HsvToRgb(hue, s, v);
            var back = SpriteColorPicker.RgbToHsv(rgb);
            if (s == 0 || v == 0)
            {   // achromatic: the hue is gone by definition (the picker then keeps its own hue) - only s / v survive
                if (back.S != 0 || MathF.Abs(v - back.V) > 0.004f) { ok = false; break; }
                continue;
            }
            float dh = MathF.Abs(hue - back.H); if (dh > 180) dh = 360 - dh;          // hue is circular (360 -> 0)
            // tolerances must absorb the 8-bit round trip: at v = 0.25 one channel step is ~0.016 in s / v and a
            // couple of degrees of hue
            if (dh > 2.5f || MathF.Abs(s - back.S) > 0.02f || MathF.Abs(v - back.V) > 0.02f) { ok = false; break; }
          }
      Chk(ok, "color: RGB -> HSV -> RGB round-trips the hue / saturation / value grid");
    }
    {   // the control: Value syncs the entries, events fire once, ShowAlpha keeps the alpha, render smoke
      int fired = 0;
      var pk = new SpriteColorPicker { Size = new Size(244, 176) };   // (no using: the headless Control shim has no IDisposable)
      pk.ValueChanged += (_, _) => fired++;
      pk.Value = System.Drawing.Color.FromArgb(128, 255, 0, 0);
      Chk(fired == 1 && pk.Value == System.Drawing.Color.FromArgb(128, 255, 0, 0), "color: Value set fires once and round-trips (alpha kept)");
      Chk(pk.Value == System.Drawing.Color.FromArgb(128, 255, 0, 0), "color: the getter returns the exact ARGB");
      pk.ShowAlpha = false;
      Chk(pk.Value.A == 128, "color: with the alpha bar hidden the alpha is kept, not reset");
      pk.Scheme = ColorScheme.Hsb; pk.RenderOnce();                          // the paint path (square, bars, markers, labels)
      pk.Scheme = ColorScheme.Cmyk; pk.RenderOnce();
      pk.Mode = SelectorMode.Wheel; pk.RenderOnce();
      pk.Mode = SelectorMode.Brightness; pk.RenderOnce();
      pk.Mode = SelectorMode.HueSquare; pk.Scheme = ColorScheme.Rgb; pk.ShowAlpha = false; pk.RenderOnce();
      Chk(true, "color: RenderOnce survives every mode / scheme / layout (no throw)");
      fired = 0; pk.Value = System.Drawing.Color.FromArgb(128, 255, 0, 0);   // same colour: no event
      Chk(fired == 0, "color: setting the same colour raises nothing");
      pk.Dispose();
    }
    {   // THE marker regression: an SV drag must store the new s / v (the first dialog version stored only the
      // hue - the ring never followed the mouse). Reproduced through the same maths the mouse handler calls.
      var pk2 = new SpriteColorPicker { Size = new Size(244, 252) };
      pk2.Value = System.Drawing.Color.FromArgb(255, 255, 0, 0);      // h = 0, s = 1, v = 1
      // (the drag maths itself is exercised via the HSV setters the handler calls: SetHsv stores s / v)
      int before = pk2.Value.ToArgb();
      var mid = SpriteColorPicker.HsvToRgb(0, 0.5f, 0.5f);
      Chk(mid == unchecked((int)0xFF804040), $"color: HSV(0, 0.5, 0.5) is the mid red ({mid:X8})");
      _ = before; _ = pk2;
    }
    {   // the six schemes round-trip through the published conversions
      bool ok = true;
      int worst = 0;
      for (int rgb = 0; rgb < 1 << 24 && ok; rgb += 7919)             // stride walk: ~2100 samples over the cube
      {
        int rr = (rgb >>> 16) & 255, gg = (rgb >>> 8) & 255, bb = rgb & 255;
        int Err(int back) => Math.Max(Math.Abs(((back >>> 16) & 255) - rr), Math.Max(Math.Abs(((back >>> 8) & 255) - gg), Math.Abs((back & 255) - bb)));
        var hsv = SpriteColorPicker.RgbToHsv(rgb);
        var hsl = SpriteColorPicker.RgbToHsl(rgb);
        var yiq = SpriteColorPicker.RgbToYiq(rgb);
        var cmyk = SpriteColorPicker.RgbToCmyk(rgb);
        var lab = SpriteColorPicker.RgbToLab(rgb);
        // CMYK round-trips exactly; HSV / HSL / YIQ carry the 8-bit rounding of the inverse matrix (+- 1);
        // Lab is a perceptual space through a gamma curve (+- 3)
        worst = Math.Max(worst, Err(SpriteColorPicker.HsvToRgb(hsv.H, hsv.S, hsv.V)));
        worst = Math.Max(worst, Err(SpriteColorPicker.HslToRgb(hsl.H, hsl.S, hsl.L)));
        worst = Math.Max(worst, Err(SpriteColorPicker.YiqToRgb(yiq.Y, yiq.I, yiq.Q)));
        if (Err(SpriteColorPicker.CmykToRgb(cmyk.C, cmyk.M, cmyk.Y, cmyk.K)) > 0) ok = false;
        if (Err(SpriteColorPicker.LabToRgb(lab.L, lab.A, lab.B)) > 3) ok = false;
      }
      Chk(ok && worst <= 1, $"color: HSV / HSL / YIQ round-trip within 1/255 (worst {worst}), CMYK exact, Lab within 3/255");
    }
    {   // hex parsing: #RGB, #RRGGBB, #AARRGGBB, garbage is ignored
      var pk3 = new SpriteColorPicker { Size = new Size(244, 252) };
      pk3.Value = System.Drawing.Color.Navy;
      pk3.Hex = "#F00";
      Chk(pk3.Value == System.Drawing.Color.FromArgb(255, 255, 0, 0), "color: hex #F00 parses");
      pk3.Hex = "00FF00";
      Chk(pk3.Value == System.Drawing.Color.FromArgb(255, 0, 255, 0), "color: hex without # parses");
      pk3.Hex = "#80406080";
      Chk(pk3.Value == System.Drawing.Color.FromArgb(0x80, 0x40, 0x60, 0x80), "color: #AARRGGBB parses (alpha lands)");
      pk3.Hex = "not-a-color";
      Chk(pk3.Value == System.Drawing.Color.FromArgb(0x80, 0x40, 0x60, 0x80), "color: garbage hex is ignored");
      Chk(pk3.Hex == "#80406080", "color: Hex output is #AARRGGBB");
      pk3.Dispose();
    }
    {   // the dialog wiring: buttons carry DialogResult (WinForms closes the modal itself), the form is themed
      var dlg = new SpriteColorDialog { Value = System.Drawing.Color.Teal };
      dlg.Show();
      Chk(dlg.Value.ToArgb() == System.Drawing.Color.Teal.ToArgb(), "color dialog: Value flows in");   // named colours never equal raw FromArgb - compare ARGB
      dlg.Value = System.Drawing.Color.FromArgb(255, 10, 20, 30);
      Chk(dlg.Value == System.Drawing.Color.FromArgb(255, 10, 20, 30), "color dialog: Value flows out");
      dlg.Dispose();
    }
    {   // the v3.1 round: square gamut, strips following it, capitalised drop lists, the embeddable dialog
      var pk = new SpriteColorPicker { Size = new Size(250, 252) };
      pk.PerformLayout();
      System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;   // not "F": the stack pins use it for the frame inset
      Rectangle Sel() => (Rectangle)typeof(SpriteColorPicker).GetField("_selRect", BF)!.GetValue(pk)!;
      Rectangle Bar() => (Rectangle)typeof(SpriteColorPicker).GetField("_barRect", BF)!.GetValue(pk)!;
      Chk(Sel().Width == Sel().Height && Sel().Width > 8, $"color: the gamut is a square ({Sel().Width}x{Sel().Height})");
      Chk(Bar().Width == Sel().Width && Bar().Y == Sel().Bottom + 6, "color: the strip follows the square, not the old full width");
      foreach (var m in new[] { SelectorMode.HueSquare, SelectorMode.Brightness, SelectorMode.Wheel })
      {
        pk.Mode = m; pk.PerformLayout();
        Chk(Sel().Width == Sel().Height, $"color: {m} keeps the gamut square");
      }
      var mb = (SpriteCombo)typeof(SpriteColorPicker).GetField("_modeBox", BF)!.GetValue(pk)!;
      var sb = (SpriteCombo)typeof(SpriteColorPicker).GetField("_schemeBox", BF)!.GetValue(pk)!;
      Chk(mb.Items[0] == "Hue Square" && mb.Items[1] == "Brightness" && mb.Items[2] == "Wheel", "color: the mode drop list is capitalised");
      Chk(sb.Items[2] == "HSL", "color: the scheme list says HSL (was the HLS typo)");
      var dlg2 = new SpriteColorDialog { Value = System.Drawing.Color.FromArgb(255, 1, 2, 3) };
      Chk(dlg2.CloseOnButton, "color dialog: CloseOnButton defaults to true (the modal use)");
      dlg2.CloseOnButton = false; dlg2.Show();
      bool fired = false; dlg2.ButtonClick += (_, _) => fired = true;
      var okBtn = (SpriteButton)typeof(SpriteColorDialog).GetField("_ok", BF)!.GetValue(dlg2)!;
      typeof(SpriteClickable).GetMethod("Activate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(okBtn, null);
      Chk(fired && dlg2.DialogResult == DialogResult.OK && dlg2.Visible, "color dialog: CloseOnButton = false keeps it open and raises ButtonClick");
      dlg2.Dispose();
    }
    {   // the eyedropper: the preview arms it, one click picks the desktop colour, Esc / right click cancels
      var pk = new SpriteColorPicker { Size = new Size(250, 252) };
      pk.PerformLayout();
      System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
      System.Windows.Forms.Cursor.Position = new Point(500, 400);
      Point seen = new(-1, -1);
      SpriteColorPicker.ScreenSampler = p => { seen = p; return unchecked((int)0xFF123456); };
      var before = pk.Cursor;
      pk.BeginPick();
      Chk(pk.Picking && pk.Capture, "eyedropper: BeginPick arms and captures the mouse");
      var pickCur = pk.Cursor;
      // the engine hands over its own pipette (nib on the hotspot) on Windows and Cursors.Cross when headless
      Chk(pickCur != before && (ReferenceEquals(pickCur, Cursors.Cross) || !ReferenceEquals(pickCur, Cursors.Default)),
        $"eyedropper: the cursor switched to the pick cursor ({(ReferenceEquals(pickCur, Cursors.Cross) ? "cross (headless)" : "pipette")})");
      Chk(seen == new Point(500, 400), "eyedropper: it starts live from the colour under the pointer");
      typeof(Control).GetMethod("OnMouseMove", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.None, 0, 30, 40, 0) });
      Chk(seen == new Point(30, 40), "eyedropper: a move samples the desktop at the pointer (PointToScreen)");
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 30, 40, 0) });
      Chk(!pk.Picking && !pk.Capture && pk.Cursor == before, "eyedropper: one click picks and puts the regular cursor back");
      Chk(pk.Value.ToArgb() == unchecked((int)0xFF123456), "eyedropper: the picked colour landed in Value");
      SpriteColorPicker.ScreenSampler = p => unchecked((int)0xFF654321);
      var preview = (Rectangle)typeof(SpriteColorPicker).GetField("_previewRect", BF)!.GetValue(pk)!;
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, preview.X + 2, preview.Y + 2, 0) });
      Chk(pk.Picking, "eyedropper: clicking the preview's pipette badge arms it");
      typeof(Control).GetMethod("OnMouseMove", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.None, 0, 77, 33, 0) });
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 77, 33, 0) });
      Chk(!pk.Picking && pk.Value.ToArgb() == unchecked((int)0xFF654321), "eyedropper: the next one-click pick lands the new colour");
      pk.BeginPick();
      Chk(pk.Cursor == pickCur, "eyedropper: re-arming reuses the one built pick cursor (no new GDI handle per arm)");
      typeof(Control).GetMethod("OnKeyDown", BF)!.Invoke(pk, new object[] { new KeyEventArgs(Keys.Escape) });
      Chk(!pk.Picking && pk.Value.ToArgb() == unchecked((int)0xFF654321), "eyedropper: Esc cancels and keeps the colour");
      pk.BeginPick();
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0) });
      Chk(!pk.Picking && pk.Value.ToArgb() == unchecked((int)0xFF654321), "eyedropper: right click cancels too");
      SpriteColorPicker.ScreenSampler = p => unchecked((int)0xFF0A0B0C);
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, preview.X + 2, preview.Y + 2, 0) });
      typeof(Control).GetMethod("OnMouseMove", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.None, 0, 300, 400, 0) });
      typeof(Control).GetMethod("OnMouseUp", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 300, 400, 0) });
      Chk(!pk.Picking && pk.Value.ToArgb() == unchecked((int)0xFF0A0B0C), "eyedropper: press - sweep - release takes the colour under the pipette");
      SpriteColorPicker.ScreenSampler = p => unchecked((int)0xFF010203);
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, preview.X + 2, preview.Y + 2, 0) });
      typeof(Control).GetMethod("OnMouseUp", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, preview.X + 2, preview.Y + 2, 0) });
      Chk(pk.Picking, "eyedropper: a plain preview click (no sweep) stays armed for the one-click pick");
      typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 90, 20, 0) });
      Chk(!pk.Picking && pk.Value.ToArgb() == unchecked((int)0xFF010203), "eyedropper: after a plain badge click the next click still picks");
      SpriteColorPicker.ScreenSampler = null;
    }
    {   // round ten: the wheel's brightness bar, the gamut following the numeric column, the scheme ranges, the dialog's Esc
      var pk = new SpriteColorPicker { Size = new Size(250, 252) };
      pk.PerformLayout();
      System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
      Rectangle Bar() => (Rectangle)typeof(SpriteColorPicker).GetField("_barRect", BF)!.GetValue(pk)!;
      float Hf() => (float)typeof(SpriteColorPicker).GetField("_h", BF)!.GetValue(pk)!;
      SpriteNumeric Num(string f) => (SpriteNumeric)typeof(SpriteColorPicker).GetField(f, BF)!.GetValue(pk)!;
      float Bright() => Math.Max(pk.Value.R, Math.Max(pk.Value.G, pk.Value.B)) / 255f;
      void Click(int x, int y) => typeof(Control).GetMethod("OnMouseDown", BF)!.Invoke(pk, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
      pk.Value = System.Drawing.Color.FromArgb(255, 255, 0, 0);
      pk.Mode = SelectorMode.Wheel; pk.PerformLayout();
      var wbar = Bar();
      Click(wbar.Right - 2, wbar.Y + wbar.Height / 2);
      Chk(Bright() > 0.9f, $"color wheel: the brightness bar is BRIGHT at its right end ({Bright():0.00}) - it used to give black there");
      Click(wbar.X + 1, wbar.Y + wbar.Height / 2);
      Chk(Bright() < 0.1f, $"color wheel: and dark at its left end ({Bright():0.00}), matching the painted gradient");
      pk.Value = System.Drawing.Color.FromArgb(255, 0, 0, 255);
      Chk(Math.Abs(Hf() - 240) < 0.01f, $"color: an assignment seeds the hue state ({Hf():0.#})");
      Num("_c0").Value = 255; Num("_c2").Value = 0;                       // R 0 -> 255, B 255 -> 0: blue turns red
      Chk(pk.Value == System.Drawing.Color.FromArgb(255, 255, 0, 0), $"color: an RGB numeric edit changes the colour ({pk.Hex})");
      Chk(Hf() < 0.01f, $"color: and the gamut hue follows it ({Hf():0.#}) - it used to stay on the old blue");
      pk.Scheme = ColorScheme.Cmyk;
      Chk(Num("_cA").Maximum == 100, "color: CMYK's fourth entry is K (0..100)");
      pk.Scheme = ColorScheme.Rgb;
      Chk(Num("_cA").Maximum == 255, $"color: leaving CMYK restores the alpha entry (max {Num("_cA").Maximum:0}) - it used to stay at 100");
      pk.ShowAlpha = false; pk.PerformLayout();
      Chk(!Num("_cA").Visible, "color: ShowAlpha = false takes the alpha entry out of the column");
      pk.ShowAlpha = true; pk.PerformLayout();
      Chk(Num("_cA").Visible, "color: and puts it back");
      var pk2 = new SpriteColorPicker { Size = new Size(250, 252) };
      int keep = System.Drawing.Color.FromArgb(255, 200, 120, 40).ToArgb();
      pk2.Value = System.Drawing.Color.FromArgb(keep);
      var schemeBox = (SpriteCombo)typeof(SpriteColorPicker).GetField("_schemeBox", BF)!.GetValue(pk2)!;
      foreach (var (i, name) in new[] { (4, "Lab"), (5, "CMYK"), (1, "HSB"), (2, "HSL"), (3, "YIQ"), (0, "RGB") })
      {
        schemeBox.SelectedIndex = i;                                        // through the drop list: re-ranging the entries must not leak back as an edit
        Chk(pk2.Value.ToArgb() == keep, $"color: the {name} entry of the scheme drop list keeps the colour ({pk2.Hex})");
      }
      var dlg3 = new SpriteColorDialog { Value = System.Drawing.Color.FromArgb(keep) };
      dlg3.Show();
      var picker3 = (SpriteColorPicker)typeof(SpriteColorDialog).GetField("_picker", BF)!.GetValue(dlg3)!;
      SpriteColorPicker.ScreenSampler = _ => unchecked((int)0xFF204060);   // headless: no desktop to grab - the pipette must stay armed for the Esc pins below
      picker3.BeginPick();
      dlg3.Key(Keys.Escape);
      Chk(!picker3.Picking && dlg3.DialogResult == DialogResult.None, "color dialog: Esc while the pipette is armed lays it down instead of cancelling the dialog");
      picker3.BeginPick(); picker3.CancelPick();
      Chk(!picker3.Picking && picker3.Value.ToArgb() == keep, "color: CancelPick drops the pipette and keeps the colour");
      dlg3.Key(Keys.Escape);
      Chk(dlg3.DialogResult == DialogResult.Cancel, "color dialog: with no pick armed, Esc still cancels");
      SpriteColorPicker.ScreenSampler = null;
      dlg3.Dispose(); pk.Dispose(); pk2.Dispose();
    }
    Console.WriteLine("color picker: all checks passed");
  }
  static void SizeModeBox() {
    // The real SpriteBox + SpriteBox.View machinery (the demo's "SpriteBox SizeMode" strip builds exactly this while
    // its host is still invisible - the first-selection crash): modes, zoom / pan, scroll bar decisions, RenderOnce.
    // None of it may recurse (StackOverflow) or throw, however often the bar decision flips.
    var box = new SpriteBox { Size = new Size(600, 400), ImageSize = new Size(1600, 1200), SizeMode = SpriteSizeMode.Zoom, ScrollBars = true, Overscroll = 0, GdiSurface = false };   // plain surface: no GDI on the Linux runner; overscroll 0 = bars only when the image really does not fit
    int renders = 0; box.Render += (_, _) => renders++;
    box.RenderOnce(); Chk(renders == 1, $"RenderOnce ran the renderer once ({renders})");
    box.RenderOnce(); Chk(renders == 1, "clean surface is not re-rendered");
    box.Redraw(); box.RenderOnce(); Chk(renders == 2, "Redraw marks the scene dirty");
    box.SmoothZoom = false;                                    // jump mode: no glide timer in the headless runner
    box.ActualPixels();
    bool bars = box.ViewportRectangle.Width < box.ClientSize.Width || box.ViewportRectangle.Height < box.ClientSize.Height;
    Chk(bars, $"1:1 of a 1600x1200 image in 600x400 reserves scroll bar space (viewport {box.ViewportRectangle.Width}x{box.ViewportRectangle.Height})");
    box.FitToView(); box.ResetPan();
    var rp = (bool)(typeof(SpriteBox).GetField("_relayoutPending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(box) ?? false);
    var hb = (bool)(typeof(SpriteBox).GetField("_hbar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(box) is Control h ? h.Visible : false);
    // whatever the fit decides, the bars shown must match the decision - the first-selection crash was this pair fighting (and recursing)
    var decided = ((bool h, bool v))typeof(SpriteBox).GetMethod("DecideBars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(box, null)!;
    bool hbarShown = typeof(SpriteBox).GetField("_hbar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(box) is Control h2 && h2.Visible;
    bool vbarShown = typeof(SpriteBox).GetField("_vbar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(box) is Control v2 && v2.Visible;
    Chk(decided.h == hbarShown && decided.v == vbarShown, $"the bars shown match the decision (decided {decided.h}/{decided.v}, shown {hbarShown}/{vbarShown})");
    box.Zoom = 3; box.Pan = new PointF(10, 5); box.ResetZoom(); box.ResetPan();
    foreach (SpriteSizeMode m in Enum.GetValues<SpriteSizeMode>()) box.SizeMode = m;   // every mode, incl. back to None
    box.SizeMode = SpriteSizeMode.None;                        // ...and end on None (the walk above ends on the last enum value)
    box.Size = new Size(320, 240);                             // resize with the view machinery on (the strip does this on show)
    box.RenderOnce();
    Chk(box.SizeMode == SpriteSizeMode.None && box.Surface.Width == 320 && box.Surface.Height == 240, "SizeMode None: the surface is the client area again");
    box.SizeMode = SpriteSizeMode.Zoom;
    box.RenderOnce();
    Chk(box.Surface.Width == 1600 && box.Surface.Height == 1200, "back to Zoom: the surface is ImageSize again");
    Console.WriteLine("  sizemode box: all checks passed");
  }
  static void VoxelViews() {
    var c = new Sprite(1000, 700); c.ClearBuffer(unchecked((int)0xFF202428));
    // a small house grid like the bench one
    var g = new VoxelGrid(24, 24, 24);
    var wall = new Voxel(0xFFD8C8A0); var roof = new Voxel(0xFFA03828); var lamp = new Voxel(0xFFFFD070, 15, 9);
    g.FillBox(0, 0, 0, 24, 24, 1, new Voxel(0xFF4C9A3C));
    g.FillBox(4, 4, 1, 20, 20, 10, wall); g.FillBox(5, 5, 1, 19, 19, 10, Voxel.Empty);
    g.FillBox(11, 4, 1, 13, 5, 5, Voxel.Empty); g.FillBox(7, 4, 4, 10, 5, 7, Voxel.Empty);
    for (int i = 0; i < 6; i++) g.FillBox(4 + i, 4 + i, 10 + i, 20 - i, 20 - i, 11 + i, roof);
    g.Set(12, 12, 8, lamp); g.Update();
    // 1: free orbit, smooth lighting, depth fade, info + axes
    var b1 = new VoxelBox { Size = new Size(480, 340), ShowInfo = true, ShowAxes = true, Fade = VoxelFade.Depth, FadeMin = 0.5f, Zoom = 10 };
    b1.Grid = g; c.Draw(b1.RenderOnce(), 10, 10, SR2D.Op.Paint);
    Console.WriteLine($"voxelbox free: drawn {b1.LastDrawnVoxels} in {b1.LastFrameMs:0.0} ms cam {b1.Camera?.EffectiveMode}");
    // mouse: orbit drag (preview frame while down), then release -> full frame
    b1.Down(200, 200); b1.Move(260, 180); var pv = b1.RenderOnce(); Console.WriteLine($"  during drag: yaw {b1.Yaw:0.00} pitch {b1.Pitch:0.00} preview={b1.LastFrameMs:0.0}ms"); b1.Up(260, 180);
    var hit = b1.Pick(new Point(240, 170)); Console.WriteLine($"  pick centre: index {hit.index} face {hit.face} {(hit.index >= 0 ? g.Coords(hit.index).ToString() : "")}");
    // 2: isometric preset, night, points/cubes auto
    var b2 = new VoxelBox { Size = new Size(480, 340), View = VoxelCameraView.Isometric, Night = true, ShowInfo = true, Zoom = 8 };
    b2.Grid = g; c.Draw(b2.RenderOnce(), 510, 10, SR2D.Op.Paint);
    Console.WriteLine($"voxelbox iso night: drawn {b2.LastDrawnVoxels} in {b2.LastFrameMs:0.0} ms");
    // 3: the settings menu, opened on a fake screen and painted, plus a sub-menu
    var b3 = new VoxelBox { Size = new Size(480, 340), View = VoxelCameraView.ThreeQuarter, Zoom = 6, Lighting = VoxelLighting.Faces };
    b3.Grid = g; c.Draw(b3.RenderOnce(), 10, 360, SR2D.Op.Paint);
    var m = b3.Menu; m.Show(b3, new Point(20, 20));
    var panel = m.Panel!; Console.WriteLine($"  menu open={m.IsOpen} size {panel.Size} items {m.Items.Count} host {panel.Parent?.Bounds}");
    // hover the "Lighting" row and open it with the keyboard (Right)
    int li = m.Items.FindIndex(x => x.Text == "Lighting"); var rows = typeof(SpriteMenuPanel).GetField("_rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel) as System.Collections.Generic.List<Rectangle>;
    var lr = rows![li]; panel.Move(lr.X + 10, lr.Y + 4);
    panel.Key(Keys.Right);
    var sub = m.Items[li].Sub!; Console.WriteLine($"  sub open={sub.IsOpen} deepest={(m.Deepest() == sub.Panel)} hot row in sub after Right = ?");
    // toggle "Night" in the sub with Enter after moving to it by first letter
    sub.Panel!.Key(Keys.N); sub.Panel.Key(Keys.Enter); Console.WriteLine($"  night after N + Enter: {b3.Night} (menu still open: {sub.IsOpen})");
    // slider: press at 75 % of the Sky light row
    int si = sub.Items.FindIndex(x => x.Text == "Sky light"); var srows = typeof(SpriteMenuPanel).GetField("_rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sub.Panel) as System.Collections.Generic.List<Rectangle>;
    var sr = srows![si]; sub.Panel.Down(sub.Panel.Width * 3 / 4, sr.Y + sr.Height - 6); sub.Panel.Up(sub.Panel.Width * 3 / 4, sr.Y + sr.Height - 6);
    Console.WriteLine($"  sky light after slider press at 75 %: {b3.NightSkyLight} (night sky, expect ~11)");
    var mp = panel.RenderOnce(); c.Draw(mp, 10 + panel.Parent!.Left, 360 + panel.Parent.Top, SR2D.Op.Paint);
    var sp = sub.Panel.RenderOnce(); c.Draw(sp, 10 + sub.Panel.Parent!.Left, 360 + sub.Panel.Parent.Top, SR2D.Op.Paint);
    // outside click closes the chain through the message filter (it must not be swallowed)
    Cursor.Position = new Point(900, 900); var msg = new Message { Msg = 0x0201 }; bool swallowed = false; foreach (var f in Application.Filters.ToArray()) swallowed |= f.PreFilterMessage(ref msg);
    Console.WriteLine($"  outside click: swallowed={swallowed} open={m.IsOpen} sub open={sub.IsOpen} filters left={Application.Filters.Count}");
    // A RIGHT-click context menu must not eat the next left click on its opener: the click closes the menu and still
    // reaches the box underneath. (A left-click menu does swallow it, so its opener does not toggle open again.)
    b3.Location = new Point(900, 600); m.Show(b3, new Point(20, 20), MouseButtons.Right);
    Console.WriteLine($"  right menu opener {b3.Bounds} host {m.Panel!.Parent?.FindForm()?.Bounds}");
    Cursor.Position = new Point(905, 605);                       // on the box, clear of the menu chain
    var rmsg = new Message { Msg = 0x0201 }; bool rsw = false;
    foreach (var f in Application.Filters.ToArray()) rsw |= f.PreFilterMessage(ref rmsg);
    Chk(!rsw && !m.IsOpen, $"a right-click menu lets the next left click on its opener through (swallowed {rsw}, open {m.IsOpen})");
    m.Show(b3, new Point(20, 20));                              // the left-click default still swallows the opener click
    Cursor.Position = new Point(905, 605);
    var lmsg = new Message { Msg = 0x0201 }; bool lsw = false;
    foreach (var f in Application.Filters.ToArray()) lsw |= f.PreFilterMessage(ref lmsg);
    Chk(lsw && !m.IsOpen, $"a left-click menu swallows the click on its opener (swallowed {lsw}, open {m.IsOpen})");
    Save(c, "/home/user/.cache/ctlrun/voxelbox.rgba");
  }
  // ---- Sprite.Move / Sprite.Offset (selection-scoped move tool and Photoshop-style offset) ----------------
  // Every case is checked against an independent slow managed reference of the documented formula:
  // content = round(pixel * coverage / 255) travels, the source fades to round(pixel * (255 - coverage) / 255),
  // the destination receives round(dst * (255 - w) + content * 255 + 127) / 255 (all four premultiplied channels).
  static int RefPix(int c, int wgt) {
    int a = (c >> 24) & 255, r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
    a = (a * wgt + 127) / 255; r = (r * wgt + 127) / 255; g = (g * wgt + 127) / 255; b = (b * wgt + 127) / 255;
    return (a << 24) | (r << 16) | (g << 8) | b;
  }
  static int RefLane(int d, int c, int inv, int sh) { int v = (((d >> sh) & 255) * inv + ((c >> sh) & 255) * 255 + 127) / 255; return Math.Min(255, v) << sh; }
  static int RefMix(int dst0, int content, int inv) => RefLane(dst0, content, inv, 24) | RefLane(dst0, content, inv, 16) | RefLane(dst0, content, inv, 8) | RefLane(dst0, content, inv, 0);
  static int[] RefMove(int[] p0, byte[] cov, int w, int h, int dx, int dy, out int selected) {
    var p = (int[])p0.Clone(); var cc = new int[w * h]; selected = 0;
    for (int i = 0; i < w * h; i++) { int m = cov[i]; if (m == 0) continue; selected++; cc[i] = RefPix(p0[i], m); p[i] = RefPix(p0[i], 255 - m); }
    var o = (int[])p.Clone();
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      if (cov[y * w + x] == 0) continue;
      int tx = x + dx, ty = y + dy; if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
      o[ty * w + tx] = RefMix(p[ty * w + tx], cc[y * w + x], 255 - cov[y * w + x]);
    }
    return o;
  }
  static int[] RefOffset(int[] p0, byte[] cov, int w, int h, Func<int, int, (int, int)> route, out int written) {
    var cc = new int[w * h]; var ww = new byte[w * h];
    for (int i = 0; i < w * h; i++) { ww[i] = cov[i]; cc[i] = cov[i] == 0 ? 0 : RefPix(p0[i], cov[i]); }
    var o = (int[])p0.Clone(); written = 0;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      if (cov[y * w + x] == 0) continue;
      (int sx, int sy) = route(x, y); int s = sy * w + sx; if (ww[s] == 0) continue;
      o[y * w + x] = RefMix(p0[y * w + x], cc[s], 255 - ww[s]); written++;
    }
    return o;
  }
  static string FirstDiff(Sprite s, int[] want) {
    for (int y = 0; y < s.Height; y++) for (int x = 0; x < s.Width; x++)
        if (s.Pixels[y * s.Width + x] != want[y * s.Width + x])
          return $"first diff at {x},{y}: got {unchecked((uint)s.Pixels[y * s.Width + x]):X8} want {unchecked((uint)want[y * s.Width + x]):X8}";
    return "none";
  }
  static Sprite PatSprite(int w, int h) {
    var s = new Sprite(w, h);
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      uint v = (uint)(x * 37 + y * 91 + 11);
      s.Pixels[y * w + x] = unchecked((int)((((v * 5) % 256) << 24) | (((v * 3) % 256) << 16) | (((v * 7) % 256) << 8) | ((v * 13) % 256)));
    }
    return s;
  }
  static void SelectionMoveOps() {
    const int W = 16, H = 12;
    Console.WriteLine("Sprite.Move / Sprite.Offset (selection-scoped, feathered):");
    byte[] CovRect(int x0, int y0, int cw, int ch) { var c = new byte[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) c[y * W + x] = x >= x0 && x < x0 + cw && y >= y0 && y < y0 + ch ? (byte)255 : (byte)0; return c; }
    int[] P0; var sel = new Selection(W, H);

    // Move: hard rectangle, no overlap / overlap / pushed past the edge
    foreach (var (dx, dy) in new[] { (5, 3), (1, 0), (13, 0), (0, -4), (-15, -11) }) {
      P0 = new int[W * H]; using (var s = PatSprite(W, H)) { for (int i = 0; i < W * H; i++) P0[i] = s.Pixels[i];
        sel.Rect(3, 2, 6, 5);
        int n = s.Move(sel, dx, dy);
        var want = RefMove(P0, CovRect(3, 2, 6, 5), W, H, dx, dy, out int selN);
        bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != want[i]) same = false;
        Chk(same && n == selN, $"move: 6x5 rect by ({dx},{dy}) exact vs reference ({FirstDiff(s, want)}), {n} px reported");
      }
    }
    // Move: feathered coverage (the coverage travels as soft alpha)
    P0 = new int[W * H]; using (var s = PatSprite(W, H)) {
      for (int i = 0; i < W * H; i++) P0[i] = s.Pixels[i];
      var cov = new byte[W * H];
      for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int d = Math.Abs(x - 5) + Math.Abs(y - 4); cov[y * W + x] = d > 6 ? (byte)0 : (byte)(255 - d * 40); }
      for (int i = 0; i < W * H; i++) sel.Pixels[i] = cov[i];
      sel.Invalidate();                                                     // direct Pixels writes: bounds + version must be rebuilt
      int n = s.Move(sel, 2, -1);
      var want = RefMove(P0, cov, W, H, 2, -1, out int selN);
      bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != want[i]) same = false;
      int kept = 0; for (int i = 0; i < W * H; i++) if (cov[i] == 255 && P0[i] == s.Pixels[i + 2 - 1 * W]) kept++;   // opaque centre lands 2 right, 1 up
      Chk(same && n == selN, $"move: feathered diamond coverage exact vs reference ({FirstDiff(s, want)}), {n} px reported");
      Chk(kept > 0, "move: opaque centre pixel travels 2 right / 1 up unchanged");
    }
    // Move: no-ops and guards
    using (var s = PatSprite(W, H)) {
      int[] before = new int[W * H]; for (int i = 0; i < W * H; i++) before[i] = s.Pixels[i];
      sel.Rect(3, 2, 6, 5);
      int z = s.Move(sel, 0, 0);
      sel.Clear();
      int e = s.Move(sel, 3, 3);
      bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != before[i]) same = false;
      Chk(z == 0 && e == 0 && same, "move: zero offset and empty selection are no-ops");
      var b0 = sel.Bounds; sel.Rect(1, 1, 2, 2); var b1 = sel.Bounds;
      Chk(b1.Width == 2 && b1.Height == 2 && b0.Width == 0, "move: the marquee itself never moves");
      sel.Rect(1, 1, 2, 2);
      bool threw = false; try { using var small = new Sprite(4, 4); using var ssel = new Selection(16, 12); small.Move(ssel, 1, 0); } catch (ArgumentException) { threw = true; }
      Chk(threw, "move: a foreign-size selection throws");
    }

    // Offset: a full-width row strip shifted along itself (wrap = its bounding box = the strip)
    P0 = new int[W * H]; using (var s = PatSprite(W, H)) {
      for (int i = 0; i < W * H; i++) P0[i] = s.Pixels[i];
      sel.Rect(0, 4, W, 4);
      int n = s.Offset(sel, 5, 0, SelOffsetWrap.BoundingBox);
      var want = RefOffset(P0, CovRect(0, 4, W, 4), W, H, (x, y) => ((((x - 5) % W) + W) % W, y), out _);
      bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != want[i]) same = false;
      Chk(same && n == W * 4, $"offset: 16x4 row strip +5 along itself exact vs reference ({FirstDiff(s, want)}), {n} px written");
      // total content preserved: the strip is a permutation
      var a0 = new List<int>(); var a1 = new List<int>();
      for (int y = 4; y < 8; y++) for (int x = 0; x < W; x++) { a0.Add(P0[y * W + x]); a1.Add(s.Pixels[y * W + x]); }
      a0.Sort(); a1.Sort(); bool perm = a0.SequenceEqual(a1);
      Chk(perm, "offset: the strip keeps its content (a circular shift, nothing lost)");
      // rows outside the strip untouched
      bool outSame = true; for (int y = 0; y < H; y++) if (y < 4 || y >= 8) for (int x = 0; x < W; x++) if (s.Pixels[y * W + x] != P0[y * W + x]) outSame = false;
      Chk(outSame, "offset: pixels outside the selection are untouched");
    }
    // Offset: diagonal shift of the whole sprite (bbox route over SelectAll)
    P0 = new int[W * H]; using (var s = PatSprite(W, H)) using (var all = new Selection(W, H)) {
      for (int i = 0; i < W * H; i++) P0[i] = s.Pixels[i];
      all.SelectAll();
      int n = s.Offset(all, 3, -2, SelOffsetWrap.BoundingBox);
      var want = RefOffset(P0, CovRect(0, 0, W, H), W, H, (x, y) => ((((x - 3) % W) + W) % W, (((y + 2) % H) + H) % H), out _);
      bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != want[i]) same = false;
      Chk(same && n == W * H, $"offset: SelectAll by (3,-2) exact vs reference ({FirstDiff(s, want)}), {n} px written");
      bool perm = true; { var a0 = new List<int>(); var a1 = new List<int>(); for (int i = 0; i < W * H; i++) { a0.Add(P0[i]); a1.Add(s.Pixels[i]); } a0.Sort(); a1.Sort(); perm = a0.SequenceEqual(a1); }
      Chk(perm, "offset: a full-sprite offset only permutes the pixels");
    }
    // Offset: two column blocks with a gap, NOT at the origin - BoundingBox carries content across the gap,
    // Spans wraps inside each block; the off-origin bounds also pin the SpanTable line indexing
    P0 = new int[W * H]; using (var s1 = PatSprite(W, H)) using (var s2 = PatSprite(W, H)) using (var two = new Selection(W, H)) {
      for (int i = 0; i < W * H; i++) P0[i] = s1.Pixels[i];
      two.Rect(2, 3, 4, 6); two.Rect(10, 3, 4, 6, SelectMode.Add);
      var cov = new byte[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) cov[y * W + x] = ((x >= 2 && x < 6) || (x >= 10 && x < 14)) && y >= 3 && y < 9 ? (byte)255 : (byte)0;
      int nB = s1.Offset(two, 6, 0, SelOffsetWrap.BoundingBox);      // 6 = NOT a multiple of the 4-px block: a real (non-identity) wrap
      int nS = s2.Offset(two, 6, 0, SelOffsetWrap.Spans);
      // spans reference: x wraps within its own 4-px block run of that row
      var wantS = RefOffset(P0, cov, W, H, (x, y) => (x >= 2 && x < 6) ? (2 + ((x - 2 - 6) % 4 + 4) % 4, y) : (10 + ((x - 10 - 6) % 4 + 4) % 4, y), out _);
      bool sameS = true; for (int i = 0; i < W * H; i++) if (s2.Pixels[i] != wantS[i]) sameS = false;
      Chk(sameS && nS == 8 * 6, $"offset: Spans wraps each 4-px block inside itself ({FirstDiff(s2, wantS)}), {nS} px written");
      // bbox: a pixel of block A may land on block B (content crosses the gap) -> different picture than Spans
      bool crossed = false; for (int y = 3; y < 9; y++) if (s1.Pixels[y * W + 2] != s2.Pixels[y * W + 2]) crossed = true;
      Chk(crossed, "offset: BoundingBox routes across the gap, Spans never does");
      // bbox reference: wrap over the union bounds 2..13; block B's sources wrap onto the unselected gap -> masked (4 per row)
      var wantB = RefOffset(P0, cov, W, H, (x, y) => (x >= 2 && x < 14) ? (2 + ((x - 2 - 6) % 12 + 12) % 12, y) : (x, y), out _);
      bool sameB = true; for (int i = 0; i < W * H; i++) if (s1.Pixels[i] != wantB[i]) sameB = false;
      Chk(sameB && nB == 4 * 6, $"offset: BoundingBox over the union bounds exact vs reference ({FirstDiff(s1, wantB)}), {nB} px written");
      // vertical: the same blocks shifted DOWN - exercises the per-column span table (route within the column run)
      using (var s3 = PatSprite(W, H)) using (var s4 = PatSprite(W, H)) {
        int nBv = s3.Offset(two, 0, 4, SelOffsetWrap.BoundingBox);
        int nSv = s4.Offset(two, 0, 4, SelOffsetWrap.Spans);
        var wantV = RefOffset(P0, cov, W, H, (x, y) => (x, y >= 3 && y < 9 ? 3 + ((y - 3 - 4) % 6 + 6) % 6 : y), out _);
        bool sameV = true; for (int i = 0; i < W * H; i++) if (s3.Pixels[i] != wantV[i]) sameV = false;
        bool sameVS = true; for (int i = 0; i < W * H; i++) if (s4.Pixels[i] != wantV[i]) sameVS = false;
        Chk(sameV && nBv == 48 && sameVS && nSv == 48, $"offset: vertical shift wraps within the column runs, BoundingBox = Spans here ({FirstDiff(s3, wantV)}), {nBv}/{nSv} px written");
      }
    }
    // Offset: feathered coverage on both ends
    P0 = new int[W * H]; using (var s = PatSprite(W, H)) {
      for (int i = 0; i < W * H; i++) P0[i] = s.Pixels[i];
      var cov = new byte[W * H];
      for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int d = Math.Abs(x - 8) + Math.Abs(y - 6); cov[y * W + x] = d > 5 ? (byte)0 : (byte)(255 - d * 50); }
      for (int i = 0; i < W * H; i++) sel.Pixels[i] = cov[i];
      sel.Invalidate();
      int n = s.Offset(sel, 2, 0, SelOffsetWrap.BoundingBox);
      var want = RefOffset(P0, cov, W, H, (x, y) => (x >= 3 && x < 14 && y >= 1 && y < 12) ? (3 + ((x - 3 - 2) % 11 + 11) % 11, y) : (x, y), out _);
      bool same = true; for (int i = 0; i < W * H; i++) if (s.Pixels[i] != want[i]) same = false;
      Chk(same, $"offset: feathered coverage exact vs reference ({FirstDiff(s, want)}), {n} px written");
      sel.Rect(0, 0, W, H);
      int z = s.Offset(sel, 0, 0);
      Chk(z == 0, "offset: zero offset is a no-op");
    }
    Console.WriteLine("  (move/offset: exact vs reference, feathered, bbox + spans wrap, clipping, no-ops)");
  }
  // ---- discrete value mode (Values / PowersOfTwo on the range controls) -------------------------------------
  static void DiscreteValues() {
    Console.WriteLine("Discrete values (Values / PowersOfTwo on SpriteSlider / SpriteKnob / SpriteNumeric):");
    var s = new SpriteSlider { Minimum = 8, Maximum = 256, PowersOfTwo = true };
    Chk(s.Value == 8, $"pow2 slider: the default value clamps onto the first entry ({s.Value})");
    s.Value = 100; Chk(s.Value == 128, $"pow2 slider: 100 snaps to the nearest entry 128 ({s.Value})");
    s.Value = 9; Chk(s.Value == 8, $"pow2 slider: 9 snaps back to 8 ({s.Value})");
    s.Value = 255; Chk(s.Value == 256, $"pow2 slider: 255 snaps up to 256 ({s.Value})");
    s.Minimum = 0;
    Chk(s.Values is double[] list && list[0] == 1 && list[^1] == 256 && list.Length == 9, $"pow2 slider: the list follows a range change (now {string.Join(" ", s.Values!)})");
    var k = new SpriteKnob { Minimum = 0, Maximum = 3, Values = new double[] { 3, 0, 1.5, 0, 3 } };
    Chk(k.Values!.Length == 3 && Math.Abs(k.Values[1] - 1.5) < 1e-9, "knob: a value list is sorted and de-duplicated (0 1.5 3)");
    k.Value = 2; Chk(Math.Abs(k.Value - 1.5) < 1e-9, $"knob: 2 snaps to 1.5 ({k.Value})");
    var n = new SpriteNumeric { Minimum = 8, Maximum = 256, PowersOfTwo = true, Value = 64 };
    n.Nudge(1); Chk(n.Value == 128, $"numeric: Nudge goes to the NEXT entry (64 -> 128, never stuck at 65) ({n.Value})");
    n.Nudge(1); Chk(n.Value == 256, $"numeric: Nudge again -> 256 ({n.Value})");
    n.Nudge(1); Chk(n.Value == 256, $"numeric: Nudge past the last entry stays there ({n.Value})");
    n.Nudge(-1); Chk(n.Value == 128, $"numeric: Nudge(-1) steps back to 128 ({n.Value})");
    var st = new SpriteSlider { Minimum = 0, Maximum = 12, Snap = true, Step = 2, Value = 41 };
    Chk(st.Value == 12, $"step snap unchanged: 41 -> 12 on 0..12 step 2 ({st.Value})");
    st.Value = 7; Chk(st.Value == 8, $"step snap: 7 -> 8 ({st.Value})");
    Console.WriteLine("  (discrete: nearest-entry snapping on every input, wheel / arrows / buttons step the list, pow2 mode)");
  }

  // ---- keyframe tracks / tangents (cs/Animation.cs) + the discrete notch rendering ----------------------------
  static void AnimationTangents() {
    Console.WriteLine("Animation tangents (Track / TangentMode):");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Linear, 0.25) - 0.25) < 1e-12, $"linear: u ({Track.EaseAt(TangentMode.Linear, 0.25)})");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Step, 0.99)) < 1e-12, "step: holds 0 through the whole segment");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Step, 1) - 1) < 1e-12, "step: jumps to the key at the segment end (no stuck animation)");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Smooth, 0.25) - 0.15625) < 1e-12, $"smooth: 0.25 -> {Track.EaseAt(TangentMode.Smooth, 0.25)} (smoothstep)");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Smooth, 0.5) - 0.5) < 1e-12, "smooth: symmetric at the middle");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Fast, 0.25) - 0.4375) < 1e-12, $"fast: 0.25 -> {Track.EaseAt(TangentMode.Fast, 0.25)} (fast start)");
    Chk(Math.Abs(Track.EaseAt(TangentMode.Slow, 0.25) - 0.0625) < 1e-12, $"slow: 0.25 -> {Track.EaseAt(TangentMode.Slow, 0.25)} (slow start)");
    { var a = new Keyframe(0, 0) { Out = TangentMode.Spline, HandleOut = 1 }; var b = new Keyframe(1, 1) { In = TangentMode.Spline, HandleIn = 0 };
      Chk(Math.Abs(Track.EaseSegment(a, b, 0.5) - 0.625) < 1e-12, $"spline handles (1,0): 0.5 -> {Track.EaseSegment(a, b, 0.5):0.###} (Hermite)"); }
    { var t3 = new Track();
      t3.Add(new Keyframe(0, 0) { Out = TangentMode.Auto });
      t3.Add(new Keyframe(1, 1) { Out = TangentMode.Auto });
      t3.Add(new Keyframe(2, 0) { Out = TangentMode.Auto });
      Chk(Math.Abs(t3.Evaluate(-1) - 0) < 1e-12 && Math.Abs(t3.Evaluate(5) - 0) < 1e-12, "track: holds the end keys outside the range");
      Chk(Math.Abs(t3.Evaluate(0.5) - 0.625) < 1e-12, $"track: auto slopes, segment 1 at 0.5 -> {t3.Evaluate(0.5):0.###} (Catmull-Rom, slopes 1 and 0)");
      Chk(Math.Abs(t3.Evaluate(1.5) - 0.375) < 1e-12, $"track: auto slopes, segment 2 at 1.5 -> {t3.Evaluate(1.5):0.###} (slopes 0 and -1)");
      Chk(Math.Abs(t3.Evaluate(1.0) - 1.0) < 1e-12, "track: passes exactly through the keys"); }
    { var ts = new Track(); var mid = ts.Add(1, 10, TangentMode.Step); ts.Add(0, 0); ts.Add(2, 20); ts.Sort();
      Chk(ts.Count == 3 && Math.Abs(ts.Evaluate(1.5) - 10) < 1e-12, $"track: unsorted Add + Sort, a Step key holds its value ({ts.Evaluate(1.5)})");
      mid.Out = TangentMode.Linear; ts.Evaluate(0.5);
      Chk(Math.Abs(ts.Evaluate(0.5) - 5) < 1e-12, $"track: retangenting a key applies at once ({ts.Evaluate(0.5)})"); }
    { var sl = new SpriteSlider { Minimum = 0, Maximum = 100, Values = new double[] { 0, 25, 50, 75, 100 }, ShowNotches = true, NotchLabels = true, Size = new Size(220, 40) };
      sl.Value = 61; Chk(sl.Value == 50, $"discrete slider renders + snaps: 61 -> {sl.Value} (even spacing: index of 50 is drawn at the middle)");
      var pic = sl.RenderOnce();
      int ink = 0; foreach (var v in pic.Pixels) if ((v >> 24 & 255) > 100) ink++;
      Chk(ink > 500, $"discrete slider: notches + labels painted ({ink} px)");
      var kn = new SpriteKnob { Minimum = 0, Maximum = 100, Values = new double[] { 0, 10, 25, 50, 75, 100 }, ShowNotches = true, NotchLabels = true, Size = new Size(130, 128) };
      kn.Value = 61; Chk(kn.Value == 50, $"discrete knob snaps the same way ({kn.Value})");
      var kp = kn.RenderOnce();
      int ink2 = 0; foreach (var v in kp.Pixels) if ((v >> 24 & 255) > 100) ink2++;
      Chk(ink2 > 500, $"discrete knob: ticks + numbers painted ({ink2} px)"); }
    { var curve = new Curve();
      Chk(Math.Abs(curve.Evaluate(0)) < 1e-12 && Math.Abs(curve.Evaluate(1) - 1) < 1e-12, "curve: the default is the identity (0 -> 0, 1 -> 1)");
      Chk(Math.Abs(curve.Evaluate(0.25) - 0.25) < 1e-12 && Math.Abs(curve.Evaluate(0.75) - 0.75) < 1e-12, "curve: linear along the diagonal");
      curve.Add(0.5f, 0.1f);
      Chk(Math.Abs(curve.Evaluate(0.5) - 0.1) < 1e-6, $"curve: passes exactly through the points ({curve.Evaluate(0.5):0.####})");   // points are float, so ~1e-7 rounding
      bool mono = true;
      double last = -1;
      for (double u = 0; u <= 1; u += 0.01) { double v = curve.Evaluate(u); if (v < last - 1e-9) mono = false; last = v; }
      Chk(mono, "curve: monotone (a dip never overshoots back up - safe as a speed profile / Levels ramp)");
      curve.Set(new[] { new System.Drawing.PointF(0f, 0f), new System.Drawing.PointF(0.3f, 0.8f), new System.Drawing.PointF(1f, 1f) });
      Chk(Math.Abs(curve.Evaluate(0.3) - 0.8) < 1e-6 && curve.Points.Count == 3, "curve: Set replaces the points (sorted, endpoints pinned)");
      int before = curve.Points.Count;
      int merged = curve.Add(0.31f, 0.5f);
      Chk(curve.Points.Count == before && merged >= 0 && Math.Abs(curve.Evaluate(0.3) - 0.5) < 1e-6, "curve: an add nearer than MinGap moves the existing point (no floating duplicates)");
      curve.Clamp01 = false;
      int k = curve.Add(0.6f, 1.4f);
      Chk(curve.Evaluate(0.6) > 1.0, $"curve: overshoot mode - a point above 1 evaluates above 1 ({curve.Evaluate(0.6):0.###})");
      curve.CompressTo01();
      Chk(curve.Evaluate(0.6) <= 1.0 && curve.Clamp01 == false, "curve: CompressTo01 rescales the shape into the range");
      curve.Clamp01 = true; curve.ClampTo01();
      Chk(curve.Evaluate(0.6) <= 1.0, "curve: ClampTo01 pins everything back into 0..1");
      { var cm = new Curve();
        double plainMid = cm.Evaluate(0.25);
        cm.SetMode(0, CurvePointMode.Flat);
        Chk(cm.Evaluate(0.25) < plainMid, $"curve: a Flat start eases in ({cm.Evaluate(0.25):0.###} < {plainMid:0.###})");
        cm.SetMode(0, CurvePointMode.Auto); cm.SetMode(1, CurvePointMode.Linear);
        Chk(Math.Abs(cm.Evaluate(0.5) - 0.5) < 1e-9, $"curve: a Linear end runs straight ({cm.Evaluate(0.5):0.###})");
        cm.SetMode(1, CurvePointMode.Auto); }
      { var c2 = new Curve(); c2.Add(0.4f, 0.75f);
        string code = c2.ToCode();
        Chk(code.Contains("new(0.4f, 0.75f)") && code.Contains("curve.Set(new System.Drawing.PointF[]"), "curve: ToCode emits a paste-able Set call on a curve local");
        Chk(SpriteCurveEditor.TryParsePoints(code, out var back) && back.Length == 3 && Math.Abs(back[1].X - 0.4f) < 1e-5 && Math.Abs(back[1].Y - 0.75f) < 1e-5, "curve: TryParsePoints round-trips the copied code"); }
    {   // round ten: the tangent modes survive a copy, a lone point is safe, the editor answers the model and the mouse
      var c3 = new Curve(); c3.Add(0.5f, 0.2f); c3.SetMode(1, CurvePointMode.Flat);
      var copy = new Curve(); copy.Set(c3.Points, c3.Modes);
      Chk(copy.Points.Count == 3 && copy.ModeAt(1) == CurvePointMode.Flat, "curve: Set(points, modes) carries the tangent types (Set(points) alone made every point Auto)");
      Chk(c3.ToCode().Contains("curve.SetMode(1, CurvePointMode.Flat)"), "curve: ToCode writes the modes too");
      Chk(SpriteCurveEditor.TryParsePoints(c3.ToCode(), out var rt) && rt.Length == 3, "curve: the mode lines do not confuse the paste parser");
      var one = new Curve(); one.Set(new[] { new System.Drawing.PointF(0.4f, 0.3f) });
      Chk(one.Points.Count == 2 && Math.Abs(one.Evaluate(0.7) - 0.3) < 1e-6, $"curve: a lone point becomes the flat line through it ({one.Points.Count} points, Evaluate(0.7) = {one.Evaluate(0.7):0.###}) - it used to index a missing point");
      var ed2 = new SpriteCurveEditor { Size = new System.Drawing.Size(300, 170) };
      ed2.Curve.Set(new[] { new System.Drawing.PointF(0, 0), new System.Drawing.PointF(0.5f, 0.25f), new System.Drawing.PointF(1, 1) });
      int[] Snap() { var s = ed2.RenderOnce(); var a = new int[s.Width * s.Height]; s.Pixels.CopyTo(a); return a; }
      void Down2(int x, int y, int clicks) => typeof(Control).GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(ed2, new object[] { new MouseEventArgs(MouseButtons.Left, clicks, x, y, 0) });
      // the middle point of this plot sits at x = 16 + 0.5 * 275 = 153.5, y = 161 - 0.25 * 153 = 122.75
      Down2(154, 123, 1); ed2.Up(154, 123);
      Chk(ed2.Curve.ModeAt(1) == CurvePointMode.Auto, "curve editor: a single click on a point does not change its type");
      Down2(154, 123, 2);
      Chk(ed2.Curve.ModeAt(1) == CurvePointMode.Flat, "curve editor: double click a point = the next tangent type (Auto -> Flat)");
      Down2(154, 123, 2);
      Chk(ed2.Curve.ModeAt(1) == CurvePointMode.Linear, "curve editor: ... -> Linear");
      Down2(154, 123, 2);
      Chk(ed2.Curve.ModeAt(1) == CurvePointMode.Auto, "curve editor: ... -> Auto, the cycle closes");
      int n0 = ed2.Curve.Points.Count;
      Down2(166, 123, 1);                                                   // 12 px sideways: the old normalised radius reached ~13 px and swallowed the click
      Chk(ed2.Curve.Points.Count == n0 + 1, $"curve editor: a click 12 px sideways ADDS a point ({ed2.Curve.Points.Count}) - the grab area is 8 screen px in every direction");
      var snapA = Snap();
      ed2.Curve.Add(0.8f, 0.9f);                                           // an edit made on the Curve object, not through the mouse
      var snapB = Snap();
      int changed = 0; for (int i = 0; i < snapA.Length; i++) if (snapA[i] != snapB[i]) changed++;
      Chk(changed > 100, $"curve editor: an edit on the Curve object repaints it ({changed} px changed)");
      var snapC = Snap();
      ed2.Guides = false;
      var snapD = Snap();
      int gd = 0; for (int i = 0; i < snapC.Length; i++) if (snapC[i] != snapD[i]) gd++;
      Chk(gd > 100, $"curve editor: the Guides property repaints when toggled ({gd} px changed)");
      ed2.Dispose(); }
      var ed = new SpriteCurveEditor { Size = new System.Drawing.Size(300, 170) };
      var pic = ed.RenderOnce();
      int ink = 0; foreach (var v in pic.Pixels) if ((v >> 24 & 255) > 100) ink++;
      Chk(ink > 300, $"curve editor renders the plot + points + guides ({ink} px)"); }
    {   // round ten: the help overlay and the caption band have to fit the control they are drawn on.
      // The old overlay was 17 lines at 13 px (5x7 glyphs + 2) = 221 px in the 134 px plot a 320x150 editor
      // gives it (cs_backup_213754/SpriteControls.Curve.cs), so it ran over the frame and off the bottom.
      var eh = new SpriteCurveEditor { Size = new System.Drawing.Size(320, 150) };
      typeof(SpriteCurveEditor).GetField("showHelp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(eh, true);
      var hp = eh.RenderOnce();
      int back = hp.Pixels[0], cut = 0;
      for (int x = 0; x < hp.Width; x++) if (hp.Pixels[(hp.Height - 1) * hp.Width + x] != back) cut++;
      Chk(cut == 0, $"curve editor: the help overlay stays inside the control (ink on the bottom row: {cut} px)");
      // a box too short for the whole list scrolls it instead of cutting it: the wheel must move the overlay
      var et = new SpriteCurveEditor { Size = new System.Drawing.Size(320, 60) };
      typeof(SpriteCurveEditor).GetField("showHelp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(et, true);
      int scroll0 = (int)typeof(SpriteCurveEditor).GetField("helpScroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(et)!;
      typeof(Control).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(et, new object[] { new MouseEventArgs(MouseButtons.None, 0, 160, 30, -120) });
      int scroll1 = (int)typeof(SpriteCurveEditor).GetField("helpScroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(et)!;
      Chk(scroll0 == 0 && scroll1 > 0, $"curve editor: the wheel scrolls the help when the plot cannot show it all ({scroll0} -> {scroll1})");
      var tp = et.RenderOnce();
      int tcut = 0; for (int x = 0; x < tp.Width; x++) if (tp.Pixels[(tp.Height - 1) * tp.Width + x] != tp.Pixels[0]) tcut++;
      Chk(tcut == 0, $"curve editor: a plot too short for the help clips it at the frame instead of running off the control (bottom row ink: {tcut})");
      eh.Dispose(); et.Dispose();
      var ec = new SpriteCurveEditor { Size = new System.Drawing.Size(300, 170), Text = "Trail curve" };
      var BFC = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
      var plot = (System.Drawing.Rectangle)typeof(SpriteCurveEditor).GetProperty("Plot", BFC)!.GetValue(ec)!;
      int capH = (int)typeof(SpriteCurveEditor).GetProperty("CapH", BFC)!.GetValue(ec)!;
      Chk(capH > 0 && plot.Bottom + capH <= ec.ClientSize.Height, $"curve editor: a caption keeps its band clear of the plot (plot bottom {plot.Bottom} + {capH} px caption in {ec.ClientSize.Height} px) - the y = 0 endpoints live on that edge");
      ec.Dispose(); }
    {   // round ten: the editor's menu opens on a RIGHT click, so it must not eat the next left click on the plot
      // (the Show overload defaults to Left, which tells the outside-click filter to swallow the opener's click).
      var em = new SpriteCurveEditor { Size = new System.Drawing.Size(320, 150) };
      em.Location = new System.Drawing.Point(900, 600);
      typeof(Control).GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
          .Invoke(em, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 250, 20, 0) });      // empty plot: the menu
      var cm = (SpriteMenu?)typeof(SpriteCurveEditor).GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(em);
      Chk(cm is { IsOpen: true }, "curve editor: right click on an empty spot opens the menu");
      Cursor.Position = new System.Drawing.Point(905, 605);                     // on the editor, clear of the menu
      var cmsg = new Message { Msg = 0x0201 }; bool csw = false;
      foreach (var f in Application.Filters.ToArray()) csw |= f.PreFilterMessage(ref cmsg);
      bool stillOpen = cm?.IsOpen ?? true;
      Chk(!csw && !stillOpen, $"curve editor: the next left click on the plot reaches it (swallowed {csw}, open {stillOpen})");
      em.Dispose(); }
    {   // round eleven: right click on a NODE selects it and opens the menu - it must not delete on the press,
      // or the menu's node-type entries can never be reached for a point that is gone before its own menu opens.
      var BFR = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
      var er = new SpriteCurveEditor { Size = new System.Drawing.Size(300, 170) };
      er.Curve.Set(new[] { new System.Drawing.PointF(0, 0), new System.Drawing.PointF(0.5f, 0.25f), new System.Drawing.PointF(1, 1) });
      int selOf() => (int)typeof(SpriteCurveEditor).GetField("selected", BFR)!.GetValue(er)!;
      // The node's own screen pixel, asked of the control rather than derived from the plot rectangle by hand:
      // the layout (caption band, DPI-scaled client size) is exactly what a hand-computed point can get wrong.
      var toPlot = typeof(SpriteCurveEditor).GetMethod("ToPlot", BFR)!;
      PointF nodePx(int i) => (PointF)toPlot.Invoke(er, new object[] { er.Curve.Points[i] })!;
      void RightClick(PointF p) => typeof(Control).GetMethod("OnMouseDown", BFR)!.Invoke(er, new object[] { new MouseEventArgs(MouseButtons.Right, 1, (int)MathF.Round(p.X), (int)MathF.Round(p.Y), 0) });
      SpriteMenu? OpenMenu() { var m = (SpriteMenu?)typeof(SpriteCurveEditor).GetField("_menu", BFR)!.GetValue(er); if (m is { IsOpen: true }) m.Close(); return m; }
      var mid = nodePx(1);
      RightClick(mid);
      var rmn = (SpriteMenu?)typeof(SpriteCurveEditor).GetField("_menu", BFR)!.GetValue(er);
      Chk(er.Curve.Points.Count == 3, $"curve editor: right click on a point leaves it in the curve ({er.Curve.Points.Count} points) - the press used to delete it");
      Chk(rmn is { IsOpen: true }, "curve editor: ... and opens the menu");
      Chk(selOf() == 1, $"curve editor: the point under the cursor is the selected one (selected = {selOf()}, node pixel {mid.X:0.#},{mid.Y:0.#})");
      var del = rmn!.Items.Find(i => i.Text == "Delete node");
      Chk(del is { Click: not null }, "curve editor: the menu carries Delete node");
      Chk(del!.Enabled, "curve editor: Delete node is live while a middle point is selected");
      del.Click!();
      Chk(er.Curve.Points.Count == 2 && selOf() < 0, $"curve editor: Delete node removes the selected point ({er.Curve.Points.Count} left, selected = {selOf()})");
      // the two endpoints are not removable: the entry greys out and the click does nothing
      OpenMenu();
      RightClick(nodePx(1));      // the (1,1) endpoint, now the last point
      rmn = (SpriteMenu?)typeof(SpriteCurveEditor).GetField("_menu", BFR)!.GetValue(er);
      Chk(selOf() == 1 && er.Curve.Points.Count == 2, $"curve editor: right click on an endpoint selects it without deleting it (selected = {selOf()}, {er.Curve.Points.Count} points left)");
      del = rmn!.Items.Find(i => i.Text == "Delete node");
      del!.Click!();
      Chk(!del.Enabled && er.Curve.Points.Count == 2, $"curve editor: Delete node is greyed for an endpoint and removes nothing ({er.Curve.Points.Count} left)");
      er.Dispose(); }
    Console.WriteLine("  (tangents: exact smoothstep / Hermite / Catmull-Rom values, tracks hold and retangent, notches paint, curves stay monotone)");
  }

  static void DiffuseEdges() {
    // Diffuse border contract: a pick that leaves the picture folds back onto the outermost real pixel
    // (reflect-101) - no wrap-around, and the transparent work ring around the picture is never picked.
    // The old kernel clamped: border pixels could pick themselves (bright edges stuck / circulated along
    // the border) and picked the ring (black holes crept into opaque pictures).
    int[] init = { 211,2,19,19,19,2,19,2, 211,19,63,63,2,63,19,19, 19,19,19,63,120,63,63,19, 63,120,177,120,177,177,120,63,
                   120,177,120,177,221,177,221,221, 177,221,221,177,244,221,177,211, 211,221,244,253,253,244,244,221, 211,253,211,244,244,244,244,253 };
    var vals = new System.Collections.Generic.HashSet<int>(init);
    bool bleed = false, foreign = false;
    for (int seed = 1; seed <= 64; seed++) {
      var s = new Sprite(8, 8); for (int i = 0; i < 64; i++) s.Pixels[i] = unchecked((int)(0xFF000000u | (uint)init[i]));
      var e = new Effects(); e.Diffuse(1, 1, seed); s.DrawFx(s, 0, 0, e);
      for (int i = 0; i < 64; i++) { if ((uint)s.Pixels[i] >> 24 != 255) bleed = true; if (!vals.Contains((int)((uint)s.Pixels[i] & 0xFF))) foreign = true; }
    }
    Chk(!bleed, "diffuse: no transparent-ring bleed at the borders (64 seeds, the 8x8 gradient)");
    Chk(!foreign, "diffuse: output values are always relocated input values");
    bool wrap = false;
    for (int seed = 1; seed <= 64; seed++) {
      var s = new Sprite(8, 8); s.ClearBuffer(unchecked((int)0xFF000000));
      for (int x = 0; x < 8; x++) s.Pixels[7 * 8 + x] = unchecked((int)0xFFFFFFFFu);            // bottom row white
      var e = new Effects(); e.Diffuse(1, 1, seed); s.DrawFx(s, 0, 0, e);
      for (int i = 0; i < 16; i++) if ((uint)s.Pixels[i] == 0xFFFFFFFFu) wrap = true;           // rows 0-1 stay dark
      var s2 = new Sprite(8, 8); s2.ClearBuffer(unchecked((int)0xFF000000));
      for (int y = 0; y < 8; y++) s2.Pixels[y * 8 + 7] = unchecked((int)0xFFFFFFFFu);           // right column white
      var e2 = new Effects(); e2.Diffuse(1, 1, seed); s2.DrawFx(s2, 0, 0, e2);
      for (int y = 0; y < 8; y++) { if ((uint)s2.Pixels[y * 8] == 0xFFFFFFFFu) wrap = true; if ((uint)s2.Pixels[y * 8 + 1] == 0xFFFFFFFFu) wrap = true; }
    }
    Chk(!wrap, "diffuse: no wrap-around - a stripe at one edge never reaches the opposite edge");
    {   // motion blur: the trail averages the taps (native DRAW_FX, this runner has the DLL)
      var src = new Sprite(12, 12); src.FillRect(6, 4, 4, 4, unchecked((int)0xFFFFFFFF));
      var d1 = new Sprite(20, 6); d1.ClearBuffer(0);
      d1.DrawFx(src, 2, 0, new Effects().MotionBlur(0, 2, 3, SR2D.Filter.Nearest), SR2D.Op.Paint);   // taps at src x, x-1, x-2
      bool head = d1.GetPixel(10, 5) == unchecked((int)0xFFFFFFFF) && d1.GetPixel(11, 5) == unchecked((int)0xFFFFFFFF);
      bool ramp = d1.GetPixel(8, 5) == unchecked((int)0x55555555) && d1.GetPixel(9, 5) == unchecked((int)0xAAAAAAAA);   // 1/3 and 2/3 coverage - the alpha fades over the transparent background too (premultiplied average)
      bool tail = d1.GetPixel(12, 5) == unchecked((int)0xAAAAAAAA) && d1.GetPixel(13, 5) == unchecked((int)0x55555555) && d1.GetPixel(14, 5) == 0;   // the smear extends past the right edge (taps gather from the left)
      Chk(head && ramp && tail, $"motion blur: linear 0 deg len 2 taps 3 = exact thirds trail ({d1.GetPixel(8, 5):X8} {d1.GetPixel(12, 5):X8})");
      var d2 = new Sprite(20, 10); d2.ClearBuffer(0);
      d2.DrawFx(src, 2, 0, new Effects().MotionBlurPath(new System.Drawing.PointF[] { new(0, 0), new(0, 4) }, 1f, 3, SR2D.Filter.Nearest), SR2D.Op.Paint);
      bool vert = d2.GetPixel(10, 7) == unchecked((int)0xAAAAAAAA) && d2.GetPixel(10, 6) == unchecked((int)0xAAAAAAAA);   // both probe rows catch 2 of the 3 taps (y, y-2, y-4)   // the path bends the trail DOWN now (taps at y, y-2, y-4)
      Chk(vert, "motion blur: custom path (0,0)->(0,4) trails downward");
      var e3 = new Effects().MotionBlurPath(new System.Drawing.PointF[] { new(0, 0), new(8, 0) }, 1f);
      e3.Clear();                                                        // the native path buffer must free cleanly
      Chk(e3.Count == 0, "motion blur: Clear frees the path buffer (chain reusable)");
    }
    {   // MotionEcho: the real-time feedback blur - two passes per frame, trail = faded history
      using var echo = new MotionEcho(4, 2);
      var a = new Sprite(4, 2); a.Pixels[0] = unchecked((int)0xFFFF0000);           // red at (0,0)
      echo.Persistence = 0.5f;
      var acc = echo.Step(a);
      bool first = acc.GetPixel(0, 0) == unchecked((int)0xFFFF0000);               // empty accumulator: frame 1 == frame
      var b = new Sprite(4, 2); b.Pixels[3] = unchecked((int)0xFF00FF00);          // green, moved to (3,0)
      acc = echo.Step(b);
      bool moved = acc.GetPixel(3, 0) == unchecked((int)0xFF00FF00);               // the new position is fully the frame
      bool faded = acc.GetPixel(0, 0) == unchecked((int)0x80FF0000);               // the old one faded to half alpha (colour kept)
      bool clean = acc.GetPixel(1, 0) == 0 && acc.GetPixel(2, 0) == 0 && acc.GetPixel(0, 1) == 0;   // nothing smeared between
      Chk(first && moved && faded && clean, $"motion echo: step = fade + over ({acc.GetPixel(0, 0):X8} {acc.GetPixel(3, 0):X8})");
      echo.Persistence = 0f;
      acc = echo.Step(a);
      Chk(acc.GetPixel(0, 0) == unchecked((int)0xFFFF0000) && acc.GetPixel(3, 0) == 0, "motion echo: persistence 0 = no trail");
      echo.Persistence = 0.9f; echo.Reset();
      acc = echo.Step(b);
      Chk(acc.GetPixel(3, 0) == unchecked((int)0xFF00FF00) && acc.GetPixel(0, 0) == 0, "motion echo: Reset clears the history");
      a.Dispose(); b.Dispose();
    }
    {   // DrawMotionTaps: the real-time multi-tap blur must be the same AVERAGE the resampled blur is,
      // not n copies piled up. A 4x4 white sprite, taps at x0, x0+1, x0+2 (three taps, each 1/3): the
      // columns are covered by 1, 2, 3, 3, 2, 1 taps = 85, 170, 255, 255, 170, 85 in colour AND alpha.
      var src = new Sprite(4, 4); src.ClearBuffer(unchecked((int)0xFFFFFFFF));
      var d = new Sprite(12, 4); d.ClearBuffer(0);
      d.DrawMotionTaps(src, 2, 0, new System.Drawing.PointF[] { new(0, 0), new(1, 0), new(2, 0) }, 3);
      int[] col = new int[6]; for (int x = 0; x < 6; x++) col[x] = d.GetPixel(2 + x, 1);
      bool ramp = col[0] == unchecked((int)0x55555555) && col[1] == unchecked((int)0xAAAAAAAA)
        && col[2] == unchecked((int)0xFFFFFFFF) && col[3] == unchecked((int)0xFFFFFFFF)
        && col[4] == unchecked((int)0xAAAAAAAA) && col[5] == unchecked((int)0x55555555);
      Chk(ramp, $"motion taps: 3 taps = the exact box average, colour and alpha ({string.Join(" ", col.Select(v => v.ToString("X8")))} expected 55555555 AAAAAAAA FFFFFFFF FFFFFFFF AAAAAAAA 55555555)");
      // a semi-transparent copy must not bleed its colour into the trail: straight (255,255,255,128) is
      // premultiplied first, so one tap of it is 64 of everything, two overlapping taps 128.
      var soft = new Sprite(4, 4); soft.ClearBuffer(unchecked((int)0x80FFFFFF));
      var d2 = new Sprite(12, 4); d2.ClearBuffer(0);
      d2.DrawMotionTaps(soft, 2, 0, new System.Drawing.PointF[] { new(0, 0), new(1, 0) }, 2);
      Chk(d2.GetPixel(3, 1) == unchecked((int)0x80808080) && d2.GetPixel(2, 1) == unchecked((int)0x40404040),
          $"motion taps: alpha-weighted (a straight half-alpha sprite gives 1/2 of its coverage, not its colour) ({d2.GetPixel(2, 1):X8} {d2.GetPixel(3, 1):X8})");
      // a still trail (every tap on one spot) is the sprite itself - the echo cannot say that
      var d3 = new Sprite(8, 8); d3.ClearBuffer(0);
      d3.DrawMotionTaps(src, 2, 2, new System.Drawing.PointF[] { new(0, 0), new(0, 0), new(0, 0) }, 3);
      Chk(d3.GetPixel(3, 3) == unchecked((int)0xFFFFFFFF), $"motion taps: a zero-length trail is the sprite, not a faded copy ({d3.GetPixel(3, 3):X8})");
      // the cost the whole point of it: the resampled blur re-samples (sprite + 2 x trail) per tap
      var big = new Sprite(256, 256); big.ClearBuffer(unchecked((int)0xFFFFFFFF));
      var dst = new Sprite(1024, 1024); dst.ClearBuffer(0);
      var fxm = new Effects().MotionBlur(0, 400);
      dst.DrawFx(big, 64, 64, fxm, SR2D.Op.Paint);
      var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
      for (int i = 0; i < 3; i++) dst.DrawFx(big, 64, 64, fxm, SR2D.Op.Paint);
      double blurMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 3;
      t0 = System.Diagnostics.Stopwatch.GetTimestamp();
      for (int i = 0; i < 3; i++) dst.DrawMotionTaps(big, 64, 64, new System.Drawing.PointF[] { new(0, 0), new(400, 0) }, 8);
      double tapsMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 3;
      Console.WriteLine($"      256^2 sprite, 400 px trail, one draw: resampled {blurMs:0.00} ms (32 taps), taps {tapsMs:0.00} ms (8 taps) - {blurMs / Math.Max(0.001, tapsMs):0.0}x");
      Chk(tapsMs < blurMs, $"motion taps: cheaper per frame than the resampled blur at a long trail ({tapsMs:0.00} ms vs {blurMs:0.00} ms)");
      src.Dispose(); soft.Dispose(); d.Dispose(); d2.Dispose(); d3.Dispose(); big.Dispose(); dst.Dispose();
    }
    {   // pixel font drop shadow: the shadow glyphs sit under the text, offset down-right
      static (int white, int shadow) Count(Sprite s) {
        int w = 0, sh = 0;
        for (int i = 0; i < s.Width * s.Height; i++) { int c = s.Pixels[i]; if (c == unchecked((int)0xFFFFFFFF)) w++; else if (c == unchecked((int)0xFF204060)) sh++; }
        return (w, sh);
      }
      var s1 = new Sprite(40, 16);
      var r1 = s1.DrawText(2, 2, "ll", unchecked((int)0xFFFFFFFF), 0, 2, 0, 0, SR2D.LineOp.Set, 128, TextAnchor.TopLeft, null, unchecked((int)0xFF204060), 1);
      var (w1, sh1) = Count(s1);
      var s2 = new Sprite(40, 16);
      var r2 = s2.DrawText(2, 2, "ll", unchecked((int)0xFFFFFFFF), 0, 2, 0, 0, SR2D.LineOp.Set);
      var (w2, sh2) = Count(s2);
      Chk(w1 == w2 && w1 > 0, $"pixel font shadow: the text itself is unchanged ({w1} vs {w2} px)");
      Chk(sh1 > 0 && sh2 == 0, $"pixel font shadow: the shadow is there only with the colour set ({sh1} vs {sh2} px)");
      Chk(r1.Width > r2.Width && r1.Height > r2.Height, $"pixel font shadow: the box covers the shadow ({r1.Width}x{r1.Height} vs {r2.Width}x{r2.Height})");
      s1.Dispose(); s2.Dispose();
    }
    {   // ink measurement: the set pixels, not the advance box - what the title plate hugs
      var box = Sprite.MeasureText("T.");
      var iTD = Sprite.MeasureTextInk("T.");
      var iT = Sprite.MeasureTextInk("T");
      Chk(iT.X == 0 && iT.Width > 0, $"ink: 'T' starts at its first set pixel ({iT.Width} px wide)");
      Chk(iTD.Width > 0 && iTD.Width < box.Width, $"ink: the advance box runs past the dot's ink ({iTD.Width} ink vs {box.Width} advance)");
      var iTS = Sprite.MeasureTextInk("T ");
      Chk(iTS.X == iT.X && iTS.Width == iT.Width, "ink: a trailing space does not widen the ink");
      var ispc = Sprite.MeasureTextInk(" T");
      Chk(ispc.X == PixelFont.Default.GlyphWidth + 1, $"ink: a leading space shifts the ink by its advance ({ispc.X})");
      var iH = Sprite.MeasureTextInk("H");
      Chk(iH.Y == 0 && iH.Height == PixelFont.Default.GlyphHeight, $"ink: 'H' spans the full glyph height ({iH.Height} rows)");
      var iw = Sprite.MeasureTextInk("H", 1, 2);
      Chk(iw.Width == iH.Width + 2, $"ink: weight widens the ink by its px ({iw.Width} vs {iH.Width})");
      Chk(Sprite.MeasureTextInk("   ").IsEmpty, "ink: only spaces = no ink");
    }

    {   // translucent voxels: a glass voxel composites over the background through DrawVoxels (exact side-view pixels)
      static int Over(int bg, uint s) {   // mirrors the native vox_over: straight-alpha over, per channel incl. alpha
        uint a = s >> 24, ia = 255 - a;
        uint r = ((s >> 16) & 255) * a + (uint)((bg >> 16) & 255) * ia + 128, g = ((s >> 8) & 255) * a + (uint)((bg >> 8) & 255) * ia + 128;
        uint b = (s & 255) * a + (uint)(bg & 255) * ia + 128, o = a * 255u + (uint)((bg >> 24) & 255) * ia + 128;
        uint Q(uint t) => (t + (t >> 8)) >> 8;
        return unchecked((int)((Q(o) << 24) | (Q(r) << 16) | (Q(g) << 8) | Q(b)));
      }
      const int BG = unchecked((int)0xFF203050);
      var g = new VoxelGrid(6, 1, 1);
      g[1, 0, 0] = new Voxel(0xFFFF0000u); g[2, 0, 0] = new Voxel(0x80FFFFFFu); g[3, 0, 0] = new Voxel(0xFF00FF00u); g[4, 0, 0] = new Voxel(0xC0ABCDEFu);
      // exposure rule: the face toward a neighbour of LOWER alpha is drawn
      Chk((g.FaceBits(2, 0, 0) & 1) == 0 && (g.FaceBits(1, 0, 0) & 1) != 0 && (g.FaceBits(3, 0, 0) & 2) != 0,
          $"voxels: exposure = own alpha > neighbour alpha ({g.FaceBits(1, 0, 0):X2} {g.FaceBits(2, 0, 0):X2} {g.FaceBits(3, 0, 0):X2})");
      var cam = VoxelCamera.Side(); cam.Anchor = VoxelAnchor.Origin; cam.Mode = VoxelMode.Cubes;
      var d = new Sprite(12, 6); d.ClearBuffer(BG);
      int drawn = d.DrawVoxels(g, cam, 4, 3, VoxelLighting.None);
      Chk(drawn == 4, $"voxels: 4 solid cells drawn ({drawn})");
      Chk(d.GetPixel(5, 2) == unchecked((int)0xFFFF0000) && d.GetPixel(7, 2) == unchecked((int)0xFF00FF00),
          "voxels: opaque cells write exactly their colour");
      Chk(d.GetPixel(6, 2) == Over(BG, 0x80FFFFFFu) && d.GetPixel(8, 2) == Over(BG, 0xC0ABCDEFu),
          $"voxels: glass composites over the background ({d.GetPixel(6, 2):X8} {d.GetPixel(8, 2):X8})");
      Chk(d.GetPixel(4, 2) == BG && d.GetPixel(9, 2) == BG && d.GetPixel(6, 1) == BG && d.GetPixel(6, 3) == BG,
          "voxels: nothing drawn outside the exposed faces");
      cam.KeepAlpha = true;                       // the alpha-tag contract: raw stamp, no blending
      var d2 = new Sprite(12, 6); d2.ClearBuffer(BG);
      d2.DrawVoxels(g, cam, 4, 3, VoxelLighting.None);
      Chk(d2.GetPixel(6, 2) == unchecked((int)0x80FFFFFF) && d2.GetPixel(8, 2) == unchecked((int)0xC0ABCDEF) && d2.GetPixel(5, 2) == unchecked((int)0xFFFF0000),
          $"voxels: KEEP_ALPHA stamps the raw colours ({d2.GetPixel(6, 2):X8} {d2.GetPixel(8, 2):X8})");
    }
    int diluted = 0;
    for (int seed = 1; seed <= 64; seed++) {
      var s = new Sprite(8, 8); s.ClearBuffer(unchecked((int)0xFF000000));
      for (int x = 0; x < 8; x++) s.Pixels[x] = unchecked((int)0xFFFFFFFFu);                    // only the top row white
      var e = new Effects(); e.Diffuse(1, 1, seed); s.DrawFx(s, 0, 0, e);
      for (int x = 0; x < 8; x++) if ((uint)s.Pixels[x] == 0xFF000000u) { diluted++; break; }
    }
    Chk(diluted >= 48, $"diffuse: a bright top row dilutes like Photoshop ({diluted}/64 seeds get a dark pixel up there; the old edge clamp kept it 64/64)");
    int stick = 0;
    for (int seed = 1; seed <= 256; seed++) {
      var s = new Sprite(16, 16); s.ClearBuffer(unchecked((int)0xFF000000)); s.Pixels[0] = unchecked((int)0xFFFFFFFFu);
      var e = new Effects(); e.Diffuse(1, 1, seed); s.DrawFx(s, 0, 0, e);
      if ((uint)s.Pixels[0] == 0xFFFFFFFFu) stick++;
    }
    Chk(stick >= 12 && stick <= 44, $"diffuse: an isolated bright corner keeps its value ~1 seed in 9 ({stick}/256; border pixels have no extra self-pick bias)");
    int stickW = 0;
    for (int seed = 1; seed <= 128; seed++) {
      var s = new Sprite(128, 128); s.ClearBuffer(unchecked((int)0xFF000000)); s.Pixels[0] = unchecked((int)0xFFFFFFFFu);
      var e = new Effects(); e.Diffuse(1, 1, seed); s.DrawFx(s, 0, 0, e);
      if ((uint)s.Pixels[0] == 0xFFFFFFFFu) stickW++;
    }
    Chk(stickW >= 6 && stickW <= 24, $"diffuse: same at a 128x128 corner (wide-picture fold formula): {stickW}/128");
    bool flat = true;
    { var s = new Sprite(16, 16); s.ClearBuffer(unchecked((int)0xFF808080));
      var e = new Effects(); e.Diffuse(3, 2, 7); s.DrawFx(s, 0, 0, e);
      for (int i = 0; i < 256; i++) if (s.Pixels[i] != unchecked((int)0xFF808080)) flat = false; }
    { var s = new Sprite(128, 128); s.ClearBuffer(unchecked((int)0xFF808080));
      var e = new Effects(); e.Diffuse(3, 2, 7); s.DrawFx(s, 0, 0, e);
      for (int i = 0; i < 128 * 128; i++) if (s.Pixels[i] != unchecked((int)0xFF808080)) flat = false; }
    Chk(flat, "diffuse: a flat opaque picture is unchanged (narrow LUT + wide formula fold, 2 passes)");
    bool one = true;
    for (int seed = 1; seed <= 32; seed++) {
      var s = new Sprite(1, 8); for (int y = 0; y < 8; y++) s.Pixels[y] = unchecked((int)(0xFF000000u | (uint)(30 + y * 30)));
      var e = new Effects(); e.Diffuse(2, 1, seed); s.DrawFx(s, 0, 0, e);
      for (int y = 0; y < 8; y++) if ((uint)s.Pixels[y] >> 24 != 255) one = false;
    }
    Chk(one, "diffuse: a 1 px wide picture folds cleanly (no crash, alpha intact)");
    bool ring = true;
    { var src = new Sprite(8, 8); for (int i = 0; i < 64; i++) src.Pixels[i] = unchecked((int)(0xFF000000u | (uint)init[i]));
      var dst = new Sprite(32, 32); dst.ClearBuffer(unchecked((int)0xFF010203));
      var e = new Effects(); e.Diffuse(2, 2, 543); dst.DrawFx(src, 12, 12, e, SR2D.Op.AlphaBlend);
      for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        if (x < 12 || x >= 20 || y < 12 || y >= 20) { if (dst.Pixels[y * 32 + x] != unchecked((int)0xFF010203)) ring = false; } }
    Chk(ring, "diffuse: the transparent ring is restored - a draw onto a larger canvas touches only the source rect");
    { var a = new Sprite(8, 8); for (int i = 0; i < 64; i++) a.Pixels[i] = unchecked((int)(0xFF000000u | (uint)init[i]));
      var e = new Effects(); e.Diffuse(); e.DiffuseSeed(543); a.DrawFx(a, 0, 0, e);
      var was = SR2D.SetSimdLevel(SR2D.SimdLevel.Sse2);
      var b = new Sprite(8, 8); for (int i = 0; i < 64; i++) b.Pixels[i] = unchecked((int)(0xFF000000u | (uint)init[i]));
      var e2 = new Effects(); e2.Diffuse(); e2.DiffuseSeed(543); b.DrawFx(b, 0, 0, e2);
      SR2D.SetSimdLevel(was);
      bool same = true; for (int i = 0; i < 64; i++) if (a.Pixels[i] != b.Pixels[i]) same = false;
      Chk(same, "diffuse: SSE2 and AVX2 kernels produce identical output"); }
    Console.WriteLine("  (diffuse edges: borders fold, no wrap, no ring bleed)");
  }
  static int Main() {
    if (FrameCheck.Run() != 0) return 1;
    EdgeStrips();
    DiffuseEdges();
    SelectionMoveOps();
    DiscreteValues();
    AnimationTangents();
    CursorSheet();
    var canvas = new Sprite(1100, 620); canvas.ClearBuffer(unchecked((int)0xFF202428));
    int col = 0;
    foreach (var mode in new[]{KnobDragMode.Angular, KnobDragMode.Endless}) {
      int row = 0;
      foreach (var g in new[]{KnobGauge.Arc, KnobGauge.Circle, KnobGauge.Rings, KnobGauge.Spiral, KnobGauge.None}) {
        var k = new SpriteKnob { Text = $"{mode} {g}", Minimum = -100, Maximum = 100, Value = 35, Step = 1, Unit = " px", DragMode = mode, Gauge = g, Turns = 4, Size = new Size(150, 184), AccentColor = Color.FromArgb(0xFF,0x90,0x30) };
        var s = k.RenderOnce(); canvas.Draw(s, 10 + col * 160, 10 + row * 0, SR2D.Op.Paint);
        // and the same with an infinite pointer after a drag past the end
        var k2 = new SpriteKnob { Text = $"{g} inf", Minimum = -100, Maximum = 100, Value = 35, Step = 1, Unit = " px", DragMode = mode, Gauge = g, Pointer = KnobPointer.Infinite, Turns = 4, Size = new Size(150, 184), AccentColor = Color.FromArgb(0x60,0xE0,0x80) };
        k2.Down(75 + 40, 100); for (int i = 1; i <= 40; i++) { double a = i * Math.PI * 2 / 20; k2.Move((int)(75 + 40 * Math.Cos(a)), (int)(100 + 40 * Math.Sin(a))); } k2.Up(75,100);
        canvas.Draw(k2.RenderOnce(), 10 + col * 160, 210, SR2D.Op.Paint);
        Console.WriteLine($"{mode} {g}: infinite pointer knob value after 2 turns = {k2.Value}");
        col++;
      }
    }
    // infinite pointer: relative drag, no jump on press, pointer keeps going past the end, value moves back at once
    {
      var k = new SpriteKnob { Minimum = 0, Maximum = 100, Value = 50, Pointer = KnobPointer.Infinite, Gauge = KnobGauge.Circle, Size = new Size(150, 184) };
      double v0 = k.Value; k.Down(75 + 40, 100); double v1 = k.Value;   // press at 3 o'clock: value must not change
      // 3 clockwise turns from 3 o'clock: value should clamp at 100 while the pointer accumulates ~3 turns
      for (int i = 1; i <= 60; i++) { double a = i * Math.PI * 2 / 20; k.Move((int)(75 + 40 * Math.Cos(a)), (int)(100 + 40 * Math.Sin(a))); }
      double v2 = k.Value;
      // now back a quarter turn: value must drop immediately (no dead travel)
      for (int i = 59; i >= 55; i--) { double a = i * Math.PI * 2 / 20; k.Move((int)(75 + 40 * Math.Cos(a)), (int)(100 + 40 * Math.Sin(a))); }
      double v3 = k.Value; double deg3 = k.PointerDegrees;
      // pinned at 100: another half turn must still turn the pointer by ~180 degrees
      for (int i = 56; i <= 66; i++) { double a = i * Math.PI * 2 / 20; k.Move((int)(75 + 40 * Math.Cos(a)), (int)(100 + 40 * Math.Sin(a))); }
      double v4 = k.Value, deg4 = k.PointerDegrees; k.Up(75, 100);
      Console.WriteLine($"infinite past the end: value {v4} (expect 100), pointer turned {deg4 - deg3:0} deg (expect ~180)");
      if (v4 != 100 || Math.Abs(deg4 - deg3 - 180) > 15) Environment.Exit(1);
      Console.WriteLine($"infinite: press {v0}->{v1} (expect no change), 3 turns -> {v2} (expect 100), back 1/4 turn -> {v3:0} (expect ~72, integer mouse positions on a 40 px circle)");
      if (v0 != v1 || v2 != 100 || Math.Abs(v3 - 72) > 4) Environment.Exit(1);
    }
    // sliders
    var sl = new SpriteSlider { Text = "Bipolar", Minimum = -50, Maximum = 50, Value = 20, Bipolar = true, Ticks = 10, Size = new Size(260, 52) };
    canvas.Draw(sl.RenderOnce(), 10, 420, SR2D.Op.Paint);
    // endless bounded: drag 3 turns from 0 -> should clamp at 100 (4 turns for 200 => 50/turn => 3 turns = 150 -> clamp 100)
    var e = new SpriteKnob { Minimum = -100, Maximum = 100, Value = 0, DragMode = KnobDragMode.Endless, Turns = 4, Size = new Size(150,184) };
    e.Down(75, 60); for (int i = 1; i <= 60; i++) { double a = -Math.PI/2 + i * Math.PI * 2 / 20; e.Move((int)(75 + 40 * Math.Cos(a)), (int)(100 + 40 * Math.Sin(a))); } e.Up(75,100);
    Console.WriteLine($"endless bounded after 3 turns from 0: {e.Value} (expect 100)");
    canvas.Draw(e.RenderOnce(), 300, 420, SR2D.Op.Paint);
    Save(canvas, "/home/user/.cache/ctlrun/knobs.rgba");
    Buttons();
    Furniture();
    Fonts();
    TextViews();
    Wheels();
    Views();
    SizeModeBox();
    VoxelViews();
    return 0;
  }

  // cursor pictures at 1x, 2x and a 3x blow-up (the HCURSOR conversion itself is Windows-only)
  static void CursorSheet() {
    var sheet = new Sprite(800, 240); sheet.ClearBuffer(unchecked((int)0xFF6080A0));
    for (int x = 0; x < 800; x += 20) for (int y = 0; y < 240; y += 20) if (((x / 20) + (y / 20)) % 2 == 0) sheet.ClearRect(x, x + 20, y, y + 20, unchecked((int)0xFF8098B8));
    var kinds = new[] { SpriteCursors.Kind.HandOpen, SpriteCursors.Kind.HandGrab, SpriteCursors.Kind.Rotate, SpriteCursors.Kind.ZoomIn, SpriteCursors.Kind.ZoomOut, SpriteCursors.Kind.Pen };
    Console.WriteLine($"cursor sizes: 96 dpi -> {SpriteCursors.SizeForDpi(96)}, 120 -> {SpriteCursors.SizeForDpi(120)}, 144 -> {SpriteCursors.SizeForDpi(144)}, 192 (4K 200%) -> {SpriteCursors.SizeForDpi(192)}, 288 -> {SpriteCursors.SizeForDpi(288)}");
    if (SpriteCursors.SizeForDpi(96) != 32 || SpriteCursors.SizeForDpi(144) != 48 || SpriteCursors.SizeForDpi(192) != 64) Fail("cursor size per dpi");
    // the hands must be opaque where the fill is and empty outside; the 64 px version is the same picture scaled
    { using var a = new Sprite(32, 32, SR2D.Op.AlphaOver); a.ClearBuffer(0); var hot = SpriteCursors.Draw(SpriteCursors.Kind.HandOpen, a, 1f);
      using var b = new Sprite(64, 64, SR2D.Op.AlphaOver); b.ClearBuffer(0); var hot2 = SpriteCursors.Draw(SpriteCursors.Kind.HandOpen, b, 2f);
      int inkA = 0, inkB = 0; foreach (var v in a.Pixels) if ((v >> 24 & 255) > 128) inkA++; foreach (var v in b.Pixels) if ((v >> 24 & 255) > 128) inkB++;
      Console.WriteLine($"open hand: {inkA} opaque px at 32, {inkB} at 64 (expect ~4x), hot {hot} / {hot2} (expect 16,17 / 32,34), palm pixel white = {(a.Pixels[17 * 32 + 16] & 0xFFFFFF) == 0xFFFFFF}");
      if (inkA < 200 || Math.Abs(inkB - inkA * 4) > inkA || hot2 != new Point(32, 34) || (a.Pixels[17 * 32 + 16] & 0xFFFFFF) != 0xFFFFFF) Fail("hand cursor picture"); }
    // the pen: tip at the hot spot (bottom left), a dark nib and a white body
    { using var a = new Sprite(32, 32, SR2D.Op.AlphaOver); a.ClearBuffer(0); var hot = SpriteCursors.Draw(SpriteCursors.Kind.Pen, a, 1f);
      int ink = 0; foreach (var v in a.Pixels) if ((v >> 24 & 255) > 128) ink++;
      int nib = 0; foreach (var v in a.Pixels) if ((v >> 24 & 255) > 200 && ((v >> 16 & 255) < 90) && (v & 255) < 90) nib++;
      Console.WriteLine($"pen: {ink} opaque px, {nib} dark nib px, hot {hot} (expect 8,24)");
      if (ink < 100 || nib < 8 || hot != new Point(8, 24)) Fail("pen cursor picture"); }
    // cursor policy: draggable controls show the hand, clickable ones the arrow
    if (new SpriteKnob().Cursor != SpriteCursors.HandOpen || new SpriteSlider().Cursor != SpriteCursors.HandOpen) Fail("range controls must show the open hand");
    if (new SpriteButton().Cursor == SpriteCursors.HandOpen || new SpriteToggle().Cursor == SpriteCursors.HandOpen || new SpriteRadio().Cursor == SpriteCursors.HandOpen || new SpriteLed { Clickable = true }.Cursor == SpriteCursors.HandOpen) Fail("clickable controls must not show the hand");
    for (int k = 0; k < kinds.Length; k++) {
      using var c1 = new Sprite(32, 32, SR2D.Op.AlphaOver); c1.ClearBuffer(0); var hot = SpriteCursors.Draw(kinds[k], c1, 1f);
      sheet.Draw(c1, 10 + k * 125, 10, SR2D.Op.AlphaOver);
      sheet.ClearRect(10 + k * 125 + hot.X, 10 + k * 125 + hot.X + 1, 10 + hot.Y, 10 + hot.Y + 1, unchecked((int)0xFFFF0000));
      using var c2 = new Sprite(64, 64, SR2D.Op.AlphaOver); c2.ClearBuffer(0); SpriteCursors.Draw(kinds[k], c2, 2f);
      sheet.Draw(c2, 10 + k * 125, 50, SR2D.Op.AlphaOver);
      using var big = new Sprite(96, 96); big.ClearBuffer(unchecked((int)0xFF6080A0)); big.DrawScaled(c1, 0, 0, 3f, 0, 0, SR2D.Op.AlphaOver, SR2D.Filter.Nearest);
      sheet.Draw(big, 10 + k * 125, 125, SR2D.Op.Paint);
    }
    Save(sheet, "/home/user/.cache/ctlrun/cursors.rgba"); Console.WriteLine("wrote cursors.rgba 800x240 (hot spot = red pixel)");
  }
}
