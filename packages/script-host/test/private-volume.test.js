import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PrivateVolume } from '../src/private-volume.js';

function fixture(t, onWrite = () => {}) {
  const root = fs.mkdtempSync(join(tmpdir(), 'meowfilm-volume-'));
  const volume = new PrivateVolume(root, 'fixture', onWrite, 'source-data');
  t.after(async () => {
    try { if (!volume.closed) await volume.close(); }
    finally { fs.rmSync(root, { recursive: true, force: true }); }
  });
  return { volume, api: volume.facade(), path: name => join(volume.root, name) };
}

function replaceFS(overrides) {
  const originals = Object.fromEntries(Object.keys(overrides).map(key => [key, fs[key]]));
  Object.assign(fs, overrides);
  syncBuiltinESMExports();
  return () => {
    Object.assign(fs, originals);
    syncBuiltinESMExports();
  };
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

const closed = stream => new Promise(resolve => stream.once('close', resolve));
const callbackResult = operation => new Promise((resolve, reject) =>
  operation(error => error ? reject(error) : resolve()));
const assertClosed = fd => assert.throws(() => fs.fstatSync(fd), { code: 'EBADF' });

test('private writes preserve bytes, encoding, append, flags and creation mode', async t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  api.writeFileSync('/data', '4142', { encoding: 'hex', flag: 'wx', mode: 0o640 });
  assert.deepEqual(fs.readFileSync(path('data')), Buffer.from('AB'));
  if (process.platform !== 'win32')
    assert.equal(fs.statSync(path('data')).mode & 0o777, 0o640 & ~process.umask());
  api.appendFileSync('/data', 'é', 'latin1');
  api.writeFileSync('/data', Buffer.from('C'), { flag: 'a' });
  assert.deepEqual(fs.readFileSync(path('data')), Buffer.from([65, 66, 233, 67]));
  assert.throws(() => api.writeFileSync('/data', 'lost', { flag: 'wx' }), { code: 'EEXIST' });
  api.writeFileSync('/data', 'Z', { flag: 'r+' });
  assert.deepEqual(fs.readFileSync(path('data')), Buffer.from([90, 66, 233, 67]));
  api.writeFileSync('/data', new DataView(Uint8Array.from([0, 120, 121, 0]).buffer, 1, 2), {
    flag: fs.constants.O_WRONLY | fs.constants.O_TRUNC,
  });
  assert.equal(fs.readFileSync(path('data'), 'utf8'), 'xy');
  assert.equal(writes, 5);
});

test('invalid write input never truncates or creates a file', t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  fs.writeFileSync(path('existing'), 'unchanged');
  const invalid = [
    [undefined], [null], [12], [{}], [new ArrayBuffer(2)],
    [{ toString: () => 'not write data' }],
    ['value', { encoding: 'invalid-encoding' }],
    [Buffer.from('value'), { encoding: 'invalid-encoding' }],
    ['value', { signal: null }],
    ['value', { flag: 'invalid-flag' }],
    ['value', { mode: 'invalid-mode' }],
  ];
  for (const args of invalid) {
    assert.throws(() => api.writeFileSync('/existing', ...args));
    assert.equal(fs.readFileSync(path('existing'), 'utf8'), 'unchanged');
    assert.throws(() => api.writeFileSync('/absent', ...args));
    assert.equal(fs.existsSync(path('absent')), false);
  }
  assert.equal(writes, 0);
});

