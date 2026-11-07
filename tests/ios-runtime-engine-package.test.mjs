import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { packageIOSRuntime } from '../scripts/package-ios-runtime.mjs';
import { assertOwnedPath } from '../scripts/windows/working-paths.mjs';
import {
  fixture as sharedFixture, hash, inert, readJSON, repository,
} from './runtime-engine-package-support.mjs';

function fixture(t) {
  const f = sharedFixture(t);
  for (const file of ['bootstrap.mjs', 'entries/aggregate.mjs']) {
    f.write(`apps/ios/runtime/${file}`, readFileSync(join(repository, 'apps/ios/runtime', file)));
  }
  return f;
}

test('iOS wrapper relocates its entry onto the music/Cat asm.js closure and completes the payload', async t => {
  const f = fixture(t);
  const manifest = await packageIOSRuntime({ ...f, product: 'aggregate' });
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.entry, 'app/ios/entries/aggregate.mjs');
  assert.equal(manifest.platform, 'ios');
  assert.equal(manifest.transport, 'ios-socketpair-v1');
  assert.equal(manifest.assemblyProfile, 'ios-embedded');
  assert.equal(Object.hasOwn(manifest, 'sharedEntry'), false);
  assert.deepEqual(manifest.modules, ['music', 'cat']);
  assert.deepEqual(manifest.fileHashExcludes, ['runtime-manifest.json']);
  assert.deepEqual(manifest.files.filter(file => file.path.startsWith('app/')).map(file => file.path), [
    'app/bootstrap.mjs', 'app/ios/bootstrap.mjs', 'app/ios/entries/aggregate.mjs',
    'app/modules/cat-script.mjs', 'app/modules/music-script.mjs',
  ]);
  assert.deepEqual(manifest.files.filter(file => /\.(?:wasm|node|dll|dylib|so|exe)$/i.test(file.path)), []);
  assert.deepEqual(manifest.trustedWasm, []);
  assert.deepEqual(manifest.relocations.map(value => value.path), ['app/ios/bootstrap.mjs', 'app/ios/entries/aggregate.mjs']);
  const entry = readFileSync(join(f.output, manifest.entry), 'utf8');
  assert.ok(entry.includes("from '../../modules/music-script.mjs'"));
  assert.ok(entry.includes("from '../../modules/cat-script.mjs'"));
  assert.ok(!entry.includes('source-runtime'));
  assert.ok(readFileSync(join(f.output, 'app/ios/bootstrap.mjs'), 'utf8').includes("from '../bootstrap.mjs'"));
  for (const file of manifest.files) {
    const bytes = readFileSync(join(f.output, file.path));
    assert.equal(bytes.length, file.bytes);
    assert.equal(hash(bytes), file.sha256);
  }
  for (const path of ['apps/ios/runtime/bootstrap.mjs', 'apps/ios/runtime/entries/aggregate.mjs'])
    assert.deepEqual(manifest.inputs.find(input => input.path === path), { path, sha256: hash(readFileSync(join(f.root, path))) });
});

for (const [name, change, code] of [
  ['an extra platform source', f => f.write('apps/ios/runtime/entries/other.mjs', inert), 'IOS_CLOSURE'],
  ['a missing platform entry', f => rmSync(assertOwnedPath(f.area, join(f.root, 'apps/ios/runtime/entries/aggregate.mjs'))), 'IOS_CLOSURE'],
  ['an unexpected entry import', f => f.write('apps/ios/runtime/entries/aggregate.mjs',
    "import { createSourceRuntimeModule } from '../../../runtime/modules/source-runtime.mjs';\n" +
    readFileSync(join(repository, 'apps/ios/runtime/entries/aggregate.mjs'))), 'IOS_IMPORT'],
  ['a dynamic platform import', f => f.write('apps/ios/runtime/bootstrap.mjs',
    readFileSync(join(repository, 'apps/ios/runtime/bootstrap.mjs')) + "\nawait import('node:fs');\n"), 'IOS_IMPORT'],
]) {
  test(`iOS wrapper refuses ${name} instead of silently shipping or omitting it`, async t => {
    const f = fixture(t);
    change(f);
    await assert.rejects(packageIOSRuntime({ ...f, product: 'aggregate' }), { code });
    const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
    assert.equal(manifest.status, 'failed');
  });
}
