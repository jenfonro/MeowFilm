import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const digest = value => createHash('sha256').update(value).digest('hex');

/** The native client engine is separate from the Node-hosted WASM allowlist. */
export function verifyNativeJavaScript(directory = root) {
  const manifest = JSON.parse(readFileSync(resolve(directory, 'scripts/quickjs-native.json'), 'utf8'));
  if (manifest.schemaVersion !== 1 || manifest.project !== 'quickjs-ng/quickjs' ||
      !/^[a-f0-9]{40}$/.test(manifest.commit) || !/^\d+\.\d+\.\d+$/.test(manifest.version))
    throw new Error('Invalid native JavaScript source manifest');
  const base = resolve(directory, 'native/JavaScript/quickjs');
  const names = Object.keys(manifest.files).sort();
  if (names.length === 0 || names.join() !== readdirSync(base).sort().join())
    throw new Error('Native JavaScript source inventory differs');
  for (const name of names) {
    if (!/^[a-z0-9-]+\.[ch]$/.test(name)) throw new Error('Invalid native JavaScript source path');
    const path = resolve(base, name);
    const stat = lstatSync(path);
    if (!stat.isFile() || stat.isSymbolicLink() || digest(readFileSync(path)) !== manifest.files[name])
      throw new Error('Native JavaScript source hash differs: ' + name);
  }
  const license = resolve(directory, 'LICENSES/quickjs-native-MIT.txt');
  if (lstatSync(license).isSymbolicLink() || digest(readFileSync(license)) !== manifest.licenseSha256)
    throw new Error('Native JavaScript license hash differs');
  return { project: manifest.project, version: manifest.version, commit: manifest.commit, files: names.length };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  console.log(JSON.stringify(verifyNativeJavaScript()));
