import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
import { createSnapshotStream } from './sse.js';
import { mapWithConcurrency } from './concurrency.js';
import { clone, codeOf, normalizeWorkKey, object, requireValue, text } from './identity.js';
import { filmRuleFailure } from './rules.js';

const require = createRequire(import.meta.url);
const MAX_SITE_CONCURRENCY = 6;
const MAX_METADATA_DETAIL_CONCURRENCY = 4;
const itemIdentity = item => stringifyJSONValue([item.siteKey, item.detailId]);
const metadataIdentity = item => `${item.tmdbType}:${item.tmdbId}`;
const metadataSearchRequest = query => ({ kind: 'metadata', provider: 'tmdb',
  path: '/3/search/multi', query: { query, page: 1 }, imageSize: 'w500' });
const metadataDetailRequest = item => ({ kind: 'metadata', provider: 'tmdb',
  path: `/3/${item.tmdbType}/${item.tmdbId}`,
  query: { append_to_response: 'credits,similar,images' }, imageSize: 'original' });
const members = card => card.aggregateItems?.length ? card.aggregateItems
  : card.sourceKind === 'site' ? [card] : [];
const rawSelection = card => card.sourceKind === 'site' && card.displayKey.startsWith('site:');
const displayIdentity = key => key.startsWith('group:') ? `group:${JSON.stringify(key.slice(6))}` : key;

function wireValue(value) {
  if (isRawJSON(value)) return clone(value);
  if (Array.isArray(value)) return value.map(wireValue);
  if (object(value)) return Object.fromEntries(Object.entries(value).map(([key, child]) =>
    [key, key === 'displayKey' || key === 'id' && typeof child === 'string' && child.startsWith('group:')
      ? displayIdentity(child) : wireValue(child)]));
  return value;
}

const settle = async works => {
  const results = await Promise.allSettled(works);
  const failure = results.find(result => result.status === 'rejected');
  if (failure) throw failure.reason;
};

/**
 * Owns search scheduling and clean card projections inside Node.
 * Shares `film.data` 1h search/metadata cache in-process with `film.playback`,
 * never dumping cacheEntries or raw spider rows to UI clients.
 */
