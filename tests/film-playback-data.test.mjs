import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createPlaybackCache } from '../packages/film-playback/src/cache.js';
import { createPlaybackData } from '../packages/film-playback/src/data.js';
import { recognizeCandidates } from '../packages/film-playback/src/recognition.js';
import { buildEpisodeMap, resolveEpisode } from '../packages/film-playback/src/episodes.js';
import { sourceRevision } from '../packages/film-playback/src/identity.js';
import { encodePanResource } from '@meowfilm/script-host/cat-output';
import { createTMDBDataModule, createDoubanDataModule } from '../apps/runtime/modules/metadata-data.mjs';

const HOUR = 3_600_000, DAY = 24 * HOUR, STABLE = 14 * DAY, ADDRESS = 180_000;
const initialTime = Date.parse('2026-11-08T04:00:00Z');
const source = { key: 'cat:installation-a:site', engine: 'cat', installationId: 'installation-a',
  siteId: 'site', revision: 3, generation: 4, name: '原始站点' };
const portable = { key: 'source:installation-b', engine: 'source', installationId: 'installation-b',
  siteId: 'source', revision: 7, generation: 8, name: '独立 Spider' };
const movieTarget = { id: 'tmdb:movie:42', metadataProvider: 'tmdb', metadataId: 42,
  kind: 'movie', title: '真实电影', year: 2024 };
const tvTarget = { id: 'tmdb:tv:43', metadataProvider: 'tmdb', metadataId: 43,
  kind: 'tv', title: '真实剧集', year: 2024 };
const movieValue = (id = 42, patch = {}) => ({ id, title: '真实电影', release_date: '2024-01-01',
  overview: '实际简介', runtime: 120, genres: [], credits: { cast: [] }, similar: { results: [] },
  images: { posters: [] }, ...patch });
const tvValue = (id = 43, patch = {}) => ({ id, name: '真实剧集', first_air_date: '2024-01-01',
  overview: '实际剧集简介', genres: [], seasons: [{ season_number: 1, episode_count: 2 }],
  credits: { cast: [] }, similar: { results: [] }, images: { posters: [] }, status: 'Ended', ...patch });
const doubanValue = (id = '51', patch = {}) => ({ id, title: '真实电影', year: '2024',
  intro: '豆瓣实际简介', genres: ['剧情'], ...patch });
const detailValue = (id = 'detail-a', patch = {}) => ({ list: [{ vod_id: id,
  vod_play_from: '自定义盘名', vod_play_url: '正片$original$opaque$id', ...patch }] });
const share = (provider = 'quark', patch = {}) => ({ provider,
  url: ({ quark: 'https://pan.quark.cn/s/shareABCD', uc: 'https://drive.uc.cn/s/shareABCD',
    yidong: 'https://caiyun.139.com/m/i?shareABCD' })[provider], password: 'synthetic-only', ...patch });
const ref = (provider = 'quark', fileId = 'file-1', patch = {}) => ({ kind: 'share', provider,
  accountRevision: 2, fileId, shareId: 'shareABCD', name: 'S01E01.mkv',
  stoken: 'synthetic-only-token', fileToken: 'synthetic-only-file-token', ...patch });
const candidate = (file = ref(), patch = {}) => ({ id: 'candidate-1', source: { ...source, detailId: 'detail-a' },
  lineId: 0, flag: 0, provider: file.provider, file, ...patch });
const rawCandidate = (selectedSource = source, patch = {}) => ({ id: 'raw-candidate', source: selectedSource,
  lineId: 0, flag: '未知原始flag', lineLabel: '错误展示名称', playId: 'original$opaque$id', ...patch });
const requestFor = target => ({ kind: 'metadata', provider: target.metadataProvider,
  path: target.metadataProvider === 'tmdb' ? `/3/${target.kind}/${target.metadataId}`
    : `/rexxar/api/v2/${target.kind}/${target.metadataId}`,
  query: target.metadataProvider === 'tmdb' ? { language: 'zh-CN', append_to_response: 'credits,similar,images' } : {} });
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const tick = () => new Promise(done => setImmediate(done));
const success = value => ({ ok: true, value });
const failure = code => ({ ok: false, code });

function fixture(t, { maximumBytes, dataRoot } = {}) {
  let at = initialTime;
  const observed = [];
  const instances = new Map([[source.key, { ...source, state: 'ready', enabled: true, sites: [{ key: source.siteId }] }],
    [portable.key, { ...portable, state: 'ready', protocol: 'cat', capabilities: ['search', 'detail', 'play', 'report'] }]]);
  const accountRows = ['baidu', 'quark', 'uc', 'tianyi', 'yidong', 'quark_tv', 'uc_tv'].map(provider =>
    ({ provider, revision: 2, configured: !provider.endsWith('_tv'), fields: [] }));
  const revisions = { tmdb: 'opaque-config-a', douban: 'opaque-douban-a' };
  const invoke = params => success(params.operation === 'search' || params.action === 'search'
    ? { list: [{ vod_id: 'detail-a', vod_name: '真实电影' }], pagecount: 1 }
    : params.operation === 'detail' || params.action === 'detail' ? detailValue(params.payload.id)
      : params.operation === 'report' || params.action === 'report' ? { ok: true }
        : { url: 'https://media.invalid/raw.mp4', header: { Referer: 'https://example.invalid/' }, watchReport: true });
  const handlers = {
    cat: { status: () => success({ instances: [...instances.values()].filter(row => row.engine === 'cat') }), invoke },
    source: { status: () => success({ instances: [...instances.values()].filter(row => row.engine === 'source') }), invoke },
    pan: {
      'settings.get': () => success({ accounts: structuredClone(accountRows) }),
      list: ({ provider }) => success({ share: { id: 'shareABCD' }, files: [{ name: 'S01E01.mkv',
        path: '/原始目录/第1季/S01E01.mkv', size: '9007199254740992', file: ref(provider) }] }),
      prepare: ({ file }) => success({ file: { ...file, kind: 'stored', fileId: 'stored-' + file.fileId } }),
      'account.match': ({ provider, revision, baseRevision }) => success({ provider, revision, baseRevision,
        baseProvider: provider.replace(/_tv$/, ''), sameAccount: true }),
      play: ({ provider, mode, file }) => success({ url: `https://media.invalid/${provider}/${mode}/${file.fileId}?issued=${at}`,
        headers: { Referer: 'https://example.invalid/' }, mode, file }),
    },
    tmdb: {
      'settings.get': () => success({ revision: revisions.tmdb, apiKeyConfigured: true }),
      request: ({ path }) => success(path.startsWith('/3/search/') ? { page: 1, total_pages: 1, results: [] }
        : path.includes('/season/') ? { season_number: Number(path.split('/').at(-1)),
          episodes: [{ episode_number: 1, air_date: '2024-01-01' }] }
          : path.startsWith('/3/tv/') ? tvValue(Number(path.split('/').at(-1))) : movieValue(Number(path.split('/').at(-1)))),
    },
    douban: {
      'settings.get': () => success({ revision: revisions.douban, cookieConfigured: true }),
      search: () => success({ subjects: { items: [] } }),
      request: ({ path }) => success(doubanValue(path.split('/').at(-1))),
    },
  };
  const modules = new Map(Object.entries(handlers).map(([id, operations]) => [id, { id,
    methods: Object.fromEntries(Object.keys(operations).map(operation =>
      [({ cat: 'cat.script.', source: 'source.runtime.', pan: 'pan.', tmdb: 'tmdb.data.', douban: 'douban.data.' })[id] + operation,
        async (params, { signal, mediaPurpose }) => {
          observed.push({ id, operation, params: structuredClone(params), signal, mediaPurpose });
          return handlers[id][operation](params, signal);
        }])) }]));
  const cache = createPlaybackCache({ now: () => at, ...(maximumBytes ? { maximumBytes } : {}) });
  const data = createPlaybackData({ modules, cache, dataRoot, now: () => at });
  t.after(async () => { await data.close(); await cache.close(); });
  return { data, cache, modules, handlers, observed, instances, accountRows, revisions,
    now: () => at, setTime: value => { at = value; }, advance: duration => { at += duration; },
    calls: (id, operation) => observed.filter(row => row.id === id && row.operation === operation) };
}

test('E08 Cat search keeps raw pagination and typed detail/flag/play IDs, without unrelated detail/list requests', async t => {
  const f = fixture(t);
  f.handlers.cat.invoke = params => params.operation === 'search' ? success(JSON.stringify({ page: 2,
    pagecount: 4, list: [{ vod_id: 0, vod_name: '真实电影' }] })) : params.operation === 'detail'
      ? success(JSON.stringify({ id: 0, lines: [{ id: 0, label: '展示名字不是flag', flag: 0,
        episodes: [{ id: 0, name: '正片' }] }] })) : success({ url: 'https://media.invalid/movie.mp4' });
  const page = await f.data.search(source, '真实电影', 2);
  assert.equal(page.hasMore, true);
  assert.equal(page.items[0].vod_id, 0);
  assert.deepEqual(f.calls('cat', 'invoke')[0].params.payload, { wd: '真实电影', quick: false, page: 2 });
  assert.equal(f.calls('pan', 'list').length, 0);
  assert.equal(f.calls('cat', 'invoke').length, 1);
  const detail = await f.data.detail(source, 0);
  assert.equal(detail.detailId, 0);
  const [selected] = await recognizeCandidates(detail, { source, target: movieTarget });
  assert.equal(selected.playId, 0); assert.equal(selected.flag, 0); assert.equal(selected.lineId, 0);
  const descriptor = await f.data.resolve(selected);
  const play = f.calls('cat', 'invoke').at(-1).params;
  assert.deepEqual(play.payload, { flag: 0, id: 0, flags: [] });
  assert.equal(f.calls('cat', 'invoke').at(-1).mediaPurpose, 'receipt');
  assert.ok(f.calls('cat', 'invoke').slice(0, -1).every(call => call.mediaPurpose === undefined));
  assert.equal(descriptor.scope.sourceEngine, 'cat');
  assert.equal(descriptor.scope.sourceInstallationId, source.installationId);
  assert.equal(descriptor.scope.sourceRevision, sourceRevision(source));
});

test('E08 portable Spider uses keyword/action/revision rather than Cat wd/operation', async t => {
  const f = fixture(t);
  await f.data.search(portable, '真实电影', 3);
  const params = f.calls('source', 'invoke')[0].params;
  assert.deepEqual(params, { installationId: portable.installationId, revision: portable.revision,
    generation: portable.generation, action: 'search', payload: { keyword: '真实电影', quick: false, page: 3 } });
  const descriptor = await f.data.resolve(rawCandidate(portable));
  assert.equal(descriptor.scope.sourceEngine, 'source');
  assert.deepEqual(f.calls('source', 'invoke').at(-1).params.payload,
    { flag: '未知原始flag', id: 'original$opaque$id', vipFlags: [] });
});

test('E49 K2 data retains full nonempty pages without a pagecount and exposes valid explicit totals independently of overlap', async t => {
  const f = fixture(t);
  const list = Array.from({ length: 1301 }, (_, id) => ({ vod_id: id, vod_name: '完整原行', vod_remarks: `备注-${id}` }));
  const raw = { list, total: 1, limit: 1, hasMore: false };
  const before = structuredClone(raw);
  f.handlers.cat.invoke = () => success(raw);
  const first = await f.data.search(source, '完整无总页', 1);
  assert.equal(first.hasMore, true);
  assert.equal(first.explicitPageCount, false);
  assert.equal(first.items.length, 1301);
  assert.deepEqual(first.raw, before);
  assert.deepEqual(raw, before);
  f.handlers.cat.invoke = () => success({ list: [] });
  const empty = await f.data.search(source, '空页', 2);
  assert.equal(empty.hasMore, false);
  assert.equal(empty.explicitPageCount, false);
  for (const count of [{ pagecount: 4 }, { pageCount: '4' }, { pagecount: 'invalid', pageCount: 4 }]) {
    f.handlers.cat.invoke = () => success({ list: [], ...count });
    const page = await f.data.search(source, JSON.stringify(count), 2);
    assert.equal(page.pageCount, 4); assert.equal(page.explicitPageCount, true); assert.equal(page.hasMore, true);
  }
  f.handlers.cat.invoke = () => success({ list: [{ vod_id: 0 }, { vod_id: '0' }], pagecount: '2' });
  const last = await f.data.search(source, '末页', 2);
  assert.equal(last.hasMore, false);
  assert.deepEqual(last.items.map(row => row.vod_id), [0, '0']);
  for (const pagecount of [0, null, '', -1, 1.5, 'NaN']) {
    f.handlers.cat.invoke = () => success({ list: [{ id: 0 }], pagecount });
    const page = await f.data.search(source, `无效总页${JSON.stringify(pagecount)}`, 3);
    assert.equal(page.explicitPageCount, false); assert.equal(page.hasMore, true);
  }
  assert.equal(f.calls('cat', 'invoke').every(row => row.params.operation === 'search'), true);
});

