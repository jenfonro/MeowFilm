const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const postcss = require('postcss');

const source = readFileSync(join(__dirname, '../src/shared/ArtPlayer.vue'), 'utf8');

function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, `source fixture boundary changed: ${start}`);
  return source.slice(from, to);
}

function mediaErrorContext(error) {
  const instance = { video: { error }, isDestroy: false };
  const emitted = [];
  const bufferingCalls = [];
  const context = vm.createContext({
    instance, art: instance, currentMediaInvalid: false, currentVideoReusable: true,
    uiVisible: { value: false }, emitted, bufferingCalls,
    emit(name, data) { emitted.push([name, { ...data }]); },
    setBuffering(next) { bufferingCalls.push(next); },
  });
  vm.runInContext(
    section('let lastMediaErrorEvent = null;', 'art = new Artplayer(') +
    '\nthis.onMediaError = onMediaError;',
    context,
  );
  return context;
}

for (const [code, summary] of [
  [1, '播放已中止'],
  [2, '播放失败：网络错误'],
  [3, '播放失败：媒体解码错误'],
  [4, '播放失败：媒体源不可用或格式不受支持'],
  [99, '播放失败'],
]) {
  test(`media error code ${code} is preserved even when the browser message contains 403`, () => {
    const originalMessage = 'HTTP 403 Forbidden';
    const context = mediaErrorContext({ code, message: originalMessage });
    context.onMediaError({ type: 'error' });
    assert.deepEqual(context.emitted, [['error', {
      code, message: `${summary}：${originalMessage}`, originalMessage,
    }]]);
    assert.equal(context.currentMediaInvalid, true);
    assert.equal(context.currentVideoReusable, false);
    assert.deepEqual(context.bufferingCalls, [false]);
    assert.equal(context.uiVisible.value, true);
  });
}

test('missing or blank browser messages and empty media errors retain the summary without a suffix', () => {
  for (const [error, code, message, originalMessage] of [
    [{ code: 2 }, 2, '播放失败：网络错误', ''],
    [{ code: 2, message: '' }, 2, '播放失败：网络错误', ''],
    [{ code: 2, message: ' \t\r\n ' }, 2, '播放失败：网络错误', ' \t\r\n '],
    [null, 0, '播放失败', ''],
    [undefined, 0, '播放失败', ''],
    [{}, 0, '播放失败', ''],
  ]) {
    const context = mediaErrorContext(error);
    context.onMediaError({ type: 'error' });
    assert.deepEqual(context.emitted, [['error', { code, message, originalMessage }]]);
  }
});

test('non-enumerable MediaError getters preserve original whitespace and trim only the display detail', () => {
  const originalMessage = ' \tPIPELINE_ERROR_DECODE\n  decoder failed\r\n ';
  class MediaError {
    get code() { return 3; }
    get message() { return originalMessage; }
  }
  const context = mediaErrorContext(new MediaError());
  assert.deepEqual(Object.keys(context.instance.video.error), []);
  context.onMediaError({ type: 'error', code: 4, message: 'not the video MediaError' });
  assert.deepEqual(context.emitted, [['error', {
    code: 3,
    message: '播放失败：媒体解码错误：PIPELINE_ERROR_DECODE\n  decoder failed',
    originalMessage,
  }]]);
});

test('the same native event forwarded by ArtPlayer emits once, while distinct events still report', () => {
  const context = mediaErrorContext({ code: 4, message: 'Unsupported source' });
  const firstEvent = { type: 'error' };
  // Both entry points bind this exact handler (checked below).
  context.onMediaError(firstEvent);
  context.onMediaError(firstEvent);
  assert.equal(context.emitted.length, 1);

  const nextEvent = { type: 'error' };
  context.onMediaError(nextEvent);
  context.onMediaError(nextEvent);
  assert.equal(context.emitted.length, 2);
  assert.deepEqual(context.emitted[1], context.emitted[0]);
});

test('calls without an event are not deduplicated', () => {
  const context = mediaErrorContext({ code: 1 });
  for (const event of [undefined, undefined, null, null]) context.onMediaError(event);
  assert.equal(context.emitted.length, 4);
});

test('stale, destroyed or missing instances cannot pollute current playback state', () => {
  for (const state of ['replaced', 'destroyed', 'cleared', 'uninitialized']) {
    const context = mediaErrorContext({ code: 3, message: 'stale error' });
    // A replacement may reuse the same video element, but not the old handler.
    if (state === 'replaced') context.art = { video: context.instance.video, isDestroy: false };
    if (state === 'destroyed') context.instance.isDestroy = true;
    if (state === 'cleared') context.art = null;
    if (state === 'uninitialized') context.instance = null;
    context.onMediaError({ type: 'error' });
    assert.deepEqual(context.emitted, [], state);
    assert.deepEqual(context.bufferingCalls, [], state);
    assert.equal(context.currentMediaInvalid, false, state);
    assert.equal(context.currentVideoReusable, true, state);
    assert.equal(context.uiVisible.value, false, state);
  }
});

test('native and ArtPlayer error listeners and native cleanup bind the same handler', () => {
  const init = section('const init = () => {', 'const restoreShortVideoRate = () => {');
  assert.match(init, /\bv\.addEventListener\(\s*['"]error['"]\s*,\s*onMediaError\s*\)/);
  assert.match(init, /\bart\.on\(\s*['"]video:error['"]\s*,\s*onMediaError\s*\)/);
  assert.equal((init.match(/\bv\.addEventListener\(\s*['"]error['"]/g) || []).length, 1);
  assert.equal((init.match(/\bart\.on\(\s*['"]video:error['"]/g) || []).length, 1);
  const cleanup = section('cleanupNativeVideoListeners = () => {', '// Ensure UI auto-hide');
  assert.match(cleanup, /\bv\.removeEventListener\(\s*['"]error['"]\s*,\s*onMediaError\s*\)/);
});

test('error-only overlays wrap, scroll and accept interaction outside media queries', () => {
  const page = readFileSync(join(__dirname, '../src/pages/play/PlayPage.vue'), 'utf8');
  const styles = postcss.parse(page.match(/<style\b[^>]*>([\s\S]*?)<\/style>/)[1]);
  for (const [target, properties] of [
    ['panel', {
      'min-height': '0', 'max-height': '100%', 'justify-content': 'flex-start',
      overflow: 'auto', 'pointer-events': 'auto',
    }],
    ['statusText', { 'overflow-wrap': 'anywhere', 'white-space': 'pre-wrap' }],
  ]) {
    const base = `.play-player-overlay__${target}`;
    const selector = `.play-player-overlay--error ${base}`;
    const rule = styles.nodes.find(node => node.type === 'rule' && node.selector === selector);
    assert.ok(rule, `${selector} must be a top-level error-only rule, not a mobile override`);
    const declarations = Object.fromEntries(
      rule.nodes.filter(node => node.type === 'decl').map(node => [node.prop, node.value]),
    );
    for (const [property, value] of Object.entries(properties)) {
      assert.equal(declarations[property], value, `${selector}: ${property}`);
    }
    styles.walkRules(loading => {
      if (!loading.selectors.includes(base)) return;
      loading.walkDecls(decl => assert.notEqual(decl.value, properties[decl.prop],
        `${base} must retain ordinary loading styles: ${decl.prop}`));
    });
  }
  const overlay = styles.nodes.find(node => node.type === 'rule' && node.selector === '.play-player-overlay');
  assert.equal(overlay.nodes.find(node => node.prop === 'pointer-events').value, 'none');
});
