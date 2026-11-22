import { isDeepStrictEqual } from 'node:util';
import { openDatabase } from '@meowfilm/storage';
import { upsert, nodeInserts } from '@meowfilm/storage/statements';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';

const OWNER = 'music.scripts';
const versionKeys = ['contentHash', 'bytes', 'version', 'author', 'description', 'homepage'];
const versionColumns = ['hash', 'bytes', 'version', 'author', 'description', 'homepage'];
const catalogColumns = [
  'installation_id', 'position', 'name', 'enabled', 'origin_kind', 'origin_url',
  ...['current', 'previous'].flatMap(prefix => versionColumns.map(key => prefix + '_' + key)),
  'declared_hash', 'check_hash', 'checked_at_ms', 'check_failure',
];

function installationRow(row, position) {
  return [
    row.id, position, row.name, Number(row.enabled), row.origin.kind, row.origin.url ?? null,
    ...['current', 'previous'].flatMap(prefix => versionKeys.map(key => row[prefix]?.[key] ?? null)),
    row.declared?.contentHash ?? null,
    row.check?.contentHash ?? null,
    row.check?.checkedAt ?? null,
    row.check?.failure ?? null,
  ];
}

function installationHeaderEqual(a, b) {
  return a.name === b.name && a.enabled === b.enabled &&
    a.origin.kind === b.origin.kind && (a.origin.url ?? null) === (b.origin.url ?? null) &&
    isDeepStrictEqual(a.current, b.current) && isDeepStrictEqual(a.previous, b.previous) &&
    (a.declared?.contentHash ?? null) === (b.declared?.contentHash ?? null) &&
    (a.check?.contentHash ?? null) === (b.check?.contentHash ?? null) &&
    (a.check?.checkedAt ?? null) === (b.check?.checkedAt ?? null) &&
    (a.check?.failure ?? null) === (b.check?.failure ?? null);
}

function declarationStatements(id, declared) {
  const statements = [{ sql: 'DELETE FROM music_script_declared_sites WHERE installation_id=?', params: [id] }];
  const capabilities = declared?.capabilities;
  if (!capabilities) return statements;
  for (const [index, [site, value]] of Object.entries(capabilities).entries()) {
    statements.push({
      sql: 'INSERT INTO music_script_declared_sites(installation_id,site_id,position,has_qualities) VALUES(?,?,?,?)',
      params: [id, site, index, Number(value.qualitys !== undefined)],
    });
    for (const [pos, action] of (value.actions ?? []).entries()) {
      statements.push({
        sql: 'INSERT INTO music_script_declared_actions(installation_id,site_id,position,action) VALUES(?,?,?,?)',
        params: [id, site, pos, action],
      });
    }
    for (const [pos, quality] of (value.qualitys ?? []).entries()) {
      statements.push({
        sql: 'INSERT INTO music_script_declared_qualities(installation_id,site_id,position,quality) VALUES(?,?,?,?)',
        params: [id, site, pos, quality],
      });
    }
    const extras = Object.fromEntries(Object.entries(value).filter(([key]) => !['actions', 'qualitys'].includes(key)));
    statements.push(...nodeInserts('music_script_declared_nodes', ['installation_id', 'site_id'], [id, site], encodeValueNodes(extras)));
  }
  return statements;
}

function checkStatements(id, check) {
  const statements = [{ sql: 'DELETE FROM music_script_checks WHERE installation_id=?', params: [id] }];
  for (const [site, value] of Object.entries(check?.sites ?? {})) {
    statements.push({
      sql: 'INSERT INTO music_script_checks(installation_id,site_id,state,quality,sample,code) VALUES(?,?,?,?,?,?)',
      params: [id, site, value.state, value.quality ?? null, value.sample ?? null, value.code ?? null],
    });
  }
  return statements;
}

