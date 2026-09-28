import { normalizeString } from './normalize';
import {
  addSmartMatchBlockItem,
  deleteSmartMatchBlockItem,
  fetchBlockedMatchIndex,
} from './searchRuntime';

export const loadMatchBlockedIndexByKeyword = async (keyword, aggregateRules) => {
  const safeKeyword = normalizeString(keyword);
  if (!safeKeyword) return {};
  const rules = Array.isArray(aggregateRules) ? aggregateRules : [];
  const next = await fetchBlockedMatchIndex(safeKeyword, rules).catch(() => ({}));
  return next && typeof next === 'object' ? next : {};
};

export const toggleMatchBlockPayload = async ({ payload, blocked }) => {
  const current = payload && typeof payload === 'object' ? payload : null;
  if (!current) return false;
  if (blocked) {
    await deleteSmartMatchBlockItem(current);
    return false;
  }
  await addSmartMatchBlockItem(current);
  return true;
};

export const emitMatchBlockUpdated = ({ keyword, payload, blocked }) => {
  try {
    window.dispatchEvent(new CustomEvent('tv:smart-matchblock-updated', {
      detail: { keyword, payload, blocked },
    }));
  } catch (_error) {}
};
