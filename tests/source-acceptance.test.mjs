import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { classifyGuestMessage, classifySourceFailure, sourceDiagnosticObservers } from '../scripts/source-acceptance-diagnostics.mjs';
import { describeScriptError } from '../packages/script-host/src/script-diagnostic.js';
const temporaryRoot = join(process.cwd(), 'tmp/test-results');

test('source diagnostics classify known configuration and data errors without retaining text', () => {
  for (const [message, reason] of [
    ['还没有配置夸克 Cookie，请先去配置中心登录夸克', 'configuration'],
    ['xstree 追更表需要登录：请在前端源配置中填写账号和密码', 'configuration'],
    ['次元城播放需要登录，请在站源设置中填写账号和密码', 'configuration'],
    ['请先在 TG 视频源设置中添加频道', 'configuration'],
    ['TG 视频标识无效', 'input'],
    ['瓜子体育暂无直播源', 'upstream'],
    ['unexpected character', 'invalid_json'],
    ['Unexpected end of JSON input', 'empty_json'],
    ['Unexpected token < in JSON at position 0', 'invalid_json'],
  ]) {
    assert.equal(classifyGuestMessage(message), reason);
  }
  assert.deepEqual(describeScriptError({ name: 'TypeError', code: 'ERR_INVALID_PROTOCOL',
    message: 'SYNTHETIC-PRIVATE-URL' }), { kind: 'TypeError', reason: 'input', code: 'ERR_INVALID_PROTOCOL' });
  for (const message of ['config required: SYNTHETIC-PRIVATE', 'upstream HTTP request failed',
    '请先登录', 'Unexpected end of JSON input']) {
    assert.deepEqual(describeScriptError({ name: 'Error', message }), { kind: 'Error', reason: 'script' });
  }
});

test('acceptance-only observation classifies text without retaining it or replacing structured errors', () => {
  const diagnostics = [], observer = sourceDiagnosticObservers(diagnostics);
  observer.onDiagnostic({ kind: 'Error', reason: 'script' });
  observer.onScriptError({ kind: 'Error', reason: 'script', message: 'config required: SYNTHETIC-PRIVATE' });
  observer.onDiagnostic({ kind: 'ScriptHostError', reason: 'host', code: 'SCRIPT_EXCEPTION' });
  observer.onScriptError({ kind: 'Error', reason: 'upstream', code: 'ECONNABORTED',
    message: 'SYNTHETIC-PRIVATE' });
  observer.onScriptError({ kind: 'ReferenceError', reason: 'reference', reference: 'Buffer',
    message: 'Buffer is not defined: SYNTHETIC-PRIVATE' });
  observer.onScriptError({ kind: 'TypeError', reason: 'input', member: 'final',
    message: 'final is not a function: SYNTHETIC-PRIVATE' });
  assert.deepEqual(diagnostics, [
    { kind: 'Error', reason: 'configuration' },
    { kind: 'ScriptHostError', reason: 'host', code: 'SCRIPT_EXCEPTION' },
    { kind: 'Error', reason: 'upstream', code: 'ECONNABORTED' },
    { kind: 'ReferenceError', reason: 'reference', reference: 'Buffer' },
    { kind: 'TypeError', reason: 'script', member: 'final' },
  ]);
  assert.equal(JSON.stringify(diagnostics).includes('SYNTHETIC-PRIVATE'), false);
});

test('timeout attribution needs causal network evidence and preserves host failures', () => {
  const reply = { code: 'TIMEOUT' }, upstream = { scope: 'startup', code: 'NETWORK_ERROR' };
  assert.equal(classifySourceFailure('cat', reply, [], []), 'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', reply, [upstream], []), 'upstream_failure');
  assert.equal(classifySourceFailure('cat', reply, [{ ...upstream, scope: 'session' }], []),
    'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', reply, [upstream], [
    { reason: 'host', code: 'SCRIPT_EXCEPTION' },
  ]), 'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', { code: 'SCRIPT_EXCEPTION' }, [], [
    { kind: 'Error', reason: 'upstream', code: 'ECONNABORTED' },
  ]), 'upstream_failure');
});