export function createMusicStorage(dataRoot) {
  const database = openDatabase(dataRoot);
  const declarations = new WeakMap();
  let previous = { schema: 1, revision: 0, installations: [] };

  async function load() {
    const [state, installations, sites, actions, qualities, checks, nodes] = await database.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [OWNER], rows: true },
      { sql: `SELECT ${catalogColumns.join(',')} FROM music_script_installations ORDER BY position`, rows: true },
      { sql: 'SELECT installation_id,site_id,position,has_qualities FROM music_script_declared_sites ORDER BY installation_id,position', rows: true },
      { sql: 'SELECT installation_id,site_id,position,action FROM music_script_declared_actions ORDER BY installation_id,site_id,position', rows: true },
      { sql: 'SELECT installation_id,site_id,position,quality FROM music_script_declared_qualities ORDER BY installation_id,site_id,position', rows: true },
      { sql: 'SELECT installation_id,site_id,state,quality,sample,code FROM music_script_checks', rows: true },
      { sql: 'SELECT installation_id,site_id,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value FROM music_script_declared_nodes ORDER BY installation_id,site_id,node_id', rows: true, maxRows: Number.MAX_SAFE_INTEGER },
    ]);
    return previous = {
      schema: 1,
      revision: state[0]?.revision ?? 0,
      installations: installations.map(row => {
        const version = prefix => Object.fromEntries(versionKeys.map((key, index) => [key, row[prefix + '_' + versionColumns[index]]]));
        const declared = row.declared_hash === null ? null : { contentHash: row.declared_hash };
        if (declared) {
          declarations.set(declared, {
            sites: sites.filter(item => item.installation_id === row.installation_id),
            actions: actions.filter(item => item.installation_id === row.installation_id),
            qualities: qualities.filter(item => item.installation_id === row.installation_id),
            nodes: nodes.filter(item => item.installation_id === row.installation_id),
          });
        }
        return {
          id: row.installation_id,
          name: row.name,
          enabled: !!row.enabled,
          origin: row.origin_kind === 'file' ? { kind: 'file' } : { kind: 'network', url: row.origin_url },
          current: version('current'),
          previous: row.previous_hash === null ? null : version('previous'),
          declared,
          check: row.check_hash === null ? null : {
            contentHash: row.check_hash,
            checkedAt: row.checked_at_ms,
            failure: row.check_failure,
            sites: Object.fromEntries(checks.filter(item => item.installation_id === row.installation_id).map(item => [
              item.site_id,
              { state: item.state, quality: item.quality, sample: item.sample, code: item.code },
            ])),
          },
        };
      }),
    };
  }

  function readDeclaration(declared) {
    const retained = declarations.get(declared);
    if (!retained) return declared;
    const { sites, actions, qualities, nodes } = retained;
    const capabilities = Object.fromEntries(sites.map(site => [site.site_id, {
      actions: actions.filter(item => item.site_id === site.site_id).map(item => item.action),
      ...(site.has_qualities ? { qualitys: qualities.filter(item => item.site_id === site.site_id).map(item => item.quality) } : {}),
      ...decodeValueNodes(nodes.filter(item => item.site_id === site.site_id), { rawNumbers: true }),
    }]));
    return { contentHash: declared.contentHash, capabilities };
  }

  async function save(next) {
    const oldById = new Map(previous.installations.map((row, idx) => [row.id, { row, idx }]));
    const nextIds = new Set(next.installations.map(row => row.id));
    const gate = previous.revision
      ? { sql: 'UPDATE owner_revisions SET revision=? WHERE owner=? AND revision=?', params: [next.revision, OWNER, previous.revision], expectChanges: 1 }
      : { sql: 'INSERT INTO owner_revisions(owner,revision) VALUES(?,?)', params: [OWNER, next.revision], expectChanges: 1 };
    const statements = [gate];

    for (const [id] of oldById) {
      if (!nextIds.has(id)) {
        statements.push(
          { sql: 'DELETE FROM music_script_kv_entries WHERE installation_id=?', params: [id] },
          { sql: 'DELETE FROM music_script_installations WHERE installation_id=?', params: [id] },
        );
      }
    }

    for (let i = 0; i < next.installations.length; i++) {
      const row = next.installations[i];
      const old = oldById.get(row.id);
      if (!old || old.idx !== i || !installationHeaderEqual(old.row, row)) {
        statements.push(upsert('music_script_installations', catalogColumns, installationRow(row, i), ['installation_id']));
      }
      if (!old || (row.declared !== old.row.declared && !declarations.has(row.declared))) {
        if (row.declared === null) {
          if (old?.row.declared !== null) {
            statements.push({ sql: 'DELETE FROM music_script_declared_sites WHERE installation_id=?', params: [row.id] });
          }
        } else {
          statements.push(...declarationStatements(row.id, row.declared));
        }
      }
      if (!old || !isDeepStrictEqual(old.row.check?.sites ?? null, row.check?.sites ?? null)) {
        statements.push(...checkStatements(row.id, row.check));
      }
    }

    await database.transaction(statements);
    previous = next;
  }

  const queues = new Map();
  function kv(operation, input, check) {
    const id = input.installationId;
    const work = (queues.get(id) ?? Promise.resolve()).then(async () => {
      check();
      const scope = [id, input.siteId, input.key];
      const where = 'installation_id=? AND site_id=? AND entry_key=?';
      if (operation === 'get') {
        const nodes = await database.query(
          'SELECT node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value FROM music_script_kv_nodes WHERE ' + where + ' ORDER BY node_id',
          scope,
          { signal: input.signal, maxRows: Number.MAX_SAFE_INTEGER },
        );
        check();
        return decodeValueNodes(nodes, { rawNumbers: true }) ?? null;
      }
      const changes = [{ sql: 'DELETE FROM music_script_kv_entries WHERE ' + where, params: scope }];
      if (operation === 'set') {
        changes.push({ sql: 'INSERT INTO music_script_kv_entries(installation_id,site_id,entry_key) VALUES(?,?,?)', params: scope });
        changes.push(...nodeInserts('music_script_kv_nodes', ['installation_id', 'site_id', 'entry_key'], scope, encodeValueNodes(input.value)));
      }
      check();
      await database.transaction(changes, { signal: input.signal });
      return null;
    });
    const observed = work.catch(() => {});
    queues.set(id, observed);
    observed.finally(() => { if (queues.get(id) === observed) queues.delete(id); });
    return work;
  }

  return {
    load, save, readDeclaration, kv,
    async close() { await Promise.allSettled([...queues.values()]); await database.close(); },
  };
}
