import { existsSync, lstatSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { isBuiltin } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isWindowsSource, sourceDirectories } from './source-scope.mjs';
import { sourceReferences, publicPackageImport, workspaceMap } from '../module-policy.mjs';
import { discoverRuntimeSource } from '../runtime-source.mjs';
import { discoverWorkspaces } from '../verify-dependency-lock.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
function fail(message) { throw new Error('Windows App structure: ' + message); }
function samePath(a, b) {
  return process.platform === 'win32'
    ? resolve(a).toLowerCase() === resolve(b).toLowerCase()
    : resolve(a) === resolve(b);
}
function inside(base, target) {
  const part = relative(base, target);
  return part !== '..' && !part.startsWith('..' + sep) && !isAbsolute(part);
}

/** Static source/identity/resource gate; never builds, imports App code or opens user data. */
export function verifyAppStructure(directory = root) {
  directory = resolve(directory);
  if (lstatSync(directory).isSymbolicLink() || !samePath(realpathSync(directory), directory)) fail('source root alias');
  const manifest = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'));
  const workspaces = discoverWorkspaces(directory, manifest);
  const packages = workspaceMap(directory, workspaces);
  discoverRuntimeSource(directory);
  const files = [];
  function walk(rel) {
    const path = join(directory, rel);
    if (lstatSync(path).isSymbolicLink() || !samePath(realpathSync(path), path)) fail('linked source: ' + rel);
    for (const item of readdirSync(join(directory, rel), { withFileTypes: true })) {
      const child = rel + '/' + item.name;
      if (!isWindowsSource(child)) continue;
      if (item.isSymbolicLink()) fail('linked source: ' + child);
      if (item.isDirectory()) walk(child);
      else if (item.isFile()) files.push(child);
      else fail('non-regular source: ' + child);
    }
  }
  for (const part of sourceDirectories) walk(part);
  for (const item of readdirSync(join(directory, 'scripts'), { withFileTypes: true })) {
    const file = 'scripts/' + item.name;
    if (!isWindowsSource(file)) continue;
    const path = join(directory, file);
    if (!item.isFile() || !samePath(realpathSync(path), path)) fail('linked/non-regular source: ' + file);
    files.push(file);
  }
  if (!files.includes('apps/windows/runtime/entries/aggregate.mjs'))
    fail('missing runtime source: apps/windows/runtime/entries/aggregate.mjs');

  let imports = 0, resources = 0;
  const embeddedResources = [];
  function sourceTarget(owner, specifier) {
    if (!specifier.startsWith('.')) return;
    const target = resolve(dirname(join(directory, owner)), specifier);
    if (!inside(directory, target))
      fail('external source dependency: ' + owner);
    if (!existsSync(target) || !lstatSync(target).isFile() || !samePath(realpathSync(target), target))
      fail('missing/linked relative import: ' + owner + ' -> ' + specifier);
    if (owner.startsWith('apps/runtime/') && !inside(join(directory, 'apps/runtime'), target))
      fail('shared runtime platform dependency: ' + owner + ' -> ' + specifier);
    if (owner.startsWith('apps/windows/runtime/') &&
        !inside(join(directory, 'apps/windows/runtime'), target) && !inside(join(directory, 'apps/runtime'), target))
      fail('Windows runtime source dependency: ' + owner + ' -> ' + specifier);
    if (!isWindowsSource(relative(directory, target).split(sep).join('/')))
      fail('Windows source dependency outside its scope: ' + owner + ' -> ' + specifier);
    imports++;
  }
  for (const file of files) {
    if (file.endsWith('.csproj')) {
      const text = readFileSync(join(directory, file), 'utf8');
      const names = new Set();
      for (const match of text.matchAll(/<EmbeddedResource\b([^>]*)\/?>/g)) {
        const include = /\bInclude="([^"]+)"/.exec(match[1])?.[1];
        const logicalName = /\bLogicalName="([^"]+)"/.exec(match[1])?.[1];
        if (!include) continue;
        const target = resolve(dirname(join(directory, file)), include.replaceAll('\\', '/'));
        const source = relative(directory, target).split(sep).join('/');
        if (!inside(directory, target) || !isWindowsSource(source) || !existsSync(target) ||
            !lstatSync(target).isFile() || lstatSync(target).isSymbolicLink() || !samePath(realpathSync(target), target))
          fail('missing/unsafe embedded resource: ' + file + ' -> ' + include);
        if (logicalName && names.has(logicalName)) fail('duplicate embedded resource identity: ' + file + ' -> ' + logicalName);
        if (logicalName) names.add(logicalName);
        embeddedResources.push({ project: file, source, logicalName: logicalName ?? null });
        resources++;
      }
    }
    if (/\.(?:mjs|js)$/.test(file) && !/(?:^|\/)(?:test|tests|fixtures)\//.test(file.replace(/^tests\/baselines\//, 'baseline/'))) {
      const text = readFileSync(join(directory, file), 'utf8');
      const references = sourceReferences(text);
      const runtimeSource = file.startsWith('apps/runtime/') || file.startsWith('apps/windows/runtime/');
      const runtimeModule = runtimeSource || file.startsWith('packages/');
      if (runtimeModule && references.unresolvedImports.length)
        fail('module specifiers must be statically resolvable: ' + file);
      for (const { specifier, kind } of references.imports) {
        sourceTarget(file, specifier);
        if (specifier.startsWith('.')) continue;
        if (runtimeSource && !isBuiltin(specifier) && !publicPackageImport(directory, packages, specifier, { kind }))
          fail('runtime private/undeclared import: ' + file + ' -> ' + specifier);
      }
    }
    if (file.endsWith('.xaml')) {
      const text = readFileSync(join(directory, file), 'utf8');
      const className = /\bx:Class="[^"]*\.([^".]+)"/.exec(text)?.[1];
      if (className && [...text.matchAll(/\bx:Name="([^"]+)"/g)].some(match => match[1] === className))
        fail('XAML generated member conflicts with its class: ' + file);
      for (const match of text.matchAll(/<ResourceDictionary\b[^>]*\bSource="([^"]+)"/g)) {
        const uri = match[1], pack = /^\/(MeowFilm\.[A-Za-z]+);component\/(.+)$/.exec(uri);
        const target = pack ? resolve(directory, 'apps/windows/src', pack[1], pack[2])
          : resolve(dirname(join(directory, file)), uri);
        if (!inside(join(directory, 'apps/windows'), target) || !existsSync(target) || !lstatSync(target).isFile() ||
            lstatSync(target).isSymbolicLink() || !samePath(realpathSync(target), target))
          fail('missing/unsafe XAML resource: ' + file);
        resources++;
      }
    }
  }
  return { relativeImports: imports, resourceReferences: resources, embeddedResources, sourceFiles: files.sort() };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { sourceFiles, ...report } = verifyAppStructure();
  console.log(JSON.stringify({ ...report, authoredFiles: sourceFiles.length }, null, 2));
}
