import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../apps/runtime/modules/source-runtime.mjs';
import { createNodeSourceCapabilities } from '@meowfilm/script-host';

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

function setup(t, { owner: suppliedOwner } = {}) {
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
