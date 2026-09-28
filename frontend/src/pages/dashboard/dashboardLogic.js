import { requestJsonResponse } from '../../shared/requestJson';
import { normalizeHttpBase as sharedNormalizeHttpBase } from '../../shared/urlText';

const jsonHeaders = {
  Accept: 'application/json'
};

export async function requestJson(url, options = {}) {
  return requestJsonResponse(url, {
    credentials: 'include',
    headers: {
      Accept: 'application/json',
      ...(options.headers || {})
    },
    ...options
  });
}

export async function probeGoProxyVersion(base, timeoutMs = 4000) {
  const normalized = normalizeHttpBase(base);
  if (!normalized) throw new Error('GoProxy 接口地址无效');
  const target = new URL('version', `${normalized}/`).toString();
  const ms = Number.isFinite(Number(timeoutMs)) ? Math.max(0, Math.trunc(Number(timeoutMs))) : 0;
  const controller = ms > 0 && typeof AbortController !== 'undefined' ? new AbortController() : null;
  const timer = controller ? setTimeout(() => controller.abort(), ms) : null;
  try {
    const { resp, data } = await requestJson(target, {
      method: 'GET',
      credentials: 'omit',
      signal: controller ? controller.signal : undefined
    });
    if (!resp || !resp.ok) {
      throw new Error((data && (data.message || data.error)) || `HTTP ${resp ? resp.status : 500}`);
    }
    return {
      ok: true,
      version: data && typeof data.version === 'string' ? data.version.trim() : ''
    };
  } finally {
    if (timer) clearTimeout(timer);
  }
}

export async function probeRelayVersion(base, secret, timeoutMs = 4000) {
  const normalized = normalizeHttpBase(base);
  if (!normalized) throw new Error('函数接口地址无效');
  const target = new URL('version', `${normalized}/`);
  target.searchParams.set('secret', typeof secret === 'string' ? secret.trim() : '');
  const ms = Number.isFinite(Number(timeoutMs)) ? Math.max(0, Math.trunc(Number(timeoutMs))) : 0;
  const controller = ms > 0 && typeof AbortController !== 'undefined' ? new AbortController() : null;
  const timer = controller ? setTimeout(() => controller.abort(), ms) : null;
  try {
    const { resp, data } = await requestJson(target.toString(), {
      method: 'GET',
      credentials: 'omit',
      signal: controller ? controller.signal : undefined
    });
    if (!resp || !resp.ok) {
      throw new Error((data && (data.message || data.error)) || `HTTP ${resp ? resp.status : 500}`);
    }
    return {
      ok: true,
      version: data && typeof data.version === 'string' ? data.version.trim() : ''
    };
  } finally {
    if (timer) clearTimeout(timer);
  }
}

export async function getSuccessJson(url) {
  const { resp, data } = await requestJsonResponse(url, {
    credentials: 'include',
    headers: jsonHeaders
  });
  if (!resp.ok || !data || data.success !== true) {
    throw new Error((data && (data.message || data.error)) || `HTTP ${resp.status}`);
  }
  return data;
}

export async function postForm(url, fields) {
  const body = new URLSearchParams();
  Object.entries(fields || {}).forEach(([key, value]) => {
    body.set(key, value == null ? '' : String(value));
  });
  const { resp, data } = await requestJsonResponse(url, {
    method: 'POST',
    credentials: 'include',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8'
    },
    body
  });
  if (!resp.ok || !data || data.success !== true) {
    throw new Error((data && (data.message || data.error)) || `HTTP ${resp.status}`);
  }
  return data;
}

export async function postJson(url, payload) {
  const { resp, data } = await requestJsonResponse(url, {
    method: 'POST',
    credentials: 'include',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload == null ? {} : payload)
  });
  if (!resp.ok || !data || data.success !== true) {
    throw new Error((data && (data.message || data.error)) || `HTTP ${resp.status}`);
  }
  return data;
}

export async function postJsonLoose(url, payload) {
  return requestJson(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload == null ? {} : payload)
  });
}

export async function fetchSiteSettings() {
  return getSuccessJson('/dashboard/site/settings');
}

function normalizeCatpawrunnerServerRow(server) {
  const row = server && typeof server === 'object' ? server : {};
  return {
    name: typeof row.name === 'string' ? row.name.trim() : '',
    apiBase: typeof row.apiBase === 'string' ? row.apiBase.trim() : ''
  };
}

function pickCatpawrunnerActiveKey(servers, desired) {
  const list = Array.isArray(servers) ? servers : [];
  const wanted = typeof desired === 'string' ? desired.trim() : '';
  if (wanted) {
    const hit = list.find((item) => item && item.name === wanted);
    if (hit) return hit.name;
  }
  return list[0] ? list[0].name : '';
}

function resolveCatpawrunnerApiBaseFromSettings(settings) {
  const servers = Array.isArray(settings && settings.catpawrunnerServers)
    ? settings.catpawrunnerServers.map(normalizeCatpawrunnerServerRow).filter((item) => item.name)
    : [];
  const active = settings && typeof settings.catpawrunnerActive === 'string'
    ? settings.catpawrunnerActive
    : settings && typeof settings.CatpawrunnerActive === 'string'
      ? settings.CatpawrunnerActive
      : '';
  const key = pickCatpawrunnerActiveKey(servers, active);
  const server = servers.find((item) => item && item.name === key) || servers[0];
  return normalizeHttpBase(server && server.apiBase ? server.apiBase : '');
}

const catpawrunnerApiBaseCache = {
  value: '',
  at: 0,
  inFlight: null
};

export async function resolveDashboardCatpawrunnerApiBase(currentApiBase = '', ttlMs = 10 * 1000) {
  const direct = normalizeHttpBase(currentApiBase);
  if (direct) {
    catpawrunnerApiBaseCache.value = direct;
    catpawrunnerApiBaseCache.at = Date.now();
    return direct;
  }
  const now = Date.now();
  if (catpawrunnerApiBaseCache.value && now - catpawrunnerApiBaseCache.at < ttlMs) {
    return catpawrunnerApiBaseCache.value;
  }
  if (catpawrunnerApiBaseCache.inFlight) {
    return await catpawrunnerApiBaseCache.inFlight;
  }
  catpawrunnerApiBaseCache.inFlight = (async () => {
    try {
      const settings = await fetchSiteSettings();
      const base = resolveCatpawrunnerApiBaseFromSettings(settings);
      catpawrunnerApiBaseCache.value = base;
      catpawrunnerApiBaseCache.at = Date.now();
      return base;
    } catch (_e) {
      catpawrunnerApiBaseCache.value = '';
      catpawrunnerApiBaseCache.at = Date.now();
      return '';
    }
  })();
  try {
    return await catpawrunnerApiBaseCache.inFlight;
  } finally {
    catpawrunnerApiBaseCache.inFlight = null;
  }
}

export async function fetchMetadataSettings() {
  return getSuccessJson('/dashboard/metadata/settings');
}

export async function saveMetadataSettings(payload) {
  return postJson('/dashboard/metadata/settings', payload);
}

export async function clearMetadataCache(scope) {
  const target = typeof scope === 'string' ? scope.trim().toLowerCase() : '';
  if (target !== 'douban' && target !== 'tmdb' && target !== 'all') {
    throw new Error('缓存清理类型无效');
  }
  return postJson('/dashboard/metadata/cache/clear', { scope: target });
}

export async function clearDoubanMetadataCache() {
  return clearMetadataCache('douban');
}

export async function clearTMDBMetadataCache() {
  return clearMetadataCache('tmdb');
}

export async function clearAllMetadataCache() {
  return clearMetadataCache('all');
}

export async function fetchMagicSettings() {
  return getSuccessJson('/dashboard/magic/settings');
}

export async function saveMagicSettings(payload) {
  return postJson('/dashboard/magic/settings', payload);
}

export async function fetchSmartSettings() {
  return getSuccessJson('/dashboard/smart/settings');
}

