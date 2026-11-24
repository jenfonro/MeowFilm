// SPDX-License-Identifier: GPL-3.0-only
// Fixed api-enhanced / iWebPlus search-discovery recipes; see LICENSES/NOTICE.md.
import { array, record, collection, optionalNumber, text } from '../model.mjs';
import { fail } from '../errors.mjs';

export async function wySearchDiscovery(c, { components = ['hot', 'charts', 'suggestions'] }) {
  if (!Array.isArray(components) || !components.every(item => typeof item === 'string')) fail('INVALID_REQUEST');
  return c.cached(['searchDiscovery', [...components].sort()], async fc => {
    async function capture(stage, chartId, action) {
      try { return { value: await action(), failure: null }; }
      catch (error) {
        const code = fc.errorCode(error);
        if (['CANCELLED', 'TIMEOUT', 'STALE_SESSION', 'CLOSED', 'CREDENTIALS_UNAVAILABLE'].includes(code)) throw error;
        return { value: null, failure: { stage, chartId, code } };
      }
    }
    const wantHot = components.includes('hot');
    const wantCharts = components.includes('charts');
    const wantSuggestions = components.includes('suggestions');
    const [hot, top, suggested] = await Promise.all([
      wantHot ? capture('hot', null, async () => {
        const root = await fc.wy('/api/search/pc/chart/list', {}, false, { dataStatus: true }), data = record(root?.data);
        fc.observe('tabInfo.list', data.tabInfo?.list);
        const selected = array(data.tabInfo?.list).find(row => row?.id === 'HOT_SEARCH_SONG#@#');
        if (!selected) return { items: [], canPlay: false };
        let source = data.items?.[selected.id];
        if (source == null || Array.isArray(source) && !source.length)
          source = (await fc.wy('/api/search/pc/chart/detail', { id: selected.id }, false, { dataStatus: true }))?.data?.itemList;
        fc.observe('hot.items', source);
        const items = array(source).map(record).map((row, index) => ({
          keyword: text(row.searchWord), rank: index + 1, score: optionalNumber(row.score),
        }));
        return { items, canPlay: selected.needPlay === true || selected.needPlay === 1 };
      }) : { value: null, failure: null },
      wantCharts ? capture('charts', null, async () => {
        const data = await fc.wy('/api/toplist', {});
        fc.observe('list', data?.list);
        return array(data?.list).map(row => ({ ...collection('wy', row), songs: [] }));
      }) : { value: null, failure: null },
      wantSuggestions ? capture('default', null, async () =>
        text((await fc.wy('/api/search/defaultkeyword/get', {}, false, { dataStatus: true }))?.data?.realkeyword)) : { value: null, failure: null },
    ]);
    const requested = [wantHot ? hot : null, wantCharts ? top : null, wantSuggestions ? suggested : null].filter(Boolean);
    if (requested.length && requested.every(item => item.failure)) fail(requested[0].failure.code);
    return { hot: hot.value?.items ?? [], hotCanPlay: hot.value?.canPlay ?? false,
      suggestions: [...new Set([suggested.value ?? '', ...(hot.value?.items ?? []).map(row => row.keyword)].filter(Boolean))],
      charts: top.value ?? [], failures: [hot.failure, top.failure, suggested.failure].filter(Boolean),
      fetchedAt: fc.clock(), expiresAt: fc.clock() + 600000 };
  }, 600000, { shouldCache: result => !result.failures.length });
}
