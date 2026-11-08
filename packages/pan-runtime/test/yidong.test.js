import test from 'node:test';
import assert from 'node:assert/strict';
import crypto, { createCipheriv, createDecipheriv, createHash, webcrypto } from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { yidong } from '../src/providers/yidong.js';
import { PanError } from '../src/errors.js';
import { createTransport } from '../src/http.js';

const key = Buffer.from('PVGDwmcvfs1uV3d1');
const iv = Buffer.from('000102030405060708090a0b0c0d0e0f', 'hex');
const instant = Date.UTC(2026, 0, 1, 16, 2, 3);
const linkID = 'ShareFixture123';
const largeID = '900719925474099312345';
const sampleName = '原名 #$* 中文.MKV';
const makeFile = patch => ({
  kind: 'share', shareId: linkID, password: '', coID: largeID,
  name: sampleName, path: `/${sampleName}`, ...patch,
});
const identity = 'WebClient:AccountFixture:TokenPart:Tail';
const credentials = { authorization: Buffer.from(identity).toString('base64'), refreshedAt: instant };
const co = (id, patch = {}) => ({ coID: String(id), coType: 3, coName: `文件 ${id}.mp4`, ...patch });
const empty = () => ({ code: 0, data: { caLst: [], coLst: [] } });
const jsonReply = (value, status = 200) => ({ status, body: JSON.stringify(value) });
function cipherReply(value, quoted = true) {
  const cipher = createCipheriv('aes-128-cbc', key, iv);
  const plain = typeof value === 'string' ? value : JSON.stringify(value);
  const encrypted = Buffer.concat([iv, cipher.update(plain), cipher.final()]).toString('base64');
  return { status: 200, body: quoted ? JSON.stringify(encrypted) : encrypted };
}
function plainRequest(request) {
  const encoded = JSON.parse(request.body);
  assert.equal(typeof encoded, 'string', 'OutLink encrypted body must be a JSON string');
  const bytes = Buffer.from(encoded, 'base64');
  assert.ok(bytes.length >= 32 && bytes.length % 16 === 0);
  const decipher = createDecipheriv('aes-128-cbc', key, bytes.subarray(0, 16));
  return Buffer.concat([decipher.update(bytes.subarray(16)), decipher.final()]).toString();
}
const encryptedRequest = request => JSON.parse(plainRequest(request));
function signatureFromProtocol(plain, timestamp, nonce) {
  const digest = value => createHash('md5').update(value).digest('hex');
  const encoded = [...encodeURIComponent(plain)].sort().join('');
  return digest(digest(Buffer.from(encoded).toString('base64')) + digest(`${timestamp}:${nonce}`)).toUpperCase();
}
function assertAnonymous(request) {
  assert.equal(request.method, 'POST');
  assert.equal(request.headers['user-agent'],
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36');
  assert.equal(request.headers.accept, 'application/json, text/plain, */*');
  assert.equal(request.headers['content-type'], 'application/json');
  assert.equal(request.headers.authorization, undefined);
  assert.equal(request.headers['mcloud-sign'], undefined);
}

// Each fixture routes the provider's real transport to a loopback HTTP server.
// It never opens a connection to the remote URLs retained in the request records.
async function fixture(t, respond, options = {}) {
  const calls = [], requests = [], failures = [], updates = [];
  let currentCredentials = options.credentials ?? {}, refreshCalls = 0;
  const server = createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const call = calls[requests.length];
      const record = {
        url: call.url, endpoint: new URL(call.url).pathname.split('/').pop(),
        headers: request.headers, method: request.method, body: Buffer.concat(chunks).toString(),
        incoming: request, outgoing: response,
      };
      requests.push(record);
      const result = await respond(record, requests);
      if (result == null) return;
      response.writeHead(result.status ?? 200, result.headers ?? { 'content-type': 'application/json' });
      response.end(result.body ?? '');
    } catch (error) {
      failures.push(error);
      response.writeHead(500);
      response.end();
    }
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const origin = `http://127.0.0.1:${server.address().port}`;
  const transport = createTransport();
  const ctx = {
    signal: options.signal,
    get credentials() { return currentCredentials; },
    now: options.now ?? (() => instant),
    http: {
      async request(request) {
        calls.push(request);
        const url = new URL(request.url);
        return transport.request({ ...request, url: origin + url.pathname + url.search });
      },
    },
    async updateCredentials(patch) {
      if (options.saveError) throw new PanError(options.saveError);
      updates.push(patch);
      currentCredentials = { ...currentCredentials, ...patch };
    },
    async refresh() {
      refreshCalls += 1;
      await yidong.refresh({}, ctx);
      return currentCredentials;
    },
  };
  t.after(async () => {
    await transport.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    assert.deepEqual(failures, []);
  });
  return { ctx, calls, requests, updates, get refreshCalls() { return refreshCalls; } };
}

test('mobile exposes the two actual play modes and credential schema', () => {
  assert.equal(yidong.id, 'yidong');
  assert.deepEqual(yidong.modes, ['streaming', 'download']);
  assert.deepEqual(yidong.credentialFields, { authorization: 'string', refreshedAt: 'number' });
  assert.equal(yidong.prepare, undefined);
  assert.equal(yidong.loginStart, undefined);
});