test('E49 K1 formal full detail and search requests share the actual data cache entry without dropping append or forging timestamps', async t => {
  const f = fixture(t);
  const query = { language: 'zh-CN', append_to_response: 'credits,similar,images' };
  const request = { kind: 'metadata', provider: 'tmdb', path: '/3/movie/42', query, imageSize: 'original' };
  f.handlers.tmdb.request = ({ path }) => success(path === '/3/search/multi'
    ? { results: [{ id: 42, title: '真实电影', media_type: 'movie' }] } : movieValue());
  const detail = await f.data.metadata('tmdb', request.path, query, { imageSize: 'original' });
  const entry = await f.data.cachedEntry(request);
  assert.equal(entry.completeness, 'complete');
  assert.equal(entry.fetchedAt, initialTime);
  assert.equal(entry.expiresAt, initialTime + STABLE);
  assert.deepEqual(entry.settings, { revision: f.revisions.tmdb });
  assert.deepEqual(await f.data.targetMetadata(movieTarget), detail);
  assert.equal(f.calls('tmdb', 'request').length, 1);
  assert.deepEqual(f.calls('tmdb', 'request')[0].params, { path: request.path, query, imageSize: 'original' });
  assert.deepEqual(await f.data.handoff([entry]), { accepted: 0, ignored: 1 });
  assert.deepEqual(await f.data.cachedEntry(request), entry);
  const searchQuery = { query: '真实电影', page: 1, language: 'zh-CN' };
  await f.data.metadata('tmdb', '/3/search/multi', searchQuery, { imageSize: 'w500' });
  const search = await f.data.cachedEntry({ kind: 'metadata', provider: 'tmdb',
    path: '/3/search/multi', query: searchQuery, imageSize: 'w500' });
  assert.equal(search.fetchedAt, initialTime);
  assert.equal(search.expiresAt, initialTime + 6 * HOUR);
  assert.deepEqual(f.calls('tmdb', 'request')[1].params,
    { path: '/3/search/multi', query: searchQuery, imageSize: 'w500' });
  assert.equal(await f.data.targetMetadata({ id: 'actual-group', kind: 'group', title: '真实电影',
    members: [{ sourceKey: source.key, detailId: 0 }] }), null);
  assert.equal(f.calls('tmdb', 'request').length, 2);
});

test('E01/E49 detail aliases retain the original typed vod_id despite conflicting id, detailId and an unknown raw field', async t => {
  const f = fixture(t);
  const rows = [0, '0'].map(vod_id => ({ vod_id, id: 'ignored', detailId: 'ignored-detail', vod_name: '星河',
    raw: { id: 'wrong', original: true }, lines: [{ id: 'actual', episodes: [{ id: `file-${typeof vod_id}`, name: '星河' }] }] }));
  const before = structuredClone(rows);
  f.handlers.cat.invoke = params => success(params.operation === 'search' ? { list: rows, pagecount: 1 } : { list: rows });
  const page = await f.data.search(source, '星河', 1);
  assert.deepEqual(page.items, before);
  for (const id of [0, '0']) {
    const value = await f.data.detail(source, id);
    assert.equal(value.detailId, id);
    assert.equal(value.lines[0].episodes[0].playId, `file-${typeof id}`);
  }
  assert.deepEqual(f.calls('cat', 'invoke').filter(row => row.params.operation === 'detail').map(row => row.params.payload.id),
    [0, '0']);
  assert.deepEqual(rows, before);
  for (const [index, vod_id] of [false, '', {}, [], 0.5].entries()) {
    const requested = `invalid-${index}`;
    f.handlers.cat.invoke = () => success({ list: [{ vod_id, id: requested, detailId: requested,
      raw: { id: requested }, lines: [] }] });
    await assert.rejects(f.data.detail(source, requested), { code: 'UPSTREAM_FORMAT' });
  }
});

test('E38/E42/N1 Cat and portable media accept normal local and external HTTP addresses', async t => {
  const f = fixture(t);
  for (const candidate of [rawCandidate(), rawCandidate(portable)]) {
    for (const url of ['http://127.0.0.1:49000/proxy/installation-a/4/resource/media',
      'http://192.168.1.2:49000/movie?part=1', 'https://user:password@media.invalid/direct.mp4']) {
      f.handlers[candidate.source.engine].invoke = () => success({ url, headers: { Host: 'media.virtual.invalid' } });
      const result = await f.data.refresh(candidate);
      assert.equal(result.url, url);
      assert.deepEqual(result.headers, { Host: 'media.virtual.invalid' });
    }
  }
});

test('E23 a late Cat media response cannot publish after source revocation', async t => {
  const f = fixture(t), entered = deferred(), drained = deferred();
  f.handlers.cat.invoke = async () => {
    entered.resolve(); await drained.promise;
    return success({ url: 'http://127.0.0.1:49000/late-media' });
  };
  const result = f.data.resolve(rawCandidate());
  const rejected = assert.rejects(result, { code: 'CANCELLED' });
  await entered.promise;
  let complete = false;
  const revoked = f.data.invalidate({ kind: 'source', engine: 'cat', installationId: source.installationId })
    .then(() => { complete = true; });
  await rejected;
  await tick();
  assert.equal(complete, false, 'revocation must wait for the accepted source request to settle');
  drained.resolve();
  await revoked;
  assert.equal(complete, true);
});

test('E08/E48 public data operations reject invalid inputs asynchronously without invoking owners', async t => {
  const f = fixture(t);
  for (const request of [
    () => f.data.search({}, 'movie'), () => f.data.detail(source, null), () => f.data.expand(source, {}),
    () => f.data.metadata('tmdb', '/movie/42'), () => f.data.targetMetadata({ ...movieTarget, metadataId: 0 }),
    () => f.data.seasonMetadata(movieTarget, 1), () => f.data.metadataSources({ ...movieTarget, metadataId: 0 }),
    () => f.data.resolve({}), () => f.data.refresh(candidate(), { mode: 'invented' }),
    () => f.data.report(rawCandidate(), {}), () => f.data.seed({}), () => f.data.handoff({}), () => f.data.cachedEntry({}),
    () => f.data.invalidate({}), () => f.data.invalidate(''),
  ]) await assert.rejects(request(), { code: 'INVALID_REQUEST' });
  assert.equal(f.observed.length, 0);
});

test('E08/E23 stale source generation/site and book protocols are rejected before invocation or cached reuse', async t => {
  const f = fixture(t);
  await f.data.search(source, '真实电影');
  f.instances.get(source.key).generation++;
  await assert.rejects(f.data.search(source, '真实电影'), { code: 'STALE_SOURCE' });
  assert.equal(f.calls('cat', 'invoke').length, 1);
  f.instances.get(source.key).generation = source.generation;
  f.instances.get(source.key).sites = [{ key: 'another-site' }];
  await assert.rejects(f.data.detail(source, 0), { code: 'STALE_SOURCE' });
  f.instances.get(portable.key).protocol = 'legado';
  await assert.rejects(f.data.search(portable, '真实电影'), { code: 'UNSUPPORTED_OPERATION' });
  assert.equal(f.calls('source', 'invoke').length, 0);
});

test('E24/E25 search/detail same identity coalesces, expires in one hour, and failures never poison success', async t => {
  const f = fixture(t);
  const gate = deferred();
  f.handlers.cat.invoke = async params => { await gate.promise; return success({ list: [], pagecount: 1 }); };
  const first = f.data.search(source, 'A'), joined = f.data.search(source, 'A');
  await tick(); assert.equal(f.calls('cat', 'invoke').length, 1);
  gate.resolve(); await Promise.all([first, joined]);
  await f.data.search(source, 'A'); assert.equal(f.calls('cat', 'invoke').length, 1);
  f.advance(HOUR); await f.data.search(source, 'A'); assert.equal(f.calls('cat', 'invoke').length, 2);
  f.handlers.cat.invoke = () => success({});
  await assert.rejects(f.data.search(source, 'bad'), { code: 'UPSTREAM_FORMAT' });
  await assert.rejects(f.data.search(source, 'bad'), { code: 'UPSTREAM_FORMAT' });
  f.handlers.cat.invoke = params => success(detailValue(params.payload.id));
  await f.data.detail(source, 'A'); await f.data.detail(source, 'B'); await f.data.detail(source, 'A');
  assert.deepEqual(f.calls('cat', 'invoke').slice(-2).map(row => row.params.payload.id), ['A', 'B']);
  f.handlers.cat.invoke = () => success({ list: [] });
  await assert.rejects(f.data.detail(source, 'missing'), { code: 'UPSTREAM_FORMAT' });
  f.handlers.cat.invoke = params => success(detailValue(params.payload.id));
  assert.equal((await f.data.detail(source, 'missing')).detailId, 'missing');
});

test('E08/E49 an explicit different detail identity cannot be cached under the selected typed ID or handed off as complete', async t => {
  const f = fixture(t);
  f.handlers.cat.invoke = () => success({ id: 'other', lines: [{ id: 0, episodes: [] }] });
  await assert.rejects(f.data.detail(source, 0), { code: 'UPSTREAM_FORMAT' });
  f.handlers.cat.invoke = () => success({ id: '0', lines: [{ id: 0, episodes: [] }] });
  await assert.rejects(f.data.detail(source, 0), { code: 'UPSTREAM_FORMAT' });
  await assert.rejects(f.data.seed({ kind: 'detail', source, detailId: 0,
    value: { id: 'other', lines: [{ id: 0, episodes: [] }] },
    completeness: 'complete', fetchedAt: f.now() }), { code: 'UPSTREAM_FORMAT' });
  assert.equal(await f.data.cachedEntry({ kind: 'detail', source, detailId: 0 }), undefined);
  f.handlers.cat.invoke = () => success({ id: 0, lines: [{ id: 0, episodes: [] }] });
  assert.equal((await f.data.detail(source, 0)).detailId, 0);
  assert.equal(f.calls('cat', 'invoke').length, 3);
});

test('E26 all business items survive a small cache budget; eviction does not truncate pages', async t => {
  const f = fixture(t, { maximumBytes: 1024 });
  const list = Array.from({ length: 1500 }, (_, id) => ({ vod_id: id, vod_name: '真实名称-' + id }));
  f.handlers.cat.invoke = () => success({ list, pagecount: 2 });
  assert.equal((await f.data.search(source, '完整页')).items.length, 1500);
  assert.equal((await f.data.search(source, '完整页')).items.length, 1500);
  assert.equal(f.calls('cat', 'invoke').length, 2);
});

test('E08/E27 complete direct/file/stored lines never list; expansion preserves mixed direct siblings and complete private files', async t => {
  const f = fixture(t);
  const complete = { id: 0, label: '未知名称', episodes: [{ id: 0, playId: 'direct', name: '完整直连' },
    { id: 'ready', file: ref('quark', 'already-stored', { kind: 'stored' }), name: '已有文件' }] };
  assert.strictEqual(await f.data.expand(source, complete), complete);
  assert.equal(f.calls('pan', 'settings.get').length, 0);
  const fullRef = ref('quark', 'file-first', { path: '/实际剧集/第二季/第3集.mkv', name: '第3集.mkv' });
  f.handlers.pan.list = () => success({ share: { id: 'shareABCD' }, files: [
    { name: '原始名字', path: fullRef.path, size: '9007199254740992', file: fullRef },
    ...Array.from({ length: 1100 }, (_, index) => ({ name: `第${index + 4}集`, path: `/原始/第${index + 4}集.mkv`,
      file: ref('quark', String(index + 4)) })),
  ] });
  const encoded = encodePanResource({ ...share(), shareId: 'shareABCD', installationId: source.installationId, siteId: source.siteId });
  const line = { id: 0, label: '标签叫百度', episodes: [complete.episodes[0],
    { id: 'share', playId: encoded, name: '分享' }, { id: 'same-share', share: share(), name: '重复分享' }, complete.episodes[1]] };
  const expanded = await f.data.expand(source, line);
  assert.equal(expanded.episodes.length, 1103);
  assert.equal(expanded.episodes[0].playId, 'direct');
  assert.deepEqual(expanded.episodes[1].file, fullRef);
  assert.equal(expanded.episodes[1].provider, 'quark');
  assert.equal(expanded.episodes[1].path, fullRef.path);
  assert.equal(expanded.episodes[1].size, '9007199254740992');
  assert.equal(expanded.episodes.at(-1).file.fileId, 'already-stored');
  assert.equal(expanded.episodes.every((row, index) => row.index === index), true);
  assert.equal(expanded.episodes[1].playId, undefined);
  assert.equal(expanded.episodes[1].id.includes('synthetic'), false);
  assert.equal(f.calls('pan', 'list').length, 1);
  await f.data.expand(source, line); assert.equal(f.calls('pan', 'list').length, 1);
  await f.data.expand(source, expanded); assert.equal(f.calls('pan', 'list').length, 1);
  f.advance(5 * 60_000); await f.data.expand(source, line); assert.equal(f.calls('pan', 'list').length, 2);
});

test('E27 manual pan shares have no fabricated installation, and anonymous yidong revision zero is valid', async t => {
  const f = fixture(t);
  const row = f.accountRows.find(item => item.provider === 'yidong'); row.revision = 0; row.configured = false;
  f.handlers.pan.list = () => success({ files: [{ name: '真实集', path: '/真实集.mkv',
    file: ref('yidong', 'moving', { accountRevision: 0, contentId: 'moving', coID: 'coid' }) }] });
  const manual = { engine: 'pan', key: 'manual:1', detailId: 0, name: '人工分享' };
  const expanded = await f.data.expand(manual, { id: 0, episodes: [{ id: 0, name: '人工', share: share('yidong') }] });
  const selected = candidate(expanded.episodes[0].file, { source: manual });
  const descriptor = await f.data.resolve(selected, { mode: 'streaming' });
  assert.equal(descriptor.scope.sourceKey, 'manual:1');
  assert.equal(descriptor.scope.sourceEngine, undefined); assert.equal(descriptor.scope.sourceInstallationId, undefined);
  assert.equal(f.calls('pan', 'play')[0].params.mode, 'streaming');
  assert.equal(f.calls('pan', 'prepare').length, 0); assert.equal(f.calls('cat', 'invoke').length, 0);
});

