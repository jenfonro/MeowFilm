import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import {
  cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync,
  renameSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { packageWindowsRuntime } from '../scripts/windows/package-runtime.mjs';
import { prepareWindowsTestBundles } from '../scripts/windows/prepare-test-bundles.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const temporary = join(temporaryRoot, 'windows-runtime-package');
mkdirSync(temporary, { recursive: true });
const areas = [];
after(() => { for (const area of areas) rmSync(assertOwnedPath(temporary, area), { recursive: true, force: true }); });
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
const metadataFiles = [
  'modules/metadata-data.mjs', 'modules/metadata/douban.mjs', 'modules/metadata/network.mjs',
  'modules/metadata/settings.mjs', 'modules/metadata/tmdb.mjs',
];
function area() {
  const path = mkdtempSync(join(temporary, 'case-'));
  areas.push(path);
  return path;
}
function within(base, path) {
  const delta = relative(base, path);
  return delta !== '..' && !delta.startsWith(`..${sep}`) && !isAbsolute(delta);
}
function write(base, path, value) {
  const target = join(base, path);
  assert.ok(within(temporary, target));
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, typeof value === 'string' || Buffer.isBuffer(value) ? value : JSON.stringify(value, null, 2) + '\n');
}
function files(directory, prefix = '') {
  return readdirSync(join(directory, prefix), { withFileTypes: true }).flatMap(item => {
    assert.equal(item.isSymbolicLink(), false);
    const path = prefix ? `${prefix}/${item.name}` : item.name;
    return item.isDirectory() ? files(directory, path) : [path];
  }).sort();
}
function verifyManifest(output, manifest) {
  assert.deepEqual(readJSON(join(output, 'runtime-manifest.json')), manifest);
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.transport, 'windows-http-v1');
  assert.equal(manifest.platform, 'win32');
  assert.equal(manifest.entry, 'app/windows/entries/aggregate.mjs');
  assert.equal(manifest.assemblyProfile, 'common-native');
  assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'tmdb', 'netease']);
  const paths = manifest.files.map(file => file.path);
  assert.deepEqual(paths, [...paths].sort());
  assert.equal(new Set(paths.map(path => path.normalize('NFC').toLowerCase())).size, paths.length);
  assert.deepEqual(files(output), [...paths, 'runtime-manifest.json'].sort());
  assert.deepEqual(manifest.fileHashExcludes, ['runtime-manifest.json']);
  for (const file of manifest.files) {
    const bytes = readFileSync(join(output, file.path));
    assert.equal(bytes.length, file.bytes, file.path);
    assert.equal(sha256(bytes), file.sha256, file.path);
  }
  assert.deepEqual(paths.filter(path => path.startsWith('app/')), [
    'app/bootstrap.mjs', 'app/client.mjs', 'app/contracts.mjs', 'app/entry.mjs',
    'app/modules/cat-script.mjs', ...metadataFiles.map(file => `app/${file}`), 'app/modules/music-script.mjs',
    'app/modules/netease-token.mjs', 'app/modules/pan-runtime.mjs', 'app/modules/source-candidates.mjs', 'app/modules/source-runtime.mjs',
    'app/pan-entry.mjs',
    'app/windows/bootstrap.mjs', 'app/windows/entries/aggregate.mjs',
  ]);
}

