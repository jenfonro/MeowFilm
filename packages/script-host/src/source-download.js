import http from 'node:http';
import https from 'node:https';
import { Transform, Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';
import { fault, requireValue, safeError } from './errors.js';

const redirects = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };
const closed = stream => new Promise(resolve => stream.once('close', resolve));
const abortReason = signal => safeError(signal.reason, 'CANCELLED');

/** Complete source downloads, not the guest HTTP adapter or a media proxy.
 * The source contract bounds both transferred bytes and decompressed source. */
export function createSourceDownload({ maxSourceBytes, maxRedirects = 5 } = {}) {
  requireValue(Number.isSafeInteger(maxSourceBytes) && maxSourceBytes > 0 &&
    Number.isSafeInteger(maxRedirects) && maxRedirects >= 0 && maxRedirects <= 10);
  return async ({ url: input, options = {}, signal, validateURL }) => {
    requireValue(signal instanceof AbortSignal && typeof validateURL === 'function' &&
      options && typeof options === 'object' && !Array.isArray(options) &&
      Object.keys(options).every(key => key === 'followRedirect') &&
      (options.followRedirect === undefined || typeof options.followRedirect === 'boolean'));
    const follow = options.followRedirect === false ? 0 : maxRedirects;
    let url = new URL(validateURL(input));
    for (let hop = 0; ; hop++) {
      if (signal.aborted) throw abortReason(signal);
      requireValue(['http:', 'https:'].includes(url.protocol), 'PERMISSION_DENIED');
      const resources = [], closing = [];
      const own = resource => {
        resources.push(resource);
        closing.push(closed(resource));
        return resource;
      };
      let response, request;
      try {
        response = await new Promise((resolve, reject) => {
          request = own((url.protocol === 'https:' ? https : http).request(url, {
            method: 'GET', agent: false, signal,
          }, incoming => {
            own(incoming);
            incoming.on('error', () => {}); // pipeline owns errors once headers are accepted.
            resolve(incoming);
          }));
          request.once('socket', socket => own(socket));
          request.on('error', reject);
          request.end();
        });
        if (signal.aborted) throw abortReason(signal);
        if (follow && redirects.has(response.statusCode) && response.headers.location) {
          requireValue(hop < follow, 'RESOURCE_LIMIT');
          url = new URL(validateURL(new URL(response.headers.location, url).href));
          continue; // Finally drains this hop before opening the next connection.
        }
        const chunks = [];
        let transferred = 0, decoded = 0;
        const wire = own(new Transform({
          transform(chunk, _encoding, callback) {
            transferred += chunk.length;
            callback(transferred > maxSourceBytes ? fault('RESOURCE_LIMIT') : null, chunk);
          },
        }));
        const sink = own(new Writable({
          write(chunk, _encoding, callback) {
            decoded += chunk.length;
            if (decoded > maxSourceBytes) { callback(fault('RESOURCE_LIMIT')); return; }
            chunks.push(chunk);
            callback();
          },
        }));
        const encoding = String(response.headers['content-encoding'] ?? 'identity').toLowerCase();
        const factory = Object.hasOwn(decompressors, encoding) ? decompressors[encoding] : undefined;
        requireValue(encoding === 'identity' || factory, 'UNSUPPORTED_OPTION');
        const streams = [response, wire];
        if (factory) streams.push(own(factory()));
        streams.push(sink);
        await pipeline(streams, { signal });
        if (signal.aborted) throw abortReason(signal);
        const headers = { ...response.headers };
        if (factory) {
          delete headers['content-encoding'];
          delete headers['content-length'];
        }
        return { statusCode: response.statusCode, statusMessage: response.statusMessage,
          headers, body: Buffer.concat(chunks, decoded), url: url.href };
      } catch (error) {
        throw signal.aborted ? abortReason(signal) : safeError(error, 'NETWORK_ERROR');
      } finally {
        for (const resource of resources) resource.destroy();
        await Promise.all(closing);
      }
    }
  };
}