test('session failure notifications retain the guest cause without concealing real host failures', () => {
  const reply = { code: 'SCRIPT_EXCEPTION' };
  const notification = { reason: 'host', operation: 'session.failed', code: 'SCRIPT_EXCEPTION' };
  const guest = { kind: 'Error', reason: 'upstream', code: 'ECONNABORTED' };
  assert.equal(classifySourceFailure('cat', reply, [], [guest, notification]), 'upstream_failure');
  assert.equal(classifySourceFailure('cat', reply, [], [notification]), 'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', reply, [], [
    guest, { ...notification, operation: 'worker.exit' },
  ]), 'requires_diagnosis');
});

test('source JSON error attribution needs request evidence and cannot conceal host failures', () => {
  const reply = { code: 'UPSTREAM_HTTP_ERROR' };
  const response = { scope: 'request', statusCode: 200 };
  const invalid = { kind: 'Error', reason: 'invalid_json' }, empty = { kind: 'Error', reason: 'empty_json' };
  assert.equal(classifySourceFailure('cat', reply, [response], [invalid]), 'upstream_failure');
  assert.equal(classifySourceFailure('cat', reply, [response], [empty]), 'upstream_failure');
  assert.equal(classifySourceFailure('cat', reply, [], [empty]), 'input_required');
  assert.equal(classifySourceFailure('cat', reply, [], [invalid]), 'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', reply, [{ ...response, scope: 'session' }], [invalid]),
    'requires_diagnosis');
  assert.equal(classifySourceFailure('cat', reply, [], [
    { kind: 'SyntaxError', reason: 'script' }, { reason: 'http', statusCode: 500 },
  ]), 'requires_diagnosis');
  for (const [code, expected] of [
    ['PERMISSION_DENIED', 'script_rejected'], ['UNMAPPED_LOCAL_URL', 'script_rejected'],
    ['SCRIPT_EXCEPTION', 'requires_diagnosis'], ['CAPABILITY_UNAVAILABLE', 'host_failure'],
    ['RESOURCE_LIMIT', 'runtime_limit'],
  ]) {
    assert.equal(classifySourceFailure('cat', reply, [response], [
      { reason: 'host', code }, invalid,
    ]), expected);
  }
});

function area(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const directory = mkdtempSync(join(temporaryRoot, 'source-acceptance-'));
  t.after(() => rmSync(directory, { recursive: true }));
  return { directory, output: join(directory, 'report.json') };
}

async function run(args) {
  const child = spawn(process.execPath, ['scripts/accept-sources.mjs', ...args], {
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let stdout = '', stderr = '';
  child.stdout.on('data', value => { stdout += value; });
  child.stderr.on('data', value => { stderr += value; });
  return await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', code => resolve({ code, stdout, stderr }));
  });
}

