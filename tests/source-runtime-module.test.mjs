import test from 'node:test';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred, until } from '../packages/runtime-bridge/test/http-fixture.js';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../apps/runtime/modules/source-runtime.mjs';
import { createNodeSourceCapabilities, createScriptHost, ScriptHostError } from '@meowfilm/script-host';

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
    call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
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
    call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
    network: async () => ({}),
    capabilityOwner: { invoke() {} },
  }), { code: 'INVALID_REQUEST' });
});

test('source close reports host shutdown failures as cleanup failures', async () => {
  const module = createSourceRuntimeModule({
    call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
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
      call: async () => assert.fail('close must not make outbound RPC calls'),
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
    call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
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
