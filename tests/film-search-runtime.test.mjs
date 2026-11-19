import './storage-test-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { connectFilmPlayback, createAppRuntime, createRuntimeClient } from '../apps/runtime/entry.mjs';

const source = { key: 'a', engine: 'cat', installationId: 'script', siteId: 'fixture',
  generation: 1, name: '甲源', enabled: true, searchable: true };
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
async function until(read, description = 'public state') {
  const end = Date.now() + 10_000;
  do {
    const value = await read();
    if (value) return value;
    await tick();
  } while (Date.now() < end);
  assert.fail(`Timed out waiting for ${description}`);
}

async function setup(t, { invoke, metadata } = {}) {
  const area = new URL('../tmp/test-results/film-rules-unification/', import.meta.url);
  mkdirSync(area, { recursive: true });
  const directory = mkdtempSync(new URL('http-', area));
  const calls = [], metadataCalls = [];
  const success = value => ({ ok: true, contract: 'cat.script.v2', value });
  let generation = 1;
  const cat = { id: 'cat', version: '0.2.0', contract: 'cat.script.v2',
    upload() { assert.fail('unexpected upload'); }, async close() {},
    methods: {
      'cat.script.status': async () => success({ instances: [{
        installationId: 'script', generation, state: 'ready', enabled: true,
        sites: [{ key: 'fixture', name: '甲源' }],
      }] }),
      'cat.script.edit': async () => { generation++; return success({ changed: true }); },
      'cat.script.invoke': async (input, context) => {
        calls.push(structuredClone(input));
        const replacement = await invoke?.(input, context);
        if (replacement !== undefined) return success(replacement);
        if (input.operation === 'search') return success({ list: [
          { vod_id: 0, vod_name: '星河', displayKey: 'group:来源自己的字段', extension: { raw: '原文' } },
          { vod_id: '0', vod_name: '星河' },
        ], pagecount: 1 });
        if (input.operation === 'detail') return success({ list: [{
          vod_id: input.payload.id, vod_name: '星河', vod_play_from: '原始线路',
          vod_play_url: '第1集$one#第2集$two',
        }] });
        assert.fail(`Unexpected upstream request: ${input.operation}`);
      },
    },
  };
  const modules = [cat];
  if (metadata) {
    const success = value => ({ ok: true, contract: 'tmdb.data.v1', value });
    modules.push({ id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {
      'tmdb.data.settings.get': async () => success({ revision: 'selection-fixture' }),
      'tmdb.data.request': async (input, context) => {
        metadataCalls.push(structuredClone(input));
        return success(await metadata(input, context));
      },
    }, async close() {} });
  }
  connectFilmPlayback(modules, { dataRoot: join(directory, 'data') });
  const app = createAppRuntime({ sessionId: randomUUID(), modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  const rpc = async (operation, input = {}) => okay(await client.call(`film.${operation}`, input));
  const snapshot = id => rpc('search.snapshot', { sessionId: id });
  const complete = id => until(async () => {
    const value = await snapshot(id);
    return !value.loading && value;
  }, 'search completion');
  t.after(async () => {
    await client.close(); await app.close();
    rmSync(directory, { recursive: true });
  });
  return { rpc, app, client, calls, metadataCalls, snapshot, complete };
}

test('HTTP search returns both views, preserves original provider fields and navigates through Node facts', async t => {
  const f = await setup(t);
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  const value = await f.complete(opened.sessionId);
  assert.equal(value.cards.length, 1);
  assert.equal(value.rawCards.length, 2);
  assert.equal(value.metadataState, 'unavailable');
  assert.equal(value.rawCards[0].raw.displayKey, 'group:来源自己的字段');
  assert.deepEqual(value.cards[0].aggregateItems.map(row => row.detailId), [0, '0']);
  assert.deepEqual(f.calls.map(row => row.operation), ['search']);
  const handoff = await f.rpc('search.handoff', { sessionId: opened.sessionId,
    selectionId: value.cards[0].displayKey, sources: [source] });
  assert.deepEqual(handoff.selectionMembers, [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }]);
  const playback = await f.rpc('session.open', {
    platform: 'test', target: { id: 'client-display', kind: 'source', title: '不能替换原查询',
      source: { key: 'a', detailId: 'wrong' } },
    sources: [source], query: '客户端不能重造查询',
    results: [{ sourceKey: 'a', items: [{ id: 'wrong', title: 'wrong' }] }],
    searchReference: handoff.searchReference,
  });
  assert.equal(playback.target.id, handoff.target.id);
  assert.deepEqual(playback.target.members.map(member => member.detailId), [0, '0']);
  assert.deepEqual(playback.sources.map(row => row.source.detailId), [0, '0']);
  assert.equal(playback.rulesRevision, value.rulesRevision);
  await until(async () => (await f.rpc('session.snapshot', { sessionId: playback.sessionId })).stage !== 'loading');
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1);
  const raw = await f.rpc('search.handoff', { sessionId: opened.sessionId,
    selectionId: value.rawCards.find(row => row.detailId === '0').displayKey, sources: [source] });
  const single = await f.rpc('session.open', { platform: 'test', ...raw });
  assert.equal(single.target.source.detailId, '0');
  assert.deepEqual(single.sources.map(row => row.source.detailId), ['0']);
  assert.equal(single.metadata, null);
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1);
  assert.equal(f.calls.some(row => row.operation === 'play'), false);
  await f.rpc('session.release', { sessionId: playback.sessionId });
  await f.rpc('session.release', { sessionId: single.sessionId });
});

