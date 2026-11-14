import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import { detailCases, fallbackCases, siteCases, sources, tmdbCases } from './fixtures.mjs';

const program = vm.createContext({});
vm.runInContext(readFileSync(new URL('../film-search.js', import.meta.url), 'utf8'), program);
const clone = (value) => JSON.parse(JSON.stringify(value));
const invoke = (input) => clone(program.meowfilmProcess(clone(input)));
const site = (payload, overrides = {}) => invoke({
  operation: 'site', query: '星河', aggregateRules: [],
  site: sources[0], page: 1, payload, ...overrides,
});
const display = (siteItems, overrides = {}) => invoke({
  operation: 'display', query: '星河', aggregateRules: [], siteItems,
  tmdbItems: [], siteOrder: sources.map((source) => source.siteKey),
  searchCoverSite: '', rawListMode: false, previousOrder: [], ...overrides,
});
const mapTMDB = (query, results) => invoke({ operation: 'tmdb', query, payload: { results } });

test('all five JSON operations execute without mutating caller data', () => {
  for (const fixture of siteCases) {
    const input = { ...clone(fixture), operation: 'site' };
    const before = clone(input);
    program.meowfilmProcess(input);
    assert.deepEqual(input, before);
  }
  const input = {
    operation: 'display',
    siteItems: site({ list: [{ id: 'a', name: '星河' }] }).items,
    tmdbItems: mapTMDB('星河', [{ id: 1, media_type: 'tv', name: '星河' }]).items,
    siteOrder: sources.map((source) => source.siteKey),
  };
  const before = clone(input);
  const first = clone(program.meowfilmProcess(input));
  const second = clone(program.meowfilmProcess(input));
  assert.deepEqual(input, before);
  assert.deepEqual(first, second);
  assert.deepEqual(Object.keys(program), ['meowfilmProcess']);
  assert.throws(() => program.meowfilmProcess({ operation: 'missing' }), /Unknown film-search operation/);
});

test('site mapping preserves the complete valid page, including more than 100 rows', () => {
  const list = Array.from({ length: 237 }, (_, index) => ({
    vod_id: String(index), vod_name: `影片${String(index).padStart(3, '0')}x`,
    vod_pic: `/poster/${index}`, vod_remarks: '更新至5集',
  }));
  const output = site({ pagecount: '3', list: [...list, null, {}, { id: 'bad' }] }, { page: 2 });
  assert.equal(output.items.length, 237);
  assert.equal(new Set(output.items.map((item) => item.id)).size, 237);
  assert.equal(output.items[236].siteDetail, '236');
  assert.equal(output.items[236].detailId, '236');
  assert.deepEqual(output.items[236].raw, list[236]);
  assert.equal(output.items[236].rawTitle, '影片236x');
  assert.equal(output.items[236].poster, '/poster/236');
  assert.equal(output.page, 2);
  assert.equal(output.pageCount, 3);
  assert.equal(output.hasMore, true);
  assert.equal(output.explicitPageCount, true);
  assert.equal(display(output.items, { rawListMode: true }).items.length, 237);
});

test('site identities keep original JSON types and complete raw rows instead of display strings', () => {
  const list = [
    { vod_id: 0, id: 'ignored', vod_name: ' 星河 第二季 ', vod_remarks: ' \t第2季第4集 · 双语\r\n',
      vod_pic: ' /poster/zero ', source: { provider: 'fixture', flags: [0, '0', null] }, raw: { original: true } },
    { id: '0', name: '星河 第二季', remark: '\n第2季第5集\t', extra: { file: '/原目录/零.mkv' } },
    { vod_id: 7, vod_name: '星河 第二季' },
    { id: '7', name: '星河 第二季' },
    { id: '007', name: '星河 第二季' },
    { id: ' 7 ', name: '星河 第二季' },
    { id: Number.MAX_SAFE_INTEGER, name: '星河 第二季' },
    { id: Number.MIN_SAFE_INTEGER, name: '星河 第二季' },
    { id: '9007199254740993', name: '星河 第二季' },
    { vod_id: null, id: 'fallback', name: '星河 第二季', remark: ' \t ' },
  ];
  const before = clone(list);
  const output = site({ list });
  assert.deepEqual(output.items.map((item) => item.detailId),
    [0, '0', 7, '7', '007', ' 7 ', Number.MAX_SAFE_INTEGER, Number.MIN_SAFE_INTEGER, '9007199254740993', 'fallback']);
  assert.deepEqual(output.items.map((item) => item.siteDetail),
    ['0', '0', '7', '7', '007', '7', String(Number.MAX_SAFE_INTEGER), String(Number.MIN_SAFE_INTEGER), '9007199254740993', 'fallback']);
  assert.equal(new Set(output.items.map((item) => item.id)).size, list.length);
  assert.deepEqual(output.items.map((item) => item.raw), before);
  assert.equal(output.items[0].textBadge, '第2季第4集 · 双语');
  assert.equal(output.items[0].poster, '/poster/zero');
  assert.equal(output.items[0].rawTitle, '星河 第二季');
  assert.equal(output.items.at(-1).textBadge, '');
  assert.equal(output.items.at(-1).raw.remark, ' \t ');
  const raw = display(output.items, { rawListMode: true });
  assert.equal(raw.items.length, list.length);
  for (const original of output.items) {
    const item = raw.items.find((candidate) => candidate.detailId === original.detailId);
    assert.equal(item.id, original.id);
    assert.deepEqual(item.raw, original.raw);
  }
  assert.deepEqual(list, before);
});