test('share URL forms retain the exact password, Unicode name, large ID and size', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'getOutLinkInfoV6');
    assertAnonymous(request);
    assert.equal(request.headers['hcy-cool-flag'], '1');
    assert.equal(request.headers['x-deviceinfo'],
      '||3|12.27.0|chrome|131.0.0.0|5c7c68368f048245e1ce47f1c0f8f2d0||windows 10|1536X695|zh-CN|||');
    assert.equal(request.headers.referer, `https://caiyun.139.com/w/i/${linkID}`);
    const value = encryptedRequest(request);
    assert.deepEqual(value, {
      getOutLinkInfoReq: {
        account: '', linkID, passwd: ' pw & <原> ', caSrt: 0, coSrt: 0, srtDr: 1,
        bNum: 1, pCaID: 'root', eNum: 200,
      },
      commonAccountInfo: { account: '', accountType: 1 },
    });
    return cipherReply(`{"code":0,"data":{"caLst":{"item":[]},"coLst":{"item":[{"coID":${largeID},"coType":3,"coName":${JSON.stringify(sampleName)},"coSize":9007199254740994001}]}}}`);
  }, { credentials: { authorization: 'intentionally invalid' } });
  const password = ' pw & <原> ';
  for (const url of [
    `https://caiyun.139.com/w/i/${linkID}`,
    `https://caiyun.139.com/m/i?${linkID}`,
    `https://yun.139.com/shareweb/#/w/i/${linkID}`,
    `http://yun.139.com/share?linkID=${linkID}`,
  ]) {
    const result = await yidong.list({ share: { url, password } }, f.ctx);
    assert.deepEqual(result, {
      share: { id: linkID, url: `https://caiyun.139.com/w/i/${linkID}`, password },
      files: [{
        name: sampleName, path: `/${sampleName}`, size: '9007199254740994001',
        file: makeFile({ password }),
      }],
    });
  }
  assert.equal(f.refreshCalls, 0);
  assert.equal(f.requests.length, 4);
});

test('URL query and fragment passwords survive as independent file fields', async t => {
  const f = await fixture(t, request => {
    const value = encryptedRequest(request).getOutLinkInfoReq;
    assert.equal(value.passwd, 'p+#$*');
    return jsonReply({ data: { coLst: [co(largeID, { coName: sampleName, coSize: '42' })] } });
  });
  for (const url of [
    `https://caiyun.139.com/m/i?${linkID}&passwd=p%2B%23%24*`,
    `https://yun.139.com/shareweb/#/w/i/${linkID}?pwd=p%2B%23%24*`,
  ]) {
    const result = await yidong.list({ share: { url } }, f.ctx);
    assert.equal(result.share.password, 'p+#$*');
    assert.equal(result.files[0].file.password, 'p+#$*');
    assert.equal(result.files[0].size, 42);
  }
});

test('list keeps original names while matching only the reference video suffixes after trimming', async t => {
  const names = [' 电影.MP4 ', '剧集.mKv\t', '片段.MOV', '影碟.iso', '视频.flv\n'];
  const excluded = ['影片.avi', '影片.ts', '播放.m3u8', '影片.wmv', '字幕.srt', '影片.mp4.bak'];
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'getOutLinkInfoV6');
    return jsonReply({ data: { coLst: [
      ...names.map((coName, index) => co(index, { coName })),
      ...excluded.map((coName, index) => co(`excluded-${index}`, { coName })),
      co('wrong-type', { coType: 1, coName: 'not-a-video.mp4' }),
    ] } });
  });
  const result = await yidong.list({ share: { url: `https://caiyun.139.com/w/i/${linkID}` } }, f.ctx);
  assert.deepEqual(result.files.map(file => file.name), names);
  assert.deepEqual(result.files.map(file => file.path), names.map(name => `/${name}`));
  assert.deepEqual(result.files.map(file => file.file.name), names);
  assert.equal(f.requests.length, 1);
});

for (const [label, password] of [['empty', ''], ['nonempty', ' explicit +#$* 提取码 ']]) {
  test(`explicit ${label} password overrides URL query and fragment passwords`, async t => {
    const f = await fixture(t, request => {
      assert.equal(request.endpoint, 'getOutLinkInfoV6');
      assertAnonymous(request);
      assert.equal(encryptedRequest(request).getOutLinkInfoReq.passwd, password);
      return jsonReply({ data: { coLst: [co(largeID, { coName: sampleName })] } });
    });
    for (const url of [
      `https://caiyun.139.com/m/i?${linkID}&pwd=old-query-password`,
      `https://yun.139.com/shareweb/#/w/i/${linkID}?pwd=old-fragment-password`,
      `https://yun.139.com/shareweb/?pwd=old-query-password#/w/i/${linkID}?passwd=old-fragment-password`,
    ]) {
      const result = await yidong.list({ share: { url, password } }, f.ctx);
      assert.equal(result.share.password, password);
      assert.equal(result.files[0].file.password, password);
      assert.equal(result.files[0].file.coID, largeID);
    }
    assert.equal(f.requests.length, 3);
    assert.equal(f.refreshCalls, 0);
  });
}

