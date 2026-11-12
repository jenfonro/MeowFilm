// Migration evidence: execute the actual functions at the fixed Git revision.
// There is no second implementation of the search algorithms in this runner.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { detailCases, fallbackCases, siteCases, sources, tmdbCases } from './fixtures.mjs';

const server = process.argv[2];
if (!server) throw new Error('Usage: node packages/film-search/test/compare-server.mjs <meowfilm-server repository>');
const revision = '934a03e5aeda7edbcef0cfb037734fed93bfe881';
const readReference = (name) => execFileSync('git', [
  '-C', server, 'show', `${revision}:Meowfilm/frontend/src/shared/${name}.js`,
], { encoding: 'utf8' });
const original = {
  normalize: readReference('normalize'),
  tmdbRaw: readReference('tmdbRaw'),
  search: readReference('searchRuntime'),
};
const withoutModules = (source) => source
  .replace(/^import[\s\S]*?from\s*['"][^'"]+['"];\s*/gm, '')
  .replace(/^export /gm, '');
const rawExports = [...original.tmdbRaw.matchAll(/^export const (\w+)/gm)].map((match) => match[1]);
const rawImports = [
  'buildTMDBDetailTextBadge', 'getTMDBBackdropPath', 'getTMDBDetailTitle',
  'getTMDBPosterPath', 'getTMDBSearchResults', 'getTMDBSearchTitle', 'getTMDBYear',
  'normalizeTMDBID', 'normalizeTMDBMediaType',
];
let currentPayload = null;
let currentSites = [];
let currentDetails = new Map();
let searchResponses = [];
let requestQueries = [];
const context = vm.createContext({
  URLSearchParams,
  fetchBootstrap: async () => ({ settings: {} }),
  requestCatSpider: async () => currentPayload,
  fetchTMDBDetailCached: async ({ type, id }) => {
    const value = currentDetails.get(`${type}:${id}`);
    if (value instanceof Error) throw value;
    return value;
  },
  requestJson: async (url) => {
    if (url === '/api/user/sites') return { sites: currentSites };
    const params = new URL(url, 'https://reference.invalid').searchParams;
    requestQueries.push(params.get('q'));
    const next = searchResponses.shift();
    if (next instanceof Error) throw next;
    assert.notEqual(next, undefined, 'the reference made an unexpected search request');
    return next;
  },
});
vm.runInContext(`
  const normalizationReference = (() => {
    ${withoutModules(original.normalize)}
    return { normalizeString, normalizeInt };
  })();
  const rawReference = (() => {
    const { normalizeString, normalizeInt } = normalizationReference;
    ${withoutModules(original.tmdbRaw)}
    return { ${rawExports.join(', ')} };
  })();
  globalThis.reference = (() => {
    const { normalizeString } = normalizationReference;
    const { ${rawImports.join(', ')} } = rawReference;
    ${withoutModules(original.search)}
    return {
      streamSearch, compileCleanRules, buildDisplayedResults, enrichTMDBItemWithDetail,
      buildTrailingDigitsFallbackQuery, requestTMDBSearchWithTrailingDigitsFallback,
      mapTmdbItems, computeMatchScoreFactory, buildSiteDisplayGroups,
      buildTMDBGroupMap, applyTMDBAggregateIntoCards,
    };
  })();
  globalThis.setReferenceToday = (today) => {
    const [year, month, day] = today.split('-').map(Number);
    const NativeDate = Date;
    globalThis.Date = class extends NativeDate {
      constructor(...args) { super(...(args.length ? args : [year, month - 1, day])); }
    };
  };
`, context);
const reference = context.reference;
const program = vm.createContext({});
vm.runInContext(readFileSync(new URL('../film-search.js', import.meta.url), 'utf8'), program);
const clone = (value) => JSON.parse(JSON.stringify(value));
const invoke = (input) => clone(program.meowfilmProcess(clone(input)));
const counters = { sites: 0, tmdb: 0, fallback: 0, detail: 0, display: 0, aggregateOutput: 0 };
const configFor = (rules = []) => ({
  searchDisplayMode: 'both',
  catpawrunnerApiBase: 'https://reference.invalid',
  settings: {},
  searchCoverSite: '',
  aggregateRules: reference.compileCleanRules(rules),
  siteOrderMap: vm.runInContext('new Map()', context),
});
const stripNativeAdaptation = (item) => {
  const {
    displayKey, siteId, installationId, generation, siteOrder, sitePage,
    detailId, raw,
    ...result
  } = clone(item);
  if (result.sourceKind === 'site') {
    if (result.aggregateKind !== 'site') result.id = `${result.siteKey}:${result.siteDetail}`;
    // First source, first page: its reference seq is the original page-row index.
    delete result.spiderApi;
  }
  if (result.aggregateItems) result.aggregateItems = result.aggregateItems.map(stripNativeAdaptation);
  return result;
};

