import test from 'node:test';
import assert from 'node:assert/strict';
import { createNodeSourceCapabilities } from '../src/index.js';
import { createCapabilityClient, hostApiVersion } from '@meowfilm/source-runtime';

function deferred() {
  let resolve, reject;
  const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

function lateResponse(input) {
  return {
    statusCode: 200,
    statusMessage: 'OK',
    url: input.url,
    headers: { 'set-cookie': ['session=late; Path=/'] },
    body: Buffer.from('late'),
  };
}

for (const boundary of ['owner', 'client']) {
  for (const completion of ['response', 'error']) {
    test(`${boundary} fences late HTTP ${completion} after its deadline without settling I/O early`,
      { timeout: 5000 }, async () => {
        const entered = deferred();
        const release = deferred();
        let physicalPending = 0;
        const owner = createNodeSourceCapabilities({
          async network(input) {
            physicalPending++;
            entered.resolve(input);
            try { return await release.promise; }
            finally { physicalPending--; }
          },
        });
        const client = createCapabilityClient({
          invoke: (name, input, context) => owner.invoke(name, input, context),
        });
        const call = boundary === 'owner' ? owner.invoke.bind(owner) : client.call;
        const parent = new AbortController();
        const profile = 'source:deadline:1:http';
        await call('cookie.set', {
          version: hostApiVersion, profile, url: 'https://fixture.invalid/',
          values: ['session=current; Path=/'],
        });
        let settled = false;
        const operation = call('http.request', {
          version: hostApiVersion,
          url: 'https://fixture.invalid/deadline',
          method: 'GET',
          headers: {},
          timeoutMs: 20,
          redirects: 0,
          cookieProfile: profile,
        }, { signal: parent.signal });
        operation.then(() => { settled = true; }, () => { settled = true; });
        const rejected = assert.rejects(operation, { code: 'TIMEOUT' });
        const input = await entered.promise;
        try {
          await new Promise(resolve => {
            if (input.signal.aborted) resolve();
            else input.signal.addEventListener('abort', resolve, { once: true });
          });
          await new Promise(resolve => setImmediate(resolve));
          assert.equal(input.signal.reason.code, 'TIMEOUT');
          assert.equal(parent.signal.aborted, false);
          assert.equal(physicalPending, 1);
          assert.equal(settled, false);
          if (completion === 'error') release.reject(new Error('SYNTHETIC-PRIVATE-LATE-ERROR'));
          else release.resolve(lateResponse(input));
          await rejected;
          assert.equal(physicalPending, 0);
          assert.equal(settled, true);
          assert.equal(await call('cookie.get', {
            version: hostApiVersion, profile, url: 'https://fixture.invalid/',
          }), 'session=current');
        } finally {
          release.resolve(lateResponse(input));
          await operation.catch(() => {});
          await owner.releaseProfiles('source:deadline:');
        }
      });

    test(`${boundary} fences synchronous late HTTP ${completion} before its timeout timer runs`,
      { timeout: 5000 }, async () => {
        const entered = deferred();
        const release = deferred();
        const timeoutMs = 20;
        const blocker = new Int32Array(new SharedArrayBuffer(4));
        let physicalPending = 0;
        let completionState;
        const owner = createNodeSourceCapabilities({
          async network(input) {
            physicalPending++;
            entered.resolve(input);
            try {
              await release.promise;
              const began = Date.now();
              Atomics.wait(blocker, 0, 0, timeoutMs + 30);
              completionState = {
                elapsedMs: Date.now() - began,
                signalAborted: input.signal.aborted,
                physicalPending,
              };
              if (completion === 'error') throw new Error('SYNTHETIC-PRIVATE-LATE-ERROR');
              return lateResponse(input);
            } finally { physicalPending--; }
          },
        });
        const client = createCapabilityClient({
          invoke: (name, input, context) => owner.invoke(name, input, context),
        });
        const call = boundary === 'owner' ? owner.invoke.bind(owner) : client.call;
        const parent = new AbortController();
        const profile = 'source:synchronous:1:http';
        await call('cookie.set', {
          version: hostApiVersion, profile, url: 'https://fixture.invalid/',
          values: ['session=current; Path=/'],
        });
        let settled = false;
        const operation = call('http.request', {
          version: hostApiVersion,
          url: 'https://fixture.invalid/synchronous',
          method: 'GET',
          headers: {},
          timeoutMs,
          redirects: 0,
          cookieProfile: profile,
        }, { signal: parent.signal });
        operation.then(() => { settled = true; }, () => { settled = true; });
        const rejected = assert.rejects(operation, { code: 'TIMEOUT' });
        const input = await entered.promise;
        try {
          assert.equal(input.options.headers.cookie, 'session=current');
          assert.equal(physicalPending, 1);
          assert.equal(settled, false);
          release.resolve();
          await rejected;
          assert.ok(completionState.elapsedMs >= timeoutMs);
          assert.equal(completionState.signalAborted, false);
          assert.equal(completionState.physicalPending, 1);
          assert.equal(parent.signal.aborted, false);
          assert.equal(input.signal.reason.code, 'TIMEOUT');
          assert.equal(physicalPending, 0);
          assert.equal(settled, true);
          assert.equal(await call('cookie.get', {
            version: hostApiVersion, profile, url: 'https://fixture.invalid/',
          }), 'session=current');
        } finally {
          release.resolve();
          await operation.catch(() => {});
          await owner.releaseProfiles('source:synchronous:');
        }
      });
  }

  for (const retire of [false, true]) {
    test(`${boundary} fences cancelled HTTP cookies with ${retire ? 'retired' : 'retained'} profiles`,
      { timeout: 5000 }, async () => {
        const entered = deferred();
        const release = deferred();
        let physicalPending = 0;
        const owner = createNodeSourceCapabilities({
          async network(input) {
            physicalPending++;
            entered.resolve(input);
            try { return await release.promise; }
            finally { physicalPending--; }
          },
        });
        const client = createCapabilityClient({
          invoke: (name, input, context) => owner.invoke(name, input, context),
        });
        const call = boundary === 'owner' ? owner.invoke.bind(owner) : client.call;
        const parent = new AbortController();
        const profile = 'source:cancelled:1:http';
        const activeProfile = 'source:cancelled:2:http';
        for (const name of [profile, activeProfile]) {
          await call('cookie.set', {
            version: hostApiVersion, profile: name, url: 'https://fixture.invalid/',
            values: ['session=current; Path=/'],
          });
        }
        let settled = false;
        const operation = call('http.request', {
          version: hostApiVersion,
          url: 'https://fixture.invalid/cancelled',
          method: 'GET',
          headers: {},
          timeoutMs: 4000,
          redirects: 0,
          cookieProfile: profile,
        }, { signal: parent.signal });
        operation.then(() => { settled = true; }, () => { settled = true; });
        const rejected = assert.rejects(operation, { code: 'CANCELLED' });
        const input = await entered.promise;
        try {
          assert.equal(input.options.headers.cookie, 'session=current');
          parent.abort();
          assert.equal(input.signal.aborted, true);
          if (retire) await owner.releaseProfiles('source:cancelled:', 'source:cancelled:2');
          await new Promise(resolve => setImmediate(resolve));
          assert.equal(physicalPending, 1);
          assert.equal(settled, false);
          release.resolve(lateResponse(input));
          await rejected;
          assert.equal(physicalPending, 0);
          assert.equal(settled, true);
          assert.equal(await call('cookie.get', {
            version: hostApiVersion, profile, url: 'https://fixture.invalid/',
          }), retire ? '' : 'session=current');
          assert.equal(await call('cookie.get', {
            version: hostApiVersion, profile: activeProfile, url: 'https://fixture.invalid/',
          }), 'session=current');
        } finally {
          release.resolve(lateResponse(input));
          await operation.catch(() => {});
          await owner.releaseProfiles('source:cancelled:');
        }
      });
  }
}
