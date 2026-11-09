import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { assertOwnedPath, temporaryRoot, windowsRoot } from '../scripts/windows/working-paths.mjs';

const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const bootstrap = "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bootstrap-marker'), 'ready')";
const build = `
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$FrameworkDependent,
    [switch]$Installer
)
& (Join-Path $PSScriptRoot 'bootstrap.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'bootstrap-marker'))) { throw 'Bootstrap was not called first.' }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'build-marker'), 'called')
[ordered]@{configuration=$Configuration; frameworkDependent=[bool]$FrameworkDependent; installer=[bool]$Installer} | ConvertTo-Json -Compress
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
  assert.deepEqual(JSON.parse(result.stdout), { configuration: 'Release', frameworkDependent: false, installer: false });
}));

test('Windows build entry forwards the existing configuration and framework-dependent switches', () => fixture(result => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { configuration: 'Debug', frameworkDependent: true, installer: false });
}, { args: ['-Configuration', 'Debug', '-FrameworkDependent'] }));

test('Windows build entry forwards the explicit installer switch', () => fixture(result => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { configuration: 'Release', frameworkDependent: false, installer: true });
}, { args: ['-Installer'] }));

test('Windows build entry stops on dependency preparation errors without invoking compilation', () => fixture((result, scripts) => {
  assert.equal(result.status, 1);
  // Windows PowerShell wraps errors at the host width, including inside a word.
  assert.match(result.stderr.replace(/\r?\n/g, ''), /bootstrap-fixture-failure/);
  assert.equal(existsSync(join(scripts, 'build-marker')), false);
}, { prepare: "Write-Error 'bootstrap-fixture-failure'" }));

test('Windows build entry propagates compilation failure as a nonzero process exit', () => fixture((result, scripts) => {
  assert.equal(result.status, 1);
  assert.match(result.stderr, /build-fixture-failure/);
  assert.equal(existsSync(join(scripts, 'bootstrap-marker')), true);
}, { compile: "& (Join-Path $PSScriptRoot 'bootstrap.ps1'); throw 'build-fixture-failure'" }));

test('Windows build entry preserves the exact inner compilation exit code', () => fixture((result, scripts) => {
  assert.equal(result.status, 23, result.stderr);
  assert.equal(existsSync(join(scripts, 'bootstrap-marker')), true);
}, { compile: "& (Join-Path $PSScriptRoot 'bootstrap.ps1'); exit 23" }));

for (const args of [['-Installer', '-Configuration', 'Debug'], ['-Installer', '-FrameworkDependent']]) {
  test(`Windows installer rejects unsupported ${args.join(' ')} before dependency preparation`, () => fixture((result, scripts) => {
    assert.equal(result.status, 1);
    assert.match(result.stderr.replace(/\r?\n/g, ''), /installers require Release, self-contained win-x64/);
    assert.equal(existsSync(join(scripts, 'bootstrap-marker')), false);
  }, { args, compile: readFileSync(join(windowsRoot, 'scripts/windows/build.ps1'), 'utf8') }));
}

for (const failStage of ['', 'verify-dependency-lock', 'test-root', 'ensure-runtime-native']) {
  test(`Windows Node entry selects its platform and ${failStage ? `propagates ${failStage} failure` : 'completes the gate'}`, t => {
    mkdirSync(temporaryRoot, { recursive: true });
    const root = mkdtempSync(join(temporaryRoot, 'windows-node-entry-'));
    t.after(() => rmSync(assertOwnedPath(temporaryRoot, root), { recursive: true, force: true }));
    const scripts = join(root, 'scripts/windows');
    mkdirSync(scripts, { recursive: true });
    copyFileSync(join(windowsRoot, 'scripts/windows/test-node.ps1'), join(scripts, 'test-node.ps1'));
    writeFileSync(join(scripts, 'environment.ps1'), `
      $ErrorActionPreference = 'Stop'
      $WindowsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
      $WindowsTestResults = Join-Path $WindowsRoot 'results'
      $WindowsNode = 'fixture-node'
      function New-WindowsDirectory($Path) { [IO.Directory]::CreateDirectory($Path) | Out-Null; return $Path }
      function Invoke-WindowsLoggedTool($Executable, $Arguments, $Log) {
        $Stage = [IO.Path]::GetFileNameWithoutExtension($Arguments[0])
        $Record = [ordered]@{stage=$Stage; arguments=@($Arguments | Select-Object -Skip 1)}
        [IO.File]::WriteAllText((Join-Path $WindowsTestResults ($Stage + '.json')), ($Record | ConvertTo-Json -Compress))
        if ($Stage -eq '${failStage}') { return 23 }
        return 0
      }
    `);
    const result = spawnSync(powershell,
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', join(scripts, 'test-node.ps1'), '-All'],
      { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.ifError(result.error);
    assert.equal(result.status, failStage ? 23 : 0, result.stderr);
    for (const stage of ['ensure-runtime-native', 'verify-dependency-lock', 'test-packages', 'test-root', 'verify-boundaries']) {
      if (failStage === 'ensure-runtime-native' && stage !== failStage ||
          failStage === 'verify-dependency-lock' && !['ensure-runtime-native', failStage].includes(stage)) {
        assert.equal(existsSync(join(root, 'results', `${stage}.json`)), false);
        continue;
      }
      const record = JSON.parse(readFileSync(join(root, 'results', `${stage}.json`), 'utf8'));
      assert.equal(record.stage, stage);
      assert.deepEqual(record.arguments, stage === 'test-root' ? ['--platform', 'windows'] : []);
    }
  });
}

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
