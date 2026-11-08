import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { baidu } from '../src/providers/baidu.js';
import { PanError } from '../src/errors.js';
import { createPanService } from '../src/service.js';

const share = { url: 'https://pan.baidu.com/s/1Share-id', password: 'p#$*' };
const sharedFile = {
  kind: 'share', fileId: '9223372036854775807', shareId: 'share-identity', uk: 'owner',
  surl: 'Share-id', password: share.password, name: 'same.mp4', path: '/Series/same.mp4',
};
const storedFile = { kind: 'stored', fileId: 'stored-identity', name: 'same(1).mp4', path: '/MeowFilm/same(1).mp4' };
const webUA = 'Mozilla/5.0 (Linux; Android 12; V2238A) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/101.0.4951.40 Safari/537.36';
const qrUA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36';
const netdiskUA = 'netdisk;12.11.9;V2238A;android-android;12;JSbridge4.4.0;jointBridge;1.1.0;';
const playUA = 'com.android.chrome/131.0.6778.200 (Linux;Android 10) AndroidXMedia3/1.5.1';
const qrPNG = Buffer.from('89504e470d0a1a0a0000000d49484452', 'hex');
const qrChallenge = { type: 'image', url: `data:image/png;base64,${qrPNG.toString('base64')}` };

function response(value, status = 200, headers = {}) {
  return { status, headers, body: Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)), url: '' };
}

function header(request, name) {
  return Object.entries(request.headers).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1];
}

function fixture(handler, initial = { cookie: 'BDUSS=synthetic' }) {
  let credentials = { ...initial };
  let now = 1800000000000;
  const calls = [];
  const patches = [];
  const controller = new AbortController();
  const transport = {
    async request(request) {
      calls.push(request);
      assert.equal(request.signal instanceof AbortSignal, true);
      if (request.signal.aborted) throw new PanError('CANCELLED');
      return handler(new URL(request.url), request, calls.length);
    },
    async close() {},
  };
  const ctx = {
    http: transport, signal: controller.signal, now: () => now,
    get credentials() { return credentials; },
    async updateCredentials(patch) { patches.push(patch); credentials = { ...credentials, ...patch }; },
    async refresh() { throw new Error('Baidu has no credential refresh protocol'); },
    async prepare(file) { return (await baidu.prepare({ file }, ctx)).file; },
  };
  return { ctx, calls, patches, controller, transport, advance(ms) { now += ms; } };
}

function directoryItem(fileId, path) {
  return { fs_id: fileId, isdir: 1, server_filename: path.split('/').at(-1) || 'root', path };
}

function listing(items, more = false) {
  return response({ errno: 0, shareid: 'share-identity', uk: 'owner', list: items, has_more: more ? 1 : 0 });
}

test('Baidu validates configuration, share references and unsupported mode before network', async () => {
  const f = fixture(() => assert.fail('unexpected upstream'));
  await assert.rejects(baidu.play({ file: storedFile, mode: 'download' }, f.ctx), { code: 'UNSUPPORTED_OPERATION' });
  await assert.rejects(baidu.prepare({ file: { ...sharedFile, uk: '' } }, f.ctx), { code: 'INVALID_REQUEST' });
  await assert.rejects(baidu.list({ share: { url: 'https://pan.baidu.com.attacker.invalid/s/1test' } }, f.ctx), { code: 'INVALID_REQUEST' });
  const missing = fixture(() => assert.fail('unexpected upstream'), {});
  await assert.rejects(baidu.list({ share }, missing.ctx), { code: 'NOT_CONFIGURED' });
  assert.equal(f.calls.length, 0);
});

test('Baidu list consumes declared pages, deep directories and preserves IDs, names and passwords', async () => {
  const name = '  原名 #$*.MP4';
  const f = fixture(url => {
    assert.equal(url.pathname, '/share/list');
    if (url.searchParams.get('root') === '1') {
      if (url.searchParams.get('page') === '1') return listing([directoryItem('d0', '/Series')], true);
      return response(`{"errno":0,"shareid":"share-identity","uk":"owner","has_more":0,"list":[{"fs_id":9223372036854775807,"isdir":0,"server_filename":${JSON.stringify(name)},"path":${JSON.stringify(`/${name}`)},"size":9007199254740993}]}`);
    }
    const path = url.searchParams.get('dir');
    if (path === '/Series') return listing([directoryItem('cycle', '/'), directoryItem('d1', '/deep/1')]);
    const depth = Number(path.split('/').at(-1));
    if (depth < 61) return listing([directoryItem(`d${depth + 1}`, `/deep/${depth + 1}`)]);
    return listing([
      { fs_id: 'deep-file', isdir: 0, server_filename: 'same.mp4', path: '/deep/61/same.mp4' },
      { fs_id: 'text', isdir: 0, server_filename: 'skip.txt', path: '/deep/61/skip.txt' },
    ]);
  });
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.equal(result.files.length, 2);
  assert.equal(result.files[0].name, name);
  assert.equal(result.files[0].path, `/${name}`);
  assert.equal(result.files[0].size, '9007199254740993');
  assert.equal(result.files[0].file.fileId, '9223372036854775807');
  assert.equal(result.files[1].file.path, '/deep/61/same.mp4');
  assert.ok(f.calls.length > 61);
  assert.equal(f.calls.every(call => new URL(call.url).pathname === '/share/list'), true);
});

test('Baidu shares merge upstream cookies and retain extraction password in structured references', async () => {
  const f = fixture((url, request) => {
    assert.equal(request.method ?? 'GET', url.pathname === '/share/verify' ? 'POST' : 'GET');
    assert.equal(header(request, 'User-Agent'), qrUA);
    assert.equal(header(request, 'Referer'), 'https://pan.baidu.com/');
    assert.equal(header(request, 'Origin'), 'https://pan.baidu.com');
    assert.equal(header(request, 'Accept'), 'application/json, text/plain, */*');
    if (url.pathname === '/share/verify') {
      assert.deepEqual(Object.fromEntries(url.searchParams), { surl: 'Share-id', t: String(f.ctx.now()) });
      assert.equal(header(request, 'Content-Type'), 'application/x-www-form-urlencoded');
      assert.equal(new URLSearchParams(request.body.toString()).get('pwd'), share.password);
      return response({ errno: 0 }, 200, { 'set-cookie': ['BDCLND=synthetic-share; Path=/'] });
    }
    assert.match(header(request, 'Cookie'), /BDCLND=synthetic-share/);
    return listing([{ fs_id: '1', isdir: 0, server_filename: 'same.mp4', path: '/same.mp4', size: 42 }]);
  });
  const result = await baidu.list({ share }, f.ctx);
  assert.equal(result.share.password, share.password);
  assert.equal(result.files[0].file.password, share.password);
  assert.equal(result.files[0].file.surl, 'Share-id');
  assert.equal(result.files[0].size, 42);
  assert.equal(f.patches.length, 1);
});

