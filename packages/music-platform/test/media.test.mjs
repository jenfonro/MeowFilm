import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, eapi, qq, kg, seedClients, wySong, txSong, kgSong, kgHash,
  krc, kuwo, xeapi, now, md5 } from './support.mjs';

test('standard audio uses actual guest resource facts without profile/rights or detail preflights', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      if (platform === 'wy') {
        const value = eapi(request); assert.equal(value.path, '/api/song/enhance/player/url');
        assert.equal(value.data.ids, '[123]'); assert.equal(value.data.br, 128000);
        assert.equal(value.data.header.MUSIC_U, undefined);
        return response({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/full.mp3', br: 128000, time: 240000, expi: 120 }] });
      }
      if (platform === 'tx') {
        const value = qq(request).req_0;
        assert.equal(value.method, 'UrlGetVkey'); assert.equal(value.param.uin, '0');
        assert.deepEqual(value.param.filename, ['M500midmid.mp3']);
        return rpc({ midurlinfo: [{ result: 0, purl: 'full.mp3' }], expiration: 600 });
      }
      const value = kg(request); assert.equal(value.path, '/v5/url'); assert.equal(value.fields.IsFreePart, '0');
      assert.equal(value.fields.userid, undefined);
      return response({ status: 1, url: ['https://audio.invalid/full.mp3'], is_free_part: 0, fileSize: 123 });
    }); seedClients(f);
    const result = await f.request(platform, 'audio', { id: platform === 'wy' ? '123' : platform === 'tx' ? 'mid' : kgHash });
    assert.equal(result.full, true); assert.equal(result.requestQuality, '128k'); assert.ok(result.url.startsWith('https://'));
    if (platform !== 'kg') assert.equal(result.expiresAt, now + 120000);
    assert.equal(f.requests.length, 1); await f.close();
  });
});

test('audio consumes quality arrays before dependencies and retains the existing null/default and single-quality semantics', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async st => {
    const id = platform === 'wy' ? '123' : platform === 'tx' ? 'mid' : kgHash;
    const invalid = fixture(() => assert.fail('unusable quality arrays must not dispatch metadata or audio'));
    st.after(() => invalid.close());
    for (const qualities of [{}, '128k', 42, { 0: '128k', length: 2 }, { toString: null },
      [null], [{ toString: null }], ['flac', { toString: null }]])
      await assert.rejects(invalid.request(platform, 'audio', { id, qualities }), { code: 'INVALID_REQUEST' });
    assert.equal(invalid.requests.length, 0); assert.equal(invalid.reads.length, 0); assert.equal(invalid.writes.length, 0);
    const f = fixture(request => {
      if (platform === 'wy') {
        assert.equal(eapi(request).path, '/api/song/enhance/player/url');
        return response({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/full', br: 128000 }] });
      }
      if (platform === 'tx') {
        assert.equal(qq(request).req_0.method, 'UrlGetVkey');
        return rpc({ midurlinfo: [{ result: 0, purl: 'full.mp3' }] });
      }
      assert.equal(kg(request).path, '/v5/url');
      return response({ status: 1, url: ['https://audio.invalid/full'], is_free_part: 0 });
    });
    seedClients(f); st.after(() => f.close());
    const original = await f.request(platform, 'audio', { id });
    for (const qualities of [null, ['128k']])
      assert.deepEqual(await f.request(platform, 'audio', { id, qualities, unused: { qualities: 'not-consumed' } }), original);
    assert.equal(original.full, true); assert.equal(original.requestQuality, '128k'); assert.equal(f.requests.length, 3);
  });
});

