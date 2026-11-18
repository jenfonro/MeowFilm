import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync, symlinkSync } from 'node:fs';
import { dirname, join, posix } from 'node:path';
import { verifyAppStructure } from '../scripts/windows/verify-app-structure.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

const metadataFiles = [
  'modules/metadata-data.mjs', 'modules/metadata/douban.mjs', 'modules/metadata/network.mjs',
  'modules/metadata/settings.mjs', 'modules/metadata/tmdb.mjs',
];

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const source = mkdtempSync(join(temporaryRoot, 'app-structure-'));
  t.after(() => { assertOwnedPath(temporaryRoot, source); rmSync(source, { recursive: true }); });
  for (const folder of ['apps/windows', 'apps/runtime', 'packages', 'scripts/windows', 'scripts/quickjs'])
    mkdirSync(join(source, folder), { recursive: true });
  const write = (path, value) => {
    const file = assertOwnedPath(source, join(source, path), { allowMissing: true });
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, typeof value === 'string' || Buffer.isBuffer(value)
      ? value : `${JSON.stringify(value, null, 2)}\n`);
  };
  write('package.json', { workspaces: ['packages/*'] });
  for (const [name, exports] of [
    ['runtime-bridge', { '.': './src/index.js', './json': './src/json-value.js' }],
    ['script-host', { '.': './src/index.js', './native-json': './src/native-json.js' }],
    ['source-runtime', { '.': './src/index.js' }],
    ['novel-rules', { '.': './src/index.js' }],
    ['pan-runtime', { '.': './src/index.js' }],
    ['film-playback', { '.': './src/index.js' }],
    ['netease-token', { '.': './src/index.js' }],
  ]) {
    write(`packages/${name}/package.json`, { name: `@meowfilm/${name}`, exports });
    for (const target of Object.values(exports)) write(`packages/${name}/${target.slice(2)}`, 'export {};');
  }
  write('apps/runtime/bootstrap.mjs', "import './modules/music-script.mjs';");
  write('apps/runtime/entry.mjs', "export { createRuntime } from './bootstrap.mjs';");
  write('apps/runtime/pan-entry.mjs', 'export {};');
  write('apps/runtime/client.mjs', 'export {};');
  write('apps/runtime/contracts.mjs', 'export {};');
  write('apps/runtime/modules/source-candidates.mjs', 'export {};');
  write('apps/runtime/modules/source-library.mjs', 'export {};');
  write('apps/runtime/modules/source-web.mjs', 'export {};');
  write('apps/runtime/modules/source-rules.mjs', 'export {};');
  write('apps/runtime/modules/music-script.mjs', 'export {};');
  write('apps/runtime/modules/cat-script.mjs', 'export {};');
  write('apps/runtime/modules/source-runtime.mjs', 'export {};');
  write('apps/runtime/modules/pan-runtime.mjs', 'export {};');
  write('apps/runtime/modules/film-playback.mjs', 'export {};');
  write('apps/runtime/modules/netease-token.mjs', 'export {};');
  for (const file of metadataFiles) write(`apps/runtime/${file}`, 'export {};');
  write('apps/windows/runtime/bootstrap.mjs',
    "import { Socket } from 'node:net';\nimport './native-settings.mjs';");
  write('apps/windows/runtime/native-settings.mjs', `
    import { BridgeError } from '@meowfilm/runtime-bridge';
    import { parseJSONText } from '@meowfilm/runtime-bridge/json';
  `);
  write('apps/windows/runtime/entries/aggregate.mjs',
    "import '../bootstrap.mjs';\nimport '../../../runtime/entry.mjs';");
  write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<UserControl x:Class="MeowFilm.Desktop.Page"><ResourceDictionary Source="Theme.xaml"/></UserControl>');
  write('apps/windows/src/MeowFilm.Desktop/Theme.xaml', '<ResourceDictionary/>');
  return { source, write };
}

test('checks relative imports and resources in the declared source tree', t => {
  const result = verifyAppStructure(fixture(t).source);
  assert.equal(result.relativeImports, 5);
  assert.ok(result.sourceFiles.includes('apps/windows/runtime/native-settings.mjs'));
  assert.equal(result.resourceReferences, 1);
});

