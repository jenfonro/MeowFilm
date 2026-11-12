import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after } from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { createPlaybackService } from '../packages/film-playback/src/service.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';

// F-level scheduling evidence: the service is real; providers, storage and media
// are controlled boundaries. No HTTP, decoding, byte-cache or native output is exercised.
const movie = { id: 'tmdb:movie:17', kind: 'movie', title: 'Film', metadataProvider: 'tmdb', metadataId: 17 };
const tv = { id: 'tmdb:tv:19', kind: 'tv', title: 'Series', metadataProvider: 'tmdb', metadataId: 19 };
const ruleRoot = mkdtempSync(join(tmpdir(), 'film-rules-playback-'));
const rules = createFilmRulesService({ dataRoot: ruleRoot });
await rules.snapshot();
after(async () => { await rules.close(); rmSync(ruleRoot, { recursive: true, force: true }); });

const source = key => ({ key, engine: 'cat', installationId: `install-${key}`,
  siteId: key, generation: 1, name: key });
const file = (id, quality = '1080p', provider) => ({ id, name: `Film.${id}.${quality}`,
  ...(provider ? { file: { kind: 'share', provider, shareId: 'fixture-share',
    fileId: id, accountRevision: 1 } } : {}) });
const line = (id, episodes) => ({ id, label: id, episodes });
const detail = (episodes, lineId = 'line') => ({ lines: [line(lineId, episodes)] });
const result = id => ({ vod_id: id, vod_name: 'Film' });
const history = (detailId, sourceKey = 'a') => ({ targetId: movie.id, title: movie.title, sourceKey,
  candidateId: 'previous-owner-candidate', detailId, lineId: 'line', episode: {},
  position: 31, duration: 120, paused: false, updatedAt: 1 });
const manualSite = detailId => ({ id: `site-${detailId}`, targetId: movie.id, kind: 'site',
  sourceKey: 'a', detailId, enabled: true, autoDisable: false });
const manualShare = (id, provider = 'quark') => ({ id, targetId: movie.id, kind: 'share', provider,
  share: { url: `https://share.invalid/${id}` }, enabled: true, autoDisable: false });
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const error = code => Object.assign(new Error(code), { code });
const tick = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let turn = 0; turn < 1500; turn++) {
    const value = predicate();
    if (value) return value;
    await delay(2);
  }
  assert.fail('Expected public playback state was not reached');
}
const coordinates = value => [value.season, value.episode, value.globalEpisode];

