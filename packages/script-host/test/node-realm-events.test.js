import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { SCRIPT_LIMITS } from '../src/index.js';
import { CatSession } from '../src/cat-session.js';

async function sessionFor(t, source) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'meowfilm-node-realm-events-'));
  const session = new CatSession({ dataRoot, installationId: 'events', generation: 1,
    revision: { source, version: '1', config: {} },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 1000 } });
  t.after(async () => {
    try { await session.stop(); }
    finally { rmSync(dataRoot, { recursive: true, force: true }); }
  });
  await session.start();
  session.activate();
  return session;
}

const constructorKey = Symbol.for('meowfilm.test.node-realm-events.CustomEvent');
const source = `
  'use strict';
  module.exports.home = () => {
    const detail = { value: 7 };
    const custom = new CustomEvent('custom', { detail,
      bubbles: true, cancelable: true, composed: true });
    let assignmentError = null;
    try { custom.detail = { value: 99 }; }
    catch (error) { assignmentError = error.name; }
    const target = new EventTarget();
    let received = null;
    target.addEventListener('custom', event => {
      received = { sameEvent: event === custom, sameDetail: event.detail === detail,
        target: event.target === target, currentTarget: event.currentTarget === target };
      event.detail.value = 8;
      event.preventDefault();
    });
    const dispatched = target.dispatchEvent(custom);
    const defaults = new CustomEvent('defaults');
    target.addEventListener('defaults', event => event.preventDefault());
    const defaultDispatched = target.dispatchEvent(defaults);
    return {
      hostConstructor: CustomEvent === Event[Symbol.for('meowfilm.test.node-realm-events.CustomEvent')],
      nativeEvent: custom instanceof Event,
      customEvent: custom instanceof CustomEvent,
      flags: { bubbles: custom.bubbles, cancelable: custom.cancelable, composed: custom.composed },
      dispatched, prevented: custom.defaultPrevented, received,
      defaults: { detail: defaults.detail, bubbles: defaults.bubbles,
        cancelable: defaults.cancelable, composed: defaults.composed,
        dispatched: defaultDispatched, prevented: defaults.defaultPrevented },
      detail: { sameReference: custom.detail === detail, value: detail.value, assignmentError,
        empty: new CustomEvent('empty', {}).detail,
        undefined: new CustomEvent('undefined', { detail: undefined }).detail,
        falsy: [false, 0, ''].map(detail => new CustomEvent('falsy', { detail }).detail) },
      tag: custom[Symbol.toStringTag],
      stringTag: Object.prototype.toString.call(custom),
    };
  };
`;
const expectedEvents = {
  nativeEvent: true,
  customEvent: true,
  flags: { bubbles: true, cancelable: true, composed: true },
  dispatched: false, prevented: true,
  received: { sameEvent: true, sameDetail: true, target: true, currentTarget: true },
  defaults: { detail: null, bubbles: false, cancelable: false, composed: false,
    dispatched: true, prevented: false },
  detail: { sameReference: true, value: 8, assignmentError: 'TypeError',
    empty: null, undefined: null, falsy: [false, 0, ''] },
  tag: 'CustomEvent',
  stringTag: '[object CustomEvent]',
};

test('Cat CustomEvent preserves native identity or uses a realm-local native Event subclass',
  { timeout: 30000 }, async t => {
    const hostDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'CustomEvent');
    const NativeCustomEvent = globalThis.CustomEvent;
    assert.equal(typeof NativeCustomEvent, 'function', 'native branch uses the normal Node 24 test host');
    const markerDescriptor = Object.getOwnPropertyDescriptor(Event, constructorKey);
    // A temporary native Event property proves constructor identity through
    // public script results, without a production test injection API.
    Object.defineProperty(Event, constructorKey, { value: NativeCustomEvent, configurable: true });
    try {
      await t.test('keeps the host constructor and native event behavior', async t => {
        const session = await sessionFor(t, source);
        assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
          { hostConstructor: true, ...expectedEvents });
        assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, 'CustomEvent'), hostDescriptor);
        await session.stop();
        assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, 'CustomEvent'), hostDescriptor);
      });
      await t.test('keeps CustomEvent missing on the host while providing full event behavior', async t => {
        assert.equal(Reflect.deleteProperty(globalThis, 'CustomEvent'), true);
        try {
          const session = await sessionFor(t, source);
          assert.equal(Object.hasOwn(globalThis, 'CustomEvent'), false);
          assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
            { hostConstructor: false, ...expectedEvents });
          assert.equal(Object.hasOwn(globalThis, 'CustomEvent'), false);
          await session.stop();
          assert.equal(Object.hasOwn(globalThis, 'CustomEvent'), false);
        } finally { Object.defineProperty(globalThis, 'CustomEvent', hostDescriptor); }
      });
    } finally {
      if (markerDescriptor) Object.defineProperty(Event, constructorKey, markerDescriptor);
      else delete Event[constructorKey];
    }
    assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, 'CustomEvent'), hostDescriptor);
  });
