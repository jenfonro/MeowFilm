// SPDX-License-Identifier: GPL-3.0-only
// Fixed api-enhanced / iWebPlus search-discovery recipes; see LICENSES/NOTICE.md.
import { array, record, collection, optionalNumber, text } from '../model.mjs';
import { fail } from '../errors.mjs';

export async function wySearchDiscovery(c, { components = ['hot', 'charts', 'suggestions'] }) {
  return c.cached(['searchDiscovery', [...components].sort()], async () => {
    async function capture(stage, chartId, action) {
      try { return { value: await action(), failure: null }; }
      catch (error) {
        const code = c.errorCode(error);
        if (['CANCELLED', 'TIMEOUT', 'STALE_SESSION', 'CLOSED', 'CREDENTIALS_UNAVAILABLE'].includes(code)) throw error;
        return { value: null, failure: { stage, chartId, code } };
      }
    }
    const [hot, top, suggested] = await Promise.all([
      components.includes('hot') ? capture('hot', null, async () => {
        const root = await c.wy('/api/search/pc/chart/list', {}, false, { dataStatus: true }), data = record(root?.data);
        c.observe('tabInfo.list', data.tabInfo?.list);
        const selected = array(data.tabInfo?.list).find(row => row?.id === 'HOT_SEARCH_SONG#@#');
        if (!selected) return { items: [], canPlay: false };
        let source = data.items?.[selected.id];
        if (source == null || Array.isArray(source) && !source.length)
          source = (await c.wy('/api/search/pc/chart/detail', { id: selected.id }, false, { dataStatus: true }))?.data?.itemList;
        c.observe('hot.items', source);
        const items = array(source).map(record).map((row, index) => ({
          keyword: text(row.searchWord), rank: index + 1, score: optionalNumber(row.score),
        }));
        return { items, canPlay: selected.needPlay === true || selected.needPlay === 1 };
      }) : { value: null, failure: null },
      components.includes('charts') ? capture('charts', null, async () => {
        const data = await c.wy('/api/toplist', {});
        c.observe('list', data?.list);
        return array(data?.list).map(row => ({ ...collection('wy', row), songs: [] }));
      }) :
        { value: null, failure: null },
      components.includes('suggestions') ? capture('default', null, async () =>
        text((await c.wy('/api/search/defaultkeyword/get', {}, false, { dataStatus: true }))?.data?.realkeyword)) : { value: null, failure: null },
    ]);
    if (hot.failure && top.failure) fail(hot.failure.code);
    return { hot: hot.value?.items ?? [], hotCanPlay: hot.value?.canPlay ?? false,
      suggestions: [...new Set([suggested.value ?? '', ...(hot.value?.items ?? []).map(row => row.keyword)].filter(Boolean))],
      charts: top.value ?? [], failures: [hot.failure, top.failure, suggested.failure].filter(Boolean),
      fetchedAt: c.clock(), expiresAt: c.clock() + 600000 };
  }, 600000);
}
