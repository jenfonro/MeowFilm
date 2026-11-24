import { isAbsolute } from 'node:path';
import { openDatabase } from '@meowfilm/storage';
import { PanError, requireValue } from './errors.js';
import { object } from './values.js';

const fields = ['cookie', 'username', 'password', 'authorization', 'refreshedAt'];
const columns = ['cookie', 'username', 'password', 'authorization', 'refreshed_at_ms'];
const allowed = { baidu: ['cookie'], quark: ['cookie'], uc: ['cookie'],
  tianyi: ['cookie', 'username', 'password'], yidong: ['authorization', 'refreshedAt'] };
const integer = value => Number.isSafeInteger(value) && value >= 0;
const selectAccounts = 'SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms FROM pan_accounts WHERE account_revision>0';
function accounts(rows) {
  return { version: 1, accounts: Object.fromEntries(rows.map(row => [row.provider, {
    revision: row.account_revision, rowRevision: row.row_revision,
    credentials: Object.fromEntries(fields.flatMap((field, index) =>
      row[columns[index]] === null ? [] : [[field, row[columns[index]]]])),
  }])) };
}
const fault = error => error?.code === 'STORAGE_CONFLICT' ? error : new PanError(
  ['STORAGE_CORRUPT', 'CORRUPT_STORAGE', 'STORAGE_SCHEMA'].includes(error?.code) ? 'CORRUPT_STORAGE' :
    error?.code === 'STORAGE_CLOSED' ? 'CLOSED' : 'STORAGE_ERROR');

/** Standalone account owner. App accounts use the native owner's restricted port. */
export class PanStore {
  #database;

  constructor(dataRoot) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot) &&
      !dataRoot.includes('\0') && !dataRoot.split(/[\\/]/).includes('..'));
    this.#database = openDatabase(dataRoot);
  }

  async read() {
    try {
      return accounts(await this.#database.query(selectAccounts));
    } catch (error) { throw fault(error); }
  }

  reload() { return this.read(); }

  async update(command) {
    requireValue(object(command) && Object.hasOwn(allowed, command.provider) &&
      ['replace', 'refresh'].includes(command.mode) && integer(command.accountRevision) &&
      integer(command.rowRevision) && object(command.credentials), 'STORAGE_ERROR');
    const { provider, credentials, accountRevision, rowRevision, mode } = command;
    requireValue(Object.keys(credentials).every(key => allowed[provider].includes(key)), 'STORAGE_ERROR');
    const values = fields.map(field => {
      const value = credentials[field] ?? null;
      requireValue(value === null || (field === 'refreshedAt' ? integer(value) :
        typeof value === 'string' && !value.includes('\0') && (field === 'password' || !/[\r\n]/.test(value))),
      'STORAGE_ERROR');
      return value;
    });
    const next = accountRevision + (mode === 'replace' ? 1 : 0);
    requireValue(integer(next) && integer(rowRevision + 1), 'STORAGE_ERROR');
    try {
      const [, rows] = await this.#database.transaction([
        { sql: 'UPDATE pan_accounts SET account_revision=?,row_revision=row_revision+1,' +
            columns.map(key => key + '=?').join(',') + ' WHERE provider=? AND account_revision=? AND row_revision=?',
          params: [next, ...values, provider, accountRevision, rowRevision], expectChanges: 1 },
        { sql: selectAccounts, rows: true, maxRows: 5 },
      ]);
      return accounts(rows);
    } catch (error) { throw fault(error); }
  }

  close() { return this.#database.close(); }
}