test('descriptor writes preserve the current offset and caller ownership', t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  fs.writeFileSync(path('data'), 'abcdef');
  const id = api.openSync('/data', 'r+');
  const first = Buffer.alloc(2);
  assert.equal(api.readSync(id, first, 0, 2, null), 2);
  assert.equal(first.toString(), 'ab');
  assert.throws(() => api.writeFileSync(id, {}, { flag: 'w' }));
  api.writeFileSync(id, 'XY', { flag: 'w' });
  api.appendFileSync(id, 'Z');
  const last = Buffer.alloc(1);
  assert.equal(api.readSync(id, last, 0, 1, null), 1);
  assert.equal(last.toString(), 'f');
  assert.equal(fs.readFileSync(path('data'), 'utf8'), 'abXYZf');
  assert.equal(api.fstatSync(id).size, 6);
  api.closeSync(id);
  assert.equal(writes, 2);
  assert.throws(() => api.fstatSync(id), { code: 'EBADF' });
});

test('synchronous writes confirm persistence only after file sync and owned close', t => {
  const events = [];
  const { api, path } = fixture(t, () => events.push('receipt'));
  fs.writeFileSync(path('data'), 'old');
  const originalOpen = fs.openSync, originalSync = fs.fsyncSync, originalClose = fs.closeSync;
  let target;
  const restore = replaceFS({
    openSync(...args) {
      const fd = originalOpen(...args);
      if (args[0] === path('data')) target = fd;
      return fd;
    },
    fsyncSync(fd) {
      const result = originalSync(fd);
      if (fd === target) events.push('synced');
      return result;
    },
    closeSync(fd) {
      const result = originalClose(fd);
      if (fd === target) events.push('closed');
      return result;
    },
  });
  try {
    api.writeFileSync('/data', 'saved', { flush: false });
    assert.deepEqual(events, ['synced', 'closed', 'receipt']);
    assertClosed(target);
    assert.equal(fs.readFileSync(path('data'), 'utf8'), 'saved');
  } finally { restore(); }
});

test('callback and Promise writes preserve content and FileHandle ownership', async t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  let inline = true, observedInline;
  const writing = callbackResult(done => api.writeFile('/data', '4142', 'hex', error => {
    observedInline = inline;
    done(error);
  }));
  inline = false;
  await writing;
  assert.equal(observedInline, false);
  await callbackResult(done => api.appendFile('/data', 'C', done));
  await api.promises.writeFile('/data', 'D', { flag: 'a' });
  await api.promises.appendFile('/data', '45', 'hex');
  const handle = await api.promises.open('/data', 'r+');
  const prefix = Buffer.alloc(2);
  assert.equal((await handle.read(prefix, 0, 2, null)).bytesRead, 2);
  assert.equal(prefix.toString(), 'AB');
  await handle.writeFile('X');
  await handle.appendFile('Y');
  assert.equal((await handle.stat()).size, 5);
  await handle.close();
  assert.equal(fs.readFileSync(path('data'), 'utf8'), 'ABXYE');
  assert.equal(writes, 6);
});

test('failed synchronous, callback and Promise syncs close owned fds without receipts', async t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  const failure = Object.assign(new Error('file sync failed'), { code: 'EIO' });
  const originalOpen = fs.openSync, originalSync = fs.fsyncSync;
  const opened = [];
  const restore = replaceFS({
    openSync(...args) {
      const fd = originalOpen(...args);
      opened.push(fd);
      return fd;
    },
    fsyncSync(fd) {
      if (fs.fstatSync(fd).isFile()) throw failure;
      return originalSync(fd);
    },
  });
  try {
    assert.throws(() => api.writeFileSync('/sync', 'sync'), error => error === failure);
    assertClosed(opened.at(-1));
    await assert.rejects(callbackResult(done => api.writeFile('/callback', 'callback', done)),
      error => error === failure);
    assertClosed(opened.at(-1));
    await assert.rejects(api.promises.appendFile('/promise', 'promise'), error => error === failure);
    assertClosed(opened.at(-1));
    assert.equal(writes, 0);
    for (const name of ['sync', 'callback', 'promise'])
      assert.equal(fs.readFileSync(path(name), 'utf8'), name);
  } finally { restore(); }
});

