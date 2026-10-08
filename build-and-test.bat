@echo off
rem SR2D - one entry point that builds and tests everything on Windows (the Linux twin is build-and-test.sh).
rem The native DLL is built by build_release.bat (needs the VS "C++ Clang tools for Windows" component);
rem everything managed builds and runs with the .NET 10 SDK alone. Exits non-zero on the first red gate.
setlocal
cd /d "%~dp0"

set FAIL=0
set DOTNET_NOLOGO=1

echo ==== native DLL (build_release.bat, Release x64)
call build_release.bat || set FAIL=1

for %%P in (tests\cs\ctlrun tests\cs\benchrun tests\cs\vecrun tests\cs\selchk tests\cs\autochk tests\cs\benchchk tests\cs\codechk tests\cs\fontchk tests\cs\webpchk) do (
  if exist "%%P" (
    echo ==== build %%P
    dotnet build "%%P" -c Release --no-incremental || set FAIL=1
  )
)

echo ==== projchk (file lists vs template)
python tests\cs\projchk.py || set FAIL=1

echo ==== build demo (Implicit off / on) + template
dotnet build demo -c Release --no-incremental -p:Implicit=disable -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended || set FAIL=1
dotnet build demo -c Release --no-incremental -p:Implicit=enable -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended || set FAIL=1
dotnet build template -c Release --no-incremental -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended || set FAIL=1
dotnet build apps\SpriteBox -c Release --no-incremental -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended || set FAIL=1

echo ==== run headless suites
for %%R in (ctlrun benchrun) do (
  if exist "%USERPROFILE%\.cache\%%R\bin\Release\net10.0\%%R.exe" (
    copy /y tests\build\SR2D64.dll "%USERPROFILE%\.cache\%%R\bin\Release\net10.0\" >nul 2>&1
    "%USERPROFILE%\.cache\%%R\bin\Release\net10.0\%%R.exe" || set FAIL=1
  ) else if exist "tests\cs\%%R\bin\Release\net10.0\%%R.exe" (
    copy /y tests\build\SR2D64.dll "tests\cs\%%R\bin\Release\net10.0\" >nul 2>&1
    "tests\cs\%%R\bin\Release\net10.0\%%R.exe" || set FAIL=1
  )
)
if exist "tests\cs\selchk\bin\Release\net10.0\selchk.exe" "tests\cs\selchk\bin\Release\net10.0\selchk.exe" || set FAIL=1

echo ==================================
if "%FAIL%"=="0" (echo ALL GREEN) else (echo RED - %FAIL% gate(s) failed)
exit /b %FAIL%
