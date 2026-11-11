import * as fs from 'node:fs';
import { randomUUID } from 'node:crypto';
import { isAbsolute, join, resolve } from 'node:path';
import { PanError, requireValue } from './errors.js';
import { object, parseJSON } from './values.js';

const empty = () => ({ version: 1, accounts: {} });

function state(path) {
  try { return fs.statSync(path); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

function document(value, code) {
  requireValue(object(value) && value.version === 1 && object(value.accounts), code);
  return value;
}

function failure(error) {
  return error instanceof PanError && error.code === 'CORRUPT_STORAGE' ?
    error : new PanError('STORAGE_ERROR');
}

export class PanStore {
  #root;
  #path;

  constructor(dataRoot) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot) &&
      !dataRoot.includes('\0') && !dataRoot.split(/[\\/]/).includes('..'));
    this.#root = resolve(dataRoot, 'pan');
    this.#path = join(this.#root, 'accounts.json');
  }

  read() {
    let fd;
    try {
      const root = state(this.#root);
      if (!root) return empty();
      requireValue(root.isDirectory(), 'STORAGE_ERROR');
      fs.chmodSync(this.#root, 0o700);
      const entry = state(this.#path);
      if (!entry) return empty();
      requireValue(entry.isFile(), 'STORAGE_ERROR');
      fd = fs.openSync(this.#path, fs.constants.O_RDONLY);
      fs.fchmodSync(fd, 0o600);
      const bytes = fs.readFileSync(fd);
      let value;
      try { value = parseJSON(bytes); }
      catch { throw new PanError('CORRUPT_STORAGE'); }
      return document(value, 'CORRUPT_STORAGE');
    } catch (error) { throw failure(error); }
    finally {
      if (fd !== undefined) {
        try { fs.closeSync(fd); } catch { throw new PanError('STORAGE_ERROR'); }
      }
    }
  }

  write(value) {
    let fd, temporary, error;
    try {
      document(value, 'STORAGE_ERROR');
      const bytes = Buffer.from(JSON.stringify(value));
      this.read();
      fs.mkdirSync(this.#root, { recursive: true, mode: 0o700 });
      fs.chmodSync(this.#root, 0o700);
      temporary = join(this.#root, `.accounts-${randomUUID()}.tmp`);
      fd = fs.openSync(temporary,
        fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
      fs.fchmodSync(fd, 0o600);
      fs.writeFileSync(fd, bytes);
      fs.fsyncSync(fd);
      fs.closeSync(fd);
      fd = undefined;
      // Commit point: a successful rename must not be reported as an uncommitted write.
      fs.renameSync(temporary, this.#path);
      temporary = undefined;
    } catch (caught) { error = caught; }
    finally {
      if (fd !== undefined) {
        try { fs.closeSync(fd); } catch (caught) { error ??= caught; }
      }
      if (temporary !== undefined) {
        try { fs.unlinkSync(temporary); }
        catch (caught) { if (caught.code !== 'ENOENT') error ??= caught; }
      }
    }
    if (error) throw failure(error);
  }

  close() {}
}
