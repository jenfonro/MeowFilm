import { withTestDatabase } from './storage-test-fixture.mjs';
import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { ScriptHostError } from '@meowfilm/script-host';
import { encodeValueNodes } from '@meowfilm/storage/values';
import { parseJSONText } from '@meowfilm/runtime-bridge/json';
import { nodeInserts } from '@meowfilm/storage/statements';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred } from '../packages/runtime-bridge/test/http-fixture.js';
import { serverFor } from '../packages/script-host/test/http-fixture.js';

const source = {
  bookSourceName: 'Books', bookSourceUrl: 'https://books.invalid/', bookSourceGroup: 'test',
  enabledExplore: true, exploreUrl: 'All::https://books.invalid/explore',
  searchUrl: 'https://books.invalid/search?key={{key}}',
  header: { Referer: 'https://books.invalid/' },
  ruleSearch: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
  ruleBookInfo: { name: 'h1@text', tocUrl: '.toc@href' },
  ruleToc: { chapterList: 'li', chapterName: 'a@text', chapterUrl: 'a@href' },
  ruleContent: { title: 'h1@text', content: 'article@html' },
};
const html = '<div class="book"><h2>Result</h2><a href="/book">open</a></div>';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aCfoAAAAASUVORK5CYII=', 'base64');
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, 'source.runtime.v2');
  return reply.value;
}
function denied(reply, code) {
  assert.deepEqual(reply, { ok: false, contract: 'source.runtime.v2', code });
}
const results = new URL('../tmp/test-results/source-library/', import.meta.url);
mkdirSync(results, { recursive: true });
const suiteRoot = mkdtempSync(join(fileURLToPath(results), 'suite-'));
after(() => rmSync(suiteRoot, { recursive: true, force: true }));
function temp() {
  return mkdtempSync(join(suiteRoot, 'case-'));
}
function setup(t, options = {}) {
  const calls = [];
  const network = async input => {
    calls.push(input);
    return options.respond ? options.respond(input) : {
      statusCode: 200, headers: {}, body: html, url: input.url,
    };
  };
  const module = createSourceRuntimeModule({ ...options, network, platform: 'fixture' });
  const rpc = moduleHttp(t, module);
  return { module, rpc, calls };
}
async function importSource(rpc, definition = source) {
  const imported = value(await rpc('library.import', { text: JSON.stringify(definition) }));
  assert.deepEqual(imported.failures, []);
  assert.equal(imported.sources.length, 1);
  return imported.sources[0];
}
const target = item => ({ installationId: item.installationId, revision: item.revision });
const invoke = (rpc, item, action, payload = {}, options) =>
  rpc('library.invoke', { ...target(item), action, payload }, options);
async function eventually(predicate) {
  for (let count = 0; count < 1000; count++) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail('expected public state did not arrive');
}
async function pending(rpc) {
  let requests;
  await eventually(async () => {
    requests = value(await rpc('web.pending')).requests;
    return requests.length > 0;
  });
  return requests[0];
}

test('library manages batch imports, origin updates, revisions, edits, ordering and exports', async t => {
  const { rpc } = setup(t, { dataRoot: temp() });
  const imported = value(await rpc('library.import', { text: JSON.stringify([
    source, { unknown: true }, { ...source, bookSourceName: 'Other', bookSourceUrl: 'https://other.invalid/' },
  ]), fileName: 'books.json' }));
  assert.equal(imported.sources.length, 2);
  assert.deepEqual(imported.failures, [{ index: 1, name: '', code: 'UNSUPPORTED_PROTOCOL' }]);
  let item = imported.sources[0];
  assert.equal(item.protocol, 'legado');
  assert.equal(item.enabledExplore, true);
  assert.ok(item.actions.includes('content'));
  assert.equal(Object.hasOwn(item, 'generation'), false);
  const updated = await importSource(rpc, { ...source, bookSourceName: 'New Books' });
  assert.equal(updated.installationId, item.installationId);
  assert.equal(updated.revision, item.revision + 1);
  denied(await rpc('library.update', { installationId: item.installationId, expectedRevision: item.revision,
    patch: { enabled: false } }), 'STALE_SOURCE');
  item = value(await rpc('library.update', { installationId: updated.installationId,
    expectedRevision: updated.revision, patch: { name: 'Renamed', group: 'Favorites', order: -1,
      enabledExplore: false } })).source;
  assert.equal(value(await rpc('library.list')).sources[0].name, 'Renamed');
  assert.equal(value(await rpc('library.get', { installationId: item.installationId })).source.group, 'Favorites');
  const editable = JSON.parse(value(await rpc('library.get', { installationId: item.installationId })).definition);
  assert.equal(editable.bookSourceName, 'Renamed');
  assert.equal(editable.bookSourceGroup, 'Favorites');
  assert.equal(editable.enabledExplore, false);
  assert.equal(editable.customOrder, -1);
  assert.deepEqual(editable.ruleSearch, source.ruleSearch);
  const exported = JSON.parse(value(await rpc('library.export', { installationIds: [item.installationId] })).text);
  assert.equal(exported[0].bookSourceName, 'Renamed');
  assert.equal(exported[0].bookSourceGroup, 'Favorites');
  assert.equal(exported[0].enabledExplore, false);
  denied(await rpc('library.save', { installationId: item.installationId,
    expectedRevision: item.revision, definition: '{"invalid":true}' }), 'UNSUPPORTED_PROTOCOL');
  assert.equal(value(await invoke(rpc, item, 'search', { keyword: 'still usable' })).books[0].name, 'Result');
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: false } })).source;
  assert.equal(item.enabled, false);
  denied(await invoke(rpc, item, 'search', { keyword: 'disabled' }), 'SOURCE_DISABLED');
  assert.equal(value(await rpc('library.list')).sources.length, 2);
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: true } })).source;
  assert.equal(value(await invoke(rpc, item, 'search', { keyword: 'enabled' })).books.length, 1);
  value(await rpc('library.remove', { installationId: item.installationId, expectedRevision: item.revision }));
  denied(await rpc('library.get', { installationId: item.installationId }), 'SOURCE_NOT_FOUND');
});

