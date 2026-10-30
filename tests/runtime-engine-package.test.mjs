import test from 'node:test';
import assert from 'node:assert/strict';
import {
  cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { packageWindowsRuntime } from '../scripts/windows/package-runtime.mjs';
import { prepareWindowsTestBundles } from '../scripts/windows/prepare-test-bundles.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const policyPath = 'scripts/quickjs-runtime.json';
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
const policy = readJSON(join(repository, policyPath));
const repositoryLock = readJSON(join(repository, 'package-lock.json'));
const engine = policy.packages.find(pkg => pkg.name === '@jitl/quickjs-wasmfile-release-sync');
const wasmPath = `node_modules/${engine.name}/${engine.wasm.path}`;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const inert = 'throw new Error("Synthetic workspace code must never execute");\n';

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'runtime-engine-package-'));
  t.after(() => {
    assertOwnedPath(temporaryRoot, area);
    rmSync(area, { recursive: true });
  });
  const root = join(area, 'source');
  const write = (path, value) => {
    const target = assertOwnedPath(area, join(root, path), { allowMissing: true });
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, typeof value === 'string' || Buffer.isBuffer(value)
      ? value : `${JSON.stringify(value, null, 2)}\n`);
  };
  const manifest = { name: 'runtime-engine-fixture', version: '1.0.0', private: true, workspaces: ['packages/*'] };
  const lock = { name: manifest.name, version: manifest.version, lockfileVersion: 3, packages: { '': { ...manifest } } };
  write('package.json', manifest);
  for (const name of ['runtime-bridge', 'script-host']) {
    const location = `packages/${name}`, installed = `node_modules/@meowfilm/${name}`;
    const pkg = { name: `@meowfilm/${name}`, version: '1.0.0', type: 'module', license: 'UNLICENSED',
      exports: './src/index.js', files: ['src'],
      ...(name === 'script-host' ? { dependencies: {
        'quickjs-emscripten-core': '0.32.0', '@jitl/quickjs-wasmfile-release-sync': '0.32.0',
      } } : {}),
    };
    write(`${location}/package.json`, pkg);
    write(`${location}/src/index.js`, inert);
    lock.packages[location] = { version: pkg.version, ...(pkg.dependencies ? { dependencies: pkg.dependencies } : {}) };
    lock.packages[installed] = { resolved: location, link: true };
    mkdirSync(dirname(join(root, installed)), { recursive: true });
    symlinkSync(join(root, location), join(root, installed), process.platform === 'win32' ? 'junction' : 'dir');
  }
  // Real installed engine files, never a dummy WASM or invented registry SRI.
  // Only these controlled fixture copies are altered by rejection tests.
  for (const pkg of policy.packages) {
    const location = `node_modules/${pkg.name}`;
    mkdirSync(dirname(join(root, location)), { recursive: true });
    cpSync(join(repository, location), join(root, location), { recursive: true });
    lock.packages[location] = structuredClone(repositoryLock.packages[location]);
  }
  write(policyPath, readFileSync(join(repository, policyPath)));
  write(policy.license.path, readFileSync(join(repository, policy.license.path)));
  write('apps/runtime/bootstrap.mjs', inert);
  write('apps/runtime/modules/music-script.mjs', inert);
  for (const file of ['bootstrap.mjs', 'entries/aggregate.mjs'])
    write(`apps/windows/runtime/${file}`, readFileSync(join(repository, 'apps/windows/runtime', file)));
  const saveLock = () => write('package-lock.json', lock);
  saveLock();
  const editManifest = (location, changes) =>
    write(`${location}/package.json`, { ...readJSON(join(root, location, 'package.json')), ...changes });
  const output = join(area, 'payload');
  return { root, area, output, lock, write, saveLock, editManifest,
    pack: () => packageRuntime({ root, product: 'aggregate', output }) };
}

async function rejected(f, code) {
  await assert.rejects(f.pack(), { code });
  const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
  assert.equal(manifest.status, 'failed');
  assert.equal(manifest.error.code, code);
  for (const file of manifest.files) {
    const bytes = readFileSync(join(f.output, file.path));
    assert.equal(file.bytes, bytes.length);
    assert.equal(file.sha256, hash(bytes));
  }
  return manifest;
}

