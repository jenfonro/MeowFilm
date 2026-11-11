import { identity, object, text } from './identity.js';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;
const STABLE = 14 * DAY;
export const cnDay = at => Math.floor((at + 8 * HOUR) / DAY) * DAY - 8 * HOUR;

export function cnDate(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return undefined;
  const [year, month, day] = value.split('-').map(Number);
  const at = Date.UTC(year, month - 1, day);
  return new Date(at).toISOString().slice(0, 10) === value ? at - 8 * HOUR : undefined;
}

export function metadataKind(request) {
  if (request.provider === 'tmdb' && /^\/3\/tv\/\d+\/season\/\d+$/.test(request.path)) return 'season';
  const detail = /^(?:\/3|\/rexxar\/api\/v2)\/(movie|tv)\/\d+$/.exec(request.path);
  return detail?.[1] ?? 'response';
}

export function canonicalMetadataRequest(request) {
  if (request.provider !== 'tmdb' || metadataKind(request) === 'response') return request;
  const query = { ...request.query };
  delete query.append_to_response;
  return { ...request, query };
}

export function requestedComponents(request, completeness) {
  const kind = metadataKind(request);
  if (completeness === 'partial' && kind !== 'response') return ['summary'];
  const components = kind === 'season' ? ['detail', 'episodes']
    : kind === 'tv' && request.provider === 'tmdb' ? ['detail', 'seasons']
      : kind === 'response' ? ['response'] : ['detail'];
  if (request.provider === 'tmdb' && kind !== 'response') {
    for (const name of (request.query.append_to_response ?? '').split(',')) {
      if (name.trim() && !components.includes(name.trim())) components.push(name.trim());
    }
  }
  return components;
}

const good = component => component?.fetchedAt !== undefined;
const usable = component => ['ready', 'empty'].includes(component?.status);
export function isEmpty(value) {
  if (value === null || value === '') return true;
  if (Array.isArray(value)) return value.length === 0;
  if (!object(value)) return false;
  return Object.keys(value).length === 0 || Object.values(value).some(Array.isArray) &&
    Object.values(value).filter(Array.isArray).every(rows => rows.length === 0);
}

export function parentSignature(parent, season) {
  if (!parent) return null;
  const value = parent.value;
  const row = value.seasons?.find(row => row.season_number === season);
  const episode = row => row?.season_number === season ? {
    episode_number: row.episode_number, air_date: row.air_date,
  } : null;
  return identity({
    declared: Array.isArray(value.seasons), season: row ? {
      episode_count: row.episode_count, air_date: row.air_date,
    } : null,
    status: value.status, next: episode(value.next_episode_to_air),
    last: episode(value.last_episode_to_air),
  });
}

export function metadataTTL(provider, path, value, at, completeness, parent) {
  const request = { provider, path };
  const kind = metadataKind(request);
  if (provider === 'douban' && /^\/rexxar\/api\/v2\/subject\/recent_hot\/(?:movie|tv)$/.test(path)) return HOUR;
  if (path === 'search' || /^\/3\/(?:search|discover|trending)\//.test(path) ||
      completeness === 'partial') return 6 * HOUR;
  if (kind === 'season') {
    const today = cnDay(at);
    let next, lastDated = -1;
    const episodes = value.episodes ?? [];
    for (const row of episodes) {
      const date = cnDate(row.air_date);
      if (date === undefined) continue;
      lastDated = Math.max(lastDated, row.episode_number);
      if (date >= today && (next === undefined || date < next)) next = date;
    }
    const declared = parent?.value.seasons?.find(row => row.season_number === value.season_number)?.episode_count;
    const announced = parent?.value.next_episode_to_air;
    const announcedDate = announced?.season_number === value.season_number ? cnDate(announced.air_date) : undefined;
    if (announcedDate !== undefined && announcedDate >= today &&
        (next === undefined || announcedDate < next)) next = announcedDate;
    if (next !== undefined) return Math.min(STABLE, (next <= at ? today + DAY : next) - at);
    const tailUnknown = episodes.some(row => cnDate(row.air_date) === undefined &&
      row.episode_number >= lastDated);
    const numbers = new Set(episodes.filter(row => row.episode_number > 0 &&
      row.episode_number <= declared).map(row => row.episode_number));
    const covered = Number.isSafeInteger(declared) && declared >= 0 &&
      numbers.size === declared;
    // A count is an announced total, not proof of broadcast. Only real past
    // dates and complete declared coverage establish a stable season.
    const stable = covered && (declared === 0 || lastDated >= declared && !tailUnknown) ||
      parent === undefined && lastDated >= 0 && !tailUnknown;
    return stable ? STABLE : DAY;
  }
  if (kind === 'tv') {
    const status = text(value.status).toLowerCase();
    if (status === 'ended') return STABLE;
    if (!status || status === 'unknown') return DAY;
    const next = cnDate(value.next_episode_to_air?.air_date);
    if (next !== undefined) return Math.min(STABLE, (next <= at ? cnDay(at) + DAY : next) - at);
    return DAY;
  }
  return STABLE;
}