test('URL and BOM file imports preserve batch failures and export managed Legado and HTML sources', async t => {
  const { rpc, calls } = setup(t, { respond: input => ({ statusCode: 200, headers: {},
    url: input.url, body: '\uFEFF' + JSON.stringify([source, { invalid: true }]) }) });
  const imported = value(await rpc('library.import', { url: 'https://sources.invalid/library.json' }));
  assert.equal(calls[0].url, 'https://sources.invalid/library.json');
  assert.equal(imported.sources.length, 1);
  assert.deepEqual(imported.failures, [{ index: 1, name: '', code: 'UNSUPPORTED_PROTOCOL' }]);
  let item = imported.sources[0];
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: false, enabledExplore: false,
      order: -7, group: 'Managed metadata' } })).source;
  const exported = value(await rpc('library.export', { installationIds: [item.installationId] })).text;
  const reimported = value(await rpc('library.import', { text: '\uFEFF' + exported })).sources[0];
  assert.equal(reimported.installationId, item.installationId);
  assert.equal(reimported.protocol, 'legado');
  assert.equal(reimported.enabled, false);
  assert.equal(reimported.enabledExplore, false);
  assert.equal(reimported.order, -7);
  assert.equal(reimported.group, 'Managed metadata');
  const html = value(await rpc('library.import', { fileName: 'standalone.html',
    text: '<script>function search(){return [];}</script>' })).sources[0];
  const htmlExport = JSON.parse(value(await rpc('library.export', {
    installationIds: [html.installationId],
  })).text)[0];
  assert.equal(htmlExport.bookSourceName, 'standalone.html');
  assert.equal(htmlExport.bookSourceUrl, html.sourceUrl);
  assert.match(htmlExport.html, /function search/);
  const restoredHtml = value(await rpc('library.import', { text: JSON.stringify([htmlExport]) })).sources[0];
  assert.equal(restoredHtml.installationId, html.installationId);
});

test('ordinary RPC extras are inert and management patches cannot replace source-owned fields', async t => {
  const { rpc, calls } = setup(t);
  const current = { ...source };
  delete current.enabledExplore;
  delete current.exploreUrl;
  const extras = { enable: false, serialNumber: -90, ruleFindUrl: 'All::https://unused.invalid/' };
  let item = await importSource(rpc, { ...current, ...extras });
  assert.equal(item.enabled, true);
  assert.equal(item.order, 0);
  assert.equal(item.enabledExplore, false);
  assert.equal(item.hasExplore, false);
  const irrelevant = { revision: { ignored: true }, expectedRevision: ['unused'], extra: { deep: [1] } };
  assert.deepEqual(value(await rpc('library.list', { ...irrelevant, installationId: false })).sources, [item]);
  const before = value(await rpc('library.get', { installationId: item.installationId, ...irrelevant }));
  item = value(await rpc('library.update', {
    installationId: item.installationId, expectedRevision: item.revision,
    revision: false, extra: ['unused'], patch: {
      name: 'Renamed', order: 4, enabledExplore: true, extra: { deep: false },
      installationId: 'injected', revision: 99, sourceUrl: 'https://unused.invalid/',
      protocol: 'qysg', definition: '<script>throw 1</script>', actions: [],
      private: { cookies: [['injected', []]], storage: [] },
    },
  })).source;
  assert.equal(item.installationId, before.source.installationId);
  assert.equal(item.sourceUrl, before.source.sourceUrl);
  assert.equal(item.protocol, 'legado');
  assert.deepEqual(item.actions, before.source.actions);
  assert.equal(item.name, 'Renamed');
  assert.equal(item.order, 4);
  const managed = JSON.parse(value(await rpc('library.get', {
    installationId: item.installationId, ...irrelevant,
  })).definition);
  for (const [key, value] of Object.entries(extras)) assert.deepEqual(managed[key], value);
  assert.equal(managed.enabled, true);
  assert.equal(managed.enabledExplore, true);
  assert.equal(managed.customOrder, 4);
  assert.deepEqual(managed.ruleSearch, source.ruleSearch);
  assert.equal(value(await invoke(rpc, item, 'search', { keyword: 'unchanged' })).books[0].name, 'Result');
  assert.equal(calls.at(-1).url, 'https://books.invalid/search?key=unchanged');
  assert.equal(calls.at(-1).options.headers.cookie, undefined);
  const exported = JSON.parse(value(await rpc('library.export', {
    installationIds: [item.installationId], installationId: { unused: true }, ...irrelevant,
  })).text)[0];
  for (const [key, value] of Object.entries(extras)) assert.deepEqual(exported[key], value);
});

