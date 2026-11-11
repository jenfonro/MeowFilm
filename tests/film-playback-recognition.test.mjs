import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildEpisodeMap, resolveEpisode } from '../packages/film-playback/src/episodes.js';
import { matchesResult, normalizeDetail, recognizeCandidates } from '../packages/film-playback/src/recognition.js';
import { isCandidateAllowed } from '../packages/film-playback/src/policy.js';
import { createRecognitionRules } from '../packages/film-playback/src/rules.js';
import { checkTarget, resultDetailId } from '../packages/film-playback/src/identity.js';
import { encodePanResource } from '@meowfilm/script-host/cat-output';

const source = { key: 'cat:install:site', engine: 'cat', installationId: 'install', siteId: 'site',
  revision: 3, generation: 4, name: '实际站点' };
const target = { id: 'tmdb:tv:1', kind: 'tv', title: '真实剧集', metadataProvider: 'tmdb', metadataId: 1 };
const metadata = { seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 3 }] };
const file = (fileId, patch = {}) => ({ kind: 'share', provider: 'quark', accountRevision: 2,
  shareId: 'share-a', fileId, stoken: 'synthetic-token', fileToken: 'synthetic-file-token', ...patch });
const detail = (episodes, line = {}) => ({ id: 'detail-a', lines: [{ id: 'flag-a', label: '原始线路',
  episodes, ...line }] });
const recognize = (raw, options = {}) => recognizeCandidates(normalizeDetail(raw, { source }),
  { target, source, ...options });
const coordinates = map => map.map(({ season, episode, globalEpisode }) =>
  [season, episode, globalEpisode]);

test('E49 typed projected detail identity is not a UI id, display siteDetail or a String-coerced member', () => {
  const raw = { vod_id: 0, vod_name: '原始标题', vod_remarks: '原备注' };
  const projection = { id: 'opaque-display-id', sourceKind: 'site', siteKey: source.key,
    detailId: 0, siteDetail: '0', title: '展示标题', raw };
  const before = structuredClone(projection);
  assert.equal(resultDetailId(projection), 0);
  assert.equal(resultDetailId({ id: 'provider-id', raw }), 'provider-id');
  assert.equal(resultDetailId({ detailId: '0', raw: { id: '0' } }), '0');
  assert.equal(resultDetailId({ siteDetail: 'display-only' }), undefined);
  const group = checkTarget({ id: 'group:actual', kind: 'group', title: '原始标题',
    members: [{ sourceKey: source.key, detailId: 0 }, { sourceKey: source.key, detailId: '0' }] });
  assert.deepEqual(group.members.map(row => row.detailId), [0, '0']);
  assert.equal(group.metadataProvider, undefined);
  const filtered = checkTarget({ ...group, members: [{ sourceKey: 'remaining', detailId: 0 }],
    source: { key: 'removed', detailId: 0, candidateId: 'old-exact-selection' } });
  assert.deepEqual(filtered.source, { key: 'removed', detailId: 0, candidateId: 'old-exact-selection' });
  assert.deepEqual(filtered.members, [{ sourceKey: 'remaining', detailId: 0 }]);
  for (const patch of [{ members: [] }, { members: [{ sourceKey: source.key, detailId: null }] },
    { members: [{ sourceKey: '', detailId: 0 }] }, { metadataProvider: 'tmdb', metadataId: 42 }]) {
    assert.throws(() => checkTarget({ ...group, ...patch }), { code: 'INVALID_REQUEST' });
  }
  assert.deepEqual(projection, before);
});

test('E01/E02/E49 provider raw fields never turn an original row into a shared projection or override typed aliases', async () => {
  for (const id of [0, '0']) {
    for (const raw of [{ original: true }, { id: 'wrong', vod_id: 'wrong', vod_name: '其他影片' }]) {
      const row = { vod_id: id, id: 'ignored', detailId: 'ignored-detail', vod_name: '星河', raw };
      const before = structuredClone(row);
      assert.equal(resultDetailId(row), id);
      assert.equal((await matchesResult(row, { target: { kind: 'group', title: '星河' } })), true);
      const value = normalizeDetail({ list: [
        { vod_id: id === 0 ? '0' : 0, id, lines: [] },
        { ...row, lines: [{ id: 'line', episodes: [{ id: 'actual-file', name: '星河' }] }] },
      ] }, { source: { ...source, detailId: id } });
      assert.equal(value.detailId, id);
      assert.equal(value.lines[0].episodes[0].playId, 'actual-file');
      assert.equal((await recognizeCandidates(value, { source, target: { id: 'group:star', kind: 'group', title: '星河' } }))[0]
        .source.detailId, id);
      assert.deepEqual(row, before);
    }
    assert.equal(resultDetailId({ id, detailId: 'ignored', raw: { id: 'wrong' } }), id);
    assert.equal(resultDetailId({ vod_id: null, id, raw: { id: 'wrong' } }), id);
  }
  for (const vod_id of [false, '', {}, [], 0.5, Number.MAX_SAFE_INTEGER + 1]) {
    const row = { vod_id, id: 0, detailId: 0, raw: { id: 0 }, lines: [] };
    assert.equal(resultDetailId(row), vod_id);
    assert.throws(() => normalizeDetail(row, { source: { ...source, detailId: 0 } }), { code: 'INVALID_DETAIL' });
    assert.throws(() => normalizeDetail({ list: [row] }, { source: { ...source, detailId: 0 } }),
      { code: 'INVALID_DETAIL' });
  }
  const projection = { id: 'opaque-ui', sourceKind: 'site', siteKey: source.key, detailId: 0,
    title: '展示标题', raw: { id: 0, vod_name: '星河' } };
  assert.equal(resultDetailId(projection), 0);
  assert.equal((await matchesResult(projection, { target: { kind: 'group', title: '星河' } })), true);
  assert.equal(resultDetailId({ ...projection, detailId: undefined }), undefined);
  assert.equal(resultDetailId({ ...projection, detailId: false }), false);
  assert.throws(() => normalizeDetail({ list: [{ lines: [] }, { lines: [] }] }), { code: 'INVALID_DETAIL' });
});

