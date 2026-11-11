import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { createPlaybackCache } from '../packages/film-playback/src/cache.js';
import { createPlaybackData } from '../packages/film-playback/src/data.js';
import { createMetadataSettings } from '../apps/runtime/modules/metadata/settings.mjs';
import { PrivateDocuments } from '@meowfilm/script-host';

const HOUR = 3_600_000, DAY = 24 * HOUR, STABLE = 14 * DAY;
const initial = Date.parse('2026-11-08T04:00:00Z');
const movie = (patch = {}) => ({ id: 42, title: '缓存电影', overview: '旧简介',
  poster_path: '/old.jpg', runtime: 120, genres: [], credits: { cast: [] },
  similar: { results: [] }, images: { posters: [] }, ...patch });
const tv = (patch = {}) => ({ id: 43, name: '缓存剧集', genres: [], status: 'Ended',
  seasons: [{ season_number: 1, episode_count: 1 }], credits: { cast: [] },
  similar: { results: [] }, images: { posters: [] }, ...patch });
const target = { id: 'tmdb:tv:43', metadataProvider: 'tmdb', metadataId: 43,
  kind: 'tv', title: '缓存剧集' };
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const tick = () => new Promise(done => setImmediate(done));

function fixture(t, { dataRoot = mkdtempSync(join(tmpdir(), 'film-metadata-')),
  maximumBytes, at = initial, persist = true } = {}) {
  const observed = [];
  const settings = Object.fromEntries(['tmdb', 'douban'].map(id => [id, createMetadataSettings(dataRoot, id)]));
  const state = {
    movie: movie(), tv: tv(),
    season: { season_number: 1, episodes: [{ episode_number: 1, air_date: '2024-01-01', overview: '旧集简介' }] },
    douban: { id: '51', title: '豆瓣资料', intro: '旧豆瓣简介', genres: [], episodes_count: 2 },
  };
  let response = ({ provider, path }) => provider === 'douban' ? state.douban
    : path.includes('/season/') ? { ...state.season, season_number: Number(path.split('/').at(-1)) }
      : path.startsWith('/3/tv/') ? state.tv : path.startsWith('/3/search/')
        ? { results: [], total_pages: 1 } : state.movie;
  const modules = new Map(['tmdb', 'douban'].map(provider => [provider, { methods: {
    [provider + '.data.settings.get']: () => ({ ok: true, value: settings[provider].get() }),
    [provider + '.data.request']: async (params, { signal }) => {
      observed.push({ provider, ...params, signal });
      return { ok: true, value: await response({ provider, ...params }, signal) };
    },
  } }]));
  const cache = createPlaybackCache({ now: () => at, ...(maximumBytes ? { maximumBytes } : {}) });
  const data = createPlaybackData({ ...(persist ? { dataRoot } : {}), modules, cache, now: () => at });
  t.after(async () => { await data.close(); await cache.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  return { data, dataRoot, cache, state, settings, observed, modules,
    setResponse: value => { response = value; }, now: () => at,
    setTime: value => { at = value; }, advance: value => { at += value; },
    calls: path => observed.filter(row => row.path === path),
    files: provider => {
      try { return readdirSync(join(dataRoot, 'film-metadata', provider)).filter(name => name.endsWith('.json')); }
      catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    },
  };
}
const readMovie = (f, options) => f.data.metadataEntry('tmdb', '/3/movie/42', { language: 'zh-CN' }, options);
const readSeason = (f, options) => f.data.seasonMetadataEntry(target, 1, options);

test('F1 metadata persists across owners and partitions provider, resource, query, language and image size', async t => {
  const f = fixture(t);
  const first = await readMovie(f);
  const douban = await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  await f.data.metadataEntry('tmdb', '/3/movie/42', { language: 'en-US' });
  await f.data.metadataEntry('tmdb', '/3/movie/42', { language: 'zh-CN' }, { imageSize: 'w500' });
  await f.data.metadataEntry('tmdb', '/3/search/movie', { query: '缓存', page: 1 });
  await f.data.metadataEntry('tmdb', '/3/search/movie', { query: '缓存', page: 2 });
  await f.data.close();
  const next = fixture(t, { dataRoot: f.dataRoot });
  next.setResponse(() => assert.fail('restart requested fresh metadata'));
  assert.deepEqual(await readMovie(next), first);
  assert.deepEqual(await next.data.metadataEntry('douban', '/rexxar/api/v2/movie/51'), douban);
  await next.data.metadataEntry('tmdb', '/3/movie/42', { language: 'en-US' });
  await next.data.metadataEntry('tmdb', '/3/movie/42', { language: 'zh-CN' }, { imageSize: 'w500' });
  await next.data.metadataEntry('tmdb', '/3/search/movie', { query: '缓存', page: 1 });
  await next.data.metadataEntry('tmdb', '/3/search/movie', { query: '缓存', page: 2 });
  assert.equal(next.observed.length, 0);
  assert.equal(f.files('tmdb').length, 5);
  assert.equal(f.files('douban').length, 1);
});

test('F1 metadata identity survives a real process restart and durable documents exclude request credentials', async t => {
  const f = fixture(t);
  f.settings.tmdb.update({ accessToken: 'synthetic-settings-token' });
  const query = { language: 'zh-CN', api_key: 'synthetic-query-token' };
  const entry = await f.data.metadataEntry('tmdb', '/3/movie/42', query);
  const bytes = readFileSync(join(f.dataRoot, 'film-metadata/tmdb', f.files('tmdb')[0]), 'utf8');
  assert.equal(bytes.includes('synthetic-settings-token'), false);
  assert.equal(bytes.includes('synthetic-query-token'), false);
  await f.data.close();
  const script = `
    import {createPlaybackCache} from ${JSON.stringify(new URL('../packages/film-playback/src/cache.js', import.meta.url).href)};
    import {createPlaybackData} from ${JSON.stringify(new URL('../packages/film-playback/src/data.js', import.meta.url).href)};
    import {createMetadataSettings} from ${JSON.stringify(new URL('../apps/runtime/modules/metadata/settings.mjs', import.meta.url).href)};
    const dataRoot = process.argv[1];
    const settings = createMetadataSettings(dataRoot, 'tmdb');
    const modules = new Map([['tmdb', {methods: {
      'tmdb.data.settings.get': () => ({ok:true, value: settings.get()}),
      'tmdb.data.request': () => { throw new Error('unexpected upstream after restart'); }
    }}]]);
    const cache = createPlaybackCache({now: () => ${initial}});
    const data = createPlaybackData({dataRoot, modules, cache, now: () => ${initial}});
    console.log(JSON.stringify(await data.metadataEntry('tmdb', '/3/movie/42', ${JSON.stringify(query)})));
    await data.close(); await cache.close();
  `;
  const run = spawnSync(process.execPath, ['--input-type=module', '-e', script, f.dataRoot],
    { encoding: 'utf8', timeout: 30_000 });
  assert.equal(run.status, 0, run.stderr);
  assert.deepEqual(JSON.parse(run.stdout), entry);
});

test('F2 requested empty components are complete; missing/failed appends supplement without renewing the base', async t => {
  const f = fixture(t);
  const query = { language: 'zh-CN', append_to_response: 'credits,images' };
  const raw = movie();
  delete raw.images;
  f.state.movie = raw;
  const partial = await f.data.metadataEntry('tmdb', '/3/movie/42', query);
  assert.equal(partial.completeness, 'partial');
  assert.equal(partial.cache.status, 'stale');
  assert.equal(partial.cache.components.credits.status, 'empty');
  assert.equal(partial.cache.components.images.status, 'missing');
  assert.equal(partial.cache.components.images.fetchedAt, undefined);
  const baseTime = partial.fetchedAt;
  f.advance(HOUR);
  f.state.movie = movie({ overview: 'append请求不应改基础资料', images: { posters: [] } });
  const full = await f.data.metadataEntry('tmdb', '/3/movie/42', query);
  assert.equal(f.observed.at(-1).query.append_to_response, 'images');
  assert.equal(full.cache.status, 'fresh');
  assert.equal(full.completeness, 'complete');
  assert.equal(full.value.overview, '旧简介');
  assert.equal(full.fetchedAt, baseTime);
  assert.equal(full.cache.components.credits.fetchedAt, baseTime);
  assert.equal(full.cache.components.images.status, 'empty');
  await f.data.metadataEntry('tmdb', '/3/movie/42', query);
  assert.equal(f.observed.length, 2);
  f.advance(HOUR);
  f.state.movie = movie({ images: { success: false, status_message: 'private upstream text' } });
  const failed = await f.data.metadataEntry('tmdb', '/3/movie/42', query, { refresh: true });
  assert.equal(failed.cache.status, 'stale');
  assert.deepEqual(failed.cache.error, { code: 'UPSTREAM_HTTP' });
  assert.deepEqual(failed.value.images, { posters: [] });
  assert.equal(failed.cache.components.images.fetchedAt, full.cache.components.images.fetchedAt);
  assert.equal(failed.cache.components.images.status, 'failed');
  assert.equal(await f.data.cachedEntry(failed, { minimumCompleteness: 'partial' }), undefined);
  assert.equal(await f.data.cachedEntry(failed, { minimumCompleteness: 'complete' }), undefined);
  f.state.movie = movie({ images: { posters: [{ file_path: '/new.jpg' }] } });
  const recovered = await f.data.metadataEntry('tmdb', '/3/movie/42', query);
  assert.equal(f.observed.at(-1).query.append_to_response, 'images');
  assert.equal(recovered.cache.status, 'fresh');
  assert.equal(recovered.cache.error, undefined);
  assert.deepEqual(recovered.value.images, f.state.movie.images);
});

test('F2 a partial handoff or search cannot replace full detail or extend its success time', async t => {
  const f = fixture(t), path = '/3/movie/42', query = { language: 'zh-CN' };
  const partial = { kind: 'metadata', provider: 'tmdb', path, query,
    settings: { revision: f.settings.tmdb.get().revision }, completeness: 'partial',
    fetchedAt: f.now(), value: { id: 42, title: '摘要' } };
  await f.data.seed(partial);
  const full = await readMovie(f);
  assert.equal(full.value.runtime, 120);
  assert.equal(full.completeness, 'complete');
  f.advance(HOUR);
  assert.deepEqual(await f.data.seed({ ...partial, fetchedAt: f.now() }), full.value);
  const retained = await readMovie(f);
  assert.deepEqual(retained.value, full.value);
  assert.equal(retained.fetchedAt, full.fetchedAt);
  assert.equal(retained.expiresAt, full.expiresAt);
  assert.equal(retained.cache.components.summary.fetchedAt, f.now());
  await f.data.metadataEntry('tmdb', '/3/search/movie', { query: '缓存' });
  f.state.movie = { id: 42, title: '新摘要' };
  const repeated = await readMovie(f, { completeness: 'partial', refresh: true });
  assert.equal(repeated.fetchedAt, full.fetchedAt);
  assert.deepEqual(repeated.value, full.value);
  assert.equal((await f.data.cachedEntry(partial)).expiresAt, full.expiresAt);
});

test('F2/F5 newer public seed/handoff summaries keep full and append evidence while old or duplicate summaries are ignored', async t => {
  const f = fixture(t), path = '/3/movie/42', query = { language: 'zh-CN', append_to_response: 'credits,images' };
  const read = options => f.data.metadataEntry('tmdb', path, query, options);
  const partial = { kind: 'metadata', provider: 'tmdb', path, query,
    settings: { revision: f.settings.tmdb.get().revision }, completeness: 'partial',
    fetchedAt: f.now() - HOUR, value: { id: 42, title: '先前摘要' } };
  await f.data.seed(partial);
  const full = await read();
  const current = { ...partial, fetchedAt: f.now(), expiresAt: f.now() + HOUR,
    value: { id: 42, title: '本次摘要', overview: '摘要不覆盖原始详情' } };
  // Summary and detail may have equal times but still own different expirations.
  assert.deepEqual(await f.data.seed(current), full.value);
  for (const minimumCompleteness of ['partial', 'complete']) {
    const exported = await f.data.cachedEntry(partial, { minimumCompleteness });
    assert.deepEqual(exported.value, full.value);
    assert.equal(exported.completeness, 'complete');
    assert.equal(exported.fetchedAt, full.fetchedAt);
    assert.equal(exported.expiresAt, full.expiresAt);
    for (const name of ['detail', 'credits', 'images']) {
      assert.deepEqual(exported.cache.components[name], full.cache.components[name]);
    }
  }
  f.advance(1);
  assert.deepEqual(await read({ completeness: 'partial' }), await f.data.cachedEntry(partial));
  f.setTime(full.expiresAt - HOUR);
  const newer = { ...partial, fetchedAt: f.now(), value: { id: 42, title: '较新的交接摘要' } };
  assert.deepEqual(await f.data.handoff([newer]), { accepted: 1, ignored: 0 });
  const beforeExpiry = await read();
  assert.deepEqual(beforeExpiry.value, full.value);
  assert.equal(beforeExpiry.fetchedAt, full.fetchedAt);
  assert.equal(beforeExpiry.expiresAt, full.expiresAt);
  for (const name of ['detail', 'credits', 'images']) {
    assert.deepEqual(beforeExpiry.cache.components[name], full.cache.components[name]);
  }
  f.setTime(full.expiresAt);
  const summary = await read({ completeness: 'partial' });
  assert.deepEqual(summary.value, newer.value);
  assert.equal(summary.completeness, 'partial');
  assert.equal(summary.cache.status, 'fresh');
  assert.equal(summary.fetchedAt, newer.fetchedAt);
  assert.equal(summary.expiresAt, newer.fetchedAt + 6 * HOUR);
  assert.deepEqual(await f.data.cachedEntry(partial, { minimumCompleteness: 'partial' }), summary);
  assert.equal(await f.data.cachedEntry(partial, { minimumCompleteness: 'complete' }), undefined);
  const files = f.files('tmdb');
  const bytes = files.map(name => readFileSync(join(f.dataRoot, 'film-metadata/tmdb', name)));
  assert.equal(await f.data.seed({ ...newer, value: { id: 42, title: '同时间不能覆盖' },
    expiresAt: f.now() + DAY }), undefined);
  assert.deepEqual(await f.data.handoff([{ ...newer, fetchedAt: newer.fetchedAt - 1,
    value: { id: 42, title: '较旧不能覆盖' } }]), { accepted: 0, ignored: 1 });
  assert.deepEqual(await read({ completeness: 'partial' }), summary);
  assert.deepEqual(files.map(name => readFileSync(join(f.dataRoot, 'film-metadata/tmdb', name))), bytes);
  assert.equal(f.observed.length, 1, 'seed, handoff, and fresh summary reads make no upstream requests');
  await f.data.close();
  const restarted = fixture(t, { dataRoot: f.dataRoot, at: f.now() });
  assert.deepEqual(await restarted.data.metadataEntry('tmdb', path, query, { completeness: 'partial' }), summary);
  assert.deepEqual(await restarted.data.cachedEntry(partial, { minimumCompleteness: 'partial' }), summary);
  assert.equal(restarted.observed.length, 0);
});

test('F2 expired full detail and a refreshed summary keep independent values and success times', async t => {
  for (const persist of [false, true]) await t.test(persist ? 'persistent restart' : 'memory only', async t => {
    const f = fixture(t, { persist });
    const full = await readMovie(f);
    f.advance(STABLE);
    f.state.movie = { id: 42, title: '新的摘要', overview: '新的摘要简介' };
    const partial = await readMovie(f, { completeness: 'partial', refresh: true });
    assert.deepEqual(partial.value, f.state.movie);
    assert.equal(partial.completeness, 'partial');
    assert.equal(partial.cache.status, 'fresh');
    assert.equal(partial.fetchedAt, f.now());
    assert.equal(partial.expiresAt, f.now() + 6 * HOUR);
    assert.equal(partial.cache.components.summary.fetchedAt, f.now());
    assert.equal(partial.cache.components.detail.fetchedAt, full.fetchedAt);
    assert.deepEqual(await f.data.cachedEntry(partial, { minimumCompleteness: 'partial' }), partial);
    assert.equal(await f.data.cachedEntry(partial, { minimumCompleteness: 'complete' }), undefined);
    assert.deepEqual(await readMovie(f, { completeness: 'partial' }), partial);
    assert.equal(f.observed.length, 2);
    let active = f;
    if (persist) {
      await f.data.close();
      active = fixture(t, { dataRoot: f.dataRoot, at: f.now() });
      assert.deepEqual(await readMovie(active, { completeness: 'partial' }), partial);
      assert.deepEqual(await active.data.cachedEntry(partial, { minimumCompleteness: 'partial' }), partial);
      assert.equal(await active.data.cachedEntry(partial, { minimumCompleteness: 'complete' }), undefined);
      assert.equal(active.observed.length, 0);
    } else assert.deepEqual(f.files('tmdb'), []);
    active.setResponse(() => { throw Object.assign(new Error('synthetic'), { code: 'TIMEOUT' }); });
    const stale = await readMovie(active);
    assert.deepEqual(stale.value, full.value);
    assert.equal(stale.fetchedAt, full.fetchedAt);
    assert.equal(stale.expiresAt, full.expiresAt);
    assert.equal(stale.cache.status, 'stale');
    assert.deepEqual(stale.cache.error, { code: 'TIMEOUT' });
    const calls = active.observed.length;
    const retainedSummary = await readMovie(active, { completeness: 'partial' });
    assert.deepEqual(retainedSummary.value, partial.value);
    assert.equal(retainedSummary.cache.status, 'fresh');
    assert.equal(retainedSummary.completeness, 'partial');
    assert.equal(retainedSummary.fetchedAt, partial.fetchedAt);
    assert.equal(retainedSummary.expiresAt, partial.expiresAt);
    assert.equal(active.observed.length, calls);
    assert.deepEqual(await active.data.cachedEntry(partial, { minimumCompleteness: 'partial' }), retainedSummary);
    assert.equal(await active.data.cachedEntry(partial, { minimumCompleteness: 'complete' }), undefined);
    active.advance(1);
    active.setResponse(() => movie({ overview: '恢复的完整简介', runtime: 130 }));
    const recovered = await readMovie(active);
    assert.equal(recovered.cache.status, 'fresh');
    assert.equal(recovered.completeness, 'complete');
    assert.equal(recovered.value.runtime, 130);
    assert.equal(recovered.fetchedAt, active.now());
  });
});

test('F2 complete responses clear old fields, seasons and real episodes, including successful empty lists', async t => {
  const f = fixture(t);
  f.state.movie.obsolete = 'remove';
  await readMovie(f);
  f.state.movie = movie({ overview: '', poster_path: null, runtime: 0 });
  const cleared = await readMovie(f, { refresh: true });
  assert.equal(cleared.value.overview, '');
  assert.equal(cleared.value.poster_path, null);
  assert.equal(cleared.value.runtime, 0);
  assert.equal(Object.hasOwn(cleared.value, 'obsolete'), false);
  await f.data.targetMetadataEntry(target);
  await readSeason(f);
  f.state.tv = tv({ seasons: [] });
  const parent = await f.data.targetMetadataEntry(target, { refresh: true });
  assert.deepEqual(parent.value.seasons, []);
  assert.equal(parent.cache.components.seasons.status, 'empty');
  f.state.season = { season_number: 1, episodes: [] };
  const season = await readSeason(f, { refresh: true });
  assert.deepEqual(season.value.episodes, []);
  assert.equal(season.cache.components.episodes.status, 'empty');
  await readSeason(f, { requestedEpisode: 999 });
  assert.equal(f.calls('/3/tv/43/season/1').length, 2);
});

test('F2 quarterly summaries do not satisfy episodes; requested successful empty episodes do not loop', async t => {
  const f = fixture(t);
  f.state.season = { season_number: 1, name: '第一季' };
  const summary = await readSeason(f);
  assert.equal(summary.completeness, 'partial');
  assert.equal(summary.cache.components.episodes.status, 'missing');
  assert.equal(summary.cache.components.episodes.fetchedAt, undefined);
  f.state.season = { ...f.state.season, episodes: [] };
  const full = await readSeason(f);
  assert.equal(full.completeness, 'complete');
  assert.equal(full.cache.status, 'fresh');
  assert.equal(full.cache.components.episodes.status, 'empty');
  await readSeason(f);
  assert.equal(f.calls('/3/tv/43/season/1').length, 2);
});

test('F3 parent count/schedule changes refresh only requested related seasons, not title-only edits or all seasons', async t => {
  const f = fixture(t);
  f.state.tv.seasons.push({ season_number: 2, episode_count: 1 });
  await f.data.targetMetadataEntry(target);
  await readSeason(f);
  await f.data.seasonMetadataEntry(target, 2);
  f.state.tv = { ...f.state.tv, overview: '仅修改简介' };
  await f.data.targetMetadataEntry(target, { refresh: true });
  await readSeason(f);
  assert.equal(f.calls('/3/tv/43/season/1').length, 1);
  f.state.tv = { ...f.state.tv, last_air_date: '2026-11-07' };
  await f.data.targetMetadataEntry(target, { refresh: true });
  await readSeason(f);
  await f.data.seasonMetadataEntry(target, 2);
  assert.equal(f.calls('/3/tv/43/season/1').length, 1);
  assert.equal(f.calls('/3/tv/43/season/2').length, 1);
  f.state.tv = { ...f.state.tv, seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 1 }] };
  await f.data.targetMetadataEntry(target, { refresh: true });
  f.state.season = { season_number: 1, episodes: [
    { episode_number: 1, air_date: '2024-01-01' }, { episode_number: 2, air_date: '2024-01-02' },
  ] };
  const refreshed = await readSeason(f, { requestedEpisode: 2 });
  assert.equal(refreshed.value.episodes.length, 2);
  assert.equal(f.calls('/3/tv/43/season/1').length, 2);
  await f.data.seasonMetadataEntry(target, 2);
  assert.equal(f.calls('/3/tv/43/season/2').length, 1);
  f.state.tv = { ...f.state.tv, status: 'Returning Series',
    next_episode_to_air: { season_number: 1, episode_number: 3, air_date: '2026-11-09' } };
  await f.data.targetMetadataEntry(target, { refresh: true });
  await readSeason(f);
  assert.equal(f.calls('/3/tv/43/season/1').length, 3);
  assert.equal(f.calls('/3/tv/43/season/2').length, 1);
});