test('only nonempty JSON strings and safe integer detail identities are admitted without alias coercion', () => {
  const valid = [
    { vod_id: null, id: 0, name: '星河' },
    { vod_id: '0', id: false, vod_name: '星河' },
    { id: -7, name: '星河' },
  ];
  const invalid = [
    ...[true, false, 1.5, Number.MAX_SAFE_INTEGER + 1, Number.MIN_SAFE_INTEGER - 1, {}, ['x'], '', ' \t ']
      .map((id) => ({ id, name: '星河' })),
    { vod_id: '', id: 'not-a-fallback', name: '星河' },
    { vod_id: false, id: 'not-a-fallback', name: '星河' },
    { vod_id: 1.5, id: 'not-a-fallback', name: '星河' },
    { id: null, name: '星河' }, { name: '星河' },
    { vod_id: 'empty-title', vod_name: '', name: 'not-a-fallback' },
  ];
  const output = site({ list: [...valid, ...invalid] });
  assert.deepEqual(output.items.map((item) => item.detailId), [0, '0', -7]);
  assert.deepEqual(output.items.map((item) => item.raw), valid);
  assert.equal(display(output.items, { rawListMode: true }).items.length, 3);
});

test('normal single-source cards retain raw remarks when the visible badge is normalized', () => {
  const row = { vod_id: 0, vod_name: ' 星河 第二季 ', vod_remarks: ' \t第2季第4集 · 双语\n',
    vod_play_from: '原始线路', vod_play_url: '原始不透明引用', sourceFields: { enabled: false, detail: '0' } };
  const mapped = site({ list: [row] }).items[0];
  const card = display([mapped]).items[0];
  assert.equal(card.aggregateItems, undefined);
  assert.equal(card.textBadge, '更新至第2季第4集');
  assert.equal(card.detailId, 0);
  assert.equal(card.siteDetail, '0');
  assert.equal(card.siteKey, sources[0].siteKey);
  assert.equal(card.installationId, sources[0].installationId);
  assert.equal(card.siteId, sources[0].siteId);
  assert.equal(card.generation, sources[0].generation);
  assert.deepEqual(card.raw, row);
  assert.equal(card.raw.vod_remarks, ' \t第2季第4集 · 双语\n');
  assert.deepEqual(display([mapped], { rawListMode: true }).items[0].raw, row);
  assert.equal(mapped.textBadge, '第2季第4集 · 双语');
});

