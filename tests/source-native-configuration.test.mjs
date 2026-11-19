import { setTestReadOnly, withTestDatabase } from './storage-test-fixture.mjs';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { rawJSON } from '@meowfilm/runtime-bridge/json';
import { encodeValueNodes } from '@meowfilm/storage/values';
import { nodeInserts } from '@meowfilm/storage/statements';
import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { createScriptHost, ScriptHostError } from '@meowfilm/script-host';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { createSourceLibrary } from '../apps/runtime/modules/source-library.mjs';
import { createSourceWebBroker } from '../apps/runtime/modules/source-web.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';

const contract = 'source.runtime.v2';
const results = fileURLToPath(new URL('../tmp/test-results/source-native/', import.meta.url));
mkdirSync(results, { recursive: true });
const suiteRoot = mkdtempSync(join(results, 'suite-'));
after(() => rmSync(suiteRoot, { recursive: true, force: true }));
const temp = () => mkdtempSync(join(suiteRoot, 'case-'));
const emptyPrivate = () => ({ cookies: [], storage: [] });
const privateState = () => ({
  cookies: [['reader', [[JSON.stringify(['books.invalid', '/', 'fixture']), { name: 'fixture', value: 'mock', domain: 'books.invalid',
    path: '/', hostOnly: true, secure: false, httpOnly: false, sameSite: null, sequence: 1, expires: null }]]]],
  storage: [['reader', [['fixture', { saved: true }]]]],
});
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aCfoAAAAASUVORK5CYII=', 'base64');
const definition = (id = 'books', marker = 'ready') => JSON.stringify({
  bookSourceName: id, bookSourceUrl: `https://${id}.invalid/`, ruleSearch: {}, marker,
});
const source = (id = 'books', patch = {}) => ({
  installationId: id, revision: 1, name: id, sourceUrl: `https://${id}.invalid/`,
  group: '', order: 0, enabled: true, enabledExplore: false, protocol: 'legado',
  definition: definition(id), ...patch,
});
const snapshot = (sources, revision = 1) => ({ revision, sources });
const target = item => ({ installationId: item.installationId, revision: item.revision });
const fault = code => Object.assign(new Error(code), { code });
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function wait(promise, signal) {
  if (signal?.aborted) return Promise.reject(new ScriptHostError('CANCELLED'));
  return new Promise((resolve, reject) => {
    const abort = () => {
      signal?.removeEventListener('abort', abort);
      reject(new ScriptHostError('CANCELLED'));
    };
    signal?.addEventListener('abort', abort, { once: true });
    Promise.resolve(promise).then(resolve, reject).finally(() => signal?.removeEventListener('abort', abort));
  });
}
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, contract);
  return reply.value;
}
function denied(reply, code) {
  assert.deepEqual(reply, { ok: false, contract, code });
}
function profileOwner() {
  const profiles = new Map(), restored = [], released = [];
  const owner = {
    profiles, restored, released, failRelease: false,
    invoke: async () => null,
    snapshotProfiles: prefix => structuredClone(profiles.get(prefix) ?? emptyPrivate()),
    restoreProfiles(prefix, state) {
      restored.push({ prefix, state: structuredClone(state) });
      profiles.set(prefix, structuredClone(state));
    },
    async releaseProfiles(prefix, except = '') {
      released.push({ prefix, except });
      if (owner.failRelease) throw new ScriptHostError('STORAGE_ERROR');
      for (const name of profiles.keys()) {
        if (name.startsWith(prefix) && !(except && name.startsWith(`${except}:`))) profiles.delete(name);
      }
    },
  };
  return owner;
}
function mockHost(options = {}) {
  const records = new Map(), generations = new Map(), initializing = new Map(), jobs = new Set();
  const loads = [], uninstalls = [], captures = [], invocations = [];
  const issues = new Set(), pending = new Map();
  const host = {
    loads, uninstalls, captures, invocations, issues, pending,
    list: () => [...records.values()].map(item => ({ ...item })),
    stats: () => ({ cleanupIssues: [...issues].map(installationId => ({ installationId })) }),
    cleanupPending: id => pending.get(id) ?? 0,
    captureInitialization(id) {
      const current = initializing.get(id);
      if (!current) return null;
      return () => {
        if (initializing.get(id) !== current) return;
        captures.push({ id, generation: current.generation });
        current.controller.abort();
      };
    },
    async uninstall(id) {
      uninstalls.push(id);
      await options.uninstall?.(id);
      records.delete(id);
    },
    observe: id => {
      const captured = records.get(id);
      return () => ({ ...captured });
    },
    async invoke(input, { signal } = {}) {
      const item = records.get(input.installationId);
      if (!item || item.state !== 'ready') throw new ScriptHostError('DISABLED');
      if (input.generation !== item.generation) throw new ScriptHostError('STALE_SESSION');
      invocations.push({ ...input, signal });
      if (options.invoke) return options.invoke(input, { signal, generation: item.generation });
      if (input.operation === 'image') return { body: new Uint8Array(png) };
      const raw = JSON.parse(item.source);
      return { name: raw.bookSourceName, marker: raw.marker ?? null };
    },
    async close() {
      for (const item of initializing.values()) item.controller.abort();
      await Promise.allSettled([...jobs]);
      records.clear();
      return { cleanupComplete: issues.size === 0 && [...pending.values()].every(count => count === 0) };
    },
  };
  function load(spec, { signal } = {}, operation) {
    const work = (async () => {
      const id = spec.installationId, generation = (generations.get(id) ?? 0) + 1;
      generations.set(id, generation);
      const controller = new AbortController(), abort = () => controller.abort();
      signal?.addEventListener('abort', abort, { once: true });
      if (signal?.aborted) abort();
      initializing.set(id, { controller, generation });
      loads.push({ ...spec, operation, generation });
      if (operation === 'install') records.set(id, { ...spec, generation, state: 'failed' });
      try {
        await wait(options.initialize?.(spec, { signal: controller.signal, generation }), controller.signal);
        if (controller.signal.aborted) throw new ScriptHostError('CANCELLED');
        const item = { ...spec, generation, state: 'ready',
          capabilities: { source: { actions: ['search', 'image', 'web'] } } };
        records.set(id, item);
        return { ...item };
      } catch (error) {
        await options.initializationCleanup?.(spec, error);
        throw error;
      } finally {
        signal?.removeEventListener('abort', abort);
        initializing.delete(id);
      }
    })();
    jobs.add(work);
    work.finally(() => jobs.delete(work)).catch(() => {});
    return work;
  }
  host.install = (spec, context) => load(spec, context, 'install');
  host.update = (_id, spec, context) => load(spec, context, 'update');
  return host;
}
function databaseFacts(root) {
  return { root,
    rows: (sql, params = []) => withTestDatabase(root, db => db.query(sql, params)),
    async state(id = 'books') {
      const [revision, profiles, cookies, entries, kv, reader] = await withTestDatabase(root, db => db.snapshot([
        { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: ['source.private'], rows: true },
        { sql: 'SELECT * FROM source_private_profiles WHERE installation_id=? ORDER BY profile_suffix', params: [id], rows: true },
        { sql: 'SELECT * FROM source_private_cookies WHERE installation_id=? ORDER BY profile_suffix,sequence', params: [id], rows: true },
        { sql: 'SELECT * FROM source_private_kv_entries WHERE installation_id=? ORDER BY profile_suffix,position', params: [id], rows: true },
        { sql: 'SELECT * FROM source_private_kv_nodes WHERE installation_id=? ORDER BY profile_suffix,entry_key,node_id', params: [id], rows: true },
        { sql: 'SELECT * FROM source_reader_state WHERE installation_id=?', params: [id], rows: true },
      ]));
      return { revision: revision[0]?.revision ?? 0, profiles, cookies, entries, kv, reader };
    },
  };
}
function fixture(t, options = {}) {
  const dataRoot = options.dataRoot ?? temp(), owner = profileOwner();
  const disk = databaseFacts(dataRoot);
  const host = mockHost(options);
  const module = createSourceRuntimeModule({
    configurationOwner: options.configurationOwner ?? 'native', platform: 'windows',
    dataRoot, hostFactory: () => host, capabilityOwner: owner,
    network: async () => { throw new Error('unexpected network'); },
  });
  const rpc = (name, params = {}, context = {}) => module.methods[`source.runtime.${name}`](params, context);
  t.after(async () => {
    owner.failRelease = false; host.issues.clear(); host.pending.clear();
    value(await module.close());
  });
  return { disk, dataRoot, owner, host, module, rpc };
}
async function seedPrivate(dataRoot, installationId, state) {
  await withTestDatabase(dataRoot, async database => {
    await database.execute('INSERT INTO owner_revisions VALUES(?,?,?) ON CONFLICT DO NOTHING',
      ['source.private', 1, 'initial']);
    const statements = [{ sql: 'INSERT INTO source_reader_state(installation_id) VALUES(?)', params: [installationId] }];
    for (const [suffix, cookies] of state.cookies) {
      statements.push({ sql: 'INSERT INTO source_private_profiles VALUES(?,?)', params: [installationId, suffix] });
      for (const [, cookie] of cookies) statements.push({
        sql: 'INSERT INTO source_private_cookies VALUES(?,?,?,?,?,?,?,?,?,?,?,?)',
        params: [installationId, suffix, cookie.domain, cookie.path, cookie.name, cookie.value,
          Number(cookie.hostOnly), Number(cookie.secure), Number(cookie.httpOnly), cookie.sameSite,
          cookie.expires, cookie.sequence],
      });
    }
    for (const [suffix, entries] of state.storage) {
      if (!state.cookies.some(([name]) => name === suffix))
        statements.push({ sql: 'INSERT INTO source_private_profiles VALUES(?,?)', params: [installationId, suffix] });
      for (const [position, [key, value]] of entries.entries()) {
        statements.push({ sql: 'INSERT INTO source_private_kv_entries VALUES(?,?,?,?)',
          params: [installationId, suffix, key, position] });
        statements.push(...nodeInserts('source_private_kv_nodes',
          ['installation_id', 'profile_suffix', 'entry_key'], [installationId, suffix, key], encodeValueNodes(value)));
      }
    }
    await database.transaction(statements);
  });
}
const search = (rpc, item, context) =>
  rpc('library.invoke', { ...target(item), action: 'search', payload: {} }, context);

