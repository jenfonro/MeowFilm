import { setTestReadOnly, withTestDatabase } from './storage-test-fixture.mjs';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, existsSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { cloneJSONValue, parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { createRecognitionRules, filmRuleFailure } from '../packages/film-playback/src/rules.js';
import { matchesResults } from '../packages/film-playback/src/recognition.js';

const empty = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
function fixture(t) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'film-rules-owner-')), owners = [];
  function open() {
    const service = createFilmRulesService({ dataRoot });
    owners.push(service);
    return service;
  }
  const rows = () => withTestDatabase(dataRoot, db => db.snapshot([
    { sql: 'SELECT group_name,row_revision,overridden FROM film_standalone_rule_groups ORDER BY group_name', rows: true },
    { sql: 'SELECT * FROM film_standalone_rule_entries ORDER BY group_name,position', rows: true },
  ]));
  t.after(async () => { for (const owner of owners) await owner.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  return { service: open(), dataRoot, open, rows, overridden: async () => (await rows())[0].some(group => group.overridden) };
}
const request = { section: 'search', text: '[草稿]真实电影2', query: '真实电影2' };

test('native rule configuration reloads without a runtime writer or repeated save normalization', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'film-native-rules-owner-'));
  let document = { version: 1, rules: { ...empty(), episodeRules: [{ pattern: '\\\\\\\\d', replace: 'native', flags: 'g' }] } };
  const service = createFilmRulesService({ dataRoot, nativeSettings: { read: () => structuredClone(document) } });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  assert.deepEqual((await service.get()).rules, document.rules);
  assert.equal(existsSync(join(dataRoot, 'storage-v1')), false);
  await assert.rejects(service.set({ rules: empty() }), { code: 'PERMISSION_DENIED' });
  document = { version: 1, rules: { ...empty(), aggregateRegexRules: ['native-latest'] } };
  await service.settingsChanged();
  assert.deepEqual((await service.get()).rules, document.rules);
});

test('reading defaults, saving an empty override and clearing have distinct durable effects', async t => {
  const f = fixture(t), initial = await f.service.get();
  assert.equal(initial.rules.episodeRules.length, 2);
  assert.ok(Object.isFrozen(initial.rules.episodeRules[0]));
  assert.ok((await f.rows())[0].every(row => row.row_revision === 0 && !row.overridden));
  const saved = await f.service.setGroup({group: 'episode', rules: []});
  assert.notEqual(saved.revision, initial.revision);
  await f.service.close();
  const reopened = f.open();
  assert.deepEqual(await reopened.get(), saved);
  assert.deepEqual(saved.rules.episodeRules, []);
  const row = (await f.rows())[0].find(row => row.group_name === 'episodeRules');
  assert.deepEqual(row, {group_name:'episodeRules', row_revision:1, overridden:1});
  assert.deepEqual(await reopened.clear(), initial);
  await reopened.close();
  assert.deepEqual(await f.open().get(), initial);
  assert.equal(await f.overridden(), false);
});

test('independent rule owners merge different groups and reject stale same-group and full drafts', async t => {
  const f = fixture(t), first = f.service, second = f.open();
  await Promise.all([first.get(), second.get()]);
  await first.setGroup({group:'search',rules:['winner']});
  const merged = await second.setGroup({group:'movie',rules:[{pattern:'a',replace:'b',flags:'g'}]});
  assert.deepEqual(merged.rules.aggregateRegexRules,['winner']);
  assert.deepEqual(merged.rules.movieRules,[{pattern:'a',replace:'b',flags:'g'}]);
  await assert.rejects(first.setGroup({group:'movie',rules:[]}), {code:'STORAGE_CHANGED'});
  await assert.rejects(first.set({rules:empty()}), {code:'STORAGE_CHANGED'});
  assert.deepEqual(await first.get(),merged);
  assert.deepEqual(await f.open().get(),merged);
  const updated = await first.setGroup({group:'movie',rules:[]});
  assert.deepEqual(updated.rules.aggregateRegexRules,['winner']);
  assert.deepEqual(updated.rules.movieRules,[]);
});

