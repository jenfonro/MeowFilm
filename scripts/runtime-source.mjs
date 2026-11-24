import { lstatSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { isAbsolute, join, posix, relative, resolve, sep } from 'node:path';
import { sourceReferences } from './module-policy.mjs';

export const REQUIRED_RUNTIME_FILES = Object.freeze([
  'bootstrap.mjs', 'entry.mjs', 'pan-entry.mjs',
]);

function inside(base, target) {
  const path = relative(base, target);
  return path === '' || (path !== '..' && !path.startsWith(`..${sep}`) && !isAbsolute(path));
}

function discoverSource(root, source, required) {
  root = realpathSync(resolve(root));
  const directory = resolve(root, source);
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
      else if (!item.isFile()) throw Object.assign(new Error(`Invalid shared runtime source: ${relativeChild}`),
        { code: 'INVALID_SOURCE' });
    }
  }
  walk(directory);
  const missingFile = required.find(path => !files.includes(path));
  if (missingFile) {
    throw Object.assign(new Error(`Missing shared runtime source: ${missingFile}`),
      { code: 'MISSING_SOURCE' });
  }
  return { files: files.sort() };
}

export function discoverRuntimeSource(root, platform) {
  const source = discoverSource(root, 'apps/runtime', REQUIRED_RUNTIME_FILES);
  if (platform) {
    const directory = `apps/${platform}/runtime`;
    const available = discoverSource(root, directory, ['entries/aggregate.mjs']).files;
    const files = new Set();
    function visit(file) {
      if (files.has(file) || !available.includes(file)) return;
      files.add(file);
      const { imports } = sourceReferences(readFileSync(join(root, directory, file), 'utf8'));
      for (const { specifier } of imports) {
        if (specifier.startsWith('.')) visit(posix.normalize(posix.join(posix.dirname(file), specifier)));
      }
    }
    visit('entries/aggregate.mjs');
    source.platform = { source: directory, files: [...files].sort() };
  }
  return source;
}

export function runtimeOutputPath(location, platform) {
  if (location.startsWith('apps/runtime/')) return `app/${location.slice('apps/runtime/'.length)}`;
  const source = `apps/${platform}/runtime/`;
  if (location.startsWith(source)) return `app/${platform}/${location.slice(source.length)}`;
  return null;
}

export function relocateRuntimeSource(text, location, platform) {
  const references = sourceReferences(text), imports = [], replacements = [];
  if (references.unresolvedImports.length) {
    throw Object.assign(new Error(`${location}: module specifiers must be statically resolvable`),
      { code: 'PLATFORM_IMPORT' });
  }
  const path = runtimeOutputPath(location, platform);
  for (const { specifier, start, end } of references.imports) {
    let to = specifier;
    if (specifier.startsWith('.')) {
      const target = runtimeOutputPath(posix.normalize(posix.join(posix.dirname(location), specifier)), platform);
      if (!target) throw Object.assign(new Error(`${location}: relative import escapes the runtime: ${specifier}`),
        { code: 'PLATFORM_IMPORT' });
      to = posix.relative(posix.dirname(path), target);
      if (!to.startsWith('.')) to = `./${to}`;
    }
    imports.push({ from: specifier, to });
    if (to !== specifier) replacements.push({ start, end, value: JSON.stringify(to) });
  }
  for (const { start, end, value } of replacements.sort((a, b) => b.start - a.start))
    text = text.slice(0, start) + value + text.slice(end);
  return { path, bytes: Buffer.from(text), imports };
}
