import { randomBytes, randomUUID } from 'node:crypto';
import http from 'node:http';
import { createHttpServer, createHttpClient, HTTP_PROTOCOL } from '../src/index.js';

export const credentials = () => ({ sessionId: randomUUID(), key: randomBytes(32).toString('hex') });
export const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  promise.catch(() => {});
  return { promise, resolve, reject };
};
export const turn = () => new Promise(resolve => setImmediate(resolve));
export async function until(predicate) {
  for (let i = 0; i < 500; i++) {
    if (predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  throw new Error('condition did not complete');
}
export async function pair(t, handler, extra = {}) {
  const identity = credentials();
  const { direction, ...options } = extra;
  identity.path = direction === 'reverse' ? '/v1/capabilities/test.echo' : '/v1/echo';
  const server = createHttpServer({ ...identity, resolve: ({ method, url }) => {
    if (method !== 'POST' || url.pathname !== identity.path || url.search) return;
    return { contentType: 'application/json', contract: HTTP_PROTOCOL,
      codes: new Set(['INVALID_REQUEST', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']),
      handler: async (input, context) => ({ ok: true, contract: HTTP_PROTOCOL, value: await handler(input, context) }) };
  }, ...options });
  await server.start();
  const client = createHttpClient({ ...identity, baseUrl: server.baseUrl });
  t.after(async () => { await Promise.all([client.close(), server.close()]); });
  return { server, client, identity, call: (body, options) => client.request({ path: identity.path, body, ...options }) };
}
export function rawRequest(baseUrl, identity, { method = 'POST', path = identity.path ?? '/v1/echo', headers = {}, body, end = true, pauseResponse = false } = {}) {
  const received = deferred();
  const requestId = randomUUID();
  const request = http.request(new URL(path, baseUrl), { method, headers: {
    authorization: `Bearer ${identity.key}`, 'x-meow-session': identity.sessionId,
    'x-meow-request-id': requestId, 'content-type': 'application/json', ...headers,
  } }, response => {
    const chunks = [];
    response.on('data', bytes => chunks.push(bytes));
    if (pauseResponse) response.pause();
    response.on('end', () => received.resolve({ status: response.statusCode, headers: response.headers,
      body: Buffer.concat(chunks), value: JSON.parse(Buffer.concat(chunks).toString()) }));
    response.on('error', received.reject);
    response.on('aborted', () => received.reject(new Error('CLOSED')));
  });
  request.on('error', received.reject);
  if (end) request.end(body); else if (body) request.write(body);
  return { request, requestId, response: received.promise };
}
