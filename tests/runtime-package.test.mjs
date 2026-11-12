import test from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync,
  symlinkSync, writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const temporary = join(temporaryRoot, 'runtime-package-tests');
mkdirSync(temporary, { recursive: true });
const sourceCode = 'throw new Error("Fixture business code must never execute");\n';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));

function write(base, rel, value) {
  const path = join(base, rel);
  const delta = relative(temporary, path);
  assert.ok(delta && delta !== '..' && !delta.startsWith(`..${sep}`), 'fixture writes stay in the owned working directory');
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, typeof value === 'string' || Buffer.isBuffer(value) ? value : `${JSON.stringify(value, null, 2)}\n`);
}

function fixture({ missing = [], omitFiles = [] } = {}) {
  const area = mkdtempSync(join(temporary, 'case-'));
  const root = join(area, 'source');
  mkdirSync(root);
  const pkg = {
    name: 'runtime-packaging-fixture', version: '1.0.0', private: true, type: 'module',
    workspaces: ['packages/*'], devDependencies: { 'dev-tool': '1.0.0' },
  };
  const lock = { name: pkg.name, version: pkg.version, lockfileVersion: 3, packages: { '': { ...pkg } } };
  function file(location, content) {
    if (!omitFiles.includes(location)
      && !missing.some(parent => location === parent || location.startsWith(`${parent}/`))) write(root, location, content);
  }
  function add(location, name, version, fields = {}, workspace = false) {
    const manifest = {
      name, version, type: 'module', main: './src/index.js', exports: { '.': './src/index.js' },
      license: 'MIT', scripts: { install: 'node -e "throw new Error(\'Never execute fixture lifecycle scripts\')"' },
      ...fields,
    };
    const record = { name, version };
    for (const field of ['dependencies', 'optionalDependencies', 'peerDependencies', 'peerDependenciesMeta']) {
      if (manifest[field]) record[field] = manifest[field];
    }
    if (!workspace) {
      record.resolved = `https://registry.npmjs.org/${name}/-/${name.split('/').at(-1)}-${version}.tgz`;
      // Synthetic fixture SRI only. No real lock/tarball or registry assertion is made.
      record.integrity = `sha512-${createHash('sha512').update(`synthetic-fixture:${name}@${version}`).digest('base64')}`;
    }
    lock.packages[location] = record;
    if (!missing.includes(location)) {
      file(`${location}/package.json`, manifest);
      file(`${location}/src/index.js`, sourceCode);
      file(`${location}/data/table.json`, { kind: 'inert runtime data', name });
      file(`${location}/LICENSE`, `Fixture license: ${name}\n`);
      file(`${location}/NOTICE`, `Fixture attribution: ${name}\n`);
      file(`${location}/SOURCES.md`, `Synthetic source identity: ${name}@${version}\n`);
      file(`${location}/licenses/LICENSE.extra`, 'Keep nested license text.\n');
      file(`${location}/test/not-runtime.test.js`, sourceCode);
      file(`${location}/src/index.spec.js`, sourceCode);
      file(`${location}/fixtures/secret.json`, '{"mustNotShip":true}');
      file(`${location}/coverage/result.json`, '{}');
      file(`${location}/.cache/cached`, 'must not ship');
      file(`${location}/.env`, 'FIXTURE_ONLY=not-a-real-credential');
    }
    if (workspace) {
      const installed = `node_modules/${name}`;
      lock.packages[installed] = { resolved: location, link: true };
      if (!missing.includes(installed)) {
        mkdirSync(dirname(join(root, installed)), { recursive: true });
        symlinkSync(join(root, location), join(root, installed), process.platform === 'win32' ? 'junction' : 'dir');
      }
    }
    return record;
  }
  add('packages/runtime-bridge', '@meowfilm/runtime-bridge', '0.1.0',
    { dependencies: { shared: '^1.0.0' } }, true);
  add('packages/script-host', '@meowfilm/script-host', '0.2.0',
    { files: ['src'], dependencies: {
      '@meowfilm/source-runtime': '0.1.0',
      'fixture-lib': '^1.0.0', 'host-lib': '1.x', 'music-lib': '~1.0.0', 'fixture-dom': '26.1.0', acorn: '8.18.0',
    } }, true);
  add('packages/source-runtime', '@meowfilm/source-runtime', '0.1.0',
    { files: ['src'] }, true);
  add('packages/pan-runtime', '@meowfilm/pan-runtime', '0.1.0', { files: ['src'] }, true);
  add('packages/film-rules', '@meowfilm/film-rules', '0.1.0',
    { files: ['film-rules.js'], main: './film-rules.js', exports: './film-rules.js' }, true);
  file('packages/film-rules/film-rules.js', sourceCode);
  add('packages/film-search', '@meowfilm/film-search', '0.1.0',
    { files: ['film-search.js'], main: './film-search.js', exports: './film-search.js' }, true);
  file('packages/film-search/film-search.js', sourceCode);
  add('packages/novel-rules', '@meowfilm/novel-rules', '0.1.0',
    { files: ['novel-rules.js'], main: './novel-rules.js', exports: './novel-rules.js' }, true);
  file('packages/novel-rules/novel-rules.js', sourceCode);
  add('packages/film-playback', '@meowfilm/film-playback', '0.1.0',
    { files: ['src'], dependencies: {
      '@meowfilm/film-rules': '0.1.0', '@meowfilm/film-search': '0.1.0', '@meowfilm/script-host': '0.2.0',
    } }, true);
  add('packages/netease-token', '@meowfilm/netease-token', '0.1.0', { files: ['src'] }, true);
  add('packages/unused-core', '@meowfilm/unused-core', '0.2.0',
    { dependencies: { 'music-lib': '~1.0.0' } }, true);
  add('packages/inactive-core', '@meowfilm/inactive-core', '0.1.0',
    { dependencies: { 'fixture-dom': '26.1.0', acorn: '8.18.0' } }, true);
  add('node_modules/shared', 'shared', '1.2.3');
  add('node_modules/host-lib', 'host-lib', '1.1.0', { peerDependencies: { 'peer-lib': '^2.0.0' } });
  add('node_modules/peer-lib', 'peer-lib', '2.4.1');
  add('node_modules/fixture-lib', 'fixture-lib', '1.0.4',
    { dependencies: { shared: '^2.0.0', '@fixture/token': '^1.0.0' } });
  add('node_modules/fixture-lib/node_modules/shared', 'shared', '2.1.0');
  add('node_modules/@fixture/token', '@fixture/token', '1.0.0');
  add('node_modules/music-lib', 'music-lib', '1.0.9', { dependencies: { shared: '^1.0.0' } });
  add('packages/script-host/node_modules/fixture-dom', 'fixture-dom', '26.1.0', {
    dependencies: { 'fixture-leaf': '1.0.0' },
    peerDependencies: { canvas: '^3.0.0', 'required-fixture-peer': '>= 1.0.0 < 2.0.0' },
    peerDependenciesMeta: { canvas: { optional: true } },
  });
  add('packages/script-host/node_modules/acorn', 'acorn', '8.18.0');
  add('packages/script-host/node_modules/fixture-leaf', 'fixture-leaf', '1.0.0', { dependencies: { shared: '3' } });
  add('packages/script-host/node_modules/fixture-leaf/node_modules/shared', 'shared', '3.1.0');
  add('packages/script-host/node_modules/required-fixture-peer', 'required-fixture-peer', '1.1.0');
  add('node_modules/dev-tool', 'dev-tool', '1.0.0');
  file('node_modules/dev-tool/native.node', 'Unused developer dependency; must not be scanned/copied.');
  file('node_modules/unlocked-dev-only/package.json', { name: 'unlocked-dev-only', version: '9.0.0' });
  file('apps/runtime/bootstrap.mjs', `import '@meowfilm/runtime-bridge';\n${sourceCode}`);
  file('apps/runtime/modules/music-script.mjs', sourceCode);
  file('apps/runtime/modules/cat-script.mjs', sourceCode);
  file('apps/runtime/modules/source-runtime.mjs', sourceCode);
  file('apps/runtime/modules/pan-runtime.mjs', sourceCode);
  file('apps/runtime/modules/film-playback.mjs', `import '@meowfilm/film-playback';\n${sourceCode}`);
  file('apps/runtime/modules/netease-token.mjs', sourceCode);
  for (const path of ['entry.mjs', 'pan-entry.mjs', 'client.mjs', 'contracts.mjs', 'modules/source-candidates.mjs',
    'modules/source-library.mjs', 'modules/source-web.mjs', 'modules/source-rules.mjs',
    'modules/metadata-data.mjs', 'modules/metadata/settings.mjs', 'modules/metadata/network.mjs',
    'modules/metadata/tmdb.mjs', 'modules/metadata/douban.mjs']) {
    file(`apps/runtime/${path}`, sourceCode);
  }
  function saveLock() { write(root, 'package-lock.json', lock); }
  function update(location, changes, updateLock = true) {
    const manifest = { ...readJSON(join(root, location, 'package.json')), ...changes };
    write(root, `${location}/package.json`, manifest);
    if (updateLock) Object.assign(lock.packages[location], changes);
    saveLock();
  }
  write(root, 'package.json', pkg);
  saveLock();
  return {
    root, area, lock, pkg, add, file, saveLock, update,
    output: name => join(area, name ?? 'payload'),
    pack: (product = 'aggregate', name = 'payload') => packageRuntime({ root, product, output: join(area, name) }),
  };
}

