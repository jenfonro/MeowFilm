import fs from 'node:fs';
import { isUtf8 } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { isAbsolute, join, resolve } from 'node:path';
import { defaultPreferences, normalizePreferences } from './policy.js';
import { cloneJSONValue, parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

const codes = new Set([
  'INVALID_REQUEST', 'CLOSED', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'STORAGE_CHANGED', 'INVALID_PREFERENCES', 'LIBRARY_UPDATE_FAILED',
]);
function failure(code) {
  const error = new Error(codes.has(code) ? code : 'LIBRARY_UPDATE_FAILED');
  error.name = 'PlaybackLibraryError';
  error.code = error.message;
  return error;
}

function requireValue(condition) {
  if (!condition) throw failure('INVALID_REQUEST');
}

function object(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value) &&
    [Object.prototype, null].includes(Object.getPrototypeOf(value));
}

function document(value) {
  requireValue(object(value) && value.version === 2 &&
    ['history', 'manual', 'blocks', 'previewTargets', 'groups'].every(key => Array.isArray(value[key])));
  return value;
}

function freeze(value) {
  if (value && typeof value === 'object') {
    for (const child of Object.values(value)) freeze(child);
    Object.freeze(value);
  }
  return value;
}

/**
 * One trusted local owner, one document: <dataRoot>/film-playback/library.json.
 * read/update return immutable internal snapshots (including sensitive shares).
 * update mutates a detached draft synchronously; only rename commits/publishes.
 */
export function createPlaybackLibrary({ dataRoot, nativePreferences } = {}) {
  requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot) &&
    !dataRoot.includes('\0') && !dataRoot.split(/[\\/]/).includes('..'));
  const root = join(resolve(dataRoot), 'film-playback');
  const path = join(root, 'library.json');
  let closed = false;
  let updating = false;

  function readDisk() {
    let fd;
    try {
      fd = fs.openSync(path, fs.constants.O_RDONLY);
      fs.chmodSync(root, 0o700);
      const bytes = fs.readFileSync(fd);
      fs.fchmodSync(fd, 0o600);
      try {
        requireValue(isUtf8(bytes));
        return document(parseJSONText(bytes.toString('utf8')));
      }
      catch { throw failure('CORRUPT_STORAGE'); }
    } catch (error) {
      if (fd === undefined && error.code === 'ENOENT') return null;
      throw failure(error.code === 'CORRUPT_STORAGE' ? error.code : 'STORAGE_ERROR');
    } finally {
      if (fd !== undefined) {
        try { fs.closeSync(fd); } catch { throw failure('STORAGE_ERROR'); }
      }
    }
  }

  const serialized = stringifyJSONValue;
  const initial = readDisk();
  let committed = freeze(initial ?? {
    version: 2, ...(nativePreferences ? {} : { preferences: defaultPreferences() }),
    history: [], manual: [], blocks: [], previewTargets: [], groups: [],
  });
  let diskState = initial === null ? null : serialized(initial);

  function open() {
    if (closed) throw failure('CLOSED');
  }

  function unchanged() {
    const current = readDisk();
    if ((current === null ? null : serialized(current)) !== diskState) {
      throw failure('STORAGE_CHANGED');
    }
  }

  function write(bytes) {
    let fd, temporary, error;
    try {
      fs.mkdirSync(root, { recursive: true, mode: 0o700 });
      fs.chmodSync(root, 0o700);
      temporary = join(root, `.library-${randomUUID()}.tmp`);
      fd = fs.openSync(temporary,
        fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
      fs.fchmodSync(fd, 0o600);
      fs.writeFileSync(fd, bytes, 'utf8');
      fs.fsyncSync(fd);
      fs.closeSync(fd);
      fd = undefined;
      unchanged();
      // Nothing fallible after the commit point may turn success into a false
      // "not committed" response. The previous document survives any prior error.
      fs.renameSync(temporary, path);
      temporary = undefined;
    } catch (caught) { error = caught; }
    finally {
      if (fd !== undefined) {
        try { fs.closeSync(fd); } catch (caught) { error ??= caught; }
      }
      if (temporary !== undefined) {
        try { fs.unlinkSync(temporary); }
        catch (caught) { if (caught.code !== 'ENOENT') error ??= caught; }
      }
    }
    if (error) {
      throw failure(['CORRUPT_STORAGE', 'STORAGE_CHANGED'].includes(error.code) ? error.code : 'STORAGE_ERROR');
    }
  }

  function read() {
    open();
    return nativePreferences ? freeze({ ...committed, preferences: normalizePreferences(nativePreferences()) }) : committed;
  }

  function update(mutator) {
    open();
    requireValue(!updating && typeof mutator === 'function' &&
      mutator.constructor?.name !== 'AsyncFunction');
    updating = true;
    try {
      unchanged();
      const draft = cloneJSONValue(committed);
      const result = mutator(draft);
      if (result && typeof result.then === 'function') {
        Promise.resolve(result).catch(() => {});
        throw failure('INVALID_REQUEST');
      }
      open();
      // A held draft cannot mutate the owner. Check only the document root;
      // records are consumed by their selected business operation.
      const bytes = serialized(document(draft));
      const next = freeze(parseJSONText(bytes));
      const persisted = serialized(next);
      write(persisted);
      diskState = persisted;
      committed = next;
      return committed;
    } catch (error) { throw failure(error.code); }
    finally { updating = false; }
  }

  function close() { closed = true; }

  return { read, update, close, configurationOwner: nativePreferences ? 'native' : 'runtime' };
}
