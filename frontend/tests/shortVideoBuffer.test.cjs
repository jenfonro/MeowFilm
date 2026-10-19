const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(join(__dirname, '../src/shared/shortVideoBuffer.js'), 'utf8');
const BUFFER_BYTES = 4 * 1024 * 1024;
const flush = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const candidate = (name, headers) => ({ url: `https://media.test/${name}`, headers });

class Events {
  listeners = new Map();
  addEventListener(name, callback) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(callback);
  }
  removeEventListener(name, callback) {
    this.listeners.get(name)?.delete(callback);
  }
  dispatch(name, data = {}) {
    for (const callback of [...(this.listeners.get(name) || [])]) callback(data);
  }
  get listenerCount() {
    return [...this.listeners.values()].reduce((sum, listeners) => sum + listeners.size, 0);
  }
}

class Video extends Events {
  attributes = new Map();
  srcWrites = [];
  loadCalls = 0;
  playCalls = 0;
  readyState = 0;
  currentTime = 0;
  duration = 60;
  ranges = [];
  autoplay = true;
  controls = true;
  muted = false;
  get defaultMuted() { return this.attributes.has('muted'); }
  set defaultMuted(value) {
    if (value) this.attributes.set('muted', '');
    else this.attributes.delete('muted');
  }
  get playsInline() { return this.attributes.has('playsinline'); }
  set playsInline(value) {
    if (value) this.attributes.set('playsinline', '');
    else this.attributes.delete('playsinline');
  }
  get buffered() {
    return {
      length: this.ranges.length,
      start: index => this.ranges[index][0],
      end: index => this.ranges[index][1],
    };
  }
  get src() { return this.attributes.get('src') || ''; }
  set src(value) { this.srcWrites.push(value); this.attributes.set('src', value); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  removeAttribute(name) { this.attributes.delete(name); }
  load() {
    this.loadCalls++;
    if (!this.src) {
      this.readyState = 0;
      this.ranges = [];
    }
  }
  play() { this.playCalls++; assert.fail('prebuffering must never play media'); }
  remove() {
    if (!this.parentNode) return;
    const parent = this.parentNode;
    parent.children.splice(parent.children.indexOf(this), 1);
    this.parentNode = null;
  }
}

class Xhr extends Events {
  readyState = 0;
  withCredentials = true;
  headers = {};
  open(method, url, async) {
    assert.equal(method, 'GET');
    assert.equal(async, true);
    this.url = url;
    this.readyState = 1;
  }
  setRequestHeader(name, value) {
    assert.equal(this.readyState, 1, 'headers require an opened XHR');
    this.headers[name] = value;
  }
  progress(loaded) { this.dispatch('progress', { loaded }); }
}

function fixture({ preferNativeHls = false, nativeHls = '', supported = true, loadHls } = {}) {
  const videos = [], engines = [], timers = new Map();
  let now = 0, timerId = 0, loadHlsCalls = 0;
  const host = {
    children: [], peak: 0,
    ownerDocument: {
      createElement(tag) {
        assert.equal(tag, 'video');
        const video = new Video();
        video.canPlayType = type => {
          assert.equal(type, 'application/vnd.apple.mpegurl');
          return nativeHls;
        };
        videos.push(video);
        return video;
      },
    },
    appendChild(video) {
      video.remove();
      video.parentNode = this;
      this.children.push(video);
      this.peak = Math.max(this.peak, this.children.length);
    },
  };
  class Hls extends Events {
    static Events = Object.fromEntries([
      'ERROR', 'MANIFEST_LOADED', 'LEVEL_LOADED', 'FRAG_LOADED', 'FRAG_BUFFERED',
    ].map(name => [name, name]));
    static isSupported() { return supported; }
    stopCalls = 0;
    destroyCalls = 0;
    starts = [];
    constructor(config) {
      super();
      if (Hls.failure === 'constructor') throw new Error('constructor failed');
      this.config = config;
      engines.push(this);
    }
    on(name, callback) { this.addEventListener(name, callback); }
    off(name, callback) { this.removeEventListener(name, callback); }
    emit(name, data = {}) {
      for (const callback of [...(this.listeners.get(name) || [])]) callback(name, data);
    }
    loadSource(url) {
      this.url = url;
      if (Hls.failure === 'loadSource') this.emit('ERROR', { fatal: true });
    }
    attachMedia(video) {
      if (Hls.failure === 'attachMedia') throw new Error('attach failed');
      this.video = video;
      video.src = 'blob:mock-media-source';
    }
    stopLoad() { this.stopCalls++; }
    startLoad(position) { this.starts.push(position); }
    destroy() {
      this.destroyCalls++;
      this.listeners.clear();
    }
  }
  const context = vm.createContext({
    setTimeout(callback, delay) {
      const id = ++timerId;
      timers.set(id, { callback, at: now + delay });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    console: {
      error() { assert.fail('prefetch errors must be silent'); },
      warn() { assert.fail('prefetch errors must be silent'); },
    },
  });
  vm.runInContext(source.replace('export const createShortVideoBuffer', 'const createShortVideoBuffer')
    + '\nthis.createShortVideoBuffer = createShortVideoBuffer;', context);
  const pool = context.createShortVideoBuffer({
    loadHls() {
      loadHlsCalls++;
      return loadHls ? loadHls() : Promise.resolve(Hls);
    },
    detectFormat(url) {
      const path = url.split(/[?#]/)[0];
      if (path.endsWith('.m3u8')) return 'hls';
      if (path.endsWith('.mpd')) return 'dash';
      if (path.endsWith('.flv')) return 'flv';
      return 'native';
    },
    preferNativeHls, host,
  });
  return {
    pool, videos, engines, Hls, host, timers,
    get loadHlsCalls() { return loadHlsCalls; },
    advance(ms) {
      now += ms;
      for (const [id, timer] of [...timers]) {
        if (timer.at > now) continue;
        timers.delete(id);
        timer.callback();
      }
    },
  };
}

test('native prefetch mounts a tiny muted inline video and only requests metadata', () => {
  const { pool, videos, host } = fixture();
  const { url } = candidate('a.mp4?token=A#exact');
  pool.sync([{ url }]);
  const video = videos[0];
  assert.equal(host.children[0], video);
  assert.equal(video.width, 2);
  assert.equal(video.height, 2);
  assert.equal(video.autoplay, false);
  assert.equal(video.muted, true);
  assert.equal(video.defaultMuted, true);
  assert.equal(video.playsInline, true);
  assert.equal(video.controls, false);
  assert.equal(video.preload, 'metadata');
  assert.equal(video.attributes.get('referrerpolicy'), 'no-referrer');
  assert.equal(video.attributes.has('crossorigin'), false);
  assert.equal(Object.hasOwn(video, 'crossOrigin'), false);
  assert.equal(video.src, url);
  assert.equal(video.loadCalls, 1);
  video.ranges = [[0, 120]];
  video.dispatch('progress');
  assert.equal(video.preload, 'metadata', 'native buffering remains browser-controlled');
  assert.equal(video.playCalls, 0);
  pool.clear();
  assert.equal(video.src, '');
  assert.equal(video.loadCalls, 2);
  assert.equal(host.children.length, 0);
  assert.equal(video.listenerCount, 0);
});

test('sync deduplicates, caps the pool at two, and evicts before allocating replacements', () => {
  const { pool, videos, host, timers } = fixture();
  const a = candidate('a.mp4'), b = candidate('b.mp4'), c = candidate('c.mp4');
  pool.sync([a, a, b, c]);
  assert.equal(videos.length, 2);
  assert.equal(pool.take(c.url), null);
  pool.sync([b, a]);
  assert.equal(videos.length, 2, 'reordering does not reload retained candidates');
  pool.sync([b, c]);
  assert.equal(videos.length, 3);
  assert.equal(videos[0].src, '');
  assert.equal(videos[1].loadCalls, 1);
  assert.equal(host.children.length, 2);
  assert.equal(host.peak, 2);
  pool.sync([]);
  pool.clear();
  assert.equal(host.children.length, 0);
  assert.equal(timers.size, 0);
  assert.ok(videos.every(video => video.loadCalls === 2 && video.playCalls === 0));
});

test('exact URLs and sorted header key/value pairs isolate entries and snapshot request headers', async () => {
  const f = fixture();
  const a = candidate('a.m3u8?token=A', { Authorization: 'A', Z: 3 });
  const b = candidate('a.m3u8?token=A', { Authorization: 'B', Z: 3 });
  f.pool.sync([a, b]);
  a.headers.Authorization = 'mutated';
  await flush();
  assert.equal(f.engines.length, 2);
  const xhr = new Xhr();
  f.engines[0].config.xhrSetup(xhr, a.url);
  assert.deepEqual(xhr.headers, { Authorization: 'A', Z: '3' });
  assert.equal(xhr.withCredentials, false);
  f.pool.sync([
    { url: a.url, headers: { Z: 3, Authorization: 'A' } },
    { url: b.url, headers: { Z: 3, Authorization: 'B' } },
  ]);
  assert.equal(f.loadHlsCalls, 2);
  assert.equal(f.pool.take(a.url.toLowerCase(), { Z: 3, Authorization: 'A' }), null);
  assert.equal(f.pool.take(a.url, { Z: 3, authorization: 'A' }), null);
  assert.equal(f.pool.take(a.url, { Z: 3, Authorization: 'C' }), null);
  assert.equal(f.engines[0].destroyCalls, 0);
  f.videos[0].readyState = 2;
  const adopted = f.pool.take(a.url, { Z: 3, Authorization: 'A' });
  assert.equal(adopted.customPlayer.hls, f.engines[0]);
  assert.equal(f.pool.take(a.url, { Authorization: 'A', Z: 3 }), null);
  assert.equal(xhr.listenerCount, 0);
  f.pool.clear();
  assert.equal(f.engines[0].destroyCalls, 0);
  assert.equal(f.engines[1].destroyCalls, 1);
  adopted.destroy();
});

test('native custom headers and FLV/DASH are skipped without media requests', () => {
  for (const entry of [
    candidate('a.mp4', { Authorization: 'secret' }),
    candidate('a.mp4', { 'X-Empty': '' }),
    candidate('a.flv'),
    candidate('a.mpd'),
  ]) {
    const f = fixture();
    f.pool.sync([entry]);
    assert.equal(f.videos.length, 0);
    assert.equal(f.loadHlsCalls, 0);
    assert.equal(f.pool.take(entry.url, entry.headers), null);
    assert.equal(f.timers.size, 0);
  }
});

test('native HLS requires preference, capability, and no custom headers', async t => {
  for (const [preferNativeHls, nativeHls, headers, native] of [
    [true, 'maybe', {}, true],
    [true, 'probably', { ignored: null }, true],
    [true, '', {}, false],
    [false, 'probably', {}, false],
    [true, 'probably', { Authorization: 'secret' }, false],
  ]) {
    await t.test(JSON.stringify([preferNativeHls, nativeHls, headers]), async () => {
      const f = fixture({ preferNativeHls, nativeHls });
      const entry = candidate('a.m3u8', headers);
      f.pool.sync([entry]);
      await flush();
      assert.equal(f.engines.length, native ? 0 : 1);
      assert.equal(f.videos[0].src, native ? entry.url : 'blob:mock-media-source');
      if (native) assert.equal(f.videos[0].attributes.get('referrerpolicy'), 'no-referrer');
      assert.equal(f.videos[0].attributes.has('crossorigin'), false);
      assert.equal(Object.hasOwn(f.videos[0], 'crossOrigin'), false);
      assert.equal(f.videos[0].playCalls, 0);
      f.pool.clear();
    });
  }
});

test('unsupported HLS never falls back to an unauthenticated native request', async () => {
  const f = fixture({ preferNativeHls: true, nativeHls: 'probably', supported: false });
  const entry = candidate('a.m3u8', { Authorization: 'secret' });
  f.pool.sync([entry]);
  await flush();
  assert.equal(f.engines.length, 0);
  assert.deepEqual(f.videos[0].srcWrites, []);
  assert.equal(f.host.children.length, 0);
  assert.equal(f.pool.take(entry.url, entry.headers), null);
});

test('take is synchronous and requires a first frame or positive buffered duration', () => {
  for (const [readyState, ranges, loadeddata, usable] of [
    [0, [], false, false],
    [1, [], false, false],
    [1, [[0, 0]], false, false],
    [2, [], false, true],
    [1, [[0.1, 0.2]], false, true],
    [1, [], true, true],
  ]) {
    const f = fixture();
    const entry = candidate('a.mp4');
    f.pool.sync([entry]);
    const video = f.videos[0];
    video.readyState = readyState;
    video.ranges = ranges;
    if (loadeddata) video.dispatch('loadeddata');
    const adopted = f.pool.take(entry.url);
    assert.equal(!!adopted, usable);
    assert.equal(f.timers.size, 0);
    assert.equal(video.listenerCount, 0);
    if (usable) {
      assert.equal(adopted.video, video);
      assert.equal(adopted.then, undefined);
      assert.equal(adopted.customPlayer, null);
      assert.equal(video.loadCalls, 1);
      adopted.destroy();
    } else {
      assert.equal(adopted, null);
      assert.equal(video.src, '');
      assert.equal(f.host.children.length, 0);
    }
  }
});

test('native adoption preserves media, resume only changes preload, and clear cannot destroy it', () => {
  const f = fixture();
  const entry = candidate('a.mp4');
  f.pool.sync([entry]);
  const video = f.videos[0];
  video.ranges = [[0, 5]];
  video.currentTime = 1.25;
  const adopted = f.pool.take(entry.url);
  assert.equal(video.src, entry.url);
  assert.equal(video.loadCalls, 1);
  assert.equal(video.currentTime, 1.25);
  assert.deepEqual(video.ranges, [[0, 5]]);
  video.dispatch('error');
  f.pool.sync([candidate('b.mp4')]);
  f.pool.clear();
  f.advance(8000);
  assert.equal(video.src, entry.url);
  adopted.resume();
  assert.equal(video.preload, 'auto');
  assert.equal(video.muted, true);
  assert.equal(video.defaultMuted, true);
  assert.equal(video.playCalls, 0);
  assert.deepEqual(video.srcWrites, [entry.url]);
  assert.equal(video.loadCalls, 1);
  adopted.destroy();
  adopted.destroy();
  adopted.resume();
  assert.equal(video.src, '');
  assert.equal(video.loadCalls, 2);
  assert.equal(video.parentNode, null);
});

test('destroying an adopted same-key entry cannot evict its new pooled replacement', () => {
  const f = fixture();
  const entry = candidate('a.mp4');
  f.pool.sync([entry]);
  f.videos[0].readyState = 2;
  const old = f.pool.take(entry.url);
  f.pool.sync([entry]);
  old.destroy();
  f.videos[1].readyState = 2;
  const replacement = f.pool.take(entry.url);
  assert.equal(replacement.video, f.videos[1]);
  replacement.destroy();
});

test('HLS uses bounded config and stops at three buffered seconds, not a range end timestamp', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8');
  f.pool.sync([entry]);
  await flush();
  const hls = f.engines[0], video = f.videos[0];
  for (const [key, value] of Object.entries({
    enableWorker: true, startPosition: 0, maxBufferLength: 3,
    maxMaxBufferLength: 3, maxBufferSize: BUFFER_BYTES, backBufferLength: 0,
  })) assert.equal(hls.config[key], value, key);
  assert.equal(hls.url, entry.url);
  assert.equal(hls.video, video);
  video.ranges = [[10, 12.999]];
  hls.emit('FRAG_BUFFERED');
  assert.equal(hls.stopCalls, 0);
  video.ranges = [[10, 13]];
  hls.emit('FRAG_BUFFERED');
  assert.equal(hls.stopCalls, 1);
  hls.emit('FRAG_BUFFERED');
  f.advance(8000);
  assert.equal(hls.stopCalls, 1);
  assert.equal(hls.destroyCalls, 0);
  const adopted = f.pool.take(entry.url);
  assert.equal(adopted.video, video);
  adopted.destroy();
});

test('a long first HLS fragment is retained but stops subsequent loading', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8');
  f.pool.sync([entry]);
  await flush();
  f.videos[0].ranges = [[0, 6]];
  f.engines[0].emit('FRAG_BUFFERED');
  assert.equal(f.engines[0].stopCalls, 1);
  const adopted = f.pool.take(entry.url);
  assert.deepEqual(adopted.video.ranges, [[0, 6]]);
  adopted.destroy();
});

test('XHR byte budget is cumulative, deduplicates progress/load, and stops mid-fragment at 4MiB', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8', { Authorization: 'secret' });
  f.pool.sync([entry]);
  await flush();
  const hls = f.engines[0];
  const first = new Xhr(), second = new Xhr();
  hls.config.xhrSetup(first, 'https://media.test/first.ts');
  hls.config.xhrSetup(second, 'https://media.test/second.ts');
  assert.equal(first.withCredentials, false);
  assert.equal(second.headers.Authorization, 'secret');
  first.progress(BUFFER_BYTES / 4);
  first.progress(BUFFER_BYTES / 2);
  first.dispatch('load', { loaded: BUFFER_BYTES / 2 });
  hls.emit('FRAG_LOADED', { frag: { stats: { loaded: BUFFER_BYTES / 2 } } });
  assert.equal(hls.stopCalls, 0, 'FRAG_LOADED must not double-count XHR bytes');
  second.progress(BUFFER_BYTES / 2 - 1);
  assert.equal(hls.stopCalls, 0);
  second.progress(BUFFER_BYTES / 2);
  assert.equal(hls.stopCalls, 1);
  second.progress(BUFFER_BYTES);
  assert.equal(hls.stopCalls, 1);
  f.advance(8000);
  assert.equal(hls.destroyCalls, 1, 'a stopped entry without usable buffer must fall back');
  assert.equal(f.pool.take(entry.url, entry.headers), null);
  assert.equal(first.listenerCount + second.listenerCount, 0);
});

test('fragment loaded byte stats also enforce the budget and retain existing media', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8');
  f.pool.sync([entry]);
  await flush();
  const hls = f.engines[0];
  f.videos[0].ranges = [[0, 1]];
  hls.emit('FRAG_LOADED', { frag: { stats: { loaded: BUFFER_BYTES / 2 } } });
  hls.emit('FRAG_LOADED', { part: { stats: { loaded: BUFFER_BYTES / 2 - 1 } } });
  assert.equal(hls.stopCalls, 0);
  hls.emit('FRAG_LOADED', { stats: { loaded: 1 } });
  assert.equal(hls.stopCalls, 1);
  f.advance(8000);
  assert.equal(hls.destroyCalls, 0);
  f.pool.take(entry.url).destroy();
});