test('stable cards reopen after their original search session is released without another search or playback', async t => {
  const f = await setup(t);
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  const value = await f.complete(opened.sessionId), card = value.rawCards[0];
  assert.equal(card.businessId, card.playbackTarget.id);
  await f.rpc('search.release', { sessionId: opened.sessionId });
  const reopened = await f.rpc('session.open', { platform: 'windows', restoreOnly: true,
    target: card.playbackTarget, sources: value.sources,
    searchReference: { sessionId: opened.sessionId, selectionId: card.displayKey } });
  const ready = await until(async () => {
    const current = await f.rpc('session.snapshot', { sessionId: reopened.sessionId });
    return current.stage !== 'loading' && !current.browsing.loading && current;
  });
  assert.equal(ready.target.id, card.businessId);
  assert.equal(ready.browsing.episodes.length, 2);
  assert.equal(ready.sources.length, 1);
  assert.equal(ready.browsing.episodes[0].businessId, ready.browsing.episodes[0].candidateId);
  assert.equal(ready.browsing.episodes[0].playbackTarget.source.detailId, 0);
  assert.deepEqual(f.calls.map(row => row.operation), ['search', 'detail']);
  assert.deepEqual((await f.rpc('library.get')).history, []);
});

test('restore-only reading restores the selected source line without starting an intelligent match or exact address', async t => {
  const f = await setup(t, { invoke: async input => input.operation === 'detail' ? { list: [{
    vod_id: input.payload.id, vod_name: '星河', vod_play_from: '主线$$$副线',
    vod_play_url: '第1集$one$$$第2集$two#第3集$three',
  }] } : undefined });
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  const value = await f.complete(opened.sessionId), card = value.rawCards[0];
  const request = { platform: 'windows', restoreOnly: true, target: card.playbackTarget, sources: value.sources };
  const first = await f.rpc('session.open', request);
  const ready = await until(async () => {
    const state = await f.rpc('session.snapshot', { sessionId: first.sessionId });
    return !state.browsing.loading && state.browsing.lines.length && state;
  });
  const selection = { sourceId: ready.browsing.sourceId, lineId: ready.browsing.lines[1].id };
  await f.rpc('session.release', { sessionId: first.sessionId });
  const reopened = await f.rpc('session.open', { ...request, browsing: selection });
  const restored = await until(async () => {
    const state = await f.rpc('session.snapshot', { sessionId: reopened.sessionId });
    return state.stage !== 'loading' && !state.browsing.loading && state;
  });
  assert.equal(restored.browsing.sourceId, selection.sourceId);
  assert.equal(restored.browsing.lineId, selection.lineId);
  assert.equal(restored.browsing.episodes.length, 2);
  assert.deepEqual(f.calls.map(row => row.operation), ['search', 'detail']);
});

