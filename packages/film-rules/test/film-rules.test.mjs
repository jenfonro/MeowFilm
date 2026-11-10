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
const initial = invoke({ operation: 'defaults' }).rules;
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

test('defaults seed two episode rows; saving empty groups preserves empty groups', () => {
  const first = invoke({ operation: 'defaults' });
  assert.equal(first.rules.episodeRules.length, 2);
  const saved = invoke({ operation: 'save', rules: {
    ...empty(), movieRules: [{ pattern: '[', replace: 'invalid but saved', flags: '' }]
  } });
  assert.equal(saved.rules.episodeRules.length, 0);
  assert.equal(saved.rules.episodeCleanRegexRules.length, 0);
  assert.equal(saved.rules.aggregateRegexRules.length, 0);
  assert.equal(saved.rules.movieRules[0].pattern, '[');
  assert.deepEqual(saved.rules, { ...empty(),
    movieRules: [{ pattern: '[', replace: 'invalid but saved', flags: '' }] });
});

test('empty episode matching keeps cleaned results for drafts and saved rules', () => {
  const process = createProgram();
  const rules = { ...empty(), episodeCleanRegexRules: [String.raw`^noise\s*`] };
  const before = JSON.stringify(rules);
  const request = { section: 'episode', text: 'noise E2' };
  const draft = process({ operation: 'process', request, rules });
  const saved = process({ operation: 'save', rules });
  assert.deepEqual(saved.rules, rules);
  const savedResult = process({ operation: 'process', request, rules: saved.rules });
  const expected = {
    section: 'episode', type: 'error', text: '无匹配规则', inputText: 'noise E2', processedText: 'E2',
    matched: false, matchedRuleIndex: null, rewriteApplied: false, rewrittenText: null,
    groupKey: '', trailingDigits: '', errors: [], previews: ['E2']
  };
  for (const result of [draft, savedResult]) {
    assert.deepEqual(result, expected);
    // FilmRuleResult.OutputText uses the same null-coalescing selection.
    assert.equal(result.rewrittenText ?? result.processedText, 'E2');
  }
  const control = process({ operation: 'process', request, rules: {
    ...rules, episodeRules: [{ pattern: 'E2', replace: '', flags: '' }]
  } });
  assert.equal(control.matched, true);
  assert.equal(control.processedText, 'E2');
  assert.equal(control.rewrittenText ?? control.processedText, 'E2');
  assert.deepEqual(control.previews, ['E2']);
  assert.equal(JSON.stringify(rules), before);
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

test('saved clean rules follow page trim then Go TrimSpace then response decode, without changing drafts', () => {
  const rows = ['\u0085foo\u0085', '\u0085', '\u0085\ufeffbar\ufeff\u0085',
    'a\u0085b', '\ufeff\u0085baz\u0085\ufeff', '\u200bkeep\u200b'];
  const draft = { ...empty(), episodeCleanRegexRules: rows, aggregateRegexRules: rows,
    movieRules: [{ pattern: '\u0085movie\u0085', replace: '\u0085keep\u0085', flags: '' }] };
  const before = JSON.stringify(draft);
  const request = { section: 'search', text: 'foo' };
  assert.equal(invoke({ operation: 'process', rules: draft, request }).processedText, 'foo');
  const saved = invoke({ operation: 'save', rules: draft });
  const decodedRows = ['foo', 'bar', 'a\u0085b', 'baz', '\u200bkeep\u200b'];
  for (const group of ['episodeCleanRegexRules', 'aggregateRegexRules']) {
    assert.deepEqual(saved.rules[group], decodedRows);
  }
  assert.deepEqual(saved.rules.movieRules, draft.movieRules);
  assert.equal(invoke({ operation: 'process', rules: saved.rules, request }).processedText, '');
  assert.deepEqual(invoke({ operation: 'restore', section: 'movie', rules: draft }).rules.aggregateRegexRules, decodedRows);
  assert.equal(JSON.stringify(draft), before);
});

test('search cleaning keeps its exact pictograph, regional indicator and selector behavior', () => {
  for (const [text, expected] of [
    ['星©™↔☀✨\u{1F004}\u{1F600}\u{1FAEA}\u{1F1E8}\u{1F1F3}河', '星河'],
    ['星\u2600\u2606\u2700\u2701\u27BF河', '星河'],
    ['星\uFE0E\uFE0F\u{E0100}河', '星\uFE0E\u{E0100}河'],
    ['星\u{1F3FB}\u{1F3FF}河', '星\u{1F3FB}\u{1F3FF}河'],
  ]) {
    const result = invoke({ operation: 'process', rules: empty(), request: { section: 'search', text } });
    assert.equal(result.type, 'success');
    assert.equal(result.processedText, expected, text);
    assert.equal(result.groupKey, expected, text);
    assert.deepEqual(result.errors, []);
  }
});

test('search cleaning preserves ordinary text and non-emoji astral characters', () => {
  const text = '普通文字ABCé\u{20000}\u{30000}\u{1D11E}\u{1F100}\u{1F700}\u{1F800}\u{1FA00}';
  const result = invoke({ operation: 'process', rules: empty(), request: { section: 'search', text } });
  assert.equal(result.processedText, text);
  assert.equal(result.groupKey, '普通文字abcé\u{20000}\u{30000}\u{1D11E}\u{1F100}\u{1F700}\u{1F800}\u{1FA00}');
  assert.deepEqual(result.errors, []);
});
