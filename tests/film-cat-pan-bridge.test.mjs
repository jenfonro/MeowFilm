import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import { decodePanResource, parseM3U8Metadata } from '../packages/script-host/src/cat-output.js';
import { bridgeCrossShares, bridgeFiles, bridgeMaster, bridgeMasterURL, bridgeShare, bridgeUCFiles,
  startCatPanBridgeFixture, waitForBridge } from './film-cat-pan-bridge-fixture.mjs';

const tick = () => new Promise(resolve => setImmediate(resolve));
const highHLSURL = 'https://manifest.bridge.invalid/show/high/index.m3u8?part=1';
const lowHLSURL = 'https://manifest.bridge.invalid/low.m3u8';
const phases = (f, layer) => f.records.filter(row => row.layer === layer).map(row => row.phase);
const intercepted = (f, phase) => f.records.filter(row => row.layer === 'pan-interceptor' && row.phase === phase);
const detailTarget = (f, detailId) => ({ ...f.directTarget, id: `site:${detailId}`,
  source: { ...f.directTarget.source, detailId } });
async function releasedInterceptions(f, count) {
  await waitForBridge(() => f.counters.interceptorReleases === count &&
    f.cat.host.get(f.source.installationId).resources.networkRequests === 0, 'observed interceptor release');
  assert.equal(f.counters.interceptorRequests, count);
  assert.equal(f.counters.interceptorResponses, count);
  const requests = new Map(intercepted(f, 'request').map(row => [row.observationId, row]));
  assert.equal(requests.size, count);
  const ids = [...requests.keys()].sort((a, b) => a - b);
  for (const phase of ['body', 'response', 'end', 'release']) {
    const records = intercepted(f, phase);
    assert.deepEqual(records.map(row => row.observationId).sort((a, b) => a - b), ids);
    for (const row of records) assert.deepEqual(row.scope, requests.get(row.observationId).scope);
  }
  assert.ok([...requests.values()].every(row => row.scope.installationId === f.source.installationId &&
    row.scope.generation === f.source.generation && row.scope.siteId === f.source.siteId &&
    Number.isSafeInteger(row.scope.requestId)));
  assert.equal(intercepted(f, 'end').length, count);
  assert.ok(intercepted(f, 'end').every(row => row.complete === true && row.readableEnded === true));
  assert.ok(intercepted(f, 'release').every(row => row.requestClosed && row.requestDestroyed));
  assert.equal(intercepted(f, 'release').at(-1).networkRequests, 0);
}
async function fixture(t, options) {
  const f = await startCatPanBridgeFixture(options);
  t.after(() => f.close());
  return f;
}
async function open(f, input = {}) {
  const value = await f.call('film.session.open', {
    platform: 'test', entrySurface: 'search', target: f.directTarget, sources: [f.source], ...input,
  });
  return waitForBridge(async () => {
    const state = await f.snapshot(value.sessionId);
    return state.stage !== 'loading' && state;
  }, 'film initialization');
}
async function browse(f, state) {
  await f.command(state.sessionId, 'browse', { sourceId: state.sources[0].id });
  return waitForBridge(async () => {
    const next = await f.snapshot(state.sessionId);
    return !next.browsing.loading && next;
  }, 'provider directory');
}
async function pending(f, sessionId, previousId) {
  return waitForBridge(async () => {
    const state = await f.snapshot(sessionId);
    return state.attempt && state.attempt.id !== previousId && state;
  }, 'structured attempt');
}
async function terminal(f, sessionId) {
  return waitForBridge(async () => {
    const state = await f.snapshot(sessionId);
    return ['unavailable', 'error'].includes(state.stage) && state;
  }, 'terminal failure');
}
async function events(t, f, sessionId) {
  const controller = new AbortController();
  const response = await fetch(`${f.baseUrl}/v1/film/events/${sessionId}`, { signal: controller.signal });
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type'), /^text\/event-stream/);
  const reader = response.body.getReader(), decoder = new TextDecoder();
  t.after(async () => { controller.abort(); await reader.cancel().catch(() => {}); });
  let buffer = '';
  return {
    async next() {
      while (!buffer.includes('\n\n')) {
        const part = await reader.read();
        assert.equal(part.done, false);
        buffer += decoder.decode(part.value, { stream: true });
      }
      const boundary = buffer.indexOf('\n\n'), frame = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      const state = JSON.parse(frame.split('\n').find(row => row.startsWith('data: ')).slice(6));
      assert.ok(frame.includes(`id: ${state.sequence}`));
      f.records.push({ sequence: f.records.length + 1, layer: 'film-event', phase: 'snapshot',
        sessionId: state.sessionId, eventSequence: state.sequence, stage: state.stage,
        ...(state.attempt ? { attemptId: state.attempt.id, candidate: state.attempt.candidate } : {}) });
      return state;
    },
  };
}

async function publishedAttempt(observation, state) {
  const frames = [];
  do { frames.push(await observation.next()); }
  while (frames.at(-1).attempt?.id !== state.attempt.id);
  assert.ok(frames.every(frame => frame.sessionId === state.sessionId));
  assert.deepEqual(frames.at(-1).attempt, state.attempt);
  return frames;
}

async function hlsReceipt(t, f, state, expectedURL) {
  const receipt = await f.call('film.session.attempt', {
    sessionId: state.sessionId, attemptId: state.attempt.id,
  });
  assert.equal(receipt.attemptId, state.attempt.id);
  assert.equal(receipt.format, 'hls');
  assert.equal(receipt.drm, undefined);
  assert.equal(receipt.scope.sourceKey, f.source.key);
  assert.equal(receipt.scope.sourceEngine, 'cat');
  assert.equal(receipt.scope.sourceInstallationId, f.source.installationId);
  assert.equal(receipt.scope.accountProvider, undefined);
  assert.equal(new URL(receipt.url).origin + '/', receipt.mediaOrigin);
  assert.notEqual(receipt.url, expectedURL, 'source headers require the real local media route');
  const forwarded = [];
  // Replace only this synthetic upstream I/O while probing the public receipt.
  // The real media owner still selects the target, attaches headers and routes it.
  const mock = t.mock.method(https, 'request', function(url, ...args) {
    const address = new URL(url), options = args[0];
    assert.equal(address.origin, 'https://manifest.bridge.invalid');
    forwarded.push({ url: address.href, method: options.method,
      playbackHeader: options.headers['x-bridge-playback'] });
    return http.request(new URL(address.pathname + address.search, f.controlUrl), ...args);
  });
  try {
    const head = await fetch(receipt.url, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.headers.get('content-type'), 'application/vnd.apple.mpegurl');
  } finally { mock.mock.restore(); }
  assert.deepEqual(forwarded, [{ url: expectedURL, method: 'HEAD', playbackHeader: 'synthetic' }]);
  const address = new URL(expectedURL);
  const upstream = f.records.filter(row => row.layer === 'media' && row.phase === 'hls-headers').at(-1);
  assert.equal(upstream.path, address.pathname + address.search);
  assert.equal(upstream.authorized, true);
  assert.equal(f.counters.mediaBody, 0);
  return receipt;
}