export async function fetchThirdpartySettings() {
  return getSuccessJson('/dashboard/thirdparty/settings');
}

export async function saveThirdpartySettings(payload) {
  const sections = Array.isArray(payload?.thirdPartyClientHomeSections)
    ? payload.thirdPartyClientHomeSections
    : (Array.isArray(payload?.embyHomeSections) ? payload.embyHomeSections : []);
  return postForm('/dashboard/thirdparty/save', {
    thirdPartyClientHomeSectionsJson: JSON.stringify(sections)
  });
}

export async function fetchThirdpartySiteCategories(siteKey) {
  return getSuccessJson(`/dashboard/thirdparty/site/categories?siteKey=${encodeURIComponent(String(siteKey || ''))}`);
}

export async function saveSmartSettings(payload) {
  return postJson('/dashboard/smart/settings', payload);
}

export async function fetchSmartMatchBlockKeywords() {
  return getSuccessJson('/dashboard/smart/matchblock/keywords');
}

export async function fetchSmartMatchBlockItems(keyword) {
  return getSuccessJson(`/dashboard/smart/matchblock/items?keyword=${encodeURIComponent(String(keyword || ''))}`);
}

export async function deleteSmartMatchBlockItem(payload) {
  return postJson('/dashboard/smart/matchblock/delete', payload);
}

export async function deleteSmartMatchBlockKeyword(payload) {
  return postJson('/dashboard/smart/matchblock/keyword/delete', payload);
}

export async function fetchDashboardSmartManualTMDBItems() {
  return getSuccessJson('/api/smart/manual/tmdb/get-list');
}

export async function addDashboardSmartManualTMDBItem(payload) {
  return postJson('/api/smart/manual/tmdb/add', payload);
}

export async function deleteDashboardSmartManualTMDBItem(payload) {
  return postJson('/api/smart/manual/tmdb/delete', payload);
}

export async function fetchDashboardSmartManualItems(tmdbType, tmdbId) {
  const type = String(tmdbType || '').trim().toLowerCase();
  const id = String(tmdbId || '').trim();
  return getSuccessJson(`/api/smart/manual/item/get-data?tmdbType=${encodeURIComponent(type)}&tmdbId=${encodeURIComponent(id)}`);
}

export async function addDashboardSmartManualItem(payload) {
  return postJson('/api/smart/manual/item/add', payload);
}

export async function updateDashboardSmartManualItem(payload) {
  return postJson('/api/smart/manual/item/update', payload);
}

export async function deleteDashboardSmartManualItem(payload) {
  return postJson('/api/smart/manual/item/delete', payload);
}

export async function fetchUsers() {
  return getSuccessJson('/dashboard/user/list');
}

export async function fetchPanSettings(key = '') {
  const suffix = key ? `?key=${encodeURIComponent(String(key))}` : '';
  return getSuccessJson(`/dashboard/pan/settings${suffix}`);
}

export const normalizeHttpBase = (value) => sharedNormalizeHttpBase(value);

export async function savePanLoginSettings(payload) {
  return postForm('/dashboard/pan/settings', payload);
}

export async function startPanQrLogin(provider) {
  return postJson(`/dashboard/pan/${encodeURIComponent(String(provider || ''))}/start`, {});
}

export async function pollPanQrLogin(provider, qid) {
  return postJsonLoose(`/dashboard/pan/${encodeURIComponent(String(provider || ''))}/cookie`, { qid });
}

export async function saveGlobalSiteSettings(payload) {
  return postForm('/dashboard/site/save', payload);
}

export async function saveDashboardCatpawrunnerServer(payload) {
  return postForm('/dashboard/catpawrunner/save', payload);
}

export async function deleteDashboardCatpawrunnerServer(payload) {
  return postForm('/dashboard/catpawrunner/delete', payload);
}

export async function saveDashboardGoProxySettings(payload) {
  return postForm('/dashboard/goproxy/save', payload);
}

export async function saveDashboardRelaySettings(payload) {
  return postForm('/dashboard/relay/save', payload);
}

export async function fetchDashboardVideoPans() {
  return getSuccessJson('/dashboard/video/pans/list');
}

export async function saveDashboardVideoPans(list) {
  return postForm('/dashboard/video/pans/list', {
    list: JSON.stringify(Array.isArray(list) ? list : [])
  });
}

export async function fetchDashboardVideoSourceSites() {
  return getSuccessJson('/dashboard/video/source/sites');
}

export async function importDashboardVideoSourceSites(list) {
  return postForm('/dashboard/video/source/sites/import', {
    sites: JSON.stringify(Array.isArray(list) ? list : [])
  });
}

export async function updateDashboardVideoSourceStatus(key, enabled) {
  return postForm('/dashboard/video/source/sites/status', {
    key,
    enabled: enabled ? '1' : '0'
  });
}

export async function updateDashboardVideoSourceHome(key, home) {
  return postForm('/dashboard/video/source/sites/home', {
    key,
    home: home ? '1' : '0'
  });
}

export async function updateDashboardVideoSourceSearch(key, search) {
  return postForm('/dashboard/video/source/sites/search', {
    key,
    search: search ? '1' : '0'
  });
}

export async function updateDashboardVideoSourceCover(key) {
  return postForm('/dashboard/video/source/sites/cover', { key });
}

export async function saveDashboardVideoSourceOrder(orderKeys) {
  return postForm('/dashboard/video/source/sites/order', {
    order: JSON.stringify(Array.isArray(orderKeys) ? orderKeys : [])
  });
}

export async function checkDashboardVideoSourceSites(results, errors = {}) {
  return postForm('/dashboard/video/source/sites/check', {
    results: JSON.stringify(results && typeof results === 'object' ? results : {}),
    errors: JSON.stringify(errors && typeof errors === 'object' ? errors : {})
  });
}

export async function applyDashboardVideoSourceAutoResults(sites) {
  const nextSites = Array.isArray(sites) ? sites : [];
  const skippedKeys = nextSites
    .filter((site) => shouldSkipDashboardVideoSourceCheck(site))
    .map((site) => site.key)
    .filter(Boolean);
  const basesetKeys = nextSites
    .filter((site) => {
      const key = typeof site?.key === 'string' ? site.key : '';
      const api = typeof site?.api === 'string' ? site.api : '';
      return key && api && !shouldSkipDashboardVideoSourceCheck(site) && /^\/(?:[a-f0-9]{10}\/)?spider\/baseset(?:\/|$)/.test(api);
    })
    .map((site) => site.key)
    .filter(Boolean);
  if (!basesetKeys.length && !skippedKeys.length) {
    return {
      sites: nextSites,
      results: {}
    };
  }
  const results = {};
  basesetKeys.forEach((key) => {
    results[key] = 'valid';
  });
  skippedKeys.forEach((key) => {
    results[key] = 'skipped';
  });
  const checked = await checkDashboardVideoSourceSites(results);
  return {
    sites: Array.isArray(checked && checked.sites) ? checked.sites : nextSites,
    results: checked && typeof checked.results === 'object' ? checked.results : results
  };
}

function normalizeSiteNameForMatch(value) {
  const raw = value != null ? String(value) : '';
  if (!raw) return '';
  let normalized = raw.replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\uFE0F/g, '');
  try {
    normalized = normalized.replace(/[\u{1F300}-\u{1FAFF}]/gu, '');
  } catch (_e) {}
  normalized = normalized.replace(/[^\p{L}\p{N}\u4e00-\u9fff]+/gu, '');
  return normalized.trim();
}

function normalizeAvailability(value) {
  const raw = typeof value === 'string' ? value.trim().toLowerCase() : '';
  if (raw === 'valid') return 'valid';
  if (raw === 'invalid') return 'invalid';
  if (raw === 'category_error') return 'category_error';
  if (raw === 'search_error') return 'search_error';
  if (raw === 'skipped') return 'skipped';
  return 'unchecked';
}

