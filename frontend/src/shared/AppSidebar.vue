<template>
  <div class="app-sidebar-wrap">
    <aside
      id="desktopSidebar"
      data-sidebar="true"
      class="app-sidebar"
      style="backdrop-filter: blur(20px); -webkit-backdrop-filter: blur(20px)"
    >
      <div class="app-sidebar__body">
        <div class="app-sidebar__brand">
          <div class="app-sidebar__brand-box">
            <div class="app-sidebar__brand-inner">
              <span class="app-sidebar__brand-title">{{ bootstrap.siteName }}</span>
            </div>
          </div>
          <button
            id="sidebarToggleBtn"
            class="app-sidebar__toggle"
            type="button"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              width="24"
              height="24"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              class="app-sidebar__toggle-icon"
            >
              <line x1="4" x2="20" y1="12" y2="12"></line>
              <line x1="4" x2="20" y1="6" y2="6"></line>
              <line x1="4" x2="20" y1="18" y2="18"></line>
            </svg>
          </button>
        </div>

        <nav class="app-sidebar__nav">
          <div class="app-sidebar__nav-group">
            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'home' && !activeSiteKeyResolved"
              @click="emitNavigate({ view: 'home' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"></path>
                  <path
                    d="M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"
                  ></path>
                </svg>
              </div>
              <span class="app-sidebar__label">首页</span>
            </button>

            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'search'"
              @click="emitNavigate({ view: 'search' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <circle cx="11" cy="11" r="8"></circle>
                  <path d="m21 21-4.3-4.3"></path>
                </svg>
              </div>
              <span class="app-sidebar__label">搜索</span>
            </button>

            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'category' && !activeSiteKeyResolved && doubanTypeResolved === 'movie'"
              @click="emitNavigate({ view: 'category', categoryType: 'movie' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <rect width="18" height="18" x="3" y="3" rx="2"></rect>
                  <path d="M7 3v18"></path>
                  <path d="M3 7.5h4"></path>
                  <path d="M3 12h18"></path>
                  <path d="M3 16.5h4"></path>
                  <path d="M17 3v18"></path>
                  <path d="M17 7.5h4"></path>
                  <path d="M17 16.5h4"></path>
                </svg>
              </div>
              <span class="app-sidebar__label">电影</span>
            </button>

            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'category' && !activeSiteKeyResolved && doubanTypeResolved === 'tv'"
              @click="emitNavigate({ view: 'category', categoryType: 'tv' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <rect width="20" height="15" x="2" y="7" rx="2" ry="2"></rect>
                  <polyline points="17 2 12 7 7 2"></polyline>
                </svg>
              </div>
              <span class="app-sidebar__label">剧集</span>
            </button>

            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'category' && !activeSiteKeyResolved && doubanTypeResolved === 'anime'"
              @click="emitNavigate({ view: 'category', categoryType: 'anime' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <path
                    d="M12 5c.67 0 1.35.09 2 .26 1.78-2 5.03-2.84 6.42-2.26 1.4.58-.42 7-.42 7 .57 1.07 1 2.24 1 3.44C21 17.9 16.97 21 12 21s-9-3-9-7.56c0-1.25.5-2.4 1-3.44 0 0-1.89-6.42-.5-7 1.39-.58 4.72.23 6.5 2.23A9.04 9.04 0 0 1 12 5Z"
                  ></path>
                  <path d="M8 14v.5"></path>
                  <path d="M16 14v.5"></path>
                  <path d="M11.25 16.25h1.5L12 17l-.75-.75Z"></path>
                </svg>
              </div>
              <span class="app-sidebar__label">动漫</span>
            </button>

            <button
              type="button"
              class="app-sidebar__item"
              :data-active="activePageResolved === 'category' && !activeSiteKeyResolved && doubanTypeResolved === 'show'"
              @click="emitNavigate({ view: 'category', categoryType: 'show' })"
            >
              <div class="app-sidebar__icon">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  width="24"
                  height="24"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  class="app-sidebar__icon-svg"
                >
                  <path d="M16.17 7.83 2 22"></path>
                  <path
                    d="M4.02 12a2.827 2.827 0 1 1 3.81-4.17A2.827 2.827 0 1 1 12 4.02a2.827 2.827 0 1 1 4.17 3.81A2.827 2.827 0 1 1 19.98 12a2.827 2.827 0 1 1-3.81 4.17A2.827 2.827 0 0 1 12 19.98a2.827 2.827 0 0 1-4.17-3.81A1 1 0 1 1 4 12"
                  ></path>
                  <path d="m7.83 7.83 8.34 8.34"></path>
                </svg>
              </div>
              <span class="app-sidebar__label">综艺</span>
            </button>
          </div>

          <div v-if="showSiteNavResolved" class="app-sidebar__site">
            <div class="app-sidebar__section-title">站点导航</div>
            <div class="app-sidebar__site-body">
              <div
                :id="siteNavVariant === 'index' ? 'homeSiteNavList' : undefined"
                ref="siteListEl"
                class="app-sidebar__site-list"
                @scroll.passive="syncSiteNavScrollState"
              >
                <template v-if="siteNavVariant === 'index'">
                  <button
                    v-for="s in homeSitesResolved"
                    :key="s.key"
                    type="button"
                    class="app-sidebar__item"
                    :data-site-key="s.key"
                    :data-site-api="s.api"
                    :data-active="activeSiteKeyResolved && activeSiteKeyResolved === s.key"
                    @click="emitSiteNavigate(s)"
                  >
                    <span class="app-sidebar__label">{{ s.name || s.key }}</span>
                  </button>
                </template>

                <template v-else>
                  <a
                    v-for="s in homeSitesResolved"
                    :key="s.key"
                    class="app-sidebar__item"
                    href="/"
                    :data-site-key="s.key"
                    :data-site-api="s.api"
                    :data-active="activeSiteKeyResolved && activeSiteKeyResolved === s.key"
                  >
                    <span class="app-sidebar__label">{{ s.name || s.key }}</span>
                  </a>
                </template>

                <div
                  v-if="homeSitesResolved.length === 0"
                  class="app-sidebar__empty"
                >
                  无数据
                </div>
              </div>

              <template v-if="showSiteNavOverlays">
                <div
                  id="homeSiteNavOverlayTop"
                  class="app-sidebar__overlay app-sidebar__overlay--top"
                  v-show="showSiteNavResolved && canScrollSiteUp"
                >
                  <button
                    id="homeSiteNavScrollUp"
                    type="button"
                    class="app-sidebar__overlay-btn app-sidebar__overlay-btn--up"
                    aria-label="向上滚动"
                    @click="scrollSiteListBy(-160)"
                  >
                    <svg
                      xmlns="http://www.w3.org/2000/svg"
                      width="16"
                      height="16"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                    >
                      <path d="m18 15-6-6-6 6" />
                    </svg>
                  </button>
                </div>
                <div
                  id="homeSiteNavOverlayBottom"
                  class="app-sidebar__overlay app-sidebar__overlay--bottom"
                  v-show="showSiteNavResolved && canScrollSiteDown"
                >
                  <button
                    id="homeSiteNavScrollDown"
                    type="button"
                    class="app-sidebar__overlay-btn app-sidebar__overlay-btn--down"
                    aria-label="向下滚动"
                    @click="scrollSiteListBy(160)"
                  >
                    <svg
                      xmlns="http://www.w3.org/2000/svg"
                      width="16"
                      height="16"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                    >
                      <path d="m6 9 6 6 6-6" />
                    </svg>
                  </button>
                </div>
              </template>
            </div>
          </div>
        </nav>
      </div>
    </aside>

    <div id="sidebarOffset" class="app-sidebar__offset"></div>
  </div>
