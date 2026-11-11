import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, existsSync, mkdirSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { createRecognitionRules, filmRuleFailure } from '../packages/film-playback/src/rules.js';
import { matchesResults } from '../packages/film-playback/src/recognition.js';

const empty = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
function fixture(t) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'film-rules-owner-'));
  const service = createFilmRulesService({ dataRoot });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  return { service, dataRoot, path: join(dataRoot, 'film-rules', 'rules.json') };
}
const request = { section: 'search', text: '[草稿]真实电影2', query: '真实电影2' };

test('real QuickJS owner initializes defaults and reopens only its current document, including saved empty groups', async t => {
  const f = fixture(t);
  const initial = await f.service.get();
  assert.equal(initial.rules.episodeRules.length, 2);
  assert.equal(Object.isFrozen(initial.rules.episodeRules[0]), true);
  if (process.platform !== 'win32') assert.equal(statSync(f.path).mode & 0o777, 0o600);
  assert.deepEqual(JSON.parse(readFileSync(f.path, 'utf8')), { rules: initial.rules });
  const saved = await f.service.set({ rules: empty() });
  assert.notEqual(saved.revision, initial.revision);
  await f.service.close();
  const reopened = createFilmRulesService({ dataRoot: f.dataRoot });
  t.after(() => reopened.close());
  assert.deepEqual(await reopened.snapshot(), saved);
});

test('draft preview and normalization use QuickJS without persisting or replacing saved rules', async t => {
  const f = fixture(t), draft = { ...empty(), aggregateRegexRules: ['\\[草稿\\]'] };
  assert.equal((await f.service.preview({ request, rules: draft })).processedText, '真实电影2');
  assert.equal(existsSync(f.path), false);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'E2' } })).rewrittenText, 'E2');
  assert.equal(existsSync(f.path), false, 'previewing defaults also never creates the saved document');
  assert.deepEqual(await f.service.normalize({ kind: 'match', pattern: ' /\\\\d+/gy ', replace: ' $1 ' }),
    { pattern: '\\d+', replace: '$1', flags: 'gy' });
  assert.equal(await f.service.normalize({ kind: 'clean', pattern: ' /a/m ' }), '/a/m');
  const snapshot = await f.service.get(), before = readFileSync(f.path, 'utf8');
  await f.service.preview({ request, rules: draft });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.equal(readFileSync(f.path, 'utf8'), before);
});

test('failed durable writes keep the previous snapshot and external changes are detected', async t => {
  const f = fixture(t), previous = await f.service.snapshot(), saved = readFileSync(f.path, 'utf8');
  renameSync(f.path, `${f.path}.saved`);
  mkdirSync(f.path);
  await assert.rejects(f.service.set({ rules: empty() }), { code: 'STORAGE_ERROR' });
  assert.strictEqual(await f.service.get(), previous);
  rmSync(f.path, { recursive: true });
  renameSync(`${f.path}.saved`, f.path);
  assert.equal(readFileSync(f.path, 'utf8'), saved);
  writeFileSync(f.path, JSON.stringify({ rules: empty() }));
  await assert.rejects(f.service.set({ rules: previous.rules }), { code: 'STORAGE_CHANGED' });
  assert.strictEqual(await f.service.get(), previous);
});

test('a failed first read can recover after the local storage failure is corrected', async t => {
  const f = fixture(t);
  mkdirSync(f.path, { recursive: true });
  await assert.rejects(f.service.snapshot(), { code: 'STORAGE_ERROR' });
  rmSync(f.path, { recursive: true });
  assert.equal((await f.service.snapshot()).rules.episodeRules.length, 2);
  assert.equal(existsSync(f.path), true);
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

test('clear removes only the owned document and old snapshots still execute', async t => {
  const f = fixture(t), unrelated = join(f.dataRoot, 'unrelated.json');
  writeFileSync(unrelated, '{"keep":true}');
  const previous = await f.service.set({ rules: empty() });
  const cleared = await f.service.clear({});
  assert.equal(existsSync(f.path), false);
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
  const f = fixture(t), snapshot = await f.service.snapshot(), bytes = readFileSync(f.path, 'utf8');
  await assert.rejects(f.service.preview({ rules: { ...empty(),
    movieRules: [{ pattern: '(a+)', replace: '$1'.repeat(1024), flags: '' }] },
    request: { section: 'movie', text: 'a'.repeat(100_000) } }), { code: 'RESOURCE_LIMIT' });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.equal((await f.service.preview({ request: { section: 'episode', text: 'S02E03' } })).rewrittenText, 'S02E03');
  await assert.rejects(f.service.preview({ rules: { ...empty(),
    movieRules: [{ pattern: '^(a+)+$', replace: '', flags: '' }] },
    request: { section: 'movie', text: `${'a'.repeat(100_000)}!` } }), { code: 'TIMEOUT' });
  assert.strictEqual(await f.service.snapshot(), snapshot);
  assert.equal(readFileSync(f.path, 'utf8'), bytes);
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
  assert.equal(existsSync(f.path), false);
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
  const next = createFilmRulesService({ dataRoot: f.dataRoot });
  t.after(() => next.close());
  assert.equal((await next.preview({ request: { section: 'episode', text: 'E2' } })).rewrittenText, 'E2');
});
