import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { SourceStorage } from '../apps/runtime/modules/source-storage.mjs';
import { CatStorage } from '../packages/script-host/src/cat-storage.js';
import './storage-test-fixture.mjs';

async function fixture(run) {
  const base = resolve('tmp/test-results');
  await mkdir(base, { recursive: true });
  const root = await mkdtemp(base + '/runtime-storage-');
  try { await run(root); } finally { await rm(root, { recursive: true, force: true }); }
}

test('source definitions, cookies and single-key typed values survive a real reopen', () => fixture(async root => {
  let store = new SourceStorage(root, false);
  const records = await store.load();
  const definition = { bookSourceName: 'Example', bookSourceUrl: 'https://example.test',
    searchUrl: '/search', bookSourceType: 0, ruleSearch: { bookList: '.book', name: '.name' },
    customExtension: { flags: [true, null], text: 'arbitrary' } };
  const item = { installationId: 'source', revision: 1, name: 'Example', sourceUrl: 'https://example.test',
    group: '', order: 0, enabled: true, enabledExplore: false, protocol: 'legado', actions: ['search'],
    definition: JSON.stringify(definition), private: {
      cookies: [['http', [['unused', { name: 'sample', value: 'synthetic', domain: 'example.test',
        path: '/', hostOnly: true, secure: true, httpOnly: false, sameSite: null, expires: null, sequence: 1 }]]]],
      storage: [['http', [['item', { count: 1, enabled: true }]]]],
    } };
  records.set(item.installationId, item);
  await store.save(records);
  assert.equal((await store.db.query('SELECT searchUrl FROM source_definitions'))[0].searchUrl, '/search');
  assert.equal((await store.db.query('SELECT bookList FROM source_definition_rules'))[0].bookList, '.book');
  assert.equal((await store.db.query('SELECT field FROM source_definition_nodes WHERE node_id=0'))[0].field, 'customExtension');
  await store.close();
  store = new SourceStorage(root, false);
  const restored = (await store.load()).get('source');
  assert.deepEqual(JSON.parse(restored.definition), definition);
  assert.deepEqual(restored.private.storage, item.private.storage);
  assert.equal(restored.private.cookies[0][1][0][1].value, 'synthetic');
  await store.close();
}));

test('native source mode owns private rows without persisting native definitions', () => fixture(async root => {
  const store = new SourceStorage(root, true);
  await store.load();
  await store.save(new Map([['native', { installationId: 'native', definition: 'not owned',
    private: { cookies: [], storage: [['http', [['value', 7]]]] } }]]));
  assert.equal((await store.db.query('SELECT count(*) AS count FROM source_installations'))[0].count, 0);
  assert.equal((await store.db.query('SELECT count(*) AS count FROM source_private_kv_entries'))[0].count, 1);
  await store.close();
}));

test('cat typed facts and mappings reopen; native intent is never mirrored', () => fixture(async root => {
  let store = new CatStorage(root, 'standalone');
  await store.ready;
  await store.save(value => ({ ...value, providers: { quark: true }, mappings: [{ provider: 'quark', aliases: ['Alias'] }],
    scripts: [{ installationId: 'cat', name: 'Example', sourceType: 'local', sourceUrl: null,
      enabled: true, sourceHash: 'a'.repeat(64), version: '1', config: { sample: [1, false] },
      sites: [{ key: 'default', name: 'Example', api: 'http://example.test', discovery: 'declared',
        actions: ['home'], methods: { home: 'GET' } }] }] }));
  const before = store.value;
  await store.close();
  store = new CatStorage(root, 'standalone');
  await store.ready;
  assert.deepEqual(store.value.providers, before.providers);
  assert.deepEqual(store.value.mappings, before.mappings);
  assert.deepEqual(store.value.scripts[0].config, before.scripts[0].config);
  assert.deepEqual(store.value.scripts[0].sites, before.scripts[0].sites);
  const native = new CatStorage(root, 'native');
  await native.ready;
  await native.save(value => ({ ...value, scripts: [{ ...before.scripts[0], inputRevision: 3 }] }));
  const row = (await native.db.query("SELECT * FROM cat_installations WHERE mode='native'"))[0];
  assert.equal(row.name, null);
  assert.equal(row.input_revision, null);
  assert.equal(row.source_hash, 'a'.repeat(64));
  await native.close();
  await store.close();
}));

test('a failed cat transaction cannot publish proposed mutable facts', () => fixture(async root => {
  const store = new CatStorage(root, 'standalone');
  try {
    await store.ready;
    await store.save(value => ({ ...value, port: 8000 }));
    await store.db.execute(`CREATE TRIGGER reject_cat_settings BEFORE UPDATE ON cat_gateway_settings
      BEGIN SELECT RAISE(ABORT, 'injected write failure'); END`);
    await assert.rejects(store.save(value => ({ ...value, port: 8001 })), { code: 'STORAGE_ERROR' });
    assert.equal(store.value.port, 8000);
    assert.equal((await store.db.query('SELECT port FROM cat_gateway_settings'))[0].port, 8000);
  } finally { await store.close(); }
}));
