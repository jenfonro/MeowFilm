import { createHash, randomUUID } from 'node:crypto';
import {
  createScriptHost, createNodeNetwork, PrivateDocuments, ScriptHostError, SCRIPT_LIMITS, SourceTransfers,
} from '@meowfilm/script-host';
import { BridgeError, encodeJSONValue, decodeJSONValue } from '@meowfilm/runtime-bridge';

export const musicScriptContract = 'music.script.v3';
/** Platforms an LX source can resolve for the native music catalog. */
export const musicPlatforms = Object.freeze(['wy', 'tx', 'kw', 'kg', 'mg']);
const chunkBytes = 64 * 1024;
const stageTTL = 300000;
const downloadTimeout = 30000;
const checkTimeout = 15000;
const codes = new Set(['INVALID_REQUEST', 'CLOSED', 'CANCELLED', 'LIMIT', 'SCRIPT_INTEGRITY',
  'SCRIPT_ENCODING', 'STAGE_NOT_FOUND', 'STALE_SOURCE', 'SOURCE_NOT_FOUND', 'SOURCE_DISABLED',
  'CAPABILITY_UNAVAILABLE', 'PERMISSION_DENIED', 'TIMEOUT', 'SCRIPT_CLEANUP', 'SCRIPT_FAILURE',
  'SCRIPT_PROTOCOL_ERROR', 'NETWORK_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'STORAGE_ERROR',
  'CORRUPT_STORAGE', 'ROLLBACK_UNAVAILABLE', 'INVALID_SOURCE_URL', 'SOURCE_DOWNLOAD_FAILED',
  'SOURCE_REDIRECT', 'MEDIA_UNAVAILABLE', 'INVALID_MEDIA_CANDIDATE', 'EXPIRED_MEDIA_CANDIDATE',
  'MEDIA_HEADERS_UNSUPPORTED', 'TARGET_CHANGED', 'RECORDING_MISMATCH']);
// Public catalog identifiers of one well-known recording per platform. A pass
// means "this sample resolved", never "every song or quality is playable".
const checkSamples = Object.freeze({
  wy: Object.freeze({ name: '海阔天空', singer: 'Beyond', songmid: '347230' }),
  tx: Object.freeze({ name: '晴天', singer: '周杰伦', songmid: '0039MnYb0qxYhV' }),
  kw: Object.freeze({ name: '晴天', singer: '周杰伦', songmid: '838607' }),
  kg: Object.freeze({ name: '晴天', singer: '周杰伦', hash: '30ece05c17d4e9ec8ba0ef9f96e7a07c' }),
  mg: Object.freeze({ name: '晴天', singer: '周杰伦', songmid: '69984639', copyrightId: '69984639' }),
});
const checkQualities = ['128k', '192k', '320k', 'flac', 'flac24bit', 'master'];
const restrictedMedia = ['freeTrialInfo', 'trial', 'preview', 'drm', 'encrypted', 'requiresLicense', 'licenseRequired'];
const forbiddenHeaders = new Set(['host', 'range', 'accept-encoding', 'connection', 'keep-alive', 'te',
  'trailer', 'transfer-encoding', 'upgrade', 'expect']);
const hashPattern = /^[a-f0-9]{64}$/;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const sitePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const codePattern = /^[A-Z][A-Z_]{0,63}$/;
const headerPattern = /^\s*(\/\*[\s\S]*?\*\/|(?:\/\/[^\n]*\n?)+)/;
class Failure extends Error {
  constructor(code) { super(codes.has(code) ? code : 'SCRIPT_FAILURE'); this.code = this.message; }
}
const requireValue = (condition, code = 'INVALID_REQUEST') => { if (!condition) throw new Failure(code); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const shape = (value, keys) => object(value) && Object.keys(value).length === keys.length &&
  keys.every(key => Object.hasOwn(value, key));
const digest = value => createHash('sha256').update(value).digest('hex');
const scalars = value => [...value].length;
const control = /[\u0000-\u001f\u007f]/;
function fields(value, allowed, required = allowed) {
  requireValue(object(value) && Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)));
}
function integer(value, min = 0) {
  requireValue(Number.isSafeInteger(value) && value >= min);
}
function text(value, max = 128) {
  requireValue(typeof value === 'string' && value.trim().length > 0 && value.length <= max && !control.test(value));
}
function installation(value) {
  requireValue(typeof value === 'string' && uuidPattern.test(value));
}
function snapshot(value) {
  const bytes = encodeJSONValue(value);
  try { return decodeJSONValue(bytes); }
  finally { bytes.fill(0); }
}
const plainText = (value, max, empty = true) => typeof value === 'string' &&
  (empty || value.length > 0) && scalars(value) <= max && !control.test(value);
// Object ordering is not a content change; array ordering remains meaningful.
const canonical = value => JSON.stringify(value, (_key, item) => object(item)
  ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);