test('Cat token/list interception retains the original share password and releases both native requests', async t => {
  const f = await fixture(t);
  const result = await f.invokeCat('detail', { id: 'bridge-detail' });
  assert.deepEqual(result.interceptedShape, { complete: true, tokenStatus: 200, listStatus: 200 });
  const requests = intercepted(f, 'request');
  assert.deepEqual(requests.map(row => [row.stage, row.method, row.handled]),
    [['token', 'POST', true], ['list', 'GET', true]]);
  assert.equal(requests[0].url, 'https://drive.quark.cn/1/clouddrive/share/sharepage/token');
  const listURL = new URL(requests[1].url);
  assert.equal(listURL.origin + listURL.pathname, 'https://drive.quark.cn/1/clouddrive/share/sharepage/detail');
  assert.equal(listURL.searchParams.get('pwd_id'), bridgeShare.shareId);
  assert.equal(listURL.searchParams.get('stoken'), '<redacted>');
  assert.deepEqual(intercepted(f, 'body').map(row =>
    [row.stage, row.shareId, row.passwordFieldPresent, row.passwordPresent, row.bodyMalformed]),
  [['token', bridgeShare.shareId, true, true, false], ['list', bridgeShare.shareId, false, false, false]]);
  assert.equal(requests[0].networkRequestsBefore, 0);
  assert.ok(intercepted(f, 'body').every(row => row.networkRequests >= 1));
  assert.deepEqual(requests[0].knownShares, []);
  assert.deepEqual(requests[1].knownShares,
    [{ provider: 'quark', shareId: bridgeShare.shareId, passwordPresent: true }]);
  assert.ok(intercepted(f, 'response').every(row => row.status === 200 &&
    row.captures.length === 1 && row.captures[0].shareId === bridgeShare.shareId && row.captures[0].passwordPresent));
  assert.equal(f.counters.scriptNetwork, 0);
  assert.equal(f.counters.interceptorForwards, 0);
  assert.equal(f.panInputs.length, 0, 'interception captures a share, it cannot resolve a file itself');
  const id = result.list[0].vod_play_url.split('$').at(-1);
  assert.equal(result._meow.resources[0].id, id);
  assert.equal(result._meow.captures[0].id, id);
  await releasedInterceptions(f, 2);
  const evidence = await (await fetch(`${f.controlUrl}/evidence`)).json();
  assert.equal(evidence.counters.interceptorReleases, 2);
  assert.equal(evidence.records.filter(row => row.layer === 'pan-interceptor' && row.phase === 'request').length, 2);
  assert.equal(JSON.stringify(evidence).includes(bridgeShare.password), false);
  assert.equal(JSON.stringify(evidence).includes('meow_'), false, 'evidence redacts interception tokens');
  assert.deepEqual(decodePanResource(id), { ...bridgeShare,
    installationId: f.source.installationId, siteId: f.source.siteId });
});

test('an explicitly empty list password overrides only that invocation, without leaking into a later detail', async t => {
  const f = await fixture(t);
  const empty = await f.invokeCat('detail', { id: 'explicit-empty-password' });
  const first = decodePanResource(empty.list[0].vod_play_url.split('$').at(-1));
  assert.equal(first.password, '');
  const current = await f.invokeCat('detail', { id: 'bridge-detail' });
  const next = decodePanResource(current.list[0].vod_play_url.split('$').at(-1));
  assert.equal(next.password, bridgeShare.password);
  assert.equal(next.shareId, first.shareId);
  assert.equal(f.counters.scriptNetwork, 0);
  assert.deepEqual(intercepted(f, 'body').map(row => [row.stage, row.passwordFieldPresent, row.passwordPresent]),
    [['token', true, true], ['list', true, false], ['token', true, true], ['list', false, false]]);
  await releasedInterceptions(f, 4);
});

for (const [interception, detailId] of [
  [false, 'bridge-detail'], [true, 'unknown-provider'], [true, 'unknown-share'],
]) {
  test(`Cat original network fallback: interception=${interception}, detail=${detailId}`, async t => {
    const f = await fixture(t, { interception });
    const result = await f.invokeCat('detail', { id: detailId });
    assert.equal(f.counters.scriptNetwork, 2);
    assert.deepEqual(phases(f, 'script-network'), ['token', 'list']);
    assert.equal(result.interceptedShape.complete, true);
    assert.equal(result._meow, undefined);
    const rawReference = JSON.parse(Buffer.from(result.list[0].vod_play_url.split('$').at(-1), 'base64'));
    assert.equal(rawReference.fid, 'script-original-file');
    assert.equal(rawReference.stoken, 'script-original-token');
    assert.equal(f.panInputs.length, 0);
    await waitForBridge(() => f.cat.host.get(f.source.installationId).resources.networkRequests === 0,
      'fallback response release');
  });
}

for (const [detailId, invalidShare] of [
  ['known-then-unknown-share', 'bridge-unknown-share'], ['known-then-invalid-share', 'root'],
]) {
  test(`Cat → pan → film retains the valid share after ${detailId} falls back to the original network`, async t => {
    const f = await fixture(t);
    let state = await browse(f, await open(f, { target: detailTarget(f, detailId) }));
    assert.equal(state.browsing.error, null);
    assert.equal(state.browsing.episodes.length, 3);
    const captured = f.records.find(row => row.layer === 'cat' && row.phase === 'intercepted');
    assert.deepEqual(captured.responseShape,
      { complete: true, tokenStatus: 200, listStatus: 200, fallbackStatus: 200 });
    assert.equal(captured.fallbackFileId, 'script-original-file');
    assert.deepEqual(captured.resources.map(row => [row.provider, row.shareId, row.passwordPresent]),
      [['quark', bridgeShare.shareId, true]]);
    assert.equal(f.counters.scriptNetwork, 1);
    assert.deepEqual(phases(f, 'script-network'), ['list']);
    assert.equal(f.records.find(row => row.layer === 'script-network').shareId, invalidShare);
    assert.equal(f.counters.interceptorForwards, 1);
    const bodies = intercepted(f, 'body');
    assert.deepEqual(bodies.map(row => row.shareId), [bridgeShare.shareId, bridgeShare.shareId, invalidShare]);
    assert.deepEqual(intercepted(f, 'request').at(-1).knownShares,
      [{ provider: 'quark', shareId: bridgeShare.shareId, passwordPresent: true }]);
    assert.deepEqual(intercepted(f, 'response').at(-1).captures,
      [{ provider: 'quark', shareId: bridgeShare.shareId, passwordPresent: true }]);
    assert.ok(intercepted(f, 'response').every(row => row.status === 200));
    await releasedInterceptions(f, 3);
    assert.equal(f.panInputs[0].input.share.url, bridgeShare.url);
    assert.equal(f.panInputs[0].input.share.password, bridgeShare.password);
    await f.command(state.sessionId, 'play');
    state = await pending(f, state.sessionId);
    assert.equal(state.attempt.candidate.episode, 1);
    assert.equal(state.attempt.candidate.provider, 'quark');
    assert.deepEqual(f.panInputs.map(row => row.operation), ['list', 'prepare', 'play']);
    assert.equal(f.panInputs[1].input.file.shareId, bridgeShare.shareId);
    assert.equal(f.panInputs[1].input.file.fileId, 'episode-1-high');
    const receipt = await f.call('film.session.attempt', { sessionId: state.sessionId, attemptId: state.attempt.id });
    assert.equal(receipt.scope.accountProvider, 'quark');
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
  });
}

