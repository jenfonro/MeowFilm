import test from 'node:test';
import assert from 'node:assert/strict';
import { createCipheriv, createHash } from 'node:crypto';
import {
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
} from '../src/index.js';
import { createCapabilityClient, hostApiVersion } from '@meowfilm/source-runtime';

function sourceHost(t, { network, web, limits } = {}) {
  const owner = createNodeSourceCapabilities({
    network: network ?? (async input => ({
      statusCode: 200,
      statusMessage: 'OK',
      headers: { 'content-type': 'text/plain' },
      body: '',
      url: input.url,
    })),
    web,
  });
  const capabilities = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const adapter = createPortableSourceAdapter({
    capabilities,
    metadata: { platform: 'test', deviceId: 'fixture-device' },
  });
  const host = createScriptHost({
    adapters: { legado: adapter, qysg: adapter, cat: adapter },
    limits,
  });
  t.after(async () => {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
    assert.equal(host.stats().networkRequests, 0);
  });
  return { host, owner, capabilities };
}

const target = (installationId, operation, payload = {}) => ({
  installationId,
  siteId: 'source',
  operation,
  payload,
});

test('QYSG runs HTTP and DOM bridge code inside QuickJS', async t => {
  const calls = [];
  const { host } = sourceHost(t, {
    network: async input => {
      calls.push({
        url: input.url,
        cookie: input.options.headers.cookie ?? '',
      });
      return {
        statusCode: 200,
        statusMessage: 'OK',
        headers: { 'content-type': 'text/html; charset=utf-8' },
        body: `
          <div class="book">
            <span class="name">Fixture Book</span>
            <span class="author">Alice</span>
            <a href="https://books.invalid/1">open</a>
          </div>`,
        url: input.url,
      };
    },
  });
  const html = `
    <html><body><script>
      async function search(keyword, page) {
        await window.flutter_inappwebview.callHandler(
          'cookie.set', 'https://fixture.invalid/', 'session=fixture; Path=/'
        );
        await window.flutter_inappwebview.callHandler(
          'cookie.setCookie', 'https://fixture.invalid/', 'token', 'fixture'
        );
        const response = await window.flutter_inappwebview.callHandler(
          'http', 'GET', 'https://fixture.invalid/search?key=' + encodeURIComponent(keyword),
          '', {}, true, ''
        );
        return $(response.data).find('.book').map(function (_index, node) {
          const item = $(node);
          return {
            name: item.find('.name').text(),
            author: item.find('.author').text(),
            bookUrl: item.find('a').attr('href')
          };
        });
      }
    </script></body></html>
  `;
  const installed = await host.install({
    installationId: 'qysg',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'Fixture QYSG',
      bookSourceUrl: 'https://fixture.invalid/',
      html,
    }),
    enabled: true,
  });
  assert.deepEqual(installed.capabilities.source.actions, ['search', 'loginStatus', 'image']);
  const result = await host.invoke(target('qysg', 'search', { keyword: '测试', page: 2 }));
  assert.equal(calls.length, 1);
  assert.match(calls[0].url, /key=%E6%B5%8B%E8%AF%95/);
  assert.match(calls[0].cookie, /(?:^|; )session=fixture(?:;|$)/);
  assert.match(calls[0].cookie, /(?:^|; )token=fixture(?:;|$)/);
  assert.deepEqual(result, {
    books: [{
      name: 'Fixture Book',
      author: 'Alice',
      kind: '',
      coverUrl: '',
      intro: '',
      bookUrl: 'https://books.invalid/1',
      tocUrl: 'https://books.invalid/1',
      wordCount: '',
      latestChapterTitle: '',
      type: 0,
      origin: 'https://fixture.invalid/',
      originName: 'Fixture QYSG',
      variable: '',
    }],
    page: 2,
  });
});

test('QYSG sources may shadow browser helper globals with protocol objects', async t => {
  const { host } = sourceHost(t);
  await host.install({
    installationId: 'qysg-shadow',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'QYSG shadow',
      bookSourceUrl: 'https://fixture.invalid/',
      html: `<script>
        const $ = {
          async webview() { return 'shadowed'; }
        };
        async function search() {
          await window.flutter_inappwebview.callHandler('putLoginInfo', 'account');
          await window.flutter_inappwebview.callHandler('cache.allset', 'fixture', 'shared');
          const login = await window.flutter_inappwebview.callHandler('getLoginInfo');
          const shared = await window.flutter_inappwebview.callHandler('cache.allget', 'fixture');
          return [{ name: await $.webview() + ':' + login + ':' + shared }];
        }
      </script>`,
    }),
    enabled: true,
  });
  const result = await host.invoke(target('qysg-shadow', 'search'));
  assert.equal(result.books[0].name, 'shadowed:account:shared');
});