test('Baidu root and directory list use reference parameters, desktop headers and exact original paths', async () => {
  const f = fixture((url, request) => {
    assert.equal(url.origin, 'https://pan.baidu.com');
    assert.equal(url.pathname, '/share/list');
    assert.equal(request.method ?? 'GET', 'GET');
    assert.equal(header(request, 'User-Agent'), qrUA);
    assert.equal(header(request, 'Cookie'), 'BDUSS=synthetic');
    assert.equal(header(request, 'Referer'), 'https://pan.baidu.com/');
    assert.equal(header(request, 'Origin'), 'https://pan.baidu.com');
    assert.equal(header(request, 'Accept'), 'application/json, text/plain, */*');
    const common = { desc: '1', showempty: '0', page: '1', num: '10000', shorturl: 'Share-id' };
    if (url.searchParams.get('root') === '1') {
      assert.deepEqual(Object.fromEntries(url.searchParams), { ...common, order: 'time', root: '1' });
      return listing([{ fsid: 'directory', isdir: true, name: ' 原目录 #$* ', server_path: '/ 原目录 #$* ' }]);
    }
    assert.deepEqual(Object.fromEntries(url.searchParams), {
      ...common, order: 'other', root: '0', dir: '/ 原目录 #$* ', t: String(f.ctx.now() * 1000),
    });
    return listing([{ fsid: 'selected', isdir: false, name: ' 原视频 #$*.MP4 ' }]);
  });
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.equal(f.calls.length, 2);
  assert.equal(result.files[0].name, ' 原视频 #$*.MP4 ');
  assert.equal(result.files[0].path, '/ 原目录 #$* / 原视频 #$*.MP4 ');
  assert.equal(result.files[0].file.fileId, 'selected');
  assert.equal(result.files[0].file.path, result.files[0].path);
});

test('Baidu lists only reference-supported video extensions while preserving whitespace and same-name identities', async () => {
  const names = [' film.MP4 ', 'film.mKv', 'film.MOV', 'film.iso', 'film.flv', 'same.mp4', 'same.mp4'];
  const f = fixture(() => listing([
    ...names.map((name, index) => ({ fs_id: String(index + 1), server_filename: name, path: `/folder-${index}/${name}`, isdir: 0 })),
    ...['film.avi', 'film.ts', 'film.webm', 'film.m3u8', 'film.mp4.srt', 'README'].map((name, index) =>
      ({ fs_id: `other-${index}`, server_filename: name, path: `/${name}`, isdir: 0 })),
  ]));
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.deepEqual(result.files.map(item => item.name), names);
  assert.deepEqual(result.files.map(item => item.file.fileId), ['1', '2', '3', '4', '5', '6', '7']);
  assert.equal(result.files[0].path, '/folder-0/ film.MP4 ');
  assert.equal(result.files[5].file.path, '/folder-5/same.mp4');
  assert.equal(result.files[6].file.path, '/folder-6/same.mp4');
  assert.equal(f.calls.length, 1);
});

test('Baidu URL extraction password is preserved unless explicitly replaced or cleared', async () => {
  for (const override of [undefined, 'explicit-code', '']) {
    const expected = override ?? 'url+#$*';
    const f = fixture((url, request) => {
      if (url.pathname === '/share/verify') {
        assert.equal(new URLSearchParams(request.body.toString()).get('pwd'), expected);
        return response({ errno: 0 });
      }
      return listing([{ fs_id: 'selected', server_filename: 'one.mp4', path: '/one.mp4', isdir: 0 }]);
    });
    const result = await baidu.list({ share: {
      url: 'https://pan.baidu.com/share/init?surl=Share-id&pwd=url%2B%23%24*',
      ...(override === undefined ? {} : { password: override }),
    } }, f.ctx);
    assert.equal(result.share.password, expected);
    assert.equal(result.files[0].file.password, expected);
    assert.equal(f.calls.filter(call => new URL(call.url).pathname === '/share/verify').length, expected ? 1 : 0);
  }
});

test('Baidu share URL keys use reference prefix handling and request credentials are normalized without changing references', async () => {
  for (const [url, surl] of [
    ['https://pan.baidu.com/s/1Share-id', 'Share-id'],
    ['https://pan.baidu.com/s/11Share-id', '1Share-id'],
    ['https://pan.baidu.com/s/Share-id', 'Share-id'],
    ['https://pan.baidu.com/share/init?surl=1Share-id', '1Share-id'],
  ]) {
    const f = fixture((upstream, request) => {
      assert.equal(header(request, 'Cookie'), 'BDUSS=synthetic');
      if (upstream.pathname === '/share/verify') {
        assert.equal(upstream.searchParams.get('surl'), surl);
        assert.equal(new URLSearchParams(request.body.toString()).get('pwd'), 'code');
        return response({ errno: 0 });
      }
      assert.equal(upstream.searchParams.get('shorturl'), surl);
      return listing([{ fs_id: 'file', server_filename: 'one.mp4', path: '/one.mp4', isdir: 0 }]);
    }, { cookie: '  BDUSS=synthetic  ' });
    const result = await baidu.list({ share: { url, password: ' code ' } }, f.ctx);
    assert.equal(result.share.id, surl);
    assert.equal(result.share.password, ' code ');
    assert.equal(result.files[0].file.password, ' code ');
  }
});

test('Baidu reference field precedence and recursive data lists retain exact names and path-only directory traversal', async () => {
  const f = fixture(url => {
    if (url.searchParams.get('root') === '1') return response({
      errno: 0, shareid: '', share_id: 'share-alias', uk: '', share_uk: 'owner-alias',
      data: { data: { list: [{ isdir: ' true ', path: '', server_path: '/Folder' }] } },
    });
    assert.equal(url.searchParams.get('dir'), '/Folder');
    return response({ errno: 0, data: { data: { list: [
      { isdir: 1, path: '/' },
      { fs_id: '', fsid: 'alias-file', isdir: 0, server_filename: '', name: ' Original.MOV ', path: '', server_path: '/Folder/ Original.MOV ' },
    ] } } });
  });
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.deepEqual(result.files.map(item => [item.name, item.path]), [[' Original.MOV ', '/Folder/ Original.MOV ']]);
  assert.equal(result.files[0].file.fileId, 'alias-file');
  assert.equal(result.files[0].file.shareId, 'share-alias');
  assert.equal(result.files[0].file.uk, 'owner-alias');
  assert.equal(f.calls.length, 2);
});

