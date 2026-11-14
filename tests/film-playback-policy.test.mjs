import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  compareCandidates, defaultPreferences, isCandidateAllowed, isSmartSiteAllowed, normalizePreferences,
} from '../packages/film-playback/src/policy.js';

const source = { key: 'cat:install:site', engine: 'cat', installationId: 'install', siteId: 'site',
  revision: 2, generation: 7, name: '电影站' };
const target = { id: 'tmdb:tv:12:1:2', kind: 'tv', title: '真实剧集', season: 1, episode: 2, globalEpisode: 2 };
const candidate = (patch = {}) => ({ id: 'candidate-a', fileIdentity: 'file-a', targetId: target.id,
  source, detailId: 'detail-a', lineId: 'line-a', name: '真实剧集 S01E02', kind: 'tv',
  season: 1, episode: 2, globalEpisode: 2, ...patch });
const sorted = (items, options) => [...items].sort((a, b) => compareCandidates(a, b, options)).map(item => item.id);

test('E36 default preferences contain only supported pan families and are independent copies', () => {
  const expected = { ruleOrder: ['quality', 'pan', 'keyword'],
    panOrder: ['yidong', 'tianyi', 'quark', 'uc', 'baidu'], keywords: [],
    siteCleanKeywords: ['直播', '体育', '短剧', '听书', '舞曲', '哔哩'], iosAutoPrepare: true };
  assert.deepEqual(defaultPreferences(), expected);
  const changed = defaultPreferences();
  changed.ruleOrder.reverse();
  changed.panOrder.push('foreign');
  changed.siteCleanKeywords.length = 0;
  assert.deepEqual(defaultPreferences(), expected);
});

test('E36 normalized preferences keep empty keyword groups and complete a partial pan order', () => {
  const value = { ruleOrder: ['keyword', 'pan', 'quality'], panOrder: ['baidu', 'quark'],
    keywords: ['  DDP ', 'H.265'], siteCleanKeywords: [], iosAutoPrepare: false };
  const before = structuredClone(value);
  const result = normalizePreferences(value);
  assert.deepEqual(result, { ...value, keywords: ['DDP', 'H.265'],
    panOrder: ['baidu', 'quark', 'yidong', 'tianyi', 'uc'] });
  assert.deepEqual(value, before);
  assert.deepEqual(normalizePreferences({ keywords: [], siteCleanKeywords: [] }).keywords, []);
  assert.deepEqual(normalizePreferences(), defaultPreferences());
});

test('E36 malformed preferences fail with a closed code and without echoing input', () => {
  for (const value of [null, [], 'secret', { ruleOrder: ['quality'] },
    { ruleOrder: ['quality', 'pan', 'pan'] }, { panOrder: ['115'] }, { panOrder: ['139'] },
    { panOrder: ['quark', 'quark'] }, { keywords: ['DDP', 'ddp'] }, { keywords: [''] },
    { keywords: [1] }, { iosAutoPrepare: 1 }]) {
    assert.throws(() => normalizePreferences(value), error =>
      error.code === 'INVALID_PREFERENCES' && error.message === 'Invalid film playback preferences');
  }
});

test('E35 smart site cleaning respects enabled/searchable and can be cleared without disabling sources', () => {
  const before = structuredClone(source);
  assert.equal(isSmartSiteAllowed(source), true);
  assert.equal(isSmartSiteAllowed({ ...source, name: '精选短剧资源' }), false);
  assert.equal(isSmartSiteAllowed({ ...source, name: '精选短剧资源' },
    normalizePreferences({ siteCleanKeywords: [] })), true);
  assert.equal(isSmartSiteAllowed({ ...source, name: 'Live TV' },
    normalizePreferences({ siteCleanKeywords: ['LIVE'] })), false);
  for (const patch of [{ enabled: false }, { searchable: false }, { searchable: 0 }, { enabled: 0 }]) {
    assert.equal(isSmartSiteAllowed({ ...source, ...patch }, normalizePreferences({ siteCleanKeywords: [] })), false);
  }
  assert.equal(isSmartSiteAllowed({ ...source, name: '括号[站' },
    normalizePreferences({ siteCleanKeywords: ['['] })), false);
  assert.deepEqual(source, before);
});

