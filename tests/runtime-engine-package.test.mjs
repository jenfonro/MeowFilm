import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import {
  cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, rmdirSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packageRuntime } from '../scripts/package-runtime.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const temporary = join(repository, 'tmp/test-results/runtime-engine-package');
const policyPath = 'scripts/script-host-native.json';
const artifactPath = 'packages/script-host/build/Release/script_host_native.node';
const receiptPath = 'packages/script-host/build/Release/script-host-native.json';
const sdkPath = 'packages/script-host/build/Release/source-sdk.js';
const payloadPath = path => path.replace('packages/script-host/', 'node_modules/@meowfilm/script-host/');
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

let workspace;
const fixtureAreas = [];
after(() => {
  for (const area of fixtureAreas) rmSync(area, { recursive: true });
});

// Copy the real build once for these serial tests. Each case restores its own
// mutations; packaging still checks all actual inputs on every invocation.
function sourceFixture() {
  if (workspace) return workspace;
  mkdirSync(temporary, { recursive: true });
  const area = mkdtempSync(join(temporary, 'case-'));
  fixtureAreas.push(area);
  const root = join(area, 'source'), output = join(area, 'payload');
  mkdirSync(root);
  const receipt = readJSON(join(repository, receiptPath));
  const lock = readJSON(join(repository, 'package-lock.json'));
  const copy = path => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    cpSync(join(repository, path), join(root, path), { recursive: true });
  };
  for (const path of new Set(['package.json', 'package-lock.json', policyPath, receiptPath,
    artifactPath, sdkPath, ...receipt.inputs.map(input => input.path)])) copy(path);
  for (const name of ['runtime-bridge', 'source-runtime', 'script-host', 'pan-runtime',
    'film-rules', 'film-playback', 'netease-token']) {
    const location = `packages/${name}`;
    copy(`${location}/package.json`);
    copy(`${location}/${name === 'film-rules' ? 'film-rules.js' : 'src'}`);
    const installed = join(root, 'node_modules/@meowfilm', name);
    mkdirSync(dirname(installed), { recursive: true });
    symlinkSync(join(root, location), installed, process.platform === 'win32' ? 'junction' : 'dir');
  }
  for (const [path, entry] of Object.entries(lock.packages)) {
    if (path.includes('node_modules/') && !entry.link && !entry.dev && existsSync(join(repository, path))) copy(path);
  }
  copy('apps/runtime');
  workspace = { root, output, area };
  return workspace;
}

function fixture(t) {
  const { root, output, area } = sourceFixture();
  const originals = new Map(), directories = [];
  const remember = path => {
    if (!originals.has(path)) originals.set(path,
      existsSync(join(root, path)) ? readFileSync(join(root, path)) : undefined);
  };
  const parent = path => {
    const missing = [];
    for (let dir = dirname(join(root, path)); !existsSync(dir); dir = dirname(dir)) missing.push(dir);
    for (const dir of missing.reverse()) {
      mkdirSync(dir);
      directories.push(dir);
    }
  };
  t.after(() => {
    for (const [path, bytes] of [...originals].reverse()) {
      rmSync(join(root, path), { force: true });
      if (bytes !== undefined) writeFileSync(join(root, path), bytes);
    }
    for (const dir of directories.reverse()) rmdirSync(dir);
    rmSync(output, { recursive: true, force: true });
  });
  const write = (path, value) => {
    remember(path);
    parent(path);
    writeFileSync(join(root, path), typeof value === 'string' || Buffer.isBuffer(value)
      ? value : `${JSON.stringify(value, null, 2)}\n`);
  };
  const remove = path => {
    remember(path);
    rmSync(join(root, path));
  };
  const symlink = (path, target) => {
    remove(path);
    symlinkSync(target, join(root, path));
  };
  return { root, output, area, policy: readJSON(join(root, policyPath)),
    receipt: readJSON(join(root, receiptPath)), write, remove, symlink,
    pack: () => packageRuntime({ root, product: 'aggregate', output }) };
}

async function rejected(f, code) {
  await assert.rejects(f.pack(), { code });
  const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
  assert.equal(manifest.status, 'failed');
  assert.equal(manifest.error.code, code);
  for (const file of manifest.files) {
    const bytes = readFileSync(join(f.output, file.path));
    assert.equal(bytes.length, file.bytes);
    assert.equal(hash(bytes), file.sha256);
  }
}

