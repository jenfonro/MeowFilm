import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { hash, paths, readJSON, repository, target } from './ios-native-package-support.mjs';
import { buildCatWebValues } from '../scripts/quickjs/build-ios.mjs';

// The artifact must come from the real iOS prepare/build, not an in-test bundle.
// These isolated desktop value tests do not stand in for NodeMobile host tests.

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

function isolatedValues(scenario, path = preparedBundle().path) {
  const result = spawnSync(process.execPath, ['--input-type=commonjs', '-e', `
    const assert = require('node:assert/strict');
    const { Blob } = require('node:buffer');
    const { ReadableStream, WritableStream } = require('node:stream/web');
    const Module = require('node:module');
    const artifact = process.argv[1], touched = [];
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
    const dispatcherKey = Symbol.for('undici.globalDispatcher.1');
    const dispatcher = new Proxy({}, { get() { throw new Error('AMBIENT_DISPATCHER_USE'); } });
    globalThis[dispatcherKey] = dispatcher;
    const intern = Symbol.for;
    Symbol.for = function(name) {
      if (name === 'undici.globalDispatcher.1') throw new Error('AMBIENT_DISPATCHER_LOOKUP');
      return intern(name);
    };
    const load = Module._load;
    Module._load = function(specifier, parent, isMain) {
      if (parent?.filename === artifact) assert.ok(Module.isBuiltin(specifier),
        'VALUE_BUNDLE_EXTERNAL:' + specifier);
      return load.call(this, specifier, parent, isMain);
    };
    const values = require(artifact);
    (async () => {
      const { Request, Response, Headers, FormData } = values;
      ${scenario}
      assert.deepEqual(touched, []);
      assert.equal(globalThis[dispatcherKey], dispatcher);
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

// Local builder tests exercise only the JS dependency collector, not an iOS receipt or framework.
function builderFixture(t) {
  const temporary = join(repository, 'tmp/test-results/compatibility-cleanup/clean36/ios-build');
  mkdirSync(temporary, { recursive: true });
  const root = mkdtempSync(join(temporary, 'web-values-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  for (const path of ['package.json', 'package-lock.json', 'scripts/quickjs/build-ios.mjs',
    'scripts/quickjs/ios-target.mjs', 'scripts/ios-node-mobile.json', 'node_modules/esbuild/package.json',
    'node_modules/undici']) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    cpSync(join(repository, path), join(root, path), { recursive: true });
  }
  const write = (path, value) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), typeof value === 'string' ? value : JSON.stringify(value));
  };
  return { root, write, build: () => buildCatWebValues(join(root, 'output'), root) };
}

test('Cat Web value builder records its real inputs and loads without ambient transport capabilities', async t => {
  const f = builderFixture(t), values = await f.build();
  for (const input of values.inputs) {
    assert.equal(hash(readFileSync(join(f.root, input.path))), input.sha256);
  }
  assert.equal(hash(readFileSync(join(f.root, values.path))), values.sha256);
  isolatedValues(`
    const headers = new Headers({ 'x-value': 'local' });
    const controller = new AbortController();
    const request = new Request('https://example.invalid/value', { headers, signal: controller.signal });
    controller.abort('cancelled');
    assert.equal(request.signal.reason, 'cancelled');
    assert.equal(request.headers.get('x-value'), 'local');
    assert.equal(await new Response(new Blob(['local'])).text(), 'local');
    const storage = new Uint8Array([9, 1, 2, 8]);
    const body = new Request('https://example.invalid/value', {
      method: 'POST', body: new DataView(storage.buffer, 1, 2),
    });
    const copy = body.clone();
    storage.fill(7);
    for (const bytes of await Promise.all([body.arrayBuffer(), copy.arrayBuffer()])) {
      assert.deepEqual([...new Uint8Array(bytes)], [1, 2]);
    }
    const form = new FormData();
    form.append('key', 'value');
    assert.equal(form.get('key'), 'value');
  `, join(f.root, values.path));
});

test('Cat Web value builder follows a referenced helper and public builtin without an approval list', async t => {
  const f = builderFixture(t), entry = 'node_modules/undici/lib/web/fetch/headers.js';
  const helper = 'node_modules/undici/lib/web/fetch/helpers/index.js';
  f.write(entry, `${readFileSync(join(f.root, entry), 'utf8')}
module.exports.Headers.prototype.helperValue = require('./helpers').value;
`);
  f.write(helper, "module.exports.value = require('node:path').basename('/values/helper');\n");
  f.write('node_modules/undici/unreferenced.js', "require('./missing');\n");
  const values = await f.build();
  assert.ok(values.inputs.some(input => input.path === helper));
  assert.equal(values.inputs.some(input => input.path === 'node_modules/undici/unreferenced.js'), false);
  isolatedValues("assert.equal(new Headers().helperValue, 'helper');", join(f.root, values.path));
});

test('Cat Web value builder follows a locked dependency without dev or engines gates', async t => {
  const f = builderFixture(t), lock = readJSON(join(f.root, 'package-lock.json'));
  const installed = readJSON(join(f.root, 'node_modules/undici/package.json'));
  const dependencies = { 'web-value-helper': '1.0.0' };
  installed.dependencies = dependencies;
  installed.engines.node = '>=18';
  Object.assign(lock.packages['node_modules/undici'], { dev: false, engines: installed.engines, dependencies });
  lock.packages['node_modules/web-value-helper'] = {
    version: '1.0.0', resolved: 'https://registry.npmjs.org/web-value-helper/-/web-value-helper-1.0.0.tgz',
    integrity: 'sha512-' + Buffer.alloc(64).toString('base64'),
  };
  f.write('node_modules/web-value-helper/package.json', { name: 'web-value-helper', version: '1.0.0' });
  f.write('node_modules/web-value-helper/index.js', "module.exports.value = 'locked helper';\n");
  const entry = 'node_modules/undici/lib/web/fetch/headers.js';
  f.write(entry, `${readFileSync(join(f.root, entry), 'utf8')}
module.exports.Headers.prototype.helperValue = require('web-value-helper').value;
`);
  f.write('node_modules/undici/package.json', installed);
  f.write('package-lock.json', lock);
  const values = await f.build();
  for (const path of ['node_modules/web-value-helper/index.js', 'node_modules/web-value-helper/package.json']) {
    assert.ok(values.inputs.some(input => input.path === path));
  }
  isolatedValues("assert.equal(new Headers().helperValue, 'locked helper');", join(f.root, values.path));
});

test('Cat Web value builder rejects an installed dependency version that differs from the root lock', async t => {
  const f = builderFixture(t), installed = readJSON(join(f.root, 'node_modules/undici/package.json'));
  f.write('node_modules/undici/package.json', { ...installed, version: '6.29.0' });
  await assert.rejects(f.build(), /differs from package lock/);
});

test('prepared iOS Cat Web values bind the consumed inputs and MIT license', () => {
  const { receipt } = preparedBundle(), values = receipt.catWebValues;
  assert.ok(values.inputs.length > 0);
  assert.equal(new Set(values.inputs.map(input => input.path)).size, values.inputs.length);
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
    for (const constructor of [Request, Response, Headers, FormData]) assert.equal(typeof constructor, 'function');
    assert.equal(values.fetch, undefined);
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
