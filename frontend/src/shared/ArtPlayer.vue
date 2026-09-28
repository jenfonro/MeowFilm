<template>
  <div
    class="tv-artplayer"
    :class="{ 'tv-artplayer--fullscreen': isFullscreen, 'tv-artplayer--mobile': isMobile }"
    ref="shell"
  >
    <div ref="container" class="artplayer-root" />

    <teleport :to="teleportTarget || 'body'" :disabled="!teleportTarget">
      <div v-if="toastVisible && toastText" class="yt-toast" :class="{ 'yt-toast--sticky': toastSticky }" aria-live="polite">
        {{ toastText }}
      </div>
      <div v-show="showBufferRing" class="m-buffer-mask" aria-hidden="true"></div>
      <div v-show="showBufferRing" class="m-buffer-ring" aria-hidden="true"></div>

      <div class="yt-ui" :class="{ 'yt-ui--show': overlayVisible }">
        <div v-if="portraitMode" class="yt-top" @click.stop @mousedown.stop @touchstart.stop>
          <button class="yt-top__back" type="button" aria-label="退出竖屏" title="退出竖屏" @click.stop="emitExitPortrait">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M15 6l-6 6 6 6" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <div class="yt-top__title">{{ portraitTopText }}</div>
        </div>
	      <div v-if="!isMobile" class="yt-bar" @click.stop @mousedown.stop @touchstart.stop>
	        <div class="yt-progress" :style="{ '--yt-progress-p': progressFrac, '--yt-buffer-p': bufferedFrac }">
	          <div class="yt-progress__track" aria-hidden="true">
	            <div class="yt-progress__buffer" />
	            <div class="yt-progress__fill" />
	          </div>
	          <input
	            class="yt-progress__range"
	            type="range"
            min="0"
            :max="Math.max(duration, 0)"
            step="0.1"
            :value="Math.min(displayTime, duration)"
            :disabled="!duration"
            @mousedown.stop="onSeekPointerDown"
            @touchstart.stop="onSeekPointerDown"
            @click.stop
            @input="onSeekPreview"
            @change="onSeekCommit"
            @mouseup.stop="onSeekPointerUp"
            @touchend.stop="onSeekPointerUp"
            @blur="onSeekCancel"
          />
        </div>
        <div class="yt-row" ref="desktopRowEl">
          <div class="yt-pill yt-left" ref="leftPillEl">
            <button class="yt-btn" type="button" :aria-label="playing ? '暂停' : '播放'" @click.stop="togglePlay">
              <svg viewBox="0 0 24 24" class="yt-ico">
                <template v-if="playing">
                  <path fill="currentColor" d="M6 5h4v14H6V5zm8 0h4v14h-4V5z" />
                </template>
                <template v-else>
                  <path fill="currentColor" d="M8 5v14l11-7L8 5z" />
                </template>
              </svg>
            </button>

            <button class="yt-btn" type="button" aria-label="上一集" @click.stop="emitEpisodeDelta(-1)">
              <svg viewBox="0 0 24 24" class="yt-ico">
                <path fill="currentColor" d="M6 6h2v12H6V6zm3.5 6L18 6v12l-8.5-6z" />
              </svg>
            </button>
            <button class="yt-btn" type="button" aria-label="下一集" @click.stop="emitEpisodeDelta(1)">
              <svg viewBox="0 0 24 24" class="yt-ico">
                <path fill="currentColor" d="M16 6h2v12h-2V6zM6 18V6l8.5 6L6 18z" />
              </svg>
            </button>

            <div class="yt-volume" @mouseenter="volumeHover = true" @mouseleave="volumeHover = false">
              <button class="yt-btn" type="button" :aria-label="muted ? '取消静音' : '静音'" @click.stop="toggleMute">
                <svg viewBox="0 0 24 24" class="yt-ico">
                  <template v-if="muted || volume <= 0.01">
                    <path
                      fill="currentColor"
                      d="M16.5 12a4.5 4.5 0 0 0-1.17-3.02l-1.42 1.42A2.5 2.5 0 0 1 14.5 12c0 .68-.27 1.3-.7 1.76l1.42 1.42A4.5 4.5 0 0 0 16.5 12z"
                      opacity="0.0"
                    />
                    <path
                      fill="currentColor"
                      d="M3 10v4h4l5 5V5L7 10H3zm13.59 2 2.7 2.7-1.41 1.41L15.18 13l-2.7 2.7-1.41-1.41 2.7-2.7-2.7-2.7 1.41-1.41 2.7 2.7 2.7-2.7 1.41 1.41-2.7 2.7z"
                    />
                  </template>
                  <template v-else>
                    <path
                      fill="currentColor"
                      d="M3 10v4h4l5 5V5L7 10H3zm13.5 2c0-1.77-1.02-3.29-2.5-4.03v8.06A4.5 4.5 0 0 0 16.5 12z"
                    />
                  </template>
                </svg>
              </button>
              <div class="yt-volume__slider" :data-show="volumeHover ? 'true' : 'false'">
                <input
                  class="yt-volume__range"
                  type="range"
                  min="0"
                  max="1"
                  step="0.01"
                  :value="muted ? 0 : volume"
                  @mousedown.stop
                  @click.stop
                  @input.stop="onVolume"
                />
              </div>
            </div>

            <div class="yt-time">{{ displayTimeLabel }}</div>
          </div>

          <div class="yt-pill yt-right" ref="rightPillEl">
            <div v-for="m in extraMenusVisible" :key="m.key" class="yt-proxy" :ref="(el) => setExtraMenuEl(m.key, el)">
              <button
                class="yt-proxy__btn"
                type="button"
                :disabled="!!m.disabled"
                :aria-label="m.ariaLabel || m.label"
                :data-open="extraMenuOpenKey === m.key ? 'true' : 'false'"
                @click.stop="toggleExtraMenu(m.key)"
              >
                {{ m.label }}
              </button>
              <div class="yt-proxy__menu" :class="{ 'yt-proxy__menu--open': extraMenuOpenKey === m.key }">
                <button
                  v-for="o in m.options"
                  :key="`${m.key}::${o.value}`"
                  type="button"
                  class="yt-proxy__item"
                  :data-active="String(o.value) === String(m.value) ? 'true' : 'false'"
                  @click.stop="selectExtraMenu(m.key, o.value)"
                >
                  {{ o.label }}
                </button>
              </div>
            </div>

            <button
              v-for="a in extraActionsVisible"
              :key="a.key"
              class="yt-proxy__btn"
              type="button"
              :disabled="!!a.disabled"
              :aria-label="a.ariaLabel || a.label"
              @click.stop="fireExtraAction(a.key)"
            >
              {{ a.label }}
            </button>

            <div v-if="showGoProxyControl" class="yt-proxy" ref="goProxyEl">
              <button
                class="yt-proxy__btn"
                type="button"
                :aria-label="`GoProxy：${goProxyLabel}`"
                :data-open="goProxyMenuOpen ? 'true' : 'false'"
                @click.stop="toggleGoProxyMenu"
              >
                {{ goProxyLabel }}
              </button>
              <div class="yt-proxy__menu" :class="{ 'yt-proxy__menu--open': goProxyMenuOpen }">
                <button
                  v-for="s in goProxyOptions"
                  :key="s.base"
                  type="button"
                  class="yt-proxy__item"
                  :data-active="s.base === goProxySelectedBase ? 'true' : 'false'"
                  @click.stop="selectGoProxy(s.base)"
                >
                  {{ s.label }}
                </button>
              </div>
            </div>
            <button
              v-if="showPipControl"
              class="yt-btn"
              type="button"
              :aria-label="isPip ? '退出画中画' : '画中画'"
              :data-active="isPip ? 'true' : 'false'"
              @click.stop="togglePip"
            >
              <svg viewBox="0 0 24 24" class="yt-ico">
                <template v-if="!isPip">
                  <path
                    fill="currentColor"
                    d="M19 7H5v10h14V7zm0-2a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h14z"
                    opacity="0.55"
                  />
                  <path fill="currentColor" d="M13 11h6v4h-6v-4z" />
                </template>
                <template v-else>
                  <path
                    fill="currentColor"
                    d="M19 7H5v10h14V7zm0-2a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h14z"
                    opacity="0.55"
                  />
                  <path fill="currentColor" d="M14 10h-4v4h4v-4z" />
                </template>
              </svg>
            </button>

            <div class="yt-setting" ref="settingEl">
              <button class="yt-btn" type="button" aria-label="设置" @click.stop="toggleSettingsMenu">
                <svg viewBox="0 0 24 24" class="yt-ico">
                  <path
                    fill="currentColor"
                    d="M19.14 12.94c.04-.31.06-.63.06-.94s-.02-.63-.06-.94l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.6-.22l-2.39.96a7.027 7.027 0 0 0-1.63-.94l-.36-2.54A.5.5 0 0 0 13.9 1h-3.8a.5.5 0 0 0-.49.42l-.36 2.54c-.58.22-1.12.52-1.63.94l-2.39-.96a.5.5 0 0 0-.6.22L2.71 7.48a.5.5 0 0 0 .12.64l2.03 1.58c-.04.31-.06.63-.06.94s.02.63.06.94L2.83 14.52a.5.5 0 0 0-.12.64l1.92 3.32a.5.5 0 0 0 .6.22l2.39-.96c.5.41 1.05.72 1.63.94l.36 2.54a.5.5 0 0 0 .49.42h3.8a.5.5 0 0 0 .49-.42l.36-2.54c.58-.22 1.12-.52 1.63-.94l2.39.96a.5.5 0 0 0 .6-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.5A3.5 3.5 0 1 1 12 8a3.5 3.5 0 0 1 0 7.5z"
                  />
                </svg>
              </button>
              <div class="yt-setting__menu" :class="{ 'yt-setting__menu--open': settingsOpen }">
                <div class="yt-setting__section">
                  <div class="yt-setting__title">播放速度</div>
                  <div class="yt-setting__grid">
                    <button
                      v-for="r in rates"
                      :key="r"
                      type="button"
                      class="yt-setting__item"
                      :data-active="Math.abs(playbackRate - r) < 0.001 ? 'true' : 'false'"
                      @click="setRate(r)"
                    >
                      {{ r }}x
                    </button>
                  </div>
                </div>
                <div class="yt-setting__section">
                  <div class="yt-setting__title">画面比例</div>
                  <div class="yt-setting__grid">
                    <button
                      v-for="a in ratios"
                      :key="a"
                      type="button"
                      class="yt-setting__item"
                      :data-active="aspectRatio === a ? 'true' : 'false'"
                      @click="setRatio(a)"
                    >
                      {{ a === 'default' ? '默认' : a }}
                    </button>
                  </div>
                </div>
              </div>
            </div>

            <button
              class="yt-btn"
              type="button"
              :aria-label="isFullscreen ? '退出全屏' : '全屏'"
              @click.stop="toggleFullscreen"
            >
              <svg viewBox="0 0 24 24" class="yt-ico">
                <template v-if="!isFullscreen">
                  <path
                    fill="currentColor"
                    d="M7 7h3V5H5v5h2V7zm10 0v3h2V5h-5v2h3zm-7 12H7v-3H5v5h5v-2zm9-3h-2v3h-3v2h5v-5z"
                  />
                </template>
                <template v-else>
                  <path
                    fill="currentColor"
                    d="M5 16h3v3h2v-5H5v2zm0-6h5V5H8v3H5v2zm14 6v-2h-5v5h2v-3h3zm-5-11v5h5V8h-3V5h-2z"
                  />
                </template>
              </svg>
            </button>
          </div>
        </div>
      </div>
      <div v-else class="m-bar" @click.stop @mousedown.stop @touchstart.stop>
        <div class="yt-setting m-setting" ref="settingEl">
          <button class="yt-btn" type="button" aria-label="设置" @click.stop="toggleSettingsMenu">
            <svg viewBox="0 0 24 24" class="yt-ico">
              <path
                fill="currentColor"
                d="M19.14 12.94c.04-.31.06-.63.06-.94s-.02-.63-.06-.94l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.6-.22l-2.39.96a7.027 7.027 0 0 0-1.63-.94l-.36-2.54A.5.5 0 0 0 13.9 1h-3.8a.5.5 0 0 0-.49.42l-.36 2.54c-.58.22-1.12.52-1.63.94l-2.39-.96a.5.5 0 0 0-.6.22L2.71 7.48a.5.5 0 0 0 .12.64l2.03 1.58c-.04.31-.06.63-.06.94s.02.63.06.94L2.83 14.52a.5.5 0 0 0-.12.64l1.92 3.32a.5.5 0 0 0 .6.22l2.39-.96c.5.41 1.05.72 1.63.94l.36 2.54a.5.5 0 0 0 .49.42h3.8a.5.5 0 0 0 .49-.42l.36-2.54c.58-.22 1.12-.52 1.63-.94l2.39.96a.5.5 0 0 0 .6-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.5A3.5 3.5 0 1 1 12 8a3.5 3.5 0 0 1 0 7.5z"
              />
            </svg>
          </button>
          <div class="yt-setting__menu" :class="{ 'yt-setting__menu--open': settingsOpen }">
            <div class="yt-setting__section">
              <div class="yt-setting__title">播放速度</div>
              <div class="yt-setting__grid">
                <button
                  v-for="r in rates"
                  :key="r"
                  type="button"
                  class="yt-setting__item"
                  :data-active="Math.abs(playbackRate - r) < 0.001 ? 'true' : 'false'"
                  @click="setRate(r)"
                >
                  {{ r }}x
                </button>
              </div>
            </div>
            <div class="yt-setting__section">
              <div class="yt-setting__title">画面比例</div>
              <div class="yt-setting__grid">
                <button
                  v-for="a in ratios"
                  :key="a"
                  type="button"
                  class="yt-setting__item"
                  :data-active="aspectRatio === a ? 'true' : 'false'"
                  @click="setRatio(a)"
                >
                  {{ a === 'default' ? '默认' : a }}
                </button>
              </div>
            </div>
          </div>
        </div>

	        <div class="yt-progress m-progress" :style="{ '--yt-progress-p': progressFrac, '--yt-buffer-p': bufferedFrac }">
	          <div class="yt-progress__track" aria-hidden="true">
	            <div class="yt-progress__buffer" />
	            <div class="yt-progress__fill" />
	          </div>
	          <input
	            class="yt-progress__range"
	            type="range"
            min="0"
            :max="Math.max(duration, 0)"
            step="0.1"
            :value="Math.min(displayTime, duration)"
            :disabled="!duration"
            @mousedown.stop="onSeekPointerDown"
            @touchstart.stop="onSeekPointerDown"
            @click.stop
            @input="onSeekPreview"
            @change="onSeekCommit"
            @mouseup.stop="onSeekPointerUp"
            @touchend.stop="onSeekPointerUp"
            @blur="onSeekCancel"
          />
        </div>

        <button class="yt-btn m-fullscreen" type="button" :aria-label="isFullscreen ? '退出全屏' : '全屏'" @click.stop="toggleFullscreen">
          <svg viewBox="0 0 24 24" class="yt-ico">
            <template v-if="!isFullscreen">
              <path fill="currentColor" d="M7 7h3V5H5v5h2V7zm10 0v3h2V5h-5v2h3zm-7 12H7v-3H5v5h5v-2zm9-3h-2v3h-3v2h5v-5z" />
            </template>
            <template v-else>
              <path fill="currentColor" d="M5 16h3v3h2v-5H5v2zm0-6h5V5H8v3H5v2zm14 6v-2h-5v5h2v-3h3zm-5-11v5h5V8h-3V5h-2z" />
            </template>
          </svg>
        </button>

        <div class="m-center" :class="{ 'm-center--show': uiVisible || !playing }">
          <div class="m-center__controls" @click.stop @mousedown.stop @touchstart.stop>
            <button class="m-btn m-btn--skip" type="button" aria-label="上一集" @click.stop="emitEpisodeDelta(-1)">
              <svg viewBox="0 0 24 24" class="m-ico">
                <path fill="currentColor" d="M6 6h2v12H6V6zm3.5 6L18 6v12l-8.5-6z" />
              </svg>
            </button>
            <button
              class="m-btn m-btn--play"
              type="button"
              :data-loading="buffering ? 'true' : 'false'"
              :aria-label="playing ? '暂停' : '播放'"
              @click.stop="togglePlay"
            >
              <svg viewBox="0 0 24 24" class="m-ico m-ico--play">
                <template v-if="playing">
                  <path fill="currentColor" d="M6 5h4v14H6V5zm8 0h4v14h-4V5z" />
                </template>
                <template v-else>
                  <path fill="currentColor" d="M8 5v14l11-7L8 5z" />
                </template>
              </svg>
            </button>
            <button class="m-btn m-btn--skip" type="button" aria-label="下一集" @click.stop="emitEpisodeDelta(1)">
              <svg viewBox="0 0 24 24" class="m-ico">
                <path fill="currentColor" d="M16 6h2v12h-2V6zM6 18V6l8.5 6L6 18z" />
              </svg>
            </button>
          </div>
        </div>
      </div>
      </div>
    </teleport>
  </div>
