// SPDX-License-Identifier: GPL-3.0-only
// Fixed NetEase song detail batch recipe; see LICENSES/NOTICE.md.
import { array, encodeProtocolJSON, protocolInteger, wySong } from '../model.mjs';

const idOf = song => song.sources[0]?.musicInfo.songmid ?? '';

export async function wyDetails(c, ids) {
  const result = [];
  for (let at = 0; at < ids.length; at += 100) {
    const requested = ids.slice(at, at + 100);
    const data = await c.wy('/api/v3/song/detail', { c: encodeProtocolJSON(requested.map(id => ({ id: protocolInteger(id) }))) }, true);
    c.observe('songs', data?.songs);
    const values = array(data?.songs).map(wySong), known = new Map(values.map(song => [idOf(song), song]));
    result.push(...requested.flatMap(id => known.has(id) ? [known.get(id)] : []),
      ...values.filter(song => !requested.includes(idOf(song))));
  }
  return result;
}
