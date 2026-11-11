import test from 'node:test';
import assert from 'node:assert/strict';
import {
  cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import vm from 'node:vm';
import { buildSourceSDK } from '../scripts/quickjs/build-source-sdk.mjs';
import { sdkDependencyInputs } from '../scripts/quickjs/build-native.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const require = createRequire(import.meta.url);
const tar = createRequire(require.resolve('node-gyp/package.json'))('tar');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('native header verification follows node-gyp default and configured shared caches', () => {
  const gypPackage = require.resolve('node-gyp/package.json');
  const gypRequire = createRequire(gypPackage);
  const version = JSON.parse(readFileSync(join(repository, 'scripts/script-host-native.json'))).nodeHeaders.version;
  const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
    !/^npm_(?:package_config_node_gyp_|config_)devdir$/i.test(name)));
  const configured = join(repository, 'tmp/test-results/native-build-source/shared headers');
  const preferred = join(repository, 'tmp/test-results/native-build-source/preferred headers');
  for (const [settings, expected] of [
    [{}, join(gypRequire('env-paths')('node-gyp', { suffix: '' }).cache, version)],
    [{ npm_config_devdir: configured }, join(configured, version)],
    [{ npm_config_devdir: configured, npm_package_config_node_gyp_devdir: preferred }, join(preferred, version)],
    [{ npm_package_config_node_gyp_devdir: '~/Developer/cache/node-gyp' }, join(homedir(), 'Developer/cache/node-gyp', version)],
    [{ npm_package_config_node_gyp_devdir: 'tmp/test-results/native-build-source/relative headers' },
      resolve(repository, 'tmp/test-results/native-build-source/relative headers', version)],
  ]) {
    const result = spawnSync(process.execPath, ['--input-type=module', '-e', `
      import { nodeHeaderRoot } from './scripts/quickjs/build-native.mjs';
      console.log(nodeHeaderRoot(${JSON.stringify(gypPackage)}));
    `], { cwd: repository, env: { ...environment, ...settings }, encoding: 'utf8' });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), resolve(expected));
  }
});

async function fixture(t, recorded = true, shared = false) {
  const temporary = join(repository, 'tmp/test-results/native-build-source');
  mkdirSync(temporary, { recursive: true });
  const area = mkdtempSync(join(temporary, 'case-'));
  t.after(() => rmSync(area, { recursive: true, force: true }));
  const root = join(area, 'source');
  const write = (path, bytes) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), bytes);
  };
  for (const path of ['scripts/quickjs/build-native.mjs', 'scripts/script-host-native.json']) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    cpSync(join(repository, path), join(root, path));
  }
  const cache = 'tmp/build/runtime-native';
  const originals = new Map(shared ? [
    ['quickjs.c', 'Synthetic checked-in source.\n'],
    ['quickjs.h', 'Synthetic checked-in header.\n'],
  ] : [
    ['Configure', 'Synthetic upstream source; not a compiler fixture.\n'],
    ['providers/common/der/oids_to_c.pm', 'Synthetic module input.\n'],
  ]);
  // Synthetic archive exercises exact preparation, not the real OpenSSL pin or build.
  for (const [path, bytes] of originals) write(`archive/upstream/${path}`, bytes);
  const archivePath = `${cache}/openssl.tar.gz`;
  mkdirSync(join(root, cache), { recursive: true });
  await tar.c({ gzip: true, file: join(root, archivePath), cwd: join(root, 'archive') }, ['upstream']);
  const source = {
    version: 'fixture', commit: '0'.repeat(40), sha256: hash(readFileSync(join(root, archivePath))),
    url: 'https://example.invalid/source.tar.gz', archivePath, sourcePath: `${cache}/openssl`,
  };
  if (shared) {
    source.sourcePath = 'native/JavaScript/quickjs';
    source.files = Object.fromEntries([...originals].map(([path, bytes]) => [path, hash(bytes)]));
  }
  const paths = [...originals.keys()].map(path => `${source.sourcePath}/${path}`).sort();
  for (const [path, bytes] of originals) write(`${source.sourcePath}/${path}`, bytes);
  const identity = { version: source.version, commit: source.commit, sha256: source.sha256, patches: [] };
  const recordPath = `${cache}/openssl-source.json`;
  if (recorded) write(recordPath, JSON.stringify({ identity,
    files: paths.map(path => ({ path, sha256: hash(readFileSync(join(root, path))) })) }));
  const builder = await import(pathToFileURL(join(root, 'scripts/quickjs/build-native.mjs')));
  const prepare = () => builder.prepareSource('openssl', source);
  return { root, source, paths, recordPath, identity, builder, prepare, write };
}

