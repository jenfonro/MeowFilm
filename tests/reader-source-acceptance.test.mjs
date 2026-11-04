import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import {
  mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

function area(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const directory = mkdtempSync(join(temporaryRoot, 'source-acceptance-'));
  t.after(() => {
    assertOwnedPath(temporaryRoot, directory);
    rmSync(directory, { recursive: true });
  });
  return { directory, output: join(directory, 'report.json') };
}

async function run(args, { direct = false } = {}) {
  const script = direct ? 'scripts/accept-reader-sources.mjs' : 'scripts/accept-sources.mjs';
  const child = spawn(process.execPath, [script, ...(direct ? [] : ['--kind', 'source']), ...args], {
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', value => { stdout += value; });
  child.stderr.on('data', value => { stderr += value; });
  return await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', code => resolve({ code, stdout, stderr }));
  });
}

test('Reader inventory accepts CatVod, QYSG, Legado, Yiciyuan and split JSON arrays without copying secrets', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-VALUE';
  writeFileSync(join(directory, 'cat.js'), `
    const privateValue = ${JSON.stringify(privateValue)};
    export default {
      async searchContent(keyword) {
        return { list: [{ vod_id: keyword, vod_name: keyword }] };
      }
    };
  `);
  writeFileSync(join(directory, 'qysg.html'), `
    <html><body><script>
      function search(keyword) { return [{ name: keyword, bookUrl: 'https://books.invalid/1' }]; }
    </script></body></html>
  `);
  writeFileSync(join(directory, 'qysg-string.json'), JSON.stringify(`
    <html><body><script>function search(){ return []; }</script></body></html>
  `));
  const legado = name => ({
    bookSourceName: name,
    bookSourceUrl: 'https://books.invalid/',
    searchUrl: 'https://books.invalid/search?key={{key}}',
    ruleSearch: { bookList: '.book', name: '.name@text', bookUrl: 'a@href' },
  });
  writeFileSync(join(directory, 'books.json'), JSON.stringify([
    legado('One'),
    legado('Two'),
  ]));

  writeFileSync(join(directory, 'yiciyuan.json'), JSON.stringify({
    bookSourceName: 'Legacy',
    bookSourceUrl: 'https://books.invalid/',
    ruleSearchUrl: 'https://books.invalid/search?key=searchKey',
    ruleSearchList: 'class.book',
    ruleSearchName: 'class.name@text',
    ruleSearchNoteUrl: 'tag.a@href',
  }));

  const result = await run(['--scripts', directory, '--output', output]);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8');
  const report = JSON.parse(text);
  assert.equal(report.schema, 2);
  assert.equal(report.kind, 'source');
  assert.equal(report.summary.sources, 6);
  assert.equal(report.summary.initialized, 6);
  assert.deepEqual(report.rows.map(row => row.protocol).sort(),
    ['cat', 'legado', 'legado', 'qysg', 'qysg', 'yiciyuan']);
  assert.equal(report.rows.every(row =>
    row.cleanup === 'complete' && row.unload === 'complete' && row.sourceUnchanged), true);
  assert.equal(report.summary.declaredActions > 5, true);
  assert.equal(report.summary.invokedActions, 0);
  assert.equal(text.includes(privateValue), false);
  assert.equal(text.includes('https://books.invalid/1'), false);
});

test('live Legado acceptance exercises declared actions with explicit image input', async t => {
  const { directory, output } = area(t);
  const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end('<div class="book"><span class="name">Result</span><a href="/book/1">open</a></div>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.json'), JSON.stringify({
    bookSourceName: 'Fixture',
    bookSourceUrl: origin,
    searchUrl: `${origin}/search?key={{key}}`,
    ruleSearch: { bookList: '.book', name: '.name@text', bookUrl: 'a@href' },
  }));
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: {
      'source.json': {
        actions: {
          image: { url: `${origin}/image`, body: '' },
        },
      },
    },
  }));
  const result = await run([
    '--scripts', join(directory, 'source.json'),
    '--records', records,
    '--output', output,
    '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['search', 'returned'],
    ['loginStatus', 'returned'],
    ['image', 'returned'],
  ]);
  assert.equal(report.summary.returned, 3);
  assert.equal(report.summary.not_exercised, 0);
});

