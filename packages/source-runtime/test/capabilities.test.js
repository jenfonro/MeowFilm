import test from 'node:test';
import assert from 'node:assert/strict';
import {
  base64ToBytes,
  bytesToBase64,
  createCapabilityClient,
  hostApiVersion,
} from '../src/index.js';
import { SourceHttpClient } from '../src/http.js';

const encoded = value => new TextEncoder().encode(value);

test('capability JSON business values accept 64 levels while control fields do not consume depth', async () => {
  const nested = (depth, objects = false, leaf = null) => {
    let value = leaf;
    for (let index = 0; index < depth; index++) value = objects ? { child: value } : [value];
    return value;
  };
  const cycle = []; cycle.push(cycle);
  const values = [];
  let returned;
  const client = createCapabilityClient({
    invoke(name, input) {
      if (name === 'storage.get') return returned;
      assert.equal(name, 'storage.set');
      values.push(input.value);
      return input.value;
    },
  });
  const input = { version: hostApiVersion, profile: 'depth:storage', key: 'key' };
  for (const objects of [false, true]) {
    const value = nested(64, objects);
    assert.deepEqual(await client.call('storage.set', { ...input, value }), value);
    assert.deepEqual(values.at(-1), value);
    assert.notEqual(values.at(-1), value, 'input must be snapshotted');
    returned = value;
    assert.deepEqual(await client.call('storage.get', input), value);
  }
  const special = {
    ['__proto__']: { value: 1 }, constructor: { prototype: 'data' }, ['📚']: '中文👨‍👩‍👧‍👦\u0000',
  };
  const shared = { left: special, right: special };
  assert.deepEqual(await client.call('storage.set', { ...input, value: shared }), shared);
  const count = values.length;
  for (const value of [
    nested(65), nested(65, true), { ok: true, value: nested(64) }, cycle,
    '\ud800', '\udc00', 'a\ud800b', '\udc00\ud800', { ['\ud800']: 1 }, { ['\udc00']: 1 },
    Infinity, Number.MAX_SAFE_INTEGER + 1, 1n,
    undefined, new Uint8Array([1]), new ArrayBuffer(1),
  ]) {
    await assert.rejects(client.call('storage.set', { ...input, value }), { code: 'INVALID_ARGUMENT' });
    assert.equal(values.length, count, 'invalid input must not reach the owner');
    returned = value;
    await assert.rejects(client.call('storage.get', input), { code: 'INVALID_RESPONSE' });
  }
  returned = nested(64);
  assert.deepEqual(await client.call('storage.get', input), returned);
});

test('Base64 codec preserves arbitrary bytes and rejects non-canonical input', () => {
  const bytes = Uint8Array.from([0, 1, 2, 127, 128, 254, 255]);
  const value = bytesToBase64(bytes);
  assert.deepEqual(base64ToBytes(value), bytes);
  for (const input of ['A', 'AB==', 'AA=A', '!!!!', 'YWJjZA===']) {
    assert.throws(() => base64ToBytes(input), { code: 'INVALID_ARGUMENT' });
  }
});

test('capability ABI normalizes HTTP envelopes and validates returned bytes', async () => {
  const calls = [];
  const client = createCapabilityClient({
    async invoke(name, input, context) {
      calls.push({ name, input, context });
      return {
        version: hostApiVersion,
        status: 200,
        statusText: 'OK',
        headers: { 'Content-Type': ['text/plain'] },
        url: 'https://fixture.invalid/final',
        body: encoded('response'),
      };
    },
  });
  const controller = new AbortController();
  const result = await client.call('http.request', {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    method: 'post',
    headers: { 'X-Test': 'value', Host: 'virtual.invalid', Origin: 'https://caller.invalid' },
    body: encoded('request'),
    timeoutMs: 1000,
    redirects: 2,
    cookieProfile: 'fixture:1:http',
  }, { signal: controller.signal });
  assert.deepEqual(calls[0].input, {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    method: 'POST',
    headers: { 'x-test': ['value'], host: ['virtual.invalid'], origin: ['https://caller.invalid'] },
    body: encoded('request'),
    timeoutMs: 1000,
    redirects: 2,
    cookieProfile: 'fixture:1:http',
  });
  assert.equal(calls[0].context.signal, controller.signal);
  assert.deepEqual(result, {
    version: hostApiVersion,
    status: 200,
    statusText: 'OK',
    headers: { 'content-type': ['text/plain'] },
    url: 'https://fixture.invalid/final',
    body: encoded('response'),
  });
  await assert.rejects(client.call('http.request', {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    unexpected: true,
  }), { code: 'INVALID_ARGUMENT' });
  await assert.rejects(client.call('http.request', {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    headers: { 'Content-Length': '1' },
  }), { code: 'PERMISSION_DENIED' });
});