function packagingFixture({
  missingBootstrap = false, extraSource = false, extraImport = false,
  missingShared = false, missingCommonModule = false, missingSourceModule = false, platformModule = false,
} = {}) {
  const fixtureArea = area(), source = join(fixtureArea, 'source');
  const manifest = { name: 'windows-package-fixture', version: '1.0.0', private: true, workspaces: ['packages/*'] };
  const lock = { name: manifest.name, version: manifest.version, lockfileVersion: 3, packages: { '': { ...manifest } } };
  const inert = 'throw new Error("Synthetic fixture must not execute");\n';
  write(source, 'package.json', manifest);
  for (const name of ['runtime-bridge', 'script-host', 'source-runtime', 'pan-runtime', 'netease-token', 'inactive-core']) {
    const location = `packages/${name}`, installed = `node_modules/@meowfilm/${name}`;
    const pkg = {
      name: `@meowfilm/${name}`, version: '1.0.0', type: 'module', license: 'MIT', exports: './src/index.js',
      ...(name === 'script-host' ? { dependencies: { '@meowfilm/source-runtime': '1.0.0' } } : {}),
    };
    write(source, `${location}/package.json`, pkg);
    if (!(missingShared && name === 'script-host')) write(source, `${location}/src/index.js`, inert);
    write(source, `${location}/LICENSE`, 'Synthetic fixture license\n');
    lock.packages[location] = { version: pkg.version, ...(pkg.dependencies ? { dependencies: pkg.dependencies } : {}) };
    lock.packages[installed] = { resolved: location, link: true };
    mkdirSync(dirname(join(source, installed)), { recursive: true });
    symlinkSync(join(source, location), join(source, installed), process.platform === 'win32' ? 'junction' : 'dir');
  }
  write(source, 'package-lock.json', lock);
  for (const file of ['bootstrap.mjs', 'entry.mjs', 'pan-entry.mjs', 'client.mjs', 'contracts.mjs',
    'modules/pan-runtime.mjs', 'modules/netease-token.mjs', 'modules/source-candidates.mjs'])
    write(source, `apps/runtime/${file}`, inert);
  if (!missingCommonModule) {
    write(source, 'apps/runtime/modules/music-script.mjs', inert);
    write(source, 'apps/runtime/modules/cat-script.mjs', inert);
  }
  if (!missingSourceModule) write(source, 'apps/runtime/modules/source-runtime.mjs', inert);
  for (const file of metadataFiles) write(source, `apps/runtime/${file}`, inert);
  for (const path of ['bootstrap.mjs', 'entries/aggregate.mjs']) {
    if (missingBootstrap && path === 'bootstrap.mjs') continue;
    let text = readFileSync(join(root, 'apps/windows/runtime', path), 'utf8');
    if (extraImport && path === 'bootstrap.mjs') text += "\nimport { unexpected } from './unexpected.mjs';\n";
    write(source, `apps/windows/runtime/${path}`, text);
  }
  if (extraSource) write(source, 'apps/windows/runtime/unlisted-helper.mjs', inert);
  if (platformModule) write(source, 'apps/windows/runtime/modules/music-script.mjs', inert);
  return { root: source, area: fixtureArea, output: join(fixtureArea, 'payload') };
}

test('packaging rejects existing output without changing a single prior byte', async () => {
  const f = packagingFixture();
  mkdirSync(f.output);
  write(f.output, 'runtime-manifest.json', { status: 'existing-do-not-touch' });
  write(f.output, 'sentinel.txt', 'preserve');
  const before = files(f.output).map(path => [path, sha256(readFileSync(join(f.output, path)))]);
  await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(files(f.output).map(path => [path, sha256(readFileSync(join(f.output, path)))]), before);
});

for (const [option, code] of [
  ['missingBootstrap', 'ENOENT'], ['extraSource', 'WINDOWS_CLOSURE'],
  ['extraImport', 'WINDOWS_IMPORT'], ['missingShared', 'MISSING_PUBLIC_FILE'],
  ['missingCommonModule', 'MISSING_SOURCE'], ['platformModule', 'WINDOWS_CLOSURE'],
  ['missingSourceModule', 'MISSING_SOURCE'],
]) {
  test(`packaging ${option} is explicit and retains a failed file inventory`, async () => {
    const f = packagingFixture({ [option]: true });
    await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code });
    const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
    assert.equal(manifest.status, 'failed');
    assert.equal(manifest.error.code, code);
    for (const file of manifest.files) {
      assert.equal(sha256(readFileSync(join(f.output, file.path))), file.sha256);
    }
    if (!['missingShared', 'missingCommonModule', 'missingSourceModule'].includes(option)) {
      assert.equal(manifest.transport, 'windows-http-v1');
      assert.equal(manifest.entry, 'app/windows/entries/aggregate.mjs');
    }
  });
}

test('Windows source junctions are rejected, not dereferenced or silently omitted', async () => {
  const f = packagingFixture();
  const entries = join(f.root, 'apps/windows/runtime/entries'), relocated = join(f.root, 'saved-entries');
  assert.ok(within(temporary, entries) && within(temporary, relocated));
  renameSync(entries, relocated);
  symlinkSync(relocated, entries, process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code: 'UNSAFE_SYMLINK' });
  assert.equal(readJSON(join(f.output, 'runtime-manifest.json')).status, 'failed');
  assert.ok(existsSync(join(relocated, 'aggregate.mjs')), 'source is never removed');
});


