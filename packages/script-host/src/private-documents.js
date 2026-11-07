import * as fs from 'node:fs';
import { isAbsolute, resolve } from 'node:path';
import { atomicWrite } from './private-volume.js';
import { fault, identifier, requireValue, ScriptHostError } from './errors.js';

const segment = '[A-Za-z0-9][A-Za-z0-9._-]{0,127}';
const documentName = new RegExp(`^${segment}(?:/${segment})?$`);
const folderName = new RegExp(`^${segment}$`);

function storageFailure(error) {
  return error instanceof ScriptHostError ? error : fault('STORAGE_ERROR');
}
function regularFile(path) {
  const state = fs.lstatSync(path);
  requireValue(state.isFile() && !state.isSymbolicLink() && state.nlink === 1, 'PERMISSION_DENIED');
}
function plainDirectory(path) {
  const state = fs.lstatSync(path);
  requireValue(state.isDirectory() && !state.isSymbolicLink(), 'PERMISSION_DENIED');
}

/**
 * One owner's private documents below the host data root: `<dataRoot>/<area>/<name>`
 * with at most one sub-folder. Links, other file types and traversal are rejected;
 * writes replace a whole file atomically. Formats belong to the business owner.
 */
export class PrivateDocuments {
  #root;

  constructor(dataRoot, area) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    identifier(area);
    this.#root = resolve(dataRoot, area);
  }

  #path(name) {
    requireValue(typeof name === 'string' && documentName.test(name) && !name.split('/').includes('..'));
    return resolve(this.#root, ...name.split('/'));
  }

  #existing(path) {
    if (!fs.existsSync(this.#root)) return false;
    plainDirectory(this.#root);
    const parent = resolve(path, '..');
    if (parent !== this.#root) {
      if (!fs.existsSync(parent)) return false;
      plainDirectory(parent);
    }
    return fs.existsSync(path);
  }

  /** The stored bytes, or null when the document does not exist. */
  read(name) {
    const path = this.#path(name);
    try {
      if (!this.#existing(path)) return null;
      regularFile(path);
      return fs.readFileSync(path);
    } catch (error) { throw storageFailure(error); }
  }

  write(name, bytes) {
    const path = this.#path(name);
    requireValue(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array);
    try {
      fs.mkdirSync(this.#root, { recursive: true });
      plainDirectory(this.#root);
      const parent = resolve(path, '..');
      if (parent !== this.#root) {
        if (!fs.existsSync(parent)) fs.mkdirSync(parent);
        plainDirectory(parent);
      }
      atomicWrite(path, bytes);
    } catch (error) { throw storageFailure(error); }
  }

  /** true when a document was removed; a missing one is not an error. */
  remove(name) {
    const path = this.#path(name);
    try {
      if (!this.#existing(path)) return false;
      regularFile(path);
      fs.unlinkSync(path);
      return true;
    } catch (error) { throw storageFailure(error); }
  }

  /** Document names directly inside one sub-folder. */
  list(folder) {
    requireValue(typeof folder === 'string' && folderName.test(folder));
    const path = resolve(this.#root, folder);
    try {
      if (!this.#existing(path)) return [];
      plainDirectory(path);
      return fs.readdirSync(path, { withFileTypes: true })
        .filter(entry => entry.isFile() && folderName.test(entry.name))
        .map(entry => `${folder}/${entry.name}`);
    } catch (error) { throw storageFailure(error); }
  }
}