</template>

<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';

const emit = defineEmits(['navigate']);

const props = defineProps({
  bootstrap: { type: Object, required: true },
  activePage: { type: String, default: '' },
  doubanType: { type: String, default: '' },
  activeSiteKey: { type: String, default: '' },
  showSiteNav: { type: Boolean, default: true },
  siteNavVariant: { type: String, default: 'links' },
  showSiteNavOverlays: { type: Boolean, default: false },
});

const activePageResolved = computed(() => {
  if (props.activePage) return props.activePage;
  return 'home';
});
const activeSiteKeyResolved = computed(() => {
  if (props.activeSiteKey) return props.activeSiteKey;
  return '';
});
const doubanTypeResolved = computed(() => {
  if (props.doubanType) return props.doubanType;
  return 'movie';
});

const homeSitesResolved = computed(() => {
  const settings = props.bootstrap && typeof props.bootstrap === 'object' && props.bootstrap.settings && typeof props.bootstrap.settings === 'object'
    ? props.bootstrap.settings
    : {};
  const list = Array.isArray(settings.homeSites) ? settings.homeSites : [];
  return list
    .map((site) => ({
      key: site && typeof site.key === 'string' ? site.key.trim() : '',
      name: site && typeof site.name === 'string' ? site.name.trim() : '',
      api: site && typeof site.api === 'string' ? site.api.trim() : '',
    }))
    .filter((site) => site.key && site.name && site.api);
});

