import test from 'node:test';
import assert from 'node:assert/strict';
import {
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
  ScriptHostError,
} from '../src/index.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';

const source = `
  export default {
    async playerContent() {
      return {
        urls: [
          js2Proxy(false, 0, 'fixture', 'https://media.invalid/one', {}),
          js2Proxy(false, 0, 'fixture', 'https://media.invalid/two', {}),
          js2Proxy(false, 0, 'fixture', 'https://media.invalid/unattempted', {})
        ]
      };
    }
  };
`;

function setup(t, { rollback, close }) {
  const owner = createNodeSourceCapabilities({
    network: async () => assert.fail('virtual routes must not use the network'),
  });
  const calls = [];
  let closing = false;
  const capabilities = createCapabilityClient({
    async invoke(name, input, context) {
      calls.push([name, input.route]);
      if (name === 'virtual.register') {
        const result = await owner.invoke(name, input, context);
        if (input.route === 'route-2') throw new ScriptHostError('CAPABILITY_UNAVAILABLE');
        return result;
      }
      if (name === 'virtual.unregister') {
        const outcome = (closing ? close : rollback)[input.route];
        if (outcome === 'throw') throw new ScriptHostError('CAPABILITY_UNAVAILABLE');
        if (outcome === false) return false;
      }
      return owner.invoke(name, input, context);
    },
  });
  const adapter = createPortableSourceAdapter({ capabilities });
  const host = createScriptHost({ adapters: { cat: adapter } });
  t.after(async () => {
    try { await host.close(); }
    catch { /* Expected persistent failures are asserted before profile disposal. */ }
    await owner.releaseProfiles('source:route-cleanup:');
  });
  return {
    host, owner, calls,
    close() { closing = true; return host.close(); },
  };
}

for (const refusal of [false, 'throw']) {
  test(`portable Worker close retries virtual rollback ${refusal}`, { timeout: 15000 }, async t => {
    const debt = refusal === false ? 'route-2' : 'route-1';
    const confirmed = debt === 'route-1' ? 'route-2' : 'route-1';
    const { host, owner, calls, close } = setup(t, {
      rollback: { [debt]: refusal, [confirmed]: true },
      close: { [debt]: true },
    });
    const instance = await host.install({
      installationId: 'route-cleanup', protocol: 'cat', source, enabled: true,
    });
    const profile = `source:route-cleanup:${instance.generation}`;
    await assert.rejects(host.invoke({
      installationId: instance.installationId,
      generation: instance.generation,
      siteId: 'source',
      operation: 'play',
      payload: {},
    }), { code: 'CAPABILITY_UNAVAILABLE' });
    assert.deepEqual(calls, [
      ['virtual.register', 'route-1'],
      ['virtual.register', 'route-2'],
      ['virtual.unregister', 'route-1'],
      ['virtual.unregister', 'route-2'],
    ]);
    assert.equal(owner.resolveVirtual(profile, confirmed), null);
    assert.ok(owner.resolveVirtual(profile, debt));
    assert.equal(owner.resolveVirtual(profile, 'route-3'), null);

    const closed = await close();
    assert.equal(closed.cleanupComplete, true);
    assert.equal(closed.liveSessions, 0);
    assert.equal(closed.pendingAdapterTasks, 0);
    assert.equal(owner.resolveVirtual(profile, debt), null);
    assert.deepEqual(calls.slice(4), [['virtual.unregister', debt]]);
    await close();
    assert.equal(calls.length, 5);
  });
}

test('portable Worker close reports virtual rollback debts that still fail', { timeout: 15000 }, async t => {
  const { host, owner, calls, close } = setup(t, {
    rollback: { 'route-1': false, 'route-2': 'throw' },
    close: { 'route-1': false, 'route-2': 'throw' },
  });
  const instance = await host.install({
    installationId: 'route-cleanup', protocol: 'cat', source, enabled: true,
  });
  const profile = `source:route-cleanup:${instance.generation}`;
  await assert.rejects(host.invoke({
    installationId: instance.installationId,
    generation: instance.generation,
    siteId: 'source',
    operation: 'play',
    payload: {},
  }), { code: 'CAPABILITY_UNAVAILABLE' });
  await assert.rejects(close(), { code: 'CAPABILITY_UNAVAILABLE' });
  assert.deepEqual(calls.slice(4), [
    ['virtual.unregister', 'route-1'],
    ['virtual.unregister', 'route-2'],
  ]);
  assert.ok(owner.resolveVirtual(profile, 'route-1'));
  assert.ok(owner.resolveVirtual(profile, 'route-2'));
  assert.equal(host.stats().liveSessions, 0);
  assert.equal(host.stats().pendingAdapterTasks, 0);
  await assert.rejects(close(), { code: 'CAPABILITY_UNAVAILABLE' });
  assert.equal(calls.length, 6);
});
