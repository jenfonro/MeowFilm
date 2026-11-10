import {
  closeSync, constants, fstatSync, fsyncSync, ftruncateSync, lstatSync,
  mkdirSync, openSync, readFileSync, readdirSync, realpathSync, writeSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { join, posix, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { packageRuntime } from './package-runtime.mjs';
import { iosTarget } from './quickjs/ios-target.mjs';

const MANIFEST = 'runtime-manifest.json';
const TRANSPORT = 'runtime.http.v1';
const IOS_SOURCE = 'apps/ios/runtime';
const IOS_OUTPUT = 'app/ios';
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);
const key = path => path.normalize('NFC').toLowerCase();
function check(condition, code, message) {
  if (!condition) throw Object.assign(new Error(message), { code });
}

function safeRelative(path) {
  check(typeof path === 'string' && path.split('/').every(part =>
    part && part !== '.' && part !== '..' && !/[\\:\x00-\x1f\x7f]/.test(part)),
  'UNSAFE_PATH', 'Unsafe runtime file path');
  return path;
}

function checkedPath(root, relative) {
  safeRelative(relative);
  let path = root;
  for (const part of relative.split('/')) {
    path = join(path, part);
    check(!lstatSync(path).isSymbolicLink(), 'UNSAFE_SYMLINK', 'Runtime symlinks are not allowed');
  }
  return path;
}

function readStable(root, relative) {
  const path = checkedPath(root, relative);
  const stat = lstatSync(path);
  check(stat.isFile(), 'INVALID_SOURCE', 'Runtime input is not a regular file');
  const fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const before = fstatSync(fd);
    check(before.isFile() && before.dev === stat.dev && before.ino === stat.ino,
      'SOURCE_CHANGED', 'Runtime input identity changed');
    const bytes = readFileSync(fd), after = fstatSync(fd);
    check(before.size === after.size && before.mtimeMs === after.mtimeMs &&
      before.ctimeMs === after.ctimeMs && bytes.length === before.size,
    'SOURCE_CHANGED', 'Runtime input changed while reading');
    return bytes;
  } finally { closeSync(fd); }
}

function inventory(root, relative = '', occupied = new Map()) {
  const paths = [];
  const directory = relative ? checkedPath(root, relative) : root;
  for (const item of readdirSync(directory, { withFileTypes: true }).sort((a, b) => compare(a.name, b.name))) {
    const path = relative ? `${relative}/${item.name}` : item.name;
    safeRelative(path);
    check(!occupied.has(key(path)), 'PATH_COLLISION', 'Case/Unicode-colliding runtime path');
    occupied.set(key(path), { path, directory: item.isDirectory() });
    check(!item.isSymbolicLink(), 'UNSAFE_SYMLINK', 'Runtime symlinks are not allowed');
    check(item.isDirectory() || item.isFile(), 'INVALID_SOURCE', 'Runtime contains a non-regular file');
    if (item.isDirectory()) paths.push(...inventory(root, path, occupied).paths);
    else paths.push(path);
  }
  return { paths, occupied };
}

