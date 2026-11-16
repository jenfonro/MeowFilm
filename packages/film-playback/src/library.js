import fs from 'node:fs';
import { isUtf8 } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { isAbsolute, join, resolve } from 'node:path';
import { defaultPreferences, normalizePreferences } from './policy.js';
import { checkTarget } from './identity.js';

const codes = new Set([
  'INVALID_REQUEST', 'CLOSED', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'STORAGE_CHANGED', 'INVALID_PREFERENCES', 'LIBRARY_UPDATE_FAILED',
]);
const historyKeys = [
  'targetId', 'title', 'sourceKey', 'candidateId', 'detailId', 'lineId', 'episode',
  'position', 'duration', 'paused', 'ended', 'playedAt', 'updatedAt', 'playbackTarget',
];
const manualKeys = [
  'id', 'targetId', 'kind', 'title', 'name', 'sourceKey', 'detailId', 'provider', 'share',
  'seasonHint', 'enabled', 'autoDisable', 'failureCount',
];
const blockKeys = ['targetId', 'sourceKey', 'detailId', 'lineId'];

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

function shape(value, keys) {
  requireValue(object(value) && Object.keys(value).every(key => keys.includes(key)));
}

function text(value) {
  requireValue(typeof value === 'string' && value.length > 0 && !value.includes('\0'));
}

function number(value) {
  requireValue(Number.isFinite(value) && value >= 0);
}

function integer(value) {
  requireValue(Number.isSafeInteger(value) && value >= 0);
}

// Candidate ids are stable resource identities, not the transport descriptor.
// Other source/detail identities may legitimately be ordinary page URLs.
function candidateIdentity(value) {
  identity(value);
  requireValue(!/https?(?::|%3a)(?:\/|%2f){2}/i.test(value));
}

function identity(value) {
  text(value);
  let decoded;
  try { decoded = decodeURIComponent(value); } catch { decoded = value; }
  requireValue(!/[?&](?:sign(?:ature)?|token|access_token|auth_key|authorization|expires?)=/i.test(decoded));
  if (/^https?:\/\//i.test(decoded)) {
    const url = parseURL(decoded);
    requireValue(!url.username && !url.password);
  }
}

function parseURL(value) {
  try { return new URL(value); } catch { throw failure('INVALID_REQUEST'); }
}

function resourceIdentity(value) {
  if (typeof value === 'number') requireValue(Number.isFinite(value));
  else identity(value);
}

function history(value) {
  shape(value, historyKeys);
  identity(value.targetId);
  text(value.title);
  identity(value.sourceKey);
  candidateIdentity(value.candidateId);
  if (value.playbackTarget !== undefined) {
    const target = value.playbackTarget;
    shape(target, ['id', 'kind', 'title', 'year', 'metadataProvider', 'metadataId',
      'season', 'episode', 'globalEpisode', 'members', 'source']);
    try { checkTarget(target); } catch { requireValue(false); }
    requireValue(target.id === value.targetId);
    if (target.source !== undefined) {
      shape(target.source, ['key', 'engine', 'installationId', 'siteId', 'detailId', 'lineId', 'candidateId']);
      identity(target.source.key);
      resourceIdentity(target.source.detailId);
      if (target.source.lineId !== undefined) resourceIdentity(target.source.lineId);
      if (target.source.candidateId !== undefined) candidateIdentity(target.source.candidateId);
    }
    for (const member of target.members ?? []) {
      shape(member, ['sourceKey', 'detailId']);
      identity(member.sourceKey);
      resourceIdentity(member.detailId);
    }
  }
  resourceIdentity(value.detailId);
  resourceIdentity(value.lineId);
  number(value.position);
  number(value.updatedAt);
  if (value.episode !== undefined) {
    shape(value.episode, ['season', 'episode', 'globalEpisode']);
    for (const key of ['season', 'episode', 'globalEpisode']) {
      if (value.episode[key] !== undefined) integer(value.episode[key]);
    }
  }
  if (value.duration !== undefined) number(value.duration);
  requireValue(typeof value.paused === 'boolean');
  if (value.ended !== undefined) requireValue(typeof value.ended === 'boolean');
  if (value.playedAt !== undefined) {
    number(value.playedAt);
  }
}