for (const [detailId, lineLabel, title] of [
  ['bridge-detail', '夸克',
    'one Cat → Quark list/prepare/play → film HTTP/SSE chain preserves identities, media metadata and header-only routing'],
  ['unknown-line-label', '原始自定义线路',
    'a captured Cat share with a custom line label uses the exact Quark directory, file and film HTTP/SSE receipt'],
]) {
  test(title,
    { timeout: 15000 }, async t => {
      const f = await fixture(t);
      let state = await open(f, { target: detailTarget(f, detailId) });
      const { sessionId } = state;
      assert.equal(state.target.kind, 'source');
      assert.equal(state.sources[0].source.name, 'Bridge site');
      assert.equal(f.panInputs.length, 0, 'opening a specific site is not a request to search every site');
      assert.equal(f.counters.mediaHead, 0);
      const observation = await events(t, f, sessionId);
      assert.equal((await observation.next()).sessionId, sessionId);
      state = await browse(f, state);
      assert.equal(state.browsing.error, null);
      assert.deepEqual(state.browsing.lines, [{ id: lineLabel, label: lineLabel, provider: 'quark' }]);
      assert.notEqual(state.sources[0].source.name, state.browsing.lines[0].label);
      assert.deepEqual(state.browsing.episodes.map(row => [row.season, row.episode, row.quality]),
        [[1, 1, '4k_fps'], [1, 1, '1080p'], [1, 2, '1080p']]);
      assert.deepEqual(state.browsing.episodes.map(row => row.path),
        bridgeFiles.map(file => `/Season 01/${file.file_name}`));
      const high = state.candidates.find(row => row.quality === '4k_fps');
      assert.equal(high.resolution, '3840x2160');
      assert.equal(high.fps, 60000 / 1001);
      assert.equal(high.codec, 'hevc');
      assert.equal(high.source.detailId, detailId);
      assert.equal(high.source.key, f.source.key);
      assert.equal(high.provider, 'quark');
      assert.deepEqual(phases(f, 'provider'), ['token', 'list', 'list']);
      assert.deepEqual(f.providerRequests.filter(row => row.url.pathname.endsWith('/detail'))
        .map(row => row.url.searchParams.get('pdir_fid')), ['0', 'season-one']);
      const normalized = f.records.find(row => row.layer === 'normalization' && row.phase === 'files').files;
      assert.deepEqual(normalized.map(row => row.fileId), bridgeFiles.map(row => row.fid));
      assert.deepEqual(normalized.map(row => row.size), bridgeFiles.map(row => row.size));
      assert.ok(normalized.every(row => row.provider === 'quark' && row.shareId === bridgeShare.shareId &&
        row.accountRevision === 1));
      assert.equal(normalized[0].quality, '4K');
      assert.equal(normalized[0].fps, high.fps);
      assert.equal(f.panInputs[0].input.share.url, bridgeShare.url);
      assert.equal(f.panInputs[0].input.share.password, bridgeShare.password);
      const listInput = f.records.find(row => row.layer === 'pan-service' && row.phase === 'list');
      assert.equal(listInput.shareUrl, f.panInputs[0].input.share.url);
      assert.equal(listInput.passwordPresent, true);
      const captured = f.records.find(row => row.layer === 'cat' && row.phase === 'intercepted').resources[0];
      assert.equal(captured.shareId, normalized[0].shareId);
      assert.equal(captured.provider, normalized[0].provider);
      assert.equal(captured.passwordPresent, true);
      assert.equal(captured.installationId, high.source.installationId);
      assert.equal(captured.siteId, high.source.siteId);
      await f.command(sessionId, 'select.candidate', { candidateId: high.id });
      state = await pending(f, sessionId);
      assert.equal(state.attempt.candidate.id, high.id);
      assert.equal(state.attempt.prepareOnly, false);
      assert.deepEqual(f.panInputs.map(row => row.operation), ['list', 'prepare', 'play']);
      const prepared = f.panInputs[1].input.file, played = f.panInputs[2].input.file;
      assert.equal(prepared.kind, 'share');
      assert.equal(prepared.shareId, bridgeShare.shareId);
      assert.equal(prepared.fileId, 'episode-1-high');
      assert.equal(prepared.fileToken, 'synthetic-file-high');
      assert.equal(prepared.accountRevision, 1);
      assert.equal(played.kind, 'stored');
      assert.equal(played.fileId, 'stored-episode-1-high');
      assert.equal(played.accountRevision, 1);
      assert.equal(f.panInputs[2].input.mode, 'streaming');
      const playback = f.records.find(row => row.layer === 'normalization' && row.phase === 'play-result');
      assert.equal(playback.mode, 'streaming');
      assert.equal(playback.fileId, played.fileId);
      assert.equal(playback.accountRevision, played.accountRevision);
      assert.ok(playback.headerNames.includes('Cookie'));
      const transfer = f.providerRequests.find(row => row.url.pathname.endsWith('/save')).body;
      assert.deepEqual(transfer.fid_list, ['episode-1-high']);
      assert.deepEqual(transfer.fid_token_list, ['synthetic-file-high']);
      assert.equal(transfer.pwd_id, bridgeShare.shareId);
      assert.equal(transfer.stoken, prepared.stoken);
      assert.deepEqual(phases(f, 'provider'), ['token', 'list', 'list', 'destination', 'prepare', 'play']);
      const receipt = await f.call('film.session.attempt', { sessionId, attemptId: state.attempt.id });
      assert.equal(receipt.scope.sourceKey, f.source.key);
      assert.equal(receipt.scope.sourceEngine, 'cat');
      assert.equal(receipt.scope.accountProvider, 'quark');
      assert.equal(receipt.scope.accountRevision, 1);
      assert.equal(receipt.scope.fileProvider, 'quark');
      assert.equal(receipt.scope.fileAccountRevision, 1);
      assert.equal(new URL(receipt.url).origin + '/', receipt.mediaOrigin);
      assert.notEqual(new URL(receipt.url).origin, f.controlUrl);
      assert.equal(f.counters.mediaHead, 0, 'prepare does not read any media or control document');
      assert.equal(f.counters.mediaBody, 0);
      const head = await fetch(receipt.url, { method: 'HEAD' });
      assert.equal(head.status, 200);
      assert.equal(head.headers.get('content-length'), '1024');
      assert.equal(f.counters.mediaHead, 1);
      assert.equal(f.records.find(row => row.layer === 'media').authorized, true);
      assert.equal(f.counters.mediaBody, 0);
      let frame;
      do { frame = await observation.next(); } while (!frame.attempt);
      assert.equal(frame.attempt.id, state.attempt.id);
      assert.equal(frame.attempt.candidate.id, high.id);
      for (const value of ['synthetic-file-high', prepared.stoken, bridgeShare.password, receipt.url]) {
        assert.equal(JSON.stringify(frame).includes(value), false, 'events do not publish private player inputs');
      }
      assert.equal(f.counters.scriptNetwork, 0);
      await f.command(sessionId, 'media.started', { attemptId: state.attempt.id });
      state = await f.snapshot(sessionId);
      assert.equal(state.current.candidate.id, high.id);
      assert.equal(state.stage, 'playing');
      await f.command(sessionId, 'stop');
      assert.equal((await fetch(receipt.url, { method: 'HEAD' })).status, 404);
      assert.equal(f.counters.mediaHead, 1, 'released receipts never forward another upstream request');
      assert.equal(f.counters.mediaBody, 0);
    });
}

test('same fixture quality switch resolves the exact other file without repeating Cat detail or pan list', async t => {
  const f = await fixture(t);
  let state = await browse(f, await open(f));
  const { sessionId } = state;
  const high = state.candidates.find(row => row.quality === '4k_fps');
  await f.command(sessionId, 'select.candidate', { candidateId: high.id });
  state = await pending(f, sessionId);
  const first = state.attempt;
  await f.command(sessionId, 'media.started', { attemptId: first.id });
  await f.command(sessionId, 'quality', { quality: '1080p' });
  state = await pending(f, sessionId, first.id);
  assert.equal(state.attempt.candidate.episode, 1);
  assert.equal(state.attempt.candidate.quality, '1080p');
  assert.equal(state.attempt.candidate.codec, 'avc1');
  assert.equal(state.attempt.candidate.resolution, '1920x1080');
  assert.deepEqual(f.panInputs.filter(row => row.operation === 'prepare').map(row => row.input.file.fileId),
    ['episode-1-high', 'episode-1-low']);
  assert.deepEqual(f.panInputs.filter(row => row.operation === 'play').map(row => row.input.file.fileId),
    ['stored-episode-1-high', 'stored-episode-1-low']);
  assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
  assert.equal(f.panInputs.filter(row => row.operation === 'list').length, 1);
  assert.equal(f.counters.mediaBody, 0);
});

