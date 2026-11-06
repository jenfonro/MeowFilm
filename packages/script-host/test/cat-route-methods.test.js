import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCatRuntime } from '../src/cat-runtime.js';

const source = String.raw`
  const http = require('http'), fs = require('fs');
  let server;
  module.exports.start = async options => {
    const echo = value => value;
    const routes = new Map([
      ['GET /api/a/customGet', echo],
      ['POST /api/a/customPost', echo],
      ['GET /api/a/extra', echo],
      ['GET /api/b/customGet', echo],
      ['POST /api/b/customPost', echo],
      ['GET /api/b/extra', echo],
    ]);
    fs.writeFileSync('/requests.ndjson', '');
    server = http.createServer(async (request, response) => {
      const url = new URL(request.url, 'http://127.0.0.1');
      const send = value => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(value));
      };
      if (url.pathname === '/full-config' || url.pathname === '/config')
        return send({ video: { sites: options.sites } });
      const chunks = [];
      for await (const chunk of request) chunks.push(Buffer.from(chunk));
      const observed = {
        method: request.method, path: url.pathname,
        query: Object.fromEntries(url.searchParams),
        body: chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null,
      };
      fs.appendFileSync('/requests.ndjson', JSON.stringify(observed) + '\n');
      const handler = routes.get(request.method + ' ' + url.pathname);
      if (handler) return send(handler(observed));
      response.statusCode = 405;
      send({ error: { code: 'fixture_method_mismatch' } });
    });
    await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
  };
  module.exports.stop = () => {
    if (!server?.listening) return;
    server.closeAllConnections();
    return new Promise(resolve => server.close(resolve));
  };
`;

const declaredActions = ['customPost', 'customGet'];
const site = (key, declared) => ({
  key, api: '/api/' + key, ...(declared ? { actions: declaredActions } : {}),
});
const cases = [];
for (const declared of [false, true]) {
  const label = declared ? 'declared' : 'undeclared';
  const a = site('a', declared);
  cases.push([`${label} alone`, [a]]);
  for (const otherDeclared of [false, true]) {
    const otherLabel = otherDeclared ? 'declared' : 'undeclared';
    const b = site('b', otherDeclared);
    cases.push([`${label} before ${otherLabel}`, [a, b]],
      [`${label} after ${otherLabel}`, [b, a]]);
  }
}

for (const [label, sites] of cases) {
  test(`Cat Map methods preserve GET query and POST body: ${label}`,
    { timeout: 30000 }, async t => {
      const root = mkdtempSync(join(tmpdir(), 'meowfilm-cat-route-methods-'));
      const runtime = createCatRuntime({ dataRoot: root });
      t.after(async () => {
        try { await runtime.close(); }
        finally { rmSync(root, { recursive: true, force: true }); }
      });
      const installationId = 'map-methods';
      const installed = await runtime.install({
        installationId, source, config: { sites }, enabled: true,
      });
      assert.equal(installed.state, 'ready');
      const instance = (await runtime.status()).instances[0];
      assert.deepEqual(instance.sites.map(value => value.key), sites.map(value => value.key));
      const requestsPath = join(root, 'volumes', installationId, 'requests.ndjson');
      assert.equal(readFileSync(requestsPath, 'utf8'), '', 'discovery must not probe action endpoints');
      const payload = { wd: 'space 中文 &?=+', page: 2, filters: { year: '2026' }, quick: false };
      const query = { wd: 'space 中文 &?=+', page: '2', filters: '{"year":"2026"}', quick: 'false' };
      const requests = [];
      for (const configured of sites) {
        const discovered = instance.sites.find(value => value.key === configured.key);
        assert.deepEqual(discovered.actions,
          configured.actions ?? ['customGet', 'customPost', 'extra']);
        assert.deepEqual(discovered.methods, configured.actions
          ? { customPost: 'POST', customGet: 'GET' }
          : { customGet: 'GET', customPost: 'POST', extra: 'GET' });
        assert.equal(discovered.discovery, configured.actions ? 'declared' : 'source-routes');
        const invoke = operation => runtime.invoke({
          installationId, siteId: configured.key, operation, payload,
        });
        const get = { method: 'GET', path: configured.api + '/customGet', query, body: null };
        assert.deepEqual(await invoke('customGet'), get);
        const post = { method: 'POST', path: configured.api + '/customPost', query: {}, body: payload };
        assert.deepEqual(await invoke('customPost'), post);
        requests.push(get, post);
        if (configured.actions)
          await assert.rejects(invoke('extra'), { code: 'CAPABILITY_UNAVAILABLE' });
      }
      const observed = readFileSync(requestsPath, 'utf8').trim().split('\n').map(JSON.parse);
      assert.deepEqual(observed, requests, 'only explicit calls may reach the action endpoints');
    });
}

const methodGuard = `(url, request) =>
  url.pathname === '/api/a/customGet' && request.method !== 'GET'`;
