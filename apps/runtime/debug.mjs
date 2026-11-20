import { AsyncLocalStorage } from 'node:async_hooks';
import { createHash, randomBytes } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { Session } from 'node:inspector/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import v8 from 'node:v8';
import { BridgeError } from '@meowfilm/runtime-bridge';

export const DEBUG_CATEGORIES = Object.freeze([
  'lifecycle',
  'ui-nav',
  'ui-action',
  'runtime-bridge',
  'runtime-node',
  'music-platform',
  'music-account',
  'music-catalog',
  'music-library',
  'music-playback',
  'film-catalog',
  'film-playback',
  'novel',
  'search',
  'storage',
  'diagnostics',
]);

export const DEBUG_CATEGORY_SET = Object.freeze(new Set(DEBUG_CATEGORIES));

const VALID_LEVELS = new Set(['trace', 'debug', 'info', 'warn', 'error', 'fatal']);
const NODE_DEBUG_SYMBOL = Symbol.for('meowfilm.nodeDebug.v1');
const DEFAULT_INLINE_LIMIT = 2048;
const DEFAULT_EVENT_BYTES_LIMIT = 16384;
const HEAD_CHARS = 512;
const TAIL_CHARS = 128;

function invalid() {
  throw new BridgeError('INVALID_REQUEST');
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function validateSessionId(value) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 128 ||
      /[/\\\u0000-\u001f\u007f]/.test(value) || value === '.' || value === '..' || value.includes('..')) {
    invalid();
  }
  return value;
}

function validateSessionDir(value) {
  if (typeof value !== 'string' || !isAbsolute(value) ||
      /[\u0000-\u001f\u007f]/.test(value) ||
      (process.platform === 'win32' && (/^[/\\]{2}/.test(value) || !/^[A-Za-z]:[\\/]/.test(value) || value.slice(2).includes(':')))) {
    invalid();
  }
  return resolve(value);
}

function validateLimit(value, fallback) {
  if (value === undefined) return fallback;
  if (!Number.isSafeInteger(value) || value < 0) invalid();
  return value;
}

function validateModules(value, fallback) {
  if (value === undefined) return fallback;
  if (!Array.isArray(value) || value.length === 0) invalid();
  const normalized = [];
  for (const item of value) {
    if (typeof item !== 'string') invalid();
    const trimmed = item.trim();
    if (!trimmed || (trimmed !== 'all' && !DEBUG_CATEGORY_SET.has(trimmed))) invalid();
    if (!normalized.includes(trimmed)) normalized.push(trimmed);
  }
  return normalized.includes('all') ? ['all'] : normalized;
}

/** Validate optional `debug` handshake block from Windows pipe / iOS socketpair / direct runtime config. */
export function parseDebugHandshake(raw) {
  if (raw === undefined) return null;
  if (!isPlainObject(raw) || raw.enabled !== true) invalid();
  const sessionId = validateSessionId(raw.sessionId);
  const sessionDir = validateSessionDir(raw.sessionDir);
  const inlineLimit = validateLimit(raw.inlineLimit, DEFAULT_INLINE_LIMIT);
  const eventBytesLimit = validateLimit(raw.eventBytesLimit, DEFAULT_EVENT_BYTES_LIMIT);
  const modules = validateModules(raw.modules, ['all']);
  return Object.freeze({
    enabled: true,
    sessionId,
    sessionDir,
    inlineLimit,
    eventBytesLimit,
    modules: Object.freeze(modules),
  });
}

export function serializeDebugError(error, depth = 0, seen = new WeakSet()) {
  if (error === null || error === undefined) return null;
  if (typeof error !== 'object') {
    return { code: null, type: typeof error, message: String(error), stack: null, inner: null };
  }
  if (seen.has(error)) {
    return { code: null, type: 'CircularError', message: '[Circular]', stack: null, inner: null };
  }
  seen.add(error);
  const type = typeof error.name === 'string' && error.name ? error.name
    : error.constructor?.name || 'Error';
  const code = typeof error.code === 'string' || typeof error.code === 'number' ? String(error.code) : null;
  const message = typeof error.message === 'string' ? error.message : String(error);
  const stack = typeof error.stack === 'string' ? error.stack : null;
  const innerSource = error.cause ?? error.inner ?? (Array.isArray(error.errors) && error.errors.length ? error.errors[0] : undefined);
  const inner = depth < 6 && innerSource !== undefined && innerSource !== null
    ? serializeDebugError(innerSource, depth + 1, seen)
    : null;
  return { code, type, message, stack, inner };
}

