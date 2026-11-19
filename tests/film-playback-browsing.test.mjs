import './storage-test-fixture.mjs';
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
    async history(targetId) { return structuredClone(document.history.find(row => row?.targetId === targetId)); },
    async saveHistory(value) {
      document.history = document.history.filter(row => row?.targetId !== value.targetId);
      document.history.push(structuredClone(value));
    },
    async list() { return {...structuredClone(document),historyAfter:null}; },
    async removeHistory(targetId) { document.history = targetId == null ? [] : document.history.filter(row => row?.targetId !== targetId); },
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
  return { open, state, command, calls, released, rawTarget, catalog, library, service };
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
    summary: 'Douban summary', seasonCount: 3, episodeCount: 30,
    metadata: f.catalog.metadata('douban', '/rexxar/api/v2/tv/55', metadata) });
  assert.equal(f.state(first.sessionId).browsing.season, 2);
});

const workMetadata = { id: 17, title: 'Stable film', original_title: 'Stable original', overview: 'TMDB summary',
  poster_path: '/stable-poster', vote_average: 8.5, genres: [{ id: 1, name: 'Drama' }], runtime: 100,
  credits: { cast: [{ id: 3, name: 'Stable actor' }], crew: [{ id: 4, name: 'Stable director', job: 'Director' }] },
  images: { posters: [{ file_path: '/stable-poster' }], backdrops: [] },
  similar: { results: [{ id: 18, title: 'Related film', media_type: 'movie' }] } };
const otherMetadata = { id: 55, title: 'Douban film', intro: 'Douban summary', genres: [],
  actors: [{ name: 'Douban actor' }], rating: { value: 9 }, pic: { normal: '/douban-poster' } };
const metadataRows = () => [
  { provider: 'tmdb', metadataId: 17, kind: 'movie', title: workMetadata.title, metadata: structuredClone(workMetadata) },
  { provider: 'douban', metadataId: 55, kind: 'movie', title: otherMetadata.title, metadata: structuredClone(otherMetadata),
    association: { targetProvider: 'tmdb', targetMetadataId: 17 } },
];

test('workInfo exposes detached full catalog metadata and stays independent of the directory provider', async t => {
  const f = fixture(t, { async metadataSources() { return metadataRows(); } });
  const first = await f.open({ results: [{ sourceKey: 'a', items: [{ id: 'detail-a', title: 'Film' }] }] });
  const before = await until(() => {
    const state = f.state(first.sessionId);
    return state.metadataSources.length === 2 && !state.browsing.loading && state;
  });
  assert.deepEqual(before.workInfo.metadata, f.catalog.metadata('tmdb', '/3/movie/17', workMetadata));
  assert.equal(before.workInfo.metadata.businessId, first.entryTarget.id);
  assert.equal(before.workInfo.metadata.similar.results[0].playbackTarget.id, 'tmdb:movie:18');
  const copy = f.state(first.sessionId);
  copy.workInfo.metadata.credits.cast[0].name = 'Changed outside the owner';
  assert.deepEqual(f.state(first.sessionId).workInfo, before.workInfo);
  await f.command(first.sessionId, 'browse', { sourceId: before.sources[0].id, lineId: 'B' });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const sourceDisplay = f.state(first.sessionId);
  await f.command(first.sessionId, 'browse.metadata', {
    metadataSourceId: before.metadataSources.find(row => row.provider === 'douban').id,
  });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const after = f.state(first.sessionId);
  assert.deepEqual(after.entryTarget, before.entryTarget);
  assert.deepEqual(after.workInfo, before.workInfo);
  assert.equal(after.metadata.id, 55);
  assert.equal(after.browsing.sourceId, sourceDisplay.browsing.sourceId);
  assert.equal(after.browsing.lineId, 'B');
  await f.command(first.sessionId, 'browse', { sourceId: null });
  await until(() => !f.state(first.sessionId).browsing.loading);
  assert.equal(f.state(first.sessionId).metadataSourceId, after.metadataSourceId);
});