test('failed descriptor sync preserves caller ownership until close and never confirms failure', async t => {
  let writes = 0;
  const { volume, api, path } = fixture(t, () => writes++);
  fs.writeFileSync(path('data'), 'original');
  const id = api.openSync('/data', 'r+'), fd = volume.fd(id);
  const failure = Object.assign(new Error('descriptor sync failed'), { code: 'EIO' });
  const originalSync = fs.fsyncSync;
  const restore = replaceFS({
    fsyncSync(current) {
      if (current === fd) throw failure;
      return originalSync(current);
    },
  });
  try {
    assert.throws(() => api.writeFileSync(id, 'updated!'), error => error === failure);
    assert.equal(api.fstatSync(id).size, 8);
    assert.equal(writes, 0);
    assert.throws(() => api.closeSync(id), error => error === failure);
    assertClosed(fd);
    assert.throws(() => api.fstatSync(id), { code: 'EBADF' });
    assert.equal(writes, 0);
    await assert.rejects(volume.close(), error => error === failure);
  } finally { restore(); }
});

test('write stream and volume close wait for real sync before closing and confirming', async t => {
  const events = [], syncing = deferred();
  const { volume, api, path } = fixture(t, () => events.push('receipt'));
  const originalSync = fs.fsync, originalClose = fs.close;
  let target, release;
  const restore = replaceFS({
    fsync(fd, callback) {
      target = fd;
      release = () => originalSync(fd, error => {
        if (!error) events.push('synced');
        callback(error);
      });
      syncing.resolve();
    },
    close(fd, callback) {
      originalClose(fd, error => {
        if (fd === target && !error) events.push('closed');
        callback(error);
      });
    },
  });
  let closing;
  try {
    const stream = api.createWriteStream('/data', {
      encoding: 'latin1', mode: 0o640, flush: false, autoClose: false, emitClose: false,
    });
    const completion = closed(stream);
    stream.end('é');
    await syncing.promise;
    assert.equal(stream.closed, false);
    assert.equal(fs.fstatSync(target).size, 1);
    assert.deepEqual(events, []);
    let finished = false;
    closing = volume.close().then(() => { finished = true; });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(finished, false);
    release(); release = null;
    await completion;
    await closing;
    assert.equal(stream.errored, null);
    assert.equal(stream.closed, true);
    assertClosed(target);
    assert.deepEqual(events, ['synced', 'closed', 'receipt']);
    assert.deepEqual(fs.readFileSync(path('data')), Buffer.from([233]));
    if (process.platform !== 'win32')
      assert.equal(fs.statSync(path('data')).mode & 0o777, 0o640 & ~process.umask());
  } finally {
    release?.();
    try { await closing; } finally { restore(); }
  }
});

test('write streams preserve append and start offsets and sync empty truncation', async t => {
  let writes = 0;
  const { api, path } = fixture(t, () => writes++);
  fs.writeFileSync(path('data'), 'abcd');
  for (const [options, value, expected] of [
    [{ flags: 'a' }, 'e', 'abcde'],
    [{ flags: 'r+', start: 1 }, 'XY', 'aXYde'],
    [{ flags: 'w' }, '', ''],
  ]) {
    const stream = api.createWriteStream('/data', options), completion = closed(stream);
    stream.end(value);
    await completion;
    assert.equal(stream.errored, null);
    assert.equal(fs.readFileSync(path('data'), 'utf8'), expected);
  }
  assert.equal(writes, 3);
});

test('failed stream sync still closes its fd and emits an error without a receipt', async t => {
  let writes = 0, target;
  const { volume, api, path } = fixture(t, () => writes++);
  const failure = Object.assign(new Error('stream sync failed'), { code: 'EIO' });
  const restore = replaceFS({
    fsync(fd, callback) {
      target = fd;
      queueMicrotask(() => callback(failure));
    },
  });
  try {
    const stream = api.createWriteStream('/data'), completion = closed(stream), errors = [];
    stream.on('error', error => errors.push(error));
    stream.end('written');
    await completion;
    assert.deepEqual(errors, [failure]);
    assert.equal(stream.errored, failure);
    assert.equal(stream.closed, true);
    assertClosed(target);
    assert.equal(writes, 0);
    assert.equal(fs.readFileSync(path('data'), 'utf8'), 'written');
    assert.equal(volume.streams.size, 0);
    assert.equal(volume.streamCleanup.size, 0);
    await volume.close();
  } finally { restore(); }
});

