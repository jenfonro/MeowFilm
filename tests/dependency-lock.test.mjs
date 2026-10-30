import test from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, unlinkSync, writeFileSync,
} from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { verifyDependencyLock } from '../scripts/verify-dependency-lock.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const checker = join(root, 'scripts/verify-dependency-lock.mjs');
const read = path => JSON.parse(readFileSync(path, 'utf8'));
// No production registry dependency is needed by the remaining workspaces.
// Keep non-vacuous integrity/nested/alias tests using an inert, synthetic lock.
// These are format fixtures, not downloaded artifacts or claimed npm digests.
const hostPath = 'packages/fixture-host';
const hostLink = 'node_modules/@fixture/host';
const acornPath = 'node_modules/acorn';
const manifest = { name: 'dependency-lock-fixture', version: '1.0.0', private: true, workspaces: ['packages/*'] };
const workspaceManifests = {
  [hostPath]: { name: '@fixture/host', version: '1.0.0', dependencies: { acorn: '8.18.0', 'fixture-dom': '1.0.0' } },
  'packages/fixture-core': { name: '@fixture/core', version: '1.0.0' },
};
const workspacePaths = Object.keys(workspaceManifests);
const registryRecord = (name, version = '1.0.0') => ({
  version, resolved: `https://registry.npmjs.org/${name}/-/${name.split('/').at(-1)}-${version}.tgz`,
  integrity: 'sha512-' + createHash('sha512').update(`synthetic:${name}:${version}`).digest('base64'),
});
const original = {
  name: manifest.name, version: manifest.version, lockfileVersion: 3,
  packages: {
    '': structuredClone(manifest), ...structuredClone(workspaceManifests),
    [hostLink]: { link: true, resolved: hostPath },
    'node_modules/@fixture/core': { link: true, resolved: 'packages/fixture-core' },
    [acornPath]: registryRecord('acorn', '8.18.0'),
    [`${hostPath}/node_modules/fixture-dom`]: registryRecord('fixture-dom'),
    [`${hostPath}/node_modules/fixture-dom/node_modules/@fixture/scoped`]: registryRecord('@fixture/scoped'),
  },
};

// Fixtures contain source manifests and a lock, never installed modules.
// Each fault is written to disk and passed through the same offline reader as the CLI.
function fixture(t, mutate = () => {}, input = {
  lock: original, manifest, workspaces: workspaceManifests,
}) {
  const local = join(temporaryRoot, 'lock-audits');
  mkdirSync(local, { recursive: true });
  const directory = mkdtempSync(join(local, 'lock-audit-test-'));
  t.after(() => {
    const target = realpathSync(directory);
    assert.equal(dirname(target), realpathSync(local));
    assert.ok(basename(target).startsWith('lock-audit-test-'));
    rmSync(target, { recursive: true, force: true });
  });
  const data = structuredClone(input);
  mutate(data);
  const write = (path, value) => {
    const file = join(directory, path);
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
  };
  write('package.json', data.manifest);
  write('package-lock.json', data.lock);
  for (const [path, workspace] of Object.entries(data.workspaces)) write(`${path}/package.json`, workspace);
  return { directory, data, write };
}

test('the repository lock can be audited offline without any installed modules', t => {
  const lock = read(join(root, 'package-lock.json'));
  const workspaces = Object.fromEntries(Object.keys(lock.packages)
    .filter(path => path && !path.includes('node_modules/'))
    .map(path => [path, read(join(root, path, 'package.json'))]));
  const project = fixture(t, () => {}, { lock, manifest: read(join(root, 'package.json')), workspaces });
  const report = verifyDependencyLock(project.directory);
  assert.deepEqual(report, verifyDependencyLock(root));
  assert.deepEqual(Object.keys(workspaces).sort(), ['packages/runtime-bridge', 'packages/script-host']);
  assert.equal(report.registry.length, Object.values(lock.packages)
    .filter(entry => !entry.link && typeof entry.resolved === 'string' && entry.resolved.startsWith('https://registry.npmjs.org/')).length);
  assert.equal(report.workspaceLinks, 2);
  assert.equal(report.bundled.length, 0);
  assert.ok(!existsSync(join(project.directory, 'node_modules')));
});

