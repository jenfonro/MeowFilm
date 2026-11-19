import { parentPort, workerData } from 'node:worker_threads';
import { DatabaseSync, backup } from 'node:sqlite';
import { lstatSync, chmodSync, openSync, closeSync, unlinkSync, realpathSync, fsyncSync } from 'node:fs';
import { dirname, isAbsolute } from 'node:path';
import { acquireNativeStorageLock as acquire } from './native-engine.js';

const { path, identity, initialize } = workerData;
const fail = code => { throw Object.assign(new Error(code), { code }); };
let db, fileIdentity, commits = 0, maintenanceFailure;
const enter = (exclusive, timeout = 2500) =>
  acquire(dirname(path) + '/maintenance.lock', exclusive, timeout);
function maintain() {
  let lease;
  try {
    lease = enter(true, 0);
    query('PRAGMA wal_checkpoint(PASSIVE)');
    if (db.prepare('PRAGMA freelist_count').get().freelist_count >= 256)
      db.exec('PRAGMA incremental_vacuum(128)');
  } catch (error) {
    if (code(error) !== 'STORAGE_BUSY') maintenanceFailure = code(error);
  } finally { lease?.release(); }
}

function safeFile(file, required = false) {
  let stat;
  try { stat = lstatSync(file); }
  catch (error) { if (error.code === 'ENOENT' && !required) return; throw error; }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1) fail('UNSAFE_PATH');
  return stat;
}

function code(error) {
  if (error?.code && !error.code.startsWith('ERR_SQLITE')) return error.code;
  if (error?.errcode === 19 || [1555, 2067, 275].includes(error?.errcode)) return 'STORAGE_CONSTRAINT';
  switch (error?.errcode & 0xff) {
    case 5: case 6: return 'STORAGE_BUSY';
    case 8: return 'STORAGE_READONLY';
    case 10: case 14: return 'STORAGE_IO';
    case 11: case 26: return 'STORAGE_CORRUPT';
    case 13: return 'STORAGE_FULL';
    case 19: return 'STORAGE_CONSTRAINT';
    default: return 'STORAGE_ERROR';
  }
}

function parameters(params) {
  if (!Array.isArray(params)) fail('INVALID_REQUEST');
  return params.map(value => {
    if (value === null || typeof value === 'string' || typeof value === 'bigint' || ArrayBuffer.isView(value))
      return value;
    if (typeof value === 'boolean') return Number(value);
    if (typeof value === 'number' && Number.isFinite(value) &&
        (!Number.isInteger(value) || Number.isSafeInteger(value))) return value;
    fail('INVALID_REQUEST');
  });
}

function value(item) {
  if (typeof item === 'bigint' && item >= -9007199254740991n && item <= 9007199254740991n)
    return Number(item);
  return item;
}

function query(sql, params = [], maximum = Number.MAX_SAFE_INTEGER) {
  if (typeof sql !== 'string' || !Number.isSafeInteger(maximum) || maximum < 1)
    fail('INVALID_REQUEST');
  const statement = db.prepare(sql);
  statement.setReadBigInts(true);
  const rows = [];
  for (const row of statement.iterate(...parameters(params))) {
    if (rows.length >= maximum) fail('STORAGE_RESULT_LIMIT');
    const result = {};
    for (const [key, item] of Object.entries(row)) {
      const field = value(item);
      result[key] = field;
    }
    rows.push(result);
  }
  return rows;
}

function execute(sql, params = [], expected) {
  if (typeof sql !== 'string') fail('INVALID_REQUEST');
  try {
    const result = db.prepare(sql).run(...parameters(params));
    if (expected !== undefined && result.changes !== expected) fail('STORAGE_CONFLICT');
    return { changes: Number(result.changes), lastInsertRowid: value(result.lastInsertRowid) };
  } catch (error) { throw error; }
}

function transaction(statements, readOnly = false) {
  if (!Array.isArray(statements) || !statements.length)
    fail('INVALID_REQUEST');
  db.exec(readOnly ? 'BEGIN DEFERRED' : 'BEGIN IMMEDIATE');
  try {
    const results = statements.map(item => item.rows
      ? query(item.sql, item.params, item.maxRows)
      : execute(item.sql, item.params, item.expectChanges));
    db.exec('COMMIT');
    return results;
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  }
}

