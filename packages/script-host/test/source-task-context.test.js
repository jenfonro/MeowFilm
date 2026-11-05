import test from 'node:test';
import assert from 'node:assert/strict';
import { AsyncLocalStorage, createHook } from 'node:async_hooks';
import { createCapabilityClient, SourceSession } from '@meowfilm/source-runtime';
import { PortableSandbox } from '../src/portable-sandbox.js';
import { SCRIPT_LIMITS } from '../src/host.js';
import { createNodeSourceCapabilities } from '../src/source-capabilities.js';
import { deferred, turn } from './fixtures.js';

async function sourceSession(t, source, {
  protocol = 'cat', operation = 'search', onRequest,
} = {}) {
  const writes = [];
  const ownerCalls = new Set();
  const networkCalls = new Set();
  const timers = new Set();
  const timerOwner = new AsyncLocalStorage();
  const owned = body => timerOwner.run(timers, body);
  const hook = createHook({
    init(id, type) {
      if (type === 'Timeout' && timerOwner.getStore() === timers) timers.add(id);
    },
    destroy(id) { timers.delete(id); },
  }).enable();
  const owner = createNodeSourceCapabilities({
    async network(input) {
      networkCalls.add(input);
      try {
        await onRequest?.(input);
        return {
          statusCode: 200, statusMessage: 'OK',
          headers: { 'content-type': 'text/plain; charset=utf-8' },
          body: 'entered', url: input.url,
        };
      } finally { networkCalls.delete(input); }
    },
  });
  const session = owned(() => new SourceSession({
    installationId: 'task-context',
    protocol,
    source,
    capabilities: createCapabilityClient({
      maximumBytes: SCRIPT_LIMITS.maxMemoryBytes,
      async invoke(name, input, context) {
        const call = { name };
        ownerCalls.add(call);
        try {
          if (name === 'storage.set') {
            writes.push({
              key: input.key, value: structuredClone(input.value),
              scope: structuredClone(context.scope), aborted: context.signal.aborted,
            });
          }
          return await owner.invoke(name, input, context);
        } finally { ownerCalls.delete(call); }
      },
    }),
    limits: {
      maximumBytes: SCRIPT_LIMITS.maxMemoryBytes,
      requestTimeoutMs: 5000, cleanupTimeoutMs: 1000,
    },
    createSandbox: options => PortableSandbox.create({
      ...options,
      limits: {
        ...SCRIPT_LIMITS,
        initTimeoutMs: 5000, requestTimeoutMs: 5000, cleanupTimeoutMs: 1000,
      },
    }),
  }));
  const idle = async (live = true) => {
    await owned(() => session.drain());
    // Timeout destroy notifications follow resource retirement on the next turn.
    await turn();
    assert.deepEqual(session.stats(), {
      live, invocations: 0, timers: 0, networkRequests: 0, pendingAdapterTasks: 0,
    });
    assert.equal(ownerCalls.size, 0, 'capability owner calls must have settled');
    assert.equal(networkCalls.size, 0, 'physical network calls must have settled');
    assert.equal(timers.size, 0, 'session-owned real Timeout resources must be destroyed');
  };
  t.after(async () => {
    try {
      // Closing the session or releasing its profiles must not make this pass.
      await idle();
      await owned(() => session.close());
      await idle(false);
    } finally {
      try {
        await owned(() => session.close());
        await owner.releaseProfiles('source:task-context:');
      } finally {
        hook.disable();
        timerOwner.disable();
      }
    }
  });
  await owned(() => session.start());
  session.activate();
  await idle();
  return {
    writes, idle,
    invoke: (keyword, options) => owned(() => session.invoke(operation, { keyword }, options)),
  };
}

function currentWrites(writes, entries, protocol = 'cat') {
  assert.deepEqual(writes, entries.map(([key, value]) => ({
    key, value, aborted: false,
    scope: {
      installationId: 'task-context', generation: 1, protocol,
      siteId: 'source', requestId: 2,
    },
  })));
}