test('shared metadata sources and public HTTP imports resolve', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/metadata-data.mjs', `
    import '../contracts.mjs';
    import './metadata/settings.mjs';
    import './metadata/network.mjs';
    import './metadata/tmdb.mjs';
    import './metadata/douban.mjs';
  `);
  f.write('apps/runtime/modules/metadata/network.mjs', `
    import 'node:http';
    import 'node:https';
    import '@meowfilm/script-host';
  `);
  const result = verifyAppStructure(f.source);
  assert.equal(result.relativeImports, 10);
  assert.ok(result.sourceFiles.includes('apps/runtime/modules/metadata/network.mjs'));
});

test('shared pan entry and public workspace import coexist with metadata sources', t => {
  const f = fixture(t);
  f.write('apps/runtime/pan-entry.mjs',
    "export { createPanRuntimeModule } from './modules/pan-runtime.mjs';");
  f.write('apps/runtime/modules/pan-runtime.mjs', `
    import '@meowfilm/pan-runtime';
    import '../contracts.mjs';
  `);
  assert.equal(verifyAppStructure(f.source).relativeImports, 7);
});

test('film business module uses its public workspace without platform dependencies', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/film-playback.mjs', `
    import '@meowfilm/film-playback';
    import '../contracts.mjs';
  `);
  assert.equal(verifyAppStructure(f.source).relativeImports, 6);
});

test('novel rule module uses the public program and native JSON entry', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/source-rules.mjs', `
    import '@meowfilm/novel-rules';
    import '@meowfilm/script-host/native-json';
  `);
  assert.equal(verifyAppStructure(f.source).relativeImports, 5);
});

for (const [file, importer] of [
  ['modules/metadata-data.mjs', 'entry.mjs'],
  ...metadataFiles.filter(file => file !== 'modules/metadata-data.mjs')
    .map(file => [file, 'modules/metadata-data.mjs']),
  ['modules/pan-runtime.mjs', 'pan-entry.mjs'],
  ['modules/film-playback.mjs', 'entry.mjs'],
  ['modules/netease-token.mjs', 'entry.mjs'],
  ['modules/source-library.mjs', 'modules/source-runtime.mjs'],
  ['modules/source-web.mjs', 'modules/source-runtime.mjs'],
  ['modules/source-rules.mjs', 'modules/source-runtime.mjs'],
]) {
  test(`shared runtime rejects a missing imported module ${file}`, t => {
    const f = fixture(t);
    const target = `apps/runtime/${file}`;
    const owner = `apps/runtime/${importer}`;
    const specifier = posix.relative(posix.dirname(owner), target).startsWith('.')
      ? posix.relative(posix.dirname(owner), target)
      : `./${posix.relative(posix.dirname(owner), target)}`;
    f.write(owner, `import '${specifier}';`);
    rmSync(join(f.source, 'apps/runtime', file));
    assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
  });
}

for (const file of [
  'unlisted-helper.mjs', 'modules/metadata/unlisted-helper.mjs',
  'modules/metadata/new-helper/unlisted-helper.mjs',
]) {
  test(`shared runtime automatically includes the new module ${file}`, t => {
    const f = fixture(t);
    f.write(`apps/runtime/${file}`, 'export {};');
    assert.ok(verifyAppStructure(f.source).sourceFiles.includes(`apps/runtime/${file}`));
  });
}

test('tool scripts may load runtime-selected modules but application imports stay statically checkable', t => {
  const f = fixture(t);
  f.write('scripts/accept-reader-sources.mjs', 'export const load = path => import(path);');
  assert.ok(verifyAppStructure(f.source).sourceFiles.includes('scripts/accept-reader-sources.mjs'));
  f.write('apps/runtime/bootstrap.mjs', 'export const load = path => import(path);');
  assert.throws(() => verifyAppStructure(f.source), /statically resolvable/);
});

test('Windows scope ignores other-platform sources and generated-runtime templates', t => {
  const f = fixture(t);
  f.write('scripts/ios-node-preflight/probe.mjs', "import './app/bootstrap.mjs';");
  f.write('scripts/ios/package-runtime.mjs', "import './missing-ios-input.mjs';");
  f.write('apps/ios/runtime/bootstrap.mjs', "import './missing-platform-input.mjs';");
  const result = verifyAppStructure(f.source);
  assert.equal(result.relativeImports, 5);
  assert.ok(result.sourceFiles.every(file => !file.includes('ios')));
});

test('Windows tools and their shared dependencies remain checked', t => {
  const f = fixture(t);
  f.write('scripts/windows/package-runtime.mjs', "export { packageRuntime } from '../package-runtime.mjs';");
  f.write('scripts/package-runtime.mjs', "export { packageRuntime } from './verify-dependency-lock.mjs';");
  f.write('scripts/verify-dependency-lock.mjs', 'export const packageRuntime = () => {};');
  assert.equal(verifyAppStructure(f.source).relativeImports, 7);
  f.write('scripts/verify-dependency-lock.mjs', "import './missing.mjs';");
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

test('new shared Node tools and their dependencies are collected without registration', t => {
  const f = fixture(t);
  f.write('scripts/windows/tool.mjs', "import '../fresh-helper.mjs';");
  f.write('scripts/fresh-helper.mjs', "export { value } from './another-helper.mjs';");
  f.write('scripts/another-helper.mjs', 'export const value = 1;');
  const result = verifyAppStructure(f.source);
  assert.ok(result.sourceFiles.includes('scripts/fresh-helper.mjs'));
  assert.ok(result.sourceFiles.includes('scripts/another-helper.mjs'));
  assert.equal(result.relativeImports, 7);
  f.write('scripts/another-helper.mjs', "import './missing.mjs';");
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

test('standalone shared Node tools are checked without an importing entry', t => {
  const f = fixture(t);
  f.write('scripts/fresh-tool.mjs', "import './missing.mjs';");
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

for (const owner of ['scripts/windows/tool.mjs', 'scripts/package-runtime.mjs', 'packages/example/index.js']) {
  test(owner + ' cannot depend on an iOS-only tool', t => {
    const f = fixture(t);
    f.write('scripts/ios/package-runtime.mjs', 'export {};');
    const specifier = owner.startsWith('scripts/windows/') ? '../ios/package-runtime.mjs'
      : owner.startsWith('packages/') ? '../../scripts/ios/package-runtime.mjs' : './ios/package-runtime.mjs';
    f.write(owner, `import '${specifier}';`);
    assert.throws(() => verifyAppStructure(f.source), /Windows source dependency outside its scope/);
  });
}

for (const [specifier, error] of [
  ['../../../outside.mjs', /external source/],
  ['./missing.mjs', /missing\/linked relative import/],
]) {
  test('rejects invalid relative import: ' + specifier, t => {
    const f = fixture(t);
    f.write('apps/runtime/bootstrap.mjs', `import '${specifier}';`);
    assert.throws(() => verifyAppStructure(f.source), error);
  });
}

test('resolves WinUI resource references and rejects missing resources', t => {
  const f = fixture(t);
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<ResourceDictionary Source="ms-appx:///MeowFilm.Desktop/Theme.xaml"/>');
  assert.equal(verifyAppStructure(f.source).resourceReferences, 1);
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<ResourceDictionary Source="ms-appx:///MeowFilm.Desktop/Missing.xaml"/>');
  assert.throws(() => verifyAppStructure(f.source), /missing\/unsafe XAML resource/);
});

test('Windows resources cannot depend on the other platform', t => {
  const f = fixture(t);
  f.write('apps/ios/Theme.xaml', '<ResourceDictionary/>');
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<ResourceDictionary Source="../../../ios/Theme.xaml"/>');
  assert.throws(() => verifyAppStructure(f.source), /missing\/unsafe XAML resource/);
});

test('embedded resources are inventoried and must resolve to authored shared inputs', t => {
  const f = fixture(t);
  f.write('packages/film-search/film-search.js', 'globalThis.meowfilmProcess = value => value;');
  const project = 'apps/windows/src/MeowFilm.Capabilities/MeowFilm.Capabilities.csproj';
  f.write(project, '<Project><EmbeddedResource Include="../../../../packages/film-search/film-search.js" LogicalName="MeowFilm.FilmSearch.js" /></Project>');
  const result = verifyAppStructure(f.source);
  assert.deepEqual(result.embeddedResources, [{
    project, source: 'packages/film-search/film-search.js', logicalName: 'MeowFilm.FilmSearch.js',
  }]);
  assert.equal(result.resourceReferences, 2);
  f.write(project, '<Project><EmbeddedResource Include="missing.js" LogicalName="MeowFilm.FilmSearch.js" /></Project>');
  assert.throws(() => verifyAppStructure(f.source), /missing\/unsafe embedded resource/);
});

test('embedded resource identities cannot collide or import the other platform', t => {
  const f = fixture(t);
  f.write('packages/film-search/film-search.js', 'globalThis.meowfilmProcess = value => value;');
  const project = 'apps/windows/src/MeowFilm.Capabilities/MeowFilm.Capabilities.csproj';
  const resource = '<EmbeddedResource Include="../../../../packages/film-search/film-search.js" LogicalName="MeowFilm.FilmSearch.js" />';
  f.write(project, `<Project>${resource}${resource}</Project>`);
  assert.throws(() => verifyAppStructure(f.source), /duplicate embedded resource identity/);
  f.write('apps/ios/fixture.js', 'export {};');
  f.write(project, '<Project><EmbeddedResource Include="../../../ios/fixture.js" /></Project>');
  assert.throws(() => verifyAppStructure(f.source), /missing\/unsafe embedded resource/);
});

test('shared tool links cannot hide dependency inputs', t => {
  const f = fixture(t);
  f.write('packages/shared/package-runtime.mjs', 'export {};');
  symlinkSync(join(f.source, 'packages/shared'), join(f.source, 'scripts/tools'),
    process.platform === 'win32' ? 'junction' : 'dir');
  f.write('scripts/package-runtime.mjs', "import './tools/package-runtime.mjs';");
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

test('rejects linked source directories', t => {
  const f = fixture(t);
  symlinkSync(join(f.source, 'packages'), join(f.source, 'apps/windows/alias'),
    process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => verifyAppStructure(f.source), /linked source/);
});

test('rejects XAML fields with the enclosing class name', t => {
  const f = fixture(t);
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<UserControl x:Class="MeowFilm.Desktop.Page"><Grid x:Name="Page"/></UserControl>');
  assert.throws(() => verifyAppStructure(f.source), /generated member conflicts/);
});

test('shared runtime cannot import the Windows transport', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/music-script.mjs', "import '../../windows/runtime/bootstrap.mjs';");
  assert.throws(() => verifyAppStructure(f.source), /shared runtime platform dependency/);
});

for (const owner of [
  'apps/runtime/modules/music-script.mjs', 'apps/runtime/modules/metadata/network.mjs',
  'apps/windows/runtime/native-settings.mjs', 'apps/windows/runtime/entries/aggregate.mjs',
]) {
  test(`${owner} accepts Node builtins without per-file registration`, t => {
    const f = fixture(t);
    f.write(owner, `
      import 'node:net';
      import 'node:fs';
      import 'node:child_process';
      import 'node:url';
      import 'util';
      import 'fs/promises';
      import 'node:test';
    `);
    assert.doesNotThrow(() => verifyAppStructure(f.source));
  });
}

for (const specifier of ['@meowfilm/script-host/src/host.js', '@meowfilm/pan-runtime/src/index.js',
  '@meowfilm/film-playback/src/service.js']) {
  test(`metadata helpers cannot bypass public imports with ${specifier}`, t => {
    const f = fixture(t);
    f.write('apps/runtime/modules/metadata/network.mjs', `import '${specifier}';`);
    assert.throws(() => verifyAppStructure(f.source), /runtime private\/undeclared import/);
  });
}

for (const platform of ['windows', 'ios']) {
  test(`metadata helpers cannot import the ${platform} transport`, t => {
    const f = fixture(t);
    f.write(`apps/${platform}/runtime/bootstrap.mjs`, 'export {};');
    f.write('apps/runtime/modules/metadata/network.mjs',
      `import '../../../${platform}/runtime/bootstrap.mjs';`);
    assert.throws(() => verifyAppStructure(f.source), /shared runtime platform dependency/);
  });
}

test('Windows runtime resolves new nested helpers and their public dependencies', t => {
  const f = fixture(t);
  f.write('apps/windows/runtime/bootstrap.mjs', "import './helpers/settings.mjs';");
  f.write('apps/windows/runtime/helpers/settings.mjs', `
    export { parseJSONText } from '@meowfilm/runtime-bridge/json';
    import './paths/storage.mjs';
  `);
  f.write('apps/windows/runtime/helpers/paths/storage.mjs', "import 'node:path';");
  const result = verifyAppStructure(f.source);
  assert.ok(result.sourceFiles.includes('apps/windows/runtime/helpers/settings.mjs'));
  assert.ok(result.sourceFiles.includes('apps/windows/runtime/helpers/paths/storage.mjs'));
  rmSync(join(f.source, 'apps/windows/runtime/helpers/paths/storage.mjs'));
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

test('Windows runtime requires its imported private settings helper', t => {
  const f = fixture(t);
  rmSync(join(f.source, 'apps/windows/runtime/native-settings.mjs'));
  assert.throws(() => verifyAppStructure(f.source), /missing\/linked relative import/);
});

for (const owner of ['apps/windows/runtime/native-settings.mjs', 'apps/windows/runtime/entries/aggregate.mjs']) {
  test(`${owner} accepts new public workspace subpaths but rejects private and missing targets`, t => {
    const f = fixture(t);
    f.write('packages/script-host/package.json', {
      name: '@meowfilm/script-host',
      exports: { '.': './src/index.js', './helper': './src/helper.js' },
    });
    f.write('packages/script-host/src/helper.js', 'export const value = 1;');
    f.write(owner, "import '@meowfilm/script-host/helper';");
    assert.doesNotThrow(() => verifyAppStructure(f.source));
    f.write(owner, "import '@meowfilm/script-host/src/helper.js';");
    assert.throws(() => verifyAppStructure(f.source), /runtime private\/undeclared import/);
    f.write(owner, "import '@meowfilm/script-host/helper';");
    rmSync(join(f.source, 'packages/script-host/src/helper.js'));
    assert.throws(() => verifyAppStructure(f.source), /runtime private\/undeclared import/);
  });
  for (const specifier of ['node:meowfilm-missing', '@meowfilm/undeclared', '@meowfilm/runtime-bridge/src/index.js']) {
    test(`${owner} rejects unavailable or private imports: ${specifier}`, t => {
      const f = fixture(t);
      f.write(owner, `import '${specifier}';`);
      assert.throws(() => verifyAppStructure(f.source), /runtime private\/undeclared import/);
    });
  }
}

for (const [file, error] of [
  ['entries/aggregate.mjs', /missing runtime source/],
  ['bootstrap.mjs', /missing\/linked relative import/],
]) {
  test(`Windows runtime requires its actual entry dependency ${file}`, t => {
    const f = fixture(t);
    rmSync(join(f.source, 'apps/windows/runtime', file));
    assert.throws(() => verifyAppStructure(f.source), error);
  });
}

test('Windows runtime cannot bypass public workspace exports with a relative import', t => {
  const f = fixture(t);
  f.write('apps/windows/runtime/native-settings.mjs',
    "import '../../../packages/runtime-bridge/src/index.js';");
  assert.throws(() => verifyAppStructure(f.source), /Windows runtime source dependency/);
});

test('native build intermediates are excluded while authored native inputs stay in source scope', t => {
  const f = fixture(t);
  f.write('packages/script-host/build/Release/source-sdk.js', "import './generated-only.js';");
  f.write('packages/script-host/native/fixture.cc', '// native build input');
  const result = verifyAppStructure(f.source);
  assert.ok(result.sourceFiles.includes('packages/script-host/native/fixture.cc'));
  assert.ok(!result.sourceFiles.some(path => path.startsWith('packages/script-host/build/')));
});

test('generated files cannot become authored dependencies through a relative import', t => {
  const f = fixture(t);
  f.write('packages/script-host/build/Release/source-sdk.js', 'export {};');
  f.write('scripts/fresh-helper.mjs', "import '../packages/script-host/build/Release/source-sdk.js';");
  assert.throws(() => verifyAppStructure(f.source), /Windows source dependency outside its scope/);
});
