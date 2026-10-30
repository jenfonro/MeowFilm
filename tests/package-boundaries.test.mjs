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

test('shared runtime may use its own modules and data-only builtins', t => {
  const f = fixture(t);
  f.write('apps/runtime/bootstrap.mjs', "import './modules/music-script.mjs';");
  f.write('apps/runtime/modules/music-script.mjs', "import { createHash } from 'node:crypto';");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
});

test('shared runtime cannot depend on Windows source', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/music-script.mjs', "import '../../windows/runtime/bootstrap.mjs';");
  assert.throws(() => verifyBoundaries(f.root), /shared runtime depends on platform source/);
});

for (const specifier of ['node:net', 'node:child_process', '@meowfilm/script-host/src/host.js']) {
  test(`shared runtime rejects platform/private import: ${specifier}`, t => {
    const f = fixture(t);
    f.write('apps/runtime/modules/music-script.mjs', `import '${specifier}';`);
    assert.throws(() => verifyBoundaries(f.root), /shared runtime platform\/private import/);
  });
}