test('failed private writes roll back all Reader keys, the variable and the revision', async t => {
  const dataRoot = temp(), { rpc } = setup(t, { dataRoot });
  const item = await importSource(rpc, { ...source, coverDecodeJs: `
    if (result !== '') {
      source.put('first', result);
      source.put('second', result);
      source.setVariable(result);
    }
    [source.get('first'), source.get('second'), source.getVariable()].join('|')`,
  });
  const read = body => invoke(rpc, item, 'image', { body });
  assert.equal(value(await read('accepted')).body, 'accepted|accepted|accepted');
  await withTestDatabase(dataRoot, async db => {
    const facts = () => db.snapshot([
      { sql: 'SELECT revision,variable FROM source_reader_state WHERE installation_id=?', params: [item.installationId], rows: true },
      { sql: 'SELECT * FROM source_reader_value_nodes WHERE installation_id=? ORDER BY entry_key,node_id', params: [item.installationId], rows: true },
    ]);
    const committed = await facts();
    assert.equal(committed[0][0].revision, 1);
    assert.equal(committed[1].length, 2);
    const second = Buffer.from('second', 'utf16le').toString('hex');
    await db.execute(`CREATE TEMP TRIGGER reject_second_reader_key BEFORE INSERT ON source_reader_value_nodes
      WHEN NEW.entry_key=x'${second}' BEGIN SELECT RAISE(ABORT,'rejected'); END`);
    denied(await read('rejected'), 'STORAGE_ERROR');
    assert.deepEqual(await facts(), committed);
    assert.equal(value(await read('')).body, 'accepted|accepted|accepted');
    await db.execute('DROP TRIGGER reject_second_reader_key');
    assert.equal(value(await read('recovered')).body, 'recovered|recovered|recovered');
    assert.equal((await facts())[0][0].revision, 2);
  });
});

test('Web login, dynamic request and cancellation bind responses and cookies to the exact source', async t => {
  const { rpc, calls } = setup(t, { dataRoot: temp() });
  const first = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login',
    searchUrl: 'https://books.invalid/search,{"webView":true}' });
  const second = await importSource(rpc, { ...source, bookSourceName: 'Independent',
    bookSourceUrl: 'https://independent.invalid/' });
  const login = invoke(rpc, first, 'login');
  const request = await pending(rpc);
  assert.equal(request.installationId, first.installationId);
  assert.equal(request.mode, 'login');
  assert.equal(Object.hasOwn(request, 'generation'), false);
  value(await rpc('web.respond', { id: request.id, extra: { ignored: true }, installationId: false, result: {
    url: 'https://books.invalid/login', headers: { 'set-cookie': ['fixture=private; Path=/; Secure'] },
    data: 'logged in',
  } }));
  value(await login);
  const search = invoke(rpc, first, 'search', { keyword: 'dynamic' });
  const page = await pending(rpc);
  assert.equal(page.mode, 'request');
  assert.equal(page.request.headers.Cookie, 'fixture=private');
  assert.equal(page.request.headers.Referer, 'https://books.invalid/');
  value(await rpc('web.respond', { id: page.id, result: { url: 'https://books.invalid/search', data: html } }));
  assert.equal(value(await search).books[0].name, 'Result');
  value(await invoke(rpc, second, 'search', { keyword: 'independent' }));
  assert.equal(calls.at(-1).options.headers.cookie, undefined);
  const cancelled = invoke(rpc, first, 'login');
  const cancelPage = await pending(rpc);
  value(await rpc('web.respond', { id: cancelPage.id, cancelled: true }));
  denied(await cancelled, 'CANCELLED');
  denied(await rpc('web.respond', { id: cancelPage.id, result: 'late' }), 'STALE_SOURCE');
  const controller = new AbortController();
  const aborted = invoke(rpc, first, 'login', {}, { signal: controller.signal });
  const abortedPage = await pending(rpc);
  controller.abort();
  denied(await aborted, 'CANCELLED');
  await eventually(async () => value(await rpc('web.pending')).requests.length === 0);
  denied(await rpc('web.respond', { id: abortedPage.id, result: 'late' }), 'STALE_SOURCE');
});