const showSiteNavResolved = computed(() => props.showSiteNav && homeSitesResolved.value.length > 0);
const siteListEl = ref(null);
const canScrollSiteUp = ref(false);
const canScrollSiteDown = ref(false);
let siteListResizeObserver = null;

const syncSiteNavScrollState = () => {
  const el = siteListEl.value;
  if (!el) {
    canScrollSiteUp.value = false;
    canScrollSiteDown.value = false;
    return;
  }
  const maxScrollTop = Math.max(0, el.scrollHeight - el.clientHeight);
  const scrollTop = Math.max(0, Number(el.scrollTop) || 0);
  canScrollSiteUp.value = scrollTop > 2;
  canScrollSiteDown.value = maxScrollTop - scrollTop > 2;
};

const scrollSiteListBy = (delta) => {
  const el = siteListEl.value;
  if (!el || !Number.isFinite(delta) || delta === 0) return;
  if (typeof el.scrollBy === 'function') {
    el.scrollBy({ top: delta, behavior: 'smooth' });
  } else {
    el.scrollTop += delta;
  }
  window.requestAnimationFrame(syncSiteNavScrollState);
};

const bindSiteNavScrollState = async () => {
  await nextTick();
  syncSiteNavScrollState();
  if (siteListResizeObserver) {
    siteListResizeObserver.disconnect();
    siteListResizeObserver = null;
  }
  if (typeof ResizeObserver === 'function' && siteListEl.value) {
    siteListResizeObserver = new ResizeObserver(() => syncSiteNavScrollState());
    siteListResizeObserver.observe(siteListEl.value);
  }
};

const emitNavigate = (payload) => {
  emit('navigate', payload);
};

const emitSiteNavigate = (site) => {
  const key = site && typeof site.key === 'string' ? site.key.trim() : '';
  const name = site && typeof site.name === 'string' ? site.name.trim() : '';
  const api = site && typeof site.api === 'string' ? site.api.trim() : '';
  if (!key || !name || !api) return;
  emitNavigate({ view: 'home', sourceKind: 'site', siteKey: key, siteName: name, siteApi: api });
};

onMounted(() => {
  bindSiteNavScrollState();
  window.addEventListener('resize', syncSiteNavScrollState, { passive: true });
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncSiteNavScrollState);
  if (siteListResizeObserver) {
    siteListResizeObserver.disconnect();
    siteListResizeObserver = null;
  }
});

watch(showSiteNavResolved, () => {
  bindSiteNavScrollState();
});

watch(homeSitesResolved, () => {
  bindSiteNavScrollState();
}, { deep: true });
</script>

<style>
.app-sidebar-wrap {
  display: flex;
}

.app-sidebar {
  position: fixed;
  top: 0;
  left: 0;
  height: 100vh;
  width: 16rem;
  z-index: 10;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.4);
  border-right: 1px solid rgba(229, 231, 235, 0.5);
  box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -4px rgba(0, 0, 0, 0.1);
  backdrop-filter: blur(24px);
  transition: all 0.3s ease;
}

.app-sidebar.collapsed {
  width: 64px !important;
  overflow: hidden;
}

