import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after } from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { createPlaybackService } from '../packages/film-playback/src/service.js';
import { createPlaybackData } from '../packages/film-playback/src/data.js';
import { createPlaybackCache } from '../packages/film-playback/src/cache.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';
import { bindRuleSnapshot, invokeFilmRules } from '../packages/film-playback/src/rules.js';

const ruleRoot = mkdtempSync(join(tmpdir(), 'film-rules-playback-'));
const rules = createFilmRulesService({ dataRoot: ruleRoot });
await rules.snapshot();
after(async () => { await rules.close(); rmSync(ruleRoot, { recursive: true, force: true }); });

const source = (key, name = key) => ({ key, engine: 'cat', installationId: `install-${key}`,
  siteId: key, generation: 1, name });
const movie = { id: 'tmdb:movie:17', kind: 'movie', title: 'Film', metadataProvider: 'tmdb', metadataId: 17 };
const tv = { id: 'tmdb:tv:19', kind: 'tv', title: 'Series', metadataProvider: 'tmdb', metadataId: 19 };
const metadata = { id: 19, status: 'Ended', seasons: [{ season_number: 1, episode_count: 2,
  episodes: [{ season_number: 1, episode_number: 1, name: 'One', air_date: '2020-01-01' },
    { season_number: 1, episode_number: 2, name: 'Two', air_date: '2020-01-02' }] }] };
const tmdbAppended = { credits: { cast: [], crew: [] },
  similar: { page: 1, results: [], total_pages: 0, total_results: 0 },
  images: { backdrops: [], posters: [], logos: [] } };
const detail = (items, line = 'line') => ({ lines: [{ id: line, label: line, episodes: items.map((name, index) =>
  typeof name === 'string' ? { id: `play-${name}`, name, index } : { ...name, index }) }] });
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const error = code => Object.assign(new Error(code), { code });
const tick = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let turn = 0; turn < 1500; turn++) { const value = predicate(); if (value) return value; await delay(2); }
  assert.fail('Expected observable state was not reached');
}
function fixture(overrides = {}) {
  let clock = Date.parse('2026-11-08T12:00:00Z');
  let document = { version: 1, preferences: defaultPreferences(), history: [], manual: [], blocks: [], previewTargets: [] };
  const calls = [], resources = new Set(), released = [];
  let sequence = 0, storageError;
  const library = {
    read: () => structuredClone(document),
    update(mutator) {
      if (storageError) throw error(storageError);
      const draft = structuredClone(document); mutator(draft); document = draft;
    },
  };
  const data = {
    async handoff(entries) { calls.push(['handoff', entries]); return { accepted: entries.length, ignored: 0 }; },
    async targetMetadata(target) { calls.push(['metadata', target.id]); return target.kind === 'tv' ? structuredClone(metadata) : { id: 17 }; },
    async targetMetadataEntry(target, options) {
      const value = await this.targetMetadata(target, options);
      return value === null ? null : { value, completeness: 'complete', fetchedAt: clock, expiresAt: clock + 86_400_000,
        cache: { status: 'fresh', components: { detail: { status: 'ready', fetchedAt: clock } } } };
    },
    async metadataSources(target, { metadata: value }) {
      return [{ provider: target.metadataProvider, metadataId: target.metadataId,
        kind: target.kind, title: target.title, metadata: structuredClone(value) }];
    },
    async seasonMetadata(target, season) { calls.push(['season', season]); return structuredClone(metadata.seasons.find(row => row.season_number === season)); },
    async seasonMetadataEntry(target, season, options) {
      const value = await this.seasonMetadata(target, season, options);
      if (!value) throw error('NOT_FOUND');
      return { value, completeness: 'complete', fetchedAt: clock, expiresAt: clock + 86_400_000,
        cache: { status: 'fresh', components: { episodes: { status: 'ready', fetchedAt: clock } } } };
    },
    async search(value, query, page) { calls.push(['search', value.key, query, page]); return { page, hasMore: false,
      items: [{ vod_id: `detail-${value.key}`, vod_name: query }] }; },
    async detail(value, id) { calls.push(['detail', value.key, id]); return detail(['Film.1080p', 'Film.720p']); },
    async expand(value, line) { calls.push(['expand', value.key, line.id]); return line; },
    async resolve(candidate) { calls.push(['resolve', candidate.source.key, candidate.name]);
      return { url: `https://media.invalid/${encodeURIComponent(candidate.name)}`, watchReport: false,
        scope: { sourceKey: candidate.source.key } }; },
    async invalidateAddress(candidate) { calls.push(['invalidateAddress', candidate.id]); },
    async refresh(candidate, options) {
      calls.push(['refreshAddress', candidate.id]); return this.resolve(candidate, options);
    },
    async report(candidate, value) { calls.push(['report', candidate.source.key, value]); return { ok: true }; },
    ...overrides.data,
  };
  const media = {
    async prepare(descriptor, options) { calls.push(['prepare', descriptor.url, options]); const id = `resource-${++sequence}`;
      resources.add(id); return { id, url: descriptor.url }; },
    async release(id) { released.push(id); resources.delete(id); },
    async prefetch(id) { calls.push(['prefetch', id]); return { bytes: 4096 }; },
    async cancelPrefetch(id) { calls.push(['cancelPrefetch', id]); },
    ...overrides.media,
  };
  const service = createPlaybackService({ data, library, media, rules: overrides.rules ?? rules, now: () => clock });
  const open = async (value = {}, internal) => (await service.open({ platform: 'test', target: movie, sources: [source('a'), source('b')], ...value }, internal));
  const snapshot = id => service.snapshot({ sessionId: id });
  const command = (id, action, value = {}) => service.command({ sessionId: id, action, ...value });
  const pending = id => until(() => snapshot(id).attempt);
  const current = async id => { const attempt = await pending(id); await command(id, 'media.started', { attemptId: attempt.id }); return attempt; };
  return { service, data, media, library, calls, released, resources, open, snapshot, command, pending, current,
    advance: milliseconds => { clock += milliseconds; }, failStorage: code => { storageError = code; } };
}

test('playback captures only owner rules, preserves a playing snapshot and accepts internal search snapshots', async t => {
  const root = mkdtempSync(join(tmpdir(), 'film-rules-playback-snapshots-'));
  const owner = createFilmRulesService({ dataRoot: root }), initial = await owner.snapshot();
  const f = fixture({ rules: owner, data: {
    async search(_source, _query, page) { return { page, hasMore: false, items: [] }; },
  } });
  t.after(async () => { await f.service.close(); await owner.close(); rmSync(root, { recursive: true, force: true }); });
  const empty = { episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] };
  const input = { rules: empty, results: [{ sourceKey: 'a', items: [{ id: 'same-film', title: '[注]Film' }] }] };
  const first = await f.open(input);
  assert.equal(first.rulesRevision, initial.revision);
  await until(() => f.snapshot(first.sessionId).stage === 'idle');
  await f.command(first.sessionId, 'play');
  const current = await f.current(first.sessionId);
  const saved = await owner.set({ rules: empty });
  assert.equal(f.snapshot(first.sessionId).rulesRevision, initial.revision);
  assert.equal(f.snapshot(first.sessionId).current.attemptId, current.id);
  f.service.removeLibrary({ kind: 'history', id: movie.id });
  const second = await f.open(input);
  assert.equal(second.rulesRevision, saved.revision);
  await until(() => f.snapshot(second.sessionId).stage === 'idle');
  await f.command(second.sessionId, 'play');
  await until(() => f.snapshot(second.sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(second.sessionId).attempt, null);
  const handoff = await f.open(input, { ruleSnapshot: initial });
  assert.equal(handoff.rulesRevision, initial.revision);
  await until(() => f.snapshot(handoff.sessionId).stage === 'idle');
  await f.command(handoff.sessionId, 'play');
  await f.pending(handoff.sessionId);
});

test('playback close interrupts rules during an unfinished open without closing the shared owner', async t => {
  const root = mkdtempSync(join(tmpdir(), 'film-rules-playback-opening-'));
  const owner = createFilmRulesService({ dataRoot: root });
  const snapshot = await owner.set({ rules: { ...(await owner.snapshot()).rules, aggregateRegexRules: ['^(a+)+$'] } });
  const f = fixture({ rules: owner });
  t.after(async () => { await f.service.close(); await owner.close(); rmSync(root, { recursive: true, force: true }); });
  let settled = false;
  const opening = f.open({ results: [{ sourceKey: 'a', items: [{ id: 'slow-title', title: `${'a'.repeat(100_000)}!` }] }] },
    { ruleSnapshot: snapshot });
  const rejected = assert.rejects(opening, { code: 'CANCELLED' });
  void opening.finally(() => { settled = true; }).catch(() => {});
  await delay(25);
  assert.equal(settled, false);
  await f.service.close();
  await rejected;
  assert.equal(settled, true);
  assert.equal(f.calls.length, 0, 'an unreturned session never starts metadata or source producers');
  assert.equal((await owner.preview({ request: { section: 'episode', text: 'E3' } })).rewrittenText, 'E3');
});

test('stop revokes a repeated prepare while its asynchronous eligibility check is pending', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = await f.open({ platform: 'windows' });
  await f.pending(sessionId);
  const before = f.calls.filter(call => call[0] === 'resolve').length;
  const repeat = f.command(sessionId, 'prepare');
  const stop = f.command(sessionId, 'stop');
  await Promise.all([repeat, stop]);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).stage, 'stopped');
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, before);
});

test('a superseded rules refresh cannot write episodes into a newer browse selection', async t => {
  const original = await rules.snapshot(), entered = deferred(), gate = deferred();
  let armed = false;
  const snapshot = bindRuleSnapshot(Object.freeze({ ...original }), async (request, options) => {
    const value = await invokeFilmRules(original, request, options);
    if (armed && request.operation === 'recognition.candidates') {
      armed = false; entered.resolve(); await gate.promise;
    }
    return value;
  });
  const f = fixture({ data: {
    async detail(value) { return detail([value.detailId === 'one' ? 'Series.S01E01' : 'Series.S01E02']); },
  } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = await f.open({ target: tv, results: [{ sourceKey: 'a',
    items: [{ id: 'one', title: 'Series' }, { id: 'two', title: 'Series' }] }] }, { ruleSnapshot: snapshot });
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const [one, two] = f.snapshot(sessionId).sources;
  await f.command(sessionId, 'browse', { sourceId: one.id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  armed = true;
  await f.command(sessionId, 'browse.season', { season: 1 });
  await entered.promise;
  await f.command(sessionId, 'browse', { sourceId: two.id });
  await until(() => f.snapshot(sessionId).browsing.sourceId === two.id && !f.snapshot(sessionId).browsing.loading);
  const expected = f.snapshot(sessionId).browsing;
  assert.equal(expected.episodes[0].name, 'Series.S01E02');
  gate.resolve();
  await tick();
  assert.deepEqual(f.snapshot(sessionId).browsing, expected);
});

test('opening a non-autoplay page and its source menu does no source I/O; more searches but never reads detail', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open()); await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.equal(f.calls.some(call => ['search', 'detail', 'expand', 'resolve', 'prepare'].includes(call[0])), false);
  f.snapshot(sessionId); f.snapshot(sessionId);
  await f.command(sessionId, 'search.more');
  await until(() => f.snapshot(sessionId).searchComplete);
  assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a', 'b']);
  assert.equal(f.calls.some(call => ['detail', 'expand', 'resolve', 'prepare'].includes(call[0])), false);
});

test('a Cat local media address does not implicitly enable short-video prefetch', async t => {
  const f = fixture({ data: { async resolve() {
    return { url: 'http://127.0.0.1:49000/owned-script',
      scope: { sourceKey: 'a', sourceEngine: 'cat', sourceInstallationId: 'install-a' } };
  } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  await f.pending(sessionId);
  const prepared = f.calls.find(call => call[0] === 'prepare');
  assert.equal(prepared[1], 'http://127.0.0.1:49000/owned-script');
  assert.equal(prepared[2].cache, false);
  assert.equal(f.calls.some(call => call[0] === 'prefetch'), false);
});

test('browse selects exactly one source; failed third row drops old episodes and valid source recovers', async t => {
  const f = fixture({ data: { async detail(value) {
    f.calls.push(['detail', value.key]); if (value.key === 'b') throw error('UPSTREAM_HTTP');
    return detail(['One', 'Two']);
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ results: [ { sourceKey: 'a', items: [{ id: 'aa', title: 'Film' }] },
    { sourceKey: 'b', items: [{ id: 'bb', title: 'Film' }] } ] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const [a, b] = f.snapshot(sessionId).sources;
  await f.command(sessionId, 'browse', { sourceId: a.id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 2);
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail'), [['detail', 'a']]);
  assert.ok(f.snapshot(sessionId).browsing.episodes.every(row => row.candidateId));
  await f.command(sessionId, 'browse', { sourceId: b.id });
  await until(() => f.snapshot(sessionId).browsing.error === 'UPSTREAM_HTTP');
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes, []);
  assert.equal(f.snapshot(sessionId).sources.length, 2);
  await f.command(sessionId, 'browse', { sourceId: a.id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 2);
  assert.equal(f.snapshot(sessionId).browsing.error, null);
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
});

test('iOS prepares real first episode, never starts media/history/report and stops before next source', async t => {
  const f = fixture({ data: { async detail(value) {
    f.calls.push(['detail', value.key]); return detail(['Series.S01E01.1080p', 'Series.S01E02.4K']);
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios', target: tv }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.prepareOnly, true); assert.equal(attempt.candidate.episode, 1);
  assert.equal(f.snapshot(sessionId).stage, 'prepared'); assert.equal(f.snapshot(sessionId).current, null);
  assert.deepEqual(f.library.read().history, []);
  assert.equal(f.calls.some(call => call[0] === 'report' || call[0] === 'prefetch'), false);
  assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a']);
  assert.equal(f.snapshot(sessionId).searchComplete, false);
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: attempt.id }), { code: 'STALE_SESSION' });
  const preparedCount = f.calls.filter(call => call[0] === 'resolve').length;
  await f.command(sessionId, 'play');
  assert.equal(f.snapshot(sessionId).attempt.id, attempt.id);
  assert.equal(f.snapshot(sessionId).attempt.prepareOnly, false);
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, preparedCount);
  await f.command(sessionId, 'media.started', { attemptId: attempt.id });
  assert.equal(f.library.read().history.length, 1);
});

test('disabled iOS auto preparation and raw source entry do not secretly take media ownership', async t => {
  const f = fixture(); t.after(() => f.service.close());
  (await f.service.setSettings({ ...defaultPreferences(), iosAutoPrepare: false }));
  const first = (await f.open({ platform: 'ios' }));
  await until(() => f.snapshot(first.sessionId).stage === 'idle');
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
  (await f.service.setSettings(defaultPreferences()));
  const second = (await f.open({ platform: 'ios', target: { id: 'source:a:aa', kind: 'source', title: 'Raw',
    source: { key: 'a', detailId: 'aa' } } }));
  await until(() => f.snapshot(second.sessionId).stage === 'idle');
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
});

for (const platform of ['ios', 'windows']) {
  test(`${platform} can prepare the real Douban first episode while optional metadata association fails`, async t => {
    const calls = [], result = value => ({ ok: true, value });
    const target = { id: 'douban:tv:51', kind: 'tv', title: 'Series',
      metadataProvider: 'douban', metadataId: '51' };
    const modules = new Map([
      ['douban', { methods: {
        'douban.data.settings.get': async () => result({ revision: 'douban-a', cookieConfigured: true }),
        'douban.data.request': async () => {
          calls.push('primary');
          return result({ id: '51', title: 'Series 第1季', episodes_count: 2, intro: '', genres: [] });
        },
        'douban.data.search': async () => { calls.push('association'); return { ok: false, code: 'UPSTREAM_HTTP' }; },
      } }],
      ['tmdb', { methods: {
        'tmdb.data.settings.get': async () => result({ revision: 'tmdb-a', apiKeyConfigured: true }),
        'tmdb.data.request': async () => { throw error('NOT_CONFIGURED'); },
      } }],
      ['cat', { authorizeMedia: async () => false, methods: {
        'cat.script.status': async () => result({ instances: [{ installationId: 'install-a', generation: 1,
          state: 'ready', sites: [{ key: 'a' }] }] }),
        'cat.script.invoke': async ({ operation }) => {
          calls.push(operation);
          return result(operation === 'search' ? { pagecount: 1, list: [{ vod_id: 'actual', vod_name: 'Series' }] }
            : operation === 'detail' ? { list: [{ vod_id: 'actual', vod_play_from: 'direct',
              vod_play_url: 'Series.S01E01$episode-1#Series.S01E02$episode-2' }] }
              : { url: 'https://media.invalid/episode-1' });
        },
      } }],
    ]);
    const cache = createPlaybackCache(), data = createPlaybackData({ modules, cache });
    const f = fixture({ data });
    t.after(async () => { await f.service.close(); await data.close(); await cache.close(); });
    const { sessionId } = (await f.open({ platform, target, sources: [source('a')] }));
    const attempt = await f.pending(sessionId);
    await until(() => f.snapshot(sessionId).metadataError === 'UPSTREAM_HTTP');
    assert.equal(attempt.prepareOnly, platform === 'ios');
    assert.equal(attempt.candidate.episode, 1);
    assert.deepEqual(f.snapshot(sessionId).episodes.map(row => [row.season, row.episode]), [[1, 1], [1, 2]]);
    assert.equal(f.snapshot(sessionId).metadataSources.length, 1);
    assert.equal(f.snapshot(sessionId).error, null);
    assert.equal(f.snapshot(sessionId).current, null);
    assert.deepEqual(f.library.read().history, []);
    assert.equal(calls.filter(value => value === 'primary').length, 1);
    assert.deepEqual(calls.filter(value => ['search', 'detail', 'play'].includes(value)), ['search', 'detail', 'play']);
  });
}

test('metadata initialization, seasonal browsing, provider selection and invalidation use the fixed owner rules', async t => {
  const previousRules = await rules.snapshot(), reads = [];
  const ruleSnapshot = await rules.set({ rules: { ...previousRules.rules, episodeCleanRegexRules: ['trailer'] } });
  t.after(() => rules.set({ rules: previousRules.rules }));
  const f = fixture({ data: {
    async targetMetadata(target, options) {
      reads.push(['target', options?.rules]); return structuredClone(metadata);
    },
    async seasonMetadata(target, season, options) {
      reads.push(['season', options?.rules]); return structuredClone(metadata.seasons[0]);
    },
  } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv }, { ruleSnapshot }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'browse.season', { season: 1 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: f.snapshot(sessionId).metadataSources[0].id });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  await f.service.invalidate('tmdb');
  await until(() => f.snapshot(sessionId).metadata !== null);
  assert.equal(reads.filter(row => row[0] === 'target').length, 3);
  assert.equal(reads.filter(row => row[0] === 'season').length, 3);
  for (const [, actual] of reads) assert.strictEqual(actual, ruleSnapshot);
});

test('G02-C01 real data restores cached season fields and preview after returning from a source directory', async t => {
  const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
    seasons: [{ season_number: 1, episode_count: 3 }] };
  const season = { season_number: 1, episodes: [1, 2, 3].map(episode_number => ({
    season_number: 1, episode_number, name: `Real episode ${episode_number}`,
    air_date: episode_number === 3 ? '2999-01-01' : '2020-01-01',
    still_path: `/real-${episode_number}.jpg`, overview: `Real overview ${episode_number}`,
  })) };
  const runtime = runtimeDataFixture(({ operation, payload }) => {
    assert.equal(operation, 'detail'); assert.equal(payload.id, 0);
    return { vod_id: 0, vod_name: 'Series', ...detail([1, 2, 3, 99].map(episode => `Series.S01E${episode}`)) };
  }, ({ path }) => {
    if (path === '/3/tv/19') return series;
    assert.equal(path, '/3/tv/19/season/1'); return season;
  });
  const f = fixture({ data: runtime.data });
  t.after(async () => { await f.service.close(); await runtime.close(); });
  (await f.service.setSettings({ ...defaultPreferences(), iosAutoPrepare: false }));
  const { sessionId } = (await f.open({ platform: 'ios', target: tv, sources: [source('a')],
    results: [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: 'Series' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const primary = f.snapshot(sessionId).metadataSources[0];
  const initial = f.snapshot(sessionId).episodes;
  assert.deepEqual(initial.map(row => row.episode), [1, 2]);
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.originalEpisodes?.length === 4);
  assert.deepEqual(f.snapshot(sessionId).browsing.originalEpisodes.map(row => row.episode), [1, 2, 3, 99]);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).browsing.sourceId === null);
  assert.deepEqual(f.snapshot(sessionId).episodes, initial);
  await f.command(sessionId, 'preview', { enabled: true });
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => row.episode), [1, 2, 3]);
  assert.deepEqual(f.snapshot(sessionId).episodes[2],
    { ...season.episodes[2], season: 1, episode: 3, globalEpisode: 3 });
  await f.command(sessionId, 'preview', { enabled: false });
  assert.deepEqual(f.snapshot(sessionId).episodes, initial);
  assert.deepEqual(runtime.metadataCalls.map(row => row.path), ['/3/tv/19', '/3/tv/19/season/1']);
  assert.deepEqual(runtime.calls.map(row => row.operation), ['detail']);
  assert.equal(f.calls.some(row => ['resolve', 'prepare', 'prefetch', 'report'].includes(row[0])), false);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).current, null);
  assert.deepEqual(f.library.read().history, []);
});

for (const state of ['idle', 'prepared', 'playing']) {
  test(`FR-Node-01 real data same-TV search.update preserves full season fields and preview while ${state}`, async t => {
    const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
      seasons: [{ season_number: 1, episode_count: 3 }] };
    const season = { season_number: 1, episodes: [1, 2, 3].map(episode_number => ({
      season_number: 1, episode_number, name: `Real episode ${episode_number}`,
      air_date: episode_number === 3 ? '2999-01-01' : '2020-01-01',
      still_path: `/real-${episode_number}.jpg`, overview: `Real overview ${episode_number}`,
    })) };
    const runtime = runtimeDataFixture(({ operation, payload }) => {
      if (operation === 'detail') {
        assert.equal(payload.id, 0);
        return { vod_id: 0, vod_name: 'Series', vod_play_from: 'direct',
          vod_play_url: 'Series.S01E01$episode-1#Series.S01E02$episode-2#Series.S01E03$episode-3' };
      }
      assert.equal(operation, 'play'); assert.equal(payload.id, 'episode-1');
      return { url: 'https://media.invalid/episode-1' };
    }, ({ path }) => {
      if (path === '/3/tv/19') return series;
      assert.equal(path, '/3/tv/19/season/1'); return season;
    });
    const f = fixture({ data: runtime.data });
    t.after(async () => { await f.service.close(); await runtime.close(); });
    (await f.service.setSettings({ ...defaultPreferences(), iosAutoPrepare: false }));
    const sources = [source('a')], results = [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: 'Series' }] }];
    const search = [{ sourceKey: 'a', page: 1, completed: true }];
    const { sessionId } = (await f.open({ platform: 'ios', target: tv, sources, results, search }));
    await until(() => f.snapshot(sessionId).stage === 'idle');
    if (state !== 'idle') {
      await f.command(sessionId, state === 'prepared' ? 'prepare' : 'play');
      if (state === 'playing') await f.current(sessionId);
      else await f.pending(sessionId);
    }
    const before = f.snapshot(sessionId), sourceCalls = structuredClone(runtime.calls);
    const prepares = f.calls.filter(row => row[0] === 'prepare').length, history = f.library.read().history;
    const initial = season.episodes.slice(0, 2).map(row =>
      ({ ...row, season: 1, episode: row.episode_number, globalEpisode: row.episode_number }));
    assert.deepEqual(before.episodes, initial);
    await f.command(sessionId, 'search.update', searchContext(tv, 1, { sources, results, search, metadataTarget: tv }));
    await until(() => f.snapshot(sessionId).sequence >= before.sequence + 2);
    const updated = f.snapshot(sessionId);
    assert.deepEqual(updated.episodes, initial);
    assert.deepEqual(updated.metadata.seasons[0].episodes, season.episodes);
    assert.deepEqual(updated.target, before.target);
    assert.deepEqual(updated.attempt, before.attempt);
    assert.deepEqual(updated.current, before.current);
    assert.equal(updated.stage, before.stage);
    assert.equal(updated.metadataSourceId, before.metadataSourceId);
    await assert.rejects(f.command(sessionId, 'select.episode', { episode: { season: 1, episode: 3 } }),
      { code: 'NO_MATCH' });
    await f.command(sessionId, 'preview', { enabled: true });
    assert.deepEqual(f.snapshot(sessionId).episodes, season.episodes.map(row =>
      ({ ...row, season: 1, episode: row.episode_number, globalEpisode: row.episode_number })));
    await f.command(sessionId, 'preview', { enabled: false });
    assert.deepEqual(f.snapshot(sessionId).episodes, initial);
    assert.deepEqual(runtime.metadataCalls.map(row => row.path), ['/3/tv/19', '/3/tv/19/season/1']);
    assert.deepEqual(runtime.calls, sourceCalls);
    assert.equal(f.calls.filter(row => row[0] === 'prepare').length, prepares);
    assert.equal(f.calls.some(row => ['resolve', 'prefetch', 'report'].includes(row[0])), false);
    assert.deepEqual(f.library.read().history, history);
  });
}

test('FR-Node-02 real data expired S1 playback preserves the newer full S2 browse and continues S1', async t => {
  let clock = Date.parse('2026-11-08T12:00:00Z'), seasonReads = 0;
  const now = () => clock, gate = deferred(), entered = deferred();
  const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
    seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 3 }] };
  const seasons = {
    1: { season_number: 1, episodes: [1, 2].map(episode_number => ({
      season_number: 1, episode_number, name: `S1 episode ${episode_number}`,
      air_date: episode_number === 2 ? '2026-11-09' : '2020-01-01',
      still_path: `/s1-${episode_number}.jpg`, overview: `S1 overview ${episode_number}`,
    })) },
    2: { season_number: 2, episodes: [1, 2, 3].map(episode_number => ({
      season_number: 2, episode_number, name: `S2 episode ${episode_number}`,
      air_date: episode_number === 3 ? '2999-01-01' : '2020-01-01',
      still_path: `/s2-${episode_number}.jpg`, overview: `S2 overview ${episode_number}`,
    })) },
  };
  const runtime = runtimeDataFixture(({ operation, payload }) => {
    if (operation === 'detail') {
      assert.equal(payload.id, 0);
      return { vod_id: 0, vod_name: 'Series', vod_play_from: 'direct',
        vod_play_url: 'Series.S01E01$episode-1#Series.S02E01$other-season' };
    }
    assert.equal(operation, 'play'); assert.equal(payload.id, 'episode-1');
    return { url: 'https://media.invalid/episode-1' };
  }, async ({ path }) => {
    if (path === '/3/tv/19') return series;
    if (path === '/3/tv/19/season/1') {
      if (++seasonReads === 2) {
        entered.resolve(); await gate.promise;
        return { ...seasons[1], episodes: seasons[1].episodes.map(row => ({ ...row, name: `Refreshed ${row.name}` })) };
      }
      return seasons[1];
    }
    assert.equal(path, '/3/tv/19/season/2'); return seasons[2];
  }, { now });
  const f = fixture({ data: runtime.data });
  t.after(async () => { gate.resolve(); await f.service.close(); await runtime.close(); });
  (await f.service.setSettings({ ...defaultPreferences(), iosAutoPrepare: false }));
  const { sessionId } = (await f.open({ platform: 'ios', target: tv, sources: [source('a')],
    results: [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: 'Series' }] }],
    search: [{ sourceKey: 'a', page: 1, completed: true }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => row.name), ['S1 episode 1']);
  const request = { kind: 'metadata', provider: 'tmdb', path: '/3/tv/19/season/1', query: { language: 'zh-CN' } };
  const cached = await runtime.data.cachedEntry(request, { minimumCompleteness: 'complete' });
  assert.equal(cached.expiresAt, Date.parse('2026-11-08T16:00:00Z'));
  clock += 5 * 3_600_000; f.advance(5 * 3_600_000);
  assert.equal(await runtime.data.cachedEntry(request, { minimumCompleteness: 'complete' }), undefined);
  await f.command(sessionId, 'play');
  await entered.promise;
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.deepEqual(runtime.calls, []);
  await f.command(sessionId, 'browse.season', { season: 2 });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).browsing.season === 2);
  const second = f.snapshot(sessionId);
  const visible = seasons[2].episodes.slice(0, 2).map(row =>
    ({ ...row, season: 2, episode: row.episode_number, globalEpisode: 2 + row.episode_number }));
  assert.deepEqual(second.episodes, visible);
  assert.deepEqual(second.metadata.seasons.find(row => row.season_number === 2).episodes, seasons[2].episodes);
  assert.equal(second.browsing.error, null);
  gate.resolve();
  const attempt = await f.pending(sessionId), final = f.snapshot(sessionId);
  assert.deepEqual(final.browsing, second.browsing);
  assert.deepEqual(final.episodes, visible);
  assert.deepEqual(final.metadata.seasons.find(row => row.season_number === 2),
    second.metadata.seasons.find(row => row.season_number === 2));
  assert.equal(final.metadata.seasons[0].episodes[0].name, 'Refreshed S1 episode 1');
  assert.equal(attempt.candidate.season, 1); assert.equal(attempt.candidate.episode, 1);
  assert.equal(attempt.prepareOnly, false);
  assert.equal(final.target.season, 1); assert.equal(final.target.episode, 1);
  assert.equal(final.stage, 'pending'); assert.equal(final.error, null);
  await assert.rejects(f.command(sessionId, 'select.episode', { episode: { season: 2, episode: 3 } }),
    { code: 'NO_MATCH' });
  await f.command(sessionId, 'preview', { enabled: true });
  assert.deepEqual(f.snapshot(sessionId).episodes, seasons[2].episodes.map(row =>
    ({ ...row, season: 2, episode: row.episode_number, globalEpisode: 2 + row.episode_number })));
  await f.command(sessionId, 'preview', { enabled: false });
  assert.deepEqual(f.snapshot(sessionId).episodes, visible);
  assert.equal(f.snapshot(sessionId).attempt.id, attempt.id);
  assert.deepEqual(runtime.metadataCalls.map(row => row.path),
    ['/3/tv/19', '/3/tv/19/season/1', '/3/tv/19/season/1', '/3/tv/19/season/2']);
  assert.deepEqual(runtime.calls.map(row => row.operation), ['detail', 'play']);
  assert.equal(f.calls.filter(row => row[0] === 'prepare').length, 1);
  assert.deepEqual(f.library.read().history, []);
});

