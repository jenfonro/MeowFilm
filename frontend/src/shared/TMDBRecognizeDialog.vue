<template>
  <div v-if="modelValue" class="manual-tmdb-modal-mask" @click.self="closeDialog">
    <div class="manual-tmdb-modal">
      <form class="manual-tmdb-modal__toolbar" @submit.prevent="search">
        <button type="button" class="btn-ghost-blue manual-tmdb-modal__close" @click="closeDialog">关闭</button>
        <input
          v-model="searchKeyword"
          class="tv-field manual-tmdb-modal__searchInput"
          type="text"
          autocomplete="off"
          placeholder="搜索 TMDB 标题"
        />
        <button type="submit" class="btn-green" :disabled="searchLoading">
          {{ searchLoading ? '搜索中' : '搜索' }}
        </button>
      </form>

      <div class="manual-tmdb-modal__content">
        <div v-if="searchLoading" class="manual-tmdb-modal__placeholder">搜索中...</div>
        <div v-else-if="searchErrorText" class="manual-tmdb-modal__placeholder">{{ searchErrorText }}</div>
        <div v-else-if="!searchResults.length" class="manual-tmdb-modal__placeholder">{{ emptyPlaceholderText }}</div>
        <div v-else class="manual-tmdb-grid">
          <div v-for="item in searchResults" :key="item.key">
            <MediaCard
              :item="item"
              :poster-src="item.poster"
              :show-link-badge="false"
              :show-text-badge="false"
              :show-score-badge="false"
              :card-class="selectedKey === item.key ? 'media-card manual-tmdb-card is-selected' : 'media-card manual-tmdb-card'"
              @activate="selectResult(item)"
            />
          </div>
        </div>
      </div>

      <div class="manual-tmdb-modal__footer">
        <div class="manual-tmdb-modal__footerLeft">
          <label class="manual-tmdb-option manual-tmdb-option--check">
            <input v-model="autoDisable" type="checkbox" />
            <span>自动失效</span>
          </label>
          <label class="manual-tmdb-option">
            <span>季度补全</span>
            <input
              v-model="seasonHint"
              class="tv-field manual-tmdb-option__input"
              type="text"
              placeholder="S01"
              autocomplete="off"
            />
          </label>
        </div>
        <div class="manual-tmdb-modal__footerRight">
          <button
            type="button"
            class="btn-green"
            :disabled="confirmDisabled"
            @click="confirmSelected"
          >
            {{ confirmLabel }}
          </button>
          <button type="button" class="btn-ghost-blue" @click="closeDialog">取消</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { computed, ref, watch } from 'vue';
import MediaCard from './MediaCard.vue';
import './commonButtons.css';
import './commonFields.css';
import './manualTmdbModal.css';
import { normalizeString } from './normalize';
import { rewriteDisplayPosterUrl } from './posterUrl';
import { requestJson } from './requestJson';
import { fetchTMDBDetailCached } from './tmdbRuntime';
import { getTMDBDetailTitle, getTMDBPosterPath, getTMDBSearchResults, getTMDBSearchTitle, getTMDBYear, normalizeTMDBID, normalizeTMDBMediaType } from './tmdbRaw';

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  keyword: { type: String, default: '' },
  bootstrap: { type: Object, default: () => ({}) },
  confirmText: { type: String, default: '添加' },
  confirmBusyText: { type: String, default: '处理中...' },
  confirmBusy: { type: Boolean, default: false },
  disableConfirm: { type: Boolean, default: false },
  autoCloseOnSelect: { type: Boolean, default: true },
});

const emit = defineEmits(['update:modelValue', 'select']);

const searchKeyword = ref('');
const searchLoading = ref(false);
const searchErrorText = ref('');
const searchResults = ref([]);
const selectedKey = ref('');
const resultMode = ref('manual');
const seasonHint = ref('');
const autoDisable = ref(true);

const selectedResult = computed(() => (
  searchResults.value.find((item) => item.key === selectedKey.value) || null
));
const emptyPlaceholderText = computed(() => (
  resultMode.value === 'manual' ? '暂无已维护数据' : '请输入关键词并搜索'
));
const confirmLabel = computed(() => (
  props.confirmBusy ? String(props.confirmBusyText || '处理中...') : String(props.confirmText || '添加')
));
const confirmDisabled = computed(() => {
  if (!selectedResult.value) return true;
  if (searchLoading.value) return true;
  if (props.confirmBusy) return true;
  return !!props.disableConfirm;
});

const readPosterSettings = () => {
  const settings = props.bootstrap && props.bootstrap.settings && typeof props.bootstrap.settings === 'object'
    ? props.bootstrap.settings
    : {};
  return {
    doubanImgProxy: typeof settings.doubanImgProxy === 'string' ? settings.doubanImgProxy : '',
    doubanImgCustom: typeof settings.doubanImgCustom === 'string' ? settings.doubanImgCustom : '',
    tmdbImageProxyBase: typeof settings.tmdbImageProxyBase === 'string' ? settings.tmdbImageProxyBase : '',
  };
};

