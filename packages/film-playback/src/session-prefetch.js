import { compareCandidates } from './policy.js';
import { clone, codeOf, identity, requireValue } from './identity.js';
import { sameEpisode } from './session-helpers.js';

export function createSessionPrefetch(ctx) {
  const { data, media, debug } = ctx;

  async function clearPrefetch(session) {
    session.prefetchGeneration++;
    const values = [...session.prefetch.values()]; session.prefetch.clear();
    await Promise.all(values.filter(value => value.resource).map(value => media.release(value.resource.id)));
  }

  async function refreshEligibility(session) {
    // Discovery is relative to the currently eligible source set, not a
    // permanent fact. A previously purified source may now need searching.
    session.discoveryComplete = false;
    const generation = session.generation;
    if ((await Promise.all([...session.prefetch.values()].map(async value => value.candidate &&
        !await ctx.candidates.allowed(session, value.candidate, { gate: false, target: value.target })))).some(Boolean)) {
      ctx.detached(session, clearPrefetch(session));
    }
    if (ctx.isClosed() || session.released || generation !== session.generation) return;
    const pending = session.pending;
    const eligible = !pending || await ctx.candidates.allowed(session, pending.candidate, { gate: false, explicit: pending.explicit });
    if (ctx.isClosed() || session.released || generation !== session.generation || pending !== session.pending) return;
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
    const target = clone(session.target);
    const usable = () => session.prefetchGeneration === generation && !session.released && !ctx.isClosed() &&
      session.shortVideo && sameEpisode(session.target, target) && !!session.current && !session.current.resourceReleased;
    const neighbors = [1, -1].map(direction => {
      const value = ctx.selection.adjacent(session, direction);
      return value ? { ...value, direction } : null;
    }).filter(Boolean);
    const keep = new Set(neighbors.map(item => identity(item.target)));
    for (const [key, value] of session.prefetch) {
      if (!keep.has(key) || !ctx.preparedValid(value) || value.warming || value.state === 'error') {
        session.prefetch.delete(key);
        if (value.resource) await media.release(value.resource.id);
      } else value.generation = generation;
    }
    for (let index = 0; index < neighbors.length && usable(); index++) {
      const neighbor = neighbors[index], key = identity(neighbor.target);
      if (session.prefetch.has(key)) continue;
      const entry = { target: neighbor.target, state: 'resolving', generation, warming: false };
      session.prefetch.set(key, entry); ctx.publish(session);
      try {
        const available = (await Promise.all(session.results.map(async result => result.detail &&
          (!neighbor.target.metadataProvider || await ctx.candidates.smartResult(session, result, neighbor.target))
          ? ctx.candidates.recognizeResult(session, result, result.detail, neighbor.target) : []))).flat();
        const eligibility = await Promise.all(available.map(candidate =>
          ctx.candidates.allowed(session, candidate, { gate: false, target: neighbor.target })));
        if (!usable() || session.prefetch.get(key) !== entry) return;
        const candidates = available.filter((_, idx) => eligibility[idx]);
        candidates.sort((a, b) => compareCandidates(a, b, { preferences: ctx.preferences(), current: session.current.candidate }));
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
            debug?.emit({
              level: 'debug', cat: 'film-playback', sub: 'film.prefetch', event: 'prefetch.buffered',
              data: { sessionId: session.id, targetId: neighbor.target.id, episode: neighbor.target.episode ?? null, bytes: buffered.bytes },
            });
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
          data: { sessionId: session.id, targetId: neighbor.target.id, code: entry.error },
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