test('F3 direct season requests check expired parents and refresh only the requested dependency', async t => {
  const f = fixture(t), query = { language: 'en-US', append_to_response: 'credits' };
  const options = { imageSize: 'w500' }, parentPath = '/3/tv/43';
  const seasonPath = season => `${parentPath}/season/${season}`;
  const read = season => f.data.metadataEntry('tmdb', seasonPath(season), query, options);
  f.state.tv = tv({ status: 'Returning Series',
    seasons: [{ season_number: 1, episode_count: 1 }, { season_number: 2, episode_count: 1 }] });
  f.state.season.credits = { cast: [] };
  const first = await read(1), unrelated = await read(2);
  assert.equal(first.expiresAt, f.now() + STABLE);
  assert.deepEqual(f.observed.map(row => row.path), [parentPath, seasonPath(1), seasonPath(2)]);
  assert.deepEqual(f.observed[0].query, { language: 'en-US' });
  assert.equal(f.observed[0].imageSize, 'w500');
  assert.equal(f.observed[0].query.append_to_response, undefined);
  f.advance(DAY - 1);
  assert.deepEqual(await read(1), first);
  assert.equal(f.observed.length, 3);
  f.advance(1);
  f.state.tv = { ...f.state.tv,
    seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 1 }] };
  f.state.season = { ...f.state.season, episodes: [
    { episode_number: 1, air_date: '2024-01-01' }, { episode_number: 2, air_date: '2024-01-02' },
  ] };
  const updated = await read(1);
  assert.equal(updated.cache.status, 'fresh');
  assert.equal(updated.value.episodes.length, 2);
  assert.equal(updated.fetchedAt, f.now());
  assert.deepEqual(f.observed.map(row => row.path),
    [parentPath, seasonPath(1), seasonPath(2), parentPath, seasonPath(1)]);
  assert.deepEqual(await read(2), unrelated);
  assert.equal(f.calls(seasonPath(2)).length, 1);
  f.advance(DAY);
  f.state.tv = { ...f.state.tv, status: 'Ended' };
  const raw = await f.data.metadata('tmdb', seasonPath(1), query, options);
  assert.deepEqual(raw, updated.value);
  assert.equal(f.calls(parentPath).length, 3);
  assert.equal(f.calls(seasonPath(1)).length, 3);
  assert.equal(f.calls(seasonPath(2)).length, 1);
  assert.ok(f.calls(parentPath).every(row => row.query.append_to_response === undefined &&
    row.query.language === 'en-US' && row.imageSize === 'w500'));
});

