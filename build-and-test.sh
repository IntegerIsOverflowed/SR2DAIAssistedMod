#!/usr/bin/env bash
# SR2D - one entry point that builds and tests everything on Linux (the Windows twin is build-and-test.bat).
# Native: the differential suite (new engine vs tests/ref originals) + regchk, on the committed Makefile.
# Managed: every headless runner under tests/cs plus the demo and the template, analyzers on.
# Exits non-zero on the first red gate.
set -uo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="$(dirname "$(command -v dotnet)")"   # the apphosts are executed directly below
export NUGET_PACKAGES=/home/user/.cache/nuget                    # keep the ~100 MB package cache out of the workspace
dotnet build-server shutdown > /dev/null 2>&1 || true      # the MSBuild nodes eat the RAM the native build needs

PASS=0; FAIL=0
step() { # step <name> <cmd...>
  local name="$1"; shift
  echo "==== $name"
  if "$@"; then PASS=$((PASS+1)); else echo "!!!! FAILED: $name"; FAIL=$((FAIL+1)); fi
}
buildwarn() { # buildwarn <dotnet build args...> - the build must be WARNING-FREE (step judges the exit code).
  # Allowed: the "SR2D64.dll not found" advisory (intentional on a Linux checkout) and MSB3539
  # (the BaseIntermediateOutputPath redirect of the runner csprojs, benign, documented).
  local out; out="$(dotnet build "$@" 2>&1)"; local rc=$?
  echo "$out" | tail -2
  local dirty; dirty="$(echo "$out" | grep -E "warning" | grep -vE "SR2D64.dll not found|MSB3539" || true)"
  if [ -n "$dirty" ]; then echo "$dirty" | head -10; echo "!!!! warnings are not allowed here"; return 1; fi
  return $rc
}
runlog() { # runlog <name> <cmd...>  - run, print the last lines, judge by the exit code
  local name="$1"; shift
  echo "==== $name"
  local out; out="$("$@" 2>&1)"; local rc=$?
  echo "$out" | tail -3
  if [ $rc -eq 0 ]; then PASS=$((PASS+1)); else echo "!!!! FAILED: $name"; FAIL=$((FAIL+1)); fi
}

# native: build + full differential suite (includes regchk)
step "native clean build" make -C tests clean so
step "native suite"       make -C tests test

# managed gates: the runners (one dotnet build per project - MSB1008 refuses two)
export DOTNET_NOLOGO=1
for proj in tests/cs/ctlrun tests/cs/benchrun tests/cs/vecrun tests/cs/selchk \
            tests/cs/autochk tests/cs/benchchk tests/cs/codechk tests/cs/fontchk tests/cs/webpchk \
            tests/cs/outbench tests/cs/legacybench; do
  [ -d "$proj" ] || continue
  step "build $proj" dotnet build "$proj" -c Release --no-incremental
done
# file-list / template drift guard
step "projchk" python3 tests/cs/projchk.py

# app + template, analyzers on, both implicit-usings modes for the demo
step "build demo (Implicit off)"  buildwarn demo -c Release --no-incremental -p:Implicit=disable \
     -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended
step "build demo (Implicit on)"   buildwarn demo -c Release --no-incremental -p:Implicit=enable \
     -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended
step "build EmptySR2DFormTemplate" buildwarn EmptySR2DFormTemplate -c Release --no-incremental \
     -p:EnableWindowsTargeting=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended

# run the headless suites (each copies the fresh native build next to itself first)
mkdir -p /home/user/.cache/ctlrun-out
for r in ctlrun benchrun; do
  ( cd /home/user/.cache/$r/bin/Release/net10.0 2>/dev/null && cp ../../../../../SR2D/tests/build/libSR2D64.so SR2D64.so )
  runlog "run $r" /home/user/.cache/$r/bin/Release/net10.0/$r
done
cp tests/build/libSR2D64.so tests/cs/selchk/bin/Release/net10.0/SR2D64.so
runlog "run selchk" tests/cs/selchk/bin/Release/net10.0/selchk
cp tests/build/libSR2D64.so /home/user/.cache/vecrun/bin/Release/net10.0/SR2D64.so
runlog "run vecrun anim (SMIL: morph / masks / motion / frames)" env VECANIM=1 /home/user/.cache/vecrun/bin/Release/net10.0/vecrun /home/user/.cache/ctlrun-out

echo "=================================="
echo "gates passed: $PASS, failed: $FAIL"
[ $FAIL -eq 0 ] && echo "ALL GREEN" || echo "RED - see the !!!! lines above"
exit $FAIL

# the runners drop their preview artifacts next to the repo root - not part of the tree
rm -f sel_preview.raw layers_preview.raw vox_preview.raw vox_night.raw vox_edit.raw objs.vox
