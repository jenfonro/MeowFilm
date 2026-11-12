import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, mkdir, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createPanService, PanError } from '../src/index.js';

const now = 1_800_000_000_000;
const ordinary = [
  { id: 'quark', host: 'pan.quark.cn', apiHost: 'drive.quark.cn', prefix: '/1/clouddrive', pr: 'ucpro' },
  { id: 'uc', host: 'drive.uc.cn', apiHost: 'pc-api.uc.cn', prefix: '/1/clouddrive', pr: 'UCBrowser' },
];
const tv = [
  {
    id: 'quark_tv', ordinary: 'quark', host: 'open-api-drive.quark.cn', tokenPath: '/quarkdrive/token',
    clientId: 'd3194e61504e493eb6222857bccfed94', key: 'kw2dvtd7p4t3pjl2d9ed9yc8yej8kw2d',
    appVersion: '1.8.2.2', channel: 'GENERAL', invalid: 10001,
  },
  {
    id: 'uc_tv', ordinary: 'uc', host: 'open-api-drive.uc.cn', tokenPath: '/ucdrive/token',
    clientId: '5acf882d27b74502b7040b0c65519aa7', key: 'l3srvtd7p42l0d0x1u8d7yc8ye9kki4d',
    appVersion: '1.7.2.2', channel: 'UCTVOFFICIALWEB', invalid: 11001,
  },
];
const cookie = '__pus=synthetic-account; __puus=synthetic-transfer; Video-Auth=synthetic-video';
const account = { accessToken: 'synthetic-access', refreshToken: 'synthetic-refresh', deviceId: 'synthetic-device', expiresAt: now + 600_000 };
const video = (id, name = 'movie.mp4') => ({ fid: String(id), file_name: name, file_type: 1, share_fid_token: 'synthetic-file-token' });
const folder = (id, name) => ({ fid: String(id), file_name: name, dir: true, file_type: 0 });
const ok = (data) => ({ value: { code: 0, data } });
const page = (list, extra = {}) => ok({ list, ...extra });

function deferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}

async function fixture(t, route, options = {}) {
  const root = await mkdtemp(join(tmpdir(), 'pan-quark-uc-'));
  const requests = [];
  const transport = {
    async request(request) {
      assert.ok(request.signal instanceof AbortSignal);
      if (request.signal.aborted) throw new PanError('CANCELLED');
      const headers = Object.fromEntries(Object.entries(request.headers || {}).map(([key, value]) => [key.toLowerCase(), value]));
      const entry = {
        ...request, url: new URL(request.url), headers,
        payload: request.body ? JSON.parse(request.body.toString()) : undefined,
      };
      requests.push(entry);
      const response = await route(entry, requests);
      if (request.signal.aborted) throw new PanError('CANCELLED');
      assert.ok(response, `Unexpected protocol request: ${entry.url.pathname}`);
      return {
        status: response.status ?? 200, headers: response.headers ?? {},
        body: Buffer.from(response.body ?? JSON.stringify(response.value)), url: request.url,
      };
    },
    async close() {},
  };
  const service = createPanService({ dataRoot: root, transport, now: options.now || (() => now) });
  t.after(async () => { await service.close(); await rm(root, { recursive: true, force: true }); });
  for (const [provider, credentials] of Object.entries(options.accounts || {})) {
    await service.invoke('settings.set', { provider, credentials });
  }
  return {
    service, requests, root,
    set: (provider, credentials) => service.invoke('settings.set', { provider, credentials }),
    list: (config, password) => service.invoke('list', { provider: config.id, share: { url: `https://${config.host}/s/share-A?pwd=url-password`, ...(password === undefined ? {} : { password }) } }),
    document: async () => JSON.parse(await readFile(join(root, 'pan/accounts.json'), 'utf8')),
  };
}

function shareReference(provider, extra = {}) {
  return {
    kind: 'share', provider, accountRevision: 1, shareId: 'share-A', shareUrl: `https://${provider === 'uc' ? 'drive.uc.cn' : 'pan.quark.cn'}/s/share-A`,
    password: 'synthetic-password', stoken: 'synthetic-stoken', fileId: '9007199254740997',
    fileToken: 'synthetic-file-token', name: 'same # $ *** name.mp4', path: '/nested/same # $ *** name.mp4', ...extra,
  };
}

function storedReference(provider, extra = {}) {
  return { kind: 'stored', provider, accountRevision: 1, fileId: '9007199254740999', ...extra };
}

function ordinaryRoute(config, overrides = {}) {
  return async (request, requests) => {
    const path = request.url.pathname;
    assert.equal(request.url.hostname, config.apiHost);
    assert.equal(request.url.searchParams.get('pr'), config.pr);
    assert.equal(request.url.searchParams.get('fr'), 'pc');
    assert.equal(request.headers.cookie, cookie);
    if (overrides[path]) return overrides[path](request, requests);
    if (path.endsWith('/share/sharepage/token')) return ok({ stoken: 'synthetic-stoken' });
    if (path.endsWith('/share/sharepage/detail')) return page([video('9007199254740997', 'same # $ *** name.mp4')]);
    if (path.endsWith('/file/sort')) return page([folder('destination', 'MeowFilm'), video('preexisting', 'same # $ *** name.mp4')]);
    if (path.endsWith('/share/sharepage/save')) return { value: { code: 0, task_sync: true, data: { save_as: { save_as_top_fids: ['exact-saved-file'] } } } };
    if (path.endsWith('/file/download')) return ok([{ fid: 'exact-saved-file', play_url: 'https://media.example/stream', download_url: 'https://media.example/download' }]);
    throw new Error(`Unexpected ordinary operation: ${path}`);
  };
}

test('capabilities expose ordinary QR/list/prepare and independent TV modes', async (t) => {
  const { service, requests } = await fixture(t, () => { throw new Error('No upstream request expected'); });
  const result = await service.invoke('providers');
  for (const config of ordinary) {
    const provider = result.providers.find((item) => item.provider === config.id);
    assert.equal(provider.list, true);
    assert.equal(provider.prepare, true);
    assert.equal(provider.login, true);
    assert.equal(provider.refresh, true);
    assert.deepEqual(provider.credentialFields, ['cookie']);
    assert.deepEqual(provider.modes, ['streaming', 'download']);
  }
  for (const config of tv) {
    const provider = result.providers.find((item) => item.provider === config.id);
    assert.equal(provider.list, false);
    assert.equal(provider.prepare, false);
    assert.equal(provider.login, false);
    assert.equal(provider.refresh, true);
    assert.deepEqual(provider.modes, ['streaming', 'download']);
    assert.deepEqual(provider.credentialFields, ['refreshToken', 'deviceId', 'accessToken', 'expiresAt']);
    await assert.rejects(service.invoke('list', { provider: config.id, share: { url: 'https://example.test/s/a' } }), { code: 'UNSUPPORTED_OPERATION' });
    await assert.rejects(service.invoke('prepare', { provider: config.id, file: storedReference(config.id, { accountRevision: 0 }) }), { code: 'UNSUPPORTED_OPERATION' });
  }
  assert.equal(requests.length, 0);
});

