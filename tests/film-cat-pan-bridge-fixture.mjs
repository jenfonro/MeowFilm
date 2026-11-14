import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { createCatRuntime } from '../packages/script-host/src/index.js';
import { decodePanResource } from '../packages/script-host/src/cat-output.js';
import { PanError } from '../packages/pan-runtime/src/index.js';
import { connectFilmPlayback, createAppRuntime, createCatScriptModule, createPanRuntimeModule,
  createRuntimeClient, createTMDBDataModule } from '../apps/runtime/entry.mjs';

export const bridgeTitle = 'Bridge Series';
export const bridgeShare = { provider: 'quark', shareId: 'bridge-share', password: 'test-code',
  url: 'https://pan.quark.cn/s/bridge-share' };
export const bridgeFiles = [
  { fid: 'episode-1-high', file_name: `${bridgeTitle}.S01E01.mkv`, file_type: 1,
    share_fid_token: 'synthetic-file-high', size: 123456,
    video_meta: { width: 3840, height: 2160, fps: '60000/1001', codec: 'hevc' } },
  { fid: 'episode-1-low', file_name: `${bridgeTitle}.S01E01.mp4`, file_type: 1,
    share_fid_token: 'synthetic-file-low', size: 65432,
    video_meta: { width: 1920, height: 1080, fps: 25, codec: 'avc1' } },
  { fid: 'episode-2', file_name: `${bridgeTitle}.S01E02.mp4`, file_type: 1,
    share_fid_token: 'synthetic-file-two', size: 54321,
    video_meta: { width: 1920, height: 1080, fps: 25, codec: 'avc1' } },
];
export const bridgeCrossShares = [
  { ...bridgeShare, shareId: 'bridge-cross-quark', url: 'https://pan.quark.cn/s/bridge-cross-quark' },
  { provider: 'uc', shareId: 'bridge-cross-uc', password: 'synthetic-uc-code',
    url: 'https://drive.uc.cn/s/bridge-cross-uc' },
];
export const bridgeUCFiles = [
  { fid: 'episode-1-high', file_name: `${bridgeTitle}.S01E01.mkv`, file_type: 1,
    share_fid_token: 'synthetic-uc-high', size: 98765,
    video_meta: { width: 3840, height: 2160, fps: 30, codec: 'hevc', quality: '4K' } },
  { fid: 'episode-1-low', file_name: `${bridgeTitle}.S01E01.mp4`, file_type: 1,
    share_fid_token: 'synthetic-uc-low', size: 43210,
    video_meta: { width: 1280, height: 720, fps: 25, codec: 'avc1', quality: '720P' } },
];
export const bridgeMasterURL = 'https://manifest.bridge.invalid/show/master.m3u8';
export const bridgeMaster = '#EXTM3U\n' +
  '#EXT-X-STREAM-INF:BANDWIDTH=5000,RESOLUTION=3840x2160,FRAME-RATE=59.94\nhigh/index.m3u8?part=1\n' +
  '#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1920x1080,FRAME-RATE=25\n../low.m3u8\n';
export const bridgeScenarios = ['success', 'list-empty', 'list-http', 'list-auth', 'list-format',
  'list-timeout', 'prepare-error', 'play-error', 'play-format'];
const fixtureArea = fileURLToPath(new URL('../tmp/test-results/film-playback/cat-pan-fixture/', import.meta.url));
const workerThreads = createRequire(import.meta.url)('node:worker_threads');
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const json = (response, value, status = 200) => {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(value));
};
const listen = server => new Promise((done, reject) => {
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => { server.off('error', reject); done(); });
});
const closeServer = server => new Promise(done => {
  server.closeAllConnections();
  server.close(done);
});
export function bridgeOkay(reply) {
  if (reply.ok !== true) throw Object.assign(new Error(reply.code), { code: reply.code });
  return reply.value;
}
export async function waitForBridge(read, message = 'fixture state', timeout = 10000) {
  const deadline = Date.now() + timeout;
  do {
    const value = await read();
    if (value) return value;
    await delay(5);
  } while (Date.now() < deadline);
  throw new Error(`Timed out waiting for ${message}`);
}

