import test from 'node:test';
import assert from 'node:assert/strict';
import {
  base64ToBytes,
  bytesToBase64,
  createCapabilityClient,
  createLXNetworkFromCapabilities,
  hostApiVersion,
} from '../src/index.js';

const encoded = value => bytesToBase64(new TextEncoder().encode(value));

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
    maximumBytes: 64,
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
    headers: { 'X-Test': 'value' },
    body: encoded('request'),
    timeoutMs: 1000,
    redirects: 2,
    cookieProfile: 'fixture:1:http',
  }, { signal: controller.signal });
  assert.deepEqual(calls[0].input, {
    version: hostApiVersion,
    url: 'https://fixture.invalid/start',
    method: 'POST',
    headers: { 'x-test': ['value'] },
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
    headers: { Host: 'other.invalid' },
  }), { code: 'PERMISSION_DENIED' });
});

test('cookie, text and AES arguments are bounded before reaching a host adapter', async () => {
  const calls = [];
  const client = createCapabilityClient({
    maximumBytes: 32,
    invoke(name, input) {
      calls.push({ name, input });
      if (name === 'crypto.aes') return input.data;
      return true;
    },
  });
  await assert.rejects(client.call('cookie.set', {
    version: hostApiVersion,
    profile: 'fixture',
    url: 'https://fixture.invalid/',
    values: Array.from({ length: 65 }, (_, index) => `k${index}=v`),
  }), { code: 'INVALID_ARGUMENT' });
  await assert.rejects(client.call('encoding.encode', {
    version: hostApiVersion,
    encoding: 'gbk',
    text: '中文'.repeat(10),
  }), { code: 'INVALID_ARGUMENT' });

  const key = bytesToBase64(Uint8Array.from({ length: 16 }, (_, index) => index));
  const iv = bytesToBase64(Uint8Array.from({ length: 16 }, (_, index) => 15 - index));
  const data = encoded('payload');
  assert.equal(await client.call('crypto.aes', {
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

test('LX network adapter preserves JSON, form and multipart request semantics through the ABI', async () => {
  const calls = [];
  const scopes = [];
  const client = createCapabilityClient({
    maximumBytes: 2 * 1024 * 1024,
    invoke(name, input, context) {
      if (name === 'crypto.random') return bytesToBase64(new Uint8Array(16));
      assert.equal(name, 'http.request');
      calls.push(structuredClone(input));
      scopes.push(structuredClone(context.scope));
      return {
        version: hostApiVersion,
        status: 200,
        statusText: 'OK',
        headers: {},
        url: input.url,
        body: encoded('response'),
      };
    },
  });
  const network = createLXNetworkFromCapabilities(client);
  const scope = {
    installationId: 'fixture',
    generation: 7,
    siteId: 'kw',
    requestId: 4,
  };
  const signal = new AbortController().signal;

  await network({
    url: 'https://fixture.invalid/json',
    options: { method: 'POST', body: { name: 'synthetic' }, json: true, retry: 2 },
    signal,
    scope,
  });
  await network({
    url: 'https://fixture.invalid/form',
    options: { method: 'POST', form: { name: 'a b' } },
    signal,
    scope,
  });
  await network({
    url: 'https://fixture.invalid/multipart',
    options: {
      method: 'POST',
      formData: {
        name: 'synthetic',
        file: {
          value: { type: 'Buffer', data: [0, 255, 65] },
          options: { filename: 'fixture.bin' },
        },
      },
    },
    signal,
    scope,
  });

  assert.equal(new TextDecoder().decode(base64ToBytes(calls[0].body)), '{"name":"synthetic"}');
  assert.equal(calls[0].headers['content-type'][0], 'application/json');
  assert.equal(new TextDecoder().decode(base64ToBytes(calls[1].body)), 'name=a+b');
  assert.equal(calls[1].headers['content-type'][0], 'application/x-www-form-urlencoded');
  const multipart = base64ToBytes(calls[2].body);
  const multipartText = new TextDecoder('latin1').decode(multipart);
  assert.equal(calls[2].headers['content-type'][0],
    'multipart/form-data; boundary=meowfilm-00000000000000000000000000000000');
  assert.match(multipartText, /filename="fixture\.bin"/);
  assert.equal(Buffer.from(multipart).includes(Buffer.from([0, 255, 65])), true);
  assert.equal(calls.every(call => call.cookieProfile === 'music:fixture:7:kw'), true);
  assert.deepEqual(scopes, [scope, scope, scope]);

  await assert.rejects(network({
    url: 'https://fixture.invalid/',
    options: { proxy: 'https://proxy.invalid/' },
    signal,
    scope,
  }), { code: 'UNSUPPORTED_OPTION' });
  await assert.rejects(network({
    url: 'https://fixture.invalid/',
    options: { headers: { Host: 'other.invalid' } },
    signal,
    scope,
  }), { code: 'PERMISSION_DENIED' });
  await assert.rejects(network({
    url: 'https://fixture.invalid/',
    signal,
    scope: { installationId: 'fixture', siteId: 'kw', requestId: 4 },
  }), { code: 'INVALID_ARGUMENT' });
});
