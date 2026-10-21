const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(join(__dirname, '../src/shared/shortVideoBuffer.js'), 'utf8');
const MiB = 1024 * 1024;
const CACHE_BYTES = 512 * MiB;
const NORMAL_BUFFER = { maxBufferLength: 60, maxMaxBufferLength: 120, backBufferLength: 75, maxBufferSize: 200e6 };
const flush = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const candidate = (name, headers, key = name) => ({ key, url: `https://media.test/${name}`, headers });

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
  pauseCalls = 0;
  paused = true;
  readyState = 0;
  currentTime = 0;
  duration = NaN;
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
  pause() { this.pauseCalls++; this.paused = true; }
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
      'ERROR', 'MANIFEST_LOADED', 'MANIFEST_PARSED', 'LEVEL_LOADED', 'FRAG_LOADED', 'FRAG_BUFFERED',
    ].map(name => [name, name]));
    static isSupported() { return supported; }
    stopCalls = 0;
    destroyCalls = 0;
    starts = [];
    pauseBufferCalls = 0;
    resumeBufferCalls = 0;
    bufferingEnabled = true;
    loadingEnabled = false;
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
      this.video = this.media = video;
      video.src = 'blob:mock-media-source';
    }
    stopLoad() { this.stopCalls++; }
    startLoad(position) { this.starts.push(position); this.loadingEnabled = this.bufferingEnabled = true; }
    pauseBuffering() { this.pauseBufferCalls++; this.bufferingEnabled = false; }
    resumeBuffering() { this.resumeBufferCalls++; this.bufferingEnabled = true; }
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
    newVideo() { return host.ownerDocument.createElement('video'); },
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

const sync = (f, candidates, keys = candidates.map(item => item.key)) => f.pool.sync(candidates, keys);
const take = (f, item) => f.pool.take(item.url, item.headers, item.key);
const metadata = (video, duration) => {
  video.duration = duration;
  video.dispatch('loadedmetadata');
};
const manifest = (hls, duration, live = false) => hls.emit('MANIFEST_PARSED', {
  levels: [{ details: { totalduration: duration, live } }],
});
const fragment = (hls, bytes) => hls.emit('FRAG_LOADED', { frag: { stats: { loaded: bytes } } });
const external = (f, item, duration = 120, customPlayer, onError) => {
  const video = f.newVideo();
  video.src = item.url;
  video.duration = duration;
  video.paused = false;
  f.pool.adopt({ ...item, video, customPlayer, onError });
  return video;
};

// Native media has no byte estimate: all seven short episode slots are allowed.
test('native next is metadata-gated, then requests auto preload without playing or reloading', () => {
  const f = fixture(), item = candidate('a.mp4?token=A#exact');
  sync(f, [item]);
  const video = f.videos[0];
  assert.equal(f.host.children[0], video);
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
  metadata(video, 359.99);
  assert.equal(video.preload, 'auto');
  assert.equal(video.src, item.url);
  assert.equal(video.loadCalls, 1);
  f.advance(60000);
  assert.equal(video.src, item.url, 'no old eight-second deadline truncates full-episode buffering');
  assert.equal(video.playCalls, 0);
  f.pool.clear();
  assert.equal(video.src, '');
  assert.equal(video.loadCalls, 2);
  assert.equal(video.listenerCount, 0);
  assert.equal(f.host.children.length, 0);
});

test('only next candidates download; keys alone never download unvisited previous episodes', () => {
  const f = fixture(), a = candidate('a.mp4'), b = candidate('b.mp4');
  const keys = ['prev-5', 'prev-4', 'prev-3', 'prev-2', 'prev-1', a.key, b.key];
  sync(f, [b, b], keys);
  assert.equal(f.videos.length, 1);
  assert.equal(take(f, a), null);
  sync(f, [b], keys);
  assert.equal(f.videos.length, 1);
  sync(f, [], keys);
  assert.equal(f.videos[0].src, b.url, 'already warmed media inside the window survives next changing');
  assert.equal(f.videos[0].preload, 'none', 'non-next media stops prefetching without losing its source');
  sync(f, [b], keys);
  assert.equal(f.videos.length, 1);
  sync(f, [candidate('outside.mp4')], keys);
  assert.equal(f.videos.length, 1, 'candidates outside the seven-key window are not requested');
  f.pool.clear();
});

test('a new window/next may sync before old current parks and warmed current is taken', async () => {
  for (const extension of ['mp4', 'm3u8']) {
    const f = fixture();
    const items = [10, 11, 12].map(id => candidate(`${id}.${extension}`, undefined, String(id)));
    sync(f, [items[0], items[1]], ['5', '6', '7', '8', '9', '10', '11']);
    await flush();
    if (extension === 'm3u8') f.engines.forEach(hls => manifest(hls, 240));
    else f.videos.forEach(video => metadata(video, 240));
    const old = take(f, items[0]), warmedVideo = f.videos[1], warmedHls = f.engines[1];
    old.video.currentTime = 48;
    const loads = warmedVideo.loadCalls, sources = [...warmedVideo.srcWrites];
    // PlayPage has switched to 11 and resolved 12; ArtPlayer still owns 10.
    sync(f, [items[2]], ['6', '7', '8', '9', '10', '11', '12']);
    await flush();
    assert.notEqual(warmedVideo.src, '');
    if (warmedHls) {
      assert.equal(warmedHls.destroyCalls, 0);
      assert.equal(warmedHls.bufferingEnabled, false);
    } else assert.equal(warmedVideo.preload, 'none');
    assert.equal(f.pool.park(items[0].key), true);
    const current = take(f, items[1]);
    assert.equal(current.video, warmedVideo, '11 keeps its prewarmed node, so 10 need not be repurposed');
    assert.equal(current.restored, false);
    current.resume();
    assert.equal(warmedVideo.loadCalls, loads);
    assert.deepEqual(warmedVideo.srcWrites, sources);
    assert.equal(f.pool.park(items[1].key), true);
    const restored = take(f, items[0]);
    assert.equal(restored.video, old.video);
    assert.equal(restored.position, 48);
    assert.equal(restored.restored, true);
    restored.destroy();
    f.pool.clear();
  }
});