test('raw, aggregate and TMDB members dedupe by typed detail identity without losing original source rows', () => {
  const rows = [
    { vod_id: 0, vod_name: '星河 第二季', vod_remarks: ' \t第2季第4集 · 双语\n',
      sourceFields: { file: '/原目录/数字零.mkv', flags: ['原始', 0] } },
    { vod_id: '0', vod_name: '星河 第二季', vod_remarks: '\n第2季第5集 \t', sourceFields: { token: null } },
    { id: 7, name: '星河 第二季', remark: ' \t ', unknown: [false, '原字段'] },
    { id: '7', name: '星河 第二季', remark: ' 第2季第6集 ', pic: ' /原图 ' },
  ];
  const a = site({ list: rows }).items;
  const b = site({ list: rows }, { site: sources[1] }).items;
  const duplicates = site({ list: rows.map((row) => ({ ...row, vod_name: '不应覆盖原行', vod_remarks: '重复备注' })) },
    { page: 2 }).items;
  const input = [...a, ...b, ...duplicates, a[0]];
  const before = clone(input);
  const raw = display(input, { rawListMode: true });
  const grouped = display(input);
  const tmdb = mapTMDB('星河', [{ id: 8, media_type: 'tv', name: '星河', poster_path: '/poster' }]).items;
  const merged = display(input, { tmdbItems: tmdb });
  const mergedRaw = display(input, { tmdbItems: tmdb, rawListMode: true });
  assert.equal(raw.items.length, 8);
  assert.equal(new Set(raw.order).size, 8);
  assert.equal(grouped.items.length, 1);
  assert.equal(grouped.items[0].aggregateKind, 'site');
  assert.equal(grouped.items[0].aggregateSourceCount, 2);
  assert.equal(grouped.items[0].textBadge, '更新至第2季第6集');
  assert.equal(merged.items.length, 1);
  assert.equal(merged.items[0].id, 'tmdb:tv:8');
  assert.equal(merged.items[0].aggregateKind, 'tmdb');
  assert.equal(merged.items[0].aggregateSourceCount, 2);
  assert.equal(merged.items[0].textBadge, '更新至第2季第6集');
  assert.equal(mergedRaw.items.length, 9);
  const memberLists = [
    raw.items, grouped.items[0].aggregateItems, merged.items[0].aggregateItems,
    mergedRaw.items.find((item) => item.sourceKind === 'tmdb').aggregateItems,
    mergedRaw.items.filter((item) => item.sourceKind === 'site'),
  ];
  for (const members of memberLists) {
    assert.equal(members.length, 8);
    for (const source of sources) {
      const actual = members.filter((item) => item.siteKey === source.siteKey);
      assert.deepEqual(actual.map((item) => item.detailId), [0, '0', 7, '7']);
      for (const [index, item] of actual.entries()) {
        assert.equal(item.installationId, source.installationId);
        assert.equal(item.siteId, source.siteId);
        assert.equal(item.generation, source.generation);
        assert.equal(item.siteName, source.siteName);
        assert.deepEqual(item.raw, rows[index]);
        assert.equal(item.rawTitle, '星河 第二季');
        assert.equal(item.textBadge, (rows[index].vod_remarks ?? rows[index].remark).trim());
      }
    }
  }
  assert.deepEqual(input, before);
  assert.equal(tmdb[0].aggregateItems, undefined);
});

test('page count metadata and empty unknown pages expose their actual continuation state', () => {
  const list = [{ id: 'a', name: '影片' }];
  const cases = [
    [{ pagecount: '2', list }, 1, 2, true],
    [{ pageCount: 2, list }, 2, 2, false],
    [{ pagecount: 'invalid', pageCount: '4', list }, 3, 4, true],
    [{ pagecount: 0, list }, 1, null, true],
    [{ pagecount: -2, list }, 1, null, true],
    [{ pagecount: 2.5, list }, 1, null, true],
    [{ pagecount: true, list }, 1, null, true],
    [{ list: [{ id: 'invalid' }] }, 1, null, false],
    [{ list: [] }, 1, null, false],
    [{ pagecount: 3, list: [] }, 1, 3, true],
  ];
  for (const [payload, page, pageCount, hasMore] of cases) {
    const actual = site(payload, { page });
    assert.equal(actual.page, page);
    assert.equal(actual.pageCount, pageCount);
    assert.equal(actual.hasMore, hasMore);
    assert.equal(actual.explicitPageCount, pageCount !== null);
  }
});

test('site projection does not treat repeated nonempty pages as consumer exhaustion', () => {
  const list = [{ vod_id: 0, vod_name: '星河' }, { vod_id: '0', vod_name: '星河' }];
  for (const payload of [{ list }, { list, pagecount: 3 }]) {
    const first = site(payload);
    const second = site(payload, { page: 2 });
    assert.equal(first.hasMore, true);
    assert.equal(second.hasMore, true);
    assert.equal(second.page, 2);
    assert.equal(second.pageCount, payload.pagecount ?? null);
    assert.equal(second.explicitPageCount, payload.pagecount !== undefined);
    assert.deepEqual(second.items.map((item) => item.detailId), [0, '0']);
    assert.deepEqual(second.items.map((item) => item.id), first.items.map((item) => item.id));
    assert.equal(display([...first.items, ...second.items], { rawListMode: true }).items.length, 2);
  }
});

