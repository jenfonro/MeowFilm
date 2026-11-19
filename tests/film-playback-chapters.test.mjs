import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { setImmediate as tick } from 'node:timers/promises';
import { recognizeChapters } from '../packages/film-playback/src/chapters.js';
import { createPlaybackService } from '../packages/film-playback/src/service.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';
import { bindRuleSnapshot } from '../packages/film-playback/src/rules.js';

const chapter = (title, start = 0, end = 10) => ({ title, start, end });
const opening = [chapter('Opening', 0, 15), chapter('剧情', 15, 85), chapter('片尾', 85, 100)];
const expected = [{ kind: 'intro', start: 0, end: 15 }, { kind: 'outro', start: 85, end: 100 }];
const rejects = (work, code) => assert.rejects(work, error => error.code === code && error.message === code);
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

test('chapter recognition retains labels after the former collection and title limits', () => {
  const chapters = Array.from({ length: 257 }, (_, index) => chapter('剧情'.repeat(257), index, index + 1));
  chapters.push(chapter('片头', 257, 258), chapter('片尾', 258, 259));
  assert.deepEqual(recognizeChapters(chapters), { segments: [
    { kind: 'intro', start: 257, end: 258 }, { kind: 'outro', start: 258, end: 259 },
  ] });
});

// Real session/receipt owner, with controlled provider, media and unrelated
// candidate-recognition boundaries. This is not decoder or native-rule evidence.
function fixture(t, { releaseGate } = {}) {
  const source = { key: 'chapters-source', engine: 'cat', installationId: 'chapters-script',
    siteId: 'chapters-site', generation: 1, name: 'Fixture source' };
  const target = { id: 'source:chapters', kind: 'source', title: 'Fixture film',
    source: { key: source.key, engine: source.engine, installationId: source.installationId,
      siteId: source.siteId, detailId: 'film' } };
  let document = { version: 2, preferences: defaultPreferences(), history: [], manual: [],
    blocks: [], previewTargets: [], groups: [] };
  const resources = new Map();
  const snapshot = bindRuleSnapshot(Object.freeze({ revision: 'chapter-fixture', rules: {} }), async request => {
    assert.equal(request.operation, 'recognition.candidates');
    return { value: request.detail.lines.flatMap(line => line.episodes.map(() => ({}))) };
  });
  const data = {
    async handoff() { return { accepted: 0, ignored: 0 }; },
    async targetMetadataEntry() { return null; },
    async metadataSources() { return []; },
    async detail() {
      return { lines: [{ id: 'line', label: 'Line', episodes: [
        { id: 'file-one', name: 'One' }, { id: 'file-two', name: 'Two' },
      ] }] };
    },
    async detailEntry() { return { value: await this.detail(), completeness: 'complete', fetchedAt: 1,
      expiresAt: Number.MAX_SAFE_INTEGER, cache: { status: 'fresh', components: {} } }; },
    async expand(_, line) { return line; },
    async resolve(candidate) { return { url: `https://media.invalid/${candidate.id}`, watchReport: false }; },
    async invalidateAddress() {},
  };
  const media = {
    async prepare(descriptor) {
      const resource = { id: randomUUID(), url: descriptor.url };
      resources.set(resource.id, resource); return resource;
    },
    async release(id) { await releaseGate?.promise; resources.delete(id); },
    async prefetch() { return { bytes: 0 }; },
    async cancelPrefetch() {},
  };
  const service = createPlaybackService({ data, media, rules: { async snapshot() { return snapshot; } },
    library: { read() { return structuredClone(document); },
      async history(targetId) { return structuredClone(document.history.find(row => row?.targetId === targetId)); },
      async saveHistory(value) {
        document.history = document.history.filter(row => row?.targetId !== value.targetId);
        document.history.push(structuredClone(value));
      },
      async list() { return {...structuredClone(document),historyAfter:null}; },
      async removeHistory(targetId) { document.history = targetId == null ? [] : document.history.filter(row => row?.targetId !== targetId); },
      update(mutator) { const next = structuredClone(document); mutator(next); document = next; } } });
  t.after(async () => { releaseGate?.resolve(); await service.close(); });
  const command = (sessionId, action, fields = {}) => service.command({ sessionId, action, ...fields });
  const state = sessionId => service.snapshot({ sessionId });
  async function pending(sessionId) {
    for (let turn = 0; turn < 100; turn++) {
      const attempt = state(sessionId).attempt;
      if (attempt) return service.attempt({ sessionId, attemptId: attempt.id });
      await tick();
    }
    assert.fail(JSON.stringify(state(sessionId)));
  }
  async function open(action = 'play') {
    const value = await service.open({ platform: 'test', target, sources: [source] });
    const sessionId = value.sessionId;
    await command(sessionId, action);
    return { sessionId, receipt: await pending(sessionId) };
  }
  const recognize = ({ sessionId, receipt }, fields = {}) => command(sessionId, 'media.chapters',
    { attemptId: receipt.attemptId, resourceId: receipt.id, chapters: opening, ...fields });
  return { service, resources, command, state, pending, open, recognize };
}