function received(request, raw, name) {
  if (name === 'summary' || name === 'response') return { present: true,
    value: request.provider === 'douban' && request.path === 'search' ? raw.subjects.items : raw };
  if (name === 'detail') {
    const kind = metadataKind(request);
    const present = kind === 'season' || Array.isArray(raw.genres) &&
      (request.provider === 'douban' ? typeof raw.intro === 'string'
        : kind === 'movie' ? Object.hasOwn(raw, 'runtime') : true);
    return { present, value: raw };
  }
  const value = raw[name];
  if (name === 'seasons') return { present: Array.isArray(value) && value.every(row =>
    object(row) && Number.isSafeInteger(row.season_number) && row.season_number >= 0 &&
    (row.episode_count == null || Number.isSafeInteger(row.episode_count) && row.episode_count >= 0)), value };
  if (name === 'episodes') return { present: Array.isArray(value), value };
  return { present: Object.hasOwn(raw, name) && !(object(value) &&
    (value.success === false || value.status_code !== undefined && value.status_code !== 1)),
  value, failed: object(value) && (value.success === false || value.status_code !== undefined && value.status_code !== 1) };
}

export function mergeMetadata(previous, request, raw, selected, at, completeness, parent) {
  const components = { ...previous?.components };
  let summaryValue = previous?.summaryValue;
  const kind = metadataKind(request);
  const duration = metadataTTL(request.provider, request.path, raw, at, completeness, parent);
  const incoming = Object.fromEntries(selected.map(name => [name, received(request, raw, name)]));
  // A later summary owns its own value/time, never the retained full detail.
  if (incoming.summary && good(components.detail)) summaryValue = raw;
  if (incoming.detail?.present && good(components.summary) && !good(components.detail)) summaryValue = previous.value;
  // A successful base replaces all of its fields. Appended responses and a
  // missing requested seasons/episodes component retain their own ownership.
  let value = previous?.value ?? raw;
  if (incoming.response || incoming.summary && !good(components.detail)) value = raw;
  else if (incoming.detail?.present) {
    value = { ...raw };
    const separate = new Set([...Object.keys(components), ...selected]);
    for (const name of separate) {
      if (['summary', 'detail', 'response'].includes(name)) continue;
      delete value[name];
      if (good(components[name]) && Object.hasOwn(previous?.value ?? {}, name)) value[name] = previous.value[name];
    }
  } else value = { ...value };
  for (const [name, result] of Object.entries(incoming)) {
    if (result.present) {
      components[name] = { status: name !== 'detail' && isEmpty(result.value) ? 'empty' : 'ready',
        fetchedAt: at, expiresAt: at + duration };
      if (!['summary', 'detail', 'response'].includes(name)) value = { ...value, [name]: result.value };
    } else {
      components[name] = { ...components[name], status: result.failed ? 'failed' : 'missing',
        error: { code: result.failed ? 'UPSTREAM_HTTP' : 'UPSTREAM_FORMAT' } };
    }
  }
  if (!good(components.detail) && !components.summary && kind !== 'response') {
    components.summary = { status: 'ready', fetchedAt: at, expiresAt: at + 6 * HOUR };
  }
  return { request: canonicalMetadataRequest(request), value, components,
    ...(summaryValue === undefined ? {} : { summaryValue }),
    ...(kind === 'season' ? {
      parentSignature: incoming.summary && good(previous?.components.detail)
        ? previous.parentSignature : parentSignature(parent, Number(request.path.split('/').at(-1))),
      ...(incoming.summary ? { summaryParentSignature: parentSignature(parent, Number(request.path.split('/').at(-1))) }
        : previous?.summaryParentSignature === undefined ? {} : { summaryParentSignature: previous.summaryParentSignature }),
    } : {}) };
}

