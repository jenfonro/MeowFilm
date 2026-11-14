import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { createFilmSearchService } from '../packages/film-playback/src/search.js';

const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((a, b) => { resolve = a; reject = b; });
  return { promise, resolve, reject };
};
const source = { key: 'a', engine: 'cat', installationId: 'a', siteId: 'site', generation: 1,
  name: '甲源', enabled: true, searchable: true };
const sourceB = { ...source, key: 'b', installationId: 'b', name: '乙源' };
const page = (list, pageNumber = 1, pagecount) => ({
  raw: { list, ...(pagecount === undefined ? {} : { pagecount }) },
  items: list, page: pageNumber, explicitPageCount: pagecount !== undefined,
  hasMore: pagecount === undefined ? list.length > 0 : pageNumber < pagecount,
});

async function until(work, description = 'search state') {
  const timeout = Date.now() + 8_000;
  do {
    const value = await work();
    if (value) return value;
    await tick();
  } while (Date.now() < timeout);
  assert.fail(`Timed out waiting for ${description}`);
}
function setup(t, { search, metadata, getRules, cachedEntry, invoke } = {}) {
  const calls = [];
  const data = {
    async search(source, query, number, options) {
      calls.push(['search', source.key, query, number]);
      return search ? search(source, query, number, options) : page([], number, 1);
    },
    async metadata(provider, path, query, options) {
      calls.push(['metadata', provider, path, query, options.imageSize]);
      if (metadata) return metadata(provider, path, query, options);
      throw Object.assign(new Error('not configured'), { code: 'CAPABILITY_UNAVAILABLE' });
    },
    async cachedEntry(request, options) {
      calls.push(['receipt', structuredClone(request)]);
      return cachedEntry?.(request, options);
    },
  };
  const nativeProgram = new NativeJsonProgram(readFileSync(new URL('../packages/film-search/film-search.js', import.meta.url), 'utf8'));
  // Inject failures at the public processor boundary; successful projections
  // still execute the real shared QuickJS program.
  const program = invoke ? {
    invoke: (input, options) => invoke(input, options, () => nativeProgram.invoke(input, options)),
    close: () => nativeProgram.close(),
  } : nativeProgram;
  const service = createFilmSearchService({ data, program,
    rules: { snapshot: async () => getRules ? getRules() : { rules: { aggregateRegexRules: [] }, revision: 'one' } },
    now: () => Date.UTC(2026, 10, 10) });
  t.after(() => service.close());
  const snapshot = id => service.snapshot({ sessionId: id });
  const complete = id => until(() => {
    const value = snapshot(id);
    return !value.loading && value;
  }, 'search completion');
  return { service, calls, snapshot, complete };
}

test('one source page is complete; raw and aggregate views keep typed identities without another fetch', async t => {
  const rows = [{ id: 0, name: '星河', displayKey: 'group:站点原字段',
    extension: { displayKey: 'group:嵌套原字段' } }, { id: '0', name: '星河' },
    ...Array.from({ length: 48 }, (_, index) => ({ id: `film-${index}`, name: `作品${index}` }))];
  const { service, calls, complete } = setup(t, { search: async () => page(rows, 1, 1) });
  const opened = await service.open({ query: '星河', sources: [source] });
  const value = await complete(opened.sessionId);
  assert.equal(value.siteItems.length, 50);
  assert.equal(value.rawCards.length, 50);
  assert.deepEqual(value.rawCards.find(card => card.detailId === 0).raw, rows[0]);
  const group = value.cards.find(card => card.aggregateItems?.length === 2);
  assert.ok(group);
  assert.deepEqual(group.aggregateItems.map(item => item.detailId), [0, '0']);
  for (const raw of value.rawCards.filter(card => card.title === '星河')) {
    const handoff = service.handoff({ sessionId: opened.sessionId, selectionId: raw.displayKey, sources: [source] });
    assert.equal(handoff.target.id, raw.displayKey);
    assert.equal(handoff.target.source.detailId, raw.detailId);
    assert.deepEqual(handoff.selectionMembers, [{ sourceKey: 'a', detailId: raw.detailId }]);
    assert.deepEqual(handoff.results, [{ sourceKey: 'a', items: [rows.find(row => row.id === raw.detailId)] }]);
    assert.equal(handoff.metadata, null);
  }
  const handoff = service.handoff({ sessionId: opened.sessionId, selectionId: group.displayKey, sources: [source] });
  assert.equal(handoff.target.id, group.displayKey);
  assert.deepEqual(handoff.target.members.map(member => member.detailId), [0, '0']);
  assert.deepEqual(handoff.selectionMembers, handoff.target.members);
  assert.deepEqual(handoff.results, [{ sourceKey: 'a', items: rows.slice(0, 2) }]);
  assert.deepEqual(handoff.search, [{ sourceKey: 'a', page: 1, completed: true }]);
  assert.equal(calls.filter(call => call[0] === 'search').length, 1);
});

