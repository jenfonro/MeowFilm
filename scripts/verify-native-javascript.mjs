import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const digest = value => createHash('sha256').update(value).digest('hex');

/** Verify the one shared QuickJS source tree before native compilation. */
export function verifyNativeJavaScript(directory = root) {
  const policy = JSON.parse(readFileSync(resolve(directory, 'scripts/script-host-native.json'), 'utf8'));
  const manifest = policy.sources?.quickjs;
  if (policy.schemaVersion !== 1 || manifest?.version !== '0.18.0' ||
      manifest.commit !== 'd7215b0ad3fc2c562e8317fd9bca058fd4863804' ||
      manifest.sourcePath !== 'native/JavaScript/quickjs')
    throw new Error('Invalid native JavaScript source manifest');
  const base = resolve(directory, manifest.sourcePath);
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
  const patches = ['native/JavaScript/patches/regexp-compile-interrupt.patch',
    'packages/script-host/native/task-context.patch',
    'packages/script-host/native/arraybuffer-ownership.patch'];
  if (!Array.isArray(policy.patches) || policy.patches.length !== patches.length ||
      policy.patches.some((patch, index) => patch.path !== patches[index] ||
        patch.source !== 'quickjs' || !/^[a-f0-9]{64}$/.test(patch.sha256)))
    throw new Error('Invalid native JavaScript patch manifest');
  for (const patch of policy.patches) {
    const patchPath = resolve(directory, patch.path);
    if (!lstatSync(patchPath).isFile() || lstatSync(patchPath).isSymbolicLink() ||
        digest(readFileSync(patchPath)) !== patch.sha256)
      throw new Error('Native JavaScript patch hash differs');
  }
  if (manifest.license?.path !== 'LICENSES/quickjs-ng-LICENSE.txt')
    throw new Error('Invalid native JavaScript license manifest');
  const license = resolve(directory, manifest.license.path);
  if (lstatSync(license).isSymbolicLink() || digest(readFileSync(license)) !== manifest.license.sha256)
    throw new Error('Native JavaScript license hash differs');
  const unicode = manifest.unicodeLicense;
  if (!unicode || unicode.path !== 'LICENSES/Unicode-3.0.txt' ||
      unicode.source !== 'libunicode-table.h' || !/^[a-f0-9]{64}$/.test(unicode.sha256))
    throw new Error('Invalid native JavaScript Unicode license manifest');
  const unicodePath = resolve(directory, unicode.path);
  const unicodeStat = lstatSync(unicodePath);
  if (!unicodeStat.isFile() || unicodeStat.isSymbolicLink())
    throw new Error('Native JavaScript Unicode license hash differs');
  const unicodeBytes = readFileSync(unicodePath);
  if (digest(unicodeBytes) !== unicode.sha256)
    throw new Error('Native JavaScript Unicode license hash differs');
  const notice = readFileSync(resolve(base, unicode.source), 'utf8')
    .match(/\/\*\n(UNICODE LICENSE V3\n[\s\S]*?)\*\//)?.[1];
  if (!notice || !unicodeBytes.equals(Buffer.from(notice, 'utf8')))
    throw new Error('Native JavaScript Unicode license differs from the source notice');
  return { project: 'quickjs-ng/quickjs', version: manifest.version, commit: manifest.commit, files: names.length };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  console.log(JSON.stringify(verifyNativeJavaScript()));