test('chapter recognition matches only complete explicit labels and preserves real intervals', () => {
  const intros = ['片头', '片头曲', 'Opening', 'opening credits', 'INTRO', 'OP',
    'Chapter 01: Opening', '01. 片头', '章节2：片头曲', 'Opening 2'];
  const outros = ['片尾', '片尾曲', 'Ending', 'ENDING CREDITS', 'Outro', 'ED', '02-Ending'];
  for (const [kind, labels] of [['intro', intros], ['outro', outros]]) {
    for (const title of labels) assert.deepEqual(recognizeChapters([chapter(` ${title} `, 12.5, 38)]),
      { segments: [{ kind, start: 12.5, end: 38 }] }, title);
  }
  const generic = ['Chapter 01', '最后一章', 'Credits', '序章', '预告', 'Main Story', 'Introduction',
    'Opening the door', 'Intro to the story', 'Ending explained', 'Not an intro', 'Opening - 剧情',
    '片头之后', '片尾彩蛋', '01. Introduction', 'OP theme - song name', 'Chapter 1:'];
  assert.deepEqual(recognizeChapters(generic.map(title => chapter(title))), { segments: [] });
  const original = [opening[2], opening[0], opening[0], opening[1]];
  const copy = structuredClone(original);
  assert.deepEqual(recognizeChapters(original), { segments: expected });
  assert.deepEqual(original, copy);
});

test('chapter inputs retain long titles and invalid entries reject the entire snapshot', () => {
  assert.deepEqual(recognizeChapters([]), { segments: [] });
  assert.deepEqual(recognizeChapters(Array.from({ length: 257 }, () => chapter('剧情'))), { segments: [] });
  assert.deepEqual(recognizeChapters([chapter('x'.repeat(257)), chapter('😀'.repeat(129))]), { segments: [] });
  for (const input of [null, {}, [null],
    [chapter(1)], [chapter('片头', -1)], [chapter('片头', NaN)],
    [chapter('片头', 0, Infinity)], [chapter('片头', 1, 1)], [chapter('片头', 2, 1)],
    [chapter('片头', '0', 10)], [chapter('片头'), chapter('普通', 0, NaN)]]) {
    assert.throws(() => recognizeChapters(input), error => error.code === 'INVALID_REQUEST');
  }
});

test('candidate and committed receipts can recognize without mutation or playback feedback', async t => {
  const f = fixture(t), value = await f.open();
  const before = f.state(value.sessionId), count = f.resources.size;
  assert.deepEqual(await f.recognize(value), { segments: expected });
  assert.deepEqual(f.state(value.sessionId), before);
  assert.equal(f.resources.size, count);
  await f.command(value.sessionId, 'media.started', { attemptId: value.receipt.attemptId });
  const committed = f.state(value.sessionId);
  assert.deepEqual(await f.recognize(value), { segments: expected });
  assert.deepEqual(f.state(value.sessionId), committed);
  assert.deepEqual(await f.recognize(value, { chapters: [] }), { segments: [] });
  await rejects(f.recognize(value, { chapters: [chapter('片头', 0, NaN)] }), 'INVALID_REQUEST');
});