test('native full snapshots expose the unchanged contract over HTTP and reload only changed sources', async t => {
  const { module, host, disk } = fixture(t);
  const rpc = moduleHttp(t, module);
  const a = source(), b = source('other'), disabled = source('disabled', { enabled: false });
  const initial = snapshot([a, b, disabled]);
  const applied = value(await rpc('library.apply', initial));
  assert.equal(applied.revision, 1);
  assert.deepEqual(applied.sources.map(item => [item.installationId, item.actions]),
    [['books', ['search', 'image', 'web']], ['other', ['search', 'image', 'web']], ['disabled', []]]);
  assert.equal(host.loads.length, 2);
  denied(await search(rpc, disabled), 'SOURCE_DISABLED');
  assert.deepEqual(value(await rpc('library.apply', initial)), applied);
  value(await rpc('library.apply', snapshot(initial.sources, 2)));
  assert.equal(host.loads.length, 2);
  const leaseOnly = { ...a, revision: 2 };
  value(await rpc('library.apply', snapshot([leaseOnly, b, disabled], 3)));
  assert.equal(host.loads.length, 2);
  denied(await search(rpc, a), 'STALE_SOURCE');
  assert.equal(value(await search(rpc, leaseOnly)).name, a.name);
  const changed = { ...leaseOnly, revision: 3, definition: definition('books', 'changed') };
  value(await rpc('library.apply', snapshot([changed, disabled], 4)));
  assert.equal(host.loads.length, 3);
  assert.deepEqual(host.uninstalls, ['other']);
  assert.equal(value(await search(rpc, changed)).marker, 'changed');
  const saved = await disk.state('books');
  assert.deepEqual(saved.profiles, []);
  assert.equal(saved.reader[0].installation_id, 'books');
  assert.deepEqual((await disk.state('other')).reader, []);
  assert.equal((await disk.rows('SELECT count(*) AS count FROM source_installations'))[0].count, 0);
});

