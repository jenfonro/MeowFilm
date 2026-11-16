import { existsSync, readFileSync } from 'node:fs';
import { isAbsolute, resolve } from 'node:path';
import { atomicWrite } from './private-volume.js';
import { catName, catSourceURL, validateCatSettings } from './cat-settings.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';

const digest = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const factsKeys = ['installationId', 'sourceHash', 'version', 'config', 'loadedIdentity',
  'importedAt', 'updatedAt', 'updateState', 'updateError', 'sites'];
const inputKeys = ['installationId', 'name', 'sourceType', 'sourceUrl', 'localPath',
  'localHash', 'enabled', 'inputRevision'];
const empty = () => ({ version: 1, port: 0, providers: {}, mappings: [], scripts: [] });
const identity = script => script.sourceType === 'network'
  ? `network:${script.inputRevision}:${script.sourceUrl}`
  : `local:${script.inputRevision}:${script.localHash}`;
const sameInput = (a, b) => inputKeys.every(key => a[key] === b[key]);

/** Native owns source intent. This file contains only actual runtime installation facts. */
export class CatNativeSettings {
  constructor(dataRoot, configuration) {
    this.path = resolve(dataRoot, 'settings.json');
    this.configuration = configuration;
    if (!existsSync(this.path)) atomicWrite(this.path, JSON.stringify({ version: 2, scripts: [] }) + '\n');
    this.value = this.read();
  }
  inputs() {
    const value = clone(this.configuration.read() ?? empty());
    requireValue(value?.version === 1 && Array.isArray(value.scripts), 'CORRUPT_STORAGE');
    const ids = new Set();
    for (const script of value.scripts) {
      requireValue(script && Object.keys(script).every(key => inputKeys.includes(key)), 'CORRUPT_STORAGE');
      identifier(script.installationId);
      requireValue(!ids.has(script.installationId), 'CORRUPT_STORAGE');
      ids.add(script.installationId);
      catName(script.name);
      requireValue(typeof script.enabled === 'boolean' && Number.isSafeInteger(script.inputRevision) &&
        script.inputRevision > 0, 'CORRUPT_STORAGE');
      if (script.sourceType === 'network') {
        catSourceURL(script.sourceUrl);
        requireValue(script.localPath === null && script.localHash === null, 'CORRUPT_STORAGE');
      } else {
        requireValue(script.sourceType === 'local' && script.sourceUrl === null && digest(script.localHash) &&
          (script.localPath === null || typeof script.localPath === 'string' &&
            isAbsolute(script.localPath) && !/[\u0000-\u001f]/.test(script.localPath)), 'CORRUPT_STORAGE');
      }
    }
    // Reuse the common gateway/provider/mapping validation without making it an input owner.
    validateCatSettings({ ...value, scripts: [] });
    return value;
  }
  facts() {
    try {
      const value = JSON.parse(readFileSync(this.path, 'utf8'));
      requireValue(value?.version === 2 && Object.keys(value).every(key => ['version', 'scripts'].includes(key)) &&
        Array.isArray(value.scripts), 'CORRUPT_STORAGE');
      const ids = new Set();
      for (const script of value.scripts) {
        requireValue(script && Object.keys(script).every(key => factsKeys.includes(key)) &&
          !ids.has(script.installationId), 'CORRUPT_STORAGE');
        identifier(script.installationId); ids.add(script.installationId);
        requireValue(script.sourceHash === null || digest(script.sourceHash), 'CORRUPT_STORAGE');
        requireValue(script.loadedIdentity === null || typeof script.loadedIdentity === 'string', 'CORRUPT_STORAGE');
        const { loadedIdentity, ...installed } = script;
        validateCatSettings({ ...empty(), scripts: [{ ...installed,
          sourceHash: script.sourceHash ?? '0'.repeat(64), name: script.installationId,
          sourceType: 'local', sourceUrl: null, enabled: false }] });
      }
      return value;
    } catch { throw fault('CORRUPT_STORAGE'); }
  }
  read() {
    const input = this.inputs(), facts = this.facts();
    return { ...input, scripts: input.scripts.map(script => {
      const installed = facts.scripts.find(value => value.installationId === script.installationId);
      return { sourceHash: null, version: '0.0.0', config: {}, loadedIdentity: null,
        importedAt: null, updatedAt: null, updateState: 'idle', updateError: null, sites: [],
        ...installed, ...script, inputIdentity: identity(script) };
    }) };
  }
  async reload() { await this.configuration.reload(); return this.read(); }
  publish(value) { this.value = clone(value); }
  save(change) {
    const current = this.read(), next = change(clone(current)), facts = this.facts();
    requireValue(next.scripts.length === current.scripts.length && next.scripts.every(script =>
      current.scripts.some(input => sameInput(input, script))), 'PERMISSION_DENIED');
    for (const script of next.scripts) {
      const installed = Object.fromEntries(factsKeys.map(key => [key, script[key]]));
      const index = facts.scripts.findIndex(value => value.installationId === script.installationId);
      if (index < 0) facts.scripts.push(installed);
      else facts.scripts[index] = installed;
    }
    try { atomicWrite(this.path, JSON.stringify(facts) + '\n'); }
    catch (error) {
      if (JSON.stringify(this.facts()) === JSON.stringify(facts)) throw fault('COMMIT_UNKNOWN');
      throw safeError(error, 'STORAGE_ERROR');
    }
    return this.read();
  }
  prune(installationId) {
    requireValue(!this.inputs().scripts.some(script => script.installationId === installationId), 'STORAGE_CONFLICT');
    const facts = this.facts();
    facts.scripts = facts.scripts.filter(script => script.installationId !== installationId);
    atomicWrite(this.path, JSON.stringify(facts) + '\n');
  }
}