function payloadPaths(directory, rel = '') {
  return readdirSync(join(directory, rel), { withFileTypes: true }).flatMap(item => {
    assert.equal(item.isSymbolicLink(), false);
    const path = rel ? `${rel}/${item.name}` : item.name;
    return item.isDirectory() ? payloadPaths(directory, path) : [path];
  }).sort();
}

async function fails(f, code, product = 'aggregate', name = 'payload') {
  await assert.rejects(f.pack(product, name), { code });
  const status = readJSON(join(f.output(name), 'runtime-manifest.json'));
  assert.equal(status.status, 'failed');
  assert.equal(status.error.code, code);
  assert.equal(status.product, product);
  return status;
}

test('installed registry contents do not reapply upstream npm-pack negative rules', async () => {
  const f = fixture();
  f.update('packages/script-host/node_modules/fixture-dom', { files: ['lib/**/*.js', '!__tests__'] });
  const manifest = await f.pack();
  assert.ok(manifest.files.some(file => file.path.endsWith('/fixture-dom/src/index.js')));
});

test('interpreted workspace files still reject unsupported negative rules', async () => {
  const f = fixture();
  f.update('packages/script-host', { files: ['src', '!__tests__'] });
  await fails(f, 'UNSAFE_PATH');
});

test('common runtime packaging stages the eight-module App closure with exact hashes', async () => {
  const f = fixture(), manifest = await f.pack();
  assert.equal(manifest.product, 'aggregate');
  assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease']);
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.assemblyProfile, 'common-native');
  assert.equal(manifest.entry, 'app/entry.mjs');
  assert.match(manifest.integrityScope, /not an upstream signature/);
  assert.deepEqual(manifest.files.filter(f => f.path.startsWith('app/')).map(f => f.path),
    ['app/bootstrap.mjs', 'app/client.mjs', 'app/contracts.mjs', 'app/entry.mjs',
      'app/modules/cat-script.mjs', 'app/modules/film-playback.mjs', 'app/modules/metadata-data.mjs', 'app/modules/metadata/douban.mjs',
      'app/modules/metadata/network.mjs', 'app/modules/metadata/settings.mjs', 'app/modules/metadata/tmdb.mjs',
      'app/modules/music-script.mjs',
      'app/modules/netease-token.mjs', 'app/modules/pan-runtime.mjs', 'app/modules/source-candidates.mjs', 'app/modules/source-library.mjs',
      'app/modules/source-rules.mjs', 'app/modules/source-runtime.mjs', 'app/modules/source-web.mjs',
      'app/pan-entry.mjs']);
  assert.deepEqual(manifest.dependencies.map(p => p.path).sort(), [
    'node_modules/@meowfilm/runtime-bridge', 'node_modules/@meowfilm/script-host',
    'node_modules/@meowfilm/source-runtime', 'node_modules/@meowfilm/pan-runtime',
    'node_modules/@meowfilm/film-rules', 'node_modules/@meowfilm/film-search', 'node_modules/@meowfilm/film-playback',
    'node_modules/@meowfilm/netease-token', 'node_modules/@meowfilm/novel-rules',
    'node_modules/shared', 'node_modules/host-lib', 'node_modules/peer-lib',
    'node_modules/fixture-lib', 'node_modules/fixture-lib/node_modules/shared', 'node_modules/@fixture/token',
    'node_modules/music-lib', 'node_modules/@meowfilm/script-host/node_modules/fixture-dom',
    'node_modules/@meowfilm/script-host/node_modules/acorn',
    'node_modules/@meowfilm/script-host/node_modules/fixture-leaf',
    'node_modules/@meowfilm/script-host/node_modules/fixture-leaf/node_modules/shared',
    'node_modules/@meowfilm/script-host/node_modules/required-fixture-peer',
  ].sort());
  assert.ok(!manifest.files.some(f => /(?:\/test\/|\/fixtures\/|\.env$|\.spec\.js$|dev-tool|unlocked-dev)/.test(f.path)));
  assert.deepEqual(manifest.omittedOptionalDependencies, [{
    from: 'node_modules/@meowfilm/script-host/node_modules/fixture-dom', name: 'canvas', kind: 'peer',
    range: '^3.0.0', reason: 'not-installed',
  }]);
  assert.deepEqual(payloadPaths(f.output()).filter(p => p !== 'runtime-manifest.json'), manifest.files.map(f => f.path));
  for (const file of manifest.files) {
    const bytes = readFileSync(join(f.output(), file.path));
    assert.equal(file.bytes, bytes.length); assert.equal(file.sha256, sha256(bytes));
  }
});

