import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, seedClients, xeapi } from './support.mjs';

// HEAD MusicNeteaseFailureMatrixTests: actual business codes, not ordinary IDs.
test('WY root and selected FM data status require numeric integers and preserve real failure classifications', async () => {
  const matrix = [[301, 'AUTH_REQUIRED'], [401, 'AUTH_REQUIRED'], [403, 'FORBIDDEN'], [404, 'NOT_FOUND'],
    [429, 'RATE_LIMITED'], [509, 'RATE_LIMITED'], [250, 'VERIFICATION_REQUIRED'],
    [460, 'VERIFICATION_REQUIRED'], [461, 'VERIFICATION_REQUIRED'], [-1, 'UPSTREAM_ERROR'],
    [500, 'UPSTREAM_ERROR'], [999, 'UPSTREAM_ERROR'], [100000, 'UPSTREAM_ERROR'],
    [-100000, 'UPSTREAM_ERROR'], [true, 'INVALID_RESPONSE'], [false, 'INVALID_RESPONSE'],
    [200.5, 'INVALID_RESPONSE'], ['200', 'INVALID_RESPONSE'], [null, 'INVALID_RESPONSE']];
  for (const [code, error] of matrix) for (const nested of [false, true]) {
    const f = fixture(() => response(nested ? { code: 200, data: { code } } : { code }));
    await assert.rejects(f.request('wy', 'fm'), { code: error });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('WY search does not consume sibling data status and selected FM data status still controls success', async () => {
  for (const code of [0, 200, '200', 'unrelated', false, null, 301, 429]) {
    const f = fixture(() => response({ code: 200, result: { songs: [], songCount: 0 }, data: { code } }));
    assert.equal((await f.request('wy', 'search', { keyword: 'x' })).items.length, 0); await f.close();
  }
  for (const code of [0, 200]) {
    const f = fixture(() => response({ code: 200, data: { code, songs: [] } }));
    assert.equal((await f.request('wy', 'fm')).items.length, 0); await f.close();
  }
  const rootString = fixture(() => response({ code: '200', result: { songs: [], songCount: 0 } }));
  await assert.rejects(rootString.request('wy', 'search', { keyword: 'x' }), { code: 'INVALID_RESPONSE' });
  await rootString.close();
});

test('WY selected data failures remain failures for object, array and account recipes', async t => {
  const recipes = [
    ['searchHotPlayback', { keywords: ['current'] }], ['playlists', { ids: ['1'] }],
    ['fm', { ruleId: 'rule' }], ['channel', { id: 'newsong' }], ['channel', { id: 'chart' }],
    ['chorus', { id: '1' }], ['qr.create', {}],
    ['heartbeat', { songId: '1', continuation: { playlistId: '10', startMusicId: '1', count: 1 } }],
    ['audio', { id: '1' }], ['audio', { id: '1', quality: 'flac' }],
  ];
  for (const [operation, params] of recipes) for (const [code, expected] of [[301, 'AUTH_REQUIRED'], [false, 'INVALID_RESPONSE']])
    await t.test(operation + ':' + JSON.stringify(params) + ':' + code, async t => {
      const server = xeapi();
      const f = fixture(request => {
        const path = new URL(request.url).pathname;
        if (params.quality === 'flac') {
          if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
          if (path === '/api/bsr/sk/get') return server.key(request);
          server.open(request); return server.reply({ code: 200, data: { code } });
        }
        return response({ code: 200, data: { code } });
      }); seedClients(f); t.after(() => f.close());
      if (operation === 'heartbeat') await f.session('wy');
      await assert.rejects(f.request('wy', operation, params), { code: expected });
      assert.equal(f.requests.length, params.quality === 'flac' ? 3 : 1);
    });
});

test('WY root QR key and chorus results do not consume sibling data failures', async t => {
  for (const code of ['unconsumed-extension', 403]) for (const operation of ['qr.create', 'chorus'])
    await t.test(operation + ':' + code, async t => {
      const f = fixture(() => response({ code: 200, unikey: 'synthetic-key',
        chorus: [{ startTime: 30000, endTime: 45000 }], data: { code } }));
      t.after(() => f.close());
      const value = await f.request('wy', operation, { id: '1' });
      if (operation === 'chorus') assert.deepEqual(value.point, { start: 30, end: 45 });
      else assert.ok(value.url.includes('codekey=synthetic-key'));
      assert.equal(f.requests.length, 1);
    });
});

test('WY QR-only codes do not become public-read success and HTTP failure takes precedence over a login-shaped body', async () => {
  for (const code of [800, 801, 802, 803]) {
    const f = fixture(() => response({ code, data: [] }));
    await assert.rejects(f.request('wy', 'fm'), { code: 'UPSTREAM_ERROR' }); await f.close();
  }
  for (const status of [403, 429, 503]) {
    const f = fixture(() => response({ code: 301 }, { status }));
    await assert.rejects(f.request('wy', 'fm'), { code: 'UPSTREAM_HTTP' }); await f.close();
  }
  for (const bytes of [Buffer.alloc(0), Buffer.from('not-json'), Buffer.from([0x1f, 0x8b, 8]),
    Buffer.alloc(16), Buffer.from('[200]'), Buffer.from('{"success":true}')]) {
    const f = fixture(() => response(bytes));
    await assert.rejects(f.request('wy', 'fm'), { code: 'INVALID_RESPONSE' }); await f.close();
  }
});

test('KG final duplicate status and error fields, not truthiness, control the public recipe', async () => {
  for (const [raw, code] of [
    ['{"status":0,"status":1,"error_code":20018,"error_code":0,"data":{"lists":[],"total":0},"unused":1e400}', null],
    ['{"status":1,"error_code":0,"error_code":20018}', 'AUTH_REQUIRED'],
    ['{"status":1,"error_code":"20028"}', 'VERIFICATION_REQUIRED'],
    ['{"status":true}', 'INVALID_RESPONSE'], ['{"status":1,"ssaCode":"verification"}', 'VERIFICATION_REQUIRED'],
  ]) {
    const f = fixture(() => response(raw)); seedClients(f);
    if (code) await assert.rejects(f.request('kg', 'search', { keyword: 'x' }), { code });
    else assert.equal((await f.request('kg', 'search', { keyword: 'x' })).items.length, 0);
    assert.equal(f.requests.length, 1); await f.close();
  }
});
