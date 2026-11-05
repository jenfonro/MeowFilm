import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// These are desktop simulations of backend selection, not iOS device evidence.
// Only this trusted preload changes the platform; Workers inherit --import.
function withoutWebAssembly(platform, run) {
  const directory = mkdtempSync(join(tmpdir(), 'meowfilm-quickjs-backend-'));
  try {
    const preload = `
      import { isMainThread, threadId } from 'node:worker_threads';
      Object.defineProperty(process, 'platform', {
        value: ${JSON.stringify(platform)}, configurable: false, writable: false,
      });
      Object.defineProperty(globalThis, 'WebAssembly', {
        value: undefined, configurable: false, writable: false,
      });
      console.log('BACKEND_PRELOAD ' + JSON.stringify({
        isMainThread, threadId, platform: process.platform, wasm: typeof WebAssembly,
      }));
    `;
    const program = join(directory, 'scenario.mjs');
    writeFileSync(program, `
      import assert from 'node:assert/strict';
      import { QuickJSEngine } from ${JSON.stringify(new URL('../src/quickjs-engine.js', import.meta.url).href)};
      import { createScriptHost, SCRIPT_LIMITS } from ${JSON.stringify(new URL('../src/index.js', import.meta.url).href)};
      import { lxScript, target } from ${JSON.stringify(new URL('./fixtures.js', import.meta.url).href)};
      assert.equal(process.platform, ${JSON.stringify(platform)});
      assert.equal(typeof WebAssembly, 'undefined');
      await (${run})();
      console.log('BACKEND_COMPLETE');
    `);
    const result = spawnSync(process.execPath,
      ['--import', `data:text/javascript,${encodeURIComponent(preload)}`, program], {
        env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
        encoding: 'utf8', windowsHide: true, timeout: 30000,
        killSignal: 'SIGKILL', maxBuffer: 1024 * 1024,
      });
    const detail = JSON.stringify({
      status: result.status, signal: result.signal,
      error: result.error && { code: result.error.code, message: result.error.message },
      stdout: result.stdout, stderr: result.stderr,
    }, null, 2);
    assert.equal(result.error, undefined, detail);
    assert.equal(result.signal, null, detail);
    assert.equal(result.status, 0, detail);
    assert.ok(result.stdout.split('\n').includes('BACKEND_COMPLETE'), detail);
    const preloads = result.stdout.split('\n')
      .filter(line => line.startsWith('BACKEND_PRELOAD '))
      .map(line => JSON.parse(line.slice('BACKEND_PRELOAD '.length)));
    assert.ok(preloads.some(value => value.isMainThread && value.threadId === 0), detail);
    for (const value of preloads) {
      assert.equal(value.platform, platform, detail);
      assert.equal(value.wasm, 'undefined', detail);
    }
    return preloads;
  } finally {
    // spawnSync has reaped the child (including its Workers) before removal.
    rmSync(directory, { recursive: true, force: true });
  }
}