test('synthetic security fixture covers registry, scoped and nested installations without npm', t => {
  const project = fixture(t);
  const report = verifyDependencyLock(project.directory);
  assert.equal(report.registry.length, 3);
  assert.equal(report.workspaceLinks, workspacePaths.length);
  assert.ok(report.registry.some(entry => entry.name === '@fixture/scoped'));
  assert.ok(report.registry.some(entry => entry.location.includes('/node_modules/fixture-dom/node_modules/')));
  assert.ok(!existsSync(join(project.directory, 'node_modules')));
  for (const entry of report.registry) {
    assert.equal(entry.version, original.packages[entry.location].version);
    assert.equal(entry.resolved, original.packages[entry.location].resolved);
    assert.equal(entry.integrity, original.packages[entry.location].integrity);
  }
});

for (const field of ['resolved', 'integrity']) {
  test(`every synthetic registry record requires ${field}, including nested installations`, t => {
    const project = fixture(t);
    let checked = 0;
    for (const entry of verifyDependencyLock(project.directory).registry) {
      const broken = structuredClone(original);
      delete broken.packages[entry.location][field];
      project.write('package-lock.json', broken);
      assert.throws(() => verifyDependencyLock(project.directory),
        error => error.message.includes(entry.location) && error.message.includes(field));
      checked++;
    }
    assert.equal(checked, 3);
  });
}

test('the historical lock missing all dist fields is rejected rather than silently repaired', t => {
  const project = fixture(t, ({ lock }) => {
    for (const [path, entry] of Object.entries(lock.packages)) {
      if (!path.includes('node_modules/') || entry.link) continue;
      delete entry.resolved;
      delete entry.integrity;
    }
  });
  const before = readFileSync(join(project.directory, 'package-lock.json'));
  assert.throws(() => verifyDependencyLock(project.directory), /resolved/);
  assert.deepEqual(readFileSync(join(project.directory, 'package-lock.json')), before);
});

for (const [label, resolved] of [
  ['mirror', 'https://registry.npmmirror.com/acorn/-/acorn-8.18.0.tgz'],
  ['suffix spoof', 'https://registry.npmjs.org.invalid/acorn.tgz'],
  ['userinfo spoof', 'https://registry.npmjs.org@invalid.example/acorn.tgz'],
  ['HTTP', 'http://registry.npmjs.org/acorn/-/acorn-8.18.0.tgz'],
  ['credentials', 'https://fixture:fixture@registry.npmjs.org/acorn.tgz'],
  ['nonstandard port', 'https://registry.npmjs.org:444/acorn.tgz'],
  ['relative tarball', 'acorn/-/acorn-8.18.0.tgz'],
  ['local tarball', 'file:acorn-8.18.0.tgz'],
  ['git source', 'git+https://registry.npmjs.org/acorn.git#deadbeef'],
  ['backslash URL', 'https://registry.npmjs.org\\@invalid.example/acorn.tgz'],
  ['query', 'https://registry.npmjs.org/acorn.tgz?fixture=true'],
  ['non-string', { origin: 'https://registry.npmjs.org' }],
]) {
  test(`rejects ${label} resolved source`, t => {
    const project = fixture(t, ({ lock }) => { lock.packages[acornPath].resolved = resolved; });
    assert.throws(() => verifyDependencyLock(project.directory), /acorn: resolved/);
  });
}

for (const integrity of ['', ' ', null, 123, 'sha512-', 'sha512-not-a-digest',
  'sha512-YQ==', 'sha1-YQ==', 'md5-YQ==', `${original.packages[acornPath].integrity} garbage`]) {
  test(`rejects unusable integrity ${JSON.stringify(integrity)}`, t => {
    const project = fixture(t, ({ lock }) => { lock.packages[acornPath].integrity = integrity; });
    assert.throws(() => verifyDependencyLock(project.directory), /acorn: .*integrity/);
  });
}

const acornSHA1 = 'sha1-' + createHash('sha1').update('synthetic:acorn:8.18.0').digest('base64');
for (const [label, transform] of [
  ['legacy SHA-1', () => acornSHA1],
  ['multiple SRI hashes', sri => `${acornSHA1} ${sri}`],
  ['SRI options', sri => `${sri}?fixture`],
  ['unpadded Base64', sri => sri.replace(/=+$/, '')],
]) {
  test(`preserves supported ${label} integrity syntax`, t => {
    const project = fixture(t, ({ lock }) => {
      lock.packages[acornPath].integrity = transform(lock.packages[acornPath].integrity);
    });
    assert.doesNotThrow(() => verifyDependencyLock(project.directory));
  });
}