test('QYSG browser action placeholders tolerate an unused legacy toc alias', async t => {
  const { host } = sourceHost(t);
  const installed = await host.install({
    installationId: 'qysg-toc-alias',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'QYSG toc alias',
      bookSourceUrl: 'https://fixture.invalid/',
      html: `<script>
        'use strict';
        const strictMode = (function () { return this === undefined; })();
        async function chapter() {
          return [{
            name: strictMode ? 'Chapter One' : 'Non-strict',
            chapterId: 'https://fixture.invalid/chapter/1',
          }];
        }
        window.toc = toc;
      </script>`,
    }),
    enabled: true,
  });
  assert.equal(installed.capabilities.source.actions.includes('toc'), true);
  const result = await host.invoke(target('qysg-toc-alias', 'toc', {
    tocUrl: 'https://fixture.invalid/book/1',
  }));
  assert.deepEqual(result.chapters.map(chapter => [chapter.name, chapter.chapterUrl]), [
    ['Chapter One', 'https://fixture.invalid/chapter/1'],
  ]);
});

test('QYSG portable browser APIs support fetch, cloning and text-node traversal', async t => {
  const requests = [];
  const { host } = sourceHost(t, {
    network: async input => {
      requests.push(input);
      return {
        statusCode: 200,
        statusMessage: 'OK',
        headers: { 'content-type': 'text/html; charset=utf-8' },
        body: '<html><head><title>Fixture Page</title></head><body>' +
          '<div class="book">Prefix <b>Fixture</b><i>remove</i></div>' +
          '<p id="replace"><br></p></body></html>',
        url: input.url,
      };
    },
  });
  await host.install({
    installationId: 'qysg-browser',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'QYSG browser',
      bookSourceUrl: 'https://fixture.invalid/',
      html: `
        <head><title>Initial Source</title></head>
        <body><div id="initial">Ready</div></body>
        <script src="https://vc.jd.com/web/js/jquery-3.1.1.min.js"></script>
        <script src="https://cdnjs.cloudflare.com/ajax/libs/crypto-js/4.2.0/crypto-js.min.js"></script>
        <script src="https://cdnjs.cloudflare.com/ajax/libs/js-sha1/0.6.0/sha1.min.js"></script>
        <script type="module"
          src="https://static.cloudflareinsights.com/beacon.min.js/example"></script>
        <script src="//local.adguard.org?type=content-script"></script>
        <script>
        const initialLabel = document.getElementById('initial').textContent;
        async function search() {
          window.document.write('<span id="written">Written</span>');
          window.document.title = 'Source Page';
          const visited = [];
          $.each([1, 2, 3], function (_index, value) {
            visited.push(value);
            return false;
          });
          const wrappedDocument = $(window.document);
          const controller = new AbortController();
          const response = await fetch('/books', { signal: controller.signal });
          const document = new DOMParser().parseFromString(await response.text(), 'text/html');
          const headers = new Headers(new Map([['X-Test', 'yes']]));
          await fetch('/form', {
            method: 'POST',
            headers,
            body: new URLSearchParams({ key: 'a b' })
          });
          await fetch('/bytes', {
            method: 'POST',
            body: new Uint8Array([0, 255, 65])
          });
          const clone = document.querySelector('.book').cloneNode(true);
          clone.querySelector('i').remove();
          const copied = $(clone).clone();
          const copiedName = copied.children('i').remove().end().find('b').last().text();
          document.querySelector('#replace br')
            .replaceWith(document.createTextNode('replaced'));
          const direct = $(clone).contents().map(function () {
            return this.nodeType === 3 ? this.nodeValue.trim() : '';
          }).filter(Boolean).join('');
          clone.dataset.fixtureId = 'one';
          clone.style.display = 'block';
          const random = new Uint8Array(4);
          crypto.getRandomValues(random);
          const word = CryptoJS.enc.Hex.parse('0011223344556677').clone();
          word.words.splice(0, 1);
          word.sigBytes -= 4;
          const extended = CryptoJS.lib.WordArray.create(
            CryptoJS.enc.Utf8.parse('0123456789abcdef').words.slice(), 32
          );
          return [{
            name: clone.querySelector('b').textContent,
            author: document.title,
            kind: [
              copiedName,
              initialLabel,
              document.querySelector('#replace').textContent,
              document.body !== null,
              clone.getAttribute('missing') === null,
              $(clone).attr('missing') === undefined,
              response instanceof Response,
              location.protocol,
              typeof performance.now(),
              sha1('fixture'),
              random.length,
              word.toString(),
              extended.toString().length,
              wrappedDocument.find('#written').text(),
              window.document.title,
              window.document.textContent.includes('Written'),
              window.document.innerText.includes('Written'),
              window.document.hidden,
              matchMedia('(prefers-color-scheme: dark)').matches,
              wrappedDocument.length,
              visited.join(',')
            ].join('|'),
            intro: direct + ':' + clone.dataset.fixtureId + ':' + clone.style.display
          }];
        }
      </script>`,
    }),
    enabled: true,
  });
  const result = await host.invoke(target('qysg-browser', 'search', {
    keyword: 'fixture',
    page: 1,
  }));
  assert.equal(result.books[0].name, 'Fixture');
  assert.equal(result.books[0].author, 'Fixture Page');
  assert.equal(result.books[0].kind,
    `Fixture|Ready|replaced|true|true|true|true|https:|number|` +
      `${createHash('sha1').update('fixture').digest('hex')}|4|44556677|64|` +
      'Written|Source Page|true|true|false|false|1|1');
  assert.equal(result.books[0].intro, 'Prefix:one:block');
  assert.equal(requests.length, 3);
  assert.equal(requests[0].url, 'https://fixture.invalid/books');
  assert.equal(requests[1].options.headers['x-test'], 'yes');
  assert.equal(requests[1].options.headers['content-type'],
    'application/x-www-form-urlencoded;charset=UTF-8');
  assert.equal(requests[1].options.body.toString(), 'key=a+b');
  assert.deepEqual([...requests[2].options.body], [0, 255, 65]);
});

