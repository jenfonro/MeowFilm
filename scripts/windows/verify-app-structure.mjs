import { existsSync, lstatSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const generated = new Set(['bin', 'obj', 'node_modules', '.git']);
const runtimeTrees = new Map([
  ['apps/runtime', {
    files: ['bootstrap.mjs', 'modules/music-script.mjs', 'modules/source-runtime.mjs'],
    directories: ['modules'],
  }],
  ['apps/windows/runtime', { files: ['bootstrap.mjs', 'entries/aggregate.mjs'], directories: ['entries'] }],
]);
const sharedImports = new Set([
  'node:crypto', '@meowfilm/runtime-bridge', '@meowfilm/script-host', '@meowfilm/source-runtime',
]);
const windowsImports = new Set(['node:net', '@meowfilm/runtime-bridge', '@meowfilm/runtime-bridge/descriptor']);
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
  const files = [], directories = [];
  function walk(rel) {
    const path = join(directory, rel);
    if (lstatSync(path).isSymbolicLink() || !samePath(realpathSync(path), path)) fail('linked source: ' + rel);
    directories.push(rel);
    for (const item of readdirSync(join(directory, rel), { withFileTypes: true })) {
      if (generated.has(item.name)) continue;
      const child = rel + '/' + item.name;
      if (item.isSymbolicLink()) fail('linked source: ' + child);
      if (item.isDirectory()) walk(child);
      else if (item.isFile()) files.push(child);
      else fail('non-regular source: ' + child);
    }
  }
  for (const part of ['apps/windows', 'apps/runtime', 'packages', 'scripts']) walk(part);
  for (const [source, expected] of runtimeTrees) {
    const authored = files.filter(file => file.startsWith(source + '/')).map(file => file.slice(source.length + 1));
    if (expected.files.some(file => !authored.includes(file))) fail('missing runtime source: ' + source);
    if (authored.some(file => !expected.files.includes(file)) ||
        directories.filter(path => path.startsWith(source + '/'))
          .some(path => !expected.directories.includes(path.slice(source.length + 1))))
      fail('unexpected runtime source: ' + source);
  }

  let imports = 0, resources = 0;
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
    imports++;
  }
  for (const file of files) {
    if (/\.(?:mjs|js)$/.test(file) && !/(?:^|\/)(?:test|tests|fixtures)\//.test(file.replace(/^tests\/baselines\//, 'baseline/'))) {
      const text = readFileSync(join(directory, file), 'utf8');
      const specs = [
        ...text.matchAll(/^[ \t]*(?:import|export)\s+(?:[^;]*?\s+from\s+)(['"])([^'"\r\n]+)\1/gm),
        ...text.matchAll(/^[ \t]*import\s*(['"])([^'"\r\n]+)\1/gm),
        ...text.matchAll(/\bimport\(\s*(['"])([^'"\r\n]+)\1\s*\)/g),
      ];
      for (const match of specs) {
        const specifier = match[2];
        sourceTarget(file, specifier);
        if (specifier.startsWith('.')) continue;
        if (file.startsWith('apps/runtime/') && !sharedImports.has(specifier))
          fail('shared runtime platform import: ' + file + ' -> ' + specifier);
        if (file.startsWith('apps/windows/runtime/') &&
            (file !== 'apps/windows/runtime/bootstrap.mjs' || !windowsImports.has(specifier)))
          fail('Windows runtime bypasses shared module: ' + file + ' -> ' + specifier);
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
        if (!inside(directory, target) || !existsSync(target) || !lstatSync(target).isFile() ||
            lstatSync(target).isSymbolicLink() || !samePath(realpathSync(target), target))
          fail('missing/unsafe XAML resource: ' + file);
        resources++;
      }
    }
  }
  return { relativeImports: imports, resourceReferences: resources, sourceFiles: files.sort() };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { sourceFiles, ...report } = verifyAppStructure();
  console.log(JSON.stringify({ ...report, authoredFiles: sourceFiles.length }, null, 2));
}