test('live QYSG acceptance detects remote HTML, retries candidates and uses the final URL for scripts', async t => {
  const { directory, output } = area(t);
  const requests = [];
  const server = createServer((request, response) => {
    requests.push(request.url);
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    if (request.url === '/unavailable.html') {
      response.statusCode = 503;
      response.end('unavailable');
      return;
    }
    if (request.url === '/entry.html') {
      response.statusCode = 302;
      response.setHeader('Location', '/rules/source.html');
      response.end();
      return;
    }
    if (request.url === '/rules/source.html') {
      response.end(`
        <main id="fixture">Ready</main>
        <script src="./helper.js"></script>
        <script>
          function search(keyword) {
            return [{ name: helper(keyword), bookUrl: location.href }];
          }
        </script>
      `);
      return;
    }
    if (request.url === '/rules/helper.js') {
      response.setHeader('Content-Type', 'application/javascript; charset=utf-8');
      response.end('function helper(value){ return "remote:" + value; }');
      return;
    }
    response.statusCode = 404;
    response.end('missing');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.json'), JSON.stringify({
    bookSourceName: 'Remote QYSG',
    bookSourceUrl: origin,
    enabled: true,
    enabledExplore: false,
    login: false,
    html: `${origin}/unavailable.html  ${origin}/entry.html`,
  }));
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: {
      'source.json': {
        actions: {
          image: { url: `${origin}/cover`, body: '' },
        },
      },
    },
  }));

  const result = await run([
    '--scripts', join(directory, 'source.json'),
    '--records', records,
    '--output', output,
    '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.rows[0].protocol, 'qysg');
  assert.equal(report.rows[0].initialization, 'ready');
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['search', 'returned'],
    ['loginStatus', 'returned'],
    ['image', 'returned'],
  ]);
  assert.deepEqual(requests.slice(0, 4), [
    '/unavailable.html',
    '/entry.html',
    '/rules/source.html',
    '/rules/helper.js',
  ]);
  assert.equal(requests.includes('/helper.js'), false);
});

test('unavailable QYSG remote HTML is classified as upstream failure, not engine incompatibility', async t => {
  const { directory, output } = area(t);
  const server = createServer((_request, response) => {
    response.statusCode = 502;
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end('unavailable');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.json'), JSON.stringify({
    bookSourceName: 'Unavailable QYSG',
    bookSourceUrl: origin,
    enabled: true,
    html: `${origin}/source.html`,
  }));

  const result = await run([
    '--scripts', join(directory, 'source.json'),
    '--output', output,
    '--live',
  ]);
  assert.notEqual(result.code, 0);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.rows[0].protocol, 'qysg');
  assert.equal(report.rows[0].initialization, 'failed');
  assert.equal(report.rows[0].code, 'HTTP_ERROR');
  assert.equal(report.rows[0].failure, 'upstream_failure');
  assert.equal(report.summary.upstream_failure, 1);
  assert.equal(report.summary.engine_incompatible, 0);
});

test('Reader preserves configuration attribution for an actual rejected HTTP response', async t => {
  const { directory, output } = area(t);
  const server = createServer((_request, response) => {
    response.statusCode = 403;
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ message: 'api_key invalid SYNTHETIC-PRIVATE-KEY' }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const file = join(directory, 'source.json');
  writeFileSync(file, JSON.stringify({
    bookSourceName: 'Configuration fixture',
    bookSourceUrl: origin,
    html: `${origin}/source.html`,
  }));
  const result = await run(['--scripts', file, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.rows[0].initialization, 'failed');
  assert.equal(report.rows[0].code, 'HTTP_ERROR');
  assert.equal(report.rows[0].failure, 'account_region_configuration_required');
  assert.equal(report.summary.account_region_configuration_required, 1);
  assert.equal(report.summary.upstream_failure, 0);
  assert.equal(report.summary.engine_incompatible, 0);
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(text.includes(origin), false);
  assert.equal(text.includes('SYNTHETIC-PRIVATE-KEY'), false);
  assert.equal((result.stdout + result.stderr).includes('SYNTHETIC-PRIVATE-KEY'), false);
});

test('empty source inventories and invalid protocol overrides cannot satisfy acceptance', async t => {
  const { directory, output } = area(t);
  const empty = await run(['--scripts', directory, '--output', output]);
  assert.notEqual(empty.code, 0);
  const invalid = await run([
    '--scripts', directory,
    '--output', output,
    '--protocol', 'unknown',
  ]);
  assert.notEqual(invalid.code, 0);
});

test('live Spider acceptance requires business actions while offline initialization remains valid', async t => {
  for (const [name, script, actions] of [
    ['empty', 'export default {};', []],
    ['lifecycle', 'export default { init(){ return true; }, destroy(){ return true; } };',
      ['init', 'destroy']],
  ]) {
    await t.test(name, async t => {
      const { directory, output } = area(t);
      const file = join(directory, 'spider.js');
      writeFileSync(file, script);
      const offline = await run(['--scripts', file, '--output', output]);
      assert.equal(offline.code, 0, offline.stderr);
      const initialized = JSON.parse(readFileSync(output, 'utf8'));
      assert.equal(initialized.mode, 'offline-init');
      assert.equal(initialized.summary.initialized, 1);
      assert.equal(initialized.summary.invokedActions, 0);
      assert.equal(initialized.summary.engine_incompatible, 0);
      assert.equal(initialized.rows[0].failure, undefined);

      const live = await run(['--scripts', file, '--output', output, '--live']);
      assert.notEqual(live.code, 0);
      const report = JSON.parse(readFileSync(output, 'utf8'));
      assert.equal(report.summary.initialized, 1);
      assert.equal(report.summary.declaredActions, actions.length);
      assert.equal(report.summary.invokedActions, actions.length);
      assert.equal(report.summary.returned, actions.length);
      assert.equal(report.summary.engine_incompatible, 1);
      assert.equal(report.rows[0].code, 'NO_BUSINESS_ACTIONS');
      assert.equal(report.rows[0].failure, 'engine_incompatible');
      assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]),
        actions.map(action => [action, 'returned']));
      assert.equal(report.rows[0].unload, 'complete');
      assert.equal(report.rows[0].cleanup, 'complete');
      assert.equal(report.rows[0].sourceUnchanged, true);
    });
  }
});

