import test from 'node:test';
import assert from 'node:assert/strict';
import stream from 'node:stream';
import { inherits } from 'node:util';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CatSession } from '../src/cat-session.js';
import { SCRIPT_LIMITS } from '../src/host.js';

const streamTypes = ['Readable', 'Writable', 'Duplex', 'Transform', 'PassThrough'];

async function sessionFor(t, installationId, source) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'meowfilm-cat-stream-'));
  const session = new CatSession({ dataRoot, installationId, generation: 1,
    revision: { source, version: '1', config: {}, permissions: { storage: [] } },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 60_000,
      requestTimeoutMs: 60_000, cleanupTimeoutMs: 3_000 } });
  t.after(async () => {
    try { await session.stop(); }
    finally { rmSync(dataRoot, { recursive: true, force: true }); }
  });
  await session.start();
  session.activate();
  return session;
}

const invoke = (session, operation) =>
  session.invoke({ siteId: 'default', operation, payload: {} });

function assertOwnedStreams(session, expected) {
  assert.equal(new Set(expected).size, expected.length, 'expected streams have distinct identities');
  assert.equal(session.node.streams.size, expected.length, 'complete owned stream set');
  for (const value of expected) {
    assert.equal(session.node.streams.has(value), true, 'same owned stream');
    assert.equal(session.node.resourceCleanup.has(value), true, 'same cleanup owner');
  }
}

function observeStandardStreams(session) {
  const baseline = ['stdin', 'stdout', 'stderr']
    .map(name => ({ name, value: session.node.process[name], closes: 0 }));
  assertOwnedStreams(session, baseline.map(entry => entry.value));
  for (const entry of baseline) {
    assert.equal(entry.value.destroyed, false, entry.name);
    assert.equal(entry.value.closed, false, entry.name);
    entry.value.on('close', () => { entry.closes++; });
  }
  return baseline;
}

function assertStandardStreamsClosed(session, baseline) {
  for (const { name, value, closes } of baseline) {
    assert.equal(value.destroyed, true, 'destroyed ' + name);
    assert.equal(value.closed, true, 'closed ' + name);
    assert.equal(closes, 1, 'close count ' + name);
    assert.equal(session.node.streams.has(value), false, name);
    assert.equal(session.node.resourceCleanup.has(value), false, name);
  }
}

// Run the same public operations against Node and inside the actual Cat loader.
// Node supplies the call/instanceof oracle, including Writable's Duplex rule.
async function describeStream(Stream, inherits) {
  'use strict';
  class Child extends Stream {}
  function Legacy() { this.initialized = Stream.call(this) === undefined; }
  inherits(Legacy, Stream);
  const base = new Stream(), child = new Child(), legacy = new Legacy();
  let emitted;
  legacy.once('value', value => { emitted = value; });
  legacy.emit('value', 'native-event');
  const receiver = {};
  const receiverResult = Stream.call(receiver);
  let called;
  try {
    const result = Stream();
    called = { type: typeof result, instance: result instanceof Stream };
  } catch (error) { called = { error: error.name }; }
  const names = ['Readable', 'Writable', 'Duplex', 'Transform', 'PassThrough'];
  const instances = [], rows = [];
  class WritableChild extends Stream.Writable {}
  try {
    for (const name of names) {
      const Native = Stream[name];
      class Derived extends Native {}
      function Traditional(options) { Native.call(this, options); }
      inherits(Traditional, Native);
      const options = { read() {}, write(_chunk, _encoding, done) { done(); },
        transform(chunk, _encoding, done) { done(null, chunk); } };
      const called = Native(options);
      const values = [new Native(options), called, new Derived(options), new Traditional(options)];
      instances.push(...values);
      rows.push(values.map(value => ({
        stream: value instanceof Stream,
        types: names.filter(type => value instanceof Stream[type]),
        derived: value instanceof Derived,
        traditional: value instanceof Traditional,
        writableChild: value instanceof WritableChild,
      })));
    }
    return { type: typeof Stream, keys: Object.keys(Stream), alias: Stream.Stream === Stream,
      base: base instanceof Stream, child: child instanceof Stream && child instanceof Child,
      legacy: legacy instanceof Stream && legacy instanceof Legacy && legacy.initialized,
      emitted, receiver: receiverResult === undefined && receiver._events !== undefined,
      called, rows };
  } finally {
    await Promise.all(instances.map(value => new Promise(resolve => {
      value.once('close', resolve);
      value.destroy();
    })));
  }
}