export function failedMetadata(previous, selected, code) {
  const components = { ...previous.components };
  for (const name of selected) components[name] = { ...components[name], status: 'failed', error: { code } };
  return { ...previous, components };
}

function summaryUsesDetail(state, at, parent) {
  return usable(state.components.detail) && state.components.detail.expiresAt > at &&
    (metadataKind(state.request) !== 'season' ||
      parentSignature(parent, Number(state.request.path.split('/').at(-1))) === state.parentSignature);
}

export function dueComponents(state, required, at, { refresh, parent, requestedEpisode } = {}) {
  if (refresh || !state) return required;
  const summary = required.length === 1 && required[0] === 'summary';
  if (summary && summaryUsesDetail(state, at, parent) && state.components.summary?.status !== 'failed') {
    return [];
  }
  const kind = metadataKind(state.request);
  const due = required.filter(name => !usable(state.components[name]) || state.components[name].expiresAt <= at);
  if (kind === 'season') {
    const season = Number(state.request.path.split('/').at(-1));
    if (parentSignature(parent, season) !== (summary ? state.summaryParentSignature : state.parentSignature)) return required;
    if (requestedEpisode !== undefined &&
        !(state.value.episodes ?? []).some(row => row.episode_number === requestedEpisode)) {
      const scheduled = [parent?.value.next_episode_to_air, parent?.value.last_episode_to_air].find(row =>
        row?.season_number === season && row.episode_number === requestedEpisode);
      const date = cnDate(scheduled?.air_date);
      if (date !== undefined && date <= cnDay(at) &&
          cnDay(state.components.episodes?.fetchedAt ?? 0) < date) return required;
    }
  }
  return due;
}

export function metadataRecord(state, request, required, at, context = {}) {
  const due = dueComponents(state, required, at, context);
  const summaryOnly = required.length === 1 && required[0] === 'summary' &&
    !summaryUsesDetail(state, at, context.parent) && good(state.components.summary);
  const anchor = summaryOnly ? state.components.summary
    : [state.components.detail, state.components.response, state.components.summary].find(good);
  const complete = !summaryOnly && requestedComponents(request, 'complete').every(name => good(state.components[name]) &&
    state.components[name].status !== 'missing');
  const parentDue = metadataKind(request) === 'season' && (!context.parent ||
    dueComponents(context.parent, ['detail', 'seasons'], at).length > 0);
  const parentError = context.parentError ?? (parentDue
    ? ['detail', 'seasons'].map(name => context.parent?.components[name]?.error).find(Boolean) ??
      { code: 'STALE_METADATA' } : undefined);
  const error = parentError ?? required.map(name => state.components[name]?.error).find(Boolean);
  const components = Object.fromEntries(Object.entries(state.components).map(([name, component]) =>
    [name, Object.fromEntries(Object.entries(component).filter(([key]) => key !== 'expiresAt'))]));
  const timestamps = !summaryOnly && required.length === 1 && required[0] === 'summary' && good(state.components.detail)
    ? ['detail'] : required;
  return { ...request, value: summaryOnly ? state.summaryValue ?? state.value : state.value,
    completeness: complete ? 'complete' : 'partial',
    fetchedAt: anchor?.fetchedAt ?? 0, expiresAt: Math.min(...timestamps.map(name =>
      state.components[name]?.expiresAt ?? at)),
    cache: { status: due.length || parentError ? 'stale' : 'fresh', ...(error ? { error } : {}), components } };
}