test('private files reject guest fd and fs injection without taking host ownership', t => {
  let writes = 0, injected = false;
  const { api, path } = fixture(t, () => writes++);
  fs.writeFileSync(path('data'), 'unchanged');
  const fd = fs.openSync(path('data'), 'r+');
  const customFS = { open() { injected = true; }, close() { injected = true; } };
  try {
    for (const operation of ['createReadStream', 'createWriteStream']) {
      assert.throws(() => api[operation]('/data', { fd }), { code: 'EPERM' });
      assert.throws(() => api[operation]('/data', { fs: customFS }), { code: 'EPERM' });
    }
    assert.throws(() => api.writeFileSync(fd, 'lost'), { code: 'EBADF' });
    assert.throws(() => api.writeFileSync('/source.js', 'lost'), { code: 'EPERM' });
    assert.throws(() => api.createWriteStream('/source.js'), { code: 'EPERM' });
    assert.equal(injected, false);
    assert.equal(writes, 0);
    assert.equal(fs.fstatSync(fd).size, 9);
    assert.equal(fs.readFileSync(path('data'), 'utf8'), 'unchanged');
  } finally { fs.closeSync(fd); }
});

test('native private streams keep relative paths and close their actual descriptor', async t => {
  const { api, path } = fixture(t);
  api.mkdirSync('relative');
  const output = api.createWriteStream('relative/native.bin');
  const closed = new Promise((resolve, reject) => {
    output.once('error', reject);
    output.once('close', resolve);
  });
  output.end(Buffer.from([0, 255, 17, 128]));
  assert.equal(output.path, 'relative/native.bin');
  await closed;
  assert.equal(output.closed, true);
  assert.deepEqual(fs.readFileSync(path('relative/native.bin')), Buffer.from([0, 255, 17, 128]));
  const input = api.createReadStream('relative/native.bin');
  const chunks = [];
  for await (const chunk of input) chunks.push(chunk);
  assert.deepEqual(Buffer.concat(chunks), Buffer.from([0, 255, 17, 128]));
  assert.equal(input.path, 'relative/native.bin');
  assert.equal(input.closed, true);
});

test('private native stream fd mutation cannot select or close a volume-external descriptor', async t => {
  const { volume, api, path } = fixture(t);
  const sentinel = join(volume.root, '..', 'external-sentinel.txt');
  fs.writeFileSync(sentinel, 'OUTSIDE');
  for (const kind of ['read', 'write']) {
    api.writeFileSync('/inside.txt', 'INSIDE');
    const outside = fs.openSync(sentinel, 'r+');
    let owned, assignment, redefinition, detached, chunks = [];
    try {
      const stream = kind === 'write' ? api.createWriteStream('/inside.txt')
        : api.createReadStream('/inside.txt');
      const completion = closed(stream), errors = [];
      stream.on('error', error => errors.push(error));
      stream.once('open', fd => {
        owned = fd;
        try { stream.fd = outside; } catch (error) { assignment = error.code; }
        try { Object.defineProperty(stream, 'fd', { value: outside }); }
        catch (error) { redefinition = error.name; }
        try { stream.fd = null; } catch (error) { detached = error.code; }
        if (kind === 'write') stream.end('OWNED');
      });
      if (kind === 'read') stream.on('data', chunk => chunks.push(chunk));
      await completion;
      assert.equal(assignment, 'EPERM'); assert.equal(redefinition, 'TypeError');
      assert.equal(detached, 'EPERM');
      assert.deepEqual(errors, []);
      assert.equal(fs.readFileSync(sentinel, 'utf8'), 'OUTSIDE');
      assert.equal(fs.fstatSync(outside).size, 7);
      assertClosed(owned);
      assert.equal(stream.fd, null);
      assert.equal(fs.readFileSync(path('inside.txt'), 'utf8'), kind === 'write' ? 'OWNED' : 'INSIDE');
      if (kind === 'read') assert.equal(Buffer.concat(chunks).toString(), 'INSIDE');
      assert.equal(volume.streams.size, 0); assert.equal(volume.streamCleanup.size, 0);
    } finally {
      for (const fd of [owned, outside]) {
        if (fd === undefined) continue;
        try { fs.closeSync(fd); } catch (error) { if (error.code !== 'EBADF') throw error; }
      }
    }
  }
});

