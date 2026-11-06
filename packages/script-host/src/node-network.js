import http from 'node:http';
import https from 'node:https';
import { randomBytes } from 'node:crypto';
import { gunzip, inflate, brotliDecompress } from 'node:zlib';
import { promisify } from 'node:util';
import { fault, requireValue, safeError } from './errors.js';

const knownOptions = new Set([
  'method', 'headers', 'body', 'form', 'formData', 'timeout', 'json', 'encoding', 'follow_max', 'followRedirect', 'retry',
]);
// LX request does not consume retry; accept it without replaying user requests.
const forbiddenHeaders = new Set([
  'host', 'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);
const redirects = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: promisify(gunzip), deflate: promisify(inflate), br: promisify(brotliDecompress) };

function buffer(value) {
  if (typeof value === 'string') return Buffer.from(value);
  if (ArrayBuffer.isView(value)) return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  if (value instanceof ArrayBuffer) return Buffer.from(value);
  if (value?.type === 'Buffer' && Array.isArray(value.data)) return Buffer.from(value.data);
  throw fault('INVALID_ARGUMENT');
}

function prepare(options) {
  requireValue(Object.keys(options).every(key => knownOptions.has(key)), 'UNSUPPORTED_OPTION');
  const method = String(options.method ?? 'GET').toUpperCase();
  requireValue(['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'].includes(method));
  const headers = {};
  requireValue(options.headers === undefined ||
    (options.headers && typeof options.headers === 'object' && !Array.isArray(options.headers)));
  for (const [key, value] of Object.entries(options.headers ?? {})) {
    const lower = key.toLowerCase();
    requireValue(!forbiddenHeaders.has(lower), 'PERMISSION_DENIED');
    if (lower === 'connection') {
      requireValue(['close', 'keep-alive'].includes(String(value).toLowerCase()), 'PERMISSION_DENIED');
      continue; // No shared agent/session configuration from script options.
    }
    requireValue(typeof value === 'string' && !/[\r\n]/.test(value));
    headers[lower] = value;
  }
  requireValue([options.body, options.form, options.formData].filter(v => v !== undefined).length <= 1);
  let body;
  if (options.body !== undefined) {
    if (options.body && typeof options.body === 'object' && !ArrayBuffer.isView(options.body) &&
        !(options.body instanceof ArrayBuffer) && options.body.type !== 'Buffer') {
      body = Buffer.from(JSON.stringify(options.body));
      headers['content-type'] ??= 'application/json';
    } else body = buffer(options.body);
  } else if (options.form !== undefined) {
    requireValue(options.form && typeof options.form === 'object' && !Array.isArray(options.form));
    const form = new URLSearchParams();
    for (const [key, value] of Object.entries(options.form)) form.set(key, String(value));
    body = Buffer.from(form.toString());
    headers['content-type'] ??= 'application/x-www-form-urlencoded';
  } else if (options.formData !== undefined) {
    requireValue(options.formData && typeof options.formData === 'object' && !Array.isArray(options.formData));
    const boundary = `meowfilm-${randomBytes(16).toString('hex')}`;
    const chunks = [];
    const append = chunk => {
      const b = typeof chunk === 'string' ? Buffer.from(chunk) : chunk;
      chunks.push(b);
    };
    const quote = text => {
      requireValue(typeof text === 'string' && !/[\r\n"]/.test(text));
      return text;
    };
    for (const [name, value] of Object.entries(options.formData)) {
      let data = value, filename, contentType;
      if (value && typeof value === 'object' && Object.hasOwn(value, 'value')) {
        data = value.value;
        filename = value.options?.filename ?? 'blob';
        contentType = value.options?.contentType ?? 'application/octet-stream';
      } else if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer || value?.type === 'Buffer') {
        filename = 'blob'; contentType = 'application/octet-stream';
      }
      append(`--${boundary}\r\nContent-Disposition: form-data; name="${quote(name)}"`);
      if (filename) append(`; filename="${quote(filename)}"\r\nContent-Type: ${quote(contentType)}`);
      append('\r\n\r\n');
      append(filename ? buffer(data) : String(data));
      append('\r\n');
    }
    append(`--${boundary}--\r\n`);
    body = Buffer.concat(chunks);
    headers['content-type'] = `multipart/form-data; boundary=${boundary}`;
  }
  if (body) {
    headers['content-length'] = String(body.length);
  }
  return { method, headers, body };
}

function requestOnce(url, { method, headers, body }, signal, follow) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(signal.reason ?? fault('CANCELLED')); return; }
    const transport = url.protocol === 'https:' ? https : http;
    const request = transport.request(url, { method, headers, signal, agent: false }, response => {
      if (follow && redirects.has(response.statusCode) && response.headers.location) {
        response.destroy(); // No redirect body buffering before validating the next URL.
        resolve({ statusCode: response.statusCode, headers: response.headers, body: Buffer.alloc(0) });
        return;
      }
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.once('error', reject);
      response.once('aborted', () => reject(signal.aborted
        ? signal.reason ?? fault('CANCELLED') : fault('NETWORK_ERROR')));
      response.once('end', () => resolve({
        statusCode: response.statusCode, statusMessage: response.statusMessage,
        headers: response.headers, body: Buffer.concat(chunks),
      }));
    });
    request.once('error', error => reject(signal.aborted
      ? signal.reason ?? fault('CANCELLED') : error));
    request.end(body);
  });
}

