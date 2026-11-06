import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, renameSync, rmSync, symlinkSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { assertOwnedPath } from '../scripts/windows/working-paths.mjs';
import {
  asmjs, engine, fixture, hash, localLoader, localLoaderPath, localWasm, localWasmPath,
  policy, policyPath, readJSON, repository, taskContext, wasmPath, wasmPaths,
} from './runtime-engine-package-support.mjs';

const profiles = [
  { profile: 'windows-native', backend: engine, excluded: asmjs },
  { profile: 'ios-embedded', backend: asmjs, excluded: engine },
];
const profileModules = { 'windows-native': ['music', 'cat', 'source'], 'ios-embedded': ['music', 'cat'] };
const moduleFiles = { music: 'modules/music-script.mjs', cat: 'modules/cat-script.mjs', source: 'modules/source-runtime.mjs' };

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

for (const { profile, backend, excluded } of profiles) {
  test(`${profile} pins real lock/manifest/license/engine bytes without executing workspace code`, async t => {
    // The Windows case also verifies the API's unchanged default.
    const f = fixture(t, profile === 'windows-native' ? undefined : profile), manifest = await f.pack();
    assert.equal(manifest.status, 'platform-pending');
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.product, 'aggregate');
    assert.equal(manifest.assemblyProfile, profile);
    assert.deepEqual(manifest.modules, profileModules[profile]);
    assert.equal(manifest.sharedEntry, profile === 'ios-embedded' ? 'app/bootstrap.mjs' : undefined);
    assert.deepEqual(manifest.files.filter(file => file.path.startsWith('app/')).map(file => file.path),
      ['bootstrap.mjs', ...profileModules[profile].map(name => moduleFiles[name])].map(file => `app/${file}`).sort());
    assert.deepEqual(manifest.omittedPlatformDependencies, [{
      from: 'node_modules/@meowfilm/script-host', name: excluded.name,
      kind: 'dependency', range: '0.32.0', reason: 'platform', profile,
    }]);
    assert.deepEqual(manifest.omittedPlatformFiles, backend === engine ? [] : [localLoaderPath, localWasmPath]
      .map(path => ({ path, reason: 'platform', profile })));
    assert.deepEqual(manifest.omittedOptionalDependencies, []);
    assert.equal(manifest.dependencies.some(value => value.name === excluded.name), false);
    assert.equal(existsSync(join(f.output, 'node_modules', excluded.name)), false);
    assert.equal(manifest.files.some(file => file.path.startsWith(`node_modules/${excluded.name}/`)), false);
    assert.equal(manifest.inputs.some(input => input.path.startsWith(`node_modules/${excluded.name}/`)), false);
    assert.deepEqual(manifest.trustedWasm, backend === engine ? [{
      package: engine.name, version: engine.version, ...engine.wasm, path: wasmPath,
      licensePath: `node_modules/${engine.name}/LICENSE`, licenseSha256: engine.licenseSha256,
    }, {
      package: '@meowfilm/script-host', version: '1.0.0', ...localWasm, path: localWasmPath,
      taskContext: { revision: 1, source: policy.source, gitHead: taskContext.gitHead },
      licensePath: `node_modules/${engine.name}/LICENSE`, licenseSha256: engine.licenseSha256,
    }] : []);
    assert.deepEqual(manifest.trustedAsmjs, backend === asmjs ? asmjs.files.map(file => ({
      package: asmjs.name, version: asmjs.version, ...file, path: `node_modules/${asmjs.name}/${file.path}`,
      licensePath: `node_modules/${asmjs.name}/LICENSE`, licenseSha256: asmjs.licenseSha256,
    })) : []);
    assert.deepEqual(manifest.files.filter(file => /\.wasm$/i.test(file.path)).map(file => file.path),
      backend === engine ? wasmPaths : []);
    if (backend === engine) {
      assert.equal(manifest.entry, 'app/windows/entries/aggregate.mjs');
      for (const trusted of manifest.trustedWasm) {
        const wasm = readFileSync(join(f.output, trusted.path));
        assert.equal(wasm.length, trusted.bytes);
        assert.equal(hash(wasm), trusted.sha256);
        assert.deepEqual(wasm.subarray(0, 8), Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]));
        assert.equal(hash(readFileSync(join(f.output, trusted.licensePath))), trusted.licenseSha256);
      }
      const loader = readFileSync(join(f.output, localLoaderPath));
      assert.equal(loader.length, localLoader.bytes);
      assert.equal(hash(loader), localLoader.sha256);
    } else {
      for (const path of [localLoaderPath, localWasmPath]) assert.equal(existsSync(join(f.output, path)), false);
      assert.equal(Object.hasOwn(manifest, 'entry'), false);
      assert.equal(existsSync(join(f.output, 'app/windows')), false);
      assert.equal(manifest.files.some(file => /\.(?:node|dll|dylib|so|exe|a|o|wasm)$/i.test(file.path)), false);
    }
    const artifacts = backend.wasm ? [backend.wasm] : backend.files;
    for (const file of artifacts) {
      const path = `node_modules/${backend.name}/${file.path}`, bytes = readFileSync(join(f.output, path));
      assert.equal(bytes.length, file.bytes);
      assert.equal(hash(bytes), file.sha256);
      assert.deepEqual(manifest.files.find(file => file.path === path),
        { path, bytes: bytes.length, sha256: hash(bytes) });
    }
    for (const pkg of policy.packages.filter(pkg => pkg.name !== excluded.name)) {
      const dependency = manifest.dependencies.find(value => value.name === pkg.name);
      assert.equal(dependency.version, pkg.version);
      assert.equal(dependency.resolved, pkg.resolved);
      assert.equal(dependency.integrity, pkg.integrity);
      assert.equal(dependency.manifestSha256, hash(readFileSync(join(f.output, dependency.path, 'package.json'))));
      assert.equal(dependency.licenseSha256, hash(readFileSync(join(f.output, dependency.path, 'LICENSE'))));
    }
    const taskInputs = [taskContext.patch.path, taskContext.build.path, localLoader.path, localWasm.path];
    for (const path of [policyPath, policy.license.path,
      ...artifacts.map(file => `node_modules/${backend.name}/${file.path}`),
      ...(backend === engine ? taskInputs : []),
      'apps/runtime/bootstrap.mjs', ...profileModules[profile].map(name => `apps/runtime/${moduleFiles[name]}`)]) {
      assert.deepEqual(manifest.inputs.find(input => input.path === path), {
        path, sha256: hash(readFileSync(join(f.root, path))),
      });
      assert.equal(manifest.inputs.filter(input => input.path === path).length, 1);
    }
    if (backend !== engine) {
      for (const path of [...taskInputs, 'apps/runtime/modules/source-runtime.mjs'])
        assert.equal(manifest.inputs.some(input => input.path === path), false);
    }
    assert.ok(!manifest.files.some(file => file.path === taskContext.patch.path || file.path === taskContext.build.path));
    assert.deepEqual(readJSON(join(f.output, 'node_modules/@meowfilm/script-host/package.json')).dependencies,
      readJSON(join(f.root, 'packages/script-host/package.json')).dependencies);
  });
}