test('F3/F4 a parent refresh failure preserves the successful season without publishing new dependency evidence', async t => {
  const f = fixture(t), path = '/3/tv/43/season/1', query = { language: 'zh-CN' };
  f.state.tv = tv({ status: 'Returning Series' });
  const original = await readSeason(f);
  f.advance(DAY);
  f.setResponse(({ path }) => {
    assert.equal(path, '/3/tv/43', 'failed parent must not dispatch a season');
    throw Object.assign(new Error('synthetic parent failure'), { code: 'NETWORK_ERROR' });
  });
  const stale = await f.data.metadataEntry('tmdb', path, query);
  assert.equal(stale.cache.status, 'stale');
  assert.deepEqual(stale.cache.error, { code: 'NETWORK_ERROR' });
  assert.deepEqual(stale.value, original.value);
  assert.equal(stale.fetchedAt, original.fetchedAt);
  assert.equal(stale.expiresAt, original.expiresAt);
  assert.deepEqual(stale.cache.components, original.cache.components);
  assert.equal(await f.data.cachedEntry(stale), undefined);
  await assert.rejects(f.data.metadata('tmdb', path, query), { code: 'NETWORK_ERROR' });
  assert.equal(f.calls(path).length, 1);
  f.state.tv = tv({ status: 'Returning Series', seasons: [{ season_number: 1, episode_count: 2 }] });
  f.state.season = { season_number: 1, episodes: [
    { episode_number: 1, air_date: '2024-01-01' }, { episode_number: 2, air_date: '2024-01-02' },
  ] };
  f.setResponse(({ path }) => path.includes('/season/') ? f.state.season : f.state.tv);
  const recovered = await readSeason(f);
  assert.equal(recovered.cache.status, 'fresh');
  assert.equal(recovered.cache.error, undefined);
  assert.equal(recovered.value.episodes.length, 2);
  assert.equal(recovered.fetchedAt, f.now());
  assert.equal(f.calls(path).length, 2);
});

