#!/usr/bin/env python3
# Verifies demo/SR2DDemo.csproj lists every cs/*.cs file (the demo csproj enumerates its sources
# explicitly; a new cs file that is missing there builds fine in the glob-based headless checks
# but breaks build_release.bat on Windows).
import os, re, sys
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
proj = open(os.path.join(root, 'demo', 'SR2DDemo.csproj')).read()
listed = set(re.findall(r'Compile Include="\.\.\\cs\\([^"]+)"', proj))
have = set(f for f in os.listdir(os.path.join(root, 'cs')) if f.endswith('.cs'))
missing = sorted(have - listed); stale = sorted(listed - have)
for m in missing: print('MISSING from demo/SR2DDemo.csproj:', m)
for s in stale: print('listed but not on disk:', s)
# the app template must stay a complete, self-consistent project: three sources + csproj + readme
tpl = os.path.join(root, 'template')
tpl_missing = [f for f in ('SR2DApp.csproj', 'Program.cs', 'MainForm.cs', 'MainForm.Designer.cs', 'README.txt') if not os.path.exists(os.path.join(tpl, f))]
for m in tpl_missing: print('MISSING from template/:', m)
tpl_proj = open(os.path.join(tpl, 'SR2DApp.csproj')).read() if not tpl_missing else ''
tpl_bad = tpl_proj and ('net10.0-windows' not in tpl_proj or '<PlatformTarget>x64</PlatformTarget>' not in tpl_proj or '$(Sr2dCs)\\*.cs' not in tpl_proj)
if tpl_bad: print('template/SR2DApp.csproj: expected net10.0-windows, x64 and the cs\\*.cs wildcard')
# the change log must have an entry for the newest cs file (a reminder, not a build gate)
log = open(os.path.join(root, 'CHANGELOG.txt')).read() if os.path.exists(os.path.join(root, 'CHANGELOG.txt')) else ''
newest = max(have, key=lambda f: os.path.getmtime(os.path.join(root, 'cs', f)))
if log and newest not in log: print(f'note: CHANGELOG.txt does not mention the most recently changed source cs/{newest} - add an entry?')
bad = missing or stale or tpl_missing or tpl_bad
print('projchk:', 'OK' if not bad else 'FAILED', f'({len(have)} cs files, {len(listed)} listed; template {"OK" if not (tpl_missing or tpl_bad) else "BROKEN"})')
sys.exit(1 if bad else 0)