test('Baidu recursive share data retains declared pagination from surrounding response objects', async () => {
  const f = fixture(url => {
    const page = Number(url.searchParams.get('page'));
    assert.ok(page <= 3);
    return response({
      errno: 0, shareid: 'share-identity', uk: 'owner',
      data: { ...(page === 1 ? { total: 3 } : {}), data: { list: [
        { fs_id: `file-${page}`, server_filename: `file-${page}.mp4`, path: `/file-${page}.mp4`, isdir: 0 },
      ] } },
    });
  });
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.deepEqual(result.files.map(item => item.file.fileId), ['file-1', 'file-2', 'file-3']);
  assert.equal(f.calls.length, 3);
});

test('Baidu repeated continuing page, malformed list and explicit share failure do not become empty success', async () => {
  const repeat = fixture(() => listing([{ fs_id: '1', isdir: 0, server_filename: 'one.mp4', path: '/one.mp4' }], true));
  await assert.rejects(baidu.list({ share: { ...share, password: '' } }, repeat.ctx), { code: 'UPSTREAM_FORMAT' });
  assert.equal(repeat.calls.length, 2);
  const malformed = fixture(() => response({ errno: 0, shareid: 's', uk: 'u' }));
  await assert.rejects(baidu.list({ share: { ...share, password: '' } }, malformed.ctx), { code: 'UPSTREAM_FORMAT' });
  const wrongPassword = fixture(() => response({ errno: -9, errmsg: 'synthetic-secret must not escape' }));
  await assert.rejects(baidu.list({ share }, wrongPassword.ctx), error => error.code === 'UPSTREAM_ERROR' && !error.message.includes('synthetic-secret'));
  assert.equal(wrongPassword.calls.length, 1);
});

test('Baidu explicit empty list is a complete successful result', async () => {
  const f = fixture(() => listing([]));
  assert.deepEqual((await baidu.list({ share: { ...share, password: '' } }, f.ctx)).files, []);
});

test('Baidu nested share context, total-only pages and directory aliases retain completeness without looping', async () => {
  const f = fixture(url => {
    if (url.searchParams.get('root') === '1') {
      const page = Number(url.searchParams.get('page'));
      if (page === 1) return response({ errno: 0, data: {
        shareid: 's', uk: 'u', total: 3, list: [directoryItem('same-directory', '/root')],
      } });
      return response({ errno: 0, data: {
        list: [{ fs_id: `f${page}`, isdir: 0, server_filename: `file${page}.mp4`, path: `/file${page}.mp4` }],
      } });
    }
    return listing([directoryItem('same-directory', '/root/alias')]);
  });
  const result = await baidu.list({ share: { ...share, password: '' } }, f.ctx);
  assert.deepEqual(result.files.map(item => item.name), ['file2.mp4', 'file3.mp4']);
  assert.equal(result.files[0].file.shareId, 's');
  assert.equal(result.files[0].file.uk, 'u');
  assert.equal(f.calls.length, 4);
});

test('Baidu declared total survives omitted or lowered later totals and missing pages fail explicitly', async () => {
  for (const laterTotal of [undefined, 0, 1]) {
    for (const missing of [false, true]) {
      const f = fixture(url => {
        const page = Number(url.searchParams.get('page'));
        assert.ok(page <= 3);
        return response({
          errno: 0, shareid: 's', uk: 'u',
          ...(page === 1 ? { total: 3 } : laterTotal === undefined ? {} : { total: laterTotal }),
          list: missing && page === 3 ? [] : [{
            fs_id: `file-${page}`, server_filename: `page-${page}.mp4`, path: `/page-${page}.mp4`, isdir: 0,
          }],
        });
      });
      const result = baidu.list({ share: { ...share, password: '' } }, f.ctx);
      if (missing) await assert.rejects(result, { code: 'UPSTREAM_FORMAT' });
      else assert.deepEqual((await result).files.map(item => item.name), ['page-1.mp4', 'page-2.mp4', 'page-3.mp4']);
      assert.equal(f.calls.length, 3);
    }
  }
});

test('Baidu prepare creates only the dedicated directory and requests exactly the selected transfer', async () => {
  const f = fixture((url, request) => {
    assert.equal(url.origin, 'https://pan.baidu.com');
    assert.equal(header(request, 'Referer'), 'https://pan.baidu.com/');
    assert.equal(header(request, 'Origin'), 'https://pan.baidu.com');
    assert.equal(header(request, 'Accept'), 'application/json, text/plain, */*');
    assert.equal(header(request, 'User-Agent'), url.pathname === '/share/verify' ? qrUA : webUA);
    if (url.pathname === '/api/loginStatus') {
      assert.equal(request.method ?? 'GET', 'GET');
      assert.deepEqual(Object.fromEntries(url.searchParams), { clienttype: '1', web: '1', channel: 'web', version: '0' });
      return response({ login_info: { bdstoken: 'synthetic-token' } }, 200, {
        'set-cookie': ['STOKEN=synthetic-updated; Path=/'],
      });
    }
    assert.match(header(request, 'Cookie'), /STOKEN=synthetic-updated/);
    if (url.pathname === '/api/list') {
      assert.equal(request.method ?? 'GET', 'GET');
      assert.deepEqual(Object.fromEntries(url.searchParams), {
        clienttype: '0', app_id: '250528', web: '1', order: 'time', desc: '1', num: '9999', page: '1',
      });
      return response({ errno: 0, list: [directoryItem('wrong-location', '/Other/MeowFilm')] });
    }
    assert.equal(request.method, 'POST');
    assert.equal(header(request, 'Content-Type'), 'application/x-www-form-urlencoded');
    if (url.pathname === '/api/create') {
      assert.deepEqual(Object.fromEntries(url.searchParams), { a: 'commit', bdstoken: 'synthetic-token', clienttype: '0', web: '1' });
      assert.equal(new URLSearchParams(request.body.toString()).get('path'), '//MeowFilm');
      assert.equal(new URLSearchParams(request.body.toString()).get('isdir'), '1');
      assert.equal(new URLSearchParams(request.body.toString()).get('block_list'), '[]');
      return response({ errno: 0, path: '/MeowFilm' });
    }
    if (url.pathname === '/share/verify') return response({ errno: 0 });
    assert.equal(url.pathname, '/share/transfer');
    assert.equal(url.searchParams.get('shareid'), sharedFile.shareId);
    assert.equal(url.searchParams.get('from'), sharedFile.uk);
    assert.equal(url.searchParams.get('ondup'), 'newcopy');
    assert.deepEqual(Object.fromEntries(url.searchParams), { shareid: sharedFile.shareId, from: sharedFile.uk, ondup: 'newcopy' });
    const form = new URLSearchParams(request.body.toString());
    assert.equal(form.get('fsidlist'), `[${sharedFile.fileId}]`);
    assert.equal(form.get('path'), '/MeowFilm');
    return response(`{"errno":0,"task_id":0,"extra":{"list":[{"from_fs_id":"different","to_fs_id":"old","to":"/MeowFilm/same.mp4"},{"from_fs_id":${sharedFile.fileId},"from":"/Series/same.mp4","to_fs_id":9007199254740993,"to":"/MeowFilm/same(1).mp4"}]}}`);
  });
  assert.deepEqual(await baidu.prepare({ file: sharedFile }, f.ctx), {
    file: { ...storedFile, fileId: '9007199254740993' },
  });
  assert.deepEqual(f.calls.map(call => new URL(call.url).pathname), [
    '/api/loginStatus', '/api/list', '/api/create', '/share/verify', '/share/transfer',
  ]);
  assert.equal(f.calls.some(call => /mediainfo|delete|clear|filemanager/.test(call.url)), false);
});

