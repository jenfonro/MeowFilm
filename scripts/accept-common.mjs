import { randomUUID } from 'node:crypto';
import { mkdir, readFile, realpath, rename, writeFile } from 'node:fs/promises';
import { registerHooks } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export function inside(base, target) {
  const delta = relative(base, target);
  return delta !== '..' && !delta.startsWith(`..${sep}`) && !isAbsolute(delta);
}

export function createReportWriter(output, report) {
  let writing = Promise.resolve();
  return function saveReport() {
    const bytes = `${JSON.stringify(report, null, 2)}\n`;
    writing = writing.then(async () => {
      await mkdir(dirname(output), { recursive: true });
      await writeFile(`${output}.tmp`, bytes);
      await rename(`${output}.tmp`, output);
    });
    return writing;
  };
}

export async function preparePayloadRuntime(payloadPath, required, {
  incompleteError = 'Incomplete runtime payload',
  escapeError = 'Runtime dependency escaped payload',
} = {}) {
  const payloadRoot = await realpath(payloadPath);
  let manifest;
  try { manifest = JSON.parse(await readFile(join(payloadRoot, 'runtime-manifest.json'), 'utf8')); }
  catch { throw new Error(incompleteError); }
  if (manifest?.status !== 'complete' || !Array.isArray(manifest.files) ||
      required.some(path => !manifest.files.some(file => file.path === path))) {
    throw new Error(incompleteError);
  }
  const hook = registerHooks({
    resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url.startsWith('file:')) {
        const path = fileURLToPath(result.url);
        if (!inside(payloadRoot, path) && resolve(path) !== resolve(payloadRoot)) {
          throw new Error(escapeError);
        }
      }
      return result;
    },
  });
  return { payloadRoot, hook };
}

export async function loadAcceptanceHttp(runtimeDirectory, hasPayload) {
  const { configureStandaloneStorage } = hasPayload
    ? await import(pathToFileURL(join(runtimeDirectory, '../node_modules/@meowfilm/storage/src/engine/database.js')).href)
    : await import('@meowfilm/storage');
  const { createDesktopStorageDriver } = hasPayload
    ? await import(pathToFileURL(join(runtimeDirectory, '../node_modules/@meowfilm/script-host/src/native-storage.js')).href)
    : await import('@meowfilm/script-host/storage');
  configureStandaloneStorage(createDesktopStorageDriver);
  const [{ createAppRuntime }, { createRuntimeClient }] = await Promise.all([
    import(pathToFileURL(join(runtimeDirectory, 'bootstrap.mjs')).href),
    import(pathToFileURL(join(runtimeDirectory, 'client.mjs')).href),
  ]);
  return async function openHttp(module, { useAssemble = false } = {}) {
    const sessionId = randomUUID();
    let runtime, client;
    try {
      runtime = createAppRuntime(useAssemble
        ? { sessionId, assemble: () => [module] }
        : { sessionId, modules: [module] });
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl });
      return {
        client,
        async close() {
          const results = await Promise.allSettled([client.close(), runtime.close()]);
          if (results.some(result => result.status === 'rejected')) {
            throw Object.assign(new Error(), { code: 'SCRIPT_CLEANUP' });
          }
        },
      };
    } catch (error) {
      await Promise.allSettled([client?.close(), runtime?.close()]);
      throw error;
    }
  };
}

export async function probeMedia(value) {
  const candidate = typeof value === 'string' ? { url: value } : value;
  let url;
  try { url = new URL(candidate?.url); } catch { return { state: 'invalid_url' }; }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
    return { state: 'invalid_url' };
  }
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  let reader;
  try {
    const headers = {};
    for (const [key, input] of Object.entries(candidate?.headers ?? candidate?.header ?? {})) {
      if (typeof input === 'string' && !/^(?:host|content-length|transfer-encoding)$/i.test(key)) {
        headers[key] = input;
      }
    }
    const response = await fetch(url, {
      headers: { ...headers, Range: 'bytes=0-1023' },
      signal: controller.signal,
    });
    if (!response.ok) return { state: 'upstream_failure', statusCode: response.status };
    reader = response.body?.getReader();
    const chunks = [];
    let size = 0;
    while (reader && size < 1024) {
      const part = await reader.read();
      if (part.done) break;
      chunks.push(Buffer.from(part.value.subarray(0, 1024 - size)));
      size += chunks.at(-1).length;
    }
    const bytes = Buffer.concat(chunks), magic = bytes.subarray(0, 16).toString('latin1');
    const media = /^ID3|^fLaC|^OggS|^RIFF|^#EXTM3U/.test(magic) ||
      bytes.length > 1 && bytes[0] === 255 && (bytes[1] & 224) === 224 ||
      bytes.subarray(4, 8).toString() === 'ftyp';
    return { state: media ? 'media_detected' : 'unverified_content', statusCode: response.status, bytes: size };
  } catch { return { state: 'upstream_failure' }; }
  finally {
    try { await reader?.cancel(); } catch { /* Request abort below also releases transport. */ }
    controller.abort();
    clearTimeout(timer);
  }
}
