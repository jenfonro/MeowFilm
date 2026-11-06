import {
  closeSync, constants, fstatSync, fsyncSync, ftruncateSync, lstatSync,
  mkdirSync, openSync, readFileSync, readdirSync, readSync, realpathSync, writeSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, isAbsolute, join, matchesGlob, parse, posix, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { isDeepStrictEqual } from 'node:util';

const PRODUCTS = Object.freeze({
  aggregate: ['music', 'cat', 'source'],
});
const MODULE_FILES = Object.freeze({
  music: 'modules/music-script.mjs', cat: 'modules/cat-script.mjs', source: 'modules/source-runtime.mjs',
});
const MANIFEST = 'runtime-manifest.json';
const SHARED_SOURCE = 'apps/runtime';
const SHARED_FILES = ['bootstrap.mjs', 'entry.mjs', 'client.mjs', 'contracts.mjs',
  'modules/source-candidates.mjs', ...Object.values(MODULE_FILES)];
const SHARED_IMPORTS = new Set([
  'node:buffer', 'node:crypto', '@meowfilm/runtime-bridge', '@meowfilm/script-host', '@meowfilm/source-runtime',
]);
const NATIVE_POLICY = 'scripts/quickjs-native.json';
const NATIVE_ARTIFACT = 'packages/script-host/build/Release/script_host_native.node';
const NATIVE_RECEIPT = 'packages/script-host/build/Release/script-host-native.json';
const SOURCE_SDK = 'packages/script-host/build/Release/source-sdk.js';
const NATIVE_SOURCES = Object.freeze({
  quickjs: { version: '0.14.0', commit: '3c051980ab7e783dfbfb1c70c014ce5e05ecf24c',
    repository: 'quickjs-ng/quickjs', license: 'LICENSES/quickjs-ng-LICENSE.txt' },
  openssl: { version: '3.5.9', commit: '45e844fa2a14ec92d146bd8f5778ac130b6625fb',
    repository: 'openssl/openssl', license: 'LICENSES/openssl-LICENSE.txt' },
  zlib: { version: '1.3.1', commit: '51b7f2abdade71cd9bb0e7a373ef2610ec6f9daf',
    repository: 'madler/zlib', license: 'LICENSES/zlib-LICENSE.txt' },
});
const OMIT_DIRECTORIES = new Set([
  'node_modules', 'test', 'tests', '__tests__', 'fixtures', '__fixtures__', '__mocks__',
  'coverage', 'benchmark', 'benchmarks', 'example', 'examples',
]);
const NATIVE_FILE = /(?:\.(?:node|dll|dylib|so(?:\.\d+)*|exe|a|o|wasm)|^binding\.gyp)$/i;
const LEGAL_FILE = /^(?:\.?licen[cs]e[sd]?|notice|copying|copyright|authors|third[-_]?party|sources)(?:[._-]|$)/i;
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

function check(condition, code, message) {
  if (!condition) throw Object.assign(new Error(message), { code });
}

function inside(base, target) {
  const rel = relative(base, target);
  return rel === '' || (rel !== '..' && !rel.startsWith(`..${sep}`) && !isAbsolute(rel));
}

function safeSegment(part, globs = false) {
  return part !== '' && part !== '.' && part !== '..'
    && !/[\\:\u0000-\u001f\u007f<>"|]/.test(part)
    && (globs || !/[?*[\]{}!]/.test(part))
    && !/[. ]$/.test(part)
    && !/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part);
}

function safeRelative(value, globs = false) {
  check(typeof value === 'string' && value.split('/').every(part => safeSegment(part, globs)),
    'UNSAFE_PATH', `Unsafe relative path: ${String(value)}`);
  return value;
}

function packageName(value) {
  check(typeof value === 'string'
    && /^(?:@[a-z0-9][a-z0-9._-]*\/)?[a-z0-9][a-z0-9._-]*$/.test(value),
  'INVALID_PACKAGE_NAME', `Invalid package name: ${String(value)}`);
  return safeRelative(value);
}

function statOrMissing(path) {
  try { return lstatSync(path); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

// All source paths except the explicitly checked root workspace links must be real
// files/directories. Reject even in-tree links rather than silently changing identity.
function sourcePath(root, location, allowMissing = false) {
  safeRelative(location);
  let current = root;
  const parts = location.split('/');
  for (let i = 0; i < parts.length; i++) {
    current = join(current, parts[i]);
    const stat = statOrMissing(current);
    if (!stat && allowMissing) return null;
    check(stat, 'MISSING_SOURCE', `Missing source: ${location}`);
    check(!stat.isSymbolicLink(), 'UNSAFE_SYMLINK', `Source symlink is not allowed: ${location}`);
    check(i === parts.length - 1 || stat.isDirectory(), 'UNSAFE_PATH', `Not a directory: ${location}`);
  }
  check(inside(root, realpathSync(current)), 'UNSAFE_PATH', `Source escapes root: ${location}`);
  return current;
}

function readJSON(root, location) {
  const file = sourcePath(root, location);
  check(lstatSync(file).isFile(), 'INVALID_SOURCE', `Not a regular file: ${location}`);
  const bytes = readFileSync(file);
  let value;
  try { value = JSON.parse(bytes.toString('utf8')); }
  catch { check(false, 'INVALID_JSON', `Invalid JSON: ${location}`); }
  check(isObject(value), 'INVALID_JSON', `Expected JSON object: ${location}`);
  return { value, hash: sha256(bytes) };
}

function addInput(inputs, input) {
  const previous = inputs.find(value => value.path === input.path);
  check(!previous || previous.sha256 === input.sha256,
    'SOURCE_CHANGED', `${input.path} has inconsistent input hashes`);
  if (!previous) inputs.push(input);
}

function fileHash(root, location) {
  const file = sourcePath(root, location);
  const fd = openSync(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    check(fstatSync(fd).isFile(), 'INVALID_SOURCE', `Not a regular file: ${location}`);
    const hash = createHash('sha256'), buffer = Buffer.alloc(64 * 1024);
    let count;
    while ((count = readSync(fd, buffer, 0, buffer.length, null)) !== 0) {
      hash.update(buffer.subarray(0, count));
    }
    return hash.digest('hex');
  } finally { closeSync(fd); }
}

function inputFiles(root, location) {
  return readdirSync(sourcePath(root, location), { withFileTypes: true }).flatMap(item => {
    const path = `${location}/${item.name}`;
    check(!item.isSymbolicLink(), 'UNSAFE_SYMLINK', `Native input symlink is not allowed: ${path}`);
    if (item.isDirectory()) return inputFiles(root, path);
    check(item.isFile(), 'INVALID_SOURCE', `Not a regular native input: ${path}`);
    return [path];
  });
}

function checkNativeTarget(root, artifact) {
  const fd = openSync(sourcePath(root, artifact), 'r');
  const bytes = Buffer.alloc(64 * 1024);
  let length;
  try { length = readSync(fd, bytes); } finally { closeSync(fd); }
  let valid = false;
  if (process.platform === 'linux' && length >= 20 && bytes.subarray(0, 4).equals(Buffer.from([127, 69, 76, 70]))) {
    valid = bytes[4] === 2 && bytes[5] === 1
      && bytes.readUInt16LE(18) === { x64: 62, arm64: 183 }[process.arch];
  } else if (process.platform === 'darwin' && length >= 8 && bytes.readUInt32LE(0) === 0xfeedfacf) {
    valid = bytes.readUInt32LE(4) === { x64: 0x01000007, arm64: 0x0100000c }[process.arch];
  } else if (process.platform === 'win32' && length >= 64 && bytes.toString('ascii', 0, 2) === 'MZ') {
    const offset = bytes.readUInt32LE(60);
    valid = offset + 6 <= length && bytes.readUInt32LE(offset) === 0x00004550
      && bytes.readUInt16LE(offset + 4) === { x64: 0x8664, arm64: 0xaa64, ia32: 0x014c }[process.arch];
  }
  check(valid, 'NATIVE_TARGET_MISMATCH', 'Native artifact format/architecture does not match this packaging host');
}

function trustedNative(root, inputs) {
  const { value: policy, hash: policyHash } = readJSON(root, NATIVE_POLICY);
  const { value: receipt, hash: receiptHash } = readJSON(root, NATIVE_RECEIPT);
  const digest = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
  const artifact = (value, path) => isObject(value) && value.path === path
    && Number.isSafeInteger(value.bytes) && value.bytes > 0 && digest(value.sha256);
  check(policy.schemaVersion === 1 && policy.abi === 1 && policy.napi === 8
    && isObject(policy.sources) && Object.keys(policy.sources).length === 3
    && isDeepStrictEqual(policy.artifact, { path: NATIVE_ARTIFACT, manifest: NATIVE_RECEIPT })
    && Array.isArray(policy.patches) && policy.patches.length === 3
    && ['task-context.patch', 'bigint-uintn.patch', 'utf16-cstring.patch'].every((name, index) =>
      policy.patches[index]?.path === `packages/script-host/native/${name}`
      && policy.patches[index]?.source === 'quickjs' && digest(policy.patches[index]?.sha256))
    && Array.isArray(policy.mandatoryInputs) && Array.isArray(policy.inputDirectories)
    && policy.inputDirectories.includes('packages/script-host/native')
    && policy.nodeHeaders?.version === '24.21.0'
    && policy.nodeHeaders.url === 'https://nodejs.org/dist/v24.21.0/node-v24.21.0-headers.tar.gz'
    && policy.nodeHeaders.archivePath === 'tmp/build/runtime-native/node-headers.tar.gz'
    && digest(policy.nodeHeaders.sha256),
  'INVALID_NATIVE_POLICY', 'Native policy must pin the reviewed ABI, sources, patch and artifact');
  const sources = {};
  for (const [name, expected] of Object.entries(NATIVE_SOURCES)) {
    const source = policy.sources[name];
    check(isObject(source) && source.version === expected.version && source.commit === expected.commit
      && source.url === `https://codeload.github.com/${expected.repository}/tar.gz/${expected.commit}`
      && digest(source.sha256) && source.license?.path === expected.license && digest(source.license.sha256)
      && source.archivePath === `tmp/build/runtime-native/${name}.tar.gz`
      && source.sourcePath === `tmp/build/runtime-native/${name}`,
    'INVALID_NATIVE_POLICY', `Unreviewed native source: ${name}`);
    sources[name] = { version: source.version, commit: source.commit, url: source.url, sha256: source.sha256 };
    check(fileHash(root, source.license.path) === source.license.sha256,
      'NATIVE_LICENSE_MISMATCH', `Native license does not match policy: ${name}`);
  }
  check(receipt.schemaVersion === 1 && receipt.abi === policy.abi && receipt.napi === policy.napi
    && isDeepStrictEqual(receipt.sources, sources)
    && isDeepStrictEqual(receipt.nodeHeaders, policy.nodeHeaders)
    && isObject(receipt.sourceTrees)
    && artifact(receipt.artifact, NATIVE_ARTIFACT) && artifact(receipt.sdk, SOURCE_SDK)
    && Array.isArray(receipt.inputs) && receipt.inputs.length > 0,
  'INVALID_NATIVE_RECEIPT', 'Build receipt does not match the reviewed native policy');
  check(receipt.platform === process.platform && receipt.arch === process.arch
    && Number(process.versions.napi) >= receipt.napi,
  'NATIVE_TARGET_MISMATCH', 'Native build target does not match this packaging host');
  const recorded = new Map();
  for (const input of receipt.inputs) {
    check(isObject(input) && digest(input.sha256), 'INVALID_NATIVE_RECEIPT', 'Malformed native build input');
    safeRelative(input.path);
    check(!recorded.has(input.path), 'INVALID_NATIVE_RECEIPT', `Duplicate native build input: ${input.path}`);
    recorded.set(input.path, input.sha256);
  }
  const required = [
    NATIVE_POLICY, 'packages/script-host/binding.gyp', 'scripts/quickjs/build-native.mjs',
    'scripts/quickjs/build-source-sdk.mjs', 'package.json', 'package-lock.json',
    policy.nodeHeaders.archivePath,
    ...policy.mandatoryInputs,
    ...policy.patches.map(input => input.path),
    ...Object.values(policy.sources).flatMap(source => [source.license.path, source.archivePath]),
    ...policy.inputDirectories.flatMap(path => inputFiles(root, safeRelative(path))),
  ];
  for (const path of required) {
    check(recorded.has(path), 'NATIVE_INPUT_MISMATCH', `Build receipt omits an input: ${path}`);
  }
  for (const source of Object.values(policy.sources)) {
    check(recorded.get(source.archivePath) === source.sha256
      && recorded.get(source.license.path) === source.license.sha256,
    'NATIVE_INPUT_MISMATCH', `Build source differs from policy: ${source.version}`);
  }
  for (const [name, source] of Object.entries(policy.sources)) {
    const tree = receipt.sourceTrees[name];
    check(tree?.path === source.sourcePath && Array.isArray(tree.files) && tree.files.length > 0
      && new Set(tree.files).size === tree.files.length
      && tree.files.every(path => typeof path === 'string' && path.startsWith(`${source.sourcePath}/`)
        && recorded.has(path)),
    'NATIVE_INPUT_MISMATCH', `Build receipt omits the native source inventory: ${name}`);
  }
  check(recorded.get(policy.nodeHeaders.archivePath) === policy.nodeHeaders.sha256,
    'NATIVE_INPUT_MISMATCH', 'Node headers differ from policy');
  check(recorded.get(NATIVE_POLICY) === policyHash && policy.patches.every(patch =>
    recorded.get(patch.path) === patch.sha256),
  'NATIVE_INPUT_MISMATCH', 'Native policy or patches changed since the build');
  for (const [path, sha256] of recorded) {
    check(fileHash(root, path) === sha256, 'NATIVE_INPUT_MISMATCH', `Native input changed since the build: ${path}`);
    addInput(inputs, { path, sha256 });
  }
  for (const value of [receipt.artifact, receipt.sdk]) {
    check(lstatSync(sourcePath(root, value.path)).size === value.bytes
      && fileHash(root, value.path) === value.sha256,
    'NATIVE_HASH_MISMATCH', `Build output differs from its receipt: ${value.path}`);
  }
  checkNativeTarget(root, NATIVE_ARTIFACT);
  addInput(inputs, { path: NATIVE_RECEIPT, sha256: receiptHash });
  return { policy, receipt, receiptHash };
}

function dependencyMap(manifest, field, location) {
  const value = manifest[field] ?? {};
  check(isObject(value), 'INVALID_DEPENDENCIES', `${location}: invalid ${field}`);
  for (const [name, range] of Object.entries(value)) {
    packageName(name);
    check(typeof range === 'string' && range.trim(), 'INVALID_DEPENDENCIES',
      `${location}: invalid dependency range for ${name}`);
  }
  return { ...value };
}

function checkManifest(manifest, locked, location, root = false) {
  check(isObject(locked) && !locked.link, 'UNLOCKED_PACKAGE', `${location}: missing lock record`);
  check(typeof manifest.name === 'string' && (locked.name === undefined || locked.name === manifest.name),
    'NAME_MISMATCH', `${location}: package name does not match lock`);
  check(manifest.version === locked.version && parseVersion(manifest.version),
    'VERSION_MISMATCH', `${location}: installed version does not match lock`);
  for (const field of ['dependencies', 'optionalDependencies', 'peerDependencies',
    ...(root ? ['devDependencies'] : [])]) {
    const actual = dependencyMap(manifest, field, location);
    const expected = dependencyMap(locked, field, location);
    if (field === 'dependencies') {
      for (const name of Object.keys(manifest.optionalDependencies ?? {})) delete actual[name];
      for (const name of Object.keys(locked.optionalDependencies ?? {})) delete expected[name];
    }
    check(isDeepStrictEqual(actual, expected), 'DEPENDENCIES_MISMATCH',
      `${location}: installed ${field} do not match lock`);
  }
  const meta = manifest.peerDependenciesMeta ?? {};
  check(isObject(meta) && isDeepStrictEqual(meta, locked.peerDependenciesMeta ?? {}),
    'PEER_METADATA_MISMATCH', `${location}: peer metadata does not match lock`);
  for (const [name, value] of Object.entries(meta)) {
    packageName(name);
    check(isObject(value) && (value.optional === undefined || typeof value.optional === 'boolean'),
      'PEER_METADATA_MISMATCH', `${location}: invalid peer metadata for ${name}`);
  }
}

function parseVersion(value) {
  if (typeof value !== 'string') return null;
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$/.exec(value);
  if (!match) return null;
  const numbers = match.slice(1, 4).map(Number);
  const pre = match[4]?.split('.') ?? [];
  const build = match[5]?.split('.') ?? [];
  if (!numbers.every(Number.isSafeInteger) || [...pre, ...build].some(x => x === '')
    || pre.some(x => /^\d+$/.test(x) && x.length > 1 && x[0] === '0')) return null;
  return { numbers, pre };
}

function versionCompare(a, b) {
  for (let i = 0; i < 3; i++) if (a.numbers[i] !== b.numbers[i]) return compare(a.numbers[i], b.numbers[i]);
  if (!a.pre.length || !b.pre.length) return compare(b.pre.length > 0, a.pre.length > 0);
  for (let i = 0; i < Math.max(a.pre.length, b.pre.length); i++) {
    if (a.pre[i] === undefined || b.pre[i] === undefined) return compare(a.pre.length, b.pre.length);
    const x = a.pre[i], y = b.pre[i];
    if (x === y) continue;
    const xn = /^\d+$/.test(x), yn = /^\d+$/.test(y);
    if (xn && yn) return compare(BigInt(x), BigInt(y));
    return xn !== yn ? (xn ? -1 : 1) : compare(x, y);
  }
  return 0;
}

// No dependency code is loaded to interpret ranges. Supported npm semver syntax:
// exact/partial/x, comparators, caret, tilde, hyphen, AND, OR and prereleases.
// Aliases, tags, URLs and file specs fail closed rather than invoking npm.
function satisfies(version, range) {
  const actual = parseVersion(version);
  check(actual && typeof range === 'string', 'INVALID_RANGE', `Invalid version/range: ${version}`);
  function partial(text) {
    const clean = text.replace(/^v/, '');
    const exact = parseVersion(clean);
    if (exact) return { ...exact, count: 3, prereleaseAnchor: exact.pre.length > 0 };
    check(/^(?:0|[1-9]\d*|[xX*])(?:\.(?:0|[1-9]\d*|[xX*])){0,2}$/.test(clean),
      'UNSUPPORTED_RANGE', `Unsupported dependency range: ${range}`);
    const parts = clean.split('.');
    let count = parts.findIndex(x => /^[xX*]$/.test(x));
    if (count < 0) count = parts.length;
    check(parts.slice(count).every(x => /^[xX*]$/.test(x)),
      'UNSUPPORTED_RANGE', `Unsupported dependency range: ${range}`);
    const numbers = [0, 1, 2].map(i => i < count ? Number(parts[i]) : 0);
    check(numbers.every(Number.isSafeInteger), 'INVALID_RANGE', `Invalid dependency range: ${range}`);
    return { numbers, pre: [], count };
  }
  function next(v, index) {
    const numbers = v.numbers.map((x, i) => i < index ? x : i === index ? x + 1 : 0);
    return { numbers, pre: ['0'] };
  }
  return range.split('||').map(part => {
    const tests = [];
    const add = (op, value) => tests.push({ op, value });
    const clause = part.trim().replace(/([<>]=?|[~^=])\s+/g, '$1');
    const hyphen = /^(\S+)\s+-\s+(\S+)$/.exec(clause);
    if (hyphen) {
      const low = partial(hyphen[1]), high = partial(hyphen[2]);
      if (low.count) add('>=', low);
      if (high.count) add(high.count === 3 ? '<=' : '<',
        high.count === 3 ? high : next(high, high.count - 1));
    } else {
      for (const token of clause.split(/\s+/)) {
        check(token, 'INVALID_RANGE', `Empty dependency range: ${range}`);
        const [, op, text] = /^([<>]=?|[~^=])?(.*)$/.exec(token);
        const v = partial(text);
        if (!v.count) {
          check(!op || ['=', '^', '~', '>=', '<='].includes(op),
            'UNSUPPORTED_RANGE', `Unsupported dependency range: ${range}`);
        } else if (op === '^' || op === '~') {
          add('>=', v);
          const index = op === '~' ? Math.min(1, v.count - 1)
            : v.numbers[0] ? 0 : v.count === 1 ? 0 : v.numbers[1] ? 1 : v.count === 2 ? 1 : 2;
          add('<', next(v, index));
        } else if (!op || op === '=') {
          add(v.count === 3 ? '=' : '>=', v);
          if (v.count < 3) add('<', next(v, v.count - 1));
        } else if (v.count < 3 && ['>', '<='].includes(op)) {
          const bound = next(v, v.count - 1);
          if (op === '>') bound.pre = [];
          add(op === '>' ? '>=' : '<', bound);
        } else {
          if (op === '<' && v.count < 3) v.pre = ['0'];
          add(op, v);
        }
      }
    }
    const allowedPrerelease = !actual.pre.length || tests.some(({ value }) =>
      value.prereleaseAnchor
      && value.numbers.every((number, i) => number === actual.numbers[i]));
    return allowedPrerelease && tests.every(({ op, value }) => {
      const order = versionCompare(actual, value);
      return { '=': order === 0, '>': order > 0, '>=': order >= 0, '<': order < 0, '<=': order <= 0 }[op];
    });
  }).some(Boolean);
}

function registryMetadata(record, location) {
  let url;
  try { url = new URL(record.resolved); } catch { /* Fail with the policy error below. */ }
  check(typeof record.resolved === 'string' && !/[\s\\]/.test(record.resolved)
    && url?.origin === 'https://registry.npmjs.org' && !url.username && !url.password
    && !url.search && !url.hash && url.pathname.endsWith('.tgz'),
  'INVALID_REGISTRY', `${location}: requires a credential-free HTTPS registry.npmjs.org tarball`);
  const sizes = { sha1: 20, sha256: 32, sha384: 48, sha512: 64 };
  check(typeof record.integrity === 'string' && record.integrity.trim(),
    'MISSING_INTEGRITY', `${location}: missing registry integrity`);
  for (const token of record.integrity.trim().split(/\s+/)) {
    const match = /^(sha1|sha256|sha384|sha512)-([A-Za-z0-9+/]+={0,2})$/.exec(token);
    const bytes = match && Buffer.from(match[2], 'base64');
    check(match && bytes.length === sizes[match[1]]
      && bytes.toString('base64').replace(/=+$/, '') === match[2].replace(/=+$/, ''),
    'INVALID_INTEGRITY', `${location}: malformed registry integrity`);
  }
}

function candidates(location, name) {
  const paths = [];
  for (let dir = location; ; dir = posix.dirname(dir) === '.' ? '' : posix.dirname(dir)) {
    if (posix.basename(dir) !== 'node_modules') paths.push(posix.join(dir, 'node_modules', name));
    if (!dir) break;
  }
  return paths;
}

function fileDeclarations(root, manifest, location, workspace) {
  const required = new Set();
  function target(value, exports = false, packageMain = false) {
    if (value === null) return;
    if (packageMain && value === false) return;
    if (Array.isArray(value)) { for (const item of value) target(item, exports, packageMain); return; }
    if (isObject(value)) { for (const item of Object.values(value)) target(item, exports, packageMain); return; }
    check(typeof value === 'string' && (!exports || value.startsWith('./')),
      'UNSAFE_PATH', `${location}: invalid public file target`);
    let rel = packageMain && ['.', './'].includes(value) ? 'index' : value.replace(/^\.\//, '');
    safeRelative(rel, true);
    check(!rel.includes('%') && !rel.split('/').includes('node_modules'),
      'UNSAFE_PATH', `${location}: public file target escapes package`);
    if (!/[?*[\]{}!]/.test(rel)) {
      if (packageMain) {
        // Do not use require/import to resolve or execute dependency code.
        const choices = [rel, `${rel}.js`, `${rel}.json`, `${rel}.node`,
          `${rel}/index.js`, `${rel}/index.json`, `${rel}/index.node`];
        rel = choices.find(candidate => {
          const file = sourcePath(root, `${location}/${candidate}`, true);
          return file && lstatSync(file).isFile();
        }) ?? rel;
      }
      required.add(rel);
    }
  }
  if (manifest.exports !== undefined) target(manifest.exports, true);
  for (const field of ['main', 'module', 'types', 'typings', 'bin']) {
    if (manifest[field] !== undefined) target(manifest[field], false, field === 'main');
  }
  // npm has already selected registry tarball contents. We copy that installed
  // runtime tree, not re-run its npm-pack rules (e.g. tough-cookie's !__tests__).
  // Only workspace files rules are interpreted by this tool.
  if (workspace && manifest.files !== undefined) {
    check(Array.isArray(manifest.files), 'UNSAFE_PATH', `${location}: files must be an array`);
    for (const file of manifest.files) {
      check(typeof file === 'string' && !file.startsWith('!'), 'UNSAFE_PATH', `${location}: unsupported files pattern`);
      safeRelative(file.replace(/^\.\//, '').replace(/\/$/, ''), true);
    }
  }
  return required;
}

function buildGraph(root, product) {
  const rootJSON = readJSON(root, 'package.json');
  const lockJSON = readJSON(root, 'package-lock.json');
  const manifest = rootJSON.value, lock = lockJSON.value;
  check(!sourcePath(root, 'npm-shrinkwrap.json', true), 'LOCK_CONFLICT', 'Use only the root package-lock.json');
  check([2, 3].includes(lock.lockfileVersion) && isObject(lock.packages),
    'INVALID_LOCK', 'Expected an npm v2/v3 packages lock');
  check(lock.name === manifest.name && lock.version === manifest.version,
    'ROOT_MISMATCH', 'Root package identity does not match lock');
  checkManifest(manifest, lock.packages[''], '<root>', true);
  check(isDeepStrictEqual(manifest.workspaces, lock.packages[''].workspaces),
    'WORKSPACE_MISMATCH', 'Root workspace declarations do not match lock');
  const patterns = manifest.workspaces?.packages ?? manifest.workspaces;
  check(Array.isArray(patterns) && patterns.length, 'WORKSPACE_MISMATCH', 'Missing root workspaces');
  for (const pattern of patterns) safeRelative(pattern, true);
  for (const [location, value] of Object.entries(lock.packages)) {
    if (location) safeRelative(location);
    check(isObject(value), 'INVALID_LOCK', `Invalid lock record: ${location}`);
    if (value.link) safeRelative(value.resolved);
  }
  const selected = ['runtime-bridge', 'source-runtime', 'script-host'];
  const workspacePaths = new Map(selected.map(name => [`packages/${name}`, `node_modules/@meowfilm/${name}`]));
  const nodes = new Map(), edges = [], omitted = [];
  const inputs = [{ path: 'package.json', sha256: rootJSON.hash }, { path: 'package-lock.json', sha256: lockJSON.hash }];
  function outputPath(location) {
    for (const [source, dest] of workspacePaths) {
      if (location === source || location.startsWith(`${source}/node_modules/`)) return dest + location.slice(source.length);
    }
    check(location.startsWith('node_modules/'), 'UNSUPPORTED_LAYOUT', `Unsupported installed location: ${location}`);
    return location;
  }

  function followLink(location, name) {
    const link = lock.packages[location];
    check(link?.link === true && workspacePaths.has(link.resolved),
      'PRODUCT_CLOSURE', `${location}: workspace is not enabled for ${product}`);
    check(location === `node_modules/${name}` && workspacePaths.get(link.resolved) === location,
      'UNSAFE_LINK', `${location}: unexpected workspace link`);
    const source = sourcePath(root, link.resolved);
    check(patterns.some(pattern => matchesGlob(link.resolved, pattern)),
      'WORKSPACE_MISMATCH', `${link.resolved}: not declared as a root workspace`);
    sourcePath(root, posix.dirname(location));
    const installed = join(root, location);
    check(statOrMissing(installed)?.isSymbolicLink(), 'MISSING_WORKSPACE_LINK',
      `${location}: expected an installed workspace link`);
    check(realpathSync(installed) === source, 'UNSAFE_SYMLINK', `${location}: workspace link target mismatch`);
    return link.resolved;
  }

  function visit(location, expectedName) {
    const locked = lock.packages[location];
    check(locked, 'UNLOCKED_PACKAGE', `${location}: package is not locked`);
    if (locked.link) return visit(followLink(location, expectedName), expectedName);
    if (nodes.has(location)) {
      check(nodes.get(location).name === expectedName, 'NAME_MISMATCH', `${location}: package alias is not supported`);
      return nodes.get(location);
    }
    const workspace = workspacePaths.has(location);
    check(workspace || location.includes('node_modules/'), 'PRODUCT_CLOSURE', `${location}: unexpected workspace`);
    if (!workspace) registryMetadata(locked, location);
    const source = sourcePath(root, location);
    check(lstatSync(source).isDirectory(), 'INVALID_SOURCE', `${location}: expected a package directory`);
    const data = readJSON(root, `${location}/package.json`);
    const pkg = data.value;
    check(pkg.name === expectedName, 'NAME_MISMATCH', `${location}: installed package name mismatch`);
    checkManifest(pkg, locked, location);
    check(!pkg.gypfile || (location === 'packages/script-host' && pkg.gypfile === true),
      'NATIVE_PAYLOAD', `${location}: native build metadata is not allowed`);
    check(pkg.name !== 'jsdom',
      'PRODUCT_CLOSURE', 'jsdom is outside the App script closure');
    if (pkg.name.startsWith('@meowfilm/')) {
      check(workspace && selected.includes(pkg.name.slice('@meowfilm/'.length)),
        'PRODUCT_CLOSURE', `${location}: workspace is not enabled`);
    }
    if (workspace) {
      for (const file of ['package-lock.json', 'npm-shrinkwrap.json']) {
        check(!sourcePath(root, `${location}/${file}`, true), 'LOCK_CONFLICT', `${location}: use only the root lock`);
      }
      check(pkg.exports !== undefined, 'MISSING_PUBLIC_ENTRY', `${location}: missing workspace exports`);
    }
    const node = {
      location, path: outputPath(location), name: pkg.name, version: pkg.version,
      workspace, manifest: pkg, manifestHash: data.hash, required: fileDeclarations(root, pkg, location, workspace), locked,
      nativeBuild: pkg.gypfile ? trustedNative(root, inputs) : undefined,
    };
    nodes.set(location, node); // Register before traversal: real dependency cycles are legal.
    const dependencies = dependencyMap(pkg, 'dependencies', location);
    const optional = dependencyMap(pkg, 'optionalDependencies', location);
    for (const name of Object.keys(optional)) delete dependencies[name];
    for (const [kind, entries] of [
      ['dependency', dependencies], ['optionalDependency', optional],
      ['peer', dependencyMap(pkg, 'peerDependencies', location)],
    ]) {
      for (const name of Object.keys(entries).sort()) {
        const optionalEdge = kind === 'optionalDependency' || (kind === 'peer' && pkg.peerDependenciesMeta?.[name]?.optional === true);
        let found;
        for (const candidate of candidates(location, name)) {
          const record = lock.packages[candidate];
          let installed;
          if (record?.link) {
            const parent = sourcePath(root, posix.dirname(candidate), true);
            installed = parent && statOrMissing(join(root, candidate));
          } else installed = sourcePath(root, candidate, true);
          if (!installed && !record) continue;
          check(record, 'UNLOCKED_PACKAGE', `${candidate}: installed dependency is not locked`);
          if (installed) found = visit(candidate, name);
          break; // Never fall through a missing locked nearest dependency.
        }
        if (!found) {
          check(optionalEdge, 'MISSING_DEPENDENCY', `${location}: missing required ${kind} ${name}`);
          omitted.push({ from: node.path, name, kind, range: entries[name], reason: 'not-installed' });
        } else {
          check(satisfies(found.version, entries[name]), 'RANGE_MISMATCH',
            `${location}: ${name}@${found.version} does not satisfy ${entries[name]}`);
        }
        edges.push({ from: node, name, to: found });
      }
    }
    return node;
  }

  for (const name of selected) visit(`node_modules/@meowfilm/${name}`, `@meowfilm/${name}`);
  const destinations = new Map();
  for (const node of nodes.values()) {
    check(!destinations.has(node.path), 'PATH_COLLISION', `Duplicate package destination: ${node.path}`);
    destinations.set(node.path, node);
  }
  for (const edge of edges) {
    const resolved = candidates(edge.from.path, edge.name).find(path => destinations.has(path));
    check(destinations.get(resolved) === edge.to, 'RESOLUTION_CHANGED',
      `${edge.from.path}: relocation changes resolution of ${edge.name}`);
  }
  return { nodes: [...nodes.values()].sort((a, b) => compare(a.path, b.path)),
    omitted: omitted.sort((a, b) => compare(`${a.from}/${a.name}/${a.kind}`, `${b.from}/${b.name}/${b.kind}`)),
    inputs };
}

function newOutput(root, output) {
  check(typeof output === 'string' && isAbsolute(output) && !/^(?:\\\\|\/\/)/.test(output),
    'INVALID_OUTPUT', 'Output must be a new local absolute directory');
  const tail = output.slice(parse(output).root.length).split(sep === '\\' ? /[\\/]/ : /\//);
  if (tail.at(-1) === '') tail.pop();
  check(tail.length && tail.every(part => safeSegment(part)), 'UNSAFE_PATH', 'Unsafe output path');
  output = resolve(output);
  for (const input of ['apps', 'packages', 'node_modules', '.git']) {
    check(!inside(join(root, input), output), 'OUTPUT_OVERLAP', `Output overlaps source tree: ${input}`);
  }
  let parent = dirname(output);
  for (;;) {
    const stat = statOrMissing(parent);
    check(stat?.isDirectory() && !stat.isSymbolicLink(),
      'UNSAFE_OUTPUT_PARENT', 'Output parent must exist and have no symlink ancestors');
    if (dirname(parent) === parent) break;
    parent = dirname(parent);
  }
  check(!statOrMissing(output), 'OUTPUT_EXISTS', 'Output already exists; choose a new directory');
  mkdirSync(output); // Deliberately not recursive; an existing/racing output is never adopted.
  return output;
}

function includedFile(rel, node) {
  const parts = rel.split('/');
  if (parts.some(part => LEGAL_FILE.test(part))) return true;
  if (parts.some(part => part.startsWith('.')) || /\.(?:test|spec)\.[cm]?[jt]sx?$/i.test(rel)
    || /(?:^|\/)(?:package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml)$/i.test(rel)) return false;
  if (!node.workspace || node.manifest.files === undefined || rel === 'package.json' || node.required.has(rel)) return true;
  return node.manifest.files.some(pattern => {
    pattern = pattern.replace(/^\.\//, '').replace(/\/$/, '');
    for (let path = rel; path !== '.'; path = posix.dirname(path)) {
      if (path === pattern || matchesGlob(path, pattern)) return true;
    }
    return false;
  });
}

/**
 * Offline, dependency-free packaging. Returns a manifest Promise; does not import
 * app/dependency code, install, fetch, overwrite an old output, or delete anything.
 * Synchronous file operations keep each local validation/copy step contiguous.
 */
export async function packageRuntime({ root = fileURLToPath(new URL('../', import.meta.url)), product, output,
  profile = 'common-native' } = {}) {
  check(Object.hasOwn(PRODUCTS, product), 'INVALID_PRODUCT', 'Only the App wire identity aggregate is supported');
  check(profile === 'common-native', 'INVALID_PROFILE', 'Only common-native is supported');
  check(typeof root === 'string' && isAbsolute(root) && !/^(?:\\\\|\/\/)/.test(root),
    'INVALID_ROOT', 'Root must be a local absolute directory');
  root = realpathSync(root);
  check(lstatSync(root).isDirectory(), 'INVALID_ROOT', 'Root must be a directory');
  const modules = PRODUCTS[product];
  const sharedFiles = SHARED_FILES;
  output = newOutput(root, output);
  const outputIdentity = lstatSync(output);
  let manifestFD;
  const files = [], occupied = new Map([[MANIFEST.toLowerCase(), { path: MANIFEST, directory: false }]]);
  const manifest = {
    schemaVersion: 1, status: 'building', product,
    entry: 'app/entry.mjs',
    assemblyProfile: profile,
    modules,
    integrityScope: 'Build-input inventory only; not an upstream signature or tarball integrity verification.',
    files,
  };
  function saveManifest() {
    const bytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
    ftruncateSync(manifestFD, 0);
    for (let offset = 0; offset < bytes.length;) offset += writeSync(manifestFD, bytes, offset, bytes.length - offset, offset);
    fsyncSync(manifestFD);
  }
  function destination(rel) {
    safeRelative(rel);
    const rootStat = lstatSync(output);
    check(!rootStat.isSymbolicLink() && rootStat.dev === outputIdentity.dev && rootStat.ino === outputIdentity.ino,
      'UNSAFE_OUTPUT', 'Output directory identity changed');
    function reserve(path, directory) {
      const key = path.normalize('NFC').toLowerCase(), prior = occupied.get(key);
      check(!prior || (directory && prior.directory && prior.path === path),
        'PATH_COLLISION', `Case/Unicode-colliding output path: ${path}`);
      occupied.set(key, { path, directory });
    }
    let parent = output;
    let relativeParent = '';
    for (const part of rel.split('/').slice(0, -1)) {
      relativeParent = posix.join(relativeParent, part);
      reserve(relativeParent, true);
      parent = join(parent, part);
      if (!statOrMissing(parent)) mkdirSync(parent);
      const stat = lstatSync(parent);
      check(stat.isDirectory() && !stat.isSymbolicLink(), 'UNSAFE_OUTPUT', 'Output parent is not a real directory');
    }
    reserve(rel, false);
    return join(output, rel);
  }
  function copy(location, rel, expectedHash, nativeArtifact) {
    const file = sourcePath(root, location);
    check(!NATIVE_FILE.test(posix.basename(location))
      || (location === NATIVE_ARTIFACT && nativeArtifact?.path === location),
      'NATIVE_PAYLOAD', `Native payload is not allowed: ${location}`);
    const stat = lstatSync(file);
    check(stat.isFile(), 'INVALID_SOURCE', `Not a regular source file: ${location}`);
    const target = destination(rel);
    let inputFD, outputFD;
    manifest.incompleteFile = rel;
    try {
      inputFD = openSync(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
      const before = fstatSync(inputFD);
      check(before.isFile() && before.ino === stat.ino && before.dev === stat.dev,
        'SOURCE_CHANGED', `Source changed: ${location}`);
      outputFD = openSync(target, 'wx', 0o644);
      const hash = createHash('sha256'), buffer = Buffer.alloc(64 * 1024);
      let bytes = 0, count;
      while ((count = readSync(inputFD, buffer, 0, buffer.length, null)) !== 0) {
        hash.update(buffer.subarray(0, count));
        for (let offset = 0; offset < count;) offset += writeSync(outputFD, buffer, offset, count - offset);
        bytes += count;
      }
      const after = fstatSync(inputFD), digest = hash.digest('hex');
      check(before.size === after.size && before.mtimeMs === after.mtimeMs && before.ctimeMs === after.ctimeMs
        && bytes === before.size && (!expectedHash || expectedHash === digest),
      'SOURCE_CHANGED', `Source changed during packaging: ${location}`);
      check(!nativeArtifact || (bytes === nativeArtifact.bytes && digest === nativeArtifact.sha256),
        'NATIVE_HASH_MISMATCH', `Native artifact differs from its build receipt: ${location}`);
      files.push({ path: rel, bytes, sha256: digest });
      addInput(manifest.inputs, { path: location, sha256: digest });
      delete manifest.incompleteFile;
    } finally {
      if (outputFD !== undefined) closeSync(outputFD);
      if (inputFD !== undefined) closeSync(inputFD);
    }
  }
  function copyPackage(node, rel = '') {
    const location = posix.join(node.location, rel);
    const directory = sourcePath(root, location);
    for (const item of readdirSync(directory, { withFileTypes: true }).sort((a, b) => compare(a.name, b.name))) {
      safeRelative(item.name);
      const child = posix.join(rel, item.name);
      if (item.name === 'node_modules') continue; // Only the resolved graph can add dependencies.
      check(!item.isSymbolicLink(), 'UNSAFE_SYMLINK', `Package symlink is not allowed: ${location}/${item.name}`);
      // Build intermediates and reviewed native sources are never runtime payloads.
      // The three exact build outputs are copied below, through sourcePath checks.
      if (node.nativeBuild && ['build', 'native', 'binding.gyp'].includes(child)) continue;
      check(!NATIVE_FILE.test(item.name),
        'NATIVE_PAYLOAD', `Native payload is not allowed: ${location}/${item.name}`);
      if (item.isDirectory()) {
        if (OMIT_DIRECTORIES.has(item.name.toLowerCase())
          || (item.name.startsWith('.') && !LEGAL_FILE.test(item.name))) continue;
        copyPackage(node, child);
      } else if (includedFile(child, node)) {
        copy(posix.join(node.location, child), posix.join(node.path, child),
          child === 'package.json' ? node.manifestHash : undefined);
      }
    }
  }
  function copySharedRuntime(rel = '') {
    const location = posix.join(SHARED_SOURCE, rel);
    for (const item of readdirSync(sourcePath(root, location), { withFileTypes: true })) {
      const child = posix.join(rel, item.name);
      safeRelative(child);
      check(!item.isSymbolicLink(), 'UNSAFE_SYMLINK', `Shared runtime symlink is not allowed: ${location}/${item.name}`);
      if (item.isDirectory()) {
        check(child === 'modules', 'SHARED_CLOSURE', `Unexpected shared runtime directory: ${child}`);
        copySharedRuntime(child);
      } else {
        check(item.isFile() && SHARED_FILES.includes(child), 'SHARED_CLOSURE', `Unexpected shared runtime source: ${child}`);
      }
    }
    if (rel) return;
    for (const file of sharedFiles) {
      const location = `${SHARED_SOURCE}/${file}`;
      const input = readFileSync(sourcePath(root, location)), hash = sha256(input);
      const text = new TextDecoder('utf-8', { fatal: true }).decode(input);
      for (const match of text.matchAll(/(?:\bfrom\s*|\bimport\s*(?:\(\s*)?|\brequire\s*\()\s*['"]([^'"]+)['"]/g)) {
        const specifier = match[1];
        const target = posix.normalize(posix.join(posix.dirname(file), specifier));
        check(specifier.startsWith('.') ? sharedFiles.includes(target) : SHARED_IMPORTS.has(specifier),
          'SHARED_IMPORT', `${location}: import escapes the shared runtime closure`);
      }
      copy(location, `app/${file}`, hash);
    }
  }
  try {
    manifestFD = openSync(join(output, MANIFEST), 'wx', 0o644);
    saveManifest();
    const graph = buildGraph(root, product);
    manifest.inputs = graph.inputs;
    manifest.omittedOptionalDependencies = graph.omitted;
    manifest.dependencies = graph.nodes.map(node => ({
      name: node.name, version: node.version, path: node.path, lockPath: node.location,
      workspace: node.workspace,
      ...(!node.workspace ? { resolved: node.locked.resolved, integrity: node.locked.integrity } : {}),
    }));
    manifest.trustedNative = graph.nodes.filter(node => node.nativeBuild).map(node => {
      const { receipt, policy } = node.nativeBuild;
      return { package: node.name, version: node.version,
        ...receipt.artifact, path: `${node.path}/build/Release/script_host_native.node`,
        platform: receipt.platform, arch: receipt.arch, abi: receipt.abi, napi: receipt.napi,
        sources: receipt.sources, patches: policy.patches,
        sdk: { ...receipt.sdk, path: `${node.path}/build/Release/source-sdk.js` },
        licenses: Object.values(policy.sources).map(source => ({ ...source.license,
          path: `licenses/${posix.basename(source.license.path)}` })),
      };
    });
    copySharedRuntime();
    for (const node of graph.nodes) {
      copyPackage(node);
      if (node.nativeBuild) {
        const { receipt, policy, receiptHash } = node.nativeBuild;
        copy(NATIVE_ARTIFACT, `${node.path}/build/Release/script_host_native.node`,
          receipt.artifact.sha256, receipt.artifact);
        copy(NATIVE_RECEIPT, `${node.path}/build/Release/script-host-native.json`, receiptHash);
        copy(SOURCE_SDK, `${node.path}/build/Release/source-sdk.js`, receipt.sdk.sha256);
        for (const { license } of Object.values(policy.sources)) {
          copy(license.path, `licenses/${posix.basename(license.path)}`, license.sha256);
        }
      }
      for (const entry of node.required) {
        check(files.some(file => file.path === `${node.path}/${entry}`), 'MISSING_PUBLIC_FILE',
          `${node.location}: public file missing or excluded: ${entry}`);
      }
    }
    for (const input of graph.inputs) {
      check(fileHash(root, input.path) === input.sha256,
        'SOURCE_CHANGED', `${input.path} changed during packaging`);
    }
    files.sort((a, b) => compare(a.path, b.path));
    manifest.status = 'complete';
    saveManifest();
    return manifest;
  } catch (error) {
    manifest.status = 'failed';
    manifest.error = { code: error.code ?? 'PACKAGING_FAILED', message: error.message };
    files.sort((a, b) => compare(a.path, b.path));
    if (manifestFD !== undefined) {
      try { saveManifest(); }
      catch { error.message += ' (could not persist failed status; output is not usable)'; }
    }
    throw error;
  } finally {
    if (manifestFD !== undefined) closeSync(manifestFD);
  }
}

async function main(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    check(['--product', '--output', '--profile'].includes(args[i]) && args[i + 1] && !Object.hasOwn(options, args[i].slice(2)),
      'CLI_USAGE', 'Usage: node scripts/package-runtime.mjs --product aggregate --output <new absolute directory> [--profile common-native]');
    options[args[i].slice(2)] = args[i + 1];
  }
  check(options.product && options.output, 'CLI_USAGE', 'Both --product and --output are required');
  const result = await packageRuntime(options);
  console.log(JSON.stringify({ status: result.status, product: result.product, files: result.files.length }));
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`${error.code ?? 'PACKAGING_FAILED'}: ${error.message}`);
    process.exitCode = 1;
  });
}
