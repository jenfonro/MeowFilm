import test from 'node:test';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred, until } from '../packages/runtime-bridge/test/http-fixture.js';
import { serverFor } from '../packages/script-host/test/http-fixture.js';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../apps/runtime/modules/source-runtime.mjs';
import {
  createNodeNetwork, createNodeSourceCapabilities, createScriptHost, ScriptHostError,
} from '@meowfilm/script-host';

const fixtureSource = {
  bookSourceName: 'Fixture',
  bookSourceUrl: 'https://books.invalid/',
  searchUrl: 'https://books.invalid/search?key={{key}}',
  ruleSearch: {
    bookList: '.book',
    name: '.name@text',
    bookUrl: 'a@href',
  },
};
const catSource = `
  export default {
    async searchContent(keyword) {
      return { list: [{ vod_id: keyword, vod_name: keyword }] };
    }
  };
`;

test('CatVod reporting is a discovered formal action with exact playback payload, not an arbitrary callback URL', async t => {
  const { rpc } = setup(t);
  const instance = await load(rpc, { protocol: 'cat', source: `
    export default {
      async playerContent(flag, id) { return { url: 'https://media.invalid/video', watchReport: true }; },
      async report(payload) { return { ok: true, received: payload }; }
    };
  ` });
  assert.ok(instance.capabilities.source.actions.includes('report'));
  const payload = { flag: '原始线路', id: 0, sessionId: 'attempt', event: 'progress',
    positionSeconds: 12.5, durationSeconds: 300 };
  const reported = okay(await rpc('invoke', { installationId: instance.installationId,
    revision: instance.revision, generation: instance.generation, action: 'report', payload }));
  assert.deepEqual(reported, { ok: true, received: payload });
  const absent = await load(rpc, { installationId: 'without-report', protocol: 'cat', source: catSource });
  assert.equal(absent.capabilities.source.actions.includes('report'), false);
  denied(await rpc('invoke', { installationId: absent.installationId, revision: absent.revision,
    generation: absent.generation, action: 'report', payload }), 'UNSUPPORTED_ACTION');
});

function setup(t, { owner: suppliedOwner, hostFactory } = {}) {
  const network = async input => ({
    statusCode: 200,
    statusMessage: 'OK',
    headers: { 'content-type': 'text/html; charset=utf-8' },
    body: '<div class="book"><span class="name">Result</span><a href="/book/1">open</a></div>',
    url: input.url,
  });
  const owner = suppliedOwner ?? createNodeSourceCapabilities({ network });
  const module = createSourceRuntimeModule({
    network,
    capabilityOwner: owner,
    hostFactory,
    platform: 'test',
  });
  t.after(() => module.close());
  const rpc = moduleHttp(t, module);
  return { module, rpc, owner };
}

function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, sourceRuntimeContract);
  return reply.value;
}

function denied(reply, code) {
  assert.equal(reply.ok, false, JSON.stringify(reply));
  assert.equal(reply.contract, sourceRuntimeContract);
  assert.equal(reply.code, code);
}

async function upload(rpc, value, basename = 'source.json', installationId = 'fixture', sourceRevision = 1) {
  const bytes = Buffer.from(typeof value === 'string' ? value : `${JSON.stringify(value)}\n`);
  okay(await rpc.upload(installationId, sourceRevision, bytes, { basename }));
  return sourceRevision;
}

async function load(rpc, {
  installationId = 'fixture',
  revision = 1,
  expectedRevision = revision - 1,
  source = fixtureSource,
  protocol = 'legado',
  basename = protocol === 'cat' ? 'source.js' : 'source.json',
} = {}) {
  const sourceRevision = await upload(rpc, source, basename, installationId, revision);
  return okay(await rpc('load', {
    sourceRevision,
    installationId,
    revision,
    expectedRevision,
    protocol,
    version: '1.0.0',
    metadata: {},
  })).instance;
}

async function catLifecycleHttp(t, heldPaths) {
  const gates = new Map(heldPaths.map(path => [path, deferred()]));
  const upstream = await serverFor(t, (request, response) => {
    const closed = deferred();
    response.once('close', closed.resolve);
    const gate = gates.get(request.url);
    if (gate) gate.resolve({ response, closed: closed.promise });
    else response.end('ok');
  });
  const network = createNodeNetwork(), networkCalls = [], sessions = new Map(), admissions = new Map();
  const key = (installationId, action, payload) => JSON.stringify([installationId, action, payload]);
  let host;
  const { module, rpc } = setup(t, {
    owner: createNodeSourceCapabilities({ network: input => {
      networkCalls.push({ ...input, path: new URL(input.url).pathname });
      return network(input);
    } }),
    hostFactory: options => {
      const adapter = options.adapters.cat;
      host = createScriptHost({ ...options, adapters: { ...options.adapters,
        cat: input => {
          const session = adapter(input);
          sessions.set(input.installationId, session);
          return session;
        },
      } });
      const invoke = host.invoke.bind(host);
      host.invoke = (target, options) => {
        const result = invoke(target, options);
        const ticket = admissions.get(key(target.installationId, target.operation, target.payload));
        if (ticket) {
          admissions.delete(key(target.installationId, target.operation, target.payload));
          // The real native invoke is already queued. A following core command
          // acknowledges that admission before the test releases the HTTP gate.
          sessions.get(target.installationId).engine.call('stats')
            .then(ticket.admitted.resolve, ticket.admitted.reject);
          result.then(ticket.finished.resolve, ticket.finished.resolve);
        }
        return result;
      };
      return host;
    },
  });
  const source = `
    const origin = ${JSON.stringify(upstream.socketOrigin)};
    export default {
      async init(ext) {
        const attempt = this.inits = (this.inits || 0) + 1;
        const response = await req(origin + '/init/' + attempt);
        if (response.content === 'fail') throw new Error('init failed');
        await req(origin + '/init-complete/' + attempt);
        this.ready = attempt;
        return { attempt, ext };
      },
      async searchContent(keyword) {
        await req(origin + '/search/' + encodeURIComponent(keyword));
        if (keyword === 'slow') await req(origin + '/slow');
        return { keyword, inits: this.inits, ready: this.ready, destroys: this.destroys || 0 };
      },
      async destroy() {
        await req(origin + '/destroy');
        this.ready = 0;
        return { destroys: this.destroys = (this.destroys || 0) + 1 };
      }
    };
  `;
  const identity = ({ installationId, revision, generation }) => ({ installationId, revision, generation });
  const request = (instance, action, payload = {}, options) => {
    const ticket = { admitted: deferred(), finished: deferred() };
    admissions.set(key(instance.installationId, action, payload), ticket);
    return {
      result: rpc('invoke', { ...identity(instance), action, payload }, options),
      admitted: ticket.admitted.promise,
      finished: ticket.finished.promise,
    };
  };
  const exited = async session => {
    await session.engine.exited;
    await session.whenAdaptersIdle();
    for (const field of ['threadCount', 'runtimeCount', 'contextCount']) {
      assert.equal(session.engine.stats()[field], 0, field);
    }
    assert.equal(session.stats().pendingAdapterTasks, 0);
  };
  const close = async () => {
    okay(await module.close());
    for (const session of sessions.values()) await exited(session);
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
  };
  return { rpc, host, sessions, source, identity, request, networkCalls, gates, exited, close };
}

test('CatVod HTTP shares asynchronous init while preserving request scopes, action overlap and independent installations',
  { timeout: 15000 }, async t => {
    const fixture = await catLifecycleHttp(t, ['/init/1', '/slow']);
    const { rpc, source, request, gates, networkCalls, identity, sessions, host } = fixture;
    const instance = await load(rpc, { protocol: 'cat', source });
    const other = await load(rpc, { installationId: 'other', protocol: 'cat', source: catSource });
    const a = request(instance, 'search', { keyword: 'A' });
    const init = await gates.get('/init/1').promise;
    const b = request(instance, 'search', { keyword: 'B' });
    assert.ok((await b.admitted).pendingAdapterTasks >= 2, 'B reached the rule owner while init was pending');
    assert.equal(okay(await request(other, 'search', { keyword: 'independent' }).result)
      .list[0].vod_name, 'independent');
    assert.deepEqual(networkCalls.map(call => call.path), ['/init/1']);
    init.response.end('ok');
    for (const [call, keyword] of [[a, 'A'], [b, 'B']]) {
      assert.deepEqual(okay(await call.result), { keyword, inits: 1, ready: 1, destroys: 0 });
    }
    const initScope = networkCalls.find(call => call.path === '/init/1').scope;
    const aScope = networkCalls.find(call => call.path === '/search/A').scope;
    const bScope = networkCalls.find(call => call.path === '/search/B').scope;
    assert.equal(initScope.requestId, aScope.requestId);
    assert.notEqual(aScope.requestId, bScope.requestId);
    for (const scope of [initScope, aScope, bScope]) {
      assert.equal(scope.installationId, instance.installationId);
      assert.equal(scope.generation, instance.generation);
    }

    const slow = request(instance, 'search', { keyword: 'slow' });
    const slowIO = await gates.get('/slow').promise;
    assert.deepEqual(okay(await request(instance, 'search', { keyword: 'fast' }).result),
      { keyword: 'fast', inits: 1, ready: 1, destroys: 0 });
    slowIO.response.end('ok');
    assert.deepEqual(okay(await slow.result), { keyword: 'slow', inits: 1, ready: 1, destroys: 0 });
    assert.equal(host.get(instance.installationId).generation, instance.generation);

    const retired = sessions.get(instance.installationId);
    okay(await rpc('unload', identity(instance)));
    await fixture.exited(retired);
    assert.equal(host.cleanupPending(instance.installationId), 0);
    const successor = await load(rpc, { protocol: 'cat', source: catSource, revision: 2, expectedRevision: 0 });
    assert.ok(successor.generation > instance.generation);
    denied(await rpc('invoke', { ...identity(instance), action: 'search', payload: { keyword: 'stale' } }),
      'STALE_SOURCE');
    assert.equal(okay(await request(successor, 'search', { keyword: 'successor' }).result)
      .list[0].vod_name, 'successor');
    assert.equal(okay(await request(other, 'search', { keyword: 'still-independent' }).result)
      .list[0].vod_name, 'still-independent');
    await fixture.close();
  });

