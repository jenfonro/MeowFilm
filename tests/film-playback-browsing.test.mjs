import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { createPlaybackService } from '../packages/film-playback/src/service.js';
import { createFilmCatalog } from '../packages/film-playback/src/catalog.js';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';

const area = new URL('../tmp/test-results/film-playback-page-corrections/browsing/', import.meta.url);
mkdirSync(area, { recursive: true });
const ruleRoot = mkdtempSync(new URL('rules-', area));
const rules = createFilmRulesService({ dataRoot: ruleRoot });
await rules.snapshot();
after(async () => { await rules.close(); rmSync(ruleRoot, { recursive: true, force: true }); });

const source = key => ({ key, engine: 'cat', installationId: `installation-${key}`, siteId: key,
  generation: 1, name: key });
const line = id => ({ id, label: id, episodes: [{ id: `file-${id}`, name: `Film.${id}.1080p`, index: 0 }] });
const failure = code => Object.assign(new Error(code), { code });
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
async function until(predicate) {
  for (let i = 0; i < 1500; i++) {
    const value = predicate();
    if (value) return value;
    await delay(2);
  }
  assert.fail('The selected operation did not reach its expected state');
}
function fixture(t, overrides = {}) {
  let document = { version: 2, preferences: defaultPreferences(), history: [], manual: [], blocks: [],
    previewTargets: [], groups: [] };
  const calls = [], released = [];
  const library = {
    read: () => structuredClone(document),
    update: mutator => { const draft = structuredClone(document); mutator(draft); document = draft; },
  };
  const catalog = createFilmCatalog({ library });
  const data = {
    async targetMetadataEntry(target) {
      calls.push(['metadata', target.metadataProvider, target.metadataId]);
      return target.metadataProvider ? { value: { id: Number(target.metadataId), title: 'Film', overview: 'TMDB summary' },
        cache: { status: 'fresh', components: {} }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER } : null;
    },
    async metadataSources(target) {
      return [{ id: 'unused', provider: target.metadataProvider, metadataId: target.metadataId,
        kind: target.kind, title: 'Film', metadata: { id: Number(target.metadataId), title: 'Film', overview: 'TMDB summary' } }];
    },
    async detailEntry(value, id) {
      calls.push(['detail', value.key, id]);
      return { value: { lines: [line('A'), line('B')] }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
    async expand(value, selected) { calls.push(['expand', value.key, selected.id]); return selected; },
    async resolve(candidate) {
      calls.push(['resolve', candidate.source.key, candidate.lineId]);
      return { url: 'https://synthetic.invalid/video.mp4', scope: { sourceKey: candidate.source.key } };
    },
    async search(value, query, page) {
      calls.push(['search', value.key, page]);
      return { page, hasMore: false, items: [{ id: `detail-${value.key}`, title: query }] };
    },
    ...overrides,
  };
  let resource = 0;
  const media = {
    async prepare() { return { id: `resource-${++resource}`, url: 'https://synthetic.invalid/controlled' }; },
    async release(id) { released.push(id); },
    async cancelPrefetch() {},
  };
  const service = createPlaybackService({ data, library, media, rules, catalog });
  t.after(() => service.close());
  const open = input => service.open({ platform: 'test',
    target: catalog.metadata('tmdb', '/3/movie/17', { id: 17, title: 'Film' }).playbackTarget,
    sources: [source('a'), source('b')], ...input });
  const state = id => service.snapshot({ sessionId: id });
  const command = (id, action, fields = {}) => service.command({ sessionId: id, action, ...fields });
  const rawTarget = catalog.sourceRows(source('a'), { list: [{ id: 'detail-a', title: 'Film' }] }).list[0].playbackTarget;
  return { open, state, command, calls, released, rawTarget };
}

test('initial directory is loading and the detail menu is published before the selected line completes', async t => {
  const blocked = deferred();
  const f = fixture(t, {
    async expand(value, selected) {
      f.calls.push(['expand', value.key, selected.id]);
      return selected.id === 'A' ? blocked.promise : selected;
    },
  });
  try {
    const first = await f.open({ target: f.rawTarget, sources: [source('a')] });
    assert.equal(first.browsing.loading, true);
    assert.deepEqual(first.browsing.episodes, []);
    await until(() => f.calls.some(row => row[0] === 'expand'));
    const loading = f.state(first.sessionId);
    assert.deepEqual(loading.browsing.lines.map(row => row.id), ['A', 'B']);
    assert.equal(loading.browsing.lineId, 'A');
    assert.equal(loading.browsing.loading, true);
    assert.deepEqual(loading.browsing.episodes, []);
    await f.command(first.sessionId, 'browse', { sourceId: loading.browsing.sourceId, lineId: 'B' });
    await until(() => !f.state(first.sessionId).browsing.loading);
    assert.equal(f.state(first.sessionId).browsing.lineId, 'B');
    blocked.resolve(line('A'));
    await delay(5);
    assert.equal(f.state(first.sessionId).browsing.lineId, 'B');
    assert.equal(f.state(first.sessionId).browsing.episodes[0].playbackTarget.source.lineId, 'B');
  } finally { blocked.resolve(line('A')); }
});

test('completed line and site displays reuse their directory reads without changing playback', async t => {
  const f = fixture(t);
  const first = await f.open({ results: [
    { sourceKey: 'a', items: [{ id: 'detail-a', title: 'Film' }] },
    { sourceKey: 'b', items: [{ id: 'detail-b', title: 'Film' }] },
  ] });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const ids = Object.fromEntries(f.state(first.sessionId).sources.map(row => [row.source.key, row.id]));
  for (const [key, id] of [['a', 'A'], ['a', 'B'], ['a', 'A'], ['b', 'A'], ['a', 'A']]) {
    await f.command(first.sessionId, 'browse', { sourceId: ids[key], lineId: id });
    await until(() => !f.state(first.sessionId).browsing.loading);
    assert.equal(f.state(first.sessionId).browsing.lineId, id);
  }
  assert.deepEqual(f.calls.filter(row => row[0] === 'detail').map(row => row[1]), ['a', 'b']);
  assert.deepEqual(f.calls.filter(row => row[0] === 'expand').map(row => row.slice(1)),
    [['a', 'A'], ['a', 'B'], ['b', 'A']]);
  assert.equal(f.state(first.sessionId).target.id, first.target.id);
  assert.equal(f.state(first.sessionId).attempt, null);
  assert.equal(f.state(first.sessionId).error, null);
});

test('a cached file target plays its own line without a browsing synchronization command', async t => {
  const f = fixture(t), first = await f.open({ target: f.rawTarget, sources: [source('a')] });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const saved = f.state(first.sessionId).browsing.episodes[0].playbackTarget;
  await f.command(first.sessionId, 'browse', { sourceId: f.state(first.sessionId).browsing.sourceId, lineId: 'B' });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const reads = f.calls.filter(row => row[0] === 'detail' || row[0] === 'expand').length;
  await f.command(first.sessionId, 'select.target', { target: saved, metadataSourceId: null });
  const attempt = await until(() => f.state(first.sessionId).attempt);
  assert.equal(attempt.candidate.lineId, 'A');
  assert.equal(f.state(first.sessionId).browsing.lineId, 'B');
  assert.equal(f.calls.filter(row => row[0] === 'detail' || row[0] === 'expand').length, reads);
  const forged = structuredClone(saved);
  forged.source.lineId = 'B';
  await assert.rejects(f.command(first.sessionId, 'select.target', { target: forged, metadataSourceId: null }),
    { code: 'NO_MATCH' });
  assert.equal(f.state(first.sessionId).attempt.id, attempt.id);
});

test('source errors and metadata browsing do not revoke a pending attempt or replace the main summary', async t => {
  const f = fixture(t, {
    async metadataSources() {
      return [
        { provider: 'tmdb', metadataId: 17, kind: 'movie', title: 'Film',
          metadata: { id: 17, title: 'Film', overview: 'TMDB summary' } },
        { provider: 'douban', metadataId: 55, kind: 'movie', title: 'Film',
          metadata: { id: 55, title: 'Film', intro: 'Douban summary' },
          association: { targetProvider: 'tmdb', targetMetadataId: 17 } },
      ];
    },
    async detailEntry(value, id) {
      f.calls.push(['detail', value.key, id]);
      if (value.key === 'b') throw failure('UPSTREAM_HTTP');
      return { value: { lines: [line('A')] }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
  });
  const first = await f.open({ results: [
    { sourceKey: 'a', items: [{ id: 'detail-a', title: 'Film' }] },
    { sourceKey: 'b', items: [{ id: 'detail-b', title: 'Film' }] },
  ] });
  await until(() => !f.state(first.sessionId).browsing.loading);
  await f.command(first.sessionId, 'play');
  const attempt = await until(() => f.state(first.sessionId).attempt);
  const before = f.state(first.sessionId);
  await f.command(first.sessionId, 'browse', { sourceId: before.sources.find(row => row.source.key === 'b').id });
  await until(() => f.state(first.sessionId).browsing.error);
  assert.equal(f.state(first.sessionId).browsing.error, 'UPSTREAM_HTTP');
  assert.deepEqual(f.state(first.sessionId).browsing.episodes, []);
  const douban = f.state(first.sessionId).metadataSources.find(row => row.provider === 'douban');
  await f.command(first.sessionId, 'browse.metadata', { metadataSourceId: douban.id });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const after = f.state(first.sessionId);
  assert.equal(after.metadataSourceId, douban.id);
  assert.equal(after.workInfo.summary, 'TMDB summary');
  assert.deepEqual(after.target, before.target);
  assert.equal(after.stage, 'pending');
  assert.equal(after.error, null);
  assert.equal(after.attempt.id, attempt.id);
  assert.deepEqual(f.released, []);
});

test('TMDB has a stable main projection and only a reliably associated Douban summary fills its blank', async t => {
  const f = fixture(t, {
    async targetMetadataEntry() {
      return { value: { id: 17, title: 'Film', overview: '' }, cache: { status: 'fresh', components: {} },
        fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
    async metadataSources() {
      return [
        { provider: 'tmdb', metadataId: 17, kind: 'movie', title: 'Film', metadata: { id: 17, overview: '' } },
        { provider: 'douban', metadataId: 55, kind: 'movie', title: 'Film', metadata: { id: 55, intro: 'Reliable summary' },
          association: { targetProvider: 'tmdb', targetMetadataId: 17 } },
      ];
    },
  });
  const first = await f.open({ sources: [] });
  await until(() => f.state(first.sessionId).workInfo?.summary === 'Reliable summary');
  assert.equal(f.state(first.sessionId).workInfo.provider, 'tmdb');
  assert.equal(f.state(first.sessionId).stage, 'unavailable');
  assert.equal(f.state(first.sessionId).error, 'MEDIA_UNAVAILABLE');
  assert.equal(f.state(first.sessionId).browsing.loading, false);
});

test('a completed empty source has no fake loading or playable file target', async t => {
  const f = fixture(t, { async detailEntry() { return { value: { vod_play_from: '', vod_play_url: '' } }; } });
  const first = await f.open({ target: f.rawTarget, sources: [source('a')] });
  const done = await until(() => !f.state(first.sessionId).browsing.loading && f.state(first.sessionId).stage === 'unavailable'
    ? f.state(first.sessionId) : null);
  assert.equal(done.error, 'MEDIA_UNAVAILABLE');
  assert.equal(done.browsing.error, null);
  assert.deepEqual(done.browsing.lines, []);
  assert.deepEqual(done.browsing.episodes, []);
  assert.equal(done.attempt, null);
});

test('Douban series totals use the confirmed distinct seasons and never the selected directory size', async t => {
  const seasons = [12, 10, 8].map((episodeCount, index) => ({ season: index + 1, episodeCount,
    metadataId: 55 + index, metadata: { id: 55 + index, title: 'Film' } }));
  const metadata = { id: 55, title: 'Film', intro: 'Douban summary', doubanSeasons: seasons };
  const f = fixture(t, {
    async tmdbTargetEntry() { return null; },
    async targetMetadataEntry() {
      return { value: metadata, cache: { status: 'fresh', components: {} }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
    async metadataSources() {
      return [{ provider: 'douban', metadataId: 55, kind: 'tv', title: 'Film', metadata, seasons }];
    },
    async seasonMetadataEntry(_target, season) {
      return { value: seasons.find(row => row.season === season), cache: { status: 'fresh', components: {} },
        fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
  });
  const first = await f.open({ target: { id: 'douban:tv:55', title: 'Film', kind: 'tv', metadataProvider: 'douban', metadataId: '55' },
    metadata, sources: [] });
  await until(() => f.state(first.sessionId).workInfo?.episodeCount === 30);
  await f.command(first.sessionId, 'browse.season', { season: 2 });
  await until(() => !f.state(first.sessionId).browsing.loading);
  assert.deepEqual(f.state(first.sessionId).workInfo, { provider: 'douban', metadataId: '55', kind: 'tv', title: 'Film',
    summary: 'Douban summary', seasonCount: 3, episodeCount: 30 });
  assert.equal(f.state(first.sessionId).browsing.season, 2);
});