test('Baidu existing-directory create codes are accepted and other create failures stop transfer', async () => {
  for (const errno of [31066, -8, -9]) {
    const f = fixture(url => {
      if (url.pathname === '/api/loginStatus') return response({ login_info: { bdstoken: 'synthetic-token' } });
      if (url.pathname === '/api/list') return response({ errno: 0, list: [] });
      if (url.pathname === '/api/create') return response({ errno });
      if (url.pathname === '/share/verify') return response({ errno: 0 });
      assert.equal(url.pathname, '/share/transfer');
      return response({ errno: 0, extra: { list: [{
        from_fs_id: sharedFile.fileId, to: storedFile.path, to_fs_id: storedFile.fileId,
      }] } });
    });
    if (errno === -9) {
      await assert.rejects(baidu.prepare({ file: sharedFile }, f.ctx), { code: 'UPSTREAM_ERROR' });
      assert.deepEqual(f.calls.map(call => new URL(call.url).pathname), ['/api/loginStatus', '/api/list', '/api/create']);
    } else assert.deepEqual(await baidu.prepare({ file: sharedFile }, f.ctx), { file: storedFile });
  }
});

test('Baidu dedicated directory lookup traverses pages and matches exact path', async () => {
  const f = fixture(url => {
    if (url.pathname === '/api/loginStatus') return response({ login_info: { bdstoken: 'synthetic' } });
    if (url.pathname === '/api/list') {
      if (url.searchParams.get('page') === '1') return response({ errno: 0, list: [directoryItem('other', '/Other')], has_more: 1 });
      return response({ errno: 0, list: [directoryItem('exact', '/MeowFilm')], has_more: 0 });
    }
    if (url.pathname === '/share/verify') return response({ errno: 0 });
    assert.equal(url.pathname, '/share/transfer');
    return response({ errno: 0, extra: { list: [{ from: sharedFile.path, to: storedFile.path, to_fs_id: storedFile.fileId }] } });
  });
  assert.deepEqual(await baidu.prepare({ file: sharedFile }, f.ctx), { file: storedFile });
  assert.equal(f.calls.filter(call => new URL(call.url).pathname === '/api/list').length, 2);
  assert.equal(f.calls.some(call => new URL(call.url).pathname === '/api/create'), false);
});

function transferFixture(transfer) {
  return fixture((url, request) => {
    if (url.pathname === '/api/loginStatus') return response({ login_info: { bdstoken: 'synthetic-token' } });
    if (url.pathname === '/api/list') return response({ errno: 0, list: [directoryItem('directory', '/MeowFilm')] });
    if (url.pathname === '/share/verify') return response({ errno: 0 });
    assert.equal(url.pathname, '/share/transfer');
    return typeof transfer === 'function' ? transfer(url, request) : response(transfer);
  });
}

test('Baidu missing, ambiguous, wrong-source or invalid target mappings fail rather than select another file', async () => {
  const entry = { from_fs_id: sharedFile.fileId, from: sharedFile.path, to: storedFile.path, to_fs_id: storedFile.fileId };
  for (const value of [
    { errno: 0 },
    { errno: 0, extra: { list: [] } },
    { errno: 0, extra: { list: [{ ...entry, from_fs_id: 'different' }] } },
    { errno: 0, extra: { list: [entry, entry] } },
    { errno: 0, extra: { list: [{ ...entry, to_fs_id: undefined }] } },
    { errno: 0, extra: { list: [{ ...entry, to: undefined, to_path: storedFile.path }] } },
    { errno: 0, extra: { list: [{ ...entry, to: '/MeowFilm/../wrong/same.mp4' }] } },
    { errno: 0, extra: { list: [{ ...entry, to: '/Other/same.mp4' }] } },
  ]) {
    const f = transferFixture(value);
    await assert.rejects(baidu.play({ file: sharedFile, mode: 'streaming' }, f.ctx), { code: 'UPSTREAM_FORMAT' });
    assert.equal(f.calls.some(call => /mediainfo|delete|clear|filemanager/.test(call.url)), false);
  }
  const failed = transferFixture({ errno: 12, errmsg: 'synthetic-secret' });
  await assert.rejects(baidu.prepare({ file: sharedFile }, failed.ctx), { code: 'UPSTREAM_ERROR' });
});

test('Baidu task-only transfer replies fail without extra upstream requests or guessed paths', async () => {
  for (const transfer of [
    { errno: 0, task_id: '9007199254740997' },
    { errno: 0, task_id: '9007199254740997', extra: {} },
    { errno: 0, task_id: '9007199254740997', extra: { list: [] } },
    { errno: 0, task_id: '9007199254740997', list: [{
      from_fs_id: sharedFile.fileId, to_fs_id: storedFile.fileId, to: storedFile.path,
    }] },
  ]) {
    for (const operation of ['prepare', 'play']) {
      const f = transferFixture(transfer);
      await assert.rejects(baidu[operation]({ file: sharedFile, mode: 'streaming' }, f.ctx), { code: 'UPSTREAM_FORMAT' });
      assert.deepEqual(f.calls.map(call => new URL(call.url).pathname), [
        '/api/loginStatus', '/api/list', '/share/verify', '/share/transfer',
      ]);
    }
  }
});

