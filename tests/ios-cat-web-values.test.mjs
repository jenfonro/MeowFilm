import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { hash, paths, readJSON, repository, target } from './ios-native-package-support.mjs';

// The artifact must come from the real iOS prepare/build, not an in-test bundle.
// These isolated desktop value tests do not stand in for NodeMobile host tests.
const modules = [
  'lib/core/constants.js', 'lib/core/errors.js', 'lib/core/symbols.js', 'lib/core/tree.js', 'lib/core/util.js',
  'lib/web/fetch/body.js', 'lib/web/fetch/constants.js', 'lib/web/fetch/data-url.js',
  'lib/web/fetch/dispatcher-weakref.js', 'lib/web/fetch/file.js', 'lib/web/fetch/formdata-parser.js',
  'lib/web/fetch/formdata.js', 'lib/web/fetch/global.js', 'lib/web/fetch/headers.js',
  'lib/web/fetch/request.js', 'lib/web/fetch/response.js', 'lib/web/fetch/symbols.js',
  'lib/web/fetch/util.js', 'lib/web/fetch/webidl.js',
];
const builtins = [
  'node:assert', 'node:buffer', 'node:crypto', 'node:events', 'node:http', 'node:net',
  'node:perf_hooks', 'node:querystring', 'node:stream', 'node:util', 'node:util/types',
  'node:worker_threads', 'node:zlib',
];

function preparedBundle() {
  const receipt = readJSON(join(repository, paths.receipt));
  assert.equal(receipt.platform, 'ios');
  assert.deepEqual(receipt.target, target);
  assert.equal(receipt.catWebValues.path, paths.catWebValues);
  const path = join(repository, paths.catWebValues), bytes = readFileSync(path);
  assert.equal(bytes.length, receipt.catWebValues.bytes);
  assert.equal(hash(bytes), receipt.catWebValues.sha256);
  return { receipt, path };
}

function isolatedValues(scenario) {
  const { path } = preparedBundle();
  const result = spawnSync(process.execPath, ['--input-type=commonjs', '-e', `
    const assert = require('node:assert/strict');
    const { Blob } = require('node:buffer');
    const { ReadableStream, WritableStream } = require('node:stream/web');
    const Module = require('node:module');
    const artifact = process.argv[1], touched = [], allowed = new Set(${JSON.stringify(builtins)});
    assert.equal(globalThis.ReadableStream, ReadableStream);
    assert.equal(globalThis.WritableStream, WritableStream);
    assert.equal(globalThis.Blob, Blob);
    delete globalThis.WebAssembly;
    assert.equal(typeof WebAssembly, 'undefined');
    for (const name of ['Request', 'Response', 'Headers', 'FormData', 'fetch']) {
      Object.defineProperty(globalThis, name, { configurable: true, get() {
        touched.push(name);
        throw new Error('BUILTIN_FETCH_VALUE_READ:' + name);
      } });
    }
    const load = Module._load;
    Module._load = function(specifier, parent, isMain) {
      if (parent?.filename === artifact) assert.ok(allowed.has(specifier),
        'VALUE_BUNDLE_EXTERNAL:' + specifier);
      return load.call(this, specifier, parent, isMain);
    };
    const values = require(artifact);
    (async () => {
      const { Request, Response, Headers, FormData } = values;
      ${scenario}
      assert.deepEqual(touched, []);
      assert.equal(typeof WebAssembly, 'undefined');
      console.log('CAT_WEB_VALUES_OK');
    })().catch(error => { console.error(error); process.exitCode = 1; });
  `, path], {
    cwd: repository, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
    encoding: 'utf8', timeout: 30_000,
  });
  assert.equal(result.signal, null, result.stderr || result.error?.message);
  assert.equal(result.status, 0, result.stderr || result.error?.message);
  assert.match(result.stdout, /CAT_WEB_VALUES_OK/);
}

test('prepared iOS Cat Web values bind the reviewed pure inputs and MIT license', () => {
  const { receipt } = preparedBundle(), values = receipt.catWebValues;
  assert.deepEqual(values.inputs.filter(input => input.path.startsWith('node_modules/undici/lib/'))
    .map(input => input.path).sort(), modules.map(path => `node_modules/undici/${path}`).sort());
  assert.equal(values.license.path, 'node_modules/undici/LICENSE');
  assert.equal(values.license.sha256, 'a6db8096b2707bc0102d256917d4d33f298ba36d8c3f25de067a2b5bb379db27');
  const license = readFileSync(join(repository, values.license.path));
  assert.equal(license.length, values.license.bytes);
  assert.equal(hash(license), values.license.sha256);
  for (const input of values.inputs) {
    assert.ok(receipt.inputs.some(value => value.path === input.path && value.sha256 === input.sha256));
    assert.equal(hash(readFileSync(join(repository, input.path))), input.sha256);
  }
});

test('prepared pure CJS loads without WebAssembly or readable native fetch-value getters', () => {
  isolatedValues(`
    assert.deepEqual(Object.keys(values).sort(), ['FormData', 'Headers', 'Request', 'Response']);
    for (const constructor of [Request, Response, Headers, FormData]) assert.equal(typeof constructor, 'function');
  `);
});

