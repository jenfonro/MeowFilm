import { fail } from '../errors.mjs';
import { input, page, pageNumber } from '../model.mjs';

export function batchedPlaylistPage(c, { id, page: current = 1, limit = 100, continuation }, {
  platform, batchSize, loadMeta, loadBatch, mapSong,
}) {
  id = input(id); current = pageNumber(current); limit = pageNumber(limit, 100);
  if (!Number.isSafeInteger(current) || current < 1 || !Number.isSafeInteger(limit) || limit < 1) fail('INVALID_REQUEST');
  const offset = continuation == null ? (current - 1) * limit : 0;
  if (!Number.isSafeInteger(offset)) fail('INVALID_REQUEST');
  const firstBatch = pageNumber(continuation?.batch, Math.floor(offset / batchSize));
  const firstIndex = pageNumber(continuation?.index, offset % batchSize);
  if (!Number.isSafeInteger(firstBatch) || firstBatch < 0 || !Number.isSafeInteger(firstIndex) || firstIndex < 0)
    fail('INVALID_REQUEST'); // These values address discrete upstream members.
  return c.cached(['playlist', id, current, firstBatch, firstIndex, limit], async fc => {
    const initial = loadMeta ? await loadMeta(fc, id) : null;
    const items = [];
    let batch = firstBatch, index = firstIndex, info = initial?.info, total = initial?.total ?? null;
    let more = true, stalled = false;
    while (items.length < limit && more) {
      const result = await loadBatch(fc, id, batch, total);
      info ??= result.info;
      total = result.total;
      const selected = result.members.slice(index, index + limit - items.length);
      items.push(...selected.map(mapSong));
      index += selected.length;
      if (index < result.members.length) { more = true; break; }
      more = result.more;
      if (!selected.length && more) { stalled = true; break; }
      batch++; index = 0;
    }
    return { ...page(platform, items, { page: current, limit }, { total, more, ...info }),
      consumed: items.length, continuation: more && !stalled ? { page: current + 1, batch, index } : null };
  }, 3600000);
}

export function batchedPlaylistComplete(c, { id }, {
  platform, loadMeta, loadBatch, mapSong,
}) {
  id = input(id);
  return c.cached(['playlist.complete', id], async fc => {
    const initial = loadMeta ? await loadMeta(fc, id) : null;
    const items = [];
    let batch = 0, info = initial?.info, total = initial?.total ?? null, more, members;
    do {
      const result = await loadBatch(fc, id, batch++, total);
      info ??= result.info;
      total = result.total;
      members = result.members;
      items.push(...members.map(mapSong));
      more = result.more;
    } while (more && members.length);
    return { platform, ...info, items, total, count: items.length,
      more, continuation: null, complete: !more };
  }, 3600000);
}