test('E49 unknown group uses canonical evidence for newly discovered rows without guessing movie/tv from a card title', async () => {
  const group = { kind: 'group', title: '真实电影2', year: 2024 };
  const projection = { sourceKind: 'site', siteKey: source.key, detailId: 0 };
  assert.equal((await matchesResult({ ...projection, raw: { vod_name: '真实电影2[4K]', vod_year: '2024' }, title: 'display' },
    { target: group })), true);
  assert.equal((await matchesResult({ ...projection, raw: { vod_name: '别的影片', vod_year: '2024' }, title: '真实电影2' },
    { target: group })), false);
  assert.equal((await matchesResult({ title: '真实电影3', year: 2024 }, { target: group })), false);
  assert.equal((await matchesResult({ title: '真实电影2', year: 2023 }, { target: group })), false);
  for (const kind of ['movie', 'tv']) assert.equal((await matchesResult({ title: '真实电影2', year: 2024, kind },
    { target: group })), true);
});

test('E49 group preserves every actual file and never borrows a late metadata display to fabricate global coordinates', async () => {
  const group = { id: 'group:files', kind: 'group', title: '原始内容',
    members: [{ sourceKey: source.key, detailId: 'detail-a' }] };
  const raw = detail([{ id: 'unknown-first', name: '原始未编号正片' },
    { id: 'second', name: 'S01E02.4K' }, { id: 'bare-third', name: '第3集' }]);
  const before = structuredClone(raw);
  const items = (await recognize(raw, { target: group, metadata: { seasons: [{ season_number: 1, episode_count: 8 }] } }));
  assert.equal(items.length, 3);
  assert.equal(items.every(item => item.kind === 'group' && item.targetId === group.id), true);
  assert.equal(items[0].episode, undefined); assert.equal(items[0].season, undefined);
  assert.equal(items[1].episode, 2); assert.equal(items[1].season, 1);
  assert.equal(items[2].episode, 3); assert.equal(items[2].season, undefined);
  assert.equal(items.every(item => item.globalEpisode === undefined), true);
  assert.deepEqual(raw, before);
});

test('E02 smart search rejects unrelated keyword results and accepts actual canonical titles/aliases only', async () => {
  const selected = { kind: 'movie', title: '真实·电影', year: 2024, originalTitle: 'Actual Original' };
  assert.equal((await matchesResult({ vod_name: '真实电影幕后', vod_year: '2024' }, { target: selected })), false);
  assert.equal((await matchesResult({ vod_name: '完全无关', vod_year: '2024' }, { target: selected })), false);
  assert.equal((await matchesResult({ title: '真实 电影（2024）' }, { target: selected })), true);
  assert.equal((await matchesResult({ title: 'ＡＣＴＵＡＬ　ＯＲＩＧＩＮＡＬ', year: '2024' }, { target: selected })), true);
  assert.equal((await matchesResult({ title: '另一译名', originalTitle: 'Actual Original', year: 2024 }, { target: selected })), true);
  assert.equal((await matchesResult({ title: '另一译名', localized_name: '真实电影', year: 2024 }, { target: selected })), true);
});