test('seven native slots retain visited nodes; moving the window prunes the oldest, not active', () => {
  const f = fixture();
  const items = Array.from({ length: 8 }, (_, i) => candidate(`${10 + i}.mp4`));
  const keys = items.slice(0, 7).map(item => item.key);
  for (let i = 0; i < 6; i++) {
    const video = external(f, items[i]);
    video.currentTime = i + 1;
    sync(f, [], keys);
    assert.equal(f.pool.park(items[i].key), true);
  }
  sync(f, [items[6]], keys);
  assert.equal(f.host.children.length, 7, 'native nodes are not charged fictitious 64MiB fees');
  assert.ok(f.videos.every(video => video.src && video.loadCalls === 0 || video.loadCalls === 1));
  const restored = take(f, items[0]);
  assert.equal(restored.video, f.videos[0]);
  assert.equal(restored.position, 1);
  sync(f, [items[7]], items.slice(1).map(item => item.key));
  assert.notEqual(f.videos[0].src, '', 'active is never pruned, even outside keys');
  assert.equal(f.pool.park(items[0].key), true);
  sync(f, [items[7]], items.slice(1).map(item => item.key));
  assert.equal(f.videos[0].src, '');
  assert.equal(take(f, items[0]), null, 'pruning drops position metadata as well');
  f.pool.clear();
  assert.equal(f.host.children.length, 0);
});

test('manual park/take preserves the same node, engine, buffer and position; ended rewinds to zero', async () => {
  const f = fixture(), item = candidate('a.m3u8');
  sync(f, [item]);
  await flush();
  const video = f.videos[0], hls = f.engines[0];
  manifest(hls, 240);
  video.ranges = [[0, 240]];
  const first = take(f, item);
  assert.equal(first.restored, false);
  first.resume();
  video.currentTime = 78.5;
  video.remove();
  sync(f, [], [item.key]);
  const loads = video.loadCalls, writes = [...video.srcWrites];
  assert.equal(f.pool.park(item.key), true);
  assert.equal(video.paused, true);
  assert.equal(f.host.children[0], video);
  assert.equal(hls.bufferingEnabled, false, 'parked previous is paused without aborting its source');
  const restored = take(f, item);
  assert.equal(restored.video, video);
  assert.equal(restored.customPlayer.hls, hls);
  assert.equal(restored.position, 78.5);
  assert.equal(restored.restored, true);
  restored.resume();
  assert.equal(hls.bufferingEnabled, true);
  assert.equal(hls.stopCalls, 0);
  assert.equal(hls.destroyCalls, 0);
  assert.deepEqual(video.ranges, [[0, 240]]);
  assert.equal(video.loadCalls, loads);
  assert.deepEqual(video.srcWrites, writes);
  video.currentTime = 240;
  assert.equal(f.pool.park(item.key, { ended: true }), true);
  const ended = take(f, item);
  assert.equal(ended.position, 0);
  assert.equal(ended.video.currentTime, 0);
  ended.destroy();
  ended.destroy();
  assert.equal(hls.destroyCalls, 1);
});

test('long, unknown, live, invalid, FLV and DASH park only position and leave destruction to the caller', () => {
  for (const [name, duration, invalid] of [
    ['a.mp4', 360], ['a.mp4', 1000], ['a.mp4', NaN], ['a.mp4', 0],
    ['a.m3u8', Infinity], ['a.flv', 120], ['a.mpd', 120], ['a.mp4', 120, true],
  ]) {
    const f = fixture({ preferNativeHls: true, nativeHls: 'maybe' });
    const item = candidate(name);
    let destroys = 0;
    const customPlayer = name.endsWith('.flv') ? { flv: { destroy() { destroys++; } } }
      : name.endsWith('.mpd') ? { dash: { destroy() { destroys++; } } } : null;
    const video = external(f, item, duration, customPlayer);
    video.currentTime = 42.5;
    const src = video.src, loads = video.loadCalls;
    assert.equal(f.pool.park(item.key, { invalid }), false, name);
    assert.equal(video.paused, true);
    assert.equal(video.src, src, 'park(false) must not clear the source');
    assert.equal(video.loadCalls, loads);
    assert.equal(video.listenerCount, 0);
    const restored = take(f, item);
    assert.equal(restored.video, null);
    assert.equal(restored.customPlayer, null);
    assert.equal(restored.restored, true);
    assert.equal(restored.position, 42.5);
    restored.resume(() => assert.fail('position-only entries cannot emit errors'));
    restored.destroy();
    f.pool.clear();
    assert.equal(video.src, src);
    assert.equal(destroys, 0, 'only the caller owns external FLV/DASH destruction');
  }
});

test('ended park resets position even when no media can be retained', () => {
  const f = fixture(), item = candidate('long.mp4');
  const video = external(f, item, 720);
  video.currentTime = 720;
  assert.equal(f.pool.park(item.key, { ended: true }), false);
  assert.equal(take(f, item).position, 0);
  assert.equal(f.pool.park(item.key, { ended: true }), false);
  assert.equal(take(f, item).position, 0);
  f.pool.clear();
});

test('adopt updates the same prewarmed node and late engine without resetting progress or gesture permission', async () => {
  const f = fixture(), item = candidate('a.m3u8');
  const video = external(f, item);
  video.currentTime = 21;
  const hls = new f.Hls({ ...NORMAL_BUFFER });
  hls.attachMedia(video);
  f.pool.adopt({ ...item, video, customPlayer: { hls }, activated: true });
  const listeners = hls.listenerCount;
  f.pool.adopt({ ...item, video, customPlayer: { hls }, activated: false });
  assert.equal(hls.listenerCount, listeners);
  assert.equal(hls.config.backBufferLength, Infinity);
  assert.equal(f.pool.park(item.key), true);
  const result = take(f, item);
  assert.equal(result.position, 21);
  assert.equal(result.video, video);
  assert.equal(result.activated, true);
  assert.equal(result.customPlayer.hls, hls);
  assert.equal(f.videos.length, 1);
  assert.equal(f.loadHlsCalls, 0);
  assert.equal(video.loadCalls, 0);
  result.destroy();
});

