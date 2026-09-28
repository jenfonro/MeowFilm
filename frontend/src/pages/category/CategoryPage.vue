<template>
  <main id="categoryPage" class="category-page">
    <div class="category-page__content">
      <header class="category-header ui-page-header">
        <div class="category-header__row ui-page-header__row">
          <button type="button" class="ui-nav-back-btn" aria-label="返回" @click="emit('back')">
            <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="m15 18-6-6 6-6"></path>
            </svg>
          </button>
          <div class="category-header__titleWrap ui-page-header__main">
            <div class="category-header__title">{{ currentCategory.label }}</div>
            <div v-if="currentCategory.subtitle" class="category-header__subtitle">{{ currentCategory.subtitle }}</div>
          </div>
        </div>
      </header>

      <section v-if="filterRows.length" class="category-filter-panel">
        <div
          v-for="row in filterRows"
          :key="row.key"
          class="category-filter-row"
        >
          <div class="category-filter-label">{{ row.label }}</div>
          <div class="category-chip-group" role="tablist">
            <button
              v-for="option in row.options"
              :key="option.value"
              type="button"
              class="category-chip"
              :class="{ 'category-chip--active': option.value === row.value }"
              @click="selectFilter(row.key, option.value)"
            >{{ option.label }}</button>
          </div>
        </div>
      </section>

      <div v-if="statusText" class="category-status">{{ statusText }}</div>

      <section
        class="category-grid"
        :class="{ 'category-grid--empty': !items.length }"
      >
        <MediaCard
          v-for="item in items"
          :key="item.id"
          :item="item"
          :poster-src="displayPosterFor(item.poster)"
          :title-fallback="currentCategory.label"
          card-class="media-card"
          link-aria-label="打开详情页"
          @activate="openCard(item, $event)"
          @contextmenu="openCardContextMenu($event, item)"
        />

        <div v-if="!items.length && !loading" class="category-empty">
          {{ emptyText }}
        </div>
      </section>

      <div v-if="items.length && hasMore" class="category-pagination">
        <button
          type="button"
          class="category-load-more"
          :disabled="loadingMore"
          @click="loadMore"
        >
          {{ loadingMore ? '加载中...' : '加载更多' }}
        </button>
        <div ref="loadMoreSentinel" class="category-load-sentinel" aria-hidden="true"></div>
      </div>
    </div>
    <CardActionContextMenu
      :menu="cardContextMenu"
      :match-block-label="cardMatchBlockMenuLabel"
      :match-block-danger="!cardContextMenu.blocked"
      :match-block-disabled="cardMatchBlockMenuDisabled"
      @recognize="openRecognizeDialogFromMenu"
      @toggle-match-block="toggleMatchBlockMenuItem"
    />
    <TMDBRecognizeDialog
      v-model="recognizeDialogOpen"
      :keyword="recognizeDialogKeyword"
      :bootstrap="props.bootstrap"
      :confirm-busy="recognizeDialogSaving"
      :disable-confirm="!canSubmitRecognizeDialog || recognizeDialogSaving"
      :auto-close-on-select="false"
      @select="submitManualRecognizeDialog"
    />
  </main>
</template>

<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import '../../shared/contextMenu.css';
import { buildDoubanDataUrl } from '../../shared/bootstrap';
import {
  buildCategoryCacheKey,
  getCategoryCacheEntry,
  primeCategoryCacheEntry,
  setCategoryCacheEntry,
} from '../../shared/categoryRuntime';
import CardActionContextMenu from '../../shared/CardActionContextMenu.vue';
import { rewriteDoubanImageUrl } from '../../shared/doubanImage';
import MediaCard from '../../shared/MediaCard.vue';
import TMDBRecognizeDialog from '../../shared/TMDBRecognizeDialog.vue';
import { buildContextMenuClosedState, buildContextMenuOpenState } from '../../shared/contextMenuState';
import { rewriteDisplayPosterUrl } from '../../shared/posterUrl';
import { requestCatSpider } from '../../shared/catpawrunner';
import { useManualRecognizeDialog } from '../../shared/useManualRecognizeDialog';
import { useMatchBlockedIndex } from '../../shared/useMatchBlockedIndex';
import { useSiteActionContextMenu } from '../../shared/useSiteActionContextMenu';
import { normalizeInt, normalizeString } from '../../shared/normalize';

const PAGE_SIZE = 24;
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

const props = defineProps({
  bootstrap: { type: Object, default: () => ({}) },
  categoryType: { type: String, default: 'movie' },
  source: {
    type: Object,
    default: () => ({
      kind: 'douban',
      siteKey: '',
      siteName: '',
      siteApi: '',
      categoryId: '',
      categoryName: '',
      initialItems: [],
      initialHasMore: false,
      initialPage: 1,
      initialSeedCount: 0,
    }),
  },
});

