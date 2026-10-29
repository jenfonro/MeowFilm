import { readdirSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { windowsRoot } from './windows/working-paths.mjs';
import { discoverWorkspaces } from './verify-dependency-lock.mjs';

export function verifyBoundaries(root = windowsRoot) {
  const workspaces = discoverWorkspaces(root, JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')));
  const names = new Set([...workspaces.values()].map(manifest => manifest.name));
  const graph = new Map(), errors = [];
  function walk(directory) {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
      if (['node_modules', 'dist', '.build', 'build', '.npm-cache', '.cache'].includes(entry.name)) return [];
      const path = join(directory, entry.name);
      return entry.isDirectory() ? walk(path) : [path];
    });
  }
  for (const [location, manifest] of workspaces) {
    const base = join(root, location);
    const dependencies = new Set(Object.keys({ ...manifest.dependencies, ...manifest.optionalDependencies,
      ...manifest.peerDependencies, ...manifest.devDependencies }));
    graph.set(manifest.name, [...dependencies].filter(name => names.has(name)));
    for (const file of walk(base).filter(path => /\.[cm]?js$/.test(path) && !/[\\/]tests?[\\/]/.test(path))) {
      const text = readFileSync(file, 'utf8');
      for (const match of text.matchAll(/(?:\bfrom\s*|\bimport\s*(?:\(\s*)?|\brequire\s*\()\s*['"]([^'"]+)['"]/g)) {
        const target = match[1];
        if (target.startsWith('.')) {
          const path = relative(base, resolve(dirname(file), target));
          if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
            errors.push(`${relative(root, file)}: escapes package boundary: ${target}`);
        } else {
          const name = target.split('/').slice(0, target.startsWith('@') ? 2 : 1).join('/');
          if (names.has(name) && name !== manifest.name && !dependencies.has(name))
            errors.push(`${relative(root, file)}: undeclared workspace dependency ${name}`);
        }
      }
    }
  }
  function visit(name, path) {
    if (path.includes(name)) { errors.push('Dependency cycle: ' + [...path, name].join(' -> ')); return; }
    for (const dependency of graph.get(name) ?? []) visit(dependency, [...path, name]);
  }
  for (const name of graph.keys()) visit(name, []);
  assert.equal(errors.length, 0, errors.join('\n'));
  return { packages: workspaces.size };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(`Boundary checks passed for ${verifyBoundaries().packages} declared packages.`);
}