test('draft preview and normalization use QuickJS without persisting or replacing saved rules', async t => {
  const f = fixture(t), draft = { ...empty(), aggregateRegexRules: ['\\[草稿\\]'] };
  assert.equal((await f.service.preview({ request, rules: draft })).processedText, '真实电影2');
  assert.equal(await f.overridden(), false);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'E2' } })).rewrittenText, 'E2');
  assert.equal(await f.overridden(), false, 'previewing defaults leaves all groups unmodified');
  assert.deepEqual(await f.service.normalize({ kind: 'match', pattern: ' /\\\\d+/gy ', replace: ' $1 ' }),
    { pattern: '\\d+', replace: '$1', flags: 'gy' });
  assert.equal(await f.service.normalize({ kind: 'clean', pattern: ' /a/m ' }), '/a/m');
  const snapshot = await f.service.get(), before = await f.rows();
  await f.service.preview({ request, rules: draft });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.deepEqual(await f.rows(), before);
});

test('preview and recognition consume only their actual section and ignore unused query and rule groups', async t => {
  const f = fixture(t);
  const rules = { ...empty(), movieRules: [{ pattern: '^Movie', replace: 'Film', flags: '' }],
    aggregateRegexRules: { unused: 'not a search draft' }, episodeRules: null };
  const result = await f.service.preview({ rules, request: { section: 'movie', text: 'Movie',
    query: { unused: [65536] }, source: 'globalThis.mustNotRun = true' } });
  assert.equal(result.rewrittenText, 'Film');
  const grouped = await f.service.previewGroup({ group: 'movie', rules: rules.movieRules,
    request: { section: 'movie', text: 'Movie', query: [65536] } });
  assert.equal(grouped.rewrittenText, 'Film');
  assert.equal(await f.service.normalize({ kind: 'clean', pattern: '/a/m', replace: { unused: true } }), '/a/m');
  assert.equal(await f.overridden(), false);
  assert.equal(await createRecognitionRules({ ...empty(), episodeRules: null }).movie('Movie'), false);
});

test('queued restore and preview capture consumed drafts before callers can mutate them', async t => {
  const f = fixture(t);
  await f.service.snapshot();
  const draft = { ...empty(), aggregateRegexRules: ['original'] };
  const restore = f.service.restore({ section: 'episode', rules: draft });
  draft.aggregateRegexRules[0] = 'changed';
  assert.deepEqual((await restore).rules.aggregateRegexRules, ['original']);

  const rules = [{ pattern: '^Movie$', replace: 'Film', flags: '' }];
  const text = [...'Movie'].map(character => character.charCodeAt(0));
  const preview = f.service.previewGroup({ group: 'movie', rules,
    request: { section: 'movie', text } });
  rules[0].replace = 'changed'; text[0] = 'X'.charCodeAt(0);
  assert.equal((await preview).rewrittenText, 'Film');
});

test('preview and restore pass exact numeric draft extensions to the real rule program without changing saved rule projection', async t => {
  const f = fixture(t), seen = [], invoke = NativeJsonProgram.prototype.invoke;
  t.mock.method(NativeJsonProgram.prototype, 'invoke', function(input, options) {
    if (['preview', 'restore'].includes(input.operation)) seen.push(cloneJSONValue(input));
    return invoke.call(this, input, options);
  });
  const extensionText = '{"integer":9007199254740993,"decimal":1.0000000000000001,"large":1e400}';
  const extension = parseJSONText(extensionText);
  const rows = [{ pattern: '^Movie$', replace: 'Film', flags: '', extension }];
  assert.equal((await f.service.previewGroup({ group: 'movie', rules: rows,
    request: { section: 'movie', text: 'Movie' } })).rewrittenText, 'Film');
  assert.equal(stringifyJSONValue(seen[0].rules.movieRules[0].extension), extensionText);
  assert.equal(await f.overridden(), false);
  const restored = await f.service.restore({ section: 'episode',
    rules: { ...empty(), movieRules: rows, extension } });
  assert.equal(stringifyJSONValue(seen[1].rules.extension), extensionText);
  assert.equal(stringifyJSONValue(seen[1].rules.movieRules[0].extension), extensionText);
  assert.deepEqual(restored.rules.movieRules, [{ pattern: '^Movie$', replace: 'Film', flags: '' }]);
  assert.deepEqual(await f.open().get(), restored);
  assert.equal(stringifyJSONValue(extension), extensionText);
});

test('a failed rule write preserves both the committed snapshot and its group revisions', async t => {
  const f = fixture(t), previous = await f.service.get(), rows = await f.rows();
  await setTestReadOnly(f.dataRoot, true);
  await assert.rejects(f.service.set({ rules: empty() }), { code: 'STORAGE_ERROR', message: 'STORAGE_ERROR' });
  assert.strictEqual(await f.service.get(), previous);
  assert.deepEqual(await f.rows(), rows);
  await setTestReadOnly(f.dataRoot, false);
  assert.deepEqual((await f.service.set({ rules: empty() })).rules, empty());
});