test('common native payload preserves exact build, ABI, SDK, source and license evidence', async t => {
  const f = fixture(t);
  f.write('packages/script-host/build/Release/obj.target/not-shipped.o', 'build intermediate');
  const manifest = await f.pack();
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.assemblyProfile, 'common-native');
  assert.equal(manifest.entry, 'app/entry.mjs');
  assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease']);
  assert.equal(manifest.trustedNative.length, 1);
  const native = manifest.trustedNative[0];
  assert.equal(native.path, payloadPath(artifactPath));
  assert.equal(native.platform, process.platform);
  assert.equal(native.arch, process.arch);
  assert.equal(native.abi, 1);
  assert.equal(native.napi, 8);
  assert.deepEqual(native.sources, f.receipt.sources);
  assert.deepEqual(native.patches, f.policy.patches);
  assert.equal(native.sdk.path, payloadPath(sdkPath));
  assert.deepEqual(manifest.files.filter(file => /\.(?:node|dll|dylib|so|exe|a|o|wasm)$/i.test(file.path))
    .map(file => file.path), [payloadPath(artifactPath)]);
  for (const value of [native, native.sdk, ...native.licenses]) {
    const bytes = readFileSync(join(f.output, value.path));
    assert.equal(hash(bytes), value.sha256);
    if (value.bytes !== undefined) assert.equal(bytes.length, value.bytes);
  }
  for (const input of f.receipt.inputs) {
    assert.deepEqual(manifest.inputs.find(value => value.path === input.path), input);
    assert.equal(manifest.inputs.filter(value => value.path === input.path).length, 1);
  }
  assert.deepEqual(readJSON(join(f.output, payloadPath(receiptPath))), f.receipt);
  assert.equal(manifest.files.some(file => /\/(?:native|obj\.target)\//.test(file.path)), false);
});

function runIsolated(output, program) {
  const hook = `
    import { registerHooks } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep } from 'node:path';
    const root = ${JSON.stringify(output)};
    globalThis.WebAssembly = undefined;
    globalThis.fetch = () => { throw new Error('UNEXPECTED_NETWORK'); };
    registerHooks({ resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url.startsWith('file:')) {
        const path = relative(root, fileURLToPath(result.url));
        if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
          throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
      }
      return result;
    } });
  `;
  // A file entry keeps eval-only CLI flags out of the real Cat Worker.
  const entry = join(output, 'isolated-test.mjs');
  writeFileSync(entry, program, { flag: 'wx' });
  try {
    return spawnSync(process.execPath, ['--import', `data:text/javascript,${encodeURIComponent(hook)}`, entry], {
      cwd: output, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', timeout: 60000, maxBuffer: 1024 * 1024,
    });
  } finally { rmSync(entry); }
}

test('packaged entry starts HTTP and serves explicitly selected film modules without repository fallback', async t => {
  const f = fixture(t);
  await f.pack();
  const result = runIsolated(f.output, `
    import assert from 'node:assert/strict';
    import { join } from 'node:path';
    import { createRuntime, createRuntimeClient } from './app/entry.mjs';
    const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
    const runtime = createRuntime({ sessionId, dataRoot: join(process.cwd(), 'runtime-data'),
      pan: {}, tmdb: {}, douban: {}, film: {} });
    const okay = reply => {
      assert.equal(reply.ok, true, JSON.stringify(reply));
      return reply.value;
    };
    let client;
    try {
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl });
      const descriptor = await client.describe();
      assert.equal(descriptor.ok, true);
      assert.equal(descriptor.value.protocol, 'runtime.http.v1');
      assert.deepEqual(descriptor.value.modules.map(value => value.contract),
        ['music.script.v3', 'cat.script.v2', 'source.runtime.v2', 'pan.runtime.v1',
          'tmdb.data.v1', 'douban.data.v1', 'film.playback.v1']);
      for (const method of ['pan.settings.get', 'tmdb.data.settings.get', 'douban.data.settings.get',
        'film.settings.get', 'film.library.get']) okay(await client.call(method));
      const installationId = '7bd84ef5-9557-4338-a67f-9dc4df2413f9';
      okay(await client.upload('music', installationId, 1, Buffer.from(
        'let n=0; lx.on(lx.EVENT_NAMES.request, async () => ++n);' +
        'lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{kw:{type:"music",actions:["musicUrl"],qualitys:["320k"]}}});'),
        { basename: 'fixture.js' }));
      const added = okay(await client.call('music.script.sources.add', {
        installationId, sourceRevision: 1,
      })).item;
      assert.equal(added.installationId, installationId);
      assert.equal(added.enabled, false);
      const lx = okay(await client.call('music.script.sources.enable', { installationId })).item;
      assert.equal(lx.state, 'ready');
      const input = { installationId: lx.installationId,
        siteId: 'kw', operation: 'musicUrl', payload: { quality: '320k' } };
      assert.equal(okay(await client.call('music.script.invoke', input)), 1);
      assert.equal(okay(await client.call('music.script.invoke', input)), 2);
      okay(await client.upload('cat', 'native-cat', 1, Buffer.from(
        'module.exports.home = () => ({value: Buffer.from([0,255,65]).toString("hex")});')));
      const cat = okay(await client.call('cat.script.install', {
        installationId: 'native-cat', sourceRevision: 1, enabled: true,
      }));
      assert.deepEqual(okay(await client.call('cat.script.invoke', {
        installationId: 'native-cat', generation: cat.generation, siteId: 'default',
        operation: 'home', payload: {},
      })), { value: '00ff41' });
      okay(await client.upload('source', 'native-source', 1, Buffer.from(
        'export default { searchContent(keyword) { return {list:[{vod_id:keyword,vod_name:keyword}]}; } };'),
        { basename: 'fixture.js' }));
      const source = okay(await client.call('source.runtime.load', {
        installationId: 'native-source', sourceRevision: 1, revision: 1, expectedRevision: 0, protocol: 'cat',
      })).instance;
      assert.deepEqual(okay(await client.call('source.runtime.invoke', {
        installationId: source.installationId, revision: source.revision, generation: source.generation,
        action: 'search', payload: { keyword: 'fixture' },
      })), { list: [{ vod_id: 'fixture', vod_name: 'fixture' }] });
      assert.deepEqual(okay(await client.shutdown()), { closed: true, cleanupComplete: true });
      console.log('PACKAGED_NATIVE_HTTP_SEVEN_MODULES_OK');
    } finally {
      await runtime.close();
      await client?.close();
    }
  `);
  assert.equal(result.signal, null, result.stderr);
  assert.equal(result.status, 0, result.stderr || result.error?.message);
  assert.match(result.stdout, /PACKAGED_NATIVE_HTTP_SEVEN_MODULES_OK/);
});

test('packaged pan entry imports and serves its contract without script engines or repository fallback', async t => {
  const f = fixture(t);
  await f.pack();
  const result = runIsolated(f.output, `
    import assert from 'node:assert/strict';
    import { join } from 'node:path';
    import { createPanRuntime, createRuntimeClient } from './app/pan-entry.mjs';
    const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
    const runtime = createPanRuntime({ sessionId, dataRoot: join(process.cwd(), 'pan-data') });
    let client;
    try {
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl });
      assert.deepEqual((await client.describe()).value.modules.map(item => item.id), ['pan']);
      assert.equal((await client.call('pan.providers')).value.providers.length, 7);
      const saved = await client.call('pan.settings.set', {
        provider: 'uc', credentials: { cookie: 'synthetic-offline-only' },
      });
      assert.equal(saved.ok, true);
      assert.deepEqual(saved.value.fields, ['cookie']);
      assert.deepEqual((await client.shutdown()).value, { closed: true, cleanupComplete: true });
      console.log('PACKAGED_PAN_HTTP_OK');
    } finally { await client?.close(); await runtime.close(); }
  `);
  assert.equal(result.signal, null, result.stderr);
  assert.equal(result.status, 0, result.stderr || result.error?.message);
  assert.match(result.stdout, /PACKAGED_PAN_HTTP_OK/);
});

for (const [field, value, code] of [
  ['abi', 2, 'INVALID_NATIVE_RECEIPT'], ['napi', 9, 'INVALID_NATIVE_RECEIPT'],
  ['arch', 'not-the-host', 'NATIVE_TARGET_MISMATCH'],
  ['platform', 'not-the-host', 'NATIVE_TARGET_MISMATCH'],
]) {
  test(`native receipt rejects wrong ${field}`, async t => {
    const f = fixture(t);
    f.write(receiptPath, { ...f.receipt, [field]: value });
    await rejected(f, code);
  });
}

for (const path of [artifactPath, sdkPath]) {
  test(`native build output hash is checked: ${path}`, async t => {
    const f = fixture(t);
    f.write(path, Buffer.concat([readFileSync(join(f.root, path)), Buffer.from('changed')]));
    await rejected(f, 'NATIVE_HASH_MISMATCH');
  });
  test(`missing advertised native build output fails: ${path}`, async t => {
    const f = fixture(t);
    f.remove(path);
    await rejected(f, 'MISSING_SOURCE');
  });
  test(`native build output cannot be a symlink: ${path}`, async t => {
    const f = fixture(t);
    f.symlink(path, join(repository, path));
    await rejected(f, 'UNSAFE_SYMLINK');
  });
}

test('a self-matching artifact hash cannot hide an invalid binary format', async t => {
  const f = fixture(t), bytes = Buffer.from('this is not a native addon');
  f.write(artifactPath, bytes);
  f.write(receiptPath, { ...f.receipt, artifact: { path: artifactPath, bytes: bytes.length, sha256: hash(bytes) } });
  await rejected(f, 'NATIVE_TARGET_MISMATCH');
});

for (const name of ['quickjs', 'openssl', 'zlib']) {
  test(`native source identity is bound to the policy: ${name}`, async t => {
    const f = fixture(t);
    f.receipt.sources[name].commit = '0'.repeat(40);
    f.write(receiptPath, f.receipt);
    await rejected(f, 'INVALID_NATIVE_RECEIPT');
  });
  test(`native source license is verified: ${name}`, async t => {
    const f = fixture(t);
    f.write(f.policy.sources[name].license.path, 'not the upstream license');
    await rejected(f, 'NATIVE_LICENSE_MISMATCH');
  });
}

for (const change of ['omitted', 'reordered', 'unreviewed']) {
  test(`native policy rejects an ${change} ownership patch`, async t => {
    const f = fixture(t), patches = f.policy.patches;
    if (change === 'omitted') patches.pop();
    else if (change === 'reordered') [patches[2], patches[3]] = [patches[3], patches[2]];
    else patches[3].path = 'packages/script-host/native/unreviewed.patch';
    f.write(policyPath, f.policy);
    await rejected(f, 'INVALID_NATIVE_POLICY');
  });
}

for (const path of ['packages/script-host/binding.gyp', 'scripts/quickjs/build-native.mjs',
  'scripts/quickjs/build-source-sdk.mjs', 'packages/script-host/native/task-context.patch',
  'packages/script-host/native/bigint-uintn.patch', 'packages/script-host/native/utf16-cstring.patch',
  'packages/script-host/native/arraybuffer-ownership.patch']) {
  test(`changed build input requires a new valid build: ${path}`, async t => {
    const f = fixture(t);
    f.write(path, Buffer.concat([readFileSync(join(f.root, path)), Buffer.from('\nchanged\n')]));
    await rejected(f, 'NATIVE_INPUT_MISMATCH');
  });
}

test('new native source files cannot silently bypass the build input inventory', async t => {
  const f = fixture(t);
  f.write('packages/script-host/native/unrecorded.cc', 'int added = 1;');
  await rejected(f, 'NATIVE_INPUT_MISMATCH');
});

test('omitting a mandatory build input invalidates the receipt', async t => {
  const f = fixture(t);
  f.write(receiptPath, { ...f.receipt,
    inputs: f.receipt.inputs.filter(input => input.path !== 'packages/script-host/binding.gyp') });
  await rejected(f, 'NATIVE_INPUT_MISMATCH');
});

for (const extension of ['node', 'so', 'dll', 'wasm']) {
  test(`unreviewed runtime binary is still rejected: ${extension}`, async t => {
    const f = fixture(t);
    f.write(`packages/script-host/src/extra.${extension}`, 'not approved');
    await rejected(f, 'NATIVE_PAYLOAD');
  });
}

test('unknown profiles cannot select an unreviewed backend', async t => {
  mkdirSync(temporary, { recursive: true });
  const area = mkdtempSync(join(temporary, 'profiles-'));
  t.after(() => rmSync(area, { recursive: true }));
  const output = join(area, 'arbitrary');
  await assert.rejects(packageRuntime({ root: repository, product: 'aggregate', output, profile: 'arbitrary' }),
    { code: 'INVALID_PROFILE' });
  assert.equal(existsSync(output), false);
});