test('E02 format controls in canonical titles are ignored without admitting a different movie', async () => {
  const selected = { kind: 'movie', title: '真实电影2' };
  // Soft hyphen, Arabic/Syriac/Mongolian controls, bidi controls, annotations and supplementary format controls.
  const controls = '\u00ad\u0600\u0605\u061c\u06dd\u070f\u0890\u0891\u08e2\u180e' +
    '\u200b\u200c\u200d\u200e\u200f\u202a\u202e\u2060\u2064\u2066\u2069\u206f\ufeff\ufff9\ufffb' +
    '\u{110bd}\u{110cd}\u{13430}\u{1343f}\u{1bca0}\u{1bca3}\u{1d173}\u{1d17a}\u{e0001}\u{e0020}\u{e007f}';
  for (const control of controls) {
    const title = `真${control}实电影2`, label = control.codePointAt(0).toString(16);
    assert.equal((await matchesResult({ title }, { target: selected })), true, label);
    assert.equal((await matchesResult({ title: selected.title }, { target: { ...selected, title } })), true, label);
    assert.equal((await matchesResult({ title: `真${control}实电影3` }, { target: selected })), false, label);
  }
});

test('E02 zero-width non-format characters and format-range neighbours remain title identity evidence', async () => {
  const selected = { kind: 'movie', title: '真实电影' };
  // Combining grapheme joiner, Khmer/Mongolian/Unicode variation marks and unassigned range gaps are not Cf.
  const significant = '\u034f\u0606\u061d\u06de\u0710\u0892\u08e3\u17b4\u180b\u180d\u180f\u2065\ufe00\ufe0e' +
    '\ufff8\ufffc\u{110bc}\u{110be}\u{110cc}\u{110ce}\u{1342f}\u{13440}\u{1bc9f}\u{1bca4}' +
    '\u{1d172}\u{1d17b}\u{e0000}\u{e0002}\u{e001f}\u{e0080}\u{e0100}\u{e01ef}';
  for (const character of significant) {
    const title = `真${character}实电影`, label = character.codePointAt(0).toString(16);
    assert.equal((await matchesResult({ title }, { target: selected })), false, label);
    assert.equal((await matchesResult({ title }, { target: { ...selected, title } })), true, label);
  }
});

test('E02 canonical title identity retains full NFKC rather than only fullwidth folding', async () => {
  for (const [raw, normalized] of [
    ['ＡＢＣ１２３', 'ABC123'], ['\ufb03', 'ffi'], ['e\u0301', '\u00e9'],
    ['\u1112\u1161\u11ab', '한'], ['x\u0315\u0300', 'x\u0300\u0315'],
  ]) {
    assert.equal((await matchesResult({ title: '片名' + raw },
      { target: { kind: 'movie', title: '片名' + normalized } })), true);
  }
});

test('E02 explicit year/type/season and provider identity conflicts are not overridden by a matching title', async () => {
  const selected = { kind: 'movie', title: '真实电影', year: 2024, metadataProvider: 'tmdb', metadataId: 42 };
  assert.equal((await matchesResult({ title: '真实电影', year: 2023 }, { target: selected })), false);
  assert.equal((await matchesResult({ title: '真实电影', kind: 'tv', year: 2024 }, { target: selected })), false);
  assert.equal((await matchesResult({ title: '真实电影', metadataProvider: 'tmdb', metadataId: 43 }, { target: selected })), false);
  assert.equal((await matchesResult({ title: '不同展示标题', metadataProvider: 'tmdb', metadataId: 42 }, { target: selected })), true);
  assert.equal((await matchesResult({ title: '不同展示标题', tmdbId: 42 }, { target: selected })), true);
  assert.equal((await matchesResult({ title: '真实电影', year: 2023, metadataProvider: 'tmdb', metadataId: 42 }, { target: selected })), false);
  const series = { kind: 'tv', title: '真实剧集', season: 2, year: 2020 };
  assert.equal((await matchesResult({ title: '真实剧集 第二季', year: 2022 }, { target: series })), true);
  assert.equal((await matchesResult({ title: '真实剧集 第一季', year: 2020 }, { target: series })), false);
  assert.equal((await matchesResult({ title: '真实剧集', year: 2022 }, { target: series })), false);
  assert.equal((await matchesResult({ title: '真实剧集', season_number: 1, year: 2020 }, { target: series })), false);
  assert.equal((await matchesResult({ title: '真实剧集', season_number: 2, year: 2022 }, { target: series })), true);
});

test('E02 aggregate display/group keys do not replace raw identity evidence', async () => {
  const selected = { kind: 'movie', title: '真实电影', year: 2024 };
  assert.equal((await matchesResult({ rawTitle: '其他影片', title: '真实电影', aggregateContentKey: '真实电影',
    groupKey: '真实电影', year: 2024 }, { target: selected })), false);
  assert.equal((await matchesResult({ rawTitle: '其他影片', title: '真实电影', originalTitle: '真实电影', year: 2024 },
    { target: selected })), true);
  assert.equal((await matchesResult({ rawTitle: '其他影片', title: '真实电影', metadataProvider: 'tmdb', metadataId: 42 },
    { target: { ...selected, metadataProvider: 'tmdb', metadataId: 42 } })), true);
  assert.equal((await matchesResult({ title: '其他影片' }, { target: { kind: 'source', title: '原始来源' } })), true);
});