function normalizePanMockPasscode(raw) {
  let value = typeof raw === 'string' ? raw.trim() : '';
  if (!value) return '';
  value = value.replace(/\s*\[[^\]]*]\s*$/g, '').trim();
  if (value.toLowerCase().endsWith('.mp4')) value = value.slice(0, -4).trim();
  if (value.toLowerCase().endsWith('-nopass')) value = value.slice(0, -7).trim();
  if (value.toLowerCase().endsWith('_nopass')) value = value.slice(0, -7).trim();
  if (!value) return '';
  const lower = value.toLowerCase();
  if (lower === 'nopass' || lower === 'none' || value === '无密码') return '';
  const firstToken = String(value.split(/\s+/)[0] || '').trim();
  return firstToken || '';
}

function panMockProviderFromFlag(flag) {
  const value = typeof flag === 'string' ? flag.trim() : '';
  if (!value) return '';
  const headSeg = (value.split('-')[0] || '').trim();
  if (!headSeg) return '';
  const head2 = Array.from(headSeg).slice(0, 2).join('');
  if (head2 === '天意') return '189';
  if (head2 === '逸动') return '139';
  if (head2 === '夸父') return 'quark';
  if (head2 === '优夕') return 'uc';
  if (head2 === '百度') return 'baidu';
  return '';
}

function extractTianyiShareCodeLike(flagOrURL) {
  const value = typeof flagOrURL === 'string' ? flagOrURL.trim() : '';
  if (!value) return '';
  const match1 = value.match(/天意-([A-Za-z0-9]{6,64})/);
  if (match1 && match1[1]) return String(match1[1]).trim();
  const match2 = value.match(/^https?:\/\/cloud\.189\.cn\/t\/([A-Za-z0-9]{6,64})(?:\b|\/|$)/i);
  if (match2 && match2[1]) return String(match2[1]).trim();
  const match3 = value.match(/^[A-Za-z0-9]{6,64}$/);
  if (match3) return value;
  return '';
}

function deriveTianyiMockMeta(flag, rawPasscode) {
  const out = { shareCode: '', accessCode: '' };
  const fromFlag = extractTianyiShareCodeLike(flag);
  if (fromFlag) out.shareCode = fromFlag;
  const pass = normalizePanMockPasscode(rawPasscode);
  if (!pass) return out;
  if (pass.toLowerCase() === 'nopass') return out;
  const looksShare = (v) => /^[A-Za-z0-9]{6,64}$/.test(String(v || '').trim());
  if (pass.includes('-')) {
    const parts = pass.split('-', 2).map((item) => String(item || '').trim());
    const left = String(parts[0] || '').trim();
    const right = String(parts[1] || '').trim();
    if (!out.shareCode && looksShare(left)) out.shareCode = left;
    if (right && right.toLowerCase() !== 'nopass') out.accessCode = right;
    return out;
  }
  if (!out.shareCode && looksShare(pass)) {
    out.shareCode = pass;
    return out;
  }
  out.accessCode = pass;
  return out;
}

function derivePanMockPasscode(siteEpisodeFile, episodeID) {
  const nameRaw = typeof siteEpisodeFile === 'string' ? siteEpisodeFile.trim() : '';
  if (nameRaw) return normalizePanMockPasscode(nameRaw);
  const idRaw = typeof episodeID === 'string' ? episodeID.trim() : '';
  if (idRaw && idRaw.includes('***')) {
    return normalizePanMockPasscode(String(idRaw.split('***').pop() || '').trim());
  }
  return '';
}

function extractSpiderNameFromApi(api) {
  const raw = typeof api === 'string' ? api.trim() : '';
  if (!raw) return '';
  try {
    const url = raw.startsWith('http://') || raw.startsWith('https://') ? new URL(raw) : null;
    const pathname = url ? url.pathname : raw;
    if (pathname.indexOf('/spider/') === 0) {
      const parts = pathname.split('/').filter(Boolean);
      return parts.length >= 2 ? parts[1] : '';
    }
  } catch (_e) {}
  if (raw.indexOf('/spider/') === 0) {
    const parts = raw.split('/').filter(Boolean);
    return parts.length >= 2 ? parts[1] : '';
  }
  return '';
}

function shouldSkipDashboardVideoSourceCheck(site) {
  const name = normalizeSiteNameForMatch(site && site.name);
  const api = typeof site?.api === 'string' ? site.api : '';
  const apiLower = api.trim().toLowerCase();
  if (name === '豆瓣首页' && apiLower.includes('douban')) return true;
  if (name === '配置中心' && apiLower.includes('baseset')) return true;
  return false;
}

function isMyPanSite(site) {
  const name = normalizeSiteNameForMatch(site && site.name);
  if (!name || !name.includes('我的')) return false;
  const keys = ['夸克', '百度', '天逸', '115', '123', 'quark', 'baidu'];
  const lower = name.toLowerCase();
  return keys.some((k) => (/[a-z]/i.test(k) ? lower.includes(k) : name.includes(k)));
}

function formatHttpError(err) {
  const status = err && typeof err.status === 'number' ? err.status : 0;
  const msg = err && err.message ? String(err.message) : '请求失败';
  if (!status) return msg;
  if (msg.startsWith(`HTTP ${status}`)) return msg;
  return `HTTP ${status}：${msg}`;
}

function isFatalHttpProbeError(err) {
  const status = err && typeof err.status === 'number' ? err.status : 0;
  const msg = err && err.message ? String(err.message) : '';
  if (status === 403 || status === 404) return true;
  if (status === 500 && /ECONNREFUSED/i.test(msg)) return true;
  return false;
}

function extractList(data) {
  if (!data) return [];
  if (Array.isArray(data)) return data;
  if (Array.isArray(data.list)) return data.list;
  if (data.data && Array.isArray(data.data)) return data.data;
  if (data.data && Array.isArray(data.data.list)) return data.data.list;
  if (data.data && Array.isArray(data.data.vod_list)) return data.data.vod_list;
  if (data.data && Array.isArray(data.data.items)) return data.data.items;
  return [];
}

function extractClasses(data) {
  if (!data) return [];
  if (Array.isArray(data.class)) return data.class;
  if (Array.isArray(data.classes)) return data.classes;
  if (data.data && Array.isArray(data.data.class)) return data.data.class;
  if (data.data && Array.isArray(data.data.classes)) return data.data.classes;
  if (data.data && Array.isArray(data.data.types)) return data.data.types;
  if (Array.isArray(data.types)) return data.types;
  return [];
}

function extractClassId(c) {
  if (!c) return '';
  const pick = (v) => (v != null ? String(v).trim() : '');
  return pick(c.type_id) || pick(c.tid) || pick(c.id) || pick(c.typeId) || '';
}

function normalizeStatusCode(resp) {
  if (resp && typeof resp.statusCode === 'number') return resp.statusCode;
  if (resp && resp.data && typeof resp.data.statusCode === 'number') return resp.data.statusCode;
  return 0;
}

function normalizeMessage(resp) {
  if (resp && typeof resp.message === 'string') return resp.message;
  if (resp && typeof resp.msg === 'string') return resp.msg;
  if (resp && resp.data && typeof resp.data.message === 'string') return resp.data.message;
  return '';
}

function readPanMockFlag(resp) {
  if (!resp || typeof resp !== 'object') return false;
  if (resp.pan_mock === true) return true;
  if (resp.data && typeof resp.data === 'object' && resp.data.pan_mock === true) return true;
  return false;
}