test('disable, remove and close revoke Web requests and prevent cookie writeback', async t => {
  for (const action of ['disable', 'remove', 'close']) {
    const { rpc, module } = setup(t, { dataRoot: temp() });
    let item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login' });
    const running = invoke(rpc, item, 'login');
    const request = await pending(rpc);
    if (action === 'disable') item = value(await rpc('library.update', {
      installationId: item.installationId, expectedRevision: item.revision, patch: { enabled: false },
    })).source;
    else if (action === 'remove') value(await rpc('library.remove', {
      installationId: item.installationId, expectedRevision: item.revision,
    }));
    else value(await module.close());
    assert.equal((await running).ok, false);
    if (action !== 'close') {
      assert.deepEqual(value(await rpc('web.pending')), { requests: [] });
      denied(await rpc('web.respond', { id: request.id, result: { url: source.bookSourceUrl,
        headers: { 'set-cookie': ['late=discarded; Path=/'] } } }), 'STALE_SOURCE');
    }
    if (action === 'disable') {
      item = value(await rpc('library.update', { installationId: item.installationId,
        expectedRevision: item.revision, patch: { enabled: true } })).source;
      assert.equal(value(await invoke(rpc, item, 'loginStatus')).loggedIn, false);
    }
  }
});

test('a remote URL match cannot revive a cancelled or retired Web request or write its Cookies', async t => {
  for (const ending of ['cancel', 'disable']) {
    const { rpc, module, calls } = setup(t, { dataRoot: temp() });
    let item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login' });
    const controller = new AbortController();
    const login = invoke(rpc, item, 'login', {}, { signal: controller.signal });
    const page = await pending(rpc);
    const matching = module.methods['source.runtime.rules.process']({ request: {
      operation: 'matchUrl', pattern: 'https://books\\.invalid/login', url: 'https://books.invalid/login',
    } });
    if (ending === 'cancel') controller.abort();
    else item = value(await rpc('library.update', { installationId: item.installationId,
      expectedRevision: item.revision, patch: { enabled: false } })).source;
    assert.equal(value(await matching), true, 'the independent pure computation can finish after Web revocation');
    assert.equal((await login).ok, false);
    await eventually(async () => value(await rpc('web.pending')).requests.length === 0);
    denied(await rpc('web.respond', { id: page.id, result: { value: true,
      url: 'https://books.invalid/login', headers: {},
      cookieChanges: [{ url: 'https://books.invalid/login', setCookie: 'late=discarded; Path=/; Secure' }],
    } }), 'STALE_SOURCE');
    if (ending === 'disable') item = value(await rpc('library.update', {
      installationId: item.installationId, expectedRevision: item.revision, patch: { enabled: true },
    })).source;
    value(await invoke(rpc, item, 'search', { keyword: 'no late cookie' }));
    assert.equal(calls.at(-1).options.headers.cookie, undefined);
  }
});

test('Cookie envelopes unwrap scalar verification, rule, webview and QYSG results', async t => {
  const { rpc } = setup(t);
  const item = await importSource(rpc, { ...source, coverDecodeJs: `
    const code = java.getVerificationCode('https://books.invalid/code', {});
    const result = java.webView('', 'https://books.invalid/page', 'document.title', 0);
    code + ':' + result`,
  });
  const verification = invoke(rpc, item, 'image', { body: '' });
  const code = await pending(rpc);
  assert.equal(code.mode, 'verificationCode');
  value(await rpc('web.respond', { id: code.id, result: { value: '1234', url: 'https://books.invalid/code',
    headers: { 'set-cookie': ['fixture=verification; Path=/; Secure'] } } }));
  const webview = await pending(rpc);
  assert.equal(webview.mode, 'webview');
  assert.equal(webview.request.headers.Cookie, 'fixture=verification');
  value(await rpc('web.respond', { id: webview.id, result: {
    value: 'Page title', url: 'https://books.invalid/page', headers: {},
  } }));
  assert.equal(value(await verification).body, '1234:Page title');
  const ruleSource = await importSource(rpc, { ...source, bookSourceUrl: 'https://rule.invalid/',
    ruleContent: { content: '@webjs:document.body.textContent' } });
  const rule = invoke(rpc, ruleSource, 'content', { bookUrl: 'https://rule.invalid/book',
    chapterUrl: 'https://rule.invalid/chapter' });
  const rulePage = await pending(rpc);
  assert.equal(rulePage.mode, 'rule');
  value(await rpc('web.respond', { id: rulePage.id, result: {
    value: 'Rule scalar', url: 'https://rule.invalid/chapter', headers: {},
  } }));
  assert.equal(value(await rule).content, 'Rule scalar');
  const dynamicSource = await importSource(rpc, { ...source,
    bookSourceUrl: 'https://qysg.invalid/',
    html: `<script>
      async function content(url, book) {
        return await window.flutter_inappwebview.callHandler('getVerificationCode', 'https://qysg.invalid/code');
      }
    </script>`,
  });
  const qysg = invoke(rpc, dynamicSource, 'content', {
    bookUrl: 'https://qysg.invalid/book', chapterUrl: 'https://qysg.invalid/chapter',
  });
  const qysgPage = await pending(rpc);
  assert.equal(qysgPage.mode, 'getVerificationCode');
  value(await rpc('web.respond', { id: qysgPage.id, result: {
    value: 'QYSG scalar', url: 'https://qysg.invalid/code', headers: { 'set-cookie': 'qysg=private; Path=/' },
  } }));
  assert.equal(value(await qysg).content, 'QYSG scalar');
});