test('external-source initialization report does not copy scripts or private values', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    const privateValue = 'SYNTHETIC-PRIVATE-VALUE';
    lx.on('request',async()=>privateValue);
    lx.send('inited',{sources:{kw:{type:'music',actions:['musicUrl'],qualitys:['128k']}}});
  `);
  const result = await run(['--scripts', directory, '--output', output]);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.mode, 'offline-init');
  assert.equal(report.summary.initialized, 1);
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(report.rows[0].sourceUnchanged, true);
  assert.equal(report.summary.changedSources, 0);
  assert.equal(text.includes('SYNTHETIC-PRIVATE-VALUE'), false);
  assert.equal(report.rows[0].actions.length, 0);
});

test('Cat acceptance rejects offline mode because native guest networking cannot be fully blocked',
  async t => {
    const { directory, output } = area(t);
    writeFileSync(join(directory, 'source.js'), 'module.exports = { async home() { return {}; } };');
    const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output]);
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /Cat acceptance requires --live/);
  });

test('live acceptance distinguishes returned data, configuration requirements and upstream HTTP failures', async t => {
  const { directory, output } = area(t);
  const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/failure') response.statusCode = 503;
    response.end(JSON.stringify({ url: 'https://fixture.invalid/audio' }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const endpoint = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request',async ({action}) => {
      if(action==='search') return {list:[{songmid:'synthetic'}]};
      if(action==='pic') throw Error('api_key SYNTHETIC-PRIVATE-VALUE');
      return await new Promise((resolve,reject) =>
        lx.request(${JSON.stringify(endpoint)}+(action==='lyric'?'/failure':''), {}, (error,response,body) =>
          error?reject(error):response.statusCode>=400?reject(Error('HTTP 503')):resolve(body.url)));
    });
    lx.send('inited',{sources:{kw:{type:'music',actions:['search','musicUrl','pic','lyric'],
      qualitys:[{type:'128k',label:'standard'}]}}});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.summary.returned, 2);
  assert.equal(report.summary.declaredActions, 4);
  assert.equal(report.summary.invokedActions, 4);
  assert.equal(report.summary.configurationRequired, 1);
  assert.equal(report.summary.upstreamFailures, 1);
  assert.equal(report.rows[0].actions.find(action => action.operation === 'musicUrl').result.kind, 'url');
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(text.includes('SYNTHETIC-PRIVATE-VALUE'), false);
  assert.equal(text.includes('https://fixture.invalid/audio'), false);
});

test('empty external-source inventories cannot satisfy acceptance', async t => {
  const { directory, output } = area(t);
  const result = await run(['--scripts', directory, '--output', output]);
  assert.notEqual(result.code, 0);
});

test('missing guest APIs and unclassified failures fail live acceptance instead of passing initialization', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request',async ({action}) => {
      if(action==='search') return {list:[{songmid:'synthetic'}]};
      if(action==='musicUrl') return unsupportedMissingFunction();
      return lx.utils.crypto.aesEncrypt('SYNTHETIC-PRIVATE-VALUE','unsupported','key',null);
    });
    lx.send('inited',{sources:{kw:{type:'music',actions:['search','musicUrl','pic'],qualitys:['128k']}}});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.summary.initialized, 1);
  assert.equal(report.summary.scriptRejected, 1);
  assert.equal(report.summary.hostFailures, 1);
  assert.equal(report.summary.cleanupFailures, 0);
  assert.equal(text.includes('SYNTHETIC-PRIVATE-VALUE'), false);
});

test('unknown script failures cannot satisfy live acceptance', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request',async ({action}) => {
      if(action==='search') return {list:[{songmid:'synthetic'}]};
      throw Error('SYNTHETIC-PRIVATE-VALUE');
    });
    lx.send('inited',{sources:{kw:{type:'music',actions:['search','musicUrl'],qualitys:['128k']}}});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.summary.requiresDiagnosis, 1);
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(text.includes('SYNTHETIC-PRIVATE-VALUE'), false);
});

test('HTTP 200 provider error bodies remain upstream failures and local data is not fabricated', async t => {
  const { directory, output } = area(t);
  const server = createServer((_request, response) => {
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ status: 0, err_code: 42, data: null }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const endpoint = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request',async ({action}) => {
      if(action==='search') return {list:[{songmid:'synthetic'}]};
      return await new Promise((resolve,reject) => lx.request(${JSON.stringify(endpoint)}, {},
        (error,_response,body) => {
          if (error) return reject(error);
          const payload = typeof body === 'string' ? JSON.parse(body) : body;
          reject(Error(String(payload.err_code)));
        }));
    });
    lx.send('inited',{sources:{
      wy:{type:'music',actions:['search','musicUrl'],qualitys:['128k']},
      local:{type:'music',actions:['musicUrl'],qualitys:[]}
    }});
  `);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({ wy: { songmid: 'synthetic' } }));
  const result = await run(['--scripts', directory, '--output', output, '--records', records, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.returned, 1);
  assert.equal(report.summary.upstreamFailures, 1);
  assert.equal(report.summary.inputRequired, 1);
  assert.equal(report.summary.invokedActions, 2);
  assert.equal(report.rows[0].actions.find(action => action.siteId === 'local').state, 'input_required');
});

test('six-digit provider business errors are diagnosed without exposing response text', async t => {
  const { directory, output } = area(t);
  const server = createServer((_request, response) => {
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ code: '100002', info: 'SYNTHETIC-PRIVATE-VALUE' }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const endpoint = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.js'), `
    lx.on('request',async ({action}) => {
      if(action==='search') return {list:[{songmid:'synthetic'}]};
      return await new Promise((resolve,reject) => lx.request(${JSON.stringify(endpoint)}, {},
        (error,_response,body) => {
          if (error) return reject(error);
          const payload = typeof body === 'string' ? JSON.parse(body) : body;
          reject(Error(payload.info));
        }));
    });
    lx.send('inited',{sources:{mg:{type:'music',actions:['search','musicUrl'],qualitys:['128k']}}});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.summary.upstreamFailures, 1);
  assert.equal(report.summary.requiresDiagnosis, 0);
  assert.equal(report.rows[0].actions.find(action => action.operation === 'musicUrl').network[0].businessCode, 100002);
  assert.equal(text.includes('SYNTHETIC-PRIVATE-VALUE'), false);
});

