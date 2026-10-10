import { getPanShareInput, normalizePanMockFlag } from '../utils/matchCore';
import { normalizeString } from './normalize';
import { hasDetailNavigation, navigationRequestKey, runProviderListTask, walkDetailNavigation } from './detailNavigation';

export function normalizecatpawrunnerApiBase(inputUrl) {
  const raw = typeof inputUrl === 'string' ? inputUrl.trim() : '';
  if (!raw) return '';
  try {
    const url = new URL(raw);
    url.hash = '';
    url.search = '';
    let path = url.pathname || '/';
    const spiderIdx = path.indexOf('/spider/');
    if (spiderIdx >= 0) path = path.slice(0, spiderIdx) || '/';
    // If user pasted an id-prefixed spider API like "/<id>/spider/...", drop the id segment.
    if (/^\/[a-f0-9]{10}\/?$/.test(path)) path = '/';
    path = path.replace(/\/spider\/?$/, '/');
    path = path.replace(/\/(full-config|config|website)\/?$/, '/');
    // Keep pathname but ensure it ends with "/" so URL(resolve) works as expected.
    if (!path.endsWith('/')) path += '/';
    url.pathname = path;
    return url.toString();
  } catch (_e) {
    return '';
  }
}

let lowPriorityPauseCount = 0;
let lowPriorityWaiters = [];
let lowPrioritySearchTickets = 0;

const flushLowPriorityWaiters = () => {
  if (!lowPriorityWaiters.length) return;
  const waiters = lowPriorityWaiters;
  lowPriorityWaiters = [];
  waiters.forEach((fn) => {
    try {
      fn();
    } catch (_e) {}
  });
};

const waitIfLowPriorityPaused = async () => {
  if (lowPriorityPauseCount <= 0) return;
  if (lowPrioritySearchTickets > 0) {
    lowPrioritySearchTickets = Math.max(0, lowPrioritySearchTickets - 1);
    return;
  }
  await new Promise((resolve) => {
    lowPriorityWaiters.push(resolve);
  });
};

export function pauseCatLowPriority() {
  lowPriorityPauseCount += 1;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    lowPriorityPauseCount = Math.max(0, lowPriorityPauseCount - 1);
    if (lowPriorityPauseCount === 0) flushLowPriorityWaiters();
  };
}

export function grantCatLowPrioritySearchTickets(count) {
  const n = Number.isFinite(Number(count)) ? Math.floor(Number(count)) : 0;
  if (n <= 0) return;
  lowPrioritySearchTickets = Math.min(5000, Math.max(0, lowPrioritySearchTickets + n));
}

