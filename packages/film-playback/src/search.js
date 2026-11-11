import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { Readable } from 'node:stream';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { checkSource, clone, codeOf, fail, object, requireValue, sourceIdentity,
  sourceRevision, text } from './identity.js';

const require = createRequire(import.meta.url);
const itemIdentity = item => JSON.stringify([item.siteKey, item.detailId]);
const metadataIdentity = item => `${item.tmdbType}:${item.tmdbId}`;
const metadataSearchRequest = query => ({ kind: 'metadata', provider: 'tmdb',
  path: '/3/search/multi', query: { query, page: 1, language: 'zh-CN' }, imageSize: 'w500' });
const metadataDetailRequest = item => ({ kind: 'metadata', provider: 'tmdb',
  path: `/3/${item.tmdbType}/${item.tmdbId}`,
  query: { language: 'zh-CN', append_to_response: 'credits,similar,images' }, imageSize: 'original' });
const members = card => card.aggregateItems?.length ? card.aggregateItems
  : card.sourceKind === 'site' ? [card] : [];
const rawSelection = card => card.sourceKind === 'site' && card.displayKey.startsWith('site:');
// Display identities are opaque. JSON-escaping the complete group key keeps
// lone UTF16 code units and avoids collisions with legitimate textual escapes.
const displayIdentity = key => key.startsWith('group:') ? `group:${JSON.stringify(key.slice(6))}` : key;
function scalarText(value) {
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index);
    if (unit < 0xd800 || unit > 0xdfff) continue;
    if (unit > 0xdbff) return false;
    const next = value.charCodeAt(++index);
    if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
  }
  return true;
}
function wireValue(value) {
  if (typeof value === 'string') return scalarText(value) ? value
    : Array.from({ length: value.length }, (_, index) => value.charCodeAt(index));
  if (Array.isArray(value)) return value.map(wireValue);
  if (object(value)) return Object.fromEntries(Object.entries(value).map(([key, child]) =>
    [key, key === 'raw' ? clone(child)
      : key === 'displayKey' && typeof child === 'string' ? displayIdentity(child) : wireValue(child)]));
  return value;
}
const settle = async works => {
  const results = await Promise.allSettled(works);
  const failure = results.find(result => result.status === 'rejected');
  if (failure) throw failure.reason;
};

