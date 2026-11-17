import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { isUtf8 } from 'node:buffer';
import { parseJSONText, stringifyJSONValue, cloneJSONValue as clone } from '@meowfilm/runtime-bridge/json';
import { atomicWrite } from './private-volume.js';
import { fault, identifier, requireValue, safeError } from './errors.js';

const factsKeys = ['installationId', 'sourceHash', 'version', 'config', 'loadedIdentity',
  'importedAt', 'updatedAt', 'updateState', 'updateError', 'sites'];
const inputKeys = ['installationId', 'name', 'sourceType', 'sourceUrl', 'localPath',
  'localHash', 'enabled', 'inputRevision'];
const empty = () => ({ version: 1, port: 0, providers: {}, mappings: [], scripts: [] });
const identityPart = value => typeof value === 'string' ? value : stringifyJSONValue(value);
const identity = script => `${script.sourceType}:${identityPart(script.inputRevision)}:${identityPart(
  script.sourceType === 'network' ? script.sourceUrl : script.localHash)}`;
const sameInput = (a, b) => inputKeys.filter(key => a.sourceType === 'network'
  ? !['localPath', 'localHash'].includes(key) : key !== 'sourceUrl')
  .every(key => stringifyJSONValue(a[key]) === stringifyJSONValue(b[key]));

/** Native owns source intent. This file contains only actual runtime installation facts. */
export class CatNativeSettings {
  constructor(dataRoot, configuration) {
    this.path = resolve(dataRoot, 'settings.json');
    this.configuration = configuration;
    if (!existsSync(this.path)) atomicWrite(this.path, stringifyJSONValue({ version: 2, scripts: [] }) + '\n');
    this.value = this.read();
  }
  inputs() {
    const value = clone(this.configuration.read() ?? empty());
    requireValue(value?.version === 1 && Array.isArray(value.scripts), 'CORRUPT_STORAGE');
    return value;
  }
  facts() {
    try {
      const bytes = readFileSync(this.path);
      requireValue(isUtf8(bytes), 'CORRUPT_STORAGE');
      const value = parseJSONText(bytes.toString('utf8'));
      requireValue(value?.version === 2 && Array.isArray(value.scripts), 'CORRUPT_STORAGE');
      const ids = new Set();
      for (const script of value.scripts) {
        identifier(script?.installationId);
        requireValue(!ids.has(script.installationId), 'CORRUPT_STORAGE');
        ids.add(script.installationId);
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
      else facts.scripts[index] = { ...facts.scripts[index], ...installed };
    }
    try { atomicWrite(this.path, stringifyJSONValue(facts) + '\n'); }
    catch (error) {
      if (stringifyJSONValue(this.facts()) === stringifyJSONValue(facts)) throw fault('COMMIT_UNKNOWN');
      throw safeError(error, 'STORAGE_ERROR');
    }
    return this.read();
  }
  prune(installationId) {
    requireValue(!this.inputs().scripts.some(script => script.installationId === installationId), 'STORAGE_CONFLICT');
    const facts = this.facts();
    facts.scripts = facts.scripts.filter(script => script.installationId !== installationId);
    atomicWrite(this.path, stringifyJSONValue(facts) + '\n');
  }
}
