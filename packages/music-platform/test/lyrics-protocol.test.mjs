import test from 'node:test';
import assert from 'node:assert/strict';
import { deflateSync } from 'node:zlib';
import { fixture, response, rpc, kgHash, seedClients, krc, kuwo, md5, gate } from './support.mjs';

// HEAD Windows MusicNativeLyricsTests and Swift NativeService Lyrics tests.
// All codecs are exercised through factory/request + actual byte transport.
function kgFixture(content) {
  const f = fixture(request => {
    const url = new URL(request.url), query = Object.fromEntries(url.searchParams), signature = query.signature;
    delete query.signature;
    assert.equal(signature, md5('OIlwieks28dk2k092lksi2UIkp' +
      Object.keys(query).sort().map(key => key + '=' + query[key]).join('') + 'OIlwieks28dk2k092lksi2UIkp'));
    assert.equal(request.headers.Cookie, undefined);
    for (const key of ['token', 'userid', 'mid', 'dfid']) assert.equal(query[key], undefined);
    if (url.pathname === '/v1/search') {
      assert.equal(query.hash, kgHash); assert.equal(query.duration, '180000'); assert.equal(query.album_audio_id, '42');
      return response({ status: 200, candidates: [{ id: 17, accesskey: 'actual+&/key', hash: 'unused-other-hash' }] });
    }
    assert.equal(url.pathname, '/download'); assert.equal(query.id, '17'); assert.equal(query.accesskey, 'actual+&/key');
    assert.equal(query.fmt, 'krc'); assert.equal(query.charset, 'utf8'); assert.equal(query.client, 'android');
    return response({ status: 200, error_code: 0, content });
  }); seedClients(f); return f;
}
const readKg = f => f.request('kg', 'lyrics', { id: kgHash.toLowerCase(), musicInfo: { duration: 180, albumAudioId: '42' } });
const language = rows => Buffer.from(JSON.stringify({ version: 1, content: [{ type: 1, lyricContent: rows }] })).toString('base64');
function wrapKrc(bytes) {
  const key = Buffer.from([64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105]);
  return Buffer.concat([Buffer.from('krc1'), Buffer.from(bytes.map((value, index) => value ^ key[index % key.length]))]).toString('base64');
}

test('anonymous KG lyric recipes keep real word clocks, blank alignment rows, offset and optional language without candidate proof', async () => {
  const f = kgFixture(krc('[offset:-250]\n[language:' + language([['译一'], [''], ['译二']]) +
    ']\n[1000,500]<0,250,0>甲<250,250,0>乙\n[1750,0]\n[2000,300]<0,300,0>丙'));
  await f.session('kg');
  assert.deepEqual(await readKg(f), {
    text: '[offset:-250]\n[1000,500](1000,250,0)甲(1250,250,0)乙\n[1750,0]\n[2000,300](2000,300,0)丙',
    translation: '[offset:-250]\n[00:01.000]译一\n[00:02.000]译二', romanization: '',
  });
  assert.equal(f.requests.length, 2); await f.close();
});

test('KRC malformed optional language and ambiguous effective clocks suppress only translation', async () => {
  for (const tag of ['eA==', language([['too'], ['many']]), language([['[00:01]injected']])]) {
    const f = kgFixture(krc('[language:' + tag + ']\n[1000,300]<0,300,0>原'));
    assert.deepEqual(await readKg(f), { text: '[1000,300](1000,300,0)原', translation: '', romanization: '' });
    await f.close();
  }
  for (const prefix of ['[offset:-2000]', '[offset:0]\n[offset:0]']) {
    const f = kgFixture(krc(prefix + '\n[language:' + language([['一'], ['二']]) + ']\n[1000,100]甲\n[2000,100]乙'));
    const result = await readKg(f);
    assert.ok(result.text.includes('[1000,100]甲')); assert.equal(result.translation, ''); await f.close();
  }
});

