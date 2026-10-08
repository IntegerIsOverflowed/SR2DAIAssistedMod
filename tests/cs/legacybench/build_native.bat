@echo off
rem =====================================================================
rem Build the ORIGINAL SR2D engine sources (legacy/original-engine/*.cpp)
rem into SR2DOLD64.dll (export names unchanged, only the file name differs).
rem Direct cl.exe build: the legacy SR2D.vcxproj pins toolset v141 and SDK
rem 8.1, neither of which is installed here (only MSVC 14.51.36231 + SDK
rem 10.0.26100.0).
rem Options mirror the legacy project's Release|x64 settings: MaxSpeed(/O2),
rem IntrinsicFunctions(default on with /O2), MultiThreaded CRT(/MT), no /GL
rem (the old project does not enable WholeProgramOptimization).
rem The only non-default link input is libvcruntime.lib: the legacy sources
rem reference a compiler-generated memset (Transform.cpp RESIZE, Bump.cpp)
rem and this toolchain ships memset in libvcruntime.lib, not libcmt.lib, so
rem the auto-added default libs do not cover it.
rem Run from Git Bash:  cmd //c "build_native.bat"
rem =====================================================================
setlocal
call "D:\ProgramsHDD\VisualStudioCommunity\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
cd /d D:\SR2Dlastdump\workspace\SR2D\tests\cs\legacybench\obj_native || exit /b 1
set SRC=D:\SR2Dlastdump\workspace\SR2D\legacy\original-engine
cl /nologo /LD /O2 /MT /GS- /W3 ^
  "%SRC%\SR2D.cpp" "%SRC%\Filter.cpp" "%SRC%\Transform.cpp" "%SRC%\Bump.cpp" "%SRC%\Vector.cpp" ^
  /Fe:SR2DOLD64.dll ^
  /link /DEF:"%SRC%\SR2D.def" /INCREMENTAL:NO /DEFAULTLIB:libvcruntime.lib %EXTRALINK% || exit /b 1
echo BUILD_OK
dir SR2DOLD64.dll