function formatDumpTimestamp(date = new Date()) {
  const iso = date.toISOString();
  return iso.replace(/[-:]/g, '').replace('.', '');
}

function sanitizeExtension(ext) {
  if (typeof ext !== 'string') return 'bin';
  const cleaned = ext.toLowerCase().replace(/[^a-z0-9]/g, '');
  return cleaned || 'bin';
}

function summarizeData(value) {
  if (Array.isArray(value)) {
    return { keys: [], counts: { items: value.length } };
  }
  if (isPlainObject(value)) {
    const keys = Object.keys(value);
    const counts = {};
    for (const key of keys) {
      const entry = value[key];
      if (Array.isArray(entry)) counts[key] = entry.length;
      else if (isPlainObject(entry) && !Object.hasOwn(entry, '$blob')) {
        const subKeys = Object.keys(entry);
        if (subKeys.length > 0) counts[key] = subKeys.length;
      }
    }
    return { keys: keys.slice(0, 32), counts };
  }
  return { keys: [], counts: {} };
}

function abortableDelay(ms, signal) {
  if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
  return new Promise((resolveDelay, rejectDelay) => {
    const onAbort = () => {
      clearTimeout(timer);
      rejectDelay(new BridgeError('CANCELLED'));
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolveDelay();
    }, ms);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

function aggregateCounts(moduleDiagnostics) {
  const counts = {
    quickjsRuntimes: 0,
    catWorkers: 0,
    neteaseWorkers: 0,
    mediaProxyStreams: 0,
    eventStreams: 0,
    imageStreams: 0,
    activeFilmSearches: 0,
    activeFilmSessions: 0,
    musicPlatformActive: 0,
    musicPlatformCacheEntries: 0,
    storagePending: 0,
  };
  if (!isPlainObject(moduleDiagnostics)) return counts;
  for (const [id, diag] of Object.entries(moduleDiagnostics)) {
    if (!isPlainObject(diag)) continue;
    if (id === 'music' && isPlainObject(diag.host)) {
      counts.quickjsRuntimes += Number(diag.host.liveSessions) || 0;
    }
    if (id === 'source' && isPlainObject(diag.host)) {
      counts.quickjsRuntimes += Number(diag.host.liveSessions) || 0;
    }
    if (id === 'cat') {
      counts.catWorkers += Number(diag.liveWorkers ?? diag.readyWorkers) || 0;
    }
    if (id === 'netease' && diag.workerAlive) {
      counts.neteaseWorkers += 1;
    }
    if (id === 'tmdb' || id === 'douban') {
      counts.imageStreams += Number(diag.activeImageStreams) || 0;
    }
    if (id === 'film') {
      counts.mediaProxyStreams += Number(diag.media?.activeStreams) || 0;
      counts.activeFilmSearches += Number(diag.search?.activeSessions) || 0;
      counts.activeFilmSessions += Number(diag.playback?.activeSessions) || 0;
      counts.eventStreams += (Number(diag.search?.activeEventStreams) || 0) +
        (Number(diag.playback?.activeEventStreams) || 0);
    }
    if (id === 'music-platform') {
      counts.musicPlatformActive += Number(diag.activeRequests) || 0;
      counts.musicPlatformCacheEntries += Number(diag.cacheEntries) || 0;
    }
    if (id === 'storage') {
      counts.storagePending += Number(diag.pendingCount) || 0;
    }
  }
  return counts;
}

export function isNodeDebug(value) {
  return Boolean(value && typeof value === 'object' && value[NODE_DEBUG_SYMBOL] === true);
}

/**
 * Shared Node Debug session core.
 * Zero side effects when not created; all directories and files are created lazily on first write.
 */
export function createNodeDebug(rawOptions) {
  if (rawOptions === undefined || rawOptions === null) return null;
  if (isNodeDebug(rawOptions)) return rawOptions;
  const parsed = parseDebugHandshake(rawOptions);
  const { sessionId, sessionDir } = parsed;
  const logsDir = join(sessionDir, 'logs');
  const blobsDir = join(sessionDir, 'blobs');
  const dumpsDir = join(sessionDir, 'dumps');

  let seq = 0;
  let tmpSeq = 0;
  const als = new AsyncLocalStorage();
  const blobCache = new Set();
  let logsDirReady = false;
  let blobsDirReady = false;
  let dumpsDirReady = false;
  let writeErrorReported = false;
  let profileTail = Promise.resolve();

  const config = {
    modulesList: [...parsed.modules],
    allModules: parsed.modules.includes('all'),
    modulesSet: new Set(parsed.modules),
    inlineLimit: parsed.inlineLimit,
    eventBytesLimit: parsed.eventBytesLimit,
  };

  function ensureLogsDir() {
    if (logsDirReady) return;
    mkdirSync(logsDir, { recursive: true });
    logsDirReady = true;
  }

  function ensureBlobsDir() {
    if (blobsDirReady) return;
    mkdirSync(blobsDir, { recursive: true });
    blobsDirReady = true;
  }

  function ensureDumpsDir() {
    if (dumpsDirReady) return;
    mkdirSync(dumpsDir, { recursive: true });
    dumpsDirReady = true;
  }

  function writeBlobAtomic(relPath, buffer) {
    if (blobCache.has(relPath)) return;
    ensureBlobsDir();
    const absPath = join(sessionDir, relPath);
    if (existsSync(absPath)) {
      blobCache.add(relPath);
      return;
    }
    const tmpPath = join(blobsDir, `.tmp-${process.pid}-${++tmpSeq}-${randomBytes(4).toString('hex')}`);
    try {
      writeFileSync(tmpPath, buffer);
      try {
        renameSync(tmpPath, absPath);
      } catch (renameError) {
        if (!existsSync(absPath)) throw renameError;
        try { unlinkSync(tmpPath); } catch { /* ignore */ }
      }
      blobCache.add(relPath);
    } catch (error) {
      try { unlinkSync(tmpPath); } catch { /* ignore */ }
      throw error;
    }
  }

  function storeBinaryBlob(bytes, { ext = 'bin', contentType = 'application/octet-stream' } = {}) {
    const buf = Buffer.isBuffer(bytes)
      ? bytes
      : ArrayBuffer.isView(bytes)
        ? Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength)
        : bytes instanceof ArrayBuffer
          ? Buffer.from(bytes)
          : Buffer.from(String(bytes ?? ''), 'utf8');
    const sha256 = createHash('sha256').update(buf).digest('hex');
    const safeExt = sanitizeExtension(ext);
    const rel = `blobs/${sha256}.${safeExt}`;
    writeBlobAtomic(rel, buf);
    return {
      $blob: rel,
      sha256,
      bytes: buf.byteLength,
      contentType: typeof contentType === 'string' && contentType ? contentType : 'application/octet-stream',
    };
  }

  function foldString(value) {
    const limit = config.inlineLimit;
    if (limit <= 0 || value.length <= limit) return value;
    const buf = Buffer.from(value, 'utf8');
    const sha256 = createHash('sha256').update(buf).digest('hex');
    const rel = `blobs/${sha256}.txt`;
    writeBlobAtomic(rel, buf);
    return {
      $blob: rel,
      sha256,
      length: value.length,
      bytes: buf.byteLength,
      head: value.slice(0, HEAD_CHARS),
      tail: value.slice(-TAIL_CHARS),
    };
  }

  function sanitizeAndFold(value, seen = new WeakSet(), depth = 0) {
    if (value === null || value === undefined) return value;
    const t = typeof value;
    if (t === 'string') return foldString(value);
    if (t === 'number') return Number.isFinite(value) ? value : String(value);
    if (t === 'boolean') return value;
    if (t === 'bigint') return value.toString();
    if (t === 'symbol') return value.toString();
    if (t === 'function') return `[Function ${value.name || 'anonymous'}]`;
    if (depth > 24) return '[MaxDepth]';
    if (value instanceof Date) return Number.isNaN(value.getTime()) ? 'Invalid Date' : value.toISOString();
    if (value instanceof URL) return foldString(value.href);
    if (value instanceof URLSearchParams) return foldString(value.toString());
    if (typeof Headers === 'function' && value instanceof Headers) {
      const out = {};
      for (const [k, v] of value.entries()) out[k] = foldString(v);
      return out;
    }
    if (value instanceof RegExp) return value.toString();
    if (value instanceof Error) return sanitizeAndFold(serializeDebugError(value), seen, depth + 1);
    if (Buffer.isBuffer(value) || ArrayBuffer.isView(value) || value instanceof ArrayBuffer) {
      return storeBinaryBlob(value, { ext: 'bin', contentType: 'application/octet-stream' });
    }
    if (seen.has(value)) return '[Circular]';
    seen.add(value);
    try {
      if (value instanceof Map) {
        const out = {};
        for (const [k, v] of value.entries()) out[String(k)] = sanitizeAndFold(v, seen, depth + 1);
        return out;
      }
      if (value instanceof Set) {
        return [...value].map(item => sanitizeAndFold(item, seen, depth + 1));
      }
      if (Array.isArray(value)) {
        return value.map(item => sanitizeAndFold(item, seen, depth + 1));
      }
      if (typeof value.toJSON === 'function') {
        try {
          const jsonValue = value.toJSON();
          if (jsonValue !== value) return sanitizeAndFold(jsonValue, seen, depth + 1);
        } catch { /* Fall back to enumerable own properties. */ }
      }
      if (Object.hasOwn(value, '$blob') && typeof value.$blob === 'string' && typeof value.sha256 === 'string') {
        return value;
      }
      const out = {};
      for (const [k, v] of Object.entries(value)) {
        if (v === undefined) continue;
        out[k] = sanitizeAndFold(v, seen, depth + 1);
      }
      return out;
    } finally {
      seen.delete(value);
    }
  }

  function prepareData(rawData) {
    if (rawData === undefined) return undefined;
    const folded = sanitizeAndFold(rawData);
    if (folded === undefined) return undefined;
    const limit = config.eventBytesLimit;
    if (limit <= 0) return folded;
    const json = JSON.stringify(folded);
    if (json === undefined) return undefined;
    const utf8Bytes = Buffer.byteLength(json, 'utf8');
    if (utf8Bytes <= limit) return folded;
    const buf = Buffer.from(json, 'utf8');
    const sha256 = createHash('sha256').update(buf).digest('hex');
    const rel = `blobs/${sha256}.json`;
    writeBlobAtomic(rel, buf);
    return {
      $blob: rel,
      sha256,
      bytes: utf8Bytes,
      summary: summarizeData(folded),
    };
  }

  function isEnabled(cat) {
    if (!DEBUG_CATEGORY_SET.has(cat)) return false;
    return config.allModules || config.modulesSet.has(cat);
  }

  function emit({
    level = 'info',
    cat,
    sub = 'runtime',
    event,
    opId,
    traceId,
    durationMs,
    data,
    error,
  } = {}) {
    if (!cat || !event || !isEnabled(cat)) return;
    try {
      const normalizedLevel = VALID_LEVELS.has(level) ? level : 'info';
      const ctx = als.getStore();
      const resolvedOpId = opId ?? ctx?.opId;
      const resolvedTraceId = traceId ?? ctx?.traceId;
      const record = {
        seq: ++seq,
        ts: new Date().toISOString(),
        // performance.now() is measured from Node's process timeOrigin (contract: since process start).
        monoMs: Math.round(performance.now() * 1000) / 1000,
        proc: 'node',
        level: normalizedLevel,
        cat,
        sub: typeof sub === 'string' && sub ? sub : cat,
        event: String(event),
      };
      if (typeof resolvedOpId === 'string' && resolvedOpId) record.opId = resolvedOpId;
      if (typeof resolvedTraceId === 'string' && resolvedTraceId) record.traceId = resolvedTraceId;
      if (typeof durationMs === 'number' && Number.isFinite(durationMs)) {
        record.durationMs = Math.round(durationMs * 1000) / 1000;
      }
      if (data !== undefined) {
        const prepared = prepareData(data);
        if (prepared !== undefined) record.data = prepared;
      }
      record.error = error !== undefined && error !== null ? serializeDebugError(error) : null;
      ensureLogsDir();
      appendFileSync(join(logsDir, `${cat}.jsonl`), `${JSON.stringify(record)}\n`, 'utf8');
    } catch (writeError) {
      if (!writeErrorReported) {
        writeErrorReported = true;
        try {
          process.stderr.write(`[meow-debug] log write failed: ${writeError?.message ?? writeError}\n`);
        } catch { /* Never break business logic on stderr failure. */ }
      }
    }
  }

  function getConfig() {
    return {
      enabled: true,
      sessionId,
      sessionDir,
      modules: [...config.modulesList],
      inlineLimit: config.inlineLimit,
      eventBytesLimit: config.eventBytesLimit,
    };
  }

  function validateConfigPatch(patch) {
    if (!isPlainObject(patch)) invalid();
    const nextModules = patch.modules !== undefined
      ? validateModules(patch.modules, config.modulesList)
      : config.modulesList;
    const nextInlineLimit = patch.inlineLimit !== undefined
      ? validateLimit(patch.inlineLimit, config.inlineLimit)
      : config.inlineLimit;
    const nextEventBytesLimit = patch.eventBytesLimit !== undefined
      ? validateLimit(patch.eventBytesLimit, config.eventBytesLimit)
      : config.eventBytesLimit;
    return { nextModules, nextInlineLimit, nextEventBytesLimit };
  }

  function updateConfig(patch) {
    const { nextModules, nextInlineLimit, nextEventBytesLimit } = validateConfigPatch(patch);
    config.modulesList = [...nextModules];
    config.allModules = nextModules.includes('all');
    config.modulesSet = new Set(nextModules);
    config.inlineLimit = nextInlineLimit;
    config.eventBytesLimit = nextEventBytesLimit;
    const updated = getConfig();
    emit({ level: 'info', cat: 'diagnostics', sub: 'node.config', event: 'config.updated', data: updated });
    return updated;
  }

  function getDiagnostics({ activeRequests = 0, moduleDiagnostics = {} } = {}) {
    return {
      pid: process.pid,
      uptimeSec: Math.round(process.uptime() * 1000) / 1000,
      nodeVersion: process.version,
      platform: process.platform,
      arch: process.arch,
      memoryUsage: process.memoryUsage(),
      heapStatistics: v8.getHeapStatistics(),
      heapSpaceStatistics: v8.getHeapSpaceStatistics(),
      activeHttpRequests: activeRequests,
      modules: moduleDiagnostics,
      counts: aggregateCounts(moduleDiagnostics),
      config: getConfig(),
    };
  }

  function runGc() {
    const before = {
      memoryUsage: process.memoryUsage(),
      heapStatistics: v8.getHeapStatistics(),
    };
    let gcTriggered = false;
    if (typeof globalThis.gc === 'function') {
      globalThis.gc();
      gcTriggered = true;
    }
    const after = {
      memoryUsage: process.memoryUsage(),
      heapStatistics: v8.getHeapStatistics(),
    };
    const result = {
      gcTriggered,
      before,
      after,
      freedBytes: {
        rss: before.memoryUsage.rss - after.memoryUsage.rss,
        heapTotal: before.memoryUsage.heapTotal - after.memoryUsage.heapTotal,
        heapUsed: before.memoryUsage.heapUsed - after.memoryUsage.heapUsed,
        external: before.memoryUsage.external - after.memoryUsage.external,
      },
    };
    emit({ level: 'info', cat: 'diagnostics', sub: 'node.gc', event: 'gc.completed', data: result });
    return result;
  }

  function writeHeapSnapshot() {
    ensureDumpsDir();
    const fileName = `${formatDumpTimestamp()}-node.heapsnapshot`;
    const targetPath = join(dumpsDir, fileName);
    const actualPath = v8.writeHeapSnapshot(targetPath);
    const bytes = statSync(actualPath).size;
    const result = { path: actualPath, relativePath: `dumps/${fileName}`, bytes };
    emit({ level: 'info', cat: 'diagnostics', sub: 'node.heapsnapshot', event: 'heapsnapshot.written', data: result });
    return result;
  }

  function captureCpuProfile({ durationMs = 5000, signal } = {}) {
    if (!Number.isSafeInteger(durationMs) || durationMs < 500 || durationMs > 60000) invalid();
    if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
    const run = async () => {
      if (signal?.aborted) throw new BridgeError('CANCELLED');
      ensureDumpsDir();
      const session = new Session();
      session.connect();
      try {
        await session.post('Profiler.enable');
        await session.post('Profiler.start');
        await abortableDelay(durationMs, signal);
        const { profile } = await session.post('Profiler.stop');
        await session.post('Profiler.disable').catch(() => {});
        const fileName = `${formatDumpTimestamp()}-node.cpuprofile`;
        const targetPath = join(dumpsDir, fileName);
        const tmpPath = join(dumpsDir, `.tmp-${process.pid}-${++tmpSeq}-${randomBytes(4).toString('hex')}`);
        const payload = Buffer.from(JSON.stringify(profile), 'utf8');
        try {
          writeFileSync(tmpPath, payload);
          renameSync(tmpPath, targetPath);
        } catch (error) {
          try { unlinkSync(tmpPath); } catch { /* ignore */ }
          throw error;
        }
        const result = {
          path: targetPath,
          relativePath: `dumps/${fileName}`,
          bytes: payload.byteLength,
          durationMs,
        };
        emit({ level: 'info', cat: 'diagnostics', sub: 'node.cpu_profile', event: 'cpu_profile.written', data: result });
        return result;
      } finally {
        try { session.disconnect(); } catch { /* ignore */ }
      }
    };
    const next = profileTail.then(run, run);
    profileTail = next.then(() => {}, () => {});
    return next;
  }

  function runWithContext(context, fn) {
    if (!context || (!context.opId && !context.traceId)) return fn();
    const parent = als.getStore();
    return als.run({
      opId: context.opId ?? parent?.opId,
      traceId: context.traceId ?? parent?.traceId,
    }, fn);
  }

  function scope(cat, defaultSub) {
    const subFor = sub => sub || defaultSub || cat;
    return Object.freeze({
      enabled: true,
      isEnabled: (targetCat = cat) => isEnabled(targetCat),
      emit: (fields = {}) => emit({ ...fields, cat: fields.cat ?? cat, sub: subFor(fields.sub) }),
      trace: (event, data, extra = {}) => emit({ ...extra, level: 'trace', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data }),
      debug: (event, data, extra = {}) => emit({ ...extra, level: 'debug', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data }),
      info: (event, data, extra = {}) => emit({ ...extra, level: 'info', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data }),
      warn: (event, data, extra = {}) => emit({ ...extra, level: 'warn', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data }),
      error: (event, error, data, extra = {}) => emit({ ...extra, level: 'error', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data, error }),
      fatal: (event, error, data, extra = {}) => emit({ ...extra, level: 'fatal', cat: extra.cat ?? cat, sub: subFor(extra.sub), event, data, error }),
      storeBinaryBlob,
    });
  }

  return Object.freeze({
    [NODE_DEBUG_SYMBOL]: true,
    enabled: true,
    sessionId,
    sessionDir,
    isEnabled,
    emit,
    scope,
    storeBinaryBlob,
    runWithContext,
    currentContext: () => als.getStore() ?? null,
    getConfig,
    validateConfigPatch,
    updateConfig,
    getDiagnostics,
    runGc,
    writeHeapSnapshot,
    captureCpuProfile,
  });
}
