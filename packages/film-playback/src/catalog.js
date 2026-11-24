import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { checkSource, checkTarget, clone, fields, identity, isIdentifier, object, requireValue,
  resultDetailId, sourceIdentity, text } from './identity.js';
import { candidateIdentity, candidateResourceIdentity } from './recognition.js';

const MAX_PERSISTED_GROUPS = 2048;
const metadataId = (provider, kind, id) => `${provider}:${kind}:${id}`;
const positiveMetadataId = value => /^[1-9][0-9]*$/.test(String(value));
const stableId = id => /^(?:tmdb|douban):(?:movie|tv):[1-9][0-9]*$|^(?:source|group):[a-f0-9]{64}$/.test(id);
const coordinate = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;

function sourceMember(source, detailId) {
  requireValue(source && ['cat', 'source'].includes(source.engine ?? 'cat') &&
    text(source.installationId) && text(source.siteId) && isIdentifier(detailId));
  return identity(sourceIdentity(source), detailId);
}

function indexGroups(groups) {
  const byKey = new Map();
  const byId = new Map();
  const byMember = new Map();
  for (const row of groups) {
    if (!object(row)) continue;
    if (typeof row.id === 'string' && !byId.has(row.id)) byId.set(row.id, row);
    if (Array.isArray(row.keys)) {
      for (const key of row.keys) if (key && !byKey.has(key)) byKey.set(key, row);
    }
    if (Array.isArray(row.members)) {
      for (const member of row.members) if (member && !byMember.has(member)) byMember.set(member, row);
    }
  }
  return { byKey, byId, byMember };
}

function findGroupRecord(index, identities, key, preferred) {
  if (key && index.byKey.has(key)) return index.byKey.get(key);
  if (preferred && index.byId.has(preferred)) return index.byId.get(preferred);
  for (const member of identities) {
    if (index.byMember.has(member)) return index.byMember.get(member);
  }
  return undefined;
}

function groupNeedsMutation(record, identities, key, persistNew) {
  if (!record) return persistNew;
  requireValue(/^group:[a-f0-9]{64}$/.test(record.id) && Array.isArray(record.keys) &&
    Array.isArray(record.members), 'CORRUPT_STORAGE');
  if (key && !record.keys.includes(key)) return true;
  return identities.some(member => !record.members.includes(member));
}

function registerGroup(identities, groups, index, key, preferred, { persistNew = true } = {}) {
  let record = findGroupRecord(index, identities, key, preferred);
  if (!record) {
    const id = `group:${identity('group', identities[0])}`;
    if (!persistNew) return id;
    record = { id, keys: [], members: [] };
    groups.push(record);
    index.byId.set(id, record);
  }
  requireValue(/^group:[a-f0-9]{64}$/.test(record.id) && Array.isArray(record.keys) &&
    Array.isArray(record.members), 'CORRUPT_STORAGE');
  if (key && !record.keys.includes(key)) {
    record.keys.push(key);
    if (!index.byKey.has(key)) index.byKey.set(key, record);
  }
  for (const member of identities) {
    if (!record.members.includes(member)) {
      record.members.push(member);
      if (!index.byMember.has(member)) index.byMember.set(member, record);
    }
  }
  while (groups.length > MAX_PERSISTED_GROUPS) {
    const evicted = groups.shift();
    if (!object(evicted)) continue;
    if (typeof evicted.id === 'string' && index.byId.get(evicted.id) === evicted) {
      index.byId.delete(evicted.id);
    }
    if (Array.isArray(evicted.keys)) {
      for (const evictedKey of evicted.keys) {
        if (index.byKey.get(evictedKey) === evicted) index.byKey.delete(evictedKey);
      }
    }
    if (Array.isArray(evicted.members)) {
      for (const evictedMember of evicted.members) {
        if (index.byMember.get(evictedMember) === evicted) index.byMember.delete(evictedMember);
      }
    }
  }
  return record.id;
}