test('malformed shares and legacy encoded inputs fail before any network request', async t => {
  const f = await fixture(t, () => { throw new Error('unexpected request'); });
  for (const url of [
    'https://example.invalid/w/i/ShareFixture123',
    'https://caiyun.139.com.evil.invalid/w/i/ShareFixture123',
    'https://other.caiyun.139.com/w/i/ShareFixture123',
    'https://name:pass@caiyun.139.com/w/i/ShareFixture123',
    'file:///w/i/ShareFixture123',
    'https://caiyun.139.com/',
    'https://caiyun.139.com/m/i?linkID=',
    'https://caiyun.139.com/share?linkID=bad%2Fid',
    'yidong-ShareFixture123',
  ]) {
    await assert.rejects(yidong.list({ share: { url } }, f.ctx), { code: 'INVALID_REQUEST' });
  }
  await assert.rejects(yidong.list({ share: { url: `https://caiyun.139.com/w/i/${linkID}`, password: 123 } }, f.ctx),
    { code: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 0);
});

test('list consumes more than fifty full pages even when they contain no videos', async t => {
  const f = await fixture(t, request => {
    assertAnonymous(request);
    const { bNum, eNum, pCaID } = encryptedRequest(request).getOutLinkInfoReq;
    assert.equal(eNum, 200);
    assert.equal(pCaID, 'root');
    assert.ok(bNum >= 1 && bNum <= 52);
    const files = bNum <= 51
      ? Array.from({ length: 200 }, (_, index) => co(`${bNum}-${index}`, { coName: `说明 ${index}.txt` }))
      : [co(largeID)];
    return jsonReply({ code: 'success', data: { caLst: [], coLst: files } });
  });
  const result = await yidong.list({ share: { url: `https://caiyun.139.com/w/i/${linkID}` } }, f.ctx);
  assert.equal(result.files.length, 1);
  assert.equal(result.files[0].file.coID, largeID);
  assert.equal(f.requests.length, 52);
  assert.deepEqual(f.requests.map(request => encryptedRequest(request).getOutLinkInfoReq.bNum),
    Array.from({ length: 52 }, (_, index) => index + 1));
});

test('overlapping pages deduplicate by full IDs without a sixteen-item signature shortcut', async t => {
  const first = Array.from({ length: 200 }, (_, index) => co(index));
  const second = [...first.slice(0, 199), co('new-200')];
  const f = await fixture(t, request => {
    const page = encryptedRequest(request).getOutLinkInfoReq.bNum;
    return cipherReply({ code: 200, data: {
      caLst: [], coLst: page === 1 ? first : page === 2 ? { item: second } : [
        first[0], co('new-201'), co('not-video', { coName: '字幕.srt' }),
        co('wrong-type', { coType: 1 }), co('iso', { coName: '影碟.ISO' }),
      ],
    } }, page !== 2);
  });
  const result = await yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx);
  assert.equal(result.files.length, 203);
  assert.equal(new Set(result.files.map(item => item.file.coID)).size, 203);
  assert.ok(result.files.some(item => item.file.coID === 'new-201'));
  assert.ok(result.files.some(item => item.name === '影碟.ISO'));
  assert.equal(f.requests.length, 3);
});

test('directory pages use the same page window independently of file count', async t => {
  const f = await fixture(t, request => {
    const { pCaID, bNum } = encryptedRequest(request).getOutLinkInfoReq;
    if (pCaID !== 'root') return jsonReply(empty());
    return jsonReply({ code: 0, data: {
      caLst: bNum === 1
        ? Array.from({ length: 200 }, (_, index) => ({ caName: `目录${index}`, path: `dir-${index}` }))
        : [{ caName: '最后目录', path: 'last-directory' }],
      coLst: bNum === 1 ? [] : [co(largeID)],
    } });
  });
  const result = await yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx);
  assert.equal(result.files.length, 1);
  assert.equal(f.requests.filter(request => encryptedRequest(request).getOutLinkInfoReq.pCaID === 'root').length, 2);
  assert.equal(f.requests.length, 203);
});

test('nested directories preserve the original path and same-name files and stop cycles', async t => {
  const f = await fixture(t, request => {
    const { pCaID } = encryptedRequest(request).getOutLinkInfoReq;
    if (pCaID === 'root') return cipherReply({ code: 0, data: {
      caLst: { item: [
        { caName: '原始根 #$*', path: 'd1' },
        { caName: '重复引用', path: 'd1' },
        { caName: '另一目录', path: 'other' },
      ] },
      coLst: [co('root-file', { coName: sampleName })],
    } });
    if (pCaID === 'other') return jsonReply({ data: { coLst: [co('other-file', { coName: sampleName })] } });
    const depth = Number(pCaID.slice(1));
    return jsonReply({ data: {
      caLst: depth < 14 ? [{ caName: `层 ${depth + 1}`, path: `d${depth + 1}` }] : [
        { caName: '循环', path: 'root' }, { caName: '回到首层', path: 'd1' },
      ],
      coLst: depth === 14 ? [co('deep-file', { coName: sampleName })] : [],
    } });
  });
  const result = await yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx);
  assert.deepEqual(result.files.map(item => item.name), [sampleName, sampleName, sampleName]);
  const paths = [
    `/${sampleName}`, `/另一目录/${sampleName}`,
    `/原始根 #$*/${Array.from({ length: 13 }, (_, index) => `层 ${index + 2}`).join('/')}/${sampleName}`,
  ];
  assert.deepEqual(result.files.map(item => item.path), paths);
  assert.deepEqual(result.files[2].file, makeFile({ coID: 'deep-file', path: paths[2] }));
  assert.equal(f.requests.length, 16);
});

test('a full repeated page is an explicit completeness failure', async t => {
  const repeated = Array.from({ length: 200 }, (_, index) => co(index, { coName: `${index}.txt` }));
  const f = await fixture(t, () => jsonReply({ data: { coLst: repeated } }));
  await assert.rejects(yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx),
    { code: 'UPSTREAM_FORMAT' });
  assert.equal(f.requests.length, 2);
});

