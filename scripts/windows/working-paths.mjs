import { fileURLToPath } from 'node:url';
import { lstatSync } from 'node:fs';
import { isAbsolute, join, parse, relative, resolve, sep } from 'node:path';

export function assertOwnedPath(owner, target, { allowRoot = false, allowMissing = false } = {}) {
  if (!isAbsolute(owner) || !isAbsolute(target)) throw new Error('Explicit absolute development paths are required');
  const root = resolve(owner), full = resolve(target), delta = relative(root, full);
  if ((!allowRoot && !delta) || delta === '..' || delta.startsWith(`..${sep}`) || isAbsolute(delta))
    throw new Error('Development path leaves its Windows owner');
  if (process.platform === 'win32' && (!/^[a-z]:\\/i.test(full) || full.slice(2).includes(':')))
    throw new Error('Development paths must be local and cannot use alternate data streams');
  let current = parse(full).root;
  for (const part of full.slice(current.length).split(sep).filter(Boolean)) {
    current = join(current, part);
    let info;
    try { info = lstatSync(current); }
    catch (error) {
      if (allowMissing && error.code === 'ENOENT') return full;
      throw error;
    }
    if (info.isSymbolicLink()) throw new Error('Development paths cannot traverse links or junctions');
  }
  return full;
}

// Relative to this repository, not to a shell or chat's current directory.
// These paths are for development artifacts, never runtime application data.
export const windowsRoot = resolve(fileURLToPath(new URL('../../', import.meta.url)));
export const buildRoot = join(windowsRoot, 'build', 'windows');
export const tmpRoot = join(windowsRoot, 'tmp');
export const workingRoot = join(tmpRoot, 'working');
assertOwnedPath(windowsRoot, windowsRoot, { allowRoot: true });
assertOwnedPath(windowsRoot, buildRoot, { allowMissing: true });
assertOwnedPath(windowsRoot, tmpRoot, { allowMissing: true });
assertOwnedPath(tmpRoot, workingRoot, { allowMissing: true });
export const fixtureTools = assertOwnedPath(tmpRoot, join(tmpRoot, 'tools', 'fixtures'), { allowMissing: true });
export const temporaryRoot = assertOwnedPath(tmpRoot, join(tmpRoot, 'temporary'), { allowMissing: true });
export const testResultsRoot = assertOwnedPath(tmpRoot, join(tmpRoot, 'test-results'), { allowMissing: true });