test('initialization snapshots do not claim a background request completed before it settled', async t => {
  const { directory, output } = area(t);
  let background, released = false;
  const finish = response => {
    response.setHeader('Content-Type', 'application/json');
    response.end('{}');
  };
  const server = createServer((request, response) => {
    if (request.url === '/background') {
      if (released) finish(response);
      else background = response;
    } else {
      released = true;
      if (background) finish(background);
      finish(response);
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const endpoint = `http://127.0.0.1:${server.address().port}`;
  writeFileSync(join(directory, 'source.js'), `
    const background = new Promise((resolve,reject) =>
      lx.request(${JSON.stringify(endpoint)}+'/background',{},error=>error?reject(error):resolve()));
    lx.on('request',async () => {
      await new Promise((resolve,reject) =>
        lx.request(${JSON.stringify(endpoint)}+'/release',{},error=>error?reject(error):resolve()));
      await background;
      return {list:[{songmid:'synthetic'}]};
    });
    lx.send('inited',{sources:{kw:{type:'music',actions:['search'],qualitys:[]}}});
  `);
  const result = await run(['--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8')), row = report.rows[0];
  assert.equal(report.summary.returned, 1);
  assert.deepEqual(row.initializationNetwork, [{ state: 'pending' }]);
  assert.deepEqual(row.backgroundNetwork, [{ state: 'completed', statusCode: 200 }]);
  assert.equal(row.cleanup, 'complete');
});

test('Cat acceptance derives standard home, search, detail and play inputs from returned data', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    const privateValue = 'SYNTHETIC-CAT-PRIVATE-VALUE';
    module.exports = {
      async home(filter) {
        if (filter !== true) throw Error('missing filter');
        return { class: [{ type_id: 'films', type_name: privateValue.length ? 'Films' : '' }] };
      },
      async search(keyword, quick, page) {
        if (keyword !== '海阔天空' || quick !== false || page !== 1) throw Error('bad search input');
        return { page: 1, pagecount: 1, list: [{ vod_id: 'film-1', vod_name: 'Fixture' }] };
      },
      async category(id, page, filter, filters) {
        if (id !== 'films' || page !== 1 || filter !== true || Object.keys(filters).length)
          throw Error('bad category input');
        return { page: 1, list: [{ vod_id: 'film-1', vod_name: 'Fixture' }] };
      },
      async detail(id) {
        if (id !== 'film-1') throw Error('bad detail input');
        return { list: [{ vod_id: 'film-1', vod_play_from: 'Fixture',
          vod_play_url: 'Episode$media-1' }] };
      },
      async play(flag, id, flags) {
        if (flag !== 'Fixture' || id !== 'media-1' || flags[0] !== 'Fixture')
          throw Error('bad play input');
        return { parse: 0, url: 'https://fixture.invalid/media-1' };
      },
    };
  `);
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.kind, 'cat');
  assert.equal(report.summary.initialized, 1);
  assert.equal(report.summary.declaredActions, 5);
  assert.equal(report.summary.invokedActions, 5);
  assert.equal(report.summary.returned, 5);
  assert.deepEqual(report.rows[0].actions.map(action => action.operation),
    ['home', 'search', 'category', 'detail', 'play']);
  assert.equal(report.rows[0].actions.at(-1).result.kind, 'url');
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(report.rows[0].sourceUnchanged, true);
  assert.equal(text.includes('SYNTHETIC-CAT-PRIVATE-VALUE'), false);
  assert.equal(text.includes('https://fixture.invalid/media-1'), false);
});

test('Cat acceptance does not invoke unknown extensions without input or hide an unclassified handler failure',
  async t => {
    const { directory, output } = area(t);
    writeFileSync(join(directory, 'source.js'), `
      module.exports = {
        init() {},
        search() { throw Error('SYNTHETIC-CAT-PRIVATE-VALUE'); },
        resetAccount() { throw Error('extension ran without supplied input'); },
      };
    `);
    const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
    assert.notEqual(result.code, 0);
    const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
    assert.equal(report.summary.returned, 1);
    assert.equal(report.rows[0].actions.find(action => action.operation === 'init').result.kind, 'empty');
    assert.equal(report.summary.requiresDiagnosis, 1);
    assert.equal(report.summary.inputRequired, 1);
    assert.equal(report.summary.upstreamFailures, 0);
    assert.equal(report.summary.hostFailures, 0);
    assert.equal(report.summary.cleanupFailures, 0);
    assert.equal(text.includes('SYNTHETIC-CAT-PRIVATE-VALUE'), false);
    assert.equal(text.includes('extension ran without supplied input'), false);
  });

test('Cat acceptance invokes a non-core extension with its supplied protocol input', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    module.exports = {
      customSearch(input) {
        if (input.query !== 'fixture') throw Error('wrong extension input');
        return { list: [{ vod_id: 'custom', vod_name: 'Custom' }] };
      },
    };
  `);
  const records = join(directory, 'records.json');
  writeFileSync(records, JSON.stringify({ default: { customSearch: { query: 'fixture' } } }));
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output,
    '--records', records, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.declaredActions, 1);
  assert.equal(report.summary.invokedActions, 1);
  assert.equal(report.summary.returned, 1);
  assert.equal(report.rows[0].actions[0].operation, 'customSearch');
});