test('an async then handler stays retired while B registers a reaction and serializes a route getter',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local, savedProxy = js2Proxy;
      let old, resume, late;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            old = new Promise(resolve => { resume = resolve; });
            let started;
            const entered = new Promise(resolve => { started = resolve; });
            late = Promise.resolve().then(async () => {
              started();
              await old;
              try {
                await savedLocal.set('A-handler', 'old');
                return 'wrote';
              } catch (failure) { return failure.code; }
            });
            return entered.then(() => ({ phase: 'A-complete' }));
          }
          return (async () => {
            const current = old.then(async value => {
              await savedLocal.set('B-reaction', value);
              return value;
            });
            resume('current');
            const retired = await late;
            const value = await current;
            await savedLocal.set('B-after', value);
            return {
              retired, current: await savedLocal.get('B-after'),
              get route() {
                return savedProxy(false, 0, 'fixture', 'https://media.invalid/current', {});
              }
            };
          })();
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      retired: 'STALE_SESSION', current: 'current',
      route: 'meow-source://source%3Atask-context%3A1/route-1',
    });
    currentWrites(writes, [['B-reaction', 'current'], ['B-after', 'current']]);
    await idle();
  });

for (const exit of ['break', 'return']) {
  test(`for-await retains retired scope through implicit next and ${exit} cleanup`,
    { timeout: 15000 }, async t => {
      const { invoke, writes, idle } = await sourceSession(t, `
        const savedLocal = local;
        let resume, late;
        const events = [];
        export default {
          searchContent(keyword) {
            if (keyword === 'A') {
              const old = new Promise(resolve => { resume = resolve; });
              const outcomes = [];
              const iterable = {
                [Symbol.asyncIterator]() { return this; },
                next() { events.push('next'); return old; },
                async return() {
                  events.push('return');
                  await Promise.resolve();
                  try {
                    await savedLocal.set('A-close', 'old');
                    outcomes.push(['close', 'wrote']);
                  } catch (failure) { outcomes.push(['close', failure.code]); }
                  return { done: true };
                }
              };
              late = (async () => {
                for await (const value of iterable) {
                  try {
                    await savedLocal.set('A-body', value);
                    outcomes.push(['body', 'wrote']);
                  } catch (failure) { outcomes.push(['body', failure.code]); }
                  if (${JSON.stringify(exit)} === 'return') return outcomes;
                  break;
                }
                return outcomes;
              })();
              return { phase: 'A-complete' };
            }
            return (async () => {
              resume({ value: 'old', done: false });
              const outcomes = await late;
              await savedLocal.set('B-after-close', 'current');
              return {
                outcomes, events,
                current: await savedLocal.get('B-after-close')
              };
            })();
          }
        };
      `);
      assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
      await idle();
      assert.deepEqual(writes, []);
      assert.deepEqual(await invoke('B'), {
        outcomes: [['body', 'STALE_SESSION'], ['close', 'STALE_SESSION']],
        events: ['next', 'return'], current: 'current',
      });
      currentWrites(writes, [['B-after-close', 'current']]);
      await idle();
    });
}

test('for-await unwraps a synchronous iterator value without borrowing its resolver scope',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resume, late;
      const events = [];
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            const iterable = {
              [Symbol.iterator]() { return this; },
              next() { events.push('next'); return { value: old, done: false }; },
              return() { events.push('return'); return { done: true }; }
            };
            late = (async () => {
              let outcome;
              for await (const value of iterable) {
                try {
                  await savedLocal.set('A-unwrapped', value);
                  outcome = 'wrote';
                } catch (failure) { outcome = failure.code; }
                break;
              }
              return outcome;
            })();
            return { phase: 'A-complete' };
          }
          return (async () => {
            resume('old');
            const outcome = await late;
            await savedLocal.set('B-after-unwrap', 'current');
            return { outcome, events, current: await savedLocal.get('B-after-unwrap') };
          })();
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      outcome: 'STALE_SESSION', events: ['next', 'return'], current: 'current',
    });
    currentWrites(writes, [['B-after-unwrap', 'current']]);
    await idle();
  });

