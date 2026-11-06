// One-off migration evidence. Reads the original Vue functions rather than
// keeping a second copy of their algorithms in this package.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';

const server = process.argv[2];
if (!server) throw new Error('Usage: node packages/film-rules/test/compare-server.mjs <Meowfilm server directory>');
const read = (relative) => readFileSync(path.join(server, relative), 'utf8');
const page = read('frontend/src/pages/dashboard/DashboardPage.vue');
const defaults = read('backend/internal/db/defaults.go');
const between = (text, start, end) => {
  const offset = text.indexOf(start);
  const finish = text.indexOf(end, offset + start.length);
  assert.ok(offset >= 0 && finish > offset, `reference markers: ${start}`);
  return text.slice(offset, finish);
};
const databaseRows = (table) => {
  const start = defaults.indexOf(`Table: "${table}"`);
  assert.ok(start >= 0, table);
  const block = between(defaults.slice(start), 'rules := []string{', '\n\t\t\t\t}');
  return [...block.matchAll(/`([^`]*)`/g)].map((match) => match[1]);
};
const document = {
  episodeCleanRegexRules: databaseRows('magic_episode_clean_regex_rule'),
  episodeRules: databaseRows('magic_episode_rule'),
  movieRules: databaseRows('magic_movie_rule'),
  aggregateRegexRules: databaseRows('magic_aggregate_regex_rule')
};
const refs = [
  'magicEpisodeCleanRegexRules', 'magicEpisodeRules', 'magicMovieRules', 'magicAggregateRegexRules',
  'magicEpisodeRuleTestInput', 'magicMovieRuleTestInput', 'magicAggregateRuleTestInput',
  'magicAggregateRuleTestQueryInput', 'magicEpisodeRuleTestOutput', 'magicMovieRuleTestOutput',
  'magicAggregateRuleTestOutput', 'magicShadowSmartSettings'
];
const marker = read('frontend/src/shared/episodeMarkerNormalize.js').replace(/^import[^\n]*\n/gm, '').replaceAll('export ', '');
const normalize = read('frontend/src/shared/normalize.js').replaceAll('export ', '');
const context = vm.createContext({});
vm.runInContext([
  'const computed = (get) => ({ get value() { return get(); } });',
  ...refs.map((name) => `const ${name} = { value: null };`),
  normalize,
  marker,
  between(page, 'const DEFAULT_EPISODE_RULES =', 'const isAdmin ='),
  between(page, 'function sanitizeMagicDisplayTitle(', 'const magicShadowSmartSettings ='),
  between(page, 'function setMagicTestOutput(', 'function buildDefaultSmartSourceRuleRows('),
  between(page, 'function normalizeMagicRuleRow(', 'function normalizeUserRow('),
  between(page, 'function applyMagicSettings(', 'async function loadMagicPanel('),
  between(page, 'function buildMagicSavePayload(', 'async function saveMagicBlock(')
].join('\n'), context);
// The three adjacent entry functions are followed by unrelated dashboard code.
vm.runInContext(between(page, 'function runMagicEpisodeRuleTest()', 'function runMagicMovieRuleTest()'), context);
vm.runInContext(between(page, 'function runMagicMovieRuleTest()', 'function runMagicAggregateRuleTest()'), context);
vm.runInContext(between(page, 'function runMagicAggregateRuleTest()', 'async function importVideoSourcesFromCatpawrunner()'), context);

vm.runInContext(`
  globalThis.readInitial = (document) => {
    applyMagicSettings(document);
    return readRules();
  };
  function readRules() {
    return {
      episodeCleanRegexRules: magicEpisodeCleanRegexRules.value,
      episodeRules: magicEpisodeRules.value,
      movieRules: magicMovieRules.value,
      aggregateRegexRules: magicAggregateRegexRules.value
    };
  }
  function setRules(rules) {
    magicEpisodeCleanRegexRules.value = rules.episodeCleanRegexRules;
    magicEpisodeRules.value = rules.episodeRules;
    magicMovieRules.value = rules.movieRules;
    magicAggregateRegexRules.value = rules.aggregateRegexRules;
  }
  globalThis.readRestored = () => ({
    episodeCleanRegexRules: DEFAULT_EPISODE_CLEAN_RULES,
    episodeRules: DEFAULT_EPISODE_RULES,
    movieRules: DEFAULT_MOVIE_RULES,
    aggregateRegexRules: DEFAULT_AGGREGATE_REGEX_RULES
  });
  globalThis.processReference = (request, rules) => {
    setRules(rules);
    if (request.section === 'episode') {
      magicEpisodeRuleTestInput.value = request.text;
      runMagicEpisodeRuleTest();
      return { ...magicEpisodeRuleTestOutput.value, previews: magicEpisodeCleanRulePreviews.value };
    }
    if (request.section === 'movie') {
      magicMovieRuleTestInput.value = request.text;
      runMagicMovieRuleTest();
      return { ...magicMovieRuleTestOutput.value, previews: [] };
    }
    magicAggregateRuleTestInput.value = request.text;
    magicAggregateRuleTestQueryInput.value = request.query;
    runMagicAggregateRuleTest();
    const normalized = runMagicAggregateNormalization({ title: request.text, query: request.query, rules: rules.aggregateRegexRules });
    return { ...magicAggregateRuleTestOutput.value, previews: magicAggregateRulePreviews.value,
      processedText: normalized.cleanedTitle, groupKey: normalized.groupKey, trailingDigits: normalized.trailingDigits };
  };
  globalThis.saveReference = (rules) => {
    setRules(rules);
    const payload = buildMagicSavePayload();
    applyMagicSettings(payload);
    return { document: payload, rules: readRules() };
  };
`, context);

const clone = (value) => JSON.parse(JSON.stringify(value));
const empty = { episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] };
const initial = clone(context.readInitial(document));
const restored = clone(context.readRestored());
const fixtures = JSON.parse(readFileSync(new URL('./reference-cases.json', import.meta.url), 'utf8'));
let compared = 0;
for (const fixture of fixtures) {
  const rules = { ...({ initial, restored, empty }[fixture.setup]), ...fixture.rules };
  const actual = clone(context.processReference(fixture.request, rules));
  for (const [property, expected] of Object.entries(fixture.expected)) {
    if (!(property in actual)) continue;
    assert.deepEqual(actual[property], expected, `${fixture.name}: server ${property}`);
    compared++;
  }
}

const program = vm.createContext({});
vm.runInContext(readFileSync(new URL('../film-rules.js', import.meta.url), 'utf8'), program);
const invoke = (request) => clone(program.meowfilmProcess(request));
const sharedInitial = invoke({ operation: 'initialize', document: null });
assert.deepEqual(sharedInitial.document, document, 'initial document matches exact database seed strings');
assert.deepEqual(sharedInitial.rules, initial, 'initial page decoding matches');
let sharedRestored = initial;
for (const section of ['episode', 'movie', 'search']) {
  sharedRestored = invoke({ operation: 'restore', section, rules: sharedRestored }).rules;
}
assert.deepEqual(sharedRestored, restored, 'restored defaults match all four page groups');
const draft = {
  episodeCleanRegexRules: ['  ', '  /foo/m ', 'foo', 'foo'],
  episodeRules: [{ pattern: String.raw` ^\\\\d+$ `, replace: '  $1  ', flags: ' g ' }, { pattern: ' ' }],
  movieRules: [{ pattern: '/movie/i', replace: ' value ', flags: '' }, { pattern: '[', replace: '', flags: '' }],
  aggregateRegexRules: ['  \\d+$  ', '']
};
const referenceSaved = clone(context.saveReference(draft));
const sharedSaved = invoke({ operation: 'save', rules: draft });
assert.deepEqual(sharedSaved.rules, referenceSaved.rules, 'full-panel save and response decoding match');
for (const group of Object.keys(empty)) {
  assert.deepEqual(sharedSaved.document[group], referenceSaved.document[group], `saved document ${group}`);
}
console.log(`PASS server dashboard fixed cases=${fixtures.length}; observable assertions=${compared}; exact initial/default/save chains=3`);
