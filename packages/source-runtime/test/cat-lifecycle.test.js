import test from 'node:test';
import assert from 'node:assert/strict';
import { createCapabilityClient, SourceSession, sourceFault } from '../src/index.js';

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  promise.catch(() => {});
  return { promise, resolve, reject };
}

async function catSession(t, invoke) {
  const calls = [];
  const session = new SourceSession({
    installationId: 'cat-lifecycle',
    protocol: 'cat',
    source: { script: 'export default {};' },
    capabilities: createCapabilityClient({
      invoke: () => assert.fail('the controlled Spider does not use host capabilities'),
    }),
    createSandbox: async () => ({
      capabilities: { actions: ['init', 'search', 'destroy'] },
      async load() {},
      activate() {},
      async invoke(action, payload, { context, signal }) {
        const call = { action, payload: structuredClone(payload), id: context.invocationId, signal };
        calls.push(call);
        return invoke(call);
      },
      close() {},
      async drain() {},
    }),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  return { session, calls };
}

test('CatVod shares a successful automatic init but not ordinary action I/O or request identities',
  { timeout: 10000 }, async t => {
    const entered = deferred(), release = deferred(), slowEntered = deferred(), slowRelease = deferred();
    t.after(() => { release.resolve(); slowRelease.resolve(); });
    let inits = 0;
    const { session, calls } = await catSession(t, async call => {
      if (call.action === 'init') {
        inits++;
        entered.resolve(call);
        await release.promise;
        return null;
      }
      if (call.payload.keyword === 'slow') {
        slowEntered.resolve();
        await slowRelease.promise;
      }
      return { keyword: call.payload.keyword, id: call.id, inits };
    });
    const a = session.invoke('search', { keyword: 'A', ext: { owner: 'A' } });
    const init = await entered.promise;
    const b = session.invoke('search', { keyword: 'B', ext: { owner: 'B' } });
    assert.deepEqual(calls.map(call => call.action), ['init']);
    assert.deepEqual(init.payload, { ext: { owner: 'A' } });
    release.resolve();
    assert.deepEqual(await a, { keyword: 'A', id: 1, inits: 1 });
    assert.deepEqual(await b, { keyword: 'B', id: 2, inits: 1 });
    assert.equal(init.id, 1);
    assert.notEqual(calls.find(call => call.id === 2).signal, init.signal);

    const slow = session.invoke('search', { keyword: 'slow' });
    await slowEntered.promise;
    assert.deepEqual(await session.invoke('search', { keyword: 'fast' }),
      { keyword: 'fast', id: 4, inits: 1 });
    slowRelease.resolve();
    assert.deepEqual(await slow, { keyword: 'slow', id: 3, inits: 1 });
    assert.equal(inits, 1);
  });

test('CatVod cancelling an init waiter leaves its owner and the next waiter live',
  { timeout: 10000 }, async t => {
    const entered = deferred(), release = deferred();
    t.after(() => release.resolve());
    const { session, calls } = await catSession(t, async call => {
      if (call.action === 'init') {
        entered.resolve(call);
        await release.promise;
      }
      return { id: call.id };
    });
    const a = session.invoke('search', { keyword: 'A' });
    const init = await entered.promise;
    const controller = new AbortController();
    const b = session.invoke('search', { keyword: 'B' }, { signal: controller.signal });
    const cancelled = assert.rejects(b, { code: 'CANCELLED' });
    const c = session.invoke('search', { keyword: 'C' });
    controller.abort();
    await cancelled;
    assert.equal(init.signal.aborted, false);
    release.resolve();
    assert.deepEqual(await a, { id: 1 });
    assert.deepEqual(await c, { id: 3 });
    assert.deepEqual(calls.map(({ action, id }) => [action, id]),
      [['init', 1], ['search', 1], ['search', 3]]);
  });

for (const action of ['search', 'init']) {
  for (const ending of ['failure', 'cancel']) {
    test(`CatVod ${action} init owner ${ending} lets a live waiter retry with its own identity`,
      { timeout: 10000 }, async t => {
        const firstEntered = deferred(), firstRelease = deferred();
        const retryEntered = deferred(), retryRelease = deferred();
        t.after(() => { firstRelease.resolve(); retryRelease.resolve(); });
        let inits = 0;
        const { session, calls } = await catSession(t, async call => {
          if (call.action === 'init') {
            if (++inits === 1) {
              firstEntered.resolve(call);
              // Deliberately settle after cancellation to exercise the state fence.
              await firstRelease.promise;
            } else {
              retryEntered.resolve(call);
              await retryRelease.promise;
            }
          }
          return { id: call.id, inits };
        });
        const controller = new AbortController();
        const a = session.invoke(action, { keyword: 'A', ext: { owner: 'A' } },
          { signal: controller.signal });
        const failed = assert.rejects(a, { code: ending === 'failure' ? 'NETWORK_ERROR' : 'CANCELLED' });
        const first = await firstEntered.promise;
        const b = session.invoke('search', { keyword: 'B', ext: { owner: 'B' } });
        if (ending === 'failure') firstRelease.reject(sourceFault('NETWORK_ERROR'));
        else {
          controller.abort();
          await failed;
          firstRelease.resolve('late cancelled success');
        }
        await failed;
        const retry = await retryEntered.promise;
        assert.equal(first.id, 1);
        assert.equal(retry.id, 2);
        assert.notEqual(retry.signal, first.signal);
        assert.equal(retry.signal.aborted, false);
        assert.deepEqual(retry.payload, { ext: { owner: 'B' } });
        retryRelease.resolve();
        assert.deepEqual(await b, { id: 2, inits: 2 });
        assert.deepEqual(calls.filter(call => call.action === 'search').map(call => call.id), [2]);
        assert.deepEqual(await session.invoke('search', { keyword: 'after-retry' }), { id: 3, inits: 2 });
      });
  }
}

test('CatVod orders automatic init, explicit init, destroy and subsequent automatic init',
  { timeout: 10000 }, async t => {
    const entered = [deferred(), deferred(), deferred(), deferred()];
    const release = [deferred(), deferred(), deferred(), deferred()];
    t.after(() => { for (const gate of release) gate.resolve(); });
    let lifecycleCalls = 0;
    const { session, calls } = await catSession(t, async call => {
      if (call.action !== 'search') {
        const index = lifecycleCalls++;
        entered[index].resolve(call);
        await release[index].promise;
        return { action: call.action, id: call.id };
      }
      return { id: call.id };
    });
    const a = session.invoke('search', { keyword: 'A' });
    await entered[0].promise;
    const explicit = session.invoke('init', { ext: { explicit: true } });
    const destroy = session.invoke('destroy');
    const b = session.invoke('search', { keyword: 'B' });
    assert.equal(lifecycleCalls, 1);
    release[0].resolve();
    assert.deepEqual(await a, { id: 1 });
    const second = await entered[1].promise;
    assert.equal(second.action, 'init');
    assert.equal(second.id, 2);
    assert.deepEqual(second.payload, { ext: { explicit: true } });
    assert.equal(lifecycleCalls, 2);
    release[1].resolve();
    assert.deepEqual(await explicit, { action: 'init', id: 2 });
    assert.equal((await entered[2].promise).action, 'destroy');
    assert.equal(lifecycleCalls, 3);
    release[2].resolve();
    assert.deepEqual(await destroy, { action: 'destroy', id: 3 });
    const afterDestroy = await entered[3].promise;
    assert.equal(afterDestroy.action, 'init');
    assert.equal(afterDestroy.id, 4);
    release[3].resolve();
    assert.deepEqual(await b, { id: 4 });
    assert.deepEqual(calls.filter(call => call.action !== 'search').map(call => [call.action, call.id]),
      [['init', 1], ['init', 2], ['destroy', 3], ['init', 4]]);
  });

for (const action of ['init', 'destroy']) {
  for (const ending of ['failure', 'cancel']) {
    test(`CatVod ${action} ${ending} preserves the last successful lifecycle state for its successor`,
      { timeout: 10000 }, async t => {
        const entered = deferred(), release = deferred();
        t.after(() => release.resolve());
        let inits = 0;
        const { session, calls } = await catSession(t, async call => {
          if (call.action === 'init') inits++;
          if (call.id === 2) {
            entered.resolve();
            await release.promise;
          }
          return { id: call.id, inits };
        });
        assert.deepEqual(await session.invoke('search', { keyword: 'first' }), { id: 1, inits: 1 });
        const controller = new AbortController();
        const changing = session.invoke(action, {}, { signal: controller.signal });
        const failed = assert.rejects(changing, {
          code: ending === 'failure' ? 'SCRIPT_EXCEPTION' : 'CANCELLED',
        });
        await entered.promise;
        const next = session.invoke('search', { keyword: 'next' });
        if (ending === 'failure') release.reject(sourceFault('SCRIPT_EXCEPTION'));
        else {
          controller.abort();
          await failed;
          release.resolve('late cancelled success');
        }
        await failed;
        assert.deepEqual(await next, { id: 3, inits: action === 'init' ? 2 : 1 });
        assert.deepEqual(calls.map(call => [call.action, call.id]),
          [['init', 1], ['search', 1], [action, 2], ['search', 3]]);
      });
  }
}