test('corrupt rule group metadata fails explicitly and no defaults replace the damaged state', async t => {
  const f = fixture(t);
  await withTestDatabase(f.dataRoot, db => db.execute("DELETE FROM film_standalone_rule_groups WHERE group_name='movieRules'"));
  const rows = await f.rows();
  await assert.rejects(f.service.get(), {code:'CORRUPT_STORAGE'});
  await assert.rejects(f.service.set({rules:empty()}), {code:'CORRUPT_STORAGE'});
  assert.deepEqual(await f.rows(), rows);
  await withTestDatabase(f.dataRoot, db => db.execute("INSERT INTO film_standalone_rule_groups(group_name) VALUES('movieRules')"));
  assert.equal((await f.service.get()).rules.episodeRules.length, 2);
});

test('restore saves the full draft and only restores the selected section', async t => {
  const f = fixture(t), initial = await f.service.snapshot();
  const draft = { ...empty(), aggregateRegexRules: [' custom '],
    movieRules: [{ pattern: ' movie ', replace: ' output ', flags: ' g ' }] };
  const restored = await f.service.restore({ section: 'episode', rules: draft });
  assert.equal(restored.rules.episodeRules.length, 3);
  assert.deepEqual(restored.rules.aggregateRegexRules, ['custom']);
  assert.deepEqual(restored.rules.movieRules, [{ pattern: 'movie', replace: ' output ', flags: 'g' }]);
  const search = await f.service.restore({ section: 'search' });
  assert.deepEqual(search.rules.aggregateRegexRules, initial.rules.aggregateRegexRules);
  assert.deepEqual(search.rules.movieRules, restored.rules.movieRules);
});

test('two immutable snapshots compute concurrently and a new save affects only newly captured tasks', async t => {
  const f = fixture(t);
  const first = await f.service.set({ rules: { ...empty(), aggregateRegexRules: ['\\[草稿\\]'],
    episodeRules: [{ pattern: 'part(\\d+)', replace: 'E$1', flags: 'gy' }] } });
  const second = await f.service.set({ rules: empty() });
  assert.notEqual(first.revision, second.revision);
  assert.throws(() => first.rules.aggregateRegexRules.push('.*'), TypeError);
  const selected = { kind: 'movie', title: '真实电影2' };
  const rows = [{ title: '[草稿]真实电影2' }, { title: '真实电影3' }];
  for (let repeat = 0; repeat < 2; repeat++) {
    const values = await Promise.all([
      matchesResults(rows, { target: selected, rules: first }),
      matchesResults(rows, { target: selected, rules: second }),
      createRecognitionRules(first).batch([{ kind: 'episode', text: 'part09' }, { kind: 'episode', text: 'part09' }]),
    ]);
    assert.deepEqual(values, [[true, false], [false, false], [{ episode: 9 }, { episode: 9 }]]);
  }
});

test('metadata hints and row-specific matching run as full QuickJS batches', async t => {
  const f = fixture(t), rules = await f.service.snapshot(), recognition = createRecognitionRules(rules);
  const rows = [{ title: 'ＡＣＴＵＡＬ 第二季', year: '2024' }, { name: '另一部 第一季', year: 2023 }];
  const hints = await recognition.batch(rows.map(record => ({ kind: 'metadata', record, contentKind: 'tv' })));
  assert.deepEqual(hints.map(({ query, year, season }) => [query, year, season]),
    [['ACTUAL', 2024, 2], ['另一部', 2023, 1]]);
  assert.deepEqual(await recognition.batch(rows.map(record => ({ kind: 'match', record,
    target: { kind: 'tv', title: 'ACTUAL', season: 2, year: 2024 } }))), [true, false]);
});

test('real previews retain Unicode properties, lookarounds, captures and exact isolated UTF16 arrays', async t => {
  const f = fixture(t);
  const rules = { ...empty(), movieRules: [{ pattern: '(?<=甲)(\\p{Letter}+)(?=乙)', replace: '<$1>', flags: 'gu' }] };
  const result = await f.service.preview({ rules, request: { section: 'movie', text: '甲丙丁乙' } });
  assert.equal(result.rewrittenText, '甲<丙丁>乙');
  const split = await f.service.preview({ rules: { ...empty(), movieRules: [{ pattern: '^.', replace: '$&', flags: '' }] },
    request: { section: 'movie', text: [0xd800, 65] } });
  assert.deepEqual(split.inputText, [0xd800, 65]);
  assert.deepEqual(split.processedText, [0xd800, 65]);
  assert.deepEqual(split.rewrittenText, [0xd800, 65]);
  assert.equal(typeof result.rewrittenText, 'string');
  assert.throws(() => f.service.set({ rules: { ...empty(), aggregateRegexRules: ['\ud800'] } }),
    { code: 'INVALID_RULES' });
});