test('eight-second timeout stops HLS, retaining usable media and destroying empty entries', async t => {
  for (const usable of [false, true]) {
    await t.test(`usable=${usable}`, async () => {
      const f = fixture();
      const entry = candidate('a.m3u8');
      f.pool.sync([entry]);
      await flush();
      const hls = f.engines[0];
      if (usable) f.videos[0].readyState = 2;
      f.advance(7999);
      assert.equal(hls.stopCalls, 0);
      f.pool.sync([entry]);
      f.advance(1);
      assert.equal(hls.stopCalls, 1, 'sync must not extend the original deadline');
      assert.equal(hls.destroyCalls, usable ? 0 : 1);
      const adopted = f.pool.take(entry.url);
      assert.equal(!!adopted, usable);
      if (adopted) adopted.destroy();
      assert.equal(f.timers.size, 0);
    });
  }
});

test('native timeout discards metadata-only media but preserves browser-provided buffer', () => {
  for (const usable of [false, true]) {
    const f = fixture();
    const entry = candidate('a.mp4');
    f.pool.sync([entry]);
    f.videos[0].readyState = 1;
    if (usable) f.videos[0].ranges = [[0, 1]];
    f.advance(8000);
    assert.equal(f.videos[0].src, usable ? entry.url : '');
    const adopted = f.pool.take(entry.url);
    assert.equal(!!adopted, usable);
    if (adopted) adopted.destroy();
  }
});