const emit = defineEmits(['back', 'select-type', 'open-item']);

const CATEGORY_CONFIG = {
  movie: {
    label: '电影',
    subtitle: '来自豆瓣的精选电影',
    kind: 'movie',
    rows: [
      {
        key: 'category',
        label: '分类',
        defaultValue: '热门',
        options: [
          { label: '热门电影', value: '热门' },
          { label: '最新电影', value: '最新' },
          { label: '豆瓣高分', value: '豆瓣高分' },
          { label: '冷门佳片', value: '冷门佳片' },
        ],
      },
      {
        key: 'area',
        label: '地区',
        defaultValue: '全部',
        options: [
          { label: '全部', value: '全部' },
          { label: '华语', value: '华语' },
          { label: '欧美', value: '欧美' },
          { label: '韩国', value: '韩国' },
          { label: '日本', value: '日本' },
        ],
      },
    ],
  },
  tv: {
    label: '剧集',
    subtitle: '来自豆瓣的精选剧集',
    kind: 'tv',
    rows: [
      {
        key: 'category',
        label: '分类',
        defaultValue: 'all',
        options: [
          { label: '全部', value: 'all' },
          { label: '动漫', value: 'tv_animation' },
          { label: '纪录片', value: 'tv_documentary' },
        ],
      },
      {
        key: 'area',
        label: '类型',
        defaultValue: 'tv',
        options: [
          { label: '全部', value: 'tv' },
          { label: '国产', value: 'tv_domestic' },
          { label: '欧美', value: 'tv_american' },
          { label: '日本', value: 'tv_japanese' },
          { label: '韩国', value: 'tv_korean' },
        ],
      },
    ],
  },
  anime: {
    label: '动漫',
    subtitle: '来自豆瓣的精选动漫',
    kind: 'bangumi',
    rows: [
      {
        key: 'category',
        label: '分类',
        defaultValue: 'week',
        options: [
          { label: '本周', value: 'week' },
          { label: '今日', value: 'today' },
        ],
      },
      {
        key: 'area',
        label: '星期',
        defaultValue: 'Mon',
        options: [
          { label: '周一', value: 'Mon' },
          { label: '周二', value: 'Tue' },
          { label: '周三', value: 'Wed' },
          { label: '周四', value: 'Thu' },
          { label: '周五', value: 'Fri' },
          { label: '周六', value: 'Sat' },
          { label: '周日', value: 'Sun' },
        ],
      },
    ],
  },
  show: {
    label: '综艺',
    subtitle: '来自豆瓣的精选综艺',
    kind: 'tv',
    rows: [
      {
        key: 'category',
        label: '分类',
        defaultValue: 'all',
        options: [
          { label: '全部', value: 'all' },
        ],
      },
      {
        key: 'area',
        label: '类型',
        defaultValue: 'show',
        options: [
          { label: '全部', value: 'show' },
          { label: '国内', value: 'show_domestic' },
          { label: '国外', value: 'show_foreign' },
        ],
      },
    ],
  },
};

const items = ref([]);
const loading = ref(false);
const loadingMore = ref(false);
const emptyText = ref('暂无相关内容');
const errorText = ref('');
const hasMore = ref(false);
const currentPage = ref(1);
const selectedFilters = ref({});
const siteClassOptions = ref([]);
const loadMoreSentinel = ref(null);
const cardContextMenu = ref(buildContextMenuClosedState({ blocked: false }));
const {
  dialogOpen: recognizeDialogOpen,
  dialogKeyword: recognizeDialogKeyword,
  dialogSaving: recognizeDialogSaving,
  canSubmitDialog: canSubmitRecognizeDialog,
  openManualRecognizeDialog,
  submitManualRecognizeDialog,
} = useManualRecognizeDialog();
let browseSeq = 0;
let loadMoreObserver = null;

const typeResolved = computed(() => {
  return CATEGORY_CONFIG[props.categoryType] ? props.categoryType : 'movie';
});

const sourceResolved = computed(() => {
  const input = props.source && typeof props.source === 'object' ? props.source : {};
  return {
    kind: input.kind === 'site' ? 'site' : 'douban',
    siteKey: typeof input.siteKey === 'string' ? input.siteKey.trim() : '',
    siteName: typeof input.siteName === 'string' ? input.siteName.trim() : '',
    siteApi: typeof input.siteApi === 'string' ? input.siteApi.trim() : '',
    categoryId: typeof input.categoryId === 'string' ? input.categoryId.trim() : '',
    categoryName: typeof input.categoryName === 'string' ? input.categoryName.trim() : '',
    initialItems: Array.isArray(input.initialItems) ? input.initialItems.slice() : [],
    initialHasMore: !!input.initialHasMore,
    initialPage: Number(input.initialPage || 1) || 1,
    initialSeedCount: Number(input.initialSeedCount || 0) || 0,
  };
});

