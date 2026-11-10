import test from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { Readable } from 'node:stream';
import { createWatchman } from '../src/watchman.js';

test('SDK is loaded lazily and reused, token generation uses the official business identity each time', async () => {
  let loads = 0;
  const sdk = createWatchman({ loadTool: async () => {
    loads++;
    return `window.initWatchman = ({productNumber,onload}) => {
      if (productNumber !== 'YD00000558929251') throw new Error('product');
      let next = 0, ready = false;
      onload({ getInstance() { return { I(done) { ready = true; done(); } }; },
        getToken(id,done) {
          if (!ready || id !== 'bd5d2f973ef74cd2a61325a412ae54d9') throw new Error('business');
          ready = false; done('synthetic-' + (++next));
        } });
    };`;
  } });
  try {
    assert.equal(loads, 0);
    assert.equal(await sdk.get(), 'synthetic-1');
    assert.equal(await sdk.get(), 'synthetic-2');
    assert.equal(loads, 1);
  } finally { sdk.close(); }
});
test('invalid SDK result closes its DOM and a later attempt initializes again', async () => {
  let loads = 0, closed = 0;
  const sdk = createWatchman({
    loadTool: async () => { loads++; return '// synthetic SDK'; },
    createDOM: options => {
      assert.equal(options.url, 'https://music.163.com/');
      assert.equal(options.cookieJar, undefined);
      return { window: {
        document: { createElement: () => ({}), body: { appendChild() {} } },
        initWatchman({ onload }) { onload({
          getInstance: () => ({ I: callback => callback() }),
          getToken: (_id, callback) => callback(loads === 1 ? '' : 'synthetic-fresh'),
        }); },
        close() { closed++; },
      } };
    },
  });
  await assert.rejects(sdk.get(), { code: 'UPSTREAM_FORMAT' });
  assert.equal(closed, 1);
  assert.equal(await sdk.get(), 'synthetic-fresh');
  assert.equal(loads, 2);
  sdk.close(); assert.equal(closed, 2);
});
test('SDK errors never include upstream text', async () => {
  const sdk = createWatchman({ loadTool: async () => { throw new Error('private upstream text'); } });
  await assert.rejects(sdk.get(), { code: 'NETWORK_ERROR', message: 'NETWORK_ERROR' });
  sdk.close();
  const malformed = createWatchman({ loadTool: async () => 'window.other = 1;' });
  await assert.rejects(malformed.get(), { code: 'SDK_FAILURE', message: 'SDK_FAILURE' });
  malformed.close();
});
test('SDK downloads through native HTTPS without fetch or caller credentials', async t => {
  let calls = 0;
  t.mock.method(globalThis, 'fetch', () => { throw new Error('fetch must not be used'); });
  t.mock.method(https, 'get', (url, options, receive) => {
    calls++;
    assert.equal(url, 'https://acstatic-dun.126.net/tool.min.js');
    assert.deepEqual(Object.keys(options), ['signal']);
    assert.ok(options.signal instanceof AbortSignal);
    const request = new EventEmitter();
    queueMicrotask(() => {
      const response = Readable.from([`window.initWatchman = ({onload}) => onload({
        getInstance: () => ({ I: callback => callback() }),
        getToken: (_id, callback) => callback('synthetic-native-http')
      });`]);
      response.statusCode = 200;
      receive(response);
    });
    return request;
  });
  const sdk = createWatchman();
  try { assert.equal(await sdk.get(), 'synthetic-native-http'); assert.equal(calls, 1); }
  finally { sdk.close(); }
});
test('native HTTPS rejects redirects, transport failures and request deadlines without raw details', async t => {
  let outcome;
  t.mock.method(https, 'get', (_url, _options, receive) => {
    const request = new EventEmitter();
    queueMicrotask(() => {
      if (outcome instanceof Error) { request.emit('error', outcome); return; }
      const response = Readable.from(['untrusted response']);
      response.statusCode = outcome;
      receive(response);
    });
    return request;
  });
  for (const [value, code] of [[302, 'UPSTREAM_HTTP'], [503, 'UPSTREAM_HTTP'],
    [new Error('private upstream message'), 'NETWORK_ERROR'],
    [Object.assign(new Error('private timeout'), { name: 'AbortError' }), 'TIMEOUT']]) {
    outcome = value;
    const sdk = createWatchman();
    await assert.rejects(sdk.get(), { code, message: code });
    sdk.close();
  }
});