test('G02-C01 real data preserves a selected season-zero episode-zero and an explicitly empty season on metadata return', async t => {
  const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
    seasons: [0, 1, 2].map(season_number => ({ season_number, episode_count: season_number === 2 ? 4 : 1 })) };
  const seasons = {
    0: { season_number: 0, episodes: [{ season_number: 0, episode_number: 0,
      name: 'Real prologue', air_date: '2020-01-01', still_path: '/prologue.jpg' }] },
    1: { season_number: 1, episodes: [{ season_number: 1, episode_number: 1, air_date: '2020-01-01' }] },
    2: { season_number: 2, episodes: [] },
  };
  const runtime = runtimeDataFixture(() => { assert.fail('metadata browsing must not invoke a site'); }, ({ path }) => {
    if (path === '/3/tv/19') return series;
    const number = Number(/^\/3\/tv\/19\/season\/(\d+)$/.exec(path)?.[1]);
    assert.ok(Object.hasOwn(seasons, number)); return seasons[number];
  });
  const f = fixture({ data: runtime.data });
  t.after(async () => { await f.service.close(); await runtime.close(); });
  const { sessionId } = (await f.open({ target: { ...tv, season: 0, episode: 0 }, sources: [] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const primary = f.snapshot(sessionId).metadataSources[0];
  await f.command(sessionId, 'browse.season', { season: 0 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  const special = f.snapshot(sessionId).episodes;
  assert.deepEqual(special, [{ ...seasons[0].episodes[0], season: 0, episode: 0 }]);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.equal(f.snapshot(sessionId).browsing.season, 0);
  assert.deepEqual(f.snapshot(sessionId).episodes, special);
  assert.equal(f.snapshot(sessionId).target.season, 0);
  assert.equal(f.snapshot(sessionId).target.episode, 0);
  assert.equal(f.snapshot(sessionId).target.globalEpisode, undefined);
  await f.command(sessionId, 'browse.season', { season: 2 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.equal(f.snapshot(sessionId).browsing.season, 2);
  assert.equal(f.snapshot(sessionId).browsing.error, null);
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  assert.deepEqual(f.snapshot(sessionId).metadata.seasons.find(row => row.season_number === 2).episodes, []);
  await f.command(sessionId, 'preview', { enabled: true });
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  await f.command(sessionId, 'preview', { enabled: false });
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  assert.deepEqual(runtime.metadataCalls.map(row => row.path),
    ['/3/tv/19', '/3/tv/19/season/0', '/3/tv/19/season/2']);
});

test('G02-C01 real data does not merge TMDB season fields into a different Douban identity', async t => {
  const calls = [], reply = value => ({ ok: true, value });
  const primary = { id: '20', title: 'Series 第1季', year: '2020', season: 1,
    episodes_count: 1, genres: [], intro: 'Real Douban data' };
  const series = { ...tmdbAppended, id: 19, name: 'Series', first_air_date: '2020-01-01', genres: [], status: 'Ended',
    seasons: [{ season_number: 1, episode_count: 3 }] };
  const season = { season_number: 1, episodes: [1, 2, 3].map(episode_number => ({
    season_number: 1, episode_number, name: `TMDB episode ${episode_number}`,
    air_date: episode_number === 3 ? '2999-01-01' : '2020-01-01', still_path: `/tmdb-${episode_number}.jpg`,
  })) };
  const modules = new Map([
    ['douban', { methods: {
      'douban.data.settings.get': async () => reply({ revision: 'douban-current' }),
      'douban.data.request': async ({ path }) => {
        calls.push(['douban', path]); assert.equal(path, '/rexxar/api/v2/tv/20'); return reply(primary);
      },
      'douban.data.search': async () => {
        calls.push(['douban', 'search']); return reply({ subjects: { items: [] } });
      },
    } }],
    ['tmdb', { methods: {
      'tmdb.data.settings.get': async () => reply({ revision: 'tmdb-current' }),
      'tmdb.data.request': async ({ path }) => {
        calls.push(['tmdb', path]);
        if (path === '/3/search/tv') return reply({ results: [{ id: 19, name: 'Series', first_air_date: '2020-01-01' }],
          total_pages: 1 });
        if (path === '/3/tv/19') return reply(series);
        assert.equal(path, '/3/tv/19/season/1'); return reply(season);
      },
    } }],
  ]);
  const cache = createPlaybackCache(), data = createPlaybackData({ modules, cache });
  const f = fixture({ data });
  t.after(async () => { await f.service.close(); await data.close(); await cache.close(); });
  const { sessionId } = (await f.open({ target: { id: 'douban:tv:20', kind: 'tv', title: 'Series',
    metadataProvider: 'douban', metadataId: '20' }, sources: [] }));
  await until(() => f.snapshot(sessionId).stage === 'idle' && f.snapshot(sessionId).metadataSources.length === 2);
  const first = f.snapshot(sessionId).metadataSources.find(row => row.provider === 'douban');
  const second = f.snapshot(sessionId).metadataSources.find(row => row.provider === 'tmdb');
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: second.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).metadataSourceId === second.id);
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => row.name), ['TMDB episode 1', 'TMDB episode 2']);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: first.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).metadataSourceId === first.id);
  assert.equal(f.snapshot(sessionId).metadata.id, '20');
  assert.equal(f.snapshot(sessionId).target.metadataId, '20');
  assert.deepEqual(f.snapshot(sessionId).episodes, [{ season: 1, episode: 1, globalEpisode: 1 }]);
  await f.command(sessionId, 'preview', { enabled: true });
  assert.deepEqual(f.snapshot(sessionId).episodes, [{ season: 1, episode: 1, globalEpisode: 1 }]);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: second.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).metadataSourceId === second.id);
  assert.equal(f.snapshot(sessionId).metadata.id, 19);
  assert.deepEqual(f.snapshot(sessionId).episodes[2], { ...season.episodes[2], season: 1, episode: 3, globalEpisode: 3 });
  assert.deepEqual(calls, [['douban', '/rexxar/api/v2/tv/20'], ['douban', 'search'], ['tmdb', '/3/search/tv'],
    ['tmdb', '/3/tv/19'], ['tmdb', '/3/tv/19/season/1']]);
});

for (const pausedAt of ['metadata', 'season']) {
  test(`G02-C01 real data late ${pausedAt} return cannot overwrite a newer special-season browse`, async t => {
    const gate = deferred();
    let hold = false, waiting = false;
    const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
      seasons: [{ season_number: 0, episode_count: 1 }, { season_number: 1, episode_count: 1 }] };
    const seasons = [0, 1].map(season_number => ({ season_number, episodes: [{
      season_number, episode_number: season_number, name: `Season ${season_number}`, air_date: '2020-01-01',
    }] }));
    const runtime = runtimeDataFixture(() => { assert.fail('metadata browsing must not invoke a site'); }, async ({ path }) => {
      if (hold && pausedAt === 'season' && path === '/3/tv/19/season/1') {
        hold = false; waiting = true; await gate.promise;
      }
      if (path === '/3/tv/19') return series;
      const number = Number(/^\/3\/tv\/19\/season\/(\d+)$/.exec(path)?.[1]);
      assert.ok(Object.hasOwn(seasons, number)); return seasons[number];
    });
    const f = fixture({ data: { ...runtime.data, async targetMetadataEntry(...args) {
      const entry = await runtime.data.targetMetadataEntry(...args);
      // A newer season read must see the fresh parent; hold only this page's delivery.
      if (hold && pausedAt === 'metadata') {
        hold = false; waiting = true; await gate.promise;
      }
      return entry;
    } } });
    t.after(async () => { gate.resolve(); await f.service.close(); await runtime.close(); });
    const { sessionId } = (await f.open({ target: tv, sources: [] }));
    await until(() => f.snapshot(sessionId).stage === 'idle');
    const primary = f.snapshot(sessionId).metadataSources[0];
    await runtime.data.invalidate('tmdb');
    hold = true;
    await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
    await until(() => waiting);
    await f.command(sessionId, 'browse.season', { season: 0 });
    await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).browsing.season === 0);
    const latest = f.snapshot(sessionId);
    assert.deepEqual(latest.episodes.map(row => [row.season, row.episode, row.name]), [[0, 0, 'Season 0']]);
    gate.resolve(); await delay(2); await delay(2);
    assert.deepEqual(f.snapshot(sessionId), latest);
    assert.equal(runtime.metadataCalls.filter(row => row.path === '/3/tv/19/season/1').length, pausedAt === 'metadata' ? 1 : 2);
  });
}

test('Windows auto playback waits for output; first media failure continues cached candidate without new search', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.pending(sessionId);
  assert.equal(first.prepareOnly, false); assert.equal(f.snapshot(sessionId).current, null);
  assert.equal(f.library.read().history.length, 0);
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  const second = await f.pending(sessionId);
  assert.notEqual(second.candidate.id, first.candidate.id);
  assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a']);
  await f.command(sessionId, 'media.started', { attemptId: second.id });
  assert.equal(f.snapshot(sessionId).stage, 'playing');
  assert.equal(f.library.read().history[0].candidateId, second.candidate.id);
});

test('history and manual data are attempted before unresolved search results and new pages', async t => {
  const f = fixture(); t.after(() => f.service.close());
  f.library.update(document => { document.history.push({ targetId: movie.id, title: 'Film', sourceKey: 'b',
    candidateId: 'previous', detailId: 'from-history', lineId: 'line', episode: {}, position: 35, duration: 90,
    paused: true, updatedAt: 1 }); });
  const { sessionId } = (await f.open({ platform: 'windows', results: [{ sourceKey: 'a', items: [{ id: 'query-result', title: 'Film' }] }] }));
  const attempted = await f.pending(sessionId);
  assert.equal(attempted.candidate.source.detailId, 'from-history');
  assert.equal(attempted.position, 35);
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail'), [['detail', 'b', 'from-history']]);
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
});

