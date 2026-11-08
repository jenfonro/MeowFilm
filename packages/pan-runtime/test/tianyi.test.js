import assert from 'node:assert/strict';
import { constants, generateKeyPairSync, privateDecrypt } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { tianyi } from '../src/providers/tianyi.js';
import { PanError } from '../src/errors.js';
import { createPanService } from '../src/service.js';

const share = { url: 'https://cloud.189.cn/t/SyntheticCode', password: '' };
const file = {
  kind: 'share', shareId: 'share-identity', fileId: '9223372036854775807',
  shareCode: 'SyntheticCode', password: 'p#$*', name: '原始#$*.mp4', path: '/root/原始#$*.mp4',
};

function response(value, status = 200, headers = {}) {
  return { status, headers, body: Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)), url: '' };
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

function header(request, name) {
  return Object.entries(request.headers).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1];
}

function fixture(handler, initial = { cookie: 'session=synthetic' }) {
  let credentials = { ...initial };
  const controller = new AbortController();
  const calls = [], patches = [];
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
    http: transport, signal: controller.signal, now: () => 1800000000000,
    get credentials() { return credentials; },
    async updateCredentials(patch) { patches.push(patch); credentials = { ...credentials, ...patch }; },
    async refresh() { await tianyi.refresh({}, ctx); return credentials; },
    async prepare() { assert.fail('Tianyi does not transfer'); },
  };
  return { ctx, transport, calls, patches, controller };
}

function shareInfo(overrides = {}) {
  return response({ res_code: 0, shareId: 'share-identity', fileId: 'root', fileName: 'root', isFolder: true, shareMode: '3', ...overrides });
}

function list(files = [], folders = [], count = files.length + folders.length) {
  return response({ res_code: 0, fileListAO: { fileList: files, folderList: folders, count } });
}

test('Tianyi Cookie alone supports single-file list and streaming without password login', async () => {
  const f = fixture((url, request) => {
    if (url.origin === 'https://media.invalid') {
      assert.equal(header(request, 'Cookie'), undefined);
      assert.equal(request.method, 'GET');
      assert.equal(request.responseMode, 'headers');
      assert.equal(header(request, 'Range'), 'bytes=0-0');
      return response('', 206);
    }
    assert.equal(header(request, 'Cookie'), 'session=synthetic');
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) {
      return shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name });
    }
    if (url.pathname.endsWith('getNewVlcVideoPlayUrl.action')) return response({ res_code: 0, normal: { url: 'https://media.invalid/file' } });
    assert.fail(`unexpected endpoint ${url.pathname}`);
  });
  const result = await tianyi.list({ share }, f.ctx);
  assert.equal(result.files[0].file.fileId, file.fileId);
  const played = await tianyi.play({ file: result.files[0].file, mode: 'streaming' }, f.ctx);
  assert.equal(played.mode, 'streaming');
  assert.equal(played.url, 'https://media.invalid/file');
  assert.equal(f.calls.some(call => new URL(call.url).hostname === 'open.e.189.cn'), false);
  assert.deepEqual(f.patches, []);
});

test('Tianyi reference arrays retain original names and rootless share paths', async () => {
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return response({ res_code: 0, shareId: 's', isFolder: true });
    if (url.searchParams.get('fileId') === '0') {
      return response({ res_code: 0, fileListAO: {
        folderList: [{ id: 'd', name: '  original folder  ', isFolder: false }], count: 1,
      } });
    }
    return response({ res_code: 0, fileListAO: {
      fileList: [{ id: file.fileId, name: file.name, size: 42 }], count: 1,
    } });
  });
  const result = await tianyi.list({ share }, f.ctx);
  assert.equal(result.files[0].file.fileId, file.fileId);
  assert.equal(result.files[0].name, file.name);
  assert.equal(result.files[0].path, `/  original folder  /${file.name}`);
  assert.equal(result.files[0].size, 42);
});

test('Tianyi unsupported list response aliases are not treated as reference protocol data', async () => {
  for (const value of [
    { data: { fileListAO: { fileList: [], folderList: [], count: 0 } } },
    { fileListAO: { files: [{ id: 'one', name: 'one.mp4' }] } },
    { fileListAO: { dirList: [] } },
    { fileListAO: { folders: [] } },
    { fileListAO: { fileList: [{ fileId: 'one', fileName: 'one.mp4' }] } },
    { fileListAO: { fileList: [{ file_id: 'one', file_name: 'one.mp4' }] } },
  ]) {
    const f = fixture(url => url.pathname.endsWith('getShareInfoByCodeV2.action')
      ? shareInfo() : response({ res_code: 0, ...value }));
    await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'UPSTREAM_FORMAT' });
    assert.equal(f.calls.length, 2);
  }
});

test('Tianyi reference video extensions are recognized without changing original names', async () => {
  const names = ['  original.MP4  ', 'film.mkv', 'film.MoV', 'film.iso', 'film.FLV'];
  const f = fixture(url => url.pathname.endsWith('getShareInfoByCodeV2.action') ? shareInfo()
    : list([...names, 'film.avi', 'film.ts', 'film.m3u8', 'notes.txt'].map((name, index) => ({ id: `f${index}`, name }))));
  const result = await tianyi.list({ share }, f.ctx);
  assert.deepEqual(result.files.map(item => item.name), names);
  assert.deepEqual(result.files.map(item => item.file.name), names);
});

test('Tianyi absent folder marker lists before using an evidenced single-file fallback', async () => {
  const f = fixture(url => url.pathname.endsWith('getShareInfoByCodeV2.action')
    ? response({ res_code: 0, shareId: 's', fileId: file.fileId, fileName: file.name }) : list());
  const result = await tianyi.list({ share }, f.ctx);
  assert.equal(result.files[0].path, `/${file.name}`);
  assert.equal(result.files[0].file.fileId, file.fileId);
  assert.equal(f.calls.length, 2);
});

