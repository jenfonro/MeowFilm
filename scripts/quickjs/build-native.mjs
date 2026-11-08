import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { availableParallelism } from 'node:os';
import { createRequire } from 'node:module';
import {
  access, mkdir, readFile, writeFile, readdir, lstat, realpath, rename, rm, appendFile,
} from 'node:fs/promises';
import { basename, dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(import.meta.url);
const policyPath = 'scripts/script-host-native.json';
const policyBytes = await readFile(resolve(root, policyPath));
export const policy = JSON.parse(policyBytes.toString('utf8'));
export const loadedBuildInputs = [
  { path: policyPath, sha256: createHash('sha256').update(policyBytes).digest('hex') },
  { path: 'scripts/quickjs/build-native.mjs',
    sha256: createHash('sha256').update(await readFile(fileURLToPath(import.meta.url))).digest('hex') },
];
const cache = resolve(root, policy.build.cache);
const sha256 = data => createHash('sha256').update(data).digest('hex');
const pathOf = path => relative(root, resolve(root, path)).split(sep).join('/');
const fileHash = async path => sha256(await readFile(resolve(root, path)));
const exists = async path => access(path).then(() => true, () => false);
export const commands = [];
let headerCache = '';
function portableArgument(value) {
  if (value === process.execPath) return 'node';
  const replacements = [
    [root.replace(/[\\/]$/, ''), '${workspace}'],
    ...(headerCache ? [[headerCache, '${node-gyp-cache}']] : []),
    [dirname(process.execPath), '${node-bin}'],
  ];
  let result = value;
  for (const [path, replacement] of replacements) {
    result = result.split(path).join(replacement);
    result = result.split(path.split(sep).join('/')).join(replacement);
  }
  return result;
}

export async function run(command, args, cwd = root, capture = false, environment = {}) {
  const invocation = { command, args, cwd };
  await appendFile(resolve(cache, 'build-commands.jsonl'), JSON.stringify(invocation) + '\n');
  const portableCommand = portableArgument(command);
  commands.push({
    command: isAbsolute(portableCommand) ? basename(portableCommand) : portableCommand,
    args: args.map(portableArgument), cwd: pathOf(cwd),
  });
  return new Promise((resolveResult, reject) => {
    const child = spawn(command, args, {
      cwd, stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
      env: {
        ...process.env, ...environment,
        PATH: `${dirname(process.execPath)}${process.platform === 'win32' ? ';' : ':'}${process.env.PATH}`,
        // node-gyp's Python sources are tracked inputs, not a bytecode cache.
        PYTHONDONTWRITEBYTECODE: '1',
      },
    });
    const output = [];
    if (capture) {
      child.stdout.on('data', data => output.push(data));
      child.stderr.on('data', data => output.push(data));
    }
    child.on('error', reject);
    child.on('exit', (code, signal) => {
      const text = Buffer.concat(output).toString('utf8');
      if (code !== 0) reject(Object.assign(
        new Error(`${command} ${args.join(' ')} exited ${code ?? signal}\n${text}`), { exitCode: code ?? 1 }));
      else resolveResult(text.trim());
    });
  });
}
export async function files(directory, packageContents = false) {
  const result = [];
  for (const entry of await readdir(resolve(root, directory), { withFileTypes: true })) {
    const path = `${directory}/${entry.name}`;
    // Package dependencies are traversed from their own manifest and resolver below.
    if (packageContents && entry.name === 'node_modules') continue;
    if (entry.isSymbolicLink()) throw new Error(`Symbolic link in native inputs: ${path}`);
    if (entry.isDirectory()) result.push(...await files(path, packageContents));
    else if (entry.isFile()) result.push(path);
    else throw new Error(`Unsupported native input: ${path}`);
  }
  return result.sort();
}
export async function hashInputs(paths) {
  const inputs = [...new Set(paths)].sort(), result = [];
  // Limit open files even when dependency source trees are prepared in parallel.
  const batchSize = 16;
  for (let index = 0; index < inputs.length; index += batchSize) {
    result.push(...await Promise.all(inputs.slice(index, index + batchSize).map(async path => {
      if (path.startsWith('../') || pathOf(path) !== path) throw new Error(`Invalid native input: ${path}`);
      const stat = await lstat(resolve(root, path));
      if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Not a regular native input: ${path}`);
      return { path, sha256: await fileHash(path) };
    })));
  }
  return result;
}
async function download(url, path, expected) {
  if (await exists(path)) {
    if (sha256(await readFile(path)) !== expected) throw new Error(`Cached source hash mismatch: ${pathOf(path)}`);
    return;
  }
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Source download failed: ${response.status} ${url}`);
  const data = Buffer.from(await response.arrayBuffer());
  if (sha256(data) !== expected) throw new Error(`Source archive hash mismatch: ${url}`);
  await writeFile(`${path}.download`, data);
  await rename(`${path}.download`, path);
}
async function applyPatch(source, patch) {
  if (await fileHash(patch.path) !== patch.sha256) throw new Error(`Patch hash mismatch: ${patch.path}`);
  // A small strict unified-diff reader avoids requiring Git or a platform patch utility.
  const lines = (await readFile(resolve(root, patch.path), 'utf8')).split('\n');
  let index = 0;
  while (index < lines.length) {
    if (!lines[index].startsWith('--- a/')) { index++; continue; }
    const name = lines[index++].slice(6);
    if (lines[index++] !== `+++ b/${name}` || name.includes('..') || name.startsWith('/')) {
      throw new Error('Invalid native patch file');
    }
    const path = resolve(root, source.sourcePath, name);
    const original = (await readFile(path, 'utf8')).split('\n');
    const output = [];
    let cursor = 0;
    while (index < lines.length && lines[index].startsWith('@@')) {
      const header = /^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@/.exec(lines[index++]);
      if (!header) throw new Error(`Invalid native patch hunk: ${name}`);
      const start = Number(header[1]) - 1;
      output.push(...original.slice(cursor, start)); cursor = start;
      while (index < lines.length && !lines[index].startsWith('@@') && !lines[index].startsWith('--- a/')) {
        const line = lines[index++];
        if (!line || line.startsWith('\\')) continue;
        if (line[0] === ' ' || line[0] === '-') {
          if (original[cursor++] !== line.slice(1)) throw new Error(`Native patch context mismatch: ${name}:${cursor}`);
        }
        if (line[0] === ' ' || line[0] === '+') output.push(line.slice(1));
      }
    }
    output.push(...original.slice(cursor)); await writeFile(path, output.join('\n'));
  }
}
export async function verifySourceInventory(source, paths) {
  const remaining = new Set(paths);
  for (const path of await files(source.sourcePath)) {
    if (!remaining.delete(path)) throw new Error(`Unrecorded upstream input: ${path}`);
  }
  if (remaining.size) throw new Error(`Missing upstream input: ${remaining.values().next().value}`);
}
export async function prepareSource(name, source) {
  const archive = resolve(root, source.archivePath);
  const directory = resolve(root, source.sourcePath);
  const recordPath = resolve(cache, `${name}-source.json`);
  const patches = policy.patches.filter(patch => patch.source === name);
  const identity = { version: source.version, commit: source.commit, sha256: source.sha256, patches };
  await download(source.url, archive, source.sha256);
  if (await exists(recordPath)) {
    const record = JSON.parse(await readFile(recordPath, 'utf8'));
    const paths = record.files.map(input => input.path);
    await verifySourceInventory(source, paths);
    if (JSON.stringify(record.identity) === JSON.stringify(identity)) {
      for (const input of record.files) {
        if (await fileHash(input.path) !== input.sha256) throw new Error(`Modified upstream input: ${input.path}`);
      }
      return paths;
    }
    // Only this tool's fixed dependency cache is replaced when locked inputs change.
    await rm(directory, { recursive: true, force: true });
  } else if (await exists(directory)) {
    // Existing source was downloaded during implementation; validate through a temporary extraction.
    // The fixed preparation directory is removed immediately after adoption.
    const temporary = `${directory}.prepare`;
    if (await exists(temporary)) throw new Error(`Unfinished native source preparation: ${pathOf(temporary)}`);
    await mkdir(temporary);
    try {
      await run('tar', ['-xzf', archive, '--strip-components=1', '-C', temporary]);
      const oldPath = source.sourcePath;
      const tempSource = { ...source, sourcePath: pathOf(temporary) };
      for (const patch of patches) await applyPatch(tempSource, patch);
      const sourceFiles = await files(pathOf(temporary));
      const paths = sourceFiles.map(file => `${oldPath}/${file.slice(pathOf(temporary).length + 1)}`);
      await verifySourceInventory(source, paths);
      for (const file of sourceFiles) {
        const target = `${oldPath}/${relative(temporary, resolve(root, file)).split(sep).join('/')}`;
        if (!await exists(resolve(root, target)) || await fileHash(target) !== await fileHash(file)) {
          throw new Error(`Existing native source does not match pinned input: ${target}`);
        }
      }
      await writeFile(recordPath, JSON.stringify({ identity, files: await hashInputs(paths) }, null, 2) + '\n');
      return paths;
    } finally { await rm(temporary, { recursive: true }); }
  }
  await mkdir(directory, { recursive: true });
  await run('tar', ['-xzf', archive, '--strip-components=1', '-C', directory]);
  for (const patch of patches) await applyPatch(source, patch);
  const paths = await files(source.sourcePath);
  await writeFile(recordPath, JSON.stringify({ identity, files: await hashInputs(paths) }, null, 2) + '\n');
  return paths;
}
export async function artifact(path) {
  const stat = await lstat(resolve(root, path));
  if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Invalid native artifact: ${path}`);
  return { path, bytes: stat.size, sha256: await fileHash(path) };
}
export async function unchanged(inputs) {
  for (const input of inputs) {
    if (await fileHash(input.path) !== input.sha256) throw new Error(`Native input changed during build: ${input.path}`);
  }
}
export async function sdkDependencyInputs() {
  const visited = new Set(), paths = [];
  const workspaceManifests = new Set(await Promise.all([
    'packages/script-host/package.json', 'packages/source-runtime/package.json',
  ].map(path => realpath(resolve(root, path)))));
  const resolveManifest = async (name, resolver) => {
    // A manifest subpath avoids Node's builtin-name shortcut (e.g. npm buffer).
    for (const searchPath of resolver.resolve.paths(`${name}/package.json`) ?? []) {
      const candidate = resolve(searchPath, name, 'package.json');
      if (await exists(candidate)) return realpath(candidate);
    }
    return null;
  };
  const visit = async manifest => {
    if (pathOf(manifest).startsWith('../')) throw new Error(`SDK dependency outside the workspace: ${manifest}`);
    const directory = dirname(manifest);
    if (visited.has(directory)) return;
    visited.add(directory);
    if (workspaceManifests.has(manifest)) paths.push(pathOf(manifest));
    else paths.push(...await files(pathOf(directory), true));
    const data = JSON.parse(await readFile(manifest, 'utf8'));
    const resolver = createRequire(manifest);
    for (const dependency of new Set([
      ...Object.keys(data.dependencies ?? {}), ...Object.keys(data.optionalDependencies ?? {}),
    ])) {
      const resolved = await resolveManifest(dependency, resolver);
      if (resolved) await visit(resolved);
      else if (!(dependency in (data.optionalDependencies ?? {}))) {
        throw new Error(`Missing SDK dependency ${dependency} from ${pathOf(manifest)}`);
      }
    }
  };
  // Preserve each workspace's actual resolution, including its nested acorn version.
  for (const manifest of workspaceManifests) await visit(manifest);
  // Build tools and explicit SDK plugin/version-check imports resolve from this script.
  for (const name of ['node-gyp', 'esbuild', 'iconv-lite', 'buffer', 'whatwg-url', 'linkedom']) {
    const manifest = await resolveManifest(name, require);
    if (!manifest) throw new Error(`Missing SDK dependency: ${name}`);
    await visit(manifest);
  }
  return [...new Set(paths)].sort();
}
async function verifyHeaders(gypPackage, archive) {
  const gypRequire = createRequire(gypPackage);
  const headerRoot = resolve(gypRequire('env-paths')('node-gyp', { suffix: '' }).cache, policy.nodeHeaders.version);
  const inputs = [], pending = [];
  await gypRequire('tar').t({
    file: archive,
    onReadEntry(entry) {
      const path = entry.path.split('/').slice(1).join('/');
      if (entry.type !== 'File' || !/\.(h|gypi)$/.test(path)) { entry.resume(); return; }
      if (path.includes('..') || !path.startsWith('include/')) throw new Error(`Invalid Node header path: ${path}`);
      const chunks = [];
      pending.push(new Promise((resolveInput, reject) => {
        entry.on('data', data => chunks.push(data));
        entry.on('error', reject);
        entry.on('end', () => {
          const expected = sha256(Buffer.concat(chunks));
          readFile(resolve(headerRoot, path)).then(data => {
            if (sha256(data) !== expected) throw new Error(`Node header cache mismatch: ${path}`);
            inputs.push({ path, sha256: expected }); resolveInput();
          }).catch(reject);
        });
      }));
    },
  });
  await Promise.all(pending);
  if (!inputs.length) throw new Error('Node headers archive was empty');
  return { root: headerRoot, inputs: inputs.sort((a, b) => a.path.localeCompare(b.path)) };
}
async function buildNative() {
  if (policy.schemaVersion !== 1 || policy.abi !== 1 || policy.napi !== 8) throw new Error('Unsupported native policy');
  if (process.versions.node !== policy.build.node) throw new Error(`Native build requires Node ${policy.build.node}`);
  const gypPackage = require.resolve('node-gyp/package.json');
  if (JSON.parse(await readFile(gypPackage, 'utf8')).version !== policy.build.nodeGyp) throw new Error('Unexpected node-gyp version');
  await mkdir(cache, { recursive: true });
  await writeFile(resolve(cache, 'build-commands.jsonl'), '');
  const sourceInputs = (await Promise.all(Object.entries(policy.sources).map(([name, source]) => prepareSource(name, source)))).flat();
  const nativeInputs = (await Promise.all(policy.inputDirectories.map(directory => files(directory)))).flat();
  const sdkDependencies = await sdkDependencyInputs();
  const sdkCandidates = [
    ...await files('packages/script-host/src'), ...await files('packages/source-runtime/src'),
    ...sdkDependencies,
  ];
  const trackedPaths = [
    'package.json', 'package-lock.json', ...policy.mandatoryInputs,
    'scripts/quickjs/build-source-sdk.mjs', ...nativeInputs, ...sourceInputs,
    ...Object.values(policy.sources).flatMap(source => [source.archivePath, source.license.path]),
    ...sdkCandidates,
  ];
  // Snapshot source/lock/tool inputs BEFORE any native compilation or SDK bundling.
  const before = await hashInputs(trackedPaths);
  const beforeMap = new Map(before.map(input => [input.path, input.sha256]));
  await unchanged(loadedBuildInputs);
  const { buildSourceSDK } = await import('./build-source-sdk.mjs');
  const nodeArchive = resolve(root, policy.nodeHeaders.archivePath);
  await download(policy.nodeHeaders.url, nodeArchive, policy.nodeHeaders.sha256);
  const headerArchiveInput = await hashInputs([policy.nodeHeaders.archivePath]);
  const gyp = resolve(dirname(gypPackage), 'bin/node-gyp.js');
  // node-gyp owns and reuses its normal header cache; --tarball pins the exact input.
  await run(process.execPath, [gyp, 'install', '--ensure', `--tarball=${nodeArchive}`]);
  const headers = await verifyHeaders(gypPackage, nodeArchive);
  headerCache = headers.root;
  const opensslSource = resolve(root, policy.sources.openssl.sourcePath);
  const opensslBuild = resolve(cache, 'openssl-build');
  await mkdir(opensslBuild, { recursive: true });
  const targets = {
    'linux-arm64': 'linux-aarch64', 'linux-x64': 'linux-x86_64',
    'darwin-arm64': 'darwin64-arm64-cc', 'darwin-x64': 'darwin64-x86_64-cc',
    'win32-x64': 'VC-WIN64A', 'win32-arm64': 'VC-WIN64-ARM',
  };
  const target = targets[`${process.platform}-${process.arch}`];
  if (!target) throw new Error(`Unsupported native build target ${process.platform}/${process.arch}`);
  const configure = [resolve(opensslSource, 'Configure'), target, 'no-shared', 'no-module', 'no-tests', 'no-apps', 'no-docs',
    'no-legacy', 'no-autoload-config', 'no-ssl', 'no-dtls'];
  if (process.platform !== 'win32') configure.push('-fPIC', '-fvisibility=hidden');
  const configFile = resolve(opensslBuild, 'meow-config.json');
  const config = JSON.stringify({ source: policy.sources.openssl.sha256, configure, cc: process.env.CC ?? '' });
  if (!await exists(configFile) || await readFile(configFile, 'utf8') !== config) {
    await run('perl', configure, opensslBuild);
    await writeFile(configFile, config);
  }
  const parallel = String(availableParallelism());
  await run(process.platform === 'win32' ? 'nmake' : 'make',
    process.platform === 'win32' ? ['build_generated', 'libcrypto.lib'] : ['-j', parallel, 'build_generated', 'libcrypto.a'], opensslBuild);
  const library = resolve(opensslBuild, process.platform === 'win32' ? 'libcrypto.lib' : 'libcrypto.a');
  const packageRoot = resolve(root, 'packages/script-host');
  // Windows node-gyp's normal cache uses <arch>/node.lib. --nodedir instead
  // means a Node source build and incorrectly selects Release/node.lib.
  const headerArguments = process.platform === 'win32' ? [] : [`--nodedir=${headers.root}`];
  await run(process.execPath, [gyp, 'configure', ...headerArguments,
    '--', `-Dopenssl_library=${library}`], packageRoot);
  await run(process.execPath, [gyp, 'build', '--jobs', parallel], packageRoot);
  const sdkResult = await buildSourceSDK();
  const sdk = await artifact(pathOf(sdkResult.path));
  if (sdk.path !== 'packages/script-host/build/Release/source-sdk.js') throw new Error('Unexpected source SDK output');
  if (sdkResult.bytes !== sdk.bytes || sdkResult.sha256 !== sdk.sha256) {
    throw new Error('Source SDK output changed after bundling');
  }
  const sdkInputs = sdkResult.inputs.map(input => input.path);
  for (const path of sdkInputs) {
    if (!beforeMap.has(path)) throw new Error(`SDK input omitted from pre-build snapshot: ${path}`);
  }
  for (const input of sdkResult.inputs) {
    if (input.sha256 !== beforeMap.get(input.path)) {
      throw new Error(`Source SDK consumed a changed input: ${input.path}`);
    }
  }
  if (JSON.stringify((await Promise.all(policy.inputDirectories.map(directory => files(directory)))).flat()) !== JSON.stringify(nativeInputs)) {
    throw new Error('Native source directory membership changed during build');
  }
  const finalSDKCandidates = [
    ...await files('packages/script-host/src'), ...await files('packages/source-runtime/src'),
    ...await sdkDependencyInputs(),
  ];
  if (JSON.stringify(finalSDKCandidates) !== JSON.stringify(sdkCandidates)) throw new Error('SDK source membership changed during build');
  await unchanged(before);
  await unchanged(headerArchiveInput);
  const finalHeaders = await verifyHeaders(gypPackage, nodeArchive);
  if (JSON.stringify(headers.inputs) !== JSON.stringify(finalHeaders.inputs)) throw new Error('Node headers changed during build');
  const paths = [
    'package.json', 'package-lock.json', ...policy.mandatoryInputs,
    'scripts/quickjs/build-source-sdk.mjs', ...nativeInputs, ...sourceInputs, ...sdkInputs,
    ...Object.values(policy.sources).flatMap(source => [source.archivePath, source.license.path]),
    policy.nodeHeaders.archivePath, ...sdkDependencies,
  ];
  const sdkHashes = new Map(sdkResult.inputs.map(input => [input.path, input.sha256]));
  const inputs = await hashInputs(paths);
  for (const input of inputs) {
    const captured = sdkHashes.get(input.path);
    if (captured !== undefined) {
      if (captured !== input.sha256) throw new Error(`SDK input changed after compilation: ${input.path}`);
      input.sha256 = captured;
    }
  }
  for (const source of Object.values(policy.sources)) {
    if (await fileHash(source.license.path) !== source.license.sha256) throw new Error(`License mismatch: ${source.license.path}`);
  }
  const receipt = {
    schemaVersion: 1, abi: 1, napi: 8, platform: process.platform, arch: process.arch,
    sources: Object.fromEntries(Object.entries(policy.sources).map(([name, { version, commit, url, sha256 }]) =>
      [name, { version, commit, url, sha256 }])),
    inputs, artifact: await artifact(policy.artifact.path), sdk,
    nodeHeaders: { ...policy.nodeHeaders },
    nodeHeaderFiles: headers.inputs,
    sourceTrees: Object.fromEntries(Object.entries(policy.sources).map(([name, source]) =>
      [name, { path: source.sourcePath, files: sourceInputs.filter(path => path.startsWith(`${source.sourcePath}/`)) }])),
    toolchain: {
      node: process.versions.node, nodeGyp: policy.build.nodeGyp, esbuild: policy.build.esbuild,
      compiler: portableArgument((await run(process.env.CC ?? (process.platform === 'win32' ? 'cl' : 'cc'),
        process.platform === 'win32' ? [] : ['--version'], root, true)).split(/\r?\n/, 1)[0]),
      perl: await run('perl', ['-e', 'print $^V'], root, true),
      commands,
    },
  };
  await unchanged(before);
  await unchanged(inputs);
  for (const source of Object.values(policy.sources)) {
    await verifySourceInventory(source, sourceInputs.filter(path => path.startsWith(source.sourcePath + '/')));
  }
  await writeFile(resolve(root, policy.artifact.manifest), JSON.stringify(receipt, null, 2) + '\n');
  console.log(`Built ${receipt.artifact.path} ${receipt.artifact.sha256}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await buildNative().catch(error => {
    console.error(error.message);
    process.exitCode = error.exitCode ?? 1;
  });
}