test('failed cached, history and manual candidates all fall through to another playable source', async t => {
  const f = fixture({ data: {
    async detail(value, id) { f.calls.push(['detail', value.key, id]); return detail([`Film.${id}.1080p`]); },
    async resolve(candidate) { f.calls.push(['resolve', candidate.source.detailId]);
      if (candidate.source.detailId !== 'detail-b') throw error('MEDIA_UNAVAILABLE');
      return { url: 'https://media.invalid/good' }; },
  } }); t.after(() => f.service.close());
  f.library.update(document => {
    document.history.push({ targetId: movie.id, title: 'Film', sourceKey: 'a', candidateId: 'old',
      detailId: 'history', lineId: 'line', episode: {}, position: 0, duration: 0, paused: false, updatedAt: 1 });
    document.manual.push({ id: 'manual', targetId: movie.id, sourceKey: 'a', detailId: 'manual', enabled: true, failureCount: 0 });
  });
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [{ id: 'cached', title: 'Film' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  await f.command(sessionId, 'play'); const value = await f.pending(sessionId);
  assert.equal(value.candidate.source.detailId, 'detail-b');
  assert.deepEqual(f.calls.filter(call => call[0] === 'resolve').map(call => call[1]),
    ['cached', 'history', 'manual', 'detail-a', 'detail-b']);
});

test('same episode replacement keeps progress and paused intent and leaves old lease on failure', async t => {
  const f = fixture({ data: { async detail(value) { f.calls.push(['detail', value.key]);
    return detail(['Film.4K', 'Film.1080p', 'Film.720p']); } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const first = await f.current(sessionId);
  const original = f.service.attempt({ sessionId, attemptId: first.id });
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 53, duration: 180, paused: true });
  await f.command(sessionId, 'switch'); const second = await f.pending(sessionId);
  assert.equal(second.position, 53); assert.equal(second.paused, true);
  assert.equal(f.resources.has(original.id), true);
  await f.command(sessionId, 'media.failed', { attemptId: second.id });
  const third = await f.pending(sessionId);
  assert.equal(third.position, 53); assert.equal(third.paused, true);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.equal(f.resources.has(original.id), true);
  await f.command(sessionId, 'media.started', { attemptId: third.id }); await delay(2);
  assert.equal(f.resources.has(original.id), false);
  assert.equal(f.snapshot(sessionId).current.attemptId, third.id);
  assert.equal(f.library.read().blocks.length, 0);
});

test('movie wrong-source persists exact line before switching, and failed persistence does not switch', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const first = await f.current(sessionId);
  f.failStorage('STORAGE_ERROR');
  await assert.rejects(f.command(sessionId, 'block', { attemptId: first.id }), { code: 'STORAGE_ERROR' });
  assert.equal(f.library.read().blocks.length, 0); assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  f.failStorage(null); await f.command(sessionId, 'block', { attemptId: first.id });
  const second = await f.pending(sessionId);
  assert.equal(second.candidate.source.key, 'b');
  assert.deepEqual(f.library.read().blocks, [{ targetId: movie.id, sourceKey: 'a', detailId: 'detail-a', lineId: 'line' }]);
});

test('E19/E21 a block confirmation captured for A cannot write or switch after B becomes current', async t => {
  const f = fixture({ data: { async detail() { return detail(['Film.1080p']); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId), capturedAttemptId = first.id;
  await f.command(sessionId, 'switch');
  const second = await f.pending(sessionId);
  assert.equal(first.candidate.source.key, 'a'); assert.equal(second.candidate.source.key, 'b');
  await f.command(sessionId, 'media.started', { attemptId: second.id }); await delay(2);
  assert.equal(f.snapshot(sessionId).current.attemptId, second.id);
  const before = f.snapshot(sessionId), document = f.library.read(), calls = f.calls.slice();
  const resources = [...f.resources], released = f.released.slice();
  const update = f.library.update; let writes = 0;
  f.library.update = mutator => { writes++; return update(mutator); };
  await assert.rejects(f.command(sessionId, 'block', { attemptId: capturedAttemptId }), { code: 'STALE_SESSION' });
  await delay(2);
  assert.equal(writes, 0);
  assert.deepEqual(f.library.read(), document);
  assert.deepEqual(f.snapshot(sessionId), before);
  assert.deepEqual(f.calls, calls);
  assert.deepEqual([...f.resources], resources); assert.deepEqual(f.released, released);
});

test('E19/E21 a block confirmation for current A still blocks only A while B is pending', async t => {
  const f = fixture({ data: { async detail() { return detail(['Film.1080p']); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId), capturedAttemptId = first.id;
  await f.command(sessionId, 'switch');
  const second = await f.pending(sessionId);
  assert.equal(first.candidate.source.key, 'a'); assert.equal(second.candidate.source.key, 'b');
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  const before = f.snapshot(sessionId), document = f.library.read(), calls = f.calls.slice();
  const update = f.library.update; let writes = 0;
  f.library.update = mutator => { writes++; return update(mutator); };
  await assert.rejects(f.command(sessionId, 'block', { attemptId: second.id }), { code: 'STALE_SESSION' });
  assert.equal(writes, 0);
  assert.deepEqual(f.library.read(), document);
  assert.deepEqual(f.snapshot(sessionId), before);
  assert.deepEqual(f.calls, calls);
  await f.command(sessionId, 'block', { attemptId: capturedAttemptId });
  const replacement = await f.pending(sessionId);
  assert.equal(writes, 1);
  assert.deepEqual(f.library.read().blocks,
    [{ targetId: movie.id, sourceKey: 'a', detailId: 'detail-a', lineId: 'line' }]);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.equal(replacement.candidate.source.key, 'b');
});

test('E19/E21 block requires the exact current-or-pending attemptId with no missing-ID fallback', async t => {
  const f = fixture({ data: { async detail() { return detail(['Film.1080p']); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open());
  await until(() => f.snapshot(sessionId).stage === 'idle'); await delay(2);
  const update = f.library.update; let writes = 0;
  f.library.update = mutator => { writes++; return update(mutator); };
  const idle = f.snapshot(sessionId), document = f.library.read(), calls = f.calls.slice();
  await assert.rejects(f.command(sessionId, 'block'), { code: 'STALE_SESSION' });
  assert.equal(writes, 0);
  assert.deepEqual(f.library.read(), document);
  assert.deepEqual(f.snapshot(sessionId), idle);
  assert.deepEqual(f.calls, calls);
  await f.command(sessionId, 'play');
  const first = await f.pending(sessionId);
  assert.equal(f.snapshot(sessionId).current, null);
  const before = f.snapshot(sessionId), pendingCalls = f.calls.slice();
  for (const input of [{}, { attemptId: null }, { attemptId: 'foreign-attempt' }]) {
    await assert.rejects(f.command(sessionId, 'block', input), { code: 'STALE_SESSION' });
    assert.equal(writes, 0);
    assert.deepEqual(f.library.read(), document);
    assert.deepEqual(f.snapshot(sessionId), before);
    assert.deepEqual(f.calls, pendingCalls);
  }
  await f.command(sessionId, 'block', { attemptId: first.id });
  const replacement = await f.pending(sessionId);
  assert.equal(writes, 1);
  assert.deepEqual(f.library.read().blocks,
    [{ targetId: movie.id, sourceKey: 'a', detailId: 'detail-a', lineId: 'line' }]);
  assert.equal(f.snapshot(sessionId).current, null);
  assert.equal(replacement.candidate.source.key, 'b');
});

test('quality is fixed, keeps current pan while discovery incomplete, then accepts other pan only after discovery', async t => {
  const file = (id, provider) => ({ kind: 'share', fileId: id, shareId: 's', provider, accountRevision: 1 });
  const f = fixture({ data: { async detail(value) { f.calls.push(['detail', value.key]);
    return detail(value.key === 'a' ? [{ name: 'Film.4K', file: file('1', 'quark') },
      { name: 'Film.1080p', file: file('2', 'uc') }] : [{ name: 'Film.720p', file: file('3', 'quark') }]);
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); await f.current(sessionId);
  await f.command(sessionId, 'quality', { quality: '1080p' }); const next = await f.pending(sessionId);
  assert.equal(next.candidate.provider, 'uc'); assert.equal(next.candidate.quality, '1080p');
  assert.equal(f.snapshot(sessionId).searchComplete, true);
  assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a', 'b']);
  await f.command(sessionId, 'media.started', { attemptId: next.id });
  await f.command(sessionId, 'quality', { quality: '4k_hdr' });
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).current.candidate.quality, '1080p');
});

test('rapid operation revokes old pending publish; late address is not prepared and duplicate action shares work', async t => {
  const waiting = deferred(); let first = true;
  const f = fixture({ data: { async resolve(candidate) {
    f.calls.push(['resolve', candidate.name]);
    if (first) { first = false; await waiting.promise; }
    return { url: `https://media.invalid/${encodeURIComponent(candidate.name)}` };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open()); await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'play'); await until(() => f.calls.some(call => call[0] === 'resolve'));
  await f.command(sessionId, 'play');
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, 1);
  await f.command(sessionId, 'quality', { quality: '720p' }); const latest = await f.pending(sessionId);
  waiting.resolve(); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).attempt.id, latest.id);
  assert.equal(f.snapshot(sessionId).attempt.candidate.quality, '720p');
  assert.equal(f.calls.filter(call => call[0] === 'prepare').length, 1);
});

test('release stops downstream scheduling while accepted data settles; stale feedback is rejected', async () => {
  const waiting = deferred();
  const f = fixture({ data: { async search(value) { f.calls.push(['search', value.key]); await waiting.promise;
    return { page: 1, hasMore: false, items: [{ id: 'late', title: 'Film' }] }; } } });
  const { sessionId } = (await f.open({ platform: 'windows' })); await until(() => f.calls.some(call => call[0] === 'search'));
  await f.service.release({ sessionId }); waiting.resolve(); await delay(2);
  assert.equal(f.calls.some(call => call[0] === 'detail' || call[0] === 'prepare'), false);
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: 'late' }), { code: 'NOT_FOUND' });
  await f.service.close();
});

for (const state of ['prepared', 'playing']) {
  for (const keepMedia of state === 'prepared' ? [false, true] : [false]) {
    test(`NFR-02 full release of ${state} input (keepMedia=${keepMedia}) forbids play and prepare before drain`, async t => {
      const gate = deferred();
      const f = fixture({ media: { async release(id) {
        f.released.push(id); await gate.promise; f.resources.delete(id);
      } } });
      t.after(async () => { gate.resolve(); await f.service.close(); });
      const { sessionId } = (await f.open({ platform: state === 'prepared' ? 'ios' : 'windows' }));
      const first = await f.pending(sessionId);
      if (state === 'playing') await f.command(sessionId, 'media.started', { attemptId: first.id });
      const receipt = f.service.attempt({ sessionId, attemptId: first.id });
      const stream = f.service.events(sessionId), updates = [];
      stream.body.on('data', bytes => updates.push(bytes.toString()));
      await until(() => updates.length === 1);
      const calls = f.calls.slice();
      let settled = false;
      const releasing = f.service.release({ sessionId, keepMedia }).then(value => { settled = true; return value; });
      assert.throws(() => f.snapshot(sessionId), { code: 'NOT_FOUND' });
      assert.throws(() => f.service.attempt({ sessionId, attemptId: first.id }), { code: 'NOT_FOUND' });
      assert.throws(() => f.service.events(sessionId), { code: 'NOT_FOUND' });
      await Promise.all(['play', 'prepare'].map(action =>
        assert.rejects(f.command(sessionId, action), { code: 'NOT_FOUND' })));
      (await f.service.setSettings(f.service.settings()));
      await until(() => stream.body.readableEnded);
      assert.equal(settled, false);
      assert.equal(updates.length, 1, 'the released page publishes no stopped or restarted snapshot');
      assert.deepEqual(f.calls, calls, 'rejected commands and settings updates schedule no new work');
      assert.deepEqual(f.released, [receipt.id]);
      assert.deepEqual([...f.resources], [receipt.id], 'release must wait for the real media owner');
      gate.resolve();
      assert.deepEqual(await releasing, { released: true });
      assert.equal(f.resources.size, 0);
      assert.deepEqual(f.released, [receipt.id]);
      assert.throws(() => f.snapshot(sessionId), { code: 'NOT_FOUND' });
    });
  }
}

test('NFR-02 keepMedia retires the pending input but retains the exact current owner and feedback', async t => {
  const gate = deferred();
  const f = fixture({ media: { async release(id) {
    f.released.push(id); await gate.promise; f.resources.delete(id);
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId), current = f.service.attempt({ sessionId, attemptId: first.id });
  await f.command(sessionId, 'switch');
  const replacement = await f.pending(sessionId);
  const pending = f.service.attempt({ sessionId, attemptId: replacement.id });
  const resolves = f.calls.filter(call => call[0] === 'resolve').length;
  const retaining = f.service.release({ sessionId, keepMedia: true });
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.deepEqual(f.released, [pending.id]);
  assert.equal(f.resources.has(current.id), true);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 25, duration: 100, paused: true });
  await f.command(sessionId, 'play');
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.equal(f.snapshot(sessionId).current.position, 25);
  assert.equal(f.snapshot(sessionId).current.paused, true);
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, resolves);
  gate.resolve();
  assert.deepEqual(await retaining, { retained: true });
  assert.deepEqual([...f.resources], [current.id]);
  assert.equal(f.service.attempt({ sessionId, attemptId: first.id }).id, current.id);
  await f.service.release({ sessionId });
  assert.deepEqual(f.released, [pending.id, current.id]);
  assert.equal(f.resources.size, 0);
});

for (const failed of [false, true]) {
  test(`NFR-02 close joins a full session release already draining (failed=${failed})`, async t => {
    const gate = deferred(), failure = error('MEDIA_UPSTREAM_ERROR');
    const f = fixture({ media: { async release(id) {
      f.released.push(id); await gate.promise; f.resources.delete(id);
      if (failed) throw failure;
    } } });
    t.after(async () => { gate.resolve(); await Promise.allSettled([f.service.close()]); });
    const { sessionId } = (await f.open({ platform: 'windows' }));
    const first = await f.pending(sessionId), receipt = f.service.attempt({ sessionId, attemptId: first.id });
    const outcomes = [];
    const releasing = f.service.release({ sessionId }).then(
      value => { outcomes.push('release'); return { value }; },
      error => { outcomes.push('release'); return { error }; });
    const closing = f.service.close().then(
      value => { outcomes.push('close'); return { value }; },
      error => { outcomes.push('close'); return { error }; });
    await delay(2);
    assert.deepEqual(outcomes, [], 'neither caller may complete while the native owner is still draining');
    assert.deepEqual(f.released, [receipt.id], 'close must not release the same lease a second time');
    assert.equal(f.resources.has(receipt.id), true);
    gate.resolve();
    const [released, closed] = await Promise.all([releasing, closing]);
    if (failed) {
      assert.equal(released.error, failure);
      assert.equal(closed.error, failure);
    } else {
      assert.deepEqual(released, { value: { released: true } });
      assert.deepEqual(closed, { value: undefined });
    }
    assert.equal(f.resources.size, 0);
    assert.deepEqual(f.released, [receipt.id]);
  });
}

for (const ending of ['stop', 'release']) {
  for (const phase of ['accounts', 'prepare']) {
    test(`E22 ${ending} during ${phase} keeps finite data but schedules no later transfer or address`, async t => {
      const gate = deferred(), entered = deferred(), calls = [], resolutions = [];
      const selectedFile = { kind: 'share', provider: 'quark', accountRevision: 2,
        shareId: 'synthetic-share', fileId: 'selected-file', name: 'Film.1080p.mkv' };
      const result = value => ({ ok: true, value });
      let waiting = true, inFlightSignal;
      async function pause(at, signal) {
        if (at !== phase || !waiting) return;
        waiting = false; inFlightSignal = signal; entered.resolve();
        await gate.promise;
      }
      const modules = new Map([
        ['cat', { methods: {
          'cat.script.status': async () => result({ instances: [{ installationId: 'install-a',
            generation: 1, state: 'ready', sites: [{ key: 'a' }] }] }),
          'cat.script.invoke': async ({ operation }) => {
            assert.equal(operation, 'detail');
            return result({ list: [{ vod_id: 'actual', lines: [{ id: 'quark', provider: 'quark',
              episodes: [{ id: 'selected-file', name: selectedFile.name, file: selectedFile }] }] }] });
          },
        } }],
        ['pan', { methods: {
          'pan.settings.get': async (_, { signal }) => {
            calls.push('accounts'); await pause('accounts', signal);
            return result({ accounts: [{ provider: 'quark', revision: 2, configured: true }] });
          },
          'pan.prepare': async ({ file }, { signal }) => {
            calls.push('prepare'); await pause('prepare', signal);
            return result({ file: { ...file, kind: 'stored', fileId: 'stored-selected-file' } });
          },
          'pan.play': async ({ file, mode }) => {
            calls.push('play');
            assert.equal(file.fileId, 'stored-selected-file');
            return result({ url: 'https://media.invalid/selected-file', mode, file });
          },
        } }],
      ]);
      const cache = createPlaybackCache(), data = createPlaybackData({ modules, cache });
      const f = fixture({ data: { ...data, resolve(...args) {
        const work = data.resolve(...args);
        resolutions.push(work.then(value => ({ value }), failure => ({ failure })));
        return work;
      } } });
      t.after(async () => { gate.resolve(); await f.service.close(); await data.close(); await cache.close(); });
      const input = { platform: 'windows', target: { id: 'source:a:actual', kind: 'source',
        title: 'Film', source: { key: 'a', detailId: 'actual' } }, sources: [source('a')] };
      const { sessionId } = (await f.open(input));
      await entered.promise;
      if (ending === 'stop') await f.command(sessionId, 'stop');
      else await f.service.release({ sessionId });
      assert.equal(inFlightSignal.aborted, false);
      gate.resolve();
      assert.equal((await resolutions[0]).failure?.code, 'CANCELLED');
      assert.equal(inFlightSignal.aborted, false);
      assert.deepEqual(calls, phase === 'accounts' ? ['accounts'] : ['accounts', 'prepare']);
      assert.equal(f.calls.some(call => call[0] === 'prepare'), false);
      if (ending === 'stop') {
        assert.equal(f.snapshot(sessionId).stage, 'stopped');
        assert.equal(f.snapshot(sessionId).attempt, null);
      } else assert.throws(() => f.snapshot(sessionId), { code: 'NOT_FOUND' });
      const resumed = ending === 'stop' ? sessionId : (await f.open(input)).sessionId;
      if (ending === 'stop') await f.command(sessionId, 'play');
      await f.pending(resumed);
      assert.equal(calls.filter(value => value === 'prepare').length, 1,
        'a transfer that was already in flight remains reusable after an explicit restart');
      assert.equal(calls.filter(value => value === 'play').length, 1);
    });
  }
}

test('E22 stopping source status prevents detail but an explicit browse after stop is still allowed', async t => {
  const gate = deferred(), entered = deferred(), calls = [], details = [];
  let waiting = true, inFlightSignal;
  const result = value => ({ ok: true, value });
  const cache = createPlaybackCache(), data = createPlaybackData({ cache, modules: new Map([
    ['cat', { methods: {
      'cat.script.status': async (_, { signal }) => {
        calls.push('status');
        if (waiting) { waiting = false; inFlightSignal = signal; entered.resolve(); await gate.promise; }
        return result({ instances: [{ installationId: 'install-a', generation: 1,
          state: 'ready', sites: [{ key: 'a' }] }] });
      },
      'cat.script.invoke': async ({ operation }) => {
        calls.push(operation);
        assert.equal(operation, 'detail');
        return result({ list: [{ vod_id: 'actual', vod_play_from: 'direct', vod_play_url: 'Film$actual-file' }] });
      },
    } }],
  ]) });
  const f = fixture({ data: { ...data, detail(...args) {
    const work = data.detail(...args);
    details.push(work.then(value => ({ value }), failure => ({ failure })));
    return work;
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); await data.close(); await cache.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'source:a:actual', kind: 'source',
    title: 'Film', source: { key: 'a', detailId: 'actual' } }, sources: [source('a')] }));
  await entered.promise;
  await f.command(sessionId, 'stop');
  assert.equal(inFlightSignal.aborted, false);
  gate.resolve();
  assert.equal((await details[0]).failure?.code, 'CANCELLED');
  assert.deepEqual(calls, ['status']);
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  assert.deepEqual(calls, ['status', 'status', 'detail']);
  assert.equal(f.snapshot(sessionId).attempt, null);
});

test('E22 a released metadata page caches the in-flight association search without fetching its detail', async t => {
  const gate = deferred(), entered = deferred(), associations = [], calls = [];
  const result = value => ({ ok: true, value });
  let inFlightSignal;
  const cache = createPlaybackCache(), data = createPlaybackData({ cache, modules: new Map([
    ['tmdb', { methods: {
      'tmdb.data.settings.get': async () => result({ revision: 'tmdb-current' }),
      'tmdb.data.request': async () => result({ ...tmdbAppended, id: 17, title: 'Film', release_date: '2020-01-01',
        genres: [], runtime: 100 }),
    } }],
    ['douban', { methods: {
      'douban.data.settings.get': async () => result({ revision: 'douban-current' }),
      'douban.data.search': async (_, { signal }) => {
        calls.push('search'); inFlightSignal = signal; entered.resolve(); await gate.promise;
        return result({ subjects: { items: [{ target_type: 'movie', target_id: '51',
          target: { id: '51', title: 'Film', year: '2020' } }] } });
      },
      'douban.data.request': async () => {
        calls.push('detail');
        return result({ id: '51', title: 'Film', year: '2020', intro: '', genres: [] });
      },
    } }],
  ]) });
  const f = fixture({ data: { ...data, metadataSources(...args) {
    const work = data.metadataSources(...args);
    associations.push(work.then(value => ({ value }), failure => ({ failure })));
    return work;
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); await data.close(); await cache.close(); });
  const { sessionId } = (await f.open({ sources: [] }));
  await entered.promise;
  await f.service.release({ sessionId });
  assert.equal(inFlightSignal.aborted, false);
  gate.resolve();
  assert.equal((await associations[0]).failure?.code, 'CANCELLED');
  assert.deepEqual(calls, ['search']);
  const resumed = (await f.open({ sources: [] }));
  await until(() => f.snapshot(resumed.sessionId).metadataSources.length === 2);
  assert.deepEqual(calls, ['search', 'detail'], 'the completed search is reused, not requested again');
});

test('manual failure counts real bootstrap requests, cancellation is not a failure and third failure disables', async t => {
  let failure = 'CANCELLED';
  const f = fixture({ data: { async detail(value, id) { f.calls.push(['detail', value.key, id]);
    throw error(failure); } } }); t.after(() => f.service.close());
  f.library.update(document => { document.manual.push({ id: 'm', targetId: movie.id, sourceKey: 'a',
    detailId: 'manual', failureCount: 0, enabled: true, autoDisable: true }); });
  const { sessionId } = (await f.open({ search: [{ sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 1, completed: true }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'play'); await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.library.read().manual[0].failureCount, 0);
  failure = 'UPSTREAM_HTTP';
  for (let index = 1; index <= 3; index++) {
    await f.command(sessionId, 'play'); await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.library.read().manual[0].failureCount, index);
  }
  assert.equal(f.library.read().manual[0].enabled, false);
});

test('preview uses metadata dates, persists per title, and never fabricates an episode from filenames', async t => {
  const future = { ...metadata, status: 'Returning Series', next_episode_to_air: { season_number: 1,
    episode_number: 2, air_date: '2026-11-10' }, seasons: [{ season_number: 1, episode_count: 2,
    episodes: [{ season_number: 1, episode_number: 1, air_date: '2026-11-01' },
      { season_number: 1, episode_number: 2, air_date: '2026-11-10' }] }] };
  const f = fixture({ data: { async targetMetadata() { return future; }, async seasonMetadata() { return future.seasons[0]; } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv })); await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => row.episode), [1]);
  await assert.rejects(f.command(sessionId, 'select.episode', { episode: { season: 1, episode: 2 } }), { code: 'NO_MATCH' });
  await f.command(sessionId, 'preview', { enabled: true });
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => row.episode), [1, 2]);
  assert.deepEqual(f.library.read().previewTargets, [tv.id]);
  const second = (await f.open({ target: tv })); await until(() => f.snapshot(second.sessionId).stage === 'idle');
  assert.equal(f.snapshot(second.sessionId).previewEpisodes, true);
  await assert.rejects(f.command(sessionId, 'select.episode', { episode: { season: 1, episode: 99 } }), { code: 'NO_MATCH' });
});

test('first episode cannot be invented when metadata supplies no reliable map', async t => {
  const f = fixture({ data: { async targetMetadata() { return { id: 19 }; } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios', target: tv }));
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).error, 'NO_EPISODE_MAPPING');
  assert.equal(f.calls.some(call => ['search', 'resolve'].includes(call[0])), false);
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
});

test('shortvideo prepares known neighbor address and real media bytes without all-source discovery', async t => {
  const f = fixture({ data: { async detail(value) { f.calls.push(['detail', value.key]);
    return detail(['Series.S01E01.1080p', 'Series.S01E02.1080p']); } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: tv })); await f.current(sessionId);
  const count = f.calls.filter(call => call[0] === 'search').length;
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => f.snapshot(sessionId).prefetch.some(value => value.state === 'buffered'));
  assert.equal(f.calls.filter(call => call[0] === 'prefetch').length, 1);
  assert.equal(f.calls.filter(call => call[0] === 'search').length, count);
  const resolves = f.calls.filter(call => call[0] === 'resolve').length;
  await f.command(sessionId, 'next'); const next = await f.pending(sessionId);
  assert.equal(next.candidate.episode, 2); assert.equal(next.position, 0);
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, resolves);
});

test('watch reporting is opt-in and driven only by output/progress/stopped, bound to original attempt', async t => {
  const f = fixture({ data: { async resolve(candidate) { f.calls.push(['resolve', candidate.name]);
    return { url: 'https://media.invalid/video', watchReport: true }; } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios' })); const attempt = await f.pending(sessionId);
  assert.equal(f.calls.some(call => call[0] === 'report'), false);
  await f.command(sessionId, 'play'); await f.command(sessionId, 'media.started', { attemptId: attempt.id }); await delay(2);
  assert.equal(f.calls.filter(call => call[0] === 'report').length, 1);
  await f.command(sessionId, 'media.progress', { attemptId: attempt.id, position: 1, duration: 60, paused: false });
  await delay(2); assert.equal(f.calls.filter(call => call[0] === 'report').length, 1);
  f.advance(12_000);
  await f.command(sessionId, 'media.progress', { attemptId: attempt.id, position: 13, duration: 60, paused: false });
  await f.command(sessionId, 'stop'); await delay(2);
  const reports = f.calls.filter(call => call[0] === 'report').map(call => call[2]);
  assert.deepEqual(reports.map(value => value.event), ['started', 'progress', 'stopped']);
  assert.ok(reports.every(value => value.sessionId === attempt.id));
  assert.equal(reports[2].positionSeconds, 13);
});

test('snapshots never expose playback addresses/headers/keys/original private file ids; attempt is separate', async t => {
  const f = fixture({ data: { async detail() { return detail([{ id: 'https://secret.invalid?token=private', name: 'Film', index: 0 }]); },
    async resolve() { return { url: 'https://private.invalid?token=secret', headers: { Cookie: 'private-cookie' },
      drm: { keys: { privatekid: 'privatekey' } } }; } }, media: { async prepare(value) {
      return { id: 'lease', url: 'http://127.0.0.1:1234/media/hidden', drm: value.drm }; } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const attempt = await f.pending(sessionId);
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  const wire = JSON.stringify(f.snapshot(sessionId));
  for (const secret of ['secret.invalid', 'private.invalid', 'token=', 'private-cookie', 'privatekey', '/media/hidden'])
    assert.equal(wire.includes(secret), false, secret);
  const receipt = f.service.attempt({ sessionId, attemptId: attempt.id });
  assert.equal(receipt.url, 'http://127.0.0.1:1234/media/hidden');
  assert.equal(receipt.drm.keys.privatekid, 'privatekey');
});

test('SSE reconnect sends current snapshot, cancellation wakes an idle stream without closing business', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open()); await until(() => f.snapshot(sessionId).stage === 'idle');
  const stream = f.service.events(sessionId, f.snapshot(sessionId).sequence);
  const received = once(stream.body, 'data');
  const [bytes] = await received;
  assert.match(bytes.toString(), /event: snapshot/);
  assert.match(bytes.toString(), new RegExp(sessionId));
  const done = once(stream.body, 'close'); await stream.dispose(); await done;
  assert.equal(f.snapshot(sessionId).stage, 'idle');
  await f.command(sessionId, 'play'); assert.ok(await f.pending(sessionId));
});

test('media receipt release is scoped, idempotent, and safe after whole session release', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const attempt = await f.pending(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: attempt.id });
  await assert.rejects(f.service.releaseMedia({ sessionId, attemptId: attempt.id, resourceId: 'foreign' }), { code: 'STALE_SESSION' });
  await f.service.releaseMedia({ sessionId, attemptId: attempt.id, resourceId: receipt.id });
  await f.service.releaseMedia({ sessionId, attemptId: attempt.id, resourceId: receipt.id });
  assert.equal(f.resources.size, 0);
  await f.service.release({ sessionId });
  assert.deepEqual(await f.service.releaseMedia({ sessionId, attemptId: attempt.id, resourceId: receipt.id }), { released: true });
});

test('site purification changes smart eligibility, not the existence of a source or ordinary browsing', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('sports', '体育源'), source('b', '影院')] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.key, 'b');
  assert.equal(f.calls.some(call => call[0] === 'search' && call[1] === 'sports'), false);
  (await f.service.setSettings({ ...defaultPreferences(), siteCleanKeywords: [] }));
  await f.command(sessionId, 'search.more');
  await until(() => f.calls.some(call => call[0] === 'search' && call[1] === 'sports'));
  assert.ok(f.snapshot(sessionId).search.some(value => value.sourceKey === 'sports'));
});

test('raw numeric identities remain numeric for detail, line, and exact adjacent episode playback', async t => {
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', value.key, id]);
    return detail([{ id: 0, name: 'One' }, { id: 2, name: 'Two' }], 0);
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'raw-zero', kind: 'source',
    title: 'Raw', source: { key: 'a', detailId: 0 } } }));
  const first = await f.current(sessionId);
  assert.equal(first.candidate.source.detailId, 0);
  assert.equal(first.candidate.lineId, 0);
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail'), [['detail', 'a', 0]]);
  await f.command(sessionId, 'next'); const next = await f.pending(sessionId);
  assert.equal(next.candidate.name, 'Two');
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
});

test('known special episode zero is selectable without pretending it has a global episode', async t => {
  const special = { season_number: 0, episodes: [{ season_number: 0, episode_number: 0, name: 'Prologue' }] };
  const value = { ...metadata, seasons: [special, ...metadata.seasons] };
  const f = fixture({ data: {
    async targetMetadata() { return value; },
    async seasonMetadata(target, season) { return value.seasons.find(row => row.season_number === season); },
    async detail() { return detail([{ id: 'prologue', name: 'Series Prologue', season: 0, episode: 0 }]); },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv })); await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'select.episode', { episode: { season: 0, episode: 0 } });
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.season, 0); assert.equal(attempt.candidate.episode, 0);
  assert.equal(attempt.candidate.globalEpisode, undefined);
});

test('an overlapping provider page is not an error or truncation of its later results', async t => {
  const f = fixture({ data: { async search(value, query, page) {
    f.calls.push(['search', value.key, page]);
    return { page, hasMore: page < 3, explicitPageCount: true, pageCount: 3,
      items: [{ id: page === 3 ? 'last' : 'overlap', title: query }] };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  for (let page = 1; page <= 3; page++) {
    await f.command(sessionId, 'search.more');
    await until(() => f.snapshot(sessionId).search[0].page === page);
    assert.equal(f.snapshot(sessionId).search[0].error, null);
  }
  assert.equal(f.snapshot(sessionId).sources.length, 2);
  assert.equal(f.snapshot(sessionId).searchComplete, true);
  assert.equal(f.calls.some(call => call[0] === 'detail'), false);
});

test('metadata without episode coordinates cannot silently turn explicit play into an arbitrary episode', async t => {
  const f = fixture({ data: { async targetMetadata() { return { id: 19 }; } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv })); await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'play'); await until(() => f.snapshot(sessionId).error === 'NO_EPISODE_MAPPING');
  assert.equal(f.calls.some(call => ['search', 'detail', 'resolve'].includes(call[0])), false);
});

test('native receipt release clears the pending state so a later play does not reuse revoked media', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios' })); const before = await f.pending(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: before.id });
  await f.service.releaseMedia({ sessionId, attemptId: before.id, resourceId: receipt.id });
  assert.equal(f.snapshot(sessionId).attempt, null);
  await f.command(sessionId, 'play'); const after = await f.pending(sessionId);
  assert.notEqual(after.id, before.id);
  assert.equal(f.resources.has(f.service.attempt({ sessionId, attemptId: after.id }).id), true);
});

test('iOS automatic preparation preserves an explicitly selected S1E2 target on reopen', async t => {
  const f = fixture({ data: { async detail() { return detail(['Series.S01E01', 'Series.S01E02']); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios', target: { ...tv, season: 1, episode: 2, globalEpisode: 2 } }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.season, 1); assert.equal(attempt.candidate.episode, 2);
  assert.equal(attempt.candidate.globalEpisode, 2); assert.equal(attempt.prepareOnly, true);
  assert.deepEqual(f.snapshot(sessionId).target, { ...tv, season: 1, episode: 2, globalEpisode: 2 });
  assert.equal(f.library.read().history.length, 0);
});

for (const { platform = 'ios', name, selected, expected, historyEpisode, position = 0 } of [
  { name: 'an unselected card ignores non-first library history and prepares the first real regular episode',
    selected: {}, expected: { season: 1, episode: 1, globalEpisode: 1 },
    historyEpisode: { season: 2, episode: 3, globalEpisode: 5 } },
  { name: 'an explicit S2E3 reopen retains its selected target',
    selected: { season: 2, episode: 3, globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 } },
  { name: 'an explicit season-zero special reopen retains its selected target',
    selected: { season: 0, episode: 1 }, expected: { season: 0, episode: 1 } },
  { name: 'an explicit season-zero episode-zero reopen retains its selected target',
    selected: { season: 0, episode: 0 }, expected: { season: 0, episode: 0 } },
  { platform: 'windows', name: 'an explicit S2E3 target overrides old S1E2 history and starts at zero',
    selected: { season: 2, episode: 3, globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 },
    historyEpisode: { season: 1, episode: 2, globalEpisode: 2 } },
  { platform: 'windows', name: 'an explicit S2E3 target resumes position only from its own episode history',
    selected: { season: 2, episode: 3, globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 },
    historyEpisode: { season: 2, episode: 3, globalEpisode: 5 }, position: 67 },
  { platform: 'windows', name: 'an unselected card preserves its default historical episode and position',
    selected: {}, expected: { season: 1, episode: 2, globalEpisode: 2 },
    historyEpisode: { season: 1, episode: 2, globalEpisode: 2 }, position: 67 },
  { name: 'NCR-01 an explicit global5 reopen resolves S2E3 before the default first episode',
    selected: { globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 } },
  { platform: 'windows', name: 'NCR-01 an explicit global5 target overrides S1E2 history without borrowing its position',
    selected: { globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 },
    historyEpisode: { season: 1, episode: 2, globalEpisode: 2 } },
  { platform: 'windows', name: 'NCR-01 an explicit global5 target resumes only its resolved S2E3 history',
    selected: { globalEpisode: 5 }, expected: { season: 2, episode: 3, globalEpisode: 5 },
    historyEpisode: { season: 2, episode: 3, globalEpisode: 5 }, position: 67 },
]) {
  test(`I2/E20/E21/E33/K3 real data ${platform} ${name}`, async t => {
    const series = { ...metadata, ...tmdbAppended, name: 'Series', genres: [], number_of_seasons: 2, seasons: [
      { season_number: 0, episode_count: 2, episodes: [
        { season_number: 0, episode_number: 0, name: 'Prologue', air_date: '2020-01-01' },
        { season_number: 0, episode_number: 1, name: 'Special', air_date: '2020-01-01' },
      ] },
      ...structuredClone(metadata.seasons),
      { season_number: 2, episode_count: 3, episodes: [1, 2, 3].map(episode => ({
        season_number: 2, episode_number: episode, name: `Second season ${episode}`, air_date: '2020-02-01',
      })) },
    ] };
    const files = [
      { id: 's0e0', name: 'Series Prologue', season: 0, episode: 0 },
      { id: 's0e1', name: 'Series.S00E01.4K', season: 0, episode: 1 },
      { id: 's1e2', name: 'Series.S01E02.4K', season: 1, episode: 2 },
      { id: 's1e1', name: 'Series.S01E01.1080p', season: 1, episode: 1 },
      { id: 's2e3', name: 'Series.S02E03.1080p', season: 2, episode: 3 },
    ];
    const owner = runtimeDataFixture(({ operation, payload, siteId }) => {
      assert.equal(siteId, 'a');
      if (operation === 'detail') {
        assert.equal(payload.id, 0);
        return { vod_id: 0, vod_name: 'Series', lines: [{ id: 'direct', episodes: files }] };
      }
      assert.equal(operation, 'play'); assert.equal(payload.id, `s${expected.season}e${expected.episode}`);
      return { url: 'https://media.invalid/selected-series-episode' };
    }, ({ path }) => {
      if (path === '/3/tv/19') return structuredClone(series);
      assert.equal(path, `/3/tv/19/season/${expected.season}`);
      return structuredClone(series.seasons.find(row => row.season_number === expected.season));
    });
    const f = fixture({ data: owner.data });
    t.after(async () => { await f.service.close(); await owner.close(); });
    if (historyEpisode) f.library.update(document => {
      document.history.push({ targetId: tv.id, title: 'Series', sourceKey: 'a', detailId: 0,
        candidateId: `previous-s${historyEpisode.season}e${historyEpisode.episode}`, lineId: 'direct', episode: historyEpisode,
        position: 67, duration: 100, paused: true, updatedAt: 1 });
    });
    const history = f.library.read().history, target = { ...tv, ...selected };
    const { sessionId } = (await f.open({ platform, target, sources: [source('a')],
      results: [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: 'Series' }] }] }));
    const attempt = await f.pending(sessionId), snapshot = f.snapshot(sessionId);
    assert.equal(attempt.prepareOnly, platform === 'ios');
    assert.equal(attempt.candidate.season, expected.season);
    assert.equal(attempt.candidate.episode, expected.episode);
    assert.equal(attempt.candidate.globalEpisode, expected.globalEpisode);
    assert.deepEqual(snapshot.target, { ...tv, ...expected });
    assert.equal(snapshot.stage, platform === 'ios' ? 'prepared' : 'pending'); assert.equal(snapshot.current, null);
    assert.equal(attempt.position, position); assert.equal(attempt.paused, false);
    assert.deepEqual(f.library.read().history, history);
    assert.deepEqual(owner.calls.map(row => row.operation), ['detail', 'play']);
    assert.deepEqual(owner.metadataCalls.map(row => row.path),
      ['/3/tv/19', `/3/tv/19/season/${expected.season}`]);
    assert.equal(f.calls.some(row => row[0] === 'prefetch'), false);
  });
}

for (const platform of ['ios', 'windows']) {
  test(`NCR-01 real data ${platform} rejects an explicit global5 without a reliable earlier-season offset`, async t => {
    const series = { ...tmdbAppended, id: 19, name: 'Series', genres: [], status: 'Ended',
      seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 3, episode_count: 5 }] };
    const owner = runtimeDataFixture(() => { assert.fail('An unmapped global target must not request a source'); },
      ({ path }) => { assert.equal(path, '/3/tv/19'); return series; });
    const f = fixture({ data: owner.data });
    t.after(async () => { await f.service.close(); await owner.close(); });
    f.library.update(document => {
      document.history.push({ targetId: tv.id, episode: { season: 3, episode: 1 },
        sourceKey: 'a', detailId: 0, position: 67 });
    });
    const target = { ...tv, globalEpisode: 5 };
    const { sessionId } = (await f.open({ platform, target, sources: [source('a')] }));
    await until(() => f.snapshot(sessionId).error === 'NO_EPISODE_MAPPING');
    const snapshot = f.snapshot(sessionId);
    assert.deepEqual(snapshot.target, target);
    assert.equal(snapshot.attempt, null); assert.equal(snapshot.current, null);
    assert.deepEqual(owner.calls, []);
    assert.deepEqual(owner.metadataCalls.map(row => row.path), ['/3/tv/19']);
    assert.equal(f.calls.some(row => ['prepare', 'resolve', 'prefetch'].includes(row[0])), false);
  });

  for (const reliable of [true, false]) {
    test(`NCR-01 ${platform} late Douban mapping respects explicit global5 instead of first/history (reliable=${reliable})`, async t => {
      const gate = deferred();
      const target = { ...tv, metadataProvider: 'douban', globalEpisode: 5 };
      const f = fixture({ data: {
        async targetMetadata() { return { id: 19, title: 'Series' }; },
        async metadataSources() { return gate.promise; },
        async detail(value, id) {
          f.calls.push(['detail', value.key, id]);
          return detail(['Series.S01E01', 'Series.S02E01', 'Series.S02E03']);
        },
      } });
      t.after(async () => { gate.resolve([]); await f.service.close(); });
      f.library.update(document => {
        document.history.push({ targetId: tv.id, sourceKey: 'a', detailId: 'current', lineId: 'line',
          episode: reliable ? { season: 2, episode: 3, globalEpisode: 5 } : { season: 3, episode: 1 },
          position: 67 });
      });
      const { sessionId } = (await f.open({ platform, target, sources: [source('a')],
        results: [{ sourceKey: 'a', items: [{ id: 'current', title: 'Series' }] }],
        search: [{ sourceKey: 'a', page: 1, completed: true }] }));
      await until(() => f.snapshot(sessionId).error === 'NO_EPISODE_MAPPING');
      assert.deepEqual(f.snapshot(sessionId).target, target);
      assert.equal(f.calls.some(row => ['detail', 'resolve', 'prepare'].includes(row[0])), false);
      gate.resolve([{ provider: 'douban', metadataId: 19, kind: 'tv', title: 'Series',
        metadata: { id: 19, title: 'Series' },
        seasons: reliable ? [{ season: 1, episodeCount: 2 }, { season: 2, episodeCount: 3 }]
          : [{ season: 1, episodeCount: 2 }, { season: 3, episodeCount: 5 }] }]);
      await until(() => f.snapshot(sessionId).metadata?.doubanSeasons);
      if (reliable) {
        const attempt = await f.pending(sessionId);
        assert.deepEqual(f.snapshot(sessionId).target, { ...target, season: 2, episode: 3 });
        assert.equal(attempt.candidate.season, 2); assert.equal(attempt.candidate.episode, 3);
        assert.equal(attempt.candidate.globalEpisode, 5);
        assert.equal(attempt.prepareOnly, platform === 'ios');
        assert.equal(attempt.position, platform === 'windows' ? 67 : 0);
        assert.equal(attempt.paused, false);
      } else {
        await delay(2); await delay(2);
        assert.deepEqual(f.snapshot(sessionId).target, target);
        assert.equal(f.snapshot(sessionId).stage, 'unavailable');
        assert.equal(f.snapshot(sessionId).error, 'NO_EPISODE_MAPPING');
        assert.equal(f.snapshot(sessionId).attempt, null);
        assert.equal(f.calls.some(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])), false);
      }
    });
  }
}