function parsePlayCandidates(fromStr, urlStr) {
  const fromRaw = typeof fromStr === 'string' ? fromStr : '';
  const urlRaw = typeof urlStr === 'string' ? urlStr : '';
  const splitTop = (s) => (s ? s.split('$$$') : []);
  const fromParts = splitTop(fromRaw);
  const urlParts = splitTop(urlRaw);
  const len = Math.max(fromParts.length, urlParts.length);
  const out = [];
  const seen = new Set();
  for (let i = 0; i < len; i += 1) {
    const baseLabel = String(fromParts[i] || '').trim() || `源${i + 1}`;
    const baseURL = String(urlParts[i] || '').trim();
    if (!baseURL) continue;
    const fromSubs = baseLabel.includes('|||')
      ? baseLabel.split('|||').map((x) => String(x || '').trim())
      : [baseLabel];
    const urlSubs = baseLabel.includes('|||') && baseURL.includes('|||')
      ? baseURL.split('|||').map((x) => String(x || '').trim())
      : [baseURL];
    const subLen = Math.max(fromSubs.length, urlSubs.length);
    for (let j = 0; j < subLen; j += 1) {
      const flag = String(fromSubs[j] || '').trim() || baseLabel;
      const urlBlock = String(urlSubs[j] || '').trim();
      if (!urlBlock) continue;
      const firstLine = String(urlBlock.split('#')[0] || '').trim();
      if (!firstLine) continue;
      const idx = firstLine.indexOf('$');
      const id = String(idx >= 0 ? firstLine.slice(idx + 1) : firstLine).trim();
      const siteEpisodeFile = String(idx >= 0 ? firstLine.slice(0, idx) : '').trim();
      const passcode = derivePanMockPasscode(siteEpisodeFile, id);
      if (!flag || !id) continue;
      const uniqKey = `${flag}@@${id}`;
      if (seen.has(uniqKey)) continue;
      seen.add(uniqKey);
      out.push({ flag, id, passcode });
    }
  }
  return out;
}

function extractPlayCandidatesFromVod(vod) {
  if (!vod || typeof vod !== 'object') return [];
  const from =
    (typeof vod.vod_play_from === 'string' ? vod.vod_play_from : '') ||
    (typeof vod.play_from === 'string' ? vod.play_from : '') ||
    (typeof vod.playFrom === 'string' ? vod.playFrom : '');
  const url =
    (typeof vod.vod_play_url === 'string' ? vod.vod_play_url : '') ||
    (typeof vod.play_url === 'string' ? vod.play_url : '') ||
    (typeof vod.playUrl === 'string' ? vod.playUrl : '');
  return parsePlayCandidates(from, url);
}

function extractVodId(vod) {
  if (!vod) return '';
  const pick = (v) => (v != null ? String(v).trim() : '');
  return pick(vod.vod_id) || pick(vod.vodId) || pick(vod.id) || pick(vod.ID) || '';
}