for (const version of [undefined, '', 'latest', '^8.18.0', 'npm:acorn@8.18.0', '8.18']) {
  test(`rejects non-exact registry version ${String(version)}`, t => {
    const project = fixture(t, ({ lock }) => { lock.packages[acornPath].version = version; });
    assert.throws(() => verifyDependencyLock(project.directory), /acorn: .*version/);
  });
}

for (const target of ['packages/missing', '../outside', '/absolute', 'C:/outside',
  'packages\\fixture-host', 'packages/fixture-core']) {
  test(`rejects dangling or redirected workspace link ${target}`, t => {
    const project = fixture(t, ({ lock }) => { lock.packages[hostLink].resolved = target; });
    assert.throws(() => verifyDependencyLock(project.directory), /workspace link/);
  });
}

for (const [label, mutate, error] of [
  ['missing workspace link', ({ lock }) => { delete lock.packages[hostLink]; }, /workspace link/],
  ['missing workspace record', ({ lock }) => { delete lock.packages[hostPath]; }, /missing package record/],
  ['link masquerading as workspace record', ({ lock }) => { lock.packages[hostPath].link = true; }, /missing package record/],
  ['workspace identity mismatch', ({ lock }) => { lock.packages[hostPath].name = '@fixture/not-host'; }, /name mismatch/],
  ['workspace version mismatch', ({ lock }) => { lock.packages[hostPath].version = '999.0.0'; }, /version mismatch/],
  ['extra mismatched link', ({ lock }) => { lock.packages['node_modules/orphan'] = { link: true, resolved: hostPath }; }, /mismatched workspace link/],
  ['string-valued link', ({ lock }) => { lock.packages[acornPath].link = 'true'; }, /link must be boolean/],
  ['undeclared workspace target', ({ lock, workspaces }) => {
    workspaces['tools/ghost'] = { name: 'ghost', version: '1.0.0' };
    lock.packages['node_modules/ghost'] = { link: true, resolved: 'tools/ghost' };
    lock.packages['tools/ghost'] = { name: 'ghost', version: '1.0.0' };
  }, /dangling.*workspace link/],
  ['stale workspace declarations', ({ lock }) => { lock.packages[''].workspaces = []; }, /workspace declarations mismatch/],
  ['dependency manifest drift', ({ workspaces }) => { workspaces[hostPath].dependencies.acorn = '1.0.0'; }, /dependencies mismatch/],
  ['orphan installed parent', ({ lock }) => {
    lock.packages['orphan/node_modules/acorn'] = structuredClone(lock.packages[acornPath]);
  }, /missing installed parent/],
  ['path traversal', ({ lock }) => {
    lock.packages['../node_modules/acorn'] = structuredClone(lock.packages[acornPath]);
  }, /invalid package location/],
]) {
  test(`rejects ${label}`, t => {
    const project = fixture(t, mutate);
    assert.throws(() => verifyDependencyLock(project.directory), error);
  });
}

test('a lock entry does not substitute for a missing workspace manifest on disk', t => {
  const project = fixture(t);
  unlinkSync(join(project.directory, hostPath, 'package.json'));
  assert.throws(() => verifyDependencyLock(project.directory), /packages\/fixture-host: undeclared workspace/);
});

for (const path of ['npm-shrinkwrap.json', `${hostPath}/package-lock.json`, `${hostPath}/npm-shrinkwrap.json`]) {
  test(`rejects competing lock ${path}`, t => {
    const project = fixture(t);
    project.write(path, original);
    assert.throws(() => verifyDependencyLock(project.directory), /override|root lock only/);
  });
}

test('installed hidden locks are neither consulted nor mistaken for competing source locks', t => {
  const project = fixture(t);
  project.write('node_modules/.package-lock.json', { corrupt: true });
  project.write(`${hostPath}/node_modules/.package-lock.json`, { corrupt: true });
  assert.doesNotThrow(() => verifyDependencyLock(project.directory));
  delete project.data.lock.packages[acornPath].integrity;
  project.write('package-lock.json', project.data.lock);
  assert.throws(() => verifyDependencyLock(project.directory), /missing integrity/);
});