test('Tianyi list consumes all declared pages, authoritative folders and a directory chain beyond old depth limits', async () => {
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo();
    const parent = url.searchParams.get('fileId');
    const page = Number(url.searchParams.get('pageNum'));
    if (parent === 'root') {
      if (page === 1) return list([], [{ id: 'd1', name: 'folder', isFolder: false }], 2);
      return response(`{"res_code":0,"fileListAO":{"fileList":[{"id":9223372036854775807,"name":"original #$*.mp4","size":9007199254740995}],"folderList":[],"count":2}}`);
    }
    const depth = Number(parent.slice(1));
    if (depth < 61) return list([], [{ id: `d${depth + 1}`, name: `depth${depth + 1}` }, { id: 'root', name: 'loop' }]);
    return list([
      { id: 'other-file', name: 'original #$*.mp4', size: 42 },
      { id: 'text', name: 'skip.txt' },
    ]);
  });
  const result = await tianyi.list({ share }, f.ctx);
  assert.equal(result.files.length, 2);
  assert.equal(result.files[0].file.fileId, '9223372036854775807');
  assert.equal(result.files[0].size, '9007199254740995');
  assert.equal(result.files[0].name, 'original #$*.mp4');
  assert.match(result.files[1].path, /^\/root\/folder\/depth2\//);
  assert.ok(result.files[1].path.endsWith('/original #$*.mp4'));
  assert.notEqual(result.files[0].file.fileId, result.files[1].file.fileId);
  assert.ok(f.calls.length > 61);
});

test('Tianyi remembered positive count cannot silently disappear on a later page', async () => {
  let page = 0;
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo();
    if (++page === 1) return list([{ id: 'first', name: 'first.mp4' }], [], 2);
    return response({ res_code: 0, fileListAO: { fileList: [], folderList: [] } });
  });
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'UPSTREAM_FORMAT' });
});

test('Tianyi list traverses beyond the reference 500-page stop without a silent truncation', async () => {
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo();
    const page = Number(url.searchParams.get('pageNum'));
    return list([{ id: `file-${page}`, name: `page-${page}.mp4` }], [], 501);
  });
  assert.equal((await tianyi.list({ share }, f.ctx)).files.length, 501);
  assert.equal(f.calls.length, 502);
});

test('Tianyi URL accessCode is decoded once and retained with explicit override precedence', async () => {
  for (const location of [
    'https://cloud.189.cn/t/SyntheticCode?accessCode=url%2B%23%24*',
    'https://h5.cloud.189.cn/web/share?shareCode=SyntheticCode&accessCode=url%2B%23%24*',
    'https://cloud.189.cn/web/share.html#/share?shareCode=SyntheticCode&accessCode=url%2B%23%24*',
  ]) {
    for (const override of [undefined, 'explicit-code', '']) {
      const expected = override ?? 'url+#$*';
      const f = fixture(url => {
        assert.equal(url.searchParams.get('accessCode'), expected || null);
        if (url.pathname.endsWith('checkAccessCode.action')) return response({ res_code: 0, shareId: 'verified-share' });
        return shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name });
      });
      const result = await tianyi.list({ share: {
        url: location, ...(override === undefined ? {} : { password: override }),
      } }, f.ctx);
      assert.equal(result.share.password, expected);
      assert.equal(result.files[0].file.password, expected);
      assert.equal(f.calls.length, expected ? 2 : 1);
    }
  }
});

test('Tianyi share URL aliases absent from the reference are rejected before requests', async () => {
  const f = fixture(() => assert.fail('unexpected upstream'));
  for (const location of [
    'https://h5.cloud.189.cn/t/SyntheticCode',
    'https://h5.cloud.189.cn/web/share?code=SyntheticCode',
    'https://cloud.189.cn/web/share?code=SyntheticCode',
    'https://cloud.189.cn/web/share.html#/share?code=SyntheticCode',
  ]) {
    await assert.rejects(tianyi.list({ share: { url: location } }, f.ctx), { code: 'INVALID_REQUEST' });
  }
  assert.equal(f.calls.length, 0);
});

test('Tianyi duplicate continuing pages, missing arrays and declared-but-empty pages fail explicitly', async () => {
  for (const kind of ['repeat', 'missing', 'empty']) {
    const f = fixture(url => {
      if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo();
      if (kind === 'repeat') return list([{ id: 'one', name: 'one.mp4' }], [], 2);
      if (kind === 'empty') return list([], [], 2);
      return response({ res_code: 0, fileListAO: { count: 0 } });
    });
    await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'UPSTREAM_FORMAT' });
    assert.ok(f.calls.length <= 3);
  }
});

test('Tianyi explicit empty folder succeeds without a fabricated single-file fallback', async () => {
  const f = fixture(url => url.pathname.endsWith('getShareInfoByCodeV2.action') ? shareInfo() : list());
  assert.deepEqual((await tianyi.list({ share }, f.ctx)).files, []);
});

test('Tianyi split-folder duplicates remain directories even with a video extension', async () => {
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo();
    if (url.searchParams.get('fileId') === 'root') {
      return list([{ id: 'folder-id', name: 'video.mp4' }], [{ id: 'folder-id', name: 'video.mp4' }], 1);
    }
    return list([{ id: 'real-file', name: 'inside.mkv' }]);
  });
  const result = await tianyi.list({ share }, f.ctx);
  assert.deepEqual(result.files.map(item => item.path), ['/root/video.mp4/inside.mkv']);
});