test('unrelated source changes preserve live media; exact source revocation retires only its current lease', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const first = await f.current(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: first.id });
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-b' },
    [{ installationId: 'install-b', generation: 2, state: 'ready', sites: [{ key: 'b', name: 'Updated B' }] }]);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.equal(f.resources.has(receipt.id), true);
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' },
    [{ installationId: 'install-a', generation: 2, state: 'ready', sites: [{ key: 'a', name: 'Updated A' }] }]);
  assert.equal(f.snapshot(sessionId).current, null);
  assert.equal(f.resources.has(receipt.id), false);
  assert.equal(f.snapshot(sessionId).error, 'STALE_SOURCE');
  await f.command(sessionId, 'play'); const next = await f.pending(sessionId);
  assert.equal(next.candidate.source.generation, 2);
});

test('metadata reconfiguration refreshes data without revoking independently authorized actual media', async t => {
  let revision = 1;
  const f = fixture({ data: { async targetMetadata() { return { id: 17, title: `Version ${revision}` }; } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' })); const current = await f.current(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: current.id }); revision = 2;
  await f.service.invalidate('tmdb');
  await until(() => f.snapshot(sessionId).metadata?.title === 'Version 2');
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.equal(f.resources.has(receipt.id), true);
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, 1);
});

test('source revocation during address resolution prevents the late result from publishing', async t => {
  const waiting = deferred();
  const f = fixture({ data: { async resolve() {
    f.calls.push(['resolve']); await waiting.promise; return { url: 'https://media.invalid/revoked' };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  await until(() => f.calls.some(call => call[0] === 'resolve'));
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' }, []);
  waiting.resolve(); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.calls.some(call => call[0] === 'prepare'), false);
  assert.equal(f.snapshot(sessionId).error, 'STALE_SOURCE');
});

test('late metadata initialization cannot overwrite the replacement configuration', async t => {
  const waiting = deferred(); let requests = 0;
  const f = fixture({ data: { async targetMetadata() {
    requests++;
    return requests === 1 ? waiting.promise : { id: 17, title: 'Current metadata' };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open());
  await f.service.invalidate('tmdb');
  await until(() => f.snapshot(sessionId).metadata?.title === 'Current metadata');
  waiting.resolve({ id: 17, title: 'Retired metadata' }); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).metadata.title, 'Current metadata');
});

test('released provisional receipt still accepts its exact failure and continues without reusing it', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.pending(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: first.id });
  await f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id });
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.throws(() => f.service.attempt({ sessionId, attemptId: first.id }), { code: 'STALE_SESSION' });
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: first.id }), { code: 'STALE_SESSION' });
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  const second = await f.pending(sessionId);
  assert.notEqual(second.id, first.id);
  assert.notEqual(second.candidate.id, first.candidate.id);
  await assert.rejects(f.command(sessionId, 'media.failed', { attemptId: first.id }), { code: 'STALE_SESSION' });
  assert.deepEqual(await f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id }), { released: true });
});

test('physical release before final progress and ended preserves watched state and advances the actual episode', async t => {
  const f = fixture({ data: { async detail() { return detail(['Series.S01E01', 'Series.S01E02']); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: tv }));
  const first = await f.current(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: first.id });
  await f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id });
  assert.equal(f.snapshot(sessionId).current, null);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 59, duration: 60, paused: false });
  await f.command(sessionId, 'media.ended', { attemptId: first.id });
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.episode, 2);
  assert.equal(next.position, 0);
  assert.equal(f.library.read().history[0].ended, true);
  assert.equal(f.library.read().history[0].position, 60);
});

test('last media end is not rewritten as unfinished when the page closes; released input is not a PiP owner', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId), receipt = f.service.attempt({ sessionId, attemptId: first.id });
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 90, duration: 90, paused: false });
  await f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id });
  await f.command(sessionId, 'media.ended', { attemptId: first.id });
  assert.equal(f.snapshot(sessionId).stage, 'ended');
  assert.deepEqual(await f.service.release({ sessionId, keepMedia: true }), { released: true });
  assert.equal(f.library.read().history[0].ended, true);
  assert.equal(f.resources.size, 0);
});

test('library management clears only the selected collection and reports write failure without mutating it', async t => {
  const f = fixture(); t.after(() => f.service.close());
  f.library.update(document => {
    document.history.push({ targetId: movie.id }, { targetId: tv.id });
    document.blocks.push({ targetId: movie.id, sourceKey: 'a', detailId: 0, lineId: 0 });
  });
  f.service.removeLibrary({ kind: 'history', id: movie.id });
  assert.deepEqual(f.library.read().history, [{ targetId: tv.id }]);
  f.failStorage('STORAGE_ERROR');
  assert.throws(() => f.service.removeLibrary({ kind: 'blocks' }), { code: 'STORAGE_ERROR' });
  assert.equal(f.library.read().blocks.length, 1);
  f.failStorage(null);
  f.service.removeLibrary({ kind: 'history', id: null });
  assert.equal(f.library.read().history.length, 0);
  assert.equal(f.library.read().blocks.length, 1);
  f.service.removeLibrary({ kind: 'blocks' });
  assert.equal(f.library.read().blocks.length, 0);
});

test('smart selection ignores another title but menus retain it and explicit browsing remains available', async t => {
  const f = fixture({ data: { async search(value, query, page) {
    f.calls.push(['search', value.key, query, page]);
    return { page, hasMore: false, items: [{ id: 'wrong', title: 'Film 2' }, { id: 'right', title: 'Film' }] };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.detailId, 'right');
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail'), [['detail', 'a', 'right']]);
  const wrong = f.snapshot(sessionId).sources.find(row => row.title === 'Film 2');
  assert.ok(wrong);
  await f.command(sessionId, 'browse', { sourceId: wrong.id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 2);
  assert.ok(f.calls.some(call => call[0] === 'detail' && call[2] === 'wrong'));
  await f.command(sessionId, 'play');
  assert.equal(f.snapshot(sessionId).attempt.candidate.source.detailId, 'right');
});

test('nonmatching search results do not prevent fallback after full discovery of the requested quality', async t => {
  const f = fixture({ data: {
    async search(value, query, page) { return { page, hasMore: false,
      items: [{ id: 'wrong', title: 'Film 2' }, { id: 'right', title: 'Film' }] }; },
    async detail(value, id) { f.calls.push(['detail', value.key, id]);
      return detail([{ name: 'Film.1080p', file: { provider: 'uc', kind: 'share',
        shareId: 'share', fileId: 'file', accountRevision: 1 } }]); },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ sources: [source('a')] })); await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'pan', { provider: 'uc' });
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.provider, 'uc');
  assert.equal(f.snapshot(sessionId).searchComplete, true);
  assert.equal(f.calls.filter(call => call[0] === 'detail' && call[2] === 'wrong').length, 0);
});

test('metadata handoff retains original freshness evidence and never trusts a caller complete boolean', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const entry = { kind: 'metadata', provider: 'tmdb', path: '/3/movie/17', query: { language: 'zh-CN' },
    settings: { revision: 'original-config' }, fetchedAt: 1000, expiresAt: 2000,
    completeness: 'partial', value: { id: 17, title: 'Partial' } };
  const { sessionId } = (await f.open({ metadata: entry.value, metadataComplete: true, cacheEntries: [entry] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.deepEqual(f.calls.filter(call => ['handoff', 'metadata'].includes(call[0])),
    [['handoff', [entry]], ['metadata', movie.id]]);
  assert.deepEqual(f.snapshot(sessionId).metadata, { id: 17 });
});

test('metadata association is independent of primary playback and only verified selectors can be selected', async t => {
  const waiting = deferred();
  const f = fixture({ data: {
    async metadataSources() { return waiting.promise; },
    async targetMetadata(target) { return { id: target.metadataId, title: target.metadataProvider }; },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId);
  assert.equal(f.snapshot(sessionId).metadataSources.length, 1);
  const primary = f.snapshot(sessionId).metadataSources[0];
  waiting.resolve([
    { provider: 'tmdb', metadataId: 17, kind: 'movie', title: 'Film', metadata: { id: 17, title: 'tmdb' } },
    { provider: 'douban', metadataId: '18', kind: 'movie', title: 'Film', metadata: { id: '18', title: 'douban' } },
  ]);
  await until(() => f.snapshot(sessionId).metadataSources.length === 2);
  const other = f.snapshot(sessionId).metadataSources.find(row => row.provider === 'douban');
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: other.id });
  await until(() => f.snapshot(sessionId).metadataSourceId === other.id);
  assert.equal(f.snapshot(sessionId).target.id, movie.id, 'the content identity is not replaced by a display mapping');
  assert.equal(f.snapshot(sessionId).target.metadataProvider, 'douban');
  assert.equal(f.snapshot(sessionId).metadata.title, 'douban');
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id, 'browsing must not stop an independent live input');
  assert.equal(f.snapshot(sessionId).attempt, null);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
  await until(() => f.snapshot(sessionId).metadataSourceId === primary.id);
  assert.equal(f.snapshot(sessionId).metadata.title, 'tmdb');
});

test('metadata source failure stays in the third row and switching back clears it without fabricated episodes', async t => {
  let failDouban = false;
  const f = fixture({ data: {
    async metadataSources() {
      return [
        { provider: 'tmdb', metadataId: 19, kind: 'tv', title: 'Series', metadata },
        { provider: 'douban', metadataId: '20', kind: 'tv', title: 'Series',
          metadata: { id: '20', title: 'Series', episodes_count: 2 }, seasons: [{ season: 1, episodeCount: 2 }] },
      ];
    },
    async targetMetadata(target) {
      if (target.metadataProvider === 'douban' && failDouban) throw error('UPSTREAM_HTTP');
      return structuredClone(metadata);
    },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv }));
  await until(() => f.snapshot(sessionId).stage === 'idle' && f.snapshot(sessionId).metadataSources.length === 2);
  const [primary, other] = f.snapshot(sessionId).metadataSources;
  failDouban = true;
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: other.id });
  await until(() => f.snapshot(sessionId).browsing.error === 'UPSTREAM_HTTP');
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: primary.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).browsing.error === null);
  assert.equal(f.snapshot(sessionId).episodes.length, 2);
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
});

test('lease drain completing after ended cannot overwrite the terminal state', async t => {
  const drain = deferred();
  const f = fixture({ media: { async release(id) { await drain.promise; f.resources.delete(id); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId), receipt = f.service.attempt({ sessionId, attemptId: first.id });
  const releasing = f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id });
  assert.equal(f.snapshot(sessionId).current, null);
  await f.command(sessionId, 'media.ended', { attemptId: first.id });
  drain.resolve(); await releasing;
  assert.equal(f.snapshot(sessionId).stage, 'ended');
});

test('a different installation mutation does not interrupt in-flight matching or discard its search page', async t => {
  const waiting = deferred();
  const f = fixture({ data: { async search(value, query, page) {
    f.calls.push(['search', value.key, page]);
    if (value.key === 'a') await waiting.promise;
    return { page, hasMore: false, items: [{ id: 'found', title: query }] };
  } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  await until(() => f.calls.some(call => call[0] === 'search'));
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-b' },
    [{ installationId: 'install-b', generation: 2, state: 'ready', sites: [{ key: 'b' }] }]);
  waiting.resolve();
  const attempted = await f.pending(sessionId);
  assert.equal(attempted.candidate.source.key, 'a');
  assert.equal(f.snapshot(sessionId).error, null);
  assert.deepEqual(f.calls.filter(call => call[0] === 'search'), [['search', 'a', 1]]);
  assert.equal(f.snapshot(sessionId).search.find(value => value.sourceKey === 'a').completed, true);
});

test('all affected sessions lose native publication authority before the first slow resource drains', async t => {
  const drain = deferred();
  const f = fixture({ media: { async release(id) { await drain.promise; f.resources.delete(id); } } });
  t.after(() => f.service.close());
  const first = (await f.open({ platform: 'windows' })), second = (await f.open({ platform: 'windows' }));
  const a = await f.pending(first.sessionId), b = await f.pending(second.sessionId);
  const invalidation = f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' }, []);
  assert.equal(f.snapshot(first.sessionId).attempt, null);
  assert.equal(f.snapshot(second.sessionId).attempt, null);
  await assert.rejects(f.command(second.sessionId, 'media.started', { attemptId: b.id }), { code: 'STALE_SESSION' });
  assert.throws(() => f.service.attempt({ sessionId: first.sessionId, attemptId: a.id }), { code: 'STALE_SESSION' });
  drain.resolve(); await invalidation;
  assert.equal(f.resources.size, 0);
});

test('browsing expands an immutable shared detail without modifying its cached data', async t => {
  const episode = Object.freeze({ id: 'share', name: 'Share', share: Object.freeze({ provider: 'quark', url: 'https://share.invalid/s' }) });
  const line = Object.freeze({ id: 'quark', label: 'quark', episodes: Object.freeze([episode]) });
  const immutable = Object.freeze({ lines: Object.freeze([line]) });
  const f = fixture({ data: {
    async detail() { return immutable; },
    async expand() { return { id: 'quark', label: 'quark', expanded: true,
      episodes: [{ id: 0, name: 'Film.1080p', index: 0 }] }; },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [{ id: 0, title: 'Film' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  assert.equal(f.snapshot(sessionId).browsing.error, null);
  assert.equal(f.snapshot(sessionId).browsing.episodes[0].name, 'Film.1080p');
  assert.equal(immutable.lines[0].episodes[0].name, 'Share');
});

test('source season selection changes the episode row but the original directory remains complete', async t => {
  const seasons = [1, 2].map(season => ({ season_number: season, episode_count: 1,
    episodes: [{ season_number: season, episode_number: 1, air_date: '2020-01-01' }] }));
  const f = fixture({ data: {
    async targetMetadata() { return { id: 19, status: 'Ended', seasons }; },
    async seasonMetadata(target, season) { return seasons.find(row => row.season_number === season); },
    async detail() { return detail(['Series.S01E01', 'Series.S02E01']); },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ target: tv, results: [{ sourceKey: 'a', items: [{ id: 'series', title: 'Series' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.originalEpisodes?.length === 2);
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes.map(row => row.season), [1]);
  await f.command(sessionId, 'browse.season', { season: 2 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes.map(row => row.season), [2]);
  assert.equal(f.snapshot(sessionId).browsing.originalEpisodes.length, 2);
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
});

test('initial metadata failure cannot be bypassed by a play command queued before initialization', async t => {
  const gate = deferred();
  const f = fixture({ data: { async targetMetadata() { return gate.promise; } } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open());
  await f.command(sessionId, 'play');
  gate.reject(error('UPSTREAM_HTTP'));
  await until(() => f.snapshot(sessionId).stage === 'error');
  assert.equal(f.snapshot(sessionId).error, 'UPSTREAM_HTTP');
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
});

test('stop prevents a late metadata association from restarting automatic iOS preparation', async t => {
  const gate = deferred();
  const f = fixture({ data: {
    async targetMetadata() { return { id: 19 }; },
    async metadataSources() { return gate.promise; },
  } }); t.after(() => f.service.close());
  const target = { ...tv, metadataProvider: 'douban' };
  const { sessionId } = (await f.open({ platform: 'ios', target }));
  await until(() => f.snapshot(sessionId).error === 'NO_EPISODE_MAPPING');
  await f.command(sessionId, 'stop');
  gate.resolve([{ provider: 'douban', metadataId: 19, kind: 'tv', title: 'Series',
    metadata: { id: 19 }, seasons: [{ season: 1, episodeCount: 2 }] }]);
  await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).stage, 'stopped');
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
});

test('promoting a warming neighbor cancels only prefetch, keeps its lease and never takes it back after completion', async t => {
  const warm = deferred();
  const f = fixture({ data: { async detail() { return detail(['Series.S01E01', 'Series.S01E02']); } },
    media: {
      async prefetch(id) { f.calls.push(['prefetch', id]); return warm.promise; },
      async cancelPrefetch(id) { f.calls.push(['cancelPrefetch', id]); warm.reject(error('MEDIA_CANCELLED')); },
    } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: tv })); await f.current(sessionId);
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => f.calls.some(call => call[0] === 'prefetch'));
  const resource = f.calls.find(call => call[0] === 'prefetch')[1];
  await f.command(sessionId, 'next');
  const next = await f.pending(sessionId);
  assert.equal(f.service.attempt({ sessionId, attemptId: next.id }).id, resource);
  assert.equal(f.resources.has(resource), true);
  assert.equal(f.calls.filter(call => call[0] === 'resolve' && call[2] === 'Series.S01E02').length, 1);
  await delay(2);
  assert.equal(f.snapshot(sessionId).attempt.id, next.id);
});

test('closing immediately forbids new pages while waiting for native resource drain', async () => {
  const gate = deferred();
  const f = fixture({ media: { async release(id) { await gate.promise; f.resources.delete(id); } } });
  const { sessionId } = (await f.open({ platform: 'windows' })); await f.pending(sessionId);
  const closing = f.service.close();
  (await assert.rejects(async () => (await f.open()), { code: 'CLOSED' }));
  await assert.rejects(f.command(sessionId, 'play'), { code: 'CLOSED' });
  gate.resolve(); await closing;
  assert.equal(f.resources.size, 0);
});

test('an old episode ending during a newer selection cannot skip that selected episode', async t => {
  const gate = deferred(), entered = deferred();
  const series = { ...metadata, seasons: [{ season_number: 1, episode_count: 3,
    episodes: [1, 2, 3].map(episode_number => ({ season_number: 1, episode_number, air_date: '2020-01-01' })) }] };
  const f = fixture({ data: {
    async targetMetadata() { return series; },
    async seasonMetadata() { return series.seasons[0]; },
    async detail() { return detail(['Series.S01E01', 'Series.S01E02', 'Series.S01E03']); },
    async resolve(candidate) {
      f.calls.push(['resolve', candidate.episode]);
      if (candidate.episode === 2) { entered.resolve(); await gate.promise; }
      return { url: `https://media.invalid/${candidate.episode}` };
    },
  } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', target: tv }));
  const first = await f.current(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 40, duration: 60, paused: false });
  await f.command(sessionId, 'select.episode', { episode: { season: 1, episode: 2 } });
  await entered.promise;
  await f.command(sessionId, 'media.ended', { attemptId: first.id });
  assert.equal(f.snapshot(sessionId).target.episode, 2);
  assert.equal(f.library.read().history[0].episode.episode, 1);
  assert.equal(f.library.read().history[0].ended, true);
  gate.resolve();
  const selected = await f.pending(sessionId);
  assert.equal(selected.candidate.episode, 2);
  assert.deepEqual(f.calls.filter(call => call[0] === 'resolve').map(call => call[1]), [1, 2]);
});

test('the old output ending during same-episode source replacement preserves the newer replacement', async t => {
  const gate = deferred(), entered = deferred();
  const f = fixture({ data: {
    async detail() { return detail(['Film.1080p', 'Film.720p']); },
    async resolve(candidate) {
      if (candidate.name.includes('720p')) { entered.resolve(); await gate.promise; }
      return { url: `https://media.invalid/${candidate.name}` };
    },
  } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ platform: 'windows' }));
  const first = await f.current(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 21, duration: 90, paused: true });
  await f.command(sessionId, 'switch');
  await entered.promise;
  await f.command(sessionId, 'media.ended', { attemptId: first.id });
  assert.notEqual(f.snapshot(sessionId).stage, 'ended');
  gate.resolve();
  assert.match((await f.pending(sessionId)).candidate.name, /720p/);
});

for (const retained of [true, false]) {
  test(`raw-source resume ${retained ? 'restores the exact unnumbered file' : 'does not apply missing-file progress to another file'}`, async t => {
    let items = ['One', 'Two'];
    const f = fixture({ data: { async detail() { return detail(items); } } });
    t.after(() => f.service.close());
    const target = { id: 'source:a:raw', kind: 'source', title: 'Raw', source: { key: 'a', detailId: 'raw' } };
    const { sessionId } = (await f.open({ target }));
    await until(() => f.snapshot(sessionId).stage === 'idle');
    await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
    await until(() => f.snapshot(sessionId).browsing.episodes.length === 2);
    const selected = f.snapshot(sessionId).candidates.find(value => value.name === 'Two');
    await f.command(sessionId, 'select.candidate', { candidateId: selected.id });
    const current = await f.current(sessionId);
    await f.command(sessionId, 'media.progress', { attemptId: current.id, position: 43, duration: 100, paused: true });
    await f.command(sessionId, 'stop');
    await f.service.release({ sessionId });
    if (!retained) items = ['One'];
    const reopened = (await f.open({ platform: 'windows', target }));
    const next = await f.pending(reopened.sessionId);
    assert.equal(next.candidate.name, retained ? 'Two' : 'One');
    assert.equal(next.position, retained ? 43 : 0);
    if (retained) assert.equal(next.candidate.id, selected.id);
  });
}

const finishedSources = [{ sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 1, completed: true }];

test('editing an enabled manual mapping replaces its prepared input only after the new record is saved', async t => {
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', id]); return detail([`${id}.1080p`]);
  } } });
  t.after(() => f.service.close());
  const item = { id: 'm', targetId: movie.id, sourceKey: 'a', detailId: 'Old', enabled: true };
  (await f.service.updateManual({ action: 'save', item }));
  const { sessionId } = (await f.open({ platform: 'ios', search: finishedSources }));
  const old = await f.pending(sessionId);
  f.failStorage('STORAGE_ERROR');
  (await assert.rejects(async () => (await f.service.updateManual({ action: 'save', item: { ...item, detailId: 'New' } })),
    { code: 'STORAGE_ERROR' }));
  assert.equal(f.snapshot(sessionId).attempt.id, old.id);
  f.failStorage(null);
  (await f.service.updateManual({ action: 'save', item: { ...item, detailId: 'New' } }));
  const next = await f.pending(sessionId);
  assert.notEqual(next.id, old.id);
  assert.equal(next.candidate.source.detailId, 'New');
  await f.command(sessionId, 'play');
  assert.equal(f.snapshot(sessionId).attempt.id, next.id);
  assert.equal(f.snapshot(sessionId).attempt.prepareOnly, false);
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: old.id }), { code: 'STALE_SESSION' });
});

test('a late old manual bootstrap cannot publish or overwrite the edited mapping failure state', async t => {
  const entered = deferred(), release = deferred();
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', id]);
    if (id === 'Old') { entered.resolve(); await release.promise; throw error('UPSTREAM_HTTP'); }
    return detail(['New.1080p']);
  } } });
  t.after(async () => { release.resolve(); await f.service.close(); });
  const item = { id: 'm', targetId: movie.id, sourceKey: 'a', detailId: 'Old', enabled: true, autoDisable: true };
  (await f.service.updateManual({ action: 'save', item }));
  const { sessionId } = (await f.open({ platform: 'ios', search: finishedSources }));
  await entered.promise;
  (await f.service.updateManual({ action: 'save', item: { ...item, detailId: 'New' } }));
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.source.detailId, 'New');
  release.resolve(); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).attempt.id, next.id);
  assert.equal(f.service.library().manual[0].failureCount, 0);
  assert.equal(f.service.library().manual[0].enabled, true);
});

for (const action of ['disable', 'remove']) {
  test(`manual site ${action} stops its neighbor prefetch while keeping existing output`, async t => {
    const f = fixture({ data: { async detail() { return detail(['Unrelated.S01E01', 'Unrelated.S01E02']); } } });
    t.after(() => f.service.close());
    const item = { id: 'm', targetId: tv.id, sourceKey: 'a', detailId: 'manual-only', enabled: true };
    (await f.service.updateManual({ action: 'save', item }));
    const { sessionId } = (await f.open({ platform: 'windows', target: tv, search: finishedSources }));
    const current = await f.current(sessionId);
    await delay(2);
    // History is an independent valid association; clearing it makes this
    // scenario specifically about the revoked manual-only association.
    f.service.removeLibrary({ kind: 'history' });
    (await f.service.updateManual(action === 'remove' ? { action: 'remove', id: 'm' }
      : { action: 'save', item: { ...item, enabled: false } }));
    await f.command(sessionId, 'shortvideo', { enabled: true });
    await until(() => f.snapshot(sessionId).prefetch.some(value => value.state === 'error'));
    assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
    assert.equal(f.calls.filter(call => call[0] === 'resolve').length, 1);
    assert.equal(f.calls.some(call => call[0] === 'prefetch'), false);
    await f.command(sessionId, 'next');
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).attempt, null);
  });
}

test('a known manual share prepares and buffers its adjacent episode without all-source search', async t => {
  const f = fixture({ data: { async expand(value, line) {
    f.calls.push(['expand', value.key]);
    return { ...line, expanded: true, episodes: [1, 2].map(number => ({
      id: `file-${number}`, name: `Series.S01E0${number}.1080p`, file: {
        provider: 'quark', kind: 'share', shareId: 'share', fileId: `file-${number}`, accountRevision: 1,
      },
    })) };
  } } });
  t.after(() => f.service.close());
  (await f.service.updateManual({ action: 'save', item: { id: 'share', targetId: tv.id, kind: 'share',
    provider: 'quark', share: { url: 'https://share.invalid/s' }, enabled: true } }));
  const { sessionId } = (await f.open({ platform: 'windows', target: tv }));
  await f.current(sessionId);
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => f.snapshot(sessionId).prefetch.some(value => value.state === 'buffered'));
  assert.equal(f.calls.filter(call => call[0] === 'expand').length, 1);
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
  const resolves = f.calls.filter(call => call[0] === 'resolve').length;
  const cachedId = f.calls.find(call => call[0] === 'prefetch')[1];
  await f.command(sessionId, 'next');
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.episode, 2);
  assert.equal(f.service.attempt({ sessionId, attemptId: next.id }).id, cachedId);
  assert.equal(f.calls.filter(call => call[0] === 'resolve').length, resolves);
});

test('a source renamed into purification cannot reuse its iOS prepared resource for new playback', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'ios', sources: [source('a', '影院源'), source('b')] }));
  const old = await f.pending(sessionId);
  assert.equal(old.candidate.source.key, 'a');
  (await f.service.updateSourceDisplay('cat', [{ installationId: 'install-a', sites: [{ key: 'a', name: '体育源' }] }]));
  await f.command(sessionId, 'play');
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.source.key, 'b');
  assert.notEqual(next.id, old.id);
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: old.id }), { code: 'STALE_SESSION' });
});