test('async generator pending yield and queued return stay retired while a direct B next is legal',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resume, first, closed, currentGenerator, cleanup;
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            const retiredGenerator = (async function* () {
              try { yield old; }
              finally {
                try {
                  await savedLocal.set('A-generator-close', 'old');
                  cleanup = 'wrote';
                } catch (failure) { cleanup = failure.code; }
              }
            })();
            first = retiredGenerator.next();
            closed = retiredGenerator.return('closed');
            currentGenerator = (async function* () {
              yield 'primed';
              await savedLocal.set('B-generator', 'current');
              return 'B-done';
            })();
            return { phase: 'A-complete', primed: await currentGenerator.next() };
          }
          resume('old');
          const retired = [await first, await closed];
          // This next is issued by B after A's ordinary yield has completed.
          const current = await currentGenerator.next();
          await savedLocal.set('B-after-generator', 'current');
          return {
            retired, cleanup, current,
            stored: await savedLocal.get('B-after-generator')
          };
        }
      };
    `);
    assert.deepEqual(await invoke('A'), {
      phase: 'A-complete', primed: { value: 'primed', done: false },
    });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      retired: [{ value: 'old', done: false }, { value: 'closed', done: true }],
      cleanup: 'STALE_SESSION', current: { value: 'B-done', done: true }, stored: 'current',
    });
    currentWrites(writes, [['B-generator', 'current'], ['B-after-generator', 'current']]);
    await idle();
  });

test('an A next queued behind a running B generator request resumes with A ownership',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resumeOld, resumeHead, generator, late, queued;
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') {
            late = new Promise(resolve => { resumeOld = resolve; }).then(() => {
              const next = generator.next();
              queued();
              return next;
            });
            return { phase: 'A-complete' };
          }
          generator = (async function* () {
            await new Promise(resolve => { resumeHead = resolve; });
            yield 'B-head';
            try {
              await savedLocal.set('A-queued-next', 'old');
              return 'wrote';
            } catch (failure) { return failure.code; }
          })();
          const head = generator.next();
          const added = new Promise(resolve => { queued = resolve; });
          resumeOld();
          await added;
          resumeHead();
          const first = await head, second = await late;
          await savedLocal.set('B-after-queue', 'current');
          return { first, second, current: await savedLocal.get('B-after-queue') };
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      first: { value: 'B-head', done: false },
      second: { value: 'STALE_SESSION', done: true }, current: 'current',
    });
    currentWrites(writes, [['B-after-queue', 'current']]);
    await idle();
  });

test('FinalizationRegistry entries retain their register scope when B allocations trigger GC',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let target, registry, finalized, finish;
      let calls = 0, completed = 0;
      const outcomes = {};
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') {
            finalized = new Promise(resolve => { finish = resolve; });
            registry = new FinalizationRegistry(key => {
              calls++;
              const value = key === 'A-finalizer' ? 'old' : 'current';
              savedLocal.set(key, value).then(
                () => { outcomes[key] = 'wrote'; },
                failure => { outcomes[key] = failure.code; }
              ).then(() => {
                if (++completed === 2) finish(outcomes);
              });
            });
            target = {};
            registry.register(target, 'A-finalizer');
            return { phase: 'A-complete' };
          }
          let currentTarget = {};
          registry.register(currentTarget, 'B-finalizer');
          currentTarget = null;
          target = null;
          // This engine schedules GC from live allocation counts, not retained
          // bytes, so ordinary object allocations drive it. Each round yields to
          // the host so the finalizers' capability calls can settle. No GC hook
          // or no-trigger fallback: both callbacks must finish before the deadline.
          let settled = false;
          const observed = finalized.then(value => { settled = true; return value; });
          const pressure = [];
          while (!settled) {
            for (let index = 0; index < 16384; index++) pressure.push({ index });
            await new Promise(resolve => setTimeout(resolve, 0));
          }
          await savedLocal.set('B-after-GC', 'current');
          return {
            observed: await observed, calls, completed,
            current: await savedLocal.get('B-after-GC')
          };
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      observed: { 'A-finalizer': 'STALE_SESSION', 'B-finalizer': 'wrote' },
      calls: 2, completed: 2, current: 'current',
    });
    currentWrites(writes, [['B-finalizer', 'current'], ['B-after-GC', 'current']]);
    await idle();
  });

for (const compiler of ['AsyncFunction', 'eval']) {
  test(`runtime ${compiler} await code retains its retired action identity`,
    { timeout: 15000 }, async t => {
      const { invoke, writes, idle } = await sourceSession(t, `
        const savedLocal = local;
        let resume, late;
        export default {
          searchContent(keyword) {
            if (keyword === 'A') {
              const old = new Promise(resolve => { resume = resolve; });
              const body = [
                'await gate;',
                "try { await saved.set('A-dynamic', 'old'); return 'wrote'; }",
                'catch (failure) { return failure.code; }'
              ].join('\\n');
              const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
              const run = ${JSON.stringify(compiler)} === 'AsyncFunction'
                ? new AsyncFunction('gate', 'saved', body)
                : eval('(async (gate, saved) => {' + body + '})');
              late = run(old, savedLocal);
              return { phase: 'A-complete' };
            }
            return (async () => {
              resume();
              const outcome = await late;
              await savedLocal.set('B-after-dynamic', 'current');
              return { outcome, current: await savedLocal.get('B-after-dynamic') };
            })();
          }
        };
      `);
      assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
      await idle();
      assert.deepEqual(writes, []);
      assert.deepEqual(await invoke('B'), { outcome: 'STALE_SESSION', current: 'current' });
      currentWrites(writes, [['B-after-dynamic', 'current']]);
      await idle();
    });
}

test('cancelled await and async then frames stay retired while B timer thenables retain scope',
  { timeout: 15000 }, async t => {
    const entered = deferred();
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resume, late;
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            let started;
            const handlerEntered = new Promise(resolve => { started = resolve; });
            late = [
              (async () => {
                await old;
                try {
                  await savedLocal.set('A-cancelled-await', 'old');
                  return 'wrote';
                } catch (failure) { return failure.code; }
              })(),
              Promise.resolve().then(async () => {
                started();
                await old;
                try {
                  await savedLocal.set('A-cancelled-handler', 'old');
                  return 'wrote';
                } catch (failure) { return failure.code; }
              })
            ];
            await handlerEntered;
            setInterval(() => {}, 60000);
            await req('https://fixture.invalid/cancel-ready');
            return new Promise(() => {});
          }
          resume();
          const outcomes = await Promise.all(late);
          const current = await new Promise((resolve, reject) => {
            setTimeout(() => ({
              then(done, fail) {
                (async () => {
                  await savedLocal.set('B-timer', 'current');
                  return await savedLocal.get('B-timer');
                })().then(
                  value => { resolve(value); done(); },
                  failure => { reject(failure); fail(failure); }
                );
              }
            }), 1);
          });
          return { outcomes, current };
        }
      };
    `, {
      onRequest(input) {
        assert.equal(input.url, 'https://fixture.invalid/cancel-ready');
        assert.equal(input.scope.requestId, 1);
        entered.resolve();
      },
    });
    const controller = new AbortController();
    const cancelled = assert.rejects(invoke('A', { signal: controller.signal }), {
      code: 'CANCELLED',
    });
    await entered.promise;
    controller.abort();
    await cancelled;
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      outcomes: ['STALE_SESSION', 'STALE_SESSION'], current: 'current',
    });
    currentWrites(writes, [['B-timer', 'current']]);
    await idle();
  });

