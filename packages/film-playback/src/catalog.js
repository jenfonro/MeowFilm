import { checkSource, checkTarget, clone, identity, isIdentifier, object, requireValue,
  resultDetailId, sourceIdentity, text } from './identity.js';
import { candidateIdentity, candidateResourceIdentity } from './recognition.js';

const metadataId = (provider, kind, id) => `${provider}:${kind}:${id}`;
const stableId = id => /^(?:tmdb|douban):(?:movie|tv):[0-9]+$|^(?:source|group):[a-f0-9]{64}$/.test(id);
const coordinate = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
const fields = (value, keys) => Object.fromEntries(keys.filter(key => value[key] !== undefined).map(key => [key, value[key]]));

function sourceMember(source, detailId) {
  requireValue(source && ['cat', 'source'].includes(source.engine ?? 'cat') &&
    text(source.installationId) && text(source.siteId) && isIdentifier(detailId));
  return identity(sourceIdentity(source), detailId);
}

function relocate(document, previous, next) {
  if (!previous || previous === next) return;
  for (const name of ['history', 'manual', 'blocks'])
    for (const row of document[name]) if (row.targetId === previous) {
      row.targetId = next;
      if (row.playbackTarget?.id === previous) row.playbackTarget.id = next;
    }
  const latest = new Map();
  for (const row of document.history)
    if (!latest.has(row.targetId) || row.updatedAt > latest.get(row.targetId).updatedAt) latest.set(row.targetId, row);
  document.history = [...latest.values()];
  document.blocks = [...new Map(document.blocks.map(row =>
    [JSON.stringify([row.targetId, row.sourceKey, row.detailId, row.lineId]), row])).values()];
  document.previewTargets = [...new Set(document.previewTargets.map(id => id === previous ? next : id))];
}

