import {
  decodeHttpBody, encodeText, hostApiVersion,
} from './capabilities.js';
import {
  sourceFault, sourceIdentifier, sourceObject, sourceRequire,
} from './errors.js';

function jsonObject(value) {
  if (value === undefined || value === null || value === '') return {};
  for (let depth = 0; typeof value === 'string' && depth < 3; depth++) {
    const input = value.trim();
    if (!input) return {};
    try { value = JSON.parse(input); }
    catch { throw sourceFault('INVALID_ARGUMENT'); }
  }
  return sourceObject(value);
}

export function normalizeHeaders(value) {
  const input = jsonObject(value);
  sourceRequire(Object.keys(input).length <= 128);
  const output = {};
  for (const [key, raw] of Object.entries(input)) {
    sourceRequire(/^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,128}$/.test(key));
    const values = Array.isArray(raw) ? raw : [raw];
    sourceRequire(values.length > 0 && values.length <= 32);
    output[key] = values.map(item => {
      const value = String(item);
      sourceRequire(value.length <= 16384 && !/[\r\n]/.test(value));
      return value;
    });
  }
  return output;
}

function headerValue(headers, name) {
  const entry = Object.entries(headers).find(([key]) => key.toLowerCase() === name);
  return entry ? entry[1].join(', ') : '';
}

function responseEncoding(headers, fallback) {
  const contentType = headerValue(headers, 'content-type');
  const match = /(?:^|;)\s*charset\s*=\s*["']?([^;"'\s]+)/i.exec(contentType);
  return match?.[1] || fallback || 'utf-8';
}

function normalizeUrl(value, baseUrl) {
  let input = String(value ?? '').trim();
  if (input.startsWith('http2://')) input = `http://${input.slice(8)}`;
  else if (input.startsWith('https2://')) input = `https://${input.slice(9)}`;
  try { return new URL(input, baseUrl || undefined).href; }
  catch { throw sourceFault('INVALID_ARGUMENT'); }
}

function formBody(value) {
  const params = new URLSearchParams();
  for (const [key, item] of Object.entries(sourceObject(value))) {
    if (Array.isArray(item)) for (const child of item) params.append(key, String(child));
    else params.append(key, String(item ?? ''));
  }
  return params.toString();
}

function normalizeCapabilityScope(value) {
  sourceObject(value);
  sourceRequire(Object.keys(value).every(key =>
    ['installationId', 'generation', 'protocol', 'siteId'].includes(key)) &&
    ['installationId', 'generation', 'protocol', 'siteId'].every(key =>
      Object.hasOwn(value, key)));
  sourceIdentifier(value.installationId);
  sourceIdentifier(value.protocol);
  sourceIdentifier(value.siteId);
  sourceRequire(Number.isSafeInteger(value.generation) && value.generation > 0);
  return Object.freeze({
    installationId: value.installationId,
    generation: value.generation,
    protocol: value.protocol,
    siteId: value.siteId,
  });
}

export function trustedCapabilityContext(scope, context = {}) {
  const trusted = normalizeCapabilityScope(scope);
  sourceObject(context);
  sourceRequire(context.invocationId === undefined ||
    Number.isSafeInteger(context.invocationId) && context.invocationId > 0);
  const rest = { ...context };
  delete rest.scope;
  return Object.freeze({
    ...rest,
    scope: Object.freeze({
      ...trusted,
      requestId: context.invocationId ?? 0,
    }),
  });
}

export class SourceHttpClient {
  constructor({ capabilities, profile, scope }) {
    sourceRequire(capabilities?.version === hostApiVersion && typeof capabilities.call === 'function');
    sourceRequire(typeof profile === 'string' && profile.length > 0);
    this.capabilities = capabilities;
    this.profile = profile;
    this.scope = normalizeCapabilityScope(scope);
  }

  async request(input, context = {}) {
    sourceObject(input);
    sourceRequire(input.cookieProfile === undefined, 'PERMISSION_DENIED');
    const trustedContext = trustedCapabilityContext(this.scope, context);
    const headers = normalizeHeaders(input.headers);
    const hasContentType = Object.keys(headers).some(key => key.toLowerCase() === 'content-type');
    if (!hasContentType && input.contentType) {
      headers['Content-Type'] = normalizeHeaders({ 'Content-Type': input.contentType })['Content-Type'];
    }
    const method = String(input.method ?? (input.body !== undefined || input.data !== undefined ? 'POST' : 'GET'))
      .toUpperCase();
    const contentType = headerValue(headers, 'content-type');
    let body = input.body ?? input.data;
    if (body !== undefined && body !== null && typeof body !== 'string' &&
        !(body instanceof Uint8Array) && !ArrayBuffer.isView(body)) {
      if (/application\/x-www-form-urlencoded/i.test(contentType)) body = formBody(body);
      else {
        body = JSON.stringify(body);
        if (!contentType && !hasContentType) headers['Content-Type'] = ['application/json'];
      }
    }
    let bytes = new Uint8Array();
    if (body instanceof Uint8Array) bytes = body;
    else if (ArrayBuffer.isView(body)) {
      bytes = new Uint8Array(body.buffer, body.byteOffset, body.byteLength);
    } else if (body !== undefined && body !== null && body !== '') {
      bytes = await encodeText(
        String(body), this.capabilities, input.charset ?? 'utf-8', trustedContext,
      );
    }
    const response = await this.capabilities.call('http.request', {
      version: hostApiVersion,
      url: normalizeUrl(input.url, input.baseUrl),
      method,
      headers,
      body: bytes,
      timeoutMs: Math.max(1, Math.min(Math.trunc(input.timeoutMs ?? input.timeout ?? 15000), 120000)),
      redirects: input.followRedirects === false || input.followRedirect === false
        ? 0 : Math.max(0, Math.min(Math.trunc(input.redirects ?? input.follow_max ?? 5), 10)),
      cookieProfile: `${this.profile}:http`,
    }, trustedContext);
    const encoding = responseEncoding(response.headers, input.responseEncoding ?? input.charset);
    const data = await decodeHttpBody(
      response, this.capabilities, encoding, trustedContext,
    );
    return {
      statusCode: response.status,
      statusMessage: response.statusText,
      headers: response.headers,
      url: response.url,
      body: response.body,
      data,
      encoding,
    };
  }
}
