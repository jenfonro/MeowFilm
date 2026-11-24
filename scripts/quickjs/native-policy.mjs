import { iosTargetPolicy, lockedNodeHeaders } from './native-inputs.mjs';

export const NATIVE_POLICY_PATH = 'scripts/script-host-native.json';
export const NATIVE_ARTIFACT_PATH = 'packages/script-host/build/Release/script_host_native.node';
export const NATIVE_RECEIPT_PATH = 'packages/script-host/build/Release/script-host-native.json';
export const SOURCE_SDK_PATH = 'packages/script-host/build/Release/source-sdk.js';

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const semver = /^\d+\.\d+\.\d+$/;
export const isSha256 = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);

function fail(code, message) {
  throw Object.assign(new Error(message), { code });
}

function check(condition, code, message) {
  if (!condition) fail(code, message);
}

/** Validate scripts/script-host-native.json as the single source of truth without duplicating pinned values. */
export function validateNativePolicy(policy) {
  check(isObject(policy) && policy.schemaVersion === 1 && policy.abi === 1 && policy.napi === 8,
    'INVALID_NATIVE_POLICY', 'Native policy must pin schemaVersion 1, ABI 1 and N-API 8');
  check(policy.artifact?.path === NATIVE_ARTIFACT_PATH && policy.artifact?.manifest === NATIVE_RECEIPT_PATH,
    'INVALID_NATIVE_POLICY', 'Native policy must pin the script-host artifact and manifest paths');
  check(isObject(policy.sources)
    && Object.keys(policy.sources).sort().join(',') === 'openssl,quickjs,zlib',
  'INVALID_NATIVE_POLICY', 'Native policy must declare quickjs, openssl and zlib sources');
  const sources = {};
  for (const [name, source] of Object.entries(policy.sources)) {
    const expectedSourcePath = name === 'quickjs' ? 'native/JavaScript/quickjs' : `tmp/build/runtime-native/${name}`;
    check(isObject(source)
      && typeof source.version === 'string' && source.version.length > 0
      && typeof source.commit === 'string' && /^[a-f0-9]{40}$/.test(source.commit)
      && typeof source.url === 'string' && source.url.startsWith('https://')
      && isSha256(source.sha256)
      && typeof source.license?.path === 'string' && source.license.path.startsWith('LICENSES/')
      && isSha256(source.license?.sha256)
      && source.archivePath === `tmp/build/runtime-native/${name}.tar.gz`
      && source.sourcePath === expectedSourcePath,
    'INVALID_NATIVE_POLICY', `Invalid native source policy: ${name}`);
    sources[name] = { version: source.version, commit: source.commit, url: source.url, sha256: source.sha256 };
  }
  const quickjs = policy.sources.quickjs;
  check(isObject(quickjs.files) && Object.keys(quickjs.files).length > 0
    && Object.entries(quickjs.files).every(([file, hash]) => /^[a-z0-9-]+\.[ch]$/.test(file) && isSha256(hash)),
  'INVALID_NATIVE_POLICY', 'QuickJS policy must pin the shared source files');
  const unicode = quickjs.unicodeLicense;
  check(unicode?.path === 'LICENSES/Unicode-3.0.txt' && unicode.source === 'libunicode-table.h'
    && isSha256(unicode.sha256),
  'INVALID_NATIVE_POLICY', 'QuickJS policy must pin the Unicode license');
  check(Array.isArray(policy.patches), 'INVALID_NATIVE_POLICY', 'Native policy patches must be an array');
  const seenPatches = new Set();
  for (const patch of policy.patches) {
    check(isObject(patch)
      && typeof patch.path === 'string' && patch.path.endsWith('.patch') && !seenPatches.has(patch.path)
      && typeof patch.source === 'string' && Object.hasOwn(policy.sources, patch.source)
      && isSha256(patch.sha256),
    'INVALID_NATIVE_POLICY', `Invalid native patch policy: ${String(patch?.path)}`);
    seenPatches.add(patch.path);
  }
  check(Array.isArray(policy.mandatoryInputs)
    && policy.mandatoryInputs.every(path => typeof path === 'string' && path.length > 0)
    && Array.isArray(policy.inputDirectories)
    && policy.inputDirectories.includes('packages/script-host/native'),
  'INVALID_NATIVE_POLICY', 'Native policy must list mandatory inputs and native input directories');
  const nodeHeaders = lockedNodeHeaders(policy);
  check(typeof policy.build?.node === 'string' && semver.test(policy.build.node)
    && typeof policy.build?.nodeGyp === 'string' && policy.build.nodeGyp.length > 0
    && typeof policy.build?.esbuild === 'string' && policy.build.esbuild.length > 0
    && nodeHeaders.version === policy.build.node
    && nodeHeaders.archivePath === 'tmp/build/runtime-native/node-headers.tar.gz'
    && isSha256(nodeHeaders.sha256),
  'INVALID_NATIVE_POLICY', 'Native policy must pin build tools and Node headers');
  check(Array.isArray(policy.ios?.targets) && policy.ios.targets.length > 0
    && policy.ios.targets.every(entry => iosTargetPolicy(policy, entry.sdk, entry.arch) === entry)
    && policy.ios.framework === 'MeowScriptHost.framework/MeowScriptHost'
    && policy.ios.cache === '.cache/ios-native'
    && typeof policy.ios.minimumOS === 'string' && policy.ios.minimumOS.length > 0
    && policy.ios.nodeMobilePolicy === 'scripts/ios-node-mobile.json'
    && Array.isArray(policy.ios.exports) && policy.ios.exports.length > 0,
  'INVALID_NATIVE_POLICY', 'Native policy must pin valid iOS target settings');
  return { sources, nodeHeaders };
}

/** Validate scripts/ios-node-mobile.json structure without duplicating pinned versions/hashes. */
export function validateNodeMobilePolicy(mobile) {
  check(isObject(mobile) && mobile.schemaVersion === 1
    && typeof mobile.version === 'string' && semver.test(mobile.version)
    && typeof mobile.archive?.url === 'string' && mobile.archive.url.startsWith('https://')
    && isSha256(mobile.archive?.sha256)
    && typeof mobile.license?.path === 'string' && mobile.license.path.startsWith('LICENSES/')
    && isSha256(mobile.license?.sha256),
  'INVALID_NATIVE_POLICY', 'Invalid iOS Node Mobile policy');
  return mobile;
}
