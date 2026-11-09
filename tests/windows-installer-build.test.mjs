import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { installerFixture } from './windows-installer-fixture.mjs';
import { windowsRoot } from '../scripts/windows/working-paths.mjs';

const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const quote = value => "'" + value.replaceAll("'", "''") + "'";
const original = 'last valid installer bytes';
function fixture(t, { compiler = 'failure' } = {}) {
  const f = installerFixture(t);
  // PowerShell creates a synthetic PE with distinct numeric file/product
  // versions. It is never executed or installed.
  const source = join(f.payload, 'meowfilm.exe');
  rmSync(source);
  const icon = join(f.root, 'apps/windows/app/Assets/AppIcon.ico');
  mkdirSync(dirname(icon), { recursive: true });
  copyFileSync(join(windowsRoot, 'apps/windows/app/Assets/AppIcon.ico'), icon);
  const destination = join(f.root, 'build/windows-installer/meowfilm-setup.exe');
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, original);
  const compilerScript = join(f.root, 'fixture-compiler.ps1');
  const marker = join(f.root, 'compiler-invoked.json');
  const candidate = join(f.root, 'tmp/windows-installer/meowfilm-setup.exe');
  writeFileSync(compilerScript, `
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$CompilerArguments)
$CompilerArguments | ConvertTo-Json | Set-Content -LiteralPath ${quote(marker)}
${compiler === 'empty' ? '' : compiler === 'corrupt'
    ? `[IO.File]::WriteAllText(${quote(candidate)}, 'invalid PE')`
    : `Copy-Item -LiteralPath ${quote(join(f.payload, 'meowfilm.exe'))} -Destination ${quote(candidate)}`}
$global:LASTEXITCODE = ${compiler === 'failure' ? 23 : 0}
`);
  const run = code => {
    const script = `
      $ErrorActionPreference = 'Stop'
      . ${quote(join(windowsRoot, 'scripts/windows/paths.ps1'))}
      . ${quote(join(windowsRoot, 'scripts/windows/build-installer.ps1'))}
      $WindowsRoot = ${quote(f.root)}
      $WindowsAppRoot = Join-Path $WindowsRoot 'apps/windows'
      $WindowsTmpRoot = Join-Path $WindowsRoot 'tmp'
      $WindowsNode = ${quote(process.execPath)}
      Add-Type -TypeDefinition @'
using System.Reflection;
[assembly: AssemblyVersion("1.2.3.4")]
[assembly: AssemblyFileVersion("1.2.3.4")]
[assembly: AssemblyInformationalVersion("5.6.7.8")]
public static class InstallerVersionFixture
{
    public static void Main() { }
}
'@ -OutputAssembly ${quote(source)} -OutputType ConsoleApplication
      ${code}
    `;
    const result = spawnSync(powershell,
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
      { cwd: f.root, windowsHide: true, encoding: 'utf8', timeout: 30_000 });
    assert.ifError(result.error);
    return result;
  };
  return { ...f, run, destination, candidate, marker, icon,
    compile: `New-WindowsInstaller -Payload ${quote(f.payload)} -Compiler ${quote(compilerScript)}` };
}

test('missing Inno compiler is an explicit error without touching the last valid installer', t => {
  const f = fixture(t);
  const result = f.run(`
    $env:PATH = $WindowsRoot
    $env:ProgramFiles = $WindowsRoot
    \${env:ProgramFiles(x86)} = $WindowsRoot
    $env:LOCALAPPDATA = $WindowsRoot
    Resolve-WindowsInstallerCompiler
  `);
  assert.equal(result.status, 1);
  assert.match(result.stderr.replace(/\r?\n/g, ''), /Inno Setup 6 is required only for -Installer/);
  assert.equal(readFileSync(f.destination, 'utf8'), original);
  assert.equal(existsSync(f.marker), false);
});

for (const compiler of ['failure', 'empty', 'corrupt']) {
  test(`installer ${compiler} compiler output does not replace the previous installer`, t => {
    const f = fixture(t, { compiler });
    const result = f.run(`$Candidate = ${f.compile}; Publish-WindowsInstaller -Candidate $Candidate`);
    assert.equal(result.status, 1);
    assert.match(result.stderr.replace(/\r?\n/g, ''), compiler === 'failure' ? /compilation failed.*exit 23/
      : compiler === 'empty' ? /did not produce meowfilm-setup.exe/ : /differs from the published EXE/);
    assert.ok(existsSync(f.marker), 'the compiler ran');
    assert.equal(readFileSync(f.destination, 'utf8'), original);
  });
}

test('incomplete installer content fails before invoking the compiler and preserves the previous installer', t => {
  const f = fixture(t, { compiler: 'success' });
  f.write('runtime/obsolete-runner.mjs');
  const result = f.run(`$Candidate = ${f.compile}; Publish-WindowsInstaller -Candidate $Candidate`);
  assert.equal(result.status, 1);
  assert.match(result.stderr.replace(/\r?\n/g, ''), /payload validation failed/);
  assert.equal(existsSync(f.marker), false);
  assert.equal(readFileSync(f.destination, 'utf8'), original);
});

test('a complete compiler candidate stays staged until publication and uses the fixed installer output', t => {
  const f = fixture(t, { compiler: 'success' });
  const result = f.run(`
    $Candidate = ${f.compile}
    if ($Candidate -isnot [string]) { throw 'Compiler logs were mixed into the returned candidate path.' }
    if ([IO.File]::ReadAllText(${quote(f.destination)}) -ne ${quote(original)}) { throw 'The old installer changed before publication.' }
    Publish-WindowsInstaller -Candidate $Candidate
  `);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readFileSync(f.destination), readFileSync(join(f.payload, 'meowfilm.exe')));
  assert.equal(existsSync(f.candidate), false);
  const args = JSON.parse(readFileSync(f.marker, 'utf8').replace(/^\uFEFF/u, ''));
  assert.ok(args.includes(`/DPayloadDir=${f.payload}`));
  assert.ok(args.includes(`/O${join(f.root, 'tmp/windows-installer')}`));
  assert.ok(args.includes('/Fmeowfilm-setup'));
});

test('failed atomic publication leaves the previous installer byte-for-byte unchanged', t => {
  const f = fixture(t, { compiler: 'success' });
  const result = f.run(`
    $Candidate = ${f.compile}
    $Held = [IO.File]::Open(${quote(f.destination)}, 'Open', 'ReadWrite', 'None')
    try { Publish-WindowsInstaller -Candidate $Candidate }
    finally { $Held.Dispose() }
  `);
  assert.equal(result.status, 1);
  assert.equal(readFileSync(f.destination, 'utf8'), original);
  assert.ok(existsSync(f.candidate), 'failed publication did not consume the staged candidate');
});
