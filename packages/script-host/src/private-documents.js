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
  requireValue(fs.statSync(path).isFile(), 'STORAGE_ERROR');
}

/**
 * One owner's private documents below the host data root: `<dataRoot>/<area>/<name>`
 * with at most one sub-folder. The App owns the storage location; document names
 * follow the internal naming contract and writes replace a whole file atomically.
 * Formats belong to the business owner. This is not a guest filesystem.
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

  /** The stored bytes, or null when the document does not exist. */
  read(name) {
    const path = this.#path(name);
    try {
      regularFile(path);
      return fs.readFileSync(path);
    } catch (error) {
      if (error.code === 'ENOENT') return null;
      throw storageFailure(error);
    }
  }

  write(name, bytes) {
    const path = this.#path(name);
    requireValue(Buffer.isBuffer(bytes) || bytes instanceof Uint8Array);
    try {
      atomicWrite(path, bytes);
    } catch (error) { throw storageFailure(error); }
  }

  /** true when a document was removed; a missing one is not an error. */
  remove(name) {
    const path = this.#path(name);
    try {
      fs.unlinkSync(path);
      return true;
    } catch (error) {
      if (error.code === 'ENOENT') return false;
      throw storageFailure(error);
    }
  }

  /** Document names directly inside one sub-folder. */
  list(folder) {
    requireValue(typeof folder === 'string' && folderName.test(folder));
    const path = resolve(this.#root, folder);
    try {
      return fs.readdirSync(path, { withFileTypes: true })
        .filter(entry => folderName.test(entry.name) && (entry.isFile() ||
          entry.isSymbolicLink() && fs.statSync(resolve(path, entry.name), { throwIfNoEntry: false })?.isFile()))
        .map(entry => `${folder}/${entry.name}`);
    } catch (error) {
      if (error.code === 'ENOENT') return [];
      throw storageFailure(error);
    }
  }
}
