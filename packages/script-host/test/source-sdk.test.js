import test from 'node:test';
import assert from 'node:assert/strict';
import { createHook } from 'node:async_hooks';
import { PortableSourceSession } from '../src/portable-source-session.js';
import { SCRIPT_LIMITS } from '../src/host.js';
import { createNodeSourceCapabilities } from '../src/source-capabilities.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';
import { createScriptHost } from '../src/index.js';
import { lxScript, target } from './fixtures.js';

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
  const nativeSession = new PortableSourceSession({
    installationId: 'sdk',
    generation: 1,
    revision: { source, protocol, version: '1.0.0' },
    capabilities: createCapabilityClient({
      invoke: (name, input, context) => owner.invoke(name, input, context),
    }),
    limits: {
      ...SCRIPT_LIMITS,
      initTimeoutMs: 5000, requestTimeoutMs: 5000, cleanupTimeoutMs: 1000,
    },
  });
  const session = {
    invoke: (operation, payload = {}, options) => nativeSession.invoke({
      siteId: 'source', operation, payload,
    }, options),
    close: () => nativeSession.stop(),
    drain: () => nativeSession.drain(),
    stats: () => nativeSession.stats(),
  };
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
  await nativeSession.start();
  await nativeSession.activate();
  return { session, owner, nativeSession };
}

test('a minimal source starts, invokes and physically releases both native contexts',
  { timeout: 10000 }, async t => {
    const { session, nativeSession } = await sourceSession(t, {
      source: `
        export default {
          searchContent() { return { list: [{ vod_id: 'fixture', vod_name: 'ready' }] }; }
        };
      `,
    });
    const engine = nativeSession.engine;
    assert.equal(await engine.ready, engine);
    assert.equal(nativeSession.live, true);
    assert.equal(nativeSession.activated, true);
    assert.deepEqual(nativeSession.capabilities.source, {
      protocol: 'cat', importedProtocol: 'cat', actions: ['search'],
    });
    const running = engine.stats();
    assert.equal(running.threadCount, 1);
    assert.equal(running.runtimeCount, 1);
    assert.equal(running.contextCount, 2);
    let exited = false;
    const stopped = engine.exited.then(result => { exited = true; return result; });
    assert.deepEqual(await session.invoke('search'), {
      list: [{ vod_id: 'fixture', vod_name: 'ready' }],
    });
    assert.equal(nativeSession.engine, engine);
    assert.equal(exited, false);
    await session.close();
    assert.equal(exited, true, 'close must await the real native owner exit');
    const closed = await stopped;
    assert.equal(closed.stopped, true);
    for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
      'hostCalls', 'timers', 'jobsPending']) assert.equal(closed[key], 0, key);
  });

test('source core and guest share one native owner and keep private hooks hidden', async t => {
  const { session } = await sourceSession(t, {
    source: `
      let count = 0;
      export default {
        searchContent() {
          return { count: ++count, hidden: [
            typeof __meowHost, typeof __meowCore, typeof __meowGuest,
            typeof __meowNative, typeof __meowEmit, typeof __meowTaskContext,
            new Function('return typeof __meowCore')()
          ] };
        }
      };
    `,
  });
  const hidden = Array(7).fill('undefined');
  assert.deepEqual(await session.invoke('search'), { count: 1, hidden });
  assert.deepEqual(await session.invoke('search'), { count: 2, hidden });
  const stats = session.stats().native;
  assert.equal(stats.threadCount, 1);
  assert.equal(stats.runtimeCount, 1);
  assert.equal(stats.contextCount, 2);
});

