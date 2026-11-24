import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { packagePlatformRuntime, parsePlatformArgs } from '../package-runtime-platform.mjs';
import { iosTarget } from '../quickjs/ios-target.mjs';

/**
 * Offline iOS wrapper over the shared platform packager.
 * The App embeds the result as a code-signed resource; Node Mobile is not part of this payload.
 */
export async function packageIOSRuntime({
  root = fileURLToPath(new URL('../../', import.meta.url)), product, output, sdk, arch,
} = {}) {
  const target = iosTarget(sdk, arch);
  return packagePlatformRuntime({
    root,
    product,
    output,
    platform: 'ios',
    manifestPlatform: 'ios',
    label: 'iOS',
    errorPrefix: 'IOS',
    target,
    extraLicenses: ['npm-node-compat-ISC.txt'],
    validateTrustedNative(manifest, check) {
      check(manifest.trustedNative.length === 1 && manifest.trustedNative[0].platform === 'ios'
        && manifest.trustedNative[0].package === '@meowfilm/script-host',
      'INVALID_NATIVE_RECEIPT', 'The iOS runtime requires the reviewed embedded framework');
    },
  });
}

async function main(args) {
  const options = parsePlatformArgs(
    args,
    ['--product', '--output', '--sdk', '--arch'],
    'Usage: node scripts/ios/package-runtime.mjs --product aggregate --sdk <sdk> --arch <arch> --output <new absolute directory>',
  );
  const manifest = await packageIOSRuntime(options);
  console.log(JSON.stringify({
    status: manifest.status, product: manifest.product, transport: manifest.transport,
    entry: manifest.entry, files: manifest.files.length,
  }));
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`${error.code ?? 'IOS_PACKAGING_FAILED'}: ${error.message}`);
    process.exitCode = 1;
  });
}
