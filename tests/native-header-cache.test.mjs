import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { homedir } from 'node:os';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dependencyCacheRoot } from '../scripts/dependency-download.mjs';
import { loadedBuildInputs, nodeGypCache, policy, root, unchanged, verifyHeaders } from '../scripts/quickjs/build-native.mjs';

const require = createRequire(import.meta.url);
const gypPackage = require.resolve('node-gyp/package.json');
const gypRequire = createRequire(gypPackage);
const tar = gypRequire('tar');
const version = policy.nodeHeaders.version;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

function environment(t, values) {
  const names = Object.keys(process.env).filter(key => /^npm_(?:config_devdir|package_config_node_gyp_devdir)$/i.test(key));
  const previous = Object.fromEntries(names.map(key => [key, process.env[key]]));
  for (const key of names) delete process.env[key];
  Object.assign(process.env, values);
  t.after(() => {
    for (const key of Object.keys(values)) delete process.env[key];
    Object.assign(process.env, previous);
  });
}

for (const [name, values, expected] of [
  ['default', {}, () => resolve(dependencyCacheRoot(), 'node-gyp')],
  ['legacy environment', { npm_config_devdir: 'relative/gyp' }, () => resolve(root, 'relative/gyp')],
  ['preferred environment', { npm_package_config_node_gyp_devdir: '~/Developer/cache/selected-gyp' },
    () => resolve(homedir(), 'Developer/cache/selected-gyp')],
  ['preferred over legacy', { npm_config_devdir: 'other', npm_package_config_node_gyp_devdir: 'selected' },
    () => resolve(root, 'selected')],
  ['case-insensitive configuration', { NPM_PACKAGE_CONFIG_NODE_GYP_DEVDIR: 'selected' }, () => resolve(root, 'selected')],
]) {
  test(`node-gyp ${name} resolves one absolute devdir for install and configure`, t => {
    environment(t, values);
    const cache = nodeGypCache(gypPackage);
    assert.equal(cache.directory, expected());
    for (const [command, cwd] of [['install', root], ['configure', join(root, 'packages/script-host')]]) {
      const parsed = spawnSync(process.execPath, ['-e', `
        const gyp = require(${JSON.stringify(dirname(gypPackage))})();
        gyp.parseArgv(['node', 'node-gyp', ${JSON.stringify(command)}, process.argv[1]]);
        console.log(JSON.stringify({ devdir: gyp.opts.devdir, nodedir: gyp.opts.nodedir ?? null }));
      `, '--', `--devdir=${cache.directory}`], {
        cwd, env: { ...process.env, ...cache.environment }, encoding: 'utf8', timeout: 10000,
      });
      assert.equal(parsed.status, 0, parsed.stderr);
      assert.deepEqual(JSON.parse(parsed.stdout), { devdir: cache.directory, nodedir: null });
    }
  });
}

test('node-gyp installs the local pinned tarball into the configured cache and verification uses it',
  { skip: process.platform === 'win32' ? 'Windows node.lib installation requires a network download' : false }, async t => {
    const temporary = fileURLToPath(new URL('../tmp/test-results/native-header-cache/', import.meta.url));
    await mkdir(temporary, { recursive: true });
    const area = await mkdtemp(join(temporary, 'case-'));
    t.after(() => rm(area, { recursive: true, force: true }));
    environment(t, { npm_package_config_node_gyp_devdir: join(area, 'configured') });
    const cache = nodeGypCache(gypPackage), input = join(area, 'source'), archive = join(area, 'headers.tar.gz');
    const header = `node-v${version}/include/node/node_api.h`;
    await mkdir(dirname(join(input, header)), { recursive: true });
    await writeFile(join(input, header), 'offline N-API header fixture\n');
    await tar.c({ gzip: true, file: archive, cwd: input }, [`node-v${version}`]);
    const installed = spawnSync(process.execPath, [join(dirname(gypPackage), 'bin/node-gyp.js'),
      'install', version, '--ensure', `--devdir=${cache.directory}`, `--tarball=${archive}`], {
      cwd: root, env: { ...process.env, ...cache.environment }, encoding: 'utf8', timeout: 10000,
    });
    assert.equal(installed.status, 0, installed.stderr);
    const headerRoot = join(cache.directory, version);
    const verified = await verifyHeaders(gypPackage, archive, headerRoot);
    assert.equal(verified.root, headerRoot);
    assert.deepEqual(verified.inputs, [{
      path: 'include/node/node_api.h', sha256: hash('offline N-API header fixture\n'),
    }]);
    await writeFile(join(headerRoot, 'include/node/node_api.h'), 'changed');
    await assert.rejects(verifyHeaders(gypPackage, archive, headerRoot), /Node header cache mismatch/);
  });

test('the download helper is a mandatory, byte-bound native build input', async () => {
  const path = 'scripts/dependency-download.mjs';
  assert.ok(policy.mandatoryInputs.includes(path));
  const recorded = loadedBuildInputs.find(input => input.path === path);
  assert.deepEqual(recorded, { path, sha256: hash(await readFile(resolve(root, path))) });
  await unchanged([recorded]);
  await assert.rejects(unchanged([{ ...recorded, sha256: '0'.repeat(64) }]), /Native input changed during build/);
});