test('Cat exports, CryptoJS, isolation and virtual routes share one portable session', async t => {
  const requests = [];
  const { host, owner } = sourceHost(t, {
    network: async input => {
      requests.push(input.url);
      return {
        statusCode: 200,
        statusMessage: 'OK',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ name: 'Network Result' }),
        url: input.url,
      };
    },
  });
  const key = '0123456789abcdef';
  const iv = 'abcdef0123456789';
  const cipher = createCipheriv('aes-128-cbc', Buffer.from(key), Buffer.from(iv));
  const encrypted = Buffer.concat([cipher.update('hello'), cipher.final()]).toString('base64');
  const desKey = Buffer.from('rThYzbEp');
  const desCipher = createCipheriv('des-ede3', Buffer.concat([desKey, desKey, desKey]), null);
  const desEncrypted = Buffer.concat([
    desCipher.update('portable'),
    desCipher.final(),
  ]).toString('base64');
  const script = `
    export default class Spider {
      async init() { this.started = true; }
      async searchContent(keyword, _quick, page) {
        const response = await req('https://fixture.invalid/cat?key=' + encodeURIComponent(keyword));
        let constructorEscape = false;
        try { Function('return process')(); constructorEscape = true; } catch (_) {}
        const key = CryptoJS.enc.Utf8.parse(${JSON.stringify(key)});
        const iv = CryptoJS.enc.Utf8.parse(${JSON.stringify(iv)});
        return {
          list: [{ vod_id: 'video-1', vod_name: JSON.parse(response.content).name }],
          state: {
            started: this.started,
            page,
            process: typeof process,
            require: typeof require,
            packages: typeof Packages,
            constructorEscape,
            md5: CryptoJS.MD5(keyword).toString(),
            aes: CryptoJS.AES.encrypt('hello', key, { iv }).toString(),
            des: CryptoJS.DES.decrypt(
              ${JSON.stringify(desEncrypted)},
              CryptoJS.enc.Utf8.parse('rThYzbEp'),
              { mode: CryptoJS.mode.ECB, padding: CryptoJS.pad.Pkcs7 }
            ).toString(CryptoJS.enc.Utf8)
          }
        };
      }
      async playerContent(flag, id) {
        return { parse: 0, url: js2Proxy(false, 0, 'fixture-site', id, { Referer: flag }) };
      }
      async proxy(input) {
        return {
          siteKey: input.siteKey,
          originalUrl: input.url,
          referer: input.headers.Referer,
          routed: Boolean(input.route)
        };
      }
      async destroy() { this.started = false; return true; }
    }
  `;
  const installed = await host.install({
    installationId: 'cat',
    protocol: 'cat',
    source: script,
    enabled: true,
  });
  assert.deepEqual(installed.capabilities.source.actions, ['init', 'search', 'play', 'proxy', 'destroy']);
  const search = await host.invoke(target('cat', 'search', { keyword: 'fixture', page: 3 }));
  assert.equal(requests.length, 1);
  assert.deepEqual(search, {
    list: [{ vod_id: 'video-1', vod_name: 'Network Result' }],
    state: {
      started: true,
      page: '3',
      process: 'undefined',
      require: 'undefined',
      packages: 'undefined',
      constructorEscape: false,
      md5: createHash('md5').update('fixture').digest('hex'),
      aes: encrypted,
      des: 'portable',
    },
  });
  const play = await host.invoke(target('cat', 'play', {
    flag: 'https://referer.invalid/',
    id: 'https://media.invalid/video.m3u8',
  }));
  assert.match(play.url, /^meow-source:\/\/source%3Acat%3A1\/route-1$/);
  assert.deepEqual(owner.resolveVirtual('source:cat:1', 'route-1'), {
    siteKey: 'fixture-site',
    url: 'https://media.invalid/video.m3u8',
    headers: { Referer: 'https://referer.invalid/' },
  });
  assert.deepEqual(await host.invoke(target('cat', 'proxy', { url: play.url })), {
    siteKey: 'fixture-site',
    originalUrl: 'https://media.invalid/video.m3u8',
    referer: 'https://referer.invalid/',
    routed: true,
  });
  await assert.rejects(host.invoke(target('cat', 'proxy', {
    url: 'meow-source://source%3Acat%3A1/missing',
  })), { code: 'VIRTUAL_ROUTE_NOT_FOUND' });
  await host.uninstall('cat');
  assert.equal(owner.resolveVirtual('source:cat:1', 'route-1'), null);
});

