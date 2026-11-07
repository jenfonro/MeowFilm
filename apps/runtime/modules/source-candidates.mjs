import { createHash } from 'node:crypto';
import { isUtf8 } from 'node:buffer';

export const sourceByteLimit = 16 * 1024 * 1024;
const identifier = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$(?![\s\S])/;

/** Complete management inputs only; no upload sessions, history or timers. */
export class SourceCandidates {
  #pending = new Map();
  #held = new Map();
  #published = new Map();
  #closed = false;
  constructor({ module, fail }) { this.module = module; this.fail = fail; }
  #require(value, code = 'INVALID_REQUEST') { if (!value) throw this.fail(code); }
  #code(kind) {
    return ({ music: { missing: 'SOURCE_NOT_FOUND', integrity: 'SCRIPT_INTEGRITY', encoding: 'SCRIPT_ENCODING', limit: 'LIMIT' },
      source: { missing: 'SOURCE_NOT_FOUND', integrity: 'SOURCE_INTEGRITY', encoding: 'SOURCE_ENCODING', limit: 'LIMIT' },
      cat: { missing: 'NOT_FOUND', integrity: 'INVALID_ARGUMENT', encoding: 'SCRIPT_PROTOCOL_ERROR', limit: 'RESOURCE_LIMIT' } })[this.module][kind];
  }
  validate({ installationId, sourceRevision, basename }) {
    this.#require(!this.#closed, this.module === 'cat' ? 'HOST_CLOSED' : 'CLOSED');
    const invalid = this.module === 'cat' ? 'INVALID_ARGUMENT' : 'INVALID_REQUEST';
    this.#require(typeof installationId === 'string' && identifier.test(installationId) &&
      Number.isSafeInteger(sourceRevision) && sourceRevision > 0, invalid);
    if (this.module === 'cat') this.#require(basename === undefined, invalid);
    else this.#require(typeof basename === 'string' && basename.trim().length > 0 && basename.length <= 255 &&
      !/[\\/:*?"<>|\u0000-\u001f\u007f]/.test(basename) &&
      (this.module === 'music' ? /\.js$(?![\s\S])/i : /\.(?:json|js|html?)$(?![\s\S])/i).test(basename), invalid);
  }
  put(input, bytes, signal) {
    this.validate(input);
    this.#require(!signal?.aborted, 'CANCELLED');
    this.#require(bytes instanceof Uint8Array, this.#code('encoding'));
    this.#require(bytes.length <= sourceByteLimit, this.#code('limit'));
    this.#require(bytes.length > 0 && isUtf8(bytes), this.#code('encoding'));
    const source = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('utf8');
    this.#require(source.trim().length > 0 && (this.module !== 'cat' || !source.startsWith('\uFEFF')), this.#code('encoding'));
    const { installationId, sourceRevision } = input;
    const existing = [this.#pending.get(installationId), this.#published.get(installationId),
      ...(this.#held.get(installationId)?.keys() ?? [])].find(record => record?.sourceRevision === sourceRevision);
    if (existing) {
      this.#require(existing.source === source, this.#code('integrity'));
      if (this.module === 'music') this.#require(existing.basename === input.basename, this.#code('integrity'));
      return this.receipt(existing);
    }
    const record = { installationId, sourceRevision, source, sourceBytes: bytes.length,
      ...(this.module === 'music' ? { basename: input.basename } : {}),
      contentHash: createHash('sha256').update(bytes).digest('hex') };
    this.#pending.set(installationId, record);
    return this.receipt(record);
  }
  receipt({ installationId, sourceRevision, sourceBytes, contentHash }) {
    return { installationId, sourceRevision, sourceBytes, contentHash };
  }
  peek(id, revision) {
    const pending = this.#pending.get(id), published = this.#published.get(id);
    const record = pending?.sourceRevision === revision ? pending : published?.sourceRevision === revision ? published
      : [...(this.#held.get(id)?.keys() ?? [])].find(value => value.sourceRevision === revision);
    this.#require(record, this.#code('missing'));
    return record;
  }
  take(id, revision) {
    const record = this.peek(id, revision);
    if (this.#pending.get(id) === record) this.#pending.delete(id);
    // CatRuntime owns its transaction queue, so more than one admitted call
    // can still own an exact complete input. These are live references only.
    let held = this.#held.get(id);
    if (!held) this.#held.set(id, held = new Map());
    held.set(record, (held.get(record) ?? 0) + 1);
    return record;
  }
  finish(record, published = false) {
    const held = this.#held.get(record.installationId), references = held?.get(record);
    if (references > 1) held.set(record, references - 1);
    else held?.delete(record);
    if (held?.size === 0) this.#held.delete(record.installationId);
    if (published && !this.#closed) this.#published.set(record.installationId, record);
  }
  retire(id) { this.#published.delete(id); }
  remove(id) { this.#pending.delete(id); this.#published.delete(id); }
  close() { this.#closed = true; this.#pending.clear(); this.#published.clear(); }
}

/** Management is ordered within an installation; status and other installations proceed. */
export class InstallationOperations {
  #tails = new Map();
  has(id) { return this.#tails.has(id); }
  run(id, body) {
    const result = (this.#tails.get(id) ?? Promise.resolve()).catch(() => {}).then(body);
    this.#tails.set(id, result);
    result.finally(() => { if (this.#tails.get(id) === result) this.#tails.delete(id); }).catch(() => {});
    return result;
  }
  async drain() { await Promise.allSettled([...this.#tails.values()]); }
}
