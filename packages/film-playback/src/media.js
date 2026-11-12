import http from 'node:http';
import https from 'node:https';
import { randomBytes } from 'node:crypto';
import { pipeline } from 'node:stream/promises';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';
import { compileDASHTemplate, dashPrefetchPlan, decodeManifest, hlsPrefetchPlan, manifestFormat, rewriteDASH, rewriteHLS } from './manifests.js';
import { cacheCoverage, cacheGaps, createMediaCachePool, firstDASHRange, progressivePrefetchBytes } from './media-cache.js';

// Resource budgets bound this local transport, not movie length or business result counts.
const registryBudget = 16 * 1024 * 1024;
const manifestBudget = 8 * 1024 * 1024;
const pendingManifestBudget = 64 * 1024 * 1024;
const maximumActiveRequests = 64;
const maximumRedirects = 10;
const responseHeaders = [
  'content-type', 'content-length', 'content-range', 'content-encoding', 'accept-ranges',
  'cache-control', 'etag', 'last-modified', 'expires', 'date',
];
const clientHeaders = ['range', 'if-range', 'if-none-match', 'if-modified-since'];
const transportHeaders = new Set([
  'connection', 'keep-alive', 'transfer-encoding', 'content-length', 'upgrade', 'te', 'trailer',
]);
const redirectStatuses = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };

function failure(code) {
  return Object.assign(new Error(code), { code });
}

const strongETag = value => typeof value === 'string' && /^"[\x21\x23-\x7e\x80-\xff]*"$/.test(value);
const imfDate = /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun), (\d{2}) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (\d{4}) (\d{2}:\d{2}:\d{2}) GMT$/;
const obsoleteDate = /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), (\d{2})-(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)-(\d{2}) (\d{2}:\d{2}:\d{2}) GMT$/;
const asciiDate = /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) ([ \d]\d) (\d{2}:\d{2}:\d{2}) (\d{4})$/;

function httpDate(value) {
  if (typeof value !== 'string') return null;
  const imf = imfDate.exec(value), obsolete = obsoleteDate.exec(value), ascii = asciiDate.exec(value);
  const time = Date.parse(ascii ? value + ' GMT' : value);
  if (!Number.isFinite(time) || !imf && !obsolete && !ascii) return null;
  const canonical = imf ? value : obsolete
    ? `${obsolete[1].slice(0, 3)}, ${obsolete[2]} ${obsolete[3]} ${new Date(time).getUTCFullYear()} ${obsolete[5]} GMT`
    : `${ascii[1]}, ${ascii[3].trim().padStart(2, '0')} ${ascii[2]} ${ascii[5]} ${ascii[4]} GMT`;
  return new Date(time).toUTCString() === canonical ? time : null;
}

function matchesIfRange(value, headers) {
  if (strongETag(value)) return value === headers.etag;
  const time = httpDate(value);
  return time !== null && time === httpDate(headers['last-modified']);
}

function address(value, base) {
  try {
    const url = new URL(value, base);
    if (!['http:', 'https:'].includes(url.protocol)) throw failure();
    url.hash = '';
    return url;
  } catch { throw failure('MEDIA_INVALID_DESCRIPTOR'); }
}

function headers(value = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw failure('MEDIA_INVALID_DESCRIPTOR');
  const result = {};
  try {
    for (const [key, entry] of Object.entries(value)) {
      const name = key.toLowerCase();
      if (typeof entry !== 'string' || Object.hasOwn(result, name)) throw failure();
      http.validateHeaderName(name);
      http.validateHeaderValue(name, entry);
      // Framing belongs to this streaming HTTP hop; source Host and credentials
      // remain source-provided values, not an access policy.
      if (!transportHeaders.has(name)) result[name] = entry;
    }
  } catch { throw failure('MEDIA_INVALID_DESCRIPTOR'); }
  return result;
}

function descriptorInput(descriptor, scope) {
  if (!descriptor || typeof descriptor !== 'object') throw failure('MEDIA_INVALID_DESCRIPTOR');
  const url = address(descriptor.url);
  const requestHeaders = headers(descriptor.headers);
  if (descriptor.format !== undefined && typeof descriptor.format !== 'string') throw failure('MEDIA_INVALID_DESCRIPTOR');
  if (scope !== undefined && (!scope || typeof scope !== 'object' || Array.isArray(scope))) throw failure('MEDIA_INVALID_DESCRIPTOR');
  const identity = {};
  for (const name of ['sourceKey', 'sourceRevision', 'sourceEngine', 'sourceInstallationId',
    'accountProvider', 'accountRevision', 'fileProvider', 'fileAccountRevision']) {
    const value = scope?.[name];
    if (value !== undefined) {
      if (!['string', 'number'].includes(typeof value) || typeof value === 'number' && !Number.isFinite(value)) {
        throw failure('MEDIA_INVALID_DESCRIPTOR');
      }
      identity[name] = value;
    }
  }
  let drm;
  if (descriptor.drm !== undefined) {
    // This transport passes initialization metadata; the decoder decides which
    // schemes it supports. Merely carrying a license URL does not request it.
    try { drm = structuredClone(descriptor.drm); } catch { throw failure('MEDIA_INVALID_DESCRIPTOR'); }
  }
  return { url: url.href, headers: requestHeaders, scope: Object.freeze(identity), format: descriptor.format, drm };
}