test('Windows packaging preserves common files byte-for-byte and is reproducible without running fixture code', async () => {
  const f = packagingFixture();
  const first = await packageWindowsRuntime({ ...f, product: 'aggregate' });
  verifyManifest(f.output, first);
  const second = await packageWindowsRuntime({ root: f.root, product: 'aggregate', output: join(f.area, 'second') });
  assert.deepEqual(first, second);
  const shared = await packageRuntime({ root: f.root, product: 'aggregate', output: join(f.area, 'common'), profile: 'common-native' });
  assert.deepEqual(shared.modules, ['music', 'cat', 'source']);
  assert.deepEqual(first.files.filter(file => !file.path.startsWith('app/windows/')), shared.files);
  assert.deepEqual(first.dependencies, shared.dependencies);
  assert.deepEqual(first.omittedOptionalDependencies, shared.omittedOptionalDependencies);
  for (const file of [...metadataFiles, 'modules/pan-runtime.mjs', 'pan-entry.mjs']) {
    const path = `apps/runtime/${file}`, bytes = readFileSync(join(f.root, path));
    assert.deepEqual(readFileSync(join(f.output, 'app', file)), bytes);
    assert.deepEqual(first.inputs.filter(input => input.path === path), [{ path, sha256: sha256(bytes) }]);
  }
  assert.ok(readFileSync(join(f.output, first.entry), 'utf8').includes("from '../../entry.mjs'"));
});

test('Windows packaging accepts optional metadata and pan closures and neutral HTTP imports', async () => {
  const f = packagingFixture();
  write(f.root, 'apps/runtime/modules/metadata-data.mjs', `
    import '../contracts.mjs';
    import './metadata/settings.mjs';
    import './metadata/network.mjs';
    import './metadata/tmdb.mjs';
    import './metadata/douban.mjs';
    throw new Error('Metadata fixture code must never execute');
  `);
  write(f.root, 'apps/runtime/modules/metadata/network.mjs', `
    import 'node:http';
    import 'node:https';
    import '@meowfilm/script-host';
    import './settings.mjs';
    throw new Error('Metadata network fixture code must never execute');
  `);
  write(f.root, 'apps/runtime/pan-entry.mjs',
    "export { createPanRuntimeModule } from './modules/pan-runtime.mjs';");
  write(f.root, 'apps/runtime/modules/pan-runtime.mjs', `
    import '@meowfilm/pan-runtime';
    import '../contracts.mjs';
    throw new Error('Pan fixture code must never execute');
  `);
  verifyManifest(f.output, await packageWindowsRuntime({ ...f, product: 'aggregate' }));
});

for (const [name, mutate, code] of [
  ...[...metadataFiles, 'modules/pan-runtime.mjs', 'pan-entry.mjs'].map(file => [`missing shared ${file}`, f =>
    rmSync(join(f.root, 'apps/runtime', file)), 'MISSING_SOURCE']),
  ...['unlisted', 'modules/unlisted', 'modules/metadata/unlisted'].map(path =>
    [`unlisted shared directory ${path}`, f =>
      mkdirSync(join(f.root, 'apps/runtime', path)), 'SHARED_CLOSURE']),
  ...['unlisted-helper.mjs', 'modules/metadata/unlisted-helper.mjs'].map(file =>
    [`unlisted shared file ${file}`, f =>
      write(f.root, `apps/runtime/${file}`, 'export {};'), 'SHARED_CLOSURE']),
  ...['node:net', 'node:fs', '@meowfilm/script-host/src/host.js', '@meowfilm/pan-runtime/src/index.js',
    '../../../windows/runtime/bootstrap.mjs', '../../../ios/runtime/bootstrap.mjs'].map(specifier =>
    [`metadata import ${specifier}`, f =>
      write(f.root, 'apps/runtime/modules/metadata/network.mjs', `import '${specifier}';`), 'SHARED_IMPORT']),
]) {
  test(`Windows packaging rejects ${name} and retains a failed inventory`, async () => {
    const f = packagingFixture();
    mutate(f);
    await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code });
    const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
    assert.equal(manifest.status, 'failed');
    assert.equal(manifest.error.code, code);
    assert.deepEqual(manifest.modules, ['music', 'cat', 'source']);
    for (const file of manifest.files) {
      const bytes = readFileSync(join(f.output, file.path));
      assert.equal(bytes.length, file.bytes);
      assert.equal(sha256(bytes), file.sha256);
    }
  });
}

test('test bundles use the public assembly and reject existing or unowned output', async () => {
  const f = packagingFixture(), output = join(f.area, 'bundles');
  const manifest = await prepareWindowsTestBundles({ root: f.root, output });
  verifyManifest(join(output, 'aggregate'), manifest);
  const before = readFileSync(join(output, 'aggregate/runtime-manifest.json'));
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output }), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(readFileSync(join(output, 'aggregate/runtime-manifest.json')), before);
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: 'relative' }), { code: 'INVALID_TEST_OUTPUT' });
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: root }), /leaves its Windows owner/);
});

