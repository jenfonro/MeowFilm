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
  write('apps/runtime/bootstrap.mjs', "import './helper.mjs';");
  write('apps/runtime/helper.mjs', 'export {};');
  write('apps/windows/src/MeowFilm.Desktop/Page.xaml',
    '<UserControl x:Class="MeowFilm.Desktop.Page"><ResourceDictionary Source="Theme.xaml"/></UserControl>');
  write('apps/windows/src/MeowFilm.Desktop/Theme.xaml', '<ResourceDictionary/>');
  return { source, write };
}

test('checks relative imports and resources in the declared source tree', t => {
  const result = verifyAppStructure(fixture(t).source);
  assert.equal(result.relativeImports, 1);
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