test('CatVod HTTP cancellation of an admitted init waiter does not cancel the init owner',
  { timeout: 15000 }, async t => {
    const fixture = await catLifecycleHttp(t, ['/init/1']);
    const { rpc, source, request, gates, networkCalls, sessions, host } = fixture;
    const instance = await load(rpc, { protocol: 'cat', source });
    const a = request(instance, 'search', { keyword: 'A' });
    const init = await gates.get('/init/1').promise;
    const controller = new AbortController();
    const b = request(instance, 'search', { keyword: 'B' }, { signal: controller.signal });
    assert.ok((await b.admitted).pendingAdapterTasks >= 2);
    controller.abort();
    denied(await b.result, 'CANCELLED');
    await b.finished;
    await sessions.get(instance.installationId).engine.call('stats');
    assert.equal(networkCalls[0].signal.aborted, false);
    const c = request(instance, 'search', { keyword: 'C' });
    await c.admitted;
    init.response.end('ok');
    assert.deepEqual(okay(await a.result), { keyword: 'A', inits: 1, ready: 1, destroys: 0 });
    assert.deepEqual(okay(await c.result), { keyword: 'C', inits: 1, ready: 1, destroys: 0 });
    assert.equal(networkCalls.some(call => call.path === '/search/B'), false);
    assert.equal(networkCalls.filter(call => call.path.startsWith('/init/')).length, 1);
    assert.equal(host.get(instance.installationId).generation, instance.generation);
    await fixture.close();
  });

for (const ending of ['failure', 'cancel']) {
  test(`CatVod HTTP ${ending} of the init owner lets its admitted waiter retry under its own scope`,
    { timeout: 15000 }, async t => {
      const fixture = await catLifecycleHttp(t, ['/init/1', '/init/2']);
      const { rpc, source, request, gates, networkCalls, host } = fixture;
      const instance = await load(rpc, { protocol: 'cat', source });
      const controller = new AbortController();
      const a = request(instance, 'search', { keyword: 'A' }, { signal: controller.signal });
      const first = await gates.get('/init/1').promise;
      const b = request(instance, 'search', { keyword: 'B' });
      assert.ok((await b.admitted).pendingAdapterTasks >= 2);
      if (ending === 'failure') first.response.end('fail');
      else controller.abort();
      denied(await a.result, ending === 'failure' ? 'SOURCE_FAILURE' : 'CANCELLED');
      await a.finished;
      await first.closed;
      const retry = await gates.get('/init/2').promise;
      const firstCall = networkCalls.find(call => call.path === '/init/1');
      const retryCall = networkCalls.find(call => call.path === '/init/2');
      assert.notEqual(firstCall.scope.requestId, retryCall.scope.requestId);
      assert.equal(retryCall.scope.generation, instance.generation);
      assert.equal(retryCall.signal.aborted, false);
      if (ending === 'cancel') assert.equal(firstCall.signal.aborted, true);
      retry.response.end('ok');
      assert.deepEqual(okay(await b.result), { keyword: 'B', inits: 2, ready: 2, destroys: 0 });
      assert.equal(networkCalls.find(call => call.path === '/search/B').scope.requestId,
        retryCall.scope.requestId);
      assert.equal(networkCalls.some(call => call.path === '/search/A'), false);
      assert.equal(networkCalls.some(call => call.path === '/init-complete/1'), false,
        'the failed or retired init must not continue under B');
      assert.deepEqual(okay(await request(instance, 'search', { keyword: 'after-retry' }).result),
        { keyword: 'after-retry', inits: 2, ready: 2, destroys: 0 });
      assert.equal(host.get(instance.installationId).generation, instance.generation);
      await fixture.close();
    });
}

test('CatVod HTTP explicit init and destroy remain ordered before the next automatic initialization',
  { timeout: 15000 }, async t => {
    const fixture = await catLifecycleHttp(t, ['/init/2', '/destroy', '/init/3']);
    const { rpc, source, request, gates, networkCalls, host } = fixture;
    const instance = await load(rpc, { protocol: 'cat', source });
    assert.deepEqual(okay(await request(instance, 'search', { keyword: 'initial' }).result),
      { keyword: 'initial', inits: 1, ready: 1, destroys: 0 });
    const explicit = request(instance, 'init', { ext: { explicit: true } });
    const init = await gates.get('/init/2').promise;
    const destroy = request(instance, 'destroy');
    await destroy.admitted;
    const next = request(instance, 'search', { keyword: 'after-destroy' });
    await next.admitted;
    assert.equal(networkCalls.some(call => ['/destroy', '/init/3'].includes(call.path)), false);
    init.response.end('ok');
    assert.deepEqual(okay(await explicit.result), { attempt: 2, ext: { explicit: true } });
    const destroying = await gates.get('/destroy').promise;
    assert.equal(networkCalls.some(call => call.path === '/init/3'), false);
    destroying.response.end('ok');
    assert.deepEqual(okay(await destroy.result), { destroys: 1 });
    const reinitializing = await gates.get('/init/3').promise;
    reinitializing.response.end('ok');
    assert.deepEqual(okay(await next.result), { keyword: 'after-destroy', inits: 3, ready: 3, destroys: 1 });
    const lifecycle = networkCalls.filter(call => call.path.startsWith('/init/') || call.path === '/destroy');
    assert.deepEqual(lifecycle.map(call => call.path), ['/init/1', '/init/2', '/destroy', '/init/3']);
    assert.equal(new Set(lifecycle.map(call => call.scope.requestId)).size, 4);
    assert.equal(networkCalls.find(call => call.path === '/search/after-destroy').scope.requestId,
      lifecycle.at(-1).scope.requestId);
    assert.equal(host.get(instance.installationId).generation, instance.generation);
    await fixture.close();
  });

test('source HTTP uploads, loads, invokes, reports and unloads a source instance', async t => {
  const { rpc } = setup(t);
  const instance = await load(rpc);
  assert.deepEqual(instance.capabilities, {
    source: {
      protocol: 'legado',
      importedProtocol: 'legado',
      actions: ['search', 'loginStatus', 'image'],
    },
  });
  const result = okay(await rpc('invoke', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
    action: 'search',
    payload: { keyword: 'fixture', page: 1 },
  }));
  assert.equal(result.books[0].name, 'Result');
  assert.equal(result.books[0].bookUrl, 'https://books.invalid/book/1');
  assert.deepEqual(okay(await rpc('status')).instances, [instance]);
  assert.deepEqual(okay(await rpc('unload', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
  })), {
    unloaded: true,
    cleanupComplete: true,
  });
  assert.deepEqual(okay(await rpc('status')).instances, []);
});

test('source RPC auto-detects raw HTML, Cat JS and JSON-string-wrapped HTML', async t => {
  const { rpc } = setup(t);
  const html = '<html><script>function search(){ return []; }</script></html>';
  const cases = [
    ['raw-html', 'source.html', html, 'qysg'],
    ['wrapped-html', 'source.json', JSON.stringify(html), 'qysg'],
    ['raw-cat', 'source.js', catSource, 'cat'],
  ];
  for (const [installationId, basename, source, protocol] of cases) {
    const sourceRevision = await upload(rpc, source, basename, installationId, 1);
    const instance = okay(await rpc('load', {
      sourceRevision,
      installationId,
      revision: 1,
      expectedRevision: 0,
      version: '1.0.0',
      metadata: {},
    })).instance;
    assert.equal(instance.protocol, protocol);
    assert.equal(instance.importedProtocol, protocol);
    okay(await rpc('unload', {
      installationId,
      revision: 1,
      generation: instance.generation,
    }));
  }
});