test('Baidu exact extra.list transfer mapping remains authoritative when a task ID is also returned', async () => {
  const f = transferFixture({ errno: 0, task_id: '9007199254740997', extra: { list: [{
    from_fs_id: sharedFile.fileId, to_fs_id: storedFile.fileId, to: storedFile.path,
  }] } });
  assert.deepEqual(await baidu.prepare({ file: sharedFile }, f.ctx), { file: storedFile });
  assert.equal(f.calls.length, 4);
});

test('Baidu transfer encodes reference int64 IDs as exact JSON numbers and other IDs as strings', async () => {
  for (const [fileId, expected] of [
    ['9223372036854775807', '[9223372036854775807]'],
    ['9223372036854775808', '["9223372036854775808"]'],
    ['00042', '[42]'],
    ['+42', '[42]'],
    ['opaque-file-id', '["opaque-file-id"]'],
  ]) {
    const source = { ...sharedFile, fileId };
    const f = transferFixture((_url, request) => {
      assert.equal(new URLSearchParams(request.body.toString()).get('fsidlist'), expected);
      return response({ errno: 0, extra: { list: [{ from_fs_id: fileId, to_fs_id: storedFile.fileId, to: storedFile.path }] } });
    });
    assert.deepEqual(await baidu.prepare({ file: source }, f.ctx), { file: storedFile });
    assert.equal(f.calls.length, 4);
  }
});

test('Baidu cancellation interrupts an in-flight transfer without reporting a stored file or issuing later requests', async () => {
  let pending = false;
  let entered;
  const started = new Promise(resolve => { entered = resolve; });
  const f = transferFixture((_url, request) => {
    pending = true;
    entered();
    return new Promise((resolve, reject) => {
      request.signal.addEventListener('abort', () => {
        pending = false;
        reject(new PanError('CANCELLED'));
      }, { once: true });
    });
  });
  const work = baidu.prepare({ file: sharedFile }, f.ctx);
  await started;
  assert.equal(pending, true);
  f.controller.abort();
  await assert.rejects(work, { code: 'CANCELLED' });
  assert.equal(pending, false);
  assert.equal(f.calls.length, 4);
});

test('Baidu transfer transport failures remain explicit without retries or other requests', async () => {
  for (const code of ['TIMEOUT', 'NETWORK_ERROR', 'CANCELLED']) {
    const f = transferFixture(() => { throw new PanError(code); });
    await assert.rejects(baidu.prepare({ file: sharedFile }, f.ctx), { code });
    assert.equal(f.calls.length, 4);
  }
});

test('Baidu stored playback uses exact prepared path and carries required UA without Cookie to media', async () => {
  const f = fixture((url, request) => {
    if (url.pathname === '/api/mediainfo') {
      assert.equal(url.searchParams.get('path'), storedFile.path);
      assert.equal(url.searchParams.get('type'), 'M3U8_FLV_264_480');
      assert.deepEqual(Object.fromEntries(url.searchParams), {
        type: 'M3U8_FLV_264_480', path: storedFile.path, origin: 'dlna', check_blue: '1', app_id: '250528',
        devuid: 'kx1cK7VGweDrdrLiQpQRZduW5KTFvBHU|YyLyiRidC', clienttype: '80',
        channel: 'android_12_V2238A_bd-netdisk_1024266g', network_type: 'wifi', version: '12.11.9',
      });
      assert.equal(header(request, 'User-Agent'), netdiskUA);
      assert.equal(header(request, 'Content-Type'), 'application/x-www-form-urlencoded');
      assert.equal(header(request, 'Cookie'), 'BDUSS=synthetic');
      assert.equal(header(request, 'Referer'), 'https://pan.baidu.com/');
      assert.equal(header(request, 'Origin'), 'https://pan.baidu.com');
      assert.equal(header(request, 'Accept'), 'application/json, text/plain, */*');
      return response({ info: { dlink: 'https://media.invalid/start' } });
    }
    assert.equal(header(request, 'Cookie'), undefined);
    assert.equal(header(request, 'User-Agent'), playUA);
    assert.equal(request.method, 'GET');
    assert.equal(request.responseMode, 'headers');
    assert.equal(request.headers.Range, 'bytes=0-0');
    if (url.pathname === '/start') return response('', 302, { location: '/exact' });
    return response('', 206);
  });
  let prepared = 0;
  f.ctx.prepare = async input => { assert.deepEqual(input, sharedFile); prepared++; return storedFile; };
  const result = await baidu.play({ file: sharedFile, mode: 'streaming' }, f.ctx);
  assert.equal(prepared, 1);
  assert.equal(result.file.path, storedFile.path);
  assert.equal(result.url, 'https://media.invalid/exact');
  assert.match(result.headers['User-Agent'], /AndroidXMedia3/);
  assert.equal(result.mode, 'streaming');
  assert.equal(f.calls.some(call => new URL(call.url).pathname === '/share/transfer'), false);
});

test('Baidu mediainfo resolves a returned dlink even with a warning errno, and exposes missing-link errors without fallback', async () => {
  for (const errno of [undefined, 0, 12]) {
    const f = fixture(url => url.pathname === '/api/mediainfo'
      ? response({ ...(errno === undefined ? {} : { errno }), info: { dlink: '  https://media.invalid/exact  ' } })
      : response('', 206));
    const result = await baidu.play({ file: storedFile, mode: 'streaming' }, f.ctx);
    assert.equal(result.url, 'https://media.invalid/exact');
    assert.deepEqual(result.headers, { 'User-Agent': playUA });
    assert.equal(f.calls.length, 2);
  }
  for (const [value, code] of [
    [{ errno: 12, errmsg: 'synthetic-secret' }, 'UPSTREAM_ERROR'],
    [{ errno: 12, info: { dlink: ' ' } }, 'UPSTREAM_ERROR'],
    [{ errno: 0, info: {} }, 'UPSTREAM_FORMAT'],
    [{ info: {} }, 'UPSTREAM_FORMAT'],
  ]) {
    const f = fixture(() => response(value));
    await assert.rejects(baidu.play({ file: storedFile, mode: 'streaming' }, f.ctx),
      error => error.code === code && !error.message.includes('synthetic-secret'));
    assert.equal(f.calls.length, 1);
  }
});

test('Baidu media redirect cycles, downgrade and terminal failure are explicit', async () => {
  for (const behavior of ['cycle', 'downgrade', 'denied']) {
    const f = fixture(url => {
      if (url.pathname === '/api/mediainfo') return response({ info: { dlink: 'https://media.invalid/start' } });
      if (behavior === 'cycle') return response('', 302, { location: '/start' });
      if (behavior === 'downgrade') return response('', 302, { location: 'http://media.invalid/file' });
      return response('', 403);
    });
    await assert.rejects(baidu.play({ file: storedFile, mode: 'streaming' }, f.ctx), {
      code: behavior === 'cycle' ? 'UPSTREAM_FORMAT' : 'UPSTREAM_HTTP',
    });
  }
});