function manual(value) {
  shape(value, manualKeys);
  identity(value.id);
  identity(value.targetId);
  requireValue((value.kind === undefined || ['site', 'share'].includes(value.kind)) &&
    (value.enabled === undefined || typeof value.enabled === 'boolean') &&
    (value.autoDisable === undefined || typeof value.autoDisable === 'boolean'));
  integer(value.failureCount);
  for (const key of ['name', 'title']) {
    if (value[key] !== undefined) requireValue(typeof value[key] === 'string');
  }
  if (value.seasonHint !== undefined) integer(value.seasonHint);
  const kind = value.kind ?? (value.share === undefined ? 'site' : 'share');
  if (kind === 'site') {
    identity(value.sourceKey);
    resourceIdentity(value.detailId);
    requireValue(value.share === undefined && value.provider === undefined);
  } else {
    requireValue(value.sourceKey === undefined && value.detailId === undefined);
    if (value.provider !== undefined) text(value.provider);
    shape(value.share, ['url', 'password']);
    text(value.share.url);
    const url = parseURL(value.share.url);
    requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password);
    if (value.share.password !== undefined) requireValue(typeof value.share.password === 'string');
  }
}

function block(value) {
  shape(value, blockKeys);
  for (const key of ['targetId', 'sourceKey']) identity(value[key]);
  resourceIdentity(value.detailId);
  resourceIdentity(value.lineId);
}

function unique(values, key) {
  const seen = new Set();
  for (const value of values) {
    const id = key(value);
    requireValue(!seen.has(id));
    seen.add(id);
  }
}

function blockIdentity(value) {
  return JSON.stringify(blockKeys.map(key => value[key]));
}

function validate(value) {
  if (value.version === 1) {
    shape(value, ['version', 'preferences', 'history', 'manual', 'blocks', 'previewTargets']);
    value = { ...value, version: 2, groups: [] };
  }
  shape(value, ['version', 'preferences', 'history', 'manual', 'blocks', 'previewTargets', 'groups']);
  requireValue(value.version === 2 && object(value.preferences) &&
    Array.isArray(value.history) && Array.isArray(value.manual) && Array.isArray(value.blocks) &&
    Array.isArray(value.previewTargets) && Array.isArray(value.groups));
  shape(value.preferences, Object.keys(defaultPreferences()));
  value.preferences = normalizePreferences(value.preferences);
  value.history.forEach(history);
  value.manual.forEach(manual);
  value.blocks.forEach(block);
  value.previewTargets.forEach(identity);
  for (const group of value.groups) {
    shape(group, ['id', 'keys', 'members']);
    requireValue(/^group:[a-f0-9]{64}$/.test(group.id) && Array.isArray(group.keys) &&
      Array.isArray(group.members) && group.members.length > 0 &&
      [...group.keys, ...group.members].every(key => /^[a-f0-9]{64}$/.test(key)));
    unique(group.keys, key => key);
    unique(group.members, key => key);
  }
  unique(value.history, entry => entry.targetId);
  unique(value.manual, entry => entry.id);
  unique(value.blocks, blockIdentity);
  unique(value.groups, group => group.id);
  unique(value.groups.flatMap(group => group.keys), key => key);
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
        const value = JSON.parse(bytes.toString('utf8'));
        return validate(nativePreferences ? { ...value, preferences: defaultPreferences() } : value);
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

  const serialized = value => JSON.stringify(nativePreferences ? Object.fromEntries(Object.entries(value)
    .filter(([key]) => key !== 'preferences')) : value);
  const initial = readDisk();
  let committed = freeze(initial ?? {
    version: 2, preferences: defaultPreferences(), history: [], manual: [], blocks: [], previewTargets: [], groups: [],
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
      const draft = structuredClone(committed);
      const result = mutator(draft);
      if (result && typeof result.then === 'function') {
        Promise.resolve(result).catch(() => {});
        throw failure('INVALID_REQUEST');
      }
      open();
      // Serialize the checked document before commit, also detaching anything
      // the trusted mutator might retain. A held draft cannot mutate the owner.
      const bytes = JSON.stringify(validate(draft));
      const next = freeze(validate(JSON.parse(bytes)));
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