function fixture({ details = {}, pages = {}, shares = {}, metadata, onResolve } = {}) {
  let document = { version: 1, preferences: defaultPreferences(), history: [], manual: [],
    blocks: [], previewTargets: [] };
  let resourceSequence = 0;
  const calls = [], resources = new Map(), addresses = new Map();
  const library = {
    read: () => structuredClone(document),
    update(mutator) {
      const draft = structuredClone(document);
      mutator(draft);
      document = draft;
    },
  };
  const data = {
    async targetMetadata(target) {
      calls.push({ type: 'metadata', targetId: target.id });
      return structuredClone(target.kind === 'tv' ? metadata : { id: 17, title: 'Film' });
    },
    async targetMetadataEntry(target, options) {
      const value = await this.targetMetadata(target, options);
      return value === null ? null : { value, completeness: 'complete',
        fetchedAt: Date.parse('2026-11-08T12:00:00Z'), expiresAt: Date.parse('2026-11-09T12:00:00Z'),
        cache: { status: 'fresh', components: {} } };
    },
    async metadataSources(target, { metadata: value }) {
      return [{ provider: target.metadataProvider, metadataId: target.metadataId,
        kind: target.kind, title: target.title, metadata: structuredClone(value) }];
    },
    async seasonMetadata(target, season) {
      calls.push({ type: 'season', season });
      const row = metadata.seasons.find(value => value.season_number === season);
      assert.ok(row, `Unexpected season request: ${season}`);
      return structuredClone(row);
    },
    async seasonMetadataEntry(target, season, options) {
      const value = await this.seasonMetadata(target, season, options);
      return { value, completeness: 'complete',
        fetchedAt: Date.parse('2026-11-08T12:00:00Z'), expiresAt: Date.parse('2026-11-09T12:00:00Z'),
        cache: { status: 'fresh', components: {} } };
    },
    async invalidateAddress(candidate) { calls.push({ type: 'invalidateAddress', candidateId: candidate.id }); },
    async search(value, query, page) {
      calls.push({ type: 'search', sourceKey: value.key, query, page });
      const configured = pages[value.key] ?? [];
      return { page, hasMore: page < configured.length,
        items: structuredClone(configured[page - 1] ?? []) };
    },
    async detail(value, detailId) {
      calls.push({ type: 'detail', sourceKey: value.key, detailId });
      assert.ok(Object.hasOwn(details[value.key] ?? {}, detailId),
        `Unexpected detail request: ${value.key}/${detailId}`);
      return structuredClone(details[value.key][detailId]);
    },
    async expand(value, input) {
      calls.push({ type: 'expand', sourceKey: value.key, lineId: input.id });
      if (value.engine !== 'pan') return structuredClone(input);
      assert.ok(Object.hasOwn(shares, input.id), `Unexpected manual share: ${input.id}`);
      return { ...input, expanded: true, episodes: structuredClone(shares[input.id]) };
    },
    async resolve(candidate) {
      calls.push({ type: 'resolve', sourceKey: candidate.source.key, detailId: candidate.source.detailId,
        lineId: candidate.lineId, candidateId: candidate.id, fileId: candidate.file?.fileId ?? candidate.episodeId,
        season: candidate.season, episode: candidate.episode, globalEpisode: candidate.globalEpisode,
        provider: candidate.provider, quality: candidate.quality });
      if (onResolve) await onResolve(candidate);
      const descriptor = { url: `https://media.invalid/${candidate.id}`, watchReport: false,
        scope: { sourceKey: candidate.source.key } };
      addresses.set(descriptor.url, candidate);
      return descriptor;
    },
  };
  const media = {
    async prepare(descriptor) {
      const candidate = addresses.get(descriptor.url);
      assert.ok(candidate, 'Only a resolved fixture address can be prepared');
      const resource = { id: `resource-${++resourceSequence}`, url: descriptor.url };
      resources.set(resource.id, resource);
      calls.push({ type: 'prepare', resourceId: resource.id, candidateId: candidate.id,
        season: candidate.season, episode: candidate.episode, globalEpisode: candidate.globalEpisode });
      return resource;
    },
    async release(id) {
      calls.push({ type: 'release', resourceId: id });
      resources.delete(id);
    },
    async prefetch(id) {
      calls.push({ type: 'prefetch', resourceId: id });
      return { bytes: 4096 };
    },
    async cancelPrefetch(id) { calls.push({ type: 'cancelPrefetch', resourceId: id }); },
  };
  const service = createPlaybackService({ data, library, media, rules, now: () => Date.parse('2026-11-08T12:00:00Z') });
  const open = async (input = {}) => (await service.open({ platform: 'test', target: movie,
    sources: [source('a'), source('b')], ...input }));
  const snapshot = sessionId => service.snapshot({ sessionId });
  const command = (sessionId, action, input = {}) => service.command({ sessionId, action, ...input });
  const pending = sessionId => until(() => snapshot(sessionId).attempt);
  const commit = async sessionId => {
    const attempt = await pending(sessionId);
    await command(sessionId, 'media.started', { attemptId: attempt.id });
    return attempt;
  };
  const trace = type => calls.filter(call => call.type === type);
  return { service, library, calls, resources, open, snapshot, command, pending, commit, trace,
    setDetail(sourceKey, id, value) { details[sourceKey][id] = structuredClone(value); } };
}

async function browseKnown(f, sessionId, detailId) {
  await until(() => f.snapshot(sessionId).stage === 'idle');
  const selected = f.snapshot(sessionId).sources.find(row => row.source.detailId === detailId);
  assert.ok(selected, `Expected a handed-off result: ${detailId}`);
  await f.command(sessionId, 'browse', { sourceId: selected.id });
  await until(() => !f.snapshot(sessionId).browsing.loading && f.snapshot(sessionId).browsing.episodes.length);
}
function assertNoLaterWork(f, detailIds) {
  assert.equal(f.trace('detail').some(call => detailIds.includes(call.detailId)), false);
  assert.deepEqual(f.trace('search'), []);
}
function resolvedFiles(f) { return f.trace('resolve').map(call => call.fileId); }