for (const protocol of ['legado', 'qysg']) {
  test(`${protocol} image HTTP results preserve byte bodies and keep JSON validation strict`, async t => {
    let internal;
    const { rpc } = setup(t, {
      owner: createNodeSourceCapabilities({ network: () => assert.fail('image decoding is local') }),
      hostFactory: options => {
        const host = createScriptHost(options), invoke = host.invoke.bind(host);
        host.invoke = async (...args) => {
          internal = await invoke(...args);
          return internal;
        };
        return host;
      },
    });
    const decode = protocol === 'legado'
      ? 'java.base64DecodeToByteArray(result)' : 'Buffer.from(result, "base64")';
    const script = `
      if (result === 'text') return 'plain-image';
      if (result === 'view') return new Uint8Array([9, 0, 255, 65, 9]).subarray(1, 4);
      if (result === 'nested') return { nested: new Uint8Array([0, 255, 65]) };
      if (result === 'bigint') return 1n;
      return ${decode};
    `;
    const source = {
      bookSourceName: 'Image bytes', bookSourceUrl: 'https://books.invalid/',
      ...(protocol === 'legado' ? { coverDecodeJs: script }
        : { html: `<script>function imagedecrypt(url, result) { ${script} }</script>` }),
    };
    const instance = await load(rpc, { source, protocol });
    const image = body => rpc('invoke', {
      installationId: instance.installationId, revision: instance.revision,
      generation: instance.generation, action: 'image',
      payload: { url: 'https://books.invalid/cover', body },
    });
    for (const body of ['AP9B', 'view']) {
      assert.deepEqual(okay(await image(body)), {
        url: 'https://books.invalid/cover', body: { type: 'Buffer', data: [0, 255, 65] },
      });
      assert.ok(internal.body instanceof Uint8Array, 'the internal ABI still returns typed bytes');
      assert.deepEqual([...internal.body], [0, 255, 65]);
    }
    assert.deepEqual(okay(await image('text')), {
      url: 'https://books.invalid/cover', body: 'plain-image',
    });
    denied(await image('nested'), 'INVALID_REQUEST');
    denied(await image('bigint'), 'SOURCE_FAILURE');
    assert.deepEqual(okay(await image('AP9B')).body, { type: 'Buffer', data: [0, 255, 65] });
  });
}

test('Legado HTTP rules preserve explicit Promise chains, recovery, finally and await propagation', async t => {
  const owner = createNodeSourceCapabilities({ network: async input => {
    if (input.url.endsWith('/fail')) throw new ScriptHostError('NETWORK_ERROR');
    return { statusCode: 200, statusMessage: 'OK', headers: {}, body: 'ok', url: input.url };
  } });
  const { rpc } = setup(t, { owner });
  const instance = await load(rpc, { source: {
    bookSourceName: 'Promise chains', bookSourceUrl: 'https://books.invalid/',
    coverDecodeJs: `
      const trace = [];
      async function read(path) {
        trace.push('read');
        return await java.ajax('https://books.invalid/' + path);
      }
      async function run() {
        if (result === 'then') return read('ok')
          .then(value => { trace.push('then'); return value; })
          .finally(() => trace.push('finally')).then(value => ({ value, trace }));
        if (result === 'catch') return read('fail')
          .catch(error => { trace.push(error.code); return 'recovered'; })
          .finally(() => trace.push('finally')).then(value => ({ value, trace }));
        if (result === 'finally') return read('fail')
          .finally(() => trace.push('finally'))
          .catch(error => { trace.push(error.code); return { value: 'recovered', trace }; });
        if (result === 'await') {
          const value = await read('ok'); trace.push('await'); return { value, trace };
        }
        if (result === 'sync') {
          const value = read('ok'); trace.push('sync'); return { value, trace };
        }
        return java.ajax('https://books.invalid/ok')
          .then(value => { trace.push('then'); return { value, trace }; });
      }
      run()
    `,
  } });
  for (const [mode, value, trace] of [
    ['then', 'ok', ['read', 'then', 'finally']],
    ['catch', 'recovered', ['read', 'NETWORK_ERROR', 'finally']],
    ['finally', 'recovered', ['read', 'finally', 'NETWORK_ERROR']],
    ['await', 'ok', ['read', 'await']],
    ['sync', 'ok', ['read', 'sync']],
    ['direct', 'ok', ['then']],
  ]) {
    assert.deepEqual(okay(await rpc('invoke', {
      installationId: instance.installationId, revision: instance.revision,
      generation: instance.generation, action: 'image', payload: { body: mode },
    })).body, { value, trace }, mode);
  }
});

test('Legado HTTP rules still reject host-dependent Promise chains in Array callbacks', async t => {
  const { rpc } = setup(t, {
    owner: createNodeSourceCapabilities({ network: () => assert.fail('unsafe callbacks must not execute') }),
  });
  const instance = await load(rpc, { source: {
    bookSourceName: 'Unsafe callback', bookSourceUrl: 'https://books.invalid/',
    coverDecodeJs: `
      function read() { return java.ajax('https://books.invalid/'); }
      [1].map(() => read().then(value => value))
    `,
  } });
  denied(await rpc('invoke', {
    installationId: instance.installationId, revision: instance.revision,
    generation: instance.generation, action: 'image', payload: {},
  }), 'UNSUPPORTED_RULE');
});

test('QYSG HTTP list actions reject malformed and non-list results while preserving arrays and wrappers', async t => {
  const { rpc } = setup(t, {
    owner: createNodeSourceCapabilities({ network: () => assert.fail('list fixtures are local') }),
  });
  const instance = await load(rpc, { protocol: 'qysg', source: {
    bookSourceName: 'List contracts', bookSourceUrl: 'https://books.invalid/',
    html: `<script>
      let current;
      function imagedecrypt(url, value) { current = value.value; return ''; }
      function search() { return current; }
      function getfinds() { return current; }
      function find() { return current; }
      function chapter() { return current; }
      function content() { return current; }
    </script>`,
  } });
  const invoke = (action, payload = {}) => rpc('invoke', {
    installationId: instance.installationId, revision: instance.revision,
    generation: instance.generation, action, payload,
  });
  const select = async value => { okay(await invoke('image', { body: { value } })); };
  for (const [action, key] of [
    ['search', 'books'], ['exploreKinds', 'kinds'], ['explore', 'books'], ['toc', 'chapters'],
  ]) {
    for (const value of ['[{"name":', '{"list":[', 'not a list', 7, false, null, {}, { [key]: {} }]) {
      await select(value);
      denied(await invoke(action), 'SOURCE_FAILURE');
    }
    for (const value of [[], '[]', { [key]: [] }, { list: [] },
      JSON.stringify({ [key]: [] }), '{"list":[]}']) {
      await select(value);
      assert.deepEqual(okay(await invoke(action))[key], [], `${action}: ${JSON.stringify(value)}`);
    }
    const item = action === 'exploreKinds' ? { title: 'Kind', url: '/find' }
      : action === 'toc' ? { name: 'Chapter', chapterUrl: '/chapter' }
        : { name: 'Book', bookUrl: '/book' };
    for (const wrapper of [key, 'list']) {
      await select(JSON.stringify({ [wrapper]: [item] }));
      const items = okay(await invoke(action))[key];
      assert.equal(items.length, 1);
      if (action === 'exploreKinds') {
        assert.equal(items[0].title, 'Kind');
        assert.equal(items[0].url, '/find');
      } else if (action === 'toc') {
        assert.equal(items[0].name, 'Chapter');
        assert.equal(items[0].chapterUrl, 'https://books.invalid/chapter');
      } else {
        assert.equal(items[0].name, 'Book');
        assert.equal(items[0].bookUrl, 'https://books.invalid/book');
      }
    }
  }
  for (const value of ['ordinary chapter text', '[{"name":']) {
    await select(value);
    assert.deepEqual(okay(await invoke('content')), {
      title: '', chapter: {}, content: value, blocks: [{ type: 'text', text: value }], imageStyle: '',
    });
  }
  const cat = await load(rpc, { installationId: 'plain-cat', protocol: 'cat',
    source: 'export default { searchContent(keyword) { return keyword; } };' });
  for (const keyword of ['ordinary CatVod text', '[{"name":']) {
    assert.equal(okay(await rpc('invoke', {
      installationId: cat.installationId, revision: cat.revision, generation: cat.generation,
      action: 'search', payload: { keyword },
    })), keyword);
  }
});

