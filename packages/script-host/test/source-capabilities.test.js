import test from 'node:test';
import assert from 'node:assert/strict';
import { createNodeSourceCapabilities } from '../src/source-capabilities.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';
import { parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

const exact = '{"integer":9007199254740993,"decimal":1.234567890123456789,"large":1e400,"zero":-0,' +
  '"nested":{"kept":true},"é":1,"é":2}';
const prefix = 'source:exact:1:';
const profile = `${prefix}storage`;

test('profile storage clones exact JSON on writes, reads, snapshots, restore and failed commits', async t => {
  let failing = false;
  const owner = createNodeSourceCapabilities({ onProfileChange() {
    if (failing) throw Object.assign(new Error('fixture'), { code: 'STORAGE_ERROR' });
  } });
  t.after(() => owner.releaseProfiles('source:exact:'));
  const client = createCapabilityClient({ invoke: (name, input, context) => owner.invoke(name, input, context) });
  const call = (name, input = {}) => client.call(name, { version: 2, profile, key: 'saved', ...input });
  const original = parseJSONText(exact);
  assert.equal(await call('storage.set', { value: original }), true);
  original.nested.kept = false;
  const value = await call('storage.get');
  assert.equal(stringifyJSONValue(value), exact);
  value.nested.kept = false;
  assert.equal(stringifyJSONValue(await call('storage.get')), exact);

  const saved = owner.snapshotProfiles(prefix);
  await owner.releaseProfiles('source:exact:');
  assert.equal(await call('storage.get'), null);
  owner.restoreProfiles(prefix, saved);
  saved.storage[0][1][0][1].nested.kept = false;
  assert.equal(stringifyJSONValue(await call('storage.get')), exact);
  assert.equal(stringifyJSONValue(owner.snapshotProfiles(prefix).storage[0][1][0][1]), exact);

  await call('storage.set', { key: 'other', value: 'ordinary write' });
  failing = true;
  await assert.rejects(call('storage.set', { value: 'replacement' }), { code: 'STORAGE_ERROR' });
  assert.equal(stringifyJSONValue(await call('storage.get')), exact);
  assert.equal(await call('storage.get', { key: 'other' }), 'ordinary write');
  assert.equal(stringifyJSONValue(owner.snapshotProfiles(prefix).storage[0][1][0][1]), exact);
});

test('virtual metadata clones exact JSON without sharing input or output and releases by generation', async t => {
  const owner = createNodeSourceCapabilities();
  t.after(() => owner.releaseProfiles('source:exact:'));
  const client = createCapabilityClient({ invoke: (name, input, context) => owner.invoke(name, input, context) });
  const oldProfile = 'source:exact:1', currentProfile = 'source:exact:2';
  const metadata = parseJSONText(exact);
  for (const profile of [oldProfile, currentProfile]) {
    assert.deepEqual(await client.call('virtual.register', {
      version: 2, profile, route: 'route-1', metadata,
    }), { url: `meow-source://${encodeURIComponent(profile)}/route-1` });
  }
  metadata.nested.kept = false;
  const resolved = owner.resolveVirtual(oldProfile, 'route-1');
  assert.equal(stringifyJSONValue(resolved), exact);
  resolved.nested.kept = false;
  assert.equal(stringifyJSONValue(owner.resolveVirtual(oldProfile, 'route-1')), exact);
  await owner.releaseProfiles('source:exact:', currentProfile);
  assert.equal(owner.resolveVirtual(oldProfile, 'route-1'), null);
  assert.equal(stringifyJSONValue(owner.resolveVirtual(currentProfile, 'route-1')), exact);
  assert.equal(await client.call('virtual.unregister', {
    version: 2, profile: currentProfile, route: 'route-1',
  }), true);
  assert.equal(owner.resolveVirtual(currentProfile, 'route-1'), null);
});