test('E02 movie sequel digits and digit-bearing TV titles are not fuzzy canonical aliases', async () => {
  assert.equal((await matchesResult({ title: '真实电影2' }, { target: { kind: 'movie', title: '真实电影' } })), false);
  assert.equal((await matchesResult({ title: '真实电影3' }, { target: { kind: 'movie', title: '真实电影2' } })), false);
  assert.equal((await matchesResult({ title: '中国1946' }, { target: { kind: 'tv', title: '中国1945' } })), false);
  assert.equal((await matchesResult({ title: '请回答1989' }, { target: { kind: 'tv', title: '请回答1988' } })), false);
  assert.equal((await matchesResult({ title: '请回答1988 第二季' }, { target: { kind: 'tv', title: '请回答1988', season: 2 } })), true);
  assert.equal((await matchesResult({ title: '中国1946 第二季' }, { target: { kind: 'tv', title: '中国1945 第二季' } })), false);
  assert.equal((await matchesResult({ title: '中国1945 Season 02' }, { target: { kind: 'tv', title: '中国1945 第二季' } })), true);
});

test('E02 movie titles consume actual shared aggregate cleaning while retaining sequel numbers through source annotations', async () => {
  const selected = { kind: 'movie', title: '真实电影', year: 2024 };
  assert.equal((await matchesResult({ title: '🎬【站点】真实电影 1080P（2024）' }, { target: selected })), true);
  assert.equal((await matchesResult({ title: '真实电影2[4K]' }, { target: selected })), false);
  assert.equal((await matchesResult({ title: '真实电影2[4K]' }, { target: { ...selected, title: '真实电影2' } })), true);
  assert.equal((await matchesResult({ title: '真实电影3[4K]' }, { target: { ...selected, title: '真实电影2' } })), false);
  assert.equal((await matchesResult({ title: '[自定]真实电影' },
    { target: selected, rules: { aggregateRegexRules: ['\\[自定\\]'] } })), true);
});

test('E02 shared aggregate rules are deterministic and never mutate input or use a fuzzy score', async () => {
  const rules = { aggregateRegexRules: ['\\[[^\\]]*\\]'] };
  const selected = { kind: 'tv', title: '真实剧集', season: 2 };
  const raw = { title: '[站点] 真实剧集 Season 02', season: 2 };
  const before = structuredClone({ raw, selected, rules });
  for (let repeat = 0; repeat < 3; repeat++) assert.equal((await matchesResult(raw, { target: selected, rules })), true);
  assert.deepEqual({ raw, selected, rules }, before);
  assert.equal((await matchesResult({ title: '真实剧集衍生' }, { target: selected, rules })), false);
  (await assert.rejects(async () => (await matchesResult(raw, { target: selected, rules: { aggregateRegexRules: [null] } })),
    { code: 'INVALID_RULES' }));
  assert.equal((await matchesResult(null, { target: selected })), false);
});

test('E04 TMDB real season numbers, specials and unequal lengths have exact global coordinates', () => {
  const raw = { seasons: [{ season_number: 2, episode_count: 3 },
    { season_number: 0, episodes: [{ episode_number: 2, name: '特别篇' }] },
    { season_number: 1, episode_count: 2 }] };
  const before = structuredClone(raw);
  const map = buildEpisodeMap(raw);
  assert.deepEqual(coordinates(map), [[0, 2, undefined], [1, 1, 1], [1, 2, 2],
    [2, 1, 3], [2, 2, 4], [2, 3, 5]]);
  assert.equal(resolveEpisode(map, { globalEpisode: 3 }).season, 2);
  assert.equal(resolveEpisode(map, { season: 0, episode: 2 }).name, '特别篇');
  assert.equal(resolveEpisode(map, { season: 0, episode: 2, globalEpisode: 2 }), null);
  assert.equal(resolveEpisode(map, { season: 2, episode: 4 }), null);
  assert.equal(resolveEpisode(map, { season: 2, episode: 1, globalEpisode: 4 }), null);
  assert.deepEqual(raw, before);
});

test('E04 Douban independently maps its declared seasons without requiring TMDB', () => {
  const map = buildEpisodeMap({ doubanSeasons: [{ season: 1, episodeCount: 3 }, { season: 2, episodeCount: 1 }] });
  assert.deepEqual(coordinates(map), [[1, 1, 1], [1, 2, 2], [1, 3, 3], [2, 1, 4]]);
  assert.deepEqual(resolveEpisode(map, { globalEpisode: 4 }), { season: 2, episode: 1, globalEpisode: 4 });
  assert.deepEqual(buildEpisodeMap({ id: 'douban-independent', episodes_count: 24 }), []);
});