test('Tianyi extraction code is verified and survives list/play reference roundtrip', async () => {
  const f = fixture(url => {
    if (url.pathname.endsWith('checkAccessCode.action')) {
      assert.equal(url.searchParams.get('accessCode'), file.password);
      return response({ res_code: 0, shareId: 'verified-share' });
    }
    assert.equal(url.searchParams.get('accessCode'), file.password);
    return shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name });
  });
  const result = await tianyi.list({ share: { ...share, password: file.password } }, f.ctx);
  assert.equal(result.files[0].file.shareId, 'verified-share');
  assert.equal(result.files[0].file.password, file.password);
  assert.equal(result.files[0].file.shareCode, 'SyntheticCode');
});

test('Tianyi share requests use the reference parameters and endpoint-specific Referer', async () => {
  const expected = [
    ['/api/open/share/checkAccessCode.action', 'https://cloud.189.cn/web/share?code=SyntheticCode',
      { key: 'noCache', shareCode: 'SyntheticCode', accessCode: 'code' }],
    ['/api/open/share/getShareInfoByCodeV2.action', share.url,
      { key: 'noCache', shareCode: 'SyntheticCode', accessCode: 'code' }],
    ['/api/open/share/listShareDir.action', 'https://cloud.189.cn/',
      { key: 'noCache', noCache: '1800000000000', shareId: 'verified-share', fileId: '0', shareDirFileId: '0',
        isFolder: 'true', iconOption: '5', shareMode: '3', pageNum: '1', pageSize: '200',
        orderBy: 'lastOpTime', descending: 'true', accessCode: 'code' }],
  ];
  const f = fixture((url, request, count) => {
    const [path, referer, parameters] = expected[count - 1];
    assert.equal(url.pathname, path);
    assert.deepEqual(Object.fromEntries(url.searchParams), parameters);
    assert.equal(request.method, 'GET');
    assert.equal(request.body, undefined);
    assert.equal(header(request, 'Referer'), referer);
    assert.equal(header(request, 'Accept'), 'application/json;charset=UTF-8');
    assert.equal(header(request, 'Accept-Encoding'), 'identity');
    assert.match(header(request, 'User-Agent'), /Chrome\/144\.0\.0\.0/);
    assert.equal(header(request, 'Cookie'), 'session=synthetic');
    if (count === 1) return response({ res_code: 0, shareId: 'verified-share' });
    if (count === 2) return shareInfo({ fileId: '  ', shareMode: '  ' });
    return list();
  });
  const result = await tianyi.list({ share: { ...share, password: '  code  ' } }, f.ctx);
  assert.deepEqual(result.files, []);
  assert.equal(result.share.password, '  code  ');
  assert.equal(f.calls.length, expected.length);
});

test('Tianyi missing share identity without an extraction code remains an authentication error', async () => {
  const f = fixture(() => response({ res_code: 0, fileId: 'root', fileName: 'root', isFolder: true }));
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'AUTH_REQUIRED' });
  assert.equal(f.calls.length, 1);
});

test('Tianyi requested download and streaming only retry HTTP FileTooLarge on the same endpoint', async () => {
  for (const mode of ['download', 'streaming']) {
    for (const errorBody of [{ res_code: 'FileTooLarge' }, 'file too large']) {
      const f = fixture((url, request) => {
        if (url.origin === 'https://media.invalid') {
          assert.equal(header(request, 'Cookie'), undefined);
          assert.equal(request.method, 'GET');
          assert.equal(request.responseMode, 'headers');
          return response('', 206);
        }
        assert.equal(url.pathname, mode === 'streaming'
          ? '/api/portal/getNewVlcVideoPlayUrl.action' : '/api/open/file/getFileDownloadUrl.action');
        assert.equal(url.searchParams.get('fileId'), file.fileId);
        assert.equal(url.searchParams.get('shareId'), file.shareId);
        assert.equal(request.method, 'GET');
        assert.equal(header(request, 'Referer'), 'https://cloud.189.cn/');
        if (mode === 'download') {
          assert.equal(url.searchParams.get('accessCode'), file.password);
          assert.equal(url.searchParams.has('type'), false);
        } else {
          assert.equal(url.searchParams.get('type'), '4');
          assert.equal(url.searchParams.has('accessCode'), false);
        }
        if (url.searchParams.get('dt') === '1') return response(errorBody, 400);
        assert.equal(url.searchParams.get('dt'), '3');
        return response(mode === 'streaming' ? { res_code: 0, fileDownloadUrl: 'https://media.invalid/exact' }
          : { res_code: 0, data: { downloadUrl: 'https://media.invalid/exact' } });
      });
      const result = await tianyi.play({ file, mode }, f.ctx);
      assert.equal(result.mode, mode);
      assert.equal(result.url, 'https://media.invalid/exact');
      assert.equal(f.calls.length, 3);
    }
  }
});

test('Tianyi FileTooLarge business errors, unrelated HTTP errors and repeated HTTP failures stop without changing mode', async () => {
  for (const mode of ['streaming', 'download']) {
    for (const [failure, error, attempts] of [
      [response({ res_code: 'FileTooLarge' }), 'UPSTREAM_ERROR', 1],
      [response({ res_code: 1, res_message: 'FileTooLarge' }), 'UPSTREAM_ERROR', 1],
      [response('unrelated upstream error', 400), 'UPSTREAM_HTTP', 1],
      [response({ res_code: 'FileTooLarge' }, 400), 'UPSTREAM_ERROR', 2],
    ]) {
      const f = fixture(url => {
        assert.equal(url.pathname, mode === 'streaming'
          ? '/api/portal/getNewVlcVideoPlayUrl.action' : '/api/open/file/getFileDownloadUrl.action');
        assert.equal(url.searchParams.get('fileId'), file.fileId);
        assert.equal(url.searchParams.get('shareId'), file.shareId);
        return failure;
      });
      await assert.rejects(tianyi.play({ file, mode }, f.ctx), { code: error });
      assert.equal(f.calls.length, attempts);
      assert.deepEqual(f.calls.map(call => new URL(call.url).searchParams.get('dt')), attempts === 1 ? ['1'] : ['1', '3']);
    }
  }
});