test('a late native HLS fallback can be adopted without replacing its video', () => {
  const f = fixture(), item = candidate('a.m3u8');
  const video = external(f, item);
  video.currentTime = 16;
  f.pool.adopt({ ...item, video, customPlayer: { direct: true } });
  assert.equal(f.pool.park(item.key), true);
  const restored = take(f, item);
  assert.equal(restored.video, video);
  assert.equal(restored.position, 16);
  assert.equal(video.loadCalls, 0);
  restored.destroy();
});

test('HLS next waits for playlist duration; full 24-fragment short episodes are not stopped at three seconds', async () => {
  const f = fixture(), item = candidate('next.m3u8');
  sync(f, [item]);
  await flush();
  const hls = f.engines[0], video = f.videos[0];
  assert.equal(hls.config.autoStartLoad, false);
  hls.emit('MANIFEST_PARSED', { levels: [{}] });
  assert.deepEqual(hls.starts, [-1], 'master playlist can fetch its selected media playlist');
  assert.equal(hls.bufferingEnabled, false, 'unknown duration never opens segment loading for next');
  hls.emit('LEVEL_LOADED', { details: { live: false, totalduration: 240 } });
  assert.equal(hls.bufferingEnabled, true);
  assert.equal(hls.config.maxBufferLength, 240);
  assert.equal(hls.config.maxMaxBufferLength, 240);
  assert.equal(hls.config.maxBufferSize, CACHE_BYTES);
  assert.equal(hls.config.backBufferLength, Infinity);
  for (let i = 1; i <= 24; i++) {
    fragment(hls, 4 * MiB);
    video.ranges = [[0, i * 10]];
    hls.emit('FRAG_BUFFERED');
    assert.equal(hls.bufferingEnabled, true);
    assert.equal(hls.stopCalls, 0);
    assert.equal(hls.destroyCalls, 0);
  }
  assert.equal(video.playCalls, 0);
  assert.equal(take(f, item).video, video, '96MiB short clips are not subject to a 64MiB per-clip cap');
  f.pool.park(item.key);
  f.pool.clear();
});

test('long/live next playlists are discarded before enabling fragments; zero/unknown stay gated', async () => {
  for (const [duration, live, discarded] of [[360, false, true], [900, false, true], [120, true, true], [0, false, false], [NaN, false, false]]) {
    const f = fixture(), item = candidate('a.m3u8');
    sync(f, [item]);
    await flush();
    const hls = f.engines[0];
    hls.emit('MANIFEST_PARSED', { levels: [{}] });
    assert.equal(hls.bufferingEnabled, false);
    hls.emit('LEVEL_LOADED', { details: { live, totalduration: duration } });
    assert.equal(hls.destroyCalls, discarded ? 1 : 0);
    assert.equal(hls.resumeBufferCalls, 0, 'next never resumes fragment buffering for non-short duration');
    if (discarded) assert.equal(take(f, item), null);
    f.pool.clear();
  }
});

test('native next duration boundaries discard long/live and do not full-preload unknown duration', () => {
  for (const duration of [0, NaN, 360, Infinity]) {
    const f = fixture(), item = candidate('a.mp4');
    sync(f, [item]);
    const video = f.videos[0];
    metadata(video, duration);
    if (duration >= 360) assert.equal(video.src, '');
    else assert.equal(video.preload, 'metadata');
    f.pool.clear();
  }
});

test('active long/unknown/live retains original rolling config and is never stopped by duration classification', () => {
  for (const duration of [NaN, 0, 360, 900, Infinity]) {
    const f = fixture(), item = candidate('current.m3u8');
    const normal = { maxBufferLength: 120, maxMaxBufferLength: 240, backBufferLength: 180, maxBufferSize: 800e6 };
    const hls = new f.Hls({ ...normal });
    const video = external(f, item, duration, { hls });
    if (duration === Infinity) manifest(hls, 120, true);
    else manifest(hls, duration);
    for (const [key, value] of Object.entries(normal)) assert.equal(hls.config[key], value);
    assert.equal(hls.stopCalls, 0);
    assert.equal(hls.pauseBufferCalls, 0);
    assert.equal(hls.destroyCalls, 0);
    video.currentTime = 38;
    assert.equal(f.pool.park(item.key), false);
    assert.equal(take(f, item).position, 38);
    assert.equal(video.loadCalls, 0);
    f.pool.clear();
  }
});

test('duration changes enable full buffering for active short clips and restore rolling config when long', () => {
  const f = fixture(), item = candidate('a.m3u8');
  const hls = new f.Hls({ ...NORMAL_BUFFER });
  const video = external(f, item, NaN, { hls });
  metadata(video, 100);
  assert.equal(hls.config.backBufferLength, Infinity);
  assert.equal(hls.config.maxBufferLength, 100);
  metadata(video, 360);
  assert.equal(hls.config.backBufferLength, NORMAL_BUFFER.backBufferLength);
  assert.equal(hls.stopCalls, 0);
  assert.equal(f.pool.park(item.key), false);
  assert.equal(hls.destroyCalls, 0);
});

test('take transfers attached native/HLS synchronously even before readiness or gesture authorization', async () => {
  for (const name of ['a.mp4', 'a.m3u8']) {
    const f = fixture(), item = candidate(name);
    sync(f, [item]);
    await flush();
    const video = f.videos[0];
    assert.equal(video.readyState, 0);
    const result = take(f, item);
    assert.equal(result.video, video);
    assert.equal(result.activated, false);
    assert.equal(result.then, undefined);
    result.resume();
    if (f.engines[0]) {
      const hls = f.engines[0];
      assert.equal(hls.stopCalls, 0, 'taking a pending manifest must not abort it');
      assert.equal(hls.destroyCalls, 0);
      manifest(hls, 240);
      assert.deepEqual(hls.starts, [-1]);
      assert.equal(hls.bufferingEnabled, true);
    } else assert.equal(video.preload, 'auto');
    result.destroy();
  }
});