test('F01 history pending media failure continues an untried history file before any later stage', async t => {
  const f = fixture({ details: { a: {
    history: detail([file('history-high'), file('history-low', '720p')]),
    manual: detail([file('manual')]),
  }, b: { deferred: detail([file('deferred')]) } } });
  t.after(() => f.service.close());
  f.library.update(document => { document.history.push(history('history')); });
  (await f.service.updateManual({ action: 'save', item: manualSite('manual') }));
  const { sessionId } = (await f.open({ platform: 'windows',
    results: [{ sourceKey: 'b', items: [result('deferred')] }] }));
  const first = await f.pending(sessionId);
  assert.equal(first.candidate.source.detailId, 'history');
  assert.equal(first.position, 31);
  assertNoLaterWork(f, ['manual', 'deferred']);
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  const next = await f.pending(sessionId);
  assert.notEqual(next.id, first.id);
  assert.notEqual(next.candidate.id, first.candidate.id);
  assert.equal(next.candidate.source.detailId, 'history');
  assert.deepEqual(resolvedFiles(f), ['history-high', 'history-low']);
  assert.deepEqual(f.trace('detail').map(call => call.detailId), ['history']);
  assertNoLaterWork(f, ['manual', 'deferred']);
});

test('F02 manual site pending media failure continues its next exact file without reading query results', async t => {
  const f = fixture({ details: { a: { manual: detail([file('manual-high'), file('manual-low', '720p')]) },
    b: { deferred: detail([file('deferred')]) } } });
  t.after(() => f.service.close());
  (await f.service.updateManual({ action: 'save', item: manualSite('manual') }));
  const { sessionId } = (await f.open({ platform: 'windows',
    results: [{ sourceKey: 'b', items: [result('deferred')] }] }));
  const first = await f.pending(sessionId);
  assert.equal(first.candidate.source.detailId, 'manual');
  assertNoLaterWork(f, ['deferred']);
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  const next = await f.pending(sessionId);
  assert.notEqual(next.candidate.id, first.candidate.id);
  assert.equal(next.candidate.source.detailId, 'manual');
  assert.deepEqual(resolvedFiles(f), ['manual-high', 'manual-low']);
  assert.deepEqual(f.trace('detail').map(call => call.detailId), ['manual']);
  assertNoLaterWork(f, ['deferred']);
});

test('F03 manual share pending media failure continues a different actual file without a synthetic site', async t => {
  const f = fixture({ shares: { share: [file('share-high', '1080p', 'quark'), file('share-low', '720p', 'quark')] },
    details: { b: { deferred: detail([file('deferred')]) } } });
  t.after(() => f.service.close());
  (await f.service.updateManual({ action: 'save', item: manualShare('share') }));
  const { sessionId } = (await f.open({ platform: 'windows',
    results: [{ sourceKey: 'b', items: [result('deferred')] }] }));
  const first = await f.pending(sessionId);
  assert.equal(first.candidate.source.key, 'manual:share');
  assert.equal(first.candidate.source.engine, 'pan');
  assert.equal(first.candidate.source.installationId, undefined);
  assert.equal(first.candidate.source.siteId, undefined);
  assert.deepEqual(f.trace('detail'), []);
  assert.deepEqual(f.trace('search'), []);
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  const next = await f.pending(sessionId);
  assert.notEqual(next.candidate.id, first.candidate.id);
  assert.equal(next.candidate.source.key, 'manual:share');
  assert.deepEqual(resolvedFiles(f), ['share-high', 'share-low']);
  assert.deepEqual(f.trace('expand').map(call => call.sourceKey), ['manual:share']);
  assert.deepEqual(f.trace('detail'), []);
  assert.deepEqual(f.trace('search'), []);
});

test('F04 media failures traverse known history manual unresolved results and incremental search in order', async t => {
  const f = fixture({ details: { a: {
    known: detail([file('known')]), history: detail([file('history')]), manual: detail([file('manual')]),
    deferred: detail([file('deferred')]), searched: detail([file('searched-high'), file('searched-low', '720p')]),
  } }, pages: { a: [[result('known'), result('deferred')], [result('searched')], [result('later-page')]] } });
  t.after(() => f.service.close());
  f.library.update(document => { document.history.push(history('history')); });
  (await f.service.updateManual({ action: 'save', item: manualSite('manual') }));
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [result('known'), result('deferred')] }],
    search: [{ sourceKey: 'a', page: 1, completed: false }] }));
  await browseKnown(f, sessionId, 'known');
  await f.command(sessionId, 'play');
  const stages = [
    ['known', ['history', 'manual', 'deferred', 'searched']],
    ['history', ['manual', 'deferred', 'searched']],
    ['manual', ['deferred', 'searched']],
    ['deferred', ['searched']],
    ['searched-high', []],
    ['searched-low', []],
  ];
  const attempts = new Set();
  for (let index = 0; index < stages.length; index++) {
    const [expected, unread] = stages[index];
    const attempt = await f.pending(sessionId);
    assert.equal(attempt.candidate.name, `Film.${expected}.${expected === 'searched-low' ? '720p' : '1080p'}`);
    assert.equal(attempts.has(attempt.candidate.id), false, 'A failed accurate file must never be retried');
    attempts.add(attempt.candidate.id);
    assert.equal(f.trace('detail').some(call => unread.includes(call.detailId)), false);
    if (index < 4) assert.deepEqual(f.trace('search'), []);
    else assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 2]]);
    if (index < stages.length - 1) await f.command(sessionId, 'media.failed', { attemptId: attempt.id });
  }
  assert.deepEqual(resolvedFiles(f), ['known', 'history', 'manual', 'deferred', 'searched-high', 'searched-low']);
  assert.deepEqual([...new Set(f.trace('detail').map(call => call.detailId))],
    ['known', 'history', 'manual', 'deferred', 'searched']);
  assert.deepEqual(f.snapshot(sessionId).search.map(({ sourceKey, page, completed }) => [sourceKey, page, completed]),
    [['a', 2, false], ['b', 0, false]], 'Pending must not fetch page 3 or exhaust an untouched source');
  assert.equal(f.snapshot(sessionId).searchComplete, false, 'An untouched later source is not exhausted');
});

