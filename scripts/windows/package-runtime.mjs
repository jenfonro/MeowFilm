import {
  closeSync, constants, fstatSync, fsyncSync, ftruncateSync, lstatSync,
  mkdirSync, openSync, readFileSync, readdirSync, realpathSync, writeSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { join, posix, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { packageRuntime } from '../package-runtime.mjs';

const MANIFEST = 'runtime-manifest.json';
const TRANSPORT = 'windows-named-pipe-v1';
const WINDOWS_SOURCE = 'apps/windows/runtime';
const WINDOWS_OUTPUT = 'app/windows';
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);
const key = path => path.normalize('NFC').toLowerCase();
function check(condition, code, message) {
  if (!condition) throw Object.assign(new Error(message), { code });
}

function safeRelative(path) {
  check(typeof path === 'string' && path.split('/').every(part =>
    part && part !== '.' && part !== '..' && !/[\\:\x00-\x1f\x7f<>"|?*[\]{}!]/.test(part) &&
    !/[. ]$/.test(part) && !/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part)),
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

// Only platform imports are relocated. Remaining shared dependencies and legal
// notices stay byte-for-byte from packageRuntime. Do not silently omit a new
// platform helper, dynamic import or asset: extend this explicit mapping first.
function relocate(source, imports, dynamicImports = new Map()) {
  const seen = new Set();
  const pattern = /^import\s+\{[^{}]+\}\s+from\s+(['"])([^'"\r\n]+)\1;[ \t]*\r?$/gm;
  let output = source.replace(pattern, (statement, quote, specifier) => {
    check(imports.has(specifier) && !seen.has(specifier), 'WINDOWS_IMPORT',
      'Unexpected or duplicate Windows runtime import');
    seen.add(specifier);
    return statement.replace(`${quote}${specifier}${quote}`, `${quote}${imports.get(specifier)}${quote}`);
  });
  const dynamicSeen = new Set();
  const dynamicPattern = /\bimport\(\s*(['"])([^'"\r\n]+)\1\s*\)/g;
  output = output.replace(dynamicPattern, (statement, quote, specifier) => {
    check(dynamicImports.has(specifier), 'WINDOWS_IMPORT', 'Unexpected dynamic Windows runtime import');
    dynamicSeen.add(specifier);
    return statement.replace(`${quote}${specifier}${quote}`, `${quote}${dynamicImports.get(specifier)}${quote}`);
  });
  const rest = source.replace(pattern, '').replace(dynamicPattern, '').replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, '');
  check(seen.size === imports.size && dynamicSeen.size === dynamicImports.size &&
    !/\bimport\b|\bexport\s+[^;]*\bfrom\b/.test(rest),
    'WINDOWS_IMPORT', 'Windows runtime imports must match the explicit static closure');
  return Buffer.from(output);
}

/**
 * Offline Windows wrapper. The shared packager alone resolves dependencies,
 * retains licenses, validates the lock and creates a NEW output directory.
 * This wrapper adds only the platform bootstrap and thin entry.
 * Native-owned domains never ship their old ordinary JS business closure.
 * It never installs/downloads Node, copies node.exe or imports app code.
 */
export async function packageWindowsRuntime({
  root = fileURLToPath(new URL('../../', import.meta.url)), product, output,
} = {}) {
  // Rejections here retain the shared packager's failed manifest, if it created
  // one. An existing output is never opened, adopted, altered or removed.
  const manifest = await packageRuntime({ root, product, output, profile: 'windows-native' });
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
    'MANIFEST_CHANGED', 'Shared runtime manifest changed before Windows packaging');
    manifest.entry = `${WINDOWS_OUTPUT}/entries/${product}.mjs`;
    manifest.platform = 'win32';
    manifest.transport = TRANSPORT;
    manifest.status = 'building';
    // Self-hashing a manifest is undefined; every OTHER emitted file is hashed.
    manifest.fileHashExcludes = [MANIFEST];
    manifest.relocations = [];
    save();
    const occupied = audit();
    const allowedSources = new Set([
      'bootstrap.mjs', 'entries/aggregate.mjs',
    ]);
    const sourceDirectory = checkedPath(root, WINDOWS_SOURCE);
    const sourceTree = inventory(sourceDirectory);
    check(sourceTree.paths.every(path => allowedSources.has(path)) &&
      [...sourceTree.occupied.values()].every(item => !item.directory || item.path === 'entries'),
    'WINDOWS_CLOSURE', 'Unexpected Windows runtime source; refusing to silently omit it');
    function appendFile(path, bytes) {
      safeRelative(path);
      assertOutput();
      let parent = '';
      for (const part of path.split('/').slice(0, -1)) {
        parent = parent ? `${parent}/${part}` : part;
        const prior = occupied.get(key(parent));
        check(!prior || (prior.directory && prior.path === parent), 'PATH_COLLISION',
          'Windows runtime directory collides with an existing file');
        if (!prior) {
          mkdirSync(join(output, parent)); // Never adopt a racing directory.
          occupied.set(key(parent), { path: parent, directory: true });
        }
        check(lstatSync(checkedPath(output, parent)).isDirectory(),
          'UNSAFE_OUTPUT', 'Windows runtime parent is not a directory');
      }
      check(!occupied.has(key(path)), 'PATH_COLLISION', 'Windows runtime file already exists');
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
    const bootstrapImports = new Map([
      ['node:net', 'node:net'],
      ['node:path', 'node:path'],
      ['@meowfilm/runtime-bridge', '@meowfilm/runtime-bridge'],
      ['@meowfilm/runtime-bridge/descriptor', '@meowfilm/runtime-bridge/descriptor'],
      ['../../runtime/bootstrap.mjs', '../bootstrap.mjs'],
    ]);
    const entryImports = new Map([
      ['../bootstrap.mjs', '../bootstrap.mjs'],
      ['../../../runtime/modules/music-script.mjs', '../../modules/music-script.mjs'],
      ['../../../runtime/modules/cat-script.mjs', '../../modules/cat-script.mjs'],
    ]);
    for (const [relative, imports, dynamicImports = new Map()] of [
      ['bootstrap.mjs', bootstrapImports], ['entries/aggregate.mjs', entryImports],
    ]) {
      const source = `${WINDOWS_SOURCE}/${relative}`, path = `${WINDOWS_OUTPUT}/${relative}`;
      const input = readStable(root, source);
      const text = new TextDecoder('utf-8', { fatal: true }).decode(input);
      const bytes = relocate(text, imports, dynamicImports);
      sourceInputs.push({ path: source, sha256: digest(input) });
      appendFile(path, bytes);
      manifest.relocations.push({
        source, path, imports: [...imports].map(([from, to]) => ({ from, to })),
        ...(dynamicImports.size ? { dynamicImports: [...dynamicImports].map(([from, to]) => ({ from, to })) } : {}),
      });
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
          'WINDOWS_CLOSURE', 'Windows runtime import is absent from the payload');
      }
    }
    manifest.files.sort((a, b) => compare(a.path, b.path));
    audit();
    manifest.status = 'complete';
    save();
    return manifest;
  } catch (error) {
    manifest.status = 'failed';
    manifest.error = { code: error.code ?? 'WINDOWS_PACKAGING_FAILED', message: error.message };
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
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index], value = args[index + 1];
    check(['--product', '--output'].includes(flag) && typeof value === 'string' && value.length > 0 &&
      !Object.hasOwn(options, flag.slice(2)), 'CLI_USAGE',
    'Usage: node scripts/windows/package-runtime.mjs --product aggregate --output <new absolute directory>');
    options[flag.slice(2)] = value;
  }
  check(options.product && options.output, 'CLI_USAGE', 'Both --product and --output are required');
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
