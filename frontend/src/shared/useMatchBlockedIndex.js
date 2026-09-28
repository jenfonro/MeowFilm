import { ref } from 'vue';
import { applyMatchBlockedIndexMutation, isMatchBlockedBySiteDetail } from './matchBlockIndex';
import { loadMatchBlockedIndexByKeyword } from './matchBlockActions';
import { normalizeString } from './normalize';

export const useMatchBlockedIndex = (resolveAggregateRules) => {
  const index = ref({});

  const resetIndex = () => {
    index.value = {};
    return index.value;
  };

  const loadIndex = async (keyword) => {
    const safeKeyword = normalizeString(keyword);
    if (!safeKeyword) return resetIndex();
    const aggregateRules = typeof resolveAggregateRules === 'function'
      ? resolveAggregateRules()
      : [];
    index.value = await loadMatchBlockedIndexByKeyword(safeKeyword, aggregateRules);
    return index.value;
  };

  const applyIndexMutation = (payload, blocked) => {
    index.value = applyMatchBlockedIndexMutation(index.value, payload, blocked);
    return index.value;
  };

  const isBlocked = (siteKey, siteDetail) => (
    isMatchBlockedBySiteDetail(index.value, siteKey, siteDetail)
  );

  return {
    resetIndex,
    loadIndex,
    applyIndexMutation,
    isBlocked,
  };
};