export function createFilmSearchService({ data, rules, catalog, sourceRegistry, debug = null }) {
  requireValue(catalog && typeof catalog.searchCards === 'function' && sourceRegistry);
  const processor = new NativeJsonProgram(readFileSync(require.resolve('@meowfilm/film-search'), 'utf8'), {
    debug,
    name: 'film-search',
  });
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
      sessionId: session.id,
      query: session.query,
      rulesRevision: session.ruleSnapshot.revision,
      round: session.round,
      revision: session.sequence,
      sequence: session.sequence,
      siteState: session.siteState,
      metadataState: session.metadataState,
      loading: session.loading,
      hasMore: !session.loading && !session.stopped && !session.invalidated &&
        session.firstRoundSettled && canLoadMore(session),
      noSources: session.cursors.length === 0,
      invalidated: session.invalidated,
      cards: wireValue(session.cleanCards),
      rawCards: wireValue(session.cleanRawCards),
    };
  }
  function publish(session, { force = false } = {}) {
    if (closed || session.released || (!force && session.stopped)) return;
    session.sequence++;
    for (const listener of [...session.listeners]) listener();
  }
  function track(work) {
    jobs.add(work);
    work.then(() => jobs.delete(work), () => jobs.delete(work));
    return work;
  }
  async function invoke(session, epoch, input) {
    admitted(session, epoch);
    let value;
    try {
      value = await processor.invoke(input, { signal: session.controller.signal });
    } catch (error) {
      admitted(session, epoch);
      throw filmRuleFailure(error);
    }
    admitted(session, epoch);
    return value;
  }

  function project(session, epoch) {
    if (session.displayWork) return session.displayWork;
    const work = (async () => {
      while (session.projectedRevision !== session.displayRevision) {
        admitted(session, epoch);
        const revision = session.displayRevision;
        const value = await invoke(session, epoch, {
          operation: 'display',
          query: session.query,
          aggregateRules: session.ruleSnapshot.rules.aggregateRegexRules,
          seasonRules: session.ruleSnapshot.rules.seasonRules,
          siteItems: session.siteItems.slice(),
          tmdbItems: session.tmdbItems.slice(),
          siteOrder: session.cursors.map(cursor => cursor.source.key),
          searchCoverSite: session.coverSite,
          previousOrder: session.order,
          previousRawOrder: session.rawOrder,
        });
        if (revision !== session.displayRevision) continue;
        session.cards = value.cards;
        session.rawCards = value.rawCards;
        session.cleanCards = catalog.searchCards(value.cards);
        session.cleanRawCards = catalog.searchCards(value.rawCards);
        session.projectedRevision = revision;
        for (const card of [...session.cards, ...session.rawCards]) {
          const key = displayIdentity(card.displayKey);
          session.selections.set(key, card);
        }
        publish(session);
      }
    })();
    session.displayWork = work;
    work.then(() => { session.displayWork = undefined; }, () => { session.displayWork = undefined; });
    return work;
  }

  function display(session, epoch) {
    session.displayRevision++;
    return project(session, epoch);
  }

  async function readPages(session, epoch, cursors) {
    if (!cursors.length) {
      if (session.siteState !== 'failed') session.siteState = 'completed';
      return;
    }
    session.siteState = 'loading';
    let succeeded = 0;
    try {
      await mapWithConcurrency(cursors, MAX_SITE_CONCURRENCY, async cursor => {
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
              cursor.recovery = false;
              debug?.emit({ level: 'debug', cat: 'search', sub: 'film.search', event: 'search.page.error',
                data: { sessionId: session.id, sourceKey: cursor.source.key, page: cursor.page, code: codeOf(error) } });
              return;
            }
            admitted(session, epoch);
            read = { value }; cursor.pages.set(cursor.page, read);
          }
          const mapped = await invoke(session, epoch, {
            operation: 'site',
            query: session.query,
            aggregateRules: session.ruleSnapshot.rules.aggregateRegexRules,
            seasonRules: session.ruleSnapshot.rules.seasonRules,
            site: {
              siteKey: cursor.source.key,
              siteId: cursor.source.siteId,
              installationId: cursor.source.installationId,
              generation: cursor.source.generation,
              siteName: cursor.source.name,
              order: cursor.source.order ?? cursor.order,
            },
            page: cursor.page,
            payload: read.value.raw,
          });
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
      }, { shouldContinue: () => active(session, epoch) });
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
    const key = stringifyJSONValue(request);
    let read = session.metadataReads.get(key);
    if (!read) {
      const value = await data.metadata(request.provider, request.path, request.query,
        { ...options(session, epoch), imageSize: request.imageSize });
      admitted(session, epoch);
      read = { value }; session.metadataReads.set(key, read);
    }
    return read.value;
  }

  async function readMetadata(session, epoch) {
    if (!await data.isMetadataConfigured('tmdb', { signal: session.controller.signal })) {
      session.metadataState = 'unavailable';
      return;
    }
    session.metadataState = 'loading';
    try {
      if (!session.metadataSearchCompleted) {
        if (session.metadataSearch === null) {
          try {
            session.metadataSearch = await metadataRead(session, epoch, metadataSearchRequest(session.query));
          } catch (error) {
            admitted(session, epoch);
            if (error.code === 'STALE_METADATA') { invalidateSession(session); throw error; }
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
              if (error.code === 'STALE_METADATA') { invalidateSession(session); throw error; }
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
      const pendingDetails = session.tmdbItems.slice().filter(item =>
        !session.detailCompleted.has(metadataIdentity(item)));
      await mapWithConcurrency(pendingDetails, MAX_METADATA_DETAIL_CONCURRENCY, async item => {
        const key = metadataIdentity(item);
        let payload = session.details.get(key);
        if (!session.details.has(key)) {
          try {
            payload = await metadataRead(session, epoch, metadataDetailRequest(item));
          } catch (error) {
            admitted(session, epoch);
            if (error?.code === 'STALE_METADATA') {
              invalidateSession(session);
              throw error;
            }
            if (['CANCELLED', 'CLOSED'].includes(error?.code)) throw error;
            session.detailCompleted.add(key);
            return;
          }
          session.details.set(key, payload);
        }
        const enhanced = await invoke(session, epoch, { operation: 'detail', item, payload,
          today: new Date(Date.now() + 8 * 3_600_000).toISOString().slice(0, 10) });
        const index = session.tmdbItems.findIndex(row => metadataIdentity(row) === key);
        if (index >= 0) session.tmdbItems[index] = enhanced;
        session.detailCompleted.add(key);
        await display(session, epoch);
      }, { shouldContinue: () => active(session, epoch) });
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
          await project(session, epoch);
          if (session.cursors.every(cursor => cursor.ended)) session.siteState = 'completed';
          if (active(session, epoch)) session.firstRoundSettled = true;
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
    requireValue(object(input) && text(input.query));
    const allSources = await sourceRegistry.liveSources({ explicitSources: input.sources, signal });
    const sources = allSources.filter(source => source.enabled !== false && source.searchable !== false);
    const ruleSnapshot = await rules.snapshot();
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    const session = {
      id: randomUUID(),
      query: input.query.trim(),
      ruleSnapshot,
      cursors: sources.map((source, order) => ({
        source, order, page: 1, confirmedPage: 0, ended: false, recovery: true, pages: new Map(),
      })),
      coverSite: sources.find(source => source.aggregateImages)?.key ?? '',
      round: 0, firstRoundSettled: false, sequence: 0, epoch: 0, commandEpoch: 0, displayRevision: 0, projectedRevision: 0,
      controller: new AbortController(), work: Promise.resolve(),
      stopped: false, released: false, invalidated: false, loading: false,
      siteState: 'idle', metadataState: 'idle', siteItems: [], tmdbItems: [],
      metadataSearch: null, metadataQuery: input.query.trim(), metadataSearchCompleted: false,
      tmdbMapped: false, details: new Map(), detailCompleted: new Set(), metadataReads: new Map(),
      cards: [], rawCards: [], cleanCards: [], cleanRawCards: [], order: [], rawOrder: [], identities: new Set(),
      selections: new Map(), listeners: new Set(),
    };
    sessions.set(session.id, session);
    debug?.emit({ level: 'info', cat: 'search', sub: 'film.search', event: 'search.open',
      data: { sessionId: session.id, query: session.query, sourceCount: sources.length } });
    start(session, 'first');
    return snapshot(session);
  }

  function stop(session) {
    session.commandEpoch++;
    if (session.stopped) return false;
    session.stopped = true; session.epoch++; session.controller.abort();
    session.loading = false;
    if (session.siteState === 'loading' || session.siteState === 'idle') session.siteState = 'cancelled';
    if (session.metadataState === 'loading' || session.metadataState === 'idle') session.metadataState = 'cancelled';
    publish(session, { force: true });
    return true;
  }

  function invalidateSession(session) {
    const alreadyInvalidated = session.invalidated;
    session.invalidated = true;
    const published = stop(session);
    if (!published && !alreadyInvalidated) publish(session, { force: true });
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
      } else if (session.firstRoundSettled && canLoadMore(session)) {
        await session.work;
        if (intent === session.commandEpoch && session.firstRoundSettled) start(session, 'more');
      }
    }
    return snapshot(session);
  }

  /**
   * Internal single-direction search -> playback handoff inside Node.
   * Returns matched group items, raw card selection intent, and cross-query display attachments.
   */
  function resolveEntry(reference) {
    if (!object(reference) || !text(reference.sessionId)) return null;
    const session = sessions.get(reference.sessionId);
    if (!session || session.released) return null;
    const cardId = text(reference.cardId ?? reference.selectionId);
    if (!cardId) return null;
    const selected = session.selections.get(cardId) ?? session.selections.get(displayIdentity(cardId));
    if (!selected) return null;

    const isRaw = rawSelection(selected);
    const keyword = text(selected.keyword) || text(selected.aggregateContentKey) || text(selected.title);
    const targetWorkKey = normalizeWorkKey(keyword);
    const queryWorkKey = normalizeWorkKey(session.query);

    // Find the aggregated group or TMDB carrier sharing the same canonical work key.
    const tmdbCarrier = session.cards.find(c => c.sourceKind === 'tmdb' &&
      (c.displayKey === selected.displayKey || normalizeWorkKey(c.keyword || c.title) === targetWorkKey)) ?? null;

    const groupItems = session.siteItems.filter(item =>
      normalizeWorkKey(item.keyword || item.aggregateContentKey || item.title) === targetWorkKey);

    // Cross-query attachment: e.g. searched "仙逆" and clicked "仙逆高清" -> attach "仙逆" items for display only.
    const crossQueryItems = queryWorkKey && queryWorkKey !== targetWorkKey
      ? session.siteItems.filter(item =>
        normalizeWorkKey(item.keyword || item.aggregateContentKey || item.title) === queryWorkKey)
      : [];

    const sameKeywordSearch = Boolean(queryWorkKey && queryWorkKey === targetWorkKey && !session.invalidated);
    const searchCursors = sameKeywordSearch
      ? session.cursors.map(cursor => ({
        sourceKey: cursor.source.key,
        page: cursor.confirmedPage,
        completed: cursor.ended,
      }))
      : [];

    return {
      query: session.query,
      isRaw,
      keyword,
      seasonHint: Number.isSafeInteger(selected.seasonHint) && selected.seasonHint > 0 ? selected.seasonHint : undefined,
      selectedSite: isRaw ? {
        sourceKey: selected.siteKey,
        detailId: selected.detailId,
        rawTitle: selected.rawTitle || selected.title,
        remark: selected.textBadge,
        seasonHint: selected.seasonHint,
      } : null,
      tmdb: tmdbCarrier ? {
        kind: tmdbCarrier.tmdbType,
        id: tmdbCarrier.tmdbId,
        detail: session.details.get(metadataIdentity(tmdbCarrier)) ?? null,
      } : null,
      groupItems: clone(groupItems),
      crossQueryItems: clone(crossQueryItems),
      searchCursors,
      ruleSnapshot: session.ruleSnapshot,
    };
  }

  function events(id, after = -1, signal) {
    const session = get(id);
    requireValue(Number.isSafeInteger(after) && after >= -1);
    return createSnapshotStream({
      listeners: session.listeners,
      isClosed: () => closed || session.released,
      getSequence: () => session.sequence,
      getSnapshot: () => snapshot(session),
      after,
      signal,
    });
  }

  async function release(input) {
    const session = get(input.sessionId);
    stop(session); session.released = true;
    for (const listener of [...session.listeners]) listener();
    await session.work;
    sessions.delete(session.id);
    return { released: true };
  }

  async function invalidate(domain) {
    const affected = [...sessions.values()].filter(session => domain === 'tmdb' ||
      domain === 'film.video-sources' ||
      session.cursors.some(({ source }) => domain === 'source' ||
        typeof domain === 'string' && source.key === domain ||
        object(domain) && (domain.kind === 'source' || domain.kind === 'script-account') &&
          source.engine === (domain.kind === 'script-account' ? 'cat' : domain.engine) &&
          (domain.installationId === undefined || source.installationId === domain.installationId) &&
          (domain.generation === undefined || source.generation === domain.generation)));
    for (const session of affected) invalidateSession(session);
    await Promise.allSettled(affected.map(session => session.work));
  }

  return {
    open, command, resolveEntry, release, events, invalidate,
    has: id => sessions.has(id),
    snapshot: input => snapshot(get(input.sessionId)),
    diagnostics() {
      let activeEventStreams = 0;
      for (const session of sessions.values()) activeEventStreams += session.listeners.size;
      return {
        closed,
        activeSessions: sessions.size,
        activeJobs: jobs.size,
        activeEventStreams,
        program: processor.stats(),
      };
    },
    close() {
      return closing ??= (async () => {
        for (const session of sessions.values()) {
          stop(session); session.released = true;
          for (const listener of [...session.listeners]) listener();
        }
        closed = true;
        await Promise.allSettled([...jobs]);
        sessions.clear();
        await processor.close();
      })();
    },
  };
}