test('E36 each ordered dimension controls default selection and ties preserve encounter order', () => {
  const a = candidate({ id: 'high', quality: '4K·HDR', provider: 'baidu', name: 'H.265' });
  const b = candidate({ id: 'pan', quality: '720P', provider: 'quark', name: '普通' });
  const c = candidate({ id: 'word', quality: '1080P', provider: 'uc', name: 'DDP' });
  assert.deepEqual(sorted([b, c, a]), ['high', 'word', 'pan']);
  assert.deepEqual(sorted([a, c, b], { preferences: normalizePreferences({
    ruleOrder: ['pan', 'quality', 'keyword'], keywords: ['DDP', 'H.265'],
  }) }), ['pan', 'word', 'high']);
  assert.deepEqual(sorted([a, b, c], { preferences: normalizePreferences({
    ruleOrder: ['keyword', 'quality', 'pan'], keywords: ['DDP', 'H.265'],
  }) }), ['word', 'high', 'pan']);
  assert.equal(compareCandidates(candidate(), candidate({ id: 'different-identity' })), 0);
});

test('E36 pan order is explicit, including TV families, and labels never determine provider', () => {
  const preferences = normalizePreferences({ ruleOrder: ['pan', 'quality', 'keyword'], panOrder: ['baidu', 'quark'] });
  const items = [candidate({ id: 'misleading', lineLabel: '百度移动夸克', name: '百度文件' }),
    candidate({ id: 'quark-tv', provider: 'quark_tv' }), candidate({ id: 'baidu', provider: 'baidu' }),
    candidate({ id: 'unknown', provider: '139', lineLabel: '移动' })];
  assert.deepEqual(sorted(items, { preferences }), ['baidu', 'quark-tv', 'misleading', 'unknown']);
  assert.equal(isCandidateAllowed(items[0], { target, constraint: { action: 'pan', provider: 'baidu' } }), false);
  assert.equal(isCandidateAllowed(items[1], { target, constraint: { action: 'pan', provider: 'quark' } }), true);
  assert.equal(isCandidateAllowed(items[3], { target, constraint: { action: 'pan', provider: 'yidong' } }), false);
});

test('E36 keyword priority follows the first saved keyword across actual file text', () => {
  const preferences = normalizePreferences({ ruleOrder: ['keyword', 'quality', 'pan'], keywords: ['DDP', 'H.265'] });
  assert.deepEqual(sorted([
    candidate({ id: 'h265', name: 'h.265' }), candidate({ id: 'many', name: 'H.265 H.265 H.265' }),
    candidate({ id: 'ddp', name: '资源', path: '/原文件/ddp5.1 S01E02.mkv' }),
  ], { preferences }), ['ddp', 'h265', 'many']);
});

test('E16 fixed pan never admits another family and prefers current quality before higher quality', () => {
  const items = [candidate({ id: 'other', provider: 'baidu', quality: '1080P' }),
    candidate({ id: 'best', provider: 'quark', quality: '4K·HDR' }),
    candidate({ id: 'current-quality', provider: 'quark', quality: '1080P' })];
  const options = { constraint: { action: 'pan', provider: 'quark' }, current: { quality: '1080P' } };
  for (const searchComplete of [false, true]) {
    assert.deepEqual(sorted(items.filter(item => isCandidateAllowed(item, { target, ...options })),
      { ...options, searchComplete }), ['current-quality', 'best']);
  }
  assert.equal(isCandidateAllowed(items[0], { target, constraint: { action: 'pan', provider: '115' } }), false);
});

test('E17 fixed quality keeps current pan first and never silently downgrades', () => {
  const preferences = normalizePreferences({ panOrder: ['baidu', 'quark'] });
  const items = [candidate({ id: 'preferred-pan', provider: 'baidu', quality: '4K' }),
    candidate({ id: 'current-pan', provider: 'quark_tv', quality: '2160P' }),
    candidate({ id: 'wrong-quality', provider: 'quark', quality: '1080P' }),
    candidate({ id: 'different-quality', provider: 'quark', quality: '4K·HDR' })];
  const options = { preferences, constraint: { action: 'quality', quality: '4K' }, current: { provider: 'quark' } };
  const allowed = items.filter(item => isCandidateAllowed(item, { target, ...options }));
  assert.deepEqual(sorted(allowed, options), ['current-pan', 'preferred-pan']);
  assert.equal(isCandidateAllowed(items[2], { target, constraint: { action: 'quality', quality: 'unknown' } }), false);
});