function constrainedFixture(known) {
  return fixture({ details: { a: {
    known: detail(known),
    history: detail([file('history-other', '1080p', 'uc')]),
    manual: detail([file('manual-other', '720p', 'uc')]),
    deferred: detail([file('deferred-other', '4k', 'uc')]),
    searched: detail([file('searched-other', '1080p', 'uc')]),
  } }, shares: { share: [file('share-other', '720p', 'uc')] }, pages: { a: [[result('searched')]] } });
}
async function seedConstrainedStages(f) {
  f.library.update(document => { document.history.unshift(history('history')); });
  (await f.service.updateManual({ action: 'save', item: manualSite('manual') }));
  (await f.service.updateManual({ action: 'save', item: manualShare('share', 'uc') }));
}
function assertAllConstraintStagesVisited(f, sessionId, historyDetail = 'history') {
  assert.deepEqual([...new Set(f.trace('detail').map(call => call.detailId))],
    [...new Set(['known', historyDetail, 'manual', 'deferred', 'searched'])]);
  assert.deepEqual(f.trace('expand').map(call => call.sourceKey), ['a', 'manual:share']);
  assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 1], ['b', 1]]);
  assert.equal(f.snapshot(sessionId).sources.every(row => row.state === 'ready' && row.error === null), true,
    'Unavailable must come from the hard constraint, not from a failed fixture boundary');
  assert.equal(f.library.read().manual.every(row => row.failureCount === 0 && row.enabled), true);
}

test('F05 fixed pan survives a pending media failure and rejects every other-pan fallback stage', async t => {
  const f = constrainedFixture([file('chosen-pan', '4k', 'quark'), file('known-other', '4k_hdr', 'uc')]);
  t.after(() => f.service.close());
  seedConstrainedStages(f);
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [result('known'), result('deferred')] }] }));
  await browseKnown(f, sessionId, 'known');
  await f.command(sessionId, 'pan', { provider: 'quark' });
  const first = await f.pending(sessionId);
  assert.equal(first.candidate.provider, 'quark');
  assertNoLaterWork(f, ['history', 'manual', 'deferred', 'searched']);
  await f.command(sessionId, 'media.failed', { attemptId: first.id });
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.equal(f.snapshot(sessionId).error, 'NO_MATCH');
  assert.deepEqual(f.snapshot(sessionId).constraint, { action: 'pan', provider: 'quark' });
  assert.deepEqual(resolvedFiles(f), ['chosen-pan']);
  assert.equal(f.trace('resolve').every(call => call.provider === 'quark'), true);
  assertAllConstraintStagesVisited(f, sessionId);
});

