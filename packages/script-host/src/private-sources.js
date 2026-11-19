import * as fs from 'node:fs';
import { isAbsolute, resolve } from 'node:path';
import { atomicWrite } from './private-volume.js';
import { fault, requireValue, ScriptHostError } from './errors.js';

const sourceName = /^sources\/[a-f0-9]{64}\.js$/;

function storageFailure(error) {
  return error instanceof ScriptHostError ? error : fault('STORAGE_ERROR');
}
function regularFile(path) {
  requireValue(fs.statSync(path).isFile(), 'STORAGE_ERROR');
}

/**
 * Immutable installed JavaScript source bytes. All host-owned catalog and KV
 * state belongs in the shared typed SQLite database; this is not a guest volume.
 */
export class PrivateSources {
  #root;

  constructor(dataRoot, area) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    requireValue(area === 'music' || area === 'cat');
    this.#root = resolve(dataRoot, area);
  }

  /** The stored bytes, or null when the document does not exist. */
  read(name) {
    requireValue(typeof name === 'string' && sourceName.test(name));
    const path = resolve(this.#root, name);
    try {
      regularFile(path);
      return fs.readFileSync(path);
    } catch (error) {
      if (error.code === 'ENOENT') return null;
      throw storageFailure(error);
    }
  }

  write(name, bytes) {
    requireValue(typeof name === 'string' && sourceName.test(name));
    const path = resolve(this.#root, name);
    requireValue(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array);
    try {
      atomicWrite(path, bytes);
    } catch (error) { throw storageFailure(error); }
  }

  /** true when a document was removed; a missing one is not an error. */
  remove(name) {
    requireValue(typeof name === 'string' && sourceName.test(name));
    const path = resolve(this.#root, name);
    try {
      fs.unlinkSync(path);
      return true;
    } catch (error) {
      if (error.code === 'ENOENT') return false;
      throw storageFailure(error);
    }
  }

  /** Content-addressed source files owned by this runtime. */
  list(folder) {
    requireValue(folder === 'sources');
    const path = resolve(this.#root, folder);
    try {
      return fs.readdirSync(path, { withFileTypes: true })
        .filter(entry => /^[a-f0-9]{64}\.js$/.test(entry.name) && (entry.isFile() ||
          entry.isSymbolicLink() && fs.statSync(resolve(path, entry.name), { throwIfNoEntry: false })?.isFile()))
        .map(entry => `${folder}/${entry.name}`);
    } catch (error) {
      if (error.code === 'ENOENT') return [];
      throw storageFailure(error);
    }
  }
}
