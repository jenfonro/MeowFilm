import { randomUUID } from 'node:crypto';
import { decodePanResource } from '@meowfilm/script-host/cat-output';
import { normalizeDetail, stableFileIdentity } from './recognition.js';
import { createRecognitionRules, providerFamily } from './rules.js';
import { createMetadataCache } from './metadata-cache.js';
import { metadataTTL } from './metadata-policy.js';
import { checkSource, clone, fail, identity, isIdentifier, object, parseSourceValue, requireValue,
  sourceRevision, text } from './identity.js';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;
const STABLE = 14 * DAY;
const ADDRESS = 3 * 60_000;
const ordinaryProviders = new Set(['baidu', 'quark', 'uc', 'tianyi', 'yidong']);
const providers = [...ordinaryProviders, 'quark_tv', 'uc_tv'];
const prefixes = { cat: 'cat.script.', source: 'source.runtime.', pan: 'pan.',
  tmdb: 'tmdb.data.', douban: 'douban.data.' };
const errorCodes = new Set([
  'INVALID_REQUEST', 'UPSTREAM_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'NETWORK_ERROR',
  'TIMEOUT', 'RESOURCE_LIMIT', 'CANCELLED', 'CLOSED', 'STALE_SOURCE', 'STALE_ACCOUNT', 'AUTH_REQUIRED', 'AUTH_FAILED',
  'NOT_CONFIGURED', 'NOT_FOUND', 'LOGIN_EXPIRED', 'CHALLENGE_REQUIRED', 'WEB_INTERACTION_REQUIRED',
  'CAPABILITY_UNAVAILABLE', 'UNSUPPORTED_OPERATION', 'PAN_FAILURE', 'SOURCE_FAILURE', 'CAT_FAILURE',
  'SCRIPT_FAILURE', 'SCRIPT_PROTOCOL_ERROR', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'MEDIA_UNAVAILABLE', 'INVALID_RULES', 'CACHE_LOAD_FAILED', 'STALE_METADATA',
]);
const safeError = error => fail(error?.name === 'AbortError' ? 'CANCELLED'
  : errorCodes.has(error?.code) ? error.code : 'UPSTREAM_ERROR');
const accountDependency = (provider, revision) => ({ kind: 'account', domain: provider,
  ...(revision === undefined ? {} : { revision }) });

