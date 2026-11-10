import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { syncBuiltinESMExports } from 'node:module';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { connectFilmPlayback, createAppRuntime, createRuntimeClient,
  createCatScriptModule, createNeteaseTokenModule } from '../apps/runtime/entry.mjs';

const root = fileURLToPath(new URL('../tmp/test-results/film-playback/http/', import.meta.url));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((a, b) => { resolve = a; reject = b; });
  return { promise, resolve, reject };
};
const tick = () => new Promise(resolve => setImmediate(resolve));
async function until(work) {
  for (let turn = 0; turn < 100; turn++) {
    const value = await work();
    if (value) return value;
    await tick();
  }
  assert.fail('Expected public HTTP state was not reached');
}
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
const source = { key: 'film-source', engine: 'cat', installationId: 'script', siteId: 'fixture',
  generation: 1, name: 'Fixture source' };
const movie = { id: 'film:fixture', kind: 'movie', title: 'Fixture Film' };
async function setup(t, { invoke, tokenProvider } = {}) {
  mkdirSync(root, { recursive: true });
  const directory = mkdtempSync(join(root, 'case-'));
  const identity = { sessionId: randomUUID() };
  const calls = [], reads = [];
  const server = http.createServer((request, response) => {
    reads.push({ url: request.url, cookie: request.headers.cookie, range: request.headers.range });
    if (request.headers.cookie !== 'film-fixture=private') { response.writeHead(403); response.end(); return; }
    response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': 8 });
    response.end(Buffer.from([0, 1, 2, 3, 4, 5, 6, 7]));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = `http://127.0.0.1:${server.address().port}/private-video`;
  let instances = [{ installationId: 'script', generation: 1, state: 'ready',
    sites: [{ key: 'fixture', name: 'Fixture source' }] }];
  const success = value => ({ ok: true, contract: 'cat.script.v2', value });
  const cat = { id: 'cat', version: '0.2.0', contract: 'cat.script.v2', upload() { assert.fail('unexpected upload'); },
    methods: {
      'cat.script.status': async () => success({ instances: structuredClone(instances) }),
      'cat.script.edit': async input => {
        if (input.remove) instances = [];
        else if (input.name) instances[0].sites[0].name = input.name;
        if (input.changed) instances[0].generation++;
        if (input.failAfterCommit) return { ok: false, contract: 'cat.script.v2', code: 'STORAGE_ERROR' };
        return success({ changed: !!input.changed });
      },
      'cat.script.invoke': async (input, context) => {
        calls.push(structuredClone(input));
        const replacement = await invoke?.(input, context);
        if (replacement !== undefined) return success(replacement);
        switch (input.operation) {
          case 'search': return success({ list: [{ vod_id: 0, vod_name: 'Fixture Film' }], pagecount: 1 });
          case 'detail': return success({ list: [{ vod_id: 0, vod_name: 'Fixture Film',
            vod_play_from: '原始线路', vod_play_url: 'Fixture Film.1080p$opaque-id' }] });
          case 'play': return success({ url: address, headers: { Cookie: 'film-fixture=private' }, watchReport: true });
          case 'report': return success({ ok: true });
          default: assert.fail(`Unexpected fixture operation ${input.operation}`);
        }
      },
    }, async close() {},
  };
  const modules = [cat], film = connectFilmPlayback(modules, { dataRoot: directory });
  if (tokenProvider) modules.push(createNeteaseTokenModule({ provider: tokenProvider }));
  const app = createAppRuntime({ ...identity, modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  const rpc = async (operation, input = {}, options) => client.call(`film.${operation}`, input, options);
  const snapshot = async id => okay(await rpc('session.snapshot', { sessionId: id }));
  const command = async (id, action, input = {}) => okay(await rpc('session.command', { sessionId: id, action, ...input }));
  const open = async (input = {}) => {
    const value = okay(await rpc('session.open', { platform: 'test', target: movie, sources: [source], ...input }));
    await until(async () => (await snapshot(value.sessionId)).stage !== 'loading');
    return value.sessionId;
  };
  const pending = id => until(async () => (await snapshot(id)).attempt);
  t.after(async () => {
    await client.close(); await app.close();
    await new Promise(resolve => { server.closeAllConnections(); server.close(resolve); });
    rmSync(directory, { recursive: true });
  });
  const headers = () => ({ 'x-meow-request-id': randomUUID() });
  return { rpc, snapshot, command, open, pending, calls, reads, address, app, client, film, headers };
}

async function observation(f, id, query = '', signal) {
  const controller = new AbortController();
  const response = await fetch(`${f.app.baseUrl}/v1/film/events/${id}${query}`, {
    headers: f.headers(), signal: signal ? AbortSignal.any([controller.signal, signal]) : controller.signal,
  });
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type'), /^text\/event-stream/);
  const reader = response.body.getReader(), decoder = new TextDecoder();
  let pending = '';
  return {
    headers: response.headers,
    async next() {
      while (!pending.includes('\n\n')) {
        const value = await reader.read();
        assert.equal(value.done, false);
        pending += decoder.decode(value.value, { stream: true });
      }
      const end = pending.indexOf('\n\n'), message = pending.slice(0, end);
      pending = pending.slice(end + 2);
      const data = message.split('\n').find(row => row.startsWith('data: '));
      assert.ok(data);
      const value = JSON.parse(data.slice(6));
      assert.ok(message.includes(`id: ${value.sequence}`));
      return value;
    },
    async drain() { while (!(await reader.read()).done) {} },
    async close() { controller.abort(); await reader.cancel().catch(() => {}); },
  };
}

test('film HTTP source discovery, exact list selection, private first-read routing and real watch events', async t => {
  const f = await setup(t), id = await f.open();
  assert.equal(f.calls.length, 0);
  await f.command(id, 'search.more');
  await until(async () => (await f.snapshot(id)).searchComplete);
  assert.deepEqual(f.calls.map(value => value.operation), ['search']);
  const result = (await f.snapshot(id)).sources[0];
  await f.command(id, 'browse', { sourceId: result.id });
  await until(async () => (await f.snapshot(id)).browsing.episodes.length === 1);
  assert.deepEqual(f.calls.map(value => value.operation), ['search', 'detail']);
  assert.equal(f.calls[1].payload.id, 0);
  await f.command(id, 'play');
  const attempt = await f.pending(id);
  const receipt = okay(await f.rpc('session.attempt', { sessionId: id, attemptId: attempt.id }));
  assert.equal(f.reads.length, 0, 'address preparation must not read media');
  assert.notEqual(receipt.url, f.address);
  assert.equal(new URL(receipt.url).origin + '/', receipt.mediaOrigin);
  const frame = JSON.stringify(await f.snapshot(id));
  for (const secret of ['film-fixture=private', 'private-video', 'opaque-id', receipt.url]) assert.equal(frame.includes(secret), false);
  assert.deepEqual([...new Uint8Array(await (await fetch(receipt.url)).arrayBuffer())], [0, 1, 2, 3, 4, 5, 6, 7]);
  assert.deepEqual(f.reads.map(value => value.cookie), ['film-fixture=private']);
  assert.equal(okay(await f.rpc('library.get')).history.length, 0);
  assert.equal(f.calls.some(value => value.operation === 'report'), false);
  await f.command(id, 'media.started', { attemptId: attempt.id });
  await until(() => f.calls.some(value => value.operation === 'report'));
  const report = f.calls.find(value => value.operation === 'report');
  assert.equal(report.payload.flag, '原始线路');
  assert.equal(report.payload.id, 'opaque-id');
  assert.equal(report.payload.event, 'started');
  await f.command(id, 'media.progress', { attemptId: attempt.id, position: 23, duration: 90, paused: true });
  assert.equal(okay(await f.rpc('library.get')).history[0].position, 23);
  await f.command(id, 'stop');
  assert.equal((await fetch(receipt.url)).status, 404);
});

test('SSE disconnect and reconnect without credentials observe one live session without stopping playback or leaking receipts', async t => {
  const f = await setup(t), id = await f.open();
  const external = { ...f, headers: () => ({
    Host: 'external.example.invalid', Origin: 'https://browser.example.invalid',
  }) };
  for (const query of ['?extra=1', '?after=1&after=2', '?after=1.5']) {
    const response = await fetch(`${f.app.baseUrl}/v1/film/events/${id}${query}`, { headers: f.headers() });
    assert.equal((await response.json()).code, 'INVALID_REQUEST');
  }
  const events = await observation(external, id);
  assert.equal(events.headers.get('access-control-allow-origin'), '*');
  assert.match(events.headers.get('x-meow-request-id'), /^[0-9a-f-]{36}$/);
  const initial = await events.next();
  assert.equal(initial.sessionId, id);
  await events.close();
  await f.command(id, 'play'); const attempt = await f.pending(id);
  await f.command(id, 'media.started', { attemptId: attempt.id });
  const reconnected = await observation(external, id, `?after=${initial.sequence}`);
  const current = await reconnected.next();
  assert.equal(current.current.attemptId, attempt.id);
  assert.ok(current.sequence > initial.sequence);
  assert.equal(JSON.stringify(current).includes('film-fixture=private'), false);
  await reconnected.close();
  assert.equal((await f.snapshot(id)).current.attemptId, attempt.id);
});

test('source display changes preserve live media, but actual generation changes revoke it even if the mutation receipt fails', async t => {
  const f = await setup(t), id = await f.open({ platform: 'windows' });
  const attempt = await f.pending(id);
  await f.command(id, 'media.started', { attemptId: attempt.id });
  const receipt = okay(await f.rpc('session.attempt', { sessionId: id, attemptId: attempt.id }));
  assert.equal((await f.client.call('cat.script.edit', { installationId: 'script', name: 'Renamed source' })).ok, true);
  assert.equal((await f.snapshot(id)).current.attemptId, attempt.id);
  assert.equal((await f.snapshot(id)).sources[0].source.name, 'Renamed source');
  assert.equal((await fetch(receipt.url)).status, 200);
  const changed = await f.client.call('cat.script.edit', { installationId: 'script', changed: true, failAfterCommit: true });
  assert.equal(changed.ok, false);
  assert.equal(changed.code, 'STORAGE_ERROR');
  assert.equal((await f.snapshot(id)).current, null);
  assert.equal((await fetch(receipt.url)).status, 404);
  assert.equal((await f.rpc('session.attempt', { sessionId: id, attemptId: attempt.id })).code, 'STALE_SESSION');
});

test('a cancelled HTTP search observer leaves one finite producer which populates the original-freshness cache', async t => {
  const entered = deferred(), finish = deferred();
  const f = await setup(t, { async invoke(input, context) {
    if (input.operation !== 'search') return;
    entered.resolve(context.signal);
    await finish.promise;
    return { list: [{ vod_id: 0, vod_name: 'Fixture Film' }], pagecount: 1 };
  } });
  const controller = new AbortController();
  const work = f.rpc('data.search', { source, query: movie.title, page: 1 }, { signal: controller.signal });
  const signal = await entered.promise;
  controller.abort();
  await assert.rejects(work, { code: 'CANCELLED' });
  assert.equal(signal.aborted, false, 'ordinary observer cancellation is not account/source revocation');
  finish.resolve();
  const request = { kind: 'search', source, query: movie.title, page: 1 };
  const cached = await until(async () => okay(await f.rpc('data.cache.entry', request)));
  assert.equal(cached.completeness, 'complete');
  assert.ok(cached.fetchedAt > 0 && cached.expiresAt > cached.fetchedAt);
  assert.equal(okay(await f.rpc('data.search', { source, query: movie.title, page: 1 })).items.length, 1);
  assert.equal(f.calls.filter(value => value.operation === 'search').length, 1);
  assert.equal(okay(await f.rpc('data.cache.entry', request)).fetchedAt, cached.fetchedAt);
});

test('runtime shutdown stops provider work and waits for physical completion, not just cancellation', async t => {
  const entered = deferred(), finish = deferred();
  const f = await setup(t, { async invoke(input, context) {
    if (input.operation !== 'search') return;
    entered.resolve(context.signal);
    await finish.promise;
    return { list: [], pagecount: 0 };
  } });
  const work = f.rpc('data.search', { source, query: movie.title, page: 1 }).catch(error => ({ code: error.code }));
  const signal = await entered.promise;
  let completed = false;
  const closing = f.client.shutdown().then(reply => { completed = true; return reply; });
  await until(() => signal.aborted);
  assert.equal(completed, false);
  assert.equal((await f.rpc('settings.get')).code, 'CLOSED');
  finish.resolve();
  assert.equal(okay(await closing).cleanupComplete, true);
  // Runtime shutdown closes admitted HTTP exchanges. This is not the caller's
  // local AbortSignal cancellation, whose public result above is CANCELLED.
  assert.equal((await work).code, 'CLOSED');
});

test('film SSE and requests coexist with token success, cancellation, SDK failure and runtime shutdown',
  { timeout: 10000 }, async t => {
    const requests = [], searchEntered = deferred(), finishSearch = deferred();
    let events, closes = 0;
    t.after(async () => { finishSearch.resolve(); await events?.close(); });
    const f = await setup(t, {
      tokenProvider: {
        get({ signal }) {
          const request = deferred();
          const cancel = () => request.reject(Object.assign(new Error('CANCELLED'), { code: 'CANCELLED' }));
          signal.addEventListener('abort', cancel, { once: true });
          requests.push(request);
          return request.promise.finally(() => signal.removeEventListener('abort', cancel));
        },
        async close() {
          closes++;
          for (const request of requests) request.reject(Object.assign(new Error('CLOSED'), { code: 'CLOSED' }));
        },
      },
      async invoke(input) {
        if (input.operation !== 'search' || input.payload.wd !== 'parallel-sdk') return;
        searchEntered.resolve();
        await finishSearch.promise;
        return { list: [{ vod_id: 'parallel', vod_name: 'Parallel Film' }], pagecount: 1 };
      },
    });
    const id = await f.open({ platform: 'windows' }), attempt = await f.pending(id);
    await f.command(id, 'media.started', { attemptId: attempt.id });
    events = await observation(f, id);
    let previous = await events.next();
    const progress = async position => {
      await f.command(id, 'media.progress', { attemptId: attempt.id, position, duration: 90, paused: false });
      let current;
      do { current = await events.next(); } while (current.current?.position !== position);
      assert.equal(current.sessionId, id);
      assert.equal(current.current.attemptId, attempt.id);
      assert.ok(current.sequence > previous.sequence);
      previous = current;
    };

    const success = f.client.call('netease.token.get');
    const first = await until(() => requests[0]);
    await progress(11);
    first.resolve('synthetic-coexistence-token');
    assert.deepEqual(await success, {
      ok: true, contract: 'netease.token.v1', value: { token: 'synthetic-coexistence-token' },
    });

    const controller = new AbortController();
    const cancelled = assert.rejects(f.client.call('netease.token.get', {}, { signal: controller.signal }),
      { code: 'CANCELLED' });
    await until(() => requests[1]);
    controller.abort();
    await cancelled;
    await progress(22);

    const failed = f.client.call('netease.token.get');
    const third = await until(() => requests[2]);
    const search = f.rpc('data.search', { source, query: 'parallel-sdk', page: 1 });
    await searchEntered.promise;
    third.reject(new Error('private SDK failure detail'));
    assert.deepEqual(await failed, { ok: false, contract: 'netease.token.v1', code: 'SDK_FAILURE' });
    finishSearch.resolve();
    assert.deepEqual(okay(await search).items.map(item => item.vod_id), ['parallel']);
    await progress(33);

    const stopped = assert.rejects(f.client.call('netease.token.get'), { code: 'CLOSED' });
    await until(() => requests[3]);
    await progress(44);
    // Keep the SSE exchange open: /v1/close must drain both it and the token request.
    const ended = events.drain().catch(error => { assert.equal(error.name, 'TypeError'); });
    assert.deepEqual(okay(await f.client.shutdown()), { closed: true, cleanupComplete: true });
    await Promise.all([stopped, ended]);
    assert.equal(closes, 1);
    await until(() => f.app.state.active === 0);
  });

test('real Cat Worker local media uses its mapped listener, stays cold during preparation and expires with the source',
  { timeout: 20000 }, async t => {
    mkdirSync(root, { recursive: true });
    const directory = mkdtempSync(join(root, 'cat-'));
    const cat = createCatScriptModule({ dataRoot: join(directory, 'cat') });
    const modules = [cat];
    connectFilmPlayback(modules, { dataRoot: directory });
    const identity = { sessionId: randomUUID() };
    const app = createAppRuntime({ ...identity, modules });
    await app.start();
    const client = createRuntimeClient({ baseUrl: app.baseUrl });
    let events;
    t.after(async () => {
      await events?.close();
      await client.close();
      await app.close();
      rmSync(directory, { recursive: true });
    });
    const script = String.raw`
      const http = require('http');
      let server;
      const mediaReads = [];
      const json = (response, value) => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(value));
      };
      module.exports.start = () => new Promise(resolve => {
        server = http.createServer((request, response) => {
          const path = request.url.split('?')[0];
          if (path === '/config' || path === '/full-config') {
            json(response, { video: { sites: [{ key: 'fixture', name: 'Fixture source',
              api: '/api/fixture', actions: ['home', 'search', 'detail', 'play'] }] } });
          } else if (path === '/api/fixture/home') {
            json(response, { mediaReads });
          } else if (path === '/api/fixture/search') {
            json(response, { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film' }], pagecount: 1 });
          } else if (path === '/api/fixture/detail') {
            json(response, { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film',
              vod_play_from: '原始线路', vod_play_url: 'Fixture Film.1080p$fixture' }] });
          } else if (path === '/api/fixture/play') {
            json(response, { parse: 0, url: 'http://127.0.0.1:9987/proxy/video.mp4' });
          } else if (path === '/proxy/video.mp4') {
            mediaReads.push({ method: request.method, range: request.headers.range ?? null });
            const bytes = Buffer.from(Array.from({ length: 32 }, (_, index) => index));
            if (request.headers.range === 'bytes=8-15') {
              response.writeHead(206, { 'content-type': 'video/mp4', 'content-range': 'bytes 8-15/32',
                'content-length': 8 });
              response.end(bytes.subarray(8, 16));
            } else {
              response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': bytes.length });
              response.end(bytes);
            }
          } else { response.writeHead(404); response.end(); }
        });
        server.listen(9987, '127.0.0.1', resolve);
      });
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `;
    okay(await client.upload('cat', 'script', 1, Buffer.from(script)));
    okay(await client.call('cat.script.install', {
      installationId: 'script', sourceRevision: 1, enabled: true,
    }));
    const state = okay(await client.call('cat.script.status', {})).instances[0];
    assert.equal(state.state, 'ready');
    const actualSource = { ...source, generation: state.generation };
    const readState = async () => okay(await client.call('cat.script.invoke', {
      installationId: 'script', generation: state.generation, siteId: 'fixture', operation: 'home',
    }));
    const rpc = (operation, params) => client.call(`film.${operation}`, params);
    const opened = okay(await rpc('session.open', { platform: 'test', target: movie, sources: [actualSource] }));
    const id = opened.sessionId;
    events = await observation({ app, headers: () => ({
      'x-meow-request-id': randomUUID(),
    }) }, id);
    let snapshot = await events.next();
    while (snapshot.stage === 'loading') snapshot = await events.next();
    assert.equal(snapshot.stage, 'idle');
    okay(await rpc('session.command', { sessionId: id, action: 'play' }));
    do { snapshot = await events.next(); } while (!snapshot.attempt && !snapshot.error);
    assert.ok(snapshot.attempt, JSON.stringify(snapshot));
    const receipt = okay(await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id }));
    assert.equal(receipt.mediaOrigin, undefined, 'headerless Cat media does not create a film stream route');
    assert.equal(new URL(receipt.url).origin,
      okay(await client.call('cat.script.status', {})).aggregate.origin);
    assert.match(new URL(receipt.url).pathname,
      new RegExp(`^/proxy/script/${state.generation}/[^/]+/proxy/video\\.mp4$`));
    assert.deepEqual((await readState()).mediaReads, [], 'preparation must not read a hidden media stream');
    const response = await fetch(receipt.url, { headers: { Range: 'bytes=8-15' } });
    assert.equal(response.status, 206);
    assert.equal(response.headers.get('content-range'), 'bytes 8-15/32');
    assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [8, 9, 10, 11, 12, 13, 14, 15]);
    assert.deepEqual((await readState()).mediaReads, [{ method: 'GET', range: 'bytes=8-15' }]);
    const publicState = JSON.stringify(okay(await rpc('session.snapshot', { sessionId: id })));
    for (const value of [receipt.url, '/proxy/video.mp4']) {
      assert.equal(publicState.includes(value), false, 'public snapshots cannot leak private media resources');
    }
    assert.equal(okay(await rpc('library.get', {})).history.length, 0, 'transport reads are not real media output');
    okay(await client.call('cat.script.enable', { installationId: 'script', enabled: false }));
    const expired = await fetch(receipt.url);
    assert.equal(expired.status, 502);
    assert.deepEqual(await expired.json(), { error: { code: 'STALE_SESSION' } });
    assert.equal((await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id })).code, 'STALE_SESSION');
  });