test('handoff separates full search authorization from card members while raw selection stays fixed across late same-name sources', async t => {
  const gate = deferred();
  const first = { id: 0, name: '星河' }, later = { id: '0', name: '星河' };
  const sameNameSource = { ...sourceB, name: source.name };
  const outsideSource = { ...source, key: 'outside', installationId: 'outside' };
  t.after(() => gate.resolve(page([later, { id: 'unrelated-b', name: '别片乙' }], 1, 1)));
  const entries = [];
  const { service, calls, complete, snapshot } = setup(t, {
    search: async item => item.key === 'a'
      ? page([first, { id: 'unrelated-a', name: '别片甲' }], 1, 1) : gate.promise,
    cachedEntry: async request => {
      const entry = { request, producer: 'accepted', storedAt: 42 };
      entries.push(entry); return entry;
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source, sameNameSource] });
  const early = await until(() => snapshot(sessionId).cards.length && snapshot(sessionId));
  const selectionId = early.cards.find(card => card.detailId === 0).displayKey;
  const rawId = early.rawCards.find(card => card.detailId === 0).displayKey;
  const sources = [source, sameNameSource, outsideSource];
  const before = service.handoff({ sessionId, selectionId, sources });
  assert.equal(before.target.metadataProvider, undefined);
  assert.deepEqual(before.sources, [source, sameNameSource]);
  assert.deepEqual(before.selectionMembers, [{ sourceKey: 'a', detailId: 0 }]);
  assert.deepEqual(before.results, [{ sourceKey: 'a', items: [first] }]);
  assert.deepEqual(before.resultHints, []);
  assert.deepEqual(before.search, [
    { sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 0, completed: false },
  ]);
  const rawBefore = service.handoff({ sessionId, selectionId: rawId, sources });
  assert.deepEqual(rawBefore.sources, before.sources);
  assert.deepEqual(rawBefore.selectionMembers, before.selectionMembers);
  assert.deepEqual(rawBefore.results, before.results);
  assert.throws(() => service.handoff({ sessionId, selectionId: rawId,
    sources: [{ ...source, enabled: false }, sameNameSource] }), error => error.code === 'SOURCE_NOT_FOUND');
  assert.throws(() => service.handoff({ sessionId, selectionId: rawId,
    sources: [{ ...source, installationId: 'forged' }, sameNameSource] }), error => error.code === 'SOURCE_NOT_FOUND');
  assert.throws(() => service.handoff({ sessionId, selectionId: 'client-only', sources }),
    error => error.code === 'NOT_FOUND');

  gate.resolve(page([later, { id: 'unrelated-b', name: '别片乙' }], 1, 1));
  const final = await complete(sessionId);
  assert.equal(final.cards.find(card => card.title === '星河').displayKey, selectionId);
  const after = service.handoff({ sessionId, selectionId, sources });
  assert.equal(after.target.id, before.target.id);
  assert.deepEqual(after.searchReference, before.searchReference);
  assert.deepEqual(after.sources, before.sources);
  assert.deepEqual(after.selectionMembers, [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'b', detailId: '0' }]);
  assert.deepEqual(after.results, [{ sourceKey: 'a', items: [first] }, { sourceKey: 'b', items: [later] }]);
  assert.deepEqual(after.search, [
    { sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 1, completed: true },
  ]);
  assert.deepEqual(after.cacheEntries, entries);
  const rawAfter = service.handoff({ sessionId, selectionId: rawId, sources });
  assert.deepEqual(rawAfter.target, rawBefore.target);
  assert.deepEqual(rawAfter.selectionMembers, rawBefore.selectionMembers);
  assert.deepEqual(rawAfter.results, rawBefore.results);
  assert.deepEqual(rawAfter.sources, after.sources);
  assert.deepEqual(rawAfter.search, after.search);
  assert.deepEqual(rawAfter.cacheEntries, entries);
  assert.equal(rawAfter.metadata, null);
  assert.equal(rawAfter.metadataTarget, undefined);
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => [call[1], call[3]]),
    [['a', 1], ['b', 1]]);
  assert.equal(calls.filter(call => call[0] === 'receipt').length, 2);
});

test('explicit more retries only failed cursor and appends new groups without reordering existing cards', async t => {
  let firstB = true;
  const { service, calls, complete } = setup(t, {
    search: async (item, _query, number) => {
      if (item.key === 'b' && firstB) {
        firstB = false; throw Object.assign(new Error('offline'), { code: 'NETWORK_ERROR' });
      }
      if (item.key === 'a' && number === 1) return page([{ id: 'x', name: '星河外传' }], 1, 2);
      if (item.key === 'a') return page([{ id: 'x', name: '星河外传' }, { id: 'y', name: '星河' }], 2, 2);
      return page([{ id: 'z', name: '星河外传' }], 1, 1);
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source, sourceB] });
  const first = await complete(sessionId);
  assert.equal(first.hasMore, true);
  assert.equal(calls.filter(call => call[0] === 'search' && call[1] === 'b').length, 1);
  const firstKey = first.cards[0].displayKey;
  await service.command({ sessionId, action: 'more' });
  const next = await complete(sessionId);
  assert.equal(next.cards[0].displayKey, firstKey);
  assert.equal(next.cards[0].aggregateItems.length, 2);
  assert.equal(next.cards[1].title, '星河');
  assert.equal(next.rawCards.length, 3);
  assert.equal(next.hasMore, false);
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => [call[1], call[3]]),
    [['a', 1], ['b', 1], ['a', 2], ['b', 1]]);
});