test('one episode across real Quark/UC owners sorts qualities, reselects after failure and resolves a quality switch',
  { timeout: 15000 }, async t => {
    const f = await fixture(t);
    let state = await open(f, { target: detailTarget(f, 'cross-provider') });
    const { sessionId } = state, observation = await events(t, f, sessionId);
    state = await browse(f, state);
    assert.equal(state.browsing.error, null);
    assert.deepEqual(state.browsing.lines, [
      { id: '夸克', label: '夸克', provider: 'quark' }, { id: 'UC', label: 'UC', provider: 'uc' },
    ]);
    assert.deepEqual(state.browsing.episodes.map(row => row.quality), ['1080p', '4k_fps'],
      'the upstream deliberately supplies the lower quality first');
    // A missing quality makes the real scheduler finish both pan lines before
    // selection. Browsing another line reloads detail and is not discovery.
    await f.command(sessionId, 'quality', { quality: '4k_hdr' });
    state = await terminal(f, sessionId);
    assert.equal(state.error, 'NO_MATCH');
    assert.equal(state.attempt, null);
    assert.equal(state.current, null);
    assert.equal(f.panInputs.some(row => row.operation !== 'list'), false);
    assert.equal(state.sources[0].source.name, 'Bridge site');
    assert.deepEqual(state.candidates.filter(row => row.provider === 'uc').map(row =>
      [row.season, row.episode, row.quality]),
      [[1, 1, '4k'], [1, 1, '720p']]);
    const candidates = state.candidates;
    assert.equal(candidates.length, 4);
    assert.equal(new Set(candidates.map(row => row.id)).size, 4);
    assert.ok(candidates.every(row => row.season === 1 && row.episode === 1 &&
      row.source.key === f.source.key && row.source.detailId === 'cross-provider'));
    assert.deepEqual(candidates.map(row => [row.provider, row.quality, row.resolution, row.fps, row.codec]), [
      ['quark', '1080p', '1920x1080', 25, 'avc1'],
      ['quark', '4k_fps', '3840x2160', 60000 / 1001, 'hevc'],
      ['uc', '4k', '3840x2160', 30, 'hevc'], ['uc', '720p', '1280x720', 25, 'avc1'],
    ]);
    const captured = f.records.find(row => row.layer === 'cat' && row.phase === 'intercepted').resources;
    assert.deepEqual(captured.map(row => [row.provider, row.shareId]),
      bridgeCrossShares.map(row => [row.provider, row.shareId]));
    await releasedInterceptions(f, 4);
    assert.equal(f.counters.scriptNetwork, 0);
    assert.equal(f.counters.interceptorForwards, 0);
    assert.deepEqual(intercepted(f, 'body').map(row => row.shareId),
      bridgeCrossShares.flatMap(row => [row.shareId, row.shareId]));
    assert.deepEqual(f.panInputs.map(row => [row.operation, row.input.provider]), [['list', 'quark'], ['list', 'uc']]);
    assert.deepEqual(f.panInputs.map(row => [row.input.share.url, row.input.share.password]),
      bridgeCrossShares.map(row => [row.url, row.password]));
    const normalized = f.records.filter(row => row.layer === 'normalization' && row.phase === 'files');
    assert.deepEqual(normalized.map(row => row.files.map(file => file.fileId)),
      [['episode-1-low', 'episode-1-high'], bridgeUCFiles.map(file => file.fid)]);
    for (const [index, share] of bridgeCrossShares.entries()) {
      const files = share.provider === 'quark' ? [bridgeFiles[1], bridgeFiles[0]] : bridgeUCFiles;
      assert.ok(normalized[index].files.every(row =>
        row.provider === share.provider && row.shareId === share.shareId && row.accountRevision === 1));
      assert.deepEqual(normalized[index].files.map(row => [row.name, row.path, row.size]),
        files.map(file => [file.file_name, `/Season 01/${file.file_name}`, file.size]));
      assert.deepEqual(f.providerRequests.filter(row => row.provider === share.provider).map(row =>
        [row.method, row.url.pathname.split('/').at(-1)]),
      [['POST', 'token'], ['GET', 'detail'], ['GET', 'detail']]);
      const upstream = f.records.find(row => row.layer === 'normalization' && row.phase === 'upstream-files' &&
        row.provider === share.provider && row.parentFileId === 'season-one');
      assert.deepEqual(upstream.files.map(row => row.video_meta), files.map(file => file.video_meta));
    }
    await f.command(sessionId, 'play');
    state = await pending(f, sessionId);
    const first = state.attempt;
    assert.equal(first.candidate.id, candidates.find(row => row.quality === '4k_fps').id,
      'film chooses the high quality even though its provider listed it second');
    const firstReceipt = await f.call('film.session.attempt', { sessionId, attemptId: first.id });
    assert.equal(firstReceipt.scope.accountProvider, 'quark');
    await f.command(sessionId, 'media.failed', { attemptId: first.id });
    state = await pending(f, sessionId, first.id);
    const replacement = state.attempt;
    assert.equal(replacement.candidate.id, candidates.find(row => row.provider === 'uc' && row.quality === '4k').id,
      'after failed 4K60, UC 4K sorts before the remaining Quark 1080P');
    assert.equal(state.current, null);
    assert.equal(replacement.candidate.episode, 1);
    assert.equal((await f.client.call('film.session.attempt', { sessionId, attemptId: first.id })).code, 'STALE_SESSION');
    const replacementReceipt = await f.call('film.session.attempt', { sessionId, attemptId: replacement.id });
    assert.equal(replacementReceipt.scope.accountProvider, 'uc');
    assert.equal(replacementReceipt.scope.fileProvider, 'uc');
    assert.notEqual(replacementReceipt.url, firstReceipt.url);
    await f.command(sessionId, 'media.started', { attemptId: replacement.id });
    await f.command(sessionId, 'quality', { quality: '720p' });
    state = await pending(f, sessionId, replacement.id);
    assert.equal(state.attempt.candidate.id, candidates.find(row => row.quality === '720p').id);
    assert.equal(state.current.candidate.id, replacement.candidate.id);
    const receipt = await f.call('film.session.attempt', { sessionId, attemptId: state.attempt.id });
    assert.equal(receipt.scope.sourceKey, f.source.key);
    assert.equal(receipt.scope.accountProvider, 'uc');
    assert.equal(receipt.scope.accountRevision, 1);
    assert.notEqual(receipt.url, replacementReceipt.url);
    assert.equal(receipt.format, undefined);
    assert.equal(receipt.drm, undefined);
    assert.equal(new URL(receipt.url).origin + '/', receipt.mediaOrigin);
    assert.deepEqual(f.panInputs.filter(row => row.operation === 'prepare').map(row => [
      row.input.provider, row.input.file.shareId, row.input.file.fileId, row.input.file.fileToken,
    ]), [
      ['quark', bridgeCrossShares[0].shareId, 'episode-1-high', 'synthetic-file-high'],
      ['uc', bridgeCrossShares[1].shareId, 'episode-1-high', 'synthetic-uc-high'],
      ['uc', bridgeCrossShares[1].shareId, 'episode-1-low', 'synthetic-uc-low'],
    ]);
    assert.deepEqual(f.panInputs.filter(row => row.operation === 'play').map(row => [
      row.input.provider, row.input.file.kind, row.input.file.fileId, row.input.mode,
    ]), [
      ['quark', 'stored', 'stored-episode-1-high', 'streaming'],
      ['uc', 'stored', 'stored-episode-1-high', 'streaming'],
      ['uc', 'stored', 'stored-episode-1-low', 'streaming'],
    ]);
    assert.equal(f.panInputs.filter(row => row.operation === 'list').length, 2);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
    assert.deepEqual(f.records.filter(row => row.layer === 'normalization' && row.phase === 'play-result')
      .map(row => [row.provider, row.fileId, row.mode, row.format, row.drm, row.headerNames.includes('Cookie')]), [
      ['quark', 'stored-episode-1-high', 'streaming', null, null, true],
      ['uc', 'stored-episode-1-high', 'streaming', null, null, true],
      ['uc', 'stored-episode-1-low', 'streaming', null, null, true],
    ]);
    for (const provider of ['quark', 'uc']) {
      const requests = f.providerRequests.filter(row => row.provider === provider);
      assert.deepEqual(requests.map(row => row.url.pathname.split('/').at(-1)), provider === 'quark'
        ? ['token', 'detail', 'detail', 'sort', 'save', 'download']
        : ['token', 'detail', 'detail', 'sort', 'save', 'download', 'sort', 'save', 'download']);
    }
    const observed = [];
    while (!observed.includes(state.attempt.id)) {
      const frame = await observation.next();
      assert.equal(frame.sessionId, sessionId);
      if (frame.attempt) observed.push(frame.attempt.id);
    }
    assert.deepEqual([...new Set(observed)], [first.id, replacement.id, state.attempt.id]);
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
    const evidence = JSON.stringify((await (await fetch(`${f.controlUrl}/evidence`)).json()).records);
    for (const privateValue of [bridgeCrossShares[0].password, bridgeCrossShares[1].password,
      'synthetic-provider-token', 'synthetic-uc-high', receipt.url])
      assert.equal(evidence.includes(privateValue), false);
    await f.command(sessionId, 'stop');
    assert.equal(f.counters.mediaBody, 0);
  });