test('purification changes do not revoke an explicitly selected source outside smart matching', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [{ id: 'explicit', title: 'Film' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'browse', { sourceId: f.snapshot(sessionId).sources[0].id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length > 0);
  await f.command(sessionId, 'select.candidate', { candidateId: f.snapshot(sessionId).browsing.episodes[0].candidateId });
  const selected = await f.pending(sessionId);
  (await f.service.updateSourceDisplay('cat', [{ installationId: 'install-a', sites: [{ key: 'a', name: '体育源' }] }]));
  assert.equal(f.snapshot(sessionId).attempt.id, selected.id);
  await f.command(sessionId, 'media.started', { attemptId: selected.id });
  assert.equal(f.snapshot(sessionId).current.attemptId, selected.id);
});

for (const update of ['settings', 'rename']) {
  test(`removing purification by ${update} reopens discovery before relaxing a fixed-pan quality gate`, async t => {
    const f = fixture({ data: { async detail(value) {
      f.calls.push(['detail', value.key]);
      return { lines: [{ id: 'quark', label: 'quark', provider: 'quark',
        episodes: [{ id: `file-${value.key}`, name: `Film.${value.key === 'a' ? '1080p' : '4K'}`, provider: 'quark' }] }] };
    } } });
    t.after(() => f.service.close());
    const { sessionId } = (await f.open({ sources: [source('a'), source('b', '体育源')] }));
    await until(() => f.snapshot(sessionId).stage === 'idle');
    await f.command(sessionId, 'pan', { provider: 'uc' });
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).searchComplete, true);
    assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a']);
    if (update === 'settings') (await f.service.setSettings({ ...f.service.settings(), siteCleanKeywords: [] }));
    else (await f.service.updateSourceDisplay('cat', [{ installationId: 'install-b', sites: [{ key: 'b', name: '影院源' }] }]));
    assert.equal(f.snapshot(sessionId).searchComplete, false);
    await f.command(sessionId, 'pan', { provider: 'quark' });
    const next = await f.pending(sessionId);
    assert.equal(next.candidate.source.key, 'b');
    assert.equal(next.candidate.quality, '4k');
    assert.deepEqual(f.calls.filter(call => call[0] === 'search').map(call => call[1]), ['a', 'b']);
  });
}

test('account revocation while expanding a selected share cannot publish late files or schedule media', async t => {
  const gate = deferred();
  const f = fixture({ data: {
    async detail() { return { lines: [{ id: '盘', label: '盘', episodes: [{ id: 'share', name: 'Film',
      share: { provider: 'quark', url: 'https://share.invalid/s' } }] }] }; },
    async expand() { f.calls.push(['expanding']); return gate.promise; },
  } }); t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows' }));
  await until(() => f.calls.some(call => call[0] === 'expanding'));
  await f.service.invalidate('quark');
  gate.resolve({ id: '盘', label: '盘', expanded: true, episodes: [{ id: 'file', name: 'Film.1080p',
    file: { provider: 'quark', kind: 'share', shareId: 's', fileId: 'f', accountRevision: 1 } }] });
  await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).error, 'STALE_ACCOUNT');
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
});

// Cross-runtime hints are not current search evidence, even at generation 1.
function runtimeDataFixture(invoke, metadataRequest = () => ({ ...tmdbAppended, id: 17, title: 'Film', release_date: '2020-01-01',
  genres: [], runtime: 100 }), { now = Date.now } = {}) {
  const calls = [], metadataCalls = [], result = value => ({ ok: true, value });
  const modules = new Map([
    ['tmdb', { methods: {
      'tmdb.data.settings.get': async () => result({ revision: 'metadata-current', apiKeyConfigured: true }),
      'tmdb.data.request': async value => {
        metadataCalls.push(structuredClone(value)); return result(await metadataRequest(value));
      },
    } }],
    ['cat', { authorizeMedia: async () => false, methods: {
      'cat.script.status': async () => result({ instances: ['a', 'b'].map(key => ({
        installationId: `install-${key}`, generation: 1, state: 'ready', sites: [{ key }],
      })) }),
      'cat.script.invoke': async value => {
        calls.push(structuredClone(value));
        return result(await invoke(value));
      },
    } }],
  ]);
  const cache = createPlaybackCache({ now }), data = createPlaybackData({ modules, cache, now });
  return { data, calls, metadataCalls, async close() { await data.close(); await cache.close(); } };
}

test('a new generation-1 owner verifies hinted detail despite stale nonmatching raw and stops at its first hit', async t => {
  const old = runtimeDataFixture(({ operation }) => {
    assert.equal(operation, 'search');
    return { pagecount: 3, list: [{ vod_id: 0, vod_name: 'Old unrelated title', vod_remarks: '原备注',
      metadataProvider: 'tmdb', metadataId: 999, vod_play_url: 'old$must-not-play' },
    { vod_id: 'unused', vod_name: 'Film' }] };
  });
  t.after(() => old.close());
  const page = await old.data.search(source('a'), 'Film', 3);
  await old.close();
  const gate = deferred();
  const current = runtimeDataFixture(async ({ operation, siteId, generation, payload }) => {
    assert.equal(generation, 1); assert.equal(siteId, 'a');
    if (operation === 'detail') { assert.equal(payload.id, 0); await gate.promise;
      return { list: [
        { vod_id: '0', vod_name: 'Wrong typed ID', vod_play_from: 'direct', vod_play_url: 'Wrong$wrong-id' },
        { vod_id: 0, vod_name: 'Film', vod_year: '2020', vod_play_from: 'direct', vod_play_url: 'Film.1080p$fresh-id' },
      ] };
    }
    assert.equal(operation, 'play'); assert.equal(payload.id, 'fresh-id');
    return { url: 'https://media.invalid/fresh' };
  });
  const f = fixture({ data: current.data });
  t.after(async () => { gate.resolve(); await f.service.close(); await current.close(); });
  const hints = [{ sourceKey: 'a', items: page.items }, { sourceKey: 'b', items: [{ id: 'later', title: 'Film' }] }];
  const before = structuredClone(hints);
  const opened = (await f.open({ platform: 'windows', target: { ...movie, year: 2020 }, resultHints: hints }));
  await until(() => current.calls.some(value => value.operation === 'detail'));
  assert.deepEqual(f.snapshot(opened.sessionId).candidates, []);
  assert.equal(f.snapshot(opened.sessionId).attempt, null);
  assert.equal(f.snapshot(opened.sessionId).sources[0].title, 'Old unrelated title');
  assert.equal(f.snapshot(opened.sessionId).sources[0].source.detailId, 0);
  assert.ok(f.snapshot(opened.sessionId).search.every(value => value.page === 0 && !value.completed));
  gate.resolve();
  const attempt = await f.pending(opened.sessionId);
  assert.equal(attempt.candidate.source.detailId, 0);
  assert.equal(attempt.candidate.name, 'Film.1080p');
  assert.equal(f.snapshot(opened.sessionId).sources[0].id, opened.sources[0].id);
  assert.equal(f.snapshot(opened.sessionId).sources[0].title, 'Old unrelated title');
  assert.equal(f.snapshot(opened.sessionId).sources[1].state, 'idle');
  assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'play']);
  assert.deepEqual(hints, before);
  assert.equal(hints[0].items[0].vod_remarks, '原备注');
});

for (const mismatch of ['title', 'year']) {
  test(`old hinted metadataId/title cannot approve a current detail ${mismatch} conflict`, async t => {
    const current = runtimeDataFixture(({ operation, payload }) => {
      if (operation === 'search') return { pagecount: 1, list: [] };
      assert.equal(operation, 'detail'); assert.equal(payload.id, 'wanted');
      return { list: [
        { vod_id: 'another', vod_name: 'Film', vod_year: '2020',
          vod_play_from: 'direct', vod_play_url: 'Film$must-not-play-first-record' },
        { vod_id: 'wanted', vod_name: mismatch === 'title' ? 'Another Film' : 'Film',
          vod_year: mismatch === 'year' ? '2021' : '2020',
          vod_play_from: 'direct', vod_play_url: 'Film$must-not-play-old-identity' },
      ] };
    });
    const f = fixture({ data: current.data });
    t.after(async () => { await f.service.close(); await current.close(); });
    const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')], target: { ...movie, year: 2020 },
      resultHints: [{ sourceKey: 'a', items: [{ id: 'wanted', title: 'Film', metadataId: 17, metadataProvider: 'tmdb' }] }] }));
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
    assert.equal(f.snapshot(sessionId).sources[0].error, 'NO_MATCH');
    assert.deepEqual(f.snapshot(sessionId).candidates, []);
    assert.equal(f.snapshot(sessionId).attempt, null);
    assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'search']);
    assert.equal(f.calls.some(call => call[0] === 'prepare'), false);
  });
}

test('hint detail errors and identity mismatches continue in order without fetching later hints or searching', async t => {
  const f = fixture({ data: {
    async detail(value, id) {
      f.calls.push(['detail', value.key, id]);
      if (id === 'error') throw error('UPSTREAM_HTTP');
      return { ...detail(['Film.1080p']), detailId: id, raw: { id, title: id === 'wrong' ? 'Other' : 'Film' } };
    },
  } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', resultHints: [{ sourceKey: 'a',
    items: ['error', 'wrong', 'good', 'unused'].map(id => ({ id, title: 'Old Film', metadataId: 17, metadataProvider: 'tmdb' })) }] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.detailId, 'good');
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail').map(call => call[2]), ['error', 'wrong', 'good']);
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => row.error), ['UPSTREAM_HTTP', 'NO_MATCH', null, null]);
  assert.equal(f.snapshot(sessionId).sources[3].state, 'idle');
  assert.equal(f.snapshot(sessionId).candidates.length, 1);
  assert.equal(f.calls.some(call => call[0] === 'search' || call[0] === 'expand'), false);
});

test('opening and browsing hints does no source work until selected and no resolve before current verification', async t => {
  const gate = deferred();
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', value.key, id]); await gate.promise;
    return { ...detail(['Film.1080p']), detailId: id, raw: { detailId: id, title: 'Film' } };
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ resultHints: [{ sourceKey: 'a',
    items: [{ detailId: 'hint', title: 'An old title', vod_remarks: '原备注' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.equal(f.calls.some(call => ['detail', 'search', 'resolve'].includes(call[0])), false);
  const row = f.snapshot(sessionId).sources[0];
  await f.command(sessionId, 'browse', { sourceId: row.id });
  await until(() => f.calls.some(call => call[0] === 'detail'));
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes, []);
  gate.resolve();
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 1);
  assert.ok(f.snapshot(sessionId).browsing.episodes[0].candidateId);
  assert.equal(f.snapshot(sessionId).sources[0].title, 'An old title');
  assert.equal(f.calls.some(call => call[0] === 'resolve' || call[0] === 'search'), false);
});

test('history/manual linkage cannot turn an unverified hint into candidate evidence', async t => {
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', value.key, id]);
    return { ...detail(['Film.1080p']), detailId: id, raw: { id, title: 'Other Film' } };
  }, async search(value, query, page) { f.calls.push(['search', page]); return { page, hasMore: false, items: [] }; } } });
  t.after(() => f.service.close());
  f.library.update(document => {
    document.history.push({ targetId: movie.id, sourceKey: 'a', detailId: 'linked', candidateId: 'old', position: 20 });
    document.manual.push({ id: 'manual-linked', targetId: movie.id, sourceKey: 'a', detailId: 'linked',
      enabled: true, failureCount: 0 });
  });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
    resultHints: [{ sourceKey: 'a', items: [{ id: 'linked', title: 'Film', metadataId: 17, metadataProvider: 'tmdb' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
  assert.equal(f.calls.some(call => call[0] === 'resolve'), false);
  assert.equal(f.calls.filter(call => call[0] === 'detail').length, 1);
});

test('real search evidence can promote the same failed hint and continue its ordinary current-owner flow', async t => {
  let details = 0;
  const current = runtimeDataFixture(({ operation, payload }) => {
    if (operation === 'detail') {
      assert.equal(payload.id, 'same');
      if (++details === 1) throw error('UPSTREAM_HTTP');
      return { id: 'same', title: 'Film', vod_play_from: 'direct', vod_play_url: 'Film.1080p$current-id' };
    }
    if (operation === 'search') return { pagecount: 1, list: [{ id: 'same', metadataProvider: 'tmdb', metadataId: 17 }] };
    assert.equal(operation, 'play'); assert.equal(payload.id, 'current-id');
    return { url: 'https://media.invalid/current-search' };
  });
  const f = fixture({ data: current.data });
  t.after(async () => { await f.service.close(); await current.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
    resultHints: [{ sourceKey: 'a', items: [{ id: 'same', title: 'Old unmatched title' }] }] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.detailId, 'same');
  assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'search', 'detail', 'play']);
  assert.equal(f.snapshot(sessionId).sources.length, 1);
  assert.equal(f.snapshot(sessionId).sources[0].error, null);
});

test('late hint detail cannot produce candidates or media after its exact source is revoked', async t => {
  const gate = deferred();
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', id]); await gate.promise;
    return { ...detail(['Film.1080p']), detailId: id, raw: { id, title: 'Film' } };
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
    resultHints: [{ sourceKey: 'a', items: [{ id: 'late', title: 'Film' }] }] }));
  await until(() => f.calls.some(call => call[0] === 'detail'));
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' });
  gate.resolve(); await delay(2); await delay(2);
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.calls.some(call => call[0] === 'resolve' || call[0] === 'prepare'), false);
});

test('raw hints retain typed zero and only verify the original source/detail/line/episode scope', async t => {
  const current = runtimeDataFixture(({ operation, siteId, payload }) => {
    assert.equal(siteId, 'a');
    if (operation === 'detail') {
      assert.equal(payload.id, 0);
      return { id: 0, title: 'Unrelated current raw title', lines: [
        { id: '0', episodes: [{ id: 0, name: 'Different line' }] },
        { id: 0, episodes: [{ id: 2, name: 'Other episode' }, { id: 0, name: 'Exact typed zero' }] },
      ] };
    }
    assert.equal(operation, 'play'); assert.equal(payload.flag, 0); assert.equal(payload.id, 0);
    return { url: 'https://media.invalid/raw-zero' };
  });
  const f = fixture({ data: current.data });
  t.after(async () => { await f.service.close(); await current.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'raw-zero-hint', kind: 'source', title: 'Raw old',
    source: { key: 'a', detailId: 0, lineId: 0, playId: 0, episodeId: 0 } },
  resultHints: [
    { sourceKey: 'b', items: [{ id: 0, title: 'Raw old' }] },
    { sourceKey: 'a', items: [{ id: '0', title: 'Raw old' }, { id: 0, title: 'Kept original raw title' }] },
  ] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.detailId, 0);
  assert.equal(attempt.candidate.lineId, 0);
  assert.equal(attempt.candidate.name, 'Exact typed zero');
  assert.equal(f.snapshot(sessionId).sources.find(row => row.source.key === 'a' && row.source.detailId === 0).title,
    'Kept original raw title');
  assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'play']);
  assert.equal(f.snapshot(sessionId).target.source.candidateId, attempt.candidate.id);
  assert.equal('playId' in f.snapshot(sessionId).target.source, false);
  assert.equal('episodeId' in f.snapshot(sessionId).target.source, false);
});

for (const completed of [true, false]) {
  test(`same-owner results preserve page 3 and completed=${completed} without restarting search`, async t => {
    const f = fixture({ data: {
      async detail(value, id) { f.calls.push(['detail', value.key, id]); return detail(['Film.1080p']); },
      async search(value, query, page) {
        f.calls.push(['search', value.key, query, page]); return { page, hasMore: false, items: [{ id: 'fresh', title: 'Film' }] };
      },
    } });
    t.after(() => f.service.close());
    const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
      results: [{ sourceKey: 'a', items: [{ id: 'same-owner', title: 'Film', vod_remarks: '原备注' }] }],
      resultHints: [{ sourceKey: 'a', items: [{ id: 'same-owner', title: 'Stale other title' }] }],
      search: [{ sourceKey: 'a', page: 3, completed }] }));
    const first = await f.pending(sessionId);
    assert.equal(first.candidate.source.detailId, 'same-owner');
    assert.deepEqual(f.calls.filter(call => call[0] === 'search'), []);
    assert.equal(f.snapshot(sessionId).search[0].page, 3);
    assert.equal(f.snapshot(sessionId).search[0].completed, completed);
    await f.command(sessionId, 'media.failed', { attemptId: first.id });
    if (completed) {
      await until(() => f.snapshot(sessionId).stage === 'unavailable');
      assert.deepEqual(f.calls.filter(call => call[0] === 'search'), []);
    } else {
      const next = await f.pending(sessionId);
      assert.equal(next.candidate.source.detailId, 'fresh');
      assert.deepEqual(f.calls.filter(call => call[0] === 'search'), [['search', 'a', 'Film', 4]]);
    }
  });
}

test('opaque raw selection survives a new generation-1 owner but a changed same-name file returns NO_MATCH', async t => {
  const rawDetail = changed => ({ id: 0, title: 'Raw current', lines: [{ id: 0, episodes: [
    { id: 'private-first-id', name: 'One' },
    { id: changed ? 'private-second-replacement' : 'private-second-id', name: 'Two' },
  ] }] });
  const owner = runtimeDataFixture(({ operation, payload }) => {
    if (operation === 'detail') return rawDetail(false);
    assert.equal(operation, 'play');
    return { url: `https://media.invalid/${payload.id === 'private-first-id' ? 'first' : 'second'}` };
  });
  const first = fixture({ data: owner.data });
  t.after(async () => { await first.service.close(); await owner.close(); });
  const opened = (await first.open({ platform: 'windows', sources: [source('a')], target: { id: 'raw-opaque', kind: 'source',
    title: 'Raw', source: { key: 'a', detailId: 0, lineId: 0, playId: 'private-first-id' } } }));
  const a = await first.current(opened.sessionId);
  await first.command(opened.sessionId, 'next');
  const b = await first.current(opened.sessionId);
  assert.notEqual(b.candidate.id, a.candidate.id);
  const target = first.snapshot(opened.sessionId).target;
  assert.equal(target.source.candidateId, b.candidate.id);
  assert.equal('playId' in target.source, false); assert.equal('episodeId' in target.source, false);
  assert.equal(JSON.stringify(first.snapshot(opened.sessionId)).includes('private-second-id'), false);
  await first.service.close(); await owner.close();
  for (const changed of [false, true]) {
    const current = runtimeDataFixture(({ operation, payload }) => {
      if (operation === 'detail') { assert.equal(payload.id, 0); return rawDetail(changed); }
      assert.equal(operation, 'play'); assert.equal(changed, false); assert.equal(payload.id, 'private-second-id');
      return { url: 'https://media.invalid/recovered-second' };
    });
    const f = fixture({ data: current.data });
    t.after(async () => { await f.service.close(); await current.close(); });
    const { sessionId } = (await f.open({ platform: 'windows', target, sources: [source('a')],
      resultHints: [{ sourceKey: 'a', items: [{ id: 0, title: 'Kept old raw title' }] }] }));
    if (!changed) {
      const recovered = await f.pending(sessionId);
      assert.equal(recovered.candidate.id, b.candidate.id);
      assert.equal(recovered.candidate.name, 'Two');
      assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'play']);
    } else {
      await until(() => f.snapshot(sessionId).stage === 'unavailable');
      assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
      assert.equal(f.snapshot(sessionId).attempt, null);
      assert.deepEqual(current.calls.map(value => value.operation), ['detail']);
      assert.equal(f.calls.some(call => call[0] === 'prepare'), false);
    }
  }
});

test('raw adjacent prefetch, next, previous and ended use the new exact opaque candidate instead of the old constraint', async t => {
  const f = fixture({ data: { async detail() {
    return { lines: [
      { id: 'other', label: 'other', episodes: [{ id: 'one', playId: 'other-one', name: 'Other.4K' },
        { id: 'two', playId: 'other-two', name: 'Other next.4K' }] },
      { id: 'selected', label: 'selected', episodes: [{ id: 'one', playId: 'selected-one', name: 'One' },
        { id: 'two', playId: 'selected-two', name: 'Two' }] },
    ] };
  } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'raw-adjacent', kind: 'source', title: 'Raw',
    source: { key: 'a', detailId: 0, lineId: 'selected', playId: 'selected-one', episodeId: 'one' } } }));
  const a = await f.current(sessionId);
  assert.equal(a.candidate.name, 'One');
  await f.command(sessionId, 'media.progress', { attemptId: a.id, position: 50, duration: 100, paused: true });
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => f.snapshot(sessionId).prefetch.some(value => value.state === 'buffered'));
  const neighbor = f.snapshot(sessionId).prefetch.find(value => value.state === 'buffered');
  const expected = f.snapshot(sessionId).candidates.find(value => value.name === 'Two' && value.lineId === 'selected');
  assert.equal(neighbor.target.source.candidateId, expected.id);
  assert.notEqual(neighbor.target.source.candidateId, a.candidate.id);
  await f.command(sessionId, 'next');
  const b = await f.current(sessionId);
  assert.equal(b.candidate.id, expected.id); assert.equal(b.position, 0);
  assert.equal(f.snapshot(sessionId).target.source.candidateId, b.candidate.id);
  await f.command(sessionId, 'shortvideo', { enabled: false });
  await f.command(sessionId, 'previous');
  const previous = await f.current(sessionId);
  assert.equal(previous.candidate.id, a.candidate.id);
  await f.command(sessionId, 'media.ended', { attemptId: previous.id });
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.id, b.candidate.id);
  assert.equal(f.snapshot(sessionId).target.source.candidateId, b.candidate.id);
  assert.equal(f.calls.some(call => call[0] === 'search'), false);
  assert.equal(f.calls.some(call => call[0] === 'resolve' && call[2].startsWith('Other')), false);
});

test('raw next follows distinct files even when their parsed episode coordinates happen to be equal', async t => {
  const f = fixture({ data: { async detail() {
    return detail([{ id: 'first-file', name: 'Series.S01E01.A' }, { id: 'second-file', name: 'Series.S01E01.B' }]);
  } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'raw-same-coordinates', kind: 'source',
    title: 'Raw', source: { key: 'a', detailId: 0, lineId: 'line', playId: 'first-file' } } }));
  const first = await f.current(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 46, duration: 100, paused: true });
  await f.command(sessionId, 'next');
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.name, 'Series.S01E01.B');
  assert.notEqual(next.candidate.id, first.candidate.id);
  assert.equal(next.candidate.season, first.candidate.season);
  assert.equal(next.candidate.episode, first.candidate.episode);
  assert.equal(next.position, 0); assert.equal(next.paused, false);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
});

test('raw opaque reopen restores only that exact candidate history rather than another same-detail file position', async t => {
  const f = fixture({ data: { async detail() { return detail(['One', 'Two']); } } });
  t.after(() => f.service.close());
  const original = (await f.open({ platform: 'windows', target: { id: 'raw-opaque-history', kind: 'source', title: 'Raw',
    source: { key: 'a', detailId: 0, playId: 'play-Two' } } }));
  const selected = await f.current(original.sessionId);
  await f.command(original.sessionId, 'media.progress', { attemptId: selected.id, position: 37, duration: 100, paused: false });
  const target = f.snapshot(original.sessionId).target;
  await f.service.release({ sessionId: original.sessionId });
  for (const retained of [true, false]) {
    f.library.update(document => { document.history[0].candidateId = retained ? selected.candidate.id : 'another-file'; });
    const { sessionId } = (await f.open({ platform: 'windows', target }));
    const resumed = await f.pending(sessionId);
    assert.equal(resumed.candidate.id, selected.candidate.id);
    assert.equal(resumed.position, retained ? 37 : 0);
    await f.service.release({ sessionId });
  }
});

test('a successfully read but nonmatching hint resets manual request failures without approving its candidates', async t => {
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', value.key, id]);
    return { ...detail(['Film.1080p']), detailId: id, raw: { id, title: 'Other Film' } };
  } } });
  t.after(() => f.service.close());
  f.library.update(document => { document.manual.push({ id: 'manual-hint', targetId: movie.id,
    sourceKey: 'a', detailId: 'old', enabled: true, failureCount: 2, autoDisable: true }); });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
    search: [{ sourceKey: 'a', page: 1, completed: true }],
    resultHints: [{ sourceKey: 'a', items: [{ id: 'old', title: 'Film', metadataProvider: 'tmdb', metadataId: 17 }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
  assert.equal(f.library.read().manual[0].failureCount, 0);
  assert.equal(f.library.read().manual[0].enabled, true);
  assert.deepEqual(f.calls.filter(call => call[0] === 'detail'), [['detail', 'a', 'old']]);
  assert.equal(f.calls.some(call => call[0] === 'resolve' || call[0] === 'search'), false);
});

test('hint verification accepts a current direct detail without an explicit id using the data owner request identity', async t => {
  const raw = { title: 'Film', lines: [{ id: 'direct', episodes: [{ id: 'current-play-id', name: 'Film.1080p' }] }] };
  const before = structuredClone(raw);
  const current = runtimeDataFixture(({ operation, payload }) => {
    if (operation === 'detail') { assert.equal(payload.id, 0); return raw; }
    assert.equal(operation, 'play'); assert.equal(payload.id, 'current-play-id');
    return { url: 'https://media.invalid/no-detail-id' };
  });
  const f = fixture({ data: current.data });
  t.after(async () => { await f.service.close(); await current.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
    resultHints: [{ sourceKey: 'a', items: [{ id: 0, title: 'An old unrelated title', vod_remarks: '原备注' }] }] }));
  const attempt = await f.pending(sessionId);
  assert.equal(attempt.candidate.source.detailId, 0);
  assert.equal(attempt.candidate.name, 'Film.1080p');
  assert.deepEqual(current.calls.map(value => value.operation), ['detail', 'play']);
  assert.deepEqual(raw, before);
  assert.equal('detailId' in raw, false); assert.equal('id' in raw, false);
});

for (const malformed of ['explicit-id-conflict', 'list-without-exact-id']) {
  test(`hint verification rejects ${malformed} through the real data owner rather than borrowing old raw identity`, async t => {
    const current = runtimeDataFixture(({ operation, payload }) => {
      assert.equal(operation, 'detail'); assert.equal(payload.id, 0);
      const lines = [{ id: 'direct', episodes: [{ id: 'must-not-play', name: 'Film.1080p' }] }];
      return malformed === 'explicit-id-conflict' ? { id: '0', title: 'Film', lines }
        : { list: [{ title: 'Film', lines }, { vod_id: '0', vod_name: 'Film', lines }] };
    });
    const f = fixture({ data: current.data });
    t.after(async () => { await f.service.close(); await current.close(); });
    const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
      search: [{ sourceKey: 'a', page: 1, completed: true }],
      resultHints: [{ sourceKey: 'a', items: [{ id: 0, title: 'Film', metadataProvider: 'tmdb', metadataId: 17 }] }] }));
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).sources[0].error, 'UPSTREAM_FORMAT');
    assert.deepEqual(f.snapshot(sessionId).candidates, []);
    assert.equal(f.snapshot(sessionId).attempt, null);
    assert.deepEqual(current.calls.map(value => value.operation), ['detail']);
    assert.equal(f.calls.some(call => call[0] === 'prepare'), false);
  });
}

const groupTarget = (members = [{ sourceKey: 'a', detailId: 0 }]) => ({
  id: 'group:Film', kind: 'group', title: 'Film', members,
});
const searchContext = (target, revision, changes = {}) => ({
  selectionId: target.id, revision, sources: [source('a'), source('b')], results: [], search: [], ...changes,
});