for (const config of ordinary) {
  test(`${config.id}: explicit empty and nonempty passwords override the URL extraction code`, async (t) => {
    let expected;
    const { list } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) {
        assert.deepEqual(request.payload, {
          pwd_id: 'share-A', ...(expected ? { passcode: expected } : {}),
        });
        return ok({ stoken: 'synthetic-stoken' });
      }
      return page([video('password-reference')]);
    }, { accounts: { [config.id]: { cookie } } });
    for (const password of [undefined, '', 'explicit-new-password']) {
      expected = password ?? 'url-password';
      const result = await list(config, password);
      assert.equal(result.share.password, expected);
      assert.equal(result.files[0].file.password, expected);
    }
  });

  test(`${config.id}: nested token arrays and wide objects do not impose an argument-stack size limit`, async (t) => {
    const entries = Array(200_000).fill(null);
    entries.push({ stoken: 'synthetic-large-token' });
    let data = { entries };
    const { list } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) return ok(data);
      assert.equal(request.url.searchParams.get('stoken'), 'synthetic-large-token');
      return page([video('large-token-reference')]);
    }, { accounts: { [config.id]: { cookie } } });
    assert.equal((await list(config)).files[0].file.stoken, 'synthetic-large-token');
    data = Object.fromEntries(Array.from({ length: 200_000 }, (_, index) => [`entry-${index}`, null]));
    data.last = { stoken: 'synthetic-large-token' };
    assert.equal((await list(config)).files[0].file.stoken, 'synthetic-large-token');
  });

  test(`${config.id}: structured list preserves password, IDs, same names and full paths across explicit pages and directory cycles`, async (t) => {
    const name = '同名 # $ *** 🎞.MKV';
    const { list, requests } = await fixture(t, (request) => {
      const path = request.url.pathname;
      if (path.endsWith('/token')) {
        assert.deepEqual(request.payload, { pwd_id: 'share-A', passcode: 'explicit-password' });
        return { ...ok({ stoken: 'synthetic-stoken' }), headers: { 'set-cookie': ['rotation=new-cookie; Path=/'] } };
      }
      assert.equal(request.headers.cookie.includes('rotation=new-cookie'), true);
      assert.equal(request.url.searchParams.get('_sort'), 'file_type:asc,file_name:asc');
      const parent = request.url.searchParams.get('pdir_fid');
      const number = request.url.searchParams.get('_page');
      if (parent === '0' && number === '1') {
        return page([folder('nested-dir', '原始目录'), { fid: 'readme', file_type: 1, file_name: 'readme.txt' }], { has_more: true, next_page: 3 });
      }
      if (parent === '0' && number === '3') return page([video('root-video')], { has_more: false, total: 3 });
      if (parent === 'nested-dir') {
        return { body: `{"code":0,"data":{"list":[{"fid":9007199254740993,"file_name":${JSON.stringify(name)},"file_type":1,"share_fid_token":"file-A","size":9007199254740995},{"fid":"9007199254740997","file_name":${JSON.stringify(name)},"file_type":1,"share_fid_token":"file-B","size":42},{"fid":"0","file_name":"cycle","dir":true}],"has_more":false}}` };
      }
      throw new Error('Unexpected directory/page');
    }, { accounts: { [config.id]: { cookie } } });
    const result = await list(config, 'explicit-password');
    assert.deepEqual(result.share, { id: 'share-A', url: `https://${config.host}/s/share-A`, password: 'explicit-password' });
    assert.equal(result.files.length, 3);
    const same = result.files.filter((item) => item.name === name);
    assert.equal(same.length, 2);
    assert.deepEqual(same.map((item) => item.file.fileId), ['9007199254740993', '9007199254740997']);
    assert.equal(same[0].path, `/原始目录/${name}`);
    assert.equal(same[0].size, '9007199254740995');
    assert.equal(same[1].size, 42);
    assert.equal(same[0].file.password, 'explicit-password');
    assert.equal(same[0].file.name, name);
    assert.equal(same[0].file.provider, config.id);
    assert.equal(same[0].file.accountRevision, 1);
    assert.deepEqual(requests.map((item) => item.url.pathname), [
      `${config.prefix}/share/sharepage/token`, `${config.prefix}/share/sharepage/detail`,
      `${config.prefix}/share/sharepage/detail`, `${config.prefix}/share/sharepage/detail`,
    ]);
  });

  test(`${config.id}: entire non-video page does not hide later video, and empty shares stay empty`, async (t) => {
    let empty = false;
    const { list, requests } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) {
        assert.equal(request.payload.passcode, 'url-password');
        return ok({ stoken: 'synthetic-stoken' });
      }
      if (empty) return page([]);
      return request.url.searchParams.get('_page') === '1'
        ? page([{ fid: 'text', file_name: 'text.txt', file_type: 1 }], { has_more: true })
        : page([video('later')], { has_more: false });
    }, { accounts: { [config.id]: { cookie } } });
    assert.equal((await list(config)).files[0].file.fileId, 'later');
    empty = true;
    assert.deepEqual((await list(config)).files, []);
    assert.equal(requests.length, 5);
  });

  test(`${config.id}: a total declared only on the first page still requires every later page`, async (t) => {
    const { list, requests } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) return ok({ stoken: 'synthetic-stoken' });
      const number = Number(request.url.searchParams.get('_page'));
      return page([video(number)], number === 1 ? { total: 3 } : {});
    }, { accounts: { [config.id]: { cookie } } });
    assert.deepEqual((await list(config)).files.map((item) => item.file.fileId), ['1', '2', '3']);
    assert.equal(requests.length, 4);
  });

  test(`${config.id}: a later zero total cannot silently retract the initially declared file count`, async (t) => {
    const { list, requests } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) return ok({ stoken: 'synthetic-stoken' });
      const number = Number(request.url.searchParams.get('_page'));
      return page([video(number)], { total: number === 1 ? 3 : 0 });
    }, { accounts: { [config.id]: { cookie } } });
    assert.equal((await list(config)).files.length, 3);
    assert.equal(requests.length, 4);
  });

  test(`${config.id}: empty continuing pages and backward page pointers are protocol failures`, async (t) => {
    let response = { list: [], has_more: true };
    const { list, requests } = await fixture(t, (request) => request.url.pathname.endsWith('/token')
      ? ok({ stoken: 'synthetic-stoken' }) : ok(response), { accounts: { [config.id]: { cookie } } });
    await assert.rejects(list(config), { code: 'UPSTREAM_FORMAT' });
    response = { list: [video('one')], next_page: 1 };
    await assert.rejects(list(config), { code: 'UPSTREAM_FORMAT' });
    assert.equal(requests.length, 4);
  });

  test(`${config.id}: deep directory traversal preserves every original path segment`, async (t) => {
    const { list } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) return ok({ stoken: 'synthetic-stoken' });
      const depth = Number(request.url.searchParams.get('pdir_fid'));
      return page(depth < 24 ? [folder(depth + 1, `level-${depth + 1}`)] : [video('deep')]);
    }, { accounts: { [config.id]: { cookie } } });
    const result = await list(config);
    assert.equal(result.files.length, 1);
    assert.equal(result.files[0].path, `/${Array.from({ length: 24 }, (_, index) => `level-${index + 1}`).join('/')}/movie.mp4`);
  });

  test(`${config.id}: reference field aliases are read without replacing original names`, async (t) => {
    const name = '  原始别名 #$.FlV  ';
    const { list } = await fixture(t, (request) => request.url.pathname.endsWith('/token')
      ? ok({ stoken: 'synthetic-stoken' })
      : page([{ fid: '', file_id: '9007199254740999', file_name: '', name, share_fid_token: '', token: 'alias-token' }]),
    { accounts: { [config.id]: { cookie } } });
    const result = await list(config);
    assert.equal(result.files[0].name, name);
    assert.equal(result.files[0].path, `/${name}`);
    assert.equal(result.files[0].file.fileId, '9007199254740999');
    assert.equal(result.files[0].file.fileToken, 'alias-token');
  });

  test(`${config.id}: lists all 20,201 files instead of copying prior total limits`, async (t) => {
    const count = 20_201;
    const { list, requests } = await fixture(t, (request) => {
      if (request.url.pathname.endsWith('/token')) return ok({ stoken: 'synthetic-stoken' });
      const offset = (Number(request.url.searchParams.get('_page')) - 1) * 200;
      return page(Array.from({ length: Math.min(200, count - offset) }, (_, index) => video(String(offset + index + 1))), { total: count });
    }, { accounts: { [config.id]: { cookie } } });
    const result = await list(config);
    assert.equal(result.files.length, count);
    assert.equal(result.files.at(-1).file.fileId, String(count));
    assert.equal(requests.length, 103);
  });

  test(`${config.id}: continuing repeated page is a format error, never partial success or infinite polling`, async (t) => {
    const { list, requests } = await fixture(t, (request) => request.url.pathname.endsWith('/token')
      ? ok({ stoken: 'synthetic-stoken' }) : page([video('repeated')], { has_more: true }),
    { accounts: { [config.id]: { cookie } } });
    await assert.rejects(list(config), { code: 'UPSTREAM_FORMAT' });
    assert.equal(requests.length, 3);
  });

  test(`${config.id}: invalid shares, missing cookies and incomplete file references fail before side effects`, async (t) => {
    const { service, requests, set } = await fixture(t, () => { throw new Error('Network forbidden'); });
    await assert.rejects(service.invoke('list', { provider: config.id, share: { url: `https://${config.host}.evil.test/s/share-A` } }), { code: 'INVALID_REQUEST' });
    await assert.rejects(service.invoke('list', { provider: config.id, share: { url: `https://${config.host}/s/share-A` } }), { code: 'NOT_CONFIGURED' });
    await set(config.id, { cookie });
    await assert.rejects(service.invoke('play', { provider: config.id, file: shareReference(config.id, { fileToken: '' }), mode: 'streaming' }), { code: 'INVALID_REQUEST' });
    await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'automatic' }), { code: 'UNSUPPORTED_OPERATION' });
    assert.equal(requests.length, 0);
  });

  test(`${config.id}: errors stay failures on repeated calls and do not disclose upstream secrets`, async (t) => {
    const { list } = await fixture(t, () => ({ value: { code: 999, message: 'synthetic-PRIVATE-cookie=secret' } }),
      { accounts: { [config.id]: { cookie } } });
    for (let index = 0; index < 2; index += 1) {
      await assert.rejects(list(config), (error) => error.code === 'UPSTREAM_ERROR' && !String(error).includes('PRIVATE'));
    }
  });

  for (const mode of ['streaming', 'download']) {
    test(`${config.id}: list to ${mode} prepares only the exact file and requests one ordinary address`, async (t) => {
      const { service, list, requests } = await fixture(t, ordinaryRoute(config), { accounts: { [config.id]: { cookie } } });
      const file = (await list(config)).files[0].file;
      const result = await service.invoke('play', { provider: config.id, file, mode });
      assert.equal(result.url, `https://media.example/${mode === 'streaming' ? 'stream' : 'download'}`);
      assert.equal(result.mode, mode);
      assert.equal(result.file.fileId, 'exact-saved-file');
      assert.equal(result.headers.Cookie, cookie);
      assert.equal(result.headers.Referer, `https://${config.host}${config.id === 'quark' ? '/' : ''}`);
      assert.match(result.headers['User-Agent'], new RegExp(`${config.id}-cloud-drive`));
      const transfer = requests.filter((item) => item.url.pathname.endsWith('/share/sharepage/save'));
      assert.equal(transfer.length, 1);
      assert.deepEqual(transfer[0].payload, {
        fid_list: ['9007199254740997'], fid_token_list: ['synthetic-file-token'], to_pdir_fid: 'destination',
        pwd_id: 'share-A', stoken: 'synthetic-stoken', pdir_fid: '0', scene: 'link', share_id: 'share-A',
      });
      const addresses = requests.filter((item) => item.url.pathname.endsWith('/file/download'));
      assert.equal(addresses.length, 1);
      assert.equal(addresses[0].payload.fid, 'exact-saved-file');
      assert.equal(requests.some((item) => item.url.hostname.startsWith('open-api') || item.url.pathname.includes('delete')), false);
    });
  }

  test(`${config.id}: absent dedicated directory is created and task output preserves exact saved identity`, async (t) => {
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/sort`]: () => page([video('unrelated', 'same # $ *** name.mp4')]),
      [`${config.prefix}/file`]: (request) => {
        assert.equal(request.method, 'POST');
        assert.deepEqual(request.payload, { pdir_fid: '0', file_name: 'MeowFilm', dir_path: '', dir_init_lock: false });
        return ok({ fid: 'created-directory' });
      },
      [`${config.prefix}/share/sharepage/save`]: () => ok({ task_id: 'synthetic-task' }),
      [`${config.prefix}/task`]: (request) => {
        assert.equal(request.url.searchParams.get('task_id'), 'synthetic-task');
        return ok({ state: 2, save_as: { save_as_top_fid: ['9007199254740999'] } });
      },
    }), { accounts: { [config.id]: { cookie } } });
    const result = await service.invoke('prepare', { provider: config.id, file: shareReference(config.id) });
    assert.deepEqual(result.file, storedReference(config.id, { parentFileId: 'created-directory' }));
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/task')).length, 1);
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/file/download')).length, 0);
  });

  test(`${config.id}: reference boolean and string task completion markers return the precise saved fid`, async (t) => {
    let completion;
    const { service } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ok({ taskId: 'reference-task' }),
      [`${config.prefix}/task`]: () => ok({ ...completion, save_as_select_top_fids: ['reference-saved-fid'] }),
    }), { accounts: { [config.id]: { cookie } } });
    for (const flag of [{ finished: true }, { finished: 'true' }, { finish: true }, { finish: 'TRUE' }, { finish: 1 }]) {
      completion = flag;
      assert.equal((await service.invoke('prepare', { provider: config.id, file: shareReference(config.id) })).file.fileId, 'reference-saved-fid');
    }
  });

  test(`${config.id}: destination lookup consumes declared pages instead of treating lookup failure as absence`, async (t) => {
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/sort`]: (request) => request.url.searchParams.get('_page') === '1'
        ? page([folder('unrelated', 'Other')], { has_more: true, next_page: 2 })
        : page([folder('destination', 'MeowFilm')], { has_more: false }),
    }), { accounts: { [config.id]: { cookie } } });
    await service.invoke('prepare', { provider: config.id, file: shareReference(config.id) });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/file/sort')).length, 2);
    assert.equal(requests.some((item) => item.url.pathname === `${config.prefix}/file`), false);
  });

  test(`${config.id}: task without an exact saved fid never guesses a same-name destination file`, async (t) => {
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ok({ task_id: 'task' }),
      [`${config.prefix}/task`]: () => ok({ state: 2 }),
    }), { accounts: { [config.id]: { cookie } } });
    await assert.rejects(service.invoke('play', { provider: config.id, file: shareReference(config.id), mode: 'streaming' }), { code: 'UPSTREAM_FORMAT' });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/file/sort')).length, 1);
    assert.equal(requests.some((item) => item.url.pathname.endsWith('/file/download')), false);
  });

  test(`${config.id}: synchronous transfer rejects root, multiple and conflicting saved identities`, async (t) => {
    let data;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ({ value: { code: 0, task_sync: true, data } }),
    }), { accounts: { [config.id]: { cookie } } });
    for (const candidate of [
      { save_as_top_fids: ['0'] },
      { save_as_top_fids: ['one', 'two'] },
      { save_as_top_fids: ['one'], save_as_top_fid: 'other' },
    ]) {
      data = candidate;
      await assert.rejects(service.invoke('prepare', { provider: config.id, file: shareReference(config.id) }), { code: 'UPSTREAM_FORMAT' });
    }
    assert.equal(requests.some((item) => item.url.pathname.endsWith('/file/download') || item.url.pathname.includes('delete')), false);
  });

  test(`${config.id}: a large nested transfer result still returns its exact saved fid`, async (t) => {
    const entries = Array(200_000).fill(null);
    entries.push({ save_as_top_fids: ['exact-large-result-fid'] });
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ({ value: { code: 0, task_sync: true, data: { entries } } }),
    }), { accounts: { [config.id]: { cookie } } });
    const result = await service.invoke('prepare', { provider: config.id, file: shareReference(config.id) });
    assert.equal(result.file.fileId, 'exact-large-result-fid');
    assert.equal(requests.length, 2);
  });

  test(`${config.id}: destination lookup and task rejection stay errors instead of creating or selecting unrelated files`, async (t) => {
    let lookupFailure = true;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/sort`]: () => lookupFailure ? { value: { code: 403 } } : page([folder('destination', 'MeowFilm')]),
      [`${config.prefix}/share/sharepage/save`]: () => ok({ task_id: 'rejected-task' }),
      [`${config.prefix}/task`]: () => ({ value: { code: 409, message: 'synthetic-transfer-rejected' } }),
    }), { accounts: { [config.id]: { cookie } } });
    await assert.rejects(service.invoke('prepare', { provider: config.id, file: shareReference(config.id) }), { code: 'UPSTREAM_ERROR' });
    assert.equal(requests.length, 1);
    lookupFailure = false;
    await assert.rejects(service.invoke('prepare', { provider: config.id, file: shareReference(config.id) }), { code: 'UPSTREAM_ERROR' });
    assert.equal(requests.length, 4);
    assert.equal(requests.some((item) => item.url.pathname === `${config.prefix}/file` || item.url.pathname.endsWith('/file/download')), false);
  });

  test(`${config.id}: a task deadline reports timeout even if an unfinished response contains a saved fid`, async (t) => {
    let clock = now;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ok({ task_id: 'unfinished-task' }),
      [`${config.prefix}/task`]: () => {
        clock += 30_000;
        return ok({ state: 1, save_as_top_fids: ['not-completed'] });
      },
    }), { accounts: { [config.id]: { cookie } }, now: () => clock });
    await assert.rejects(service.invoke('prepare', { provider: config.id, file: shareReference(config.id) }), { code: 'TIMEOUT' });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/task')).length, 1);
    assert.equal(requests.some((item) => item.url.pathname.endsWith('/file/download')), false);
  });

  test(`${config.id}: stored address selection never substitutes another fid`, async (t) => {
    let data = [{ fid: 'unrelated', play_url: 'https://media.example/wrong-file' }];
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/download`]: () => ok(data),
    }), { accounts: { [config.id]: { cookie } } });
    const file = storedReference(config.id, { fileToken: 'synthetic-stored-token' });
    await assert.rejects(service.invoke('play', { provider: config.id, file, mode: 'streaming' }), { code: 'UPSTREAM_FORMAT' });
    data = { fid: file.fileId };
    await assert.rejects(service.invoke('play', { provider: config.id, file, mode: 'streaming' }), { code: 'UPSTREAM_FORMAT' });
    data = [
      { fid: 'unrelated', play_url: 'https://media.example/wrong-file' },
      { fid: file.fileId, play_url: 'https://media.example/right-file' },
    ];
    assert.equal((await service.invoke('play', { provider: config.id, file, mode: 'streaming' })).url, 'https://media.example/right-file');
    assert.equal(requests.length, 3);
    assert.equal(requests.every((item) => item.url.pathname.endsWith('/file/download')), true);
  });

  test(`${config.id}: reference data-only responses are usable but explicit API failures stay failures`, async (t) => {
    let rejected = false;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/token`]: () => ({ value: { data: { stoken: 'synthetic-stoken' } } }),
      [`${config.prefix}/share/sharepage/detail`]: () => ({ value: { data: { list: [video('shared')] } } }),
      [`${config.prefix}/file/download`]: () => ({
        value: { ...(rejected ? { code: 403 } : {}), data: { download_url: 'https://media.example/original' } },
      }),
    }), { accounts: { [config.id]: { cookie } } });
    const listed = await service.invoke('list', { provider: config.id, share: { url: `https://${config.host}/s/share-A` } });
    assert.equal(listed.files[0].file.fileId, 'shared');
    const params = { provider: config.id, file: storedReference(config.id), mode: 'streaming' };
    assert.equal((await service.invoke('play', params)).url, 'https://media.example/original');
    rejected = true;
    await assert.rejects(service.invoke('play', params), { code: 'UPSTREAM_ERROR' });
    assert.equal(requests.length, 4);
  });

  for (const mode of ['streaming', 'download']) {
    test(`${config.id}: ${mode} preserves the reference field preference in one ordinary response`, async (t) => {
      let data;
      const { service, requests } = await fixture(t, ordinaryRoute(config, {
        [`${config.prefix}/file/download`]: () => ok(data),
      }), { accounts: { [config.id]: { cookie } } });
      const file = storedReference(config.id, { parentFileId: 'destination' });
      const streamURL = 'https://media.example/play';
      const downloadURL = 'https://media.example/download';
      const genericURL = 'https://media.example/generic';
      const cases = [
        [{ download_url: downloadURL }, downloadURL],
        [{ play_url: streamURL }, streamURL],
        [{ play_url: streamURL, download_url: downloadURL, url: genericURL }, mode === 'streaming' ? streamURL : downloadURL],
        [{ play_url: ' ', download_url: downloadURL, url: genericURL }, downloadURL],
        [{ url: genericURL }, genericURL],
        [{ play_url: ' ', download_url: 7, links: { play_url: streamURL, download_url: downloadURL } },
          mode === 'streaming' ? streamURL : downloadURL],
      ];
      for (const [fields, expected] of cases) {
        data = [{ fid: 'unrelated', play_url: 'https://media.example/wrong' }, { fid: file.fileId, ...fields }];
        const result = await service.invoke('play', { provider: config.id, file, mode });
        assert.equal(result.url, expected);
        assert.equal(result.mode, mode);
        assert.equal(result.file.fileId, file.fileId);
        assert.equal(result.headers.Cookie, cookie);
      }
      assert.equal(requests.length, cases.length);
      assert.equal(requests.every((item) => item.url.hostname === config.apiHost
        && item.url.pathname === `${config.prefix}/file/download`
        && item.payload.fid === file.fileId), true);
    });
  }

  test(`${config.id}: token lookup only retries the precise stored fid, never the first file`, async (t) => {
    let addressRequests = 0;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/download`]: (request) => {
        addressRequests += 1;
        if (addressRequests === 1) return { value: { code: 403, message: 'token required' } };
        assert.equal(request.payload.fid, '9007199254740999');
        assert.equal(request.payload.fid_token, 'exact-token');
        return ok({ download_url: 'https://media.example/exact-file' });
      },
      [`${config.prefix}/file/sort`]: () => page([
        { ...video('other', 'same.mp4'), fid_token: 'wrong-token' },
        { fid: '9007199254740999', file_name: 'same.mp4', file_type: 1, fid_token: 'exact-token' },
      ]),
    }), { accounts: { [config.id]: { cookie } } });
    const result = await service.invoke('play', { provider: config.id, file: storedReference(config.id, { parentFileId: 'destination' }), mode: 'download' });
    assert.equal(result.url, 'https://media.example/exact-file');
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/file/download')).length, 2);
    assert.equal(requests.some((item) => item.url.pathname.endsWith('/save')), false);
  });

  test(`${config.id}: lookup without exact stored fid fails without requesting an unrelated file`, async (t) => {
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/file/download`]: () => ({ value: { code: 403 } }),
      [`${config.prefix}/file/sort`]: () => page([video('unrelated', 'same.mp4')]),
    }), { accounts: { [config.id]: { cookie } } });
    await assert.rejects(service.invoke('play', {
      provider: config.id, file: storedReference(config.id, { parentFileId: 'destination' }), mode: 'download',
    }), { code: 'NOT_FOUND' });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/file/download')).length, 1);
  });

  test(`${config.id}: share prepare and simultaneous play share one transfer while a cancelled waiter does not abort the survivor`, async (t) => {
    const entered = deferred(), release = deferred();
    let saveSignal;
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: async (request) => {
        saveSignal = request.signal;
        entered.resolve();
        await release.promise;
        return { value: { code: 0, task_sync: true, data: { save_as_top_fids: ['exact-saved-file'] } } };
      },
    }), { accounts: { [config.id]: { cookie } } });
    const controller = new AbortController();
    const file = shareReference(config.id);
    const preparing = service.invoke('prepare', { provider: config.id, file }, { signal: controller.signal });
    const playing = service.invoke('play', { provider: config.id, file, mode: 'streaming' });
    await entered.promise;
    controller.abort();
    await assert.rejects(preparing, { code: 'CANCELLED' });
    assert.equal(saveSignal.aborted, false);
    release.resolve();
    assert.equal((await playing).file.fileId, 'exact-saved-file');
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/save')).length, 1);
    await service.invoke('prepare', { provider: config.id, file });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/save')).length, 2);
  });

  test(`${config.id}: task polling is cancelled and drained without scheduling later upstream work`, async (t) => {
    const entered = deferred();
    const { service, requests } = await fixture(t, ordinaryRoute(config, {
      [`${config.prefix}/share/sharepage/save`]: () => ok({ task_id: 'pending-task' }),
      [`${config.prefix}/task`]: () => { entered.resolve(); return ok({ state: 1 }); },
    }), { accounts: { [config.id]: { cookie } } });
    const controller = new AbortController();
    const pending = service.invoke('prepare', { provider: config.id, file: shareReference(config.id) }, { signal: controller.signal });
    await entered.promise;
    controller.abort();
    await assert.rejects(pending, { code: 'CANCELLED' });
    await service.close();
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/task')).length, 1);
    assert.equal(requests.some((item) => item.url.pathname.includes('delete')), false);
  });
}

