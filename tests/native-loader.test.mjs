import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { iosTarget } from '../scripts/quickjs/ios-target.mjs';
import { sourceSDKPath } from '../packages/script-host/src/portable-source-session.js';

// A VM supplies only filesystem/process/dlopen doubles. No native code is loaded
// and process.platform is never changed in the actual test process.
test('portable source SDK paths follow the shared XCFramework architecture identity', () => {
  const simulator = iosTarget('iphonesimulator', 'x64');
  assert.equal(sourceSDKPath('ios', 'x64', { platform: 'ios', target: simulator }),
    '.cache/ios-native/iphonesimulator-x86_64/build/Release/source-sdk.js');
  const device = iosTarget('iphoneos', 'arm64');
  assert.equal(sourceSDKPath('ios', 'arm64', { platform: 'ios', target: device }),
    '.cache/ios-native/iphoneos-arm64/build/Release/source-sdk.js');
  assert.equal(sourceSDKPath('ios', 'x64', {
    platform: 'ios', target: { ...simulator, xcframeworkArchitecture: 'armv7' },
  }), 'packages/script-host/build/Release/source-sdk.js');
});

test('loader enforces bundle identity on iOS and preserves desktop byte-hash admission', () => {
  const source = fileURLToPath(new URL('../packages/script-host/src/native-engine.js', import.meta.url));
  const policy = fileURLToPath(new URL('../scripts/script-host-native.json', import.meta.url));
  const result = spawnSync(process.execPath, ['--experimental-vm-modules', '--input-type=module', '-e', `
    import assert from 'node:assert/strict';
    import { SourceTextModule, SyntheticModule, createContext } from 'node:vm';
    import { readFileSync } from 'node:fs';
    import * as crypto from 'node:crypto';
    import * as path from 'node:path';
    import * as url from 'node:url';
    const text = readFileSync(${JSON.stringify(source)}, 'utf8');
    const nativePolicy = JSON.parse(readFileSync(${JSON.stringify(policy)}, 'utf8'));
    const sha = data => crypto.createHash('sha256').update(data).digest('hex');
    const artifact = Buffer.from('desktop-native-fixture');
    const nativePath = 'packages/script-host/build/Release/script_host_native.node';
    const identity = 'a'.repeat(64);
    async function scenario({ platform = 'ios', arch = 'x64', edit = () => {}, loadedEdit = () => {},
      self = 'file:///Fixture.app/Runtime/node_modules/@meowfilm/script-host/src/native-engine.js',
      linked = '', expected = 'FIXTURE_LOADED', changedDesktop = false, napiVersion = '8',
      duplicate = false, duplicateFailure = false } = {}) {
      const reads = [], loads = [], copies = [];
      let creates = 0;
      const nativeFailure = Object.assign(new Error('duplicate failed'), { code: 'DESCRIPTOR_UNAVAILABLE' });
      const targetPolicy = nativePolicy.ios.targets.find(value =>
        value.sdk === 'iphonesimulator' && value.arch === arch);
      const target = { ...targetPolicy, minimumOS: nativePolicy.ios.minimumOS };
      const loadedTarget = { ...target };
      const manifest = { schemaVersion: 1, abi: 1, napi: 8, platform, arch,
        artifact: platform === 'ios'
          ? { path: '.cache/ios-native/iphonesimulator-' + target.xcframeworkArchitecture + '/MeowScriptHost.framework/MeowScriptHost' }
          : { path: nativePath, bytes: artifact.length, sha256: sha(artifact) },
        target, bindingIdentity: identity };
      edit(manifest);
      const loaded = { abi: 1, napi: 8, bindingIdentity: identity, ...loadedTarget,
        create() { creates++; throw Object.assign(new Error('loaded'), { code: 'FIXTURE_LOADED' }); },
        duplicateBootstrapDescriptor(descriptor) {
          copies.push(descriptor);
          if (duplicateFailure) throw nativeFailure;
          return descriptor + 100;
        } };
      loadedEdit(loaded);
      const convert = value => value instanceof URL ? url.fileURLToPath(value) : String(value);
      const fs = {
        lstatSync(value) {
          const name = convert(value);
          return { isDirectory: () => name.endsWith('/') || !/\\.(?:node|json|js)$/.test(name)
              && !name.endsWith('/MeowScriptHost'),
            isFile: () => /\\.(?:node|json|js)$/.test(name) || name.endsWith('/MeowScriptHost'),
            isSymbolicLink: () => linked && name.endsWith(linked) };
        },
        readFileSync(value) {
          const name = convert(value); reads.push(name);
          if (name.endsWith('.json')) return JSON.stringify(manifest);
          if (name.endsWith('.node')) return changedDesktop ? Buffer.from('changed') : artifact;
          throw new Error('Framework bytes must not be hashed after signing');
        },
      };
      const process = { platform, arch, versions: { napi: napiVersion },
        env: { MEOW_NATIVE_PATH: '/untrusted/arbitrary.node' },
        dlopen(module, executable, flags) { loads.push({ executable, flags }); module.exports = loaded; } };
      const context = createContext({ process, URL, Buffer });
      const builtins = { 'node:crypto': crypto, 'node:fs': fs, 'node:path': path, 'node:url': url,
        'node:os': { constants: { dlopen: { RTLD_NOW: 2, RTLD_LOCAL: 4 } } },
        'node:module': { createRequire: () => executable => { loads.push({ executable }); return loaded; } } };
      const module = new SourceTextModule(text, { context, initializeImportMeta(meta) { meta.url = self; } });
      await module.link(specifier => {
        const values = builtins[specifier];
        return new SyntheticModule(Object.keys(values), function() {
          for (const [name, value] of Object.entries(values)) this.setExport(name, value);
        }, { context });
      });
      await module.evaluate();
      assert.equal(loads.length, 0, 'Importing the internal helper must not load native code');
      if (duplicate && expected === 'FIXTURE_LOADED') {
        assert.equal(module.namespace.duplicateBootstrapDescriptor(41), 141);
        assert.equal(module.namespace.duplicateBootstrapDescriptor(43), 143);
        assert.deepEqual(copies, [41, 43]);
      } else {
        assert.throws(() => duplicate ? module.namespace.duplicateBootstrapDescriptor(41)
          : module.namespace.NativeEngine.create({ source: '({})', path: '/untrusted/arbitrary.node' }),
        error => duplicateFailure ? error === nativeFailure : error.code === expected, expected);
      }
      if (duplicate) assert.equal(creates, 0, 'Bootstrap handoff must not create a QuickJS owner');
      if (expected === 'FIXTURE_LOADED') {
        assert.equal(loads.length, 1);
        if (platform === 'ios') {
          assert.equal(loads[0].executable, '/Fixture.app/Frameworks/MeowScriptHost.framework/MeowScriptHost');
          assert.equal(loads[0].flags, 6);
          assert.equal(reads.some(name => name.endsWith('/MeowScriptHost')), false);
        } else assert.ok(reads.some(name => name.endsWith('.node')));
      } else if (expected === 'NATIVE_ARTIFACT_INVALID') {
        assert.equal(loads.length, 0);
        assert.deepEqual(copies, []);
      }
      if (duplicateFailure) assert.deepEqual(copies, [41]);
    }
    await scenario();
    await scenario({ arch: 'arm64' });
    await scenario({ arch: 'arm64', edit: value => {
      value.target.sdk = 'iphoneos';
      value.target.variant = null;
      value.artifact.path = '.cache/ios-native/iphoneos-arm64/MeowScriptHost.framework/MeowScriptHost';
    }, loadedEdit: value => { value.sdk = 'iphoneos'; } });
    await scenario({ napiVersion: '7', expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ edit: value => { value.arch = 'arm64'; }, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ edit: value => { value.target.minimumOS = '16.0'; }, expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ arch: 'arm64', edit: value => { value.target.xcframeworkArchitecture = 'armv7'; },
      expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ edit: value => { value.artifact.path = '/arbitrary/native'; }, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ edit: value => { value.target.sdk = 'macosx'; }, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ linked: 'Frameworks/MeowScriptHost.framework', expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ self: 'file:///outside/node_modules/@meowfilm/script-host/src/native-engine.js', expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ loadedEdit: value => { value.bindingIdentity = 'b'.repeat(64); }, expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ loadedEdit: value => { value.sdk = 'iphoneos'; }, expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ loadedEdit: value => { value.abi = 2; }, expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ platform: 'linux' });
    await scenario({ platform: 'linux', changedDesktop: true, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ platform: 'darwin', edit: value => { value.arch = 'arm64'; }, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ duplicate: true });
    await scenario({ duplicate: true, platform: 'linux' });
    await scenario({ duplicate: true, platform: 'darwin' });
    await scenario({ duplicate: true, duplicateFailure: true, expected: 'DESCRIPTOR_UNAVAILABLE' });
    await scenario({ duplicate: true, loadedEdit: value => { delete value.duplicateBootstrapDescriptor; },
      expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ duplicate: true, platform: 'win32',
      loadedEdit: value => { delete value.duplicateBootstrapDescriptor; }, expected: 'NATIVE_ABI_MISMATCH' });
    await scenario({ duplicate: true, platform: 'linux', changedDesktop: true, expected: 'NATIVE_ARTIFACT_INVALID' });
    await scenario({ duplicate: true, loadedEdit: value => { value.bindingIdentity = 'b'.repeat(64); },
      expected: 'NATIVE_ABI_MISMATCH' });
    console.log('NATIVE_LOADER_BOUNDARIES_OK');
  `], { encoding: 'utf8', timeout: 15_000, env: { ...process.env, NODE_OPTIONS: '' } });
  assert.equal(result.signal, null, result.stderr);
  assert.equal(result.status, 0, result.stderr || result.error?.message);
  assert.match(result.stdout, /NATIVE_LOADER_BOUNDARIES_OK/);
});