test('E49 K2 real data pages continue through partial overlap and stop only at a page without a new typed identity', async t => {
  const pages = [
    [{ vod_id: 0, vod_name: 'Film', vod_remarks: 'numeric' }, { vod_id: 1, vod_name: 'Film' }],
    [{ vod_id: 0, vod_name: 'Film' }, { vod_id: '0', vod_name: 'Film', vod_remarks: 'string' }],
    [{ vod_id: '0', vod_name: 'Film' }, { vod_id: 1, vod_name: 'Film' }],
  ];
  const before = structuredClone(pages);
  const owner = runtimeDataFixture(({ operation, payload }) => {
    assert.equal(operation, 'search'); return { list: pages[payload.page - 1], total: 1, limit: 1 };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const { sessionId } = (await f.open({ sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  for (let page = 1; page <= 3; page++) {
    await f.command(sessionId, 'search.more');
    await until(() => f.snapshot(sessionId).search[0].page === page && !f.snapshot(sessionId).search[0].loading);
    assert.equal(f.snapshot(sessionId).searchComplete, page === 3);
  }
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => row.source.detailId), [0, 1, '0']);
  await f.command(sessionId, 'search.more'); await delay(2);
  assert.deepEqual(owner.calls.map(row => [row.operation, row.payload.page]), [['search', 1], ['search', 2], ['search', 3]]);
  assert.deepEqual(pages, before);
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
});

test('E49 K2 explicit pagecount survives repeated and empty pages, and a failed page is retried without cursor advancement', async t => {
  let failed = false;
  const owner = runtimeDataFixture(({ operation, payload }) => {
    assert.equal(operation, 'search');
    if (payload.page === 2 && !failed) { failed = true; throw error('UPSTREAM_HTTP'); }
    return { pageCount: '4', list: payload.page === 3 ? []
      : [{ vod_id: payload.page === 4 ? 'last' : 0, vod_name: 'Film' }] };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const { sessionId } = (await f.open({ sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.more'); await until(() => f.snapshot(sessionId).search[0].page === 1);
  await f.command(sessionId, 'search.more'); await until(() => f.snapshot(sessionId).search[0].error === 'UPSTREAM_HTTP');
  assert.equal(f.snapshot(sessionId).search[0].page, 1);
  assert.equal(f.snapshot(sessionId).searchComplete, false);
  for (let page = 2; page <= 4; page++) {
    await f.command(sessionId, 'search.more');
    await until(() => f.snapshot(sessionId).search[0].page === page && !f.snapshot(sessionId).search[0].loading);
    assert.equal(f.snapshot(sessionId).searchComplete, page === 4);
    assert.equal(f.snapshot(sessionId).search[0].error, null);
  }
  assert.deepEqual(owner.calls.map(row => [row.operation, row.payload.page]),
    [['search', 1], ['search', 2], ['search', 2], ['search', 3], ['search', 4]]);
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => row.source.detailId), [0, 'last']);
});

test('E49 group uses its real first file and actual adjacent rows without guessing kind, numbers or a representative source', async t => {
  const raw = { id: 0, title: 'A different current site title', lines: [{ id: 0, episodes: [
    { id: 0, name: 'Unnumbered actual first.720p' }, { id: 2, name: 'Film.S01E02.4K' },
    { id: 3, name: 'Unnumbered actual last' },
  ] }] };
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: '0' }]);
  const before = structuredClone({ target, raw });
  const owner = runtimeDataFixture(({ operation, payload, siteId }) => {
    assert.equal(siteId, 'a');
    if (operation === 'detail') { assert.equal(payload.id, 0); return raw; }
    assert.equal(operation, 'play'); return { url: 'https://media.invalid/group' };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', target }));
  const first = await f.current(sessionId);
  assert.equal(first.candidate.name, raw.lines[0].episodes[0].name);
  assert.equal(first.candidate.episode, undefined);
  assert.equal(f.snapshot(sessionId).target.kind, 'group');
  assert.deepEqual(f.snapshot(sessionId).target.members, target.members);
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  assert.equal(f.snapshot(sessionId).metadata, null);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 48, duration: 100, paused: true });
  await f.command(sessionId, 'next');
  const second = await f.current(sessionId);
  assert.equal(second.candidate.name, 'Film.S01E02.4K');
  assert.equal(second.position, 0); assert.equal(second.paused, false);
  assert.equal(second.candidate.globalEpisode, undefined);
  await f.command(sessionId, 'previous');
  const previous = await f.pending(sessionId);
  assert.equal(previous.candidate.id, first.candidate.id);
  assert.equal(previous.position, 0);
  const saved = f.snapshot(sessionId).target;
  assert.equal(saved.source.candidateId, first.candidate.id);
  assert.equal('playId' in saved.source, false); assert.equal('episodeId' in saved.source, false);
  assert.deepEqual(saved.members, before.target.members);
  assert.equal(owner.calls.filter(row => row.operation === 'detail').length, 1);
  assert.equal(owner.calls.some(row => row.operation === 'search'), false);
  assert.deepEqual({ target, raw }, before);
});

test('E49 iOS unassociated group remains an actual directory with no automatic source or media requests', async t => {
  const owner = runtimeDataFixture(() => { assert.fail('An unselected iOS group must not invoke its source'); });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }]);
  const { sessionId } = (await f.open({ platform: 'ios', target, sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.deepEqual(f.snapshot(sessionId).target, target);
  assert.deepEqual(f.snapshot(sessionId).episodes, []);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.deepEqual(owner.calls, []); assert.deepEqual(owner.metadataCalls, []);
  assert.equal(f.calls.some(row => row[0] === 'prepare'), false);
});

test('E49 search.update merges only the original typed scope with monotonic pages and revisions and does no eager discovery', async t => {
  const f = fixture();
  t.after(() => f.service.close());
  const target = groupTarget();
  const { sessionId } = (await f.open({ target, search: [{ sourceKey: 'a', page: 3, completed: true }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const update = searchContext(target, 2, {
    results: [{ sourceKey: 'a', items: [
      { id: 'ui-one', sourceKind: 'site', siteKey: 'a', detailId: 0,
        title: 'Display', raw: { vod_id: 0, vod_name: 'Film', vod_remarks: '原备注' } },
      { id: 'ui-two', sourceKind: 'site', siteKey: 'a', detailId: '0',
        title: 'Display', raw: { vod_id: '0', vod_name: 'Film' } },
      { vod_id: 'other', vod_name: 'Another Film', raw: { id: 'not-an-identity' } },
    ] }],
    search: [{ sourceKey: 'a', page: 1, completed: false }, { sourceKey: 'b', page: 2, completed: false }],
  });
  const before = structuredClone(update);
  await f.command(sessionId, 'search.update', update); await delay(2);
  assert.deepEqual(f.snapshot(sessionId).target.members, [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }]);
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => row.source.detailId), [0, '0', 'other']);
  assert.deepEqual(f.snapshot(sessionId).search.map(row => [row.page, row.completed]), [[3, true], [2, false]]);
  const sequence = f.snapshot(sessionId).sequence;
  await f.command(sessionId, 'search.update', searchContext(target, 1, {
    results: [{ sourceKey: 'b', items: [{ id: 'stale', title: 'Film' }] }],
  }));
  await f.command(sessionId, 'search.update', searchContext(target, 2));
  assert.equal(f.snapshot(sessionId).sequence, sequence);
  await assert.rejects(f.command(sessionId, 'search.update', { ...update, selectionId: 'another-selection', revision: 3 }),
    { code: 'INVALID_REQUEST' });
  for (const forbidden of [source('c'), { ...source('a'), generation: 2 },
    { ...source('a'), installationId: 'different-installation' }]) {
    await assert.rejects(f.command(sessionId, 'search.update', searchContext(target, 3, { sources: [forbidden] })),
      { code: 'STALE_SOURCE' });
  }
  assert.equal(f.calls.some(row => ['search', 'detail', 'expand', 'resolve', 'prepare'].includes(row[0])), false);
  assert.deepEqual(update, before);
});

for (const resume of [false, true]) for (const carrier of ['group', 'tmdb']) {
  test(`E49 trusted initial handoff keeps exact group members without matching raw menu rows (resume=${resume}, carrier=${carrier})`, async t => {
    const original = await rules.snapshot(), computations = [];
    const ruleSnapshot = bindRuleSnapshot(Object.freeze({ ...original }), (request, options) => {
      computations.push(request.operation);
      return invokeFilmRules(original, request, options);
    });
    const f = fixture({ data: { async targetMetadata() { return null; } } });
    t.after(() => f.service.close());
    const target = { id: 'group:星河', kind: 'group', title: '星河 第1季',
      members: [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }] };
    const currentTarget = resume ? { ...target, title: '星河 第2季', season: 2, episode: 3,
      members: [{ sourceKey: 'a', detailId: '0' }],
      source: { key: 'a', detailId: '0', lineId: 'saved-line', candidateId: 'saved-file' } } : target;
    const carrierTarget = { ...tv, id: target.id, title: '星河' };
    const handoff = searchContext(target, 1, {
      target: carrier === 'tmdb' ? carrierTarget : target, selectionMembers: target.members,
      ...(carrier === 'tmdb' ? { metadataTarget: carrierTarget } : {}), results: [
      { sourceKey: 'a', items: [
        { vod_id: 0, vod_name: target.title }, { vod_id: '0', vod_name: target.title },
        { vod_id: 'not-selected', vod_name: currentTarget.title },
      ] },
      { sourceKey: 'b', items: [{ vod_id: 0, vod_name: currentTarget.title }] },
    ], search: [{ sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 1, completed: true }] });
    const unchanged = structuredClone(handoff);
    const opened = await f.open({ ...handoff, platform: 'ios', target: currentTarget, metadata: null },
      { ruleSnapshot, searchHandoff: handoff });
    assert.deepEqual(opened.target, currentTarget);
    await until(() => f.snapshot(opened.sessionId).stage === 'idle');
    const after = f.snapshot(opened.sessionId);
    assert.deepEqual(after.target, currentTarget);
    assert.deepEqual(after.sources.map(row => [row.source.key, row.source.detailId]),
      [['a', 0], ['a', '0'], ['a', 'not-selected'], ['b', 0]]);
    assert.equal(after.attempt, null);
    assert.equal(after.current, null);
    assert.deepEqual(computations, [], 'the trusted initial group is not reclassified by playback matching');
    assert.deepEqual(f.calls, [], 'opening the group menu does not fetch search, detail, pan or media');
    assert.deepEqual(handoff, unchanged);
  });
}

for (const carrier of ['group', 'tmdb']) {
  test(`E49 trusted initial handoff does not reset an explicit metadata resume target (carrier=${carrier})`, async t => {
    const target = groupTarget();
    const currentTarget = { ...movie, id: target.id, metadataProvider: 'douban', metadataId: 81,
      title: 'User selected metadata' };
    const currentMetadata = { id: 81, title: currentTarget.title, selected: true };
    const f = fixture({ data: { async targetMetadata(value) {
      assert.deepEqual(value, currentTarget);
      return structuredClone(currentMetadata);
    } } });
    t.after(() => f.service.close());
    const handoff = searchContext(target, 1, {
      target: carrier === 'tmdb' ? { ...movie, id: target.id } : target,
      selectionMembers: target.members, metadata: { title: 'Old search display' },
      results: [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: 'Film' }] }] });
    const opened = await f.open({ ...handoff, target: currentTarget, metadata: null },
      { ruleSnapshot: await rules.snapshot(), searchHandoff: handoff });
    assert.deepEqual(opened.target, currentTarget);
    assert.equal(opened.metadata, null);
    await until(() => f.snapshot(opened.sessionId).stage === 'idle');
    assert.deepEqual(f.snapshot(opened.sessionId).target, currentTarget);
    assert.deepEqual(f.snapshot(opened.sessionId).metadata, currentMetadata);
    assert.equal(f.calls.some(row => ['search', 'detail', 'expand', 'resolve', 'prepare'].includes(row[0])), false);
  });
}

for (const output of ['pending', 'current']) for (const carrier of ['group', 'tmdb']) {
  test(`E49 trusted search handoff adds cross-season typed members after navigation without replacing ${output} (carrier=${carrier})`, async t => {
    const original = await rules.snapshot(), computations = [], metadataReads = [];
    const ruleSnapshot = bindRuleSnapshot(Object.freeze({ ...original }), (request, options) => {
      computations.push(request.operation);
      return invokeFilmRules(original, request, options);
    });
    const f = fixture({ rules: { snapshot: async () => ruleSnapshot }, data: {
      async targetMetadata(value) {
        if (value.kind === 'group') return null;
        assert.deepEqual(value, carrierTarget);
        metadataReads.push(structuredClone(value));
        return structuredClone(carrierMetadata);
      },
      async detail(value, id) {
        f.calls.push(['detail', value.key, id]);
        return detail(['星河.S01E01.1080p']);
      },
    } });
    t.after(() => f.service.close());
    const target = { id: 'group:星河', kind: 'group', title: '星河 第1季',
      members: [{ sourceKey: 'a', detailId: 0 }] };
    const carrierTarget = { ...tv, id: target.id, title: '星河' };
    const carrierMetadata = { ...metadata, ...tmdbAppended, name: '星河', genres: [] };
    const { sessionId } = await f.open({ platform: 'windows', target,
      results: [{ sourceKey: 'a', items: [{ vod_id: 0, vod_name: '星河 第1季' }] }] });
    if (output === 'current') await f.current(sessionId);
    else await f.pending(sessionId);
    await tick();
    const before = f.snapshot(sessionId), calls = structuredClone(f.calls);
    const matching = computations.filter(operation => operation === 'recognition.matches').length;
    const lateMembers = [{ sourceKey: 'a', detailId: '0' }, { sourceKey: 'b', detailId: 0 }];
    const handoff = searchContext(target, 1, {
      target: carrier === 'tmdb' ? carrierTarget : { ...target, title: '星河 第2季', members: lateMembers },
      selectionMembers: lateMembers,
      ...(carrier === 'tmdb' ? { metadataTarget: carrierTarget, metadata: carrierMetadata } : {}),
      results: [
        { sourceKey: 'a', items: [
          { vod_id: 0, vod_name: '星河 第1季' }, { vod_id: '0', vod_name: '星河 第2季' },
          { vod_id: 'not-selected', vod_name: '星河 第1季' },
        ] },
        { sourceKey: 'b', items: [
          { vod_id: 0, vod_name: '星河 第2季' }, { vod_id: '0', vod_name: '星河 第1季' },
        ] },
      ],
      search: [{ sourceKey: 'a', page: 2, completed: true }, { sourceKey: 'b', page: 1, completed: false }],
    });
    const unchanged = structuredClone(handoff);
    await f.service.command({ ...handoff, sessionId, action: 'search.update' }, { searchHandoff: handoff });
    await tick();
    const after = f.snapshot(sessionId), members = [...target.members, ...lateMembers];
    assert.deepEqual(after.target, { ...before.target, members });
    assert.deepEqual(after.attempt, before.attempt);
    assert.deepEqual(after.current, before.current);
    assert.equal(after.stage, before.stage);
    assert.deepEqual(after.sources.map(row => [row.source.key, row.source.detailId]),
      [['a', 0], ['a', '0'], ['a', 'not-selected'], ['b', 0], ['b', '0']]);
    assert.deepEqual(after.search.map(row => [row.page, row.completed]), [[2, true], [1, false]]);
    if (carrier === 'tmdb') assert.deepEqual(after.metadata, carrierMetadata);
    const repeated = searchContext(target, 2, {
      target: carrier === 'tmdb' ? carrierTarget : { ...target, members: [lateMembers[1]] },
      selectionMembers: [lateMembers[1]],
    });
    await f.service.command({ ...repeated, sessionId, action: 'search.update' }, { searchHandoff: repeated });
    await tick();
    assert.deepEqual(f.snapshot(sessionId).target, { ...before.target, members });
    assert.deepEqual(f.snapshot(sessionId).attempt, before.attempt);
    assert.deepEqual(f.snapshot(sessionId).current, before.current);
    assert.equal(f.snapshot(sessionId).stage, before.stage);
    assert.equal(computations.filter(operation => operation === 'recognition.matches').length, matching);
    assert.deepEqual(f.calls, calls, 'handoff updates do not schedule search, detail, expansion, pan or media work');
    assert.deepEqual(metadataReads, carrier === 'tmdb' ? [carrierTarget, carrierTarget] : []);
    assert.deepEqual(handoff, unchanged);
  });
}

test('E49 client member claims are not trusted and internal handoff facts stay bound to selection, revision and source scope', async t => {
  const f = fixture();
  t.after(() => f.service.close());
  const target = { id: 'group:星河', kind: 'group', title: '星河 第1季',
    members: [{ sourceKey: 'a', detailId: 0 }] };
  const { sessionId } = await f.open({ target });
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const claimedTarget = { ...target, title: '星河 第2季', members: [{ sourceKey: 'a', detailId: '0' }] };
  const proof = searchContext(target, 2, { target: claimedTarget, selectionMembers: claimedTarget.members });
  const input = searchContext(target, 1, {
    target: claimedTarget, selectionMembers: claimedTarget.members, searchHandoff: proof,
    results: [{ sourceKey: 'a', items: [{ vod_id: '0', vod_name: '星河 第2季' }] }],
  });
  await f.command(sessionId, 'search.update', input);
  assert.deepEqual(f.snapshot(sessionId).target, target);
  assert.equal(f.snapshot(sessionId).sources.some(row => row.source.detailId === '0'), true);
  const before = f.snapshot(sessionId);
  for (const [searchHandoff, code] of [
    [{ ...proof, selectionId: 'group:other' }, 'INVALID_REQUEST'],
    [{ ...proof, revision: 3 }, 'INVALID_REQUEST'],
    [{ ...proof, target: { ...claimedTarget, id: 'group:other' } }, 'INVALID_REQUEST'],
    [{ ...proof, selectionMembers: undefined }, 'INVALID_REQUEST'],
    [{ ...proof, selectionMembers: {} }, 'INVALID_REQUEST'],
    [{ ...proof, selectionMembers: [{ sourceKey: 'a', detailId: 0.5 }] }, 'INVALID_REQUEST'],
    [{ ...proof, selectionMembers: [{ sourceKey: 'c', detailId: 0 }] }, 'STALE_SOURCE'],
    [{ ...proof, target: { ...tv, id: target.id }, selectionMembers: [{ sourceKey: 'c', detailId: 0 }] }, 'STALE_SOURCE'],
  ]) {
    await assert.rejects(f.service.command({ ...input, sessionId, revision: 2, action: 'search.update' },
      { searchHandoff }), { code });
    await assert.rejects(f.open({ ...input, target, revision: 2 }, { searchHandoff }), { code });
    assert.deepEqual(f.snapshot(sessionId), before);
  }
  const emptyProof = searchContext(target, 2, {
    target: { ...tv, id: target.id }, selectionMembers: [],
    results: [{ sourceKey: 'a', items: [{ vod_id: '0', vod_name: target.title }] }],
  });
  await f.service.command({ ...emptyProof, sessionId, action: 'search.update' }, { searchHandoff: emptyProof });
  assert.deepEqual(f.snapshot(sessionId).target, target,
    'an empty selection proof never falls back to matching raw menu rows');
  assert.equal(f.calls.some(row => ['search', 'detail', 'expand', 'resolve', 'prepare'].includes(row[0])), false);
});

test('E49 a late Node search response cannot move a native-confirmed page 3 cursor backwards or reopen completed search', async t => {
  const gate = deferred();
  const f = fixture({ data: { async search(value, query, page) {
    f.calls.push(['search', value.key, page]); await gate.promise;
    return { page, hasMore: true, explicitPageCount: false, items: [{ id: 'old-page', title: query }] };
  } } });
  t.after(async () => { gate.resolve(); await f.service.close(); });
  const { sessionId } = (await f.open({ sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.more'); await until(() => f.calls.some(row => row[0] === 'search'));
  await f.command(sessionId, 'search.update', searchContext(movie, 1, {
    sources: [source('a')], results: [{ sourceKey: 'a', items: [{ detailId: 'new-page', title: 'Film' }] }],
    search: [{ sourceKey: 'a', page: 3, completed: true }],
  }));
  gate.resolve(); await until(() => !f.snapshot(sessionId).search[0].loading);
  assert.equal(f.snapshot(sessionId).search[0].page, 3);
  assert.equal(f.snapshot(sessionId).search[0].completed, true);
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => row.source.detailId), ['new-page', 'old-page']);
  await f.command(sessionId, 'search.more'); await delay(2);
  assert.deepEqual(f.calls.filter(row => row[0] === 'search'), [['search', 'a', 1]]);
  assert.equal(f.calls.some(row => row[0] === 'detail'), false);
});

test('E49 late search results and cursors survive a metadata switch without restoring its original carrier or display', async t => {
  const metadataCalls = [];
  const selectedMetadata = { id: 81, title: 'Film', runtime: 88, selected: 'douban' };
  const f = fixture({ data: {
    async targetMetadata(target) {
      metadataCalls.push([target.metadataProvider, target.metadataId]);
      return target.metadataProvider === 'douban' ? structuredClone(selectedMetadata)
        : { id: 17, title: 'Film', runtime: 100 };
    },
    async metadataSources() {
      return [{ provider: 'douban', metadataId: 81, kind: 'movie', title: 'Film', metadata: selectedMetadata }];
    },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open();
  await until(() => f.snapshot(sessionId).stage === 'idle' &&
    f.snapshot(sessionId).metadataSources.some(row => row.provider === 'douban'));
  await f.command(sessionId, 'play');
  const current = await f.current(sessionId);
  await f.command(sessionId, 'search.update', searchContext(movie, 1, { metadataTarget: movie }));
  const douban = f.snapshot(sessionId).metadataSources.find(row => row.provider === 'douban');
  await f.command(sessionId, 'browse.metadata', { metadataSourceId: douban.id });
  await until(() => !f.snapshot(sessionId).browsing.loading &&
    f.snapshot(sessionId).target.metadataProvider === 'douban');
  const before = f.snapshot(sessionId), calls = metadataCalls.length;
  const mediaCalls = f.calls.filter(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])).length;
  await f.command(sessionId, 'search.update', searchContext(movie, 2, {
    results: [{ sourceKey: 'b', items: [{ id: 'late-without-carrier', title: 'Film' }] }],
    search: [{ sourceKey: 'b', page: 2, completed: false }],
  }));
  const handoff = searchContext(movie, 3, {
    target: movie, selectionMembers: [], metadataTarget: movie, metadata: { id: 17, title: 'Wrong old display', runtime: 999 },
    results: [{ sourceKey: 'a', items: [{ id: 'late-with-old-carrier', title: 'Film' }] }],
    search: [{ sourceKey: 'a', page: 3, completed: true }],
  });
  await f.service.command({ ...handoff, sessionId, action: 'search.update' }, { searchHandoff: handoff });
  await tick();
  const after = f.snapshot(sessionId);
  assert.deepEqual(after.target, before.target);
  assert.deepEqual(after.metadata, selectedMetadata);
  assert.equal(after.metadataSourceId, douban.id);
  assert.equal(after.current.attemptId, current.id);
  assert.equal(after.attempt, null);
  assert.equal(after.sources.some(row => row.source.detailId === 'late-without-carrier'), true);
  assert.equal(after.sources.some(row => row.source.detailId === 'late-with-old-carrier'), true);
  assert.deepEqual(after.search.map(row => [row.sourceKey, row.page, row.completed]),
    [['a', 3, true], ['b', 2, false]]);
  assert.equal(metadataCalls.length, calls);
  assert.equal(f.calls.filter(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])).length, mediaCalls);
});