</template>

<script setup>
	import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
	import Artplayer from 'artplayer';

	const emit = defineEmits([
		'loadedmetadata',
		'videoinfo',
		'error',
		'buffering',
		'playing',
		'firstframe',
		'ended',
		'timeupdate',
		'goproxyselect',
		'extramenuselect',
		'extraaction',
    'exit-portrait',
    'episodedelta',
	]);

	const props = defineProps({
		url: { type: String, default: '' },
		poster: { type: String, default: '' },
		headers: { type: Object, default: () => ({}) },
		title: { type: String, default: '' },
		autoplay: { type: Boolean, default: true },
		showBufferRing: { type: Boolean, default: false },
		goProxyOptions: { type: Array, default: () => [] }, // [{ base, label }]
		goProxySelectedBase: { type: String, default: '' },
		goProxyLabel: { type: String, default: '' },
		statsExtra: { type: Object, default: () => ({}) },
		extraMenus: { type: Array, default: () => [] },
		extraActions: { type: Array, default: () => [] },
		toastText: { type: String, default: '' },
		toastSticky: { type: Boolean, default: false },
    portraitMode: { type: Boolean, default: false },
    portraitTopText: { type: String, default: '' },
	});

const container = ref(null);
const shell = ref(null);
const settingEl = ref(null);
const goProxyEl = ref(null);
const desktopRowEl = ref(null);
const leftPillEl = ref(null);
const rightPillEl = ref(null);
const extraMenuEls = new Map();
const teleportTarget = ref(null);

	const extraMenuOpenKey = ref('');

let art = null;
let cleanupPipListeners = null;
let cleanupFsListeners = null;
let cleanupNativeVideoListeners = null;
let cleanupPlayerElListeners = null;
let cleanupNoCorsEnforcer = null;
let cleanupInfoExtraObserver = null;
let cleanupDesktopLayoutObserver = null;
let infoExtraSyncing = false;

let timeUpdateRaf = 0;
let timeUpdatePending = 0;
let timeUpdateEmitAt = 0;
let desktopClickTimer = 0;
let rightFitRaf = 0;
let rightFitSyncing = false;
let rightFitReschedule = false;

const playing = ref(false);
const currentTime = ref(0);
const duration = ref(0);
const bufferedEnd = ref(0);
const scrubTime = ref(0);
const scrubbing = ref(false);
const pointerScrubbing = ref(false);
const volume = ref(0.7);
const muted = ref(false);
const buffering = ref(false);
const playbackRate = ref(1);
const aspectRatio = ref('default');
	const settingsOpen = ref(false);
	const goProxyMenuOpen = ref(false);
	const volumeHover = ref(false);
	const uiVisible = ref(true);
	const desktopControlsVisible = ref(true);
const rightHiddenCount = ref(0);
const isFullscreen = ref(false);
const isPip = ref(false);
const isMobile = ref(false);
const isIos = ref(false);
const portraitMode = computed(() => !!props.portraitMode);
const portraitTopText = computed(() => String(props.portraitTopText || '').trim());
const overlayVisible = computed(() => {
	  const hasExtraMenu = !!extraMenuOpenKey.value;
	  if (isMobile.value) return uiVisible.value || !playing.value || settingsOpen.value || goProxyMenuOpen.value || hasExtraMenu;
	  return desktopControlsVisible.value || settingsOpen.value || goProxyMenuOpen.value || hasExtraMenu || !playing.value;
	});

const emitExitPortrait = () => {
  try {
    emit('exit-portrait');
  } catch (_e) {}
};

const extraMenusResolved = computed(() => {
  const list = Array.isArray(props.extraMenus) ? props.extraMenus : [];
  return list
    .map((m) => {
      const key = m && m.key != null ? String(m.key).trim() : '';
      const label = m && m.label != null ? String(m.label) : '';
      const ariaLabel = m && m.ariaLabel != null ? String(m.ariaLabel) : '';
      const value = m && m.value != null ? String(m.value) : '';
      const disabled = !!(m && m.disabled);
      const optionsRaw = m && Array.isArray(m.options) ? m.options : [];
      const options = optionsRaw
        .map((o) => ({
          value: o && o.value != null ? String(o.value) : '',
          label: o && o.label != null ? String(o.label) : '',
        }))
        .filter((o) => o.value || o.label);
      if (!key || !label || !options.length) return null;
      return { key, label, ariaLabel, value, disabled, options };
    })
    .filter(Boolean);
});

const extraActionsResolved = computed(() => {
  const list = Array.isArray(props.extraActions) ? props.extraActions : [];
  return list
    .map((a) => {
      const key = a && a.key != null ? String(a.key).trim() : '';
      const label = a && a.label != null ? String(a.label) : '';
      const ariaLabel = a && a.ariaLabel != null ? String(a.ariaLabel) : '';
      const disabled = !!(a && a.disabled);
      if (!key || !label) return null;
      return { key, label, ariaLabel, disabled };
    })
	    .filter(Boolean);
});

const rightOptionalKeys = computed(() => {
  const keys = [];
  for (const m of extraMenusResolved.value) keys.push(`menu:${m.key}`);
  for (const a of extraActionsResolved.value) keys.push(`action:${a.key}`);
  if (Array.isArray(props.goProxyOptions) && props.goProxyOptions.length > 1) keys.push('goproxy');
  keys.push('pip');
  return keys;
});

const rightHiddenKeySet = computed(() => {
  const keys = rightOptionalKeys.value;
  if (!keys.length) return new Set();
  const count = Math.max(0, Math.min(Number(rightHiddenCount.value) || 0, keys.length));
  return new Set(keys.slice(0, count));
});

const extraMenusVisible = computed(() =>
  extraMenusResolved.value.filter((m) => !rightHiddenKeySet.value.has(`menu:${m.key}`))
);
const extraActionsVisible = computed(() =>
  extraActionsResolved.value.filter((a) => !rightHiddenKeySet.value.has(`action:${a.key}`))
);
const showGoProxyControl = computed(
  () => Array.isArray(props.goProxyOptions) && props.goProxyOptions.length > 1 && !rightHiddenKeySet.value.has('goproxy')
);
const showPipControl = computed(() => !rightHiddenKeySet.value.has('pip'));

const clampRightHiddenCount = (next) => {
  const total = rightOptionalKeys.value.length;
  const raw = Number(next);
  const normalized = Number.isFinite(raw) ? Math.floor(raw) : 0;
  return Math.max(0, Math.min(normalized, total));
};

const getDesktopRowGap = () => {
  try {
    const row = desktopRowEl.value;
    if (!row || typeof window === 'undefined' || typeof window.getComputedStyle !== 'function') return 0;
    const style = window.getComputedStyle(row);
    const raw =
      style.columnGap ||
      style.gap ||
      style.getPropertyValue('column-gap') ||
      style.getPropertyValue('gap') ||
      '0';
    const px = parseFloat(raw);
    return Number.isFinite(px) ? Math.max(0, px) : 0;
  } catch (_e) {
    return 0;
  }
};

const getDesktopRowOverflow = () => {
  try {
    const row = desktopRowEl.value;
    const left = leftPillEl.value;
    const right = rightPillEl.value;
    if (!row || !left || !right) return 0;
    const rowWidth = row.clientWidth || 0;
    const leftWidth = left.offsetWidth || 0;
    const rightWidth = right.offsetWidth || 0;
    const used = leftWidth + rightWidth + getDesktopRowGap();
    return used - rowWidth;
  } catch (_e) {
    return 0;
  }
};

const applyRightHiddenCount = async (next) => {
  const normalized = clampRightHiddenCount(next);
  if (rightHiddenCount.value === normalized) return;
  rightHiddenCount.value = normalized;
  await nextTick();
};

const syncDesktopRightControlsFit = async () => {
  if (rightFitSyncing) {
    rightFitReschedule = true;
    return;
  }
  rightFitSyncing = true;
  try {
    const total = rightOptionalKeys.value.length;
    if (!total || isMobile.value) {
      await applyRightHiddenCount(0);
      return;
    }
    if (!desktopRowEl.value || !leftPillEl.value || !rightPillEl.value) {
      await applyRightHiddenCount(0);
      return;
    }

    let hidden = clampRightHiddenCount(rightHiddenCount.value);
    await applyRightHiddenCount(hidden);

    let overflow = getDesktopRowOverflow();
    while (overflow > 0.5 && hidden < total) {
      hidden += 1;
      await applyRightHiddenCount(hidden);
      overflow = getDesktopRowOverflow();
    }

    while (hidden > 0) {
      const probeHidden = hidden - 1;
      await applyRightHiddenCount(probeHidden);
      const probeOverflow = getDesktopRowOverflow();
      if (probeOverflow > 0.5) {
        await applyRightHiddenCount(hidden);
        break;
      }
      hidden = probeHidden;
    }
  } finally {
    rightFitSyncing = false;
    if (rightFitReschedule) {
      rightFitReschedule = false;
      scheduleSyncDesktopRightControlsFit();
    }
  }
};