test('live HLS manifests/levels are destroyed; VOD remains eligible', async () => {
  for (const [event, data] of [
    ['LEVEL_LOADED', { details: { live: true } }],
    ['MANIFEST_LOADED', { levels: [{ details: { live: true } }] }],
  ]) {
    const f = fixture();
    const entry = candidate('a.m3u8');
    f.pool.sync([entry]);
    await flush();
    const hls = f.engines[0];
    hls.emit('LEVEL_LOADED', { details: { live: false } });
    assert.equal(hls.destroyCalls, 0);
    f.videos[0].readyState = 2;
    hls.emit(event, data);
    assert.equal(hls.destroyCalls, 1);
    assert.equal(hls.listenerCount, 0);
    assert.equal(f.host.children.length, 0);
    assert.equal(f.pool.take(entry.url), null);
  }
});

test('native HLS cleans up when metadata or duration changes reveal a live stream', () => {
  for (const event of ['loadedmetadata', 'durationchange']) {
    const f = fixture({ preferNativeHls: true, nativeHls: 'maybe' });
    const entry = candidate('a.m3u8');
    f.pool.sync([entry]);
    const video = f.videos[0];
    video.dispatch('loadedmetadata');
    assert.equal(video.src, entry.url);
    video.duration = Infinity;
    video.dispatch(event);
    assert.equal(video.src, '');
    assert.equal(f.pool.take(entry.url), null);
  }
});