for (const [workspace, file] of [
  ['film-rules', 'film-rules.js'], ['film-search', 'film-search.js'], ['film-playback', 'src/index.js'],
  ['novel-rules', 'novel-rules.js'],
]) {
  test(`the App closure requires the ${workspace} workspace and its public file`, async () => {
    const absent = fixture({ missing: [`packages/${workspace}`] });
    await fails(absent, 'MISSING_SOURCE');
    const missingFile = fixture({ omitFiles: [`packages/${workspace}/${file}`] });
    await fails(missingFile, 'MISSING_PUBLIC_FILE');
  });
}

test('hashes and manifest bytes are reproducible across absolute output directories', async () => {
  const f = fixture();
  await f.pack('aggregate', 'first');
  await f.pack('aggregate', 'second');
  assert.deepEqual(readFileSync(join(f.output('first'), 'runtime-manifest.json')),
    readFileSync(join(f.output('second'), 'runtime-manifest.json')));
  write(f.root, 'packages/script-host/src/index.js', `${sourceCode}// changed input\n`);
  await f.pack('aggregate', 'changed');
  const first = readJSON(join(f.output('first'), 'runtime-manifest.json'));
  const changed = readJSON(join(f.output('changed'), 'runtime-manifest.json'));
  assert.deepEqual(first.files.filter((file, i) => file.sha256 !== changed.files[i].sha256).map(x => x.path),
    ['node_modules/@meowfilm/script-host/src/index.js']);
});

