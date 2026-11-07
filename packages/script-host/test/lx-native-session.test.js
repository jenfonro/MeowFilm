import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createScriptHost } from '../src/index.js';
import { NativeSession } from '../src/native-session.js';
import { fault } from '../src/errors.js';
import { lxScript, target, deferred, turn } from './fixtures.js';

function capturingHost(options = {}) {
  const sessions = [];
  const host = createScriptHost({ ...options, adapters: { lx: input => {
    const session = new NativeSession(input);
    sessions.push(session);
    return session;
  } } });
  return { host, sessions };
}

test('LX raw bytes preserve every byte, view offsets, NUL and response Buffer identity', async t => {
  const bytes = Buffer.from(Array.from({ length: 256 }, (_, index) => index));
  const backing = Buffer.concat([Buffer.from([83]), bytes, Buffer.from([71])]);
  const requests = [];
  const host = createScriptHost({ network: async input => {
    requests.push(input);
    assert.equal(input.options.encoding, null);
    assert.equal(Buffer.isBuffer(input.options.body), true);
    assert.deepEqual(input.options.body, Buffer.from([0, 255, 128]));
    return { statusCode: 200, body: backing.subarray(1, -1) };
  } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => new Promise((resolve,reject) => {
      const input = Buffer.from([83,0,255,128,71]);
      lx.request('https://fixture.invalid/bytes',{method:'POST',encoding:null,body:input.subarray(1,-1)},
        (failure,response,body) => failure ? reject(failure) : resolve({
          raw:body.toString('hex'), alias:body===response.raw, buffer:Buffer.isBuffer(body),
          binary:body.subarray(0,4), text:Buffer.from('a\\0中文🎵').toString(),
        }));
      input.fill(9);
    })`,
  }) });
  assert.deepEqual(await host.invoke(target()), {
    raw: bytes.toString('hex'), alias: true, buffer: true,
    binary: Uint8Array.from([0, 1, 2, 3]), text: 'a\0中文🎵',
  });
  assert.equal(requests.length, 1);
  assert.equal(backing[0], 83);
  assert.equal(backing.at(-1), 71);
});

test('LX serializes JSON, urlencoded and multipart request bodies before asynchronous Node I/O', async t => {
  const received = [];
  const host = createScriptHost({ network: async input => {
    received.push(input.options);
    assert.equal(Buffer.isBuffer(input.options.body), true);
    assert.equal(input.options.form, undefined);
    assert.equal(input.options.formData, undefined);
    return { statusCode: 200, body: Buffer.from('{"accepted":true}') };
  } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({ handler: `async () => {
    const send = options => new Promise((resolve,reject) => lx.request('https://fixture.invalid/body',
      {method:'POST',...options},(error,response,body)=>error?reject(error):resolve(body)));
    return [
      await send({json:true,body:{'中文':'x'.repeat(1024*1024),nul:'a\\0b'}}),
      await send({form:{name:'a b',special:'~!中文'}}),
      await send({formData:{name:'fixture',file:{value:Buffer.from([0,255,128]),options:{filename:'a.bin'}}}}),
    ];
  }` }) });
  assert.deepEqual(await host.invoke(target()), [{ accepted: true }, { accepted: true }, { accepted: true }]);
  assert.deepEqual(JSON.parse(received[0].body), { 中文: 'x'.repeat(1024 * 1024), nul: 'a\0b' });
  assert.equal(received[0].headers['content-type'], 'application/json');
  assert.equal(received[1].body.toString(), 'name=a+b&special=%7E%21%E4%B8%AD%E6%96%87');
  assert.equal(received[1].headers['content-type'], 'application/x-www-form-urlencoded');
  assert.match(received[2].headers['content-type'], /^multipart\/form-data; boundary=meowfilm-/);
  assert.ok(received[2].body.includes(Buffer.from([0, 255, 128])));
  assert.ok(received[2].body.toString().includes('filename="a.bin"'));
});

test('LX native response decoding keeps default JSON, explicit JSON failure and character encodings', async t => {
  const replies = new Map([
    ['json', Buffer.from('{"中文":"a\\u0000b"}')],
    ['text', Buffer.from('ordinary text')],
    ['utf16le', Buffer.from('中文🎵\0', 'utf16le')],
    ['latin1', Buffer.from([0, 255, 128])],
    ['ascii', Buffer.from([65, 255, 128])],
  ]);
  const host = createScriptHost({ network: async ({ url }) => ({
    statusCode: 200, body: replies.get(new URL(url).pathname.slice(1)),
  }) });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({ handler: `async () => {
    const read = (path,options={}) => new Promise(resolve => lx.request('https://fixture.invalid/'+path,
      options,(error,response,body)=>resolve(error?error.code:body)));
    return [await read('json'), await read('text'), await read('text',{json:true}),
      await read('utf16le',{encoding:'utf16le'}),await read('latin1',{encoding:'latin1'}),
      await read('ascii',{encoding:'ascii'})];
  }` }) });
  assert.deepEqual(await host.invoke(target()), [
    { 中文: 'a\0b' }, 'ordinary text', 'INVALID_JSON', '中文🎵\0', '\0ÿ\x80', 'A\x7f\0',
  ]);
});

test('B10 LX native retains the original 12 MiB response, 12 MiB Buffer allocations and both explicit MD5 operations',
  { timeout: 60000 }, async t => {
    const size = 12 * 1024 * 1024, body = Buffer.alloc(size, 0x5a);
    const digest = value => createHash('md5').update(value).digest('hex');
    const host = createScriptHost({ network: async () => ({ statusCode: 200, body }) });
    t.after(() => host.close());
    await host.install({ installationId: 'a', enabled: true, source: lxScript({
      setup: 'const digest = value => lx.utils.crypto.md5(value);',
      handler: `async ({info}) => new Promise((resolve,reject) =>
        lx.request('https://fixture.invalid/large',{encoding:null},
          (error,response,body)=>error?reject(error):resolve({
            length:body.length, first:body[0], last:body[body.length-1],
            hash:digest(body),
            allocLength:Buffer.alloc(info.musicInfo.size||0,0x5a).length,
            allocHash:digest(Buffer.alloc(info.musicInfo.size||0,0x5a)),
          })))`,
    }) });
    assert.deepEqual(await host.invoke(target('a', 'kw', { size })), {
      length: size, first: 0x5a, last: 0x5a, hash: digest(body),
      allocLength: size, allocHash: digest(Buffer.alloc(size, 0x5a)),
    });
    assert.deepEqual(await host.invoke(target('a', 'kw', { size: 0 })), {
      length: size, first: 0x5a, last: 0x5a, hash: digest(body),
      allocLength: 0, allocHash: digest(Buffer.alloc(0, 0x5a)),
    });
  });

test('LX retirement reports physical storage I/O debt after native thread exit and fences stale authority', async t => {
  const entered = deferred(), release = deferred();
  let operation;
  const { host, sessions } = capturingHost({ storage: { get(input) {
    operation = input; entered.resolve(); return release.promise;
  } } });
  t.after(async () => { release.resolve('late'); await host.close(); });
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read'] },
    source: lxScript({ handler: 'async (_,{storage}) => await storage.get("key")' }) });
  const pending = assert.rejects(host.invoke(target()), { code: 'STALE_SESSION' });
  await entered.promise;
  await host.disable('a');
  await pending;
  assert.equal(operation.signal.aborted, true);
  assert.equal(operation.isCurrent(), false);
  assert.equal(sessions[0].stats().threadCount, 0);
  assert.equal(sessions[0].stats().runtimeCount, 0);
  assert.equal(host.cleanupPending('a'), 1);
  assert.equal(host.stats().pendingAdapterTasks, 1);
  const retirement = sessions[0].adapterRetirement();
  release.resolve('late');
  await retirement.idle;
  assert.equal(retirement.pendingAdapterTasks(), 0);
  assert.equal(host.cleanupPending('a'), 0);
});

for (const ending of ['timeout', 'cancellation']) {
  test(`LX ${ending} fences asynchronous storage at native revocation without dropping physical work`,
    { timeout: 15000 }, async t => {
      const entered = deferred(), release = deferred(), resumed = deferred(), finished = deferred();
      const revoked = deferred(), controller = new AbortController();
      let operation, writes = 0, invocationSettled = false;
      const { host, sessions } = capturingHost({ storage: { async set(input) {
        operation = input; entered.resolve();
        await release.promise;
        if (!input.signal.aborted && input.isCurrent()) writes++;
        resumed.resolve();
        // An adapter that ignores cancellation remains physical work until this settles.
        await finished.promise;
      } } });
      t.after(async () => { release.resolve(); finished.resolve(); await host.close(); });
      const a = await host.install({
        installationId: 'a', enabled: true, permissions: { storage: ['write'] },
        source: lxScript({ setup: 'let tailSteps=0, calls=0;', handler: `async ({info},{storage}) => {
          if (!info.musicInfo.hold) return {calls:++calls,tailSteps};
          try { await storage.set('key','forbidden'); }
          catch {
            for(let i=0;i<128;i++) { await Promise.resolve(); tailSteps++; }
          }
          return 'obsolete';
        }` }),
      });
      const b = await host.install({ installationId: 'b', enabled: true,
        source: lxScript({ setup: 'let calls=0;', handler: 'async()=>++calls' }) });
      assert.equal(await host.invoke(target('b')), 1);
      const session = sessions[0], engine = session.engine;
      assert.equal(session.limits.cleanupTimeoutMs, 100);
      const receive = engine.receive;
      let storageId, observed = false;
      engine.receive = function (message) {
        const result = receive.call(this, message);
        if (message.type === 'host' && message.name === 'storage') storageId = message.id;
        if (!observed && operation && (
          message.type === 'cancelHost' && message.id === storageId ||
          message.type === 'cancelScope' && message.scope === operation.requestId
        )) {
          observed = true;
          // Observe the public adapter authority after the real notification was processed.
          revoked.resolve({
            code: message.code, aborted: operation.signal.aborted, current: operation.isCurrent(),
            invocationSettled, pendingAdapterTasks: session.adapterTasks.size,
          });
          release.resolve();
        }
        return result;
      };
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      const pending = assert.rejects(host.invoke(target('a', 'kw', { hold: true }), {
        signal: controller.signal, ...(ending === 'timeout' ? { timeoutMs: 100 } : {}),
      }).finally(() => { invocationSettled = true; }), { code });
      await entered.promise;
      assert.equal(await host.invoke(target('b')), 2);
      if (ending === 'cancellation') controller.abort();
      const snapshot = await revoked.promise;
      await resumed.promise;
      await pending;
      assert.equal(snapshot.code, code);
      assert.equal(snapshot.aborted, true);
      assert.equal(snapshot.current, false);
      if (ending === 'timeout') assert.equal(snapshot.invocationSettled, false);
      assert.equal(snapshot.pendingAdapterTasks, 1);
      assert.equal(operation.signal.aborted, true);
      assert.equal(operation.isCurrent(), false);
      assert.equal(writes, 0);
      assert.equal(host.stats().pendingAdapterTasks, 1);
      finished.resolve();
      await session.whenAdaptersIdle();
      assert.equal(host.stats().pendingAdapterTasks, 0);
      assert.deepEqual(await host.invoke(target('a')), { calls: 1, tailSteps: 128 });
      assert.equal(host.get('a').generation, a.generation);
      assert.equal(host.get('a').state, 'ready');
      assert.equal(await host.invoke(target('b')), 3);
      assert.equal(host.get('b').generation, b.generation);
      assert.equal(host.get('b').state, 'ready');
    });
}

test('LX explicit storage context stays private across native await and completed contexts remain denied', async t => {
  const calls = [];
  const host = createScriptHost({ storage: { get(input) { calls.push(input.siteId); return input.siteId; } } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read'] },
    source: lxScript({ setup: 'let saved; const globalStorage = typeof meow.storage;', handler: `async ({info},{storage}) => {
      if(info.musicInfo.save) { saved=storage; return globalStorage; }
      await Promise.resolve();
      await new Promise(resolve=>setTimeout(resolve,1));
      let old;
      try { old=await saved.get('key'); } catch(error) { old=error.code; }
      return {old,current:await storage.get('key'),global:typeof meow.storage};
    }` }) });
  assert.equal(await host.invoke(target('a', 'kw', { save: true })), 'undefined');
  assert.deepEqual(await host.invoke(target('a', 'tx')), {
    old: 'PERMISSION_DENIED', current: 'tx', global: 'undefined',
  });
  assert.deepEqual(calls, ['tx']);
});

test('LX session-owned callbacks keep the original invocation deadline after task-scope restoration',
  { timeout: 15000 }, async t => {
    const diagnostics = [];
    const host = createScriptHost({ onDiagnostic: event => diagnostics.push(event) });
    t.after(() => host.close());
    await host.install({ installationId: 'a', enabled: true, source: lxScript({
      handler: `async () => {
        setTimeout(()=>{},10000);
        lx.send('updateAlert',{log:'scope-restored'});
        while(true) {}
      }`,
    }) });
    await host.install({ installationId: 'b', enabled: true, source: lxScript({ handler: 'async()=>7' }) });
    await assert.rejects(host.invoke(target('a'), { timeoutMs: 100 }), { code: 'TIMEOUT' });
    assert.equal(host.get('a').state, 'failed');
    assert.equal(host.get('a').updateAlert.log, 'scope-restored');
    assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
      event.operation === 'native.unresponsive' && event.code === 'TIMEOUT').length, 1);
    assert.equal(await host.invoke(target('b')), 7);
  });

// L3: use public LX callbacks and finite work, not private task/owner hooks.
for (const [kind, schedule] of [
  ['timer', 'setTimeout(run,1);'],
  ['network', `lx.request('https://fixture.invalid/callback',{},error=>error?reject(error):run());`],
  ['microtask', 'queueMicrotask(run);'],
]) for (const ending of ['timeout', 'abort', 'complete']) {
  test(`LX active ${kind} callback keeps invocation execution control on ${ending}`,
    { timeout: 15000 }, async t => {
      const entered = deferred(), networkEntered = deferred(), releaseNetwork = deferred(), natural = deferred();
      const controller = new AbortController(), failures = [], diagnostics = [];
      let naturalCompletions = 0, cancelledAt;
      const { host, sessions } = capturingHost({
        onDiagnostic: event => diagnostics.push(event),
        network: input => {
          if (input.url === 'https://fixture.invalid/callback') {
            networkEntered.resolve(input);
            return releaseNetwork.promise;
          }
          assert.equal(input.url, 'https://fixture.invalid/natural-completion');
          naturalCompletions++;
          natural.resolve();
          return { statusCode: 200, body: 'observed' };
        },
      });
      t.after(async () => {
        releaseNetwork.resolve({ statusCode: 200, body: 'released' });
        await host.close();
      });
      const a = await host.install({ installationId: 'a', enabled: true, source: lxScript({
        setup: 'let callbacks=0;',
        handler: `async ({info}) => {
          if (info.musicInfo.probe) return callbacks;
          return new Promise((resolve,reject) => {
            const run = () => {
              callbacks++;
              const started=Date.now();
              lx.send('updateAlert',{log:JSON.stringify({kind:${JSON.stringify(kind)},started})});
              while(Date.now()-started<600) {}
              lx.request('https://fixture.invalid/natural-completion',{},()=>{});
              resolve({callbacks,elapsed:Date.now()-started});
            };
            ${schedule}
          });
        }`,
      }) });
      const b = await host.install({ installationId: 'b', enabled: true,
        source: lxScript({ setup: 'let calls=0;', handler: 'async()=>++calls' }) });
      const session = sessions[0], engine = session.engine;
      assert.equal(session.limits.cleanupTimeoutMs, 100);
      const onEvent = engine.onEvent;
      engine.onEvent = (name, value, scope) => {
        onEvent(name, value, scope);
        if (name === 'send' && value.event === 'updateAlert') {
          entered.resolve(JSON.parse(value.data.log));
          if (ending === 'abort') { cancelledAt = Date.now(); controller.abort(); }
        }
        if (name === 'failure') failures.push({ code: value.code, at: Date.now() });
      };
      await withoutBackgroundFallbackStop(engine, async () => {
        assert.equal(await host.invoke(target('b')), 1);
        const admittedAt = Date.now();
        const outcome = host.invoke(target('a'), {
          signal: controller.signal,
          ...(ending === 'timeout' ? { timeoutMs: 150 } : ending === 'complete' ? { timeoutMs: 2000 } : {}),
        }).then(value => ({ value }), error => ({ code: error.code }));
        if (kind === 'network') {
          const input = await networkEntered.promise;
          assert.equal(input.signal.aborted, false);
          releaseNetwork.resolve({ statusCode: 200, body: 'released' });
        }
        const marker = await entered.promise;
        assert.equal(marker.kind, kind);
        assert.ok(Number.isSafeInteger(marker.started));
        assert.equal(await host.invoke(target('b')), 2);
        if (ending === 'complete') {
          const result = await outcome;
          assert.equal(result.value?.callbacks, 1);
          assert.ok(result.value.elapsed >= 600, 'normal callbacks may exceed the cleanup window');
          await natural.promise;
          await session.whenAdaptersIdle();
          assert.equal(naturalCompletions, 1);
          assert.deepEqual(failures, []);
          assert.equal(await host.invoke(target('a', 'kw', { probe: true })), 1);
          assert.equal(host.get('a').state, 'ready');
          assert.equal(host.get('a').generation, a.generation);
          assert.equal(engine.stats().threadCount, 1);
        } else {
          const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
          assert.deepEqual(await outcome, { code });
          const exit = await Promise.race([
            engine.exited,
            natural.promise.then(() => { throw Error('callback reached its finite natural completion before owner exit'); }),
          ]);
          const exitedAt = Date.now();
          t.diagnostic(JSON.stringify({ kind, ending, admittedAt, enteredAt: marker.started,
            cancelledAt, failures, exitedAt, naturalCompletions }));
          assert.equal(exit.code, code);
          assert.equal(exit.stopped, true);
          for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
            'hostCalls', 'timers', 'jobsPending']) assert.equal(exit[key], 0, key);
          assert.equal(failures.length, 1);
          assert.equal(failures[0].code, code);
          assert.ok(failures[0].at - marker.started < 600, 'the request must interrupt before natural completion');
          assert.ok(exitedAt - marker.started < 600, 'logical cancellation alone is not actual owner exit');
          assert.equal(naturalCompletions, 0);
          assert.equal(session.live, false);
          assert.equal(host.get('a').state, 'failed');
          assert.equal(host.get('a').generation, a.generation);
          assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
            event.operation === 'native.unresponsive' && event.code === code).length, 1);
        }
        assert.equal(await host.invoke(target('b')), 3);
        assert.equal(host.get('b').state, 'ready');
        assert.equal(host.get('b').generation, b.generation);
        assert.equal(sessions.length, 2, 'callback control must not silently replace an owner');
        assert.equal(host.stats().pendingAdapterTasks, 0);
      });
    });
}

for (const kind of ['timer', 'network']) for (const ending of ['completed', 'cancelled']) {
  test(`LX ${ending} request retains its ${kind} callback without borrowing a new storage context`,
    { timeout: 15000 }, async t => {
      const armed = deferred(), networkEntered = deferred(), releaseNetwork = deferred(), currentWritten = deferred();
      const controller = new AbortController(), storageCalls = [];
      let stored = null;
      const { host, sessions } = capturingHost({
        network: input => {
          assert.equal(kind, 'network');
          networkEntered.resolve(input);
          return releaseNetwork.promise;
        },
        storage: {
          set(input) {
            storageCalls.push(['set', input.siteId, input.value]);
            assert.equal(input.signal.aborted, false);
            assert.equal(input.isCurrent(), true);
            stored = input.value;
            if (input.value === 'current') currentWritten.resolve(input);
          },
          get(input) {
            storageCalls.push(['get', input.siteId]);
            assert.equal(input.isCurrent(), true);
            return stored;
          },
        },
      });
      t.after(async () => {
        releaseNetwork.resolve({ statusCode: 200, body: 'released' });
        await host.close();
      });
      const a = await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read', 'write'] },
        source: lxScript({
          setup: `let releaseTimer=false, callbacks=0, oldCode, finishLate;
            const lateDone=new Promise(resolve=>{finishLate=resolve;});`,
          handler: `async ({info},{storage}) => {
            if (info.musicInfo.arm) {
              const finish=async error=>{
                callbacks++;
                if (error) oldCode=error.code;
                else {
                  try { await storage.set('key','old'); oldCode='wrote'; }
                  catch(failure) { oldCode=failure.code; }
                }
                finishLate();
              };
              // Release from the newer invocation, not from an elapsed-time guess.
              ${kind === 'timer' ? `const timer=setInterval(()=>{
                if (!releaseTimer) return;
                clearInterval(timer);
                return finish();
              },1);` : `lx.request('https://fixture.invalid/late',{},finish);`}
              lx.send('updateAlert',{log:'armed'});
              ${ending === 'cancelled' ? 'return new Promise(()=>{});' : "return 'armed';"}
            }
            await storage.set('key','current');
            releaseTimer=true;
            await lateDone;
            return {callbacks,old:oldCode,value:await storage.get('key')};
          }`,
        }) });
      const session = sessions[0], engine = session.engine;
      assert.equal(session.limits.cleanupTimeoutMs, 100);
      const onEvent = engine.onEvent;
      engine.onEvent = (name, value, scope) => {
        onEvent(name, value, scope);
        if (name === 'send' && value.event === 'updateAlert') armed.resolve();
      };
      await withoutBackgroundFallbackStop(engine, async () => {
        const old = host.invoke(target('a', 'kw', { arm: true }), { signal: controller.signal })
          .then(value => ({ value }), error => ({ code: error.code }));
        await armed.promise;
        const outbound = kind === 'network' ? await networkEntered.promise : null;
        if (ending === 'cancelled') controller.abort();
        assert.deepEqual(await old, ending === 'cancelled' ? { code: 'CANCELLED' } : { value: 'armed' });
        if (outbound) {
          assert.equal(outbound.signal.aborted, false);
          assert.equal(host.stats().networkRequests, 1);
          assert.equal(host.stats().pendingAdapterTasks, 1);
        }
        const current = host.invoke(target('a', 'tx'))
          .then(value => ({ value }), error => ({ code: error.code }));
        const context = await currentWritten.promise;
        assert.equal(context.isCurrent(), true);
        releaseNetwork.resolve({ statusCode: 200, body: 'released' });
        assert.deepEqual(await current, {
          value: { callbacks: 1, old: 'PERMISSION_DENIED', value: 'current' },
        });
        await session.whenAdaptersIdle();
        assert.equal(context.isCurrent(), false);
        assert.deepEqual(storageCalls, [['set', 'tx', 'current'], ['get', 'tx']]);
        assert.equal(host.get('a').state, 'ready');
        assert.equal(host.get('a').generation, a.generation);
        assert.equal(sessions.length, 1);
        assert.equal(host.stats().pendingAdapterTasks, 0);
        assert.equal((await engine.stats({ refresh: true })).timers, 0);
      });
    });
}

test('LX startup failure retires its native owner while another initialized instance retains state', async t => {
  const { host, sessions } = capturingHost();
  t.after(() => host.close());
  await host.install({ installationId: 'b', enabled: true,
    source: lxScript({ setup: 'let count=0;', handler: 'async()=>++count' }) });
  assert.equal(await host.invoke(target('b')), 1);
  await assert.rejects(host.install({ installationId: 'a', enabled: true,
    source: 'setInterval(()=>{},1); throw Error("synthetic startup failure");' }), { code: 'SCRIPT_EXCEPTION' });
  assert.equal(sessions[1].live, false);
  assert.equal(sessions[1].stats().threadCount, 0);
  assert.equal(sessions[1].stats().runtimeCount, 0);
  assert.equal(sessions[1].stats().timers, 0);
  assert.equal(await host.invoke(target('b')), 2);
  assert.equal(host.stats().liveSessions, 1);
});

test('LX native cleanup failure stays explicit after actual exit and does not skip other owners', async () => {
  const { host, sessions } = capturingHost();
  try {
    for (const installationId of ['a', 'b'])
      await host.install({ installationId, enabled: true, source: lxScript() });
    const engine = sessions[0].engine;
    const actualStop = engine.stop.bind(engine);
    engine.stop = async reason => {
      await actualStop(reason);
      throw Error('synthetic cleanup completion failure');
    };
    await assert.rejects(host.close(), { code: 'SCRIPT_CLEANUP' });
    for (const session of sessions) {
      assert.equal(session.live, false);
      assert.equal(session.stats().threadCount, 0);
      assert.equal(session.stats().runtimeCount, 0);
      assert.equal(session.stats().contextCount, 0);
    }
    assert.equal(sessions[0].stats().cleanupError, 'SCRIPT_CLEANUP');
    assert.equal(host.stats().liveSessions, 0);
    assert.deepEqual(host.list(), []);
  } finally { await host.close().catch(() => {}); }
});

test('LX initialization preserves the original failure when cleanup completion also fails', async () => {
  const session = new NativeSession({
    installationId: 'initialization-cleanup', generation: 1,
    revision: { source: 'lx.send("inited",{});', version: '1', config: {}, permissions: { storage: [] } },
    limits: { maxStackBytes: 256 * 1024, initTimeoutMs: 5000, requestTimeoutMs: 60000, cleanupTimeoutMs: 100 },
  });
  const actualStop = session.stop.bind(session);
  session.stop = async reason => {
    await actualStop(reason);
    throw fault('SCRIPT_CLEANUP');
  };
  try {
    await assert.rejects(session.start(), { code: 'SCRIPT_PROTOCOL_ERROR' });
    assert.equal(session.failure.code, 'SCRIPT_PROTOCOL_ERROR');
    assert.equal(session.stats().threadCount, 0);
    assert.equal(session.stats().runtimeCount, 0);
    assert.equal(session.stats().contextCount, 0);
  } finally { await actualStop(); }
});

test('LX script diagnostics omit raw exception text unless the observer is explicitly provided', async t => {
  const events = [];
  const host = createScriptHost({ onDiagnostic: event => events.push(event) });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: 'async()=>{throw Error("SYNTHETIC-PRIVATE-TEXT");}',
  }) });
  await assert.rejects(host.invoke(target()), { code: 'SCRIPT_EXCEPTION' });
  await turn();
  assert.equal(events.length, 1);
  assert.equal(events[0].kind, 'Error');
  assert.equal(events[0].siteId, 'kw');
  assert.equal(Object.hasOwn(events[0], 'message'), false);
  assert.equal(JSON.stringify(events).includes('SYNTHETIC-PRIVATE-TEXT'), false);
});

async function withoutBackgroundFallbackStop(engine, scenario) {
  let timer, usedFallback = false;
  const expired = new Promise((_resolve, reject) => {
    timer = setTimeout(() => {
      usedFallback = true;
      reject(new Error('LX background scenario exceeded its external deadline; fallback stop is not request cleanup'));
      engine.stop().catch(() => {});
    }, 5_000);
  });
  try {
    const value = await Promise.race([Promise.resolve().then(scenario), expired]);
    assert.equal(usedFallback, false, 'fallback or after-hook stop must not count as native retirement');
    return value;
  } finally { clearTimeout(timer); }
}

// BG01 / S03, M10, V09: exercise real host.invoke, including abort's separate logical response.
for (const ending of ['timeout', 'abort']) {
  test(`LX admitted ${ending} retires an owner already stuck in its background timer`,
    { timeout: 15_000 }, async t => {
      const entered = deferred(), diagnostics = [], failures = [];
      const { host, sessions } = capturingHost({ onDiagnostic: event => diagnostics.push(event) });
      t.after(() => host.close());
      await host.install({ installationId: 'a', enabled: true, source: lxScript({
        handler: `async ({info}) => {
          if (info.musicInfo.arm) {
            setTimeout(() => {
              lx.send('updateAlert', {log:'timer-entered'});
              for (;;) {}
            }, 10);
            return 'armed';
          }
          return 'pong';
        }`,
      }) });
      await host.install({ installationId: 'b', enabled: true,
        source: lxScript({ setup: 'let count=0;', handler: 'async()=>++count' }) });
      const session = sessions[0], engine = session.engine;
      const generation = host.get('a').generation, otherGeneration = host.get('b').generation;
      const onEvent = engine.onEvent;
      engine.onEvent = (name, value, scope) => {
        onEvent(name, value, scope);
        if (name === 'send' && value.event === 'updateAlert' && value.data.log === 'timer-entered') entered.resolve();
        if (name === 'failure') failures.push({ code: value.code, scope });
      };
      await withoutBackgroundFallbackStop(engine, async () => {
        assert.equal(await host.invoke(target('a', 'kw', { arm: true })), 'armed');
        await entered.promise;
        assert.equal(await host.invoke(target('b')), 1);
        const controller = new AbortController();
        const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
        const pending = host.invoke(target('a'), {
          timeoutMs: ending === 'timeout' ? 250 : 60_000, signal: controller.signal,
        });
        const rejected = assert.rejects(pending, { code });
        if (ending === 'abort') controller.abort(); // host/session/native admission has already returned.
        assert.equal(await host.invoke(target('b')), 2);
        await rejected;
        const exit = await engine.exited; // Logical abort alone is explicitly insufficient.
        assert.equal(exit.code, code); assert.equal(exit.stopped, true);
        for (const key of ['threadCount', 'runtimeCount', 'contextCount']) assert.equal(exit[key], 0, key);
        assert.deepEqual(failures, [{ code, scope: 2 }]);
        assert.equal(session.live, false); assert.equal(session.failure.code, code);
        assert.equal(host.get('a').state, 'failed'); assert.equal(host.get('a').generation, generation);
        assert.equal(sessions.length, 2, 'failure must not silently create a replacement engine');
        assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
          event.operation === 'native.unresponsive' && event.code === code).length, 1);
        assert.equal(await host.invoke(target('b')), 3);
        assert.equal(host.get('b').state, 'ready'); assert.equal(host.get('b').generation, otherGeneration);
      });
    });
}

test('LX finite background work completes before the existing request deadline without changing generation',
  { timeout: 15_000 }, async t => {
    const entered = deferred(), diagnostics = [];
    const { host, sessions } = capturingHost({ onDiagnostic: event => diagnostics.push(event) });
    t.after(() => host.close());
    await host.install({ installationId: 'a', enabled: true, source: lxScript({
      setup: 'let count=0, finishedAt=0, rounds=0;',
      handler: `async ({info}) => {
        if (info.musicInfo.arm) {
          setTimeout(() => {
            lx.send('updateAlert', {log:'finite-timer-entered'});
            const started=Date.now();
            while(Date.now()-started<500) rounds++;
            finishedAt=Date.now();
          }, 10);
          return 'armed';
        }
        return {count:++count,finishedAt,rounds};
      }`,
    }) });
    const engine = sessions[0].engine, generation = host.get('a').generation;
    const onEvent = engine.onEvent;
    engine.onEvent = (name, value, scope) => {
      onEvent(name, value, scope);
      if (name === 'send' && value.event === 'updateAlert' && value.data.log === 'finite-timer-entered') entered.resolve();
    };
    await withoutBackgroundFallbackStop(engine, async () => {
      assert.equal(await host.invoke(target('a', 'kw', { arm: true })), 'armed');
      await entered.promise;
      const controller = new AbortController();
      controller.abort();
      await assert.rejects(host.invoke(target('a'), { signal: controller.signal, timeoutMs: 1 }), { code: 'CANCELLED' });
      await assert.rejects(host.invoke(target('a'), { timeoutMs: 0 }), { code: 'INVALID_ARGUMENT' });
      const admittedAt = Date.now();
      const result = await host.invoke(target('a'), { timeoutMs: 2_000 });
      assert.equal(result.count, 1); assert.ok(result.rounds > 0);
      assert.ok(admittedAt < result.finishedAt, 'invoke must be accepted before the finite timer finishes');
      assert.equal(diagnostics.some(event => event.operation === 'native.unresponsive'), false);
      assert.equal(engine.stats().stopped, false); assert.equal(engine.stats().threadCount, 1);
      assert.equal(host.get('a').state, 'ready'); assert.equal(host.get('a').generation, generation);
      assert.equal(sessions.length, 1);
      assert.equal((await host.invoke(target('a'))).count, 2);
    });
  });

test('LX minimal native install reaches ready, invokes a fixed result and closes the actual owner', async t => {
  const { host, sessions } = capturingHost();
  t.after(() => host.close());
  const record = await host.install({ installationId: 'a', enabled: true, source: `
lx.on('request', async () => 'lx-ready');
lx.send('inited', {status:true,sources:{
  kw:{type:'music',actions:['musicUrl'],qualitys:['128k']},
}});
` });
  assert.equal(record.state, 'ready');
  assert.equal(sessions.length, 1);
  const session = sessions[0];
  assert.equal(session.live, true);
  assert.equal(await host.invoke({
    installationId: 'a', siteId: 'kw', operation: 'musicUrl',
    payload: { quality: '128k', musicInfo: {} },
  }), 'lx-ready');
  assert.equal((await host.close()).cleanupComplete, true);
  const exit = await session.engine.exited;
  assert.equal(exit.stopped, true);
  for (const key of ['threadCount', 'runtimeCount', 'contextCount']) assert.equal(exit[key], 0, key);
  assert.equal(session.live, false);
  assert.deepEqual(host.list(), []);
});

// L10: completion enters guest code before the user's callback, including in
// byte normalization and error constructors. Exercise those public surfaces.
for (const stage of ['bytes', 'decode', 'host-error', 'sync-host-error', 'json-error',
  'immediate-abort', 'launched-abort']) for (const ending of ['timeout', 'abort', 'complete']) {
  test(`LX network ${stage} completion keeps invocation execution control on ${ending}`,
    { timeout: 15000 }, async t => {
      const entered = deferred(), networkEntered = deferred(), releaseNetwork = deferred(), natural = deferred();
      const adapterAborted = deferred(), controller = new AbortController(), failures = [], diagnostics = [];
      const errorCode = {
        'host-error': 'NETWORK_ERROR', 'sync-host-error': 'INVALID_ARGUMENT', 'json-error': 'INVALID_JSON',
        'immediate-abort': 'CANCELLED', 'launched-abort': 'CANCELLED',
      }[stage] ?? null;
      const noNetwork = stage === 'sync-host-error' || stage === 'immediate-abort';
      const decoded = { text: '中文🎵\0', values: [0, 255, 128] };
      const raw = Buffer.from(stage === 'json-error' ? '{broken' : JSON.stringify(decoded));
      const response = { statusCode: 200, headers: { 'x-fixture': 'completion' }, body: raw };
      let requests = 0, naturalCompletions = 0, cancelledAt;
      const { host, sessions } = capturingHost({
        onDiagnostic: event => diagnostics.push(event),
        network: input => {
          if (input.url === 'https://fixture.invalid/natural-completion') {
            naturalCompletions++;
            natural.resolve();
            return { statusCode: 200, body: 'observed' };
          }
          if (input.url === 'https://fixture.invalid/abort-ready') {
            assert.equal(stage, 'launched-abort');
            // The guest may abort only after the original physical adapter entered.
            return networkEntered.promise.then(() => ({ statusCode: 200, body: '' }));
          }
          assert.equal(input.url, 'https://fixture.invalid/response');
          assert.equal(noNetwork, false);
          assert.equal(input.signal.aborted, false);
          requests++;
          networkEntered.resolve(input);
          if (stage === 'launched-abort') input.signal.addEventListener('abort', () => {
            adapterAborted.resolve(input.signal.reason);
            releaseNetwork.resolve(response);
          }, { once: true });
          return releaseNetwork.promise.then(value => {
            if (stage === 'host-error') throw fault('NETWORK_ERROR');
            return value;
          });
        },
      });
      t.after(async () => {
        releaseNetwork.resolve(response);
        await host.close();
      });
      const a = await host.install({ installationId: 'a', enabled: true, source: lxScript({
        setup: 'let callbacks=0, entries=0, wrappers=0, elapsed=0;',
        handler: `async ({info}) => {
          if (info.musicInfo.probe) return {callbacks,entries,wrappers};
          const run = () => {
            if (entries) return;
            entries++;
            const started=Date.now();
            lx.send('updateAlert',{log:JSON.stringify({stage:${JSON.stringify(stage)},started,callbacks})});
            while(Date.now()-started<600) {}
            elapsed=Date.now()-started;
            lx.request('https://fixture.invalid/natural-completion',{encoding:null},()=>{});
          };
          ${stage === 'bytes' ? `
            const isView=ArrayBuffer.isView;
            ArrayBuffer.isView=value=>{run(); return isView(value);};
          ` : stage === 'decode' ? `
            const toString=Buffer.prototype.toString;
            Buffer.prototype.toString=function(...args){run(); return toString.apply(this,args);};
          ` : `
            const OriginalError=Error;
            globalThis.Error=function(message){
              if (message===${JSON.stringify(errorCode)}) { wrappers++; run(); }
              return new OriginalError(message);
            };
            Error.prototype=OriginalError.prototype;
          `}
          return new Promise((resolve,reject) => {
            const abort=lx.request('https://fixture.invalid/response',
              ${stage === 'json-error' ? '{json:true}' : stage === 'sync-host-error'
                ? "{headers:{'x-invalid':()=>{}}}" : '{}'},
              (failure,response,body) => {
                callbacks++;
                resolve({
                  callbacks,entries,wrappers,elapsed,
                  failure:failure?{code:failure.code,name:failure.name,message:failure.message}:null,
                  response:response?{
                    statusCode:response.statusCode,header:response.headers['x-fixture'],
                    rawHex:Buffer.from(response.raw||response.body).toString('hex'),
                    bodyAlias:response.body===body,buffer:Buffer.isBuffer(response.raw),
                  }:null,
                  body,
                });
              });
            ${stage === 'immediate-abort' ? 'abort(); abort();' : stage === 'launched-abort' ? `
              lx.request('https://fixture.invalid/abort-ready',{encoding:null},failure=>{
                if (failure) reject(failure);
                else { abort(); abort(); }
              });
            ` : ''}
          });
        }`,
      }) });
      const b = await host.install({ installationId: 'b', enabled: true,
        source: lxScript({ setup: 'let calls=0;', handler: 'async()=>++calls' }) });
      const session = sessions[0], engine = session.engine;
      assert.equal(session.limits.cleanupTimeoutMs, 100);
      const onEvent = engine.onEvent;
      engine.onEvent = (name, value, scope) => {
        onEvent(name, value, scope);
        if (name === 'send' && value.event === 'updateAlert') {
          entered.resolve(JSON.parse(value.data.log));
          if (ending === 'abort') { cancelledAt = Date.now(); controller.abort(); }
        }
        if (name === 'failure') failures.push({ code: value.code, at: Date.now() });
      };
      await withoutBackgroundFallbackStop(engine, async () => {
        assert.equal(await host.invoke(target('b')), 1);
        const admittedAt = Date.now();
        const outcome = host.invoke(target('a'), {
          signal: controller.signal,
          ...(ending === 'timeout' ? { timeoutMs: 150 } : ending === 'complete' ? { timeoutMs: 2000 } : {}),
        }).then(value => ({ value }), error => ({ code: error.code }));
        if (!noNetwork) {
          await networkEntered.promise;
          if (stage !== 'launched-abort') releaseNetwork.resolve(response);
        }
        const marker = await entered.promise;
        assert.equal(marker.stage, stage);
        assert.ok(Number.isSafeInteger(marker.started));
        assert.equal(marker.callbacks, 0, 'the controlled work must precede the user callback');
        assert.equal(await host.invoke(target('b')), 2);
        if (stage === 'launched-abort')
          assert.equal((await adapterAborted.promise).code, 'CANCELLED');
        if (ending === 'complete') {
          const result = await outcome;
          assert.ok(result.value?.elapsed >= 600, 'normal completion may exceed the cleanup window');
          assert.deepEqual(result.value, {
            callbacks: 1, entries: 1, wrappers: errorCode ? 1 : 0, elapsed: result.value.elapsed,
            failure: errorCode ? { code: errorCode, name: errorCode === 'CANCELLED' ? 'AbortError' : 'Error',
              message: errorCode } : null,
            response: !errorCode || stage === 'json-error' ? {
              statusCode: 200, header: 'completion', rawHex: raw.toString('hex'),
              bodyAlias: !errorCode, buffer: !errorCode,
            } : null,
            body: errorCode ? null : decoded,
          });
          await natural.promise;
          await session.whenAdaptersIdle();
          assert.equal(naturalCompletions, 1);
          assert.deepEqual(failures, []);
          assert.deepEqual(await host.invoke(target('a', 'kw', { probe: true })),
            { callbacks: 1, entries: 1, wrappers: errorCode ? 1 : 0 });
          assert.equal(host.get('a').state, 'ready');
          assert.equal(host.get('a').generation, a.generation);
          assert.equal(engine.stats().threadCount, 1);
        } else {
          const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
          assert.deepEqual(await outcome, { code });
          const exit = await Promise.race([
            engine.exited,
            natural.promise.then(() => { throw Error('completion reached its finite end before owner exit'); }),
          ]);
          const exitedAt = Date.now();
          t.diagnostic(JSON.stringify({ stage, ending, admittedAt, enteredAt: marker.started,
            cancelledAt, failures, exitedAt, naturalCompletions }));
          assert.equal(exit.code, code);
          assert.equal(exit.stopped, true);
          for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
            'hostCalls', 'timers', 'jobsPending']) assert.equal(exit[key], 0, key);
          assert.equal(failures.length, 1);
          assert.equal(failures[0].code, code);
          assert.ok(failures[0].at - marker.started < 600, 'completion must interrupt before its finite end');
          assert.ok(exitedAt - marker.started < 600, 'logical cancellation alone is not actual owner exit');
          assert.equal(naturalCompletions, 0);
          assert.equal(session.live, false);
          assert.equal(host.get('a').state, 'failed');
          assert.equal(host.get('a').generation, a.generation);
          assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
            event.operation === 'native.unresponsive' && event.code === code).length, 1);
        }
        assert.equal(requests, noNetwork ? 0 : 1);
        assert.equal(await host.invoke(target('b')), 3);
        assert.equal(host.get('b').state, 'ready');
        assert.equal(host.get('b').generation, b.generation);
        assert.equal(sessions.length, 2, 'completion control must not silently replace an owner');
        assert.equal(host.stats().pendingAdapterTasks, 0);
      });
    });
}