const scheduleSyncDesktopRightControlsFit = () => {
  if (typeof window === 'undefined') return;
  if (rightFitRaf) {
    try {
      window.cancelAnimationFrame(rightFitRaf);
    } catch (_e) {}
    rightFitRaf = 0;
  }
  if (typeof window.requestAnimationFrame !== 'function') {
    window.setTimeout(() => {
      syncDesktopRightControlsFit();
    }, 0);
    return;
  }
  rightFitRaf = window.requestAnimationFrame(() => {
    rightFitRaf = 0;
    syncDesktopRightControlsFit();
  });
};

const bindDesktopLayoutObserver = () => {
  try {
    if (typeof cleanupDesktopLayoutObserver === 'function') cleanupDesktopLayoutObserver();
  } catch (_e) {}
  cleanupDesktopLayoutObserver = null;

  try {
    if (typeof ResizeObserver !== 'function') return;
    const targets = [shell.value, desktopRowEl.value, leftPillEl.value, rightPillEl.value].filter(Boolean);
    if (!targets.length) return;
    const obs = new ResizeObserver(() => {
      scheduleSyncDesktopRightControlsFit();
    });
    for (const target of targets) {
      try {
        obs.observe(target);
      } catch (_e) {}
    }
    cleanupDesktopLayoutObserver = () => {
      try {
        obs.disconnect();
      } catch (_e) {}
    };
  } catch (_e) {
    cleanupDesktopLayoutObserver = null;
  }
};

watch(
  () => rightHiddenKeySet.value,
  (hiddenSet) => {
    if (extraMenuOpenKey.value && hiddenSet.has(`menu:${extraMenuOpenKey.value}`)) extraMenuOpenKey.value = '';
    if (goProxyMenuOpen.value && hiddenSet.has('goproxy')) goProxyMenuOpen.value = false;
  }
);

watch(
  [desktopRowEl, leftPillEl, rightPillEl, isMobile],
  () => {
    bindDesktopLayoutObserver();
    scheduleSyncDesktopRightControlsFit();
  },
  { flush: 'post' }
);

watch(
  () =>
    [
      rightOptionalKeys.value.join('|'),
      props.goProxyLabel || '',
      extraMenuOpenKey.value,
      goProxyMenuOpen.value ? '1' : '0',
      settingsOpen.value ? '1' : '0',
    ].join('||'),
  () => {
    scheduleSyncDesktopRightControlsFit();
  },
  { immediate: true, flush: 'post' }
);

const toastText = computed(() => (props.toastText || '').trim());
const toastSticky = computed(() => !!props.toastSticky);
const toastVisible = ref(false);
let toastTimer = 0;
watch(
  () => [toastText.value, toastSticky.value ? '1' : '0'].join('||'),
  (v) => {
    if (toastTimer) window.clearTimeout(toastTimer);
    toastTimer = 0;
    const [textRaw, stickyFlag] = String(v || '').split('||');
    const text = (textRaw || '').trim();
    const sticky = stickyFlag === '1';
    if (!text) {
      toastVisible.value = false;
      return;
    }
    toastVisible.value = true;
    if (sticky) return;
    toastTimer = window.setTimeout(() => {
      toastTimer = 0;
      toastVisible.value = false;
    }, 2200);
  },
  { immediate: true }
);

const normalizeInfoValue = (v) => {
  const s = typeof v === 'string' ? v.trim() : v == null ? '' : String(v).trim();
  return s;
};

