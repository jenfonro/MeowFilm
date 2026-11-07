import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { HTTP_PROTOCOL } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../apps/runtime/modules/source-runtime.mjs';
import { createNodeSourceCapabilities } from '@meowfilm/script-host';
import { lxScript } from '../packages/script-host/test/fixtures.js';
import { rawRequest } from '../packages/runtime-bridge/test/http-fixture.js';
import { moduleHttp } from './runtime-http-fixture.mjs';

function nestedArrays(depth) {
  let value = null;
  for (let index = 0; index < depth; index++) value = [value];
  return value;
}

function musicHttp(t) {
  const directory = fileURLToPath(new URL('../tmp/test-results/runtime-depth/', import.meta.url));
  mkdirSync(directory, { recursive: true });
  const dataRoot = mkdtempSync(join(directory, 'case-'));
  const rpc = moduleHttp(t, createMusicScriptModule({
    dataRoot, network: async () => { assert.fail('No network is used by this fixture'); },
  }));
  t.after(async () => {
    await rpc.transport.runtime.close();
    rmSync(dataRoot, { recursive: true });
  });
  return { rpc, dataRoot };
}

async function installMusic(rpc, source) {
  const installationId = randomUUID();
  const uploaded = await rpc.upload(installationId, 1, Buffer.from(source), { basename: 'fixture.js' });
  assert.equal(uploaded.ok, true, JSON.stringify(uploaded));
  const added = await rpc('sources.add', { installationId, sourceRevision: 1 });
  assert.equal(added.ok, true, JSON.stringify(added));
  assert.equal(added.value.item.installationId, installationId);
  assert.equal(added.value.item.enabled, false);
  const enabled = await rpc('sources.enable', { installationId });
  assert.equal(enabled.ok, true, JSON.stringify(enabled));
  assert.equal(enabled.value.item.state, 'ready');
  return enabled.value.item;
}

