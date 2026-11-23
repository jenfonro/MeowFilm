import { mkdir, stat } from 'node:fs/promises';
import { resolve, join, isAbsolute } from 'node:path';
import { performance } from 'node:perf_hooks';
import { storageError } from './errors.js';
import { loadSchemaIdentity, resolveDatabaseRole, resolveSchemaTarget, roleMetadata } from '../schema/loader.js';

const MAX_PENDING = 128;
const MAX_BYTES = 32 * 1024 * 1024;
const owners = new Map();
let platform;
let observer = null;

export { storageError };

/** Optional debug observer injected only in Debug mode; null disables all hooks. */
export function setStorageObserver(value) {
  observer = value && typeof value.emit === 'function' ? value : null;
}

/** Aggregate active storage owner diagnostics for Debug inspection. */
export function getStorageDiagnostics() {
  let pendingCount = 0;
  let pendingBytes = 0;
  const activeRoots = [];
  for (const owner of owners.values()) {
    const diag = owner.diagnostics();
    pendingCount += diag.pendingCount;
    pendingBytes += diag.pendingBytes;
    activeRoots.push({
      root: owner.root,
      role: owner.role,
      target: owner.target,
      references: owner.references,
      closing: owner.closing,
      pendingCount: diag.pendingCount,
      pendingBytes: diag.pendingBytes,
    });
  }
  return { openDatabases: owners.size, pendingCount, pendingBytes, roots: activeRoots };
}

function storageLog(level, event, data, { durationMs, error } = {}) {
  if (!observer || observer.isEnabled?.('storage') === false) return;
  observer.emit({ level, cat: 'storage', sub: 'sqlite', event, durationMs, data, error });
}

function summarizeStatement(input) {
  if (!input || typeof input !== 'object') return {};
  if (input.kind === 'query' || input.kind === 'execute') {
    return { kind: input.kind, sql: input.sql, params: input.params, maxRows: input.maxRows, expectChanges: input.expectChanges };
  }
  if (input.kind === 'transaction' || input.kind === 'snapshot') {
    return {
      kind: input.kind,
      statementCount: Array.isArray(input.statements) ? input.statements.length : 0,
      statements: input.statements,
    };
  }
  return { kind: input.kind };
}

/** Only the trusted platform bootstrap selects the driver and schema target, before opening owners. */
export function configureStorage({ driver, initialize = true, target = 'standalone' } = {}) {
  if (owners.size || platform || typeof driver !== 'function')
    throw storageError('STORAGE_CONFIGURATION');
  platform = { driver, initialize, target: resolveSchemaTarget(target) };
}

