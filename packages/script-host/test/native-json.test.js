import assert from 'node:assert/strict';
import test from 'node:test';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { NativeEngine } from '../src/native-engine.js';

const timeout = 15_000;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function program(t, source) {
  const owner = new NativeJsonProgram(source);
  t.after(() => owner.close());
  return owner;
}

function observeOwners(t) {
  const create = NativeEngine.create, owners = [];
  t.mock.method(NativeEngine, 'create', options => {
    const engine = create(options);
    owners.push(engine);
    return engine;
  });
  return owners;
}

async function entered(engine) {
  const end = Date.now() + 3_000;
  while (!engine.stats().activeCalls) {
    assert.ok(Date.now() < end, 'the native invocation actually starts');
    await sleep(2);
  }
  await sleep(20);
}

function assertReleased(engine) {
  const stats = engine.stats();
  assert.equal(stats.stopped, true, 'settlement waits for the native thread join');
  for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
    'hostCalls', 'timers', 'jobsPending', 'heapBytes', 'byteBuffers', 'byteBufferBytes']) {
    assert.equal(stats[key], 0, key);
  }
}

test('JSON boundary preserves deep data, all UTF16 keys and values, JSON numbers and complete results',
  { timeout }, async t => {
    const owner = program(t, 'globalThis.meowfilmProcess = value => value;');
    let value = { '\ud800\0key': '\udfff😀\0中文', '\udfff': '\ud800',
      large: 1e100, small: 1e-100, list: Array.from({ length: 10_001 }, (_, i) => i),
      text: '中文\0😀'.repeat(10_001) };
    for (let i = 0; i < 96; i++) value = { child: value };
    const pending = owner.invoke(value);
    const expected = JSON.parse(JSON.stringify(value));
    value.child = null;
    assert.deepEqual(await pending, expected, 'admission snapshots the complete JSON text');
    for (const scalar of [null, false, 0, 'a\ud800b\udfff', 1e100]) {
      assert.equal(await owner.invoke(scalar), scalar);
    }
    const created = program(t, `globalThis.meowfilmProcess = () =>
      ({ ['\\udfff']: '\\ud800', parts: Array(96).fill('\\ud800\\0中文') });`);
    assert.deepEqual(await created.invoke(null), {
      '\udfff': '\ud800', parts: Array(96).fill('\ud800\0中文'),
    });
  });

test('JSON programs and scripts have independent heaps while each program serializes shared state',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const source = `let count = 0; globalThis.meowfilmProcess = async value => {
      const n = ++count;
      if (value.wait) {
        const end = Date.now() + 150;
        while (Date.now() < end) {}
        await Promise.resolve();
      }
      return { n, value, hooks: typeof __meowHost, node: typeof process,
        timer: typeof setTimeout };
    };`;
    const owner = program(t, source), other = program(t, source);
    const script = NativeEngine.create({
      source: '(() => { let count = 0; return { next() { return ++count; } }; })()',
    });
    t.after(() => script.stop());
    await script.ready;
    const output = await Promise.all([
      owner.invoke({ wait: true }), owner.invoke({ id: 2 }), owner.invoke({ id: 3 }),
    ]);
    assert.deepEqual(output, [
      { n: 1, value: { wait: true }, hooks: 'undefined', node: 'undefined', timer: 'undefined' },
      { n: 2, value: { id: 2 }, hooks: 'undefined', node: 'undefined', timer: 'undefined' },
      { n: 3, value: { id: 3 }, hooks: 'undefined', node: 'undefined', timer: 'undefined' },
    ]);
    assert.equal((await other.invoke({})).n, 1);
    assert.equal(await script.call('next'), 1);
    assert.equal(owners.length, 3);
    for (const engine of owners) {
      assert.equal(engine.stats().runtimeCount, 1);
      assert.equal(engine.stats().contextCount, 1);
      assert.equal(engine.stats().threadCount, 1);
    }
  });