test('KG lyric duration conversion retains request checks while absent candidates produce no lyric', async t => {
  const invalid = fixture(() => assert.fail('unusable request duration must not dispatch lyric search'));
  t.after(() => invalid.close());
  for (const duration of ['not-a-number', '1e400', {}, { toString: null }])
    await assert.rejects(invalid.request('kg', 'lyrics', { id: kgHash, musicInfo: { duration } }), { code: 'INVALID_REQUEST' });
  assert.equal(invalid.requests.length, 0); assert.equal(invalid.reads.length, 0); assert.equal(invalid.writes.length, 0);
  const durations = [];
  const f = fixture(request => {
    const url = new URL(request.url);
    assert.equal(url.hostname, 'lyrics.kugou.com'); assert.equal(url.pathname, '/v1/search');
    assert.equal(url.searchParams.get('hash'), kgHash); durations.push(url.searchParams.get('duration'));
    return response({ status: 404 });
  });
  t.after(() => f.close());
  for (const duration of [undefined, null, '240.25'])
    assert.deepEqual(await f.request('kg', 'lyrics', { id: kgHash, musicInfo: { duration } }),
      { text: '', translation: '', romanization: '' });
  assert.deepEqual(durations, ['0', '0', '240250']); assert.equal(f.requests.length, 3);
  const damaged = fixture(() => response({ status: 200, error_code: 0, candidates: 'not-a-list' }));
  t.after(() => damaged.close());
  assert.deepEqual(await damaged.request('kg', 'lyrics', { id: kgHash, musicInfo: { duration: '240.25' } }),
    { text: '', translation: '', romanization: '' });
  assert.equal(damaged.requests.length, 1);
});

test('audio distinguishes explicit trials, no resource, real denial, parse failure and network failure', async t => {
  for (const [platform, body, code] of [
    ['wy', { code: 200, data: [{ code: 200, freeTrialInfo: { start: 0 }, url: 'https://audio.invalid/trial' }] }, 'TRIAL_ONLY'],
    ['wy', { code: 200, data: [{ code: 200, url: null }] }, 'MEDIA_UNAVAILABLE'],
    ['wy', { code: 200, data: [{ code: 500, url: null }] }, 'UPSTREAM_ERROR'],
    ['wy', { code: 200, data: 'not-a-list' }, 'INVALID_RESPONSE'],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ purl: 'trial.mp3', trial: 1 }] } } }, 'TRIAL_ONLY'],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ purl: 'trial.mp3', preview: true }] } } }, 'TRIAL_ONLY'],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ purl: 'full.mp3', result: '1' }] } } }, 'MEDIA_UNAVAILABLE'],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [] } } }, 'MEDIA_UNAVAILABLE'],
    ['kg', { status: 1, url: ['https://audio.invalid/trial'], is_free_part: 1 }, 'TRIAL_ONLY'],
    ['kg', { status: 1, url: [], is_free_part: 0 }, 'MEDIA_UNAVAILABLE'],
    ['kg', { status: 1, url: ['https://audio.invalid/unknown'] }, 'INVALID_RESPONSE'],
  ]) await t.test(platform + ':' + code, async () => {
    const f = fixture(() => response(body)); seedClients(f);
    await assert.rejects(f.request(platform, 'audio', { id: platform === 'wy' ? '123' : 'mid' }), { code });
    assert.equal(f.requests.length, 1); await f.close();
  });
  const f = fixture(() => { throw Error('synthetic network'); });
  await assert.rejects(f.request('wy', 'audio', { id: '123' }), { code: 'NETWORK_ERROR' });
  assert.equal(f.requests.length, 1); await f.close();
});

test('unusable consumed audio status or trial facts are parse failures, never invented resource conclusions', async t => {
  const full = 'https://audio.invalid/full.mp3';
  for (const [platform, body] of [
    ['wy', { code: 200, data: [{ code: {}, url: full, br: 128000 }] }],
    ['wy', { code: 200, data: [{ code: '1e400', url: full, br: 128000 }] }],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ result: {}, purl: full }] } } }],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ result: '1e400', purl: full }] } } }],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ trial: {}, purl: full }] } } }],
    ['tx', { code: 0, req_0: { code: 0, data: { midurlinfo: [{ preview: {}, purl: full }] } } }],
    ['kg', { status: 1, IsFreePart: 'not-a-flag', url: [full] }],
    ...['IsFreePart', 'privilege', 'pay_type', 'fail_process', 'status', 'code', 'errcode', 'error']
      .map(key => ['kg', { status: 1, data: { url: [full], IsFreePart: 0, trans_param: { [key]: {} } } }]),
  ]) await t.test(platform + ':' + JSON.stringify(body), async st => {
    const f = fixture(() => response(body)); seedClients(f); st.after(() => f.close());
    await assert.rejects(f.request(platform, 'audio', { id: platform === 'wy' ? '123' : platform === 'tx' ? 'mid' : kgHash }),
      { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1);
  });
  for (const flag of [false, 'false', 0, '0']) {
    const f = fixture(() => response({ status: 1, IsFreePart: flag, url: [full] }));
    seedClients(f); t.after(() => f.close());
    assert.equal((await f.request('kg', 'audio', { id: kgHash })).full, true);
    assert.equal(f.requests.length, 1);
  }
  for (const flag of [true, 'true', 1, '1']) {
    const f = fixture(() => response({ status: 1, IsFreePart: flag, url: [full] }));
    seedClients(f); t.after(() => f.close());
    await assert.rejects(f.request('kg', 'audio', { id: kgHash }), { code: 'TRIAL_ONLY' });
    assert.equal(f.requests.length, 1);
  }
});