test('E27 wrong pan references and old account revisions fail before caching complete lists', async t => {
  const f = fixture(t);
  const line = { id: 'share', episodes: [{ id: 'share', name: '', share: share() }] };
  f.handlers.pan.list = () => success({ files: [{ name: 'x', path: '/x', file: ref('uc') }] });
  await assert.rejects(f.data.expand(source, line), { code: 'UPSTREAM_FORMAT' });
  f.handlers.pan.list = () => success({ files: [{ name: 'x', path: '/x', file: ref('quark', 'valid') }] });
  assert.equal((await f.data.expand(source, line)).episodes[0].file.fileId, 'valid');
  assert.equal(f.calls('pan', 'list').length, 2);
  await assert.rejects(f.data.expand(source, { id: 'wrong', episodes: [{ name: 'x', share: share('quark',
    { installationId: 'another', siteId: 'another' }) }] }), { code: 'STALE_SOURCE' });
  f.accountRows.find(row => row.provider === 'quark').revision++;
  await assert.rejects(f.data.resolve(candidate()), { code: 'STALE_ACCOUNT' });
  assert.equal(f.calls('pan', 'prepare').length, 0);
});

test('E28/E29 only selected file is prepared once; cache-hit, address expiry and explicit refresh reuse exact stored file', async t => {
  const f = fixture(t);
  const selected = candidate();
  const descriptor = await f.data.resolve(selected);
  const hit = await f.data.resolve(selected);
  assert.equal(f.calls('pan', 'prepare').length, 1); assert.equal(f.calls('pan', 'play').length, 1);
  assert.equal(hit.resolvedAt, descriptor.resolvedAt); assert.equal(hit.expiresAt, descriptor.expiresAt);
  assert.equal(descriptor.resolvedFile.fileId, 'stored-file-1');
  assert.equal(f.calls('pan', 'prepare')[0].params.file.fileId, 'file-1');
  f.advance(ADDRESS); const fresh = await f.data.resolve(selected);
  assert.notEqual(fresh.url, descriptor.url); assert.equal(f.calls('pan', 'prepare').length, 1);
  await f.data.refresh(selected);
  assert.equal(f.calls('pan', 'play').length, 3); assert.equal(f.calls('pan', 'prepare').length, 1);
  const other = candidate(ref('quark', 'other-file'));
  await f.data.resolve(other); await f.data.refresh(selected); await f.data.resolve(other);
  assert.equal(f.calls('pan', 'prepare').length, 2);
  assert.equal(f.calls('pan', 'play').filter(row => row.params.file.fileId === 'stored-other-file').length, 1);
});

test('CR-06 E28/E29 refreshed list tokens reuse the same valid stored file without trusting caller identity', async t => {
  const f = fixture(t);
  const line = { id: 'share-line', episodes: [{ id: 'share', share: share(), name: '真实剧集' }] };
  const first = await f.data.expand(source, line);
  const selected = candidate(first.episodes[0].file, { fileIdentity: 'caller-first' });
  const prepared = await f.data.resolve(selected);
  assert.deepEqual(f.calls('pan', 'prepare')[0].params.file, first.episodes[0].file);
  f.advance(5 * 60_000);
  f.handlers.pan.list = () => success({ files: [{ name: 'S01E01.mkv', path: '/原始目录/第1季/S01E01.mkv',
    file: ref('quark', 'file-1', { stoken: 'synthetic-refreshed-token', fileToken: 'synthetic-refreshed-file-token' }) }] });
  const second = await f.data.expand(source, line);
  assert.notEqual(first.episodes[0].file.stoken, second.episodes[0].file.stoken);
  const refreshedCandidate = candidate(second.episodes[0].file, { id: 'new-row', fileIdentity: 'caller-different' });
  const refreshed = await f.data.resolve(refreshedCandidate);
  assert.equal(f.calls('pan', 'list').length, 2);
  assert.equal(f.calls('pan', 'prepare').length, 1);
  assert.equal(f.calls('pan', 'play').length, 2);
  assert.deepEqual(refreshed.resolvedFile, prepared.resolvedFile);
  assert.notEqual(refreshed.url, prepared.url);
  await f.data.refresh(refreshedCandidate);
  assert.equal(f.calls('pan', 'play').length, 3);
  assert.equal(f.calls('pan', 'prepare').length, 1);
});

test('CR-06 E28 concurrent token variants coalesce the same exact prepare producer', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  f.handlers.pan.prepare = async ({ file }) => {
    entered.resolve(); await gate.promise;
    return success({ file: { ...file, kind: 'stored', fileId: 'exact-stored' } });
  };
  const first = f.data.resolve(candidate());
  await entered.promise;
  const second = f.data.resolve(candidate(ref('quark', 'file-1', {
    stoken: 'synthetic-other-token', fileToken: 'synthetic-other-file-token',
  })));
  await tick();
  const requestsWhilePending = f.calls('pan', 'prepare').length;
  gate.resolve();
  const [one, two] = await Promise.all([first, second]);
  assert.equal(requestsWhilePending, 1);
  assert.equal(f.calls('pan', 'prepare').length, 1);
  assert.equal(f.calls('pan', 'play').length, 1);
  assert.deepEqual(one.resolvedFile, two.resolvedFile);
});

test('CR-06 E28 stable prepare identity still isolates exact file/share/provider/source/account and revocation', async t => {
  const f = fixture(t);
  let transfers = 0;
  f.handlers.pan.prepare = ({ file }) => success({ file: { ...file, kind: 'stored', fileId: `stored-exact-${++transfers}` } });
  const forged = { id: 'caller-same', fileIdentity: 'caller-same' };
  const selected = candidate(ref(), forged);
  const first = await f.data.resolve(selected);
  for (const item of [
    candidate(ref('quark', 'different-file'), forged),
    candidate(ref('quark', 'file-1', { shareId: 'different-share' }), forged),
    candidate(ref('uc'), forged),
    candidate(ref(), { ...forged, source: { key: 'manual:other', engine: 'pan', detailId: 'other' } }),
  ]) {
    const descriptor = await f.data.resolve(item);
    assert.notEqual(descriptor.resolvedFile.fileId, first.resolvedFile.fileId);
  }
  assert.equal(f.calls('pan', 'prepare').length, 5);
  assert.deepEqual((await f.data.resolve(selected)).resolvedFile, first.resolvedFile);
  assert.equal(f.calls('pan', 'prepare').length, 5);
  f.accountRows.find(row => row.provider === 'quark').revision = 3;
  await assert.rejects(f.data.resolve(selected), { code: 'STALE_ACCOUNT' });
  const current = candidate(ref('quark', 'file-1', { accountRevision: 3 }), forged);
  const replaced = await f.data.resolve(current);
  assert.notEqual(replaced.resolvedFile.fileId, first.resolvedFile.fileId);
  assert.equal(f.calls('pan', 'prepare').length, 6);
  f.instances.get(source.key).generation++;
  const nextSource = candidate(current.file, { ...forged, source: { ...current.source, generation: source.generation + 1 } });
  await f.data.resolve(nextSource);
  assert.equal(f.calls('pan', 'prepare').length, 7);
  await f.data.invalidate('quark');
  await f.data.resolve(nextSource);
  assert.equal(f.calls('pan', 'prepare').length, 8);
  await f.data.invalidate({ kind: 'source', engine: 'cat', installationId: source.installationId });
  await f.data.resolve(nextSource);
  assert.equal(f.calls('pan', 'prepare').length, 9);
});

test('CR-06 E28/E29 a token change does not extend the original stored lifetime', async t => {
  const f = fixture(t);
  await f.data.resolve(candidate());
  f.advance(STABLE - 1);
  const refreshed = candidate(ref('quark', 'file-1', {
    stoken: 'synthetic-refreshed-token', fileToken: 'synthetic-refreshed-file-token',
  }));
  await f.data.resolve(refreshed);
  assert.equal(f.calls('pan', 'prepare').length, 1);
  f.advance(1);
  await f.data.resolve(refreshed);
  assert.equal(f.calls('pan', 'prepare').length, 2);
});

test('CR-06 E23/E29 an old-account refresh cannot revoke the same file in the current account revision', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  const old = candidate();
  await f.data.resolve(old);
  f.accountRows.find(row => row.provider === 'quark').revision = 3;
  f.handlers.pan.play = async ({ file, mode }, signal) => {
    entered.resolve(signal); await gate.promise;
    return success({ url: 'https://media.invalid/current-account.mp4', headers: {}, file, mode });
  };
  const current = f.data.resolve(candidate(ref('quark', 'file-1', { accountRevision: 3 })))
    .then(value => ({ value }), error => ({ error }));
  const signal = await entered.promise;
  const oldFailureWork = f.data.refresh(old).then(() => null, error => error.code);
  await tick();
  const revoked = signal.aborted;
  gate.resolve();
  const [result, oldFailure] = await Promise.all([current, oldFailureWork]);
  assert.equal(oldFailure, 'STALE_ACCOUNT');
  assert.equal(revoked, false);
  assert.equal(result.error, undefined);
  assert.equal(result.value.scope.fileAccountRevision, 3);
  assert.equal(result.value.url, 'https://media.invalid/current-account.mp4');
});

test('E28 valid same-account proof enables TV first for exactly the requested mode and ordinary fallback', async t => {
  const f = fixture(t);
  f.accountRows.find(row => row.provider === 'quark_tv').configured = true;
  const descriptor = await f.data.resolve(candidate(), { mode: 'download' });
  assert.equal(f.calls('pan', 'account.match').length, 1);
  assert.deepEqual(f.calls('pan', 'account.match')[0].params, { provider: 'quark_tv', revision: 2, baseRevision: 2 });
  assert.equal(descriptor.scope.accountProvider, 'quark_tv');
  assert.equal(descriptor.scope.fileProvider, 'quark');
  assert.deepEqual(f.calls('pan', 'play').map(row => [row.params.provider, row.params.mode]), [['quark_tv', 'download']]);
  f.handlers.pan.play = params => params.provider === 'quark_tv' ? failure('UPSTREAM_HTTP')
    : success({ url: 'https://media.invalid/ordinary.mp4', headers: {}, mode: params.mode, file: params.file });
  await f.data.refresh(candidate(), { mode: 'streaming' });
  assert.deepEqual(f.calls('pan', 'play').slice(-2).map(row => [row.params.provider, row.params.mode]),
    [['quark_tv', 'streaming'], ['quark', 'streaming']]);
  assert.equal(f.calls('pan', 'prepare').length, 1);
});

test('E28 configured TV is not identity proof; mismatched/failed proof never invokes it', async t => {
  const f = fixture(t);
  f.accountRows.find(row => row.provider === 'uc_tv').configured = true;
  f.handlers.pan['account.match'] = ({ provider, revision, baseRevision }) => success({ provider, revision,
    baseRevision, baseProvider: 'uc', sameAccount: false });
  await f.data.resolve(candidate(ref('uc')));
  assert.deepEqual(f.calls('pan', 'play').map(row => row.params.provider), ['uc']);
  await f.data.invalidate('uc_tv');
  f.handlers.pan['account.match'] = () => failure('AUTH_FAILED');
  await f.data.refresh(candidate(ref('uc')));
  assert.equal(f.calls('pan', 'play').every(row => row.params.provider === 'uc'), true);
  await f.data.invalidate('uc_tv');
  f.handlers.pan['account.match'] = params => success({ ...params, baseProvider: 'uc', sameAccount: true, revision: 999 });
  await f.data.refresh(candidate(ref('uc')));
  assert.equal(f.calls('pan', 'play').every(row => row.params.provider === 'uc'), true);
});

test('E28 direct TV-owned stored files do not prepare or borrow ordinary files; yidong defaults to download only', async t => {
  const f = fixture(t);
  f.accountRows.find(row => row.provider === 'quark_tv').configured = true;
  await f.data.resolve(candidate(ref('quark_tv', 'owned', { kind: 'stored' })));
  assert.equal(f.calls('pan', 'account.match').length, 0); assert.equal(f.calls('pan', 'prepare').length, 0);
  await f.data.resolve(candidate(ref('yidong', 'moving', { contentId: 'moving', coID: 'coid' })));
  assert.deepEqual(f.calls('pan', 'play').map(row => [row.params.provider, row.params.mode]),
    [['quark_tv', 'streaming'], ['yidong', 'download']]);
  await assert.rejects(async () => f.data.resolve(candidate(), { mode: 'invented' }), { code: 'INVALID_REQUEST' });
  assert.equal(f.calls('pan', 'prepare').length, 0);
});

test('E29 upstream URL expiration is honored, and malformed/empty successes are retried rather than retained', async t => {
  const f = fixture(t);
  f.handlers.pan.play = ({ mode }) => success({ url: 'https://media.invalid/short.mp4', mode, headers: {}, expiresAt: f.now() + 1000 });
  const selected = candidate(ref('tianyi'));
  const descriptor = await f.data.resolve(selected);
  assert.equal(descriptor.expiresAt, f.now() + 1000);
  f.advance(1000); await f.data.resolve(selected); assert.equal(f.calls('pan', 'play').length, 2);
  await f.data.refresh(selected);
  f.handlers.pan.play = () => success({ url: 'https://media.invalid/wrong.mp4', headers: {} });
  await assert.rejects(f.data.refresh(selected), { code: 'UPSTREAM_FORMAT' });
  f.handlers.pan.play = ({ mode }) => success({ url: 'https://media.invalid/fixed.mp4', mode, headers: {} });
  assert.equal((await f.data.resolve(selected)).url, 'https://media.invalid/fixed.mp4');
  f.handlers.cat.invoke = () => success({});
  await assert.rejects(f.data.resolve(rawCandidate()), { code: 'MEDIA_UNAVAILABLE' });
  f.handlers.cat.invoke = () => success({ url: 'https://media.invalid/fixed-native.mp4', type: 'hls' });
  assert.equal((await f.data.resolve(rawCandidate())).format, 'hls');
});

