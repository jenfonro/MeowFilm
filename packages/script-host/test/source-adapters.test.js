import test from 'node:test';
import assert from 'node:assert/strict';
import {
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
} from '../src/index.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';

function sourceHost(t, network = async input => ({
  statusCode: 200,
  statusMessage: 'OK',
  headers: { 'content-type': 'text/plain' },
  body: '',
  url: input.url,
})) {
  const owner = createNodeSourceCapabilities({ network });
  const capabilities = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const adapter = createPortableSourceAdapter({
    capabilities,
    metadata: { platform: 'test', deviceId: 'fixture-device' },
  });
  const host = createScriptHost({
    adapters: { legado: adapter, qysg: adapter, cat: adapter },
  });
  t.after(async () => {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
    assert.equal(host.stats().networkRequests, 0);
  });
  return host;
}

const target = (installationId, operation, payload = {}) => ({
  installationId,
  siteId: 'source',
  operation,
  payload,
});

test('QYSG guest queries preserve DOM identity and deduplicate shared parents', async t => {
  const host = sourceHost(t);
  await host.install({
    installationId: 'qysg-node-identity',
    protocol: 'qysg',
    source: JSON.stringify({
      bookSourceName: 'QYSG node identity',
      bookSourceUrl: 'https://books.invalid/',
      html: `<script>
        function search() {
          const doc = new DOMParser().parseFromString('<div><a></a><a></a></div>', 'text/html');
          const first = doc.querySelector('a');
          const clone = first.cloneNode(true);
          const parents = $(doc.querySelectorAll('a')).parent();
          return [{
            name: String(first.isSameNode(doc.querySelector('div').children[0])),
            author: String(parents.length),
            kind: String(first.isSameNode(clone)),
            intro: String(parents[0].isSameNode(doc.querySelector('div')))
          }];
        }
      </script>`,
    }),
    enabled: true,
  });
  for (let index = 0; index < 2; index++) {
    const { books: [book] } = await host.invoke(target('qysg-node-identity', 'search', {
      keyword: 'fixture',
    }));
    assert.equal(book.name, 'true');
    assert.equal(book.author, '1');
    assert.equal(book.kind, 'false');
    assert.equal(book.intro, 'true');
  }
});

test('Cat default declarations retain their bindings and other supported exports still work', async t => {
  const host = sourceHost(t);
  const sources = [
    `
      export default class Spider {
        searchContent() { return { list: [{ vod_id: 'fixture', vod_name: this.label }] }; }
      }
      Spider.prototype.init = function(ext) { this.label = ext.label; };
    `,
    `
      const prototype = Spider.prototype;
      export default function Spider() {}
      prototype.init = function(ext) { this.label = ext.label; };
      Spider.prototype.searchContent = function() {
        return { list: [{ vod_id: 'fixture', vod_name: this.label }] };
      };
    `,
    `
      export default class {
        init(ext) { this.label = ext.label; }
        searchContent() { return { list: [{ vod_id: 'fixture', vod_name: this.label }] }; }
      }
    `,
    `
      export default function() {
        return {
          init(ext) { this.label = ext.label; },
          searchContent() { return { list: [{ vod_id: 'fixture', vod_name: this.label }] }; }
        };
      }
    `,
    `
      export { Spider as default };
      export class Spider {
        init(ext) { this.label = ext.label; }
        searchContent() { return { list: [{ vod_id: 'fixture', vod_name: this.label }] }; }
      }
    `,
  ];
  for (const [index, script] of sources.entries()) {
    const installationId = `cat-declaration-${index}`;
    await host.install({
      installationId,
      protocol: 'cat',
      source: JSON.stringify({ script, ext: { label: 'bound' } }),
      enabled: true,
    });
    assert.deepEqual(await host.invoke(target(installationId, 'search')), {
      list: [{ vod_id: 'fixture', vod_name: 'bound' }],
    });
  }
  for (const [index, script] of [
    "import Spider from './spider.js'; export default Spider;",
    "export { default } from './spider.js';",
    "export default { searchContent() { return import('./spider.js'); } };",
  ].entries()) {
    await assert.rejects(host.install({
      installationId: `cat-declaration-dependency-${index}`,
      protocol: 'cat',
      source: script,
      enabled: true,
    }), { code: 'UNSUPPORTED_DEPENDENCY' });
  }
});

test('Legado Jsoup execute and get/post chains return their awaited public values', async t => {
  const requests = [];
  const host = sourceHost(t, async input => {
    requests.push(input);
    const path = new URL(input.url).pathname.slice(1);
    return {
      statusCode: 200,
      statusMessage: 'OK',
      headers: { 'content-type': 'text/html; charset=utf-8' },
      body: `<b>${path}</b>`,
      url: input.url,
    };
  });
  await host.install({
    installationId: 'legado-jsoup-execute',
    protocol: 'legado',
    source: JSON.stringify({
      bookSourceName: 'Legado Jsoup execute',
      bookSourceUrl: 'https://books.invalid/',
      coverDecodeJs: `
        const response = Jsoup.connect('https://books.invalid/execute')
          .header('X-Fixture', 'execute').execute();
        const body = Jsoup.connect('https://books.invalid/chain')
          .userAgent('fixture-agent').execute().body();
        const get = Jsoup.connect('https://books.invalid/get').get().select('b').text();
        const post = Jsoup.connect('https://books.invalid/post').data('payload')
          .post().selectFirst('b').text();
        [response.body(), body, get, post].join('|')
      `,
    }),
    enabled: true,
  });
  assert.deepEqual(await host.invoke(target('legado-jsoup-execute', 'image', {
    url: 'https://books.invalid/image',
    body: 'fixture',
  })), {
    url: 'https://books.invalid/image',
    body: '<b>execute</b>|<b>chain</b>|get|post',
  });
  assert.deepEqual(requests.map(input => [input.url, input.options.method]), [
    ['https://books.invalid/execute', 'GET'],
    ['https://books.invalid/chain', 'GET'],
    ['https://books.invalid/get', 'GET'],
    ['https://books.invalid/post', 'POST'],
  ]);
  assert.equal(requests[0].options.headers['x-fixture'], 'execute');
  assert.equal(requests[1].options.headers['user-agent'], 'fixture-agent');
  assert.equal(requests[3].options.body.toString(), 'payload');
});