test('source preparation reuses an exact recorded inventory without rewriting source or record', async t => {
  const f = await fixture(t), record = readFileSync(join(f.root, f.recordPath));
  const before = f.paths.map(path => readFileSync(join(f.root, path)));
  assert.deepEqual(await f.prepare(), f.paths);
  assert.deepEqual(await f.prepare(), f.paths);
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
  assert.deepEqual(f.paths.map(path => readFileSync(join(f.root, path))), before);
  assert.deepEqual(f.builder.commands, []);
});

for (const recorded of [true, false]) {
  test(`source preparation rejects unrecorded generated headers with ${recorded ? 'a cached record' : 'archive adoption'}`, async t => {
    const f = await fixture(t, recorded), extra = `${f.source.sourcePath}/include/openssl/configuration.h`;
    const record = recorded ? readFileSync(join(f.root, f.recordPath)) : undefined;
    f.write(extra, 'Unknown generated content must not be removed.\n');
    await assert.rejects(f.prepare(), { message: `Unrecorded upstream input: ${extra}` });
    assert.equal(readFileSync(join(f.root, extra), 'utf8'), 'Unknown generated content must not be removed.\n');
    assert.equal(existsSync(join(f.root, f.recordPath)), recorded);
    if (recorded) assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
    assert.equal(existsSync(join(f.root, f.source.sourcePath + '.prepare')), false);
  });
}

test('source adoption publishes a record only for exact archive files', async t => {
  const f = await fixture(t, false);
  assert.deepEqual(await f.prepare(), f.paths);
  const record = JSON.parse(readFileSync(join(f.root, f.recordPath), 'utf8'));
  assert.deepEqual(record.identity, f.identity);
  assert.deepEqual(record.files.map(input => input.path), f.paths);
  for (const input of record.files) assert.equal(input.sha256, hash(readFileSync(join(f.root, input.path))));
  assert.equal(existsSync(join(f.root, f.source.sourcePath + '.prepare')), false);
});

test('missing recorded upstream files remain rejected without rewriting the record', async t => {
  const f = await fixture(t), record = readFileSync(join(f.root, f.recordPath));
  rmSync(join(f.root, f.paths[0]));
  await assert.rejects(f.prepare(), { message: `Missing upstream input: ${f.paths[0]}` });
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
});

test('modified recorded upstream bytes remain rejected without rewriting the record', async t => {
  const f = await fixture(t), record = readFileSync(join(f.root, f.recordPath));
  f.write(f.paths[0], 'Modified upstream input.\n');
  await assert.rejects(f.prepare(), { message: `Modified upstream input: ${f.paths[0]}` });
  assert.equal(readFileSync(join(f.root, f.paths[0]), 'utf8'), 'Modified upstream input.\n');
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
});

test('unknown contents are refused before a changed source identity can discard them', async t => {
  const f = await fixture(t), record = readFileSync(join(f.root, f.recordPath));
  const extra = `${f.source.sourcePath}/libcrypto.a`;
  f.write(extra, 'Unknown build output.\n');
  f.source.version = 'fixture-next';
  await assert.rejects(f.prepare(), { message: `Unrecorded upstream input: ${extra}` });
  assert.equal(readFileSync(join(f.root, extra), 'utf8'), 'Unknown build output.\n');
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
});