test('cached provider directories and addresses are shared, while explicit pan invalidation rebuilds the real chain', async t => {
  const f = await fixture(t);
  let state = await browse(f, await open(f));
  const high = state.candidates.find(row => row.quality === '4k_fps');
  await f.command(state.sessionId, 'select.candidate', { candidateId: high.id });
  state = await pending(f, state.sessionId);
  const count = f.providerRequests.length;
  const second = await browse(f, await open(f));
  await f.command(second.sessionId, 'select.candidate', { candidateId: high.id });
  await pending(f, second.sessionId);
  assert.equal(f.providerRequests.length, count);
  assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
  assert.deepEqual(await f.call('film.data.invalidate', { domain: 'quark' }), { invalidated: true });
  const third = await browse(f, await open(f));
  await f.command(third.sessionId, 'select.candidate', { candidateId: high.id });
  await pending(f, third.sessionId);
  assert.equal(f.panInputs.filter(row => row.operation === 'list').length, 2);
  assert.equal(f.panInputs.filter(row => row.operation === 'prepare').length, 2);
  assert.equal(f.panInputs.filter(row => row.operation === 'play').length, 2);
  assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1, 'pan clear is not source detail clear');
  assert.equal(f.counters.mediaBody, 0);
});

for (const [platform, prepareOnly] of [['ios', true], ['windows', false]]) {
  test(`TMDB ${platform} opens the same intercepted chain with prepareOnly=${prepareOnly} and no media read`, async t => {
    const f = await fixture(t);
    let state = await open(f, { platform, target: f.metadataTarget });
    state = await pending(f, state.sessionId);
    assert.equal(state.target.metadataProvider, 'tmdb');
    assert.equal(state.target.season, 1);
    assert.equal(state.target.episode, 1);
    assert.equal(state.attempt.prepareOnly, prepareOnly);
    assert.equal(state.attempt.candidate.quality, '4k_fps');
    assert.equal(state.attempt.candidate.source.name, 'Bridge site');
    assert.equal(state.attempt.candidate.provider, 'quark');
    assert.equal(phases(f, 'cat').filter(row => row === 'search').length, 1);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
    assert.deepEqual(f.panInputs.map(row => row.operation), ['list', 'prepare', 'play']);
    assert.deepEqual(state.episodes.map(row => row.episode), [1, 2]);
    assert.equal(state.current, null);
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
  });
}

for (const [scenario, code] of [['list-http', 'UPSTREAM_HTTP'], ['list-auth', 'AUTH_REQUIRED'],
  ['list-format', 'UPSTREAM_FORMAT'], ['list-timeout', 'TIMEOUT']]) {
  test(`provider ${scenario} remains a real browse error, then retries without cached false success`, async t => {
    const f = await fixture(t, { scenario });
    let state = await browse(f, await open(f));
    assert.equal(state.browsing.error, code);
    assert.deepEqual(state.browsing.episodes, []);
    assert.equal(state.attempt, null);
    assert.deepEqual(f.panInputs.map(row => row.operation), ['list']);
    const failure = f.records.find(row => row.layer === 'pan-service' && row.phase === 'list-error');
    assert.equal(failure.code, code);
    f.setScenario('success');
    state = await browse(f, state);
    assert.equal(state.browsing.error, null);
    assert.equal(state.browsing.episodes.length, 3);
    assert.deepEqual(f.panInputs.map(row => row.operation), ['list', 'list']);
    assert.equal(f.counters.mediaBody, 0);
  });
}

test('an empty provider directory stays empty, with no fabricated episode or address', async t => {
  const f = await fixture(t, { scenario: 'list-empty' });
  const state = await browse(f, await open(f));
  assert.equal(state.browsing.error, null);
  assert.deepEqual(state.browsing.episodes, []);
  assert.deepEqual(state.candidates, []);
  assert.equal(state.attempt, null);
  assert.deepEqual(f.panInputs.map(row => row.operation), ['list']);
  assert.deepEqual(phases(f, 'provider'), ['token', 'list']);
  assert.equal(f.counters.mediaBody, 0);
});

for (const [scenario, phase, code] of [['prepare-error', 'prepare', 'UPSTREAM_ERROR'],
  ['play-error', 'play', 'UPSTREAM_ERROR'], ['play-format', 'play', 'UPSTREAM_FORMAT']]) {
  test(`provider ${scenario} never becomes an attempt, and preserves ${phase} failure provenance`, async t => {
    const f = await fixture(t, { scenario });
    let state = await browse(f, await open(f));
    await f.command(state.sessionId, 'play');
    state = await terminal(f, state.sessionId);
    assert.equal(state.attempt, null);
    assert.equal(state.current, null);
    assert.equal(state.error, code);
    const errors = f.records.filter(row => row.layer === 'pan-service' && row.phase === `${phase}-error`);
    assert.ok(errors.length > 0);
    assert.ok(errors.every(row => row.code === code));
    if (phase === 'prepare') assert.equal(f.panInputs.some(row => row.operation === 'play'), false);
    else assert.ok(f.panInputs.filter(row => row.operation === 'play').every(row => row.input.file.kind === 'stored'));
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
  });
}

test('direct Cat DASH/CENC playback metadata and custom headers cross the bridge without reading media or licenses', async t => {
  const f = await fixture(t);
  const target = { ...f.directTarget, id: 'site:descriptor',
    source: { ...f.directTarget.source, detailId: 'descriptor' } };
  const state = await open(f, { target });
  await f.command(state.sessionId, 'play');
  const selected = await pending(f, state.sessionId);
  const receipt = await f.call('film.session.attempt', { sessionId: state.sessionId, attemptId: selected.attempt.id });
  assert.equal(receipt.format, 'dash');
  assert.deepEqual(receipt.drm, { scheme: 'cenc',
    clearKeys: { '00000000000000000000000000000001': '00000000000000000000000000000002' } });
  assert.equal(receipt.scope.sourceKey, f.source.key);
  assert.equal(receipt.scope.accountProvider, undefined, 'a Cat descriptor is not a pan result');
  assert.equal(f.panInputs.length, 0);
  assert.equal(f.counters.mediaHead, 0);
  assert.equal(f.counters.mediaBody, 0);
  assert.equal((await fetch(receipt.url, { method: 'HEAD' })).status, 200);
  assert.equal(f.records.find(row => row.layer === 'media').authorized, true);
  assert.equal(f.counters.mediaBody, 0);
});

test('headerless Cat DASH/CENC descriptor keeps its direct resource through film HTTP/SSE without a local header route',
  { timeout: 15000 }, async t => {
    const f = await fixture(t);
    const descriptor = await f.invokeCat('play', { flag: '原始线路', id: 'descriptor-direct' });
    assert.equal(Object.hasOwn(descriptor, 'headers'), false);
    assert.equal(Object.hasOwn(descriptor, 'header'), false);
    let state = await open(f, { target: detailTarget(f, 'descriptor-direct') });
    const { sessionId } = state, observation = await events(t, f, sessionId);
    assert.equal((await observation.next()).sessionId, sessionId);
    await f.command(sessionId, 'play');
    state = await pending(f, sessionId);
    const receipt = await f.call('film.session.attempt', { sessionId, attemptId: state.attempt.id });
    assert.equal(receipt.url, descriptor.url);
    assert.equal(receipt.url, `${f.controlUrl}/media/descriptor-direct.mpd`);
    assert.equal(Object.hasOwn(receipt, 'mediaOrigin'), false);
    assert.equal(receipt.format, 'dash');
    assert.deepEqual(receipt.drm, descriptor.drm);
    assert.equal(receipt.scope.sourceKey, f.source.key);
    assert.equal(receipt.scope.sourceEngine, 'cat');
    assert.equal(receipt.scope.accountProvider, undefined);
    assert.equal(state.attempt.candidate.source.detailId, 'descriptor-direct');
    const normalized = f.records.filter(row => row.layer === 'normalization' && row.phase === 'cat-play-result').at(-1);
    assert.equal(normalized.playId, 'descriptor-direct');
    assert.deepEqual(normalized.headerNames, []);
    assert.equal(normalized.format, receipt.format);
    assert.deepEqual(normalized.drm, receipt.drm);
    let frame;
    do { frame = await observation.next(); } while (!frame.attempt);
    assert.equal(frame.attempt.id, receipt.attemptId);
    assert.equal(frame.attempt.candidate.id, state.attempt.candidate.id);
    assert.equal(JSON.stringify(frame).includes(receipt.url), false);
    await f.command(sessionId, 'media.started', { attemptId: state.attempt.id });
    state = await f.snapshot(sessionId);
    assert.equal(state.current.candidate.source.detailId, 'descriptor-direct');
    assert.equal(state.stage, 'playing');
    await f.command(sessionId, 'stop');
    assert.equal((await f.client.call('film.session.attempt', { sessionId, attemptId: receipt.attemptId })).code,
      'STALE_SESSION');
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.interceptorRequests, 0);
    assert.equal(f.counters.scriptNetwork, 0);
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
  });