test('REV-1A Cat CommonJS stream matches native construction, calls, inheritance and statics',
  { timeout: 30000 }, async t => {
    const originals = [stream, ...streamTypes.map(name => stream[name])];
    const descriptors = originals.map(value => Object.getOwnPropertyDescriptors(value));
    const staticValue = stream.isReadable(null);
    const source = `
      const Stream = require('stream');
      module.exports.probe = async () => ({
        shape: await (${describeStream.toString()})(Stream, require('util').inherits),
        aliases: ['stream', 'node:stream', 'readable-stream', 'readable-stream/lib/stream.js']
          .every(name => require(name) === Stream && require(name).Stream === Stream &&
            ['Readable', 'Writable', 'Duplex', 'Transform', 'PassThrough']
              .every(type => require(name)[type] === Stream[type])),
        promises: Stream.promises === require('stream/promises') &&
          Stream.promises === require('node:stream/promises'),
        pipeline: typeof Stream.pipeline,
        finished: typeof Stream.finished,
        highWaterMark: Stream.getDefaultHighWaterMark(false),
      });
      module.exports.bytes = async () => {
        const chunks = [];
        await Stream.promises.pipeline(Stream.Readable.from(['cat-', 'stream']),
          new Stream.PassThrough(),
          new Stream.Writable({ write(chunk, _encoding, done) {
            chunks.push(Buffer.from(chunk)); done();
          } }));
        return { text: Buffer.concat(chunks).toString() };
      };
      module.exports.replaceStatic = () => {
        Stream.isReadable = () => 'local';
        return { local: Stream.isReadable(null) };
      };
      module.exports.staticValue = () => ({ local: Stream.isReadable(null) });
    `;
    const first = await sessionFor(t, 'stream-first', source);
    const firstBaseline = observeStandardStreams(first);
    const second = await sessionFor(t, 'stream-second', source);
    const secondBaseline = observeStandardStreams(second);
    const expected = { shape: await describeStream(stream, inherits), aliases: true,
      promises: true, pipeline: 'function', finished: 'function',
      highWaterMark: stream.getDefaultHighWaterMark(false) };
    assert.equal(expected.shape.type, 'function');
    assert.equal(expected.shape.alias, true);
    assert.equal(expected.shape.base, true);
    assert.equal(expected.shape.child, true);
    assert.equal(expected.shape.legacy, true);
    assert.deepEqual(await invoke(first, 'probe'), expected);
    assert.deepEqual(await invoke(second, 'probe'), expected);
    assert.deepEqual(await invoke(first, 'bytes'), { text: 'cat-stream' });
    assert.deepEqual(await invoke(first, 'replaceStatic'), { local: 'local' });
    assert.deepEqual(await invoke(second, 'staticValue'), { local: staticValue });
    assertOwnedStreams(first, firstBaseline.map(entry => entry.value));
    assertOwnedStreams(second, secondBaseline.map(entry => entry.value));
    await first.stop();
    await second.stop();
    assertStandardStreamsClosed(first, firstBaseline);
    assertStandardStreamsClosed(second, secondBaseline);
    assert.equal(first.node.streams.size, 0);
    assert.equal(second.node.streams.size, 0);
    assert.equal(first.node.resourceCleanup.size, 0);
    assert.equal(second.node.resourceCleanup.size, 0);
    assert.equal(first.cleanupError, undefined);
    assert.equal(second.cleanupError, undefined);
    for (const [index, original] of originals.entries())
      assert.deepEqual(Object.getOwnPropertyDescriptors(original), descriptors[index],
        index === 0 ? 'host Stream statics' : 'host ' + streamTypes[index - 1] + ' statics');
  });