const normalizeSearchResult = (item) => {
  const current = item && typeof item === 'object' ? item : null;
  const tmdbType = normalizeTMDBMediaType(current && current.media_type);
  const tmdbId = normalizeTMDBID(current && current.id);
  const title = getTMDBSearchTitle(current);
  if (!tmdbType || tmdbId <= 0 || !title) return null;
  const year = getTMDBYear(current);
  const posterRaw = getTMDBPosterPath(current);
  return {
    key: `${tmdbType}:${tmdbId}`,
    tmdbType,
    tmdbId,
    title,
    poster: rewriteDisplayPosterUrl(posterRaw, readPosterSettings()),
    siteLabel: year > 0 ? String(year) : '',
    detailUrl: '',
    scoreBadge: '',
    textBadge: '',
  };
};

const resetSearchState = () => {
  searchLoading.value = false;
  searchErrorText.value = '';
  searchResults.value = [];
  selectedKey.value = '';
  resultMode.value = 'manual';
};

const closeDialog = () => {
  emit('update:modelValue', false);
};

const openDialogWithKeyword = (value) => {
  const nextKeyword = normalizeString(value);
  searchKeyword.value = nextKeyword;
  searchErrorText.value = '';
  searchResults.value = [];
  selectedKey.value = '';
  resultMode.value = 'manual';
  seasonHint.value = '';
  autoDisable.value = true;
};

const normalizeManualLibraryResult = async (item) => {
  const current = item && typeof item === 'object' ? item : null;
  const tmdbType = normalizeTMDBMediaType(current && current.tmdbType);
  const tmdbId = normalizeTMDBID(current && current.tmdbId);
  if (!tmdbType || tmdbId <= 0) return null;
  const titleFromDB = normalizeString(current && current.title);
  const detail = await fetchTMDBDetailCached({ type: tmdbType, id: tmdbId }).catch(() => null);
  const title = titleFromDB || getTMDBDetailTitle(detail, tmdbType);
  const posterRaw = getTMDBPosterPath(detail);
  const year = getTMDBYear(detail);
  return {
    key: `${tmdbType}:${tmdbId}`,
    tmdbType,
    tmdbId,
    title: title || `TMDB#${tmdbId}`,
    poster: rewriteDisplayPosterUrl(posterRaw, readPosterSettings()),
    siteLabel: year > 0 ? String(year) : '',
    detailUrl: '',
    scoreBadge: '',
    textBadge: '',
  };
};

const loadManualLibraryResults = async () => {
  if (searchLoading.value) return;
  resultMode.value = 'manual';
  searchLoading.value = true;
  searchErrorText.value = '';
  try {
    const data = await requestJson('/api/smart/manual/tmdb/get-list', {
      method: 'GET',
      credentials: 'same-origin',
    });
    const list = Array.isArray(data && data.items) ? data.items : [];
    const results = await Promise.all(list.map((item) => normalizeManualLibraryResult(item)));
    searchResults.value = results.filter(Boolean);
    if (!searchResults.value.some((item) => item.key === selectedKey.value)) selectedKey.value = '';
  } catch (error) {
    searchResults.value = [];
    selectedKey.value = '';
    searchErrorText.value = error && error.message ? String(error.message) : '加载失败';
  } finally {
    searchLoading.value = false;
  }
};

const search = async () => {
  if (searchLoading.value) return;
  const keyword = normalizeString(searchKeyword.value);
  if (!keyword) {
    searchResults.value = [];
    selectedKey.value = '';
    searchErrorText.value = '请输入关键词';
    resultMode.value = 'search';
    return;
  }
  resultMode.value = 'search';
  searchLoading.value = true;
  searchErrorText.value = '';
  try {
    const data = await requestJson(`/api/tmdb/search?q=${encodeURIComponent(keyword)}`, {
      method: 'GET',
      credentials: 'same-origin',
    });
    const results = getTMDBSearchResults(data).map((item) => normalizeSearchResult(item)).filter(Boolean);
    searchResults.value = results;
    if (!results.length) {
      selectedKey.value = '';
      searchErrorText.value = '无搜索结果';
      return;
    }
    if (!results.some((item) => item.key === selectedKey.value)) selectedKey.value = '';
  } catch (error) {
    searchResults.value = [];
    selectedKey.value = '';
    searchErrorText.value = error && error.message ? String(error.message) : '搜索失败';
  } finally {
    searchLoading.value = false;
  }
};

const selectResult = (item) => {
  const key = item && typeof item.key === 'string' ? item.key : '';
  selectedKey.value = key;
};

const confirmSelected = () => {
  const selected = selectedResult.value;
  if (!selected || searchLoading.value || props.confirmBusy || props.disableConfirm) return;
  emit('select', {
    ...selected,
    seasonHint: normalizeString(seasonHint.value),
    autoDisable: !!autoDisable.value,
  });
  if (props.autoCloseOnSelect) closeDialog();
};

watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      openDialogWithKeyword(props.keyword);
      void loadManualLibraryResults();
      return;
    }
    resetSearchState();
  }
);

watch(
  () => props.keyword,
  (value) => {
    if (!props.modelValue) return;
    if (normalizeString(searchKeyword.value)) return;
    searchKeyword.value = normalizeString(value);
  }
);
</script>