test('live Cat acceptance uses the production native network path instead of an injected adapter',
  async t => {
    const { directory, output } = area(t);
    writeFileSync(join(directory, 'source.js'), `
      const dns = require('dns');
      module.exports = {
        async search() {
          const code = await new Promise(resolve =>
            dns.lookup('cat-acceptance.invalid', error => resolve(error?.code || 'OK')));
          if (code === 'CAPABILITY_UNAVAILABLE') {
            const error = new Error(code);
            error.code = code;
            throw error;
          }
          return { list: [{ vod_id: 'native-network', vod_name: 'Native network' }] };
        },
      };
    `);
    const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
    assert.equal(result.code, 0, result.stderr);
    const report = JSON.parse(readFileSync(output, 'utf8'));
    assert.equal(report.summary.returned, 1);
    assert.equal(report.rows[0].actions[0].state, 'returned');
  });

test('Cat acceptance keeps initialization audit at the startup boundary and does not inject fixture config',
  async t => {
    const { directory, output } = area(t);
    writeFileSync(join(directory, 'source.js'), `
      const http = require('http');
      const https = require('https');
      let server;
      const probe = () => new Promise(resolve => {
        const request = https.get('https://cat-acceptance.invalid/probe', response => {
          response.resume();
          response.once('end', resolve);
        });
        request.once('error', resolve);
      });
      module.exports = {
        async start(config) {
          if (Object.hasOwn(config, 'id') || Object.hasOwn(config, 'name'))
            throw Error('acceptance injected fixture config');
          server = http.createServer(async (request, response) => {
            if (request.url === '/full-config' || request.url === '/config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'audit', name: 'Audit', api: '/api/audit', actions: ['search'],
              }] } }));
              return;
            }
            if (request.url === '/api/audit/search') {
              await probe();
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ list: [{ vod_id: 'audit-1', vod_name: 'Audit' }] }));
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
        },
        stop() { server?.close(); },
      };
    `);
    const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
    assert.equal(result.code, 0, result.stderr);
    const row = JSON.parse(readFileSync(output, 'utf8')).rows[0];
    assert.deepEqual(row.initializationNetwork, []);
    assert.equal(row.actions[0].operation, 'search');
    assert.equal(row.actions[0].state, 'returned');
    assert.equal(row.actions[0].network.length, 1);
    assert.equal(row.actions[0].network[0].code, 'NETWORK_ERROR');
  });