test('restore-only metadata reading restores the selected season and never schedules source search or media', async t => {
  const f = await setup(t, { metadata: async input => {
    if (input.path === '/3/tv/42') return { id: 42, name: '星河', genres: [],
      seasons: [1, 2].map(season_number => ({ season_number, episode_count: 2 })) };
    const season = Number(input.path.match(/\/season\/(\d+)$/)?.[1]);
    assert.ok(season === 1 || season === 2, input.path);
    return { id: season, name: `第${season}季`, season_number: season,
      episodes: [1, 2].map(episode_number => ({ name: `第${episode_number}集`,
        season_number: season, episode_number })) };
  } });
  const request = { platform: 'windows', restoreOnly: true, sources: [source],
    target: { id: 'tmdb:tv:42', kind: 'tv', title: '星河', metadataProvider: 'tmdb', metadataId: '42' } };
  const first = await f.rpc('session.open', request);
  const ready = await until(async () => {
    const state = await f.rpc('session.snapshot', { sessionId: first.sessionId });
    return state.stage !== 'loading' && !state.browsing.loading && state;
  });
  await f.rpc('session.release', { sessionId: first.sessionId });
  const reopened = await f.rpc('session.open', { ...request, browsing: {
    metadataSourceId: ready.metadataSourceId, sourceId: null, season: 2,
  } });
  const restored = await until(async () => {
    const state = await f.rpc('session.snapshot', { sessionId: reopened.sessionId });
    return state.stage !== 'loading' && !state.browsing.loading && state;
  });
  assert.equal(restored.metadataSourceId, ready.metadataSourceId);
  assert.equal(restored.browsing.season, 2);
  assert.deepEqual(restored.episodes.map(row => row.businessId),
    ['tmdb:tv:42:season:2:episode:1', 'tmdb:tv:42:season:2:episode:2']);
  assert.deepEqual(f.calls, []);
  assert.deepEqual((await f.rpc('library.get')).history, []);
});

for (const platform of ['windows', 'ios']) {
  test(`restore-only ${platform} late metadata keeps the loaded source directory and never auto-plays`, async t => {
    const gate = deferred();
    t.after(() => gate.resolve({ results: [] }));
    const f = await setup(t, { metadata: ({ path }) => {
      if (path === '/3/search/multi') return gate.promise;
      assert.equal(path, '/3/movie/12');
      return { id: 12, title: '星河', release_date: '2020-01-01', genres: [],
        credits: { cast: [], crew: [] }, similar: { results: [] }, images: { posters: [], backdrops: [] } };
    } });
    const search = await f.rpc('search.open', { query: '星河', sources: [source] });
    const early = await until(async () => {
      const state = await f.snapshot(search.sessionId);
      return state.cards.length && state;
    });
    const reference = { sessionId: search.sessionId, selectionId: early.cards[0].displayKey };
    const opened = await f.rpc('session.open', { platform, restoreOnly: true,
      target: early.cards[0].playbackTarget, sources: early.sources, searchReference: reference });
    const loaded = await until(async () => {
      const state = await f.rpc('session.snapshot', { sessionId: opened.sessionId });
      return !state.browsing.loading && state.stage === 'idle' && state;
    });
    gate.resolve({ results: [{ id: 12, media_type: 'movie', title: '星河' }] });
    await f.complete(search.sessionId);
    await f.rpc('session.command', { sessionId: opened.sessionId, action: 'search.update',
      searchReference: reference, sources: early.sources });
    const updated = await until(async () => {
      const state = await f.rpc('session.snapshot', { sessionId: opened.sessionId });
      return state.metadata?.id === 12 && state;
    });
    assert.equal(updated.target.id, loaded.target.id);
    assert.equal(updated.target.kind, 'group');
    assert.deepEqual(updated.browsing, loaded.browsing);
    assert.equal(updated.stage, 'idle');
    assert.equal(updated.attempt, null);
    assert.equal(updated.current, null);
    assert.deepEqual(f.calls.map(row => row.operation), ['search', 'detail']);
    await f.rpc('session.release', { sessionId: opened.sessionId });
  });
}

