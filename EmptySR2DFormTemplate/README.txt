EmptySR2DFormTemplate - an EMPTY WinForms project (.NET 10, x64) with SR2D
=========================================================================

What this is
------------
The file set Visual Studio generates for "Windows Forms App" (.NET 10), with
nothing on the form - plus the SR2D engine compiled in, and the one blank form
derived from SpriteForm so the window is an SR2D window from the first F5.

  Program.cs                    ApplicationConfiguration.Initialize() + Application.Run(new Form1())
  Form1.cs                      the blank form (empty constructor, like the VS template)
  Form1.Designer.cs             InitializeComponent() - the form itself, no controls
  EmptySR2DFormTemplate.csproj  net10.0-windows, x64, WinForms, nullable + ImplicitUsings on,
                                unsafe on, analyzers on; compiles ..\cs\*.cs into the exe and
                                copies SR2D64.dll next to it

One deliberate difference from the stock template
-------------------------------------------------
  * AutoScaleMode.None instead of Font: the engine works in device pixels (see the csproj DPI
    note and the README "DPI" section), so WinForms must not scale the chrome.

Form1 is `public partial` in BOTH files. SpriteForm is public, and a designer-created form has
to be public for the designer host to instantiate it; the two halves of a partial class must
agree on the accessibility, so Form1.Designer.cs says `public partial class Form1` too.

Getting started
---------------
  1. Copy this folder and the WHOLE cs/ folder together (including SR2D.Native.targets and
     .editorconfig). cs/ can be a sibling of the project folder OR inside it; both are detected.
  2. Add the x64 SR2D64.dll with Solution Explorer -> Add Existing Item (None or Content, including
     Add As Link), or put it next to this .csproj. A DLL already in the actual Debug/Release output
     directory also works. native/bin/x64 is an OPTIONAL repository fallback, not a required second copy.
  3. Build once. F5: an empty 800 x 450 window with the SR2D title bar.

The form designer
-----------------
Build once, then close/reopen the designer and View Designer on Form1.cs. The native DLL is a
project ASSET, not a managed assembly reference: do not use Add Reference for it. Files under
Assets/ or linked from elsewhere are copied to the output ROOT as SR2D64.dll. The cs/ import
embeds actual source/output/project paths so DesignToolsServer can locate that file even when
it loads the managed assembly from a cache/shadow-copy folder. It does not require native/ to
exist in a relocated solution, and an output-only DLL does not require a project-root duplicate.

Keep DLL dependencies and bitness correct (x64); Windows full-path loading also probes beside
the DLL for its dependencies. SR2D.IsAvailable / SR2D.NativeLoadError report availability/reason.
If native loading fails, SpriteBox controls AND the form title bar paint placeholders, leaving
the designer usable rather than disabling TitleBarStrip. Rebuild/reopen after fixing the file.
Actual designer painting is a Windows smoke test; the loader/metadata/relocation probes are in
../tests/cs/nativeload/check.py. The copyable cs/.editorconfig keeps the engine's audited analyzer
settings with its sources, without changing your own application code's analyzer policy.

The toolbox: every SR2D control type (and SpriteForm) is PUBLIC now, which is the condition
Visual Studio uses to put a WinForms control in the toolbox - an internal one compiles and runs
fine but never appears. The types referenced by their public members are public for the same
reason (a toolbox scan is a compile, and CS0050/CS0051/CS0053 would drop the whole assembly).
If the toolbox still shows nothing for a project that builds: Toolbox -> right-click -> Show All,
then Reset Toolbox; if it shows nothing for a project that has never been built, build it first -
the entries come from the output assembly.

SpriteForm itself carries [DesignerCategory("Form")], and that attribute is INHERITED: with the
older [DesignerCategory("Code")] (which Form has by default for code-designed forms) Visual
Studio opened every derived form straight into the code editor, and an `internal` base hid all
of the chrome properties below from the property grid. Both are fixed in the base class, so
nothing special is needed here.

