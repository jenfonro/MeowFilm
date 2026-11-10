import { afterCloseDispatch, closeResource } from './resource-close.js';
import { randomBytes } from 'node:crypto';
import { Readable } from 'node:stream';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';
import { fault, requireValue } from './errors.js';
import { compileDASHTemplate, createHLSVariables, decodeManifest, hlsAttributes, hlsAttributeLine, manifestFormat, rewriteDASH,
  serializeXMLChunks } from './media-manifest.js';

// Control documents are bounded; the binary media body is not buffered here.
const playlistBytes = 8 * 1024 * 1024;
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };

async function proxyChunk(stream, maximum) {
  for (;;) {
    const chunk = stream.read(Math.min(maximum, stream.readableLength) || maximum);
    if (chunk) return chunk;
    if (stream.readableEnded || stream.complete && !stream.readableLength) return null;
    requireValue(!stream.destroyed, 'NETWORK_ERROR');
    await new Promise((resolve, reject) => {
      const cleanup = () => {
        stream.off('readable', ready); stream.off('end', ready);
        stream.off('close', failed); stream.off('error', failed);
      };
      const ready = () => { cleanup(); resolve(); };
      const failed = () => { cleanup(); reject(fault('NETWORK_ERROR')); };
      stream.once('readable', ready); stream.once('end', ready);
      stream.once('close', failed); stream.once('error', failed);
    });
  }
}

function playlistPrefix(chunks, bytes) {
  const text = Buffer.concat(chunks, bytes).subarray(0, 512).toString('utf8').replace(/^\uFEFF/, '').trimStart();
  const format = text.startsWith('#EXTM3U') ? 'hls'
    : /^<(?:\?xml\b|!--|(?:[A-Za-z_][\w.-]*:)?MPD\b)/.test(text) ? 'dash' : null;
  const complete = format || bytes >= 512 ||
    text && !'#EXTM3U'.startsWith(text) && !text.startsWith('<') && !text.startsWith('\uFFFD');
  return { format, complete };
}

function replayProxy(chunks, stream, resources) {
  const replay = Readable.from((async function* () {
    yield* chunks;
    yield* stream;
  })(), { objectMode: false });
  resources.track(replay);
  return replay;
}

// Inspect only a prefix before choosing the control-document path. For an
// encoded binary response, replay the original wire prefix, not decoded bytes.
export async function proxyPlaylistInput(upstream, target, resources) {
  let format = manifestFormat(target.href, String(upstream.headers['content-type'] ?? ''));
  const encoding = String(upstream.headers['content-encoding'] ?? 'identity').trim().toLowerCase();
  const factory = Object.hasOwn(decompressors, encoding) ? decompressors[encoding] : null;
  if (encoding && encoding !== 'identity' && !factory) return { stream: upstream, format: null };
  if (!factory) {
    if (format) return { stream: upstream, format };
    const chunks = [];
    let bytes = 0, prefix;
    do {
      const chunk = await proxyChunk(upstream, 512 - bytes);
      if (!chunk) break;
      chunks.push(chunk); bytes += chunk.length;
      prefix = playlistPrefix(chunks, bytes);
    } while (!prefix.complete);
    return { stream: replayProxy(chunks, upstream, resources), format: prefix?.format ?? null };
  }
  const decoder = factory();
  resources.track(decoder);
  upstream.once('error', error => decoder.destroy(error));
  if (format) {
    upstream.pipe(decoder);
    return { stream: decoder, format };
  }
  const wire = [], decoded = [];
  let wireBytes = 0, decodedBytes = 0, settled = false, resolve, reject;
  const decision = new Promise((yes, no) => { resolve = yes; reject = no; });
  decision.catch(() => {});
  const data = chunk => {
    decoded.push(chunk); decodedBytes += chunk.length;
    const prefix = playlistPrefix(decoded, decodedBytes);
    if (prefix.complete) {
      format = prefix.format; settled = true;
      decoder.pause();
      resolve();
    }
  };
  const ended = () => { settled = true; resolve(); };
  const failed = error => reject(error);
  decoder.on('data', data);
  decoder.once('end', ended); decoder.once('error', failed);
  try {
    while (!settled) {
      const chunk = await proxyChunk(upstream, 512);
      if (!chunk) { decoder.end(); await decision; break; }
      wire.push(chunk); wireBytes += chunk.length;
      // The same control-input budget bounds retained encoded probe metadata.
      requireValue(wireBytes <= playlistBytes, 'RESOURCE_LIMIT');
      await Promise.race([new Promise((yes, no) => decoder.write(chunk, error => error ? no(error) : yes())), decision]);
    }
  } finally {
    decoder.off('data', data); decoder.off('end', ended); decoder.off('error', failed);
  }
  if (!format) {
    decoder.destroy();
    return { stream: replayProxy(wire, upstream, resources), format: null };
  }
  if (!decoder.writableEnded) upstream.pipe(decoder);
  return { stream: replayProxy(decoded, decoder, resources), format };
}