test('real Cat Worker manifest media preserves DASH BaseURL and xml:base through the runtime and exact ranged children',
  { timeout: 30000 }, async t => {
    mkdirSync(root, { recursive: true });
    const directory = mkdtempSync(join(root, 'cat-manifest-'));
    const cat = createCatScriptModule({ dataRoot: join(directory, 'cat') });
    const modules = [cat];
    connectFilmPlayback(modules, { dataRoot: directory });
    const identity = { sessionId: randomUUID() };
    const app = createAppRuntime({ ...identity, modules });
    await app.start();
    const client = createRuntimeClient({ baseUrl: app.baseUrl });
    let events;
    t.after(async () => {
      await events?.close();
      await client.close();
      await app.close();
      rmSync(directory, { recursive: true });
    });
    const dash = `<d:MPD xmlns:d='urn:mpeg:dash:schema:mpd:2011' xmlns:m="urn:fixture:metadata" xml:base='contexts/'>
      <d:BaseURL xml:base='../'>assets/</d:BaseURL><d:Period xml:base="../period/"><d:AdaptationSet xml:base='tracks/'>
      <d:SegmentTemplate xml:base="parts/" initialization='init.mp4?key=a&amp;b=c' media="seg-$Number$.m4s?part=1"/>
      <d:Representation id='relative' bandwidth="1"><m:note media="/config">opaque</m:note></d:Representation>
      <d:Representation id="root" bandwidth='2'><d:BaseURL>/proxy/root/</d:BaseURL></d:Representation>
      <d:Representation id='absolute' bandwidth="3"><d:BaseURL>http://127.0.0.1:9987/proxy/absolute/</d:BaseURL>
      </d:Representation><d:Representation id="explicit" bandwidth='4'>
      <d:BaseURL>shallow/</d:BaseURL><d:BaseURL>deeper/path/</d:BaseURL>
      <d:SegmentTemplate initialization='/proxy/explicit/init.mp4?key=a&amp;b=c'
        media="http://127.0.0.1:9987/proxy/explicit/seg-$Number$.m4s?part=1"/>
      </d:Representation></d:AdaptationSet></d:Period></d:MPD>`;
    const script = String.raw`
      const http = require('http');
      let server, managementReads = 0;
      const reads = [];
      const json = (response, value) => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(value));
      };
      module.exports.start = () => new Promise(resolve => {
        server = http.createServer((request, response) => {
          const path = request.url.split('?')[0];
          if (path === '/config' || path === '/full-config') {
            managementReads++;
            return json(response, { video: { sites: [{ key: 'fixture', name: 'Fixture source',
              api: '/api/fixture', actions: ['home', 'search', 'detail', 'play'] }] } });
          }
          if (path === '/api/fixture/home') return json(response, { reads, managementReads });
          if (path === '/api/fixture/search') return json(response,
            { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film' }], pagecount: 1 });
          if (path === '/api/fixture/detail') return json(response, { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film',
            vod_play_from: '原始线路', vod_play_url: 'Fixture Film.1080p$fixture' }] });
          if (path === '/api/fixture/play') return json(response,
            { parse: 0, url: 'http://127.0.0.1:9987/proxy/dash/root.mpd' });
          reads.push({ method: request.method, url: request.url, range: request.headers.range ?? null });
          if (path === '/proxy/dash/root.mpd') {
            response.writeHead(200, { 'content-type': 'application/dash+xml' });
            return response.end(MPD_TEXT);
          }
          const bases = ['/proxy/dash/period/tracks/parts/', '/proxy/root/parts/', '/proxy/absolute/parts/',
            '/proxy/explicit/'];
          const unlistedBases = ['/proxy/dash/period/tracks/', '/proxy/root/', '/proxy/absolute/',
            '/proxy/dash/period/tracks/shallow/', '/proxy/dash/period/tracks/deeper/path/'];
          if (unlistedBases.some(base => request.url === base + 'unregistered/config?url=/config&sig=a%2Fb')) {
            response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': 4 });
            return response.end(Buffer.from([17, 34, 51, 68]));
          }
          if (!bases.some(base => path === base + 'init.mp4' || /^seg-[0-9]+\.m4s$/.test(path.slice(base.length)) &&
              path.startsWith(base))) {
            response.writeHead(404);
            return response.end('wrong-resource');
          }
          const bytes = Buffer.from(Array.from({ length: 32 }, (_, index) => index));
          const range = /^bytes=(\d+)-(\d+)$/.exec(request.headers.range || '');
          if (range) {
            const start = Number(range[1]), end = Number(range[2]);
            response.writeHead(206, { 'content-type': 'video/mp4',
              'content-range': 'bytes ' + start + '-' + end + '/32', 'content-length': end - start + 1 });
            return response.end(bytes.subarray(start, end + 1));
          }
          response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': bytes.length });
          response.end(bytes);
        });
        server.listen(9987, '127.0.0.1', resolve);
      });
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `.replace('MPD_TEXT', JSON.stringify(dash));
    okay(await client.upload('cat', 'script', 1, Buffer.from(script)));
    okay(await client.call('cat.script.install', { installationId: 'script', sourceRevision: 1, enabled: true }));
    const state = okay(await client.call('cat.script.status', {})).instances[0];
    assert.equal(state.state, 'ready');
    const readState = async () => okay(await client.call('cat.script.invoke', {
      installationId: 'script', generation: state.generation, siteId: 'fixture', operation: 'home',
    }));
    const before = await readState(), rpc = (operation, params) => client.call(`film.${operation}`, params);
    const id = okay(await rpc('session.open', { platform: 'test', target: movie,
      sources: [{ ...source, generation: state.generation }] })).sessionId;
    events = await observation({ app, headers: () => ({
      'x-meow-request-id': randomUUID(),
    }) }, id);
    let snapshot;
    do { snapshot = await events.next(); } while (snapshot.stage === 'loading');
    assert.equal(snapshot.stage, 'idle');
    okay(await rpc('session.command', { sessionId: id, action: 'play' }));
    do { snapshot = await events.next(); } while (!snapshot.attempt && !snapshot.error);
    assert.ok(snapshot.attempt, JSON.stringify(snapshot));
    const receipt = okay(await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id }));
    const aggregateOrigin = okay(await client.call('cat.script.status', {})).aggregate.origin;
    assert.equal(receipt.mediaOrigin, undefined, 'headerless DASH uses the Cat aggregate directly');
    assert.equal(new URL(receipt.url).origin, aggregateOrigin);
    assert.deepEqual((await readState()).reads, [], 'manifest preparation does not read any media');
    const response = await fetch(receipt.url);
    assert.equal(response.status, 200);
    const text = await response.text();
    const representations = [...text.matchAll(/<d:Representation\b([^>]*)>([^]*?)<\/d:Representation>/g)];
    assert.equal(representations.length, 4);
    assert.ok(text.includes('<m:note media="/config">opaque</m:note>'), 'foreign namespace metadata stays opaque');
    const expected = [], localChildren = [];
    for (const representation of representations) {
      const name = /\bid="([^"]+)"/.exec(representation[1])[1];
      const bases = [...representation[2].matchAll(/<d:BaseURL>([^<]+)<\/d:BaseURL>/g)]
        .map(match => match[1].replace(/&amp;/g, '&'));
      assert.equal(bases.length, name === 'explicit' ? 2 : 1, 'all alternative bases remain available');
      const segment = /<d:SegmentTemplate\b([^>]*)\/>/.exec(representation[2])[1];
      const initialization = /\binitialization="([^"]+)"/.exec(segment)[1].replace(/&amp;/g, '&');
      const media = /\bmedia="([^"]+)"/.exec(segment)[1].replace(/&amp;/g, '&');
      const originBase = name === 'relative' ? '/proxy/dash/period/tracks/parts/'
        : name === 'explicit' ? '/proxy/explicit/' : `/proxy/${name}/parts/`;
      for (const [baseIndex, base] of bases.entries()) {
        assert.equal(new URL(base).origin, aggregateOrigin);
        const unlistedBase = name === 'relative' ? '/proxy/dash/period/tracks/'
          : name === 'explicit' ? '/proxy/dash/period/tracks/' + (baseIndex === 0 ? 'shallow/' : 'deeper/path/')
            : `/proxy/${name}/`;
        const init = new URL(initialization, base).href;
        localChildren.push(init);
        const initResponse = await fetch(init);
        assert.equal(initResponse.status, 200);
        assert.deepEqual([...new Uint8Array(await initResponse.arrayBuffer())], Array.from({ length: 32 }, (_, i) => i));
        expected.push({ method: 'GET', url: originBase + 'init.mp4?key=a&b=c', range: null });
        for (const number of [1, 3]) {
          const url = new URL(media.replace('$Number$', String(number)), base).href;
          localChildren.push(url);
          const child = await fetch(url, { headers: { Range: 'bytes=8-15' } });
          assert.equal(child.status, 206);
          assert.equal(child.headers.get('content-range'), 'bytes 8-15/32');
          assert.deepEqual([...new Uint8Array(await child.arrayBuffer())], [8, 9, 10, 11, 12, 13, 14, 15]);
          expected.push({ method: 'GET', url: originBase + `seg-${number}.m4s?part=1`, range: 'bytes=8-15' });
        }
        const unlisted = await fetch(new URL('unregistered/config?url=/config&sig=a%2Fb', base));
        assert.equal(unlisted.status, 200, 'an unlisted suffix resolves against the original BaseURL');
        assert.deepEqual([...new Uint8Array(await unlisted.arrayBuffer())], [17, 34, 51, 68]);
        expected.push({ method: 'GET', url: unlistedBase + 'unregistered/config?url=/config&sig=a%2Fb', range: null });
        const missing = await fetch(new URL('missing/config?sig=a%2Fb', base));
        assert.equal(missing.status, 404);
        assert.equal(await missing.text(), 'wrong-resource', '404 comes from the actual Worker resource lookup');
        expected.push({ method: 'GET', url: unlistedBase + 'missing/config?sig=a%2Fb', range: null });
      }
    }
    const after = await readState();
    assert.equal(after.managementReads, before.managementReads);
    assert.deepEqual(after.reads, [{ method: 'GET', url: '/proxy/dash/root.mpd', range: null }, ...expected]);
    const publicState = JSON.stringify(okay(await rpc('session.snapshot', { sessionId: id })));
    for (const value of [receipt.url, '/proxy/dash/root.mpd']) {
      assert.equal(publicState.includes(value), false);
    }
    okay(await client.call('cat.script.enable', { installationId: 'script', enabled: false }));
    for (const url of [receipt.url, localChildren[0]]) {
      const revoked = await fetch(url);
      assert.equal(revoked.status, 502);
      assert.deepEqual(await revoked.json(), { error: { code: 'STALE_SESSION' } });
    }
    assert.equal((await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id })).code, 'STALE_SESSION');
  });

