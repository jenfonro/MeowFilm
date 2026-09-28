const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const source = readFileSync('src/shared/ArtPlayer.vue', 'utf8');
const pipCode = source.slice(source.indexOf('const togglePip ='), source.indexOf('const syncMediaMetadata ='));
const mediaCode = source.slice(source.indexOf('const syncMediaMetadata ='), source.indexOf('watch(() => [props.title'));

function pipContext(video, document = {}) {
  const context = vm.createContext({
    art: { video, notice: {} }, document, showUiTemporarily() {},
  });
  vm.runInContext(`${pipCode}\nthis.toggle = togglePip;`, context);
  return context;
}

test('Safari enters and exits native picture-in-picture', async () => {
  const video = {
    webkitPresentationMode: 'inline',
    webkitSupportsPresentationMode: () => true,
    webkitSetPresentationMode(mode) { this.webkitPresentationMode = mode; },
  };
  const context = pipContext(video);
  await context.toggle();
  assert.equal(video.webkitPresentationMode, 'picture-in-picture');
  await context.toggle();
  assert.equal(video.webkitPresentationMode, 'inline');
});

test('standard PiP enters, exits, and reports rejected requests', async () => {
  let entered = false;
  let exited = false;
  const video = { requestPictureInPicture: async () => { entered = true; } };
  const document = { exitPictureInPicture: async () => { exited = true; } };
  const context = pipContext(video, document);
  await context.toggle();
  assert.equal(entered, true);
  document.pictureInPictureElement = video;
  await context.toggle();
  assert.equal(exited, true);
  document.pictureInPictureElement = null;
  video.requestPictureInPicture = async () => { throw new Error('NotAllowedError'); };
  await context.toggle();
  assert.match(context.art.notice.show, /先播放/);
});

test('unsupported PiP gives feedback', async () => {
  const context = pipContext({});
  await context.toggle();
  assert.match(context.art.notice.show, /Safari/);
});

test('media session controls, position, metadata and teardown', () => {
  const handlers = {};
  const listeners = new Map();
  const session = {
    setActionHandler(action, handler) {
      if (action === 'seekforward') throw new Error('unsupported action');
      handlers[action] = handler;
    },
    setPositionState(state) { this.position = state; },
  };
  const video = {
    duration: 100, currentTime: 25, playbackRate: 1, paused: false,
    play() { this.paused = false; return Promise.resolve(); },
    pause() { this.paused = true; },
    addEventListener(event, callback) { listeners.set(event, callback); },
    removeEventListener(event) { listeners.delete(event); },
  };
  const context = vm.createContext({
    art: { video, notice: {} }, navigator: { mediaSession: session },
    props: { title: '测试影片', poster: '/poster.jpg' },
    window: { location: { href: 'https://example.com/play' } }, URL,
    MediaMetadata: class { constructor(data) { Object.assign(this, data); } },
    cleanupMediaSession: null,
  });
  vm.runInContext(`${mediaCode}\nbindMediaSession(art.video);`, context);
  assert.equal(session.metadata.title, '测试影片');
  assert.equal(session.metadata.artwork[0].src, 'https://example.com/poster.jpg');
  assert.equal(session.playbackState, 'playing');
  assert.equal(session.position.position, 25);
  handlers.pause();
  listeners.get('pause')();
  assert.equal(session.playbackState, 'paused');
  handlers.play();
  assert.equal(video.paused, false);
  handlers.seekbackward({});
  assert.equal(video.currentTime, 15);
  handlers.seekto({ seekTime: 200 });
  assert.equal(video.currentTime, 100);
  video.duration = Infinity;
  listeners.get('durationchange')();
  assert.equal(session.position, undefined);
  context.cleanupMediaSession();
  assert.equal(listeners.size, 0);
  assert.equal(session.metadata, null);
  assert.equal(session.playbackState, 'none');
  assert.ok(Object.values(handlers).every((handler) => handler === null));
});

test('iPhone native HLS preference does not bypass custom headers', async () => {
  const start = source.indexOf('async hls(videoEl, url, headers) {');
  const end = source.indexOf('const Hls = await loadHls();', start);
  const code = source.slice(start, end).replace('async hls(', 'async function hls(') + '\nreturn { fallback: true }; }';
  const context = vm.createContext({ isIos: { value: true } });
  vm.runInContext(`${code}\nthis.hls = hls;`, context);
  const video = { canPlayType: () => 'maybe', load() {} };
  assert.equal((await context.hls(video, 'stream.m3u8', {})).direct, true);
  assert.equal(video.src, 'stream.m3u8');
  assert.equal((await context.hls(video, 'private.m3u8', { Authorization: 'token' })).fallback, true);
  assert.equal(video.src, 'stream.m3u8');
  context.isIos.value = false;
  assert.equal((await context.hls(video, 'stream.m3u8', {})).fallback, true);
});