test('full installation and source identity survives dedupe and aggregation', () => {
  const payload = { list: [{ id: 'same', name: '星河 第二季', remark: '第2季第4集' }] };
  const a = site(payload).items[0];
  const b = site(payload, { site: sources[1] }).items[0];
  const result = display([a, b, a]);
  assert.equal(result.items.length, 1);
  const card = result.items[0];
  assert.equal(card.aggregateKind, 'site');
  assert.equal(card.aggregateSourceCount, 2);
  assert.equal(card.aggregateItems.length, 2);
  assert.equal(card.siteDetail, 'same');
  assert.equal(card.textBadge, '更新至第2季第4集');
  for (const [index, item] of card.aggregateItems.entries()) {
    assert.equal(item.installationId, sources[index].installationId);
    assert.equal(item.siteId, 'shared');
    assert.equal(item.siteKey, sources[index].siteKey);
    assert.equal(item.generation, sources[index].generation);
    assert.equal(item.siteDetail, 'same');
    assert.equal(item.rawTitle, '星河 第二季');
    assert.equal(item.textBadge, '第2季第4集');
    assert.equal(item.seasonHint, 2);
    assert.equal(item.seasonHintLabel, '第2季');
  }
  const raw = display([a, b, a], { rawListMode: true });
  assert.equal(raw.items.length, 2);
  assert.equal(new Set(raw.order).size, 2);
  const c = site({ list: [{ id: 'a:b', name: '甲' }] }, { site: { ...sources[0], siteKey: 'c' } }).items[0];
  const d = site({ list: [{ id: 'b', name: '乙' }] }, { site: { ...sources[0], siteKey: 'c:a' } }).items[0];
  assert.notEqual(c.id, d.id);
  assert.equal(display([c, d], { rawListMode: true }).items.length, 2);
});

test('aggregation counts entries even when both entries come from the same source', () => {
  const items = site({ list: [
    { id: 'a', name: '星河 第1季', remark: '第1季第8集' },
    { id: 'b', name: '星河 第2季', remark: '第2季第3集' },
  ] }).items;
  const card = display(items).items[0];
  assert.equal(card.aggregateKind, 'site');
  assert.equal(card.siteLabel, '聚合');
  assert.equal(card.aggregateSourceCount, 1);
  assert.equal(card.aggregateItems.length, 2);
  assert.equal(card.textBadge, '更新至第2季第3集');
});

test('a normal card upgrades in place, updates its representative and appends new groups by round', () => {
  const firstItems = site({ list: [
    { id: 'a', name: '星河 第二季', remark: '第2季第3集' },
    { id: 'b', name: '遥远世界' },
  ] }).items;
  const first = display(firstItems);
  assert.equal(first.items[0].siteLabel, '甲源');
  assert.equal(first.items[0].aggregateKind, undefined);
  const secondItems = site({ list: [
    { id: 'c', name: '星河', remark: '第3季第4集' },
    { id: 'd', name: '星河新篇' },
    { id: 'e', name: '星河' + '外传' },
  ] }, { site: sources[1], page: 2 }).items;
  const second = display([...firstItems, ...secondItems], { previousOrder: first.order });
  assert.deepEqual(second.order.slice(0, 2), first.order);
  assert.equal(second.items[0].displayKey, first.items[0].displayKey);
  assert.equal(second.items[0].aggregateKind, 'site');
  assert.equal(second.items[0].siteDetail, 'c');
  assert.equal(second.items[0].title, '星河');
  assert.equal(second.items[0].aggregateItems.length, 2);
  assert.equal(second.items[0].textBadge, '更新至第3季第4集');
  assert.equal(second.items[1].title, '遥远世界');
  assert.deepEqual(second.items.slice(2).map((item) => item.title), ['星河新篇', '星河外传']);
  const thirdItems = site({ list: [{ id: 'f', name: '星河 第四季', remark: '第4季第1集' }] }, { page: 3 }).items;
  const third = display([...firstItems, ...secondItems, ...thirdItems], { previousOrder: second.order });
  assert.deepEqual(third.order, second.order);
  assert.equal(third.items[0].aggregateItems.length, 3);
  assert.equal(third.items[0].textBadge, '更新至第4季第1集');
});

test('raw and aggregate modes keep independent stable orders over multiple rounds', () => {
  const firstItems = site({ list: [{ id: 'a', name: '遥远世界' }] }).items;
  const first = display(firstItems);
  const firstRaw = display(firstItems, { rawListMode: true });
  const later = site({ list: [
    { id: 'b', name: '星河' }, { id: 'c', name: '星河 第二季' }, { id: 'a', name: '遥远世界' },
  ] }, { page: 2 }).items;
  const cumulative = [...firstItems, ...later];
  const second = display(cumulative, { previousOrder: first.order });
  const secondRaw = display(cumulative, { rawListMode: true, previousOrder: firstRaw.order });
  assert.equal(second.items[0].title, '遥远世界');
  assert.equal(second.items[1].aggregateItems.length, 2);
  assert.equal(secondRaw.items[0].title, '遥远世界');
  assert.deepEqual(secondRaw.items.map((item) => item.siteDetail), ['a', 'b', 'c']);
  assert.deepEqual(display(cumulative, { previousOrder: first.order }), second);
  assert.deepEqual(display(cumulative, { rawListMode: true, previousOrder: firstRaw.order }), secondRaw);
  // Before settlement, passing the preceding round's order lets late high scores
  // reorder only the current round. With no previous round, the first round sorts all.
  assert.equal(display(cumulative, { rawListMode: true }).items[0].siteDetail, 'b');
});