test('KRC checksum, UTF8, truncated, trailing and concatenated streams fail; malformed timing never becomes empty success', async () => {
  const compressed = deflateSync(Buffer.from('[1000,300]甲')), checksum = Buffer.from(compressed);
  checksum[checksum.length - 1] ^= 1;
  const header = Buffer.from(krc('[1000,300]甲'), 'base64'); header[0] = 0;
  const fixtures = [header.toString('base64'), wrapKrc(checksum), wrapKrc(compressed.subarray(0, -2)),
    wrapKrc(Buffer.concat([compressed, Buffer.from([0, 0])])), wrapKrc(Buffer.concat([compressed, compressed])),
    wrapKrc(deflateSync(Buffer.from([255, 255]))), 'invalid-base64',
    ...['[9007199254740991,1]a', '[1000,300]prefix<0,300,0>a',
      '[1000,300]<0,300,0>a(1,2,3)', '[1,broken]a'].map(krc)];
  for (const content of fixtures) {
    const f = kgFixture(content);
    await assert.rejects(readKg(f), { code: 'INVALID_RESPONSE' }); assert.equal(f.requests.length, 2); await f.close();
  }
  for (const code of [20018, 20028]) {
    const f = fixture(() => response({ status: 0, errcode: code }));
    await assert.rejects(readKg(f), { code: code === 20018 ? 'AUTH_REQUIRED' : 'VERIFICATION_REQUIRED' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('KG required lyric and chorus status conversion fails at consumption without inventing an upstream denial', async t => {
  for (const body of [{ error_code: 0, candidates: [] }, { status: {}, candidates: [] }]) {
    const f = fixture(() => response(body)); t.after(() => f.close());
    await assert.rejects(readKg(f), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1);
  }
  for (const body of [{ status: 200 }, { error_code: 0 }, { status: 200, error_code: {} }]) {
    const f = fixture(request => response(new URL(request.url).pathname === '/v1/search' ?
      { status: 200, candidates: [{ id: 17, accesskey: 'synthetic-key' }] } : body));
    t.after(() => f.close());
    await assert.rejects(readKg(f), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 2);
  }
  for (const body of [{ data: [] }, { status: {}, data: [] }]) {
    const f = fixture(() => response(body)); t.after(() => f.close());
    await assert.rejects(f.request('kg', 'chorus', { id: kgHash }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1);
  }
});

test('QQ actual lyric fields reject XML declarations, duplicate content, malformed cipher and damaged optional tracks without executing data', async () => {
  for (const lyric of ['01', '0'.repeat(16), 'WzAwOjAxXXg=',
    '<x LyricContent="[00:01]a" LyricContent="[00:01]b"/>', '<!DOCTYPE x><x LyricContent="[00:01]x"/>']) {
    const f = fixture(() => rpc({ lyric })); seedClients(f);
    await assert.rejects(f.request('tx', 'lyrics', { id: 'mid' }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1); await f.close();
  }
  const damaged = fixture(() => rpc({ lyric: '[00:01]main', trans: 'not-hex' })); seedClients(damaged);
  await assert.rejects(damaged.request('tx', 'lyrics', { id: 'mid' }), { code: 'INVALID_RESPONSE' }); await damaged.close();
  const xml = fixture(() => rpc({ lyric: '<x LyricContent="[00:01]A &amp; B"/>', roma: '[00:01]yuan', qrc_t: 999999 }));
  seedClients(xml);
  assert.deepEqual(await xml.request('tx', 'lyrics', { id: 'mid' }),
    { text: '[00:01]A & B', translation: '', romanization: '[00:01]yuan' }); await xml.close();
});

test('Kuwo independent zlib/XOR golden, plain entities, duplicate translation and overlap retain fixed semantics', async () => {
  const golden = Buffer.from('dHA9Y29udGVudA0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluDQoNCnicFczBCoIwHAfgB+riHwrcwcNG0xxqOduPsVtqs3TgIWjl00ffA3zlQ9Z4pkU+JW0el8RdTSdFeTR0GFWR7nhM1UlHbYmc3ZrtEjAYYh4E1EBvl6CAcjXEKgDcgt31d3w3FDxksB19XD2vleAvL9r9TfK1crPydmmARP3PHIRBg7XnKct++kAuXg==', 'base64');
  const vectors = [
    [golden, '[offset:100]\n[1000,1200](1200,200,0)A(1800,400,0)B\n[2000,200](2100,100,0)C',
      '[offset:100]\n[00:01.000]Translation'],
    [kuwo('[00:01.00]A &amp; B\n[00:02.00]译文\n[00:02.00]next'), '[00:01.000]A & B\n[00:02.000]next', '[00:01.000]译文'],
    [kuwo('[00:01.00]&amp;lt;'), '[00:01.000]&lt;', ''],
    [kuwo('[kuwo:13]\n[00:01.00]<800,-400>A<600,200>B'), '[1000,600](1200,200,0)A(1400,200,0)B', ''],
  ];
  for (const [bytes, text, translation] of vectors) {
    const f = fixture(request => {
      const url = new URL(request.url); assert.equal(url.origin + url.pathname, 'https://mlyric.kuwo.cn/mobi.s');
      assert.equal(url.searchParams.get('rid'), '9007199254740993');
      assert.equal(request.headers.Cookie, undefined); return response(bytes);
    });
    assert.deepEqual(await f.request('kw', 'lyrics', { id: '9007199254740993' }), { text, translation, romanization: '' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('Kuwo rejects malformed factor/clock/word conversion and invalid envelopes without retrying', async () => {
  const good = kuwo('[00:01.00]A'), separator = Buffer.from('tp=content\r\n\r\n');
  const payloads = ['', '<html>denied</html>', '[kuwo:0]\n[00:01.00]<2,0>A',
    '[kuwo:invalid]\n[00:01.00]<2,0>A', '[00:70.00]A', '[00:01.00]<999999999999999999999,0>A',
    '[offset:1]\n[offset:2]\n[00:01.00]A'].map(kuwo);
  payloads.push(Buffer.alloc(0), good.subarray(0, -1), Buffer.concat([good, Buffer.from([0])]),
    Buffer.concat([good, good.subarray(separator.length)]),
    ...['eJzLyy/RTUosTjUzUQQAGIkDpQ==', 'eJzLSLe1BQADkAFK'].map(raw => Buffer.concat([separator, Buffer.from(raw, 'base64')])));
  for (const bytes of payloads) {
    const f = fixture(() => response(bytes));
    await assert.rejects(f.request('kw', 'lyrics', { id: '123' }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

// Independent forward signed-64-bit corrected-block TEA encoder. The product
// only decrypts. This permits malformed *plaintext* vectors through real HTTP,
// without exposing a raw codec or relying on random bad ciphertext.
function mrc(value) {
  const bytes = Buffer.from(value, 'utf16le'), padded = Buffer.alloc(Math.max(16, Math.ceil(bytes.length / 8) * 8));
  bytes.copy(padded);
  const blocks = Array.from({ length: padded.length / 8 }, (_, index) => padded.readBigInt64LE(index * 8));
  const key = [27303562373562475n, 18014862372307051n, 22799692160172081n, 34058940340699235n];
  const long = value => BigInt.asIntN(64, value), rounds = 6 + Math.floor(52 / blocks.length);
  let sum = 0n, z = blocks.at(-1);
  for (let round = 0; round < rounds; round++) {
    sum += 2654435769n;
    for (let at = 0; at < blocks.length; at++) {
      const y = blocks[(at + 1) % blocks.length];
      const mixed = long((y ^ sum) + (z ^ key[(at & 3) ^ Number(sum >> 2n & 3n)])) ^
        long(((z >> 5n) ^ long(y << 2n)) + ((y >> 3n) ^ long(z << 4n)));
      blocks[at] = long(blocks[at] + mixed); z = blocks[at];
    }
  }
  return blocks.map(value => BigInt.asUintN(64, value).toString(16).padStart(16, '0')).join('');
}
function mgFixture(content) {
  return fixture(request => {
    const url = new URL(request.url);
    if (url.pathname.endsWith('/resourceinfo.do')) {
      assert.equal(new URLSearchParams(request.body).get('resourceId'), '123');
      return response({ code: '000000', data: { resource: [{ songId: 'unconsumed-alias',
        mrcUrl: 'http://lyrics.invalid/current.mrc', lrcUrl: 'https://lyrics.invalid/never-fallback.lrc',
        trcUrl: 'https://lyrics.invalid/current.trc' }] } });
    }
    assert.equal(url.protocol, 'https:'); assert.equal(request.headers.Cookie, undefined);
    return response(url.pathname.endsWith('.mrc') ? content : '[00:01.000]Translation');
  });
}
test('MRC validates actual UTF16 and word clocks through encrypted semantic input, never falling back or fetching translation after failure', async () => {
  const plain = '[1000,800]你(1000,300)好(1300,500)\n[2000,500]世界(2000,500)';
  const golden = '23345d6d70eb4614dd6d67fe21e85fa279918709c10d9fbc24fc3652996322208d46cdd8b9ea9cd84d689a7ef80b5230694439ca54dffd6bf1deb48135f7449880b41f63ad753fdabd334ae3abb54e65e21f4661891b80ff58cd20b90f5d4fe4e3e82bc7fe3eb10ac4ebf81edaed3972';
  // Independent fixed ciphertext remains the positive oracle for the encoder.
  const good = mgFixture(golden);
  const result = await good.request('mg', 'lyrics', { id: '123', musicInfo: { mrcUrl: 'https://never.invalid/' } });
  assert.equal(result.text, '[1000,800](1000,300,0)你(1300,500,0)好\n[2000,500](2000,500,0)世界');
  assert.equal(result.translation, '[00:01.000]Translation'); assert.equal(good.requests.length, 3); await good.close();
  const encoded = mgFixture(mrc(plain));
  assert.deepEqual(await encoded.request('mg', 'lyrics', { id: '123' }), result); await encoded.close();
  for (const content of ['', 'invalid', 'x'.repeat(32), golden.slice(0, -1),
    ...['[1000,100]A(999,100)', '[1000,100]A(1000,101)', '[1000,100]A(1000,100)tail',
      '[1000,100]A(99999999999999999999,100)', '[1000,100]A(1000,100)\0X',
      '[1000,100]\ud800(1000,100)', '<html>denied</html>'].map(mrc)]) {
    const f = mgFixture(content);
    await assert.rejects(f.request('mg', 'lyrics', { id: '123' }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 2, 'bad MRC neither retries LRC nor fetches optional translation'); await f.close();
  }
});

test('MG resource cancellation or account publication physically retires the hop before fetching any lyric file', async t => {
  for (const cancel of [false, true]) await t.test(String(cancel), async t => {
    const held = gate(), controller = new AbortController();
    const f = fixture(async () => { await held.wait();
      return response({ code: '000000', data: { resource: [{ lrcUrl: 'https://never.invalid/file' }] } });
    });
    t.after(async () => { held.release(); await f.close(); });
    const work = f.request('mg', 'lyrics', { id: '123' }, { signal: controller.signal }); work.catch(() => {});
    await held.entered;
    let publication;
    if (cancel) controller.abort(); else publication = f.owner.sessionUpdate({ platform: 'mg', session: null });
    assert.equal(f.requests[0].signal.aborted, true); held.release();
    await assert.rejects(work, { code: cancel ? 'CANCELLED' : 'STALE_SESSION' });
    await publication; assert.equal(f.requests.length, 1);
  });
});