.app-sidebar__body {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.app-sidebar__brand {
  position: relative;
  height: 4.75rem;
}

.app-sidebar__brand-box {
  position: absolute;
  inset: 0.75rem 0;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: opacity 0.2s ease;
}

.app-sidebar__brand-inner {
  width: calc(100% - 4rem);
  display: flex;
  justify-content: center;
}

.app-sidebar__brand-title {
  font-size: 1.5rem;
  line-height: 2rem;
  font-weight: 700;
  color: #16a34a;
  letter-spacing: -0.025em;
}

.app-sidebar__toggle {
  position: absolute;
  right: 0.5rem;
  top: 0.75rem;
  bottom: 0.75rem;
  margin: auto 0;
  width: 2rem;
  height: 2rem;
  border-radius: 0.5rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: #6b7280;
  background: transparent;
  border: none;
  cursor: pointer;
  transition: color 0.2s ease, background-color 0.2s ease;
  z-index: 10;
}

.app-sidebar__toggle:hover {
  color: #374151;
  background: rgba(243, 244, 246, 0.5);
}

.app-sidebar__toggle-icon {
  width: 1rem;
  height: 1rem;
}

.app-sidebar__nav {
  padding: 0 0.5rem;
  margin-top: 1rem;
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
}

.app-sidebar__nav-group {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.app-sidebar__item {
  appearance: none;
  -webkit-appearance: none;
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 0.5rem 0.5rem 1rem;
  border: 0;
  border-radius: 0.5rem;
  background: transparent;
  font-size: 0.875rem;
  font-weight: 500;
  color: #374151;
  text-decoration: none;
  min-height: 40px;
  transition: background-color 0.2s ease, color 0.2s ease;
  justify-content: flex-start;
  width: 100%;
}

.app-sidebar__item:hover {
  background: rgba(243, 244, 246, 0.3);
  color: #16a34a;
}

.app-sidebar__item[data-active='true'] {
  background: rgba(34, 197, 94, 0.2);
  color: #15803d;
}

.app-sidebar__icon {
  width: 1rem;
  height: 1rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.app-sidebar__icon-svg {
  width: 1rem;
  height: 1rem;
  color: #6b7280;
}

.app-sidebar__item:hover .app-sidebar__icon-svg {
  color: #16a34a;
}

.app-sidebar__item[data-active='true'] .app-sidebar__icon-svg {
  color: #15803d;
}

.app-sidebar__label {
  white-space: nowrap;
  transition: opacity 0.2s ease;
}

.app-sidebar__site {
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
}

.app-sidebar__section-title {
  padding: 0 1rem;
  margin-bottom: 0.5rem;
  font-size: 0.75rem;
  font-weight: 600;
  color: #9ca3af;
  letter-spacing: 0.05em;
  user-select: none;
  flex-shrink: 0;
}

.app-sidebar__site-body {
  position: relative;
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
}

.app-sidebar__site-list {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  scrollbar-width: none;
  -ms-overflow-style: none;
}

.app-sidebar__site-list::-webkit-scrollbar {
  width: 0;
  height: 0;
}

.app-sidebar__empty {
  padding: 0.5rem 1rem;
  font-size: 0.875rem;
  color: #9ca3af;
  user-select: none;
}

.app-sidebar__overlay {
  position: absolute;
  left: 0;
  right: 0;
  height: 2.5rem;
  display: block;
  z-index: 2;
}

.app-sidebar__overlay--top {
  top: 0;
}

.app-sidebar__overlay--bottom {
  bottom: 0;
}

.app-sidebar__overlay-btn {
  appearance: none;
  -webkit-appearance: none;
  width: 100%;
  height: 100%;
  display: flex;
  border: none;
  background: transparent;
  color: #374151;
  cursor: pointer;
}

.app-sidebar__overlay-btn--up {
  align-items: flex-start;
  justify-content: center;
  padding-top: 0.5rem;
  background: linear-gradient(to bottom, rgba(229, 231, 235, 0.95), rgba(243, 244, 246, 0.7), transparent);
}

.app-sidebar__overlay-btn--down {
  align-items: flex-end;
  justify-content: center;
  padding-bottom: 0.5rem;
  background: linear-gradient(to top, rgba(229, 231, 235, 0.95), rgba(243, 244, 246, 0.7), transparent);
}

.app-sidebar__offset {
  width: 16rem;
  transition: all 0.3s ease;
}

.app-sidebar__offset.collapsed {
  width: 64px !important;
}

.app-sidebar.collapsed .app-sidebar__brand-box {
  display: none !important;
}

.app-sidebar.collapsed .app-sidebar__label {
  display: none !important;
}

.app-sidebar.collapsed .app-sidebar__item {
  justify-content: center !important;
  padding-left: 0.75rem !important;
  padding-right: 0.75rem !important;
  gap: 0 !important;
}

.app-sidebar.collapsed .app-sidebar__section-title,
.app-sidebar.collapsed .app-sidebar__site,
.app-sidebar.collapsed .app-sidebar__overlay {
  display: none !important;
}

.app-sidebar.collapsed .app-sidebar__toggle {
  left: 50% !important;
  right: auto !important;
  transform: translateX(-50%) !important;
}

.dark .app-sidebar {
  background: #0d1624;
  border-color: rgba(255, 255, 255, 0.05);
  color: #e5e7eb;
}

.dark .app-sidebar__brand-title {
  color: #16a34a;
}

.dark .app-sidebar__item {
  color: #e5e7eb;
}

.dark .app-sidebar__item:hover {
  background: rgba(255, 255, 255, 0.1);
  color: #f1f5f9;
}

.dark .app-sidebar__item[data-active='true'] {
  background: #123928;
  color: #4ade80;
}

.dark .app-sidebar__item[data-active='true'] .app-sidebar__icon-svg {
  color: #4ade80;
}

.dark .app-sidebar__icon-svg {
  color: #e5e7eb;
}

.dark .app-sidebar__label {
  color: #e5e7eb;
}
</style>
