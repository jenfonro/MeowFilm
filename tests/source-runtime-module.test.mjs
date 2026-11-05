import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../apps/runtime/modules/source-runtime.mjs';
import { createNodeSourceCapabilities, createScriptHost } from '@meowfilm/script-host';

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
  const rpc = (name, params = {}, context) =>
    module.methods[`source.runtime.${name}`](params, context);
  return { module, rpc, owner };
}

function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, sourceRuntimeContract);
  return reply;
}

function denied(reply, code) {
  assert.equal(reply.ok, false, JSON.stringify(reply));
  assert.equal(reply.contract, sourceRuntimeContract);
  assert.equal(reply.code, code);
}

async function upload(rpc, value, basename = 'source.json') {
  const bytes = Buffer.from(typeof value === 'string' ? value : `${JSON.stringify(value)}\n`);
  const begun = okay(await rpc('stage.begin', {
    basename,
    length: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  }));
  for (let offset = 0; offset < bytes.length; offset += begun.chunkBytes) {
    okay(await rpc('stage.chunk', {
      token: begun.token,
      offset,
      data: bytes.subarray(offset, offset + begun.chunkBytes).toString('base64'),
    }));
  }
  return begun.token;
}

async function load(rpc, {
  installationId = 'fixture',
  revision = 1,
  expectedRevision = revision - 1,
  source = fixtureSource,
  protocol = 'legado',
  basename = protocol === 'cat' ? 'source.js' : 'source.json',
} = {}) {
  const token = await upload(rpc, source, basename);
  return okay(await rpc('load', {
    token,
    installationId,
    revision,
    expectedRevision,
    protocol,
    version: '1.0.0',
    metadata: {},
  })).instance;
}

test('source RPC stages, loads, invokes, reports and unloads a source instance', async t => {
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
  })).value;
  assert.equal(result.books[0].name, 'Result');
  assert.equal(result.books[0].bookUrl, 'https://books.invalid/book/1');
  assert.deepEqual(okay(await rpc('status')).instances, [instance]);
  assert.deepEqual(okay(await rpc('unload', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
  })), {
    ok: true,
    contract: sourceRuntimeContract,
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
    const token = await upload(rpc, source, basename);
    const instance = okay(await rpc('load', {
      token,
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
    token: await upload(rpc, fixtureSource),
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
  })).value, { list: [{ vod_id: 'still-live', vod_name: 'still-live' }] });
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
    version: 1, profile: `source:retired:${retired.generation}:storage`, key: 'retained',
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
  })).value.list[0].vod_name, 'still-live');
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
  })).value.list[0].vod_name, 'CANCELLED');
  assert.equal(host.stats().pendingAdapterTasks, 1);
  denied(await rpc('unload', identity(retired)), 'SOURCE_CLEANUP');
  assert.deepEqual(host.list().map(value => value.installationId), ['healthy']);
  assert.equal(host.cleanupPending('retired'), 1);
  assert.equal(host.stats().pendingAdapterTasks, 1);
  assert.deepEqual(okay(await rpc('status')).instances, [healthy]);
  assert.equal(okay(await rpc('invoke', {
    ...identity(healthy), action: 'search', payload: { keyword: 'still-live' },
  })).value.list[0].vod_name, 'still-live');

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
  const token = await upload(rpc, 'export default {', 'broken.js');
  const failed = await rpc('load', {
    token,
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
  })).value.books[0].name, 'Result');
});

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
  const token = await upload(rpc, catSource, 'candidate.js');
  denied(await rpc('load', {
    token,
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
  })).value.books[0].name, 'Stable');
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
    ok: true,
    contract: sourceRuntimeContract,
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