test('real Cat Worker manifest media keeps HLS free titles verbatim and never reads management paths for them',
  { timeout: 30000 }, async t => {
    mkdirSync(root, { recursive: true });
    const directory = mkdtempSync(join(root, 'cat-manifest-'));
    const cat = createCatScriptModule({ dataRoot: join(directory, 'cat') }), modules = [cat];
    connectFilmPlayback(modules, { dataRoot: directory });
    const identity = { sessionId: randomUUID() };
    const app = createAppRuntime({ ...identity, modules });
    await app.start();
    const client = createRuntimeClient({ baseUrl: app.baseUrl });
    let events;
    t.after(async () => {
      await events?.close();
      await client.close();
      await app.close();
      rmSync(directory, { recursive: true });
    });
    const title = '  #EXTINF:3,URI="/config",media="/admin",sourceURL=\'/full-config\'  ';
    const playlist = '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-DEFINE:NAME="part",VALUE="1"\n' +
      '#EXT-X-KEY:METHOD=AES-128,URI="key?token=a,b"\n' +
      '#EXT-X-MAP:URI="/proxy/hls/init.mp4"\n' + title + '\nsegment-{$part}.ts?part=1\n#EXT-X-ENDLIST\n';
    const script = String.raw`
      const http = require('http');
      let server, managementReads = 0;
      const reads = [];
      const json = (response, value) => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(value));
      };
      module.exports.start = () => new Promise(resolve => {
        server = http.createServer((request, response) => {
          const path = request.url.split('?')[0];
          if (path === '/config' || path === '/full-config') {
            managementReads++;
            return json(response, { video: { sites: [{ key: 'fixture', name: 'Fixture source',
              api: '/api/fixture', actions: ['home', 'search', 'detail', 'play'] }] } });
          }
          if (path === '/api/fixture/home') return json(response, { reads, managementReads });
          if (path === '/api/fixture/search') return json(response,
            { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film' }], pagecount: 1 });
          if (path === '/api/fixture/detail') return json(response, { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film',
            vod_play_from: '原始线路', vod_play_url: 'Fixture Film.1080p$fixture' }] });
          if (path === '/api/fixture/play') return json(response,
            { parse: 0, url: 'http://127.0.0.1:9987/proxy/hls/list', format: 'hls' });
          reads.push({ method: request.method, url: request.url });
          if (path === '/proxy/hls/list') {
            response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl' });
            return response.end(HLS_TEXT);
          }
          if (!['/proxy/hls/key?token=a,b', '/proxy/hls/init.mp4', '/proxy/hls/segment-1.ts?part=1'].includes(request.url)) {
            response.writeHead(404);
            return response.end('wrong-resource');
          }
          response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': 4 });
          response.end(Buffer.from([2, 4, 6, 8]));
        });
        server.listen(9987, '127.0.0.1', resolve);
      });
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `.replace('HLS_TEXT', JSON.stringify(playlist));
    okay(await client.upload('cat', 'script', 1, Buffer.from(script)));
    okay(await client.call('cat.script.install', { installationId: 'script', sourceRevision: 1, enabled: true }));
    const state = okay(await client.call('cat.script.status', {})).instances[0];
    assert.equal(state.state, 'ready');
    const readState = async () => okay(await client.call('cat.script.invoke', {
      installationId: 'script', generation: state.generation, siteId: 'fixture', operation: 'home',
    }));
    const before = await readState(), rpc = (operation, params) => client.call(`film.${operation}`, params);
    const id = okay(await rpc('session.open', { platform: 'test', target: movie,
      sources: [{ ...source, generation: state.generation }] })).sessionId;
    events = await observation({ app, headers: () => ({
      'x-meow-request-id': randomUUID(),
    }) }, id);
    let snapshot;
    do { snapshot = await events.next(); } while (snapshot.stage === 'loading');
    assert.equal(snapshot.stage, 'idle');
    okay(await rpc('session.command', { sessionId: id, action: 'play' }));
    do { snapshot = await events.next(); } while (!snapshot.attempt && !snapshot.error);
    assert.ok(snapshot.attempt, JSON.stringify(snapshot));
    const receipt = okay(await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id }));
    const aggregateOrigin = okay(await client.call('cat.script.status', {})).aggregate.origin;
    assert.equal(receipt.mediaOrigin, undefined, 'headerless HLS uses the Cat aggregate directly');
    assert.equal(new URL(receipt.url).origin, aggregateOrigin);
    assert.deepEqual((await readState()).reads, [], 'an extensionless HLS descriptor must not trigger hidden media reads');
    const response = await fetch(receipt.url);
    assert.equal(response.status, 200);
    const text = await response.text();
    assert.ok(text.split('\n').includes(title), 'the entire free title, including whitespace and quotes, is opaque');
    assert.deepEqual((await readState()).reads, [{ method: 'GET', url: '/proxy/hls/list' }]);
    const references = [...text.matchAll(/^#EXT-X-[^\n]*?\bURI="([^"]+)"/gm)].map(match => match[1]);
    references.push(text.split('\n').find(line => line && !line.trim().startsWith('#')));
    assert.equal(references.length, 3);
    for (const url of references) {
      assert.equal(new URL(url).origin, aggregateOrigin);
      const child = await fetch(url);
      assert.equal(child.status, 200);
      assert.deepEqual([...new Uint8Array(await child.arrayBuffer())], [2, 4, 6, 8]);
    }
    const after = await readState();
    assert.equal(after.managementReads, before.managementReads, 'opaque free titles do not fetch management paths');
    assert.deepEqual(after.reads, ['/proxy/hls/list', '/proxy/hls/key?token=a,b', '/proxy/hls/init.mp4',
      '/proxy/hls/segment-1.ts?part=1'].map(url => ({ method: 'GET', url })));
    okay(await client.call('cat.script.enable', { installationId: 'script', enabled: false }));
    const revoked = await fetch(references[0]);
    assert.equal(revoked.status, 502);
    assert.deepEqual(await revoked.json(), { error: { code: 'STALE_SESSION' } });
    assert.equal((await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id })).code, 'STALE_SESSION');
  });

