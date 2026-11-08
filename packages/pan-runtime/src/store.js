import * as fs from 'node:fs';
import { randomUUID } from 'node:crypto';
import { isAbsolute, join, parse, relative, resolve, sep } from 'node:path';
import { PanError, requireValue } from './errors.js';
import { object, parseJSON } from './values.js';

const noFollow = fs.constants.O_NOFOLLOW ?? 0;
const empty = () => ({ version: 1, accounts: {} });

function state(path) {
  try { return fs.lstatSync(path); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

function directory(path, create) {
  let current = parse(path).root;
  for (const part of relative(current, path).split(sep).filter(Boolean)) {
    current = join(current, part);
    let entry = state(current);
    if (!entry && create) {
      fs.mkdirSync(current, { mode: 0o700 });
      entry = fs.lstatSync(current);
    }
    if (!entry) return false;
    requireValue(entry.isDirectory() && !entry.isSymbolicLink(), 'STORAGE_ERROR');
  }
  return true;
}

function regular(entry) {
  requireValue(entry.isFile() && !entry.isSymbolicLink() && entry.nlink === 1, 'STORAGE_ERROR');
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
      if (!directory(this.#root, false)) return empty();
      fs.chmodSync(this.#root, 0o700);
      const entry = state(this.#path);
      if (!entry) return empty();
      regular(entry);
      fd = fs.openSync(this.#path, fs.constants.O_RDONLY | noFollow);
      const opened = fs.fstatSync(fd);
      regular(opened);
      requireValue(entry.dev === opened.dev && entry.ino === opened.ino, 'STORAGE_ERROR');
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
      directory(this.#root, true);
      fs.chmodSync(this.#root, 0o700);
      temporary = join(this.#root, `.accounts-${randomUUID()}.tmp`);
      fd = fs.openSync(temporary,
        fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL | noFollow, 0o600);
      fs.fchmodSync(fd, 0o600);
      fs.writeFileSync(fd, bytes);
      fs.fsyncSync(fd);
      fs.closeSync(fd);
      fd = undefined;
      directory(this.#root, false);
      const existing = state(this.#path);
      if (existing) regular(existing);
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
