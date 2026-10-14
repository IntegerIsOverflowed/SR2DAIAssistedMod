#!/usr/bin/env python3
"""Portable DLL project items + output-only + designer-shadow-copy probes (.NET 10, no WinForms host required).
On Linux an ABI-compatible ELF engine is named SR2D64.dll, exercising the same explicit path/metadata wiring.
Actual Visual Studio designer painting still needs a Windows smoke test; this verifies discovery and safe failure.
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from xml.sax.saxutils import escape

REPO = Path(__file__).resolve().parents[3]
WORK = Path('/home/user/.cache/nativeload-fixtures')
ENGINE = REPO / 'tests/build/libSR2D64.so'
if not ENGINE.exists():
    raise SystemExit('native engine missing: make -C tests so first')
WORK.mkdir(parents=True, exist_ok=True)
ENV = os.environ.copy()
ENV.pop('SR2D_DLL', None)
ENV['DOTNET_NOLOGO'] = '1'
ENV['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
ENV['NUGET_PACKAGES'] = '/home/user/.cache/nuget'
ENV['DOTNET_CLI_HOME'] = '/home/user/.cache/dotnet-home'
checks = 0


def check(ok, label):
    global checks
    if not ok:
        raise AssertionError(label)
    checks += 1
    print('ok  ' + label, flush=True)


def run(command, cwd, expected=0):
    p = subprocess.run(command, cwd=cwd, env=ENV, text=True, stdout=subprocess.PIPE,
                       stderr=subprocess.STDOUT, timeout=120)
    if p.returncode != expected:
        raise AssertionError(f'failed ({p.returncode}): {command}\n{p.stdout[-6000:]}')
    return p.stdout


def build(case, items='', properties='', source=True):
    root = WORK / case
    if root.exists():
        shutil.rmtree(root)
    root.mkdir()
    if source:
        shutil.copy2(ENGINE, root / 'SR2D64.dll')
    project = f'''<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
 <AllowUnsafeBlocks>true</AllowUnsafeBlocks><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>
 <EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>probe</AssemblyName>{properties}</PropertyGroup>
 <ItemGroup><Compile Include="{escape(str(REPO / 'cs/SR2D.cs'))}" />
 <Compile Include="{escape(str(Path(__file__).parent / 'Program.cs'))}" />{items}</ItemGroup>
 <Import Project="{escape(str(REPO / 'cs/SR2D.Native.targets'))}" />
</Project>'''
    (root / 'probe.csproj').write_text(project)
    return root


def compile_probe(root, present=True):
    output = run(['dotnet', 'build', 'probe.csproj', '-c', 'Debug', '--no-incremental'], root)
    check(('SR2D0001' not in output) == present, root.name + ': warning reflects actual DLL availability, not native/ layout')
    other = [line for line in output.splitlines() if 'warning ' in line and 'SR2D0001' not in line]
    check(not other, root.name + ': no unrelated build warnings')
    assembly_info = (root / 'obj/Debug/net10.0/probe.AssemblyInfo.cs').read_text()
    check('SR2D.OutputDir' in assembly_info and 'SR2D.ProjectDir' in assembly_info,
          root.name + ': real output/project hints embedded in assembly')
    return root / 'bin/Debug/net10.0'


def shadow_probe(root, output, present=True, cwd=None):
    shadow = root / 'designer cache'
    shadow.mkdir(exist_ok=True)
    for file in output.glob('probe.*'):
        shutil.copy2(file, shadow / file.name)
    # Do NOT copy the engine into the shadow cache, like an out-of-process designer which copied just managed code.
    log = run(['dotnet', str(shadow / 'probe.dll')] + ([] if present else ['missing']), cwd or shadow)
    result = json.loads(log.splitlines()[-1])
    check(result['available'] == present, root.name + ': shadow-copy native probe matches expectation')
    if present:
        check(result['simd'] >= 0 and result['path'] is not None, root.name + ': ABI-checked engine loaded by original asset/output hint')
    else:
        check(bool(result['error']), root.name + ': missing / incompatible engine has a diagnostic and does not throw')
    return result


# The standard project root item (SDK default None): one source copy + normal output copy, no native/ anywhere.
root = build('root item')
out = compile_probe(root)
check((out / 'SR2D64.dll').read_bytes() == ENGINE.read_bytes(), 'root item: copied next to managed output')
shadow_probe(root, out)
(root / 'SR2D64.dll').unlink()
result = shadow_probe(root, out)
check(Path(result['path']) == out / 'SR2D64.dll', 'root item: output-directory hint works even AFTER source DLL is removed')

# A None item in an asset subfolder, explicitly marked Copy If Newer.
root = build('None asset', '<None Update="Assets/SR2D64.dll" CopyToOutputDirectory="PreserveNewest" />', source=False)
(root / 'Assets').mkdir(); shutil.copy2(ENGINE, root / 'Assets/SR2D64.dll')
out = compile_probe(root); shadow_probe(root, out)
check((out / 'SR2D64.dll').exists() and not (out / 'Assets/SR2D64.dll').exists(), 'None asset: one ROOT output DLL, not a second subfolder copy')

# A Content item (also normal Solution Explorer usage) without a predeclared native-source property.
root = build('Content asset', '<None Remove="Assets/SR2D64.dll" /><Content Include="Assets/SR2D64.dll" CopyToOutputDirectory="PreserveNewest" />', source=False)
(root / 'Assets').mkdir(); shutil.copy2(ENGINE, root / 'Assets/SR2D64.dll')
out = compile_probe(root); shadow_probe(root, out)
check((out / 'SR2D64.dll').exists() and not (out / 'Assets/SR2D64.dll').exists(), 'Content asset: one root output DLL too')

# Add Existing Item -> Add As Link to a DLL outside the project.
shared = WORK / 'shared DLL'; shared.mkdir(exist_ok=True); shutil.copy2(ENGINE, shared / 'SR2D64.dll')
root = build('linked asset', f'<None Include="{escape(str(shared / "SR2D64.dll"))}" Link="Dependencies/SR2D64.dll" CopyToOutputDirectory="PreserveNewest" />', source=False)
out = compile_probe(root); shadow_probe(root, out)
check((out / 'SR2D64.dll').exists() and not (out / 'Dependencies/SR2D64.dll').exists(), 'linked asset: source link remains convenient, output name stays SR2D64.dll')

# Exactly the user report: DLL ONLY in bin/Debug/net10.0, no native folder or source copy at all.
root = build('output only', source=False)
out = root / 'bin/Debug/net10.0'; out.mkdir(parents=True); shutil.copy2(ENGINE, out / 'SR2D64.dll')
out = compile_probe(root); shadow_probe(root, out)
check(not (root / 'SR2D64.dll').exists(), 'output only: did not require / create a project-root duplicate')

# Explicit override, even with a different source file name.
root = build('explicit override', properties='<Sr2dDll>Engine-x64.dll</Sr2dDll>', source=False)
shutil.copy2(ENGINE, root / 'Engine-x64.dll')
out = compile_probe(root); shadow_probe(root, out)

# Missing DLL: the build is valid with a useful warning; probing is safe and does not pick a planted CWD DLL.
root = build('missing engine', source=False)
out = compile_probe(root, present=False)
planted = WORK / 'untrusted current directory'; planted.mkdir(exist_ok=True)
shutil.copy2(ENGINE, planted / 'libSR2D64.so'); shutil.copy2(ENGINE, planted / 'SR2D64.dll')
shadow_probe(root, out, present=False, cwd=planted)
check(not (root / 'SR2D64.dll').exists(), 'missing engine: no arbitrary current-directory fallback')

# Found but ABI-incompatible DLL: never "available" just because a file exists.
root = build('bad ABI', source=False)
(root / 'bad.c').write_text('int SR2D_ABI_VERSION(void) { return 999; }\nint SR2D_SIMD_LEVEL(void) { return 1; }\n')
run(['gcc', '-shared', '-fPIC', 'bad.c', '-o', 'SR2D64.dll'], root)
out = compile_probe(root); result = shadow_probe(root, out, present=False)
check('ABI 999' in result['error'], 'bad ABI: incompatibility reason retained for designer placeholder / diagnosis')

# Compile the REAL template after moving it (not a synthetic csproj), with sibling and nested cs/ layouts.
for nested in (False, True):
    parent = WORK / ('portable nested' if nested else 'portable sibling')
    if parent.exists(): shutil.rmtree(parent)
    project = parent / 'My application'; shutil.copytree(REPO / 'EmptySR2DFormTemplate', project,
        ignore=shutil.ignore_patterns('bin', 'obj', '*.user'))
    sources = project / 'cs' if nested else parent / 'cs'
    shutil.copytree(REPO / 'cs', sources)
    shutil.copy2(ENGINE, project / 'SR2D64.dll')
    output = run(['dotnet', 'build', 'EmptySR2DFormTemplate.csproj', '-c', 'Debug', '--no-incremental',
                  '-p:EnableWindowsTargeting=true'], project)
    check('Build succeeded' in output and 'warning ' not in output,
          ('nested' if nested else 'sibling') + ' cs/: copied real template builds warning-free without native/')
    check((project / 'bin/Debug/net10.0-windows/SR2D64.dll').exists(), 'portable template: native asset copied to its actual build output')

print(f'nativeload: {checks} checks passed; project assets/output-only/shadow-copy/ABI/portable template', flush=True)
