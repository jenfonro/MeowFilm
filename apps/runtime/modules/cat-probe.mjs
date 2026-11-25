import { performance } from 'node:perf_hooks';
import { ScriptHostError } from '@meowfilm/script-host';
import { isRawJSON } from '@meowfilm/runtime-bridge/json';
import { canonicalJSON } from './json-canonical.mjs';

const MAX_NAVIGATION_PAGES = 64;
const FATAL_CODES = new Set(['CANCELLED', 'STALE_SESSION', 'HOST_CLOSED', 'CLOSED', 'DISABLED', 'STARTING']);

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value) && !isRawJSON(value);
const prop = (value, key) => isObject(value) && Object.hasOwn(value, key) ? value[key] : undefined;
const textOf = (value, key) => {
  const item = prop(value, key);
  return typeof item === 'string' ? item.trim() : '';
};

function pick(value, ...keys) {
  for (const key of keys) {
    const item = prop(value, key);
    let text = '';
    if (typeof item === 'string') text = item.trim();
    else if (typeof item === 'number' && Number.isFinite(item)) text = String(item);
    else if (isRawJSON(item)) text = item.rawJSON.trim();
    if (text.length > 0) return text;
  }
  return '';
}

function pageNumber(value, key) {
  const raw = pick(value, key);
  if (!/^[1-9][0-9]*$/.test(raw)) return 0;
  const parsed = Number(raw);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 0;
}

function listOf(value) {
  if (Array.isArray(value)) return value;
  const data = prop(value, 'data');
  for (const candidate of [
    prop(value, 'list'),
    data,
    prop(data, 'list'),
    prop(data, 'vod_list'),
    prop(data, 'items'),
  ]) {
    if (Array.isArray(candidate)) return candidate;
  }
  return [];
}

function classesOf(value) {
  const data = prop(value, 'data');
  for (const candidate of [
    prop(value, 'class'),
    prop(value, 'classes'),
    prop(data, 'class'),
    prop(data, 'classes'),
    prop(data, 'types'),
    prop(value, 'types'),
  ]) {
    if (Array.isArray(candidate)) return candidate;
  }
  return [];
}

function playCandidates(value) {
  const from = pick(value, 'vod_play_from', 'play_from', 'playFrom');
  const urls = pick(value, 'vod_play_url', 'play_url', 'playUrl');
  const flags = from.length === 0 ? [] : from.split('$$$');
  const blocks = urls.length === 0 ? [] : urls.split('$$$');
  const result = [];
  const seen = new Set();
  for (let i = 0; i < Math.max(flags.length, blocks.length); i++) {
    const flag = i < flags.length && flags[i].trim().length > 0 ? flags[i].trim() : `源${i + 1}`;
    const block = i < blocks.length ? blocks[i].trim() : '';
    if (block.length === 0) continue;
    const split = flag.includes('|||');
    const subFlags = split ? flag.split('|||') : [flag];
    const subBlocks = split && block.includes('|||') ? block.split('|||') : [block];
    for (let j = 0; j < Math.max(subFlags.length, subBlocks.length); j++) {
      const label = j < subFlags.length && subFlags[j].trim().length > 0 ? subFlags[j].trim() : flag;
      const line = j < subBlocks.length ? subBlocks[j].split('#')[0].trim() : '';
      const separator = line.indexOf('$');
      const id = (separator < 0 ? line : line.slice(separator + 1)).trim();
      if (id.length === 0) continue;
      const key = `${label}\0${id}`;
      if (seen.has(key)) continue;
      seen.add(key);
      result.push({ flag: label, id });
    }
  }
  return result;
}

function playUrl(value) {
  const data = prop(value, 'data');
  for (const item of [prop(value, 'url'), prop(value, 'playUrl'), prop(data, 'url')]) {
    if (typeof item === 'string' && item.trim().length > 0) return item.trim();
    if (!Array.isArray(item)) continue;
    const strings = item
      .filter(part => typeof part === 'string')
      .map(part => part.trim())
      .filter(part => part.length > 0);
    let url;
    for (let i = strings.length - 1; i >= 0; i--) {
      if (/^https?:\/\//i.test(strings[i])) {
        url = strings[i];
        break;
      }
    }
    url ??= strings[0];
    if (url !== undefined) return url;
  }
  return '';
}

function navigationOf(item) {
  if (!isObject(item) || pick(item, 'vod_id').length === 0 ||
      textOf(item, 'vod_play_from').length > 0 || textOf(item, 'vod_play_url').length > 0) {
    return null;
  }
  const nav = prop(item, 'vod_navigation');
  let action = textOf(nav, 'action');
  if (action.length === 0 && textOf(item, 'vod_tag') === 'folder') action = 'category';
  if (!/^[A-Za-z][A-Za-z0-9_-]*$/.test(action)) return null;
  let payload = prop(nav, 'payload');
  if (!isObject(payload)) {
    payload = action === 'category' ? { id: item.vod_id, page: 1 } : { id: item.vod_id };
  }
  return { action, payload };
}

