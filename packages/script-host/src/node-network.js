import http from 'node:http';
import https from 'node:https';
import { randomBytes } from 'node:crypto';
import { Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';
import { fault, requireValue, safeError } from './errors.js';

const knownOptions = new Set([
  'method', 'headers', 'body', 'form', 'formData', 'timeout', 'json', 'encoding', 'follow_max', 'followRedirect', 'retry',
]);
// LX request does not consume retry; accept it without replaying user requests.
const forbiddenHeaders = new Set([
  'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);
const redirects = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };

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

async function requestOnce(url, { method, headers, body }, signal, follow) {
  if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
  const resources = [], closing = [];
  const own = resource => {
    resources.push(resource);
    closing.push(new Promise(resolve => resource.once('close', resolve)));
    return resource;
  };
  try {
    const response = await new Promise((resolve, reject) => {
      const transport = url.protocol === 'https:' ? https : http;
      const request = own(transport.request(url, { method, headers, signal, agent: false }, incoming => {
        own(incoming);
        // Cover the interval between headers and installation of the pipeline.
        incoming.on('error', () => {});
        resolve(incoming);
      }));
      request.once('socket', socket => own(socket));
      request.on('error', error => reject(signal.aborted
        ? signal.reason ?? fault('CANCELLED') : error));
      request.end(body);
    });
    if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
    if (follow && redirects.has(response.statusCode) && response.headers.location) {
      // Finally closes this hop without collecting a redirect body.
      return { statusCode: response.statusCode, headers: response.headers, body: Buffer.alloc(0) };
    }
    const responseHeaders = { ...response.headers };
    const encodings = method === 'HEAD' || [204, 205, 304].includes(response.statusCode) ? [] :
      String(response.headers['content-encoding'] ?? 'identity')
        .split(',').map(value => value.trim().toLowerCase()).reverse();
    const streams = [response];
    for (const encoding of encodings) {
      if (encoding === 'identity') continue;
      requireValue(Object.hasOwn(decompressors, encoding), 'UNSUPPORTED_OPTION');
      streams.push(own(decompressors[encoding]()));
    }
    if (streams.length > 1) {
      delete responseHeaders['content-encoding'];
      delete responseHeaders['content-length'];
    }
    const chunks = [];
    let length = 0;
    streams.push(own(new Writable({
      write(chunk, _encoding, callback) {
        length += chunk.length;
        chunks.push(chunk);
        callback();
      },
    })));
    // The complete-response API retains decoded bytes only; compressed input
    // and each decoder stay streaming and cancellable, including after EOF.
    await pipeline(streams, { signal });
    if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
    return { statusCode: response.statusCode, statusMessage: response.statusMessage,
      headers: responseHeaders, body: chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, length) };
  } finally {
    for (const resource of resources) resource.destroy();
    await Promise.all(closing);
  }
}

/**
 * Optional, explicitly injected adapter for metadata HTTP, not a media relay.
 * Validate HTTP(S) on every redirect and preserve explicit caller headers.
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
          if ((response.statusCode === 303 && request.method !== 'HEAD') ||
              ([301, 302].includes(response.statusCode) && request.method === 'POST')) {
            request = { method: 'GET', headers: { ...request.headers }, body: undefined };
            delete request.headers['content-length'];
            delete request.headers['content-type'];
          }
          url = next;
          continue;
        }
        if (signal.aborted) throw signal.reason ?? fault('CANCELLED');
        return { ...response, url: url.href };
      }
    } catch (e) {
      throw signal.aborted ? safeError(signal.reason, 'CANCELLED') : safeError(e, 'NETWORK_ERROR');
    }
  };
}
