import { existsSync, lstatSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const generated = new Set(['bin', 'obj', 'node_modules', '.git']);
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
  const files = [];
  function walk(rel) {
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

  let imports = 0, resources = 0;
  function sourceTarget(owner, specifier) {
    if (!specifier.startsWith('.')) return;
    const target = resolve(dirname(join(directory, owner)), specifier);
    if (!inside(directory, target))
      fail('external source dependency: ' + owner);
    if (!existsSync(target) || !lstatSync(target).isFile() || !samePath(realpathSync(target), target))
      fail('missing/linked relative import: ' + owner + ' -> ' + specifier);
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
      for (const match of specs) sourceTarget(file, match[2]);
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