export async function requestCatSpider({
  apiBase,
  username,
  action,
  spiderApi,
  payload,
  query,
  headers: extraHeaders,
  signal,
  timeoutMs,
}) {
  const safeAction = typeof action === 'string' ? action.trim() : '';
  const safeSpider = typeof spiderApi === 'string' ? spiderApi.trim() : '';
  const body = payload && typeof payload === 'object' ? payload : {};
  const q = query && typeof query === 'object' ? query : null;
  const extra = extraHeaders && typeof extraHeaders === 'object' ? extraHeaders : null;
  const sig = signal || null;
  const timeoutRaw = Number(timeoutMs);
  const timeout =
    Number.isFinite(timeoutRaw) && timeoutRaw > 0 ? Math.max(1000, Math.floor(timeoutRaw)) : 0;

  if (!safeAction) throw new Error('action 不能为空');
  if (!safeSpider || !(/^\/spider\/|^\/[a-f0-9]{10}\/spider\//.test(safeSpider))) throw new Error('站点 API 无效');

  if (safeAction === 'search') await waitIfLowPriorityPaused();

  const normalizedBase = normalizecatpawrunnerApiBase(apiBase);
  if (!normalizedBase) throw new Error('catpawrunner 接口地址未设置');

  const spiderPath = safeSpider.endsWith('/') ? safeSpider.slice(0, -1) : safeSpider;
  const target = new URL(`${spiderPath}/${encodeURIComponent(safeAction)}`, normalizedBase);
  if (q) {
    Object.entries(q).forEach(([k, v]) => {
      const key = typeof k === 'string' ? k.trim() : '';
      if (!key) return;
      if (v == null) return;
      target.searchParams.set(key, String(v));
    });
  }
  const headers = { 'Content-Type': 'application/json', ...(extra ? extra : {}) };

  let controller = null;
  let timer = null;
  if (timeout || sig) {
    controller = new AbortController();
    if (sig) {
      try {
        if (sig.aborted) controller.abort();
        else sig.addEventListener('abort', () => controller.abort(), { once: true });
      } catch (_e) {}
    }
    if (timeout) {
      timer = setTimeout(() => {
        try {
          controller.abort();
        } catch (_e) {}
      }, timeout);
    }
  }

  let resp;
  try {
    resp = await fetch(target.toString(), {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
    credentials: 'omit',
    ...(controller ? { signal: controller.signal } : sig ? { signal: sig } : {}),
  });
  } catch (e) {
    if (timer) clearTimeout(timer);
    if (e && (e.name === 'AbortError' || e.code === 20)) {
      const err = new Error('请求超时');
      err.status = 408;
      err.code = 'ETIMEDOUT';
      throw err;
    }
    throw e;
  } finally {
    if (timer) clearTimeout(timer);
  }
  const status = resp && typeof resp.status === 'number' ? resp.status : 0;
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    const msg = data && data.message ? String(data.message) : '请求失败';
    const err = new Error(msg);
    err.status = status;
    throw err;
  }
  return data;
}

export async function requestCatPlay({ apiBase, username, payload, query, headers: extraHeaders, signal }) {
  const body = payload && typeof payload === 'object' ? payload : {};
  const q = query && typeof query === 'object' ? query : null;
  const extra = extraHeaders && typeof extraHeaders === 'object' ? extraHeaders : null;
  const sig = signal || null;

  const normalizedBase = normalizecatpawrunnerApiBase(apiBase);
  if (!normalizedBase) throw new Error('catpawrunner 接口地址未设置');

  const target = new URL('play', normalizedBase);
  if (q) {
    Object.entries(q).forEach(([k, v]) => {
      const key = typeof k === 'string' ? k.trim() : '';
      if (!key) return;
      if (v == null) return;
      target.searchParams.set(key, String(v));
    });
  }

  const headers = { 'Content-Type': 'application/json', ...(extra ? extra : {}) };
  const u = typeof username === 'string' ? username.trim() : '';
  if (u) headers['X-TV-User'] = u;

  const resp = await fetch(target.toString(), {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
    credentials: 'omit',
    ...(sig ? { signal: sig } : {}),
  });
  const status = resp && typeof resp.status === 'number' ? resp.status : 0;
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    const msg = data && data.message ? String(data.message) : '请求失败';
    const err = new Error(msg);
    err.status = status;
    throw err;
  }
  return data;
}

const splitEpisodeSegments = (value) =>
  normalizeString(value)
    .split('#')
    .map(normalizeString)
    .filter(Boolean);

const buildJoinedEpisodeSegments = (segments) =>
  (Array.isArray(segments) ? segments.map(normalizeString).filter(Boolean) : []).join('#');

export const normalizeSourceEntry = (item, index = 0) => {
  const source = item && typeof item === 'object' ? item : {};
  const label = normalizeString(source.label);
  if (!label) return null;
  const fallbackSourceValue = normalizeString(source.sourceValue)
    || normalizeString(source.baseUrl)
    || normalizeString(source.url);
  const episodeSegments = Array.isArray(source.episodeSegments)
    ? source.episodeSegments.map(normalizeString).filter(Boolean)
    : splitEpisodeSegments(fallbackSourceValue);
  const sourceValue = normalizeString(source.sourceValue)
    || normalizeString(source.baseUrl)
    || buildJoinedEpisodeSegments(episodeSegments);
  return {
    key: normalizeString(source.key) || `source:${index}:${label}`,
    label,
    provider: normalizeString(source.provider).toLowerCase(),
    sourceKind: normalizeString(source.sourceKind) || (normalizeString(source.provider) ? 'panmock' : 'normal'),
    groupIndex: Number.isFinite(Number(source.groupIndex)) ? Math.trunc(Number(source.groupIndex)) : index,
    sourceValue,
    episodeSegments,
    error: normalizeString(source.error),
    loading: source && source.loading === true,
  };
};