test('HTTP default and raw single-card handoffs open and update only the selected typed detail, not the search page', async t => {
  const selected = { id: 0, name: '星河' }, other = { id: '0', name: '别片' };
  const f = await setup(t, { invoke: input => input.operation === 'search'
    ? { list: [selected, other], pagecount: 1 } : undefined });
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  const value = await f.complete(opened.sessionId);
  assert.equal(value.siteItems.length, 2);
  for (const cards of [value.cards, value.rawCards]) {
    const card = cards.find(row => row.detailId === 0);
    const handoff = await f.rpc('search.handoff', { sessionId: opened.sessionId,
      selectionId: card.displayKey, sources: [source] });
    assert.equal(handoff.target.id, card.businessId);
    assert.equal(handoff.target.metadataProvider, undefined);
    assert.deepEqual(handoff.selectionMembers, [{ sourceKey: 'a', detailId: 0 }]);
    assert.deepEqual(handoff.results, [{ sourceKey: 'a', items: [selected] }]);
    assert.deepEqual(handoff.search, [{ sourceKey: 'a', page: 1, completed: true }]);
    const playback = await f.rpc('session.open', { platform: 'test', ...handoff,
      results: [{ sourceKey: 'a', items: [other] }] });
    const ready = await until(async () => {
      const state = await f.rpc('session.snapshot', { sessionId: playback.sessionId });
      return state.stage === 'idle' && state;
    });
    assert.equal(ready.target.id, card.businessId);
    assert.deepEqual(ready.sources.map(row => row.source.detailId), [0]);
    await f.rpc('session.command', { sessionId: playback.sessionId, action: 'browse', sourceId: ready.sources[0].id });
    const browsing = await until(async () => {
      const state = await f.rpc('session.snapshot', { sessionId: playback.sessionId });
      return !state.browsing.loading && state.browsing.episodes.length === 2 && state;
    });
    assert.equal(browsing.browsing.sourceId, ready.sources[0].id);
    assert.equal(browsing.current, null);
    assert.equal(browsing.attempt, null);
    const calls = structuredClone(f.calls);
    const updated = await f.rpc('session.command', { sessionId: playback.sessionId,
      action: 'search.update', sources: handoff.sources, searchReference: handoff.searchReference,
      results: [{ sourceKey: 'a', items: [other] }] });
    assert.deepEqual(updated.sources.map(row => row.source.detailId), [0]);
    assert.equal(updated.browsing.sourceId, ready.sources[0].id);
    assert.deepEqual(f.calls, calls);
    await f.rpc('session.release', { sessionId: playback.sessionId });
  }
  assert.deepEqual(f.calls.filter(row => row.operation === 'search').map(row => row.payload.page), [1]);
  assert.deepEqual(f.calls.filter(row => row.operation === 'detail').map(row => row.payload.id), [0]);
  assert.equal(f.calls.some(row => row.operation === 'play'), false);
});

test('HTTP transports derived lone UTF16 losslessly while raw titles and typed IDs remain unchanged', async t => {
  const title = '𠀀星河';
  const f = await setup(t, { invoke: input => input.operation === 'search'
    ? { list: [{ id: 0, name: title }, { id: '0', name: title }], pagecount: 1 } : undefined });
  const initial = await f.rpc('rules.get');
  const rules = { ...initial.rules, aggregateRegexRules: ['/\\uD840/g'] };
  const saved = await f.rpc('rules.set', { rules });
  const preview = await f.rpc('rules.preview', { rules, request: { section: 'search', text: title } });
  const opened = await f.rpc('search.open', { query: title, sources: [source] });
  const value = await f.complete(opened.sessionId);
  assert.equal(value.rulesRevision, saved.revision);
  assert.deepEqual(value.cards[0].groupKey, [0xdc00, ...Array.from('星河', character => character.charCodeAt(0))]);
  assert.deepEqual(value.cards[0].groupKey, preview.groupKey);
  assert.equal(typeof value.cards[0].displayKey, 'string');
  assert.ok(value.cards[0].displayKey.includes('\\udc00'));
  assert.equal(value.cards[0].aggregateItems[0].raw.name, title);
  assert.deepEqual(value.rawCards.map(card => card.detailId), [0, '0']);
  const handoff = await f.rpc('search.handoff', { sessionId: opened.sessionId,
    selectionId: value.cards[0].displayKey, sources: [source] });
  assert.equal(handoff.target.title, title);
  assert.deepEqual(handoff.target.members.map(member => member.detailId), [0, '0']);
  assert.deepEqual(handoff.results[0].items.map(row => row.id), [0, '0']);
});

