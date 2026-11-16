import { existsSync, statSync, readFileSync } from 'node:fs';
import { isUtf8 } from 'node:buffer';
import { parseJSONText, stringifyJSONValue, cloneJSONValue as clone } from '@meowfilm/runtime-bridge/json';
import { resolve } from 'node:path';
import { atomicWrite } from './private-volume.js';
import { fault, requireValue, safeError } from './errors.js';

export function catSourceURL(value) {
  requireValue(typeof value === 'string' && value.trim().length > 0);
  let url;
  try { url = new URL(value); } catch { throw fault('INVALID_ARGUMENT'); }
  requireValue(['http:', 'https:'].includes(url.protocol));
  return value;
}
export function catName(value) {
  requireValue(typeof value === 'string' && value.trim().length > 0);
  return value.trim();
}
function jsonShape(value) {
  try { return clone(value); }
  catch { throw fault('INVALID_ARGUMENT'); }
}
export function normalizeCatConfig(input) {
  const value = jsonShape(input);
  requireValue(value && typeof value === 'object' && !Array.isArray(value));
  return value;
}
function settingsBytes(path) {
  requireValue(statSync(path).isFile(), 'CORRUPT_STORAGE');
  const bytes = readFileSync(path);
  requireValue(isUtf8(bytes), 'CORRUPT_STORAGE');
  return bytes.toString('utf8');
}
function readSettings(input) {
  requireValue(input && typeof input === 'object' && !Array.isArray(input) &&
    input.version === 1 && Array.isArray(input.scripts), 'CORRUPT_STORAGE');
  return input;
}

/** The file is the only durable settings authority. A failed reload neither
 * publishes a partial snapshot nor writes yesterday's values over the file. */
export class CatSettingsFile {
  constructor(dataRoot) {
    this.path = resolve(dataRoot, 'settings.json');
    if (!existsSync(this.path)) atomicWrite(this.path, stringifyJSONValue({
      version: 1, port: 0, providers: {}, mappings: [], scripts: [],
    }) + '\n');
    this.value = this.read();
  }
  read() {
    try { return readSettings(parseJSONText(settingsBytes(this.path))); }
    catch { throw fault('CORRUPT_STORAGE'); }
  }
  publish(value) { this.value = clone(value); }
  save(change) {
    const next = jsonShape(change(this.read()));
    try { atomicWrite(this.path, stringifyJSONValue(next) + '\n'); }
    catch (error) {
      // A rename can succeed before directory synchronization fails. Callers
      // must reconcile the actual file instead of rolling a committed source back.
      if (stringifyJSONValue(this.read()) === stringifyJSONValue(next)) throw fault('COMMIT_UNKNOWN');
      throw safeError(error, 'STORAGE_ERROR');
    }
    return next;
  }
}