test('E04 sparse metadata uses supplied episode numbers, never array indexes or invented gap rows', () => {
  const map = buildEpisodeMap({ seasons: [{ season_number: 1, episode_count: 5, episodes: [
    { episode_number: 5, name: '真实第五集' }, { name: '没有集号' }, { episode_number: 2, still_path: '/real.jpg' },
  ] }] });
  assert.deepEqual(coordinates(map), [[1, 2, 2], [1, 5, 5]]);
  assert.equal(resolveEpisode(map, { season: 1, episode: 1 }), null);
  assert.equal(resolveEpisode(map, { season: 1, episode: 2 }).still_path, '/real.jpg');
});

test('E04 missing preceding seasons or unknown lengths prevent fabricated global mappings', () => {
  const map = buildEpisodeMap({ seasons: [{ season_number: 1, episodes: [{ episode_number: 3 }] },
    { season_number: 2, episode_count: 2 }] });
  assert.equal(resolveEpisode(map, { season: 2, episode: 1 }).globalEpisode, undefined);
  assert.equal(resolveEpisode(map, { globalEpisode: 4 }), null);
  const missing = buildEpisodeMap({ seasons: [{ season_number: 2, episode_count: 2 }] });
  assert.deepEqual(coordinates(missing), [[2, 1, undefined], [2, 2, undefined]]);
  assert.equal(resolveEpisode(missing, { globalEpisode: 1 }), null);
});

test('E04 contradictory duplicate metadata and incomplete selectors do not resolve ambiguously', () => {
  const map = buildEpisodeMap({ seasons: [{ season_number: 1, episode_count: 1 },
    { season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 1 }] });
  assert.equal(resolveEpisode(map, { season: 1, episode: 1 }), null);
  assert.equal(resolveEpisode(map, { season: 2, episode: 1 }).globalEpisode, undefined);
  for (const query of [{}, { season: 1 }, { episode: 1 }, { globalEpisode: 0 },
    { season: -1, episode: 1 }, { globalEpisode: 1.5 }]) assert.equal(resolveEpisode(map, query), null);
  assert.deepEqual(buildEpisodeMap(undefined), []);
});

test('E11 Cat normalization preserves unknown flag, opaque ID, all dollars and original episode positions', async () => {
  const raw = { list: [{ vod_id: 'detail-a', vod_name: '真实剧集',
    vod_play_from: '神秘百度夸克-线路$$$普通直连',
    vod_play_url: '第1集$opaque$part$remain#特别篇$other-id$$$正片$https://media.invalid/video?token=synthetic',
  }] };
  const before = structuredClone(raw);
  const normalized = normalizeDetail(raw, { source });
  assert.equal(normalized.lines[0].id, '神秘百度夸克-线路');
  assert.equal(normalized.lines[0].label, '神秘百度夸克-线路');
  assert.equal(normalized.lines[0].provider, undefined);
  assert.equal(normalized.lines[0].episodes[0].id, 'opaque$part$remain');
  assert.equal(normalized.lines[0].episodes[0].index, 0);
  assert.equal(normalized.lines[0].episodes[1].index, 1);
  assert.strictEqual(normalized.raw, raw);
  assert.strictEqual(normalized.source, source);
  const items = (await recognize(raw));
  assert.equal(items[0].playId, 'opaque$part$remain');
  assert.equal(items[0].flag, '神秘百度夸克-线路');
  assert.equal(items[0].provider, undefined);
  assert.deepEqual(raw, before);
});

test('E12 runtime structured shares consume provider without rewriting the original play flag/ID', () => {
  const share = { provider: 'quark', shareId: 'shareABCD', url: 'https://pan.quark.cn/s/shareABCD',
    password: 'synthetic', installationId: source.installationId, siteId: source.siteId };
  const playId = encodePanResource(share);
  const raw = { list: [{ vod_id: 'detail-a', vod_play_from: '显示成移动盘的线路', vod_play_url: `分享$${playId}` }],
    _meow: { version: 1, resources: [{ kind: 'pan', provider: 'quark', installationId: source.installationId,
      siteId: source.siteId, vodIndex: 0, lineIndex: 0, episodeIndex: 0, id: playId }] } };
  const normalized = normalizeDetail(raw, { source });
  assert.equal(normalized.lines[0].provider, 'quark');
  assert.equal(normalized.lines[0].flag, '显示成移动盘的线路');
  assert.equal(normalized.lines[0].episodes[0].share.provider, 'quark');
  assert.equal(normalized.lines[0].episodes[0].playId, playId);
  assert.throws(() => normalizeDetail(raw, { source: { ...source, siteId: 'other-site' } }), { code: 'INVALID_DETAIL' });
});

test('E12 a known resource does not assign its provider to unknown siblings on the same Cat line', async () => {
  const share = { provider: 'quark', shareId: 'shareABCD', url: 'https://pan.quark.cn/s/shareABCD',
    password: '', installationId: source.installationId, siteId: source.siteId };
  const playId = encodePanResource(share);
  const items = (await recognize({ vod_id: 'detail-a', vod_play_from: '夸克线路',
    vod_play_url: `第1集$${playId}#第2集$opaque-still-native` }));
  assert.equal(items[0].provider, 'quark');
  assert.equal(items[1].provider, undefined);
  assert.equal(items[1].playId, 'opaque-still-native');
});

