import test from 'node:test';
import assert from 'node:assert/strict';
import { createPanRuntimeModule } from '../apps/runtime/modules/pan-runtime.mjs';

test('pan module forwards trusted native-store reload, exposes no credentials and owns its drain', async t => {
  let saved = { version: 1, accounts: {} }, release, entered;
  const ready = new Promise(resolve => { entered = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const store = {
    read: () => structuredClone(saved),
    async reload() { entered(); await gate; return structuredClone(saved); },
    async update() { assert.fail('ordinary native edits must not be HTTP-backfilled'); },
    async close() {},
  };
  const module = createPanRuntimeModule({ store, transport: {
    async request() { assert.fail('loading native settings must not request upstream'); },
    async close() {},
  } });
  t.after(() => { release(); return module.close(); });
  const read = () => module.methods['pan.settings.get']({}, {});
  assert.equal((await read()).value.accounts.find(item => item.provider === 'baidu').configured, false);
  saved = { version: 1, accounts: { baidu: {
    revision: 2, rowRevision: 2, credentials: { cookie: 'synthetic-native-private' },
  } } };
  const notification = module.settingsChanged();
  await ready;
  let closed = false;
  const close = module.close().then(() => { closed = true; });
  assert.equal(closed, false);
  release();
  await Promise.all([notification, close]);
  assert.equal(closed, true, 'closing drains an admitted native reload');
  await assert.rejects(read(), { code: 'CLOSED' });
});

test('pan module reports bad native accounts individually without replacing their authority', async t => {
  let saved = null;
  const module = createPanRuntimeModule({ store: {
    read: () => saved,
    async reload() { return saved; },
    async update() { assert.fail('no second persistence owner'); },
    async close() {},
  }, transport: { async request() { assert.fail('passive settings'); }, async close() {} } });
  t.after(() => module.close());
  saved = { version: 1, accounts: { baidu: {
    revision: 1, rowRevision: 1, credentials: { cookie: 'synthetic-native-private' },
  } } };
  await module.settingsChanged();
  const result = await module.methods['pan.settings.get']({}, {});
  assert.equal(result.ok, true);
  assert.equal(result.contract, 'pan.runtime.v1');
  assert.equal(result.value.accounts.find(item => item.provider === 'baidu').revision, 1);
  assert.equal(JSON.stringify(result).includes('synthetic-native-private'), false);
  saved = { version: 1, accounts: { baidu: { revision: 0, rowRevision: 1, credentials: {} } } };
  await module.settingsChanged();
  const changed = await module.methods['pan.settings.get']({}, {});
  assert.deepEqual(changed.value.accounts.find(item => item.provider === 'baidu'),
    { provider: 'baidu', errorCode: 'CORRUPT_STORAGE' });
  assert.equal(changed.value.accounts.find(item => item.provider === 'uc').status, 'signedOut');
  saved = { version: 1, accounts: [] };
  await assert.rejects(module.settingsChanged(), { code: 'CORRUPT_STORAGE' });
  await assert.rejects(module.methods['pan.settings.get']({}, {}), { code: 'CORRUPT_STORAGE' });
});
