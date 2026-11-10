import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { parse } from 'acorn';
import { windowsRoot } from './windows/working-paths.mjs';
import { discoverWorkspaces } from './verify-dependency-lock.mjs';

const portablePackage = '@meowfilm/source-runtime';
const portableGlobals = new Set([
  'Buffer', 'process', 'require', 'module', '__dirname', '__filename',
  'Packages', 'Java', 'ActiveXObject', 'Deno', 'Bun',
]);

function sourceReferences(text) {
  const ast = parse(text, { ecmaVersion: 'latest', sourceType: 'module' });
  const imports = [], globals = [];
  function visit(node, parent) {
    if (!node || typeof node !== 'object') return;
    const source = ['ImportDeclaration', 'ExportNamedDeclaration', 'ExportAllDeclaration', 'ImportExpression'].includes(node.type)
      ? node.source
      : node.type === 'CallExpression' && node.callee.type === 'Identifier' && node.callee.name === 'require'
        ? node.arguments[0] : null;
    if (source?.type === 'Literal' && typeof source.value === 'string') imports.push(source.value);
    if (node.type === 'MemberExpression' && !node.computed &&
        ['globalThis', 'global', 'window'].includes(node.object?.name) &&
        portableGlobals.has(node.property?.name)) {
      globals.push(node.property.name);
    } else if (node.type === 'Identifier' && portableGlobals.has(node.name)) {
      const propertyKey = parent?.type === 'Property' && parent.key === node &&
        !parent.computed && !parent.shorthand;
      const memberKey = parent?.type === 'MemberExpression' && parent.property === node &&
        !parent.computed;
      const methodKey = ['MethodDefinition', 'PropertyDefinition'].includes(parent?.type) &&
        parent.key === node && !parent.computed;
      const label = ['LabeledStatement', 'BreakStatement', 'ContinueStatement'].includes(parent?.type) &&
        parent.label === node;
      if (!propertyKey && !memberKey && !methodKey && !label) globals.push(node.name);
    }
    for (const [key, value] of Object.entries(node)) {
      if (['start', 'end', 'loc', 'range'].includes(key)) continue;
      if (Array.isArray(value)) {
        for (const child of value) if (child?.type) visit(child, node);
      } else if (value?.type) visit(value, node);
    }
  }
  visit(ast, null);
  return { imports, globals: [...new Set(globals)] };
}

export function verifyBoundaries(root = windowsRoot) {
  const workspaces = discoverWorkspaces(root, JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')));
  const names = new Set([...workspaces.values()].map(manifest => manifest.name));
  const graph = new Map(), errors = [];
  function walk(directory) {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
      if (['node_modules', 'dist', '.build', 'build', '.npm-cache', '.cache'].includes(entry.name)) return [];
      const path = join(directory, entry.name);
      if (entry.isSymbolicLink()) { errors.push(`${relative(root, path)}: linked source`); return []; }
      return entry.isDirectory() ? walk(path) : [path];
    });
  }
  for (const [location, manifest] of workspaces) {
    const base = join(root, location);
    const dependencies = new Set(Object.keys({ ...manifest.dependencies, ...manifest.optionalDependencies,
      ...manifest.peerDependencies, ...manifest.devDependencies }));
    graph.set(manifest.name, [...dependencies].filter(name => names.has(name)));
    for (const file of walk(base).filter(path => /\.[cm]?js$/.test(path) && !/[\\/]tests?[\\/]/.test(path))) {
      const references = sourceReferences(readFileSync(file, 'utf8'));
      for (const target of references.imports) {
        if (manifest.name === portablePackage && target.startsWith('node:')) {
          errors.push(`${relative(root, file)}: portable runtime imports Node builtin ${target}`);
        }
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
      if (manifest.name === portablePackage) {
        for (const name of references.globals) {
          errors.push(`${relative(root, file)}: portable runtime uses host global ${name}`);
        }
      }
    }
  }
  const shared = join(root, 'apps/runtime');
  const sharedImports = new Set([
    'node:buffer', 'node:crypto', 'node:http', 'node:https',
    '@meowfilm/runtime-bridge', '@meowfilm/script-host', '@meowfilm/source-runtime',
    '@meowfilm/pan-runtime', '@meowfilm/film-playback', '@meowfilm/netease-token',
  ]);
  if (existsSync(shared)) {
    if (lstatSync(shared).isSymbolicLink()) errors.push('apps/runtime: linked source');
    else for (const file of walk(shared).filter(path => /\.[cm]?js$/.test(path))) {
      for (const target of sourceReferences(readFileSync(file, 'utf8')).imports) {
        if (target.startsWith('.')) {
          const path = relative(shared, resolve(dirname(file), target));
          if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
            errors.push(`${relative(root, file)}: shared runtime depends on platform source: ${target}`);
        } else if (!sharedImports.has(target)) {
          errors.push(`${relative(root, file)}: shared runtime platform/private import: ${target}`);
        } else if (target.startsWith('@meowfilm/') && !names.has(target)) {
          errors.push(`${relative(root, file)}: undeclared shared runtime package: ${target}`);
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
