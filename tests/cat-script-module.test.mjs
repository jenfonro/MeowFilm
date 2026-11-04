import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { ScriptHostError } from '@meowfilm/script-host';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';

const temporary = join(temporaryRoot, 'cat-script-module-tests');
mkdirSync(temporary, { recursive: true });
after(() => rmSync(temporary, { recursive: true, force: true }));
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

test('Cat runtime module closes its runtime after transfer cleanup reports a failure', async () => {
  const nativeClearTimeout = globalThis.clearTimeout;
  let runtimeClosed = false;
  let module;
  globalThis.clearTimeout = timer => {
    nativeClearTimeout(timer);
    throw new Error('synthetic transfer cleanup failure');
  };
  try {
    module = createCatScriptModule({
      dataRoot: temporary,
      runtimeFactory: () => ({
        async status() { return { state: 'ready' }; },
        async close() { runtimeClosed = true; },
      }),
    });
  } finally {
    globalThis.clearTimeout = nativeClearTimeout;
  }
  const stage = await module.methods['cat.script.stage.begin']({
    length: 1, sha256: sha256(Buffer.from('x')), kind: 'source',
  });
  assert.equal(stage.ok, true);
  const status = await module.methods['cat.script.status']({});
  assert.equal(status.ok, true);
  const oversized = await module.methods['cat.script.stage.begin']({
    length: 16 * 1024 * 1024 + 1, sha256: sha256(Buffer.from('x')), kind: 'source',
  });
  assert.equal(oversized.ok, false);
  assert.equal(oversized.code, 'RESOURCE_LIMIT');
  await assert.rejects(module.close(), /synthetic transfer cleanup failure/);
  assert.equal(runtimeClosed, true);
});

test('Cat RPC reports one abort-listener cleanup failure and preserves the operation failure', async () => {
  const signal = {
    aborted: false,
    reason: undefined,
    listener: null,
    removals: 0,
    addEventListener(_event, listener) { this.listener = listener; },
    removeEventListener(_event, listener) {
      assert.equal(listener, this.listener);
      this.removals++;
      throw new Error('synthetic detach failure');
    },
  };
  const module = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async status() { return { state: 'ready' }; },
      async close() {},
    }),
  });
  const reply = await module.methods['cat.script.status']({}, { signal });
  assert.deepEqual(reply, { ok: false, contract: 'cat.script.v1', code: 'SCRIPT_EXCEPTION' });
  assert.equal(signal.removals, 1);
  await module.close();
  assert.doesNotThrow(() => signal.listener());
  assert.equal(signal.removals, 1);

  const permanent = {
    aborted: false,
    reason: undefined,
    removals: 0,
    addEventListener() {},
    removeEventListener() { this.removals++; throw new Error('synthetic permanent detach failure'); },
  };
  const failing = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async status() { throw new ScriptHostError('TIMEOUT'); },
      async close() {},
    }),
  });
  assert.deepEqual(await failing.methods['cat.script.status']({}, { signal: permanent }), {
    ok: false, contract: 'cat.script.v1', code: 'TIMEOUT',
  });
  assert.equal(permanent.removals, 1);
  await failing.close();
});

test('Cat RPC exposes starting status and preserves STARTING from invocation', async () => {
  const snapshot = {
    settingsPath: join(temporary, 'settings.json'),
    aggregate: { origin: 'http://127.0.0.1:1', access: 'fixture' },
    instances: [{ installationId: 'pending', state: 'starting' }],
  };
  const module = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async start() { return snapshot; },
      async status() { return snapshot; },
      async invoke() { throw new ScriptHostError('STARTING'); },
      async close() {},
    }),
  });
  const started = await module.methods['cat.script.start']({});
  assert.equal(started.ok, true);
  assert.deepEqual(JSON.parse(started.data), snapshot);
  assert.deepEqual(await module.methods['cat.script.invoke']({
    installationId: 'pending', siteId: 'site', operation: 'home', payload: '{}',
  }), { ok: false, contract: 'cat.script.v1', code: 'STARTING' });
  await module.close();
});