test('HTTP LX accepts the original 62-level musicInfo at total depth 64 and rejects depth 65 before dispatch',
  { timeout: 15_000 }, async t => {
    const { rpc } = musicHttp(t);
    const source = lxScript({
      setup: 'let calls = 0;',
      handler: `async ({info}) => {
        calls++;
        if (info.musicInfo.returnDepth) {
          let value = null;
          for (let i = 0; i < info.musicInfo.returnDepth; i++) value = [value];
          return value;
        }
        let value = info.musicInfo, depth = 0;
        while (Array.isArray(value)) { depth++; value = value[0]; }
        return { depth, leaf: value, calls };
      }`,
    });
    const item = await installMusic(rpc, source);
    const request = musicInfo => ({
      installationId: item.installationId,
      siteId: 'kw', operation: 'musicUrl', payload: { quality: '320k', musicInfo },
    });
    // The body and payload add two layers. Native dispatch's argument list adds none.
    const original = request(nestedArrays(62));
    assert.deepEqual(await rpc('invoke', original), {
      ok: true, contract: musicScriptContract, value: { depth: 62, leaf: null, calls: 1 },
    });
    const tooDeep = request(nestedArrays(63));
    await assert.rejects(rpc('invoke', tooDeep), { code: 'INVALID_REQUEST' });
    // Bypass client validation to prove the real HTTP request boundary also rejects it.
    const rejected = await rawRequest(rpc.transport.runtime.baseUrl, rpc.transport.identity, {
      path: '/v1/music/invoke', body: JSON.stringify(tooDeep),
    }).response;
    assert.equal(rejected.status, 400);
    assert.deepEqual(rejected.value, { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_REQUEST' });
    assert.deepEqual(await rpc('invoke', request({ returnDepth: 64 })), {
      ok: true, contract: musicScriptContract, value: nestedArrays(64),
    });
    assert.deepEqual(await rpc('invoke', request({ returnDepth: 65 })), {
      ok: false, contract: musicScriptContract, code: 'INVALID_REQUEST',
    });
    assert.deepEqual(await rpc('invoke', original), {
      ok: true, contract: musicScriptContract, value: { depth: 62, leaf: null, calls: 4 },
    });
  });

test('HTTP LX private KV preserves depth 64 through persistence and rejects depth 65 without overwriting it',
  { timeout: 15_000 }, async t => {
    const { rpc, dataRoot } = musicHttp(t);
    const item = await installMusic(rpc, lxScript({
      handler: `async ({info}, {storage}) => {
        if (info.musicInfo.depth) {
          let value = null;
          for (let i = 0; i < info.musicInfo.depth; i++) value = [value];
          await storage.set('depth', value);
        }
        return await storage.get('depth');
      }`,
    }));
    const invoke = musicInfo => rpc('invoke', {
      installationId: item.installationId, siteId: 'kw', operation: 'musicUrl',
      payload: { quality: '320k', musicInfo },
    });
    assert.deepEqual(await invoke({ depth: 64 }), {
      ok: true, contract: musicScriptContract, value: nestedArrays(64),
    });
    const persisted = () => JSON.parse(readFileSync(join(dataRoot, 'music/data', `${item.installationId}.json`), 'utf8'));
    assert.deepEqual(persisted(), { schema: 1, sites: { kw: [{ key: 'depth', value: nestedArrays(64) }] } });
    assert.deepEqual(await invoke({ depth: 65 }), {
      ok: false, contract: musicScriptContract, code: 'INVALID_REQUEST',
    });
    assert.deepEqual(persisted(), { schema: 1, sites: { kw: [{ key: 'depth', value: nestedArrays(64) }] } });
    const disabled = await rpc('sources.disable', { installationId: item.installationId });
    assert.equal(disabled.ok, true, JSON.stringify(disabled));
    const reopened = await rpc('sources.enable', { installationId: item.installationId });
    assert.equal(reopened.ok, true, JSON.stringify(reopened));
    assert.equal(reopened.value.item.state, 'ready');
    assert.ok(reopened.value.item.generation > item.generation);
    assert.deepEqual(await invoke({}), {
      ok: true, contract: musicScriptContract, value: nestedArrays(64),
    });
  });

test('HTTP source returns the original 8/62/64-level values and rejects 65 without replacing its instance',
  { timeout: 15_000 }, async t => {
    const network = async () => { assert.fail('This source fixture has no network'); };
    const rpc = moduleHttp(t, createSourceRuntimeModule({
      network, capabilityOwner: createNodeSourceCapabilities({ network }), platform: 'test',
    }));
    const source = `
      let calls = 0;
      export default {
        searchContent(keyword) {
          calls++;
          if (keyword === 'count') return calls;
          let value = null;
          for (let i = 0; i < Number(keyword === 'envelope' ? 64 : keyword); i++) value = [value];
          return keyword === 'envelope' ? {ok:true, value} : value;
        }
      };
    `;
    assert.equal((await rpc.upload('source-depth', 1, Buffer.from(source), { basename: 'fixture.js' })).ok, true);
    const loaded = await rpc('load', {
      installationId: 'source-depth', sourceRevision: 1, revision: 1, expectedRevision: 0,
      protocol: 'cat', version: '1.0.0', metadata: {},
    });
    assert.equal(loaded.ok, true, JSON.stringify(loaded));
    const invoke = keyword => rpc('invoke', {
      installationId: 'source-depth', revision: 1, generation: loaded.value.instance.generation,
      action: 'search', payload: { keyword: String(keyword) }, timeoutMs: 1000,
    });
    for (const depth of [8, 62, 64]) {
      assert.deepEqual(await invoke(depth), {
        ok: true, contract: sourceRuntimeContract, value: nestedArrays(depth),
      });
    }
    for (const keyword of [65, 'envelope']) {
      assert.deepEqual(await invoke(keyword), {
        ok: false, contract: sourceRuntimeContract, code: 'INVALID_REQUEST',
      });
    }
    assert.deepEqual(await invoke(64), {
      ok: true, contract: sourceRuntimeContract, value: nestedArrays(64),
    });
    assert.deepEqual(await invoke('count'), {
      ok: true, contract: sourceRuntimeContract, value: 7,
    });
    const status = await rpc('status');
    assert.equal(status.ok, true, JSON.stringify(status));
    assert.deepEqual(status.value.instances, [loaded.value.instance]);
  });