test('E03 exact TMDB /3 paths, append/query/imageSize and original metadata shape are consumed', async t => {
  const f = fixture(t);
  const value = await f.data.targetMetadata(movieTarget);
  assert.deepEqual(value, movieValue());
  assert.deepEqual(f.calls('tmdb', 'request')[0].params, { path: '/3/movie/42', imageSize: 'original',
    query: { language: 'zh-CN', append_to_response: 'credits,similar,images' } });
  await f.data.seasonMetadata(tvTarget, 0);
  assert.deepEqual(f.calls('tmdb', 'request').at(-1).params, { path: '/3/tv/43/season/0', imageSize: 'original', query: { language: 'zh-CN' } });
  await f.data.metadata('tmdb', '/3/movie/42', {}, { imageSize: 'w500' });
  await f.data.metadata('tmdb', '/3/movie/42', {}, { imageSize: 'w780' });
  assert.equal(f.calls('tmdb', 'request').length, 5);
  assert.deepEqual(f.calls('tmdb', 'request')[1].params, { path: '/3/tv/43',
    imageSize: 'original', query: { language: 'zh-CN' } });
  await assert.rejects(async () => f.data.metadata('tmdb', '/movie/42'), { code: 'INVALID_REQUEST' });
  await assert.rejects(async () => f.data.metadata('tmdb', '/3/movie/42', { page: [] }), { code: 'INVALID_REQUEST' });
});

test('E03 Douban search/detail preserve subjects.items/target and independent identity, not TMDB wrappers', async t => {
  const f = fixture(t);
  const raw = { subjects: { items: [{ target_id: '51', target_type: 'movie', target: doubanValue() }] }, total: 1 };
  f.handlers.douban.search = () => success(raw);
  const found = await f.data.metadata('douban', 'search', { query: '真实电影' });
  assert.deepEqual(found, raw); assert.equal(found.results, undefined);
  assert.deepEqual(f.calls('douban', 'search')[0].params, { query: '真实电影' });
  const target = { ...movieTarget, metadataProvider: 'douban', metadataId: '51' };
  assert.deepEqual(await f.data.targetMetadata(target), doubanValue());
  assert.equal(f.calls('douban', 'request')[0].params.path, '/rexxar/api/v2/movie/51');
});

test('E25 stable movie/EndedTV expire at 14 days, metadata search at 6h and unknown ongoing TV at 24h', async t => {
  const f = fixture(t);
  await f.data.targetMetadata(movieTarget); await f.data.targetMetadata(tvTarget);
  f.advance(STABLE - 1); await f.data.targetMetadata(movieTarget); await f.data.targetMetadata(tvTarget);
  assert.equal(f.calls('tmdb', 'request').length, 2);
  f.advance(1); await f.data.targetMetadata(movieTarget); await f.data.targetMetadata(tvTarget);
  assert.equal(f.calls('tmdb', 'request').length, 4);
  await f.data.metadata('tmdb', '/3/search/movie', { query: '真实电影' });
  f.advance(6 * HOUR); await f.data.metadata('tmdb', '/3/search/movie', { query: '真实电影' });
  assert.equal(f.calls('tmdb', 'request').length, 6);
  f.handlers.tmdb.request = () => success(tvValue(44, { status: 'Returning Series', next_episode_to_air: null }));
  const ongoing = { ...tvTarget, metadataId: 44 };
  await f.data.targetMetadata(ongoing); f.advance(DAY - 1); await f.data.targetMetadata(ongoing);
  assert.equal(f.calls('tmdb', 'request').length, 7);
  f.advance(1); await f.data.targetMetadata(ongoing); assert.equal(f.calls('tmdb', 'request').length, 8);
});

test('E25 CN broadcast midnight refreshes once per day, not immediately on the same schedule', async t => {
  const f = fixture(t);
  const boundary = Date.parse('2026-11-09T00:00:00+08:00');
  f.handlers.tmdb.request = () => success(tvValue(43, { status: 'Returning Series',
    next_episode_to_air: { season_number: 1, episode_number: 2, air_date: '2026-11-09' } }));
  await f.data.targetMetadata(tvTarget);
  f.setTime(boundary - 1); await f.data.targetMetadata(tvTarget);
  assert.equal(f.calls('tmdb', 'request').length, 1);
  f.setTime(boundary); await f.data.targetMetadata(tvTarget); await f.data.targetMetadata(tvTarget);
  assert.equal(f.calls('tmdb', 'request').length, 2);
  f.advance(DAY - 1); await f.data.targetMetadata(tvTarget); assert.equal(f.calls('tmdb', 'request').length, 2);
  f.advance(1); await f.data.targetMetadata(tvTarget); assert.equal(f.calls('tmdb', 'request').length, 3);
});

test('E25 season expiration uses real air dates/episode numbers, including unsorted days and undated tail', async t => {
  const f = fixture(t);
  const boundary = Date.parse('2026-11-09T00:00:00+08:00');
  const parent = tvValue(43, { seasons: [2, 3, 4].map(season_number => ({
    season_number, episode_count: season_number === 2 ? 10 : 9,
  })) });
  let season = { season_number: 2, episodes: [
    { episode_number: 10, air_date: '2026-11-12' }, { episode_number: 3, air_date: '2026-11-09' },
    { episode_number: 1, air_date: '2026-01-01' },
  ] };
  f.handlers.tmdb.request = ({ path }) => success(path === '/3/tv/43' ? parent : season);
  const seasonCalls = () => f.calls('tmdb', 'request').filter(row => row.params.path.includes('/season/'));
  await f.data.seasonMetadata(tvTarget, 2); f.setTime(boundary - 1); await f.data.seasonMetadata(tvTarget, 2);
  assert.equal(seasonCalls().length, 1);
  f.setTime(boundary); await f.data.seasonMetadata(tvTarget, 2); await f.data.seasonMetadata(tvTarget, 2);
  assert.equal(seasonCalls().length, 2);
  season = { season_number: 3, episodes: [
    { episode_number: 9, air_date: null }, { episode_number: 2, air_date: '2025-01-01' },
  ] };
  await f.data.seasonMetadata(tvTarget, 3); f.advance(DAY); await f.data.seasonMetadata(tvTarget, 3);
  assert.equal(seasonCalls().length, 4);
  season = { season_number: 4, episodes: [
    { episode_number: 9, air_date: '2025-01-01' },
    ...Array.from({ length: 8 }, (_, index) => ({ episode_number: index + 1, air_date: null })),
  ] };
  await f.data.seasonMetadata(tvTarget, 4); f.advance(DAY); await f.data.seasonMetadata(tvTarget, 4);
  assert.equal(seasonCalls().length, 5);
  assert.equal(f.calls('tmdb', 'request').filter(row => row.params.path === '/3/tv/43').length, 1);
});

test('E24 metadata keys use opaque configuration revision and image size, not configured booleans', async t => {
  const f = fixture(t);
  await f.data.targetMetadata(movieTarget); await f.data.targetMetadata(movieTarget);
  assert.equal(f.calls('tmdb', 'request').length, 1);
  f.revisions.tmdb = 'opaque-credential-replacement';
  await f.data.targetMetadata(movieTarget); assert.equal(f.calls('tmdb', 'request').length, 2);
  await f.data.targetMetadata(movieTarget); assert.equal(f.calls('tmdb', 'request').length, 2);
  await f.data.invalidate('tmdb'); await f.data.targetMetadata(movieTarget);
  assert.equal(f.calls('tmdb', 'request').length, 3);
  assert.equal(f.calls('tmdb', 'request').some(row => JSON.stringify(row.params).includes('opaque-')), false);
});

test('E25 empty/malformed metadata success cannot be cached, partial detail cannot skip the complete request', async t => {
  const f = fixture(t);
  f.handlers.tmdb.request = () => success({ id: 42, title: '只有搜索卡片' });
  await assert.rejects(f.data.targetMetadata(movieTarget), { code: 'UPSTREAM_FORMAT' });
  f.handlers.tmdb.request = () => success(movieValue());
  f.advance(1);
  const req = requestFor(movieTarget);
  const partial = { ...req, value: { id: 42, title: '真实电影' }, completeness: 'partial',
    fetchedAt: f.now(), settings: { revision: f.revisions.tmdb } };
  assert.equal((await f.data.seed(partial)).title, '真实电影');
  const full = await f.data.targetMetadata(movieTarget);
  assert.equal(full.runtime, 120);
  const exported = await f.data.cachedEntry(req);
  assert.equal(exported.completeness, 'complete');
  assert.equal(await f.data.seed({ ...partial, fetchedAt: f.now() }), undefined);
  assert.equal((await f.data.targetMetadata(movieTarget)).runtime, 120);
  f.handlers.douban.search = () => success({});
  await assert.rejects(f.data.metadata('douban', 'search', { query: 'empty' }), { code: 'UPSTREAM_FORMAT' });
  f.handlers.douban.search = () => success({ subjects: { items: [] } });
  assert.deepEqual(await f.data.metadata('douban', 'search', { query: 'empty' }), { subjects: { items: [] } });
});

test('E49 original complete search/detail/metadata handoff avoids upstream content requests and preserves original timestamps', async t => {
  const f = fixture(t);
  const fetchedAt = f.now() - 10 * 60_000;
  const entries = [
    { kind: 'search', source, query: '真实电影', page: 1, value: { list: [{ vod_id: 0, vod_name: '真实电影' }], pagecount: 1 },
      completeness: 'complete', fetchedAt, expiresAt: fetchedAt + HOUR },
    { kind: 'detail', source, detailId: 0, value: detailValue(0), completeness: 'complete', fetchedAt },
    { ...requestFor(movieTarget), value: movieValue(), completeness: 'complete', fetchedAt,
      settings: { revision: f.revisions.tmdb } },
  ];
  assert.deepEqual(await f.data.handoff(entries), { accepted: 3, ignored: 0 });
  await f.data.search(source, '真实电影'); await f.data.detail(source, 0); await f.data.targetMetadata(movieTarget);
  assert.equal(f.calls('cat', 'invoke').length, 0); assert.equal(f.calls('tmdb', 'request').length, 0);
  const exported = await f.data.cachedEntry({ kind: 'detail', source, detailId: 0 });
  assert.equal(exported.fetchedAt, fetchedAt); assert.equal(exported.expiresAt, fetchedAt + HOUR);
  const metadataEntry = await f.data.cachedEntry(requestFor(movieTarget));
  assert.equal(metadataEntry.fetchedAt, fetchedAt); assert.equal(metadataEntry.expiresAt, fetchedAt + STABLE);
  assert.equal(metadataEntry.settings.revision, f.revisions.tmdb);
  f.advance(1000);
  assert.deepEqual(await f.data.handoff([exported, metadataEntry]), { accepted: 0, ignored: 2 });
  assert.equal((await f.data.cachedEntry(requestFor(movieTarget))).fetchedAt, fetchedAt);
});

test('E49 expired/old generation/old metadata revision handoff is ignored; caller cannot extend original business TTL', async t => {
  const f = fixture(t);
  const original = { kind: 'search', source, query: '真实电影', page: 1, value: { list: [], pagecount: 1 }, completeness: 'complete' };
  assert.equal(await f.data.seed({ ...original, fetchedAt: f.now() - HOUR }), undefined);
  assert.equal(await f.data.seed({ ...original, source: { ...source, generation: 3 }, fetchedAt: f.now() }), undefined);
  assert.equal(await f.data.seed({ ...requestFor(movieTarget), value: movieValue(), completeness: 'complete',
    fetchedAt: f.now(), settings: { revision: 'old-config' } }), undefined);
  await f.data.seed({ ...original, fetchedAt: f.now() - HOUR + 500, expiresAt: f.now() + 100 * DAY });
  const handedOff = await f.data.cachedEntry(original);
  assert.equal(handedOff.expiresAt, f.now() + 500);
  f.advance(500); await f.data.search(source, '真实电影'); assert.equal(f.calls('cat', 'invoke').length, 1);
  await assert.rejects(f.data.seed({ ...original, fetchedAt: f.now() + 1 }), { code: 'INVALID_REQUEST' });
});

test('E49 older handoff cannot replace a fresher complete entry even with a larger claimed expiration', async t => {
  const f = fixture(t);
  const req = { kind: 'search', source, query: 'scope', page: 1 };
  await f.data.seed({ ...req, value: { list: [{ vod_id: 'fresh' }] }, completeness: 'complete',
    fetchedAt: f.now() - 100, expiresAt: f.now() + 500 });
  assert.equal(await f.data.seed({ ...req, value: { list: [{ vod_id: 'old' }] }, completeness: 'complete',
    fetchedAt: f.now() - 1000, expiresAt: f.now() + 10000 }), undefined);
  assert.equal((await f.data.search(source, 'scope')).items[0].vod_id, 'fresh');
});