export async function readProxyPlaylist(upstream, headers = upstream.headers) {
  const declared = headers['content-length'];
  requireValue(declared === undefined || Number(declared) <= playlistBytes, 'RESOURCE_LIMIT');
  const chunks = [];
  let bytes = 0;
  for await (const chunk of upstream) {
    bytes += chunk.byteLength;
    requireValue(bytes <= playlistBytes, 'RESOURCE_LIMIT');
    chunks.push(chunk);
  }
  try { return decodeManifest(Buffer.concat(chunks, bytes)); }
  catch (error) { throw manifestError(error); }
}

export function* proxyPlaylistLines(text, rewrite) {
  let start = 0, end;
  while ((end = text.indexOf('\n', start)) !== -1) {
    yield rewrite(text.slice(start, end)) + '\n';
    start = end + 1;
  }
  if (start < text.length) yield rewrite(text.slice(start));
}

function manifestError(error) {
  if (error?.code === 'MEDIA_MANIFEST_LIMIT') return fault('RESOURCE_LIMIT');
  if (['MEDIA_MANIFEST_UNSUPPORTED', 'MEDIA_DRM_UNSUPPORTED'].includes(error?.code)) return fault('UNSUPPORTED_PROTOCOL');
  if (error?.code === 'MEDIA_MANIFEST_INVALID') return fault('INVALID_RESPONSE');
  return error;
}

function registerManifest(entry, resources, proxyRoot) {
  const mappings = entry.manifestMappings ??= {
    prefix: `/proxy/${randomBytes(24).toString('base64url')}/`, keys: new Map(), contexts: new Map(),
  };
  const key = JSON.stringify(resources);
  let id = mappings.keys.get(key);
  if (!id) {
    id = randomBytes(24).toString('base64url');
    const context = {
      base: resources.find(resource => resource.path === '').url,
      exact: new Map(resources.filter(resource => !resource.template).map(resource => [resource.path, resource])),
      templates: resources.filter(resource => resource.template)
        .map(resource => compileDASHTemplate(resource.url, resource.path)),
    };
    mappings.keys.set(key, id);
    mappings.contexts.set(id, context);
  }
  return `${proxyRoot.replace(/\/$/, '')}${mappings.prefix}${id}/`;
}

// Tokens and DASH patterns translate the player's synthetic references back to
// original URLs. They are not an allowlist: other suffixes use the real base.
export function resolveProxyManifest(entry, path, search = '') {
  const mappings = entry.manifestMappings;
  if (!mappings || !path.startsWith(mappings.prefix)) return undefined;
  const rest = path.slice(mappings.prefix.length), slash = rest.indexOf('/');
  const context = slash < 0 ? undefined : mappings.contexts.get(rest.slice(0, slash));
  requireValue(context, 'STALE_SESSION');
  const suffix = rest.slice(slash + 1), exact = context.exact.get(suffix);
  let target;
  try {
    let resolved = exact?.url;
    if (!resolved) for (const resolve of context.templates) {
      resolved = resolve(suffix);
      if (resolved) break;
    }
    target = new URL(resolved ?? new URL(suffix, context.base).href);
  } catch { throw fault('INVALID_ARGUMENT'); }
  requireValue(['http:', 'https:'].includes(target.protocol), 'UNSUPPORTED_PROTOCOL');
  if (search) {
    const query = search.slice(1), names = new Set(new URLSearchParams(query).keys());
    const original = target.search.slice(1).split('&').filter(part =>
      part && !names.has(new URLSearchParams(part).keys().next().value));
    target.search = [...original, query].filter(Boolean).join('&');
  }
  return { url: target.href, hlsVariables: exact?.hlsVariables };
}

