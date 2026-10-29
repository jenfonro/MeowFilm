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
test('native quality vocabulary does not manufacture missing or official spatial declarations', () => {
  const caps = lxCapabilities({ sources: { wy: { type: 'music', actions: ['musicUrl'],
    qualitys: ['320k', 'dolby', 'hires', 'jymaster', 'auto'] } } });
  assert.deepEqual(caps.wy.qualitys, ['320k']);
  for (const quality of ['192k', 'master', 'dolby', 'auto'])
    assert.throws(() => lxPayload(caps, { siteId: 'wy', operation: 'musicUrl', payload: { quality } }));
  assert.throws(() => lxPayload(caps, { siteId: 'tx', operation: 'musicUrl', payload: { quality: '320k' } }));
});
test('native quality vocabulary leaves local actions and platform permissions unchanged', () => {
  const caps = lxCapabilities({ sources: {
    local: { type: 'music', actions: ['lyric', 'musicUrl', 'search'], qualitys: ['master'] },
    wy: { type: 'music', actions: ['musicUrl', 'search', 'lyric'], qualitys: ['master'] }
  } });
  assert.deepEqual(caps.local.qualitys, []);
  assert.deepEqual(caps.local.actions, ['musicUrl', 'lyric']);
  assert.deepEqual(caps.wy.actions, ['musicUrl']);
});