test('packaging CLIs reject missing, duplicate and extra arguments before preparing any inputs', () => {
  const output = join(area(), 'payload');
  for (const [script, argumentsList] of [
    ['package-runtime.mjs', [[], ['--product', 'aggregate'], ['--output', output],
      ['--product', 'aggregate', '--output', output, '--output', output],
      ['--product', 'aggregate', '--output', output, '--unknown', 'x']]],
    ['prepare-test-bundles.mjs', [[], ['--help'], [output, 'extra']]],
  ]) for (const args of argumentsList) {
    const result = spawnSync(process.execPath, [join(root, 'scripts/windows', script), ...args], {
      cwd: root, encoding: 'utf8', windowsHide: true, timeout: 10000,
    });
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /^CLI_USAGE:/);
    assert.equal(result.stdout, '');
    assert.equal(existsSync(output), false);
  }
});

const artifactPath = 'packages/script-host/build/Release/script_host_native.node';
const receiptPath = 'packages/script-host/build/Release/script-host-native.json';
const sdkPath = 'packages/script-host/build/Release/source-sdk.js';
const payloadPath = path => path.replace('packages/script-host/', 'node_modules/@meowfilm/script-host/');
let nativeSource;
function nativeFixture(t) {
  if (!nativeSource) {
    const base = area(), source = join(base, 'source');
    mkdirSync(source);
    const receipt = readJSON(join(root, receiptPath)), lock = readJSON(join(root, 'package-lock.json'));
    const copy = path => {
      mkdirSync(dirname(join(source, path)), { recursive: true });
      cpSync(join(root, path), join(source, path), { recursive: true });
    };
    // Use actual build evidence. No fabricated addon, SDK, upstream hash or receipt can satisfy this fixture.
    for (const path of new Set(['package.json', 'package-lock.json', 'scripts/script-host-native.json',
      artifactPath, receiptPath, sdkPath, ...receipt.inputs.map(input => input.path)])) copy(path);
    for (const name of ['runtime-bridge', 'source-runtime', 'script-host', 'pan-runtime', 'netease-token']) {
      copy(`packages/${name}/package.json`);
      copy(`packages/${name}/src`);
      const installed = join(source, 'node_modules/@meowfilm', name);
      mkdirSync(dirname(installed), { recursive: true });
      symlinkSync(join(source, 'packages', name), installed, process.platform === 'win32' ? 'junction' : 'dir');
    }
    for (const [path, entry] of Object.entries(lock.packages)) {
      if (path.includes('node_modules/') && !entry.link && !entry.dev && existsSync(join(root, path))) copy(path);
    }
    copy('apps/runtime');
    copy('apps/windows/runtime');
    nativeSource = { root: source, area: base };
  }
  const f = { ...nativeSource, output: join(nativeSource.area, 'payload') }, originals = new Map();
  f.write = (path, value) => {
    if (!originals.has(path)) originals.set(path, existsSync(join(f.root, path)) ? readFileSync(join(f.root, path)) : null);
    write(f.root, path, value);
  };
  t.after(() => {
    for (const [path, bytes] of originals) {
      if (bytes === null) rmSync(join(f.root, path));
      else writeFileSync(join(f.root, path), bytes);
    }
    rmSync(f.output, { recursive: true, force: true });
  });
  return f;
}

test('native Windows wrapper enforces win-x64 and preserves the trusted addon/SDK/receipt and inventory', async t => {
  const f = nativeFixture(t);
  if (process.platform !== 'win32' || process.arch !== 'x64') {
    await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code: 'NATIVE_TARGET_MISMATCH' });
    assert.equal(readJSON(join(f.output, 'runtime-manifest.json')).status, 'failed');
    return;
  }
  const manifest = await packageWindowsRuntime({ ...f, product: 'aggregate' });
  verifyManifest(f.output, manifest);
  assert.equal(manifest.trustedNative.length, 1);
  const native = manifest.trustedNative[0], receipt = readJSON(join(f.root, receiptPath));
  assert.equal(native.path, payloadPath(artifactPath));
  assert.equal(native.sdk.path, payloadPath(sdkPath));
  assert.equal(native.platform, 'win32');
  assert.equal(native.arch, 'x64');
  assert.equal(native.abi, 1);
  assert.equal(native.napi, 8);
  assert.deepEqual(native.sources, receipt.sources);
  assert.deepEqual(manifest.files.filter(file => /\.(?:node|dll|exe|so|dylib|a|o|wasm)$/i.test(file.path))
    .map(file => file.path), [payloadPath(artifactPath)]);
  for (const record of [native, native.sdk, ...native.licenses])
    assert.equal(sha256(readFileSync(join(f.output, record.path))), record.sha256);
  assert.deepEqual(readJSON(join(f.output, payloadPath(receiptPath))), receipt);
  for (const input of receipt.inputs) assert.deepEqual(manifest.inputs.find(record => record.path === input.path), input);
  for (const file of files(join(f.root, 'apps/runtime')))
    assert.deepEqual(readFileSync(join(f.output, 'app', file)), readFileSync(join(f.root, 'apps/runtime', file)));
  for (const dependency of manifest.dependencies) {
    assert.deepEqual(readJSON(join(f.output, dependency.path, 'package.json')), readJSON(join(f.root, dependency.lockPath, 'package.json')));
    for (const name of readdirSync(join(f.root, dependency.lockPath), { withFileTypes: true })) {
      if (name.isFile() && /^(?:\.?licen[cs]e[sd]?|notice|copying|copyright|authors|third[-_]?party|sources)(?:[._-]|$)/i.test(name.name))
        assert.deepEqual(readFileSync(join(f.output, dependency.path, name.name)), readFileSync(join(f.root, dependency.lockPath, name.name)));
    }
  }
});