for (const fixture of siteCases) {
  currentPayload = clone(fixture.payload);
  currentSites = [{
    key: fixture.site.siteKey, name: fixture.site.siteName,
    api: 'https://reference.invalid/spider', enabled: true, search: true,
  }];
  const config = configFor(fixture.aggregateRules);
  config.searchDisplayMode = 'sites';
  const expected = clone(await reference.streamSearch(fixture.query, config));
  const actual = invoke({ operation: 'site', ...fixture });
  for (const item of actual.items) {
    const row = fixture.payload.list.find((entry) => entry && (entry.vod_id ?? entry.id) === item.detailId);
    assert.notEqual(row, undefined, `${fixture.name}: typed source identity remains available`);
    assert.deepEqual(item.raw, row, `${fixture.name}: native handoff keeps the original row`);
  }
  assert.deepEqual(actual.items.map(stripNativeAdaptation), expected.siteItems.map(stripNativeAdaptation), fixture.name);
  counters.sites++;
}
console.log(`PASS exact reference site mapping cases=${counters.sites}`);

for (const fixture of tmdbCases) {
  const query = fixture.query.trim();
  const expected = clone(reference.mapTmdbItems(
    clone(fixture.payload), {}, reference.computeMatchScoreFactory(query), { rawQuery: query },
  ));
  const actual = invoke({ operation: 'tmdb', ...fixture });
  assert.deepEqual(actual.items, expected, fixture.name);
  assert.equal(actual.pinTmdbFirst, expected.some((item) => item.score >= 1000), fixture.name);
  counters.tmdb++;
}
console.log(`PASS exact reference TMDB map/score/dedupe/rank cases=${counters.tmdb}`);

for (const fixture of fallbackCases) {
  assert.equal(invoke({ operation: 'fallback', query: fixture.query }).query,
    reference.buildTrailingDigitsFallbackQuery(fixture.query), fixture.query);
  counters.fallback++;
}
const fallbackNetworkCases = [
  {
    name: 'nonempty raw results stop fallback even if the mapper rejects them',
    query: '星河2', responses: [{ results: [{ media_type: 'person', id: 1 }] }],
    queries: ['星河2'], used: false, effective: '星河2',
  },
  {
    name: 'empty results use the stripped query once',
    query: '星河2', responses: [{ results: [] }, { results: [{ media_type: 'tv', id: 1, name: '星河' }] }],
    queries: ['星河2', '星河'], used: true, effective: '星河',
  },
  {
    name: 'empty fallback retains the original empty response',
    query: '星河2', responses: [{ results: [], origin: 'raw' }, { results: [], origin: 'fallback' }],
    queries: ['星河2', '星河'], used: false, effective: '星河2',
  },
  {
    name: 'search failure does not invoke fallback',
    query: '星河2', responses: [new Error('reference search failed')],
    queries: ['星河2'], error: true,
  },
];
for (const fixture of fallbackNetworkCases) {
  searchResponses = [...fixture.responses];
  requestQueries = [];
  if (fixture.error) {
    await assert.rejects(() => reference.requestTMDBSearchWithTrailingDigitsFallback(fixture.query), /reference search failed/);
  } else {
    const result = clone(await reference.requestTMDBSearchWithTrailingDigitsFallback(fixture.query));
    assert.equal(result.usedFallback, fixture.used, fixture.name);
    assert.equal(result.effectiveQuery, fixture.effective, fixture.name);
    if (fixture.responses[0].origin) assert.equal(result.payload.origin, 'raw');
  }
  assert.deepEqual(requestQueries, fixture.queries, fixture.name);
}
console.log(`PASS exact reference fallback queries=${counters.fallback}; request-boundary cases=${fallbackNetworkCases.length} (reference only; host owns network)`);