/** Stable content addressing belongs to the film owner, not a search session or a view. */
export function createFilmCatalog({ library } = {}) {
  requireValue(library && typeof library.read === 'function' && typeof library.update === 'function');

  function group(members, sources, groups, key, preferred) {
    const identities = [...new Set(members.map(member =>
      sourceMember(sources.find(source => source.key === member.sourceKey), member.detailId)))].sort();
    requireValue(identities.length);
    let record = groups.find(row => key && row.keys.includes(key)) ??
      groups.find(row => row.id === preferred) ??
      groups.find(row => identities.some(member => row.members.includes(member)));
    if (!record) {
      record = { id: /^group:[a-f0-9]{64}$/.test(preferred ?? '') ? preferred
        : `group:${identity('group', identities[0])}`, keys: [], members: [] };
      groups.push(record);
    }
    if (key && !record.keys.includes(key)) record.keys.push(key);
    for (const member of identities) if (!record.members.includes(member)) record.members.push(member);
    return record.id;
  }

  function target(value, sources, groups, aggregationKey) {
    requireValue(object(value) && text(value.title) && ['movie', 'tv', 'source', 'group'].includes(value.kind));
    const result = clone(value);
    if (value.kind === 'group') {
      requireValue(Array.isArray(value.members) && value.members.length);
      result.id = group(value.members, sources, groups, aggregationKey, value.id);
    } else if (value.metadataProvider) {
      requireValue(['tmdb', 'douban'].includes(value.metadataProvider) &&
        /^[0-9]+$/.test(String(value.metadataId)));
      result.id = stableId(value.id ?? '') ? value.id : metadataId(value.metadataProvider, value.kind, value.metadataId);
    } else if (value.kind === 'source') {
      if (value.source?.engine === 'pan') {
        const manual = library.read().manual.find(row => row.share && row.enabled !== false &&
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

  function commit(groups, moves) {
    const current = library.read();
    const changed = JSON.stringify(current.groups) !== JSON.stringify(groups);
    const relocation = moves.some(([old, id]) => old !== id && ['history', 'manual', 'blocks'].some(name =>
      current[name].some(row => row.targetId === old)) || old !== id && current.previewTargets.includes(old));
    if (!changed && !relocation) return;
    library.update(document => {
      document.groups = groups;
      for (const [old, id] of moves) relocate(document, old, id);
    });
  }

  function resolve(value, sources = []) {
    requireValue(Array.isArray(sources));
    const groups = clone(library.read().groups);
    const resolved = target(value, sources, groups);
    commit(groups, [[value.id, resolved.id]]);
    return resolved;
  }

  function search(cards, sources, revision) {
    const groups = clone(library.read().groups), moves = [];
    const result = cards.map(card => {
      const members = (card.aggregateItems?.length ? card.aggregateItems : card.sourceKind === 'site' ? [card] : [])
        .map(row => ({ sourceKey: row.siteKey, detailId: row.detailId }));
      const raw = card.sourceKind === 'site' && card.displayKey.startsWith('site:');
      const value = card.sourceKind === 'tmdb' ? {
        kind: card.tmdbType, title: text(card.title), metadataProvider: 'tmdb', metadataId: String(card.tmdbId),
        ...(members.length ? { members } : {}),
      } : raw ? { kind: 'source', title: text(card.title),
        source: { key: card.siteKey, detailId: card.detailId } }
        : { kind: 'group', title: text(card.title), members };
      const key = value.kind === 'group' ? identity('aggregation', revision, card.groupKey) : undefined;
      const resolved = target(value, sources, groups, key);
      const previous = card.displayKey.startsWith('group:') ? `group:${JSON.stringify(card.displayKey.slice(6))}` : card.displayKey;
      moves.push([previous, resolved.id]);
      return { ...card, businessId: resolved.id, playbackTarget: resolved };
    });
    commit(groups, moves);
    return result;
  }

  function metadata(provider, path, value, contentTarget) {
    const detail = /^\/(?:3|rexxar\/api\/v2)\/(movie|tv)\/(\d+)(?:\/season\/(\d+))?$/.exec(path);
    const lane = /\/(?:search|discover|recent_hot|trending)\/(movie|tv)(?:\/|$)/.exec(path)?.[1];
    function row(record, kind, id = record?.id, parent, season, episode) {
      if (!object(record) || !['movie', 'tv'].includes(kind) || !/^[0-9]+$/.test(String(id))) return record;
      const title = text(parent?.title ?? parent?.name ?? record.title ?? record.name ?? contentTarget?.title);
      if (!title) return record;
      const base = metadataId(provider, kind, id);
      const playbackTarget = { id: base, kind, title, metadataProvider: provider, metadataId: String(id),
        ...(season === undefined ? {} : { season }), ...(episode === undefined ? {} : { episode }) };
      const businessId = base + (season === undefined ? '' : `:season:${season}`) +
        (episode === undefined ? '' : `:episode:${episode}`);
      const projected = { ...record, businessId, playbackTarget };
      if (kind === 'tv' && Array.isArray(record.seasons)) projected.seasons = record.seasons.map(item =>
        row(item, kind, id, record, coordinate(item.season_number ?? item.season)));
      if (kind === 'tv' && Array.isArray(record.doubanSeasons)) projected.doubanSeasons = record.doubanSeasons.map(item =>
        row(item, kind, item.metadataId ?? id, record, coordinate(item.season_number ?? item.season)));
      if (kind === 'tv' && Array.isArray(record.episodes) && season !== undefined) projected.episodes = record.episodes.map(item =>
        row(item, kind, id, parent ?? record, season, coordinate(item.episode_number ?? item.episode)));
      for (const name of ['last_episode_to_air', 'next_episode_to_air'])
        if (object(record[name])) projected[name] = row(record[name], kind, id, record,
          coordinate(record[name].season_number), coordinate(record[name].episode_number));
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
      detail[3] === undefined ? undefined : Number(detail[3])) : value;
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
    const scope = [source.key, source.engine, source.installationId, source.siteId];
    const resource = candidateResourceIdentity(value.file, value.share, scope, source.detailId, lineId, value.playId);
    return candidate(parent, { ...fields(value, ['season', 'episode', 'globalEpisode']),
      id: candidateIdentity(scope, source.detailId, lineId, resource), source, lineId });
  }

  function content(value) {
    const match = /^(tmdb|douban):(movie|tv):([0-9]+)$/.exec(value.id);
    return match ? { id: value.id, kind: match[2], title: value.title,
      metadataProvider: match[1], metadataId: match[3] } : null;
  }

  function history(value) {
    const playbackTarget = value.playbackTarget ?? { id: value.targetId, kind: 'source', title: value.title,
      ...value.episode, source: { key: value.sourceKey, detailId: value.detailId,
        lineId: value.lineId, candidateId: value.candidateId } };
    return { ...value, businessId: value.targetId, playbackTarget: clone(playbackTarget) };
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