test('native mode reads only private state and refuses runtime-owned configuration writes', async t => {
  const { rpc, disk, host } = fixture(t);
  assert.deepEqual(value(await rpc('library.list')), { sources: [] });
  assert.equal(host.loads.length, 0);
  for (const [name, params] of [
    ['library.import', { text: definition() }],
    ['library.save', { installationId: 'books', expectedRevision: 1, definition: definition() }],
    ['library.update', { installationId: 'books', expectedRevision: 1, patch: { enabled: false } }],
    ['library.remove', { installationId: 'books', expectedRevision: 1 }],
  ]) denied(await rpc(name, params), 'PERMISSION_DENIED');
  assert.equal((await disk.state()).revision, 1);
  value(await rpc('library.apply', snapshot([source()])));
  const projected = value(await rpc('library.get', { installationId: 'books' }));
  assert.equal(projected.source.revision, 1);
  assert.equal(JSON.parse(projected.definition).bookSourceName, 'books');
  assert.equal((await disk.state()).revision, 2);
});

test('the runtime-owned default writes and reopens typed definitions without native apply', async t => {
  const dataRoot = temp(), owner = profileOwner(), host = mockHost();
  const module = createSourceRuntimeModule({ dataRoot, hostFactory: () => host,
    capabilityOwner: owner, network: async () => null });
  t.after(async () => value(await module.close()));
  const rpc = (name, params = {}) => module.methods[`source.runtime.${name}`](params);
  assert.equal(value(await rpc('library.list')).sources.length, 0);
  assert.equal(host.loads.length, 0);
  denied(await rpc('library.apply', snapshot([source()])), 'PERMISSION_DENIED');
  const imported = value(await rpc('library.import', { text: definition() })).sources[0];
  assert.equal(host.loads.length, 1);
  value(await rpc('library.save', { installationId: imported.installationId, expectedRevision: imported.revision,
    definition: definition('books', 'default-edited') }));
  assert.equal(host.loads.length, 2);
  await withTestDatabase(dataRoot, async db => {
    const rows = await db.query('SELECT revision,definition_kind FROM source_installations');
    assert.deepEqual(rows, [{ revision: 2, definition_kind: 'object' }]);
    assert.equal((await db.query('SELECT bookSourceName FROM source_definitions'))[0].bookSourceName, 'books');
  });
  value(await module.close());
  const reopened = fixture(t, { dataRoot, configurationOwner: 'runtime' });
  const saved = value(await reopened.rpc('library.list')).sources[0];
  assert.equal(saved.installationId, imported.installationId);
  assert.equal(saved.revision, 2);
  assert.equal(value(await search(reopened.rpc, saved)).marker, 'default-edited');
});

test('native snapshots and runtime saves preserve definitions above the former 16 MiB boundary', async t => {
  const padding = 'x'.repeat(16 * 1024 * 1024 + 1);
  const item = source('large', { definition: definition('large', padding) });
  const native = fixture(t);
  value(await native.rpc('library.apply', snapshot([item])));
  assert.equal(JSON.parse(native.host.loads[0].source).marker, padding);
  assert.equal(value(await search(native.rpc, item)).marker, padding);
  const runtime = fixture(t, { configurationOwner: 'runtime' });
  const imported = value(await runtime.rpc('library.import', { text: definition('large') }));
  assert.deepEqual(imported.failures, []);
  value(await runtime.rpc('library.save', { installationId: imported.sources[0].installationId,
    expectedRevision: 1, definition: item.definition }));
  assert.equal(JSON.parse(runtime.disk.json('library.json').sources[0].definition).marker, padding);
});

test('snapshot identity and consumed configuration failures remain atomic before session changes', async t => {
  const { rpc, host, disk } = fixture(t), a = source(), b = source('other');
  value(await rpc('library.apply', snapshot([a, b])));
  const stable = value(await rpc('library.list')), rows = await disk.state('books');
  const changed = { ...a, revision: 2, definition: definition('books', 'must-not-apply') };
  const invalid = [
    [snapshot([changed, { ...b, enabled: 'true' }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, { ...b, group: null }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, { ...b, revision: 0 }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, { ...b, sourceUrl: ' https://other.invalid/' }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, { ...b, definition: {} }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, { ...b, installationId: 'invalid\n' }], 2), 'INVALID_REQUEST'],
    [snapshot([changed, changed], 2), 'SOURCE_INTEGRITY'],
    [snapshot([changed, { ...b, sourceUrl: a.sourceUrl }], 2), 'SOURCE_INTEGRITY'],
    [snapshot([changed, { ...b, protocol: 'cat' }], 2), 'UNSUPPORTED_PROTOCOL'],
    [snapshot([changed], 0), 'INVALID_REQUEST'],
    [snapshot([changed], Number.MAX_SAFE_INTEGER + 1), 'INVALID_REQUEST'],
    [snapshot([changed], '2'), 'INVALID_REQUEST'],
    [{ revision: 2, sources: {} }, 'INVALID_REQUEST'],
  ];
  const cycle = {}; cycle.self = cycle;
  invalid.push([snapshot([changed, cycle], 2), 'INVALID_REQUEST']);
  const missing = { ...b }; delete missing.enabledExplore;
  invalid.push([snapshot([changed, missing], 2), 'INVALID_REQUEST']);
  for (const [input, code] of invalid) {
    denied(await rpc('library.apply', input), code);
    assert.deepEqual(value(await rpc('library.list')), stable);
    assert.deepEqual(await disk.state('books'), rows);
    assert.equal(host.loads.length, 2);
    assert.equal(host.uninstalls.length, 0);
    assert.equal(host.captures.length, 0);
  }
  assert.equal(value(await search(rpc, a)).marker, 'ready');
});

