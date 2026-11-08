import assert from 'node:assert/strict';
import test from 'node:test';
import { closeSync, mkdtempSync, openSync, readSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { duplicateBootstrapDescriptor } from '../src/native-engine.js';

const posix = { skip: process.platform === 'win32' ? 'The iOS bootstrap descriptor is POSIX-only' : false };

function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), 'meow-bootstrap-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const path = join(directory, 'bytes');
  writeFileSync(path, 'native');
  return path;
}

function byte(descriptor) {
  const bytes = Buffer.alloc(1);
  assert.equal(readSync(descriptor, bytes, 0, 1, null), 1);
  return bytes.toString();
}

for (const retired of ['original', 'copy']) {
  test(`POSIX bootstrap duplication keeps the other owner valid after closing ${retired}`, posix, t => {
    let original = openSync(fixture(t), 'r'), copy;
    try {
      copy = duplicateBootstrapDescriptor(original);
      assert.ok(Number.isInteger(copy) && copy >= 3);
      assert.notEqual(copy, original);
      assert.equal(byte(original), 'n');
      assert.equal(byte(copy), 'a', 'A duplicate shares the open file position; it is not a reopened file');
      if (retired === 'original') {
        closeSync(original); original = undefined;
        assert.equal(byte(copy), 't');
      } else {
        closeSync(copy); copy = undefined;
        assert.equal(byte(original), 't');
      }
    } finally {
      if (copy !== undefined) closeSync(copy);
      if (original !== undefined) closeSync(original);
    }
  });
}

test('POSIX duplication failures leave the original and a reused copy number untouched', posix, t => {
  const path = fixture(t), original = openSync(path, 'r');
  let copy, guard;
  try {
    copy = duplicateBootstrapDescriptor(original);
    const retired = copy;
    closeSync(copy); copy = undefined;
    assert.throws(() => duplicateBootstrapDescriptor(retired), { code: 'DESCRIPTOR_UNAVAILABLE' });
    guard = openSync(path, 'r');
    assert.equal(guard, retired, 'The scenario must actually reuse the retired copy number');
    for (const value of [-1, 0, 1, 2, 3.5, 0x80000000, NaN, Infinity, '3', null, undefined, 3n, {}]) {
      assert.throws(() => duplicateBootstrapDescriptor(value), { code: 'INVALID_ARGUMENT' });
    }
    copy = duplicateBootstrapDescriptor(original);
    assert.notEqual(copy, original);
    assert.notEqual(copy, guard);
    closeSync(copy); copy = undefined;
    assert.equal(byte(guard), 'n');
    assert.equal(byte(original), 'n');
  } finally {
    if (copy !== undefined) closeSync(copy);
    if (guard !== undefined) closeSync(guard);
    closeSync(original);
  }
});