const detailCache = new Map();
const resolvedDetailCache = new Map();
const panListCache = new Map();
const panListResultByProviderFlag = new Map();
let detailCacheGeneration = 0;

const notifyResolvedDetailListeners = (cacheKey, data) => {
  const cached = resolvedDetailCache.get(cacheKey);
  if (!cached || !cached.listeners || !(cached.listeners instanceof Set) || !cached.listeners.size) return;
  Array.from(cached.listeners).forEach((listener) => {
    try {
      listener(data);
    } catch (_e) {}
  });
};

const readPanMockEnabledFromRaw = (raw) => {
  if (!raw || typeof raw !== 'object') return false;
  return raw.pan_mock === true;
};

const extractDetailVodObject = (raw) => {
  const root = raw && typeof raw === 'object' ? raw : {};
  if (hasDetailNavigation(root) && root.vod && typeof root.vod === 'object') return root.vod;
  const first = Array.isArray(root.list) && root.list[0] && typeof root.list[0] === 'object'
    ? root.list[0]
    : {};
  return first && typeof first === 'object' ? first : {};
};

export const extractCatDetailFields = (raw) => {
  const vod = extractDetailVodObject(raw);
  const get = (key) => (vod && vod[key] != null ? String(vod[key]) : '').trim();
  return {
    vod,
    title: get('vod_name'),
    poster: get('vod_pic'),
    year: get('vod_year'),
    type: get('vod_class'),
    remark: get('vod_remarks'),
    content: get('vod_content'),
    playFrom: get('vod_play_from'),
    playUrl: get('vod_play_url'),
    panMock: readPanMockEnabledFromRaw(raw),
  };
};