test('snapshots project extras and isolate disabled or failing definitions from healthy sources', async t => {
  const { rpc, host, owner } = fixture(t);
  const disabled = source('disabled', { enabled: false, definition: '{broken', name: '', order: 1.5,
    private: { storage: [['intruder', [['key', 'value']]]], cookies: [] }, actions: ['intruder'],
    extra: rawJSON('9007199254740993') });
  const activeBad = source('broken', { definition: '{broken' });
  const healthy = source('healthy', { definition: '{"ruleSearch":{},"extra":9007199254740993}' });
  const result = value(await rpc('library.apply', snapshot([disabled, activeBad, healthy])));
  assert.equal(result.sources[0].name, '');
  assert.equal(result.sources[0].order, 1.5);
  assert.deepEqual(result.sources[0].actions, []);
  assert.equal(result.sources[0].errorCode, undefined);
  assert.ok(result.sources[1].errorCode);
  assert.equal(result.sources[2].errorCode, undefined);
  assert.deepEqual(host.loads.map(item => item.installationId), ['healthy']);
  assert.deepEqual(owner.restored.map(item => item.state), [emptyPrivate()]);
  assert.match(host.loads[0].source, /9007199254740993/);
  assert.equal(value(await search(rpc, healthy)).name, 'healthy');
  value(await rpc('library.apply', snapshot([disabled, activeBad, healthy])));
  assert.equal(host.loads.length, 1);
  value(await rpc('library.apply', snapshot([{ ...disabled, revision: 2, order: -0 }, activeBad, healthy], 2)));
  assert.equal(value(await rpc('library.list')).sources.find(item => item.installationId === 'disabled').order, -0);
});

test('global and per-source revisions reject stale and inconsistent snapshots, including removed leases', async t => {
  const { rpc, host } = fixture(t), a = source('books', { revision: 4 });
  value(await rpc('library.apply', snapshot([a], 8)));
  denied(await rpc('library.apply', snapshot([], 7)), 'STALE_SOURCE');
  denied(await rpc('library.apply', snapshot([{ ...a, name: 'different' }], 8)), 'SOURCE_INTEGRITY');
  denied(await rpc('library.apply', snapshot([{ ...a, revision: 3 }], 9)), 'STALE_SOURCE');
  denied(await rpc('library.apply', snapshot([{ ...a, definition: definition('books', 'different') }], 9)),
    'SOURCE_INTEGRITY');
  const reordered = Object.fromEntries(Object.entries(a).reverse());
  value(await rpc('library.apply', { revision: 8, sources: [reordered], ignored: true }));
  assert.equal(host.loads.length, 1);
  value(await rpc('library.apply', snapshot([], 9)));
  denied(await rpc('library.apply', snapshot([{ ...a, revision: 3 }], 10)), 'STALE_SOURCE');
  denied(await rpc('library.apply', snapshot([{ ...a, name: 'changed' }], 10)), 'SOURCE_INTEGRITY');
  value(await rpc('library.apply', snapshot([{ ...a, revision: 5, name: 'changed' }], 10)));
  denied(await search(rpc, a), 'STALE_SOURCE');
});

test('saved execution failures fence the new lease instead of rolling back and retry only failed sources', async t => {
  let fail = true;
  const { rpc, host } = fixture(t, { initialize: spec => {
    if (JSON.parse(spec.source).marker === 'broken' && fail) throw new ScriptHostError('CAPABILITY_UNAVAILABLE');
  } });
  const a = source(), b = source('other');
  value(await rpc('library.apply', snapshot([a, b])));
  const broken = { ...a, revision: 2, definition: definition('books', 'broken') };
  const input = snapshot([broken, b], 2);
  const applied = value(await rpc('library.apply', input));
  assert.equal(applied.sources[0].revision, 2);
  assert.equal(applied.sources[0].enabled, true);
  assert.equal(applied.sources[0].errorCode, 'CAPABILITY_UNAVAILABLE');
  assert.deepEqual(applied.sources[0].actions, []);
  assert.equal(JSON.parse(value(await rpc('library.get', { installationId: 'books' })).definition).marker, 'broken');
  denied(await search(rpc, a), 'STALE_SOURCE');
  denied(await search(rpc, broken), 'CAPABILITY_UNAVAILABLE');
  assert.equal(value(await search(rpc, b)).marker, 'ready');
  assert.equal(host.loads.length, 3);
  fail = false;
  const retried = value(await rpc('library.apply', input));
  assert.equal(Object.hasOwn(retried.sources[0], 'errorCode'), false);
  assert.equal(host.loads.length, 4);
  assert.equal(value(await search(rpc, broken)).marker, 'broken');
  value(await rpc('library.apply', input));
  assert.equal(host.loads.length, 4);
});