test('E49 a complete seed wins over an already-running older producer without extending its timestamp', async t => {
  const f = fixture(t), gate = deferred();
  f.handlers.cat.invoke = async () => { await gate.promise; return success({ list: [{ vod_id: 'old' }], pagecount: 1 }); };
  const oldWork = f.data.search(source, 'seeded'); await tick();
  const input = { kind: 'search', source, query: 'seeded', page: 1, value: { list: [{ vod_id: 'current' }], pagecount: 1 },
    completeness: 'complete', fetchedAt: f.now() - 100 };
  await f.data.seed(input); gate.resolve();
  assert.equal((await oldWork).items[0].vod_id, 'current');
  assert.equal((await f.data.cachedEntry(input)).fetchedAt, input.fetchedAt);
});

test('E03 metadata association accepts only unambiguous actual title/year evidence and preserves aliases', async t => {
  const f = fixture(t);
  f.handlers.tmdb.request = () => success(movieValue(42, { original_title: 'Actual Original' }));
  f.handlers.douban.search = () => success({ subjects: { items: [
    { target_id: '51', target_type: 'movie', target: doubanValue('51', { title: '另一中文名', original_title: 'Actual Original' }) },
    { target_id: '52', target_type: 'movie', target: doubanValue('52', { year: '2025' }) },
    { target_id: '53', target_type: 'movie', target: doubanValue('53', { title: '无关影片' }) },
  ] } });
  f.handlers.douban.request = () => success(doubanValue('51', { title: '另一中文名', original_title: 'Actual Original' }));
  const sources = await f.data.metadataSources(movieTarget);
  assert.deepEqual(sources.map(row => [row.provider, row.metadataId]), [['tmdb', 42], ['douban', '51']]);
  assert.equal(sources[1].association.kind, 'title-year');
  assert.equal(sources[1].metadata.original_title, 'Actual Original');
  assert.equal(f.calls('douban', 'request').length, 1);
});

test('E03 no year or ambiguous same-name counterparts never fabricate an associated provider', async t => {
  const f = fixture(t);
  f.handlers.douban.search = () => success({ subjects: { items: [
    { target_id: '51', target_type: 'movie', target: doubanValue('51') },
    { target_id: '52', target_type: 'movie', target: doubanValue('52') },
  ] } });
  assert.equal((await f.data.metadataSources(movieTarget)).length, 1);
  assert.equal(f.calls('douban', 'request').length, 0);
  await f.data.invalidate('douban');
  f.handlers.douban.search = () => success({ subjects: { items: [
    { target_id: '51', target_type: 'movie', target: { id: '51', title: '真实电影' } },
  ] } });
  assert.equal((await f.data.metadataSources(movieTarget)).length, 1);
});

test('CR-01 E04/D2 complete Douban primary maps and resolves first episode without optional searches, while association errors remain visible', async t => {
  const f = fixture(t);
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '51' };
  const raw = doubanValue('51', { title: '真实剧集 第1季', episodes_count: '2' });
  f.handlers.douban.request = () => success(raw);
  f.handlers.tmdb.request = () => failure('NOT_CONFIGURED');
  f.handlers.douban.search = () => failure('UPSTREAM_HTTP');
  const primary = await f.data.targetMetadata(target);
  assert.deepEqual(primary.doubanSeasons, [{ season: 1, episodeCount: 2, metadataId: '51', metadata: raw }]);
  const first = resolveEpisode(buildEpisodeMap(primary), { globalEpisode: 1 });
  assert.deepEqual(first, { season: 1, episode: 1, globalEpisode: 1 });
  const detail = { detailId: 'detail-a',
    lines: [{ id: 'actual', episodes: [{ id: 'actual-e1', playId: 'actual-e1', name: 'S01E01.mkv' }] }] };
  const [actual] = await recognizeCandidates(detail, { target: { ...target, ...first }, source,
    metadata: primary });
  assert.deepEqual([actual.season, actual.episode, actual.globalEpisode], [1, 1, 1]);
  assert.equal((await f.data.resolve(actual)).url, 'https://media.invalid/raw.mp4');
  const season = await f.data.seasonMetadata(target, 1);
  assert.deepEqual([season.season, season.episodeCount], [1, 2]);
  assert.equal(f.calls('douban', 'search').length, 0);
  assert.equal(f.calls('tmdb', 'request').length, 0);
  await assert.rejects(f.data.metadataSources(target, { metadata: primary }), { code: 'UPSTREAM_HTTP' });
  f.handlers.douban.search = () => success({ subjects: { items: [] } });
  await assert.rejects(f.data.metadataSources(target, { metadata: primary }), { code: 'NOT_CONFIGURED' });
  assert.deepEqual(resolveEpisode(buildEpisodeMap(await f.data.targetMetadata(target)), { globalEpisode: 1 }), first);
  assert.equal(f.calls('douban', 'request').length, 1);
});

test('CR-01 E04/E25 primary projection leaves cached raw/timestamps unchanged and honors explicit rules on each consumer', async t => {
  const f = fixture(t);
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '51' };
  const raw = doubanValue('51', { title: '真实剧集 第1季', episodes_count: 2 });
  f.handlers.douban.request = () => success(raw);
  const first = await f.data.targetMetadata(target);
  const before = await f.data.cachedEntry(requestFor(target));
  assert.deepEqual(before.value, raw);
  assert.equal(Object.hasOwn(before.value, 'doubanSeasons'), false);
  first.doubanSeasons[0].episodeCount = 99;
  f.advance(HOUR);
  assert.equal((await f.data.targetMetadata(target)).doubanSeasons[0].episodeCount, 2);
  const rules = { episodeCleanRegexRules: ['第1季'] };
  assert.equal((await f.data.targetMetadata(target, { rules })).doubanSeasons, undefined);
  f.handlers.douban.search = () => failure('UPSTREAM_HTTP');
  await assert.rejects(f.data.seasonMetadata(target, 1, { rules }), { code: 'UPSTREAM_HTTP' });
  assert.equal((await f.data.seasonMetadata(target, 1)).episodeCount, 2);
  assert.deepEqual(await f.data.cachedEntry(requestFor(target)), before);
  assert.equal(f.calls('douban', 'request').length, 1);
});

test('CR-01 E04/D2 absent primary evidence and caller coordinates never fabricate a season or count', async t => {
  const f = fixture(t);
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '51', season: 1, episode: 1 };
  for (const patch of [
    { title: '真实剧集', episodes_count: 2 },
    { title: '真实剧集 第1季', episodes_count: null },
    { title: '真实剧集 第1季' },
    { title: '真实剧集 第1季', episodes_count: -1 },
  ]) {
    await f.data.invalidate('douban');
    const raw = doubanValue('51', patch);
    f.handlers.douban.request = () => success(raw);
    assert.deepEqual(await f.data.targetMetadata(target), raw);
  }
  await f.data.invalidate('douban');
  f.handlers.douban.request = () => success(doubanValue('51', { title: '真实剧集 第2季', episodes_count: 3 }));
  const later = await f.data.targetMetadata(target);
  assert.deepEqual(buildEpisodeMap(later).map(row => [row.season, row.episode]), [[2, 1], [2, 2], [2, 3]]);
  assert.equal(resolveEpisode(buildEpisodeMap(later), { globalEpisode: 1 }), null);
  await f.data.invalidate('douban');
  f.handlers.douban.request = () => success(doubanValue('51', { title: '真实剧集 特别篇', season_number: 0, episodes_count: 1 }));
  const special = await f.data.targetMetadata(target);
  assert.deepEqual(buildEpisodeMap(special), [{ season: 0, episode: 1 }]);
  assert.equal(f.calls('douban', 'search').length, 0);
  assert.equal(f.calls('tmdb', 'request').length, 0);
});

test('E03 independent Douban seasons use explicit real season markers/counts without a ten-season truncation', async t => {
  const f = fixture(t);
  f.modules.delete('tmdb');
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '101' };
  f.handlers.douban.request = ({ path }) => {
    const id = path.split('/').at(-1), season = Number(id) - 100;
    return success(doubanValue(id, { title: `真实剧集 第${season}季`, year: String(2023 + season), episodes_count: season + 1 }));
  };
  f.handlers.douban.search = () => success({ subjects: { items: [
    ...Array.from({ length: 12 }, (_, index) => ({ target_id: String(index + 101), target_type: 'tv',
      target: { id: String(index + 101), title: `真实剧集 第${index + 1}季`, year: String(2024 + index) } })),
    { target_id: '999', target_type: 'tv', target: { id: '999', title: '另一个剧 第2季', year: '2025' } },
  ] } });
  const sources = await f.data.metadataSources(target);
  const rows = sources.find(row => row.provider === 'douban').seasons;
  assert.equal(rows.length, 12); assert.equal(rows.at(-1).season, 12); assert.equal(rows.at(-1).episodeCount, 13);
  assert.equal(sources.every(row => row.provider === 'douban'), true);
  const season = await f.data.seasonMetadata(target, 12);
  assert.equal(season.id, '112'); assert.equal(season.season, 12); assert.equal(season.episodeCount, 13);
  assert.equal(f.calls('douban', 'request').some(row => row.params.path.endsWith('/999')), false);
});

test('E03 primary season evidence survives missing opposite owner, but absent season/count never fabricates season one or zero count', async t => {
  const f = fixture(t);
  const tmdb = f.modules.get('tmdb');
  f.modules.delete('tmdb');
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '51' };
  f.handlers.douban.request = () => success(doubanValue('51', { title: '真实剧集 第二季', episodes_count: 8 }));
  assert.deepEqual((await f.data.metadataSources(target))[0].seasons.map(row => [row.season, row.episodeCount]), [[2, 8]]);
  await f.data.invalidate('douban');
  f.handlers.douban.request = () => success(doubanValue('51', { title: '真实剧集', episodes_count: 8 }));
  assert.equal((await f.data.metadataSources(target))[0].seasons, undefined);
  await f.data.invalidate('douban');
  f.handlers.douban.request = () => success(doubanValue('51', { title: '真实剧集 第1季', episodes_count: null }));
  assert.equal((await f.data.metadataSources(target))[0].seasons, undefined);
  f.modules.set('tmdb', tmdb);
  f.modules.delete('douban');
  assert.deepEqual((await f.data.metadataSources(tvTarget))[0].seasons, tvValue().seasons);
});

test('E03 Douban association rejects first-season year conflicts and ambiguous season identities while admitting earlier seasons of a known later primary', async t => {
  const f = fixture(t);
  f.modules.delete('tmdb');
  const values = {
    '101': doubanValue('101', { title: '真实剧集 第1季', year: '2024', episodes_count: 2 }),
    '102': doubanValue('102', { title: '真实剧集 第2季', year: '2025', episodes_count: 3 }),
    '202': doubanValue('202', { title: '真实剧集 第2季', year: '2025', episodes_count: 3 }),
    '103': doubanValue('103', { title: '真实剧集 第3季', year: '2026', episodes_count: 4 }),
    '201': doubanValue('201', { title: '真实剧集 第1季', year: '2010', episodes_count: 2 }),
    '301': doubanValue('301', { title: '真实剧集', year: '2025', episodes_count: 2 }),
  };
  f.handlers.douban.request = ({ path }) => success(values[path.split('/').at(-1)]);
  f.handlers.douban.search = () => success({ subjects: { items: Object.entries(values).map(([id, value]) =>
    ({ target_id: id, target_type: 'tv', target: value })) } });
  const primary = { ...tvTarget, metadataProvider: 'douban', metadataId: '101' };
  const sources = await f.data.metadataSources(primary);
  assert.deepEqual(sources[0].seasons.map(row => row.season), [1, 3]);
  assert.deepEqual(f.calls('douban', 'request').map(row => row.params.path.split('/').at(-1)), ['101', '103']);
  await f.data.invalidate('douban');
  f.handlers.douban.search = () => success({ subjects: { items: ['101', '102'].map(id =>
    ({ target_id: id, target_type: 'tv', target: values[id] })) } });
  assert.deepEqual((await f.data.metadataSources({ ...primary, metadataId: '102' }))[0].seasons
    .map(row => [row.season, row.metadataId]), [[1, '101'], [2, '102']]);
});

test('E03 a confirmed detail season that conflicts with its search record cannot become a fabricated season map', async t => {
  const f = fixture(t);
  f.modules.delete('tmdb');
  const target = { ...tvTarget, metadataProvider: 'douban', metadataId: '101' };
  f.handlers.douban.search = () => success({ subjects: { items: [
    { target_id: '102', target_type: 'tv', target: { id: '102', title: '真实剧集 第2季', year: '2025' } },
  ] } });
  f.handlers.douban.request = ({ path }) => success(doubanValue(path.split('/').at(-1), {
    title: path.endsWith('/101') ? '真实剧集 第1季' : '真实剧集',
    season_number: path.endsWith('/101') ? 1 : 3, episodes_count: 8,
  }));
  await assert.rejects(f.data.metadataSources(target), { code: 'UPSTREAM_FORMAT' });
});

