import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { fixture, response, seedClients, gate, now, nativeHTTP } from './support.mjs';
import { createMusicPlatform } from '../src/index.mjs';

// HEAD MusicNeteasePlaybackLogCodecTests / MusicNeteasePlaybackUploadTests.
// NCBLv3 uses RSA modular wrapping, ChaCha20 and a raw Zstandard frame. Gzip/AES
// belong to QQ/KG feedback, not this file format. No private codec is exported.
const keyBytes = Buffer.from(Array.from({ length: 32 }, (_, index) => index));
const uuidBytes = Buffer.from(Array.from({ length: 16 }, (_, index) => index + 16));
const wrappedFixture = Buffer.from('dda20437ce173c34273cb03bffb85db8f3ce53bc2f1334a752303d26890094af', 'hex');
function entropy(t, draws) {
  const original = crypto.randomBytes, observed = [];
  t.mock.method(crypto, 'randomBytes', size => {
    if (size !== 32 && size !== 16) return original(size);
    const next = draws?.shift();
    const bytes = next ?? original(size);
    assert.equal(bytes.length, size);
    observed.push(Buffer.from(bytes)); return Buffer.from(bytes);
  });
  syncBuiltinESMExports(); t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  return observed;
}
function fileFrom(request) {
  assert.equal(request.url, 'https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true');
  assert.equal(request.method, 'POST'); assert.equal(request.headers['Cache-Control'], 'no-store');
  assert.equal(request.headers['User-Agent'], 'Meowfilm');
  const boundary = request.headers['Content-Type'].split('boundary=')[1], body = request.body;
  assert.ok(boundary.startsWith('meowfilm-'));
  const prefix = body.indexOf('\r\n\r\n'), trailer = Buffer.from(`\r\n--${boundary}--\r\n`);
  assert.deepEqual(body.subarray(-trailer.length), trailer);
  const headers = body.subarray(0, prefix).toString(), filename = /filename="([^"]+)"/.exec(headers)[1];
  assert.match(filename, /^play_[\da-f-]{36}$/);
  assert.ok(headers.includes('Content-Disposition: form-data; name="file"'));
  assert.ok(headers.includes('Content-Type: multipart/form-data'));
  return { bytes: body.subarray(prefix + 4, -trailer.length), filename };
}
function inspect(bytes, key) {
  assert.equal(bytes.subarray(0, 4).toString(), 'NCBL'); assert.equal(bytes.readUInt32LE(4), 3);
  const header = bytes.readUInt16LE(8), metadataSize = bytes.readUInt16LE(72), uuid = bytes.subarray(10, 26);
  assert.equal(header, 74 + metadataSize); assert.equal(bytes.readUInt16LE(70), 0x4343);
  assert.equal(bytes.readUInt32LE(58), 0);
  const iv = Buffer.alloc(16); iv.writeUInt32LE(uuid.readUInt32LE(12) >>> 2); uuid.copy(iv, 4, 0, 12);
  const open = (value, secret) => {
    const cipher = crypto.createDecipheriv('chacha20', secret, iv);
    return Buffer.concat([cipher.update(value), cipher.final()]);
  };
  const metadata = JSON.parse(open(bytes.subarray(74, header), bytes.subarray(26, 58)));
  const frames = [], decoded = [];
  for (let offset = header; offset < bytes.length;) {
    const length = bytes.readUInt16LE(offset), sequence = bytes.readUInt32LE(offset + 2);
    assert.ok(length > 0 && length <= 32768); assert.equal(sequence, frames.length);
    assert.ok(offset + 6 + length <= bytes.length);
    frames.push(length); decoded.push(open(bytes.subarray(offset + 6, offset + 6 + length), key)); offset += length + 6;
    if (offset >= bytes.length) assert.equal(offset, bytes.length);
  }
  assert.equal(bytes.readUInt32LE(62), frames.length - 1);
  assert.equal(bytes.readUInt32LE(66), frames.reduce((sum, value) => sum + value + 6, 0));
  const raw = Buffer.concat(decoded);
  assert.equal(raw.readUInt32LE(), 0xfd2fb528); assert.equal(raw[4], 0xa0);
  assert.equal(raw.readUInt32LE(5), raw.length - 12);
  assert.equal(raw.readUIntLE(9, 3), ((raw.length - 12) << 3) | 1);
  return { metadata, records: raw.subarray(12), frames };
}
const base = { event: 'start', occurredAt: now / 1000 + 0.125, target: { nativeId: '101' } };
function expectedRecord(event) {
  const start = event.event === 'start', target = event.target, fields = { id: Number(target.nativeId), type: 'song',
    _logTime: Math.floor(event.occurredAt * 1000), time: start ? 0 : Math.floor(event.mediaListened),
    realtime: start ? 0 : Math.floor(event.listened) };
  if (target.source) fields.source = target.source;
  if (target.sourceId) fields.sourceId = target.sourceId;
  if (target.algorithm) fields.alg = target.algorithm;
  if (target.origin?.kind === 'heartbeat') fields.play_scene = 'heartbeat';
  if (!start) { fields.end = event.reason; fields.play_speed = event.rate; }
  return Buffer.from(`${Math.floor(event.occurredAt)}\x01${start ? '_plv' : '_pld'}\x01${JSON.stringify(fields)}\n`);
}
function sizedRecords(length) {
  const events = [];
  while (length > 0) {
    const size = Math.min(15000, length), event = { ...base, target: { ...base.target, algorithm: 'x' } };
    event.target.algorithm = 'x'.repeat(size - expectedRecord(event).length + 1);
    events.push(event); length -= size;
  }
  return events;
}