test('F3/F5 leaving during parent validation retains the parent but does not dispatch a season', async t => {
  const f = fixture(t), gate = deferred(), entered = deferred();
  t.after(() => gate.resolve());
  let active = true;
  f.setResponse(async ({ path }, signal) => {
    assert.equal(path, '/3/tv/43');
    entered.resolve(signal);
    await gate.promise;
    return f.state.tv;
  });
  const work = readSeason(f, { canSchedule: () => active });
  const stopped = assert.rejects(work, { code: 'CANCELLED' });
  const signal = await entered.promise;
  active = false;
  gate.resolve();
  await stopped;
  assert.equal(signal.aborted, false);
  assert.equal(f.calls('/3/tv/43/season/1').length, 0);
  f.setResponse(({ path }) => {
    assert.equal(path, '/3/tv/43/season/1');
    return f.state.season;
  });
  assert.equal((await readSeason(f)).cache.status, 'fresh');
  assert.equal(f.calls('/3/tv/43').length, 1);
});

test('F3 requested missing/future episodes respect real CN broadcast days and successful completion evidence', async t => {
  const f = fixture(t), boundary = Date.parse('2026-11-09T00:00:00+08:00');
  f.state.tv = tv({ status: 'Returning Series', seasons: [{ season_number: 1, episode_count: 20 }],
    next_episode_to_air: { season_number: 1, episode_number: 2, air_date: '2026-11-09' } });
  await f.data.targetMetadataEntry(target);
  const season = await readSeason(f, { requestedEpisode: 2 });
  assert.equal(season.expiresAt, boundary);
  await readSeason(f, { requestedEpisode: 2 });
  await readSeason(f, { requestedEpisode: 999999999 });
  f.setTime(boundary - 1);
  await readSeason(f, { requestedEpisode: 2 });
  assert.equal(f.calls('/3/tv/43/season/1').length, 1);
  f.setTime(boundary);
  f.state.season = { season_number: 1, episodes: [] };
  await readSeason(f, { requestedEpisode: 2 });
  await readSeason(f, { requestedEpisode: 2 });
  await readSeason(f, { requestedEpisode: 3 });
  await readSeason(f, { requestedEpisode: 999999999 });
  assert.equal(f.calls('/3/tv/43/season/1').length, 2);
  await readSeason(f, { requestedEpisode: 2, refresh: true });
  assert.equal(f.calls('/3/tv/43/season/1').length, 3);
  for (const requestedEpisode of [0, -1, 1.5, '2']) {
    await assert.rejects(readSeason(f, { requestedEpisode }), { code: 'INVALID_REQUEST' });
  }
});