test('E11 structured pan files retain complete private references, original names and paths', async () => {
  const ref = file('fid-1', { name: '第2集.1080P.mkv', path: '/真实剧集/第二季/第2集.1080P.mkv' });
  const raw = { id: 'detail-a', provider: 'baidu', label: '错误盘名', files: [
    { name: '展示名', path: ref.path, file: ref },
  ] };
  const normalized = normalizeDetail(raw, { source });
  assert.equal(normalized.lines[0].provider, 'quark');
  assert.equal(typeof normalized.lines[0].episodes[0].id, 'string');
  const [item] = (await recognize(raw, { metadata }));
  assert.strictEqual(item.file, ref);
  assert.equal(item.provider, 'quark');
  assert.equal(item.name, '展示名');
  assert.equal(item.fileName, '第2集.1080P.mkv');
  assert.equal(item.path, ref.path);
  assert.equal(item.quality, '1080p');
  assert.deepEqual([item.season, item.episode, item.globalEpisode], [2, 2, 4]);
  assert.equal(item.playId, undefined);
  assert.equal(item.id.includes('synthetic'), false);
  assert.equal(item.id.includes('fid-1'), false);
});

test('E11 data.expand rows keep opaque row IDs separate from original script play IDs', async () => {
  const raw = normalizeDetail(detail([{ id: 'original-script-id', name: '原始分享' }]), { source });
  const expandedFile = file('expanded-fid', { name: 'S02E01.mkv', path: '/实际目录/S02E01.mkv' });
  raw.lines[0] = { ...raw.lines[0], expanded: true, episodes: [{
    id: 'opaque-expanded-row', provider: 'quark', file: expandedFile,
    name: expandedFile.name, path: expandedFile.path, index: 0,
  }] };
  const [item] = (await recognizeCandidates(raw, { source: { ...source, detailId: 'detail-a' }, target, metadata }));
  assert.equal(item.episodeId, 'opaque-expanded-row');
  assert.equal(item.playId, undefined);
  assert.strictEqual(item.file, expandedFile);
  assert.equal(item.source.detailId, 'detail-a');
  assert.deepEqual([item.season, item.episode, item.globalEpisode], [2, 1, 3]);
});

test('E11 custom rules reuse existing cleaning and replacement semantics with deterministic stateful regexes', async () => {
  const rules = { episodeCleanRegexRules: ['\\[[^\\]]*\\]'],
    episodeRules: [{ pattern: 'part(\\d+)', replace: 'E$1', flags: 'gi' }],
    movieRules: [{ pattern: '^FEATURE$', replace: '', flags: 'i' }], aggregateRegexRules: [] };
  const before = structuredClone(rules);
  const raw = detail([{ id: 'opaque-part', name: '[推广] part09', path: '/Season 02/[推广] part09.mkv' }]);
  for (let repeat = 0; repeat < 2; repeat += 1) {
    const [item] = (await recognize(raw, { rules, metadata: { seasons: [{ season: 1, episodes: 10 }, { season: 2, episodes: 10 }] } }));
    assert.deepEqual([item.season, item.episode, item.globalEpisode], [2, 9, 19]);
  }
  assert.deepEqual(rules, before);
  assert.equal((await createRecognitionRules(rules).movie('FEATURE')), true);
  assert.equal((await createRecognitionRules({ ...rules, movieRules: [] }).movie('FEATURE')), false);
  assert.throws(() => createRecognitionRules({ movieRules: ['secret-pattern'] }), { code: 'INVALID_RULES' });
});

test('E11 default Chinese/fullwidth markers and explicit specials are recognized through shared rules', async () => {
  const raw = detail([{ id: 'a', name: '真实剧集 第二季 第０３集.mkv' },
    { id: 'special', name: '真实剧集 S00E02.mkv' }]);
  const items = (await recognize(raw, { metadata: { seasons: [...metadata.seasons,
    { season_number: 0, episodes: [{ episode_number: 2 }] }] } }));
  assert.deepEqual([items[0].season, items[0].episode, items[0].globalEpisode], [2, 3, 5]);
  assert.deepEqual([items[1].season, items[1].episode, items[1].globalEpisode], [0, 2, undefined]);
});