// Cat owns its interceptor in a Worker, not in host.get()'s public snapshot.
// This fixture-only preload wraps that actual instance and delegates every
// request/body/response unchanged. It observes fields, never resolves a share.
function interceptorObserverSource() {
  return `
import { CatSession } from ${JSON.stringify(new URL('../packages/script-host/src/cat-session.js', import.meta.url).href)};
const start = CatSession.prototype.start;
CatSession.prototype.start = function(...args) {
  if (this.installationId === 'cat-pan-bridge') {
    const session = this, interceptor = this.interceptor, original = interceptor.request;
    let nextId = 0;
    interceptor.request = function(url, options, scope, open, own) {
      const observationId = ++nextId, before = session.nativeRequests.size;
      const visible = new URL(url);
      for (const key of visible.searchParams.keys())
        if (!['pwd_id', 'pdir_fid', '_page', 'pr', 'fr'].includes(key))
          visible.searchParams.set(key, '<redacted>');
      const stage = url.pathname.endsWith('/token') ? 'token' : url.pathname.endsWith('/detail') ? 'list' : 'other';
      const observe = (phase, value = {}) => session.networkEvent({
        fixtureLayer: 'pan-interceptor', phase, observationId, stage,
        scope: session.scopeIdentity(scope), ...value,
      });
      const client = original.call(this, url, options, scope, settings => {
        observe('forward', { networkRequests: session.nativeRequests.size });
        return open(settings);
      }, own);
      observe('request', { url: visible.href, method: options.method ?? 'GET',
        handled: client !== null, networkRequestsBefore: before,
        knownShares: [...(scope.panShares?.values() ?? [])].map(share =>
          ({ provider: share.provider, shareId: share.shareId, passwordPresent: !!share.password })) });
      if (!client) return client;
      const final = client._final;
      client._final = function(done) {
        const bytes = Buffer.concat(this.chunks);
        let body = {}, bodyMalformed = false;
        try { if (bytes.length) body = JSON.parse(bytes.toString()); }
        catch { bodyMalformed = true; }
        finally { bytes.fill(0); }
        const present = Object.hasOwn(body, 'passcode') || url.searchParams.has('passcode');
        observe('body', { shareId: body.pwd_id ?? url.searchParams.get('pwd_id'),
          passwordFieldPresent: present, passwordPresent: !!(body.passcode ?? url.searchParams.get('passcode')),
          bodyMalformed, networkRequests: session.nativeRequests.size });
        return final.call(this, done);
      };
      client.once('response', response => {
        observe('response', { status: response.statusCode, captures: [...scope.panShares.values()].map(share =>
          ({ provider: share.provider, shareId: share.shareId, passwordPresent: !!share.password })) });
        response.once('end', () => observe('end', { complete: response.complete,
          readableEnded: response.readableEnded, networkRequests: session.nativeRequests.size }));
      });
      client.once('close', () => queueMicrotask(() => observe('release', {
        requestClosed: client.closed, requestDestroyed: client.destroyed,
        networkRequests: session.nativeRequests.size,
      })));
      return client;
    };
  }
  return start.apply(this, args);
};
`;
}

