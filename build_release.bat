@echo off
rem ============================================================================
rem  SR2D release build: native DLL (clang-cl, x64) + demo app (Release, x64),
rem  DLL copied into the demo so the exe is runnable straight away.
rem
rem  Usage:  build_release.bat            (double-click works too - it pauses at the end)
rem          build_release.bat nopause
rem          build_release.bat msvc       build the DLL with MSVC (v145) instead of clang-cl
rem  Exit code 0 = everything built, 1 = something failed (see the summary at the end).
rem
rem  If MSBuild is not found automatically, set the full path here (or as an
rem  environment variable SR2D_MSBUILD before starting the script), e.g.
rem    set SR2D_MSBUILD=H:\Programs\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
rem ============================================================================
setlocal
cd /d "%~dp0"
set "ROOT=%CD%"
set "TOOLSET=ClangCL"
if /i "%~1"=="msvc" set "TOOLSET=v145"
if /i "%~2"=="msvc" set "TOOLSET=v145"
set "STEP_DLL=not run"
set "STEP_COPY=not run"
set "STEP_BENCH=not run"
set "FAILED=0"
set "BENCH_EXE="

echo.
echo === [1/3] native DLL : native\SR2D.vcxproj  Release ^| x64  ^(toolset %TOOLSET%^)

rem ---------------------------------------------------------------- find MSBuild
set "MSBUILD="
if defined SR2D_MSBUILD if exist "%SR2D_MSBUILD%" set "MSBUILD=%SR2D_MSBUILD%"
if defined MSBUILD goto :msbuild_found

rem 1) vswhere (installed with every Visual Studio 2017+ setup, always under Program Files (x86))
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :msbuild_scan
echo   vswhere: %VSWHERE%
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if defined MSBUILD goto :msbuild_found
rem older vswhere without -find: ask for the install root
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -property installationPath`) do set "VSROOT=%%i"
if defined VSROOT if exist "%VSROOT%\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%VSROOT%\MSBuild\Current\Bin\MSBuild.exe"
if defined MSBUILD goto :msbuild_found

:msbuild_scan
rem 2) scan the usual install roots on every drive: <root>\Microsoft Visual Studio\<ver>\<edition>\MSBuild\Current\Bin\MSBuild.exe
echo   scanning for MSBuild.exe ...
for %%d in (C D E F G H I J K L M) do (
    for %%r in ("%%d:\Program Files" "%%d:\Program Files (x86)" "%%d:\Programs" "%%d:\Programs\Microsoft" "%%d:\" "%%d:\VS" "%%d:\VisualStudio") do (
        if not defined MSBUILD if exist "%%~r\Microsoft Visual Studio\" for /f "delims=" %%f in ('dir /b /s "%%~r\Microsoft Visual Studio\MSBuild.exe" 2^>nul ^| findstr /i /r "\\MSBuild\\Current\\Bin\\MSBuild.exe$"') do if not defined MSBUILD set "MSBUILD=%%f"
    )
)
if defined MSBUILD goto :msbuild_found

rem 3) on PATH (developer command prompt)
where msbuild >nul 2>nul && set "MSBUILD=msbuild"
if defined MSBUILD goto :msbuild_found

echo   ERROR: MSBuild.exe not found.
echo          Looked via vswhere, under "^<drive^>:\Program Files*\Microsoft Visual Studio\" and
echo          "^<drive^>:\Programs\Microsoft Visual Studio\", and on PATH.
echo          Set SR2D_MSBUILD to the full path of MSBuild.exe, e.g.
echo          set SR2D_MSBUILD=H:\Programs\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
set "STEP_DLL=FAILED - MSBuild.exe not found"
set "FAILED=1"
goto :bench

:msbuild_found
echo   MSBuild: %MSBUILD%
"%MSBUILD%" "%ROOT%\native\SR2D.vcxproj" /nologo /m /v:m /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=%TOOLSET%
if errorlevel 1 goto :dll_failed
if not exist "%ROOT%\native\bin\x64\SR2D64.dll" goto :dll_missing
set "STEP_DLL=OK  native\bin\x64\SR2D64.dll"
goto :copy

:dll_failed
set "STEP_DLL=FAILED - compiler / linker error, see above"
set "FAILED=1"
goto :bench

:dll_missing
set "STEP_DLL=FAILED - MSBuild returned 0 but native\bin\x64\SR2D64.dll is missing"
set "FAILED=1"
goto :bench

rem ---------------------------------------------------------------- copy the DLL
:copy
echo.
echo === [2/3] copy DLL into the demo project
copy /y "%ROOT%\native\bin\x64\SR2D64.dll" "%ROOT%\demo\SR2D64.dll" >nul
if errorlevel 1 goto :copy_failed
set "STEP_COPY=OK  demo\SR2D64.dll"
goto :bench

:copy_failed
set "STEP_COPY=FAILED - could not copy to demo\SR2D64.dll"
set "FAILED=1"
goto :bench

rem ---------------------------------------------------------------- bench
:bench
echo.
echo === [3/3] demo  : demo\SR2DDemo.csproj  Release  x64
where dotnet >nul 2>nul
if errorlevel 1 goto :no_dotnet
dotnet build "%ROOT%\demo\SR2DDemo.csproj" -c Release -nologo -v m
if errorlevel 1 goto :bench_failed
rem locate the output exe (bin\x64\Release\<tfm>\ or bin\Release\<tfm>\)
for /f "delims=" %%f in ('dir /b /s "%ROOT%\demo\bin\SR2DDemo.exe" 2^>nul ^| findstr /i "\\Release\\"') do set "BENCH_EXE=%%f"
if not defined BENCH_EXE goto :bench_noexe
for %%f in ("%BENCH_EXE%") do set "BENCH_DIR=%%~dpf"
if exist "%ROOT%\native\bin\x64\SR2D64.dll" copy /y "%ROOT%\native\bin\x64\SR2D64.dll" "%BENCH_DIR%SR2D64.dll" >nul
if not exist "%BENCH_DIR%SR2D64.dll" goto :bench_nodll
set "STEP_BENCH=OK  %BENCH_EXE%"
goto :summary

:no_dotnet
echo   ERROR: dotnet SDK not found on PATH.
set "STEP_BENCH=FAILED - dotnet not found"
set "FAILED=1"
goto :summary

:bench_failed
set "STEP_BENCH=FAILED - build errors, see above"
set "FAILED=1"
goto :summary

:bench_noexe
set "STEP_BENCH=FAILED - built, but SR2DDemo.exe not found under demo\bin"
set "FAILED=1"
goto :summary

:bench_nodll
set "STEP_BENCH=FAILED - built, but no SR2D64.dll next to the exe"
set "FAILED=1"
goto :summary

rem ---------------------------------------------------------------- summary
:summary
echo.
echo ============================================================================
echo  native DLL : %STEP_DLL%
echo  copy DLL   : %STEP_COPY%
echo  demo       : %STEP_BENCH%
echo ----------------------------------------------------------------------------
if "%FAILED%"=="0" echo  RESULT: BUILD OK - run %BENCH_EXE%
if not "%FAILED%"=="0" echo  RESULT: BUILD FAILED - see the step^(s^) marked FAILED above
echo ============================================================================
echo.
if /i "%~1"=="nopause" goto :end
if /i "%~2"=="nopause" goto :end
rem pause only when started by double-click (the console would vanish otherwise)
echo %cmdcmdline% | find /i "%~0" >nul && pause
:end
endlocal & exit /b %FAILED%