test('optional media states retain real defaults, scalar success and QQ boolean non-trial facts', async t => {
  for (const [platform, variants] of [
    ['wy', [{ code: null }, { code: '200' }]],
    ['tx', [{ result: null, trial: null, preview: null }, { result: '0', trial: false, preview: '' },
      { trial: '0', preview: false }]],
  ]) await t.test(platform, async st => {
    let extra = {};
    const f = fixture(() => platform === 'wy' ?
      response({ code: 200, data: [{ url: 'https://audio.invalid/full.mp3', br: 128000, ...extra }] }) :
      rpc({ midurlinfo: [{ purl: 'full.mp3', ...extra }] }));
    seedClients(f); st.after(() => f.close());
    const id = platform === 'wy' ? '123' : 'mid';
    const original = await f.request(platform, 'audio', { id });
    assert.equal(original.full, true);
    for (const fields of variants) {
      extra = fields;
      assert.deepEqual(await f.request(platform, 'audio', { id }), original);
    }
    assert.equal(f.requests.length, variants.length + 1);
  });
});

test('quality selection uses real available material and XEAPI cryptography, not unrelated membership', async () => {
  const server = xeapi();
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/song/music/detail/get') return response({ code: 200, data: { sq: {} } });
    if (path === '/api/bsr/sk/get') return server.key(request);
    assert.equal(path, '/xeapi/song/enhance/player/url/v1');
    const params = server.open(request);
    assert.equal(params.get('ids'), '[123]'); assert.equal(params.get('level'), 'lossless');
    assert.equal(params.get('encodeType'), 'flac');
    return server.reply({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/lossless', level: 'lossless', time: 240000 }] });
  }); seedClients(f); await f.session('wy');
  const result = await f.request('wy', 'audio', { id: '123', qualities: ['master', 'flac', '128k'] });
  assert.equal(result.requestQuality, 'flac'); assert.equal(result.actualQuality, 'flac'); assert.equal(result.duration, 240);
  assert.equal(result.qualityVerified, true);
  assert.equal(f.requests.length, 3); await f.close();
});

test('WY actual delivered quality remains separate from the requested high tier, and unknown facts stay unverified', async () => {
  const server = xeapi();
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/song/music/detail/get') return response({ code: 200, data: { h: { size: 1 } } });
    if (path === '/api/bsr/sk/get') return server.key(request);
    assert.equal(server.open(request).get('level'), 'exhigh');
    return server.reply({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/delivered.mp3', level: 'standard' }] });
  }); seedClients(f);
  const result = await f.request('wy', 'audio', { id: '123', qualities: ['320k'] });
  assert.equal(result.requestQuality, '320k'); assert.equal(result.actualQuality, '128k');
  assert.equal(result.qualityVerified, true); assert.equal(f.requests.length, 3); await f.close();
  const unknown = fixture(() => response({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/full' }] }));
  const standard = await unknown.request('wy', 'audio', { id: '123' });
  assert.equal(standard.actualQuality, ''); assert.equal(standard.qualityVerified, false); await unknown.close();
});

test('audio options retain existing per-platform availability recipes', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => platform === 'tx' ? rpc({ tracks: [txSong('mid', { file: { size_flac: 20, size_new: [30] } })] }) :
        response({ status: 1, data: [kgSong(kgHash, { SQFileHash: 'flac-hash', SQFileSize: 20 })] }));
    seedClients(f);
    if (platform === 'wy') {
      assert.deepEqual(await f.request('wy', 'audio.options', { id: '123' }), { qualities: ['128k'] });
      assert.equal(f.requests.length, 0); await f.session('wy');
    }
    const result = await f.request(platform, 'audio.options', { id: platform === 'wy' ? '123' : 'mid' });
    assert.deepEqual(result.qualities, platform === 'wy' ?
      ['128k', '192k', '320k', 'flac', 'flac24bit', 'master', 'jyeffect', 'dolby', 'sky', 'vivid'] :
      platform === 'tx' ? ['128k', 'flac', 'master'] : ['128k', 'flac']);
    assert.equal(f.requests.length, platform === 'wy' ? 0 : 1); await f.close();
  });
});