function extractPlayUrl(resp) {
  if (!resp) return '';
  const pickStr = (v) => (typeof v === 'string' ? v.trim() : '');
  const pickUrlLike = (v) => {
    const direct = pickStr(v);
    if (direct) return direct;
    if (!Array.isArray(v)) return '';
    for (let i = v.length - 1; i >= 0; i -= 1) {
      const item = pickStr(v[i]);
      if (item && /^https?:\/\//i.test(item)) return item;
    }
    for (let i = 0; i < v.length; i += 1) {
      const item = pickStr(v[i]);
      if (item) return item;
    }
    return '';
  };
  return pickUrlLike(resp.url) || pickUrlLike(resp.playUrl) || (resp.data && pickUrlLike(resp.data.url)) || '';
}

function parseFirstPanEpisodeID(vodPlayURL) {
  const raw = typeof vodPlayURL === 'string' ? vodPlayURL.trim() : '';
  if (!raw) return '';
  const block = String(raw.split('$$$')[0] || '').trim();
  if (!block) return '';
  const firstLine = String(block.split('#')[0] || '').trim();
  if (!firstLine) return '';
  const idx = firstLine.indexOf('$');
  if (idx < 0) return firstLine;
  return String(firstLine.slice(idx + 1) || '').trim();
}

async function fetchJsonSafe(url, options = {}) {
  const resp = await fetch(url, options);
  const data = await readJson(resp);
  return { resp, data };
}

async function callPanPlayResolver(provider, { flag = '', id = '', passcode = '' } = {}, tvUser = '') {
  const p = String(provider || '').trim();
  const panFlag = String(flag || '').trim();
  const panID = String(id || '').trim();
  const panPasscode = String(passcode || '').trim();
  if (!p || !panID) return '';
  const pathByProvider = {
    '189': '/api/pan/189/play',
    '139': '/api/pan/139/play',
    quark: '/api/pan/quark/play',
    uc: '/api/pan/uc/play',
    baidu: '/api/pan/baidu/play'
  };
  const target = pathByProvider[p] || '';
  if (!target) return '';
  const headers = { 'Content-Type': 'application/json' };
  const normalizedTvUser = normalizeTvUser(tvUser);
  if (normalizedTvUser) headers['X-TV-User'] = normalizedTvUser;
  const body = p === '189'
    ? (panPasscode ? { id: panID, accessCode: panPasscode } : { id: panID })
    : { flag: panFlag, id: panID };
  const { resp, data } = await fetchJsonSafe(target, {
    method: 'POST',
    credentials: 'include',
    headers,
    body: JSON.stringify(body)
  });
  if (!resp.ok || !data || data.ok === false) {
    const msg = data && data.message ? String(data.message) : `HTTP ${resp.status}`;
    const err = new Error(msg);
    try { err.status = resp.status; } catch (_e) {}
    throw err;
  }
  const pick = (v) => (typeof v === 'string' ? v.trim() : '');
  return pick(data.url) || pick(data.playUrl) || (data.data && (pick(data.data.url) || pick(data.data.playUrl))) || '';
}

async function callPanListResolver(provider, { flag = '', passcode = '' } = {}, tvUser = '') {
  const p = String(provider || '').trim();
  const panFlag = String(flag || '').trim();
  const rawPasscode = String(passcode || '').trim();
  const tianyiMeta = p === '189' ? deriveTianyiMockMeta(panFlag, rawPasscode) : null;
  const panPasscode = p === '189' ? String((tianyiMeta && tianyiMeta.accessCode) || '').trim() : rawPasscode;
  if (!p || !panFlag) return '';
  const pathByProvider = {
    '189': '/api/pan/189/list',
    '139': '/api/pan/139/list',
    quark: '/api/pan/quark/list',
    uc: '/api/pan/uc/list',
    baidu: '/api/pan/baidu/list'
  };
  const target = pathByProvider[p] || '';
  if (!target) return '';
  const headers = { 'Content-Type': 'application/json' };
  const normalizedTvUser = normalizeTvUser(tvUser);
  if (normalizedTvUser) headers['X-TV-User'] = normalizedTvUser;
  const listBody = { flag: panFlag };
  if (p === '189' && tianyiMeta && tianyiMeta.shareCode) {
    const sc = String(tianyiMeta.shareCode || '').trim();
    listBody.shareCode = sc;
    listBody.flag = `天意-${sc}`;
  }
  if (panPasscode) {
    if (p === '189') listBody.accessCode = panPasscode;
    if (p === '139' || p === 'quark' || p === 'uc') listBody.passcode = panPasscode;
    if (p === 'baidu') listBody.pwd = panPasscode;
  }
  const { resp, data } = await fetchJsonSafe(target, {
    method: 'POST',
    credentials: 'include',
    headers,
    body: JSON.stringify(listBody)
  });
  if (!resp.ok || !data || data.ok === false) {
    const msg = data && data.message ? String(data.message) : `HTTP ${resp.status}`;
    const err = new Error(msg);
    try { err.status = resp.status; } catch (_e) {}
    throw err;
  }
  const vodPlayURL = data && typeof data.vod_play_url === 'string' ? data.vod_play_url : '';
  return parseFirstPanEpisodeID(vodPlayURL);
}

function buildDashboardVideoSourceProbeResult(key, result, error = '', disableSearch = false) {
  return {
    key,
    result,
    error,
    disableSearch
  };
}

async function tryPlayCandidatesForVideoSource({ apiBase, spiderPath, items, tvUser, meta }) {
  const state = meta && typeof meta === 'object' ? meta : null;
  let panMockFlow = !!(state && state.panMock === true);
  let playErr = '';
  const candidates = (Array.isArray(items) ? items : []).filter((v) => v && typeof v === 'object').slice(0, 3);
  for (let j = 0; j < candidates.length; j += 1) {
    const vod = candidates[j];
    let playCandidates = extractPlayCandidatesFromVod(vod);
    try {
      let detailFetched = false;
      let detailOK = false;
      let detailCandidates = [];
      const loadDetailOnce = async () => {
        if (detailFetched) return detailOK;
        detailFetched = true;
        const vodId = extractVodId(vod);
        if (!vodId) return false;
        const detailResp = await requestCatpawrunnerAdminJson({
          apiBase,
          path: `${spiderPath}/detail`,
          method: 'POST',
          body: { id: vodId, vod_id: vodId },
          tvUser
        });
        const dsc = normalizeStatusCode(detailResp);
        if (dsc >= 400) return false;
        if (state && readPanMockFlag(detailResp)) {
          state.panMock = true;
          panMockFlow = true;
        }
        const detailList = extractList(detailResp);
        const first = Array.isArray(detailList) && detailList.length ? detailList[0] : null;
        detailCandidates = extractPlayCandidatesFromVod(first);
        detailOK = true;
        return true;
      };

      if (!playCandidates.length) {
        const ok = await loadDetailOnce();
        if (!ok) continue;
        playCandidates = detailCandidates;
      }

      if (!panMockFlow) {
        const hasPanProvider = playCandidates.some((candidate) => !!panMockProviderFromFlag(candidate && candidate.flag));
        if (hasPanProvider) {
          const ok = await loadDetailOnce();
          if (ok && detailCandidates.length) playCandidates = detailCandidates;
        }
      }

      if (!playCandidates.length) {
        if (state) state.missingPlayMeta = true;
        if (!playErr) playErr = '缺少播放信息';
        continue;
      }

      const orderedCandidates = playCandidates.slice();
      for (let k = 0; k < orderedCandidates.length; k += 1) {
        const playCandidate = orderedCandidates[k];
        if (!playCandidate || !playCandidate.flag || !playCandidate.id) continue;
        const panProvider = panMockProviderFromFlag(playCandidate.flag);
        if (panMockFlow && panProvider) {
          try {
            const panPasscode = panProvider === '189'
              ? deriveTianyiMockMeta(playCandidate.flag, playCandidate.passcode).accessCode
              : playCandidate.passcode;
            const resolvedID = await callPanListResolver(panProvider, {
              flag: playCandidate.flag,
              passcode: playCandidate.passcode
            }, tvUser);
            if (!resolvedID) {
              if (!playErr) playErr = '网盘列表为空';
              continue;
            }
            const panURL = await callPanPlayResolver(panProvider, {
              flag: playCandidate.flag,
              id: resolvedID,
              passcode: panPasscode
            }, tvUser);
            if (panURL) return { ok: true, playErr: '' };
            playErr = '未提取到地址';
          } catch (err) {
            playErr = formatHttpError(err);
            if (isFatalHttpProbeError(err)) break;
          }
          continue;
        }

        const playResp = await requestCatpawrunnerAdminJson({
          apiBase,
          path: 'play',
          method: 'POST',
          body: {
            flag: playCandidate.flag,
            id: playCandidate.id,
            siteApi: `/${spiderPath}`.replace(/\/{2,}/g, '/')
          },
          tvUser
        });
        const psc = normalizeStatusCode(playResp);
        if (psc >= 400) continue;
        const url = extractPlayUrl(playResp);
        if (url) return { ok: true, playErr: '' };
        playErr = '未提取到地址';
      }
    } catch (err) {
      playErr = formatHttpError(err);
      if (isFatalHttpProbeError(err)) break;
    }
  }
  return { ok: false, playErr };
}

async function probeDashboardVideoSourceHome({ apiBase, spiderPath, tvUser }) {
  try {
    const homeResp = await requestCatpawrunnerAdminJson({
      apiBase,
      path: `${spiderPath}/home`,
      method: 'POST',
      body: {},
      tvUser
    });
    const statusCode = normalizeStatusCode(homeResp);
    if (statusCode >= 400) {
      const message = normalizeMessage(homeResp) || '请求失败';
      return {
        ok: false,
        error: message.startsWith('HTTP') ? message : `HTTP ${statusCode}：${message}`,
        classes: []
      };
    }
    return {
      ok: true,
      error: '',
      classes: extractClasses(homeResp)
    };
  } catch (err) {
    return {
      ok: false,
      error: formatHttpError(err),
      classes: []
    };
  }
}

async function probeDashboardVideoSourceCategory({ apiBase, spiderPath, classes, tvUser }) {
  const firstWithId = Array.isArray(classes) ? classes.find((item) => !!extractClassId(item)) : null;
  const tid = extractClassId(firstWithId);
  const body = tid
    ? { id: tid, page: 1, filter: true, filters: {} }
    : { id: '0', page: 1, filter: true, filters: {} };
  try {
    const categoryResp = await requestCatpawrunnerAdminJson({
      apiBase,
      path: `${spiderPath}/category`,
      method: 'POST',
      body,
      tvUser
    });
    const statusCode = normalizeStatusCode(categoryResp);
    if (statusCode >= 400) {
      const message = normalizeMessage(categoryResp) || '请求失败';
      return {
        ok: false,
        error: message.startsWith('HTTP') ? message : `HTTP ${statusCode}：${message}`,
        items: [],
        empty: false,
        panMock: false
      };
    }
    const items = extractList(categoryResp);
    return {
      ok: true,
      error: '',
      items: Array.isArray(items) ? items.slice(0, 10) : [],
      empty: !Array.isArray(items) || items.length === 0,
      panMock: readPanMockFlag(categoryResp)
    };
  } catch (err) {
    return {
      ok: false,
      error: formatHttpError(err),
      items: [],
      empty: false,
      panMock: false
    };
  }
}

async function probeDashboardVideoSourceSearch({ apiBase, spiderPath, tvUser }) {
  try {
    const searchResp = await requestCatpawrunnerAdminJson({
      apiBase,
      path: `${spiderPath}/search`,
      method: 'POST',
      body: { wd: '斗破', page: 1 },
      tvUser
    });
    const statusCode = normalizeStatusCode(searchResp);
    if (statusCode >= 400) {
      const message = normalizeMessage(searchResp) || '请求失败';
      return {
        ok: false,
        error: message.startsWith('HTTP') ? message : `HTTP ${statusCode}：${message}`,
        items: [],
        panMock: false
      };
    }
    return {
      ok: true,
      error: '',
      items: extractList(searchResp),
      panMock: readPanMockFlag(searchResp)
    };
  } catch (err) {
    return {
      ok: false,
      error: formatHttpError(err),
      items: [],
      panMock: false
    };
  }
}

function finalizeDashboardVideoSourceProbe({
  key,
  homeError = '',
  categoryError = '',
  playError = '',
  searchError = '',
  playOkFromCategory = false,
  playOkFromSearch = false,
  categoryMissingPlayMeta = false,
  searchOk = false
}) {
  let result = 'invalid';
  if (playOkFromCategory) {
    result = searchOk ? 'valid' : 'search_error';
  } else if (playOkFromSearch) {
    result = categoryMissingPlayMeta ? 'valid' : 'category_error';
  }
  const parts = [];
  if (homeError) parts.push(`首页接口:${homeError}`);
  if (categoryError) parts.push(`分类接口:${categoryError}`);
  if (playError) parts.push(`播放接口:${playError}`);
  if (searchError) parts.push(`搜索接口:${searchError}`);
  return buildDashboardVideoSourceProbeResult(key, result, parts.join('  '), false);
}

async function probeSingleDashboardVideoSourceSite({ apiBase, site, tvUser }) {
  const key = site && typeof site.key === 'string' ? site.key.trim() : '';
  if (!site || !site.api) {
    return buildDashboardVideoSourceProbeResult(key, 'invalid', '分类接口:站点配置无效', false);
  }
  if (shouldSkipDashboardVideoSourceCheck(site)) {
    return buildDashboardVideoSourceProbeResult(key, 'skipped', '', false);
  }
  const spiderName = extractSpiderNameFromApi(site.api);
  if (spiderName === 'baseset') {
    return buildDashboardVideoSourceProbeResult(key, 'valid', '', false);
  }
  if (isMyPanSite(site)) {
    return buildDashboardVideoSourceProbeResult(key, 'valid', '', true);
  }

  const spiderPath = String(site.api || '').trim().replace(/\/+$/, '').replace(/^\//, '');
  const homeState = await probeDashboardVideoSourceHome({
    apiBase,
    spiderPath,
    tvUser
  });
  const categoryState = homeState.ok
    ? await probeDashboardVideoSourceCategory({
        apiBase,
        spiderPath,
        classes: homeState.classes,
        tvUser
      })
    : { ok: false, error: '', items: [], empty: false, panMock: false };
  const categoryMeta = { missingPlayMeta: false, panMock: categoryState.panMock };
  let playErr = '';
  let playOkFromCategory = false;
  if (categoryState.ok && !categoryState.empty) {
    const result = await tryPlayCandidatesForVideoSource({
      apiBase,
      spiderPath,
      items: categoryState.items,
      tvUser,
      meta: categoryMeta
    });
    playOkFromCategory = result.ok;
    playErr = result.playErr;
  }

  const shouldUseSearchForPlay = !categoryState.ok || categoryState.empty || (!playOkFromCategory && categoryMeta.missingPlayMeta);
  const shouldProbeSearchFinally = playOkFromCategory;
  const searchState = shouldUseSearchForPlay || shouldProbeSearchFinally
    ? await probeDashboardVideoSourceSearch({
        apiBase,
        spiderPath,
        tvUser
      })
    : { ok: false, error: '', items: [], panMock: false };

  let playOkFromSearch = false;
  if (shouldUseSearchForPlay && searchState.ok && Array.isArray(searchState.items) && searchState.items.length) {
    const result = await tryPlayCandidatesForVideoSource({
      apiBase,
      spiderPath,
      items: searchState.items,
      tvUser,
      meta: { panMock: searchState.panMock }
    });
    playOkFromSearch = result.ok;
    if (!playOkFromCategory) playErr = result.playErr;
  }
  if (playOkFromSearch) playErr = '';
  return finalizeDashboardVideoSourceProbe({
    key,
    homeError: homeState.error,
    categoryError: categoryState.error,
    playError: playErr,
    searchError: searchState.error,
    playOkFromCategory,
    playOkFromSearch,
    categoryMissingPlayMeta: categoryMeta.missingPlayMeta,
    searchOk: searchState.ok
  });
}

export async function probeDashboardVideoSourceSites({ apiBase, keys, sites, tvUser = '' }) {
  const normalizedBase = normalizeHttpBase(apiBase);
  if (!normalizedBase) throw new Error('CatPawRunner 接口地址未设置');
  const uniq = (Array.isArray(keys) ? keys : [])
    .map((k) => (typeof k === 'string' ? k.trim() : ''))
    .filter(Boolean);
  if (!uniq.length) {
    return { sites: Array.isArray(sites) ? sites : [], results: {} };
  }
  const byKey = new Map();
  (Array.isArray(sites) ? sites : []).forEach((site) => {
    if (site && site.key) byKey.set(site.key, site);
  });
  const results = {};
  const errors = {};
  const disableSearchKeys = [];

  for (let i = 0; i < uniq.length; i += 1) {
    const key = uniq[i];
    const site = byKey.get(key);
    try {
      const probed = await probeSingleDashboardVideoSourceSite({
        apiBase: normalizedBase,
        site,
        tvUser
      });
      results[key] = normalizeAvailability(probed.result);
      if (probed.error) errors[key] = probed.error;
      if (probed.disableSearch) disableSearchKeys.push(key);
    } catch (_e) {
      results[key] = 'invalid';
      errors[key] = '首页接口:检测失败  分类接口:检测失败  播放接口:检测失败  搜索接口:检测失败';
    }
  }

  const data = await checkDashboardVideoSourceSites(results, errors);
  if (disableSearchKeys.length) {
    for (let i = 0; i < disableSearchKeys.length; i += 1) {
      const k = disableSearchKeys[i];
      await postForm('/dashboard/video/source/sites/home', { key: k, home: '0' }).catch(() => {});
      await postForm('/dashboard/video/source/sites/search', { key: k, search: '0' }).catch(() => {});
    }
  }
  return {
    sites: Array.isArray(data && data.sites) ? data.sites : [],
    results: data && typeof data.results === 'object' ? data.results : {}
  };
}

export async function fetchCatpawrunnerFullConfig(apiBase, tvUser = '') {
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: 'admin/full-config',
    method: 'GET',
    timeoutMs: 12000,
    tvUser
  });
}

