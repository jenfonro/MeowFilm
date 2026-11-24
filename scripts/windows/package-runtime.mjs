import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { packagePlatformRuntime, parsePlatformArgs } from '../package-runtime-platform.mjs';

/**
 * Offline Windows wrapper over the shared platform packager.
 * Adds only the Windows bootstrap and thin entry; never installs Node or copies node.exe.
 */
export async function packageWindowsRuntime({
  root = fileURLToPath(new URL('../../', import.meta.url)), product, output,
} = {}) {
  return packagePlatformRuntime({
    root,
    product,
    output,
    platform: 'windows',
    manifestPlatform: 'win32',
    label: 'Windows',
    errorPrefix: 'WINDOWS',
    validateTrustedNative(manifest, check) {
      check((manifest.trustedNative ?? []).every(native => native.platform === 'win32' && native.arch === 'x64'),
        'NATIVE_TARGET_MISMATCH', 'Windows runtime requires the win-x64 native build');
    },
  });
}

async function main(args) {
  const options = parsePlatformArgs(
    args,
    ['--product', '--output'],
    'Usage: node scripts/windows/package-runtime.mjs --product aggregate --output <new absolute directory>',
  );
  const manifest = await packageWindowsRuntime(options);
  console.log(JSON.stringify({
    status: manifest.status, product: manifest.product, transport: manifest.transport,
    entry: manifest.entry, files: manifest.files.length,
  }));
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`${error.code ?? 'WINDOWS_PACKAGING_FAILED'}: ${error.message}`);
    process.exitCode = 1;
  });
}