test('WY signed-in standard options and resolution never depend on membership or high-tier resource capabilities', async () => {
  const f = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/song/enhance/player/url');
    assert.equal(data.br, 128000); assert.equal(data.header.MUSIC_U, 'synthetic-music-u');
    return response({ code: 200, data: [{ url: 'https://audio.invalid/full-standard', code: 200, br: 128000 }] });
  }); seedClients(f); await f.session('wy');
  assert.ok((await f.request('wy', 'audio.options', { id: '123' })).qualities.includes('vivid'));
  assert.equal(f.requests.length, 0);
  const result = await f.request('wy', 'audio', { id: '123', qualities: ['128k', 'flac'] });
  assert.equal(result.requestQuality, '128k'); assert.equal(result.full, true);
  assert.equal(f.requests.length, 1); await f.close();
});

test('KG exact quality resources retain their own tracker hash, key and album locators without membership reads', async () => {
  const hashes = { '128k': kgHash, '320k': '1'.repeat(32), flac: '2'.repeat(32), flac24bit: '3'.repeat(32) };
  const levels = { '128k': '128', '320k': '320', flac: 'flac', flac24bit: 'high' };
  const f = fixture(request => {
    const value = kg(request);
    if (value.path === '/v1/audio/audio') return response({ status: 1, data: [kgSong(kgHash, {
      album_id: 11, mixsongid: 22, filesize: 100, hash_320: hashes['320k'], filesize_320: 200,
      hash_flac: hashes.flac, filesize_flac: 300, hash_high: hashes.flac24bit, filesize_high: 400,
    })] });
    assert.equal(value.path, '/v5/url');
    const quality = Object.keys(levels).find(key => levels[key] === value.fields.quality);
    assert.equal(value.fields.hash, hashes[quality].toLowerCase());
    assert.equal(value.fields.key, md5(hashes[quality].toLowerCase() +
      '57ae12eb6890223e355ccfcb74edf70d1005' + '176905054734845483393488264506404844848' + '42'));
    assert.equal(value.fields.album_id, '11'); assert.equal(value.fields.album_audio_id, '22');
    assert.equal(value.fields.behavior, 'play'); assert.equal(value.fields.IsFreePart, '0');
    assert.equal(value.fields.clientver, '11430'); assert.equal(value.fields.version, '11430');
    return response({ status: 1, url: ['https://fs.kugou.com/current.flac'], IsFreePart: 0, fileSize: 999 });
  }); seedClients(f); await f.session('kg');
  const info = (await f.request('kg', 'song', { id: kgHash })).sources[0].musicInfo;
  for (const quality of Object.keys(hashes)) {
    const result = await f.request('kg', 'audio', { id: kgHash, quality, musicInfo: info });
    assert.equal(result.requestQuality, quality); assert.equal(result.qualityVerified, false);
    assert.equal(result.expiresAt, null); assert.equal(result.full, true);
    assert.equal(result.size, 999); assert.deepEqual(result.headers, {});
  }
  assert.equal(f.requests.length, 5); await f.close();
});