export function unwrapCatpawrunnerWebsiteData(resp) {
  if (!resp) throw new Error('CatPawRunner 返回为空');
  if (resp && typeof resp === 'object') {
    if (resp.code === 0) return resp.data;
    if (resp.success === true && Object.prototype.hasOwnProperty.call(resp, 'data')) return resp.data;
    if (Object.prototype.hasOwnProperty.call(resp, 'data')) return resp.data;
    const msg = typeof resp.message === 'string' ? resp.message : '';
    if (msg) throw new Error(msg);
  }
  return resp;
}

function normalizeTvUser(value) {
  const raw = value != null ? String(value) : '';
  const trimmed = raw.trim();
  if (!trimmed) return '';
  return trimmed.replace(/[\r\n]+/g, '');
}

export async function requestCatpawrunnerAdminJson({ apiBase, path, method = 'GET', body, timeoutMs = 0, tvUser = '' }) {
  const base = normalizeHttpBase(apiBase);
  if (!base) throw new Error('CatPawRunner 接口地址无效');
  const cleanPath = String(path || '').replace(/^\//, '');
  const target = new URL(cleanPath, `${base}/`);
  const headers = {
    Accept: 'application/json',
    'Content-Type': 'application/json'
  };
  const normalizedTvUser = normalizeTvUser(tvUser);
  if (normalizedTvUser) {
    headers['X-TV-User'] = normalizedTvUser;
  }

  const ms = Number.isFinite(Number(timeoutMs)) ? Math.max(0, Math.trunc(Number(timeoutMs))) : 0;
  const controller = ms > 0 && typeof AbortController !== 'undefined' ? new AbortController() : null;
  const timer = controller ? setTimeout(() => controller.abort(), ms) : null;
  try {
    const { resp, data } = await requestJson(target.toString(), {
      method,
      headers,
      body: body != null ? JSON.stringify(body) : undefined,
      credentials: 'omit',
      signal: controller ? controller.signal : undefined
    });
    const status = resp && typeof resp.status === 'number' ? resp.status : 0;
    if (!resp || !resp.ok) {
      throw new Error((data && (data.message || data.error)) || `HTTP ${status}`);
    }
    return data;
  } finally {
    if (timer) clearTimeout(timer);
  }
}

export async function fetchCatpawrunnerAdminSettings(apiBase, tvUser = '') {
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: 'admin/settings',
    method: 'GET',
    timeoutMs: 8000,
    tvUser
  });
}

export async function saveCatpawrunnerAdminSettings(apiBase, payload, tvUser = '') {
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: 'admin/settings',
    method: 'PUT',
    body: payload,
    timeoutMs: 12000,
    tvUser
  });
}

export async function updateCatpawrunnerOnlineConfig(apiBase, onlineConfigId, tvUser = '') {
  const id = String(onlineConfigId || '').trim();
  if (!id) throw new Error('配置 ID 无效');
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: 'admin/online-configs/update',
    method: 'POST',
    body: { id },
    timeoutMs: 12000,
    tvUser
  });
}

export async function fetchCatpawrunnerWebsitePans(apiBase, runtimeId, tvUser = '') {
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: `${String(runtimeId || '').replace(/^\//, '')}/website/pans/list`,
    method: 'GET',
    timeoutMs: 8000,
    tvUser
  });
}

