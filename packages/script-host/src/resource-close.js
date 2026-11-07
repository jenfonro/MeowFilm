import { fault } from './errors.js';

const ignoreError = () => {};
const closeDispatches = new WeakMap();

/** Retire native event owners only after every completion listener has run.
 * A listener cannot provide this ordering: later listeners may hold leases
 * that retirement removes. The per-emitter adapter is removed with its owner. */
export function afterCloseDispatch(target, completed, closeEvent = 'close') {
  const name = typeof closeEvent === 'symbol' ? closeEvent : String(closeEvent);
  let state = closeDispatches.get(target);
  if (!state) {
    const reference = new WeakRef(target);
    const descriptor = Object.getOwnPropertyDescriptor(target, 'emit');
    const emit = target.emit;
    state = { callbacks: new Map(), reference, descriptor, dispatch: null };
    state.dispatch = function(event, ...args) {
      try { return Reflect.apply(emit, this, [event, ...args]); }
      finally {
        const callbacks = state.callbacks.get(typeof event === 'symbol' ? event : String(event));
        if (callbacks && this === reference.deref())
          for (const callback of [...callbacks]) callback();
      }
    };
    Object.defineProperty(target, 'emit', {
      configurable: true, writable: true, value: state.dispatch,
    });
    closeDispatches.set(target, state);
  }
  let callbacks = state.callbacks.get(name);
  if (!callbacks) state.callbacks.set(name, callbacks = new Set());
  callbacks.add(completed);
  let active = true;
  return () => {
    if (!active) return;
    active = false;
    callbacks.delete(completed);
    if (!callbacks.size) state.callbacks.delete(name);
    if (state.callbacks.size) return;
    const target = state.reference.deref();
    if (!target) return;
    closeDispatches.delete(target);
    if (target.emit !== state.dispatch) return;
    if (state.descriptor) Object.defineProperty(target, 'emit', state.descriptor);
    else delete target.emit;
  };
}

/** Stop an owned resource and wait for its actual close event.
 *
 * `stop` must begin termination; it may be called once. `release` runs when
 * the resource has closed or when the timeout detaches it from its owner.
 * A timed-out resource keeps only a no-op error guard so a late native error
 * cannot crash the host; it does not retain the owner. */
export function closeResource(target, { stop, release, timeoutMs = 3000,
  closeEvent = 'close', closed = value => value.closed } = {}) {
  if (!target) return Promise.resolve({ complete: true, error: null, timedOut: false, guarded: false });
  let error = null, done = false, timer, stopping = false, closeObserved = false;
  const canObserve = typeof target.emit === 'function';
  let detachClosed = null, errorAttached = false;
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  const finish = (complete, timedOut = false) => {
    if (done) return;
    done = true;
    clearTimeout(timer);
    try { detachClosed?.(); } catch (failure) { error ??= failure; }
    detachClosed = null;
    try { release?.(); } catch (failure) { error ??= failure; }
    if (errorAttached) {
      try { target.off?.('error', ignoreError); } catch (failure) { error ??= failure; }
    }
    // Release may remove all listeners. Install the detached guard afterwards
    // and clear every callback that could retain the resource owner.
    const guard = complete ? null : guardDetachedEmitter(target);
    if (guard?.error) error ??= guard.error;
    stop = null;
    release = null;
    closed = null;
    resolve({ complete, error, timedOut, guarded: typeof guard === 'function' });
  };
  const observed = () => {
    closeObserved = true;
    if (!stopping) finish(true);
  };
  let alreadyClosed = false;
  try { alreadyClosed = Boolean(closed(target)); } catch (failure) { error ??= failure; }
  if (alreadyClosed) { finish(true); return promise; }
  if (typeof target.on === 'function') {
    try { target.on('error', ignoreError); errorAttached = true; } catch (failure) { error ??= failure; }
  }
  if (canObserve && closeEvent) {
    // This host barrier must not enter a managed emitter's guest listener set.
    try { detachClosed = afterCloseDispatch(target, observed, closeEvent); }
    catch (failure) { error ??= failure; }
  }
  stopping = true;
  try { stop?.(); } catch (failure) { error ??= failure; }
  stopping = false;
  if (closeObserved) finish(true);
  else if (!detachClosed) {
    try { if (closed(target)) finish(true); } catch (failure) { error ??= failure; }
  }
  if (!done) timer = setTimeout(() => {
    error ??= fault('TIMEOUT');
    finish(false, true);
  }, timeoutMs);
  return promise;
}

/** Keep an already-detached native emitter from raising an unhandled error. */
export function guardDetachedEmitter(target) {
  if (!target || typeof target.on !== 'function') return null;
  try {
    target.on('error', ignoreError);
    return () => {
      try { target.off?.('error', ignoreError); } catch { /* The emitter is detached. */ }
    };
  } catch (error) {
    return { error };
  }
}