test('media and HLS errors silently destroy only the affected pooled entry', async () => {
  for (const error of ['media', 'fatal', 'nonfatal']) {
    const f = fixture();
    const a = candidate('a.m3u8'), b = candidate('b.m3u8');
    f.pool.sync([a, b]);
    await flush();
    if (error === 'media') f.videos[0].dispatch('error');
    else f.engines[0].emit('ERROR', { fatal: error === 'fatal', details: 'network failure' });
    assert.equal(f.engines[0].destroyCalls, 1);
    assert.equal(f.engines[1].destroyCalls, 0);
    assert.equal(f.pool.take(a.url), null);
    assert.equal(f.host.children.length, 1);
    f.pool.clear();
  }
});

test('loadHls rejection and synchronous engine failures are silent and fully cleaned up', async () => {
  for (const failure of ['loadHls', 'constructor', 'loadSource', 'attachMedia']) {
    const f = fixture({
      loadHls: failure === 'loadHls' ? () => Promise.reject(new Error('chunk failed')) : undefined,
    });
    f.Hls.failure = failure;
    const entry = candidate('a.m3u8');
    f.pool.sync([entry]);
    await flush();
    assert.equal(f.pool.take(entry.url), null);
    assert.equal(f.host.children.length, 0);
    assert.equal(f.timers.size, 0);
    assert.ok(f.engines.every(hls => hls.destroyCalls === 1));
    if (failure === 'loadSource') assert.equal(f.engines[0].video, undefined);
  }
});