test('saved rules affect only a new HTTP search; old search handoff keeps its fixed recognition snapshot', async t => {
  const f = await setup(t);
  const old = await f.rpc('search.open', { query: '星河', sources: [source] });
  const first = await f.complete(old.sessionId);
  const previous = await f.rpc('rules.get');
  const saved = await f.rpc('rules.set', {
    rules: { ...previous.rules, aggregateRegexRules: ['河'] },
  });
  assert.notEqual(saved.revision, previous.revision);
  const fresh = await f.rpc('search.open', { query: '星河', sources: [source] });
  const second = await f.complete(fresh.sessionId);
  assert.equal(second.rulesRevision, saved.revision);
  assert.notEqual(first.cards[0].groupKey, second.cards[0].groupKey);
  assert.equal((await f.snapshot(old.sessionId)).rulesRevision, first.rulesRevision);
  const handoff = await f.rpc('search.handoff', { sessionId: old.sessionId,
    selectionId: first.cards[0].displayKey, sources: [source] });
  const playback = await f.rpc('session.open', { platform: 'test', ...handoff });
  assert.equal(playback.rulesRevision, first.rulesRevision);
  const current = { ...playback.target, season: 2, episode: 3, globalEpisode: 15,
    source: { key: 'a', detailId: '0', lineId: '已选线路', playId: '已选文件' } };
  await f.rpc('session.release', { sessionId: playback.sessionId });
  const reentered = await f.rpc('session.open', { platform: 'test', ...handoff, resumeTarget: current });
  assert.equal(reentered.target.season, 2);
  assert.equal(reentered.target.episode, 3);
  assert.equal(reentered.target.globalEpisode, 15);
  assert.equal(reentered.target.source.detailId, '0');
  assert.equal(reentered.target.source.lineId, '已选线路');
  assert.equal(reentered.rulesRevision, first.rulesRevision);
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1,
    'a new rules revision changes projections, not the valid raw response cache');
});

test('HTTP stop leaves an accepted network producer alive for cache reuse and starts no follow-up work', async t => {
  const started = deferred(), finish = deferred();
  let networkSignal;
  const f = await setup(t, { invoke: async (input, context) => {
    if (input.operation !== 'search') return;
    networkSignal = context.signal;
    started.resolve();
    return finish.promise;
  } });
  const first = await f.rpc('search.open', { query: '星河', sources: [source] });
  await started.promise;
  const stopped = await f.rpc('search.command', { sessionId: first.sessionId, action: 'stop' });
  assert.equal(stopped.siteState, 'cancelled');
  assert.equal(networkSignal.aborted, false);
  const second = await f.rpc('search.open', { query: '星河', sources: [source] });
  finish.resolve({ list: [{ id: 'shared', name: '星河' }], pagecount: 1 });
  const completed = await f.complete(second.sessionId);
  assert.equal(completed.cards.length, 1);
  assert.deepEqual((await f.snapshot(first.sessionId)).cards, []);
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1);
  await f.rpc('search.command', { sessionId: first.sessionId, action: 'resume' });
  assert.equal((await f.complete(first.sessionId)).cards.length, 1);
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1);
});

test('HTTP SSE reconnect gives the current search snapshot and stream cancellation does not stop the owner', async t => {
  const finish = deferred();
  const f = await setup(t, { invoke: input => input.operation === 'search' ? finish.promise : undefined });
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  async function observe(after = '') {
    const response = await fetch(`${f.app.baseUrl}/v1/film/events/${opened.sessionId}${after}`, {
      headers: { 'x-meow-request-id': randomUUID() },
    });
    assert.equal(response.status, 200);
    const reader = response.body.getReader();
    const text = new TextDecoder().decode((await reader.read()).value);
    await reader.cancel();
    const event = text.split('\n').find(line => line.startsWith('data: '));
    assert.ok(event);
    return JSON.parse(event.slice(6));
  }
  const first = await observe();
  assert.equal(first.loading, true);
  finish.resolve({ list: [{ id: 0, name: '星河' }], pagecount: 1 });
  const completed = await f.complete(opened.sessionId);
  const replay = await observe(`?after=${completed.sequence}`);
  assert.equal(replay.sequence, completed.sequence);
  assert.equal(replay.cards[0].title, '星河');
  assert.equal(f.calls.filter(row => row.operation === 'search').length, 1);
});

