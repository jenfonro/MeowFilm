import {
  closeSync, constants, fstatSync, fsyncSync, ftruncateSync, lstatSync,
  mkdirSync, openSync, readFileSync, readdirSync, realpathSync, writeSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { join, posix, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { check, compare, packageRuntime, safeRelative } from './package-runtime.mjs';
import { discoverRuntimeSource, relocateRuntimeSource } from './runtime-source.mjs';

const MANIFEST = 'runtime-manifest.json';
const TRANSPORT = 'runtime.http.v1';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);
const key = path => path.normalize('NFC').toLowerCase();

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
    check(before.size === after.size && before.mtimeMs === after.mtimeMs
      && before.ctimeMs === after.ctimeMs && bytes.length === before.size,
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

export function parsePlatformArgs(args, allowedFlags, usage) {
  const options = {};
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index], value = args[index + 1];
    check(allowedFlags.includes(flag) && typeof value === 'string' && value.length > 0
      && !Object.hasOwn(options, flag.slice(2)), 'CLI_USAGE', usage);
    options[flag.slice(2)] = value;
  }
  check(options.product && options.output, 'CLI_USAGE', 'Both --product and --output are required');
  return options;
}

/**
 * Shared offline platform runtime packager used by both iOS and Windows wrappers.
 * The common packager owns dependency resolution, lock validation, licenses and the new output directory;
 * this wrapper appends platform bootstrap/entry files, relocates their imports, and re-audits inventory/hashes.
 */
export async function packagePlatformRuntime({
  root = fileURLToPath(new URL('../', import.meta.url)),
  product,
  output,
  platform,
  manifestPlatform,
  label,
  errorPrefix,
  target,
  extraLicenses = [],
  validateTrustedNative,
} = {}) {
  const sourceDir = `apps/${platform}/runtime`;
  const outputDir = `app/${platform}`;
  const manifest = await packageRuntime({
    root, product, output, profile: 'common-native', platform,
    ...(target ? { target } : {}),
  });
  root = realpathSync(root);
  output = resolve(output);
  const identity = lstatSync(output);
  const originalManifest = jsonBytes(manifest);
  let fd;
  const assertOutput = () => {
    const current = lstatSync(output);
    check(current.isDirectory() && !current.isSymbolicLink()
      && current.dev === identity.dev && current.ino === identity.ino,
    'UNSAFE_OUTPUT', 'Runtime output identity changed');
  };
  const save = () => {
    assertOutput();
    const current = lstatSync(join(output, MANIFEST)), opened = fstatSync(fd);
    check(current.isFile() && !current.isSymbolicLink()
      && current.dev === opened.dev && current.ino === opened.ino,
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
    check(expected.size === manifest.files.length
      && actual.paths.length === expected.size + 1 && actual.paths.includes(MANIFEST)
      && actual.paths.every(path => path === MANIFEST || expected.has(path)),
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
    check(stat.isFile() && opened.dev === stat.dev && opened.ino === stat.ino
      && readFileSync(fd).equals(originalManifest),
    'MANIFEST_CHANGED', `Shared runtime manifest changed before ${label} packaging`);
    validateTrustedNative?.(manifest, check);
    manifest.entry = `${outputDir}/entries/${product}.mjs`;
    manifest.platform = manifestPlatform;
    manifest.transport = TRANSPORT;
    if (target) manifest.target = target;
    manifest.status = 'building';
    // Self-hashing a manifest is undefined; every OTHER emitted file is hashed.
    manifest.fileHashExcludes = [MANIFEST];
    manifest.relocations = [];
    save();
    const occupied = audit();
    const sourceTree = discoverRuntimeSource(root, platform).platform;
    function appendFile(filePath, bytes, documentation = false) {
      safeRelative(filePath);
      assertOutput();
      let parent = '';
      for (const part of filePath.split('/').slice(0, -1)) {
        parent = parent ? `${parent}/${part}` : part;
        const prior = occupied.get(key(parent));
        check(!prior || (prior.directory && prior.path === parent), 'PATH_COLLISION',
          `${label} runtime directory collides with an existing file`);
        if (!prior) {
          mkdirSync(join(output, parent));
          occupied.set(key(parent), { path: parent, directory: true });
        }
        check(lstatSync(checkedPath(output, parent)).isDirectory(),
          'UNSAFE_OUTPUT', `${label} runtime parent is not a directory`);
      }
      check(!occupied.has(key(filePath)), 'PATH_COLLISION', `${label} runtime file already exists`);
      manifest.incompleteFile = filePath;
      const targetFD = openSync(join(output, filePath), 'wx', 0o644);
      try {
        for (let offset = 0; offset < bytes.length;) {
          offset += writeSync(targetFD, bytes, offset, bytes.length - offset);
        }
        fsyncSync(targetFD);
      } finally { closeSync(targetFD); }
      occupied.set(key(filePath), { path: filePath, directory: false });
      manifest.files.push({
        path: filePath, bytes: bytes.length, sha256: digest(bytes),
        ...(documentation ? { role: 'documentation' } : {}),
      });
      delete manifest.incompleteFile;
    }
    for (const relative of sourceTree.files) {
      const source = `${sourceDir}/${relative}`;
      const input = readStable(root, source);
      check(manifest.inputs.some(record => record.path === source && record.sha256 === digest(input)),
        'SOURCE_CHANGED', 'Platform input differs from the resolved dependency graph');
      const text = new TextDecoder('utf-8', { fatal: true }).decode(input);
      const { path: relocatedPath, bytes, imports } = relocateRuntimeSource(text, source, platform);
      appendFile(relocatedPath, bytes);
      manifest.relocations.push({ source, path: relocatedPath, imports });
    }
    for (const name of extraLicenses) {
      const source = `LICENSES/${name}`, bytes = readStable(root, source);
      manifest.inputs.push({ path: source, sha256: digest(bytes) });
      appendFile(`licenses/${name}`, bytes, true);
    }
    for (const input of manifest.inputs) {
      check(digest(readStable(root, input.path)) === input.sha256,
        'SOURCE_CHANGED', 'Runtime build input changed during packaging');
    }
    // Every relative static import must target a file actually in this payload.
    const paths = new Set(manifest.files.map(file => file.path));
    for (const relocation of manifest.relocations) {
      for (const { to } of relocation.imports) {
        if (to.startsWith('.')) {
          check(paths.has(posix.normalize(posix.join(posix.dirname(relocation.path), to))),
            `${errorPrefix}_CLOSURE`, `${label} runtime import is absent from the payload`);
        }
      }
    }
    manifest.files.sort((a, b) => compare(a.path, b.path));
    audit();
    manifest.status = 'complete';
    save();
    return manifest;
  } catch (error) {
    manifest.status = 'failed';
    manifest.error = { code: error.code ?? `${errorPrefix}_PACKAGING_FAILED`, message: error.message };
    manifest.files.sort((a, b) => compare(a.path, b.path));
    if (fd !== undefined) {
      try { save(); }
      catch { error.message += ' (failed status could not be saved; output is unusable)'; }
    }
    throw error;
  } finally { if (fd !== undefined) closeSync(fd); }
}