test('E04 bare episode numbering is reliable for a known single season and remains unmapped for multiple seasons', async () => {
  const raw = detail([{ id: 'a', name: '第2集' }]);
  const [single] = (await recognize(raw, { metadata: { seasons: [{ season_number: 1, episode_count: 3 }] } }));
  assert.deepEqual([single.season, single.episode, single.globalEpisode], [1, 2, 2]);
  const [multi] = (await recognize(raw, { metadata }));
  assert.equal(multi.season, undefined);
  assert.equal(multi.episode, 2);
  assert.equal(multi.globalEpisode, undefined);
  assert.equal(isCandidateAllowed(multi, { target: { ...target, season: 2, episode: 2, globalEpisode: 4 } }), false);
  const [partial] = (await recognize(raw, { metadata: { number_of_seasons: 3,
    seasons: [{ season_number: 1, episode_count: 3 }] } }));
  assert.equal(partial.season, undefined);
});

test('E04 explicit global and manual season clues map only against real metadata coordinates', async () => {
  const [item] = (await recognize(detail([{ id: 'a', name: '原始名称', globalEpisode: 4 }]), { metadata }));
  assert.deepEqual([item.season, item.episode, item.globalEpisode], [2, 2, 4]);
  const [manual] = (await recognize(detail([{ id: 'a', name: '第2集' }], { season: 2 }), { metadata }));
  assert.deepEqual([manual.season, manual.episode, manual.globalEpisode], [2, 2, 4]);
  const [unknown] = (await recognize(detail([{ id: 'a', name: '原始名称' }]), { metadata }));
  assert.equal(unknown.episode, undefined);
  assert.equal(unknown.globalEpisode, undefined);
});

test('E11 file-level quality wins over misleading path/line labels and does not invent HDR from audio', async () => {
  const items = (await recognize(detail([
    { id: 'a', name: 'S01E01.1080P.mkv', path: '/广告4K/S01E01.1080P.mkv' },
    { id: 'b', name: 'S01E02.4K.DDP.H.265.mkv' },
    { id: 'c', name: 'S01E03.4K.HDR10.mkv' },
    { id: 'd', name: 'S01E04.4K.60fps.mkv' },
    { id: 'e', name: 'S01E05.mkv', quality: '720P' },
    { id: 'f', name: 'S01E06.mkv' },
  ], { label: '4K HDR全网资源' })));
  assert.deepEqual(items.map(item => item.quality), ['1080p', '4k', '4k_hdr', '4k_fps', '720p', undefined]);
});

test('E11 unsupported real quality labels are preserved without advertising a different grade', async () => {
  const items = (await recognize(detail([{ id: 'a', name: 'S01E01.4K.mkv', quality: '480P' },
    { id: 'b', name: 'S01E02.8K.mkv' }])));
  assert.deepEqual(items.map(item => item.quality), ['480P', '8K']);
});

test('E11 runtime media metadata remains resource-specific and overrides an advertised filename quality', async () => {
  const raw = { list: [{ vod_id: 'detail-a', vod_play_from: '普通', vod_play_url: 'S01E01.4K.mkv$opaque' }],
    _meow: { version: 1, media: [{ vodIndex: 0, quality: '1080P', fps: 24, filename: 'actual.mkv' }] } };
  const [item] = (await recognize(raw));
  assert.equal(item.quality, '1080p');
  assert.equal(item.fileName, 'actual.mkv');
});

test('E11 a detail-wide media label is not assigned to every file on a multi-file line', async () => {
  const items = (await recognize({ vod_id: 'detail-a', vod_play_from: '普通',
    vod_play_url: 'S01E01.720P.mkv$first#S01E02.1080P.mkv$second',
    _meow: { version: 1, media: [{ vodIndex: 0, quality: '4K', filename: 'one-unscoped-file.mkv' }] } }));
  assert.deepEqual(items.map(item => item.quality), ['720p', '1080p']);
  assert.deepEqual(items.map(item => item.fileName), ['S01E01.720P.mkv', 'S01E02.1080P.mkv']);
});

test('E11/E18 stable candidate IDs include source/detail/line/accurate file identity and ignore credential refresh', async () => {
  const raw = detail([{ name: 'S01E01.mkv', file: file('fid-1') },
    { name: 'S01E01.mkv', file: file('fid-2') }]);
  const items = (await recognize(raw));
  assert.notEqual(items[0].id, items[1].id);
  const reordered = (await recognize(detail([...raw.lines[0].episodes].reverse())));
  assert.deepEqual(reordered.map(item => item.id), [items[1].id, items[0].id]);
  const refreshed = (await recognize(detail([{ name: '改过的展示名', file: file('fid-1',
    { stoken: 'refreshed', fileToken: 'refreshed' }) }])))[0];
  assert.equal(refreshed.id, items[0].id);
  assert.notEqual((await recognize({ ...raw, id: 'different-detail' }))[0].id, items[0].id);
  assert.notEqual((await recognize(detail(raw.lines[0].episodes, { id: 'different-line' })))[0].id, items[0].id);
  assert.notEqual((await recognize(raw, { source: { ...source, installationId: 'other-install' } }))[0].id, items[0].id);
  assert.notEqual((await recognize(detail([{ name: 'S01E01.mkv', file: file('fid-1', { shareId: 'different-share' }) }])))[0].id,
    items[0].id);
});

