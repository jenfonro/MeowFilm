import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  existsSync, linkSync, mkdirSync, readFileSync, readdirSync,
  rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { licenseFiles } from '../scripts/windows/package-licenses.mjs';
import { fingerprintInputs } from '../scripts/windows/input-fingerprint.mjs';
import { windowsRoot } from '../scripts/windows/working-paths.mjs';
import { installerFixture as fixture } from './windows-installer-fixture.mjs';

test('installer packages the entire publish closure and emits an explicit upgrade/uninstall ownership inventory', t => {
  const f = fixture(t);
  f.write('film-kernel/meowfilm_film_kernel.dll');
  f.write('assets/cover art/example.png');
  f.change('runtime/runtime-manifest.json', manifest => {
    manifest.modules.reverse();
    manifest.modules.push({ id: 'description-only', version: '1.0.0' });
  });
  const result = f.prepare();
  const lines = readFileSync(join(f.output, 'meowfilm-install-files.txt'), 'utf8').trim().split('\n');
  assert.equal(lines[0], 'meowfilm-installer-files-v1');
  assert.equal(lines.length, result.files + 2);
  assert.equal(new Set(lines).size, lines.length);
  for (const path of ['meowfilm.exe', 'film-kernel/meowfilm_film_kernel.dll', 'assets/cover art/example.png',
    'runtime/native/engine.node', ...licenseFiles.map(name => `licenses/${name}`), 'meowfilm-install-files.txt']) {
    assert.ok(lines.includes(path), path);
  }
  const include = readFileSync(join(f.output, 'payload-files.iss'), 'utf8');
  assert.ok(include.includes('Source: "{#PayloadDir}\\film-kernel\\meowfilm_film_kernel.dll"; DestDir: "{app}\\film-kernel"; Flags: ignoreversion'));
  assert.ok(include.includes('Source: "{#PayloadDir}\\assets\\cover art\\example.png"; DestDir: "{app}\\assets\\cover art"; Flags: ignoreversion'));
  assert.equal(include.split('\n').filter(Boolean).length, result.files);
  assert.ok(!include.includes('recursesubdirs'));
  assert.ok(!existsSync(join(f.payload, 'meowfilm-install-files.txt')), 'packaging does not add files to the runtime closure');
  assert.equal(JSON.parse(readFileSync(join(f.payload, 'build-result.json'))).status, 'built-not-accepted');
});

test('installer accepts an additional declared framework and retains its actual publish dependencies', t => {
  const f = fixture(t);
  f.change('meowfilm.runtimeconfig.json', config => {
    config.runtimeOptions.includedFrameworks.unshift({ name: 'Microsoft.WindowsDesktop.App', version: '10.0.1' });
  });
  f.change('meowfilm.deps.json', deps => {
    deps.targets['net10.0/win-x64']['DesktopRuntime/10.0.1'] = {
      runtime: { 'lib/DesktopRuntime.dll': {} },
    };
  });
  assert.throws(f.prepare, /publish dependency is missing lib\/DesktopRuntime.dll/);
  assert.deepEqual(readdirSync(f.output), []);
  f.write('DesktopRuntime.dll');
  f.prepare();
  assert.ok(readFileSync(join(f.output, 'meowfilm-install-files.txt'), 'utf8').split('\n').includes('DesktopRuntime.dll'));
});

