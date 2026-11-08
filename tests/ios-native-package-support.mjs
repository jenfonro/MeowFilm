// Offline fixtures seeded from prepare-ios-runtime's real unsigned target build.
// Native loading is verified separately in the signed iOS host, never on desktop.
import {
  cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, rmdirSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { iosArtifacts, iosTarget } from '../scripts/quickjs/ios-target.mjs';
import { packageIOSRuntime } from '../scripts/package-ios-runtime.mjs';

export const repository = fileURLToPath(new URL('../', import.meta.url));
export const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
export const target = iosTarget('iphonesimulator', process.arch);
export const paths = iosArtifacts(target);
const policyPath = 'scripts/script-host-native.json';
let workspace;

export function fixture(t) {
  if (!workspace) {
    const temporary = join(repository, 'tmp/test-results/ios-native-package');
    mkdirSync(temporary, { recursive: true });
    const area = mkdtempSync(join(temporary, 'case-')), root = join(area, 'source');
    mkdirSync(root);
    const receipt = readJSON(join(repository, paths.receipt));
    const copy = path => {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      cpSync(join(repository, path), join(root, path), { recursive: true });
    };
    for (const path of new Set(['package.json', 'package-lock.json', policyPath, paths.receipt,
      receipt.artifact.path, receipt.sdk.path, receipt.catWebValues.path, receipt.catWebValues.license.path,
      receipt.frameworkInfo.path, receipt.exports.path,
      ...receipt.inputs.map(input => input.path)])) copy(path);
    for (const name of ['runtime-bridge', 'source-runtime', 'script-host']) {
      copy(`packages/${name}/package.json`);
      copy(`packages/${name}/src`);
      const installed = join(root, 'node_modules/@meowfilm', name);
      mkdirSync(dirname(installed), { recursive: true });
      symlinkSync(join(root, 'packages', name), installed, process.platform === 'win32' ? 'junction' : 'dir');
    }
    for (const [path, entry] of Object.entries(readJSON(join(repository, 'package-lock.json')).packages)) {
      if (path.includes('node_modules/') && !entry.link && !entry.dev && existsSync(join(repository, path))) copy(path);
    }
    copy('apps/runtime'); copy('apps/ios/runtime');
    copy('LICENSES/NOTICE.md');
    workspace = { root, output: join(area, 'payload'), area };
    // This one fixture is shared serially; each case restores all mutations.
    process.once('exit', () => rmSync(area, { recursive: true, force: true }));
  }
  const { root, output, area } = workspace;
  const originals = new Map(), directories = [];
  const remember = path => {
    if (!originals.has(path)) originals.set(path, existsSync(join(root, path)) ? readFileSync(join(root, path)) : undefined);
  };
  const write = (path, value) => {
    remember(path);
    const missing = [];
    for (let dir = dirname(join(root, path)); !existsSync(dir); dir = dirname(dir)) missing.push(dir);
    for (const dir of missing.reverse()) { mkdirSync(dir); directories.push(dir); }
    writeFileSync(join(root, path), typeof value === 'string' || Buffer.isBuffer(value)
      ? value : `${JSON.stringify(value, null, 2)}\n`);
  };
  const remove = path => { remember(path); rmSync(join(root, path)); };
  const symlink = (path, target) => { remove(path); symlinkSync(target, join(root, path)); };
  t.after(() => {
    for (const [path, bytes] of [...originals].reverse()) {
      rmSync(join(root, path), { force: true });
      if (bytes !== undefined) writeFileSync(join(root, path), bytes);
    }
    for (const dir of directories.reverse()) rmdirSync(dir);
    rmSync(output, { recursive: true, force: true });
  });
  return { root, output, area, write, remove, symlink,
    policy: readJSON(join(root, policyPath)), receipt: readJSON(join(root, paths.receipt)),
    pack: () => packageIOSRuntime({ root, product: 'aggregate', output, sdk: target.sdk, arch: target.arch }) };
}
