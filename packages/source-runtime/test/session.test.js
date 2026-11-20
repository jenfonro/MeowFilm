import test from 'node:test';
import assert from 'node:assert/strict';
import {
  createCapabilityClient,
  detectSourceProtocol,
  hostApiVersion,
  normalizeSourceDefinition,
  SourceSession,
} from '../src/index.js';
import { parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

const turn = () => new Promise(resolve => setImmediate(resolve));
const body = value => new TextEncoder().encode(value);

function sandboxFactory(behavior = {}) {
  return async options => {
    const sandbox = {
      live: true,
      capabilities: {},
      async load(kind, source, metadata) {
        if (behavior.load) await behavior.load(options, kind, source, metadata);
        sandbox.capabilities = {
          actions: behavior.actions ? [...behavior.actions] : [...metadata.actions],
        };
      },
      activate() {},
      async invoke(...args) {
        if (behavior.invoke) return behavior.invoke(options, ...args);
        throw Object.assign(new Error(), { code: 'UNSUPPORTED_ACTION' });
      },
      async evaluate(...args) {
        if (behavior.evaluate) return behavior.evaluate(options, ...args);
        throw Object.assign(new Error(), { code: 'UNSUPPORTED_RULE' });
      },
      close() { sandbox.live = false; },
      async drain() {},
      stats() {
        return {
          live: sandbox.live,
          invocations: 0,
          timers: 0,
          networkRequests: 0,
          pendingAdapterTasks: 0,
        };
      },
    };
    return sandbox;
  };
}

function capabilitiesFor(routes, calls = []) {
  return createCapabilityClient({
    async invoke(name, input, context = {}) {
      calls.push({
        name,
        input: structuredClone(input),
        scope: context.scope === undefined ? undefined : structuredClone(context.scope),
      });
      if (name === 'http.request') {
        const url = new URL(input.url);
        const route = routes[url.pathname];
        const value = typeof route === 'function' ? route(input) : route;
        if (value === undefined) throw Object.assign(new Error(), { code: 'NETWORK_ERROR' });
        const response = typeof value === 'string' ? { data: value } : value;
        return {
          version: hostApiVersion,
          status: 200,
          statusText: 'OK',
          headers: { 'content-type': ['text/html; charset=utf-8'] },
          url: response.url ?? url.href,
          body: body(response.data),
        };
      }
      if (name === 'cookie.get') return '';
      if (name === 'cookie.set' || name === 'cookie.clear' ||
          name === 'storage.set' || name === 'storage.delete' ||
          name === 'virtual.unregister') return true;
      if (name === 'storage.get') return null;
      if (name === 'virtual.register') {
        return { url: `meow-source://${encodeURIComponent(input.profile)}/${input.route}` };
      }
      throw Object.assign(new Error(), { code: 'CAPABILITY_UNAVAILABLE' });
    },
  });
}

const legadoSource = {
  bookSourceName: 'Fixture Books',
  bookSourceUrl: 'https://books.invalid/',
  searchUrl: 'https://books.invalid/search?key={{key}}',
  ruleSearch: {
    bookList: '.book',
    name: 'h2@text',
    author: '.author@text',
    bookUrl: 'a@href',
    coverUrl: 'img@src',
  },
  ruleBookInfo: {
    name: 'h1@text',
    author: '.author@text',
    intro: '.intro@html',
    coverUrl: 'img@src',
    tocUrl: '.toc@href',
  },
  ruleToc: {
    chapterList: 'li',
    chapterName: 'a@text',
    chapterUrl: 'a@href',
  },
  ruleContent: {
    content: 'article@html',
  },
};

test('Legado session executes search, details, table of contents and content through one portable kernel', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'fixture',
    generation: 1,
    source: legadoSource,
    protocol: 'legado',
    capabilities: capabilitiesFor({
      '/search': `
        <div class="book">
          <h2>《Fixture Book》</h2><span class="author">作者：Alice</span>
          <a href="/book/1">open</a><img src="/cover.jpg">
        </div>`,
      '/book/1': `
        <h1>Fixture Book</h1><span class="author">Alice</span>
        <div class="intro"><b>Intro</b> text</div>
        <img src="/cover.jpg"><a class="toc" href="/book/1/toc">toc</a>`,
      '/book/1/toc': `
        <ul>
          <li><a href="/chapter/1">Chapter One</a></li>
          <li><a href="/chapter/2">Chapter Two</a></li>
        </ul>`,
      '/chapter/1': '<article><p>Hello</p><p>World</p></article>',
    }, calls),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  assert.deepEqual(await session.start(), {
    protocol: 'legado',
    importedProtocol: 'legado',
    actions: ['search', 'bookInfo', 'toc', 'content', 'loginStatus', 'image'],
  });
  session.activate();

  const search = await session.invoke('search', { keyword: 'fixture', page: 1 });
  assert.equal(search.books.length, 1);
  assert.deepEqual(search.books[0], {
    name: 'Fixture Book',
    author: 'Alice',
    kind: '',
    coverUrl: 'https://books.invalid/cover.jpg',
    intro: '',
    bookUrl: 'https://books.invalid/book/1',
    tocUrl: 'https://books.invalid/book/1',
    wordCount: '',
    latestChapterTitle: '',
    type: 0,
    origin: 'https://books.invalid/',
    originName: 'Fixture Books',
    variable: '',
  });
  const info = await session.invoke('bookInfo', {
    bookUrl: search.books[0].bookUrl,
    book: search.books[0],
  });
  assert.equal(info.book.intro, 'Intro text');
  assert.equal(info.book.tocUrl, 'https://books.invalid/book/1/toc');
  const toc = await session.invoke('toc', {
    book: info.book,
    bookUrl: info.book.bookUrl,
    tocUrl: info.book.tocUrl,
  });
  assert.deepEqual(toc.chapters.map(chapter => [chapter.name, chapter.chapterUrl]), [
    ['Chapter One', 'https://books.invalid/chapter/1'],
    ['Chapter Two', 'https://books.invalid/chapter/2'],
  ]);
  const content = await session.invoke('content', {
    book: info.book,
    chapter: toc.chapters[0],
    bookUrl: info.book.bookUrl,
    chapterUrl: toc.chapters[0].chapterUrl,
  });
  assert.match(content.content, /<p>Hello<\/p><p>World<\/p>/);
  assert.deepEqual(await session.invoke('loginStatus', {}), {
    loggedIn: false,
    cookiePresent: false,
  });
  const requests = calls.filter(call => call.name === 'http.request');
  assert.equal(requests.length, 4);
  assert.equal(requests.every(call =>
    call.input.cookieProfile === 'source:fixture:1:http'), true);
  assert.deepEqual(requests[0].scope, {
    installationId: 'fixture',
    generation: 1,
    protocol: 'legado',
    siteId: 'source',
    requestId: 1,
  });
});

test('Legado WebView requests preserve absent optional method and body fields', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'web',
    source: {
      ...legadoSource,
      searchUrl: 'https://books.invalid/search,{"webView":true}',
    },
    protocol: 'legado',
    capabilities: createCapabilityClient({
      invoke(name, input, context) {
        calls.push({ name, input, scope: context.scope });
        assert.equal(name, 'web.interact');
        return '<div class="book"><h2>Web Book</h2><a href="/web">open</a></div>';
      },
    }),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const result = await session.invoke('search', { keyword: 'web' });
  assert.equal(result.books[0].name, 'Web Book');
  assert.equal(result.books[0].bookUrl, 'https://books.invalid/web');
  assert.deepEqual(calls, [{
    name: 'web.interact',
    input: {
      version: hostApiVersion, profile: 'source:web:1:web', mode: 'request',
      request: {
        mode: 'request', url: 'https://books.invalid/search', headers: {},
        sourceRegex: '', overrideUrlRegex: '',
      },
    },
    scope: {
      installationId: 'web', generation: 1, protocol: 'legado',
      siteId: 'source', requestId: 1,
    },
  }]);
});

