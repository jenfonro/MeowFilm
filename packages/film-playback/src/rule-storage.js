import { openDatabase } from '@meowfilm/storage';
import { reconcile } from '@meowfilm/storage/statements';

const columns = ['group_name', 'position', 'pattern', 'replacement', 'flags'];
const snapshot = [
  { sql: 'SELECT group_name,row_revision,overridden FROM film_standalone_rule_groups', rows: true, maxRows: 4 },
  { sql: `SELECT ${columns.join(',')} FROM film_standalone_rule_entries ORDER BY group_name,position`, rows: true },
];
const failure = code => Object.assign(new Error(code), { code });

function decode([groups, entries], defaults) {
  const rules = { ...defaults }, versions = new Map(), rows = new Map();
  for (const group of groups) {
    const key = group.group_name;
    if (!Object.hasOwn(defaults, key) || !Number.isSafeInteger(group.row_revision) || group.row_revision < 0)
      throw failure('CORRUPT_STORAGE');
    versions.set(key, group.row_revision);
    if (group.overridden) { rules[key] = []; rows.set(key, []); }
  }
  if (versions.size !== Object.keys(defaults).length) throw failure('CORRUPT_STORAGE');
  for (const row of entries) {
    const key = row.group_name, group = rows.get(key);
    if (!group || row.position !== group.length) throw failure('CORRUPT_STORAGE');
    group.push(columns.map(column => row[column]));
    const match = key === 'episodeRules' || key === 'movieRules';
    rules[key].push(match ? { pattern: row.pattern,
      ...(row.replacement === null ? {} : { replace: row.replacement }),
      ...(row.flags === null ? {} : { flags: row.flags }) } : row.pattern);
  }
  return { rules, versions, rows };
}

/** A standalone owner compares only edited groups. Its receipt includes siblings at the same commit. */
export function openRuleStorage(root) {
  const db = openDatabase(root);
  const translate = error => failure(error.code === 'STORAGE_CONFLICT' ? 'STORAGE_CHANGED' :
    ['CORRUPT_STORAGE', 'STORAGE_SCHEMA', 'STORAGE_CORRUPT'].includes(error.code) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
  return {
    async read(defaults) {
      try { return decode(await db.snapshot(snapshot), defaults); }
      catch (error) { throw translate(error); }
    },
    async save(previous, defaults, edits, signal) {
      const changes = [];
      for (const [key, values] of Object.entries(edits)) {
        const saved = values !== null;
        const rows = (values ?? []).map((row, i) => typeof row === 'string' ? [key, i, row, null, null] :
          [key, i, row.pattern, row.replace ?? null, row.flags ?? null]);
        const entries = reconcile('film_standalone_rule_entries', columns, ['group_name', 'position'], previous.rows.get(key) ?? [], rows);
        const changed = entries.length > 0 || saved !== previous.rows.has(key);
        changes.push({ sql: 'UPDATE film_standalone_rule_groups SET overridden=?,row_revision=row_revision+? WHERE group_name=? AND row_revision=?',
          params: [Number(saved), Number(changed), key, previous.versions.get(key)], expectChanges: 1 }, ...entries);
      }
      try { return decode((await db.transaction([...changes, ...snapshot], { signal })).slice(-2), defaults); }
      catch (error) { if (error.code === 'CANCELLED') throw failure('CANCELLED'); throw translate(error); }
    },
    close: () => db.close(),
  };
}