/** Owns search scheduling and projections, borrowing the film data/cache owner. */
export function createFilmSearchService({ data, rules, program, now = Date.now }) {
  const processor = program ?? new NativeJsonProgram(readFileSync(require.resolve('@meowfilm/film-search'), 'utf8'));
  const sessions = new Map(), jobs = new Set();
  let closed = false, closing;

  function get(id) {
    requireValue(!closed, 'CLOSED');
    const session = sessions.get(id);
    requireValue(session && !session.released, 'NOT_FOUND');
    return session;
  }
  function active(session, epoch) {
    return !closed && !session.released && !session.stopped && !session.invalidated && session.epoch === epoch;
  }
  function admitted(session, epoch) {
    requireValue(active(session, epoch), 'CANCELLED');
  }
  function options(session, epoch) {
    return { signal: session.controller.signal, canSchedule: () => active(session, epoch) };
  }
  function canLoadMore(session) {
    return session.cursors.some(cursor => !cursor.ended) ||
      session.siteState === 'failed' || session.metadataState === 'failed';
  }
  function snapshot(session) {
    return {
      sessionId: session.id, query: session.query, rulesRevision: session.ruleSnapshot.revision,
      round: session.round, revision: session.sequence, sequence: session.sequence, siteState: session.siteState,
      metadataState: session.metadataState, loading: session.loading,
      hasMore: !session.loading && !session.stopped && !session.invalidated &&
        canLoadMore(session),
      noSources: session.cursors.length === 0, invalidated: session.invalidated,
      siteItems: wireValue(session.siteItems), tmdbItems: wireValue(session.tmdbItems),
      metadataSearch: clone(session.metadataSearch), metadataQuery: session.metadataQuery,
      metadataDetails: clone(Object.fromEntries(session.details)),
      cards: wireValue(session.cards), rawCards: wireValue(session.rawCards),
    };
  }
  function publish(session) {
    if (session.released || closed) return;
    session.sequence++;
    for (const listener of session.listeners) listener();
  }
  function track(work) {
    jobs.add(work);
    work.then(() => jobs.delete(work), () => jobs.delete(work));
    return work;
  }
  async function invoke(session, epoch, input) {
    admitted(session, epoch);
    const value = await processor.invoke(input, { signal: session.controller.signal });
    admitted(session, epoch);
    return value;
  }
  async function receipt(session, epoch, request) {
    admitted(session, epoch);
    try {
      const entry = await data.cachedEntry(request, options(session, epoch));
      admitted(session, epoch);
      return entry;
    } catch (error) {
      admitted(session, epoch);
      if (error.code === 'STALE_SOURCE' || error.code === 'STALE_METADATA') throw error;
      // Eviction or failure to export a receipt does not invent a cache seed.
      return undefined;
    }
  }
  async function display(session, epoch) {
    const revision = ++session.displayRevision;
    const input = { operation: 'display', query: session.query,
      aggregateRules: session.ruleSnapshot.rules.aggregateRegexRules,
      siteItems: session.siteItems.slice(), tmdbItems: session.tmdbItems.slice(),
      siteOrder: session.cursors.map(cursor => cursor.source.key), searchCoverSite: session.coverSite };
    const aggregate = await invoke(session, epoch, { ...input, rawListMode: false, previousOrder: session.order });
    const raw = await invoke(session, epoch, { ...input, rawListMode: true, previousOrder: session.rawOrder });
    if (revision !== session.displayRevision) return;
    session.cards = aggregate.items; session.rawCards = raw.items;
    for (const card of [...session.cards, ...session.rawCards]) {
      // Keep only identity-bearing selections, so a previously displayed group
      // can follow a later TMDB carrier without title inference.
      const key = displayIdentity(card.displayKey);
      if (!session.selections.has(key)) session.selections.set(key, clone(card));
    }
    publish(session);
  }
  async function readPages(session, epoch, cursors) {
    if (!cursors.length) {
      if (session.siteState !== 'failed') session.siteState = 'completed';
      return;
    }
    session.siteState = 'loading';
    let succeeded = 0;
    try {
      await settle(cursors.map(async cursor => {
        cursor.recovery = true;
        try {
          let read = cursor.pages.get(cursor.page);
          if (!read) {
            let value;
            try {
              value = await data.search(cursor.source, session.query, cursor.page, options(session, epoch));
            } catch (error) {
              admitted(session, epoch);
              if (error.code === 'STALE_SOURCE') throw error;
              cursor.recovery = false; // Retry a failed network page only on explicit "more".
              return;
            }
            admitted(session, epoch);
            read = { value }; cursor.pages.set(cursor.page, read);
          }
          if (!Object.hasOwn(read, 'entry')) read.entry = await receipt(session, epoch,
            { kind: 'search', source: cursor.source, query: session.query, page: cursor.page });
          const mapped = await invoke(session, epoch, { operation: 'site', query: session.query,
            aggregateRules: session.ruleSnapshot.rules.aggregateRegexRules,
            site: { siteKey: cursor.source.key, siteId: cursor.source.siteId,
              installationId: cursor.source.installationId, generation: cursor.source.generation,
              siteName: cursor.source.name, order: cursor.source.order ?? cursor.order },
            page: cursor.page, payload: read.value.raw });
          let added = 0;
          for (const item of mapped.items) {
            const key = itemIdentity(item);
            if (session.identities.has(key)) continue;
            session.identities.add(key); session.siteItems.push(item); added++;
          }
          cursor.confirmedPage = cursor.page;
          cursor.ended = !mapped.hasMore || !mapped.explicitPageCount && added === 0;
          cursor.page++; cursor.recovery = false; succeeded++;
          await display(session, epoch);
        } catch (error) {
          admitted(session, epoch);
          cursor.recovery = false;
          if (error.code === 'STALE_SOURCE') invalidateSession(session);
          throw error;
        }
      }));
      admitted(session, epoch);
      session.siteState = succeeded ? 'completed' : 'failed';
    } catch (error) {
      if (active(session, epoch)) session.siteState = 'failed';
      throw error;
    } finally {
      if (active(session, epoch)) publish(session);
    }
  }
  async function metadataRead(session, epoch, request) {
    admitted(session, epoch);
    const key = JSON.stringify(request);
    let read = session.metadataReads.get(key);
    if (!read) {
      const value = await data.metadata(request.provider, request.path, request.query,
        { ...options(session, epoch), imageSize: request.imageSize });
      admitted(session, epoch);
      read = { value }; session.metadataReads.set(key, read);
    }
    if (!Object.hasOwn(read, 'entry')) read.entry = await receipt(session, epoch, request);
    return read.value;
  }
  async function readMetadata(session, epoch) {
    session.metadataState = 'loading';
    try {
      if (!session.metadataSearchCompleted) {
        if (session.metadataSearch === null) {
          try {
            session.metadataSearch = await metadataRead(session, epoch, metadataSearchRequest(session.query));
          } catch (error) {
            admitted(session, epoch);
            session.metadataState = ['CAPABILITY_UNAVAILABLE', 'NOT_CONFIGURED'].includes(error.code)
              ? 'unavailable' : 'failed';
            return;
          }
          session.metadataQuery = session.query;
        }
        let payload = session.metadataSearch;
        if (payload.results.length === 0) {
          const fallback = await invoke(session, epoch, { operation: 'fallback', query: session.query });
          if (fallback.query) {
            let next;
            try {
              next = await metadataRead(session, epoch, metadataSearchRequest(fallback.query));
            } catch (error) {
              admitted(session, epoch);
              session.metadataState = ['CAPABILITY_UNAVAILABLE', 'NOT_CONFIGURED'].includes(error.code)
                ? 'unavailable' : 'failed';
              return;
            }
            if (next.results.length) {
              payload = next; session.metadataQuery = fallback.query;
            }
          }
        }
        session.metadataSearch = payload; session.metadataSearchCompleted = true;
      }
      if (!session.tmdbMapped) {
        const mapped = await invoke(session, epoch,
          { operation: 'tmdb', query: session.query, payload: session.metadataSearch });
        session.tmdbItems = mapped.items; session.tmdbMapped = true;
      }
      await display(session, epoch);
      await settle(session.tmdbItems.slice().filter(item =>
        !session.detailCompleted.has(metadataIdentity(item))).map(async item => {
        const key = metadataIdentity(item);
        let payload = session.details.get(key);
        if (!session.details.has(key)) {
          try {
            payload = await metadataRead(session, epoch, metadataDetailRequest(item));
          } catch {
            admitted(session, epoch);
            session.detailCompleted.add(key);
            return; // A failed network detail never erases the valid search summary.
          }
          session.details.set(key, payload);
        }
        const enhanced = await invoke(session, epoch, { operation: 'detail', item, payload,
          today: new Date(now() + 8 * 3_600_000).toISOString().slice(0, 10) });
        const index = session.tmdbItems.findIndex(row => metadataIdentity(row) === key);
        session.tmdbItems[index] = enhanced; session.detailCompleted.add(key);
        await display(session, epoch);
      }));
      admitted(session, epoch);
      session.metadataState = 'completed';
    } catch (error) {
      if (active(session, epoch)) session.metadataState = 'failed';
      throw error;
    } finally {
      if (active(session, epoch)) publish(session);
    }
  }
  function start(session, mode) {
    requireValue(!session.invalidated, 'STALE_SESSION');
    if (session.loading) return session.work;
    session.controller = new AbortController();
    session.stopped = false; session.loading = true;
    const epoch = ++session.epoch;
    if (mode !== 'resume' || session.round === 0) session.round++;
    publish(session);
    session.work = track((async () => {
      try {
        const cursors = session.cursors.filter(cursor => !cursor.ended &&
          (mode !== 'resume' || cursor.recovery));
        const metadataPending = ['idle', 'loading', 'cancelled'].includes(session.metadataState) ||
          mode === 'more' && session.metadataState === 'failed';
        await settle([readPages(session, epoch, cursors),
          metadataPending ? readMetadata(session, epoch) : Promise.resolve()]);
        admitted(session, epoch);
        try {
          await display(session, epoch);
          if (session.cursors.every(cursor => cursor.ended)) session.siteState = 'completed';
        } catch (error) {
          if (active(session, epoch)) {
            if (session.siteState === 'completed') session.siteState = 'failed';
            if (session.metadataState === 'completed') session.metadataState = 'failed';
          }
          throw error;
        }
      } catch (error) {
        if (active(session, epoch)) session.error = codeOf(error);
      } finally {
        if (session.epoch === epoch && !session.released) {
          session.order = session.cards.map(item => item.displayKey);
          session.rawOrder = session.rawCards.map(item => item.displayKey);
          session.loading = false; publish(session);
        }
      }
    })());
    return session.work;
  }
  async function open(input, { signal } = {}) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    requireValue(object(input) && text(input.query) && Array.isArray(input.sources));
    const sources = input.sources.map(checkSource).filter(source =>
      source.enabled !== false && source.searchable !== false);
    requireValue(new Set(sources.map(source => source.key)).size === sources.length);
    const ruleSnapshot = await rules.snapshot();
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    const session = { id: randomUUID(), query: input.query.trim(), ruleSnapshot,
      cursors: sources.map((source, order) => ({ source, order, page: 1, confirmedPage: 0,
        ended: false, recovery: true, pages: new Map() })),
      coverSite: sources.find(source => source.aggregateImages)?.key ?? '',
      round: 0, sequence: 0, epoch: 0, commandEpoch: 0, displayRevision: 0,
      controller: new AbortController(), work: Promise.resolve(),
      stopped: false, released: false, invalidated: false, loading: false,
      siteState: 'idle', metadataState: 'idle', siteItems: [], tmdbItems: [],
      metadataSearch: null, metadataQuery: input.query.trim(), metadataSearchCompleted: false,
      tmdbMapped: false, details: new Map(), detailCompleted: new Set(), metadataReads: new Map(),
      cards: [], rawCards: [], order: [], rawOrder: [], identities: new Set(),
      selections: new Map(), listeners: new Set() };
    sessions.set(session.id, session);
    start(session, 'first');
    return snapshot(session);
  }
  function stop(session) {
    session.commandEpoch++;
    if (session.stopped) return;
    session.stopped = true; session.epoch++; session.controller.abort();
    session.loading = false;
    if (session.siteState === 'loading' || session.siteState === 'idle') session.siteState = 'cancelled';
    if (session.metadataState === 'loading' || session.metadataState === 'idle') session.metadataState = 'cancelled';
    publish(session);
  }
  function invalidateSession(session) {
    session.invalidated = true; stop(session); publish(session);
  }
  async function command(input) {
    const session = get(input.sessionId);
    requireValue(['more', 'stop', 'resume'].includes(input.action));
    const intent = ++session.commandEpoch;
    if (input.action === 'stop') stop(session);
    else if (!session.loading) {
      requireValue(!session.invalidated, 'STALE_SESSION');
      if (input.action === 'resume') {
        if (session.stopped) {
          await session.work;
          if (intent === session.commandEpoch) start(session, 'resume');
        }
      } else if (canLoadMore(session)) {
        await session.work;
        if (intent === session.commandEpoch) start(session, 'more');
      }
    }
    return snapshot(session);
  }
  function currentSelection(session, selectionId) {
    const selected = session.selections.get(selectionId);
    requireValue(selected, 'NOT_FOUND');
    const list = rawSelection(selected) ? session.rawCards : session.cards;
    const same = list.find(card => displayIdentity(card.displayKey) === selectionId);
    if (same) return { selected, card: same };
    const keys = new Set(members(selected).map(itemIdentity));
    const carrier = rawSelection(selected) ? undefined
      : list.find(card => members(card).some(item => keys.has(itemIdentity(item))));
    return { selected, card: carrier ?? selected };
  }
  function handoff(input) {
    const session = get(input.sessionId);
    requireValue(text(input.selectionId) && Array.isArray(input.sources));
    const { selected, card } = currentSelection(session, input.selectionId);
    const isRaw = rawSelection(selected), currentSources = input.sources.map(checkSource);
    const sources = session.cursors.flatMap(cursor => {
      if (isRaw && cursor.source.key !== selected.siteKey) return [];
      const current = currentSources.find(source => sourceIdentity(source) === sourceIdentity(cursor.source) &&
        source.enabled !== false && source.searchable !== false);
      return current ? [{ ...current, key: cursor.source.key }] : [];
    });
    const selectedMembers = members(card).filter(item => sources.some(source => source.key === item.siteKey));
    const selectionMembers = selectedMembers.map(item => ({ sourceKey: item.siteKey, detailId: item.detailId }));
    let target;
    if (!isRaw && card.sourceKind === 'tmdb') {
      target = { id: input.selectionId, kind: card.tmdbType, title: card.title,
        metadataProvider: 'tmdb', metadataId: String(card.tmdbId) };
    } else if (isRaw) {
      requireValue(sources.length > 0, 'SOURCE_NOT_FOUND');
      target = { id: input.selectionId, kind: 'source', title: selected.title,
        source: { key: selected.siteKey, detailId: selected.detailId } };
    } else {
      requireValue(selectedMembers.length > 0, 'SOURCE_NOT_FOUND');
      target = { id: input.selectionId, kind: 'group', title: card.title,
        members: selectionMembers };
    }
    const results = [], resultHints = [], search = [], cacheEntries = [];
    for (const source of sources) {
      const cursor = session.cursors.find(row => row.source.key === source.key);
      const items = session.siteItems.filter(item => item.siteKey === source.key &&
        (!isRaw || item.detailId === selected.detailId)).map(item => clone(item.raw));
      const sameProducer = !session.invalidated && sourceRevision(cursor.source) === sourceRevision(source);
      const result = { sourceKey: source.key, items };
      if (sameProducer) {
        results.push(result);
        search.push({ sourceKey: source.key, page: cursor.confirmedPage, completed: cursor.ended });
        cacheEntries.push(...[...cursor.pages.values()].flatMap(page => page.entry ? [page.entry] : []));
      } else resultHints.push(result);
    }
    let metadata = null;
    if (!isRaw && card.sourceKind === 'tmdb') {
      metadata = session.details.get(metadataIdentity(card)) ?? session.metadataSearch?.results.find(row =>
        row.media_type === card.tmdbType && row.id === card.tmdbId) ?? null;
    }
    if (!isRaw && !session.invalidated) {
      cacheEntries.push(...[...session.metadataReads.values()].flatMap(read => read.entry ? [read.entry] : []));
    }
    return clone({ target, selectionMembers, sources, query: session.query, results, resultHints, search, metadata, cacheEntries,
      selectionId: input.selectionId, revision: session.sequence,
      ...(target.metadataProvider ? { metadataTarget: target } : {}),
      searchReference: { sessionId: session.id, selectionId: input.selectionId } });
  }
  function events(id, after = -1) {
    const session = get(id);
    requireValue(Number.isSafeInteger(after) && after >= -1);
    let demand = false, disposed = false, initial = true;
    const changed = () => {
      if (disposed) return;
      if (closed || session.released) { body.push(null); return; }
      if (!demand || !initial && session.sequence <= after) return;
      initial = false; demand = false; after = session.sequence;
      body.push(Buffer.from(`id: ${after}\nevent: snapshot\ndata: ${JSON.stringify(snapshot(session))}\n\n`));
    };
    const body = new Readable({ highWaterMark: 16 * 1024, read() { demand = true; changed(); },
      destroy(error, done) { disposed = true; session.listeners.delete(changed); done(error); } });
    session.listeners.add(changed);
    return { status: 200, headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-store' }, body,
      async dispose() { body.destroy(); } };
  }
  async function release(input) {
    const session = get(input.sessionId);
    stop(session); session.released = true;
    for (const listener of session.listeners) listener();
    await session.work;
    sessions.delete(session.id);
    return { released: true };
  }
  async function invalidate(domain) {
    const affected = [...sessions.values()].filter(session => domain === 'tmdb' ||
      session.cursors.some(({ source }) => domain === 'source' ||
        typeof domain === 'string' && source.key === domain ||
        object(domain) && domain.kind === 'source' && source.engine === domain.engine &&
          (domain.installationId === undefined || source.installationId === domain.installationId)));
    for (const session of affected) invalidateSession(session);
    await Promise.allSettled(affected.map(session => session.work));
  }
  return {
    open, command, handoff, release, events, invalidate,
    has: id => sessions.has(id),
    snapshot: input => snapshot(get(input.sessionId)),
    ruleSnapshot: input => get(input.sessionId).ruleSnapshot,
    close() {
      return closing ??= (async () => {
        for (const session of sessions.values()) {
          stop(session); session.released = true;
          for (const listener of session.listeners) listener();
        }
        closed = true;
        await Promise.allSettled([...jobs]);
        sessions.clear();
        await processor.close();
      })();
    },
  };
}