test('capability HTTP preserves URL credentials and accepts ordinary origins without destination grants', async () => {
  const calls = [];
  const client = createCapabilityClient({
    invoke(name, input) {
      assert.equal(name, 'http.request');
      calls.push(input);
      return { version: hostApiVersion, status: 200, url: input.url, body: encoded('ok') };
    },
  });
  for (const url of ['http://fixture-user:fixture-pass@127.0.0.1:19888/path?sig=a%2Fb',
    'https://fixture-user:fixture-pass@site.invalid/path?sig=a%2Fb']) {
    assert.equal((await client.call('http.request', {
      version: hostApiVersion, url, headers: {
        Host: 'virtual.invalid', Origin: 'https://caller.invalid',
        Authorization: 'explicit-input', Cookie: 'explicit=input',
      },
    })).url, url);
    assert.equal(calls.at(-1).url, url);
    assert.deepEqual(calls.at(-1).headers, { host: ['virtual.invalid'], origin: ['https://caller.invalid'],
      authorization: ['explicit-input'], cookie: ['explicit=input'] });
  }
});

test('source HTTP uses the effective Content-Type for both headers and body encoding', async t => {
  const calls = [];
  const capabilities = createCapabilityClient({
    invoke(name, input) {
      assert.equal(name, 'http.request');
      calls.push(input);
      return {
        version: hostApiVersion,
        status: 200,
        url: input.url,
        headers: {},
        body: encoded('ok'),
      };
    },
  });
  const http = new SourceHttpClient({
    capabilities,
    profile: 'source:http:1',
    scope: { installationId: 'http', generation: 1, protocol: 'qysg', siteId: 'source' },
  });
  for (const scenario of [
    {
      name: 'standalone form type',
      input: { contentType: 'application/x-www-form-urlencoded',
        body: { key: 'a b', tag: ['x y', 'z'] } },
      type: 'application/x-www-form-urlencoded',
      body: 'key=a+b&tag=x+y&tag=z',
    },
    {
      name: 'standalone JSON type',
      input: { contentType: 'application/json; charset=utf-8', body: { key: 'a b' } },
      type: 'application/json; charset=utf-8',
      body: '{"key":"a b"}',
    },
    {
      name: 'explicit lowercase form header overrides standalone JSON type',
      input: { headers: { 'content-type': 'application/x-www-form-urlencoded' },
        contentType: 'application/json', body: { key: 'a b' } },
      type: 'application/x-www-form-urlencoded',
      body: 'key=a+b',
    },
    {
      name: 'explicit mixed-case JSON header overrides standalone form type',
      input: { headers: { 'cOnTeNt-TyPe': ['application/json'] },
        contentType: 'application/x-www-form-urlencoded', body: { key: 'a b' } },
      type: 'application/json',
      body: '{"key":"a b"}',
    },
    {
      name: 'standalone type is also sent with text bodies',
      input: { contentType: 'text/plain', body: 'a b' },
      type: 'text/plain',
      body: 'a b',
    },
    {
      name: 'objects without a type keep the JSON default',
      input: { body: { key: 'a b' } },
      type: 'application/json',
      body: '{"key":"a b"}',
    },
    {
      name: 'an explicit empty header is retained without a duplicate default',
      input: { headers: { 'content-type': '' },
        contentType: 'application/x-www-form-urlencoded', body: { key: 'a b' } },
      type: '',
      body: '{"key":"a b"}',
    },
  ]) {
    await t.test(scenario.name, async () => {
      const response = await http.request({
        url: 'https://fixture.invalid/post',
        ...scenario.input,
      });
      const request = calls.at(-1);
      assert.equal(request.method, 'POST');
      assert.deepEqual(request.headers, { 'content-type': [scenario.type] });
      assert.equal(new TextDecoder().decode(request.body), scenario.body);
      assert.equal(response.statusCode, 200);
      assert.equal(response.data, 'ok');
    });
  }
  const beforeInvalid = calls.length;
  await assert.rejects(http.request({
    url: 'https://fixture.invalid/post',
    body: 'fixture',
    contentType: 'text/plain\r\nX-Injected: yes',
  }), { code: 'INVALID_ARGUMENT' });
  assert.equal(calls.length, beforeInvalid);
});