test('source owner receives trusted generation scope and a source-domain profile', async t => {
  const network = async input => ({
    statusCode: 200,
    statusMessage: 'OK',
    headers: { 'content-type': 'text/html' },
    body: '<div class="book"><span class="name">Scoped</span><a href="/1">open</a></div>',
    url: input.url,
  });
  const base = createNodeSourceCapabilities({ network });
  const calls = [];
  const owner = {
    invoke(name, input, context) {
      calls.push({
        name,
        input: structuredClone(input),
        scope: structuredClone(context.scope),
      });
      return base.invoke(name, input, context);
    },
    releaseProfiles: (prefix, except) => base.releaseProfiles(prefix, except),
  };
  const { rpc } = setup(t, { owner });
  const instance = await load(rpc, { installationId: 'shared' });
  okay(await rpc('invoke', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
    action: 'search',
    payload: { keyword: 'scope' },
  }));
  const request = calls.find(call => call.name === 'http.request');
  assert.equal(request.input.cookieProfile,
    `source:shared:${instance.generation}:http`);
  assert.deepEqual(request.scope, {
    installationId: 'shared',
    generation: instance.generation,
    protocol: 'legado',
    siteId: 'source',
    requestId: 1,
  });
});

test('external source owners must implement profile cleanup', () => {
  assert.throws(() => createSourceRuntimeModule({
    network: async () => ({}),
    capabilityOwner: { invoke() {} },
  }), { code: 'INVALID_REQUEST' });
});

test('source close reports host shutdown failures as cleanup failures', async () => {
  const module = createSourceRuntimeModule({
    network: async () => ({}),
    hostFactory: () => ({
      list: () => [],
      async close() { throw new Error('synthetic host close failure'); },
    }),
  });
  denied(await module.close(), 'SOURCE_CLEANUP');
});

for (const hostFailure of ['throw', 'incomplete']) {
  test(`source direct and RPC close preserve the ${hostFailure} cleanup failure and run once`, async () => {
    let closes = 0;
    const module = createSourceRuntimeModule({
      network: async () => assert.fail('close must not use the network'),
      hostFactory: () => ({
        list: () => [],
        async close() {
          closes++;
          if (hostFailure === 'throw') throw new Error('private host cleanup diagnostics');
          return { cleanupComplete: false };
        },
      }),
    });
    const direct = module.close();
    const rpc = module.methods['source.runtime.close']({});
    for (const reply of await Promise.all([direct, rpc])) {
      assert.deepEqual(reply, {
        ok: false, contract: sourceRuntimeContract, code: 'SOURCE_CLEANUP',
      });
    }
    denied(await module.close(), 'SOURCE_CLEANUP');
    denied(await module.methods['source.runtime.close']({}), 'SOURCE_CLEANUP');
    assert.equal(closes, 1);
  });
}

test('source profile cleanup debt remains tracked until close retries it', async t => {
  const base = createNodeSourceCapabilities({
    network: async input => ({
      statusCode: 200,
      statusMessage: 'OK',
      headers: {},
      body: '',
      url: input.url,
    }),
  });
  let failures = 2;
  let releases = 0;
  const owner = {
    invoke: (name, input, context) => base.invoke(name, input, context),
    async releaseProfiles(prefix, except) {
      releases++;
      if (failures-- > 0) throw new Error('synthetic cleanup failure');
      await base.releaseProfiles(prefix, except);
    },
  };
  const { module, rpc } = setup(t, { owner });
  denied(await rpc('load', {
    sourceRevision: await upload(rpc, fixtureSource, 'source.json', 'cleanup-debt'),
    installationId: 'cleanup-debt',
    revision: 1,
    expectedRevision: 0,
    protocol: 'legado',
    version: '1.0.0',
    metadata: {},
  }), 'SOURCE_CLEANUP');
  assert.deepEqual(okay(await rpc('status')).instances, []);
  okay(await module.close());
  assert.ok(releases >= 3);
});

test('failed route cleanup removes the retired RPC binding and keeps other instances usable', {
  timeout: 15000,
}, async t => {
  const base = createNodeSourceCapabilities({
    network: async () => assert.fail('virtual route cleanup must not use the network'),
  });
  const releases = [];
  let host;
  const owner = {
    invoke(name, input, context) {
      if (name === 'virtual.unregister' && input.profile.startsWith('source:retired:')) return false;
      return base.invoke(name, input, context);
    },
    async releaseProfiles(prefix, except) {
      releases.push({ prefix, except });
      await base.releaseProfiles(prefix, except);
    },
  };
  const { module, rpc } = setup(t, {
    owner,
    hostFactory: options => { host = createScriptHost(options); return host; },
  });
  const retired = await load(rpc, {
    installationId: 'retired',
    protocol: 'cat',
    source: `export default {
      async playerContent() {
        return { url: js2Proxy(false, 0, 'fixture', 'https://media.invalid/one', {}) };
      }
    };`,
  });
  const healthy = await load(rpc, {
    installationId: 'healthy', protocol: 'cat', source: catSource,
  });
  const identity = ({ installationId, revision, generation }) =>
    ({ installationId, revision, generation });
  okay(await rpc('invoke', { ...identity(retired), action: 'play', payload: {} }));
  const profile = `source:retired:${retired.generation}`;
  assert.ok(base.resolveVirtual(profile, 'route-1'));
  denied(await rpc('unload', identity(retired)), 'SOURCE_CLEANUP');
  assert.deepEqual(host.list().map(value => value.installationId), ['healthy']);
  assert.deepEqual(host.stats().cleanupIssues, [{
    installationId: 'retired', generation: retired.generation, code: 'CAPABILITY_UNAVAILABLE',
  }]);
  assert.deepEqual(okay(await rpc('status')).instances, [healthy]);
  assert.deepEqual(okay(await rpc('invoke', {
    ...identity(healthy), action: 'search', payload: { keyword: 'still-live' },
  })), { list: [{ vod_id: 'still-live', vod_name: 'still-live' }] });
  denied(await rpc('invoke', {
    ...identity(retired), action: 'play', payload: {},
  }), 'SOURCE_NOT_FOUND');
  assert.ok(base.resolveVirtual(profile, 'route-1'));
  assert.equal(releases.some(value => value.prefix === 'source:retired:' && value.except === ''), false);

  denied(await module.close(), 'SOURCE_CLEANUP');
  assert.ok(releases.some(value => value.prefix === 'source:retired:' && value.except === ''));
  assert.equal(base.resolveVirtual(profile, 'route-1'), null);
});

test('failed unload profile release keeps cleanup debt without keeping a live RPC binding', async t => {
  const base = createNodeSourceCapabilities({
    network: async () => assert.fail('profile cleanup must not use the network'),
  });
  const releases = [];
  let refuseRelease = true;
  let host;
  const owner = {
    invoke: (name, input, context) => base.invoke(name, input, context),
    async releaseProfiles(prefix, except) {
      releases.push({ prefix, except });
      if (prefix === 'source:retired:' && except === '' && refuseRelease) {
        refuseRelease = false;
        throw new Error('synthetic profile cleanup failure');
      }
      await base.releaseProfiles(prefix, except);
    },
  };
  const { module, rpc } = setup(t, {
    owner,
    hostFactory: options => { host = createScriptHost(options); return host; },
  });
  const retired = await load(rpc, {
    installationId: 'retired',
    protocol: 'cat',
    source: `export default {
      async searchContent(keyword) {
        await local.set('retained', keyword);
        return { list: [{ vod_id: keyword, vod_name: keyword }] };
      }
    };`,
  });
  const healthy = await load(rpc, {
    installationId: 'healthy', protocol: 'cat', source: catSource,
  });
  okay(await rpc('invoke', {
    installationId: retired.installationId, revision: retired.revision, generation: retired.generation,
    action: 'search', payload: { keyword: 'profile-debt' },
  }));
  const stored = () => base.invoke('storage.get', {
    version: 2, profile: `source:retired:${retired.generation}:storage`, key: 'retained',
  });
  assert.equal(await stored(), 'profile-debt');
  denied(await rpc('unload', {
    installationId: retired.installationId, revision: retired.revision, generation: retired.generation,
  }), 'SOURCE_CLEANUP');
  assert.deepEqual(host.list().map(value => value.installationId), ['healthy']);
  assert.deepEqual(okay(await rpc('status')).instances, [healthy]);
  assert.equal(okay(await rpc('invoke', {
    installationId: healthy.installationId, revision: healthy.revision, generation: healthy.generation,
    action: 'search', payload: { keyword: 'still-live' },
  })).list[0].vod_name, 'still-live');
  assert.equal(releases.filter(value =>
    value.prefix === 'source:retired:' && value.except === '').length, 1);
  assert.equal(await stored(), 'profile-debt');
  okay(await module.close());
  assert.equal(releases.filter(value =>
    value.prefix === 'source:retired:' && value.except === '').length, 2);
  assert.equal(await stored(), null);
});

