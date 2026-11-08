import { isAbsolute, resolve } from 'node:path';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { packageWindowsRuntime } from './package-runtime.mjs';
import { ensureRuntimeNative } from './ensure-runtime-native.mjs';
import { assertOwnedPath, tmpRoot } from './working-paths.mjs';

function testBundleRoot(output) {
  if (typeof output !== 'string' || !isAbsolute(output)) {
    throw Object.assign(new Error('Explicit absolute Windows test bundle root required'), { code: 'INVALID_TEST_OUTPUT' });
  }
  return assertOwnedPath(tmpRoot, output, { allowMissing: true });
}

export async function prepareWindowsTestBundles({
  root = fileURLToPath(new URL('../../', import.meta.url)), output,
} = {}) {
  output = testBundleRoot(output);
  await mkdir(output, { recursive: true });
  return packageWindowsRuntime({ root, product: 'aggregate', output: resolve(output, 'aggregate') });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.length !== 1 || args[0].startsWith('--')) {
    console.error('CLI_USAGE: node scripts/windows/prepare-test-bundles.mjs <absolute test bundle root>');
    process.exitCode = 1;
  } else {
    await (async () => {
      const output = testBundleRoot(args[0]);
      await ensureRuntimeNative();
      await prepareWindowsTestBundles({ output });
    })().catch(error => {
      console.error(`Windows test bundle preparation failed: ${error.code ?? error.message}`);
      process.exitCode = error.exitCode ?? 1;
    });
  }
}