test('E03 cross-provider search consumes later TMDB pages and accepts ordinary overlap, but rejects repeating unending pages', async t => {
  const f = fixture(t);
  const target = { ...movieTarget, metadataProvider: 'douban', metadataId: '51' };
  f.handlers.tmdb.request = ({ path, query }) => path.startsWith('/3/search/') ? success({ page: query.page, total_pages: 2,
    results: query.page === 1 ? [movieValue(11, { title: '其他', release_date: '2020-01-01' })]
      : [movieValue(11, { title: '其他', release_date: '2020-01-01' }), movieValue(42)] }) : success(movieValue());
  assert.deepEqual((await f.data.metadataSources(target)).map(row => row.provider), ['douban', 'tmdb']);
  assert.deepEqual(f.calls('tmdb', 'request').filter(row => row.params.path.startsWith('/3/search/')).map(row => row.params.query.page), [1, 2]);
  await f.data.invalidate('tmdb');
  f.handlers.tmdb.request = () => success({ page: 1, total_pages: 100,
    results: [movieValue(11, { title: '其他', release_date: '2020-01-01' })] });
  await assert.rejects(f.data.metadataSources(target), { code: 'UPSTREAM_FORMAT' });
});

test('E47 Cat string/source JSON report action keeps exact flag/id/session and requires inner ok true', async t => {
  const f = fixture(t);
  const payload = { sessionId: 'play-session', positionSeconds: 12.5, durationSeconds: 100, event: 'pause' };
  f.handlers.cat.invoke = () => success(JSON.stringify({ ok: true, accepted: 1 }));
  assert.deepEqual(await f.data.report(rawCandidate(source, { flag: 0, playId: 0 }), payload), { ok: true, accepted: 1 });
  assert.deepEqual(f.calls('cat', 'invoke')[0].params.payload, { flag: 0, id: 0, ...payload });
  assert.equal(f.calls('cat', 'invoke')[0].params.operation, 'report');
  await f.data.report(rawCandidate(portable), payload);
  assert.equal(f.calls('source', 'invoke')[0].params.action, 'report');
  f.handlers.cat.invoke = () => success({ ok: false });
  await assert.rejects(f.data.report(rawCandidate(), payload), { code: 'UPSTREAM_ERROR' });
  f.handlers.cat.invoke = () => success({ reportUrl: 'https://not-called.invalid/' });
  await assert.rejects(f.data.report(rawCandidate(), payload), { code: 'UPSTREAM_ERROR' });
  assert.equal(f.calls('pan', 'play').length, 0);
});

test('E23/E48 source invalidation aborts direct play/report synchronously and awaits real pending completion', async t => {
  const f = fixture(t), gate = deferred(), entered = deferred();
  f.handlers.cat.invoke = async (_params, signal) => { entered.resolve(signal); await gate.promise;
    return success({ url: 'https://media.invalid/late.mp4', ok: true }); };
  const play = f.data.resolve(rawCandidate()); const rejected = assert.rejects(play, { code: 'CANCELLED' });
  const signal = await entered.promise;
  let drained = false;
  const closing = f.data.invalidate({ kind: 'source', engine: 'cat', installationId: source.installationId }).then(() => { drained = true; });
  assert.equal(signal.aborted, true); await rejected; await tick(); assert.equal(drained, false);
  gate.resolve(); await closing; assert.equal(drained, true);
  f.handlers.cat.invoke = () => success({ url: 'https://media.invalid/current.mp4' });
  assert.equal((await f.data.resolve(rawCandidate())).url, 'https://media.invalid/current.mp4');
});

test('E23 exact source installation/domain invalidation leaves other engine/installation data intact', async t => {
  const f = fixture(t);
  const other = { ...source, key: 'cat:other:site', installationId: 'other' };
  f.instances.set(other.key, { ...other, state: 'ready', sites: [{ key: other.siteId }] });
  await f.data.search(source, 'scoped'); await f.data.search(other, 'scoped'); await f.data.search(portable, 'scoped');
  await f.data.invalidate({ kind: 'source', engine: 'cat', installationId: source.installationId });
  await f.data.search(other, 'scoped'); await f.data.search(portable, 'scoped');
  assert.equal(f.calls('cat', 'invoke').length, 2); assert.equal(f.calls('source', 'invoke').length, 1);
  await f.data.search(source, 'scoped'); assert.equal(f.calls('cat', 'invoke').length, 3);
  await f.data.invalidate(other.key); await f.data.search(other, 'scoped'); assert.equal(f.calls('cat', 'invoke').length, 4);
  await f.data.invalidate({ kind: 'source', engine: 'source' }); await f.data.search(portable, 'scoped');
  assert.equal(f.calls('source', 'invoke').length, 2);
});

test('E23 cached pan producers carry exact source installation dependencies as well as account revisions', async t => {
  const f = fixture(t), gate = deferred(), entered = deferred();
  f.handlers.pan.prepare = async ({ file }, signal) => { entered.resolve(signal); await gate.promise;
    return success({ file: { ...file, kind: 'stored', fileId: 'late-stored' } }); };
  const work = f.data.resolve(candidate()); const cancelled = assert.rejects(work, { code: 'CANCELLED' });
  const signal = await entered.promise;
  const invalidated = f.data.invalidate({ kind: 'source', engine: 'cat', installationId: source.installationId });
  assert.equal(signal.aborted, true); await cancelled; gate.resolve(); await invalidated;
  assert.equal(f.calls('pan', 'play').length, 0);
  f.handlers.pan.prepare = ({ file }) => success({ file: { ...file, kind: 'stored', fileId: 'current-stored' } });
  assert.equal((await f.data.resolve(candidate())).resolvedFile.fileId, 'current-stored');
  assert.equal(f.calls('pan', 'prepare').length, 2);
});

test('E23 account invalidation aborts signed-URL work; refreshing unchanged credentials retains stored', async t => {
  const f = fixture(t);
  await f.data.resolve(candidate());
  f.handlers.pan.play = ({ file, mode }) => success({ url: 'https://media.invalid/token-refresh.mp4', file, mode, headers: {} });
  await f.data.refresh(candidate()); assert.equal(f.calls('pan', 'prepare').length, 1);
  const gate = deferred(), entered = deferred();
  f.handlers.pan.play = async (params, signal) => { entered.resolve(signal); await gate.promise;
    return success({ url: 'https://media.invalid/old-account.mp4', ...params }); };
  const work = f.data.refresh(candidate()); const cancelled = assert.rejects(work, { code: 'CANCELLED' });
  const signal = await entered.promise;
  f.accountRows.find(row => row.provider === 'quark').revision = 3;
  const invalidated = f.data.invalidate('quark'); assert.equal(signal.aborted, true); await cancelled;
  gate.resolve(); await invalidated;
  await assert.rejects(f.data.resolve(candidate()), { code: 'STALE_ACCOUNT' });
});

test('E23 metadata settings invalidation drains the old producer and cannot publish into the new configuration', async t => {
  const f = fixture(t), entered = deferred(), gate = deferred();
  f.handlers.tmdb.request = async (_params, signal) => { entered.resolve(signal); await gate.promise;
    return success(movieValue(42, { overview: 'old revision' })); };
  const work = f.data.targetMetadata(movieTarget); const cancelled = assert.rejects(work, { code: 'CANCELLED' });
  const signal = await entered.promise;
  f.revisions.tmdb = 'new-opaque-revision';
  let drained = false;
  const invalidated = f.data.invalidate('tmdb').then(() => { drained = true; });
  assert.equal(signal.aborted, true); await cancelled; await tick(); assert.equal(drained, false);
  gate.resolve(); await invalidated;
  f.handlers.tmdb.request = () => success(movieValue(42, { overview: 'current revision' }));
  assert.equal((await f.data.targetMetadata(movieTarget)).overview, 'current revision');
});

test('DT-01 E22 stopping during source validation prevents each next content/account request', async t => {
  const operations = [
    ['search', (data, options) => data.search(source, '停止查询', 1, options)],
    ['detail', (data, options) => data.detail(source, 0, options)],
    ['expand', (data, options) => data.expand(source, { id: 0, episodes: [{ id: 0, share: share() }] }, options)],
    ['pan resolve', (data, options) => data.resolve(candidate(), options)],
    ['direct resolve', (data, options) => data.resolve(rawCandidate(), options)],
    ['report', (data, options) => data.report(rawCandidate(),
      { sessionId: 's', positionSeconds: 0, durationSeconds: 1, event: 'progress' }, options)],
  ];
  for (const [name, run] of operations) await t.test(name, async t => {
    const entered = deferred(), gate = deferred();
    t.after(() => gate.resolve());
    const f = fixture(t), status = f.handlers.cat.status;
    let active = true;
    f.handlers.cat.status = async (_params, signal) => { entered.resolve(signal); await gate.promise; return status(); };
    const rejected = assert.rejects(run(f.data, { canSchedule: () => active }), { code: 'CANCELLED' });
    const signal = await entered.promise;
    active = false; gate.resolve(); await rejected;
    assert.equal(signal.aborted, false);
    assert.deepEqual(f.observed.map(row => [row.id, row.operation]), [['cat', 'status']]);
  });
});

test('DT-01 E22 stopping or cancelling during account lookup prevents prepare/play and share lists', async t => {
  for (const stop of ['callback', 'signal']) for (const operation of ['resolve', 'expand']) {
    await t.test(`${operation}: ${stop}`, async t => {
      const entered = deferred(), gate = deferred(), consumer = new AbortController();
      t.after(() => gate.resolve());
      const f = fixture(t), accounts = f.handlers.pan['settings.get'];
      let active = true;
      f.handlers.pan['settings.get'] = async (_params, signal) => {
        entered.resolve(signal); await gate.promise; return accounts();
      };
      const options = stop === 'signal' ? { signal: consumer.signal } : { canSchedule: () => active };
      const work = operation === 'resolve' ? f.data.resolve(candidate(), options)
        : f.data.expand(source, { id: 0, episodes: [{ id: 0, share: share() }] }, options);
      const rejected = assert.rejects(work, { code: 'CANCELLED' });
      const signal = await entered.promise;
      if (stop === 'signal') consumer.abort(); else active = false;
      assert.equal(signal.aborted, false);
      gate.resolve(); await rejected; await tick();
      assert.equal(f.calls('pan', 'prepare').length, 0);
      assert.equal(f.calls('pan', 'account.match').length, 0);
      assert.equal(f.calls('pan', 'play').length, 0);
      assert.equal(f.calls('pan', 'list').length, 0);
    });
  }
});

test('DT-01 E22 an accepted prepare finishes after stop and its stored file is reused by the next explicit resolve', async t => {
  for (const stop of ['callback', 'signal']) await t.test(stop, async t => {
    const entered = deferred(), gate = deferred(), consumer = new AbortController();
    t.after(() => gate.resolve());
    const f = fixture(t), selected = candidate();
    let active = true;
    f.handlers.pan.prepare = async ({ file }, signal) => {
      entered.resolve(signal); await gate.promise;
      return success({ file: { ...file, kind: 'stored', fileId: 'accepted-stored' } });
    };
    const options = stop === 'signal' ? { signal: consumer.signal } : { canSchedule: () => active };
    const rejected = assert.rejects(f.data.resolve(selected, options), { code: 'CANCELLED' });
    const signal = await entered.promise;
    if (stop === 'signal') consumer.abort(); else active = false;
    assert.equal(signal.aborted, false);
    gate.resolve(); await rejected; await tick();
    assert.equal(f.calls('pan', 'play').length, 0);
    const descriptor = await f.data.resolve(selected);
    assert.equal(descriptor.resolvedFile.fileId, 'accepted-stored');
    assert.equal(f.calls('pan', 'prepare').length, 1);
    assert.equal(f.calls('pan', 'play').length, 1);
    assert.equal(f.calls('pan', 'play')[0].params.file.fileId, 'accepted-stored');
  });
});

test('DT-01 E22 stopping an accepted share list keeps its cache but never starts the next share', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t), list = f.handlers.pan.list;
  let active = true;
  const line = { id: 0, episodes: [{ id: 0, share: share() },
    { id: 1, share: share('quark', { url: 'https://pan.quark.cn/s/otherABCD' }) }] };
  f.handlers.pan.list = async (params, signal) => { entered.resolve(signal); await gate.promise; return list(params); };
  const rejected = assert.rejects(f.data.expand(source, line, { canSchedule: () => active }), { code: 'CANCELLED' });
  const signal = await entered.promise;
  active = false; gate.resolve(); await rejected;
  assert.equal(signal.aborted, false);
  assert.equal(f.calls('pan', 'list').length, 1);
  await f.data.expand(source, line);
  assert.deepEqual(f.calls('pan', 'list').map(row => row.params.share.url),
    ['https://pan.quark.cn/s/shareABCD', 'https://pan.quark.cn/s/otherABCD']);
});

test('DT-01 E22 metadata methods stop between settings and the requested content', async t => {
  const operations = [
    ['metadata', (data, options) => data.metadata('tmdb', '/3/movie/42', {}, options)],
    ['target', (data, options) => data.targetMetadata(movieTarget, options)],
    ['season', (data, options) => data.seasonMetadata(tvTarget, 1, options)],
    ['associations', (data, options) => data.metadataSources(movieTarget, options)],
  ];
  for (const [name, run] of operations) await t.test(name, async t => {
    const entered = deferred(), gate = deferred();
    t.after(() => gate.resolve());
    const f = fixture(t), settings = f.handlers.tmdb['settings.get'];
    let active = true;
    f.handlers.tmdb['settings.get'] = async (_params, signal) => {
      entered.resolve(signal); await gate.promise; return settings();
    };
    const rejected = assert.rejects(run(f.data, { canSchedule: () => active }), { code: 'CANCELLED' });
    const signal = await entered.promise;
    active = false; gate.resolve(); await rejected;
    assert.equal(signal.aborted, false);
    assert.deepEqual(f.observed.map(row => [row.id, row.operation]), [['tmdb', 'settings.get']]);
  });
});