test('Cat helpers captured during initialization bind to the active invocation scope', async t => {
  const { host, owner } = sourceHost(t, {
    network: async input => ({
      statusCode: 200,
      statusMessage: 'OK',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name: 'Network' }),
      url: input.url,
    }),
  });
  const script = `
    const startupReq = req;
    const startupLocal = local;
    const startupPdfa = pdfa;
    const startupProxy = js2Proxy;
    export default {
      async searchContent() {
        const response = await startupReq('https://fixture.invalid/captured');
        await startupLocal.set('captured', JSON.parse(response.content).name);
        const stored = await startupLocal.get('captured');
        const node = startupPdfa('<div class="name">DOM</div>', '.name&&Text')[0];
        return {
          list: [{
            vod_id: startupProxy(false, 0, 'captured', 'https://media.invalid/captured', {}),
            vod_name: stored + '-' + node.textContent
          }]
        };
      }
    };
  `;
  await host.install({
    installationId: 'captured',
    protocol: 'cat',
    source: script,
    enabled: true,
  });
  const result = await host.invoke(target('captured', 'search', { keyword: 'x' }));
  assert.equal(result.list[0].vod_name, 'Network-DOM');
  assert.match(result.list[0].vod_id,
    /^meow-source:\/\/source%3Acaptured%3A1\/route-1$/);
  assert.deepEqual(owner.resolveVirtual('source:captured:1', 'route-1'), {
    siteKey: 'captured',
    url: 'https://media.invalid/captured',
    headers: {},
  });
});

test('Cat rejects dynamic module loading before guest execution', async t => {
  const { host } = sourceHost(t);
  await assert.rejects(host.install({
    installationId: 'cat-import',
    protocol: 'cat',
    source: `
      export default { async searchContent() { return import('node:fs'); } }
    `,
    enabled: true,
  }), { code: 'UNSUPPORTED_DEPENDENCY' });
});

test('Cat export normalization uses syntax instead of matching comments and strings', async t => {
  const { host } = sourceHost(t);
  await host.install({
    installationId: 'cat-export',
    protocol: 'cat',
    source: `
      const harmless = "require('node:fs') process.exit() import('node:fs')";
      // require('node:fs'); process.exit(); import('node:fs');
      export const fixture = harmless.length;
      class Spider {
        searchContent() {
          return { list: [{ vod_id: 'fixture', vod_name: String(fixture > 0) }] };
        }
      }
      export { Spider as default };
    `,
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('cat-export', 'search')), {
    list: [{ vod_id: 'fixture', vod_name: 'true' }],
  });
  await host.install({
    installationId: 'cat-forward-export',
    protocol: 'cat',
    source: `
      export { Spider as default };
      class Spider {
        searchContent() {
          return { list: [{ vod_id: 'forward', vod_name: 'forward' }] };
        }
      }
    `,
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('cat-forward-export', 'search')), {
    list: [{ vod_id: 'forward', vod_name: 'forward' }],
  });

  for (const [installationId, source] of [
    ['cat-require', `const value = require('node:fs'); export default {};`],
    ['cat-process', `process.cwd(); export default {};`],
    ['cat-static-import', `import value from 'node:fs'; export default value;`],
    ['cat-import-meta', `const value = import.meta.url; export default value;`],
    ['cat-reexport', `export { default } from 'node:fs';`],
  ]) {
    await assert.rejects(host.install({
      installationId,
      protocol: 'cat',
      source,
      enabled: true,
    }), { code: 'UNSUPPORTED_DEPENDENCY' });
  }
});