test('unloaded source physical work remains tracked independently of healthy RPC instances', {
  timeout: 15000,
}, async t => {
  let releaseNetwork;
  const pendingNetwork = new Promise(resolve => { releaseNetwork = resolve; });
  let entered;
  const networkEntered = new Promise(resolve => { entered = resolve; });
  const response = {
    statusCode: 200, headers: { 'content-type': 'text/plain' },
    body: '', url: 'https://books.invalid/pending',
  };
  t.after(() => releaseNetwork(response));
  const base = createNodeSourceCapabilities({
    async network(input) {
      if (input.url.endsWith('/pending')) {
        entered();
        return pendingNetwork;
      }
      assert.equal(input.url, 'https://books.invalid/entered');
      await networkEntered;
      return { ...response, url: input.url };
    },
  });
  const releases = [];
  let host, retiredSession;
  const { module, rpc } = setup(t, {
    owner: {
      invoke: (name, input, context) => base.invoke(name, input, context),
      async releaseProfiles(prefix, except) {
        releases.push({ prefix, except });
        await base.releaseProfiles(prefix, except);
      },
    },
    hostFactory: options => {
      const adapter = options.adapters.cat;
      host = createScriptHost({
        ...options,
        adapters: {
          ...options.adapters,
          cat: input => {
            const session = adapter(input);
            if (input.installationId === 'retired') retiredSession = session;
            return session;
          },
        },
      });
      return host;
    },
  });
  const retired = await load(rpc, {
    installationId: 'retired',
    protocol: 'cat',
    source: `export default {
      async searchContent() {
        const controller = new AbortController();
        const pending = fetch('https://books.invalid/pending', { signal: controller.signal })
          .then(() => 'unexpected', error => error.code);
        await fetch('https://books.invalid/entered');
        controller.abort();
        return { list: [{ vod_id: 'pending', vod_name: await pending }] };
      }
    };`,
  });
  const healthy = await load(rpc, {
    installationId: 'healthy', protocol: 'cat', source: catSource,
  });
  const identity = ({ installationId, revision, generation }) =>
    ({ installationId, revision, generation });
  assert.equal(okay(await rpc('invoke', {
    ...identity(retired), action: 'search', payload: {},
  })).list[0].vod_name, 'CANCELLED');
  assert.equal(host.stats().pendingAdapterTasks, 1);
  denied(await rpc('unload', identity(retired)), 'SOURCE_CLEANUP');
  assert.deepEqual(host.list().map(value => value.installationId), ['healthy']);
  assert.equal(host.cleanupPending('retired'), 1);
  assert.equal(host.stats().pendingAdapterTasks, 1);
  assert.deepEqual(okay(await rpc('status')).instances, [healthy]);
  assert.equal(okay(await rpc('invoke', {
    ...identity(healthy), action: 'search', payload: { keyword: 'still-live' },
  })).list[0].vod_name, 'still-live');

  releaseNetwork(response);
  await retiredSession.whenAdaptersIdle();
  assert.equal(host.cleanupPending('retired'), 0);
  assert.equal(host.stats().pendingAdapterTasks, 0);
  okay(await module.close());
  assert.ok(releases.some(value => value.prefix === 'source:retired:' && value.except === ''));
});

test('a failed candidate keeps the exact live revision and generation', async t => {
  const { rpc } = setup(t);
  const old = await load(rpc);
  const sourceRevision = await upload(rpc, 'export default {', 'broken.js', old.installationId, 2);
  const failed = await rpc('load', {
    sourceRevision,
    installationId: old.installationId,
    revision: 2,
    expectedRevision: 1,
    protocol: 'cat',
    version: '1.0.0',
    metadata: {},
  });
  assert.equal(failed.ok, false);
  assert.ok(['SOURCE_FAILURE', 'UNSUPPORTED_PROTOCOL'].includes(failed.code));
  const current = okay(await rpc('status')).instances[0];
  assert.equal(current.revision, old.revision);
  assert.equal(current.generation, old.generation);
  assert.equal(current.contentHash, old.contentHash);
  assert.equal(okay(await rpc('invoke', {
    installationId: current.installationId,
    revision: current.revision,
    generation: current.generation,
    action: 'search',
    payload: { keyword: 'still-live' },
  })).books[0].name, 'Result');
});

for (const phase of ['install', 'update']) {
  test(`HTTP abort during real source ${phase} initialization never publishes or replaces the old generation`, async t => {
    const entered = deferred();
    let host;
    const owner = createNodeSourceCapabilities({ network: input => new Promise((resolve, reject) => {
      entered.resolve(input);
      const abort = () => reject(new ScriptHostError('CANCELLED'));
      if (input.signal.aborted) abort();
      else input.signal.addEventListener('abort', abort, { once: true });
    }) });
    const { rpc } = setup(t, { owner,
      hostFactory: options => host = createScriptHost(options) });
    const old = phase === 'update' ? await load(rpc, { protocol: 'cat', source: catSource }) : null;
    const revision = old ? 2 : 1;
    const sourceRevision = await upload(rpc, {
      bookSourceName: 'Pending remote HTML', bookSourceUrl: 'https://books.invalid/',
      html: 'https://books.invalid/initialization',
    }, 'source.json', 'fixture', revision);
    const params = { installationId: 'fixture', sourceRevision, revision,
      expectedRevision: old ? 1 : 0, protocol: 'qysg' };
    const controller = new AbortController();
    const pending = rpc('load', params, { signal: controller.signal });
    const network = await entered.promise;
    controller.abort();
    denied(await pending, 'CANCELLED');
    await until(() => network.signal.aborted && rpc.transport.runtime.state.active === 0);
    assert.deepEqual(okay(await rpc('status')).instances, old ? [old] : []);
    assert.equal(host.stats().pendingAdapterTasks, 0);
    denied(await rpc('load', params), 'SOURCE_NOT_FOUND');
    if (old) {
      assert.equal(okay(await rpc('invoke', { installationId: old.installationId,
        revision: old.revision, generation: old.generation, action: 'search',
        payload: { keyword: 'still-live' } })).list[0].vod_name, 'still-live');
    } else assert.equal(host.stats().installations, 0);
  });
}

for (const [phase, operation] of [['install', 'load'], ['update', 'load'], ['update', 'unload']]) {
  test(`valid HTTP ${operation} stops the real source ${phase} initializer before its installation tail`,
    { timeout: 15000 }, async t => {
      const entered = deferred();
      let captures = 0;
      const owner = createNodeSourceCapabilities({ network: input => new Promise((_resolve, reject) => {
        entered.resolve(input);
        input.signal.addEventListener('abort', () => reject(new ScriptHostError('CANCELLED')), { once: true });
      }) });
      const { rpc } = setup(t, { owner, hostFactory: options => {
        const host = createScriptHost(options), capture = host.captureInitialization.bind(host);
        host.captureInitialization = id => { captures++; return capture(id); };
        return host;
      } });
      const b = await load(rpc, { installationId: 'b', protocol: 'cat', source: catSource });
      const old = phase === 'update' ? await load(rpc, { protocol: 'cat', source: catSource }) : null;
      const sourceRevision = await upload(rpc, {
        bookSourceName: 'Pending remote HTML', bookSourceUrl: 'https://books.invalid/',
        html: 'https://books.invalid/init-gate',
      }, 'source.json', 'fixture', old ? 2 : 1);
      const controller = new AbortController();
      const trial = rpc('load', { installationId: 'fixture', sourceRevision,
        revision: old ? 2 : 1, expectedRevision: old ? 1 : 0, protocol: 'qysg' },
      { signal: controller.signal });
      const network = await entered.promise;
      const next = { installationId: 'fixture', revision: old ? 3 : 2,
        expectedRevision: old ? 1 : 0, protocol: 'cat',
        sourceRevision: await upload(rpc, catSource, 'source.js', 'fixture', old ? 3 : 2) };
      denied(await rpc('load', { ...next, version: 1 }), 'INVALID_REQUEST');
      const before = captures;
      const stale = rpc('load', { ...next, expectedRevision: 99 });
      await until(() => captures > before);
      const staleUnload = old ? rpc('unload', { installationId: old.installationId,
        revision: old.revision, generation: old.generation + 100 }) : null;
      if (staleUnload) await until(() => rpc.transport.runtime.state.active === 3);
      assert.equal(network.signal.aborted, false, 'stale CAS must not stop or consume a candidate');
      assert.deepEqual(okay(await rpc('status')).instances, old ? [b, old] : [b]);
      const invokeB = () => rpc('invoke', { installationId: 'b', revision: b.revision,
        generation: b.generation, action: 'search', payload: { keyword: 'independent' } });
      assert.equal(okay(await invokeB()).list[0].vod_name, 'independent');
      const managing = operation === 'load' ? rpc('load', next) : rpc('unload', {
        installationId: old.installationId, revision: old.revision, generation: old.generation,
      });
      await until(() => network.signal.aborted);
      assert.equal(controller.signal.aborted, false);
      denied(await trial, 'STALE_SOURCE');
      denied(await stale, 'STALE_SOURCE');
      if (staleUnload) denied(await staleUnload, 'STALE_SOURCE');
      const result = okay(await managing);
      assert.equal(okay(await invokeB()).list[0].vod_name, 'independent');
      if (operation === 'load') {
        assert.equal(result.instance.revision, next.revision);
        controller.abort();
        assert.equal(okay(await rpc('invoke', { installationId: 'fixture', revision: next.revision,
          generation: result.instance.generation, action: 'search',
          payload: { keyword: 'successor' } })).list[0].vod_name, 'successor');
      } else assert.equal(result.cleanupComplete, true);
    });
}