function runOffline(output, program, { profile = 'windows-native' } = {}) {
  const hook = `
    import { registerHooks, syncBuiltinESMExports } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep } from 'node:path';
    import http from 'node:http';
    import https from 'node:https';
    import net from 'node:net';
    const offline = () => { throw new Error('PAYLOAD_NETWORK_ACCESS'); };
    globalThis.fetch = offline;
    http.get = http.request = https.get = https.request = offline;
    net.connect = net.createConnection = net.Socket.prototype.connect = offline;
    syncBuiltinESMExports();
    ${profile === 'ios-embedded' ? `
      Object.defineProperty(process, 'platform', { value: 'ios' });
      globalThis.WebAssembly = undefined;
    ` : ''}
    const root = ${JSON.stringify(output)};
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
  return spawnSync(process.execPath,
    ['--import', `data:text/javascript,${encodeURIComponent(hook)}`, '--input-type=module', '-e', program], {
      cwd: output, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', windowsHide: true, timeout: 20000,
    });
}

test('packaged task-context engine isolates memory and exposes its native hook only to trusted SDK initialization', async t => {
  const f = fixture(t);
  for (const file of ['quickjs-engine.js', 'quickjs-module.js', 'errors.js']) {
    const path = `packages/script-host/src/${file}`;
    f.write(path, readFileSync(join(repository, path)));
  }
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
    const { default: loader } = await import(${JSON.stringify(`./${localLoaderPath}`)});
    const localVariant = { ...variant, importModuleLoader: async () => loader };
    const wasm = await readFile(new URL(${JSON.stringify(`./${localWasmPath}`)}, import.meta.url));
    const compiled = await WebAssembly.compile(wasm);
    const first = await newQuickJSWASMModuleFromVariant(newVariant(localVariant, { wasmModule: compiled }));
    const second = await newQuickJSWASMModuleFromVariant(newVariant(localVariant, { wasmModule: compiled }));
    const context = first.newContext(), other = second.newContext();
    let output;
    try {
      const value = context.unwrapResult(context.evalCode(
        'globalThis.fixtureInstallationMarker = 1; JSON.stringify({ value: 2 + 3, process: typeof process })'));
      const isolated = other.unwrapResult(other.evalCode('typeof fixtureInstallationMarker'));
      try {
        output = { ...JSON.parse(context.getString(value)),
          isolatedMemory: first.getWasmMemory().buffer !== second.getWasmMemory().buffer,
          otherInstallation: other.getString(isolated) };
      } finally { isolated.dispose(); value.dispose(); }
    } finally { other.dispose(); context.dispose(); }
    const { QuickJSEngine } = await import('./node_modules/@meowfilm/script-host/src/quickjs-engine.js');
    const engine = await QuickJSEngine.create({
      source: \`(() => {
        const nativeTaskContext = globalThis.__meowTaskContext;
        return {
          watchStartup() {},
          inspectTaskContext() {
            return JSON.stringify({
              captured: typeof nativeTaskContext,
              current: nativeTaskContext(),
              installed: globalThis.fixtureTaskContext,
            });
          },
        };
      })()\`,
      bridge: () => null,
      limits: { maxMemoryBytes: 16 * 1024 * 1024, maxStackBytes: 128 * 1024,
        initTimeoutMs: 10000, cleanupTimeoutMs: 1000 },
    });
    try {
      engine.evaluate(\`globalThis.fixtureTaskContext = {
        type: typeof globalThis.__meowTaskContext,
        own: Object.prototype.hasOwnProperty.call(globalThis, '__meowTaskContext'),
      };\`);
      output.taskContext = engine.callJSON('inspectTaskContext', []);
    } finally { engine.close(); }
    console.log(JSON.stringify(output));
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
    taskContext: { captured: 'function', current: 0, installed: { type: 'undefined', own: false } },
  });
});

test('packaged asmjs public exports execute offline without WebAssembly and isolate their heaps', async t => {
  const f = fixture(t, 'ios-embedded');
  await f.pack();
  const result = runOffline(f.output, `
    import assert from 'node:assert/strict';
    const { newQuickJSWASMModuleFromVariant } = await import('quickjs-emscripten-core');
    const { default: variant } = await import('@jitl/quickjs-asmjs-mjs-release-sync');
    const { default: loader } = await import('@jitl/quickjs-asmjs-mjs-release-sync/emscripten-module');
    assert.equal(typeof WebAssembly, 'undefined');
    const first = await newQuickJSWASMModuleFromVariant(variant);
    const second = await newQuickJSWASMModuleFromVariant({ ...variant,
      importModuleLoader: () => Promise.resolve(loader),
    });
    const context = first.newContext(), other = second.newContext();
    try {
      const value = context.unwrapResult(context.evalCode(
        'globalThis.fixtureInstallationMarker = 1; JSON.stringify({ value: 2 + 3, process: typeof process })'));
      const isolated = other.unwrapResult(other.evalCode('typeof fixtureInstallationMarker'));
      try {
        console.log(JSON.stringify({ ...JSON.parse(context.getString(value)),
          isolatedMemory: first.getWasmMemory().buffer !== second.getWasmMemory().buffer,
          otherInstallation: other.getString(isolated), webAssembly: typeof WebAssembly }));
      } finally { isolated.dispose(); value.dispose(); }
    } finally { other.dispose(); context.dispose(); }
  `, { profile: 'ios-embedded' });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, '');
  assert.deepEqual(JSON.parse(result.stdout), {
    value: 5, process: 'undefined', isolatedMemory: true, otherInstallation: 'undefined', webAssembly: 'undefined',
  });
});

for (const { profile } of profiles) {
  test(`${profile} real shared entry/modules and script-host engine are reachable in the offline payload`, async t => {
    const f = fixture(t, profile), output = join(f.area, 'real-payload');
    const manifest = await packageRuntime({ root: repository, product: 'aggregate', output, profile });
    assert.deepEqual(manifest.modules, profileModules[profile]);
    const result = runOffline(output, `
      import assert from 'node:assert/strict';
      const { createAppRuntime } = await import('./${profile === 'ios-embedded' ? manifest.sharedEntry : 'app/bootstrap.mjs'}');
      const { createMusicScriptModule } = await import('./app/modules/music-script.mjs');
      const { createCatScriptModule } = await import('./app/modules/cat-script.mjs');
      if (${JSON.stringify(profile)} === 'windows-native') {
        const { createSourceRuntimeModule } = await import('./app/modules/source-runtime.mjs');
        assert.equal(typeof createSourceRuntimeModule, 'function');
      }
      const { createScriptHost } = await import('@meowfilm/script-host');
      const { QuickJSEngine } = await import('./node_modules/@meowfilm/script-host/src/quickjs-engine.js');
      for (const value of [createAppRuntime, createMusicScriptModule, createCatScriptModule, createScriptHost])
        assert.equal(typeof value, 'function');
      const engine = await QuickJSEngine.create({
        source: '({ inspect() { return JSON.stringify({ value: String(21n * 2n), process: typeof process, require: typeof require }); } })',
        bridge: () => null,
        limits: { maxMemoryBytes: 32 * 1024 * 1024, maxStackBytes: 128 * 1024,
          initTimeoutMs: 10000, cleanupTimeoutMs: 1000 },
      });
      try { console.log(JSON.stringify(engine.callJSON('inspect', []))); }
      finally { engine.close(); }
    `, { profile });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.deepEqual(JSON.parse(result.stdout), { value: '42', process: 'undefined', require: 'undefined' });
  });
}

for (const input of [taskContext.patch, taskContext.build, ...taskContext.artifacts]) {
  test(`task-context input drift is rejected before copying: ${input.path}`, async t => {
    const f = fixture(t), bytes = readFileSync(join(f.root, input.path));
    bytes[bytes.length - 1] ^= 1;
    f.write(input.path, bytes);
    const manifest = await rejected(f, 'ENGINE_HASH_MISMATCH');
    assert.deepEqual(manifest.files, []);
  });
  test(`missing task-context input is rejected before copying: ${input.path}`, async t => {
    const f = fixture(t);
    rmSync(assertOwnedPath(f.area, join(f.root, input.path)));
    const manifest = await rejected(f, 'MISSING_SOURCE');
    assert.deepEqual(manifest.files, []);
  });
}

for (const artifact of taskContext.artifacts) {
  test(`task-context artifact byte count is independently pinned: ${artifact.path}`, async t => {
    const f = fixture(t), altered = structuredClone(policy);
    altered.taskContext.artifacts.find(input => input.path === artifact.path).bytes += 1;
    f.write(policyPath, altered);
    const manifest = await rejected(f, 'ENGINE_HASH_MISMATCH');
    assert.deepEqual(manifest.files, []);
  });
}

test('a matching policy hash cannot admit a task-context artifact without WASM v1 magic', async t => {
  const f = fixture(t), altered = structuredClone(policy), bytes = readFileSync(join(f.root, localWasm.path));
  bytes[0] ^= 1;
  f.write(localWasm.path, bytes);
  altered.taskContext.artifacts.find(artifact => artifact.path === localWasm.path).sha256 = hash(bytes);
  f.write(policyPath, altered);
  const manifest = await rejected(f, 'ENGINE_WASM_MAGIC');
  assert.deepEqual(manifest.files, []);
});

for (const [label, change] of [
  ['revision', value => { value.taskContext.revision = 2; }],
  ['upstream source', value => { value.source = 'https://example.invalid/quickjs'; }],
  ['upstream gitHead', value => { value.gitHead = '0'.repeat(40); }],
  ['task-context gitHead', value => { value.taskContext.gitHead = '0'.repeat(40); }],
  ['upstream archive', value => { value.taskContext.upstreamArchiveSha256 = '0'.repeat(64); }],
  ['toolchain', value => { value.taskContext.toolchainVersion = '5.0.2'; }],
  ['patch path', value => { value.taskContext.patch.path = 'scripts/quickjs/other.patch'; }],
  ['build path traversal', value => { value.taskContext.build.path = '../build.py'; }],
  ['loader path', value => { value.taskContext.artifacts[0].path = 'packages/script-host/src/other.mjs'; }],
  ['WASM path', value => { value.taskContext.artifacts[1].path = 'packages/script-host/src/other.wasm'; }],
  ['extra WASM', value => {
    value.taskContext.artifacts.push({ ...localWasm, path: 'packages/script-host/src/quickjs/extra.wasm' });
  }],
  ['extra task-context field', value => { value.taskContext.otherWasm = { ...localWasm }; }],
  ['extra reproduction field', value => { value.taskContext.build.args = ['unreviewed']; }],
  ['extra artifact field', value => { value.taskContext.artifacts[0].package = 'unreviewed'; }],
  ['duplicate artifact', value => { value.taskContext.artifacts[0] = { ...localWasm }; }],
  ['missing loader', value => {
    value.taskContext.artifacts = value.taskContext.artifacts.filter(artifact => artifact.path !== localLoader.path);
  }],
  ['unfilled patch hash', value => { value.taskContext.patch.sha256 = null; }],
  ['unfilled artifact hash', value => { value.taskContext.artifacts[0].sha256 = null; }],
  ['unfilled artifact bytes', value => { value.taskContext.artifacts[0].bytes = null; }],
]) {
  test(`task-context policy cannot change the reviewed ${label}`, async t => {
    const f = fixture(t), altered = structuredClone(policy);
    change(altered);
    f.write(policyPath, altered);
    const manifest = await rejected(f, 'INVALID_ENGINE_POLICY');
    assert.deepEqual(manifest.files, []);
  });
}

test('workspace files pruning cannot omit the reviewed task-context artifact pair', async t => {
  const f = fixture(t);
  f.editManifest('packages/script-host', { files: ['src/index.js'] });
  const manifest = await f.pack();
  for (const path of [localLoaderPath, localWasmPath]) assert.ok(manifest.files.some(file => file.path === path));
  assert.deepEqual(manifest.files.filter(file => /\.wasm$/i.test(file.path)).map(file => file.path), wasmPaths);
});

for (const { profile, backend, excluded } of profiles) {
  for (const pkg of policy.packages.filter(pkg => pkg.name !== excluded.name)) {
    test(`${profile} reviewed engine manifest rejects drift: ${pkg.name}`, async t => {
      const f = fixture(t, profile);
      f.editManifest(`node_modules/${pkg.name}`, { description: 'altered fixture manifest' });
      await rejected(f, 'ENGINE_MANIFEST_MISMATCH');
    });
    test(`${profile} reviewed engine license rejects drift: ${pkg.name}`, async t => {
      const f = fixture(t, profile);
      f.write(`node_modules/${pkg.name}/LICENSE`, 'Altered fixture license\n');
      await rejected(f, 'ENGINE_LICENSE_MISMATCH');
    });
  }

  for (const [field, value] of [
    ['integrity', `sha512-${createHash('sha512').update('not-the-reviewed-tarball').digest('base64')}`],
    ['resolved', `https://registry.npmjs.org/${backend.name}/-/unreviewed-0.32.0.tgz`],
  ]) {
    test(`${profile} reviewed engine requires the exact registry ${field}`, async t => {
      const f = fixture(t, profile);
      f.lock.packages[`node_modules/${backend.name}`][field] = value;
      f.saveLock();
      await rejected(f, 'ENGINE_LOCK_MISMATCH');
    });
  }

  test(`${profile} rejects a matching installed/locked but unreviewed engine version`, async t => {
    const f = fixture(t, profile), location = `node_modules/${backend.name}`;
    f.editManifest(location, { version: '0.32.1' });
    f.lock.packages[location].version = '0.32.1';
    f.saveLock();
    await rejected(f, 'ENGINE_LOCK_MISMATCH');
  });

  for (const pkg of [backend, excluded]) {
    test(`${profile} requires an exact direct backend declaration even if omitted: ${pkg.name}`, async t => {
      const f = fixture(t, profile), location = 'packages/script-host';
      const dependencies = { ...readJSON(join(f.root, location, 'package.json')).dependencies,
        [pkg.name]: '^0.32.0' };
      f.editManifest(location, { dependencies });
      f.lock.packages[location].dependencies = dependencies;
      f.saveLock();
      await rejected(f, 'ENGINE_VERSION_PIN');
    });

    test(`${profile} cannot disguise a backend as optional: ${pkg.name}`, async t => {
      const f = fixture(t, profile), location = 'packages/script-host';
      const optionalDependencies = { [pkg.name]: '0.32.0' };
      f.editManifest(location, { optionalDependencies });
      f.lock.packages[location].optionalDependencies = optionalDependencies;
      f.saveLock();
      await rejected(f, 'ENGINE_VERSION_PIN');
    });
  }

  test(`${profile} checks engine license declarations independently of package identity`, async t => {
    const f = fixture(t, profile), location = `node_modules/${backend.name}`;
    f.editManifest(location, { license: 'UNLICENSED' });
    f.lock.packages[location].license = 'UNLICENSED';
    f.saveLock();
    await rejected(f, 'ENGINE_LICENSE_MISMATCH');
  });

  test(`${profile} requires the central redistributed engine license to match the reviewed original`, async t => {
    const f = fixture(t, profile);
    f.write(policy.license.path, 'Not the QuickJS and quickjs-emscripten license\n');
    await rejected(f, 'ENGINE_LICENSE_MISMATCH');
  });

  test(`${profile} missing engine license cannot produce a publishable payload`, async t => {
    const f = fixture(t, profile);
    const target = assertOwnedPath(f.area, join(f.root, `node_modules/${backend.name}/LICENSE`));
    rmSync(target);
    await rejected(f, 'MISSING_SOURCE');
  });

  test(`${profile} still rejects root manifest/lock drift`, async t => {
    const f = fixture(t, profile);
    f.lock.packages[''].workspaces = ['other/*'];
    f.saveLock();
    await rejected(f, 'WORKSPACE_MISMATCH');
  });

  test(`${profile} still rejects script-host dependency manifest/lock drift`, async t => {
    const f = fixture(t, profile);
    const dependencies = { ...f.lock.packages['packages/script-host'].dependencies };
    delete dependencies[excluded.name];
    f.editManifest('packages/script-host', { dependencies });
    await rejected(f, 'DEPENDENCIES_MISMATCH');
  });

  test(`${profile} omits only the other platform backend when it is not installed`, async t => {
    const f = fixture(t, profile);
    rmSync(assertOwnedPath(f.area, join(f.root, 'node_modules', excluded.name)), { recursive: true });
    const manifest = await f.pack();
    assert.equal(manifest.status, 'platform-pending');
    assert.deepEqual(manifest.omittedOptionalDependencies, []);
    assert.deepEqual(manifest.omittedPlatformDependencies, [{
      from: 'node_modules/@meowfilm/script-host', name: excluded.name,
      kind: 'dependency', range: '0.32.0', reason: 'platform', profile,
    }]);
    assert.ok(manifest.dependencies.some(pkg => pkg.name === backend.name));
  });

  for (const name of [backend.name, '@jitl/quickjs-ffi-types']) {
    test(`${profile} missing required engine dependency is never omitted or substituted: ${name}`, async t => {
      const f = fixture(t, profile);
      rmSync(assertOwnedPath(f.area, join(f.root, 'node_modules', name)), { recursive: true });
      await rejected(f, 'MISSING_DEPENDENCY');
    });
  }

  test(`${profile} does not omit ordinary required script-host dependencies`, async t => {
    const f = fixture(t, profile), location = 'packages/script-host';
    const dependencies = { ...f.lock.packages[location].dependencies, 'missing-required': '1.0.0' };
    f.editManifest(location, { dependencies });
    f.lock.packages[location].dependencies = dependencies;
    f.saveLock();
    await rejected(f, 'MISSING_DEPENDENCY');
  });

  test(`${profile} cannot omit or ship the opposite backend requested by another consumer`, async t => {
    const f = fixture(t, profile), location = 'packages/runtime-bridge';
    const dependencies = { [excluded.name]: '0.32.0' };
    f.editManifest(location, { dependencies });
    f.lock.packages[location].dependencies = dependencies;
    f.saveLock();
    await rejected(f, 'ENGINE_PROFILE_MISMATCH');
  });
}

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