test('startup timers lose capability access when readiness closes the startup scope', async t => {
  let networkCalls = 0;
  const { host } = sourceHost(t, {
    network: async input => {
      networkCalls++;
      return {
        statusCode: 200,
        statusMessage: 'OK',
        headers: {},
        body: 'unexpected',
        url: input.url,
      };
    },
  });
  const script = `
    let startup = 'pending';
    setTimeout(() => {
      req('https://fixture.invalid/background').then(
        () => { startup = 'unexpected'; },
        error => { startup = error.code; }
      );
    }, 1000);
    export default {
      async searchContent() {
        return { list: [{ vod_id: startup, vod_name: startup }] };
      }
    };
  `;
  await host.install({
    installationId: 'startup',
    protocol: 'cat',
    source: script,
    enabled: true,
  });
  assert.equal(host.stats().timers, 0);
  assert.deepEqual(await host.invoke(target('startup', 'search', { keyword: 'x' })), {
    list: [{ vod_id: 'pending', vod_name: 'pending' }],
  });
  assert.equal(networkCalls, 0);
});

test('Node HTTP follows normal origins while Cookie domain, path, Secure and profile semantics remain isolated', async () => {
  const calls = [];
  const owner = createNodeSourceCapabilities({ network: async input => {
    calls.push({ url: input.url, headers: { ...input.options.headers } });
    if (input.url === 'https://first.invalid/start') return { statusCode: 302, url: input.url,
      headers: { location: 'http://second.invalid/allowed/final?opaque=a%2Fb' }, body: Buffer.alloc(0) };
    return { statusCode: 200, url: input.url, headers: {}, body: Buffer.from([0, 127, 255]) };
  } });
  const client = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const profile = 'source:network:1:cookies';
  await client.call('cookie.set', {
    version: hostApiVersion, profile, url: 'https://first.invalid/',
    values: ['first=private; Path=/'],
  });
  await client.call('cookie.set', {
    version: hostApiVersion, profile, url: 'https://second.invalid/allowed/',
    values: ['second=current; Path=/allowed', 'secure=private; Secure; Path=/',
      'path=private; Path=/elsewhere', 'foreign=private; Domain=first.invalid; Path=/'],
  });
  await client.call('cookie.set', {
    version: hostApiVersion, profile: 'source:network:2:cookies', url: 'https://second.invalid/',
    values: ['otherProfile=private; Path=/'],
  });
  const result = await client.call('http.request', {
    version: hostApiVersion, url: 'https://first.invalid/start',
    headers: { Host: 'source.virtual.invalid', Origin: 'https://caller.invalid',
      authorization: 'call-input', cookie: 'explicit=call-input' },
    cookieProfile: profile,
  });
  assert.equal(result.status, 200);
  assert.equal(result.url, 'http://second.invalid/allowed/final?opaque=a%2Fb');
  assert.deepEqual(Buffer.from(result.body), Buffer.from([0, 127, 255]));
  assert.deepEqual(calls, [
    { url: 'https://first.invalid/start', headers: {
      host: 'source.virtual.invalid', origin: 'https://caller.invalid',
      authorization: 'call-input', cookie: 'explicit=call-input',
    } },
    { url: 'http://second.invalid/allowed/final?opaque=a%2Fb',
      headers: { host: 'source.virtual.invalid', origin: 'https://caller.invalid',
        authorization: 'call-input', cookie: 'explicit=call-input' } },
  ]);
  calls.length = 0;
  await client.call('http.request', {
    version: hostApiVersion, url: 'https://first.invalid/start',
    headers: { Origin: 'https://caller.invalid' }, cookieProfile: profile,
  });
  assert.deepEqual(calls, [
    { url: 'https://first.invalid/start', headers: { origin: 'https://caller.invalid', cookie: 'first=private' } },
    { url: 'http://second.invalid/allowed/final?opaque=a%2Fb',
      headers: { origin: 'https://caller.invalid', cookie: 'second=current' } },
  ], 'jar cookies are selected afresh per actual target, unlike explicit input headers');
  assert.equal(await client.call('cookie.get', {
    version: hostApiVersion, profile, url: 'https://second.invalid/elsewhere',
  }), 'path=private; secure=private');
  await owner.releaseProfiles('source:network:');
});

