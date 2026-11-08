import {
  closeSync, constants, fstatSync, fsyncSync, lstatSync, mkdirSync, openSync,
  readSync, readdirSync, realpathSync, writeSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { dirname, isAbsolute, join, parse, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { tmpRoot } from './working-paths.mjs';

const MANIFEST = 'licenses-manifest.json';
const MAX_FILE = 1024 * 1024, MAX_TOTAL = 8 * 1024 * 1024;
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const key = path => process.platform === 'win32' ? path.toLowerCase() : path;

// Explicit list of the reviewed files in LICENSES. No directory
// glob copying, inferred licensing or execution of application code.
const SOURCE = 'LICENSES';
export const licenseFiles = [
  'NOTICE.md', 'Apache-2.0.txt', 'GPL-3.0-only.txt', 'api-enhanced-MIT.txt', 'KuGouMusicApi-MIT.txt',
  'iwebplus-NOTICE.txt', 'meowmusic-NOTICE.txt', 'node-LICENSE.txt', 'qr-code-generator-LICENSE.txt',
  'quickjs-ng-LICENSE.txt', 'openssl-LICENSE.txt', 'zlib-LICENSE.txt',
  'quickjs-native-MIT.txt', 'Unicode-3.0.txt', 'catpawrunner-MIT.txt',
  'npm-node-compat-BSD-3-Clause.txt', 'npm-node-compat-ISC.txt', 'npm-node-compat-MIT.txt',
  'SkiaSharp-MIT.txt', 'SkiaSharp-THIRD-PARTY-NOTICES.txt',
];
const INPUTS = licenseFiles.map(name => ({ sourcePath: `${SOURCE}/${name}`, path: name }));

function need(condition, code) {
  if (!condition) throw Object.assign(new Error(code), { code });
}
function segment(value) {
  return value && value !== '.' && value !== '..' &&
    !/[\\:\x00-\x1f\x7f<>"|?*]/u.test(value) && !/[. ]$/u.test(value) &&
    !/^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:[ .]|$)/iu.test(value);
}
function localAbsolute(value, code) {
  need(typeof value === 'string' && value.length > 0 && value.length <= 30000, code);
  need(!/^[\\/]{2}/u.test(value), code); // UNC and Win32 device namespaces are not build roots.
  if (process.platform === 'win32') need(/^[A-Za-z]:[\\/]/u.test(value), code);
  need(isAbsolute(value), code);
  const normalized = process.platform === 'win32' ? value.replaceAll('/', '\\') : value;
  const volume = parse(normalized).root;
  const tail = normalized.slice(volume.length).replace(/[\\/]$/u, '');
  need(tail.length > 0 && tail.split(sep).every(segment), code);
  return resolve(normalized);
}
function inside(root, path) {
  const rel = relative(root, path);
  return rel !== '' && rel !== '..' && !rel.startsWith(`..${sep}`) && !isAbsolute(rel);
}
function stat(path, missing = 'MISSING_SOURCE') {
  try { return lstatSync(path, { bigint: true }); }
  catch (error) { if (error.code === 'ENOENT') need(false, missing); throw error; }
}
function absent(path) {
  try { lstatSync(path); return false; }
  catch (error) { if (error.code === 'ENOENT') return true; throw error; }
}
function sameIdentity(a, b) {
  return a.dev === b.dev && a.ino === b.ino && a.mode === b.mode;
}
function unchanged(a, b) {
  return sameIdentity(a, b) && a.size === b.size && a.mtimeNs === b.mtimeNs &&
    a.ctimeNs === b.ctimeNs && a.nlink === b.nlink;
}
function plain(s, directory) {
  need(!s.isSymbolicLink(), 'REPARSE_DENIED');
  need(directory ? s.isDirectory() : s.isFile(), 'INVALID_SOURCE_TYPE');
  if (!directory) need(s.nlink === 1n, 'HARDLINK_DENIED');
}
function directories(path, identities, missing) {
  const volume = parse(path).root;
  let current = volume;
  for (const part of ['', ...relative(volume, path).split(sep).filter(Boolean)]) {
    if (part) current = join(current, part);
    const now = stat(current, missing);
    plain(now, true);
    const old = identities.get(current);
    if (old) need(sameIdentity(old, now), 'PATH_CHANGED');
    else identities.set(current, now);
  }
}
function checkIdentities(identities) {
  for (const [path, old] of identities) {
    const now = stat(path, 'PATH_CHANGED');
    plain(now, old.isDirectory());
    need(sameIdentity(old, now), 'PATH_CHANGED');
  }
}

// Node lstat detects symlinks/junctions; not every Windows reparse tag is a link.
// Query the native attribute as well. Fixed code, paths as JSON stdin, no shell
// interpolation/profile/reference scripts. Failure to audit is a hard failure.
function windowsReparseAudit(paths) {
  if (process.platform !== 'win32') return;
  const systemRoot = localAbsolute(process.env.SystemRoot, 'REPARSE_CHECK_UNAVAILABLE');
  const program = `
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
try {
  $paths = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd())
  foreach ($path in $paths) {
    $attributes = [IO.File]::GetAttributes([string]$path)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 31 }
  }
  [Console]::Out.Write('OK')
} catch { exit 32 }
`;
  const result = spawnSync(join(systemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe'),
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(program, 'utf16le').toString('base64')],
    { input: JSON.stringify([...new Set(paths)]), encoding: 'utf8', windowsHide: true, timeout: 15000, maxBuffer: 32768 });
  need(result.status !== 31, 'REPARSE_DENIED');
  need(!result.error && result.status === 0 && result.stdout === 'OK', 'REPARSE_CHECK_UNAVAILABLE');
}
function readStable(path, identities) {
  const beforePath = stat(path);
  plain(beforePath, false);
  need(beforePath.size > 0n && beforePath.size <= BigInt(MAX_FILE), 'SOURCE_SIZE');
  const fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const before = fstatSync(fd, { bigint: true });
    need(unchanged(beforePath, before), 'SOURCE_CHANGED');
    const bytes = Buffer.alloc(Number(before.size));
    let offset = 0;
    while (offset < bytes.length) {
      const count = readSync(fd, bytes, offset, bytes.length - offset, offset);
      need(count > 0, 'SOURCE_CHANGED'); offset += count;
    }
    need(readSync(fd, Buffer.alloc(1), 0, 1, offset) === 0, 'SOURCE_CHANGED');
    need(unchanged(before, fstatSync(fd, { bigint: true })) && unchanged(before, stat(path)), 'SOURCE_CHANGED');
    identities.set(path, before);
    return bytes;
  } finally { closeSync(fd); }
}
function writeNew(path, bytes, identities) {
  checkIdentities(identities);
  const fd = openSync(path, 'wx', 0o644);
  try {
    const opened = fstatSync(fd, { bigint: true });
    plain(opened, false);
    let offset = 0;
    while (offset < bytes.length) {
      const count = writeSync(fd, bytes, offset, bytes.length - offset, offset);
      need(count > 0, 'OUTPUT_WRITE'); offset += count;
    }
    fsyncSync(fd);
    need(sameIdentity(opened, stat(path, 'PATH_CHANGED')), 'PATH_CHANGED');
    identities.set(path, fstatSync(fd, { bigint: true }));
  } finally { closeSync(fd); }
}
function outputDirectory(path, output, identities) {
  if (path === output) return;
  outputDirectory(dirname(path), output, identities);
  if (!identities.has(path)) {
    checkIdentities(identities);
    // Never adopt a pre-existing directory inserted by another writer.
    mkdirSync(path);
    const created = stat(path); plain(created, true);
    identities.set(path, created);
  }
}
function inventory(output) {
  const paths = [];
  const walk = (base, prefix) => {
    for (const item of readdirSync(base, { withFileTypes: true })) {
      need(segment(item.name) && !item.isSymbolicLink(), 'REPARSE_DENIED');
      const path = prefix ? `${prefix}/${item.name}` : item.name;
      if (item.isDirectory()) walk(join(base, item.name), path);
      else { need(item.isFile(), 'INVALID_SOURCE_TYPE'); paths.push(path); }
    }
  };
  walk(output, '');
  return paths.sort(compare);
}

/**
 * Offline license copier for a controlled build tree. Does not install, fetch,
 * execute reference/app code, overwrite, adopt, remove or retry any output.
 * Returns the exact manifest. Only a successful return/CLI exit is publishable.
 */
export async function packageLicenses({
  root = fileURLToPath(new URL('../../', import.meta.url)), output, outputRoot = root,
} = {}) {
  root = localAbsolute(root, 'INVALID_ROOT');
  outputRoot = localAbsolute(outputRoot, 'INVALID_ROOT');
  output = localAbsolute(output, 'INVALID_OUTPUT');
  need(inside(outputRoot, output), 'OUTPUT_ESCAPE');
  const identities = new Map();
  directories(root, identities, 'INVALID_ROOT');
  directories(outputRoot, identities, 'INVALID_ROOT');
  need(key(realpathSync(root)) === key(root), 'PATH_ALIAS');
  directories(dirname(output), identities, 'OUTPUT_PARENT_MISSING');
  need(absent(output), 'OUTPUT_EXISTS');
  const records = [...INPUTS].sort((a, b) => compare(a.path, b.path));
  const sourcePaths = [];
  for (const record of records) {
    need(record.sourcePath.split('/').every(segment) && record.path.split('/').every(segment), 'INVALID_MAPPING');
    const source = join(root, record.sourcePath);
    directories(dirname(source), identities, 'MISSING_SOURCE');
    plain(stat(source), false);
    sourcePaths.push(source);
  }
  windowsReparseAudit([...identities.keys(), ...sourcePaths]);
  checkIdentities(identities);
  const buffers = records.map((file, i) => readStable(sourcePaths[i], identities));
  need(buffers.reduce((sum, b) => sum + b.length, 0) <= MAX_TOTAL, 'SOURCE_SIZE');
  checkIdentities(identities);
  need(absent(output), 'OUTPUT_EXISTS');
  mkdirSync(output); // non-recursive, exclusive directory creation
  const created = stat(output); plain(created, true); identities.set(output, created);
  for (let i = 0; i < records.length; i++) {
    const target = join(output, records[i].path);
    outputDirectory(dirname(target), output, identities);
    writeNew(target, buffers[i], identities);
  }
  windowsReparseAudit([...identities.keys()]);
  checkIdentities(identities);
  need(JSON.stringify(inventory(output)) === JSON.stringify(records.map(r => r.path)), 'INVENTORY_MISMATCH');
  for (let i = 0; i < records.length; i++) {
    need(readStable(sourcePaths[i], identities).equals(buffers[i]), 'SOURCE_CHANGED');
    need(readStable(join(output, records[i].path), identities).equals(buffers[i]), 'HASH_MISMATCH');
  }
  const manifest = {
    schema: 1, kind: 'licenses', status: 'complete', sourcePathBase: 'root', fileHashExcludes: [MANIFEST],
    files: records.map((file, i) => ({ ...file, bytes: buffers[i].length, sha256: hash(buffers[i]) })),
    redistributionPermissionVerified: false,
  };
  writeNew(join(output, MANIFEST), Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`), identities);
  return manifest;
}

async function main(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    const name = args[i];
    need(name === '--output' && args[i + 1] && !args[i + 1].startsWith('--') &&
      !Object.hasOwn(options, name.slice(2)), 'CLI_USAGE');
    options[name.slice(2)] = args[i + 1];
  }
  need(options.output, 'CLI_USAGE');
  const result = await packageLicenses({ ...options, outputRoot: tmpRoot });
  console.log(JSON.stringify({ status: result.status, files: result.files.length }));
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`License packaging failed: ${error.code ?? 'PACKAGE_FAILED'}`);
    process.exitCode = 1;
  });
}
