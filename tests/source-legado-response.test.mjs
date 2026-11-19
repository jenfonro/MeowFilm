import './storage-test-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { createNodeSourceCapabilities } from '../packages/script-host/src/index.js';
import { moduleHttp } from './runtime-http-fixture.mjs';

const source = {
  bookSourceName: 'Response semantics', bookSourceUrl: 'https://books.invalid/',
  searchUrl: '/search',
  exploreUrl: '[{"title":"List","url":"/list"}]',
  ruleSearch: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
  ruleExplore: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
  ruleBookInfo: { name: 'h2@text' },
  ruleToc: { chapterList: 'li', chapterName: 'a@text', chapterUrl: 'a@href' },
  ruleContent: { content: 'article@text' },
};
const bookPage = '<div class="book"><h2>Fixture book</h2><a href="/book">open</a></div>';
const scriptPage = '<html><head><script>var state = "pending";</script>' +
  '<script src="/browser.js"></script></head><body></body></html>';

function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value;
}

async function fixture(t, definition = source, web) {
  let response;
  const requests = [];
  const network = async input => {
    requests.push(new URL(input.url).pathname);
    return { ...response, url: input.url,
      body: typeof response.body === 'function' ? response.body(input) : response.body };
  };
  const capabilityOwner = web ? createNodeSourceCapabilities({ network, web }) : undefined;
  const rpc = moduleHttp(t, createSourceRuntimeModule({ network, capabilityOwner }));
  const imported = okay(await rpc('library.import', { text: JSON.stringify(definition) }));
  assert.deepEqual(imported.failures, []);
  const item = imported.sources[0];
  return {
    requests,
    rpc,
    respond(statusCode, body, contentType = 'text/html; charset=utf-8') {
      response = { statusCode, body, headers: { 'content-type': contentType } };
    },
    invoke(action, payload = {}) {
      return rpc('library.invoke', {
        installationId: item.installationId, revision: item.revision, action, payload,
      });
    },
  };
}

test('Legado business requests reject non-success HTTP instead of parsing an empty or partial result', async t => {
  const { respond, invoke } = await fixture(t);
  for (const status of [302, 304, 401, 403, 404, 429, 500, 503]) {
    respond(status, bookPage);
    for (const [action, payload] of [
      ['search', { keyword: 'book' }], ['explore', { url: '/list' }],
      ['bookInfo', { bookUrl: 'https://books.invalid/book' }],
      ['toc', { tocUrl: 'https://books.invalid/toc' }],
      ['content', { chapterUrl: 'https://books.invalid/chapter' }],
    ]) {
      assert.deepEqual(await invoke(action, payload),
        { ok: false, contract: 'source.runtime.v2', code: 'HTTP_ERROR' }, `${action} HTTP ${status}`);
    }
  }
  respond(200, bookPage);
  assert.equal(okay(await invoke('search', { keyword: 'book' })).books[0].name, 'Fixture book');
});

test('static list rules report accepted script-only HTML as requiring Web access without executing the page', async t => {
  const { respond, invoke, requests, rpc } = await fixture(t);
  assert.equal(okay(await invoke('exploreKinds')).kinds.length, 1);
  assert.deepEqual(requests, [], 'static choices are not a business health check');
  respond(202, scriptPage);
  for (const [action, payload] of [
    ['search', { keyword: 'book' }], ['explore', { url: '/list' }],
  ]) {
    assert.deepEqual(await invoke(action, payload),
      { ok: false, contract: 'source.runtime.v2', code: 'WEB_INTERACTION_REQUIRED' });
  }
  assert.deepEqual(requests, ['/search', '/list'], 'no script download, retry or browser fallback');
  assert.deepEqual(okay(await rpc('web.pending')).requests, []);
  respond(200, bookPage);
  assert.equal(okay(await invoke('explore', { url: '/list' })).books.length, 1);
});

test('normal empty results, unrelated markup and non-executable script data retain their empty semantics', async t => {
  const { respond, invoke } = await fixture(t);
  for (const [status, body, contentType] of [
    [200, '<html><body><ul class="books"></ul><p>No results</p></body></html>'],
    [200, '<div class="changed-list"><a href="/book">Unmatched structure</a></div>'],
    [200, scriptPage],
    [204, ''],
    [202, '<html><head><script src="/analytics.js"></script></head><body><ul></ul></body></html>'],
    [202, '<html><head><script src="/analytics.js"></script></head><body>No results</body></html>'],
    [202, '<html><head><script type="application/json">[]</script></head><body></body></html>'],
    [202, '<html><head><script type="application/ld+json">{}</script></head><body></body></html>'],
    [202, '<html><head><script></script></head><body></body></html>'],
    [202, '[]', 'application/json'],
  ]) {
    respond(status, body, contentType);
    assert.deepEqual(okay(await invoke('explore', { url: '/list' })).books, [], `HTTP ${status}: ${body}`);
  }
  respond(202, bookPage);
  assert.equal(okay(await invoke('explore', { url: '/list' })).books.length, 1);
});

test('script list rules remain authoritative for recovery and intentional empty results', async t => {
  const definition = { ...source, ruleSearch: { ...source.ruleSearch,
    bookList: '@js:key === "empty" ? [] : java.ajax("https://books.invalid/recovered")',
  } };
  const { invoke, respond, requests } = await fixture(t, definition);
  respond(202, input => new URL(input.url).pathname === '/recovered' ? bookPage : scriptPage);
  assert.deepEqual(okay(await invoke('search', { keyword: 'empty' })).books, []);
  assert.equal(okay(await invoke('search', { keyword: 'recover' })).books.length, 1);
  assert.deepEqual(requests, ['/search', '/search', '/recovered']);
});

test('explicit Web request responses use the same HTTP and script-only classification', async t => {
  let response = { statusCode: 503, data: bookPage };
  const definition = { ...source, searchUrl: '/search,{"webView":true}' };
  const { invoke, requests } = await fixture(t, definition, async () => ({
    ...response, url: 'https://books.invalid/search', headers: { 'Content-Type': 'text/html' },
  }));
  assert.deepEqual(await invoke('search', { keyword: 'book' }),
    { ok: false, contract: 'source.runtime.v2', code: 'HTTP_ERROR' });
  response = { statusCode: 202, data: scriptPage };
  assert.deepEqual(await invoke('search', { keyword: 'book' }),
    { ok: false, contract: 'source.runtime.v2', code: 'WEB_INTERACTION_REQUIRED' });
  response = { statusCode: 200, data: bookPage };
  assert.equal(okay(await invoke('search', { keyword: 'book' })).books.length, 1);
  assert.deepEqual(requests, []);
});

test('raw scripted HTTP retains non-success statuses for source-owned handling', async t => {
  const { respond, invoke } = await fixture(t, { ...source,
    coverDecodeJs: '@js:const response = java.connect("https://books.invalid/probe"); response.statusCode;',
  });
  respond(403, 'denied');
  assert.equal(okay(await invoke('image', { body: 'input' })).body, 403);
});