test('chapter requests cannot borrow a receipt from another session or mismatched resource', async t => {
  const f = fixture(t), first = await f.open(), second = await f.open();
  await rejects(f.recognize({ sessionId: second.sessionId, receipt: first.receipt }), 'STALE_SESSION');
  await rejects(f.recognize(first, { attemptId: second.receipt.attemptId }), 'STALE_SESSION');
  await rejects(f.recognize(first, { resourceId: second.receipt.id }), 'STALE_SESSION');
  await rejects(f.recognize(first, { resourceId: '' }), 'INVALID_REQUEST');
  await rejects(f.recognize(first, { attemptId: '' }), 'INVALID_REQUEST');
  await rejects(f.recognize({ sessionId: randomUUID(), receipt: first.receipt }), 'NOT_FOUND');
});

test('prepare-only candidates may recognize chapters without becoming committed playback', async t => {
  const f = fixture(t), value = await f.open('prepare');
  const before = f.state(value.sessionId);
  assert.equal(before.stage, 'prepared');
  assert.equal(before.current, null);
  assert.deepEqual(await f.recognize(value), { segments: expected });
  assert.deepEqual(f.state(value.sessionId), before);
});

test('released receipts are invalid immediately, before a slow media drain completes', async t => {
  const gate = deferred(), f = fixture(t, { releaseGate: gate }), value = await f.open();
  const release = f.service.releaseMedia({ sessionId: value.sessionId,
    attemptId: value.receipt.attemptId, resourceId: value.receipt.id });
  assert.equal(f.resources.has(value.receipt.id), true);
  await rejects(f.recognize(value), 'STALE_SESSION');
  gate.resolve(); await release;
  await rejects(f.recognize(value), 'STALE_SESSION');
});

test('source revocation rejects chapters while the obsolete resource is still draining', async t => {
  const gate = deferred(), f = fixture(t, { releaseGate: gate }), value = await f.open();
  await f.command(value.sessionId, 'media.started', { attemptId: value.receipt.attemptId });
  const drain = f.service.invalidate({ kind: 'source', engine: 'cat', installationId: 'chapters-script' },
    [{ installationId: 'chapters-script', state: 'disabled', generation: 2, sites: [] }]);
  assert.equal(f.resources.has(value.receipt.id), true);
  await rejects(f.recognize(value), 'STALE_SESSION');
  gate.resolve(); await drain;
  await rejects(f.recognize(value), 'STALE_SESSION');
});

test('replaced, stopped and released sessions cannot reuse old chapter receipts', async t => {
  const gate = deferred(), f = fixture(t, { releaseGate: gate }), value = await f.open();
  await f.command(value.sessionId, 'media.started', { attemptId: value.receipt.attemptId });
  const next = f.state(value.sessionId).candidates.find(row =>
    row.id !== f.state(value.sessionId).current.candidate.id);
  assert.ok(next?.playbackTarget);
  await f.command(value.sessionId, 'select.target', { target: next.playbackTarget, metadataSourceId: null });
  const receipt = await f.pending(value.sessionId);
  assert.notEqual(receipt.attemptId, value.receipt.attemptId);
  assert.deepEqual(await f.recognize(value), { segments: expected }, 'old committed receipt is still active before swap');
  await f.command(value.sessionId, 'media.started', { attemptId: receipt.attemptId });
  assert.equal(f.resources.has(value.receipt.id), true);
  await rejects(f.recognize(value), 'STALE_SESSION');
  gate.resolve();
  await f.command(value.sessionId, 'stop');
  await rejects(f.recognize({ sessionId: value.sessionId, receipt }), 'STALE_SESSION');
  await f.service.release({ sessionId: value.sessionId });
  await rejects(f.recognize(value), 'NOT_FOUND');
});