for (const protocol of ['legado', 'yiciyuan']) {
  test(`live ${protocol} acceptance derives book details, TOC and content from actual responses`, async t => {
    const { directory, output } = area(t);
    const requests = [];
    const pages = {
      '/search': '<div class="book"><h2>Fixture</h2><a href="/book/1">open</a></div>',
      '/book/1': '<h1>Fixture</h1><a class="toc" href="/toc/1">contents</a>',
      '/toc/1': '<li><a href="/chapter/1">Chapter One</a></li>',
      '/chapter/1': '<article>SYNTHETIC-PRIVATE-CONTENT</article>',
    };
    const server = createServer((request, response) => {
      const url = new URL(request.url, 'http://fixture.invalid');
      requests.push(url.pathname);
      response.setHeader('Content-Type', 'text/html; charset=utf-8');
      response.statusCode = pages[url.pathname] ? 200 : 404;
      response.end(pages[url.pathname] ?? 'missing');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    const origin = `http://127.0.0.1:${server.address().port}`;
    const source = protocol === 'legado' ? {
      searchUrl: `${origin}/search?key={{key}}`,
      ruleSearch: { bookList: '.book', name: 'h2@text', bookUrl: 'a@href' },
      ruleBookInfo: { name: 'h1@text', tocUrl: '.toc@href' },
      ruleToc: { chapterList: 'li', chapterName: 'a@text', chapterUrl: 'a@href' },
      ruleContent: { content: 'article@text' },
    } : {
      ruleSearchUrl: `${origin}/search?key=searchKey`,
      ruleSearchList: '.book',
      ruleSearchName: 'h2@text',
      ruleSearchNoteUrl: 'a@href',
      ruleBookName: 'h1@text',
      ruleChapterUrl: '.toc@href',
      ruleChapterList: 'li',
      ruleChapterName: 'a@text',
      ruleContentUrl: 'a@href',
      ruleBookContent: 'article@text',
    };
    writeFileSync(join(directory, 'source.json'), JSON.stringify({
      bookSourceName: 'Fixture',
      bookSourceUrl: origin,
      ...source,
    }));
    const records = join(directory, 'records.json');
    writeFileSync(records, JSON.stringify({
      source: { 'source.json': { actions: { image: { url: `${origin}/cover`, body: '' } } } },
    }));

    const result = await run([
      '--scripts', directory, '--records', records, '--output', output, '--live',
    ]);
    assert.equal(result.code, 0, result.stderr);
    const text = readFileSync(output, 'utf8'), report = JSON.parse(text), row = report.rows[0];
    assert.equal(row.protocol, protocol);
    assert.equal(row.importedProtocol, protocol);
    assert.deepEqual(row.actions.map(action => [action.operation, action.state]), [
      ['search', 'returned'], ['bookInfo', 'returned'], ['toc', 'returned'],
      ['content', 'returned'], ['loginStatus', 'returned'], ['image', 'returned'],
    ]);
    assert.deepEqual(requests, ['/search', '/book/1', '/toc/1', '/chapter/1']);
    assert.equal(report.summary.declaredActions, 6);
    assert.equal(report.summary.invokedActions, 6);
    assert.equal(row.unload, 'complete');
    assert.equal(row.cleanup, 'complete');
    assert.equal(row.sourceUnchanged, true);
    assert.equal(text.includes('SYNTHETIC-PRIVATE-CONTENT'), false);
    assert.equal(text.includes(origin), false);
  });
}

test('live QYSG acceptance exercises every declared Reader action with chained and supplied inputs', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-QYSG';
  writeFileSync(join(directory, 'source.html'), `<script>
    const bookUrl = 'https://books.invalid/book/1';
    function search(keyword, page) {
      if (keyword !== '测试' || page !== 1) throw Error('bad search');
      return [{ name: 'Fixture', bookUrl }];
    }
    function getfinds() { return [{ title: 'Fixture', url: 'https://books.invalid/explore' }]; }
    function find(url, page) {
      if (url !== 'https://books.invalid/explore' || page !== 1) throw Error('bad explore');
      return [{ name: 'Fixture', bookUrl }];
    }
    function info(url) {
      if (url !== bookUrl) throw Error('bad info');
      return { name: 'Fixture', bookUrl, tocUrl: 'https://books.invalid/toc/1' };
    }
    function chapter(url, book) {
      if (url !== 'https://books.invalid/toc/1' || book !== bookUrl) throw Error('bad toc');
      return [{ name: 'Chapter', chapterUrl: 'https://books.invalid/chapter/1' }];
    }
    function content(url, book) {
      if (url !== 'https://books.invalid/chapter/1' || book !== bookUrl) throw Error('bad content');
      return ${JSON.stringify(privateValue)};
    }
    function getloginurl() { return 'https://books.invalid/login'; }
    function login(value) {
      if (value !== ${JSON.stringify(privateValue)}) throw Error('bad login');
      return true;
    }
    function imagedecrypt(url, body) {
      if (url !== 'https://books.invalid/cover' || body !== ${JSON.stringify(privateValue)})
        throw Error('bad image');
      return body;
    }
  </script>`);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: {
      'source.html': {
        actions: {
          login: { arguments: [privateValue] },
          image: { url: 'https://books.invalid/cover', body: privateValue },
        },
      },
    },
  }));
  const result = await run([
    '--scripts', directory, '--records', records, '--output', output, '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['search', 'returned'], ['exploreKinds', 'returned'], ['explore', 'returned'],
    ['bookInfo', 'returned'], ['toc', 'returned'], ['content', 'returned'],
    ['loginStatus', 'returned'], ['login', 'returned'], ['image', 'returned'],
  ]);
  assert.equal(report.summary.declaredActions, 9);
  assert.equal(report.summary.invokedActions, 9);
  assert.equal(report.summary.not_exercised, 0);
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(text.includes(privateValue), false);
  assert.equal(text.includes('https://books.invalid'), false);
});