test('HTTP rule resource failure stays distinct and the same owner recovers without changing saved rules', async t => {
  const f = await setup(t);
  const saved = await f.rpc('rules.get');
  const failure = await f.client.call('film.rules.preview', {
    rules: { ...saved.rules, episodeCleanRegexRules: [],
      movieRules: [{ pattern: '(a+)', replace: '$1'.repeat(1024), flags: '' }] },
    request: { section: 'movie', text: 'a'.repeat(100_000) },
  });
  assert.deepEqual(failure, { ok: false, contract: 'film.playback.v1', code: 'RESOURCE_LIMIT' });
  assert.deepEqual(await f.rpc('rules.get'), saved);
  const result = await f.rpc('rules.preview', { request: { section: 'episode', text: 'E5' } });
  assert.equal(result.rewrittenText, 'E5');
});

test('HTTP late group members come from the Node selection and do not reschedule content or replace its target', async t => {
  const f = await setup(t, { invoke: input => input.operation === 'search'
    ? { list: [input.payload.page === 1
      ? { id: 0, name: '星河 第1季' } : { id: '0', name: '星河 第2季' }], pagecount: 2 }
    : undefined });
  const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
  const first = await f.complete(opened.sessionId);
  const handoff = await f.rpc('search.handoff', { sessionId: opened.sessionId,
    selectionId: first.cards[0].displayKey, sources: [source] });
  const playback = await f.rpc('session.open', { platform: 'test', ...handoff });
  await until(async () => (await f.rpc('session.snapshot', { sessionId: playback.sessionId })).stage === 'idle');
  assert.deepEqual(playback.target.members, [{ sourceKey: 'a', detailId: 0 }]);
  await f.rpc('search.command', { sessionId: opened.sessionId, action: 'more' });
  const next = await f.complete(opened.sessionId);
  assert.equal(next.cards.length, 1);
  assert.deepEqual(next.cards[0].aggregateItems.map(row => row.detailId), [0, '0']);
  const before = structuredClone(f.calls);
  const updated = await f.rpc('session.command', {
    sessionId: playback.sessionId, action: 'search.update', sources: [source],
    searchReference: handoff.searchReference,
    target: { ...playback.target, members: [{ sourceKey: 'a', detailId: 'forged' }] },
  });
  assert.deepEqual(updated.target, { ...playback.target,
    members: [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }] });
  assert.equal(updated.current, null);
  assert.equal(updated.attempt, null);
  assert.deepEqual(f.calls, before);
  await f.rpc('session.release', { sessionId: playback.sessionId });
});