test('Tianyi VLC raw JSON parse failures retain its reference FileTooLarge retry without adding one to download', async () => {
  for (const mode of ['streaming', 'download']) {
    const f = fixture(url => {
      if (url.origin === 'https://media.invalid') return response('', 206);
      assert.equal(url.pathname, mode === 'streaming'
        ? '/api/portal/getNewVlcVideoPlayUrl.action' : '/api/open/file/getFileDownloadUrl.action');
      if (url.searchParams.get('dt') === '1') return response('FileTooLarge');
      return response({ res_code: 0, fileDownloadUrl: 'https://media.invalid/exact' });
    });
    const pending = tianyi.play({ file, mode }, f.ctx);
    if (mode === 'streaming') {
      assert.equal((await pending).url, 'https://media.invalid/exact');
      assert.equal(f.calls.length, 3);
    } else {
      await assert.rejects(pending, { code: 'UPSTREAM_FORMAT' });
      assert.equal(f.calls.length, 1);
    }
  }
});

test('Tianyi download resolves reference fields and containers from a single exact-file response', async () => {
  const fields = ['fileDownloadUrl', 'downloadUrl', 'url', 'fileDownloadUrlHttp', 'fileDownloadUrlHttps'];
  for (const field of fields) {
    for (const container of ['root', 'data', 'Data']) {
      const link = `https://media.invalid/${field}`;
      const address = { [field]: `  ${link}  ` };
      const value = { resCode: ' 0 ', ...(container === 'root' ? address : { [container]: address }) };
      const f = fixture((url, request) => {
        if (url.origin === 'https://cloud.189.cn') {
          assert.equal(url.pathname, '/api/open/file/getFileDownloadUrl.action');
          assert.deepEqual(Object.fromEntries(url.searchParams),
            { shareId: file.shareId, fileId: file.fileId, dt: '1', accessCode: file.password });
          return response(value);
        }
        assert.equal(header(request, 'Cookie'), undefined);
        return response('', 206);
      });
      const result = await tianyi.play({ file, mode: 'download' }, f.ctx);
      assert.equal(result.url, link);
      assert.equal(result.mode, 'download');
      assert.deepEqual(result.file, file);
      assert.equal(f.calls.length, 2);
    }
  }
});

test('Tianyi download field priority skips blank and non-string values without inventing containers', async () => {
  for (const [address, expected] of [
    [{ fileDownloadUrl: 'https://media.invalid/first', downloadUrl: 'https://media.invalid/second' }, '/first'],
    [{ data: { fileDownloadUrl: '', downloadUrl: '  ', url: 12, fileDownloadUrlHttp: false,
      fileDownloadUrlHttps: 'https://media.invalid/last' } }, '/last'],
    [{ data: { downloadUrl: 'https://media.invalid/nested' }, fileDownloadUrl: 'https://media.invalid/root' }, '/nested'],
    [{ data: null, Data: { downloadUrl: 'https://media.invalid/ignored' }, url: 'https://media.invalid/root' }, '/root'],
    [{ data: 'invalid-container', url: 'https://media.invalid/root' }, '/root'],
    [{ data: [], url: 'https://media.invalid/root' }, '/root'],
  ]) {
    const f = fixture(url => url.origin === 'https://cloud.189.cn' ? response({ res_code: 0, ...address }) : response('', 206));
    assert.equal((await tianyi.play({ file, mode: 'download' }, f.ctx)).url, `https://media.invalid${expected}`);
    assert.equal(f.calls.length, 2);
  }
});

test('Tianyi absent download URLs fail without guessing another file or requesting streaming', async () => {
  for (const address of [
    {},
    { fileDownloadUrl: '', downloadUrl: '  ' },
    { data: {}, fileDownloadUrl: 'https://media.invalid/root-is-not-selected' },
    { data: [{ fileId: 'other-file', fileDownloadUrl: 'https://media.invalid/other' }] },
  ]) {
    const f = fixture(url => {
      assert.equal(url.pathname, '/api/open/file/getFileDownloadUrl.action');
      assert.equal(url.searchParams.get('fileId'), file.fileId);
      return response({ res_code: 0, ...address });
    });
    await assert.rejects(tianyi.play({ file, mode: 'download' }, f.ctx), { code: 'UPSTREAM_FORMAT' });
    assert.equal(f.calls.length, 1);
  }
});

test('Tianyi VLC uses fileDownloadUrl then normal.url without requesting download or using its aliases', async () => {
  for (const [address, expected] of [
    [{ fileDownloadUrl: 'https://media.invalid/first', normal: { url: 'https://media.invalid/second' } }, '/first'],
    [{ fileDownloadUrl: '  ', normal: { url: ' https://media.invalid/normal ' } }, '/normal'],
    [{ normal: { url: 'https://media.invalid/normal' } }, '/normal'],
    [{ downloadUrl: 'https://media.invalid/download' }, null],
    [{ data: { fileDownloadUrl: 'https://media.invalid/download' } }, null],
  ]) {
    const f = fixture(url => {
      if (url.origin === 'https://cloud.189.cn') {
        assert.equal(url.pathname, '/api/portal/getNewVlcVideoPlayUrl.action');
        assert.deepEqual(Object.fromEntries(url.searchParams),
          { shareId: file.shareId, fileId: file.fileId, dt: '1', type: '4' });
        return response({ res_code: 0, ...address });
      }
      return response('', 206);
    });
    const pending = tianyi.play({ file, mode: 'streaming' }, f.ctx);
    if (expected) assert.equal((await pending).url, `https://media.invalid${expected}`);
    else await assert.rejects(pending, { code: 'UPSTREAM_FORMAT' });
    assert.equal(f.calls.length, expected ? 2 : 1);
  }
});