test('Node cookie and profile storage remain scoped and reject obvious public-suffix domains', async () => {
  const owner = createNodeSourceCapabilities();
  const client = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  await client.call('cookie.set', {
    version: hostApiVersion,
    profile: 'source:one:1:cookies',
    url: 'https://reader.example.co.uk/path/page',
    values: [
      'public=blocked; Domain=co.uk; Path=/',
      'host=allowed; Path=/',
    ],
  });
  assert.equal(await client.call('cookie.get', {
    version: hostApiVersion,
    profile: 'source:one:1:cookies',
    url: 'https://reader.example.co.uk/',
  }), 'host=allowed');
  await client.call('storage.set', {
    version: hostApiVersion,
    profile: 'source:one:1:storage',
    key: 'value',
    value: 1,
  });
  await client.call('storage.set', {
    version: hostApiVersion,
    profile: 'source:one:2:storage',
    key: 'value',
    value: 2,
  });
  await client.call('storage.set', {
    version: hostApiVersion,
    profile: 'source:one:20:storage',
    key: 'value',
    value: 20,
  });
  await client.call('storage.set', {
    version: hostApiVersion,
    profile: 'music:one:1:kw',
    key: 'value',
    value: 'music',
  });
  await owner.releaseProfiles('source:one:', 'source:one:2');
  assert.equal(await client.call('storage.get', {
    version: hostApiVersion,
    profile: 'source:one:1:storage',
    key: 'value',
  }), null);
  assert.equal(await client.call('storage.get', {
    version: hostApiVersion,
    profile: 'source:one:2:storage',
    key: 'value',
  }), 2);
  assert.equal(await client.call('storage.get', {
    version: hostApiVersion,
    profile: 'source:one:20:storage',
    key: 'value',
  }), null);
  assert.equal(await client.call('storage.get', {
    version: hostApiVersion,
    profile: 'music:one:1:kw',
    key: 'value',
  }), 'music');
});

test('Node capability owner retains complete storage, cookies and virtual metadata until profile release', async () => {
  const owner = createNodeSourceCapabilities();
  const client = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  for (const key of ['a', 'b']) {
    assert.equal(await client.call('storage.set', {
      version: hostApiVersion, profile: 'source:small:1:storage', key, value: 'x'.repeat(60),
    }), true);
    assert.equal(await client.call('storage.get', {
      version: hostApiVersion, profile: 'source:small:1:storage', key,
    }), 'x'.repeat(60));
  }
  assert.equal(await client.call('cookie.set', {
    version: hostApiVersion,
    profile: 'source:small:1:http',
    url: 'https://fixture.invalid/',
    values: [`session=${'x'.repeat(160)}; Path=/`],
  }), true);
  assert.equal(await client.call('cookie.get', {
    version: hostApiVersion, profile: 'source:small:1:http', url: 'https://fixture.invalid/',
  }), `session=${'x'.repeat(160)}`);
  await client.call('virtual.register', {
    version: hostApiVersion, profile: 'source:small:1', route: 'route-1',
    metadata: { value: 'x'.repeat(160) },
  });
  assert.deepEqual(owner.resolveVirtual('source:small:1', 'route-1'), { value: 'x'.repeat(160) });
  await owner.releaseProfiles('source:small:');
  assert.equal(owner.resolveVirtual('source:small:1', 'route-1'), null);
  assert.equal(await client.call('cookie.get', {
    version: hostApiVersion, profile: 'source:small:1:http', url: 'https://fixture.invalid/',
  }), '');
  assert.equal(await client.call('storage.get', {
    version: hostApiVersion, profile: 'source:small:1:storage', key: 'a',
  }), null);
  assert.equal(await client.call('storage.set', {
    version: hostApiVersion, profile: 'source:small:2:storage', key: 'b', value: 'x'.repeat(60),
  }), true);
});

test('portable Cat routes retain complete large metadata and unregister on close', async t => {
  const { host, owner } = sourceHost(t);
  await host.install({
    installationId: 'route-budget',
    protocol: 'cat',
    source: `
      export default class Spider {
        async playerContent() {
          for (let index = 0; index < 200; index++) {
            js2Proxy(false, 0, 'fixture', 'https://fixture.invalid/' + index, {
              value: 'x'.repeat(90000)
            });
          }
          return {
            parse: 0,
            url: js2Proxy(false, 0, 'fixture', 'https://fixture.invalid/two', {
              value: 'x'.repeat(90000)
            })
          };
        }
      }
    `,
    enabled: true,
  });
  const result = await host.invoke(target('route-budget', 'play'));
  assert.ok(result.url.endsWith('/route-201'));
  for (let index = 1; index <= 201; index++) {
    assert.equal(owner.resolveVirtual('source:route-budget:1', `route-${index}`).headers.value, 'x'.repeat(90000));
  }
  await host.disable('route-budget');
  for (let index = 1; index <= 201; index++) {
    assert.equal(owner.resolveVirtual('source:route-budget:1', `route-${index}`), null);
  }
  assert.equal(host.get('route-budget').state, 'disabled');
});