test('an implicit duplicate page ends its source, but explicit page counts take precedence', async t => {
  const { service, calls, complete } = setup(t, {
    search: async (item, _query, number) => page([{ id: 'same', name: '星河' }], number, item.key === 'b' ? 3 : undefined),
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source, sourceB] });
  await complete(sessionId);
  await service.command({ sessionId, action: 'more' }); await complete(sessionId);
  await service.command({ sessionId, action: 'more' });
  const last = await complete(sessionId);
  assert.equal(last.hasMore, false);
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => [call[1], call[3]]),
    [['a', 1], ['b', 1], ['a', 2], ['b', 2], ['b', 3]]);
});

test('late metadata changes the selected group carrier but never changes a raw selection or repeats search', async t => {
  const metadataGate = deferred();
  t.after(() => metadataGate.resolve({ results: [] }));
  const { service, calls, complete, snapshot } = setup(t, {
    search: async () => page([{ id: 'a', name: '星河' }], 1, 1),
    metadata: async (_provider, path) => path.includes('/search/')
      ? metadataGate.promise : { id: 12, name: '星河', genres: [], seasons: [], status: 'Ended' },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  const early = await until(() => snapshot(sessionId).cards.length && snapshot(sessionId));
  const group = early.cards[0], raw = early.rawCards[0];
  const before = service.handoff({ sessionId, selectionId: group.displayKey, sources: [source] });
  const rawBefore = service.handoff({ sessionId, selectionId: raw.displayKey, sources: [source] });
  assert.equal(before.target.metadataProvider, undefined);
  assert.deepEqual(before.selectionMembers, [{ sourceKey: 'a', detailId: 'a' }]);
  assert.deepEqual(before.results, [{ sourceKey: 'a', items: [{ id: 'a', name: '星河' }] }]);
  metadataGate.resolve({ results: [{ id: 12, media_type: 'tv', name: '星河' }] });
  await complete(sessionId);
  const after = service.handoff({ sessionId, selectionId: group.displayKey, sources: [source] });
  assert.equal(after.target.kind, 'tv');
  assert.equal(after.target.id, group.displayKey);
  assert.equal(after.target.metadataId, '12');
  assert.equal(after.metadata.id, 12);
  assert.deepEqual(after.selectionMembers, before.selectionMembers);
  assert.deepEqual(after.results, before.results);
  const rawAfter = service.handoff({ sessionId, selectionId: raw.displayKey, sources: [source] });
  assert.deepEqual(rawAfter.target, rawBefore.target);
  assert.deepEqual(rawAfter.results, rawBefore.results);
  assert.equal(rawAfter.metadataTarget, undefined);
  assert.equal(calls.filter(call => call[0] === 'search').length, 1);
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[2]), ['/3/search/multi', '/3/tv/12']);
  assert.equal(calls.some(call => call[0] === 'metadata' && call[1] === 'douban'), false);
});