test('clear removes only the saved rule overrides and old snapshots still execute', async t => {
  const f = fixture(t), unrelated = join(f.dataRoot, 'unrelated.json');
  writeFileSync(unrelated, '{"keep":true}');
  const previous = await f.service.set({ rules: empty() });
  const cleared = await f.service.clear({});
  assert.equal(await f.overridden(), false);
  assert.equal(readFileSync(unrelated, 'utf8'), '{"keep":true}');
  assert.equal(cleared.rules.episodeRules.length, 2);
  assert.strictEqual(await f.service.get(), cleared);
  assert.equal(await createRecognitionRules(previous).movie('Film.2024.mkv'), false);
  assert.equal(await createRecognitionRules(cleared).movie('Film.2024.mkv'), true);
});

test('cancellation interrupts pending native rules, drains it and permits later calls', async t => {
  const f = fixture(t);
  await f.service.snapshot();
  const controller = new AbortController();
  let settled = false;
  const work = f.service.preview({ rules: { ...empty(), movieRules: [{ pattern: '^(a+)+$', replace: '', flags: '' }] },
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } }, { signal: controller.signal });
  void work.finally(() => { settled = true; }).catch(() => {});
  await delay(25);
  assert.equal(settled, false, 'the actual native computation must still be pending');
  controller.abort();
  await assert.rejects(work, { code: 'CANCELLED' });
  assert.equal(settled, true);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'S02E03' } })).rewrittenText, 'S02E03');
});

test('native resource and timeout errors retain public codes, saved state and subsequent execution', async t => {
  const f = fixture(t), snapshot = await f.service.snapshot(), bytes = await f.rows();
  await assert.rejects(f.service.preview({ rules: { ...empty(),
    movieRules: [{ pattern: '(a+)', replace: '$1'.repeat(1024), flags: '' }] },
    request: { section: 'movie', text: 'a'.repeat(100_000) } }), { code: 'RESOURCE_LIMIT' });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'S02E03' } })).rewrittenText, 'S02E03');
  await assert.rejects(f.service.preview({ rules: { ...empty(),
    movieRules: [{ pattern: '^(a+)+$', replace: '', flags: '' }] },
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } }), { code: 'TIMEOUT' });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.deepEqual(await f.rows(), bytes);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'E4' } })).rewrittenText, 'E4');
  await assert.rejects(f.service.invoke({ operation: 'unknown' }), { code: 'SCRIPT_FAILURE' });
  await assert.rejects(f.service.preview({ request: { section: 'movie', text: [65536] } }), { code: 'INVALID_REQUEST' });
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'E5' } })).rewrittenText, 'E5');
});

test('an actual native invalid JSON result maps to the film request error instead of a generic failure', async t => {
  const program = new NativeJsonProgram('globalThis.meowfilmProcess = () => undefined;');
  t.after(() => program.close());
  await assert.rejects(program.invoke(null).catch(error => { throw filmRuleFailure(error); }),
    { code: 'INVALID_REQUEST' });
});

test('a queued clear follows cancelled native work and leaves the old snapshot usable', async t => {
  const f = fixture(t), previous = await f.service.set({ rules: empty() });
  const controller = new AbortController();
  const work = f.service.preview({ rules: { ...empty(),
    movieRules: [{ pattern: '^(a+)+$', replace: '', flags: '' }] },
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } }, { signal: controller.signal });
  const rejected = assert.rejects(work, { code: 'CANCELLED' });
  await delay(25);
  const cleared = f.service.clear({});
  controller.abort();
  await rejected;
  const current = await cleared;
  assert.equal(await f.overridden(), false);
  assert.equal(current.rules.episodeRules.length, 2);
  assert.equal(await createRecognitionRules(previous).movie('Film.2024.mkv'), false);
  assert.equal(await createRecognitionRules(current).movie('Film.2024.mkv'), true);
});

