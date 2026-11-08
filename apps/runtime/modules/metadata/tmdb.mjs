const routes = [
  /^\/3\/configuration(?:\/(?:countries|languages|primary_translations|timezones))?$/,
  /^\/3\/genre\/(?:movie|tv)\/list$/,
  /^\/3\/search\/(?:movie|tv|multi|person)$/,
  /^\/3\/discover\/(?:movie|tv)$/,
  /^\/3\/trending\/(?:all|movie|tv|person)\/(?:day|week)$/,
  /^\/3\/movie\/(?:latest|now_playing|popular|top_rated|upcoming)$/,
  /^\/3\/movie\/[1-9]\d*(?:\/(?:alternative_titles|credits|external_ids|images|keywords|lists|recommendations|release_dates|similar|translations|videos|watch\/providers))?$/,
  /^\/3\/tv\/(?:latest|airing_today|on_the_air|popular|top_rated)$/,
  /^\/3\/tv\/[1-9]\d*(?:\/(?:aggregate_credits|alternative_titles|content_ratings|credits|episode_groups|external_ids|images|keywords|recommendations|screened_theatrically|similar|translations|videos|watch\/providers))?$/,
  /^\/3\/tv\/[1-9]\d*\/season\/(?:0|[1-9]\d*)(?:\/(?:aggregate_credits|credits|external_ids|images|translations|videos|watch\/providers))?$/,
  /^\/3\/tv\/[1-9]\d*\/season\/(?:0|[1-9]\d*)\/episode\/(?:0|[1-9]\d*)(?:\/(?:credits|external_ids|images|translations|videos))?$/,
  /^\/3\/tv\/episode_group\/[A-Za-z0-9_-]+$/,
  /^\/3\/person\/(?:latest|popular)$/,
  /^\/3\/person\/[1-9]\d*(?:\/(?:combined_credits|external_ids|images|movie_credits|tagged_images|translations|tv_credits))?$/,
  /^\/3\/collection\/[1-9]\d*(?:\/(?:images|translations))?$/,
  /^\/3\/credit\/[A-Za-z0-9_-]+$/,
  /^\/3\/watch\/providers\/(?:regions|movie|tv)$/,
];
const imageFields = new Set([
  'poster_path', 'backdrop_path', 'profile_path', 'logo_path', 'still_path', 'file_path',
]);
const credentialFields = new Set([
  'apikey', 'accesstoken', 'requesttoken', 'sessionid', 'guestsessionid', 'authorization', 'cookie',
]);
const control = /[\u0000-\u001f\u007f\u2028\u2029]/;
const sizePattern = /^(?:original|[wh][1-9]\d*)$/;
const filePattern = /^\/[A-Za-z0-9_-]+\.[A-Za-z0-9]+$/;
const imagePrefix = '/v1/tmdb/images/';
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const record = value => object(value) &&
  (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);

class Failure extends Error {
  constructor(code) { super(code); this.code = code; }
}

function requireValue(condition, code = 'INVALID_REQUEST') {
  if (!condition) throw new Failure(code);
}

function allowedPath(path) {
  return typeof path === 'string' && !control.test(path) && routes.some(route => route.test(path));
}

function imageSize(size) {
  requireValue(typeof size === 'string' && !control.test(size) && sizePattern.test(size));
  return size;
}

function baseURL(value) {
  requireValue(typeof value === 'string' && !/[\u0000-\u0020\u007f\\?#]/.test(value));
  let url;
  try { url = new URL(value); } catch { throw new Failure('INVALID_REQUEST'); }
  requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password);
  return url.href.replace(/\/+$/, '');
}

function queryParams(query, path) {
  requireValue(record(query));
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(query)) {
    requireValue(/^[A-Za-z][A-Za-z0-9_.]*$/.test(name) && !control.test(name) &&
      !credentialFields.has(name.replaceAll('_', '').toLowerCase()));
    requireValue(typeof value === 'string' || typeof value === 'boolean' ||
      typeof value === 'number' && Number.isFinite(value));
    if (name.toLowerCase() === 'append_to_response') {
      requireValue(typeof value === 'string' && value.split(',').every(part =>
        part.length > 0 && allowedPath(`${path}/${part}`)));
    }
    params.set(name, String(value));
  }
  return params;
}