test('Web Cookies persist as typed rows isolated to their source installation', async t => {
  const dataRoot=temp(), {rpc}=setup(t,{dataRoot});
  const item=await importSource(rpc,{...source,loginUrl:'https://books.invalid/private/login'});
  const other=await importSource(rpc,{...source,bookSourceUrl:'https://other-source.invalid/',loginUrl:'https://other-source.invalid/login'});
  const login=invoke(rpc,item,'login'),page=await pending(rpc);
  value(await rpc('web.respond',{id:page.id,result:{value:true,url:'https://books.invalid/private/login',headers:{},
    cookieChanges:[{url:'https://books.invalid/private/login',setCookie:'session=private; Path=/private; Secure; HttpOnly; SameSite=Strict'}]}}));
  assert.equal(value(await login).value,true);
  const rows=await withTestDatabase(dataRoot,db=>db.query('SELECT name,value,path,secure,http_only,same_site FROM source_private_cookies WHERE installation_id=?',[item.installationId]));
  assert.deepEqual(rows,[{name:'session',value:'private',path:'/private',secure:1,http_only:1,same_site:'Strict'}]);
  const otherLogin=invoke(rpc,other,'login');
  let requests;
  await eventually(async()=>{requests=value(await rpc('web.pending')).requests;return requests.some(request=>request.installationId===other.installationId)});
  const otherPage=requests.find(request=>request.installationId===other.installationId);
  assert.deepEqual(otherPage.request.cookies,[]);
  value(await rpc('web.respond',{id:otherPage.id,cancelled:true}));
  denied(await otherLogin,'CANCELLED');
});

test('protected image fetching and decoding uses a revocable binary reference', async t => {
  const { rpc, calls } = setup(t, { respond: async input => ({
    statusCode: 200, headers: {}, url: input.url,
    body: new URL(input.url).pathname === '/image' ? Buffer.concat([Buffer.from('mask'), png]) : html,
  }) });
  const item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login',
    coverDecodeJs: 'result.subarray(4)' });
  const login = invoke(rpc, item, 'login');
  const page = await pending(rpc);
  value(await rpc('web.respond', { id: page.id, result: {
    value: true, url: 'https://books.invalid/login',
    headers: { 'set-cookie': ['fixture=image-private; Path=/; Secure'] },
  } }));
  value(await login);
  const image = value(await rpc('library.image', { ...target(item), url: 'https://books.invalid/image',
    bookUrl: 'https://books.invalid/book', kind: 'cover' }));
  assert.equal(calls.at(-1).options.headers.cookie, 'fixture=image-private');
  assert.equal(calls.at(-1).options.headers.referer, 'https://books.invalid/');
  assert.deepEqual(Object.keys(image), ['reference']);
  assert.match(image.reference, /^\/v1\/source\/images\//);
  const { runtime } = rpc.transport;
  await rpc.transport.ready;
  async function fetchImage(reference = image.reference) {
    const response = await fetch(new URL(reference, runtime.baseUrl), { headers: {
      'x-meow-request-id': crypto.randomUUID(),
      origin: 'https://browser.example.invalid',
    } });
    return { status: response.status, headers: Object.fromEntries(response.headers),
      body: Buffer.from(await response.arrayBuffer()) };
  }
  const reply = await fetchImage();
  assert.equal(reply.status, 200);
  assert.equal(reply.headers['access-control-allow-origin'], '*');
  assert.equal(reply.headers['content-type'], 'image/png');
  assert.deepEqual(reply.body, png);
  value(await rpc('library.releaseImage', { reference: image.reference }));
  const released = await fetchImage();
  assert.equal(JSON.parse(released.body).code, 'SOURCE_NOT_FOUND');
  const retired = value(await rpc('library.image', { ...target(item), url: 'https://books.invalid/image',
    bookUrl: 'https://books.invalid/book', kind: 'content' }));
  value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: false } }));
  assert.equal(JSON.parse((await fetchImage(retired.reference)).body).code, 'SOURCE_NOT_FOUND');
});