test('queued cancellation settles immediately and retains every other admitted request',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const owner = program(t, `let count = 0; globalThis.meowfilmProcess = value => {
      if (value.wait) { const end = Date.now() + 300; while (Date.now() < end) {} }
      return { n: ++count, value };
    };`);
    const first = owner.invoke({ wait: true });
    const controller = new AbortController();
    const cancelled = assert.rejects(owner.invoke({ removed: true }, { signal: controller.signal }),
      { code: 'CANCELLED' });
    const third = owner.invoke({ id: 3 }), fourth = owner.invoke({ id: 4 });
    await entered(owners[0]);
    controller.abort();
    await cancelled;
    assert.deepEqual(await first, { n: 1, value: { wait: true } });
    assert.deepEqual(await third, { n: 2, value: { id: 3 } });
    assert.deepEqual(await fourth, { n: 3, value: { id: 4 } });
    assert.equal(owners.length, 1, 'a queued abort does not reset the active program');
    await assert.rejects(owner.invoke(null, { signal: controller.signal }), { code: 'CANCELLED' });
  });

for (const ending of ['abort', 'timeout']) for (const phase of ['loop', 'regexp', 'jobs', 'pending']) {
  test(`active ${phase} ${ending} releases the owner and resumes the same source for queued callers`,
    { timeout }, async t => {
      const owners = observeOwners(t);
      const owner = program(t, `let count = 0; globalThis.meowfilmProcess = value => {
        if (value.fail) {
          if (${JSON.stringify(phase)} === 'regexp') /^(a+)+$/.test('a'.repeat(100) + '!');
          if (${JSON.stringify(phase)} === 'jobs') return (async () => { for (;;) await 0; })();
          if (${JSON.stringify(phase)} === 'pending') return new Promise(() => {});
          for (;;) {}
        }
        return { n: ++count, value };
      };`);
      assert.deepEqual(await owner.invoke({ warm: true }), { n: 1, value: { warm: true } });
      const original = owners[0], controller = new AbortController();
      const failed = assert.rejects(owner.invoke({ fail: true }, { signal: controller.signal }),
        { code: ending === 'abort' ? 'CANCELLED' : 'TIMEOUT' }).then(() => assertReleased(original));
      const removed = new AbortController();
      const queuedAbort = assert.rejects(owner.invoke({ removed: true }, { signal: removed.signal }),
        { code: 'CANCELLED' });
      const next = owner.invoke({ id: 2 }), last = owner.invoke({ id: 3 });
      await entered(original);
      if (ending === 'abort') controller.abort();
      removed.abort();
      await Promise.all([failed, queuedAbort]);
      assert.deepEqual(await next, { n: 1, value: { id: 2 } });
      assert.deepEqual(await last, { n: 2, value: { id: 3 } });
      assert.equal(owners.length, 2, 'recovery uses one replacement of the same native core');
      await owner.close();
      for (const engine of owners) assertReleased(engine);
    });
}

test('pure native computation leaves Node HTTP media I/O responsive', { timeout }, async t => {
  const owners = observeOwners(t);
  const owner = program(t, `globalThis.meowfilmProcess = value => {
    if (value) { const end = Date.now() + 900; while (Date.now() < end) {} }
    return value;
  };`);
  await owner.invoke(false);
  let done = false;
  const work = owner.invoke(true).then(value => { done = true; return value; });
  await entered(owners[0]);
  const server = createServer((_request, response) => response.end('synthetic-media-payload'));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => new Promise(resolve => { server.closeAllConnections(); server.close(resolve); }));
  const response = await fetch(`http://127.0.0.1:${server.address().port}/media`);
  assert.equal(await response.text(), 'synthetic-media-payload');
  assert.equal(done, false, 'HTTP completes while the native CPU turn is still running');
  assert.equal(await work, true);
});

