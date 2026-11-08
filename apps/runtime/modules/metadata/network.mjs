import http from 'node:http';
import https from 'node:https';
import { createNodeNetwork } from '@meowfilm/script-host';
import { metadataFailure } from './settings.mjs';

function address(value) {
  let url;
  try { url = new URL(value); } catch { throw metadataFailure('INVALID_REQUEST'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.hash) {
    throw metadataFailure('INVALID_REQUEST');
  }
  return url.href;
}

/** Reuse the existing buffered network adapter for metadata only. Redirects
 * require the source adapter's explicit policy; credentials do not travel
 * through an automatically followed Location. */
export function createMetadataRequest() {
  const network = createNodeNetwork();
  return async (url, { signal, ...options } = {}) => {
    try {
      const result = await network({ url, signal, validateURL: address,
        options: { followRedirect: false, ...options } });
      return { status: result.statusCode, headers: result.headers, body: result.body, url: result.url };
    } catch (error) {
      throw metadataFailure(signal?.aborted ? 'CANCELLED'
        : error?.code === 'INVALID_ARGUMENT' ? 'INVALID_REQUEST' : 'NETWORK_ERROR');
    }
  };
}

async function imageExchange(url, headers, signal) {
  const resources = [], drained = [];
  let disposing;
  function own(resource) {
    resources.push(resource);
    drained.push(new Promise(done => resource.once('close', done)));
    return resource;
  }
  const dispose = () => disposing ??= (async () => {
    for (const resource of resources) resource.destroy();
    await Promise.all(drained);
  })();
  try {
    const response = await new Promise((resolve, reject) => {
      const target = new URL(address(url));
      const transport = target.protocol === 'https:' ? https : http;
      const request = own(transport.request(target, { method: 'GET', agent: false, signal,
        headers: { ...headers, 'accept-encoding': 'identity' } }, reply => {
        own(reply);
        // The stream can fail before the bridge has installed its pipeline.
        reply.on('error', () => {});
        resolve(reply);
      }));
      request.once('socket', own);
      request.on('error', () => reject(metadataFailure(signal.aborted ? 'CANCELLED' : 'NETWORK_ERROR')));
      request.end();
    });
    return { response, dispose };
  } catch (error) {
    await dispose();
    throw error;
  }
}

// Check only the preamble, not the entire image. Some relays label JSON/HTML
// errors as image/*; these must fail before the bridge commits image headers.
function inspectImageStart(response, signal) {
  return new Promise((resolve, reject) => {
    // A bounded sniff window, not an image-size limit. Unclosed XML comments
    // or whitespace cannot make a relay bypass downstream backpressure.
    const prefix = Buffer.alloc(4096);
    let length = 0, settled = false;
    function finish(error) {
      if (settled) return;
      settled = true;
      response.off('readable', inspect);
      response.off('end', empty);
      response.off('error', failed);
      signal.removeEventListener('abort', cancelled);
      if (error) reject(error);
      else {
        response.unshift(prefix.subarray(0, length));
        resolve();
      }
    }
    const empty = () => finish(metadataFailure('UPSTREAM_FORMAT'));
    const failed = () => finish(metadataFailure('NETWORK_ERROR'));
    const cancelled = () => finish(metadataFailure('CANCELLED'));
    function inspect() {
      while (!settled && length < prefix.length && response.readableLength > 0) {
        const chunk = response.read(Math.min(prefix.length - length, response.readableLength));
        chunk.copy(prefix, length); length += chunk.length;
        if (length < 3 && prefix[0] === 0xef &&
            (length === 1 || prefix[1] === 0xbb)) continue;
        let start = prefix.subarray(0, length).toString('utf8').trimStart();
        if (!start) continue;
        if (/^(?:\{|\[|")/.test(start)) { finish(metadataFailure('UPSTREAM_FORMAT')); return; }
        if (!start.startsWith('<')) { finish(); return; }
        // SVG may have an XML declaration and comments. No page code or XML
        // entities are evaluated, and HTML cannot masquerade as an SVG root.
        let incomplete = false;
        while (/^(?:<\?xml(?:\s|\?)|<!--|<!doctype\s+svg(?:\s|\[|>))/i.test(start)) {
          let end;
          if (/^<!doctype/i.test(start)) {
            let quote, brackets = 0;
            for (let index = 0; index < start.length; index++) {
              const char = start[index];
              if (quote) { if (char === quote) quote = undefined; }
              else if (char === '"' || char === "'") quote = char;
              else if (char === '[') brackets++;
              else if (char === ']') brackets--;
              else if (char === '>' && brackets === 0) { end = index + 1; break; }
            }
          } else {
            const marker = start.startsWith('<!--') ? '-->' : '?>';
            const index = start.indexOf(marker);
            if (index >= 0) end = index + marker.length;
          }
          if (end === undefined) { incomplete = true; break; }
          start = start.slice(end).trimStart();
        }
        if (incomplete) continue;
        if (/^<svg(?:\s|\/?>)/i.test(start)) { finish(); return; }
        if (!start || ['<svg', '<?xml', '<!--', '<!doctype'].some(prefix => prefix.startsWith(start.toLowerCase())) ||
            /^<!doctype\s+(?:s|sv|svg)?$/i.test(start)) continue;
        finish(metadataFailure('UPSTREAM_FORMAT'));
      }
      if (!settled && length === prefix.length) finish(metadataFailure('UPSTREAM_FORMAT'));
      else if (!settled) response.read(0);
    }
    response.on('readable', inspect);
    response.once('end', empty);
    response.once('error', failed);
    signal.addEventListener('abort', cancelled, { once: true });
    if (signal.aborted) cancelled();
    else inspect();
  });
}

/** Returns an owned stream, never a buffered image or an upstream redirect.
 * dispose waits for the real request, response and socket to close. */
export async function openMetadataImage({ url, headers }, signal) {
  for (let hop = 0; ; hop++) {
    if (signal.aborted) throw metadataFailure('CANCELLED');
    const exchange = await imageExchange(url, headers, signal);
    const { response, dispose } = exchange;
    try {
      if ([301, 302, 303, 307, 308].includes(response.statusCode)) {
        if (hop === 5 || !response.headers.location) throw metadataFailure('UPSTREAM_HTTP');
        try { url = address(new URL(response.headers.location, url).href); }
        catch { throw metadataFailure('UPSTREAM_FORMAT'); }
        await dispose();
        continue;
      }
      if (response.statusCode !== 200) throw metadataFailure('UPSTREAM_HTTP');
      if (!/^image\/[a-z0-9.+-]+(?:\s*;[^\r\n]*)?$/i.test(response.headers['content-type'] ?? '') ||
          !['identity', undefined].includes(response.headers['content-encoding'])) {
        throw metadataFailure('UPSTREAM_FORMAT');
      }
      await inspectImageStart(response, signal);
      const output = { 'content-type': response.headers['content-type'] };
      for (const name of ['content-length', 'etag', 'last-modified']) {
        if (response.headers[name] !== undefined) output[name] = response.headers[name];
      }
      // HTTP caching must not tie a port/session or upstream validator to the
      // stable resource identity. Native image caches own their own lifetime.
      output['cache-control'] = 'no-store';
      return { status: 200, headers: output, body: response, dispose };
    } catch (error) {
      await dispose();
      throw error;
    }
  }
}