const getMap = `new Map([['GET /api/a/customGet', value => value]])`;
function guardedSource(placement) {
  const guard = placement === 'loaded path'
    ? `const rejects = require('vm').runInNewContext(${JSON.stringify(methodGuard)});`
    : `const rejects = ${methodGuard};`;
  const map = placement === 'loaded method'
    ? `const routes = require('vm').runInNewContext(${JSON.stringify(getMap)});`
    : `const routes = ${getMap};`;
  const declarations = placement === 'path first' || placement === 'loaded method'
    ? guard + '\n' + map : map + '\n' + (placement === 'path last' ? '' : guard);
  const condition = placement === 'path last'
    ? `url.pathname === '/api/a/customGet' && request.method !== 'GET'`
    : 'rejects(url, request)';
  return source.replace(/    const routes = new Map\(\[[\s\S]*?    \]\);/, declarations)
    .replace("      const handler = routes.get(request.method + ' ' + url.pathname);",
      `      if (${condition}) {
        response.statusCode = 405;
        return send({ error: { code: 'fixture_method_mismatch' } });
      }
      const handler = routes.get(request.method + ' ' + url.pathname);`);
}

async function evidenceFixture(t, source, sites) {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-cat-method-evidence-'));
  const runtime = createCatRuntime({ dataRoot: root });
  let session, worker, exited;
  t.after(async () => {
    try {
      await runtime.close();
      if (worker) {
        assert.deepEqual(await exited, [0]);
        assert.equal(worker.threadId, -1);
        assert.equal(session.worker, null);
        assert.equal(session.live, false);
      }
    } finally { rmSync(root, { recursive: true, force: true }); }
  });
  const installed = await runtime.install({
    installationId: 'method-evidence', source, config: { sites }, enabled: true,
  });
  assert.equal(installed.state, 'ready');
  session = runtime.getSession('method-evidence');
  worker = session.worker;
  assert.ok(worker.threadId > 0);
  exited = once(worker, 'exit');
  const path = join(root, 'volumes', 'method-evidence', 'requests.ndjson');
  const requests = () => {
    const text = readFileSync(path, 'utf8').trim();
    return text ? text.split('\n').map(JSON.parse) : [];
  };
  assert.deepEqual(requests(), [], 'discovery must not request an action');
  return {
    installed, requests,
    invoke: (operation, payload) => runtime.invoke({
      installationId: 'method-evidence', siteId: 'a', operation, payload,
    }),
  };
}

for (const declared of [false, true]) {
  for (const placement of ['path first', 'path last', 'loaded path', 'loaded method']) {
    test(`Cat explicit GET wins over ${placement} evidence (${declared ? 'declared' : 'discovered'})`,
      { timeout: 30000 }, async t => {
        const fixture = await evidenceFixture(t, guardedSource(placement), [{
          key: 'a', api: '/api/a', ...(declared ? { actions: ['customGet'] } : {}),
        }]);
        assert.deepEqual(fixture.installed.sites[0].actions, ['customGet']);
        assert.deepEqual(fixture.installed.sites[0].methods, { customGet: 'GET' });
        const payload = { wd: 'space 中文 &?=+', page: 2, filters: { year: '2026' }, quick: false };
        const expected = {
          method: 'GET', path: '/api/a/customGet',
          query: { wd: 'space 中文 &?=+', page: '2', filters: '{"year":"2026"}', quick: 'false' },
          body: null,
        };
        assert.deepEqual(await fixture.invoke('customGet', payload), expected);
        assert.deepEqual(fixture.requests(), [expected], 'exactly one call, no probing or replay');
        t.diagnostic(JSON.stringify({ placement, declared, methods: fixture.installed.sites[0].methods,
          requests: fixture.requests() }));
      });
  }
}

test('Cat preserves explicit methods, real POST preference and methodless POST fallback',
  { timeout: 30000 }, async t => {
    const controls = source.replace(/    const routes = new Map\(\[[\s\S]*?    \]\);/, `
    const routes = new Map([
      ['GET /api/a/both', echo], ['POST /api/a/both', echo],
      ['PUT /api/a/put', echo], ['PATCH /api/a/patch', echo],
      ['DELETE /api/a/remove', echo], ['HEAD /api/a/head', echo],
      ['OPTIONS /api/a/options', echo],
    ]);`).replace("      const handler = routes.get(request.method + ' ' + url.pathname);", `
      if (url.pathname === '/api/a/compareOnly') return send(observed);
      switch (url.pathname) { case '/api/a/switchOnly': return send(observed); }
      if (/^\\/api\\/a\\/(patternOnly)$/.test(url.pathname)) return send(observed);
      const handler = routes.get(request.method + ' ' + url.pathname);`);
    const fixture = await evidenceFixture(t, controls, [{ key: 'a', api: '/api/a' }]);
    const methods = {
      both: 'POST', put: 'PUT', patch: 'PATCH', remove: 'DELETE',
      head: 'HEAD', options: 'OPTIONS', compareOnly: 'POST', switchOnly: 'POST', patternOnly: 'POST',
    };
    assert.deepEqual(fixture.installed.sites[0].methods, methods);
    const payload = { page: 2, filters: { year: '2026' } };
    const expected = [];
    for (const [operation, method] of Object.entries(methods)) {
      const query = method === 'HEAD' ? { page: '2', filters: '{"year":"2026"}' } : {};
      const observed = { method, path: '/api/a/' + operation, query,
        body: method === 'HEAD' ? null : payload };
      assert.deepEqual(await fixture.invoke(operation, payload), method === 'HEAD' ? null : observed);
      expected.push(observed);
    }
    assert.deepEqual(fixture.requests(), expected);
  });