test('DT-01 E22 stopping an association search prevents detail but preserves the accepted search for reuse', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t);
  let active = true;
  f.handlers.douban.search = async (_params, signal) => {
    entered.resolve(signal); await gate.promise;
    return success({ subjects: { items: [{ target_id: '51', target_type: 'movie', target: doubanValue() }] } });
  };
  const rejected = assert.rejects(f.data.metadataSources(movieTarget,
    { metadata: movieValue(), canSchedule: () => active }), { code: 'CANCELLED' });
  const signal = await entered.promise;
  active = false; gate.resolve(); await rejected;
  assert.equal(signal.aborted, false);
  assert.equal(f.calls('douban', 'request').length, 0);
  const sources = await f.data.metadataSources(movieTarget, { metadata: movieValue() });
  assert.deepEqual(sources.map(row => [row.provider, row.metadataId]), [['tmdb', 42], ['douban', '51']]);
  assert.equal(f.calls('douban', 'search').length, 1);
  assert.equal(f.calls('douban', 'request').length, 1);
});

test('DT-01 E22 stopping cross-provider pagination prevents the next page or associated detail', async t => {
  for (const stopAt of [1, 2]) await t.test(`page ${stopAt}`, async t => {
    const entered = deferred(), gate = deferred();
    t.after(() => gate.resolve());
    const f = fixture(t), target = { ...movieTarget, metadataProvider: 'douban', metadataId: '51' };
    let active = true;
    f.handlers.tmdb.request = async ({ path, query }, signal) => {
      if (!path.startsWith('/3/search/')) return success(movieValue());
      if (query.page === stopAt) { entered.resolve(signal); await gate.promise; }
      return success({ page: query.page, total_pages: 2, results: [movieValue()] });
    };
    const rejected = assert.rejects(f.data.metadataSources(target,
      { metadata: doubanValue(), canSchedule: () => active }), { code: 'CANCELLED' });
    const signal = await entered.promise;
    active = false; gate.resolve(); await rejected;
    assert.equal(signal.aborted, false);
    assert.deepEqual(f.calls('tmdb', 'request').map(row => row.params.query.page), stopAt === 1 ? [1] : [1, 2]);
  });
});

test('DT-01 E22 stopping an accepted ordinary/TV proof prevents both TV play and ordinary fallback', async t => {
  for (const matched of [true, false]) await t.test(matched ? 'proof succeeds' : 'proof fails', async t => {
    const entered = deferred(), gate = deferred();
    t.after(() => gate.resolve());
    const f = fixture(t), match = f.handlers.pan['account.match'];
    let active = true;
    f.accountRows.find(row => row.provider === 'quark_tv').configured = true;
    f.handlers.pan['account.match'] = async (params, signal) => {
      entered.resolve(signal); await gate.promise; return matched ? match(params) : failure('AUTH_FAILED');
    };
    const rejected = assert.rejects(f.data.resolve(candidate(ref('quark', 'stored', { kind: 'stored' })),
      { canSchedule: () => active }), { code: 'CANCELLED' });
    const signal = await entered.promise;
    active = false; gate.resolve(); await rejected;
    assert.equal(signal.aborted, false);
    assert.equal(f.calls('pan', 'prepare').length, 0);
    assert.equal(f.calls('pan', 'play').length, 0);
  });
});

test('DT-01 E22 stopping while TV play is pending prevents a new ordinary play on failure', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t), play = f.handlers.pan.play;
  let active = true;
  f.accountRows.find(row => row.provider === 'quark_tv').configured = true;
  f.handlers.pan.play = async (params, signal) => {
    if (params.provider !== 'quark_tv') return play(params);
    entered.resolve(signal); await gate.promise; return failure('UPSTREAM_HTTP');
  };
  const rejected = assert.rejects(f.data.resolve(candidate(ref('quark', 'stored', { kind: 'stored' })),
    { canSchedule: () => active }), { code: 'CANCELLED' });
  const signal = await entered.promise;
  active = false; gate.resolve(); await rejected;
  assert.equal(signal.aborted, false);
  assert.deepEqual(f.calls('pan', 'play').map(row => row.params.provider), ['quark_tv']);
});

test('DT-01 E22 refresh checks scheduling after address invalidation has physically drained', async t => {
  const entered = deferred(), invalidating = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t), play = f.handlers.pan.play;
  let active = true;
  f.handlers.pan.play = async (params, signal) => { entered.resolve(signal); await gate.promise; return play(params); };
  const oldRejected = assert.rejects(f.data.resolve(candidate()), { code: 'CANCELLED' });
  const signal = await entered.promise;
  signal.addEventListener('abort', () => invalidating.resolve(), { once: true });
  const rejected = assert.rejects(f.data.refresh(candidate(), { canSchedule: () => active }), { code: 'CANCELLED' });
  await invalidating.promise;
  active = false; gate.resolve(); await Promise.all([oldRejected, rejected]);
  assert.equal(signal.aborted, true, 'explicit address invalidation still aborts the old producer');
  assert.equal(f.calls('cat', 'status').length, 1);
  assert.equal(f.calls('pan', 'settings.get').length, 1);
  assert.equal(f.calls('pan', 'prepare').length, 1);
  assert.equal(f.calls('pan', 'play').length, 1);
});

test('DT-01 E22 handoff may finish its accepted seed but stops before querying the next entry', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t), settings = f.handlers.tmdb['settings.get'];
  let active = true;
  const first = { ...requestFor(movieTarget), settings: { revision: f.revisions.tmdb },
    value: movieValue(), completeness: 'complete', fetchedAt: f.now() };
  f.handlers.tmdb['settings.get'] = async (_params, signal) => {
    entered.resolve(signal); await gate.promise; return settings();
  };
  const rejected = assert.rejects(f.data.handoff([first, { kind: 'search', source, query: 'next', page: 1,
    value: { list: [] }, completeness: 'complete', fetchedAt: f.now() }], { canSchedule: () => active }), { code: 'CANCELLED' });
  const signal = await entered.promise;
  active = false; gate.resolve(); await rejected;
  assert.equal(signal.aborted, false);
  assert.equal(f.calls('cat', 'status').length, 0);
  assert.deepEqual((await f.data.cachedEntry(requestFor(movieTarget))).value, movieValue());
});

test('DT-01 E22 soft stop does not abort accepted content or prevent another consumer from reusing its cache', async t => {
  const entered = deferred(), gate = deferred();
  t.after(() => gate.resolve());
  const f = fixture(t);
  let active = true;
  f.handlers.cat.invoke = async (_params, signal) => {
    entered.resolve(signal); await gate.promise; return success({ list: [{ vod_id: 0 }], pagecount: 1 });
  };
  const first = f.data.search(source, 'accepted-content', 1, { canSchedule: () => active });
  const signal = await entered.promise;
  const joined = f.data.search(source, 'accepted-content');
  active = false; gate.resolve();
  const [completed, shared] = await Promise.all([first, joined]);
  assert.equal(signal.aborted, false);
  assert.deepEqual(shared, completed);
  assert.deepEqual(await f.data.search(source, 'accepted-content'), completed);
  assert.equal(f.calls('cat', 'invoke').length, 1);
});

test('E22/E48 cancelling one consumer does not abort direct or shared cached producers; successful result remains reusable', async t => {
  const f = fixture(t), gate = deferred(), entered = deferred(), consumer = new AbortController();
  f.handlers.cat.invoke = async (_params, signal) => { entered.resolve(signal); await gate.promise; return success({ list: [], pagecount: 1 }); };
  const first = f.data.search(source, 'continued', 1, { signal: consumer.signal });
  const rejected = assert.rejects(first, { code: 'CANCELLED' });
  const producerSignal = await entered.promise;
  const joined = f.data.search(source, 'continued'); consumer.abort(); await rejected;
  assert.equal(producerSignal.aborted, false); gate.resolve(); await joined;
  await f.data.search(source, 'continued'); assert.equal(f.calls('cat', 'invoke').length, 1);
  const directGate = deferred(), directEntered = deferred(), directConsumer = new AbortController();
  f.handlers.cat.invoke = async (_params, signal) => { directEntered.resolve(signal); await directGate.promise; return success({ ok: true }); };
  const report = f.data.report(rawCandidate(), { sessionId: 's', positionSeconds: 0, durationSeconds: 1, event: 'progress' },
    { signal: directConsumer.signal });
  const directRejected = assert.rejects(report, { code: 'CANCELLED' });
  const directSignal = await directEntered.promise; directConsumer.abort(); await directRejected;
  assert.equal(directSignal.aborted, false); directGate.resolve();
});

test('E48 close revokes direct report/play and cached metadata synchronously, drains physically, and never closes borrowed owners', async t => {
  const f = fixture(t), gate = deferred(), entered = [];
  let borrowedClosed = 0;
  for (const module of f.modules.values()) module.close = () => { borrowedClosed++; };
  f.handlers.cat.invoke = async (_params, signal) => { entered.push(signal); await gate.promise;
    return success({ ok: true, url: 'https://media.invalid/late.mp4' }); };
  f.handlers.tmdb.request = async (_params, signal) => { entered.push(signal); await gate.promise; return success(movieValue()); };
  const works = [f.data.resolve(rawCandidate()), f.data.report(rawCandidate(), {
    sessionId: 's', positionSeconds: 1, durationSeconds: 2, event: 'pause' }), f.data.targetMetadata(movieTarget)];
  const rejected = works.map(work => assert.rejects(work, error => ['CLOSED', 'CANCELLED'].includes(error.code)));
  await tick(); assert.equal(entered.length, 3);
  let closed = false;
  const closing = f.data.close().then(() => { closed = true; });
  assert.equal(entered.every(signal => signal.aborted), true);
  await Promise.all(rejected); await tick(); assert.equal(closed, false);
  await assert.rejects(async () => f.data.targetMetadata(movieTarget), { code: 'CLOSED' });
  gate.resolve(); await closing; assert.equal(closed, true); assert.equal(borrowedClosed, 0);
  assert.strictEqual(f.data.close(), f.data.close());
});

test('E48 unknown thrown/outer error prose is redacted while known network/format errors retain their codes', async t => {
  const f = fixture(t);
  f.handlers.cat.invoke = () => { throw new Error('secret=https://private.invalid/password'); };
  await assert.rejects(f.data.resolve(rawCandidate()), error => error.code === 'UPSTREAM_ERROR' && error.message === 'UPSTREAM_ERROR');
  f.handlers.cat.invoke = () => failure('TIMEOUT');
  await assert.rejects(f.data.resolve(rawCandidate()), { code: 'TIMEOUT' });
  f.handlers.cat.invoke = () => failure('RESOURCE_LIMIT');
  await assert.rejects(f.data.resolve(rawCandidate()), { code: 'RESOURCE_LIMIT' });
  f.handlers.cat.invoke = () => failure('SECRET_RAW_TEXT');
  await assert.rejects(f.data.resolve(rawCandidate()), { code: 'UPSTREAM_ERROR' });
  f.handlers.cat.invoke = () => success('not-json');
  await assert.rejects(f.data.resolve(rawCandidate()), { code: 'UPSTREAM_FORMAT' });
});

test('E48 cached metadata interaction and empty media successes retain distinct errors without poisoning the next request', async t => {
  const f = fixture(t);
  f.handlers.tmdb.request = () => failure('WEB_INTERACTION_REQUIRED');
  await assert.rejects(f.data.targetMetadata(movieTarget), { code: 'WEB_INTERACTION_REQUIRED' });
  f.handlers.tmdb.request = () => success(movieValue());
  assert.equal((await f.data.targetMetadata(movieTarget)).runtime, 120);
  f.handlers.pan.play = ({ mode }) => success({ url: '', headers: {}, mode });
  const selected = candidate(ref('tianyi'));
  await assert.rejects(f.data.resolve(selected), { code: 'MEDIA_UNAVAILABLE' });
  f.handlers.pan.play = ({ mode }) => success({ url: 'https://media.invalid/recovered.mp4', headers: {}, mode });
  assert.equal((await f.data.resolve(selected)).url, 'https://media.invalid/recovered.mp4');
});