for (const file of asmjs.files) {
  const path = `node_modules/${asmjs.name}/${file.path}`;
  for (const change of ['byte', 'length']) {
    test(`asmjs rejects ${change} drift at its reviewed public file: ${file.path}`, async t => {
      const f = fixture(t, 'ios-embedded'), bytes = readFileSync(join(f.root, path));
      if (change === 'byte') bytes[bytes.length - 1] ^= 1;
      f.write(path, change === 'byte' ? bytes : bytes.subarray(0, bytes.length - 1));
      const manifest = await rejected(f, 'ENGINE_HASH_MISMATCH');
      assert.equal(manifest.incompleteFile, path);
    });
  }
  test(`missing public asmjs file cannot leave a platform-pending payload: ${file.path}`, async t => {
    const f = fixture(t, 'ios-embedded');
    rmSync(assertOwnedPath(f.area, join(f.root, path)));
    await rejected(f, 'MISSING_PUBLIC_FILE');
  });
}

for (const { profile, backend } of profiles) {
  for (const path of [
    `node_modules/${backend.name}/extra.wasm`,
    `node_modules/${backend.name}/dist/extra.wasm`,
    `node_modules/${backend.name}/dist/emscripten-module.node`,
    `node_modules/${backend.name}/dist/binding.node`,
    `node_modules/${backend.name}/dist/extra.dylib`,
    'node_modules/quickjs-emscripten-core/dist/emscripten-module.wasm',
    'packages/script-host/src/emscripten-module.wasm',
    'packages/script-host/src/quickjs/extra.wasm',
    'packages/script-host/src/quickjs/extra.WASM',
    'packages/script-host/src/quickjs/binding.node',
    'packages/script-host/src/quickjs/engine.dll',
    'packages/source-runtime/src/quickjs/emscripten-module.wasm',
  ]) {
    test(`${profile} only the reviewed WASM artifact may be binary: ${path}`, async t => {
      const f = fixture(t, profile);
      f.write(path, readFileSync(join(repository, wasmPath)));
      await rejected(f, 'NATIVE_PAYLOAD');
    });
  }
  if (profile === 'ios-embedded') {
    test('asmjs cannot acquire the WASM variant artifact at its otherwise reviewed path', async t => {
      const f = fixture(t, profile);
      f.write(`node_modules/${backend.name}/${engine.wasm.path}`, readFileSync(join(repository, wasmPath)));
      await rejected(f, 'NATIVE_PAYLOAD');
    });
  }

  test(`${profile} engine policy cannot widen the trusted binary path`, async t => {
    const f = fixture(t, profile), altered = structuredClone(policy);
    altered.packages.find(pkg => pkg.name === engine.name).wasm.path = 'dist/other.wasm';
    f.write(policyPath, altered);
    await rejected(f, 'INVALID_ENGINE_POLICY');
  });

  test(`${profile} trusted engine ancestor junctions are rejected rather than dereferenced`, async t => {
    const f = fixture(t, profile), target = assertOwnedPath(f.area, join(f.root, `node_modules/${backend.name}/dist`));
    const saved = assertOwnedPath(f.area, join(f.area, 'saved-dist'), { allowMissing: true });
    renameSync(target, saved);
    symlinkSync(saved, target, process.platform === 'win32' ? 'junction' : 'dir');
    await rejected(f, 'UNSAFE_SYMLINK');
  });
}

