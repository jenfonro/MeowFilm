import test from 'node:test';
import assert from 'node:assert/strict';
import {
  bytesToBase64,
  createCapabilityClient,
  detectSourceProtocol,
  hostApiVersion,
  importYiciyuanSource,
  normalizeSourceDefinition,
  SourceSession,
} from '../src/index.js';

const turn = () => new Promise(resolve => setImmediate(resolve));
const body = value => bytesToBase64(new TextEncoder().encode(value));

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
    maximumBytes: 1024 * 1024,
    async invoke(name, input, context = {}) {
      calls.push({
        name,
        input: structuredClone(input),
        scope: context.scope === undefined ? undefined : structuredClone(context.scope),
      });
      if (name === 'http.request') {
        const url = new URL(input.url);
        const value = routes[url.pathname];
        if (value === undefined) throw Object.assign(new Error(), { code: 'NETWORK_ERROR' });
        return {
          version: hostApiVersion,
          status: 200,
          statusText: 'OK',
          headers: { 'content-type': ['text/html; charset=utf-8'] },
          url: url.href,
          body: body(value),
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

test('Yiciyuan input is imported into the Legado contract without executing source code', () => {
  const imported = importYiciyuanSource({
    bookSourceName: 'Legacy',
    bookSourceUrl: 'https://legacy.invalid/',
    bookSourceType: '漫画',
    ruleSearchUrl: '@header:{"X-Test":"yes"} https://legacy.invalid/search?key=searchKey|gbk',
    ruleSearchList: 'class.book',
    ruleSearchName: 'tag.h2@text',
    ruleSearchNoteUrl: 'tag.a@href',
    ruleChapterList: 'class.chapter',
    ruleChapterName: 'tag.a@text',
    ruleContentUrl: 'tag.a@href',
    ruleBookContent: '$.data.content',
  });
  assert.equal(imported.__importedFrom, 'yiciyuan');
  assert.equal(imported.bookSourceType, 2);
  assert.match(imported.searchUrl, /\{\{key\}\}/);
  assert.match(imported.searchUrl, /"charset":"gbk"/);
  assert.equal(imported.ruleSearch.bookList, 'class.book');
  assert.equal(imported.ruleToc.chapterName, 'tag.a@text');
  assert.equal(imported.ruleContent.content, '$.data.content');
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

test('pagination limits fail explicitly instead of returning a truncated table of contents', async t => {
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
    limits: { maximumPages: 1 },
  });
  t.after(() => session.close());
  await session.start();
  session.activate();
  await assert.rejects(session.invoke('toc', {
    bookUrl: 'https://books.invalid/book/1',
    tocUrl: 'https://books.invalid/book/1/toc',
  }), { code: 'RESOURCE_LIMIT' });
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
    limits: { maximumBytes: 128 },
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
    maximumBytes: 1024 * 1024,
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
    limits: { maximumNodes: 5, maximumBytes: 1024 },
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
    limits: { maximumNodes: 8, maximumBytes: 4096 },
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