test('Cat acceptance reports an enabled site whose actions cannot be discovered', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    const http = require('http');
    let server;
    module.exports = {
      start() {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'opaque', name: 'Opaque', api: '/api/opaque', actions: [],
            }] } }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(9988, '127.0.0.1');
      },
      stop() { server?.close(); },
    };
  `);
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.initialized, 1);
  assert.equal(report.summary.declaredActions, 0);
  assert.equal(report.summary.requiresDiagnosis, 1);
  assert.deepEqual(report.rows[0].actions[0], {
    siteId: 'opaque', operation: 'discover',
    state: 'requires_diagnosis', reason: 'actions_undiscovered',
  });
  assert.equal(report.rows[0].cleanup, 'complete');
});

test('Cat acceptance preserves failed initialization codes and separately attributes the cause', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    module.exports.start = async () => {
      const error = new Error('SYNTHETIC-PRIVATE-TIMEOUT');
      error.code = 'ECONNABORTED';
      throw error;
    };
  `);
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
  assert.notEqual(result.code, 0);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.equal(report.rows[0].initialization, 'failed');
  assert.equal(report.rows[0].code, 'SCRIPT_EXCEPTION');
  assert.equal(report.rows[0].initializationFailure, 'upstream_failure');
  assert.equal(report.rows[0].cleanup, 'complete');
  assert.equal(report.summary.initialized, 0);
  assert.equal(text.includes('SYNTHETIC-PRIVATE'), false);
});

test('Cat acceptance classifies structured service failures without logging exception text', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    let server;
    module.exports.start = () => {
      server = catServerFactory((request, response) => {
        response.setHeader('content-type', 'application/json');
        if (request.url === '/config' || request.url === '/full-config') {
          response.end(JSON.stringify({ video: { sites: [{
            key: 'errors', api: '/errors', actions: ['home', 'search', 'test', 'init'],
          }] } })); return;
        }
        response.statusCode = 500;
        response.end(JSON.stringify(request.url.endsWith('/init')
          ? { code: 'ECONNABORTED', err: 'SYNTHETIC-PRIVATE-TIMEOUT' }
          : request.url.endsWith('/home')
          ? { message: 'config required: SYNTHETIC-PRIVATE-CREDENTIAL' }
          : request.url.endsWith('/search')
            ? { error: { code: 'ERR_INVALID_URL', message: 'SYNTHETIC-PRIVATE-URL' } }
            : { error: 'upstream HTTP request failed SYNTHETIC-PRIVATE-RESPONSE' }));
      });
      server.listen(9988);
    };
    module.exports.stop = () => server.close();
  `);
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const text = readFileSync(output, 'utf8'), report = JSON.parse(text);
  assert.deepEqual(report.rows[0].actions.map(action => [action.operation, action.state]), [
    ['init', 'upstream_failure'], ['home', 'configuration_required'],
    ['search', 'input_required'], ['test', 'upstream_failure'],
  ]);
  assert.equal(text.includes('SYNTHETIC-PRIVATE'), false);
  assert.equal(report.summary.requiresDiagnosis, 0);
});

test('Cat acceptance distinguishes a complete empty framework route inventory from unknown actions', async t => {
  const { directory, output } = area(t);
  writeFileSync(join(directory, 'source.js'), `
    const http = require('http'), channels = require('diagnostics_channel');
    let server;
    module.exports.start = () => {
      const hooks = new Map();
      server = http.createServer((request, response) => {
        response.setHeader('content-type', 'application/json');
        if (request.url === '/full-config' || request.url === '/config')
          return response.end(JSON.stringify({ video: { sites: [
            { key: 'metadata', api: '/metadata' }, { key: 'business', api: '/business' },
          ] } }));
        response.end(JSON.stringify({ list: [] }));
      });
      const app = { server, hasRoute() { return false; }, ready: async () => {},
        addHook(name, callback) { hooks.set(name, callback); } };
      channels.channel('fastify.initialization').publish({ fastify: app });
      for (const url of ['/config', '/full-config', '/business/home'])
        hooks.get('onRoute')({ url, method: 'GET' });
      server.listen(9988);
    };
    module.exports.stop = () => server.close();
  `);
  const result = await run(['--kind', 'cat', '--scripts', directory, '--output', output, '--live']);
  assert.equal(result.code, 0, result.stderr);
  const report = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(report.summary.sitesWithoutActions, 1);
  assert.equal(report.summary.requiresDiagnosis, 0);
  assert.equal(report.summary.invokedActions, 1);
  assert.equal(report.summary.returned, 1);
  assert.deepEqual(report.rows[0].actions[0], {
    siteId: 'metadata', operation: 'discover', state: 'no_actions', reason: 'no_registered_actions',
  });
});

test('Reader protocol overrides require the explicit source kind', async t => {
  const { directory, output } = area(t);
  const result = await run(['--scripts', directory, '--output', output, '--protocol', 'cat']);
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /--protocol requires --kind source/);
});