test('a retired rejected await preserves its error identity and catch/finally ownership',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      const originalFailure = {};
      let rejectOld, late;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise((_resolve, reject) => { rejectOld = reject; });
            const outcome = {};
            late = (async () => {
              try { await old; outcome.unexpected = true; }
              catch (failure) {
                outcome.identity = failure === originalFailure;
                try {
                  await savedLocal.set('A-catch', 'old');
                  outcome.catch = 'wrote';
                } catch (failure) { outcome.catch = failure.code; }
                return outcome;
              } finally {
                try {
                  await savedLocal.set('A-finally', 'old');
                  outcome.finally = 'wrote';
                } catch (failure) { outcome.finally = failure.code; }
              }
            })();
            return { phase: 'A-complete' };
          }
          return (async () => {
            rejectOld(originalFailure);
            const outcome = await late.finally(async () => {
              await Promise.resolve();
              await savedLocal.set('B-finally', 'current');
            });
            return { outcome, current: await savedLocal.get('B-finally') };
          })();
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      outcome: { identity: true, catch: 'STALE_SESSION', finally: 'STALE_SESSION' },
      current: 'current',
    });
    currentWrites(writes, [['B-finally', 'current']]);
    await idle();
  });

test('retired Legado reactions cannot use the cache and source helpers installed for B',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, {
      bookSourceName: 'Task context', bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: `
        if (key === 'A') {
          globalThis.oldResult = new Promise(resolve => { globalThis.resumeOld = resolve; })
            .then(async () => {
              const outcomes = [];
              try {
                await globalThis.cache.put('A-current-cache', 'old');
                outcomes.push('wrote');
              } catch (failure) { outcomes.push(failure.code); }
              try {
                await globalThis.source.putLoginInfo('old');
                outcomes.push('wrote');
              } catch (failure) { outcomes.push(failure.code); }
              return outcomes;
            });
          return { phase: 'A-complete' };
        }
        globalThis.resumeOld();
        const outcomes = await globalThis.oldResult;
        await cache.put('B-legado', 'current');
        return { outcomes, current: await cache.get('B-legado') };
      `,
    }, { protocol: 'legado', operation: 'image' });
    assert.deepEqual(await invoke('A'), { body: { phase: 'A-complete' }, url: '' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      body: { outcomes: ['STALE_SESSION', 'STALE_SESSION'], current: 'current' }, url: '',
    });
    currentWrites(writes, [['B-legado', 'current']], 'legado');
    await idle();
  });