test('private volume close survives cross-stream listener removal and releases only after real dispatch',
  { timeout: 30000 }, async t => {
    const { volume, api, path } = fixture(t);
    fs.writeFileSync(path('x'), 'X');
    fs.writeFileSync(path('y'), 'Y');
    const x = api.createReadStream('/x'), y = api.createReadStream('/y');
    const opened = value => new Promise((resolve, reject) => {
      value.once('open', resolve);
      value.once('error', reject);
    });
    const [xFd, yFd] = await Promise.all([opened(x), opened(y)]);
    const originalClose = fs.close;
    let releaseY, yCloseCalls = 0;
    const restore = replaceFS({
      close(fd, callback) {
        if (fd !== yFd) return originalClose(fd, callback);
        yCloseCalls++;
        releaseY = () => {
          releaseY = null;
          originalClose(fd, callback);
        };
      },
    });
    const owned = value => volume.streams.has(value) && volume.streamCleanup.has(value);
    let guestY = 0, rawX = 0, rawY = 0, settled = false, removal;
    const observations = [];
    const removed = () => { guestY++; };
    y.on('close', removed);
    x.on('close', () => {
      rawX++;
      observations.push({ name: 'x', owned: owned(x) });
      removal = { yOwned: owned(y), yClosed: y.closed, settled, yCloseCalls,
        returned: y.removeAllListeners('close') === y,
        removed: !y.listeners('close').includes(removed) };
      // A fresh public observer is deliberate; the removed callback stays gone.
      // The underlying fs.close is only delayed, never replaced with fake completion.
      y.on('close', () => {
        rawY++;
        observations.push({ name: 'y', owned: owned(y), xOwned: owned(x) });
      });
      releaseY?.();
    });
    let closing;
    try {
      closing = volume.close().then(() => { settled = true; });
      assert.equal(settled, false);
      assert.equal(owned(x), true);
      assert.equal(owned(y), true);
      assert.equal(y.closed, false);
      assert.equal(fs.fstatSync(yFd).size, 1);
      await closing;
      assert.deepEqual({ rawX, rawY, guestY, settled, yCloseCalls },
        { rawX: 1, rawY: 1, guestY: 0, settled: true, yCloseCalls: 1 });
      assert.deepEqual(removal, { yOwned: true, yClosed: false, settled: false,
        yCloseCalls: 1, returned: true, removed: true });
      assert.deepEqual(observations, [
        { name: 'x', owned: true }, { name: 'y', owned: true, xOwned: false },
      ]);
      assertClosed(xFd);
      assertClosed(yFd);
      for (const value of [x, y]) {
        assert.equal(value.closed, true);
        assert.equal(owned(value), false);
        assert.equal(Object.hasOwn(value, 'emit'), false);
      }
      assert.equal(volume.streams.size, 0);
      assert.equal(volume.streamCleanup.size, 0);
      assert.equal(volume.failure, null);
    } finally {
      releaseY?.();
      try { await closing; } finally { restore(); }
    }
  });