test('package LICENSE, NOTICE, nested license and source attribution survive workspace files pruning', async () => {
  const f = fixture();
  const manifest = await f.pack('aggregate');
  for (const node of manifest.dependencies) {
    for (const file of ['LICENSE', 'NOTICE', 'SOURCES.md', 'licenses/LICENSE.extra']) {
      assert.deepEqual(readFileSync(join(f.output(), node.path, file)), readFileSync(join(f.root, node.lockPath, file)));
    }
  }
  assert.equal(existsSync(join(f.output(), 'node_modules/@meowfilm/script-host/data/table.json')), false);
  assert.equal(existsSync(join(f.output(), 'node_modules/fixture-lib/data/table.json')), true);
});

test('extensionless Node main entries resolve locally without loading package code', async () => {
  const f = fixture();
  f.update('node_modules/shared', { main: './src/index' });
  f.update('node_modules/fixture-lib', { main: './src' });
  const manifest = await f.pack('aggregate');
  for (const name of ['shared', 'fixture-lib']) {
    assert.ok(manifest.files.some(file => file.path === `node_modules/${name}/src/index.js`));
  }
});

test('an explicitly disabled Node main does not invalidate exported package files', async () => {
  const f = fixture();
  f.update('node_modules/shared', { main: false });
  const manifest = await f.pack('aggregate');
  assert.ok(manifest.dependencies.some(item => item.name === 'shared'));
});

test('large payload files are streamed and their byte lengths and hashes are exact', async () => {
  const f = fixture();
  const payload = Buffer.from(`export const text = ${JSON.stringify('猫'.repeat(60_000))};\n`);
  f.file('packages/script-host/src/large.js', payload);
  const manifest = await f.pack('aggregate');
  const file = manifest.files.find(item => item.path === 'node_modules/@meowfilm/script-host/src/large.js');
  assert.equal(file.bytes, payload.length);
  assert.equal(file.sha256, sha256(payload));
  assert.deepEqual(readFileSync(join(f.output(), file.path)), payload);
});

for (const [label, location, code] of [
  ['required registry dependency', 'node_modules/fixture-lib', 'MISSING_DEPENDENCY'],
  ['required peer', 'node_modules/peer-lib', 'MISSING_DEPENDENCY'],
  ['nested required peer', 'packages/script-host/node_modules/required-fixture-peer', 'MISSING_DEPENDENCY'],
  ['installed workspace link', 'node_modules/@meowfilm/script-host', 'MISSING_WORKSPACE_LINK'],
  ['source workspace link', 'node_modules/@meowfilm/source-runtime', 'MISSING_WORKSPACE_LINK'],
]) {
  test(`rejects missing ${label}`, async () => {
    await fails(fixture({ missing: [location] }), code);
  });
}

test('does not silently fall back to a hoisted package when the nearest locked package is absent', async () => {
  const f = fixture({ missing: ['node_modules/fixture-lib/node_modules/shared'] });
  await fails(f, 'MISSING_DEPENDENCY', 'aggregate');
});

test('missing required bootstrap leaves an explicit failed payload', async () => {
  const f = fixture({ omitFiles: ['apps/runtime/bootstrap.mjs'] });
  await fails(f, 'MISSING_SOURCE');
  assert.equal(readJSON(join(f.output(), 'runtime-manifest.json')).status, 'failed');
});

for (const [module, file] of [
  ['music', 'music-script.mjs'], ['cat', 'cat-script.mjs'], ['source', 'source-runtime.mjs'],
  ['source library', 'source-library.mjs'], ['source Web broker', 'source-web.mjs'],
  ['source rules', 'source-rules.mjs'],
]) {
  test(`missing shared ${module} module cannot leave a complete runtime`, async () => {
    await fails(fixture({ omitFiles: [`apps/runtime/modules/${file}`] }), 'MISSING_SOURCE');
  });
}

test('unlisted shared helpers are rejected rather than silently omitted', async () => {
  const f = fixture();
  f.file('apps/runtime/unlisted-helper.mjs', sourceCode);
  await fails(f, 'SHARED_CLOSURE');
});