test('E12/E17 switching quality can retain the exact current unknown line without guessing a provider', () => {
  const current = candidate({ lineId: 'unknown-line', quality: '720P' });
  const items = [candidate({ id: 'known-pan', provider: 'yidong', quality: '1080P' }),
    candidate({ id: 'same-line', lineId: 'unknown-line', quality: '1080P' }),
    candidate({ id: 'same-label-other-detail', lineId: 'unknown-line', detailId: 'other-detail', quality: '1080P' })];
  assert.deepEqual(sorted(items, { constraint: { action: 'quality', quality: '1080P' }, current }),
    ['same-line', 'known-pan', 'same-label-other-detail']);
});

test('E11/E17 unsupported observed quality labels remain outside the established selection grades', () => {
  const item = candidate({ quality: '8K' });
  assert.equal(isCandidateAllowed(item, { target, constraint: { action: 'quality', quality: '8K' } }), false);
  assert.equal(isCandidateAllowed(item, { target, constraint: { action: 'quality', quality: '4K' } }), false);
  assert.deepEqual(sorted([item, candidate({ id: 'known-grade', quality: '4K' })]), ['known-grade', item.id]);
});

test('E04 exact target and coordinate checks never infer a missing season or merge identical titles', () => {
  assert.equal(isCandidateAllowed(candidate(), { target }), true);
  for (const patch of [{ targetId: 'douban:tv:12' }, { episode: 1 }, { season: 2 },
    { globalEpisode: undefined }, { kind: 'movie' }]) {
    assert.equal(isCandidateAllowed(candidate(patch), { target }), false);
  }
  assert.equal(isCandidateAllowed(candidate({ season: undefined }), { target: { ...target, globalEpisode: undefined } }), false);
});

test('E02 raw source scope retains exact installation/site/detail/line/play identity', () => {
  const original = { id: 'original', kind: 'source', title: '同名', source: {
    ...source, detailId: 'detail-a', lineId: 'line-a', playId: 'opaque' } };
  const item = candidate({ kind: 'source', targetId: 'original', playId: 'opaque' });
  assert.equal(isCandidateAllowed(item, { target: original }), true);
  for (const patch of [{ detailId: 'detail-b' }, { lineId: 'line-b' }, { playId: 'different' },
    { source: { ...source, installationId: 'other' } }, { source: { ...source, generation: 8 } }]) {
    assert.equal(isCandidateAllowed({ ...item, ...patch }, { target: original }), false);
  }
  assert.equal(isCandidateAllowed(item, { target: { ...original, source: undefined } }), false);
});

test('E18 temporary exclusions apply to accurate files and preserve same-name alternatives', () => {
  const a = candidate();
  const other = candidate({ id: 'candidate-b', fileIdentity: 'file-b' });
  assert.equal(isCandidateAllowed(a, { target, constraint: { action: 'switch' }, excluded: new Set([a.id]) }), false);
  assert.equal(isCandidateAllowed(other, { target, excluded: new Set([a.id, a.fileIdentity]) }), true);
  assert.equal(isCandidateAllowed({ ...a, id: 'other-line' }, { target, excluded: [a] }), false);
  assert.equal(isCandidateAllowed(other, { target, excluded: [a] }), true);
});

test('E27 unresolved structured shares remain list inputs rather than playable file candidates', () => {
  assert.equal(isCandidateAllowed(candidate({ share: { provider: 'quark', shareId: 'synthetic-share' } }), { target }), false);
  assert.equal(isCandidateAllowed(candidate({ file: { provider: 'quark', fileId: 'synthetic-file' } }), { target }), true);
});

test('E19 persistent blocks match target/source/detail/line exactly, including movie targets', () => {
  const movieTarget = { id: 'douban:movie:12', kind: 'movie', title: '电影' };
  const item = candidate({ targetId: movieTarget.id, kind: 'movie', season: undefined,
    episode: undefined, globalEpisode: undefined });
  const block = { targetId: movieTarget.id, sourceKey: source.key, detailId: 'detail-a', lineId: 'line-a' };
  assert.equal(isCandidateAllowed(item, { target: movieTarget, blocks: [block] }), false);
  for (const patch of [{ targetId: 'tmdb:movie:12' }, { sourceKey: 'other-source' },
    { detailId: 'detail-b' }, { lineId: 'line-b' }]) {
    assert.equal(isCandidateAllowed(item, { target: movieTarget, blocks: [{ ...block, ...patch }] }), true);
  }
});

