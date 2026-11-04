import test from 'node:test';
import assert from 'node:assert/strict';
import { createHook } from 'node:async_hooks';
import { PortableSandbox } from '../src/portable-sandbox.js';
import { SCRIPT_LIMITS } from '../src/host.js';
import { createNodeSourceCapabilities } from '../src/source-capabilities.js';
import { createCapabilityClient, SourceSession } from '@meowfilm/source-runtime';

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

function response(input, body = '', extra = {}) {
  return {
    statusCode: 200, statusMessage: 'OK',
    headers: { 'content-type': 'text/plain; charset=utf-8' },
    body, url: input.url, ...extra,
  };
}

async function sourceSession(t, {
  source, protocol = 'cat', network = async input => response(input),
  owner = createNodeSourceCapabilities({ network }),
} = {}) {
  const timers = new Set();
  const hook = createHook({
    init(id, type) { if (type === 'Timeout') timers.add(id); },
    destroy(id) { timers.delete(id); },
  }).enable();
  const session = new SourceSession({
    installationId: 'sdk',
    source,
    protocol,
    capabilities: createCapabilityClient({
      maximumBytes: SCRIPT_LIMITS.maxMemoryBytes,
      invoke: (name, input, context) => owner.invoke(name, input, context),
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
  });
  t.after(async () => {
    try {
      await session.close();
      await owner.releaseProfiles('source:sdk:');
      assert.equal(session.stats().timers, 0);
      assert.equal(session.stats().networkRequests, 0);
      assert.equal(session.stats().invocations, 0);
      assert.equal(session.stats().pendingAdapterTasks, 0);
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(timers.size, 0, 'all real Timeout resources must be destroyed');
    } finally { hook.disable(); }
  });
  await session.start();
  session.activate();
  return { session, owner };
}

test('portable intervals repeat until cleared and retire on action completion', { timeout: 10000 }, async t => {
  const { session } = await sourceSession(t, {
    source: `
      export default {
        searchContent() {
          return new Promise(resolve => {
            let count = 0;
            const id = setInterval(() => {
              if (++count === 3) {
                clearInterval(id);
                resolve({ count });
              }
            }, 1);
          });
        },
        homeContent() {
          setInterval(() => {}, 1);
          return { complete: true };
        }
      };
    `,
  });
  assert.deepEqual(await session.invoke('search'), { count: 3 });
  assert.equal(session.stats().timers, 0);
  assert.deepEqual(await session.invoke('home'), { complete: true });
  assert.equal(session.stats().timers, 0);
});

for (const ending of ['cancel', 'close', 'timeout']) {
  test(`portable intervals release real timers when their action ends by ${ending}`,
    { timeout: 10000 }, async t => {
      const entered = deferred();
      const { session } = await sourceSession(t, {
        network: async input => {
          entered.resolve();
          return response(input);
        },
        source: `
          export default {
            searchContent() {
              return new Promise(() => {
                setInterval(() => req('https://fixture.invalid/tick'), 1);
              });
            }
          };
        `,
      });
      const controller = new AbortController();
      const pending = session.invoke('search', {}, {
        signal: controller.signal, ...(ending === 'timeout' ? { timeoutMs: 30 } : {}),
      });
      const rejected = assert.rejects(pending, {
        code: ending === 'cancel' ? 'CANCELLED' : ending === 'timeout' ? 'TIMEOUT' : 'STALE_SESSION',
      });
      await entered.promise;
      if (ending === 'cancel') controller.abort();
      else if (ending === 'close') await session.close();
      await rejected;
      await session.drain();
      assert.equal(session.stats().timers, 0);
      assert.equal(session.stats().pendingAdapterTasks, 0);
    });
}

test('retired Promise reactions cannot borrow a later action storage scope', async t => {
  const writes = [];
  const base = createNodeSourceCapabilities({ network: async input => response(input) });
  const owner = {
    async invoke(name, input, context) {
      if (name === 'storage.set') writes.push({ input, scope: context.scope });
      return base.invoke(name, input, context);
    },
    releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
  };
  const { session } = await sourceSession(t, {
    owner,
    source: `
      const savedLocal = local;
      let resume, late;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            late = new Promise(resolve => { resume = resolve; })
              .then(() => savedLocal.set('late', 'old'))
              .then(() => 'wrote', failure => failure.code);
            return { phase: 'A-complete' };
          }
          resume();
          return late;
        },
        async homeContent() { return { stored: await savedLocal.get('late') }; }
      };
    `,
  });
  assert.deepEqual(await session.invoke('search', { keyword: 'A' }), { phase: 'A-complete' });
  await session.drain();
  assert.equal(writes.length, 0);
  assert.equal(await session.invoke('search', { keyword: 'B' }), 'STALE_SESSION');
  assert.equal(writes.length, 0);
  assert.deepEqual(await session.invoke('home'), { stored: null });
});

test('cancelled Promise reactions remain retired when a later action resolves them', async t => {
  const entered = deferred();
  const writes = [];
  const base = createNodeSourceCapabilities({
    network: async input => {
      entered.resolve();
      return response(input);
    },
  });
  const { session } = await sourceSession(t, {
    owner: {
      async invoke(name, input, context) {
        if (name === 'storage.set') writes.push(input.key);
        return base.invoke(name, input, context);
      },
      releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
    },
    source: `
      const savedLocal = local;
      let resume, late;
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') {
            late = new Promise(resolve => { resume = resolve; })
              .then(() => savedLocal.set('late', 'old'))
              .then(() => 'wrote', failure => failure.code);
            await req('https://fixture.invalid/registered');
            return new Promise(() => {});
          }
          resume();
          const result = await late;
          await savedLocal.set('current', 'new');
          return { result, current: await savedLocal.get('current') };
        }
      };
    `,
  });
  const controller = new AbortController();
  const cancelled = assert.rejects(
    session.invoke('search', { keyword: 'A' }, { signal: controller.signal }),
    { code: 'CANCELLED' },
  );
  await entered.promise;
  controller.abort();
  await cancelled;
  await session.drain();
  assert.deepEqual(await session.invoke('search', { keyword: 'B' }), {
    result: 'STALE_SESSION', current: 'new',
  });
  assert.deepEqual(writes, ['current']);
});

test('retired native await continuations cannot borrow a later action storage scope', async t => {
  const writes = [];
  const base = createNodeSourceCapabilities({ network: async input => response(input) });
  const owner = {
    async invoke(name, input, context) {
      if (name === 'storage.set') writes.push({ input, scope: context.scope });
      return base.invoke(name, input, context);
    },
    releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
  };
  const { session } = await sourceSession(t, {
    owner,
    source: `
      const savedLocal = local;
      let resume, late;
      export default {
        searchContent(keyword) {
          if (keyword === 'A') {
            const old = new Promise(resolve => { resume = resolve; });
            late = (async () => {
              await old;
              try {
                await savedLocal.set('late', 'old');
                return 'wrote';
              } catch (failure) { return failure.code; }
            })();
            return { phase: 'A-complete' };
          }
          resume();
          return late;
        }
      };
    `,
  });
  assert.deepEqual(await session.invoke('search', { keyword: 'A' }), { phase: 'A-complete' });
  await session.drain();
  assert.equal(await session.invoke('search', { keyword: 'B' }), 'STALE_SESSION');
  assert.deepEqual(writes, []);
});

test('current actions retain helper scope through await, Promise reactions and timers', async t => {
  const scopes = [];
  const base = createNodeSourceCapabilities({
    network: async input => {
      scopes.push(input.scope.requestId);
      return response(input, 'network');
    },
  });
  const { session } = await sourceSession(t, {
    owner: base,
    source: `
      const savedReq = req, savedLocal = local;
      export default {
        async searchContent(keyword) {
          const value = await savedReq('https://fixture.invalid/await');
          return Promise.resolve(value.content)
            .then(async text => {
              const next = await savedReq('https://fixture.invalid/then');
              await new Promise(resolve => setTimeout(resolve, 1));
              await savedLocal.set(keyword, text + ':' + next.content);
              throw Error('handled');
            })
            .catch(async () => await savedLocal.get(keyword))
            .finally(() => savedReq('https://fixture.invalid/finally'));
        }
      };
    `,
  });
  assert.equal(await session.invoke('search', { keyword: 'one' }), 'network:network');
  assert.equal(await session.invoke('search', { keyword: 'two' }), 'network:network');
  assert.deepEqual(scopes, [1, 1, 1, 2, 2, 2]);
});

test('QYSG fetch, location and external scripts use the final remote HTML URL', async t => {
  const urls = [];
  const { session } = await sourceSession(t, {
    protocol: 'qysg',
    source: {
      bookSourceName: 'Remote',
      bookSourceUrl: 'https://books.invalid/base/',
      html: 'https://entry.invalid/source',
    },
    network: async input => {
      urls.push(input.url);
      if (input.url === 'https://entry.invalid/source') {
        return response(input, '<script src="./actions.js"></script>', {
          url: 'https://cdn.invalid/rules/source.html',
          headers: { 'content-type': 'text/html' },
        });
      }
      if (input.url === 'https://cdn.invalid/rules/actions.js') {
        return response(input, `
          async function search() {
            const reply = await fetch('./api');
            return [{ name: location.href + ':' + await reply.text() }];
          }
        `);
      }
      assert.equal(input.url, 'https://cdn.invalid/rules/api');
      return response(input, 'correct');
    },
  });
  const result = await session.invoke('search', { keyword: 'fixture' });
  assert.equal(result.books[0].name, 'https://cdn.invalid/rules/source.html:correct');
  assert.deepEqual(urls, [
    'https://entry.invalid/source',
    'https://cdn.invalid/rules/actions.js',
    'https://cdn.invalid/rules/api',
  ]);
});

for (const protocol of ['cat', 'legado', 'qysg']) {
  test(`${protocol} fetch preserves typed-array view and ArrayBuffer request bytes`, async t => {
    const bodies = [];
    const code = `
      const bytes = new Uint8Array([9, 0, 255, 65, 9]);
      await fetch('https://fixture.invalid/view', {
        method: 'POST', headers: { 'Content-Type': 'application/octet-stream' },
        body: bytes.subarray(1, 4)
      });
      await fetch('https://fixture.invalid/buffer', {
        method: 'POST', headers: { 'Content-Type': 'application/octet-stream' },
        body: new Uint8Array([0, 255, 65]).buffer
      });
    `;
    const source = protocol === 'cat'
      ? `export default { async searchContent() { ${code} return []; } };`
      : protocol === 'qysg'
        ? { bookSourceUrl: 'https://fixture.invalid/',
          html: `<script>async function search(){ ${code} return []; }</script>` }
        : { bookSourceName: 'Bytes', bookSourceUrl: 'https://fixture.invalid/',
          coverDecodeJs: `${code} 'complete'` };
    const { session } = await sourceSession(t, {
      source, protocol,
      network: async input => {
        bodies.push(Buffer.from(input.options.body));
        assert.equal(input.options.headers['content-type'], 'application/octet-stream');
        return response(input, 'complete');
      },
    });
    await session.invoke(protocol === 'legado' ? 'image' : 'search', {});
    assert.deepEqual(bodies, [Buffer.from([0, 255, 65]), Buffer.from([0, 255, 65])]);
  });
}

test('invalid guest results fail explicitly instead of waiting for the deadline', async t => {
  const { session } = await sourceSession(t, {
    source: `
      export default {
        searchContent(kind) {
          if (kind === 'bigint') return 1n;
          if (kind === 'cycle') { const value = {}; value.self = value; return value; }
          if (kind === 'getter') return { get value() { throw Error('private result'); } };
          if (kind === 'error-getter') return { get value() {
            throw { get name() { throw Error('private error metadata'); } };
          } };
          return { legal: true };
        }
      };
    `,
  });
  for (const [keyword, code] of [
    ['bigint', 'SCRIPT_EXCEPTION'], ['cycle', 'SCRIPT_EXCEPTION'], ['getter', 'SCRIPT_EXCEPTION'],
    ['error-getter', 'SCRIPT_EXCEPTION'],
  ]) {
    await assert.rejects(session.invoke('search', { keyword }, { timeoutMs: 1000 }), error => {
      assert.equal(error.code, code);
      assert.doesNotMatch(error.message, /private result/);
      return true;
    });
    assert.deepEqual(await session.invoke('search', { keyword: 'valid' }), { legal: true });
  }
});

test('Legado evaluation publishes invalid-result errors without timing out', async t => {
  const { session } = await sourceSession(t, {
    protocol: 'legado',
    source: {
      bookSourceName: 'Invalid result', bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: '1n',
    },
  });
  await assert.rejects(session.invoke('image', {}, { timeoutMs: 1000 }), {
    code: 'SCRIPT_EXCEPTION',
  });
});

test('CryptoJS WordArray defaults to words while typed arrays retain byte semantics', async t => {
  const { session } = await sourceSession(t, {
    source: `
      export default {
        searchContent() {
          const full = CryptoJS.lib.WordArray.create([0x00010203, 0x04050607]);
          const partial = CryptoJS.lib.WordArray.create([0x00010203], 3);
          const bytes = CryptoJS.lib.WordArray.create(new Uint8Array([0, 1, 2, 3]));
          return {
            full: full.toString(), fullBytes: full.sigBytes,
            partial: partial.toString(), partialBytes: partial.sigBytes,
            bytes: bytes.toString(), bytesLength: bytes.sigBytes,
            hash: CryptoJS.MD5(CryptoJS.lib.WordArray.create([0x00010203])).toString()
          };
        }
      };
    `,
  });
  assert.deepEqual(await session.invoke('search'), {
    full: '0001020304050607', fullBytes: 8,
    partial: '000102', partialBytes: 3,
    bytes: '00010203', bytesLength: 4,
    hash: '37b59afd592725f9305e484a5d7f5168',
  });
});
