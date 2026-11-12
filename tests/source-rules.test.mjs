import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { createSourceRules } from '../apps/runtime/modules/source-rules.mjs';
import { NativeEngine } from '../packages/script-host/src/native-engine.js';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred, until } from '../packages/runtime-bridge/test/http-fixture.js';

const fixtures = JSON.parse(await readFile(
  new URL('../packages/novel-rules/test/fixtures.json', import.meta.url), 'utf8'));
const timeout = 15_000;
const identity = { operation: 'identity',
  book: { name: '雨夜来信', author: '林舟', installationId: 'one', bookUrl: '/book' } };
const raw = { operation: 'transform', title: '书', blocks: [{ type: 'text', text: 'A' }],
  book: {}, source: {}, rules: [], conversion: 'none' };
const rule = fields => ({ pattern: 'A', replacement: 'B', isRegex: false,
  isEnabled: true, scopeTitle: true, scopeContent: true, ...fields });
const slowRule = fields => rule({ pattern: '^(a+)+$', isRegex: true, ...fields });
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, 'source.runtime.v2');
  return reply.value;
}
function denied(reply, code) {
  assert.deepEqual(reply, { ok: false, contract: 'source.runtime.v2', code });
}
function setup(t) {
  const module = createSourceRuntimeModule({
    network: async () => { throw Error('pure rules cannot request a network capability'); },
  });
  return { module, rpc: moduleHttp(t, module),
    process: (request, options) => module.methods['source.runtime.rules.process']({ request }, options) };
}
function observeOwners(t) {
  const create = NativeEngine.create, owners = [];
  t.mock.method(NativeEngine, 'create', options => {
    const engine = create(options);
    owners.push(engine);
    return engine;
  });
  return owners;
}
function released(engine) {
  const stats = engine.stats();
  assert.equal(stats.stopped, true, 'completion waits for the actual native thread join');
  for (const field of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
    'hostCalls', 'timers', 'jobsPending', 'heapBytes', 'byteBuffers', 'byteBufferBytes']) {
    assert.equal(stats[field], 0, field);
  }
}

test('rules.process runs the unchanged shared chapter fixtures through the real HTTP and native engine',
  { timeout }, async t => {
    const { rpc } = setup(t);
    for (const fixture of fixtures) {
      const before = structuredClone(fixture.input);
      assert.deepEqual(value(await rpc('rules.process', { request: fixture.input })), fixture.expected, fixture.name);
      assert.deepEqual(fixture.input, before, 'computation never mutates the native-owned request');
    }
    assert.deepEqual(value(await rpc('library.list')), { sources: [] });
  });

test('the public client gives only rules.process no batch deadline while other source calls retain their timeouts',
  { timeout }, async t => {
    const entered = deferred(), release = deferred();
    t.after(() => release.resolve());
    const module = createSourceRuntimeModule({
      network: async () => { throw Error('pure rules cannot request a network capability'); },
    });
    for (const operation of ['rules.process', 'library.list']) {
      const name = 'source.runtime.' + operation, original = module.methods[name];
      module.methods[name] = async (...args) => {
        if (operation === 'rules.process') entered.resolve();
        await release.promise;
        return original(...args);
      };
    }
    const rpc = moduleHttp(t, module);
    await rpc.transport.ready;
    const client = createRuntimeClient({ baseUrl: rpc.transport.runtime.baseUrl, timeout: 50 });
    t.after(() => client.close());
    const request = { ...raw, rules: [
      rule({ order: 1 }), rule({ pattern: 'B', replacement: '书', order: 2 }),
    ], conversion: 'traditional' };
    const pending = client.call('source.runtime.rules.process', { request }, { timeout: 1 });
    pending.catch(() => {});
    try {
      await entered.promise;
      await Promise.all([
        assert.rejects(client.call('source.runtime.library.list'), { code: 'TIMEOUT' }),
        assert.rejects(client.call('source.runtime.library.list', {}, { timeout: 25 }), { code: 'TIMEOUT' }),
      ]);
      release.resolve();
      assert.deepEqual(value(await pending), { title: '書', blocks: [{ type: 'text', text: '書' }] });
    } finally { release.resolve(); }
  });