test('engine packaging pins real lock/manifest/license/WASM bytes without executing workspace code', async t => {
  const f = fixture(t), manifest = await f.pack();
  assert.equal(manifest.status, 'platform-pending');
  assert.deepEqual(manifest.trustedWasm, [{
    package: engine.name, version: engine.version, ...engine.wasm, path: wasmPath,
    licensePath: `node_modules/${engine.name}/LICENSE`, licenseSha256: engine.licenseSha256,
  }]);
  assert.deepEqual(manifest.files.filter(file => /\.wasm$/i.test(file.path)).map(file => file.path), [wasmPath]);
  const wasm = readFileSync(join(f.output, wasmPath));
  assert.equal(wasm.length, engine.wasm.bytes);
  assert.equal(hash(wasm), engine.wasm.sha256);
  assert.deepEqual(wasm.subarray(0, 8), Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]));
  for (const pkg of policy.packages) {
    const dependency = manifest.dependencies.find(value => value.name === pkg.name);
    assert.equal(dependency.version, pkg.version);
    assert.equal(dependency.resolved, pkg.resolved);
    assert.equal(dependency.integrity, pkg.integrity);
    assert.equal(dependency.manifestSha256, hash(readFileSync(join(f.output, dependency.path, 'package.json'))));
    assert.equal(dependency.licenseSha256, hash(readFileSync(join(f.output, dependency.path, 'LICENSE'))));
  }
  for (const path of [policyPath, policy.license.path, wasmPath,
    'apps/runtime/bootstrap.mjs', 'apps/runtime/modules/music-script.mjs']) {
    assert.deepEqual(manifest.inputs.find(input => input.path === path), {
      path, sha256: hash(readFileSync(join(f.root, path))),
    });
  }
});