function catSource(origin) {
  return `
const https = require('https');
const upstream = (url, payload) => new Promise((resolve, reject) => {
  const body = payload === undefined ? undefined : JSON.stringify(payload);
  const request = https.request(url, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' }
  }, response => {
    const chunks = [];
    response.on('data', chunk => chunks.push(Buffer.from(chunk)));
    response.once('error', reject);
    response.once('end', () => {
      try { resolve({ value: JSON.parse(Buffer.concat(chunks).toString()),
        complete: response.complete, status: response.statusCode }); }
      catch (error) { reject(error); }
    });
  });
  request.once('error', reject);
  request.end(body);
});
module.exports.search = (query, _quick, page) => ({ page: page || 1, pagecount: 1,
  list: [{ vod_id: 'bridge-detail', vod_name: ${JSON.stringify(bridgeTitle)}, vod_year: '2025' }] });
const reference = (token, list) => Buffer.from(JSON.stringify({ fid: list.value.data.list[0].fid,
  stoken: token.value.data.stoken })).toString('base64');
module.exports.detail = async id => {
  if (['descriptor', 'descriptor-direct', 'hls-master', 'headers-invalid', 'header-value-invalid', 'format-invalid'].includes(id))
    return { list: [{ vod_id: id, vod_name: ${JSON.stringify(bridgeTitle)},
      vod_play_from: '原始线路', vod_play_url: '正片$' + id }] };
  if (id === 'cross-provider') {
    const references = [];
    for (const share of ${JSON.stringify(bridgeCrossShares)}) {
      const host = share.provider === 'quark' ? 'https://drive.quark.cn' : 'https://pc-api.uc.cn';
      const token = await upstream(host + '/1/clouddrive/share/sharepage/token',
        { pwd_id: share.shareId, passcode: share.password });
      const list = await upstream(host + '/1/clouddrive/share/sharepage/detail?pwd_id=' + share.shareId +
        '&stoken=' + encodeURIComponent(token.value.data.stoken));
      references.push(reference(token, list));
    }
    return { list: [{ vod_id: id, vod_name: ${JSON.stringify(bridgeTitle)},
      vod_play_from: '夸克$$$UC', vod_play_url: references.map(value => '第一季$' + value).join('$$$') }] };
  }
  const opaque = id === 'unknown-share', unknown = id === 'unknown-provider';
  const host = unknown ? 'https://unrecognized-provider.invalid' : 'https://drive.quark.cn';
  const token = await upstream(host + '/1/clouddrive/share/sharepage/token',
    { pwd_id: opaque ? 'x' : ${JSON.stringify(bridgeShare.shareId)},
      passcode: ${JSON.stringify(bridgeShare.password)} });
  const list = await upstream(host + '/1/clouddrive/share/sharepage/detail?pwd_id=' +
    (opaque ? 'x' : ${JSON.stringify(bridgeShare.shareId)}) + '&stoken=' +
    encodeURIComponent(token.value.data.stoken) + (id === 'explicit-empty-password' ? '&passcode=' : ''));
  if (['known-then-unknown-share', 'known-then-invalid-share'].includes(id)) {
    const invalid = id === 'known-then-unknown-share' ? 'bridge-unknown-share' : 'root';
    const fallback = await upstream(host + '/1/clouddrive/share/sharepage/detail?pwd_id=' + invalid +
      '&stoken=' + encodeURIComponent(token.value.data.stoken));
    return { interceptedShape: { complete: token.complete && list.complete && fallback.complete,
      tokenStatus: token.status, listStatus: list.status, fallbackStatus: fallback.status },
      fallback: { fid: fallback.value.data.list[0].fid },
      list: [{ vod_id: id, vod_name: ${JSON.stringify(bridgeTitle)},
        vod_play_from: '夸克', vod_play_url: '第一季$' + reference(token, list) }] };
  }
  return { interceptedShape: { complete: token.complete && list.complete,
    tokenStatus: token.status, listStatus: list.status },
    list: [{ vod_id: id, vod_name: ${JSON.stringify(bridgeTitle)},
      vod_play_from: id === 'unknown-line-label' ? '原始自定义线路' : '夸克',
      vod_play_url: '第一季$' + reference(token, list) }] };
};
module.exports.play = (_flag, id) => id === 'hls-master' ? {
  parse: 0, url: ${JSON.stringify(bridgeMasterURL)}, format: 'hls',
  headers: { 'X-Bridge-Playback': 'synthetic' },
} : ({
  parse: 0, url: ${JSON.stringify(origin)} + '/media/' + id + '.mpd',
  ...(id === 'descriptor-direct' ? {} : { headers: id === 'headers-invalid' ? { 'Invalid Header': 'value' }
    : id === 'header-value-invalid' ? { 'X-Bridge-Playback': 'synthetic\\r\\nInvalid: value' }
      : { 'X-Bridge-Playback': 'synthetic' } }),
  format: id === 'format-invalid' ? {} : 'dash',
  drm: { scheme: 'cenc', clearKeys: { '00000000000000000000000000000001': '00000000000000000000000000000002' } }
});
`;
}

