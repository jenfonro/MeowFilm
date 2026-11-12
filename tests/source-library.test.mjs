import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, rmSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { PrivateDocuments, ScriptHostError } from '@meowfilm/script-host';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred } from '../packages/runtime-bridge/test/http-fixture.js';

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
function temp(t) {
  const root = new URL('../tmp/test-results/source-library/', import.meta.url);
  mkdirSync(root, { recursive: true });
  const path = mkdtempSync(join(root.pathname, 'case-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
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
  const { rpc } = setup(t, { dataRoot: temp(t) });
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

test('URL and BOM file imports preserve batch failures and export managed legacy and HTML sources', async t => {
  const legacy = { bookSourceName: 'Legacy', bookSourceUrl: 'https://legacy.invalid/',
    ruleSearchList: '.book', ruleSearchName: 'h2@text', ruleSearchUrl: 'https://legacy.invalid/search',
    ruleBookContent: 'article@text', ruleFindUrl: 'All::https://legacy.invalid/explore' };
  const { rpc, calls } = setup(t, { respond: input => ({ statusCode: 200, headers: {},
    url: input.url, body: '\uFEFF' + JSON.stringify([legacy, { invalid: true }]) }) });
  const imported = value(await rpc('library.import', { url: 'https://sources.invalid/library.json' }));
  assert.equal(calls[0].url, 'https://sources.invalid/library.json');
  assert.equal(imported.sources.length, 1);
  assert.deepEqual(imported.failures, [{ index: 1, name: '', code: 'UNSUPPORTED_PROTOCOL' }]);
  let item = imported.sources[0];
  item = value(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { enabled: false, enabledExplore: false,
      order: -7, group: 'Legacy metadata' } })).source;
  const exported = value(await rpc('library.export', { installationIds: [item.installationId] })).text;
  const reimported = value(await rpc('library.import', { text: '\uFEFF' + exported })).sources[0];
  assert.equal(reimported.installationId, item.installationId);
  assert.equal(reimported.protocol, 'yiciyuan');
  assert.equal(reimported.enabled, false);
  assert.equal(reimported.enabledExplore, false);
  assert.equal(reimported.order, -7);
  assert.equal(reimported.group, 'Legacy metadata');
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

test('failed definition initialization and disk transactions preserve the published definition', async t => {
  const dataRoot = temp(t), disk = new PrivateDocuments(dataRoot, 'source');
  let failWrite = false;
  const { rpc } = setup(t, { respond: input => ({
    statusCode: 200, url: input.url,
    headers: new URL(input.url).pathname === '/broken.js' ? { 'set-cookie': 'trial=discarded; Path=/' } : {},
    body: new URL(input.url).pathname === '/broken.js' ? 'function search( {' : html,
  }), documents: {
    read: name => disk.read(name),
    write(name, bytes) {
      if (failWrite) throw new ScriptHostError('STORAGE_ERROR');
      disk.write(name, bytes);
    },
  } });
  const item = await importSource(rpc);
  const old = value(await rpc('library.get', { installationId: item.installationId }));
  const bad = { bookSourceName: 'Broken', bookSourceUrl: source.bookSourceUrl,
    html: '<script src="https://books.invalid/broken.js"></script>' };
  denied(await rpc('library.save', { installationId: item.installationId,
    expectedRevision: item.revision, definition: JSON.stringify(bad) }), 'SOURCE_FAILURE');
  assert.deepEqual(value(await rpc('library.get', { installationId: item.installationId })), old);
  assert.equal(value(await invoke(rpc, item, 'loginStatus')).loggedIn, false);
  failWrite = true;
  denied(await rpc('library.save', { installationId: item.installationId, expectedRevision: item.revision,
    definition: JSON.stringify({ ...source, bookSourceName: 'Not committed' }) }), 'STORAGE_ERROR');
  assert.deepEqual(value(await rpc('library.get', { installationId: item.installationId })), old);
  assert.equal(value(await invoke(rpc, item, 'search', { keyword: 'old definition' })).books[0].name, 'Result');
  failWrite = false;
});

test('a storage failure after replacement restores the last committed library bytes', async t => {
  const dataRoot = temp(t), disk = new PrivateDocuments(dataRoot, 'source');
  let failAfterWrite = false;
  const { rpc } = setup(t, { documents: {
    read: name => disk.read(name),
    remove: name => disk.remove(name),
    write(name, bytes) {
      disk.write(name, bytes);
      if (failAfterWrite) {
        failAfterWrite = false;
        throw new ScriptHostError('STORAGE_ERROR');
      }
    },
  } });
  const item = await importSource(rpc);
  const committed = disk.read('library.json');
  failAfterWrite = true;
  denied(await rpc('library.update', { installationId: item.installationId,
    expectedRevision: item.revision, patch: { group: 'Not committed' } }), 'STORAGE_ERROR');
  assert.deepEqual(disk.read('library.json'), committed);
  assert.deepEqual(value(await rpc('library.list')).sources, [item]);
  assert.equal(value(await invoke(rpc, item, 'search', { keyword: 'old' })).books[0].name, 'Result');
});

test('failed private writes report storage errors and keep the last accepted source value', async t => {
  const disk = new PrivateDocuments(temp(t), 'source');
  let failWrite = false;
  const { rpc } = setup(t, { documents: {
    read: name => disk.read(name),
    write(name, bytes) {
      if (failWrite) throw new ScriptHostError('STORAGE_ERROR');
      disk.write(name, bytes);
    },
  } });
  const item = await importSource(rpc, { ...source, coverDecodeJs: `
    if (result !== '') source.put('private', result);
    source.get('private')`,
  });
  assert.equal(value(await invoke(rpc, item, 'image', { body: 'accepted' })).body, 'accepted');
  const committed = disk.read('library.json');
  failWrite = true;
  denied(await invoke(rpc, item, 'image', { body: 'rejected' }), 'STORAGE_ERROR');
  failWrite = false;
  assert.equal(value(await invoke(rpc, item, 'image', { body: '' })).body, 'accepted');
  assert.deepEqual(disk.read('library.json'), committed);
});

test('uncertain storage recovery fences private writes from already admitted source calls', async t => {
  const disk = new PrivateDocuments(temp(t), 'source');
  let failedWrites = 0, writeCount = 0;
  const { rpc } = setup(t, { documents: {
    read: name => disk.read(name),
    write(name, bytes) {
      writeCount++;
      disk.write(name, bytes);
      if (failedWrites > 0) {
        failedWrites--;
        throw new ScriptHostError('STORAGE_ERROR');
      }
    },
  } });
  const a = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login' });
  const b = await importSource(rpc, { ...source, bookSourceUrl: 'https://second.invalid/',
    loginUrl: 'https://second.invalid/login' });
  const first = invoke(rpc, a, 'login'), firstPage = await pending(rpc);
  const second = invoke(rpc, b, 'login');
  let pages;
  await eventually(async () => {
    pages = value(await rpc('web.pending')).requests;
    return pages.length === 2;
  });
  failedWrites = 2;
  value(await rpc('web.respond', { id: firstPage.id, result: {
    value: true, url: 'https://books.invalid/login', headers: { 'set-cookie': ['private=first; Path=/'] },
  } }));
  denied(await first, 'STORAGE_ERROR');
  const fencedWrites = writeCount, fencedBytes = disk.read('library.json');
  const secondPage = pages.find(page => page.installationId === b.installationId);
  value(await rpc('web.respond', { id: secondPage.id, result: {
    value: true, url: 'https://second.invalid/login', headers: { 'set-cookie': ['private=second; Path=/'] },
  } }));
  denied(await second, 'STORAGE_ERROR');
  assert.equal(writeCount, fencedWrites);
  assert.deepEqual(disk.read('library.json'), fencedBytes);
  denied(await rpc('library.list'), 'STORAGE_ERROR');
});

test('Web login, dynamic request and cancellation bind responses and cookies to the exact source', async t => {
  const { rpc, calls } = setup(t, { dataRoot: temp(t) });
  const first = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login',
    searchUrl: 'https://books.invalid/search,{"webView":true}' });
  const second = await importSource(rpc, { ...source, bookSourceName: 'Independent',
    bookSourceUrl: 'https://independent.invalid/' });
  const login = invoke(rpc, first, 'login');
  const request = await pending(rpc);
  assert.equal(request.installationId, first.installationId);
  assert.equal(request.mode, 'login');
  assert.equal(Object.hasOwn(request, 'generation'), false);
  value(await rpc('web.respond', { id: request.id, result: {
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
    const { rpc, module } = setup(t, { dataRoot: temp(t) });
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
    const { rpc, module, calls } = setup(t, { dataRoot: temp(t) });
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

test('Web origin Cookie changes persist as one transaction without changing unchanged seeds or other sources', async t => {
  const disk = new PrivateDocuments(temp(t), 'source');
  let writes = 0, failWrite = false;
  const { rpc } = setup(t, { documents: {
    read: name => disk.read(name),
    write(name, bytes) {
      writes++;
      if (failWrite) throw new ScriptHostError('STORAGE_ERROR');
      disk.write(name, bytes);
    },
  } });
  const item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/private/login' });
  const other = await importSource(rpc, { ...source, bookSourceUrl: 'https://other-source.invalid/',
    loginUrl: 'https://other-source.invalid/login' });
  const first = invoke(rpc, item, 'login'), firstPage = await pending(rpc);
  assert.deepEqual(firstPage.request.cookies, []);
  writes = 0;
  value(await rpc('web.respond', { id: firstPage.id, result: { value: true,
    url: 'https://redirect.invalid/final', headers: { 'set-cookie': [] },
    cookieChanges: [
      { url: 'https://books.invalid/private/login',
        setCookie: 'session=private; Path=/private; Secure; HttpOnly; SameSite=Strict' },
      { url: 'https://books.invalid/', setCookie: '__Host-session=root; Path=/; Secure; HttpOnly' },
      { url: 'https://redirect.invalid/', setCookie: 'redirect=other; Path=/; Secure; SameSite=None' },
    ] } }));
  assert.equal(value(await first).value, true);
  assert.equal(writes, 1);
  const committed = disk.read('library.json');
  const second = invoke(rpc, item, 'login'), secondPage = await pending(rpc);
  assert.equal(secondPage.request.cookies.length, 3);
  assert.deepEqual(secondPage.request.cookies.find(cookie => cookie.name === 'session'), {
    name: 'session', value: 'private', domain: 'books.invalid', hostOnly: true, path: '/private',
    secure: true, httpOnly: true, sameSite: 'Strict', expires: null,
  });
  writes = 0;
  value(await rpc('web.respond', { id: secondPage.id, result: { value: false,
    url: 'https://books.invalid/private/login', headers: { 'set-cookie': [] }, cookieChanges: [] } }));
  assert.equal(value(await second).value, false);
  assert.equal(writes, 0);
  assert.deepEqual(disk.read('library.json'), committed);
  const independent = invoke(rpc, other, 'login'), independentPage = await pending(rpc);
  assert.deepEqual(independentPage.request.cookies, []);
  value(await rpc('web.respond', { id: independentPage.id, cancelled: true }));
  denied(await independent, 'CANCELLED');

  const rejected = invoke(rpc, item, 'login'), rejectedPage = await pending(rpc);
  failWrite = true;
  value(await rpc('web.respond', { id: rejectedPage.id, result: { value: null,
    url: 'https://redirect.invalid/final', headers: {}, cookieChanges: [
      { url: 'https://books.invalid/private/login', setCookie: 'session=rejected; Path=/private; Secure' },
      { url: 'https://redirect.invalid/', setCookie: 'redirect=rejected; Path=/; Secure' },
    ] } }));
  denied(await rejected, 'STORAGE_ERROR');
  assert.deepEqual(disk.read('library.json'), committed);
  failWrite = false;
  const read = invoke(rpc, item, 'login'), readPage = await pending(rpc);
  assert.deepEqual(readPage.request.cookies, secondPage.request.cookies);
  value(await rpc('web.respond', { id: readPage.id, cancelled: true }));
  denied(await read, 'CANCELLED');
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

test('Cookie, source values, cache and management flags recover through an App directory alias in another Node process', async t => {
  const base = temp(t), actual = join(base, 'private', 'var'), alias = join(base, 'var');
  mkdirSync(actual, { recursive: true });
  symlinkSync(actual, alias, process.platform === 'win32' ? 'junction' : 'dir');
  const dataRoot = join(alias, 'data'), { rpc, module } = setup(t, { dataRoot });
  let item = await importSource(rpc, { ...source, loginUrl: 'https://books.invalid/login', searchUrl: `@js:
    if (key === 'write') {
      cookie.setCookie('https://books.invalid/', 'fixture=persisted; Path=/; Secure; HttpOnly; SameSite=Lax');
      cookie.setCookie('https://books.invalid/legacy/page', 'legacy=old; Path=/legacy; Secure');
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
  const file = join(dataRoot, 'source/library.json'), stored = JSON.parse(readFileSync(file, 'utf8'));
  const legacy = stored.sources[0].private.cookies[0][1].find(([, cookie]) => cookie.name === 'legacy')[1];
  delete legacy.httpOnly;
  delete legacy.sameSite;
  writeFileSync(file, JSON.stringify(stored));
  const childSource = `
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
    { name: 'legacy', value: 'old', domain: 'books.invalid', hostOnly: true, path: '/legacy',
      secure: true, httpOnly: false, sameSite: null, expires: null },
    { name: 'fixture', value: 'persisted', domain: 'books.invalid', hostOnly: true, path: '/',
      secure: true, httpOnly: true, sameSite: 'Lax', expires: null },
  ]);
  assert.equal(restored.sources[1].installationId, disabled.installationId);
  assert.equal(restored.sources[1].enabled, false);
  denied(restored.disabled, 'SOURCE_DISABLED');
});

test('corrupt persistent storage is reported and is never replaced by an empty library', async t => {
  const dataRoot = temp(t);
  mkdirSync(join(dataRoot, 'source'));
  writeFileSync(join(dataRoot, 'source/library.json'), 'broken-private-data');
  const { rpc } = setup(t, { dataRoot });
  denied(await rpc('library.list'), 'CORRUPT_STORAGE');
  denied(await rpc('library.import', { text: JSON.stringify(source) }), 'CORRUPT_STORAGE');
  assert.equal(readFileSync(join(dataRoot, 'source/library.json'), 'utf8'), 'broken-private-data');
});

test('a late HTTP response after disable cannot persist its Set-Cookie header', async t => {
  const entered = deferred(), release = deferred();
  let networkSignal;
  const { rpc } = setup(t, { dataRoot: temp(t), respond: async input => {
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
  const dataRoot = temp(t), disk = new PrivateDocuments(dataRoot, 'source');
  const { rpc, calls } = setup(t, { dataRoot });
  const candidates = [
    { ...source, enabled: false, weight: 7, respondTime: 12, lastUpdateTime: 42,
      jsLib: 'throw new Error("preview must not execute")', extension: { kept: [null, 3, true] } },
    { bookSourceName: '异次元', bookSourceUrl: 'https://legacy-preview.invalid/', enable: false,
      ruleSearchList: '.book', ruleSearchUrl: 'https://legacy-preview.invalid/?searchKey' },
    { bookSourceName: '轻悦时光', bookSourceUrl: 'https://qysg-preview.invalid/', html: '<script>throw new Error("not executed")</script>' },
  ];
  const preview = value(await rpc('library.previewImport', { text: JSON.stringify({ bookSources: candidates }) }));
  assert.deepEqual(preview.failures, []);
  assert.deepEqual(preview.items.map(item => item.protocol), ['legado', 'yiciyuan', 'qysg']);
  assert.ok(preview.items.every(item => item.exists === false));
  assert.deepEqual(value(await rpc('library.list')).sources, []);
  assert.equal(disk.read('library.json'), null);
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