const currentCategory = computed(() => {
  if (sourceResolved.value.kind === 'site') {
    const selectedCategoryId = selectedFilters.value.category || sourceResolved.value.categoryId;
    const hit = siteClassOptions.value.find((item) => item.value === selectedCategoryId);
      return {
        label:
          (hit && hit.label) ||
        sourceResolved.value.categoryName ||
        sourceResolved.value.siteName ||
        '站点分类',
      subtitle: '',
      kind: 'site',
      rows: [],
    };
  }
  return CATEGORY_CONFIG[typeResolved.value] || CATEGORY_CONFIG.movie;
});

const filterRows = computed(() => {
  if (sourceResolved.value.kind === 'site') {
    if (!siteClassOptions.value.length) return [];
    return [
      {
        key: 'category',
        label: '分类',
        options: siteClassOptions.value,
        value:
          selectedFilters.value.category ||
          sourceResolved.value.categoryId ||
          (siteClassOptions.value[0] ? siteClassOptions.value[0].value : ''),
      },
    ];
  }
  return currentCategory.value.rows.map((row) => ({
    ...row,
    value: selectedFilters.value[row.key] || row.defaultValue,
  }));
});

const statusText = computed(() => {
  if (loading.value) return '加载中...';
  if (errorText.value && !items.value.length) return errorText.value;
  return '';
});

const readSettings = () => {
  const settings = props.bootstrap && props.bootstrap.settings ? props.bootstrap.settings : {};
  return {
    doubanDataProxy: typeof settings.doubanDataProxy === 'string' ? settings.doubanDataProxy.trim() : 'server-proxy',
    doubanDataCustom: typeof settings.doubanDataCustom === 'string' ? settings.doubanDataCustom.trim() : '',
    doubanImgProxy: typeof settings.doubanImgProxy === 'string' ? settings.doubanImgProxy.trim() : 'server-proxy',
    doubanImgCustom: typeof settings.doubanImgCustom === 'string' ? settings.doubanImgCustom.trim() : '',
    tmdbImageProxyBase: typeof settings.tmdbImageProxyBase === 'string' ? settings.tmdbImageProxyBase.trim() : '',
  };
};

const displayPosterFor = (poster) => rewriteDisplayPosterUrl(poster, readSettings());

const initFilters = (type) => {
  const cfg = CATEGORY_CONFIG[type] || CATEGORY_CONFIG.movie;
  const next = {};
  cfg.rows.forEach((row) => {
    next[row.key] = row.defaultValue;
  });
  if (type === 'anime' && next.category === 'today') {
    next.area = WEEKDAYS[new Date().getDay()];
  }
  selectedFilters.value = next;
};

