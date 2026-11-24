import { randomUUID } from 'node:crypto';
import { decodePanResource } from '@meowfilm/script-host/cat-output';
import { normalizeNFKC } from '@meowfilm/script-host/unicode';
import { mediaVariants, normalizeDetail, stableFileIdentity } from './recognition.js';
import { createRecognitionRules } from './rules.js';
import { createMetadataCache } from './metadata-cache.js';
import { metadataTTL } from './metadata-policy.js';
import { tmdbQuery } from './metadata-query.js';
import { mapWithConcurrency } from './concurrency.js';
import { checkSource, clone, fail, identity, isIdentifier, object, parseSourceValue, requireValue,
  sourceRevision, text } from './identity.js';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;
const STABLE = 14 * DAY;
const ADDRESS = 3 * 60_000;
const MAX_TMDB_SEARCH_PAGES = 5;
const MAX_METADATA_DETAIL_CONCURRENCY = 4;
const providers = ['baidu', 'quark', 'uc', 'tianyi', 'yidong'];
const prefixes = { cat: 'cat.script.', source: 'source.runtime.', pan: 'pan.',
  tmdb: 'tmdb.data.', douban: 'douban.data.' };
const errorCodes = new Set([
  'INVALID_REQUEST', 'UPSTREAM_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'NETWORK_ERROR',
  'TIMEOUT', 'RESOURCE_LIMIT', 'CANCELLED', 'CLOSED', 'STALE_SOURCE', 'STALE_ACCOUNT', 'AUTH_REQUIRED', 'AUTH_FAILED',
  'NOT_CONFIGURED', 'NOT_FOUND', 'LOGIN_EXPIRED', 'CHALLENGE_REQUIRED', 'WEB_INTERACTION_REQUIRED',
  'CAPABILITY_UNAVAILABLE', 'UNSUPPORTED_OPERATION', 'PAN_FAILURE', 'PAN_INTERCEPTION_UNSUPPORTED', 'SOURCE_FAILURE', 'CAT_FAILURE',
  'SCRIPT_FAILURE', 'SCRIPT_PROTOCOL_ERROR', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'MEDIA_UNAVAILABLE', 'INVALID_RULES', 'CACHE_LOAD_FAILED', 'STALE_METADATA',
  'PERMISSION_DENIED', 'SOURCE_NOT_FOUND', 'SOURCE_DISABLED', 'COMMIT_UNKNOWN',
]);
const sourceErrorCodes = {
  INVALID_ARGUMENT: 'INVALID_REQUEST', HOST_CLOSED: 'CLOSED', SERVICE_CLOSED: 'CLOSED',
  STALE_SESSION: 'STALE_SOURCE', DISABLED: 'SOURCE_DISABLED', STARTING: 'CAPABILITY_UNAVAILABLE',
  LIMIT: 'RESOURCE_LIMIT', UPSTREAM_HTTP_ERROR: 'UPSTREAM_HTTP', HTTP_ERROR: 'UPSTREAM_HTTP',
  INVALID_JSON: 'UPSTREAM_FORMAT', INVALID_RESPONSE: 'UPSTREAM_FORMAT',
  SCRIPT_EXCEPTION: 'SCRIPT_FAILURE', SERVICE_ERROR: 'CAT_FAILURE',
  SOURCE_INTEGRITY: 'SOURCE_FAILURE', SOURCE_ENCODING: 'SOURCE_FAILURE',
  SOURCE_CLEANUP: 'SOURCE_FAILURE', SCRIPT_CLEANUP: 'SCRIPT_FAILURE',
  UNSUPPORTED_PROTOCOL: 'UNSUPPORTED_OPERATION', UNSUPPORTED_ACTION: 'UNSUPPORTED_OPERATION',
  UNSUPPORTED_RULE: 'UNSUPPORTED_OPERATION', UNSUPPORTED_DEPENDENCY: 'CAPABILITY_UNAVAILABLE',
  UNSUPPORTED_SCRIPT_ASYNC: 'SCRIPT_PROTOCOL_ERROR', UNSUPPORTED_OPTION: 'UNSUPPORTED_OPERATION',
  UNSUPPORTED_CRYPTO: 'CAPABILITY_UNAVAILABLE', VIRTUAL_ROUTE_NOT_FOUND: 'NOT_FOUND',
  UNMAPPED_LOCAL_URL: 'UPSTREAM_FORMAT', STORAGE_CONFLICT: 'STORAGE_ERROR',
  ALREADY_EXISTS: 'CAT_FAILURE', SERVICE_EXISTS: 'CAT_FAILURE', ROLLBACK_UNAVAILABLE: 'UNSUPPORTED_OPERATION',
};
const safeError = error => fail(error?.name === 'AbortError' ? 'CANCELLED'
  : errorCodes.has(error?.code) ? error.code
    : Object.hasOwn(sourceErrorCodes, error?.code) ? sourceErrorCodes[error.code] : 'UPSTREAM_ERROR');
const accountDependency = (provider, revision) => ({ kind: 'account', domain: provider,
  ...(revision === undefined ? {} : { revision }) });

