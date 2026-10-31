import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync, symlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { verifyAppStructure } from '../scripts/windows/verify-app-structure.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const source = mkdtempSync(join(temporaryRoot, 'app-structure-'));
  t.after(() => { assertOwnedPath(temporaryRoot, source); rmSync(source, { recursive: true }); });
  for (const folder of ['apps/windows', 'apps/runtime', 'packages', 'scripts'])
    mkdirSync(join(source, folder), { recursive: true });
  const write = (path, value) => {
    const file = assertOwnedPath(source, join(source, path), { allowMissing: true });
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, value);
  };
  write('apps/runtime/bootstrap.mjs', "import './modules/music-script.mjs';");
  write('apps/runtime/modules/music-script.mjs', 'export {};');
  write('apps/runtime/modules/cat-script.mjs', 'export {};');
  write('apps/windows/runtime/bootstrap.mjs', "import '../../runtime/bootstrap.mjs';");
  write('apps/windows/runtime/entries/aggregate.mjs',
    "import '../bootstrap.mjs';\nimport '../../../runtime/modules/music-script.mjs';\nimport '../../../runtime/modules/cat-script.mjs';");
  write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<UserControl x:Class="MeowFilm.Desktop.Page"><ResourceDictionary Source="Theme.xaml"/></UserControl>');
  write('apps/windows/src/MeowFilm.Desktop/Theme.xaml', '<ResourceDictionary/>');
  return { source, write };
}

test('checks relative imports and resources in the declared source tree', t => {
  const result = verifyAppStructure(fixture(t).source);
  assert.equal(result.relativeImports, 5);
  assert.equal(result.resourceReferences, 1);
});

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

test('resolves WPF pack references and rejects missing resources', t => {
  const f = fixture(t);
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<ResourceDictionary Source="/MeowFilm.Desktop;component/Theme.xaml"/>');
  assert.equal(verifyAppStructure(f.source).resourceReferences, 1);
  f.write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<ResourceDictionary Source="/MeowFilm.Desktop;component/Missing.xaml"/>');
  assert.throws(() => verifyAppStructure(f.source), /missing\/unsafe XAML resource/);
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

test('shared runtime cannot create a platform transport directly', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/music-script.mjs', "import { Socket } from 'node:net';");
  assert.throws(() => verifyAppStructure(f.source), /shared runtime platform import/);
});

test('Windows runtime contains only the platform bootstrap and thin entry', t => {
  const f = fixture(t);
  f.write('apps/windows/runtime/modules/music-script.mjs', 'export {};');
  assert.throws(() => verifyAppStructure(f.source), /unexpected runtime source: apps\/windows\/runtime/);
});

test('an empty Windows module directory is not a valid platform boundary', t => {
  const f = fixture(t);
  mkdirSync(join(f.source, 'apps/windows/runtime/modules'));
  assert.throws(() => verifyAppStructure(f.source), /unexpected runtime source: apps\/windows\/runtime/);
});

test('Windows entry cannot import script-host instead of its shared business module', t => {
  const f = fixture(t);
  f.write('apps/windows/runtime/entries/aggregate.mjs', "import '@meowfilm/script-host';");
  assert.throws(() => verifyAppStructure(f.source), /Windows runtime bypasses shared module/);
});