test('close settles active and queued native work, is idempotent and a fresh owner recovers', async t => {
  const f = fixture(t);
  await f.service.snapshot();
  let settled = false;
  const work = f.service.preview({ rules: { ...empty(), movieRules: [{ pattern: '^(a+)+$', replace: '', flags: '' }] },
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } });
  void work.finally(() => { settled = true; }).catch(() => {});
  await delay(25);
  assert.equal(settled, false);
  const queued = f.service.preview({ request: { section: 'episode', text: 'E1' } });
  const drained = Promise.allSettled([work, queued]);
  const closing = f.service.close();
  assert.strictEqual(f.service.close(), closing);
  await closing;
  assert.equal(settled, true);
  for (const result of await drained) assert.equal(result.status, 'rejected');
  await assert.rejects(f.service.snapshot(), { code: 'CLOSED' });
  const next = f.open();
  assert.equal((await next.preview({ request: { section: 'episode', text: 'E2' } })).rewrittenText, 'E2');
});

test('group writes atomically merge the latest saved groups without normalizing unrelated rows', async t => {
  const f = fixture(t);
  const original = { ...empty(), movieRules: [{ pattern: ' movie ', replace: ' keep ', flags: ' i ' }] };
  await withTestDatabase(f.dataRoot, db => db.transaction([
    { sql: "UPDATE film_standalone_rule_groups SET overridden=1,row_revision=1 WHERE group_name='movieRules'" },
    { sql: "INSERT INTO film_standalone_rule_entries VALUES('movieRules',0,?,?,?)", params: [' movie ', ' keep ', ' i '] },
  ]));
  await f.service.get();
  const search = [' \uFEFFfirst\uFEFF ', ' second ', ''];
  const episode = [{ pattern: ' E(\\d+) ', replace: ' E$1 ', flags: ' g ' }];
  const [first, second] = await Promise.all([
    f.service.setGroup({ group: 'search', rules: search }),
    f.service.setGroup({ group: 'episode', rules: episode }),
  ]);
  assert.deepEqual(first.rules.aggregateRegexRules, ['first', 'second']);
  assert.deepEqual(first.rules.movieRules, original.movieRules);
  assert.deepEqual(second.rules.aggregateRegexRules, ['first', 'second']);
  assert.deepEqual(second.rules.episodeRules, [{ pattern: 'E(\\d+)', replace: ' E$1 ', flags: 'g' }]);
  assert.deepEqual(second.rules.movieRules, original.movieRules);
  assert.equal(Object.isFrozen(second.rules.episodeRules[0]), true);
  assert.deepEqual(await f.open().get(), second);
  const cleared = await f.service.setGroup({ group: 'episode', rules: [] });
  assert.deepEqual(cleared.rules.episodeRules, []);
  assert.deepEqual(cleared.rules.aggregateRegexRules, second.rules.aggregateRegexRules);
  await f.service.close();
  const reopened = f.open();
  assert.deepEqual(await reopened.get(), cleared);
});

test('group defaults and previews do not create overrides or advance revisions', async t => {
  const f = fixture(t);
  const defaults = await f.service.groupDefaults({ group: 'episode' });
  assert.equal(defaults.length, 3, 'restore includes the full default episode list');
  const clean = await f.service.groupDefaults({ group: 'episodeClean' });
  assert.ok(clean.length > 0);
  assert.equal(await f.overridden(), false);
  const preview = await f.service.previewGroup({ group: 'search', rules: ['\\[草稿\\]'], request });
  assert.equal(preview.processedText, '真实电影2');
  assert.equal(await f.overridden(), false);
  const saved = await f.service.set({ rules: empty() }), bytes = await f.rows();
  await f.service.groupDefaults({ group: 'search' });
  await f.service.previewGroup({ group: 'search', rules: ['\\[草稿\\]'], request });
  assert.strictEqual(await f.service.snapshot(), saved);
  assert.deepEqual(await f.rows(), bytes);
});