for (const kind of ['movie', 'tv']) {
  test(`handoff keeps exact typed members across a late ${kind} carrier and later cross-season results`, async t => {
    const metadataGate = deferred();
    t.after(() => metadataGate.resolve({ results: [] }));
    const first = { id: 0, name: '星河 第1季' };
    const later = { id: '0', name: '星河 第2季' };
    const other = { id: 'not-selected', name: '别片', aka: ['星河 第1季'] };
    const { service, calls, complete, snapshot } = setup(t, {
      search: async (_source, _query, number) => page(number === 1 ? [first] : [later, other], number, 2),
      metadata: async (_provider, path) => path.includes('/search/')
        ? metadataGate.promise : { id: 12, title: '星河', name: '星河', genres: [], seasons: [], status: 'Ended' },
    });
    const { sessionId } = await service.open({ query: '星河', sources: [source] });
    const early = await until(() => snapshot(sessionId).cards.length && snapshot(sessionId));
    const selectionId = early.cards[0].displayKey;
    const before = service.handoff({ sessionId, selectionId, sources: [source] });
    assert.equal(before.target.metadataProvider, undefined);
    assert.deepEqual(before.selectionMembers, [{ sourceKey: 'a', detailId: 0 }]);
    metadataGate.resolve({ results: [{ id: 12, media_type: kind, title: '星河', name: '星河' }] });
    await complete(sessionId);
    const associated = service.handoff({ sessionId, selectionId, sources: [source] });
    assert.equal(associated.target.kind, kind);
    assert.deepEqual(associated.selectionMembers, before.selectionMembers);
    await service.command({ sessionId, action: 'more' });
    const next = await complete(sessionId);
    const handoff = service.handoff({ sessionId, selectionId, sources: [source] });
    assert.equal(handoff.target.kind, kind);
    assert.equal(handoff.target.id, selectionId);
    assert.equal(handoff.target.metadataId, '12');
    assert.deepEqual(handoff.selectionMembers,
      [{ sourceKey: 'a', detailId: 0 }, { sourceKey: 'a', detailId: '0' }]);
    assert.deepEqual(handoff.results, [{ sourceKey: 'a', items: [first, later] }]);
    assert.deepEqual(handoff.search, [{ sourceKey: 'a', page: 2, completed: true }]);
    const raw = service.handoff({ sessionId,
      selectionId: next.rawCards.find(card => card.detailId === '0').displayKey, sources: [source] });
    assert.equal(raw.target.source.detailId, '0');
    assert.deepEqual(raw.selectionMembers, [{ sourceKey: 'a', detailId: '0' }]);
    assert.deepEqual(raw.results, [{ sourceKey: 'a', items: [later] }]);
    assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => call[3]), [1, 2]);
    assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[2]),
      ['/3/search/multi', `/3/${kind}/12`]);
  });
}

test('a new rules snapshot affects new sessions only, while a stopped session resumes completed pages', async t => {
  let revision = 'one';
  const { service, calls, complete } = setup(t, {
    getRules: () => ({ rules: { aggregateRegexRules: revision === 'one' ? [] : ['附录'] }, revision }),
    search: async (_source, _query, number) => page([{ id: number, name: '星河附录' }], number, 2),
  });
  const a = await service.open({ query: '星河', sources: [source] });
  const first = await complete(a.sessionId);
  await service.command({ sessionId: a.sessionId, action: 'stop' });
  revision = 'two';
  await service.command({ sessionId: a.sessionId, action: 'resume' });
  const resumed = await complete(a.sessionId);
  assert.equal(resumed.rulesRevision, 'one');
  assert.equal(resumed.cards[0].displayKey, first.cards[0].displayKey);
  assert.equal(calls.filter(call => call[0] === 'search').length, 1);
  const b = await service.open({ query: '星河', sources: [source] });
  const second = await complete(b.sessionId);
  assert.equal(second.rulesRevision, 'two');
  assert.notEqual(second.cards[0].groupKey, first.cards[0].groupKey);
});

