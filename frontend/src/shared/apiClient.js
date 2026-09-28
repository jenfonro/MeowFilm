const inflight = new Map();
const cache = new Map();
const CACHE_MAX_ENTRIES = 200;

const now = () => Date.now();

const normalizeUrl = (url) => {
  const raw = typeof url === 'string' ? url.trim() : '';
  if (!raw) return '';
  return raw;
};

const toKey = (method, url, body) => {
  const u = normalizeUrl(url);
  const m = String(method || 'GET').toUpperCase();
  const b = body == null ? '' : typeof body === 'string' ? body : JSON.stringify(body);
  return `${m} ${u} ${b}`;
};

const cacheGet = (key, cacheMs) => {
  if (!(cacheMs > 0)) return null;
  const hit = cache.get(key);
  if (!hit || !hit.t) return null;
  if (now() - hit.t >= cacheMs) {
    cache.delete(key);
    return null;
  }
  // LRU bump
  cache.delete(key);
  cache.set(key, hit);
  return hit.v;
};

const cacheSet = (key, value) => {
  cache.set(key, { t: now(), v: value });
  while (cache.size > CACHE_MAX_ENTRIES) {
    const oldest = cache.keys().next().value;
    if (!oldest) break;
    cache.delete(oldest);
  }
};

const withTimeout = async (promise, timeoutMs) => {
  const ms = Number(timeoutMs);
  if (!Number.isFinite(ms) || ms <= 0) return await promise;
  let t = 0;
  const timeout = new Promise((_, reject) => {
    t = setTimeout(() => reject(new Error('请求超时')), ms);
  });
  try {
    return await Promise.race([promise, timeout]);
  } finally {
    clearTimeout(t);
  }
};

export const buildQuery = (params) => {
  const obj = params && typeof params === 'object' ? params : null;
  if (!obj) return '';
  const usp = new URLSearchParams();
  Object.keys(obj).forEach((k) => {
    const v = obj[k];
    if (v == null) return;
    if (Array.isArray(v)) {
      v.forEach((x) => {
        if (x == null) return;
        usp.append(k, String(x));
      });
      return;
    }
    usp.set(k, String(v));
  });
  const s = usp.toString();
  return s ? `?${s}` : '';
};

const parseJsonSafe = async (resp) => {
  try {
    return await resp.json();
  } catch (_e) {
    return null;
  }
};

export class ApiError extends Error {
  constructor(message, status, data) {
    super(message || '请求失败');
    this.name = 'ApiError';
    this.status = typeof status === 'number' ? status : 0;
    this.data = data;
    this.method = '';
    this.url = '';
  }
}

export const apiRequestJson = async (url, options = {}) => {
  const u = normalizeUrl(url);
  if (!u) throw new Error('url 不能为空');

  const opts = options && typeof options === 'object' ? options : {};
  const method = String(opts.method || 'GET').toUpperCase();
  const timeoutMs = opts.timeoutMs != null ? Number(opts.timeoutMs) : 15000;
  const dedupe = opts.dedupe !== false;
  const cacheMs = opts.cacheMs != null ? Number(opts.cacheMs) : 0;
  const body = opts.body != null ? opts.body : null;
  const signal = opts.signal || null;

  const key = toKey(method, u, body);
  const cached = cacheGet(key, cacheMs);
  if (cached != null) return cached;
  if (dedupe && inflight.has(key)) return await inflight.get(key);

  const p = (async () => {
    const headers = Object.assign({}, opts.headers || {});
    const init = {
      method,
      credentials: opts.credentials || 'same-origin',
      cache: opts.cache || 'no-store',
      headers,
    };
    if (signal) init.signal = signal;
    if (body != null) {
      init.body = typeof body === 'string' ? body : JSON.stringify(body);
      if (!headers['Content-Type'] && !headers['content-type']) headers['Content-Type'] = 'application/json';
    }

    const resp = await withTimeout(fetch(u, init), timeoutMs);
    const status = resp && typeof resp.status === 'number' ? resp.status : 0;
    const data = await parseJsonSafe(resp);
    if (!resp || !resp.ok) {
      const msg =
        data && typeof data === 'object'
          ? String(data.message || data.error || data.msg || `HTTP ${status}`)
          : `HTTP ${status || 0}`;
      const err = new ApiError(msg || '请求失败', status, data);
      err.method = method;
      err.url = u;
      throw err;
    }
    if (cacheMs > 0) cacheSet(key, data);
    return data;
  })();

  inflight.set(key, p);
  try {
    return await p;
  } finally {
    if (inflight.get(key) === p) inflight.delete(key);
  }
};

export const apiGetJson = async (url, opts = {}) => {
  return await apiRequestJson(url, { ...(opts || {}), method: 'GET' });
};

export const apiPostJson = async (url, body, opts = {}) => {
  return await apiRequestJson(url, { ...(opts || {}), method: 'POST', body });
};

export const apiPostForm = async (url, form, opts = {}) => {
  const obj = form && typeof form === 'object' ? form : {};
  const params = new URLSearchParams();
  Object.keys(obj).forEach((k) => {
    const v = obj[k];
    if (v == null) return;
    params.set(k, String(v));
  });
  return await apiRequestJson(url, {
    ...(opts || {}),
    method: 'POST',
    body: params.toString(),
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', ...(opts && opts.headers ? opts.headers : {}) },
  });
};

export const apiPutJson = async (url, body, opts = {}) => {
  return await apiRequestJson(url, { ...(opts || {}), method: 'PUT', body });
};

export const apiDeleteJson = async (url, opts = {}) => {
  return await apiRequestJson(url, { ...(opts || {}), method: 'DELETE' });
};

export const apiInvalidateCache = ({ urlPrefix, method } = {}) => {
  const prefix = typeof urlPrefix === 'string' ? urlPrefix.trim() : '';
  const m = typeof method === 'string' ? method.trim().toUpperCase() : '';
  try {
    Array.from(cache.keys()).forEach((key) => {
      const s = typeof key === 'string' ? key : '';
      const mm = s.match(/^(\S+)\s+(\S+)\s/);
      if (!mm) return;
      const km = mm[1] ? String(mm[1]).toUpperCase() : '';
      const ku = mm[2] ? String(mm[2]) : '';
      if (m && km !== m) return;
      if (prefix && !ku.startsWith(prefix)) return;
      cache.delete(key);
    });
    Array.from(inflight.keys()).forEach((key) => {
      const s = typeof key === 'string' ? key : '';
      const mm = s.match(/^(\S+)\s+(\S+)\s/);
      if (!mm) return;
      const km = mm[1] ? String(mm[1]).toUpperCase() : '';
      const ku = mm[2] ? String(mm[2]) : '';
      if (m && km !== m) return;
      if (prefix && !ku.startsWith(prefix)) return;
      inflight.delete(key);
    });
  } catch (_e) {}
};