test('portable Cat retains every registered route', async t => {
  const { host, owner } = sourceHost(t);
  await host.install({
    installationId: 'many-routes',
    protocol: 'cat',
    source: `
      export default class Spider {
        async playerContent() {
          let url;
          for (let index = 0; index < 129; index++) {
            url = js2Proxy(false, 0, 'fixture', 'https://fixture.invalid/' + index, {});
          }
          return { parse: 0, url };
        }
      }
    `,
    enabled: true,
  });
  const value = await host.invoke(target('many-routes', 'play'));
  assert.equal(owner.resolveVirtual('source:many-routes:1', 'route-129').url,
    'https://fixture.invalid/128');
  assert.ok(value.url.includes('route-129'));
});

test('portable session state retains complete values across calls', async t => {
  const { host } = sourceHost(t);
  await host.install({
    installationId: 'state-budget',
    protocol: 'legado',
    source: JSON.stringify({
      bookSourceName: 'State budget',
      bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: `
        if (result === 'check') {
          let bytes = 0;
          for (let index = 0; index < 130; index++) {
            const value = source.get('key-' + index);
            if (value !== 'x'.repeat(128 * 1024)) throw Error('state changed');
            bytes += value.length;
          }
          return bytes;
        }
        for (let index = 0; index < 130; index++) {
          source.put('key-' + index, 'x'.repeat(128 * 1024));
        }
        source.get('key-129').length + ':' + result
      `,
    }),
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('state-budget', 'image', {
    url: 'https://fixture.invalid/image',
    body: 'fixture',
  })), { body: '131072:fixture', url: 'https://fixture.invalid/image' });
  assert.deepEqual(await host.invoke(target('state-budget', 'image', {
    url: 'https://fixture.invalid/image', body: 'check',
  })), { body: 130 * 128 * 1024, url: 'https://fixture.invalid/image' });
  assert.equal(host.get('state-budget').state, 'ready');
});

test('portable DOM preserves browser, jQuery and Jsoup missing-attribute semantics', async t => {
  const { host } = sourceHost(t);
  await host.install({
    installationId: 'dom-attributes',
    protocol: 'legado',
    source: JSON.stringify({
      bookSourceName: 'DOM attributes',
      bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: `
        const element = Jsoup.parse('<div></div>').selectFirst('div');
        result = element.attr('missing') === '' ? result : 'invalid';
        result
      `,
    }),
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('dom-attributes', 'image', {
    url: 'https://fixture.invalid/image',
    body: 'fixture',
  })), {
    body: 'fixture',
    url: 'https://fixture.invalid/image',
  });
});

test('portable Legado Jsoup, content, cookies and request options preserve common source semantics', async t => {
  const requests = [];
  const { host } = sourceHost(t, {
    network: async input => {
      requests.push(input);
      const binary = input.url.endsWith('/binary');
      return {
        statusCode: 200,
        statusMessage: 'OK',
        headers: { 'content-type': binary ? 'application/octet-stream' : 'text/plain' },
        body: binary ? Buffer.from([0x89, 0x50]) : 'posted',
        url: input.url,
      };
    },
  });
  await host.install({
    installationId: 'legado-common',
    protocol: 'legado',
    source: JSON.stringify({
      bookSourceName: 'Legado common',
      bookSourceUrl: 'https://fixture.invalid/',
      coverDecodeJs: `
        java.setContent('<a href="../next">next</a>', 'https://fixture.invalid/base/page');
        const contentLink = java.getElements('a').first();
        const parsed = Jsoup.parse(
          '<div><a href="item">item</a><img src="/cover"></div>',
          'https://fixture.invalid/root/'
        );
        const cloned = parsed.select('a').clone();
        cloned.attr('href', '/changed');
        java.setCookie('https://fixture.invalid/', 'token=fixture; Path=/');
        const token = java.getCookie('https://fixture.invalid/', 'token');
        java.removeCookie('https://fixture.invalid/');
        const cleared = java.getCookie('https://fixture.invalid/', 'token');
        const posted = java.ajax(
          'https://fixture.invalid/post,{"method":"POST","headers":{"X-Test":"yes"},' +
          '"body":"payload","charset":"utf-8"}'
        );
        const binary = java.connect(
          "https://fixture.invalid/binary,{'type':'hex'}"
        ).body();
        const compatible =
          contentLink.attr('abs:href') === 'https://fixture.invalid/next' &&
          parsed.selectFirst('img').attr('abs:src') === 'https://fixture.invalid/cover' &&
          parsed.selectFirst('a').attr('href') === 'item' &&
          cloned.first().attr('href') === '/changed' &&
          token === 'fixture' && cleared === '' &&
          posted === 'posted' && binary === '8950';
        compatible ? result : 'invalid'
      `,
    }),
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('legado-common', 'image', {
    url: 'https://fixture.invalid/image',
    body: 'fixture',
  })), {
    body: 'fixture',
    url: 'https://fixture.invalid/image',
  });
  assert.equal(requests.length, 2);
  assert.equal(requests[0].options.method, 'POST');
  assert.equal(requests[0].options.headers['x-test'], 'yes');
  assert.equal(requests[0].options.body.toString(), 'payload');
  assert.equal(requests[1].options.method, 'GET');
});