test('F3 unknown or future totals are not proof of a stable season; unknown TV status uses one day', async t => {
  const f = fixture(t);
  f.state.tv = tv({ status: 'Unknown', seasons: [{ season_number: 1, episode_count: 20 }],
    next_episode_to_air: { season_number: 1, episode_number: 2, air_date: '2026-12-01' } });
  const parent = await f.data.targetMetadataEntry(target);
  assert.equal(parent.expiresAt, f.now() + DAY);
  f.state.tv = tv({ seasons: [{ season_number: 1, episode_count: 20 }] });
  await f.data.targetMetadataEntry(target, { refresh: true });
  const season = await readSeason(f);
  assert.equal(season.expiresAt, f.now() + DAY);
  f.state.tv = tv({ seasons: [{ season_number: 1 }] });
  await f.data.targetMetadataEntry(target, { refresh: true });
  assert.equal((await readSeason(f)).expiresAt, f.now() + DAY);
  f.state.tv = tv({ seasons: [{ season_number: 1, episode_count: 1 }] });
  await f.data.targetMetadataEntry(target, { refresh: true });
  assert.equal((await readSeason(f)).expiresAt, f.now() + STABLE);
});

test('F4 expiry and explicit-refresh failures preserve success values/times and raw methods report the real failure', async t => {
  const f = fixture(t);
  const original = await readMovie(f);
  f.advance(STABLE);
  f.setResponse(() => { throw Object.assign(new Error('private upstream detail'), { code: 'TIMEOUT' }); });
  const stale = await readMovie(f);
  assert.equal(stale.cache.status, 'stale');
  assert.deepEqual(stale.cache.error, { code: 'TIMEOUT' });
  assert.equal(stale.fetchedAt, original.fetchedAt);
  assert.equal(stale.expiresAt, original.expiresAt);
  assert.deepEqual(stale.value, original.value);
  assert.equal(stale.cache.components.detail.fetchedAt, original.fetchedAt);
  assert.equal(stale.cache.components.detail.status, 'failed');
  await assert.rejects(f.data.metadata('tmdb', '/3/movie/42', { language: 'zh-CN' }), { code: 'TIMEOUT' });
  f.setResponse(() => movie({ overview: '恢复' }));
  const recovered = await readMovie(f);
  assert.equal(recovered.cache.status, 'fresh');
  assert.equal(recovered.fetchedAt, f.now());
  assert.equal(recovered.value.overview, '恢复');
  f.advance(1);
  f.setResponse(() => { throw Object.assign(new Error('synthetic'), { code: 'NETWORK_ERROR' }); });
  const failedRefresh = await readMovie(f, { refresh: true });
  assert.equal(failedRefresh.cache.status, 'stale');
  assert.equal(failedRefresh.fetchedAt, recovered.fetchedAt);
  assert.deepEqual(failedRefresh.cache.error, { code: 'NETWORK_ERROR' });
});