export function proxyManifestLocation(entry, url, map, hlsVariables) {
  if (!hlsVariables) return map(url);
  return registerManifest(entry, [{ path: '', url, hlsVariables }],
    map(new URL('/', entry.origin).href)) + new URL(url).search;
}

export function proxyPlaylistBody(text, target, format, entry, map, inheritedVariables) {
  const proxyRoot = map(new URL('/', entry.origin).href);
  if (format === 'dash') {
    try {
      return rewriteDASH(text, target.href, specification => specification.resources
        ? registerManifest(entry, specification.resources, proxyRoot)
        : map(specification.url), serializeXMLChunks);
    } catch (error) {
      throw manifestError(error);
    }
  }
  requireValue(text.replace(/^\uFEFF/, '').startsWith('#EXTM3U'), 'INVALID_RESPONSE');
  const { values: variables, substitute, define } = createHLSVariables(target.href, inheritedVariables);
  let variant = false;
  function reference(value, child) {
    value = substitute(value);
    if (value.startsWith('data:')) return value;
    let url;
    try {
      const resolved = new URL(value, target);
      requireValue(['http:', 'https:'].includes(resolved.protocol), 'UNSUPPORTED_PROTOCOL');
      url = resolved.href;
    }
    catch (error) { throw manifestError(error); }
    if (!child || !variables.size) return map(url);
    return proxyManifestLocation(entry, url, map, Object.fromEntries(variables));
  }
  const lines = proxyPlaylistLines(text, line => {
    const trimmed = line.trim();
    try {
      if (trimmed && !trimmed.startsWith('#')) {
        const child = variant; variant = false;
        return reference(trimmed, child);
      }
      const tag = trimmed.split(':', 1)[0];
      if (tag === '#EXT-X-DEFINE') {
        define(trimmed);
        return line;
      }
      if (tag === '#EXT-X-STREAM-INF') variant = true;
      if (!hlsAttributeLine(trimmed) || !/[:,]\s*(?:URI|SERVER-URI)\s*=/.test(trimmed)) return line;
      const child = ['#EXT-X-MEDIA', '#EXT-X-I-FRAME-STREAM-INF', '#EXT-X-RENDITION-REPORT', '#EXT-X-IMAGE-STREAM-INF'].includes(tag);
      const rewritten = hlsAttributes(trimmed, value => reference(value, child));
      const start = line.indexOf(trimmed);
      return line.slice(0, start) + rewritten + line.slice(start + trimmed.length);
    } catch (error) { throw manifestError(error); }
  });
  return lines;
}

/** Each request owns its socket (agent:false). Observe close from acquisition,
 * but only start the finite cleanup clock when the transfer is retired. */
export class CatProxyResources {
  constructor(timeoutMs) {
    this.timeoutMs = timeoutMs;
    this.resources = new Map();
    this.closing = false;
  }
  track(target) {
    if (this.resources.has(target)) return;
    const state = { target, closed: false, closing: null };
    const ignoreError = () => {};
    const detach = afterCloseDispatch(target, () => { state.closed = true; });
    target.on('error', ignoreError);
    state.release = () => {
      detach();
      target.off('error', ignoreError);
    };
    this.resources.set(target, state);
    if (this.closing) this.retire(state);
  }
  retire(state) {
    return state.closing ??= closeResource(state.target, {
      stop: () => state.target.destroy(),
      closed: () => state.closed,
      release: state.release,
      timeoutMs: this.timeoutMs,
    });
  }
  async close() {
    this.closing = true;
    let states, results;
    do {
      states = [...this.resources.values()];
      results = await Promise.all(states.map(state => this.retire(state)));
    } while (states.length !== this.resources.size);
    this.resources.clear();
    const failure = results.find(result => result.error);
    if (failure) throw failure.error;
  }
}
