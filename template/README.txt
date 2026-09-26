SR2D application template  (WinForms, .NET 10, x64)
====================================================

What this is
------------
An empty-but-runnable WinForms app wired to the SR2D engine: a SpriteBox canvas
that fills the window and a few SR2D controls (knob, slider, toggle, button,
labels on a SpritePanel) on the right that drive it. Delete the sample drawing
in MainForm.cs and put your own in - the plumbing stays.

Files
-----
  SR2DApp.csproj        net10.0-windows, x64, WinForms, unsafe on, ImplicitUsings on. Compiles ..\cs\*.cs
                        into the app (the SR2D classes are internal; one assembly = they
                        are visible and the JIT can inline across them) and copies
                        SR2D64.dll next to the exe.
  Program.cs            64-bit guard + ApplicationConfiguration.Initialize() + Run(MainForm)
  MainForm.cs           your code: the Render handler and the event handlers
  MainForm.Designer.cs  the layout, designer-compatible (open the form in the VS designer;
                        the SR2D controls show up in the toolbox after the first build)

Getting started
---------------
  1. Build the native DLL once:  ..\build_release.bat   (clang-cl, Release x64)
     -> ..\native\bin\x64\SR2D64.dll.  The csproj picks it up from there, or from a
     copy dropped next to the .csproj (that one wins - handy to pin a DLL build).
  2. Open SR2DApp.csproj (Visual Studio 2022 17.12+ / Rider / `dotnet build`).
  3. F5.

The form designer
-----------------
Open MainForm.cs in the designer after the first build. The SR2D controls draw
themselves there like at run time when the designer can load SR2D64.dll; the
csproj bakes the DLL's absolute path into the assembly (AssemblyMetadata
"SR2D.DllPath") because the designer process does not run from bin\... and would
otherwise report "Unable to load DLL 'SR2D64'" and disable the controls. If the
DLL cannot be loaded anyway (not built yet, moved), the controls show a plain
placeholder with their type name instead of failing - build the DLL, rebuild the
project and reopen the designer. ImplicitUsings can stay enabled (default).

Moving the project out of the repo
----------------------------------
Copy this folder anywhere and either
  * keep the SR2D sources reachable and set <Sr2dCs> (and <Sr2dDll>) in the csproj, or
  * copy ..\cs into the project as "cs" and set <Sr2dCs>cs</Sr2dCs>, copy SR2D64.dll
    next to the csproj.
Nothing else is needed - no NuGet packages, no references.

The one pattern to keep
-----------------------
  void canvas_Render(object sender, RenderEventArgs e)  // designer: Events tab > Render (or double-click the box)
  { var s = e.Surface; ...draw into s... }              // s = the back buffer, client size
  canvas.Redraw();                                      // when a value changed (repaint on the next paint message)
  canvas.RedrawNow();                                   // when you need it on screen before returning (drags)
Load / build sprites once (fields), draw them every frame. Sprites with
transparency: create with SR2D.Op.AlphaOver, fill, call Premultiply().

Where to look next
------------------
  ..\README.md          the full API (drawing, effects, vector import, voxels, controls, SpriteBox modes)
  ..\demo\Tests.cs      every feature as a small test (the demo's "Code (F2)" button shows the source of the running one)
  ..\demo\ControlsDemo.cs   the control strips - how the Sprite* controls are put together
  ..\CHANGELOG.txt      what changed, newest first