test('F4 refresh bypasses only the selected identity and ordinary reads keep other metadata fresh', async t => {
  const f = fixture(t);
  await readMovie(f);
  await f.data.metadataEntry('tmdb', '/3/movie/42', { language: 'en-US' });
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  await readMovie(f, { refresh: true });
  await f.data.metadataEntry('tmdb', '/3/movie/42', { language: 'en-US' });
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  assert.equal(f.observed.length, 4);
});

test('F4/F5 a failed refresh before TTL expiry cannot export stale success as fresh handoff evidence', async t => {
  const f = fixture(t), request = { kind: 'metadata', provider: 'tmdb',
    path: '/3/movie/42', query: { language: 'zh-CN' } };
  const original = await readMovie(f);
  assert.deepEqual(await f.data.cachedEntry(request, { minimumCompleteness: 'complete' }), original);
  f.advance(1);
  f.setResponse(() => { throw Object.assign(new Error('synthetic refresh failure'), { code: 'NETWORK_ERROR' }); });
  const failedRefresh = await readMovie(f, { refresh: true });
  assert.equal(failedRefresh.cache.status, 'stale');
  assert.deepEqual(failedRefresh.cache.error, { code: 'NETWORK_ERROR' });
  assert.equal(failedRefresh.expiresAt, original.expiresAt);
  assert.ok(failedRefresh.expiresAt > f.now());
  for (const minimumCompleteness of ['partial', 'complete']) {
    const exported = await f.data.cachedEntry(request, { minimumCompleteness });
    assert.equal(exported, undefined);
    assert.deepEqual(await f.data.handoff(exported ? [exported] : []), { accepted: 0, ignored: 0 });
  }
  assert.equal(f.observed.length, 2);
  const repeated = await readMovie(f);
  assert.equal(repeated.cache.status, 'stale');
  assert.deepEqual(repeated.cache.error, { code: 'NETWORK_ERROR' });
  assert.equal(repeated.fetchedAt, original.fetchedAt);
  assert.equal(repeated.expiresAt, original.expiresAt);
  assert.deepEqual(repeated.value, original.value);
  assert.equal(f.observed.length, 3);
});

test('F1/F4 effective configuration partitions persistent results without exposing credentials or clearing another provider', async t => {
  const f = fixture(t);
  const before = await readMovie(f);
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  f.settings.tmdb.update({ accessToken: 'synthetic-next-configuration-token' });
  f.state.movie = movie({ overview: '新配置' });
  const after = await readMovie(f);
  assert.notEqual(after.settings.revision, before.settings.revision);
  assert.equal(after.value.overview, '新配置');
  assert.equal(f.files('tmdb').length, 2);
  assert.equal(f.observed.length, 3);
  f.advance(STABLE);
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  assert.equal(f.files('douban').length, 1);
  assert.equal(f.observed.length, 4);
});

test('F3 Douban recent_hot home/category pages expire in one hour while details keep their own policy', async t => {
  const f = fixture(t), path = '/rexxar/api/v2/subject/recent_hot/movie';
  f.setResponse(({ path }) => path.includes('recent_hot')
    ? { items: [], tags: ['热门'], recommend_tags: [] } : f.state.douban);
  const home = await f.data.metadataEntry('douban', path, { category: '热门', start: 0, limit: 20 });
  assert.equal(home.expiresAt, f.now() + HOUR);
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  f.advance(HOUR - 1);
  await f.data.metadataEntry('douban', path, { category: '热门', start: 0, limit: 20 });
  assert.equal(f.observed.length, 2);
  f.advance(1);
  await f.data.metadataEntry('douban', path, { category: '热门', start: 0, limit: 20 });
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  assert.equal(f.observed.length, 3);
});

test('F5 same-key callers merge; leaving stops waiting while accepted metadata still persists', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred(), consumer = new AbortController();
  t.after(() => gate.resolve());
  f.setResponse(async (_request, signal) => {
    entered.resolve(signal);
    await gate.promise;
    return movie();
  });
  const left = readMovie(f, { signal: consumer.signal });
  const rejected = assert.rejects(left, { code: 'CANCELLED' });
  const signal = await entered.promise;
  const second = readMovie(f);
  consumer.abort();
  await rejected;
  assert.equal(signal.aborted, false);
  gate.resolve();
  assert.equal((await second).cache.status, 'fresh');
  assert.equal(f.observed.length, 1);
  assert.equal(f.files('tmdb').length, 1);
});

test('F5 distinct append demands share entity work then fetch only their missing components', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  f.setResponse(async ({ query }) => {
    if (query.append_to_response === 'credits') { entered.resolve(); await gate.promise; }
    const raw = movie();
    delete raw.images;
    if (query.append_to_response === 'images') raw.images = { posters: [] };
    return raw;
  });
  const first = f.data.metadataEntry('tmdb', '/3/movie/42', { append_to_response: 'credits' });
  await entered.promise;
  const second = f.data.metadataEntry('tmdb', '/3/movie/42', { append_to_response: 'credits,images' });
  gate.resolve();
  assert.equal((await first).cache.status, 'fresh');
  assert.equal((await second).cache.status, 'fresh');
  assert.deepEqual(f.observed.map(row => row.query.append_to_response), ['credits', 'images']);
});