for (const path of ['packages/script-host/src/quickjs', 'scripts/quickjs']) {
  test(`task-context input ancestor junctions are rejected before copying: ${path}`, async t => {
    const f = fixture(t), target = assertOwnedPath(f.area, join(f.root, path));
    const saved = assertOwnedPath(f.area, join(f.area, 'saved-inputs'), { allowMissing: true });
    renameSync(target, saved);
    symlinkSync(saved, target, process.platform === 'win32' ? 'junction' : 'dir');
    const manifest = await rejected(f, 'UNSAFE_SYMLINK');
    assert.deepEqual(manifest.files, []);
  });
}

for (const [name, alter] of [
  ['unreviewed path', pkg => { pkg.files[0].path = 'dist/other.mjs'; }],
  ['duplicate path', pkg => { pkg.files[1].path = pkg.files[0].path; }],
  ['missing public module', pkg => { pkg.files.pop(); }],
  ['additional native file', pkg => { pkg.files.push({ ...pkg.files[0], path: 'dist/extra.node' }); }],
  ['WASM permission', pkg => { pkg.wasm = structuredClone(engine.wasm); }],
]) {
  test(`asmjs policy cannot widen or omit its reviewed files: ${name}`, async t => {
    const f = fixture(t, 'ios-embedded'), altered = structuredClone(policy);
    alter(altered.packages.find(pkg => pkg.name === asmjs.name));
    f.write(policyPath, altered);
    await rejected(f, 'INVALID_ENGINE_POLICY');
  });
}