test('HLS budget includes current, deduplicates XHR/fragment bytes, and evicts farthest parked first', async () => {
  const f = fixture();
  const items = ['old', 'near', 'current', 'next'].map(name => candidate(`${name}.m3u8`));
  const keys = items.map(item => item.key), handles = [];
  for (let i = 0; i < 3; i++) {
    sync(f, [items[i]], keys);
    await flush();
    manifest(f.engines[i], 240);
    const handle = take(f, items[i]);
    handles.push(handle);
    handle.video.currentTime = i + 10;
    fragment(f.engines[i], 128 * MiB);
    if (i < 2) assert.equal(f.pool.park(items[i].key), true);
  }
  sync(f, [items[3]], keys);
  await flush();
  const hls = f.engines[3];
  manifest(hls, 240);
  const xhr = new Xhr();
  hls.config.xhrSetup(xhr, 'https://media.test/frag.ts');
  xhr.progress(128 * MiB);
  fragment(hls, 128 * MiB);
  assert.ok(f.engines.every(engine => engine.destroyCalls === 0), 'exactly 512MiB is allowed; no double-counting');
  xhr.progress(129 * MiB);
  assert.equal(f.engines[0].destroyCalls, 1, 'farthest parked episode is evicted before near/next/current');
  assert.equal(f.engines[1].destroyCalls, 0);
  assert.equal(f.engines[2].destroyCalls, 0);
  assert.equal(hls.destroyCalls, 0);
  assert.equal(f.engines[2].stopCalls, 0);
  const restored = take(f, items[0]);
  assert.equal(restored.video, null);
  assert.equal(restored.restored, true);
  assert.equal(restored.position, 10);
  // The previously current video was relinquished, not destroyed by take.
  assert.equal(f.engines[2].destroyCalls, 0);
  f.pool.clear();
});

test('HLS budget preserves visited next before an unvisited non-next cache', async () => {
  const f = fixture();
  const [old, current, next] = ['old', 'current', 'next'].map(name => candidate(`${name}.m3u8`));
  const keys = [old.key, current.key, next.key];
  // Skip the old prewarm, visit next, then return to current.
  for (const item of [old, next, current]) {
    sync(f, [item], keys);
    await flush();
    const hls = f.engines[f.engines.length - 1];
    manifest(hls, 240);
    fragment(hls, 128 * MiB);
    if (item !== old) take(f, item);
    if (item === next) assert.equal(f.pool.park(item.key), true);
  }
  const [oldHls, nextHls, currentHls] = f.engines;
  sync(f, [next], keys);
  currentHls.video.paused = false;
  fragment(currentHls, 129 * MiB);
  assert.equal(oldHls.destroyCalls, 1, 'non-next cache must yield even when next was already visited');
  assert.equal(nextHls.destroyCalls, 0);
  assert.equal(currentHls.destroyCalls, 0);
  assert.equal(currentHls.stopCalls, 0);
  assert.equal(currentHls.video.pauseCalls, 0);
  assert.equal(currentHls.video.paused, false);
  assert.equal(f.pool.park(current.key), true);
  const restored = take(f, next);
  assert.equal(restored.restored, true);
  assert.equal(restored.video, nextHls.video);
  assert.equal(restored.customPlayer.hls, nextHls);
  restored.destroy();
  f.pool.clear();
});

test('when only current+next exceed the unified budget, next falls back without stopping current', async () => {
  const f = fixture(), current = candidate('current.m3u8'), next = candidate('next.m3u8');
  sync(f, [current], [current.key, next.key]);
  await flush();
  manifest(f.engines[0], 300);
  take(f, current);
  fragment(f.engines[0], 400 * MiB);
  sync(f, [next], [current.key, next.key]);
  await flush();
  manifest(f.engines[1], 300);
  fragment(f.engines[1], 113 * MiB);
  assert.equal(f.engines[1].destroyCalls, 1);
  assert.equal(f.engines[0].destroyCalls, 0);
  assert.equal(f.engines[0].stopCalls, 0);
  assert.equal(f.engines[0].pauseBufferCalls, 1, 'only its original unclassified prewarm was paused');
  assert.equal(take(f, next), null);
  sync(f, [next], [current.key, next.key]);
  await flush();
  assert.equal(f.engines.length, 2, 'budget evictions are deduplicated');
  f.pool.clear();
});

test('an oversized active clip falls back to rolling buffers and park(false), never interruption', async () => {
  const f = fixture(), item = candidate('current.m3u8');
  const hls = new f.Hls({ ...NORMAL_BUFFER });
  const video = external(f, item, 300, { hls });
  const xhr = new Xhr();
  hls.config.xhrSetup(xhr, 'https://media.test/current.ts');
  xhr.progress(CACHE_BYTES + 1);
  assert.equal(hls.config.backBufferLength, 75);
  metadata(video, 300);
  assert.equal(hls.config.backBufferLength, 75, 'duration events cannot re-enable an over-budget cache');
  assert.equal(hls.stopCalls, 0);
  assert.equal(hls.pauseBufferCalls, 0);
  assert.equal(hls.destroyCalls, 0);
  assert.equal(video.pauseCalls, 0);
  video.currentTime = 123;
  assert.equal(f.pool.park(item.key), false);
  assert.equal(hls.destroyCalls, 0, 'caller owns the only destruction after park(false)');
  assert.equal(xhr.listenerCount, 0);
  assert.equal(take(f, item).position, 123);
  f.pool.clear();
});