for (const fixture of detailCases) {
  context.setReferenceToday(fixture.today);
  const item = invoke({
    operation: 'tmdb', query: '原搜索标题',
    payload: { results: [{ id: 1, media_type: fixture.type, title: '原搜索标题', name: '原搜索标题', poster_path: '/initial' }] },
  }).items[0];
  currentDetails = new Map([[`${fixture.type}:1`, fixture.payload]]);
  const expected = clone(await reference.enrichTMDBItemWithDetail(clone(item)));
  const actual = invoke({ operation: 'detail', item, payload: fixture.payload, today: fixture.today });
  assert.deepEqual(actual, expected, fixture.name);
  counters.detail++;
}
const failedDetailItem = invoke({ operation: 'tmdb', ...tmdbCases[0] }).items[0];
currentDetails = new Map([[`${failedDetailItem.tmdbType}:${failedDetailItem.tmdbId}`, new Error('single detail failed')]]);
assert.deepEqual(clone(await reference.enrichTMDBItemWithDetail(clone(failedDetailItem))), failedDetailItem);
console.log(`PASS exact reference detail/date/badge cases=${counters.detail}; single failure retains original item (reference only)`);

const displayCases = [
  {
    name: 'site-only representatives, cover precedence, full source count and season remarks',
    query: '星河', rules: [String.raw`\[[^\]]*\]`],
    pages: [
      { list: [
        { id: 'a', name: '星河 第二季', remark: '第2季第6集' },
        { id: 'b', name: '星河', remark: '更新至12集' },
        { id: 'c', name: '星河 外传' },
        { id: 'd', name: '遥远世界' },
      ] },
      { list: [
        { id: 'e', name: '星河', remark: '第3季第1集' },
        { id: 'f', name: '星河 外传' },
      ] },
    ],
    tmdb: [],
  },
  {
    name: 'TMDB merging, exact pin and no duplicate group cards',
    query: '星河', rules: [],
    pages: [
      { list: [
        { id: 'a', name: '星河 第二季', remark: '第2季第6集' },
        { id: 'b', name: '其他电影', remark: '2020' },
      ] },
      { list: [{ id: 'a', name: '星河 第3季', remark: '第3季第9集' }] },
    ],
    tmdb: [
      { id: 10, media_type: 'tv', name: '星河' },
      { id: 20, media_type: 'movie', title: '其他电影' },
    ],
  },
  {
    name: 'TMDB without exact score follows relevance',
    query: '星河', rules: [],
    pages: [{ list: [
      { id: 'a', name: '星河' }, { id: 'b', name: '无关作品' },
    ] }, { list: [] }],
    tmdb: [{ id: 10, media_type: 'movie', title: '星河 外传' }],
  },
  {
    name: 'raw-title display and unseasoned aggregate episode progress',
    query: '星河2', rules: [String.raw`\d+$`],
    pages: [{ list: [
      { id: 'a', name: '星河2', remark: '更新至11集' },
      { id: 'b', name: '星河', remark: '12/24' },
      { id: 'c', name: '星河 第二季', remark: '更新至99集' },
    ] }, { list: [] }],
    tmdb: [],
  },
  {
    name: 'movie and TV retain type identity when sharing a group',
    query: '星河', rules: [],
    pages: [{ list: [{ id: 'a', name: '星河', remark: '更新至2集' }] }, { list: [] }],
    tmdb: [{ id: 11, media_type: 'tv', name: '星河' }, { id: 12, media_type: 'movie', title: '星河' }],
  },
];