test('source decodes legacy charset responses and preserves every response byte', async t => {
  const bytes = Buffer.from([0, 255, 128, 65]);
  const { session } = await sourceSession(t, {
    source: `
      export default {
        async searchContent() {
          const text = await (await fetch('https://fixture.invalid/gbk')).text();
          const raw = new Uint8Array(await (await fetch('https://fixture.invalid/raw')).arrayBuffer());
          const western = await (await fetch('https://fixture.invalid/western')).text();
          const bom = await (await fetch('https://fixture.invalid/bom')).text();
          return { text, hex: Buffer.from(raw).toString('hex'), western, bom };
        }
      };
    `,
    network: async input => {
      if (input.url.endsWith('/gbk')) return response(input, Buffer.from([0xd6, 0xd0, 0xce, 0xc4]), {
        headers: { 'content-type': 'text/plain; charset=gbk' },
      });
      if (input.url.endsWith('/western')) return response(input, Buffer.from([0x80]), {
        headers: { 'content-type': 'text/plain; charset=iso-8859-1' },
      });
      if (input.url.endsWith('/bom')) return response(input, Buffer.from('\ufeffok'));
      return response(input, bytes);
    },
  });
  assert.deepEqual(await session.invoke('search'), {
    text: '中文', hex: bytes.toString('hex'), western: '€', bom: 'ok',
  });
});

test('one source admits B while A awaits I/O without borrowing capability identity', async t => {
  const entered = deferred(), release = deferred();
  const scopes = [];
  const { session } = await sourceSession(t, {
    source: `
      export default {
        async searchContent(keyword) {
          if (keyword === 'A') await req('https://fixture.invalid/A');
          await local.set(keyword, keyword);
          return { value: await local.get(keyword) };
        }
      };
    `,
    network: async input => {
      scopes.push(input.scope.requestId);
      entered.resolve();
      await release.promise;
      return response(input);
    },
  });
  const first = session.invoke('search', { keyword: 'A' });
  try {
    await entered.promise;
    assert.deepEqual(await session.invoke('search', { keyword: 'B' }), { value: 'B' });
  } finally { release.resolve(); }
  assert.deepEqual(await first, { value: 'A' });
  assert.deepEqual(scopes, [1]);
});

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

test('source charset aliases preserve Unicode and NUL while stripping UTF-8 and UTF-16 BOMs', async t => {
  const text = 'A\0中文🎵Z';
  const { session } = await sourceSession(t, {
    source: `
      export default {
        async searchContent() {
          const utf8 = await (await fetch('https://fixture.invalid/utf8-alias')).text();
          const utf16 = await (await fetch('https://fixture.invalid/utf16-auto')).text();
          return { utf8, utf16, node: [typeof require, typeof process] };
        }
      };
    `,
    network: async input => {
      if (input.url.endsWith('/utf8-alias')) return response(input, Buffer.from('\ufeff' + text), {
        headers: { 'content-type': 'text/plain; charset=unicode11utf8' },
      });
      assert.equal(input.url, 'https://fixture.invalid/utf16-auto');
      return response(input, Buffer.from('\ufeff' + text, 'utf16le'), {
        headers: { 'content-type': 'text/plain; charset=utf16' },
      });
    },
  });
  assert.deepEqual(await session.invoke('search'), {
    utf8: text, utf16: text, node: ['undefined', 'undefined'],
  });
});

test('source charset decoding flushes truncated UTF-8 without leaking state or using Node encoding capabilities', async t => {
  const requests = [], hostEncodingCalls = [];
  const replies = new Map([
    ['truncated', Buffer.from([0x61, 0xe2, 0x82])],
    ['complete', Buffer.from([0xf0, 0x9f, 0x8e, 0xb5, 0x62])],
    ['empty', Buffer.alloc(0)],
  ]);
  const base = createNodeSourceCapabilities({
    network: async input => {
      const key = new URL(input.url).pathname.slice(1);
      assert.ok(replies.has(key));
      requests.push(key);
      return response(input, replies.get(key), {
        headers: { 'content-type': 'text/plain; charset=unicode11utf8' },
      });
    },
  });
  const owner = {
    invoke(name, input, context) {
      if (name.startsWith('encoding.')) {
        hostEncodingCalls.push(name);
        throw Error('Source encoding must stay on its native owner thread');
      }
      return base.invoke(name, input, context);
    },
    releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
  };
  const { session } = await sourceSession(t, {
    owner,
    source: `
      export default {
        async searchContent() {
          const values = [];
          for (const path of ['truncated', 'complete', 'empty']) {
            values.push(await (await fetch('https://fixture.invalid/' + path)).text());
          }
          return values;
        }
      };
    `,
  });
  assert.deepEqual(await session.invoke('search'), ['a\ufffd', '🎵b', '']);
  assert.deepEqual(requests, ['truncated', 'complete', 'empty']);
  assert.deepEqual(hostEncodingCalls, []);
});