test('E02 raw opaque selection must match exactly and never falls back to matching title or typed selectors', () => {
  const item = candidate({ targetId: 'raw-opaque', kind: 'source', playId: 0, episodeId: 0 });
  const raw = { id: 'raw-opaque', kind: 'source', title: item.name,
    source: { ...source, detailId: item.detailId, lineId: item.lineId, candidateId: item.id } };
  assert.equal(isCandidateAllowed(item, { target: raw }), true);
  assert.equal(isCandidateAllowed(candidate({ ...item, id: 'same-name-replacement' }), { target: raw }), false);
  assert.equal(isCandidateAllowed({ ...item, id: undefined }, { target: raw }), false);
  assert.equal(isCandidateAllowed(item, { target: { ...raw,
    source: { ...raw.source, candidateId: 'gone', playId: 0, episodeId: 0 } } }), false);
  assert.equal(isCandidateAllowed(item, { target: { ...raw,
    source: { ...raw.source, candidateId: 0 } } }), false);
  for (const patch of [{ detailId: 'other' }, { lineId: 'other' }, { source: { ...source, generation: 8 } }]) {
    assert.equal(isCandidateAllowed({ ...item, ...patch }, { target: raw }), false);
  }
});

test('E02 legacy raw playId and episodeId retain exact JSON types without requiring an opaque selection', () => {
  const item = candidate({ targetId: 'raw-typed', kind: 'source', detailId: 0, lineId: 0, playId: 0, episodeId: 0 });
  const raw = { id: 'raw-typed', kind: 'source', title: 'Raw', source: { ...source, detailId: 0, lineId: 0,
    playId: 0, episodeId: 0 } };
  assert.equal(isCandidateAllowed(item, { target: raw }), true);
  for (const field of ['detailId', 'lineId', 'playId', 'episodeId']) {
    assert.equal(isCandidateAllowed(item, { target: { ...raw, source: { ...raw.source, [field]: '0' } } }), false);
  }
  assert.equal(isCandidateAllowed(item, { target: { ...raw, source: { ...raw.source, candidateId: item.id } } }), true);
  assert.equal(isCandidateAllowed(item, { target: { ...raw, source: { ...raw.source,
    candidateId: item.id, playId: '0' } } }), false);
});

test('E49 group membership is an accurate typed source/detail identity rather than a movie/tv guess or representative row', () => {
  const group = { id: 'group:known', kind: 'group', title: '原始内容',
    members: [{ sourceKey: source.key, detailId: 0 }, { sourceKey: 'other-source', detailId: 'actual' }] };
  const item = candidate({ targetId: group.id, kind: 'group', detailId: 0,
    season: undefined, episode: undefined, globalEpisode: undefined });
  assert.equal(isCandidateAllowed(item, { target: group }), true);
  assert.equal(isCandidateAllowed({ ...item, detailId: '0' }, { target: group }), false);
  assert.equal(isCandidateAllowed({ ...item, source: { ...source, key: 'not-admitted' } }, { target: group }), false);
  assert.equal(isCandidateAllowed({ ...item, kind: 'movie' }, { target: group }), false);
});

test('an exact group file uses its typed selector rather than requiring semantic membership again', () => {
  const selected = candidate({ targetId: 'group:directory', kind: 'group', detailId: 0 });
  const target = { id: selected.targetId, kind: 'group', title: '原组',
    members: [{ sourceKey: 'another-site', detailId: 'original' }],
    season: 1, episode: 2, globalEpisode: 2,
    source: { key: source.key, detailId: 0, lineId: selected.lineId, candidateId: selected.id } };
  assert.equal(isCandidateAllowed(selected, { target }), true);
  for (const patch of [{ id: 'forged' }, { detailId: '0' }, { lineId: 'other-line' },
    { episode: 3 }, { source: { ...source, key: 'other-source' } }])
    assert.equal(isCandidateAllowed({ ...selected, ...patch }, { target }), false);
  assert.equal(isCandidateAllowed(selected, { target, blocks: [{
    targetId: target.id, sourceKey: source.key, detailId: 0, lineId: selected.lineId,
  }] }), false);
});

