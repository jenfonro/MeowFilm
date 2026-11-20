import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { openDatabase } from '../src/database.js';
import { databaseProbe } from '../../../tests/storage-test-fixture.mjs';

async function fixture(run) {
  const directory = fileURLToPath(new URL('../../../tmp/storage-tests', import.meta.url));
  await mkdir(directory, {recursive:true});
  const root = await mkdtemp(join(directory, 'database-'));
  try { await run(root); } finally { await rm(root, {recursive:true, force:true}); }
}

test('current schema, constraints, transactional CAS and restart', () => fixture(async root => {
  const db = openDatabase(root), second = openDatabase(root);
  await db.ready;
  assert.equal((await db.query('PRAGMA journal_mode'))[0].journal_mode, 'wal');
  assert.equal((await db.query('PRAGMA synchronous'))[0].synchronous, 2);
  await db.execute('INSERT INTO owner_revisions VALUES(?,?,?)', ['test', 1, 'first']);
  await assert.rejects(db.transaction([
    {sql:'UPDATE owner_revisions SET revision=2 WHERE owner=? AND revision=?', params:['test',1],expectChanges:1},
    {sql:'UPDATE owner_revisions SET revision=3 WHERE owner=? AND revision=?', params:['test',7],expectChanges:1},
  ]), {code:'STORAGE_CONFLICT'});
  assert.equal((await second.query('SELECT revision FROM owner_revisions'))[0].revision, 1);
  await db.close();
  assert.equal((await second.query('SELECT revision FROM owner_revisions'))[0].revision, 1);
  await second.close();
  const restarted = openDatabase(root); await restarted.ready;
  assert.equal((await restarted.query('SELECT receipt FROM owner_revisions'))[0].receipt, 'first');
  await restarted.close();
}));

test('process termination without close preserves committed records and rolls back an unfinished transaction', () => fixture(async root => {
  const module = new URL('../src/database.js', import.meta.url).href;
  const setup = new URL('../../../tests/storage-test-fixture.mjs', import.meta.url).href;
  const source = `await import(process.argv[1]); const {openDatabase} = await import(process.argv[2]);
    const {DatabaseSync} = await import('node:sqlite'); const {join} = await import('node:path');
    const db = openDatabase(process.argv[3]);
    await db.execute("INSERT INTO owner_revisions VALUES('durable',1,'committed')");
    // The production API commits each call; a second connection holds the crash window open.
    const unfinished = new DatabaseSync(join(process.argv[3], 'storage-v1', 'meowfilm.sqlite3'));
    unfinished.exec('PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; BEGIN IMMEDIATE');
    unfinished.exec("UPDATE owner_revisions SET revision=2,receipt='unfinished' WHERE owner='durable'");
    unfinished.exec("INSERT INTO owner_revisions VALUES('partial',1,'unfinished')");
    process.send('transaction-open');`;
  const child = spawn(process.execPath, ['--input-type=module', '-e', source, setup, module, root],
    {stdio:['ignore', 'ignore', 'inherit', 'ipc']});
  const exited = once(child, 'exit');
  try {
    const signal = AbortSignal.timeout(15000);
    const ready = once(child, 'message', {signal});
    const message = await Promise.race([ready, exited.then(() => { throw new Error('writer exited before transaction'); })]);
    assert.equal(message[0], 'transaction-open');
    assert.ok((await readFile(join(root, 'storage-v1', 'meowfilm.sqlite3-wal'))).length > 0);
    assert.ok(child.kill('SIGKILL'));
    await exited;
  } finally {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    await exited;
  }
  const reopened = openDatabase(root, {initialize:false});
  try {
    assert.deepEqual(await reopened.query('SELECT owner,revision,receipt FROM owner_revisions'),
      [{owner:'durable', revision:1, receipt:'committed'}]);
    assert.equal((await reopened.query('PRAGMA integrity_check'))[0].integrity_check, 'ok');
    assert.deepEqual(await reopened.query('PRAGMA foreign_key_check'), []);
  } finally { await reopened.close(); }
}));

test('new program never reads an old JSON file; mismatched current database is a fault', () => fixture(async root => {
  await writeFile(join(root, 'accounts.json'), 'not json');
  const db = openDatabase(root); await db.ready;
  await db.execute('UPDATE storage_identity SET schema_hash=?', ['0'.repeat(64)]);
  await db.close();
  const wrong = openDatabase(root);
  await assert.rejects(wrong.ready, {code:'STORAGE_SCHEMA'});
  await wrong.close();
}));

test('opening does not consume unrelated business relations and new writes still enforce foreign keys', () => fixture(async root => {
  const original = openDatabase(root); await original.ready; await original.close();
  const { DatabaseSync } = await import('node:sqlite');
  const external = new DatabaseSync(join(root, 'storage-v1', 'meowfilm.sqlite3'));
  try {
    external.exec("PRAGMA foreign_keys=OFF; INSERT INTO cat_mapping_aliases VALUES(7,0,'orphan')");
  } finally { external.close(); }
  const db = openDatabase(root, {initialize:false});
  try {
    await db.ready;
    await db.execute("INSERT INTO owner_revisions VALUES('unrelated',1,'saved')");
    assert.equal((await db.query("SELECT receipt FROM owner_revisions WHERE owner='unrelated'"))[0].receipt, 'saved');
    await assert.rejects(db.execute("INSERT INTO cat_mapping_aliases VALUES(8,0,'new')"), {code:'STORAGE_CONSTRAINT'});
    // An explicit diagnostic can report corruption without making it a startup preflight.
    assert.equal((await db.query('PRAGMA foreign_key_check')).length, 1);
  } finally { await db.close(); }
}));