test('the single rules RPC preserves identity, merge, chapter matching, validation and full URL matching',
  { timeout }, async t => {
    const { rpc } = setup(t);
    const call = async request => value(await rpc('rules.process', { request }));
    assert.deepEqual(await call(identity),
      { name: '雨夜来信', author: '林舟', key: '["book","雨夜来信","林舟"]' });
    const known = { ...identity.book, installationId: 'two', bookUrl: '/other' };
    const merged = await call({ operation: 'merge',
      books: [identity.book, known, { ...known, installationId: 'unknown', author: '未知' }] });
    assert.equal(merged.books.length, 2);
    assert.deepEqual(merged.books[0].candidates, [identity.book, known]);
    assert.deepEqual(await call({ operation: 'matchChapter',
      chapters: [{ name: '第１２章：夜 雨', chapterUrl: '/chapter' }],
      anchor: { chapterTitle: '第十二章 夜雨', chapterIndex: 9 } }), { index: 0, requiresSelection: false });
    assert.deepEqual(await call({ operation: 'validateRule', rule: rule({ pattern: '[', isRegex: true }) }),
      { valid: false, errors: ['pattern'] });
    assert.deepEqual(await call({ operation: 'validateRule', rule: null }), { valid: false, errors: ['rule'] });
    for (const [pattern, url, expected] of [
      ['https://books\\.test/.+', 'https://books.test/书', true],
      ['books', 'https://books.test/', false], ['a|ab', 'ab', false],
      ['a(?:b)?', 'ab', true], ['', '', true],
    ]) assert.equal(await call({ operation: 'matchUrl', pattern, url }), expected);
  });

test('rules input admits only fixed operations and never executes request source text',
  { timeout }, async t => {
    const { rpc } = setup(t);
    for (const params of [{}, { request: null }, { request: { operation: 'eval', source: 'for (;;) {}' } },
      { request: { ...identity, source: 'for (;;) {}' } }, { request: identity, source: 'for (;;) {}' },
      { request: { operation: 'identity' } }, { request: { operation: ['identity'], book: {} } },
      { request: { ...raw, rules: [null] } }, { request: { ...raw, conversion: 'dictionary' } },
      { request: { operation: 'matchUrl', pattern: '[', url: 'private body' } }]) {
      denied(await rpc('rules.process', params), 'INVALID_REQUEST');
    }
    assert.equal(value(await rpc('rules.process', { request: identity })).author, '林舟');
  });

test('transform sorts the complete native-owned rules stably, skips disabled rules and converts once at the end',
  { timeout }, async t => {
    const { rpc } = setup(t);
    const request = { ...raw, rules: [
      rule({ pattern: 'B', replacement: '书', order: 2 }),
      rule({ order: 1 }),
      rule({ pattern: '书', replacement: '读书', order: 2 }),
      rule({ pattern: '[', isRegex: true, order: 0, isEnabled: false, timeoutMillisecond: 0 }),
    ], conversion: 'traditional' };
    assert.deepEqual(value(await rpc('rules.process', { request })),
      { title: '讀書', blocks: [{ type: 'text', text: '讀書' }] });
    assert.deepEqual(value(await rpc('rules.process', { request: {
      ...raw, rules: [rule({ timeoutMillisecond: Number.MAX_SAFE_INTEGER })],
    } })), { title: '书', blocks: [{ type: 'text', text: 'B' }] });
  });

test('an invalid later rule returns no partial chapter and releases the failed engine before recovery',
  { timeout }, async t => {
    const owners = observeOwners(t), { process } = setup(t);
    const request = { ...raw, rules: [rule({ order: 1 }),
      rule({ order: 2, pattern: '[', isRegex: true })] };
    const before = structuredClone(request);
    denied(await process(request), 'INVALID_REQUEST');
    released(owners[0]);
    assert.deepEqual(request, before);
    assert.deepEqual(value(await process(raw)), { title: '书', blocks: [{ type: 'text', text: 'A' }] });
    assert.equal(owners.length, 2);
  });

