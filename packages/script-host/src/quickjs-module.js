import { newQuickJSWASMModuleFromVariant, newVariant } from 'quickjs-emscripten-core';
import { readFile } from 'node:fs/promises';
import { requireValue } from './errors.js';

// iOS has no JIT/WASM in the pinned Node Mobile build. This is a platform
// selection, never a fallback from a broken desktop WASM installation.
// Desktop uses the reviewed task-context derivative of the same release; iOS
// uses the unmodified release-sync asm.js build, which has no task-context hook.
const useAsmJS = process.platform === 'ios';
let compiledPromise;
let variantPromise;

export async function prepareQuickJSModule() {
  if (useAsmJS) return undefined;
  compiledPromise ??= readFile(new URL('./quickjs/emscripten-module.wasm', import.meta.url))
    .then(bytes => WebAssembly.compile(bytes));
  return compiledPromise;
}

export async function createQuickJSModule(wasmModule) {
  if (useAsmJS) {
    requireValue(wasmModule === undefined);
    variantPromise ??= import('@jitl/quickjs-asmjs-mjs-release-sync').then(module => module.default);
    return newQuickJSWASMModuleFromVariant(await variantPromise);
  }
  requireValue(wasmModule === undefined || wasmModule instanceof WebAssembly.Module);
  variantPromise ??= import('@jitl/quickjs-wasmfile-release-sync').then(module => ({
    ...module.default,
    importModuleLoader: () => import('./quickjs/emscripten-module.mjs').then(loader => loader.default),
  }));
  return newQuickJSWASMModuleFromVariant(newVariant(await variantPromise, {
    wasmModule: wasmModule ?? await prepareQuickJSModule(),
  }));
}
