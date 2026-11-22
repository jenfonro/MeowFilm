import { parentPort, workerData } from 'node:worker_threads';
import { DatabaseSync, backup } from 'node:sqlite';
import { lstatSync, chmodSync, openSync, closeSync, unlinkSync, realpathSync, fsyncSync } from 'node:fs';
import { dirname, isAbsolute } from 'node:path';
import { acquireNativeStorageLock as acquire } from './native-engine.js';

const { path, identity, initialize, role = 'writer' } = workerData;
const isReader = role === 'reader';
const fail = code => { throw Object.assign(new Error(code), { code }); };
const MAX_PREPARED_STATEMENTS = 256;
const MAX_CACHED_SQL_LENGTH = 4096;
const statementCache = new Map();
let db, fileIdentity, commits = 0, maintenanceFailure;
const enter = (exclusive, timeout = 5000) =>
  acquire(dirname(path) + '/maintenance.lock', exclusive, timeout);

function verifyFiles() {
  if (realpathSync(dirname(path)) !== dirname(path)) fail('UNSAFE_PATH');
  for (const suffix of ['', '-wal', '-shm', '-journal']) safeFile(path + suffix, suffix === '');
  const current = lstatSync(path, { bigint: true });
  if (current.ino !== fileIdentity.ino || current.dev !== fileIdentity.dev) fail('UNSAFE_PATH');
}

function prepare(sql) {
  if (sql.length > MAX_CACHED_SQL_LENGTH || /^\s*PRAGMA\b/i.test(sql)) {
    const statement = db.prepare(sql);
    statement.setReadBigInts(true);
    return statement;
  }
  let statement = statementCache.get(sql);
  if (statement) {
    statementCache.delete(sql);
    statementCache.set(sql, statement);
    return statement;
  }
  statement = db.prepare(sql);
  statement.setReadBigInts(true);
  if (statementCache.size >= MAX_PREPARED_STATEMENTS) {
    statementCache.delete(statementCache.keys().next().value);
  }
  statementCache.set(sql, statement);
  return statement;
}

