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
  const contexts = [];
  const properties = { Configuration: 'Debug', RuntimeIdentifier: 'win-x64', SelfContained: 'false',
    VlcWindowsX64Enabled: 'true' };
  const verify = () => verifyWindowsProjects(root, { properties, evaluate: (path, properties) => {
    contexts.push(properties); return entries.get(path);
  } });
  return { app, core, feature, entries, verify, properties, contexts };
}

test('uses the evaluated graph and configuration instead of a fixed project list', t => {
  const f = fixture(t), policy = f.verify();
  assert.deepEqual(policy.application.assemblies, ['MeowFilm.Capabilities', 'MeowFilm.Core', 'meowfilm']);
  assert.deepEqual(f.contexts, [f.properties, f.properties, f.properties]);
  assert.deepEqual(policy.properties, f.properties);
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
    runtimeTarget: { name: target }, targets: { [target]: { 'Dependency/1': {
      runtime: { 'lib/Dependency.dll': {} }, native: { 'runtimes/win-x64/native/Native.dll': {} },
      runtimeTargets: {
        'runtimes/win-x64/native/RuntimeTarget.dll': { rid: 'win-x64', assetType: 'native' },
        'runtimes/win-arm64/native/OtherRuntimeTarget.dll': { rid: 'win-arm64', assetType: 'native' },
      },
    } } } };
  const files = [...names.map(name => name + '.dll'), 'meowfilm.exe', 'Dependency.dll', 'Native.dll',
    'RuntimeTarget.dll'];
  assert.deepEqual(verifyPublishedGraph('aggregate', deps, files, policy).projects, names);
  assert.throws(() => verifyPublishedGraph('different', deps, files, policy), /data identity/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'meowfilm.exe'), policy), /assemblies/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, [...files, 'MeowFilm.Unlisted.dll'], policy), /assemblies/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, [...files, 'RetiredLibrary.dll'], policy), /assemblies/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'Dependency.dll'), policy), /runtime asset/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'Native.dll'), policy),
    /missing native runtime asset/);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(n => n !== 'RuntimeTarget.dll'), policy),
    /missing runtime target asset/);
  assert.throws(() => verifyPublishedGraph('aggregate', {
    ...deps, runtimeTarget: { name: 'net10.0/win-arm64' },
  }, files, policy), /runtime identifier/);
});

test('published assemblies follow custom evaluated identities rather than product name prefixes', t => {
  const f = fixture(t);
  f.entries.get(f.app).Properties.AssemblyName = 'Viewer';
  f.entries.get(f.core).Properties.AssemblyName = 'FoundationKit';
  f.entries.get(f.feature).Properties.AssemblyName = 'FilmExperience';
  const policy = f.verify(), names = policy.application.assemblies;
  const target = 'net10.0/win-x64';
  const deps = { libraries: Object.fromEntries(names.map(name => [name + '/1.0.0', { type: 'project' }])),
    runtimeTarget: { name: target }, targets: { [target]: {} } };
  const files = [...names.map(name => name + '.dll'), 'Viewer.exe'];
  assert.deepEqual(verifyPublishedGraph('aggregate', deps, files, policy).projects, names);
  assert.throws(() => verifyPublishedGraph('aggregate', deps, files.filter(name => name !== 'FoundationKit.dll'), policy),
    /assemblies/);
});