async function catManifestFixture(t, { document, format, contentType, encoding, assets, headers = {},
  path = `/proxy/${format}/list?sig=a%20b%2Fc~&token=a,b`, platform = 'test' }) {
  mkdirSync(root, { recursive: true });
  const directory = mkdtempSync(join(root, 'cat-fmf-'));
  const cat = createCatScriptModule({ dataRoot: join(directory, 'cat') }), modules = [cat];
  if (platform === 'ios') {
    const success = value => ({ ok: true, contract: 'tmdb.data.v1', value });
    modules.push({ id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {
      'tmdb.data.settings.get': async () => success({ revision: 'manifest-fixture' }),
      'tmdb.data.request': async ({ path }) => {
        assert.equal(path, '/3/movie/42');
        return success({ id: 42, title: movie.title, release_date: '2024-01-01', runtime: 3,
          genres: [], credits: { cast: [] }, similar: { results: [] }, images: { posters: [] } });
      },
    }, async close() {} });
  }
  connectFilmPlayback(modules, { dataRoot: directory });
  const identity = { sessionId: randomUUID() };
  const app = createAppRuntime({ ...identity, modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  let events;
  t.after(async () => {
    await events?.close(); await client.close(); await app.close();
    rmSync(directory, { recursive: true });
  });
  let workerOrigin;
  const transportReads = [], originalRequest = http.request;
  t.mock.method(http, 'request', function(url, ...args) {
    const request = Reflect.apply(originalRequest, this, [url, ...args]);
    if (typeof url === 'string' || url instanceof URL) {
      const address = new URL(url);
      if (address.origin === workerOrigin && address.pathname.startsWith('/proxy/')) {
        transportReads.push({ method: request.method, url: address.pathname + address.search,
          access: !!request.getHeader('x-meow-script-access') });
      }
    }
    return request;
  });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  const configuration = { document, format, contentType, encoding, assets, headers, path };
  const script = String.raw`
    const http = require('http'), zlib = require('zlib');
    const fixture = FIXTURE;
    let server, managementReads = 0;
    const reads = [];
    const json = (response, value) => {
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(value));
    };
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer((request, response) => {
        const path = request.url.split('?')[0];
        if (['/config', '/full-config', '/admin'].includes(path)) {
          managementReads++;
          return json(response, { video: { sites: [{ key: 'fixture', name: 'Fixture source',
            api: '/api/fixture', actions: ['home', 'search', 'detail', 'play'] }] } });
        }
        if (path === '/api/fixture/home') return json(response, { reads, managementReads });
        if (path === '/api/fixture/search') return json(response,
          { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film' }], pagecount: 1 });
        if (path === '/api/fixture/detail') return json(response, { list: [{ vod_id: 'fixture', vod_name: 'Fixture Film',
          vod_play_from: '原始线路', vod_play_url: 'Fixture Film.1080p$fixture' }] });
        if (path === '/api/fixture/play') return json(response,
          { parse: 0, url: 'http://127.0.0.1:9987' + fixture.path, format: fixture.format, headers: fixture.headers });
        reads.push({ method: request.method, url: request.url, range: request.headers.range || null,
          cookie: request.headers.cookie || null, authorization: request.headers.authorization || null,
          referer: request.headers.referer || null, origin: request.headers.origin || null,
          access: !!request.headers['x-meow-script-access'],
          scriptAccess: request.headers['x-meow-script-access'] || null,
          scope: request.headers['x-meow-request-scope'] || null });
        if (Object.entries(fixture.headers).some(([name, value]) => request.headers[name.toLowerCase()] !== value)) {
          response.writeHead(403); return response.end('missing-source-headers');
        }
        if (request.url === fixture.path) {
          const bytes = fixture.encoding === 'gzip' ? zlib.gzipSync(Buffer.from(fixture.document)) : Buffer.from(fixture.document);
          response.writeHead(200, { 'content-type': fixture.contentType, 'content-length': bytes.length,
            ...(fixture.encoding ? { 'content-encoding': fixture.encoding } : {}) });
          return response.end(bytes);
        }
        if (!Object.hasOwn(fixture.assets, request.url)) {
          response.writeHead(404); return response.end('wrong-resource');
        }
        const bytes = Buffer.from(fixture.assets[request.url]);
        const range = /^bytes=(\d+)-(\d+)$/.exec(request.headers.range || '');
        if (range) {
          const start = Number(range[1]), end = Number(range[2]);
          response.writeHead(206, { 'content-type': 'application/octet-stream',
            'content-range': 'bytes ' + start + '-' + end + '/' + bytes.length, 'content-length': end - start + 1 });
          return response.end(bytes.subarray(start, end + 1));
        }
        response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': bytes.length });
        response.end(bytes);
      });
      server.listen(9987, '127.0.0.1', resolve);
    });
    module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
  `.replace('FIXTURE', JSON.stringify(configuration));
  okay(await client.upload('cat', 'script', 1, Buffer.from(script)));
  okay(await client.call('cat.script.install', { installationId: 'script', sourceRevision: 1, enabled: true }));
  const status = okay(await client.call('cat.script.status', {})), state = status.instances[0];
  assert.equal(state.state, 'ready');
  workerOrigin = state.endpoints[0].origin;
  const readState = async () => okay(await client.call('cat.script.invoke', {
    installationId: 'script', generation: state.generation, siteId: 'fixture', operation: 'home',
  }));
  const before = await readState(), rpc = (operation, params) => client.call(`film.${operation}`, params);
  const target = platform === 'ios' ? { ...movie, id: 'tmdb:movie:42', metadataProvider: 'tmdb', metadataId: 42 } : movie;
  const id = okay(await rpc('session.open', { platform, target,
    sources: [{ ...source, generation: state.generation }] })).sessionId;
  events = await observation({ app, headers: () => ({
    'x-meow-request-id': randomUUID(),
  }) }, id, '', t.signal);
  let snapshot;
  do { snapshot = await events.next(); } while (snapshot.stage === 'loading');
  if (platform === 'test') {
    assert.equal(snapshot.stage, 'idle');
    okay(await rpc('session.command', { sessionId: id, action: 'prepare' }));
  }
  while (!snapshot.attempt && !snapshot.error) snapshot = await events.next();
  assert.ok(snapshot.attempt, JSON.stringify(snapshot));
  assert.equal(snapshot.attempt.prepareOnly, true);
  const receipt = okay(await rpc('session.attempt', { sessionId: id, attemptId: snapshot.attempt.id }));
  assert.deepEqual(await readState(), before, 'prepare must issue zero media reads and zero management requests');
  assert.deepEqual(transportReads, [], 'preparation must not issue a transport request');
  return { receipt, path, readState, before, client, rpc, id, cat, transportReads,
    aggregateOrigin: status.aggregate.origin, attemptId: snapshot.attempt.id };
}

test('real Cat Worker prepared HLS/DASH receipts validate original UTF-8 and remain cold until the first read',
  { timeout: 30000 }, async t => {
    const Decoder = globalThis.TextDecoder;
    t.mock.method(globalThis, 'TextDecoder', function(label, options) {
      if (options?.fatal) throw new TypeError('fatal decoding requires ICU');
      return new Decoder(label, options);
    });
    const title = '正片é\uFEFF\uFFFD🎬';
    const documents = [
      { format: 'hls', path: '/proxy/manifest.m3u8', contentType: 'application/vnd.apple.mpegurl',
        text: '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MAP:URI="init.mp4"\n' +
          `#EXTINF:3,${title}\nsegment.m4s\n#EXT-X-ENDLIST\n` },
      { format: 'dash', path: '/proxy/manifest.mpd', contentType: 'application/dash+xml',
        text: '<?xml version="1.0" encoding="UTF-8"?><MPD><ProgramInformation><Title>' + title +
          '</Title></ProgramInformation><Period><AdaptationSet contentType="video"><Representation id="v" bandwidth="1">' +
          '<SegmentList duration="3"><Initialization sourceURL="init.mp4"/><SegmentURL media="segment.m4s"/>' +
          '</SegmentList></Representation></AdaptationSet></Period></MPD>' },
    ];
    for (const document of documents) for (const invalid of [false, true]) {
      await t.test(`${document.format}: ${invalid ? 'invalid' : 'valid'} UTF-8`, async t => {
        const prefix = invalid ? document.format === 'hls' ? document.text + '#'
          : document.text.replace('</MPD>', '<!--') : document.text;
        const bytes = Buffer.concat([Buffer.from('\uFEFF' + prefix),
          ...(invalid ? [Buffer.from([0xc0, 0xaf]), Buffer.from(document.format === 'hls' ? '\n' : '--></MPD>')] : [])]);
        const assets = { '/proxy/init.mp4': [0, 1, 128, 255], '/proxy/segment.m4s': [2, 3, 129, 254] };
        const headers = { Cookie: 'film-fixture=private' };
        const f = await catManifestFixture(t, { ...document, document: [...bytes], assets, headers, platform: 'ios' });
        const before = okay(await f.rpc('session.snapshot', { sessionId: f.id }));
        assert.equal(before.stage, 'prepared');
        assert.equal(before.attempt.prepareOnly, true);
        assert.equal(before.current, null);
        assert.deepEqual(okay(await f.rpc('library.get')).history, []);
        assert.equal(f.receipt.format, document.format);
        assert.equal(new URL(f.receipt.url).origin + '/', f.receipt.mediaOrigin);
        const response = await fetch(f.receipt.url);
        const body = Buffer.from(await response.arrayBuffer()), text = body.toString('utf8');
        assert.equal(response.status, invalid ? 502 : 200);
        let references = [];
        if (invalid) assert.equal(text, 'MEDIA_MANIFEST_INVALID');
        else {
          assert.equal(response.headers.get('content-type'), document.contentType);
          assert.equal(body.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), false);
          assert.ok(text.includes(title));
          if (document.format === 'hls') references = [
            /\bURI="([^"]+)"/.exec(text)[1], ...text.split('\n').filter(line => line && !line.startsWith('#')),
          ];
          else {
            const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(text)[1];
            references = [...text.matchAll(/(?:sourceURL|media)="([^"]+)"/g)].map(match => new URL(match[1], base).href);
          }
          assert.equal(references.length, 2);
        }
        assert.deepEqual((await f.readState()).reads.map(read => read.url), [document.path],
          'only the requested root is read; preparation and parsing cannot fetch binary resources');
        for (const [index, reference] of references.entries()) {
          assert.equal(new URL(reference).origin, new URL(f.receipt.url).origin);
          const child = await fetch(reference);
          assert.equal(child.status, 200);
          assert.deepEqual([...new Uint8Array(await child.arrayBuffer())], Object.values(assets)[index]);
        }
        const finished = await f.readState();
        assert.deepEqual(finished.reads.map(read => read.url),
          [document.path, ...(invalid ? [] : Object.keys(assets))]);
        assert.ok(finished.reads.every(read => read.cookie === headers.Cookie));
        assert.equal(finished.managementReads, f.before.managementReads);
        assert.deepEqual(okay(await f.rpc('library.get')).history, []);
        const after = okay(await f.rpc('session.snapshot', { sessionId: f.id }));
        assert.equal(after.current, null);
        assert.equal(after.attempt.prepareOnly, true);
        okay(await f.rpc('session.release', { sessionId: f.id }));
        assert.equal((await fetch(f.receipt.url)).status, 404);
        assert.deepEqual(await f.readState(), finished, 'release must prevent another upstream request');
      });
    }
  });

