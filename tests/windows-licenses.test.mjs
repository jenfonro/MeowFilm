import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  existsSync, linkSync, lstatSync, mkdirSync, mkdtempSync, readFileSync,
  readdirSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { packageLicenses } from '../scripts/windows/package-licenses.mjs';
import { tmpRoot, temporaryRoot } from '../scripts/windows/working-paths.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const helper = join(repository, 'scripts/windows/package-licenses.mjs');
const temporary = join(temporaryRoot, 'licenses-tests');
for (const path of [tmpRoot, temporaryRoot, temporary]) {
  if (existsSync(path)) assert.equal(lstatSync(path).isSymbolicLink(), false);
  else mkdirSync(path);
}
const manifestName = 'licenses-manifest.json';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
// Test-side expectation, not the producer's internal inventory.
const sources = [
  'NOTICE.md', 'Apache-2.0.txt', 'GPL-3.0-only.txt', 'api-enhanced-MIT.txt', 'KuGouMusicApi-MIT.txt',
  'iwebplus-NOTICE.txt', 'meowmusic-NOTICE.txt', 'node-LICENSE.txt', 'qr-code-generator-LICENSE.txt',
  'quickjs-emscripten-LICENSE.txt', 'quickjs-native-MIT.txt', 'catpawrunner-MIT.txt',
  'npm-node-compat-BSD-3-Clause.txt', 'npm-node-compat-ISC.txt', 'npm-node-compat-MIT.txt',
].map(name => ({ source: `LICENSES/${name}`, path: name }));
function boundedPath(path) {
  const rel = relative(temporary, path);
  assert.ok(rel && rel !== '..' && !rel.startsWith(`..${sep}`) && !isAbsolute(rel));
  return path;
}
function write(path, bytes) {
  boundedPath(path);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, bytes, { flag: 'wx' });
}
function fixture({ omit, override, empty = false } = {}) {
  const base = mkdtempSync(join(temporary, 'case-'));
  const root = join(base, 'root');
  mkdirSync(root);
  for (const entry of empty ? [] : sources.filter(s => s.source !== omit)) {
    write(join(root, entry.source), override?.source === entry.source ? override.bytes : readFileSync(join(repository, entry.source)));
  }
  return { base, root, output: join(root, 'new-licenses') };
}
function entries(path, prefix = '') {
  return readdirSync(path, { withFileTypes: true }).flatMap(item => {
    assert.equal(item.isSymbolicLink(), false);
    const name = prefix ? `${prefix}/${item.name}` : item.name;
    return item.isDirectory() ? entries(join(path, item.name), name) : [name];
  }).sort();
}
function verify(f, manifest) {
  assert.deepEqual(JSON.parse(readFileSync(join(f.output, manifestName), 'utf8')), manifest);
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.kind, 'licenses');
  assert.equal(manifest.redistributionPermissionVerified, false);
  assert.equal(manifest.sourcePathBase, 'root');
  assert.deepEqual(manifest.fileHashExcludes, [manifestName]);
  const selected = [...sources].sort((a, b) => a.path < b.path ? -1 : 1);
  assert.deepEqual(manifest.files.map(f => f.path), selected.map(f => f.path));
  assert.deepEqual(entries(f.output), [...selected.map(f => f.path), manifestName].sort());
  for (const [i, file] of manifest.files.entries()) {
    assert.equal(file.sourcePath, selected[i].source);
    assert.ok(!isAbsolute(file.sourcePath) && !file.sourcePath.includes('\\'));
    const source = readFileSync(join(f.root, file.sourcePath));
    const copy = readFileSync(join(f.output, file.path));
    assert.deepEqual(copy, source);
    assert.equal(file.bytes, copy.length);
    assert.equal(file.sha256, digest(copy));
  }
}
async function rejects(f, code, options = {}) {
  await assert.rejects(packageLicenses({ ...f, ...options }), { code });
}
const settings = { timeout: 60000 };