function sourceDependencies(source) {
  if (source.engine === 'pan') return [{ domain: source.key }];
  return [{ kind: 'source', domain: source.key, engine: source.engine,
    installationId: source.installationId, siteId: source.siteId,
    generation: source.generation, ...(source.revision === undefined ? {} : { revision: source.revision }),
    sourceRevision: sourceRevision(source) }];
}
function scriptDependencies(source) {
  return [...sourceDependencies(source), ...(source.engine === 'cat' ? [{ kind: 'script-account',
    installationId: source.installationId, generation: source.generation }] : [])];
}
function playbackSource(value) {
  if (value?.engine !== 'pan') return checkSource(value);
  requireValue(object(value) && text(value.key) && isIdentifier(value.detailId));
  return clone(value);
}
function scopeOf(source) {
  return { sourceKey: source.key, sourceRevision: sourceRevision(source),
    ...(source.engine === 'pan' ? {} : {
      sourceEngine: source.engine, sourceInstallationId: source.installationId, sourceGeneration: source.generation,
    }) };
}
function searchPage(raw, page) {
  const rows = Array.isArray(raw.list) ? raw.list : Array.isArray(raw.items) ? raw.items : null;
  requireValue(rows, 'UPSTREAM_FORMAT');
  const items = rows.filter(object);
  requireValue(!rows.length || items.length, 'UPSTREAM_FORMAT');
  const pageCount = [raw.pagecount, raw.pageCount]
    .filter(value => typeof value === 'number' || typeof value === 'string')
    .map(Number).find(value => Number.isSafeInteger(value) && value > 0);
  const explicitPageCount = pageCount !== undefined;
  return { items, page, hasMore: explicitPageCount ? page < pageCount : items.length > 0,
    explicitPageCount, ...(explicitPageCount ? { pageCount } : {}),
    raw: items.length === rows.length ? raw : { ...raw, [Array.isArray(raw.list) ? 'list' : 'items']: items } };
}
function detailValue(raw, source, detailId) {
  try {
    const value = normalizeDetail(raw, { source: { ...source, detailId } });
    requireValue(value.detailId === detailId, 'UPSTREAM_FORMAT');
    return value;
  }
  catch { throw fail('UPSTREAM_FORMAT'); }
}
function metadataValue(provider, path, value, completeness) {
  requireValue(object(value) || Array.isArray(value), 'UPSTREAM_FORMAT');
  if (provider === 'douban' && path === 'search') {
    requireValue(Array.isArray(value.subjects?.items), 'UPSTREAM_FORMAT');
    const items = value.subjects.items.filter(row =>
      object(row) && object(row.target) && isIdentifier(row.target_id ?? row.target.id));
    requireValue(!value.subjects.items.length || items.length, 'UPSTREAM_FORMAT');
    return { ...value, subjects: { ...value.subjects, items } };
  } else if (provider === 'tmdb' && path === '/3/search/multi') {
    requireValue(Array.isArray(value.results), 'UPSTREAM_FORMAT');
    const results = value.results.filter(object);
    requireValue(!value.results.length || results.length, 'UPSTREAM_FORMAT');
    return { ...value, results };
  } else if (provider === 'tmdb' && /^\/3\/(?:search|discover|trending)\//.test(path)) {
    requireValue(Array.isArray(value.results), 'UPSTREAM_FORMAT');
    const results = value.results.filter(row => object(row) && isIdentifier(row.id));
    requireValue(!value.results.length || results.length, 'UPSTREAM_FORMAT');
    return { ...value, results };
  } else {
    const season = provider === 'tmdb' && /^\/3\/tv\/\d+\/season\/(\d+)$/.exec(path);
    const detail = /^(?:\/3|\/rexxar\/api\/v2)\/(movie|tv)\/(\d+)$/.exec(path);
    if (season) {
      requireValue(value.season_number === Number(season[1]) && (value.episodes === undefined ||
        Array.isArray(value.episodes) && value.episodes.every(row => object(row) &&
          Number.isSafeInteger(row.episode_number) && row.episode_number >= 0)), 'UPSTREAM_FORMAT');
    } else if (detail) {
      requireValue(String(value.id) === detail[2] && text(value.title ?? value.name), 'UPSTREAM_FORMAT');
      if (completeness === 'complete') {
        requireValue(Array.isArray(value.genres) && (provider === 'douban' ? typeof value.intro === 'string'
          : detail[1] === 'tv' ? Array.isArray(value.seasons) : Object.hasOwn(value, 'runtime')), 'UPSTREAM_FORMAT');
      }
    } else requireValue(Array.isArray(value) || Object.keys(value).length > 0, 'UPSTREAM_FORMAT');
  }
  return value;
}
async function metadataSeason(record, recognition, signal) {
  return (await recognition.metadata(record, 'tv', { signal })).season ?? undefined;
}
function seasonEvidence(record, metadataId, season) {
  const count = typeof record.episodes_count === 'string' && /^\d+$/.test(record.episodes_count)
    ? Number(record.episodes_count) : record.episodes_count;
  if (season === undefined || !Number.isSafeInteger(count) || count < 0) return undefined;
  return { season, episodeCount: count, metadataId, metadata: record };
}
async function doubanSeason(record, metadataId, recognition, signal) {
  return seasonEvidence(record, metadataId, await metadataSeason(record, recognition, signal));
}
function mediaValue(value) {
  requireValue(object(value), 'UPSTREAM_FORMAT');
  let url = value.url ?? value.playUrl ?? value.play_url;
  if (Array.isArray(url)) url = url.find(item => typeof item === 'string' && /^https?:\/\//i.test(item));
  requireValue(typeof url === 'string' && /^https?:\/\//i.test(url) &&
    !/[\u0000-\u0020\u007f]/.test(url), 'MEDIA_UNAVAILABLE');
  let parsed;
  try { parsed = new URL(url); } catch { throw fail('MEDIA_UNAVAILABLE'); }
  const headers = value.headers ?? value.header ?? {};
  requireValue(object(headers) && Object.values(headers).every(item => typeof item === 'string'),
    'UPSTREAM_FORMAT');
  return { ...value, url: parsed.href, headers, format: value.format ?? value.type };
}
function addressTTL(value, at) {
  if (value.expiresAt === undefined) return ADDRESS;
  requireValue(Number.isFinite(value.expiresAt) && value.expiresAt > at, 'MEDIA_UNAVAILABLE');
  return Math.min(ADDRESS, value.expiresAt - at);
}
function fileValue(file, provider, revision, kind) {
  requireValue(object(file) && file.provider === provider && file.accountRevision === revision &&
    ['share', 'stored'].includes(file.kind) && (kind === undefined || file.kind === kind) &&
    ['fileId', 'coID', 'contentId'].some(key => text(file[key])), 'UPSTREAM_FORMAT');
  return file;
}

// Unicode 15.0 Cf + punctuation/whitespace, without \p{...} for ICU-less NodeMobile.
const titleKeyStrip = /[\u00ad\u0600-\u0605\u061c\u06dd\u070f\u0890-\u0891\u08e2\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb\u{110bd}\u{110cd}\u{13430}-\u{1343f}\u{1bca0}-\u{1bca3}\u{1d173}-\u{1d17a}\u{e0001}\u{e0020}-\u{e007f}\s.\-_,，:：;；!！?？·•/\\|《》「」『』【】()（）]+/gu;

function normalizeTitleKey(value, kind) {
  let normalized = normalizeNFKC(text(value)).replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '').trim();
  if (kind === 'tv') {
    normalized = normalized
      .replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
      .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ');
  }
  return normalized.toLowerCase().replace(titleKeyStrip, '');
}
function recordTitleKeys(record, kind) {
  const keys = [];
  for (const field of ['title', 'original_title', 'name', 'original_name']) {
    const key = normalizeTitleKey(record?.[field], kind);
    if (key) keys.push(key);
  }
  return keys;
}
function tmdbRecordYear(record, kind) {
  const field = kind === 'movie' ? 'release_date' : 'first_air_date';
  return typeof record?.[field] === 'string' && /^\d{4}/.test(record[field])
    ? Number(record[field].slice(0, 4)) : null;
}