let initializing, validating;
try {
  initializing = acquire(dirname(path) + '/initialize.lock', true, 5000);
  validating = enter(false, 5000);
  const existing = safeFile(path);
  for (const suffix of ['-wal', '-shm', '-journal']) {
    if (safeFile(path + suffix) && !existing) fail('STORAGE_CORRUPT');
  }
  if (existing?.size === 0) fail('STORAGE_CORRUPT');
  if (!existing && !initialize) fail('STORAGE_SCHEMA');
  if (!existing) closeSync(openSync(path, 'wx', 0o600));
  db = new DatabaseSync(path, { enableForeignKeyConstraints: true, allowExtension: false });
  if (process.platform !== 'win32') chmodSync(path, 0o600);
  const engine = db.prepare('SELECT sqlite_version() AS version').get().version.split('.').map(Number);
  if (!(engine[0] === 3 && (engine[1] > 51 || engine[1] === 51 && engine[2] >= 3 ||
      engine[1] === 50 && engine[2] >= 7 || engine[1] === 44 && engine[2] >= 6)))
    fail('STORAGE_ENGINE_UNSUPPORTED');
  const application = db.prepare('PRAGMA application_id').get().application_id;
  const version = db.prepare('PRAGMA user_version').get().user_version;
  const empty = db.prepare("SELECT count(*) AS count FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'").get().count === 0;
  if (empty) {
    if (existing || application !== 0 || version !== 0) fail('STORAGE_SCHEMA');
    db.exec('PRAGMA auto_vacuum=INCREMENTAL');
  } else if (application !== identity.applicationId || version !== identity.version) fail('STORAGE_SCHEMA');
  db.exec('PRAGMA busy_timeout=2500; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; ' +
    'PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0;');
  if (db.prepare('PRAGMA journal_mode').get().journal_mode !== 'wal' ||
      db.prepare('PRAGMA synchronous').get().synchronous !== 2 ||
      db.prepare('PRAGMA foreign_keys').get().foreign_keys !== 1 ||
      db.prepare('PRAGMA trusted_schema').get().trusted_schema !== 0 ||
      db.prepare('PRAGMA wal_autocheckpoint').get().wal_autocheckpoint !== 0 ||
      db.prepare('PRAGMA auto_vacuum').get().auto_vacuum !== 2) fail('STORAGE_CONFIGURATION');
  if (empty) {
    db.exec('BEGIN IMMEDIATE');
    try {
      db.exec(identity.sql);
      db.prepare('INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,?)').run(identity.hash);
      db.exec(`PRAGMA application_id=${identity.applicationId}; PRAGMA user_version=${identity.version};`);
      db.exec('COMMIT');
    } catch (error) { if (db.isTransaction) db.exec('ROLLBACK'); throw error; }
  }
  const stored = db.prepare('SELECT schema_hash FROM storage_identity WHERE singleton=1').get();
  if (stored?.schema_hash !== identity.hash) fail('STORAGE_SCHEMA');
  const expected = new DatabaseSync(':memory:', { allowExtension: false });
  try {
    expected.exec(identity.sql);
    const structure = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
    if (JSON.stringify(db.prepare(structure).all()) !== JSON.stringify(expected.prepare(structure).all()))
      fail('STORAGE_SCHEMA');
  } finally { expected.close(); }
  if (db.prepare('PRAGMA foreign_key_check').get()) fail('STORAGE_CORRUPT');
  fileIdentity = lstatSync(path, {bigint:true});
  parentPort.postMessage({ id: 0, value: true });
} catch (error) {
  try { db?.close(); } catch {}
  parentPort.postMessage({ id: 0, code: code(error) });
  parentPort.close();
} finally { validating?.release(); initializing?.release(); }

parentPort.on('message', async ({ id, input }) => {
  let lease;
  try {
    if (maintenanceFailure && input.kind !== 'close') fail(maintenanceFailure);
    if (realpathSync(dirname(path)) !== dirname(path)) fail('UNSAFE_PATH');
    for (const suffix of ['', '-wal', '-shm', '-journal']) safeFile(path + suffix, suffix === '');
    const current = lstatSync(path, {bigint:true});
    if (current.ino !== fileIdentity.ino || current.dev !== fileIdentity.dev) fail('UNSAFE_PATH');
    lease = enter(['checkpoint','backup','close'].includes(input.kind));
    let result;
    if (input.kind === 'query') result = query(input.sql, input.params, input.maxRows);
    else if (input.kind === 'execute') result = transaction([input])[0];
    else if (input.kind === 'transaction') result = transaction(input.statements);
    else if (input.kind === 'snapshot') result = transaction(input.statements, true);
    else if (input.kind === 'checkpoint') result = query('PRAGMA wal_checkpoint(PASSIVE)');
    else if (input.kind === 'backup') {
      // A backup is a new recovery point, never an overwrite of an existing file.
      if (typeof input.path !== 'string' || !isAbsolute(input.path) ||
          realpathSync(dirname(input.path)) !== dirname(input.path)) fail('UNSAFE_PATH');
      let claimed = false;
      try {
        closeSync(openSync(input.path, 'wx', 0o600));
        claimed = true;
        result = await backup(db, input.path);
        const file = openSync(input.path, 'r');
        try { fsyncSync(file); } finally { closeSync(file); }
        if (process.platform !== 'win32') {
          const parent = openSync(dirname(input.path), 'r');
          try { fsyncSync(parent); } finally { closeSync(parent); }
        }
      } catch (error) {
        if (claimed) unlinkSync(input.path);
        if (error.code === 'EEXIST') fail('STORAGE_CONFLICT');
        throw error;
      }
    }
    else if (input.kind === 'close') {
      query('PRAGMA wal_checkpoint(PASSIVE)');
      db.close(); result = true;
      if (maintenanceFailure) fail(maintenanceFailure);
    }
    else fail('INVALID_REQUEST');
    parentPort.postMessage({ id, value: result });
    if (input.kind === 'close') parentPort.close();
    if (input.kind === 'execute' || input.kind === 'transaction') ++commits;
  } catch (error) {
    parentPort.postMessage({ id, code: code(error) });
    if (input.kind === 'close') { try { db.close(); } catch {} parentPort.close(); }
  } finally {
    lease?.release();
    if (commits >= 64) { commits = 0; maintain(); }
  }
});