function sourceDepthValue(depth, leaf = null, objects = false) {
  let value = leaf;
  for (let index = 0; index < depth; index++) value = objects ? { child: value } : [value];
  return value;
}

test('source SDK keeps full business depth through result normalization, clone and guest inputs', async t => {
  const { session, nativeSession } = await sourceSession(t, {
    source: `
      export default {
        proxy(input) { return input; },
        searchContent(kind) {
          if (kind === 'mixed') {
            const shared = {value:'shared'};
            const value = Object.create(null);
            Object.defineProperty(value, '__proto__', {value:shared, enumerable:true});
            value.constructor = {prototype:'ordinary data'};
            value.left = shared; value.right = shared;
            value.omitted = undefined; value.method = () => false;
            value.bytes = new Uint8Array([9, 0, 255, 65, 9]).subarray(1, 4);
            return value;
          }
          const [shape, depth] = kind.split(':');
          let value = null;
          for (let i = 0; i < Number(depth); i++) value = shape === 'object' ? {child:value} : [value];
          return shape === 'envelope' ? {ok:true, value} : value;
        }
      };
    `,
  });
  assert.equal(nativeSession.limits.maxStackBytes, 256 * 1024);
  const engine = nativeSession.engine;
  for (const shape of ['array', 'object']) {
    for (const depth of [8, 62, 64]) {
      assert.deepEqual(await session.invoke('search', { keyword: `${shape}:${depth}` }, { timeoutMs: 1000 }),
        sourceDepthValue(depth, null, shape === 'object'));
    }
    const payload = { value: sourceDepthValue(63, null, shape === 'object') };
    assert.deepEqual(await session.invoke('proxy', payload), payload);
    await assert.rejects(session.invoke('search', { keyword: `${shape}:65` }), { code: 'INVALID_ARGUMENT' });
    await assert.rejects(session.invoke('proxy', { value: sourceDepthValue(64) }), { code: 'INVALID_ARGUMENT' });
  }
  await assert.rejects(session.invoke('search', { keyword: 'envelope:64' }), { code: 'INVALID_ARGUMENT' });
  const bytes = { value: sourceDepthValue(63, Buffer.from([0, 255, 65])) };
  assert.deepEqual(await session.invoke('proxy', bytes), bytes);
  assert.deepEqual(await session.invoke('search', { keyword: 'mixed' }), {
    ['__proto__']: { value: 'shared' }, constructor: { prototype: 'ordinary data' },
    left: { value: 'shared' }, right: { value: 'shared' }, bytes: Buffer.from([0, 255, 65]),
  });
  assert.deepEqual(await session.invoke('search', { keyword: 'array:64' }), sourceDepthValue(64));
  assert.equal(nativeSession.engine, engine);
  assert.equal(nativeSession.live, true);
});

