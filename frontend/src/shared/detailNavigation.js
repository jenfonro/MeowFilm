const text = value => typeof value === 'string' ? value.trim() : '';
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const pageNumber = value => {
  const page = typeof value === 'number' || typeof value === 'string' ? Number(value) : 0;
  return Number.isSafeInteger(page) && page > 0 ? page : 0;
};

export const navigationOf = item => {
  if (!object(item) || !String(item.vod_id ?? '').trim() || text(item.vod_play_from) || text(item.vod_play_url)) return null;
  const nav = object(item.vod_navigation) ? item.vod_navigation : null;
  // folder is an existing script protocol, not a guess based on a pan name.
  const action = text(nav && nav.action) || (item.vod_tag === 'folder' ? 'category' : '');
  if (!/^[a-z][a-z0-9_-]*$/i.test(action)) return null;
  return { ...nav, action, payload: object(nav && nav.payload)
    ? { ...nav.payload } : { id: item.vod_id, ...(action === 'category' ? { page: 1 } : {}) } };
};

export const hasDetailNavigation = raw =>
  Array.isArray(raw && raw.list) && raw.list.some(item => navigationOf(item) || (object(item) && item.vod_navigation));

export function navigationRequestKey(action, payload) {
  const stable = value => Array.isArray(value) ? value.map(stable) : object(value)
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key])])) : value;
  return action + ':' + JSON.stringify(stable(payload));
}

const listTails = new Map();
export async function runProviderListTask(key, task, signal) {
  const previous = listTails.get(key) || Promise.resolve();
  let release;
  const done = new Promise(resolve => { release = resolve; });
  listTails.set(key, done);
  await previous;
  try {
    if (signal && signal.aborted) return;
    return await task();
  } finally {
    release();
    if (listTails.get(key) === done) listTails.delete(key);
  }
}

// Traversal only: leaves enter the caller's existing source/matching logic.
// No script-private ID decoding, play selection, or second detail cache.
export async function walkDetailNavigation(raw, {
  request, visit, onError = () => {}, signal, ownerKey = '', shouldStop = () => false,
  shareIdentity = nav => nav.share_url,
} = {}) {
  const seen = new Set();
  const shares = new Set();
  const stopped = () => (signal && signal.aborted) || shouldStop();
  const report = (item, message) => onError({ item, message });
  const walk = async (doc, ancestors = new Set(), inheritedMode = false, context = null) => {
    if (stopped()) return;
    if (!object(doc)) { report(doc, '未支持的详情结构'); return; }
    const mode = typeof doc.pan_mock === 'boolean' ? doc.pan_mock : inheritedMode;
    if (doc.ok === false) { report(doc, text(doc.message) || text(doc.msg) || '导航请求失败'); return; }
    if (context && mode !== inheritedMode) { report(doc, '网盘解析模式已变更，请刷新详情'); return; }
    if (!Array.isArray(doc.list)) { report(doc, text(doc.message) || text(doc.msg) || '未支持的详情结构'); return; }
    if (doc.message || doc.msg) report(doc, text(doc.message) || text(doc.msg));
    const page = pageNumber(doc.page), pageCount = pageNumber(doc.pagecount);
    if (context && (context.previousPage || (page && pageCount > page))) {
      if (context.previousPage && page <= context.previousPage) { report(doc, '导航分页页码未前进'); return; }
      if (doc.list.length) {
        const key = navigationRequestKey('page', doc.list);
        if (context.pageLists.has(key)) { report(doc, '导航分页重复返回相同数据'); return; }
        context.pageLists.add(key);
      }
    }
    await Promise.all(doc.list.map(async item => {
      if (stopped()) return;
      if (!object(item)) { report(item, '未支持的详情项'); return; }
      const nav = navigationOf(item);
      if (!nav) {
        if (text(item.vod_play_from) || text(item.vod_play_url)) {
          await visit({ ...doc, list: [item], pan_mock: mode });
        } else report(item, text(doc.message) || text(doc.msg) || '未支持的详情结构');
        return;
      }
      const key = navigationRequestKey(nav.action, nav.payload);
      if (ancestors.has(key)) { report(item, '详情导航循环引用'); return; }
      if (seen.has(key)) return;
      seen.add(key);
      const lineage = new Set(ancestors).add(key);
      const supportedShare = nav.share_url && nav.share_flag && nav.provider;
      if (supportedShare) {
        const shareKey = nav.provider + ':' + shareIdentity(nav);
        if (shares.has(shareKey)) return;
        shares.add(shareKey);
        if (mode) {
          await visit({ pan_mock: true, list: [{
            ...item, vod_navigation: undefined,
            vod_play_from: nav.share_flag, vod_play_url: nav.share_url,
          }] });
          return;
        }
      }
      if (stopped()) return;
      try {
        // Only the selected share request owns a provider slot. Release it
        // before handling its response, which may itself contain navigation.
        const load = () => stopped() ? null : request(nav.action, nav.payload);
        const child = supportedShare
          ? await runProviderListTask(ownerKey + ':' + nav.provider, load, signal)
          : await load();
        if (!stopped()) await walk(child, lineage, mode, {
          action: nav.action, payload: nav.payload, previousPage: 0, pageLists: new Set(),
        });
      } catch (error) { report(item, error && error.message ? error.message : '导航请求失败'); }
    }));
    // Advance one page at a time, keeping the script's operation and page key.
    // Repeated data/page numbers report a protocol error, not an arbitrary cap.
    if (context && !stopped() && doc.list.length && page && pageCount > page) {
      const payload = { ...context.payload };
      if ('pg' in payload) payload.pg = page + 1;
      if ('page' in payload || !('pg' in payload)) payload.page = page + 1;
      const key = navigationRequestKey(context.action, payload);
      if (seen.has(key)) return;
      seen.add(key);
      try {
        await walk(await request(context.action, payload), new Set(ancestors).add(key), mode,
          { ...context, payload, previousPage: page });
      } catch (error) { report(doc, error && error.message ? error.message : '导航分页请求失败'); }
    }
  };
  await walk(raw);
}
