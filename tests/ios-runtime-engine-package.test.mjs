import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, hash, paths, readJSON, repository, target } from './ios-native-package-support.mjs';

async function rejects(f, code) {
  await assert.rejects(f.pack(), { code });
  assert.equal(readJSON(join(f.output, 'runtime-manifest.json')).status, 'failed');
}

test('iOS wrapper packages the eight-module App closure and separate pre-sign framework identity', async t => {
  const f = fixture(t), manifest = await f.pack();
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.entry, 'app/ios/entries/aggregate.mjs');
  assert.equal(manifest.platform, 'ios');
  assert.equal(manifest.transport, 'runtime.http.v1');
  assert.equal(manifest.assemblyProfile, 'common-native');
  assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease']);
  assert.deepEqual(manifest.target, target);
  assert.deepEqual(manifest.fileHashExcludes, ['runtime-manifest.json']);
  assert.equal(manifest.trustedNative.length, 1);
  const native = manifest.trustedNative[0];
  assert.equal(native.path, 'Frameworks/MeowScriptHost.framework/MeowScriptHost');
  assert.equal(native.sha256, f.receipt.artifact.sha256);
  assert.equal(native.bindingIdentity, f.receipt.bindingIdentity);
  assert.deepEqual(native.target, target);
  const valuesPath = 'node_modules/@meowfilm/script-host/build/Release/cat-web-values.cjs';
  assert.deepEqual(native.catWebValues, { ...f.receipt.catWebValues, path: valuesPath,
    license: { ...f.receipt.catWebValues.license, path: 'licenses/undici-LICENSE.txt' } });
  assert.ok(native.licenses.some(license => license.path === 'licenses/undici-LICENSE.txt'
    && license.sha256 === f.receipt.catWebValues.license.sha256));
  assert.ok(manifest.files.some(file => file.path === valuesPath
    && file.bytes === f.receipt.catWebValues.bytes && file.sha256 === f.receipt.catWebValues.sha256));
  for (const input of f.receipt.catWebValues.inputs) {
    assert.ok(manifest.inputs.some(value => value.path === input.path && value.sha256 === input.sha256));
  }
  assert.equal(manifest.dependencies.some(dependency => dependency.name === 'undici'), false);
  assert.equal(manifest.files.some(file => /(?:^|\/)node_modules\/undici\//.test(file.path)), false);
  assert.equal(manifest.files.some(file => /\.(?:node|wasm|dylib|so|dll)$/i.test(file.path)), false);
  const entry = readFileSync(join(f.output, manifest.entry), 'utf8');
  for (const name of ['music-script', 'cat-script', 'source-runtime', 'pan-runtime', 'metadata-data', 'netease-token'])
    assert.ok(entry.includes(`../../modules/${name}.mjs`));
  const imports = manifest.relocations.find(file => file.path === manifest.entry).imports;
  assert.ok(imports.some(({ from, to }) => from === '../../../runtime/entry.mjs' && to === '../../entry.mjs'));
  assert.ok(manifest.dependencies.some(dependency => dependency.name === '@meowfilm/film-playback'));
  assert.ok(manifest.files.some(file => file.path === 'node_modules/@meowfilm/film-rules/film-rules.js'));
  assert.ok(manifest.files.some(file => file.path === 'node_modules/@meowfilm/film-rules/playback-rules.js'));
  assert.ok(manifest.files.some(file => file.path === 'node_modules/@meowfilm/film-search/film-search.js'));
  for (const license of ['quickjs-ng-LICENSE.txt', 'openssl-LICENSE.txt', 'zlib-LICENSE.txt', 'NOTICE.md',
    'undici-LICENSE.txt', 'Unicode-3.0.txt', 'api-enhanced-MIT.txt', 'npm-node-compat-ISC.txt'])
    assert.ok(manifest.files.some(file => file.path === `licenses/${license}`));
  assert.deepEqual(readFileSync(join(f.output, 'licenses/undici-LICENSE.txt')),
    readFileSync(join(f.root, f.receipt.catWebValues.license.path)));
  assert.deepEqual(readJSON(join(f.output, 'node_modules/@meowfilm/script-host/build/Release/script-host-native.json')), f.receipt);
  for (const file of manifest.files) {
    const bytes = readFileSync(join(f.output, file.path));
    assert.equal(bytes.length, file.bytes); assert.equal(hash(bytes), file.sha256);
  }
});

test('iOS wrapper relocates only the fixed internal descriptor helper onto the packaged native loader', async t => {
  const f = fixture(t), source = '../../../packages/script-host/src/native-engine.js';
  const bootstrap = readFileSync(join(repository, 'apps/ios/runtime/bootstrap.mjs'), 'utf8');
  assert.ok(bootstrap.includes(`import { duplicateBootstrapDescriptor } from '${source}';`));
  const manifest = await f.pack(), path = 'app/ios/bootstrap.mjs';
  const relocated = '../../node_modules/@meowfilm/script-host/src/native-engine.js';
  assert.ok(readFileSync(join(f.output, path), 'utf8')
    .includes(`from '${relocated}'`));
  assert.deepEqual(manifest.relocations.find(file => file.path === path).imports.find(value => value.from === source),
    { from: source, to: relocated });
  assert.ok(manifest.files.some(file => file.path === 'node_modules/@meowfilm/script-host/src/native-engine.js'));
});

for (const [name, change, code] of [
  ['an extra platform source', f => f.write('apps/ios/runtime/entries/other.mjs', 'export {};'), 'IOS_CLOSURE'],
  ['a missing platform entry', f => f.remove('apps/ios/runtime/entries/aggregate.mjs'), 'IOS_CLOSURE'],
  ['an unexpected entry import', f => f.write('apps/ios/runtime/entries/aggregate.mjs',
    "import { fork } from 'node:child_process';\n" + readFileSync(join(repository, 'apps/ios/runtime/entries/aggregate.mjs'))), 'IOS_IMPORT'],
  ['a dynamic platform import', f => f.write('apps/ios/runtime/bootstrap.mjs',
    readFileSync(join(repository, 'apps/ios/runtime/bootstrap.mjs')) + "\nawait import('node:fs');\n"), 'IOS_IMPORT'],
  ['another internal package path', f => f.write('apps/ios/runtime/bootstrap.mjs',
    "import { createScriptHost } from '../../../packages/script-host/src/index.js';\n" +
    readFileSync(join(repository, 'apps/ios/runtime/bootstrap.mjs'))), 'IOS_IMPORT'],
  ['an extra native dependency', f => f.write('packages/script-host/src/extra.node', 'unreviewed'), 'NATIVE_PAYLOAD'],
  // Package content and the injected native receipt reserve the same output directory.
  // Separate input locations make this a real collision on case-insensitive hosts too.
  ['a colliding package/native output path', f =>
    f.write('packages/script-host/Build/Release/NOTICE.txt', 'collision'), 'PATH_COLLISION'],
  ['a wrong target architecture', f => f.write(paths.receipt, { ...f.receipt, arch: 'wrong' }), 'NATIVE_TARGET_MISMATCH'],
  ['a wrong device/simulator target', f => f.write(paths.receipt, { ...f.receipt, target: { ...target, sdk: 'iphoneos' } }), 'NATIVE_TARGET_MISMATCH'],
  ['a changed embedded identity', f => f.write(paths.receipt, { ...f.receipt, bindingIdentity: '0'.repeat(64) }), 'NATIVE_INPUT_MISMATCH'],
  ['missing Node Mobile headers', f => f.remove('.cache/nodejs-mobile/ios/include/node/node_api.h'), 'NATIVE_INPUT_MISMATCH'],
  ['a changed download helper', f => f.write('scripts/dependency-download.mjs', 'changed'), 'NATIVE_INPUT_MISMATCH'],
  ['an omitted download helper input', f => f.write(paths.receipt, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'scripts/dependency-download.mjs') }), 'NATIVE_INPUT_MISMATCH'],
  ['a changed SDK', f => f.write(paths.sdk, 'changed'), 'NATIVE_HASH_MISMATCH'],
  ['a missing SDK', f => f.remove(paths.sdk), 'MISSING_SOURCE'],
  ['a linked SDK', f => f.symlink(paths.sdk, join(repository, paths.sdk)), 'UNSAFE_SYMLINK'],
  ['a missing Cat Web value receipt', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: undefined }), 'INVALID_NATIVE_RECEIPT'],
  ['a wrong Cat Web value target path', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: { ...f.receipt.catWebValues,
      path: '.cache/ios-native/iphoneos-arm64/build/Release/cat-web-values.cjs' } }), 'INVALID_NATIVE_RECEIPT'],
  ['a changed Cat Web value bundle', f => f.write(paths.catWebValues, 'changed'), 'NATIVE_HASH_MISMATCH'],
  ['a missing Cat Web value bundle', f => f.remove(paths.catWebValues), 'MISSING_SOURCE'],
  ['a linked Cat Web value bundle', f => f.symlink(paths.catWebValues,
    join(repository, paths.catWebValues)), 'UNSAFE_SYMLINK'],
  ['a Cat Web value bundle and receipt changed without recompiling the identity', f => {
    const bytes = Buffer.from('module.exports = {};\n');
    f.write(paths.catWebValues, bytes);
    f.write(paths.receipt, { ...f.receipt, catWebValues: {
      ...f.receipt.catWebValues, bytes: bytes.length, sha256: hash(bytes),
    } });
  }, 'NATIVE_INPUT_MISMATCH'],
  ['an omitted Cat Web value consumption input', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: { ...f.receipt.catWebValues,
      inputs: f.receipt.catWebValues.inputs.filter(input =>
        input.path !== 'node_modules/undici/lib/web/fetch/request.js') } }), 'NATIVE_INPUT_MISMATCH'],
  ['an omitted Cat Web value pre-build input', f => f.write(paths.receipt,
    { ...f.receipt, inputs: f.receipt.inputs.filter(input =>
      input.path !== 'node_modules/undici/lib/web/fetch/response.js') }), 'NATIVE_INPUT_MISMATCH'],
  ['an unreviewed Cat Web value consumption input', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: { ...f.receipt.catWebValues, inputs: [
      ...f.receipt.catWebValues.inputs, { path: 'node_modules/undici/lib/dispatcher/client.js', sha256: '0'.repeat(64) },
    ] } }), 'NATIVE_INPUT_MISMATCH'],
  ['an omitted Undici license input', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: { ...f.receipt.catWebValues, inputs: f.receipt.catWebValues.inputs.filter(input =>
      input.path !== f.receipt.catWebValues.license.path) } }), 'NATIVE_INPUT_MISMATCH'],
  ['a wrong Undici license path', f => f.write(paths.receipt,
    { ...f.receipt, catWebValues: { ...f.receipt.catWebValues, license: {
      ...f.receipt.catWebValues.license, path: 'LICENSES/openssl-LICENSE.txt',
    } } }), 'INVALID_NATIVE_RECEIPT'],
  ['a changed Undici license', f => f.write(f.receipt.catWebValues.license.path, 'changed'), 'NATIVE_LICENSE_MISMATCH'],
  ['a missing Undici license', f => f.remove(f.receipt.catWebValues.license.path), 'MISSING_SOURCE'],
  ['a linked Undici license', f => f.symlink(f.receipt.catWebValues.license.path,
    join(repository, f.receipt.catWebValues.license.path)), 'UNSAFE_SYMLINK'],
  ['a changed Undici source input', f => f.write('node_modules/undici/lib/web/fetch/formdata.js', 'changed'),
    'NATIVE_INPUT_MISMATCH'],
  ['an unrecorded Undici file', f => f.write('node_modules/undici/extra.js', 'untracked'), 'NATIVE_INPUT_MISMATCH'],
  ...[
    ['a different Undici version', { version: '6.29.0' }],
    ['a different Undici tarball integrity', { integrity: 'sha512-' + Buffer.alloc(64).toString('base64') }],
    ['a production Undici lock record', { dev: false }],
  ].map(([name, value]) => [name, f => {
    const lock = readJSON(join(f.root, 'package-lock.json'));
    f.write('package-lock.json', { ...lock, packages: {
      ...lock.packages, 'node_modules/undici': { ...lock.packages['node_modules/undici'], ...value },
    } });
  }, 'NATIVE_INPUT_MISMATCH']),
  ['a changed framework', f => f.write(paths.artifact, 'changed'), 'NATIVE_TARGET_MISMATCH'],
  ['a missing framework', f => f.remove(paths.artifact), 'MISSING_SOURCE'],
  ['a linked framework', f => f.symlink(paths.artifact, join(repository, paths.artifact)), 'UNSAFE_SYMLINK'],
  ['changed export evidence', f => f.write(paths.exports, { exported: [], dylibs: [] }), 'NATIVE_HASH_MISMATCH'],
  ['a changed native license', f => f.write('LICENSES/openssl-LICENSE.txt', 'changed'), 'NATIVE_LICENSE_MISMATCH'],
]) test(`iOS packaging rejects ${name} and retains an unusable failed inventory`, async t => {
  const f = fixture(t); change(f); await rejects(f, code);
});
