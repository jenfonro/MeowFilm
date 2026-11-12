import { readFileSync, readdirSync, realpathSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { basename, dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const shared = new Set(['MeowFilm.Core', 'MeowFilm.Platform', 'MeowFilm.Desktop', 'MeowFilm.Media', 'MeowFilm.SystemMedia']);
const DEFAULT_PROPERTIES = Object.freeze({
  Configuration: 'Release', RuntimeIdentifier: 'win-x64', SelfContained: 'true',
  TargetLatestRuntimePatch: 'true', VlcWindowsX64Enabled: 'true',
  VlcWindowsX86Enabled: 'false', VlcWindowsArm64Enabled: 'false',
});
function fail(message) { throw new Error(`Windows project boundary: ${message}`); }
function inside(base, path) {
  const delta = relative(base, path);
  return delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
}
function evaluateProject(project, properties) {
  return JSON.parse(execFileSync('dotnet', ['msbuild', project, '-nologo',
    '-getProperty:MSBuildProjectName,AssemblyName', '-getItem:ProjectReference',
    ...Object.entries(properties).map(([name, value]) => `-p:${name}=${value}`)],
  { cwd: dirname(project), windowsHide: true, encoding: 'utf8' }));
}

// MSBuild evaluates conditional references and imports. The checker does not
// substitute a second package-version table or prohibit standard MSBuild syntax.
export function verifyWindowsProjects(directory = root, {
  evaluate = evaluateProject, properties = DEFAULT_PROPERTIES,
} = {}) {
  directory = realpathSync(directory);
  const graph = new Map(), visiting = new Set();
  function visit(path) {
    path = realpathSync(path);
    if (!inside(directory, path)) fail('project leaves repository');
    if (visiting.has(path)) fail('dependency cycle');
    if (graph.has(path)) return graph.get(path);
    visiting.add(path);
    const result = evaluate(path, properties);
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
  return { projects: graph.size, properties: { ...properties }, application: { name: app.assembly, dataIdentity: 'aggregate',
    projects: [...graph.values()].map(item => item.name).sort(), assemblies } };
}

export function verifyPublishedGraph(dataIdentity, deps, filenames, policy) {
  const app = policy.application;
  if (dataIdentity !== app.dataIdentity) fail('application data identity differs');
  const declared = Object.entries(deps.libraries ?? {}).filter(([, value]) => value.type === 'project')
    .map(([name]) => name.slice(0, name.lastIndexOf('/'))).sort();
  if (declared.join() !== app.assemblies.join()) fail('published project graph differs from the evaluated graph');
  const runtimeTargetName = deps.runtimeTarget?.name;
  if (!runtimeTargetName || runtimeTargetName.split('/').at(-1) !== policy.properties.RuntimeIdentifier)
    fail('published runtime identifier differs from the evaluated graph');
  if (app.assemblies.some(name => !filenames.includes(name + '.dll')) || !filenames.includes(app.name + '.exe'))
    fail('published project assemblies are missing or unlisted');
  const target = deps.targets?.[deps.runtimeTarget?.name];
  if (!target) fail('missing runtime dependency target');
  const assets = new Set(app.assemblies.map(name => name + '.dll'));
  for (const library of Object.values(target)) {
    for (const asset of Object.keys(library.runtime ?? {})) {
      assets.add(basename(asset));
      if (!filenames.includes(basename(asset))) fail('missing managed runtime asset: ' + asset);
    }
    for (const asset of Object.keys(library.native ?? {})) {
      assets.add(basename(asset));
      if (!filenames.includes(basename(asset))) fail('missing native runtime asset: ' + asset);
    }
    for (const [asset, metadata] of Object.entries(library.runtimeTargets ?? {})) {
      if (metadata.rid !== deps.runtimeTarget.name.split('/').at(-1)) continue;
      assets.add(basename(asset));
      if (!filenames.includes(basename(asset))) fail('missing runtime target asset: ' + asset);
    }
  }
  if (filenames.some(name => /\.dll$/i.test(name) && !assets.has(name)))
    fail('published project assemblies are missing or unlisted');
  return { application: app.name, dataIdentity, projects: declared };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (!args.length) console.log(JSON.stringify(verifyWindowsProjects(), null, 2));
  else {
    const [flag, directory, identityFlag, identity, ...propertyArgs] = args;
    if (args.length < 4 || flag !== '--build' || identityFlag !== '--identity' || !isAbsolute(directory)
        || propertyArgs.some(arg => !/^-p:[A-Za-z_][A-Za-z0-9_]*=.+$/.test(arg)))
      fail('usage: --build <absolute output> --identity <data identity> [-p:Name=Value ...]');
    const properties = Object.fromEntries(propertyArgs.map(arg => {
      const index = arg.indexOf('=');
      return [arg.slice(3, index), arg.slice(index + 1)];
    }));
    if (!properties.Configuration || !properties.RuntimeIdentifier ||
        !['true', 'false'].includes(properties.SelfContained))
      fail('publish verification requires Configuration, RuntimeIdentifier and SelfContained properties');
    const policy = verifyWindowsProjects(root, { properties });
    const deps = JSON.parse(readFileSync(resolve(directory, policy.application.name + '.deps.json'), 'utf8'));
    console.log(JSON.stringify(verifyPublishedGraph(identity, deps, readdirSync(directory), policy), null, 2));
  }
}
