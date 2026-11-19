import { cloneJSONValue as clone } from '@meowfilm/runtime-bridge/json';
import { fault, requireValue } from './errors.js';
import { CatStorage } from './cat-storage.js';

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
export class CatSettingsStore {
  constructor(dataRoot) {
    this.storage = new CatStorage(dataRoot, 'standalone');
    this.value = this.storage.value;
    this.ready = this.storage.ready.then(value => { this.value = value; });
  }
  read() { return clone(this.storage.value); }
  async reload() { await this.storage.reload(); return this.read(); }
  publish(value) { this.value = clone(value); }
  save(change) { return this.storage.save(value => jsonShape(change(clone(value)))); }
  close() { return this.storage.close(); }
}