test('real Cat Worker FMF01 identity, extensionless text and gzip HLS/MPD preserve the original media base',
  { timeout: 60000 }, async t => {
    for (const format of ['hls', 'dash']) for (const delivery of ['identity', 'extensionless', 'gzip']) {
      await t.test(`${format} ${delivery}`, async t => {
        const assetPaths = [
          '/proxy/assets/init.mp4?key=a%20b%2Fc~&token=a,b', '/proxy/assets/one.ts?part=1&sig=a%2Fb',
          '/proxy/absolute/init.mp4?key=a%20b%2Fc~&token=a,b', '/proxy/absolute/seg-3.m4s?part=2&sig=a%2Fb',
        ];
        const assets = Object.fromEntries(assetPaths.map((path, index) =>
          [path, [index + 1, 12 + index, 24 + index, 48 + index, 96 + index]]));
        const unlistedPath = '/proxy/assets/unregistered/config?url=/config&sig=a%2Fb';
        assets[unlistedPath] = [5, 15, 25, 35, 45];
        const document = format === 'hls'
          ? '#EXTM3U\n#EXT-X-MAP:URI="/proxy/assets/init.mp4?key=a%20b%2Fc~&token=a,b"\n' +
            '#EXT-X-KEY:METHOD=AES-128,URI="http://127.0.0.1:9987/proxy/absolute/init.mp4?key=a%20b%2Fc~&token=a,b"\n' +
            '#EXTINF:1,\n/proxy/assets/one.ts?part=1&sig=a%2Fb\n#EXTINF:1,\n' +
            'http://127.0.0.1:9987/proxy/absolute/seg-3.m4s?part=2&sig=a%2Fb\n#EXT-X-ENDLIST\n'
          : '<MPD><Period><AdaptationSet><Representation id="root"><BaseURL>/proxy/assets/</BaseURL>' +
            '<SegmentList><Initialization sourceURL="init.mp4?key=a%20b%2Fc~&amp;token=a,b"/>' +
            '<SegmentURL media="one.ts?part=1&amp;sig=a%2Fb"/></SegmentList></Representation>' +
            '<Representation id="absolute"><BaseURL>http://127.0.0.1:9987/proxy/absolute/</BaseURL>' +
            '<SegmentTemplate initialization="init.mp4?key=a%20b%2Fc~&amp;token=a,b" ' +
            'media="seg-$Number$.m4s?part=2&amp;sig=a%2Fb"/></Representation></AdaptationSet></Period></MPD>';
        const f = await catManifestFixture(t, { document, format, assets,
          contentType: delivery === 'identity' ? format === 'hls' ? 'application/vnd.apple.mpegurl' : 'application/dash+xml'
            : 'text/plain; charset=utf-8', encoding: delivery === 'extensionless' ? undefined : delivery });
        assert.equal(f.receipt.mediaOrigin, undefined, 'headerless manifests do not create a film stream route');
        assert.equal(new URL(f.receipt.url).origin, f.aggregateOrigin);
        const response = await fetch(f.receipt.url);
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('content-encoding'), null);
        const text = await response.text(), references = [];
        if (format === 'hls') {
          references.push(...[...text.matchAll(/\bURI="([^"]+)"/g)].map(match => match[1]));
          references.push(...text.split('\n').filter(line => line && !line.startsWith('#')));
        } else {
          for (const match of text.matchAll(/<Representation\b[^>]*>([^]*?)<\/Representation>/g)) {
            const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(match[1])[1];
            for (const reference of match[1].matchAll(/(?:sourceURL|initialization|media)="([^"]+)"/g)) {
              references.push(new URL(reference[1].replace(/&amp;/g, '&').replace('$Number$', '3'), base).href);
            }
          }
        }
        assert.equal(references.length, 4);
        assert.deepEqual((await f.readState()).reads.map(read => read.url), [f.path], 'manifest reads cannot prefetch children');
        const paths = format === 'hls' ? [assetPaths[0], assetPaths[2], assetPaths[1], assetPaths[3]] : assetPaths;
        for (const [index, reference] of references.entries()) {
          assert.equal(new URL(reference).origin, f.aggregateOrigin);
          const child = await fetch(reference, { headers: { Range: 'bytes=1-3' } });
          assert.equal(child.status, 206, paths[index]);
          assert.equal(child.headers.get('content-range'), 'bytes 1-3/5');
          assert.deepEqual([...new Uint8Array(await child.arrayBuffer())], assets[paths[index]].slice(1, 4));
        }
        const after = await f.readState();
        assert.equal(after.managementReads, f.before.managementReads);
        assert.deepEqual(after.reads.map(read => [read.url, read.range, read.access]),
          [[f.path, null, false], ...paths.map(path => [path, 'bytes=1-3', false])],
          'no access header is synthesized for a headerless descriptor');
        assert.deepEqual(f.transportReads, [f.path, ...paths].map(url => ({ method: 'GET', url, access: false })));
        const unlisted = await fetch(new URL('unregistered/config?url=/config&sig=a%2Fb', references[0]));
        assert.equal(unlisted.status, 200, 'a path absent from the manifest still reaches its original Worker base');
        assert.deepEqual([...new Uint8Array(await unlisted.arrayBuffer())], assets[unlistedPath]);
        const missingPath = '/proxy/assets/missing/config?sig=a%2Fb';
        const missing = await fetch(new URL('missing/config?sig=a%2Fb', references[0]));
        assert.equal(missing.status, 404);
        assert.equal(await missing.text(), 'wrong-resource');
        const finalState = await f.readState();
        assert.equal(finalState.managementReads, f.before.managementReads);
        assert.deepEqual(finalState.reads.map(read => [read.url, read.range, read.access]),
          [...after.reads.map(read => [read.url, read.range, read.access]), [unlistedPath, null, false],
            [missingPath, null, false]], 'a missing resource is a real Worker 404, not a zero-I/O permission refusal');
        assert.deepEqual(f.transportReads, [f.path, ...paths, unlistedPath, missingPath]
          .map(url => ({ method: 'GET', url, access: false })));
        okay(await f.client.call('cat.script.enable', { installationId: 'script', enabled: false }));
        for (const url of [f.receipt.url, references[0]]) {
          const expired = await fetch(url);
          assert.equal(expired.status, 502);
          assert.deepEqual(await expired.json(), { error: { code: 'STALE_SESSION' } });
        }
        assert.equal((await f.rpc('session.attempt', { sessionId: f.id, attemptId: f.attemptId })).code, 'STALE_SESSION');
      });
    }
  });

