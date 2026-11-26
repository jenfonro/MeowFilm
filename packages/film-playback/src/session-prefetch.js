import { compareCandidates } from './policy.js';
import { clone, codeOf, identity, requireValue } from './identity.js';
import { samePlayIntent } from './session-helpers.js';

export function createSessionPrefetch(ctx) {
  const { data, media, debug } = ctx;

  async function clearPrefetch(session) {
    session.prefetchGeneration++;
    const values = [...session.prefetch.values()];
    session.prefetch.clear();
    await Promise.all(values.filter(value => value.resource).map(value => media.release(value.resource.id)));
  }

  async function refreshEligibility(session) {
    session.discoveryComplete = false;
    const generation = session.generation;
    if ([...session.prefetch.values()].some(value => value.candidate &&
        !ctx.candidates.candidateAllowed(session, value.candidate, { gate: false, intent: value.intent }))) {
      ctx.detached(session, clearPrefetch(session));
    }
    if (ctx.isClosed() || session.released || generation !== session.generation) return;
    const pending = session.pending;
    const eligible = !pending || ctx.candidates.candidateAllowed(session, pending.candidate, {
      gate: false,
      explicit: pending.explicit,
    });
    if (pending && !eligible) {
      session.generation++; session.runKey = null;
      void ctx.selection.start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
    } else if (session.runWork && !session.pending && !session.stopped) {
      session.generation++; session.runKey = null;
      void ctx.selection.start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
    }
    ctx.publish(session);
  }

  async function prefetchNeighbors(session) {
    if (!session.shortVideo || !session.current || session.current.resourceReleased || session.released || session.stopped) return;
    const generation = ++session.prefetchGeneration;
    const baseIntent = clone(session.playIntent);
    const usable = () => session.prefetchGeneration === generation && !session.released && !ctx.isClosed() &&
      session.shortVideo && samePlayIntent(session.playIntent, baseIntent) &&
      Boolean(session.current && !session.current.resourceReleased);

    const neighbors = [1, -1].map(direction => {
      const value = ctx.selection.adjacent(session, direction);
      return value ? { ...value, direction } : null;
    }).filter(Boolean);

    const keep = new Set(neighbors.map(item => identity(item.intent)));
    for (const [key, value] of session.prefetch) {
      if (!keep.has(key) || !ctx.preparedValid(value) || value.warming || value.state === 'error') {
        session.prefetch.delete(key);
        if (value.resource) await media.release(value.resource.id);
      } else value.generation = generation;
    }

    const multiSeason = ctx.browsing.isMultiSeasonSession(session);
    for (let index = 0; index < neighbors.length && usable(); index++) {
      const neighbor = neighbors[index];
      const key = identity(neighbor.intent);
      if (session.prefetch.has(key)) continue;
      const entry = { intent: neighbor.intent, state: 'resolving', generation, warming: false };
      session.prefetch.set(key, entry);
      ctx.publish(session);
      try {
        for (const result of session.results) {
          if (result.detail) await ctx.candidates.ensureRecognized(session, result, 'session');
        }
        if (!usable() || session.prefetch.get(key) !== entry) return;
        const candidates = [...session.candidates.values()].filter(cand =>
          ctx.candidates.candidateAllowed(session, cand, { gate: false, intent: neighbor.intent }));
        candidates.sort((a, b) => {
          if (session.affinity) {
            const sameLine = c => c.source.key === session.affinity.sourceKey &&
              (c.detailId ?? c.source.detailId) === session.affinity.detailId &&
              c.lineId === session.affinity.lineId;
            const diff = Number(sameLine(b)) - Number(sameLine(a));
            if (diff) return diff;
          }
          return compareCandidates(a, b, {
            preferences: ctx.preferences(),
            current: session.current.candidate,
            multiSeason,
            preferSeason: neighbor.intent.season ?? 0,
          });
        });
        const candidate = candidates[0];
        requireValue(candidate, 'NO_MATCH');
        entry.candidate = candidate;
        const admitted = () => usable() && session.prefetch.get(key) === entry;
        const descriptor = await data.resolve(candidate, { canSchedule: admitted });
        if (!admitted()) return;
        const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: neighbor.direction === 1 });
        if (!admitted()) { await media.release(resource.id); return; }
        Object.assign(entry, { candidate, descriptor, resource, preparedAt: Date.now(), state: 'prepared' });
        if (neighbor.direction === 1) {
          entry.warming = true;
          try {
            const buffered = await media.prefetch(resource.id);
            if (!usable()) return;
            entry.state = 'buffered'; entry.bytes = buffered.bytes;
          } finally {
            entry.warming = false;
          }
        }
      } catch (error) {
        entry.warming = false;
        if (entry.resource) {
          const failedResource = entry.resource;
          delete entry.resource;
          await media.release(failedResource.id).catch(() => {});
        }
        if (!usable()) return;
        entry.state = 'error'; entry.error = codeOf(error);
        debug?.emit({
          level: 'debug', cat: 'film-playback', sub: 'film.prefetch', event: 'prefetch.failed',
          data: { sessionId: session.id, code: entry.error },
        });
      } finally {
        if (!usable() && entry.generation === generation && session.prefetch.get(key) === entry) {
          session.prefetch.delete(key);
          if (entry.resource) {
            const released = entry.resource;
            delete entry.resource;
            await media.release(released.id);
          }
        }
      }
      ctx.publish(session);
    }
  }

  return { clearPrefetch, refreshEligibility, prefetchNeighbors };
}