test('WY feedback send consumes event fields without reading unused prepare, duration or position facts', async t => {
  const records = [], events = [
    { ...base, duration: {}, position: {}, listened: {}, mediaListened: {}, rate: {} },
    { ...base, event: 'end', duration: {}, position: {}, listened: 2.5, mediaListened: 5.5, rate: 2, reason: 'ui' },
  ];
  const f = fixture(request => {
    const { bytes, filename } = fileFrom(request);
    records.push(inspect(bytes, keyBytes).records);
    return response({ code: 200, data: { successfiles: [filename] } });
  });
  seedClients(f); t.after(() => f.close()); await f.session('wy');
  entropy(t, [keyBytes, uuidBytes, keyBytes, uuidBytes]);
  assert.deepEqual(await f.request('wy', 'feedback.prepare', events[0]), { status: 'registered', sessionId: null });
  assert.equal(f.requests.length, 0);
  for (const event of events)
    assert.deepEqual(await f.request('wy', 'feedback.send', event), { status: 'confirmed', errorCode: null });
  assert.deepEqual(records, events.map(expectedRecord));
  assert.deepEqual(await f.request('wy', 'feedback.send', { ...events[1], mediaListened: {} }),
    { status: 'notSent', errorCode: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 2);
});

test('NCBL public semantic upload retains original milliseconds, separate media/wall totals, attribution and only original MUSIC_U metadata', async t => {
  const records = [];
  const f = fixture(request => {
    const { bytes, filename } = fileFrom(request);
    assert.deepEqual(bytes.subarray(10, 26), uuidBytes);
    assert.deepEqual(bytes.subarray(26, 58), wrappedFixture, 'independent HEAD modular RSA fixture');
    const decoded = inspect(bytes, keyBytes);
    assert.deepEqual(decoded.metadata, { MUSIC_U: 'synthetic+original/key=' });
    records.push(...decoded.records.toString().trimEnd().split('\n'));
    assert.equal(request.headers.Cookie, 'MUSIC_U=synthetic+original/key=; __csrf=synthetic-csrf');
    return response({ code: 200, data: { successfiles: [filename] } }, { cookies: ['MUSIC_U=never-imported-response'] });
  });
  seedClients(f); await f.session('wy', '42', 'MUSIC_U=synthetic+original/key=; __csrf=synthetic-csrf');
  const draws = entropy(t, [keyBytes, uuidBytes]);
  const events = [
    { ...base, rate: {}, listened: {}, mediaListened: {}, duration: {}, position: {},
      target: { nativeId: '101', source: 'search', sourceId: '真实关键词', algorithm: 'actual-alg' } },
    { ...base, event: 'end', occurredAt: now / 1000 + 4.125, listened: 3.75, mediaListened: 7.5,
      duration: {}, position: {}, rate: 2, reason: 'ui', target: { nativeId: '101', source: 'search', sourceId: '真实关键词', algorithm: 'actual-alg' } },
    { ...base, event: 'end', occurredAt: now / 1000 + 90000, listened: 90000, mediaListened: 2880000,
      position: 2880000, rate: 32, reason: 'interrupt' },
  ];
  assert.equal((await f.request('wy', 'feedback.upload', { events })).status, 'confirmed');
  assert.deepEqual(Buffer.from(records.join('\n') + '\n'), Buffer.concat(events.map(expectedRecord)));
  for (const row of records) {
    const value = JSON.parse(row.split('\x01')[2]);
    for (const absent of ['mainsite', 'mainsiteWeb', 'refer', 'deviceId', 'wifi', 'osver', 'resolution']) assert.equal(value[absent], undefined);
  }
  assert.equal(draws.length, 2); assert.equal(f.requests.length, 1);
  assert.ok(!JSON.stringify(await f.clientState()).includes('never-imported-response')); await f.close();
});

test('NCBL frames preserve exact full public record boundaries and reject oversized records or metadata without dispatch', async t => {
  let draws;
  const files = [];
  const f = fixture(request => {
    const { bytes, filename } = fileFrom(request);
    files.push(bytes); const decoded = inspect(bytes, draws.at(-2));
    assert.equal(decoded.records.length, expectedLength);
    assert.deepEqual(decoded.records, Buffer.concat(events.map(expectedRecord)));
    assert.deepEqual(decoded.frames, expectedFrames);
    return response({ code: 200, data: { successfiles: [filename] } });
  });
  seedClients(f); await f.session('wy', '42', 'MUSIC_U=' + 'x'.repeat(8178));
  draws = entropy(t);
  let expectedLength, expectedFrames, events;
  for (const [length, frames] of [
    [0, [12]],
    [32755, [32767]],
    [32756, [32768]],
    [32757, [32768, 1]],
    [65524, [32768, 32768]],
    [65525, [32768, 32768, 1]],
    [65536, [32768, 32768, 12]],
  ]) {
    expectedLength = length; expectedFrames = frames; events = sizedRecords(length);
    assert.equal((await f.request('wy', 'feedback.upload', { events })).status, 'confirmed');
    assert.equal(files.at(-1).readUInt16LE(8), 8266);
  }
  const count = f.requests.length;
  assert.deepEqual(await f.request('wy', 'feedback.upload', { events: sizedRecords(65537) }),
    { status: 'notSent', errorCode: 'LIMIT' });
  assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [{ ...base, target: { nativeId: '101', algorithm: 'x'.repeat(16385) } }] }),
    { status: 'notSent', errorCode: 'LIMIT' });
  await f.session('wy', '42', 'MUSIC_U=' + 'x'.repeat(8179));
  assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [] }), { status: 'notSent', errorCode: 'LIMIT' });
  assert.equal(f.requests.length, count); await f.close();
});

