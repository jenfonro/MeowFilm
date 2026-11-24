import { readFileSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { constants } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const artifactFile = new URL('../build/Release/script_host_native.node', import.meta.url);
const manifestFile = new URL('../build/Release/script-host-native.json', import.meta.url);
const sdkFile = new URL('../build/Release/source-sdk.js', import.meta.url);
const catWebValuesFile = new URL('../build/Release/cat-web-values.cjs', import.meta.url);

let binding;
let cachedManifest;
let sdkSource;
let catWebValues;

function nativeFault(code) {
  return Object.assign(new Error(code), { code });
}

function readReceiptSync() {
  if (cachedManifest) return cachedManifest;
  let manifest;
  try { manifest = JSON.parse(readFileSync(manifestFile, 'utf8')); }
  catch { throw nativeFault('NATIVE_ARTIFACT_INVALID'); }
  if (!manifest || typeof manifest !== 'object' || manifest.schemaVersion !== 1 ||
      manifest.abi !== 1 || manifest.napi !== 8) {
    throw nativeFault('NATIVE_ARTIFACT_INVALID');
  }
  cachedManifest = manifest;
  return manifest;
}

function isValidIOSTarget(platform, arch, manifest) {
  const target = manifest?.target;
  return platform === 'ios' && manifest?.platform === 'ios' && manifest?.arch === arch
    && ['iphoneos', 'iphonesimulator'].includes(target?.sdk)
    && target.platform === 'ios' && target.arch === arch
    && target.variant === (target.sdk === 'iphonesimulator' ? 'simulator' : null)
    && ['arm64', 'x86_64'].includes(target.xcframeworkArchitecture)
    && typeof target.minimumOS === 'string'
    && (target.sdk !== 'iphoneos' || target.arch === 'arm64')
    && ['arm64', 'x64'].includes(target.arch);
}

function sourceSDKPath(platform, arch, manifest) {
  return isValidIOSTarget(platform, arch, manifest)
    ? `.cache/ios-native/${manifest.target.sdk}-${manifest.target.xcframeworkArchitecture}/build/Release/source-sdk.js`
    : 'packages/script-host/build/Release/source-sdk.js';
}

export function nativeKernelIdentity() {
  const manifest = readReceiptSync();
  const quickjs = manifest.sources?.quickjs;
  if (!quickjs || typeof quickjs.version !== 'string' || typeof quickjs.commit !== 'string') {
    throw nativeFault('NATIVE_ARTIFACT_INVALID');
  }
  return `quickjs-ng@${quickjs.version}:${quickjs.commit}:${quickjs.sha256 ?? ''}`;
}

/**
 * Runtime launch checks entry & ABI/build identity only.
 * Full source, patch, hash and inventory verification runs at build/package time.
 */
export function loadBinding() {
  if (binding) return binding;
  const manifest = readReceiptSync();
  if (Number(process.versions.napi) < 8) throw nativeFault('NATIVE_ABI_MISMATCH');
  if (process.platform === 'ios') {
    const target = manifest.target;
    if (!isValidIOSTarget(process.platform, process.arch, manifest)
        || !/^[a-f0-9]{64}$/.test(manifest.bindingIdentity ?? '')
        || typeof manifest.sqlite?.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(manifest.sqlite.version)) {
      throw nativeFault('NATIVE_ARTIFACT_INVALID');
    }
    const self = fileURLToPath(import.meta.url);
    const suffix = '/Runtime/node_modules/@meowfilm/script-host/src/native-loader.js';
    if (!self.endsWith(suffix)) throw nativeFault('NATIVE_ARTIFACT_INVALID');
    const app = self.slice(0, -suffix.length);
    const executable = join(app, 'Frameworks/MeowScriptHost.framework/MeowScriptHost');
    const module = { exports: {} };
    try {
      process.dlopen(module, executable, constants.dlopen.RTLD_NOW | constants.dlopen.RTLD_LOCAL);
    } catch {
      throw nativeFault('NATIVE_ARTIFACT_INVALID');
    }
    const loaded = module.exports;
    if (loaded.abi !== 1 || loaded.napi !== 8 || loaded.bindingIdentity !== manifest.bindingIdentity
        || loaded.sdk !== target.sdk || loaded.arch !== target.arch || loaded.minimumOS !== target.minimumOS
        || loaded.storageSQLiteVersion !== manifest.sqlite.version) {
      throw nativeFault('NATIVE_ABI_MISMATCH');
    }
    binding = loaded;
    return binding;
  }
  if (manifest.platform !== process.platform || manifest.arch !== process.arch ||
      manifest.toolchain?.node !== process.versions.node) {
    throw nativeFault('NATIVE_ARTIFACT_INVALID');
  }
  let loaded;
  try { loaded = require(fileURLToPath(artifactFile)); }
  catch { throw nativeFault('NATIVE_ARTIFACT_INVALID'); }
  if (loaded.abi !== 1 || loaded.napi !== 8) throw nativeFault('NATIVE_ABI_MISMATCH');
  binding = loaded;
  return binding;
}

export function loadSourceSDK() {
  return sdkSource ??= (async () => {
    const manifest = readReceiptSync();
    if (manifest.sdk?.path !== sourceSDKPath(process.platform, process.arch, manifest)) {
      throw nativeFault('NATIVE_ARTIFACT_INVALID');
    }
    try { return await readFile(sdkFile, 'utf8'); }
    catch { throw nativeFault('NATIVE_ARTIFACT_INVALID'); }
  })().catch(error => { sdkSource = undefined; throw error; });
}

export function loadCatWebValues() {
  if (process.platform !== 'ios') return globalThis;
  if (catWebValues) return catWebValues;
  readReceiptSync();
  try { catWebValues = require(fileURLToPath(catWebValuesFile)); }
  catch { throw nativeFault('NATIVE_ARTIFACT_INVALID'); }
  return catWebValues;
}

/** Internal launch handoff: the caller owns the copy, never the input descriptor. */
export function duplicateBootstrapDescriptor(descriptor) {
  const loaded = loadBinding();
  if (typeof loaded.duplicateBootstrapDescriptor !== 'function') throw nativeFault('NATIVE_ABI_MISMATCH');
  return loaded.duplicateBootstrapDescriptor(descriptor);
}

/** Synchronous Unicode utility without a script owner or guest context. */
export function normalizeNFKCNative(value) {
  const loaded = loadBinding();
  if (typeof loaded.normalizeNFKC !== 'function') throw nativeFault('NATIVE_ABI_MISMATCH');
  return loaded.normalizeNFKC(value);
}

/** App storage is a separate native mechanism, never a QuickJS/guest capability. */
export function loadNativeStorageBinding() {
  const loaded = loadBinding();
  if (typeof loaded.openStorage !== 'function') throw nativeFault('NATIVE_ABI_MISMATCH');
  return loaded;
}

export function acquireNativeStorageLock(path, exclusive, timeout) {
  const loaded = loadBinding();
  if (typeof loaded.acquireStorageLock !== 'function') throw nativeFault('NATIVE_ABI_MISMATCH');
  return loaded.acquireStorageLock(path, exclusive, timeout);
}