/** Standalone assembly supplies its driver without replacing an App bootstrap. */
export function configureStandaloneStorage(driver, { target = 'standalone' } = {}) {
  if (!platform) configureStorage({ driver, target });
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

function isReadRequest(input) {
  return input?.kind === 'query' || input?.kind === 'snapshot';
}

async function ensureDirectory(path, create) {
  if (create) {
    await mkdir(path, { recursive: true, mode: 0o700 });
  }
  let info;
  try { info = await stat(path); }
  catch (error) {
    if (error.code === 'ENOENT' && !create) throw storageError('STORAGE_SCHEMA');
    throw error;
  }
  if (!info.isDirectory()) throw storageError('UNSAFE_PATH');
}

function createOwner(ownerKey, root, role, options) {
  let driver, pending = 0, bytes = 0, closing = false, disposal;
  // Separate read and write lanes under WAL so read queries/snapshots never queue behind writes.
  let readTail = Promise.resolve();
  let writeTail = Promise.resolve();
  const target = resolveSchemaTarget(options.target ?? platform?.target ?? 'standalone');
  const meta = roleMetadata(role);
  const ready = (async () => {
    const startedAt = observer ? performance.now() : 0;
    const factory = options.driver ?? platform?.driver;
    if (!factory) throw storageError('STORAGE_DRIVER_REQUIRED');
    const initialize = options.initialize ?? platform?.initialize ?? true;
    const directory = join(root, 'storage-v1');
    const file = join(directory, meta.filename);
    try {
      await ensureDirectory(directory, initialize);
      driver = await factory(file, await loadSchemaIdentity(target, role), initialize);
      await driver.ready;
      storageLog('info', 'database.open', { root, role, file, initialize, target },
        { durationMs: observer ? performance.now() - startedAt : undefined });
    } catch (error) {
      storageLog('error', 'database.open.error', { root, role, file, initialize, target },
        { durationMs: observer ? performance.now() - startedAt : undefined, error });
      throw error;
    }
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
    const readLane = isReadRequest(owned);
    const laneTail = readLane ? readTail : writeTail;
    const result = laneTail.then(async () => {
      await ready;
      if (signal?.aborted) throw storageError('CANCELLED');
      const startedAt = observer ? performance.now() : 0;
      // Once execution starts, cancellation cannot claim an accepted commit rolled back.
      try {
        const value = await driver.request(owned);
        if (observer) {
          const durationMs = performance.now() - startedAt;
          const rowCount = Array.isArray(value) ? value.length
            : Array.isArray(value?.rows) ? value.rows.length : undefined;
          storageLog('debug', `database.${owned.kind}`, {
            root,
            role,
            lane: readLane ? 'read' : 'write',
            ...summarizeStatement(owned),
            ...(rowCount !== undefined ? { rowCount } : {}),
            ...(typeof value?.changes === 'number' ? { changes: value.changes } : {}),
            pendingCount: pending,
            pendingBytes: bytes,
          }, { durationMs });
        }
        return value;
      } catch (error) {
        storageLog('error', `database.${owned.kind}.error`, {
          root,
          role,
          lane: readLane ? 'read' : 'write',
          ...summarizeStatement(owned),
          pendingCount: pending,
          pendingBytes: bytes,
        }, { durationMs: observer ? performance.now() - startedAt : undefined, error });
        throw error;
      }
    }).finally(() => { --pending; bytes -= size; });
    const settled = result.then(() => {}, () => {});
    if (readLane) readTail = settled;
    else writeTail = settled;
    admitted(settled, readLane);
    return result;
  };
  return {
    root,
    role,
    target,
    references: 0,
    get closing() { return closing; },
    ready,
    request,
    diagnostics: () => ({ pendingCount: pending, pendingBytes: bytes }),
    close() {
      return disposal ??= (async () => {
        closing = true;
        const startedAt = observer ? performance.now() : 0;
        await Promise.all([readTail, writeTail]);
        try { await ready; } catch { /* Open failure is delivered by ready and requests. */ }
        try { await driver?.close(); }
        finally {
          owners.delete(ownerKey);
          storageLog('info', 'database.close', { root, role, target },
            { durationMs: observer ? performance.now() - startedAt : undefined });
        }
      })();
    },
  };
}

/** One shared connection owner per (product root, database role); each domain explicitly releases its lease. */
export function openDatabase(dataRoot, options = {}) {
  if (typeof dataRoot !== 'string' || !isAbsolute(dataRoot) || dataRoot.includes('\0') || dataRoot.split(/[\\/]/).includes('..'))
    throw storageError('INVALID_REQUEST');
  const root = resolve(dataRoot);
  const role = resolveDatabaseRole(options.role ?? 'data');
  const ownerKey = `${root}\0${role}`;
  let owner = owners.get(ownerKey);
  if (owner?.closing) throw storageError('STORAGE_CLOSED');
  if (owner && options.target !== undefined && resolveSchemaTarget(options.target) !== owner.target)
    throw storageError('STORAGE_CONFIGURATION');
  if (!owner) { owner = createOwner(ownerKey, root, role, options); owners.set(ownerKey, owner); }
  ++owner.references;
  let closed = false, disposal, readDrain = Promise.resolve(), writeDrain = Promise.resolve();
  const request = (input, signal) => closed
    ? Promise.reject(storageError('STORAGE_CLOSED'))
    : owner.request(input, signal, (end, isRead) => {
      if (isRead) readDrain = end;
      else writeDrain = end;
    });
  return {
    role,
    ready: owner.ready,
    query: (sql, params = [], { signal, maxRows = Number.MAX_SAFE_INTEGER } = {}) =>
      request({ kind: 'query', sql, params, maxRows }, signal),
    execute: (sql, params = [], { signal, expectChanges } = {}) =>
      request({ kind: 'execute', sql, params, expectChanges }, signal),
    transaction: (statements, { signal } = {}) =>
      request({ kind: 'transaction', statements }, signal),
    snapshot: (statements, { signal } = {}) =>
      request({ kind: 'snapshot', statements }, signal),
    diagnostics: owner.diagnostics,
    close() {
      return disposal ??= (async () => {
        closed = true;
        if (--owner.references === 0) await owner.close();
        else { await Promise.all([readDrain, writeDrain]); }
      })();
    },
  };
}