test('Tianyi successful API result codes follow the reference rather than login-only success aliases', async () => {
  for (const code of [0, '0', ' 0 ', '00', '0000']) {
    const f = fixture(() => shareInfo({ res_code: code, isFolder: false, fileId: file.fileId, fileName: file.name }));
    const pending = tianyi.list({ share }, f.ctx);
    if (String(code).trim() === '0') assert.equal((await pending).files.length, 1);
    else await assert.rejects(pending, { code: 'UPSTREAM_ERROR' });
    assert.equal(f.calls.length, 1);
  }
});

test('Tianyi share metadata requires res_code while the address APIs accept the reference resCode alias', async () => {
  const f = fixture(() => response({ resCode: 0, shareId: file.shareId, fileId: file.fileId, fileName: file.name, isFolder: false }));
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'UPSTREAM_FORMAT' });
  assert.equal(f.calls.length, 1);
  const vlc = fixture(url => url.origin === 'https://cloud.189.cn'
    ? response({ resCode: 0, normal: { url: 'https://media.invalid/exact' } }) : response('', 206));
  assert.equal((await tianyi.play({ file, mode: 'streaming' }, vlc.ctx)).url, 'https://media.invalid/exact');
  assert.equal(vlc.calls.length, 2);
});

test('Tianyi ordinary API Set-Cookie does not masquerade as the reference password refresh', async () => {
  const f = fixture(() => response({ res_code: 0, shareId: file.shareId, fileId: file.fileId, fileName: file.name, isFolder: false },
    200, { 'set-cookie': ['session=unrelated; Path=/; Secure'] }));
  assert.equal((await tianyi.list({ share }, f.ctx)).files.length, 1);
  assert.equal(f.ctx.credentials.cookie, 'session=synthetic');
  assert.deepEqual(f.patches, []);
});

test('Tianyi Cookie-only session expiry is AUTH_REQUIRED without password login', async () => {
  for (const value of [
    response({ res_code: 'InvalidSessionKey' }),
    response('login expired', 401),
    response('userSessionBO is null', 400),
    response({ res_code: -1, res_message: 'userSessionBO is null' }),
  ]) {
    const f = fixture(() => value);
    await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'AUTH_REQUIRED' });
    assert.equal(f.calls.length, 1);
  }
});

test('Tianyi configured password recovery retries the same request with the new Cookie', async () => {
  for (const expired of [
    response({ res_code: 'InvalidSessionKey', res_message: 'session expired' }),
    response('userSessionBO is null', 400),
  ]) {
    const f = fixture((_url, request, count) => {
      if (count === 1) return expired;
      assert.equal(header(request, 'Cookie'), 'session=fresh');
      return shareInfo({ isFolder: false, fileName: 'one.mp4' });
    }, { cookie: 'session=old', username: 'synthetic-user', password: ' synthetic pass ' });
    let refreshes = 0;
    f.ctx.refresh = async () => {
      refreshes++;
      await f.ctx.updateCredentials({ cookie: 'session=fresh' });
      return f.ctx.credentials;
    };
    const result = await tianyi.list({ share }, f.ctx);
    assert.equal(result.files.length, 1);
    assert.equal(refreshes, 1);
    assert.equal(f.calls[0].url, f.calls[1].url);
    assert.equal(f.ctx.credentials.password, ' synthetic pass ');
  }
});

test('Tianyi persistent session expiry is retried only once and Cookie-only refresh is not a fake success', async () => {
  const f = fixture(() => response({ res_code: 'InvalidSessionKey' }),
    { cookie: 'session=old', username: 'synthetic-user', password: 'password' });
  let refreshes = 0;
  f.ctx.refresh = async () => { refreshes++; await f.ctx.updateCredentials({ cookie: 'session=fresh' }); };
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'AUTH_REQUIRED' });
  assert.equal(refreshes, 1);
  assert.equal(f.calls.length, 2);
  const cookieOnly = fixture(() => assert.fail('unexpected upstream'));
  await assert.rejects(tianyi.refresh({}, cookieOnly.ctx), { code: 'NOT_CONFIGURED' });
  assert.equal(cookieOnly.calls.length, 0);
});