test('NCBL rejection sampling keeps unsigned N-1 and zero wrapping; each normal payload gets a fresh nonce and key', async t => {
  const modulus = Buffer.from('fd90bd466ff9bc8a3fec2fbcf263b90d5c564879fa5d7aab89b31c1d5cb4139d', 'hex');
  const largest = Buffer.from(modulus); largest[31]--;
  const files = [];
  const f = fixture(request => {
    const file = fileFrom(request); files.push(file.bytes);
    return response({ code: 200, data: { successfiles: [file.filename] } });
  }); seedClients(f); await f.session('wy');
  const draws = entropy(t, [modulus, Buffer.alloc(32, 255), largest, uuidBytes, Buffer.alloc(32), uuidBytes]);
  for (let count = 0; count < 4; count++) assert.equal((await f.request('wy', 'feedback.upload', { events: [base] })).status, 'confirmed');
  assert.deepEqual(files[0].subarray(26, 58), largest); assert.deepEqual(files[1].subarray(26, 58), Buffer.alloc(32));
  assert.notDeepEqual(files[2].subarray(10, 26), files[3].subarray(10, 26));
  assert.notDeepEqual(files[2].subarray(26, 58), files[3].subarray(26, 58));
  assert.ok(draws.length >= 10); await f.close();
});