test('E49 hints arriving for linked group members still require current detail evidence and stop at the first verified hit', async t => {
  const owner = runtimeDataFixture(({ operation, siteId, payload }) => {
    if (operation === 'detail') {
      assert.notEqual(payload.id, 'unused');
      return { title: siteId === 'a' ? 'Another Film' : 'Film',
        lines: [{ id: 'actual', episodes: [{ id: `${siteId}-fresh`, name: 'Film.1080p' }] }] };
    }
    assert.equal(operation, 'play'); assert.equal(siteId, 'b'); assert.equal(payload.id, 'b-fresh');
    return { url: 'https://media.invalid/verified-update' };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const target = groupTarget();
  const { sessionId } = (await f.open({ platform: 'ios', target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.update', searchContext(target, 1, {
    resultHints: [{ sourceKey: 'a', items: [{ detailId: 0, title: 'Film', metadataProvider: 'tmdb', metadataId: 17 }] },
      { sourceKey: 'b', items: [{ detailId: 'good', title: 'Old nonmatching title' }, { detailId: 'unused', title: 'Film' }] }],
    search: ['a', 'b'].map(sourceKey => ({ sourceKey, page: 3, completed: true })),
  }));
  await delay(2);
  assert.deepEqual(owner.calls, []);
  assert.deepEqual(f.snapshot(sessionId).candidates, []);
  await f.command(sessionId, 'play');
  const pending = await f.pending(sessionId);
  assert.equal(pending.candidate.source.key, 'b');
  assert.equal(pending.candidate.source.detailId, 'good');
  assert.equal(f.snapshot(sessionId).sources[0].error, 'NO_MATCH');
  assert.deepEqual(owner.calls.map(row => [row.operation, row.siteId, row.payload.id]),
    [['detail', 'a', 0], ['detail', 'b', 'good'], ['play', 'b', 'b-fresh']]);
  assert.deepEqual(f.snapshot(sessionId).target.members,
    [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'good' }]);
});

test('E49 a real late movie carrier upgrades an unselected iOS group through the full shared data cache and prepares only its first hit', async t => {
  const owner = runtimeDataFixture(({ operation, payload, siteId }) => {
    assert.equal(siteId, 'a');
    if (operation === 'detail') { assert.equal(payload.id, 0);
      return { id: 0, title: 'Actual member with another site display title',
        lines: [{ id: 'direct', episodes: [{ id: 'actual-feature', name: 'Film.1080p' }] }] };
    }
    assert.equal(operation, 'play'); assert.equal(payload.id, 'actual-feature');
    return { url: 'https://media.invalid/late-movie' };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'unused' }]);
  const { sessionId } = (await f.open({ platform: 'ios', target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  assert.deepEqual(owner.calls, []);
  await f.command(sessionId, 'search.update', searchContext(target, 1, {
    metadataTarget: movie, metadata: { id: 17, title: 'Do not trust display as complete' },
  }));
  const pending = await f.pending(sessionId);
  assert.equal(pending.prepareOnly, true);
  assert.equal(pending.candidate.name, 'Film.1080p');
  assert.equal(pending.candidate.episode, undefined);
  assert.equal(f.snapshot(sessionId).target.kind, 'movie');
  assert.equal(f.snapshot(sessionId).target.id, target.id);
  assert.equal(f.snapshot(sessionId).target.metadataId, 17);
  assert.equal(f.snapshot(sessionId).metadata.runtime, 100);
  assert.equal(f.snapshot(sessionId).current, null);
  assert.equal(f.library.read().history.length, 0);
  assert.deepEqual(owner.metadataCalls, [{ path: '/3/movie/17',
    query: { language: 'zh-CN', append_to_response: 'credits,similar,images' }, imageSize: 'original' }]);
  assert.deepEqual(owner.calls.map(row => [row.operation, row.siteId]), [['detail', 'a'], ['play', 'a']]);
});

test('E49 a late real TV carrier prepares the metadata first episode rather than a higher-quality next file', async t => {
  const owner = runtimeDataFixture(({ operation }) => operation === 'detail'
    ? { id: 0, title: 'Series', lines: [{ id: 'direct', episodes: [
      { id: 'first', name: 'Series.S01E01.1080p' }, { id: 'next', name: 'Series.S01E02.4K' },
    ] }] } : { url: 'https://media.invalid/late-series' },
  ({ path }) => path.endsWith('/season/1') ? { ...metadata.seasons[0] }
    : { ...metadata, ...tmdbAppended, id: 19, name: 'Series', genres: [] });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const target = { ...groupTarget(), title: 'Series' };
  const { sessionId } = (await f.open({ platform: 'ios', target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.update', searchContext(target, 1, { metadataTarget: tv }));
  const pending = await f.pending(sessionId);
  assert.equal(pending.prepareOnly, true);
  assert.equal(pending.candidate.episode, 1); assert.equal(pending.candidate.globalEpisode, 1);
  assert.equal(f.snapshot(sessionId).target.id, target.id);
  assert.equal(f.snapshot(sessionId).target.kind, 'tv');
  assert.equal(f.snapshot(sessionId).episodes.length, 2);
  assert.deepEqual(owner.calls.map(row => row.operation), ['detail', 'play']);
  assert.deepEqual(owner.metadataCalls.map(row => row.path), ['/3/tv/19', '/3/tv/19/season/1']);
});

test('E49 an output-free Windows group can upgrade on a real carrier and resumes the existing platform automatic-play rule', async t => {
  let ready = false;
  const f = fixture({ data: {
    async targetMetadata(target) { f.calls.push(['metadata', target.kind]);
      return target.kind === 'group' ? null : { id: 17, title: 'Film', genres: [], runtime: 100 }; },
    async detail(value, id) { f.calls.push(['detail', value.key, id]); if (!ready) throw error('UPSTREAM_HTTP');
      return detail(['Film.1080p']); },
  } });
  t.after(() => f.service.close());
  const target = groupTarget();
  const { sessionId } = (await f.open({ platform: 'windows', target, sources: [source('a')],
    search: [{ sourceKey: 'a', page: 3, completed: true }] }));
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  ready = true;
  await f.command(sessionId, 'search.update', searchContext(target, 1, {
    sources: [source('a')], metadataTarget: movie,
  }));
  const pending = await f.pending(sessionId);
  assert.equal(pending.prepareOnly, false);
  assert.equal(f.snapshot(sessionId).target.kind, 'movie');
  assert.equal(f.snapshot(sessionId).target.id, target.id);
  assert.equal(f.calls.some(row => row[0] === 'search'), false);
});

for (const output of ['pending', 'current']) {
  test(`E49 late results and metadata only supplement a group with ${output} and never replace its media intent`, async t => {
    const f = fixture({ data: {
      async targetMetadata(target) { f.calls.push(['metadata', target.kind]);
        return target.kind === 'group' ? null : { id: 17, title: 'Film', runtime: 100 }; },
      async detail(value, id) { f.calls.push(['detail', value.key, id]); return detail(['Film.S01E01.1080p']); },
    } });
    t.after(() => f.service.close());
    const target = groupTarget();
    const { sessionId } = (await f.open({ platform: 'windows', target }));
    const selected = output === 'current' ? await f.current(sessionId) : await f.pending(sessionId);
    const before = f.snapshot(sessionId);
    const resolves = f.calls.filter(row => row[0] === 'resolve').length;
    await f.command(sessionId, 'search.update', searchContext(target, 1, {
      results: [{ sourceKey: 'b', items: [{ detailId: 'late-b', title: 'Film' }] }],
      search: [{ sourceKey: 'b', page: 3, completed: true }], metadataTarget: movie,
    }));
    await until(() => f.snapshot(sessionId).metadata?.runtime === 100);
    const after = f.snapshot(sessionId);
    assert.equal(after.target.kind, 'group');
    assert.equal(after.target.source.candidateId, selected.candidate.id);
    assert.deepEqual(after.target.members, [...before.target.members, { sourceKey: 'b', detailId: 'late-b' }]);
    assert.deepEqual(after.attempt, before.attempt);
    assert.deepEqual(after.current, before.current);
    assert.deepEqual(after.episodes, []);
    assert.equal(after.metadataSources[0].kind, 'movie');
    assert.equal(f.calls.filter(row => row[0] === 'resolve').length, resolves);
    assert.deepEqual(f.calls.filter(row => row[0] === 'detail').map(row => row[1]), ['a']);
    assert.equal(f.calls.some(row => row[0] === 'search'), false);
    await f.command(sessionId, 'browse.metadata', { metadataSourceId: after.metadataSources[0].id });
    await until(() => f.snapshot(sessionId).target.kind === 'movie');
    assert.equal(f.snapshot(sessionId).target.id, target.id);
    assert.equal(f.calls.filter(row => row[0] === 'resolve').length, resolves);
    if (output === 'current') assert.equal(f.snapshot(sessionId).current.attemptId, selected.id);
  });
}

for (const savedSelection of [false, true]) {
  test(`E49 user-selected group never auto upgrades after its output disappears (selection restored=${savedSelection})`, async t => {
    const f = fixture({ data: {
      async targetMetadata(target) { return target.kind === 'group' ? null : { id: 17, title: 'Film', runtime: 100 }; },
      async detail() { return detail(['Film.S01E01']); },
    } });
    t.after(() => f.service.close());
    const first = (await f.open({ platform: 'ios', target: groupTarget() }));
    await until(() => f.snapshot(first.sessionId).stage === 'idle');
    await f.command(first.sessionId, 'browse', { sourceId: f.snapshot(first.sessionId).sources[0].id });
    await until(() => f.snapshot(first.sessionId).browsing.episodes.length === 1);
    await f.command(first.sessionId, 'select.candidate', {
      candidateId: f.snapshot(first.sessionId).browsing.episodes[0].candidateId,
    });
    const selected = await f.pending(first.sessionId);
    const saved = f.snapshot(first.sessionId).target;
    const receipt = f.service.attempt({ sessionId: first.sessionId, attemptId: selected.id });
    await f.service.releaseMedia({ sessionId: first.sessionId, attemptId: selected.id, resourceId: receipt.id });
    let sessionId = first.sessionId;
    if (savedSelection) {
      await f.service.release({ sessionId });
      sessionId = (await f.open({ platform: 'ios', target: saved })).sessionId;
      await until(() => f.snapshot(sessionId).stage === 'idle');
    }
    const resolves = f.calls.filter(row => row[0] === 'resolve').length;
    await f.command(sessionId, 'search.update', searchContext(saved, 1, { metadataTarget: movie }));
    await until(() => f.snapshot(sessionId).metadata?.runtime === 100);
    assert.equal(f.snapshot(sessionId).target.kind, 'group');
    assert.equal(f.snapshot(sessionId).target.source.candidateId, selected.candidate.id);
    assert.equal(f.snapshot(sessionId).attempt, null); assert.equal(f.snapshot(sessionId).current, null);
    assert.equal(f.calls.filter(row => row[0] === 'resolve').length, resolves);
  });
}

test('E49 only the latest asynchronous carrier may publish and a revision without a new carrier keeps the pending association', async t => {
  const gates = new Map([[17, deferred()], [18, deferred()]]);
  const f = fixture({ data: { async targetMetadata(target) {
    if (target.kind === 'group') return null;
    f.calls.push(['metadata', target.metadataId]); return gates.get(target.metadataId).promise;
  } } });
  t.after(async () => { for (const gate of gates.values()) gate.resolve({ id: 17, title: 'Film' }); await f.service.close(); });
  const target = groupTarget();
  const { sessionId } = (await f.open({ target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.update', searchContext(target, 1, { metadataTarget: movie }));
  await until(() => f.calls.some(row => row[1] === 17));
  await f.command(sessionId, 'search.update', searchContext(target, 2, { metadataTarget: { ...movie, metadataId: 18 } }));
  await until(() => f.calls.some(row => row[1] === 18));
  gates.get(18).resolve({ id: 18, title: 'Film', runtime: 120 });
  await until(() => f.snapshot(sessionId).metadata?.id === 18);
  gates.get(17).resolve({ id: 17, title: 'Film', runtime: 100 }); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).metadata.id, 18);
  assert.equal(f.snapshot(sessionId).target.metadataId, 18);
  assert.deepEqual(f.snapshot(sessionId).metadataSources.map(row => row.metadataId), [18]);
  assert.equal(f.calls.some(row => row[0] === 'resolve'), false);

  const gate = deferred();
  const g = fixture({ data: { async targetMetadata(value) {
    if (value.kind === 'group') return null;
    g.calls.push(['metadata', value.metadataId]); return gate.promise;
  } } });
  t.after(async () => { gate.resolve({ id: 17, title: 'Film' }); await g.service.close(); });
  const opened = (await g.open({ target }));
  await until(() => g.snapshot(opened.sessionId).stage === 'idle');
  await g.command(opened.sessionId, 'search.update', searchContext(target, 1, { metadataTarget: movie }));
  await until(() => g.calls.length === 1);
  await g.command(opened.sessionId, 'search.update', searchContext(target, 2, {
    results: [{ sourceKey: 'b', items: [{ detailId: 'late', title: 'Film' }] }],
  }));
  gate.resolve({ id: 17, title: 'Film', runtime: 100 });
  await until(() => g.snapshot(opened.sessionId).target.kind === 'movie');
  assert.equal(g.snapshot(opened.sessionId).metadata.id, 17);
  assert.equal(g.snapshot(opened.sessionId).target.id, target.id);
  assert.equal(g.calls.some(row => row[0] === 'search' || row[0] === 'detail'), false);
});

for (const ending of ['stop', 'release', 'source', 'metadata']) {
  test(`E49 ${ending} revokes late search-update publication without aborting its ordinary finite metadata producer`, async t => {
    const gate = deferred();
    const f = fixture({ data: { async targetMetadata(target) {
      if (target.kind === 'group') return null;
      f.calls.push(['metadata', target.metadataId]); return gate.promise;
    } } });
    t.after(async () => { gate.resolve({ id: 17, title: 'Film' }); await f.service.close(); });
    const target = groupTarget();
    const { sessionId } = (await f.open({ platform: 'ios', target }));
    await until(() => f.snapshot(sessionId).stage === 'idle');
    await f.command(sessionId, 'search.update', searchContext(target, 1, { metadataTarget: movie }));
    await until(() => f.calls.some(row => row[0] === 'metadata'));
    if (ending === 'stop') await f.command(sessionId, 'stop');
    else if (ending === 'release') await f.service.release({ sessionId });
    else await f.service.invalidate(ending === 'source' ? { kind: 'source', engine: 'cat', installationId: 'install-a' } : 'tmdb');
    const before = ending === 'release' ? null : f.snapshot(sessionId);
    gate.resolve({ id: 17, title: 'Film', runtime: 100 }); await delay(2); await delay(2);
    if (ending === 'release') {
      assert.throws(() => f.snapshot(sessionId), { code: 'NOT_FOUND' });
      await assert.rejects(f.command(sessionId, 'search.update', searchContext(target, 2)), { code: 'NOT_FOUND' });
    } else {
      assert.equal(f.snapshot(sessionId).target.kind, 'group');
      assert.deepEqual(f.snapshot(sessionId).metadataSources, []);
      assert.equal(f.snapshot(sessionId).sequence, before.sequence);
      assert.equal(f.snapshot(sessionId).attempt, null);
      if (ending === 'stop') {
        await f.command(sessionId, 'search.update', searchContext(target, 2, { metadataTarget: movie }));
        assert.equal(f.snapshot(sessionId).stage, 'stopped');
      } else if (ending === 'source') {
        await assert.rejects(f.command(sessionId, 'search.update', searchContext(target, 2)), { code: 'STALE_SOURCE' });
      }
    }
    assert.deepEqual(f.calls.filter(row => ['metadata', 'search', 'detail', 'resolve', 'prepare'].includes(row[0])),
      [['metadata', 17]]);
  });
}

test('E49 search.update raw scope keeps typed detail zero and refuses metadata association and unrelated seeds or records', async t => {
  const f = fixture();
  t.after(() => f.service.close());
  const target = { id: 'raw:update', kind: 'source', title: 'Raw', source: { key: 'a', detailId: 0 } };
  const { sessionId } = (await f.open({ target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.update', searchContext(target, 1, {
    results: [{ sourceKey: 'a', items: [{ detailId: 0, title: 'Current raw' }, { detailId: '0', title: 'Not typed zero' }] },
      { sourceKey: 'b', items: [{ detailId: 0, title: 'Other source' }] }],
    search: [{ sourceKey: 'b', page: 4, completed: true }],
  }));
  assert.deepEqual(f.snapshot(sessionId).sources.map(row => [row.source.key, row.source.detailId]), [['a', 0]]);
  await assert.rejects(f.command(sessionId, 'search.update', searchContext(target, 2, { metadataTarget: movie })),
    { code: 'INVALID_REQUEST' });
  await f.command(sessionId, 'search.update', searchContext(target, 2, { cacheEntries: [
    { kind: 'detail', source: source('a'), detailId: '0' },
    { kind: 'detail', source: source('b'), detailId: 0 },
    { kind: 'search', source: source('a'), query: 'Raw', page: 1 },
  ] }));
  await delay(2);
  assert.equal(f.calls.some(row => row[0] === 'handoff'), false);
  assert.equal(f.calls.some(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])), false);
});

const rawVariants = () => ({ id: 0, title: 'Actual raw', lines: [
  { id: 'a', provider: 'quark', episodes: [
    { id: 'private-a4-e1', name: 'Film.S01E01.A.4K' }, { id: 'private-a4-e2', name: 'Film.S01E02.4K' },
    { id: 'private-a1-e1', name: 'Film.S01E01.B.1080p' }, { id: 'private-a1-e2', name: 'Film.S01E02.1080p' },
  ] },
  { id: 'b', provider: 'baidu', episodes: [
    { id: 'private-b4-e1', name: 'Film.S01E01.B.4K' }, { id: 'private-b4-e2', name: 'Film.S01E02.4K' },
    { id: 'private-b1-e1', name: 'Film.S01E01.B.1080p' }, { id: 'private-b1-e2', name: 'Film.S01E02.1080p' },
  ] },
] });

for (const action of ['media.failed', 'switch', 'pan', 'quality', 'block']) {
  test(`E15–20 raw ${action} unlocks only this episode while preserving exact opaque reopen and paused progress`, async t => {
    const original = rawVariants();
    const owner = runtimeDataFixture(({ operation, payload, siteId }) => {
      assert.equal(siteId, 'a');
      if (operation === 'detail') { assert.equal(payload.id, 0); return original; }
      assert.equal(operation, 'play');
      assert.ok(payload.id.endsWith('e1'), 'A different episode is never a source replacement');
      return { url: 'https://media.invalid/raw-same-episode' };
    });
    const f = fixture({ data: owner.data });
    t.after(async () => { await f.service.close(); await owner.close(); });
    const target = { id: `raw:${action}`, kind: 'source', title: 'Raw',
      source: { key: 'a', detailId: 0, lineId: 'a', playId: 'private-a4-e1' } };
    const { sessionId } = (await f.open({ platform: 'windows', target }));
    const first = await f.current(sessionId);
    const receipt = f.service.attempt({ sessionId, attemptId: first.id });
    await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 53, duration: 180, paused: true });
    await f.command(sessionId, action, action === 'media.failed' || action === 'block' ? { attemptId: first.id }
      : action === 'pan' ? { provider: 'baidu' } : action === 'quality' ? { quality: '1080p' } : {});
    const replacement = await f.pending(sessionId);
    assert.notEqual(replacement.candidate.id, first.candidate.id);
    assert.equal(replacement.candidate.episode, 1); assert.equal(replacement.candidate.season, 1);
    assert.equal(replacement.candidate.globalEpisode, undefined);
    assert.ok(replacement.candidate.name.includes('.B.'));
    assert.equal(replacement.candidate.provider, action === 'quality' ? 'quark' : 'baidu');
    assert.equal(replacement.candidate.quality, action === 'quality' ? '1080p' : '4k');
    assert.equal(replacement.position, 53); assert.equal(replacement.paused, true);
    if (action !== 'media.failed') {
      assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
      assert.equal(f.resources.has(receipt.id), true);
    }
    const saved = f.snapshot(sessionId).target;
    assert.equal(saved.source.candidateId, replacement.candidate.id);
    assert.equal('playId' in saved.source, false); assert.equal('episodeId' in saved.source, false);
    assert.equal(owner.calls.some(row => row.operation === 'search'), false);
    assert.deepEqual(f.library.read().blocks.map(row => row.lineId), action === 'block' ? ['a'] : []);
    await f.command(sessionId, 'media.started', { attemptId: replacement.id }); await delay(2);
    assert.equal(f.resources.has(receipt.id), false);

    for (const missing of [false, true]) {
      const currentRaw = rawVariants();
      if (missing) {
        const line = currentRaw.lines.find(row => row.id === replacement.candidate.lineId);
        line.episodes = line.episodes.filter(row => row.name !== replacement.candidate.name);
      }
      const current = runtimeDataFixture(({ operation, payload }) => {
        if (operation === 'detail') return currentRaw;
        assert.equal(operation, 'play'); assert.equal(missing, false);
        assert.ok(payload.id.endsWith('e1'));
        return { url: 'https://media.invalid/reopened-raw' };
      });
      const g = fixture({ data: current.data });
      t.after(async () => { await g.service.close(); await current.close(); });
      const reopened = (await g.open({ platform: 'windows', target: saved, sources: [source('a')] }));
      if (missing) {
        await until(() => g.snapshot(reopened.sessionId).stage === 'unavailable');
        assert.equal(g.snapshot(reopened.sessionId).error, 'NO_MATCH');
        assert.equal(g.snapshot(reopened.sessionId).attempt, null);
        assert.deepEqual(current.calls.map(row => row.operation), ['detail']);
      } else {
        const exact = await g.pending(reopened.sessionId);
        assert.equal(exact.candidate.id, replacement.candidate.id);
        assert.deepEqual(current.calls.map(row => row.operation), ['detail', 'play']);
      }
    }
  });
}

for (const clue of ['different-episode', 'different-season', 'no-coordinates']) {
  test(`E15 raw switch with ${clue} never guesses an alternative and preserves the still-playable donor`, async t => {
    const firstName = clue === 'no-coordinates' ? 'Unnumbered first.4K' : 'Film.E01.4K';
    const otherName = clue === 'different-episode' ? 'Film.E02.4K'
      : clue === 'different-season' ? 'Film.S02E01.4K' : 'Unnumbered second.4K';
    const f = fixture({ data: { async detail() { return { lines: [
      { id: 'a', episodes: [{ id: 'a', name: firstName }] },
      { id: 'b', episodes: [{ id: 'b', name: otherName }] },
    ] }; } } });
    t.after(() => f.service.close());
    const { sessionId } = (await f.open({ platform: 'windows', target: { id: `raw:${clue}`, kind: 'source', title: 'Raw',
      source: { key: 'a', detailId: 0, lineId: 'a', playId: 'a' } } }));
    const first = await f.current(sessionId);
    const saved = f.snapshot(sessionId).target;
    const receipt = f.service.attempt({ sessionId, attemptId: first.id });
    await f.command(sessionId, 'switch');
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
    assert.equal(f.snapshot(sessionId).attempt, null);
    assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
    assert.deepEqual(f.snapshot(sessionId).target, saved);
    assert.equal(f.resources.has(receipt.id), true);
    assert.deepEqual(f.calls.filter(row => row[0] === 'resolve').map(row => row[2]), [firstName]);
    assert.equal(f.calls.some(row => row[0] === 'search'), false);
  });
}

test('E15 failing a raw provisional replacement preserves the older output and continues only within the same episode', async t => {
  const f = fixture({ data: { async detail() { return rawVariants(); } } });
  t.after(() => f.service.close());
  const { sessionId } = (await f.open({ platform: 'windows', target: { id: 'raw:provisional-failure', kind: 'source', title: 'Raw',
    source: { key: 'a', detailId: 0, lineId: 'a', playId: 'private-a4-e1' } } }));
  const first = await f.current(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: first.id });
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 41, duration: 100, paused: true });
  await f.command(sessionId, 'switch');
  const second = await f.pending(sessionId);
  await f.command(sessionId, 'media.failed', { attemptId: second.id });
  const third = await f.pending(sessionId);
  assert.notEqual(third.id, second.id);
  assert.notEqual(third.candidate.id, first.candidate.id); assert.notEqual(third.candidate.id, second.candidate.id);
  assert.equal(third.candidate.episode, 1); assert.equal(third.position, 41); assert.equal(third.paused, true);
  assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
  assert.equal(f.resources.has(receipt.id), true);
  assert.equal(f.calls.some(row => row[0] === 'resolve' && row[2].includes('E02')), false);
});

for (const kind of ['source', 'group']) {
  const sources = kind === 'source' ? [source('a')] : [source('a'), source('b')];
  const target = kind === 'source'
    ? { id: 'raw:ncr', kind, title: 'Raw', source: { key: 'a', detailId: 0, lineId: 'a', playId: 'private-a4-e1' } }
    : groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'second' }]);
  const currentDetail = value => {
    const raw = rawVariants();
    return { lines: raw.lines.filter(row => kind === 'source' || row.id === value.key)
      .map(row => ({ ...row, episodes: row.episodes.slice(0, 2) })) };
  };
  const search = sources.map(row => ({ sourceKey: row.key, page: 1, completed: true }));

  for (const releasedDonor of [false, true]) {
    test(`NCR-02 ${kind} exhausted same-episode replacement restores only a still-valid donor target (released=${releasedDonor})`, async t => {
      const f = fixture({ data: { async detail(value) { return currentDetail(value); } } });
      t.after(() => f.service.close());
      const { sessionId } = (await f.open({ platform: 'windows', target, sources, search }));
      const first = await f.current(sessionId), saved = f.snapshot(sessionId).target;
      const receipt = f.service.attempt({ sessionId, attemptId: first.id });
      await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 41, duration: 100, paused: true });
      await f.command(sessionId, 'switch');
      const second = await f.pending(sessionId), failedTarget = f.snapshot(sessionId).target;
      assert.notEqual(second.candidate.id, first.candidate.id);
      assert.equal(second.candidate.episode, 1);
      if (releasedDonor) await f.service.releaseMedia({ sessionId, attemptId: first.id, resourceId: receipt.id });
      await f.command(sessionId, 'media.failed', { attemptId: second.id });
      await until(() => f.snapshot(sessionId).stage === 'unavailable');
      const exhausted = f.snapshot(sessionId);
      assert.equal(exhausted.error, 'NO_MATCH'); assert.equal(exhausted.attempt, null);
      assert.deepEqual(exhausted.target, releasedDonor ? failedTarget : saved);
      assert.equal(exhausted.current?.attemptId ?? null, releasedDonor ? null : first.id);
      assert.equal(f.resources.has(receipt.id), !releasedDonor);
      if (!releasedDonor) {
        assert.equal(exhausted.current.position, 41); assert.equal(exhausted.current.paused, true);
        await f.command(sessionId, 'play');
        assert.equal(f.snapshot(sessionId).stage, 'playing');
        assert.equal(f.snapshot(sessionId).attempt, null);
        const reopened = fixture({ data: { async detail(value) { return currentDetail(value); } } });
        t.after(() => reopened.service.close());
        const page = (await reopened.open({ platform: 'windows', target: exhausted.target, sources, search }));
        assert.equal((await reopened.pending(page.sessionId)).candidate.id, first.candidate.id);
      }
      const resolved = f.calls.filter(row => row[0] === 'resolve');
      assert.equal(resolved.length, 2);
      if (!releasedDonor) {
        await f.command(sessionId, 'switch');
        await until(() => f.snapshot(sessionId).stage === 'unavailable');
        assert.deepEqual(f.calls.filter(row => row[0] === 'resolve'), resolved, 'Exhaustion must not clear failed-file exclusions');
      }
      assert.equal(resolved.some(row => row[2].includes('E02')), false);
    });
  }

  for (const selection of ['different-episode', 'different-file']) {
    test(`NCR-02 ${kind} exhaustion cannot roll back an explicit ${selection} selection to the donor`, async t => {
      let firstResolve = true;
      const f = fixture({ data: {
        async detail(value) { return currentDetail(value); },
        async resolve(candidate) {
          f.calls.push(['resolve', candidate.source.key, candidate.name]);
          if (!firstResolve && candidate.lineId === 'a') throw error('MEDIA_UNAVAILABLE');
          firstResolve = false;
          return { url: `https://media.invalid/${candidate.id}` };
        },
      } });
      t.after(() => f.service.close());
      const { sessionId } = (await f.open({ platform: 'windows', target, sources, search }));
      const first = await f.current(sessionId), saved = f.snapshot(sessionId).target;
      const receipt = f.service.attempt({ sessionId, attemptId: first.id });
      await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 41, duration: 100, paused: true });
      if (kind === 'group') {
        const row = f.snapshot(sessionId).sources.find(value => value.source.key === 'b');
        await f.command(sessionId, 'browse', { sourceId: row.id });
        await until(() => !f.snapshot(sessionId).browsing.loading);
      }
      const selected = f.snapshot(sessionId).candidates.find(row => row.lineId === 'b' &&
        row.episode === (selection === 'different-episode' ? 2 : 1));
      assert.ok(selected);
      await f.command(sessionId, 'select.candidate', { candidateId: selected.id });
      const second = await f.pending(sessionId), selectedTarget = f.snapshot(sessionId).target;
      assert.equal(second.candidate.id, selected.id);
      await f.command(sessionId, 'media.failed', { attemptId: second.id });
      await until(() => f.snapshot(sessionId).stage === 'unavailable');
      assert.deepEqual(f.snapshot(sessionId).target, selectedTarget);
      assert.notDeepEqual(selectedTarget, saved);
      assert.equal(f.snapshot(sessionId).current.attemptId, first.id);
      assert.equal(f.snapshot(sessionId).current.position, 41); assert.equal(f.snapshot(sessionId).current.paused, true);
      assert.equal(f.resources.has(receipt.id), true);
    });
  }

  for (const action of ['switch', 'pan', 'quality', 'select.candidate']) {
    test(`NCR-03 ${kind} old-current failure preserves a newer ${action} resolve and its exact intent`, async t => {
      const gate = deferred(), entered = deferred();
      let firstResolve = true;
      const f = fixture({ data: {
        async detail(value) {
          const raw = rawVariants();
          return { lines: raw.lines.filter(row => kind === 'source' || row.id === value.key) };
        },
        async resolve(candidate) {
          f.calls.push(['resolve', candidate.source.key, candidate.name, candidate.id]);
          if (!firstResolve) { entered.resolve(); await gate.promise; }
          firstResolve = false;
          return { url: `https://media.invalid/${candidate.id}` };
        },
      } });
      t.after(async () => { gate.resolve(); await f.service.close(); });
      const { sessionId } = (await f.open({ platform: 'windows', target, sources, search }));
      const first = await f.current(sessionId);
      const receipt = f.service.attempt({ sessionId, attemptId: first.id });
      await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 41, duration: 100, paused: true });
      const selected = action === 'select.candidate' ? f.snapshot(sessionId).candidates.find(row =>
        row.lineId === 'a' && row.episode === 2 && row.quality === '4k') : null;
      await f.command(sessionId, action, action === 'pan' ? { provider: 'baidu' }
        : action === 'quality' ? { quality: '1080p' }
          : action === 'select.candidate' ? { candidateId: selected.id } : {});
      await entered.promise;
      const latest = f.snapshot(sessionId);
      assert.equal(latest.attempt, null); assert.equal(latest.stage, 'resolving');
      await f.command(sessionId, 'media.failed', { attemptId: first.id });
      await delay(2);
      const waiting = f.snapshot(sessionId);
      assert.deepEqual(waiting.target, latest.target); assert.deepEqual(waiting.constraint, latest.constraint);
      assert.equal(waiting.current, null); assert.equal(waiting.stage, 'resolving');
      assert.equal(f.resources.has(receipt.id), false);
      assert.deepEqual(f.released, [receipt.id]);
      assert.equal(f.library.read().history[0].candidateId, first.candidate.id);
      assert.equal(f.library.read().history[0].episode.episode, 1);
      assert.equal(f.library.read().history[0].position, 41); assert.equal(f.library.read().history[0].paused, true);
      assert.equal(f.library.read().history[0].ended, false);
      gate.resolve();
      const replacement = await f.pending(sessionId);
      assert.equal(replacement.candidate.episode, action === 'select.candidate' ? 2 : 1);
      assert.equal(replacement.position, action === 'select.candidate' ? 0 : 41);
      assert.equal(replacement.paused, action !== 'select.candidate');
      if (action === 'pan') assert.equal(replacement.candidate.provider, 'baidu');
      if (action === 'quality') assert.equal(replacement.candidate.quality, '1080p');
      if (selected) assert.equal(replacement.candidate.id, selected.id);
      const resolved = f.calls.filter(row => row[0] === 'resolve');
      assert.equal(resolved.length, 2); assert.equal(resolved[1][3], replacement.candidate.id);
      await f.command(sessionId, 'media.started', { attemptId: replacement.id });
      assert.equal(f.snapshot(sessionId).current.attemptId, replacement.id);
      assert.deepEqual(f.released, [receipt.id]);
    });
  }
}