test('upstream inventory does not follow a linked source directory', async t => {
  const f = await fixture(t), record = readFileSync(join(f.root, f.recordPath));
  const outside = join(f.root, 'outside');
  mkdirSync(outside);
  const link = `${f.source.sourcePath}/linked`;
  symlinkSync(outside, join(f.root, link), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(f.prepare(), { message: `Symbolic link in native inputs: ${link}` });
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
});

test('the final source inventory gate rejects additions after preparation without deleting them', async t => {
  const f = await fixture(t);
  const inputs = await f.prepare(), extra = `${f.source.sourcePath}/configdata.pm`;
  f.write(extra, 'Unexpected in-tree configuration.\n');
  await assert.rejects(f.builder.verifySourceInventory(f.source, inputs),
    { message: `Unrecorded upstream input: ${extra}` });
  assert.equal(readFileSync(join(f.root, extra), 'utf8'), 'Unexpected in-tree configuration.\n');
});

test('native input hashing returns every sorted unique hash with 512 real files', async t => {
  const f = await fixture(t), originals = new Map();
  for (let index = 0; index < 512; index++) {
    const path = `inventory/input-${index}.bin`;
    const bytes = Buffer.from([0, index & 0xff, index >>> 8, 0xff, 0x80]);
    f.write(path, bytes);
    originals.set(path, bytes);
  }
  const paths = [...originals.keys()].reverse();
  const requested = [...paths, paths[0], paths[256], paths[0]];
  const expected = [...originals.keys()].sort().map(path => ({ path, sha256: hash(originals.get(path)) }));
  assert.deepEqual(await f.builder.hashInputs(requested), expected);
});

test('shared checked-in source is verified against the pinned archive and reused without rewriting', async t => {
  const f = await fixture(t, false, true);
  assert.deepEqual(await f.prepare(), f.paths);
  const record = readFileSync(join(f.root, f.recordPath));
  const commands = f.builder.commands.length;
  assert.deepEqual(await f.prepare(), f.paths);
  assert.deepEqual(readFileSync(join(f.root, f.recordPath)), record);
  assert.equal(f.builder.commands.length, commands);
});

test('shared source hash changes cannot disguise a mismatch with its pinned archive', async t => {
  const f = await fixture(t, false, true);
  f.write(f.paths[0], 'Modified checked-in source.\n');
  f.source.files['quickjs.c'] = hash('Modified checked-in source.\n');
  await assert.rejects(f.prepare(),
    { message: `Shared source does not match patched archive: ${f.paths[0]}` });
  assert.equal(existsSync(join(f.root, f.recordPath)), false);
  assert.equal(readFileSync(join(f.root, f.paths[0]), 'utf8'), 'Modified checked-in source.\n');
});

test('shared source inventory and hashes remain enforced after successful preparation', async t => {
  const f = await fixture(t, false, true);
  await f.prepare();
  f.write(f.paths[0], 'Modified checked-in source.\n');
  await assert.rejects(f.prepare(), { message: `Modified shared source: ${f.paths[0]}` });
  f.write(f.paths[0], 'Synthetic checked-in source.\n');
  const extra = `${f.source.sourcePath}/generated.h`;
  f.write(extra, 'Unexpected generated header.\n');
  await assert.rejects(f.prepare(), { message: `Unrecorded upstream input: ${extra}` });
  assert.equal(readFileSync(join(f.root, extra), 'utf8'), 'Unexpected generated header.\n');
});

test('source SDK keeps its Node-free dependency closure when the token DOM dependencies are installed', async t => {
  const temporary = join(repository, 'tmp/test-results/native-build-source');
  mkdirSync(temporary, { recursive: true });
  const directory = mkdtempSync(join(temporary, 'sdk-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  // Both are installed for Watchman; neither is a source SDK dependency.
  assert.ok(require.resolve('jsdom'));
  assert.ok(require.resolve('cssstyle'));
  const before = new Set(await sdkDependencyInputs());
  const sdk = await buildSourceSDK({ directory });
  for (const input of sdk.inputs.filter(input => input.path.startsWith('node_modules/'))) {
    assert.ok(before.has(input.path), `unrecorded SDK dependency: ${input.path}`);
  }
  assert.equal(sdk.inputs.some(input => /node_modules\/(?:cssstyle|jsdom)\//.test(input.path)), false);
  // Load the real SDK without Node globals; this is a bundling regression,
  // not a claim that this VM replaces the native QuickJS host acceptance.
  const runtime = vm.runInNewContext(readFileSync(join(repository, sdk.path), 'utf8'), {}, { timeout: 1000 });
  assert.equal(typeof runtime.start, 'function');
  assert.deepEqual(Array.from(runtime.utility('encode', ['测试', 'gbk'])), [178, 226, 202, 212]);
});