test('pure Headers and Request retain public normalization and native abort signal behavior', () => {
  isolatedValues(`
    const headers = new Headers([['X-Value', ' one '], ['x-value', 'two']]);
    assert.equal(headers.get('X-VALUE'), 'one, two');
    headers.set('X-Value', 'three');
    assert.deepEqual([...headers], [['x-value', 'three']]);
    assert.throws(() => headers.append('bad\\nname', 'value'), TypeError);
    const controller = new AbortController(), reason = new Error('request cancelled');
    const request = new Request('https://example.invalid/value', { headers, signal: controller.signal });
    assert.equal(request.url, 'https://example.invalid/value');
    assert.equal(request.method, 'GET');
    assert.ok(request.headers instanceof Headers);
    assert.equal(request.headers.get('x-value'), 'three');
    const clone = request.clone();
    controller.abort(reason);
    assert.equal(request.signal.aborted, true);
    assert.equal(request.signal.reason, reason);
    assert.equal(clone.signal.aborted, true);
    assert.equal(clone.signal.reason, reason);
  `);
});

test('pure Request copies only a typed view range and clones both body branches', () => {
  isolatedValues(`
    const storage = new Uint8Array([9, 1, 2, 3, 8]);
    const request = new Request('https://example.invalid/value', {
      method: 'POST', body: new DataView(storage.buffer, 1, 3),
    });
    storage.fill(7);
    const clone = request.clone();
    assert.equal(request.bodyUsed, false);
    assert.ok(request.body instanceof ReadableStream);
    assert.ok(clone.body instanceof ReadableStream);
    const bodies = await Promise.all([request.arrayBuffer(), clone.arrayBuffer()]);
    for (const body of bodies) assert.deepEqual([...new Uint8Array(body)], [1, 2, 3]);
    assert.equal(request.bodyUsed, true);
    assert.equal(clone.bodyUsed, true);
    assert.throws(() => request.clone(), TypeError);
    await assert.rejects(request.arrayBuffer(), TypeError);
  `);
});

test('pure Response preserves native Blob content and cloned body consumption', () => {
  isolatedValues(`
    const blob = new Blob(['原生', new Uint8Array([0, 1, 255])], { type: 'application/octet-stream' });
    const response = new Response(blob, { status: 201, headers: { 'x-value': 'blob' } });
    assert.equal(response.status, 201);
    assert.equal(response.ok, true);
    assert.ok(response.headers instanceof Headers);
    assert.equal(response.headers.get('content-type'), 'application/octet-stream');
    const clone = response.clone();
    const [copy, bytes] = await Promise.all([response.blob(), clone.arrayBuffer()]);
    assert.ok(copy instanceof Blob);
    assert.equal(copy.type, blob.type);
    assert.deepEqual(new Uint8Array(await copy.arrayBuffer()), new Uint8Array(await blob.arrayBuffer()));
    assert.deepEqual(new Uint8Array(bytes), new Uint8Array(await blob.arrayBuffer()));
    assert.equal(response.bodyUsed, true);
    assert.equal(clone.bodyUsed, true);
    assert.throws(() => response.clone(), TypeError);
  `);
});

test('pure Response uses native stream tee, pipe and cancellation rather than a stream shim', () => {
  isolatedValues(`
    const source = new ReadableStream({ start(controller) {
      controller.enqueue(new Uint8Array([65, 66]));
      controller.enqueue(new Uint8Array([67]));
      controller.close();
    } });
    const response = new Response(source);
    assert.equal(response.body, source);
    const clone = response.clone(), chunks = [];
    await Promise.all([
      response.body.pipeTo(new WritableStream({ write(chunk) { chunks.push(...chunk); } })),
      clone.text().then(text => assert.equal(text, 'ABC')),
    ]);
    assert.deepEqual(chunks, [65, 66, 67]);
    assert.equal(response.body.locked, false);
    assert.equal(response.bodyUsed, true);
    assert.equal(clone.bodyUsed, true);
    let cancelled;
    const cancellable = new ReadableStream({ cancel(reason) { cancelled = reason; } });
    const value = new Response(cancellable), reason = new Error('cancel value body');
    await value.body.cancel(reason);
    assert.equal(cancelled, reason);
    assert.equal(value.body.locked, false);
  `);
});

test('pure FormData round-trips multipart native Blob values and URL encoded fields', () => {
  isolatedValues(`
    const form = new FormData();
    form.append('title', '中文');
    form.append('title', 'second');
    form.append('file', new Blob([new Uint8Array([0, 1, 255])], { type: 'application/octet-stream' }), 'value.bin');
    assert.deepEqual(form.getAll('title'), ['中文', 'second']);
    assert.equal(form.get('file').name, 'value.bin');
    const request = new Request('https://example.invalid/value', { method: 'POST', body: form });
    assert.match(request.headers.get('content-type'), /^multipart\\/form-data; boundary=/);
    const clone = request.clone();
    const [parsed, cloned] = await Promise.all([request.formData(), clone.formData()]);
    for (const result of [parsed, cloned]) {
      assert.ok(result instanceof FormData);
      assert.deepEqual(result.getAll('title'), ['中文', 'second']);
      const file = result.get('file');
      assert.ok(file instanceof Blob);
      assert.equal(file.name, 'value.bin');
      assert.equal(file.type, 'application/octet-stream');
      assert.deepEqual([...new Uint8Array(await file.arrayBuffer())], [0, 1, 255]);
    }
    form.set('title', 'replaced');
    assert.deepEqual(form.getAll('title'), ['replaced']);
    form.delete('title');
    assert.equal(form.has('title'), false);
    const encoded = await new Response('a=one&a=two&title=%E4%B8%AD%E6%96%87', {
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
    }).formData();
    assert.ok(encoded instanceof FormData);
    assert.deepEqual(encoded.getAll('a'), ['one', 'two']);
    assert.equal(encoded.get('title'), '中文');
  `);
});