function rewriteImages(value, size) {
  if (value === null || typeof value !== 'object') return value;
  for (const key of Object.keys(value)) {
    const item = value[key];
    if (!imageFields.has(key) || item === null || item === '') {
      rewriteImages(item, size);
      continue;
    }
    requireValue(typeof item === 'string' && !control.test(item) && filePattern.test(item),
      'UPSTREAM_FORMAT');
    value[key] = `${imagePrefix}${size}${item}`;
  }
  return value;
}

function header(headers, name) {
  if (typeof headers?.get === 'function') return headers.get(name) ?? '';
  const entry = Object.entries(headers ?? {}).find(([key]) => key.toLowerCase() === name);
  return typeof entry?.[1] === 'string' ? entry[1] : '';
}

function responseData(response, path, size) {
  requireValue(object(response) && Number.isInteger(response.status) &&
    response.status >= 100 && response.status <= 599 && Buffer.isBuffer(response.body),
    'UPSTREAM_FORMAT');
  requireValue(response.status < 300 || response.status >= 400, 'UPSTREAM_HTTP');
  const text = response.body.toString('utf8');
  const html = /(?:text\/html|application\/xhtml\+xml)/i.test(header(response.headers, 'content-type')) ||
    /^\s*</.test(text);
  const interaction = /(?:captcha|cf-chl-|challenge-platform|turnstile|verify\s+(?:you|that)|verification|log[ -]?in|sign[ -]?in|登录|验证码|人机)/i;
  requireValue(!(header(response.headers, 'cf-mitigated').toLowerCase() === 'challenge' ||
    html && interaction.test(text)), 'WEB_INTERACTION_REQUIRED');
  requireValue(response.status >= 200 && response.status < 300, 'UPSTREAM_HTTP');
  requireValue(!html && Buffer.from(text, 'utf8').equals(response.body), 'UPSTREAM_FORMAT');
  let value;
  try { value = JSON.parse(text); } catch { throw new Failure('UPSTREAM_FORMAT'); }
  requireValue(object(value) || Array.isArray(value), 'UPSTREAM_FORMAT');
  requireValue(value.success !== false, 'UPSTREAM_HTTP');
  try { value = rewriteImages(value, size); }
  catch (error) {
    if (error instanceof Failure) throw error;
    throw new Failure('UPSTREAM_FORMAT');
  }
  if (path === '/3/configuration' && object(value.images)) {
    for (const key of ['base_url', 'secure_base_url']) {
      if (Object.hasOwn(value.images, key)) value.images[key] = imagePrefix;
    }
  }
  return value;
}

export async function requestTMDB(input, { settings, request, signal }) {
  requireValue(record(input) && Object.hasOwn(input, 'path') &&
    Object.keys(input).every(key => ['path', 'query', 'imageSize'].includes(key)));
  requireValue(allowedPath(input.path) && object(settings) && typeof request === 'function' &&
    (signal === undefined || signal instanceof AbortSignal));
  const path = input.path;
  const size = imageSize(input.imageSize === undefined ? 'original' : input.imageSize);
  const url = new URL(`${baseURL(settings.apiBase)}${path}`);
  url.search = queryParams(input.query === undefined ? {} : input.query, path).toString();
  const { apiKey, accessToken } = settings;
  requireValue(typeof apiKey === 'string' && !control.test(apiKey) &&
    typeof accessToken === 'string' && !control.test(accessToken));
  const headers = { Accept: 'application/json' };
  // Both authenticate TMDB reads; prefer the token so no key is placed in the URL.
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  else if (apiKey) url.searchParams.set('api_key', apiKey);
  requireValue(!signal?.aborted, 'CANCELLED');
  let response;
  try {
    response = await request(url.href, { method: 'GET', headers, signal, followRedirect: false });
  } catch (error) {
    if (signal?.aborted || error?.name === 'AbortError' ||
        ['CANCELLED', 'ABORT_ERR'].includes(error?.code)) throw new Failure('CANCELLED');
    throw new Failure('NETWORK_ERROR');
  }
  requireValue(!signal?.aborted, 'CANCELLED');
  return responseData(response, path, size);
}

export function tmdbImageTarget(path, settings) {
  requireValue(typeof path === 'string' && !control.test(path) && object(settings));
  const match = /^\/v1\/tmdb\/images\/(original|[wh][1-9]\d*)\/([A-Za-z0-9_-]+\.[A-Za-z0-9]+)$/.exec(path);
  requireValue(match !== null);
  return {
    url: `${baseURL(settings.imageBase)}/${match[1]}/${match[2]}`,
    headers: { Accept: 'image/*' },
  };
}