function passwordFixture({
  challenge = false, persistFailure = false, hostile = false,
  app = {}, result = 0, pre = '{SYNTHETIC}', expectedPre = '{SYNTHETIC}',
  expectedApp = {}, paddedContext = false,
} = {}) {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 1024 });
  const publicPEM = publicKey.export({ type: 'spki', format: 'pem' }).toString();
  const submitted = [];
  const password = '  exact-password \t';
  const f = fixture((url, request) => {
    assert.match(header(request, 'User-Agent'), /Chrome\/144\.0\.0\.0/);
    assert.equal(header(request, 'Accept-Encoding'), 'identity');
    if (url.pathname.endsWith('encryptConf.do')) {
      assert.equal(request.method, 'GET');
      assert.deepEqual(Object.fromEntries(url.searchParams), { appId: '8025431004', timeStamp: '1800000000000' });
      assert.equal(header(request, 'Referer'), 'https://open.e.189.cn');
      assert.equal(header(request, 'Accept'), 'application/json, text/plain, */*');
      return response({ result, data: { pubKey: publicPEM, pre } });
    }
    if (url.pathname.endsWith('loginUrl.action')) {
      assert.equal(request.method, 'GET');
      assert.equal(url.searchParams.get('redirectURL'), 'https://cloud.189.cn/main.action');
      assert.equal(header(request, 'Referer'), 'https://cloud.189.cn/');
      return response('', 302, {
        location: paddedContext
          ? 'https://open.e.189.cn/login?lt=%20synthetic-lt%20&reqId=%20synthetic-req%20&appId=%20cloud%20'
          : 'https://open.e.189.cn/login?lt=synthetic-lt&reqId=synthetic-req&appId=cloud',
        'set-cookie': ['cloudNonce=nonce; Path=/; Secure'],
      });
    }
    if (url.pathname === '/login') {
      assert.equal(request.headers.Cookie, '');
      return response('', 200, { 'set-cookie': ['authNonce=nonce; Path=/; Secure'] });
    }
    if (url.pathname.endsWith('appConf.do')) {
      assert.equal(request.method, 'POST');
      assert.equal(header(request, 'Content-Type'), 'application/x-www-form-urlencoded');
      assert.equal(header(request, 'Referer'), 'https://open.e.189.cn');
      assert.deepEqual(Object.fromEntries(new URLSearchParams(request.body)), { version: '2.0', appKey: 'cloud' });
      assert.equal(request.headers.Lt, 'synthetic-lt');
      assert.equal(request.headers.Reqid, 'synthetic-req');
      assert.match(request.headers.Cookie, /authNonce=nonce/);
      return response({ result, data: {
        returnUrl: 'https://cloud.189.cn/main.action', paramId: 'synthetic-param', clientType: 10020, isOauth2: true, ...app,
      } });
    }
    if (url.pathname.endsWith('loginSubmit.do')) {
      assert.equal(request.method, 'POST');
      assert.equal(header(request, 'Content-Type'), 'application/x-www-form-urlencoded');
      assert.equal(header(request, 'Referer'), 'https://open.e.189.cn');
      assert.equal(request.headers.Lt, 'synthetic-lt');
      assert.equal(request.headers.Reqid, 'synthetic-req');
      const form = new URLSearchParams(request.body.toString());
      const decrypt = field => {
        const value = form.get(field);
        assert.equal(value.startsWith(expectedPre), true);
        assert.match(value.slice(expectedPre.length), /^[0-9A-F]+$/);
        const block = privateDecrypt({ key: privateKey, padding: constants.RSA_NO_PADDING }, Buffer.from(value.slice(expectedPre.length), 'hex'));
        assert.equal(block[0], 0);
        assert.equal(block[1], 2);
        const separator = block.indexOf(0, 2);
        assert.ok(separator >= 10);
        return block.subarray(separator + 1).toString('utf8');
      };
      const plain = { username: decrypt('userName'), password: decrypt('password') };
      submitted.push(plain);
      assert.deepEqual({ ...Object.fromEntries(form), userName: plain.username, password: plain.password }, {
        appKey: 'cloud', version: 'v2.0', apToken: '', accountType: '01', userName: 'synthetic-user', password,
        validateCode: '', captchaToken: '', returnUrl: 'https://cloud.189.cn/main.action', mailSuffix: '@189.cn',
        paramId: 'synthetic-param', dynamicCheck: 'FALSE', clientType: '10020', cb_SaveName: '3', isOauth2: 'true',
        state: '', ...expectedApp,
      });
      if (challenge) return response({ result: -2, msg: '验证码 required' });
      return response({ result, toUrl: hostile ? 'https://attacker.invalid/callback' : 'https://cloud.189.cn/complete' });
    }
    assert.equal(url.pathname, '/complete');
    assert.match(request.headers.Cookie, /cloudNonce=nonce/);
    assert.equal(request.headers.Lt, undefined);
    assert.equal(request.headers.Reqid, undefined);
    assert.equal(header(request, 'Referer'), undefined);
    return response('', 200, { 'set-cookie': ['session=fresh; Path=/; Secure'] });
  }, { username: 'synthetic-user', password });
  if (persistFailure) f.ctx.updateCredentials = async () => { throw new PanError('STORAGE_ERROR'); };
  return { ...f, submitted, password };
}

test('Tianyi password login uses RSA PKCS#1 v1.5 and preserves exact password bytes', async () => {
  const f = passwordFixture();
  assert.deepEqual(await tianyi.refresh({}, f.ctx), { refreshed: true });
  assert.deepEqual(f.submitted, [{ username: 'synthetic-user', password: f.password }]);
  assert.match(f.ctx.credentials.cookie, /session=fresh/);
  assert.equal(f.ctx.credentials.password, f.password);
  assert.deepEqual(f.patches.map(patch => Object.keys(patch)), [['cookie']]);
});

test('Tianyi password login normalizes reference metadata and keeps the exact password', async () => {
  for (const options of [
    { result: ' 00 ', pre: '  {SYNTHETIC}  ', paddedContext: true, app: {
      accountType: ' 01 ', mailSuffix: ' @189.cn ', clientType: ' +010020 ', isOauth2: 2,
      returnUrl: ' https://cloud.189.cn/main.action ', paramId: ' synthetic-param ',
    } },
    { result: ' 0000 ', pre: '  ', expectedPre: '{RSA}', app: {
      accountType: '', mailSuffix: '', clientType: 'invalid', isOauth2: 'NO',
    }, expectedApp: { isOauth2: 'false' } },
    { app: { clientType: 10021.9, isOauth2: 'yes' }, expectedApp: { clientType: '10021' } },
    { app: { clientType: 0, isOauth2: 0 }, expectedApp: { clientType: '0', isOauth2: 'false' } },
  ]) {
    const f = passwordFixture(options);
    await tianyi.refresh({}, f.ctx);
    assert.deepEqual(f.submitted, [{ username: 'synthetic-user', password: f.password }]);
    assert.deepEqual(f.patches.map(patch => Object.keys(patch)), [['cookie']]);
  }
});