for (const action of ['remove', 'replace', 'disable']) {
  test(`${action} captures initialization before queueing and waits for its actual cleanup`, async t => {
    const started = deferred(), finish = deferred(), cleaning = deferred(), cleaned = deferred();
    t.after(() => { finish.resolve(); cleaned.resolve(); });
    const { rpc, host } = fixture(t, {
      initialize: (spec, { signal }) => {
        if (JSON.parse(spec.source).marker !== 'initializing') return;
        started.resolve();
        return wait(finish.promise, signal);
      },
      initializationCleanup: () => { cleaning.resolve(); return cleaned.promise; },
    });
    const initial = source('books', { definition: definition('books', 'initializing') });
    const applying = rpc('library.apply', snapshot([initial]));
    await started.promise;
    const next = action === 'remove' ? [] : [{ ...initial, revision: 2,
      enabled: action !== 'disable', definition: definition('books', 'replacement') }];
    let completed = false;
    const replacing = rpc('library.apply', snapshot(next, 2)).then(reply => { completed = true; return reply; });
    await cleaning.promise;
    assert.equal(host.captures.length, 1);
    assert.equal(completed, false);
    cleaned.resolve();
    denied(await applying, 'STALE_SOURCE');
    const result = value(await replacing);
    assert.equal(result.revision, 2);
    assert.equal(host.loads.length, action === 'replace' ? 2 : 1);
    assert.equal(host.list().length, action === 'replace' ? 1 : 0);
    if (action === 'disable') assert.deepEqual(result.sources[0].actions, []);
  });
}

test('an identical in-flight snapshot does not cancel or repeat a successful initialization', async t => {
  const started = deferred(), finish = deferred();
  t.after(() => finish.resolve());
  const { rpc, host } = fixture(t, { initialize: (_spec, { signal }) => {
    started.resolve();
    return wait(finish.promise, signal);
  } });
  const input = snapshot([source()]);
  const first = rpc('library.apply', input);
  await started.promise;
  const second = rpc('library.apply', input);
  assert.equal(host.captures.length, 0);
  finish.resolve();
  assert.deepEqual(value(await second), value(await first));
  assert.equal(host.loads.length, 1);
});

test('a newer snapshot preserves unchanged initialization and rejects only the superseded receipt', async t => {
  const started = deferred(), finish = deferred();
  t.after(() => finish.resolve());
  const { rpc, host } = fixture(t, { initialize: (spec, { signal }) => {
    if (spec.installationId !== 'books') return;
    started.resolve();
    return wait(finish.promise, signal);
  } });
  const a = source();
  const first = rpc('library.apply', snapshot([a]));
  await started.promise;
  const second = rpc('library.apply', snapshot([a, source('other')], 2));
  assert.equal(host.captures.length, 0);
  finish.resolve();
  denied(await first, 'STALE_SOURCE');
  value(await second);
  assert.deepEqual(host.loads.map(item => item.installationId), ['books', 'other']);
  assert.deepEqual(host.uninstalls, []);
});

test('invalid snapshots cannot interrupt an initialization or reserve their revision', async t => {
  const started = deferred(), finish = deferred();
  t.after(() => finish.resolve());
  const { rpc, host } = fixture(t, { initialize: (_spec, { signal }) => {
    started.resolve();
    return wait(finish.promise, signal);
  } });
  const first = rpc('library.apply', snapshot([source()]));
  await started.promise;
  denied(await rpc('library.apply', snapshot([{ ...source(), revision: 2 },
    source('other', { enabled: 'false' })], 2)), 'INVALID_REQUEST');
  assert.equal(host.captures.length, 0);
  finish.resolve();
  value(await first);
  value(await rpc('library.apply', snapshot([source()], 2)));
  assert.equal(host.loads.length, 1);
});

test('cancelled apply waits for cleanup, keeps the saved revision and permits an exact-snapshot retry', async t => {
  const started = deferred(), finish = deferred(), cleaning = deferred(), cleaned = deferred();
  t.after(() => { finish.resolve(); cleaned.resolve(); });
  let hold = true;
  const { rpc, host } = fixture(t, {
    initialize: (spec, { signal }) => {
      if (JSON.parse(spec.source).marker !== 'cancelled' || !hold) return;
      started.resolve();
      return wait(finish.promise, signal);
    },
    initializationCleanup: () => { cleaning.resolve(); return cleaned.promise; },
  });
  const a = source(), b = source('other');
  value(await rpc('library.apply', snapshot([a, b])));
  const updated = { ...a, revision: 2, definition: definition('books', 'cancelled') };
  const input = snapshot([updated, b], 2), controller = new AbortController();
  let completed = false;
  const applying = rpc('library.apply', input, { signal: controller.signal })
    .then(reply => { completed = true; return reply; });
  await started.promise;
  controller.abort();
  await cleaning.promise;
  assert.equal(completed, false);
  cleaned.resolve();
  denied(await applying, 'CANCELLED');
  const saved = value(await rpc('library.list')).sources.find(item => item.installationId === 'books');
  assert.equal(saved.revision, 2);
  assert.equal(saved.errorCode, 'CANCELLED');
  assert.equal(host.list().some(item => item.installationId === 'books'), false);
  hold = false;
  value(await rpc('library.apply', input));
  assert.equal(host.loads.filter(item => item.installationId === 'other').length, 1);
  assert.equal(value(await search(rpc, updated)).marker, 'cancelled');
});

