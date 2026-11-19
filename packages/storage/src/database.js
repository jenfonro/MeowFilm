import { readFile, mkdir, lstat, realpath } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve, join, dirname, isAbsolute } from 'node:path';

const APPLICATION_ID = 0x4d464442;
const MAX_PENDING = 64;
const MAX_BYTES = 16 * 1024 * 1024;
const roots = new Map();
let platform;
let schema;

export const storageError = code => Object.assign(new Error(code), { code });

/** Only the trusted platform bootstrap selects the driver, before opening owners. */
export function configureStorage({ driver, initialize = true } = {}) {
  if (roots.size || platform || typeof driver !== 'function')
    throw storageError('STORAGE_CONFIGURATION');
  platform = { driver, initialize };
}

/** Standalone assembly supplies its driver without replacing an App bootstrap. */
export function configureStandaloneStorage(driver) {
  if (!platform) configureStorage({ driver });
}

function cost(value) {
  if (value === null || value === undefined) return 8;
  if (typeof value === 'string') return Buffer.byteLength(value) + 32;
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') return 16;
  if (ArrayBuffer.isView(value)) return value.byteLength + 32;
  if (Array.isArray(value)) return 32 + value.reduce((total, item) => total + cost(item), 0);
  if (typeof value === 'object')
    return 64 + Object.entries(value).reduce((total, [key, item]) => total + cost(key) + cost(item), 0);
  throw storageError('INVALID_REQUEST');
}

async function ensureDirectory(path, create) {
  try {
    if (await realpath(path) !== path || !(await lstat(path)).isDirectory()) throw storageError('UNSAFE_PATH');
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    if (!create) throw storageError('STORAGE_SCHEMA');
    await ensureDirectory(dirname(path), true);
    try { await mkdir(path, { mode: 0o700 }); }
    catch (creation) { if (creation.code !== 'EEXIST') throw creation; }
    if ((await lstat(path)).isSymbolicLink()) throw storageError('UNSAFE_PATH');
    await ensureDirectory(path, false);
  }
}

async function schemaSource() {
  schema ??= readFile(new URL('../schema.sql', import.meta.url), 'utf8').then(sql => ({
    sql,
    hash: createHash('sha256').update(sql).digest('hex'),
    applicationId: APPLICATION_ID,
    version: 1,
  }));
  return schema;
}

function createOwner(root, options) {
  let driver, pending = 0, bytes = 0, closing = false, disposal;
  let tail = Promise.resolve();
  const ready = (async () => {
    const factory = options.driver ?? platform?.driver;
    if (!factory) throw storageError('STORAGE_DRIVER_REQUIRED');
    const initialize = options.initialize ?? platform?.initialize ?? true;
    const directory = join(root, 'storage-v1');
    await ensureDirectory(directory, initialize);
    driver = await factory(join(directory, 'meowfilm.sqlite3'), await schemaSource(), initialize);
    await driver.ready;
  })();
  ready.catch(() => {});
  const request = (input, signal, admitted) => {
    if (closing) return Promise.reject(storageError('STORAGE_CLOSED'));
    if (signal?.aborted) return Promise.reject(storageError('CANCELLED'));
    const size = cost(input);
    if (pending >= MAX_PENDING || pending > 0 && size > MAX_BYTES - bytes)
      return Promise.reject(storageError('STORAGE_BUSY'));
    // Reserve before cloning and retaining producer input in the queue.
    ++pending; bytes += size;
    let owned;
    try { owned = structuredClone(input); }
    catch { --pending; bytes -= size; return Promise.reject(storageError('INVALID_REQUEST')); }
    const result = tail.then(async () => {
      await ready;
      if (signal?.aborted) throw storageError('CANCELLED');
      // Once execution starts, cancellation cannot claim an accepted commit rolled back.
      return driver.request(owned);
    }).finally(() => { --pending; bytes -= size; });
    tail = result.then(() => {}, () => {});
    admitted(tail);
    return result;
  };
  return {
    references: 0,
    get closing() { return closing; },
    ready,
    request,
    diagnostics: () => ({ pendingCount: pending, pendingBytes: bytes }),
    close() {
      return disposal ??= (async () => {
        closing = true;
        await tail;
        let error;
        try { await ready; } catch (failure) { error = failure; }
        try { await driver?.close(); } catch (failure) { error ??= failure; }
        roots.delete(root);
        if (error) throw error;
      })();
    },
  };
}

/** One shared connection owner per product root; each domain explicitly releases its lease. */
export function openDatabase(dataRoot, options = {}) {
  if (typeof dataRoot !== 'string' || !isAbsolute(dataRoot) || dataRoot.includes('\0') || dataRoot.split(/[\\/]/).includes('..'))
    throw storageError('INVALID_REQUEST');
  const root = resolve(dataRoot);
  let owner = roots.get(root);
  if (owner?.closing) throw storageError('STORAGE_CLOSED');
  if (!owner) { owner = createOwner(root, options); roots.set(root, owner); }
  ++owner.references;
  let closed = false, disposal, drain = Promise.resolve();
  const request = (input, signal) => closed
    ? Promise.reject(storageError('STORAGE_CLOSED')) : owner.request(input, signal, end => { drain = end; });
  return {
    ready: owner.ready,
    query: (sql, params = [], { signal, maxRows = Number.MAX_SAFE_INTEGER } = {}) =>
      request({ kind: 'query', sql, params, maxRows }, signal),
    execute: (sql, params = [], { signal, expectChanges } = {}) =>
      request({ kind: 'execute', sql, params, expectChanges }, signal),
    transaction: (statements, { signal } = {}) =>
      request({ kind: 'transaction', statements }, signal),
    snapshot: (statements, { signal } = {}) =>
      request({ kind: 'snapshot', statements }, signal),
    checkpoint: () => request({ kind: 'checkpoint' }),
    backup: path => request({ kind: 'backup', path }),
    diagnostics: owner.diagnostics,
    close() {
      return disposal ??= (async () => {
        closed = true;
        if (--owner.references === 0) await owner.close();
        else { await drain; await owner.ready; }
      })();
    },
  };
}