test('representative priority is relevance, title length, preferred cover, source order, then page sequence', () => {
  const a = site({ list: [{ id: 'a', name: '星河' }] }).items[0];
  const longer = site({ list: [{ id: 'b', name: '星河 第二季' }] }, { site: sources[1] }).items[0];
  assert.equal(display([longer, a], { searchCoverSite: sources[1].siteKey }).items[0].siteDetail, 'a');
  const b = site({ list: [{ id: 'b', name: '星河' }] }, { site: sources[1] }).items[0];
  assert.equal(display([b, a]).items[0].siteDetail, 'a');
  assert.equal(display([a, b], { searchCoverSite: sources[1].siteKey }).items[0].siteDetail, 'b');
  const nextPage = site({ list: [{ id: 'later', name: '星河' }] }, { page: 2 }).items[0];
  assert.equal(display([nextPage, a]).items[0].siteDetail, 'a');
  const equalScore = site({ list: [
    { id: 'long', name: 'A 星河长篇' }, { id: 'short', name: '星河长篇' },
  ] }, { query: 'unrelated', aggregateRules: ['/^A\\s*/'] }).items;
  assert.equal(display(equalScore).items[0].siteDetail, 'short');
});

test('pages beyond 100 rows keep deterministic source, page and row sequence without collisions', () => {
  const payload = { list: Array.from({ length: 151 }, (_, index) => ({
    id: String(index), name: '同名影片',
  })) };
  const a = site(payload, { site: { ...sources[0], order: 0 } }).items;
  const b = site(payload, { site: { ...sources[1], order: 1 } }).items;
  const result = display([...b, ...a], { siteOrder: [], rawListMode: true });
  assert.equal(result.items.length, 302);
  assert.ok(result.items.slice(0, 151).every((item) => item.siteKey === sources[0].siteKey));
  assert.deepEqual(result.items.slice(0, 151).map((item) => item.siteDetail), payload.list.map((item) => item.id));
  const page2 = site({ list: [{ id: 'next', name: '同名影片' }] }, { page: 2 }).items;
  const ordered = display([...page2, ...a], { rawListMode: true });
  assert.equal(ordered.items.at(-1).siteDetail, 'next');
});

test('fixed cleaning and scoring observations retain zero scores and original titles', () => {
  const output = invoke({ operation: 'site', ...siteCases[0] });
  assert.equal(output.items[0].rawTitle, '✨星河 第二季[高清]');
  assert.equal(output.items[0].aggregateContentKey, '星河');
  assert.equal(output.items[1].groupKey, '星河');
  assert.equal(output.items[1].score, 1000);
  assert.equal(output.items[2].seasonHintLabel, '年番3');
  assert.equal(output.items[4].seasonHint, 12);
  assert.equal(output.items[7].score, 0);
  const scored = site({ list: [
    { id: 'exact', name: 'TEST' }, { id: 'prefix', name: 'Test extended' },
    { id: 'infix', name: 'A test film' }, { id: 'none', name: 'other' },
  ] }, { query: 'test' }).items;
  assert.deepEqual(scored.map((item) => item.score), [1000, 900, 894, 0]);
  const tokens = site({ list: [{ id: 'x', name: 'alpha extra beta' }] }, { query: 'alpha beta' }).items;
  assert.equal(tokens[0].score, 640);
});

test('site cleaning keeps its exact pictograph, presentation emoji and selector behavior', () => {
  for (const [text, expected] of [
    ['星©™↔☀✨\u{1F004}\u{1F600}\u{1FAEA}\u{1F1E8}\u{1F1F3}河', '星河'],
    ['星\u2600\u2606\u2700\u2701\u27BF河', '星\u2606\u2700\u2701河'],
    ['星\uFE0E\uFE0F\u{E0100}河', '星\u{E0100}河'],
    ['星\u{1F3FB}\u{1F3FF}河', '星河'],
  ]) {
    const output = site({ list: [{ id: 'emoji', name: text }] });
    assert.equal(output.items.length, 1);
    assert.equal(output.items[0].rawTitle, text);
    assert.equal(output.items[0].title, text);
    assert.equal(output.items[0].aggregateContentKey, expected, text);
    assert.equal(output.items[0].groupKey, expected, text);
  }
});

