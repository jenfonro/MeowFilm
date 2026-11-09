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
  assert.equal(output.items[236].rawTitle, '影片236x');
  assert.equal(output.items[236].poster, '/poster/236');
  assert.equal(output.page, 2);
  assert.equal(output.pageCount, 3);
  assert.equal(output.hasMore, true);
  assert.equal(output.explicitPageCount, true);
  assert.equal(display(output.items, { rawListMode: true }).items.length, 237);
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
