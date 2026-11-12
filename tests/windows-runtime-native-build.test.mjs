import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { ensureRuntimeNative, runtimeNativeNeedsBuild } from '../scripts/windows/ensure-runtime-native.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

const policyPath = 'scripts/script-host-native.json';
const receiptPath = 'packages/script-host/build/Release/script-host-native.json';
const artifactPath = 'packages/script-host/build/Release/script_host_native.node';
const sdkPath = 'packages/script-host/build/Release/source-sdk.js';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

// Synthetic freshness records only; these files are never claimed as trusted
// native artifacts or passed to the packaging trust gate.
function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'windows-native-preparation-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, root), { recursive: true, force: true }));
  const write = (path, value) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), typeof value === 'string' ? value : JSON.stringify(value));
  };
  const policy = { schemaVersion: 1, abi: 1, napi: 8,
    build: { node: process.versions.node, nodeGyp: '11.5.0', esbuild: '0.28.1' },
    mandatoryInputs: ['packages/script-host/binding.gyp', 'scripts/quickjs/build-native.mjs',
      'scripts/dependency-download.mjs'],
    inputDirectories: ['packages/script-host/native'],
    patches: [{ path: 'packages/script-host/native/fixture.patch' }],
    nodeHeaders: { archivePath: 'tmp/build/runtime-native/node-headers.tar.gz' },
    sources: { quickjs: { archivePath: 'tmp/build/runtime-native/quickjs.tar.gz',
      license: { path: 'LICENSES/quickjs-ng-LICENSE.txt' },
      unicodeLicense: { path: 'LICENSES/Unicode-3.0.txt' } } } };
  write(policyPath, policy);
  for (const path of ['package.json', 'package-lock.json', 'packages/script-host/binding.gyp',
    'scripts/dependency-download.mjs',
    'scripts/quickjs/build-source-sdk.mjs', 'packages/script-host/native/binding.cc',
    'packages/script-host/native/fixture.patch', policy.nodeHeaders.archivePath,
    policy.sources.quickjs.archivePath, policy.sources.quickjs.license.path,
    policy.sources.quickjs.unicodeLicense.path])
    write(path, 'synthetic input');
  write('scripts/quickjs/build-native.mjs', `
    import { writeFileSync } from 'node:fs';
    writeFileSync('called.json', JSON.stringify(process.argv.slice(2)));
    process.exit(23);
  `);
  write(artifactPath, 'synthetic freshness artifact');
  write(sdkPath, 'synthetic freshness SDK');
  const record = path => {
    const bytes = readFileSync(join(root, path));
    return { path, bytes: bytes.length, sha256: hash(bytes) };
  };
  const receipt = { schemaVersion: 1, platform: process.platform, arch: process.arch, abi: 1, napi: 8,
    toolchain: { node: process.versions.node, nodeGyp: policy.build.nodeGyp, esbuild: policy.build.esbuild },
    inputs: [policyPath, 'package.json', 'package-lock.json', 'scripts/quickjs/build-source-sdk.mjs',
      ...policy.mandatoryInputs, 'packages/script-host/native/binding.cc',
      'packages/script-host/native/fixture.patch', policy.nodeHeaders.archivePath,
      policy.sources.quickjs.archivePath, policy.sources.quickjs.license.path,
      policy.sources.quickjs.unicodeLicense.path].map(record),
    artifact: record(artifactPath), sdk: record(sdkPath) };
  write(receiptPath, receipt);
  return { root, write, receipt };
}

test('native preparation reuses current build inputs and artifacts without invoking the builder', async t => {
  const f = fixture(t);
  assert.deepEqual(await ensureRuntimeNative(f), { built: false });
  assert.equal(existsSync(join(f.root, 'called.json')), false);
});