test('a changed table cannot pass by retaining the schema hash', () => fixture(async root => {
  const db = openDatabase(root); await db.ready;
  await db.execute('ALTER TABLE owner_revisions ADD COLUMN unknown TEXT');
  await db.close();
  const wrong = openDatabase(root);
  await assert.rejects(wrong.ready, {code:'STORAGE_SCHEMA'});
  await wrong.close();
}));

test('a truncated database or orphaned WAL is preserved, never adopted as a fresh install', () => fixture(async root => {
  for (const suffix of ['', '-wal', '-shm', '-journal']) {
    const dataRoot = join(root, 'case' + suffix), directory = join(dataRoot, 'storage-v1');
    await mkdir(directory, {recursive:true});
    const file = join(directory, 'meowfilm.sqlite3' + suffix);
    const bytes = Buffer.from(suffix ? 'synthetic retained journal' : '');
    await writeFile(file, bytes);
    const db = openDatabase(dataRoot);
    await assert.rejects(db.ready, {code:'STORAGE_CORRUPT'});
    await db.close();
    assert.deepEqual(await readFile(file), bytes);
    if (suffix) assert.ok(!(await readdir(directory)).includes('meowfilm.sqlite3'));
  }
}));

test('an attached runtime cannot initialize a missing native store', () => fixture(async root => {
  const db = openDatabase(join(root, 'absent'), { initialize:false });
  await assert.rejects(db.ready, {code:'STORAGE_SCHEMA'});
  await db.close();
  assert.deepEqual(await readdir(root), []);
}));

test('releasing a borrowed connection waits for its admitted write and preserves other borrowers', () => fixture(async root => {
  const probe = databaseProbe(root), lease = openDatabase(root);
  const entered = Promise.withResolvers(), release = Promise.withResolvers();
  probe.before = async input => { if (input.kind === 'execute') { entered.resolve(); await release.promise; } };
  try {
    const writing = lease.execute("INSERT INTO owner_revisions VALUES('lease',1,'saved')");
    await entered.promise;
    let ended = false;
    const closing = lease.close().then(() => { ended = true; });
    await assert.rejects(lease.query('SELECT 1'), {code:'STORAGE_CLOSED'});
    assert.equal(ended, false);
    release.resolve();
    await Promise.all([writing, closing]);
    assert.equal((await probe.database.query("SELECT revision FROM owner_revisions WHERE owner='lease'"))[0].revision, 1);
  } finally { release.resolve(); await lease.close(); await probe.close(); }
}));

test('backup is a consistent new snapshot and never overwrites an existing destination', () => fixture(async root => {
  const db = openDatabase(root); await db.ready;
  await db.execute('INSERT INTO owner_revisions VALUES(?,?,?)', ['backup-test',1,'saved']);
  const destination = join(root, 'snapshot.sqlite3');
  await db.backup(destination);
  await assert.rejects(db.backup(destination), {code:'STORAGE_CONFLICT'});
  const { DatabaseSync } = await import('node:sqlite');
  const snapshot = new DatabaseSync(destination, {readOnly:true});
  try {
    assert.equal(snapshot.prepare("SELECT receipt FROM owner_revisions WHERE owner='backup-test'").get().receipt, 'saved');
    assert.equal(snapshot.prepare('PRAGMA integrity_check').get().integrity_check, 'ok');
  } finally { snapshot.close(); }
  await db.close();
}));

test('admission is bounded, cancelled queued writes do not execute, accepted writes settle', () => fixture(async root => {
  const db = openDatabase(root); await db.ready;
  const writes = Array.from({length:64}, (_,i) => db.execute('INSERT INTO owner_revisions VALUES(?,?,?)', ['test'+i,1,'receipt']));
  await assert.rejects(db.query('SELECT 1'), {code:'STORAGE_BUSY'});
  await Promise.all(writes);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(db.execute('DELETE FROM owner_revisions', [], {signal:controller.signal}), {code:'CANCELLED'});
  assert.equal((await db.query('SELECT count(*) AS count FROM owner_revisions'))[0].count, 64);
  assert.deepEqual(db.diagnostics(), {pendingCount:0,pendingBytes:0});
  await db.close();
}));

test('independent process owners use a shared writer lock without lost updates', () => fixture(async root => {
  const module = new URL('../src/database.js', import.meta.url).href;
  const fixture = new URL('../../../tests/storage-test-fixture.mjs', import.meta.url).href;
  const source = `await import(process.argv[1]); const {openDatabase} = await import(process.argv[2]);
    const db = openDatabase(process.argv[3]);
    try {
      await db.execute("INSERT INTO owner_revisions VALUES('test',1,'receipt') ON CONFLICT DO NOTHING");
      for (let i=0; i<20; ++i) await db.execute("UPDATE owner_revisions SET revision=revision+1 WHERE owner='test'");
    } finally { await db.close(); }`;
  const results = await Promise.all(Array.from({length:2}, () => once(spawn(process.execPath,
    ['--input-type=module','-e',source,fixture,module,root], {stdio:'inherit'}), 'exit')));
  assert.deepEqual(results, [[0,null],[0,null]]);
  const db = openDatabase(root, {initialize:false});
  assert.equal((await db.query('SELECT revision FROM owner_revisions'))[0].revision, 41);
  await db.close();
}));

test('one large typed value is accepted while concurrent retained work stays bounded', () => fixture(async root => {
  const db = openDatabase(root);
  try {
    const text = 'x'.repeat(17 * 1024 * 1024);
    const writing = db.execute('INSERT INTO owner_revisions VALUES(?,?,?)', ['large', 1, text]);
    await assert.rejects(db.query('SELECT 1'), {code:'STORAGE_BUSY'});
    await writing;
    assert.equal((await db.query("SELECT receipt FROM owner_revisions WHERE owner='large'"))[0].receipt, text);
  } finally { await db.close(); }
}));
