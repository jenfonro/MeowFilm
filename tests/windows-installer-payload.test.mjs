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
  f.write('libvlc/plugins/video output/libdirect3d11_plugin.dll');
  const result = f.prepare();
  const lines = readFileSync(join(f.output, 'meowfilm-install-files.txt'), 'utf8').trim().split('\n');
  assert.equal(lines[0], 'meowfilm-installer-files-v1');
  assert.equal(lines.length, result.files + 2);
  assert.equal(new Set(lines).size, lines.length);
  for (const path of ['meowfilm.exe', 'libvlc/plugins/video output/libdirect3d11_plugin.dll',
    'runtime/native/engine.node', ...licenseFiles.map(name => `licenses/${name}`), 'meowfilm-install-files.txt']) {
    assert.ok(lines.includes(path), path);
  }
  const include = readFileSync(join(f.output, 'payload-files.iss'), 'utf8');
  assert.ok(include.includes('Source: "{#PayloadDir}\\libvlc\\plugins\\video output\\libdirect3d11_plugin.dll"; DestDir: "{app}\\libvlc\\plugins\\video output"; Flags: ignoreversion'));
  assert.equal(include.split('\n').filter(Boolean).length, result.files);
  assert.ok(!include.includes('recursesubdirs'));
  assert.ok(!existsSync(join(f.payload, 'meowfilm-install-files.txt')), 'packaging does not add files to the runtime closure');
  assert.equal(JSON.parse(readFileSync(join(f.payload, 'build-result.json'))).status, 'built-not-accepted');
});

for (const [name, change] of [
  ['Debug', f => f.change('build-result.json', result => { result.configuration = 'Debug'; })],
  ['framework-dependent', f => f.change('build-result.json', result => { result.selfContained = false; })],
  ['wrong architecture', f => f.change('build-result.json', result => { result.runtimeIdentifier = 'win-arm64'; })],
  ['missing native runtime', f => rmSync(join(f.payload, 'coreclr.dll'))],
  ['missing native dependency', f => rmSync(join(f.payload, 'libvlc.dll'))],
  ['missing managed dependency', f => rmSync(join(f.payload, 'Example.dll'))],
  ['missing satellite dependency', f => rmSync(join(f.payload, 'zh-Hans/Example.resources.dll'))],
  ['missing runtime entry', f => f.change('runtime/runtime-manifest.json', result => { result.entry = 'absent.mjs'; })],
  ['runtime corruption', f => f.write('runtime/node.exe', 'changed')],
  ['stale runtime file', f => f.write('runtime/old-runner.mjs')],
  ['incomplete runtime', f => f.change('runtime/runtime-manifest.json', result => { result.status = 'building'; })],
  ...[
    ['legacy five-module runtime', ['music', 'cat', 'source', 'tmdb', 'netease']],
    ['legacy seven-module runtime', ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film']],
    ['reordered App modules', ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'netease', 'film']],
  ].map(([name, modules]) => [name, f => f.change('runtime/runtime-manifest.json', result => { result.modules = modules; })]),
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