test('F06 fixed quality survives a failed replacement without degrading or destroying the old output', async t => {
  const f = constrainedFixture([file('old-output', '1080p', 'quark'), file('chosen-quality', '4k_hdr', 'quark')]);
  t.after(() => f.service.close());
  seedConstrainedStages(f);
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [result('known'), result('deferred')] }] }));
  await browseKnown(f, sessionId, 'known');
  const selected = f.snapshot(sessionId).candidates.find(candidate => candidate.name === 'Film.old-output.1080p');
  assert.ok(selected);
  await f.command(sessionId, 'select.candidate', { candidateId: selected.id });
  const old = await f.commit(sessionId);
  const receipt = f.service.attempt({ sessionId, attemptId: old.id });
  await f.command(sessionId, 'media.progress', { attemptId: old.id, position: 47, duration: 120, paused: true });
  assert.equal(f.library.read().history[0].detailId, 'known');
  await f.command(sessionId, 'quality', { quality: '4k_hdr' });
  const replacement = await f.pending(sessionId);
  assert.equal(replacement.candidate.quality, '4k_hdr');
  assert.equal(replacement.position, 47);
  assert.equal(replacement.paused, true);
  assertNoLaterWork(f, ['history', 'manual', 'deferred', 'searched']);
  await f.command(sessionId, 'media.failed', { attemptId: replacement.id });
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  const state = f.snapshot(sessionId);
  assert.equal(state.attempt, null);
  assert.equal(state.error, 'NO_MATCH');
  assert.deepEqual(state.constraint, { action: 'quality', quality: '4k_hdr' });
  assert.equal(state.current.attemptId, old.id);
  assert.equal(state.current.position, 47);
  assert.equal(state.current.paused, true);
  assert.equal(f.resources.has(receipt.id), true);
  assert.deepEqual(resolvedFiles(f), ['old-output', 'chosen-quality']);
  assertAllConstraintStagesVisited(f, sessionId, 'known');
});

test('F07 block carries the confirmed attempt and persists only its exact line across fallback and reopen', async t => {
  const f = fixture({ details: { a: {
    blocked: detail([file('blocked-first', '4k'), file('blocked-sibling')], 'wrong-line'),
    fallback: detail([file('fallback', '720p')]),
  } }, pages: { a: [[result('blocked'), result('fallback')]] } });
  t.after(() => f.service.close());
  (await f.service.updateManual({ action: 'save', item: manualSite('blocked') }));
  const { sessionId } = (await f.open({ results: [{ sourceKey: 'a', items: [result('blocked')] }] }));
  await browseKnown(f, sessionId, 'blocked');
  await f.command(sessionId, 'play');
  const original = await f.commit(sessionId);
  const confirmation = f.snapshot(sessionId);
  const confirmedAttemptId = confirmation.current?.attemptId ?? confirmation.attempt?.id;
  assert.equal(confirmedAttemptId, original.id);
  await f.command(sessionId, 'block', { attemptId: confirmedAttemptId });
  const replacement = await f.pending(sessionId);
  assert.equal(replacement.candidate.source.detailId, 'fallback');
  assert.deepEqual(resolvedFiles(f), ['blocked-first', 'fallback']);
  assert.deepEqual(f.library.read().blocks, [{ targetId: movie.id, sourceKey: 'a',
    detailId: 'blocked', lineId: 'wrong-line' }]);
  assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 1]]);
  await f.service.release({ sessionId });
  f.setDetail('a', 'blocked', { lines: [
    line('wrong-line', [file('blocked-first', '4k'), file('blocked-sibling')]),
    line('good-line', [file('same-detail-good', '720p')]),
  ] });
  const reopened = (await f.open({ platform: 'windows',
    results: [{ sourceKey: 'a', items: [result('blocked')] }] }));
  const afterReopen = await f.pending(reopened.sessionId);
  assert.equal(afterReopen.candidate.source.detailId, 'blocked');
  assert.equal(afterReopen.candidate.lineId, 'good-line');
  assert.deepEqual(resolvedFiles(f), ['blocked-first', 'fallback', 'same-detail-good']);
  assert.equal(f.library.read().blocks.length, 1);
  assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 1]]);
});

test('F08 switch excludes aliases of only the accurate file across stages without persisting a block', async t => {
  const sameFile = () => file('same-file', '1080p', 'quark');
  const f = fixture({ details: { a: {
    known: detail([sameFile()]), manual: detail([sameFile()]), deferred: detail([sameFile()]),
    searched: detail([sameFile(), file('alternate-file', '720p', 'quark')]),
  } }, shares: { share: [sameFile()] }, pages: { a: [[result('searched')]] } });
  t.after(() => f.service.close());
  (await f.service.updateManual({ action: 'save', item: manualSite('manual') }));
  (await f.service.updateManual({ action: 'save', item: manualShare('share') }));
  const input = { results: [{ sourceKey: 'a', items: [result('known'), result('deferred')] }] };
  const { sessionId } = (await f.open(input));
  await browseKnown(f, sessionId, 'known');
  await f.command(sessionId, 'play');
  const original = await f.commit(sessionId);
  await f.command(sessionId, 'media.progress', { attemptId: original.id, position: 37, duration: 120, paused: true });
  await f.command(sessionId, 'switch');
  const alternate = await f.pending(sessionId);
  assert.equal(alternate.candidate.source.detailId, 'searched');
  assert.equal(alternate.position, 37);
  assert.equal(alternate.paused, true);
  assert.equal(f.snapshot(sessionId).current.attemptId, original.id);
  assert.deepEqual(resolvedFiles(f), ['same-file', 'alternate-file']);
  assert.deepEqual([...new Set(f.trace('detail').map(call => call.detailId))],
    ['known', 'manual', 'deferred', 'searched']);
  assert.deepEqual(f.trace('expand').map(call => call.sourceKey), ['a', 'manual:share']);
  assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 1]]);
  assert.deepEqual(f.library.read().blocks, []);
  await f.service.release({ sessionId });
  const reopened = (await f.open({ ...input, platform: 'windows' }));
  const again = await f.pending(reopened.sessionId);
  assert.equal(again.candidate.id, original.candidate.id);
  assert.equal(again.position, 37);
  assert.deepEqual(resolvedFiles(f), ['same-file', 'alternate-file', 'same-file']);
  assert.deepEqual(f.library.read().blocks, []);
});