for (const output of ['pending', 'current']) {
  test(`HTTP late TMDB carries exact cross-season members without trusting client claims or replacing ${output}`, async t => {
    const metadataGate = deferred();
    t.after(() => metadataGate.resolve({ results: [] }));
    const first = { id: 0, name: '星河 第1季' };
    const later = { id: '0', name: '星河 第2季' };
    const other = { id: 'not-selected', name: '别片', aka: ['星河 第1季'] };
    const metadata = { id: 12, name: '星河', first_air_date: '2020-01-01', status: 'Ended', genres: [],
      number_of_seasons: 2, number_of_episodes: 2,
      seasons: [{ season_number: 1, episode_count: 1 }, { season_number: 2, episode_count: 1 }],
      credits: { cast: [], crew: [] }, similar: { results: [] }, images: { posters: [], backdrops: [] } };
    const f = await setup(t, {
      invoke: input => {
        if (input.operation === 'search') return {
          list: input.payload.page === 1 ? [first] : [later, other], pagecount: 2,
        };
        if (input.operation === 'detail') return { list: [{
          vod_id: input.payload.id, vod_name: first.name, vod_play_from: '原始线路',
          vod_play_url: '星河.S01E01.1080p$one',
        }] };
        if (input.operation === 'play') return { url: 'http://127.0.0.1:9/selection.mp4' };
      },
      metadata: ({ path }) => {
        if (path === '/3/search/multi') return metadataGate.promise;
        assert.equal(path, '/3/tv/12');
        return metadata;
      },
    });
    const opened = await f.rpc('search.open', { query: '星河', sources: [source] });
    const early = await until(async () => {
      const value = await f.snapshot(opened.sessionId);
      return value.cards.length && value;
    });
    const handoff = await f.rpc('search.handoff', { sessionId: opened.sessionId,
      selectionId: early.cards[0].displayKey, sources: [source] });
    const forgedMembers = [{ sourceKey: 'a', detailId: 'not-selected' }, { sourceKey: 'a', detailId: 'client-only' }];
    const playback = await f.rpc('session.open', {
      platform: 'test', ...handoff, selectionMembers: forgedMembers,
      target: { ...handoff.target, members: forgedMembers },
      searchHandoff: { ...handoff, selectionMembers: forgedMembers },
    });
    assert.equal(playback.target.metadataProvider, undefined);
    assert.deepEqual(playback.target.members, [{ sourceKey: 'a', detailId: 0 }]);
    await until(async () => (await f.rpc('session.snapshot', { sessionId: playback.sessionId })).stage === 'idle');
    await f.rpc('session.command', { sessionId: playback.sessionId, action: 'play' });
    const pending = await until(async () =>
      (await f.rpc('session.snapshot', { sessionId: playback.sessionId })).attempt);
    if (output === 'current') {
      await f.rpc('session.command', { sessionId: playback.sessionId, action: 'media.started', attemptId: pending.id });
      await f.rpc('session.command', { sessionId: playback.sessionId, action: 'media.progress',
        attemptId: pending.id, position: 23, duration: 90, paused: true });
    }
    const before = await f.rpc('session.snapshot', { sessionId: playback.sessionId });
    assert.ok(output === 'current' ? before.current : before.attempt);
    const receipt = await f.rpc('session.attempt', { sessionId: playback.sessionId, attemptId: pending.id });
    metadataGate.resolve({ results: [{ id: 12, media_type: 'tv', name: '星河' }] });
    await f.complete(opened.sessionId);
    await f.rpc('search.command', { sessionId: opened.sessionId, action: 'more' });
    const next = await f.complete(opened.sessionId);
    assert.deepEqual(next.cards.find(card => card.tmdbId === 12).aggregateItems.map(row => row.detailId), [0, '0']);
    const latest = await f.rpc('search.handoff', { sessionId: opened.sessionId,
      selectionId: handoff.searchReference.selectionId, sources: [source] });
    const members = [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }];
    assert.equal(latest.target.kind, 'tv');
    assert.deepEqual(latest.selectionMembers, members);
    assert.deepEqual(latest.results, [{ sourceKey: 'a', items: [first, later] }]);
    const calls = structuredClone(f.calls), metadataCalls = structuredClone(f.metadataCalls);
    await f.rpc('session.command', {
      sessionId: playback.sessionId, action: 'search.update', sources: [source],
      searchReference: handoff.searchReference, selectionMembers: forgedMembers,
      target: { ...before.target, members: forgedMembers },
      searchHandoff: { ...latest, selectionMembers: forgedMembers },
      results: [{ sourceKey: 'a', items: [{ id: 'client-only', name: '星河 第1季' }] }],
    });
    const updated = await until(async () => {
      const value = await f.rpc('session.snapshot', { sessionId: playback.sessionId });
      return value.metadata?.id === 12 && value;
    });
    assert.deepEqual(updated.target, { ...before.target, members });
    assert.deepEqual(updated.attempt, before.attempt);
    assert.deepEqual(updated.current, before.current);
    assert.equal(updated.stage, before.stage);
    assert.deepEqual(updated.sources.map(row => row.source.detailId), [0, '0']);
    assert.deepEqual(updated.search.map(row => [row.page, row.completed]), [[2, true]]);
    assert.deepEqual(await f.rpc('session.attempt', { sessionId: playback.sessionId, attemptId: pending.id }), receipt);
    assert.deepEqual(f.calls, calls, 'the update reuses search, detail and media instead of rescheduling them');
    assert.deepEqual(f.metadataCalls, metadataCalls, 'the late TMDB detail is reused from the same data owner');
    assert.deepEqual(f.calls.filter(row => row.operation === 'search').map(row => row.payload.page), [1, 2]);
    assert.equal(f.calls.filter(row => row.operation === 'detail').length, 1);
    assert.equal(f.calls.filter(row => row.operation === 'play').length, 1);
    await f.rpc('session.release', { sessionId: playback.sessionId });
  });
}