test('each declared rule budget interrupts actual regex work and preserves TIMEOUT across the public RPC',
  { timeout }, async t => {
    const owners = observeOwners(t), { rpc } = setup(t);
    const request = { ...raw, blocks: [{ type: 'text', text: 'seed' }], rules: [
      rule({ pattern: 'seed', replacement: 'a'.repeat(100) + '!', order: 1 }),
      slowRule({ order: 2, timeoutMillisecond: 35 }),
    ] };
    const before = structuredClone(request), started = Date.now();
    denied(await rpc('rules.process', { request }), 'TIMEOUT');
    assert.ok(Date.now() - started < 2_000, 'the rule keeps its own deadline rather than the default program budget');
    released(owners[0]);
    assert.deepEqual(request, before, 'failure publishes no partially transformed native chapter');
    assert.equal(value(await rpc('rules.process', { request: identity })).name, '雨夜来信');
    assert.equal(owners.length, 2);
  });

for (const operation of ['transform', 'matchUrl']) {
  test(`${operation} keeps its declared default budget on the real native engine`,
    { timeout }, async t => {
      const owners = observeOwners(t), { process } = setup(t);
      await process(identity);
      const text = 'a'.repeat(100) + '!';
      const request = operation === 'transform'
        ? { ...raw, blocks: [{ type: 'text', text }], rules: [slowRule({})] }
        : { operation, pattern: '^(a+)+$', url: text };
      const started = Date.now(), budget = operation === 'transform' ? 3_000 : 2_000;
      denied(await process(request), 'TIMEOUT');
      const elapsed = Date.now() - started;
      assert.ok(elapsed >= budget - 300 && elapsed < budget + 5_000, `actual elapsed ${elapsed}ms`);
      released(owners[0]);
      assert.equal(value(await process(identity)).name, '雨夜来信');
    });
}

for (const ending of ['cancel', 'close']) {
  test(`rules ${ending} drains the active native engine and all accepted requests without publishing partial output`,
    { timeout }, async t => {
      const owners = observeOwners(t), { module, process } = setup(t);
      await process(identity);
      const controller = new AbortController();
      const request = { ...raw, blocks: [{ type: 'text', text: 'a'.repeat(100) + '!' }],
        rules: [slowRule({ timeoutMillisecond: 10_000 })], conversion: 'traditional' };
      const original = owners[0], active = process(request, { signal: controller.signal });
      await until(() => original.stats().activeCalls > 0);
      const queued = process(identity);
      if (ending === 'cancel') {
        controller.abort();
        denied(await active, 'CANCELLED');
        released(original);
        assert.equal(value(await queued).author, '林舟');
        assert.equal(owners.length, 2, 'only a fully released engine can be replaced');
      } else {
        const closing = module.close();
        denied(await active, 'CLOSED');
        denied(await queued, 'CLOSED');
        assert.deepEqual(value(await closing), { closed: true, cleanupComplete: true });
        for (const owner of owners) released(owner);
        denied(await process(identity), 'CLOSED');
      }
    });
}

test('source rules close is idempotent and drains several admitted transforms and queued matches',
  { timeout }, async t => {
    const owners = observeOwners(t), service = createSourceRules();
    t.after(() => service.close());
    await service.process(identity);
    const active = assert.rejects(service.process({ ...raw,
      blocks: [{ type: 'text', text: 'a'.repeat(100) + '!' }],
      rules: [slowRule({ timeoutMillisecond: 10_000 })] }), { code: 'CLOSED' });
    await until(() => owners[0].stats().activeCalls > 0);
    const queued = [service.process(raw), service.process(identity),
      service.process({ operation: 'matchUrl', pattern: '.', url: 'x' })]
      .map(work => assert.rejects(work, { code: 'CLOSED' }));
    const closing = service.close();
    assert.equal(service.close(), closing);
    await Promise.all([closing, active, ...queued]);
    for (const owner of owners) released(owner);
    await assert.rejects(service.process(identity), { code: 'CLOSED' });
  });
