import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRuntime, createRuntimeClient } from '../apps/runtime/entry.mjs';
import { configureStorage, openDatabase } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';

configureStorage({ driver: createDesktopStorageDriver, initialize: true });

const root = fileURLToPath(new URL('../', import.meta.url));
const temporary = join(root, 'tmp/test-results/film-playback/runtime-assembly');
function area(t) {
  mkdirSync(temporary, { recursive: true });
  const path = mkdtempSync(join(temporary, 'case-'));
  return path;
}

test('E49/E50: the common embedding factory retains its default three-module contract', async t => {
  const sessionId = randomUUID();
  const base = area(t);
  const runtime = createRuntime({ sessionId, dataRoot: join(base, 'data') });
  await runtime.start();
  const client = createRuntimeClient({ baseUrl: runtime.baseUrl });
  t.after(async () => {
    try { await client.close(); await runtime.close(); }
    finally { rmSync(base, { recursive: true }); }
  });
  const description = await client.describe();
  assert.equal(description.ok, true);
  assert.deepEqual(description.value.modules.map(({ id, version, contract }) => [id, version, contract]), [
    ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'], ['source', '0.3.0', 'source.runtime.v2'],
  ]);
  assert.equal((await client.call('music.script.sources.list')).ok, true);
});

for (const platform of ['ios', 'windows']) {
  test(`E42/E48/E49/E50: the ${platform} App entry assembles nine real owners and connects provider invalidation`, async t => {
    const base = area(t), actual = join(base, 'private', 'var'), alias = join(base, 'var');
    t.after(() => rmSync(base, { recursive: true }));
    mkdirSync(actual, { recursive: true });
    symlinkSync(actual, alias, process.platform === 'win32' ? 'junction' : 'dir');
    const dataRoot = join(realpathSync(alias), 'data');
    const database = openDatabase(dataRoot);
    await database.ready;
    await database.close();
    const bootstrap = new URL(`../apps/${platform}/runtime/bootstrap.mjs`, import.meta.url).href;
    const entry = new URL(`../apps/${platform}/runtime/entries/aggregate.mjs`, import.meta.url).href;
    const sharedBootstrap = new URL('../apps/runtime/bootstrap.mjs', import.meta.url).href;
    const sharedClient = new URL('../apps/runtime/client.mjs', import.meta.url).href;
    const sharedContracts = new URL('../apps/runtime/contracts.mjs', import.meta.url).href;
    // Replace only the native-private startup channel in an isolated process.
    // Both thin entries, providers, film owners and public HTTP are real code;
    // this does not claim Windows pipes or an iOS NodeMobile executor was run.
    const startup = platform === 'ios' ? `
      import { createAppRuntime } from ${JSON.stringify(sharedBootstrap)};
      import { configureStorage } from '@meowfilm/storage';
      import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
      configureStorage({ driver: createDesktopStorageDriver, initialize: false });
      export async function startIOSAppRuntime({ product, assemble, settingsChanged }) {
        const { settings, ...configuration } = globalThis.fixtureConfiguration;
        await settings.initialize();
        globalThis.fixtureRuntime = await createAppRuntime({
          ...configuration, product,
          assemble: () => assemble({ dataRoot: configuration.dataRoot, settings }),
          settingsChanged: block => settingsChanged(block, { settings }),
        }).start();
      }
      export function reportIOSRuntimeStartFailure() { throw new Error('FIXTURE_START_FAILED'); }
    ` : `
      export async function startWindowsAppRuntime({ createRuntime }) {
        globalThis.fixtureRuntime = await (await createRuntime(globalThis.fixtureConfiguration)).start();
      }
      export function reportWindowsRuntimeStartFailure() { throw new Error('FIXTURE_START_FAILED'); }
    `;
    const script = `
      import assert from 'node:assert/strict';
      import http from 'node:http';
      import https from 'node:https';
      import { randomUUID } from 'node:crypto';
      import { registerHooks, syncBuiltinESMExports } from 'node:module';
      const request = http.request;
      http.request = function(target, ...args) {
        const endpoint = typeof target === 'string' ? new URL(target) : target;
        assert.equal(endpoint.hostname ?? endpoint.host, '127.0.0.1', 'only fixture loopback HTTP is permitted');
        return Reflect.apply(request, this, [target, ...args]);
      };
      https.request = () => assert.fail('external HTTPS is not permitted in the assembly fixture');
      syncBuiltinESMExports();
      const counts = { A: 0, B: 0, D: 0 };
      async function upstream(label) {
        const server = http.createServer((incoming, response) => {
          counts[label]++;
          assert.equal(incoming.url, label === 'D' ? '/rexxar/api/v2/movie/43' : '/3/movie/42?language=zh-CN');
          if (label !== 'D') assert.equal(incoming.headers.authorization, 'Bearer fixture-only-token');
          response.setHeader('Content-Type', 'application/json');
          response.end(JSON.stringify({ id: label === 'D' ? 43 : 42, title: 'metadata-' + label,
            status: 'Released', genres: [], ...(label === 'D' ? { intro: 'Fixture movie' } : { runtime: 90 }) }));
        });
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        return { server, url: 'http://127.0.0.1:' + server.address().port };
      }
      const first = await upstream('A'), second = await upstream('B'), unrelated = await upstream('D');
      const documents = new Map(['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences']
        .map(block => [block, null]));
      const snapshots = new Map();
      const settings = {
        async initialize() { for (const block of documents.keys()) await this.reload(block); },
        async reload(block) {
          assert.equal(documents.has(block), true);
          snapshots.set(block, structuredClone(documents.get(block)));
        },
        read(block) {
          assert.equal(snapshots.has(block), true);
          return structuredClone(snapshots.get(block));
        },
        accounts(block) {
          assert.equal(block, 'pan.accounts');
          return { read: () => settings.read(block),
            async reload() { await settings.reload(block); return settings.read(block); },
            update() { assert.fail('assembly reads must not write native accounts'); },
            close() {} };
        },
      };
      const configuration = { sessionId: randomUUID(),
        dataRoot: ${JSON.stringify(dataRoot)}, settings };
      globalThis.fixtureConfiguration = configuration;
      registerHooks({ load(url, context, next) {
        if (url === ${JSON.stringify(bootstrap)})
          return { format: 'module', shortCircuit: true, source: ${JSON.stringify(startup)} };
        return next(url, context);
      } });
      let client;
      try {
        await import(${JSON.stringify(entry)});
        const runtime = globalThis.fixtureRuntime;
        assert.ok(runtime, 'thin App startup must assemble its runtime');
        const { createRuntimeClient } = await import(${JSON.stringify(sharedClient)});
        const { contracts } = await import(${JSON.stringify(sharedContracts)});
        client = createRuntimeClient({ baseUrl: runtime.baseUrl });
        const description = await client.describe();
        assert.equal(description.ok, true);
        assert.equal(description.value.protocol, 'runtime.http.v1');
        assert.equal(description.value.product, 'aggregate');
        assert.deepEqual(description.value.modules.map(({ id, version, contract }) => [id, version, contract]), [
          ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'], ['source', '0.3.0', 'source.runtime.v2'],
          ['pan', '0.1.0', 'pan.runtime.v1'], ['tmdb', '0.1.0', 'tmdb.data.v1'],
          ['douban', '0.1.0', 'douban.data.v1'], ['film', '0.1.0', 'film.playback.v1'],
          ['netease', '0.1.0', 'netease.token.v1'],
          ['music-platform', '0.1.0', 'music.platform.v1'],
        ]);
        for (const module of description.value.modules)
          assert.deepEqual(module.methods, contracts[module.id].operations.map(name => contracts[module.id].prefix + name).sort());
        for (const method of ['music.script.sources.list', 'cat.script.status', 'source.runtime.status',
          'pan.settings.get', 'tmdb.data.settings.get', 'douban.data.settings.get', 'film.settings.get', 'film.library.get']) {
          const reply = await client.call(method);
          assert.equal(reply.ok, true, JSON.stringify(reply));
        }
        assert.deepEqual(counts, { A: 0, B: 0, D: 0 }, 'startup and local settings must not fetch providers');
        const external = await fetch(runtime.baseUrl + '/v1/film/library.get', {
          method: 'POST', body: '{}', headers: { 'Content-Type': 'application/json',
            Host: 'external.example.invalid', Origin: 'https://browser.example.invalid' },
        });
        assert.equal(external.status, 200);
        assert.equal(external.headers.get('access-control-allow-origin'), '*');
        assert.match(external.headers.get('x-meow-request-id'), /^[0-9a-f-]{36}$/);
        assert.deepEqual(await external.json(), await client.call('film.library.get'));
        const request = { provider: 'tmdb', path: '/3/movie/42', query: { language: 'zh-CN' } };
        async function notify(block) {
          const reply = await fetch(runtime.baseUrl + '/v1/settings/changed', { method: 'POST',
            body: JSON.stringify({ block }), headers: { 'Content-Type': 'application/json' } });
          assert.equal(reply.status, 200);
          assert.deepEqual(await reply.json(), { ok: true, contract: 'runtime.http.v1', value: { notified: true } });
        }
        documents.set('tmdb', { version: 1, settings: { apiBase: first.url, accessToken: 'fixture-only-token' } });
        documents.set('douban', { version: 1, settings: { apiBase: unrelated.url } });
        await notify('tmdb');
        await notify('douban');
        const otherRequest = { provider: 'douban', path: '/rexxar/api/v2/movie/43', query: {} };
        const other = await client.call('film.data.metadata', otherRequest);
        assert.equal(other.ok, true, JSON.stringify(other));
        assert.equal(other.value.title, 'metadata-D');
        const loaded = await client.call('film.data.metadata', request);
        assert.equal(loaded.ok, true, JSON.stringify(loaded));
        assert.equal(loaded.value.title, 'metadata-A');
        assert.deepEqual(await client.call('film.data.metadata', request), loaded);
        assert.deepEqual(counts, { A: 1, B: 0, D: 1 }, 'film reuses its cache and the assembled providers');
        documents.set('tmdb', { version: 1, settings: { apiBase: second.url, accessToken: 'fixture-only-token' } });
        await notify('tmdb');
        const changed = await client.call('film.data.metadata', request);
        assert.equal(changed.ok, true, JSON.stringify(changed));
        assert.equal(changed.value.title, 'metadata-B', 'provider mutation must revoke the old film cache');
        assert.deepEqual(await client.call('film.data.metadata', otherRequest), other);
        assert.deepEqual(counts, { A: 1, B: 1, D: 1 }, 'TMDB mutation preserves unrelated Douban cache');
        assert.deepEqual(await client.call('tmdb.data.settings.update', { apiBase: first.url }),
          { ok: false, contract: 'tmdb.data.v1', code: 'PERMISSION_DENIED' });
        assert.deepEqual(await client.call('film.data.metadata', request), changed);
        assert.deepEqual(counts, { A: 1, B: 1, D: 1 }, 'a denied second-owner mutation does not invalidate valid film data');
        const unchanged = await client.call('tmdb.data.settings.update', { unknown: true });
        assert.deepEqual(unchanged, { ok: false, contract: 'tmdb.data.v1', code: 'PERMISSION_DENIED' });
        assert.deepEqual(await client.call('film.data.metadata', request), changed);
        assert.deepEqual(counts, { A: 1, B: 1, D: 1 }, 'unused fields do not invalidate valid film data');
        const release = await client.call('film.session.media.release', {});
        assert.equal(release.ok, false);
        assert.equal(release.contract, 'film.playback.v1');
        assert.equal(release.code, 'INVALID_REQUEST', 'three-part operation reaches its business validator');
        const unknown = await client.call('film.session.media.release.extra', {});
        assert.equal(unknown.ok, false);
        assert.equal(unknown.code, 'METHOD_NOT_FOUND');
        assert.equal((await client.shutdown()).ok, true);
        console.log('APP_ASSEMBLY_OK ' + JSON.stringify({ platform: ${JSON.stringify(platform)}, modules: 9, counts }));
      } finally {
        await client?.close();
        await globalThis.fixtureRuntime?.close();
        await Promise.all([first.server, second.server, unrelated.server].map(server => new Promise(resolve => server.close(resolve))));
      }
    `;
    const result = spawnSync(process.execPath, ['--input-type=module', '-'], {
      input: script, cwd: root, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', timeout: 30_000,
    });
    assert.equal(result.signal, null, result.stderr || result.error?.message);
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    assert.match(result.stdout, /APP_ASSEMBLY_OK/);
    t.diagnostic(result.stdout.trim());
  });
}