const season = (number, count) => ({ season_number: number, episode_count: count,
  episodes: Array.from({ length: count }, (_, index) => ({
    season_number: number, episode_number: index + 1, air_date: '2020-01-01',
  })) });
const series = { id: 19, status: 'Ended', number_of_seasons: 2, seasons: [
  { season_number: 0, episode_count: 1,
    episodes: [{ season_number: 0, episode_number: 0, air_date: '2020-01-01' }] },
  season(1, 2), season(2, 3),
] };
const seriesFile = (seasonNumber, episodeNumber) => ({ id: `s${seasonNumber}-e${episodeNumber}`,
  name: `Series.S${String(seasonNumber).padStart(2, '0')}E${String(episodeNumber).padStart(2, '0')}.1080p`,
  season: seasonNumber, episode: episodeNumber });
const seriesDetail = detail([seriesFile(0, 0), seriesFile(1, 1), seriesFile(1, 2),
  seriesFile(2, 1), seriesFile(2, 2), seriesFile(2, 3), seriesFile(1, 3), seriesFile(2, 4)]);
const seriesInput = target => ({ target: { ...tv, ...target },
  results: [{ sourceKey: 'a', items: [{ vod_id: 'series', vod_name: tv.title }] }] });
const requests = f => f.calls.filter(call => ['metadata', 'season', 'search', 'detail',
  'expand', 'resolve', 'prepare', 'prefetch'].includes(call.type));
async function playSeries(f, target) {
  const { sessionId } = (await f.open(seriesInput(target)));
  await until(() => f.snapshot(sessionId).stage === 'idle');
  await f.command(sessionId, 'play');
  const current = await f.commit(sessionId);
  return { sessionId, current };
}

