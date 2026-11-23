import { isDeepStrictEqual } from 'node:util';
import { openDatabase } from '@meowfilm/storage';
import { upsert } from '@meowfilm/storage/statements';

const columns = ['group_name', 'position', 'pattern', 'replacement', 'flags'];
const snapshot = [
  { sql: 'SELECT group_name,row_revision,overridden FROM film_rule_groups', rows: true, maxRows: 4 },
  { sql: `SELECT ${columns.join(',')} FROM film_rules ORDER BY group_name,position`, rows: true },
];
const failure = code => Object.assign(new Error(code), { code });

function decode([groups, entries], defaults) {
  const rules = { ...defaults }, versions = new Map(), overridden = new Set();
  for (const group of groups) {
    const key = group.group_name;
    if (!Object.hasOwn(defaults, key) || !Number.isSafeInteger(group.row_revision) || group.row_revision < 0)
      throw failure('CORRUPT_STORAGE');
    versions.set(key, group.row_revision);
    if (group.overridden) {
      overridden.add(key);
      rules[key] = [];
    }
  }
  if (versions.size !== Object.keys(defaults).length) throw failure('CORRUPT_STORAGE');
  for (const row of entries) {
    const key = row.group_name;
    if (!overridden.has(key) || row.position !== rules[key].length) throw failure('CORRUPT_STORAGE');
    const match = key === 'episodeRules' || key === 'movieRules';
    rules[key].push(match ? {
      pattern: row.pattern,
      ...(row.replacement === null ? {} : { replace: row.replacement }),
      ...(row.flags === null ? {} : { flags: row.flags }),
    } : row.pattern);
  }
  return { rules, versions, overridden };
}

/** A standalone owner updates only edited groups with direct targeted SQL mutations. */
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
        const wasSaved = previous.overridden.has(key);
        const oldList = wasSaved ? previous.rules[key] : [];
        const nextList = saved ? values : [];
        const statements = [];
        if (nextList.length < oldList.length) {
          statements.push({
            sql: 'DELETE FROM film_rules WHERE group_name=? AND position>=?',
            params: [key, nextList.length],
          });
        }
        for (let i = 0; i < nextList.length; i++) {
          if (i < oldList.length && isDeepStrictEqual(oldList[i], nextList[i])) continue;
          const row = nextList[i];
          const params = typeof row === 'string'
            ? [key, i, row, null, null]
            : [key, i, row.pattern, row.replace ?? null, row.flags ?? null];
          statements.push(upsert('film_rules', columns, params, ['group_name', 'position']));
        }
        const changed = statements.length > 0 || saved !== wasSaved;
        changes.push(
          {
            sql: 'UPDATE film_rule_groups SET overridden=?,row_revision=row_revision+? WHERE group_name=? AND row_revision=?',
            params: [Number(saved), Number(changed), key, previous.versions.get(key)],
            expectChanges: 1,
          },
          ...statements,
        );
      }
      try { return decode((await db.transaction([...changes, ...snapshot], { signal })).slice(-2), defaults); }
      catch (error) { if (error.code === 'CANCELLED') throw failure('CANCELLED'); throw translate(error); }
    },
    close: () => db.close(),
  };
}