test('retired reaction thenable getters and nested assimilation callbacks cannot borrow B',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resume, late;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            const attempts = [];
            let getterCalls = 0, thenCalls = 0, nestedCalls = 0, receiver = false;
            const attempt = key => savedLocal.set(key, 'old')
              .then(() => 'wrote', failure => failure.code);
            late = old.then(() => {
              const outer = {
                get then() {
                  getterCalls++;
                  attempts.push(attempt('A-then-getter'));
                  return function(resolve) {
                    thenCalls++;
                    receiver = this === outer;
                    attempts.push(attempt('A-thenable'));
                    resolve({
                      then(resolveNested) {
                        nestedCalls++;
                        attempts.push(attempt('A-nested-thenable'));
                        resolveNested('assimilated');
                      }
                    });
                  };
                }
              };
              return outer;
            }).then(async value => ({
              value, outcomes: await Promise.all(attempts),
              getterCalls, thenCalls, nestedCalls, receiver
            }));
            return { phase: 'A-complete' };
          }
          return (async () => {
            resume();
            const outcome = await late;
            await savedLocal.set('B-after-assimilation', 'current');
            return { outcome, current: await savedLocal.get('B-after-assimilation') };
          })();
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      outcome: {
        value: 'assimilated', outcomes: ['STALE_SESSION', 'STALE_SESSION', 'STALE_SESSION'],
        getterCalls: 1, thenCalls: 1, nestedCalls: 1, receiver: true,
      },
      current: 'current',
    });
    currentWrites(writes, [['B-after-assimilation', 'current']]);
    await idle();
  });