/** All values, timestamps and completeness live in the one injected cache. */
export function createPlaybackData({ modules, cache, dataRoot, debug }) {
  requireValue(modules instanceof Map && cache);
  const lifetime = new AbortController();
  const pending = new Set();
  const cacheOwner = { domain: 'film-data', owner: randomUUID() };
  const metadataCache = createMetadataCache({ dataRoot, cache, owner: cacheOwner, normalizeError: safeError,
    decodeValue: metadataValue, debug });
  let closed = false, closing;

  function open(signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  function time() {
    return Date.now();
  }
  function start(dependencies, run, parent) {
    open(parent);
    const controller = new AbortController();
    const signal = AbortSignal.any([lifetime.signal, controller.signal, ...(parent ? [parent] : [])]);
    const work = { controller, signal, dependencies };
    pending.add(work);
    work.done = Promise.resolve().then(() => { open(signal); return run(signal); })
      .then(value => { open(signal); return value; })
      .catch(error => { throw safeError(error); })
      .finally(() => pending.delete(work));
    work.done.catch(() => {});
    return work;
  }
  function wait(work, signal) {
    if (signal?.aborted || work.signal.aborted) return Promise.reject(fail('CANCELLED'));
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (settle, value) => {
        if (settled) return;
        settled = true;
        signal?.removeEventListener('abort', cancel);
        work.signal.removeEventListener('abort', cancel);
        settle(value);
      };
      const cancel = () => finish(reject, fail(closed ? 'CLOSED' : 'CANCELLED'));
      signal?.addEventListener('abort', cancel, { once: true });
      work.signal.addEventListener('abort', cancel, { once: true });
      work.done.then(value => finish(resolve, value), error => finish(reject, error));
    });
  }
  function execute(dependencies, { signal, canSchedule }, run) {
    requireValue(canSchedule === undefined || typeof canSchedule === 'function');
    open(signal);
    return wait(start(dependencies, ownedSignal => {
      // Stop new steps without aborting an already accepted producer.
      const checkSchedule = () => {
        open(ownedSignal);
        open(signal);
        requireValue(canSchedule === undefined || canSchedule(), 'CANCELLED');
      };
      checkSchedule();
      return run(ownedSignal, checkSchedule);
    }), signal);
  }
  function call(id, operation, params, signal, dependencies, checkSchedule, context = {}) {
    checkSchedule?.();
    return start(dependencies, async ownedSignal => {
      const handler = modules.get(id)?.methods?.[prefixes[id] + operation];
      requireValue(typeof handler === 'function', 'CAPABILITY_UNAVAILABLE');
      const reply = await handler(params, { ...context, signal: ownedSignal });
      open(ownedSignal);
      requireValue(object(reply) && typeof reply.ok === 'boolean', 'UPSTREAM_FORMAT');
      if (!reply.ok) throw safeError(reply);
      requireValue(Object.hasOwn(reply, 'value'), 'UPSTREAM_FORMAT');
      return reply.value;
    }, signal).done;
  }
  const cacheKey = request => identity('film-data', request);
  const record = (request, value, completeness, fetchedAt, expiresAt) =>
    ({ ...request, value, completeness, fetchedAt, expiresAt });
  async function load(request, producer, { ttl, completeness = 'complete', dependencies, signal, checkSchedule, refresh = false }) {
    open(signal);
    checkSchedule?.();
    return cache.load(cacheKey(request), async producerSignal => {
      const value = await producer(producerSignal);
      const at = time();
      const duration = typeof ttl === 'function' ? ttl(value, at, completeness) : ttl;
      return record(request, value, completeness, at, at + duration);
    }, { ttl: (entry, at) => Math.max(0, entry.expiresAt - at), completeness, signal,
      dependencies: [cacheOwner, ...dependencies], refresh });
  }
  const sourceRequest = (kind, source, fields) => ({ kind,
    source: { key: source.key, engine: source.engine, installationId: source.installationId,
      siteId: source.siteId, generation: source.generation,
      ...(source.revision === undefined ? {} : { revision: source.revision }) }, ...fields });
  async function liveSource(source, signal, checkSchedule) {
    const status = await call(source.engine, 'status', {}, signal, sourceDependencies(source), checkSchedule);
    requireValue(Array.isArray(status?.instances), 'UPSTREAM_FORMAT');
    const instance = status.instances.find(row => row.installationId === source.installationId);
    requireValue(instance && instance.generation === source.generation &&
      !['closed', 'closing', 'stopped', 'unloaded', 'failed', 'error'].includes(instance.state), 'STALE_SOURCE');
    if (source.engine === 'cat') {
      requireValue(Array.isArray(instance.sites) &&
        instance.sites.some(site => site.key === source.siteId), 'STALE_SOURCE');
    } else {
      requireValue(instance.revision === source.revision, 'STALE_SOURCE');
      requireValue(instance.protocol === 'cat' && source.siteId === 'source', 'UNSUPPORTED_OPERATION');
    }
  }
  async function invoke(source, action, payload, signal, checkSchedule, { discoverVariants = false } = {}) {
    const value = await call(source.engine, 'invoke', source.engine === 'cat' ? {
      installationId: source.installationId, generation: source.generation,
      siteId: source.siteId, operation: action, payload,
    } : {
      installationId: source.installationId, revision: source.revision,
      generation: source.generation, action, payload,
    }, signal, scriptDependencies(source), checkSchedule,
    source.engine === 'cat' && action === 'play' && !discoverVariants ? { mediaPurpose: 'receipt' } : undefined);
    return parseSourceValue(value);
  }
  async function catalog(source, action, payload = {}, { signal, canSchedule, refresh = false } = {}) {
    source = checkSource(source);
    requireValue(['home', 'category'].includes(action) && object(payload));
    const deps = scriptDependencies(source);
    return execute(deps, { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      await liveSource(source, ownedSignal, checkSchedule);
      const entry = await load(sourceRequest(action, source, { payload: clone(payload) }),
        producerSignal => invoke(source, action, payload, producerSignal),
        { ttl: HOUR, dependencies: deps, signal: ownedSignal, checkSchedule, refresh });
      return clone(entry.value);
    });
  }
  async function search(source, query, page = 1, { signal, canSchedule, refresh = false } = {}) {
    source = checkSource(source);
    requireValue(text(query) && Number.isSafeInteger(page) && page > 0);
    const deps = scriptDependencies(source);
    return execute(deps, { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      await liveSource(source, ownedSignal, checkSchedule);
      const entry = await load(sourceRequest('search', source, { query, page }), async producerSignal => {
        const raw = await invoke(source, 'search', {
          ...(source.engine === 'cat' ? { wd: query } : { keyword: query }), quick: false, page,
        }, producerSignal);
        searchPage(raw, page);
        return raw;
      }, { ttl: HOUR, dependencies: deps, signal: ownedSignal, checkSchedule, refresh });
      return clone(searchPage(entry.value, page));
    });
  }
  async function detailEntry(source, detailId, { signal, canSchedule, refresh = false } = {}) {
    source = checkSource(source);
    requireValue(isIdentifier(detailId));
    const deps = scriptDependencies(source);
    return execute(deps, { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      await liveSource(source, ownedSignal, checkSchedule);
      const entry = await load(sourceRequest('detail', source, { detailId }), async producerSignal => {
        const raw = await invoke(source, 'detail', { id: detailId }, producerSignal);
        detailValue(raw, source, detailId);
        return raw;
      }, { ttl: HOUR, dependencies: deps, signal: ownedSignal, checkSchedule, refresh });
      return entry;
    });
  }
  async function detail(source, detailId, options = {}) {
    return detailValue(clone((await detailEntry(source, detailId, options)).value), source, detailId);
  }
  async function readAccounts(signal, deps = providers.map(provider => accountDependency(provider)), checkSchedule) {
    const value = await call('pan', 'settings.get', {}, signal, deps, checkSchedule);
    requireValue(Array.isArray(value?.accounts), 'UPSTREAM_FORMAT');
    return value.accounts;
  }
  function selectedAccount(rows, provider, missing = 'NOT_CONFIGURED') {
    const matches = rows.filter(row => row?.provider === provider);
    requireValue(matches.length, missing);
    requireValue(matches.length === 1, 'UPSTREAM_FORMAT');
    const account = matches[0];
    if (account.errorCode !== undefined) {
      requireValue(account.errorCode === 'CORRUPT_STORAGE', 'UPSTREAM_FORMAT');
      throw fail(account.errorCode);
    }
    requireValue(Number.isSafeInteger(account.revision) && account.revision >= 0 &&
      typeof account.configured === 'boolean', 'UPSTREAM_FORMAT');
    return account;
  }
  async function expand(source, line, { signal, canSchedule, refresh = false } = {}) {
    source = playbackSource(source);
    requireValue(object(line) && isIdentifier(line.id) && Array.isArray(line.episodes));
    const shares = new Map();
    const references = line.episodes.map(episode => {
      requireValue(object(episode));
      if (episode.file) return undefined;
      let share = episode.share;
      const value = episode.playId ?? episode.id ?? episode.url;
      if (!share && typeof value === 'string' && value.startsWith('meow.pan.v1.')) {
        try { share = decodePanResource(value); } catch { throw fail('UPSTREAM_FORMAT'); }
      }
      if (!share) return undefined;
      requireValue(object(share) && providers.includes(share.provider) && text(share.url));
      if (share.installationId !== undefined || share.siteId !== undefined) {
        requireValue(share.installationId === source.installationId && share.siteId === source.siteId, 'STALE_SOURCE');
      }
      const key = identity(share.provider, share.url, share.password ?? '');
      shares.set(key, share);
      return key;
    });
    const deps = [...sourceDependencies(source),
      ...[...shares.values()].map(share => accountDependency(share.provider))];
    return execute(deps, { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      if (source.engine !== 'pan') await liveSource(source, ownedSignal, checkSchedule);
      if (!shares.size) return line;
      const rows = await readAccounts(ownedSignal, deps, checkSchedule);
      const lists = new Map();
      for (const [key, share] of shares) {
        open(ownedSignal);
        const account = selectedAccount(rows, share.provider);
        const dependency = [...sourceDependencies(source), accountDependency(share.provider, account.revision)];
        const request = { kind: 'pan-list', source: scopeOf(source), provider: share.provider,
          revision: account.revision, share: { url: share.url, password: share.password ?? '' } };
        const entry = await load(request, async producerSignal => {
          const value = await call('pan', 'list', { provider: share.provider, share: request.share },
            producerSignal, dependency);
          requireValue(Array.isArray(value?.files) && value.files.every(item => object(item) &&
            typeof item.name === 'string' && typeof item.path === 'string'), 'UPSTREAM_FORMAT');
          for (const item of value.files) fileValue(item.file, share.provider, account.revision);
          return value;
        }, { ttl: 5 * 60_000, dependencies: dependency, signal: ownedSignal, checkSchedule, refresh });
        lists.set(key, entry.value.files);
      }
      const episodes = [];
      const expanded = new Set();
      for (let index = 0; index < line.episodes.length; index++) {
        const key = references[index];
        if (key === undefined) episodes.push({ ...line.episodes[index], index: episodes.length });
        else if (!expanded.has(key)) {
          expanded.add(key);
          for (const item of lists.get(key)) episodes.push({ ...clone(item),
            id: identity(source.key, line.id, item.file), provider: item.file.provider, index: episodes.length });
        }
      }
      return { ...line, episodes, expanded: true };
    });
  }
  function metadataRequest(provider, path, query, imageSize) {
    requireValue(['tmdb', 'douban'].includes(provider) && text(path) && object(query));
    requireValue(provider === 'tmdb' ? path.startsWith('/3/') :
      path === 'search' || path.startsWith('/rexxar/api/v2/'));
    requireValue(!/[?#\\\u0000-\u0020]/.test(path));
    if (provider === 'douban' && path === 'search') {
      requireValue(text(query.query));
      query = { query: query.query };
    }
    else requireValue(Object.values(query).every(value => {
      const scalar = item => typeof item === 'string' || typeof item === 'boolean' ||
        typeof item === 'number' && Number.isFinite(item);
      return provider === 'douban' && Array.isArray(value) ? value.every(scalar) : scalar(value);
    }));
    if (provider === 'tmdb' && imageSize !== undefined) requireValue(
      typeof imageSize === 'string' && /^(?:original|[wh][1-9]\d*)$/.test(imageSize));
    if (provider === 'tmdb' && query.append_to_response !== undefined)
      requireValue(typeof query.append_to_response === 'string');
    return { kind: 'metadata', provider, path, query: clone(query),
      ...(provider === 'tmdb' ? { imageSize: imageSize ?? 'original' } : {}) };
  }
  async function settings(provider, signal, checkSchedule) {
    const value = await call(provider, 'settings.get', {}, signal, [{ domain: provider }], checkSchedule);
    requireValue(object(value) && text(value.revision), 'UPSTREAM_FORMAT');
    return value;
  }
  function configuredMetadataRequest(request, configuration) {
    return { ...request,
      query: request.provider === 'tmdb' ? tmdbQuery(request.path, request.query, configuration) : request.query,
      settings: { revision: configuration.revision } };
  }
  async function getMetadata(request, { signal, completeness = 'complete', checkSchedule, refresh = false, requestedEpisode } = {}) {
    requireValue(['partial', 'complete'].includes(completeness));
    requireValue(typeof refresh === 'boolean' && (requestedEpisode === undefined ||
      Number.isSafeInteger(requestedEpisode) && requestedEpisode > 0));
    const configuration = await settings(request.provider, signal, checkSchedule);
    const keyed = configuredMetadataRequest(request, configuration);
    const deps = [{ kind: 'metadata', domain: request.provider, revision: configuration.revision }];
    checkSchedule?.();
    return metadataCache.get(keyed, async (selected, producerSignal) => {
      const { provider, path, query, imageSize } = selected;
      const raw = await call(provider, provider === 'douban' && path === 'search' ? 'search' : 'request',
        provider === 'douban' && path === 'search' ? query : {
          path, query, ...(imageSize === undefined ? {} : { imageSize }),
        }, producerSignal, deps);
      return metadataValue(provider, path, raw, 'partial');
    }, { completeness, signal, refresh, requestedEpisode, checkSchedule });
  }
  function rawMetadata(entry) {
    if (entry?.cache.status === 'stale') throw fail(entry.cache.error?.code ?? 'STALE_METADATA');
    return entry?.value ?? null;
  }
  async function metadataEntry(provider, path, query = {},
    { signal, canSchedule, completeness = 'complete', imageSize, refresh = false, requestedEpisode } = {}) {
    const request = metadataRequest(provider, path, query, imageSize);
    return execute([{ domain: provider }], { signal, canSchedule },
      (ownedSignal, checkSchedule) => getMetadata(request,
        { signal: ownedSignal, completeness, checkSchedule, refresh, requestedEpisode }));
  }
  async function metadata(provider, path, query = {}, options = {}) {
    return rawMetadata(await metadataEntry(provider, path, query, options));
  }
  function targetRequest(target) {
    requireValue(['movie', 'tv'].includes(target.kind) && /^[1-9]\d*$/.test(String(target.metadataId)));
    return metadataRequest(target.metadataProvider, target.metadataProvider === 'tmdb'
      ? `/3/${target.kind}/${target.metadataId}` : `/rexxar/api/v2/${target.kind}/${target.metadataId}`,
    target.metadataProvider === 'tmdb' ? { append_to_response: 'credits,similar,images' } : {});
  }
  async function targetMetadataEntry(target, { signal, canSchedule, rules, refresh = false } = {}) {
    if (!target.metadataProvider || ['source', 'group'].includes(target.kind)) { open(signal); return null; }
    const request = targetRequest(target);
    return execute([{ domain: target.metadataProvider }], { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      const entry = await getMetadata(request, { signal: ownedSignal, checkSchedule, refresh });
      const { value } = entry;
      if (target.metadataProvider === 'douban' && target.kind === 'tv') {
        checkSchedule();
        const season = await doubanSeason(value, target.metadataId, createRecognitionRules(rules), ownedSignal);
        if (season) return { ...entry, value: { ...value, doubanSeasons: [season] } };
      }
      return entry;
    });
  }
  async function tmdbTargetEntry(target, { metadata, rules, signal, canSchedule, refresh = false } = {}) {
    return execute([{ domain: 'tmdb' }], { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      const recognition = createRecognitionRules(rules);
      const seed = { ...metadata, ...target };
      const hint = await recognition.metadata(seed, target.kind, { signal: ownedSignal });
      const response = rawMetadata(await getMetadata(metadataRequest('tmdb', '/3/search/multi',
        { query: hint.query, page: 1 }, 'w500'), { signal: ownedSignal, checkSchedule, refresh }));
      const sameKind = response.results.filter(record => record?.media_type === target.kind);
      const candidates = sameKind.filter(record =>
        Number.isSafeInteger(record.id) && record.id > 0 && text(record.title ?? record.name));
      requireValue(!sameKind.length || candidates.length, 'UPSTREAM_FORMAT');
      const seedTitles = new Set([
        hint.query, hint.title, seed.title, seed.name, seed.original_title, seed.original_name,
        ...(Array.isArray(seed.aka) ? seed.aka : []),
      ].map(value => normalizeTitleKey(value, target.kind)).filter(Boolean));
      const titleMatched = candidates.filter(record =>
        recordTitleKeys(record, target.kind).some(key => seedTitles.has(key)));
      let selected = null;
      if (hint.year !== null) {
        selected = titleMatched.find(record => tmdbRecordYear(record, target.kind) === hint.year) ?? null;
        if (!selected && target.kind === 'tv' && hint.season > 1) {
          selected = titleMatched.find(record => {
            const year = tmdbRecordYear(record, target.kind);
            return year !== null && year <= hint.year;
          }) ?? null;
        }
      } else {
        selected = titleMatched[0] ?? null;
      }
      if (!selected) {
        debug?.emit({ level: 'info', cat: 'film-catalog', sub: 'film.metadata.match', event: 'tmdb.seed.unmatched',
          data: { targetId: target.id, kind: target.kind, query: hint.query, year: hint.year,
            candidateCount: candidates.length, titleMatchedCount: titleMatched.length } });
        return null;
      }
      const matchedYear = tmdbRecordYear(selected, target.kind);
      debug?.emit({ level: 'info', cat: 'film-catalog', sub: 'film.metadata.match', event: 'tmdb.seed.matched',
        data: { targetId: target.id, kind: target.kind, tmdbId: selected.id, year: matchedYear } });
      const resolved = { id: `tmdb:${target.kind}:${selected.id}`, kind: target.kind,
        title: selected.title ?? selected.name, metadataProvider: 'tmdb', metadataId: selected.id,
        ...(matchedYear === null && hint.year === null ? {} : { year: matchedYear ?? hint.year }) };
      const entry = await getMetadata(targetRequest(resolved), { signal: ownedSignal, checkSchedule, refresh });
      return { target: resolved, entry };
    });
  }
  async function metadataSourcesValue(target, rules, signal, checkSchedule, { doubanOnly = false } = {}) {
    if (!target.metadataProvider) return [];
    const primaryRequest = targetRequest(target);
    const primary = rawMetadata(await getMetadata(primaryRequest, { signal, checkSchedule }));
    metadataValue(target.metadataProvider, primaryRequest.path, primary, 'complete');
    const recognition = createRecognitionRules(rules);
    checkSchedule?.();
    const hint = await recognition.metadata(primary, target.kind, { signal });
    const { title, query } = hint;
    const primarySeason = target.kind === 'tv' && target.metadataProvider === 'douban' ? hint.season ?? undefined : undefined;
    const base = { ...target, ...primary, kind: target.kind, title: query, year: hint.year ?? target.year,
      metadataProvider: target.metadataProvider, metadataId: target.metadataId };
    delete base.season; delete base.seasonHint; delete base.season_number;
    delete base.episode; delete base.globalEpisode;
    const primaryRow = { provider: target.metadataProvider, metadataId: target.metadataId,
      kind: target.kind, title, metadata: primary, association: { kind: 'identity' } };
    const result = [primaryRow];
    const opposite = target.metadataProvider === 'tmdb' ? 'douban' : 'tmdb';
    const found = [];
    if (modules.has('douban') && (opposite === 'douban' || target.metadataProvider === 'douban' && target.kind === 'tv')) {
      const response = rawMetadata(await getMetadata(metadataRequest('douban', 'search', { query }), { signal, checkSchedule }));
      for (const row of response.subjects.items) found.push({ ...row.target,
        id: row.target_id ?? row.target.id, kind: row.target_type ?? row.target.type,
        metadataProvider: 'douban', metadataId: row.target_id ?? row.target.id });
    }
    if (!doubanOnly && opposite === 'tmdb' && modules.has('tmdb')) {
      let page = 1, pages = 1;
      const seen = new Set();
      const baseTitles = new Set([
        query, title, primary.title, primary.name, primary.original_title, primary.original_name,
        ...(Array.isArray(primary.aka) ? primary.aka : []),
      ].map(value => normalizeTitleKey(value, target.kind)).filter(Boolean));
      const strongMatchOnPage = rows => rows.some(row => {
        if (!recordTitleKeys(row, target.kind).some(key => baseTitles.has(key))) return false;
        if (base.year === undefined) return true;
        return tmdbRecordYear(row, target.kind) === base.year;
      });
      do {
        const response = rawMetadata(await getMetadata(metadataRequest('tmdb', `/3/search/${target.kind}`,
          { query, page }), { signal, checkSchedule }));
        pages = Number(response.total_pages ?? 1);
        requireValue(Number.isSafeInteger(pages) && pages >= 0, 'UPSTREAM_FORMAT');
        const key = identity(response.results);
        requireValue(page >= pages || response.results.length > 0 && !seen.has(key), 'UPSTREAM_FORMAT');
        seen.add(key);
        for (const row of response.results) found.push({ ...row, title: row.title ?? row.name,
          kind: target.kind, metadataProvider: 'tmdb', metadataId: row.id });
        if (strongMatchOnPage(response.results)) {
          debug?.emit({ level: 'debug', cat: 'film-catalog', sub: 'film.metadata.match', event: 'tmdb.search.early_stop',
            data: { targetId: target.id, page, totalPages: pages } });
          break;
        }
        page++;
      } while (page <= Math.min(pages, MAX_TMDB_SEARCH_PAGES));
    }
    const candidates = new Map();
    const eligible = found.filter(row => row.kind === target.kind && isIdentifier(row.metadataId));
    checkSchedule?.();
    const hints = await recognition.batch(eligible.map(record => ({
      kind: 'metadata', record, contentKind: target.kind,
    })), { signal });
    const matches = [];
    for (const [index, row] of eligible.entries()) {
      const actualSeason = target.kind === 'tv' ? hints[index].season ?? undefined : undefined;
      const expected = { ...base };
      const sameIdentity = row.metadataProvider === target.metadataProvider &&
        String(row.metadataId) === String(target.metadataId);
      // A numbered season can premiere in a later year. It still requires an
      // exact canonical title/alias and one unambiguous identity per season.
      if (row.metadataProvider === 'douban' && actualSeason !== undefined) {
        if (!sameIdentity && target.metadataProvider === 'douban') {
          delete expected.metadataProvider; delete expected.metadataId;
        }
        if (primarySeason > 1 ? actualSeason !== primarySeason : actualSeason > 1) {
          for (const key of ['year', 'release_date', 'first_air_date', 'pubdate']) delete expected[key];
        }
      }
      const knownYear = hints[index].year !== null && base.year !== undefined;
      if (!sameIdentity && !knownYear && actualSeason === undefined) continue;
      matches.push({ row, actualSeason, sameIdentity, expected });
    }
    checkSchedule?.();
    const matched = await recognition.batch(matches.map(({ row, expected }) => ({
      kind: 'match', record: row, target: expected,
    })), { signal });
    for (const [index, { row, actualSeason, sameIdentity }] of matches.entries()) {
      if (!sameIdentity && !matched[index]) continue;
      const group = row.metadataProvider === 'douban' && actualSeason !== undefined
        ? identity(row.metadataProvider, actualSeason) : identity(row.metadataProvider, 'identity');
      if (!candidates.has(group)) candidates.set(group, new Map());
      candidates.get(group).set(String(row.metadataId), { row, actualSeason });
    }
    const doubanSeasons = [];
    if (target.metadataProvider === 'douban' && target.kind === 'tv') {
      const row = seasonEvidence(primary, target.metadataId, primarySeason);
      if (row) doubanSeasons.push(row);
    }
    const uniqueCandidates = [...candidates.values()]
      .filter(group => group.size === 1)
      .map(group => group.values().next().value);
    const confirmed = await mapWithConcurrency(uniqueCandidates, MAX_METADATA_DETAIL_CONCURRENCY,
      async ({ row, actualSeason }) => {
        const provider = row.metadataProvider;
        const existing = result.find(item => item.provider === provider && String(item.metadataId) === String(row.metadataId));
        const value = existing?.metadata ?? rawMetadata(await getMetadata(targetRequest({ ...target,
          metadataProvider: provider, metadataId: row.metadataId }), { signal, checkSchedule }));
        const verified = { ...value, kind: target.kind, title: value.title ?? value.name,
          metadataProvider: provider, metadataId: value.id };
        return { row, actualSeason, provider, existing, value, verified };
      }, { shouldContinue: () => !signal?.aborted });
    checkSchedule?.();
    const verifiedRows = await recognition.batch(confirmed.flatMap(({ row, verified }) => [
      { kind: 'match', record: verified, target: { ...row, kind: target.kind } },
      { kind: 'metadata', record: verified, contentKind: target.kind },
    ]), { signal });
    for (const [index, { row, actualSeason, provider, existing, value }] of confirmed.entries()) {
      requireValue(verifiedRows[index * 2], 'UPSTREAM_FORMAT');
      if (!existing) result.push({ provider, metadataId: row.metadataId, kind: target.kind,
        title: value.title ?? value.name, metadata: value, association: {
          kind: actualSeason === undefined ? 'title-year' : 'title-season',
          targetProvider: target.metadataProvider, targetMetadataId: target.metadataId,
        } });
      if (provider === 'douban' && actualSeason !== undefined) {
        const selected = seasonEvidence(value, row.metadataId, verifiedRows[index * 2 + 1].season ?? undefined);
        if (selected) doubanSeasons.push(selected);
      }
    }
    const uniqueSeasons = new Map();
    for (const row of doubanSeasons) {
      const prior = uniqueSeasons.get(row.season);
      if (!uniqueSeasons.has(row.season)) uniqueSeasons.set(row.season, row);
      else if (prior && String(prior.metadataId) !== String(row.metadataId)) uniqueSeasons.set(row.season, null);
    }
    const seasons = [...uniqueSeasons.values()].filter(Boolean).sort((a, b) => a.season - b.season);
    for (const row of result) {
      if (row.provider === 'tmdb' && Array.isArray(row.metadata.seasons)) row.seasons = row.metadata.seasons;
      else if (row.provider === 'douban' && seasons.length) row.seasons = seasons;
    }
    const douban = result.find(row => row.provider === 'douban' &&
      target.metadataProvider === 'douban' && String(row.metadataId) === String(target.metadataId)) ??
      result.find(row => row.provider === 'douban' && String(row.metadataId) === String(seasons[0]?.metadataId)) ??
      result.find(row => row.provider === 'douban');
    return result.filter(row => row.provider !== 'douban' || row === douban);
  }
  async function metadataSources(target, { rules, signal, canSchedule } = {}) {
    return execute([{ domain: 'tmdb' }, { domain: 'douban' }], { signal, canSchedule },
      (ownedSignal, checkSchedule) => metadataSourcesValue(target, rules, ownedSignal, checkSchedule));
  }
  async function seasonMetadataEntry(target, season, { signal, canSchedule, rules, refresh = false, requestedEpisode } = {}) {
    if (['source', 'group'].includes(target.kind)) { open(signal); return null; }
    requireValue(target.kind === 'tv' && Number.isSafeInteger(season) && season >= 0);
    return execute([{ domain: target.metadataProvider }], { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      if (target.metadataProvider === 'tmdb') return getMetadata(metadataRequest('tmdb',
        `/3/tv/${target.metadataId}/season/${season}`, {}),
      { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      requireValue(target.metadataProvider === 'douban');
      const entry = await getMetadata(targetRequest(target), { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      const primary = entry.value;
      checkSchedule();
      const row = await doubanSeason(primary, target.metadataId, createRecognitionRules(rules), ownedSignal);
      if (row?.season === season) return { ...entry, value: { ...primary, season, episodeCount: row.episodeCount } };
      rawMetadata(entry);
      const sources = await metadataSourcesValue(target, rules, ownedSignal, checkSchedule, { doubanOnly: true });
      const rows = sources.find(row => row.provider === 'douban')?.seasons ?? [];
      const selected = rows.find(row => row.season === season);
      requireValue(selected, 'NOT_FOUND');
      const selectedEntry = await getMetadata(targetRequest({ ...target, metadataId: selected.metadataId }),
        { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      return { ...selectedEntry, value: { ...selectedEntry.value, season, episodeCount: selected.episodeCount } };
    });
  }
  const addressIdentity = (candidate, source) => {
    const parent = identity(scopeOf(source), candidate.file
      ? { fileIdentity: stableFileIdentity(candidate.file), accountRevision: candidate.file.accountRevision }
      : candidate.playId, candidate.flag);
    return candidate.variant ? identity(parent, candidate.variant.id) : parent;
  };
  async function variantEntry(candidate, request, parent, dependencies, signal, checkSchedule) {
    if (!candidate.variant) return parent;
    const variant = mediaVariants(parent.value).find(value => value.id === candidate.variant.id);
    requireValue(variant, 'MEDIA_UNAVAILABLE');
    return load({ kind: 'variant-play', parent: request, variantId: variant.id,
      parentIdentity: identity(parent.value), parentFetchedAt: parent.fetchedAt },
      async () => {
        const value = clone(parent.value);
        for (const field of ['quality', 'resolution', 'fps', 'codec']) delete value[field];
        const { id, ...media } = variant;
        return { ...value, ...media, format: value.format ?? 'hls' };
      }, { ttl: (_value, at) => Math.max(0, parent.expiresAt - at), dependencies, signal, checkSchedule });
  }
  async function resolveValue(candidate, source, mode, signal, checkSchedule,
    { discoverVariants = false, refresh = false } = {}) {
    if (source.engine !== 'pan') await liveSource(source, signal, checkSchedule);
    if (candidate.file) {
      const original = candidate.file;
      requireValue(object(original) && providers.includes(original.provider) && ['share', 'stored'].includes(original.kind) &&
        Number.isSafeInteger(original.accountRevision) && original.accountRevision >= 0);
      requireValue(candidate.provider === undefined || candidate.provider === original.provider);
      const deps = [...sourceDependencies(source), accountDependency(original.provider)];
      const rows = await readAccounts(signal, deps, checkSchedule);
      const account = selectedAccount(rows, original.provider, 'STALE_ACCOUNT');
      requireValue(account.revision === original.accountRevision, 'STALE_ACCOUNT');
      let file = original;
      const fileDeps = [...sourceDependencies(source), accountDependency(file.provider, file.accountRevision)];
      if (file.kind === 'share' && ['quark', 'uc', 'baidu'].includes(file.provider)) {
        const entry = await load({ kind: 'pan-prepare', source: scopeOf(source),
          revision: file.accountRevision, fileIdentity: stableFileIdentity(file) }, async producerSignal => {
          const value = await call('pan', 'prepare', { provider: file.provider, file }, producerSignal, fileDeps);
          fileValue(value?.file, file.provider, file.accountRevision, 'stored');
          return value;
        }, { ttl: STABLE, dependencies: fileDeps, signal, checkSchedule });
        file = entry.value.file;
      }
      const selectedMode = mode ?? (file.provider === 'yidong' ? 'download' : 'streaming');
      requireValue(['streaming', 'download'].includes(selectedMode));
      const dependency = [...fileDeps, { kind: 'address', identity: addressIdentity({ ...candidate, variant: undefined }, source) }];
      const request = { kind: 'pan-play', source: scopeOf(source), provider: file.provider,
        revision: file.accountRevision, file, mode: selectedMode };
      const parent = await load(request, async producerSignal => {
        const value = mediaValue(await call('pan', 'play', { provider: file.provider, file, mode: selectedMode },
          producerSignal, dependency));
        if (value.file !== undefined) fileValue(value.file, file.provider, file.accountRevision);
        requireValue(value.mode === selectedMode, 'UPSTREAM_FORMAT');
        return value;
      }, { ttl: addressTTL, dependencies: dependency, signal, checkSchedule, refresh });
      const entry = await variantEntry(candidate, request, parent,
        [...fileDeps, { kind: 'address', identity: addressIdentity(candidate, source) }], signal, checkSchedule);
      return { ...clone(entry.value), resolvedFile: clone(file), resolvedAt: entry.fetchedAt,
        expiresAt: entry.expiresAt, scope: { ...scopeOf(source), accountProvider: file.provider,
          accountRevision: file.accountRevision } };
    }
    requireValue(source.engine !== 'pan' && !candidate.share, 'INVALID_REQUEST');
    requireValue(isIdentifier(candidate.playId) && isIdentifier(candidate.flag));
    const discover = source.engine === 'cat' && (discoverVariants || !!candidate.variant);
    const dependencies = [...scriptDependencies(source),
      { kind: 'address', identity: addressIdentity({ ...candidate, variant: undefined }, source) }];
    const request = sourceRequest('play', source, { flag: candidate.flag, playId: candidate.playId });
    // Discovery enriches the same parent receipt; ordinary readers can reuse it.
    const completeness = source.engine === 'cat' && !discover ? 'partial' : 'complete';
    const parent = await load(request,
      async producerSignal => mediaValue(await invoke(source, 'play', { flag: candidate.flag, id: candidate.playId,
        ...(source.engine === 'cat' ? { flags: [] } : { vipFlags: [] }) }, producerSignal, undefined,
      { discoverVariants: discover })),
      { ttl: addressTTL, completeness, dependencies, signal, checkSchedule, refresh });
    const entry = await variantEntry(candidate, request, parent,
      [...scriptDependencies(source), { kind: 'address', identity: addressIdentity(candidate, source) }], signal, checkSchedule);
    return { ...clone(entry.value), resolvedAt: entry.fetchedAt, expiresAt: entry.expiresAt, scope: scopeOf(source) };
  }
  function candidateDependencies(candidate, source) {
    return [...(candidate.file ? sourceDependencies(source) : scriptDependencies(source)), ...providers.filter(provider =>
      provider === candidate.file?.provider).map(provider => accountDependency(provider))];
  }
  async function resolve(candidate, { signal, canSchedule, mode, discoverVariants = false } = {}) {
    requireValue(object(candidate));
    requireValue(typeof discoverVariants === 'boolean');
    requireValue(mode === undefined || ['streaming', 'download'].includes(mode));
    const source = playbackSource(candidate.source);
    return execute(candidateDependencies(candidate, source), { signal, canSchedule },
      (ownedSignal, checkSchedule) => resolveValue(candidate, source, mode, ownedSignal, checkSchedule, { discoverVariants }));
  }
  async function refresh(candidate, { signal, canSchedule, mode } = {}) {
    requireValue(object(candidate));
    requireValue(mode === undefined || ['streaming', 'download'].includes(mode));
    const source = playbackSource(candidate.source);
    return execute(candidateDependencies(candidate, source), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      const selected = addressIdentity(candidate, source);
      const parent = candidate.variant ? addressIdentity({ ...candidate, variant: undefined }, source) : selected;
      await cache.invalidate(entry => entry.dependencies?.some(dep => dep.kind === 'address' &&
        (dep.identity === selected || dep.identity === parent)));
      checkSchedule();
      return resolveValue(candidate, source, mode, ownedSignal, checkSchedule,
        { discoverVariants: source.engine === 'cat' && !candidate.file, refresh: !!candidate.variant });
    });
  }
  async function invalidateAddress(candidate, { signal, canSchedule } = {}) {
    requireValue(object(candidate));
    const source = playbackSource(candidate.source);
    return execute(candidateDependencies(candidate, source), { signal, canSchedule }, async (_ownedSignal, checkSchedule) => {
      const selected = addressIdentity(candidate, source);
      await cache.invalidate(entry => entry.dependencies?.some(dep => dep.kind === 'address' && dep.identity === selected));
      checkSchedule();
    });
  }
  async function report(candidate, payload, { signal, canSchedule } = {}) {
    requireValue(object(candidate) && isIdentifier(candidate.playId) && isIdentifier(candidate.flag));
    const source = checkSource(candidate.source);
    requireValue(object(payload) && text(payload.sessionId) && text(payload.event) &&
      Number.isFinite(payload.positionSeconds) && payload.positionSeconds >= 0 &&
      Number.isFinite(payload.durationSeconds) && payload.durationSeconds >= 0);
    return execute(scriptDependencies(source), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      await liveSource(source, ownedSignal, checkSchedule);
      const value = await invoke(source, 'report', { id: candidate.playId, flag: candidate.flag,
        sessionId: payload.sessionId, positionSeconds: payload.positionSeconds,
        durationSeconds: payload.durationSeconds, event: payload.event }, ownedSignal, checkSchedule);
      requireValue(value.ok === true, 'UPSTREAM_ERROR');
      return value;
    });
  }
  async function handoffRequest(input, signal, checkSchedule) {
    requireValue(object(input) && ['search', 'detail', 'metadata'].includes(input.kind));
    if (input.kind === 'metadata') {
      const request = metadataRequest(input.provider, input.path, input.query ?? {}, input.imageSize);
      return { request: configuredMetadataRequest(request, await settings(input.provider, signal, checkSchedule)),
        dependencies: [{ domain: input.provider }] };
    }
    const source = checkSource(input.source);
    await liveSource(source, signal, checkSchedule);
    if (input.kind === 'search') requireValue(text(input.query) && Number.isSafeInteger(input.page) && input.page > 0);
    else requireValue(isIdentifier(input.detailId));
    return { request: sourceRequest(input.kind, source, input.kind === 'search'
      ? { query: input.query, page: input.page } : { detailId: input.detailId }), dependencies: scriptDependencies(source) };
  }
  function inputDependencies(input) {
    return input.kind === 'metadata' ? [{ domain: input.provider }] : scriptDependencies(checkSource(input.source));
  }
  async function seedValue(input, signal, checkSchedule, optionalContent = false) {
    requireValue(['partial', 'complete'].includes(input.completeness) && Number.isFinite(input.fetchedAt) &&
      input.fetchedAt >= 0 && input.fetchedAt <= time() && (input.expiresAt === undefined || Number.isFinite(input.expiresAt)));
    let prepared;
    try { prepared = await handoffRequest(input, signal, checkSchedule); }
    catch (error) { if (error.code === 'STALE_SOURCE') return undefined; throw error; }
    const { request, dependencies } = prepared;
    if (input.kind === 'metadata' &&
        (!text(input.settings?.revision) || input.settings.revision !== request.settings.revision)) return undefined;
    let ttl;
    try {
      if (input.kind === 'metadata') {
        metadataValue(input.provider, input.path, input.value, input.completeness);
        const parent = metadataCache.parentFor(request);
        ttl = metadataTTL(input.provider, input.path, input.value, input.fetchedAt, input.completeness, parent);
      } else {
        requireValue(object(input.value), 'UPSTREAM_FORMAT');
        if (input.kind === 'search') searchPage(input.value, input.page);
        else if (input.completeness === 'complete') detailValue(input.value, request.source, input.detailId);
        ttl = HOUR;
      }
    } catch (error) {
      if (optionalContent && error.code === 'UPSTREAM_FORMAT') return undefined;
      throw error;
    }
    if (input.kind === 'metadata')
      dependencies[0] = { kind: 'metadata', domain: input.provider, revision: request.settings.revision };
    const expiresAt = Math.min(input.fetchedAt + ttl, input.expiresAt ?? input.fetchedAt + ttl);
    if (expiresAt <= time()) return undefined;
    open(signal);
    if (input.kind === 'metadata') {
      return metadataCache.seed(request, input.value, { completeness: input.completeness,
        fetchedAt: input.fetchedAt, expiresAt });
    }
    const key = cacheKey(request), previous = cache.read(key);
    if (previous && previous.fetchedAt > input.fetchedAt &&
        (previous.completeness === 'complete' || input.completeness === 'partial')) return undefined;
    const accepted = cache.seed(key, record(request, input.value, input.completeness, input.fetchedAt, expiresAt),
      { expiresAt, completeness: input.completeness, dependencies: [cacheOwner, ...dependencies] });
    return accepted === previous ? undefined : accepted?.value;
  }
  async function handoff(entries, { signal, canSchedule } = {}) {
    requireValue(Array.isArray(entries) && entries.every(object));
    return execute(entries.flatMap(inputDependencies), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      let accepted = 0, ignored = 0;
      for (const entry of entries) {
        checkSchedule();
        if (await seedValue(entry, ownedSignal, checkSchedule, true) === undefined) ignored++; else accepted++;
      }
      return { accepted, ignored };
    });
  }
  async function cachedEntry(input, { signal, canSchedule, minimumCompleteness = 'partial' } = {}) {
    requireValue(object(input));
    return execute(inputDependencies(input), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      const { request } = await handoffRequest(input, ownedSignal, checkSchedule);
      open(ownedSignal);
      if (input.kind === 'metadata') return metadataCache.entry(request, { minimumCompleteness });
      return cache.read(cacheKey(request), { minimumCompleteness });
    });
  }
  function selector(scope) {
    if (typeof scope === 'string') {
      requireValue(text(scope));
      return dep => dep.domain === scope || scope === 'source' && dep.kind === 'source' || scope === 'pan' && dep.kind === 'account';
    }
    if (object(scope) && scope.kind === 'script-account') {
      requireValue(text(scope.installationId) && Number.isSafeInteger(scope.generation));
      return dep => dep.kind === 'script-account' && dep.installationId === scope.installationId &&
        dep.generation === scope.generation;
    }
    requireValue(object(scope) && scope.kind === 'source' && ['cat', 'source'].includes(scope.engine) &&
      (scope.installationId === undefined || text(scope.installationId)) &&
      (scope.generation === undefined || Number.isSafeInteger(scope.generation) && scope.generation > 0));
    return dep => dep.kind === 'source' && dep.engine === scope.engine &&
      (scope.installationId === undefined || dep.installationId === scope.installationId) &&
      (scope.generation === undefined || dep.generation === scope.generation);
  }
  async function invalidate(scope) {
    open();
    const matches = selector(scope);
    const selected = [...pending].filter(work => work.dependencies.some(matches));
    debug?.emit({ level: 'info', cat: 'film-catalog', sub: 'film.data', event: 'data.invalidate',
      data: { scope, abortedJobs: selected.length } });
    for (const work of selected) work.controller.abort();
    const drained = cache.invalidate(entry => entry.dependencies?.some(matches));
    const disk = metadataCache.invalidate(matches);
    const results = await Promise.allSettled([drained, disk, ...selected.map(work => work.done)]);
    for (const result of results.slice(0, 2)) if (result.status === 'rejected') throw result.reason;
  }
  function close() {
    if (closing) return closing;
    closed = true;
    lifetime.abort();
    const selected = [...pending];
    let drained;
    try { drained = cache.invalidate(entry => entry.dependencies?.some(dep => dep.domain === cacheOwner.domain && dep.owner === cacheOwner.owner)); }
    catch (error) { if (error.code !== 'CLOSED') throw error; }
    closing = Promise.allSettled([...selected.map(work => work.done), drained])
      .then(() => metadataCache.close());
    return closing;
  }
  function diagnostics() {
    return {
      closed,
      pendingOperations: pending.size,
      metadataCache: metadataCache.diagnostics(),
    };
  }
  return { catalog, search, detail, detailEntry, expand, metadata, metadataEntry, targetMetadataEntry, tmdbTargetEntry,
    seasonMetadataEntry, metadataSources, resolve, refresh, invalidateAddress,
    report, handoff, cachedEntry, invalidate, diagnostics, close };
}