test('close rejects the active turn and its queue and waits for real native release',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const owner = program(t, `globalThis.meowfilmProcess = value => {
      if (value) for (;;) {}
      return null;
    };`);
    await owner.invoke(false);
    const active = assert.rejects(owner.invoke(true), { code: 'STALE_SESSION' });
    const queued = assert.rejects(owner.invoke(false), { code: 'STALE_SESSION' });
    await entered(owners[0]);
    const close = owner.close();
    assert.equal(owner.close(), close);
    await Promise.all([active, queued, close]);
    assertReleased(owners[0]);
    await assert.rejects(owner.invoke(false), { code: 'STALE_SESSION' });
    const empty = new NativeJsonProgram('globalThis.meowfilmProcess = value => value;');
    await empty.close();
    await assert.rejects(empty.invoke(null), { code: 'STALE_SESSION' });
  });

test('close revokes a native success before JSON publication and waits for real release',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const owner = program(t, 'globalThis.meowfilmProcess = value => value;');
    assert.equal(await owner.invoke('warm'), 'warm');
    const engine = owners[0], receive = engine.receive;
    let closing, nativeResult;
    t.mock.method(engine, 'receive', function(message) {
      const result = Reflect.apply(receive, this, [message]);
      if (message.type === 'result') {
        nativeResult = { ok: message.ok, code: message.code, value: message.value };
        closing = owner.close();
      }
      return result;
    });
    const active = assert.rejects(owner.invoke('late'), { code: 'STALE_SESSION' })
      .then(() => assertReleased(engine));
    const queued = assert.rejects(owner.invoke('queued'), { code: 'STALE_SESSION' });
    await Promise.all([active, queued]);
    assert.deepEqual(nativeResult, { ok: true, code: '', value: '"late"' },
      'the actual native success was delivered before closing, without replacing its message');
    assert.equal(owner.close(), closing);
    await closing;
    assertReleased(engine);
  });

test('program failure and invalid JSON outputs reset the same source without losing its queue',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const owner = program(t, `let count = 0; globalThis.meowfilmProcess = value => {
      if (value === 'throw') throw Error('synthetic failure');
      if (value === 'undefined') return undefined;
      if (value === 'cycle') { const cycle = {}; cycle.self = cycle; return cycle; }
      return ++count;
    };`);
    assert.equal(await owner.invoke(null), 1);
    for (const [input, code] of [['throw', 'SCRIPT_EXCEPTION'], ['undefined', 'INVALID_ARGUMENT'],
      ['cycle', 'SCRIPT_EXCEPTION']]) {
      const original = owners.at(-1);
      const failed = assert.rejects(owner.invoke(input), { code });
      const next = owner.invoke(null);
      await failed;
      assertReleased(original);
      assert.equal(await next, 1);
    }
    const bad = program(t, 'globalThis.meowfilmProcess = 3;');
    await assert.rejects(bad.invoke(null), { code: 'INVALID_ARGUMENT' });
    assertReleased(owners.at(-1));
    const cycle = {}; cycle.self = cycle;
    await assert.rejects(owner.invoke(cycle), TypeError);
    assert.equal(await owner.invoke(null), 2, 'rejected host input never mutates program state');
  });

test('the existing heap and stack budgets fail execution and recover the same program',
  { timeout }, async t => {
    const owners = observeOwners(t);
    const owner = program(t, `globalThis.meowfilmProcess = input => {
      if (input === 'buffer') return new ArrayBuffer(128 * 1024 * 1024);
      if (input === 'array') return new Array(100_000_000).fill('allocation');
      if (input === 'stack') { const recurse = () => recurse(); return recurse(); }
      return { ok: true };
    };`);
    await owner.invoke(null);
    for (const input of ['buffer', 'array', 'stack']) {
      const original = owners.at(-1);
      await assert.rejects(owner.invoke(input), { code: 'RESOURCE_LIMIT' });
      assertReleased(original);
      assert.deepEqual(await owner.invoke(null), { ok: true });
    }
  });