export const extractRawNamesFromEpisodeUrl = (episodeUrl) => {
  const raw = normalizeString(episodeUrl);
  if (!raw) return [];
  // Seven-field media metadata starts with a cover URL, not a pan share/file ID.
  // Its final field is a playback locator, not a filename (even if it contains E05).
  const fields = raw.split('*');
  if (fields.length === 7 && /^https?:\/\//i.test(fields[0].trim())) return [];
  const stripMeta = (value) => {
    let out = normalizeString(value);
    if (!out) return '';
    const dollarIdx = out.indexOf('$');
    if (dollarIdx > 0) out = out.slice(0, dollarIdx);
    out = out.replace(/#\[[^\]]*\]\s*$/g, '');
    out = out.replace(/\s*\[\s*\d+(?:\.\d+)?\s*(?:[KMGT]?B)\s*\]\s*$/gi, '');
    out = out.replace(/^【[^】]{1,16}】\s*/g, '');
    return out.trim();
  };
  const collectStar = () => {
    if (!raw.includes('***')) return [];
    const suffix = raw.split('***').slice(1).map(stripMeta).filter(Boolean);
    return suffix.length ? suffix : [];
  };
  const collectTriple = () => {
    if (!raw.includes('|||')) return [];
    const suffix = raw.split('|||').slice(1).map(stripMeta).filter(Boolean);
    return suffix.length ? suffix : [];
  };
  const collectPipeTail = () => {
    const parts = raw.split('|').map(stripMeta).filter(Boolean);
    if (parts.length >= 4) return [parts[parts.length - 1]];
    return [];
  };
  const picked = collectStar().length ? collectStar() : collectTriple().length ? collectTriple() : collectPipeTail();
  if (picked.length) return Array.from(new Set(picked));
  if (raw.includes('*')) {
    const parts = raw.split('*').map(stripMeta).filter(Boolean);
    if (parts.length) return [parts[parts.length - 1]];
  }
  return [];
};

export const extractPanListVodPlayUrl = (data) => {
  const root = data && typeof data === 'object' ? data : null;
  if (!root || root.ok !== true) return '';
  return typeof root.vod_play_url === 'string' ? String(root.vod_play_url || '').trim() : '';
};

const callPanList = async (provider, body, { signal } = {}) => {
  const routes = {
    quark: '/api/pan/quark/list',
    uc: '/api/pan/uc/list',
    baidu: '/api/pan/baidu/list',
    '139': '/api/pan/139/list',
    '189': '/api/pan/189/list',
  };
  const path = routes[provider] || '';
  if (!path) return null;
  const resp = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
    credentials: 'include',
    ...(signal ? { signal } : {}),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok || !data || data.ok === false) {
    const message = data && data.message ? String(data.message) : `HTTP ${resp.status}`;
    throw new Error(message);
  }
  return data && typeof data === 'object' ? data : null;
};

const panListIdentity = ({ provider, playFlag, passcode = '', shareUrl = '' } = {}) => {
  const share = getPanShareInput(playFlag, shareUrl);
  const key = normalizeString(provider).toLowerCase();
  if (!share || share.provider !== key) return null;
  const pass = normalizeString(passcode) || share.passcode;
  return { share, pass, key: key + '::' + share.key + '::' + pass };
};

export const getPanListCachedByProviderFlag = (input = {}) => {
  const identity = panListIdentity(input);
  return identity ? panListResultByProviderFlag.get(identity.key) || null : null;
};

export const setPanListCachedByProviderFlag = (input = {}) => {
  const identity = panListIdentity(input);
  if (identity && input.data && typeof input.data === 'object') panListResultByProviderFlag.set(identity.key, input.data);
};

export const requestPanListByProviderFlag = async ({ provider, playFlag, passcode = '', shareUrl = '', signal } = {}) => {
  if (signal && signal.aborted) return null;
  const generation = detailCacheGeneration;
  const key = normalizeString(provider).toLowerCase();
  const flag = normalizePanMockFlag(playFlag);
  const identity = panListIdentity({ provider: key, playFlag: flag, passcode, shareUrl });
  if (!identity) return null;
  const { share, pass, key: cacheKey } = identity;
  const body = { flag: share.url || flag };
  if (key === 'baidu') body.pwd = pass;
  else if (key === '189') { body.shareCode = share.shareId; body.accessCode = pass; }
  else body.passcode = pass;
  if (cacheKey && panListCache.has(cacheKey)) {
    const cached = panListCache.get(cacheKey);
    if (cached && cached.status === 'resolved') return cached.data;
    if (cached && cached.status === 'pending') {
      if (cached.consumers) cached.consumers.push(signal || null);
      return cached.promise;
    }
  }
  const stickyCached = getPanListCachedByProviderFlag({ provider: key, playFlag: flag, passcode: pass, shareUrl: share.url });
  if (stickyCached) {
    panListCache.set(cacheKey, { status: 'resolved', data: stickyCached });
    return stickyCached;
  }

  const consumers = [signal || null];
  const promise = runProviderListTask(`local:${key}`, () => {
    // Do not start a queued request nobody still needs. An active shared
    // request is not aborted merely because one of its consumers leaves.
    if (consumers.every(consumer => consumer && consumer.aborted)) throw new Error('请求已取消');
    return callPanList(key, body, { signal: null });
  }).then((data) => {
    if (generation === detailCacheGeneration) {
      panListCache.set(cacheKey, { status: 'resolved', data });
      setPanListCachedByProviderFlag({ provider: key, playFlag: flag, passcode: pass, shareUrl: share.url, data });
    }
    return data;
  }).catch((error) => {
    if (generation === detailCacheGeneration) panListCache.delete(cacheKey);
    throw error;
  });

  panListCache.set(cacheKey, { status: 'pending', promise, consumers });
  if (!signal || typeof signal.addEventListener !== 'function') return promise;
  if (signal.aborted) return null;
  return new Promise((resolve) => {
    let settled = false;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener('abort', onAbort);
      resolve(value || null);
    };
    const onAbort = () => finish(null);
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then((value) => finish(value)).catch(() => finish(null));
  });
};