test('valid HTTP unload stops the first source initializer before its installation tail',
  { timeout: 15000 }, async t => {
    const entered = deferred(), gate = deferred();
    const admitted = { staleRevision: deferred(), staleGeneration: deferred(), valid: deferred() };
    const loadController = new AbortController(), unloadController = new AbortController();
    let released = false, captures = 0, host, candidate;
    const release = () => { released = true; gate.resolve(); };
    t.after(() => { release(); loadController.abort(); unloadController.abort(); });
    const owner = createNodeSourceCapabilities({ network: async input => {
      const aborted = deferred();
      const abort = () => aborted.reject(new ScriptHostError('CANCELLED'));
      input.signal.addEventListener('abort', abort, { once: true });
      if (input.signal.aborted) abort();
      entered.resolve(input);
      try {
        await Promise.race([gate.promise, aborted.promise]);
        return { statusCode: 200, statusMessage: 'OK', headers: {}, url: input.url,
          body: '<script>function search() { return []; }</script>' };
      } finally { input.signal.removeEventListener('abort', abort); }
    } });
    const module = createSourceRuntimeModule({
      capabilityOwner: owner, platform: 'test',
      hostFactory: options => {
        const factory = options.adapters.qysg;
        host = createScriptHost({ ...options, adapters: { ...options.adapters,
          qysg: input => candidate = factory(input),
        } });
        const capture = host.captureInitialization.bind(host);
        host.captureInitialization = id => {
          const stop = capture(id);
          if (id === 'fixture') captures++;
          return stop;
        };
        return host;
      },
    });
    t.after(() => module.close());
    const unload = module.methods['source.runtime.unload'];
    module.methods['source.runtime.unload'] = (params, context) => {
      const result = unload(params, context);
      const key = params.revision !== 1 ? 'staleRevision'
        : params.generation !== undefined ? 'staleGeneration' : 'valid';
      admitted[key].resolve();
      return result;
    };
    const rpc = moduleHttp(t, module);
    const b = await load(rpc, { installationId: 'b', protocol: 'cat',
      source: 'let count = 0; export default { searchContent() { return { count: ++count }; } };' });
    const invokeB = () => rpc('invoke', { installationId: 'b', revision: b.revision,
      generation: b.generation, action: 'search', payload: {} });
    const sourceRevision = await upload(rpc, {
      bookSourceName: 'First initializer', bookSourceUrl: 'https://books.invalid/',
      html: 'https://books.invalid/init-gate',
    });
    const loading = rpc('load', { installationId: 'fixture', sourceRevision,
      revision: 1, expectedRevision: 0, protocol: 'qysg' }, { signal: loadController.signal });
    const network = await entered.promise;
    assert.ok(candidate.engine);
    const before = captures;
    const staleRevision = rpc('unload', { installationId: 'fixture', revision: 99 });
    await admitted.staleRevision.promise;
    const staleGeneration = rpc('unload', {
      installationId: 'fixture', revision: 1, generation: candidate.generation + 100,
    });
    await admitted.staleGeneration.promise;
    assert.equal(captures, before, 'stale unloads must not capture the initializer');
    assert.equal(network.signal.aborted, false);
    assert.deepEqual(okay(await rpc('status')).instances, [b]);
    const unloading = rpc('unload', { installationId: 'fixture', revision: 1 },
      { signal: unloadController.signal });
    await admitted.valid.promise;
    await until(() => network.signal.aborted);
    assert.ok(captures > before, 'the real unload handler captured the initializer');
    assert.equal(released, false, 'unload does not wait for the initialization barrier');
    assert.equal(loadController.signal.aborted, false, 'the original load request did not cancel it');
    assert.equal(candidate.lifecycle.signal.aborted, true);
    assert.equal(okay(await invokeB()).count, 1);
    await candidate.engine.exited;
    await candidate.whenAdaptersIdle();
    for (const key of ['threadCount', 'runtimeCount', 'contextCount']) {
      assert.equal(candidate.engine.stats()[key], 0, key);
    }
    assert.equal((await loading).ok, false);
    await unloading; // Cleanup, not a new final unload response-code contract.
    denied(await staleRevision, 'STALE_SOURCE');
    denied(await staleGeneration, 'STALE_SOURCE');
    assert.deepEqual(okay(await rpc('status')).instances, [b]);
    assert.equal(host.cleanupPending('fixture'), 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
    const successorRevision = await upload(rpc, catSource, 'source.js', 'fixture', 2);
    const successor = okay(await rpc('load', { installationId: 'fixture',
      sourceRevision: successorRevision, revision: 1, expectedRevision: 0, protocol: 'cat' })).instance;
    assert.ok(successor.generation > candidate.generation);
    loadController.abort();
    unloadController.abort();
    assert.equal(okay(await rpc('invoke', {
      installationId: 'fixture', revision: 1, generation: successor.generation,
      action: 'search', payload: { keyword: 'successor' },
    })).list[0].vod_name, 'successor');
    assert.equal(okay(await invokeB()).count, 2);
    okay(await module.close());
    assert.equal(host.stats().installations, 0);
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
  });

test('an HTTP unload admitted for an unpublished source cannot remove a queued same-revision successor',
  { timeout: 15000 }, async t => {
    const entered = deferred(), cleaning = deferred(), release = deferred(), admitted = deferred();
    t.after(() => release.resolve());
    let host, held = false;
    const base = createNodeSourceCapabilities({ network: input => new Promise((_resolve, reject) => {
      const abort = () => reject(new ScriptHostError('CANCELLED'));
      input.signal.addEventListener('abort', abort, { once: true });
      if (input.signal.aborted) abort();
      entered.resolve(input);
    }) });
    const module = createSourceRuntimeModule({
      platform: 'test',
      capabilityOwner: {
        invoke: (...args) => base.invoke(...args),
        async releaseProfiles(prefix, except) {
          await base.releaseProfiles(prefix, except);
          if (prefix === 'source:fixture:' && except === '' && !held) {
            held = true; cleaning.resolve(); await release.promise;
          }
        },
      },
      hostFactory: options => host = createScriptHost(options),
    });
    t.after(() => module.close());
    const unload = module.methods['source.runtime.unload'];
    module.methods['source.runtime.unload'] = (params, context) => {
      const result = unload(params, context);
      admitted.resolve();
      return result;
    };
    const rpc = moduleHttp(t, module);
    const sourceRevision = await upload(rpc, {
      bookSourceName: 'Replaced initializer', bookSourceUrl: 'https://books.invalid/',
      html: 'https://books.invalid/init-gate',
    });
    const loading = rpc('load', { installationId: 'fixture', sourceRevision,
      revision: 1, expectedRevision: 0, protocol: 'qysg' });
    const network = await entered.promise;
    const candidate = host.initializingSessions().find(session => session.installationId === 'fixture');
    const nextRevision = await upload(rpc, catSource, 'source.js', 'fixture', 2);
    const replacing = rpc('load', { installationId: 'fixture', sourceRevision: nextRevision,
      revision: 1, expectedRevision: 0, protocol: 'cat' });
    await cleaning.promise; // The real failed candidate is stopped; its transaction still owns the tail.
    assert.equal(network.signal.aborted, true);
    await candidate.engine.exited;
    assert.equal(candidate.engine.stats().threadCount, 0);
    const unloading = rpc('unload', { installationId: 'fixture', revision: 1 });
    await admitted.promise; // Original handler ran after the successor was queued, before cleanup released.
    release.resolve();
    assert.equal((await loading).ok, false);
    const successor = okay(await replacing).instance;
    denied(await unloading, 'STALE_SOURCE');
    assert.deepEqual(okay(await rpc('status')).instances, [successor]);
    assert.equal(okay(await rpc('invoke', {
      installationId: 'fixture', revision: 1, generation: successor.generation,
      action: 'search', payload: { keyword: 'still-current' },
    })).list[0].vod_name, 'still-current');
    okay(await module.close());
    assert.equal(host.stats().installations, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
  });

for (const change of ['source', 'metadata']) {
  test(`source status is read-only during ${change} update A cleanup while B remains callable`,
    { timeout: 15000 }, async t => {
      const entered = deferred(), release = deferred(), releases = [];
      t.after(() => release.resolve());
      const base = createNodeSourceCapabilities({ network: () => assert.fail('unexpected network') });
      let host;
      const { rpc } = setup(t, {
        owner: {
          invoke: (...args) => base.invoke(...args),
          async releaseProfiles(prefix, except) {
            releases.push({ prefix, except });
            await base.releaseProfiles(prefix, except);
          },
        },
        hostFactory: options => {
          const adapters = Object.fromEntries(Object.entries(options.adapters).map(([protocol, factory]) =>
            [protocol, input => {
              const session = factory(input), stop = session.stop.bind(session);
              if (input.installationId === 'a' && input.generation === 1) {
                let stopping;
                session.stop = (...args) => stopping ??= (async () => {
                  await stop(...args); entered.resolve(); await release.promise;
                })();
              }
              return session;
            }]));
          return host = createScriptHost({ ...options, adapters });
        },
      });
      const a = await load(rpc, { installationId: 'a', protocol: 'cat', source: catSource });
      const b = await load(rpc, { installationId: 'b', protocol: 'cat', source: catSource });
      const releaseOffset = releases.length;
      const sourceRevision = await upload(rpc, change === 'source'
        ? catSource.replace('vod_name: keyword', 'vod_name: "new-" + keyword') : catSource,
      'source.js', 'a', 2);
      const updating = rpc('load', { installationId: 'a', sourceRevision, revision: 2, expectedRevision: 1,
        protocol: 'cat', version: '1.0.0', metadata: { label: 'new' } });
      await entered.promise;
      assert.ok(host.get('a').generation > a.generation);
      assert.equal(host.get('a').state, 'ready');
      assert.equal(host.get('a').contentHash === a.contentHash, change === 'metadata');
      for (let i = 0; i < 2; i++) {
        assert.deepEqual(okay(await rpc('status')).instances, [{ ...a, state: 'failed' }, b]);
        assert.deepEqual(releases.slice(releaseOffset), [], 'status cannot retire or publish any profile');
      }
      assert.equal(okay(await rpc('invoke', { installationId: 'b', revision: b.revision,
        generation: b.generation, action: 'search', payload: { keyword: 'independent' } })).list[0].vod_name,
      'independent');
      assert.deepEqual(releases.slice(releaseOffset), []);
      release.resolve();
      const next = okay(await updating).instance;
      assert.deepEqual(okay(await rpc('status')).instances, [next, b]);
      assert.deepEqual(releases.slice(releaseOffset), [{
        prefix: 'source:a:', except: `source:a:${next.generation}`,
      }]);
      assert.equal(okay(await rpc('invoke', { installationId: 'a', revision: 2,
        generation: next.generation, action: 'search', payload: { keyword: 'ready' } })).list[0].vod_name,
      change === 'source' ? 'new-ready' : 'ready');
    });
}

test('post-publication cleanup failure rolls back to the old source with a fresh generation', async t => {
  const network = async input => ({
    statusCode: 200,
    statusMessage: 'OK',
    headers: { 'content-type': 'text/html' },
    body: '<div class="book"><span class="name">Stable</span><a href="/book/1">open</a></div>',
    url: input.url,
  });
  const base = createNodeSourceCapabilities({ network });
  let failNextRelease = false;
  const releases = [];
  const owner = {
    invoke: (name, input, context) => base.invoke(name, input, context),
    resolveVirtual: (profile, route) => base.resolveVirtual(profile, route),
    async releaseProfiles(prefix, except) {
      releases.push({ prefix, except });
      if (failNextRelease) {
        failNextRelease = false;
        throw new Error('synthetic cleanup failure');
      }
      await base.releaseProfiles(prefix, except);
    },
  };
  const { rpc } = setup(t, { owner });
  const old = await load(rpc);
  failNextRelease = true;
  const sourceRevision = await upload(rpc, catSource, 'candidate.js', old.installationId, 2);
  denied(await rpc('load', {
    sourceRevision,
    installationId: old.installationId,
    revision: 2,
    expectedRevision: 1,
    protocol: 'cat',
    version: '1.0.0',
    metadata: {},
  }), 'SOURCE_CLEANUP');
  const recovered = okay(await rpc('status')).instances[0];
  assert.equal(recovered.revision, 1);
  assert.equal(recovered.protocol, 'legado');
  assert.ok(recovered.generation > old.generation);
  assert.equal(okay(await rpc('invoke', {
    installationId: recovered.installationId,
    revision: recovered.revision,
    generation: recovered.generation,
    action: 'search',
    payload: { keyword: 'rollback' },
  })).books[0].name, 'Stable');
  assert.ok(releases.some(value =>
    value.except === `source:${old.installationId}:${recovered.generation}`));
});

test('profile generations are released on update, unload and shutdown', async t => {
  const network = async input => ({
    statusCode: 200,
    statusMessage: 'OK',
    headers: { 'content-type': 'text/html' },
    body: '<div class="book"><span class="name">Result</span><a href="/book/1">open</a></div>',
    url: input.url,
  });
  const base = createNodeSourceCapabilities({ network });
  const releases = [];
  const owner = {
    invoke: (name, input, context) => base.invoke(name, input, context),
    async releaseProfiles(prefix, except) {
      releases.push({ prefix, except });
      await base.releaseProfiles(prefix, except);
    },
  };
  const { module, rpc } = setup(t, { owner });
  const first = await load(rpc, { installationId: 'profiles' });
  const second = await load(rpc, {
    installationId: 'profiles',
    revision: 2,
    expectedRevision: 1,
    source: catSource,
    protocol: 'cat',
  });
  assert.ok(second.generation > first.generation);
  okay(await rpc('unload', {
    installationId: 'profiles',
    revision: 2,
    generation: second.generation,
  }));
  const closing = await load(rpc, { installationId: 'closing' });
  const closed = okay(await module.close());
  assert.deepEqual(closed, {
    closed: true,
    cleanupComplete: true,
  });
  assert.ok(releases.some(value =>
    value.prefix === 'source:profiles:' &&
    value.except === `source:profiles:${second.generation}`));
  assert.ok(releases.some(value =>
    value.prefix === 'source:profiles:' && value.except === ''));
  assert.ok(releases.some(value =>
    value.prefix === 'source:closing:' &&
    value.except === `source:closing:${closing.generation}`));
  assert.ok(releases.some(value =>
    value.prefix === 'source:closing:' && value.except === ''));
  denied(await rpc('status'), 'CLOSED');
  okay(await module.close());
});

test('HTTP validates complete source encoding, identities and cancellation without diagnostics', async t => {
  const { rpc } = setup(t);
  const html = Buffer.from('<script>function search(){ return []; }</script>');
  okay(await rpc.upload('html', 1, html, { basename: 'source.htm' }));
  denied(await rpc.upload('bad', 1, Buffer.from([0xff]), { basename: 'source.json' }), 'SOURCE_ENCODING');
  denied(await rpc.upload('html', 1, Buffer.from('{}'), { basename: 'source.htm' }), 'SOURCE_INTEGRITY');
  const instance = await load(rpc);
  denied(await rpc('invoke', {
    installationId: instance.installationId,
    revision: instance.revision + 1,
    generation: instance.generation,
    action: 'search',
    payload: { keyword: 'x' },
  }), 'STALE_SOURCE');
  const controller = new AbortController();
  controller.abort();
  denied(await rpc('status', {}, { signal: controller.signal }), 'CANCELLED');
});

function mockSourceRuntime(t) {
  const records = new Map();
  const installations = [];
  const invocations = [];
  const releases = [];
  const host = {
    list: () => [...records.values()].map(value => structuredClone(value)),
    captureInitialization: () => null,
    observe(id) {
      const record = records.get(id);
      return () => structuredClone(record);
    },
    stats: () => ({ cleanupIssues: [] }),
    cleanupPending: () => 0,
    async install(spec) {
      installations.push(structuredClone(spec));
      const record = {
        installationId: spec.installationId,
        generation: 1,
        state: 'ready',
        enabled: true,
        protocol: spec.protocol,
        version: spec.version,
        contentHash: createHash('sha256').update(spec.source).digest('hex'),
        capabilities: {
          source: {
            protocol: spec.protocol,
            importedProtocol: spec.metadata.importedProtocol,
            actions: ['search'],
          },
        },
      };
      records.set(spec.installationId, record);
      return structuredClone(record);
    },
    async update(_id, spec) { return this.install(spec); },
    async invoke(target) {
      invocations.push(structuredClone(target));
      return { keyword: target.payload.keyword };
    },
    async uninstall(id) { records.delete(id); },
    async close() {
      records.clear();
      return { cleanupComplete: true };
    },
  };
  const module = createSourceRuntimeModule({
    hostFactory: () => host,
    network: async () => { throw new Error('mock host must not execute guest network'); },
    capabilityOwner: {
      async invoke() { throw new Error('mock host must not invoke capabilities'); },
      async releaseProfiles(prefix, except) { releases.push({ prefix, except }); },
    },
    platform: 'test',
  });
  t.after(() => module.close());
  const rpc = moduleHttp(t, module);
  return { module, rpc, records, installations, invocations, releases };
}

test('R5 source HTTP accepts exactly 16 MiB and rejects the next byte', async t => {
  const { rpc } = mockSourceRuntime(t);
  const bytes = Buffer.alloc(16 * 1024 * 1024, 0x61);
  assert.equal(okay(await rpc.upload('large', 1, bytes, { basename: 'source.js' })).sourceBytes, bytes.length);
  denied(await rpc.upload('large', 2, Buffer.concat([bytes, Buffer.from('a')]), { basename: 'source.js' }), 'LIMIT');
});

test('R5 source HTTP retains more than four independent complete candidates', async t => {
  const { rpc, installations } = mockSourceRuntime(t);
  for (let i = 0; i < 6; i++) await upload(rpc, catSource, 'source.js', `upload-${i}`);
  for (let i = 0; i < 6; i++) okay(await rpc('load', {
    installationId: `upload-${i}`, sourceRevision: 1, revision: 1, expectedRevision: 0, protocol: 'cat',
  }));
  assert.equal(installations.length, 6);
});

test('R5 source module installs more than 32 instances', async t => {
  const { rpc, records, installations } = mockSourceRuntime(t);
  const ids = Array.from({ length: 33 }, (_value, index) => `source-${index}`);
  for (const installationId of ids) {
    const instance = await load(rpc, { installationId, protocol: 'cat', source: catSource });
    assert.equal(instance.installationId, installationId);
    assert.equal(instance.state, 'ready');
  }
  assert.equal(records.size, 33);
  assert.equal(installations.length, 33);
  assert.deepEqual(okay(await rpc('status')).instances.map(value => value.installationId).sort(),
    [...ids].sort());
});

test('source status reads a same-revision restart without publishing or retiring profiles; invocation reconciles it', async t => {
  const { rpc, records, invocations, releases } = mockSourceRuntime(t);
  const first = await load(rpc, { protocol: 'cat', source: catSource });
  const nextGeneration = first.generation + 1;
  records.set(first.installationId, {
    ...records.get(first.installationId), generation: nextGeneration,
  });
  const releaseOffset = releases.length;
  const current = okay(await rpc('status')).instances[0];
  assert.deepEqual(current, { ...first, generation: nextGeneration });
  assert.deepEqual(releases.slice(releaseOffset), []);
  assert.deepEqual(okay(await rpc('status')).instances, [current]);
  assert.deepEqual(releases.slice(releaseOffset), []);
  const params = {
    installationId: first.installationId,
    revision: first.revision,
    generation: first.generation,
    action: 'search',
    payload: { keyword: 'current' },
  };
  denied(await rpc('invoke', params), 'STALE_SOURCE');
  assert.equal(invocations.length, 0);
  assert.deepEqual(okay(await rpc('invoke', {
    ...params, generation: nextGeneration,
  })), { keyword: 'current' });
  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].generation, nextGeneration);
  assert.equal(releases.length, releaseOffset + 1);
  assert.deepEqual(releases[releaseOffset], {
    prefix: `source:${first.installationId}:`,
    except: `source:${first.installationId}:${nextGeneration}`,
  });
});