test('site cleaning preserves ordinary text and non-emoji astral characters', () => {
  const text = '普通文字ABCé\u{20000}\u{30000}\u{1D11E}\u{1F100}\u{1F700}\u{1F800}\u{1FA00}';
  const output = site({ list: [{ id: 'text', name: text }] });
  assert.equal(output.items.length, 1);
  assert.equal(output.items[0].rawTitle, text);
  assert.equal(output.items[0].aggregateContentKey, text);
  assert.equal(output.items[0].groupKey, '普通文字abcé\u{20000}\u{30000}\u{1D11E}\u{1F100}\u{1F700}\u{1F800}\u{1FA00}');
});

test('TMDB keeps first type plus group occurrence, numeric ID, original rank, and movie identities', () => {
  const actual = invoke({ operation: 'tmdb', ...tmdbCases[0] });
  assert.deepEqual(actual.items.map((item) => item.tmdbId), [10, 12, 13, 14, 18]);
  assert.deepEqual(actual.items.map((item) => item.tmdbRank), [1, 3, 4, 5, 10]);
  assert.equal(actual.items[0].tmdbType, 'tv');
  assert.equal(actual.items[1].siteLabel, '电影');
  assert.equal(actual.items[1].textBadge, '2023');
  assert.equal(actual.items[0].textBadge, '');
  assert.equal(actual.pinTmdbFirst, true);
  const tokens = invoke({ operation: 'tmdb', ...tmdbCases[1] });
  assert.deepEqual(tokens.items.slice(0, 3).map((item) => item.score), [620, 620, 620]);
  const canonical = invoke({ operation: 'tmdb', ...tmdbCases[2] });
  assert.equal(canonical.items[0].score, 699);
  assert.equal(canonical.items[1].score, 0);
  assert.equal(canonical.items[2].score, 1000);
  const movies = invoke({ operation: 'tmdb', ...tmdbCases[3] });
  assert.deepEqual(movies.items.map((item) => item.tmdbId), [31, 33, 34]);
});

test('TMDB IDs reject invalid JSON identities without aliasing or suppressing valid integer results', () => {
  const invalidIDs = [18.9, 1.5, 0.9, -1.5, true, false, '18', '', null, {}, [18], 0, -1, Number.MAX_SAFE_INTEGER + 1];
  const validItem = mapTMDB('作品', [{ id: 18, media_type: 'tv', name: '作品' }]).items[0];
  for (const id of invalidIDs) {
    const output = mapTMDB('作品', [
      { id, media_type: 'movie', title: '作品' },
      { id: 18, media_type: 'movie', title: '作品' },
      { id, media_type: 'tv', name: '作品' },
      { id: 18, media_type: 'tv', name: '作品' },
    ]);
    assert.deepEqual(output.items.map((item) => [item.id, item.tmdbType, item.tmdbId, item.tmdbRank]), [
      ['tmdb:movie:18', 'movie', 18, 2],
      ['tmdb:tv:18', 'tv', 18, 4],
    ], JSON.stringify(id));
    const invalidItem = { ...validItem, tmdbId: id };
    assert.deepEqual(invoke({ operation: 'detail', item: invalidItem, payload: { name: '不可应用的详情' } }), invalidItem);
  }
  const boundary = mapTMDB('作品', [
    { id: 1, media_type: 'movie', title: '作品电影' },
    { id: Number.MAX_SAFE_INTEGER, media_type: 'tv', name: '作品剧集' },
  ]);
  assert.deepEqual(boundary.items.map((item) => item.tmdbId), [1, Number.MAX_SAFE_INTEGER]);
});

test('TMDB fallback is the exact trailing one-to-two ASCII digit query transformation', () => {
  for (const fixture of fallbackCases) {
    assert.deepEqual(invoke({ operation: 'fallback', query: fixture.query }), { query: fixture.expected });
  }
});

test('TMDB detail updates display data while preserving matching identity, score and original rank', () => {
  for (const fixture of detailCases) {
    const item = mapTMDB('原搜索标题', [{ id: 1, media_type: fixture.type, title: '原搜索标题', name: '原搜索标题' }]).items[0];
    const actual = invoke({ operation: 'detail', item, payload: fixture.payload, today: fixture.today });
    assert.equal(actual.textBadge, fixture.badge, fixture.name);
    for (const key of ['id', 'groupKey', 'score', 'seq', 'tmdbRank', 'tmdbId', 'tmdbType']) {
      assert.equal(actual[key], item[key], `${fixture.name}: ${key}`);
    }
    if (fixture.name.startsWith('movie')) {
      assert.equal(actual.title, 'Detail movie');
      assert.equal(actual.poster, '/detail');
      assert.equal(actual.backdrop, '/detail-back');
    }
    if (fixture.payload === null) assert.deepEqual(actual, item);
  }
});