test('workspace patterns, exclusions, re-inclusion and object declarations remain supported', t => {
  const project = fixture(t, ({ manifest, lock, workspaces }) => {
    manifest.workspaces = { packages: ['packages/*', '!packages/excluded', `!${hostPath}`, hostPath] };
    lock.packages[''].workspaces = manifest.workspaces;
    workspaces['packages/excluded'] = { name: 'excluded', version: '1.0.0' };
  });
  assert.equal(verifyDependencyLock(project.directory).workspaceLinks, workspacePaths.length);
});

test('npm v2 packages records remain authoritative over the legacy dependencies section', t => {
  const project = fixture(t, ({ lock }) => { lock.lockfileVersion = 2; lock.dependencies = {}; });
  assert.equal(verifyDependencyLock(project.directory).lockfileVersion, 2);
});

test('optional and peer flags do not exempt installed packages from integrity checks', t => {
  const project = fixture(t, ({ lock }) => {
    Object.assign(lock.packages[acornPath], { dev: true, optional: true, peer: true, devOptional: true });
  });
  assert.doesNotThrow(() => verifyDependencyLock(project.directory));
  delete project.data.lock.packages[acornPath].integrity;
  project.write('package-lock.json', project.data.lock);
  assert.throws(() => verifyDependencyLock(project.directory), /missing integrity/);
});

test('registry aliases, scoped packages and nested dependency locations remain supported', t => {
  const project = fixture(t, ({ lock, manifest }) => {
    lock.packages['node_modules/acorn-alias'] = { ...lock.packages[acornPath], name: 'acorn' };
    lock.packages[`${hostPath}/node_modules/fixture-dom/node_modules/acorn`] = { ...lock.packages[acornPath] };
    manifest.dependencies = { 'acorn-alias': `npm:acorn@${lock.packages[acornPath].version}` };
    lock.packages[''].dependencies = manifest.dependencies;
  });
  const report = verifyDependencyLock(project.directory);
  assert.equal(report.registry.length, 5);
  assert.equal(report.registry.find(entry => entry.location === 'node_modules/acorn-alias').name, 'acorn');
});

test('workspace records may omit folder-implied names; optional dependencies may override regular ones', t => {
  const project = fixture(t, ({ lock, workspaces }) => {
    delete lock.packages[hostPath].name;
    const workspace = workspaces[hostPath];
    workspace.optionalDependencies = { acorn: workspace.dependencies.acorn };
    lock.packages[hostPath].optionalDependencies = workspace.optionalDependencies;
    delete lock.packages[hostPath].dependencies.acorn;
  });
  assert.doesNotThrow(() => verifyDependencyLock(project.directory));
});

test('bundled children use an enclosing artifact, not invented independent digests', t => {
  const project = fixture(t, ({ lock }) => {
    lock.packages[acornPath].bundleDependencies = ['bundled-child'];
    lock.packages[`${acornPath}/node_modules/bundled-child`] = { version: '1.0.0', inBundle: true };
  });
  assert.equal(verifyDependencyLock(project.directory).bundled.length, 1);
  delete project.data.lock.packages[acornPath].integrity;
  project.write('package-lock.json', project.data.lock);
  assert.throws(() => verifyDependencyLock(project.directory), /missing integrity/);
});

test('inBundle cannot turn an unbundled dependency into an integrity exemption', t => {
  const project = fixture(t, ({ lock }) => {
    lock.packages[acornPath].inBundle = true;
    delete lock.packages[acornPath].integrity;
  });
  assert.throws(() => verifyDependencyLock(project.directory), /no enclosing bundled artifact/);
});

test('CLI returns the complete offline inventory and a nonzero exit for a real bad fixture', t => {
  const project = fixture(t);
  const before = readFileSync(join(project.directory, 'package-lock.json'));
  const good = spawnSync(process.execPath, [checker, project.directory, '--json'], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(good.status, 0, good.stderr);
  assert.deepEqual(JSON.parse(good.stdout), verifyDependencyLock(project.directory));
  assert.deepEqual(readFileSync(join(project.directory, 'package-lock.json')), before);
  delete project.data.lock.packages[acornPath].integrity;
  project.write('package-lock.json', project.data.lock);
  const bad = spawnSync(process.execPath, [checker, project.directory], { encoding: 'utf8', timeout: 10_000 });
  assert.equal(bad.status, 1);
  assert.match(bad.stderr, /missing integrity/);
  assert.equal(bad.stdout, '');
  assert.ok(!existsSync(join(project.directory, 'node_modules')));
});
