import test from 'node:test';
import assert from 'node:assert/strict';
import { lxCapabilities, lxPayload } from '../src/lx-capabilities.js';

test('native quality vocabulary retains six actual declarations and exact original info.type', () => {
  const types = ['128k', '192k', '320k', 'flac', 'flac24bit', 'master'];
  const caps = lxCapabilities({ sources: { wy: { type: 'music', actions: ['musicUrl'], qualitys: types } } });
  assert.deepEqual(caps.wy.qualitys, types);
  for (const type of types) {
    const recording = { musicId: 'synthetic', types: [{ type }] };
    assert.deepEqual(lxPayload(caps, { siteId: 'wy', operation: 'musicUrl',
      payload: { quality: type, musicInfo: recording } }), {
      source: 'wy', action: 'musicUrl', info: { type, musicInfo: recording }
    });
  }
});
test('extended qualities remain declared data and missing qualities are not manufactured', () => {
  const caps = lxCapabilities({ sources: { wy: { type: 'music', actions: ['musicUrl'],
    qualitys: ['320k', 'dolby', 'hires', 'jymaster', 'auto'] } } });
  assert.deepEqual(caps.wy.qualitys, ['320k', 'dolby', 'hires', 'jymaster', 'auto']);
  for (const quality of ['192k', 'master'])
    assert.throws(() => lxPayload(caps, { siteId: 'wy', operation: 'musicUrl', payload: { quality } }));
  assert.throws(() => lxPayload(caps, { siteId: 'tx', operation: 'musicUrl', payload: { quality: '320k' } }));
});
test('local and extended actions are retained without granting host capabilities', () => {
  const caps = lxCapabilities({ sources: {
    local: { type: 'music', actions: ['lyric', 'musicUrl', 'search'], qualitys: ['master'] },
    wy: { type: 'music', actions: ['musicUrl', 'search', 'lyric'], qualitys: ['master'] }
  } });
  assert.deepEqual(caps.local.qualitys, ['master']);
  assert.deepEqual(caps.local.actions, ['lyric', 'musicUrl', 'search']);
  assert.deepEqual(caps.wy.actions, ['musicUrl', 'search', 'lyric']);
});

test('quality descriptors normalize their explicit type without dropping declared quality', () => {
  const caps = lxCapabilities({ sources: { tx: { type: 'music', actions: ['musicUrl'],
    qualitys: [{ type: '128k', label: 'standard' }] } } });
  assert.deepEqual(caps.tx.qualitys, ['128k']);
  assert.equal(lxPayload(caps, { siteId: 'tx', operation: 'musicUrl',
    payload: { quality: '128k', musicInfo: {} } }).info.type, '128k');
});