function deduplicateCandidates(candidates) {
  const unique = [];
  const seen = new Set();
  for (const candidate of candidates) {
    const key = `${candidate.flag}\0${candidate.id}`;
    if (seen.has(key)) continue;
    seen.add(key);
    unique.push(candidate);
  }
  return unique;
}

/**
 * Canonical home/category/detail/play/search probe for Cat video sources.
 * Samples up to three videos and the first episode of every declared line;
 * a resolved URL is probe evidence only, never actual media playback proof.
 */
export async function runCatSourceProbe({ invoke, actions, siteApi = '', signal, debug } = {}) {
  const startedAt = debug ? performance.now() : 0;
  const actionSet = new Set(actions);
  const steps = [];

  function checkAborted() {
    if (signal?.aborted) throw new ScriptHostError('CANCELLED');
  }

  function addStep(operation, status, reason) {
    const step = reason === undefined ? { operation, status } : { operation, status, reason };
    steps.push(step);
    debug?.emit({
      level: status === 'failed' ? 'warn' : 'debug',
      cat: 'film-catalog',
      sub: 'cat.probe',
      event: 'probe.step',
      data: step,
    });
  }

  async function call(operation, payload, { initialDetail = false } = {}) {
    checkAborted();
    if (!actionSet.has(operation)) {
      addStep(operation, 'unsupported', 'CAPABILITY_UNAVAILABLE');
      return null;
    }
    try {
      let value = await invoke(operation, payload, signal);
      checkAborted();
      if (typeof value === 'string') {
        try { value = JSON.parse(value); }
        catch {
          addStep(operation, 'failed', 'INVALID_RESPONSE');
          return null;
        }
      }
      if (!isObject(value) && !Array.isArray(value)) {
        addStep(operation, 'failed', 'INVALID_RESPONSE');
        return null;
      }
      let scriptFailure = false;
      for (const response of [value, prop(value, 'data')]) {
        if (!isObject(response)) continue;
        const status = isRawJSON(response.statusCode) ? Number(response.statusCode.rawJSON) : response.statusCode;
        const httpFailure = typeof status === 'number' && Number.isFinite(status) && status >= 400;
        if (httpFailure || response.ok === false || response.success === false || textOf(response, 'error').length > 0) {
          const message = pick(response, 'message', 'msg', 'error');
          addStep(
            operation,
            'failed',
            message.length > 0 ? message : httpFailure ? `HTTP_${isRawJSON(response.statusCode) ? response.statusCode.rawJSON : status}` : 'SCRIPT_ERROR',
          );
          // Initial detail keeps HTTP-success bodies (including script errors)
          // for playback-metadata / search fallback; navigation stays strict.
          if (httpFailure || !initialDetail) return null;
          scriptFailure = true;
          break;
        }
      }
      if (!scriptFailure) addStep(operation, 'success');
      return value;
    } catch (error) {
      checkAborted();
      const code = typeof error?.code === 'string' && error.code.length > 0 ? error.code : 'CALL_FAILED';
      if (FATAL_CODES.has(code)) throw error;
      addStep(operation, code === 'CAPABILITY_UNAVAILABLE' ? 'unsupported' : 'failed', code);
      return null;
    }
  }

  async function detailCandidates(detail) {
    const firstList = listOf(detail);
    const hasNavigation = firstList.some(item => navigationOf(item) !== null || isObject(prop(item, 'vod_navigation')));
    if (!hasNavigation) return firstList.length > 0 ? playCandidates(firstList[0]) : [];

    const candidates = [];
    const seen = new Set();
    const queue = [{
      document: detail,
      action: null,
      payload: null,
      previousPage: 0,
      ancestors: new Set(),
      pageLists: new Set(),
    }];
    let cursor = 0;
    let scheduledPages = 1;
    let limitReached = false;

    while (cursor < queue.length) {
      checkAborted();
      const page = queue[cursor++];
      // Initial detail errors remain available for search fallback,
      // but the navigation walker never traverses an ok:false document.
      if (prop(page.document, 'ok') === false) continue;
      const list = prop(page.document, 'list');
      if (!Array.isArray(list)) {
        addStep('detail', 'failed', 'INVALID_NAVIGATION');
        continue;
      }
      const current = pageNumber(page.document, 'page');
      const count = pageNumber(page.document, 'pagecount');
      if (page.action !== null && (page.previousPage > 0 || (current > 0 && count > current))) {
        if (page.previousPage > 0 && current <= page.previousPage) {
          addStep(page.action, 'failed', 'NAVIGATION_PAGE_NOT_ADVANCING');
          continue;
        }
        if (list.length > 0) {
          const signature = canonicalJSON(list);
          if (page.pageLists.has(signature)) {
            addStep(page.action, 'failed', 'NAVIGATION_REPEATED_PAGE');
            continue;
          }
          page.pageLists.add(signature);
        }
      }
      for (const item of list) {
        const nav = navigationOf(item);
        if (nav === null) {
          candidates.push(...playCandidates(item));
          continue;
        }
        const key = `${nav.action}:${canonicalJSON(nav.payload)}`;
        if (page.ancestors.has(key)) {
          addStep(nav.action, 'failed', 'NAVIGATION_CYCLE');
          continue;
        }
        if (seen.has(key)) continue;
        seen.add(key);
        if (scheduledPages >= MAX_NAVIGATION_PAGES) {
          if (!limitReached) {
            limitReached = true;
            addStep(nav.action, 'failed', 'NAVIGATION_LIMIT');
          }
          break;
        }
        scheduledPages++;
        const child = await call(nav.action, nav.payload);
        if (child !== null) {
          const ancestors = new Set(page.ancestors);
          ancestors.add(key);
          queue.push({
            document: child,
            action: nav.action,
            payload: nav.payload,
            previousPage: 0,
            ancestors,
            pageLists: new Set(),
          });
        }
      }
      if (limitReached) break;
      if (page.action !== null && isObject(page.payload) && list.length > 0 && current > 0 && count > current) {
        const next = { ...page.payload };
        if (Object.hasOwn(next, 'pg')) next.pg = current + 1;
        if (Object.hasOwn(next, 'page') || !Object.hasOwn(next, 'pg')) next.page = current + 1;
        const key = `${page.action}:${canonicalJSON(next)}`;
        if (seen.has(key)) {
          addStep(page.action, 'failed', 'NAVIGATION_CYCLE');
          continue;
        }
        seen.add(key);
        if (scheduledPages >= MAX_NAVIGATION_PAGES) {
          if (!limitReached) {
            limitReached = true;
            addStep(page.action, 'failed', 'NAVIGATION_LIMIT');
          }
          break;
        }
        scheduledPages++;
        const following = await call(page.action, next);
        if (following !== null) {
          queue.push({ ...page, document: following, payload: next, previousPage: current });
        }
      }
    }
    return deduplicateCandidates(candidates);
  }

  async function tryPlay(items) {
    let missing = false;
    const videos = items.filter(isObject).slice(0, 3);
    for (const video of videos) {
      checkAborted();
      let candidates = playCandidates(video);
      if (candidates.length === 0) {
        const id = pick(video, 'vod_id', 'vodId', 'id', 'ID');
        if (id.length === 0) {
          addStep('detail', 'empty', 'MISSING_VIDEO_ID');
          continue;
        }
        const detail = await call('detail', { id, vod_id: id }, { initialDetail: true });
        if (detail === null) continue;
        candidates = await detailCandidates(detail);
        if (candidates.length === 0) {
          missing = true;
          addStep('detail', 'empty', 'MISSING_PLAY_METADATA');
          continue;
        }
      }
      for (const candidate of candidates) {
        const reply = await call('play', { flag: candidate.flag, id: candidate.id, siteApi });
        if (reply === null) continue;
        const url = playUrl(reply);
        if (url.startsWith('meow.pan.v1.')) {
          // A host-owned pan resource is a resolver input, not a resolved media address.
          addStep('play', 'unsupported', 'PAN_RESOLVER_UNAVAILABLE');
          continue;
        }
        if (url.length > 0) return { ok: true, missing };
        addStep('play', 'empty', 'MISSING_PLAY_URL');
      }
    }
    return { ok: false, missing };
  }

  debug?.emit({
    level: 'debug',
    cat: 'film-catalog',
    sub: 'cat.probe',
    event: 'probe.start',
    data: { siteApi, actions: [...actionSet] },
  });

  const home = await call('home', {});
  let category = null;
  if (home !== null) {
    const id = classesOf(home)
      .map(item => pick(item, 'type_id', 'tid', 'id', 'typeId'))
      .find(value => value.length > 0) ?? '0';
    category = await call('category', { id, page: 1, filter: true, filters: {} });
  }
  const categoryItems = category !== null ? listOf(category).slice(0, 10) : [];
  if (category !== null && categoryItems.length === 0) addStep('category', 'empty', 'EMPTY_LIST');
  const categoryPlay = categoryItems.length > 0 ? await tryPlay(categoryItems) : { ok: false, missing: false };
  const searchForPlay = category === null || categoryItems.length === 0 || (!categoryPlay.ok && categoryPlay.missing);
  const search = searchForPlay || categoryPlay.ok ? await call('search', { wd: '斗破', page: 1 }) : null;
  const searchItems = search !== null ? listOf(search) : [];
  if (search !== null && searchItems.length === 0) addStep('search', 'empty', 'EMPTY_LIST');
  const searchPlay = searchForPlay && searchItems.length > 0 ? await tryPlay(searchItems) : { ok: false, missing: false };
  const result = categoryPlay.ok
    ? (search !== null ? 'valid' : 'search_error')
    : searchPlay.ok
      ? (categoryPlay.missing ? 'valid' : 'category_error')
      : 'invalid';
  checkAborted();
  const output = {
    result,
    checkedAt: Date.now(),
    playbackResolved: categoryPlay.ok || searchPlay.ok,
    steps,
  };
  debug?.emit({
    level: 'debug',
    cat: 'film-catalog',
    sub: 'cat.probe',
    event: 'probe.end',
    durationMs: debug ? performance.now() - startedAt : undefined,
    data: {
      siteApi,
      result: output.result,
      playbackResolved: output.playbackResolved,
      stepCount: steps.length,
    },
  });
  return output;
}