for (const protocol of ['cat', 'legado', 'qysg']) {
  test(`${protocol} capability arguments and settle results preserve a 64-level storage value`, async t => {
    const observed = [];
    const base = createNodeSourceCapabilities({ network: async () => assert.fail('no network') });
    const owner = {
      invoke(name, input, context) {
        if (name.startsWith('storage.')) {
          observed.push({ name, input: structuredClone(input), scope: context.scope });
        }
        return base.invoke(name, input, context);
      },
      releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
    };
    const set = protocol === 'cat' ? "await local.set('deep', value)"
      : protocol === 'legado' ? "await cache.put('deep', value)"
        : "await window.flutter_inappwebview.callHandler('cache.set', 'deep', value)";
    const get = protocol === 'cat' ? "await local.get('deep')"
      : protocol === 'legado' ? "await cache.get('deep')"
        : "await window.flutter_inappwebview.callHandler('cache.get', 'deep')";
    const code = `
      let value = null;
      for (let i = 0; i < 64; i++) value = [value];
      ${set};
      let result = ${get}, depth = 0;
      while (Array.isArray(result)) { depth++; result = result[0]; }
      return String(depth) + ':' + String(result);
    `;
    const source = protocol === 'cat'
      ? `export default { async searchContent() { ${code} } };`
      : protocol === 'legado'
        ? { bookSourceName: 'Depth', bookSourceUrl: 'https://fixture.invalid/', coverDecodeJs: code }
        : { bookSourceUrl: 'https://fixture.invalid/',
          html: `<script>async function imagedecrypt() { ${code} }</script>` };
    const { session } = await sourceSession(t, { source, protocol, owner });
    const result = await session.invoke(protocol === 'cat' ? 'search' : 'image', {});
    assert.equal(protocol === 'cat' ? result : result.body, '64:null');
    assert.deepEqual(observed.map(item => item.name), ['storage.set', 'storage.get']);
    assert.deepEqual(observed[0].input.value, sourceDepthValue(64));
    for (const item of observed) {
      assert.deepEqual(item.scope, {
        installationId: 'sdk', generation: 1, protocol, siteId: 'source', requestId: 1,
      });
    }
  });
}

test('source depth violations, capability failures and actual script stack exhaustion stay explicit and recoverable', async t => {
  const base = createNodeSourceCapabilities({ network: async () => assert.fail('no network') });
  const writes = [];
  const { session } = await sourceSession(t, {
    owner: {
      invoke(name, input, context) {
        if (name === 'storage.set') writes.push(input.key);
        if (name === 'storage.get' && input.key === 'failure') {
          throw Object.assign(new Error('private storage detail'), { code: 'STORAGE_ERROR' });
        }
        if (name === 'storage.get' && input.key === 'oversized') return sourceDepthValue(65);
        return base.invoke(name, input, context);
      },
      releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
    },
    source: `
      export default {
        async searchContent(kind) {
          if (kind === 'stack') {
            function recurse(n) { return 1 + recurse(n + 1); }
            return recurse(0);
          }
          if (kind === 'range') throw new RangeError('not a stack failure');
          if (kind === 'failure' || kind === 'oversized') return local.get(kind);
          if (kind === 'unicode') return '\\ud800';
          if (kind === 'number') return Infinity;
          if (kind === 'unsafe') return 9007199254740992;
          let value = null;
          for (let i = 0; i < (kind === 'depth' ? 65 : 64); i++) value = [value];
          if (kind === 'envelope') value = {ok:true, value};
          if (kind === 'cycle') { value = []; value.push(value); }
          await local.set(kind, value);
          return local.get(kind);
        }
      };
    `,
  });
  for (const [keyword, code] of [
    ['depth', 'INVALID_ARGUMENT'], ['envelope', 'INVALID_ARGUMENT'], ['cycle', 'INVALID_ARGUMENT'],
    ['failure', 'STORAGE_ERROR'], ['oversized', 'INVALID_RESPONSE'],
    ['stack', 'RESOURCE_LIMIT'], ['range', 'SCRIPT_EXCEPTION'],
    ['unicode', 'INVALID_ARGUMENT'], ['number', 'INVALID_ARGUMENT'], ['unsafe', 'INVALID_ARGUMENT'],
  ]) {
    await assert.rejects(session.invoke('search', { keyword }, { timeoutMs: 1000 }), error => {
      assert.equal(error.code, code);
      assert.doesNotMatch(error.message, /private storage detail/);
      return true;
    });
    assert.deepEqual(await session.invoke('search', { keyword: 'valid' }), sourceDepthValue(64));
  }
  assert.deepEqual(writes, Array(10).fill('valid'), 'invalid values must not reach the storage owner');
});