for (const [name, mutate] of [
  ['missing receipt', f => rmSync(join(f.root, receiptPath))],
  ['changed input', f => f.write('packages/script-host/native/binding.cc', 'changed input')],
  ['changed download helper', f => f.write('scripts/dependency-download.mjs', 'changed helper')],
  ['added native input', f => f.write('packages/script-host/native/new.cc', 'new source')],
  ['damaged artifact', f => f.write(artifactPath, 'damaged')],
  ['damaged SDK', f => f.write(sdkPath, 'damaged')],
  ['wrong architecture', f => f.write(receiptPath, { ...f.receipt, arch: 'wrong' })],
  ['wrong ABI', f => f.write(receiptPath, { ...f.receipt, abi: 2 })],
  ['wrong schema', f => f.write(receiptPath, { ...f.receipt, schemaVersion: 0 })],
  ['changed lock', f => f.write('package-lock.json', 'changed dependency graph')],
  ['changed upstream archive', f => f.write('tmp/build/runtime-native/quickjs.tar.gz', 'changed source')],
  ['changed license', f => f.write('LICENSES/quickjs-ng-LICENSE.txt', 'changed license')],
  ['changed Unicode license', f => f.write('LICENSES/Unicode-3.0.txt', 'changed Unicode license')],
  ['changed Node headers', f => f.write('tmp/build/runtime-native/node-headers.tar.gz', 'changed headers')],
  ['missing required archive record', f => f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'tmp/build/runtime-native/quickjs.tar.gz') })],
  ['missing required license record', f => f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'LICENSES/quickjs-ng-LICENSE.txt') })],
  ['missing required Unicode license record', f => f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'LICENSES/Unicode-3.0.txt') })],
  ['missing required headers record', f => f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'tmp/build/runtime-native/node-headers.tar.gz') })],
  ['missing required download helper record', f => f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'scripts/dependency-download.mjs') })],
  ['duplicate input', f => f.write(receiptPath, { ...f.receipt, inputs: [...f.receipt.inputs, f.receipt.inputs[0]] })],
  ['unsafe input', f => f.write(receiptPath, { ...f.receipt, inputs: [...f.receipt.inputs,
    { path: '../outside-input', sha256: hash('synthetic') }] })],
]) {
  test(`native preparation rebuilds ${name} and preserves the builder exit code`, async t => {
    const f = fixture(t);
    mutate(f);
    assert.equal(await runtimeNativeNeedsBuild(f.root), true);
    await assert.rejects(ensureRuntimeNative(f), { exitCode: 23 });
    assert.deepEqual(JSON.parse(readFileSync(join(f.root, 'called.json'))), []);
  });
}

test('native preparation rejects a successful command without current build evidence', async t => {
  const f = fixture(t);
  f.write('scripts/quickjs/build-native.mjs', '// exits successfully without building');
  await assert.rejects(ensureRuntimeNative(f), /produced no current receipt/);
});

test('native preparation requires the locked Node version before invoking any build', async t => {
  const f = fixture(t);
  const policy = JSON.parse(readFileSync(join(f.root, policyPath)));
  f.write(policyPath, { ...policy, build: { ...policy.build, node: '0.0.0' } });
  await assert.rejects(ensureRuntimeNative(f), /requires Node 0\.0\.0/);
  assert.equal(existsSync(join(f.root, 'called.json')), false);
});

for (const scenario of ['current', 'failed', 'missing-builder', 'wrong-node', 'no-output']) {
  test(`native preparation CLI ${scenario} preserves the real result without using another artifact`, t => {
    const f = fixture(t);
    f.write('scripts/windows/ensure-runtime-native.mjs',
      readFileSync(new URL('../scripts/windows/ensure-runtime-native.mjs', import.meta.url), 'utf8'));
    f.write('scripts/quickjs/native-inputs.mjs',
      readFileSync(new URL('../scripts/quickjs/native-inputs.mjs', import.meta.url), 'utf8'));
    if (scenario === 'failed') f.write(artifactPath, 'stale artifact');
    if (scenario === 'missing-builder') rmSync(join(f.root, 'scripts/quickjs/build-native.mjs'));
    if (scenario === 'wrong-node') {
      const policy = JSON.parse(readFileSync(join(f.root, policyPath)));
      f.write(policyPath, { ...policy, build: { ...policy.build, node: '0.0.0' } });
    }
    if (scenario === 'no-output') f.write('scripts/quickjs/build-native.mjs', '// no output');
    const result = spawnSync(process.execPath, [join(f.root, 'scripts/windows/ensure-runtime-native.mjs')], {
      cwd: f.root, encoding: 'utf8', windowsHide: true, timeout: 10000,
    });
    assert.ifError(result.error);
    assert.equal(result.status, scenario === 'current' ? 0 : scenario === 'failed' ? 23 : 1, result.stderr);
    assert.equal(result.stdout, '');
    if (scenario === 'current') assert.equal(result.stderr, '');
    if (scenario === 'failed') {
      assert.equal(result.stderr, 'Native runtime build failed (exit 23)\n');
      assert.deepEqual(JSON.parse(readFileSync(join(f.root, 'called.json'))), []);
    }
    if (scenario === 'missing-builder') {
      assert.match(result.stderr, /Cannot find module/);
      assert.match(result.stderr, /Native runtime build failed \(exit 1\)/);
    }
    if (scenario === 'wrong-node')
      assert.equal(result.stderr, `Native runtime requires Node 0.0.0; found ${process.versions.node}\n`);
    if (scenario === 'no-output') assert.equal(result.stderr, 'Native runtime build produced no current receipt\n');
    assert.equal(existsSync(join(f.root, 'called.json')), scenario === 'failed');
  });
}
