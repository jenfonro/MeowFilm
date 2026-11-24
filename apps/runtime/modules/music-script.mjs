import { isUtf8 } from 'node:buffer';
import { createHash, randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import {
  createScriptHost, createNodeNetwork, createSourceDownload, parseLXHeader, PrivateSources, ScriptHostError,
} from '@meowfilm/script-host';
import { BridgeError, validateJSONValue } from '@meowfilm/runtime-bridge';
import { stringifyJSONValue, cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { contracts } from '../contracts.mjs';
import { canonicalJSON as canonical } from './json-canonical.mjs';
import { SourceCandidates, InstallationOperations } from './source-candidates.mjs';
import { createMusicStorage } from './storage/music-storage.mjs';

const musicScriptContract = contracts.music.contract;
/** Platforms an LX source can resolve for the native music catalog. */
const musicPlatforms = Object.freeze(['wy', 'tx', 'kw', 'kg', 'mg']);
const downloadTimeout = 30000;
const checkTimeout = 15000;
const codes = contracts.music.codes;
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
class Failure extends Error {
  constructor(code) { super(codes.has(code) ? code : 'SCRIPT_FAILURE'); this.code = this.message; }
}
const requireValue = (condition, code = 'INVALID_REQUEST') => { if (!condition) throw new Failure(code); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const hasFields = (value, keys) => object(value) && keys.every(key => Object.hasOwn(value, key));
const digest = value => createHash('sha256').update(value).digest('hex');
const scalars = value => [...value].length;
const control = /[\u0000-\u001f\u007f]/;
function fields(value, required) {
  requireValue(object(value) && required.every(key => Object.hasOwn(value, key)));
}
function integer(value, min = 0) {
  requireValue(Number.isSafeInteger(value) && value >= min);
}
function installation(value) {
  requireValue(typeof value === 'string' && uuidPattern.test(value));
}
function snapshot(value) { return cloneJSONValue(value); }
const plainText = (value, max, empty = true) => typeof value === 'string' &&
  (empty || value.length > 0) && (max === undefined || scalars(value) <= max) && !control.test(value);
// LX consumes JavaScript Numbers; never replace the exact stored JSON with this projection.
const lxValue = value => JSON.parse(stringifyJSONValue(value, -Infinity));
function decode(bytes) {
  requireValue(isUtf8(bytes), 'SCRIPT_ENCODING');
  const source = bytes.toString('utf8');
  requireValue(source.trim().length > 0, 'SCRIPT_ENCODING');
  return source;
}
function clean(value) {
  const trimmed = value.trim();
  if (!trimmed || control.test(trimmed)) return '';
  return trimmed;
}
/** Leading LX comment metadata. It describes the file; it never authorizes anything. */
function describe(source, basename) {
  const header = parseLXHeader(source);
  const fallback = clean(basename.replace(/\.js$/i, ''));
  return {
    name: clean(header.name) || fallback || 'LX 音源',
    version: clean(header.version) || '1',
    author: clean(header.author),
    description: clean(header.description),
    homepage: clean(header.homepage),
  };
}
function sourceURL(value) {
  requireValue(typeof value === 'string', 'INVALID_SOURCE_URL');
  const input = value.trim();
  requireValue(input.length > 0 && !/[\u0000-\u0020\u007f]/.test(input),
    'INVALID_SOURCE_URL');
  let url;
  try { url = new URL(input); } catch { throw new Failure('INVALID_SOURCE_URL'); }
  requireValue(['http:', 'https:'].includes(url.protocol) && url.hostname.length > 0 && !url.username && !url.password &&
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
function validSourceVersion(value) {
  return hasFields(value, ['contentHash', 'bytes', 'version']) &&
    typeof value.contentHash === 'string' && hashPattern.test(value.contentHash) &&
    Number.isSafeInteger(value.bytes) && value.bytes > 0 &&
    plainText(value.version, undefined, false);
}
function validVersion(value) {
  return validSourceVersion(value) && plainText(value.author) &&
    plainText(value.description) && plainText(value.homepage);
}
function validOrigin(value) {
  if (!hasFields(value, ['kind'])) return false;
  if (value.kind === 'file') return true;
  if (!hasFields(value, ['url']) || value.kind !== 'network') return false;
  try { return sourceURL(value.url) === value.url; } catch { return false; }
}
const validList = (value, max) => Array.isArray(value) &&
  value.every(item => plainText(item, max, false)) && new Set(value).size === value.length;
function validDeclared(value) {
  if (!hasFields(value, ['contentHash', 'capabilities']) || !hashPattern.test(value.contentHash) ||
      !object(value.capabilities)) return false;
  try { validateJSONValue(value.capabilities); } catch { return false; }
  return Object.entries(value.capabilities).every(([site, capability]) => sitePattern.test(site) &&
    object(capability) && validList(capability.actions, 128) &&
    (capability.qualitys === undefined || validList(capability.qualitys, 64)));
}
function validCheck(value) {
  return hasFields(value, ['contentHash', 'checkedAt', 'failure', 'sites']) && hashPattern.test(value.contentHash) &&
    Number.isSafeInteger(value.checkedAt) && value.checkedAt >= 0 && validCode(value.failure) &&
    object(value.sites) && Object.values(value.sites).every(result =>
      hasFields(result, ['state', 'quality', 'sample', 'code']) && ['ok', 'failed', 'unsupported'].includes(result.state) &&
      (result.quality === null || plainText(result.quality, 64, false)) &&
      (result.sample === null || plainText(result.sample, 128, false)) && validCode(result.code));
}
function validCatalog(value) {
  return hasFields(value, ['schema', 'revision', 'installations']) && value.schema === 1 &&
    Number.isSafeInteger(value.revision) && value.revision >= 0 && Array.isArray(value.installations);
}
function validInstallation(item) {
  return hasFields(item, ['id', 'name', 'origin', 'enabled', 'current']) &&
    typeof item.id === 'string' && uuidPattern.test(item.id) &&
    plainText(item.name, undefined, false) && item.name.trim().length > 0 && typeof item.enabled === 'boolean' &&
    validOrigin(item.origin) && validVersion(item.current);
}
function errorCode(error) {
  if (error instanceof Failure) return error.code;
  if (error instanceof BridgeError) return 'INVALID_REQUEST';
  if (error instanceof ScriptHostError) return ({
    INVALID_ARGUMENT: 'INVALID_REQUEST', HOST_CLOSED: 'CLOSED', CANCELLED: 'CANCELLED',
    TIMEOUT: 'TIMEOUT', STALE_SESSION: 'STALE_SOURCE', DISABLED: 'SOURCE_DISABLED',
    NOT_FOUND: 'SOURCE_NOT_FOUND', ALREADY_EXISTS: 'STALE_SOURCE',
    CAPABILITY_UNAVAILABLE: 'CAPABILITY_UNAVAILABLE', PERMISSION_DENIED: 'PERMISSION_DENIED',
    NATIVE_ARTIFACT_INVALID: 'CAPABILITY_UNAVAILABLE', NATIVE_ABI_MISMATCH: 'CAPABILITY_UNAVAILABLE',
    RESOURCE_LIMIT: 'LIMIT', NETWORK_ERROR: 'NETWORK_ERROR', STORAGE_ERROR: 'STORAGE_ERROR',
    STORAGE_CONFLICT: 'STORAGE_ERROR', CORRUPT_STORAGE: 'CORRUPT_STORAGE', SCRIPT_CLEANUP: 'SCRIPT_CLEANUP',
    UPSTREAM_HTTP_ERROR: 'UPSTREAM_HTTP', HTTP_ERROR: 'UPSTREAM_HTTP', INVALID_JSON: 'UPSTREAM_FORMAT',
    SCRIPT_PROTOCOL_ERROR: 'SCRIPT_PROTOCOL_ERROR', ROLLBACK_UNAVAILABLE: 'ROLLBACK_UNAVAILABLE',
  })[error.code] ?? 'SCRIPT_FAILURE';
  if (['STORAGE_CORRUPT', 'STORAGE_SCHEMA'].includes(error?.code)) return 'CORRUPT_STORAGE';
  if (['STORAGE_BUSY', 'STORAGE_CLOSED', 'STORAGE_CONFIGURATION', 'STORAGE_CONFLICT',
    'STORAGE_CONSTRAINT', 'STORAGE_DRIVER_REQUIRED', 'STORAGE_ERROR', 'STORAGE_FULL',
    'STORAGE_IO', 'STORAGE_READONLY', 'STORAGE_RESULT_LIMIT', 'UNSAFE_PATH'].includes(error?.code))
    return 'STORAGE_ERROR';
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
  requireValue(object(value), 'INVALID_MEDIA_CANDIDATE');
  const seen = new Set(), headers = {};
  for (const [name, item] of Object.entries(value)) {
    const lower = name.toLowerCase();
    requireValue(/^[A-Za-z0-9!#$%&'*+.^_`|~-]+$/.test(name) && !seen.has(lower) &&
      typeof item === 'string' && /^[\u0020-\u007e]*$/.test(item), 'INVALID_MEDIA_CANDIDATE');
    requireValue(!forbiddenHeaders.has(lower) &&
      !['if-', 'content-', 'proxy-', 'sec-'].some(prefix => lower.startsWith(prefix)), 'MEDIA_HEADERS_UNSUPPORTED');
    seen.add(lower);
    headers[name] = item;
  }
  return headers;
}
/** The one musicUrl result contract shared by playback and checks. */
function mediaCandidate(raw, expected, duration = 0) {
  requireValue(raw !== null && raw !== undefined && !(typeof raw === 'string' && raw.trim().length === 0),
    'MEDIA_UNAVAILABLE');
  const candidate = typeof raw === 'string' ? { url: raw } : raw;
  requireValue(object(candidate), 'INVALID_MEDIA_CANDIDATE');
  const url = candidate.url;
  requireValue(typeof url === 'string' && url.length > 0 &&
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
    requireValue(candidate.expiresAt > Date.now(), 'EXPIRED_MEDIA_CANDIDATE');
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
  return { url, headers: mediaHeaders(candidate.headers), expiresAt,
    duration: candidate.duration > 0 ? candidate.duration : null };
}
/** LX musicInfo with the conventional mm:ss interval derived from a known duration. */
function lxInfo(musicInfo) {
  const info = { ...musicInfo };
  if (typeof info.duration === 'number' && Number.isFinite(info.duration) && info.duration >= 0) {
    const pad = value => String(Math.floor(value)).padStart(2, '0');
    info.interval = `${pad(info.duration / 60)}:${pad(info.duration % 60)}`;
  }
  return info;
}
/**
 * Shared LX music source management and execution. This module is the only
 * writer of music installation facts, declarations/checks and private KV in the
 * shared database, plus immutable source files in `<dataRoot>/music`.
 * Native hosts supply the data root, file selection and presentation.
 * ScriptHost executes guest code in isolated QuickJS heaps.
 */
export function createMusicScriptModule({ dataRoot, hostFactory = createScriptHost, network, debug } = {}) {
  const activeNetwork = network ?? createNodeNetwork({ debug, debugCategory: 'music-playback', debugSub: 'music.script.http' });
  const download = createSourceDownload({ debug, debugCategory: 'music-playback', debugSub: 'music.script.download' });
  requireValue(typeof dataRoot === 'string' && typeof hostFactory === 'function' &&
    typeof activeNetwork === 'function');
  let sourceFiles;
  try { sourceFiles = new PrivateSources(dataRoot, 'music'); }
  catch { throw new Failure('INVALID_REQUEST'); }
  const persistence = createMusicStorage(dataRoot);
  let catalogWrites = Promise.resolve();
  const ready = persistence.load().then(value => {requireValue(validCatalog(value), 'CORRUPT_STORAGE');cached=value;});
  ready.catch(() => {});
  const failures = new Map(), temporaryUse = new Map(), observations = new Map();
  const adapterTasks = new Set(), proposals = new Map(), active = new Map(), retirements = new Map();
  const sources = new SourceCandidates({ module: 'music', fail: code => new Failure(code) });
  const operations = new InstallationOperations();
  const lifetime = new AbortController();
  let closed = false, closing, cached = null;
  function track(input, promise, kind = 'network') {
    const task = { installationId: input.installationId, generation: input.generation, kind, promise };
    adapterTasks.add(task);
    promise.finally(() => adapterTasks.delete(task)).catch(() => {});
    return promise;
  }
  const host = hostFactory({
    network: input => track(input.scope, Promise.resolve().then(() => activeNetwork(input))),
    storage: Object.fromEntries(['get', 'set', 'delete'].map(operation =>
      [operation, input => storage(operation, input)])),
    debug,
  });
  function guard(signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  const live = id => host.list().find(record => record.installationId === id);
  function retire(id) {
    const existing = retirements.get(id);
    if (existing) return existing;
    if (!live(id)) return Promise.resolve();
    const result = Promise.resolve(host.uninstall(id));
    retirements.set(id, result);
    result.finally(() => { if (retirements.get(id) === result) retirements.delete(id); }).catch(() => {});
    return result;
  }
  function cleanup(id, keepGeneration) {
    requireValue(!host.stats().cleanupIssues.some(issue => issue.installationId === id) &&
      ![...adapterTasks].some(task => task.installationId === id && task.kind === 'network' &&
        task.generation !== keepGeneration), 'SCRIPT_CLEANUP');
  }
  // Admission targets the captured operation/initializer, never a future successor.
  function interrupt(id) {
    const stop = host.captureInitialization(id);
    const owner = active.get(id);
    if (owner) { owner.superseded = true; owner.controller.abort(); }
    stop?.();
  }
  function locked(id, signal, body) {
    return operations.run(id, async () => {
      guard(signal);
      const owner = { controller: new AbortController(), superseded: false };
      active.set(id, owner);
      const scoped = AbortSignal.any([signal, owner.controller.signal]);
      try {
        const value = await body(scoped);
        guard(scoped);
        return value;
      } catch (error) {
        if (owner.superseded && !signal.aborted && !closed && errorCode(error) !== 'SCRIPT_CLEANUP')
          throw new Failure('STALE_SOURCE');
        throw error;
      } finally { if (active.get(id) === owner) active.delete(id); }
    });
  }
  function catalog() {requireValue(cached, 'STORAGE_ERROR');return cached;}
  function find(id) {
    const matches = catalog().installations.filter(entry => entry?.id === id);
    requireValue(matches.length > 0, 'SOURCE_NOT_FOUND');
    requireValue(matches.length === 1, 'CORRUPT_STORAGE');
    return matches[0];
  }
  /** Serialize catalog changes and publish only after the typed transaction commits. */
  function commit(change) {
    const work = catalogWrites.then(async () => {
      await ready;
      // Nested installation snapshots are replaced as units; keep unconsumed declarations intact.
      const current = catalog();
      const next = {...current, installations:current.installations.map(item => ({...item}))};
      if (change(next) === false) return;
      next.revision += 1;
      requireValue(validCatalog(next));
      await persistence.save(next);
      cached = next;
    });
    catalogWrites = work.catch(() => {});
    return work;
  }
  function edit(id, change) {
    return commit(next => {
      const target = next.installations.find(entry => entry?.id === id);
      requireValue(target, 'SOURCE_NOT_FOUND');return change(target);
    });
  }
  /** Source files no installation references any more. An orphan grants nothing. */
  function collect() {
    try {
      const referenced = new Set([...proposals.keys()].map(hash => `sources/${hash}.js`));
      for (const item of catalog().installations)
        for (const version of [item.current, item.previous])
          if (version) referenced.add(`sources/${version.contentHash}.js`);
      for (const name of sourceFiles.list('sources'))
        if (!referenced.has(name)) sourceFiles.remove(name);
    } catch { /* The next completed management command retries collection. */ }
  }
  function prepare(bytes, basename, trustedHash) {
    requireValue(bytes.length > 0, 'SCRIPT_ENCODING');
    const header = describe(decode(bytes), basename);
    const contentHash = trustedHash ?? digest(bytes), name = `sources/${contentHash}.js`;
    const existing = sourceFiles.read(name);
    if (!existing?.equals(bytes)) sourceFiles.write(name, bytes);
    proposals.set(contentHash, (proposals.get(contentHash) ?? 0) + 1);
    return { name: header.name, version: { contentHash, bytes: bytes.length, version: header.version,
      author: header.author, description: header.description, homepage: header.homepage } };
  }
  function releaseSource(version) {
    const count = proposals.get(version.contentHash);
    if (count > 1) proposals.set(version.contentHash, count - 1);
    else proposals.delete(version.contentHash);
    collect();
  }
  function readSource(version) {
    const bytes = sourceFiles.read(`sources/${version.contentHash}.js`);
    requireValue(bytes, 'CORRUPT_STORAGE');
    requireValue(bytes.length === version.bytes && digest(bytes) === version.contentHash, 'CORRUPT_STORAGE');
    return decode(bytes);
  }
  async function fetchSource(url, signal, id) {
    const timeout = new AbortController();
    const timer = setTimeout(() => timeout.abort(), downloadTimeout);
    try { timer?.unref?.(); } catch { /* The active timer still bounds the download. */ }
    let response;
    try {
      response = await track({ installationId: id }, Promise.resolve().then(() => download({
        url, options: { followRedirect: true },
        signal: AbortSignal.any([signal, timeout.signal]), validateURL: sourceURL,
      })), 'download');
      guard(signal);
      requireValue(!timeout.signal.aborted, 'TIMEOUT');
    } catch (error) {
      if (signal.aborted) throw new Failure('CANCELLED');
      if (timeout.signal.aborted) throw new Failure('TIMEOUT');
      if (error instanceof ScriptHostError && error.code === 'RESOURCE_LIMIT') throw new Failure('LIMIT');
      throw new Failure('SOURCE_DOWNLOAD_FAILED');
    } finally { clearTimeout(timer); }
    requireValue(response?.statusCode === 200 && Buffer.isBuffer(response.body) && response.body.length > 0,
      'SOURCE_DOWNLOAD_FAILED');
    return response.body;
  }

  /** Guest KV permission is checked both before enqueueing and before commit. */
  async function storage(operation, input) {
    try {
      requireValue(object(input) && typeof input.installationId === 'string' && uuidPattern.test(input.installationId) &&
        Number.isSafeInteger(input.generation) && input.generation > 0 && typeof input.siteId === 'string' &&
        sitePattern.test(input.siteId) && typeof input.key === 'string' && input.key.length > 0, 'PERMISSION_DENIED');
      const check = () => {
        requireValue(!closed && input.signal instanceof AbortSignal && !input.signal.aborted &&
          typeof input.isCurrent === 'function' && input.isCurrent() === true, 'STALE_SOURCE');
        const record = live(input.installationId);
        requireValue(record?.state === 'ready' && record.generation === input.generation, 'STALE_SOURCE');
        requireValue(Object.hasOwn(record.capabilities,input.siteId),'PERMISSION_DENIED');
      };
      if (operation === 'set') {
        validateJSONValue(input.value);
      }
      const value = await persistence.kv(operation,input,check);
      return operation === 'get' ? lxValue(value) : null;
    } catch (error) {
      if (error instanceof ScriptHostError) throw error;
      if (error instanceof BridgeError) throw storageError('INVALID_REQUEST');
      if (error?.code === 'STORAGE_CORRUPT') throw storageError('CORRUPT_STORAGE');
      throw storageError(error instanceof Failure ? error.code : error?.code ?? 'STORAGE_ERROR');
    }
  }

  function declaredOf(record) {
    return { contentHash: record.contentHash, capabilities: snapshot(record.capabilities) };
  }
  function sameDeclaration(previous, next) {
    try { return canonical(persistence.readDeclaration(previous) ?? null) === canonical(next); }
    catch (error) {
      // An unreadable old declaration is replaced by the live one, not required for its admission.
      if (error?.code === 'STORAGE_CORRUPT' || error instanceof BridgeError) return false;
      throw error;
    }
  }
  function platformSites(capabilities) {
    const sites = {};
    for (const platform of musicPlatforms) {
      const capability = capabilities[platform];
      if (capability) sites[platform] = { actions: [...capability.actions], qualitys: [...(capability.qualitys ?? [])] };
    }
    return sites;
  }
  /** Cold startup is lazy; only an explicit management command may retry a failed owner. */
  async function ensureLoaded(item, signal, explicit = false) {
    requireValue(typeof item.enabled === 'boolean' && validSourceVersion(item.current), 'CORRUPT_STORAGE');
    const current = live(item.id);
    if (current?.state === 'ready' && current.contentHash === item.current.contentHash) return current;
    const failed = failures.get(item.id);
    if (!explicit) {
      requireValue(!current, 'SOURCE_DISABLED');
      if (failed?.contentHash === item.current.contentHash) throw new Failure(failed.code);
    }
    try {
      guard(signal);
      cleanup(item.id, current?.generation);
      const spec = { installationId: item.id, protocol: 'lx', enabled: true, source: readSource(item.current),
        contentHash: item.current.contentHash, version: item.current.version,
        config: {}, permissions: { storage: ['read', 'write'] } };
      if (current?.state === 'ready') await host.update(item.id, spec, { signal });
      else {
        if (current) await retire(item.id);
        cleanup(item.id);
        await host.install(spec, { signal });
      }
      guard(signal);
      const next = live(item.id);
      requireValue(next?.state === 'ready' && next.contentHash === item.current.contentHash &&
        next.version === item.current.version && next.protocol === 'lx', 'SCRIPT_INTEGRITY');
      cleanup(item.id, next.generation);
      failures.delete(item.id);
      return next;
    } catch (error) {
      if (!closed) {
        const owner = catalog().installations.find(entry => entry?.id === item.id);
        const remaining = live(item.id);
        const retained = current?.state === 'ready' && remaining?.state === 'ready' &&
          remaining.generation === current.generation && remaining.contentHash === owner?.current.contentHash;
        if (!retained) {
          observations.delete(item.id);
          // Keep a failure marker against the attempted version: never poison an unrelated previous version.
          failures.set(item.id, { contentHash: item.current.contentHash,
            code: errorCode(error) });
          if (remaining) await retire(item.id);
        }
        cleanup(item.id, retained ? remaining.generation : undefined);
      }
      throw error;
    }
  }
  async function remember(item, record) {
    const declared = declaredOf(record);
    await edit(item.id, target => {
      if (target.current.contentHash !== record.contentHash ||
          sameDeclaration(target.declared, declared)) return false;
      target.declared = declared;
    });
    observations.set(item.id, host.observe(item.id));
  }
  function summary(check) {
    if (check.failure) return 'abnormal';
    const sites = Object.values(check.sites).filter(site => site.state !== 'unsupported');
    const passed = sites.filter(site => site.state === 'ok').length;
    return passed === 0 ? 'abnormal' : passed === sites.length ? 'normal' : 'partial';
  }
  function view(item, { full = false, isolateCorrupt = false } = {}) {
    if (!validInstallation(item)) {
      if (isolateCorrupt && object(item) && typeof item.id === 'string' && uuidPattern.test(item.id)) {
        return {
          installationId: item.id,
          name: typeof item.name === 'string' ? item.name : '',
          version: typeof item.current?.version === 'string' ? item.current.version : '',
          author: '', description: '', homepage: '',
          origin: { kind: 'file' },
          enabled: item.enabled === true,
          state: 'failed',
          generation: 0,
          contentHash: typeof item.current?.contentHash === 'string' ? item.current.contentHash : '',
          executionHash: '',
          rollbackAvailable: false,
          capabilities: null,
          sites: null,
          check: null,
          lastFailure: 'CORRUPT_STORAGE',
          errorCode: 'CORRUPT_STORAGE',
        };
      }
      throw new Failure('CORRUPT_STORAGE');
    }
    const record = observations.get(item.id)?.() ?? live(item.id);
    const ready = item.enabled && record?.state === 'ready' && record.contentHash === item.current.contentHash;
    const failure = failures.get(item.id);
    const failed = record?.state === 'failed' ? errorCode(new ScriptHostError(record.lastFailure ?? 'DISABLED'))
      : failure?.contentHash === item.current.contentHash ? failure.code : null;
    const liveCapabilities = record?.contentHash === item.current.contentHash && record.state === 'ready';
    let corrupt = false;
    let declaration = null;
    if (!liveCapabilities && item.declared?.contentHash === item.current.contentHash) {
      try {
        const candidate = persistence.readDeclaration(item.declared);
        if (validDeclared(candidate)) declaration = candidate;
        else if (isolateCorrupt) corrupt = true;
        else throw new Failure('CORRUPT_STORAGE');
      } catch (error) {
        if (isolateCorrupt && ['CORRUPT_STORAGE', 'STORAGE_CORRUPT', 'STORAGE_SCHEMA'].includes(error?.code)) {
          corrupt = true;
        } else throw error;
      }
    }
    let check = null;
    if (item.check?.contentHash === item.current.contentHash) {
      if (validCheck(item.check)) check = item.check;
      else if (isolateCorrupt) corrupt = true;
      else throw new Failure('CORRUPT_STORAGE');
    }
    const capabilities = liveCapabilities ? record.capabilities : declaration?.capabilities ?? null;
    const result = {
      installationId: item.id, name: item.name, version: item.current.version,
      author: item.current.author, description: item.current.description, homepage: item.current.homepage,
      origin: item.origin.kind === 'network'
        ? { kind: 'network', host: new URL(item.origin.url).hostname } : { kind: 'file' },
      enabled: item.enabled,
      state: !item.enabled ? 'disabled' : ready ? 'ready' : failed ? 'failed' : active.has(item.id) ? 'starting' : 'idle',
      generation: ready ? record.generation : 0,
      contentHash: item.current.contentHash,
      executionHash: digest(canonical({ id: item.id, contentHash: item.current.contentHash,
        version: item.current.version })),
      rollbackAvailable: item.previous != null,
      // null: never declared for this content. Execution re-checks the live declaration.
      capabilities,
      sites: capabilities === null ? null : platformSites(capabilities),
      check: check && { checkedAt: check.checkedAt, failure: check.failure, summary: summary(check),
        sites: check.sites },
      lastFailure: item.enabled ? failed : null,
      ...(corrupt ? { errorCode: 'CORRUPT_STORAGE' } : {}),
    };
    if (full && item.origin.kind === 'network') result.origin.url = item.origin.url;
    return result;
  }
  /** Caller owns this installation's operation; catalog publication is synchronous. */
  async function replace(id, current, previous, signal) {
    guard(signal);
    const item = find(id);
    if (item.current.contentHash === current.contentHash) return { item: view(item) };
    const priorLive = live(id);
    const hadHealthyPrevious = priorLive?.state === 'ready' && priorLive.contentHash === item.current.contentHash;
    const candidate = { ...item, current, previous };
    let record;
    try {
      if (item.enabled) record = await ensureLoaded(candidate, signal, true);
      else if (live(id)) {
        temporaryUse.delete(id); observations.delete(id);
        await retire(id);
        cleanup(id);
      }
      guard(signal);
      await edit(id, target => {
        Object.assign(target, { current, previous, declared: record ? declaredOf(record) : null, check: null });
      });
      if (record) observations.set(id, host.observe(id));
    } catch (error) {
      if (record && !closed) {
        failures.set(id, { contentHash: current.contentHash, code: errorCode(error) });
        const remaining = live(id);
        if (hadHealthyPrevious && remaining?.contentHash === current.contentHash) {
          let restored = null;
          try {
            await host.rollback(id);
            restored = live(id);
          } catch { restored = null; }
          if (restored?.state === 'ready' && restored.contentHash === item.current.contentHash) {
            observations.set(id, host.observe(id));
            cleanup(id, restored.generation);
          } else {
            observations.delete(id);
            await retire(id);
            cleanup(id);
          }
        } else if (remaining && remaining.contentHash !== item.current.contentHash) {
          observations.delete(id);
          await retire(id);
          cleanup(id);
        }
      }
      throw error;
    }
    failures.delete(id);
    return { item: view(find(id)) };
  }
  async function run(id, siteId, operation, payload, signal, accept) {
    const record = await locked(id, signal, async scoped => {
      const item = find(id);
      requireValue(item.enabled, 'SOURCE_DISABLED');
      const loaded = await ensureLoaded(item, scoped);
      await remember(item, loaded);
      return loaded;
    });
    const value = await host.invoke({ installationId: id, generation: record.generation, siteId,
      operation, payload }, { signal });
    guard(signal);
    const now = live(id), item = find(id);
    requireValue(item.enabled && now?.state === 'ready' && now.generation === record.generation &&
      item.current.contentHash === record.contentHash, 'STALE_SOURCE');
    return accept(value ?? null, record);
  }
  function releaseTemporary(id, generation) {
    const use = temporaryUse.get(id);
    if (use?.generation !== generation) return false;
    if (use.count > 1) { use.count--; return false; }
    return temporaryUse.delete(id);
  }
  /** Light check: one public sample per declared platform, its basic declared quality,
   * the musicUrl result contract only. Never downloads audio or changes enablement. */
  async function check(id, signal) {
    let loaded;
    try {
      loaded = await locked(id, signal, async scoped => {
        guard(scoped);
        const item = find(id);
        const record = await ensureLoaded(item, scoped);
        try { await remember(item, record); }
        catch (error) {
          if (!item.enabled && !temporaryUse.has(id)) {
            await retire(id);
            cleanup(id);
          }
          throw error;
        }
        if (!item.enabled) {
          const use = temporaryUse.get(id);
          temporaryUse.set(id, { generation: record.generation,
            count: use?.generation === record.generation ? use.count + 1 : 1 });
        }
        return { item, record };
      });
    } catch (error) {
      const code = errorCode(error);
      if (['CANCELLED', 'CLOSED', 'STALE_SOURCE', 'SOURCE_NOT_FOUND', 'CORRUPT_STORAGE', 'STORAGE_ERROR', 'SCRIPT_CLEANUP'].includes(code)) throw error;
      const item = find(id);
      const outcome = { contentHash: item.current.contentHash, checkedAt: Date.now(), failure: code, sites: {} };
      await edit(id, target => {
        if (target.current.contentHash !== outcome.contentHash) return false;
        target.check = outcome;
      });
      return { item: view(find(id)) };
    }
    const { item, record } = loaded;
    const checkedAt = Date.now();
    let results;
    try {
      results = await Promise.all(musicPlatforms.map(async platform => {
        const capability = record.capabilities[platform];
        const qualitys = Array.isArray(capability?.qualitys) ? capability.qualitys : [];
        const quality = capability?.actions?.includes('musicUrl') && qualitys.length > 0
          ? checkQualities.find(type => qualitys.includes(type)) ?? qualitys[0] ?? null : null;
        if (!quality) return [platform, { state: 'unsupported', quality: null, sample: null, code: null }];
        const { name, singer, ...locator } = checkSamples[platform];
        const sample = `${platform}:${locator.songmid ?? locator.hash}`;
        try {
          const value = await host.invoke({ installationId: id, generation: record.generation, siteId: platform,
            operation: 'musicUrl', payload: { type: quality, musicInfo: { name, singer, ...locator } } },
          { signal, timeoutMs: checkTimeout });
          mediaCandidate(value ?? null, { installationId: id, siteKey: platform, requestedQuality: quality });
          return [platform, { state: 'ok', quality, sample, code: null }];
        } catch (error) {
          if (signal.aborted || closed) throw error;
          return [platform, { state: 'failed', quality, sample, code: errorCode(error) }];
        }
      }));
    } finally {
      if (!closed) await operations.run(id, async () => {
        const now = live(id);
        const owner = catalog().installations.find(entry => entry?.id === id);
        if (releaseTemporary(id, record.generation) && now?.generation === record.generation && !owner?.enabled) {
          observations.delete(id);
          await retire(id);
          cleanup(id);
        }
        // Another lifecycle command replaced this generation: its results are not this content's facts.
        if (results) requireValue(now?.state === 'ready' && now.generation === record.generation, 'STALE_SOURCE');
      });
    }
    guard(signal);
    const outcome = { contentHash: record.contentHash, checkedAt, failure: null, sites: Object.fromEntries(results) };
    await edit(id, target => {
      if (target.current.contentHash !== outcome.contentHash) return false;
      target.check = outcome;
      if (target.declared?.contentHash !== record.contentHash) target.declared = declaredOf(record);
    });
    return { item: view(find(item.id)) };
  }
  function success(value) {
    validateJSONValue(value);
    return { ok: true, contract: musicScriptContract, value };
  }
  const failure = error => ({ ok: false, contract: musicScriptContract, code: errorCode(error) });
  function business(allowed, required, body) {
    return async (parameters = {}, context = {}, debugHook) => {
      try {
        requireValue(object(context));
        const { signal } = context;
        requireValue(signal === undefined || signal instanceof AbortSignal);
        await ready;
        guard(signal); fields(parameters, required ?? allowed);
        const captured = snapshot(Object.fromEntries(allowed
          .filter(key => Object.hasOwn(parameters, key)).map(key => [key, parameters[key]])));
        const scoped = signal ? AbortSignal.any([lifetime.signal, signal]) : lifetime.signal;
        const result = await body(captured, scoped);
        guard(scoped);
        return success(result);
      } catch (error) {
        debugHook?.onError?.(error);
        return failure(closed && errorCode(error) === 'CANCELLED' ? new Failure('CLOSED') : error);
      }
    };
  }
  async function upload(input, bytes, { signal } = {}) {
    try { guard(signal); installation(input?.installationId); return success(sources.put(input, bytes, signal)); }
    catch (error) { return failure(error); }
  }
  async function shutdown() {
    if (closing) return closing;
    closed = true; lifetime.abort(); sources.close();
    closing = (async () => {
      let result, cleanupFailure;
      try { result = await host.close(); } catch (error) { cleanupFailure = error; }
      finally {
        await operations.drain();
        // Downloads have no guest session to drain them; wait for the actual adapter.
        await Promise.allSettled([...adapterTasks].filter(task => task.kind === 'download').map(task => task.promise));
        await Promise.allSettled([ready,catalogWrites]);
        await persistence.close();
        failures.clear(); temporaryUse.clear(); observations.clear();
      }
      requireValue(!cleanupFailure && result?.cleanupComplete && adapterTasks.size === 0, 'SCRIPT_CLEANUP');
      return { closed: true, cleanupComplete: true };
    })();
    return closing;
  }
  async function close(debugHook) {
    try { return success(await shutdown()); }
    catch (error) { debugHook?.onError?.(error); return failure(error); }
  }
  async function add(id, prepared, origin) {
    await commit(next => {
      requireValue(!next.installations.some(item => item?.id === id), 'STALE_SOURCE');
      next.installations.push({ id, name: prepared.name, origin, enabled: false,
        current: prepared.version, previous: null, declared: null, check: null });
    });
    return { item: view(find(id)) };
  }
  const methods = {
    'music.script.sources.list': business([], undefined, () => {
      const value = catalog();
      return { revision: value.revision, items: value.installations.map(item => view(item, { isolateCorrupt: true })) };
    }),
    'music.script.sources.detail': business(['installationId'], undefined, params => {
      installation(params.installationId);
      return { item: view(find(params.installationId), { full: true }) };
    }),
    'music.script.sources.add': business(['installationId', 'sourceRevision'], undefined, (params, signal) => {
      installation(params.installationId); integer(params.sourceRevision, 1);
      return locked(params.installationId, signal, async scoped => {
        guard(scoped);
        requireValue(!catalog().installations.some(item => item?.id === params.installationId), 'STALE_SOURCE');
        const input = sources.take(params.installationId, params.sourceRevision);
        const bytes = Buffer.from(input.source);
        let prepared, published = false;
        try {
          prepared = prepare(bytes, input.basename, input.contentHash);
          const result = await add(params.installationId, prepared, { kind: 'file' });
          published = true;
          return result;
        } finally {
          sources.finish(input, published);
          if (prepared) releaseSource(prepared.version);
        }
      });
    }),
    'music.script.sources.download': business(['url'], undefined, async (params, signal) => {
      const url = sourceURL(params.url), id = randomUUID();
      const bytes = await fetchSource(url, signal, id);
      let prepared;
      try {
        guard(signal);
        prepared = prepare(bytes, sourceBasename(url));
        return await add(id, prepared, { kind: 'network', url });
      } finally { if (prepared) releaseSource(prepared.version); }
    }),
    'music.script.sources.edit': business(['installationId', 'name', 'url'], ['installationId'], (params, signal) => {
      installation(params.installationId);
      requireValue(params.name !== undefined || params.url !== undefined);
      if (params.name !== undefined) requireValue(plainText(params.name, undefined, false) && params.name.trim().length > 0);
      const url = params.url === undefined ? undefined : sourceURL(params.url);
      requireValue(url === undefined || find(params.installationId).origin.kind === 'network');
      return locked(params.installationId, signal, async () => {
        await edit(params.installationId, target => {
          if (params.name !== undefined) target.name = params.name.trim();
          if (url !== undefined) target.origin = { kind: 'network', url };
        });
        return { item: view(find(params.installationId), { full: true }) };
      });
    }),
    'music.script.sources.update': business(['installationId', 'sourceRevision'], ['installationId'], (params, signal) => {
      installation(params.installationId);
      const admitted = find(params.installationId);
      requireValue(['file', 'network'].includes(admitted.origin?.kind), 'CORRUPT_STORAGE');
      if (admitted.origin.kind === 'file') {
        integer(params.sourceRevision, 1);
        sources.peek(params.installationId, params.sourceRevision);
      }
      interrupt(params.installationId);
      return locked(params.installationId, signal, async scoped => {
        const item = find(params.installationId);
        requireValue(validOrigin(item.origin), 'CORRUPT_STORAGE');
        let input, prepared, published = false;
        try {
          let bytes;
          if (item.origin.kind === 'network') bytes = await fetchSource(item.origin.url, scoped, item.id);
          else {
            input = sources.take(item.id, params.sourceRevision);
            bytes = Buffer.from(input.source);
          }
          guard(scoped);
          prepared = prepare(bytes, input?.basename ?? sourceBasename(item.origin.url), input?.contentHash);
          const result = await replace(item.id, prepared.version, item.current, scoped);
          published = true;
          return result;
        } finally {
          if (input) sources.finish(input, published);
          if (prepared) releaseSource(prepared.version);
        }
      });
    }),
    'music.script.sources.rollback': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      const item = find(params.installationId);
      requireValue(item.previous, 'ROLLBACK_UNAVAILABLE');
      requireValue(validVersion(item.previous), 'CORRUPT_STORAGE');
      interrupt(params.installationId);
      return locked(params.installationId, signal, scoped => {
        const item = find(params.installationId);
        requireValue(item.previous, 'ROLLBACK_UNAVAILABLE');
        return replace(item.id, item.previous, item.current, scoped);
      });
    }),
    'music.script.sources.enable': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId); find(params.installationId);
      return locked(params.installationId, signal, async scoped => {
        const item = find(params.installationId);
        const record = await ensureLoaded(item, scoped, true);
        try {
          guard(scoped);
          await edit(item.id, target => {
            const declared = declaredOf(record);
            if (target.enabled && sameDeclaration(target.declared, declared)) return false;
            target.enabled = true; target.declared = declared;
          });
          observations.set(item.id, host.observe(item.id));
          temporaryUse.delete(item.id);
        } catch (error) {
          if (!item.enabled) {
            observations.delete(item.id);
            await retire(item.id);
            cleanup(item.id);
          }
          throw error;
        }
        return { item: view(find(item.id)) };
      });
    }),
    'music.script.sources.disable': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      const admitted = find(params.installationId);
      interrupt(params.installationId);
      if (admitted.enabled) failures.set(admitted.id, { contentHash: admitted.current.contentHash, code: 'SOURCE_DISABLED' });
      const retiring = retire(params.installationId);
      retiring.catch(() => {});
      return locked(params.installationId, signal, async () => {
        const item = find(params.installationId);
        sources.remove(item.id); temporaryUse.delete(item.id); observations.delete(item.id);
        let cleanupFailure;
        try { await retiring; await retire(item.id); } catch (error) { cleanupFailure = error; }
        await edit(item.id, target => { if (!target.enabled) return false; target.enabled = false; });
        requireValue(!cleanupFailure && !live(item.id), 'SCRIPT_CLEANUP');
        cleanup(item.id);
        failures.delete(item.id);
        return { item: view(find(item.id)) };
      });
    }),
    'music.script.sources.remove': business(['installationId'], undefined, (params, signal) => {
      installation(params.installationId);
      const admitted = find(params.installationId);
      interrupt(params.installationId);
      if (admitted.enabled) failures.set(admitted.id, { contentHash: admitted.current.contentHash, code: 'SOURCE_DISABLED' });
      const retiring = retire(params.installationId);
      retiring.catch(() => {});
      return locked(params.installationId, signal, async () => {
        const item = find(params.installationId);
        sources.remove(item.id); temporaryUse.delete(item.id); observations.delete(item.id);
        let cleanupFailure;
        try { await retiring; await retire(item.id); } catch (error) { cleanupFailure = error; }
        await commit(next => { next.installations = next.installations.filter(entry => entry?.id !== item.id); });
        failures.delete(item.id); collect();
        requireValue(!cleanupFailure && !live(item.id), 'SCRIPT_CLEANUP');
        cleanup(item.id);
        return { removed: true };
      });
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
        const musicInfo = lxInfo(lxValue(params.musicInfo));
        return run(params.installationId, params.platform, 'musicUrl',
          { type: params.quality, musicInfo }, signal, (value, record) => ({
            installationId: params.installationId, generation: record.generation, contentHash: record.contentHash,
            platform: params.platform, quality: params.quality,
            ...mediaCandidate(value, expected, musicInfo.duration),
          }));
      }),
    'music.script.invoke': business(['installationId', 'siteId', 'operation', 'payload'], undefined,
      (params, signal) => {
        installation(params.installationId);
        requireValue(typeof params.siteId === 'string' && sitePattern.test(params.siteId) &&
          typeof params.operation === 'string' && sitePattern.test(params.operation) && object(params.payload));
        const payload = lxValue(params.payload);
        if (object(payload.musicInfo)) payload.musicInfo = lxInfo(payload.musicInfo);
        return run(params.installationId, params.siteId, params.operation, payload, signal, value => value);
      }),
    'music.script.close': async (params = {}, _context, debugHook) => {
      try { fields(params, []); return await close(debugHook); }
      catch (error) { debugHook?.onError?.(error); return failure(error); }
    },
  };
  if (debug) {
    for (const [name, handler] of Object.entries(methods)) {
      const op = name.slice('music.script.'.length);
      const cat = op.startsWith('sources.') || op === 'close' ? 'runtime-node' : 'music-playback';
      const sub = `music.script.${op}`;
      methods[name] = async (parameters = {}, context = {}) => {
        const startedAt = performance.now();
        let rawError = null;
        debug.emit({ level: 'debug', cat, sub, event: 'script.call.start', data: { operation: op, input: parameters } });
        const reply = await handler(parameters, context, { onError: err => { rawError = err; } });
        debug.emit({
          level: reply?.ok === false ? 'warn' : 'debug',
          cat,
          sub,
          event: reply?.ok === false ? 'script.call.error' : 'script.call.end',
          durationMs: performance.now() - startedAt,
          data: reply?.ok === false
            ? { operation: op, input: parameters, code: reply.code }
            : { operation: op, result: reply?.value },
          error: reply?.ok === false ? rawError : null,
        });
        return reply;
      };
    }
  }
  return {
    id: 'music',
    ready,
    version: contracts.music.version,
    contract: musicScriptContract,
    diagnostics: () => ({
      closed,
      installations: cached?.installations?.length ?? 0,
      activeOperations: active.size,
      pendingAdapterTasks: adapterTasks.size,
      host: host.stats(),
    }),
    methods,
    upload,
    close,
  };
}