test('headers and exact URLs are preserved, sorted for identity, and never bypassed via native fallback', async () => {
  const f = fixture();
  const a = candidate('a.m3u8?token=A', { Authorization: 'A', Z: 3 }, 'a');
  const b = candidate('a.m3u8?token=A', { Authorization: 'B', Z: 3 }, 'b');
  sync(f, [a, b]);
  a.headers.Authorization = 'mutated';
  await flush();
  assert.equal(f.engines.length, 2);
  const xhr = new Xhr();
  f.engines[0].config.xhrSetup(xhr, a.url);
  assert.deepEqual(xhr.headers, { Authorization: 'A', Z: '3' });
  assert.equal(xhr.withCredentials, false);
  sync(f, [{ ...a, headers: { Z: 3, Authorization: 'A' } }, b]);
  assert.equal(f.loadHlsCalls, 2);
  assert.equal(f.pool.take(a.url.toLowerCase(), { Z: 3, Authorization: 'A' }, a.key), null);
  assert.equal(f.pool.take(a.url, { Z: 3, authorization: 'A' }, a.key), null);
  assert.equal(f.pool.take(a.url, { Z: 3, Authorization: 'C' }, a.key), null);
  const result = f.pool.take(a.url, { Z: 3, Authorization: 'A' }, a.key);
  assert.equal(result.customPlayer.hls, f.engines[0]);
  xhr.dispatch('load', { loaded: MiB });
  assert.equal(xhr.listenerCount, 0, 'completed XHR listeners do not accumulate over full episodes');
  f.pool.clear();
  assert.equal(f.engines[0].destroyCalls, 0, 'clear relinquishes active');
  assert.equal(f.engines[1].destroyCalls, 1);
  assert.equal(f.engines[0].listenerCount, 0);
});

test('native custom headers and FLV/DASH next do not issue media requests', () => {
  for (const item of [candidate('a.mp4', { Authorization: 'secret' }), candidate('a.mp4', { Empty: '' }), candidate('a.flv'), candidate('a.mpd')]) {
    const f = fixture();
    sync(f, [item]);
    assert.equal(f.videos.length, 0);
    assert.equal(f.loadHlsCalls, 0);
    assert.equal(take(f, item), null);
  }
});

test('native HLS requires preference, capability, and no custom headers', async () => {
  for (const [preferNativeHls, nativeHls, headers, native] of [
    [true, 'maybe', {}, true], [true, 'probably', { ignored: null }, true],
    [true, '', {}, false], [false, 'probably', {}, false], [true, 'probably', { Authorization: 'secret' }, false],
  ]) {
    const f = fixture({ preferNativeHls, nativeHls }), item = candidate('a.m3u8', headers);
    sync(f, [item]);
    await flush();
    assert.equal(f.engines.length, native ? 0 : 1);
    assert.equal(f.videos[0].src, native ? item.url : 'blob:mock-media-source');
    assert.equal(f.videos[0].attributes.has('crossorigin'), false);
    f.pool.clear();
  }
  const f = fixture({ preferNativeHls: true, nativeHls: 'probably', supported: false });
  sync(f, [candidate('a.m3u8', { Authorization: 'secret' })]);
  await flush();
  assert.equal(f.engines.length, 0);
  assert.deepEqual(f.videos[0].srcWrites, []);
  assert.equal(f.host.children.length, 0);
});

test('external xhrSetup survives observation and is restored on relinquishment', () => {
  const f = fixture(), item = candidate('a.m3u8');
  const original = function (xhr, url) {
    assert.equal(this, hls.config);
    xhr.open('GET', url, true);
    xhr.setRequestHeader('Authorization', 'external');
    return 'result';
  };
  const hls = new f.Hls({ ...NORMAL_BUFFER, xhrSetup: original });
  const video = external(f, item, 120, { hls });
  const xhr = new Xhr();
  assert.equal(hls.config.xhrSetup(xhr, item.url), 'result');
  assert.equal(xhr.headers.Authorization, 'external');
  f.pool.detach(video);
  assert.equal(hls.config.xhrSetup, original);
  assert.equal(xhr.listenerCount, 0);
  assert.equal(hls.listenerCount, 0);
  assert.equal(hls.destroyCalls, 0);
  assert.equal(video.loadCalls, 0);
  assert.equal(take(f, item).video, null);
});

test('stable episode keys restore progress across refreshed URLs/headers without reusing stale media', () => {
  const f = fixture(), old = candidate('a.mp4?expired', {}, 'episode');
  const video = external(f, old);
  video.currentTime = 45;
  f.pool.park(old.key);
  const fresh = { ...old, url: 'https://media.test/a.mp4?fresh', headers: { Authorization: 'fresh' } };
  const restored = take(f, fresh);
  assert.equal(restored.restored, true);
  assert.equal(restored.position, 45);
  assert.equal(restored.video, null);
  assert.equal(video.src, '');
  f.pool.clear();
});

test('activate synchronously plays/pauses only attached nonactive videos without source/load changes', async () => {
  const pending = deferred(), f = fixture({ loadHls: () => pending.promise });
  const current = candidate('current.mp4'), a = candidate('a.mp4'), b = candidate('b.m3u8');
  const activeVideo = external(f, current);
  sync(f, [a, b], [current.key, a.key, b.key]);
  const video = f.videos[1];
  const calls = [];
  video.play = () => { calls.push(['play', video.muted]); video.paused = false; return Promise.reject({ name: 'AbortError' }); };
  video.pause = () => { calls.push(['pause']); video.paused = true; };
  const loads = video.loadCalls, sources = [...video.srcWrites];
  f.pool.activate();
  assert.deepEqual(calls, [['play', false], ['pause']]);
  assert.equal(activeVideo.pauseCalls, 0);
  assert.equal(f.videos[2].playCalls, 0, 'pending imports have no source to authorize');
  assert.equal(video.muted, false, 're-muting a primed native video can discard its preloaded bytes in WebKit');
  assert.equal(video.paused, true, 'authorization must not leave background audio playing');
  assert.equal(video.loadCalls, loads);
  assert.deepEqual(video.srcWrites, sources);
  await flush();
  f.pool.activate();
  assert.equal(calls.length, 2);
  assert.equal(take(f, a).activated, true);
  f.pool.clear();
  pending.resolve(f.Hls);
  await flush();
  assert.equal(f.engines.length, 0);
});

