import test from 'node:test';
import assert from 'node:assert/strict';
import { iosTarget, iosArtifacts, checkIOSMachO, checkIOSSymbols } from '../scripts/quickjs/ios-target.mjs';

function executable({ sdk = 'iphonesimulator', arch = 'x64', minimum = 0x00110000,
  dependency = '@rpath/NodeMobile.framework/NodeMobile', platform, kind = 6 } = {}) {
  const dylib = Buffer.alloc(Math.ceil((24 + Buffer.byteLength(dependency) + 1) / 8) * 8);
  dylib.writeUInt32LE(0xc); dylib.writeUInt32LE(dylib.length, 4);
  dylib.writeUInt32LE(24, 8); dylib.write(dependency, 24);
  const version = Buffer.alloc(24);
  version.writeUInt32LE(0x32); version.writeUInt32LE(version.length, 4);
  version.writeUInt32LE(platform ?? (sdk === 'iphoneos' ? 2 : 7), 8);
  version.writeUInt32LE(minimum, 12);
  const header = Buffer.alloc(32);
  header.writeUInt32LE(0xfeedfacf); header.writeUInt32LE(arch === 'x64' ? 0x01000007 : 0x0100000c, 4);
  header.writeUInt32LE(kind, 12); header.writeUInt32LE(2, 16);
  header.writeUInt32LE(version.length + dylib.length, 20);
  return Buffer.concat([header, version, dylib]);
}

test('iOS targets preserve SDK identity and map Node x64 onto Xcode x86_64 cache paths', () => {
  assert.deepEqual(iosTarget('iphonesimulator', 'x64'),
    { sdk: 'iphonesimulator', arch: 'x64', minimumOS: '17.0' });
  assert.equal(iosArtifacts(iosTarget('iphonesimulator', 'x64')).artifact,
    '.cache/ios-native/iphonesimulator-x86_64/MeowScriptHost.framework/MeowScriptHost');
  for (const [sdk, arch] of [['iphoneos', 'arm64'], ['iphonesimulator', 'arm64'], ['iphonesimulator', 'x64']]) {
    assert.equal(iosArtifacts(iosTarget(sdk, arch)).catWebValues,
      `.cache/ios-native/${sdk}-${arch === 'x64' ? 'x86_64' : arch}/build/Release/cat-web-values.cjs`);
    assert.doesNotThrow(() => checkIOSMachO(executable({ sdk, arch }), iosTarget(sdk, arch)));
  }
  for (const [sdk, arch] of [['macosx', 'arm64'], ['iphoneos', 'x64'], ['iphonesimulator', 'x86_64']]) {
    assert.throws(() => iosTarget(sdk, arch), { code: 'NATIVE_TARGET_MISMATCH' });
  }
});

for (const [name, input] of [
  ['a host macOS dylib', { platform: 1 }],
  ['a device slice in simulator payload', { sdk: 'iphoneos' }],
  ['a wrong architecture', { arch: 'arm64' }],
  ['a wrong deployment target', { minimum: 0x00100000 }],
  ['a Node addon bundle in place of a framework', { kind: 8 }],
  ['a shared crypto dependency', { dependency: '/usr/local/lib/libcrypto.dylib' }],
  ['a system zlib dependency', { dependency: '/usr/lib/libz.1.dylib' }],
]) test(`iOS format admission rejects ${name}`, () => {
  assert.throws(() => checkIOSMachO(executable(input), iosTarget('iphonesimulator', 'x64')),
    { code: 'NATIVE_TARGET_MISMATCH' });
});

test('truncated or malformed Mach-O commands fail closed', () => {
  const target = iosTarget('iphonesimulator', 'x64');
  for (const length of [0, 8, 31, 40, 65]) {
    assert.throws(() => checkIOSMachO(executable().subarray(0, length), target), { code: 'NATIVE_TARGET_MISMATCH' });
  }
  const bytes = executable();
  bytes.writeUInt32LE(0, 36);
  assert.throws(() => checkIOSMachO(bytes, target), { code: 'NATIVE_TARGET_MISMATCH' });
});

test('framework exports only N-API registration and imports only N-API from NodeMobile', () => {
  const exports = ['_napi_register_module_v1', '_node_api_module_get_api_version_v1'];
  const napi = '                 (undefined) external _napi_create_object (from NodeMobile)';
  assert.doesNotThrow(() => checkIOSSymbols(exports, napi, exports));
  for (const [exported, imported] of [
    [[...exports, '_JS_NewRuntime'], napi],
    [exports.slice(0, 1), napi],
    [exports, napi + '\n(undefined) external _EVP_MD_fetch (from NodeMobile)'],
    [exports, napi + '\n(undefined) external _inflate (from NodeMobile)'],
    [exports, napi + '\n(undefined) external _BIO_new (from NodeMobile)'],
    [exports, napi + '\n(undefined) external _JS_NewRuntime (from NodeMobile)'],
    [exports, '(undefined) external _malloc (from libSystem)'],
  ]) assert.throws(() => checkIOSSymbols(exported, imported, exports), { code: 'NATIVE_TARGET_MISMATCH' });
});