test('UC stoken detail fallback and transfer Cookie preflight persist cookies before the next operation', async (t) => {
  const config = ordinary[1];
  let detailCalls = 0;
  const { list, service, requests, document } = await fixture(t, async (request) => {
    const path = request.url.pathname;
    if (path.endsWith('/token')) return { value: { code: 400 } };
    if (path.endsWith('/share/sharepage/detail') && !request.url.searchParams.has('stoken')) {
      detailCalls += 1;
      return { ...ok({ stoken: 'fallback-stoken' }), headers: { 'set-cookie': ['__puus=preflight-cookie; Path=/'] } };
    }
    if (path.endsWith('/share/sharepage/detail')) return page([video('shared')]);
    if (path.endsWith('/file/sort')) return page([folder('destination', 'MeowFilm')]);
    if (path === '/s/share-A') {
      assert.match(request.headers.cookie, /__puus=preflight-cookie/);
      assert.equal(request.headers.accept, 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8');
      return { body: 'synthetic share page', headers: { 'set-cookie': ['warmup=done; Path=/'] } };
    }
    if (path.endsWith('/transfer/upload/pdir')) {
      assert.match(request.headers.cookie, /warmup=done/);
      return { ...ok({}), headers: { 'set-cookie': ['Video-Auth=prepared-cookie; Path=/'] } };
    }
    if (path.endsWith('/share/sharepage/save')) {
      assert.match(request.headers.cookie, /Video-Auth=prepared-cookie/);
      assert.equal(request.payload.stoken, 'fallback-stoken');
      return { value: { code: 0, task_sync: true, data: { save_as_top_fids: ['stored'] } } };
    }
    throw new Error(`Unexpected UC operation: ${path}`);
  }, { accounts: { uc: { cookie: '__pus=initial-cookie' } } });
  const file = (await list(config)).files[0].file;
  await service.invoke('prepare', { provider: 'uc', file });
  assert.equal(detailCalls, 1);
  assert.match((await document()).accounts.uc.credentials.cookie, /Video-Auth=prepared-cookie/);
  assert.equal(requests.filter((item) => item.url.pathname.endsWith('/transfer/upload/pdir')).length, 1);
});

test('UC detail POST fallback preserves the password after GET provides no stoken', async (t) => {
  const { list, requests } = await fixture(t, (request) => {
    if (request.url.pathname.endsWith('/token')) return { value: { code: 400 } };
    if (!request.url.searchParams.has('stoken')) {
      if (request.method === 'POST') {
        assert.deepEqual(request.payload, { pwd_id: 'share-A', passcode: 'explicit-password', pdir_fid: '0' });
        return ok({ stoken: 'post-stoken' });
      }
      return ok({});
    }
    assert.equal(request.url.searchParams.get('stoken'), 'post-stoken');
    return page([video('post-fallback-file')]);
  }, { accounts: { uc: { cookie } } });
  assert.equal((await list(ordinary[1], 'explicit-password')).files[0].file.fileId, 'post-fallback-file');
  assert.equal(requests.length, 4);
});

test('UC transfer preflight still initializes cookies after the share-page request fails', async (t) => {
  const { service, document, requests } = await fixture(t, (request) => {
    if (request.url.pathname.endsWith('/file/sort')) return page([folder('destination', 'MeowFilm')]);
    if (request.url.pathname === '/s/share-A') throw new PanError('NETWORK_ERROR');
    if (request.url.pathname.endsWith('/transfer/upload/pdir')) {
      return { ...ok({}), headers: { 'set-cookie': ['Video-Auth=synthetic-video; Path=/', '__puus=synthetic-transfer; Path=/'] } };
    }
    if (request.url.pathname.endsWith('/share/sharepage/save')) {
      assert.match(request.headers.cookie, /Video-Auth=synthetic-video/);
      return { value: { code: 0, task_sync: true, data: { save_as_top_fids: ['saved-after-preflight'] } } };
    }
    throw new Error('Unexpected UC preflight operation');
  }, { accounts: { uc: { cookie: '__pus=synthetic-account' } } });
  assert.equal((await service.invoke('prepare', { provider: 'uc', file: shareReference('uc') })).file.fileId, 'saved-after-preflight');
  assert.match((await document()).accounts.uc.credentials.cookie, /Video-Auth=synthetic-video/);
  assert.equal(requests.length, 4);
});

for (const config of tv) {
  test(`${config.id}: stored fid uses one specified official method and the reference request signature`, async (t) => {
    const { service, requests } = await fixture(t, (request) => {
      assert.equal(request.url.origin, `https://${config.host}`);
      assert.equal(request.url.pathname, '/file');
      const query = request.url.searchParams;
      assert.equal(query.get('fid'), '9007199254740999');
      assert.equal(query.get('req_id'), createHash('md5').update(`synthetic-device${now}`).digest('hex'));
      assert.equal(query.get('access_token'), 'synthetic-access');
      assert.equal(query.get('app_ver'), config.appVersion);
      assert.equal(query.get('channel'), config.channel);
      assert.equal(query.get('resolution'), 'low,normal,high,super,2k,4k');
      assert.equal(query.get('support'), 'dolby_vision');
      assert.equal(request.headers['x-pan-tm'], String(now));
      assert.equal(request.headers['x-pan-client-id'], config.clientId);
      assert.equal(request.headers['x-pan-token'], createHash('sha256').update(`GET&/file&${now}&${config.key}`).digest('hex'));
      assert.equal(request.headers.cookie, undefined);
      return { value: { status: 200, errno: 0, data: { download_url: ' https://media.example/download ', video_info: [{ url: ' ' }, { url: ' https://media.example/stream ' }] } } };
    }, { accounts: { [config.id]: account, [config.ordinary]: { cookie } } });
    for (const mode of ['streaming', 'download']) {
      const result = await service.invoke('play', { provider: config.id, file: storedReference(config.ordinary), mode });
      assert.equal(result.url, `https://media.example/${mode === 'streaming' ? 'stream' : 'download'}`);
      assert.deepEqual(result.headers, {});
    }
    assert.deepEqual(requests.map((item) => item.url.searchParams.get('method')), ['streaming', 'download']);
    await assert.rejects(service.invoke('play', { provider: config.id, file: shareReference(config.ordinary), mode: 'streaming' }), { code: 'INVALID_REQUEST' });
    assert.equal(requests.length, 2);
  });

  test(`${config.id}: absent selected TV address never uses the other mode or an ordinary provider`, async (t) => {
    const { service, requests } = await fixture(t, (request) => ({
      value: { errno: 0, data: request.url.searchParams.get('method') === 'streaming'
        ? { download_url: 'https://media.example/not-streaming' }
        : { video_info: [{ url: 'https://media.example/not-download' }] } },
    }), { accounts: { [config.id]: account, [config.ordinary]: { cookie } } });
    for (const mode of ['streaming', 'download']) {
      await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id), mode }),
        { code: 'UPSTREAM_FORMAT' });
    }
    assert.deepEqual(requests.map((item) => [item.url.hostname, item.url.pathname, item.url.searchParams.get('method')]),
      [[config.host, '/file', 'streaming'], [config.host, '/file', 'download']]);
  });

  test(`${config.id}: explicit and expiring-token refresh join once, persist rotation and then request the chosen file method`, async (t) => {
    const entered = deferred(), release = deferred();
    const { service, requests, document } = await fixture(t, async (request) => {
      if (request.url.hostname === 'api.extscreen.com') {
        assert.equal(request.url.protocol, 'http:');
        assert.equal(request.url.pathname, config.tokenPath);
        assert.equal(request.method, 'POST');
        assert.equal(request.payload.refresh_token, 'synthetic-refresh');
        assert.equal(request.payload.device_id, 'synthetic-device');
        assert.equal(request.payload.req_id, createHash('md5').update(`synthetic-device${now}`).digest('hex'));
        assert.equal(request.payload.app_ver, config.appVersion);
        entered.resolve();
        await release.promise;
        return { value: { code: 200, data: { access_token: 'synthetic-new-access', refresh_token: 'synthetic-new-refresh', expires_in: 3600 } } };
      }
      assert.equal(request.url.searchParams.get('access_token'), 'synthetic-new-access');
      assert.equal(request.url.searchParams.get('method'), 'download');
      return { value: { errno: 0, data: { download_url: 'https://media.example/rotated' } } };
    }, { accounts: { [config.id]: { ...account, expiresAt: now + 30_000 } } });
    const refreshing = service.invoke('refresh', { provider: config.id });
    await entered.promise;
    const playing = service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' });
    release.resolve();
    assert.equal((await refreshing).refreshed, true);
    assert.equal((await playing).url, 'https://media.example/rotated');
    assert.equal(requests.filter((item) => item.url.hostname === 'api.extscreen.com').length, 1);
    assert.deepEqual((await document()).accounts[config.id].credentials, {
      ...account, accessToken: 'synthetic-new-access', refreshToken: 'synthetic-new-refresh', expiresAt: now + 3_600_000,
    });
  });

  test(`${config.id}: structured invalid-token 4xx refreshes and retries once without ordinary fallback`, async (t) => {
    let fileCalls = 0;
    const { service, requests } = await fixture(t, (request) => {
      if (request.url.hostname === 'api.extscreen.com') return { value: { code: 200, data: { access_token: 'retry-access', expires_in: 3600 } } };
      fileCalls += 1;
      if (fileCalls === 1) return { status: 401, value: { status: -1, errno: config.invalid, error_info: 'synthetic invalid access token' } };
      assert.equal(request.url.searchParams.get('access_token'), 'retry-access');
      return { value: { errno: 0, data: { video_info: [{ url: 'https://media.example/retry' }] } } };
    }, { accounts: { [config.id]: account } });
    assert.equal((await service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'streaming' })).url, 'https://media.example/retry');
    assert.equal(requests.length, 3);
    assert.deepEqual(requests.filter((item) => item.url.pathname === '/file').map((item) => item.url.searchParams.get('method')), ['streaming', 'streaming']);
  });

  test(`${config.id}: a late old-token rejection reuses the already rotated token without a second refresh`, async (t) => {
    const entered = deferred(), release = deferred();
    let oldCalls = 0;
    const { service, requests } = await fixture(t, async (request) => {
      if (request.url.hostname === 'api.extscreen.com') return { value: { code: 200, data: { access_token: 'rotated-access', expires_in: 3600 } } };
      if (request.url.searchParams.get('access_token') === 'synthetic-access') {
        oldCalls += 1;
        if (oldCalls === 1) { entered.resolve(); await release.promise; }
        return { value: { status: -1, errno: config.invalid } };
      }
      assert.equal(request.url.searchParams.get('access_token'), 'rotated-access');
      return { value: { errno: 0, data: { download_url: 'https://media.example/concurrent' } } };
    }, { accounts: { [config.id]: account } });
    const late = service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' });
    await entered.promise;
    const first = await service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' });
    assert.equal(first.url, 'https://media.example/concurrent');
    release.resolve();
    assert.equal((await late).url, 'https://media.example/concurrent');
    assert.equal(requests.filter((item) => item.url.hostname === 'api.extscreen.com').length, 1);
    assert.equal(requests.filter((item) => item.url.pathname === '/file').length, 4);
  });

  test(`${config.id}: repeated invalid credentials fail after one refresh; file permission errors do not refresh`, async (t) => {
    let permission = false;
    const { service, requests } = await fixture(t, (request) => {
      if (request.url.hostname === 'api.extscreen.com') return { value: { code: 200, data: { access_token: 'retry-access' } } };
      return { value: permission ? { status: -1, errno: 90004, error_info: 'file permission denied' } : { status: -1, errno: config.invalid } };
    }, { accounts: { [config.id]: account } });
    await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' }), { code: 'AUTH_FAILED' });
    assert.equal(requests.filter((item) => item.url.hostname === 'api.extscreen.com').length, 1);
    permission = true;
    await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' }), { code: 'UPSTREAM_ERROR' });
    assert.equal(requests.length, 4);
  });

  test(`${config.id}: valid token without refresh works; expired token without refresh fails before external requests`, async (t) => {
    const { service, requests, set } = await fixture(t, () => ({ value: { errno: 0, data: { download_url: 'https://media.example/direct' } } }),
      { accounts: { [config.id]: { accessToken: 'only-access', deviceId: 'synthetic-device' } } });
    await service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' });
    await set(config.id, { accessToken: 'old-access', deviceId: 'synthetic-device', expiresAt: now - 1 });
    await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id, { accountRevision: 2 }), mode: 'download' }), { code: 'NOT_CONFIGURED' });
    assert.equal(requests.length, 1);
  });

  test(`${config.id}: reference credentials trim protocol whitespace and non-positive expiry stays unknown`, async (t) => {
    let expiresIn = 0;
    const { service, requests, document, set } = await fixture(t, (request) => {
      if (request.url.hostname === 'api.extscreen.com') {
        assert.equal(request.payload.device_id, 'synthetic-device');
        assert.equal(request.payload.refresh_token, 'synthetic-refresh');
        return { value: { code: 200, data: { access_token: '  next-access  ', refresh_token: ' next-refresh ', expires_in: expiresIn } } };
      }
      assert.equal(request.url.searchParams.get('access_token'), 'next-access');
      assert.equal(request.url.searchParams.get('device_id'), 'synthetic-device');
      return { value: { errno: 0, data: { download_url: 'https://media.example/trimmed' } } };
    }, { accounts: { [config.id]: { refreshToken: ' synthetic-refresh ', deviceId: ' synthetic-device ' } } });
    await service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'download' });
    assert.equal((await document()).accounts[config.id].credentials.expiresAt, 0);
    assert.equal((await document()).accounts[config.id].credentials.refreshToken, 'next-refresh');
    expiresIn = -1;
    await set(config.id, { refreshToken: ' synthetic-refresh ', deviceId: ' synthetic-device ' });
    await service.invoke('refresh', { provider: config.id });
    assert.equal((await document()).accounts[config.id].credentials.expiresAt, 0);
    await set(config.id, { refreshToken: ' ', deviceId: ' ' });
    await assert.rejects(service.invoke('refresh', { provider: config.id }), { code: 'NOT_CONFIGURED' });
    assert.equal(requests.length, 3);
  });

  test(`${config.id}: token persistence failure is awaited and prevents file resolution`, async (t) => {
    const { service, requests, root } = await fixture(t, () => ({ value: { code: 200, data: { access_token: 'unsaved-token', expires_in: 3600 } } }),
      { accounts: { [config.id]: { ...account, expiresAt: now - 1 } } });
    const path = join(root, 'pan/accounts.json');
    await unlink(path);
    await mkdir(path);
    await assert.rejects(service.invoke('play', { provider: config.id, file: storedReference(config.id), mode: 'streaming' }), { code: 'STORAGE_ERROR' });
    assert.equal(requests.length, 1);
    assert.equal(requests[0].url.hostname, 'api.extscreen.com');
  });

  test(`${config.id}: rejected refresh token is an authentication failure without changing persisted credentials`, async (t) => {
    let data;
    const { service, requests, root } = await fixture(t, (request) => {
      assert.equal(request.url.hostname, 'api.extscreen.com');
      assert.equal(request.url.pathname, config.tokenPath);
      return { value: { code: 200, message: 'success', data } };
    }, { accounts: { [config.id]: account } });
    const path = join(root, 'pan/accounts.json');
    const before = await readFile(path);
    const settings = await service.invoke('settings.get');
    for (const [status, errno] of [[-1, 11000], ['-1', '11000']]) {
      data = { status, errno, error_info: 'refresh token无效', access_token: '', refresh_token: '', expires_in: 0 };
      await assert.rejects(service.invoke('refresh', { provider: config.id }), { code: 'AUTH_FAILED' });
      assert.deepEqual(await readFile(path), before);
      assert.deepEqual(await service.invoke('settings.get'), {
        accounts: settings.accounts.map(item => item.provider === config.id ? { ...item, status: 'expired' } : item),
      });
    }
    assert.equal(requests.length, 2);
  });

  test(`${config.id}: malformed rotated credentials and expiry fail as upstream format without publishing them`, async (t) => {
    let data;
    const { service, requests, document } = await fixture(t, () => ({ value: { code: 200, data } }),
      { accounts: { [config.id]: account } });
    for (const candidate of [
      {},
      { status: 0, errno: 0 },
      { status: -1 },
      { status: 0, errno: 11000 },
      { status: -1, errno: 90004, error_info: 'refresh token无效' },
      { access_token: 'invalid\r\nheader' },
      { access_token: 'synthetic-next', refresh_token: 123 },
      { access_token: 'synthetic-next', expires_in: 0.5 },
      { access_token: 'synthetic-next', expires_in: '9007199254740991' },
    ]) {
      data = candidate;
      await assert.rejects(service.invoke('refresh', { provider: config.id }), { code: 'UPSTREAM_FORMAT' });
      assert.deepEqual((await document()).accounts[config.id].credentials, account);
    }
    assert.equal(requests.every((item) => item.url.hostname === 'api.extscreen.com'), true);
  });
}