test('native HLS avoids re-muting after activation while custom HLS retains its original mute state', async () => {
  for (const preferNativeHls of [true, false]) {
    const f = fixture({ preferNativeHls, nativeHls: 'probably' }), item = candidate('next.m3u8');
    sync(f, [item]);
    await flush();
    const video = f.videos[0];
    video.play = () => {
      assert.equal(video.muted, false, 'activation must still request audible playback');
      video.paused = false;
      return Promise.resolve();
    };
    const loads = video.loadCalls, sources = [...video.srcWrites];
    f.pool.activate();
    await flush();
    assert.equal(video.paused, true);
    assert.equal(video.muted, !preferNativeHls);
    assert.equal(video.loadCalls, loads);
    assert.deepEqual(video.srcWrites, sources);
    assert.equal(take(f, item).activated, true);
    f.pool.clear();
  }
});

test('failed activation is silent, reports false after take, and can retry after parking', async () => {
  for (const failure of ['NotAllowedError', 'NotSupportedError', 'throw']) {
    const f = fixture(), item = candidate('a.mp4');
    sync(f, [item]);
    const video = f.videos[0];
    metadata(video, 120);
    video.play = () => {
      if (failure === 'throw') throw new Error('play failed');
      return Promise.reject({ name: failure });
    };
    f.pool.activate();
    const result = take(f, item);
    await flush();
    assert.equal(result.activated, false, 'late permission rejection updates the handed-off handle');
    assert.equal(f.pool.park(item.key), true);
    video.play = () => Promise.resolve();
    f.pool.activate();
    await flush();
    assert.equal(take(f, item).activated, true);
    f.pool.clear();
  }
});

test('adopt(false) preserves authorization but cannot hide a still-pending activation rejection', async () => {
  const f = fixture(), item = candidate('a.mp4'), playing = deferred();
  sync(f, [item]);
  const video = f.videos[0];
  video.play = () => playing.promise;
  f.pool.activate();
  const result = take(f, item);
  f.pool.adopt({ ...item, video, activated: false });
  assert.equal(result.activated, true);
  playing.reject({ name: 'NotAllowedError' });
  await flush();
  assert.equal(result.activated, false);
  result.destroy();
});

test('fatal errors dynamically report only active callbacks; parked/background errors evict silently', async () => {
  const f = fixture(), a = candidate('a.m3u8'), b = candidate('b.m3u8');
  sync(f, [a, b]);
  await flush();
  f.engines.forEach(hls => manifest(hls, 120));
  const errors = [], result = take(f, a);
  result.resume(message => errors.push(`old:${message}`));
  f.pool.adopt({ ...a, video: result.video, customPlayer: result.customPlayer, onError: message => errors.push(`new:${message}`) });
  result.resume();
  f.engines[0].emit('ERROR', { fatal: false, details: 'fragLoadError' });
  assert.deepEqual(errors, []);
  f.engines[0].emit('ERROR', { fatal: true, details: 'fragLoadError' });
  assert.deepEqual(errors, ['new:fragLoadError']);
  assert.equal(f.engines[0].destroyCalls, 0);
  assert.equal(f.pool.park(a.key), false, 'fatal current cannot be reused');
  f.engines[0].emit('ERROR', { fatal: true, details: 'stale' });
  const second = take(f, b);
  second.resume(message => errors.push(message));
  assert.equal(f.pool.park(b.key), true);
  f.engines[1].emit('ERROR', { fatal: true, details: 'background failure' });
  assert.deepEqual(errors, ['new:fragLoadError']);
  assert.equal(f.engines[1].destroyCalls, 1);
  assert.equal(take(f, b).video, null);
  f.pool.clear();
});

test('native active media errors invalidate reuse, while background errors are silent', () => {
  const f = fixture(), a = candidate('a.mp4'), b = candidate('b.mp4'), errors = [];
  const video = external(f, a, 120, null, message => errors.push(message));
  video.currentTime = 32;
  sync(f, [b], [a.key, b.key]);
  f.videos[1].dispatch('error');
  assert.equal(f.videos[1].src, '');
  assert.equal(errors.length, 0);
  video.dispatch('error');
  assert.equal(errors.length, 1);
  assert.equal(f.pool.park(a.key), false);
  assert.equal(video.src, a.url);
  assert.equal(take(f, a).position, 32);
});

test('fatal quota releases old media, degrades current to rolling config and keeps progress', async () => {
  const f = fixture(), a = candidate('old.m3u8'), b = candidate('current.m3u8');
  sync(f, [a, b]);
  await flush();
  f.engines.forEach(hls => manifest(hls, 120));
  const old = take(f, a);
  old.video.currentTime = 17;
  f.pool.park(a.key);
  const current = take(f, b), errors = [];
  current.resume(message => errors.push(message));
  f.engines[1].emit('ERROR', { fatal: true, details: 'bufferFullError', error: { name: 'QuotaExceededError' } });
  assert.equal(f.engines[0].destroyCalls, 1);
  assert.equal(f.engines[1].destroyCalls, 0);
  assert.equal(f.engines[1].stopCalls, 0);
  assert.equal(f.engines[1].config.backBufferLength, 75);
  assert.deepEqual(errors, ['bufferFullError']);
  assert.equal(f.pool.park(b.key), false);
  assert.equal(take(f, a).position, 17);
  f.pool.clear();
});