// Only the explicit platform imports are relocated. Shared modules and
// dependencies stay byte-for-byte from packageRuntime.
function relocate(source, imports) {
  const seen = new Set();
  const pattern = /^import\s+\{[^{}]+\}\s+from\s+(['"])([^'"\r\n]+)\1;[ \t]*\r?$/gm;
  const output = source.replace(pattern, (statement, quote, specifier) => {
    check(imports.has(specifier) && !seen.has(specifier), 'IOS_IMPORT', 'Unexpected or duplicate iOS runtime import');
    seen.add(specifier);
    return statement.replace(`${quote}${specifier}${quote}`, `${quote}${imports.get(specifier)}${quote}`);
  });
  const rest = source.replace(pattern, '').replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, '');
  check(seen.size === imports.size && !/\bimport\b|\bexport\s+[^;]*\bfrom\b/.test(rest),
    'IOS_IMPORT', 'iOS runtime imports must match the explicit static closure');
  return Buffer.from(output);
}

/**
 * Offline iOS wrapper. The shared packager alone resolves dependencies,
 * retains licenses, validates the lock and creates a NEW output directory.
 * This wrapper adds only the platform bootstrap and thin entry. The App embeds
 * the result as a code-signed resource; Node Mobile is not part of this payload.
 */
export async function packageIOSRuntime({
  root = fileURLToPath(new URL('../', import.meta.url)), product, output, sdk, arch,
} = {}) {
  const target = iosTarget(sdk, arch);
  const manifest = await packageRuntime({ root, product, output, profile: 'common-native', target });
  root = realpathSync(root);
  output = resolve(output);
  const identity = lstatSync(output);
  const originalManifest = jsonBytes(manifest);
  let fd;
  const sourceInputs = [];
  const assertOutput = () => {
    const current = lstatSync(output);
    check(current.isDirectory() && !current.isSymbolicLink() &&
      current.dev === identity.dev && current.ino === identity.ino,
    'UNSAFE_OUTPUT', 'Runtime output identity changed');
  };
  const save = () => {
    assertOutput();
    const current = lstatSync(join(output, MANIFEST)), opened = fstatSync(fd);
    check(current.isFile() && !current.isSymbolicLink() &&
      current.dev === opened.dev && current.ino === opened.ino,
    'UNSAFE_OUTPUT', 'Runtime manifest identity changed');
    const bytes = jsonBytes(manifest);
    ftruncateSync(fd, 0);
    for (let offset = 0; offset < bytes.length;) {
      offset += writeSync(fd, bytes, offset, bytes.length - offset, offset);
    }
    fsyncSync(fd);
  };
  const audit = () => {
    assertOutput();
    const actual = inventory(output);
    const expected = new Map(manifest.files.map(file => [file.path, file]));
    check(expected.size === manifest.files.length &&
      actual.paths.length === expected.size + 1 && actual.paths.includes(MANIFEST) &&
      actual.paths.every(path => path === MANIFEST || expected.has(path)),
    'INVENTORY_MISMATCH', 'Runtime file inventory does not match the manifest');
    for (const [path, record] of expected) {
      const bytes = readStable(output, path);
      check(bytes.length === record.bytes && digest(bytes) === record.sha256,
        'HASH_MISMATCH', 'Runtime file hash does not match the manifest');
    }
    return actual.occupied;
  };
  try {
    assertOutput();
    const path = checkedPath(output, MANIFEST), stat = lstatSync(path);
    fd = openSync(path, constants.O_RDWR | (constants.O_NOFOLLOW ?? 0));
    const opened = fstatSync(fd);
    check(stat.isFile() && opened.dev === stat.dev && opened.ino === stat.ino &&
      readFileSync(fd).equals(originalManifest),
    'MANIFEST_CHANGED', 'Shared runtime manifest changed before iOS packaging');
    check(manifest.trustedNative.length === 1 && manifest.trustedNative[0].platform === 'ios'
      && manifest.trustedNative[0].package === '@meowfilm/script-host',
    'INVALID_NATIVE_RECEIPT', 'The iOS runtime requires the reviewed embedded framework');
    manifest.entry = `${IOS_OUTPUT}/entries/${product}.mjs`;
    manifest.modules = ['music', 'cat', 'source', 'tmdb', 'netease'];
    manifest.platform = 'ios';
    manifest.transport = TRANSPORT;
    manifest.target = target;
    manifest.status = 'building';
    // Self-hashing a manifest is undefined; every OTHER emitted file is hashed.
    manifest.fileHashExcludes = [MANIFEST];
    manifest.relocations = [];
    save();
    const occupied = audit();
    const allowedSources = new Set(['bootstrap.mjs', 'entries/aggregate.mjs']);
    const sourceTree = inventory(checkedPath(root, IOS_SOURCE));
    check(sourceTree.paths.length === allowedSources.size &&
      sourceTree.paths.every(path => allowedSources.has(path)) &&
      [...sourceTree.occupied.values()].every(item => !item.directory || item.path === 'entries'),
    'IOS_CLOSURE', 'Unexpected or missing iOS runtime source; refusing to silently omit it');
    function appendFile(path, bytes) {
      safeRelative(path);
      assertOutput();
      let parent = '';
      for (const part of path.split('/').slice(0, -1)) {
        parent = parent ? `${parent}/${part}` : part;
        const prior = occupied.get(key(parent));
        check(!prior || (prior.directory && prior.path === parent), 'PATH_COLLISION',
          'iOS runtime directory collides with an existing file');
        if (!prior) {
          mkdirSync(join(output, parent)); // Never adopt a racing directory.
          occupied.set(key(parent), { path: parent, directory: true });
        }
        check(lstatSync(checkedPath(output, parent)).isDirectory(),
          'UNSAFE_OUTPUT', 'iOS runtime parent is not a directory');
      }
      check(!occupied.has(key(path)), 'PATH_COLLISION', 'iOS runtime file already exists');
      manifest.incompleteFile = path;
      const targetFD = openSync(join(output, path), 'wx', 0o644);
      try {
        for (let offset = 0; offset < bytes.length;) {
          offset += writeSync(targetFD, bytes, offset, bytes.length - offset);
        }
        fsyncSync(targetFD);
      } finally { closeSync(targetFD); }
      occupied.set(key(path), { path, directory: false });
      manifest.files.push({ path, bytes: bytes.length, sha256: digest(bytes) });
      delete manifest.incompleteFile;
    }
    const platformImports = new Set([
      'node:path', 'node:net', 'node:fs', 'node:crypto', 'node:buffer', 'node:util',
      '@meowfilm/runtime-bridge',
    ]);
    const sharedImports = new Map([
      ['../../../packages/script-host/src/native-engine.js', '../../node_modules/@meowfilm/script-host/src/native-engine.js'],
      ['../../runtime/bootstrap.mjs', '../bootstrap.mjs'],
      ['../../../runtime/modules/music-script.mjs', '../../modules/music-script.mjs'],
      ['../../../runtime/modules/cat-script.mjs', '../../modules/cat-script.mjs'],
      ['../../../runtime/modules/source-runtime.mjs', '../../modules/source-runtime.mjs'],
      ['../../../runtime/modules/metadata-data.mjs', '../../modules/metadata-data.mjs'],
      ['../../../runtime/modules/netease-token.mjs', '../../modules/netease-token.mjs'],
      ['../bootstrap.mjs', '../bootstrap.mjs'],
    ]);
    for (const relative of ['bootstrap.mjs', 'entries/aggregate.mjs']) {
      const source = `${IOS_SOURCE}/${relative}`, path = `${IOS_OUTPUT}/${relative}`;
      const input = readStable(root, source);
      const text = new TextDecoder('utf-8', { fatal: true }).decode(input);
      const imports = new Map();
      for (const match of text.matchAll(/^import\s+\{[^{}]+\}\s+from\s+(['"])([^'"\r\n]+)\1;[ \t]*\r?$/gm)) {
        const specifier = match[2];
        check(!imports.has(specifier) && (sharedImports.has(specifier) || platformImports.has(specifier)),
          'IOS_IMPORT', 'Unexpected or duplicate iOS runtime import');
        imports.set(specifier, sharedImports.get(specifier) ?? specifier);
      }
      const bytes = relocate(text, imports);
      sourceInputs.push({ path: source, sha256: digest(input) });
      appendFile(path, bytes);
      manifest.relocations.push({ source, path, imports: [...imports].map(([from, to]) => ({ from, to })) });
    }
    for (const name of ['NOTICE.md', 'api-enhanced-MIT.txt', 'npm-node-compat-ISC.txt']) {
      const source = `LICENSES/${name}`, bytes = readStable(root, source);
      sourceInputs.push({ path: source, sha256: digest(bytes) });
      appendFile(`licenses/${name}`, bytes);
    }
    manifest.inputs.push(...sourceInputs);
    for (const input of manifest.inputs) {
      check(digest(readStable(root, input.path)) === input.sha256,
        'SOURCE_CHANGED', 'Runtime build input changed during packaging');
    }
    // Every relative static import must target a file actually in this payload.
    const paths = new Set(manifest.files.map(file => file.path));
    for (const relocation of manifest.relocations) {
      for (const { to } of relocation.imports) {
        if (to.startsWith('.')) check(paths.has(posix.normalize(posix.join(posix.dirname(relocation.path), to))),
          'IOS_CLOSURE', 'iOS runtime import is absent from the payload');
      }
    }
    manifest.files.sort((a, b) => compare(a.path, b.path));
    audit();
    manifest.status = 'complete';
    save();
    return manifest;
  } catch (error) {
    manifest.status = 'failed';
    manifest.error = { code: error.code ?? 'IOS_PACKAGING_FAILED', message: error.message };
    manifest.files.sort((a, b) => compare(a.path, b.path));
    if (fd !== undefined) {
      try { save(); }
      catch { error.message += ' (failed status could not be saved; output is unusable)'; }
    }
    throw error;
  } finally { if (fd !== undefined) closeSync(fd); }
}

async function main(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    check(['--product', '--output', '--sdk', '--arch'].includes(args[i]) && args[i + 1] && !Object.hasOwn(options, args[i].slice(2)),
      'CLI_USAGE', 'Usage: node scripts/package-ios-runtime.mjs --product aggregate --sdk <sdk> --arch <arch> --output <new absolute directory>');
    options[args[i].slice(2)] = args[i + 1];
  }
  check(options.product && options.output, 'CLI_USAGE', 'Both --product and --output are required');
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