for (const { profile, backend, excluded } of profiles) {
  test(`shared packaging CLI selects the fixed ${profile} profile`, t => {
    const f = fixture(t), script = join(f.root, 'scripts/package-runtime.mjs');
    f.write('scripts/package-runtime.mjs', readFileSync(join(repository, 'scripts/package-runtime.mjs')));
    const result = spawnSync(process.execPath, [script, '--product', 'aggregate', '--output', f.output,
      '--profile', profile], { cwd: f.area, encoding: 'utf8', windowsHide: true, timeout: 20000 });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.equal(JSON.parse(result.stdout).status, 'platform-pending');
    const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
    assert.equal(manifest.assemblyProfile, profile);
    assert.ok(manifest.dependencies.some(pkg => pkg.name === backend.name));
    assert.equal(manifest.dependencies.some(pkg => pkg.name === excluded.name), false);
  });
}

test('unknown profiles cannot create a payload or choose arbitrary backends', async t => {
  const f = fixture(t);
  for (const profile of ['ios', 'asmjs', '__proto__', '']) {
    await assert.rejects(packageRuntime({ root: f.root, product: 'aggregate', output: f.output, profile }),
      { code: 'INVALID_PROFILE' });
    assert.equal(existsSync(f.output), false);
  }
  const script = join(f.root, 'scripts/package-runtime.mjs');
  f.write('scripts/package-runtime.mjs', readFileSync(join(repository, 'scripts/package-runtime.mjs')));
  for (const [args, code] of [
    [['--profile', 'ios'], 'INVALID_PROFILE'],
    [['--profile'], 'CLI_USAGE'],
    [['--profile', 'ios-embedded', '--profile', 'windows-native'], 'CLI_USAGE'],
  ]) {
    const result = spawnSync(process.execPath, [script, '--product', 'aggregate', '--output', f.output, ...args],
      { encoding: 'utf8', windowsHide: true, timeout: 20000 });
    assert.equal(result.status, 1);
    assert.ok(result.stderr.startsWith(`${code}:`), result.stderr);
    assert.equal(result.stdout, '');
    assert.equal(existsSync(f.output), false);
  }
});
