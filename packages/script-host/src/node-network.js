import http from 'node:http';
import https from 'node:https';
import { randomBytes } from 'node:crypto';
import { gunzipSync, inflateSync, brotliDecompressSync } from 'node:zlib';
import { fault, requireValue, safeError } from './errors.js';

const knownOptions = new Set([
  'method', 'headers', 'body', 'form', 'formData', 'timeout', 'json', 'encoding',
]);
const forbiddenHeaders = new Set([
  'host', 'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);
const redirects = new Set([301, 302, 303, 307, 308]);

function buffer(value) {
  if (typeof value === 'string') return Buffer.from(value);
  if (ArrayBuffer.isView(value)) return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  if (value instanceof ArrayBuffer) return Buffer.from(value);
  throw fault('INVALID_ARGUMENT');
}

function prepare(options, limit) {
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
    if (options.json && typeof options.body === 'object' && !ArrayBuffer.isView(options.body) &&
        !(options.body instanceof ArrayBuffer)) {
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
    let length = 0;
    const append = chunk => {
      const b = typeof chunk === 'string' ? Buffer.from(chunk) : chunk;
      length += b.length;
      requireValue(length <= limit, 'RESOURCE_LIMIT');
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
      } else if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer) {
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
    requireValue(body.length <= limit, 'RESOURCE_LIMIT');
    headers['content-length'] = String(body.length);
  }
  return { method, headers, body };
}

function requestOnce(url, { method, headers, body }, signal, limit) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(signal.reason ?? fault('CANCELLED')); return; }
    const transport = url.protocol === 'https:' ? https : http;
    const request = transport.request(url, { method, headers, signal, agent: false }, response => {
      if (redirects.has(response.statusCode) && response.headers.location) {
        response.destroy(); // No redirect body buffering before authorization.
        resolve({ statusCode: response.statusCode, headers: response.headers, body: Buffer.alloc(0) });
        return;
      }
      const chunks = [];
      let bytes = 0;
      response.on('data', chunk => {
        bytes += chunk.length;
        if (bytes > limit) {
          // destroy emits "aborted" before "error" on some Node versions.
          const error = fault('RESOURCE_LIMIT');
          reject(error);
          response.destroy(error);
        }
        else chunks.push(chunk);
      });
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
 * Call authorizeURL for every redirect and never forward credentials cross-origin.
 */
export function createNodeNetwork({ maxResponseBytes = 2 * 1024 * 1024, maxRedirects = 3 } = {}) {
  requireValue(Number.isSafeInteger(maxResponseBytes) && maxResponseBytes > 0);
  requireValue(Number.isSafeInteger(maxRedirects) && maxRedirects >= 0 && maxRedirects <= 10);
  return async ({ url: input, options = {}, signal, authorizeURL }) => {
    requireValue(signal && typeof authorizeURL === 'function');
    let request = prepare(options, maxResponseBytes);
    let url = new URL(authorizeURL(input));
    try {
      for (let hop = 0; ; hop++) {
        if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
        const response = await requestOnce(url, request, signal, maxResponseBytes);
        if (redirects.has(response.statusCode) && response.headers.location) {
          requireValue(hop < maxRedirects, 'RESOURCE_LIMIT');
          const next = new URL(authorizeURL(new URL(response.headers.location, url).href));
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
        const decompressor = { gzip: gunzipSync, deflate: inflateSync, br: brotliDecompressSync }[encoding];
        if (encoding !== 'identity') {
          requireValue(decompressor, 'UNSUPPORTED_OPTION');
          try { response.body = decompressor(response.body, { maxOutputLength: maxResponseBytes }); }
          catch { throw fault('RESOURCE_LIMIT'); }
        }
        return { ...response, url: url.href };
      }
    } catch (e) { throw safeError(e, 'NETWORK_ERROR'); }
  };
}
