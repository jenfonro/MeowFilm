import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../film-rules.js', import.meta.url), 'utf8');
const cases = JSON.parse(readFileSync(new URL('./reference-cases.json', import.meta.url), 'utf8'));
const empty = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
const createProgram = () => {
  const context = vm.createContext({});
  vm.runInContext(source, context);
  return (request) => JSON.parse(JSON.stringify(context.meowfilmProcess(request)));
};
const invoke = createProgram();
const initial = invoke({ operation: 'initialize', document: null }).rules;
let restored = initial;
for (const section of ['episode', 'movie', 'search']) {
  restored = invoke({ operation: 'restore', section, rules: restored }).rules;
}

for (const fixture of cases) {
  test(fixture.name, () => {
    const rules = { ...({ initial, restored, empty: empty() }[fixture.setup]), ...fixture.rules };
    const request = { operation: 'process', request: fixture.request, rules };
    for (let repeat = 0; repeat < 2; repeat += 1) {
      const result = invoke(request);
      for (const [property, expected] of Object.entries(fixture.expected)) {
        assert.deepEqual(result[property], expected, `${fixture.name}: ${property}`);
      }
    }
  });
}

test('initialization seeds only empty groups; saving empty groups does not immediately refill them', () => {
  const first = invoke({ operation: 'initialize', document: null });
  assert.equal(first.changed, true);
  assert.equal(first.rules.episodeRules.length, 2);
  const saved = invoke({ operation: 'save', rules: {
    ...empty(), movieRules: [{ pattern: '[', replace: 'invalid but saved', flags: '' }]
  } });
  assert.equal(saved.rules.episodeRules.length, 0);
  assert.equal(saved.rules.episodeCleanRegexRules.length, 0);
  assert.equal(saved.rules.aggregateRegexRules.length, 0);
  assert.equal(saved.rules.movieRules[0].pattern, '[');
  const reopened = invoke({ operation: 'initialize', document: saved.document });
  assert.equal(reopened.changed, true);
  assert.equal(reopened.rules.episodeRules.length, 2);
  assert.equal(reopened.rules.episodeCleanRegexRules.length, 6);
  assert.equal(reopened.rules.aggregateRegexRules.length, 7);
  assert.deepEqual(reopened.rules.movieRules, saved.rules.movieRules);
  assert.equal(invoke({ operation: 'initialize', document: reopened.document }).changed, false);
});

test('restoring one section saves edits in every other section', () => {
  const draft = {
    episodeCleanRegexRules: ['  custom clean  ', ''],
    episodeRules: [{ pattern: ' episode ', replace: ' E7 ', flags: ' g ' }],
    movieRules: [{ pattern: ' movie ', replace: ' movie output ', flags: '' }],
    aggregateRegexRules: ['  search  ', 'search']
  };
  const restoredEpisode = invoke({ operation: 'restore', section: 'episode', rules: draft });
  assert.equal(restoredEpisode.rules.episodeRules.length, 3);
  assert.equal(restoredEpisode.rules.episodeCleanRegexRules.length, 6);
  assert.deepEqual(restoredEpisode.rules.movieRules, [{ pattern: 'movie', replace: ' movie output ', flags: '' }]);
  assert.deepEqual(restoredEpisode.rules.aggregateRegexRules, ['search', 'search']);
  const restoredMovie = invoke({ operation: 'restore', section: 'movie', rules: draft }).rules;
  assert.deepEqual(restoredMovie.episodeCleanRegexRules, ['custom clean']);
  assert.deepEqual(restoredMovie.episodeRules, [{ pattern: 'episode', replace: ' E7 ', flags: 'g' }]);
  const restoredSearch = invoke({ operation: 'restore', section: 'search', rules: draft }).rules;
  assert.deepEqual(restoredSearch.movieRules, restoredEpisode.rules.movieRules);
  assert.equal(restoredSearch.aggregateRegexRules.length, 7);
});

test('new inputs parse slash flags and trim replacement, while saving edited rows keeps replacement whitespace', () => {
  assert.deepEqual(invoke({ operation: 'newMatch', pattern: String.raw` /\\d+/g `, replace: ' $1 ' }),
    { pattern: String.raw`\d+`, flags: 'g', replace: '$1' });
  assert.equal(invoke({ operation: 'newMatch', pattern: '  ', replace: '' }), null);
  assert.equal(invoke({ operation: 'newClean', pattern: String.raw` /\\d+/m ` }), String.raw`/\d+/m`);
  assert.equal(invoke({ operation: 'newClean', pattern: '/foo/' }), 'foo');
  const saved = invoke({ operation: 'save', rules: {
    ...empty(), movieRules: [{ pattern: '/foo/i', replace: '  text  ', flags: '' }, { pattern: ' ' }]
  } });
  assert.deepEqual(saved.rules.movieRules, [{ pattern: '/foo/i', replace: '  text  ', flags: '' }]);
});

test('draft matching does not perform save-time backslash normalization', () => {
  const draft = { ...empty(), movieRules: [{ pattern: String.raw`^\\d+$`, replace: 'number', flags: '' }] };
  const request = { section: 'movie', text: '123' };
  assert.equal(invoke({ operation: 'process', request, rules: draft }).text, '未命中');
  const saved = invoke({ operation: 'save', rules: draft });
  assert.equal(invoke({ operation: 'process', request, rules: saved.rules }).text, '命中第 1 条：number');
  assert.deepEqual(invoke({ operation: 'initialize', document: saved.document }).rules.movieRules, saved.rules.movieRules);
  assert.equal(draft.movieRules[0].pattern, String.raw`^\\d+$`);
});

test('processing does not mutate rules and changed content invalidates cached expressions', () => {
  const rules = { ...empty(), movieRules: [{ pattern: 'a', replace: 'x', flags: 'g' }] };
  const before = JSON.stringify(rules);
  const request = { section: 'movie', text: 'aaa' };
  assert.equal(invoke({ operation: 'process', request, rules }).rewrittenText, 'xxx');
  assert.equal(JSON.stringify(rules), before);
  rules.movieRules[0] = { pattern: 'b', replace: 'y', flags: 'g' };
  assert.equal(invoke({ operation: 'process', request, rules }).text, '未命中');
  rules.movieRules[0] = { pattern: 'a', replace: 'z', flags: '' };
  assert.equal(invoke({ operation: 'process', request, rules }).rewrittenText, 'zaa');
});

test('stored matching rows decode the reference JSON and literal forms', () => {
  const document = {
    ...invoke({ operation: 'initialize', document: null }).document,
    movieRules: [
      JSON.stringify({ pattern: String.raw` ^\\d+$ `, replace: ' $1 ', flags: 'g' }),
      '/foo/m', 'plain'
    ]
  };
  assert.deepEqual(invoke({ operation: 'initialize', document }).rules.movieRules, [
    { pattern: String.raw`^\d+$`, replace: ' $1 ', flags: 'g' },
    { pattern: 'foo', replace: '', flags: 'm' },
    { pattern: 'plain', replace: '', flags: '' }
  ]);
});