test('portable CatVod Spider acceptance keeps its action ABI, playback chain and virtual proxy', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-SPIDER';
  writeFileSync(join(directory, 'spider.js'), `
    export default class Spider {
      init(ext) {
        if (Object.keys(ext).length) throw Error('bad init');
        this.started = true;
        return true;
      }
      homeContent(filter) {
        if (filter !== true || !this.started) throw Error('bad home');
        return { class: [{ type_id: 'films' }] };
      }
      homeVideoContent() { return { list: [{ vod_id: 'home-video' }] }; }
      categoryContent(id, page, filter, extend) {
        if (id !== 'films' || page !== '1' || filter !== false || Object.keys(extend).length)
          throw Error('bad category');
        return { list: [{ vod_id: 'category-video' }] };
      }
      searchContent(keyword, quick, page) {
        if (keyword !== '测试' || quick !== false || page !== '1') throw Error('bad search');
        return JSON.stringify({ list: [{ vod_id: 'search-video' }] });
      }
      detailContent(ids) {
        if (ids.length !== 1 || ids[0] !== 'search-video') throw Error('bad detail');
        return { list: [{ vod_id: ids[0], vod_play_from: 'Fixture',
          vod_play_url: 'Episode$https://media.invalid/fixture.m3u8' }] };
      }
      playerContent(flag, id) {
        if (flag !== 'Fixture' || id !== 'https://media.invalid/fixture.m3u8')
          throw Error('bad play');
        return { parse: 0, url: js2Proxy(false, 0, 'fixture', id, { Referer: flag }) };
      }
      proxy(input) {
        if (!input.route || input.siteKey !== 'fixture' ||
            input.url !== 'https://media.invalid/fixture.m3u8' || input.headers.Referer !== 'Fixture')
          throw Error('bad proxy');
        return { status: 200 };
      }
      action(action, value) {
        if (action !== 'fixture' || value !== ${JSON.stringify(privateValue)})
          throw Error('bad action');
        return true;
      }
      destroy() { this.started = false; return true; }
    }
  `);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: { 'spider.js': { actions: { action: { action: 'fixture', value: privateValue } } } },
  }));
  const result = await run([
    '--scripts', directory, '--records', records, '--output', output, '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.kind, 'source');
  assert.equal(report.rows[0].protocol, 'cat');
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['init', 'returned'], ['home', 'returned'], ['homeVideo', 'returned'],
    ['category', 'returned'], ['search', 'returned'], ['detail', 'returned'],
    ['play', 'returned'], ['proxy', 'returned'], ['action', 'returned'], ['destroy', 'returned'],
  ]);
  assert.equal(report.summary.declaredActions, 10);
  assert.equal(report.summary.invokedActions, 10);
  assert.equal(report.rows[0].actions.find(action => action.operation === 'play').result.kind, 'url');
  assert.equal(report.rows[0].unload, 'complete');
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(report.rows[0].sourceUnchanged, true);
  assert.equal(text.includes(privateValue), false);
  assert.equal(text.includes('https://media.invalid'), false);
  assert.equal(text.includes('meow-source://'), false);
});