test('empty shares succeed without turning malformed or upstream error replies into emptiness', async t => {
  const replies = [
    jsonReply(empty()),
    jsonReply({ code: 0, data: { caLst: null, coLst: { item: null } } }),
    jsonReply({ code: 9188, desc: 'synthetic password required' }),
    jsonReply({ resultCode: '1', message: 'synthetic expired share' }),
    jsonReply({ result_code: '777', msg: 'synthetic 浏览次数已达上限' }),
    { status: 200, body: 'not-json-or-ciphertext' },
    jsonReply(null),
    jsonReply({ code: 0, data: null }),
    jsonReply({ code: 0, data: {} }),
    jsonReply({ code: 0, data: { coLst: {} } }),
    jsonReply({ code: 0, data: { coLst: [null] } }),
    jsonReply({ code: 0, data: { coLst: [{ coType: 3, coName: 'missing-id.mp4' }] } }),
    { status: 200, body: '"AAAA"' },
    jsonReply({ code: 0, data: { caLst: [{ path: 'missing-name' }] } }),
    jsonReply({ code: 0, data: { coLst: [co(1, { coSize: 'NaN' })] } }),
    jsonReply({ code: 0, data: { coLst: [co(1, { coSize: -1 })] } }),
    jsonReply({ data: { coLst: [] } }, 500),
  ];
  const f = await fixture(t, () => replies.shift());
  const params = { share: { url: `https://caiyun.139.com/m/i?${linkID}` } };
  assert.deepEqual((await yidong.list(params, f.ctx)).files, []);
  assert.deepEqual((await yidong.list(params, f.ctx)).files, []);
  for (const code of ['AUTH_REQUIRED', 'UPSTREAM_ERROR', 'UPSTREAM_ERROR',
    ...Array(11).fill('UPSTREAM_FORMAT'), 'UPSTREAM_HTTP']) {
    await assert.rejects(yidong.list(params, f.ctx), error => {
      assert.equal(error.code, code);
      assert.ok(!String(error).includes('synthetic'));
      return true;
    });
  }
  assert.equal(replies.length, 0);
});

test('a child-directory error rejects the whole list rather than reporting partial success', async t => {
  const f = await fixture(t, request => {
    const parent = encryptedRequest(request).getOutLinkInfoReq.pCaID;
    return jsonReply(parent === 'root'
      ? { data: { coLst: [co(1)], caLst: [{ caName: '子目录', path: 'child' }] } }
      : { code: 9188 });
  });
  await assert.rejects(yidong.list({
    share: { url: `https://caiyun.139.com/m/i?${linkID}`, password: 'bad-fixture' },
  }, f.ctx), { code: 'AUTH_FAILED' });
});

test('list-to-streaming round trip sends only the anonymous content request, with the exact ID', async t => {
  const f = await fixture(t, request => {
    assertAnonymous(request);
    if (request.endpoint === 'getOutLinkInfoV6') return jsonReply({ data: {
      coLst: [co(largeID, { coName: sampleName, presentURL: 'https://media.invalid/unused.m3u8' })],
    } });
    assert.equal(request.endpoint, 'getContentInfoFromOutLink');
    assert.deepEqual(JSON.parse(request.body), {
      getContentInfoFromOutLinkReq: { contentId: largeID, linkID, account: '' },
      commonAccountInfo: { account: '', accountType: 1 },
    });
    assert.equal(request.headers['accept-encoding'], 'identity');
    assert.equal(request.headers['hcy-cool-flag'], undefined);
    assert.equal(request.headers['x-deviceinfo'], undefined);
    assert.equal(request.headers.referer, undefined);
    return jsonReply({ code: 0, data: { contentInfo: { presentUrl: 'https://media.invalid/selected.m3u8' } } });
  }, { credentials: { authorization: 'invalid-but-streaming-is-anonymous' } });
  const listed = await yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx);
  assert.equal(f.requests.length, 1, 'list must not probe each presentURL');
  const result = await yidong.play({ file: listed.files[0].file, mode: 'streaming' }, f.ctx);
  assert.deepEqual(result, { url: 'https://media.invalid/selected.m3u8', headers: {}, mode: 'streaming' });
  assert.equal(f.requests.length, 2);
  assert.equal(f.refreshCalls, 0);
});

test('protected streaming sends only the anonymous content request with its exact coID', async t => {
  const f = await fixture(t, request => {
    assertAnonymous(request);
    assert.equal(request.endpoint, 'getContentInfoFromOutLink');
    assert.deepEqual(JSON.parse(request.body), {
      getContentInfoFromOutLinkReq: { contentId: largeID, linkID, account: '' },
      commonAccountInfo: { account: '', accountType: 1 },
    });
    assert.equal(request.headers['accept-encoding'], 'identity');
    return jsonReply({ data: { contentInfo: { presentURL: 'https://media.invalid/protected.m3u8' } } });
  });
  const file = makeFile({ password: 'pw-fixture' });
  const result = await yidong.play({ file, mode: 'streaming' }, f.ctx);
  assert.equal(result.url, 'https://media.invalid/protected.m3u8');
  assert.equal(f.requests.length, 1);
  assert.equal(f.refreshCalls, 0);
});