test('real Cat HLS enhancement retains master variants and descriptor metadata without reading media segments',
  { timeout: 15000 }, async t => {
    const f = await fixture(t);
    const descriptor = await f.invokeCat('play', { flag: '原始线路', id: 'hls-master' });
    assert.equal(descriptor.url, bridgeMasterURL);
    assert.equal(descriptor.format, 'hls');
    assert.deepEqual(descriptor.headers, { 'X-Bridge-Playback': 'synthetic' });
    assert.deepEqual(descriptor._meow.media.flatMap(row => row.variants ?? []), [
      { resolution: '3840x2160', quality: '4K', fps: 59.94,
        url: 'https://manifest.bridge.invalid/show/high/index.m3u8?part=1' },
      { resolution: '1920x1080', quality: '1080P', fps: 25, url: 'https://manifest.bridge.invalid/low.m3u8' },
    ]);
    assert.equal(f.counters.manifestDocuments, 1);
    assert.deepEqual(phases(f, 'script-network'), ['manifest']);
    const evidence = await (await fetch(`${f.controlUrl}/evidence`)).json();
    const enhanced = evidence.records.find(row => row.layer === 'normalization' && row.phase === 'cat-play-result');
    assert.equal(enhanced.playId, 'hls-master');
    assert.equal(enhanced.format, 'hls');
    assert.equal(enhanced.drm, null);
    assert.deepEqual(enhanced.headerNames, ['X-Bridge-Playback']);
    assert.deepEqual(enhanced.media, descriptor._meow.media);
    const manifest = evidence.records.find(row => row.layer === 'script-network' && row.phase === 'manifest');
    assert.equal(manifest.url, descriptor.url);
    assert.equal(manifest.method, 'GET');
    assert.ok(manifest.headerNames.includes('x-bridge-playback'));
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
    // This assertion stays SDK-only; the public film association is separate.
  });

test('M3 real HLS HTTP/SSE candidates select high, switch low and reselect only the same parent after failures',
  { timeout: 15000 }, async t => {
    const f = await fixture(t), target = detailTarget(f, 'hls-master');
    let state = await browse(f, await open(f, { target }));
    const { sessionId } = state;
    assert.equal(state.browsing.episodes.length, 1);
    assert.equal(state.browsing.episodes[0].name, '正片');
    assert.equal(state.candidates.length, 1);
    assert.equal(state.candidates[0].quality, undefined);
    assert.equal(f.counters.manifestDocuments, 0, 'ordinary detail does not probe a master');
    assert.equal(f.counters.scriptNetwork, 0);
    const observation = await events(t, f, sessionId);
    assert.equal((await observation.next()).sessionId, sessionId);
    await f.command(sessionId, 'play');
    state = await pending(f, sessionId);
    assert.equal(state.candidates.length, 2);
    assert.equal(new Set(state.candidates.map(row => row.id)).size, 2);
    const high = state.candidates.find(row => row.quality === '4k_fps');
    const low = state.candidates.find(row => row.quality === '1080p');
    assert.ok(high && low);
    assert.deepEqual([high.resolution, high.fps, low.resolution, low.fps],
      ['3840x2160', 59.94, '1920x1080', 25]);
    for (const candidate of [high, low]) {
      assert.deepEqual(candidate.source, { ...f.source, detailId: 'hls-master' });
      assert.equal(candidate.lineId, '原始线路');
      assert.equal(candidate.lineLabel, '原始线路');
      assert.equal(candidate.name, '正片');
      assert.equal(candidate.season, undefined);
      assert.equal(candidate.episode, undefined);
      assert.equal(candidate.globalEpisode, undefined);
      assert.equal(candidate.provider, undefined);
    }
    const normalized = f.records.find(row => row.layer === 'normalization' && row.phase === 'cat-play-result');
    assert.equal(normalized.playId, 'hls-master');
    assert.equal(normalized.format, 'hls');
    assert.equal(normalized.drm, null);
    assert.deepEqual(normalized.headerNames, ['X-Bridge-Playback']);
    assert.deepEqual(normalized.media.flatMap(row => row.variants ?? []), [
      { resolution: '3840x2160', quality: '4K', fps: 59.94, url: highHLSURL },
      { resolution: '1920x1080', quality: '1080P', fps: 25, url: lowHLSURL },
    ]);
    assert.equal(state.attempt.candidate.id, high.id);
    assert.equal(state.current, null);
    assert.equal(state.target.source.candidateId, high.id);
    assert.equal(f.counters.mediaHead, 0, 'discovery reads only master metadata, not a media resource');
    assert.equal(f.counters.mediaBody, 0);
    const first = state.attempt, frames = await publishedAttempt(observation, state);
    const highReceipt = await hlsReceipt(t, f, state, highHLSURL);
    await f.command(sessionId, 'media.started', { attemptId: first.id });
    await f.command(sessionId, 'media.progress', { attemptId: first.id, position: 41, duration: 90, paused: true });
    await f.command(sessionId, 'quality', { quality: '1080p' });
    state = await pending(f, sessionId, first.id);
    assert.equal(state.attempt.candidate.id, low.id);
    assert.equal(state.current.candidate.id, high.id);
    assert.deepEqual([state.attempt.position, state.attempt.paused], [41, true]);
    assert.equal(state.target.source.candidateId, low.id);
    frames.push(...await publishedAttempt(observation, state));
    const switched = state.attempt, lowReceipt = await hlsReceipt(t, f, state, lowHLSURL);
    assert.deepEqual(lowReceipt.scope, highReceipt.scope);
    assert.notEqual(lowReceipt.url, highReceipt.url);
    await f.command(sessionId, 'media.started', { attemptId: switched.id });
    await f.command(sessionId, 'media.progress', { attemptId: switched.id, position: 44, duration: 90, paused: true });
    await f.command(sessionId, 'media.failed', { attemptId: switched.id });
    state = await pending(f, sessionId, switched.id);
    assert.equal(state.attempt.candidate.id, high.id, 'a failed variant does not exclude its entire file');
    assert.equal(state.current, null);
    assert.deepEqual([state.attempt.position, state.attempt.paused], [44, true]);
    const replacement = state.attempt;
    frames.push(...await publishedAttempt(observation, state));
    const replacementReceipt = await hlsReceipt(t, f, state, highHLSURL);
    assert.notEqual(replacementReceipt.url, highReceipt.url);
    assert.deepEqual(replacementReceipt.scope, highReceipt.scope);
    assert.equal((await f.client.call('film.session.attempt', { sessionId, attemptId: switched.id })).code,
      'STALE_SESSION');
    assert.equal((await fetch(lowReceipt.url, { method: 'HEAD' })).status, 404);
    assert.deepEqual([...new Set(frames.filter(frame => frame.attempt).map(frame => frame.attempt.id))],
      [first.id, switched.id, replacement.id]);
    for (const privateValue of [highReceipt.url, lowReceipt.url, replacementReceipt.url, highHLSURL, lowHLSURL])
      assert.equal(JSON.stringify(frames).includes(privateValue), false);
    await f.command(sessionId, 'media.failed', { attemptId: replacement.id });
    state = await terminal(f, sessionId);
    assert.equal(state.stage, 'unavailable');
    assert.equal(state.error, 'NO_MATCH');
    assert.equal(state.attempt, null);
    assert.equal(state.current, null);
    let last;
    do { last = await observation.next(); } while (last.stage !== 'unavailable');
    assert.equal(last.sessionId, sessionId);
    assert.equal(last.error, 'NO_MATCH');
    assert.equal(last.attempt, null);
    assert.deepEqual(f.records.filter(row => row.layer === 'cat' && row.phase === 'play')
      .map(row => [row.playId, row.flag]), [['hls-master', '原始线路']]);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
    assert.deepEqual(phases(f, 'script-network'), ['manifest']);
    assert.equal(f.counters.manifestDocuments, 1);
    assert.equal(f.counters.scriptNetwork, 1);
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.interceptorRequests, 0);
    const probes = intercepted(f, 'request');
    assert.equal(probes.length, 1);
    assert.deepEqual([probes[0].url, probes[0].method, probes[0].stage, probes[0].handled],
      [bridgeMasterURL, 'GET', 'other', false]);
    for (const phase of ['body', 'response', 'end', 'release'])
      assert.equal(intercepted(f, phase).length, 0);
    assert.equal(f.counters.interceptorForwards, 0);
    assert.equal(f.counters.mediaHead, 3);
    assert.equal(f.counters.mediaBody, 0);
    const evidence = await (await fetch(`${f.controlUrl}/evidence`)).json();
    assert.deepEqual(evidence.records.filter(row => row.layer === 'media' && row.phase === 'hls-headers')
      .map(row => row.path), ['/show/high/index.m3u8?part=1', '/low.m3u8', '/show/high/index.m3u8?part=1']);
    assert.deepEqual([...new Set(evidence.records.filter(row => row.layer === 'film-event' && row.attemptId)
      .map(row => row.attemptId))], [first.id, switched.id, replacement.id]);
  });