test('Reader source records retain file defaults and split-item overrides on both CLI entries', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'sources.json'), JSON.stringify(['file', 'item'].map(keyword => ({
    bookSourceName: keyword,
    bookSourceUrl: 'https://books.invalid/',
    html: `<script>function search(keyword) {
      if (keyword !== ${JSON.stringify(keyword)}) throw Error('wrong record input');
      return [];
    }</script>`,
  }))));
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: {
      'sources.json': { actions: { search: { keyword: 'file' }, image: { body: '' } } },
      'sources.json#2': { actions: { search: { keyword: 'item' }, image: { body: '' } } },
    },
  }));
  writeFileSync(`${output}.tmp`, 'SYNTHETIC-PRIVATE-NOT-A-SOURCE');
  for (const direct of [false, true]) {
    const result = await run([
      '--scripts', directory, '--records', records, '--output', output, '--live',
    ], { direct });
    assert.equal(result.code, 0, result.stderr);
    const report = JSON.parse(readFileSync(output, 'utf8'));
    assert.equal(report.protocolOverride, 'auto');
    assert.equal(report.summary.sources, 2);
    assert.equal(report.summary.declaredActions, 6);
    assert.equal(report.summary.returned, 6);
    assert.deepEqual(report.rows.map(row => row.file), ['sources.json#1', 'sources.json#2']);
    assert.equal(report.rows.every(row => row.sourceUnchanged), true);
  }
});

test('missing Reader action input stays unexercised and makes live acceptance nonzero', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.html'), '<script>function chapter(){ return []; }</script>');
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: { 'source.html': { actions: { image: { body: '' } } } },
  }));
  const result = await run([
    '--scripts', directory, '--records', records, '--output', output, '--live',
  ]);
  assert.notEqual(result.code, 0);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.not_exercised, 1);
  assert.equal(report.summary.invokedActions, 2);
  assert.deepEqual(report.rows[0].actions.find(action => action.operation === 'toc'), {
    operation: 'toc', state: 'not_exercised',
  });
});