const escapeHtml = (s) =>
  String(s || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\"/g, '&quot;')
    .replace(/'/g, '&#39;');

	const renderInfoExtra = () => {
	  const extra = props.statsExtra && typeof props.statsExtra === 'object' ? props.statsExtra : {};
	  const displayName = normalizeInfoValue(extra.displayName);
	  const siteName = normalizeInfoValue(extra.siteName);
	  const panName = normalizeInfoValue(extra.panName);
	  const pathName = normalizeInfoValue(extra.pathName);
	  const rawFileName = normalizeInfoValue(extra.rawFileName);

	  const lines = [];
	  if (displayName) lines.push({ k: '名称', v: displayName });
	  if (siteName) lines.push({ k: '站源', v: siteName });
	  if (panName) lines.push({ k: '网盘', v: panName });
	  if (pathName) lines.push({ k: '路径', v: pathName });
	  if (rawFileName) lines.push({ k: '文件', v: rawFileName });
	  if (!lines.length) return '';

  return lines
    .map((x) => `<div class="tv-art-info-extra__line">${x.k}：${escapeHtml(String(x.v || ''))}</div>`)
    .join('');
};

const syncInfoExtraIntoArtInfoPanel = () => {
  try {
    if (infoExtraSyncing) return;
    const playerEl = teleportTarget.value;
    if (!playerEl) return;
    const infoPanel = playerEl.querySelector && playerEl.querySelector('.art-info');
    if (!infoPanel) return;

    let box = infoPanel.querySelector && infoPanel.querySelector('.tv-art-info-extra');
    if (!box) {
      box = document.createElement('div');
      box.className = 'tv-art-info-extra';
      infoPanel.appendChild(box);
    }

    const html = renderInfoExtra();
    const prev = box.dataset && typeof box.dataset.lastHtml === 'string' ? box.dataset.lastHtml : null;
    if (prev === html) {
      box.classList.toggle('hidden', !html);
      return;
    }
    infoExtraSyncing = true;
    try {
      if (box.dataset) box.dataset.lastHtml = html;
      box.innerHTML = html;
      box.classList.toggle('hidden', !html);
    } finally {
      infoExtraSyncing = false;
    }
  } catch (_e) {}
};

let mediaQuery = null;
const updateIsMobile = () => {
  try {
    if (typeof window === 'undefined') return;
    const ua = typeof navigator !== 'undefined' && navigator.userAgent ? String(navigator.userAgent) : '';
    const touch =
      (typeof navigator !== 'undefined' && typeof navigator.maxTouchPoints === 'number' && navigator.maxTouchPoints > 0) ||
      ('ontouchstart' in window);
    const ios = /iPad|iPhone|iPod/i.test(ua) || (!!touch && /Macintosh/i.test(ua) && (navigator.maxTouchPoints || 0) > 1);
    const android = /Android/i.test(ua);
    const uaMobile = /\bMobile\b/i.test(ua);
    const coarse =
      typeof window.matchMedia === 'function' ? window.matchMedia('(hover: none) and (pointer: coarse)').matches : false;
    // Avoid mis-detecting desktop browsers (including touchscreen laptops). Prefer UA-based mobile,
    // then fall back to the "coarse pointer + touch" heuristic.
    isIos.value = !!ios;
    isMobile.value = !!(ios || android || uaMobile || (coarse && touch));
  } catch (_e) {
    isMobile.value = false;
    isIos.value = false;
  }
};

const rates = [0.5, 0.75, 1, 1.25, 1.5, 2];
const ratios = ['default', '16:9', '4:3'];

const formatTime = (sec) => {
  const s = Number.isFinite(sec) ? Math.max(0, Math.floor(sec)) : 0;
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(ss).padStart(2, '0')}`;
  return `${m}:${String(ss).padStart(2, '0')}`;
};

const timeLabel = computed(() => `${formatTime(currentTime.value)} / ${formatTime(duration.value)}`);
const displayTime = computed(() => (scrubbing.value ? scrubTime.value : currentTime.value));
const displayTimeLabel = computed(() => `${formatTime(displayTime.value)} / ${formatTime(duration.value)}`);
const progressFrac = computed(() => {
  const d = Number(duration.value);
  const t = Number(displayTime.value);
  if (!Number.isFinite(d) || d <= 0) return 0;
  if (!Number.isFinite(t) || t <= 0) return 0;
  return Math.max(0, Math.min(1, t / d));
});

const bufferedFrac = computed(() => {
  const d = Number(duration.value);
  const e = Number(bufferedEnd.value);
  if (!Number.isFinite(d) || d <= 0) return 0;
  if (!Number.isFinite(e) || e <= 0) return 0;
  return Math.max(0, Math.min(1, e / d));
});

const setBuffering = (next) => {
  const v = !!next;
  if (buffering.value === v) return;
  buffering.value = v;
  try {
    emit('buffering', v);
  } catch (_e) {}
};

watch(
  () => props.url,
  (next, prev) => {
    const a = typeof next === 'string' ? next.trim() : '';
    const b = typeof prev === 'string' ? prev.trim() : '';
    if (!a) {
      setBuffering(false);
      return;
    }
    if (a !== b) setBuffering(true);
  }
);

let bufferUpdateRaf = 0;
let bufferUpdatePending = 0;
const computeBufferedEnd = () => {
  try {
    const v = art && art.video ? art.video : null;
    if (!v) return 0;
    const t = Number.isFinite(v.currentTime) ? v.currentTime : Number(currentTime.value) || 0;
    const b = v.buffered;
    if (!b || typeof b.length !== 'number' || b.length <= 0) return 0;
    const eps = 0.15;
    let inRangeEnd = 0;
    for (let i = 0; i < b.length; i += 1) {
      let start = 0;
      let end = 0;
      try {
        start = b.start(i);
        end = b.end(i);
      } catch (_e) {
        continue;
      }
      if (Number.isFinite(start) && Number.isFinite(end) && t + eps >= start && t - eps <= end) {
        inRangeEnd = Math.max(inRangeEnd, end);
      }
    }
    // Only report the buffered range that actually covers the current playback position.
    // (For disjoint ranges, using the global maxEnd is misleading and makes the UI look "buffered ahead"
    // even when the current time is inside a gap.)
    return inRangeEnd || 0;
  } catch (_e) {
    return 0;
  }
};
const scheduleBufferedSync = () => {
  if (!art) return;
  bufferUpdatePending = 1;
  if (bufferUpdateRaf) return;
  bufferUpdateRaf = window.requestAnimationFrame(() => {
    bufferUpdateRaf = 0;
    if (!bufferUpdatePending) return;
    bufferUpdatePending = 0;
    bufferedEnd.value = computeBufferedEnd();
  });
};

const isUiControlTarget = (target) => {
  if (!target || typeof target.closest !== 'function') return false;
  return !!target.closest(
    [
      '.yt-top',
      '.yt-top__back',
      '.yt-bar',
      '.yt-setting',
      '.yt-setting__menu',
      '.yt-setting__item',
      '.yt-progress',
      '.yt-progress__range',
      '.yt-btn',
      '.m-center__controls',
      '.m-btn',
      '.art-contextmenus',
      '.art-contextmenu',
      '.art-info',
      '.art-info-panel',
      '.art-info-close',
    ].join(',')
  );
};

	const detectVideoFormat = (url) => {
	  const raw = typeof url === 'string' ? url.trim() : '';
	  if (!raw) return 'native';
	  const lower = raw.toLowerCase();
	  const parsed = (() => {
	    try {
	      return new URL(raw, window.location.href);
	    } catch (_e) {
	      return null;
	    }
	  })();
	  const pathOnly = parsed ? (parsed.pathname || '').toLowerCase() : lower.split('#')[0].split('?')[0];

	  // Explicit hint for proxy/token URLs (e.g. GoProxy) that don't carry a suffix.
	  const hinted = (() => {
	    try {
	      const v = parsed && parsed.searchParams ? String(parsed.searchParams.get('__tv_fmt') || '').trim().toLowerCase() : '';
	      if (!v) return '';
	      if (v === 'm3u8' || v === 'hls') return 'hls';
	      if (v === 'mpd' || v === 'dash') return 'dash';
	      if (v === 'flv') return 'flv';
	      if (v === 'native' || v === 'mp4') return 'native';
	      return '';
	    } catch (_e) {
	      return '';
	    }
	  })();
	  if (hinted) return hinted;

	  // Match by suffix (after stripping query/hash), not by loose substring.
	  if (pathOnly.endsWith('.m3u8')) return 'hls';
	  if (pathOnly.endsWith('.flv')) return 'flv';
	  if (pathOnly.endsWith('.mpd')) return 'dash';
	  return 'native';
	};

	const destroyCustomPlayer = (player) => {
	  if (!player) return;
	  try {
	    if (player.hls && typeof player.hls.destroy === 'function') player.hls.destroy();
	  } catch (_) {}
	  try {
	    if (player.flv && typeof player.flv.destroy === 'function') player.flv.destroy();
	  } catch (_) {}
	  try {
	    if (player.dash && typeof player.dash.destroy === 'function') player.dash.destroy();
	  } catch (_) {}
	};

		let hlsModulePromise = null;
		let flvModulePromise = null;
		let shakaModulePromise = null;

			const loadHls = async () => {
		  if (!hlsModulePromise) {
		    hlsModulePromise = import('hls.js').then((m) => {
		      // Resolve a Hls constructor across common export shapes.
		      const resolve = (mod) => {
		        if (!mod) return null;
		        if (typeof mod === 'function') return mod;
		        if (typeof mod.Hls === 'function') return mod.Hls;
		        if (typeof mod.default === 'function') return mod.default;
		        if (mod.default && typeof mod.default.Hls === 'function') return mod.default.Hls;
		        if (mod.default && typeof mod.default.default === 'function') return mod.default.default;
		        return null;
		      };
		      return resolve(m) || resolve(m && m.default) || null;
		    });
		  }
		  return await hlsModulePromise;
		};

	const loadFlv = async () => {
	  if (!flvModulePromise) {
	    flvModulePromise = import('flv.js').then((m) => (m && (m.default || m)));
	  }
	  return await flvModulePromise;
	};

	const loadShaka = async () => {
	  if (!shakaModulePromise) {
	    shakaModulePromise = import('shaka-player/dist/shaka-player.compiled').then((m) => (m && (m.default || m)));
	  }
	  return await shakaModulePromise;
	};

const destroyNow = () => {
  try {
    settingsOpen.value = false;
  } catch (_e) {}
  try {
    setBuffering(false);
  } catch (_e) {}
  try {
    if (timeUpdateRaf) window.cancelAnimationFrame(timeUpdateRaf);
  } catch (_e) {}
  timeUpdateRaf = 0;
  timeUpdatePending = 0;
  try {
    if (bufferUpdateRaf) window.cancelAnimationFrame(bufferUpdateRaf);
  } catch (_e) {}
  bufferUpdateRaf = 0;
  bufferUpdatePending = 0;
  try {
    if (hideTimer) window.clearTimeout(hideTimer);
    hideTimer = 0;
    if (desktopHideTimer) window.clearTimeout(desktopHideTimer);
    desktopHideTimer = 0;
  } catch (_e) {}

  try {
    // Exit PiP if the current video is in PiP.
    const videoEl = art && art.video ? art.video : null;
    if (videoEl && typeof document !== 'undefined' && document.pictureInPictureElement === videoEl) {
      document.exitPictureInPicture().catch(() => {});
    }
  } catch (_e) {}

  try {
    // Exit fullscreen if we own it.
    const el = shell.value;
    const fsEl = typeof document !== 'undefined' ? document.fullscreenElement : null;
    if (el && fsEl && (fsEl === el || el.contains(fsEl) || fsEl.contains(el))) {
      document.exitFullscreen().catch(() => {});
    }
  } catch (_e) {}

	  try {
	    if (typeof cleanupPipListeners === 'function') cleanupPipListeners();
	  } catch (_e) {}
	  cleanupPipListeners = null;
	  try {
	    if (typeof cleanupFsListeners === 'function') cleanupFsListeners();
	  } catch (_e) {}
	  cleanupFsListeners = null;
	  try {
	    if (typeof cleanupNoCorsEnforcer === 'function') cleanupNoCorsEnforcer();
	  } catch (_e) {}
	  cleanupNoCorsEnforcer = null;
	  try {
	    if (typeof cleanupNativeVideoListeners === 'function') cleanupNativeVideoListeners();
	  } catch (_e) {}
	  cleanupNativeVideoListeners = null;
	  try {
	    if (typeof cleanupPlayerElListeners === 'function') cleanupPlayerElListeners();
	  } catch (_e) {}
	  cleanupPlayerElListeners = null;

  try {
    const v = art && art.video ? art.video : null;
    if (v) {
      try {
        v.pause();
      } catch (_e) {}
      try {
        v.removeAttribute('src');
      } catch (_e) {}
      try {
        while (v.firstChild) v.removeChild(v.firstChild);
      } catch (_e) {}
      try {
        v.load();
      } catch (_e) {}
    }
  } catch (_e) {}

  try {
    destroyCustomPlayer(art && art.customPlayer ? art.customPlayer : null);
  } catch (_e) {}

  try {
    if (art && typeof art.destroy === 'function') {
      // Use "remove" mode to detach DOM and stop playback aggressively.
      art.destroy(true);
    }
  } catch (_e) {
    try {
      if (art && typeof art.destroy === 'function') art.destroy(false);
    } catch (__e) {}
  }
  art = null;
  teleportTarget.value = null;
  isPip.value = false;
  playing.value = false;
  bufferedEnd.value = 0;
  scrubbing.value = false;
  pointerScrubbing.value = false;
  scrubTime.value = 0;
};

		const createCustomPlayer = {
				  async hls(videoEl, url, headers) {
				    const Hls = await loadHls();
				    const canUseHlsJs =
				      !!Hls &&
				      typeof Hls === 'function' &&
				      typeof Hls.isSupported === 'function' &&
				      !!Hls.isSupported();

			    if (!canUseHlsJs) {
			      // Fallback: if the browser can play HLS natively (Safari) or as a last resort (to surface errors),
			      // set src so we at least issue a request.
			      try {
			        if (videoEl) {
			          videoEl.src = url;
			          try { videoEl.load(); } catch (_e) {}
			        }
			      } catch (_e) {}
			      return { direct: true };
				    }
				    const withCredentials = false;
				    // Desktop browsers can afford a larger forward buffer to smooth out network jitter,
				    // especially for high bitrate streams (4K) where `maxBufferSize` becomes the real limiter.
				    const bufferGoalSeconds = isMobile.value ? 60 : 120;
				    const backBufferSeconds = isMobile.value ? 75 : 180;
				    const maxBufferSizeBytes = isMobile.value ? 200 * 1000 * 1000 : 800 * 1000 * 1000;
				    const hls = new Hls({
		      enableWorker: true,
		      maxBufferLength: bufferGoalSeconds,
		      maxMaxBufferLength: bufferGoalSeconds * 2,
		      backBufferLength: backBufferSeconds,
		      maxBufferSize: maxBufferSizeBytes,
		      // Reduce reconnect/retry attempts to avoid long "reconnecting" loops on unstable networks.
		      manifestLoadingMaxRetry: 2,
		      levelLoadingMaxRetry: 2,
		      fragLoadingMaxRetry: 2,
	      keyLoadingMaxRetry: 2,
      xhrSetup(xhr) {
        xhr.withCredentials = withCredentials;
        if (!headers || typeof headers !== 'object') return;
        Object.keys(headers).forEach((k) => {
          const v = headers[k];
          if (v == null) return;
          try {
            xhr.setRequestHeader(k, String(v));
          } catch (_) {}
        });
      },
    });
    if (Hls.Events && Hls.Events.ERROR && typeof hls.on === 'function') {
      hls.on(Hls.Events.ERROR, (_event, data) => {
        if (!data || !data.fatal) return;
        const detail = data.details ? String(data.details) : '';
        const type = data.type ? String(data.type) : '';
        const message = detail || type || 'HLS 播放失败';
        try {
          emit('error', { code: 2, message });
        } catch (_e) {}
      });
    }
	    hls.loadSource(url);
	    hls.attachMedia(videoEl);
	    return { hls };
	  },
		  async flv(videoEl, url, headers) {
		    const flvjs = await loadFlv();
		    if (!flvjs || !flvjs.isSupported || !flvjs.isSupported()) return null;
		    const withCredentials = false;
		    const bufferGoalSeconds = 60;
		    const backBufferSeconds = 75;
		    const flv = flvjs.createPlayer(
	      { type: 'flv', isLive: false, url, withCredentials },
	      {
	        // Worker demuxing is important to keep playback stable when the main thread is busy
	        // (e.g. DevTools open, background/occluded window, heavy UI).
	        enableWorker: true,
	        enableStashBuffer: true,
	        stashInitialSize: 1024 * 1024,
	        autoCleanupSourceBuffer: true,
	        lazyLoad: true,
	        lazyLoadMaxDuration: bufferGoalSeconds,
	        lazyLoadRecoverDuration: Math.max(10, Math.floor(bufferGoalSeconds / 2)),
	        autoCleanupMaxBackwardDuration: backBufferSeconds,
	        reuseRedirectedURL: true,
	        headers: headers || {},
	      }
	    );
	    flv.attachMediaElement(videoEl);
	    flv.load();
	    return { flv };
	  },
		  async dash(videoEl, url, headers) {
		    const shaka = await loadShaka();
		    if (!shaka || !shaka.Player || !shaka.Player.isBrowserSupported || !shaka.Player.isBrowserSupported()) return null;
		    const withCredentials = false;
		    const dash = new shaka.Player(videoEl);
		    try {
		      dash.configure({
		        streaming: {
		          bufferingGoal: 60,
		          rebufferingGoal: 2,
		          bufferBehind: 75,
		        },
		      });
		    } catch (_e) {}
	    dash.getNetworkingEngine().registerRequestFilter((type, request) => {
	      request.allowCrossSiteCredentials = withCredentials;
	      if (!headers || typeof headers !== 'object') return;
	      Object.keys(headers).forEach((k) => {
        const v = headers[k];
        if (v == null) return;
        request.headers[k] = String(v);
      });
    });
	    dash.load(url);
	    return { dash };
	  },
	};

	const init = () => {
	  const url = (props.url || '').trim();
	  if (!container.value || !url) return;

  if (art) destroyNow();
  isPip.value = false;

	  const format = detectVideoFormat(url);
	  const playUrlForArt = url;
	  const isCrossOriginUrl = (u) => {
	    const raw = typeof u === 'string' ? u.trim() : '';
	    if (!raw) return false;
	    try {
	      const parsed = new URL(raw, window.location.href);
	      return parsed.origin !== window.location.origin;
	    } catch (_e) {
	      return false;
	    }
	  };
	  const isCrossOriginNative = format === 'native' && isCrossOriginUrl(playUrlForArt);
	  const artType =
	    format === 'hls'
	      ? 'm3u8'
      : format === 'flv'
        ? 'flv'
        : format === 'dash'
          ? 'mpd'
          : '';

  let metaEmitted = false;
  let firstFrameEmitted = false;
  const readVideoResolution = () => {
    try {
      const v = art && art.video ? art.video : null;
      const w = v && Number.isFinite(Number(v.videoWidth)) ? Math.floor(Number(v.videoWidth)) : 0;
      const h = v && Number.isFinite(Number(v.videoHeight)) ? Math.floor(Number(v.videoHeight)) : 0;
      return { width: w > 0 ? w : 0, height: h > 0 ? h : 0 };
    } catch (_e) {
      return { width: 0, height: 0 };
    }
  };
  const emitVideoInfo = () => {
    try {
      const { width, height } = readVideoResolution();
      emit('videoinfo', { width, height });
    } catch (_e) {}
  };
  const emitMetaOnce = () => {
    if (metaEmitted) return;
    metaEmitted = true;
    try {
      const v = art && art.video ? art.video : null;
      const d = (art && typeof art.duration === 'number' ? art.duration : null) ?? (v ? v.duration : 0);
      duration.value = Number.isFinite(d) ? d : 0;
      const { width, height } = readVideoResolution();
      emit('loadedmetadata', { duration: duration.value, width, height });
    } catch (_e) {}
  };

  const emitFirstFrameOnce = () => {
    if (firstFrameEmitted) return;
    firstFrameEmitted = true;
    try {
      emit('firstframe');
    } catch (_e) {}
  };

		  art = new Artplayer({
		    container: container.value,
		    url: playUrlForArt,
    poster: props.poster || '',
    autoplay: !!props.autoplay,
    muted: false,
    volume: 0.7,
    pip: true,
    // Disable ArtPlayer's backdrop (blur background) to avoid poster/backdrop flashes during init/buffering.
    backdrop: false,
    autoSize: false,
    autoMini: false,
    setting: false,
	    playbackRate: false,
	    aspectRatio: false,
	    screenshot: !isCrossOriginNative,
	    playsInline: true,
    // Disable built-in touch gestures on mobile so taps don't also toggle play/pause
    // (we handle mobile UI + play controls ourselves).
    gesture: !isMobile.value,
    theme: '#22c55e',
    lang: 'zh-cn',
    type: artType,
    // Disable ArtPlayer fullscreen/hotkeys; we control fullscreen ourselves to keep custom UI visible.
    fullscreen: false,
    fullscreenWeb: false,
    hotkey: false,
			  customType:
			    format !== 'native'
			      ? {
			          [artType]: function (videoEl, playUrl, artInstance) {
			            const headers = props.headers || {};
			            const token = `${Date.now()}-${Math.random()}`;
			            artInstance.__tvCustomToken = token;
			            // Important: return a Promise so ArtPlayer treats this as an async custom loader.
			            // This guarantees the token URL will be requested by the custom engine (hls.js/flv.js/shaka),
			            // and avoids an extra preflight load that can get cancelled.
			            return (async () => {
			              let custom = null;
			              try {
			                if (format === 'hls') custom = await createCustomPlayer.hls(videoEl, playUrl, headers);
			                if (format === 'flv') custom = await createCustomPlayer.flv(videoEl, playUrl, headers);
			                if (format === 'dash') custom = await createCustomPlayer.dash(videoEl, playUrl, headers);
			              } catch (_e) {
			                custom = null;
			              }

			              // If URL changed before module loaded, drop the created player.
			              if (!artInstance || artInstance.__tvCustomToken !== token) {
			                destroyCustomPlayer(custom);
			                return null;
			              }

			              if (custom) {
			                artInstance.customPlayer = custom;
			              } else {
			                // As a last-resort fallback, set src so we still issue a request and show errors.
			                try {
			                  if (videoEl && playUrl) {
			                    videoEl.src = playUrl;
			                    try { videoEl.load(); } catch (_e) {}
			                  }
			                } catch (_e) {}
			              }

			              try {
			                if (props.autoplay && videoEl && typeof videoEl.play === 'function') videoEl.play().catch(() => {});
			              } catch (_e) {}

			              return custom;
			            })();
			          },
			        }
			      : {},
  });
  try {
    if (props.autoplay && art && typeof art.play === 'function') {
      Promise.resolve()
        .then(() => art && art.play && art.play())
        .catch(() => {});
    }
  } catch (_e) {}

  try {
    teleportTarget.value = art && art.template && art.template.$player ? art.template.$player : null;
  } catch (_e) {
    teleportTarget.value = null;
  }
  try {
    if (typeof cleanupInfoExtraObserver === 'function') cleanupInfoExtraObserver();
  } catch (_e) {}
  cleanupInfoExtraObserver = null;

  try {
    const playerEl = teleportTarget.value;
    if (playerEl && typeof MutationObserver !== 'undefined') {
      const obs = new MutationObserver(() => {
        try {
          const infoPanel = playerEl.querySelector && playerEl.querySelector('.art-info');
          if (!infoPanel) return;
          syncInfoExtraIntoArtInfoPanel();
          try {
            obs.disconnect();
          } catch (_e) {}
          cleanupInfoExtraObserver = null;
        } catch (_e) {}
      });
      obs.observe(playerEl, { childList: true, subtree: true });
      cleanupInfoExtraObserver = () => {
        try {
          obs.disconnect();
        } catch (_e) {}
      };
    }
  } catch (_e) {
    cleanupInfoExtraObserver = null;
  }
  syncInfoExtraIntoArtInfoPanel();
  try {
    if (typeof cleanupPlayerElListeners === 'function') cleanupPlayerElListeners();
  } catch (_e) {}
  cleanupPlayerElListeners = null;
  desktopControlsVisible.value = true;
  try {
    const playerEl = teleportTarget.value;
    if (playerEl) {
      const onDesktopMouseMove = () => {
        if (isMobile.value) return;
        desktopControlsVisible.value = true;
        scheduleDesktopAutoHide();
      };
      const onDesktopMouseLeave = () => {
        if (isMobile.value) return;
        if (!art) return;
        if (art.playing && !settingsOpen.value && !buffering.value) desktopControlsVisible.value = false;
      };
      // Desktop double-click fullscreen: prevent the browser's default <video> double-click fullscreen
      // from fighting with Artplayer (which can cause enter+exit immediately).
      if (typeof playerEl.addEventListener === 'function') {
        const clearDesktopClickTimer = () => {
          if (!desktopClickTimer) return;
          try {
            window.clearTimeout(desktopClickTimer);
          } catch (_e) {}
          desktopClickTimer = 0;
        };

        const onDblClickCapture = (evt) => {
          if (isMobile.value) return;
          if (isUiControlTarget(evt && evt.target ? evt.target : null)) return;
          clearDesktopClickTimer();
          try {
            evt.preventDefault();
          } catch (_e) {}
          try {
            if (typeof evt.stopImmediatePropagation === 'function') evt.stopImmediatePropagation();
          } catch (_e) {}
          try {
            evt.stopPropagation();
          } catch (_e) {}
          toggleFullscreen();
        };

        const onClickCapture = (evt) => {
          if (isMobile.value) return;
          if (isUiControlTarget(evt && evt.target ? evt.target : null)) return;
          clearDesktopClickTimer();
          // Delay to avoid firing on double-click (dblclick will cancel this).
          desktopClickTimer = window.setTimeout(() => {
            desktopClickTimer = 0;
            togglePlay();
            showUiTemporarily();
          }, 220);
          try {
            evt.preventDefault();
          } catch (_e) {}
          try {
            if (typeof evt.stopImmediatePropagation === 'function') evt.stopImmediatePropagation();
          } catch (_e) {}
          try {
            evt.stopPropagation();
          } catch (_e) {}
        };
        playerEl.addEventListener('dblclick', onDblClickCapture, { capture: true });
        playerEl.addEventListener('click', onClickCapture, { capture: true });
        playerEl.addEventListener('mousemove', onDesktopMouseMove, { passive: true });
        playerEl.addEventListener('mouseenter', onDesktopMouseMove, { passive: true });
        playerEl.addEventListener('mouseleave', onDesktopMouseLeave, { passive: true });
        cleanupPlayerElListeners = () => {
          try {
            clearDesktopClickTimer();
            playerEl.removeEventListener('dblclick', onDblClickCapture, true);
            playerEl.removeEventListener('click', onClickCapture, true);
            playerEl.removeEventListener('mousemove', onDesktopMouseMove);
            playerEl.removeEventListener('mouseenter', onDesktopMouseMove);
            playerEl.removeEventListener('mouseleave', onDesktopMouseLeave);
          } catch (_e) {}
        };
      }
    }
  } catch (_e) {
    desktopControlsVisible.value = true;
  }

		  // iOS Safari quirks: ensure inline playback attributes are present on the real video element.
		  try {
		    const v = art.video;
		    if (v) {
		      if (isCrossOriginNative) {
		        const enforceNoCors = () => {
		          try {
		            v.removeAttribute('crossorigin');
		          } catch (_e) {}
		          try {
		            v.setAttribute('referrerpolicy', 'no-referrer');
		          } catch (_e) {}
		        };
		        enforceNoCors();
		        try {
		          if (typeof MutationObserver === 'function') {
		            const obs = new MutationObserver(() => enforceNoCors());
		            obs.observe(v, { attributes: true, attributeFilter: ['crossorigin', 'referrerpolicy'] });
		            cleanupNoCorsEnforcer = () => {
		              try {
		                obs.disconnect();
		              } catch (_e) {}
		            };
		          }
		        } catch (_e) {}
		      }
		      v.setAttribute('playsinline', '');
		      v.setAttribute('webkit-playsinline', '');
		      v.setAttribute('x-webkit-airplay', 'allow');
		      v.setAttribute('preload', 'auto');
	      try {
	        v.preload = 'auto';
	      } catch (_e) {}
      try {
        v.load();
      } catch (_e) {}

		      const onLoadedMetadata = () => emitMetaOnce();
		      const onResize = () => emitVideoInfo();
		      const onEnded = () => {
		        try {
		          playing.value = false;
		        } catch (_e) {}
		        try {
		          setBuffering(false);
		        } catch (_e) {}
		        try {
		          uiVisible.value = true;
		        } catch (_e) {}
		        try {
		          emit('ended');
		        } catch (_e) {}
		      };
	      const onDurationChange = () => {
	        try {
	          const d = (art && typeof art.duration === 'number' ? art.duration : null) ?? v.duration;
	          duration.value = Number.isFinite(d) ? d : 0;
        } catch (_e) {}
        if (typeof v.readyState === 'number' && v.readyState >= 1) emitMetaOnce();
      };
	      const onError = () => {
	        try {
	          const err = v && v.error ? v.error : null;
	          const code = err && typeof err.code === 'number' ? err.code : 0;
	          let msg = '播放失败';
          if (code === 2) msg = '播放失败：网络错误';
          else if (code === 3) msg = '播放失败：解码错误（可能不支持该编码/清晰度）';
          else if (code === 4) msg = '播放失败：媒体不可播放（可能不支持该编码/清晰度）';
          emit('error', { code, message: msg });
        } catch (_e) {
          try {
            emit('error', { code: 0, message: '播放失败' });
          } catch (_ignored) {}
	        }
	      };
	      const onProgress = () => scheduleBufferedSync();
	      v.addEventListener('loadedmetadata', onLoadedMetadata);
	      v.addEventListener('resize', onResize);
	      v.addEventListener('durationchange', onDurationChange);
		      v.addEventListener('progress', onProgress);
		      v.addEventListener('seeking', onProgress);
		      v.addEventListener('seeked', onProgress);
		      v.addEventListener('error', onError);
		      v.addEventListener('ended', onEnded);
		      cleanupNativeVideoListeners = () => {
		        try {
		          v.removeEventListener('loadedmetadata', onLoadedMetadata);
		          v.removeEventListener('resize', onResize);
		          v.removeEventListener('durationchange', onDurationChange);
		          v.removeEventListener('progress', onProgress);
		          v.removeEventListener('seeking', onProgress);
		          v.removeEventListener('seeked', onProgress);
		          v.removeEventListener('error', onError);
		          v.removeEventListener('ended', onEnded);
		        } catch (_e) {}
		      };
	    }
	  } catch (_e) {}

  // Ensure UI auto-hide behaves like a player overlay.
  uiVisible.value = true;
  showUiTemporarily();

  // Sync state
  playing.value = art.playing;
  currentTime.value = art.currentTime || 0;
  duration.value = art.duration || 0;
  volume.value = art.volume || 0;
  muted.value = art.muted || false;
  playbackRate.value = art.playbackRate || 1;
  aspectRatio.value = art.aspectRatio || 'default';
  scheduleBufferedSync();

	  art.on('video:timeupdate', () => {
	    timeUpdatePending = art.currentTime || 0;
	    if (timeUpdateRaf) return;
		    timeUpdateRaf = window.requestAnimationFrame(() => {
		      timeUpdateRaf = 0;
		      const nextTime = timeUpdatePending || 0;
		      if (buffering.value && nextTime > (currentTime.value || 0) + 0.05) setBuffering(false);
		      currentTime.value = nextTime;
        if (nextTime > 0.01) emitFirstFrameOnce();
	      scheduleBufferedSync();
        try {
          const now = Date.now();
          if (now - timeUpdateEmitAt >= 1000) {
            timeUpdateEmitAt = now;
            emit('timeupdate', { currentTime: nextTime || 0, duration: duration.value || 0, playing: !!playing.value });
          }
        } catch (_e) {}
		    });
		  });
	  art.on('video:loadedmetadata', () => {
	    try {
	      const d = art.duration || 0;
	      duration.value = Number.isFinite(d) ? d : 0;
	    } catch (_e) {}
	    emitMetaOnce();
	    emitVideoInfo();
	    scheduleBufferedSync();
	  });
	  art.on('video:durationchange', () => {
	    duration.value = art.duration || 0;
	    scheduleBufferedSync();
	  });
  art.on('video:volumechange', () => {
    volume.value = art.volume || 0;
    muted.value = art.muted || false;
  });
  art.on('video:ratechange', () => {
    playbackRate.value = art.playbackRate || 1;
  });
	  art.on('video:play', () => {
	    playing.value = true;
	    setBuffering(false);
	    showUiTemporarily();
	    scheduleBufferedSync();
	  });
	  art.on('video:pause', () => {
	    playing.value = false;
	    setBuffering(false);
	    uiVisible.value = true;
	  });
	  art.on('video:ended', () => {
	    playing.value = false;
	    setBuffering(false);
	    uiVisible.value = true;
	    try {
	      emit('ended');
	    } catch (_e) {}
	  });
	  art.on('video:waiting', () => {
	    setBuffering(true);
	    showUiTemporarily();
	  });
  art.on('video:stalled', () => {
    setBuffering(true);
    showUiTemporarily();
  });
		  art.on('video:playing', () => {
		    setBuffering(false);
		    showUiTemporarily();
		    emitMetaOnce();
	      try {
	        emit('playing');
	      } catch (_e) {}
        try {
          const v = art && art.video ? art.video : null;
          if (v && typeof v.requestVideoFrameCallback === 'function') {
            v.requestVideoFrameCallback(() => emitFirstFrameOnce());
          }
        } catch (_e) {}
		    scheduleBufferedSync();
		  });
	  art.on('video:canplay', () => {
	    setBuffering(false);
	    emitMetaOnce();
	    emitVideoInfo();
	    emitFirstFrameOnce();
	    scheduleBufferedSync();
	  });
  art.on('video:error', () => {
    setBuffering(false);
    uiVisible.value = true;
    try {
      const v = art && art.video ? art.video : null;
      const err = v && v.error ? v.error : null;
      const code = err && typeof err.code === 'number' ? err.code : 0;
      // 1: aborted, 2: network, 3: decode, 4: src not supported
      let msg = '播放失败';
      if (code === 2) msg = '播放失败：网络错误';
      else if (code === 3) msg = '播放失败：解码错误（可能不支持该编码/清晰度）';
      else if (code === 4) msg = '播放失败：媒体不可播放（可能不支持该编码/清晰度）';
      emit('error', { code, message: msg });
    } catch (_) {
      try {
        emit('error', { code: 0, message: '播放失败' });
      } catch (_e) {}
    }
  });

  // iOS native fullscreen (video element) does not trigger document fullscreenchange.
  try {
    const videoEl = art.video;
    if (videoEl && typeof videoEl.addEventListener === 'function') {
      const onBegin = () => {
        isFullscreen.value = true;
        showUiTemporarily();
      };
      const onEnd = () => {
        isFullscreen.value = false;
        showUiTemporarily();
      };
      videoEl.addEventListener('webkitbeginfullscreen', onBegin);
      videoEl.addEventListener('webkitendfullscreen', onEnd);
      cleanupFsListeners = () => {
        try {
          videoEl.removeEventListener('webkitbeginfullscreen', onBegin);
          videoEl.removeEventListener('webkitendfullscreen', onEnd);
        } catch (_e) {}
      };
    }
  } catch (_e) {}

  // Browser Picture-in-Picture state (desktop floating window).
  try {
    const videoEl = art.video;
    if (videoEl && typeof videoEl.addEventListener === 'function') {
      const sync = () => {
        try {
          isPip.value = document.pictureInPictureElement === videoEl;
        } catch (_) {
          isPip.value = false;
        }
      };
      const onEnter = () => sync();
      const onLeave = () => sync();
      videoEl.addEventListener('enterpictureinpicture', onEnter);
      videoEl.addEventListener('leavepictureinpicture', onLeave);
      sync();
      cleanupPipListeners = () => {
        try {
          videoEl.removeEventListener('enterpictureinpicture', onEnter);
          videoEl.removeEventListener('leavepictureinpicture', onLeave);
        } catch (_) {}
      };
    }
  } catch (_) {}
};

const togglePlay = () => {
  if (!art) return;
  // During buffering/waiting, `art.playing` can be out of sync with the underlying media element.
  // Toggle based on the real <video> paused state so pause works even when stalled.
  try {
    const v = art && art.video ? art.video : null;
    if (v && typeof v.pause === 'function' && typeof v.play === 'function') {
      if (v.paused) {
        v.play().catch(() => {});
      } else {
        v.pause();
      }
      return;
    }
  } catch (_e) {}
  try {
    art.toggle();
  } catch (_e) {}
};

const seekBySeconds = (deltaSeconds) => {
  if (!art) return;
  const d = Number.isFinite(duration.value) && duration.value > 0 ? duration.value : (art.duration || 0);
  const cur = Number.isFinite(art.currentTime) ? art.currentTime : currentTime.value || 0;
 const next = Math.max(0, Math.min(d || 0, cur + Number(deltaSeconds || 0)));
 art.currentTime = next;
 scheduleBufferedSync();
};

const emitEpisodeDelta = (delta) => {
  const normalized = Number(delta) > 0 ? 1 : (Number(delta) < 0 ? -1 : 0);
  if (!normalized) return;
  emit('episodedelta', normalized);
};

const toggleMute = () => {
  if (!art) return;
  art.muted = !art.muted;
};

const onVolume = (e) => {
  const v = e && e.target ? Number(e.target.value) : NaN;
  if (!art || !Number.isFinite(v)) return;
  art.muted = false;
  art.volume = Math.max(0, Math.min(1, v));
};

const onSeekPointerDown = () => {
  pointerScrubbing.value = true;
};
const onSeekPointerUp = () => {
  pointerScrubbing.value = false;
};

const isBufferedAt = (sec) => {
  try {
    const v = art && art.video ? art.video : null;
    if (!v) return false;
    const b = v.buffered;
    if (!b || typeof b.length !== 'number' || b.length <= 0) return false;
    const t = Number(sec);
    if (!Number.isFinite(t)) return false;
    const eps = 0.25;
    for (let i = 0; i < b.length; i += 1) {
      const start = b.start(i);
      const end = b.end(i);
      if (Number.isFinite(start) && Number.isFinite(end) && t + eps >= start && t - eps <= end) return true;
    }
    return false;
  } catch (_e) {
    return false;
  }
};

const onSeekPreview = (e) => {
  const v = e && e.target ? Number(e.target.value) : NaN;
  if (!Number.isFinite(v)) return;
  if (!pointerScrubbing.value) {
    onSeekCommit(e);
    return;
  }
  scrubbing.value = true;
  scrubTime.value = Math.max(0, Math.min(duration.value || 0, v));
  showUiTemporarily();
};

const onSeekCommit = (e) => {
  const v = e && e.target ? Number(e.target.value) : NaN;
  if (!art || !Number.isFinite(v)) return;
  const next = Math.max(0, Math.min(duration.value || 0, v));
  pointerScrubbing.value = false;
  scrubbing.value = false;
  scrubTime.value = next;
  if (!isBufferedAt(next)) setBuffering(true);
  try {
    const videoEl = art && art.video ? art.video : null;
    if (videoEl && typeof videoEl.fastSeek === 'function') {
      videoEl.fastSeek(next);
    } else {
      art.currentTime = next;
    }
  } catch (_e) {
    try {
      art.currentTime = next;
    } catch (_ignored) {}
  }
  scheduleBufferedSync();
};

const onSeekCancel = () => {
  scrubbing.value = false;
  pointerScrubbing.value = false;
};

const setRate = (r) => {
  if (!art) return;
  art.playbackRate = r;
  playbackRate.value = r;
};

const setRatio = (r) => {
  if (!art) return;
  art.aspectRatio = r;
  aspectRatio.value = r;
};

const togglePip = () => {
  try {
    const videoEl = art && art.video ? art.video : null;
    if (!videoEl) return;
    if (document.pictureInPictureElement) {
      document.exitPictureInPicture();
    } else if (typeof videoEl.requestPictureInPicture === 'function') {
      videoEl.requestPictureInPicture();
    }
    showUiTemporarily();
  } catch (_) {}
};

const toggleFullscreen = async () => {
  try {
    const el = shell.value;
    if (!el || !art) return;
    const videoEl = art.video;

    // iOS Safari: use native fullscreen on the <video> element when possible.
    if (videoEl && typeof videoEl.webkitEnterFullscreen === 'function') {
      if (isFullscreen.value && typeof videoEl.webkitExitFullscreen === 'function') {
        videoEl.webkitExitFullscreen();
      } else {
        videoEl.webkitEnterFullscreen();
      }
      showUiTemporarily();
      return;
    }
    const fsEl = typeof document !== 'undefined' ? document.fullscreenElement : null;
    if (fsEl) {
      document.exitFullscreen().catch(() => {});
      showUiTemporarily();
      return;
    }
    const target = teleportTarget.value || el;
    if (target && typeof target.requestFullscreen === 'function') {
      target.requestFullscreen().catch(() => {});
    }
    showUiTemporarily();
  } catch (_) {}
};

let hideTimer = 0;
let desktopHideTimer = 0;

	const scheduleDesktopAutoHide = () => {
	  if (desktopHideTimer) window.clearTimeout(desktopHideTimer);
	  desktopHideTimer = 0;
	  if (!art) return;
	  if (!art.playing) return;
	  if (settingsOpen.value || goProxyMenuOpen.value || extraMenuOpenKey.value || buffering.value) return;
	  desktopHideTimer = window.setTimeout(() => {
	    if (!art) return;
	    if (art.playing && !settingsOpen.value && !goProxyMenuOpen.value && !extraMenuOpenKey.value && !buffering.value) desktopControlsVisible.value = false;
	  }, 2200);
	};

const showUiTemporarily = () => {
  if (isMobile.value) {
    uiVisible.value = true;
    if (hideTimer) window.clearTimeout(hideTimer);
    hideTimer = window.setTimeout(() => {
      if (!art) return;
      if (art.playing && !settingsOpen.value && !buffering.value) uiVisible.value = false;
    }, 2200);
    return;
  }

  desktopControlsVisible.value = true;
  scheduleDesktopAutoHide();
};

	const onDocDown = (e) => {
	  const target = e && e.target ? e.target : null;
	  if (settingsOpen.value) {
	    const el = settingEl.value;
	    if (!(el && target && el.contains(target))) settingsOpen.value = false;
	  }
	  if (goProxyMenuOpen.value) {
	    const el = goProxyEl.value;
	    if (!(el && target && el.contains(target))) goProxyMenuOpen.value = false;
	  }
	  if (extraMenuOpenKey.value) {
	    const el = extraMenuEls.get(extraMenuOpenKey.value) || null;
	    if (!(el && target && el.contains(target))) extraMenuOpenKey.value = '';
	  }
	};

	const setExtraMenuEl = (key, el) => {
	  try {
	    const k = typeof key === 'string' ? key : String(key || '');
	    if (!k) return;
	    if (el) extraMenuEls.set(k, el);
	    else extraMenuEls.delete(k);
	  } catch (_e) {}
	};

	const toggleExtraMenu = (key) => {
	  const k = typeof key === 'string' ? key : String(key || '');
	  if (!k) return;
	  settingsOpen.value = false;
	  goProxyMenuOpen.value = false;
	  extraMenuOpenKey.value = extraMenuOpenKey.value === k ? '' : k;
	  showUiTemporarily();
	};

	const selectExtraMenu = (key, value) => {
	  const k = typeof key === 'string' ? key : String(key || '');
	  if (!k) return;
	  extraMenuOpenKey.value = '';
	  showUiTemporarily();
	  emit('extramenuselect', { key: k, value: value != null ? String(value) : '' });
	};

	const fireExtraAction = (key) => {
	  const k = typeof key === 'string' ? key : String(key || '');
	  if (!k) return;
	  extraMenuOpenKey.value = '';
	  settingsOpen.value = false;
	  goProxyMenuOpen.value = false;
	  showUiTemporarily();
	  emit('extraaction', k);
	};

	const toggleSettingsMenu = () => {
	  goProxyMenuOpen.value = false;
	  extraMenuOpenKey.value = '';
	  settingsOpen.value = !settingsOpen.value;
	  showUiTemporarily();
	};

	const toggleGoProxyMenu = () => {
	  if (!props.goProxyOptions || !props.goProxyOptions.length) return;
	  settingsOpen.value = false;
	  extraMenuOpenKey.value = '';
	  goProxyMenuOpen.value = !goProxyMenuOpen.value;
	  showUiTemporarily();
	};

	const selectGoProxy = (base) => {
	  goProxyMenuOpen.value = false;
	  showUiTemporarily();
	  emit('goproxyselect', typeof base === 'string' ? base : '');
	};

const closeArtPopups = (target) => {
  if (!art) return;
  const playerEl = art && art.template && art.template.$player ? art.template.$player : null;
  if (!playerEl || !playerEl.classList) return;
  if (target && typeof target.closest === 'function') {
    if (target.closest('.art-contextmenus') || target.closest('.art-info')) return;
  }
  playerEl.classList.remove('art-contextmenu-show');
  playerEl.classList.remove('art-info-show');
};

onMounted(() => {
  // Ensure device flags are computed before initializing ArtPlayer,
  // otherwise desktop/mobile behaviors can be swapped after mount.
  updateIsMobile();
  init();

  try {
    if (typeof window !== 'undefined') window.addEventListener('resize', updateIsMobile, { passive: true });
    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
      mediaQuery = window.matchMedia('(max-width: 820px)');
      if (typeof mediaQuery.addEventListener === 'function') mediaQuery.addEventListener('change', updateIsMobile);
      else if (typeof mediaQuery.addListener === 'function') mediaQuery.addListener(updateIsMobile);
    }
  } catch (_e) {
    mediaQuery = null;
  }

  document.addEventListener(
    'mousedown',
    (e) => {
      try {
        onDocDown(e);
      } catch (_e) {}
      try {
        closeArtPopups(e && e.target ? e.target : null);
      } catch (_e) {}
    },
    true
  );
  try {
    const el = shell.value;
    if (el) {
      const onTouchMove = () => showUiTemporarily();
      el.addEventListener('touchmove', onTouchMove, { passive: true });

      // iOS Safari (and some WebViews) may not reliably deliver pointerdown/touchstart here due to the video element.
      // Use click capture as the reliable "tap" signal for mobile UI toggle.
      const onBlankClickCapture = (evt) => {
        if (!evt || !evt.target || !el.contains(evt.target)) return;
        if (!isMobile.value) return;
        if (isUiControlTarget(evt.target)) return;
        if (!uiVisible.value) {
          settingsOpen.value = false;
          uiVisible.value = true;
          showUiTemporarily();
        } else {
          settingsOpen.value = false;
          uiVisible.value = false;
        }
        try {
          evt.preventDefault();
        } catch (_e) {}
        try {
          if (typeof evt.stopImmediatePropagation === 'function') evt.stopImmediatePropagation();
        } catch (_e) {}
        try {
          evt.stopPropagation();
        } catch (_e) {}
      };
      el.addEventListener('click', onBlankClickCapture, { capture: true });

	      const onKeyDown = (evt) => {
	        if (!evt) return;
	        if (isMobile.value) return;
	        if (!art) return;
        if (evt.defaultPrevented) return;
        if (evt.ctrlKey || evt.metaKey || evt.altKey) return;
        if (isUiControlTarget(evt.target)) return;
        const tag = evt.target && evt.target.tagName ? String(evt.target.tagName).toLowerCase() : '';
        const editable =
          (evt.target && evt.target.isContentEditable) ||
          tag === 'input' ||
          tag === 'textarea' ||
          tag === 'select';
        if (editable) return;
        const key = String(evt.key || '');
        if (key === ' ' || key === 'Spacebar') {
          evt.preventDefault();
          togglePlay();
          showUiTemporarily();
          return;
        }
	        if (key === 'ArrowLeft') {
	          evt.preventDefault();
	          seekBySeconds(-5);
	          showUiTemporarily();
	          return;
	        }
	        if (key === 'ArrowRight') {
	          evt.preventDefault();
	          seekBySeconds(5);
	          showUiTemporarily();
	          return;
	        }
	      };
      window.addEventListener('keydown', onKeyDown, true);

      const onFsChange = () => {
        // Some browsers (and some fullscreen implementations) may re-parent elements,
        // making containment checks unreliable. For our UI, "any fullscreen" is enough.
        isFullscreen.value = !!document.fullscreenElement;
        scheduleSyncDesktopRightControlsFit();
        if (!isMobile.value) {
          desktopControlsVisible.value = true;
          scheduleDesktopAutoHide();
        }
      };
      document.addEventListener('fullscreenchange', onFsChange, true);
      onFsChange();

      el.__tvCleanupPlayerUi = () => {
        el.removeEventListener('touchmove', onTouchMove);
        el.removeEventListener('click', onBlankClickCapture, true);
        try {
          window.removeEventListener('keydown', onKeyDown, true);
        } catch (_e) {}
        document.removeEventListener('fullscreenchange', onFsChange, true);
        delete el.__tvCleanupPlayerUi;
      };
    }
  } catch (_) {}
  bindDesktopLayoutObserver();
  scheduleSyncDesktopRightControlsFit();
});
watch(
  () => props.url,
  (next, prev) => {
    const a = (next || '').trim();
    const b = (prev || '').trim();
    if (a && a !== b) init();
    if (!a && art) destroyNow();
  }
);

	watch(
	  () => {
	    const extra = props.statsExtra && typeof props.statsExtra === 'object' ? props.statsExtra : {};
	    return `${normalizeInfoValue(extra.displayName)}|${normalizeInfoValue(extra.siteName)}|${normalizeInfoValue(extra.panName)}|${normalizeInfoValue(extra.pathName)}|${normalizeInfoValue(extra.rawFileName)}`;
	  },
	  () => syncInfoExtraIntoArtInfoPanel(),
	  { immediate: true }
	);

onBeforeUnmount(() => {
  if (rightFitRaf) {
    try {
      if (typeof window.cancelAnimationFrame === 'function') window.cancelAnimationFrame(rightFitRaf);
    } catch (_e) {}
    rightFitRaf = 0;
  }
  if (hideTimer) {
    window.clearTimeout(hideTimer);
    hideTimer = 0;
  }
  if (desktopHideTimer) {
    window.clearTimeout(desktopHideTimer);
    desktopHideTimer = 0;
  }
  try {
    if (typeof window !== 'undefined') window.removeEventListener('resize', updateIsMobile);
    if (mediaQuery) {
      if (typeof mediaQuery.removeEventListener === 'function') mediaQuery.removeEventListener('change', updateIsMobile);
      else if (typeof mediaQuery.removeListener === 'function') mediaQuery.removeListener(updateIsMobile);
    }
  } catch (_e) {}
  mediaQuery = null;
  try {
    if (typeof cleanupDesktopLayoutObserver === 'function') cleanupDesktopLayoutObserver();
  } catch (_e) {}
  cleanupDesktopLayoutObserver = null;
  document.removeEventListener('mousedown', onDocDown, true);
  try {
    const el = shell.value;
    if (el && el.__tvCleanupPlayerUi) el.__tvCleanupPlayerUi();
  } catch (_) {}
  if (!art) return;
  destroyNow();
});

const pause = () => {
  try {
    if (art && typeof art.pause === 'function') {
      art.pause();
      return;
    }
    const v = art && art.video ? art.video : null;
    if (v && typeof v.pause === 'function') v.pause();
  } catch (_e) {}
};

const play = async () => {
  try {
    if (art && typeof art.play === 'function') return await art.play();
    const v = art && art.video ? art.video : null;
    if (v && typeof v.play === 'function') return await v.play();
  } catch (_e) {}
};

	const tryAutoplay = async () => {
	  await play();
	};

	const seekTo = (seconds) => {
	  const raw = Number(seconds);
	  if (!Number.isFinite(raw)) return false;
	  const target = Math.max(0, raw);
	  try {
	    if (art) {
	      if (typeof art.seek === 'function') {
	        art.seek(target);
	        return true;
	      }
	      if (art.seek != null) {
	        art.seek = target;
	        return true;
	      }
	    }
	    const v = art && art.video ? art.video : null;
	    if (!v) return false;
	    const dur = Number(v.duration);
	    if (Number.isFinite(dur) && dur > 0) {
	      v.currentTime = Math.max(0, Math.min(target, Math.max(0, dur - 0.2)));
	    } else {
	      v.currentTime = target;
	    }
	    return true;
	  } catch (_e) {
	    return false;
	  }
	};

	defineExpose({ destroy: destroyNow, pause, play, tryAutoplay, seekTo });
</script>

<style scoped>
.tv-artplayer {
  --yt-btn-size: 34px;
  --yt-ico-size: 18px;
  --yt-pill-pad-y: 8px;
  --yt-pill-pad-x: 10px;
  --yt-pill-gap: 8px;
  --yt-time-size: 12px;
  --yt-font-family: "PingFang SC", "Helvetica Neue", "Microsoft YaHei", Roboto, Arial, sans-serif;

  position: relative;
  width: 100%;
  height: 100%;
  background: #000;
  border-radius: var(--play-player-radius, 12px);
}

.tv-artplayer.tv-artplayer--fullscreen {
  /* Fullscreen: keep capsule height ~50px on 2K, scale smoothly on other screens. */
  --yt-pill-h: clamp(42px, 1.95vw, 50px);
  --yt-btn-size: calc(var(--yt-pill-h) - 10px);
  --yt-ico-size: calc(var(--yt-btn-size) * 0.52);
  --yt-pill-pad-x: clamp(10px, 0.65vw, 14px);
  --yt-pill-gap: clamp(8px, 0.55vw, 12px);
  --yt-time-size: clamp(12px, 0.55vw, 14px);
}

.artplayer-root {
  width: 100%;
  height: 100%;
  border-radius: var(--play-player-radius, 12px);
  overflow: hidden;
}

.yt-toast {
  position: absolute;
  left: 50%;
  bottom: 64px;
  transform: translateX(-50%);
  max-width: min(80vw, 520px);
  padding: 8px 12px;
  border-radius: 999px;
  background: rgba(18, 18, 18, 0.92);
  border: 1px solid rgba(255, 255, 255, 0.16);
  color: rgba(255, 255, 255, 0.9);
  font-size: 12px;
  font-weight: 800;
  box-shadow: 0 18px 44px rgba(0, 0, 0, 0.55);
  user-select: none;
  pointer-events: none;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  z-index: 160;
}

.yt-toast--sticky {
  background: rgba(19, 24, 34, 0.94);
  border-color: rgba(120, 185, 255, 0.42);
}

:deep(.art-video-player) {
  position: relative;
  width: 100% !important;
  height: 100% !important;
  border-radius: inherit;
  overflow: hidden;
}

:deep(.art-contextmenus) {
  z-index: 150 !important;
}

:deep(.art-info) {
  z-index: 140 !important;
  pointer-events: auto;
}

:deep(.art-info) .tv-art-info-extra {
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid rgba(255, 255, 255, 0.12);
  font-size: 12px;
  line-height: 1.4;
  color: rgba(255, 255, 255, 0.86);
  display: block;
  width: 100%;
  flex: 0 0 100%;
  grid-column: 1 / -1;
}

:deep(.art-info) .tv-art-info-extra__line {
  display: block;
  width: 100%;
  margin: 4px 0;
  color: rgba(255, 255, 255, 0.82);
  word-break: break-all;
}

.tv-artplayer.tv-artplayer--fullscreen :deep(.art-video-player),
.tv-artplayer.tv-artplayer--fullscreen :deep(.art-video),
.tv-artplayer.tv-artplayer--fullscreen :deep(video) {
  transform: translateZ(0);
  will-change: transform;
  backface-visibility: hidden;
}

.tv-artplayer.tv-artplayer--fullscreen :deep(.art-video),
.tv-artplayer.tv-artplayer--fullscreen :deep(video) {
  filter: opacity(0.999);
}

:deep(.art-video) {
  width: 100% !important;
  height: 100% !important;
  object-fit: contain;
}

:deep(.art-bottom) {
  display: none !important;
}

:deep(.art-progress),
:deep(.art-controls),
:deep(.art-controls-left),
:deep(.art-controls-center),
:deep(.art-controls-right) {
  display: none !important;
}

.tv-artplayer.tv-artplayer--mobile :deep(.art-layer-play) {
  display: none !important;
}

.tv-artplayer.tv-artplayer--mobile :deep(.art-state) {
  display: none !important;
}

.tv-artplayer :deep(.art-loading),
.tv-artplayer :deep(.art-layer-loading) {
  display: none !important;
}

.yt-ui {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  z-index: 110;
  font-family: var(--yt-font-family);
  text-shadow: none;
  opacity: 0;
  visibility: hidden;
  pointer-events: none;
  transition: opacity 0.18s ease;
}

.yt-ui.yt-ui--show {
  opacity: 1;
  visibility: visible;
  pointer-events: auto;
}

.yt-top {
  position: absolute;
  left: 0;
  right: 0;
  top: 0;
  z-index: 120;
  display: flex;
  align-items: flex-start;
  justify-content: flex-start;
  padding-top: max(10px, calc(env(safe-area-inset-top) + 8px));
  padding-left: 12px;
  padding-right: 12px;
  pointer-events: auto;
}

.yt-top::before {
  content: "";
  position: absolute;
  inset: 0;
  background: linear-gradient(
    180deg,
    rgba(0, 0, 0, 0.46) 0%,
    rgba(0, 0, 0, 0.24) 52%,
    rgba(0, 0, 0, 0) 100%
  );
  pointer-events: none;
}

.yt-top__back {
  position: relative;
  z-index: 1;
  pointer-events: auto;
  height: 32px;
  width: 32px;
  border: 0;
  border-radius: 0;
  background: transparent;
  color: rgba(255, 255, 255, 0.96);
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  transition: opacity 0.15s ease;
  touch-action: manipulation;
}

.yt-top__back:hover {
  opacity: 0.85;
}

.yt-top__back svg {
  width: 24px;
  height: 24px;
  display: block;
  filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.55));
}

.yt-top__title {
  position: relative;
  z-index: 1;
  margin-left: 8px;
  min-width: 0;
  min-height: 32px;
  max-width: min(72vw, 520px);
  color: rgba(255, 255, 255, 0.95);
  font-size: 13px;
  font-weight: 500;
  line-height: 32px;
  letter-spacing: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  text-shadow: none;
  -webkit-font-smoothing: antialiased;
  text-rendering: optimizeLegibility;
}

.yt-progress__range {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  display: block;
  margin: 0;
  padding: 0;
  border: 0;
  outline: none;
  background: transparent;
  -webkit-appearance: none;
  appearance: none;
  cursor: pointer;
}

.yt-progress__range::-webkit-slider-runnable-track {
  height: var(--yt-progress-track-h);
  border-radius: 999px;
  background: transparent;
}

.yt-progress__range::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  margin-top: calc((var(--yt-progress-track-h) - var(--yt-progress-thumb-h)) / 2);
  width: var(--yt-progress-thumb-h);
  height: var(--yt-progress-thumb-h);
  border-radius: 999px;
  background: #ff0033;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.45);
}

.yt-progress__range::-moz-range-track {
  height: var(--yt-progress-track-h);
  border-radius: 999px;
  background: transparent;
}

.yt-progress__range::-moz-range-thumb {
  width: var(--yt-progress-thumb-h);
  height: var(--yt-progress-thumb-h);
  border-radius: 999px;
  border: 0;
  background: #ff0033;
}

.yt-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 12px 12px 12px;
  background: linear-gradient(to top, rgba(0, 0, 0, 0.55), rgba(0, 0, 0, 0.0));
  pointer-events: none;
}

.yt-ui.yt-ui--show .yt-bar {
  pointer-events: auto;
}

.yt-progress {
  position: relative;
  margin: 0 2px 14px;
  --yt-progress-track-h: 8px;
  --yt-progress-thumb-h: 14px;
  --yt-progress-p: 0;
  --yt-buffer-p: 0;
  height: var(--yt-progress-thumb-h);
  pointer-events: auto;
  cursor: pointer;
}

.yt-progress__track {
  position: absolute;
  left: 0;
  right: 0;
  top: calc((var(--yt-progress-thumb-h) - var(--yt-progress-track-h)) / 2);
  height: var(--yt-progress-track-h);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.25);
  overflow: hidden;
  pointer-events: none;
}

.yt-progress__buffer {
  position: absolute;
  left: 0;
  top: 0;
  height: 100%;
  width: calc(
    (var(--yt-buffer-p) * (100% - var(--yt-progress-thumb-h))) + (var(--yt-progress-thumb-h) / 2)
  );
  max-width: 100%;
  background: rgba(255, 255, 255, 0.6);
  border-radius: 999px;
  pointer-events: none;
}

.yt-progress__fill {
  position: absolute;
  left: 0;
  top: 0;
  height: 100%;
  width: calc(
    (var(--yt-progress-p) * (100% - var(--yt-progress-thumb-h))) + (var(--yt-progress-thumb-h) / 2)
  );
  max-width: 100%;
  background: #ff0033;
  border-radius: 999px;
}

.yt-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.yt-pill {
  display: inline-flex;
  align-items: center;
  gap: var(--yt-pill-gap);
  padding: var(--yt-pill-pad-y) var(--yt-pill-pad-x);
  border-radius: 999px;
  background: rgba(20, 20, 20, 0.45);
  color: rgba(255, 255, 255, 0.92);
}

.tv-artplayer.tv-artplayer--fullscreen .yt-pill {
  height: var(--yt-pill-h);
  padding-top: 0;
  padding-bottom: 0;
}

.yt-btn {
  width: var(--yt-btn-size);
  height: var(--yt-btn-size);
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: rgba(255, 255, 255, 0.92);
  outline: none;
}

.yt-btn:hover {
  background: rgba(255, 255, 255, 0.1);
}

.yt-btn:focus,
.yt-btn:focus-visible {
  outline: none;
  box-shadow: none;
}

.yt-btn[data-active='true'] {
  border-color: rgba(34, 197, 94, 0.55);
  background: rgba(34, 197, 94, 0.18);
  color: rgba(74, 222, 128, 1);
}

.yt-ico {
  width: var(--yt-ico-size);
  height: var(--yt-ico-size);
}

.yt-time {
  font-size: var(--yt-time-size);
  font-weight: 700;
  color: rgba(255, 255, 255, 0.82);
  padding: 0 6px 0 2px;
  white-space: nowrap;
}

.yt-volume {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.yt-volume__slider {
  width: 0;
  overflow: hidden;
  opacity: 0;
  transition: width 0.18s ease, opacity 0.18s ease;
}

.yt-volume__slider[data-show='true'] {
  width: 86px;
  opacity: 1;
}

.yt-volume__range {
  width: 86px;
  height: 6px;
  background: transparent;
  -webkit-appearance: none;
  appearance: none;
}

.yt-volume__range::-webkit-slider-runnable-track {
  height: 4px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.25);
}

.yt-volume__range::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  margin-top: -3px;
  width: 10px;
  height: 10px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.9);
}

.yt-setting {
  position: relative;
}

.yt-proxy {
  position: relative;
  display: inline-flex;
  align-items: center;
}

.yt-proxy__btn {
  height: var(--yt-btn-size);
  border-radius: 999px;
  padding: 0 12px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.12);
  color: rgba(255, 255, 255, 0.92);
  font-size: 12px;
  font-weight: 800;
  white-space: nowrap;
  user-select: none;
}

.yt-proxy__btn:hover {
  background: rgba(255, 255, 255, 0.1);
}

.yt-proxy__btn[data-open='true'] {
  border-color: rgba(34, 197, 94, 0.55);
  background: rgba(34, 197, 94, 0.18);
  color: rgba(74, 222, 128, 1);
}

.yt-proxy__menu {
  position: absolute;
  right: 0;
  bottom: calc(100% + 10px);
  width: max-content;
  max-width: min(80vw, 360px);
  border-radius: 14px;
  padding: 8px;
  background: rgba(18, 18, 18, 0.92);
  border: 1px solid rgba(255, 255, 255, 0.12);
  box-shadow: 0 20px 48px rgba(0, 0, 0, 0.55);
  display: none;
}

.yt-proxy__menu--open {
  display: grid;
  grid-auto-flow: row;
  gap: 8px;
}

.yt-proxy__item {
  width: 100%;
  height: 34px;
  padding: 0 10px;
  display: flex;
  align-items: center;
  justify-content: center;
  text-align: center;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.1);
  color: rgba(255, 255, 255, 0.88);
  font-size: 12px;
  font-weight: 800;
  white-space: nowrap;
}

.yt-proxy__item:hover {
  background: rgba(255, 255, 255, 0.1);
}

.yt-proxy__item[data-active='true'] {
  border-color: rgba(34, 197, 94, 0.55);
  background: rgba(34, 197, 94, 0.18);
  color: rgba(74, 222, 128, 1);
}

.yt-setting__menu {
  position: absolute;
  right: 0;
  bottom: calc(100% + 10px);
  width: 240px;
  border-radius: 14px;
  padding: 10px;
  background: rgba(18, 18, 18, 0.92);
  border: 1px solid rgba(255, 255, 255, 0.12);
  box-shadow: 0 20px 48px rgba(0, 0, 0, 0.55);
  display: none;
}

.yt-setting__menu--open {
  display: block;
}

.yt-setting__section + .yt-setting__section {
  margin-top: 12px;
}

.yt-setting__title {
  font-size: 12px;
  font-weight: 800;
  color: rgba(255, 255, 255, 0.78);
  margin-bottom: 8px;
}

.yt-setting__grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.yt-setting__item {
  height: 32px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.1);
  color: rgba(255, 255, 255, 0.88);
  font-size: 12px;
  font-weight: 800;
}

.yt-setting__item:hover {
  background: rgba(255, 255, 255, 0.1);
}

.yt-setting__item[data-active='true'] {
  border-color: rgba(34, 197, 94, 0.55);
  background: rgba(34, 197, 94, 0.18);
  color: rgba(74, 222, 128, 1);
}

/* -------------------- Mobile controls -------------------- */
.tv-artplayer.tv-artplayer--mobile .yt-ui {
  /* Mobile: keep controls visible a bit more naturally on tap */
  transition: opacity 0.16s ease;
}

.tv-artplayer.tv-artplayer--mobile {
  --m-control-bottom: 10px;
}

.m-bar {
  position: absolute;
  inset: 0;
  pointer-events: auto;
}

.tv-artplayer.tv-artplayer--mobile .m-progress {
  position: absolute;
  left: 12px;
  right: 60px; /* leave room for fullscreen */
  /* Align the progress thumb center with the fullscreen button center */
  bottom: calc(var(--m-control-bottom) + (var(--yt-btn-size) - var(--yt-progress-thumb-h)) / 2);
  margin: 0;
  --yt-progress-track-h: 6px;
  --yt-progress-thumb-h: 12px;
}

.tv-artplayer.tv-artplayer--mobile .m-setting {
  position: absolute;
  top: 10px;
  right: 10px;
}

.tv-artplayer.tv-artplayer--mobile .m-setting .yt-setting__menu {
  top: calc(100% + 10px);
  bottom: auto;
  right: 0;
  transform-origin: top right;
}

.tv-artplayer.tv-artplayer--mobile .m-fullscreen {
  position: absolute;
  right: 10px;
  bottom: var(--m-control-bottom);
}

.tv-artplayer.tv-artplayer--mobile .m-center {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.16s ease;
  display: block;
}

.tv-artplayer.tv-artplayer--mobile .m-center.m-center--show {
  opacity: 1;
  pointer-events: auto;
}

.tv-artplayer.tv-artplayer--mobile .m-center__controls {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
}

.tv-artplayer.tv-artplayer--mobile .m-btn {
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: rgba(20, 20, 20, 0.38);
  border: 1px solid rgba(255, 255, 255, 0.14);
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  color: rgba(255, 255, 255, 0.92);
}

.tv-artplayer.tv-artplayer--mobile .m-btn--play {
  width: 62px;
  height: 62px;
  background: rgba(20, 20, 20, 0.48);
  border-color: rgba(255, 255, 255, 0.18);
  position: relative;
}

.tv-artplayer.tv-artplayer--mobile .m-btn--play[data-loading='true']::after {
  content: '';
  position: absolute;
  /* Sit the ring on the button border (no extra gap) */
  inset: -1px;
  border-radius: 999px;
  pointer-events: none;
  background:
    radial-gradient(circle at 50% 0%, rgba(255, 255, 255, 0.95) 0 3px, transparent 4px),
    conic-gradient(
      from 0deg,
      rgba(255, 255, 255, 0) 0deg,
      rgba(255, 255, 255, 0.12) 70deg,
      rgba(255, 255, 255, 0.28) 140deg,
      rgba(255, 255, 255, 0.62) 250deg,
      rgba(255, 255, 255, 0.0) 360deg
    );
  /* Ring thickness */
  -webkit-mask: radial-gradient(farthest-side, transparent calc(100% - 6px), #000 calc(100% - 4px));
  mask: radial-gradient(farthest-side, transparent calc(100% - 6px), #000 calc(100% - 4px));
  animation: tv-ring-spin 0.9s linear infinite;
  opacity: 0.95;
}

.tv-artplayer.tv-artplayer--mobile .m-btn--play[data-loading='true']::before {
  content: '';
  position: absolute;
  inset: -1px;
  border-radius: 999px;
  pointer-events: none;
  box-shadow: 0 0 22px rgba(255, 255, 255, 0.08);
  opacity: 1;
}

.tv-artplayer.tv-artplayer--mobile .m-btn--skip {
  width: 46px;
  height: 46px;
}

.tv-artplayer.tv-artplayer--mobile .m-ico {
  width: 20px;
  height: 20px;
}

.tv-artplayer.tv-artplayer--mobile .m-ico--play {
  width: 26px;
  height: 26px;
}

.tv-artplayer .m-buffer-mask {
  position: absolute;
  inset: 0;
  background: transparent;
  pointer-events: none;
  z-index: 10000;
}

.tv-artplayer .m-buffer-ring {
  position: absolute;
  left: 50%;
  top: 50%;
  width: 62px;
  height: 62px;
  transform: translate(-50%, -50%);
  border-radius: 999px;
  pointer-events: none;
  z-index: 10001;
}

.tv-artplayer .m-buffer-ring::after {
  content: '';
  position: absolute;
  inset: -1px;
  border-radius: 999px;
  background:
    radial-gradient(circle at 50% 0%, rgba(255, 255, 255, 0.95) 0 3px, transparent 4px),
    conic-gradient(
      from 0deg,
      rgba(255, 255, 255, 0) 0deg,
      rgba(255, 255, 255, 0.12) 70deg,
      rgba(255, 255, 255, 0.28) 140deg,
      rgba(255, 255, 255, 0.62) 250deg,
      rgba(255, 255, 255, 0.0) 360deg
    );
  -webkit-mask: radial-gradient(farthest-side, transparent calc(100% - 6px), #000 calc(100% - 4px));
  mask: radial-gradient(farthest-side, transparent calc(100% - 6px), #000 calc(100% - 4px));
  animation: tv-ring-spin 0.9s linear infinite;
  opacity: 0.95;
}

@media (prefers-reduced-motion: reduce) {
  .tv-artplayer .m-buffer-ring::after,
  .tv-artplayer.tv-artplayer--mobile .m-btn--play[data-loading='true']::after {
    animation: none;
  }
}

@keyframes tv-ring-spin {
  to {
    transform: rotate(360deg);
  }
}

/* iOS Safari: backdrop-filter overlays can trigger "audio-only / black video" rendering bugs.
   Disable blur glass effects for mobile controls on iOS to keep video rendering stable. */
@supports (-webkit-touch-callout: none) {
  .tv-artplayer.tv-artplayer--mobile .m-btn,
  .tv-artplayer.tv-artplayer--mobile .yt-btn,
  .tv-artplayer.tv-artplayer--mobile .yt-setting__menu {
    backdrop-filter: none !important;
    -webkit-backdrop-filter: none !important;
  }
}
</style>
