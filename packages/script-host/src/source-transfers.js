import { createHash, randomUUID } from 'node:crypto';
import { fault, requireValue } from './errors.js';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
/** A frame bound is not a source bound. Staging retains received chunks rather
 * than reserving the caller's claimed whole-source length before transfer. */
export class SourceTransfers {
  constructor({ clock = Date.now, ttl = 300000,
    setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
    this.clock = clock; this.ttl = ttl; this.setTimer = setTimer; this.clearTimer = clearTimer;
    this.entries = new Map(); this.closed = false; this.chunkBytes = 64 * 1024;
    this.timerGeneration = 0; this.failure = null;
    this.timers = new Set(); this.timerFailures = new Map();
  }
  deactivateTimer(record) {
    if (!record?.active) return;
    record.active = false;
    this.timers.delete(record);
    this.timerFailures.delete(record);
    if (this.timer === record) this.timer = undefined;
  }
  cancelTimer(record) {
    if (!record?.active) return true;
    try {
      this.clearTimer(record.handle);
      this.deactivateTimer(record);
      return true;
    } catch (error) {
      this.timerFailures.set(record, error);
      return false;
    }
  }
  createTimer(generation, delay) {
    const record = {
      generation, handle: undefined, active: true, armed: false, fired: false,
    };
    const ownerRef = new WeakRef(this);
    const fire = () => {
      if (!record.armed) { record.fired = true; return; }
      if (!record.active) return;
      const owner = ownerRef.deref();
      if (!owner) { record.active = false; return; }
      owner.deactivateTimer(record);
      if (generation !== owner.timerGeneration || owner.closed) return;
      try { owner.sweep(); }
      catch (error) {
        owner.failure ??= error;
        for (const token of [...owner.entries.keys()]) {
          try { owner.release(token, false); }
          catch (cleanupError) { owner.failure ??= cleanupError; }
        }
      }
    };
    this.timers.add(record);
    try {
      record.handle = this.setTimer(fire, delay);
      record.armed = true;
    } catch (error) {
      this.deactivateTimer(record);
      throw error;
    }
    try { record.handle?.unref?.(); } catch { /* The tracked timer still owns expiry. */ }
    if (record.fired) queueMicrotask(fire);
    return record;
  }
  schedule() {
    const generation = this.timerGeneration + 1;
    let next;
    if (!this.closed && this.entries.size) {
      let expires = Infinity;
      for (const entry of this.entries.values()) expires = Math.min(expires, entry.expires);
      next = this.createTimer(generation, Math.max(1, expires - this.clock()));
    }
    const previous = this.timer;
    this.timerGeneration = generation;
    this.timer = next;
    if (previous && previous !== next) this.cancelTimer(previous);
    for (const record of [...this.timers])
      if (record !== this.timer) this.cancelTimer(record);
  }
  sweep() {
    requireValue(!this.closed, 'HOST_CLOSED');
    const now = this.clock();
    for (const [token, entry] of this.entries)
      if (entry.expires <= now) this.release(token, false);
    this.schedule();
  }
  begin({ length, sha256, kind = 'source' }) {
    this.sweep();
    requireValue(Number.isSafeInteger(length) && length > 0 &&
      /^[a-f0-9]{64}$/.test(sha256) && ['source', 'json'].includes(kind));
    const token = randomUUID();
    this.entries.set(token, { kind, length, sha256, offset: 0, chunks: [], expires: this.clock() + this.ttl });
    try { this.schedule(); }
    catch (error) { this.release(token, false); throw error; }
    return { token, chunkBytes: this.chunkBytes };
  }
  entry(token) {
    this.sweep(); const entry = this.entries.get(token);
    requireValue(entry, 'NOT_FOUND'); return entry;
  }
  chunk({ token, offset, data }) {
    const entry = this.entry(token);
    requireValue(entry.kind !== 'result' && offset === entry.offset &&
      typeof data === 'string' && data.length <= Math.ceil(this.chunkBytes / 3) * 4);
    const bytes = Buffer.from(data, 'base64');
    let retained = false;
    try {
      requireValue(bytes.length > 0 && bytes.length <= this.chunkBytes &&
        bytes.toString('base64') === data && offset + bytes.length <= entry.length);
      const previousExpires = entry.expires;
      entry.chunks.push(bytes); entry.offset += bytes.length;
      entry.expires = this.clock() + this.ttl;
      try { this.schedule(); }
      catch (error) {
        entry.chunks.pop();
        entry.offset -= bytes.length;
        entry.expires = previousExpires;
        throw error;
      }
      retained = true;
      return { next: entry.offset };
    } finally { if (!retained) bytes.fill(0); }
  }
  take(token, kind) {
    const entry = this.entry(token);
    requireValue(entry.kind === kind && entry.offset === entry.length);
    const bytes = Buffer.concat(entry.chunks);
    try { this.release(token); }
    catch (error) { bytes.fill(0); throw error; }
    if (digest(bytes) !== entry.sha256) { bytes.fill(0); throw fault('SCRIPT_PROTOCOL_ERROR'); }
    return bytes;
  }
  result(value) {
    this.sweep();
    const bytes = Buffer.from(JSON.stringify(value));
    if (bytes.length <= this.chunkBytes) {
      const data = bytes.toString('utf8');
      bytes.fill(0);
      return { data };
    }
    const token = randomUUID(), sha256 = digest(bytes);
    this.entries.set(token, { kind: 'result', bytes, length: bytes.length, sha256,
      expires: this.clock() + this.ttl, chunks: [] });
    try { this.schedule(); }
    catch (error) { this.release(token, false); throw error; }
    return { blob: { token, length: bytes.length, sha256, chunkBytes: this.chunkBytes } };
  }
  read({ token, offset }) {
    const entry = this.entry(token);
    requireValue(entry.kind === 'result' && Number.isSafeInteger(offset) && offset >= 0 && offset < entry.length);
    const previousExpires = entry.expires;
    entry.expires = this.clock() + this.ttl;
    try { this.schedule(); }
    catch (error) { entry.expires = previousExpires; throw error; }
    const bytes = entry.bytes.subarray(offset, offset + this.chunkBytes);
    return { data: bytes.toString('base64'), next: offset + bytes.length };
  }
  release(token, reschedule = true) {
    const entry = this.entries.get(token);
    if (!entry) return;
    entry.bytes?.fill(0); for (const chunk of entry.chunks) chunk.fill(0);
    this.entries.delete(token);
    if (reschedule) this.schedule();
  }
  close() {
    this.closed = true;
    this.timerGeneration++;
    let failure = this.failure;
    this.timer = undefined;
    for (const record of [...this.timers]) this.cancelTimer(record);
    for (const record of [...this.timers]) this.cancelTimer(record);
    for (const error of this.timerFailures.values()) failure ??= error;
    for (const token of [...this.entries.keys()]) {
      try { this.release(token, false); } catch (error) { failure ??= error; }
    }
    for (const record of this.timers) {
      record.active = false;
      record.handle = undefined;
    }
    this.timers.clear();
    this.timerFailures.clear();
    this.failure = null;
    if (failure) throw failure;
  }
}
