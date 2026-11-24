import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { NATIVE_POLICY_PATH, validateNativePolicy } from './quickjs/native-policy.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const digest = value => createHash('sha256').update(value).digest('hex');

/** Verify the one shared QuickJS source tree before native compilation. */
export function verifyNativeJavaScript(directory = root) {
  const policy = JSON.parse(readFileSync(resolve(directory, NATIVE_POLICY_PATH), 'utf8'));
  validateNativePolicy(policy);
  const manifest = policy.sources.quickjs;
  const base = resolve(directory, manifest.sourcePath);
  const names = Object.keys(manifest.files).sort();
  if (names.join() !== readdirSync(base).sort().join()) {
    throw new Error('Native JavaScript source inventory differs');
  }
  for (const name of names) {
    const path = resolve(base, name);
    const stat = lstatSync(path);
    if (!stat.isFile() || stat.isSymbolicLink() || digest(readFileSync(path)) !== manifest.files[name]) {
      throw new Error('Native JavaScript source hash differs: ' + name);
    }
  }
  const quickjsPatches = policy.patches.filter(patch => patch.source === 'quickjs');
  if (!quickjsPatches.length) throw new Error('Invalid native JavaScript patch manifest');
  for (const patch of quickjsPatches) {
    const patchPath = resolve(directory, patch.path);
    const stat = lstatSync(patchPath);
    if (!stat.isFile() || stat.isSymbolicLink() || digest(readFileSync(patchPath)) !== patch.sha256) {
      throw new Error('Native JavaScript patch hash differs: ' + patch.path);
    }
  }
  const license = resolve(directory, manifest.license.path);
  if (lstatSync(license).isSymbolicLink() || digest(readFileSync(license)) !== manifest.license.sha256) {
    throw new Error('Native JavaScript license hash differs');
  }
  const unicode = manifest.unicodeLicense;
  const unicodePath = resolve(directory, unicode.path);
  const unicodeStat = lstatSync(unicodePath);
  if (!unicodeStat.isFile() || unicodeStat.isSymbolicLink()) {
    throw new Error('Native JavaScript Unicode license hash differs');
  }
  const unicodeBytes = readFileSync(unicodePath);
  if (digest(unicodeBytes) !== unicode.sha256) {
    throw new Error('Native JavaScript Unicode license hash differs');
  }
  const notice = readFileSync(resolve(base, unicode.source), 'utf8')
    .match(/\/\*\n(UNICODE LICENSE V3\n[\s\S]*?)\*\//)?.[1];
  if (!notice || !unicodeBytes.equals(Buffer.from(notice, 'utf8'))) {
    throw new Error('Native JavaScript Unicode license differs from the source notice');
  }
  return { project: 'quickjs-ng/quickjs', version: manifest.version, commit: manifest.commit, files: names.length };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(JSON.stringify(verifyNativeJavaScript()));
}
