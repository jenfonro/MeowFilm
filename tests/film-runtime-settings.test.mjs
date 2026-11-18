import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { contracts } from '../apps/runtime/contracts.mjs';
import { connectFilmPlayback, createAppRuntime, createRuntimeClient } from '../apps/runtime/entry.mjs';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';

const area = new URL('../tmp/test-results/film-logic-background/runtime-settings/', import.meta.url);
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
async function until(read) {
  for (let i = 0; i < 1000; i++) {
    const value = await read();
    if (value) return value;
    await delay(2);
  }
  assert.fail('The runtime did not reach the expected state');
}

async function fixture(t, initialPreferences = {}) {
  mkdirSync(area, { recursive: true });
  const root = mkdtempSync(new URL('case-', area));
  const calls = [];
  const server = http.createServer((request, response) => {
    assert.equal(request.headers['x-film-fixture'], 'controlled');
    response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': 4 });
    response.end(Buffer.from([0, 1, 2, 3]));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = `http://127.0.0.1:${server.address().port}/synthetic-video`;
  let preferences = { ...defaultPreferences(), ...initialPreferences };
  const accounts = new Map(['quark', 'uc'].map(provider => [provider, { provider, revision: 1, configured: true }]));
  const instances = new Map(['a', 'b'].map(key => [key, {
    installationId: `install-${key}`, generation: 1, state: 'ready',
    sites: [{ key, name: `Site ${key}` }],
  }]));
  let failStatus = false;
  const source = key => ({ key, engine: 'cat', installationId: `install-${key}`,
    siteId: key, generation: instances.get(key).generation, name: instances.get(key).sites[0].name });
  const module = (id, operations) => ({
    id, version: contracts[id].version, contract: contracts[id].contract,
    methods: Object.fromEntries(Object.entries(operations).map(([operation, run]) =>
      [contracts[id].prefix + operation, async input => {
        const value = await run(input);
        return value?.ok === false ? { ...value, contract: contracts[id].contract }
          : { ok: true, contract: contracts[id].contract, value };
      }])),
    ...(id === 'cat' ? { upload() { assert.fail('Unexpected upload'); } } : {}), async close() {},
  });
  const modules = [
    module('cat', {
      status: () => {
        if (failStatus) {
          failStatus = false;
          throw Object.assign(new Error('STORAGE_ERROR'), { code: 'STORAGE_ERROR' });
        }
        return { instances: structuredClone([...instances.values()]) };
      },
      edit: input => {
        instances.get(input.key).generation++;
        return input.failAfterCommit ? { ok: false, code: 'STORAGE_ERROR' } : { changed: true };
      },
      invoke: input => {
        calls.push(['cat', input.siteId, input.operation]);
        assert.equal(input.operation, 'detail');
        const provider = input.siteId === 'a' ? 'quark' : 'uc';
        return { detailId: 0, lines: [{ id: 'main', label: 'Main', provider, episodes: [{
          id: 'share', name: 'Film', index: 0,
          share: { provider, url: `https://${provider === 'quark' ? 'pan.quark.cn' : 'drive.uc.cn'}/s/fixture` },
        }] }] };
      },
    }),
    module('pan', {
      'settings.get': () => ({ accounts: structuredClone([...accounts.values()]) }),
      list: input => {
        calls.push(['list', input.provider]);
        return { files: [{ name: 'Film.1080p', path: '/Film.1080p',
          file: { kind: 'stored', provider: input.provider,
            accountRevision: accounts.get(input.provider).revision, fileId: `file-${input.provider}` } }] };
      },
      play: input => {
        calls.push(['pan', input.provider, input.file.fileId]);
        assert.equal(input.file.accountRevision, accounts.get(input.provider).revision);
        return { url: address, mode: input.mode, headers: { 'X-Film-Fixture': 'controlled' } };
      },
    }),
  ];
  const film = connectFilmPlayback(modules, { dataRoot: root, nativeSettings: {
    read: block => block === 'film.preferences' ? { preferences: structuredClone(preferences) } : null,
  } });
  const app = createAppRuntime({ sessionId: randomUUID(), modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  const rpc = async (operation, input = {}) => okay(await client.call(`film.${operation}`, input));
  const snapshot = id => rpc('session.snapshot', { sessionId: id });
  const command = (id, action, input = {}) => rpc('session.command', { sessionId: id, action, ...input });
  const open = async key => {
    const currentSource = source(key);
    const target = await rpc('data.target', { target: { kind: 'source', title: 'Film',
      source: { key, detailId: 0 } }, sources: [currentSource] });
    const first = await rpc('session.open', { platform: 'test', target, sources: [currentSource] });
    await until(async () => {
      const state = await snapshot(first.sessionId);
      return state.stage === 'idle' && !state.browsing.loading && state.browsing.episodes.length;
    });
    await command(first.sessionId, 'play');
    const attempt = await until(async () => (await snapshot(first.sessionId)).attempt);
    const receipt = await rpc('session.attempt', { sessionId: first.sessionId, attemptId: attempt.id });
    assert.ok(receipt.mediaOrigin);
    assert.notEqual(receipt.url, address);
    return { id: first.sessionId, attempt, receipt };
  };
  t.after(async () => {
    await client.close(); await app.close();
    await new Promise(resolve => { server.closeAllConnections(); server.close(resolve); });
    rmSync(root, { recursive: true, force: true });
  });
  return { calls, instances, accounts, film, app, client, rpc, snapshot, command, open,
    failNextStatus: () => { failStatus = true; },
    setPreferences: update => { preferences = { ...preferences, ...update }; } };
}

async function readable(receipt) {
  const response = await fetch(receipt.url);
  await response.arrayBuffer();
  return response.status;
}

test('native saved preference extensions and unused bad fields do not block library reads or playback', async t => {
  const preferences = { ruleOrder: ['extension', 'quality'], panOrder: null, keywords: [null, 'Film'],
    siteCleanKeywords: [], extra: { unconsumed: true } };
  const f = await fixture(t, preferences);
  const library = await f.rpc('library.get');
  assert.deepEqual(library.preferences, { ...defaultPreferences(), ...preferences });
  const a = await f.open('a');
  assert.equal(await readable(a.receipt), 200);
  await f.film.settingsChanged('film.preferences');
  assert.deepEqual((await f.snapshot(a.id)).attempt, a.attempt);
  assert.equal(await readable(a.receipt), 200);
});

test('native same-value settings preserve directories, pending/current receipts and controlled resources', async t => {
  const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
  await f.command(a.id, 'media.started', { attemptId: a.attempt.id });
  const before = [await f.snapshot(a.id), await f.snapshot(b.id)], reads = f.calls.length;
  for (const block of ['pan.accounts', 'cat.sources', 'film.preferences', 'pan.accounts', 'cat.sources']) {
    await f.film.settingsChanged(block);
  }
  assert.deepEqual([await f.snapshot(a.id), await f.snapshot(b.id)], before);
  assert.equal(f.calls.length, reads);
  assert.equal(await readable(a.receipt), 200);
  assert.equal(await readable(b.receipt), 200);
});

for (const started of [false, true]) {
test(`native provider display and iOS automatic-preparation preferences do not revoke formal media (started=${started})`, async t => {
  const f = await fixture(t), a = await f.open('a');
  if (started) await f.command(a.id, 'media.started', { attemptId: a.attempt.id });
  const before = await f.snapshot(a.id), reads = f.calls.length;
  f.setPreferences({ metadataViewProvider: 'douban', iosAutoPrepare: false });
  await f.film.settingsChanged('film.preferences');
  const after = await f.snapshot(a.id);
  assert.equal(after.preferences.metadataViewProvider, 'douban');
  assert.equal(after.preferences.iosAutoPrepare, false);
  assert.deepEqual(after.attempt, before.attempt);
  assert.deepEqual(after.current, before.current);
  assert.deepEqual(after.target, before.target);
  assert.deepEqual(after.browsing, before.browsing);
  assert.equal(f.calls.length, reads);
  assert.equal(await readable(a.receipt), 200);
  await f.film.settingsChanged('film.preferences');
  assert.deepEqual(await f.snapshot(a.id), after);
});
}

test('native account changes revoke only that provider and unchanged notifications preserve the replacement lease', async t => {
  const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
  await f.command(a.id, 'media.started', { attemptId: a.attempt.id });
  const before = await f.snapshot(b.id), reads = f.calls.length;
  f.accounts.get('quark').revision++;
  await Promise.all([f.film.settingsChanged('pan.accounts'), f.film.settingsChanged('pan.accounts')]);
  assert.equal((await f.snapshot(a.id)).current, null);
  assert.deepEqual(await f.snapshot(b.id), before);
  assert.equal(f.calls.length, reads);
  assert.notEqual(await readable(a.receipt), 200);
  assert.equal(await readable(b.receipt), 200);
  const replacement = await f.open('a');
  const replacementState = await f.snapshot(replacement.id);
  await f.film.settingsChanged('pan.accounts');
  assert.deepEqual(await f.snapshot(replacement.id), replacementState);
  assert.equal(await readable(replacement.receipt), 200);
});

for (const change of ['generation', 'remove']) {
  test(`native cat ${change} revokes only its actual installation and keeps the other source live`, async t => {
    const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
    await f.command(a.id, 'media.started', { attemptId: a.attempt.id });
    const before = await f.snapshot(b.id), reads = f.calls.length;
    if (change === 'generation') f.instances.get('a').generation++;
    else f.instances.delete('a');
    await f.film.settingsChanged('cat.sources');
    assert.equal((await f.snapshot(a.id)).current, null);
    assert.deepEqual(await f.snapshot(b.id), before);
    assert.equal(f.calls.length, reads);
    assert.notEqual(await readable(a.receipt), 200);
    assert.equal(await readable(b.receipt), 200);
    await f.film.settingsChanged('cat.sources');
    assert.deepEqual(await f.snapshot(b.id), before);
  });
}

test('native source display-only changes keep leases and browsing while updating the matching label', async t => {
  const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
  const before = await f.snapshot(a.id), other = await f.snapshot(b.id), reads = f.calls.length;
  f.instances.get('a').sites[0].name = 'Renamed site';
  await f.film.settingsChanged('cat.sources');
  const after = await f.snapshot(a.id);
  assert.equal(after.sources[0].source.name, 'Renamed site');
  assert.equal(after.attempt.id, before.attempt.id);
  assert.deepEqual(after.browsing, before.browsing);
  assert.deepEqual(await f.snapshot(b.id), other);
  assert.equal(f.calls.length, reads);
  assert.equal(await readable(a.receipt), 200);
});

test('provider mutation receipts and native notifications share the same observed installation state', async t => {
  const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
  const before = await f.snapshot(b.id);
  const failed = await f.client.call('cat.script.edit', { key: 'a', failAfterCommit: true });
  assert.equal(failed.ok, false);
  assert.equal(failed.code, 'STORAGE_ERROR');
  assert.equal((await f.snapshot(a.id)).attempt, null);
  assert.deepEqual(await f.snapshot(b.id), before);
  const replacement = await f.open('a');
  const state = await f.snapshot(replacement.id);
  await f.film.settingsChanged('cat.sources');
  assert.deepEqual(await f.snapshot(replacement.id), state);
  assert.equal(await readable(replacement.receipt), 200);
});

test('a failed provider observation does not poison a queued change or lose the previous revocation baseline', async t => {
  const f = await fixture(t), a = await f.open('a'), b = await f.open('b');
  const before = await f.snapshot(b.id);
  f.instances.get('a').generation++;
  f.failNextStatus();
  const [failed, recovered] = await Promise.allSettled([
    f.film.settingsChanged('cat.sources'), f.film.settingsChanged('cat.sources'),
  ]);
  assert.equal(failed.status, 'rejected');
  assert.equal(failed.reason.code, 'STORAGE_ERROR');
  assert.equal(recovered.status, 'fulfilled');
  assert.equal((await f.snapshot(a.id)).attempt, null);
  assert.deepEqual(await f.snapshot(b.id), before);
  assert.notEqual(await readable(a.receipt), 200);
  assert.equal(await readable(b.receipt), 200);
});