function qrFixture(poll) {
  const states = ['pending', 'scanned', 'confirmed'];
  return fixture((url, request) => {
    if (url.pathname !== '/channel/unicast') assert.equal(request.timeout, 30000);
    if (url.pathname === '/') return response('');
    if (url.pathname === '/v2/api/getqrcode') {
      return response(`${url.searchParams.get('callback')}(${JSON.stringify({ errno: 0, sign: 'synthetic-sign', imgurl: 'passport.baidu.com/qr-image' })});`);
    }
    if (url.pathname === '/qr-image') return { status: 200, headers: { 'content-type': 'image/png' }, body: qrPNG };
    if (url.pathname === '/channel/unicast') {
      if (poll) return poll(url, request);
      assert.equal(request.timeout, 40000);
      const state = states.shift();
      return response(`${url.searchParams.get('callback')}(${JSON.stringify(state === 'pending'
        ? { errno: 1 } : { errno: 0, channel_v: JSON.stringify({ status: state === 'scanned' ? 1 : 0, v: state === 'confirmed' ? 'synthetic-qr-ticket' : '' }) })});`);
    }
    assert.equal(url.pathname, '/v3/login/main/qrbdusslogin');
    assert.equal(url.searchParams.get('bduss'), 'synthetic-qr-ticket');
    assert.equal(request.signal.aborted, false);
    return response('', 302, { location: 'https://pan.baidu.com/', 'set-cookie': ['BDUSS=synthetic-login; Domain=.baidu.com; Path=/; Secure'] });
  }, {});
}

test('Baidu QR exposes challenge and transitions pending/scanned/confirmed without persisting in provider', async () => {
  const f = qrFixture();
  const login = await baidu.loginStart({}, f.ctx);
  assert.deepEqual(login.challenge, qrChallenge);
  assert.equal(login.expiresAt, f.ctx.now() + 180000);
  assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'pending' });
  assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'scanned' });
  assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'confirmed', credentials: { cookie: 'BDUSS=synthetic-login' } });
  assert.equal(f.patches.length, 0);
});

test('Baidu QR requests use reference parameters and session cookies to fetch the exact image and finalize once', async () => {
  let gid;
  let callback;
  let roots = 0;
  const f = fixture((url, request) => {
    assert.equal(request.method ?? 'GET', 'GET');
    assert.equal(header(request, 'User-Agent'), qrUA);
    assert.equal(header(request, 'Referer'), 'https://pan.baidu.com/');
    assert.equal(request.signal, f.controller.signal);
    if (url.pathname === '/') {
      roots++;
      if (roots === 1) return response('', 200, { 'set-cookie': [
        'BAIDUID=synthetic-shared; Domain=.baidu.com; Path=/; Secure',
        'PANONLY=synthetic-pan; Path=/; Secure',
      ] });
      assert.match(header(request, 'Cookie'), /BDUSS=synthetic-login/);
      return response('');
    }
    if (url.pathname === '/v2/api/getqrcode') {
      assert.equal(url.origin, 'https://passport.baidu.com');
      gid = url.searchParams.get('gid');
      callback = url.searchParams.get('callback');
      assert.match(gid, /^[0-9A-F]{32}$/);
      assert.match(callback, /^bd__cbs__[0-9a-f]{12}$/);
      assert.deepEqual(Object.fromEntries(url.searchParams), { lp: 'pc', tt: String(f.ctx.now()), gid, callback });
      assert.equal(header(request, 'Cookie'), 'BAIDUID=synthetic-shared');
      return response(`${callback}({"errno":0,"sign":"synthetic-sign","imgurl":"//passport.baidu.com/qr-image"});`, 200, {
        'set-cookie': ['QRSESSION=synthetic-image-session; Path=/; Secure'],
      });
    }
    if (url.pathname === '/qr-image') {
      assert.equal(url.origin, 'https://passport.baidu.com');
      assert.match(header(request, 'Cookie'), /QRSESSION=synthetic-image-session/);
      assert.equal(header(request, 'Cookie').includes('PANONLY'), false);
      return { status: 200, headers: { 'content-type': 'image/png; charset=binary' }, body: qrPNG };
    }
    if (url.pathname === '/channel/unicast') {
      assert.equal(request.timeout, 40000);
      assert.deepEqual(Object.fromEntries(url.searchParams), {
        channel_id: 'synthetic-sign', tpl: 'netdisk', apiver: 'v3', tt: String(f.ctx.now()), gid, callback,
      });
      return response({ errno: 0, channel_v: JSON.stringify(JSON.stringify({ status: 0, v: 'synthetic-qr-ticket' })) });
    }
    assert.equal(url.pathname, '/v3/login/main/qrbdusslogin');
    assert.deepEqual(Object.fromEntries(url.searchParams), {
      bduss: 'synthetic-qr-ticket', u: 'https://pan.baidu.com/', tpl: 'netdisk', apiver: 'v3', tt: String(f.ctx.now()),
    });
    return response('', 302, {
      location: 'https://pan.baidu.com/', 'set-cookie': ['BDUSS=synthetic-login; Domain=.baidu.com; Path=/; Secure'],
    });
  }, {});
  const login = await baidu.loginStart({}, f.ctx);
  assert.deepEqual(login.challenge, qrChallenge);
  const result = await baidu.loginPoll(login.session, f.ctx);
  assert.equal(result.status, 'confirmed');
  assert.match(result.credentials.cookie, /BDUSS=synthetic-login/);
  assert.match(result.credentials.cookie, /PANONLY=synthetic-pan/);
  assert.equal(result.credentials.cookie.includes('QRSESSION'), false);
  assert.deepEqual(f.calls.map(call => new URL(call.url).pathname), [
    '/', '/v2/api/getqrcode', '/qr-image', '/channel/unicast', '/v3/login/main/qrbdusslogin', '/', '/',
  ]);
  const count = f.calls.length;
  assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), result);
  assert.equal(f.calls.length, count);
  assert.equal(f.patches.length, 0);
});