function maintain() {
  let lease;
  try {
    lease = enter(true, 0);
    verifyFiles();
    const ckpt = query('PRAGMA wal_checkpoint(PASSIVE)');
    if (ckpt[0]?.busy === 0 && Number(db.prepare('PRAGMA freelist_count').get().freelist_count) >= 256)
      db.exec('PRAGMA incremental_vacuum(128)');
  } catch (error) {
    const err = code(error);
    if (err === 'UNSAFE_PATH' || err === 'STORAGE_CORRUPT') maintenanceFailure = err;
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
  const statement = prepare(sql);
  const rows = [];
  for (const row of statement.iterate(...parameters(params))) {
    if (rows.length >= maximum) fail('STORAGE_RESULT_LIMIT');
    const result = {};
    for (const [key, item] of Object.entries(row)) {
      result[key] = value(item);
    }
    rows.push(result);
  }
  return rows;
}

function execute(sql, params = [], expected) {
  if (typeof sql !== 'string') fail('INVALID_REQUEST');
  const result = prepare(sql).run(...parameters(params));
  const changes = Number(result.changes);
  if (expected !== undefined && changes !== expected) fail('STORAGE_CONFLICT');
  return { changes, lastInsertRowid: value(result.lastInsertRowid) };
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

if (isReader) {
  try {
    if (realpathSync(dirname(path)) !== dirname(path)) fail('UNSAFE_PATH');
    safeFile(path, true);
    db = new DatabaseSync(path, { readOnly: true, enableForeignKeyConstraints: true, allowExtension: false });
    db.exec('PRAGMA query_only=ON; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF; ' +
      'PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384;');
    if (db.prepare('PRAGMA query_only').get().query_only !== 1 ||
        db.prepare('PRAGMA trusted_schema').get().trusted_schema !== 0 ||
        db.prepare('PRAGMA foreign_keys').get().foreign_keys !== 1 ||
        db.prepare('PRAGMA busy_timeout').get().timeout !== 5000 ||
        db.prepare('PRAGMA temp_store').get().temp_store !== 2 ||
        db.prepare('PRAGMA cache_size').get().cache_size !== -16384) fail('STORAGE_CONFIGURATION');
    fileIdentity = lstatSync(path, { bigint: true });
    parentPort.postMessage({ id: 0, value: true });
  } catch (error) {
    statementCache.clear();
    try { db?.close(); } catch {}
    parentPort.postMessage({ id: 0, code: code(error) });
    parentPort.close();
  }
} else {
  let initializing, validating, createdFresh = false, committedSchema = false;
  try {
    initializing = acquire(dirname(path) + '/initialize.lock', true, 5000);
    validating = enter(false, 5000);
    if (realpathSync(dirname(path)) !== dirname(path)) fail('UNSAFE_PATH');
    const existing = safeFile(path);
    for (const suffix of ['-wal', '-shm', '-journal']) {
      if (safeFile(path + suffix) && !existing) fail('STORAGE_CORRUPT');
    }
    if (existing?.size === 0) fail('STORAGE_CORRUPT');
    if (!existing && !initialize) fail('STORAGE_SCHEMA');
    if (!existing) {
      closeSync(openSync(path, 'wx', 0o600));
      createdFresh = true;
    }
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
    db.exec('PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; ' +
      'PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; ' +
      'PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384;');
    if (db.prepare('PRAGMA journal_mode').get().journal_mode !== 'wal' ||
        db.prepare('PRAGMA synchronous').get().synchronous !== 2 ||
        db.prepare('PRAGMA foreign_keys').get().foreign_keys !== 1 ||
        db.prepare('PRAGMA trusted_schema').get().trusted_schema !== 0 ||
        db.prepare('PRAGMA wal_autocheckpoint').get().wal_autocheckpoint !== 0 ||
        db.prepare('PRAGMA auto_vacuum').get().auto_vacuum !== 2 ||
        db.prepare('PRAGMA busy_timeout').get().timeout !== 5000 ||
        db.prepare('PRAGMA temp_store').get().temp_store !== 2 ||
        db.prepare('PRAGMA cache_size').get().cache_size !== -16384) fail('STORAGE_CONFIGURATION');
    if (empty) {
      db.exec('BEGIN IMMEDIATE');
      try {
        db.exec(identity.sql);
        db.prepare('INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,?)').run(identity.hash);
        db.exec(`PRAGMA application_id=${identity.applicationId}; PRAGMA user_version=${identity.version};`);
        db.exec('COMMIT');
        committedSchema = true;
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
    fileIdentity = lstatSync(path, { bigint: true });
    parentPort.postMessage({ id: 0, value: true });
  } catch (error) {
    statementCache.clear();
    try { db?.close(); } catch {}
    if (createdFresh && !committedSchema) {
      for (const suffix of ['', '-wal', '-shm', '-journal']) {
        try { unlinkSync(path + suffix); } catch {}
      }
    }
    parentPort.postMessage({ id: 0, code: code(error) });
    parentPort.close();
  } finally { validating?.release(); initializing?.release(); }
}

parentPort.on('message', async ({ id, input }) => {
  if (input.kind === 'close') {
    try { statementCache.clear(); db.close(); parentPort.postMessage({ id, value: true }); }
    catch (error) { parentPort.postMessage({ id, code: code(error) }); }
    finally { parentPort.close(); }
    return;
  }
  let lease;
  try {
    if (maintenanceFailure) fail(maintenanceFailure);
    if (isReader) {
      let result;
      if (input.kind === 'query') result = query(input.sql, input.params, input.maxRows);
      else if (input.kind === 'snapshot') result = transaction(input.statements, true);
      else fail('STORAGE_READONLY');
      parentPort.postMessage({ id, value: result });
      return;
    }
    if (input.kind === 'checkpoint' || input.kind === 'backup') {
      lease = enter(true);
      verifyFiles();
    }
    const isWrite = input.kind === 'execute' || input.kind === 'transaction';
    const beforeChanges = isWrite ? prepare('SELECT total_changes() AS n').get().n : 0n;
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
    else fail('INVALID_REQUEST');
    const changed = isWrite && prepare('SELECT total_changes() AS n').get().n !== beforeChanges;
    parentPort.postMessage({ id, value: result });
    if (changed) ++commits;
  } catch (error) {
    parentPort.postMessage({ id, code: code(error) });
  } finally {
    lease?.release();
    if (!isReader && commits >= 64) { commits = 0; maintain(); }
  }
});