/** Stable content addressing belongs to the film owner, not a search session or a view. */
export function createFilmCatalog({ library } = {}) {
  requireValue(library && typeof library.read === 'function' && typeof library.update === 'function');

  function memberIdentities(members, sources) {
    requireValue(Array.isArray(members) && members.length);
    const identities = [...new Set(members.map(member =>
      sourceMember(sources.find(source => source.key === member.sourceKey), member.detailId)))].sort();
    requireValue(identities.length);
    return identities;
  }

  function resolveNonGroupTarget(value, sources) {
    const result = clone(value);
    if (value.metadataProvider) {
      requireValue(['tmdb', 'douban'].includes(value.metadataProvider) && positiveMetadataId(value.metadataId));
      result.id = metadataId(value.metadataProvider, value.kind, value.metadataId);
    } else if (value.kind === 'source') {
      if (value.source?.engine === 'pan') {
        const manual = library.read().manual.find(row => row?.share && row.enabled !== false &&
          `manual:${row.id}` === value.source.key && row.id === value.source.detailId && row.targetId === value.id);
        requireValue(manual, 'SOURCE_NOT_FOUND');
        result.source = { ...value.source, engine: 'pan' };
      } else {
        const source = sources.find(source => source.key === value.source?.key);
        const member = sourceMember(source, value.source?.detailId);
        result.id = stableId(value.id ?? '') ? value.id : `source:${member}`;
        result.source = { ...fields(source, ['key', 'engine', 'installationId', 'siteId']), ...value.source };
      }
    } else requireValue(false);
    return checkTarget(result);
  }

  async function resolve(value, sources = []) {
    requireValue(Array.isArray(sources));
    requireValue(object(value) && text(value.title) && ['movie', 'tv', 'source', 'group'].includes(value.kind));
    if (value.kind !== 'group') return resolveNonGroupTarget(value, sources);
    const identities = memberIdentities(value.members, sources);
    const currentGroups = library.read().groups;
    const readIndex = indexGroups(currentGroups);
    const existing = findGroupRecord(readIndex, identities, undefined, value.id);
    if (existing && !groupNeedsMutation(existing, identities, undefined, true)) {
      return checkTarget({ ...clone(value), id: existing.id });
    }
    let resolvedId;
    await library.update(document => {
      const writeIndex = indexGroups(document.groups);
      resolvedId = registerGroup(identities, document.groups, writeIndex, undefined, value.id, { persistNew: true });
    });
    return checkTarget({ ...clone(value), id: resolvedId });
  }

  async function search(cards, sources, revision) {
    const readIndex = indexGroups(library.read().groups);
    const pendingGroups = [];
    const result = cards.map((card, cardIndex) => {
      const members = (card.aggregateItems?.length ? card.aggregateItems : card.sourceKind === 'site' ? [card] : [])
        .map(row => ({ sourceKey: row.siteKey, detailId: row.detailId }));
      const raw = card.sourceKind === 'site' && card.displayKey.startsWith('site:');
      const value = card.sourceKind === 'tmdb' ? { kind: card.tmdbType, title: text(card.title),
        metadataProvider: 'tmdb', metadataId: String(card.tmdbId), ...(members.length ? { members } : {}) }
        : raw ? { kind: 'source', title: text(card.title), source: { key: card.siteKey, detailId: card.detailId } }
          : { kind: 'group', title: text(card.title), members };
      if (value.kind !== 'group') {
        const resolved = resolveNonGroupTarget(value, sources);
        return { ...card, businessId: resolved.id, playbackTarget: resolved };
      }
      requireValue(text(value.title));
      const identities = memberIdentities(value.members, sources);
      const key = identity('aggregation', revision, card.groupKey);
      const persistNew = identities.length > 1;
      const existing = findGroupRecord(readIndex, identities, key, value.id);
      if (groupNeedsMutation(existing, identities, key, persistNew)) {
        pendingGroups.push({ cardIndex, value, identities, key, persistNew });
      }
      const id = existing ? existing.id : `group:${identity('group', identities[0])}`;
      const resolved = checkTarget({ ...clone(value), id });
      return { ...card, businessId: resolved.id, playbackTarget: resolved };
    });
    if (pendingGroups.length) {
      await library.update(document => {
        const writeIndex = indexGroups(document.groups);
        for (const item of pendingGroups) {
          const id = registerGroup(item.identities, document.groups, writeIndex, item.key, item.value.id,
            { persistNew: item.persistNew });
          const resolved = checkTarget({ ...clone(item.value), id });
          result[item.cardIndex] = { ...result[item.cardIndex], businessId: resolved.id, playbackTarget: resolved };
        }
      });
    }
    return result;
  }

  function metadata(provider, path, value, contentTarget) {
    const detail = /^\/(?:3|rexxar\/api\/v2)\/(movie|tv)\/([1-9]\d*)(?:\/season\/(\d+))?$/.exec(path);
    const lane = /\/(?:search|discover|recent_hot|trending)\/(movie|tv)(?:\/|$)/.exec(path)?.[1];
    function row(record, kind, id = record?.id, parent, season, episode) {
      if (!object(record) || !['movie', 'tv'].includes(kind) || !positiveMetadataId(id)) return record;
      const title = text(parent?.title ?? parent?.name ?? record.title ?? record.name ?? contentTarget?.title);
      if (!title) return record;
      const base = metadataId(provider, kind, id);
      const playbackTarget = { id: base, kind, title, metadataProvider: provider, metadataId: String(id),
        ...(season === undefined ? {} : { season }), ...(episode === undefined ? {} : { episode }) };
      const businessId = base + (season === undefined ? '' : `:season:${season}`) +
        (episode === undefined ? '' : `:episode:${episode}`);
      const projected = { ...record, businessId, playbackTarget };
      if (kind === 'tv' && Array.isArray(record.seasons)) projected.seasons = record.seasons.map(item => {
        if (!object(item)) return item;
        const seasonNum = coordinate(item.season_number ?? item.season);
        return seasonNum === undefined ? item : row(item, kind, id, record, seasonNum);
      });
      if (kind === 'tv' && Array.isArray(record.doubanSeasons)) projected.doubanSeasons = record.doubanSeasons.map(item => {
        if (!object(item)) return item;
        const seasonNum = coordinate(item.season_number ?? item.season);
        return seasonNum === undefined ? item : row(item, kind, item.metadataId ?? id, record, seasonNum);
      });
      if (kind === 'tv' && Array.isArray(record.episodes) && season !== undefined) projected.episodes = record.episodes.map(item => {
        if (!object(item)) return item;
        const episodeNum = coordinate(item.episode_number ?? item.episode);
        return episodeNum === undefined ? item : row(item, kind, id, parent ?? record, season, episodeNum);
      });
      for (const name of ['last_episode_to_air', 'next_episode_to_air']) {
        if (object(record[name])) {
          const epSeason = coordinate(record[name].season_number);
          const epNumber = coordinate(record[name].episode_number);
          if (epSeason !== undefined && epNumber !== undefined) {
            projected[name] = row(record[name], kind, id, record, epSeason, epNumber);
          }
        }
      }
      for (const name of ['similar', 'recommendations']) {
        const related = record[name];
        if (object(related) && Array.isArray(related.results)) projected[name] = {
          ...related, results: related.results.map(item => row(item, item.media_type ?? kind)),
        };
      }
      return projected;
    }
    if (!object(value)) return value;
    let result = detail ? row(value, detail[1], detail[2], undefined,
      detail[3] === undefined ? undefined : coordinate(Number(detail[3]))) : value;
    if (Array.isArray(value.results)) result = { ...result,
      results: value.results.map(item => row(item, item.media_type ?? lane)) };
    if (Array.isArray(value.items)) result = { ...result,
      items: value.items.map(item => row(item, item.type ?? lane)) };
    if (provider === 'douban' && Array.isArray(value.subjects?.items)) result = { ...result,
      subjects: { ...value.subjects, items: value.subjects.items.map(item => {
        const projected = row(item.target, item.target_type ?? item.target?.type, item.target_id ?? item.target?.id);
        return projected === item.target ? item : { ...item, target: projected };
      }) } };
    return result;
  }

  function sourceRows(source, value) {
    if (!object(value)) return value;
    source = checkSource(source);
    const name = Array.isArray(value.list) ? 'list' : Array.isArray(value.items) ? 'items' : null;
    if (!name) return value;
    return { ...value, [name]: value[name].map(row => {
      const detailId = resultDetailId(row);
      const title = text(row?.vod_name ?? row?.title ?? row?.name);
      if (!object(row) || !isIdentifier(detailId) || !title) return row;
      const playbackTarget = { id: `source:${sourceMember(source, detailId)}`, kind: 'source', title,
        source: { ...fields(source, ['key', 'engine', 'installationId', 'siteId']), detailId } };
      return { ...row, businessId: playbackTarget.id, playbackTarget };
    }) };
  }

  function candidate(parent, item) {
    const source = item.source;
    const playbackTarget = { id: parent.id, kind: 'source', title: parent.title,
      ...fields(item, ['season', 'episode', 'globalEpisode']),
      source: { ...fields(source, ['key', 'engine', 'installationId', 'siteId']),
        detailId: item.detailId ?? source.detailId, lineId: item.lineId, candidateId: item.id } };
    return { businessId: item.id, playbackTarget };
  }

  function source(parent, value, title = parent.title) {
    const id = value.engine === 'pan' ? identity('pan', value.key, value.detailId) : sourceMember(value, value.detailId);
    const playbackTarget = { id: parent.id, kind: 'source', title,
      source: { ...fields(value, ['key', 'engine', 'installationId', 'siteId']), detailId: value.detailId } };
    return { businessId: `source:${id}`, playbackTarget };
  }

  function line(parent, value) {
    return { businessId: `line:${identity(parent.businessId, value.id)}`,
      playbackTarget: { ...parent.playbackTarget, source: { ...parent.playbackTarget.source, lineId: value.id } } };
  }

  function file(parent, source, lineId, value) {
    const scope = [source.key, source.engine ?? null, source.installationId ?? null, source.siteId ?? null];
    const resource = candidateResourceIdentity(value.file, value.share, scope, source.detailId, lineId, value.playId);
    return candidate(parent, { ...fields(value, ['season', 'episode', 'globalEpisode']),
      id: candidateIdentity(scope, source.detailId, lineId, resource), source, lineId });
  }

  function content(value) {
    const match = /^(tmdb|douban):(movie|tv):([1-9][0-9]*)$/.exec(value.id);
    return match ? { id: value.id, kind: match[2], title: value.title,
      metadataProvider: match[1], metadataId: match[3] } : null;
  }

  function history(value) {
    if (!object(value)) return { errorCode: 'CORRUPT_STORAGE' };
    if (value.errorCode) return cloneJSONValue(value);
    return { ...cloneJSONValue(value), businessId: value.targetId };
  }

  function directory(source, value) {
    source = checkSource(source);
    const detailId = value.detailId ?? source.detailId;
    const raw = Array.isArray(value.raw?.list)
      ? value.raw.list.find(row => (row?.vod_id ?? row?.id) === detailId) : value.raw;
    const title = text(raw?.vod_name ?? raw?.title ?? raw?.name) || source.name;
    const parent = { id: `source:${sourceMember(source, detailId)}`, kind: 'source', title,
      source: { ...fields(source, ['key', 'engine', 'installationId', 'siteId']), detailId } };
    const projected = { businessId: parent.id, playbackTarget: parent };
    return { ...value, businessId: parent.id, playbackTarget: parent, lines: value.lines.map(valueLine => ({
      ...valueLine, ...line(projected, valueLine),
      episodes: valueLine.episodes.map(item => ({ ...item, ...file(parent, { ...source, detailId }, valueLine.id, item) })),
    })) };
  }

  return { resolve, search, metadata, sourceRows, candidate, source, line, file, content, history, directory };
}