test('protected list-to-streaming uses the exact file and does not reuse the list URL or re-list', async t => {
  const f = await fixture(t, request => {
    assertAnonymous(request);
    if (request.endpoint === 'getOutLinkInfoV6') {
      assert.equal(encryptedRequest(request).getOutLinkInfoReq.passwd, 'pw-fixture');
      return jsonReply({ data: { coLst: [
        co('other-file', { coName: sampleName, presentURL: 'https://media.invalid/wrong-file.m3u8' }),
        co(largeID, { coName: sampleName, presentURL: 'https://media.invalid/list-url.m3u8' }),
      ] } });
    }
    assert.equal(request.endpoint, 'getContentInfoFromOutLink');
    assert.deepEqual(JSON.parse(request.body).getContentInfoFromOutLinkReq, { account: '', contentId: largeID, linkID });
    return jsonReply({ data: { contentInfo: { presentURL: 'https://media.invalid/content-url.m3u8' } } });
  });
  const listed = await yidong.list({
    share: { url: `https://caiyun.139.com/w/i/${linkID}`, password: 'pw-fixture' },
  }, f.ctx);
  assert.deepEqual(listed.files[1].file, makeFile({ password: 'pw-fixture' }));
  assert.equal((await yidong.play({ file: listed.files[1].file, mode: 'streaming' }, f.ctx)).url,
    'https://media.invalid/content-url.m3u8');
  assert.deepEqual(f.requests.map(request => request.endpoint), ['getOutLinkInfoV6', 'getContentInfoFromOutLink']);
  assert.equal(f.requests.length, 2);
});

test('protected streaming reports content response errors without taking another mode', async t => {
  const replies = [jsonReply({ data: { contentInfo: {} } }), jsonReply({ code: 9188 })];
  const f = await fixture(t, request => {
    assertAnonymous(request);
    assert.equal(request.endpoint, 'getContentInfoFromOutLink');
    return replies.shift();
  });
  const params = { file: makeFile({ password: 'pw-fixture' }), mode: 'streaming' };
  await assert.rejects(yidong.play(params, f.ctx), { code: 'UPSTREAM_FORMAT' });
  await assert.rejects(yidong.play(params, f.ctx), { code: 'AUTH_FAILED' });
  assert.equal(f.requests.length, 2);
});

test('streaming trims presentURL before falling back to presentUrl from the same response', async t => {
  const replies = [
    { presentURL: ' https://media.invalid/primary.m3u8 \n', presentUrl: 'https://media.invalid/unused.m3u8' },
    { presentURL: ' \t ', presentUrl: ' https://media.invalid/alternate.m3u8 ' },
  ];
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'getContentInfoFromOutLink');
    return jsonReply({ data: { contentInfo: replies.shift() } });
  });
  for (const selected of ['primary', 'alternate']) {
    assert.equal((await yidong.play({ file: makeFile(), mode: 'streaming' }, f.ctx)).url,
      `https://media.invalid/${selected}.m3u8`);
  }
  assert.equal(f.requests.length, 2);
});

test('invalid modes, incomplete structured files and absent or invalid download credentials make no request', async t => {
  const f = await fixture(t, () => { throw new Error('unexpected request'); });
  await assert.rejects(yidong.play({ file: makeFile(), mode: 'auto' }, f.ctx), { code: 'UNSUPPORTED_OPERATION' });
  for (const file of [
    `${largeID}*${linkID}***${sampleName}`, makeFile({ kind: 'stored' }), makeFile({ coID: 12 }),
    makeFile({ shareId: '' }), makeFile({ password: undefined }),
    makeFile({ coID: undefined, contentId: undefined }), makeFile({ coID: undefined, contentId: 'other' }),
  ]) {
    await assert.rejects(yidong.play({ file, mode: 'streaming' }, f.ctx), { code: 'INVALID_REQUEST' });
  }
  await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), { code: 'NOT_CONFIGURED' });
  const invalid = await fixture(t, () => { throw new Error('unexpected request'); }, {
    credentials: { authorization: 'Bad@@Base64', refreshedAt: instant },
  });
  await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, invalid.ctx), { code: 'AUTH_FAILED' });
  assert.equal(f.requests.length + invalid.requests.length, 0);
});

test('download AES and MD5 vector signs the same plaintext with UTC+8 time and normalized authorization', async t => {
  const f = await fixture(t, async request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    const expected = {
      dlFromOutLinkReqV3: { account: 'AccountFixture', linkID, coIDLst: { item: [largeID] } },
      commonAccountInfo: { account: 'AccountFixture', accountType: 1 },
    };
    const plain = JSON.stringify(expected);
    assert.equal(plainRequest(request), plain);
    assert.equal(request.headers.authorization, `Basic ${credentials.authorization}`);
    assert.equal(request.headers.accept, 'application/json, text/plain, */*');
    assert.equal(request.headers['accept-encoding'], 'gzip, deflate');
    assert.equal(request.headers['accept-language'], 'zh-CN,zh;q=0.9,en;q=0.8');
    assert.equal(request.headers['content-type'], 'application/json;charset=UTF-8');
    assert.equal(request.headers.origin, 'https://yun.139.com');
    assert.equal(request.headers.referer, 'https://yun.139.com/');
    assert.equal(request.headers['hcy-cool-flag'], '1');
    assert.equal(request.headers['x-deviceinfo'],
      '||9|12.27.0|chrome|143.0.0.0|pda50460feabd10141fb59a3ba787afb||windows 10|1624X1305|zh-CN|||');
    assert.equal(request.headers['accept-charset'], 'utf-8');
    assert.equal(request.headers['x-requested-with'], 'XMLHttpRequest');
    assert.equal(request.headers['user-agent'],
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36');
    const timestamp = '2026-01-02 00:02:03', nonce = 'abcdefghijklmnop';
    const signature = 'F2B7C8A70DE5F3BF4A5AB290E875BDEE';
    assert.equal(signatureFromProtocol(plain, timestamp, nonce), signature);
    assert.equal(request.headers['mcloud-sign'], `${timestamp},${nonce},${signature}`);
    const imported = await webcrypto.subtle.importKey('raw', key, { name: 'AES-CBC' }, false, ['encrypt']);
    const ciphertext = await webcrypto.subtle.encrypt({ name: 'AES-CBC', iv }, imported, Buffer.from(plain));
    assert.equal(request.body, JSON.stringify(Buffer.concat([iv, Buffer.from(ciphertext)]).toString('base64')));
    return cipherReply({ code: '0', data: { redrUrlNew: 'https://download.invalid/exact-file' } });
  }, { credentials: { ...credentials, authorization: `bAsIc ${Buffer.from(identity).toString('base64url')}\n` } });
  t.mock.method(crypto, 'randomBytes', length => {
    assert.equal(length, 16);
    return Buffer.from(iv);
  });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  assert.deepEqual(await yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), {
    url: 'https://download.invalid/exact-file', headers: {}, mode: 'download',
  });
  assert.equal(f.requests.length, 1);
  assert.equal(f.refreshCalls, 0);
});