test('cookie and AES retain their format contracts while text has no byte quota', async () => {
  const calls = [];
  const client = createCapabilityClient({
    invoke(name, input) {
      calls.push({ name, input });
      if (name === 'crypto.aes') return input.data;
      if (name === 'encoding.encode') return encoded(input.text);
      return true;
    },
  });
  await assert.rejects(client.call('cookie.set', {
    version: hostApiVersion,
    profile: 'fixture',
    url: 'https://fixture.invalid/',
    values: Array.from({ length: 65 }, (_, index) => `k${index}=v`),
  }), { code: 'INVALID_ARGUMENT' });
  assert.deepEqual(await client.call('encoding.encode', {
    version: hostApiVersion,
    encoding: 'gbk',
    text: '中文'.repeat(10),
  }), encoded('中文'.repeat(10)));

  const key = Uint8Array.from({ length: 16 }, (_, index) => index);
  const iv = Uint8Array.from({ length: 16 }, (_, index) => 15 - index);
  const data = encoded('payload');
  assert.deepEqual(await client.call('crypto.aes', {
    version: hostApiVersion,
    operation: 'encrypt',
    algorithm: 'aes-128-cbc',
    key,
    iv,
    data,
  }), data);
  assert.deepEqual(calls.at(-1), {
    name: 'crypto.aes',
    input: {
      version: hostApiVersion,
      operation: 'encrypt',
      algorithm: 'aes-128-cbc',
      key,
      iv,
      data,
    },
  });
  for (const input of [
    { operation: 'encrypt', algorithm: 'aes-128-cbc', key: encoded('short'), iv, data },
    { operation: 'encrypt', algorithm: 'aes-128-cbc', key, iv: encoded('short'), data },
    { operation: 'decrypt', algorithm: 'aes-128-ecb', key, iv, data },
  ]) {
    await assert.rejects(client.call('crypto.aes', {
      version: hostApiVersion,
      ...input,
    }), { code: 'INVALID_ARGUMENT' });
  }
});

test('capability completion is fenced by cancellation', async () => {
  let release;
  const waiting = new Promise(resolve => { release = resolve; });
  const controller = new AbortController();
  const client = createCapabilityClient({
    invoke: async () => {
      await waiting;
      return null;
    },
  });
  const result = client.call('storage.get', {
    version: hostApiVersion,
    profile: 'fixture',
    key: 'key',
  }, { signal: controller.signal });
  controller.abort();
  release();
  await assert.rejects(result, { code: 'CANCELLED' });
});

test('ABI2 snapshots byte subviews before yielding and rejects non-byte envelopes', async () => {
  const input = Uint8Array.from({ length: 260 }, (_, index) => (index - 2) & 255);
  const expected = input.slice(2, 258);
  let release;
  const barrier = new Promise(resolve => { release = resolve; });
  const client = createCapabilityClient({
    async invoke(name, captured) {
      await barrier;
      assert.equal(name, 'http.request');
      assert.deepEqual(captured.body, expected);
      return {
        version: hostApiVersion, status: 200, url: captured.url, body: captured.body,
      };
    },
  });
  const pending = client.call('http.request', {
    version: hostApiVersion, url: 'https://bytes.invalid/', body: input.subarray(2, 258),
  });
  input.fill(0);
  release();
  assert.deepEqual((await pending).body, expected);
  for (const body of ['AA==', [0], { type: 'Buffer', data: [0] }]) {
    await assert.rejects(client.call('http.request', {
      version: hostApiVersion, url: 'https://bytes.invalid/', body,
    }), { code: 'INVALID_ARGUMENT' });
  }
  const invalid = createCapabilityClient({
    invoke: () => ({ version: hostApiVersion, status: 200, url: 'https://bytes.invalid/', body: 'AA==' }),
  });
  await assert.rejects(invalid.call('http.request', {
    version: hostApiVersion, url: 'https://bytes.invalid/',
  }), { code: 'INVALID_RESPONSE' });
});

test('HTTP bodies remain independent of DOM parsing budgets', async () => {
  const data = 'x'.repeat(8 * 1024 * 1024 + 1);
  const client = createCapabilityClient({
    invoke: (_name, input) => ({
      version: hostApiVersion, status: 200, url: input.url, body: encoded(data),
    }),
  });
  const http = new SourceHttpClient({
    capabilities: client, profile: 'source:body:1',
    scope: { installationId: 'body', generation: 1, protocol: 'qysg', siteId: 'source' },
  });
  assert.equal((await http.request({ url: 'https://bytes.invalid/' })).data, data);
});