test('TMDB cards actually return all matching sources in both modes and never duplicate a site group', () => {
  const a = site({ list: [{ id: 'a', name: '星河 第二季', remark: '第2季第9集' }] }).items[0];
  const b = site({ list: [{ id: 'b', name: '星河', remark: '第3季第1集' }] }, { site: sources[1] }).items[0];
  const tmdb = mapTMDB('星河', [{ id: 8, media_type: 'tv', name: '星河', poster_path: '/poster' }]).items;
  for (const rawListMode of [false, true]) {
    const output = display([a, b], { tmdbItems: tmdb, rawListMode });
    assert.equal(output.items.length, rawListMode ? 3 : 1);
    const card = output.items.find((item) => item.sourceKind === 'tmdb');
    assert.equal(card.id, 'tmdb:tv:8');
    assert.equal(card.displayKey, 'tmdb:tv:8');
    assert.equal(card.tmdbId, 8);
    assert.equal(card.tmdbType, 'tv');
    assert.equal(card.aggregateKind, 'tmdb');
    assert.equal(card.siteLabel, '剧集');
    assert.equal(card.textBadge, '更新至第3季第1集');
    assert.equal(card.aggregateSourceCount, 2);
    assert.deepEqual(card.aggregateItems.map((item) => item.siteDetail), ['a', 'b']);
    assert.deepEqual(card.aggregateItems.map((item) => item.rawTitle), ['星河 第二季', '星河']);
    assert.equal(card.aggregateItems[0].seasonHint, 2);
    assert.equal(card.aggregateItems[0].generation, 7);
    assert.equal(card.aggregateItems[1].installationId, 'install-b');
    for (const item of card.aggregateItems) {
      assert.equal(item.tmdbId, 0);
      assert.equal(item.tmdbType, '');
    }
    for (const item of output.items.filter((item) => item.sourceKind === 'site')) {
      assert.equal(item.title, '星河');
      assert.equal(item.tmdbId, 0);
      assert.equal(item.tmdbType, '');
    }
  }
  assert.equal(tmdb[0].aggregateItems, undefined);
});

test('same-title TMDB movie and TV cards stay distinct while ambiguous site sources keep their identity', () => {
  const siteItems = [
    site({ list: [{ id: 'movie-row', name: '同名作品', opaque: { source: 'a' } }] }).items[0],
    site({ list: [{ id: 0, name: '同名作品', opaque: { source: 'b' } }] }, { site: sources[1] }).items[0],
  ];
  const tmdbItems = mapTMDB('同名作品', [
    { id: 41, media_type: 'tv', name: '同名作品' },
    { id: 42, media_type: 'movie', title: '同名作品' },
  ]).items;

  for (const rawListMode of [false, true]) {
    const initial = display(siteItems, { tmdbItems: [tmdbItems[0]], rawListMode });
    assert.equal(initial.items.find((item) => item.sourceKind === 'tmdb').aggregateItems.length, 2);
    const output = display(siteItems, { tmdbItems, rawListMode, previousOrder: initial.order });
    const tmdbCards = output.items.filter((item) => item.sourceKind === 'tmdb');
    assert.deepEqual(tmdbCards.map((item) => [item.id, item.tmdbType, item.tmdbId]), [
      ['tmdb:tv:41', 'tv', 41],
      ['tmdb:movie:42', 'movie', 42],
    ]);
    assert.ok(tmdbCards.every((item) => item.aggregateItems === undefined));
    assert.ok(tmdbCards.every((item) => item.aggregateKind === undefined && item.aggregateSourceCount === undefined));
    assert.equal(new Set(output.order).size, output.items.length);
    assert.deepEqual(display(siteItems, {
      tmdbItems: tmdbItems.slice().reverse(), rawListMode, previousOrder: output.order,
    }), output);

    const siteCards = output.items.filter((item) => item.sourceKind === 'site');
    assert.ok(siteCards.every((item) => item.tmdbId === 0 && item.tmdbType === ''));
    if (rawListMode) {
      assert.equal(siteCards.length, 2);
      assert.deepEqual(siteCards.map((item) => [item.siteKey, item.detailId]), [
        [sources[0].siteKey, 'movie-row'],
        [sources[1].siteKey, 0],
      ]);
      assert.deepEqual(siteCards.map((item) => item.raw.opaque.source), ['a', 'b']);
      assert.ok(siteCards.every((item) => item.aggregateItems === undefined));
      for (const [index, item] of siteCards.entries()) {
        for (const key of ['id', 'siteKey', 'siteId', 'installationId', 'generation', 'siteName',
          'siteDetail', 'detailId', 'rawTitle', 'raw', 'seasonHint', 'seasonHintLabel']) {
          assert.deepEqual(item[key], siteItems[index][key], key);
        }
      }
    } else {
      assert.equal(siteCards.length, 1);
      assert.equal(siteCards[0].aggregateKind, 'site');
      assert.equal(siteCards[0].aggregateItems.length, 2);
      assert.equal(siteCards[0].aggregateSourceCount, 2);
      assert.deepEqual(siteCards[0].aggregateItems, siteItems);
      assert.deepEqual(siteCards[0].aggregateItems.map((item) => [item.siteKey, item.detailId, item.raw.opaque.source]), [
        [sources[0].siteKey, 'movie-row', 'a'],
        [sources[1].siteKey, 0, 'b'],
      ]);
    }
  }
});