test('Legado URL templates accept complete js blocks', async t => {
  const { host } = sourceHost(t, {
    network: async input => ({
      statusCode: 200,
      statusMessage: 'OK',
      headers: { 'content-type': 'text/html' },
      body: '<div class="book"><h2>Fixture</h2><a href="/book">open</a></div>',
      url: input.url,
    }),
  });
  await host.install({
    installationId: 'legado-js-url',
    protocol: 'legado',
    source: JSON.stringify({
      bookSourceName: 'Legado JS URL',
      bookSourceUrl: 'https://fixture.invalid/',
      searchUrl: '<js>baseUrl + "search?key=" + encodeURIComponent(key)</js>',
      ruleSearch: {
        bookList: '.book',
        name: 'h2@text',
        bookUrl: 'a@href',
      },
    }),
    enabled: true,
  });
  const result = await host.invoke(target('legado-js-url', 'search', {
    keyword: '测试',
    page: 1,
  }));
  assert.equal(result.books[0].name, 'Fixture');
  assert.equal(result.books[0].bookUrl, 'https://fixture.invalid/book');
  assert.equal(result.url, 'https://fixture.invalid/search?key=%E6%B5%8B%E8%AF%95');
});

test('portable guest aborts retain physical capability ownership until adapters settle', { timeout: 15000 }, async t => {
  const releases = [];
  let entered;
  const started = new Promise(resolve => { entered = resolve; });
  let firstEntered;
  const firstStarted = new Promise(resolve => { firstEntered = resolve; });
  let calls = 0;
  const { host } = sourceHost(t, {
    network: async input => {
      if (input.url.endsWith('/first-entered') || input.url.endsWith('/second-entered')) {
        await (input.url.endsWith('/first-entered') ? firstStarted : started);
        return {
          statusCode: 200, headers: { 'content-type': 'text/plain' },
          body: 'entered', url: input.url,
        };
      }
      calls++;
      return await new Promise(resolve => {
        releases.push(() => resolve({
          statusCode: 200,
          statusMessage: 'OK',
          headers: { 'content-type': 'text/plain' },
          body: 'late',
          url: input.url,
        }));
        if (calls === 1) firstEntered();
        if (calls === 2) entered();
      });
    },
  });
  await host.install({
    installationId: 'physical-budget',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'Physical budget',
      bookSourceUrl: 'https://fixture.invalid/',
      html: `<script>
        async function search() {
          const controller = new AbortController();
          const first = fetch('/first', { signal: controller.signal })
            .then(() => 'unexpected', error => error.code);
          await fetch('/first-entered');
          controller.abort();
          fetch('/second').catch(() => {});
          await fetch('/second-entered');
          return [{ name: await first }];
        }
      </script>`,
    }),
    enabled: true,
  });
  const result = await host.invoke(target('physical-budget', 'search'));
  assert.equal(result.books[0].name, 'CANCELLED');
  await started;
  assert.equal(calls, 2);
  assert.equal(host.stats().pendingAdapterTasks, 2);
  for (const release of releases) release();
  while (host.stats().pendingAdapterTasks) {
    await new Promise(resolve => setImmediate(resolve));
  }
  assert.equal(host.stats().pendingAdapterTasks, 0);
});

test('Node capability owner enforces one timeout across the redirect chain', async () => {
  const signals = [];
  let calls = 0;
  const owner = createNodeSourceCapabilities({
    network: async input => {
      calls++;
      signals.push(input.signal);
      if (calls === 1) {
        return {
          statusCode: 302,
          statusMessage: 'Found',
          headers: { location: '/slow' },
          body: Buffer.alloc(0),
          url: input.url,
        };
      }
      return await new Promise((_resolve, reject) => {
        const abort = () => reject(input.signal.reason);
        if (input.signal.aborted) abort();
        else input.signal.addEventListener('abort', abort, { once: true });
      });
    },
  });
  const client = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  await assert.rejects(client.call('http.request', {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    timeoutMs: 20,
    redirects: 1,
  }), { code: 'TIMEOUT' });
  assert.equal(calls, 2);
  assert.equal(signals[0], signals[1]);
});

test('custom Node capability storage must provide profile cleanup', () => {
  assert.throws(() => createNodeSourceCapabilities({
    storage: {
      get() { return null; },
      set() { return true; },
      delete() { return true; },
    },
  }), { code: 'INVALID_ARGUMENT' });
});