test('stop revokes publication and new metadata-detail scheduling; data owner can finish its accepted request', async t => {
  const gate = deferred(), started = deferred();
  let producerFinished = false;
  const { service, snapshot } = setup(t, {
    search: async (_source, _query, _number, { signal }) => {
      started.resolve();
      // This fixture models the data owner's finite producer, not a native
      // transport: consumer cancellation must not be mistaken for cache drain.
      const result = await gate.promise;
      producerFinished = true;
      assert.equal(signal.aborted, true);
      return result;
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  await started.promise;
  await service.command({ sessionId, action: 'stop' });
  const stopped = snapshot(sessionId);
  gate.resolve(page([{ id: 1, name: '星河' }], 1, 1));
  await until(() => producerFinished);
  await tick();
  assert.equal(snapshot(sessionId).revision, stopped.revision);
  assert.deepEqual(snapshot(sessionId).cards, []);
});

test('closing a snapshot stream detaches only that observer, and reconnect gets the latest full state', async t => {
  const gate = deferred();
  const { service, complete } = setup(t, { search: async () => gate.promise });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  const first = service.events(sessionId);
  const chunks = [];
  first.body.on('data', data => chunks.push(data.toString()));
  await until(() => chunks.length);
  await first.dispose();
  gate.resolve(page([{ id: 1, name: '星河' }], 1, 1));
  const final = await complete(sessionId);
  const next = service.events(sessionId, final.sequence);
  const value = await new Promise((resolve, reject) => {
    next.body.once('data', data => resolve(data.toString()));
    next.body.once('error', reject);
  });
  assert.ok(value.includes(`id: ${final.sequence}\n`));
  assert.equal(JSON.parse(value.split('\ndata: ')[1].trim()).cards[0].title, '星河');
  await next.dispose();
  await service.release({ sessionId });
  assert.throws(() => service.snapshot({ sessionId }), error => error.code === 'NOT_FOUND');
});

test('source invalidation keeps only selected hints and cannot bless an old cursor with a new generation', async t => {
  const selected = { id: 1, name: '星河' };
  const { service, complete } = setup(t, {
    search: async () => page([selected, { id: 'other', name: '别片' }], 1, 1),
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  const value = await complete(sessionId);
  await service.invalidate({ kind: 'source', engine: 'cat', installationId: 'a' });
  const handoff = service.handoff({ sessionId, selectionId: value.cards.find(card => card.detailId === 1).displayKey,
    sources: [{ ...source, generation: 2 }] });
  assert.deepEqual(handoff.results, []);
  assert.deepEqual(handoff.search, []);
  assert.deepEqual(handoff.resultHints, [{ sourceKey: 'a', items: [selected] }]);
  assert.deepEqual(handoff.cacheEntries, []);
  await assert.rejects(service.command({ sessionId, action: 'resume' }), error => error.code === 'STALE_SESSION');
});

test('cancelling open while rules initialize does not leave a hidden search producer', async t => {
  const gate = deferred();
  const { service, calls } = setup(t, { getRules: () => gate.promise });
  const controller = new AbortController();
  const opening = service.open({ query: '星河', sources: [source] }, { signal: controller.signal });
  const cancelled = assert.rejects(opening, error => error.code === 'CANCELLED');
  controller.abort();
  gate.resolve({ rules: { aggregateRegexRules: [] }, revision: 'one' });
  await cancelled;
  assert.deepEqual(calls, []);
});

test('explicit more after a completed stop advances pages, while resume alone does not', async t => {
  const { service, calls, complete } = setup(t, {
    search: async (_source, _query, number) => page([{ id: number, name: `星河第${number}季` }], number, 2),
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  await complete(sessionId);
  await service.command({ sessionId, action: 'stop' });
  await service.command({ sessionId, action: 'more' });
  assert.equal((await complete(sessionId)).rawCards.length, 2);
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => call[3]), [1, 2]);
});

test('a later stop cancels an admitted more before it schedules another page', async t => {
  const { service, calls, complete } = setup(t, {
    search: async (_source, _query, number) => page([{ id: number, name: '星河' }], number, 2),
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  await complete(sessionId);
  const more = service.command({ sessionId, action: 'more' });
  await service.command({ sessionId, action: 'stop' });
  await more;
  assert.equal((await complete(sessionId)).loading, false);
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => call[3]), [1]);
});

test('a repeated stop supersedes a queued resume while accepted work is draining', async t => {
  const gate = deferred(), started = deferred();
  const { service, calls, snapshot } = setup(t, { search: async () => {
    started.resolve(); return gate.promise;
  } });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  await started.promise;
  await service.command({ sessionId, action: 'stop' });
  const resume = service.command({ sessionId, action: 'resume' });
  await service.command({ sessionId, action: 'stop' });
  gate.resolve(page([{ id: 1, name: '星河' }], 1, 1));
  await resume;
  assert.equal(snapshot(sessionId).siteState, 'cancelled');
  assert.deepEqual(snapshot(sessionId).cards, []);
  assert.equal(calls.filter(call => call[0] === 'search').length, 1);
});

for (const keepResult of [false, true]) {
  test(`a processor timeout stays failed despite ${keepResult ? 'nonempty' : 'empty'} source success, and more reuses the accepted page and receipts`, async t => {
    let failSite = true;
    const entries = [];
    const { service, calls, complete } = setup(t, {
      search: async item => page(item.key === 'a'
        ? [{ id: 0, name: '星河', extra: { original: true } }]
        : keepResult ? [{ id: 'b', name: '星河外传' }] : [], 1, 1),
      cachedEntry: async request => {
        const entry = { request, producer: 'accepted', storedAt: 42 };
        entries.push(entry); return entry;
      },
      invoke: async (input, _options, run) => {
        if (failSite && input.operation === 'site' && input.site.siteKey === 'a') {
          throw Object.assign(new Error('processor deadline'), { code: 'TIMEOUT' });
        }
        return run();
      },
    });
    const { sessionId } = await service.open({ query: '星河', sources: [source, sourceB] });
    const failed = await complete(sessionId);
    assert.equal(failed.siteState, 'failed');
    assert.equal(failed.metadataState, 'unavailable');
    assert.equal(failed.hasMore, true);
    assert.deepEqual(failed.siteItems.map(item => item.detailId), keepResult ? ['b'] : []);
    assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => [call[1], call[3]]),
      [['a', 1], ['b', 1]]);
    assert.equal(entries.length, 2);

    failSite = false;
    await service.command({ sessionId, action: 'more' });
    const recovered = await complete(sessionId);
    assert.equal(recovered.siteState, 'completed');
    assert.equal(recovered.hasMore, false);
    assert.deepEqual(recovered.rawCards.find(card => card.detailId === 0).raw.extra, { original: true });
    if (keepResult) {
      assert.equal(recovered.cards[0].displayKey, failed.cards[0].displayKey);
      assert.equal(recovered.rawCards[0].displayKey, failed.rawCards[0].displayKey);
    }
    assert.equal(calls.filter(call => call[0] === 'search').length, 2);
    assert.equal(calls.filter(call => call[0] === 'receipt').length, 2);
    const handoff = service.handoff({ sessionId, selectionId: recovered.cards[0].displayKey,
      sources: [source, sourceB] });
    assert.deepEqual(handoff.search, [
      { sourceKey: 'a', page: 1, completed: true }, { sourceKey: 'b', page: 1, completed: true },
    ]);
    assert.deepEqual(handoff.cacheEntries, entries);
  });
}

test('a real regular-expression interruption fails the channel and the shared processor serves a later session', async t => {
  const { service, calls, complete } = setup(t, {
    getRules: () => ({ rules: { aggregateRegexRules: ['(a+)+$'] }, revision: 'one' }),
    search: async (item, query) => page(item.key === 'b' ? [] : [
      { id: 0, name: query === '超时' ? `${'a'.repeat(35)}!` : '恢复' },
    ], 1, 1),
  });
  const failedSession = await service.open({ query: '超时', sources: [source, sourceB] });
  const failed = await complete(failedSession.sessionId);
  assert.equal(failed.siteState, 'failed');
  assert.equal(failed.metadataState, 'unavailable');
  assert.equal(failed.hasMore, true);
  assert.deepEqual(failed.cards, []);
  const later = await service.open({ query: '恢复', sources: [source] });
  const recovered = await complete(later.sessionId);
  assert.equal(recovered.siteState, 'completed');
  assert.equal(recovered.rawCards[0].detailId, 0);
  assert.equal(recovered.cards[0].title, '恢复');
  assert.deepEqual(calls.filter(call => call[0] === 'search').map(call => [call[1], call[2], call[3]]),
    [['a', '超时', 1], ['b', '超时', 1], ['a', '恢复', 1]]);
});

for (const failureAt of [2, 3]) {
  test(`a last-page ${failureAt === 2 ? 'raw' : 'final'} display failure retries without refetching or regressing the handoff`, async t => {
    let failDisplay = true, displayCalls = 0, mappedPages = 0;
    const entries = [];
    const { service, calls, complete } = setup(t, {
      search: async (_source, _query, number) => page(number === 1
        ? [{ id: 0, name: '星河外传' }] : [{ id: '0', name: '星河' }], number, 2),
      cachedEntry: async request => {
        const entry = { request, producer: 'accepted', storedAt: 42 };
        entries.push(entry); return entry;
      },
      invoke: async (input, _options, run) => {
        if (input.operation === 'site') mappedPages++;
        if (input.operation === 'display' && input.siteItems.length === 2 &&
            ++displayCalls === failureAt && failDisplay) {
          throw Object.assign(new Error('projection budget'), { code: 'RESOURCE_LIMIT' });
        }
        return run();
      },
    });
    const { sessionId } = await service.open({ query: '星河', sources: [source] });
    const first = await complete(sessionId);
    await service.command({ sessionId, action: 'more' });
    const failed = await complete(sessionId);
    assert.equal(failed.siteState, 'failed');
    assert.equal(failed.metadataState, 'unavailable');
    assert.equal(failed.hasMore, true);
    assert.equal(failed.siteItems.length, 2);
    if (failureAt === 2) {
      assert.deepEqual(failed.cards, first.cards);
      assert.deepEqual(failed.rawCards, first.rawCards);
    } else {
      assert.equal(failed.cards.length, 2);
      assert.equal(failed.rawCards.length, 2);
    }
    const handoff = service.handoff({ sessionId, selectionId: first.cards[0].displayKey, sources: [source] });
    assert.deepEqual(handoff.search, [{ sourceKey: 'a', page: 2, completed: true }]);
    assert.deepEqual(handoff.cacheEntries, entries);
    const before = structuredClone(calls);
    failDisplay = false;
    await service.command({ sessionId, action: 'more' });
    const recovered = await complete(sessionId);
    assert.equal(recovered.siteState, 'completed');
    assert.equal(recovered.hasMore, false);
    assert.equal(recovered.cards[0].displayKey, first.cards[0].displayKey);
    assert.deepEqual(recovered.rawCards.map(card => card.detailId), [0, '0']);
    assert.equal(recovered.cards.length, 2);
    assert.equal(mappedPages, 2);
    assert.deepEqual(calls, before);
    assert.deepEqual(service.handoff({ sessionId, selectionId: first.cards[0].displayKey,
      sources: [source] }).search, handoff.search);
  });
}

test('metadata detail network failure preserves its summary and settles without a computation error', async t => {
  const { service, calls, complete } = setup(t, {
    metadata: async (_provider, path) => {
      if (path.includes('/search/')) return { results: [{ id: 12, media_type: 'tv', name: '星河' }] };
      throw Object.assign(new Error('offline'), { code: 'NETWORK_ERROR' });
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [] });
  const value = await complete(sessionId);
  assert.equal(value.siteState, 'completed');
  assert.equal(value.metadataState, 'completed');
  assert.equal(value.hasMore, false);
  assert.equal(value.cards[0].title, '星河');
  assert.equal(value.cards[0].tmdbId, 12);
  assert.deepEqual(value.metadataDetails, {});
  await service.command({ sessionId, action: 'more' });
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[2]),
    ['/3/search/multi', '/3/tv/12']);
});

test('detail computation failure remains retryable, preserving successful details and a failed-network summary', async t => {
  let failDetail = true;
  const entries = [], detailCalls = [];
  const payloads = new Map([
    [13, { id: 13, name: '星河正传', genres: [], seasons: [], status: 'Ended', extra: { accepted: true } }],
    [14, { id: 14, name: '星河后传', genres: [], seasons: [], status: 'Ended' }],
  ]);
  const { service, calls, complete } = setup(t, {
    metadata: async (_provider, path) => {
      if (path.includes('/search/')) return { results: [
        { id: 12, media_type: 'tv', name: '星河' },
        { id: 13, media_type: 'tv', name: '星河摘要' },
        { id: 14, media_type: 'tv', name: '星河后传摘要' },
      ] };
      const id = Number(path.split('/').at(-1));
      if (id === 12) throw Object.assign(new Error('offline'), { code: 'NETWORK_ERROR' });
      return payloads.get(id);
    },
    cachedEntry: async request => {
      const entry = { request, producer: 'accepted', storedAt: 42 };
      entries.push(entry); return entry;
    },
    invoke: async (input, _options, run) => {
      if (input.operation === 'detail') {
        detailCalls.push(input.item.tmdbId);
        if (failDetail && input.item.tmdbId === 13) {
          throw Object.assign(new Error('processor deadline'), { code: 'TIMEOUT' });
        }
      }
      return run();
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [] });
  const failed = await complete(sessionId);
  assert.equal(failed.siteState, 'completed');
  assert.equal(failed.metadataState, 'failed');
  assert.equal(failed.hasMore, true);
  assert.equal(failed.tmdbItems.find(item => item.tmdbId === 12).title, '星河');
  assert.equal(failed.tmdbItems.find(item => item.tmdbId === 13).title, '星河摘要');
  assert.equal(failed.tmdbItems.find(item => item.tmdbId === 14).title, '星河后传');
  assert.deepEqual(failed.metadataDetails, Object.fromEntries([...payloads].map(([id, payload]) => [`tv:${id}`, payload])));
  assert.equal(failed.cards.length, 3);
  const previousOrder = failed.cards.map(card => card.displayKey);
  const before = structuredClone(calls);
  failDetail = false;
  await service.command({ sessionId, action: 'more' });
  const recovered = await complete(sessionId);
  assert.equal(recovered.metadataState, 'completed');
  assert.equal(recovered.hasMore, false);
  assert.equal(recovered.tmdbItems.find(item => item.tmdbId === 13).title, '星河正传');
  assert.deepEqual(recovered.cards.map(card => card.displayKey), previousOrder);
  assert.deepEqual(calls, before);
  assert.deepEqual(detailCalls, [13, 14, 13]);
  const handoff = service.handoff({ sessionId,
    selectionId: recovered.cards.find(card => card.tmdbId === 13).displayKey, sources: [] });
  assert.deepEqual(handoff.metadata, payloads.get(13));
  assert.deepEqual(handoff.cacheEntries, entries);
});

test('an initial metadata display failure settles both channels and more resumes the stored search before fetching details', async t => {
  let failDisplay = true;
  const { service, calls, complete } = setup(t, {
    metadata: async (_provider, path) => path.includes('/search/')
      ? { results: [{ id: 12, media_type: 'tv', name: '星河' }] }
      : { id: 12, name: '星河', genres: [], seasons: [], status: 'Ended' },
    invoke: async (input, _options, run) => {
      if (failDisplay && input.operation === 'display' && input.tmdbItems.length) {
        throw Object.assign(new Error('projection budget'), { code: 'RESOURCE_LIMIT' });
      }
      return run();
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [] });
  const failed = await complete(sessionId);
  assert.equal(failed.siteState, 'completed');
  assert.equal(failed.metadataState, 'failed');
  assert.equal(failed.hasMore, true);
  assert.equal(failed.tmdbItems.length, 1);
  assert.equal(failed.metadataSearch.results[0].id, 12);
  assert.deepEqual(failed.cards, []);
  assert.deepEqual(failed.rawCards, []);
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[2]), ['/3/search/multi']);
  failDisplay = false;
  await service.command({ sessionId, action: 'more' });
  const recovered = await complete(sessionId);
  assert.equal(recovered.siteState, 'completed');
  assert.equal(recovered.metadataState, 'completed');
  assert.equal(recovered.hasMore, false);
  assert.equal(recovered.cards[0].tmdbId, 12);
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[2]),
    ['/3/search/multi', '/3/tv/12']);
  assert.deepEqual(calls.filter(call => call[0] === 'receipt').map(call => call[1].path),
    ['/3/search/multi', '/3/tv/12']);
});