for (const method of ['load', 'invoke', 'unload']) {
  test(`source ${method} refreshes a same-revision restart before checking the generation`, async t => {
    const { rpc, records, installations, invocations, releases } = mockSourceRuntime(t);
    const first = await load(rpc, { protocol: 'cat', source: catSource });
    const nextGeneration = first.generation + 1;
    records.set(first.installationId, {
      ...records.get(first.installationId), generation: nextGeneration,
    });
    const releaseOffset = releases.length;
    if (method === 'load') {
      const current = await load(rpc, {
        protocol: 'cat', source: catSource, revision: first.revision, expectedRevision: first.revision,
      });
      assert.deepEqual(current, { ...first, generation: nextGeneration });
      assert.equal(installations.length, 1, 'a same-revision reload must not reinstall the worker');
    } else if (method === 'invoke') {
      assert.deepEqual(okay(await rpc('invoke', {
        installationId: first.installationId,
        revision: first.revision,
        generation: nextGeneration,
        action: 'search',
        payload: { keyword: 'direct' },
      })), { keyword: 'direct' });
      assert.equal(invocations.length, 1);
      assert.equal(invocations[0].generation, nextGeneration);
    } else {
      const identity = { installationId: first.installationId, revision: first.revision };
      denied(await rpc('unload', { ...identity, generation: first.generation }), 'STALE_SOURCE');
      assert.equal(records.size, 1, 'the stale unload must leave the new generation installed');
      assert.equal(okay(await rpc('unload', { ...identity, generation: nextGeneration })).unloaded, true);
      assert.equal(records.size, 0);
    }
    assert.deepEqual(releases[releaseOffset], {
      prefix: `source:${first.installationId}:`,
      except: `source:${first.installationId}:${nextGeneration}`,
    });
    assert.deepEqual(releases.slice(releaseOffset + 1), method === 'unload' ? [{
      prefix: `source:${first.installationId}:`, except: '',
    }] : []);
  });
}