test('real Cat Worker FMF02 root and second-host absolute HLS variables preserve explicit source headers and Origin',
  { timeout: 30000 }, async t => {
    const externalReads = [], externalAssets = {
      '/assets/init.mp4?key=a%20b%2Fc~&token=a,b': [31, 32, 33],
      '/assets/one.ts?part=1&sig=a%2Fb': [41, 42, 43],
    };
    const external = http.createServer((request, response) => {
      externalReads.push({ url: request.url, headers: { ...request.headers } });
      if (!Object.hasOwn(externalAssets, request.url)) { response.writeHead(404); response.end(); return; }
      response.end(Buffer.from(externalAssets[request.url]));
    });
    await new Promise(resolve => external.listen(0, resolve));
    t.after(() => new Promise(resolve => { external.closeAllConnections(); external.close(resolve); }));
    const secondOrigin = `http://localhost:${external.address().port}`;
    const headers = { Cookie: 'synthetic-cat-cookie', Authorization: 'Bearer synthetic-cat-authorization',
      Referer: 'https://synthetic.invalid/player', Origin: 'https://synthetic.invalid/source-origin',
      'x-meow-script-access': 'synthetic-source-header' };
    const assets = { '/proxy/assets/init.mp4?key=a%20b%2Fc~&token=a,b': [11, 12, 13],
      '/proxy/assets/one.ts?part=1&sig=a%2Fb': [21, 22, 23] };
    const document = '#EXTM3U\n#EXT-X-DEFINE:NAME="root",VALUE="/proxy/assets"\n' +
      `#EXT-X-DEFINE:NAME="external",VALUE="${secondOrigin}/assets"\n` +
      '#EXT-X-MAP:URI="{$root}/init.mp4?key=a%20b%2Fc~&token=a,b"\n' +
      '#EXT-X-KEY:METHOD=AES-128,URI="{$external}/init.mp4?key=a%20b%2Fc~&token=a,b"\n' +
      '#EXTINF:1,\n{$root}/one.ts?part=1&sig=a%2Fb\n#EXTINF:1,\n{$external}/one.ts?part=1&sig=a%2Fb\n#EXT-X-ENDLIST\n';
    const f = await catManifestFixture(t, { document, format: 'hls', assets, headers,
      contentType: 'application/vnd.apple.mpegurl' });
    assert.equal(new URL(f.receipt.url).origin + '/', f.receipt.mediaOrigin);
    assert.notEqual(new URL(f.receipt.url).origin, f.aggregateOrigin, 'source headers select the film stream route');
    assert.notEqual(new URL(secondOrigin).hostname, new URL(f.aggregateOrigin).hostname);
    assert.deepEqual(externalReads, []);
    const response = await fetch(f.receipt.url);
    assert.equal(response.status, 200);
    const text = await response.text();
    assert.doesNotMatch(text, /\{\$/);
    const references = [...text.matchAll(/\bURI="([^"]+)"/g)].map(match => match[1]);
    references.push(...text.split('\n').filter(line => line && !line.startsWith('#')));
    assert.equal(references.length, 4);
    assert.deepEqual(externalReads, [], 'mapping external variables does not prefetch them');
    const expected = [Object.values(assets)[0], Object.values(externalAssets)[0],
      Object.values(assets)[1], Object.values(externalAssets)[1]];
    for (const [index, reference] of references.entries()) {
      assert.equal(new URL(reference).origin + '/', f.receipt.mediaOrigin);
      const child = await fetch(reference);
      assert.equal(child.status, 200);
      assert.deepEqual([...new Uint8Array(await child.arrayBuffer())], expected[index]);
    }
    const after = await f.readState();
    assert.equal(after.managementReads, f.before.managementReads);
    assert.deepEqual(after.reads.map(read => read.url), [f.path, ...Object.keys(assets)]);
    for (const read of after.reads) {
      assert.equal(read.cookie, headers.Cookie); assert.equal(read.authorization, headers.Authorization);
      assert.equal(read.referer, headers.Referer); assert.equal(read.origin, headers.Origin);
      assert.equal(read.access, true, 'an explicitly supplied access-named header is ordinary source input');
      assert.equal(read.scriptAccess, headers['x-meow-script-access']);
      assert.equal(read.scope, null, 'internal scope is not exposed to the guest');
    }
    assert.deepEqual(f.transportReads, [f.path, ...Object.keys(assets)].map(url => ({ method: 'GET', url, access: true })));
    assert.deepEqual(externalReads.map(read => read.url), Object.keys(externalAssets));
    for (const read of externalReads) {
      for (const [name, value] of Object.entries(headers)) assert.equal(read.headers[name.toLowerCase()], value, name);
      for (const name of ['x-meow-request-scope', 'x-meow-runtime-access']) {
        assert.equal(read.headers[name], undefined, name);
      }
    }
    const finalExternalReads = structuredClone(externalReads), finalTransportReads = structuredClone(f.transportReads);
    okay(await f.client.call('cat.script.enable', { installationId: 'script', enabled: false }));
    for (const url of [f.receipt.url, ...references]) {
      const expired = await fetch(url);
      assert.equal(expired.status, 404);
      assert.equal(await expired.text(), 'MEDIA_NOT_FOUND');
    }
    assert.deepEqual(externalReads, finalExternalReads, 'expired film routes do not read external resources');
    assert.deepEqual(f.transportReads, finalTransportReads, 'expired film routes do not read the Worker');
    assert.equal((await f.rpc('session.attempt', { sessionId: f.id, attemptId: f.attemptId })).code, 'STALE_SESSION');
  });