test('download accepts the supported URL fields at the root or in data without extra media requests', async t => {
  const fields = ['redrUrl', 'redrUrlNew', 'downloadUrl', 'url', 'dlUrl'];
  const f = await fixture(t, (request, requests) => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    const index = requests.length - 1, field = fields[index % fields.length];
    const location = { [field]: `https://download.invalid/file-${index}` };
    return index < fields.length ? jsonReply({ code: 0, data: location }) : cipherReply({ resultCode: 200, ...location });
  }, { credentials });
  for (let index = 0; index < fields.length * 2; index += 1) {
    assert.equal((await yidong.play({ file: makeFile(), mode: 'download' }, f.ctx)).url,
      `https://download.invalid/file-${index}`);
  }
  assert.equal(f.requests.length, 10);
});

test('download follows the reference field order and skips non-HTTP candidates in the same response', async t => {
  const values = [
    { data: {
      redrUrl: ' https://download.invalid/first \n', redrUrlNew: 'https://download.invalid/unused',
      downloadUrl: 'https://download.invalid/unused', url: 'https://download.invalid/unused', dlUrl: 'https://download.invalid/unused',
    } },
    {
      redrUrl: 'ftp://download.invalid/non-http', redrUrlNew: 'not-a-url', downloadUrl: ' \t ',
      url: ' https://download.invalid/fourth ', dlUrl: 'https://download.invalid/unused',
    },
    { redrUrl: 'https://download.invalid/root-unused', data: { dlUrl: 'https://download.invalid/data' } },
  ];
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    return jsonReply(values.shift());
  }, { credentials });
  for (const selected of ['first', 'fourth', 'data']) {
    assert.equal((await yidong.play({ file: makeFile(), mode: 'download' }, f.ctx)).url,
      `https://download.invalid/${selected}`);
  }
  assert.equal(f.requests.length, 3);
});

test('download does not invent response fields or read root URLs when a data object is present', async t => {
  const values = [
    { download_url: 'https://download.invalid/unrecognized', play_url: 'https://media.invalid/unrecognized',
      presentURL: 'https://media.invalid/unrecognized' },
    { redrUrl: 'https://download.invalid/root-unused', data: {} },
    { data: { redrUrl: 'http-invalid', dlUrl: 'https://download.invalid/unused' } },
  ];
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    return jsonReply(values.shift());
  }, { credentials });
  for (let index = 0; index < 3; index += 1) {
    await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), { code: 'UPSTREAM_FORMAT' });
  }
  assert.equal(f.requests.length, 3);
});

test('protected list-to-download preserves the selected coID and contentId across same-name files', async t => {
  const contentId = '900719925474099312346';
  const f = await fixture(t, (request, requests) => {
    if (request.endpoint === 'getOutLinkInfoV6') {
      assert.equal(requests.length, 1);
      assertAnonymous(request);
      assert.equal(encryptedRequest(request).getOutLinkInfoReq.passwd, 'pw-fixture');
      return cipherReply({ data: { coLst: [
        co('other-file', { coName: sampleName, contentId: 'other-content' }),
        co(largeID, { coName: sampleName, contentId, presentURL: 'https://media.invalid/unused.m3u8' }),
      ] } });
    }
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    if (requests.length === 2) {
      assert.deepEqual(encryptedRequest(request).dlFromOutLinkReqV3,
        { account: 'AccountFixture', linkID, coIDLst: { item: [largeID] } });
      return jsonReply({ code: 9530 });
    }
    assert.deepEqual(encryptedRequest(request).dlFromOutLinkReq,
      { contentId, linkID, account: 'AccountFixture' });
    return cipherReply({ data: { url: 'https://download.invalid/selected' } });
  }, { credentials });
  const listed = await yidong.list({
    share: { url: `https://caiyun.139.com/w/i/${linkID}`, password: 'pw-fixture' },
  }, f.ctx);
  assert.deepEqual(listed.files[1].file, makeFile({ password: 'pw-fixture', contentId }));
  assert.equal((await yidong.play({ file: listed.files[1].file, mode: 'download' }, f.ctx)).url,
    'https://download.invalid/selected');
  assert.deepEqual(f.requests.map(request => request.endpoint),
    ['getOutLinkInfoV6', 'dlFromOutLinkV3', 'dlFromOutLinkV3']);
  assert.equal(f.refreshCalls, 0);
});

test('9530 retries the existing legacy payload only with its separately preserved contentId', async t => {
  const f = await fixture(t, (request, requests) => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    const payload = encryptedRequest(request);
    if (requests.length === 1) {
      assert.deepEqual(payload.dlFromOutLinkReqV3.coIDLst, { item: [largeID] });
      return jsonReply({ code: 9530 });
    }
    assert.deepEqual(payload, {
      dlFromOutLinkReq: { contentId: 'different-content-id', linkID, account: 'AccountFixture' },
      commonAccountInfo: { account: 'AccountFixture', accountType: 1 },
    });
    return jsonReply({ data: { downloadUrl: 'https://download.invalid/legacy' } });
  }, { credentials });
  assert.equal((await yidong.play({
    file: makeFile({ contentId: 'different-content-id' }), mode: 'download',
  }, f.ctx)).url, 'https://download.invalid/legacy');
  assert.equal(f.requests.length, 2);
});