test('late loadHls cannot create an engine after clear, eviction, timeout, or unready take', async () => {
  for (const cancel of ['clear', 'sync', 'timeout', 'take']) {
    const pending = deferred();
    const f = fixture({ loadHls: () => pending.promise });
    const entry = candidate('a.m3u8');
    f.pool.sync([entry]);
    if (cancel === 'clear') f.pool.clear();
    if (cancel === 'sync') f.pool.sync([]);
    if (cancel === 'timeout') f.advance(8000);
    if (cancel === 'take') assert.equal(f.pool.take(entry.url), null);
    pending.resolve(f.Hls);
    await flush();
    assert.equal(f.engines.length, 0);
    assert.equal(f.host.children.length, 0);
    assert.equal(f.timers.size, 0);
  }
});

test('same-key replacements use strict identity for late resolution and late rejection', async () => {
  for (const staleResult of ['resolve-first', 'resolve-last', 'reject']) {
    const old = deferred(), next = deferred();
    let calls = 0;
    const f = fixture({ loadHls: () => (++calls === 1 ? old.promise : next.promise) });
    const entry = candidate('a.m3u8');
    f.pool.sync([entry]);
    f.pool.clear();
    f.pool.sync([entry]);
    if (staleResult === 'resolve-first') {
      old.resolve(f.Hls);
      await flush();
      assert.equal(f.engines.length, 0);
    }
    next.resolve(f.Hls);
    await flush();
    if (staleResult === 'resolve-last') old.resolve(f.Hls);
    if (staleResult === 'reject') old.reject(new Error('stale chunk failed'));
    await flush();
    assert.equal(f.engines.length, 1);
    assert.equal(f.engines[0].video, f.videos[1]);
    assert.equal(f.engines[0].destroyCalls, 0);
    assert.equal(f.host.children.length, 1);
    f.pool.clear();
  }
});