test('shared runtime packaging accepts ordinary method names named import', async () => {
  const f = fixture();
  f.file('apps/runtime/modules/cat-script.mjs', `${sourceCode}\nconst methods = { 'import': () => null };\nmethods.import('installationId');\n`);
  assert.equal((await f.pack('aggregate')).status, 'complete');
});

for (const specifier of ['../../windows/runtime/bootstrap.mjs', 'node:net', '@meowfilm/script-host/src/host.js']) {
  test(`shared runtime packaging cannot bypass its platform/public closure: ${specifier}`, async () => {
    const f = fixture();
    f.file('apps/runtime/modules/music-script.mjs', `import '${specifier}';\n${sourceCode}`);
    await fails(f, 'SHARED_IMPORT');
  });
}

for (const statement of ["import'node:net';", "import('node:net');", "require('node:net');"]) {
  test(`shared runtime packaging rejects compact module syntax: ${statement}`, async () => {
    const f = fixture();
    f.file('apps/runtime/modules/music-script.mjs', `${statement}\n${sourceCode}`);
    await fails(f, 'SHARED_IMPORT');
  });
}

for (const location of ['packages/script-host', 'node_modules/fixture-lib']) {
  test(`rejects installed/lock version mismatch: ${location}`, async () => {
    const f = fixture();
    f.update(location, { version: '9.9.9' }, false);
    await fails(f, 'VERSION_MISMATCH', 'aggregate');
  });
}

test('rejects an installed dependency not present in root lock', async () => {
  const f = fixture();
  delete f.lock.packages['node_modules/fixture-lib'];
  f.saveLock();
  await fails(f, 'UNLOCKED_PACKAGE', 'aggregate');
});

test('rejects an unlocked nearer dependency instead of using the locked ancestor', async () => {
  const f = fixture();
  f.file('packages/script-host/node_modules/fixture-lib/package.json', { name: 'fixture-lib', version: '1.0.4' });
  await fails(f, 'UNLOCKED_PACKAGE', 'aggregate');
});

test('rejects changed production dependency metadata', async () => {
  const f = fixture();
  f.update('node_modules/fixture-lib', { dependencies: { shared: '^1.0.0' } }, false);
  await fails(f, 'DEPENDENCIES_MISMATCH', 'aggregate');
});

test('rejects peer metadata drift that would hide a required peer', async () => {
  const f = fixture();
  f.update('node_modules/host-lib', { peerDependenciesMeta: { 'peer-lib': { optional: true } } }, false);
  await fails(f, 'PEER_METADATA_MISMATCH', 'aggregate');
});

test('rejects a required peer whose locked installed version violates its range', async () => {
  const f = fixture();
  f.update('node_modules/peer-lib', { version: '3.0.0' });
  await fails(f, 'RANGE_MISMATCH', 'aggregate');
});

test('optional peer is included when installed and locked', async () => {
  const f = fixture();
  f.add('packages/script-host/node_modules/canvas', 'canvas', '3.1.0');
  f.saveLock();
  const manifest = await f.pack('aggregate');
  assert.deepEqual(manifest.omittedOptionalDependencies, []);
  assert.ok(manifest.dependencies.some(x => x.name === 'canvas'));
});

test('optional peer cannot bypass lock validation when present', async () => {
  const f = fixture();
  f.file('packages/script-host/node_modules/canvas/package.json', { name: 'canvas', version: '3.1.0' });
  await fails(f, 'UNLOCKED_PACKAGE', 'aggregate');
});

test('an installed optional peer still must satisfy its declared range', async () => {
  const f = fixture();
  f.add('packages/script-host/node_modules/canvas', 'canvas', '4.0.0');
  f.saveLock();
  await fails(f, 'RANGE_MISMATCH', 'aggregate');
});

test('absent locked optional peer is explicitly omitted, not installed or fabricated', async () => {
  const f = fixture({ missing: ['packages/script-host/node_modules/canvas'] });
  f.add('packages/script-host/node_modules/canvas', 'canvas', '3.1.0');
  f.saveLock();
  const manifest = await f.pack('aggregate');
  assert.equal(manifest.omittedOptionalDependencies[0].name, 'canvas');
  assert.equal(manifest.dependencies.some(x => x.name === 'canvas'), false);
});

test('optional dependencies override duplicate normal dependencies and record absence', async () => {
  const f = fixture();
  f.update('node_modules/host-lib', {
    dependencies: { 'optional-lib': '^1.0.0' }, optionalDependencies: { 'optional-lib': '^2.0.0' },
  });
  delete f.lock.packages['node_modules/host-lib'].dependencies['optional-lib'];
  f.saveLock();
  const manifest = await f.pack('aggregate');
  assert.deepEqual(manifest.omittedOptionalDependencies.filter(p => p.name === 'optional-lib'), [{
    from: 'node_modules/host-lib', name: 'optional-lib', kind: 'optionalDependency',
    range: '^2.0.0', reason: 'not-installed',
  }]);
});