test('9530 without an explicit contentId fails without inventing an ID or requesting streaming', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    assert.ok(encryptedRequest(request).dlFromOutLinkReqV3);
    return jsonReply({ resultCode: '9530' });
  }, { credentials });
  await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), { code: 'UPSTREAM_ERROR' });
  assert.equal(f.requests.length, 1);
});

test('9530 carrying a URL returns it without retrying the contentId payload', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    assert.ok(encryptedRequest(request).dlFromOutLinkReqV3);
    return jsonReply({ code: 9530, data: { redrUrl: 'https://download.invalid/already-present' } });
  }, { credentials });
  assert.equal((await yidong.play({
    file: makeFile({ contentId: 'explicit-content-id' }), mode: 'download',
  }, f.ctx)).url, 'https://download.invalid/already-present');
  assert.equal(f.requests.length, 1);
});

test('non-9530 download failures do not retry another payload or switch to streaming', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    assert.ok(encryptedRequest(request).dlFromOutLinkReqV3);
    return jsonReply({ code: 123 });
  }, { credentials });
  await assert.rejects(yidong.play({
    file: makeFile({ contentId: 'explicit-content-id' }), mode: 'download',
  }, f.ctx), { code: 'UPSTREAM_ERROR' });
  assert.equal(f.requests.length, 1);
});

test('a contentId-only reference uses the documented legacy body once', async t => {
  const f = await fixture(t, request => {
    assert.deepEqual(encryptedRequest(request), {
      dlFromOutLinkReq: { contentId: 'actual-content-id', linkID, account: 'AccountFixture' },
      commonAccountInfo: { account: 'AccountFixture', accountType: 1 },
    });
    return jsonReply({ data: { dlUrl: 'http://download.invalid/legacy-only' } });
  }, { credentials: { ...credentials, authorization: identity } });
  assert.equal((await yidong.play({
    file: makeFile({ coID: undefined, contentId: 'actual-content-id' }), mode: 'download',
  }, f.ctx)).url, 'http://download.invalid/legacy-only');
  assert.equal(f.requests.length, 1);
});

test('download with a password sends only its signed request without re-listing the parent', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    assert.deepEqual(encryptedRequest(request), {
      dlFromOutLinkReqV3: { account: 'AccountFixture', linkID, coIDLst: { item: [largeID] } },
      commonAccountInfo: { account: 'AccountFixture', accountType: 1 },
    });
    return jsonReply({ data: { url: 'https://download.invalid/protected' } });
  }, { credentials });
  assert.equal((await yidong.play({
    file: makeFile({ password: 'pw-fixture' }), mode: 'download',
  }, f.ctx)).url, 'https://download.invalid/protected');
  assert.deepEqual(f.requests.map(request => request.endpoint), ['dlFromOutLinkV3']);
  assert.equal(f.refreshCalls, 0);
});

test('streaming and download reject broken URLs, missing bodies and upstream failures', async t => {
  const responses = [
    jsonReply({ data: {} }), jsonReply({ data: { contentInfo: {} } }),
    jsonReply({ data: { contentInfo: { presentURL: 'javascript:alert(1)' } } }),
    jsonReply({ data: { contentInfo: { presentURL: 'https://name:pass@media.invalid/file' } } }),
    jsonReply({ code: 9188, data: { contentInfo: { presentURL: 'https://media.invalid/ignore' } } }),
    jsonReply({ code: 123, data: {} }),
    jsonReply({ data: { url: 'file:///media.mp4' } }),
    jsonReply({ code: 0, data: {} }),
    jsonReply({ code: 123, data: { url: 'https://download.invalid/should-not-succeed' } }),
  ];
  const f = await fixture(t, () => responses.shift(), { credentials });
  for (const code of ['UPSTREAM_FORMAT', 'UPSTREAM_FORMAT', 'UPSTREAM_FORMAT', 'UPSTREAM_FORMAT', 'AUTH_REQUIRED']) {
    await assert.rejects(yidong.play({ file: makeFile(), mode: 'streaming' }, f.ctx), { code });
  }
  for (const code of ['UPSTREAM_ERROR', 'UPSTREAM_FORMAT', 'UPSTREAM_FORMAT', 'UPSTREAM_ERROR']) {
    await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), { code });
  }
  assert.equal(responses.length, 0);
});