test('caller mutation cannot change the snapshot captured before initialization', async t => {
  const started = deferred(), finish = deferred();
  t.after(() => finish.resolve());
  const { rpc } = fixture(t, { initialize: (_spec, { signal }) => {
    started.resolve();
    return wait(finish.promise, signal);
  } });
  const a = source(), input = snapshot([a]);
  const applying = rpc('library.apply', input);
  await started.promise;
  a.name = 'mutated'; a.definition = definition('books', 'mutated'); input.revision = 99;
  finish.resolve();
  const result = value(await applying);
  assert.equal(result.revision, 1);
  assert.equal(result.sources[0].name, 'books');
  assert.equal(value(await search(rpc, source())).marker, 'ready');
});

test('cleanup failure on replacement is not an apply success and fences the saved source lease', async t => {
  const { rpc, host } = fixture(t), a = source();
  value(await rpc('library.apply', snapshot([a])));
  host.pending.set('books', 1);
  const next = { ...a, revision: 2, definition: definition('books', 'new') }, input = snapshot([next], 2);
  denied(await rpc('library.apply', input), 'SOURCE_CLEANUP');
  const record = value(await rpc('library.list')).sources[0];
  assert.equal(record.revision, 2);
  assert.equal(record.errorCode, 'SOURCE_CLEANUP');
  denied(await search(rpc, next), 'SOURCE_CLEANUP');
  host.pending.clear();
  value(await rpc('library.apply', input));
  assert.equal(value(await search(rpc, next)).marker, 'new');
});

test('removal retains private SQLite rows until cleanup succeeds', async t => {
  const dataRoot = temp();
  await seedPrivate(dataRoot, 'books', privateState());
  const { rpc, owner, disk } = fixture(t, { dataRoot });
  value(await rpc('library.apply', snapshot([source()])));
  owner.failRelease = true;
  denied(await rpc('library.apply', snapshot([], 2)), 'SOURCE_CLEANUP');
  assert.deepEqual(value(await rpc('library.list')), { sources: [] });
  assert.equal((await disk.state()).profiles.length, 1);
  owner.failRelease = false;
  value(await rpc('library.apply', snapshot([], 2)));
  assert.equal((await disk.state()).profiles.length, 0);
});

test('failed uninstall still attempts profile cleanup and cannot retain execution admission', async t => {
  let fail = false;
  const { rpc, host, owner } = fixture(t, { uninstall: () => {
    if (fail) throw new ScriptHostError('STORAGE_ERROR');
  } });
  const a = source();
  value(await rpc('library.apply', snapshot([a])));
  const releases = owner.released.length;
  fail = true;
  const next = { ...a, revision: 2, enabled: false };
  denied(await rpc('library.apply', snapshot([next], 2)), 'SOURCE_CLEANUP');
  assert.ok(owner.released.length > releases);
  denied(await search(rpc, next), 'SOURCE_DISABLED');
  assert.equal(host.list().length, 1);
  fail = false;
  value(await rpc('library.apply', snapshot([next], 2)));
  assert.equal(host.list().length, 0);
});

test('a failed SQLite private-state transaction leaves the last committed rows intact', async t => {
  let owner, library;
  const setup = libraryFixture(t, { initialize: (spec, { signal, generation }) => {
    const prefix = `source:${spec.installationId}:${generation}:`;
    library.prepareCapability('storage.get', { profile: `${prefix}reader` },
      { signal, scope: { installationId: spec.installationId, generation } });
  } });
  ({ owner, library } = setup);
  assert.equal((await setup.call('library.apply', snapshot([source()]))).sources[0].errorCode, undefined);
  const generation = setup.host.list()[0].generation, prefix = `source:books:${generation}:`;
  const changed = privateState(); changed.storage[0][1][0][1] = { changed: true };
  owner.profiles.set(prefix, changed);
  const previous = await setup.disk.state();
  await setTestReadOnly(setup.disk.root, true);
  try {
    await assert.rejects(library.profileChanged(prefix), { code: 'STORAGE_ERROR' });
    assert.deepEqual(await setup.disk.state(), previous);
  } finally { await setTestReadOnly(setup.disk.root, false); }
  await library.profileChanged(prefix);
  const saved = await setup.disk.state();
  assert.equal(saved.revision, previous.revision + 1);
  assert.deepEqual(saved.kv.map(row => row.kind), ['object', 'boolean']);
});

test('a new runtime must receive native definitions again; private data alone never restores sources', async t => {
  const dataRoot = temp(), a = source('books', { revision: 7 });
  await seedPrivate(dataRoot, 'books', privateState());
  const first = fixture(t, { dataRoot });
  assert.deepEqual(value(await first.rpc('library.list')), { sources: [] });
  assert.equal(first.host.loads.length, 0);
  value(await first.rpc('library.apply', snapshot([a], 12)));
  value(await first.module.close());
  const second = fixture(t, { dataRoot });
  assert.deepEqual(value(await second.rpc('library.list')), { sources: [] });
  assert.equal(second.host.loads.length, 0);
  value(await second.rpc('library.apply', snapshot([a], 12)));
  assert.deepEqual(second.owner.restored[0].state.storage, privateState().storage);
  assert.equal(second.owner.restored[0].state.cookies[0][1][0][1].value, 'mock');
  assert.equal(second.host.loads.length, 1);
});

test('disabled QYSG definitions are never executed even when their script would fail', async t => {
  const { rpc, host } = fixture(t, { initialize: () => { throw new ScriptHostError('CAPABILITY_UNAVAILABLE'); } });
  const disabled = source('qysg', { protocol: 'qysg', enabled: false,
    definition: '<script>function invalid({</script>' });
  const result = value(await rpc('library.apply', snapshot([disabled])));
  assert.deepEqual(result.sources[0].actions, []);
  assert.equal(Object.hasOwn(result.sources[0], 'errorCode'), false);
  assert.equal(host.loads.length, 0);
});

