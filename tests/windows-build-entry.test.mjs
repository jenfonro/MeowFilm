import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { assertOwnedPath, temporaryRoot, windowsRoot } from '../scripts/windows/working-paths.mjs';

const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const bootstrap = "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bootstrap-marker'), 'ready')";
const build = `
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$FrameworkDependent
)
& (Join-Path $PSScriptRoot 'bootstrap.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'bootstrap-marker'))) { throw 'Bootstrap was not called first.' }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'build-marker'), 'called')
[ordered]@{configuration=$Configuration; frameworkDependent=[bool]$FrameworkDependent} | ConvertTo-Json -Compress
`;

function fixture(run, { args = [], prepare = bootstrap, compile = build } = {}) {
  assertOwnedPath(windowsRoot, temporaryRoot);
  const root = mkdtempSync(join(temporaryRoot, 'meowfilm build-entry-'));
  try {
    const scripts = join(root, 'scripts/windows'), caller = join(root, 'caller');
    mkdirSync(scripts, { recursive: true });
    mkdirSync(caller);
    copyFileSync(join(windowsRoot, 'build-windows.ps1'), join(root, 'build-windows.ps1'));
    writeFileSync(join(scripts, 'bootstrap.ps1'), prepare);
    writeFileSync(join(scripts, 'build.ps1'), compile);
    const result = spawnSync(powershell,
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', join(root, 'build-windows.ps1'), ...args],
      { cwd: caller, windowsHide: true, encoding: 'utf8', timeout: 30_000,
        env: { ...process.env, PSModulePath: join(dirname(powershell), 'Modules') } });
    assert.ifError(result.error);
    return run(result, scripts);
  } finally {
    assertOwnedPath(temporaryRoot, root);
    rmSync(root, { recursive: true, force: true });
  }
}

test('Windows build entry prepares dependencies then builds from its own directory, including spaces', () => fixture(result => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { configuration: 'Release', frameworkDependent: false });
}));

test('Windows build entry forwards the existing configuration and framework-dependent switches', () => fixture(result => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { configuration: 'Debug', frameworkDependent: true });
}, { args: ['-Configuration', 'Debug', '-FrameworkDependent'] }));

test('Windows build entry stops on dependency preparation errors without invoking compilation', () => fixture((result, scripts) => {
  assert.equal(result.status, 1);
  assert.match(result.stderr, /bootstrap-fixture-failure/);
  assert.equal(existsSync(join(scripts, 'build-marker')), false);
}, { prepare: "Write-Error 'bootstrap-fixture-failure'" }));

test('Windows build entry propagates compilation failure as a nonzero process exit', () => fixture((result, scripts) => {
  assert.equal(result.status, 1);
  assert.match(result.stderr, /build-fixture-failure/);
  assert.equal(existsSync(join(scripts, 'bootstrap-marker')), true);
}, { compile: "& (Join-Path $PSScriptRoot 'bootstrap.ps1'); throw 'build-fixture-failure'" }));

test('Windows build rejects a concurrent build before dependency preparation or installation', () => {
  const quote = value => "'" + value.replaceAll("'", "''") + "'";
  const script = `
    . ${quote(join(windowsRoot, 'scripts/windows/paths.ps1'))}
    $Lock = [IO.File]::Open((Join-Path $WindowsTmpRoot 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
      $ErrorActionPreference = 'Continue'
      $Output = & ${quote(powershell)} -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ${quote(join(windowsRoot, 'build-windows.ps1'))} 2>&1
      [ordered]@{exit=$LASTEXITCODE; error=($Output | Out-String)} | ConvertTo-Json -Compress
    } finally { $Lock.Dispose() }
  `;
  const result = JSON.parse(execFileSync(powershell, ['-NoProfile', '-NonInteractive', '-Command', script],
    { cwd: temporaryRoot, windowsHide: true, encoding: 'utf8', timeout: 30_000,
      env: { ...process.env, PSModulePath: join(dirname(powershell), 'Modules') } }));
  assert.equal(result.exit, 1);
  assert.match(result.error, /Another Windows build is using this checkout/);
});