/** The only substitutes are Cat's outbound network and the pan/TMDB upstream I/O.
 * All script interception, provider parsing, transfers, business rules and HTTP
 * responses run through the production owners. No media body is ever served. */
export async function startCatPanBridgeFixture({ scenario = 'success', interception = true } = {}) {
  if (!bridgeScenarios.includes(scenario)) throw new Error('Unknown fixture scenario');
  mkdirSync(fixtureArea, { recursive: true });
  const dataRoot = mkdtempSync(join(fixtureArea, 'case-'));
  const observerPath = join(dataRoot, 'pan-interceptor-observer.mjs');
  writeFileSync(observerPath, interceptorObserverSource());
  const records = [], panInputs = [], providerRequests = [], gates = new Map(), stored = new Map(), listed = new Map();
  const counters = { scriptNetwork: 0, mediaHead: 0, mediaBody: 0, panClose: 0,
    interceptorRequests: 0, interceptorResponses: 0, interceptorReleases: 0, interceptorForwards: 0,
    manifestDocuments: 0 };
  const note = (layer, phase, value = {}) => records.push({ sequence: records.length + 1, layer, phase, ...value });
  let app, client, cat, source, closing, currentScenario = scenario, masterDocument = bridgeMaster;
  const control = http.createServer((request, response) => {
    void (async () => {
      const url = new URL(request.url, 'http://fixture.invalid');
      const hlsVariant = ['/show/high/index.m3u8', '/low.m3u8'].includes(url.pathname);
      if (url.pathname.startsWith('/media/') || hlsVariant) {
        const authorized = /(?:^|;\s*)bridge-fixture=synthetic(?:;|$)/.test(request.headers.cookie ?? '') ||
          request.headers['x-bridge-playback'] === 'synthetic';
        if (request.method === 'HEAD') {
          counters.mediaHead++;
          note('media', hlsVariant ? 'hls-headers' : 'headers',
            { path: url.pathname + url.search, authorized });
          response.writeHead(authorized ? 200 : 403, {
            'content-type': hlsVariant ? 'application/vnd.apple.mpegurl' : 'video/mp4', 'content-length': '1024',
          });
        } else {
          counters.mediaBody++;
          note('media', 'body-forbidden', { method: request.method });
          response.writeHead(409, { 'content-length': '0' });
        }
        response.end(); return;
      }
      if (request.method === 'GET' && url.pathname === '/fixture') { json(response, description()); return; }
      if (request.method === 'GET' && url.pathname === '/evidence') {
        json(response, { records, counters, scenario: currentScenario }); return;
      }
      if (request.method === 'POST' && url.pathname === '/scenario') {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const input = JSON.parse(Buffer.concat(chunks).toString());
        setScenario(input.scenario);
        json(response, { scenario: currentScenario }); return;
      }
      if (request.method === 'POST' && url.pathname === '/release') {
        for (const gate of gates.values()) gate.release.resolve();
        json(response, { released: true }); return;
      }
      response.writeHead(404); response.end();
    })().catch(error => json(response, { error: error.code ?? error.message }, 500));
  });
  await listen(control);
  const controlUrl = `http://127.0.0.1:${control.address().port}`;
  const reply = (request, value, status = 200) => ({
    url: request.url, status, headers: { 'content-type': 'application/json' },
    body: Buffer.from(JSON.stringify(value)),
  });
  const stageOf = url => url.pathname.endsWith('/share/sharepage/token') ? 'token'
    : url.pathname.endsWith('/share/sharepage/detail') ? 'list'
      : url.pathname.endsWith('/file/sort') ? 'destination'
        : url.pathname.endsWith('/share/sharepage/save') ? 'prepare'
          : url.pathname.endsWith('/file/download') ? 'play' : 'unknown';
  const transport = {
    async request(request) {
      const url = new URL(request.url), phase = stageOf(url);
      const provider = url.hostname === 'drive.quark.cn' ? 'quark'
        : url.hostname === 'pc-api.uc.cn' ? 'uc' : null;
      if (!provider) throw new Error(`Unexpected provider host: ${url.hostname}`);
      const body = request.body === undefined ? undefined : JSON.parse(request.body.toString());
      const method = request.method ?? 'GET';
      providerRequests.push({ provider, url, method, body, signal: request.signal });
      note('provider', phase, { provider, host: url.hostname, path: url.pathname, method,
        ...(body?.pwd_id ? { shareId: body.pwd_id, passwordPresent: !!body.passcode } : {}),
        ...(phase === 'list' ? { shareId: url.searchParams.get('pwd_id'),
          parentFileId: url.searchParams.get('pdir_fid'), page: url.searchParams.get('_page') } : {}),
        ...(phase === 'destination' ? { parentFileId: url.searchParams.get('pdir_fid') } : {}),
        ...(body?.fid_list ? { fileIds: body.fid_list, destinationId: body.to_pdir_fid } : {}),
        ...(body?.fid ? { storedFileId: body.fid } : {}) });
      const gate = gates.get(phase);
      if (gate) { gate.signal = request.signal; gate.entered.resolve(); await gate.release.promise; }
      if (phase === 'list' && currentScenario === 'list-timeout') throw new PanError('TIMEOUT');
      if (phase === 'list' && ['list-http', 'list-auth'].includes(currentScenario))
        return reply(request, {}, currentScenario === 'list-auth' ? 401 : 503);
      if (phase === 'list' && currentScenario === 'list-format') return reply(request, { code: 0, data: {} });
      if (phase === 'token') return reply(request, { code: 0, data: { stoken: 'synthetic-provider-token' } });
      if (phase === 'list') {
        const share = [bridgeShare, ...bridgeCrossShares].find(item =>
          item.provider === provider && item.shareId === url.searchParams.get('pwd_id'));
        if (!share) return reply(request, { code: 400, message: 'Unknown share' });
        const parent = url.searchParams.get('pdir_fid');
        const files = provider === 'uc' ? bridgeUCFiles
          : share.shareId === bridgeShare.shareId ? bridgeFiles : bridgeFiles.slice(0, 2).reverse();
        const list = currentScenario === 'list-empty' ? [] : parent === '0'
          ? [{ fid: 'season-one', file_name: 'Season 01', dir: true },
            { fid: 'readme', file_name: 'readme.txt', file_type: 1 }]
          : structuredClone(files);
        for (const file of list)
          if (file.share_fid_token) listed.set(`${provider}:${share.shareId}:${file.fid}`, file);
        note('normalization', 'upstream-files', { provider, shareId: share.shareId, parentFileId: parent,
          files: list.map(item => ({ fileId: item.fid, name: item.file_name, directory: item.dir === true,
            ...(item.video_meta ? { video_meta: item.video_meta } : {}) })) });
        return reply(request, { code: 0, data: { list, has_more: false } });
      }
      if (phase === 'destination') return reply(request, { code: 0, data: {
        list: url.searchParams.get('pdir_fid') === 'fixture-destination'
          ? [...stored.values()].filter(item => item.provider === provider)
            .map(({ id, file }) => ({ ...file, fid: id, fid_token: 'synthetic-stored-token' }))
          : [{ fid: 'fixture-destination', file_name: 'MeowFilm', dir: true }],
        has_more: false,
      } });
      if (phase === 'prepare') {
        if (currentScenario === 'prepare-error') return reply(request, { code: 403 });
        const file = listed.get(`${provider}:${body.pwd_id}:${body.fid_list?.[0]}`);
        if (!file || body.fid_token_list?.[0] !== file.share_fid_token ||
          body.stoken !== 'synthetic-provider-token')
          throw new Error('Transfer did not originate from this provider list');
        const id = `stored-${file.fid}`;
        stored.set(`${provider}:${id}`, { provider, id, file });
        return reply(request, { code: 0, task_sync: true,
          data: { save_as: { save_as_top_fids: [id] } } });
      }
      if (phase === 'play') {
        if (currentScenario === 'play-error') return reply(request, { code: 403 });
        if (!stored.has(`${provider}:${body.fid}`)) throw new Error('Playback bypassed provider prepare');
        const mediaPath = provider === 'quark' ? body.fid : `${provider}/${body.fid}`;
        return reply(request, { code: 0, data: [{ fid: body.fid,
          ...(currentScenario === 'play-format' ? {} : {
            play_url: `${controlUrl}/media/${mediaPath}.mkv`,
            download_url: `${controlUrl}/media/${mediaPath}.mkv`,
          }) }] });
      }
      throw new Error(`Unexpected provider I/O: ${url.pathname}`);
    },
    async close() { counters.panClose++; note('provider', 'close'); },
  };
  function setScenario(value) {
    if (!bridgeScenarios.includes(value)) throw new Error('Unknown fixture scenario');
    currentScenario = value;
  }
  function description() {
    const directTarget = { id: 'site:bridge-detail', kind: 'source', title: bridgeTitle,
      source: { key: source.key, detailId: 'bridge-detail' } };
    const metadataTarget = { id: 'tmdb:tv:321', kind: 'tv', title: bridgeTitle,
      metadataProvider: 'tmdb', metadataId: 321 };
    return { baseUrl: app.baseUrl, controlUrl, source, directTarget, metadataTarget,
      title: bridgeTitle, scenarios: bridgeScenarios, mediaPolicy: 'headers-only' };
  }
  try {
    const catModule = createCatScriptModule({ dataRoot, runtimeFactory: options => {
      cat = createCatRuntime({ ...options, onDiagnostic: value => note('cat-runtime', 'diagnostic',
        { code: value?.code, phase: value?.phase, event: value?.event }),
      onScriptError: value => note('cat-runtime', 'script-error', { code: value?.code,
        phase: value?.phase, message: value?.message }),
      onNetwork: event => {
        if (event.fixtureLayer !== 'pan-interceptor') return;
        const { fixtureLayer, phase, ...value } = event;
        note(fixtureLayer, phase, value);
        const counter = { request: 'interceptorRequests', response: 'interceptorResponses',
          release: 'interceptorReleases', forward: 'interceptorForwards' }[phase];
        // Keep every observation, but count only requests actually intercepted.
        if (counter && (phase !== 'request' || value.handled)) counters[counter]++;
      },
      network: async request => {
        counters.scriptNetwork++;
        const url = new URL(request.url);
        if (request.url === bridgeMasterURL) {
          counters.manifestDocuments++;
          note('script-network', 'manifest', { url: request.url, method: request.options.method,
            headerNames: Object.keys(request.options.headers) });
          return { statusCode: 200, headers: { 'content-type': 'application/vnd.apple.mpegurl' }, body: masterDocument };
        }
        const body = request.options.body === undefined ? {} : JSON.parse(Buffer.from(request.options.body).toString());
        note('script-network', stageOf(url), { host: url.hostname, path: url.pathname, method: request.options.method,
          shareId: body.pwd_id ?? url.searchParams.get('pwd_id'),
          passwordPresent: !!(body.passcode ?? url.searchParams.get('passcode')) });
        const value = url.pathname.endsWith('/token') ? { code: 0, data: { stoken: 'script-original-token' } }
          : { code: 0, data: { list: [{ fid: 'script-original-file' }] } };
        return { statusCode: 200, headers: { 'content-type': 'application/json' }, body: JSON.stringify(value) };
      } });
      const workerSession = cat.workerSession;
      cat.workerSession = function(...args) {
        const worker = workerSession.apply(this, args), start = worker.start;
        worker.start = function(...input) {
          // WorkerSession.start constructs its Worker before its first await.
          // Pass only the fixture preload explicitly so node --test runner flags
          // from the parent process are not inherited by the Cat Worker.
          const OriginalWorker = workerThreads.Worker;
          workerThreads.Worker = class extends OriginalWorker {
            constructor(url, options) {
              super(url, { ...options,
                execArgv: [...(options.execArgv ?? []), '--import', pathToFileURL(observerPath).href] });
            }
          };
          syncBuiltinESMExports();
          try { return start.apply(this, input); }
          finally { workerThreads.Worker = OriginalWorker; syncBuiltinESMExports(); }
        };
        return worker;
      };
      return cat;
    } });
    const invoke = catModule.methods['cat.script.invoke'];
    catModule.methods['cat.script.invoke'] = async (input, context) => {
      const observationStart = records.length;
      note('cat', input.operation, { siteId: input.siteId,
        ...(input.operation === 'detail' ? { detailId: input.payload.id } : {}),
        ...(input.operation === 'play' ? { playId: input.payload.id, flag: input.payload.flag } : {}) });
      const result = await invoke(input, context);
      if (input.operation === 'detail' && result.ok && result.value._meow?.resources) {
        note('cat', 'intercepted', { complete: result.value.interceptedShape?.complete,
          responseShape: result.value.interceptedShape,
          ...(result.value.fallback ? { fallbackFileId: result.value.fallback.fid } : {}),
          requests: records.slice(observationStart).filter(row =>
            row.layer === 'pan-interceptor' && row.phase === 'request').map(row => {
            const body = records.slice(observationStart).find(value => value.layer === 'pan-interceptor' &&
              value.phase === 'body' && value.observationId === row.observationId);
            return { url: row.url, method: row.method, phase: row.stage,
              shareId: body?.shareId, passwordPresent: body?.passwordPresent };
          }),
          resources: result.value._meow.resources.map(item => {
            const share = decodePanResource(item.id);
            return { provider: share.provider, shareId: share.shareId, passwordPresent: !!share.password,
              installationId: share.installationId, siteId: share.siteId };
          }) });
      }
      if (input.operation === 'play' && result.ok) note('normalization', 'cat-play-result', {
        playId: input.payload.id, format: result.value.format ?? null, drm: result.value.drm ?? null,
        headerNames: Object.keys(result.value.headers ?? result.value.header ?? {}),
        media: result.value._meow?.media ?? [],
      });
      return result;
    };
    const panModule = createPanRuntimeModule({ dataRoot, transport });
    for (const name of ['list', 'prepare', 'play']) {
      const handler = panModule.methods[`pan.${name}`];
      panModule.methods[`pan.${name}`] = async (input, context) => {
        panInputs.push({ operation: name, input: structuredClone(input) });
        note('pan-service', name, { provider: input.provider,
          ...(input.share ? { shareUrl: input.share.url, passwordPresent: !!input.share.password } : {}),
          ...(input.file ? { fileId: input.file.fileId, kind: input.file.kind,
            shareId: input.file.shareId, accountRevision: input.file.accountRevision } : {}),
          ...(input.mode ? { mode: input.mode } : {}) });
        let result;
        try { result = await handler(input, context); }
        catch (error) { note('pan-service', `${name}-error`, { code: error.code }); throw error; }
        if (!result.ok) note('pan-service', `${name}-error`, { code: result.code });
        if (name === 'list' && result.ok) note('normalization', 'files', { provider: input.provider,
          files: result.value.files.map(item => ({
          name: item.name, path: item.path, size: item.size, fileId: item.file.fileId, provider: item.file.provider,
          shareId: item.file.shareId, accountRevision: item.file.accountRevision,
          quality: item.quality, resolution: item.resolution, fps: item.fps, codec: item.codec,
        })) });
        if (name === 'play' && result.ok) note('normalization', 'play-result', {
          provider: input.provider, fileId: result.value.file.fileId,
          accountRevision: result.value.file.accountRevision, mode: result.value.mode,
          headerNames: Object.keys(result.value.headers), format: result.value.format ?? null,
          drm: result.value.drm ?? null,
        });
        return result;
      };
    }
    const tmdb = createTMDBDataModule({ dataRoot, request: async input => {
      const url = new URL(input);
      note('metadata', 'request', { path: url.pathname });
      const appended = { credits: { cast: [], crew: [] }, similar: { results: [] },
        images: { logos: [], backdrops: [], posters: [] } };
      const season = { id: 1, season_number: 1, episodes: [1, 2].map(number => ({
        id: number, season_number: 1, episode_number: number, name: `Episode ${number}`,
        overview: `Bridge episode ${number}`, air_date: '2025-01-01',
      })) };
      const value = url.pathname === '/3/tv/321/season/1' ? season
        : url.pathname.startsWith('/3/search/') ? { results: [{ id: 321, media_type: 'tv',
          name: bridgeTitle, first_air_date: '2025-01-01' }], page: 1, total_pages: 1 }
          : { id: 321, name: bridgeTitle, genres: [], first_air_date: '2025-01-01', status: 'Ended',
            number_of_seasons: 1, seasons: [{ ...season, episode_count: 2 }], ...appended };
      return { status: 200, headers: { 'content-type': 'application/json' }, body: Buffer.from(JSON.stringify(value)) };
    } });
    const modules = [catModule, panModule, tmdb];
    connectFilmPlayback(modules, { dataRoot });
    app = createAppRuntime({ sessionId: randomUUID(), modules });
    await app.start();
    client = createRuntimeClient({ baseUrl: app.baseUrl });
    const call = async (operation, input = {}, options) => bridgeOkay(await client.call(operation, input, options));
    const status = await call('cat.script.start');
    const settings = JSON.parse(readFileSync(status.settingsPath, 'utf8'));
    settings.providers.quark = interception;
    settings.providers.uc = interception;
    writeFileSync(status.settingsPath, JSON.stringify(settings));
    await call('cat.script.refresh');
    await client.upload('cat', 'cat-pan-bridge', 1, Buffer.from(catSource(controlUrl))).then(bridgeOkay);
    const installReply = await client.call('cat.script.install', { installationId: 'cat-pan-bridge',
      sourceRevision: 1, enabled: true, name: 'Bridge site' });
    if (installReply.ok !== true) {
      const state = await client.call('cat.script.status', {});
      throw new Error(JSON.stringify({ install: installReply, instances: state.value?.instances?.map(row =>
        ({ state: row.state, error: row.error, sites: row.sites })), diagnostics: records.filter(row => row.layer === 'cat-runtime') }));
    }
    const installed = installReply.value;
    source = { key: 'cat:cat-pan-bridge:default', engine: 'cat', installationId: 'cat-pan-bridge',
      siteId: 'default', generation: installed.generation, name: 'Bridge site' };
    await call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'bridge-fixture=synthetic' } });
    await call('pan.settings.set', { provider: 'uc',
      credentials: { cookie: 'bridge-fixture=synthetic; __puus=synthetic-transfer; Video-Auth=synthetic-video' } });
    await call('tmdb.data.settings.update', { apiKey: 'synthetic-fixture-key' });
    const snapshot = sessionId => call('film.session.snapshot', { sessionId });
    return { app, client, cat, source, dataRoot, controlUrl, records, counters, panInputs, providerRequests,
      ...description(), call, snapshot, setScenario,
      setMaster(document) { masterDocument = document; },
      invokeCat: (operation, payload = {}, options) => call('cat.script.invoke', {
        installationId: source.installationId, generation: source.generation, siteId: source.siteId, operation, payload,
      }, options),
      command: (sessionId, action, input = {}) => call('film.session.command', { sessionId, action, ...input }),
      gate(phase) {
        const value = { entered: deferred(), release: deferred(), signal: null };
        gates.set(phase, value);
        return value;
      },
      close() {
        return closing ??= (async () => {
          for (const gate of gates.values()) gate.release.resolve();
          await client.close();
          await app.close();
          await closeServer(control);
          rmSync(dataRoot, { recursive: true, force: true });
        })();
      },
    };
  } catch (error) {
    for (const gate of gates.values()) gate.release.resolve();
    await client?.close();
    await app?.close();
    await closeServer(control);
    rmSync(dataRoot, { recursive: true, force: true });
    throw error;
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const fixture = await startCatPanBridgeFixture({
    scenario: process.argv[2] ?? 'success', interception: process.argv[3] !== '--no-interception',
  });
  console.log(JSON.stringify({ ready: true, baseUrl: fixture.baseUrl, controlUrl: fixture.controlUrl,
    source: fixture.source, directTarget: fixture.directTarget, metadataTarget: fixture.metadataTarget }));
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => {
    fixture.close().then(() => { process.exitCode = 0; }, error => {
      console.error(error.code ?? error.message); process.exitCode = 1;
    });
  });
}
