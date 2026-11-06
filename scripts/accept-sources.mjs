import { createHash, randomUUID, randomBytes } from 'node:crypto';
import { readFile, readdir, stat, mkdir, mkdtemp, writeFile, rename, realpath, rm } from 'node:fs/promises';
import { registerHooks } from 'node:module';
import { basename, dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { classifySourceFailure, sourceDiagnosticObservers } from './source-acceptance-diagnostics.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const defaultOutput = join(root, 'tmp/test-results/script-sources/acceptance.json');
const args = process.argv.slice(2), options = {};
for (let i = 0; i < args.length; i++) {
  const arg = args[i];
  if (arg === '--live') { options.live = true; continue; }
  if (arg === '--probe-media') { options.probeMedia = true; continue; }
  if (!['--kind', '--scripts', '--records', '--payload', '--output', '--protocol'].includes(arg) || !args[i + 1]) throw new Error('Invalid acceptance arguments');
  options[arg.slice(2)] = args[++i];
}
options.kind ??= 'lx';
if (!['lx', 'cat', 'source'].includes(options.kind)) throw new Error('Acceptance kind must be lx, cat or source');
if (options.kind === 'source') {
  await import('./accept-reader-sources.mjs');
} else {
  if (options.protocol !== undefined) throw new Error('--protocol requires --kind source');
  if (!options.scripts) throw new Error('Use --kind <lx|cat|source> --scripts <directory> [--live] [--records <json>] [--payload <runtime>]');
  if (options.kind === 'cat' && !options.live) throw new Error('Cat acceptance requires --live');
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

  const moduleFile = options.kind === 'cat' ? 'cat-script.mjs' : 'music-script.mjs';
  let modulePath = join(root, 'apps/runtime/modules', moduleFile);
  let hook;
  if (options.payload) {
    const payload = await realpath(options.payload);
    const manifest = JSON.parse(await readFile(join(payload, 'runtime-manifest.json'), 'utf8'));
    const entry = `app/modules/${moduleFile}`;
    const required = [entry, 'app/bootstrap.mjs', 'app/client.mjs', 'app/contracts.mjs',
      'app/modules/source-candidates.mjs', 'node_modules/@meowfilm/runtime-bridge/src/index.js',
      'node_modules/@meowfilm/script-host/src/index.js'];
    if (manifest.status !== 'complete' || !Array.isArray(manifest.files) ||
        required.some(path => !manifest.files.some(file => file.path === path))) throw new Error('Incomplete runtime payload');
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
  let runtimeModule, records, directory, files;
  try {
    runtimeModule = await import(pathToFileURL(modulePath).href);
    records = options.records ? JSON.parse(await readFile(options.records, 'utf8')) : {};
    const sourcePath = await realpath(options.scripts);
    const singleFile = (await stat(sourcePath)).isFile();
    directory = singleFile ? dirname(sourcePath) : sourcePath;
    files = singleFile ? [basename(sourcePath)].filter(file => file.endsWith('.js'))
      : (await readdir(directory, { withFileTypes: true }))
        .filter(file => file.isFile() && file.name.endsWith('.js')).map(file => file.name).sort();
    if (!files.length) throw new Error('No JS sources to accept');
  } catch (error) {
    hook?.deregister();
    throw error;
  }
  const createMusicScriptModule = runtimeModule.createMusicScriptModule;
  const createCatScriptModule = runtimeModule.createCatScriptModule;

  const runtimeDirectory = dirname(dirname(modulePath));
  const [{ createAppRuntime }, { createRuntimeClient, createCallbackServer }] = await Promise.all([
    import(pathToFileURL(join(runtimeDirectory, 'bootstrap.mjs')).href),
    import(pathToFileURL(join(runtimeDirectory, 'client.mjs')).href),
  ]);
  async function openHttp(module) {
    const sessionId = randomUUID(), requestKey = randomBytes(32).toString('hex'), callbackKey = randomBytes(32).toString('hex');
    const callbacks = createCallbackServer({ sessionId, callbackKey, methods: Object.fromEntries(
      ['get', 'set', 'delete'].map(operation => [`platform.musicScript.storage.${operation}`,
        async () => ({ ok: false, code: 'PERMISSION_DENIED' })])) });
    await callbacks.start();
    let runtime, client;
    try {
      runtime = createAppRuntime({ sessionId, requestKey, callbackKey, callbackUrl: callbacks.baseUrl, modules: [module] });
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl, sessionId, requestKey });
      return { client, async close() {
        const results = await Promise.allSettled([client.close(), runtime.close(), callbacks.close()]);
        if (results.some(result => result.status === 'rejected')) throw Object.assign(new Error(), { code: 'SCRIPT_CLEANUP' });
      } };
    } catch (error) {
      await Promise.allSettled([client?.close(), runtime?.close(), callbacks.close()]);
      throw error;
    }
  }
  const report = { kind: options.kind, mode: options.live ? 'live' : 'offline-init', modulePath, rows: [] };

  function describe(value, action) {
    if (value === null || value === undefined || value === '') return { kind: 'empty' };
    if (action === 'musicUrl' || action === 'pic' || action === 'play') {
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

  function diagnostic({ kind, reason, reference, member, dependency, line, column, operation, code, statusCode }) {
    return { kind, reason, reference, member, dependency, line, column, operation, code, statusCode };
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
      for (const [key, input] of Object.entries(candidate.headers ?? candidate.header ?? {})) {
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

  function catRecord(records, file, siteId, operation) {
    const stem = file.replace(/\.js$/i, '');
    for (const root of [records[file], records[stem], records]) {
      const site = root?.sites?.[siteId] ?? root?.[siteId];
      const value = site?.actions?.[operation] ?? site?.[operation];
      if (value && typeof value === 'object' && !Array.isArray(value)) return structuredClone(value);
    }
    return null;
  }

  function catList(value) {
    if (Array.isArray(value?.list)) return value.list;
    if (Array.isArray(value?.data?.list)) return value.data.list;
    if (Array.isArray(value?.data)) return value.data;
    return [];
  }

  function catItemId(value) {
    const item = catList(value).find(candidate => candidate && typeof candidate === 'object');
    const id = item?.vod_id ?? item?.id ?? item?.url;
    return typeof id === 'string' && id ? id : null;
  }

  function catCategoryId(value) {
    const item = (Array.isArray(value?.class) ? value.class : Array.isArray(value?.data?.class) ? value.data.class : [])
      .find(candidate => candidate && typeof candidate === 'object');
    const id = item?.type_id ?? item?.id;
    return typeof id === 'string' && id ? id : Number.isSafeInteger(id) ? String(id) : null;
  }

  function catPlaySeed(value) {
    const item = catList(value).find(candidate => candidate && typeof candidate === 'object');
    if (!item || typeof item.vod_play_from !== 'string' || typeof item.vod_play_url !== 'string') return null;
    const flags = item.vod_play_from.split('$$$');
    const lines = item.vod_play_url.split('$$$');
    for (let index = 0; index < lines.length; index++) {
      const episode = lines[index].split('#').find(Boolean);
      if (!episode) continue;
      const separator = episode.indexOf('$');
      const id = separator < 0 ? episode : episode.slice(separator + 1);
      if (id) return { flag: flags[index] ?? flags[0] ?? '', id, flags };
    }
    return null;
  }

  function deriveCatPayload(operation, state) {
    if (operation === 'init') return { ext: {} };
    if (operation === 'home') return { filter: true };
    if (operation === 'homeVod' || operation === 'homeVideo') return {};
    if (operation === 'search') return { wd: '海阔天空', quick: false, page: 1 };
    if (operation === 'category') return state.categoryId
      ? { id: state.categoryId, page: 1, filter: true, filters: {} } : null;
    if (operation === 'detail') return state.detailId ? { id: state.detailId } : null;
    if (operation === 'play') return state.play ? { ...state.play } : null;
    if (operation === 'report' || operation === 'watchReport') return state.play ? {
      ...state.play, event: 'progress', position: 1, duration: 60,
      positionSeconds: 1, durationSeconds: 60,
    } : null;
    if (['test', 'support', 'filters', 'thumb'].includes(operation)) return {};
    return null;
  }

  const batches = new Set(), concurrency = 4;
  async function acceptLx(file, index) {
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
      hostFactory: input => createScriptHost({ ...input, ...sourceDiagnosticObservers(diagnostics) }),
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
    let transport;
    try {
      transport = await openHttp(module);
      const rpc = (name, params) => transport.client.call(`music.script.${name}`, params);
      const bytes = await readFile(join(directory, file));
      const uploaded = await transport.client.upload('music', id, 1, bytes, { basename: file });
      if (!uploaded.ok) throw Object.assign(new Error(), { code: uploaded.code });
      row.contentHash = uploaded.value.contentHash;
      const start = Date.now();
      const loaded = await rpc('load', { sourceRevision: 1, installationId: id, revision: 1, expectedRevision: 0,
        version: 'acceptance', config: {}, permissions: { storage: [] }, grant: randomUUID() });
      row.initializationMs = Date.now() - start;
      if (!loaded.ok) throw Object.assign(new Error(), { code: loaded.code });
      row.initializationNetwork = audit.map(({ finished, statusCode, code }) =>
        ({ state: finished ? 'completed' : 'pending', statusCode, code }));
      row.initializationDiagnostics = diagnostics.map(diagnostic);
      startupAuditEnd = audit.length; startupDiagnosticsEnd = diagnostics.length;
      for (const event of audit) if (!event.finished) startupPending.add(event);
      const instance = loaded.value.instance;
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
              entry.state = classifySourceFailure(options.kind, reply, entry.network, entry.diagnostics);
            }
            row.actions.push(entry);
          }
        }
      }
      const unloaded = await rpc('unload', { installationId: id, revision: 1, generation: instance.generation });
      if (!unloaded.ok) row.unloadCode = unloaded.code;
    } catch (error) { row.initialization = 'failed'; row.code = error.code ?? 'HOST_ERROR'; }
    finally {
      try {
        const close = await module.close();
        row.cleanup = close.ok ? 'complete' : close.code;
      } catch (error) { row.cleanup = error.code ?? 'SCRIPT_CLEANUP'; }
      try { await transport?.close(); } catch (error) { row.cleanup = error.code ?? 'SCRIPT_CLEANUP'; }
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

  async function acceptCat(file, index) {
    const id = `accept-${index}`, audit = [], diagnostics = [];
    const row = { file, actions: [] };
    let initializationAuditEnd, initializationDiagnosticsEnd, initializationNetwork;
    let progressAt = Date.now();
    let dataRoot, module, instance, transport;
    const { createCatRuntime } = await import(pathToFileURL(
      options.payload ? join(options.payload, 'node_modules/@meowfilm/script-host/src/index.js')
        : join(root, 'packages/script-host/src/index.js')).href);
    const pendingNetwork = new Map();
    const observeNetwork = input => {
      const key = `${input.scope.installationId}/${input.scope.generation}/${input.networkId}`;
      if (input.phase === 'request') {
        const event = { requestId: input.scope.requestId, siteId: input.scope.siteId,
          mode: 'live', finished: false };
        audit.push(event);
        pendingNetwork.set(key, event);
        return;
      }
      const event = pendingNetwork.get(key);
      if (!event) return;
      if (input.phase === 'response') event.statusCode = input.statusCode;
      else if (input.phase === 'error') event.code = input.code;
      event.finished = true;
      pendingNetwork.delete(key);
    };
    try {
      const temporary = join(root, 'tmp/test-results/script-sources');
      await mkdir(temporary, { recursive: true });
      dataRoot = await mkdtemp(join(temporary, 'cat-runtime-'));
      module = createCatScriptModule({
        dataRoot,
        runtimeFactory: input => createCatRuntime({ ...input, onNetwork: observeNetwork,
          ...sourceDiagnosticObservers(diagnostics) }),
      });
      transport = await openHttp(module);
      const rpc = (name, params = {}) => transport.client.call(`cat.script.${name}`, params);
      const bytes = await readFile(join(directory, file));
      const uploaded = await transport.client.upload('cat', id, 1, bytes);
      if (!uploaded.ok) throw Object.assign(new Error(), { code: uploaded.code });
      row.contentHash = uploaded.value.contentHash;
      const start = Date.now();
      const installed = await rpc('install', {
        installationId: id, sourceRevision: 1, version: 'acceptance', enabled: true,
        config: {},
      });
      initializationAuditEnd = audit.length;
      initializationDiagnosticsEnd = diagnostics.length;
      initializationNetwork = audit.map(({ finished, statusCode, code }) =>
        ({ state: finished ? 'completed' : 'pending', statusCode, code }));
      row.initializationMs = Date.now() - start;
      if (!installed.ok) throw Object.assign(new Error(), { code: installed.code });
      instance = installed.value;
      row.initialization = instance?.state === 'ready' ? 'ready' : 'failed';
      if (row.initialization !== 'ready')
        throw Object.assign(new Error(), { code: instance?.error ?? 'SCRIPT_PROTOCOL_ERROR' });
      row.capabilities = Object.fromEntries((instance.sites ?? []).map(site =>
        [site.key, { actions: site.actions ?? [], methods: site.methods,
          discovery: site.discovery, name: site.name }]));
      const priority = ['init', 'home', 'homeVod', 'homeVideo', 'search', 'category', 'detail', 'play',
        'proxy', 'report', 'watchReport'];
      for (const site of instance.sites ?? []) {
        const actions = [...new Set(site.actions ?? [])].sort((left, right) => {
          const a = priority.indexOf(left), b = priority.indexOf(right);
          return (a < 0 ? priority.length : a) - (b < 0 ? priority.length : b) || left.localeCompare(right);
        });
        if (!actions.length) {
          const observedEmpty = site.discovery === 'registered-routes' &&
            (instance.sites ?? []).some(candidate => candidate.actions?.length);
          row.actions.push({ siteId: site.key, operation: 'discover',
            state: observedEmpty ? 'no_actions' : 'requires_diagnosis',
            reason: observedEmpty ? 'no_registered_actions' : 'actions_undiscovered' });
          continue;
        }
        if (!options.live) continue;
        const state = {};
        for (const operation of actions) {
          const entry = { siteId: site.key, operation };
          const payload = catRecord(records, file, site.key, operation) ?? deriveCatPayload(operation, state);
          if (!payload) {
            entry.state = 'input_required'; row.actions.push(entry); continue;
          }
          const before = audit.length, beforeDiagnostic = diagnostics.length, began = Date.now();
          let reply;
          try {
            reply = await rpc('invoke', {
              installationId: id, generation: instance.generation, siteId: site.key, operation, payload,
            });
          } catch (error) { reply = { ok: false, code: error.code ?? 'HOST_ERROR' }; }
          entry.elapsedMs = Date.now() - began;
          entry.network = audit.slice(before)
            .map(({ requestId, finished, statusCode, code, businessCode, responseReason }) =>
              ({ scope: requestId === null ? 'session' : 'request',
                state: finished ? 'completed' : 'pending',
                statusCode, code, businessCode, responseReason }));
          entry.diagnostics = diagnostics.slice(beforeDiagnostic).filter(event => event.requestId !== null)
            .map(diagnostic);
          if (reply.ok) {
            entry.state = 'returned'; entry.result = describe(reply.value, operation);
            state.categoryId ??= catCategoryId(reply.value);
            state.detailId ??= catItemId(reply.value);
            state.play ??= catPlaySeed(reply.value);
            if (options.probeMedia && operation === 'play' && entry.result.kind === 'url')
              entry.media = await probeMedia(reply.value);
          } else {
            entry.code = reply.code;
            entry.state = classifySourceFailure(options.kind, reply, entry.network, entry.diagnostics);
          }
          row.actions.push(entry);
          if (Date.now() - progressAt >= 30000) {
            progressAt = Date.now();
            console.log(JSON.stringify({ file, phase: 'actions', processed: row.actions.length }));
          }
        }
      }
      const removed = await rpc('uninstall', { installationId: id });
      if (!removed.ok) row.unloadCode = removed.code;
    } catch (error) {
      row.initialization ??= 'failed';
      row.code = error.code ?? 'HOST_ERROR';
      if (row.initialization === 'failed') row.initializationFailure = classifySourceFailure('cat',
        { code: row.code }, audit.slice(0, initializationAuditEnd ?? audit.length)
          .map(event => ({ ...event, scope: 'startup' })),
        diagnostics.slice(0, initializationDiagnosticsEnd ?? diagnostics.length));
    } finally {
      try {
        await module?.close();
        row.cleanup = 'complete';
      } catch (error) { row.cleanup = error.code ?? 'SCRIPT_CLEANUP'; }
      try { await transport?.close(); } catch (error) { row.cleanup = error.code ?? 'SCRIPT_CLEANUP'; }
      if (dataRoot) {
        try { await rm(dataRoot, { recursive: true, force: true }); }
        catch { row.cleanup = 'SCRIPT_CLEANUP'; }
      }
      try {
        row.sourceUnchanged = createHash('sha256').update(await readFile(join(directory, file))).digest('hex') === row.contentHash;
      } catch { row.sourceUnchanged = false; }
      row.initializationNetwork = initializationNetwork ?? audit.slice(0, initializationAuditEnd ?? audit.length)
        .map(({ finished, statusCode, code }) =>
          ({ state: finished ? 'completed' : 'pending', statusCode, code }));
      row.initializationDiagnostics = diagnostics.slice(0, initializationDiagnosticsEnd ?? diagnostics.length)
        .map(diagnostic);
      report.rows.push(row);
      await saveReport();
      console.log(JSON.stringify({ file, initialization: row.initialization,
        returned: row.actions.filter(action => action.state === 'returned').length,
        failed: row.actions.filter(action => ['upstream_failure', 'requires_diagnosis'].includes(action.state)).length,
        code: row.code, cleanup: row.cleanup }));
    }
  }

  const accept = options.kind === 'cat' ? acceptCat : acceptLx;
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
      sitesWithoutActions: report.rows.flatMap(row => row.actions).filter(action => action.state === 'no_actions').length,
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
      report.summary.runtimeLimits || report.summary.requiresDiagnosis ||
      report.summary.scriptRejected) process.exitCode = 1;
  } finally {
    await Promise.allSettled([...batches]);
    await Promise.allSettled([writing]);
    hook?.deregister();
  }
}