const resolvePanMockPlaySources = async (raw, playFrom, playUrl, { onUpdate, signal } = {}) => {
  const panMock = readPanMockEnabledFromRaw(raw);
  const fromStr = normalizeString(playFrom);
  const urlStr = normalizeString(playUrl);
  if (!fromStr) {
    return { playFrom: fromStr, playUrl: urlStr, panMock, sources: [], panMock189AccessByShareId: {}, resolutionComplete: true };
  }

  const fromParts = fromStr.split('$$$');
  const urlParts = urlStr.split('$$$');
  const len = Math.max(fromParts.length, urlParts.length);
  const reqMap = new Map();
  const tianyiAccessByShareId = new Map();
  const sourceEntries = [];

  const cloneSources = () =>
    sourceEntries.map((item) => ({
      key: item.key,
      label: item.label,
      provider: item.provider,
      sourceKind: item.sourceKind,
      groupIndex: item.groupIndex,
      sourceValue: item.sourceValue,
      episodeSegments: Array.isArray(item.episodeSegments) ? item.episodeSegments.slice() : [],
      error: item.error,
      loading: !!item.loading,
    }));

  const buildResolvedOutput = (resolutionComplete) => {
    const grouped = new Map();
    sourceEntries.forEach((item) => {
      if (!grouped.has(item.groupIndex)) grouped.set(item.groupIndex, []);
      grouped.get(item.groupIndex).push(item);
    });
    const outFrom = [];
    const outUrl = [];
    for (let i = 0; i < len; i += 1) {
      const entries = grouped.has(i) ? grouped.get(i) : [];
      const nextFromSubs = [];
      const nextUrlSubs = [];
      entries.forEach((item) => {
        if (item.provider) {
          if (Array.isArray(item.episodeSegments) && item.episodeSegments.length) {
            nextFromSubs.push(item.label);
            nextUrlSubs.push(buildJoinedEpisodeSegments(item.episodeSegments));
          }
          return;
        }
        if (!Array.isArray(item.episodeSegments) || !item.episodeSegments.length) return;
        nextFromSubs.push(item.label);
        nextUrlSubs.push(buildJoinedEpisodeSegments(item.episodeSegments));
      });
      if (nextFromSubs.length && nextUrlSubs.length) {
        outFrom.push(nextFromSubs.join('|||'));
        outUrl.push(nextUrlSubs.join('|||'));
      }
    }
    return {
      playFrom: outFrom.join('$$$') || fromStr,
      playUrl: outUrl.join('$$$') || urlStr,
      panMock,
      sources: cloneSources(),
      panMock189AccessByShareId: Object.fromEntries(tianyiAccessByShareId.entries()),
      resolutionComplete: !!resolutionComplete,
    };
  };

  const emitUpdate = (resolutionComplete) => {
    if (typeof onUpdate !== 'function') return;
    try {
      onUpdate(buildResolvedOutput(resolutionComplete));
    } catch (_e) {}
  };

  for (let i = 0; i < len; i += 1) {
    const baseLabel = normalizeString(fromParts[i]);
    const baseUrl = normalizeString(urlParts[i]);
    if (!baseLabel) continue;
    const hasSubs = baseLabel.includes('|||') && baseUrl.includes('|||');
    const fromSubs = baseLabel.includes('|||') ? baseLabel.split('|||').map(normalizeString) : [baseLabel];
    const urlSubs = hasSubs ? baseUrl.split('|||').map(normalizeString) : [baseUrl];
    const subLen = Math.max(fromSubs.length, urlSubs.length);
    for (let j = 0; j < subLen; j += 1) {
      const label = normalizePanMockFlag(normalizeString(fromSubs[j]) || baseLabel);
      const urlSeg = normalizeString(urlSubs[j]);
      if (!label) continue;
      const share = panMock ? getPanShareInput(label, urlSeg) : null;
      // This existing field selects local list/play. A Runner-provided complete
      // list stays on the existing remote path; its label still identifies pan.
      const provider = share ? share.provider : '';
      const localList = !!share;
      const requestKey = share ? share.key + '::' + share.passcode : '';
      sourceEntries.push({
        key: i + ':' + j + ':' + (share ? share.key : label),
        label, provider,
        sourceKind: localList ? 'panmock' : 'normal',
        sourceValue: urlSeg,
        episodeSegments: localList ? [] : splitEpisodeSegments(urlSeg),
        error: '', loading: localList, groupIndex: i, requestKey,
      });
      if (!localList) continue;
      reqMap.set(requestKey, { provider, label, playFlag: label, shareUrl: share.url,
        passcode: share.passcode, shareId: share.shareId, resolveKey: requestKey });
    }
  }

  if (!panMock || !reqMap.size) return buildResolvedOutput(true);

  emitUpdate(false);

  await Promise.allSettled(
    Array.from(reqMap.values()).map(async ({ provider, playFlag, passcode, shareUrl, shareId, resolveKey }) => {
      try {
        const data = await requestPanListByProviderFlag({ provider, playFlag, passcode, shareUrl, signal });
        const vod = extractPanListVodPlayUrl(data);
        if (vod) {
          sourceEntries.forEach((item) => {
            if (item.requestKey !== resolveKey) return;
            item.episodeSegments = splitEpisodeSegments(vod);
            item.error = '';
            item.loading = false;
          });
          if (provider === '189' && passcode) {
            tianyiAccessByShareId.set(shareId, passcode);
            if (data.shareId) tianyiAccessByShareId.set(String(data.shareId), passcode);
          }
          emitUpdate(false);
          return;
        }
        sourceEntries.forEach((item) => {
          if (item.requestKey !== resolveKey) return;
          item.episodeSegments = [];
          item.error = '暂无数据';
          item.loading = false;
        });
        emitUpdate(false);
      } catch (error) {
        sourceEntries.forEach((item) => {
          if (item.requestKey !== resolveKey) return;
          item.episodeSegments = [];
          item.error = error && error.message ? String(error.message) : '请求失败';
          item.loading = false;
        });
        emitUpdate(false);
      }
    })
  );

  return buildResolvedOutput(true);
};