test('Baidu QR missing image Content-Type uses reference image sniffing and supports normalized upstream image URLs', async () => {
  for (const image of ['passport.baidu.com/qr-image', '//passport.baidu.com/qr-image', 'https://passport.baidu.com/qr-image']) {
    const f = fixture(url => {
      if (url.pathname === '/') return response('');
      if (url.pathname === '/v2/api/getqrcode') return response({ errno: 0, sign: 'synthetic-sign', imgurl: image });
      assert.equal(url.href, 'https://passport.baidu.com/qr-image');
      return { status: 200, headers: {}, body: qrPNG };
    }, {});
    const login = await baidu.loginStart({}, f.ctx);
    assert.deepEqual(login.challenge, qrChallenge);
    assert.equal(f.calls.length, 3);
  }
});

test('Baidu QR image HTTP failures and malformed images fail before exposing a challenge or saving credentials', async () => {
  for (const [image, code] of [
    [{ status: 403, headers: {}, body: Buffer.alloc(0) }, 'UPSTREAM_HTTP'],
    [{ status: 200, headers: { 'content-type': 'text/html' }, body: Buffer.from('<html>challenge</html>') }, 'UPSTREAM_FORMAT'],
    [{ status: 200, headers: { 'content-type': 'image/png' }, body: Buffer.alloc(0) }, 'UPSTREAM_FORMAT'],
    [{ status: 200, headers: {}, body: Buffer.from('invalid image') }, 'UPSTREAM_FORMAT'],
    [{ status: 302, headers: { location: 'https://attacker.invalid/qr-image' }, body: Buffer.alloc(0) }, 'UPSTREAM_FORMAT'],
  ]) {
    const f = fixture(url => {
      if (url.pathname === '/') return response('');
      if (url.pathname === '/v2/api/getqrcode') return response({ errno: 0, sign: 'synthetic-sign', imgurl: 'passport.baidu.com/qr-image' });
      assert.equal(url.origin, 'https://passport.baidu.com');
      return image;
    }, {});
    await assert.rejects(baidu.loginStart({}, f.ctx), { code });
    assert.equal(f.calls.length, 3);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR cancellation drains an in-flight image request without publishing a challenge', async () => {
  let pending = false;
  let entered;
  const started = new Promise(resolve => { entered = resolve; });
  const f = fixture((url, request) => {
    if (url.pathname === '/') return response('');
    if (url.pathname === '/v2/api/getqrcode') return response({ errno: 0, sign: 'synthetic-sign', imgurl: 'passport.baidu.com/qr-image' });
    assert.equal(url.pathname, '/qr-image');
    return new Promise((resolve, reject) => {
      request.signal.addEventListener('abort', () => { pending = false; reject(new PanError('CANCELLED')); }, { once: true });
      pending = true;
      entered();
    });
  }, {});
  const work = baidu.loginStart({}, f.ctx);
  await started;
  assert.equal(pending, true);
  f.controller.abort();
  await assert.rejects(work, { code: 'CANCELLED' });
  assert.equal(pending, false);
  assert.equal(f.calls.length, 3);
  assert.equal(f.patches.length, 0);
});

test('Baidu QR other reference channel states remain pending without finalization and malformed states fail', async () => {
  for (const channel of [{ status: 2 }, { status: -1 }, { status: 3, v: 'unused' }]) {
    const f = qrFixture(() => response({ errno: 0, channel_v: JSON.stringify(channel) }));
    const login = await baidu.loginStart({}, f.ctx);
    assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'pending' });
    assert.equal(f.calls.length, 4);
    assert.equal(f.calls.some(call => new URL(call.url).pathname === '/v3/login/main/qrbdusslogin'), false);
    assert.equal(f.patches.length, 0);
  }
  for (const channel of ['not-json', '{"v":"ticket"}', '{"status":false}', '{"status":0}']) {
    const f = qrFixture(() => response({ errno: 0, channel_v: channel }));
    const login = await baidu.loginStart({}, f.ctx);
    await assert.rejects(baidu.loginPoll(login.session, f.ctx), { code: 'UPSTREAM_FORMAT' });
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR long-poll accepts pending after thirty seconds within its forty-second protocol budget', async () => {
  for (const elapsed of [30194, 39999]) {
    const f = qrFixture((url, request) => {
      assert.equal(request.timeout, 40000);
      assert.equal(request.signal, f.controller.signal);
      assert.ok(elapsed < request.timeout);
      f.advance(elapsed);
      return response(`${url.searchParams.get('callback')}({"errno":1});`);
    });
    const login = await baidu.loginStart({}, f.ctx);
    const startedAt = f.ctx.now();
    assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'pending' });
    assert.equal(f.ctx.now() - startedAt, elapsed);
    assert.equal(login.session.expiresAt, login.expiresAt);
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR long-poll uses the exact shorter remaining session lifetime', async () => {
  for (const remaining of [39999, 5000, 1]) {
    const f = qrFixture((url, request) => {
      assert.equal(request.timeout, remaining);
      f.advance(remaining - 1);
      return response(`${url.searchParams.get('callback')}({"errno":1});`);
    });
    const login = await baidu.loginStart({}, f.ctx);
    f.advance(180000 - remaining);
    assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'pending' });
    assert.equal(login.expiresAt - f.ctx.now(), 1);
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR long-poll completion or timeout at session expiry stays LOGIN_EXPIRED', async () => {
  for (const timeout of [false, true]) {
    const f = qrFixture((url, request) => {
      assert.equal(request.timeout, 5000);
      f.advance(5000);
      if (timeout) throw new PanError('TIMEOUT');
      return response(`${url.searchParams.get('callback')}({"errno":1});`);
    });
    const login = await baidu.loginStart({}, f.ctx);
    f.advance(175000);
    await assert.rejects(baidu.loginPoll(login.session, f.ctx), { code: 'LOGIN_EXPIRED' });
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
    assert.equal(login.session.credentials, undefined);
  }
});

