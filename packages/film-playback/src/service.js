import { randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { isSmartSiteAllowed, normalizePreferences, readPreferences } from './policy.js';
import { createFilmCatalog } from './catalog.js';
import { createSnapshotStream } from './sse.js';
import { clone, codeOf, fail, fields, identity, isIdentifier, object, requireValue, text } from './identity.js';
import { addressLifetime, cancelled, manualIdentity } from './session-helpers.js';
import { createSessionMetadata } from './session-metadata.js';
import { createSessionSnapshot } from './session-snapshot.js';
import { createSessionBrowsing } from './session-browsing.js';
import { createSessionCandidates } from './session-candidates.js';
import { createSessionSelection } from './session-selection.js';
import { createSessionPrefetch } from './session-prefetch.js';
import { createSessionLifecycle } from './session-lifecycle.js';
import { createSessionCommands } from './session-commands.js';

/** Keyword-anchored playback session owner shared identically by Windows and iOS. */
export function createPlaybackService({
  data,
  library,
  media,
  rules,
  sourceRegistry,
  catalog = createFilmCatalog(),
  debug,
}) {
  const sessions = new Map();
  const sessionsByKey = new Map();
  const jobs = new Set();
  const calculations = new Set();
  const episodeMaps = new WeakMap();
  let closed = false, closing;
  requireValue(rules && typeof rules.snapshot === 'function' && sourceRegistry);

  const preferences = () => readPreferences(library.read().preferences);
  let observedPreferences;
  let observedReady = false;
  void library.ready.then(() => {
    if (!closed && !observedReady) {
      try {
        observedPreferences = preferences();
        observedReady = true;
      } catch (error) {
        debug?.emit({
          level: 'warn', cat: 'film-playback', sub: 'film.settings', event: 'preferences.init_error',
          data: { code: codeOf(error) }, error,
        });
      }
    }
  }, () => {});

  function get(id) {
    requireValue(!closed, 'CLOSED');
    const session = sessions.get(id);
    requireValue(session && !session.released, 'NOT_FOUND');
    return session;
  }

  function tracked(work) {
    jobs.add(work);
    void work.finally(() => jobs.delete(work)).catch(() => {});
    return work;
  }

  function calculate(session, operation, lifetime = 'playback') {
    const controller = new AbortController();
    calculations.add(controller);
    const work = tracked((async () => {
      requireValue(!closed && !session.released, 'CANCELLED');
      return operation(controller.signal);
    })());
    session.calculations.set(controller, { work, lifetime });
    void work.finally(() => {
      session.calculations.delete(controller);
      calculations.delete(controller);
    }).catch(() => {});
    return work;
  }

  function cancelCalculations(session, lifetime) {
    const pending = [...session.calculations].filter(([, value]) =>
      lifetime === undefined || value.lifetime === lifetime);
    for (const [controller] of pending) controller.abort();
    return Promise.allSettled(pending.map(([, value]) => value.work));
  }

  function publish(session) {
    if (session.released) return;
    session.sequence++;
    for (const listener of [...session.listeners]) listener();
  }

  function detached(session, work) {
    tracked(work).catch(error => {
      if (!session.released && !closed && !cancelled(error)) {
        session.error = codeOf(error);
        publish(session);
      }
    });
  }

  function valid(session, generation) {
    return !closed && !session.released && session.generation === generation && !session.stopped;
  }

  async function runIO(session, generation, source, candidate, operation) {
    const activity = { generation, source, candidate };
    session.runActivity = activity;
    try { return await operation(); }
    finally { if (session.runActivity === activity) session.runActivity = null; }
  }

  function currentSource(session, source) {
    if (!source) return false;
    if (source.engine === 'pan') return true;
    const current = session.sources.find(value => value.key === source.key);
    return Boolean(current && current.enabled !== false && current.generation === source.generation &&
      current.revision === source.revision);
  }

  function sourceAllowed(session, source) {
    return currentSource(session, source);
  }

  function smartSourceAllowed(session, source) {
    if (!source) return false;
    if (source.engine === 'pan') return true;
    const current = session.sources.find(value => value.key === source.key);
    return Boolean(current && isSmartSiteAllowed(current, preferences()));
  }

  function preparedValid(value) {
    return Boolean(value?.descriptor && !value.resourceReleased && Date.now() < (Number.isFinite(value.descriptor.expiresAt)
      ? Math.min(value.descriptor.expiresAt, value.preparedAt + addressLifetime) : value.preparedAt + addressLifetime));
  }

  async function retire(session, value) {
    await media.release(value.resource.id);
    session.leases.delete(value.id);
  }

  function currentManual(session, value) {
    return library.read().manual.find(row => row?.id === value.manualId && row.enabled !== false &&
      row.targetId === session.targetId && manualIdentity(row) === value.manualIdentity);
  }

  function excluded(session, candidate) {
    session.excluded.add(candidate.id);
    if (candidate.fileIdentity && !candidate.variant) session.excluded.add(candidate.fileIdentity);
  }

  function selectionError(session, fallback) {
    return session.feedbackFailure?.code ?? session.selectionError ?? fallback;
  }

  const ctx = {
    data, library, media, rules, sourceRegistry, catalog, debug,
    sessions, sessionsByKey, episodeMaps,
    isClosed: () => closed,
    preferences, get, tracked, calculate, cancelCalculations, publish, detached,
    valid, runIO, currentSource, sourceAllowed, smartSourceAllowed,
    preparedValid, retire, currentManual, excluded, selectionError,
  };

  ctx.metadata = createSessionMetadata(ctx);
  ctx.browsing = createSessionBrowsing(ctx);
  ctx.candidates = createSessionCandidates(ctx);
  ctx.selection = createSessionSelection(ctx);
  ctx.prefetch = createSessionPrefetch(ctx);
  ctx.snapshot = createSessionSnapshot(ctx);
  ctx.lifecycle = createSessionLifecycle(ctx);
  ctx.commands = createSessionCommands(ctx);

  async function refreshPreferences() {
    await library.ready;
    const next = preferences();
    if (!observedReady) {
      observedPreferences = next;
      observedReady = true;
      for (const session of sessions.values()) publish(session);
      return;
    }
    const previous = observedPreferences;
    if (isDeepStrictEqual(previous, next)) return;
    observedPreferences = next;
    const policyFields = ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords'];
    const policyChanged = !isDeepStrictEqual(fields(previous, policyFields), fields(next, policyFields));
    for (const session of sessions.values()) {
      if (policyChanged) await ctx.prefetch.refreshEligibility(session);
      else publish(session);
    }
  }

  function events(id, after = -1, signal) {
    const session = get(id);
    requireValue(Number.isSafeInteger(after) && after >= -1);
    return createSnapshotStream({
      listeners: session.listeners,
      isClosed: () => closed || session.released,
      getSequence: () => session.sequence,
      getSnapshot: () => ctx.snapshot.build(session),
      after,
      signal,
    });
  }

  return {
    open: (...args) => tracked(ctx.lifecycle.open(...args)),
    command: ctx.commands.command,
    attempt: ctx.commands.attempt,
    release: input => ctx.lifecycle.releaseSession(get(input.sessionId), input),
    releaseMedia: ctx.commands.releaseMedia,
    events,
    snapshot: input => ctx.snapshot.build(get(input.sessionId)),
    settings() { return clone(preferences()); },
    async settingsChanged() {
      await refreshPreferences();
    },
    async setSettings(input) {
      requireValue(library.configurationOwner !== 'native', 'PERMISSION_DENIED');
      const next = normalizePreferences(input);
      if (!isDeepStrictEqual(preferences(), next)) {
        await library.update(document => { document.preferences = { ...document.preferences, ...next }; });
      }
      await refreshPreferences();
      return clone(next);
    },
    library(input) { return library.list(input); },
    async removeLibrary(input) {
      requireValue(object(input) && ['history', 'blocks'].includes(input.kind));
      if (input.kind === 'history') {
        requireValue(input.id == null || text(input.id));
        await library.removeHistory(input.id);
      } else {
        requireValue((input.id == null || text(input.id)) && (input.block == null || object(input.block)));
        const blockId = input.id != null ? text(input.id) : (text(input.block?.id) || null);
        const target = input.block != null && !blockId
          ? fields(input.block, ['targetId', 'sourceKey', 'detailId', 'lineId'])
          : null;
        await library.update(document => {
          if (input.id == null && input.block == null) {
            document.blocks = [];
          } else if (blockId) {
            document.blocks = document.blocks.filter(row =>
              (row.id ?? identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId']))) !== blockId);
          } else {
            const targetKey = identity(target);
            document.blocks = document.blocks.filter(row =>
              row.errorCode || identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId'])) !== targetKey);
          }
        });
      }
      return library.list();
    },
    async updateManual(input) {
      requireValue(object(input) && ['save', 'remove'].includes(input.action));
      let previous, updated;
      await library.update(document => {
        if (input.action === 'remove') {
          requireValue(text(input.id));
          previous = document.manual.find(row => row?.id === input.id);
          document.manual = document.manual.filter(row => row?.id !== input.id);
        } else {
          requireValue(object(input.item) && text(input.item.targetId) &&
            (text(input.item.sourceKey) && isIdentifier(input.item.detailId) || object(input.item.share) && text(input.item.provider)));
          const id = input.item.id ?? randomUUID();
          requireValue(text(id));
          const normalizedTargetId = library.normalizeTargetId(input.item.targetId);
          previous = document.manual.find(row => row?.id === id);
          const item = {
            ...fields(input.item, ['kind', 'title', 'name', 'sourceKey', 'detailId', 'provider',
              'seasonHint', 'enabled', 'autoDisable']),
            targetId: normalizedTargetId,
            id,
            failureCount: 0,
          };
          if (input.item.share !== undefined) {
            requireValue(object(input.item.share));
            item.share = fields(input.item.share, ['url', 'password']);
            let url;
            try { url = new URL(item.share.url); } catch { throw fail('INVALID_REQUEST'); }
            requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password);
          }
          updated = item;
          document.manual = [...document.manual.filter(row => row?.id !== item.id), item];
        }
      });
      const affectedTargetIds = new Set([previous?.targetId, updated?.targetId].filter(Boolean));
      for (const session of sessions.values()) {
        if (session.released || !affectedTargetIds.has(session.targetId)) continue;
        await ctx.lifecycle.refreshSources();
      }
      return library.read().manual;
    },
    invalidate: ctx.lifecycle.invalidate,
    updateSourceDisplay: ctx.lifecycle.updateSourceDisplay,
    close() {
      return closing ??= (async () => {
        closed = true;
        for (const controller of calculations) controller.abort();
        const results = await Promise.allSettled([...sessions.values()].map(session =>
          ctx.lifecycle.destroySession(session)));
        await Promise.allSettled([...jobs]);
        sessionsByKey.clear();
        const failure = results.find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
    diagnostics() {
      let activeEventStreams = 0;
      let attachedSessions = 0;
      for (const session of sessions.values()) {
        activeEventStreams += session.listeners?.size ?? 0;
        if (session.attached) attachedSessions++;
      }
      return {
        closed,
        activeSessions: sessions.size,
        attachedSessions,
        activeJobs: jobs.size,
        activeCalculations: calculations.size,
        activeEventStreams,
      };
    },
  };
}
