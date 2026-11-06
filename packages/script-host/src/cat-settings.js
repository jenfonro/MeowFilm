import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { atomicWrite } from './private-volume.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';

export const catProviders = Object.freeze(['baidu', 'quark', 'uc', 'tianyi', 'yidong']);
const managementFields = ['name', 'sourceType', 'sourceUrl', 'importedAt', 'updatedAt',
  'updateState', 'updateError', 'sites'];
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
  try { return JSON.parse(JSON.stringify(value)); }
  catch { throw fault('INVALID_ARGUMENT'); }
}
export function normalizeCatConfig(input) {
  const value = jsonShape(input);
  requireValue(value && typeof value === 'object' && !Array.isArray(value));
  return value;
}
function settingsBytes(path) {
  const state = lstatSync(path);
  requireValue(state.isFile() && !state.isSymbolicLink() && state.nlink === 1, 'CORRUPT_STORAGE');
  return readFileSync(path, 'utf8');
}
export function validateCatSettings(input) {
  requireValue(input && typeof input === 'object' && !Array.isArray(input));
  requireValue(Object.keys(input).every(key => ['version', 'port', 'providers', 'mappings', 'scripts'].includes(key)));
  requireValue(input.version === 1 && Number.isInteger(input.port) && input.port >= 0 && input.port <= 65535);
  requireValue(input.providers && typeof input.providers === 'object' && !Array.isArray(input.providers));
  requireValue(Object.keys(input.providers).every(key => catProviders.includes(key)));
  for (const value of Object.values(input.providers)) requireValue(typeof value === 'boolean');
  requireValue(Array.isArray(input.mappings));
  const mappings = new Set();
  for (const item of input.mappings) {
    requireValue(item && Object.keys(item).every(key => ['provider', 'aliases'].includes(key)) &&
      catProviders.includes(item.provider) && Array.isArray(item.aliases));
    for (const alias of item.aliases) {
      requireValue(typeof alias === 'string' && alias.trim().length > 0 && alias.length <= 256 &&
        !/[\u0000-\u001f]/.test(alias) && !mappings.has(alias.trim().toLowerCase()));
      mappings.add(alias.trim().toLowerCase());
    }
  }
  requireValue(Array.isArray(input.scripts));
  const identities = new Set();
  const result = clone(input);
  for (const script of result.scripts) {
    requireValue(script && Object.keys(script).every(key =>
      ['installationId', 'sourceHash', 'enabled', 'version', 'config', ...managementFields].includes(key)));
    identifier(script.installationId);
    requireValue(!identities.has(script.installationId));
    identities.add(script.installationId);
    requireValue(/^[a-f0-9]{64}$/.test(script.sourceHash) && typeof script.enabled === 'boolean' &&
      typeof script.version === 'string' && script.version.length > 0 && script.version.length <= 128 &&
      script.config && typeof script.config === 'object' && !Array.isArray(script.config));
    // Version 1 previously stored exactly the five fields above. Their source
    // was staged locally; neither a display name nor timestamps were recorded.
    if (!managementFields.some(key => Object.hasOwn(script, key))) Object.assign(script, {
      name: script.installationId, sourceType: 'local', sourceUrl: null,
      importedAt: null, updatedAt: null, updateState: 'idle', updateError: null, sites: [],
    });
    catName(script.name);
    requireValue(['local', 'network'].includes(script.sourceType));
    if (script.sourceType === 'network') catSourceURL(script.sourceUrl);
    else requireValue(script.sourceUrl === null);
    for (const value of [script.importedAt, script.updatedAt])
      requireValue(value === null || Number.isSafeInteger(value) && value >= 0);
    requireValue(['idle', 'updated', 'unchanged', 'failed'].includes(script.updateState));
    requireValue(script.updateState === 'failed'
      ? typeof script.updateError === 'string' && script.updateError.length > 0
      : script.updateError === null);
    requireValue(Array.isArray(script.sites));
    const sites = new Set();
    for (const site of script.sites) {
      requireValue(site && typeof site.key === 'string' && site.key.length > 0 &&
        !sites.has(site.key) && typeof site.name === 'string' && typeof site.api === 'string' &&
        Array.isArray(site.actions) && site.actions.every(action => typeof action === 'string') &&
        site.methods && typeof site.methods === 'object' && !Array.isArray(site.methods) &&
        Object.values(site.methods).every(method => typeof method === 'string'));
      sites.add(site.key);
    }
  }
  return result;
}

/** The file is the only durable settings authority. A failed reload neither
 * publishes a partial snapshot nor writes yesterday's values over the file. */
export class CatSettingsFile {
  constructor(dataRoot) {
    this.path = resolve(dataRoot, 'settings.json');
    if (!existsSync(this.path)) atomicWrite(this.path, JSON.stringify({
      version: 1, port: 0, providers: {}, mappings: [], scripts: [],
    }, null, 2) + '\n');
    this.value = this.read();
  }
  read() {
    try { return validateCatSettings(JSON.parse(settingsBytes(this.path))); }
    catch { throw fault('CORRUPT_STORAGE'); }
  }
  publish(value) { this.value = validateCatSettings(value); }
  save(change) {
    const next = validateCatSettings(jsonShape(change(this.read())));
    try { atomicWrite(this.path, JSON.stringify(next, null, 2) + '\n'); }
    catch (error) {
      // A rename can succeed before directory synchronization fails. Callers
      // must reconcile the actual file instead of rolling a committed source back.
      if (JSON.stringify(this.read()) === JSON.stringify(next)) throw fault('COMMIT_UNKNOWN');
      throw safeError(error, 'STORAGE_ERROR');
    }
    return next;
  }
}