test('title ambiguity uses TMDB type and ID even when movie and TV alias rules produce different groups', () => {
  for (const title of ['同名作品', '同名作品2']) {
    const siteItems = site({ list: [{ id: 'site', name: title }] }).items;
    for (const types of [['movie', 'tv'], ['movie', 'movie'], ['tv', 'tv']]) {
      const tmdbItems = types.flatMap((media_type, index) => mapTMDB(title, [{
        id: types[0] === types[1] ? index + 41 : 41, media_type, title, name: title,
      }]).items);
      for (const rawListMode of [false, true]) {
        const output = display(siteItems, { tmdbItems, rawListMode });
        assert.equal(output.items.length, 3);
        assert.deepEqual(output.items.filter((item) => item.sourceKind === 'tmdb').map((item) => item.id),
          tmdbItems.map((item) => item.id));
        assert.ok(output.items.every((item) => item.aggregateItems === undefined));
        const card = output.items.find((item) => item.sourceKind === 'site');
        assert.equal(card.id, siteItems[0].id);
        assert.equal(card.detailId, 'site');
        assert.deepEqual(card.raw, siteItems[0].raw);
      }
    }
  }
});

test('TMDB sources and badges update in place across rounds and older seasons cannot replace newer hints', () => {
  const tmdb = mapTMDB('星河', [{ id: 8, media_type: 'tv', name: '星河' }]).items;
  tmdb[0] = invoke({ operation: 'detail', item: tmdb[0], today: '2026-11-08', payload: {
    status: 'Continuing', seasons: [
      { season_number: 1, episode_count: 10 }, { season_number: 2, episode_count: 10 },
      { season_number: 3, episode_count: 10 },
    ], next_episode_to_air: { season_number: 3, episode_number: 8, air_date: '2026-11-08' },
  } });
  const firstItems = site({ list: [
    { id: 'a', name: '星河 第二季', remark: '第2季第20集' },
    { id: 'z', name: '旧位置' },
  ] }).items;
  const first = display(firstItems, { tmdbItems: tmdb });
  assert.equal(first.items[0].textBadge, '更新至第3季第8集');
  const more = site({ list: [{ id: 'b', name: '星河 第4季', remark: '第4季第2集' }] }, { site: sources[1], page: 2 }).items;
  const second = display([...firstItems, ...more], { tmdbItems: tmdb, previousOrder: first.order });
  assert.deepEqual(second.order, first.order);
  assert.equal(second.items[0].aggregateItems.length, 2);
  assert.equal(second.items[0].textBadge, '更新至第4季第2集');
  const unseasoned = site({ list: [
    { id: 'a', name: '星河', remark: '更新至18集' },
    { id: 'b', name: '星河', remark: '20/24' },
    { id: 'c', name: '星河 第2季', remark: '更新至99集' },
  ] }).items;
  assert.equal(display(unseasoned).items[0].textBadge, '更新至第20集');
});

test('TMDB pinning requires an exact match; otherwise relevance sorts before media kind', () => {
  const sites = site({ list: [{ id: 'site', name: '星河' }] }).items;
  const lower = mapTMDB('星河', [{ id: 5, media_type: 'movie', title: '星河番外' }]).items;
  assert.equal(display(sites, { tmdbItems: lower }).items[0].sourceKind, 'site');
  const exactAndOther = mapTMDB('星河', [
    { id: 5, media_type: 'movie', title: '星河' },
    { id: 6, media_type: 'movie', title: '无关电影' },
  ]).items;
  const result = display(site({ list: [{ id: 'b', name: '星河新篇' }] }).items, { tmdbItems: exactAndOther });
  assert.deepEqual(result.items.map((item) => item.sourceKind), ['tmdb', 'tmdb', 'site']);
  assert.deepEqual(result.items.slice(0, 2).map((item) => item.tmdbId), [5, 6]);
});
