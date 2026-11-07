import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { packageWindowsRuntime } from '../scripts/windows/package-runtime.mjs';
import { prepareWindowsTestBundles } from '../scripts/windows/prepare-test-bundles.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';
import {
  engine, fixture as sharedFixture, hash, localLoader, localLoaderPath, localWasm, localWasmPath,
  repository, wasmPath, wasmPaths,
} from './runtime-engine-package-support.mjs';

function fixture(t) {
  const f = sharedFixture(t);
  for (const file of ['bootstrap.mjs', 'entries/aggregate.mjs']) {
    f.write(`apps/windows/runtime/${file}`, readFileSync(join(repository, 'apps/windows/runtime', file)));
  }
  return f;
}

test('Windows wrapper relocates its entry onto the same common module/engine closure', async t => {
  const f = fixture(t);
  const manifest = await packageWindowsRuntime({ ...f, product: 'aggregate' });
  assert.equal(manifest.status, 'complete');
  assert.deepEqual(manifest.files.filter(file => file.path.startsWith('app/')).map(file => file.path), [
    'app/bootstrap.mjs', 'app/modules/cat-script.mjs', 'app/modules/music-script.mjs',
    'app/modules/source-runtime.mjs',
    'app/windows/bootstrap.mjs', 'app/windows/entries/aggregate.mjs',
  ]);
  assert.deepEqual(readFileSync(join(f.output, 'app/modules/music-script.mjs')),
    readFileSync(join(f.root, 'apps/runtime/modules/music-script.mjs')));
  assert.deepEqual(readFileSync(join(f.output, 'app/modules/cat-script.mjs')),
    readFileSync(join(f.root, 'apps/runtime/modules/cat-script.mjs')));
  assert.deepEqual(readFileSync(join(f.output, 'app/modules/source-runtime.mjs')),
    readFileSync(join(f.root, 'apps/runtime/modules/source-runtime.mjs')));
  assert.ok(readFileSync(join(f.output, manifest.entry), 'utf8').includes("from '../../modules/music-script.mjs'"));
  assert.ok(readFileSync(join(f.output, manifest.entry), 'utf8').includes("from '../../modules/cat-script.mjs'"));
  assert.ok(readFileSync(join(f.output, manifest.entry), 'utf8').includes("from '../../modules/source-runtime.mjs'"));
  assert.deepEqual(manifest.files.filter(file => /\.wasm$/i.test(file.path)).map(file => file.path), wasmPaths);
});

test('native test bundles use the verified closure and never overwrite an existing bundle', async t => {
  const f = fixture(t), output = join(f.area, 'bundles');
  const manifest = await prepareWindowsTestBundles({ root: f.root, output });
  assert.equal(manifest.status, 'complete');
  assert.equal(hash(readFileSync(join(output, 'aggregate', wasmPath))), engine.wasm.sha256);
  assert.equal(hash(readFileSync(join(output, 'aggregate', localWasmPath))), localWasm.sha256);
  assert.equal(hash(readFileSync(join(output, 'aggregate', localLoaderPath))), localLoader.sha256);
  const before = readFileSync(join(output, 'aggregate/runtime-manifest.json'));
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output }), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(readFileSync(join(output, 'aggregate/runtime-manifest.json')), before);
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: 'relative-bundles' }),
    { code: 'INVALID_TEST_OUTPUT' });
  await assert.rejects(prepareWindowsTestBundles({ root: f.root, output: repository }), /leaves its Windows owner/);
});

test('test bundle CLI rejects extra or missing arguments before packaging', () => {
  const script = join(repository, 'scripts/windows/prepare-test-bundles.mjs');
  for (const args of [[], ['--help'], [temporaryRoot, 'extra']]) {
    const result = spawnSync(process.execPath, [script, ...args], {
      cwd: repository, encoding: 'utf8', windowsHide: true, timeout: 10000,
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /^CLI_USAGE:/);
    assert.equal(result.stdout, '');
  }
  assert.equal(existsSync(join(repository, 'aggregate')), false);
});