test('E03/E24 actual metadata module owner revision and protocol integrate without network or configured identity fallback', async t => {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-data-adapter-'));
  const observed = [];
  const request = async (url, options) => {
    observed.push({ url, headers: options.headers });
    const path = new URL(url).pathname;
    const search = path === '/movie/subject_search';
    const value = path.startsWith('/rexxar/api/v2/') ? doubanValue() : movieValue();
    return { status: 200, headers: { 'content-type': search ? 'text/html' : 'application/json' },
      body: Buffer.from(search ? `<script>window.__DATA__ = ${JSON.stringify({ items: [{
        id: '51', title: '真实电影 (2024)', labels: [{ text: '电影' }], abstract: '实际摘要',
      }], total: 1, count: 1, start: 0 })};</script>` : JSON.stringify(value)) };
  };
  const tmdb = createTMDBDataModule({ dataRoot: root, request });
  const douban = createDoubanDataModule({ dataRoot: root, request });
  const cache = createPlaybackCache();
  const data = createPlaybackData({ modules: new Map([['tmdb', tmdb], ['douban', douban]]), cache });
  t.after(async () => { await data.close(); await cache.close(); await tmdb.close(); await douban.close(); rmSync(root, { recursive: true, force: true }); });
  assert.equal((await tmdb.methods['tmdb.data.settings.update']({ accessToken: 'synthetic-access-token' })).ok, true);
  await data.targetMetadata(movieTarget); await data.targetMetadata(movieTarget);
  assert.equal(observed.length, 1);
  assert.equal(new URL(observed[0].url).pathname, '/3/movie/42');
  const firstRevision = (await data.cachedEntry(requestFor(movieTarget))).settings.revision;
  await tmdb.methods['tmdb.data.settings.update']({ accessToken: 'synthetic-access-token' });
  await data.targetMetadata(movieTarget); assert.equal(observed.length, 1);
  await tmdb.methods['tmdb.data.settings.update']({ accessToken: 'synthetic-replacement' });
  await data.targetMetadata(movieTarget); assert.equal(observed.length, 2);
  assert.notEqual((await data.cachedEntry(requestFor(movieTarget))).settings.revision, firstRevision);
  assert.equal(firstRevision.includes('synthetic'), false);
  const found = await data.metadata('douban', 'search', { query: '真实电影' });
  assert.equal(found.subjects.items[0].target_type, 'movie'); assert.equal(found.subjects.items[0].target_id, '51');
  assert.equal(found.subjects.items[0].target.year, '2024');
  const target = { ...movieTarget, metadataProvider: 'douban', metadataId: '51' };
  assert.equal((await data.targetMetadata(target)).intro, '豆瓣实际简介');
  assert.equal(new URL(observed.at(-1).url).pathname, '/rexxar/api/v2/movie/51');
  assert.equal((await data.cachedEntry(requestFor(target))).settings.revision,
    (await douban.methods['douban.data.settings.get']({})).value.revision);
});

test('F1/F4 Cat home/category/search/detail share process ownership and refresh only exact catalog/query/ID', async t => {
  const f = fixture(t);
  let version = 1;
  f.handlers.cat.invoke = ({ operation, payload }) => success(JSON.stringify(
    operation === 'detail' ? detailValue(payload.id, { version })
      : { class: [{ type_id: 0, type_name: '原始分类' }], list: [{ vod_id: version }],
        pagecount: 1, original: { operation, payload, version } }));
  const home = await f.data.catalog(source, 'home', { filter: true });
  await f.data.catalog(source, 'home', { filter: true });
  const category = { tid: 0, page: 1, filter: true, extend: { tag: '剧情' } };
  await f.data.catalog(source, 'category', category);
  await f.data.search(source, '准确查询');
  await f.data.detail(source, 0);
  assert.equal(f.calls('cat', 'invoke').length, 4);
  assert.deepEqual(home.original, { operation: 'home', payload: { filter: true }, version: 1 });
  version = 2;
  const next = await f.data.catalog(source, 'home', { filter: true }, { refresh: true });
  assert.equal(next.original.version, 2);
  assert.equal((await f.data.catalog(source, 'category', category)).original.version, 1);
  await f.data.search(source, '准确查询');
  await f.data.detail(source, 0);
  assert.equal(f.calls('cat', 'invoke').length, 5);
  await f.data.search(source, '准确查询', 1, { refresh: true });
  const detail = await f.data.detail(source, 0, { refresh: true });
  assert.equal(detail.raw.list[0].version, 2);
  assert.equal(f.calls('cat', 'invoke').length, 7);
  await f.data.catalog(source, 'category', { ...category, page: 2 });
  assert.equal(f.calls('cat', 'invoke').length, 8);
  await assert.rejects(f.data.catalog(source, 'play', {}), { code: 'INVALID_REQUEST' });
});

test('F1 Cat and pan response/prepare/account-match/address caches never persist across data-owner restarts', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'film-process-cache-'));
  t.after(() => rmSync(dataRoot, { recursive: true, force: true }));
  const line = { id: 'share-line', episodes: [{ id: 0, share: share() }] };
  for (let restart = 0; restart < 2; restart++) {
    const f = fixture(t, { dataRoot });
    f.accountRows.find(row => row.provider === 'quark_tv').configured = true;
    await f.data.catalog(source, 'home');
    await f.data.search(source, '相同查询');
    await f.data.detail(source, 0);
    await f.data.expand(source, line);
    await f.data.resolve(candidate());
    await f.data.resolve(rawCandidate());
    await f.data.resolve(rawCandidate(portable));
    await f.data.catalog(source, 'home');
    await f.data.search(source, '相同查询');
    await f.data.detail(source, 0);
    await f.data.expand(source, line);
    await f.data.resolve(candidate());
    await f.data.resolve(rawCandidate());
    await f.data.resolve(rawCandidate(portable));
    assert.equal(f.calls('cat', 'invoke').length, 4);
    assert.equal(f.calls('source', 'invoke').length, 1);
    assert.equal(f.calls('pan', 'list').length, 1);
    assert.equal(f.calls('pan', 'prepare').length, 1);
    assert.equal(f.calls('pan', 'account.match').length, 1);
    assert.equal(f.calls('pan', 'play').length, 1);
    await f.data.close();
  }
});

test('F1/F4 Cat/source direct play caches exact source/flag/typed play IDs while reports always invoke', async t => {
  for (const selectedSource of [source, portable]) await t.test(selectedSource.engine, async t => {
    const f = fixture(t), engine = selectedSource.engine;
    const otherSource = { ...selectedSource, installationId: 'other-installation', key: `${engine}:other-installation` };
    f.instances.set(otherSource.key, { ...f.instances.get(selectedSource.key), ...otherSource });
    const selected = rawCandidate(selectedSource, { flag: 0, playId: 0 });
    const others = [rawCandidate(selectedSource, { flag: '0', playId: 0 }),
      rawCandidate(selectedSource, { flag: 0, playId: '0' }), rawCandidate(otherSource, { flag: 0, playId: 0 })];
    let issued = 0;
    f.handlers[engine].invoke = ({ operation, action, payload }) => success((operation ?? action) === 'report'
      ? { ok: true } : { url: `https://media.invalid/direct-${++issued}.mp4`,
        header: { Referer: 'https://example.invalid/' }, original: { flag: payload.flag, id: payload.id } });
    const first = await f.data.resolve(selected);
    assert.deepEqual(first.original, { flag: 0, id: 0 });
    assert.deepEqual(first.headers, { Referer: 'https://example.invalid/' });
    assert.deepEqual(await f.data.resolve({ ...selected, id: 'another-consumer', lineLabel: '展示名称变化' }), first);
    const separate = await Promise.all(others.map(item => f.data.resolve(item)));
    assert.equal(issued, 4);
    const requests = f.observed.length;
    await f.data.invalidateAddress(selected);
    assert.equal(f.observed.length, requests, 'address revocation does not resolve or validate a source');
    assert.deepEqual(await Promise.all(others.map(item => f.data.resolve(item))), separate);
    assert.equal(issued, 4);
    f.advance(1);
    const reloaded = await f.data.resolve(selected);
    assert.notEqual(reloaded.url, first.url);
    assert.equal(reloaded.resolvedAt, f.now());
    assert.equal(issued, 5);
    f.advance(1);
    const refreshed = await f.data.refresh(selected);
    assert.notEqual(refreshed.url, reloaded.url);
    assert.equal(refreshed.resolvedAt, f.now());
    assert.deepEqual(await f.data.resolve(selected), refreshed);
    assert.deepEqual(await Promise.all(others.map(item => f.data.resolve(item))), separate);
    assert.equal(issued, 6);
    const payload = { sessionId: 'direct-session', event: 'progress', positionSeconds: 1, durationSeconds: 2 };
    await f.data.report(selected, payload);
    await f.data.report(selected, payload);
    const reports = f.calls(engine, 'invoke').filter(row => (row.params.operation ?? row.params.action) === 'report');
    assert.equal(reports.length, 2);
    assert.deepEqual(reports.map(row => row.params.payload), [{ flag: 0, id: 0, ...payload }, { flag: 0, id: 0, ...payload }]);
    assert.equal(issued, 6);
  });
});

test('F1 Cat/source direct addresses expire at the short boundary or an earlier upstream expiration', async t => {
  for (const selectedSource of [source, portable]) await t.test(selectedSource.engine, async t => {
    const f = fixture(t), selected = rawCandidate(selectedSource);
    let issued = 0, upstreamTTL;
    f.handlers[selectedSource.engine].invoke = () => success({ url: `https://media.invalid/direct-${++issued}.mp4`,
      ...(upstreamTTL === undefined ? {} : { expiresAt: f.now() + upstreamTTL }) });
    const first = await f.data.resolve(selected);
    assert.equal(first.expiresAt, f.now() + ADDRESS);
    f.advance(ADDRESS - 1);
    assert.deepEqual(await f.data.resolve(selected), first);
    assert.equal(issued, 1);
    f.advance(1);
    assert.notEqual((await f.data.resolve(selected)).url, first.url);
    assert.equal(issued, 2);
    upstreamTTL = 25_000;
    const shorter = await f.data.refresh(selected);
    assert.equal(shorter.expiresAt, f.now() + upstreamTTL);
    f.advance(upstreamTTL - 1);
    assert.deepEqual(await f.data.resolve(selected), shorter);
    assert.equal(issued, 3);
    f.advance(1);
    assert.notEqual((await f.data.resolve(selected)).url, shorter.url);
    assert.equal(issued, 4);
    upstreamTTL = 2 * ADDRESS;
    assert.equal((await f.data.refresh(selected)).expiresAt, f.now() + ADDRESS);
    assert.equal(issued, 5);
  });
});

test('F1/F5 Cat/source direct play coalesces after a consumer leaves and cannot publish after address revocation', async t => {
  for (const selectedSource of [source, portable]) await t.test(selectedSource.engine, async t => {
    const entered = deferred(), gate = deferred(), revokedEntered = deferred(), revokedGate = deferred();
    t.after(() => { gate.resolve(); revokedGate.resolve(); });
    const f = fixture(t), selected = rawCandidate(selectedSource), consumer = new AbortController();
    f.handlers[selectedSource.engine].invoke = async (_params, signal) => {
      entered.resolve(signal); await gate.promise; return success({ url: 'https://media.invalid/accepted.mp4' });
    };
    const rejected = assert.rejects(f.data.resolve(selected, { signal: consumer.signal }), { code: 'CANCELLED' });
    const signal = await entered.promise;
    const joined = f.data.resolve(selected);
    consumer.abort();
    await rejected;
    assert.equal(signal.aborted, false);
    gate.resolve();
    const accepted = await joined;
    assert.deepEqual(await f.data.resolve(selected), accepted);
    assert.equal(f.calls(selectedSource.engine, 'invoke').length, 1);
    f.handlers[selectedSource.engine].invoke = async (_params, signal) => {
      revokedEntered.resolve(signal); await revokedGate.promise; return success({ url: 'https://media.invalid/revoked.mp4' });
    };
    const revoked = assert.rejects(f.data.refresh(selected), { code: 'CANCELLED' });
    const revokedSignal = await revokedEntered.promise;
    let drained = false;
    const invalidation = f.data.invalidateAddress(selected).then(() => { drained = true; });
    await revoked;
    assert.equal(revokedSignal.aborted, true);
    await tick();
    assert.equal(drained, false);
    revokedGate.resolve();
    await invalidation;
    f.handlers[selectedSource.engine].invoke = () => success({ url: 'https://media.invalid/current.mp4' });
    assert.equal((await f.data.resolve(selected)).url, 'https://media.invalid/current.mp4');
    assert.equal(f.calls(selectedSource.engine, 'invoke').length, 3);
  });
});

test('F4 expand refresh replaces only the selected share list and unchanged tokens retain prepared files', async t => {
  const f = fixture(t);
  const line = url => ({ id: url, episodes: [{ id: 0, share: share('quark', { url }) }] });
  const first = line('https://pan.quark.cn/s/first'), other = line('https://pan.quark.cn/s/other');
  await f.data.expand(source, first);
  await f.data.expand(source, other);
  await f.data.resolve(candidate());
  f.handlers.pan.list = () => success({ files: [] });
  const refreshed = await f.data.expand(source, first, { refresh: true });
  assert.deepEqual(refreshed.episodes, []);
  assert.equal((await f.data.expand(source, other)).episodes.length, 1);
  await f.data.resolve(candidate());
  assert.equal(f.calls('pan', 'list').length, 3);
  assert.equal(f.calls('pan', 'prepare').length, 1);
  assert.equal(f.calls('pan', 'play').length, 1);
});

test('F4 address invalidation only revokes that address and never resolves or clears other files/prepares', async t => {
  const f = fixture(t), selected = candidate(), other = candidate(ref('quark', 'another-file'));
  await f.data.resolve(selected);
  await f.data.resolve(other);
  const requests = f.observed.length;
  await f.data.invalidateAddress(selected);
  assert.equal(f.observed.length, requests);
  await f.data.resolve(other);
  assert.equal(f.calls('pan', 'play').length, 2);
  await f.data.resolve(selected);
  assert.equal(f.calls('pan', 'play').length, 3);
  assert.equal(f.calls('pan', 'prepare').length, 2);
});

test('F6 source/group target metadata entry/raw reads return null without metadata work', async t => {
  const f = fixture(t);
  for (const kind of ['source', 'group']) {
    const target = { kind, metadataProvider: 'tmdb', metadataId: 42 };
    assert.equal(await f.data.targetMetadataEntry(target), null);
    assert.equal(await f.data.targetMetadata(target), null);
    assert.equal(await f.data.seasonMetadataEntry(target, 1), null);
    assert.equal(await f.data.seasonMetadata(target, 1), null);
  }
  assert.equal(f.observed.length, 0);
});
