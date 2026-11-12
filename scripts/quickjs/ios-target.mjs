import { readFileSync } from 'node:fs';
import { iosTargetPolicy } from './native-inputs.mjs';

const nativePolicy = JSON.parse(readFileSync(new URL('../script-host-native.json', import.meta.url), 'utf8'));

// One target identity is shared by the builder, offline packager and loader.
export function iosTarget(sdk, arch, minimumOS = nativePolicy.ios.minimumOS) {
  const target = iosTargetPolicy(nativePolicy, sdk, arch);
  if (!['iphoneos', 'iphonesimulator'].includes(sdk)
      || !target || target.platform !== 'ios'
      || target.variant !== (sdk === 'iphonesimulator' ? 'simulator' : null)
      || minimumOS !== nativePolicy.ios.minimumOS) {
    throw Object.assign(new Error('Unsupported iOS native target'), { code: 'NATIVE_TARGET_MISMATCH' });
  }
  return { ...target, minimumOS };
}

export function iosArtifacts(target) {
  const selected = iosTarget(target.sdk, target.arch, target.minimumOS);
  const directory = `.cache/ios-native/${selected.sdk}-${selected.xcframeworkArchitecture}`;
  return {
    directory,
    framework: `${directory}/MeowScriptHost.framework`,
    artifact: `${directory}/MeowScriptHost.framework/MeowScriptHost`,
    info: `${directory}/MeowScriptHost.framework/Info.plist`,
    receipt: `${directory}/build/Release/script-host-native.json`,
    sdk: `${directory}/build/Release/source-sdk.js`,
    catWebValues: `${directory}/build/Release/cat-web-values.cjs`,
    exports: `${directory}/exports.json`,
  };
}

// Fixed build-time value closure, never Undici's package entry or HTTP transport.
export const IOS_CAT_WEB_VALUES = Object.freeze({
  version: '6.29.1',
  resolved: 'https://registry.npmjs.org/undici/-/undici-6.29.1.tgz',
  integrity: 'sha512-EvRlUt/QqAEvXdQeRMYUfwOQsfzpDztykrG2owGEE/rBOBoYv9uF39ZzsNDh4lTFHhTr2XOZjKkYgueD12ZmGA==',
  license: { path: 'node_modules/undici/LICENSE',
    sha256: 'a6db8096b2707bc0102d256917d4d33f298ba36d8c3f25de067a2b5bb379db27' },
  modules: [
    'lib/core/constants.js',
    'lib/core/errors.js',
    'lib/core/symbols.js',
    'lib/core/tree.js',
    'lib/core/util.js',
    'lib/web/fetch/body.js',
    'lib/web/fetch/constants.js',
    'lib/web/fetch/data-url.js',
    'lib/web/fetch/dispatcher-weakref.js',
    'lib/web/fetch/file.js',
    'lib/web/fetch/formdata-parser.js',
    'lib/web/fetch/formdata.js',
    'lib/web/fetch/global.js',
    'lib/web/fetch/headers.js',
    'lib/web/fetch/request.js',
    'lib/web/fetch/response.js',
    'lib/web/fetch/symbols.js',
    'lib/web/fetch/util.js',
    'lib/web/fetch/webidl.js',
  ],
  builtins: [
    'node:assert', 'node:buffer', 'node:crypto', 'node:events', 'node:http', 'node:net',
    'node:perf_hooks', 'node:querystring', 'node:stream', 'node:util', 'node:util/types',
    'node:worker_threads', 'node:zlib',
  ],
});

export function checkIOSMachO(bytes, target) {
  const fail = () => { throw Object.assign(new Error('iOS Mach-O target mismatch'), { code: 'NATIVE_TARGET_MISMATCH' }); };
  const selected = iosTarget(target.sdk, target.arch, target.minimumOS);
  const [major, minor] = selected.minimumOS.split('.').map(Number);
  const minimum = (major << 16) | (minor << 8);
  if (bytes.length < 32 || bytes.readUInt32LE(0) !== 0xfeedfacf
      || bytes.readUInt32LE(4) !== { x86_64: 0x01000007, arm64: 0x0100000c }[selected.xcframeworkArchitecture]
      || bytes.readUInt32LE(12) !== 6) fail(); // MH_DYLIB, never a host .node bundle.
  const count = bytes.readUInt32LE(16), end = 32 + bytes.readUInt32LE(20);
  if (end > bytes.length) fail();
  let offset = 32, builds = 0;
  const dylibs = [];
  for (let index = 0; index < count; index++) {
    if (offset + 8 > end) fail();
    const command = bytes.readUInt32LE(offset), size = bytes.readUInt32LE(offset + 4);
    if (size < 8 || offset + size > end) fail();
    if (command === 0x32) { // LC_BUILD_VERSION
      if (size < 24 || bytes.readUInt32LE(offset + 8) !== (selected.variant === 'simulator' ? 7 : 2)
          || bytes.readUInt32LE(offset + 12) !== minimum) fail();
      builds++;
    }
    if ([0xc, 0x80000018, 0x8000001f, 0x80000023].includes(command)) {
      if (size < 24) fail();
      const start = offset + bytes.readUInt32LE(offset + 8);
      const stop = bytes.indexOf(0, start);
      if (start < offset + 24 || stop < start || stop >= offset + size) fail();
      dylibs.push(bytes.toString('utf8', start, stop));
    }
    offset += size;
  }
  if (offset !== end || builds !== 1
      || !dylibs.includes('@rpath/NodeMobile.framework/NodeMobile')
      || dylibs.some(name => ![
        '@rpath/NodeMobile.framework/NodeMobile', '/usr/lib/libc++.1.dylib',
        '/usr/lib/libSystem.B.dylib',
      ].includes(name))) fail();
  return { dylibs };
}

export function checkIOSSymbols(exported, undefinedSymbols, expected) {
  const fail = () => { throw Object.assign(new Error('iOS native symbol boundary mismatch'), { code: 'NATIVE_TARGET_MISMATCH' }); };
  if (!Array.isArray(exported) || JSON.stringify([...exported].sort()) !== JSON.stringify([...expected].sort())
      || typeof undefinedSymbols !== 'string') fail();
  const lines = undefinedSymbols.split(/\r?\n/);
  const nodeImports = lines.filter(line => /\(from NodeMobile(?:\)|[/.])/.test(line));
  if (!nodeImports.length || nodeImports.some(line => !/\bexternal _(?:napi_|node_api_)/.test(line))
      || lines.some(line => /_(?:JS_|js_|EVP_|OSSL_|OPENSSL_|zlibVersion|inflate|deflate|z_)/.test(line))) fail();
}
