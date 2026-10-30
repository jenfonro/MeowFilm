import { isAbsolute, resolve } from 'node:path';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { packageWindowsRuntime } from './package-runtime.mjs';
import { assertOwnedPath, tmpRoot } from './working-paths.mjs';

export async function prepareWindowsTestBundles({
  root = fileURLToPath(new URL('../../', import.meta.url)), output,
} = {}) {
  if (typeof output !== 'string' || !isAbsolute(output)) {
    throw Object.assign(new Error('Explicit absolute Windows test bundle root required'), { code: 'INVALID_TEST_OUTPUT' });
  }
  output = assertOwnedPath(tmpRoot, output, { allowMissing: true });
  await mkdir(output, { recursive: true });
  return packageWindowsRuntime({ root, product: 'aggregate', output: resolve(output, 'aggregate') });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.length !== 1 || args[0].startsWith('--')) {
    console.error('CLI_USAGE: node scripts/windows/prepare-test-bundles.mjs <absolute test bundle root>');
    process.exitCode = 1;
  } else {
    await prepareWindowsTestBundles({ output: args[0] }).catch(error => {
      console.error(`Windows test bundle packaging failed: ${error.code ?? 'PACKAGING_FAILED'}`);
      process.exitCode = 1;
    });
  }
}