test('Cookie, source values, cache and management flags recover in another Node process', async t => {
  const dataRoot = temp(), { rpc, module } = setup(t, { dataRoot });
  let item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login', searchUrl: `@js:
    if (key === 'write') {
      cookie.setCookie('https://books.invalid/', 'fixture=persisted; Path=/; Secure; HttpOnly; SameSite=Lax');
      cookie.setCookie('https://books.invalid/nested/page', 'nested=stored; Path=/nested; Secure');
      cache.put('private', 'cache-value');
      source.put('private', 'source-value');
      source.setVariable('source-variable');
    }
    source.put('observed', [cookie.getCookie('https://books.invalid/'),
      cache.get('private'), source.get('private'), source.getVariable()].join('|'));
    'https://books.invalid/search'`,
    coverDecodeJs: 'source.get("observed")',
  });
  value(await invoke(rpc, item, 'search', { keyword: 'write' }));
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { group: 'Recovered', order: -3, enabledExplore: false } })).source;
  const disabled = await importSource(rpc, { ...source, bookSourceName: 'Disabled',
    bookSourceUrl: 'https://disabled.invalid/', enabled: false });
  value(await module.close());
  const childSource = `
    import './tests/storage-test-fixture.mjs';
    import { createSourceRuntimeModule } from './apps/runtime/modules/source-runtime.mjs';
    const module = createSourceRuntimeModule({dataRoot:process.argv[1],
      network:async input=>({statusCode:200,headers:{},url:input.url,body:${JSON.stringify(html)}})});
    const call=async(name,input={})=>{
      const reply=await module.methods['source.runtime.'+name](input);
      if(!reply.ok) throw new Error(reply.code); return reply.value;
    };
    try {
      const sources=(await call('library.list')).sources, item=sources[0];
      await call('library.invoke',{installationId:item.installationId,revision:item.revision,
        action:'search',payload:{keyword:'read'}});
      const result=await call('library.invoke',{installationId:item.installationId,revision:item.revision,
        action:'image',payload:{body:''}});
      const disabled=await module.methods['source.runtime.library.invoke']({
        installationId:sources[1].installationId,revision:sources[1].revision,
        action:'search',payload:{keyword:'disabled'}});
      const login=call('library.invoke',{installationId:item.installationId,revision:item.revision,
        action:'login',payload:{}});
      let page;
      for (let count=0;count<1000&&!page;count++) {
        page=(await call('web.pending')).requests[0];
        if (!page) await new Promise(resolve=>setTimeout(resolve,5));
      }
      if (!page) throw new Error('restored source did not request Web login');
      await call('web.respond',{id:page.id,result:{value:true,url:'https://books.invalid/',headers:{},cookieChanges:[]}});
      await login;
      process.stdout.write(JSON.stringify({sources,observed:result.body,disabled,cookies:page.request.cookies}));
    } finally { await module.close(); }
  `;
  const child = spawn(process.execPath, ['--input-type=module', '-e', childSource, dataRoot], {
    cwd: new URL('..', import.meta.url), stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '', error = '';
  child.stdout.on('data', bytes => { output += bytes; });
  child.stderr.on('data', bytes => { error += bytes; });
  const exit = await new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
  assert.equal(exit, 0, error);
  const restored = JSON.parse(output);
  assert.equal(restored.sources[0].installationId, item.installationId);
  assert.equal(restored.sources[0].group, 'Recovered');
  assert.equal(restored.sources[0].order, -3);
  assert.equal(restored.sources[0].enabledExplore, false);
  assert.equal(restored.observed, 'fixture=persisted|cache-value|source-value|source-variable');
  assert.deepEqual(restored.cookies, [
    { name: 'nested', value: 'stored', domain: 'books.invalid', hostOnly: true, path: '/nested',
      secure: true, httpOnly: false, sameSite: null, expires: null },
    { name: 'fixture', value: 'persisted', domain: 'books.invalid', hostOnly: true, path: '/',
      secure: true, httpOnly: true, sameSite: 'Lax', expires: null },
  ]);
  assert.equal(restored.sources[1].installationId, disabled.installationId);
  assert.equal(restored.sources[1].enabled, false);
  denied(restored.disabled, 'SOURCE_DISABLED');
});

test('real Reader SDK keeps unused permanent fields lazy and preserves exact values', async t => {
  const dataRoot = temp(), first = setup(t, { dataRoot });
  const item = await importSource(first.rpc, { ...source,
    coverDecodeJs: 'result === "read" ? source.get("retained") : ' +
      'result === "number" ? typeof source.get("unused") : source.setVariable(result)' });
  await withTestDatabase(dataRoot, async db => {
    const values = new Map([['retained','value'], ['unused',parseJSONText('9007199254740993')]]);
    await db.execute('UPDATE source_reader_state SET variable=? WHERE installation_id=?',
      [Buffer.from('','utf16le'), item.installationId]);
    const statements=[];
    for (const [key,value] of values) {
      const nodes=encodeValueNodes(value);
      for(const node of nodes){
        if(node.propKey!==null) node.propKey=Buffer.from(node.propKey,'utf16le');
        if(node.kind==='text') node.textValue=Buffer.from(node.textValue,'utf16le');
      }
      statements.push(...nodeInserts('source_reader_value_nodes',['installation_id','entry_key'],
        [item.installationId,Buffer.from(key,'utf16le')],nodes));
    }
    await db.transaction(statements);
  });
  value(await first.module.close());
  const second = setup(t, { dataRoot });
  assert.equal(value(await second.rpc('library.list')).sources[0].errorCode, undefined);
  assert.equal(value(await invoke(second.rpc, item, 'search', { keyword: 'unused' })).books[0].name, 'Result');
  assert.equal(value(await invoke(second.rpc, item, 'image', { body: 'read' })).body, 'value');
  assert.equal(value(await invoke(second.rpc, item, 'image', { body: 'number' })).body, 'number');
  assert.equal(value(await invoke(second.rpc, item, 'image', { body: 'updated' })).body, 'updated');
  const values = await withTestDatabase(dataRoot, db => db.query('SELECT entry_key,kind,text_value FROM source_reader_value_nodes WHERE installation_id=?',[item.installationId]));
  assert.ok(values.some(row => row.kind === 'number_text' && row.text_value === '9007199254740993'));
  const variable = await withTestDatabase(dataRoot, db => db.query('SELECT hex(variable) AS value FROM source_reader_state WHERE installation_id=?',[item.installationId]));
  assert.equal(variable[0].value, Buffer.from('updated','utf16le').toString('hex').toUpperCase());
});

test('a late HTTP response after disable cannot persist its Set-Cookie header', async t => {
  const entered = deferred(), release = deferred();
  let networkSignal;
  const { rpc } = setup(t, { dataRoot: temp(), respond: async input => {
    networkSignal = input.signal;
    entered.resolve();
    await release.promise;
    return { statusCode: 200, headers: { 'set-cookie': 'late=discarded; Path=/' }, body: html, url: input.url };
  } });
  let item = await importSource(rpc);
  const request = invoke(rpc, item, 'search', { keyword: 'held' });
  await entered.promise;
  const disabled = rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: false } });
  await eventually(() => networkSignal.aborted);
  release.resolve();
  item = value(await disabled).source;
  assert.equal((await request).ok, false);
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: true } })).source;
  assert.equal(value(await invoke(rpc, item, 'loginStatus')).loggedIn, false);
});

test('import preview is read-only, preserves protocols and metadata, and imports only confirmed definitions', async t => {
  const dataRoot = temp();
  const { rpc, calls } = setup(t, { dataRoot });
  const candidates = [
    { ...source, enabled: false, weight: 7, respondTime: 12, lastUpdateTime: 42,
      jsLib: 'throw new Error("preview must not execute")', extension: { kept: [null, 3, true] } },
    { ...source, bookSourceName: 'Current extras', bookSourceUrl: 'https://extra-preview.invalid/',
      enabled: false, enable: true, serialNumber: -1, ruleFindUrl: { ignored: true } },
    { bookSourceName: '轻悦时光', bookSourceUrl: 'https://qysg-preview.invalid/', html: '<script>throw new Error("not executed")</script>' },
  ];
  const preview = value(await rpc('library.previewImport', { text: JSON.stringify({ bookSources: candidates }) }));
  assert.deepEqual(preview.failures, []);
  assert.deepEqual(preview.items.map(item => item.protocol), ['legado', 'legado', 'qysg']);
  assert.ok(preview.items.every(item => item.exists === false));
  assert.deepEqual(value(await rpc('library.list')).sources, []);
  assert.deepEqual(await withTestDatabase(dataRoot, db => db.query('SELECT installation_id FROM source_installations')), []);
  assert.deepEqual(calls, []);
  // A cancelled preview needs no discard RPC and must not leave an installation.
  const selected = JSON.parse(preview.items[0].definition);
  const imported = value(await rpc('library.import', { text: JSON.stringify([selected]) }));
  assert.deepEqual(imported.failures, []);
  assert.equal(imported.sources.length, 1);
  assert.deepEqual(Object.fromEntries(['weight', 'respondTime', 'lastUpdateTime', 'hasExplore'].map(key => [key, imported.sources[0][key]])),
    { weight: 7, respondTime: 12, lastUpdateTime: 42, hasExplore: true });
  const exported = JSON.parse(value(await rpc('library.export', { installationIds: [imported.sources[0].installationId] })).text);
  assert.deepEqual(exported[0].extension, candidates[0].extension);
  const again = value(await rpc('library.previewImport', { text: JSON.stringify(candidates) }));
  assert.deepEqual(again.items.map(item => item.exists), [true, false, false]);
  assert.equal(value(await rpc('library.list')).sources[0].revision, 1);
});

test('URL preview downloads once, retains standalone HTML identity and accepts Reader container and line formats', async t => {
  const standalone = '<script>function search(){return [];}</script>';
  const { rpc, calls } = setup(t, { respond: input => ({ statusCode: 200, url: input.url, headers: {}, body: standalone }) });
  const preview = value(await rpc('library.previewImport', { url: 'https://preview.invalid/source.html', fileName: 'source.html' }));
  assert.equal(calls.length, 1);
  assert.equal(preview.items[0].sourceUrl, 'https://preview.invalid/source.html');
  assert.equal(value(await rpc('library.list')).sources.length, 0);
  const imported = value(await rpc('library.import', { text: preview.items[0].definition }));
  assert.equal(imported.sources[0].sourceUrl, preview.items[0].sourceUrl);
  assert.equal(calls.length, 1, 'confirming a preview must not download its input a second time');
  for (const text of [JSON.stringify({ sources: [source] }), JSON.stringify({ data: [source] }),
    '\uFEFF' + JSON.stringify(source) + '\n' + JSON.stringify({ ...source, bookSourceUrl: 'https://second.invalid/' })]) {
    const result = value(await rpc('library.previewImport', { text }));
    assert.equal(result.failures.length, 0);
    assert.ok(result.items.length > 0);
  }
});

test('default URL preview follows HTTP redirects without executing candidates or downloading again on confirmation', async t => {
  const paths = [];
  const definition = { ...source, enabled: false, jsLib: 'throw new Error("preview must not execute")' };
  const { socketOrigin } = await serverFor(t, (request, response) => {
    paths.push(request.url);
    if (request.url === '/redirect') {
      response.writeHead(302, { location: '/definitions' }).end();
    } else response.end(JSON.stringify([definition]));
  });
  const module = createSourceRuntimeModule({ platform: 'fixture', dataRoot: temp() });
  const rpc = moduleHttp(t, module);
  const preview = value(await rpc('library.previewImport', { url: `${socketOrigin}/redirect` }));
  assert.deepEqual(preview.failures, []);
  assert.equal(preview.items.length, 1);
  assert.deepEqual(value(await rpc('library.list')).sources, []);
  assert.deepEqual(paths, ['/redirect', '/definitions']);
  const confirmed = value(await rpc('library.import', { text: preview.items[0].definition }));
  assert.equal(confirmed.sources.length, 1);
  assert.equal(confirmed.sources[0].enabled, false);
  assert.deepEqual(paths, ['/redirect', '/definitions']);
});

test('default URL preview rejects non-HTTP protocols and passes credentials to actual transport', async t => {
  const paths = [];
  const { socketOrigin } = await serverFor(t, (request, response) => {
    paths.push(request.url);
    if (request.url === '/protocol') response.writeHead(302, { location: 'file:///source.json' }).end();
    else if (request.url === '/credentials') {
      const target = new URL('/definitions', socketOrigin);
      target.username = 'fixture'; target.password = 'fixture';
      response.writeHead(302, { location: target.href }).end();
    } else response.end(JSON.stringify({ ...source, enabled: false }));
  });
  const rpc = moduleHttp(t, createSourceRuntimeModule({ platform: 'fixture' }));
  for (const url of ['not a URL', 'file:///source.json'])
    denied(await rpc('library.previewImport', { url }), 'INVALID_REQUEST');
  assert.deepEqual(paths, []);
  denied(await rpc('library.previewImport', { url: socketOrigin + '/protocol' }), 'INVALID_REQUEST');
  const direct = new URL('/definitions', socketOrigin);
  direct.username = 'fixture'; direct.password = 'fixture';
  for (const url of [direct.href, socketOrigin + '/credentials']) {
    assert.equal(value(await rpc('library.previewImport', { url })).items.length, 1);
  }
  assert.deepEqual(paths, ['/protocol', '/definitions', '/credentials', '/definitions']);
  assert.deepEqual(value(await rpc('library.list')).sources, []);
});

test('default URL preview shutdown drains the real HTTP exchange without installing a source', async t => {
  const entered = deferred(), closed = deferred();
  const { socketOrigin } = await serverFor(t, (_request, response) => {
    response.on('close', () => closed.resolve());
    response.writeHead(200);
    response.write('[');
    entered.resolve();
  });
  const module = createSourceRuntimeModule({ platform: 'fixture', dataRoot: temp() });
  const rpc = moduleHttp(t, module);
  const preview = rpc('library.previewImport', { url: socketOrigin + '/held' });
  await entered.promise;
  const receipt = await module.close();
  assert.equal(receipt.ok, true);
  assert.equal(receipt.value.cleanupComplete, true);
  await closed.promise;
  denied(await preview, 'CLOSED');
});

test('URL preview deadline cancels and waits for the actual download before reporting timeout', async t => {
  const entered = deferred(), aborted = deferred(), drained = deferred();
  const { module } = setup(t, { respond: async input => {
    entered.resolve();
    input.signal.addEventListener('abort', () => aborted.resolve(), { once: true });
    await drained.promise;
    return { statusCode: 200, headers: {}, url: input.url, body: JSON.stringify(source) };
  } });
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const preview = module.methods['source.runtime.library.previewImport']({ url: 'https://preview.invalid/held' });
  let completed = false;
  preview.then(() => { completed = true; });
  await entered.promise;
  t.mock.timers.tick(60_000);
  await aborted.promise;
  assert.equal(completed, false);
  drained.resolve();
  denied(await preview, 'TIMEOUT');
  t.mock.timers.reset();
  assert.deepEqual(value(await module.methods['source.runtime.library.list']()).sources, []);
});

test('cancelled preview is drained by shutdown and never writes the permanent repository', async t => {
  const entered = deferred(), held = deferred(), cancelled = deferred();
  const { rpc, module } = setup(t, { respond: async input => {
    entered.resolve(); input.signal.addEventListener('abort', () => cancelled.resolve(), { once: true });
    await held.promise;
    return { statusCode: 200, headers: {}, url: input.url, body: JSON.stringify(source) };
  } });
  const preview = rpc('library.previewImport', { url: 'https://preview.invalid/held' });
  await entered.promise;
  const closing = module.close(); let done = false; closing.then(() => { done = true; });
  await cancelled.promise; await new Promise(resolve => setImmediate(resolve)); assert.equal(done, false);
  held.resolve(); denied(await preview, 'CLOSED');
  const receipt = await closing; assert.equal(receipt.ok, true); assert.equal(receipt.value.cleanupComplete, true);
});
