import { lstatSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parse } from 'acorn';

// These are capability boundaries, not package allowlists. Workspace package
// imports are admitted only when their package exports the requested entry.
export const SHARED_RUNTIME_BUILTINS = new Set([
  'node:buffer', 'node:crypto', 'node:http', 'node:https', 'node:stream',
]);

const IMPORT_DECLARATIONS = new Set([
  'ImportDeclaration', 'ExportNamedDeclaration', 'ExportAllDeclaration', 'ImportExpression',
]);

export function sourceReferences(text) {
  const ast = parse(text, { ecmaVersion: 'latest', sourceType: 'module' });
  const imports = [], globals = [], unresolvedImports = [];
  const portableGlobals = new Set([
    'Buffer', 'process', 'require', 'module', '__dirname', '__filename',
    'Packages', 'Java', 'ActiveXObject', 'Deno', 'Bun',
  ]);
  function requiredSource(node) {
    if (node.type !== 'CallExpression') return null;
    if (node.callee.type === 'Identifier' && node.callee.name === 'require') {
      const source = node.arguments[0];
      return source?.type === 'Literal' && typeof source.value === 'string'
        || source?.type === 'TemplateLiteral' && source.expressions.length === 0 ? source : null;
    }
    if (node.callee.type === 'MemberExpression' && !node.callee.computed
      && node.callee.object.type === 'Identifier' && node.callee.object.name === 'require'
      && node.callee.property.name === 'resolve') {
      const source = node.arguments[0];
      return source?.type === 'Literal' && typeof source.value === 'string'
        || source?.type === 'TemplateLiteral' && source.expressions.length === 0 ? source : null;
    }
    return null;
  }
  function visit(node, parent) {
    if (!node || typeof node !== 'object') return;
    const source = IMPORT_DECLARATIONS.has(node.type)
      ? node.source
      : requiredSource(node);
    const specifier = source?.type === 'Literal' ? source.value
      : source?.type === 'TemplateLiteral' && source.expressions.length === 0 ? source.quasis[0].value.cooked
        : undefined;
    if (typeof specifier === 'string') imports.push({
      specifier, kind: IMPORT_DECLARATIONS.has(node.type) ? 'import' : 'require',
      type: node.type, start: source.start, end: source.end,
    });
    else if (source) unresolvedImports.push(node.type);
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
  return { imports, unresolvedImports, globals: [...new Set(globals)] };
}

export function packageNameFromSpecifier(specifier) {
  if (specifier.startsWith('.') || specifier.startsWith('node:') || specifier.startsWith('file:'))
    return null;
  return specifier.split('/').slice(0, specifier.startsWith('@') ? 2 : 1).join('/');
}

const packageName = packageNameFromSpecifier;

function exportTarget(value, conditions, wildcard = null) {
  if (value === null) return null;
  if (typeof value === 'string') {
    const target = wildcard === null ? value : value.replaceAll('*', wildcard);
    return target.startsWith('./') && !/[\\%]/.test(target)
      && !target.slice(2).split('/').some(part => ['', '.', '..', 'node_modules'].includes(part))
      ? target : undefined;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const target = exportTarget(item, conditions, wildcard);
      if (target !== undefined) return target;
    }
  } else if (value && typeof value === 'object') {
    for (const [condition, item] of Object.entries(value)) {
      if (!conditions.has(condition)) continue;
      const target = exportTarget(item, conditions, wildcard);
      if (target !== undefined) return target;
    }
  }
  return undefined;
}

export function packageExportTargets(manifest, specifier, kind = 'import') {
  const name = packageName(specifier);
  if (!name || typeof manifest?.name !== 'string' || manifest.name !== name) return [];
  const subpath = specifier === name ? '.' : `.${specifier.slice(name.length)}`;
  const conditions = new Set(['node', kind, 'default']);
  const exports = manifest.exports;
  let target;
  if (exports && !Array.isArray(exports) && typeof exports === 'object'
      && Object.keys(exports).some(key => key.startsWith('.'))) {
    if (Object.hasOwn(exports, subpath)) target = exportTarget(exports[subpath], conditions);
    else {
      const patterns = Object.keys(exports).filter(key => key.includes('*'))
        .sort((a, b) => b.indexOf('*') - a.indexOf('*') || b.length - a.length);
      for (const key of patterns) {
        const [prefix, suffix] = key.split('*');
        if (subpath.startsWith(prefix) && subpath.endsWith(suffix)
            && subpath.length >= prefix.length + suffix.length) {
          target = exportTarget(exports[key], conditions,
            subpath.slice(prefix.length, subpath.length - suffix.length));
          break;
        }
      }
    }
  } else if (subpath === '.') {
    target = exportTarget(exports, conditions);
  }
  return typeof target === 'string' ? [target] : [];
}

function regularPackageFile(root, location, target) {
  let path = resolve(root);
  const parts = [...location.split('/'), ...target.slice(2).split('/')];
  for (let index = 0; index < parts.length; index++) {
    path = join(path, parts[index]);
    let stat;
    try { stat = lstatSync(path); }
    catch (error) { if (error.code === 'ENOENT') return false; throw error; }
    if (stat.isSymbolicLink()) return false;
    if (index === parts.length - 1) return stat.isFile();
    if (!stat.isDirectory()) return false;
  }
  return false;
}

export function publicPackageImport(root, workspaces, specifier, { requireFile = true, kind = 'import' } = {}) {
  const name = packageName(specifier);
  const workspace = name && workspaces.get(name);
  if (!workspace) return null;
  const targets = packageExportTargets(workspace.manifest, specifier, kind);
  if (!targets.length) return null;
  for (const target of targets) {
    if (!requireFile || regularPackageFile(root, workspace.location, target)) {
      return { ...workspace, name, target, path: resolve(root, workspace.location, target.slice(2)) };
    }
  }
  return null;
}

export function workspaceMap(root, manifests) {
  return new Map([...manifests].map(([location, manifest]) => [
    manifest.name, { location, manifest },
  ]));
}
