import assert from 'node:assert/strict';
import fs, { mkdtempSync, readFileSync, existsSync, mkdirSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { createRecognitionRules, filmRuleFailure } from '../packages/film-playback/src/rules.js';
import { matchesResults } from '../packages/film-playback/src/recognition.js';

const empty = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
function fixture(t, dataRoot = mkdtempSync(join(tmpdir(), 'film-rules-owner-'))) {
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

test('App-managed root, ancestor and module directory aliases support first save, restart and clear', async t => {
  for (const kind of ['root', 'ancestor', 'module-directory']) {
    await t.test(kind, async sub => {
      const base = mkdtempSync(join(tmpdir(), 'film-rules-alias-')), actual = join(base, 'actual');
      sub.after(() => rmSync(base, { recursive: true, force: true }));
      mkdirSync(actual);
      const dataRoot = kind === 'module-directory' ? join(base, 'data') :
        kind === 'ancestor' ? join(base, 'linked', 'application-support', 'script-runtime') : join(base, 'linked');
      if (kind === 'module-directory') mkdirSync(dataRoot);
      fs.symlinkSync(actual, kind === 'module-directory' ? join(dataRoot, 'film-rules') : join(base, 'linked'), 'junction');
      const f = fixture(sub, dataRoot);
      assert.equal(existsSync(f.path), false);
      const initial = await f.service.get();
      assert.deepEqual(JSON.parse(readFileSync(f.path, 'utf8')), { rules: initial.rules });
      const saved = await f.service.set({ rules: empty() });
      await f.service.close();
      const reopened = createFilmRulesService({ dataRoot });
      sub.after(() => reopened.close());
      assert.deepEqual(await reopened.snapshot(), saved);
      const restored = await reopened.restore({ section: 'movie' });
      const actualPath = kind === 'ancestor' ?
        join(actual, 'application-support', 'script-runtime', 'film-rules', 'rules.json') :
        kind === 'root' ? join(actual, 'film-rules', 'rules.json') : join(actual, 'rules.json');
      assert.deepEqual(JSON.parse(readFileSync(actualPath, 'utf8')), { rules: restored.rules });
      await reopened.clear();
      assert.equal(existsSync(actualPath), false);
    });
  }
});

test('linked rules documents and module directories support reads, atomic replacement and restart', async t => {
  for (const kind of ['file-symlink', 'hardlink', 'directory-symlink']) {
    await t.test(kind, async sub => {
      const f = fixture(sub), directory = join(f.dataRoot, 'film-rules'), actual = join(f.dataRoot, 'actual');
      mkdirSync(actual);
      const linkedFile = join(actual, 'rules.json');
      const rules = { ...empty(), aggregateRegexRules: ['linked'] }, bytes = JSON.stringify({ rules });
      writeFileSync(linkedFile, bytes);
      if (kind === 'directory-symlink') fs.symlinkSync(actual, directory, 'junction');
      else {
        mkdirSync(directory);
        if (kind === 'file-symlink') fs.symlinkSync(linkedFile, f.path);
        else fs.linkSync(linkedFile, f.path);
      }
      assert.deepEqual((await f.service.get()).rules, rules);
      const saved = await f.service.set({ rules: empty() });
      assert.deepEqual(JSON.parse(readFileSync(f.path, 'utf8')), { rules: saved.rules });
      if (kind === 'directory-symlink') {
        assert.deepEqual(JSON.parse(readFileSync(linkedFile, 'utf8')), { rules: saved.rules });
      } else {
        assert.equal(readFileSync(linkedFile, 'utf8'), bytes, 'atomic replacement preserves the other linked entry');
      }
      await f.service.close();
      const reopened = createFilmRulesService({ dataRoot: f.dataRoot });
      sub.after(() => reopened.close());
      assert.deepEqual(await reopened.get(), saved);
      await reopened.clear();
      assert.equal(existsSync(f.path), false);
      if (kind !== 'directory-symlink') assert.equal(readFileSync(linkedFile, 'utf8'), bytes);
    });
  }
});

test('an App root alias uses its current destination while preserving content fingerprint checks', async t => {
  const base = mkdtempSync(join(tmpdir(), 'film-rules-alias-'));
  t.after(() => rmSync(base, { recursive: true, force: true }));
  const original = join(base, 'original'), other = join(base, 'other'), linked = join(base, 'linked');
  mkdirSync(original);
  mkdirSync(join(other, 'film-rules'), { recursive: true });
  fs.symlinkSync(original, linked, 'junction');
  const f = fixture(t, linked);
  await f.service.get();
  const bytes = readFileSync(f.path, 'utf8'), otherPath = join(other, 'film-rules', 'rules.json');
  writeFileSync(otherPath, bytes);
  fs.unlinkSync(linked);
  fs.symlinkSync(other, linked, 'junction');
  const saved = await f.service.set({ rules: empty() });
  assert.equal(readFileSync(join(original, 'film-rules', 'rules.json'), 'utf8'), bytes);
  assert.deepEqual(JSON.parse(readFileSync(otherPath, 'utf8')), { rules: saved.rules });
  await f.service.close();
  const reopened = createFilmRulesService({ dataRoot: linked });
  t.after(() => reopened.close());
  const current = await reopened.get();
  assert.deepEqual(current, saved);
  writeFileSync(otherPath, bytes);
  await assert.rejects(reopened.set({ rules: empty() }), { code: 'STORAGE_CHANGED' });
  assert.strictEqual(await reopened.get(), current);
  assert.equal(readFileSync(otherPath, 'utf8'), bytes);
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

test('malformed JSON, invalid rules and invalid UTF-8 remain explicit corruption without overwriting data', async t => {
  for (const [name, bytes] of [
    ['JSON', Buffer.from('{')],
    ['rules', Buffer.from(JSON.stringify({ rules: { episodeRules: [] } }))],
    ['UTF-8', Buffer.concat([Buffer.from('{"rules":{"value":"'), Buffer.from([0xff]), Buffer.from('"}}')])],
  ]) {
    await t.test(name, async sub => {
      const f = fixture(sub), previous = await f.service.get();
      writeFileSync(f.path, bytes);
      const reopened = createFilmRulesService({ dataRoot: f.dataRoot });
      sub.after(() => reopened.close());
      await assert.rejects(reopened.get(), { code: 'CORRUPT_STORAGE' });
      await assert.rejects(f.service.set({ rules: empty() }), { code: 'CORRUPT_STORAGE' });
      assert.strictEqual(await f.service.get(), previous);
      assert.deepEqual(readFileSync(f.path), bytes);
    });
  }
});

test('open/write/fsync/close/rename failures preserve rules on disk and in memory and remove the temporary file', async t => {
  for (const method of ['openSync', 'writeFileSync', 'fsyncSync', 'closeSync', 'renameSync']) {
    await t.test(method, async sub => {
      const f = fixture(sub), previous = await f.service.get(), bytes = readFileSync(f.path);
      const original = fs[method], open = fs.openSync;
      let temporaryFD, failed = false;
      if (method === 'closeSync') {
        sub.mock.method(fs, 'openSync', function(path, ...rest) {
          const descriptor = open.call(this, path, ...rest);
          if (typeof path === 'string' && path.includes('.rules-')) temporaryFD = descriptor;
          return descriptor;
        });
      }
      const mocked = sub.mock.method(fs, method, function(...args) {
        const targeted = method === 'openSync' ? typeof args[0] === 'string' && args[0].includes('.rules-') :
          method === 'closeSync' ? args[0] === temporaryFD : true;
        if (targeted && !failed) {
          failed = true;
          throw Object.assign(new Error('synthetic private storage diagnostic'), { code: 'EIO' });
        }
        return original.apply(this, args);
      });
      await assert.rejects(f.service.set({ rules: empty() }), { code: 'STORAGE_ERROR', message: 'STORAGE_ERROR' });
      mocked.mock.restore();
      assert.equal(failed, true);
      assert.strictEqual(await f.service.get(), previous);
      assert.deepEqual(readFileSync(f.path), bytes);
      assert.deepEqual(fs.readdirSync(join(f.dataRoot, 'film-rules')), ['rules.json']);
    });
  }
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

test('group writes atomically merge the latest saved groups without normalizing unrelated rows', async t => {
  const f = fixture(t);
  mkdirSync(join(f.dataRoot, 'film-rules'));
  const original = { ...empty(), movieRules: [{ pattern: ' movie ', replace: ' keep ', flags: ' i ' }] };
  writeFileSync(f.path, JSON.stringify({ rules: original }));
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
  assert.deepEqual(JSON.parse(readFileSync(f.path, 'utf8')), { rules: second.rules });
  const cleared = await f.service.setGroup({ group: 'episode', rules: [] });
  assert.deepEqual(cleared.rules.episodeRules, []);
  assert.deepEqual(cleared.rules.aggregateRegexRules, second.rules.aggregateRegexRules);
  await f.service.close();
  const reopened = createFilmRulesService({ dataRoot: f.dataRoot });
  t.after(() => reopened.close());
  assert.deepEqual(await reopened.get(), cleared);
});

test('group default drafts and previews do not initialize or write a document', async t => {
  const f = fixture(t);
  const defaults = await f.service.groupDefaults({ group: 'episode' });
  assert.equal(defaults.length, 3, 'restore includes the full default episode list');
  const clean = await f.service.groupDefaults({ group: 'episodeClean' });
  assert.ok(clean.length > 0);
  assert.equal(existsSync(f.path), false);
  const preview = await f.service.previewGroup({ group: 'search', rules: ['\\[草稿\\]'], request });
  assert.equal(preview.processedText, '真实电影2');
  assert.equal(existsSync(f.path), false);
  const saved = await f.service.set({ rules: empty() }), bytes = readFileSync(f.path, 'utf8');
  await f.service.groupDefaults({ group: 'search' });
  await f.service.previewGroup({ group: 'search', rules: ['\\[草稿\\]'], request });
  assert.strictEqual(await f.service.snapshot(), saved);
  assert.equal(readFileSync(f.path, 'utf8'), bytes);
});

test('group previews use associated saved rules, retain order and preserve isolated UTF16', async t => {
  const f = fixture(t);
  await f.service.set({ rules: { ...empty(), episodeCleanRegexRules: ['^广告'],
    episodeRules: [{ pattern: '第(\\d+)集', replace: 'E$1', flags: '' }] } });
  const bytes = readFileSync(f.path, 'utf8');
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
  assert.equal(readFileSync(f.path, 'utf8'), bytes);
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
  const bytes = readFileSync(f.path, 'utf8');
  const result = await preview;
  assert.equal(result.processedText, '第12集');
  assert.equal(result.rewrittenText, 'E12');
  assert.strictEqual(await f.service.snapshot(), saved);
  assert.equal(readFileSync(f.path, 'utf8'), bytes);
});

test('group write failure preserves the previous document and retries only the selected group', async t => {
  const f = fixture(t), previous = await f.service.snapshot(), bytes = readFileSync(f.path, 'utf8');
  renameSync(f.path, `${f.path}.saved`); mkdirSync(f.path);
  await assert.rejects(f.service.setGroup({ group: 'search', rules: [' pending '] }), { code: 'STORAGE_ERROR' });
  assert.strictEqual(await f.service.get(), previous);
  rmSync(f.path, { recursive: true }); renameSync(`${f.path}.saved`, f.path);
  assert.equal(readFileSync(f.path, 'utf8'), bytes);
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