test('HLS adoption cancels budgets, resumes normal config, and only forwards active fatal errors', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8', { Authorization: 'secret' });
  f.pool.sync([entry]);
  await flush();
  const hls = f.engines[0], video = f.videos[0], xhr = new Xhr();
  hls.config.xhrSetup(xhr, 'https://media.test/first.ts');
  video.ranges = [[0, 1]];
  const writes = [...video.srcWrites], loads = video.loadCalls;
  const adopted = f.pool.take(entry.url, entry.headers);
  assert.equal(adopted.customPlayer.hls, hls);
  assert.equal(hls.stopCalls, 1);
  assert.equal(hls.listenerCount + video.listenerCount + xhr.listenerCount, 0);
  assert.equal(f.timers.size, 0);
  const errors = [];
  adopted.resume(error => errors.push(error));
  for (const [key, value] of Object.entries({
    maxBufferLength: 60, maxMaxBufferLength: 120, backBufferLength: 75, maxBufferSize: 200e6,
  })) assert.equal(hls.config[key], value, key);
  assert.deepEqual(hls.starts, [-1]);
  assert.equal(video.muted, true);
  assert.equal(video.defaultMuted, true);
  assert.equal(video.playCalls, 0);
  assert.deepEqual(video.srcWrites, writes);
  assert.equal(video.loadCalls, loads);
  const activeXhr = new Xhr();
  hls.config.xhrSetup(activeXhr, 'https://media.test/active.ts');
  assert.deepEqual(activeXhr.headers, { Authorization: 'secret' });
  assert.equal(activeXhr.withCredentials, false);
  assert.equal(activeXhr.listenerCount, 0);
  xhr.progress(BUFFER_BYTES * 2);
  hls.emit('FRAG_LOADED', { stats: { loaded: BUFFER_BYTES * 2 } });
  video.ranges = [[0, 100]];
  hls.emit('FRAG_BUFFERED');
  hls.emit('LEVEL_LOADED', { details: { live: true } });
  video.dispatch('error');
  f.pool.sync([candidate('b.mp4')]);
  f.pool.clear();
  f.advance(8000);
  assert.equal(hls.stopCalls, 1);
  assert.equal(hls.destroyCalls, 0);
  hls.emit('ERROR', { fatal: false });
  assert.equal(errors.length, 0);
  hls.emit('ERROR', { fatal: true, details: 'fragLoadError' });
  assert.equal(errors.length, 1);
  assert.equal(errors[0], 'fragLoadError');
  const replacementErrors = [];
  adopted.resume(error => replacementErrors.push(error));
  assert.equal(hls.listenerCount, 1, 'resume replaces rather than duplicates the error callback');
  hls.emit('ERROR', { fatal: true, type: 'mediaError' });
  assert.equal(errors.length, 1);
  assert.equal(replacementErrors[0], 'mediaError');
  adopted.destroy();
  adopted.destroy();
  adopted.resume(() => assert.fail('destroyed entries cannot resume'));
  hls.emit('ERROR', { fatal: true });
  assert.equal(hls.destroyCalls, 1);
  assert.equal(hls.listenerCount, 0);
  assert.equal(video.src, '');
  assert.equal(video.loadCalls, loads + 1);
  assert.equal(video.parentNode, null);
});