function libraryFixture(t, options = {}) {
  const dataRoot = temp(), disk = databaseFacts(dataRoot);
  const owner = profileOwner(), web = createSourceWebBroker();
  const host = mockHost(options), lifetime = new AbortController();
  const guard = signal => {
    if (lifetime.signal.aborted) throw fault('CLOSED');
    if (signal?.aborted) throw fault('CANCELLED');
  };
  const library = createSourceLibrary({ dataRoot, configurationOwner: 'native', host, owner, web,
    network: async () => null, fail: fault, errorCode: error => error?.code ?? 'SOURCE_FAILURE', guard, lifetime });
  const call = (name, params = {}, signal = lifetime.signal) => library.methods[name](params, signal);
  t.after(async () => {
    lifetime.abort(); web.close(); library.retireAll();
    await host.close(); await library.close();
  });
  return { library, disk, dataRoot, owner, web, host, call };
}

test('profile mutations persist typed private rows for the current generation', async t => {
  let library, owner, disk;
  const setup = libraryFixture(t, {
    initialize: (spec, { signal, generation }) => {
      const prefix = `source:${spec.installationId}:${generation}:`;
      library.prepareCapability('storage.get', { profile: `${prefix}reader` },
        { signal, scope: { installationId: spec.installationId, generation } });
      assert.deepEqual(owner.profiles.get(prefix), emptyPrivate());
    },
  });
  ({ library, owner, disk } = setup);
  const a = source(), result = await setup.call('library.apply', snapshot([a]));
  assert.equal(result.revision, 1);
  const generation = setup.host.list()[0].generation, prefix = `source:books:${generation}:`;
  const changed = privateState(); changed.storage[0][1][0][1] = { afterApply: true };
  owner.profiles.set(prefix, changed);
  await library.profileChanged(prefix);
  const saved = await disk.state();
  assert.ok(saved.profiles.some(row => row.profile_suffix === 'reader'));
  assert.deepEqual(saved.entries.map(row => row.entry_key), ['fixture']);
  assert.equal(saved.kv[1].prop_key, 'afterApply');
  assert.equal(saved.kv[1].bool_value, 1);
  const projection = await setup.call('library.get', { installationId: 'books' });
  assert.equal(projection.source.revision, a.revision);
  assert.equal(JSON.parse(projection.definition).marker, 'ready');
  await assert.rejects(library.profileChanged(`source:books:${generation + 1}:`), { code: 'STALE_SOURCE' });
});

test('replacement retires old Web requests and image streams before reporting completion', async t => {
  let web;
  const setup = libraryFixture(t, { invoke: (input, { signal, generation }) => input.operation === 'web'
    ? web.interact({ profile: `source:${input.installationId}:${generation}:reader`, mode: 'request',
      request: { url: 'https://books.invalid/' }, signal, scope: { installationId: input.installationId } })
    : { body: new Uint8Array(png) } });
  web = setup.web;
  const a = source();
  await setup.call('library.apply', snapshot([a]));
  const image = await setup.call('library.image', { ...target(a), url: 'https://books.invalid/cover',
    bookUrl: 'https://books.invalid/book', kind: 'cover' });
  const stream = await setup.library.image(image.reference);
  const interaction = setup.call('library.invoke', { ...target(a), action: 'web', payload: {} });
  const cancelled = assert.rejects(interaction, { code: 'CANCELLED' });
  await new Promise(resolve => setImmediate(resolve));
  const request = web.pending().requests[0];
  assert.ok(request);
  await setup.call('library.apply', snapshot([{ ...a, revision: 2,
    definition: definition('books', 'replacement') }], 2));
  await cancelled;
  assert.deepEqual(web.pending(), { requests: [] });
  assert.throws(() => web.respond({ id: request.id, result: null }), { code: 'STALE_SESSION' });
  assert.equal(stream.body.closed, true);
  await assert.rejects(setup.library.image(image.reference), { code: 'SOURCE_NOT_FOUND' });
});

test('native apply does not take over the separate generic load/unload installation', async t => {
  const { rpc, module, host } = fixture(t), id = 'generic';
  value(await module.upload({ installationId: id, sourceRevision: 1, basename: 'source.json' },
    Buffer.from(definition(id))));
  const instance = value(await rpc('load', { installationId: id, sourceRevision: 1, revision: 1,
    expectedRevision: 0, protocol: 'legado', version: '1.0.0', metadata: {} })).instance;
  denied(await rpc('library.apply', snapshot([source(id)])), 'STALE_SOURCE');
  assert.equal(host.loads.length, 1);
  assert.equal(value(await rpc('status')).instances[0].generation, instance.generation);
  value(await rpc('unload', { installationId: id, revision: 1, generation: instance.generation }));
  assert.equal(host.list().length, 0);
});

test('queued generic load ownership is checked before its host record exists', async t => {
  const finish = deferred();
  t.after(() => finish.resolve());
  const { rpc, module, host } = fixture(t, { initialize: (_spec, { signal, generation }) =>
    generation === 1 ? wait(finish.promise, signal) : undefined });
  const id = 'generic';
  value(await module.upload({ installationId: id, sourceRevision: 1, basename: 'source.json' },
    Buffer.from(definition(id))));
  const loading = rpc('load', { installationId: id, sourceRevision: 1, revision: 1,
    expectedRevision: 0, protocol: 'legado', version: '1.0.0', metadata: {} });
  try {
    denied(await rpc('library.apply', snapshot([source(id)])), 'STALE_SOURCE');
  } finally { finish.resolve(); }
  value(await loading);
  assert.equal(host.loads.length, 1);
  assert.deepEqual(value(await rpc('library.list')), { sources: [] });
});

