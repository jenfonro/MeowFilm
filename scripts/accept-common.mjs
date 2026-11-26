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
  function saveReport() {
    const bytes = `${JSON.stringify(report, null, 2)}\n`;
    writing = writing.then(async () => {
      await mkdir(dirname(output), { recursive: true });
      await writeFile(`${output}.tmp`, bytes);
      await rename(`${output}.tmp`, output);
    });
    return writing;
  }
  saveReport.flush = () => writing;
  return saveReport;
}

export function classifyJsonResponseBody(rawBody, event, configurationPattern) {
  try {
    const body = JSON.parse(Buffer.from(rawBody ?? []).toString());
    event.bodyKind = 'json';
    const code = body?.code ?? body?.status ?? body?.err_code ?? body?.error_code;
    if (typeof code === 'number' && Number.isSafeInteger(code) && Math.abs(code) <= 999999) {
      event.businessCode = code;
    } else if (typeof code === 'string' && /^-?\d{1,6}$/.test(code)) {
      event.businessCode = Number(code);
    }
    const message = String(body?.message ?? body?.msg ?? body?.error ?? '');
    if (configurationPattern.test(message)) event.responseReason = 'configuration';
    const error = body?.err_code ?? body?.error_code ?? body?.errcode;
    const emptyData = body && typeof body === 'object' &&
      (body.data === null || Array.isArray(body.resource) && body.resource.length === 0 ||
        body.data && typeof body.data === 'object' && Object.hasOwn(body.data, 'url') && !body.data.url);
    if (body?.success === false || body?.status === false ||
        body?.status === 0 && error !== undefined ||
        typeof error === 'number' && error !== 0 || emptyData) {
      event.responseReason ??= 'upstream';
    }
  } catch {
    event.bodyKind = 'non_json';
  }
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
  const [{ createAppRuntime }, { contracts }] = await Promise.all([
    import(pathToFileURL(join(runtimeDirectory, 'bootstrap.mjs')).href),
    import(pathToFileURL(join(runtimeDirectory, 'contracts.mjs')).href),
  ]);
  function createAcceptanceClient(baseUrl) {
    const prefixes = Object.entries(contracts);
    return {
      async call(method, params = {}) {
        const match = prefixes.find(([, spec]) => method.startsWith(spec.prefix));
        if (!match) throw new Error(`Unknown method: ${method}`);
        const [id, spec] = match;
        const response = await fetch(`${baseUrl}/v1/${id}/${method.slice(spec.prefix.length)}`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(params),
        });
        return response.json();
      },
      async upload(module, id, sourceRevision, bytes, { basename } = {}) {
        const query = basename === undefined ? '' : `?basename=${encodeURIComponent(basename)}`;
        const response = await fetch(
          `${baseUrl}/v1/${module}/sources/${encodeURIComponent(id)}/${sourceRevision}${query}`,
          { method: 'PUT', headers: { 'content-type': 'application/octet-stream' }, body: bytes },
        );
        return response.json();
      },
    };
  }
  return async function openHttp(module, { useAssemble = false } = {}) {
    const sessionId = randomUUID();
    let runtime;
    try {
      runtime = createAppRuntime(useAssemble
        ? { sessionId, assemble: () => [module] }
        : { sessionId, modules: [module] });
      await runtime.start();
      return {
        client: createAcceptanceClient(runtime.baseUrl),
        async close() {
          try { await runtime.close(); }
          catch { throw Object.assign(new Error(), { code: 'SCRIPT_CLEANUP' }); }
        },
      };
    } catch (error) {
      await Promise.allSettled([runtime?.close()]);
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