export async function saveCatpawrunnerWebsitePans(apiBase, runtimeId, list, tvUser = '') {
  return requestCatpawrunnerAdminJson({
    apiBase,
    path: `${String(runtimeId || '').replace(/^\//, '')}/website/pans/list`,
    method: 'PUT',
    body: { list: Array.isArray(list) ? list : [] },
    timeoutMs: 12000,
    tvUser
  });
}

export async function syncPanLoginSettingsToCatpawrunner(apiBase, tvUser = '', panDefs = []) {
  const normalizedBase = normalizeHttpBase(apiBase);
  if (!normalizedBase) return { ok: false, skipped: true, reason: 'unconfigured' };
  const raw = await fetchPanSettings('');
  const store = raw && raw.settings && typeof raw.settings === 'object' ? raw.settings : {};
  const entries = store && typeof store === 'object' ? Object.entries(store) : [];
  if (!entries.length) return { ok: true, skipped: false, okCount: 0, failCount: 0 };
  const typeByKey = new Map();
  (Array.isArray(panDefs) ? panDefs : []).forEach((def) => {
    if (def && typeof def.key === 'string' && typeof def.type === 'string') {
      typeByKey.set(def.key, def.type);
    }
  });
  if (!typeByKey.size) {
    [
      ['baidu', 'cookie'],
      ['quark', 'cookie'],
      ['quark_tv', 'quark_tv'],
      ['189', 'account'],
      ['139', 'authorization'],
      ['uc', 'cookie'],
      ['uc_tv', 'uc_tv'],
      ['pan123', 'account'],
      ['115', 'cookie'],
      ['bili', 'cookie'],
      ['wuming', 'cookie'],
      ['yunchao', 'account'],
      ['pan123ziyuan', 'cookie']
    ].forEach(([key, type]) => typeByKey.set(key, type));
  }
  const pans = {};
  entries.forEach(([k, v]) => {
    const key = typeof k === 'string' ? k.trim() : '';
    if (!key) return;
    const type = typeByKey.get(key);
    const cur = v && typeof v === 'object' ? v : {};
    const payload = {};
    if (type === 'account') {
      if (typeof cur.username === 'string') payload.username = cur.username;
      if (typeof cur.password === 'string') payload.password = cur.password;
    } else if (type === 'quark_tv' || type === 'uc_tv') {
      // `/admin/pan/sync` builtin resolver payload
      if (typeof cur.refresh_token === 'string') payload.refresh_token = cur.refresh_token;
      if (typeof cur.device_id === 'string') payload.device_id = cur.device_id;
    } else if (type === 'authorization') {
      if (typeof cur.authorization === 'string') payload.authorization = cur.authorization;
    } else {
      if (typeof cur.cookie === 'string') payload.cookie = cur.cookie;
    }
    pans[key] = payload;
  });
  try {
    const resp = await requestCatpawrunnerAdminJson({
      apiBase: normalizedBase,
      path: 'admin/pan/sync',
      method: 'POST',
      body: { pans },
      timeoutMs: 12000,
      tvUser
    });
    const okCount = resp && typeof resp.okCount === 'number' ? resp.okCount : 0;
    const failCount = resp && typeof resp.failCount === 'number' ? resp.failCount : 0;
    return { ok: failCount <= 0 && resp && resp.success !== false, skipped: false, okCount, failCount };
  } catch (_e) {
    return { ok: false, skipped: false, okCount: 0, failCount: 0 };
  }
}

export async function addDashboardUser(payload) {
  return postForm('/dashboard/user/add', payload);
}

export async function updateDashboardUser(payload) {
  return postForm('/dashboard/user/update', payload);
}

export async function toggleDashboardUserStatus(payload) {
  return postForm('/dashboard/user/ban', payload);
}

export async function deleteDashboardUser(payload) {
  return postForm('/dashboard/user/delete', payload);
}

const DASHBOARD_BACKUP_FORMAT = 'meowfilm-dashboard-settings';
const DASHBOARD_BACKUP_VERSION = 1;
const KNOWN_PAN_PROVIDER_KEYS = new Set([
  'baidu',
  'quark',
  'quark_tv',
  '189',
  '139',
  'uc',
  'uc_tv',
  'pan123',
  '115',
  'bili',
  'wuming',
  'yunchao',
  'pan123ziyuan'
]);

function asObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function normalizeString(value, fallback = '') {
  return typeof value === 'string' ? value.trim() : fallback;
}

function normalizeBoolean(value, fallback = false) {
  return typeof value === 'boolean' ? value : fallback;
}

function normalizeInteger(value, fallback = 0) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? Math.max(0, Math.trunc(numeric)) : fallback;
}

function normalizeStringArray(value) {
  return asArray(value).map((item) => normalizeString(item, '')).filter(Boolean);
}

function normalizeCatpawrunnerServers(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        name: normalizeString(row.name, ''),
        apiBase: normalizeHttpBase(row.apiBase)
      };
    })
    .filter((item) => item.name && item.apiBase);
}

function normalizeCatpawrunnerPans(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        key: normalizeString(row.key, ''),
        name: normalizeString(row.name, ''),
        enable: normalizeBoolean(row.enable, false)
      };
    })
    .filter((item) => item.key);
}

function normalizePanLoginSettings(value) {
  const root = asObject(value);
  const out = {};
  Object.keys(root).forEach((provider) => {
    const key = normalizeString(provider, '');
    if (!KNOWN_PAN_PROVIDER_KEYS.has(key)) return;
    const source = asObject(root[provider]);
    const next = {};
    [
      'cookie',
      'authorization',
      'username',
      'password',
      'refresh_token',
      'device_id',
      'access_token',
      'access_token_exp_at'
    ].forEach((field) => {
      if (typeof source[field] === 'string') next[field] = source[field];
    });
    if (Object.keys(next).length) out[key] = next;
  });
  return out;
}

function normalizeGoProxyPanMap(value) {
  const pans = asObject(value);
  return {
    baidu: normalizeBoolean(pans.baidu, true),
    quark: normalizeBoolean(pans.quark, true)
  };
}

function normalizeGoProxyServers(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        name: normalizeString(row.name, ''),
        displayName: normalizeString(row.displayName, ''),
        base: normalizeHttpBase(row.base),
        pans: normalizeGoProxyPanMap(row.pans)
      };
    })
    .filter((item) => item.name && item.base);
}

function normalizeRelayServers(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        name: normalizeString(row.name, ''),
        displayName: normalizeString(row.displayName, ''),
        base: normalizeHttpBase(row.base),
        secret: normalizeString(row.secret, '')
      };
    })
    .filter((item) => item.name && item.base);
}

function normalizeVideoSourceSites(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      const key = normalizeString(row.key || row.Key, '');
      const api = normalizeString(row.api || row.API, '');
      const name = normalizeString(row.name || row.Name, '');
      const rawType = row.type != null ? row.type : row.Type;
      const next = { key, name, api };
      if (rawType != null && Number.isFinite(Number(rawType))) next.type = Math.trunc(Number(rawType));
      return next;
    })
    .filter((item) => item.key && item.api);
}

function normalizeVideoSourceStates(value) {
  const root = asObject(value);
  const out = {};
  Object.keys(root).forEach((rawKey) => {
    const key = normalizeString(rawKey, '');
    if (!key) return;
    const row = asObject(root[rawKey]);
    out[key] = {
      enabled: normalizeBoolean(row.enabled, false),
      home: normalizeBoolean(row.home, false),
      search: normalizeBoolean(row.search, false),
      orderIndex: normalizeInteger(row.orderIndex, 0)
    };
  });
  return out;
}

function normalizeThirdPartySections(value) {
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      const name = normalizeString(row.name, '');
      if (!name) return null;
      return {
        id: normalizeString(row.id, ''),
        name,
        module: normalizeString(row.module, ''),
        mediaType: normalizeString(row.mediaType, ''),
        siteKey: normalizeString(row.siteKey, ''),
        categoryId: normalizeString(row.categoryId, ''),
        cardStyle: normalizeString(row.cardStyle, '')
      };
    })
    .filter(Boolean);
}

