import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { connectFilmPlayback, createAppRuntime, createRuntimeClient } from '../apps/runtime/entry.mjs';

const source = { key: 'cache-fixture', engine: 'cat', installationId: 'script',
  siteId: 'fixture', generation: 1, name: 'Cache fixture' };
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
async function setup(t, dataRoot) {
  let failure = false, revision = 1;
  const calls = [];
  const module = (id, contract, methods) => ({
    id, contract, version: id === 'cat' ? '0.2.0' : '0.1.0', methods,
    ...(id === 'cat' ? { upload() { assert.fail('unexpected upload'); } } : {}), async close() {},
  });
  const success = (contract, value) => ({ ok: true, contract, value });
  const modules = [
    module('cat', 'cat.script.v2', {
      'cat.script.status': async () => success('cat.script.v2', { instances: [
        { installationId: 'script', generation: 1, state: 'ready', sites: [{ key: 'fixture' }] },
      ] }),
      'cat.script.invoke': async input => {
        calls.push({ operation: input.operation, payload: input.payload });
        return success('cat.script.v2', { list: [{ vod_id: revision, vod_name: `目录 ${revision}` }] });
      },
    }),
    module('douban', 'douban.data.v1', {
      'douban.data.settings.get': async () => success('douban.data.v1', { revision: 'stable-fixture' }),
      'douban.data.request': async input => {
        calls.push(input);
        if (failure) return { ok: false, contract: 'douban.data.v1', code: 'NETWORK_ERROR' };
        return success('douban.data.v1', { items: revision === 0 ? [] : [{ id: revision, title: `影片 ${revision}` }] });
      },
    }),
  ];
  connectFilmPlayback(modules, { dataRoot });
  const app = createAppRuntime({ sessionId: randomUUID(), modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  const close = async () => { await client.close(); await app.close(); };
  t.after(close);
  return { calls, close, rpc: (operation, input) => client.call(`film.${operation}`, input),
    setFailure: value => { failure = value; }, setRevision: value => { revision = value; } };
}
function directory(t) {
  const path = mkdtempSync(join(tmpdir(), 'film-cache-http-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
}

test('cache HTTP catalog keeps raw output and refreshes only the exact process-owned request', async t => {
  const f = await setup(t, directory(t));
  const home = { source, action: 'home', payload: {} };
  const first = okay(await f.rpc('data.catalog', home));
  f.setRevision(2);
  assert.deepEqual(okay(await f.rpc('data.catalog', home)), first);
  assert.equal(f.calls.length, 1);
  const category = { source, action: 'category', payload: { tid: 'movie', pg: 1 } };
  assert.equal(okay(await f.rpc('data.catalog', category)).list[0].vod_id, 2);
  assert.equal(okay(await f.rpc('data.catalog', { ...home, refresh: true })).list[0].vod_id, 2);
  assert.deepEqual(f.calls.map(row => row.operation), ['home', 'category', 'home']);
  assert.equal(okay(await f.rpc('data.catalog', category)).list[0].vod_id, 2);
  assert.equal(f.calls.length, 3);
  assert.equal((await f.rpc('data.catalog', { ...home, refresh: 'true' })).code, 'INVALID_REQUEST');
});

test('cache HTTP metadata entry preserves stale success, rejects stale handoff and replaces valid empty results', async t => {
  const f = await setup(t, directory(t));
  const input = { provider: 'douban', path: '/rexxar/api/v2/subject/recent_hot/movie',
    query: { start: 0, limit: 12, category: '热门', type: '全部' } };
  const first = okay(await f.rpc('data.metadata.entry', input));
  assert.equal(first.cache.status, 'fresh');
  assert.deepEqual(okay(await f.rpc('data.metadata', input)), first.value);
  assert.equal(f.calls.length, 1);
  f.setFailure(true);
  const stale = okay(await f.rpc('data.metadata.entry', { ...input, refresh: true }));
  assert.equal(stale.cache.status, 'stale');
  assert.equal(stale.cache.error.code, 'NETWORK_ERROR');
  assert.equal(stale.fetchedAt, first.fetchedAt);
  assert.deepEqual(stale.value, first.value);
  assert.equal(okay(await f.rpc('data.cache.entry', { ...input, kind: 'metadata' })), null);
  assert.equal((await f.rpc('data.metadata', input)).code, 'NETWORK_ERROR');
  f.setFailure(false); f.setRevision(0);
  const updated = okay(await f.rpc('data.metadata.entry', { ...input, refresh: true }));
  assert.equal(updated.cache.status, 'fresh');
  assert.deepEqual(updated.value.items, []);
  assert.equal(updated.cache.components.response.status, 'empty');
});

test('cache HTTP runtime recreation reuses durable metadata but never the previous process catalog', async t => {
  const root = directory(t), f = await setup(t, root);
  const input = { provider: 'douban', path: '/rexxar/api/v2/subject/recent_hot/movie', query: { start: 0, limit: 12 } };
  const first = okay(await f.rpc('data.metadata.entry', input));
  const home = { source, action: 'home' };
  await f.rpc('data.catalog', home);
  await f.close();
  const next = await setup(t, root);
  next.setFailure(true); next.setRevision(2);
  assert.deepEqual(okay(await next.rpc('data.metadata.entry', input)), first);
  assert.equal(next.calls.length, 0);
  assert.equal(okay(await next.rpc('data.catalog', home)).list[0].vod_id, 2);
  assert.deepEqual(next.calls.map(row => row.operation), ['home']);
});

test('cache management clears providers independently or together without removing settings or playback records', async t => {
  const { createTMDBDataModule, createDoubanDataModule } = await import('../apps/runtime/modules/metadata-data.mjs');
  const root = directory(t), calls = { tmdb: 0, douban: 0 };
  const modules = ['tmdb', 'douban'].map(provider => (provider === 'tmdb' ? createTMDBDataModule : createDoubanDataModule)({
    dataRoot: root,
    request: async () => {
      calls[provider]++;
      return { status: 200, headers: { 'content-type': 'application/json' },
        body: Buffer.from(JSON.stringify(provider === 'tmdb' ? { results: [] } : { items: [] })) };
    },
  }));
  connectFilmPlayback(modules, { dataRoot: root });
  const app = createAppRuntime({ sessionId: randomUUID(), modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  t.after(async () => { await client.close(); await app.close(); });
  const rpc = async (operation, input = {}) => okay(await client.call(operation, input));
  const tmdb = await rpc('tmdb.data.settings.update', { apiKey: 'synthetic-key', language: 'en-US' });
  const douban = await rpc('douban.data.settings.update', { cookie: 'synthetic=cookie' });
  const preferences = await rpc('film.settings.get');
  await rpc('film.manual.update', { action: 'save', item: {
    id: 'manual-fixture', targetId: 'tmdb:movie:1', kind: 'site', sourceKey: 'site-fixture', detailId: 0,
    title: 'Film', enabled: true, autoDisable: false, failureCount: 0,
  } });
  const library = await rpc('film.library.get');
  const inputs = {
    tmdb: { provider: 'tmdb', path: '/3/search/multi', query: { query: 'Film' } },
    douban: { provider: 'douban', path: '/rexxar/api/v2/subject/recent_hot/movie', query: {} },
  };
  const read = () => Promise.all(Object.values(inputs).map(input => rpc('film.data.metadata', input)));
  await read(); await read(); assert.deepEqual(calls, { tmdb: 1, douban: 1 });
  await rpc('film.data.invalidate', { domain: 'tmdb' });
  await read(); assert.deepEqual(calls, { tmdb: 2, douban: 1 });
  await rpc('film.data.invalidate', { domain: 'douban' });
  await read(); assert.deepEqual(calls, { tmdb: 2, douban: 2 });
  await Promise.all(['tmdb', 'douban'].map(domain => rpc('film.data.invalidate', { domain })));
  await read(); assert.deepEqual(calls, { tmdb: 3, douban: 3 });
  assert.deepEqual(await rpc('tmdb.data.settings.get'), tmdb);
  assert.deepEqual(await rpc('douban.data.settings.get'), douban);
  assert.deepEqual(await rpc('film.settings.get'), preferences);
  assert.deepEqual(await rpc('film.library.get'), library);
});