test('simulated iOS without WebAssembly runs isolated heaps, Promise/BigInt and bytes after 20 MiB growth', () => {
  withoutWebAssembly('ios', async () => {
    const source = `(() => {
      const integer = String(BigInt.asUintN(128, -1n));
      return {
        errorCode() { return 'SCRIPT_EXCEPTION'; },
        watchStartup() {},
        inspect() {
          const blocked = [];
          for (const run of [
            () => Function('return process')(),
            () => globalThis.constructor.constructor('return process')(),
            () => __meowBridge.constructor('return require')(),
          ]) {
            try { run(); blocked.push(false); } catch { blocked.push(true); }
          }
          return JSON.stringify({
            integer, host: [typeof process, typeof require], blocked,
            guestFunction: Function('return 6 * 7')(),
            mark: globalThis.backendMarker || null,
            prototype: Object.prototype.backendMarker || null,
            jobs: globalThis.jobs || [],
            retained: globalThis.retained
              ? [retained.length, retained[0], retained[retained.length - 1]] : null,
          });
        },
        exchange(input, frame) {
          const reply = __meowBridge('bytes', input, frame);
          return [JSON.stringify(JSON.parse(reply[0])), reply[1]];
        },
      };
    })()`;
    const metadata = { label: '中文🎵', empty: null };
    const bytes = Buffer.from(Array.from({ length: 256 }, (_, index) => index));
    const engines = [];
    const expected = {
      integer: String((1n << 128n) - 1n),
      host: ['undefined', 'undefined'], blocked: [true, true, true],
      guestFunction: 42, mark: null, prototype: null, jobs: [], retained: null,
    };
    function exchange(engine) {
      const backing = Buffer.concat([Buffer.from([71]), bytes, Buffer.from([83])]);
      const input = backing.subarray(1, -1);
      const result = engine.callJSONFrames('exchange', [JSON.stringify(metadata)], [input]);
      assert.deepEqual(result, {
        value: { ok: true, value: metadata }, frames: [bytes],
      });
      assert.deepEqual(input, Buffer.alloc(bytes.length));
      assert.equal(backing[0], 71);
      assert.equal(backing[backing.length - 1], 83);
      return result;
    }
    try {
      for (let index = 0; index < 2; index++) engines.push(await QuickJSEngine.create({
        source, limits: SCRIPT_LIMITS,
        bridge(operation, value, frames, output) {
          assert.equal(operation, 'bytes');
          assert.deepEqual(value, metadata);
          assert.deepEqual(frames, [bytes]);
          output.push(Buffer.from(frames[0]));
          return value;
        },
      }));
      const [first, second] = engines;
      assert.deepEqual(first.callJSON('inspect', []), expected);
      assert.deepEqual(second.callJSON('inspect', []), expected);
      const beforeGrowth = exchange(first);
      first.evaluate(`
        globalThis.backendMarker = 'first';
        Object.prototype.backendMarker = 'first';
        globalThis.retained = new Uint8Array(20 * 1024 * 1024);
        retained[0] = 128;
        retained[retained.length - 1] = 255;
        globalThis.jobs = [];
        Promise.resolve().then(() => jobs.push(1)).then(() => jobs.push(2));
      `);
      assert.deepEqual(first.callJSON('inspect', []), {
        ...expected, mark: 'first', prototype: 'first', jobs: [1, 2],
        retained: [20 * 1024 * 1024, 128, 255],
      });
      assert.ok(first.memoryUsage().heapUsed >= 20 * 1024 * 1024);
      assert.ok(second.memoryUsage().heapUsed < 20 * 1024 * 1024);
      assert.deepEqual(exchange(first), beforeGrowth);
      first.close();
      assert.deepEqual(second.callJSON('inspect', []), expected);
      assert.deepEqual(exchange(second), beforeGrowth);
      assert.equal(Object.prototype.backendMarker, undefined);
      assert.equal(globalThis.backendMarker, undefined);
    } finally {
      try { engines[0]?.close(); } finally { engines[1]?.close(); }
    }
  });
});

test('simulated iOS without WebAssembly installs, invokes, disables and closes real LX Workers', () => {
  const preloads = withoutWebAssembly('ios', async () => {
    const host = createScriptHost();
    const source = lxScript({
      setup: 'let calls = 0;',
      handler: `async () => ({
        count: ++calls,
        integer: String(await Promise.resolve(BigInt.asUintN(64, -1n))),
        host: [typeof process, typeof require, Function('return typeof process')()],
      })`,
    });
    const expected = {
      count: 1, integer: String((1n << 64n) - 1n),
      host: ['undefined', 'undefined', 'undefined'],
    };
    try {
      const installed = await host.install({ installationId: 'a', enabled: true, source });
      assert.equal(installed.state, 'ready');
      assert.equal(host.stats().liveSessions, 1);
      assert.deepEqual(await host.invoke(target()), expected);
      assert.deepEqual(await host.invoke(target()), { ...expected, count: 2 });
      assert.equal((await host.disable('a')).state, 'disabled');
      assert.equal(host.stats().liveSessions, 0);
      await assert.rejects(host.invoke(target()), { code: 'DISABLED' });
      const restarted = await host.enable('a');
      assert.equal(restarted.state, 'ready');
      assert.ok(restarted.generation > installed.generation);
      assert.deepEqual(await host.invoke(target()), expected);
    } finally {
      assert.equal((await host.close()).cleanupComplete, true);
      assert.equal(host.stats().liveSessions, 0);
      assert.equal(host.stats().activeRequests, 0);
    }
  });
  assert.ok(preloads.some(value => !value.isMainThread && value.threadId > 0),
    'the actual LX Worker must inherit the iOS/no-WebAssembly preload');
});

test('non-iOS without WebAssembly rejects initialization instead of falling back to asmjs', () => {
  withoutWebAssembly('linux', async () => {
    let unexpectedEngine;
    try {
      await assert.rejects(async () => {
        unexpectedEngine = await QuickJSEngine.create({
          source: '({ errorCode: () => "SCRIPT_EXCEPTION", watchStartup() {} })',
          bridge: () => null, limits: SCRIPT_LIMITS,
        });
      });
    } finally { unexpectedEngine?.close(); }
    const host = createScriptHost();
    try {
      await assert.rejects(host.install({
        installationId: 'a', enabled: true, source: lxScript(),
      }));
      assert.notEqual(host.get('a').state, 'ready');
      assert.equal(host.stats().liveSessions, 0);
    } finally {
      assert.equal((await host.close()).cleanupComplete, true);
      assert.equal(host.stats().liveSessions, 0);
    }
  });
});