test('E15–20 raw replacement policy bypasses only file/line locks for an explicitly proved same-episode operation', () => {
  const selected = candidate({ targetId: 'raw-reselect', kind: 'source', playId: 'a', episodeId: 'a' });
  const raw = { id: selected.targetId, kind: 'source', title: 'Raw', season: 1, episode: 2,
    source: { ...source, detailId: selected.detailId, lineId: selected.lineId, candidateId: selected.id,
      playId: selected.playId, episodeId: selected.episodeId } };
  const alternative = { ...selected, id: 'alternate', fileIdentity: 'another-file', lineId: 'other-pan',
    playId: 'b', episodeId: 'b' };
  assert.equal(isCandidateAllowed(alternative, { target: raw }), false, 'ordinary reopen remains exact');
  assert.equal(isCandidateAllowed(alternative, { target: raw, actualEpisode: selected }), true);
  for (const patch of [{ episode: 3 }, { season: 2 }, { detailId: 'another-detail' },
    { source: { ...source, key: 'another-source' } }, { globalEpisode: 999 }]) {
    assert.equal(isCandidateAllowed({ ...alternative, ...patch }, { target: raw, actualEpisode: selected }), false);
  }
  assert.equal(isCandidateAllowed(alternative, { target: raw, actualEpisode: selected,
    constraint: { action: 'pan', provider: 'baidu' } }), false);
  assert.equal(isCandidateAllowed(alternative, { target: raw, actualEpisode: selected,
    excluded: new Set([alternative.id]) }), false);
  assert.equal(isCandidateAllowed(alternative, { target: raw, actualEpisode: selected,
    blocks: [{ targetId: raw.id, sourceKey: source.key, detailId: alternative.detailId, lineId: alternative.lineId }] }), false);
});

test('E15 raw detail-local E01 evidence cannot admit E02, a known different season or unnumbered alternatives', () => {
  const selected = candidate({ targetId: 'raw-local', kind: 'source', season: undefined, episode: 1,
    globalEpisode: undefined });
  const raw = { id: selected.targetId, kind: 'source', title: 'Raw', episode: 1,
    source: { ...source, detailId: selected.detailId, candidateId: selected.id } };
  const other = { ...selected, id: 'b', fileIdentity: 'b', lineId: 'b' };
  assert.equal(isCandidateAllowed(other, { target: raw, actualEpisode: selected }), true);
  for (const patch of [{ episode: 2 }, { season: 2 }, { episode: undefined }])
    assert.equal(isCandidateAllowed({ ...other, ...patch }, { target: raw, actualEpisode: selected }), false);
  assert.equal(isCandidateAllowed(other, { target: raw,
    actualEpisode: { ...selected, episode: undefined } }), false);
});

test('E49 group replacement may cross an accurate member but not enlarge members or lose the normal exact selection', () => {
  const selected = candidate({ targetId: 'group:reselect', kind: 'group' });
  const group = { id: selected.targetId, kind: 'group', title: 'Group', season: 1, episode: 2,
    members: [{ sourceKey: source.key, detailId: selected.detailId }, { sourceKey: 'b', detailId: 0 }],
    source: { key: source.key, detailId: selected.detailId, lineId: selected.lineId, candidateId: selected.id } };
  const alternative = { ...selected, id: 'b', fileIdentity: 'b',
    source: { ...source, key: 'b', detailId: 0 }, detailId: 0, lineId: 'b' };
  assert.equal(isCandidateAllowed(alternative, { target: group }), false);
  assert.equal(isCandidateAllowed(alternative, { target: group, actualEpisode: selected }), true);
  for (const patch of [{ detailId: '0' }, { source: { ...alternative.source, key: 'c' } },
    { episode: 1 }, { season: undefined }]) {
    assert.equal(isCandidateAllowed({ ...alternative, ...patch }, { target: group, actualEpisode: selected }), false);
  }
  assert.equal(isCandidateAllowed(alternative, { target: group, actualEpisode: selected,
    blocks: [{ targetId: group.id, sourceKey: 'b', detailId: 0, lineId: 'b' }] }), false);
});
