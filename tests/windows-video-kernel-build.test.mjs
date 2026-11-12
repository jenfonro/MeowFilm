import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { copyFile, mkdtemp, readFile, rm, mkdir, writeFile, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { applySourcePatch, sha256, treeHash } from '../scripts/windows/prepare-video-kernel.mjs';

test('locked native patch applies all exact hunks and preserves surrounding bytes', () => {
  const files = new Map([['lib/source.c', 'first\nold\nlast\n'], ['lib/other.c', 'end\n']]);
  const patch = '--- a/lib/source.c\n+++ b/lib/source.c\n@@ -1,3 +1,4 @@\n first\n-old\n+new\n+more\n last\n' +
    '--- a/lib/other.c\n+++ b/lib/other.c\n@@ -1 +1 @@\n-end\n+done\n';
  const outputs = applySourcePatch(files, patch);
  assert.deepEqual([...outputs], [['lib/source.c', 'first\nnew\nmore\nlast\n'], ['lib/other.c', 'done\n']]);
  assert.equal(files.get('lib/source.c'), 'first\nold\nlast\n');
});

test('locked native patch rejects foreign paths, mismatched context, partial hunks and fuzz', () => {
  const files = new Map([['source.c', 'first\nold\nlast\n']]);
  const valid = '--- a/source.c\n+++ b/source.c\n@@ -1,3 +1,3 @@\n first\n-old\n+new\n last\n';
  for (const patch of [
    '', valid.replaceAll('source.c', '../foreign.c'),
    valid.replace('+++ b/source.c', '+++ b/other.c'),
    valid.replace(' first\n', ' wrong\n'), valid.replace('-old\n', '-different\n'),
    valid.replace(' last\n', ''), valid.replace('-1,3', '-2,3'),
    valid + '\nforeign trailing data\n', valid + valid,
    '--- a/source.c\n+++ b/source.c\n',
    valid.replace('@@ -1,3 +1,3 @@', '@@ malformed @@')
  ]) assert.throws(() => applySourcePatch(files, patch));
});

test('prepared source fingerprint binds relative names and bytes and refuses links', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'video-kernel-build-'));
  try {
    await mkdir(join(directory, 'lib'));
    await writeFile(join(directory, 'lib/source.c'), 'one');
    const original = await treeHash(directory);
    await writeFile(join(directory, 'lib/source.c'), 'two');
    assert.notEqual(await treeHash(directory), original);
    await writeFile(join(directory, 'lib/source.c'), 'one');
    assert.equal(await treeHash(directory), original);
    await writeFile(join(directory, 'lib/other.c'), 'one');
    assert.notEqual(await treeHash(directory), original);
    await symlink(join(directory, 'lib'), join(directory, 'alias'), process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(treeHash(directory), /cannot traverse links/);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('Windows preparation reuses shared archives while retaining private sources, SDK and receipt inputs', async t => {
  const repository = fileURLToPath(new URL('../', import.meta.url));
  const temporary = join(repository, 'tmp/test-results/windows-video-download');
  await mkdir(temporary, { recursive: true });
  const area = await mkdtemp(join(temporary, 'case-')), home = join(area, 'home');
  const bodies = { ffmpeg: Buffer.from('FFmpeg archive fixture'), libxml2: Buffer.from('libxml2 archive fixture'),
    vlcSdk: Buffer.from('VLC SDK archive fixture') };
  const requests = [];
  const server = createServer((request, response) => {
    requests.push(request.url);
    response.end(bodies[request.url.slice(1)]);
  });
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    await rm(area, { recursive: true, force: true });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const sources = Object.fromEntries(Object.entries(bodies).map(([name, bytes]) =>
    [name, { version: 'fixture', url: `http://127.0.0.1:${server.address().port}/${name}`, sha256: sha256(bytes) }]));
  const lock = { version: 1, architecture: 'win-x64', vlcPluginAbi: '3_0_0f', sources,
    vlcCoreSha256: sha256('core fixture') };
  const patch = 'prepared fixture patch', header = 'private header fixture';
  const shared = join(home, 'Developer/cache');
  const appleArchive = join(shared, 'ffmpeg/downloads', sources.ffmpeg.sha256 + '.tar.gz');
  await mkdir(dirname(appleArchive), { recursive: true });
  await writeFile(appleArchive, bodies.ffmpeg);
  const receipts = [];
  for (const name of ['first', 'second']) {
    const root = join(area, name), cache = join(root, '.cache/windows-video-kernel');
    const write = async (path, bytes) => {
      await mkdir(dirname(join(root, path)), { recursive: true });
      await writeFile(join(root, path), bytes);
    };
    for (const path of ['scripts/dependency-download.mjs', 'scripts/windows/prepare-video-kernel.mjs']) {
      await mkdir(dirname(join(root, path)), { recursive: true });
      await copyFile(join(repository, path), join(root, path));
    }
    const source = 'apps/windows/native/VideoKernel';
    await write(`${source}/video-kernel.lock.json`, JSON.stringify(lock));
    await write(`${source}/ffmpeg-controlled-avio.patch`, patch);
    await write(`${source}/ffmpeg-private.h`, header);
    await write('.cache/windows-video-kernel/sources/ffmpeg/RELEASE', 'fixture');
    await write('.cache/windows-video-kernel/sources/libxml2/source.c', 'xml fixture');
    const sdkRoot = '.cache/windows-video-kernel/sdk/vlc-3.0.24';
    await write(`${sdkRoot}/vlc-3.0.24/sdk/lib/pkgconfig/vlc-plugin.pc`, 'Version: 3.0.24');
    await write(`${sdkRoot}/vlc-3.0.24/sdk/include/vlc/plugins/vlc_plugin.h`, 'header fixture');
    await write(`${sdkRoot}/vlc-3.0.24/libvlccore.dll`, 'core fixture');
    await write('.cache/windows-video-kernel/prepared.json', JSON.stringify({
      preparation: sha256(JSON.stringify({ sources, patch: sha256(patch), header: sha256(header) })),
      ffmpeg: await treeHash(join(cache, 'sources/ffmpeg')),
      xml: await treeHash(join(cache, 'sources/libxml2')),
      sdk: await treeHash(join(root, sdkRoot)),
    }));
    // Model the Unix default cache location in an isolated child even on Windows.
    // No compiler/extractor is run: the fixture has an exact prepared source inventory.
    const { stdout } = await promisify(execFile)(process.execPath, ['--input-type=module', '-e', `
      Object.defineProperty(process, 'platform', { value: 'linux' });
      const { prepareVideoKernel } = await import(${JSON.stringify(pathToFileURL(join(root, 'scripts/windows/prepare-video-kernel.mjs')).href)});
      console.log(JSON.stringify(await prepareVideoKernel('must-not-run')));
    `], { env: { ...process.env, HOME: home, USERPROFILE: home }, timeout: 15000 });
    const receipt = JSON.parse(stdout);
    receipts.push(receipt);
    assert.equal(receipt.cache, cache);
    assert.equal(receipt.ffmpeg, join(cache, 'sources/ffmpeg'));
    assert.equal(receipt.xml, join(cache, 'sources/libxml2'));
    assert.equal(receipt.sdk, join(root, sdkRoot, 'vlc-3.0.24/sdk'));
    assert.deepEqual(receipt.prepared.downloadRecipe, { path: 'scripts/dependency-download.mjs',
      sha256: sha256(await readFile(join(repository, 'scripts/dependency-download.mjs'))) });
  }
  assert.deepEqual(requests, ['/libxml2', '/vlcSdk']);
  assert.deepEqual(receipts[0].prepared, receipts[1].prepared);
  await writeFile(join(receipts[0].ffmpeg, 'RELEASE'), 'private mutation');
  assert.equal(await readFile(join(receipts[1].ffmpeg, 'RELEASE'), 'utf8'), 'fixture');
  assert.deepEqual(await readFile(appleArchive), bodies.ffmpeg);
  for (const name of ['libxml2', 'vlcSdk'])
    assert.deepEqual(await readFile(join(shared, 'downloads', sources[name].sha256)), bodies[name]);
});
