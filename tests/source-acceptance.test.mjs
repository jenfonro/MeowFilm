import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import {
  mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
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

async function run(args) {
  const child = spawn(process.execPath, ['scripts/accept-sources.mjs', ...args], {
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

test('offline inventory accepts LX, Cat, QYSG, Legado and split JSON arrays without copying secrets', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-VALUE';
  writeFileSync(join(directory, 'music.js'), `
    const privateValue = ${JSON.stringify(privateValue)};
    lx.on('request', async () => privateValue);
    lx.send('inited', { sources: {
      kw: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
    }});
  `);
  writeFileSync(join(directory, 'cat.js'), `
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

  const result = await run(['--scripts', directory, '--output', output]);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8');
  const report = JSON.parse(text);
  assert.equal(report.schema, 2);
  assert.equal(report.summary.sources, 6);
  assert.equal(report.summary.initialized, 6);
  assert.deepEqual(report.rows.map(row => row.protocol).sort(),
    ['cat', 'legado', 'legado', 'music', 'qysg', 'qysg']);
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

test('live reports use fixed upstream, account, host and engine classifications without leaking bodies', async t => {
  const { directory, output } = area(t);
  const privateValue = 'SYNTHETIC-PRIVATE-VALUE';
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
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request', async ({ action }) => {
      if (action === 'search') return { list: [{ songmid: 'fixture' }] };
      if (action === 'upstream') return await new Promise((resolve, reject) =>
        lx.request(${JSON.stringify(origin + '/upstream')}, {}, error =>
          error ? reject(error) : reject(Error(${JSON.stringify(privateValue)}))));
      if (action === 'account') return await new Promise((resolve, reject) =>
        lx.request(${JSON.stringify(origin + '/account')}, {}, error =>
          error ? reject(error) : reject(Error(${JSON.stringify(privateValue)}))));
      if (action === 'host') return lx.utils.crypto.aesEncrypt(
        ${JSON.stringify(privateValue)}, 'unsupported', 'key', null);
      return missingFixtureFunction();
    });
    lx.send('inited', { sources: {
      kw: {
        type: 'music',
        actions: ['search', 'upstream', 'account', 'host', 'engine'],
        qualitys: []
      }
    }});
  `);
  const result = await run([
    '--scripts', directory,
    '--output', output,
    '--live',
  ]);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8');
  const report = JSON.parse(text);
  assert.equal(report.summary.returned, 1);
  assert.equal(report.summary.upstream_failure, 1);
  assert.equal(report.summary.account_region_configuration_required, 1);
  assert.equal(report.summary.host_capability_missing, 1);
  assert.equal(report.summary.engine_incompatible, 1);
  assert.equal(report.summary.cleanupFailures, 0);
  assert.equal(text.includes(privateValue), false);
  assert.equal(text.includes(origin), false);
});

test('missing action input is not exercised and makes live acceptance nonzero', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request', async () => 'unexpected');
    lx.send('inited', { sources: {
      kw: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
    }});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.not_exercised, 1);
  assert.equal(report.summary.invokedActions, 0);
  assert.equal(report.rows[0].actions[0].state, 'not_exercised');
});

test('music records separate musicInfo from explicit action payloads', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request', async ({ action, info }) => {
      if (action === 'musicUrl') return info.musicInfo.url;
      return info.custom;
    });
    lx.send('inited', { sources: {
      kw: { type: 'music', actions: ['musicUrl', 'custom'], qualitys: ['128k'] }
    }});
  `);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({
    music: {
      'source.js': {
        sites: {
          kw: {
            musicInfo: { url: 'https://media.invalid/fixture.mp3' },
            actions: { custom: { custom: 'fixture' } },
          },
        },
      },
    },
  }));
  const result = await run([
    '--scripts', directory,
    '--records', records,
    '--output', output,
    '--live',
  ]);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['custom', 'returned'],
    ['musicUrl', 'returned'],
  ]);
  assert.equal(report.summary.not_exercised, 0);
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