test('a failed metadata mapper reuses both original and fallback search receipts on explicit retry', async t => {
  let failMapping = true;
  const entries = [];
  const { service, calls, complete } = setup(t, {
    metadata: async (_provider, path, query) => path.includes('/search/')
      ? { results: query.query === '星河2' ? [] : [{ id: 12, media_type: 'tv', name: '星河' }], original: true }
      : { id: 12, name: '星河', genres: [], seasons: [], status: 'Ended' },
    cachedEntry: async request => {
      const entry = { request, producer: 'accepted', storedAt: 42 };
      entries.push(entry); return entry;
    },
    invoke: async (input, _options, run) => {
      if (failMapping && input.operation === 'tmdb') {
        throw Object.assign(new Error('mapping budget'), { code: 'RESOURCE_LIMIT' });
      }
      return run();
    },
  });
  const { sessionId } = await service.open({ query: '星河2', sources: [] });
  const failed = await complete(sessionId);
  assert.equal(failed.metadataState, 'failed');
  assert.equal(failed.hasMore, true);
  assert.equal(failed.query, '星河2');
  assert.equal(failed.metadataQuery, '星河');
  assert.equal(failed.metadataSearch.original, true);
  assert.equal(failed.metadataSearch.results[0].id, 12);
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => call[3].query), ['星河2', '星河']);
  failMapping = false;
  await service.command({ sessionId, action: 'more' });
  const recovered = await complete(sessionId);
  assert.equal(recovered.metadataState, 'completed');
  assert.equal(recovered.hasMore, false);
  assert.equal(recovered.cards[0].tmdbId, 12);
  assert.deepEqual(calls.filter(call => call[0] === 'metadata').map(call => [call[2], call[3].query]),
    [['/3/search/multi', '星河2'], ['/3/search/multi', '星河'], ['/3/tv/12', undefined]]);
  assert.equal(entries.length, 3);
  assert.deepEqual(service.handoff({ sessionId, selectionId: recovered.cards[0].displayKey,
    sources: [] }).cacheEntries, entries);
});