test('F2/F5 partial handoff during complete production never suppresses the actual full response', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  f.setResponse(async () => { entered.resolve(); await gate.promise; return movie(); });
  const work = readMovie(f);
  await entered.promise;
  await f.data.seed({ kind: 'metadata', provider: 'tmdb', path: '/3/movie/42',
    query: { language: 'zh-CN' }, value: { id: 42, title: '摘要' },
    completeness: 'partial', fetchedAt: f.now(), settings: { revision: f.settings.tmdb.get().revision } });
  gate.resolve();
  const complete = await work;
  assert.equal(complete.completeness, 'complete');
  assert.equal(complete.value.runtime, 120);
  assert.equal(complete.cache.status, 'fresh');
  assert.equal(f.observed.length, 1);
});

test('F2/F5 partial seed with retained full detail cannot supersede an in-flight complete refresh', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t), request = { kind: 'metadata', provider: 'tmdb', path: '/3/movie/42',
    query: { language: 'zh-CN', append_to_response: 'credits,images' },
    settings: { revision: f.settings.tmdb.get().revision } };
  const full = await f.data.metadataEntry(request.provider, request.path, request.query);
  f.advance(1);
  f.setResponse(async () => { entered.resolve(); await gate.promise;
    return movie({ overview: '完整刷新结果', runtime: 130, credits: { cast: [{ id: 7 }] } }); });
  const work = f.data.metadataEntry(request.provider, request.path, request.query, { refresh: true });
  await entered.promise;
  assert.deepEqual(await f.data.seed({ ...request, completeness: 'partial', fetchedAt: f.now(),
    value: { id: 42, title: '生产中的摘要' } }), full.value);
  const during = await f.data.cachedEntry(request, { minimumCompleteness: 'complete' });
  assert.deepEqual(during.value, full.value);
  assert.equal(during.fetchedAt, full.fetchedAt);
  assert.equal(during.expiresAt, full.expiresAt);
  gate.resolve();
  const complete = await work;
  assert.equal(complete.cache.status, 'fresh');
  assert.equal(complete.completeness, 'complete');
  assert.equal(complete.fetchedAt, f.now());
  assert.equal(complete.value.runtime, 130);
  assert.equal(complete.value.overview, '完整刷新结果');
  assert.deepEqual(complete.value.credits, { cast: [{ id: 7 }] });
  assert.deepEqual(await f.data.cachedEntry(request, { minimumCompleteness: 'complete' }), complete);
  assert.equal(f.observed.length, 2);
});

test('F2/F5 a summary seeded during expired full refresh survives failure in memory and after restart', async t => {
  for (const persist of [false, true]) await t.test(persist ? 'persistent restart' : 'memory only', async t => {
    const entered = deferred(), gate = deferred();
    t.after(() => gate.resolve());
    const f = fixture(t, { persist }), request = { kind: 'metadata', provider: 'tmdb', path: '/3/movie/42',
      query: { language: 'zh-CN', append_to_response: 'credits,images' },
      settings: { revision: f.settings.tmdb.get().revision } };
    const full = await f.data.metadataEntry(request.provider, request.path, request.query);
    f.advance(STABLE);
    f.setResponse(async () => { entered.resolve(); await gate.promise;
      throw Object.assign(new Error('synthetic complete failure'), { code: 'TIMEOUT' }); });
    const work = f.data.metadataEntry(request.provider, request.path, request.query);
    await entered.promise;
    f.advance(1);
    const partial = { ...request, completeness: 'partial', fetchedAt: f.now(),
      value: { id: 42, title: '完整请求在途时的新摘要', overview: '独立摘要内容' } };
    assert.deepEqual(await f.data.seed(partial), full.value);
    const during = await f.data.metadataEntry(request.provider, request.path, request.query, { completeness: 'partial' });
    assert.deepEqual(during.value, partial.value);
    assert.equal(during.fetchedAt, partial.fetchedAt);
    gate.resolve();
    const stale = await work;
    assert.equal(stale.cache.status, 'stale');
    assert.deepEqual(stale.cache.error, { code: 'TIMEOUT' });
    assert.deepEqual(stale.value, full.value);
    assert.equal(stale.fetchedAt, full.fetchedAt);
    assert.equal(stale.expiresAt, full.expiresAt);
    for (const name of ['detail', 'credits', 'images']) {
      assert.equal(stale.cache.components[name].status, 'failed');
      assert.equal(stale.cache.components[name].fetchedAt, full.cache.components[name].fetchedAt);
      assert.deepEqual(stale.cache.components[name].error, { code: 'TIMEOUT' });
    }
    let active = f;
    if (persist) {
      await f.data.close();
      active = fixture(t, { dataRoot: f.dataRoot, at: f.now() });
    }
    const summary = await active.data.metadataEntry(request.provider, request.path, request.query, { completeness: 'partial' });
    assert.equal(summary.cache.status, 'fresh');
    assert.equal(summary.completeness, 'partial');
    assert.deepEqual(summary.value, partial.value);
    assert.equal(summary.fetchedAt, partial.fetchedAt);
    assert.equal(summary.expiresAt, partial.fetchedAt + 6 * HOUR);
    for (const name of ['detail', 'credits', 'images']) {
      assert.deepEqual(summary.cache.components[name], stale.cache.components[name]);
    }
    assert.deepEqual(await active.data.cachedEntry(request, { minimumCompleteness: 'partial' }), summary);
    assert.equal(await active.data.cachedEntry(request, { minimumCompleteness: 'complete' }), undefined);
    assert.equal(active.observed.length, persist ? 0 : 2);
    active.advance(1);
    active.setResponse(() => movie({ title: '完整详情恢复', runtime: 140 }));
    const recovered = await active.data.metadataEntry(request.provider, request.path, request.query);
    assert.equal(recovered.cache.status, 'fresh');
    assert.equal(recovered.completeness, 'complete');
    assert.equal(recovered.value.title, '完整详情恢复');
    assert.equal(recovered.value.runtime, 140);
    assert.equal(recovered.cache.components.summary.fetchedAt, partial.fetchedAt);
    assert.equal(recovered.fetchedAt, active.now());
  });
});

test('F5 invalidate removes provider documents and drains/revokes late writes without removing other providers', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  await readMovie(f);
  await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  f.setResponse(async (_request, signal) => { entered.resolve(signal); await gate.promise; return movie({ overview: '晚到' }); });
  const work = readMovie(f, { refresh: true });
  const rejected = assert.rejects(work, { code: 'CANCELLED' });
  const signal = await entered.promise;
  let drained = false;
  const invalidation = f.data.invalidate('tmdb').then(() => { drained = true; });
  assert.equal(signal.aborted, true);
  await rejected;
  await tick();
  assert.equal(drained, false);
  assert.equal(f.files('tmdb').length, 0);
  assert.equal(f.files('douban').length, 1);
  gate.resolve();
  await invalidation;
  assert.equal(f.files('tmdb').length, 0);
  f.setResponse(() => movie({ overview: '当前配置' }));
  assert.equal((await readMovie(f)).value.overview, '当前配置');
});