function sendError(response, status, code) {
  if (response.destroyed) return;
  if (response.headersSent) { response.destroy(); return; }
  response.writeHead(status, {
    'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(code),
  });
  response.end(code);
}

function copyHeaders(incoming, { rewritten = false } = {}) {
  const result = { 'cache-control': 'private, no-store' };
  for (const name of responseHeaders) {
    if (incoming[name] !== undefined && !(rewritten && [
      'content-length', 'content-range', 'content-encoding', 'etag', 'last-modified', 'accept-ranges',
    ].includes(name))) result[name] = incoming[name];
  }
  // A released resource must not be reused after account/source revocation.
  result['cache-control'] = 'private, no-store';
  return result;
}

function rangeFor(value, length) {
  if (value === undefined) return null;
  const match = /^bytes=(\d*)-(\d*)$/.exec(value);
  if (!match || !match[1] && !match[2]) return false;
  let start = match[1] ? Number(match[1]) : Math.max(0, length - Number(match[2]));
  let end = match[1] && match[2] ? Number(match[2]) : length - 1;
  if (![start, end].every(Number.isSafeInteger) || start < 0 || start >= length || end < start) return false;
  end = Math.min(end, length - 1);
  return { start, end };
}

export function createMediaService() {
  const owners = new Map(), operations = new Set(), inboundSockets = new Map();
  const cachePool = createMediaCachePool();
  let server, starting, closing, closed = false, baseUrl, registryBytes = 0, pendingManifestBytes = 0;

  function socketDrain(socket, collection) {
    if (collection.has(socket)) return collection.get(socket);
    const done = socket.closed ? Promise.resolve() : new Promise(resolve => socket.once('close', resolve));
    collection.set(socket, done);
    done.then(() => collection.delete(socket));
    return done;
  }

  async function start() {
    if (closed) throw failure('MEDIA_CLOSED');
    if (!starting) {
      server = http.createServer((request, response) => { void handle(request, response); });
      server.maxConnections = 128;
      server.headersTimeout = 10000;
      server.requestTimeout = 30000;
      server.on('connection', socket => {
        socketDrain(socket, inboundSockets);
        if (closed) socket.destroy();
      });
      server.on('clientError', (_error, socket) => socket.destroy());
      starting = new Promise((resolve, reject) => {
        server.once('error', () => reject(failure('MEDIA_LISTEN_FAILED')));
        server.listen(0, () => {
          baseUrl = `http://127.0.0.1:${server.address().port}`;
          resolve();
        });
      });
    }
    await starting;
    if (closed) throw failure('MEDIA_CLOSED');
  }

  function reserve(bytes) {
    if (registryBytes + bytes > registryBudget) throw failure('MEDIA_RESOURCE_LIMIT');
    registryBytes += bytes;
  }

  function register(owner, specification, added) {
    if (owner.revoked) throw failure('MEDIA_CANCELLED');
    const key = JSON.stringify(specification);
    const existing = owner.keys.get(key);
    if (existing) return existing.url;
    const group = specification.resources !== undefined;
    const routes = new Map();
    if (group) for (const resource of specification.resources) {
      if (routes.has(resource.path)) throw failure('MEDIA_MANIFEST_INVALID');
      routes.set(resource.path, resource);
    }
    const bytes = 512 + Buffer.byteLength(key) * 2 + (specification.resources?.length ?? 0) * 512;
    const token = randomBytes(24).toString('base64url');
    const record = { token, bytes, key, specification, routes, group,
      url: `${baseUrl}/media/${owner.id}/${token}${group ? '/' : ''}` };
    if (group) record.templates = specification.resources.filter(resource => resource.template).map(resource => ({
      resolve: compileDASHTemplate(resource.url, resource.path), resource,
    }));
    reserve(bytes);
    owner.keys.set(key, record);
    owner.tokens.set(token, record);
    added?.push(record);
    return record.url;
  }

  function forget(owner, record) {
    owner.keys.delete(record.key);
    owner.tokens.delete(record.token);
    registryBytes -= record.bytes;
  }

  function resolveResource(owner, token, suffix) {
    const record = owner.tokens.get(token);
    if (!record) return null;
    if (!record.group) return suffix === undefined ? record.specification : null;
    if (suffix === undefined) return null;
    const direct = record.routes.get(suffix);
    if (direct && !direct.template) return { url: direct.url, format: 'binary' };
    for (const template of record.templates) {
      const url = template.resolve(suffix);
      if (url) return { url, format: 'binary' };
    }
    return null;
  }

  function operation(owner, response) {
    if (owner.revoked) throw failure('MEDIA_CANCELLED');
    const streams = new Map(), controller = new AbortController();
    let finish;
    const task = { controller, response, done: new Promise(resolve => { finish = resolve; }), streams, controlBytes: 0,
      track(stream) {
        socketDrain(stream, streams);
        stream.on('error', () => {}); // Only closed error codes escape the transport.
        if (controller.signal.aborted) stream.destroy();
        return stream;
      },
      cancel(code = 'MEDIA_CANCELLED') {
        if (!controller.signal.aborted) controller.abort(failure(code));
        if (response?.socket) task.track(response.socket);
        for (const stream of streams.keys()) stream.destroy();
        response?.destroy();
      },
      async finish() {
        for (const stream of streams.keys()) if (stream !== response) stream.destroy();
        while (streams.size) await Promise.all([...streams.values()]);
        pendingManifestBytes -= task.controlBytes;
        response?.removeListener('close', disconnected);
        operations.delete(task);
        owner.operations.delete(task);
        finish();
      },
    };
    const disconnected = () => { if (!response.writableFinished) task.cancel(); };
    response?.once('close', disconnected);
    if (response) task.track(response);
    operations.add(task);
    owner.operations.add(task);
    return task;
  }

  function exchange(task, url, method, values) {
    if (task.controller.signal.aborted) throw task.controller.signal.reason;
    return new Promise((resolve, reject) => {
      const request = task.track((url.protocol === 'https:' ? https : http).request(url, {
        method, headers: values, agent: false,
      }));
      // Bound connection/header stalls, but do not time out a media body paused by backpressure.
      const timer = setTimeout(() => {
        request.destroy();
        reject(failure('MEDIA_UPSTREAM_TIMEOUT'));
      }, 30000);
      timer.unref();
      request.once('socket', socket => task.track(socket));
      request.once('response', incoming => {
        clearTimeout(timer);
        task.track(incoming);
        resolve(incoming);
      });
      request.once('error', () => reject(task.controller.signal.reason ?? failure('MEDIA_UPSTREAM_ERROR')));
      request.once('close', () => {
        clearTimeout(timer);
        reject(task.controller.signal.reason ?? failure('MEDIA_UPSTREAM_ERROR'));
      });
      request.end();
    });
  }

  async function upstream(task, owner, resource, request, withoutRange = false) {
    let url = address(resource.url);
    const visited = new Set();
    for (let redirects = 0; ; redirects++) {
      if (visited.has(url.href) || redirects > maximumRedirects) throw failure('MEDIA_REDIRECT_ERROR');
      visited.add(url.href);
      const values = { ...owner.headers, 'accept-encoding': 'identity' };
      for (const name of clientHeaders) {
        if (!(withoutRange && ['range', 'if-range'].includes(name)) && request.headers[name] !== undefined) {
          values[name] = request.headers[name];
        }
      }
      if (withoutRange) {
        delete values.range;
        delete values['if-range'];
        delete values['if-none-match'];
        delete values['if-modified-since'];
      }
      const incoming = await exchange(task, url, request.method, values);
      if (!redirectStatuses.has(incoming.statusCode)) {
        if (owner.scope.sourceEngine === 'cat' && manifestFormat(resource.url, '', resource.format) &&
            request.method === 'GET' && incoming.statusCode === 502 &&
            incoming.headers['content-type']?.split(';', 1)[0].trim().toLowerCase() === 'application/json' &&
            ['', 'identity'].includes(incoming.headers['content-encoding']?.trim().toLowerCase() ?? '')) {
          // Cat validates the original manifest before rewriting it. Its existing
          // INVALID_RESPONSE envelope must retain that meaning at this media boundary.
          const body = await readManifestBytes(task, incoming);
          let code;
          try { code = JSON.parse(decodeManifest(body))?.error?.code; } catch {}
          if (code === 'INVALID_RESPONSE') throw failure('MEDIA_MANIFEST_INVALID');
          return { incoming, url: url.href, errorBody: body };
        }
        return { incoming, url: url.href };
      }
      const location = incoming.headers.location;
      incoming.destroy();
      if (!location) throw failure('MEDIA_REDIRECT_ERROR');
      try { url = address(location, url); } catch { throw failure('MEDIA_REDIRECT_ERROR'); }
    }
  }

  async function readManifestBytes(task, incoming) {
    let stream = incoming, bytes = 0;
    const chunks = [];
    const encoding = incoming.headers['content-encoding']?.trim().toLowerCase();
    if (encoding && encoding !== 'identity') {
      if (!Object.hasOwn(decompressors, encoding)) throw failure('MEDIA_MANIFEST_UNSUPPORTED');
      stream = task.track(decompressors[encoding]());
      incoming.once('error', () => stream.destroy(failure('MEDIA_UPSTREAM_ERROR')));
      incoming.pipe(stream);
    }
    for await (const chunk of stream) {
      const charge = chunk.length * 3; // Binary input plus its UTF-16 parse text.
      if (bytes + chunk.length > manifestBudget || pendingManifestBytes + charge > pendingManifestBudget) {
        throw failure('MEDIA_MANIFEST_LIMIT');
      }
      bytes += chunk.length;
      pendingManifestBytes += charge;
      task.controlBytes += charge;
      chunks.push(chunk);
    }
    if (!incoming.complete) throw failure('MEDIA_UPSTREAM_ERROR');
    return Buffer.concat(chunks, bytes);
  }

  async function readManifest(task, incoming) {
    return decodeManifest(await readManifestBytes(task, incoming));
  }

  async function sniffManifest(incoming, binary = false) {
    // Consume available bytes while probing. read(512) with a shorter unfinished buffer
    // can re-emit readable forever and starve the real socket close/error.
    const chunks = [];
    let bytes = 0, format = null;
    const limit = binary ? 1 : 512;
    while (bytes < limit) {
      if (incoming.complete && !incoming.readableLength) break;
      if (incoming.destroyed) throw failure('MEDIA_UPSTREAM_ERROR');
      const chunk = incoming.read(Math.min(limit - bytes, incoming.readableLength) || limit - bytes);
      if (chunk) {
        chunks.push(chunk); bytes += chunk.length;
        if (binary) break;
        const start = Buffer.concat(chunks, bytes).toString('utf8').replace(/^\uFEFF/, '').trimStart();
        format = start.startsWith('#EXTM3U') ? 'hls'
          : /^<(?:\?xml\b|!--|(?:[A-Za-z_][\w.-]*:)?MPD\b)/.test(start) ? 'dash' : null;
        if (format || start && !'#EXTM3U'.startsWith(start) && !start.startsWith('<') &&
            !start.startsWith('\uFFFD')) break;
      }
      if (incoming.complete && !incoming.readableLength) break;
      if (chunk) continue;
      await new Promise((resolve, reject) => {
        const finish = () => { cleanup(); resolve(); };
        const fail = () => { cleanup(); reject(failure('MEDIA_UPSTREAM_ERROR')); };
        const cleanup = () => {
          incoming.removeListener('readable', finish);
          incoming.removeListener('end', finish);
          incoming.removeListener('error', fail);
          incoming.removeListener('close', fail);
        };
        incoming.once('readable', finish);
        incoming.once('end', finish);
        incoming.once('error', fail);
        incoming.once('close', fail);
      });
    }
    if (!bytes) throw failure('MEDIA_EMPTY_RESPONSE');
    if (!binary && !format && Buffer.concat(chunks, bytes).toString('utf8').trimStart().startsWith('<')) {
      throw failure('MEDIA_MANIFEST_UNSUPPORTED');
    }
    for (const chunk of chunks.reverse()) incoming.unshift(chunk);
    return format;
  }

  function rewrittenManifest(task, owner, resource, text, url, format) {
    const added = [];
    try {
      const rewritten = (format === 'hls' ? rewriteHLS : rewriteDASH)(text, url,
        specification => register(owner, specification, added), resource.hlsVariables);
      const body = Buffer.from(rewritten);
      if (body.length > manifestBudget * 4 || pendingManifestBytes + body.length > pendingManifestBudget) {
        throw failure('MEDIA_MANIFEST_LIMIT');
      }
      pendingManifestBytes += body.length;
      task.controlBytes += body.length;
      if (task.controller.signal.aborted || owner.revoked) throw task.controller.signal.reason ?? failure('MEDIA_CANCELLED');
      return body;
    } catch (error) {
      for (const record of added) forget(owner, record);
      throw error;
    }
  }

  function controlHeaders(incoming, format) {
    return { ...copyHeaders(incoming, { rewritten: true }),
      'content-type': format === 'hls' ? 'application/vnd.apple.mpegurl' : 'application/dash+xml', 'accept-ranges': 'bytes' };
  }

  function sendDocument(request, response, headers, body) {
    // Cached documents use the service's local address. A caller on another
    // interface must receive child URLs on the endpoint it actually reached.
    const origin = `http://${request.headers.host}`;
    if (origin !== baseUrl) body = Buffer.from(body.toString().replaceAll(`${baseUrl}/media/`, `${origin}/media/`));
    // Rewritten documents have no local validator with which If-Range could match.
    const range = request.headers['if-range'] === undefined ? rangeFor(request.headers.range, body.length) : null;
    if (range === false) {
      response.writeHead(416, { ...headers, 'content-range': `bytes */${body.length}`, 'content-length': 0 });
      response.end();
    } else {
      const start = range?.start ?? 0, end = range?.end ?? body.length - 1;
      response.writeHead(range ? 206 : 200, { ...headers, 'content-length': end - start + 1,
        ...(range ? { 'content-range': `bytes ${start}-${end}/${body.length}` } : {}) });
      response.end(request.method === 'HEAD' ? undefined : body.subarray(start, end + 1));
    }
  }

  function documentKey(resource) {
    return JSON.stringify([resource.url, resource.hlsVariables ?? null]);
  }

  function responseRange(incoming) {
    const match = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(incoming.headers['content-range'] ?? '');
    if (!match) throw failure('MEDIA_RANGE_UNSUPPORTED');
    const [start, end, total] = match.slice(1).map(Number);
    if (![start, end, total].every(Number.isSafeInteger) || start < 0 || end < start || end >= total) {
      throw failure('MEDIA_RANGE_UNSUPPORTED');
    }
    return { start, end, total };
  }

  async function writeBytes(response, body) {
    for (let offset = 0; offset < body.length; offset += 65536) {
      if (response.destroyed) throw failure('MEDIA_CANCELLED');
      if (response.write(body.subarray(offset, offset + 65536))) continue;
      await new Promise((resolve, reject) => {
        const finish = () => { cleanup(); resolve(); };
        const fail = () => { cleanup(); reject(failure('MEDIA_CANCELLED')); };
        const cleanup = () => {
          response.removeListener('drain', finish);
          response.removeListener('error', fail);
          response.removeListener('close', fail);
        };
        response.once('drain', finish); response.once('error', fail); response.once('close', fail);
      });
    }
  }

  async function serveCached(task, owner, resource, request, response) {
    if (!owner.cache || request.headers['if-none-match'] || request.headers['if-modified-since']) return false;
    if (task.controller.signal.aborted || owner.revoked) throw failure('MEDIA_CANCELLED');
    const document = owner.cache.document(documentKey(resource));
    if (document && document.expiresAt > Date.now()) {
      sendDocument(request, response, document.headers, document.body);
      return true;
    }
    const entry = owner.cache.binary(resource.url);
    const sourceHeaders = owner.headers;
    const condition = request.headers['if-range'] ?? sourceHeaders?.['if-range'];
    if (!entry || condition !== undefined && !matchesIfRange(condition, entry.headers)) return false;
    const requested = request.headers.range ?? sourceHeaders?.range;
    if (requested !== undefined && !/^bytes=(\d*)-(\d*)$/.test(requested)) return false;
    const range = rangeFor(requested, entry.total);
    if (range === false) {
      response.writeHead(416, { ...entry.headers, 'content-range': `bytes */${entry.total}`, 'content-length': 0 });
      response.end();
      return true;
    }
    const start = range?.start ?? 0, end = range?.end ?? entry.total - 1;
    if (request.method !== 'HEAD' && !entry.ranges.some(part => part.end >= start && part.start <= end)) return false;
    const plan = [];
    let next = start;
    for (const part of entry.ranges) {
      if (part.end < next) continue;
      if (part.start > end) break;
      if (part.start > next) plan.push({ start: next, end: part.start - 1 });
      const first = Math.max(next, part.start), last = Math.min(end, part.end);
      plan.push({ start: first, end: last, body: part.body.subarray(first - part.start, last - part.start + 1) });
      next = last + 1;
      if (next > end) break;
    }
    if (next <= end) plan.push({ start: next, end });
    const values = { ...entry.headers, 'content-length': end - start + 1, 'accept-ranges': 'bytes' };
    delete values['content-range'];
    if (range) values['content-range'] = `bytes ${start}-${end}/${entry.total}`;
    if (request.method === 'HEAD') {
      response.writeHead(range ? 206 : 200, values); response.end();
      return true;
    }
    const validator = strongETag(entry.headers.etag) ? entry.headers.etag
      : httpDate(entry.headers['last-modified']) !== null ? entry.headers['last-modified'] : undefined;
    async function gap(part) {
      const result = await upstream(task, owner, resource, { method: 'GET', headers: {
        ...request.headers, range: `bytes=${part.start}-${part.end}`,
        ...(validator ? { 'if-range': validator } : {}),
      } });
      const changed = ['etag', 'last-modified'].some(name =>
        entry.headers[name] && result.incoming.headers[name] && entry.headers[name] !== result.incoming.headers[name]);
      if (result.incoming.statusCode !== 206) {
        if (!response.headersSent && result.incoming.statusCode >= 400) {
          response.writeHead(result.incoming.statusCode, copyHeaders(result.incoming.headers));
          if (result.errorBody) response.end(result.errorBody);
          else await pipeline(result.incoming, response);
          return null;
        }
        throw failure(changed ? 'MEDIA_RESOURCE_CHANGED' : 'MEDIA_RANGE_UNSUPPORTED');
      }
      const actual = responseRange(result.incoming);
      if (actual.start !== part.start || actual.end !== part.end || actual.total !== entry.total || changed ||
          (result.incoming.headers['content-encoding'] ?? 'identity') !== (entry.headers['content-encoding'] ?? 'identity')) {
        throw failure('MEDIA_RESOURCE_CHANGED');
      }
      return result.incoming;
    }
    // Verify the first continuation before committing cached bytes. Never redownload a
    // cached prefix by silently accepting a server that ignores its continuation Range.
    const firstGap = plan.find(part => !part.body);
    const firstIncoming = firstGap ? await gap(firstGap) : undefined;
    if (firstGap && !firstIncoming) return true;
    response.writeHead(range ? 206 : 200, values);
    for (const part of plan) {
      if (part.body) await writeBytes(response, part.body);
      else {
        const incoming = part === firstGap ? firstIncoming : await gap(part);
        let bytes = 0;
        for await (const chunk of incoming) { bytes += chunk.length; await writeBytes(response, chunk); }
        if (!incoming.complete || bytes !== part.end - part.start + 1) throw failure('MEDIA_UPSTREAM_ERROR');
      }
    }
    response.end();
    return true;
  }

  async function serve(task, owner, resource, request, response) {
    if (await serveCached(task, owner, resource, request, response)) return;
    const hinted = manifestFormat(resource.url, '', resource.format);
    let result = await upstream(task, owner, resource, request, Boolean(hinted));
    let format = manifestFormat(result.url, result.incoming.headers['content-type'], resource.format);
    if (request.method === 'HEAD') {
      const type = result.incoming.headers['content-type']?.split(';', 1)[0].trim().toLowerCase();
      const binary = ['binary', 'progressive', 'mp4', 'mkv', 'webm'].includes(resource.format?.toLowerCase()) ||
        /^(?:audio|video)\//.test(type ?? '') || ['application/mp4', 'application/ogg', 'application/webm'].includes(type);
      const values = copyHeaders(result.incoming.headers, {
        // Unknown MIME (including text/plain) may be sniffed and rewritten by GET.
        // HEAD must not download that body or claim its upstream length/validators.
        rewritten: Boolean(format) || !binary,
      });
      response.writeHead(result.incoming.statusCode, values);
      response.end();
      return;
    }
    // An extensionless playlist may first be identified by its response type.
    // Re-read its control document without Range before rewriting, never a partial manifest.
    if (!format && [200, 206].includes(result.incoming.statusCode)) {
      format = await sniffManifest(result.incoming, resource.format === 'binary' ||
        result.incoming.statusCode === 206 && !/^bytes 0-/.test(result.incoming.headers['content-range'] ?? ''));
    }
    if (result.incoming.statusCode === 204) throw failure('MEDIA_EMPTY_RESPONSE');
    if (format && [206, 416].includes(result.incoming.statusCode)) {
      result.incoming.destroy();
      result = await upstream(task, owner, { ...resource, url: result.url }, request, true);
      format = manifestFormat(result.url, result.incoming.headers['content-type'], resource.format);
      if (!format && result.incoming.statusCode === 200) format = await sniffManifest(result.incoming);
      if (result.incoming.statusCode === 206) throw failure('MEDIA_MANIFEST_INVALID');
    }
    if (format && result.incoming.statusCode === 200) {
      const text = await readManifest(task, result.incoming);
      const body = rewrittenManifest(task, owner, resource, text, result.url, format);
      sendDocument(request, response, controlHeaders(result.incoming.headers, format), body);
      return;
    }
    response.writeHead(result.incoming.statusCode, copyHeaders(result.incoming.headers));
    if (result.errorBody) response.end(result.errorBody);
    else await pipeline(result.incoming, response);
  }

  async function prefetchOperation(owner, job, perform) {
    if (job.controller.signal.aborted) throw job.controller.signal.reason;
    // Leave the final transport slot for playback, even when every active task is prefetch.
    if (operations.size >= maximumActiveRequests - 1) throw failure('MEDIA_BUSY');
    const task = operation(owner);
    const cancel = () => task.cancel(job.controller.signal.reason?.code);
    job.controller.signal.addEventListener('abort', cancel, { once: true });
    try { return await perform(task); }
    catch (error) {
      throw task.controller.signal.reason ??
        (typeof error?.code === 'string' && /^MEDIA_[A-Z_]+$/.test(error.code) ? error : failure('MEDIA_UPSTREAM_ERROR'));
    }
    finally {
      job.controller.signal.removeEventListener('abort', cancel);
      await task.finish();
    }
  }

  async function collectBinary(task, incoming, append, requested) {
    if (![200, 206].includes(incoming.statusCode)) throw failure('MEDIA_PREFETCH_HTTP_ERROR');
    if (incoming.headers['content-encoding'] && incoming.headers['content-encoding'] !== 'identity') {
      throw failure('MEDIA_PREFETCH_UNSUPPORTED');
    }
    let range;
    if (incoming.statusCode === 206) {
      range = responseRange(incoming);
      if (!requested || range.start !== requested.start || range.end !== Math.min(requested.end, range.total - 1)) {
        throw failure('MEDIA_RANGE_UNSUPPORTED');
      }
    } else {
      const total = Number(incoming.headers['content-length']);
      if (requested && (!Number.isSafeInteger(total) || total < 1 || requested.start !== 0 || total > requested.end + 1)) {
        // Never consume an ignored prefix Range as an unbounded whole movie.
        throw failure('MEDIA_RANGE_UNSUPPORTED');
      }
      if (Number.isSafeInteger(total) && total > 0) range = { start: 0, end: total - 1, total };
    }
    let bytes = 0;
    for await (const chunk of incoming) {
      if (task.controller.signal.aborted) throw task.controller.signal.reason;
      bytes += chunk.length; append(chunk);
    }
    if (!incoming.complete || range && bytes !== range.end - range.start + 1) throw failure('MEDIA_UPSTREAM_ERROR');
    if (!bytes) throw failure('MEDIA_EMPTY_RESPONSE');
    if (requested && !requested.prefix && requested.end >= range.total) throw failure('MEDIA_RANGE_UNSUPPORTED');
    return { kind: 'binary', ...(range ?? { start: 0, end: bytes - 1, total: bytes }),
      headers: copyHeaders(incoming.headers) };
  }

  async function prefetchDocument(owner, job, resource) {
    const key = documentKey(resource), hinted = manifestFormat(resource.url, '', resource.format);
    return owner.cache.load(`document:${key}`, resource.url, append => prefetchOperation(owner, job, async task => {
      const request = { method: 'GET', headers: hinted ? {} : { range: `bytes=0-${progressivePrefetchBytes - 1}` } };
      let result = await upstream(task, owner, resource, request, Boolean(hinted));
      if (![200, 206].includes(result.incoming.statusCode)) throw failure('MEDIA_PREFETCH_HTTP_ERROR');
      let format = manifestFormat(result.url, result.incoming.headers['content-type'], resource.format);
      if (!format) {
        try { format = await sniffManifest(result.incoming); }
        catch (error) {
          if (error.code === 'MEDIA_MANIFEST_UNSUPPORTED') throw failure('MEDIA_PREFETCH_UNSUPPORTED');
          throw error;
        }
      }
      if (format && result.incoming.statusCode === 206) {
        result.incoming.destroy();
        result = await upstream(task, owner, { ...resource, url: result.url, format }, request, true);
        if (result.incoming.statusCode !== 200) throw failure('MEDIA_PREFETCH_HTTP_ERROR');
      }
      if (!format) {
        const type = result.incoming.headers['content-type']?.split(';', 1)[0].toLowerCase();
        if (type && !type.startsWith('video/') && !type.startsWith('audio/') && type !== 'application/octet-stream') {
          throw failure('MEDIA_PREFETCH_UNSUPPORTED');
        }
        return collectBinary(task, result.incoming, append, { start: 0, end: progressivePrefetchBytes - 1, prefix: true });
      }
      const text = await readManifest(task, result.incoming);
      const body = rewrittenManifest(task, owner, resource, text, result.url, format);
      append(body);
      const plan = format === 'hls' ? hlsPrefetchPlan(body.toString()) : dashPrefetchPlan(body.toString());
      // VOD documents stay with the lease. Live manifests need a fresh graph rather than
      // an indefinitely frozen playlist; media bytes already read remain reusable.
      const immutable = format === 'hls' ? /#EXT-X-ENDLIST(?:\r?\n|$)|#EXT-X-STREAM-INF:/.test(text)
        : !/\btype\s*=\s*["']dynamic["']/.test(text);
      return { kind: 'document', documentKey: key, plan, format, headers: controlHeaders(result.incoming.headers, format),
        expiresAt: immutable ? Infinity : 0 };
    }));
  }

  async function prefetchBinary(owner, job, resource, requested) {
    if (requested) {
      const entry = owner.cache.binary(resource.url);
      if (entry && !requested.prefix && requested.end >= entry.total) throw failure('MEDIA_RANGE_UNSUPPORTED');
      let end = entry && requested.prefix ? Math.min(requested.end, entry.total - 1) : requested.end;
      if (requested.start > end) throw failure('MEDIA_RANGE_UNSUPPORTED');
      for (const range of cacheGaps(entry, requested.start, end)) {
        await owner.cache.load(`binary:${resource.url}:${range.start}-${range.end}`, resource.url,
          append => prefetchOperation(owner, job, async task => {
            const result = await upstream(task, owner, resource, { method: 'GET',
              headers: { range: `bytes=${range.start}-${range.end}` } });
            return collectBinary(task, result.incoming, append, { ...range, prefix: requested.prefix });
          }));
      }
      const buffered = owner.cache.binary(resource.url);
      end = Math.min(end, buffered.total - 1);
      const covered = cacheCoverage(buffered, requested.start, end);
      if (covered.end !== end) throw failure('MEDIA_RANGE_UNSUPPORTED');
      return buffered;
    }
    const entry = owner.cache.binary(resource.url);
    if (entry) {
      await prefetchBinary(owner, job, resource, { start: 0, end: entry.total - 1 });
      return;
    }
    await owner.cache.load(`binary:${resource.url}:full`, resource.url, append => prefetchOperation(owner, job, async task => {
      const result = await upstream(task, owner, resource, { method: 'GET', headers: {} }, true);
      return collectBinary(task, result.incoming, append);
    }));
  }

  function plannedResource(owner, value) {
    const local = address(value);
    const match = /^\/media\/([A-Za-z0-9_-]{32})\/([A-Za-z0-9_-]{32})(?:\/(.*))?$/.exec(local.pathname);
    const resource = !local.search && match?.[1] === owner.id
      ? resolveResource(owner, match[2], match[3]) : null;
    if (!resource) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
    return resource;
  }

  async function prefetchGraph(owner, job) {
    const documents = new Set();
    let hasMedia = false;
    async function readDocument(resource) {
      if (documents.has(documentKey(resource))) return;
      documents.add(documentKey(resource));
      const entry = await prefetchDocument(owner, job, resource);
      if (entry.kind === 'binary') { hasMedia = true; return; }
      for (const item of entry.plan.binaries) {
        await prefetchBinary(owner, job, plannedResource(owner, item.url),
          item.progressive ? { start: 0, end: progressivePrefetchBytes - 1, prefix: true } : item.range);
        if (item.media) hasMedia = true;
      }
      for (const item of entry.plan.indexes ?? []) {
        const index = await prefetchBinary(owner, job, plannedResource(owner, item.url), item.range);
        const covered = cacheCoverage(index, item.range.start, item.range.end);
        if (covered.end !== item.range.end) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
        const range = firstDASHRange(Buffer.concat(covered.ranges.map(part => part.body)), item.range.start);
        await prefetchBinary(owner, job, plannedResource(owner, item.mediaUrl), range);
        hasMedia = true;
      }
      for (const url of entry.plan.documents ?? []) await readDocument(plannedResource(owner, url));
    }
    await readDocument(owner.root);
    if (owner.revoked || job.controller.signal.aborted) throw job.controller.signal.reason ?? failure('MEDIA_CANCELLED');
    if (!hasMedia || !owner.cache.bytes) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
    return { bytes: owner.cache.bytes };
  }

  async function handle(request, response) {
    response.setHeader('Access-Control-Allow-Origin', '*');
    response.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
    response.setHeader('Access-Control-Allow-Headers', request.headers['access-control-request-headers'] ?? '*');
    response.setHeader('Access-Control-Expose-Headers', responseHeaders.join(', '));
    if (request.headers['access-control-request-private-network'] === 'true') {
      response.setHeader('Access-Control-Allow-Private-Network', 'true');
    }
    if (closed) { sendError(response, 410, 'MEDIA_CLOSED'); return; }
    if (request.method === 'OPTIONS') { response.writeHead(204); response.end(); return; }
    if (!['GET', 'HEAD'].includes(request.method)) {
      response.setHeader('Allow', 'GET, HEAD, OPTIONS');
      sendError(response, 405, 'MEDIA_METHOD_NOT_ALLOWED');
      return;
    }
    if (request.headers['transfer-encoding'] || Number(request.headers['content-length'] ?? 0) !== 0) {
      response.setHeader('Connection', 'close');
      sendError(response, 400, 'MEDIA_INVALID_REQUEST');
      return;
    }
    const separator = request.url.indexOf('?');
    const path = separator < 0 ? request.url : request.url.slice(0, separator);
    const query = separator < 0 ? undefined : request.url.slice(separator + 1);
    const match = /^\/media\/([A-Za-z0-9_-]{32})\/([A-Za-z0-9_-]{32})(?:\/(.*))?$/.exec(path);
    const owner = match ? owners.get(match[1]) : null;
    let resource = owner && !owner.revoked ? resolveResource(owner, match[2], match[3]) : null;
    if (resource && query !== undefined) {
      const parameters = new URLSearchParams(query), target = address(resource.url);
      const names = new Set(parameters.keys());
      const original = target.search.slice(1).split('&').filter(part =>
        part && !names.has(new URLSearchParams(part).keys().next().value));
      target.search = [...original, query].filter(Boolean).join('&');
      resource = { ...resource, url: target.href };
    }
    if (!resource) { sendError(response, 404, 'MEDIA_NOT_FOUND'); return; }
    if (operations.size >= maximumActiveRequests) { sendError(response, 503, 'MEDIA_BUSY'); return; }
    const task = operation(owner, response);
    try { await serve(task, owner, resource, request, response); }
    catch (error) {
      const code = typeof error?.code === 'string' && /^MEDIA_[A-Z_]+$/.test(error.code) ? error.code : 'MEDIA_UPSTREAM_ERROR';
      sendError(response, code === 'MEDIA_UPSTREAM_TIMEOUT' ? 504 : 502, code);
    } finally { await task.finish(); }
  }

  async function revoke(owner) {
    if (!owner.revoking) {
      owner.revoked = true;
      owner.cache?.revoke();
      owner.prefetchJob?.controller.abort(failure('MEDIA_CANCELLED'));
      for (const record of [...owner.tokens.values()]) forget(owner, record);
      owner.revoking = (async () => {
        for (const task of owner.operations) task.cancel();
        await Promise.all([...owner.operations].map(task => task.done));
        if (owner.prefetchJob) await owner.prefetchJob.promise.catch(() => {});
        await owner.cache?.close();
        owners.delete(owner.id);
        registryBytes -= owner.bytes;
      })();
    }
    await owner.revoking;
  }

  return {
    async prepare(descriptor, { scope, cache = false } = {}) {
      if (closed) throw failure('MEDIA_CLOSED');
      if (typeof cache !== 'boolean') throw failure('MEDIA_INVALID_REQUEST');
      const input = descriptorInput(descriptor, scope);
      // Only source-declared headers select this route. A player's own Range
      // request on a headerless URL still goes directly to that URL.
      const useStream = cache || Object.keys(input.headers).length > 0;
      if (useStream) await start();
      if (closed) throw failure('MEDIA_CLOSED');
      const id = randomBytes(24).toString('base64url');
      const bytes = 1024 + Buffer.byteLength(JSON.stringify({
        url: input.url, headers: input.headers, scope: input.scope, drm: input.drm,
      })) * 2;
      reserve(bytes);
      const owner = { ...input, id, bytes, keys: new Map(), tokens: new Map(), operations: new Set(), revoked: false,
        root: { url: input.url, format: input.format }, cache: cache ? cachePool.create() : undefined };
      owners.set(id, owner);
      try {
        const url = useStream ? register(owner, { url: input.url, format: input.format }) : input.url;
        return { id, url, ...(useStream ? { mediaOrigin: `${baseUrl}/` } : {}),
          ...(input.format === undefined ? {} : { format: input.format }),
          ...(input.drm === undefined ? {} : { drm: structuredClone(input.drm) }) };
      } catch (error) { await revoke(owner); throw error; }
    },
    async prefetch(id) {
      if (closed) throw failure('MEDIA_CLOSED');
      const owner = owners.get(id);
      if (!owner || owner.revoked) throw failure('MEDIA_NOT_FOUND');
      if (!owner.cache) throw failure('MEDIA_PREFETCH_DISABLED');
      if (!owner.prefetchJob) {
        const job = { controller: new AbortController() };
        const timer = setTimeout(() => job.controller.abort(failure('MEDIA_PREFETCH_TIMEOUT')), 30000);
        timer.unref();
        job.promise = prefetchGraph(owner, job).finally(() => clearTimeout(timer));
        owner.prefetchJob = job;
        job.promise.catch(() => { if (owner.prefetchJob === job) owner.prefetchJob = undefined; });
      }
      return owner.prefetchJob.promise;
    },
    async cancelPrefetch(id) {
      const owner = owners.get(id), job = owner?.prefetchJob;
      if (!job) return;
      job.controller.abort(failure('MEDIA_CANCELLED'));
      await job.promise.catch(() => {});
      if (owner.prefetchJob === job) owner.prefetchJob = undefined;
    },
    async release(id) {
      const owner = owners.get(id);
      if (owner) await revoke(owner);
    },
    async invalidate(predicate) {
      if (typeof predicate !== 'function') throw failure('MEDIA_INVALID_REQUEST');
      const matches = [...owners.values()].filter(owner => predicate(owner.scope));
      await Promise.all(matches.map(revoke));
    },
    close() {
      if (!closing) {
        closed = true;
        closing = (async () => {
          if (starting) await starting.catch(() => {});
          await Promise.all([...owners.values()].map(revoke));
          if (server?.listening) {
            const done = new Promise((resolve, reject) => server.close(error =>
              error ? reject(failure('MEDIA_CLOSE_FAILED')) : resolve()));
            for (const socket of inboundSockets.keys()) socket.destroy();
            await done;
          }
          while (inboundSockets.size) await Promise.all([...inboundSockets.values()]);
        })();
      }
      return closing;
    },
  };
}