test('NCBL entropy failure has no fallback and cannot dispatch an upload', async t => {
  const f = fixture(() => assert.fail('entropy failure cannot reach transport')); seedClients(f); await f.session('wy');
  t.mock.method(crypto, 'randomBytes', () => { throw Error('synthetic entropy unavailable'); });
  syncBuiltinESMExports(); t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [base] }),
    { status: 'notSent', errorCode: 'INVALID_RESPONSE' });
  assert.equal(f.requests.length, 0); await f.close();
});

test('WY upload reports missing or non-array events as unsent request material, not a response failure', async () => {
  const f = fixture(() => assert.fail('unusable upload events cannot reach HTTP')); seedClients(f); await f.session('wy');
  for (const events of [undefined, null, 'events', {}, [null],
    [{ ...base, occurredAt: { toString: null } }], [{ ...base, event: 'end', reason: 'ui', rate: 'unknown' }]])
    assert.deepEqual(await f.request('wy', 'feedback.upload', events === undefined ? {} : { events }),
      { status: 'notSent', errorCode: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 0); await f.close();
});

test('default Node upload transport distinguishes actual refused connections from a reset after dispatch', async t => {
  let reset = false, received = 0;
  const peer = await nativeHTTP(t, (request, reply) => {
    received++;
    assert.equal(request.url, '/api/clientlog/encrypt/upload?multiupload=true');
    assert.equal(request.method, 'POST');
    const filename = /name="file"; filename="([^"]+)"/.exec(request.body.toString())[1];
    if (reset) { reply.socket.destroy(); return; }
    reply.end(JSON.stringify({ code: 200, data: { successfiles: [filename] } }));
  });
  const storage = fixture(() => assert.fail('storage setup cannot dispatch'));
  seedClients(storage); await storage.clientState();
  t.after(() => storage.close());
  const owner = createMusicPlatform({ dataRoot: storage.root, clock: () => now });
  try {
    await owner.sessionUpdate({ platform: 'wy', session: { scope: 'synthetic', generation: 1, userId: '42',
      cookie: 'MUSIC_U=synthetic-music-u; __csrf=synthetic-csrf' } });
    const upload = () => owner.request({ platform: 'wy', operation: 'feedback.upload', params: { events: [base] } });
    assert.deepEqual(await upload(), { status: 'confirmed', errorCode: null });
    reset = true;
    assert.deepEqual(await upload(), { status: 'unknown', errorCode: 'NETWORK_ERROR' });
    await peer.close();
    assert.deepEqual(await upload(), { status: 'notSent', errorCode: 'NETWORK_ERROR' });
    peer.check();
    assert.equal(received, 2); assert.equal(peer.requests.length, 3);
    for (const request of peer.requests) {
      assert.equal(request.url, 'https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true');
      assert.equal(request.closed, true); assert.equal(request.socketClosed, true);
    }
  } finally { await owner.close(); }
});

test('WY upload confirms only numeric 200 and the exact submitted filename; malformed ACK never retries', async () => {
  for (const make of [
    () => ({}), () => ({ code: true }), () => ({ code: '200' }), () => ({ code: 200.5 }),
    () => ({ code: 200 }), () => ({ code: 200, data: { successfiles: [] } }),
    () => ({ code: 200, data: { successfiles: ['other.log'] } }),
    file => ({ code: 200, data: { successfiles: [file + '.extra'] } }),
    () => ({ code: 200, data: { successfiles: [1] } }), () => 'not-json',
  ]) {
    const f = fixture(request => response(make(fileFrom(request).filename))); seedClients(f); await f.session('wy');
    assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [base] }), { status: 'unknown', errorCode: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1); await f.close();
  }
  for (const status of [201, 503]) {
    const f = fixture(request => response({ code: 200, data: { successfiles: [fileFrom(request).filename] } }, { status }));
    seedClients(f); await f.session('wy');
    assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [base] }),
      { status: 'unknown', errorCode: status === 201 ? 'INVALID_RESPONSE' : 'UPSTREAM_HTTP' });
    assert.equal(f.requests.length, 1); await f.close();
  }
  const refused = fixture(request => response({ code: 401, data: { successfiles: [fileFrom(request).filename] } }));
  seedClients(refused); await refused.session('wy');
  assert.deepEqual(await refused.request('wy', 'feedback.upload', { events: [base] }),
    { status: 'rejected', errorCode: 'UPSTREAM_ERROR' });
  assert.equal(refused.requests.length, 1); await refused.close();
  const missing = fixture(() => assert.fail('missing upload metadata cannot dispatch'));
  seedClients(missing); await missing.session('wy', '42', '__csrf=synthetic-csrf');
  assert.deepEqual(await missing.request('wy', 'feedback.upload', { events: [base] }),
    { status: 'notSent', errorCode: 'INVALID_CREDENTIALS' });
  assert.equal(missing.requests.length, 0); await missing.close();
});

