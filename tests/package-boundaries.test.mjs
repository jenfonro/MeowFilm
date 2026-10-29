import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { verifyBoundaries } from '../scripts/verify-boundaries.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'package-boundaries-'));
  t.after(() => { assertOwnedPath(temporaryRoot, root); rmSync(root, { recursive: true }); });
  function write(path, content) {
    const file = assertOwnedPath(root, join(root, path), { allowMissing: true });
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, typeof content === 'string' ? content : JSON.stringify(content));
  }
  write('package.json', { workspaces: ['packages/*'] });
  write('packages/a/package.json', { name: '@meowfilm/a', dependencies: { '@meowfilm/b': '*' } });
  write('packages/b/package.json', { name: '@meowfilm/b' });
  write('packages/a/src/index.js', "import '@meowfilm/b';");
  write('packages/b/src/index.js', 'export {};');
  return { root, write };
}

test('workspace declarations permit explicit cross-package dependencies', t => {
  const f = fixture(t);
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
});

test('workspace imports require a dependency declaration', t => {
  const f = fixture(t);
  f.write('packages/a/package.json', { name: '@meowfilm/a' });
  assert.throws(() => verifyBoundaries(f.root), /undeclared workspace dependency/);
});

test('relative imports cannot bypass another package interface', t => {
  const f = fixture(t);
  f.write('packages/a/src/index.js', "import '../../b/src/index.js';");
  assert.throws(() => verifyBoundaries(f.root), /escapes package boundary/);
});

test('workspace dependency cycles are reported', t => {
  const f = fixture(t);
  f.write('packages/b/package.json', { name: '@meowfilm/b', dependencies: { '@meowfilm/a': '*' } });
  assert.throws(() => verifyBoundaries(f.root), /Dependency cycle/);
});