test('F5 invalidate rejects storage errors only after revoking and physically draining all accepted work', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t);
  await readMovie(f);
  const douban = await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51');
  const path = join(f.dataRoot, 'film-metadata/tmdb', f.files('tmdb')[0]);
  const bytes = readFileSync(path);
  const remove = PrivateDocuments.prototype.remove, removed = [];
  t.mock.method(PrivateDocuments.prototype, 'remove', function(name) {
    removed.push(name);
    if (name.startsWith('tmdb/')) throw new Error('controlled private storage failure');
    return remove.call(this, name);
  });
  f.setResponse(async (_request, signal) => {
    entered.resolve(signal); await gate.promise; return movie({ overview: '失效后的晚到值' });
  });
  const rejected = assert.rejects(readMovie(f, { refresh: true }), { code: 'CANCELLED' });
  const signal = await entered.promise, otherRevocations = [];
  let invalidation, results, settled = false;
  assert.doesNotThrow(() => {
    results = Promise.allSettled([
      invalidation = f.data.invalidate('tmdb'),
      (() => { otherRevocations.push('service'); return Promise.resolve(); })(),
      (() => { otherRevocations.push('media'); return Promise.resolve(); })(),
    ]);
  });
  invalidation.then(() => { settled = true; }, () => { settled = true; });
  assert.ok(invalidation instanceof Promise);
  assert.deepEqual(otherRevocations, ['service', 'media']);
  assert.equal(signal.aborted, true, 'revocation remains synchronous even if storage removal fails');
  assert.equal(removed.length, 1);
  assert.ok(removed[0].startsWith('tmdb/'));
  await rejected;
  await tick();
  assert.equal(settled, false, 'storage rejection waits for the real producer/cache drain');
  assert.deepEqual(readFileSync(path), bytes);
  const requests = f.observed.length;
  assert.deepEqual(await f.data.metadataEntry('douban', '/rexxar/api/v2/movie/51'), douban);
  assert.equal(f.observed.length, requests);
  assert.equal(f.files('douban').length, 1);
  gate.resolve();
  const completed = await results;
  assert.equal(completed[0].status, 'rejected');
  assert.equal(completed[0].reason.code, 'STORAGE_ERROR');
  assert.equal(completed[0].reason.message, 'STORAGE_ERROR');
  assert.deepEqual(completed.slice(1).map(row => row.status), ['fulfilled', 'fulfilled']);
  assert.equal(settled, true);
  assert.deepEqual(readFileSync(path), bytes, 'revoked producer cannot overwrite the prior document after a failed deletion');
  assert.equal(f.files('douban').length, 1);
});

test('F5 close waits for actual producers and preserves previous disk success without accepting late refreshes', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const original = await readMovie(f);
  f.setResponse(async (_request, signal) => { entered.resolve(signal); await gate.promise; return movie({ overview: '不得写回' }); });
  const work = readMovie(f, { refresh: true });
  const rejected = assert.rejects(work, error => ['CANCELLED', 'CLOSED'].includes(error.code));
  const signal = await entered.promise;
  let closed = false;
  const closing = f.data.close().then(() => { closed = true; });
  assert.equal(signal.aborted, true);
  await rejected;
  await tick();
  assert.equal(closed, false);
  gate.resolve();
  await closing;
  const next = fixture(t, { dataRoot: f.dataRoot });
  next.setResponse(() => assert.fail('valid durable result lost on close'));
  assert.deepEqual(await readMovie(next), original);
  await assert.rejects(readMovie(f), { code: 'CLOSED' });
});

test('F8 oversized metadata returns complete pages, persists and refreshes without bypassing the shared memory budget', async t => {
  const f = fixture(t, { maximumBytes: 1000 });
  f.state.movie = movie({ overview: 'large'.repeat(20_000) });
  const original = await readMovie(f);
  assert.equal(original.value.overview.length, 100_000);
  await f.data.close();
  const next = fixture(t, { dataRoot: f.dataRoot, maximumBytes: 1000 });
  assert.equal((await readMovie(next)).value.overview, original.value.overview);
  assert.equal(next.observed.length, 0);
  next.state.movie = movie({ overview: 'new'.repeat(20_000) });
  await readMovie(next, { refresh: true });
  await next.data.close();
  const final = fixture(t, { dataRoot: f.dataRoot, maximumBytes: 1000 });
  assert.equal((await readMovie(final)).value.overview, next.state.movie.overview);
  assert.equal(final.observed.length, 0);
});

test('F2/F8 without a data root oversized full results are delivered whole without blocking a later summary', async t => {
  const f = fixture(t, { maximumBytes: 1000, persist: false });
  f.state.movie = movie({ overview: 'large'.repeat(20_000) });
  const full = await readMovie(f);
  assert.equal(full.value.overview.length, 100_000);
  assert.equal(full.completeness, 'complete');
  assert.equal(await f.data.cachedEntry(full), undefined);
  f.advance(STABLE);
  f.state.movie = { id: 42, title: '独立摘要' };
  const partial = await readMovie(f, { completeness: 'partial', refresh: true });
  assert.deepEqual(partial.value, f.state.movie);
  assert.equal(partial.completeness, 'partial');
  assert.equal(partial.cache.status, 'fresh');
  assert.equal(partial.fetchedAt, f.now());
  assert.equal(partial.expiresAt, f.now() + 6 * HOUR);
  assert.deepEqual(f.files('tmdb'), []);
});

test('F1 corrupted durable metadata reports storage failure without pretending an upstream refresh succeeded', async t => {
  const f = fixture(t);
  await readMovie(f);
  const path = join(f.dataRoot, 'film-metadata/tmdb', f.files('tmdb')[0]);
  await f.data.close();
  writeFileSync(path, '{"version":1,"state":');
  const next = fixture(t, { dataRoot: f.dataRoot });
  await assert.rejects(readMovie(next), { code: 'CORRUPT_STORAGE' });
  assert.equal(next.observed.length, 0);
  assert.equal(readFileSync(path, 'utf8'), '{"version":1,"state":');
});