test('M3 first explicit HLS quality discovers the real parent before filtering and publishes only the requested low attempt',
  { timeout: 15000 }, async t => {
    const f = await fixture(t);
    let state = await open(f, { target: detailTarget(f, 'hls-master') });
    const { sessionId } = state, observation = await events(t, f, sessionId);
    assert.equal(state.attempt, null);
    assert.equal(f.counters.manifestDocuments, 0);
    await f.command(sessionId, 'quality', { quality: '1080p' });
    state = await pending(f, sessionId);
    assert.equal(state.error, null);
    assert.equal(state.candidates.length, 2);
    assert.equal(state.attempt.candidate.quality, '1080p');
    assert.equal(state.attempt.candidate.resolution, '1920x1080');
    assert.equal(state.attempt.candidate.fps, 25);
    assert.equal(state.attempt.candidate.episode, undefined);
    const frames = await publishedAttempt(observation, state);
    assert.ok(frames.filter(frame => frame.attempt).every(frame =>
      frame.attempt.candidate.id === state.attempt.candidate.id));
    assert.equal(f.counters.mediaHead, 0);
    await hlsReceipt(t, f, state, lowHLSURL);
    assert.equal(f.counters.manifestDocuments, 1);
    assert.equal(f.counters.scriptNetwork, 1);
    assert.deepEqual(f.records.filter(row => row.layer === 'cat' && row.phase === 'play')
      .map(row => [row.playId, row.flag]), [['hls-master', '原始线路']]);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.mediaBody, 0);
  });

test('M3 exact HLS reentry reuses parent cache and keeps opaque variant identity after invalidation and master reordering',
  { timeout: 15000 }, async t => {
    const f = await fixture(t), target = detailTarget(f, 'hls-master');
    let state = await open(f, { target });
    await f.command(state.sessionId, 'quality', { quality: '1080p' });
    state = await pending(f, state.sessionId);
    const original = state, low = state.attempt.candidate;
    const high = state.candidates.find(row => row.quality === '4k_fps');
    const firstReceipt = await hlsReceipt(t, f, state, lowHLSURL);
    await f.command(state.sessionId, 'media.started', { attemptId: state.attempt.id });
    const saved = await f.snapshot(state.sessionId);
    assert.equal(saved.target.source.candidateId, low.id);
    await f.call('film.session.release', { sessionId: state.sessionId });
    assert.equal((await f.client.call('film.session.snapshot', { sessionId: saved.sessionId })).code, 'NOT_FOUND');
    assert.equal((await fetch(firstReceipt.url, { method: 'HEAD' })).status, 404);
    f.setMaster('#EXTM3U\n' +
      '#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1920x1080,FRAME-RATE=25\n../low.m3u8\n' +
      '#EXT-X-STREAM-INF:BANDWIDTH=5000,RESOLUTION=3840x2160,FRAME-RATE=59.94\nhigh/index.m3u8?part=1\n');
    state = await open(f, { target, resumeTarget: saved.target });
    assert.notEqual(state.sessionId, saved.sessionId);
    assert.deepEqual(state.target, saved.target);
    assert.deepEqual(state.entryTarget, target);
    const warmEvents = await events(t, f, state.sessionId);
    await f.command(state.sessionId, 'play');
    state = await pending(f, state.sessionId);
    assert.equal(state.attempt.candidate.id, low.id, 'exact resume wins over default high-quality sorting');
    assert.deepEqual(state.target, saved.target);
    const warmFrames = await publishedAttempt(warmEvents, state);
    assert.ok(warmFrames.filter(frame => frame.attempt).every(frame => frame.attempt.candidate.id === low.id));
    const warmReceipt = await hlsReceipt(t, f, state, lowHLSURL);
    assert.notEqual(warmReceipt.url, firstReceipt.url);
    assert.deepEqual(warmReceipt.scope, firstReceipt.scope);
    assert.equal(f.counters.manifestDocuments, 1);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 1);
    assert.equal(phases(f, 'cat').filter(row => row === 'play').length, 1);
    await f.call('film.session.release', { sessionId: state.sessionId });
    assert.deepEqual(await f.call('film.data.invalidate', { domain: f.source.key }), { invalidated: true });
    state = await open(f, { target, resumeTarget: saved.target });
    const coldEvents = await events(t, f, state.sessionId);
    await f.command(state.sessionId, 'play');
    state = await pending(f, state.sessionId);
    assert.equal(state.attempt.candidate.id, low.id);
    assert.deepEqual(state.target, saved.target);
    assert.deepEqual(state.candidates.map(row => row.id).sort(), original.candidates.map(row => row.id).sort());
    assert.deepEqual(f.records.filter(row => row.layer === 'normalization' && row.phase === 'cat-play-result')
      .at(-1).media.flatMap(row => row.variants ?? []).map(row => row.url), [lowHLSURL, highHLSURL]);
    const coldFrames = await publishedAttempt(coldEvents, state);
    assert.ok(coldFrames.filter(frame => frame.attempt).every(frame => frame.attempt.candidate.id === low.id));
    await hlsReceipt(t, f, state, lowHLSURL);
    const previous = state.attempt.id;
    await f.command(state.sessionId, 'select.candidate', { candidateId: high.id });
    state = await pending(f, state.sessionId, previous);
    assert.equal(state.attempt.candidate.id, high.id);
    assert.equal(state.target.source.candidateId, high.id);
    await publishedAttempt(coldEvents, state);
    await hlsReceipt(t, f, state, highHLSURL);
    assert.equal(f.counters.manifestDocuments, 2);
    assert.equal(f.counters.scriptNetwork, 2);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 2);
    assert.deepEqual(f.records.filter(row => row.layer === 'cat' && row.phase === 'play')
      .map(row => [row.playId, row.flag]), [['hls-master', '原始线路'], ['hls-master', '原始线路']]);
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.mediaHead, 4);
    assert.equal(f.counters.mediaBody, 0);
  });