test('aged authorization refreshes through XML and persists its new token before download', async t => {
  const original = 'ClientMixed:Account<&Fixture:Old:<&Token';
  const f = await fixture(t, request => {
    if (request.endpoint === 'authTokenRefresh.do') {
      assert.equal(request.url, 'https://aas.caiyun.feixin.10086.cn:443/tellin/authTokenRefresh.do');
      assert.equal(request.headers['content-type'], 'application/xml');
      assert.equal(request.headers.accept, 'application/xml');
      assert.equal(request.headers['user-agent'],
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36');
      assert.equal(request.headers.authorization, undefined);
      assert.equal(request.body,
        '<root><token>Old:&lt;&amp;Token</token><account>Account&lt;&amp;Fixture</account><clienttype>656</clienttype></root>');
      return { body: '<?xml version="1.0" encoding="UTF-8"?><root><return>0</return><token>New&amp;Token&#58;&#x54;ail</token><desc/></root>' };
    }
    assert.equal(request.endpoint, 'dlFromOutLinkV3');
    assert.equal(request.headers.authorization,
      `Basic ${Buffer.from('ClientMixed:Account<&Fixture:New&Token:Tail').toString('base64')}`);
    assert.equal(encryptedRequest(request).dlFromOutLinkReqV3.account, 'Account<&Fixture');
    return jsonReply({ data: { url: 'https://download.invalid/refreshed' } });
  }, { credentials: { authorization: original } });
  const result = await yidong.play({ file: makeFile(), mode: 'download' }, f.ctx);
  assert.equal(result.url, 'https://download.invalid/refreshed');
  assert.equal(f.refreshCalls, 1);
  assert.deepEqual(f.updates, [{
    authorization: Buffer.from('ClientMixed:Account<&Fixture:New&Token:Tail').toString('base64'), refreshedAt: instant,
  }]);
  assert.equal(JSON.stringify(result).includes('Token'), false);
});

test('explicit refresh uses CDATA tokens, preserves prefix/account and waits for successful storage', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'authTokenRefresh.do');
    assert.equal(request.body,
      '<root><token>TokenPart:Tail</token><account>AccountFixture</account><clienttype>656</clienttype></root>');
    return { body: '<root>\n<return>0</return><token><![CDATA[New:Token<&]]></token><desc>ok</desc>\n</root>' };
  }, { credentials });
  assert.deepEqual(await yidong.refresh({}, f.ctx), { refreshed: true });
  assert.deepEqual(f.updates, [{
    authorization: Buffer.from('WebClient:AccountFixture:New:Token<&').toString('base64'), refreshedAt: instant,
  }]);
  assert.equal(f.requests.length, 1, 'explicit refresh does not reuse the twelve-hour freshness hint');
});

test('missing, twelve-hour-old and future freshness hints use the shared refresh path', async t => {
  for (const refreshedAt of [undefined, 0, instant - 12 * 60 * 60 * 1000, instant + 1]) {
    const f = await fixture(t, request => request.endpoint === 'authTokenRefresh.do'
      ? { body: '<root><return>0</return><token>new-fixture-token</token></root>' }
      : jsonReply({ data: { url: 'https://download.invalid/refreshed' } }), {
      credentials: { authorization: credentials.authorization, refreshedAt },
    });
    await yidong.play({ file: makeFile(), mode: 'download' }, f.ctx);
    assert.equal(f.refreshCalls, 1);
    assert.equal(f.updates.length, 1);
  }
});

test('refresh failures never update credentials, return success or continue a download with the old token', async t => {
  const cases = [
    [{ body: '<root><return>1</return><desc>fixture-secret-not-for-errors</desc></root>' }, 'AUTH_FAILED'],
    [{ body: '<root><return>0</return><token/></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><return>0</return></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><token>new-fixture</token></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><return>0</return><return>1</return><token>new-fixture</token></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><return>0</return><token>unescaped&value</token></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<!DOCTYPE root [<!ENTITY test SYSTEM "file:///etc/passwd">]><root><return>0</return><token>&test;</token></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><return>0</return><token><nested>unexpected</nested></token></root>' }, 'UPSTREAM_FORMAT'],
    [{ body: '<root><return>0</return><token>new-fixture</root>' }, 'UPSTREAM_FORMAT'],
    [{ body: 'not-xml' }, 'UPSTREAM_FORMAT'],
    [{ status: 500, body: '<root><return>0</return><token>new-fixture</token></root>' }, 'UPSTREAM_HTTP'],
    [{ status: 401, body: '' }, 'AUTH_REQUIRED'],
  ];
  for (const [reply, code] of cases) {
    const f = await fixture(t, request => {
      assert.equal(request.endpoint, 'authTokenRefresh.do');
      return reply;
    }, { credentials: { authorization: credentials.authorization } });
    await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), error => {
      assert.equal(error.code, code);
      assert.ok(!String(error).includes('fixture-secret'));
      assert.ok(!String(error).includes('TokenPart'));
      return true;
    });
    assert.equal(f.requests.length, 1);
    assert.deepEqual(f.updates, []);
  }
});

test('a persistence failure rejects both explicit refresh and automatic pre-download refresh', async t => {
  const f = await fixture(t, request => {
    assert.equal(request.endpoint, 'authTokenRefresh.do');
    return { body: '<root><return>0</return><token>new-fixture</token></root>' };
  }, { credentials: { authorization: credentials.authorization }, saveError: 'STORAGE_ERROR' });
  await assert.rejects(yidong.refresh({}, f.ctx), { code: 'STORAGE_ERROR' });
  await assert.rejects(yidong.play({ file: makeFile(), mode: 'download' }, f.ctx), { code: 'STORAGE_ERROR' });
  assert.equal(f.requests.length, 2);
  assert.deepEqual(f.updates, []);
  assert.equal(f.ctx.credentials.authorization, credentials.authorization);
});

test('caller cancellation reaches a pending HTTP request and closes its real socket', { timeout: 5000 }, async t => {
  const controller = new AbortController();
  let socketClosed;
  const f = await fixture(t, request => {
    socketClosed = once(request.incoming.socket, 'close');
    controller.abort();
    return null;
  }, { signal: controller.signal });
  await assert.rejects(yidong.list({ share: { url: `https://caiyun.139.com/m/i?${linkID}` } }, f.ctx), { code: 'CANCELLED' });
  await socketClosed;
  assert.equal(f.calls[0].signal, controller.signal);
  assert.equal(f.requests.length, 1);
  assert.deepEqual(f.updates, []);
});