function decode(bytes) {
  // The byte round-trip rejects malformed UTF-8 without requiring ICU.
  const source = bytes.toString('utf8');
  requireValue(source.trim().length > 0 && Buffer.from(source).equals(bytes), 'SCRIPT_ENCODING');
  return source;
}
function clean(value, max) {
  const trimmed = value.trim();
  if (!trimmed || control.test(trimmed)) return '';
  const characters = [...trimmed];
  return characters.length > max ? characters.slice(0, max).join('') : trimmed;
}
function headerField(header, name, max) {
  const match = new RegExp(`(?:^|\\n)\\s*(?:\\/\\/|\\/\\*|\\*)?\\s*@${name}\\s+([^\\r\\n]*)`).exec(header);
  return match ? clean(match[1].replaceAll('*/', ''), max) : '';
}
/** Leading LX comment metadata. It describes the file; it never authorizes anything. */
function describe(source, basename) {
  const header = headerPattern.exec(source.slice(0, 8192))?.[0] ?? '';
  const fallback = clean(basename.replace(/\.js$/i, ''), 255);
  return {
    name: headerField(header, 'name', 255) || fallback || 'LX 音源',
    version: headerField(header, 'version', 128) || '1',
    author: headerField(header, 'author', 1024),
    description: headerField(header, 'description', 1024),
    homepage: headerField(header, 'homepage', 1024),
  };
}
function sourceURL(value) {
  requireValue(typeof value === 'string', 'INVALID_SOURCE_URL');
  const input = value.trim();
  requireValue(input.length > 0 && input.length <= 2048 && !/[\u0000-\u0020\u007f]/.test(input),
    'INVALID_SOURCE_URL');
  let url;
  try { url = new URL(input); } catch { throw new Failure('INVALID_SOURCE_URL'); }
  requireValue(url.protocol === 'https:' && url.hostname.length > 0 && !url.username && !url.password &&
    !input.includes('#') && /\.js$/i.test(url.pathname), 'INVALID_SOURCE_URL');
  return url.href;
}
function sourceBasename(url) {
  try {
    const name = decodeURIComponent(new URL(url).pathname.split('/').pop());
    return name.length > 0 && name.length <= 255 && !/[\\/:*?"<>|\u0000-\u001f]/.test(name) ? name : 'source.js';
  } catch { return 'source.js'; }
}
const validCode = value => value === null || typeof value === 'string' && codePattern.test(value);
function validVersion(value) {
  return shape(value, ['contentHash', 'bytes', 'version', 'author', 'description', 'homepage']) &&
    typeof value.contentHash === 'string' && hashPattern.test(value.contentHash) &&
    Number.isSafeInteger(value.bytes) && value.bytes > 0 && value.bytes <= SCRIPT_LIMITS.maxSourceBytes &&
    plainText(value.version, 128, false) && plainText(value.author, 1024) &&
    plainText(value.description, 1024) && plainText(value.homepage, 1024);
}
function validOrigin(value) {
  if (shape(value, ['kind'])) return value.kind === 'file';
  if (!shape(value, ['kind', 'url']) || value.kind !== 'network') return false;
  try { return sourceURL(value.url) === value.url; } catch { return false; }
}
const validList = (value, max) => Array.isArray(value) && value.length <= 32 &&
  value.every(item => plainText(item, max, false)) && new Set(value).size === value.length;
function validDeclared(value) {
  return shape(value, ['contentHash', 'sites']) && hashPattern.test(value.contentHash) && object(value.sites) &&
    Object.entries(value.sites).every(([site, capability]) => musicPlatforms.includes(site) &&
      shape(capability, ['actions', 'qualitys']) && validList(capability.actions, 128) &&
      validList(capability.qualitys, 64));
}
function validCheck(value) {
  return shape(value, ['contentHash', 'checkedAt', 'failure', 'sites']) && hashPattern.test(value.contentHash) &&
    Number.isSafeInteger(value.checkedAt) && value.checkedAt >= 0 && validCode(value.failure) &&
    object(value.sites) && Object.entries(value.sites).every(([site, result]) => musicPlatforms.includes(site) &&
      shape(result, ['state', 'quality', 'sample', 'code']) && ['ok', 'failed', 'unsupported'].includes(result.state) &&
      (result.quality === null || plainText(result.quality, 64, false)) &&
      (result.sample === null || plainText(result.sample, 128, false)) && validCode(result.code));
}
function validCatalog(value) {
  if (!shape(value, ['schema', 'revision', 'installations']) || value.schema !== 1 ||
      !Number.isSafeInteger(value.revision) || value.revision < 0 || !Array.isArray(value.installations)) return false;
  const ids = new Set();
  for (const item of value.installations) {
    if (!shape(item, ['id', 'name', 'origin', 'enabled', 'current', 'previous', 'declared', 'check']) ||
        typeof item.id !== 'string' || !uuidPattern.test(item.id) || ids.has(item.id) ||
        !plainText(item.name, 255, false) || item.name.trim().length === 0 || typeof item.enabled !== 'boolean' ||
        !validOrigin(item.origin) || !validVersion(item.current) ||
        item.previous !== null && !validVersion(item.previous) ||
        item.declared !== null && !validDeclared(item.declared) ||
        item.check !== null && !validCheck(item.check)) return false;
    ids.add(item.id);
  }
  return true;
}
function validData(value) {
  if (!shape(value, ['schema', 'sites']) || value.schema !== 1 || !object(value.sites)) return false;
  const sites = Object.entries(value.sites);
  return sites.length <= 32 && sites.every(([site, values]) => sitePattern.test(site) && Array.isArray(values) &&
    values.length <= 64 && values.every(entry => shape(entry, ['key', 'value']) &&
      typeof entry.key === 'string' && entry.key.length > 0 && entry.key.length <= 256) &&
    new Set(values.map(entry => entry.key)).size === values.length);
}
function encodedLength(value) {
  const bytes = encodeJSONValue(value);
  try { return bytes.length; } finally { bytes.fill(0); }
}
function errorCode(error) {
  if (error instanceof Failure) return error.code;
  if (error instanceof BridgeError) return 'INVALID_REQUEST';
  if (error instanceof ScriptHostError) return ({
    INVALID_ARGUMENT: 'INVALID_REQUEST', HOST_CLOSED: 'CLOSED', CANCELLED: 'CANCELLED',
    TIMEOUT: 'TIMEOUT', STALE_SESSION: 'STALE_SOURCE', DISABLED: 'SOURCE_DISABLED',
    NOT_FOUND: 'SOURCE_NOT_FOUND', ALREADY_EXISTS: 'STALE_SOURCE',
    CAPABILITY_UNAVAILABLE: 'CAPABILITY_UNAVAILABLE', PERMISSION_DENIED: 'PERMISSION_DENIED',
    RESOURCE_LIMIT: 'LIMIT', NETWORK_ERROR: 'NETWORK_ERROR', STORAGE_ERROR: 'STORAGE_ERROR',
    STORAGE_CONFLICT: 'STORAGE_ERROR', CORRUPT_STORAGE: 'CORRUPT_STORAGE',
    UPSTREAM_HTTP_ERROR: 'UPSTREAM_HTTP', HTTP_ERROR: 'UPSTREAM_HTTP', INVALID_JSON: 'UPSTREAM_FORMAT',
    SCRIPT_PROTOCOL_ERROR: 'SCRIPT_PROTOCOL_ERROR', ROLLBACK_UNAVAILABLE: 'ROLLBACK_UNAVAILABLE',
  })[error.code] ?? 'SCRIPT_FAILURE';
  return error?.name === 'AbortError' ? 'CANCELLED' : 'SCRIPT_FAILURE';
}
function storageError(code) {
  return new ScriptHostError(({
    PERMISSION_DENIED: 'PERMISSION_DENIED', STALE_SOURCE: 'STALE_SESSION',
    CANCELLED: 'CANCELLED', CLOSED: 'HOST_CLOSED', TIMEOUT: 'TIMEOUT',
    LIMIT: 'RESOURCE_LIMIT', INVALID_REQUEST: 'INVALID_ARGUMENT', CORRUPT_STORAGE: 'CORRUPT_STORAGE',
  })[code] ?? 'STORAGE_ERROR');
}
const truthy = value => value !== null && value !== undefined &&
  (typeof value === 'object' || Boolean(value));
function mediaHeaders(value) {
  if (value === undefined || value === null) return {};
  requireValue(object(value) && Object.keys(value).length <= 32, 'INVALID_MEDIA_CANDIDATE');
  const seen = new Set(), headers = {};
  let bytes = 0;
  for (const [name, item] of Object.entries(value)) {
    const lower = name.toLowerCase();
    requireValue(/^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$/.test(name) && !seen.has(lower) &&
      typeof item === 'string' && item.length <= 8192 && /^[\u0020-\u007e]*$/.test(item) &&
      (bytes += name.length + item.length) <= 16384, 'INVALID_MEDIA_CANDIDATE');
    requireValue(!forbiddenHeaders.has(lower) &&
      !['if-', 'content-', 'proxy-', 'sec-'].some(prefix => lower.startsWith(prefix)), 'MEDIA_HEADERS_UNSUPPORTED');
    seen.add(lower);
    headers[name] = item;
  }
  return headers;
}
/** The one musicUrl result contract shared by playback and checks. */
function mediaCandidate(raw, clock, expected, duration = 0) {
  requireValue(raw !== null && raw !== undefined && !(typeof raw === 'string' && raw.trim().length === 0),
    'MEDIA_UNAVAILABLE');
  const candidate = typeof raw === 'string' ? { url: raw } : raw;
  requireValue(object(candidate), 'INVALID_MEDIA_CANDIDATE');
  const url = candidate.url;
  requireValue(typeof url === 'string' && url.length > 0 && url.length <= 8192 &&
    !/[\u0000-\u0020\u007f]/.test(url), 'INVALID_MEDIA_CANDIDATE');
  let parsed;
  try { parsed = new URL(url); } catch { throw new Failure('INVALID_MEDIA_CANDIDATE'); }
  requireValue(['http:', 'https:'].includes(parsed.protocol) && parsed.hostname.length > 0 &&
    !parsed.username && !parsed.password, 'INVALID_MEDIA_CANDIDATE');
  requireValue(!restrictedMedia.some(flag => truthy(candidate[flag])), 'INVALID_MEDIA_CANDIDATE');
  let expiresAt = null;
  if (candidate.expiresAt !== undefined && candidate.expiresAt !== null) {
    requireValue(typeof candidate.expiresAt === 'number' && Number.isFinite(candidate.expiresAt),
      'INVALID_MEDIA_CANDIDATE');
    requireValue(candidate.expiresAt > clock(), 'EXPIRED_MEDIA_CANDIDATE');
    expiresAt = Math.floor(candidate.expiresAt);
  }
  for (const [field, value] of Object.entries(expected))
    requireValue(candidate[field] === undefined || candidate[field] === value, 'TARGET_CHANGED');
  if (candidate.duration !== undefined && candidate.duration !== null) {
    requireValue(typeof candidate.duration === 'number' && Number.isFinite(candidate.duration) && candidate.duration >= 0,
      'INVALID_MEDIA_CANDIDATE');
    if (duration > 0 && candidate.duration > 0)
      requireValue(Math.abs(duration - candidate.duration) <= 10,
        'RECORDING_MISMATCH');
  }
  return { url, headers: mediaHeaders(candidate.headers), expiresAt };
}
/** LX musicInfo with the conventional mm:ss interval derived from a known duration. */
function lxInfo(musicInfo) {
  const info = structuredClone(musicInfo);
  if (typeof info.duration === 'number' && Number.isFinite(info.duration) && info.duration >= 0) {
    const pad = value => String(Math.floor(value)).padStart(2, '0');
    info.interval = `${pad(info.duration / 60)}:${pad(info.duration % 60)}`;
  }
  return info;
}
function abortable(promise, signal) {
  if (signal.aborted) return Promise.reject(new ScriptHostError('CANCELLED'));
  return new Promise((resolve, reject) => {
    const abort = () => reject(new ScriptHostError('CANCELLED'));
    signal.addEventListener('abort', abort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', abort)).catch(() => {});
  });
}

/**
 * Shared LX music source management and execution. This module is the only
 * writer of `<dataRoot>/music`: installation facts, immutable source files,
 * declared/checked platform facts and each installation's private storage.
 * Native hosts supply the data root, file selection and presentation.
 * ScriptHost executes guest code in isolated QuickJS heaps.
 */
export function createMusicScriptModule({ dataRoot, hostFactory = createScriptHost,
  network = createNodeNetwork(),
  download = createNodeNetwork({ maxResponseBytes: SCRIPT_LIMITS.maxSourceBytes, maxRedirects: 0 }),
  clock = Date.now, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  requireValue(typeof dataRoot === 'string' && typeof hostFactory === 'function' &&
    typeof network === 'function' && typeof download === 'function' && typeof clock === 'function' &&
    typeof setTimer === 'function' && typeof clearTimer === 'function');
  let documents;
  try { documents = new PrivateDocuments(dataRoot, 'music'); }
  catch { throw new Failure('INVALID_REQUEST'); }
  const stages = new Map(), locks = new Map(), failures = new Map(), temporaryUse = new Map();
  const adapterTasks = new Set();
  const transfers = new SourceTransfers({ clock, setTimer, clearTimer });
  const lifetime = new AbortController();
  let closed = false, closing, tail = Promise.resolve(), cached = null;
  let stageTimer, stageFailure;
  function track(promise) {
    adapterTasks.add(promise);
    promise.finally(() => adapterTasks.delete(promise)).catch(() => {});
    return promise;
  }
  // Track actual adapter completion, not merely a cancelled Promise.race.
  const host = hostFactory({
    network: input => track(Promise.resolve().then(() => network(input))),
    storage: Object.fromEntries(['get', 'set', 'delete'].map(operation =>
      [operation, input => storage(operation, input)])),
  });
  function guard(signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  function serialize(body) {
    const result = tail.then(body);
    tail = result.catch(() => {});
    return result;
  }
  /** One lifecycle owner per installation: load, replacement, unload and check cleanup. */
  function locked(id, body) {
    const result = (locks.get(id) ?? Promise.resolve()).then(body);
    const settled = result.catch(() => {});
    locks.set(id, settled);
    settled.then(() => { if (locks.get(id) === settled) locks.delete(id); });
    return result;
  }
  const live = id => host.list().find(record => record.installationId === id);
  function clearStageTimer() {
    const timer = stageTimer;
    stageTimer = undefined;
    if (timer === undefined) return;
    try { clearTimer(timer); }
    catch (error) { stageFailure ??= error; }
  }
  function scheduleStages() {
    clearStageTimer();
    if (closed || !stages.size) return;
    let expiresAt = Infinity;
    for (const stage of stages.values()) expiresAt = Math.min(expiresAt, stage.expiresAt);
    const timer = setTimer(expireStages, Math.max(1, expiresAt - clock()));
    stageTimer = timer;
    try { timer?.unref?.(); } catch { /* An active timer still owns expiry. */ }
  }
  function expireStages() {
    if (closed) return;
    try { sweep(); }
    catch (error) {
      stageFailure ??= error;
      for (const token of [...stages.keys()]) release(token, false);
    }
  }
  function release(token, reschedule = true) {
    const stage = stages.get(token);
    if (stage) { for (const bytes of stage.chunks) bytes.fill(0); stages.delete(token); }
    if (reschedule) scheduleStages();
  }
  function sweep() {
    for (const [token, stage] of stages)
      if (stage.expiresAt <= clock()) release(token, false);
    scheduleStages();
  }
  function staged(token) {
    text(token); sweep();
    const stage = stages.get(token);
    requireValue(stage, 'STAGE_NOT_FOUND');
    return stage;
  }
  /** A complete, hash-verified staged file. The caller zeroes the returned bytes. */
  function stagedBytes(token) {
    const stage = staged(token);
    requireValue(stage.offset === stage.length, 'SCRIPT_INTEGRITY');
    const bytes = Buffer.concat(stage.chunks);
    if (digest(bytes) !== stage.hash) { bytes.fill(0); throw new Failure('SCRIPT_INTEGRITY'); }
    return { bytes, basename: stage.basename };
  }

  function catalog() {
    if (cached) return cached;
    const bytes = documents.read('catalog.json');
    if (!bytes) return cached = { schema: 1, revision: 0, installations: [] };
    let value;
    try { value = JSON.parse(decode(bytes)); }
    catch { throw new Failure('CORRUPT_STORAGE'); }
    finally { bytes.fill(0); }
    requireValue(validCatalog(value), 'CORRUPT_STORAGE');
    return cached = value;
  }
  function find(id) {
    const item = catalog().installations.find(entry => entry.id === id);
    requireValue(item, 'SOURCE_NOT_FOUND');
    return item;
  }
  /** Applies change to the current catalog and replaces the file in the same turn.
   * change returns false for a no-op. A failed write never publishes a guess. */
  function commit(change) {
    const next = structuredClone(catalog());
    if (change(next) === false) return;
    next.revision += 1;
    requireValue(validCatalog(next));
    const bytes = Buffer.from(JSON.stringify(next));
    try { documents.write('catalog.json', bytes); }
    catch (error) { cached = null; throw error; }
    finally { bytes.fill(0); }
    cached = next;
  }
  function edit(id, change) {
    commit(next => {
      const target = next.installations.find(entry => entry.id === id);
      requireValue(target, 'SOURCE_NOT_FOUND');
      return change(target);
    });
  }
  /** Source files no installation references any more. An orphan grants nothing. */
  function collect() {
    try {
      const referenced = new Set();
      for (const item of catalog().installations)
        for (const version of [item.current, item.previous])
          if (version) referenced.add(`sources/${version.contentHash}.js`);
      for (const name of documents.list('sources'))
        if (!referenced.has(name)) documents.remove(name);
    } catch { /* The next completed management command retries collection. */ }
  }
  function prepare(bytes, basename) {
    requireValue(bytes.length > 0, 'SCRIPT_ENCODING');
    requireValue(bytes.length <= SCRIPT_LIMITS.maxSourceBytes, 'LIMIT');
    const header = describe(decode(bytes), basename);
    const contentHash = digest(bytes), name = `sources/${contentHash}.js`;
    const existing = documents.read(name);
    try { if (!existing?.equals(bytes)) documents.write(name, bytes); }
    finally { existing?.fill(0); }
    return { name: header.name, version: { contentHash, bytes: bytes.length, version: header.version,
      author: header.author, description: header.description, homepage: header.homepage } };
  }
  function readSource(version) {
    const bytes = documents.read(`sources/${version.contentHash}.js`);
    requireValue(bytes, 'CORRUPT_STORAGE');
    try {
      requireValue(bytes.length === version.bytes && digest(bytes) === version.contentHash, 'CORRUPT_STORAGE');
      return decode(bytes);
    } finally { bytes.fill(0); }
  }
  async function fetchSource(url, signal) {
    const timeout = new AbortController();
    const timer = setTimer(() => timeout.abort(), downloadTimeout);
    try { timer?.unref?.(); } catch { /* The active timer still bounds the download. */ }
    let response;
    try {
      response = await download({ url, options: { followRedirect: false },
        signal: AbortSignal.any([signal, timeout.signal]), validateURL: sourceURL });
    } catch (error) {
      if (signal.aborted) throw new Failure('CANCELLED');
      if (timeout.signal.aborted) throw new Failure('TIMEOUT');
      if (error instanceof ScriptHostError && error.code === 'RESOURCE_LIMIT') throw new Failure('LIMIT');
      throw new Failure('SOURCE_DOWNLOAD_FAILED');
    } finally { clearTimer(timer); }
    requireValue(!(response?.statusCode >= 300 && response.statusCode < 400), 'SOURCE_REDIRECT');
    requireValue(response?.statusCode === 200 && Buffer.isBuffer(response.body) && response.body.length > 0,
      'SOURCE_DOWNLOAD_FAILED');
    return response.body;
  }

  const dataName = id => `data/${id}.json`;
  function readData(id) {
    const bytes = documents.read(dataName(id));
    if (!bytes) return { schema: 1, sites: {} };
    let value;
    try { value = JSON.parse(decode(bytes)); }
    catch { throw new Failure('CORRUPT_STORAGE'); }
    finally { bytes.fill(0); }
    requireValue(validData(value), 'CORRUPT_STORAGE');
    return value;
  }
  /** Request-bound guest storage of the current generation's declared sites only. */
  function storage(operation, input) {
    try {
      requireValue(object(input) && typeof input.installationId === 'string' &&
        uuidPattern.test(input.installationId) && Number.isSafeInteger(input.generation) && input.generation > 0 &&
        typeof input.siteId === 'string' && sitePattern.test(input.siteId) &&
        typeof input.key === 'string' && input.key.length > 0 && input.key.length <= 256, 'PERMISSION_DENIED');
      requireValue(!closed && input.signal instanceof AbortSignal && !input.signal.aborted &&
        typeof input.isCurrent === 'function' && input.isCurrent() === true, 'STALE_SOURCE');
      const record = live(input.installationId);
      requireValue(record?.state === 'ready' && record.generation === input.generation, 'STALE_SOURCE');
      requireValue(Object.hasOwn(record.capabilities, input.siteId), 'PERMISSION_DENIED');
      const data = readData(input.installationId);
      const values = data.sites[input.siteId] ?? [];
      if (operation === 'get') return values.find(entry => entry.key === input.key)?.value ?? null;
      const next = values.filter(entry => entry.key !== input.key);
      if (operation === 'set') {
        let size;
        try { size = encodedLength(input.value); } catch { throw new Failure('INVALID_REQUEST'); }
        requireValue(size <= 16384, 'LIMIT');
        next.push({ key: input.key, value: input.value });
      }
      const sites = { ...data.sites };
      if (next.length) sites[input.siteId] = next; else delete sites[input.siteId];
      requireValue(next.length <= 64 && encodedLength(next) <= 65536 && Object.keys(sites).length <= 32, 'LIMIT');
      const bytes = Buffer.from(JSON.stringify({ schema: 1, sites }));
      try {
        requireValue(bytes.length <= 512 * 1024, 'LIMIT');
        documents.write(dataName(input.installationId), bytes);
      } finally { bytes.fill(0); }
      return null;
    } catch (error) {
      if (error instanceof ScriptHostError) throw error;
      throw storageError(error instanceof Failure ? error.code : 'STORAGE_ERROR');
    }
  }

  function declaredOf(record) {
    const sites = {};
    for (const platform of musicPlatforms) {
      const capability = record.capabilities[platform];
      if (capability) sites[platform] = { actions: [...capability.actions], qualitys: [...capability.qualitys] };
    }
    return { contentHash: record.contentHash, sites };
  }
  /** Caller holds the installation lock. Returns the ready host record of item.current. */
  async function ensureLoaded(item, signal) {
    const current = live(item.id);
    if (current?.state === 'ready' && current.contentHash === item.current.contentHash) return current;
    try {
      guard(signal);
      const spec = { installationId: item.id, protocol: 'lx', enabled: true, source: readSource(item.current),
        version: item.current.version, config: {}, permissions: { storage: ['read', 'write'] } };
      if (current?.state === 'ready') await host.update(item.id, spec);
      else {
        if (current) await host.uninstall(item.id);
        await host.install(spec);
      }
      guard(signal);
      const next = live(item.id);
      requireValue(next?.state === 'ready' && next.contentHash === item.current.contentHash &&
        next.version === item.current.version && next.protocol === 'lx', 'SCRIPT_INTEGRITY');
      failures.delete(item.id);
      return next;
    } catch (error) {
      if (!closed) {
        const code = errorCode(error);
        if (!['CANCELLED', 'CLOSED'].includes(code))
          failures.set(item.id, { contentHash: item.current.contentHash, code });
        // A failed replacement trial keeps the exact previous generation; anything
        // else that no longer matches the catalog is retired, never left half-live.
        const remaining = live(item.id);
        const owner = catalog().installations.find(entry => entry.id === item.id);
        if (remaining && (remaining.state !== 'ready' || remaining.contentHash !== owner?.current.contentHash ||
            !owner.enabled && !temporaryUse.has(item.id)))
          try { await host.uninstall(item.id); } catch { /* The next load or close reports cleanup. */ }
      }
      throw error;
    }
  }
  function remember(item, record) {
    const declared = declaredOf(record);
    try {
      edit(item.id, target => {
        if (target.current.contentHash !== record.contentHash ||
            canonical(target.declared) === canonical(declared)) return false;
        target.declared = declared;
      });
    } catch { /* The live declaration still authorizes this run; display facts retry on the next load. */ }
  }
  function summary(check) {
    if (check.failure) return 'abnormal';
    const sites = Object.values(check.sites).filter(site => site.state !== 'unsupported');
    const passed = sites.filter(site => site.state === 'ok').length;
    return passed === 0 ? 'abnormal' : passed === sites.length ? 'normal' : 'partial';
  }
  function view(item, full = false) {
    const record = live(item.id);
    const ready = item.enabled && record?.state === 'ready' && record.contentHash === item.current.contentHash;
    const failure = failures.get(item.id);
    const failed = failure?.contentHash === item.current.contentHash ? failure.code : null;
    const check = item.check?.contentHash === item.current.contentHash ? item.check : null;
    const result = {
      installationId: item.id, name: item.name, version: item.current.version,
      author: item.current.author, description: item.current.description, homepage: item.current.homepage,
      origin: item.origin.kind === 'network'
        ? { kind: 'network', host: new URL(item.origin.url).hostname } : { kind: 'file' },
      enabled: item.enabled,
      state: !item.enabled ? 'disabled' : ready ? 'ready' : failed ? 'failed' : 'idle',
      generation: ready ? record.generation : 0,
      contentHash: item.current.contentHash,
      executionHash: digest(canonical({ id: item.id, contentHash: item.current.contentHash,
        version: item.current.version })),
      rollbackAvailable: item.previous !== null,
      // null: never declared for this content. Execution re-checks the live declaration.
      sites: ready ? declaredOf(record).sites
        : item.declared?.contentHash === item.current.contentHash ? item.declared.sites : null,
      check: check && { checkedAt: check.checkedAt, failure: check.failure, summary: summary(check),
        sites: check.sites },
      lastFailure: item.enabled ? failed : null,
    };
    if (full && item.origin.kind === 'network') result.origin.url = item.origin.url;
    return result;
  }
  async function replace(id, current, previous, signal) {
    return locked(id, async () => {
      guard(signal);
      const item = find(id);
      if (item.current.contentHash === current.contentHash) return { item: view(item) };
      const candidate = { ...item, current, previous };
      let record = null;
      try {
        if (item.enabled) record = await ensureLoaded(candidate, signal);
        edit(id, target => {
          Object.assign(target, { current, previous, declared: record ? declaredOf(record) : null, check: null });
        });
      } catch (error) {
        if (record) try { await host.uninstall(id); } catch { /* Reconciled by the next load. */ }
        collect();
        throw error;
      }
      failures.delete(id);
      collect();
      return { item: view(find(id)) };
    });
  }
  async function run(id, siteId, operation, payload, signal, accept) {
    const record = await locked(id, async () => {
      guard(signal);
      const item = find(id);
      requireValue(item.enabled, 'SOURCE_DISABLED');
      const loaded = await ensureLoaded(item, signal);
      remember(item, loaded);
      return loaded;
    });
    const value = await host.invoke({ installationId: id, generation: record.generation, siteId,
      operation, payload }, { signal });
    guard(signal);
    const now = live(id);
    requireValue(now?.state === 'ready' && now.generation === record.generation, 'STALE_SOURCE');
    return { installationId: id, generation: record.generation, contentHash: record.contentHash,
      ...accept(value ?? null) };
  }
  function releaseTemporary(id) {
    const uses = temporaryUse.get(id) ?? 0;
    if (uses > 1) { temporaryUse.set(id, uses - 1); return false; }
    return temporaryUse.delete(id);
  }
  /** Light check: one public sample per declared platform, its basic declared quality,
   * the musicUrl result contract only. Never downloads audio or changes enablement. */
  async function check(id, signal) {
    let loaded;
    try {
      loaded = await locked(id, async () => {
        guard(signal);
        const item = find(id);
        const record = await ensureLoaded(item, signal);
        if (!item.enabled) temporaryUse.set(id, (temporaryUse.get(id) ?? 0) + 1);
        return { item, record };
      });
    } catch (error) {
      const code = errorCode(error);
      if (['CANCELLED', 'CLOSED', 'SOURCE_NOT_FOUND', 'CORRUPT_STORAGE', 'STORAGE_ERROR'].includes(code)) throw error;
      const item = find(id);
      const outcome = { contentHash: item.current.contentHash, checkedAt: clock(), failure: code, sites: {} };
      edit(id, target => {
        if (target.current.contentHash !== outcome.contentHash) return false;
        target.check = outcome;
      });
      return { item: view(find(id)) };
    }
    const { item, record } = loaded;
    const checkedAt = clock();
    let results;
    try {
      results = await Promise.all(musicPlatforms.map(async platform => {
        const capability = record.capabilities[platform];
        const quality = capability?.actions.includes('musicUrl')
          ? checkQualities.find(type => capability.qualitys.includes(type)) ?? capability.qualitys[0] ?? null : null;
        if (!quality) return [platform, { state: 'unsupported', quality: null, sample: null, code: null }];
        const { name, singer, ...locator } = checkSamples[platform];
        const sample = `${platform}:${locator.songmid ?? locator.hash}`;
        try {
          const value = await host.invoke({ installationId: id, generation: record.generation, siteId: platform,
            operation: 'musicUrl', payload: { type: quality, musicInfo: { name, singer, ...locator } } },
          { signal, timeoutMs: checkTimeout });
          mediaCandidate(value ?? null, clock, { installationId: id, siteKey: platform, requestedQuality: quality });
          return [platform, { state: 'ok', quality, sample, code: null }];
        } catch (error) {
          if (signal.aborted || closed) throw error;
          return [platform, { state: 'failed', quality, sample, code: errorCode(error) }];
        }
      }));
    } finally {
      if (!closed) await locked(id, async () => {
        const now = live(id);
        const owner = catalog().installations.find(entry => entry.id === id);
        if (releaseTemporary(id) && now && !owner?.enabled)
          try { await host.uninstall(id); } catch { /* Reported by the next load or close. */ }
        // Another lifecycle command replaced this generation: its results are not this content's facts.
        if (results) requireValue(now?.state === 'ready' && now.generation === record.generation, 'STALE_SOURCE');
      });
    }
    guard(signal);
    const outcome = { contentHash: record.contentHash, checkedAt, failure: null, sites: Object.fromEntries(results) };
    edit(id, target => {
      if (target.current.contentHash !== outcome.contentHash) return false;
      target.check = outcome;
      if (target.declared?.contentHash !== record.contentHash) target.declared = declaredOf(record);
    });
    return { item: view(find(item.id)) };
  }
  async function stagedOrFetched(params, origin, signal) {
    if (origin.kind === 'network') {
      requireValue(params.token === undefined);
      return { bytes: await fetchSource(origin.url, signal), basename: sourceBasename(origin.url), url: origin.url };
    }
    requireValue(typeof params.token === 'string');
    return null;
  }

  function success(value, blob = false) {
    const reply = { ok: true, contract: musicScriptContract, ...value };
    const bytes = encodeJSONValue(reply);
    if (blob) {
      try {
        const result = transfers.result(bytes, { inline: decodeJSONValue });
        return Object.hasOwn(result, 'blob') ? { ok: true, contract: musicScriptContract, ...result } : result;
      } catch (error) { bytes.fill(0); throw error; }
    }
    try { return decodeJSONValue(bytes); }
    finally { bytes.fill(0); }
  }
  const failure = error => ({ ok: false, contract: musicScriptContract, code: errorCode(error) });
  function method(allowed, required, body, { blob = false, transfer = false,
    envelope = false } = {}) {
    return async (parameters = {}, context = {}) => {
      let token, result;
      try {
        if (transfer && object(parameters)) {
          const descriptor = Object.getOwnPropertyDescriptor(parameters, 'token');
          if (typeof descriptor?.value === 'string') token = descriptor.value;
        }
        requireValue(object(context));
        const { signal } = context;
        requireValue(signal === undefined || signal instanceof AbortSignal);
        guard(signal); fields(parameters, allowed, required);
        const captured = snapshot(parameters);
        const active = signal ? AbortSignal.any([lifetime.signal, signal]) : lifetime.signal;
        result = await body(captured, active);
        guard(active);
        return envelope ? result : success(result, blob);
      } catch (error) {
        if (transfer) {
          try {
            transfers.release(token);
            if (!token && typeof result?.token === 'string') transfers.release(result.token);
            if (envelope && typeof result?.blob?.token === 'string') transfers.release(result.blob.token);
          } catch { return failure(new Failure('SCRIPT_CLEANUP')); }
        }
        // Work cancelled by module shutdown reports the shutdown, not a caller cancellation.
        return failure(closed && errorCode(error) === 'CANCELLED' ? new Failure('CLOSED') : error);
      }
    };
  }
  const business = (allowed, required, body) => method(allowed, required, body, { blob: true });
  function transferred(body) {
    try { return body(); }
    catch (error) {
      if (error instanceof ScriptHostError) {
        if (error.code === 'NOT_FOUND') throw new Failure('STAGE_NOT_FOUND');
        if (['INVALID_ARGUMENT', 'SCRIPT_PROTOCOL_ERROR'].includes(error.code))
          throw new Failure('SCRIPT_INTEGRITY');
      }
      throw error;
    }
  }
  async function shutdown() {
    if (closing) return closing;
    closed = true; lifetime.abort();
    clearStageTimer();
    for (const token of stages.keys()) release(token, false);
    closing = (async () => {
      let result, cleanupFailure = stageFailure;
      try { transfers.close(); }
      catch (error) { cleanupFailure ??= error; }
      try { result = await host.close(); }
      catch (error) { cleanupFailure ??= error; }
      finally {
        await tail;
        await Promise.allSettled([...locks.values()]);
        locks.clear(); failures.clear(); temporaryUse.clear();
      }
      stageFailure = null;
      if (cleanupFailure) throw new Failure('SCRIPT_CLEANUP');
      requireValue(result.cleanupComplete && adapterTasks.size === 0, 'SCRIPT_CLEANUP');
      return { closed: true, cleanupComplete: true };
    })();
    return closing;
  }
  async function close() {
    try { return success(await shutdown()); } catch (error) { return failure(error); }
  }
  const operations = ['sources.list', 'sources.detail', 'sources.add', 'sources.download', 'sources.edit',
    'sources.update', 'sources.rollback', 'sources.enable', 'sources.disable', 'sources.remove',
    'sources.check', 'resolve', 'invoke'];
  const methods = {
    'music.script.stage.begin': method(['basename', 'length', 'sha256'], undefined, params => {
      sweep();
      text(params.basename, 255);
      requireValue(!/[\\/:*?"<>|]/.test(params.basename) && params.basename.toLowerCase().endsWith('.js'));
      integer(params.length, 1);
      requireValue(params.length <= SCRIPT_LIMITS.maxSourceBytes, 'LIMIT');
      requireValue(typeof params.sha256 === 'string' && hashPattern.test(params.sha256));
      const token = randomUUID();
      stages.set(token, { chunks: [], length: params.length, basename: params.basename,
        hash: params.sha256, offset: 0, expiresAt: clock() + stageTTL });
      try { scheduleStages(); }
      catch (error) { release(token, false); throw error; }
      return { token, chunkBytes };
    }),
    'music.script.stage.chunk': method(['token', 'offset', 'data'], undefined, params => {
      const stage = staged(params.token);
      integer(params.offset); requireValue(params.offset === stage.offset, 'SCRIPT_INTEGRITY');
      requireValue(typeof params.data === 'string' && params.data.length <= Math.ceil(chunkBytes / 3) * 4, 'LIMIT');
      const bytes = Buffer.from(params.data, 'base64');
      let retained = false;
      try {
        requireValue(bytes.length > 0 && bytes.length <= chunkBytes && bytes.toString('base64') === params.data &&
          stage.offset + bytes.length <= stage.length, 'SCRIPT_INTEGRITY');
        const previousExpires = stage.expiresAt;
        stage.chunks.push(bytes); retained = true; stage.offset += bytes.length;
        stage.expiresAt = clock() + stageTTL;
        try { scheduleStages(); }
        catch (error) {
          stage.chunks.pop();
          stage.offset -= bytes.length;
          stage.expiresAt = previousExpires;
          retained = false;
          throw error;
        }
        return { next: stage.offset };
      } finally { if (!retained) bytes.fill(0); }
    }),
    'music.script.stage.abort': method(['token'], undefined, params => {
      text(params.token); release(params.token); return { released: true };
    }),
    'music.script.control.begin': method(['length', 'sha256'], undefined, params => {
      integer(params.length, 1);
      requireValue(typeof params.sha256 === 'string' && hashPattern.test(params.sha256));
      return transfers.begin({ ...params, kind: 'json' });
    }, { transfer: true }),
    'music.script.control.chunk': method(['token', 'offset', 'data'], undefined, params => {
      text(params.token); integer(params.offset);
      return transferred(() => transfers.chunk(params));
    }, { transfer: true }),
    'music.script.control.abort': method(['token'], undefined, params => {
      text(params.token); transfers.release(params.token); return { released: true };
    }, { transfer: true }),
    'music.script.control.call': method(['operation', 'token'], undefined, async (params, signal) => {
      text(params.token);
      requireValue(operations.includes(params.operation));
      const bytes = transferred(() => transfers.take(params.token, 'json'));
      let input;
      try { input = decodeJSONValue(bytes); }
      finally { bytes.fill(0); }
      return methods[`music.script.${params.operation}`](input, { signal });
    }, { transfer: true, envelope: true }),
    'music.script.blob.read': method(['token', 'offset'], undefined, params => {
      text(params.token); integer(params.offset);
      return transferred(() => transfers.read(params));
    }, { transfer: true }),
    'music.script.blob.release': method(['token'], undefined, params => {
      text(params.token); transfers.release(params.token); return { released: true };
    }, { transfer: true }),
    'music.script.sources.list': business([], undefined, () => {
      const value = catalog();
      return { revision: value.revision, items: value.installations.map(item => view(item)) };
    }),
    'music.script.sources.detail': business(['installationId'], undefined, params => {
      installation(params.installationId);
      return { item: view(find(params.installationId), true) };
    }),
    'music.script.sources.add': business(['token'], undefined, (params, signal) => serialize(() => {
      guard(signal);
      const { bytes, basename } = stagedBytes(params.token);
      try {
        const prepared = prepare(bytes, basename);
        release(params.token);
        const id = randomUUID();
        commit(next => {
          next.installations.push({ id, name: prepared.name, origin: { kind: 'file' }, enabled: false,
            current: prepared.version, previous: null, declared: null, check: null });
        });
        collect();
        return { item: view(find(id)) };
      } finally { bytes.fill(0); }
    })),
    'music.script.sources.download': business(['url'], undefined, async (params, signal) => {
      guard(signal);
      const url = sourceURL(params.url);
      const bytes = await fetchSource(url, signal);
      try {
        return await serialize(() => {
          guard(signal);
          const prepared = prepare(bytes, sourceBasename(url));
          const id = randomUUID();
          commit(next => {
            next.installations.push({ id, name: prepared.name, origin: { kind: 'network', url }, enabled: false,
              current: prepared.version, previous: null, declared: null, check: null });
          });
          collect();
          return { item: view(find(id)) };
        });
      } finally { bytes.fill(0); }
    }),
    'music.script.sources.edit': business(['installationId', 'name', 'url'], ['installationId'],
      (params, signal) => serialize(() => {
        guard(signal);
        installation(params.installationId);
        requireValue(params.name !== undefined || params.url !== undefined);
        const item = find(params.installationId);
        if (params.name !== undefined) text(params.name, 255);
        const url = params.url === undefined ? undefined : sourceURL(params.url);
        requireValue(url === undefined || item.origin.kind === 'network');
        edit(item.id, target => {
          if (params.name !== undefined) target.name = params.name.trim();
          if (url !== undefined) target.origin = { kind: 'network', url };
        });
        return { item: view(find(item.id), true) };
      })),
    'music.script.sources.update': business(['installationId', 'token'], ['installationId'],
      async (params, signal) => {
        guard(signal);
        installation(params.installationId);
        const fetched = await stagedOrFetched(params, find(params.installationId).origin, signal);
        try {
          return await serialize(async () => {
            guard(signal);
            const item = find(params.installationId);
            requireValue(item.origin.kind === (fetched ? 'network' : 'file'));
            requireValue(!fetched || item.origin.url === fetched.url, 'STALE_SOURCE');
            const input = fetched ?? stagedBytes(params.token);
            let prepared;
            try { prepared = prepare(input.bytes, input.basename); }
            finally { if (!fetched) input.bytes.fill(0); }
            if (!fetched) release(params.token);
            return replace(item.id, prepared.version, item.current, signal);
          });
        } finally { fetched?.bytes.fill(0); }
      }),
    'music.script.sources.rollback': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      return serialize(() => {
        guard(signal);
        const item = find(params.installationId);
        requireValue(item.previous, 'ROLLBACK_UNAVAILABLE');
        return replace(item.id, item.previous, item.current, signal);
      });
    }),
    'music.script.sources.enable': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      return serialize(() => locked(params.installationId, async () => {
        guard(signal);
        const item = find(params.installationId);
        const record = await ensureLoaded(item, signal);
        temporaryUse.delete(item.id);
        try {
          edit(item.id, target => {
            const declared = declaredOf(record);
            if (target.enabled && canonical(target.declared) === canonical(declared)) return false;
            target.enabled = true; target.declared = declared;
          });
        } catch (error) {
          if (!item.enabled) try { await host.uninstall(item.id); } catch { /* Reconciled by the next load. */ }
          throw error;
        }
        return { item: view(find(item.id)) };
      }));
    }),
    'music.script.sources.disable': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      return serialize(() => locked(params.installationId, async () => {
        guard(signal);
        const item = find(params.installationId);
        let cleanup = false;
        temporaryUse.delete(item.id);
        if (live(item.id)) try { await host.uninstall(item.id); } catch { cleanup = true; }
        edit(item.id, target => {
          if (!target.enabled) return false;
          target.enabled = false;
        });
        requireValue(!cleanup && !live(item.id), 'SCRIPT_CLEANUP');
        return { item: view(find(item.id)) };
      }));
    }),
    'music.script.sources.remove': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      return serialize(() => locked(params.installationId, async () => {
        guard(signal);
        const item = find(params.installationId);
        let cleanup = false;
        temporaryUse.delete(item.id);
        if (live(item.id)) try { await host.uninstall(item.id); } catch { cleanup = true; }
        // Private data goes first: a removed installation never leaves it behind.
        documents.remove(dataName(item.id));
        commit(next => { next.installations = next.installations.filter(entry => entry.id !== item.id); });
        failures.delete(item.id);
        collect();
        requireValue(!cleanup && !live(item.id), 'SCRIPT_CLEANUP');
        return { removed: true };
      }));
    }),
    'music.script.sources.check': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      return check(params.installationId, signal);
    }),
    'music.script.resolve': business(['installationId', 'platform', 'quality', 'musicInfo'], undefined,
      (params, signal) => {
        installation(params.installationId);
        requireValue(musicPlatforms.includes(params.platform) && typeof params.quality === 'string' &&
          /^[A-Za-z0-9_]{1,32}$/.test(params.quality) && object(params.musicInfo));
        const expected = { installationId: params.installationId, siteKey: params.platform,
          requestedQuality: params.quality };
        return run(params.installationId, params.platform, 'musicUrl',
          { type: params.quality, musicInfo: lxInfo(params.musicInfo) }, signal,
          value => ({ platform: params.platform, quality: params.quality, ...mediaCandidate(value, clock, expected, params.musicInfo.duration) }));
      }),
    // Any other declared action (lyric, extensions); the value stays raw for its consumer.
    'music.script.invoke': business(['installationId', 'siteId', 'operation', 'payload'], undefined,
      (params, signal) => {
        installation(params.installationId);
        requireValue(typeof params.siteId === 'string' && sitePattern.test(params.siteId) &&
          typeof params.operation === 'string' && sitePattern.test(params.operation) && object(params.payload));
        const payload = object(params.payload.musicInfo)
          ? { ...params.payload, musicInfo: lxInfo(params.payload.musicInfo) } : params.payload;
        return run(params.installationId, params.siteId, params.operation, payload, signal,
          value => ({ siteId: params.siteId, value }));
      }),
    'music.script.close': async (params = {}) => {
      try { fields(params, []); return await close(); } catch (error) { return failure(error); }
    },
  };
  return { id: 'music', version: '0.8.0', methods, close };
}
