import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { applySourcePatch, sha256, treeHash } from '../scripts/windows/prepare-film-kernel.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));

test('film dependency lock binds fixed FFmpeg source and every native patch input', async () => {
  const lock = JSON.parse(await readFile(join(root, 'apps/windows/native/FilmKernel/film-kernel.lock.json')));
  const ios = JSON.parse(await readFile(join(root, 'scripts/ios-video-kernel.json')));
  assert.equal(lock.schema, 1);
  assert.equal(lock.target, 'windows-x86_64-ucrt');
  for (const key of ['version', 'revision', 'url', 'sha256'])
    assert.equal(lock.sources.ffmpeg[key], ios.sources.ffmpeg[key]);
  assert.deepEqual(lock.provenance.ksPlayer, ios.sources.ksplayer);
  for (const [path, hash] of Object.entries(lock.inputs))
    assert.equal(sha256(await readFile(join(root, path))), hash, path);
  for (const source of Object.values(lock.sources)) {
    assert.match(source.sha256, /^[a-f0-9]{64}$/);
    assert.match(source.treeSHA256, /^[a-f0-9]{64}$/);
    assert.match(source.preparedTreeSHA256, /^[a-f0-9]{64}$/);
    assert.ok(Object.keys(source.licenses).length > 0);
  }
});

test('film source patches apply exact ordered hunks transactionally', () => {
  const source = new Map([['decoder.c', 'first\nold\nlast\n']]);
  const patch = '--- a/decoder.c\n+++ b/decoder.c\n@@ -1,3 +1,4 @@\n first\n-old\n+new\n+more\n last\n';
  assert.equal(applySourcePatch(source, patch).get('decoder.c'), 'first\nnew\nmore\nlast\n');
  assert.equal(source.get('decoder.c'), 'first\nold\nlast\n');
  for (const invalid of ['', patch.replace(' first\n', ' different\n'), patch.replace(' last\n', ''),
    patch.replace('+++ b/decoder.c', '+++ b/foreign.c'), patch + patch, patch + 'trailing\n'])
    assert.throws(() => applySourcePatch(source, invalid));
});

test('film source fingerprint detects bytes, names and linked inputs', async t => {
  const parent = join(root, 'tmp/test-results/windows-film-kernel-build');
  await mkdir(parent, { recursive: true });
  const directory = await mkdtemp(join(parent, 'tree-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  await mkdir(join(directory, 'decoder'));
  await writeFile(join(directory, 'decoder/source.c'), 'original');
  const original = await treeHash(directory);
  await writeFile(join(directory, 'decoder/source.c'), 'changed');
  assert.notEqual(await treeHash(directory), original);
  await writeFile(join(directory, 'decoder/source.c'), 'original');
  assert.equal(await treeHash(directory), original);
  await writeFile(join(directory, 'decoder/extra.c'), 'original');
  assert.notEqual(await treeHash(directory), original);
  await symlink(join(directory, 'decoder'), join(directory, 'alias'),
    process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(treeHash(directory), /Links are not allowed/);
});
