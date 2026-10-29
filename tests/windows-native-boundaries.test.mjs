import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { verifyPublishedGraph, verifyWindowsProjects } from '../scripts/windows/verify-projects.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'project-graph-'));
  t.after(() => { assertOwnedPath(temporaryRoot, root); rmSync(root, { recursive: true }); });
  const app = join(root, 'apps/windows/app/MeowFilm.Windows.App.csproj');
  const core = join(root, 'apps/windows/src/MeowFilm.Core/Core.csproj');
  const feature = join(root, 'apps/windows/src/MeowFilm.Capabilities/Feature.csproj');
  const entry = (name, assembly, refs) => ({ Properties: { MSBuildProjectName: name, AssemblyName: assembly },
    Items: { ProjectReference: refs.map(FullPath => ({ FullPath })) } });
  const entries = new Map([
    [app, entry('MeowFilm.Windows.App', 'meowfilm', [feature])],
    [core, entry('MeowFilm.Core', 'MeowFilm.Core', [])],
    [feature, entry('MeowFilm.Capabilities', 'MeowFilm.Capabilities', [core])],
  ]);
  for (const path of entries.keys()) {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, '<Project><Import Project="common.props"/></Project>');
  }
  const configurations = [];
  const verify = () => verifyWindowsProjects(root, { configuration: 'Debug', evaluate: (path, configuration) => {
    configurations.push(configuration); return entries.get(path);
  } });
  return { app, core, feature, entries, verify, configurations };
}

test('uses the evaluated graph and configuration instead of a fixed project list', t => {
  const f = fixture(t), policy = f.verify();
  assert.deepEqual(policy.application.assemblies, ['MeowFilm.Capabilities', 'MeowFilm.Core', 'meowfilm']);
  assert.deepEqual(f.configurations, ['Debug', 'Debug', 'Debug']);
});

test('rejects dependency cycles and shared-to-business dependencies', t => {
  const f = fixture(t);
  f.entries.get(f.core).Items.ProjectReference = [{ FullPath: f.feature }];
  assert.throws(f.verify, /dependency cycle/);
  f.entries.get(f.feature).Items.ProjectReference = [];
  f.entries.get(f.app).Items.ProjectReference = [{ FullPath: f.core }];
  assert.throws(f.verify, /shared project depends/);
});

test('rejects duplicate evaluated project references', t => {
  const f = fixture(t);
  f.entries.get(f.app).Items.ProjectReference.push({ FullPath: f.feature });
  assert.throws(f.verify, /duplicate project reference/);
});

test('published output matches evaluated projects and managed runtime assets', t => {
  const policy = fixture(t).verify(), names = policy.application.assemblies;
  const target = 'net10.0/win-x64';
  const deps = { libraries: Object.fromEntries(names.map(name => [name + '/1.0.0', { type: 'project' }])),
    runtimeTarget: { name: target }, targets: { [target]: { 'Dependency/1': { runtime: { 'lib/Dependency.dll': {} } } } } };
  const files = [...names.map(name => name + '.dll'), 'meowfilm.exe', 'Dependency.dll'];
  assert.deepEqual(verifyPublishedGraph('aggregate', deps, files, policy).projects, names);
  assert.throws(() => verifyPublishedGraph('different', deps, files, policy), /data identity/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'meowfilm.exe'), policy), /assemblies/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, [...files, 'MeowFilm.Unlisted.dll'], policy), /assemblies/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'Dependency.dll'), policy), /runtime asset/);
});