test('failed candidates are not rebuilt by repeated sync until they leave or clear resets the pool', async () => {
  for (const failure of ['media', 'hls', 'live', 'timeout', 'take', 'loadHls']) {
    const f = fixture({
      loadHls: failure === 'loadHls' ? () => Promise.reject(new Error('chunk failed')) : undefined,
    });
    const a = candidate('a.m3u8'), b = candidate('b.mp4');
    f.pool.sync([a, b]);
    await flush();
    if (failure === 'media') f.videos[0].dispatch('error');
    if (failure === 'hls') f.engines[0].emit('ERROR', { fatal: true });
    if (failure === 'live') f.engines[0].emit('LEVEL_LOADED', { details: { live: true } });
    if (failure === 'timeout') {
      f.videos[1].readyState = 2;
      f.advance(8000);
    }
    if (failure === 'take') assert.equal(f.pool.take(a.url), null);
    for (let i = 0; i < 3; i++) {
      f.pool.sync([b, a]);
      f.pool.sync([a, b]);
    }
    await flush();
    assert.equal(f.loadHlsCalls, 1, failure);
    assert.equal(f.videos.length, 2, failure);
    assert.equal(f.pool.take(a.url), null);
    f.pool.sync([b]);
    f.pool.sync([a, b]);
    await flush();
    assert.equal(f.loadHlsCalls, 2, 'leaving the candidates allows another attempt');
    f.pool.clear();
    f.pool.sync([a]);
    await flush();
    assert.equal(f.loadHlsCalls, 3, 'clear allows a fresh mode/session');
    f.pool.clear();
  }
});

