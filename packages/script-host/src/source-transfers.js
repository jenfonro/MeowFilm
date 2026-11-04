import { createHash, randomUUID } from 'node:crypto';
import { fault, requireValue } from './errors.js';
import { SCRIPT_LIMITS } from './host.js';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
/** A frame bound is not a source bound. Staging retains received chunks rather
 * than reserving the caller's claimed whole-source length before transfer. */
export class SourceTransfers {
  constructor({ clock = Date.now, ttl = 300000,
    setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
    this.clock = clock; this.ttl = ttl; this.setTimer = setTimer; this.clearTimer = clearTimer;
    this.entries = new Map(); this.closed = false; this.chunkBytes = 64 * 1024;
    this.timer = undefined; this.failure = null;
  }
  schedule() {
    const previous = this.timer;
    this.timer = undefined;
    if (previous !== undefined) {
      try { this.clearTimer(previous); }
      catch (error) { this.failure ??= error; }
    }
    if (this.closed || !this.entries.size) return;
    let expires = Infinity;
    for (const entry of this.entries.values()) expires = Math.min(expires, entry.expires);
    const handle = this.setTimer(() => this.expire(), Math.max(1, expires - this.clock()));
    this.timer = handle;
    try { handle?.unref?.(); } catch { /* The tracked timer still owns expiry. */ }
  }
  expire() {
    if (this.closed) return;
    try { this.sweep(); }
    catch (error) {
      this.failure ??= error;
      for (const token of [...this.entries.keys()]) {
        try { this.release(token, false); }
        catch (cleanupError) { this.failure ??= cleanupError; }
      }
    }
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
    requireValue(kind !== 'source' || length <= SCRIPT_LIMITS.maxSourceBytes, 'RESOURCE_LIMIT');
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
  // A caller with a validated encoding can transfer its Buffer directly and
  // decode its small inline envelope, avoiding a second serialization.
  result(value, { inline } = {}) {
    this.sweep();
    const bytes = inline === undefined ? Buffer.from(JSON.stringify(value)) : value;
    if (bytes.length <= this.chunkBytes) {
      try { return inline === undefined ? { data: bytes.toString('utf8') } : inline(bytes); }
      finally { bytes.fill(0); }
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
    if (this.closed) return;
    this.closed = true;
    let failure = this.failure;
    const previous = this.timer;
    this.timer = undefined;
    if (previous !== undefined) {
      try { this.clearTimer(previous); }
      catch (error) { failure ??= error; }
    }
    failure ??= this.failure;
    for (const token of [...this.entries.keys()]) {
      try { this.release(token, false); } catch (error) { failure ??= error; }
    }
    this.failure = null;
    if (failure) throw failure;
  }
}