test('Quark TV detects the reference invalid-token message after an empty error_info', async (t) => {
  const { service, requests } = await fixture(t, (request) => {
    if (request.url.hostname === 'api.extscreen.com') {
      return { value: { code: 200, data: { access_token: 'synthetic-new-token' } } };
    }
    if (request.url.searchParams.get('access_token') === account.accessToken) {
      return { status: 401, value: { status: -1, errno: 1234, error_info: ' ', message: 'access_token invalid' } };
    }
    return { value: { errno: 0, data: { video_info: [{ url: 'https://media.example/refreshed' }] } } };
  }, { accounts: { quark_tv: account } });
  const result = await service.invoke('play', {
    provider: 'quark_tv', file: storedReference('quark_tv'), mode: 'streaming',
  });
  assert.equal(result.url, 'https://media.example/refreshed');
  assert.deepEqual(requests.map(request => [request.url.hostname, request.url.pathname]), [
    ['open-api-drive.quark.cn', '/file'], ['api.extscreen.com', '/quarkdrive/token'], ['open-api-drive.quark.cn', '/file'],
  ]);
});

function qrRoute(config, statuses, overrides = {}) {
  return (request) => {
    const path = request.url.pathname;
    assert.equal(request.headers['accept-language'], 'zh-CN,zh;q=0.9,en;q=0.8');
    if (path === '/cas/custom/login' || path.startsWith('/cas/ajax/')) {
      const sso = config.id === 'quark' ? 'https://uop.quark.cn' : 'https://api.open.uc.cn';
      assert.equal(request.headers.referer, `${sso}/cas/custom/login`);
      assert.equal(request.headers.origin, sso);
    }
    if (config.id === 'quark' && path.endsWith('/file/sort')) {
      assert.equal(request.headers.referer, 'https://pan.quark.cn/');
      assert.equal(request.headers.origin, 'https://pan.quark.cn');
    }
    if (config.id === 'quark' && path === '/account/info') {
      assert.equal(request.headers.referer, `${request.url.origin}/`);
      assert.equal(request.headers.accept, 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8');
    }
    if (overrides[path]) return overrides[path](request);
    const domain = config.id === 'quark' ? '.quark.cn' : '.uc.cn';
    if (path === '/' || path === '/cas/custom/login') {
      return { body: 'synthetic login HTML', headers: { 'set-cookie': [`__pus=synthetic-login; Domain=${domain}; Path=/; Secure`] } };
    }
    if (path.endsWith('/getTokenForQrcodeLogin')) {
      assert.equal(request.url.searchParams.get('client_id'), config.id === 'quark' ? '532' : '381');
      assert.equal(request.url.searchParams.get('__t'), String(now));
      assert.match(request.url.searchParams.get('request_id'), /^[a-f0-9-]{36}$/);
      return { value: { data: { token: 'synthetic-qr-token' } } };
    }
    if (path.endsWith('/getServiceTicketByQrcodeToken')) return { value: statuses.shift() || { status: 'pending' } };
    if (path === '/account/info') return { ...ok({}), headers: { 'set-cookie': [`__puus=synthetic-confirmed; Domain=${domain}; Path=/; Secure`] } };
    if (path.endsWith('/file/sort')) return page([]);
    if (path.endsWith('/transfer/upload/pdir')) {
      assert.match(request.headers.cookie, /__pus=synthetic-login/);
      assert.match(request.headers.cookie, /__puus=synthetic-confirmed/);
      return { ...ok({}), headers: { 'set-cookie': ['Video-Auth=synthetic-confirmed-video; Path=/; Secure'] } };
    }
    throw new Error(`Unexpected QR operation: ${path}`);
  };
}

for (const config of ordinary) {
  test(`${config.id}: QR empty-query status remains pending without publishing credentials`, async (t) => {
    const responses = [50004001, '50004001'].map((status) => ({ status, message: 'Query result is empty' }));
    const { service, requests, document } = await fixture(t, qrRoute(config, responses.slice()),
      { accounts: { [config.id]: { cookie } } });
    const before = await document();
    const settings = await service.invoke('settings.get');
    const { sessionId } = await service.invoke('login.start', { provider: config.id });
    for (const response of responses) {
      assert.deepEqual(await service.invoke('login.poll', { sessionId }),
        { sessionId, provider: config.id, status: 'pending' }, JSON.stringify(response));
    }
    assert.deepEqual(await document(), before);
    assert.deepEqual(await service.invoke('settings.get'), settings);
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/getServiceTicketByQrcodeToken')).length, 2);
    assert.equal(requests.some((item) => item.url.pathname === '/account/info'), false);
  });

  test(`${config.id}: QR empty-query status does not override upstream expiry or a confirmed ticket`, async (t) => {
    const { service } = await fixture(t, qrRoute(config, [
      { status: 50004001, message: '二维码已过期' },
      { status: 50004001, message: 'Query result is empty', data: { service_ticket: 'synthetic-service-ticket' } },
    ]));
    const expired = await service.invoke('login.start', { provider: config.id });
    assert.equal((await service.invoke('login.poll', { sessionId: expired.sessionId })).status, 'expired');
    const confirmed = await service.invoke('login.start', { provider: config.id });
    assert.equal((await service.invoke('login.poll', { sessionId: confirmed.sessionId })).status, 'confirmed');
  });

  test(`${config.id}: QR accepts only recognizable waiting and scanned progress responses`, async (t) => {
    const cases = [
      [{ status: 'pending' }, 'pending'],
      [{ status: 'scanned' }, 'scanned'],
      [{ message: '二维码未扫描' }, 'pending'],
      [{ message: '请扫码登录' }, 'pending'],
      [{ message: 'not scanned' }, 'pending'],
      [{ message: 'waiting for scan' }, 'pending'],
      [{ message: '已扫码，请确认' }, 'scanned'],
      [{ message: 'QR code scanned' }, 'scanned'],
    ];
    const { service } = await fixture(t, qrRoute(config, cases.map(([response]) => response)));
    const { sessionId } = await service.invoke('login.start', { provider: config.id });
    for (const [, expected] of cases) assert.equal((await service.invoke('login.poll', { sessionId })).status, expected);
  });

  test(`${config.id}: QR explicit failures are not hidden as waiting or scanned and never publish credentials`, async (t) => {
    const responses = [
      { status: 'error', message: 'waiting for scan' },
      { status: 'failed' },
      { status: 'pending', message: 'synthetic-private authentication failure' },
      { status: 50004001, message: 'synthetic-private authentication failure' },
      { status: 'scanned', message: '扫码失败，请重试' },
      { status: 5000999, message: 'synthetic-private upstream error' },
      { message: 'scan failed' },
    ];
    const { service, requests } = await fixture(t, qrRoute(config, responses.slice()));
    const { sessionId } = await service.invoke('login.start', { provider: config.id });
    for (const response of responses) {
      await assert.rejects(service.invoke('login.poll', { sessionId }),
        (error) => error.code === 'AUTH_FAILED' && !String(error).includes('synthetic-private'),
        JSON.stringify(response));
    }
    const settings = await service.invoke('settings.get');
    assert.equal(settings.accounts.find((item) => item.provider === config.id).configured, false);
    assert.equal(requests.some((item) => item.url.pathname === '/account/info'), false);
  });

  test(`${config.id}: QR unknown status, empty structure and missing confirmed ticket stay format failures`, async (t) => {
    const responses = [
      {}, [], { status: 'unexpected' }, { status: 1234567 }, { data: { message: 'ok' } },
      { status: 2000000 }, { status: 'confirmed' },
    ];
    const { service, requests } = await fixture(t, qrRoute(config, responses.slice()));
    const { sessionId } = await service.invoke('login.start', { provider: config.id });
    for (const response of responses) {
      await assert.rejects(service.invoke('login.poll', { sessionId }), { code: 'UPSTREAM_FORMAT' }, JSON.stringify(response));
    }
    assert.equal(requests.some((item) => item.url.pathname === '/account/info'), false);
  });

  test(`${config.id}: QR pending/scanned/confirmed is private, concurrent poll finalizes once and persisted account survives restart`, async (t) => {
    const statuses = [{ status: 'pending' }, { message: '已扫码，请确认' }, { status: 2000000, data: { service_ticket: 'synthetic-service-ticket' } }];
    const { service, requests, root, document } = await fixture(t, qrRoute(config, statuses));
    const start = await service.invoke('login.start', { provider: config.id });
    assert.equal(start.status, 'pending');
    assert.equal(start.expiresAt, now + 180_000);
    assert.equal(start.challenge.type, 'url');
    assert.equal(new URL(start.challenge.url).hostname, config.id === 'quark' ? 'su.quark.cn' : 'su.uc.cn');
    assert.equal(Object.hasOwn(start, 'session'), false);
    assert.equal((await service.invoke('login.poll', { sessionId: start.sessionId })).status, 'pending');
    assert.equal((await service.invoke('login.poll', { sessionId: start.sessionId })).status, 'scanned');
    const result = await Promise.all([
      service.invoke('login.poll', { sessionId: start.sessionId }),
      service.invoke('login.poll', { sessionId: start.sessionId }),
    ]);
    assert.equal(result[0].status, 'confirmed');
    assert.equal(result[1].status, 'confirmed');
    assert.doesNotMatch(JSON.stringify(result), /synthetic-confirmed|credentials|cookie":/);
    assert.match((await document()).accounts[config.id].credentials.cookie, /__puus=synthetic-confirmed/);
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/getServiceTicketByQrcodeToken')).length, 3);
    const before = requests.length;
    assert.equal((await service.invoke('login.poll', { sessionId: start.sessionId })).status, 'confirmed');
    assert.equal(requests.length, before);
    await service.close();
    const restored = createPanService({ dataRoot: root, transport: { async request() { throw new Error('No restart network'); }, async close() {} }, now: () => now });
    try {
      const settings = await restored.invoke('settings.get');
      const saved = settings.accounts.find((item) => item.provider === config.id);
      assert.equal(saved.configured, true);
      assert.deepEqual(saved.fields, ['cookie']);
      assert.doesNotMatch(JSON.stringify(settings), /synthetic-confirmed/);
    } finally { await restored.close(); }
  });

  test(`${config.id}: QR cancellation and upstream expiry are distinct and stop further polling`, async (t) => {
    const statuses = [{ status: 'expired', message: '二维码已过期' }];
    const { service, requests } = await fixture(t, qrRoute(config, statuses));
    const expired = await service.invoke('login.start', { provider: config.id });
    assert.equal((await service.invoke('login.poll', { sessionId: expired.sessionId })).status, 'expired');
    const cancelled = await service.invoke('login.start', { provider: config.id });
    assert.equal((await service.invoke('login.cancel', { sessionId: cancelled.sessionId })).status, 'cancelled');
    const before = requests.length;
    assert.equal((await service.invoke('login.poll', { sessionId: cancelled.sessionId })).status, 'cancelled');
    assert.equal(requests.length, before);
  });

  test(`${config.id}: QR confirmed is withheld when credential persistence fails`, async (t) => {
    const { service, root } = await fixture(t, qrRoute(config, [{ status: 2000000, data: { service_ticket: 'synthetic-ticket' } }]));
    const start = await service.invoke('login.start', { provider: config.id });
    await mkdir(join(root, 'pan/accounts.json'), { recursive: true });
    await assert.rejects(service.invoke('login.poll', { sessionId: start.sessionId }), { code: 'STORAGE_ERROR' });
    const settings = await service.invoke('settings.get');
    assert.equal(settings.accounts.find((item) => item.provider === config.id).configured, false);
  });

  test(`${config.id}: login refuses cross-provider or HTTP redirect before sending cookies`, async (t) => {
    let location;
    const { service, requests } = await fixture(t, qrRoute(config, [], {
      '/cas/ajax/getTokenForQrcodeLogin': () => ({ status: 302, headers: { location }, body: '' }),
    }));
    for (const target of [
      'http://untrusted.example/login', `http://${config.host}/login`,
      `https://${config.id === 'uc' ? 'uop.quark.cn' : 'api.open.uc.cn'}/login`,
    ]) {
      location = target;
      await assert.rejects(service.invoke('login.start', { provider: config.id }), { code: 'UPSTREAM_FORMAT' });
    }
    assert.equal(requests.some((item) => item.url.hostname === 'untrusted.example'), false);
    assert.equal(requests.some((item) => item.url.href === location), false);
  });

  test(`${config.id}: QR follows a trusted relative redirect with its scoped cookies`, async (t) => {
    const { service, requests } = await fixture(t, qrRoute(config, [], {
      '/cas/ajax/getTokenForQrcodeLogin': () => ({
        status: 302, headers: { location: './token-result', 'set-cookie': ['scoped=synthetic-sso; Path=/cas/ajax; Secure'] }, body: '',
      }),
      '/cas/ajax/token-result': (request) => {
        assert.match(request.headers.cookie, /scoped=synthetic-sso/);
        assert.equal(request.redirect, 'manual');
        return { value: { data: { token: 'synthetic-redirect-token' } } };
      },
    }));
    const start = await service.invoke('login.start', { provider: config.id });
    assert.equal(new URL(start.challenge.url).searchParams.get('token'), 'synthetic-redirect-token');
    assert.equal(requests.at(-1).url.pathname, '/cas/ajax/token-result');
  });

  test(`${config.id}: QR rejects redirect loops and malformed locations without publishing a session`, async (t) => {
    let location = '/cas/ajax/getTokenForQrcodeLogin';
    const { service, requests } = await fixture(t, qrRoute(config, [], {
      '/cas/ajax/getTokenForQrcodeLogin': () => ({ status: 302, headers: { location }, body: '' }),
    }));
    await assert.rejects(service.invoke('login.start', { provider: config.id }), { code: 'UPSTREAM_FORMAT' });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/getTokenForQrcodeLogin')).length, 2);
    location = 'https://[invalid-host';
    await assert.rejects(service.invoke('login.start', { provider: config.id }), { code: 'UPSTREAM_FORMAT' });
    assert.equal(requests.filter((item) => item.url.pathname.endsWith('/getTokenForQrcodeLogin')).length, 3);
  });

  test(`${config.id}: QR startup redirect chains stop at the reference session deadline`, async (t) => {
    let clock = now, redirects = 0;
    const route = qrRoute(config, []);
    const { service, requests } = await fixture(t, (request) => {
      if (request.url.pathname === '/cas/custom/login' || request.url.pathname === '/') return route(request);
      clock += 60_000;
      redirects += 1;
      return { status: 302, headers: { location: `/cas/ajax/redirect-${redirects}` }, body: '' };
    }, { now: () => clock });
    await assert.rejects(service.invoke('login.start', { provider: config.id }), { code: 'LOGIN_EXPIRED' });
    assert.equal(redirects, 3);
    assert.equal(requests.length, 5);
  });
}
