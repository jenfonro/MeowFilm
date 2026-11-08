import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { SCRIPT_LIMITS } from '../src/index.js';
import { CatSession } from '../src/cat-session.js';
import * as values from '../src/node-web-values.js';
import { serverFor } from './http-fixture.js';

async function sessionFor(t, source) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'meowfilm-node-web-values-'));
  const session = new CatSession({ dataRoot, installationId: 'web-values', generation: 1,
    revision: { source, version: '1', config: {} },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 1000 } });
  t.after(async () => {
    try { await session.stop(); }
    finally { rmSync(dataRoot, { recursive: true, force: true }); }
  });
  await session.start();
  session.activate();
  return session;
}

test('Cat Web values preserve platform identity and avoid ambient fetch value getters',
  { timeout: 30000 }, async t => {
    const names = ['Request', 'Response', 'Headers', 'FormData'];
    if (process.platform !== 'ios')
      for (const name of names) assert.equal(values[name], globalThis[name]);
    const descriptors = new Map([...names, 'fetch']
      .map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    const guarded = new Map(), reads = [];
    // Bind on the actual platform, then forbid consumers from reading ambient
    // fetch values. This does not simulate iOS or replace its signed bundle.
    try {
      for (const name of descriptors.keys()) {
        Object.defineProperty(globalThis, name, { configurable: true, get() {
          reads.push(name);
          throw new Error('Unexpected host ' + name + ' getter');
        } });
        guarded.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
      }
      await t.test('keeps Web brands, bodies, clones and static values over managed HTTP', async t => {
        const upstream = await serverFor(t, async (request, response) => {
          const chunks = [];
          for await (const chunk of request) chunks.push(chunk);
          if (request.url === '/redirect') {
            response.writeHead(307, { location: '/value' });
            response.end();
            return;
          }
          response.setHeader('content-type', 'application/json');
          response.setHeader('x-value', 'response');
          response.setHeader('set-cookie', ['one=1', 'two=2']);
          response.end(JSON.stringify({ body: Buffer.concat(chunks).toString(),
            header: request.headers['x-value'] }));
        });
        const session = await sessionFor(t, `
          module.exports.home = async () => {
            const headers = new Headers({ 'x-value': 'selected' });
            const form = new FormData();
            form.append('text', 'value');
            form.append('file', new File(['file-body'], 'value.txt', { type: 'text/plain' }));
            const request = new Request('https://fixture.invalid/form', {
              method: 'POST', headers, body: form,
            });
            const requestClone = request.clone();
            const parsed = await request.formData();
            const clonedForm = await requestClone.formData();
            const response = new Response(new Blob(['response-body']).stream(), { headers });
            const responseClone = response.clone();
            const json = Response.json({ value: 7 });
            const jsonClone = json.clone();
            const redirect = Response.redirect('https://fixture.invalid/target', 307);
            const error = Response.error();
            const fetched = await fetch(new Request(${JSON.stringify(upstream.socketOrigin)} + '/redirect', {
              method: 'POST', headers, body: 'upload-body',
            }));
            const fetchedClone = fetched.clone();
            return {
              form: { input: form instanceof FormData, parsed: parsed instanceof FormData,
                clone: clonedForm instanceof FormData,
                text: [parsed.get('text'), clonedForm.get('text')],
                file: parsed.get('file') instanceof File && parsed.get('file') instanceof Blob,
                fileBody: await parsed.get('file').text() },
              request: { value: request instanceof Request, clone: requestClone instanceof Request,
                headers: request.headers instanceof Headers && requestClone.headers instanceof Headers,
                header: request.headers.get('x-value'), used: [request.bodyUsed, requestClone.bodyUsed] },
              response: { value: response instanceof Response, clone: responseClone instanceof Response,
                headers: response.headers instanceof Headers,
                stream: response.body instanceof ReadableStream && responseClone.body instanceof ReadableStream,
                body: [await response.text(), await responseClone.text()] },
              statics: {
                json: json instanceof Response && jsonClone instanceof Response,
                headers: [json, redirect, error].every(value => value.headers instanceof Headers),
                body: [await json.json(), await jsonClone.json()],
                redirect: { value: redirect instanceof Response, status: redirect.status,
                  location: redirect.headers.get('location') },
                error: { value: error instanceof Response, status: error.status, type: error.type },
              },
              fetched: { value: fetched instanceof Response, clone: fetchedClone instanceof Response,
                headers: fetched.headers instanceof Headers, header: fetched.headers.get('x-value'),
                cookies: fetched.headers.getSetCookie(), redirected: fetched.redirected, url: fetched.url,
                body: [await fetched.json(), await fetchedClone.json()] },
            };
          };
        `);
        assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }), {
          form: { input: true, parsed: true, clone: true, text: ['value', 'value'],
            file: true, fileBody: 'file-body' },
          request: { value: true, clone: true, headers: true, header: 'selected', used: [true, true] },
          response: { value: true, clone: true, headers: true, stream: true,
            body: ['response-body', 'response-body'] },
          statics: { json: true, headers: true, body: [{ value: 7 }, { value: 7 }],
            redirect: { value: true, status: 307, location: 'https://fixture.invalid/target' },
            error: { value: true, status: 0, type: 'error' } },
          fetched: { value: true, clone: true, headers: true, header: 'response',
            cookies: ['one=1', 'two=2'], redirected: true, url: upstream.socketOrigin + '/value',
            body: [{ body: 'upload-body', header: 'selected' },
              { body: 'upload-body', header: 'selected' }] },
        });
        await session.stop();
        assert.equal(session.node.webResources.size, 0);
      });
      await t.test('owns a retained static Response.json body until stop releases its native reader', async t => {
        const session = await sessionFor(t, `
          const response = Response.json({ retained: true });
          const reader = response.body.getReader();
          module.exports.home = () => ({ value: response instanceof Response,
            headers: response.headers instanceof Headers, stream: response.body instanceof ReadableStream,
            reader: reader instanceof require('stream/web').ReadableStreamDefaultReader,
            locked: response.body.locked });
        `);
        assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
          { value: true, headers: true, stream: true, reader: true, locked: true });
        const bodies = [...session.node.webResources].filter(([, role]) => role === 'readableWeb')
          .map(([body]) => body);
        assert.equal(bodies.length, 1);
        assert.equal(bodies[0].locked, true);
        await session.stop();
        assert.equal(bodies[0].locked, false);
        assert.equal(session.node.webResources.size, 0);
      });
      assert.deepEqual(reads, []);
      for (const [name, descriptor] of guarded)
        assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, name), descriptor);
    } finally {
      for (const [name, descriptor] of descriptors) {
        if (descriptor) Object.defineProperty(globalThis, name, descriptor);
        else Reflect.deleteProperty(globalThis, name);
      }
    }
    for (const [name, descriptor] of descriptors)
      assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, name), descriptor);
  });