const fetchJson = async (url, { timeoutMs = 10000, credentials = 'omit', mode = 'cors' } = {}) => {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetch(url, {
      signal: controller.signal,
      credentials,
      mode,
      headers: { Accept: 'application/json, text/plain, */*' },
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.json();
  } finally {
    clearTimeout(timer);
  }
};

const rewritePoster = (url) => {
  const settings = readSettings();
  return rewriteDoubanImageUrl(url, {
    mode: settings.doubanImgProxy,
    custom: settings.doubanImgCustom,
    defaultMode: 'server-proxy',
  });
};

const buildDetailUrl = (item) => {
  const id = item && item.id ? String(item.id).trim() : '';
  if (!id) return '';
  if (item.isBangumi) return `https://bgm.tv/subject/${encodeURIComponent(id)}`;
  return `https://movie.douban.com/subject/${encodeURIComponent(id)}`;
};

const mapRecentHotItem = (item) => {
  return {
    id: String(item?.id || ''),
    title: String(item?.title || '').trim(),
    poster: rewritePoster(item?.pic?.normal || item?.pic?.large || ''),
    scoreBadge: item?.rating?.value ? Number(item.rating.value).toFixed(1) : '',
    textBadge: '',
    detailUrl: buildDetailUrl({ id: item?.id, isBangumi: false }),
    siteLabel: '',
    sourceKind: 'douban',
  };
};

const mapBangumiItem = (item) => {
  return {
    id: String(item?.id || ''),
    title: String(item?.name_cn || item?.name || '').trim(),
    poster: rewritePoster(
      item?.images?.large ||
      item?.images?.common ||
      item?.images?.medium ||
      item?.images?.small ||
      item?.images?.grid ||
      ''
    ),
    scoreBadge: item?.rating?.score ? Number(item.rating.score).toFixed(1) : '',
    textBadge: '',
    detailUrl: buildDetailUrl({ id: item?.id, isBangumi: true }),
    siteLabel: '',
    sourceKind: 'douban',
  };
};

const normalizeSiteClasses = (data) => {
  const cls = data && Array.isArray(data.class) ? data.class : data && Array.isArray(data.classes) ? data.classes : [];
  return cls
    .map((c) => ({
      label: c && (c.type_name != null ? String(c.type_name) : c.name != null ? String(c.name) : ''),
      value: c && (c.type_id != null ? String(c.type_id) : c.tid != null ? String(c.tid) : ''),
    }))
    .filter((c) => c.label && c.value);
};

const normalizeSitePage = (data) => {
  const list = data && Array.isArray(data.list) ? data.list : [];
  const page = Number(data && (data.page != null ? data.page : 1));
  const pageCount = Number(
    data && (data.pagecount != null ? data.pagecount : data.page_count != null ? data.page_count : 0)
  );
  return {
    items: list
      .map((it) => ({
        id: it && (it.vod_id != null ? String(it.vod_id) : it.id != null ? String(it.id) : ''),
        title: it && (it.vod_name != null ? String(it.vod_name) : it.name != null ? String(it.name) : ''),
        contentKey: it && (it.vod_name != null ? String(it.vod_name) : it.name != null ? String(it.name) : ''),
        poster: it && (it.vod_pic != null ? String(it.vod_pic) : it.pic != null ? String(it.pic) : ''),
        textBadge: it && (it.vod_remarks != null ? String(it.vod_remarks) : it.remark != null ? String(it.remark) : ''),
      }))
      .filter((it) => it.id && it.title)
      .map((it) => ({
        ...it,
        scoreBadge: '',
        detailUrl: '',
        siteLabel: '',
        sourceKind: 'site',
        siteKey: sourceResolved.value.siteKey,
        siteName: sourceResolved.value.siteName,
        spiderApi: sourceResolved.value.siteApi,
        siteDetail: it.id,
      })),
    page: Number.isFinite(page) && page > 0 ? page : 1,
    pageCount: Number.isFinite(pageCount) && pageCount > 0 ? pageCount : 0,
  };
};

const fetchDoubanRecentHot = async ({ type, page }) => {
  return fetchDoubanRecentHotRange({
    type,
    start: (page - 1) * PAGE_SIZE,
    limit: PAGE_SIZE,
  });
};

const fetchDoubanRecentHotRange = async ({ type, start, limit }) => {
  const cfg = CATEGORY_CONFIG[type];
  const category = selectedFilters.value.category || cfg.rows[0].defaultValue;
  const area = selectedFilters.value.area || (cfg.rows[1] ? cfg.rows[1].defaultValue : '');
  const requestCategory =
    type === 'movie'
      ? category
      : type === 'tv'
        ? 'tv'
        : type === 'show'
          ? 'show'
          : category;
  const requestType =
    type === 'movie'
      ? area
      : type === 'tv'
        ? (category && category !== 'all' ? category : area)
        : type === 'show'
          ? area
          : category;
  const params = new URLSearchParams();
  params.set('start', String(Math.max(0, normalizeInt(start))));
  params.set('limit', String(Math.max(1, normalizeInt(limit))));
  params.set('category', requestCategory);
  params.set('type', requestType);
  const path = `/rexxar/api/v2/subject/recent_hot/${encodeURIComponent(cfg.kind)}?${params.toString()}`;
  const settings = readSettings();
  const { url, mode } = buildDoubanDataUrl(path, settings);
  const data =
    mode === 'server-proxy'
      ? await fetchJson(url, { timeoutMs: 10000, credentials: 'same-origin', mode: 'same-origin' })
      : await fetchJson(url, { timeoutMs: 10000, credentials: 'omit', mode: 'cors' });
  const list = Array.isArray(data?.items) ? data.items : [];
  return list.map(mapRecentHotItem).filter((item) => item.id && item.title);
};

const fetchBangumiPage = async ({ page }) => {
  const category = selectedFilters.value.category || 'week';
  let area = selectedFilters.value.area || 'Mon';
  if (category === 'today') {
    area = WEEKDAYS[new Date().getDay()];
  }
  const data = await fetchJson('https://api.bgm.tv/calendar', { timeoutMs: 12000, credentials: 'omit', mode: 'cors' });
  const list = Array.isArray(data) ? data : [];
  const dayMap = new Map();
  list.forEach((entry) => {
    const key = String(entry?.weekday?.en || '').trim();
    if (!key) return;
    dayMap.set(key, Array.isArray(entry?.items) ? entry.items : []);
  });
  const rawItems = dayMap.get(area) || [];
  const mapped = rawItems.map(mapBangumiItem).filter((item) => item.id && item.title);
  const start = (page - 1) * PAGE_SIZE;
  return {
    items: mapped.slice(start, start + PAGE_SIZE),
    hasMore: start + PAGE_SIZE < mapped.length,
  };
};

const fetchSiteClasses = async () => {
  const source = sourceResolved.value;
  const settings = props.bootstrap && props.bootstrap.settings ? props.bootstrap.settings : {};
  const apiBase =
    typeof settings.catpawrunnerApiBase === 'string' ? settings.catpawrunnerApiBase.trim() : '';
  if (!apiBase) throw new Error('catpawrunner 接口未配置');
  if (!source.siteApi) throw new Error('站点 API 未配置');
  const data = await requestCatSpider({
    apiBase,
    action: 'home',
    spiderApi: source.siteApi,
    payload: {},
    timeoutMs: 15000,
  });
  return normalizeSiteClasses(data);
};

const fetchSitePage = async ({ page }) => {
  const source = sourceResolved.value;
  const settings = props.bootstrap && props.bootstrap.settings ? props.bootstrap.settings : {};
  const apiBase =
    typeof settings.catpawrunnerApiBase === 'string' ? settings.catpawrunnerApiBase.trim() : '';
  if (!apiBase) throw new Error('catpawrunner 接口未配置');
  if (!source.siteApi) throw new Error('站点 API 未配置');
  const categoryId =
    selectedFilters.value.category ||
    source.categoryId ||
    (siteClassOptions.value[0] ? siteClassOptions.value[0].value : '');
  if (!categoryId) throw new Error('分类不存在');
  const data = await requestCatSpider({
    apiBase,
    action: 'category',
    spiderApi: source.siteApi,
    payload: {
      id: categoryId,
      page,
      filter: true,
      filters: {},
    },
    timeoutMs: 15000,
  });
  const normalized = normalizeSitePage(data);
  return {
    items: normalized.items,
    hasMore: normalized.pageCount > 0 ? normalized.page < normalized.pageCount : normalized.items.length >= PAGE_SIZE,
  };
};

const applyItems = (nextItems, { reset, hasNextPage }) => {
  if (reset) {
    items.value = nextItems;
  } else {
    const merged = [...items.value];
    const seen = new Set(merged.map((item) => item.id));
    nextItems.forEach((item) => {
      if (seen.has(item.id)) return;
      seen.add(item.id);
      merged.push(item);
    });
    items.value = merged;
  }
  hasMore.value = hasNextPage;
  emptyText.value = nextItems.length ? '' : '暂无相关内容';
};

const getFilterSnapshot = () => ({ ...selectedFilters.value });

const getCategoryCacheKey = () => buildCategoryCacheKey({
  source: sourceResolved.value,
  type: typeResolved.value,
  selectedFilters: getFilterSnapshot(),
  siteClassOptions: siteClassOptions.value,
});

const readCategoryCache = () => {
  return getCategoryCacheEntry(getCategoryCacheKey());
};

const writeCategoryCache = (patch) => {
  const key = getCategoryCacheKey();
  if (!key) return null;
  return setCategoryCacheEntry(key, patch);
};

const primeCategoryCacheFromSource = () => {
  return primeCategoryCacheEntry({
    key: getCategoryCacheKey(),
    source: sourceResolved.value,
  });
};

const loadPage = async ({ reset }) => {
  const seq = ++browseSeq;
  const nextPage = reset ? 1 : currentPage.value + 1;
  errorText.value = '';
  emptyText.value = '暂无相关内容';
  const cached = reset ? readCategoryCache() : null;
  if (reset && cached && Array.isArray(cached.items) && cached.items.length) {
    items.value = cached.items.slice();
    currentPage.value = Math.max(1, normalizeInt(cached.page) || 1);
    hasMore.value = !!cached.hasMore;
    loading.value = false;
    loadingMore.value = false;
    emptyText.value = '';
    if (!(sourceResolved.value.kind === 'douban' && cached.seeded && cached.items.length < PAGE_SIZE)) {
      return;
    }
  }
  if (reset) {
    if (!cached || !Array.isArray(cached.items) || !cached.items.length) {
      currentPage.value = 1;
      hasMore.value = false;
      items.value = [];
    }
    loading.value = true;
  } else {
    loadingMore.value = true;
  }

  try {
    if (sourceResolved.value.kind === 'site') {
      const result = await fetchSitePage({ page: nextPage });
      if (seq !== browseSeq) return;
      applyItems(result.items, { reset, hasNextPage: result.hasMore });
      writeCategoryCache({
        items: items.value.slice(),
        page: nextPage,
        hasMore: result.hasMore,
        seeded: false,
      });
    } else if (typeResolved.value === 'anime') {
      const result = await fetchBangumiPage({ page: nextPage });
      if (seq !== browseSeq) return;
      applyItems(result.items, { reset, hasNextPage: result.hasMore });
      writeCategoryCache({
        items: items.value.slice(),
        page: nextPage,
        hasMore: result.hasMore,
        seeded: false,
      });
    } else {
      let nextItems = [];
      if (reset && cached && cached.seeded && Array.isArray(cached.items) && cached.items.length < PAGE_SIZE) {
        const seedCount = Math.max(0, normalizeInt(cached.seedCount) || cached.items.length);
        const rest = await fetchDoubanRecentHotRange({
          type: typeResolved.value,
          start: seedCount,
          limit: Math.max(0, PAGE_SIZE - cached.items.length),
        });
        if (seq !== browseSeq) return;
        nextItems = [...cached.items, ...rest.filter((item) => !cached.items.some((seed) => seed.id === item.id))];
      } else {
        nextItems = await fetchDoubanRecentHot({ type: typeResolved.value, page: nextPage });
      }
      if (seq !== browseSeq) return;
      applyItems(nextItems, { reset, hasNextPage: nextItems.length === PAGE_SIZE });
      writeCategoryCache({
        items: items.value.slice(),
        page: nextPage,
        hasMore: nextItems.length === PAGE_SIZE,
        seeded: false,
      });
    }
    currentPage.value = nextPage;
  } catch (error) {
    if (seq !== browseSeq) return;
    errorText.value = `加载失败：${error && error.message ? error.message : '未知错误'}`;
    if (!items.value.length) emptyText.value = errorText.value;
  } finally {
    if (seq !== browseSeq) return;
    loading.value = false;
    loadingMore.value = false;
  }
};

const selectFilter = (key, value) => {
  if ((selectedFilters.value[key] || '') === value) return;
  const next = { ...selectedFilters.value, [key]: value };
  if (typeResolved.value === 'anime' && key === 'category' && value === 'today') {
    next.area = WEEKDAYS[new Date().getDay()];
  }
  selectedFilters.value = next;
  void loadPage({ reset: true });
};

const loadMore = () => {
  if (loading.value || loadingMore.value || !hasMore.value) return;
  void loadPage({ reset: false });
};

const closeCardContextMenu = () => {
  cardContextMenu.value = buildContextMenuClosedState({ blocked: false });
};

const getAggregateRules = () => {
  const settings = props.bootstrap && props.bootstrap.settings && typeof props.bootstrap.settings === 'object'
    ? props.bootstrap.settings
    : {};
  return Array.isArray(settings.magicAggregateRegexRules) ? settings.magicAggregateRegexRules : [];
};

const {
  resetIndex: resetCardMatchBlockedIndex,
  loadIndex: loadCardMatchBlockedIndexByKeyword,
  applyIndexMutation: applyCardMatchBlockedIndex,
  isBlocked: isCardMatchBlockedBySiteDetail,
} = useMatchBlockedIndex(getAggregateRules);

const {
  matchBlockMenuLabel: cardMatchBlockMenuLabel,
  matchBlockMenuDisabled: cardMatchBlockMenuDisabled,
  openRecognizeDialogFromMenu,
  toggleMatchBlockMenuItem,
} = useSiteActionContextMenu({
  menu: cardContextMenu,
  openManualRecognizeDialog,
  closeMenu: closeCardContextMenu,
  applyMatchBlockedIndex: applyCardMatchBlockedIndex,
});

const buildCardMenuItem = (item) => {
  const current = item && typeof item === 'object' ? item : null;
  if (!current || sourceResolved.value.kind !== 'site') return null;
  const title = normalizeString(current.contentKey || current.title);
  return {
    title,
    matchKeyword: title,
    poster: normalizeString(current.poster),
    siteKey: normalizeString(current.siteKey) || normalizeString(sourceResolved.value.siteKey),
    spiderApi: normalizeString(current.spiderApi) || normalizeString(sourceResolved.value.siteApi),
    siteDetail: normalizeString(current.siteDetail || current.id),
  };
};

const loadCardMatchBlockedIndex = async (keyword) => {
  return loadCardMatchBlockedIndexByKeyword(keyword);
};

const isCardItemMatchBlocked = (item) => {
  const current = item && typeof item === 'object' ? item : null;
  if (!current) return false;
  return isCardMatchBlockedBySiteDetail(
    normalizeString(current.siteKey),
    normalizeString(current.siteDetail),
  );
};

const openCardContextMenu = async (event, item) => {
  if (sourceResolved.value.kind !== 'site') {
    closeCardContextMenu();
    return;
  }
  const current = buildCardMenuItem(item);
  if (!current) {
    closeCardContextMenu();
    return;
  }
  const keyword = normalizeString(current.matchKeyword || current.title);
  if (keyword) {
    await loadCardMatchBlockedIndex(keyword);
  } else {
    resetCardMatchBlockedIndex();
  }
  cardContextMenu.value = buildContextMenuOpenState({
    event,
    item: current,
    extra: { blocked: isCardItemMatchBlocked(current) },
  });
};

const openCard = (item, event = null) => {
  closeCardContextMenu();
  const current = item && typeof item === 'object' ? item : null;
  if (!current) return;
  const target = event && event.target && typeof event.target.closest === 'function' ? event.target : null;
  if (target && target.closest('.media-card__linkBadge')) return;
  if (sourceResolved.value.kind === 'site') {
    emit('open-item', {
      sourceKind: 'site',
      isTmdbMode: false,
      contentKey: current.contentKey ? String(current.contentKey) : '',
      title: current.title ? String(current.title) : '',
      poster: current.poster ? String(current.poster) : '',
      remark: current.textBadge ? String(current.textBadge) : '',
      siteKey: current.siteKey ? String(current.siteKey) : sourceResolved.value.siteKey,
      siteName: current.siteName ? String(current.siteName) : sourceResolved.value.siteName,
      spiderApi: current.spiderApi ? String(current.spiderApi) : sourceResolved.value.siteApi,
      siteDetail: current.siteDetail ? String(current.siteDetail) : current.id ? String(current.id) : '',
    });
    return;
  }
  emit('open-item', {
    sourceKind: 'douban',
    title: current.title ? String(current.title) : '',
  });
};

const bindLoadMoreObserver = async () => {
  if (!loadMoreObserver) return;
  loadMoreObserver.disconnect();
  await nextTick();
  const el = loadMoreSentinel.value;
  if (!el || !hasMore.value || loading.value) return;
  loadMoreObserver.observe(el);
};

const handleCardContextPointer = (event) => {
  const target = event && event.target ? event.target : null;
  if (target && target.closest && target.closest('.search-matchblock-menu')) return;
  closeCardContextMenu();
};

const handleCardContextEscape = (event) => {
  if (event && event.key === 'Escape') closeCardContextMenu();
};

const initSiteFilters = async () => {
  siteClassOptions.value = [];
  selectedFilters.value = {};
  const classes = await fetchSiteClasses();
  siteClassOptions.value = classes;
  selectedFilters.value = {
    category:
      sourceResolved.value.categoryId ||
      (classes[0] ? classes[0].value : ''),
  };
};

watch(
  [typeResolved, sourceResolved],
  async ([type, source]) => {
    closeCardContextMenu();
    items.value = [];
    hasMore.value = false;
    currentPage.value = 1;
    if (source.kind === 'site') {
      try {
        loading.value = true;
        errorText.value = '';
        emptyText.value = '暂无相关内容';
        await initSiteFilters();
      } catch (error) {
        siteClassOptions.value = [];
        selectedFilters.value = {};
        errorText.value = `加载失败：${error && error.message ? error.message : '未知错误'}`;
        emptyText.value = errorText.value;
        loading.value = false;
        return;
      } finally {
        loading.value = false;
      }
      primeCategoryCacheFromSource();
      void loadPage({ reset: true });
      return;
    }
    siteClassOptions.value = [];
    initFilters(type);
    primeCategoryCacheFromSource();
    void loadPage({ reset: true });
  },
  { immediate: true, deep: true }
);

watch(
  [hasMore, loading, loadingMore, currentPage],
  async () => {
    await bindLoadMoreObserver();
  }
);

onMounted(() => {
  document.addEventListener('click', handleCardContextPointer, true);
  window.addEventListener('keydown', handleCardContextEscape);
  window.addEventListener('scroll', handleCardContextPointer, { passive: true });
  window.addEventListener('resize', handleCardContextPointer, { passive: true });
  if (typeof IntersectionObserver === 'undefined') return;
  loadMoreObserver = new IntersectionObserver(
    (entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return;
      if (loading.value || loadingMore.value || !hasMore.value) return;
      loadMore();
    },
    {
      root: null,
      rootMargin: '240px 0px 240px 0px',
      threshold: 0.01,
    }
  );
  void bindLoadMoreObserver();
});

onBeforeUnmount(() => {
  document.removeEventListener('click', handleCardContextPointer, true);
  window.removeEventListener('keydown', handleCardContextEscape);
  window.removeEventListener('scroll', handleCardContextPointer);
  window.removeEventListener('resize', handleCardContextPointer);
  if (!loadMoreObserver) return;
  loadMoreObserver.disconnect();
  loadMoreObserver = null;
});

</script>

<style>
.category-page {
  margin-bottom: 3.5rem;
}

@media (min-width: 768px) {
  .category-page {
    margin-bottom: 0;
  }
}

.category-page__content {
  margin-bottom: 40px;
}

.category-header {
  margin: 0 0 16px;
}

.category-header__titleWrap {
  min-width: 0;
}

.category-header__title {
  font-size: 2rem;
  line-height: 2.4rem;
  font-weight: 800;
  color: #0f172a;
  letter-spacing: 0.02em;
}

.category-header__subtitle {
  margin-top: 0.25rem;
  font-size: 0.95rem;
  color: #475569;
}

.dark .category-header__title {
  color: #f8fafc;
}

.dark .category-header__subtitle {
  color: rgba(255, 255, 255, 0.65);
}

.category-filter-panel {
  margin: 0 0 24px;
  padding: 1.15rem 1.25rem;
  border-radius: 1.25rem;
  background: rgba(255, 255, 255, 0.75);
  border: 1px solid rgba(226, 232, 240, 0.85);
  backdrop-filter: blur(14px);
  box-shadow: 0 10px 30px rgba(15, 23, 42, 0.06);
}

.dark .category-filter-panel {
  background: rgba(15, 23, 42, 0.55);
  border-color: rgba(255, 255, 255, 0.08);
  box-shadow: none;
}

.category-filter-row {
  display: flex;
  align-items: flex-start;
  gap: 1rem;
  margin-top: 0.75rem;
}

.category-filter-row:first-child {
  margin-top: 0;
}

.category-filter-label {
  flex: 0 0 auto;
  width: 3rem;
  padding-top: 0.5rem;
  font-size: 0.95rem;
  font-weight: 700;
  color: #475569;
}

.dark .category-filter-label {
  color: rgba(255, 255, 255, 0.72);
}

.category-chip-group {
  display: inline-flex;
  flex: 0 0 auto;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: center;
  align-self: flex-start;
  max-width: 100%;
  width: fit-content;
  padding: 0.35rem 0.45rem;
  border-radius: 9999px;
  background: rgba(241, 245, 249, 0.9);
}

.dark .category-chip-group {
  background: rgba(255, 255, 255, 0.06);
}

.category-chip {
  appearance: none;
  border: 0;
  background: transparent;
  border-radius: 9999px;
  padding: 0.45rem 0.9rem;
  font-size: 0.9rem;
  font-weight: 700;
  color: #475569;
  line-height: 1;
  transition: background-color 0.15s ease, color 0.15s ease, box-shadow 0.15s ease;
  cursor: pointer;
}

.category-chip:hover {
  color: #0f172a;
}

.category-chip--active {
  background: #fff;
  color: #0f172a;
  box-shadow: 0 6px 14px rgba(15, 23, 42, 0.1);
}

.dark .category-chip {
  color: rgba(255, 255, 255, 0.75);
}

.dark .category-chip:hover {
  color: #fff;
}

.dark .category-chip--active {
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  box-shadow: none;
}

.category-status {
  margin: 0 0 16px;
  color: #64748b;
  font-size: 0.95rem;
}

.dark .category-status {
  color: rgba(255, 255, 255, 0.65);
}

.category-grid {
  padding: 0 0 32px;
}

.category-grid--empty {
  display: block;
}

.category-empty {
  color: #64748b;
  font-size: 0.95rem;
}

.category-pagination {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 0 0 12px;
}

.category-load-more {
  min-width: 8rem;
  border: 1px solid rgba(226, 232, 240, 0.9);
  border-radius: 9999px;
  padding: 0.75rem 1.25rem;
  background: rgba(255, 255, 255, 0.82);
  color: #0f172a;
  font-size: 0.95rem;
  font-weight: 700;
  box-shadow: 0 10px 24px rgba(15, 23, 42, 0.08);
  transition: background-color 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
}

.category-load-more:hover:not(:disabled) {
  background: #fff;
  box-shadow: 0 14px 28px rgba(15, 23, 42, 0.12);
}

.category-load-more:disabled {
  opacity: 0.7;
  cursor: default;
}

.category-load-sentinel {
  width: 100%;
  height: 1px;
}

.dark .category-empty,
.dark .category-load-more {
  color: rgba(255, 255, 255, 0.82);
}

.dark .category-load-more {
  background: rgba(15, 23, 42, 0.56);
  border-color: rgba(255, 255, 255, 0.08);
  box-shadow: none;
}

.dark .category-load-more:hover:not(:disabled) {
  background: rgba(15, 23, 42, 0.76);
}

@media (max-width: 640px) {
  .category-filter-row {
    flex-direction: column;
    gap: 0.5rem;
  }

  .category-filter-label {
    width: auto;
    padding-top: 0;
  }

  .category-grid {
    gap: 1.25rem 1.25rem;
  }
}

@media (min-width: 1024px) {
  .category-grid {
    padding-left: 4rem;
    padding-right: 4rem;
  }
}
</style>