const buildDetailCacheKey = ({ apiBase, spiderApi, siteDetail, action = 'detail', payload }) =>
  `${normalizecatpawrunnerApiBase(apiBase)}::${normalizeString(spiderApi)}::${navigationRequestKey(action, payload || { id: siteDetail })}`;

export const fetchCatDetailCached = async ({ apiBase, spiderApi, siteDetail, action = 'detail', payload, timeoutMs = 15000, signal } = {}) => {
  const generation = detailCacheGeneration;
  const cacheKey = buildDetailCacheKey({ apiBase, spiderApi, siteDetail, action, payload });
  if (!cacheKey.includes('::') || (!payload && !normalizeString(siteDetail)) || !normalizeString(spiderApi)) {
    throw new Error('站点详情参数无效');
  }
  const cached = detailCache.get(cacheKey);
  if (cached && cached.status === 'resolved') return cached.data;
  if (cached && cached.status === 'pending') return cached.promise;

  const promise = requestCatSpider({
    apiBase,
    action,
    spiderApi,
    payload: payload || { id: siteDetail },
    timeoutMs,
    signal,
  }).then((raw) => {
    if (raw && raw.ok === false) throw new Error(raw.message || raw.msg || '详情请求失败');
    if (generation === detailCacheGeneration) detailCache.set(cacheKey, { status: 'resolved', data: raw });
    return raw;
  }).catch((error) => {
    if (generation === detailCacheGeneration) detailCache.delete(cacheKey);
    throw error;
  });

  detailCache.set(cacheKey, { status: 'pending', promise });
  return promise;
};

