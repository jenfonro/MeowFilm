import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { installerFixture } from './windows-installer-fixture.mjs';
import { windowsRoot } from '../scripts/windows/working-paths.mjs';

// Explicit Inno-only check: run this file with node --test after installing
// Inno Setup. Ordinary Windows Node tests do not acquire an Inno dependency.
const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const quote = value => "'" + value.replaceAll("'", "''") + "'";

test('real Inno compiles the source PE numeric versions and canonical version text without installing it', t => {
  const f = installerFixture(t);
  const source = join(f.payload, 'meowfilm.exe');
  copyFileSync(join(process.env.SystemRoot, 'System32/whoami.exe'), source);
  const icon = join(f.root, 'apps/windows/app/Assets/AppIcon.ico');
  mkdirSync(dirname(icon), { recursive: true });
  copyFileSync(join(windowsRoot, 'apps/windows/app/Assets/AppIcon.ico'), icon);
  const script = `
    $ErrorActionPreference = 'Stop'
    . ${quote(join(windowsRoot, 'scripts/windows/paths.ps1'))}
    . ${quote(join(windowsRoot, 'scripts/windows/build-installer.ps1'))}
    $WindowsRoot = ${quote(f.root)}
    $WindowsAppRoot = Join-Path $WindowsRoot 'apps/windows'
    $WindowsTmpRoot = Join-Path $WindowsRoot 'tmp'
    $WindowsNode = ${quote(process.execPath)}
    $Compiler = Resolve-WindowsInstallerCompiler
    $Candidate = New-WindowsInstaller -Payload ${quote(f.payload)} -Compiler $Compiler
    $InputVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(${quote(source)})
    $OutputVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Candidate)
    $Metadata = [ordered]@{
      candidate = $Candidate
      source = @{
        fileNumeric = "$($InputVersion.FileMajorPart).$($InputVersion.FileMinorPart).$($InputVersion.FileBuildPart).$($InputVersion.FilePrivatePart)"
        productNumeric = "$($InputVersion.ProductMajorPart).$($InputVersion.ProductMinorPart).$($InputVersion.ProductBuildPart).$($InputVersion.ProductPrivatePart)"
      }
      compiled = @{
        fileNumeric = "$($OutputVersion.FileMajorPart).$($OutputVersion.FileMinorPart).$($OutputVersion.FileBuildPart).$($OutputVersion.FilePrivatePart)"
        productNumeric = "$($OutputVersion.ProductMajorPart).$($OutputVersion.ProductMinorPart).$($OutputVersion.ProductBuildPart).$($OutputVersion.ProductPrivatePart)"
        fileText = $OutputVersion.FileVersion
        productText = $OutputVersion.ProductVersion
      }
    }
    Write-Output ('INNO-RESULT:' + ($Metadata | ConvertTo-Json -Compress -Depth 5))
  `;
  const result = spawnSync(powershell,
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
    { cwd: f.root, windowsHide: true, encoding: 'utf8', timeout: 90_000, maxBuffer: 8 * 1024 * 1024 });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  const match = /^INNO-RESULT:(.+)$/m.exec(result.stdout);
  assert.ok(match, result.stdout);
  const metadata = JSON.parse(match[1]);
  assert.match(metadata.source.fileNumeric, /^\d+\.\d+\.\d+\.\d+$/);
  assert.match(metadata.source.productNumeric, /^\d+\.\d+\.\d+\.\d+$/);
  assert.equal(metadata.compiled.fileNumeric, metadata.source.fileNumeric);
  assert.equal(metadata.compiled.productNumeric, metadata.source.productNumeric);
  assert.equal(metadata.compiled.fileText.trim(), metadata.source.fileNumeric);
  assert.equal(metadata.compiled.productText.trim(), metadata.source.productNumeric);
  assert.equal(metadata.candidate, join(f.root, 'tmp/windows-installer/meowfilm-setup.exe'));
  assert.ok(existsSync(metadata.candidate));
  assert.equal(existsSync(join(f.root, 'build/windows-installer/meowfilm-setup.exe')), false);
});
