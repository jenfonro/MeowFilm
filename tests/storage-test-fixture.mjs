import { openDatabase, configureStandaloneStorage } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
import { CatStorage } from '../packages/script-host/src/cat-storage.js';
import { PanStore } from '../packages/pan-runtime/src/store.js';

configureStandaloneStorage(createDesktopStorageDriver);

export async function withTestDatabase(root, action) {
  const database = openDatabase(root);
  try { return await action(database); }
  finally { await database.close(); }
}

export function setTestReadOnly(root, enabled) {
  return withTestDatabase(root, db => db.query(enabled ? 'PRAGMA query_only=ON' : 'PRAGMA query_only=OFF'));
}

export async function readPanFacts(root) {
  const storage = new PanStore(root);
  try { return await storage.read(); }
  finally { await storage.close(); }
}

export async function readCatFacts(root, mode = 'standalone') {
  const storage = new CatStorage(root, mode);
  try { await storage.ready; return storage.value; }
  finally { await storage.close(); }
}

export async function editCatFacts(root, change) {
  const storage = new CatStorage(root, 'standalone');
  try { await storage.ready; return await storage.save(change); }
  finally { await storage.close(); }
}

/** Hooks observe the real SQLite lane; no alternate persistence implementation. */
export function databaseProbe(root) {
  const probe = { before: null, after: null, requests: [] };
  probe.database = openDatabase(root, { driver(path, identity, initialize) {
    const driver = createDesktopStorageDriver(path, identity, initialize);
    return {
      ready: driver.ready,
      async request(input) {
        probe.requests.push(input);
        await probe.before?.(input);
        const result = await driver.request(input);
        await probe.after?.(input, result);
        return result;
      },
      close: () => driver.close(),
    };
  } });
  probe.metadataRead = input => input.kind === 'snapshot' &&
    input.statements[0].sql.includes('metadata_cache_entries');
  probe.metadataWrite = input => input.kind === 'transaction' &&
    input.statements.some(row => row.sql.includes('INSERT INTO metadata_cache_entries'));
  probe.metadataDelete = input => input.kind === 'execute' &&
    input.sql === 'DELETE FROM metadata_cache_entries WHERE provider=?';
  probe.entries = provider => probe.database.query(
    'SELECT * FROM metadata_cache_entries WHERE provider=? ORDER BY cache_key', [provider]);
  probe.close = () => probe.database.close();
  return probe;
}
