import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

function withoutWebAssembly(run) {
  const directory = mkdtempSync(join(tmpdir(), 'meowfilm-native-backend-'));
  try {
    const preload = `
      import { isMainThread, threadId } from 'node:worker_threads';
      Object.defineProperty(globalThis, 'WebAssembly', {
        value: undefined, configurable: false, writable: false,
      });
      console.log('BACKEND_PRELOAD ' + JSON.stringify({ isMainThread, threadId }));
    `;
    const program = join(directory, 'scenario.mjs');
    writeFileSync(program, `
      import assert from 'node:assert/strict';
      import { NativeEngine } from ${JSON.stringify(new URL('../src/native-engine.js', import.meta.url).href)};
      import { createScriptHost } from ${JSON.stringify(new URL('../src/index.js', import.meta.url).href)};
      import { lxScript, target } from ${JSON.stringify(new URL('./fixtures.js', import.meta.url).href)};
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
    assert.deepEqual(preloads, [{ isMainThread: true, threadId: 0 }], detail);
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

test('native QuickJS without WebAssembly preserves isolated heaps, Promise/BigInt and bytes after 20 MiB growth', () => {
  withoutWebAssembly(async () => {
    const source = `(() => {
      const integer = String(BigInt.asUintN(128, -1n));
      const host = __meowHost;
      for (const key of ['__meowHost','__meowEmit','__meowNative','__meowTaskContext','__meowGuest','__meowCore'])
        delete globalThis[key];
      return {
        async grow() {
          globalThis.backendMarker = 'first';
          Object.prototype.backendMarker = 'first';
          globalThis.retained = new Uint8Array(20 * 1024 * 1024);
          retained[0] = 128; retained[retained.length - 1] = 255;
          globalThis.jobs = [];
          await Promise.resolve().then(() => jobs.push(1)).then(() => jobs.push(2));
          return null;
        },
        inspect() {
          const blocked = [];
          for (const run of [
            () => Function('return process')(),
            () => globalThis.constructor.constructor('return process')(),
            () => __meowHost.constructor('return require')(),
          ]) {
            try { run(); blocked.push(false); } catch { blocked.push(true); }
          }
          return {
            integer, host: [typeof process, typeof require], blocked,
            guestFunction: Function('return 6 * 7')(),
            mark: globalThis.backendMarker || null,
            prototype: Object.prototype.backendMarker || null,
            jobs: globalThis.jobs || [],
            retained: globalThis.retained
              ? [retained.length, retained[0], retained[retained.length - 1]] : null,
          };
        },
        exchange(value) { return host('bytes',value); },
      };
    })()`;
    const metadata = { label: '中文🎵\0', empty: null };
    const bytes = Buffer.from(Array.from({ length: 256 }, (_, index) => index));
    const engines = [];
    const expected = {
      integer: String((1n << 128n) - 1n),
      host: ['undefined', 'undefined'], blocked: [true, true, true],
      guestFunction: 42, mark: null, prototype: null, jobs: [], retained: null,
    };
    async function exchange(engine) {
      const backing = Buffer.concat([Buffer.from([71]), bytes, Buffer.from([83])]);
      const input = backing.subarray(1, -1);
      const pending = engine.call('exchange', [{ metadata, bytes: input }]);
      assert.deepEqual(input, bytes, 'admission must not destroy caller-owned input');
      input.fill(0);
      const result = await pending;
      assert.equal(Object.getPrototypeOf(result), Object.prototype);
      assert.deepEqual({ ...result.metadata }, metadata);
      assert.deepEqual(result.bytes, bytes);
      assert.equal(backing[0], 71);
      assert.equal(backing[backing.length - 1], 83);
      return result;
    }
    try {
      for (let index = 0; index < 2; index++) {
        const engine = NativeEngine.create({ source,
          onHostCall(operation, value) {
            assert.equal(operation, 'bytes');
            assert.equal(Object.getPrototypeOf(value), Object.prototype);
            assert.deepEqual({ ...value.metadata }, metadata);
            assert.deepEqual(value.bytes, bytes);
            return value;
          },
        });
        engines.push(engine);
        await engine.ready;
      }
      const [first, second] = engines;
      assert.deepEqual({ ...await first.call('inspect') }, expected);
      assert.deepEqual({ ...await second.call('inspect') }, expected);
      const beforeGrowth = await exchange(first);
      await first.call('grow');
      assert.deepEqual({ ...await first.call('inspect') }, {
        ...expected, mark: 'first', prototype: 'first', jobs: [1, 2],
        retained: [20 * 1024 * 1024, 128, 255],
      });
      assert.ok((await first.stats({ refresh: true })).heapBytes >= 20 * 1024 * 1024);
      assert.ok((await second.stats({ refresh: true })).heapBytes < 20 * 1024 * 1024);
      assert.deepEqual(await exchange(first), beforeGrowth);
      await first.stop();
      assert.equal(first.stats().threadCount, 0);
      assert.deepEqual({ ...await second.call('inspect') }, expected);
      assert.deepEqual(await exchange(second), beforeGrowth);
      assert.equal(Object.prototype.backendMarker, undefined);
      assert.equal(globalThis.backendMarker, undefined);
    } finally { await Promise.all(engines.map(engine => engine.stop())); }
  });
});

test('LX installs, invokes, disables and closes native owners without WebAssembly or Node Workers', () => {
  withoutWebAssembly(async () => {
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
      assert.equal(installed.resources.runtimeCount, 1);
      assert.equal(installed.resources.contextCount, 1);
      assert.equal(installed.resources.threadCount, 1);
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
});