test('F09 only real ended crosses unequal seasons and specials never shift the global episode', async t => {
  const f = fixture({ metadata: series, details: { a: { series: seriesDetail } } });
  t.after(() => f.service.close());
  const { sessionId, current } = await playSeries(f, { season: 1, episode: 2, globalEpisode: 2 });
  assert.deepEqual(coordinates(current.candidate), [1, 2, 2]);
  await f.command(sessionId, 'browse.season', { season: 0 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(f.snapshot(sessionId).episodes.map(coordinates), [[0, 0, undefined]]);
  await f.command(sessionId, 'browse.season', { season: 1 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(f.snapshot(sessionId).episodes.map(coordinates), [[1, 1, 1], [1, 2, 2]]);
  await f.command(sessionId, 'browse.season', { season: 2 });
  await until(() => !f.snapshot(sessionId).browsing.loading);
  assert.deepEqual(f.snapshot(sessionId).episodes.map(coordinates), [[2, 1, 3], [2, 2, 4], [2, 3, 5]]);
  const beforeProgress = requests(f);
  await f.command(sessionId, 'media.progress', { attemptId: current.id, position: 90, duration: 90, paused: true });
  await delay(2);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.equal(f.snapshot(sessionId).stage, 'playing');
  assert.equal(f.snapshot(sessionId).attempt, null);
  assert.deepEqual(coordinates(f.snapshot(sessionId).target), [1, 2, 2]);
  assert.deepEqual(requests(f), beforeProgress, 'A duration update is not an ended event');
  await f.command(sessionId, 'media.ended', { attemptId: current.id });
  const next = await f.pending(sessionId);
  assert.deepEqual(coordinates(next.candidate), [2, 1, 3]);
  assert.deepEqual(coordinates(f.snapshot(sessionId).target), [2, 1, 3]);
  assert.equal(next.position, 0);
  assert.equal(next.paused, false);
  assert.deepEqual(f.trace('resolve').map(coordinates), [[1, 2, 2], [2, 1, 3]]);
  assert.deepEqual(f.trace('search'), []);
  await f.command(sessionId, 'media.started', { attemptId: next.id });
  await f.command(sessionId, 'previous');
  const previous = await f.pending(sessionId);
  assert.deepEqual(coordinates(previous.candidate), [1, 2, 2]);
  assert.equal(previous.position, 0);
  assert.deepEqual(f.trace('resolve').map(coordinates), [[1, 2, 2], [2, 1, 3], [1, 2, 2]]);
  assert.deepEqual(f.trace('search'), []);
});

test('F10 the actual final episode ends without asking for a fabricated next address', async t => {
  const f = fixture({ metadata: series, details: { a: { series: seriesDetail } } });
  t.after(() => f.service.close());
  const { sessionId, current } = await playSeries(f, { season: 2, episode: 3, globalEpisode: 5 });
  await f.command(sessionId, 'media.progress', { attemptId: current.id, position: 60, duration: 60, paused: false });
  const beforeEnd = requests(f);
  await f.command(sessionId, 'media.ended', { attemptId: current.id });
  await delay(2);
  const ended = f.snapshot(sessionId);
  assert.equal(ended.stage, 'ended');
  assert.equal(ended.current, null);
  assert.equal(ended.attempt, null);
  assert.deepEqual(coordinates(ended.target), [2, 3, 5]);
  assert.deepEqual(requests(f).slice(beforeEnd.length).map(call => call.type), ['metadata', 'season']);
  assert.deepEqual(f.trace('resolve').map(coordinates), [[2, 3, 5]]);
});

test('F11 a missing season has no selectable coordinates or invented global offset', async t => {
  const sparse = { ...series, number_of_seasons: 3,
    seasons: [series.seasons[0], season(1, 2), season(3, 1)] };
  const f = fixture({ metadata: sparse, details: { a: {
    series: detail([seriesFile(1, 1), seriesFile(1, 2), seriesFile(2, 1), seriesFile(3, 1)]),
  } } });
  t.after(() => f.service.close());
  const { sessionId, current } = await playSeries(f, { season: 1, episode: 2, globalEpisode: 2 });
  const beforeInvalidSelection = requests(f);
  await assert.rejects(f.command(sessionId, 'select.episode', { episode: { season: 2, episode: 1 } }), { code: 'NO_MATCH' });
  await assert.rejects(f.command(sessionId, 'browse.season', { season: 2 }), { code: 'NOT_FOUND' });
  assert.deepEqual(requests(f).slice(beforeInvalidSelection.length).map(call => call.type), ['metadata', 'metadata']);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  await f.command(sessionId, 'media.ended', { attemptId: current.id });
  const next = await f.pending(sessionId);
  assert.deepEqual(coordinates(next.candidate), [3, 1, undefined]);
  assert.deepEqual(coordinates(f.snapshot(sessionId).target), [3, 1, undefined]);
  assert.equal(next.position, 0);
  assert.equal(f.trace('season').some(call => call.season === 2), false);
  assert.deepEqual(f.trace('resolve').map(coordinates), [[1, 2, 2], [3, 1, undefined]]);
  assert.deepEqual(f.trace('search'), []);
});

test('F12 failed next-episode addresses stay unavailable at the accurate cross-season target', async t => {
  const f = fixture({ metadata: series, details: {
    a: { series: seriesDetail },
    b: { backup: detail([seriesFile(2, 1)]) },
  }, pages: { b: [[{ vod_id: 'backup', vod_name: tv.title }]] },
  async onResolve(candidate) {
    if (candidate.season === 2 && candidate.episode === 1) throw error('MEDIA_UNAVAILABLE');
  } });
  t.after(() => f.service.close());
  const { sessionId, current } = await playSeries(f, { season: 1, episode: 2, globalEpisode: 2 });
  await f.command(sessionId, 'media.ended', { attemptId: current.id });
  await until(() => f.snapshot(sessionId).stage === 'unavailable');
  const state = f.snapshot(sessionId);
  assert.deepEqual(coordinates(state.target), [2, 1, 3]);
  assert.equal(state.error, 'MEDIA_UNAVAILABLE');
  assert.equal(state.current, null);
  assert.equal(state.attempt, null);
  assert.deepEqual(f.trace('resolve').map(call => [call.sourceKey, ...coordinates(call)]),
    [['a', 1, 2, 2], ['a', 2, 1, 3], ['b', 2, 1, 3]]);
  assert.deepEqual(f.trace('prepare').map(coordinates), [[1, 2, 2]]);
  assert.deepEqual(f.trace('search').map(call => [call.sourceKey, call.page]), [['a', 1], ['b', 1]]);
  assert.deepEqual(coordinates(f.library.read().history[0].episode), [1, 2, 2]);
  assert.equal(f.library.read().history[0].ended, true);
});

test('F13a shortvideo schedules only the real cross-season neighbors without all-source discovery', async t => {
  const f = fixture({ metadata: series, details: { a: { series: seriesDetail } } });
  t.after(() => f.service.close());
  const { sessionId, current } = await playSeries(f, { season: 1, episode: 2, globalEpisode: 2 });
  const initialDetails = f.trace('detail').length;
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => {
    const entries = f.snapshot(sessionId).prefetch;
    return entries.length === 2 && entries.every(entry => ['prepared', 'buffered'].includes(entry.state));
  });
  assert.deepEqual(f.trace('resolve').map(coordinates), [[1, 2, 2], [2, 1, 3], [1, 1, 1]]);
  assert.deepEqual(f.snapshot(sessionId).prefetch.map(entry => coordinates(entry.target)), [[2, 1, 3], [1, 1, 1]]);
  assert.equal(f.trace('detail').length, initialDetails);
  assert.deepEqual(f.trace('search'), []);
  await f.command(sessionId, 'media.progress', { attemptId: current.id, position: 41, duration: 120, paused: true });
  await f.command(sessionId, 'next');
  const next = await f.pending(sessionId);
  assert.deepEqual(coordinates(next.candidate), [2, 1, 3]);
  assert.equal(next.position, 0);
  assert.equal(next.paused, false);
  assert.equal(f.trace('resolve').length, 3, 'Selecting the known cross-season neighbor adds no resolver call');
  assert.equal(f.trace('prepare').length, 3);
  assert.deepEqual(f.trace('search'), []);
});

test('F13b rapid selections fence a late cross-season neighbor and only the latest attempt can commit', async t => {
  const late = deferred(), settled = deferred();
  const f = fixture({ metadata: series, details: { a: { series: seriesDetail } },
    async onResolve(candidate) {
      if (candidate.season === 2 && candidate.episode === 1) {
        await late.promise;
        settled.resolve();
      }
    } });
  t.after(async () => { late.resolve(); await f.service.close(); });
  const { sessionId, current } = await playSeries(f, { season: 1, episode: 2, globalEpisode: 2 });
  await f.command(sessionId, 'shortvideo', { enabled: true });
  await until(() => f.trace('resolve').some(call => call.season === 2 && call.episode === 1));
  await f.command(sessionId, 'select.episode', { episode: { season: 2, episode: 2 } });
  const superseded = await f.pending(sessionId);
  assert.deepEqual(coordinates(superseded.candidate), [2, 2, 4]);
  const supersededReceipt = f.service.attempt({ sessionId, attemptId: superseded.id });
  await f.command(sessionId, 'select.episode', { episode: { season: 2, episode: 3 } });
  const latest = await f.pending(sessionId);
  assert.deepEqual(coordinates(latest.candidate), [2, 3, 5]);
  assert.equal(latest.position, 0);
  assert.notEqual(latest.id, superseded.id);
  await until(() => !f.resources.has(supersededReceipt.id));
  late.resolve();
  await settled.promise;
  await delay(2);
  assert.equal(f.snapshot(sessionId).attempt.id, latest.id);
  assert.equal(f.snapshot(sessionId).current.attemptId, current.id);
  assert.deepEqual(f.trace('resolve').map(coordinates), [[1, 2, 2], [2, 1, 3], [2, 2, 4], [2, 3, 5]]);
  assert.deepEqual(f.trace('prepare').map(coordinates), [[1, 2, 2], [2, 2, 4], [2, 3, 5]]);
  assert.deepEqual(f.trace('search'), []);
  await assert.rejects(f.command(sessionId, 'media.started', { attemptId: superseded.id }), { code: 'STALE_SESSION' });
  await f.command(sessionId, 'shortvideo', { enabled: false });
  await f.command(sessionId, 'media.started', { attemptId: latest.id });
  assert.equal(f.snapshot(sessionId).current.attemptId, latest.id);
  assert.deepEqual(coordinates(f.snapshot(sessionId).target), [2, 3, 5]);
  assert.equal(f.trace('prepare').length, 3);
  assert.deepEqual(f.trace('search'), []);
});