function sourceDependencies(source) {
  if (source.engine === 'pan') return [{ domain: source.key }];
  return [{ kind: 'source', domain: source.key, engine: source.engine,
    installationId: source.installationId, siteId: source.siteId,
    generation: source.generation, ...(source.revision === undefined ? {} : { revision: source.revision }),
    sourceRevision: sourceRevision(source) }];
}
function playbackSource(value) {
  if (value?.engine !== 'pan') return checkSource(value);
  requireValue(object(value) && text(value.key) && isIdentifier(value.detailId));
  return clone(value);
}
function scopeOf(source) {
  return { sourceKey: source.key, sourceRevision: sourceRevision(source),
    ...(source.engine === 'pan' ? {} : {
      sourceEngine: source.engine, sourceInstallationId: source.installationId,
    }) };
}
function searchPage(raw, page) {
  const items = Array.isArray(raw.list) ? raw.list : Array.isArray(raw.items) ? raw.items : null;
  requireValue(items && items.every(object), 'UPSTREAM_FORMAT');
  const pageCount = [raw.pagecount, raw.pageCount]
    .filter(value => typeof value === 'number' || typeof value === 'string')
    .map(Number).find(value => Number.isSafeInteger(value) && value > 0);
  const explicitPageCount = pageCount !== undefined;
  return { items, page, hasMore: explicitPageCount ? page < pageCount : items.length > 0,
    explicitPageCount, ...(explicitPageCount ? { pageCount } : {}), raw };
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
  requireValue(value.success !== false, 'UPSTREAM_HTTP');
  if (provider === 'douban' && path === 'search') {
    requireValue(Array.isArray(value.subjects?.items) && value.subjects.items.every(row =>
      object(row) && object(row.target) && isIdentifier(row.target_id ?? row.target.id)), 'UPSTREAM_FORMAT');
  } else if (provider === 'tmdb' && /^\/3\/(?:search|discover|trending)\//.test(path)) {
    requireValue(Array.isArray(value.results) && value.results.every(row => object(row) && isIdentifier(row.id)),
      'UPSTREAM_FORMAT');
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

/** All values, timestamps and completeness live in the one injected cache. */
export function createPlaybackData({ modules, cache, dataRoot, now = Date.now }) {
  requireValue(modules instanceof Map && cache && typeof now === 'function');
  const lifetime = new AbortController();
  const pending = new Set();
  const cacheOwner = { domain: 'film-data', owner: randomUUID() };
  const metadataCache = createMetadataCache({ dataRoot, cache, owner: cacheOwner, now: time, normalizeError: safeError });
  let closed = false, closing;

  function open(signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  function time() {
    const at = now();
    requireValue(Number.isFinite(at) && at >= 0);
    return at;
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
  async function invoke(source, action, payload, signal, checkSchedule) {
    const value = await call(source.engine, 'invoke', source.engine === 'cat' ? {
      installationId: source.installationId, generation: source.generation,
      siteId: source.siteId, operation: action, payload,
    } : {
      installationId: source.installationId, revision: source.revision,
      generation: source.generation, action, payload,
    }, signal, sourceDependencies(source), checkSchedule,
    source.engine === 'cat' && action === 'play' ? { mediaPurpose: 'receipt' } : undefined);
    return parseSourceValue(value);
  }
  async function catalog(source, action, payload = {}, { signal, canSchedule, refresh = false } = {}) {
    source = checkSource(source);
    requireValue(['home', 'category'].includes(action) && object(payload));
    const deps = sourceDependencies(source);
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
    const deps = sourceDependencies(source);
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
  async function detail(source, detailId, { signal, canSchedule, refresh = false } = {}) {
    source = checkSource(source);
    requireValue(isIdentifier(detailId));
    const deps = sourceDependencies(source);
    return execute(deps, { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      await liveSource(source, ownedSignal, checkSchedule);
      const entry = await load(sourceRequest('detail', source, { detailId }), async producerSignal => {
        const raw = await invoke(source, 'detail', { id: detailId }, producerSignal);
        detailValue(raw, source, detailId);
        return raw;
      }, { ttl: HOUR, dependencies: deps, signal: ownedSignal, checkSchedule, refresh });
      return detailValue(clone(entry.value), source, detailId);
    });
  }
  async function readAccounts(signal, deps = providers.map(provider => accountDependency(provider)), checkSchedule) {
    const value = await call('pan', 'settings.get', {}, signal, deps, checkSchedule);
    requireValue(Array.isArray(value?.accounts) && value.accounts.every(row => object(row) &&
      providers.includes(row.provider) && Number.isSafeInteger(row.revision) && row.revision >= 0 &&
      typeof row.configured === 'boolean'), 'UPSTREAM_FORMAT');
    requireValue(new Set(value.accounts.map(row => row.provider)).size === value.accounts.length, 'UPSTREAM_FORMAT');
    return value.accounts;
  }
  async function accounts(signal) {
    const deps = providers.map(provider => accountDependency(provider));
    return execute(deps, { signal }, (ownedSignal, checkSchedule) => readAccounts(ownedSignal, deps, checkSchedule));
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
      requireValue(object(share) && ordinaryProviders.has(share.provider) && text(share.url));
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
      if (!shares.size) return line;
      if (source.engine !== 'pan') await liveSource(source, ownedSignal, checkSchedule);
      const rows = await readAccounts(ownedSignal, deps, checkSchedule);
      const lists = new Map();
      for (const [key, share] of shares) {
        open(ownedSignal);
        const account = rows.find(row => row.provider === share.provider);
        requireValue(account, 'NOT_CONFIGURED');
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
    if (provider === 'douban' && path === 'search') requireValue(Object.keys(query).length === 1 && text(query.query));
    else requireValue(Object.values(query).every(value => {
      const scalar = item => typeof item === 'string' || typeof item === 'boolean' ||
        typeof item === 'number' && Number.isFinite(item);
      return provider === 'douban' && Array.isArray(value) ? value.every(scalar) : scalar(value);
    }));
    if (imageSize !== undefined) requireValue(provider === 'tmdb' &&
      typeof imageSize === 'string' && /^(?:original|[wh][1-9]\d*)$/.test(imageSize));
    if (query.append_to_response !== undefined) requireValue(typeof query.append_to_response === 'string');
    return { kind: 'metadata', provider, path, query: clone(query),
      ...(provider === 'tmdb' ? { imageSize: imageSize ?? 'original' } : {}) };
  }
  async function settings(provider, signal, checkSchedule) {
    const value = await call(provider, 'settings.get', {}, signal, [{ domain: provider }], checkSchedule);
    requireValue(object(value) && text(value.revision), 'UPSTREAM_FORMAT');
    return { revision: value.revision };
  }
  async function getMetadata(request, { signal, completeness = 'complete', checkSchedule, refresh = false, requestedEpisode } = {}) {
    requireValue(['partial', 'complete'].includes(completeness));
    requireValue(typeof refresh === 'boolean' && (requestedEpisode === undefined ||
      Number.isSafeInteger(requestedEpisode) && requestedEpisode > 0));
    const configuration = await settings(request.provider, signal, checkSchedule);
    const keyed = { ...request, settings: configuration };
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
    target.metadataProvider === 'tmdb' ? { language: 'zh-CN', append_to_response: 'credits,similar,images' } : {});
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
  async function targetMetadata(target, options = {}) {
    return rawMetadata(await targetMetadataEntry(target, options));
  }
  async function metadataSourcesValue(target, supplied, rules, signal, checkSchedule) {
    if (!target.metadataProvider) return [];
    const primaryRequest = targetRequest(target);
    if (supplied !== undefined) metadataValue(target.metadataProvider, primaryRequest.path, supplied, 'complete');
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
    if (opposite === 'tmdb' && modules.has('tmdb')) {
      let page = 1, pages = 1;
      const seen = new Set();
      do {
        const response = rawMetadata(await getMetadata(metadataRequest('tmdb', `/3/search/${target.kind}`,
          { query, language: 'zh-CN', page }), { signal, checkSchedule }));
        pages = Number(response.total_pages ?? 1);
        requireValue(Number.isSafeInteger(pages) && pages >= 0, 'UPSTREAM_FORMAT');
        const key = identity(response.results);
        requireValue(page >= pages || response.results.length > 0 && !seen.has(key), 'UPSTREAM_FORMAT');
        seen.add(key);
        for (const row of response.results) found.push({ ...row, title: row.title ?? row.name,
          kind: target.kind, metadataProvider: 'tmdb', metadataId: row.id });
        page++;
      } while (page <= pages);
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
    const confirmed = [];
    for (const group of candidates.values()) {
      if (group.size !== 1) continue;
      const { row, actualSeason } = group.values().next().value;
      const provider = row.metadataProvider;
      const existing = result.find(item => item.provider === provider && String(item.metadataId) === String(row.metadataId));
      const value = existing?.metadata ?? rawMetadata(await getMetadata(targetRequest({ ...target,
        metadataProvider: provider, metadataId: row.metadataId }), { signal, checkSchedule }));
      const verified = { ...value, kind: target.kind, title: value.title ?? value.name,
        metadataProvider: provider, metadataId: value.id };
      confirmed.push({ row, actualSeason, provider, existing, value, verified });
    }
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
    return result;
  }
  async function metadataSources(target, { metadata: supplied, rules, signal, canSchedule } = {}) {
    return execute([{ domain: 'tmdb' }, { domain: 'douban' }], { signal, canSchedule },
      (ownedSignal, checkSchedule) => metadataSourcesValue(target, supplied, rules, ownedSignal, checkSchedule));
  }
  async function seasonMetadataEntry(target, season, { signal, canSchedule, rules, refresh = false, requestedEpisode } = {}) {
    if (['source', 'group'].includes(target.kind)) { open(signal); return null; }
    requireValue(target.kind === 'tv' && Number.isSafeInteger(season) && season >= 0);
    return execute([{ domain: target.metadataProvider }, { domain: 'douban' }], { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      if (target.metadataProvider === 'tmdb') return getMetadata(metadataRequest('tmdb',
        `/3/tv/${target.metadataId}/season/${season}`, { language: 'zh-CN' }),
      { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      requireValue(target.metadataProvider === 'douban');
      const entry = await getMetadata(targetRequest(target), { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      const primary = entry.value;
      checkSchedule();
      const row = await doubanSeason(primary, target.metadataId, createRecognitionRules(rules), ownedSignal);
      if (row?.season === season) return { ...entry, value: { ...primary, season, episodeCount: row.episodeCount } };
      rawMetadata(entry);
      const sources = await metadataSourcesValue(target, primary, rules, ownedSignal, checkSchedule);
      const rows = sources.find(row => row.provider === 'douban')?.seasons ?? [];
      const selected = rows.find(row => row.season === season);
      requireValue(selected, 'NOT_FOUND');
      const selectedEntry = await getMetadata(targetRequest({ ...target, metadataId: selected.metadataId }),
        { signal: ownedSignal, checkSchedule, refresh, requestedEpisode });
      return { ...selectedEntry, value: { ...selectedEntry.value, season, episodeCount: selected.episodeCount } };
    });
  }
  async function seasonMetadata(target, season, options = {}) {
    return rawMetadata(await seasonMetadataEntry(target, season, options));
  }
  const addressIdentity = (candidate, source) => identity(scopeOf(source), candidate.file
    ? { fileIdentity: stableFileIdentity(candidate.file), accountRevision: candidate.file.accountRevision }
    : candidate.playId, candidate.flag);
  async function resolveValue(candidate, source, mode, signal, checkSchedule) {
    if (source.engine !== 'pan') await liveSource(source, signal, checkSchedule);
    if (candidate.file) {
      const original = candidate.file;
      requireValue(object(original) && providers.includes(original.provider) && ['share', 'stored'].includes(original.kind) &&
        Number.isSafeInteger(original.accountRevision) && original.accountRevision >= 0);
      requireValue(candidate.provider === undefined || providerFamily(candidate.provider) === providerFamily(original.provider));
      const deps = [...sourceDependencies(source), ...providers.filter(provider =>
        providerFamily(provider) === providerFamily(original.provider)).map(provider => accountDependency(provider))];
      const rows = await readAccounts(signal, deps, checkSchedule);
      const account = rows.find(row => row.provider === original.provider);
      requireValue(account && account.revision === original.accountRevision, 'STALE_ACCOUNT');
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
      const choices = [file.provider];
      const tv = rows.find(row => row.provider === `${file.provider}_tv`);
      if (tv?.configured && file.kind === 'stored') {
        const matchDeps = [accountDependency(file.provider, file.accountRevision), accountDependency(tv.provider, tv.revision)];
        try {
          const proof = await load({ kind: 'account-match', provider: tv.provider,
            revision: tv.revision, baseRevision: file.accountRevision }, async producerSignal => {
            const value = await call('pan', 'account.match', { provider: tv.provider,
              revision: tv.revision, baseRevision: file.accountRevision }, producerSignal, matchDeps);
            requireValue(object(value) && value.provider === tv.provider && value.revision === tv.revision &&
              value.baseProvider === file.provider && value.baseRevision === file.accountRevision &&
              typeof value.sameAccount === 'boolean', 'UPSTREAM_FORMAT');
            return value;
          }, { ttl: STABLE, dependencies: matchDeps, signal, checkSchedule });
          if (proof.value.sameAccount === true) choices.unshift(tv.provider);
        } catch (error) {
          if (['CANCELLED', 'CLOSED', 'STALE_ACCOUNT'].includes(error.code)) throw error;
          // Failed identity lookup is not proof. Only the ordinary account is usable.
        }
      }
      let failure;
      for (const provider of choices) {
        open(signal);
        const row = rows.find(item => item.provider === provider);
        const dependency = [...fileDeps, accountDependency(provider, row.revision),
          { kind: 'address', identity: addressIdentity(candidate, source) }];
        try {
          const entry = await load({ kind: 'pan-play', source: scopeOf(source), provider,
            revision: row.revision, file, mode: selectedMode }, async producerSignal => {
            const value = mediaValue(await call('pan', 'play', { provider, file, mode: selectedMode }, producerSignal, dependency));
            if (value.file !== undefined) fileValue(value.file, file.provider, file.accountRevision);
            requireValue(value.mode === selectedMode, 'UPSTREAM_FORMAT');
            return value;
          }, { ttl: addressTTL, dependencies: dependency, signal, checkSchedule });
          return { ...clone(entry.value), resolvedFile: clone(file), resolvedAt: entry.fetchedAt,
            expiresAt: entry.expiresAt, scope: { ...scopeOf(source), accountProvider: provider, accountRevision: row.revision,
              fileProvider: file.provider, fileAccountRevision: file.accountRevision } };
        } catch (error) {
          if (signal.aborted || ['CLOSED', 'CANCELLED', 'STALE_ACCOUNT', 'STALE_SOURCE'].includes(error.code)) throw error;
          failure = error;
        }
      }
      throw failure;
    }
    requireValue(source.engine !== 'pan' && !candidate.share, 'INVALID_REQUEST');
    requireValue(isIdentifier(candidate.playId) && isIdentifier(candidate.flag));
    const dependencies = [...sourceDependencies(source),
      { kind: 'address', identity: addressIdentity(candidate, source) }];
    const entry = await load(sourceRequest('play', source, { flag: candidate.flag, playId: candidate.playId }),
      async producerSignal => mediaValue(await invoke(source, 'play', { flag: candidate.flag, id: candidate.playId,
        ...(source.engine === 'cat' ? { flags: [] } : { vipFlags: [] }) }, producerSignal)),
      { ttl: addressTTL, dependencies, signal, checkSchedule });
    return { ...clone(entry.value), resolvedAt: entry.fetchedAt, expiresAt: entry.expiresAt, scope: scopeOf(source) };
  }
  function candidateDependencies(candidate, source) {
    return [...sourceDependencies(source), ...providers.filter(provider =>
      providerFamily(provider) === providerFamily(candidate.file?.provider)).map(provider => accountDependency(provider))];
  }
  async function resolve(candidate, { signal, canSchedule, mode } = {}) {
    requireValue(object(candidate));
    requireValue(mode === undefined || ['streaming', 'download'].includes(mode));
    const source = playbackSource(candidate.source);
    return execute(candidateDependencies(candidate, source), { signal, canSchedule },
      (ownedSignal, checkSchedule) => resolveValue(candidate, source, mode, ownedSignal, checkSchedule));
  }
  async function refresh(candidate, { signal, canSchedule, mode } = {}) {
    requireValue(object(candidate));
    requireValue(mode === undefined || ['streaming', 'download'].includes(mode));
    const source = playbackSource(candidate.source);
    return execute(candidateDependencies(candidate, source), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      const selected = addressIdentity(candidate, source);
      await cache.invalidate(entry => entry.dependencies?.some(dep => dep.kind === 'address' && dep.identity === selected));
      checkSchedule();
      return resolveValue(candidate, source, mode, ownedSignal, checkSchedule);
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
    return execute(sourceDependencies(source), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
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
      return { request: { ...request, settings: await settings(input.provider, signal, checkSchedule) }, dependencies: [{ domain: input.provider }] };
    }
    const source = checkSource(input.source);
    await liveSource(source, signal, checkSchedule);
    if (input.kind === 'search') requireValue(text(input.query) && Number.isSafeInteger(input.page) && input.page > 0);
    else requireValue(isIdentifier(input.detailId));
    return { request: sourceRequest(input.kind, source, input.kind === 'search'
      ? { query: input.query, page: input.page } : { detailId: input.detailId }), dependencies: sourceDependencies(source) };
  }
  function inputDependencies(input) {
    return input.kind === 'metadata' ? [{ domain: input.provider }] : sourceDependencies(checkSource(input.source));
  }
  async function seedValue(input, signal, checkSchedule) {
    requireValue(['partial', 'complete'].includes(input.completeness) && Number.isFinite(input.fetchedAt) &&
      input.fetchedAt >= 0 && input.fetchedAt <= time() && (input.expiresAt === undefined || Number.isFinite(input.expiresAt)));
    let prepared;
    try { prepared = await handoffRequest(input, signal, checkSchedule); }
    catch (error) { if (error.code === 'STALE_SOURCE') return undefined; throw error; }
    const { request, dependencies } = prepared;
    let ttl;
    if (input.kind === 'metadata') {
      if (!text(input.settings?.revision) || input.settings.revision !== request.settings.revision) return undefined;
      metadataValue(input.provider, input.path, input.value, input.completeness);
      ttl = metadataTTL(input.provider, input.path, input.value, input.fetchedAt, input.completeness);
      dependencies[0] = { kind: 'metadata', domain: input.provider, revision: request.settings.revision };
    } else {
      requireValue(object(input.value), 'UPSTREAM_FORMAT');
      if (input.kind === 'search') searchPage(input.value, input.page);
      else if (input.completeness === 'complete') detailValue(input.value, request.source, input.detailId);
      ttl = HOUR;
    }
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
  async function seed(input, { signal, canSchedule } = {}) {
    requireValue(object(input));
    return execute(inputDependencies(input), { signal, canSchedule },
      (ownedSignal, checkSchedule) => seedValue(input, ownedSignal, checkSchedule));
  }
  async function handoff(entries, { signal, canSchedule } = {}) {
    requireValue(Array.isArray(entries) && entries.every(object));
    return execute(entries.flatMap(inputDependencies), { signal, canSchedule }, async (ownedSignal, checkSchedule) => {
      let accepted = 0, ignored = 0;
      for (const entry of entries) {
        checkSchedule();
        if (await seedValue(entry, ownedSignal, checkSchedule) === undefined) ignored++; else accepted++;
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
    requireValue(object(scope) && scope.kind === 'source' && ['cat', 'source'].includes(scope.engine) &&
      (scope.installationId === undefined || text(scope.installationId)));
    return dep => dep.kind === 'source' && dep.engine === scope.engine &&
      (scope.installationId === undefined || dep.installationId === scope.installationId);
  }
  async function invalidate(scope) {
    open();
    const matches = selector(scope);
    const selected = [...pending].filter(work => work.dependencies.some(matches));
    for (const work of selected) work.controller.abort();
    const drained = cache.invalidate(entry => entry.dependencies?.some(matches));
    try { metadataCache.invalidate(matches); }
    finally { await Promise.allSettled([...selected.map(work => work.done), drained]); }
  }
  function close() {
    if (closing) return closing;
    closed = true;
    lifetime.abort();
    const selected = [...pending];
    let drained;
    try { drained = cache.invalidate(entry => entry.dependencies?.some(dep => dep.domain === cacheOwner.domain && dep.owner === cacheOwner.owner)); }
    catch (error) { if (error.code !== 'CLOSED') throw error; }
    closing = Promise.allSettled(selected.map(work => work.done)).then(() => drained);
    return closing;
  }
  return { catalog, search, detail, expand, metadata, metadataEntry, targetMetadata, targetMetadataEntry,
    seasonMetadata, seasonMetadataEntry, metadataSources, resolve, refresh, invalidateAddress,
    report, accounts, seed, handoff, cachedEntry, invalidate, close };
}