/**
 * Optional, explicitly injected adapter for metadata HTTP, not a media relay.
 * Validate HTTP(S) on every redirect and never forward credentials cross-origin.
 */
export function createNodeNetwork({
  maxRedirects = 5, ...unsupported
} = {}) {
  requireValue(Object.keys(unsupported).length === 0);
  requireValue(Number.isSafeInteger(maxRedirects) && maxRedirects >= 0 && maxRedirects <= 10);
  return async ({ url: input, options = {}, signal, validateURL }) => {
    requireValue(signal && typeof validateURL === 'function');
    requireValue(options.followRedirect === undefined || typeof options.followRedirect === 'boolean');
    const requestedFollow = options.followRedirect === false ? 0 : options.follow_max ?? maxRedirects;
    requireValue(Number.isSafeInteger(requestedFollow) && requestedFollow >= 0);
    const follow = Math.min(requestedFollow, maxRedirects);
    let request = prepare(options);
    let url = new URL(validateURL(input));
    try {
      for (let hop = 0; ; hop++) {
        if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
        const response = await requestOnce(url, request, signal, follow > 0);
        if (follow > 0 && redirects.has(response.statusCode) && response.headers.location) {
          requireValue(hop < follow, 'RESOURCE_LIMIT');
          const next = new URL(validateURL(new URL(response.headers.location, url).href));
          if (next.origin !== url.origin) {
            request = { ...request, headers: { ...request.headers } };
            delete request.headers.authorization;
            delete request.headers.cookie;
          }
          if ((response.statusCode === 303 && request.method !== 'HEAD') ||
              ([301, 302].includes(response.statusCode) && request.method === 'POST')) {
            request = { method: 'GET', headers: { ...request.headers }, body: undefined };
            delete request.headers['content-length'];
            delete request.headers['content-type'];
          }
          url = next;
          continue;
        }
        const encoding = String(response.headers['content-encoding'] ?? 'identity').toLowerCase();
        const decompressor = decompressors[encoding];
        if (encoding !== 'identity') {
          requireValue(decompressor, 'UNSUPPORTED_OPTION');
          try { response.body = await decompressor(response.body); }
          catch (error) {
            throw fault('NETWORK_ERROR');
          }
          delete response.headers['content-encoding'];
          delete response.headers['content-length'];
        }
        if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
        return { ...response, url: url.href };
      }
    } catch (e) { throw safeError(e, 'NETWORK_ERROR'); }
  };
}