The chrome of an empty form
---------------------------
SpriteForm gives this window, all of it switchable:
  Text                     the caption; SetTitleTags(...) adds more texts after it
                           (see "Extra coloured texts in the title bar" below)
  ShowStripes / Stripe*    the skewed colour bars in the left corner (seed, width, count, mode,
                           style, skew, palette)
  StripeOffset             slides the bar run right (or left, negative) from where it is placed
  StripeAlignment          Left / Center / Right / WithText - where the run sits: pinned to the bar's
                           left corner (Left), in the width the bar has for it (Center / Right), or
                           centred ON the icon + text block it mirrors (WithText: the same margin past
                           the icon as past the last glyph)
  StripeBrightness         0..300, 100 = as painted: scales every bar colour's value
  StripeColors / StripeColorList
                           a palette to cycle through instead of the random colours. In the designer
                           use StripeColorList: its "..." button opens a list editor (a swatch plus
                           AARRGGBB per row, Add / Edit / Remove, each one through the standard
                           Windows colour dialog). You can still type the value instead - AARRGGBB,
                           RRGGBB, #RRGGBB, 0xRRGGBB, the decimal ARGB integer, or a colour name,
                           separated by commas or spaces ("FF3B82F6, 20A020, Red, 4294901760"). A token
                           that is neither is skipped. StripeColors is the int[] view of the same thing
                           and is hidden from the grid: the list text is parsed ONCE, in the setter, and
                           the bars paint from that int array, so a repaint never converts text. The
                           list is stored and read back in the same hex form, so it survives a designer
                           save.
  StripeBarWidth           px per bar, taken LITERALLY in every mode. 0 = the width comes out of the
                           length: in CaptionLength / AllTitleTextLength the run is as wide as the icon +
                           text block measured IN PIXELS, divided between the bars, so the last bar's
                           top edge ends under the last glyph. A positive width keeps that pixel length
                           and asks for that width per bar, and the NUMBER of bars becomes whatever
                           covers it (ceil(length / width)). Those are the two pairings: custom width /
                           auto amount, or auto width / custom amount. Want both fixed? StripeMode
                           FixedCount - there the count and the width are yours and the run may pass the
                           text. (The history: CaptionLength with a 46 character caption and
                           StripeBarWidth 10 laid out 460 px of colour over a 305 px caption, then the
                           round that followed capped the width to each bar's share instead - 50 bars of
                           6 px, the right length with bars that were not the number in the property.
                           Length mirrors the text, width is what you typed, count gives.)
  TitleBarColor            the bar's own background colour. The int is ARGB, and the property grid
                           shows it EXACTLY like a native Color: the swatch rectangle in front of the
                           value, the value as "[A=255, R=46, G=52, B=60]" (or the name, "Red", when
                           the colour is one of the known Windows ones), the drop-down arrow with that
                           list of names to pick from - not exclusive, so you can still type - and the
                           "..." button with the standard Windows colour dialog. BorderColor,
                           TitleTextColor and TitleTextShadowColor are the same kind of cell. What you
                           can type: AARRGGBB, RRGGBB, #RRGGBB, 0xRRGGBB, the decimal ARGB integer
                           (4278190080), a colour name, or the bracket text the cell shows. The alpha
                           byte is real and survives the round trip, which is the one thing a native
                           Color cell does not give you.
  TitleTextDarken          None / Smooth / PerStripe - dims the colour bars where a title text sits on
                           them (TitleTextDarkenStrength 0..100, TitleTextDarkenFeather in px for Smooth,
                           TitleTextDarkenHeight 1..100 = how tall the dark band is as a per cent of the
                           bar height, centred on it: 100 is the whole stripe, 45 a band, 15 a slit
                           through the text)
  TitleTextFont            a real system font for the caption instead of the 1 px pixel font; the property
                           grid opens Visual Studio's font dialog. TitleTextFontFamily / TitleTextFontSize
                           are the same two values for setting them in code or in InitializeComponent
  StripesBehindText        default true: the icon and the texts are drawn IN FRONT of the bars;
                           false puts them after the pattern
  Icon / TitleIcon         the window icon drawn in the bar (null = the form's own Icon)
  IconBackdrop             the small rounded plate under the icon where it overlaps the bars
  TitleBarHeight           18 px by default (the smallest the chrome can be), 18..64; the button
                           glyphs and the pixel font scale with it
  TitleTextShadow*         every bar text is drop-shadowed by default
  BorderColor / BorderThickness / ResizeBandWidth   the frame, and the resize grab around it
  SmoothChrome             default true: the chrome is painted through a buffer and the bar keeps an
                           oversized pixel buffer, so dragging an edge does not flicker
  BackColor                the form's own - SpriteForm no longer writes it. Whatever you set in the
                           designer (or in code) is what you get at runtime; the frame colour is
                           BorderColor, the bar colour is TitleBarColor. SR2D CONTROLS TAKE IT TOO:
                           a SpriteButton / SpriteKnob / SpritePanel dropped on this form without a
                           colour of its own adopts this one and follows it when you change it (the
                           WinForms ambient rule, which our constructor used to break by setting a
                           dark default). A control you gave an explicit BackColor keeps it; a
                           generated "BackColor = Color.FromArgb(32, 36, 40)" is that default, so it
                           does not count as a choice and the grid stops writing it. Controls inside a
                           SpritePanel / SpriteTabPage take that container's face instead. A form
                           still on SystemColors.Control hands out nothing - an untouched light grey
                           form must not turn every dark control on it light.
  NativeWindowFrame        default true: the window keeps a real WS_CAPTION / WS_THICKFRAME frame
                           that SpriteForm makes invisible again, which is what gives the Windows 11
                           minimize / restore animations, the drop shadow and the snap layouts. The
                           frame's WM_NCACTIVATE is refused, so losing focus cannot paint a native
                           caption over the bar (the "turns into a default form" report). WM_NCPAINT
                           is NOT swallowed, even though there is no strip left to paint: that
                           message is also what validates the surface a resize hands the window, and
                           with nothing answering it the window blinks out to show the desktop for a
                           frame at a time while you drag an edge
  MinimumSize              not a knob: it is derived from the chrome (bar + buttons + 80 px of
                           pattern), so the window can never be resized into something with no bar
  double-click the bar     maximize / restore; right click it (or the icon) = the window menu

Extra coloured texts in the title bar (what the demo does)
----------------------------------------------------------
The demo bar reads "SR2D" in the caption colour, then "SR2D64.dll" in green and "kernels: SSE2 · new
API" in grey. There is no property for that - TitleTags is [Browsable(false)] on purpose, because a
list of objects carrying colours and fonts is not something a property grid edits usefully. It is one
call, in the constructor, the Load handler, or any time later (demo/MainForm.cs:974 is the original):

    SetTitleTags(new TitleTag("SR2D64.dll", unchecked((int)0xFF70E090)),
                 new TitleTag("kernels: SSE2", unchecked((int)0xFF8A94A6)));

A TitleTag (cs/SpriteForm.cs:99) is Text + Color (0xAARRGGBB) plus the optional FontFamily / FontSize /
Shadow / ShadowColor: a tag with ShadowColor 0 uses the bar's own TitleTextShadowColor, Shadow = false
turns the shadow off for that one text, and a FontSize of 0 keeps the bar's TitleTextFontSize. Each tag
is measured and placed after the caption with the same gap the caption has. SetTitleTags REPLACES the
list (call it with nothing to clear it), and an empty Text is skipped rather than taking up a gap.

What the colour bars do about it is StripeMode: CaptionLength mirrors the caption alone, so the run
stops at the last caption glyph and the tags read as text after the pattern (the demo look);
AllTitleTextLength mirrors caption + tags, so the bars run under all of it.

Photographing the window
------------------------
While NativeWindowFrame is on (the default) the window has a real frame that is hidden by answering
WM_NCCALCSIZE - that is a live composition, not a bitmap the window owns. So Control.DrawToBitmap and
PrintWindow do NOT show what is on screen for this form: both ask the non-client area to draw itself
and get a native caption back, which appears as a strip of system title-bar graphics over the SR2D
bar in the image only. Take a screen grab of the window rectangle (Graphics.CopyFromScreen on
GetWindowRect, in a DPI-aware process) to see what the user sees. anim-style comparisons have the same
trap: two captures of the same window through different APIs are not comparable.

Where to look next
------------------
  ..\README.md          the full API
  ..\CHANGELOG.txt      what changed, newest first