test('WY upload preserves actual heartbeat/FM sources without fabricated cohorts', async t => {
  let draws;
  const f = fixture(request => {
    const file = fileFrom(request), decoded = inspect(file.bytes, draws.at(-2));
    const values = decoded.records.toString().trimEnd().split('\n').map(line => JSON.parse(line.split('\x01')[2]));
    assert.deepEqual(values.map(value => [value.source, value.sourceId, value.play_scene]), [
      ['userfm', undefined, undefined], [undefined, undefined, 'heartbeat'],
      ['upstream-source', 'actual-source-id', 'heartbeat'], [undefined, 'upstream-id-without-source', 'heartbeat'],
    ]);
    return response({ code: 200, data: { successfiles: [file.filename] } });
  }); seedClients(f); await f.session('wy');
  draws = entropy(t);
  const events = [
    ['fm', 'userfm', ''], ['heartbeat', '', ''], ['heartbeat', 'upstream-source', 'actual-source-id'],
    ['heartbeat', '', 'upstream-id-without-source'],
  ].map(([kind, source, sourceId]) => ({ ...base, target: { nativeId: '101', source, sourceId,
    algorithm: 'actual-alg', origin: { kind } } }));
  assert.equal((await f.request('wy', 'feedback.upload', { events })).status, 'confirmed'); await f.close();
});

test('WY upload only restores proven connection-before-send failures; timeout/reset/cancel after dispatch remain unknown', async () => {
  for (const [cause, status] of [['ENOTFOUND', 'notSent'], ['ECONNREFUSED', 'notSent'], ['EAI_AGAIN', 'notSent'],
    ['ECONNRESET', 'unknown'], ['ETIMEDOUT', 'unknown'], ['UND_ERR_CONNECT_TIMEOUT', 'unknown']]) {
    const f = fixture(() => { throw new TypeError('synthetic fetch failure', { cause: { code: cause } }); });
    seedClients(f); await f.session('wy');
    assert.deepEqual(await f.request('wy', 'feedback.upload', { events: [base] }), { status, errorCode: 'NETWORK_ERROR' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('WY cancelled unsent upload dispatches nothing; a dispatched upload/close waits for its true late confirmation', async t => {
  const held = gate(), controller = new AbortController();
  const f = fixture(async request => {
    const file = fileFrom(request); await held.wait();
    return response({ code: 200, data: { successfiles: [file.filename] } });
  }); seedClients(f); await f.session('wy');
  t.after(async () => { held.release(); await f.close(); });
  const unsentController = new AbortController();
  const unsent = f.request('wy', 'feedback.upload', { events: [base] }, { signal: unsentController.signal });
  unsentController.abort();
  assert.deepEqual(await unsent, { status: 'notSent', errorCode: 'CANCELLED' }); assert.equal(f.requests.length, 0);
  const work = f.request('wy', 'feedback.upload', { events: [base] }, { signal: controller.signal });
  await held.entered; controller.abort();
  let done = false; const close = f.close().then(() => { done = true; });
  await Promise.resolve(); assert.equal(done, false); assert.equal(f.requests[0].signal.aborted, false);
  held.release(); assert.deepEqual(await work, { status: 'confirmed', errorCode: null }); await close;
});
