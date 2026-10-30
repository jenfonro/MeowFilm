import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

function area(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const directory = mkdtempSync(join(temporaryRoot, 'source-acceptance-'));
  t.after(() => { assertOwnedPath(temporaryRoot, directory); rmSync(directory, { recursive: true }); });
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
        (_error,_response,body) => reject(Error(String(body.err_code)))));
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
        (_error,_response,body) => reject(Error(body.info))));
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