test('Legado synchronous SDK state arguments retain a 64-level business value', async t => {
  const { session } = await sourceSession(t, {
    protocol: 'legado',
    source: {
      bookSourceName: 'State depth', bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: `
        let value = null;
        for (let i = 0; i < 64; i++) value = {child:value};
        java.put('deep', value);
        source.put('deep', value);
        book.setVariable('deep', value);
        const depths = [];
        for (let stored of [java.getVariable('deep'), source.get('deep'), book.getVariable('deep')]) {
          let depth = 0;
          while (stored && typeof stored === 'object') { depth++; stored = stored.child; }
          depths.push(depth);
        }
        return depths.join(',');
      `,
    },
  });
  assert.equal((await session.invoke('image', {})).body, '64,64,64');
});

test('cancelled source reactions cannot borrow B identity through the positional capability path', async t => {
  const entered = deferred(), release = deferred();
  let firstSignal;
  const storageCalls = [];
  const base = createNodeSourceCapabilities({ network: async () => assert.fail('no network') });
  const { session } = await sourceSession(t, {
    owner: {
      async invoke(name, input, context) {
        if (name.startsWith('storage.')) storageCalls.push([name, input.key, context.scope.requestId]);
        if (name === 'storage.get' && input.key === 'hold') {
          firstSignal = context.signal;
          entered.resolve();
          await release.promise;
          return sourceDepthValue(64);
        }
        return base.invoke(name, input, context);
      },
      releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
    },
    source: `
      let resume, late;
      const savedLocal = local;
      function value() { let data = null; for (let i = 0; i < 64; i++) data = [data]; return data; }
      export default {
        async searchContent(kind) {
          if (kind === 'A') {
            late = new Promise(resolve => { resume = resolve; })
              .then(() => savedLocal.set('forbidden', value()))
              .then(() => 'unsafe', error => error.code);
            return savedLocal.get('hold');
          }
          resume();
          const code = await late;
          if (code !== 'STALE_SESSION') throw new Error('retired reaction acquired authority');
          await savedLocal.set('current', value());
          return savedLocal.get('current');
        }
      };
    `,
  });
  const controller = new AbortController();
  const cancelled = assert.rejects(
    session.invoke('search', { keyword: 'A' }, { signal: controller.signal }),
    { code: 'CANCELLED' },
  );
  try {
    await entered.promise;
    controller.abort();
    await cancelled;
    assert.equal(firstSignal.aborted, true);
    assert.equal(session.stats().pendingAdapterTasks, 1, 'physical host work remains tracked');
    assert.deepEqual(await session.invoke('search', { keyword: 'B' }), sourceDepthValue(64));
    assert.deepEqual(storageCalls, [
      ['storage.get', 'hold', 1], ['storage.set', 'current', 2], ['storage.get', 'current', 2],
    ]);
  } finally { release.resolve(); }
  await session.drain();
  assert.equal(session.stats().pendingAdapterTasks, 0);
});

test('positional host hook is hidden before source and LX user initialization', async t => {
  const hidden = `[
    typeof __meowHostArgs, typeof globalThis.__meowHostArgs,
    Object.hasOwn(globalThis, '__meowHostArgs'),
    Function('return typeof __meowHostArgs')()
  ]`;
  const expected = ['undefined', 'undefined', false, 'undefined'];
  const { session } = await sourceSession(t, {
    source: `const atLoad = ${hidden};
      export default { searchContent() { return {atLoad, current:${hidden}}; } };`,
  });
  assert.deepEqual(await session.invoke('search'), { atLoad: expected, current: expected });
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    setup: `const atLoad = ${hidden};`,
    handler: `async () => ({atLoad, current:${hidden}})`,
  }) });
  assert.deepEqual(await host.invoke(target()), { atLoad: expected, current: expected });
  await host.close();
});
