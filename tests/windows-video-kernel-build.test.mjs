import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, mkdir, writeFile, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { applySourcePatch, treeHash } from '../scripts/windows/prepare-video-kernel.mjs';

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