test('a final metadata-only projection failure remains retryable without refetching or reenriching details', async t => {
  let failDisplay = true, displayCalls = 0, details = 0;
  const { service, calls, complete } = setup(t, {
    metadata: async (_provider, path) => path.includes('/search/')
      ? { results: [{ id: 12, media_type: 'tv', name: '星河' }] }
      : { id: 12, name: '星河', genres: [], seasons: [], status: 'Ended' },
    invoke: async (input, _options, run) => {
      if (input.operation === 'detail') details++;
      if (input.operation === 'display' && ++displayCalls === 5 && failDisplay) {
        throw Object.assign(new Error('projection budget'), { code: 'RESOURCE_LIMIT' });
      }
      return run();
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [] });
  const failed = await complete(sessionId);
  assert.equal(failed.siteState, 'failed');
  assert.equal(failed.metadataState, 'failed');
  assert.equal(failed.hasMore, true);
  assert.equal(failed.cards[0].tmdbId, 12);
  const before = structuredClone(calls);
  failDisplay = false;
  await service.command({ sessionId, action: 'more' });
  const recovered = await complete(sessionId);
  assert.equal(recovered.siteState, 'completed');
  assert.equal(recovered.metadataState, 'completed');
  assert.equal(recovered.hasMore, false);
  assert.deepEqual(recovered.cards, failed.cards);
  assert.deepEqual(recovered.rawCards, failed.rawCards);
  assert.deepEqual(calls, before);
  assert.equal(details, 1);
});

test('stopping a page computation and resuming reuses its successful network read and receipt', async t => {
  const started = deferred();
  let first = true;
  const { service, calls, complete, snapshot } = setup(t, {
    search: async () => page([{ id: 0, name: '星河' }], 1, 1),
    invoke: async (input, options, run) => {
      if (first && input.operation === 'site') {
        first = false; started.resolve();
        await new Promise((_, reject) => options.signal.addEventListener('abort',
          () => reject(Object.assign(new Error('stopped'), { code: 'CANCELLED' })), { once: true }));
      }
      return run();
    },
  });
  const { sessionId } = await service.open({ query: '星河', sources: [source] });
  await started.promise;
  await service.command({ sessionId, action: 'stop' });
  const stopped = snapshot(sessionId);
  assert.equal(stopped.siteState, 'cancelled');
  assert.deepEqual(stopped.cards, []);
  await service.command({ sessionId, action: 'resume' });
  const recovered = await complete(sessionId);
  assert.equal(recovered.siteState, 'completed');
  assert.equal(recovered.round, stopped.round);
  assert.equal(recovered.hasMore, false);
  assert.equal(recovered.rawCards[0].detailId, 0);
  assert.equal(calls.filter(call => call[0] === 'search').length, 1);
  assert.equal(calls.filter(call => call[0] === 'receipt').length, 1);
});
