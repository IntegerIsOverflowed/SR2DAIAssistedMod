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
# B-05: the README coverage table must list every check project under tests/cs (a new runner that is
# missing from the table is a failure, not a silent gap - no CI needed to catch it)
readme = open(os.path.join(root, 'README.md')).read()
table_at = readme.index('### What every check covers')
table_end = readme.index('\n## ', table_at)   # '\n## ': '### ...' itself contains '## ' at offset 0
table = readme[table_at:table_end]
check_dirs = sorted(d for d in os.listdir(os.path.join(root, 'tests', 'cs'))
                    if os.path.isdir(os.path.join(root, 'tests', 'cs', d)) and not d.startswith('.'))
loose = [f for f in os.listdir(os.path.join(root, 'tests', 'cs')) if f.endswith('.csproj')]
for d in check_dirs:
    if any(f.endswith('.csproj') for f in os.listdir(os.path.join(root, 'tests', 'cs', d))):
        # match the full 'tests/cs/<name>' row form - a bare mention of the name anywhere else must not count
        if f'tests/cs/{d}' not in table: print(f'MISSING from the README check-coverage table: tests/cs/{d}')
for f in loose:
    if f'tests/cs/{f[:-6]}' not in table and f[:-6] not in table:   # curvetest.csproj sits directly in tests/cs/
        print(f'MISSING from the README check-coverage table: tests/cs/{f[:-6]}')
tbl_missing = [x for x in check_dirs if f'tests/cs/{x}' not in table] + [f[:-6] for f in loose if f'tests/cs/{f[:-6]}' not in table and f[:-6] not in table]

# the app template must stay a complete, self-consistent project (the empty SpriteForm template since 2026-10-08)
tpl = os.path.join(root, 'EmptySR2DFormTemplate')
tpl_missing = [f for f in ('EmptySR2DFormTemplate.csproj', 'Form1.cs', 'Form1.Designer.cs', 'Form1.resx', 'Program.cs', 'README.txt') if not os.path.exists(os.path.join(tpl, f))]
for m in tpl_missing: print('MISSING from EmptySR2DFormTemplate/:', m)
tpl_proj = open(os.path.join(tpl, 'EmptySR2DFormTemplate.csproj')).read() if not tpl_missing else ''
tpl_bad = tpl_proj and ('net10.0-windows' not in tpl_proj or '<PlatformTarget>x64</PlatformTarget>' not in tpl_proj or '$(Sr2dCs)\\*.cs' not in tpl_proj)
if tpl_bad: print('EmptySR2DFormTemplate/EmptySR2DFormTemplate.csproj: expected net10.0-windows, x64 and the cs\\*.cs wildcard')
# the change log must have an entry for the newest cs file (a reminder, not a build gate)
log = open(os.path.join(root, 'CHANGELOG.txt')).read() if os.path.exists(os.path.join(root, 'CHANGELOG.txt')) else ''
newest = max(have, key=lambda f: os.path.getmtime(os.path.join(root, 'cs', f)))
if log and newest not in log: print(f'note: CHANGELOG.txt does not mention the most recently changed source cs/{newest} - add an entry?')
bad = missing or stale or tpl_missing or tpl_bad or tbl_missing
print('projchk:', 'OK' if not bad else 'FAILED', f'({len(have)} cs files, {len(listed)} listed; empty template {"OK" if not (tpl_missing or tpl_bad) else "BROKEN"})')
sys.exit(1 if bad else 0)