test('M3 missing exact HLS variant fails without a master or sibling attempt and recovers only its saved target',
  { timeout: 15000 }, async t => {
    const f = await fixture(t), target = detailTarget(f, 'hls-master');
    let state = await open(f, { target });
    await f.command(state.sessionId, 'quality', { quality: '1080p' });
    state = await pending(f, state.sessionId);
    const saved = state, lowId = state.attempt.candidate.id;
    await hlsReceipt(t, f, state, lowHLSURL);
    await f.call('film.session.release', { sessionId: state.sessionId });
    f.setMaster('#EXTM3U\n' +
      '#EXT-X-STREAM-INF:BANDWIDTH=5000,RESOLUTION=3840x2160,FRAME-RATE=59.94\nhigh/index.m3u8?part=1\n');
    await f.call('film.data.invalidate', { domain: f.source.key });
    state = await open(f, { target, resumeTarget: saved.target });
    const observation = await events(t, f, state.sessionId);
    await f.command(state.sessionId, 'play');
    state = await terminal(f, state.sessionId);
    assert.equal(state.stage, 'unavailable');
    assert.equal(state.error, 'NO_MATCH');
    assert.equal(state.attempt, null);
    assert.equal(state.current, null);
    assert.deepEqual(state.target, saved.target);
    assert.deepEqual(state.candidates.map(row => row.quality), ['4k_fps']);
    const frames = [];
    do { frames.push(await observation.next()); } while (frames.at(-1).stage !== 'unavailable');
    assert.ok(frames.every(frame => frame.sessionId === state.sessionId && frame.attempt === null &&
      frame.current === null));
    assert.equal(frames.at(-1).error, 'NO_MATCH');
    assert.equal(f.counters.manifestDocuments, 2);
    assert.equal(f.counters.mediaHead, 1, 'a missing variant never exposes a fallback resource');
    assert.equal(f.counters.mediaBody, 0);
    await f.call('film.session.release', { sessionId: state.sessionId });
    f.setMaster(bridgeMaster);
    await f.call('film.data.invalidate', { domain: f.source.key });
    state = await open(f, { target, resumeTarget: saved.target });
    await f.command(state.sessionId, 'play');
    state = await pending(f, state.sessionId);
    assert.equal(state.error, null);
    assert.equal(state.attempt.candidate.id, lowId);
    assert.deepEqual(state.target, saved.target);
    await hlsReceipt(t, f, state, lowHLSURL);
    assert.equal(f.counters.manifestDocuments, 3);
    assert.equal(f.counters.scriptNetwork, 3);
    assert.equal(phases(f, 'cat').filter(row => row === 'detail').length, 3);
    assert.deepEqual(f.records.filter(row => row.layer === 'cat' && row.phase === 'play')
      .map(row => [row.playId, row.flag]), Array(3).fill(['hls-master', '原始线路']));
    assert.equal(f.panInputs.length, 0);
    assert.equal(f.counters.mediaHead, 2);
    assert.equal(f.counters.mediaBody, 0);
  });

for (const detailId of ['headers-invalid', 'header-value-invalid', 'format-invalid']) {
  test(`malformed Cat descriptor ${detailId} fails before any upstream media request`, async t => {
    const f = await fixture(t);
    const state = await open(f, { target: { ...f.directTarget, id: `site:${detailId}`,
      source: { ...f.directTarget.source, detailId } } });
    await f.command(state.sessionId, 'play');
    const failed = await terminal(f, state.sessionId);
    assert.equal(failed.attempt, null);
    assert.equal(failed.error, 'MEDIA_INVALID_DESCRIPTOR');
    assert.equal(f.counters.mediaHead, 0);
    assert.equal(f.counters.mediaBody, 0);
  });
}

test('leaving an in-flight provider directory drains into shared cache without publishing into the released page', async t => {
  const f = await fixture(t), gate = f.gate('list');
  const first = await open(f);
  await f.command(first.sessionId, 'browse', { sourceId: first.sources[0].id });
  await gate.entered.promise;
  assert.equal(gate.signal.aborted, false);
  assert.deepEqual(await f.call('film.session.release', { sessionId: first.sessionId }), { released: true });
  assert.equal(gate.signal.aborted, false, 'page release stops scheduling; it does not abort finite provider I/O');
  gate.release.resolve();
  await waitForBridge(() => f.records.some(row => row.layer === 'normalization' && row.phase === 'files'),
    'released producer completion');
  assert.equal((await f.client.call('film.session.snapshot', { sessionId: first.sessionId })).code, 'NOT_FOUND');
  const second = await browse(f, await open(f));
  assert.equal(second.browsing.episodes.length, 3);
  assert.equal(f.panInputs.filter(row => row.operation === 'list').length, 1);
  assert.equal(f.panInputs.some(row => row.operation === 'prepare'), false);
  assert.equal(f.panInputs.some(row => row.operation === 'play'), false);
  assert.equal(f.counters.mediaBody, 0);
});

test('switching away while list is in flight does not restore the old browse selection', async t => {
  const f = await fixture(t), gate = f.gate('list');
  const initial = await open(f);
  await f.command(initial.sessionId, 'browse', { sourceId: initial.sources[0].id });
  await gate.entered.promise;
  await f.command(initial.sessionId, 'browse', { sourceId: null });
  gate.release.resolve();
  await waitForBridge(() => f.records.some(row => row.layer === 'normalization' && row.phase === 'files'));
  const state = await f.snapshot(initial.sessionId);
  assert.equal(state.browsing.sourceId, null);
  assert.deepEqual(state.browsing.lines, []);
  assert.deepEqual(state.browsing.episodes, []);
  assert.equal(state.attempt, null);
  assert.equal(gate.signal.aborted, false);
});

test('cancelling a direct pan HTTP observer aborts that request but waits for the controlled provider to finish', async t => {
  const f = await fixture(t), gate = f.gate('list'), controller = new AbortController();
  const request = f.client.call('pan.list', { provider: 'quark',
    share: { url: bridgeShare.url, password: bridgeShare.password } }, { signal: controller.signal });
  const cancelled = assert.rejects(request, { code: 'CANCELLED' });
  await gate.entered.promise;
  controller.abort();
  await cancelled;
  await waitForBridge(() => gate.signal.aborted, 'provider cancellation signal');
  assert.equal(f.records.some(row => row.layer === 'normalization' && row.phase === 'files'), false);
  gate.release.resolve();
  await waitForBridge(() => f.records.some(row => row.layer === 'pan-service' && row.phase === 'list-error'));
  assert.equal(f.records.find(row => row.layer === 'pan-service' && row.phase === 'list-error').code, 'CANCELLED');
  assert.equal(f.panInputs.some(row => row.operation !== 'list'), false);
  assert.equal(f.counters.mediaBody, 0);
});

test('runtime close waits for physical provider completion, closes exactly once and cannot start prepare after cancellation',
  { timeout: 15000 }, async t => {
    const f = await fixture(t), gate = f.gate('list');
    const state = await open(f);
    await f.command(state.sessionId, 'browse', { sourceId: state.sources[0].id });
    await gate.entered.promise;
    let completed = false;
    const close = f.app.close().then(() => { completed = true; });
    await waitForBridge(() => gate.signal.aborted, 'runtime cancellation');
    await tick();
    assert.equal(completed, false, 'logical cancellation cannot pretend to be physical completion');
    gate.release.resolve();
    await close;
    assert.equal(completed, true);
    assert.equal(f.counters.panClose, 1);
    assert.equal(f.panInputs.some(row => row.operation === 'prepare'), false);
    assert.equal(f.panInputs.some(row => row.operation === 'play'), false);
    assert.equal(f.counters.mediaBody, 0);
  });

test('HLS master metadata retains relative variant URLs, resolution and frame rate without media I/O', () => {
  const variants = parseM3U8Metadata('#EXTM3U\n' +
    '#EXT-X-STREAM-INF:BANDWIDTH=5000,RESOLUTION=3840x2160,FRAME-RATE=59.94\nhigh/index.m3u8?part=1\n' +
    '#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1920x1080,FRAME-RATE=25\n../low.m3u8\n' +
    '#EXT-X-STREAM-INF:BANDWIDTH=200\nunknown.m3u8\n' +
    '#EXT-X-STREAM-INF:BANDWIDTH=200\n', 'https://manifest.invalid/show/master.m3u8');
  assert.deepEqual(variants, [
    { resolution: '3840x2160', quality: '4K', fps: 59.94, url: 'https://manifest.invalid/show/high/index.m3u8?part=1' },
    { resolution: '1920x1080', quality: '1080P', fps: 25, url: 'https://manifest.invalid/low.m3u8' },
    { url: 'https://manifest.invalid/show/unknown.m3u8' },
  ]);
  assert.deepEqual(parseM3U8Metadata('#EXTM3U\n' +
    '#EXT-X-STREAM-INF:RESOLUTION=0x2160,FRAME-RATE=0\nunknown.m3u8\n' +
    '#EXT-X-STREAM-INF:RESOLUTION=bad,FRAME-RATE=bad\nlow.m3u8\n',
  'https://manifest.invalid/root.m3u8'), [
    { url: 'https://manifest.invalid/unknown.m3u8' }, { url: 'https://manifest.invalid/low.m3u8' },
  ]);
  assert.throws(() => parseM3U8Metadata('#EXTM3U\n#EXT-X-STREAM-INF:RESOLUTION=1920x1080\nhttp://[\n',
    'https://manifest.invalid/root.m3u8'), { code: 'ERR_INVALID_URL' });
});