for (const [name, mutate, code] of [
  ['addon hash', f => f.write(artifactPath, Buffer.from('damaged native artifact')), 'NATIVE_HASH_MISMATCH'],
  ['SDK hash', f => f.write(sdkPath, 'damaged SDK'), 'NATIVE_HASH_MISMATCH'],
  ['license', f => f.write('LICENSES/quickjs-ng-LICENSE.txt', 'changed license'), 'NATIVE_LICENSE_MISMATCH'],
  ['target', f => f.write(receiptPath, { ...readJSON(join(f.root, receiptPath)), arch: 'wrong-target' }), 'NATIVE_TARGET_MISMATCH'],
  ['ABI', f => f.write(receiptPath, { ...readJSON(join(f.root, receiptPath)), abi: 2 }), 'INVALID_NATIVE_RECEIPT'],
  ['receipt inputs', f => {
    const receipt = readJSON(join(f.root, receiptPath));
    f.write(receiptPath, { ...receipt, inputs: receipt.inputs.filter(input => input.path !== 'packages/script-host/binding.gyp') });
  }, 'NATIVE_INPUT_MISMATCH'],
  ['WASM inventory', f => f.write('packages/script-host/src/unreviewed.wasm', Buffer.from('wasm')), 'NATIVE_PAYLOAD'],
  ['native inventory', f => f.write('packages/script-host/src/unreviewed.node', Buffer.from('addon')), 'NATIVE_PAYLOAD'],
]) test(`native Windows packaging rejects damaged ${name}`, async t => {
  const f = nativeFixture(t);
  mutate(f);
  await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code });
  const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
  assert.equal(manifest.status, 'failed');
  assert.equal(manifest.error.code, code);
  for (const file of manifest.files) assert.equal(sha256(readFileSync(join(f.output, file.path))), file.sha256);
});

test('native Windows test bundles and CLI include the same verified runtime', async t => {
  const f = nativeFixture(t), output = join(f.area, 'bundles');
  t.after(() => rmSync(output, { recursive: true, force: true }));
  const windowsTarget = process.platform === 'win32' && process.arch === 'x64';
  if (windowsTarget) {
    const manifest = await prepareWindowsTestBundles({ root: f.root, output });
    verifyManifest(join(output, 'aggregate'), manifest);
    for (const path of [artifactPath, sdkPath, receiptPath])
      assert.deepEqual(readFileSync(join(output, 'aggregate', payloadPath(path))), readFileSync(join(f.root, path)));
  } else {
    await assert.rejects(prepareWindowsTestBundles({ root: f.root, output }), { code: 'NATIVE_TARGET_MISMATCH' });
    assert.equal(readJSON(join(output, 'aggregate/runtime-manifest.json')).status, 'failed');
  }
  const result = spawnSync(process.execPath, [join(root, 'scripts/windows/package-runtime.mjs'),
    '--product', 'aggregate', '--output', f.output], { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 120000 });
  assert.ifError(result.error);
  if (!windowsTarget) {
    assert.equal(result.status, 1);
    assert.match(result.stderr, /NATIVE_TARGET_MISMATCH/);
    assert.equal(readJSON(join(f.output, 'runtime-manifest.json')).status, 'failed');
    return;
  }
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).transport, 'windows-http-v1');
  verifyManifest(f.output, readJSON(join(f.output, 'runtime-manifest.json')));
});