test('KG unsupported quality and absent exact quality never dispatch a downgraded tracker', async () => {
  const f = fixture(() => response({ status: 1, data: [kgSong()] })); seedClients(f);
  await assert.rejects(f.request('kg', 'audio', { id: kgHash, quality: 'master' }), { code: 'UNSUPPORTED' });
  assert.equal(f.requests.length, 0);
  await assert.rejects(f.request('kg', 'audio', { id: kgHash, quality: 'flac' }), { code: 'QUALITY_UNAVAILABLE' });
  await assert.rejects(f.request('kg', 'audio', { id: kgHash, qualities: ['flac'] }), { code: 'QUALITY_UNAVAILABLE' });
  assert.deepEqual(f.requests.map(request => new URL(request.url).pathname), ['/v1/audio/audio', '/v1/audio/audio']);
  await f.close();
});

test('KG single requested high quality resolves its exact resource instead of silently choosing standard', async () => {
  const high = '2'.repeat(32);
  const f = fixture(request => {
    const row = kg(request);
    if (row.path === '/v1/audio/audio')
      return response({ status: 1, data: [kgSong(kgHash, { hash_flac: high, filesize_flac: 200 })] });
    assert.equal(row.path, '/v5/url'); assert.equal(row.fields.hash, high); assert.equal(row.fields.quality, 'flac');
    return response({ status: 1, url: ['https://audio.invalid/high.flac'], is_free_part: 0 });
  }); seedClients(f);
  const result = await f.request('kg', 'audio', { id: kgHash, qualities: ['flac'] });
  assert.equal(result.requestQuality, 'flac'); assert.equal(f.requests.length, 2); await f.close();
});

test('KG full tracker resources ignore unconsumed trial and privilege fields', async t => {
  for (const fields of [
    { freeTrialInfo: null },
    { freePart: {} },
    { priv_status: 0 },
    { freeTrialInfo: 0, freePart: null, priv_status: null },
  ]) await t.test(JSON.stringify(fields), async st => {
    let extra = {};
    const f = fixture(request => {
      const value = kg(request);
      assert.equal(value.path, '/v5/url'); assert.equal(value.fields.IsFreePart, '0');
      return response({ status: 1, ...extra, data: {
        url: ['https://fs.kugou.com/full.mp3'], IsFreePart: 0, fileSize: 299, trans_param: extra,
      } });
    }); seedClients(f); st.after(() => f.close());
    const original = await f.request('kg', 'audio', { id: kgHash });
    assert.equal(original.full, true); assert.equal(original.url, 'https://fs.kugou.com/full.mp3'); assert.equal(original.size, 299);
    extra = fields;
    assert.deepEqual(await f.request('kg', 'audio', { id: kgHash }), original);
    assert.equal(f.requests.length, 2);
  });
});

test('KG nested tracker permission, trial and business facts cannot be hidden by a successful outer envelope', async t => {
  for (const [fields, code] of [
    [{ trans_param: { free_part: 1 } }, 'TRIAL_ONLY'],
    [{ trans_param: { privilege: 1 } }, 'MEDIA_UNAVAILABLE'],
    [{ trans_param: { pay_type: 1 } }, 'MEDIA_UNAVAILABLE'],
    [{ trans_param: { fail_process: 1 } }, 'MEDIA_UNAVAILABLE'],
    [{ trans_param: { code: 403 } }, 'UPSTREAM_ERROR'],
    [{ trans_param: { status: 0 } }, 'UPSTREAM_ERROR'],
    [{ trans_param: { errcode: 7 } }, 'UPSTREAM_ERROR'],
  ]) await t.test(JSON.stringify(fields), async () => {
    const f = fixture(() => response({ status: 1, data: {
      url: ['https://fs.kugou.com/full.mp3'], IsFreePart: 0, ...fields,
    } })); seedClients(f);
    await assert.rejects(f.request('kg', 'audio', { id: kgHash }), { code });
    assert.equal(f.requests.length, 1); await f.close();
  });
});