for (const preference of ['tmdb', 'douban']) {
  for (const available of [['tmdb'], ['douban'], ['tmdb', 'douban']]) {
    test(`first metadata discovery uses ${preference} preference with ${available.join('/')} without writing a fallback`, async t => {
      const rows = metadataRows().filter(row => available.includes(row.provider));
      const entry = rows[0];
      const f = fixture(t, {
        async tmdbTargetEntry() { return null; },
        async targetMetadataEntry(target) {
          return { value: target.metadataProvider === 'douban' ? structuredClone(otherMetadata) : structuredClone(workMetadata),
            cache: { status: 'fresh', components: {} }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
        },
        async metadataSources() { return rows; },
      });
      await f.service.setSettings({ ...defaultPreferences(), metadataViewProvider: preference });
      const first = await f.open({ sources: [], target: {
        id: `${entry.provider}:movie:${entry.metadataId}`, kind: 'movie', title: entry.title,
        metadataProvider: entry.provider, metadataId: String(entry.metadataId),
      } });
      const selectedProvider = available.includes(preference) ? preference : available[0];
      const state = await until(() => {
        const value = f.state(first.sessionId);
        return value.metadataSources.length === rows.length && !value.browsing.loading &&
          value.metadataSources.find(row => row.id === value.metadataSourceId)?.provider === selectedProvider && value;
      });
      assert.equal(state.preferences.metadataViewProvider, preference);
      assert.equal(f.library.read().preferences.metadataViewProvider, preference);
      assert.equal(state.workInfo.provider, available.includes('tmdb') ? 'tmdb' : 'douban');
      assert.deepEqual(state.entryTarget, first.entryTarget);
      assert.equal(state.attempt, null);
      assert.deepEqual(f.released, []);
    });
  }
}

test('a missing preferred view does not erase the preference used on the next entry', async t => {
  let available = ['tmdb'];
  const f = fixture(t, { async metadataSources() { return metadataRows().filter(row => available.includes(row.provider)); } });
  await f.service.setSettings({ ...defaultPreferences(), metadataViewProvider: 'douban' });
  const first = await f.open();
  await until(() => !f.state(first.sessionId).browsing.loading);
  assert.equal(f.state(first.sessionId).metadataSources.find(row => row.id === f.state(first.sessionId).metadataSourceId).provider, 'tmdb');
  await f.service.release({ sessionId: first.sessionId });
  available = ['tmdb', 'douban'];
  const second = await f.open();
  await until(() => f.state(second.sessionId).metadataSources.length === 2 &&
    f.state(second.sessionId).metadataSourceId === f.state(second.sessionId).metadataSources.find(row => row.provider === 'douban').id);
  assert.equal(f.library.read().preferences.metadataViewProvider, 'douban');
  assert.equal(f.state(second.sessionId).workInfo.provider, 'tmdb');
});

test('first preferred-view discovery preserves a site chosen while association lookup was in flight', async t => {
  const waiting = deferred();
  t.after(() => waiting.resolve(metadataRows()));
  const f = fixture(t, { async metadataSources() { return waiting.promise; } });
  await f.service.setSettings({ ...defaultPreferences(), metadataViewProvider: 'douban' });
  const first = await f.open({ results: [{ sourceKey: 'a', items: [{ id: 'detail-a', title: 'Film' }] }] });
  await until(() => f.state(first.sessionId).stage === 'idle');
  await f.command(first.sessionId, 'browse', { sourceId: f.state(first.sessionId).sources[0].id, lineId: 'B' });
  await until(() => !f.state(first.sessionId).browsing.loading);
  const before = f.state(first.sessionId), reads = f.calls.length;
  waiting.resolve(metadataRows());
  await until(() => f.state(first.sessionId).metadataSources.length === 2 &&
    f.state(first.sessionId).metadataSourceId === f.state(first.sessionId).metadataSources.find(row => row.provider === 'douban').id &&
    !f.state(first.sessionId).browsing.loading);
  const after = f.state(first.sessionId);
  assert.equal(after.browsing.sourceId, before.browsing.sourceId);
  assert.equal(after.browsing.lineId, 'B');
  assert.deepEqual(after.target, before.target);
  assert.equal(f.calls.length, reads);
});

test('preferred metadata discovery does not truncate an in-flight site directory', async t => {
  const discovery = deferred(), expansion = deferred();
  t.after(() => { discovery.resolve(metadataRows()); expansion.resolve(line('B')); });
  const f = fixture(t, {
    async metadataSources() { return discovery.promise; },
    async expand(value, selected) {
      f.calls.push(['expand', value.key, selected.id]);
      return expansion.promise;
    },
  });
  await f.service.setSettings({ ...defaultPreferences(), metadataViewProvider: 'douban' });
  const first = await f.open({ results: [{ sourceKey: 'a', items: [{ id: 'detail-a', title: 'Film' }] }] });
  await until(() => f.state(first.sessionId).stage === 'idle');
  const site = f.state(first.sessionId).sources[0].id;
  await f.command(first.sessionId, 'browse', { sourceId: site, lineId: 'B' });
  await until(() => f.calls.some(row => row[0] === 'expand'));
  discovery.resolve(metadataRows());
  await until(() => f.state(first.sessionId).metadataSources.length === 2 &&
    f.state(first.sessionId).metadataSources.find(row => row.id === f.state(first.sessionId).metadataSourceId)?.provider === 'douban');
  assert.equal(f.state(first.sessionId).browsing.loading, true);
  assert.deepEqual(f.state(first.sessionId).browsing.episodes, []);
  expansion.resolve(line('B'));
  await until(() => !f.state(first.sessionId).browsing.loading);
  const after = f.state(first.sessionId);
  assert.equal(after.browsing.sourceId, site);
  assert.equal(after.browsing.lineId, 'B');
  assert.equal(after.browsing.episodes.length, 1);
  assert.deepEqual(after.target, first.target);
  assert.equal(after.attempt, null);
});

test('an explicit view choice wins over a late first discovery without rewriting the stored provider', async t => {
  const waiting = deferred();
  t.after(() => waiting.resolve(metadataRows()));
  const f = fixture(t, { async metadataSources() { return waiting.promise; } });
  await f.service.setSettings({ ...defaultPreferences(), metadataViewProvider: 'douban' });
  const first = await f.open();
  await until(() => !f.state(first.sessionId).browsing.loading);
  const selected = f.state(first.sessionId).metadataSourceId;
  await f.command(first.sessionId, 'browse.metadata', { metadataSourceId: selected, keepSource: true });
  await until(() => !f.state(first.sessionId).browsing.loading);
  waiting.resolve(metadataRows());
  await until(() => f.state(first.sessionId).metadataSources.length === 2);
  assert.equal(f.state(first.sessionId).metadataSourceId, selected);
  assert.equal(f.library.read().preferences.metadataViewProvider, 'douban');
});

test('metadata discovery stays anchored to the entry after selecting a different provider for formal playback', async t => {
  const f = fixture(t, {
    async metadataSources(target) {
      f.calls.push(['discovery', target.metadataProvider, target.metadataId]);
      return metadataRows().map(row => ({ ...row, title: 'Film', metadata: { ...row.metadata, title: 'Film' } }));
    },
    async targetMetadataEntry(target) {
      return { value: { ...structuredClone(target.metadataProvider === 'douban' ? otherMetadata : workMetadata), title: 'Film' },
        cache: { status: 'fresh', components: {} }, fetchedAt: 0, expiresAt: Number.MAX_SAFE_INTEGER };
    },
  });
  const first = await f.open();
  await until(() => f.state(first.sessionId).metadataSources.length === 2 && !f.state(first.sessionId).browsing.loading);
  const before = f.state(first.sessionId);
  const selected = before.metadataSources.find(row => row.provider === 'douban');
  await f.command(first.sessionId, 'select.target', { target: selected.playbackTarget, metadataSourceId: selected.id });
  await until(() => f.state(first.sessionId).attempt);
  assert.equal(f.state(first.sessionId).target.metadataProvider, 'douban');
  await f.command(first.sessionId, 'browse.refresh');
  await until(() => f.calls.filter(row => row[0] === 'discovery').length === 2);
  assert.deepEqual(f.calls.filter(row => row[0] === 'discovery').map(row => row.slice(1)), [['tmdb', '17'], ['tmdb', '17']]);
  assert.deepEqual(f.state(first.sessionId).workInfo, before.workInfo);
  assert.deepEqual(f.state(first.sessionId).entryTarget, before.entryTarget);
});