test('E49 group replacement crosses accurate members in its original scope but ordinary reopen remains the exact selected file', async t => {
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'second' }]);
  const owner = runtimeDataFixture(({ operation, siteId, payload }) => {
    if (operation === 'detail') {
      assert.equal(payload.id, siteId === 'a' ? 0 : 'second');
      return { id: payload.id, title: siteId === 'a' ? 'Film' : 'Different known member title',
        lines: [{ id: siteId, episodes: [{ id: `${siteId}-e1`, name: 'Film.S01E01.4K' },
          { id: `${siteId}-e2`, name: 'Film.S01E02.4K' }] }] };
    }
    assert.equal(operation, 'play'); assert.ok(payload.id.endsWith('e1'));
    return { url: 'https://media.invalid/group-switch' };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', target }));
  const first = await f.current(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 36, duration: 90, paused: true });
  await f.command(sessionId, 'switch');
  const second = await f.pending(sessionId);
  assert.equal(second.candidate.source.key, 'b'); assert.equal(second.candidate.episode, 1);
  assert.equal(second.position, 36); assert.equal(second.paused, true);
  const saved = f.snapshot(sessionId).target;
  assert.equal(saved.kind, 'group'); assert.deepEqual(saved.members, target.members);
  assert.equal(saved.source.candidateId, second.candidate.id);
  assert.equal(owner.calls.some(row => row.operation === 'search'), false);
  const current = runtimeDataFixture(({ operation, siteId, payload }) => {
    assert.equal(siteId, 'b');
    if (operation === 'detail') return { id: 'second', title: 'Renamed current title', lines: [
      { id: 'b', episodes: [{ id: 'b-e2', name: 'Film.S01E02.4K' }, { id: 'b-e1', name: 'Film.S01E01.4K' }] },
    ] };
    assert.equal(operation, 'play'); assert.equal(payload.id, 'b-e1');
    return { url: 'https://media.invalid/group-reopen' };
  });
  const g = fixture({ data: current.data });
  t.after(async () => { await g.service.close(); await current.close(); });
  const reopened = (await g.open({ platform: 'windows', target: saved }));
  assert.equal((await g.pending(reopened.sessionId)).candidate.id, second.candidate.id);
  assert.deepEqual(current.calls.map(row => [row.operation, row.siteId]), [['detail', 'b'], ['play', 'b']]);
});

test('E49 an ordinary nonmember menu result cannot silently become a selected member of the group', async t => {
  const f = fixture();
  t.after(() => f.service.close());
  const target = groupTarget();
  const { sessionId } = (await f.open({ target, results: [{ sourceKey: 'b', items: [{ detailId: 'other-film', title: 'Another Film' }] }] }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const other = f.snapshot(sessionId).sources.find(row => row.source.detailId === 'other-film');
  await f.command(sessionId, 'browse', { sourceId: other.id });
  await until(() => f.snapshot(sessionId).browsing.episodes.length === 2);
  await assert.rejects(f.command(sessionId, 'select.candidate', {
    candidateId: f.snapshot(sessionId).browsing.episodes[0].candidateId,
  }), { code: 'NO_MATCH' });
  assert.deepEqual(f.snapshot(sessionId).target, target);
  assert.equal(f.calls.some(row => row[0] === 'resolve'), false);
});

test('E49 removing A preserves B as the original group member and live owner without admitting a new C', async t => {
  const f = fixture({ data: {
    async targetMetadata() { return null; },
    async detail(value) { f.calls.push(['detail', value.key]); return detail(['Film.S01E01.4K', 'Film.S01E02.4K']); },
  } });
  t.after(() => f.service.close());
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'B' }]);
  const { sessionId } = (await f.open({ platform: 'windows', target }));
  const first = await f.current(sessionId);
  await f.command(sessionId, 'switch');
  const b = await f.current(sessionId);
  assert.equal(b.candidate.source.key, 'b');
  const aDetails = f.calls.filter(row => row[0] === 'detail' && row[1] === 'a').length;
  const receipt = f.service.attempt({ sessionId, attemptId: b.id });
  await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' }, []);
  assert.deepEqual(f.snapshot(sessionId).target.members, [{ sourceKey: 'b', detailId: 'B' }]);
  assert.equal(f.snapshot(sessionId).current.attemptId, b.id);
  assert.equal(f.resources.has(receipt.id), true);
  assert.equal(f.snapshot(sessionId).candidates.every(row => row.source.key === 'b'), true);
  await assert.rejects(f.command(sessionId, 'search.update', searchContext(target, 1, { sources: [source('c')] })),
    { code: 'STALE_SOURCE' });
  await f.command(sessionId, 'next');
  const next = await f.pending(sessionId);
  assert.equal(next.candidate.source.key, 'b'); assert.equal(next.candidate.episode, 2);
  assert.deepEqual(f.snapshot(sessionId).target.members, [{ sourceKey: 'b', detailId: 'B' }]);
  assert.notEqual(first.candidate.id, next.candidate.id);
  assert.equal(f.calls.filter(row => row[0] === 'detail' && row[1] === 'a').length, aDetails);
});

for (const knownEpisode of [true, false]) {
  test(`E49 revoked selected group file keeps exact reopen and only explicit proven same-episode reselection reaches B (numbered=${knownEpisode})`, async t => {
    const owner = runtimeDataFixture(({ operation, siteId, payload }) => {
      if (operation === 'detail') return { id: payload.id, title: 'Film', lines: [{ id: siteId,
        episodes: [{ id: `${siteId}-e1`, name: knownEpisode ? 'Film.S01E01.4K' : 'Unnumbered first.4K' },
          { id: `${siteId}-e2`, name: 'Film.S01E02.4K' }] }] };
      if (operation === 'search') return { list: [], pagecount: 1 };
      assert.equal(operation, 'play'); assert.ok(payload.id.endsWith('e1'));
      return { url: 'https://media.invalid/group-revocation' };
    });
    const f = fixture({ data: owner.data });
    t.after(async () => { await f.service.close(); await owner.close(); });
    const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'B' }]);
    const { sessionId } = (await f.open({ platform: 'windows', target }));
    const first = await f.current(sessionId);
    await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 29, duration: 90, paused: true });
    const receipt = f.service.attempt({ sessionId, attemptId: first.id });
    await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' },
      [{ installationId: 'install-a', generation: 2, state: 'ready', sites: [{ key: 'a' }] }]);
    const saved = f.snapshot(sessionId).target;
    assert.equal(saved.source.candidateId, first.candidate.id);
    assert.deepEqual(saved.members, [{ sourceKey: 'b', detailId: 'B' }]);
    assert.equal(f.snapshot(sessionId).current, null);
    assert.equal(f.resources.has(receipt.id), false);
    await f.command(sessionId, 'play');
    await until(() => f.snapshot(sessionId).stage === 'unavailable');
    assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
    assert.deepEqual(owner.calls.map(row => row.siteId), ['a', 'a']);
    await f.command(sessionId, 'switch');
    if (knownEpisode) {
      const replacement = await f.pending(sessionId);
      assert.equal(replacement.candidate.source.key, 'b'); assert.equal(replacement.candidate.episode, 1);
      assert.equal(replacement.position, 29); assert.equal(replacement.paused, true);
      assert.deepEqual(f.snapshot(sessionId).target.members, saved.members);
      assert.equal(f.snapshot(sessionId).target.source.candidateId, replacement.candidate.id);
    } else {
      await until(() => f.snapshot(sessionId).stage === 'unavailable');
      assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
      assert.equal(f.snapshot(sessionId).attempt, null);
      assert.equal(owner.calls.some(row => row.siteId === 'b'), false);
    }
    assert.equal(owner.calls.filter(row => row.siteId === 'a').length, 2);
    const reopenedOwner = runtimeDataFixture(() => assert.fail('An unavailable exact selection must not fetch another member'));
    const g = fixture({ data: reopenedOwner.data });
    t.after(async () => { await g.service.close(); await reopenedOwner.close(); });
    const reopened = (await g.open({ platform: 'windows', target: saved, sources: [source('b')] }));
    await until(() => g.snapshot(reopened.sessionId).stage === 'unavailable');
    assert.equal(g.snapshot(reopened.sessionId).error, 'NO_MATCH');
    assert.deepEqual(g.snapshot(reopened.sessionId).target.members, saved.members);
    assert.deepEqual(reopenedOwner.calls, []);
  });
}

test('E49 a group with every original member revoked reports SOURCE_NOT_FOUND without searching or adding a replacement site', async t => {
  const f = fixture({ data: { async targetMetadata() { return null; } } });
  t.after(() => f.service.close());
  const target = groupTarget([{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: 'B' }]);
  const { sessionId } = (await f.open({ platform: 'ios', target }));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.service.invalidate({ kind: 'source', engine: 'cat' }, []);
  assert.deepEqual(f.snapshot(sessionId).target.members, []);
  assert.equal(f.snapshot(sessionId).error, 'SOURCE_NOT_FOUND');
  await f.command(sessionId, 'play');
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).error, 'SOURCE_NOT_FOUND');
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.calls.some(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])), false);
});

test('E49 a manual share outside the original group source scope is not fetched or added as a synthetic member', async t => {
  const f = fixture({ data: {
    async targetMetadata() { return null; },
    async detail(value, id) { f.calls.push(['detail', value.key, id]); return detail(['Actual first']); },
  } });
  t.after(() => f.service.close());
  const target = groupTarget();
  f.library.update(document => { document.manual.push({ id: 'outside-share', targetId: target.id,
    provider: 'quark', share: { url: 'https://pan.invalid/synthetic' }, enabled: true, failureCount: 0 }); });
  const { sessionId } = (await f.open({ platform: 'windows', target }));
  const pending = await f.pending(sessionId);
  assert.equal(pending.candidate.source.key, 'a');
  assert.deepEqual(f.snapshot(sessionId).target.members, target.members);
  assert.equal(f.calls.some(row => row[0] === 'expand' || row[0] === 'search'), false);
  assert.deepEqual(f.calls.filter(row => row[0] === 'detail'), [['detail', 'a', 0]]);
  assert.equal(f.library.read().manual[0].failureCount, 0);
});

test('E49 late group initialization cannot clear SOURCE_NOT_FOUND after every original member is revoked', async t => {
  const gate = deferred();
  const f = fixture({ data: { async targetMetadata() { return gate.promise; } } });
  t.after(async () => { gate.resolve(null); await f.service.close(); });
  const { sessionId } = (await f.open({ platform: 'ios', target: groupTarget() }));
  await f.service.invalidate({ kind: 'source', engine: 'cat' }, []);
  const before = f.snapshot(sessionId);
  gate.resolve(null); await delay(2); await delay(2);
  assert.equal(f.snapshot(sessionId).sequence, before.sequence);
  assert.equal(f.snapshot(sessionId).error, 'SOURCE_NOT_FOUND');
  assert.equal(f.snapshot(sessionId).stage, 'unavailable');
  assert.equal(f.calls.some(row => ['search', 'detail', 'resolve', 'prepare'].includes(row[0])), false);
});

test('E02 raw history and manual linkage cannot schedule another detail or a manual share outside the original typed scope', async t => {
  const f = fixture({ data: { async detail(value, id) {
    f.calls.push(['detail', value.key, id]); return detail(['Actual file']);
  } } });
  t.after(() => f.service.close());
  const target = { id: 'raw:old-links', kind: 'source', title: 'Raw', source: { key: 'a', detailId: 0 } };
  f.library.update(document => {
    document.history.push({ targetId: target.id, sourceKey: 'a', detailId: '0', candidateId: 'old',
      lineId: 'line', episode: {}, position: 80, duration: 100, paused: true, updatedAt: 1 });
    document.manual.push({ id: 'old-site', targetId: target.id, sourceKey: 'a', detailId: 'different', enabled: true, failureCount: 0 },
      { id: 'old-share', targetId: target.id, provider: 'quark', share: { url: 'https://pan.invalid/synthetic' },
        enabled: true, failureCount: 0 });
  });
  const { sessionId } = (await f.open({ platform: 'windows', target }));
  const pending = await f.pending(sessionId);
  assert.equal(pending.candidate.source.detailId, 0); assert.equal(pending.position, 0);
  assert.deepEqual(f.calls.filter(row => row[0] === 'detail'), [['detail', 'a', 0]]);
  assert.equal(f.calls.some(row => row[0] === 'expand' || row[0] === 'search'), false);
  assert.deepEqual(f.library.read().manual.map(row => row.failureCount), [0, 0]);
});

for (const context of ['results', 'resultHints', 'search']) {
  for (const id of [0, '0']) {
    test(`E01/E49 ${context} keeps provider typed ${typeof id} zero and ignores unknown nested raw identity/title`, async t => {
      const rows = [
        { vod_id: id, vod_name: 'Film', raw: { original: true } },
        { vod_id: id, id: 'ignored', detailId: 'ignored-detail', vod_name: 'Film',
          raw: { id: 'wrong', vod_id: 'wrong', vod_name: 'Another Film' } },
      ];
      const before = structuredClone(rows);
      const owner = runtimeDataFixture(({ operation, payload }) => {
        if (operation === 'search') return { list: rows, pagecount: 1 };
        if (operation === 'detail') {
          assert.equal(payload.id, id);
          return { list: [
            { vod_id: id === 0 ? '0' : 0, id, lines: [] },
            { ...rows[1], lines: [{ id: 'direct', episodes: [{ id: 'actual-file', name: 'Film.1080p' }] }] },
          ] };
        }
        assert.equal(operation, 'play'); assert.equal(payload.id, 'actual-file');
        return { url: 'https://media.invalid/typed-raw' };
      });
      const f = fixture({ data: owner.data });
      t.after(async () => { await f.service.close(); await owner.close(); });
      const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')],
        ...(context === 'search' ? {} : { [context]: [{ sourceKey: 'a', items: rows }] }) }));
      const attempt = await f.pending(sessionId);
      assert.equal(attempt.candidate.source.detailId, id);
      assert.equal(f.snapshot(sessionId).sources.length, 1);
      assert.deepEqual(owner.calls.filter(row => row.operation === 'detail').map(row => row.payload.id), [id]);
      assert.deepEqual(owner.calls.map(row => row.operation), context === 'search'
        ? ['search', 'detail', 'play'] : ['detail', 'play']);
      assert.deepEqual(rows, before);
    });
  }
}

test('E01/E49 an invalid nonnull vod_id is not rescued by id, detailId or a nested raw identity', async t => {
  const owner = runtimeDataFixture(({ operation }) => {
    assert.equal(operation, 'search');
    return { pagecount: 1, list: [false, '', {}, [], 0.5].map(vod_id =>
      ({ vod_id, id: 'must-not-request', detailId: 0, vod_name: 'Film', raw: { id: 'also-not-requested' } })) };
  });
  const f = fixture({ data: owner.data });
  t.after(async () => { await f.service.close(); await owner.close(); });
  const { sessionId } = (await f.open({ platform: 'windows', sources: [source('a')] }));
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
  assert.deepEqual(f.snapshot(sessionId).sources, []);
  assert.deepEqual(owner.calls.map(row => row.operation), ['search']);
});

test('metadata refresh retains stale success and the active file, then publishes a successful replacement', async t => {
  let phase = 'initial';
  const reads = [];
  const f = fixture({ data: {
    async targetMetadataEntry(target, options) {
      reads.push(options.refresh === true);
      return { value: { id: 17, title: phase === 'updated' ? 'Updated' : 'Original',
        overview: phase === 'updated' ? '' : 'Saved overview' },
      fetchedAt: phase === 'updated' ? 20 : 10, expiresAt: phase === 'stale' ? 11 : 100,
      completeness: 'complete', cache: { status: phase === 'stale' ? 'stale' : 'fresh',
        ...(phase === 'stale' ? { error: { code: 'NETWORK_ERROR' } } : {}),
        components: { detail: { status: 'ready', fetchedAt: phase === 'updated' ? 20 : 10 } } } };
    },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open({ platform: 'windows' });
  const current = await f.current(sessionId);
  const before = f.snapshot(sessionId), resolutions = f.calls.filter(row => row[0] === 'resolve').length;
  phase = 'stale';
  await f.command(sessionId, 'browse.refresh');
  await until(() => f.snapshot(sessionId).metadataCache?.status === 'stale');
  assert.equal(f.snapshot(sessionId).metadata.overview, 'Saved overview');
  assert.equal(f.snapshot(sessionId).metadataCache.components.detail.fetchedAt, 10);
  assert.equal(f.snapshot(sessionId).metadataError, 'NETWORK_ERROR');
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.deepEqual(f.snapshot(sessionId).target, before.target);
  phase = 'updated';
  await f.command(sessionId, 'browse.refresh', { refresh: false });
  await until(() => f.snapshot(sessionId).metadata.title === 'Updated');
  assert.equal(f.snapshot(sessionId).metadata.overview, '');
  assert.equal(f.snapshot(sessionId).metadataError, null);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.equal(f.calls.filter(row => row[0] === 'resolve').length, resolutions);
  assert.deepEqual(reads, [false, true, false]);
});

test('browsing a newly declared count-unknown season loads real episodes without changing the playing target', async t => {
  let updated = false;
  const data = {
    async targetMetadata() { return { ...structuredClone(metadata), seasons: [
      structuredClone(metadata.seasons[0]), ...(updated ? [{ season_number: 2, episode_count: 0 }] : []),
    ] }; },
    async seasonMetadata(target, season) {
      if (season === 1) return structuredClone(metadata.seasons[0]);
      assert.equal(season, 2);
      return { season_number: 2, episodes: [{ season_number: 2, episode_number: 1, name: 'New episode' }] };
    },
  };
  const f = fixture({ data }); t.after(() => f.service.close());
  const { sessionId } = await f.open({ target: { ...tv, season: 1, episode: 1 } });
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const target = f.snapshot(sessionId).target;
  updated = true;
  await f.command(sessionId, 'browse.season', { season: 2 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.equal(f.snapshot(sessionId).metadata.seasons.length, 2);
  assert.deepEqual(f.snapshot(sessionId).episodes.map(row => [row.season, row.episode, row.name]),
    [[2, 1, 'New episode']]);
  assert.deepEqual(f.snapshot(sessionId).target, target);
  assert.equal(f.calls.some(row => row[0] === 'resolve'), false);
});

test('explicit address retry re-resolves exactly the chosen current file and preserves its position', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = await f.open({ platform: 'windows' });
  const current = await f.current(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: current.id, position: 42, duration: 100, paused: true });
  const searches = f.calls.filter(row => row[0] === 'search').length;
  await f.command(sessionId, 'media.retry', { attemptId: current.id });
  const replacement = await f.pending(sessionId);
  assert.notEqual(replacement.id, current.id);
  assert.equal(replacement.candidate.id, current.candidate.id);
  assert.equal(replacement.position, 42);
  assert.equal(replacement.paused, true);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.deepEqual(f.calls.filter(row => row[0] === 'refreshAddress'), [['refreshAddress', current.candidate.id]]);
  assert.equal(f.calls.filter(row => row[0] === 'search').length, searches);
});

test('refreshing a browsed source refreshes only its detail and line and keeps old episodes on failure', async t => {
  const reads = [], expansions = [];
  let failRefresh = false;
  const f = fixture({ data: {
    async detail(source, id, options) {
      reads.push([source.key, id, options.refresh]);
      if (failRefresh && options.refresh) throw error('NETWORK_ERROR');
      return detail(['Film.1080p']);
    },
    async expand(source, line, options) { expansions.push([source.key, line.id, options.refresh]); return line; },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open();
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'search.more'); await until(() => f.snapshot(sessionId).searchComplete);
  const selected = f.snapshot(sessionId).sources[0];
  await f.command(sessionId, 'browse', { sourceId: selected.id });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  const old = f.snapshot(sessionId).browsing.episodes;
  await f.command(sessionId, 'browse.refresh');
  await until(() => reads.some(row => row[2] === true) && !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(reads.at(-1), ['a', 'detail-a', true]);
  assert.deepEqual(expansions.at(-1), ['a', 'line', true]);
  failRefresh = true;
  await f.command(sessionId, 'browse.refresh');
  await until(() => f.snapshot(sessionId).error === 'NETWORK_ERROR');
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes, old);
  assert.equal(reads.some(row => row[0] === 'b'), false);
  assert.equal(f.calls.some(row => row[0] === 'resolve'), false);
});

test('only a unique verified TMDB association supplements a Douban page without changing its identity', async t => {
  const target = { id: 'douban:movie:51', kind: 'movie', metadataProvider: 'douban', metadataId: 51, title: 'Film' };
  const f = fixture({ data: {
    async metadataSources(target, { metadata: value }) { return [
      { provider: 'douban', metadataId: 51, kind: 'movie', title: 'Film', metadata: value, association: { kind: 'identity' } },
      { provider: 'tmdb', metadataId: 17, kind: 'movie', title: 'Film', metadata: { overview: 'Supplement' },
        association: { kind: 'title-year', targetProvider: 'douban', targetMetadataId: 51 } },
    ]; },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open({ target });
  await until(() => f.snapshot(sessionId).metadataSupplement !== null);
  assert.equal(f.snapshot(sessionId).metadataSupplement.metadata.overview, 'Supplement');
  assert.deepEqual(f.snapshot(sessionId).target, target);
  assert.equal(f.calls.some(row => row[0] === 'resolve'), false);
});

test('cache refresh replaces association proof rather than retaining a removed supplement', async t => {
  let associated = true;
  const target = { id: 'douban:movie:51', kind: 'movie', metadataProvider: 'douban', metadataId: 51, title: 'Film' };
  const f = fixture({ data: {
    async metadataSources(target, { metadata: value }) { return [
      { provider: 'douban', metadataId: 51, kind: 'movie', title: 'Film', metadata: value, association: { kind: 'identity' } },
      ...(associated ? [{ provider: 'tmdb', metadataId: 17, kind: 'movie', title: 'Film',
        metadata: { overview: 'Verified supplement' },
        association: { kind: 'title-year', targetProvider: 'douban', targetMetadataId: 51 } }] : []),
    ]; },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open({ target });
  await until(() => f.snapshot(sessionId).metadataSupplement);
  associated = false;
  await f.command(sessionId, 'browse.refresh');
  await until(() => f.snapshot(sessionId).metadataSources.length === 1);
  assert.equal(f.snapshot(sessionId).metadataSupplement, null);
  assert.deepEqual(f.snapshot(sessionId).target, target);
});

for (const ending of ['stop', 'source']) {
  test(`cache address retry cannot publish its late response after ${ending}`, async t => {
    const waiting = deferred(), entered = deferred();
    const f = fixture({ data: { async refresh(candidate) {
      entered.resolve(); await waiting.promise;
      return { url: 'https://media.invalid/late-retry', scope: { sourceKey: candidate.source.key } };
    } } });
    t.after(() => f.service.close());
    const { sessionId } = await f.open({ platform: 'windows' });
    const current = await f.current(sessionId);
    await f.command(sessionId, 'media.retry', { attemptId: current.id });
    await entered.promise;
    if (ending === 'stop') await f.command(sessionId, 'stop');
    else await f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'install-a' }, []);
    waiting.resolve();
    await tick(); await tick();
    assert.equal(f.snapshot(sessionId).attempt, null);
    assert.equal(f.snapshot(sessionId).current, null);
    assert.equal(f.calls.filter(row => row[0] === 'prepare').length, 1);
    assert.equal(f.resources.size, 0);
  });
}

test('cache stale retry rejection preserves the pending receipt and current playback', async t => {
  const f = fixture(); t.after(() => f.service.close());
  const { sessionId } = await f.open({ platform: 'windows' });
  const current = await f.current(sessionId);
  await f.command(sessionId, 'switch');
  const pending = await f.pending(sessionId), before = f.snapshot(sessionId);
  await assert.rejects(f.command(sessionId, 'media.retry', { attemptId: 'outdated' }), { code: 'STALE_SESSION' });
  assert.deepEqual(f.snapshot(sessionId), before);
  assert.equal(f.service.attempt({ sessionId, attemptId: pending.id }).attemptId, pending.id);
  await f.command(sessionId, 'media.started', { attemptId: pending.id });
  assert.equal(f.snapshot(sessionId).current.attemptId, pending.id);
  assert.notEqual(pending.id, current.id);
  assert.equal(f.calls.some(row => row[0] === 'refreshAddress'), false);
});

test('cache metadata refresh stops its season follow-up after the page is stopped', async t => {
  let block = false;
  const waiting = deferred(), entered = deferred();
  const f = fixture({ data: { async targetMetadata() {
    if (block) { entered.resolve(); await waiting.promise; }
    return { ...structuredClone(metadata), name: block ? 'Late data' : 'Initial data' };
  } } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open({ target: { ...tv, season: 1, episode: 1 } });
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const before = f.snapshot(sessionId).metadata;
  const seasons = f.calls.filter(row => row[0] === 'season').length;
  block = true;
  await f.command(sessionId, 'browse.refresh');
  await entered.promise;
  await f.command(sessionId, 'stop');
  waiting.resolve(); await tick(); await tick();
  assert.deepEqual(f.snapshot(sessionId).metadata, before);
  assert.equal(f.calls.filter(row => row[0] === 'season').length, seasons);
  assert.equal(f.snapshot(sessionId).stage, 'stopped');
});

test('cache directory expansion failure keeps old buttons and does not interfere with ending the actual file', async t => {
  let failed = false;
  const f = fixture({ data: {
    async targetMetadata() { return null; },
    async detail() { return detail([{ id: 'actual', name: 'Raw file', playId: 'actual' }]); },
    async expand(source, line) {
      if (failed) throw error('NETWORK_ERROR');
      return line;
    },
  } });
  t.after(() => f.service.close());
  const { sessionId } = await f.open({ platform: 'windows', target: {
    id: 'raw:tail', kind: 'source', title: 'Raw', source: { key: 'a', detailId: 'raw' },
  } });
  const current = await f.current(sessionId), result = f.snapshot(sessionId).sources[0];
  await f.command(sessionId, 'browse', { sourceId: result.id });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  const buttons = f.snapshot(sessionId).browsing.episodes;
  failed = true;
  await f.command(sessionId, 'browse.refresh');
  await until(() => f.snapshot(sessionId).error === 'NETWORK_ERROR');
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes, buttons);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  await f.command(sessionId, 'media.ended', { attemptId: current.id });
  assert.equal(f.snapshot(sessionId).stage, 'ended');
  assert.equal(f.snapshot(sessionId).current, null);
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.deepEqual(f.snapshot(sessionId).browsing.episodes, buttons);
  assert.equal(f.calls.filter(row => row[0] === 'resolve').length, 1);
});