test('actual media URLs and consumed facts are not compared to obsolete identity, size, host or unused backup proofs', async () => {
  const f = fixture(() => response({ status: 1, url: ['http://127.0.0.1:8443/live?a=1#part', 'not-a-consumed-url'],
    IsFreePart: 0, hash: 'new-canonical-hash', album_id: 999, album_audio_id: 999, fileSize: 299,
    data: { url: 'not-a-consumed-url' } })); seedClients(f);
  const value = await f.request('kg', 'audio', { id: kgHash, musicInfo: { albumId: 'old', duration: 240 } });
  assert.equal(value.url, 'http://127.0.0.1:8443/live?a=1#part'); assert.equal(value.size, 299);
  assert.equal(value.full, true); assert.equal(f.requests.length, 1); await f.close();
  const wy = fixture(() => response({ code: 200, data: [{ code: 200,
    url: 'http://m7.music.126.net/a%2Fb.mp3?signature=x%2By&key=keep' }] }));
  assert.equal((await wy.request('wy', 'audio', { id: '123' })).url,
    'https://m7.music.126.net/a%2Fb.mp3?signature=x%2By&key=keep');
  await wy.close();
});

test('account credentials and encoded reflections never escape through consumed media or public metadata', async t => {
  for (const platform of ['wy', 'tx', 'kg']) for (const encode of [
    value => value, value => value.replaceAll('-', '%2D'), value => value.replaceAll('-', '%252D'),
  ]) await t.test(platform + ':' + encode('synthetic-token'), async () => {
    const secret = { wy: 'synthetic-music-u', tx: 'synthetic-key', kg: 'synthetic-token' }[platform];
    let reflect = true;
    const f = fixture(() => {
      const url = 'https://audio.invalid/a?value=' + encode(secret);
      const unused = { secret, nested: [secret, { arbitrary: true }] };
      return platform === 'wy' ? response({ code: 200, data: [{ code: 200,
        url: reflect ? url : 'https://audio.invalid/full' }], unused }) :
        platform === 'tx' ? rpc({ midurlinfo: [{ purl: reflect ? url : 'full.mp3' }], unused }) :
          response({ status: 1, IsFreePart: 0, url: [reflect ? url : 'https://audio.invalid/full'], unused });
    }); seedClients(f); await f.session(platform);
    await assert.rejects(f.request(platform, 'audio', { id: platform === 'wy' ? '123' : kgHash }),
      { code: 'INVALID_RESPONSE' });
    reflect = false;
    assert.equal((await f.request(platform, 'audio', { id: platform === 'wy' ? '123' : kgHash })).full, true);
    assert.equal(f.requests.length, 2); await f.close();
  });
  for (const address of ['file:///tmp/audio', 'https://user:password@audio.invalid/a']) {
    const f = fixture(() => response({ code: 200, data: [{ url: address }] }));
    await assert.rejects(f.request('wy', 'audio', { id: '123' }), { code: 'INVALID_RESPONSE' });
    await f.close();
  }
});