test('shared QuickJS JSON semantics retain Unicode, assertions, replacements and g/y state',
  { timeout }, async t => {
    const owner = program(t, `globalThis.meowfilmProcess = () => {
      const global = /a/g, sticky = /a/y;
      const runs = expression => [expression.test('aa'), expression.lastIndex,
        expression.test('aa'), expression.lastIndex, expression.test('aa'), expression.lastIndex];
      return {
        unicode: /\\p{Script=Han}+/u.test('中文'),
        fold: /k/iu.test('K'), upper: 'ß中文'.toUpperCase(), lower: 'İK'.toLowerCase(),
        nfkc: 'Ａ①ﬃ'.normalize('NFKC'),
        assertions: 'abc'.replace(/(?<=a)(b)(?=c)/g, '[$1]'),
        named: 'ab'.replace(/(?<first>a)(b)/, '$2$<first>'),
        global: runs(global), sticky: runs(sticky),
        lone: '\\ud800x\\udfff'.replace(/x/u, '中文'),
        emoji: /\\p{Extended_Pictographic}/u.test('猫🎬第12集'),
        io: [typeof fetch, typeof require, typeof process],
      };
    };`);
    const expected = {
      unicode: true, fold: true, upper: 'SS中文', lower: 'i\u0307k', nfkc: 'A1ffi',
      assertions: 'a[b]c', named: 'ba', global: [true, 1, true, 2, false, 0],
      sticky: [true, 1, true, 2, false, 0], lone: '\ud800中文\udfff',
      emoji: true, io: ['undefined', 'undefined', 'undefined'],
    };
    assert.deepEqual(await owner.invoke(null), expected);
    assert.deepEqual(await owner.invoke(null), expected);
  });

const regexProgram = `let expression, pattern, flags;
globalThis.meowfilmProcess = input => {
  if (!input.pattern) return { ok: true };
  try {
    if (pattern !== input.pattern || flags !== input.flags) {
      expression = new RegExp(input.pattern, input.flags);
      pattern = input.pattern; flags = input.flags;
    }
    expression.lastIndex = 0;
    return input.remove ? { text: input.text.replace(expression, '') }
      : { matched: expression.test(input.text) };
  } catch { return { caught: true }; }
};`;

const regexWork = {
  compilation: () => ({ pattern: '(?<=' + 'a'.repeat(1_048_576) + ')b', flags: 'i', text: 'b' }),
  prescan: () => ({ pattern: '\\k<x>'.repeat(9999) + 'a'.repeat(1_048_576) + '(?<x>b)',
    flags: 'i', text: 'b' }),
  deletion: () => ({ pattern: '(?=a{4096})a', flags: 'ig', text: 'a'.repeat(1_048_576), remove: true }),
  forward: () => ({ pattern: '^(?=(a{1048576}))' + '(?=\\1)'.repeat(9999),
    flags: 'i', text: 'a'.repeat(1_048_576) }),
  backward: () => ({ pattern: '^(?=(a{1048576}))a{1048576}' + '(?<=\\1)'.repeat(9999),
    flags: 'i', text: 'a'.repeat(1_048_576) }),
};