test('E11 yidong content references remain exact without requiring another provider fileId', async () => {
  const ref = { provider: 'yidong', kind: 'share', accountRevision: 3, shareId: 'yd-share',
    coID: 'co-1', contentId: 'content-1', password: 'synthetic', name: 'S01E01.mp4', path: '/S01E01.mp4' };
  const [item] = (await recognize({ id: 'detail-a', provider: 'yidong', files: [{ name: ref.name, path: ref.path, file: ref }] }));
  assert.strictEqual(item.file, ref);
  assert.equal(item.provider, 'yidong');
});

test('E11 movie main features need no fabricated season/episode/global number', async () => {
  const movie = { id: 'douban:movie:1', kind: 'movie', title: '真实电影' };
  const items = (await recognize(detail([{ id: 'feature', name: '正片' },
    { name: '真实电影.2024.4K.HDR.mkv', file: file('movie-fid') },
    { id: 'tv-episode', name: 'S01E03.mkv' }]), { target: movie }));
  assert.deepEqual(items.map(item => item.name), ['正片', '真实电影.2024.4K.HDR.mkv']);
  for (const item of items) {
    assert.equal(item.kind, 'movie');
    for (const property of ['season', 'episode', 'globalEpisode']) assert.equal(Object.hasOwn(item, property), false);
    assert.equal(isCandidateAllowed(item, { target: movie }), true);
  }
});

test('E02 source targets never expand to a different source/detail with an identical title', async () => {
  const original = { id: 'source:original', kind: 'source', title: '同名剧集',
    source: { ...source, detailId: 'detail-a' } };
  const raw = detail([{ id: 'a', name: '第1集' }, { id: 'b', name: '原始未识别文件' }]);
  assert.equal((await recognize(raw, { target: original })).length, 2);
  assert.deepEqual((await recognize({ ...raw, id: 'detail-b' }, { target: original })), []);
  assert.deepEqual((await recognize(raw, { target: original, source: { ...source, key: 'different-source' } })), []);
});

test('E02 raw source targets select real episodes or opaque rows without metadata or fabricated numbering', async () => {
  const original = { id: 'source:original', kind: 'source', title: '原始条目',
    source: { ...source, detailId: 'detail-a' }, episode: 3 };
  const items = (await recognize(detail([{ id: 'raw-third', name: '第3集' },
    { id: 'opaque-unknown', name: '未知集号的真实文件' }]), { target: original }));
  assert.equal(isCandidateAllowed(items[0], { target: original }), true);
  assert.equal(items[0].season, undefined);
  assert.equal(items[0].globalEpisode, undefined);
  assert.equal(isCandidateAllowed(items[1], { target: original }), false);
  const selected = { ...original, episode: undefined,
    source: { ...original.source, episodeId: 'opaque-unknown' } };
  assert.equal(isCandidateAllowed(items[1], { target: selected }), true);
  assert.equal(isCandidateAllowed(items[0], { target: selected }), false);
});

test('E26 normalization and recognition retain the full list, including unrecognized later files', async () => {
  const episodes = Array.from({ length: 257 }, (_, index) => ({ id: `opaque-${index}`,
    name: index === 256 ? '未识别但真实存在的末项' : `S01E${index + 1}.mkv` }));
  const raw = detail(episodes);
  const normalized = normalizeDetail(raw, { source });
  const items = (await recognizeCandidates(normalized, { source, target }));
  assert.equal(normalized.lines[0].episodes.length, 257);
  assert.equal(items.length, 257);
  assert.equal(items[255].episode, 256);
  assert.equal(items[256].playId, 'opaque-256');
  assert.equal(items[256].episode, undefined);
});

test('E11 malformed/ambiguous details fail without leaking raw IDs or accepting the wrong record', async () => {
  for (const raw of [null, 'secret', { list: [] }, { list: [{ vod_id: 'a' }, { vod_id: 'b' }] },
    { vod_id: 'a', vod_play_from: 'one$$$two', vod_play_url: 'one-id' }]) {
    assert.throws(() => normalizeDetail(raw, { source }), error =>
      error.code === 'INVALID_DETAIL' && error.message === 'Invalid film playback detail');
  }
  const selected = normalizeDetail({ list: [
    { vod_id: 'a', vod_play_from: 'flag', vod_play_url: '第1集$one' },
    { vod_id: 'b', vod_play_from: 'flag', vod_play_url: '第1集$two' },
  ] }, { source: { ...source, detailId: 'b' } });
  assert.equal(selected.detailId, 'b');
  assert.equal(selected.lines[0].episodes[0].playId, 'two');
  (await assert.rejects(async () => (await recognize({ lines: [{ id: 'flag', label: 'flag',
    episodes: [{ id: 'secret', name: 'S01E01.mkv' }] }] })), { code: 'INVALID_DETAIL' }));
});