test('REV-1A Cat CommonJS streams stay with one instance until their real close',
  { timeout: 30000 }, async t => {
    const session = await sessionFor(t, 'stream-owner', `
      const Stream = require('stream'), { inherits } = require('util');
      const retained = [];
      class Derived extends Stream.PassThrough {}
      function Legacy(options) { Stream.Writable.call(this, options); }
      inherits(Legacy, Stream.Writable);
      module.exports.hold = () => {
        if (!retained.length) {
          const options = { read() {}, write(_chunk, _encoding, done) { done(); },
            transform(chunk, _encoding, done) { done(null, chunk); } };
          retained.push(new Stream.Readable(options), Stream.Writable(options),
            new Stream.Duplex(options), new Stream.Transform(options),
            new Stream.PassThrough(), new Legacy(options),
            Stream.Readable.from(['pending']), new Derived({
              destroy(error, done) {
                this.destroyCalls = (this.destroyCalls || 0) + 1;
                if (this.deferClose) this.finishDestroy = () => done(error);
                else done(error);
              },
            }));
          retained.forEach((value, index) => {
            value.fixtureIndex = index;
            value.closeCount = 0;
            value.once('close', () => { value.closeCount++; });
          });
        }
        return retained.map(value => ({ index: value.fixtureIndex,
          destroyed: value.destroyed, closed: value.closed, closes: value.closeCount }));
      };
      module.exports.release = () => new Promise(resolve => {
        retained[0].once('close', () => resolve({ closed: retained[0].closed }));
        retained[0].destroy();
      });
    `);
    const standards = observeStandardStreams(session);
    const baseline = new Set(standards.map(entry => entry.value));
    const open = Array.from({ length: 8 }, (_, index) =>
      ({ index, destroyed: false, closed: false, closes: 0 }));
    assert.deepEqual(await invoke(session, 'hold'), open);
    const held = [...session.node.streams.keys()].filter(value => !baseline.has(value))
      .sort((a, b) => a.fixtureIndex - b.fixtureIndex);
    assert.equal(held.length, open.length);
    assert.deepEqual(held.map(value => value.fixtureIndex), open.map(value => value.index));
    assertOwnedStreams(session, [...baseline, ...held]);
    const gated = held.at(-1);
    assert.deepEqual(await invoke(session, 'hold'), open);
    const remaining = [...session.node.streams.keys()].filter(value => !baseline.has(value))
      .sort((a, b) => a.fixtureIndex - b.fixtureIndex);
    assert.equal(remaining.length, held.length);
    remaining.forEach((value, index) => assert.equal(value, held[index]));
    assertOwnedStreams(session, [...baseline, ...held]);
    assert.deepEqual(await invoke(session, 'release'), { closed: true });
    assert.equal(held[0].destroyed, true);
    assert.equal(held[0].closed, true);
    assert.equal(held[0].closeCount, 1);
    assert.equal(session.node.streams.has(held[0]), false);
    assert.equal(session.node.resourceCleanup.has(held[0]), false);
    assertOwnedStreams(session, [...baseline, ...held.slice(1)]);
    let settled = false;
    gated.deferClose = true;
    const stopped = session.stop();
    stopped.then(() => { settled = true; }, () => { settled = true; });
    try {
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(gated.destroyed, true);
      assert.equal(gated.closed, false);
      assert.equal(gated.destroyCalls, 1);
      assert.equal(session.node.streams.has(gated), true);
      assert.equal(session.node.resourceCleanup.has(gated), true);
      assert.equal(settled, false, 'stop must await the actual close callback');
    } finally { gated.finishDestroy?.(); }
    await stopped;
    for (const value of held) {
      assert.equal(value.destroyed, true, 'destroyed ' + value.fixtureIndex);
      assert.equal(value.closed, true, 'closed ' + value.fixtureIndex);
      assert.equal(value.closeCount, 1, 'close count ' + value.fixtureIndex);
      assert.equal(session.node.streams.has(value), false);
      assert.equal(session.node.resourceCleanup.has(value), false);
    }
    assertStandardStreamsClosed(session, standards);
    assert.equal(session.node.streams.size, 0);
    assert.equal(session.node.resourceCleanup.size, 0);
    assert.equal(session.cleanupError, undefined);
  });