test('RPC validates staged integrity, stale identities and cancellation without exposing diagnostics', async t => {
  const { rpc } = setup(t);
  const html = Buffer.from('<script>function search(){ return []; }</script>');
  const htmlStage = okay(await rpc('stage.begin', {
    basename: 'source.htm',
    length: html.length,
    sha256: createHash('sha256').update(html).digest('hex'),
  }));
  okay(await rpc('stage.abort', { token: htmlStage.token }));
  const bytes = Buffer.from('{}');
  const begun = okay(await rpc('stage.begin', {
    basename: 'source.json',
    length: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  }));
  denied(await rpc('stage.chunk', {
    token: begun.token,
    offset: 1,
    data: bytes.toString('base64'),
  }), 'SOURCE_INTEGRITY');
  okay(await rpc('stage.abort', { token: begun.token }));

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

function mockSourceRuntime(t, { clock, setTimer, clearTimer } = {}) {
  const records = new Map();
  const installations = [];
  const invocations = [];
  const releases = [];
  const host = {
    list: () => [...records.values()].map(value => structuredClone(value)),
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
    clock, setTimer, clearTimer,
  });
  t.after(() => module.close());
  const rpc = (name, params = {}) => module.methods[`source.runtime.${name}`](params);
  return { module, rpc, records, installations, invocations, releases };
}

test('R5 source staging accepts exactly 16 MiB and rejects the next byte', async t => {
  const { rpc } = mockSourceRuntime(t);
  const params = { basename: 'source.js', length: 16 * 1024 * 1024, sha256: '0'.repeat(64) };
  const stage = okay(await rpc('stage.begin', params));
  assert.equal(stage.chunkBytes, 64 * 1024);
  denied(await rpc('stage.begin', { ...params, length: params.length + 1 }), 'LIMIT');
  okay(await rpc('stage.abort', { token: stage.token }));
});

test('R5 source staging retains more than four simultaneous uploads', async t => {
  const { rpc } = mockSourceRuntime(t);
  const bytes = Buffer.from('{}');
  const params = {
    basename: 'source.json',
    length: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  };
  const stages = [];
  for (let index = 0; index < 6; index++) {
    stages.push(okay(await rpc('stage.begin', params)).token);
  }
  assert.equal(new Set(stages).size, 6);
  for (const token of stages) {
    assert.equal(okay(await rpc('stage.chunk', {
      token, offset: 0, data: bytes.toString('base64'),
    })).next, bytes.length);
    okay(await rpc('stage.abort', { token }));
  }
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

test('R5 staged uploads share one active TTL timer, renew on valid chunks and cancel it on close', async t => {
  let now = 0;
  const timers = new Map();
  const { module, rpc } = mockSourceRuntime(t, {
    clock: () => now,
    setTimer(callback, delay) {
      const handle = { unref() {} };
      timers.set(handle, { callback, due: now + delay });
      return handle;
    },
    clearTimer(handle) { timers.delete(handle); },
  });
  const fireDue = () => {
    for (const [handle, timer] of [...timers]) {
      if (timer.due <= now) {
        timers.delete(handle);
        timer.callback();
      }
    }
  };
  const bytes = Buffer.from('{}');
  const params = {
    basename: 'source.json',
    length: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  };
  const first = okay(await rpc('stage.begin', params)).token;
  const second = okay(await rpc('stage.begin', params)).token;
  assert.equal(timers.size, 1);
  assert.equal([...timers.values()][0].due, 300000);
  now = 299999;
  assert.equal(okay(await rpc('stage.chunk', {
    token: first, offset: 0, data: Buffer.from('{').toString('base64'),
  })).next, 1);
  assert.equal(timers.size, 1);
  now = 300000;
  fireDue();
  assert.equal(timers.size, 1, 'only the renewed upload should retain an expiry timer');
  denied(await rpc('stage.chunk', {
    token: second, offset: 0, data: bytes.toString('base64'),
  }), 'STAGE_NOT_FOUND');
  assert.equal(okay(await rpc('stage.chunk', {
    token: first, offset: 1, data: Buffer.from('}').toString('base64'),
  })).next, 2);
  assert.equal(timers.size, 1);
  now = 600000;
  fireDue();
  assert.equal(timers.size, 0, 'expiry must release the final stage without another RPC');
  denied(await rpc('stage.chunk', {
    token: first, offset: 0, data: bytes.toString('base64'),
  }), 'STAGE_NOT_FOUND');
  okay(await rpc('stage.begin', params));
  assert.equal(timers.size, 1);
  okay(await module.close());
  assert.equal(timers.size, 0);
  denied(await rpc('stage.begin', params), 'CLOSED');
});

test('source status adopts a same-revision restart, rejects the old generation and keeps only the new profiles', async t => {
  const { rpc, records, invocations, releases } = mockSourceRuntime(t);
  const first = await load(rpc, { protocol: 'cat', source: catSource });
  const nextGeneration = first.generation + 1;
  records.set(first.installationId, {
    ...records.get(first.installationId), generation: nextGeneration,
  });
  const releaseOffset = releases.length;
  const current = okay(await rpc('status')).instances[0];
  assert.deepEqual(current, { ...first, generation: nextGeneration });
  assert.deepEqual(releases.slice(releaseOffset), [{
    prefix: `source:${first.installationId}:`,
    except: `source:${first.installationId}:${nextGeneration}`,
  }]);
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
  })).value, { keyword: 'current' });
  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].generation, nextGeneration);
  assert.equal(releases.length, releaseOffset + 1);
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
      })).value, { keyword: 'direct' });
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
      token: await upload(rpc, catSource, 'source.js'),
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
