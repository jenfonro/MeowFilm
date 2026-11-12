import { lstatSync, readdirSync, realpathSync } from 'node:fs';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';

export const REQUIRED_RUNTIME_FILES = Object.freeze([
  'bootstrap.mjs', 'entry.mjs', 'pan-entry.mjs',
]);

function inside(base, target) {
  const path = relative(base, target);
  return path === '' || (path !== '..' && !path.startsWith(`..${sep}`) && !isAbsolute(path));
}

export function discoverRuntimeSource(root) {
  root = realpathSync(resolve(root));
  const directory = resolve(root, 'apps/runtime');
  const files = [];
  function walk(path) {
    let stat;
    try { stat = lstatSync(path); }
    catch (error) {
      if (error.code === 'ENOENT') {
        throw Object.assign(new Error(`Missing shared runtime source: ${relative(root, path)}`),
          { code: 'MISSING_SOURCE' });
      }
      throw error;
    }
    const location = relative(root, path).split(sep).join('/');
    if (stat.isSymbolicLink() || !stat.isDirectory() || !inside(root, realpathSync(path))) {
      throw Object.assign(new Error(`Unsafe shared runtime source: ${location}`),
        { code: stat.isSymbolicLink() ? 'UNSAFE_SYMLINK' : 'INVALID_SOURCE' });
    }
    for (const item of readdirSync(path, { withFileTypes: true })) {
      const child = join(path, item.name);
      const relativeChild = relative(directory, child).split(sep).join('/');
      if (item.isSymbolicLink()) {
        throw Object.assign(new Error(`Unsafe shared runtime source: ${relativeChild}`),
          { code: 'UNSAFE_SYMLINK' });
      }
      if (item.isDirectory()) walk(child);
      else if (item.isFile() && item.name.endsWith('.mjs')) files.push(relativeChild);
      else if (item.isFile()) {
        throw Object.assign(new Error(`Unexpected shared runtime source: ${relativeChild}`),
          { code: 'SHARED_CLOSURE' });
      } else throw Object.assign(new Error(`Invalid shared runtime source: ${relativeChild}`),
        { code: 'INVALID_SOURCE' });
    }
  }
  walk(directory);
  const missingFile = REQUIRED_RUNTIME_FILES.find(path => !files.includes(path));
  if (missingFile) {
    throw Object.assign(new Error(`Missing shared runtime source: ${missingFile}`),
      { code: 'MISSING_SOURCE' });
  }
  return { files: files.sort() };
}