test('native Promise order, identity and introspection survive private task context propagation',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const hiddenAtLoad = typeof __meowTaskContext;
      const ownAtLoad = Object.prototype.hasOwnProperty.call(globalThis, '__meowTaskContext');
      const initialThen = Promise.prototype.then;
      const sourceAtLoad = Function.prototype.toString.call(initialThen);
      export default {
        async searchContent() {
          const order = [];
          const awaited = (async () => {
            await Promise.resolve();
            order.push('await');
          })();
          const queued = new Promise(resolve => {
            queueMicrotask(() => { order.push('queued'); resolve(); });
          });
          await Promise.all([awaited, queued]);

          const value = {}, failure = {}, finalFailure = {};
          const original = Promise.resolve(value);
          const derived = original.then();
          let finallyArguments = -1;
          const fulfilled = await derived.finally(function() {
            finallyArguments = arguments.length;
            return Promise.resolve('ignored');
          });
          const caught = await Promise.reject(failure).catch(reason => {
            if (reason !== failure) throw Error('rejection identity changed');
            return value;
          });
          const rejected = await Promise.reject(failure)
            .finally(() => Promise.resolve('ignored'))
            .catch(reason => reason === failure);
          const thrown = await original.then(() => { throw failure; })
            .catch(reason => reason === failure);
          const finalRejected = await original.finally(() => Promise.reject(finalFailure))
            .catch(reason => reason === finalFailure);
          let invalidReceiver = false;
          try { initialThen.call({}); }
          catch (failure) { invalidReceiver = failure instanceof TypeError; }
          class Derived extends Promise {}
          const species = Derived.resolve(value).then();
          const descriptor = Object.getOwnPropertyDescriptor(Promise.prototype, 'then');
          const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
          return {
            order,
            identities: [
              Promise.resolve(original) === original, derived !== original,
              fulfilled === value, caught === value, rejected, thrown, finalRejected,
              species instanceof Derived, await species === value
            ],
            finallyArguments, invalidReceiver,
            hidden: [
              hiddenAtLoad, typeof __meowTaskContext, typeof globalThis.__meowTaskContext,
              eval('typeof __meowTaskContext'),
              await new AsyncFunction('return typeof __meowTaskContext')()
            ],
            ownHook: [
              ownAtLoad, Object.prototype.hasOwnProperty.call(globalThis, '__meowTaskContext')
            ],
            then: {
              same: initialThen === Promise.prototype.then,
              nativeAtLoad: sourceAtLoad.includes('[native code]'),
              nativeNow: Function.prototype.toString.call(Promise.prototype.then)
                .includes('[native code]'),
              sameSource: sourceAtLoad === Function.prototype.toString.call(Promise.prototype.then),
              ownToString: Object.prototype.hasOwnProperty.call(initialThen, 'toString'),
              name: initialThen.name, length: initialThen.length,
              writable: descriptor.writable, enumerable: descriptor.enumerable,
              configurable: descriptor.configurable
            }
          };
        }
      };
    `);
    assert.deepEqual(await invoke('inspect'), {
      order: ['await', 'queued'],
      identities: [true, true, true, true, true, true, true, true, true],
      finallyArguments: 0, invalidReceiver: true,
      hidden: ['undefined', 'undefined', 'undefined', 'undefined', 'undefined'],
      ownHook: [false, false],
      then: {
        same: true, nativeAtLoad: true, nativeNow: true, sameSource: true,
        ownToString: false, name: 'then', length: 2,
        writable: true, enumerable: false, configurable: true,
      },
    });
    assert.deepEqual(writes, []);
    await idle();
  });

test('SDK Response.json resumes retired scope before calling user toString getters and functions',
  { timeout: 15000 }, async t => {
    const { invoke, writes, idle } = await sourceSession(t, `
      const savedLocal = local;
      let resume, late, getterWrite, stringWrite;
      let getterCalls = 0, stringCalls = 0;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            const response = new Response({ status: 200, body: 'unused' });
            response.text = () => old;
            late = response.json();
            return { phase: 'A-complete' };
          }
          return (async () => {
            resume({
              get toString() {
                getterCalls++;
                getterWrite = savedLocal.set('A-json-getter', 'old')
                  .then(() => 'wrote', failure => failure.code);
                return function() {
                  stringCalls++;
                  stringWrite = savedLocal.set('A-json-toString', 'old')
                    .then(() => 'wrote', failure => failure.code);
                  return '{"from":"old"}';
                };
              }
            });
            const value = await late;
            const outcomes = await Promise.all([getterWrite, stringWrite]);
            await savedLocal.set('B-after-json', 'current');
            return {
              value, outcomes, getterCalls, stringCalls,
              current: await savedLocal.get('B-after-json')
            };
          })();
        }
      };
    `);
    assert.deepEqual(await invoke('A'), { phase: 'A-complete' });
    await idle();
    assert.deepEqual(writes, []);
    assert.deepEqual(await invoke('B'), {
      value: { from: 'old' }, outcomes: ['STALE_SESSION', 'STALE_SESSION'],
      getterCalls: 1, stringCalls: 1, current: 'current',
    });
    currentWrites(writes, [['B-after-json', 'current']]);
    await idle();
  });