export const fetchCatResolvedDetailCached = async ({ apiBase, spiderApi, siteDetail, timeoutMs = 15000, onUpdate, signal } = {}) => {
  const generation = detailCacheGeneration;
  const cacheKey = buildDetailCacheKey({ apiBase, spiderApi, siteDetail });
  if (!cacheKey.includes('::') || !normalizeString(siteDetail) || !normalizeString(spiderApi)) {
    throw new Error('站点详情参数无效');
  }
  const cached = resolvedDetailCache.get(cacheKey);
  if (cached && cached.status === 'resolved') {
    if (typeof onUpdate === 'function' && cached.data) {
      try {
        onUpdate(cached.data);
      } catch (_e) {}
    }
    return cached.data;
  }
  if (cached && cached.status === 'pending') {
    if (typeof onUpdate === 'function') {
      cached.listeners.add(onUpdate);
      if (cached.data) {
        try {
          onUpdate(cached.data);
        } catch (_e) {}
      }
    }
    return cached.promise;
  }

  const listeners = new Set();
  if (typeof onUpdate === 'function') listeners.add(onUpdate);

  const promise = (async () => {
    const raw = await fetchCatDetailCached({ apiBase, spiderApi, siteDetail, timeoutMs, signal });
    const detail = extractCatDetailFields(raw);
    const baseData = {
      raw,
      ...detail,
      sources: [],
      panMock189AccessByShareId: {},
      resolutionComplete: !detail.panMock && !hasDetailNavigation(raw),
    };
    const emitPartial = (partial) => {
      if (generation !== detailCacheGeneration) return;
      const current = resolvedDetailCache.get(cacheKey);
      const nextData = {
        ...(current && current.data && typeof current.data === 'object' ? current.data : baseData),
        ...partial,
      };
      const nextEntry = resolvedDetailCache.get(cacheKey);
      if (nextEntry) nextEntry.data = nextData;
      notifyResolvedDetailListeners(cacheKey, nextData);
    };
    let resolved;
    const navigationErrors = [];
    if (hasDetailNavigation(raw)) {
      const leaves = new Map();
      let seq = 0;
      const snapshot = (complete) => {
        const sources = [];
        const access = {};
        leaves.forEach((value, key) => {
          Object.assign(access, value.panMock189AccessByShareId || {});
          (value.sources || []).forEach(source => sources.push({
            ...source, key: `${key}:${source.key}`, groupIndex: sources.length,
          }));
        });
        navigationErrors.forEach((error, index) => sources.push({
          key: `navigation-error:${index}`, label: normalizeString(error.item && error.item.vod_name) || '详情导航',
          sourceKind: 'normal', provider: '', sourceValue: '', episodeSegments: [],
          error: error.message, loading: false, groupIndex: sources.length,
        }));
        return { sources, panMock189AccessByShareId: access, resolutionComplete: complete, navigationErrors: navigationErrors.slice() };
      };
      emitPartial(snapshot(false));
      await walkDetailNavigation(raw, {
        signal, ownerKey: normalizecatpawrunnerApiBase(apiBase),
        shareIdentity: nav => {
          const share = getPanShareInput(nav.share_flag, nav.share_url);
          return share ? share.key : nav.share_url;
        },
        request: (action, payload) => fetchCatDetailCached({ apiBase, spiderApi, action, payload, timeoutMs, signal }),
        visit: async leaf => {
          const key = seq++;
          const fields = extractCatDetailFields(leaf);
          const update = value => { leaves.set(key, value); emitPartial(snapshot(false)); };
          const value = await resolvePanMockPlaySources(leaf, fields.playFrom, fields.playUrl, { onUpdate: update, signal });
          update(value);
        },
        onError: error => { navigationErrors.push(error); emitPartial(snapshot(false)); },
      });
      resolved = snapshot(!(signal && signal.aborted));
    } else {
      resolved = await resolvePanMockPlaySources(raw, detail.playFrom, detail.playUrl, { onUpdate: emitPartial, signal });
    }
    const data = {
      ...baseData,
      // playFrom/playUrl stay as input-compatibility fields; sources is the only
      // normalized runtime source model consumed by playback flows.
      sources: Array.isArray(resolved.sources) ? resolved.sources.map((item, index) => normalizeSourceEntry(item, index)).filter(Boolean) : [],
      panMock189AccessByShareId:
        resolved && resolved.panMock189AccessByShareId && typeof resolved.panMock189AccessByShareId === 'object'
          ? resolved.panMock189AccessByShareId
          : {},
      navigationErrors,
      resolutionComplete: resolved.resolutionComplete !== false,
    };
    if (generation === detailCacheGeneration) {
      notifyResolvedDetailListeners(cacheKey, data);
      if (data.resolutionComplete) resolvedDetailCache.set(cacheKey, { status: 'resolved', data, listeners: new Set() });
      else resolvedDetailCache.delete(cacheKey);
    }
    return data;
  })().catch((error) => {
    if (generation === detailCacheGeneration) resolvedDetailCache.delete(cacheKey);
    throw error;
  });

  resolvedDetailCache.set(cacheKey, { status: 'pending', promise, data: null, listeners });
  return promise;
};

export const clearCatDetailCache = () => {
  detailCacheGeneration += 1;
  detailCache.clear();
  resolvedDetailCache.clear();
  panListCache.clear();
  panListResultByProviderFlag.clear();
};
