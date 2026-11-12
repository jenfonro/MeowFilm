import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const program = await readFile(new URL('../novel-rules.js', import.meta.url), 'utf8');
const fixtures = JSON.parse(await readFile(new URL('./fixtures.json', import.meta.url), 'utf8'));
function process(input, globals = {}) {
  const context = vm.createContext({ ...globals });
  vm.runInContext(program, context);
  context.input = structuredClone(input);
  return JSON.parse(vm.runInContext('JSON.stringify(meowfilmProcess(input))', context));
}
const book = (source, url, author = '林舟', name = '雨夜来信') => ({
  installationId: source, bookUrl: url, name, author,
});
const rule = fields => ({
  id: 'one', pattern: '广告', replacement: '', isEnabled: true, isRegex: false,
  scopeTitle: false, scopeContent: true, order: 0, timeoutMillisecond: 3000, ...fields,
});
const transform = fields => process({
  operation: 'transform', title: '广告·雨夜来信', blocks: [{ type: 'text', text: '正文广告' }],
  book: book('one', 'https://one.test/book'), source: { sourceUrl: 'https://one.test' },
  rules: [], conversion: 'none', ...fields,
});

for (const fixture of fixtures) {
  test(`cross-platform fixture: ${fixture.name}`, () => {
    assert.deepEqual(process(fixture.input), fixture.expected);
  });
}

test('identity follows Reader name and author normalization without using a transient row index', () => {
  const left = process({ operation: 'identity', book: book('one', '/a', '作者：林 舟著', '《雨夜来信》全文阅读') });
  const right = process({ operation: 'identity', book: book('two', '/b') });
  assert.deepEqual(left, right);
  assert.equal(left.name, '雨夜来信');
  assert.equal(left.author, '林舟');
  assert.notEqual(left.key, '');
});

test('unknown authors do not impersonate known books regardless of result arrival order', () => {
  const inputs = [book('one', '/a', '未知'), book('two', '/b'), book('three', '/c', '周南')];
  for (const books of [inputs, inputs.toReversed()]) {
    const result = process({ operation: 'merge', books });
    assert.equal(result.books.length, 3);
    assert.ok(result.books.every(item => item.candidates.length === 1));
  }
});

test('same known author and title group sources without losing source-specific locators', () => {
  const result = process({ operation: 'merge', books: [book('one', '/a'), book('two', '/b')] });
  assert.equal(result.books.length, 1);
  assert.deepEqual(result.books[0].candidates.map(item => [item.installationId, item.bookUrl]),
    [['one', '/a'], ['two', '/b']]);
});

test('unknown authors are deduplicated only by the full installation and book URL', () => {
  const result = process({ operation: 'merge', books: [
    book('one', '/a', ''), book('two', '/a', ''), { ...book('one', '/a', ''), latestChapterTitle: '第二章' },
  ] });
  assert.equal(result.books.length, 2);
  assert.equal(result.books[0].latestChapterTitle, '第二章');
});

test('enrichment updates one candidate without allowing a later unknown author to erase it', () => {
  const result = process({ operation: 'merge', books: [
    book('one', '/a', ''), book('two', '/b'), book('one', '/a'), book('one', '/a', '未知'),
  ] });
  assert.equal(result.books.length, 1);
  assert.equal(result.books[0].candidates.length, 2);
  assert.ok(result.books[0].candidates.every(item => item.author === '林舟'));
});

test('merge can accept previous grouped output plus a later page without duplicating candidates', () => {
  const previous = process({ operation: 'merge', books: [book('one', '/a'), book('two', '/b')] });
  const result = process({ operation: 'merge', books: [...previous.books, book('two', '/b'), book('three', '/c')] });
  assert.equal(result.books.length, 1);
  assert.equal(result.books[0].candidates.length, 3);
});

test('incomplete unidentified rows are never all collapsed into an empty identity', () => {
  const result = process({ operation: 'merge', books: [{ name: '雨夜来信' }, { name: '雨夜来信' }] });
  assert.equal(result.books.length, 2);
});

test('chapter matching tolerates Chinese numbering and punctuation but not an arbitrary same index', () => {
  const result = process({
    operation: 'matchChapter',
    chapters: [{ name: '卷一', isVolume: true }, { name: '第１２章：夜 雨', chapterUrl: '/new' }],
    anchor: { chapterTitle: '第十二章 夜雨', chapterIndex: 0, chapterUrl: '/old' },
  });
  assert.deepEqual(result, { index: 1, requiresSelection: false });
  assert.deepEqual(process({ operation: 'matchChapter', chapters: [{ name: '完全不同' }],
    anchor: { chapterTitle: '第十二章 夜雨', chapterIndex: 0 } }),
  { index: null, requiresSelection: true });
});

test('unique chapter URL takes precedence over a renamed title', () => {
  assert.deepEqual(process({ operation: 'matchChapter',
    chapters: [{ name: '修订后的标题', chapterUrl: '/chapter' }, { name: '旧标题', chapterUrl: '/other' }],
    anchor: { chapterTitle: '旧标题', chapterUrl: '/chapter' } }),
  { index: 0, requiresSelection: false });
});

test('duplicate titles, missing chapter and volume-only match require explicit selection', () => {
  for (const chapters of [
    [{ name: '序章' }, { name: '序章' }], [], [{ name: '序章', isVolume: true }],
  ]) {
    assert.deepEqual(process({ operation: 'matchChapter', chapters, anchor: { chapterTitle: '序章', chapterIndex: 99 } }),
      { index: null, requiresSelection: true });
  }
});