test('five public lyric recipes preserve main text, word clocks and optional tracks without metadata proof reads', async t => {
  const qrc = '0c8d67dd3e549974b64ed2680459f13881aa15d10db4cc8324b86311d0d741bd6af5d8724f2b7571b2b2bf976be395e454a23ccb367e464b537e1be65e411da3f6e6b01aadcc5e634b317c352096ebbac83f8b1415b90ff5acdb443265324bbb5a5da122957dca8a91256a421f7fd9a27f649bd821b4fa3926e993f67c6fad7cbc377509b75144089a1e82144e798461';
  const translated = '32dabb4c5e9846faf8ad6a456c3eb58233c06a98fd7f551a855b6cacf6ffb3778cde8f3bc607e201';
  const mrc = '23345d6d70eb4614dd6d67fe21e85fa279918709c10d9fbc24fc3652996322208d46cdd8b9ea9cd84d689a7ef80b5230694439ca54dffd6bf1deb48135f7449880b41f63ad753fdabd334ae3abb54e65e21f4661891b80ff58cd20b90f5d4fe4e3e82bc7fe3eb10ac4ebf81edaed3972';
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (platform === 'wy') {
        const value = eapi(request); assert.equal(value.path, '/api/song/lyric/v1');
        assert.equal(value.data.yv, 0); assert.equal(value.data.cp, false);
        return response({ code: 200, id: 'different-unconsumed-id', yrc: { lyric: '[1000,800](1000,300,0)你(1300,500,0)好' },
          lrc: { lyric: '[00:04]wrong clock' }, ytlrc: { lyric: '[1000,800]Hello' } });
      }
      if (platform === 'tx') {
        const call = qq(request).req_0;
        assert.equal(call.method, 'GetPlayLyricInfo'); assert.equal(call.param.songMid, '123');
        assert.equal(call.param.songId, undefined);
        return rpc({ lyric: qrc, trans: translated, songID: 'actual-unconsumed-id' });
      }
      if (platform === 'kg') return response(path === '/v1/search' ?
        { status: 200, candidates: [{ id: 17, accesskey: 'synthetic-access' }, { id: 18, accesskey: 'other-access' }] } :
        { status: 200, error_code: 0, content: krc('[offset:100]\n[1000,800]<0,300,0>你<300,500,0>好') });
      if (platform === 'kw') return response(kuwo('[offset:-100]\n[00:01.000]<300,-300>你<800,-200>好'));
      if (path === '/MIGUM2.0/v1.0/content/resourceinfo.do') return response({ code: '000000',
        data: { resource: [{ mrcUrl: 'https://lyrics.invalid/current.mrc' }] } });
      assert.equal(request.headers.Cookie, undefined); return response(mrc);
    }); seedClients(f);
    const result = await f.request(platform, 'lyrics', { id: '123', musicInfo: { duration: 240 } });
    if (platform === 'wy') {
      assert.equal(result.text, '[1000,800](1000,300,0)你(1300,500,0)好'); assert.equal(result.translation, '[1000,800]Hello');
    } else if (platform === 'tx') {
      assert.equal(result.text, '[1000,900]喵(1000,300)音乐(1300,600)\n');
      assert.equal(result.translation, '[00:01.00]Synthetic translation\n');
    } else if (platform === 'kg') assert.equal(result.text, '[offset:100]\n[1000,800](1000,300,0)你(1300,500,0)好');
    else if (platform === 'kw') assert.equal(result.text, '[offset:-100]\n[1000,800](1000,300,0)你(1300,500,0)好');
    else assert.equal(result.text, '[1000,800](1000,300,0)你(1300,500,0)好\n[2000,500](2000,500,0)世界');
    assert.equal(result.romanization, '');
    assert.equal(f.requests.length, ['kg', 'mg'].includes(platform) ? 2 : 1);
    await f.close();
  });
});

test('lyrics retain real decryption and decompression failures and preserve an independent main track', async () => {
  const emptyOptional = fixture(() => rpc({ lyric: '[00:01]main' })); seedClients(emptyOptional);
  assert.deepEqual(await emptyOptional.request('tx', 'lyrics', { id: 'mid' }),
    { text: '[00:01]main', translation: '', romanization: '' }); await emptyOptional.close();
  const damaged = fixture(() => rpc({ lyric: 'not-hex' })); seedClients(damaged);
  await assert.rejects(damaged.request('tx', 'lyrics', { id: 'mid' }), { code: 'INVALID_RESPONSE' }); await damaged.close();
  const bomb = fixture(request => response(new URL(request.url).pathname === '/v1/search' ?
    { status: 200, candidates: [{ id: 1, accesskey: 'synthetic' }] } :
    { status: 200, error_code: 0, content: krc('[1000,300]' + 'x'.repeat(512 * 1024 + 1)) }));
  assert.equal((await bomb.request('kg', 'lyrics', { id: kgHash })).text, '[1000,300]' + 'x'.repeat(512 * 1024 + 1)); await bomb.close();
});

test('chorus consumes actual timing only; absent and unrelated fields do not block', async t => {
  for (const platform of ['wy', 'kg', 'tx']) await t.test(platform, async () => {
    let point = true;
    const f = fixture(() => response(platform === 'wy' ?
      { code: 200, chorus: point ? [{ songId: 'unconsumed', startTime: 30000, endTime: 45000 }] : [] } :
      { status: 1, data: point ? [{ start_time: 30000, end_time: 45000 }] : [] }));
    const result = await f.request(platform, 'chorus', { id: '123' });
    assert.deepEqual(result.point, platform === 'tx' ? null : { start: 30, end: 45 });
    point = false;
    assert.equal((await f.request(platform, 'chorus', { id: '123' })).point, null);
    assert.equal(f.requests.length, platform === 'tx' ? 0 : 2); await f.close();
  });
});
