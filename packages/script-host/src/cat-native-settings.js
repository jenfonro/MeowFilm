import { stringifyJSONValue, cloneJSONValue as clone } from '@meowfilm/runtime-bridge/json';
import { requireValue } from './errors.js';
import { CatStorage } from './cat-storage.js';

const factsKeys = ['installationId', 'sourceHash', 'version', 'config', 'configNodes', 'loadedIdentity',
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

/** Native owns source intent; only runtime installation facts are persisted here. */
export class CatNativeSettings {
  constructor(dataRoot, configuration) {
    this.storage = new CatStorage(dataRoot, 'native');
    this.configuration = configuration;
    this.value = empty();
    this.ready = this.storage.ready.then(() => { this.value = this.read(); });
  }
  inputs() {
    const value = clone(this.configuration.read() ?? empty());
    requireValue(value?.version === 1 && Array.isArray(value.scripts), 'CORRUPT_STORAGE');
    return value;
  }
  facts() {
    return clone(this.storage.value);
  }
  read() {
    const input = this.inputs(), facts = this.facts();
    return { ...input, scripts: input.scripts.map(script => {
      const installed = facts.scripts.find(value => value.installationId === script.installationId);
      return { sourceHash: null, version: '0.0.0', loadedIdentity: null,
        importedAt: null, updatedAt: null, updateState: 'idle', updateError: null, sites: [],
        ...installed, ...script, inputIdentity: identity(script) };
    }) };
  }
  async reload() {
    await this.configuration.reload();
    await this.storage.reload();
    return this.read();
  }
  publish(value) { this.value = clone(value); }
  async save(change) {
    await this.storage.save(latest => {
      const inputs = this.inputs();
      const current = { ...inputs, scripts: inputs.scripts.map(input => {
        const installed = latest.scripts.find(fact => fact.installationId === input.installationId);
        return { sourceHash: null, version: '0.0.0', loadedIdentity: null,
          importedAt: null, updatedAt: null, updateState: 'idle', updateError: null, sites: [],
          ...installed, ...input, inputIdentity: identity(input) };
      }) };
      const next = change(clone(current));
      requireValue(next.scripts.length === current.scripts.length && next.scripts.every(script =>
        current.scripts.some(input => sameInput(input, script))), 'PERMISSION_DENIED');
      for (const script of next.scripts) {
        const installed = Object.fromEntries(factsKeys.filter(key => script[key] !== undefined).map(key => [key, script[key]]));
        const index = latest.scripts.findIndex(value => value.installationId === script.installationId);
        if (index < 0) latest.scripts.push(installed);
        else latest.scripts[index] = installed;
      }
      return latest;
    });
    return this.read();
  }
  async prune(installationId) {
    requireValue(!this.inputs().scripts.some(script => script.installationId === installationId), 'STORAGE_CONFLICT');
    await this.storage.save(facts => ({ ...facts,
      scripts: facts.scripts.filter(script => script.installationId !== installationId) }));
  }
  close() { return this.storage.close(); }
}