for (const fixture of displayCases) {
  const siteItems = fixture.pages.flatMap((payload, index) => invoke({
    operation: 'site', query: fixture.query, aggregateRules: fixture.rules,
    site: sources[index], page: 1, payload,
  }).items);
  const tmdbItems = invoke({ operation: 'tmdb', query: fixture.query, payload: { results: fixture.tmdb } }).items;
  for (const preferred of ['', sources[1].siteKey]) {
    const config = configFor(fixture.rules);
    config.searchCoverSite = preferred;
    // Construct Map in the reference VM so its actual instanceof Map contract is exercised.
    context.siteOrderInput = sources.map((source) => source.siteKey);
    config.siteOrderMap = vm.runInContext('new Map(siteOrderInput.map((key, index) => [key, index]))', context);
    for (const rawListMode of [false, true]) {
      const state = {
        siteItems: clone(siteItems), tmdbItems: clone(tmdbItems),
        pinTmdbFirst: tmdbItems.some((item) => item.score >= 1000), displayMode: 'both',
      };
      const expected = clone(reference.buildDisplayedResults(state, config, { rawListMode }));
      const actual = invoke({
        operation: 'display', query: fixture.query, aggregateRules: fixture.rules,
        siteItems, tmdbItems, siteOrder: sources.map((source) => source.siteKey),
        searchCoverSite: preferred, rawListMode, previousOrder: [],
      });
      // First-call reference output clones cards before mutating its cached TMDB
      // objects. Compare all unaffected fields, and validate the corrected source
      // output separately against the reference's own aggregate function below.
      const withoutAggregateEffect = (item) => {
        const result = stripNativeAdaptation(item);
        if (result.sourceKind === 'tmdb') {
          const base = tmdbItems.find((candidate) => candidate.id === result.id);
          delete result.aggregateItems;
          delete result.aggregateSourceCount;
          delete result.aggregateKind;
          result.textBadge = base.textBadge;
        }
        return result;
      };
      assert.deepEqual(actual.items.map(withoutAggregateEffect), expected.map(withoutAggregateEffect),
        `${fixture.name}; raw=${rawListMode}; cover=${preferred}`);
      counters.display++;

      const referenceTMDB = clone(tmdbItems);
      const byGroup = reference.buildTMDBGroupMap(referenceTMDB);
      const groups = reference.buildSiteDisplayGroups(clone(siteItems), { tmdbByGroup: byGroup, displayMode: 'both', context: config });
      reference.applyTMDBAggregateIntoCards(byGroup, groups);
      for (const item of actual.items.filter((candidate) => candidate.sourceKind === 'tmdb')) {
        const enhanced = referenceTMDB.find((candidate) => candidate.id === item.id);
        const { displayKey, ...withoutDisplayKey } = item;
        assert.deepEqual(withoutDisplayKey, clone(enhanced), `${fixture.name}: returned TMDB sources and badge; raw=${rawListMode}`);
        if (item.aggregateItems?.length) {
          const unenhanced = expected.find((candidate) => candidate.id === item.id);
          assert.equal(unenhanced.aggregateItems, undefined, 'first-call original output hides the cache mutation');
          assert.equal(item.aggregateItems.length, enhanced.aggregateItems.length);
          counters.aggregateOutput++;
        }
      }
    }
  }
}
console.log(`PASS exact reference display/representative/order/raw unaffected-field cases=${counters.display}`);
console.log(`PASS documented TMDB output correction cases=${counters.aggregateOutput}: returned cards include reference aggregate function results on the first call in either mode; cached inputs stay unchanged`);
console.log(`PASS source=${revision}; all fixed inputs compared against original searchRuntime.js/tmdbRaw.js/normalize.js`);
