import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PrivateDocuments } from '../src/private-documents.js';
import { atomicWrite } from '../src/private-volume.js';

function root(t) {
  const path = fs.mkdtempSync(join(tmpdir(), 'meowfilm-documents-'));
  t.after(() => fs.rmSync(path, { recursive: true, force: true }));
  return path;
}
function directoryAlias(target, path) {
  fs.symlinkSync(target, path, process.platform === 'win32' ? 'junction' : 'dir');
}

test('owner documents create and restore data through App and area directory aliases', t => {
  const base = root(t), actual = join(base, 'actual'), alias = join(base, 'alias');
  fs.mkdirSync(actual);
  directoryAlias(actual, alias);
  const dataRoot = join(alias, 'app', 'data');
  const store = new PrivateDocuments(dataRoot, 'music');
  assert.equal(store.read('settings.json'), null);
  assert.deepEqual(store.list('sources'), []);
  assert.equal(store.remove('sources/missing.js'), false);
  store.write('settings.json', Buffer.from('initial'));
  const area = join(dataRoot, 'music'), moved = join(base, 'music-files');
  fs.renameSync(area, moved);
  directoryAlias(moved, area);
  store.write('sources/a.js', Buffer.from('source'));
  const restored = new PrivateDocuments(dataRoot, 'music');
  assert.equal(restored.read('settings.json').toString(), 'initial');
  assert.equal(restored.read('sources/a.js').toString(), 'source');
  assert.deepEqual(restored.list('sources'), ['sources/a.js']);
  restored.write('settings.json', Buffer.from('updated'));
  assert.equal(fs.readFileSync(join(moved, 'settings.json'), 'utf8'), 'updated');
  assert.equal(restored.remove('sources/a.js'), true);
  assert.deepEqual(restored.list('sources'), []);
});

test('owner documents read linked files and atomically replace only their own reference', t => {
  const base = root(t), store = new PrivateDocuments(base, 'music');
  store.write('sources/a.js', Buffer.from('source'));
  const source = join(base, 'music', 'sources', 'a.js');
  fs.linkSync(source, join(base, 'music', 'sources', 'b.js'));
  assert.equal(store.read('sources/b.js').toString(), 'source');
  assert.deepEqual(store.list('sources').sort(), ['sources/a.js', 'sources/b.js']);
  store.write('sources/b.js', Buffer.from('replacement'));
  assert.equal(store.read('sources/a.js').toString(), 'source');
  assert.equal(store.read('sources/b.js').toString(), 'replacement');
  assert.equal(store.remove('sources/b.js'), true);
  assert.equal(store.read('sources/a.js').toString(), 'source');
});

test('owner document I/O failures stay errors and do not replace data', t => {
  const base = root(t), store = new PrivateDocuments(base, 'music');
  store.write('settings.json', Buffer.from('saved'));
  fs.mkdirSync(join(base, 'music', 'folder'));
  assert.throws(() => store.read('folder'), { code: 'STORAGE_ERROR' });
  assert.throws(() => store.write('folder', Buffer.from('lost')), { code: 'STORAGE_ERROR' });
  assert.throws(() => store.remove('folder'), { code: 'STORAGE_ERROR' });
  assert.throws(() => store.list('settings.json'), { code: 'STORAGE_ERROR' });
  // Windows reports ENOENT for this path; POSIX reports ENOTDIR.
  if (process.platform === 'win32') assert.equal(store.read('settings.json/child'), null);
  else assert.throws(() => store.read('settings.json/child'), { code: 'STORAGE_ERROR' });
  assert.throws(() => store.write('settings.json/child', Buffer.from('lost')), { code: 'STORAGE_ERROR' });
  assert.equal(store.read('settings.json').toString(), 'saved');
  assert.deepEqual(fs.readdirSync(join(base, 'music')).sort(), ['folder', 'settings.json']);
});

test('owner atomic writes keep the accepted file on pre-rename failure', t => {
  const base = root(t), path = join(base, 'settings.json');
  atomicWrite(path, 'saved');
  const failure = Object.assign(new Error('synthetic sync failure'), { code: 'EIO' });
  const original = fs.fsyncSync;
  t.after(() => { fs.fsyncSync = original; syncBuiltinESMExports(); });
  fs.fsyncSync = () => { throw failure; };
  syncBuiltinESMExports();
  assert.throws(() => atomicWrite(path, 'uncommitted'), error => error === failure);
  assert.equal(fs.readFileSync(path, 'utf8'), 'saved');
  assert.deepEqual(fs.readdirSync(base), ['settings.json']);
});

test('owner document names retain the internal one-subfolder naming contract', t => {
  const store = new PrivateDocuments(root(t), 'music');
  for (const name of ['../other', '/absolute', 'a/b/c', 'a\\b'])
    assert.throws(() => store.read(name), { code: 'INVALID_ARGUMENT' });
});