test('changing headers retries a failed URL without reviving the old identity', async () => {
  const f = fixture();
  const old = candidate('a.m3u8', { Authorization: 'expired' });
  const next = candidate('a.m3u8', { Authorization: 'fresh' });
  f.pool.sync([old]);
  await flush();
  f.engines[0].emit('ERROR', { fatal: true });
  f.pool.sync([old]);
  f.pool.sync([next]);
  await flush();
  assert.equal(f.engines.length, 2);
  const xhr = new Xhr();
  f.engines[1].config.xhrSetup(xhr, next.url);
  assert.equal(xhr.headers.Authorization, 'fresh');
  f.pool.clear();
});

test('pending and loaded HLS candidates never exceed two even during replacement', async () => {
  const pending = deferred();
  const f = fixture({ loadHls: () => pending.promise });
  const a = candidate('a.m3u8'), b = candidate('b.m3u8');
  const c = candidate('c.m3u8'), d = candidate('d.m3u8');
  f.pool.sync([a, b, c]);
  assert.equal(f.videos.length, 2);
  f.pool.sync([b, c]);
  assert.equal(f.host.children.length, 2);
  assert.equal(f.timers.size, 2);
  pending.resolve(f.Hls);
  await flush();
  assert.deepEqual(f.engines.map(hls => hls.url), [b.url, c.url]);
  f.pool.sync([c, d]);
  assert.equal(f.engines[0].destroyCalls, 1, 'old engine is destroyed before the replacement loads');
  await flush();
  assert.equal(f.engines.filter(hls => !hls.destroyCalls).length, 2);
  assert.equal(f.host.peak, 2);
  f.pool.clear();
  assert.ok(f.engines.every(hls => hls.destroyCalls === 1));
  assert.ok(f.videos.every(video => video.playCalls === 0));
  assert.equal(f.timers.size, 0);
});

test('unready take destroys an already loading HLS engine immediately, without waiting', async () => {
  const f = fixture();
  const entry = candidate('a.m3u8');
  f.pool.sync([entry]);
  await flush();
  const hls = f.engines[0], xhr = new Xhr();
  hls.config.xhrSetup(xhr, 'https://media.test/slow-fragment.ts');
  f.videos[0].readyState = 1;
  const result = f.pool.take(entry.url);
  assert.equal(result, null);
  assert.equal(hls.destroyCalls, 1);
  assert.equal(hls.listenerCount + xhr.listenerCount + f.videos[0].listenerCount, 0);
  assert.equal(f.timers.size, 0);
  assert.equal(f.host.children.length, 0);
  f.pool.sync([entry]);
  assert.equal(f.loadHlsCalls, 1);
});