for (const kind of Object.keys(regexWork)) for (const ending of ['abort', 'timeout', 'close']) {
  test(`native JSON ${kind} ${ending} interrupts actual regex work, drains and preserves queued recovery`,
    { timeout }, async t => {
      const owners = observeOwners(t), owner = program(t, regexProgram);
      const input = regexWork[kind]();
      if (kind === 'forward' || kind === 'backward') {
        assert.deepEqual(await owner.invoke({ ...input, text: 'b' }), { matched: false });
      } else assert.deepEqual(await owner.invoke({}), { ok: true });
      const original = owners[0], controller = new AbortController();
      let done = false;
      const active = assert.rejects(owner.invoke(input, { signal: controller.signal }), {
        code: ending === 'abort' ? 'CANCELLED' : ending === 'timeout' ? 'TIMEOUT' : 'STALE_SESSION',
      }).then(() => { done = true; assertReleased(original); });
      const queued = owner.invoke({ pattern: '(?<=a)b', text: 'ab', flags: 'i' });
      const queuedResult = ending === 'close'
        ? assert.rejects(queued, { code: 'STALE_SESSION' }) : queued;
      await entered(original);
      await sleep(100);
      assert.equal(done, false, 'the native regular-expression operation is still active');
      const started = Date.now();
      if (ending === 'abort') controller.abort();
      if (ending === 'close') await owner.close();
      await active;
      assert.ok(Date.now() - started < 5_000, 'native execution physically drains within the existing bound');
      if (ending === 'close') await queuedResult;
      else {
        assert.deepEqual(await queuedResult, { matched: true });
        assert.deepEqual(await owner.invoke({ pattern: '(?=a{4})a', text: 'aaaaab',
          flags: 'ig', remove: true }), { text: 'aaab' });
        assert.deepEqual(await owner.invoke({ pattern: '[', text: 'b', flags: 'i' }), { caught: true });
        assert.equal(owners.length, 2);
      }
    });
}

test('capture discovery, ordinary deletion, references and assertions retain their exact semantics',
  { timeout }, async t => {
    const owner = program(t, regexProgram);
    for (const [pattern, text] of [
      ['\\k<x>(?<x>b)', 'b'], ['\\1(b)', 'b'], ['\\2(b)', '\u0002b'], ['\\k', 'k'],
      ['\\k<->', 'k<->'], ['\\k<missing>', 'k<missing>'], ['a|(?<x>b)', 'b'],
      ['\\k<x>[a](?<x>b)', 'ab'],
    ]) assert.deepEqual(await owner.invoke({ pattern, text, flags: 'i' }), { matched: true });
    for (const [pattern, text, flags, expected] of [
      ['a', 'banana', 'g', 'bnn'], ['(?=a{4})a', 'aaaaab', 'ig', 'aaab'],
      ['(?:)', 'A🐱B', 'gu', 'A🐱B'], ['a', 'Aa!', 'ig', '!'], ['z', 'abc', 'g', 'abc'],
    ]) assert.deepEqual(await owner.invoke({ pattern, text, flags, remove: true }), { text: expected });
    for (const [pattern, flags] of [['\\k<missing>(?<x>b)', 'i'], ['\\2(b)', 'u']]) {
      assert.deepEqual(await owner.invoke({ pattern, text: 'b', flags }), { caught: true });
    }
    for (const [pattern, text, flags, matched] of [
      ['^(a)\\1$', 'aa', '', true], ['^(a)\\1$', 'aA', '', false], ['^(a)\\1$', 'aA', 'i', true],
      ['^(?<x>a)\\k<x>$', 'aA', 'i', true], ['^..(?<=\\1(a))$', 'aa', '', true],
      ['^..(?<=\\1(a))$', 'Aa', '', false], ['^..(?<=\\1(a))$', 'Aa', 'i', true],
      ['^..(?<=\\k<x>(?<x>a))$', 'Aa', 'i', true], ['^(Ā)\\1$', 'ĀĀ', '', true],
      ['^..(?<=\\1(Ā))$', 'ĀĀ', '', true], ['^(😀)\\1$', '😀😀', 'u', true],
      ['^..(?<=\\1(😀))$', '😀😀', 'u', true], ['^(𐐀)\\1$', '𐐀𐐨', 'iu', true],
      ['^..(?<=\\1(𐐀))$', '𐐀𐐨', 'iu', true], ['^(a)?\\1b$', 'b', '', true],
      ['^(?=(a+))(?=(\\1))\\2$', 'aaaa', '', true], ['^(?!(a+)b)(a+)\\2$', 'aaaa', '', true],
      ['^(?!(a+)b)(a+)\\2$', 'aaaab', '', false], ['^(?=(a|ab))\\1c$', 'abc', '', false],
      ['^(?=(ab|a))\\1c$', 'abc', '', true],
    ]) assert.deepEqual(await owner.invoke({ pattern, text, flags }), { matched });
  });