test('Tianyi password-only list obtains one RSA session before its first share request', async () => {
  const f = passwordFixture();
  const loginRequest = f.transport.request.bind(f.transport);
  let shareRequests = 0;
  f.transport.request = async request => {
    if (new URL(request.url).pathname.endsWith('getShareInfoByCodeV2.action')) {
      f.calls.push(request);
      shareRequests++;
      assert.match(header(request, 'Cookie'), /session=fresh/);
      return shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name });
    }
    return loginRequest(request);
  };
  assert.equal((await tianyi.list({ share }, f.ctx)).files[0].file.fileId, file.fileId);
  assert.equal(shareRequests, 1);
  assert.equal(f.submitted.length, 1);
});

test('Tianyi password challenge, persistence failure and hostile redirect never claim refresh success', async () => {
  for (const [options, code] of [
    [{ challenge: true }, 'CHALLENGE_REQUIRED'],
    [{ persistFailure: true }, 'STORAGE_ERROR'],
    [{ hostile: true }, 'UPSTREAM_FORMAT'],
  ]) {
    const f = passwordFixture(options);
    await assert.rejects(tianyi.refresh({}, f.ctx), { code });
    assert.equal(f.calls.some(call => new URL(call.url).hostname === 'attacker.invalid'), false);
  }
});

test('Tianyi unsupported mode, missing configuration, cancellation and bad share fail before requests', async () => {
  const f = fixture(() => assert.fail('unexpected upstream'), {});
  await assert.rejects(tianyi.play({ file, mode: 'other' }, f.ctx), { code: 'UNSUPPORTED_OPERATION' });
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'NOT_CONFIGURED' });
  await assert.rejects(tianyi.refresh({}, f.ctx), { code: 'NOT_CONFIGURED' });
  await assert.rejects(tianyi.list({ share: { url: 'https://attacker.invalid/t/SyntheticCode' } }, f.ctx), { code: 'INVALID_REQUEST' });
  f.controller.abort();
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'CANCELLED' });
  assert.equal(f.calls.length, 0);
});

test('Tianyi whitespace-only passwords are rejected before login while supplied Cookie still works', async () => {
  const f = fixture(() => assert.fail('unexpected upstream'), { username: 'synthetic-user', password: '  \t ' });
  await assert.rejects(tianyi.refresh({}, f.ctx), { code: 'NOT_CONFIGURED' });
  await assert.rejects(tianyi.list({ share }, f.ctx), { code: 'NOT_CONFIGURED' });
  assert.equal(f.calls.length, 0);
  const cookieOnly = fixture(() => shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name }),
    { cookie: 'session=synthetic', username: 'synthetic-user', password: '  \t ' });
  assert.equal((await tianyi.list({ share }, cookieOnly.ctx)).files.length, 1);
  assert.equal(cookieOnly.calls.length, 1);
});

test('Tianyi cancellation after an awaited share or media response cannot return success', async () => {
  for (const stage of ['list', 'media']) {
    const entered = deferred(), release = deferred();
    const f = fixture(async url => {
      if (stage === 'media' && url.origin === 'https://cloud.189.cn') {
        return response({ res_code: 0, fileDownloadUrl: 'https://media.invalid/file' });
      }
      entered.resolve();
      await release.promise;
      return stage === 'list' ? shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name }) : response('', 206);
    });
    const pending = stage === 'list' ? tianyi.list({ share }, f.ctx) : tianyi.play({ file, mode: 'streaming' }, f.ctx);
    const rejected = assert.rejects(pending, { code: 'CANCELLED' });
    await entered.promise;
    f.controller.abort();
    release.resolve();
    await rejected;
    assert.deepEqual(f.patches, []);
  }
});

test('Tianyi media redirects are relative-safe and reject failure, cycles and HTTPS downgrade', async () => {
  for (const behavior of ['relative', 'denied', 'cycle', 'downgrade']) {
    const f = fixture(url => {
      if (url.origin === 'https://cloud.189.cn') return response({ res_code: 0, fileDownloadUrl: 'https://media.invalid/start' });
      if (behavior === 'denied') return response('', 403);
      if (behavior === 'cycle') return response('', 302, { location: '/start' });
      if (behavior === 'downgrade') return response('', 302, { location: 'http://media.invalid/end' });
      return url.pathname === '/start' ? response('', 302, { location: '/exact' }) : response('', 206);
    });
    const pending = tianyi.play({ file, mode: 'streaming' }, f.ctx);
    if (behavior === 'relative') assert.equal((await pending).url, 'https://media.invalid/exact');
    else await assert.rejects(pending, { code: behavior === 'cycle' ? 'UPSTREAM_FORMAT' : 'UPSTREAM_HTTP' });
    for (const call of f.calls.filter(item => new URL(item.url).origin === 'https://media.invalid')) {
      assert.equal(call.method, 'GET');
      assert.equal(call.responseMode, 'headers');
      assert.equal(header(call, 'Range'), 'bytes=0-0');
    }
  }
});

test('Tianyi upstream errors are closed and never copy account text or response bodies', async () => {
  const secret = 'synthetic-account-do-not-echo';
  const f = fixture(() => response({ res_code: 42, res_message: secret }));
  await assert.rejects(tianyi.list({ share }, f.ctx), error => error.code === 'UPSTREAM_ERROR' && !error.message.includes(secret));
});

test('Tianyi service Cookie persistence and list/play references survive reopening', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-tianyi-'));
  const f = fixture(url => {
    if (url.pathname.endsWith('getShareInfoByCodeV2.action')) return shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name });
    if (url.pathname.endsWith('getFileDownloadUrl.action')) return response({ res_code: 0, fileDownloadUrl: 'https://media.invalid/file' });
    assert.equal(url.origin, 'https://media.invalid');
    return response('', 206);
  });
  const first = createPanService({ dataRoot, transport: f.transport });
  t.after(() => rmSync(dataRoot, { recursive: true, force: true }));
  await first.invoke('settings.set', { provider: 'tianyi', credentials: { cookie: 'session=synthetic' } });
  const listed = await first.invoke('list', { provider: 'tianyi', share });
  const reference = listed.files[0].file;
  assert.equal(reference.provider, 'tianyi');
  assert.equal(reference.fileId, file.fileId);
  await first.close();
  const saved = JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'));
  assert.equal(saved.accounts.tianyi.credentials.cookie, 'session=synthetic');
  const reopened = createPanService({ dataRoot, transport: f.transport });
  t.after(async () => reopened.close());
  const result = await reopened.invoke('play', { provider: 'tianyi', file: reference, mode: 'download' });
  assert.equal(result.url, 'https://media.invalid/file');
  assert.equal(result.mode, 'download');
  assert.equal(f.calls.some(call => /loginSubmit|encryptConf/.test(call.url)), false);
});