test('visibility pauses next, leaves active background playback alone, and preserves cached media', async () => {
  const f = fixture(), current = candidate('current.m3u8'), next = candidate('next.m3u8');
  sync(f, [current, next]);
  await flush();
  f.engines.forEach(hls => manifest(hls, 120));
  const active = take(f, current);
  active.video.paused = false;
  active.video.currentTime = 42;
  f.videos[0].ranges = [[0, 120]];
  f.videos[1].ranges = [[0, 30]];
  const pauses = f.engines[0].pauseBufferCalls;
  const loads = f.videos.map(video => video.loadCalls), sources = f.videos.map(video => video.src);
  const writes = f.videos.map(video => [...video.srcWrites]);
  f.pool.setVisible(false);
  assert.equal(f.engines[0].pauseBufferCalls, pauses);
  assert.equal(f.engines[1].bufferingEnabled, false);
  assert.equal(f.videos[0].pauseCalls, 0);
  assert.ok(f.engines.every(hls => hls.destroyCalls === 0 && hls.stopCalls === 0));
  assert.deepEqual(f.videos.map(video => video.ranges), [[[0, 120]], [[0, 30]]]);
  f.pool.setVisible(true);
  assert.equal(f.engines[1].bufferingEnabled, true);
  assert.equal(active.video.paused, false);
  assert.equal(active.video.currentTime, 42);
  assert.deepEqual(f.videos.map(video => video.ranges), [[[0, 120]], [[0, 30]]]);
  assert.deepEqual(f.videos.map(video => video.loadCalls), loads);
  assert.deepEqual(f.videos.map(video => video.src), sources);
  assert.deepEqual(f.videos.map(video => video.srcWrites), writes);
  assert.equal(f.pool.park(current.key), true);
  const warmed = take(f, next);
  assert.equal(warmed.video, f.videos[1]);
  assert.equal(warmed.customPlayer.hls, f.engines[1]);
  warmed.destroy();
  f.pool.clear();
});

test('visibility round trips preserve a pending HLS manifest and defer hidden completion until visible', async () => {
  const f = fixture(), item = candidate('next.m3u8');
  sync(f, [item]);
  await flush();
  const video = f.videos[0], hls = f.engines[0];
  for (const visible of [false, true, false]) {
    f.pool.setVisible(visible);
    assert.equal(hls.stopCalls, 0, 'stopLoad would abort the unresolved manifest');
    assert.equal(hls.destroyCalls, 0);
    assert.deepEqual(hls.starts, [], 'no segment loading before the manifest response');
  }
  manifest(hls, 120);
  assert.deepEqual(hls.starts, [], 'a manifest received while hidden must wait for visibility');
  assert.equal(hls.bufferingEnabled, false);
  f.pool.setVisible(true);
  assert.deepEqual(hls.starts, [-1]);
  assert.equal(hls.bufferingEnabled, true);
  assert.equal(hls.stopCalls, 0);
  assert.equal(hls.destroyCalls, 0);
  assert.equal(f.loadHlsCalls, 1);
  assert.equal(f.engines.length, 1);
  assert.equal(f.videos.length, 1);
  assert.deepEqual(video.srcWrites, ['blob:mock-media-source']);
  assert.equal(video.loadCalls, 0);
  const warmed = take(f, item);
  assert.equal(warmed.video, video);
  assert.equal(warmed.customPlayer.hls, hls);
  warmed.destroy();
  f.pool.clear();
});

test('hidden sync delays new prefetch and keeps existing native cache without source/load resets', () => {
  for (const extension of ['mp4', 'm3u8']) {
    const f = fixture({ preferNativeHls: true, nativeHls: 'probably' });
    const current = candidate(`current.${extension}`), a = candidate(`a.${extension}`), b = candidate(`b.${extension}`);
    const active = external(f, current);
    active.currentTime = 42;
    active.ranges = [[0, 120]];
    sync(f, [a], [current.key, a.key, b.key]);
    const warmed = f.videos[1];
    metadata(warmed, 120);
    warmed.ranges = [[0, 30]];
    f.pool.setVisible(false);
    assert.equal(warmed.preload, 'none');
    assert.equal(warmed.src, a.url);
    assert.equal(active.preload, 'auto');
    assert.equal(active.pauseCalls, 0);
    sync(f, [a, b], [current.key, a.key, b.key]);
    assert.equal(f.videos.length, 2);
    f.pool.setVisible(true);
    assert.equal(warmed.preload, 'auto');
    assert.equal(warmed.loadCalls, 1);
    assert.deepEqual(warmed.srcWrites, [a.url]);
    assert.deepEqual(warmed.ranges, [[0, 30]]);
    assert.equal(active.currentTime, 42);
    assert.equal(active.paused, false);
    assert.equal(active.loadCalls, 0);
    assert.deepEqual(active.srcWrites, [current.url]);
    assert.deepEqual(active.ranges, [[0, 120]]);
    assert.equal(f.videos.length, 3);
    assert.equal(f.videos[2].src, b.url);
    assert.equal(f.videos[2].preload, 'metadata');
    assert.equal(f.videos[2].loadCalls, 1);
    assert.equal(f.loadHlsCalls, 0, 'Safari native HLS does not import hls.js');
    f.pool.clear();
  }
});

test('initial hidden sync creates no native/HLS preload until visible, without needing another sync', async () => {
  const f = fixture(), native = candidate('next.mp4'), hls = candidate('next.m3u8');
  f.pool.setVisible(false);
  sync(f, [native, hls]);
  await flush();
  assert.equal(f.videos.length, 0, 'initial hidden state must precede the first sync');
  assert.equal(f.loadHlsCalls, 0);
  f.pool.setVisible(true);
  f.pool.setVisible(true);
  await flush();
  assert.equal(f.videos.length, 2, 'repeated visibility must not duplicate pending preloads');
  assert.equal(f.videos[0].src, native.url);
  assert.equal(f.videos[0].preload, 'metadata');
  assert.equal(f.videos[0].loadCalls, 1);
  assert.equal(f.loadHlsCalls, 1);
  assert.equal(f.engines.length, 1);
  assert.equal(f.engines[0].url, hls.url);
  assert.equal(f.engines[0].video, f.videos[1]);
  assert.ok(f.videos.every(video => video.playCalls === 0));
  f.pool.clear();
});

