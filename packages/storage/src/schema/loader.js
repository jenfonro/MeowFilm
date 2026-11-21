import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { storageError } from '../engine/errors.js';

export const APPLICATION_ID = 0x4d464442;
export const SCHEMA_VERSION = 1;
export const SCHEMA_TARGETS = Object.freeze(['standalone', 'windows', 'ios']);

const SCHEMA_URLS = Object.freeze({
  standalone: new URL('../../generated/schema-standalone.sql', import.meta.url),
  windows: new URL('../../generated/schema-windows.sql', import.meta.url),
  ios: new URL('../../generated/schema-ios.sql', import.meta.url),
});

const cache = new Map();

export function resolveSchemaTarget(target = 'standalone') {
  if (typeof target !== 'string' || !Object.hasOwn(SCHEMA_URLS, target))
    throw storageError('STORAGE_CONFIGURATION');
  return target;
}

export function loadSchemaIdentity(target = 'standalone') {
  const resolved = resolveSchemaTarget(target);
  let pending = cache.get(resolved);
  if (!pending) {
    pending = readFile(SCHEMA_URLS[resolved], 'utf8').then(sql => Object.freeze({
      target: resolved,
      sql,
      hash: createHash('sha256').update(sql).digest('hex'),
      applicationId: APPLICATION_ID,
      version: SCHEMA_VERSION,
    })).catch(error => {
      cache.delete(resolved);
      throw error;
    });
    cache.set(resolved, pending);
  }
  return pending;
}