test('Reader reports keep unknown script failures fatal despite HTTP or account evidence', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-READER';
  const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/upstream') {
      response.statusCode = 503;
      response.end(JSON.stringify({ error: privateValue }));
    } else {
      response.end(JSON.stringify({ success: false, message: `api_key invalid ${privateValue}` }));
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.html'), `<script>
    function search(){ return [{ name: 'Fixture', bookUrl: 'https://books.invalid/1' }]; }
    async function getfinds() {
      await fetch(${JSON.stringify(`${origin}/upstream`)});
      throw Error(${JSON.stringify(privateValue)});
    }
    async function info() {
      await fetch(${JSON.stringify(`${origin}/account`)});
      throw Error(${JSON.stringify(privateValue)});
    }
    async function chapter() {
      await fetch(${JSON.stringify(`${origin}/upstream`)});
      return CryptoJS.AES.encrypt(${JSON.stringify(privateValue)}, 'key');
    }
    function content() { return missingFixtureFunction(); }
  </script>`);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: {
      'source.html': { actions: {
        content: { chapterUrl: 'https://books.invalid/chapter' },
        image: { body: '' },
      } },
    },
  }));
  const result = await run([
    '--scripts', directory, '--records', records, '--output', output, '--live',
  ]);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.summary.returned, 3);
  assert.equal(report.summary.upstream_failure, 0);
  assert.equal(report.summary.account_region_configuration_required, 0);
  assert.equal(report.summary.host_capability_missing, 1);
  assert.equal(report.summary.engine_incompatible, 3);
  assert.deepEqual(report.rows[0].actions.filter(action => action.code).map(action =>
    [action.operation, action.state]), [
    ['exploreKinds', 'engine_incompatible'],
    ['bookInfo', 'engine_incompatible'],
    ['toc', 'host_capability_missing'],
    ['content', 'engine_incompatible'],
  ]);
  assert.equal(report.summary.not_exercised, 0);
  assert.equal(report.summary.cleanupFailures, 0);
  assert.equal(text.includes(privateValue), false);
  assert.equal(text.includes(origin), false);
  assert.equal((result.stdout + result.stderr).includes(privateValue), false);
});

test('a same-action HTTP 503 cannot make an unknown Reader function pass live acceptance', async t => {
  const { directory, output } = area(t);
  let requests = 0;
  const server = createServer((_request, response) => {
    requests++;
    response.statusCode = 503;
    response.end('SYNTHETIC-PRIVATE-UPSTREAM');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const file = join(directory, 'source.html');
  writeFileSync(file, `<script>
    async function search() {
      await fetch(${JSON.stringify(`${origin}/unavailable`)});
      return missingFixtureFunction();
    }
  </script>`);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: { 'source.html': { actions: { image: { body: '' } } } },
  }));
  const result = await run([
    '--scripts', file, '--records', records, '--output', output, '--live',
  ]);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(requests, 1);
  assert.equal(report.summary.invokedActions, 3);
  assert.equal(report.summary.returned, 2);
  assert.equal(report.summary.upstream_failure, 0);
  assert.equal(report.summary.engine_incompatible, 1);
  assert.equal(report.summary.not_exercised, 0);
  const search = report.rows[0].actions.find(action => action.operation === 'search');
  assert.equal(search.code, 'SOURCE_FAILURE');
  assert.equal(search.state, 'engine_incompatible');
  assert.deepEqual(search.network.map(event => event.statusCode), [503]);
  assert.equal(report.rows[0].unload, 'complete');
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(report.rows[0].sourceUnchanged, true);
  assert.equal(text.includes(origin), false);
  assert.equal(text.includes('SYNTHETIC-PRIVATE-UPSTREAM'), false);
});

test('Cat guest BigInt results are fatal CLI engine errors with or without same-action HTTP failures', async t => {
  const requests = [];
  const server = createServer((request, response) => {
    requests.push(request.url);
    response.statusCode = 503;
    response.end('SYNTHETIC-PRIVATE-UPSTREAM');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  for (const afterHttp of [false, true]) {
    await t.test(afterHttp ? 'HTTP 503 then BigInt' : 'direct BigInt', async t => {
      const { directory, output } = area(t);
      const file = join(directory, 'spider.js');
      const before = requests.length;
      writeFileSync(file, `export default {
        ${afterHttp ? 'async ' : ''}searchContent() {
          ${afterHttp ? `await fetch(${JSON.stringify(`${origin}/unavailable`)});` : ''}
          return 1n;
        }
      };`);
      const result = await run(['--scripts', file, '--output', output, '--live']);
      assert.notEqual(result.code, 0);
      const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
      assert.equal(report.rows[0].protocol, 'cat');
      assert.equal(report.rows[0].initialization, 'ready');
      assert.equal(report.summary.declaredActions, 1);
      assert.equal(report.summary.invokedActions, 1);
      assert.equal(report.summary.returned, 0);
      assert.equal(report.summary.engine_incompatible, 1);
      assert.equal(report.summary.upstream_failure, 0);
      assert.equal(report.summary.runtime_limit, 0);
      assert.equal(report.summary.not_exercised, 0);
      assert.equal(report.summary.cleanupFailures, 0);
      assert.equal(report.rows[0].actions.length, 1);
      const search = report.rows[0].actions[0];
      assert.equal(search.operation, 'search');
      assert.equal(search.code, 'SOURCE_FAILURE');
      assert.equal(search.state, 'engine_incompatible');
      assert.deepEqual(search.network.map(event => event.statusCode), afterHttp ? [503] : []);
      assert.deepEqual(requests.slice(before), afterHttp ? ['/unavailable'] : []);
      assert.equal(report.rows[0].unload, 'complete');
      assert.equal(report.rows[0].cleanup, 'complete');
      assert.equal(report.rows[0].sourceUnchanged, true);
      assert.equal(text.includes(origin), false);
      assert.equal(text.includes('SYNTHETIC-PRIVATE-UPSTREAM'), false);
      assert.equal((result.stdout + result.stderr).includes('SYNTHETIC-PRIVATE-UPSTREAM'), false);
    });
  }
});

test('Reader live acceptance preserves genuine network and configuration error attribution', async t => {
  const { directory, output } = area(t);
  const server = createServer((_request, response) => response.destroy());
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const file = join(directory, 'source.json');
  writeFileSync(file, JSON.stringify({
    bookSourceName: 'Network and login fixture',
    bookSourceUrl: origin,
    login: true,
    html: `<script>
      async function search() { await fetch(${JSON.stringify(`${origin}/network`)}); return []; }
      function getloginurl() { return ''; }
    </script>`,
  }));
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: { 'source.json': { actions: { image: { body: '' } } } },
  }));
  const result = await run([
    '--scripts', file, '--records', records, '--output', output, '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state, action.code]), [
    ['search', 'upstream_failure', 'NETWORK_ERROR'],
    ['loginStatus', 'returned', undefined],
    ['login', 'account_region_configuration_required', 'WEB_INTERACTION_REQUIRED'],
    ['image', 'returned', undefined],
  ]);
  assert.equal(report.summary.upstream_failure, 1);
  assert.equal(report.summary.account_region_configuration_required, 1);
  assert.equal(report.summary.engine_incompatible, 0);
  assert.equal(report.summary.not_exercised, 0);
  assert.equal(report.rows[0].unload, 'complete');
  assert.equal(report.rows[0].cleanup, 'complete');
});

test('QYSG HTTP bridge sends standalone contentType and honors explicit headers on the wire', async t => {
  const { directory, output } = area(t);
  const requests = [];
  const server = createServer((request, response) => {
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      requests.push({
        path: request.url,
        method: request.method,
        contentType: request.headers['content-type'],
        contentTypeHeaders: request.rawHeaders.filter((value, index) =>
          index % 2 === 0 && value.toLowerCase() === 'content-type').length,
        body: Buffer.concat(chunks).toString(),
      });
      response.setHeader('Content-Type', 'application/json');
      response.end('[]');
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const file = join(directory, 'source.html');
  writeFileSync(file, `<script>
    async function search() {
      await window.flutter_inappwebview.callHandler(
        'http', 'POST', ${JSON.stringify(`${origin}/form`)},
        { key: 'a b' }, {}, true, 'application/x-www-form-urlencoded'
      );
      await window.flutter_inappwebview.callHandler(
        'http', 'POST', ${JSON.stringify(`${origin}/json`)},
        { key: 'a b' }, {}, true, 'application/json'
      );
      await window.flutter_inappwebview.callHandler(
        'http', 'POST', ${JSON.stringify(`${origin}/explicit`)},
        { key: 'a b' }, { 'cOnTeNt-TyPe': 'application/json' },
        true, 'application/x-www-form-urlencoded'
      );
      return [];
    }
  </script>`);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    source: { 'source.html': { actions: { image: { body: '' } } } },
  }));
  const result = await run([
    '--scripts', file, '--records', records, '--output', output, '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  assert.deepEqual(requests, [
    { path: '/form', method: 'POST', contentType: 'application/x-www-form-urlencoded',
      contentTypeHeaders: 1, body: 'key=a+b' },
    { path: '/json', method: 'POST', contentType: 'application/json',
      contentTypeHeaders: 1, body: '{"key":"a b"}' },
    { path: '/explicit', method: 'POST', contentType: 'application/json',
      contentTypeHeaders: 1, body: '{"key":"a b"}' },
  ]);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.returned, 3);
  assert.equal(report.summary.engine_incompatible, 0);
  assert.equal(report.rows[0].unload, 'complete');
  assert.equal(report.rows[0].cleanup, 'complete');
});

test('Reader protocol override accepts ambiguous Spider input and never falls back to LX or Cat Node', async t => {
  const { directory, output } = area(t);
  const file = join(directory, 'source.js');
  writeFileSync(file, 'const spider = { search(){ return { list: [] }; } };');
  const automatic = await run(['--scripts', file, '--output', output]);
  assert.notEqual(automatic.code, 0);
  assert.match(automatic.stderr, /UNSUPPORTED_PROTOCOL/);
  const explicit = await run(['--scripts', file, '--output', output, '--protocol', 'cat', '--live']);
  assert.equal(explicit.code, 0, explicit.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.protocolOverride, 'cat');
  assert.equal(report.summary.returned, 1);

  writeFileSync(file, "const http = require('node:http'); module.exports = { search(){ return {}; } };");
  const native = await run(['--scripts', file, '--output', output, '--protocol', 'cat']);
  assert.notEqual(native.code, 0);
  const rejected = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(rejected.rows[0].initialization, 'failed');
  assert.equal(rejected.rows[0].code, 'UNSUPPORTED_DEPENDENCY');
  assert.equal(rejected.summary.invokedActions, 0);
  assert.equal(rejected.rows[0].sourceUnchanged, true);
});

test('Reader rejects invalid options, unsafe output and private malformed input without echoing it', async t => {
  const { directory, output } = area(t);
  const file = join(directory, 'source.html');
  writeFileSync(file, '<script>function search(){ return []; }</script>');
  for (const extra of [
    ['--protocol', 'lx'], ['--protocol', 'music'], ['--live', '--live'],
    ['--protocol', 'cat', '--protocol', 'qysg'],
    ['--scripts', file],
  ]) {
    const result = await run(['--scripts', file, '--output', output, ...extra]);
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /CLI_USAGE/);
  }
  for (const target of ['report.json', join(process.cwd(), 'escaped-report.json')]) {
    const result = await run(['--scripts', file, '--output', target]);
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /CLI_USAGE|OUTPUT_ESCAPE/);
  }
  const records = join(directory, 'records.json');
  writeFileSync(records, 'SYNTHETIC-PRIVATE-MALFORMED-RECORDS');
  const malformed = await run(['--scripts', file, '--records', records, '--output', output]);
  assert.notEqual(malformed.code, 0);
  assert.match(malformed.stderr, /INVALID_RECORDS/);
  assert.equal((malformed.stdout + malformed.stderr).includes('SYNTHETIC-PRIVATE'), false);
  writeFileSync(file, Buffer.from([0xff, 0x3c, 0x2f]));
  const encoding = await run(['--scripts', file, '--output', output]);
  assert.notEqual(encoding.code, 0);
  assert.match(encoding.stderr, /SOURCE_ENCODING/);
});

test('Reader recursively rejects linked source directories', async t => {
  const { directory, output } = area(t);
  const input = join(directory, 'input'), target = join(directory, 'target');
  mkdirSync(input);
  mkdirSync(target);
  writeFileSync(join(target, 'source.html'), '<script>function search(){ return []; }</script>');
  symlinkSync(target, join(input, 'linked'), 'junction');
  const result = await run(['--scripts', input, '--output', output]);
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /LINKED_SOURCE/);
});

test('Reader keeps failed initialization, thrown invocation and unload or close errors fatal', async t => {
  for (const scenario of [
    'initialization', 'invoke', 'invoke-after-upstream', 'limit',
    'unload', 'unload-throw', 'close', 'close-throw',
  ]) {
    await t.test(scenario, async t => {
      const { directory, output } = area(t);
      const payload = join(directory, 'payload');
      const modulePath = 'app/modules/source-runtime.mjs';
      const hostPath = 'node_modules/@meowfilm/script-host/src/index.js';
      const sourcePath = 'node_modules/@meowfilm/source-runtime/src/index.js';
      for (const path of ['app/modules', 'node_modules/@meowfilm/script-host/src',
        'node_modules/@meowfilm/source-runtime/src']) {
        mkdirSync(join(payload, path), { recursive: true });
      }
      writeFileSync(join(payload, 'runtime-manifest.json'), JSON.stringify({
        status: 'complete', files: [modulePath, hostPath, sourcePath].map(path => ({ path })),
      }));
      writeFileSync(join(payload, hostPath), `
        export const createNodeNetwork = () => async () => ({ statusCode: 503, body: '{}' });
        export const createScriptHost = () => {};
        export class ScriptHostError extends Error {}
      `);
      writeFileSync(join(payload, sourcePath), 'export {};');
      writeFileSync(join(payload, modulePath), `
        export function createSourceRuntimeModule({ network }) {
          const scenario = ${JSON.stringify(scenario)};
          const failure = () => { throw Object.assign(new Error('SYNTHETIC-PRIVATE'), {
            code: scenario.startsWith('invoke') ? 'SOURCE_FAILURE' : 'SOURCE_CLEANUP',
          }); };
          return {
            methods: {
              'source.runtime.stage.begin': async () => ({ ok: true, token: 'fixture', chunkBytes: 65536 }),
              'source.runtime.stage.chunk': async () => ({ ok: true }),
              'source.runtime.load': async () => ({ ok: true, instance: {
                state: scenario === 'initialization' ? 'failed' : 'ready',
                error: 'SOURCE_FAILURE', generation: 1, protocol: 'qysg',
                capabilities: { source: { protocol: 'qysg', actions: scenario === 'invoke-after-upstream'
                  ? ['search', 'exploreKinds'] : ['search'] } },
              } }),
              'source.runtime.invoke': async ({ action }) => {
                if (scenario === 'invoke' || scenario === 'invoke-after-upstream' && action === 'exploreKinds')
                  return failure();
                if (scenario === 'limit') return { ok: false, code: 'LIMIT' };
                if (scenario === 'invoke-after-upstream') await network({ scope: { requestId: 1 } });
                return { ok: true, value: [] };
              },
              'source.runtime.unload': async () => scenario === 'unload-throw'
                ? failure() : ({ ok: scenario !== 'unload', code: 'SOURCE_CLEANUP' }),
            },
            close: async () => scenario === 'close-throw'
              ? failure() : ({ ok: scenario !== 'close', code: 'SOURCE_CLEANUP' }),
          };
        }
      `);
      const file = join(directory, 'source.html');
      writeFileSync(file, '<script>function search(){ return []; }</script>');
      const result = await run([
        '--scripts', file, '--payload', payload, '--output', output, '--live',
      ]);
      assert.notEqual(result.code, 0);
      const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
      const row = report.rows[0];
      assert.equal(report.summary.initialized, scenario === 'initialization' ? 0 : 1);
      assert.equal(row.sourceUnchanged, true);
      if (scenario.startsWith('unload')) {
        assert.equal(report.summary.unloadFailures, 1);
        assert.equal(row.unloadCode, 'SOURCE_CLEANUP');
        assert.equal(row.cleanup, 'complete');
      } else if (scenario.startsWith('close')) {
        assert.equal(report.summary.cleanupFailures, 1);
      } else if (scenario === 'limit') {
        assert.equal(report.summary.runtime_limit, 1);
      } else {
        assert.equal(row.code, 'SOURCE_FAILURE');
        assert.equal(report.summary.engine_incompatible, 1);
        if (scenario === 'invoke-after-upstream') assert.equal(report.summary.upstream_failure, 0);
      }
      assert.equal(text.includes('SYNTHETIC-PRIVATE'), false);
      assert.equal((result.stdout + result.stderr).includes('SYNTHETIC-PRIVATE'), false);
    });
  }
});