for (const [name, change] of [
  ['Debug', f => f.change('build-result.json', result => { result.configuration = 'Debug'; })],
  ['framework-dependent', f => f.change('build-result.json', result => { result.selfContained = false; })],
  ['wrong architecture', f => f.change('build-result.json', result => { result.runtimeIdentifier = 'win-arm64'; })],
  ['missing native runtime', f => rmSync(join(f.payload, 'coreclr.dll'))],
  ['missing included .NET framework', f => f.change('meowfilm.runtimeconfig.json', config => {
    config.runtimeOptions.includedFrameworks = [];
  })],
  ['external framework in self-contained publish', f => f.change('meowfilm.runtimeconfig.json', config => {
    config.runtimeOptions.framework = config.runtimeOptions.includedFrameworks[0];
    delete config.runtimeOptions.includedFrameworks;
  })],
  ['unversioned framework', f => f.change('meowfilm.runtimeconfig.json', config => {
    config.runtimeOptions.includedFrameworks.push({ name: 'Microsoft.WindowsDesktop.App' });
  })],
  ['missing native dependency', f => rmSync(join(f.payload, 'libSkiaSharp.dll'))],
  ['missing managed dependency', f => rmSync(join(f.payload, 'Example.dll'))],
  ['missing satellite dependency', f => rmSync(join(f.payload, 'zh-Hans/Example.resources.dll'))],
  ['missing runtime entry', f => f.change('runtime/runtime-manifest.json', result => { result.entry = 'absent.mjs'; })],
  ['runtime corruption', f => f.write('runtime/node.exe', 'changed')],
  ['stale runtime file', f => f.write('runtime/old-runner.mjs')],
  ['incomplete runtime', f => f.change('runtime/runtime-manifest.json', result => { result.status = 'building'; })],
  ['license corruption', f => f.write(`licenses/${licenseFiles[0]}`, 'changed')],
  ['missing license', f => f.change('licenses/licenses-manifest.json', result => { result.files.pop(); })],
  ['installer metadata in payload', f => f.write('unins000.exe')],
]) {
  test(`installer rejects ${name} without emitting a partial installer inventory`, t => {
    const f = fixture(t); change(f);
    assert.throws(f.prepare, /Windows installer:/);
    assert.deepEqual(readdirSync(f.output), []);
  });
}

test('installer rejects symlinks, hard links, and escaping manifest paths', t => {
  const f = fixture(t);
  symlinkSync(join(f.root, 'missing-target'), join(f.payload, 'runtime/alias'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(f.prepare, /link or special/);
  rmSync(join(f.payload, 'runtime/alias'));
  linkSync(join(f.payload, 'meowfilm.exe'), join(f.payload, 'linked.exe'));
  assert.throws(f.prepare, /hard link/);
  rmSync(join(f.payload, 'linked.exe'));
  f.change('runtime/runtime-manifest.json', result => { result.files[0].path = '../meowfilm.exe'; });
  assert.throws(f.prepare, /unsafe payload path/);
  assert.deepEqual(readdirSync(f.output), []);
});

test('installer CLI rejects unknown/missing parameters with a real nonzero exit', t => {
  const f = fixture(t);
  for (const args of [[], ['--payload', f.payload], ['--payload', f.payload, '--unknown', f.output]]) {
    const result = spawnSync(process.execPath, [join(windowsRoot, 'scripts/windows/prepare-installer.mjs'), ...args],
      { cwd: f.root, encoding: 'utf8', timeout: 15000 });
    assert.ifError(result.error);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /usage:/);
    assert.equal(result.stdout, '');
  }
});

test('installer code, public entry, icon and lifecycle changes affect the existing Windows input fingerprint', t => {
  const f = fixture(t);
  const paths = ['build-windows.ps1', 'scripts/windows/build-installer.ps1', 'scripts/windows/prepare-installer.mjs',
    'scripts/windows/installer.iss', 'apps/windows/app/Assets/AppIcon.ico',
    'apps/windows/app/Program.cs', 'apps/windows/src/MeowFilm.AppHost/AppInstallationLifetime.cs'];
  for (const path of paths) {
    mkdirSync(dirname(join(f.root, path)), { recursive: true });
    writeFileSync(join(f.root, path), 'before');
  }
  const original = fingerprintInputs(f.root, paths);
  for (const path of paths) {
    writeFileSync(join(f.root, path), 'after');
    assert.notEqual(fingerprintInputs(f.root, paths), original, path);
    writeFileSync(join(f.root, path), 'before');
  }
});
