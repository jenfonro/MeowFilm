import { createHash, randomUUID } from 'node:crypto';
import { readFile, readdir, stat, mkdir, writeFile, rename, realpath } from 'node:fs/promises';
import { registerHooks } from 'node:module';
import { basename, dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const defaultOutput = join(root, 'tmp/test-results/script-sources/acceptance.json');
const args = process.argv.slice(2), options = {};
for (let i = 0; i < args.length; i++) {
  const arg = args[i];
  if (arg === '--live') { options.live = true; continue; }
  if (arg === '--probe-media') { options.probeMedia = true; continue; }
  if (!['--scripts', '--records', '--payload', '--output'].includes(arg) || !args[i + 1]) throw new Error('Invalid acceptance arguments');
  options[arg.slice(2)] = args[++i];
}
if (!options.scripts) throw new Error('Use --scripts <directory> [--live] [--records <json>] [--payload <runtime>]');
if (options.probeMedia && !options.live) throw new Error('Media probing requires --live');
const output = options.output ?? defaultOutput;
if (!isAbsolute(output)) throw new Error('Acceptance output must be absolute');
const outputDelta = relative(join(root, 'tmp'), output);
if (outputDelta === '..' || outputDelta.startsWith('..' + sep) || isAbsolute(outputDelta)) throw new Error('Acceptance output must stay in repository tmp');
let writing = Promise.resolve();
function saveReport() {
  const bytes = JSON.stringify(report, null, 2) + '\n';
  writing = writing.then(async () => {
    await mkdir(dirname(output), { recursive: true });
    await writeFile(output + '.tmp', bytes);
    await rename(output + '.tmp', output);
  });
  return writing;
}

let modulePath = join(root, 'apps/runtime/modules/music-script.mjs');
let hook;
if (options.payload) {
  const payload = await realpath(options.payload);
  const manifest = JSON.parse(await readFile(join(payload, 'runtime-manifest.json'), 'utf8'));
  const entry = 'app/modules/music-script.mjs';
  if (manifest.status !== 'complete' || !manifest.files.some(file => file.path === entry)) throw new Error('Incomplete runtime payload');
  modulePath = join(payload, entry);
  hook = registerHooks({
    resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url.startsWith('file:')) {
        const delta = relative(payload, fileURLToPath(result.url));
        if (delta === '..' || delta.startsWith('..' + sep) || isAbsolute(delta)) throw new Error('Runtime dependency escaped payload');
      }
      return result;
    },
  });
}
const { createMusicScriptModule } = await import(pathToFileURL(modulePath).href);
const records = options.records ? JSON.parse(await readFile(options.records, 'utf8')) : {};
const sourcePath = await realpath(options.scripts);
const singleFile = (await stat(sourcePath)).isFile();
const directory = singleFile ? dirname(sourcePath) : sourcePath;
const files = singleFile ? [basename(sourcePath)].filter(file => file.endsWith('.js'))
  : (await readdir(directory, { withFileTypes: true }))
    .filter(file => file.isFile() && file.name.endsWith('.js')).map(file => file.name).sort();
if (!files.length) throw new Error('No JS sources to accept');
const report = { mode: options.live ? 'live' : 'offline-init', modulePath, rows: [] };