test('Baidu QR long-poll preserves transport TIMEOUT and NETWORK_ERROR instead of returning pending', async () => {
  for (const code of ['TIMEOUT', 'NETWORK_ERROR']) {
    const f = qrFixture((_url, request) => {
      assert.equal(request.timeout, 40000);
      f.advance(code === 'TIMEOUT' ? 40000 : 1000);
      throw new PanError(code);
    });
    const login = await baidu.loginStart({}, f.ctx);
    await assert.rejects(baidu.loginPoll(login.session, f.ctx), { code });
    assert.ok(f.ctx.now() < login.expiresAt);
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR long-poll preserves upstream failures and malformed replies', async () => {
  for (const [value, code] of [[{ errno: 9 }, 'UPSTREAM_ERROR'], [{ malformed: true }, 'UPSTREAM_FORMAT']]) {
    const f = qrFixture(() => response(value));
    const login = await baidu.loginStart({}, f.ctx);
    await assert.rejects(baidu.loginPoll(login.session, f.ctx), { code });
    assert.equal(f.calls.length, 4);
    assert.equal(f.patches.length, 0);
  }
});

test('Baidu QR caller abort interrupts an in-flight long-poll without finalizing login', async () => {
  let pending = false;
  const f = qrFixture((_url, request) => {
    assert.equal(request.timeout, 40000);
    assert.equal(request.signal, f.controller.signal);
    return new Promise((resolve, reject) => {
      request.signal.addEventListener('abort', () => {
        pending = false;
        reject(new PanError('CANCELLED'));
      }, { once: true });
      pending = true;
    });
  });
  const login = await baidu.loginStart({}, f.ctx);
  const polling = baidu.loginPoll(login.session, f.ctx);
  assert.equal(pending, true);
  f.controller.abort();
  await assert.rejects(polling, { code: 'CANCELLED' });
  assert.equal(pending, false);
  assert.equal(f.calls.length, 4);
  assert.equal(login.session.credentials, undefined);
  assert.equal(f.patches.length, 0);
});

test('Baidu QR expiry/cancel stops polling and clears credentials', async () => {
  for (const cancel of [false, true]) {
    const f = qrFixture();
    const login = await baidu.loginStart({}, f.ctx);
    const count = f.calls.length;
    if (cancel) baidu.loginCancel(login.session);
    else f.advance(180000);
    assert.deepEqual(await baidu.loginPoll(login.session, f.ctx), { status: 'expired' });
    assert.equal(f.calls.length, count);
    assert.equal(login.session.jar.header('https://pan.baidu.com/'), '');
  }
});

test('Baidu QR rejects an off-provider login redirect before forwarding authentication', async () => {
  const f = fixture(() => response('', 302, { location: 'https://attacker.invalid/' }), {});
  await assert.rejects(baidu.loginStart({}, f.ctx), { code: 'UPSTREAM_FORMAT' });
  assert.equal(f.calls.length, 1);
});

test('Baidu QR unique redirect chain is bounded by the challenge deadline and remaining request timeout', async () => {
  const f = fixture((_url, request, count) => {
    assert.equal(request.timeout, count === 1 ? 30000 : 5000);
    f.advance(count === 1 ? 175000 : 5001);
    return response('', 302, { location: `https://pan.baidu.com/unique-${count}` });
  }, {});
  await assert.rejects(baidu.loginStart({}, f.ctx), { code: 'LOGIN_EXPIRED' });
  assert.equal(f.calls.length, 2);
  assert.equal(f.patches.length, 0);
});

test('Baidu QR transport expiry is LOGIN_EXPIRED rather than a successful expired challenge', async () => {
  const f = fixture(() => {
    f.advance(180000);
    throw new PanError('TIMEOUT');
  }, {});
  await assert.rejects(baidu.loginStart({}, f.ctx), { code: 'LOGIN_EXPIRED' });
  assert.equal(f.calls.length, 1);
});

test('Baidu cancellation and Cookie persistence failures stop subsequent operations', async () => {
  const cancelled = fixture(() => assert.fail('cancelled before upstream'));
  cancelled.controller.abort();
  await assert.rejects(baidu.list({ share }, cancelled.ctx), { code: 'CANCELLED' });
  const f = fixture(() => response({ errno: 0 }, 200, { 'set-cookie': ['BDCLND=new; Path=/'] }));
  f.ctx.updateCredentials = async () => { throw new PanError('STORAGE_ERROR'); };
  await assert.rejects(baidu.list({ share }, f.ctx), { code: 'STORAGE_ERROR' });
  assert.equal(f.calls.length, 1);
});

test('Baidu service list stamps the reference and rejects an unsupported play mode before transfer', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-baidu-'));
  const f = fixture(() => listing([{ fs_id: 'original-file', isdir: 0, server_filename: '原名#$*.mp4', path: '/原名#$*.mp4' }]));
  const service = createPanService({ dataRoot, transport: f.transport });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  const capabilities = (await service.invoke('providers')).providers.find(item => item.provider === 'baidu');
  assert.deepEqual(capabilities, {
    provider: 'baidu', credentialFields: ['cookie'], list: true, prepare: true,
    modes: ['streaming'], login: true, refresh: false,
  });
  await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'BDUSS=synthetic' } });
  const result = await service.invoke('list', { provider: 'baidu', share: { ...share, password: '' } });
  assert.equal(result.files[0].file.provider, 'baidu');
  assert.equal(result.files[0].file.accountRevision, 1);
  await assert.rejects(service.invoke('play', { provider: 'baidu', file: result.files[0].file, mode: 'download' }), { code: 'UNSUPPORTED_OPERATION' });
  await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'UNSUPPORTED_OPERATION' });
  assert.equal(f.calls.length, 1);
});

test('Baidu service list to share playback uses precise transfer mapping, not original or first same-name file', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-baidu-'));
  const f = fixture((url, request) => {
    if (url.pathname === '/share/list') return listing([{
      fs_id: sharedFile.fileId, isdir: 0, server_filename: sharedFile.name, path: sharedFile.path,
    }]);
    if (url.pathname === '/api/loginStatus') return response({ login_info: { bdstoken: 'synthetic-token' } });
    if (url.pathname === '/api/list') return response({ errno: 0, list: [directoryItem('directory', '/MeowFilm')] });
    if (url.pathname === '/share/transfer') return response({ errno: 0, extra: { list: [
      { from_fs_id: 'different-source', to_fs_id: 'wrong-first-file', to: '/MeowFilm/same.mp4' },
      { from_fs_id: sharedFile.fileId, to_fs_id: storedFile.fileId, to: storedFile.path },
    ] } });
    if (url.pathname === '/api/mediainfo') {
      assert.equal(url.searchParams.get('path'), storedFile.path);
      return response({ info: { dlink: 'https://media.invalid/exact' } });
    }
    assert.equal(url.origin, 'https://media.invalid');
    assert.equal(request.responseMode, 'headers');
    return response('', 206);
  });
  const service = createPanService({ dataRoot, transport: f.transport });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'BDUSS=synthetic' } });
  const listed = await service.invoke('list', { provider: 'baidu', share: { ...share, password: '' } });
  const played = await service.invoke('play', { provider: 'baidu', file: listed.files[0].file, mode: 'streaming' });
  assert.deepEqual(played.file, { ...storedFile, provider: 'baidu', accountRevision: 1 });
  assert.equal(played.url, 'https://media.invalid/exact');
  assert.equal(f.calls.filter(call => new URL(call.url).pathname === '/share/transfer').length, 1);
});