test('dependency cycles are copied once without executing the modules', async () => {
  const f = fixture();
  f.update('node_modules/shared', { dependencies: { 'music-lib': '^1.0.0' } });
  const manifest = await f.pack('aggregate');
  assert.equal(manifest.dependencies.filter(x => x.path === 'node_modules/shared').length, 1);
  assert.equal(manifest.dependencies.filter(x => x.name === 'shared').length, 3);
  assert.equal(new Set(manifest.dependencies.map(x => x.path)).size, manifest.dependencies.length);
  assert.equal(manifest.dependencies.filter(x => x.name === 'music-lib').length, 1);
});

for (const [range, version, passes] of [
  ['1.2.3', '1.2.3', true], ['^1.0.0', '2.0.0', false], ['~1.2.0', '1.3.0', false],
  ['1.x', '1.9.0', true], ['>= 1.0.0 < 2.0.0', '1.5.0', true],
  ['^0.2.0', '0.3.0', false], ['^0.0.3', '0.0.4', false],
  ['1.0 - 1.3', '1.3.9', true], ['>=v1.0.0 || ^3.0.0', '3.1.0', true],
  ['^1.2.3-beta.1', '1.2.3-beta.2', true], ['^1.2.3', '1.3.0-beta.1', false],
  ['>=1.2.3-0', '1.2.3-0', true], ['^1.2.3-beta.1', '1.3.0-beta.1', false],
]) {
  test(`semver ${range} / ${version}: ${passes ? 'accept' : 'reject'}`, async () => {
    const f = fixture();
    f.update('packages/runtime-bridge', { dependencies: { shared: range } });
    f.update('node_modules/music-lib', { dependencies: { shared: range } });
    f.update('node_modules/shared', { version });
    if (passes) await f.pack('aggregate');
    else await fails(f, 'RANGE_MISMATCH', 'aggregate');
  });
}

for (const range of ['latest', 'file:../shared', 'git+https://example.invalid/shared.git', 'npm:other@1']) {
  test(`rejects unsupported dependency source/range ${range}`, async () => {
    const f = fixture();
    f.update('packages/runtime-bridge', { dependencies: { shared: range } });
    await fails(f, 'UNSUPPORTED_RANGE', 'aggregate');
  });
}

for (const [label, changes, code] of [
  ['absent resolved', { resolved: undefined }, 'INVALID_REGISTRY'],
  ['HTTP registry', { resolved: 'http://registry.npmjs.org/shared/-/shared-1.2.3.tgz' }, 'INVALID_REGISTRY'],
  ['foreign registry', { resolved: 'https://evil.invalid/shared.tgz' }, 'INVALID_REGISTRY'],
  ['registry credentials', { resolved: 'https://user:pass@registry.npmjs.org/shared.tgz' }, 'INVALID_REGISTRY'],
  ['registry query', { resolved: 'https://registry.npmjs.org/shared.tgz?token=fixture' }, 'INVALID_REGISTRY'],
  ['absent integrity', { integrity: undefined }, 'MISSING_INTEGRITY'],
  ['malformed integrity', { integrity: 'sha512-not-a-digest' }, 'INVALID_INTEGRITY'],
  ['wrong digest length', { integrity: 'sha512-YWJj' }, 'INVALID_INTEGRITY'],
  ['unsupported digest', { integrity: 'md5-YWJj' }, 'INVALID_INTEGRITY'],
]) {
  test(`rejects ${label} without repairing root lock`, async () => {
    const f = fixture();
    Object.assign(f.lock.packages['node_modules/shared'], changes);
    f.saveLock();
    const before = readFileSync(join(f.root, 'package-lock.json'));
    await fails(f, code, 'aggregate');
    assert.deepEqual(readFileSync(join(f.root, 'package-lock.json')), before);
  });
}

