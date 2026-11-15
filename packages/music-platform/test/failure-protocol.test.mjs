import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, seedClients } from './support.mjs';

// HEAD MusicNeteaseFailureMatrixTests: actual business codes, not ordinary IDs.
test('WY root and consumed nested codes require numeric integers and preserve real failure classifications', async () => {
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
  for (const code of [0, 200]) {
    const f = fixture(() => response({ code: 200, result: { songs: [], songCount: 0 }, data: { code } }));
    assert.equal((await f.request('wy', 'search', { keyword: 'x' })).items.length, 0); await f.close();
  }
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