function describe(value, action) {
  if (value === null || value === undefined || value === '') return { kind: 'empty' };
  if (action === 'musicUrl' || action === 'pic') {
    const url = typeof value === 'string' ? value : value?.url;
    if (typeof url === 'string' && /^https?:\/\//.test(url)) return { kind: 'url' };
  }
  if (action === 'lyric') {
    const lyric = typeof value === 'string' ? value : value?.lyric;
    if (typeof lyric === 'string') return { kind: 'lyric', characters: lyric.length };
  }
  if (Array.isArray(value)) return { kind: 'array', count: value.length };
  if (typeof value === 'object') {
    const list = value.list ?? value.data?.list ?? (Array.isArray(value.data) ? value.data : undefined);
    return { kind: 'object', ...(Array.isArray(list) ? { count: list.length } : {}) };
  }
  return { kind: typeof value };
}

function firstRecording(value) {
  const list = Array.isArray(value) ? value : value?.list ?? value?.data?.list ?? value?.data;
  if (!Array.isArray(list) || !list[0] || typeof list[0] !== 'object') return null;
  return list[0].source_data ?? list[0].musicInfo ?? list[0];
}

function diagnostic({ kind, reason, reference, member, line, column, operation, code }) {
  return { kind, reason, reference, member, line, column, operation, code };
}

function failureState(reply, network, diagnostics) {
  const hostGlobals = ['Buffer', 'URL', 'URLSearchParams', 'TextEncoder', 'TextDecoder',
    'fetch', 'lx', 'setTimeout', 'setInterval'];
  const guest = diagnostics.findLast(event => event.reason !== 'host');
  if (diagnostics.some(event => event.reason === 'host' &&
    ['UNSUPPORTED_OPTION', 'UNSUPPORTED_CRYPTO', 'CAPABILITY_UNAVAILABLE'].includes(event.code)) ||
    network.some(event => ['UNSUPPORTED_OPTION', 'UNSUPPORTED_CRYPTO', 'CAPABILITY_UNAVAILABLE'].includes(event.code)) ||
    guest?.reason === 'reference' && hostGlobals.includes(guest.reference)) return 'host_failure';
  if (reply.code === 'LIMIT') return 'runtime_limit';
  if (network.some(event => event.responseReason === 'configuration') ||
    guest?.reason === 'configuration') return 'configuration_required';
  if (guest?.reason === 'unsupported' || guest?.reason === 'reference') return 'script_rejected';
  if (network.some(event => event.code || event.statusCode >= 400 ||
    typeof event.businessCode === 'number' && ![0, 1, 200].includes(event.businessCode) ||
    event.responseReason === 'upstream') || guest?.reason === 'upstream') return 'upstream_failure';
  if (guest?.reason === 'input') return 'input_required';
  if (diagnostics.some(event => event.reason === 'host' && event.code === 'PERMISSION_DENIED'))
    return 'script_rejected';
  return 'requires_diagnosis';
}

async function probeMedia(value) {
  const candidate = typeof value === 'string' ? { url: value } : value;
  let url;
  try { url = new URL(candidate.url); } catch { return { state: 'invalid_url' }; }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return { state: 'invalid_url' };
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  let reader;
  try {
    const headers = {};
    for (const [key, input] of Object.entries(candidate.headers ?? {})) {
      if (typeof input === 'string' && !/host|content-length|transfer-encoding/i.test(key)) headers[key] = input;
    }
    const reply = await fetch(url, { headers: { ...headers, Range: 'bytes=0-1023' }, signal: controller.signal });
    if (!reply.ok) return { state: 'upstream_failure', statusCode: reply.status };
    reader = reply.body?.getReader();
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
    return { state: media ? 'media_detected' : 'unverified_content', statusCode: reply.status, bytes: size };
  } catch { return { state: 'upstream_failure' }; }
  finally {
    try { await reader?.cancel(); } catch { /* Request abort below also releases transport. */ }
    controller.abort();
    clearTimeout(timer);
  }
}

const batches = new Set(), concurrency = 2;
async function accept(file, index) {
  const id = `accept-${index}`, audit = [], diagnostics = [];
  const row = { file, actions: [] };
  let startupAuditEnd = 0, startupDiagnosticsEnd = 0;
  const startupPending = new Set();
  const { createNodeNetwork, createScriptHost, ScriptHostError } = await import(pathToFileURL(
    options.payload ? join(options.payload, 'node_modules/@meowfilm/script-host/src/index.js')
      : join(root, 'packages/script-host/src/index.js')).href);
  const network = createNodeNetwork();
  const module = createMusicScriptModule({
    call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
    hostFactory: input => createScriptHost({ ...input, onDiagnostic: event => diagnostics.push(event) }),
    network: async input => {
      const event = { requestId: input.scope.requestId, siteId: input.scope.siteId,
        mode: options.live ? 'live' : 'blocked', finished: false };
      audit.push(event);
      try {
        if (!options.live) throw new ScriptHostError('NETWORK_ERROR');
        const reply = await network(input);
        event.statusCode = reply.statusCode;
        try {
          const body = JSON.parse(Buffer.from(reply.body ?? []).toString());
          event.bodyKind = 'json';
          const code = body?.code ?? body?.status ?? body?.err_code ?? body?.error_code;
          if (typeof code === 'number' && Number.isSafeInteger(code) && Math.abs(code) <= 999999)
            event.businessCode = code;
          else if (typeof code === 'string' && /^-?\d{1,6}$/.test(code)) event.businessCode = Number(code);
          const message = String(body?.message ?? body?.msg ?? body?.error ?? '');
          if (/密钥.*(?:无效|缺失|错误|过期)|(?:invalid|missing|expired).*api.?key|api.?key.*(?:required|missing|invalid)|token.*(?:expired|invalid)/i.test(message))
            event.responseReason = 'configuration';
          const error = body?.err_code ?? body?.error_code ?? body?.errcode;
          const emptyData = body && typeof body === 'object' &&
            (body.data === null || Array.isArray(body.resource) && body.resource.length === 0 ||
              body.data && typeof body.data === 'object' &&
                Object.hasOwn(body.data, 'url') && !body.data.url);
          if (body?.success === false || body?.status === false ||
            body?.status === 0 && error !== undefined ||
            typeof error === 'number' && error !== 0 || emptyData)
            event.responseReason ??= 'upstream';
        } catch { event.bodyKind = 'non_json'; }
        return reply;
      } catch (error) { event.code = error.code ?? 'NETWORK_ERROR'; throw error; }
      finally { event.finished = true; }
    },
  });
  const rpc = (name, params) => module.methods[`music.script.${name}`](params);
  try {
    const bytes = await readFile(join(directory, file));
    row.contentHash = createHash('sha256').update(bytes).digest('hex');
    const begun = await rpc('stage.begin', { basename: file, length: bytes.length, sha256: row.contentHash });
    if (!begun.ok) throw Object.assign(new Error(), { code: begun.code });
    for (let offset = 0; offset < bytes.length; offset += begun.chunkBytes) {
      const chunk = await rpc('stage.chunk', { token: begun.token, offset,
        data: bytes.subarray(offset, offset + begun.chunkBytes).toString('base64') });
      if (!chunk.ok) throw Object.assign(new Error(), { code: chunk.code });
    }
    const start = Date.now();
    const loaded = await rpc('load', { token: begun.token, installationId: id, revision: 1, expectedRevision: 0,
      version: 'acceptance', config: {}, permissions: { storage: [] }, grant: randomUUID() });
    row.initializationMs = Date.now() - start;
    if (!loaded.ok) throw Object.assign(new Error(), { code: loaded.code });
    row.initializationNetwork = audit.map(({ finished, statusCode, code }) =>
      ({ state: finished ? 'completed' : 'pending', statusCode, code }));
    row.initializationDiagnostics = diagnostics.map(diagnostic);
    startupAuditEnd = audit.length; startupDiagnosticsEnd = diagnostics.length;
    for (const event of audit) if (!event.finished) startupPending.add(event);
    const instance = loaded.instance;
    row.initialization = 'ready';
    row.capabilities = Object.fromEntries(Object.entries(instance.capabilities).map(([site, cap]) =>
      [site, { actions: cap.actions, qualitys: cap.qualitys }]));
    if (options.live) {
      for (const [siteId, cap] of Object.entries(instance.capabilities)) {
        let recording = Array.isArray(records[siteId]) ? records[siteId][0] : records[siteId];
        const actions = [...cap.actions].sort((a, b) =>
          Number(!['search', 'musicSearch'].includes(a)) - Number(!['search', 'musicSearch'].includes(b)));
        for (const operation of actions) {
          const entry = { siteId, operation };
          const search = ['search', 'musicSearch'].includes(operation);
          if (!search && !recording) {
            entry.state = 'input_required'; row.actions.push(entry); continue;
          }
          const before = audit.length, beforeDiagnostic = diagnostics.length, began = Date.now();
          const payload = search ? { keyword: '海阔天空', page: 1, limit: 10, pagesize: 10 }
            : { musicInfo: recording, ...(operation === 'musicUrl' ? { quality:
              cap.qualitys.includes('128k') ? '128k' : cap.qualitys[0] ?? null } : {}) };
          const reply = await rpc('invoke', { installationId: id, revision: 1,
            generation: instance.generation, siteId, operation, payload });
          entry.elapsedMs = Date.now() - began;
          entry.network = audit.slice(before)
            .map(({ requestId, statusCode, code, bodyKind, businessCode, responseReason }) =>
              ({ scope: requestId === null ? 'session' : 'request', statusCode, code, bodyKind, businessCode, responseReason }));
          entry.diagnostics = diagnostics.slice(beforeDiagnostic).filter(event => event.requestId !== null)
            .map(diagnostic);
          if (reply.ok) {
            entry.state = 'returned';
            entry.result = describe(reply.value, operation);
            if (options.probeMedia && operation === 'musicUrl' && entry.result.kind === 'url')
              entry.media = await probeMedia(reply.value);
            if (search) recording = firstRecording(reply.value) ?? recording;
          } else {
            entry.code = reply.code;
            entry.state = failureState(reply, entry.network, entry.diagnostics);
          }
          row.actions.push(entry);
        }
      }
    }
    const unloaded = await rpc('unload', { installationId: id, revision: 1, generation: instance.generation });
    if (!unloaded.ok) row.unloadCode = unloaded.code;
  } catch (error) { row.initialization = 'failed'; row.code = error.code ?? 'HOST_ERROR'; }
  finally {
    const close = await module.close();
    row.cleanup = close.ok ? 'complete' : close.code;
    if (row.initialization === 'ready') {
      row.backgroundNetwork = audit.filter((event, index) => event.requestId === null &&
        (index >= startupAuditEnd || startupPending.has(event)))
        .map(({ finished, statusCode, code }) =>
          ({ state: finished ? 'completed' : 'pending', statusCode, code }));
      row.backgroundDiagnostics = diagnostics.slice(startupDiagnosticsEnd).filter(event => event.requestId === null)
        .map(diagnostic);
    }
    try {
      row.sourceUnchanged = createHash('sha256').update(await readFile(join(directory, file))).digest('hex') === row.contentHash;
    } catch { row.sourceUnchanged = false; }
    row.initializationNetwork ??= audit.map(({ finished, statusCode, code }) =>
      ({ state: finished ? 'completed' : 'pending', statusCode, code }));
    row.initializationDiagnostics ??= diagnostics.map(diagnostic);
    report.rows.push(row);
    await saveReport();
    console.log(JSON.stringify({ file, initialization: row.initialization,
      returned: row.actions.filter(action => action.state === 'returned').length,
      failed: row.actions.filter(action => ['upstream_failure', 'requires_diagnosis'].includes(action.state)).length,
      code: row.code, cleanup: row.cleanup }));
  }
}
try {
  for (const [index, file] of files.entries()) {
    const task = accept(file, index);
    batches.add(task);
    task.finally(() => batches.delete(task)).catch(() => {});
    if (batches.size >= concurrency) await Promise.race(batches);
  }
  await Promise.all(batches);
  report.rows.sort((a, b) => a.file.localeCompare(b.file));
  report.summary = {
    scripts: report.rows.length,
    initialized: report.rows.filter(row => row.initialization === 'ready').length,
    declaredActions: report.rows.reduce((sum, row) => sum +
      Object.values(row.capabilities ?? {}).reduce((count, cap) => count + cap.actions.length, 0), 0),
    invokedActions: report.rows.flatMap(row => row.actions).filter(action => action.elapsedMs !== undefined).length,
    returned: report.rows.flatMap(row => row.actions).filter(action => action.state === 'returned').length,
    upstreamFailures: report.rows.flatMap(row => row.actions).filter(action => action.state === 'upstream_failure').length,
    requiresDiagnosis: report.rows.flatMap(row => row.actions).filter(action => action.state === 'requires_diagnosis').length,
    hostFailures: report.rows.flatMap(row => row.actions).filter(action => action.state === 'host_failure').length,
    runtimeLimits: report.rows.flatMap(row => row.actions).filter(action => action.state === 'runtime_limit').length,
    inputRequired: report.rows.flatMap(row => row.actions).filter(action => action.state === 'input_required').length,
    configurationRequired: report.rows.flatMap(row => row.actions).filter(action => action.state === 'configuration_required').length,
    scriptRejected: report.rows.flatMap(row => row.actions).filter(action => action.state === 'script_rejected').length,
    mediaDetected: report.rows.flatMap(row => row.actions).filter(action => action.media?.state === 'media_detected').length,
    cleanupFailures: report.rows.filter(row => row.cleanup !== 'complete').length,
    unloadFailures: report.rows.filter(row => row.unloadCode).length,
    changedSources: report.rows.filter(row => !row.sourceUnchanged).length,
  };
  await saveReport();
  console.log(JSON.stringify(report.summary));
  if (report.summary.initialized !== report.summary.scripts || report.summary.cleanupFailures ||
    report.summary.unloadFailures || report.summary.changedSources || report.summary.hostFailures ||
    report.summary.runtimeLimits || report.summary.requiresDiagnosis) process.exitCode = 1;
} finally { hook?.deregister(); }