test('existing output, including empty directories, is never adopted or overwritten', async () => {
  const f = fixture();
  mkdirSync(f.output('empty'));
  await assert.rejects(f.pack('aggregate', 'empty'), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(readdirSync(f.output('empty')), []);
  write(f.area, 'old/keep.txt', 'user-owned');
  await assert.rejects(f.pack('aggregate', 'old'), { code: 'OUTPUT_EXISTS' });
  assert.equal(readFileSync(join(f.output('old'), 'keep.txt'), 'utf8'), 'user-owned');
  assert.deepEqual(readdirSync(f.output('old')), ['keep.txt']);
  write(f.area, 'a-file', 'not a directory');
  await assert.rejects(f.pack('aggregate', 'a-file'), { code: 'OUTPUT_EXISTS' });
});

test('relative, traversing, source-overlapping, and missing-parent outputs are rejected before writes', async () => {
  const f = fixture();
  for (const [output, code] of [
    ['relative-output', 'INVALID_OUTPUT'],
    [`${f.area}${sep}child${sep}..${sep}escape`, 'UNSAFE_PATH'],
    [join(f.root, 'packages', 'new-output'), 'OUTPUT_OVERLAP'],
    [join(f.root, 'apps', 'new-output'), 'OUTPUT_OVERLAP'],
    [join(f.area, 'missing-parent', 'payload'), 'UNSAFE_OUTPUT_PARENT'],
  ]) {
    await assert.rejects(packageRuntime({ root: f.root, product: 'aggregate', output }), { code });
  }
  assert.equal(existsSync(join(f.area, 'missing-parent')), false);
  assert.equal(existsSync(join(f.root, 'packages', 'new-output')), false);
});

test('output symlink parent and existing output junction are not followed', async () => {
  const f = fixture();
  const outside = join(f.area, 'outside');
  mkdirSync(outside);
  symlinkSync(outside, join(f.area, 'alias'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(packageRuntime({ root: f.root, product: 'aggregate', output: join(f.area, 'alias', 'payload') }),
    { code: 'UNSAFE_OUTPUT_PARENT' });
  await assert.rejects(f.pack('aggregate', 'alias'), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(readdirSync(outside), []);
});

for (const bad of ['../outside', 'node_modules/../../outside', '/absolute', 'node_modules\\bad', 'C:/bad']) {
  test(`rejects unsafe lock path ${bad}`, async () => {
    const f = fixture();
    f.lock.packages[bad] = { version: '1.0.0' };
    f.saveLock();
    await fails(f, 'UNSAFE_PATH', 'aggregate');
  });
}

test('rejects dependency name traversal', async () => {
  const f = fixture();
  f.update('packages/script-host', { dependencies: { '../escape': '1.0.0' } });
  await fails(f, 'INVALID_PACKAGE_NAME', 'aggregate');
});

test('rejects unsafe public export targets', async () => {
  const f = fixture();
  f.update('packages/script-host', { exports: { '.': '../other/src/index.js' } });
  await fails(f, 'UNSAFE_PATH', 'aggregate');
});

test('rejects workspace link escaping the fixture root, without reading its package', async () => {
  const f = fixture({ missing: ['node_modules/@meowfilm/script-host'] });
  const outside = join(f.area, 'outside');
  mkdirSync(outside);
  write(f.area, 'outside/keep.txt', 'leave alone');
  symlinkSync(outside, join(f.root, 'node_modules/@meowfilm/script-host'), process.platform === 'win32' ? 'junction' : 'dir');
  await fails(f, 'UNSAFE_SYMLINK', 'aggregate');
  assert.deepEqual(readdirSync(outside), ['keep.txt']);
});

test('rejects external symlink in a package payload and leaves partial output intact', async () => {
  const f = fixture();
  const outside = join(f.area, 'outside');
  mkdirSync(outside);
  write(f.area, 'outside/secret.txt', 'inert fixture, not a real secret');
  symlinkSync(outside, join(f.root, 'packages/script-host/z-link'), process.platform === 'win32' ? 'junction' : 'dir');
  const failed = await fails(f, 'UNSAFE_SYMLINK', 'aggregate');
  assert.ok(failed.files.some(x => x.path === 'app/bootstrap.mjs'));
  assert.equal(existsSync(join(f.output(), 'node_modules/@meowfilm/script-host/z-link/secret.txt')), false);
  assert.equal(readFileSync(join(outside, 'secret.txt'), 'utf8'), 'inert fixture, not a real secret');
});

test('rejects dependency directory symlink even if its manifest would match the lock', async () => {
  const f = fixture({ missing: ['node_modules/fixture-lib'] });
  const outside = join(f.area, 'outside');
  mkdirSync(outside);
  symlinkSync(outside, join(f.root, 'node_modules/fixture-lib'), process.platform === 'win32' ? 'junction' : 'dir');
  await fails(f, 'UNSAFE_SYMLINK', 'aggregate');
});

for (const extension of ['node', 'NODE', 'dylib', 'wasm']) {
  test(`rejects native/non-JS .${extension} payload, retaining failed output`, async () => {
    const f = fixture();
    f.file(`packages/script-host/z-native.${extension}`, 'fixture native file, never executed');
    const failed = await fails(f, 'NATIVE_PAYLOAD', 'aggregate');
    assert.ok(failed.files.length > 3);
    assert.equal(existsSync(join(f.root, `packages/script-host/z-native.${extension}`)), true);
    assert.equal(existsSync(join(f.output(), `node_modules/@meowfilm/script-host/z-native.${extension}`)), false);
  });
}

test('the native artifact path alone cannot grant a workspace binary exception', async () => {
  const f = fixture(), path = 'packages/script-host/build/Release/script_host_native.node';
  f.file(path, Buffer.from('unreviewed artifact'));
  await fails(f, 'NATIVE_PAYLOAD');
  assert.equal(existsSync(join(f.output(), 'node_modules/@meowfilm/script-host/build/Release/script_host_native.node')), false);
});

test('an installed optional native peer is rejected, not silently treated as absent', async () => {
  const f = fixture();
  f.add('packages/script-host/node_modules/canvas', 'canvas', '3.1.0');
  f.file('packages/script-host/node_modules/canvas/binding.node', 'fixture binary');
  f.saveLock();
  await fails(f, 'NATIVE_PAYLOAD', 'aggregate');
});

test('missing advertised public files fail instead of producing a superficially complete package', async () => {
  const f = fixture({ omitFiles: ['packages/script-host/src/index.js'] });
  await fails(f, 'MISSING_PUBLIC_FILE', 'aggregate');
});

test('the App cannot pull in an unselected workspace through dependency metadata', async () => {
  const f = fixture();
  f.update('packages/script-host', { dependencies: { '@meowfilm/inactive-core': '*' } });
  await fails(f, 'PRODUCT_CLOSURE', 'aggregate');
});

test('jsdom cannot silently enter the App script payload', async () => {
  const f = fixture();
  f.add('node_modules/jsdom', 'jsdom', '26.1.0');
  f.update('packages/script-host', { dependencies: { jsdom: '26.1.0' } });
  await fails(f, 'PRODUCT_CLOSURE', 'aggregate');
});

test('only the token owner may package the exact Node 18 compatible jsdom', async () => {
  const accepted = fixture();
  accepted.add('node_modules/jsdom', 'jsdom', '24.1.3');
  accepted.update('packages/netease-token', { dependencies: { jsdom: '24.1.3' } });
  const manifest = await accepted.pack();
  assert.ok(manifest.dependencies.some(value => value.name === 'jsdom' && value.version === '24.1.3'));
  assert.ok(manifest.files.some(value => value.path === 'node_modules/jsdom/LICENSE'));
  for (const [owner, version] of [['script-host', '24.1.3'], ['netease-token', '26.1.0']]) {
    const denied = fixture();
    denied.add('node_modules/jsdom', 'jsdom', version);
    denied.update(`packages/${owner}`, { dependencies: { jsdom: version } });
    await fails(denied, 'PRODUCT_CLOSURE', 'aggregate');
  }
});

test('unselected package contents never enter the App payload', async () => {
  const f = fixture();
  f.file('packages/inactive-core/src/extra.node', 'never ship');
  const manifest = await f.pack();
  assert.ok(!manifest.dependencies.some(p => p.name === '@meowfilm/inactive-core'));
});

test('root manifest/lock drift and nested lock overrides are rejected', async () => {
  const f = fixture();
  write(f.root, 'package.json', { ...f.pkg, version: '2.0.0' });
  await fails(f, 'ROOT_MISMATCH', 'aggregate', 'bad-root');
  write(f.root, 'package.json', f.pkg);
  f.file('packages/script-host/package-lock.json', {});
  await fails(f, 'LOCK_CONFLICT', 'aggregate', 'nested-lock');
});

test('CLI uses its own repository root and never imports fixture business code', () => {
  const f = fixture();
  write(f.root, 'scripts/package-runtime.mjs', readFileSync(join(repository, 'scripts/package-runtime.mjs')));
  write(f.root, 'scripts/quickjs/ios-target.mjs', readFileSync(join(repository, 'scripts/quickjs/ios-target.mjs')));
  const output = f.output('cli');
  const run = spawnSync(process.execPath, [join(f.root, 'scripts/package-runtime.mjs'),
    '--product', 'aggregate', '--output', output], { cwd: f.area, encoding: 'utf8', timeout: 20_000 });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(JSON.parse(run.stdout).status, 'complete');
  assert.equal(readJSON(join(output, 'runtime-manifest.json')).product, 'aggregate');
  const invalid = spawnSync(process.execPath, [join(f.root, 'scripts/package-runtime.mjs'),
    '--product', 'aggregate', '--product', 'aggregate', '--output', f.output('not-created')],
  { encoding: 'utf8', timeout: 20_000 });
  assert.equal(invalid.status, 1);
  assert.match(invalid.stderr, /CLI_USAGE/);
  assert.equal(existsSync(f.output('not-created')), false);
});

test('invalid product is rejected without creating output', async () => {
  const f = fixture();
  await assert.rejects(f.pack('../reader'), { code: 'INVALID_PRODUCT' });
  await assert.rejects(f.pack('__proto__'), { code: 'INVALID_PRODUCT' });
  assert.equal(existsSync(f.output()), false);
});

test('ES module import guard does not create output or run the CLI', () => {
  const result = spawnSync(process.execPath, ['--input-type=module', '-e',
    `import { packageRuntime } from ${JSON.stringify(new URL('../scripts/package-runtime.mjs', import.meta.url).href)}; console.log(typeof packageRuntime);`,
  ], { cwd: resolve(repository), encoding: 'utf8', timeout: 20_000 });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'function');
  assert.equal(result.stderr, '');
});
