import { computed } from 'vue';
import { emitMatchBlockUpdated, toggleMatchBlockPayload } from './matchBlockActions';
import { normalizeString } from './normalize';

const getMenuState = (menu) => {
  if (!menu || typeof menu !== 'object') return {};
  const state = menu.value;
  return state && typeof state === 'object' ? state : {};
};

const getMenuItem = (menu) => {
  const state = getMenuState(menu);
  const current = state.item;
  return current && typeof current === 'object' ? current : null;
};

const resolveKeyword = (menu, item, getKeyword) => {
  if (typeof getKeyword === 'function') {
    const keyword = normalizeString(getKeyword(item));
    if (keyword) return keyword;
  }
  return normalizeString(item && (item.matchKeyword || item.title));
};

export const useSiteActionContextMenu = ({
  menu = null,
  getKeyword = null,
  openManualRecognizeDialog = null,
  closeMenu = null,
  applyMatchBlockedIndex = null,
} = {}) => {
  const matchBlockMenuLabel = computed(() => {
    const state = getMenuState(menu);
    if (state.busy) return '处理中...';
    return state.blocked ? '取消匹配禁用' : '加入匹配禁用';
  });

  const matchBlockMenuDisabled = computed(() => {
    const state = getMenuState(menu);
    if (state.busy) return true;
    const item = getMenuItem(menu);
    const keyword = resolveKeyword(menu, item, getKeyword);
    const siteKey = normalizeString(item && item.siteKey);
    const siteDetail = normalizeString(item && item.siteDetail);
    return !keyword || !siteKey || !siteDetail;
  });

  const openRecognizeDialogFromMenu = () => {
    const item = getMenuItem(menu);
    if (!item || typeof openManualRecognizeDialog !== 'function') return;
    openManualRecognizeDialog({
      keyword: normalizeString(item.title) || normalizeString(item.contentKey),
      siteKey: normalizeString(item.siteKey),
      spiderApi: normalizeString(item.spiderApi),
      siteDetail: normalizeString(item.siteDetail),
    });
    if (typeof closeMenu === 'function') closeMenu();
  };

  const toggleMatchBlockMenuItem = async () => {
    const state = getMenuState(menu);
    const item = getMenuItem(menu);
    const keyword = resolveKeyword(menu, item, getKeyword);
    if (!item || state.busy || !keyword) return;
    const payload = {
      keyword,
      siteKey: normalizeString(item.siteKey),
      spiderApi: normalizeString(item.spiderApi),
      siteDetail: normalizeString(item.siteDetail),
      poster: normalizeString(item.poster),
    };
    if (!payload.siteKey || !payload.siteDetail) return;
    if (menu && typeof menu === 'object') {
      menu.value = {
        ...state,
        busy: true,
      };
    }
    try {
      const blocked = await toggleMatchBlockPayload({ payload, blocked: !!state.blocked });
      if (typeof applyMatchBlockedIndex === 'function') {
        applyMatchBlockedIndex(payload, blocked);
      }
      emitMatchBlockUpdated({ keyword, payload, blocked });
    } finally {
      if (typeof closeMenu === 'function') closeMenu();
    }
  };

  return {
    matchBlockMenuLabel,
    matchBlockMenuDisabled,
    openRecognizeDialogFromMenu,
    toggleMatchBlockMenuItem,
  };
};