test('licenses: every reviewed file is copied byte-for-byte into one flat directory', settings, async () => {
  const f = fixture();
  const result = await packageLicenses(f);
  verify(f, result);
  assert.equal(result.files.length, sources.length);
  assert.ok(result.files.every(file => !file.path.includes('/')));
});
test('licenses: output is reproducible', settings, async () => {
  const f = fixture();
  const a = await packageLicenses(f);
  const b = await packageLicenses({ ...f, output: join(f.root, 'second') });
  verify(f, a); assert.deepEqual(a, b);
});
test('licenses: copied bytes retain BOM/CRLF/non-ASCII and hashes reflect exact source', settings, async () => {
  const bytes = Buffer.from('\ufeff原文许可\r\nCopyright 合成作者\r\n', 'utf8');
  const f = fixture({ override: { source: sources[0].source, bytes } });
  const result = await packageLicenses(f);
  verify(f, result);
  const record = result.files.find(file => file.sourcePath === sources[0].source);
  assert.equal(record.bytes, bytes.length); assert.equal(record.sha256, digest(bytes));
});
for (const source of sources) {
  test(`licenses: missing required ${source.source} is rejected before creating output`, async () => {
    const f = fixture({ omit: source.source });
    await rejects(f, 'MISSING_SOURCE');
    assert.equal(existsSync(f.output), false);
  });
}
for (const bytes of [Buffer.alloc(0), Buffer.alloc(1024 * 1024 + 1, 65)]) {
  test(`licenses: ${bytes.length ? 'oversized' : 'empty'} legal input is not silently accepted`, settings, async () => {
    const f = fixture({ override: { source: sources[0].source, bytes } });
    await rejects(f, 'SOURCE_SIZE');
    assert.equal(existsSync(f.output), false);
  });
}
test('licenses: a directory cannot substitute for a required file', async () => {
  const f = fixture({ omit: sources[0].source });
  mkdirSync(join(f.root, sources[0].source));
  await rejects(f, 'INVALID_SOURCE_TYPE');
  assert.equal(existsSync(f.output), false);
});
for (const kind of ['empty-directory', 'nonempty-directory', 'file']) {
  test(`licenses: existing ${kind} is never adopted or overwritten`, async () => {
    const f = fixture();
    if (kind === 'file') write(f.output, 'sentinel');
    else {
      mkdirSync(f.output);
      if (kind === 'nonempty-directory') write(join(f.output, 'keep'), 'sentinel');
    }
    await rejects(f, 'OUTPUT_EXISTS');
    if (kind === 'file') assert.equal(readFileSync(f.output, 'utf8'), 'sentinel');
    else assert.deepEqual(readdirSync(f.output), kind === 'empty-directory' ? [] : ['keep']);
  });
}
test('licenses: successful output cannot be re-used', settings, async () => {
  const f = fixture();
  await packageLicenses(f);
  const before = readFileSync(join(f.output, manifestName));
  await rejects(f, 'OUTPUT_EXISTS');
  assert.deepEqual(readFileSync(join(f.output, manifestName)), before);
});
test('licenses: output outside root, root itself and missing parent are rejected', async () => {
  const f = fixture();
  await rejects(f, 'OUTPUT_ESCAPE', { output: join(f.base, 'outside') });
  await rejects(f, 'OUTPUT_ESCAPE', { output: f.root });
  await rejects(f, 'OUTPUT_PARENT_MISSING', { output: join(f.root, 'missing-parent', 'new') });
  assert.equal(existsSync(join(f.base, 'outside')), false);
  assert.equal(existsSync(join(f.root, 'missing-parent')), false);
});
test('licenses: explicit separate output root retains containment and source identity', settings, async () => {
  const f = fixture(), outputRoot = join(f.base, 'staging');
  mkdirSync(outputRoot);
  const output = join(outputRoot, 'licenses');
  const manifest = await packageLicenses({ root: f.root, outputRoot, output });
  verify({ root: f.root, output }, manifest);
  await rejects(f, 'OUTPUT_ESCAPE', { outputRoot, output: join(f.root, 'not-output') });
  await rejects(f, 'OUTPUT_ESCAPE', { outputRoot, output: outputRoot });
  const alias = join(f.base, 'staging-alias');
  symlinkSync(outputRoot, alias, process.platform === 'win32' ? 'junction' : 'dir');
  await rejects(f, 'REPARSE_DENIED', { outputRoot: alias, output: join(alias, 'escaped') });
  assert.equal(existsSync(join(outputRoot, 'escaped')), false);
});
for (const name of ['../escape', 'safe/../escape', 'CON.txt', 'NUL', 'bad:stream', 'trailing.', 'trailing ', 'bad?name']) {
  test(`licenses: unsafe output component ${name} rejected before normalization`, async () => {
    const f = fixture();
    await rejects(f, 'INVALID_OUTPUT', { output: `${f.root}${sep}${name.replaceAll('/', sep)}` });
    assert.equal(existsSync(f.output), false);
  });
}
test('licenses: relative, UNC and device paths are refused', async () => {
  const f = fixture();
  for (const output of ['relative-output', '\\\\server\\share\\new', '\\\\?\\C:\\new', '\\\\.\\pipe\\new']) {
    await rejects(f, 'INVALID_OUTPUT', { output });
  }
  await rejects(f, 'INVALID_ROOT', { root: 'relative-root' });
});
test('licenses: source file hardlink rejected without output', async () => {
  const f = fixture({ omit: sources[0].source });
  const backing = join(f.base, 'backing-notice');
  write(backing, 'synthetic shared file');
  linkSync(backing, join(f.root, sources[0].source));
  await rejects(f, 'HARDLINK_DENIED');
  assert.equal(existsSync(f.output), false);
});
test('licenses: source directory junction/reparse is never followed', async () => {
  const f = fixture({ empty: true }), donor = fixture();
  symlinkSync(join(donor.root, 'LICENSES'),
    join(f.root, 'LICENSES'), process.platform === 'win32' ? 'junction' : 'dir');
  await rejects(f, 'REPARSE_DENIED');
  assert.equal(existsSync(f.output), false);
});
test('licenses: output ancestor junction cannot redirect writes outside root', async () => {
  const f = fixture(), destination = join(f.base, 'outside');
  mkdirSync(destination);
  symlinkSync(destination, join(f.root, 'alias'), process.platform === 'win32' ? 'junction' : 'dir');
  await rejects(f, 'REPARSE_DENIED', { output: join(f.root, 'alias/new') });
  assert.deepEqual(readdirSync(destination), []);
});
test('licenses: root alias junction rejected rather than canonicalized and trusted', async () => {
  const f = fixture(), alias = join(f.base, 'root-alias');
  symlinkSync(f.root, alias, process.platform === 'win32' ? 'junction' : 'dir');
  await rejects(f, 'REPARSE_DENIED', { root: alias, output: join(alias, 'new') });
  assert.equal(existsSync(join(f.root, 'new')), false);
});
test('licenses: CLI successful output and strict arguments; import has no packaging side effects', settings, () => {
  const parent = mkdtempSync(join(temporary, 'cli-')), output = join(parent, 'licenses');
  const run = args => spawnSync(process.execPath, [helper, ...args],
    { cwd: repository, encoding: 'utf8', windowsHide: true, timeout: 30000 });
  const result = run(['--output', output]);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { status: 'complete', files: sources.length });
  verify({ root: repository, output }, JSON.parse(readFileSync(join(output, manifestName), 'utf8')));
  for (const args of [[], ['--product', 'aggregate', '--output', output],
    ['--root', repository, '--output', output], ['--output']]) {
    const rejected = run(args);
    assert.equal(rejected.status, 1); assert.match(rejected.stderr, /CLI_USAGE/);
  }
  const imported = spawnSync(process.execPath,
    ['--input-type=module', '-e', 'await import(process.argv[1]);', pathToFileURL(helper).href],
    { cwd: parent, encoding: 'utf8', windowsHide: true, timeout: 10000 });
  assert.equal(imported.status, 0, imported.stderr); assert.equal(imported.stdout, '');
  assert.deepEqual(readdirSync(parent), ['licenses']);
});