test('a native snapshot reserves its IDs before queued application so generic load cannot race it', async t => {
  const { rpc, module, host } = fixture(t), a = source();
  value(await module.upload({ installationId: a.installationId, sourceRevision: 1, basename: 'source.json' },
    Buffer.from(a.definition)));
  const applying = rpc('library.apply', snapshot([a]));
  denied(await rpc('load', { installationId: a.installationId, sourceRevision: 1, revision: 1,
    expectedRevision: 0, protocol: 'legado', version: '1.0.0', metadata: {} }), 'STALE_SOURCE');
  value(await applying);
  assert.equal(host.loads.length, 1);
  assert.deepEqual(value(await rpc('status')).instances, []);
});

test('the actual ScriptHost transaction manager retires its old session after native apply fails', async t => {
  const sessions = [], dataRoot = temp(), owner = profileOwner();
  function adapter({ installationId, generation, revision }) {
    const raw = JSON.parse(revision.source);
    const session = {
      installationId, generation, live: false, stopped: false,
      capabilities: { source: { actions: ['search'] } },
      async start() {
        if (raw.marker === 'broken') throw new ScriptHostError('CAPABILITY_UNAVAILABLE');
        session.live = true;
      },
      activate: async () => {},
      async stop() { session.live = false; session.stopped = true; },
      drainAdapters: async () => {},
      stats: () => ({ live: session.live, timers: 0, networkRequests: 0, pendingAdapterTasks: 0 }),
      invoke: async () => ({ marker: raw.marker }),
    };
    sessions.push(session);
    return session;
  }
  const host = createScriptHost({ adapters: { legado: adapter, qysg: adapter } });
  const module = createSourceRuntimeModule({ configurationOwner: 'native', dataRoot,
    hostFactory: () => host, capabilityOwner: owner, network: async () => null });
  t.after(async () => value(await module.close()));
  const rpc = (name, params) => module.methods[`source.runtime.${name}`](params);
  const a = source();
  value(await rpc('library.apply', snapshot([a])));
  const broken = { ...a, revision: 2, definition: definition('books', 'broken') };
  const reply = value(await rpc('library.apply', snapshot([broken], 2)));
  assert.equal(reply.sources[0].errorCode, 'CAPABILITY_UNAVAILABLE');
  assert.equal(sessions[0].stopped, true);
  assert.equal(sessions[1].stopped, true);
  assert.equal(host.stats().liveSessions, 0);
  assert.deepEqual(host.list(), []);
  denied(await search(rpc, a), 'STALE_SOURCE');
  denied(await search(rpc, broken), 'CAPABILITY_UNAVAILABLE');
});

test('native QuickJS apply cancellation drains the initializer, retires the old source and reuses successful sources on retry',
  { timeout: 15000 }, async t => {
    const started = deferred(), finish = deferred(), sessions = [];
    let host;
    t.after(() => finish.resolve({ statusCode: 200, headers: {}, body: '', url: 'https://books.invalid/' }));
    const inline = '<script>function search() { return [{name:"Ready",bookUrl:"https://books.invalid/book"}]; }</script>';
    const module = createSourceRuntimeModule({ configurationOwner: 'native', dataRoot: temp(),
      network: input => {
        started.resolve(input);
        return wait(finish.promise, input.signal);
      },
      hostFactory: options => {
        const factory = options.adapters.qysg;
        host = createScriptHost({ ...options, adapters: { ...options.adapters,
          qysg: input => { const session = factory(input); sessions.push(session); return session; },
        } });
        return host;
      },
    });
    t.after(async () => value(await module.close()));
    const rpc = (name, params = {}, context = {}) => module.methods[`source.runtime.${name}`](params, context);
    const a = source('books', { protocol: 'qysg', definition: inline });
    const b = source('other', { protocol: 'qysg', definition: inline });
    value(await rpc('library.apply', snapshot([a, b])));
    const successful = host.list().find(item => item.installationId === 'other').generation;
    const next = { ...a, revision: 2, definition: JSON.stringify({
      bookSourceName: a.name, bookSourceUrl: a.sourceUrl, html: 'https://books.invalid/initialization',
    }) }, input = snapshot([next, b], 2), controller = new AbortController();
    const applying = rpc('library.apply', input, { signal: controller.signal });
    const request = await started.promise, candidate = sessions.at(-1);
    assert.ok(candidate.engine);
    controller.abort();
    denied(await applying, 'CANCELLED');
    assert.equal(request.signal.aborted, true);
    await candidate.engine.exited;
    await candidate.whenAdaptersIdle();
    for (const key of ['threadCount', 'runtimeCount', 'contextCount']) {
      assert.equal(candidate.engine.stats()[key], 0, key);
    }
    assert.equal(sessions[0].live, false);
    assert.equal(host.cleanupPending('books'), 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
    assert.equal(host.list().some(item => item.installationId === 'books'), false);
    const saved = value(await rpc('library.get', { installationId: 'books' }));
    assert.equal(saved.source.revision, 2);
    assert.deepEqual(JSON.parse(saved.definition), { ...JSON.parse(next.definition),
      bookSourceGroup: next.group, customOrder: next.order, enabled: next.enabled,
      enabledExplore: next.enabledExplore });
    denied(await search(rpc, a), 'STALE_SOURCE');
    denied(await search(rpc, next), 'CANCELLED');
    finish.resolve({ statusCode: 200, headers: {}, body: inline, url: 'https://books.invalid/initialization' });
    value(await rpc('library.apply', input));
    assert.equal(host.list().find(item => item.installationId === 'other').generation, successful);
    assert.equal(sessions.filter(item => item.installationId === 'other').length, 1);
    assert.equal(value(await search(rpc, next)).books[0].name, 'Ready');
    value(await module.close());
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
  });