function normalizeSmartPanAliasMappings(value) {
  const seen = new Set();
  return asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        pan: normalizeString(row.pan, ''),
        aliases: normalizeString(row.aliases, '')
      };
    })
    .filter((item) => {
      if (!item.pan) return false;
      const key = item.pan.toLowerCase();
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
}

function normalizeSmartSourceRuleRows(value) {
  const defaults = [
    { key: 'quality', order: 1 },
    { key: 'pan', order: 2 },
    { key: 'keyword', order: 3 }
  ];
  const seen = new Set();
  const rows = asArray(value)
    .map((item) => {
      const row = asObject(item);
      return {
        key: normalizeString(row.key, '').toLowerCase(),
        order: normalizeInteger(row.order, 0)
      };
    })
    .filter((item) => item.key === 'quality' || item.key === 'keyword' || item.key === 'pan')
    .sort((left, right) => {
      const leftOrder = left.order > 0 ? left.order : 999999;
      const rightOrder = right.order > 0 ? right.order : 999999;
      if (leftOrder !== rightOrder) return leftOrder - rightOrder;
      return 0;
    })
    .filter((item) => {
      if (seen.has(item.key)) return false;
      seen.add(item.key);
      return true;
    });
  defaults.forEach((item) => {
    if (!seen.has(item.key)) rows.push({ key: item.key, order: item.order });
  });
  return rows.map((row, index) => ({
    key: row.key,
    order: index + 1
  }));
}

export function normalizeDashboardBackupSchema(rawBackup) {
  const root = asObject(rawBackup);
  const siteRoot = asObject(root.site);
  const metadataRoot = asObject(root.metadata);
  const magicRoot = asObject(root.magic);
  const smartRoot = asObject(root.smart);
  const thirdPartyRoot = asObject(root.thirdParty);
  const panRoot = asObject(root.pan);
  const catpawrunnerRoot = asObject(root.catpawrunner);
  const goProxyRoot = asObject(root.goProxy);
  const relayRoot = asObject(root.relay);
  const videoSourceRoot = asObject(root.videoSource);

  return {
    format: DASHBOARD_BACKUP_FORMAT,
    version: normalizeInteger(root.version, DASHBOARD_BACKUP_VERSION) || DASHBOARD_BACKUP_VERSION,
    exportedAt: normalizeInteger(root.exportedAt, Math.trunc(Date.now() / 1000)),
    site: {
      siteName: normalizeString(siteRoot.siteName, ''),
      searchDisplayMode: normalizeString(siteRoot.searchDisplayMode, 'sites') || 'sites',
      netdiskProxyEnabled: normalizeBoolean(siteRoot.netdiskProxyEnabled, false),
      netdiskProxyUrl: normalizeString(siteRoot.netdiskProxyUrl, '')
    },
    metadata: {
      doubanDataProxy: normalizeString(metadataRoot.doubanDataProxy, 'server-proxy') || 'server-proxy',
      doubanDataCustom: normalizeString(metadataRoot.doubanDataCustom, ''),
      doubanImgProxy: normalizeString(metadataRoot.doubanImgProxy, 'server-proxy') || 'server-proxy',
      doubanImgCustom: normalizeString(metadataRoot.doubanImgCustom, ''),
      doubanSearchCookie: normalizeString(metadataRoot.doubanSearchCookie, ''),
      tmdbApiToken: normalizeString(metadataRoot.tmdbApiToken, ''),
      tmdbDataProxyBase: normalizeHttpBase(metadataRoot.tmdbDataProxyBase || ''),
      tmdbImageProxyBase: normalizeHttpBase(metadataRoot.tmdbImageProxyBase || ''),
      language: normalizeString(metadataRoot.language, 'zh-CN') || 'zh-CN',
      region: normalizeString(metadataRoot.region, 'CN') || 'CN',
      includeAdult: normalizeBoolean(metadataRoot.includeAdult, false)
    },
    magic: {
      episodeRules: normalizeStringArray(magicRoot.episodeRules),
      episodeCleanRegexRules: normalizeStringArray(magicRoot.episodeCleanRegexRules),
      movieRules: normalizeStringArray(magicRoot.movieRules),
      aggregateRegexRules: normalizeStringArray(magicRoot.aggregateRegexRules)
    },
    smart: {
      smartSourceRuleRows: normalizeSmartSourceRuleRows(
        smartRoot.smartSourceRuleRows
      ),
      siteCleanKeywords: normalizeString(
        smartRoot.siteCleanKeywords,
        ''
      ),
      smartSourcePriorityTokens: normalizeStringArray(smartRoot.smartSourcePriorityTokens),
      smartPanMatchTokens: normalizeStringArray(smartRoot.smartPanMatchTokens),
      smartPanAliasMappings: normalizeSmartPanAliasMappings(smartRoot.smartPanAliasMappings)
    },
    thirdParty: {
      thirdPartyClientHomeSections: normalizeThirdPartySections(
        thirdPartyRoot.thirdPartyClientHomeSections
      )
    },
    pan: {
      loginSettings: normalizePanLoginSettings(panRoot.loginSettings)
    },
    catpawrunner: {
      active: normalizeString(catpawrunnerRoot.active, ''),
      servers: normalizeCatpawrunnerServers(catpawrunnerRoot.servers),
      pans: normalizeCatpawrunnerPans(catpawrunnerRoot.pans)
    },
    goProxy: {
      enabled: normalizeBoolean(goProxyRoot.enabled, false),
      autoSelect: normalizeBoolean(goProxyRoot.autoSelect, false),
      servers: normalizeGoProxyServers(goProxyRoot.servers)
    },
    relay: {
      enabled: normalizeBoolean(relayRoot.enabled, false),
      auth: normalizeString(relayRoot.auth, ''),
      servers: normalizeRelayServers(relayRoot.servers)
    },
    videoSource: {
      searchCoverSite: normalizeString(
        videoSourceRoot.searchCoverSite,
        ''
      ),
      sites: normalizeVideoSourceSites(videoSourceRoot.sites),
      states: normalizeVideoSourceStates(videoSourceRoot.states),
      order: normalizeStringArray(videoSourceRoot.order)
    }
  };
}

export function buildDashboardRestorePayload(rawBackup) {
  const normalized = normalizeDashboardBackupSchema(rawBackup);
  return {
    version: normalized.version,
    exportedAt: normalized.exportedAt,
    site: normalized.site,
    metadata: normalized.metadata,
    magic: normalized.magic,
    smart: normalized.smart,
    thirdParty: normalized.thirdParty,
    pan: normalized.pan,
    catpawrunner: normalized.catpawrunner,
    goProxy: normalized.goProxy,
    relay: normalized.relay,
    videoSource: normalized.videoSource
  };
}

export async function exportDashboardBackup() {
  const backupData = await getSuccessJson('/dashboard/backup');
  return normalizeDashboardBackupSchema(backupData);
}

export async function restoreDashboardBackup(payload) {
  return postJson('/dashboard/restore', buildDashboardRestorePayload(payload));
}

export async function validateSearchDisplayMode(mode) {
  const value = typeof mode === 'string' ? mode.trim() : '';
  if (value !== 'tmdb' && value !== 'both') return { valid: true };
  const metadata = await fetchMetadataSettings();
  const token = metadata && typeof metadata.tmdbApiToken === 'string' ? metadata.tmdbApiToken.trim() : '';
  if (token) return { valid: true };
  return { valid: false, message: 'TMDB API TOKEN 未设置' };
}

export function buildBackupFilename(now = new Date()) {
  const pad = (n) => String(n).padStart(2, '0');
  return `meowfilm_backup_${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}.json`;
}

export function downloadJsonFile(obj, filename) {
  const json = JSON.stringify(obj != null ? obj : {}, null, 2);
  const blob = new Blob([json], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename || 'meowfilm-backup.json';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