for (const [field, value] of [
  ['contentHash', '0'.repeat(64)],
  ['version', '2.0.0'],
  ['protocol', 'qysg'],
]) {
  test(`source entry points reject a restarted generation with a different ${field} as SOURCE_CLEANUP`, async t => {
    const { rpc, records, installations, invocations, releases } = mockSourceRuntime(t);
    const first = await load(rpc, { protocol: 'cat', source: catSource });
    records.set(first.installationId, {
      ...records.get(first.installationId), generation: first.generation + 1, [field]: value,
    });
    const releaseOffset = releases.length;
    const identity = {
      installationId: first.installationId,
      revision: first.revision,
      generation: first.generation,
    };
    denied(await rpc('status'), 'SOURCE_CLEANUP');
    denied(await rpc('invoke', {
      ...identity, action: 'search', payload: { keyword: 'mismatched' },
    }), 'SOURCE_CLEANUP');
    denied(await rpc('unload', identity), 'SOURCE_CLEANUP');
    denied(await rpc('load', {
      sourceRevision: await upload(rpc, catSource, 'source.js'),
      installationId: first.installationId,
      revision: first.revision,
      expectedRevision: first.revision,
      protocol: 'cat',
      version: '1.0.0',
      metadata: {},
    }), 'SOURCE_CLEANUP');
    assert.equal(records.size, 1);
    assert.equal(installations.length, 1);
    assert.equal(invocations.length, 0);
    assert.equal(releases.length, releaseOffset);
  });
}

test('source CAS and field rejection retain a complete candidate; exact revisions never follow the latest', async t => {
  const { rpc, installations } = mockSourceRuntime(t);
  await load(rpc, { protocol: 'cat', source: catSource });
  await upload(rpc, catSource, 'source.js', 'fixture', 5);
  const next = { installationId: 'fixture', sourceRevision: 5, revision: 5, expectedRevision: 1, protocol: 'cat' };
  denied(await rpc('load', { ...next, expectedRevision: 4 }), 'STALE_SOURCE');
  denied(await rpc('load', { ...next, contentHash: '0'.repeat(64) }), 'INVALID_REQUEST');
  denied(await rpc('load', { ...next, metadata: { text: 'x'.repeat(32768) } }), 'LIMIT');
  assert.equal(installations.length, 1);
  // The deterministic owner supports update through the same installation surface.
  const uploaded = await rpc.upload('another', 1, Buffer.from(catSource), { basename: 'source.js' });
  assert.equal(uploaded.ok, true);
  denied(await rpc.upload('another', 1, Buffer.from('different'), { basename: 'source.js' }), 'SOURCE_INTEGRITY');
  await upload(rpc, catSource, 'source.js', 'another', 2);
  denied(await rpc('load', { installationId: 'another', sourceRevision: 1, revision: 1, expectedRevision: 0 }), 'SOURCE_NOT_FOUND');
  const installed = okay(await rpc('load', { installationId: 'another', sourceRevision: 2, revision: 2, expectedRevision: 0, protocol: 'cat' })).instance;
  assert.equal(installed.revision, 2);
  assert.equal(okay(await rpc('load', next)).instance.revision, 5);
});

test('source equal revisions retain canonical definition and metadata identity without restarting', async t => {
  const { rpc, installations } = mockSourceRuntime(t);
  const first = await load(rpc);
  const reordered = Object.fromEntries(Object.entries(fixtureSource).reverse());
  reordered.ruleSearch = Object.fromEntries(Object.entries(reordered.ruleSearch).reverse());
  await upload(rpc, reordered, 'source.json', 'fixture', 2);
  const equal = okay(await rpc('load', { installationId: 'fixture', sourceRevision: 2,
    revision: 1, expectedRevision: 1, protocol: 'legado', metadata: {} })).instance;
  assert.deepEqual(equal, first);
  assert.equal(installations.length, 1);
});
