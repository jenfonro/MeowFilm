import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { storageError } from '../engine/errors.js';

export const DATA_APPLICATION_ID = 0x4d464442;
export const CACHE_APPLICATION_ID = 0x4d464348;
export const SCHEMA_VERSION = 1;
export const SCHEMA_TARGETS = Object.freeze(['standalone', 'windows', 'ios']);
export const DATABASE_ROLES = Object.freeze(['data', 'cache']);

const ROLE_SPEC = Object.freeze({
  data: Object.freeze({
    role: 'data',
    filename: 'meowfilm-data.sqlite3',
    applicationId: DATA_APPLICATION_ID,
    synchronous: 'FULL',
    synchronousLevel: 2,
    initializeLock: 'data-initialize.lock',
    maintenanceLock: 'data-maintenance.lock',
    urls: Object.freeze({
      standalone: new URL('../../generated/schema-data-standalone.sql', import.meta.url),
      windows: new URL('../../generated/schema-data-windows.sql', import.meta.url),
      ios: new URL('../../generated/schema-data-ios.sql', import.meta.url),
    }),
  }),
  cache: Object.freeze({
    role: 'cache',
    filename: 'meowfilm-cache.sqlite3',
    applicationId: CACHE_APPLICATION_ID,
    synchronous: 'NORMAL',
    synchronousLevel: 1,
    initializeLock: 'cache-initialize.lock',
    maintenanceLock: 'cache-maintenance.lock',
    urls: Object.freeze({
      standalone: new URL('../../generated/schema-cache-standalone.sql', import.meta.url),
      windows: new URL('../../generated/schema-cache-windows.sql', import.meta.url),
      ios: new URL('../../generated/schema-cache-ios.sql', import.meta.url),
    }),
  }),
});

const cache = new Map();

export function resolveSchemaTarget(target = 'standalone') {
  if (typeof target !== 'string' || !Object.hasOwn(ROLE_SPEC.data.urls, target))
    throw storageError('STORAGE_CONFIGURATION');
  return target;
}

export function resolveDatabaseRole(role = 'data') {
  if (typeof role !== 'string' || !Object.hasOwn(ROLE_SPEC, role))
    throw storageError('STORAGE_CONFIGURATION');
  return role;
}

export function roleMetadata(role = 'data') {
  return ROLE_SPEC[resolveDatabaseRole(role)];
}

export function loadSchemaIdentity(target = 'standalone', role = 'data') {
  const resolvedTarget = resolveSchemaTarget(target);
  const resolvedRole = resolveDatabaseRole(role);
  const spec = ROLE_SPEC[resolvedRole];
  const cacheKey = `${resolvedRole}:${resolvedTarget}`;
  let pending = cache.get(cacheKey);
  if (!pending) {
    pending = readFile(spec.urls[resolvedTarget], 'utf8').then(sql => Object.freeze({
      role: resolvedRole,
      target: resolvedTarget,
      filename: spec.filename,
      sql,
      hash: createHash('sha256').update(sql).digest('hex'),
      applicationId: spec.applicationId,
      version: SCHEMA_VERSION,
      synchronous: spec.synchronous,
      synchronousLevel: spec.synchronousLevel,
      initializeLock: spec.initializeLock,
      maintenanceLock: spec.maintenanceLock,
    })).catch(error => {
      cache.delete(cacheKey);
      throw error;
    });
    cache.set(cacheKey, pending);
  }
  return pending;
}