test('Legado raw list fallback rejects unusable JSON roots and preserves real empty arrays', async t => {
  let data;
  const session = new SourceSession({
    installationId: 'legado-raw-lists', protocol: 'legado',
    source: { ...legadoSource, exploreUrl: 'https://books.invalid/list',
      ruleSearch: {}, ruleExplore: {}, ruleToc: { chapterName: '$.name', chapterUrl: '$.chapterUrl' } },
    capabilities: capabilitiesFor({ '/search': () => data, '/list': () => data }),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  for (const [action, payload, key] of [
    ['search', { keyword: 'fixture' }, 'books'],
    ['explore', { url: 'https://books.invalid/list' }, 'books'],
    ['toc', { bookUrl: 'https://books.invalid/book', tocUrl: 'https://books.invalid/list' }, 'chapters'],
  ]) {
    for (data of ['', 'not JSON', '[{"name":', '{}', 'null', 'false', '7', '"[]"']) {
      await assert.rejects(session.invoke(action, payload), { code: 'SCRIPT_PROTOCOL_ERROR' });
    }
    data = '[]';
    assert.deepEqual((await session.invoke(action, payload))[key], []);
    data = JSON.stringify([{ name: 'One', bookUrl: '/book', chapterUrl: '/chapter' }]);
    const items = (await session.invoke(action, payload))[key];
    assert.equal(items.length, 1);
    assert.equal(items[0].name, 'One');
    assert.equal(items[0][key === 'books' ? 'bookUrl' : 'chapterUrl'],
      `https://books.invalid/${key === 'books' ? 'book' : 'chapter'}`);
  }
});

test('Legado explicit list selectors can return no matches without requiring a raw JSON array', async t => {
  const session = new SourceSession({
    installationId: 'legado-empty-selectors', source: { ...legadoSource,
      exploreUrl: 'https://books.invalid/list', ruleExplore: legadoSource.ruleSearch },
    protocol: 'legado',
    capabilities: capabilitiesFor({
      '/search': '<main>No matching books</main>',
      '/list': '<main>No matching entries</main>',
    }),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  assert.deepEqual((await session.invoke('search', { keyword: 'fixture' })).books, []);
  assert.deepEqual((await session.invoke('explore', { url: 'https://books.invalid/list' })).books, []);
  assert.deepEqual((await session.invoke('toc', { tocUrl: 'https://books.invalid/list' })).chapters, []);
});

test('Legado raw TOC never fabricates chapters from missing identities', async t => {
  let data;
  const session = new SourceSession({
    installationId: 'raw-toc', protocol: 'legado',
    source: { ...legadoSource, ruleToc: { nextTocUrl: '' } },
    capabilities: capabilitiesFor({ '/toc': () => data }), createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start(); session.activate();
  const toc = () => session.invoke('toc', { bookUrl: 'https://books.invalid/book', tocUrl: 'https://books.invalid/toc' });
  for (const item of [null, {}, false, 7]) {
    data = JSON.stringify([{ name: 'One', url: '/one' }, item, { name: 'Three', url: '/three' }]);
    await assert.rejects(toc(), { code: 'INVALID_RESPONSE' });
  }
  data = JSON.stringify([{ name: 'Volume', isVolume: true }, { name: 'Single page' },
    { name: 'Next', url: '/next' }]);
  const { chapters } = await toc();
  assert.deepEqual(chapters.map(item => item.index), [0, 1, 2]);
  assert.equal(chapters[0].isVolume, true);
  assert.equal(chapters[1].chapterUrl, 'https://books.invalid/toc');
  assert.equal(chapters[2].chapterUrl, 'https://books.invalid/next');
});
test('Legado categories isolate bad rows while retaining title-only groups and URL-only entries', async t => {
  let value;
  const session = new SourceSession({
    installationId: 'legado-categories',
    source: { ...legadoSource, exploreUrl: '@js:categories()' }, protocol: 'legado',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({ evaluate: () => value }),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const healthy = [
    { title: 'Group', style: { width: 1 } },
    { name: 'One', url: '/one', extra: { future: true } },
    { url: '/two', style: 'unused' },
  ];
  const expected = { kinds: [
    { title: 'Group', url: '', style: { width: 1 } },
    { title: 'One', url: '/one', style: {} },
    { title: '', url: '/two', style: {} },
  ] };
  for (const invalid of [null, 7, false, [], {}, { title: {} }, { url: [] }]) {
    for (let index = 0; index <= healthy.length; index++) {
      value = healthy.toSpliced(index, 0, invalid);
      assert.deepEqual(await session.invoke('exploreKinds'), expected);
    }
    value = [invalid];
    await assert.rejects(session.invoke('exploreKinds'), { code: 'SCRIPT_PROTOCOL_ERROR' });
  }
  for (value of [[], '[]', '', ' \n ']) {
    assert.deepEqual(await session.invoke('exploreKinds'), { kinds: [] });
  }
  value = 'bad row\nOne::/one\n::/two\nGroup::';
  assert.deepEqual(await session.invoke('exploreKinds'), { kinds: [
    { title: 'One', url: '/one', style: {} },
    { title: '', url: '/two', style: {} },
    { title: 'Group', url: '', style: {} },
  ] });
  for (value of ['bad row', '[{"title":', {}, null, false, 7]) {
    await assert.rejects(session.invoke('exploreKinds'), { code: 'SCRIPT_PROTOCOL_ERROR' });
  }
});

test('Legado Cookie-only login status does not consume Reader variables', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'legado-cookie-status', source: legadoSource, protocol: 'legado',
    capabilities: capabilitiesFor({}, calls),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const expected = { loggedIn: false, cookiePresent: false };
  assert.deepEqual(await session.invoke('loginStatus'), expected);
  for (const variable of ['[1]', [null], { extra: ['unused'] }]) {
    assert.deepEqual(await session.invoke('loginStatus', {
      book: { variable }, chapter: { variable },
    }), expected);
  }
  assert.deepEqual(calls.map(call => call.name), Array(4).fill('cookie.get'));
  await session.drain();
  assert.equal(session.kernel.registries.size, 0);
  assert.equal(session.kernel.invocations.size, 0);
  assert.equal(session.stats().pendingAdapterTasks, 0);
});

test('portable session variables preserve long UTF-8 keys and clone their values', async t => {
  const prefix = '变量'.repeat(1024);
  const keys = [prefix, `${prefix}-other`];
  let sync;
  const session = new SourceSession({
    installationId: 'long-session-keys', protocol: 'legado',
    source: legadoSource, capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({ load(options) {
      sync = (name, input) => options.onSync(`legado.${name}`, input, {});
      for (const [index, key] of keys.entries()) {
        const value = { index, values: ['complete'] };
        sync('variableSet', { key, value });
        value.values[0] = 'changed input';
      }
    } }),
  });
  t.after(() => session.close());
  await session.start();
  for (const [index, key] of keys.entries()) {
    assert.ok(body(key).length > 4096);
    const value = sync('variableGet', { key });
    assert.deepEqual(value, { index, values: ['complete'] });
    value.values[0] = 'changed output';
    assert.deepEqual(sync('variableGet', { key }), { index, values: ['complete'] });
  }
  assert.equal(sync('variableGet', { key: `${prefix}-missing` }), null);
  await session.close();
  assert.equal(sync('variableGet', { key: keys[0] }), null);
});

test('persistent Reader values use incremental typed writes and preserve untouched exact material', async t => {
  const saved = new Map([['kept', parseJSONText('9007199254740993')]]);
  let revision = 0, variable, operation, failWrite = false;
  const base = capabilitiesFor({ '/search': '[]' });
  const session = new SourceSession({
    installationId: 'persistent-values', protocol: 'legado', metadata: { persistentSource: true },
    source: { ...legadoSource, ruleSearch: {}, coverDecodeJs: 'fixture()' },
    capabilities: createCapabilityClient({ invoke(name, input, context) {
      if (name === 'reader.values.read') return { revision,
        entries: [...saved], ...(variable === undefined ? {} : { variable }) };
      if (name === 'reader.values.apply') {
        if (failWrite) throw Object.assign(new Error(), { code: 'STORAGE_ERROR' });
        assert.equal(input.revision, revision);
        for (const [key, value] of input.entries) saved.set(key, value);
        if (input.variable !== undefined) variable = input.variable;
        return ++revision;
      }
      return base.call(name, input, context);
    } }),
    createSandbox: sandboxFactory({ evaluate(options, _code, _bindings, { context }) {
      return operation((name, input = {}) => options.onSync(`legado.${name}`, input, context));
    } }),
  });
  t.after(() => session.close());
  await session.start(); session.activate();
  assert.deepEqual((await session.invoke('search', { keyword: 'unused' })).books, []);
  operation = call => call('sourceSet', { key: 'new', value: 'new value' });
  await session.invoke('image', { body: '' });
  assert.equal(stringifyJSONValue(saved.get('kept')), '9007199254740993');
  assert.equal(revision, 1);
  operation = call => call('sourceGet', { key: 'new' });
  assert.equal((await session.invoke('image', { body: '' })).body, 'new value');
  operation = call => typeof call('sourceGet', { key: 'kept' });
  assert.equal((await session.invoke('image', { body: '' })).body, 'number');
  operation = call => call('sourceVariableGet');
  assert.equal((await session.invoke('image', { body: '' })).body, '');
  operation = call => call('sourceVariableSet', { value: 'replacement' });
  const before = [...saved];
  failWrite = true;
  await assert.rejects(session.invoke('image', { body: '' }), { code: 'STORAGE_ERROR' });
  assert.deepEqual([...saved], before);
  assert.equal(variable, undefined);
  assert.equal(revision, 1);
  failWrite = false;
  await session.invoke('image', { body: '' });
  assert.equal(variable, 'replacement');
  assert.equal(revision, 2);
  assert.equal(stringifyJSONValue(saved.get('kept')), '9007199254740993');
});

test('persistent Reader read failures never become empty state and ordinary actions stay available', async t => {
  for (const failRead of [true, false]) {
    let operation;
    const saved = new Map();
    const session = new SourceSession({
      installationId: 'reader-read-failure', protocol: 'legado', metadata: { persistentSource: true },
      source: { ...legadoSource, coverDecodeJs: 'fixture()' },
      capabilities: createCapabilityClient({ invoke(name) {
        if (name === 'reader.values.read') {
          if (failRead) throw Object.assign(new Error(), { code: 'STORAGE_ERROR' });
          return { revision: 0, entries: [], variable: 'ready' };
        }
        if (name === 'reader.values.apply') throw new Error('unexpected write');
        if (name === 'cookie.get') return '';
        throw new Error(name);
      } }),
      createSandbox: sandboxFactory({ evaluate(options, _code, _bindings, { context }) {
        return operation((name, input = {}) => options.onSync(`legado.${name}`, input, context));
      } }),
    });
    t.after(() => session.close());
    await session.start(); session.activate();
    assert.equal((await session.invoke('loginStatus')).loggedIn, false);
    operation = call => call('sourceGet', { key: 'unused' });
    if (failRead) await assert.rejects(session.invoke('image', { body: '' }), { code: 'STORAGE_ERROR' });
    else assert.equal((await session.invoke('image', { body: '' })).body, null);
    operation = call => call('sourceVariableGet');
    if (failRead) await assert.rejects(session.invoke('image', { body: '' }), { code: 'STORAGE_ERROR' });
    else assert.equal((await session.invoke('image', { body: '' })).body, 'ready');
    assert.equal(saved.size, 0);
  }
});

for (const protocol of ['legado', 'cat', 'qysg']) {
  test(`${protocol} script storage reads project numbers without replacing precise private material`, async t => {
    const exact = '{"integer":9007199254740993,"decimal":1.234567890123456789,"zero":-0,' +
      '"nested":{"kept":true},"text":"\\ud800"}';
    const reads = protocol === 'qysg' ? [
      ['bridge.call', { name: 'cache.get', args: ['key'] }, 'key'],
      ['bridge.call', { name: 'cacheGet', args: ['key'] }, 'key'],
      ['bridge.call', { name: 'cache.allget', args: ['key'] }, 'all:key'],
      ['bridge.call', { name: 'getLoginInfo', args: [] }, 'loginInfo'],
      ['bridge.call', { name: 'getbookVariable', args: ['key'] }, 'book:key'],
    ] : [[`${protocol}.storageGet`, { key: 'key' }, 'key']];
    const stored = new Map(reads.map(([, , key]) => [key, parseJSONText(exact)]));
    const readerValues = new Map([['kept', parseJSONText('9007199254740993')]]);
    let readerRevision = 0, sourceVariable = 'old';
    let failRead = false;
    const exercise = async (sandbox, _method, _payload, options) => {
      const context = { ...options.context, signal: options.signal };
      for (const [name, input, key] of reads) {
        const value = await sandbox.onCapability(name, input, context);
        assert.deepEqual(value, JSON.parse(exact));
        assert.equal(typeof value.integer, 'number');
        value.nested.kept = false;
        assert.equal(stringifyJSONValue(stored.get(key)), exact);
        assert.deepEqual(await sandbox.onCapability(name, input, context), JSON.parse(exact));
      }
      await sandbox.onCapability(protocol === 'qysg' ? 'bridge.call' : `${protocol}.storageSet`,
        protocol === 'qysg' ? { name: 'cache.set', args: ['other', 'updated'] }
          : { key: 'other', value: 'updated' }, context);
      if (protocol === 'legado') sandbox.onSync('legado.sourceVariableSet', { value: 'updated' }, context);
      return 'read';
    };
    const session = new SourceSession({
      installationId: `projected-${protocol}`, protocol, metadata: { persistentSource: true },
      source: protocol === 'legado' ? { ...legadoSource, coverDecodeJs: 'fixture()' }
        : protocol === 'cat' ? 'export default { searchContent() {} }'
          : { bookSourceUrl: 'https://books.invalid/', html: '<script>function imagedecrypt() {}</script>' },
      capabilities: createCapabilityClient({ invoke(name, input) {
        if (name === 'reader.values.read') return { revision: readerRevision, entries: [...readerValues], variable: sourceVariable };
        if (name === 'reader.values.apply') {
          assert.equal(input.revision, readerRevision);
          for (const [key, value] of input.entries) readerValues.set(key, value);
          if (input.variable !== undefined) sourceVariable = input.variable;
          return ++readerRevision;
        }
        if (name === 'storage.get') {
          if (failRead) throw Object.assign(new Error(), { code: 'STORAGE_ERROR' });
          return stored.get(input.key) ?? null;
        }
        assert.equal(name, 'storage.set');
        stored.set(input.key, input.value);
        return true;
      } }),
      createSandbox: sandboxFactory({
        actions: protocol === 'cat' ? ['search'] : protocol === 'qysg' ? ['imagedecrypt'] : ['image'],
        evaluate: exercise, invoke: exercise,
      }),
    });
    t.after(() => session.close());
    await session.start(); session.activate();
    const action = protocol === 'cat' ? 'search' : 'image';
    const result = await session.invoke(action, { body: '' });
    assert.equal(protocol === 'cat' ? result : result.body, 'read');
    for (const [, , key] of reads) assert.equal(stringifyJSONValue(stored.get(key)), exact);
    assert.equal(stored.get('other'), 'updated');
    if (protocol === 'legado') {
      assert.equal(sourceVariable, 'updated');
      assert.equal(stringifyJSONValue(readerValues.get('kept')), '9007199254740993');
    }
    const before = stringifyJSONValue([...stored]);
    failRead = true;
    await assert.rejects(session.invoke(action, { body: '' }), { code: 'STORAGE_ERROR' });
    assert.equal(stringifyJSONValue([...stored]), before);
  });
}

test('Legado login checks import variables when the script actually reads them', async t => {
  const session = new SourceSession({
    installationId: 'legado-variable-status', source: { ...legadoSource,
      loginCheckJs: '@js:java.get("token")' }, protocol: 'legado',
    capabilities: capabilitiesFor({ '/': 'status' }),
    createSandbox: sandboxFactory({
      evaluate(options, _code, _bindings, { context }) {
        return options.onSync('legado.variableGet', { key: 'token' }, context);
      },
    }),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  assert.deepEqual(await session.invoke('loginStatus', {
    book: { variable: JSON.stringify({ token: 'present' }) },
  }), { loggedIn: true, value: 'present' });
  await assert.rejects(session.invoke('loginStatus', {
    book: { variable: '[1]' },
  }), { code: 'SCRIPT_EXCEPTION' });
  assert.deepEqual(await session.invoke('loginStatus'), { loggedIn: false, value: null });
  await session.drain();
  assert.equal(session.kernel.registries.size, 0);
  assert.equal(session.kernel.invocations.size, 0);
});

test('current Legado definitions keep unrelated keys without interpreting them', () => {
  const source = { ...legadoSource, enable: false, serialNumber: -7,
    ruleSearchList: 'not a current rule', ruleFindUrl: 'not an explore URL' };
  assert.equal(detectSourceProtocol(source), 'legado');
  assert.deepEqual(normalizeSourceDefinition(source),
    { protocol: 'legado', importedProtocol: 'legado', source });
});

test('QYSG protocol detection accepts case-insensitive script markup', () => {
  const html = '<HTML><BODY><SCRIPT>function search(){ return []; }</SCRIPT></BODY></HTML>';
  assert.equal(detectSourceProtocol({
    bookSourceName: 'QYSG',
    html,
  }), 'qysg');
  assert.equal(normalizeSourceDefinition(html, 'qysg').source.html, html);
  assert.equal(normalizeSourceDefinition(JSON.stringify({
    bookSourceName: 'QYSG',
    html,
  }), 'qysg').source.html, html);
  assert.equal(normalizeSourceDefinition(html).protocol, 'qysg');
  assert.equal(normalizeSourceDefinition(JSON.stringify(html)).source.html, html);
  assert.equal(detectSourceProtocol({
    bookSourceName: 'Remote QYSG',
    bookSourceUrl: 'https://books.invalid/',
    html: 'https://rules.invalid/one.html  https://rules.invalid/two.html',
    enabled: true,
  }), 'qysg');
  assert.equal(normalizeSourceDefinition(
    'export default { searchContent(){ return { list: [] }; } };',
  ).protocol, 'cat');
});

test('QYSG remote definitions preserve HTTP URL credentials through import and the capability ABI', async t => {
  const url = 'https://reader:synthetic-only@rules.invalid/definition.html?version=1';
  const source = { bookSourceName: 'Remote QYSG', bookSourceUrl: 'https://books.invalid/', html: url };
  assert.equal(detectSourceProtocol(source), 'qysg');
  assert.equal(normalizeSourceDefinition(source).source.html, url);
  const calls = [];
  let loaded;
  const session = new SourceSession({
    installationId: 'qysg-url-credentials', source, protocol: 'qysg',
    capabilities: capabilitiesFor({
      '/definition.html': '<script>function search(){ return []; }</script>',
    }, calls),
    createSandbox: sandboxFactory({
      load(_options, _kind, code) { loaded = code; },
    }),
  });
  t.after(() => session.close());
  await session.start();
  assert.match(loaded, /function search\(\)/);
  assert.equal(calls.filter(call => call.name === 'http.request').length, 1);
  assert.equal(calls.find(call => call.name === 'http.request').input.url, url);
});

test('pagination follows all distinct pages and stops at a repeated URL', async t => {
  const source = {
    ...legadoSource,
    ruleToc: {
      ...legadoSource.ruleToc,
      nextTocUrl: '.next@href',
    },
  };
  const session = new SourceSession({
    installationId: 'pages',
    source,
    protocol: 'legado',
    capabilities: capabilitiesFor({
      '/book/1/toc': `
        <ul><li><a href="/chapter/1">Chapter One</a></li></ul>
        <a class="next" href="/book/1/toc?page=2">next</a>`,
    }),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const result = await session.invoke('toc', {
    bookUrl: 'https://books.invalid/book/1',
    tocUrl: 'https://books.invalid/book/1/toc',
  });
  assert.equal(result.chapters.length, 1);
  assert.equal(result.chapters[0].name, 'Chapter One');
});

test('TOC aliases skip already processed final pages while preserving first-page HTML reuse', async t => {
  for (const cached of [false, true]) await t.test(cached ? 'cached first page' : 'requested first page', async t => {
    const first = '<li><a>Volume One</a><i class="volume">true</i></li>' +
      '<li><a href="/chapter/one">Chapter One</a></li>' +
      '<a class="next" href="/toc-alias">repeat</a>' +
      '<a class="next" href="/toc-two-alias">second</a>' +
      '<a class="next" href="/nested/toc-two">same second page</a>';
    const calls = [];
    const session = new SourceSession({
      installationId: 'toc-final-pages',
      source: { ...legadoSource, ruleToc: { ...legadoSource.ruleToc,
        nextTocUrl: '.next@href', isVolume: '.volume@text' } },
      protocol: 'legado',
      capabilities: capabilitiesFor({
        '/book/1/toc': first,
        '/toc-alias': { url: 'https://books.invalid/book/1/toc', data: first },
        '/toc-two-alias': { url: 'https://books.invalid/nested/toc-two',
          data: '<li><a>Volume Two</a><i class="volume">true</i></li>' +
            '<li><a href="chapter/two">Chapter Two</a></li>' +
            '<a class="next" href="/book/1/toc">cycle</a>' },
      }, calls),
      createSandbox: sandboxFactory(),
    });
    t.after(() => session.close());
    await session.start();
    session.activate();
    const result = await session.invoke('toc', { book: {
      bookUrl: 'https://books.invalid/book/1', tocUrl: 'https://books.invalid/book/1/toc',
      ...(cached ? { tocHtml: first } : {}),
    } });
    assert.deepEqual(result.chapters.map(chapter => chapter.name),
      ['Volume One', 'Chapter One', 'Volume Two', 'Chapter Two']);
    assert.deepEqual(result.chapters.map(chapter => chapter.isVolume), [true, false, true, false]);
    assert.deepEqual(result.chapters.map(chapter => chapter.index), [0, 1, 2, 3]);
    assert.equal(result.chapters[3].chapterUrl, 'https://books.invalid/nested/chapter/two');
    assert.deepEqual(calls.filter(call => call.name === 'http.request')
      .map(call => new URL(call.input.url).pathname),
    [...(cached ? [] : ['/book/1/toc']), '/toc-alias', '/toc-two-alias']);
  });
});

test('content aliases cannot repeat final pages or append the next chapter', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'content-final-pages',
    source: { ...legadoSource, ruleContent: { ...legadoSource.ruleContent,
      title: 'h1@text', nextContentUrl: '.next@href' } },
    protocol: 'legado',
    capabilities: capabilitiesFor({
      '/chapter/one': '<h1>First title</h1><article><p>First</p></article>' +
        '<a class="next" href="/content-alias">repeat</a>' +
        '<a class="next" href="/second-alias">second</a>' +
        '<a class="next" href="/nested/second">same second page</a>' +
        '<a class="next" href="/chapter/two">next chapter</a>' +
        '<a class="next" href="/next-chapter-alias">redirect to next chapter</a>',
      '/content-alias': { url: 'https://books.invalid/chapter/one',
        data: '<h1>Duplicate title</h1><article>Duplicate</article>' },
      '/second-alias': { url: 'https://books.invalid/nested/second',
        data: '<article><p>Second</p><img src="picture.png" alt="Page image"><p>Third</p></article>' +
          '<a class="next" href="/chapter/one">cycle</a>' },
      '/next-chapter-alias': { url: 'https://books.invalid/chapter/two',
        data: '<h1>Next chapter title</h1><article>Next chapter</article>' },
    }, calls),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const result = await session.invoke('content', {
    bookUrl: 'https://books.invalid/book/1', chapterUrl: 'https://books.invalid/chapter/one',
    nextChapterUrl: 'https://books.invalid/chapter/two',
  });
  assert.equal(result.title, 'First title');
  assert.equal(result.chapterUrl, 'https://books.invalid/chapter/one');
  assert.deepEqual(result.blocks, [
    { type: 'text', text: 'First' }, { type: 'text', text: 'Second' },
    { type: 'image', src: 'https://books.invalid/nested/picture.png', alt: 'Page image' },
    { type: 'text', text: 'Third' },
  ]);
  assert.deepEqual(result.nextUrls, ['https://books.invalid/chapter/one',
    'https://books.invalid/nested/second']);
  assert.deepEqual(calls.filter(call => call.name === 'http.request')
    .map(call => new URL(call.input.url).pathname),
  ['/chapter/one', '/content-alias', '/second-alias', '/next-chapter-alias']);
});

test('TOC and content retain distinct POST pages at one final URL after options and script rendering', async t => {
  for (const action of ['toc', 'content']) await t.test(action, async t => {
    const endpoint = 'https://books.invalid/pages';
    const post = '/pages,' + JSON.stringify({ method: 'POST', body: 'page={{page}}' });
    const calls = [];
    let renderedRequests = 0;
    const session = new SourceSession({
      installationId: 'post-pages',
      source: { ...legadoSource,
        jsLib: `function nextRequest() {
          const page = Math.min(4, Number(java.getVariable('requestPage') || 2) + 1);
          java.put('requestPage', page);
          return { url: '${endpoint}', body: 'page=' + page };
        }`,
        ruleToc: { ...legadoSource.ruleToc, nextTocUrl: '.next@href' },
        ruleContent: { ...legadoSource.ruleContent, nextContentUrl: '.next@href' },
      },
      protocol: 'legado',
      capabilities: capabilitiesFor({
        '/pages': input => {
          const text = new TextDecoder().decode(input.body);
          const page = input.method === 'GET' ? 1 : Number(text.slice('page='.length));
          assert.ok([1, 2, 3, 4].includes(page));
          if (page !== 1) assert.equal(text, `page=${page}`);
          const content = action === 'toc'
            ? `<li><a href="/chapter/${page}">Page ${page}</a></li>`
            : `<article><p>Page ${page}</p></article>`;
          const next = page === 1 ? post : '@js:nextRequest()';
          return content + `<a class="next" href='${next}'>next</a>` +
            (page === 4 ? '<a class="next" href="/pages">cycle</a>' : '');
        },
      }, calls),
      createSandbox: sandboxFactory({
        evaluate(_options, code) {
          assert.match(code, /nextRequest\(\)/);
          return { url: endpoint, body: `page=${Math.min(4, 2 + ++renderedRequests)}` };
        },
      }),
    });
    t.after(() => session.close());
    await session.start();
    session.activate();
    const result = await session.invoke(action, {
      bookUrl: 'https://books.invalid/book/1', page: 2,
      ...(action === 'toc' ? { tocUrl: endpoint } : { chapterUrl: endpoint }),
    });
    if (action === 'toc') {
      assert.deepEqual(result.chapters.map(chapter => chapter.name),
        ['Page 1', 'Page 2', 'Page 3', 'Page 4']);
    } else {
      assert.deepEqual(result.blocks, [
        { type: 'text', text: 'Page 1' }, { type: 'text', text: 'Page 2' },
        { type: 'text', text: 'Page 3' }, { type: 'text', text: 'Page 4' },
      ]);
    }
    assert.equal(renderedRequests, 3);
    assert.deepEqual(calls.filter(call => call.name === 'http.request').map(call =>
      [call.input.url, call.input.method, new TextDecoder().decode(call.input.body)]),
    [[endpoint, 'GET', ''], [endpoint, 'POST', 'page=2'],
      [endpoint, 'POST', 'page=3'], [endpoint, 'POST', 'page=4']]);
  });
});

test('variable parsing failures retain no invocation resources and allow later content calls', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'variable-parse-cleanup', source: legadoSource, protocol: 'legado',
    capabilities: capabilitiesFor({ '/chapter/one': '<article><p>Recovered</p></article>' }, calls),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  for (const field of ['book', 'chapter']) {
    const requests = calls.filter(call => call.name === 'http.request').length;
    await assert.rejects(session.invoke('content', {
      chapterUrl: 'https://books.invalid/chapter/one', [field]: { variable: '[1]' },
    }), { code: 'SCRIPT_EXCEPTION' });
    await session.drain();
    assert.equal(session.kernel.registries.size, 0);
    assert.equal(session.kernel.invocations.size, 0);
    assert.equal(session.stats().pendingAdapterTasks, 0);
    assert.equal(calls.filter(call => call.name === 'http.request').length, requests);

    const result = await session.invoke('content', {
      chapterUrl: 'https://books.invalid/chapter/one',
      book: { variable: JSON.stringify({ fromBook: 'book' }) },
      chapter: { variable: JSON.stringify({ fromChapter: 'chapter' }) },
    });
    assert.deepEqual(result.blocks, [{ type: 'text', text: 'Recovered' }]);
    assert.deepEqual(JSON.parse(result.chapter.variable), { fromBook: 'book', fromChapter: 'chapter' });
    await session.drain();
    assert.equal(session.kernel.registries.size, 0);
    assert.equal(session.kernel.invocations.size, 0);
    assert.equal(session.stats().pendingAdapterTasks, 0);
  }
});

test('QYSG combined script bytes remain inside the source budget', async () => {
  const session = new SourceSession({
    installationId: 'qysg-limit',
    source: {
      bookSourceName: 'QYSG limit',
      bookSourceUrl: 'https://books.invalid/',
      html: `<script>function search(){return []};${'x'.repeat(256)}</script>`,
    },
    protocol: 'qysg',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory(),
    limits: { maximumParseBytes: 128 },
  });
  await assert.rejects(session.start(), { code: 'RESOURCE_LIMIT' });
  await session.close();
});

test('QYSG fetches CryptoJS only when a source needs an unbundled algorithm', async t => {
  const calls = [];
  const session = new SourceSession({
    installationId: 'qysg-external-crypto',
    source: {
      bookSourceName: 'QYSG external crypto',
      bookSourceUrl: 'https://books.invalid/',
      html: `
        <script src="https://books.invalid/crypto-js.min.js"></script>
        <script>
          const algorithm = CryptoJS.TripleDES;
          function search() { return algorithm ? [] : []; }
        </script>`,
    },
    protocol: 'qysg',
    capabilities: capabilitiesFor({
      '/crypto-js.min.js': 'module.exports = { DES: {} };',
    }, calls),
    createSandbox: sandboxFactory(),
  });
  t.after(() => session.close());
  await session.start();
  assert.equal(calls.filter(call => call.name === 'http.request').length, 1);
});

test('QYSG ignores known bt-stats telemetry scripts', async t => {
  const calls = [];
  let loaded;
  const session = new SourceSession({
    installationId: 'qysg-bt-stats',
    source: {
      bookSourceName: 'QYSG bt-stats',
      bookSourceUrl: 'https://books.invalid/',
      html: `
        <script defer src="/bt-stats.js?site=fixture&amp;version1"></script>
        <script>function search(){ return []; }</script>
      `,
    },
    protocol: 'qysg',
    capabilities: capabilitiesFor({}, calls),
    createSandbox: sandboxFactory({
      actions: ['search'],
      load: async (_options, _kind, source) => { loaded = source; },
    }),
  });
  t.after(() => session.close());
  await session.start();
  assert.equal(calls.length, 0);
  assert.doesNotMatch(loaded, /bt-stats/);
  assert.match(loaded, /function search/);
});

test('QYSG remote HTML retries bounded candidates and resolves scripts from the final response URL', async t => {
  const calls = [];
  let loaded;
  const capabilities = createCapabilityClient({
    async invoke(name, input, context = {}) {
      assert.equal(name, 'http.request');
      calls.push({
        url: input.url,
        scope: structuredClone(context.scope),
      });
      const url = new URL(input.url);
      if (url.href === 'https://first.invalid/source.html') {
        return {
          version: hostApiVersion,
          status: 200,
          statusText: 'OK',
          headers: { 'content-type': ['text/html; charset=utf-8'] },
          url: url.href,
          body: body(`
            <script src="./missing.js"></script>
            <script>function search(){ return []; }</script>
          `),
        };
      }
      if (url.href === 'https://first.invalid/missing.js') {
        return {
          version: hostApiVersion,
          status: 404,
          statusText: 'Not Found',
          headers: { 'content-type': ['application/javascript; charset=utf-8'] },
          url: url.href,
          body: body('missing'),
        };
      }
      if (url.hostname === 'second.invalid') {
        return {
          version: hostApiVersion,
          status: 200,
          statusText: 'OK',
          headers: { 'content-type': ['text/html; charset=utf-8'] },
          url: 'https://cdn.invalid/rules/source.html',
          body: body(`
            <main id="fixture">Ready</main>
            <script src="./helper.js"></script>
            <script>function search(){ return helper(); }</script>
          `),
        };
      }
      if (url.href === 'https://cdn.invalid/rules/helper.js') {
        return {
          version: hostApiVersion,
          status: 200,
          statusText: 'OK',
          headers: { 'content-type': ['application/javascript; charset=utf-8'] },
          url: url.href,
          body: body('function helper(){ return []; }'),
        };
      }
      throw Object.assign(new Error(), { code: 'NETWORK_ERROR' });
    },
  });
  const session = new SourceSession({
    installationId: 'qysg-remote',
    source: {
      bookSourceName: 'QYSG remote',
      bookSourceUrl: 'https://books.invalid/',
      html: 'https://first.invalid/source.html https://second.invalid/source.html',
    },
    capabilities,
    createSandbox: sandboxFactory({
      actions: ['search'],
      load: async (_options, kind, source, metadata) => {
        loaded = { kind, source, metadata };
      },
    }),
  });
  t.after(() => session.close());
  assert.deepEqual(await session.start(), {
    protocol: 'qysg',
    importedProtocol: 'qysg',
    actions: ['search', 'loginStatus', 'image'],
  });
  assert.deepEqual(calls.map(call => call.url), [
    'https://first.invalid/source.html',
    'https://first.invalid/missing.js',
    'https://second.invalid/source.html',
    'https://cdn.invalid/rules/helper.js',
  ]);
  assert.equal(calls.every(call => call.scope.requestId === 0), true);
  assert.equal(loaded.kind, 'qysg');
  assert.equal(loaded.metadata.baseUrl, 'https://cdn.invalid/rules/source.html');
  assert.match(loaded.source, /function helper/);
  assert.match(loaded.source, /function search/);
});

test('QYSG remote HTML candidate count and startup lifetime are bounded', async () => {
  let calls = 0;
  const limited = new SourceSession({
    installationId: 'qysg-candidate-limit',
    source: {
      bookSourceName: 'QYSG candidate limit',
      bookSourceUrl: 'https://books.invalid/',
      html: 'https://one.invalid/source.html https://two.invalid/source.html',
    },
    capabilities: createCapabilityClient({
      async invoke() {
        calls++;
        throw Object.assign(new Error(), { code: 'NETWORK_ERROR' });
      },
    }),
    createSandbox: sandboxFactory(),
    limits: { maximumSourceCandidates: 1 },
  });
  await assert.rejects(limited.start(), { code: 'RESOURCE_LIMIT' });
  assert.equal(calls, 0);
  await limited.close();

  const timed = new SourceSession({
    installationId: 'qysg-startup-timeout',
    source: {
      bookSourceName: 'QYSG startup timeout',
      bookSourceUrl: 'https://books.invalid/',
      html: 'https://timeout.invalid/source.html',
    },
    capabilities: createCapabilityClient({
      async invoke(_name, _input, context) {
        return await new Promise((_resolve, reject) => {
          const abort = () => reject(context.signal.reason);
          if (context.signal.aborted) abort();
          else context.signal.addEventListener('abort', abort, { once: true });
        });
      },
    }),
    createSandbox: sandboxFactory(),
    limits: { requestTimeoutMs: 25 },
  });
  await assert.rejects(timed.start(), { code: 'TIMEOUT' });
  await timed.close();
  assert.equal(timed.stats().pendingAdapterTasks, 0);
});

test('QYSG remote HTML reports exhausted HTTP candidates as an upstream HTTP failure', async () => {
  const session = new SourceSession({
    installationId: 'qysg-http-failure',
    source: {
      bookSourceName: 'QYSG HTTP failure',
      bookSourceUrl: 'https://books.invalid/',
      html: 'https://one.invalid/source.html https://two.invalid/source.html',
    },
    capabilities: createCapabilityClient({
      async invoke(_name, input) {
        return {
          version: hostApiVersion,
          status: 404,
          statusText: 'Not Found',
          headers: { 'content-type': ['text/html; charset=utf-8'] },
          url: input.url,
          body: body('missing'),
        };
      },
    }),
    createSandbox: sandboxFactory(),
  });
  await assert.rejects(session.start(), { code: 'HTTP_ERROR' });
  await session.close();
});

test('QYSG normalizes result URLs against the final page and action URLs', async t => {
  const books = [
    { name: 'Root', bookUrl: '/book/1', coverUrl: '/cover.jpg', tocUrl: '/toc/1' },
    { name: 'Relative', bookUrl: './book/2', coverUrl: './cover.jpg', tocUrl: './toc/2' },
    {
      name: 'Absolute',
      bookUrl: 'https://other.invalid/book/3',
      coverUrl: 'https://images.invalid/cover.jpg',
      tocUrl: 'https://other.invalid/toc/3',
    },
    { name: 'Empty', bookUrl: '', coverUrl: '', tocUrl: '' },
  ];
  const bookUrls = value => [value.bookUrl, value.coverUrl, value.tocUrl];
  for (const remote of [false, true]) {
    const finalPage = remote
      ? 'https://cdn.invalid/rules/source.html'
      : 'https://books.invalid/base/';
    const session = new SourceSession({
      installationId: remote ? 'qysg-result-remote' : 'qysg-result-inline',
      source: {
        bookSourceName: 'QYSG result URLs',
        bookSourceUrl: 'https://books.invalid/base/',
        html: remote
          ? 'https://rules.invalid/source.html'
          : '<script>function search(){return []}</script>',
      },
      protocol: 'qysg',
      capabilities: createCapabilityClient({
        async invoke(name, input) {
          assert.equal(name, 'http.request');
          assert.equal(input.url, 'https://rules.invalid/source.html');
          return {
            version: hostApiVersion,
            status: 200,
            statusText: 'OK',
            headers: { 'content-type': ['text/html; charset=utf-8'] },
            url: finalPage,
            body: body('<script>function search(){return []}</script>'),
          };
        },
      }),
      createSandbox: sandboxFactory({
        actions: ['search', 'find', 'info', 'chapter'],
        async invoke(_sandbox, action) {
          if (action === 'search' || action === 'find') return { list: books };
          if (action === 'info') {
            return {
              name: 'Info', bookUrl: './details', coverUrl: './cover.jpg', tocUrl: './toc',
            };
          }
          if (action === 'chapter') {
            return [
              { name: 'Root', chapterUrl: '/chapter/1' },
              { name: 'Relative', chapterId: './chapter/2' },
              { name: 'Absolute', url: 'https://other.invalid/chapter/3' },
              { name: 'Empty', chapterUrl: '' },
            ];
          }
          assert.fail(`unexpected guest action: ${action}`);
        },
      }),
    });
    t.after(() => session.close());
    await session.start();
    session.activate();
    const pageOrigin = new URL(finalPage).origin;
    const pageDirectory = remote ? `${pageOrigin}/rules` : `${pageOrigin}/base`;
    const search = await session.invoke('search', { keyword: 'fixture' });
    assert.deepEqual(search.books.map(bookUrls), [
      [`${pageOrigin}/book/1`, `${pageOrigin}/cover.jpg`, `${pageOrigin}/toc/1`],
      [`${pageDirectory}/book/2`, `${pageDirectory}/cover.jpg`, `${pageDirectory}/toc/2`],
      bookUrls(books[2]),
      ['', '', ''],
    ]);

    const explore = await session.invoke('explore', {
      url: 'https://catalog.invalid/lists/index.html', page: 2,
    });
    assert.equal(explore.page, 2);
    assert.deepEqual(explore.books.map(bookUrls), [
      ['https://catalog.invalid/book/1', 'https://catalog.invalid/cover.jpg',
        'https://catalog.invalid/toc/1'],
      ['https://catalog.invalid/lists/book/2', 'https://catalog.invalid/lists/cover.jpg',
        'https://catalog.invalid/lists/toc/2'],
      bookUrls(books[2]),
      ['', '', ''],
    ]);
    const relativeExplore = await session.invoke('explore', { url: './lists/index.html' });
    assert.deepEqual(bookUrls(relativeExplore.books[1]), [
      `${pageDirectory}/lists/book/2`, `${pageDirectory}/lists/cover.jpg`,
      `${pageDirectory}/lists/toc/2`,
    ]);

    const info = await session.invoke('bookInfo', {
      bookUrl: 'https://catalog.invalid/series/one/index.html',
    });
    assert.deepEqual(bookUrls(info.book), [
      'https://catalog.invalid/series/one/details', 'https://catalog.invalid/series/one/cover.jpg',
      'https://catalog.invalid/series/one/toc',
    ]);
    const toc = await session.invoke('toc', {
      bookUrl: 'https://catalog.invalid/series/one/index.html',
      tocUrl: './toc/index.html',
    });
    assert.deepEqual(toc.chapters.map(chapter => chapter.chapterUrl), [
      'https://catalog.invalid/chapter/1',
      'https://catalog.invalid/series/one/toc/chapter/2',
      'https://other.invalid/chapter/3',
      '',
    ]);
    const bookToc = await session.invoke('toc', {
      book: { bookUrl: 'https://catalog.invalid/series/one/index.html' },
    });
    assert.equal(bookToc.chapters[1].chapterUrl,
      'https://catalog.invalid/series/one/chapter/2');
    await session.close();
  }
});

test('QYSG list contracts report protocol errors without changing non-list string actions', async t => {
  let value;
  const session = new SourceSession({
    installationId: 'qysg-list-contracts',
    source: { bookSourceName: 'Lists', bookSourceUrl: 'https://books.invalid/',
      html: '<script>function search() { return []; }</script>' },
    protocol: 'qysg',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({
      actions: ['search', 'getfinds', 'find', 'chapter', 'content'],
      invoke: async () => value,
    }),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  for (const [action, key] of [
    ['search', 'books'], ['exploreKinds', 'kinds'], ['explore', 'books'], ['toc', 'chapters'],
  ]) {
    for (value of [undefined, null, false, 7, '', 'plain text', '[1,', '{"list":',
      {}, { [key]: {} }, { list: '[]' }]) {
      await assert.rejects(session.invoke(action, {}), { code: 'SCRIPT_PROTOCOL_ERROR' },
        `${action}: ${String(value)}`);
    }
    for (value of [[], '[]', { [key]: [] }, { list: [] },
      JSON.stringify({ [key]: [] }), '{"list":[]}']) {
      assert.deepEqual((await session.invoke(action, {}))[key], []);
    }
  }
  for (value of ['plain chapter text', '[1,']) {
    assert.deepEqual(await session.invoke('content', {}), {
      title: '', chapter: {}, content: value, blocks: [{ type: 'text', text: value }], imageStyle: '',
    });
  }
});

test('QYSG rejects an entire directory with a bad chapter and keeps valid chapter array positions', async t => {
  let value;
  const session = new SourceSession({
    installationId: 'qysg-directory-positions',
    source: { bookSourceName: 'Directory', bookSourceUrl: 'https://books.invalid/',
      html: '<script>function chapter() { return []; }</script>' },
    protocol: 'qysg',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({ actions: ['chapter'], invoke: () => value }),
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  const healthy = [
    { name: 'Volume', chapterUrl: '', isVolume: true, index: 90 },
    { name: 'One', chapterUrl: '/one', index: 90 },
    { name: 'Two', chapterUrl: '/two', index: -1 },
  ];
  for (const invalid of [null, false, 7, [], {}, '[{"name":', { name: {} },
    { name: 'One', title: {} }, { chapterUrl: [] }]) {
    for (let index = 0; index <= healthy.length; index++) {
      value = healthy.toSpliced(index, 0, invalid);
      const before = structuredClone(value);
      await assert.rejects(session.invoke('toc'), { code: 'SCRIPT_PROTOCOL_ERROR' });
      assert.deepEqual(value, before);
    }
    value = [invalid];
    await assert.rejects(session.invoke('toc'), { code: 'SCRIPT_PROTOCOL_ERROR' });
  }
  for (value of [healthy, { chapters: healthy }, { list: healthy }, JSON.stringify(healthy),
    healthy.map(item => JSON.stringify(item))]) {
    const { chapters } = await session.invoke('toc');
    assert.deepEqual(chapters.map(chapter => [chapter.name, chapter.index, chapter.chapterUrl]), [
      ['Volume', 0, ''], ['One', 1, 'https://books.invalid/one'], ['Two', 2, 'https://books.invalid/two'],
    ]);
  }
  value = [];
  assert.deepEqual(await session.invoke('toc'), { chapters: [] });
  await session.drain();
  assert.equal(session.kernel.registries.size, 0);
  assert.equal(session.kernel.invocations.size, 0);
  assert.equal(session.stats().pendingAdapterTasks, 0);
});

test('QYSG external script HTTP failures retain the upstream HTTP error', async () => {
  const session = new SourceSession({
    installationId: 'qysg-script-http-failure',
    source: {
      bookSourceName: 'QYSG script HTTP failure',
      bookSourceUrl: 'https://books.invalid/',
      html: `
        <script src="./missing.js"></script>
        <script>function search(){ return []; }</script>
      `,
    },
    protocol: 'qysg',
    capabilities: createCapabilityClient({
      async invoke(_name, input) {
        return {
          version: hostApiVersion,
          status: 404,
          statusText: 'Not Found',
          headers: { 'content-type': ['application/javascript; charset=utf-8'] },
          url: input.url,
          body: body('missing'),
        };
      },
    }),
    createSandbox: sandboxFactory(),
  });
  await assert.rejects(session.start(), { code: 'HTTP_ERROR' });
  await session.close();
});

test('guest DOM results share one cumulative node budget', async t => {
  const session = new SourceSession({
    installationId: 'result-dom-budget',
    source: {
      bookSourceName: 'DOM result budget',
      bookSourceUrl: 'https://books.invalid/',
      coverDecodeJs: 'result',
    },
    protocol: 'legado',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({
      evaluate: async () => [
        { __meowMarkup: '<i>one</i>' },
        { __meowMarkup: '<i>two</i>' },
      ],
    }),
    limits: { maximumNodes: 5, maximumParseBytes: 1024 },
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  await assert.rejects(session.invoke('image', {
    url: 'https://books.invalid/image',
    body: 'fixture',
  }), { code: 'RESOURCE_LIMIT' });
});

test('QYSG htmlToText uses the session node budget', async t => {
  const session = new SourceSession({
    installationId: 'qysg-node-budget',
    source: {
      bookSourceName: 'QYSG node budget',
      bookSourceUrl: 'https://books.invalid/',
      html: '<script>function search(){ return []; }</script>',
    },
    protocol: 'qysg',
    capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({
      actions: ['search'],
      invoke: async (sandbox, _action, _payload, options) =>
        sandbox.onCapability('bridge.call', {
          name: 'htmlToText',
          args: ['<i></i>'.repeat(32)],
        }, {
          ...options.context,
          signal: options.signal,
        }),
    }),
    limits: { maximumNodes: 8, maximumParseBytes: 4096 },
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  await assert.rejects(session.invoke('search', { keyword: 'fixture' }), {
    code: 'RESOURCE_LIMIT',
  });
});

test('virtual route registration failure rolls back the complete action batch', async () => {
  const calls = [];
  let failSecond = true;
  const capabilities = createCapabilityClient({
    async invoke(name, input) {
      calls.push([name, input.route ?? '']);
      if (name === 'virtual.register') {
        if (failSecond && input.route === 'route-2') {
          failSecond = false;
          throw Object.assign(new Error(), { code: 'CAPABILITY_UNAVAILABLE' });
        }
        return { url: `meow-source://${encodeURIComponent(input.profile)}/${input.route}` };
      }
      if (name === 'virtual.unregister') return true;
      throw Object.assign(new Error(), { code: 'CAPABILITY_UNAVAILABLE' });
    },
  });
  const session = new SourceSession({
    installationId: 'route-transaction',
    source: {
      script: 'export default {};',
    },
    protocol: 'cat',
    capabilities,
    createSandbox: sandboxFactory({
      actions: ['play'],
      invoke: async (sandbox, action, _payload, options) => {
        assert.equal(action, 'play');
        const context = { ...options.context, signal: options.signal };
        return {
          urls: [
            sandbox.onSync('cat.virtualUrl', {
              siteKey: 'fixture',
              url: 'https://media.invalid/one',
              headers: {},
            }, context),
            sandbox.onSync('cat.virtualUrl', {
              siteKey: 'fixture',
              url: 'https://media.invalid/two',
              headers: {},
            }, context),
          ],
        };
      },
    }),
  });
  await session.start();
  session.activate();
  await assert.rejects(session.invoke('play', {}), {
    code: 'CAPABILITY_UNAVAILABLE',
  });
  assert.deepEqual(calls, [
    ['virtual.register', 'route-1'],
    ['virtual.register', 'route-2'],
    ['virtual.unregister', 'route-1'],
    ['virtual.unregister', 'route-2'],
  ]);

  const value = await session.invoke('play', {});
  assert.equal(value.urls.length, 2);
  assert.deepEqual(calls.slice(4), [
    ['virtual.register', 'route-3'],
    ['virtual.register', 'route-4'],
  ]);
  await session.close();
  assert.deepEqual(calls.slice(6), [
    ['virtual.unregister', 'route-3'],
    ['virtual.unregister', 'route-4'],
  ]);
});

test('cancel, timeout and close fence logical results while retaining physical cleanup debt', async () => {
  const pending = [];
  const capabilities = createCapabilityClient({
    async invoke(name, input) {
      if (name !== 'http.request') {
        if (name === 'cookie.get') return '';
        throw Object.assign(new Error(), { code: 'CAPABILITY_UNAVAILABLE' });
      }
      return await new Promise(resolve => pending.push(() => resolve({
        version: hostApiVersion,
        status: 200,
        statusText: 'OK',
        headers: { 'content-type': ['text/html'] },
        url: input.url,
        body: body('<div class="book"><h2>late</h2></div>'),
      })));
    },
  });
  const session = new SourceSession({
    installationId: 'bounded',
    source: legadoSource,
    protocol: 'legado',
    capabilities,
    createSandbox: sandboxFactory(),
    limits: { requestTimeoutMs: 100, cleanupTimeoutMs: 10 },
  });
  await session.start();
  session.activate();

  const controller = new AbortController();
  const cancelled = session.invoke('search', { keyword: 'cancel' }, {
    signal: controller.signal,
    timeoutMs: 100,
  });
  while (!pending.length) await turn();
  controller.abort();
  await assert.rejects(cancelled, { code: 'CANCELLED' });
  assert.equal(session.stats().pendingAdapterTasks, 1);
  pending.shift()();
  await session.drain();
  assert.equal(session.stats().pendingAdapterTasks, 0);

  const timedOut = session.invoke('search', { keyword: 'timeout' }, { timeoutMs: 10 });
  while (!pending.length) await turn();
  await assert.rejects(timedOut, { code: 'TIMEOUT' });
  assert.equal(session.stats().pendingAdapterTasks, 1);
  await session.close();
  assert.equal(session.stats().pendingAdapterTasks, 1);
  pending.shift()();
  await session.drain();
  assert.equal(session.stats().pendingAdapterTasks, 0);
});


test('SourceSession ignores unused limits and validates consumed budgets', async t => {
  const options = { installationId: 'limits', source: legadoSource, protocol: 'legado', capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory(), limits: { unused: { ignored: true }, maximumNodes: 42 } };
  const session = new SourceSession(options);
  t.after(() => session.close());
  assert.equal(session.limits.maximumNodes, 42);
  assert.equal(Object.hasOwn(session.limits, 'unused'), false);
  assert.throws(() => new SourceSession({ ...options, limits: { maximumNodes: 0 } }), { code: 'INVALID_ARGUMENT' });
  await session.start();
});

test('Legado malformed selected rules, headers and put directives fail without prechecking siblings', async t => {
  for (const invalid of ['{bad', null, 7, false, [], '[]']) {
    const session = new SourceSession({ installationId: 'selected-rules', source: { ...legadoSource,
      ruleSearch: {}, ruleContent: invalid, ruleToc: invalid, ruleBookInfo: invalid },
      protocol: 'legado', capabilities: capabilitiesFor({ '/search': '[{"name":"One"}]' }),
      createSandbox: sandboxFactory() });
    t.after(() => session.close());
    await session.start(); session.activate();
    assert.equal((await session.invoke('search', { keyword: 'one' })).books[0].name, 'One');
    await assert.rejects(session.invoke('content', { chapterUrl: 'https://books.invalid/search' }), { code: 'UNSUPPORTED_RULE' });
    session.source.ruleSearch = invalid;
    await assert.rejects(session.invoke('search', { keyword: 'one' }), { code: 'UNSUPPORTED_RULE' });
    session.source.ruleSearch = {};
    session.source.header = invalid;
    await assert.rejects(session.invoke('search', { keyword: 'one' }), { code: 'UNSUPPORTED_RULE' });
  }
  const session = new SourceSession({ installationId: 'selected-rules', source: { ...legadoSource, ruleSearch: {
    bookList: '$', name: '@put:{invalid}$.name' } }, protocol: 'legado',
    capabilities: capabilitiesFor({ '/search': '[{"name":"One"}]' }), createSandbox: sandboxFactory() });
  t.after(() => session.close());
  await session.start(); session.activate();
  await assert.rejects(session.invoke('search', { keyword: 'one' }), { code: 'UNSUPPORTED_RULE' });
});

test('Reader search and discovery keep healthy mixed entries and reject nonempty unusable lists', async t => {
  let data;
  const legado = new SourceSession({ installationId: 'selected-rules', source: { ...legadoSource, ruleSearch: {}, ruleExplore: {},
    exploreUrl: 'https://books.invalid/search' }, protocol: 'legado',
    capabilities: capabilitiesFor({ '/search': () => JSON.stringify(data) }), createSandbox: sandboxFactory() });
  const qysg = new SourceSession({ installationId: 'selected-rules', source: { html: '<script>function search(){return []}</script>' },
    protocol: 'qysg', capabilities: capabilitiesFor({}),
    createSandbox: sandboxFactory({ actions: ['search', 'find', 'getfinds'], invoke: () => data }) });
  t.after(() => Promise.all([legado.close(), qysg.close()]));
  for (const session of [legado, qysg]) { await session.start(); session.activate(); }
  for (const [session, action, key] of [[legado, 'search', 'books'], [legado, 'explore', 'books'],
    [qysg, 'search', 'books'], [qysg, 'explore', 'books'], [qysg, 'exploreKinds', 'kinds']]) {
    const invoke = () => session.invoke(action, { keyword: 'one', url: 'https://books.invalid/search' });
    for (data of [[null], [false, 7, 'bad', []]])
      await assert.rejects(invoke(), { code: 'SCRIPT_PROTOCOL_ERROR' });
    data = [null, { name: 'One', url: '/one' }, false, 7];
    assert.equal((await invoke())[key].length, 1);
    data = [];
    assert.deepEqual((await invoke())[key], []);
    data = [{ url: '/url-only' }];
    assert.equal((await invoke())[key].length, 1);
    if (key === 'books') {
      data = [{}];
      assert.ok(Array.isArray((await invoke())[key]));
    }
  }
});