test('literal replacement keeps dollar substitutions literal and can delete the full content', () => {
  assert.equal(transform({ rules: [rule({ replacement: '$1' })] }).blocks[0].text, '正文$1');
  assert.equal(transform({ blocks: [{ type: 'text', text: '广告' }], rules: [rule({})] }).blocks[0].text, '');
});

test('regex captures are JavaScript replacements and execute on all matches', () => {
  const result = transform({
    blocks: [{ type: 'text', text: '章节12 章节34' }],
    rules: [rule({ pattern: '章节(\\d+)', replacement: '第$1章', isRegex: true })],
  });
  assert.equal(result.blocks[0].text, '第12章 第34章');
});

test('enabled, title/content switches and stable ordering are observable', () => {
  const result = transform({ title: 'A', blocks: [{ type: 'text', text: 'A' }], rules: [
    rule({ pattern: 'B', replacement: 'C', order: 2, scopeTitle: true }),
    rule({ pattern: 'A', replacement: 'B', order: 1, scopeTitle: true, scopeContent: false }),
    rule({ pattern: 'C', replacement: 'X', order: 3, isEnabled: false }),
  ] });
  assert.equal(result.title, 'C');
  assert.equal(result.blocks[0].text, 'A');
  assert.equal(transform({ rules: [
    rule({ pattern: '正文', replacement: '广告', order: 1 }),
    rule({ pattern: '广告', replacement: '', order: 1 }),
  ] }).blocks[0].text, '');
});

test('scope accepts exact source URL and normalized name lists; exclusion wins', () => {
  assert.equal(transform({ rules: [rule({ scope: '别的书；《雨夜来信》' })] }).blocks[0].text, '正文');
  assert.equal(transform({ rules: [rule({ scope: 'https://one.test' })] }).blocks[0].text, '正文');
  assert.equal(transform({ rules: [rule({ scope: 'https://one.test/other' })] }).blocks[0].text, '正文广告');
  assert.equal(transform({ rules: [rule({ scope: '雨夜来信', excludeScope: 'https://one.test' })] }).blocks[0].text, '正文广告');
});

test('cross-paragraph regex consumes text newlines but never rewrites image URLs or metadata', () => {
  const image = { type: 'image', src: 'https://images.test/广告.png', alt: '广告', width: 800 };
  const result = transform({ blocks: [
    { type: 'text', text: '第一段' }, { type: 'text', text: '广告第二段' }, image,
    { type: 'text', text: '正文广告' },
  ], rules: [rule({ pattern: '第一段\\n广告', replacement: '', isRegex: true }), rule({})] });
  assert.deepEqual(result.blocks, [{ type: 'text', text: '第二段' }, image, { type: 'text', text: '正文' }]);
});

test('conversion runs after cleanup and only transforms display text', () => {
  const result = transform({ title: '书', blocks: [
    { type: 'text', text: '小说广告' }, { type: 'image', src: '/图书.png', alt: '图书' },
  ], rules: [rule({})], conversion: 'traditional' });
  assert.equal(result.title, '書');
  assert.equal(result.blocks[0].text, '小說');
  assert.equal(result.blocks[1].src, '/图书.png');
  assert.equal(transform({ title: '讀書', conversion: 'simplified' }).title, '读书');
});

test('validation rejects invalid regex and nonpositive timeout but permits empty replacement', () => {
  assert.deepEqual(process({ operation: 'validateRule', rule: rule({}) }), { valid: true, errors: [] });
  assert.deepEqual(process({ operation: 'validateRule', rule: rule({ pattern: '[', isRegex: true }) }),
    { valid: false, errors: ['pattern'] });
  assert.deepEqual(process({ operation: 'validateRule', rule: rule({ timeoutMillisecond: 0 }) }),
    { valid: false, errors: ['timeoutMillisecond'] });
});

test('invalid enabled rule fails the operation rather than returning a partial chapter', () => {
  assert.throws(() => transform({ rules: [rule({}), rule({ pattern: '[', isRegex: true })] }), /INVALID_RULE/);
});

test('expired rule rejects a late result rather than reporting successful purification', () => {
  let tick = 0;
  assert.throws(() => process({
    operation: 'transform', title: '', blocks: [{ type: 'text', text: '广告' }],
    book: {}, source: {}, rules: [rule({ timeoutMillisecond: 1 })], conversion: 'none',
  }, { Date: { now: () => tick++ } }), /RULE_TIMEOUT/);
});

test('calls do not mutate input objects or retain book-dependent state', () => {
  const request = { operation: 'merge', books: [book('one', '/a'), book('two', '/b')] };
  const before = structuredClone(request);
  process(request);
  assert.deepEqual(request, before);
  assert.deepEqual(process({ operation: 'merge', books: [] }), { books: [] });
});

test('fixed URL matching requires the original complete match without changing JavaScript regex semantics', () => {
  for (const [pattern, url, expected] of [
    ['https://books\\.test/[中文]+', 'https://books.test/中文', true],
    ['https://books\\.test/.+', 'https://books.test/中文', true],
    ['books', 'https://books.test/', false],
    ['a|ab', 'ab', false],
    ['a(?:b)?', 'ab', true],
    ['(?<=a)b', 'ab', false],
    ['', '', true],
    ['.*', 'line\nbreak', false],
  ]) assert.equal(process({ operation: 'matchUrl', pattern, url }), expected);
  assert.throws(() => process({ operation: 'matchUrl', pattern: '[', url: 'https://books.test/' }),
    error => error.code === 'INVALID_ARGUMENT');
  assert.throws(() => process({ operation: 'evaluate', source: 'globalThis.arbitrary = true;' }),
    /UNSUPPORTED_OPERATION/);
});