test('Tianyi service stores exact plaintext password and only patches Cookie after a real RSA login', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-tianyi-'));
  const f = passwordFixture();
  const service = createPanService({ dataRoot, transport: f.transport, now: f.ctx.now });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: {
    username: 'synthetic-user', password: f.password,
  } });
  const refreshed = await service.invoke('refresh', { provider: 'tianyi' });
  assert.equal(refreshed.refreshed, true);
  assert.equal(JSON.stringify(refreshed).includes(f.password), false);
  const saved = JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'));
  assert.equal(saved.accounts.tianyi.credentials.password, f.password);
  assert.match(saved.accounts.tianyi.credentials.cookie, /session=fresh/);
  assert.deepEqual(f.submitted, [{ username: 'synthetic-user', password: f.password }]);
});

test('Tianyi service exposes only the reference account, list, password refresh and two address capabilities', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-tianyi-'));
  const f = fixture(() => assert.fail('unsupported operations must not request upstream'));
  const service = createPanService({ dataRoot, transport: f.transport });
  t.after(async () => { await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  const capabilities = (await service.invoke('providers')).providers.find(item => item.provider === 'tianyi');
  assert.deepEqual(capabilities, {
    provider: 'tianyi', credentialFields: ['cookie', 'username', 'password'],
    list: true, prepare: false, modes: ['streaming', 'download'], login: false, refresh: true,
  });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: { cookie: 'session=synthetic' } });
  await assert.rejects(service.invoke('login.start', { provider: 'tianyi' }), { code: 'UNSUPPORTED_OPERATION' });
  await assert.rejects(service.invoke('prepare', {
    provider: 'tianyi', file: { ...file, provider: 'tianyi', accountRevision: 1 },
  }), { code: 'UNSUPPORTED_OPERATION' });
  assert.equal(f.calls.length, 0);
});

test('Tianyi service replacement during password completion never persists the old session', async t => {
  const dataRoot = mkdtempSync(join(tmpdir(), 'pan-tianyi-'));
  const f = passwordFixture();
  const entered = deferred(), release = deferred(), drained = deferred();
  const upstream = f.transport.request.bind(f.transport);
  let upstreamSignal;
  f.transport.request = async request => {
    const result = await upstream(request);
    if (new URL(request.url).pathname === '/complete') {
      upstreamSignal = request.signal;
      entered.resolve();
      await release.promise;
      drained.resolve();
    }
    return result;
  };
  const service = createPanService({ dataRoot, transport: f.transport, now: f.ctx.now });
  t.after(async () => { release.resolve(); await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: {
    username: 'synthetic-user', password: f.password,
  } });
  const refresh = service.invoke('refresh', { provider: 'tianyi' });
  const rejected = assert.rejects(refresh, { code: 'STALE_ACCOUNT' });
  await entered.promise;
  const next = { cookie: 'session=replacement' };
  await service.invoke('settings.set', { provider: 'tianyi', credentials: next });
  assert.equal(upstreamSignal.aborted, true);
  release.resolve();
  await rejected;
  await drained.promise;
  await service.close();
  const saved = JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8')).accounts.tianyi;
  assert.equal(saved.revision, 2);
  assert.deepEqual(saved.credentials, next);
  assert.equal(f.submitted.length, 1);
});

test('Tianyi service account changes fence late list and media results and invalidate old file references', async t => {
  for (const stage of ['list', 'media']) {
    await t.test(stage, async t => {
      const dataRoot = mkdtempSync(join(tmpdir(), 'pan-tianyi-'));
      const entered = deferred(), release = deferred();
      let upstreamSignal;
      const f = fixture(async (url, request) => {
        if (stage === 'media' && url.origin === 'https://cloud.189.cn') {
          return response({ res_code: 0, fileDownloadUrl: 'https://media.invalid/file' });
        }
        upstreamSignal = request.signal;
        entered.resolve();
        await release.promise;
        return stage === 'list' ? shareInfo({ isFolder: false, fileId: file.fileId, fileName: file.name }) : response('', 206);
      });
      const service = createPanService({ dataRoot, transport: f.transport });
      t.after(async () => { release.resolve(); await service.close(); rmSync(dataRoot, { recursive: true, force: true }); });
      await service.invoke('settings.set', { provider: 'tianyi', credentials: { cookie: 'session=old' } });
      const oldReference = { ...file, provider: 'tianyi', accountRevision: 1 };
      const pending = stage === 'list' ? service.invoke('list', { provider: 'tianyi', share })
        : service.invoke('play', { provider: 'tianyi', file: oldReference, mode: 'streaming' });
      const rejected = assert.rejects(pending, { code: 'CANCELLED' });
      await entered.promise;
      await service.invoke('settings.set', { provider: 'tianyi', credentials: { cookie: 'session=new' } });
      assert.equal(upstreamSignal.aborted, true);
      release.resolve();
      await rejected;
      const count = f.calls.length;
      await assert.rejects(service.invoke('play', { provider: 'tianyi', file: oldReference, mode: 'download' }),
        { code: 'STALE_ACCOUNT' });
      assert.equal(f.calls.length, count);
    });
  }
});