test('group previews use associated saved rules, retain order and preserve isolated UTF16', async t => {
  const f = fixture(t);
  await f.service.set({ rules: { ...empty(), episodeCleanRegexRules: ['^广告'],
    episodeRules: [{ pattern: '第(\\d+)集', replace: 'E$1', flags: '' }] } });
  const bytes = await f.rows();
  const clean = await f.service.previewGroup({ group: 'episodeClean', rules: ['^草稿'],
    request: { section: 'episode', text: '草稿第12集' } });
  assert.equal(clean.rewrittenText, 'E12', 'the associated episode group comes from saved data');
  const match = await f.service.previewGroup({ group: 'episode',
    rules: [{ pattern: '第(\\d+)集', replace: 'S1E$1', flags: '' }],
    request: { section: 'episode', text: '广告第12集' } });
  assert.equal(match.processedText, '第12集', 'the associated clean group comes from saved data');
  assert.equal(match.rewrittenText, 'S1E12');
  const ordered = await f.service.previewGroup({ group: 'movie', rules: [
    { pattern: '^.', replace: 'x', flags: '' }, { pattern: '.*', replace: 'wrong successor', flags: '' },
  ], request: { section: 'movie', text: '🎬' } });
  assert.equal(ordered.matchedRuleIndex, 1);
  assert.deepEqual(ordered.rewrittenText, [0x78, 0xdfac]);
  const search = await f.service.previewGroup({ group: 'search', rules: ['^.'],
    request: { section: 'search', text: [0xd840, 0xdc00] } });
  assert.deepEqual(search.processedText, [0xdc00]);
  assert.deepEqual(await f.rows(), bytes);
});

test('group preview waits for an accepted associated-group save and uses its durable receipt', async t => {
  const f = fixture(t);
  await f.service.set({ rules: { ...empty(),
    episodeRules: [{ pattern: '^第(\\d+)集$', replace: 'old$1', flags: '' }] } });
  const saving = f.service.setGroup({ group: 'episode',
    rules: [{ pattern: ' ^第(\\d+)集$ ', replace: 'E$1', flags: '' }] });
  const preview = f.service.previewGroup({ group: 'episodeClean', rules: ['^草稿'],
    request: { section: 'episode', text: '草稿第12集' } });
  const saved = await saving;
  const bytes = await f.rows();
  const result = await preview;
  assert.equal(result.processedText, '第12集');
  assert.equal(result.rewrittenText, 'E12');
  assert.strictEqual(await f.service.snapshot(), saved);
  assert.deepEqual(await f.rows(), bytes);
});

test('group write failure preserves the previous rows and retries only the selected group', async t => {
  const f = fixture(t), previous = await f.service.snapshot(), bytes = await f.rows();
  await setTestReadOnly(f.dataRoot, true);
  await assert.rejects(f.service.setGroup({ group: 'search', rules: [' pending '] }), { code: 'STORAGE_ERROR' });
  assert.strictEqual(await f.service.get(), previous);
  await setTestReadOnly(f.dataRoot, false);
  assert.deepEqual(await f.rows(), bytes);
  const saved = await f.service.setGroup({ group: 'search', rules: [' pending '] });
  assert.deepEqual(saved.rules.aggregateRegexRules, ['pending']);
  assert.deepEqual(saved.rules.episodeRules, previous.rules.episodeRules);
  assert.deepEqual(saved.rules.movieRules, previous.rules.movieRules);
  assert.deepEqual(saved.rules.episodeCleanRegexRules, previous.rules.episodeCleanRegexRules);
  assert.throws(() => f.service.setGroup({ group: 'movie', rules: [{ pattern: '\ud800' }] }), { code: 'INVALID_RULES' });
  assert.throws(() => f.service.setGroup({ group: 'toString', rules: [] }), { code: 'INVALID_REQUEST' });
  assert.throws(() => f.service.setGroup({ group: { toString: null }, rules: [] }), { code: 'INVALID_REQUEST' });
  await assert.rejects(f.service.previewGroup({ group: 'movie', rules: [], request }), { code: 'INVALID_REQUEST' });
});

test('cancelled group preview drains before subsequent group writes and cannot change saved rules', async t => {
  const f = fixture(t), previous = await f.service.snapshot();
  const controller = new AbortController();
  let settled = false;
  const work = f.service.previewGroup({ group: 'movie', rules: [{ pattern: '^(a+)+$', replace: '', flags: '' }],
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } }, { signal: controller.signal });
  void work.finally(() => { settled = true; }).catch(() => {});
  await delay(25);
  assert.equal(settled, false);
  const cancelled = new AbortController(); cancelled.abort();
  assert.throws(() => f.service.setGroup({ group: 'episode', rules: [] }, { signal: cancelled.signal }), { code: 'CANCELLED' });
  const saved = f.service.setGroup({ group: 'search', rules: ['kept'] });
  controller.abort();
  await assert.rejects(work, { code: 'CANCELLED' });
  assert.equal(settled, true);
  const current = await saved;
  assert.deepEqual(current.rules.movieRules, previous.rules.movieRules);
  assert.deepEqual(current.rules.episodeRules, previous.rules.episodeRules);
  assert.deepEqual(current.rules.aggregateRegexRules, ['kept']);
});
