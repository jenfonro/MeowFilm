import { readFileSync, readdirSync, realpathSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { basename, dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const shared = new Set(['MeowFilm.Core', 'MeowFilm.Platform', 'MeowFilm.Desktop', 'MeowFilm.Media', 'MeowFilm.SystemMedia']);
function fail(message) { throw new Error(`Windows project boundary: ${message}`); }
function inside(base, path) {
  const delta = relative(base, path);
  return delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
}
function evaluateProject(project, configuration) {
  return JSON.parse(execFileSync('dotnet', ['msbuild', project, '-nologo',
    '-getProperty:MSBuildProjectName,AssemblyName', '-getItem:ProjectReference', `-p:Configuration=${configuration}`],
  { cwd: dirname(project), windowsHide: true, encoding: 'utf8' }));
}

// MSBuild evaluates conditional references and imports. The checker does not
// substitute a second package-version table or prohibit standard MSBuild syntax.
export function verifyWindowsProjects(directory = root, { evaluate = evaluateProject, configuration = 'Release' } = {}) {
  directory = realpathSync(directory);
  const graph = new Map(), visiting = new Set();
  function visit(path) {
    path = realpathSync(path);
    if (!inside(directory, path)) fail('project leaves repository');
    if (visiting.has(path)) fail('dependency cycle');
    if (graph.has(path)) return graph.get(path);
    visiting.add(path);
    const result = evaluate(path, configuration);
    const { MSBuildProjectName: name, AssemblyName: assembly } = result.Properties;
    if (!name || !assembly) fail('missing evaluated project identity');
    const references = result.Items.ProjectReference.map(item => realpathSync(item.FullPath));
    if (new Set(references).size !== references.length) fail('duplicate project reference');
    const dependencies = references.map(visit);
    if (shared.has(name) && dependencies.some(item => !shared.has(item.name)))
      fail('shared project depends on application or business: ' + name);
    const entry = { name, assembly };
    graph.set(path, entry);
    visiting.delete(path);
    return entry;
  }
  const app = visit(resolve(directory, 'apps/windows/app/MeowFilm.Windows.App.csproj'));
  const assemblies = [...graph.values()].map(item => item.assembly).sort();
  if (new Set(assemblies).size !== assemblies.length) fail('duplicate assembly identity');
  return { projects: graph.size, application: { name: app.assembly, dataIdentity: 'aggregate',
    projects: [...graph.values()].map(item => item.name).sort(), assemblies } };
}

export function verifyPublishedGraph(dataIdentity, deps, filenames, policy) {
  const app = policy.application;
  if (dataIdentity !== app.dataIdentity) fail('application data identity differs');
  const declared = Object.entries(deps.libraries ?? {}).filter(([, value]) => value.type === 'project')
    .map(([name]) => name.slice(0, name.lastIndexOf('/'))).sort();
  if (declared.join() !== app.assemblies.join()) fail('published project graph differs from the evaluated graph');
  const assemblies = filenames.filter(name => /^(?:MeowFilm\..*|meowfilm(?:-.*)?)\.dll$/i.test(name))
    .map(name => name.slice(0, -4)).sort();
  if (assemblies.join() !== app.assemblies.join() || !filenames.includes(app.name + '.exe'))
    fail('published project assemblies are missing or unlisted');
  const target = deps.targets?.[deps.runtimeTarget?.name];
  if (!target) fail('missing runtime dependency target');
  for (const library of Object.values(target))
    for (const asset of Object.keys(library.runtime ?? {}))
      if (!filenames.includes(basename(asset))) fail('missing managed runtime asset: ' + asset);
  return { application: app.name, dataIdentity, projects: declared };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (!args.length) console.log(JSON.stringify(verifyWindowsProjects(), null, 2));
  else {
    const [flag, directory, identityFlag, identity, configuration = 'Release'] = args;
    if (args.length < 4 || args.length > 5 || flag !== '--build' || identityFlag !== '--identity' || !isAbsolute(directory))
      fail('usage: --build <absolute output> --identity <data identity> [configuration]');
    const policy = verifyWindowsProjects(root, { configuration });
    const deps = JSON.parse(readFileSync(resolve(directory, policy.application.name + '.deps.json'), 'utf8'));
    console.log(JSON.stringify(verifyPublishedGraph(identity, deps, readdirSync(directory), policy), null, 2));
  }
}