test('clear destroys only nonactive media, removes all observers/maps, and resets failed identities', async () => {
  const f = fixture(), a = candidate('a.m3u8'), b = candidate('b.m3u8');
  sync(f, [a, b]);
  await flush();
  f.engines.forEach(hls => manifest(hls, 120));
  const active = take(f, a), hls = f.engines[0], video = f.videos[0], xhr = new Xhr();
  active.resume(() => assert.fail('clear must remove the old error callback'));
  hls.config.xhrSetup(xhr, a.url);
  const loads = video.loadCalls;
  f.pool.clear();
  f.pool.clear();
  assert.equal(hls.destroyCalls, 0);
  assert.equal(f.engines[1].destroyCalls, 1);
  assert.equal(hls.listenerCount + video.listenerCount + xhr.listenerCount, 0);
  assert.equal(video.loadCalls, loads);
  assert.equal(video.src, 'blob:mock-media-source');
  assert.equal(take(f, a), null);
  active.destroy();
  active.resume();
  hls.emit('ERROR', { fatal: true });
  assert.equal(hls.destroyCalls, 0, 'stale handles cannot destroy relinquished externally owned media');
  sync(f, [a]);
  await flush();
  assert.equal(f.engines.length, 3);
  f.pool.clear();
});

test('late HLS imports cannot attach after clear, pruning, unready take, or replacing with an external node', async () => {
  for (const cancel of ['clear', 'sync', 'take', 'adopt']) {
    const pending = deferred(), f = fixture({ loadHls: () => pending.promise });
    const item = candidate('a.m3u8');
    sync(f, [item]);
    if (cancel === 'clear') f.pool.clear();
    if (cancel === 'sync') sync(f, [], []);
    if (cancel === 'take') assert.equal(take(f, item), null);
    if (cancel === 'adopt') external(f, item);
    pending.resolve(f.Hls);
    await flush();
    assert.equal(f.engines.length, 0, cancel);
    assert.equal(f.videos[0].listenerCount, 0);
    assert.equal(f.videos[0].src, '');
    f.pool.clear();
  }
});

test('adopt of the same pending node prevents a late helper loader from replacing the external engine', async () => {
  const pending = deferred(), f = fixture({ loadHls: () => pending.promise });
  const item = candidate('a.m3u8');
  sync(f, [item]);
  const video = f.videos[0], hls = new f.Hls({ ...NORMAL_BUFFER });
  hls.attachMedia(video);
  f.pool.adopt({ ...item, video, customPlayer: { hls } });
  pending.resolve(f.Hls);
  await flush();
  assert.equal(f.engines.length, 1);
  assert.equal(take(f, item).customPlayer.hls, hls);
  f.pool.clear();
});

test('same-key replacements use object identity against late resolution and rejection', async () => {
  for (const staleResult of ['resolve-first', 'resolve-last', 'reject']) {
    const old = deferred(), next = deferred();
    let calls = 0;
    const f = fixture({ loadHls: () => (++calls === 1 ? old.promise : next.promise) });
    const item = candidate('a.m3u8');
    sync(f, [item]);
    f.pool.clear();
    sync(f, [item]);
    if (staleResult === 'resolve-first') { old.resolve(f.Hls); await flush(); assert.equal(f.engines.length, 0); }
    next.resolve(f.Hls);
    await flush();
    if (staleResult === 'resolve-last') old.resolve(f.Hls);
    if (staleResult === 'reject') old.reject(new Error('stale import'));
    await flush();
    assert.equal(f.engines.length, 1);
    assert.equal(f.engines[0].video, f.videos[1]);
    assert.equal(f.engines[0].destroyCalls, 0);
    f.pool.clear();
  }
});

test('failed candidates are deduplicated by key+source until pruned, refreshed, or cleared', async () => {
  for (const failure of ['media', 'fatal', 'live', 'loadHls', 'constructor', 'loadSource', 'attachMedia']) {
    const f = fixture({ loadHls: failure === 'loadHls' ? () => Promise.reject(new Error('chunk failed')) : undefined });
    f.Hls.failure = failure;
    const item = candidate('a.m3u8', { Authorization: 'expired' });
    sync(f, [item]);
    await flush();
    if (failure === 'media') f.videos[0].dispatch('error');
    if (failure === 'fatal') f.engines[0].emit('ERROR', { fatal: true });
    if (failure === 'live') manifest(f.engines[0], 120, true);
    for (let i = 0; i < 3; i++) sync(f, [item]);
    await flush();
    assert.equal(f.loadHlsCalls, 1, failure);
    assert.equal(take(f, item), null, failure);
    assert.equal(f.host.children.length, 0);
    assert.ok(f.engines.every(hls => hls.destroyCalls === 1));
    if (failure === 'loadSource') assert.equal(f.engines[0].video, undefined);
    sync(f, [{ ...item, headers: { Authorization: 'fresh' } }]);
    await flush();
    assert.equal(f.loadHlsCalls, 2);
    sync(f, [], []);
    sync(f, [item]);
    await flush();
    assert.equal(f.loadHlsCalls, 3);
    f.pool.clear();
    sync(f, [item]);
    await flush();
    assert.equal(f.loadHlsCalls, 4);
    f.pool.clear();
  }
});

test('nonfatal HLS errors do not discard playable buffers', async () => {
  const f = fixture(), item = candidate('a.m3u8');
  sync(f, [item]);
  await flush();
  f.engines[0].emit('ERROR', { fatal: false, details: 'fragLoadError' });
  assert.equal(f.engines[0].destroyCalls, 0);
  assert.equal(take(f, item).video, f.videos[0]);
  f.pool.clear();
});

test('detach relinquishes a parked native node so later pruning cannot wipe its new source', () => {
  const f = fixture(), a = candidate('a.mp4'), b = candidate('b.mp4');
  const video = external(f, a);
  video.currentTime = 24;
  f.pool.park(a.key);
  f.pool.detach(video);
  video.src = b.url;
  f.pool.adopt({ ...b, video });
  sync(f, [], [b.key]);
  assert.equal(video.src, b.url);
  assert.equal(video.loadCalls, 0);
  assert.equal(take(f, a), null);
  f.pool.clear();
  assert.equal(video.src, b.url);
});