test('packaged WASM export compiles offline and instantiates independent memory from one compiled module', async t => {
  const f = fixture(t);
  await f.pack();
  const hook = `
    import { registerHooks } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep } from 'node:path';
    const root = ${JSON.stringify(f.output)};
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
  const program = `
    const { readFile } = await import('node:fs/promises');
    const { newQuickJSWASMModuleFromVariant, newVariant } = await import('quickjs-emscripten-core');
    const { default: variant } = await import('@jitl/quickjs-wasmfile-release-sync');
    const wasm = await readFile(new URL(import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')));
    const compiled = await WebAssembly.compile(wasm);
    const first = await newQuickJSWASMModuleFromVariant(newVariant(variant, { wasmModule: compiled }));
    const second = await newQuickJSWASMModuleFromVariant(newVariant(variant, { wasmModule: compiled }));
    const context = first.newContext(), other = second.newContext();
    try {
      const value = context.unwrapResult(context.evalCode(
        'globalThis.fixtureInstallationMarker = 1; JSON.stringify({ value: 2 + 3, process: typeof process })'));
      const isolated = other.unwrapResult(other.evalCode('typeof fixtureInstallationMarker'));
      try {
        console.log(JSON.stringify({ ...JSON.parse(context.getString(value)),
          isolatedMemory: first.getWasmMemory().buffer !== second.getWasmMemory().buffer,
          otherInstallation: other.getString(isolated) }));
      } finally { isolated.dispose(); value.dispose(); }
    } finally { other.dispose(); context.dispose(); }
  `;
  const result = spawnSync(process.execPath,
    ['--import', `data:text/javascript,${encodeURIComponent(hook)}`, '--input-type=module', '-e', program], {
      cwd: f.output, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', windowsHide: true, timeout: 20000,
    });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, '');
  assert.deepEqual(JSON.parse(result.stdout), {
    value: 5, process: 'undefined', isolatedMemory: true, otherInstallation: 'undefined',
  });
});

for (const pkg of policy.packages) {
  test(`reviewed engine manifest rejects drift: ${pkg.name}`, async t => {
    const f = fixture(t);
    f.editManifest(`node_modules/${pkg.name}`, { description: 'altered fixture manifest' });
    await rejected(f, 'ENGINE_MANIFEST_MISMATCH');
  });
  test(`reviewed engine license rejects drift: ${pkg.name}`, async t => {
    const f = fixture(t);
    f.write(`node_modules/${pkg.name}/LICENSE`, 'Altered fixture license\n');
    await rejected(f, 'ENGINE_LICENSE_MISMATCH');
  });
}

for (const [field, value] of [
  ['integrity', `sha512-${createHash('sha512').update('not-the-reviewed-tarball').digest('base64')}`],
  ['resolved', 'https://registry.npmjs.org/@jitl/quickjs-wasmfile-release-sync/-/unreviewed-0.32.0.tgz'],
]) {
  test(`reviewed engine requires the exact registry ${field}`, async t => {
    const f = fixture(t);
    f.lock.packages[`node_modules/${engine.name}`][field] = value;
    f.saveLock();
    await rejected(f, 'ENGINE_LOCK_MISMATCH');
  });
}

test('a matching installed/locked but unreviewed engine version is rejected', async t => {
  const f = fixture(t), location = `node_modules/${engine.name}`;
  f.editManifest(location, { version: '0.32.1' });
  f.lock.packages[location].version = '0.32.1';
  f.saveLock();
  await rejected(f, 'ENGINE_LOCK_MISMATCH');
});

test('QuickJS dependency declarations must use an exact version', async t => {
  const f = fixture(t), location = 'packages/script-host';
  const dependencies = { ...readJSON(join(f.root, location, 'package.json')).dependencies,
    [engine.name]: '^0.32.0' };
  f.editManifest(location, { dependencies });
  f.lock.packages[location].dependencies = dependencies;
  f.saveLock();
  await rejected(f, 'ENGINE_VERSION_PIN');
});

test('engine license declarations are checked independently of package identity', async t => {
  const f = fixture(t), location = `node_modules/${engine.name}`;
  f.editManifest(location, { license: 'UNLICENSED' });
  f.lock.packages[location].license = 'UNLICENSED';
  f.saveLock();
  await rejected(f, 'ENGINE_LICENSE_MISMATCH');
});

test('the central redistributed engine license must match the reviewed original', async t => {
  const f = fixture(t);
  f.write(policy.license.path, 'Not the QuickJS and quickjs-emscripten license\n');
  await rejected(f, 'ENGINE_LICENSE_MISMATCH');
});

test('missing engine license cannot produce a publishable payload', async t => {
  const f = fixture(t);
  const target = assertOwnedPath(f.area, join(f.root, `node_modules/${engine.name}/LICENSE`));
  rmSync(target);
  await rejected(f, 'MISSING_SOURCE');
});

test('a changed WASM byte is rejected despite its trusted package/path and valid lock', async t => {
  const f = fixture(t), bytes = readFileSync(join(f.root, wasmPath));
  bytes[bytes.length - 1] ^= 1;
  f.write(wasmPath, bytes);
  const manifest = await rejected(f, 'ENGINE_HASH_MISMATCH');
  assert.equal(manifest.incompleteFile, wasmPath);
});

test('missing advertised WASM cannot leave a platform-pending payload', async t => {
  const f = fixture(t), target = assertOwnedPath(f.area, join(f.root, wasmPath));
  rmSync(target);
  await rejected(f, 'MISSING_PUBLIC_FILE');
});

for (const path of [
  `node_modules/${engine.name}/extra.wasm`,
  `node_modules/${engine.name}/dist/extra.wasm`,
  `node_modules/${engine.name}/dist/binding.node`,
  'node_modules/quickjs-emscripten-core/dist/emscripten-module.wasm',
  'packages/script-host/src/emscripten-module.wasm',
]) {
  test(`only the reviewed engine artifact may be binary: ${path}`, async t => {
    const f = fixture(t);
    f.write(path, readFileSync(join(repository, wasmPath)));
    await rejected(f, 'NATIVE_PAYLOAD');
  });
}

test('engine policy cannot widen the trusted binary path', async t => {
  const f = fixture(t), altered = structuredClone(policy);
  altered.packages.find(pkg => pkg.name === engine.name).wasm.path = 'dist/other.wasm';
  f.write(policyPath, altered);
  await rejected(f, 'INVALID_ENGINE_POLICY');
});

test('trusted WASM ancestor junctions are rejected rather than dereferenced', async t => {
  const f = fixture(t), target = assertOwnedPath(f.area, join(f.root, `node_modules/${engine.name}/dist`));
  const saved = assertOwnedPath(f.area, join(f.area, 'saved-dist'), { allowMissing: true });
  renameSync(target, saved);
  symlinkSync(saved, target, process.platform === 'win32' ? 'junction' : 'dir');
  await rejected(f, 'UNSAFE_SYMLINK');
});

test('Windows wrapper relocates its entry onto the same common module/engine closure', async t => {
  const f = fixture(t);
  const manifest = await packageWindowsRuntime({ ...f, product: 'aggregate' });
  assert.equal(manifest.status, 'complete');
  assert.deepEqual(manifest.files.filter(file => file.path.startsWith('app/')).map(file => file.path), [
    'app/bootstrap.mjs', 'app/modules/music-script.mjs',
    'app/windows/bootstrap.mjs', 'app/windows/entries/aggregate.mjs',
  ]);
  assert.deepEqual(readFileSync(join(f.output, 'app/modules/music-script.mjs')),
    readFileSync(join(f.root, 'apps/runtime/modules/music-script.mjs')));
  assert.ok(readFileSync(join(f.output, manifest.entry), 'utf8').includes("from '../../modules/music-script.mjs'"));
  assert.deepEqual(manifest.files.filter(file => /\.wasm$/i.test(file.path)).map(file => file.path), [wasmPath]);
});

test('native test bundles use the verified closure and never overwrite an existing bundle', async t => {
  const f = fixture(t), output = join(f.area, 'bundles');
  const manifest = await prepareWindowsTestBundles({ root: f.root, output });
  assert.equal(manifest.status, 'complete');
  assert.equal(hash(readFileSync(join(output, 'aggregate', wasmPath))), engine.wasm.sha256);
  const before = readFileSync(join(output, 'aggregate/runtime-manifest.json'));
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output }), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(readFileSync(join(output, 'aggregate/runtime-manifest.json')), before);
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: 'relative-bundles' }),
    { code: 'INVALID_TEST_OUTPUT' });
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: repository }), /leaves its Windows owner/);
});

test('test bundle CLI rejects extra or missing arguments before packaging', () => {
  const script = join(repository, 'scripts/windows/prepare-test-bundles.mjs');
  for (const args of [[], ['--help'], [temporaryRoot, 'extra']]) {
    const result = spawnSync(process.execPath, [script, ...args], {
      cwd: repository, encoding: 'utf8', windowsHide: true, timeout: 10000,
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /^CLI_USAGE:/);
    assert.equal(result.stdout, '');
  }
  assert.equal(existsSync(join(repository, 'aggregate')), false);
});
