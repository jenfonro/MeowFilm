<template>
  <main
    id="playPage"
    class="play-page play-page-root"
    :class="{ 'play-page--portrait': isPortraitMode }"
  >
    <div class="play-page__content">
      <div class="play-header ui-page-header">
        <div class="play-header__row ui-page-header__row">
          <button type="button" class="ui-nav-back-btn" aria-label="返回" @click="onBackClick">
            <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="m15 18-6-6 6-6"></path>
            </svg>
          </button>
          <div class="play-header__main ui-page-header__main">
            <div class="play-header__titleRow ui-page-header__titleRow">
              <h1 class="play-header__title">
              <span id="playTitle" class="play-header__titleText">{{ displayTitle }}</span>
              </h1>
              <button type="button" class="play-detail__favBtn" aria-label="收藏">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="play-detail__favIcon" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <path d="M21 8.25c0-2.485-2.099-4.5-4.688-4.5-1.935 0-3.597 1.126-4.312 2.733-.715-1.607-2.377-2.733-4.313-2.733C5.1 3.75 3 5.765 3 8.25c0 7.22 9 12 9 12s9-4.78 9-12Z" />
                </svg>
              </button>
            </div>
          </div>
        </div>
      </div>

      <div class="play-page__main">
        <div class="play-episode-toggle-wrap">
          <button
            id="playPortraitToggle"
            class="play-episode-toggle"
            type="button"
            :title="portraitModeToggleTitleText"
            :aria-label="portraitModeToggleTitleText"
            @click="togglePortraitMode"
          >
            <svg class="play-episode-toggle__icon" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
              <rect x="7.5" y="3.5" width="9" height="17" rx="2"></rect>
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10 6h4"></path>
            </svg>
            <span class="play-episode-toggle__label">{{ portraitModeToggleLabelText }}</span>
          </button>
          <button
            id="episodePanelToggle"
            class="play-episode-toggle"
            type="button"
            :title="episodePanelToggleTitleText"
            :aria-label="episodePanelToggleTitleText"
            @click="toggleEpisodePanel"
          >
            <svg
              id="episodePanelToggleIcon"
              class="play-episode-toggle__icon"
              :class="{ 'play-episode-toggle__icon--collapsed': isEpisodePanelCollapsed }"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7"></path>
            </svg>
            <span id="episodePanelToggleLabel" class="play-episode-toggle__label">{{ episodePanelToggleLabelText }}</span>
            <div id="episodePanelToggleDot" class="play-episode-toggle__dot"></div>
          </button>
        </div>

	        <div id="playGrid" ref="playGridEl" class="play-grid" :class="{ 'play-grid--panel-hidden': isEpisodePanelCollapsed }">
	          <div id="playerArea" ref="playerAreaEl" class="play-player-area" :style="portraitPlayerAreaStyle">
	            <div ref="playerAreaContentEl" class="play-player-stack">
              <div class="play-video-ratio">
                <div class="play-video-ratio__inner">
                  <ArtPlayer
                    ref="artPlayerRef"
                    :url="playerUrl"
                    :poster="''"
                    :headers="playerHeaders"
                    :title="displayTitle"
                    :portrait-mode="isPortraitMode"
                    :portrait-top-text="portraitTopTitleEpisodeText"
                    :stats-extra="playerStatsExtra"
                    :extra-menus="playerExtraMenus"
                    :extra-actions="playerExtraActions"
                    :go-proxy-options="goProxyUiOptions"
                    :go-proxy-selected-base="goProxyManualBase"
                    :go-proxy-label="goProxyUiLabel"
                    :show-buffer-ring="playerPhase === 'buffering'"
                    :toast-text="playerToastText"
                    :toast-sticky="playerToastSticky"
                    @error="onPlayerError"
                    @buffering="onPlayerBuffering"
                    @playing="onPlayerPlaying"
                    @firstframe="onPlayerFirstFrame"
                    @ended="onPlayerEnded"
                    @loadedmetadata="onPlayerVideoInfo"
                    @videoinfo="onPlayerVideoInfo"
                    @timeupdate="onPlayerTimeUpdate"
                    @goproxyselect="onGoProxySelect"
                    @extramenuselect="onPlayerExtraMenuSelect"
                    @extraaction="onPlayerExtraAction"
                    @episodedelta="onPlayerEpisodeDelta"
                    @exit-portrait="togglePortraitMode"
                  />
                  <div
                    v-show="showPlayerStatusOverlay"
                    class="play-player-overlay"
                    :class="{ 'play-player-overlay--error': playerPhase === 'error' }"
                  >
                    <div class="play-player-overlay__panel" :style="{ '--play-stage-p': playerStageProgress }">
                      <div class="play-player-overlay__text">
                        <div class="play-player-overlay__status">
                          <div class="play-player-overlay__statusText">{{ playerPhaseText }}</div>
                          <div v-if="playerPhaseLoading" class="tv-spinner play-player-overlay__spinner" aria-hidden="true"></div>
                        </div>
                      </div>
                      <div
                        class="play-player-overlay__progress"
                        role="progressbar"
                        :aria-label="`加载进度：${Math.round(playerStageProgress * 100)}%`"
                        :aria-valuenow="Math.round(playerStageProgress * 100)"
                        aria-valuemin="0"
                        aria-valuemax="100"
                      >
                        <div class="play-player-overlay__track" aria-hidden="true">
                          <div class="play-player-overlay__fill"></div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>

              <div class="play-thirdparty-bar">
                <div class="play-thirdparty-bar__inner">
                  <button
                    v-for="player in thirdPartyVisiblePlayers"
                    :key="player.icon"
                    type="button"
                    class="play-thirdparty-btn"
                    :aria-label="player.name"
                    :title="player.name"
                    :disabled="!playerUrl"
                    @click="openWithThirdPartyPlayer(player)"
                  >
                    <img :src="`/images/${player.icon}.webp`" :alt="player.name" class="play-thirdparty-icon" />
                  </button>
                  <button
                    type="button"
                    class="play-thirdparty-expand"
                    :aria-label="thirdPartyExpanded ? '收起' : '展开'"
                    :title="thirdPartyExpanded ? '收起' : '展开'"
                    @click="toggleThirdPartyExpanded"
                  >
                    <svg
                      class="play-thirdparty-expand__ico"
                      :class="{ 'play-thirdparty-expand__ico--open': thirdPartyExpanded }"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                      aria-hidden="true"
                    >
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7"></path>
                    </svg>
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div
            v-show="!isEpisodePanelCollapsed"
            id="episodePanel"
            ref="episodePanelEl"
            class="play-episode-panel"
            :style="episodePanelStyle"
          >
            <div id="episodePanelResizer" ref="episodePanelResizerEl" class="episode-resizer" aria-hidden="true"></div>
            <div id="episodeSelector" class="play-episode-selector">
              <div class="episode-tab-header play-episode-tabs">
                <button id="tabEpisodes" type="button" class="episode-tab-btn episode-tab-btn--active">
                  选集
                </button>
                <button id="tabSources" type="button" class="episode-tab-btn">
                  换源
                </button>
              </div>
              <div class="play-episode-body">
                <div id="playSiteSourceRow" class="ui-select-row play-picker-row">
                  <div class="ui-select-row__main">
                    <div
                      id="playSiteSourceSelect"
                      ref="siteSourceSelectEl"
                      class="ui-selectbox ui-selectbox--compact play-picker"
                    >
                      <button
                        id="playSiteSourceTrigger"
                        type="button"
                        class="ui-selectbox__trigger"
                        aria-haspopup="listbox"
                        aria-controls="playSiteSourceMenu"
                        :aria-expanded="siteSourceOpen ? 'true' : 'false'"
                        @click.stop="toggleSiteSourceMenu"
                      >
                        {{ siteSourceTriggerLabel }}
                      </button>
                      <div
                        v-show="siteSourceOpen"
                        id="playSiteSourceMenu"
                        class="ui-selectbox__menu"
                        role="listbox"
                        aria-label="站点源列表"
                      >
                        <div
                          v-for="option in siteSourceOptions"
                          :key="option"
                          class="ui-selectbox__option"
                          :class="{ 'is-active': !selectedSiteResultItem && option === selectedSiteSource }"
                          role="option"
                          :aria-selected="!selectedSiteResultItem && option === selectedSiteSource ? 'true' : 'false'"
                          @click.stop="selectSiteSource(option)"
                        >{{ option }}</div>
                        <div
                          v-for="item in siteSourceResultItems"
                          :key="item.id"
                          class="ui-selectbox__option"
                          :class="{ 'is-active': item.id === selectedSearchResultId }"
                          role="option"
                          :aria-selected="item.id === selectedSearchResultId ? 'true' : 'false'"
                          @click.stop="selectSiteSourceResult(item)"
                        >{{ item.displayLabel }}</div>
                        <div
                          v-if="showSiteSourceSearchOption"
                          class="ui-selectbox__option"
                          :class="{ 'is-disabled': !siteSourceSearchInteractive }"
                          role="option"
                          aria-selected="false"
                          @click.stop="triggerSiteSourceSearchMore"
                        >{{ siteSourceSearchLabel }}</div>
                      </div>
                    </div>
                  </div>
                  <div v-if="showPreOrderButton" class="ui-select-row__actions">
                    <button
                      type="button"
                      class="ui-control-btn"
                      :class="{ 'is-active': preOrderActive }"
                      :disabled="preOrderToggleBusy"
                      :title="preOrderActive ? '关闭点映' : '开启点映'"
                      @click="togglePreOrder"
                    >
                      点映
                    </button>
                  </div>
                </div>
                <div v-if="showPanSourceRow" id="playPanSourceRow" class="ui-select-row play-picker-row">
                  <div class="ui-select-row__main">
                    <div id="playPanSourceSelect" ref="panSourceSelectEl" class="ui-selectbox ui-selectbox--compact play-picker">
                      <button
                        id="playPanSourceTrigger"
                        type="button"
                        class="ui-selectbox__trigger"
                        aria-haspopup="listbox"
                        aria-controls="playPanSourceMenu"
                        :aria-expanded="panSourceOpen ? 'true' : 'false'"
                        @click.stop="togglePanSourceMenu"
                      >
                        {{ currentPanSourceLabel }}
                      </button>
                      <div
                        v-show="panSourceOpen"
                        id="playPanSourceMenu"
                        class="ui-selectbox__menu"
                        role="listbox"
                        aria-label="网盘源列表"
                        :style="panSourceMenuStyle"
                      >
                        <div
                          v-for="option in currentPanSourceOptions"
                          :key="option.key"
                          class="ui-selectbox__option"
                          :class="{ 'is-active': option.key === selectedPanSource }"
                          role="option"
                          :aria-selected="option.key === selectedPanSource ? 'true' : 'false'"
                          @click.stop="selectPanSource(option.key)"
                        >
                          {{ option.label }}
                        </div>
                      </div>
                    </div>
                  </div>
                  <div class="ui-select-row__actions">
                    <button
                      v-if="selectedSiteResultItem && projectionSourceOptions.length > 1 && !rawListMode"
                      type="button"
                      class="ui-control-btn"
                      @click="toggleProjectionSource"
                    >
                      {{ selectedProjectionSource === '豆瓣' ? 'TMDB' : '豆瓣' }}
                    </button>
                    <button v-if="!effectiveForceRawListMode" type="button" class="ui-control-btn" @click="toggleRawList">
                      {{ rawListMode ? '返回选集' : '原始列表' }}
                    </button>
                  </div>
                </div>
                <div
                  v-if="showSeasonBar"
                  class="play-season-bar"
                  :class="{ 'play-season-bar--with-range': showRangeBar, 'play-season-bar--tight': !showRangeBar }"
                >
                  <div class="play-season-tabs">
                    <button
                      v-for="season in episodeSeasonOptions"
                      :key="season.season"
                      type="button"
                      class="play-season-btn"
                      :class="{ 'play-season-btn--active': season.season === currentEpisodeSeasonNumber }"
                      @click.prevent="selectEpisodeSeason(season.season)"
                    >{{ season.label }}</button>
                  </div>
                </div>
                <div
                  v-if="showRangeBar"
                  class="play-range-bar"
                  :class="{ 'play-range-bar--tight': !showSeasonBar }"
                >
                  <div class="play-range-tabs">
                    <button
                      v-for="range in episodeRangeOptions"
                      :key="range.start"
                      type="button"
                      class="play-range-btn"
                      :class="{ 'play-range-btn--active': range.start === currentEpisodeRangeStart }"
                      @click.prevent="selectEpisodeRange(range.start)"
                    >{{ range.label }}</button>
                  </div>
                </div>
                <div class="play-episode-content">
                  <div v-if="showSeasonBar || showRangeBar" class="play-episode-divider"></div>
                  <div v-if="episodeContentState === 'loading'" class="play-episode-loading ui-loading-state">
                    <div class="ui-loading-state__spinner" aria-hidden="true"></div>
                    <div class="ui-loading-state__text">加载中...</div>
                  </div>
                  <div v-else-if="episodeContentState === 'raw'" class="play-raw-list">
                    <div v-if="rawListDisplayPath" class="play-raw-list__path">
                      <span class="play-raw-list__pathText" :title="rawListDisplayPath">{{ rawListDisplayPath }}</span>
                      <button
                        v-if="canGoRawDirBack"
                        type="button"
                        class="ui-control-btn play-raw-list__pathBtn"
                        @click.prevent="goRawDirBack"
                      >返回</button>
                    </div>
                    <div v-if="rawListItems.length" class="play-raw-list__items">
                      <button
                      v-for="item in rawListItems"
                      :key="item.key"
                      type="button"
                      class="play-raw-list__row"
                      :class="{
                          'play-raw-list__row--dir': item.kind === 'dir',
                          'play-raw-list__row--active': item.kind === 'file' && isCurrentRawListPlaybackSelection(item),
                          'is-current': item.kind === 'file' && !isRawListFileInteractive(item),
                        }"
                        :aria-disabled="item.kind === 'file' && !isRawListFileInteractive(item) ? 'true' : null"
                        @click.prevent="onRawListItemClick(item)"
                      >
                        <span class="play-raw-list__text">{{ item.text }}</span>
                      </button>
                    </div>
                    <div v-else class="play-episode-overlay">
                      <div class="play-episode-overlay__inner">
                        <div class="play-raw-list__empty">{{ rawListEmptyText }}</div>
                      </div>
                    </div>
                  </div>
                  <div v-else-if="episodeContentState === 'status'" class="play-episode-overlay">
                    <div class="play-episode-overlay__inner">
                      <div class="play-raw-list__empty">
                    {{ episodeOverlayText }}
                      </div>
                    </div>
                  </div>
                  <div v-else-if="episodeContentState === 'movie-candidates'" class="play-raw-list">
                    <div v-if="tmdbMovieCandidateItems.length" class="play-raw-list__items">
                      <button
                        v-for="item in tmdbMovieCandidateItems"
                        :key="item.key"
                        type="button"
                        class="play-raw-list__row"
                        :class="{
                          'play-raw-list__row--active': isCurrentTmdbMovieCandidateSelection(item),
                          'is-current': !isTmdbMovieCandidateInteractive(item),
                        }"
                        :aria-disabled="!isTmdbMovieCandidateInteractive(item) ? 'true' : null"
                        @click.prevent="onTmdbMovieCandidateClick(item)"
                      >
                        <span class="play-raw-list__text">{{ item.displayText }}</span>
                      </button>
                    </div>
                    <div v-else class="play-episode-overlay">
                      <div class="play-episode-overlay__inner">
                        <div class="play-raw-list__empty">暂无数据</div>
                      </div>
                    </div>
                  </div>
                  <div v-else class="play-episode-grid">
                    <button
                      v-for="episode in episodeButtons"
                      :key="episode.key"
                      type="button"
                      class="play-episode-btn"
                      :class="{
                        'play-episode-btn--active': isEpisodeButtonActive(episode),
                        'is-current': !isEpisodeItemInteractive(episode),
                      }"
                      :aria-disabled="!isEpisodeItemInteractive(episode) ? 'true' : null"
                      :title="selectedSiteResultItem ? (episode.tooltip || '') : null"
                      @click.prevent="onEpisodeItemClick(episode)"
                    >
                      <span v-if="episode.is4k" class="play-episode-btn__badge">4K</span>
                      <span class="play-episode-btn__text">{{ episode.text }}</span>
                    </button>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div class="play-detail">
        <div class="play-detail__inner">
          <div class="play-detail__poster">
            <div class="play-detail__posterWrap">
              <img v-if="detailPoster" :src="detailPosterDisplay" :alt="displayTitle" class="play-detail__posterImg">
              <div v-else class="play-detail__posterSkeleton"></div>
            </div>
          </div>
          <div class="play-detail__info">
            <div class="play-detail__titleRow">
              <h1 class="play-detail__title">
                <span class="play-detail__titleText">{{ displayTitle }}</span>
              </h1>
              <button type="button" class="play-detail__favBtn" aria-label="收藏">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  viewBox="0 0 24 24"
                  class="play-detail__favIcon"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.6"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  aria-hidden="true"
                >
                  <path d="M21 8.25c0-2.485-2.099-4.5-4.688-4.5-1.935 0-3.597 1.126-4.312 2.733-.715-1.607-2.377-2.733-4.313-2.733C5.1 3.75 3 5.765 3 8.25c0 7.22 9 12 9 12s9-4.78 9-12Z" />
                </svg>
              </button>
            </div>
            <div class="play-detail__meta">
              <span
                v-for="tag in detailMetaTags"
                :key="tag"
                class="play-pill"
              >{{ tag }}</span>
            </div>
            <div class="play-detail__desc" style="white-space: pre-line;">
              {{ detailDescription }}
            </div>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>

<script>
import ArtPlayer from '../../shared/ArtPlayer.vue';
import { fetchBootstrap } from '../../shared/bootstrap';
import { extractRawNamesFromEpisodeUrl, fetchCatResolvedDetailCached, requestCatPlay } from '../../shared/catpawrunner';
import {
  playbackSessionState,
  buildPlayerControlUiState,
  buildSmartPlaybackActionConstraint,
  comparePlaybackCandidatesForAction,
  comparePlaybackCandidatesByDefaultRules,
  isPlaybackCandidateAllowedByAction,
  patchLastBrowsePlaybackContext,
  patchCurrentPlaybackContext,
  clearCurrentPlaybackContext,
  executeResolvedSitePlayback,
  executeProxyRetryPlayback,
  executeM3U8RelayRetryPlayback,
  hasNonEmptyHeaders,
  doesQualityMatchResolution,
  inferQualityFromResolution,
  normalizeGoProxyServers,
  resolveCandidateQualityModeKeyForPlayback,
  resolveCandidatePanFamilyForPlayback,
  resolvePanFamilyLabelForUi,
} from '../../shared/playbackRuntime';
import {
  findPlayHistoryRowForContext,
  ensurePlayHistoryRowForContext,
  buildPlayHistoryPayload,
  clearActivePlayHistoryContext,
  flushHistoryProgressBestEffort,
  confirmPlayerHistoryPlaybackReady,
  onPlayerHistoryPlaybackStart,
  onPlayerHistoryTimeUpdate,
  playHistorySessionState,
  preparePlayHistoryContext,
  setPlayHistoryPreOrder,
  syncHistoryProgressIfPossible,
  warmPlayHistoryForContext,
} from '../../shared/playHistoryRuntime';
import { apiGetJson, buildQuery } from '../../shared/apiClient';
import {
  ensureSearchSessionConfig,
  getSearchSessionLaneSnapshot,
  getSearchSessionLaneStatus,
  subscribeSearchSessionLane,
  performSearchSessionSearch,
} from '../../shared/searchSession';
import { rewriteDisplayPosterUrl } from '../../shared/posterUrl';
import {
  addSmartMatchBlockItem,
  buildSearchGroupKey,
  buildSmartMatchBlockKeyword,
  clearBlockedMatchCaches,
  fetchBlockedMatchIndex,
  stripSearchAliasMarkers,
} from '../../shared/searchRuntime';
import { runSmartPlaybackController } from '../../shared/smartPlaybackController';
import {
  buildSmartEpisodeMapping,
  clipSeasonRowsToTotalEpisodes,
  doubanGlobalEpisodeNoOf,
  doubanSeasonEpisodeOfGlobal,
  tmdbGlobalEpisodeNoOf,
  tmdbSeasonEpisodeOfGlobal,
} from '../../shared/smartEpisodeMapping';
import { buildDirectSiteEpisodeItems, detectPlaybackSiteContentKind } from '../../shared/smartSourceRecognition';
import {
  buildPanSourcesFromDetail as buildPanSourcesFromDetailRuntime,
  buildSiteSourceResultItemsFromSnapshot as buildSiteSourceResultItemsFromSnapshotRuntime,
  buildPanSegment as buildPanSegmentRuntime,
  buildHistorySitePlaybackItem as buildHistorySitePlaybackItemRuntime,
  cacheRecognitionForSiteResult as cacheRecognitionForSiteResultRuntime,
  collectRecognitionCandidatesForTarget as collectRecognitionCandidatesForTargetRuntime,
  ensureSiteResultDetailCached as ensureSiteResultDetailCachedRuntime,
  getRecognitionCandidatesForSiteResult as getRecognitionCandidatesForSiteResultRuntime,
  getSiteResultDetail as getSiteResultDetailRuntime,
  getSiteResultRecognitionBySignature as getSiteResultRecognitionBySignatureRuntime,
  resolveHistoryBootstrapPlaybackTarget as resolveHistoryBootstrapPlaybackTargetRuntime,
  resolveCachedPlaybackTarget as resolveCachedPlaybackTargetRuntime,
  setSiteResultDetailCacheEntry as setSiteResultDetailCacheEntryRuntime,
} from '../../shared/smartPlaybackRuntime';
import { tryManualSmartBootstrapRuntime } from '../../shared/smartManualBootstrapRuntime';
import { fetchTMDBDetailCached } from '../../shared/tmdbRuntime';
import { fetchTVMetaCached, normalizeTVMetaPayload } from '../../shared/tvMetaRuntime';
import {
  buildTMDBDetailTextBadge,
  getTMDBBackdropPath,
  getTMDBDetailTitle,
  getTMDBLastEpisodeToAir,
  getTMDBNextEpisodeToAir,
  getTMDBOrdinarySeasons,
  getTMDBPosterPath,
  getTMDBStatus,
  getTMDBYear,
} from '../../shared/tmdbRaw';
import { normalizeInt, normalizeString } from '../../shared/normalize';
import { readPlayLocalStorage, writePlayLocalStorage } from '../../shared/playStorage';
import { scoreEpisodeDisplayName } from '../../utils/matchCore';

const THIRD_PARTY_PLAYERS = [
  { icon: 'iina', name: 'IINA', scheme: 'iina://weblink?url=$edurl' },
  { icon: 'potplayer', name: 'PotPlayer', scheme: 'potplayer://$durl' },
  { icon: 'vlc', name: 'VLC', scheme: 'vlc://$durl' },
  { icon: 'nplayer', name: 'nPlayer', scheme: 'nplayer-$durl' },
  { icon: 'omniplayer', name: 'OmniPlayer', scheme: 'omniplayer://weblink?url=$edurl' },
  { icon: 'infuse', name: 'Infuse', scheme: 'infuse://x-callback-url/play?url=$edurl' },
  { icon: 'mxplayer', name: 'MX Player', scheme: 'intent:$durl#Intent;package=com.mxtech.videoplayer.ad;S.title=$name;end' },
  { icon: 'mxplayer-pro', name: 'MX Player Pro', scheme: 'intent:$durl#Intent;package=com.mxtech.videoplayer.pro;S.title=$name;end' },
  { icon: 'iPlay', name: 'iPlay', scheme: 'iplay://play/any?type=url&url=$bdurl' },
  { icon: 'mpv', name: 'mpv', scheme: 'mpv://$edurl' },
];
const PLAY_SEARCH_SCOPE = 'play-site-only';
const TMDB_MOVIE_RECOGNITION_SIGNATURE = 'tmdb-movie';

const CIRCLED_DIGITS = ['①', '②', '③', '④', '⑤', '⑥', '⑦', '⑧', '⑨', '⑩', '⑪', '⑫', '⑬', '⑭', '⑮', '⑯', '⑰', '⑱', '⑲', '⑳'];
const formatCircledIndex = (index) => CIRCLED_DIGITS[index] || `(${index + 1})`;
const withSiteSourceDisplayLabels = (items) => {
  const list = Array.isArray(items) ? items.filter(Boolean) : [];
  const titleCounts = new Map();
  list.forEach((item) => {
    const duplicateKey = `${normalizeString(item && item.siteKey)}::${normalizeString(item && item.title)}`;
    titleCounts.set(duplicateKey, (titleCounts.get(duplicateKey) || 0) + 1);
  });
  const titleIndexes = new Map();
  return list.map((item) => {
    const duplicateKey = `${normalizeString(item && item.siteKey)}::${normalizeString(item && item.title)}`;
    const nextIndex = titleIndexes.has(duplicateKey) ? titleIndexes.get(duplicateKey) + 1 : 0;
    titleIndexes.set(duplicateKey, nextIndex);
    const duplicateCount = titleCounts.get(duplicateKey) || 0;
    return {
      ...(item || {}),
      displayLabel: `${normalizeString(item && item.siteName)}-${normalizeString(item && item.title)}${duplicateCount > 1 ? ` ${formatCircledIndex(nextIndex)}` : ''}`,
    };
  });
};

const stripMarkupText = (value) =>
  String(value || '')
    .replace(/\[a=[^\]]*\/\]([\s\S]*?)\[\/a\]/gi, '$1')
    .replace(/<[^>]+>/g, '')
    .replace(/\s+/g, ' ')
    .trim();
const normalizeSearchKey = (value) => normalizeString(value);
const SEASON_CN_NUMERALS = ['零', '一', '二', '三', '四', '五', '六', '七', '八', '九', '十'];
const formatChineseNumber = (value) => {
  const num = normalizeInt(value);
  if (num <= 10) return SEASON_CN_NUMERALS[num] || String(num);
  if (num < 20) return `十${SEASON_CN_NUMERALS[num - 10] || ''}`;
  if (num % 10 === 0 && num < 100) return `${SEASON_CN_NUMERALS[Math.floor(num / 10)] || String(Math.floor(num / 10))}十`;
  if (num < 100) {
    const tens = Math.floor(num / 10);
    const ones = num % 10;
    return `${SEASON_CN_NUMERALS[tens] || String(tens)}十${SEASON_CN_NUMERALS[ones] || String(ones)}`;
  }
  return String(num);
};

const getTodayDateText = () => {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
};

const TMDB_GENRE_LABELS = {
  12: '冒险',
  14: '奇幻',
  16: '动画',
  18: '剧情',
  27: '恐怖',
  28: '动作',
  35: '喜剧',
  36: '历史',
  37: '西部',
  53: '惊悚',
  80: '犯罪',
  99: '纪录',
  878: '科幻',
  9648: '悬疑',
  10402: '音乐',
  10749: '爱情',
  10751: '家庭',
  10752: '战争',
  10759: '动作冒险',
  10762: '儿童',
  10763: '新闻',
  10764: '真人秀',
  10765: '科幻奇幻',
  10766: '肥皂剧',
  10767: '脱口秀',
  10768: '战争政治',
  10770: '电视电影',
};

const hasChineseText = (value) => /[\u3400-\u9fff]/.test(normalizeString(value));

const buildTMDBRenderedSeasonRows = (detail, { includeUnaired = false } = {}) => {
  const seasons = getTMDBOrdinarySeasons(detail);
  if (!seasons.length) return [];
  if (includeUnaired) return seasons;
  const status = getTMDBStatus(detail);
  if (status === 'Ended') return seasons;

  const next = getTMDBNextEpisodeToAir(detail);
  const nextSeason = normalizeInt(next && next.seasonNumber);
  const nextEpisode = normalizeInt(next && next.episodeNumber);
  const airDate = normalizeString(next && next.airDate);
  if (nextSeason <= 0 || nextEpisode <= 0) {
    return seasons;
  }

  const today = getTodayDateText();
  const currentEpisode = airDate && airDate <= today ? nextEpisode : nextEpisode - 1;
  const rendered = [];
  for (let i = 0; i < seasons.length; i += 1) {
    const row = seasons[i];
    if (!row || row.season <= 0 || row.episodes <= 0) continue;
    if (row.season < nextSeason) {
      rendered.push(row);
      continue;
    }
    if (row.season === nextSeason) {
      const episodes = Math.min(row.episodes, Math.max(0, currentEpisode));
      if (episodes > 0) {
        rendered.push({
          season: row.season,
          episodes,
        });
      }
    }
    break;
  }
  if (rendered.length) return rendered;
  return seasons;
};

const normalizeEpisodeDisplaySeasonRows = (rows, { seasonKeys = [], episodeKeys = [] } = {}) => (
  (Array.isArray(rows) ? rows : [])
    .map((item) => ({
      season: seasonKeys.reduce((out, key) => (out > 0 ? out : normalizeInt(item && item[key])), 0),
      episodes: episodeKeys.reduce((out, key) => (out > 0 ? out : normalizeInt(item && item[key])), 0),
    }))
    .filter((item) => item.season > 0 && item.episodes > 0)
    .sort((left, right) => left.season - right.season)
);

const buildDoubanRenderedSeasonRows = (meta) => normalizeEpisodeDisplaySeasonRows(
  meta && meta.doubanSeasons,
  {
    seasonKeys: ['season'],
    episodeKeys: ['episodeCount'],
  },
);

const countRenderedEpisodes = (rows) => (
  (Array.isArray(rows) ? rows : []).reduce(
    (sum, item) => sum + Math.max(0, normalizeInt(item && item.episodes)),
    0,
  )
);

const extractYearTextFromTMDBDetail = (detail) => {
  const year = getTMDBYear(detail);
  return year > 0 ? String(year) : '';
};

const splitRawPathSegments = (value) =>
  normalizeString(value)
    .replace(/\\/g, '/')
    .split('/')
    .map(normalizeString)
    .filter(Boolean);

const getRawFileName = (value) => {
  const parts = splitRawPathSegments(value);
  return parts.length ? parts[parts.length - 1] : normalizeString(value);
};

const getRawDirPath = (value) => {
  const parts = splitRawPathSegments(value);
  if (parts.length <= 1) return '';
  return `/${parts.slice(0, -1).join('/')}`;
};

const stripDisplayMetaPrefix = (value) => {
  let rest = normalizeString(value);
  while (rest.startsWith('@')) {
    const match = rest.match(/^@([^@/\\]+)/);
    if (!match || !match[0]) break;
    rest = rest.slice(match[0].length).trim();
  }
  return rest;
};

const parseStatsDisplayPath = (displayName) => {
  const stripped = stripDisplayMetaPrefix(displayName);
  if (!stripped.startsWith('/')) {
    return {
      isPathLike: false,
      fullPath: '',
      dirPath: '',
      baseName: '',
    };
  }
  const parts = splitRawPathSegments(stripped);
  const fullPath = parts.length ? `/${parts.join('/')}` : '';
  const dirPath = parts.length > 1 ? `/${parts.slice(0, -1).join('/')}` : '';
  const baseName = parts.length ? parts[parts.length - 1] : '';
  return {
    isPathLike: !!fullPath,
    fullPath,
    dirPath,
    baseName,
  };
};

const pickStatsFileName = (displayName, rawName, { preferFile = false, allFileNamesSame = false, titleLower = '' } = {}) => {
  const fileName = getRawFileName(rawName);
  const label = normalizeString(displayName);
  if (preferFile && fileName) return fileName;
  if (allFileNamesSame && label) return label;
  if (!label) return fileName;
  if (!fileName) return label;
  const displayScore = scoreEpisodeDisplayName(label, titleLower);
  const fileScore = scoreEpisodeDisplayName(fileName, titleLower);
  return displayScore >= fileScore ? label : fileName;
};

const resolvePlayerStatsPathName = ({ segment, candidate } = {}) => {
  const seg = segment && typeof segment === 'object' ? segment : null;
  const cand = candidate && typeof candidate === 'object' ? candidate : null;
  const parsed = parseStatsDisplayPath(seg && seg.displayName);
  if (parsed.isPathLike) return parsed.dirPath || parsed.fullPath;
  return normalizeString(cand && cand.pathName) || normalizeString(seg && seg.pathName);
};

const buildStatsNamingContext = (panEntry, segments, titleLower) => {
  const entry = panEntry && typeof panEntry === 'object' ? panEntry : null;
  const list = Array.isArray(segments) ? segments : [];
  let allFileNamesSame = true;
  let firstFileName = null;
  list.forEach((segment) => {
    const raw = normalizeString(segment);
    if (!raw) return;
    const dollarIdx = raw.indexOf('$');
    const episodeUrl = dollarIdx >= 0 ? normalizeString(raw.slice(dollarIdx + 1)) : raw;
    const fileName = extractRawNamesFromEpisodeUrl(episodeUrl)[0] || '';
    if (firstFileName == null) {
      firstFileName = fileName;
    } else if (fileName !== firstFileName) {
      allFileNamesSame = false;
    }
  });
  return {
    preferFile: !!normalizeString(entry && entry.provider),
    allFileNamesSame,
    titleLower: normalizeString(titleLower).toLowerCase(),
  };
};

const pickRawFileNameForStats = (displayName, rawName, namingContext = {}) => {
  const ctx = namingContext && typeof namingContext === 'object' ? namingContext : {};
  if (ctx.preferFile) {
    return getRawFileName(rawName) || parseStatsDisplayPath(displayName).baseName || '';
  }
  return pickStatsFileName(displayName, rawName, ctx);
};

const buildSegmentPlaybackIdentity = (segment) => {
  const seg = segment && typeof segment === 'object' ? segment : null;
  return normalizeString(seg && seg.segmentIdentity);
};

const SWITCH_CROSS_SITE_PROVIDERS = new Set(['quark', 'uc', 'baidu', '139', '189']);

const getPanEntrySegments = (entry) => (
  Array.isArray(entry && entry.episodeSegments)
    ? entry.episodeSegments.map(normalizeString).filter(Boolean)
    : []
);

const buildEpisodeMappingSignature = (mapping) => {
  const target = mapping && typeof mapping === 'object' ? mapping : null;
  if (!target) return '';
  const total = normalizeInt(target.totalEpisodes);
  const tmdb = Array.isArray(target.tmdbSeasons)
    ? target.tmdbSeasons.map((item) => `${normalizeInt(item && item.season)}:${normalizeInt(item && item.episodeCount)}`).join('|')
    : '';
  const douban = Array.isArray(target.doubanSeasons)
    ? target.doubanSeasons.map((item) => `${normalizeInt(item && item.season)}:${normalizeInt(item && item.episodeCount)}`).join('|')
    : '';
  return `${total}::${tmdb}::${douban}`;
};

const isElementFullyVisibleInContainer = (element, container) => {
  if (!element || !container) return true;
  const elRect = element.getBoundingClientRect();
  const containerRect = container.getBoundingClientRect();
  const padding = 4;
  return elRect.top >= containerRect.top + padding
    && elRect.bottom <= containerRect.bottom - padding;
};

const scrollElementIntoContainerCenter = (element, container) => {
  if (!element || !container) return;
  const elRect = element.getBoundingClientRect();
  const containerRect = container.getBoundingClientRect();
  const relativeTop = (elRect.top - containerRect.top) + container.scrollTop;
  const nextTop = relativeTop - (container.clientHeight / 2) + (elRect.height / 2);
  container.scrollTop = Math.max(0, Math.round(nextTop));
};

const ensureElementVisibleInContainer = (element, container) => {
  if (!element || !container) return;
  if (isElementFullyVisibleInContainer(element, container)) return;
  scrollElementIntoContainerCenter(element, container);
};

const findScrollableEpisodeContainer = (element, root) => {
  let current = element && element.parentElement ? element.parentElement : null;
  const boundary = root || null;
  while (current) {
    if (
      current.classList
      && (
        current.classList.contains('play-episode-grid')
        || current.classList.contains('play-raw-list__items')
      )
    ) {
      return current;
    }
    if (boundary && current === boundary) break;
    current = current.parentElement;
  }
  return null;
};

const buildPlaybackSiteLabel = (item) => {
  const target = item && typeof item === 'object' ? item : null;
  const displayLabel = normalizeString(target && target.displayLabel);
  if (displayLabel) return displayLabel;
  const siteName = normalizeString(target && target.siteName);
  const title = normalizeString(target && target.title);
  if (siteName && title) return `${siteName}-${title}`;
  return siteName || title;
};

const buildProjectedEpisodeTooltip = (candidate) => {
  const rawName = normalizeString(candidate && candidate.rawName);
  const fileName = normalizeString(candidate && candidate.fileName) || getRawFileName(rawName);
  const dirPath = getRawDirPath(rawName);
  if (dirPath && fileName) return `${dirPath}\n${fileName}`;
  if (fileName) return fileName;
  return dirPath;
};

const buildSiteEpisodeSelectionKey = (panKey, itemIndex) => {
  const key = normalizeString(panKey);
  const index = normalizeInt(itemIndex);
  if (!key || index < 0) return '';
  return `${key}::${index}`;
};

const convertThirdPartyUrl = (scheme, args) => {
  let url = String(scheme || '');
  url = url.replace('$name', args.name || '');
  url = url.replace(/\$[eb_]*url/g, (placeholder) => {
    const ops = placeholder.match(/[eb]/g);
    let next = String(args.raw_url || '');
    if (ops) {
      for (const op of ops.reverse()) {
        if (op === 'e') next = encodeURIComponent(next);
        else if (op === 'b') next = window.btoa(next);
      }
    }
    return next;
  });
  url = url.replace(/\$[eb_]*durl/g, (placeholder) => {
    const ops = placeholder.match(/[eb]/g);
    let next = String(args.d_url || '');
    if (ops) {
      for (const op of ops.reverse()) {
        if (op === 'e') next = encodeURIComponent(next);
        else if (op === 'b') next = window.btoa(next);
      }
    }
    return next;
  });
  return url;
};

const buildProjectedSiteEpisodeItems = (recognitionData, sourceKind) => {
  const data = recognitionData && typeof recognitionData === 'object' ? recognitionData : null;
  const sourceKey = normalizeString(data && data.source && data.source.key);
  const mode = normalizeString(sourceKind) === '豆瓣' ? 'douban' : 'tmdb';
  return (Array.isArray(data && data.items) ? data.items : [])
    .map((item) => {
      const mapping = item && item.mapping && typeof item.mapping === 'object'
        ? (mode === 'douban' ? item.mapping.douban : item.mapping.tmdb)
        : null;
      const no = normalizeInt(mapping && mapping.episode);
      if (no <= 0) return null;
      return {
        key: normalizeString(item && item.key) || `${mode}:${normalizeInt(mapping && mapping.season)}:${no}:${normalizeInt(item?.mapping?.global)}`,
        season: Math.max(1, normalizeInt(mapping && mapping.season) || 1),
        no,
        text: String(no),
        tooltip: buildProjectedEpisodeTooltip(item),
        is4k: normalizeString(item && item.quality) === '4K',
        selectionKey: buildSiteEpisodeSelectionKey(sourceKey, item && item.itemIndex),
        global: normalizeInt(item && item.mapping && item.mapping.global),
        itemIndex: normalizeInt(item && item.itemIndex),
        candidate: item,
      };
    })
    .filter(Boolean)
    .sort((left, right) => {
      const leftGlobal = normalizeInt(left && left.global);
      const rightGlobal = normalizeInt(right && right.global);
      if (leftGlobal > 0 && rightGlobal > 0 && leftGlobal !== rightGlobal) return leftGlobal - rightGlobal;
      const leftSeason = normalizeInt(left && left.season);
      const rightSeason = normalizeInt(right && right.season);
      if (leftSeason !== rightSeason) return leftSeason - rightSeason;
      const leftNo = normalizeInt(left && left.no);
      const rightNo = normalizeInt(right && right.no);
      if (leftNo !== rightNo) return leftNo - rightNo;
      return normalizeInt(left && left.itemIndex) - normalizeInt(right && right.itemIndex);
    });
};

const MOVIE_QUALITY_PRIORITY = {
  '4k_hdr': 1,
  '4k_fps': 2,
  '4k': 3,
  '1080p': 4,
  '720p': 5,
  'unknown': 6,
};

const resolveMovieCandidateQualityKey = (item) => {
  return resolveCandidateQualityModeKeyForPlayback(item) || 'unknown';
};

const formatMovieCandidateQualityLabel = (qualityKey) => {
  const key = normalizeString(qualityKey).toLowerCase();
  if (key === '4k_hdr') return '4K HDR';
  if (key === '4k_fps') return '4K 60帧';
  if (key === '4k') return '4K';
  if (key === '1080p') return '1080P';
  if (key === '720p') return '720P';
  return '未知';
};

const buildTmdbMovieCandidateItems = ({
  siteItems,
  detailStore,
  recognitionStore,
  signature,
  runtimeConfig,
} = {}) => {
  const list = Array.isArray(siteItems) ? siteItems : [];
  const store = detailStore && typeof detailStore === 'object' ? detailStore : {};
  const recognitionRoot = recognitionStore && typeof recognitionStore === 'object' ? recognitionStore : {};
  const signatureKey = normalizeString(signature);
  const siteOrderMap = runtimeConfig && runtimeConfig.siteOrderMap instanceof Map ? runtimeConfig.siteOrderMap : new Map();
  const grouped = new Map();

  list.forEach((siteItem, siteSeq) => {
    const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
    const itemId = normalizeString(item && item.id);
    if (!item || !itemId || !signatureKey) return;
    const detailEntry = store[itemId] && typeof store[itemId] === 'object' ? store[itemId] : null;
    const detail = detailEntry && detailEntry.detail && typeof detailEntry.detail === 'object' ? detailEntry.detail : null;
    const bySignature = getSiteResultRecognitionBySignatureRuntime(recognitionRoot, item, signatureKey);
    if (!detail || !bySignature) return;
    const panSources = buildPanSourcesFromDetailRuntime(detail);
    const siteKey = normalizeString(item.siteKey);
    const groupKey = siteKey || `site:${siteSeq}`;
    const siteRows = grouped.get(groupKey) || [];

    panSources.forEach((panEntry, panIndex) => {
      const pan = panEntry && typeof panEntry === 'object' ? panEntry : null;
      const panKey = normalizeString(pan && pan.key);
      if (!pan || !panKey) return;
      const recognitionData = bySignature[panKey] && typeof bySignature[panKey] === 'object' ? bySignature[panKey] : null;
      if (!recognitionData) return;
      const items = Array.isArray(recognitionData.items) ? recognitionData.items : [];
      items.forEach((candidate, candidateIndex) => {
        if (normalizeString(candidate && candidate.matchKind) !== 'movie' || !(candidate && candidate.movieMatched)) return;
        const itemIndex = normalizeInt(candidate && candidate.itemIndex);
        if (itemIndex < 0) return;
        const qualityKey = resolveMovieCandidateQualityKey(candidate);
        const qualityLabel = formatMovieCandidateQualityLabel(qualityKey);
        const selectionKey = buildSiteEpisodeSelectionKey(panKey, itemIndex);
        siteRows.push({
          key: `${normalizeString(item.siteKey)}::${normalizeString(item.siteDetail)}::${panKey}::${itemIndex}`,
          siteKey: normalizeString(item.siteKey),
          siteDetail: normalizeString(item.siteDetail),
          siteName: normalizeString(item.siteName) || normalizeString(item.siteLabel) || '站点',
          panKey,
          panFlag: normalizeString(pan && pan.label),
          itemIndex,
          selectionKey,
          fileIdentity: normalizeString(candidate && candidate.segmentIdentity),
          qualityKey,
          qualityLabel,
          displayText: '',
          suffix: '',
          candidate,
          siteItem: item,
          panEntry: pan,
          segment: buildPanSegmentRuntime(pan, itemIndex),
          siteOrder: siteOrderMap.has(siteKey) ? siteOrderMap.get(siteKey) : 999999,
          panOrder: panIndex,
          candidateOrder: candidateIndex,
        });
      });
    });

    if (siteRows.length) grouped.set(groupKey, siteRows);
  });

  const output = [];
  Array.from(grouped.values())
    .sort((left, right) => {
      const leftOrder = normalizeInt(left[0] && left[0].siteOrder) || 999999;
      const rightOrder = normalizeInt(right[0] && right[0].siteOrder) || 999999;
      if (leftOrder !== rightOrder) return leftOrder - rightOrder;
      return normalizeString(left[0] && left[0].siteName).localeCompare(normalizeString(right[0] && right[0].siteName), 'zh');
    })
    .forEach((rows) => {
      const sorted = rows.slice().sort((left, right) => {
        const leftQuality = MOVIE_QUALITY_PRIORITY[normalizeString(left && left.qualityKey).toLowerCase()] || 99;
        const rightQuality = MOVIE_QUALITY_PRIORITY[normalizeString(right && right.qualityKey).toLowerCase()] || 99;
        if (leftQuality !== rightQuality) return leftQuality - rightQuality;
        const leftPanOrder = normalizeInt(left && left.panOrder);
        const rightPanOrder = normalizeInt(right && right.panOrder);
        if (leftPanOrder !== rightPanOrder) return leftPanOrder - rightPanOrder;
        const leftIndex = normalizeInt(left && left.itemIndex);
        const rightIndex = normalizeInt(right && right.itemIndex);
        if (leftIndex !== rightIndex) return leftIndex - rightIndex;
        return normalizeInt(left && left.candidateOrder) - normalizeInt(right && right.candidateOrder);
      });
      const duplicateCounts = new Map();
      sorted.forEach((row) => {
        const duplicateKey = `${normalizeString(row && row.siteKey)}::${normalizeString(row && row.qualityKey)}`;
        duplicateCounts.set(duplicateKey, (duplicateCounts.get(duplicateKey) || 0) + 1);
      });
      const duplicateIndexes = new Map();
      sorted.forEach((row) => {
        const duplicateKey = `${normalizeString(row && row.siteKey)}::${normalizeString(row && row.qualityKey)}`;
        const nextIndex = (duplicateIndexes.get(duplicateKey) || 0) + 1;
        duplicateIndexes.set(duplicateKey, nextIndex);
        const count = duplicateCounts.get(duplicateKey) || 0;
        const suffix = count > 1 && nextIndex > 1 ? ` ${nextIndex}` : '';
        output.push({
          ...row,
          suffix,
          displayText: `${normalizeString(row && row.qualityLabel) || '未知'} - ${normalizeString(row && row.siteName) || '站点'}${suffix}`,
        });
      });
    });

  return output;
};

const EP_VIEW_MODE_STORAGE_KEY = 'meowfilm_episode_view_mode';

export default {
  name: 'PlayPage',
  components: { ArtPlayer },
  emits: ['back'],
  props: {
    bootstrap: { type: Object, default: () => ({}) },
    isTmdbMode: { type: Boolean, default: false },
    contentKey: { type: String, default: '' },
    searchQueryOriginal: { type: String, default: '' },
    videoYear: { type: String, default: '' },
    searchType: { type: String, default: '' },
    siteKey: { type: String, default: '' },
    siteName: { type: String, default: '' },
    spiderApi: { type: String, default: '' },
    siteDetail: { type: String, default: '' },
    tmdbId: { type: Number, default: 0 },
    tmdbType: { type: String, default: '' },
    videoIntro: { type: String, default: '' },
    Poster: { type: String, default: '' },
    Remark: { type: String, default: '' },
    switchOnlyToken: { type: Number, default: 0 },
  },
  computed: {
    episodeButtonWidth() {
      return 44;
    },
    episodeButtonGap() {
      return 12;
    },
    episodeGridSidePadding() {
      return 4;
    },
    episodeSelectorSidePadding() {
      return 32;
    },
    episodeSelectorDesktopOffset() {
      return 8;
    },
    episodeSelectorBorderWidth() {
      return 2;
    },
    defaultEpisodePanelWidth() {
      return this.getEpisodePanelWidthForColumns(5);
    },
    minEpisodePanelWidth() {
      return this.defaultEpisodePanelWidth;
    },
    maxEpisodePanelWidth() {
      return this.getEpisodePanelWidthForColumns(8);
    },
    panSourceMenuStyle() {
      if (!this.siteSourceSelectWidth) return null;
      return { width: `${this.siteSourceSelectWidth}px` };
    },
    isPortraitMode() {
      return !!this.portraitMode;
    },
    portraitModeToggleLabelText() {
      return this.isPortraitMode ? '退出竖屏' : '竖屏';
    },
    portraitModeToggleTitleText() {
      return this.isPortraitMode ? '退出竖屏播放模式' : '进入竖屏播放模式';
    },
    isEpisodePanelCollapsed() {
      if (this.isPortraitMode) return true;
      return this.episodePanelHidden && this.viewportWidth >= 1024;
    },
    episodePanelToggleLabelText() {
      return this.isEpisodePanelCollapsed ? '显示' : '隐藏';
    },
    episodePanelToggleTitleText() {
      return `${this.episodePanelToggleLabelText}选集换源面板`;
    },
    episodePanelStyle() {
      if (this.playerAreaHeight <= 0 || this.viewportWidth < 768) return null;
      return { height: `${this.playerAreaHeight}px` };
    },
    portraitPlayerAreaStyle() {
      if (!this.isPortraitMode) return null;
      const width = Math.max(0, normalizeInt(this.portraitPlayerAreaWidth));
      if (width <= 0) return null;
      return { width: `${width}px` };
    },
    thirdPartyCollapsedPlayers() {
      return THIRD_PARTY_PLAYERS.slice(0, 3);
    },
    thirdPartyVisiblePlayers() {
      return this.thirdPartyExpanded ? THIRD_PARTY_PLAYERS : this.thirdPartyCollapsedPlayers;
    },
    activeSitePlaybackItem() {
      if (this.selectedSiteResultItem) return this.selectedSiteResultItem;
      if (this.isTmdbMode) return null;
      const spiderApi = normalizeString(this.spiderApi);
      const siteDetail = normalizeString(this.siteDetail);
      if (!spiderApi || !siteDetail) return null;
      return {
        id: `direct:${spiderApi}::${siteDetail}`,
        siteKey: normalizeString(this.siteKey),
        siteName: normalizeString(this.siteName),
        spiderApi,
        siteDetail,
      };
    },
    currentPanSourceOptions() {
      if (!this.activeSitePlaybackItem) return [];
      return this.siteDetailPanSources;
    },
    currentPanSourceLabel() {
      const selectedKey = normalizeString(this.selectedPanSource);
      const current = selectedKey
        ? (this.currentPanSourceOptions.find((item) => item.key === selectedKey) || null)
        : (this.currentPanSourceOptions[0] || null);
      return current ? current.label : '暂无数据';
    },
    showDoubanPrimarySourceOption() {
      if (!this.isTmdbMode || this.tmdbMovieMode) return false;
      if (!this.tmdbBaseSeasonRows.length || !this.doubanBaseSeasonRows.length) return false;
      return this.tmdbBaseSeasonRows.length !== this.doubanBaseSeasonRows.length;
    },
    projectionTMDBSeasonCount() {
      if (!this.isTmdbMode || this.tmdbMovieMode) return 0;
      return (Array.isArray(this.detailDoubanData && this.detailDoubanData.tmdbSeasons)
        ? this.detailDoubanData.tmdbSeasons
        : [])
        .map((item) => normalizeInt(item && item.season))
        .filter((season) => season > 0)
        .length;
    },
    projectionDoubanSeasonCount() {
      if (!this.isTmdbMode || this.tmdbMovieMode) return 0;
      return (Array.isArray(this.detailDoubanData && this.detailDoubanData.doubanSeasons)
        ? this.detailDoubanData.doubanSeasons
        : [])
        .map((item) => normalizeInt(item && item.season))
        .filter((season) => season > 0)
        .length;
    },
    projectionSourceSwitchable() {
      if (!this.selectedSiteResultItem || !this.isTmdbMode || this.tmdbMovieMode) return false;
      const tmdbCount = normalizeInt(this.projectionTMDBSeasonCount);
      const doubanCount = normalizeInt(this.projectionDoubanSeasonCount);
      return tmdbCount !== doubanCount;
    },
    projectionSourceOptions() {
      if (!this.selectedSiteResultItem || !this.isTmdbMode) return [];
      if (!this.projectionSourceSwitchable) return ['TMDB'];
      return ['TMDB', '豆瓣'];
    },
    projectionCurrentSeasonCount() {
      if (!this.selectedSiteResultItem || !this.isTmdbMode || this.tmdbMovieMode) return 0;
      return normalizeString(this.selectedProjectionSource) === '豆瓣'
        ? normalizeInt(this.projectionDoubanSeasonCount)
        : normalizeInt(this.projectionTMDBSeasonCount);
    },
    forceRawListMode() {
      return this.contentKind === 'movie';
    },
    forceDirectSiteRawListMode() {
      if (this.isTmdbMode || this.selectedSiteResultItem || this.contentKind === 'movie') return false;
      const entry = this.currentPanSourceEntry;
      if (!entry || !getPanEntrySegments(entry).length) return false;
      if (this.directSiteEpisodeItems.length) return false;
      return getPanEntrySegments(entry).length > 0;
    },
    effectiveForceRawListMode() {
      return this.forceRawListMode || this.forceDirectSiteRawListMode;
    },
    showTmdbMovieCandidateList() {
      return !this.selectedSiteResultItem && this.isTmdbMode && this.tmdbMovieMode;
    },
    showPanSourceRow() {
      if (!this.activeSitePlaybackItem) return false;
      if (this.selectedSiteResultItem) {
        return !!(this.siteResultDetailData && this.currentPanSourceOptions.length > 0);
      }
      return !this.detailLoading && this.currentPanSourceOptions.length > 0;
    },
    siteResultDetailResolutionIncomplete() {
      if (!this.selectedSiteResultItem) return false;
      if (this.siteResultDetailLoading) return true;
      const detail = this.siteResultDetailData && typeof this.siteResultDetailData === 'object'
        ? this.siteResultDetailData
        : null;
      if (!detail) return false;
      return detail.resolutionComplete !== true;
    },
    siteSourceTriggerLabel() {
      if (this.selectedSiteResultItem) {
        return normalizeString(this.selectedSiteResultItem.displayLabel)
          || normalizeString(this.selectedSiteResultItem.siteName)
          || this.selectedSiteSource;
      }
      return this.selectedSiteSource;
    },
    isTMDBEpisodeSource() {
      return !this.selectedSiteResultItem && this.selectedSiteSource === 'TMDB' && this.isTmdbMode;
    },
    isDoubanEpisodeSource() {
      return !this.selectedSiteResultItem && this.selectedSiteSource === '豆瓣' && this.isTmdbMode;
    },
    showPreOrderButton() {
      if (!this.isTmdbMode || this.tmdbMovieMode || this.selectedSiteResultItem) return false;
      if (!(this.selectedSiteSource === 'TMDB' || this.selectedSiteSource === '豆瓣')) return false;
      const detail = this.detailTMDBData && typeof this.detailTMDBData === 'object' ? this.detailTMDBData : null;
      if (!detail) return false;
      const seasons = getTMDBOrdinarySeasons(detail);
      if (!Array.isArray(seasons) || !seasons.length) return false;
      const latestSeason = seasons[seasons.length - 1] || null;
      const latestSeasonNo = normalizeInt(latestSeason && latestSeason.season);
      const latestSeasonEpisodes = normalizeInt(latestSeason && latestSeason.episodes);
      if (latestSeasonNo <= 0 || latestSeasonEpisodes <= 0) return false;
      const last = getTMDBLastEpisodeToAir(detail);
      const lastSeasonNo = normalizeInt(last && last.seasonNumber);
      const lastEpisodeNo = normalizeInt(last && last.episodeNumber);
      if (lastSeasonNo <= 0) return false;
      if (latestSeasonNo > lastSeasonNo) return true;
      if (latestSeasonNo < lastSeasonNo) return false;
      return latestSeasonEpisodes > Math.max(0, lastEpisodeNo);
    },
    preOrderActive() {
      const active = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
        ? playHistorySessionState.activeContext
        : null;
      if (
        active
        && normalizeString(active.contentKey) === normalizeString(this.playContentPreferenceKey)
        && normalizeString(active.tmdbType).toLowerCase() === normalizeString(this.tmdbType || this.searchType).toLowerCase()
        && normalizeInt(active.tmdbId) === normalizeInt(this.tmdbId)
      ) {
        return !!active.preOrder;
      }
      return !!(this.playHistoryRowForMenu && this.playHistoryRowForMenu.preOrder);
    },
    isProjectedSiteEpisodeSource() {
      if (!this.selectedSiteResultItem || !this.isTmdbMode) return false;
      const source = normalizeString(this.selectedProjectionSource);
      return source === 'TMDB' || source === '豆瓣';
    },
    projectedSiteEpisodeItems() {
      if (!this.selectedSiteResultItem || !this.isProjectedSiteEpisodeSource) return [];
      return buildProjectedSiteEpisodeItems(this.playbackRecognitionData, this.selectedProjectionSource);
    },
    tmdbMovieCandidateItems() {
      if (this.selectedSiteResultItem || !this.isTmdbMode || !this.tmdbMovieMode) return [];
      const siteItems = buildSiteSourceResultItemsFromSnapshotRuntime({
        snapshot: this.playSearchSnapshot,
        runtimeConfig: this.playSearchRuntimeConfig,
        blockedSiteKeys: this.playBlockedSiteKeys,
        blockedMatchIndex: this.playBlockedMatchIndex,
        title: this.displayTitle,
        contentKind: 'movie',
      });
      const historyItem = buildHistorySitePlaybackItemRuntime(this.playHistoryRowForMenu);
      const candidateSiteItems = historyItem ? [historyItem].concat(siteItems) : siteItems;
      return buildTmdbMovieCandidateItems({
        siteItems: candidateSiteItems,
        detailStore: this.siteResultDetailStore,
        recognitionStore: this.siteResultRecognitionStore,
        signature: this.activeRecognitionSignature,
        runtimeConfig: this.playSearchRuntimeConfig,
      });
    },
    projectedSeasonRows() {
      if (this.selectedSiteResultItem) return this.projectedTMDBSeasonRows;
      return [];
    },
    tmdbMovieMode() {
      return normalizeString(this.tmdbType || this.searchType).toLowerCase() === 'movie';
    },
    siteContentKind() {
      if (this.isTmdbMode) return 'unknown';
      return detectPlaybackSiteContentKind({
        panSources: this.siteDetailPanSources,
        runtimeSettings: this.runtimeSettings,
      });
    },
    contentKind() {
      if (this.isTmdbMode) return this.tmdbMovieMode ? 'movie' : 'tv';
      return this.siteContentKind;
    },
    contentKindLabel() {
      if (this.contentKind === 'movie') return '电影';
      if (this.contentKind === 'tv' || this.contentKind === 'series') return '剧集';
      return '';
    },
    playContentPreferenceKey() {
      return normalizeString(this.contentKey);
    },
    directSiteEpisodeCount() {
      if (this.isTmdbMode || this.selectedSiteResultItem || this.contentKind === 'movie') return 0;
      const entry = this.currentPanSourceEntry;
      return getPanEntrySegments(entry).length;
    },
    directSiteEpisodeItems() {
      if (this.isTmdbMode || this.selectedSiteResultItem || this.contentKind === 'movie') return [];
      return buildDirectSiteEpisodeItems(this.currentPanSourceEntry, this.runtimeSettings);
    },
    directSiteSeasonRows() {
      const items = Array.isArray(this.directSiteEpisodeItems) ? this.directSiteEpisodeItems : [];
      if (!items.length) return [];
      const explicitSeasons = Array.from(new Set(
        items.map((item) => normalizeInt(item && item.season)).filter((season) => season > 0),
      )).sort((left, right) => left - right);
      const singleSeasonMode = !explicitSeasons.length || explicitSeasons.every((season) => season === 1);
      if (singleSeasonMode) {
        const maxEpisode = items.reduce((max, item) => Math.max(max, normalizeInt(item && item.no)), 0);
        return maxEpisode > 0 ? [{ season: 1, episodes: maxEpisode }] : [];
      }
      const rows = new Map();
      items.forEach((item) => {
        const season = Math.max(1, normalizeInt(item && item.season));
        const no = normalizeInt(item && item.no);
        if (no <= 0) return;
        rows.set(season, Math.max(rows.get(season) || 0, no));
      });
      return Array.from(rows.entries())
        .map(([season, episodes]) => ({ season, episodes }))
        .sort((left, right) => left.season - right.season);
    },
    tmdbBaseSeasonRows() {
      if (this.tmdbMovieMode) return [];
      return buildTMDBRenderedSeasonRows(this.detailTMDBData, { includeUnaired: this.preOrderActive });
    },
    tmdbSeasonRows() {
      if (!this.isTMDBEpisodeSource || this.tmdbMovieMode) return [];
      return this.tmdbBaseSeasonRows;
    },
    doubanBaseSeasonRows() {
      if (this.tmdbMovieMode) return [];
      const seasons = buildDoubanRenderedSeasonRows(this.detailDoubanData);
      if (this.preOrderActive) {
        return seasons
          .map((item) => ({
            season: normalizeInt(item && item.season),
            episodes: normalizeInt(item && item.episodes),
          }))
          .filter((item) => item.season > 0 && item.episodes > 0)
          .sort((left, right) => left.season - right.season);
      }
      const tmdbEpisodeTotal = countRenderedEpisodes(this.tmdbBaseSeasonRows);
      return clipSeasonRowsToTotalEpisodes(
        seasons.map((item) => ({
          season: normalizeInt(item && item.season),
          episodeCount: normalizeInt(item && item.episodes),
        })),
        tmdbEpisodeTotal,
      )
        .map((item) => ({
          season: normalizeInt(item && item.season),
          episodes: normalizeInt(item && item.episodeCount),
        }))
        .filter((item) => item.season > 0 && item.episodes > 0)
        .sort((left, right) => left.season - right.season);
    },
    doubanSeasonRows() {
      if (!this.isDoubanEpisodeSource) return [];
      return this.doubanBaseSeasonRows;
    },
    projectedTMDBSeasonRows() {
      if (!this.selectedSiteResultItem || !this.projectedSiteEpisodeItems.length) return [];
      const rows = new Map();
      this.projectedSiteEpisodeItems.forEach((item) => {
        const season = normalizeInt(item && item.season);
        const no = normalizeInt(item && item.no);
        if (season <= 0 || no <= 0) return;
        const current = rows.get(season) || { season, episodes: 0 };
        current.episodes = Math.max(current.episodes, no);
        rows.set(season, current);
      });
      return Array.from(rows.values()).sort((left, right) => left.season - right.season);
    },
    episodeSeasonRows() {
      if (this.selectedSiteResultItem) return this.projectedSeasonRows;
      if (!this.isTmdbMode) return this.directSiteSeasonRows;
      if (this.isTMDBEpisodeSource) return this.tmdbSeasonRows;
      if (this.isDoubanEpisodeSource) return this.doubanSeasonRows;
      return [];
    },
    episodeSeasonOptions() {
      if (this.selectedSiteResultItem) {
        if (this.projectionCurrentSeasonCount <= 1) return [];
        if (!this.episodeSeasonRows.length) return [];
      } else if (this.episodeSeasonRows.length <= 1) {
        return [];
      }
      return this.episodeSeasonRows.map((item) => ({
        season: item.season,
        episodeCount: item.episodes,
        label: this.isDoubanEpisodeSource
          ? (normalizeString(item.label) || `第${formatChineseNumber(item.season)}季`)
          : `第${formatChineseNumber(item.season)}季`,
      }));
    },
    defaultEpisodeSeasonNumber() {
      if (!this.episodeSeasonRows.length) return 0;
      return this.episodeSeasonRows[0].season;
    },
    currentEpisodeSeasonNumber() {
      const selected = normalizeInt(this.selectedViewSeasonNumber);
      if (selected > 0 && this.episodeSeasonRows.some((item) => item.season === selected)) return selected;
      return this.defaultEpisodeSeasonNumber;
    },
    projectedRangeOptions() {
      if (!this.selectedSiteResultItem || !this.projectedSiteEpisodeItems.length) return [];
      const filtered = this.projectedSiteEpisodeItems.filter((item) => {
        if (this.projectedSeasonRows.length <= 1) return true;
        return normalizeInt(item && item.season) === this.currentEpisodeSeasonNumber;
      });
      const nos = filtered
        .map((item) => normalizeInt(item && item.no))
        .filter((no) => no > 0)
        .sort((left, right) => left - right);
      if (!nos.length) return [];
      const minNo = nos[0];
      const maxNo = nos[nos.length - 1];
      if (maxNo <= minNo) return [];
      const ranges = [];
      for (let start = minNo; start <= maxNo; start += 50) {
        const end = Math.min(start + 49, maxNo);
        ranges.push({
          start,
          end,
          label: `${start}-${end}`,
        });
      }
      return ranges;
    },
    defaultProjectedRangeStart() {
      if (!this.selectedSiteResultItem || !this.projectedRangeOptions.length) return 1;
      const filtered = this.projectedSiteEpisodeItems.filter((item) => {
        const no = normalizeInt(item && item.no);
        if (no <= 0) return false;
        if (this.projectedSeasonRows.length <= 1) return true;
        return normalizeInt(item && item.season) === this.currentEpisodeSeasonNumber;
      });
      if (!filtered.length) return this.projectedRangeOptions[0].start;
      const firstNo = filtered.reduce((min, item) => {
        const no = normalizeInt(item && item.no);
        return min > 0 ? Math.min(min, no) : no;
      }, 0);
      const hit = this.projectedRangeOptions.find((item) => firstNo >= item.start && firstNo <= item.end);
      return hit ? hit.start : this.projectedRangeOptions[0].start;
    },
    projectedEpisodeButtons() {
      if (!this.selectedSiteResultItem || !this.projectedSiteEpisodeItems.length) return [];
      const rangeStart = this.projectedRangeOptions.length ? this.currentEpisodeRangeStart : 1;
      const end = this.projectedRangeOptions.length
        ? (this.projectedRangeOptions.find((item) => item.start === rangeStart)?.end || rangeStart + 49)
        : Number.MAX_SAFE_INTEGER;
      return this.projectedSiteEpisodeItems.filter((item) => {
        const no = normalizeInt(item && item.no);
        if (no <= 0) return false;
        if (this.projectedSeasonRows.length > 1 && normalizeInt(item && item.season) !== this.currentEpisodeSeasonNumber) return false;
        return no >= rangeStart && no <= end;
      });
    },
    directSiteEpisodeButtons() {
      const items = Array.isArray(this.directSiteEpisodeItems) ? this.directSiteEpisodeItems : [];
      if (!items.length) return [];
      const rows = Array.isArray(this.directSiteSeasonRows) ? this.directSiteSeasonRows : [];
      const multiSeason = rows.length > 1;
      const seasonNumber = this.currentEpisodeSeasonNumber;
      const rangeStart = this.episodeRangeOptions.length ? this.currentEpisodeRangeStart : 1;
      const currentSeasonRow = multiSeason
        ? rows.find((item) => normalizeInt(item && item.season) === seasonNumber) || null
        : (rows[0] || null);
      const end = this.episodeRangeOptions.length
        ? (this.episodeRangeOptions.find((item) => item.start === rangeStart)?.end
          || (currentSeasonRow ? normalizeInt(currentSeasonRow.episodes) : Number.MAX_SAFE_INTEGER))
        : Number.MAX_SAFE_INTEGER;
      return items
        .filter((item) => {
          const no = normalizeInt(item && item.no);
          if (no <= 0) return false;
          if (multiSeason && Math.max(1, normalizeInt(item && item.season)) !== seasonNumber) return false;
          return no >= rangeStart && no <= end;
        })
        .map((item) => ({
          key: normalizeString(item && item.key) || `direct:${normalizeInt(item && item.itemIndex)}`,
          text: String(normalizeInt(item && item.no)),
          itemIndex: normalizeInt(item && item.itemIndex),
          season: Math.max(1, normalizeInt(item && item.season) || 1),
          no: normalizeInt(item && item.no),
          is4k: !!(item && item.is4k),
        }));
    },
    episodeCountForDisplay() {
      if (!this.isTmdbMode && !this.selectedSiteResultItem) {
        if (!this.directSiteSeasonRows.length) return 0;
        if (this.directSiteSeasonRows.length > 1) {
          const currentSeason = this.directSiteSeasonRows.find((item) => item.season === this.currentEpisodeSeasonNumber) || null;
          return currentSeason ? currentSeason.episodes : 0;
        }
        return this.directSiteSeasonRows[0] ? Math.max(0, normalizeInt(this.directSiteSeasonRows[0].episodes)) : 0;
      }
      if (!this.episodeSeasonRows.length) return 0;
      if (this.episodeSeasonRows.length > 1) {
        const currentSeason = this.episodeSeasonRows.find((item) => item.season === this.currentEpisodeSeasonNumber) || null;
        return currentSeason ? currentSeason.episodes : 0;
      }
      if (this.isTMDBEpisodeSource) {
        return this.episodeSeasonRows[0] ? Math.max(0, normalizeInt(this.episodeSeasonRows[0].episodes)) : 0;
      }
      return this.episodeSeasonRows[0].episodes;
    },
    episodeRangeOptions() {
      if (this.selectedSiteResultItem) return this.projectedRangeOptions;
      const episodeCount = this.episodeCountForDisplay;
      if (episodeCount <= 50) return [];
      const ranges = [];
      for (let start = 1; start <= episodeCount; start += 50) {
        const end = Math.min(start + 49, episodeCount);
        ranges.push({ start, end, label: `${start}-${end}` });
      }
      return ranges;
    },
    currentEpisodeRangeStart() {
      if (!this.episodeRangeOptions.length) return 1;
      const selected = normalizeInt(this.selectedViewRangeStart);
      if (selected > 0 && this.episodeRangeOptions.some((item) => item.start === selected)) return selected;
      if (this.selectedSiteResultItem) return this.defaultProjectedRangeStart;
      return this.episodeRangeOptions[0].start;
    },
    episodeButtons() {
      if (this.selectedSiteResultItem) return this.projectedEpisodeButtons;
      if (!this.isTmdbMode && !this.selectedSiteResultItem) {
        return this.directSiteEpisodeButtons;
      }
      const episodeCount = this.episodeCountForDisplay;
      if (episodeCount <= 0) return [];
      const rangeStart = this.episodeRangeOptions.length ? this.currentEpisodeRangeStart : 1;
      const end = this.episodeRangeOptions.length
        ? (this.episodeRangeOptions.find((item) => item.start === rangeStart)?.end || Math.min(rangeStart + 49, episodeCount))
        : episodeCount;
      const items = [];
      for (let episode = rangeStart; episode <= end; episode += 1) {
        items.push({ key: `episode:${episode}`, text: String(episode) });
      }
      return items;
    },
    showSeasonBar() {
      if (this.rawListMode) return false;
      if (this.selectedSiteResultItem) return this.selectedSiteEpisodePanelState === 'episodes' && this.episodeSeasonOptions.length > 0;
      if (this.siteResultDetailResolutionIncomplete) return false;
      return this.episodeSeasonOptions.length > 0;
    },
    showRangeBar() {
      if (this.rawListMode) return false;
      if (this.selectedSiteResultItem) return this.selectedSiteEpisodePanelState === 'episodes' && this.episodeRangeOptions.length > 1;
      if (this.siteResultDetailResolutionIncomplete) return false;
      return this.episodeRangeOptions.length > 1;
    },
    playSearchQuery() {
      if (this.isTmdbMode) {
        return normalizeSearchKey(this.playSearchQueryOriginal || this.displayTitle);
      }
      return normalizeSearchKey(this.displayTitle);
    },
    playSearchQueryOriginal() {
      if (!this.isTmdbMode) return '';
      return normalizeSearchKey(this.displayTitle || this.searchQueryOriginal);
    },
    playSearchContentKind() {
      return this.isTmdbMode && this.tmdbMovieMode ? 'movie' : 'tv';
    },
    playSearchScope() {
      return `${PLAY_SEARCH_SCOPE}:${this.playSearchContentKind}`;
    },
    playSearchPrimaryScope() {
      if (this.isTmdbMode && this.playSearchQueryOriginal) return 'default';
      return `${this.playSearchScope}:primary`;
    },
    effectivePlaySearchScope() {
      if (this.isTmdbMode) return this.playSearchPrimaryScope;
      return this.playSearchScope;
    },
    playSearchPrimaryStatus() {
      const query = this.playSearchQueryOriginal;
      if (!query) return 'idle';
      return normalizeString(this.playSearchLiveStatus)
        || getSearchSessionLaneStatus(query, this.playSearchPrimaryScope, 'site');
    },
    siteSourceSearchState() {
      return this.playSearchPrimaryStatus;
    },
    showSiteSourceSearchOption() {
      if (!this.playSearchQueryOriginal) return false;
      return this.playSearchPrimaryStatus !== 'completed';
    },
    siteSourceSearchBusy() {
      return this.siteSourceSearchManualLoading || this.siteSourceSearchState === 'loading';
    },
    siteSourceSearchInteractive() {
      return !this.siteSourceSearchBusy;
    },
    siteSourceSearchLabel() {
      return this.siteSourceSearchBusy ? '加载中...' : '加载更多...';
    },
    playSearchPrimarySnapshot() {
      const query = this.playSearchQueryOriginal;
      if (!query) return null;
      return this.playSearchLiveSnapshot || getSearchSessionLaneSnapshot(query, this.playSearchPrimaryScope, 'site');
    },
    playSearchSnapshot() {
      return this.playSearchPrimarySnapshot || this.playSearchLiveSnapshot
        || getSearchSessionLaneSnapshot(this.playSearchQuery, this.effectivePlaySearchScope, 'site');
    },
    playSearchRuntimeConfig() {
      return this.playSearchRuntimeConfigData;
    },
    playHistoryRowForMenu() {
      return findPlayHistoryRowForContext(this.buildPlayHistoryBaseContext()) || null;
    },
    siteSourceResultItems() {
      const items = this.buildSiteSourceResultItemsFromSnapshot(this.playSearchSnapshot);
      if (!this.isTmdbMode) return items;
      const historyItem = buildHistorySitePlaybackItemRuntime(this.playHistoryRowForMenu);
      return withSiteSourceDisplayLabels(historyItem ? [historyItem].concat(items) : items);
    },
    selectedSiteResultItem() {
      if (!this.selectedSearchResultId) return null;
      return this.siteSourceResultItems.find((item) => String(item.id) === String(this.selectedSearchResultId)) || null;
    },
    siteDetailPanSources() {
      return buildPanSourcesFromDetailRuntime(this.siteResultDetailData);
    },
    selectedSitePanState() {
      if (!this.selectedSiteResultItem) {
        return {
          loading: false,
          error: '',
          hasSegments: false,
          hasProjectedEpisodes: false,
        };
      }
      const entry = this.currentPanSourceEntry;
      const segments = getPanEntrySegments(entry);
      return {
        loading: !!(entry && entry.loading),
        error: normalizeString(entry && entry.error),
        hasSegments: segments.length > 0,
        hasProjectedEpisodes: this.projectedEpisodeButtons.length > 0,
      };
    },
    selectedSiteEpisodeStatusText() {
      if (!this.selectedSiteResultItem) return '';
      if (this.siteResultDetailError) return this.siteResultDetailError;
      if (!this.siteResultDetailData) return '';
      if (!this.currentPanSourceOptions.length) return '暂无数据';
      if (!this.currentPanSourceEntry) return this.siteResultDetailResolutionIncomplete ? '' : '暂无数据';
      if (this.selectedSitePanState.loading) return '';
      if (this.selectedSitePanState.error) return this.selectedSitePanState.error;
      if (this.selectedSitePanState.hasProjectedEpisodes) return '';
      if (this.selectedSitePanState.hasSegments) {
        return this.rawListMode ? '' : '暂无匹配选集，可切换原始列表';
      }
      return '暂无数据';
    },
    selectedSiteEpisodePanelState() {
      if (!this.selectedSiteResultItem) return 'episodes';
      if (!this.siteResultDetailData) return 'loading';
      if (!this.currentPanSourceEntry) return this.siteResultDetailResolutionIncomplete ? 'loading' : 'status';
      if (this.selectedSitePanState.loading) return 'loading';
      const hasEpisodes = !!this.selectedSitePanState.hasProjectedEpisodes;
      const hasRaw = !!this.selectedSitePanState.hasSegments;
      if (this.rawListMode) {
        return hasRaw ? 'raw' : 'status';
      }
      if (hasEpisodes) return 'episodes';
      return 'status';
    },
    siteResultEpisodeStatusText() {
      if (this.showTmdbMovieCandidateList) {
        if (normalizeString(this.playError)) return normalizeString(this.playError);
        if (this.tmdbMovieCandidateItems.length) return '';
        return '暂无数据';
      }
      if (this.isTMDBEpisodeSource) {
        return this.episodeButtons.length ? '' : '暂无数据';
      }
      if (this.isDoubanEpisodeSource) {
        return this.episodeButtons.length ? '' : '暂无数据';
      }
      if (!this.activeSitePlaybackItem) return '';
      if (this.selectedSiteResultItem) {
        return this.selectedSiteEpisodeStatusText;
      } else if (this.detailLoading) {
        return '';
      }
      if (!this.currentPanSourceOptions.length) return '暂无数据';
      if (this.currentPanSourceEntry && this.currentPanSourceEntry.error) {
        return this.currentPanSourceEntry.error;
      }
      if (this.currentPanSourceEntry && !getPanEntrySegments(this.currentPanSourceEntry).length) {
        return '暂无数据';
      }
      if (!this.isTmdbMode && !this.selectedSiteResultItem && this.contentKind === 'series' && !this.episodeButtons.length) {
        return '暂无数据';
      }
      if (this.isProjectedSiteEpisodeSource && !this.projectedEpisodeButtons.length) {
        return '暂无数据';
      }
      return '';
    },
    episodeOverlayText() {
      return this.siteResultEpisodeStatusText || '暂无数据';
    },
    episodeContentState() {
      if (this.showTmdbMovieCandidateList) {
        if (!this.tmdbMovieCandidateItems.length && this.playLoading) return 'loading';
        if (
          !this.tmdbMovieCandidateItems.length
          && (normalizeInt(this.smartPlaybackPendingRunSeq) > 0 || normalizeInt(this.smartPlaybackAttemptRunSeq) > 0)
        ) return 'loading';
        if (this.siteSourceSearchState === 'loading' && !this.tmdbMovieCandidateItems.length) return 'loading';
        if (this.siteResultEpisodeStatusText) return 'status';
        return 'movie-candidates';
      }
      if (this.selectedSiteResultItem) {
        return this.selectedSiteEpisodePanelState;
      }
      if (!this.isTmdbMode && this.activeSitePlaybackItem) {
        if (this.detailLoading) return 'loading';
        if (this.rawListMode && this.showPanSourceRow) return 'raw';
        if (this.siteResultEpisodeStatusText) return 'status';
        return 'episodes';
      }
      if (this.isTMDBEpisodeSource) {
        if (this.detailLoading) return 'loading';
        if (this.siteResultEpisodeStatusText) return 'status';
        return 'episodes';
      }
      if (this.isDoubanEpisodeSource) {
        if (this.doubanLoading) return 'loading';
        if (this.siteResultEpisodeStatusText) return 'status';
        return 'episodes';
      }
      if (this.rawListMode && this.showPanSourceRow) return 'raw';
      if (this.siteResultEpisodeStatusText) return 'status';
      return 'episodes';
    },
    currentPanSourceEntry() {
      if (!this.siteDetailPanSources.length) return null;
      const selectedKey = normalizeString(this.selectedPanSource);
      if (selectedKey) {
        return this.siteDetailPanSources.find((item) => item.key === selectedKey) || null;
      }
      return this.siteDetailPanSources[0] || null;
    },
    rawDirModeEnabled() {
      const entry = this.currentPanSourceEntry;
      if (!this.rawListMode || !entry) return false;
      if (!(this.siteResultDetailData && this.siteResultDetailData.panMock)) return false;
      return !!normalizeString(entry.provider) && getPanEntrySegments(entry).length > 0;
    },
    rawDirIdentity() {
      const entry = this.currentPanSourceEntry;
      if (!this.rawDirModeEnabled || !entry) return '';
      return [
        normalizeString(entry.label),
        getPanEntrySegments(entry).join('#'),
      ].join('::');
    },
    rawDirTree() {
      const entry = this.currentPanSourceEntry;
      const segments = getPanEntrySegments(entry);
      if (!this.rawDirModeEnabled || !entry || !segments.length) return null;
      const root = { dirs: new Map(), files: [] };
      const ensureDir = (node, name) => {
        const key = normalizeString(name);
        if (!key) return node;
        if (!node.dirs.has(key)) node.dirs.set(key, { name: key, dirs: new Map(), files: [] });
        return node.dirs.get(key);
      };
      segments.forEach((segment, index) => {
        const raw = normalizeString(segment);
        if (!raw) return;
        const dollarIdx = raw.indexOf('$');
        const displayName = dollarIdx >= 0 ? normalizeString(raw.slice(0, dollarIdx)) : raw;
        const browseDisplayName = stripDisplayMetaPrefix(displayName);
        const episodeUrl = dollarIdx >= 0 ? normalizeString(raw.slice(dollarIdx + 1)) : raw;
        const rawName = extractRawNamesFromEpisodeUrl(episodeUrl)[0] || '';
        const fileName = getRawFileName(rawName);
        if (!fileName) return;
        const dirs = splitRawPathSegments(browseDisplayName);
        let node = root;
        dirs.forEach((dir) => {
          node = ensureDir(node, dir);
        });
        node.files.push({
          name: fileName,
          index,
          url: episodeUrl,
          displayName,
          rawName,
        });
      });
      return root;
    },
    rawDirNode() {
      const tree = this.rawDirTree;
      if (!tree) return null;
      const path = Array.isArray(this.rawDirPath) ? this.rawDirPath : [];
      let node = tree;
      for (let i = 0; i < path.length; i += 1) {
        const seg = normalizeString(path[i]);
        if (!seg) continue;
        if (!node.dirs || !node.dirs.has(seg)) return null;
        node = node.dirs.get(seg);
      }
      return node;
    },
    rawDirEntries() {
      if (!this.rawDirModeEnabled) return [];
      const node = this.rawDirNode;
      if (!node) return [];
      const dirs = node.dirs ? Array.from(node.dirs.keys()) : [];
      const files = Array.isArray(node.files) ? node.files : [];
      dirs.sort((a, b) => String(a).localeCompare(String(b), 'zh'));
      const sortedFiles = files.slice().sort((a, b) => String(a && a.name).localeCompare(String(b && b.name), 'zh'));
      const out = [];
      dirs.forEach((name) => {
        out.push({
          key: `d:${(this.rawDirPath || []).join('/')}:${name}`,
          kind: 'dir',
          name: String(name),
          index: -1,
          text: `${String(name)}/`,
        });
      });
      sortedFiles.forEach((item) => {
        const text = normalizeString(item.name);
        if (!text) return;
        out.push({
          key: `f:${item.index}:${item.url}`,
          kind: 'file',
          index: item.index,
          text,
          selectionKey: buildSiteEpisodeSelectionKey(this.currentPanSourceEntry && this.currentPanSourceEntry.key, item.index),
        });
      });
      return out;
    },
    rawListDisplayPath() {
      if (!this.rawDirModeEnabled) return '';
      const path = Array.isArray(this.rawDirPath) ? this.rawDirPath : [];
      if (!path.length) return '';
      return `/${path.map((item) => normalizeString(item)).filter(Boolean).join('/')}`;
    },
    canGoRawDirBack() {
      if (!this.rawDirModeEnabled) return false;
      const depth = Array.isArray(this.rawDirPath) ? this.rawDirPath.length : 0;
      return depth > Math.max(0, normalizeInt(this.rawDirLockedDepth));
    },
    rawListItems() {
      if (this.rawDirModeEnabled) return this.rawDirEntries;
      const entry = this.currentPanSourceEntry;
      const segments = getPanEntrySegments(entry);
      if (!entry || !segments.length) return [];
      let allFileNamesSame = true;
      let firstFileName = null;
      segments.forEach((segment) => {
        const dollarIdx = segment.indexOf('$');
        const episodeUrl = dollarIdx >= 0 ? normalizeString(segment.slice(dollarIdx + 1)) : segment;
        const fileName = extractRawNamesFromEpisodeUrl(episodeUrl)[0] || '';
        if (firstFileName == null) {
          firstFileName = fileName;
        } else if (fileName !== firstFileName) {
          allFileNamesSame = false;
        }
      });
      const titleLower = normalizeString(this.displayTitle).toLowerCase();
      const isPanMockList = !!normalizeString(entry.provider);
      return segments
        .map((segment, index) => {
          const dollarIdx = segment.indexOf('$');
          const label = dollarIdx >= 0 ? normalizeString(segment.slice(0, dollarIdx)) : segment;
          const episodeUrl = dollarIdx >= 0 ? normalizeString(segment.slice(dollarIdx + 1)) : segment;
          const rawName = extractRawNamesFromEpisodeUrl(episodeUrl)[0] || '';
          const fileName = getRawFileName(rawName);
          const displayName = label;
          let text = '';
          if (isPanMockList) {
            text = fileName;
          } else if (allFileNamesSame) {
            text = displayName;
          } else {
            const displayScore = scoreEpisodeDisplayName(displayName, titleLower);
            const fileScore = scoreEpisodeDisplayName(fileName, titleLower);
            text = displayScore >= fileScore ? displayName : fileName;
          }
          text = normalizeString(text);
          if (!text) return null;
          return {
            key: `${entry.key}:${index}:${text}`,
            kind: 'file',
            index,
            text,
            selectionKey: buildSiteEpisodeSelectionKey(entry.key, index),
          };
        })
        .filter(Boolean);
    },
    rawListEmptyText() {
      return this.episodeOverlayText;
    },
    playerToastText() {
      return normalizeString(this.playerTransientToastText);
    },
    playerToastSticky() {
      return !!this.playerTransientToastSticky;
    },
    showPlayerStatusOverlay() {
      // During action-mode search toast, never show fullscreen status overlay.
      if (this.playerTransientToastSticky) return false;
      return this.playerUiTransitionMode !== 'switch' && this.playerPhase !== 'ready' && this.playerPhase !== 'buffering';
    },
    playerPhase() {
      if (normalizeString(this.playError) || normalizeString(this.playerRuntimeError)) return 'error';
      if (!normalizeString(this.playerUrl)) {
        if (this.playRequestStage === 'play_url') return 'play_url';
        if (
          this.detailLoading
          || (this.isTmdbMode && !this.tmdbMovieMode && !this.fullEpisodeMapping)
          || (this.tmdbMovieMode && !(this.detailTMDBData && typeof this.detailTMDBData === 'object'))
        ) {
          return 'idle';
        }
        if (
          this.playRequestStage === 'detail'
          || normalizeInt(this.smartPlaybackPendingRunSeq) > 0
          || this.autoplayInFlight
          || this.playLoading
        ) {
          return 'detail';
        }
        return 'idle';
      }
      if (this.playRequestStage === 'play_info' || !this.playerMetaReady || !this.playerFirstFrameReady) {
        return 'play_info';
      }
      if (this.playerBuffering) return 'buffering';
      return 'ready';
    },
    playerPhaseLoading() {
      return ['idle', 'detail', 'play_url', 'play_info'].includes(this.playerPhase);
    },
    playerPhaseText() {
      switch (this.playerPhase) {
        case 'detail':
          return '正在获取视频...';
        case 'play_url':
          return '正在获取播放地址...';
        case 'play_info':
          return '正在获取视频信息...';
        case 'error':
          return normalizeString(this.playerRuntimeError || this.playError) || '请求失败';
        case 'idle':
        default:
          return '加载中...';
      }
    },
    playerStageIndex() {
      switch (this.playerPhase) {
        case 'detail':
          return 0;
        case 'play_url':
          return 1;
        case 'play_info':
          return 2;
        case 'buffering':
        case 'ready':
          return 3;
        case 'error':
          if (normalizeString(this.playerUrl) && this.playerMetaReady) return 3;
          if (normalizeString(this.playerUrl)) return 2;
          if (this.playRequestStage === 'play_url') return 1;
          return 0;
        case 'idle':
        default:
          return 0;
      }
    },
    playerStageDoneCount() {
      return Math.max(0, Math.min(this.playerStageIndex, this.playerStageItems.length));
    },
    playerStageProgress() {
      const total = this.playerStageItems.length || 3;
      if (this.playerPhase === 'idle') return 0;
      if (this.playerStageIndex >= total) return 1;
      if (this.playerPhase === 'error') return this.playerStageDoneCount / total;
      return (this.playerStageDoneCount + 0.5) / total;
    },
    playerStatsExtra() {
      return {
        displayName: this.displayTitle,
        siteName: normalizeString(this.playerStatsSiteName),
        panName: normalizeString(this.playerStatsPanName),
        pathName: normalizeString(this.playerStatsPathName),
        rawFileName: normalizeString(this.playerStatsRawFileName),
      };
    },
    currentPlaybackContext() {
      const raw = playbackSessionState.currentContext && typeof playbackSessionState.currentContext === 'object'
        ? playbackSessionState.currentContext
        : null;
      return {
        itemId: normalizeString(raw && raw.itemId),
        siteKey: normalizeString(raw && raw.siteKey),
        siteName: normalizeString(raw && raw.siteName),
        spiderApi: normalizeString(raw && raw.spiderApi),
        siteDetail: normalizeString(raw && raw.siteDetail),
        panKey: normalizeString(raw && raw.panKey),
        panFlag: normalizeString(raw && raw.panFlag),
        selectionKey: normalizeString(raw && raw.selectionKey),
        globalEpisode: Math.max(0, normalizeInt(raw && raw.globalEpisode)),
        itemIndex: normalizeInt(raw && raw.itemIndex),
        sourceQuality: normalizeString(raw && raw.sourceQuality),
        quality: normalizeString(raw && raw.quality),
        pathName: normalizeString(raw && raw.pathName),
        rawFileName: normalizeString(raw && raw.rawFileName),
        fileIdentity: normalizeString(raw && raw.fileIdentity),
        sourceKind: normalizeString(raw && raw.sourceKind),
        sourceSignature: normalizeString(raw && raw.sourceSignature),
      };
    },
    isPlayerInTmdbMode() {
      return this.isTmdbMode;
    },
    playerControlUiState() {
      return buildPlayerControlUiState({
        isPrimaryTmdbMode: this.isPlayerInTmdbMode,
        currentContext: this.currentPlaybackContext,
        runtimeSettings: this.runtimeSettings,
        currentPanSourceOptions: this.currentPanSourceOptions,
      });
    },
    currentPlaybackPanFamilyLabel() {
      return normalizeString(this.playerControlUiState && this.playerControlUiState.currentPanFamilyLabel) || '未知';
    },
    currentPlaybackQualityKey() {
      return normalizeString(this.playerControlUiState && this.playerControlUiState.currentQualityKey) || 'auto';
    },
    currentPlaybackQualityLabel() {
      return normalizeString(this.playerControlUiState && this.playerControlUiState.currentQualityLabel) || '未知';
    },
    fullEpisodeMapping() {
      return this.smartEpisodeMapping;
    },
    playerExtraMenus() {
      const state = this.playerControlUiState && typeof this.playerControlUiState === 'object'
        ? this.playerControlUiState
        : null;
      return Array.isArray(state && state.playerExtraMenus) ? state.playerExtraMenus : [];
    },
    playerExtraActions() {
      const state = this.playerControlUiState && typeof this.playerControlUiState === 'object'
        ? this.playerControlUiState
        : null;
      return Array.isArray(state && state.playerExtraActions) ? state.playerExtraActions : [];
    },
    currentPrimaryPlaybackEpisodeTarget() {
      if (this.selectedSiteResultItem) return null;
      if (!this.isTmdbMode) {
        const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
          ? this.currentPlaybackContext
          : null;
        const playbackIndex = normalizeInt(playback && playback.itemIndex);
        const playbackSelectionKey = normalizeString(playback && playback.selectionKey);
        const item = this.directSiteEpisodeItems.find((entry) => {
          const entryIndex = normalizeInt(entry && entry.itemIndex);
          if (playbackIndex >= 0 && entryIndex === playbackIndex) return true;
          if (!playbackSelectionKey || !this.currentPanSourceEntry) return false;
          return playbackSelectionKey === buildSiteEpisodeSelectionKey(this.currentPanSourceEntry.key, entryIndex);
        }) || null;
        if (!item) return null;
        return {
          season: Math.max(1, normalizeInt(item && item.season) || 1),
          episode: normalizeInt(item && item.no),
        };
      }
      const globalEpisode = Math.max(0, normalizeInt(this.currentPlaybackContext.globalEpisode));
      if (globalEpisode <= 0) return null;
      if (this.isTMDBEpisodeSource) {
        const tmdb = tmdbSeasonEpisodeOfGlobal(this.detailTMDBData, globalEpisode);
        const season = normalizeInt(tmdb && tmdb.season);
        const episode = normalizeInt(tmdb && tmdb.episode);
        return season > 0 && episode > 0 ? { season, episode } : null;
      }
      if (this.isDoubanEpisodeSource) {
        const douban = doubanSeasonEpisodeOfGlobal(this.detailDoubanData, globalEpisode);
        const season = normalizeInt(douban && douban.season);
        const episode = normalizeInt(douban && douban.episode);
        return season > 0 && episode > 0 ? { season, episode } : null;
      }
      return null;
    },
    portraitTopEpisodeTarget() {
      if (this.selectedSiteResultItem) {
        const projected = this.projectedSiteEpisodeItems.find((item) => this.isCurrentProjectedPlaybackSelection(item)) || null;
        if (projected) {
          return {
            season: Math.max(1, normalizeInt(projected && projected.season) || 1),
            episode: normalizeInt(projected && projected.no),
          };
        }
        return null;
      }
      return this.currentPrimaryPlaybackEpisodeTarget;
    },
    portraitEpisodeText() {
      const target = this.portraitTopEpisodeTarget;
      const episodeNo = normalizeInt(target && target.episode);
      if (episodeNo <= 0) return '';
      const seasonNo = Math.max(1, normalizeInt(target && target.season) || 1);
      if (this.episodeSeasonRows.length > 1) return `第${seasonNo}季第${episodeNo}集`;
      return `第${episodeNo}集`;
    },
    portraitTopTitleEpisodeText() {
      const title = normalizeString(this.displayTitle);
      const episodeText = normalizeString(this.portraitEpisodeText);
      if (title && episodeText) return `${title}·${episodeText}`;
      return title || episodeText;
    },
    goProxyUiEligible() {
      const settings = this.runtimeSettings && typeof this.runtimeSettings === 'object' ? this.runtimeSettings : null;
      if (!settings || !settings.goProxyEnabled) return false;
      const candidate = this.lastGoProxyCandidate && typeof this.lastGoProxyCandidate === 'object'
        ? this.lastGoProxyCandidate
        : null;
      if (!candidate || !candidate.enabled) return false;
      return hasNonEmptyHeaders(candidate.headers);
    },
    goProxyUiOptions() {
      if (!this.goProxyUiEligible) return [];
      return normalizeGoProxyServers(this.runtimeSettings && this.runtimeSettings.goProxyServers);
    },
    goProxyUiLabel() {
      if (!this.goProxyUiEligible || !this.goProxyUiOptions.length) return '';
      const activeBase = normalizeString(this.goProxyInUseBase) || normalizeString(this.goProxyManualBase);
      const active = activeBase
        ? this.goProxyUiOptions.find((item) => normalizeString(item.base) === activeBase)
        : null;
      return (active && active.label) || this.goProxyUiOptions[0].label || 'GoProxy';
    },
    smartEpisodeMappingSignature() {
      return buildEpisodeMappingSignature(this.fullEpisodeMapping);
    },
    activeRecognitionSignature() {
      if (this.isTmdbMode && this.tmdbMovieMode) return TMDB_MOVIE_RECOGNITION_SIGNATURE;
      return normalizeString(this.smartEpisodeMappingSignature);
    },
    canRunFullSmartPlayback() {
      if (!this.isTmdbMode || this.tmdbMovieMode) return false;
      if (this.detailLoading || this.doubanLoading) return false;
      const mapping = this.fullEpisodeMapping;
      return !!(mapping && normalizeInt(mapping.totalEpisodes) > 0);
    },
    playbackRecognitionData() {
      const item = this.selectedSiteResultItem;
      const entry = this.currentPanSourceEntry;
      if (!item || !entry || !entry.key) {
        return { source: null, items: [] };
      }
      const payload = getSiteResultRecognitionBySignatureRuntime(
        this.siteResultRecognitionStore,
        item,
        this.activeRecognitionSignature,
        entry.key,
      );
      return payload && typeof payload === 'object'
        ? payload
        : { source: null, items: [] };
    },
    displayTitle() {
      return normalizeString(this.contentKey) || '未命名内容';
    },
    detailPoster() {
      return getTMDBPosterPath(this.detailTMDBData)
        || normalizeString(this.Poster);
    },
    detailBackdrop() {
      return getTMDBBackdropPath(this.detailTMDBData);
    },
    detailPosterDisplay() {
      return rewriteDisplayPosterUrl(this.detailPoster, this.runtimeSettings || {});
    },
    detailMetaTags() {
      const tags = this.isTmdbMode
        ? [
          this.detailRemarkText,
          ...this.detailGenreTags,
          this.detailYearText,
        ]
        : this.selectedSiteResultItem
          ? [normalizeString(this.selectedSiteResultItem.siteName), this.detailYearText]
          : [
            this.detailSiteNameText,
            this.detailYearText,
          ];
      return tags.filter((tag) => !!normalizeString(tag));
    },
    detailDescription() {
      if (this.isTmdbMode) {
        return this.detailOverview || (this.detailLoading ? '加载中' : '暂无简介');
      }
      if (this.selectedSiteResultItem) {
        return this.detailOverview || (this.siteResultDetailLoading ? '加载中' : '暂无简介');
      }
      return this.detailOverview || (this.detailLoading ? '加载中' : '暂无简介');
    },
    detailYearText() {
      return this.detailYear || '';
    },
    detailGenreTags() {
      if (!this.isTmdbMode) return [];
      const genres = Array.isArray(this.detailTMDBData && this.detailTMDBData.genres)
        ? this.detailTMDBData.genres
        : [];
      const deduped = new Set();
      return genres
        .map((item) => {
          const genreId = normalizeInt(item && item.id);
          const mapped = normalizeString(TMDB_GENRE_LABELS[genreId]);
          if (mapped) return mapped;
          const rawName = normalizeString(item && item.name);
          return hasChineseText(rawName) ? rawName : '';
        })
        .filter((tag) => {
          const key = normalizeString(tag);
          if (!key || deduped.has(key)) return false;
          deduped.add(key);
          return true;
        });
    },
    historyRemarkText() {
      return normalizeString(this.Remark);
    },
    detailRemarkText() {
      return normalizeString(this.detailRemark) || this.historyRemarkText;
    },
    detailSiteNameText() {
      return normalizeString(this.siteName) || '';
    },
  },
  data() {
    return {
      siteSourceSelectWidth: 0,
      siteSourceOpen: false,
      panSourceOpen: false,
      siteSourceSearchManualLoading: false,
      playSearchRuntimeConfigData: null,
      playSearchLiveSnapshot: null,
      playSearchLiveStatus: 'idle',
      playSearchUnsubscribe: null,
      siteSourceOptions: [],
      selectedSiteSource: '',
      selectedPanSource: '',
      portraitMode: false,
      episodePanelHidden: false,
      episodePanelHiddenBeforePortrait: false,
      siteSourceResizeObserver: null,
      viewportWidth: typeof window === 'undefined' ? 0 : Math.max(0, normalizeInt(window.innerWidth)),
      portraitPlayerAreaWidth: 0,
      playerAreaHeight: 0,
      playerAreaResizeObserver: null,
      thirdPartyExpanded: false,
      runtimeSettings: null,
      detailLoading: false,
      detailOverview: '',
      detailYear: '',
      detailRemark: '',
      detailTMDBData: null,
      detailDoubanData: null,
      detailDoubanMetaKey: '',
      smartEpisodeMapping: null,
      doubanLoading: false,
      siteResultDetailData: null,
      siteResultDetailStore: {},
      siteResultDetailInFlightMap: {},
      siteResultRecognitionStore: {},
      playBlockedSiteKeys: [],
      playBlockedMatchIndex: {},
      siteResultDetailLoading: false,
      siteResultDetailError: '',
      playerUrl: '',
      playerHeaders: {},
      playLoading: false,
      autoNextInFlight: false,
      playError: '',
      playerRuntimeError: '',
      playerBuffering: false,
      playerMetaReady: false,
      playerFirstFrameReady: false,
      playerPlaybackStarted: false,
      playRequestStage: '',
      playerUiTransitionMode: '',
      playerTransientToastText: '',
      playerTransientToastSticky: false,
      playerTransientToastTimer: 0,
      playerActionPendingTargetKey: '',
      switchSkipByEpisodeKey: {},
      activePlayerControlAction: '',
      playerStatsSiteName: '',
      playerStatsPanName: '',
      playerStatsPathName: '',
      playerStatsRawFileName: '',
      playerStageItems: [
        { key: 'detail', label: '信息' },
        { key: 'play_url', label: '地址' },
        { key: 'play_info', label: '信息' },
      ],
      autoplayConsumed: false,
      autoplayInFlight: false,
      historySmartBootstrapStageDone: {},
      manualSmartBootstrapStageDone: {},
      playRequestSeq: 0,
      lastResolvedPlaybackPayload: null,
      smartPlaybackRunSeq: 0,
      manualSmartBootstrapSeq: 0,
      smartPlaybackStage: 'idle',
      smartPlaybackActiveChannel: '',
      smartPlaybackChannelToken: 0,
      smartPlaybackResolvedStage: '',
      rawListMode: false,
      rawDirPath: [],
      rawDirLockedDepth: 0,
      lastRawDirIdentity: '',
      selectedViewSeasonNumber: 0,
      selectedViewRangeStart: 0,
      preOrderToggleBusy: false,
      selectedSiteEpisodeSelectionKey: '',
      siteHistoryResolvedByTitle: '',
      siteHistoryResolvedMeta: null,
      detailFetchSeq: 0,
      selectedSearchResultId: '',
      selectedProjectionSource: 'TMDB',
      smartPlaybackPendingRunSeq: 0,
      smartPlaybackConfirmedRunSeq: 0,
      smartPlaybackAttemptRunSeq: 0,
      smartPlaybackResume: null,
      smartPlaybackStreamCleanup: null,
      goProxyManualBase: '',
      goProxyInUseBase: '',
      lastGoProxyCandidate: null,
      lastM3U8RelayCandidate: null,
      autoProxyRetriedSeq: 0,
      autoM3U8RelayRetriedSeq: 0,
      proxyRetryInFlight: false,
      m3u8RelayRetryInFlight: false,
      episodePanelResizeState: {
        dragging: false,
        startX: 0,
        startWidth: 0,
      },
    };
  },
  mounted() {
    this.resetSwitchSkipState();
    document.addEventListener('click', this.handleDocumentClick, true);
    window.addEventListener('tv:smart-matchblock-updated', this.handleSmartMatchBlockUpdated);
    window.addEventListener('resize', this.handleViewportResize, { passive: true });
    this.syncViewportWidth();
    this.bindSiteSourceWidth();
    this.bindPlayerAreaHeight();
    this.setEpisodePanelWidth(this.defaultEpisodePanelWidth);
    this.bindEpisodePanelResizer();
    void this.prefetchPlayHistoryForPage();
    this.bindPlaySearchQuerySubscription();
    this.loadPlayRuntimeAndDetail();
  },
  beforeUnmount() {
    this.resetSwitchSkipState();
    this.cancelActivePlaybackFlow({ stopStream: true, invalidateDetail: true, clearPlayerState: true });
    this.selectedSearchResultId = '';
    flushHistoryProgressBestEffort();
    clearActivePlayHistoryContext();
    clearCurrentPlaybackContext();
    document.removeEventListener('click', this.handleDocumentClick, true);
    window.removeEventListener('tv:smart-matchblock-updated', this.handleSmartMatchBlockUpdated);
    window.removeEventListener('resize', this.handleViewportResize);
    if (this.siteSourceResizeObserver) {
      this.siteSourceResizeObserver.disconnect();
      this.siteSourceResizeObserver = null;
    }
    if (this.playerAreaResizeObserver) {
      this.playerAreaResizeObserver.disconnect();
      this.playerAreaResizeObserver = null;
    }
    if (this.playerTransientToastTimer) {
      window.clearTimeout(this.playerTransientToastTimer);
      this.playerTransientToastTimer = 0;
    }
    this.unbindPlaySearchQuerySubscription();
    this.unbindEpisodePanelResizer();
  },
  methods: {
    cancelActivePlaybackFlow({ stopStream = true, invalidateDetail = false, clearPlayerState = false } = {}) {
      this.resetSmartPlaybackRuntimeState({ stopStream });
      this.playRequestSeq += 1;
      if (invalidateDetail) this.detailFetchSeq += 1;
      this.playLoading = false;
      this.playError = '';
      this.playerRuntimeError = '';
      this.playRequestStage = '';
      this.proxyRetryInFlight = false;
      this.m3u8RelayRetryInFlight = false;
      this.activePlayerControlAction = '';
      this.playerUiTransitionMode = '';
      this.lastGoProxyCandidate = null;
      this.lastM3U8RelayCandidate = null;
      if (clearPlayerState) {
        this.playerUrl = '';
        this.playerHeaders = {};
        this.resetPlayerReadyState();
      }
    },
    onBackClick() {
      this.cancelActivePlaybackFlow({ stopStream: true, invalidateDetail: true, clearPlayerState: true });
      this.$emit('back');
    },
    buildSwitchEpisodeKeyByGlobal(globalEpisode) {
      const contentKey = normalizeString(this.playContentPreferenceKey);
      const targetGlobalEpisode = Math.max(0, normalizeInt(globalEpisode));
      if (!contentKey || targetGlobalEpisode <= 0) return '';
      return `${contentKey}::${targetGlobalEpisode}`;
    },
    buildSwitchEpisodeKey() {
      const currentGlobalEpisode = Math.max(0, normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.globalEpisode));
      return this.buildSwitchEpisodeKeyByGlobal(currentGlobalEpisode);
    },
    buildPrimaryEpisodeActionConstraint(globalEpisode = 0) {
      const currentPanFamily = normalizeString(this.currentPlaybackPanFamilyLabel);
      const currentQualityKey = normalizeString(this.currentPlaybackQualityKey);
      const historyPanFlag = normalizeString(this.playHistoryRowForMenu && this.playHistoryRowForMenu.playFlag);
      return {
        mode: 'primary',
        currentPanFamily,
        currentQualityKey,
        historyPanFlag,
        globalEpisode: Math.max(0, normalizeInt(globalEpisode)),
      };
    },
    resolveSwitchPanProviderKey(value) {
      const raw = normalizeString(value).toLowerCase();
      if (!raw) return '';
      if (raw.includes('quark') || raw.includes('夸克')) return 'quark';
      if (raw.includes('uc')) return 'uc';
      if (raw.includes('baidu') || raw.includes('百度')) return 'baidu';
      if (raw.includes('139') || raw.includes('移动云盘')) return '139';
      if (raw.includes('189') || raw.includes('天翼')) return '189';
      return '';
    },
    resolveSwitchPanFamilyLabel(value) {
      const raw = normalizeString(value);
      if (!raw) return '';
      const runtime = this.runtimeSettings && typeof this.runtimeSettings === 'object' ? this.runtimeSettings : null;
      const mapped = resolvePanFamilyLabelForUi(raw, runtime);
      return normalizeString(mapped) || raw;
    },
    normalizeSwitchPanFlag(value) {
      const family = this.resolveSwitchPanFamilyLabel(value);
      const provider = this.resolveSwitchPanProviderKey(family);
      return provider || family;
    },
    buildSwitchSkipInfo(wrapper) {
      const target = wrapper && typeof wrapper === 'object' ? wrapper : null;
      const siteItem = target && target.siteItem && typeof target.siteItem === 'object' ? target.siteItem : null;
      const candidate = target && target.candidate && typeof target.candidate === 'object' ? target.candidate : null;
      const source = candidate && candidate.source && typeof candidate.source === 'object' ? candidate.source : null;
      const siteKey = normalizeString(target && target.siteKey)
        || normalizeString(siteItem && siteItem.siteKey)
        || normalizeString(source && source.siteKey);
      const siteDetail = normalizeString(target && target.siteDetail)
        || normalizeString(siteItem && siteItem.siteDetail)
        || normalizeString(source && source.siteDetail);
      const fileIdentity = this.buildSwitchFileIdentity(target);
      const panFlag = this.normalizeSwitchPanFlag(
        normalizeString(candidate && candidate.panFlag)
        || normalizeString(source && source.panFlag)
        || normalizeString(target && target.panFlag)
        || normalizeString(target && target.panEntry && target.panEntry.label)
      );
      const providerHint = normalizeString(candidate && candidate.provider)
        || normalizeString(source && source.provider)
        || normalizeString(target && target.provider);
      const panProvider = this.resolveSwitchPanProviderKey(providerHint)
        || this.resolveSwitchPanProviderKey(this.resolveSwitchPanFamilyLabel(panFlag));
      return {
        siteKey,
        siteDetail,
        panFlag,
        panProvider,
        fileIdentity,
      };
    },
    normalizeSwitchSkipRecord(info) {
      const item = info && typeof info === 'object' ? info : null;
      const siteKey = normalizeString(item && item.siteKey);
      const siteDetail = normalizeString(item && item.siteDetail);
      const panFlag = this.normalizeSwitchPanFlag(item && item.panFlag);
      const panProvider = normalizeString(item && item.panProvider);
      const fileIdentity = normalizeString(item && item.fileIdentity);
      if (!siteKey || !siteDetail || !panFlag || !fileIdentity) return null;
      return {
        siteKey,
        siteDetail,
        panFlag,
        panProvider,
        fileIdentity,
      };
    },
    isSameSwitchSkipRecord(left, right) {
      const l = this.normalizeSwitchSkipRecord(left);
      const r = this.normalizeSwitchSkipRecord(right);
      if (!l || !r) return false;
      return normalizeString(l.siteKey) === normalizeString(r.siteKey)
        && normalizeString(l.siteDetail) === normalizeString(r.siteDetail)
        && this.normalizeSwitchPanFlag(l.panFlag) === this.normalizeSwitchPanFlag(r.panFlag)
        && normalizeString(l.fileIdentity) === normalizeString(r.fileIdentity);
    },
    buildSwitchFileIdentity(wrapper) {
      const target = wrapper && typeof wrapper === 'object' ? wrapper : null;
      const candidate = target && target.candidate && typeof target.candidate === 'object' ? target.candidate : null;
      const direct = normalizeString(target && target.fileIdentity);
      if (direct) return direct;
      const segmentIdentity = normalizeString(candidate && candidate.segmentIdentity);
      if (segmentIdentity) return segmentIdentity;
      return '';
    },
    buildPlaybackSourceSignature({ siteKey = '', siteDetail = '', panKey = '', itemIndex = -1 } = {}) {
      const nextSiteKey = normalizeString(siteKey);
      const nextVideoId = normalizeString(siteDetail);
      const nextPanKey = normalizeString(panKey);
      const nextItemIndex = normalizeInt(itemIndex);
      if (!nextSiteKey || !nextVideoId || !nextPanKey || nextItemIndex < 0) return '';
      return `${nextSiteKey}::${nextVideoId}::${nextPanKey}::${nextItemIndex}`;
    },
    resolvePlaybackSourceKind({ sourceKind = '', fromHistoryPlayFlag = false, fromHistoryDetail = false } = {}) {
      const explicit = normalizeString(sourceKind);
      if (explicit) return explicit;
      if (fromHistoryPlayFlag) return 'history-list';
      if (fromHistoryDetail) return 'history-detail';
      if (!this.isTmdbMode && !this.selectedSiteResultItem) return 'direct-site';
      return 'site-detail';
    },
    resetSwitchSkipState() {
      this.switchSkipByEpisodeKey = {};
      this.activePlayerControlAction = '';
    },
    getSwitchSkippedRecordsForEpisode(episodeKey) {
      const key = normalizeString(episodeKey);
      if (!key) return [];
      const store = this.switchSkipByEpisodeKey && typeof this.switchSkipByEpisodeKey === 'object'
        ? this.switchSkipByEpisodeKey
        : {};
      const list = store[key];
      if (!Array.isArray(list)) return [];
      return list
        .map((item) => this.normalizeSwitchSkipRecord(item))
        .filter(Boolean)
        .map((item) => ({ ...item }));
    },
    setSwitchSkippedRecordsForEpisode(episodeKey, records) {
      const key = normalizeString(episodeKey);
      if (!key) return;
      const nextRecords = Array.isArray(records)
        ? records.map((item) => this.normalizeSwitchSkipRecord(item)).filter(Boolean)
        : [];
      this.switchSkipByEpisodeKey = {
        ...(this.switchSkipByEpisodeKey && typeof this.switchSkipByEpisodeKey === 'object' ? this.switchSkipByEpisodeKey : {}),
        [key]: nextRecords,
      };
    },
    buildSwitchSkipIndexForEpisode(episodeKey) {
      const key = normalizeString(episodeKey);
      const crossSiteSet = new Set();
      const strictSiteSet = new Set();
      if (!key) return { crossSiteSet, strictSiteSet };
      const records = this.getSwitchSkippedRecordsForEpisode(key);
      records.forEach((record) => {
        const file = normalizeString(record && record.fileIdentity);
        const panFlag = this.normalizeSwitchPanFlag(record && record.panFlag);
        const siteKey = normalizeString(record && record.siteKey);
        const siteDetail = normalizeString(record && record.siteDetail);
        const panProvider = normalizeString(record && record.panProvider)
          || this.resolveSwitchPanProviderKey(this.resolveSwitchPanFamilyLabel(panFlag));
        if (!file || !panFlag || !siteKey || !siteDetail) return;
        const cross = SWITCH_CROSS_SITE_PROVIDERS.has(panProvider);
        if (cross) {
          crossSiteSet.add(`${panFlag}::${file}`);
          return;
        }
        strictSiteSet.add(`${siteKey}::${siteDetail}::${panFlag}::${file}`);
      });
      return { crossSiteSet, strictSiteSet };
    },
    shouldSkipSwitchCandidate(episodeKey, wrapper, prebuiltIndex = null) {
      const key = normalizeString(episodeKey);
      if (!key) return false;
      const candidateInfo = this.buildSwitchSkipInfo(wrapper);
      const candidateFile = normalizeString(candidateInfo && candidateInfo.fileIdentity);
      const candidatePanFlag = this.normalizeSwitchPanFlag(candidateInfo && candidateInfo.panFlag);
      const candidateProvider = normalizeString(candidateInfo && candidateInfo.panProvider);
      const candidateSiteKey = normalizeString(candidateInfo && candidateInfo.siteKey);
      const candidateSiteDetail = normalizeString(candidateInfo && candidateInfo.siteDetail);
      if (!candidateFile || !candidatePanFlag || !candidateSiteKey || !candidateSiteDetail) return false;
      const index = prebuiltIndex && typeof prebuiltIndex === 'object'
        ? prebuiltIndex
        : this.buildSwitchSkipIndexForEpisode(key);
      const candidateCrossSite = SWITCH_CROSS_SITE_PROVIDERS.has(candidateProvider);
      if (candidateCrossSite) {
        return !!(index.crossSiteSet && index.crossSiteSet.has(`${candidatePanFlag}::${candidateFile}`));
      }
      return !!(index.strictSiteSet && index.strictSiteSet.has(`${candidateSiteKey}::${candidateSiteDetail}::${candidatePanFlag}::${candidateFile}`));
    },
    appendSwitchSkippedCandidate(episodeKey, wrapper) {
      const key = normalizeString(episodeKey);
      const record = this.normalizeSwitchSkipRecord(this.buildSwitchSkipInfo(wrapper));
      if (!key || !record) return false;
      const current = this.getSwitchSkippedRecordsForEpisode(key);
      const exists = current.some((item) => this.isSameSwitchSkipRecord(item, record));
      if (exists) return true;
      this.setSwitchSkippedRecordsForEpisode(key, current.concat(record));
      return true;
    },
    recordCurrentPlaybackIntoSwitchSkipBucket(globalEpisodeOverride = 0) {
      const globalEpisode = Math.max(0, normalizeInt(globalEpisodeOverride));
      const episodeKey = globalEpisode > 0
        ? this.buildSwitchEpisodeKeyByGlobal(globalEpisode)
        : this.buildSwitchEpisodeKey();
      if (!episodeKey) return false;
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      const siteKey = normalizeString(playback && playback.siteKey);
      const siteDetail = normalizeString(playback && playback.siteDetail);
      const panFlag = this.normalizeSwitchPanFlag(normalizeString(playback && playback.panFlag));
      const fileIdentity = normalizeString(playback && playback.fileIdentity);
      if (!siteKey || !siteDetail || !panFlag || !fileIdentity) return false;
      return this.appendSwitchSkippedCandidate(episodeKey, {
        siteKey,
        siteDetail,
        panFlag,
        fileIdentity,
      });
    },
    recordResolvedPayloadIntoSwitchSkipBucket(payload) {
      const episodeKey = this.buildSwitchEpisodeKey();
      const target = payload && typeof payload === 'object' ? payload : null;
      const siteItem = target && target.siteItem && typeof target.siteItem === 'object' ? target.siteItem : null;
      const segment = target && target.segment && typeof target.segment === 'object' ? target.segment : null;
      const fileIdentity = buildSegmentPlaybackIdentity(segment);
      if (!episodeKey || !siteItem || !fileIdentity) return;
      this.appendSwitchSkippedCandidate(episodeKey, {
        siteItem,
        fileIdentity,
      });
    },
    clearPlayerActionToast() {
      if (this.playerTransientToastTimer) {
        window.clearTimeout(this.playerTransientToastTimer);
        this.playerTransientToastTimer = 0;
      }
      this.playerTransientToastText = '';
      this.playerTransientToastSticky = false;
    },
    showPlayerActionToast(text, { sticky = false, durationMs = 1800 } = {}) {
      const nextText = normalizeString(text);
      if (!nextText) return;
      this.clearPlayerActionToast();
      this.playerTransientToastText = nextText;
      this.playerTransientToastSticky = !!sticky;
      if (this.playerTransientToastSticky) return;
      const ttl = Math.max(400, normalizeInt(durationMs) || 1800);
      this.playerTransientToastTimer = window.setTimeout(() => {
        this.playerTransientToastTimer = 0;
        this.playerTransientToastText = '';
        this.playerTransientToastSticky = false;
      }, ttl);
    },
    keepPlayerActionSearchingToast() {
      this.showPlayerActionToast('正在搜索片源...', { sticky: true });
    },
    startPlayerActionSearchFlow(actionTargetKey = '') {
      this.playerUiTransitionMode = 'switch';
      this.playerActionPendingTargetKey = normalizeString(actionTargetKey);
      this.clearPlayerActionToast();
      this.keepPlayerActionSearchingToast();
      this.playError = '';
      this.playerRuntimeError = '';
    },
    shouldIgnorePlayerControlAction({ action = '', value = '', suppressStatusUi = false, actionTargetKey = '' } = {}) {
      const searchingSameTarget = !!(
        suppressStatusUi
        && this.playerTransientToastSticky
        && actionTargetKey
        && normalizeString(this.playerActionPendingTargetKey) === actionTargetKey
      );
      if (searchingSameTarget) {
        this.keepPlayerActionSearchingToast();
        return true;
      }
      const currentPanFamily = normalizeString(this.currentPlaybackPanFamilyLabel);
      const currentQualityKey = normalizeString(this.currentPlaybackQualityKey);
      if (action === 'pan' && value && value === currentPanFamily) return true;
      if (action === 'quality' && value && value === currentQualityKey) return true;
      return false;
    },
    finalizePlayerReadyState({ markFirstFrame = false } = {}) {
      this.playLoading = false;
      this.playError = '';
      this.playerRuntimeError = '';
      this.playerPlaybackStarted = true;
      if (markFirstFrame) {
        this.playerBuffering = false;
        this.playerFirstFrameReady = true;
        this.playRequestStage = '';
      }
      this.closePlayerActionFlow();
    },
    resetPlayerReadyState() {
      this.playerMetaReady = false;
      this.playerFirstFrameReady = false;
      this.playerPlaybackStarted = false;
      this.playerBuffering = false;
    },
    beginExplicitPlaybackTransition(stage = 'play_url') {
      this.playerUiTransitionMode = '';
      this.clearPlayerActionToast();
      this.playerUrl = '';
      this.playerHeaders = {};
      this.playLoading = false;
      this.playError = '';
      this.playerRuntimeError = '';
      this.playRequestStage = normalizeString(stage) || 'play_url';
      this.lastGoProxyCandidate = null;
      this.lastM3U8RelayCandidate = null;
      this.resetPlayerReadyState();
    },
    closePlayerActionFlow({ toast = '', sticky = false, durationMs = 1800, clearErrors = false } = {}) {
      this.activePlayerControlAction = '';
      this.playerUiTransitionMode = '';
      this.playerActionPendingTargetKey = '';
      if (this.playerTransientToastSticky) this.clearPlayerActionToast();
      if (clearErrors) {
        this.playError = '';
        this.playerRuntimeError = '';
      }
      const text = normalizeString(toast);
      if (text) this.showPlayerActionToast(text, { sticky, durationMs });
    },
    buildPlayerActionTargetKey({ globalEpisode = 0, actionKey = '', selectedValue = '' } = {}) {
      const ep = Math.max(0, normalizeInt(globalEpisode));
      const action = normalizeString(actionKey);
      const value = normalizeString(selectedValue) || '*';
      if (!ep || !action) return '';
      return `${ep}::${action}::${value}`;
    },
    getNoMatchPlayerActionText(actionKey) {
      const action = normalizeString(actionKey);
      if (action === 'pan') return '暂无匹配网盘片源';
      if (action === 'quality') return '暂无匹配画质片源';
      return '未匹配到相关片源';
    },
    buildPlayerActionCandidateAllowed(action = '', globalEpisode = 0) {
      const normalizedAction = normalizeString(action);
      const switchEpisodeKey = this.buildSwitchEpisodeKeyByGlobal(globalEpisode);
      const skipIndex = this.buildSwitchSkipIndexForEpisode(switchEpisodeKey);
      return (wrapper) => {
        if (normalizedAction !== 'switch' && normalizedAction !== 'primary') return true;
        const blocked = this.shouldSkipSwitchCandidate(switchEpisodeKey, wrapper, skipIndex);
        return !blocked;
      };
    },
    bindPlaySearchQuerySubscription() {
      this.unbindPlaySearchQuerySubscription();
      const query = normalizeSearchKey(this.playSearchQueryOriginal);
      const scope = normalizeString(this.playSearchPrimaryScope) || this.playSearchScope;
      if (!query) {
        this.playSearchLiveSnapshot = null;
        this.playSearchLiveStatus = 'idle';
        return;
      }
      this.playSearchLiveSnapshot = getSearchSessionLaneSnapshot(query, scope, 'site');
      this.playSearchLiveStatus = getSearchSessionLaneStatus(query, scope, 'site');
      this.playSearchUnsubscribe = subscribeSearchSessionLane(query, scope, 'site', (snapshot, status) => {
        if (normalizeSearchKey(this.playSearchQueryOriginal) !== query) return;
        if ((normalizeString(this.playSearchPrimaryScope) || this.playSearchScope) !== scope) return;
        this.playSearchLiveSnapshot = snapshot;
        this.playSearchLiveStatus = normalizeString(status) || 'idle';
      });
    },
    unbindPlaySearchQuerySubscription() {
      if (typeof this.playSearchUnsubscribe === 'function') {
        this.playSearchUnsubscribe();
      }
      this.playSearchUnsubscribe = null;
    },
    async ensurePlaySearchRuntimeConfig() {
      if (this.playSearchRuntimeConfigData) return this.playSearchRuntimeConfigData;
      const config = await ensureSearchSessionConfig(this.bootstrap).catch(() => null);
      this.playSearchRuntimeConfigData = config || null;
      return this.playSearchRuntimeConfigData;
    },
    async ensurePlayBlockedMatchIndexLoaded() {
      const query = normalizeString(this.playSearchQueryOriginal || this.playSearchQuery || this.displayTitle);
      if (!query) {
        this.playBlockedMatchIndex = {};
        return this.playBlockedMatchIndex;
      }
      const runtimeConfig = await this.ensurePlaySearchRuntimeConfig();
      const aggregateRules = runtimeConfig && Array.isArray(runtimeConfig.aggregateRules)
        ? runtimeConfig.aggregateRules
        : [];
      const next = await fetchBlockedMatchIndex(query, aggregateRules).catch(() => ({}));
      this.playBlockedMatchIndex = next && typeof next === 'object' ? next : {};
      return this.playBlockedMatchIndex;
    },
    async handleSmartMatchBlockUpdated(event) {
      const detail = event && event.detail && typeof event.detail === 'object' ? event.detail : null;
      const keyword = normalizeString(this.playSearchQueryOriginal || this.playSearchQuery || this.displayTitle);
      const nextKeyword = normalizeString(detail && detail.keyword);
      if (nextKeyword && keyword && nextKeyword !== keyword) return;
      clearBlockedMatchCaches();
      await this.ensurePlayBlockedMatchIndexLoaded();
    },
    async loadPlayRuntimeAndDetail() {
      this.historySmartBootstrapStageDone = {};
      this.manualSmartBootstrapStageDone = {};
      this.autoplayConsumed = false;
      this.autoplayInFlight = false;
      this.smartPlaybackResolvedStage = '';
      this.syncPrimarySiteSourceOptions();
      await this.ensurePlayRuntimeSettings();
      await this.ensurePlayBlockedSiteKeysLoaded();
      await this.ensurePlaySearchRuntimeConfig();
      await this.ensurePlayBlockedMatchIndexLoaded();
      if (this.isTmdbMode && !this.tmdbMovieMode) {
        await this.loadDetailData();
        this.syncSmartEpisodeMapping();
        this.$nextTick(() => this.syncPlaybackDisplayFocus());
        return;
      }
      await this.loadDetailData();
      this.syncSmartEpisodeMapping();
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    syncPrimarySiteSourceOptions() {
      if (this.isTmdbMode) {
        this.siteSourceOptions = this.showDoubanPrimarySourceOption ? ['TMDB', '豆瓣'] : ['TMDB'];
        if (!normalizeString(this.selectedSiteSource) || !this.siteSourceOptions.includes(this.selectedSiteSource)) {
          this.selectedSiteSource = 'TMDB';
        }
        return;
      }
      const siteLabel = normalizeString(this.siteName) || '站点';
      this.siteSourceOptions = [siteLabel];
      this.selectedSiteSource = siteLabel;
    },
    toggleSiteSourceMenu() {
      this.siteSourceOpen = !this.siteSourceOpen;
      if (this.siteSourceOpen) this.panSourceOpen = false;
    },
    togglePanSourceMenu() {
      this.panSourceOpen = !this.panSourceOpen;
      if (this.panSourceOpen) this.siteSourceOpen = false;
    },
    async selectSiteSource(option) {
      if (!this.isTmdbMode) {
        this.siteSourceOpen = false;
        return;
      }
      if (this.selectedSiteResultItem && this.currentPanSourceEntry) {
        patchLastBrowsePlaybackContext({
          itemId: normalizeString(this.selectedSiteResultItem.id),
          panKey: normalizeString(this.currentPanSourceEntry.key),
        });
      }
      const hadSelectedSiteResult = !!this.selectedSiteResultItem;
      this.selectedSiteSource = option;
      this.siteSourceOpen = false;
      this.selectedSearchResultId = '';
      this.selectedPanSource = '';
      this.siteResultDetailData = null;
      this.siteResultDetailLoading = false;
      this.siteResultDetailError = '';
      this.applyEpisodeViewModePreference();
      if (hadSelectedSiteResult && this.isTmdbMode) {
        this.$nextTick(() => this.syncPlaybackDisplayFocus());
        return;
      }
      if (option === '豆瓣') {
        await this.ensureTVMetaLoaded();
      }
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    toggleProjectionSource() {
      if (!this.selectedSiteResultItem || !this.isTmdbMode) return;
      const options = this.projectionSourceOptions;
      if (!Array.isArray(options) || options.length <= 1) return;
      const current = normalizeString(this.selectedProjectionSource);
      const idx = options.findIndex((item) => item === current);
      const next = options[(idx + 1 + options.length) % options.length] || options[0];
      this.selectedProjectionSource = normalizeString(next) || 'TMDB';
      this.selectedViewSeasonNumber = 0;
      this.selectedViewRangeStart = 0;
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    async triggerSiteSourceSearchMore() {
      if (!this.playSearchQueryOriginal || !this.siteSourceSearchInteractive) return;
      await this.ensurePlayBlockedSiteKeysLoaded();
      this.siteSourceSearchManualLoading = true;
      try {
        if (this.playSearchPrimaryStatus !== 'completed') {
          await performSearchSessionSearch(this.playSearchQueryOriginal, this.bootstrap, {
            saveHistoryEnabled: false,
            blockedSiteKeys: this.playBlockedSiteKeys,
            affectUi: false,
            scope: this.playSearchPrimaryScope,
            searchDisplayModeOverride: 'sites',
            contentKind: this.playSearchContentKind,
          });
        }
      } finally {
        this.siteSourceSearchManualLoading = false;
      }
    },
    async ensurePlayBlockedSiteKeysLoaded() {
      const raw = this.runtimeSettings && Array.isArray(this.runtimeSettings.smartSkipSiteKeys)
        ? this.runtimeSettings.smartSkipSiteKeys
        : [];
      this.playBlockedSiteKeys = raw.map((item) => normalizeString(item)).filter(Boolean);
    },
    selectSiteSourceResult(item) {
      if (!item || !item.id) return;
      this.selectedSearchResultId = String(item.id);
      this.selectedProjectionSource = 'TMDB';
      this.selectedPanSource = '';
      this.selectedSiteEpisodeSelectionKey = '';
      this.applyEpisodeViewModePreference();
      this.siteSourceOpen = false;
      this.loadSelectedSiteResultDetail(item);
      patchLastBrowsePlaybackContext({
        itemId: normalizeString(item.id),
        panKey: '',
      });
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    selectPanSource(option) {
      this.selectedPanSource = option;
      this.panSourceOpen = false;
      if (this.selectedSiteResultItem) {
        patchLastBrowsePlaybackContext({
          itemId: normalizeString(this.selectedSiteResultItem.id),
          panKey: normalizeString(option),
        });
      }
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    syncCurrentPlaybackContextFromPage({
      siteItem,
      panEntry,
      segment,
      candidate,
      selectionKey,
      globalEpisode = 0,
      sourceKind = '',
      fromHistoryPlayFlag = false,
      fromHistoryDetail = false,
    } = {}) {
      const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
      const pan = panEntry && typeof panEntry === 'object' ? panEntry : null;
      const seg = segment && typeof segment === 'object' ? segment : null;
      const cand = candidate && typeof candidate === 'object' ? candidate : null;
      const nextSelectionKey = normalizeString(selectionKey);
      const nextSourceKind = this.resolvePlaybackSourceKind({
        sourceKind,
        fromHistoryPlayFlag,
        fromHistoryDetail,
      });
      const nextSiteKey = normalizeString(item && item.siteKey);
      const nextVideoId = normalizeString(item && item.siteDetail);
      const nextPanKey = normalizeString(pan && pan.key);
      const nextItemIndex = normalizeInt(seg && seg.index);
      const namingContext = buildStatsNamingContext(
        pan,
        getPanEntrySegments(pan),
        this.displayTitle,
      );
      const nextPathName = namingContext.preferFile ? resolvePlayerStatsPathName({ segment: seg, candidate: cand }) : '';
      const nextRawFileName = pickRawFileNameForStats(seg && seg.displayName, seg && seg.rawName, namingContext);
      const nextFileIdentity = buildSegmentPlaybackIdentity(seg);
      patchCurrentPlaybackContext({
        itemId: normalizeString(item && item.id),
        siteKey: nextSiteKey,
        siteName: normalizeString(item && item.siteName),
        spiderApi: normalizeString(item && item.spiderApi),
        siteDetail: nextVideoId,
        panKey: nextPanKey,
        panFlag: normalizeString(pan && pan.label),
        selectionKey: nextSelectionKey,
        globalEpisode: Math.max(0, normalizeInt(globalEpisode)),
        itemIndex: nextItemIndex,
        sourceQuality: normalizeString(cand && cand.quality),
        quality: normalizeString(cand && cand.quality),
        pathName: normalizeString(nextPathName),
        rawFileName: nextRawFileName,
        fileIdentity: nextFileIdentity,
        sourceKind: nextSourceKind,
        sourceSignature: this.buildPlaybackSourceSignature({
          siteKey: nextSiteKey,
          siteDetail: nextVideoId,
          panKey: nextPanKey,
          itemIndex: nextItemIndex,
        }),
      });
      this.selectedSiteEpisodeSelectionKey = nextSelectionKey;
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    selectSiteEpisodeFile(index, { globalEpisode = 0 } = {}) {
      const item = this.activeSitePlaybackItem;
      const pan = this.currentPanSourceEntry;
      const segment = this.buildCurrentPanSegment(index);
      const candidate = this.getCurrentPanRecognitionCandidate(index);
      if (!item || !pan || !segment || !normalizeString(segment.episodeUrl)) return null;
      const selectionKey = buildSiteEpisodeSelectionKey(pan.key, segment.index);
      if (!selectionKey) return null;
      this.syncPlayerStatsForSegment({ item, pan, segment });
      this.selectedSiteEpisodeSelectionKey = selectionKey;
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
      return { item, pan, segment, candidate, selectionKey };
    },
    isEpisodeItemInteractive(item) {
      return !this.isEpisodeButtonActive(item);
    },
    isRawListFileInteractive(item) {
      const entry = item && typeof item === 'object' ? item : null;
      if (!entry || normalizeString(entry.kind) !== 'file') return true;
      return !this.isCurrentRawListPlaybackSelection(entry);
    },
    isTmdbMovieCandidateInteractive(item) {
      return !this.isCurrentTmdbMovieCandidateSelection(item);
    },
    async onTmdbMovieCandidateClick(item) {
      if (!this.isTmdbMovieCandidateInteractive(item)) return;
      const target = item && typeof item === 'object' ? item : null;
      const siteItem = target && target.siteItem && typeof target.siteItem === 'object' ? target.siteItem : null;
      const panEntry = target && target.panEntry && typeof target.panEntry === 'object'
        ? target.panEntry
        : (() => {
          const detail = siteItem ? this.getCachedSiteResultDetail(siteItem) : null;
          const panKey = normalizeString(target && target.panKey);
          return detail
            ? this.buildPanSourcesFromDetail(detail).find((entry) => normalizeString(entry && entry.key) === panKey) || null
            : null;
        })();
      const itemIndex = normalizeInt(target && target.itemIndex);
      const segment = target && target.segment && typeof target.segment === 'object'
        ? target.segment
        : this.buildPanSegment(panEntry, itemIndex);
      if (!siteItem || !panEntry || itemIndex < 0 || !segment || !normalizeString(segment.episodeUrl)) return;
      this.beginExplicitPlaybackTransition('play_url');
      await this.playResolvedSiteSegment({
        siteItem,
        panEntry,
        segment,
        candidate: target && target.candidate ? target.candidate : null,
        selectionKey: normalizeString(target && target.selectionKey) || buildSiteEpisodeSelectionKey(panEntry.key, itemIndex),
        globalEpisode: 0,
      });
    },
    toggleRawList() {
      if (!this.activeSitePlaybackItem || this.effectiveForceRawListMode) return;
      this.rawListMode = !this.rawListMode;
      this.persistEpisodeViewModePreference();
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    applyEpisodeViewModePreference() {
      if (!this.activeSitePlaybackItem) {
        this.rawListMode = false;
        return;
      }
      if (this.effectiveForceRawListMode) {
        this.rawListMode = true;
        return;
      }
      this.rawListMode = this.readEpisodeViewModePreference() === 'raw';
    },
    readEpisodeViewModePreference() {
      const mode = normalizeString(readPlayLocalStorage(EP_VIEW_MODE_STORAGE_KEY)).toLowerCase();
      return mode === 'raw' || mode === 'episodes' ? mode : '';
    },
    persistEpisodeViewModePreference() {
      if (this.effectiveForceRawListMode) return;
      writePlayLocalStorage(EP_VIEW_MODE_STORAGE_KEY, this.rawListMode ? 'raw' : 'episodes');
    },
    onRawListItemClick(item) {
      if (!item) return;
      if (this.rawDirModeEnabled && item.kind === 'dir' && item.name) {
        const current = Array.isArray(this.rawDirPath) ? this.rawDirPath : [];
        this.rawDirPath = current.concat([normalizeString(item.name)]);
        this.$nextTick(() => this.ensureActivePlaybackRowVisible());
        return;
      }
      if (!this.isRawListFileInteractive(item)) return;
      if (item.kind === 'file') {
        const targetGlobalEpisode = this.getCurrentPanSegmentGlobalEpisode(item.index);
        const selected = this.selectSiteEpisodeFile(item.index, {
          globalEpisode: targetGlobalEpisode,
        });
        if (!selected) return;
        this.beginExplicitPlaybackTransition('play_url');
        void this.playSiteResultItemByIndex(item.index, targetGlobalEpisode);
      }
    },
    onEpisodeItemClick(item) {
      if (!this.isEpisodeItemInteractive(item)) return;
      if (!this.selectedSiteResultItem && this.isTmdbMode) {
        this.resetSmartPlaybackRuntimeState({ stopStream: true });
        this.beginExplicitPlaybackTransition('detail');
        void this.playPrimaryEpisodeItem(item);
        return;
      }
      const itemIndex = normalizeInt(item && item.itemIndex);
      if (itemIndex < 0) return;
      const selected = this.selectSiteEpisodeFile(itemIndex, {
        globalEpisode: normalizeInt(item && item.global),
      });
      if (!selected) return;
      this.beginExplicitPlaybackTransition('play_url');
      void this.playSiteResultItemByIndex(itemIndex, normalizeInt(item && item.global));
    },
    buildPanSegment(entry, index) {
      return buildPanSegmentRuntime(entry, index);
    },
    buildCurrentPanSegment(index) {
      return this.buildPanSegment(this.currentPanSourceEntry, index);
    },
    buildPanSourcesFromDetail(detail) {
      return buildPanSourcesFromDetailRuntime(detail);
    },
    buildSiteSourceResultItemsFromSnapshot(snapshot) {
      return buildSiteSourceResultItemsFromSnapshotRuntime({
        snapshot,
        runtimeConfig: this.playSearchRuntimeConfig,
        blockedSiteKeys: this.playBlockedSiteKeys,
        blockedMatchIndex: this.playBlockedMatchIndex,
        title: this.displayTitle,
        contentKind: this.playSearchContentKind,
      });
    },
    getPlayMatchBlockEntry(siteKey, siteDetail) {
      const key = `${normalizeString(siteKey)}::${normalizeString(siteDetail)}`;
      if (!key || key === '::') return null;
      const index = this.playBlockedMatchIndex && typeof this.playBlockedMatchIndex === 'object'
        ? this.playBlockedMatchIndex
        : {};
      const entry = index[key];
      return entry && typeof entry === 'object' ? entry : null;
    },
    isPlaybackCandidateBlockedByMatchBlock(wrapper) {
      const item = wrapper && wrapper.siteItem && typeof wrapper.siteItem === 'object' ? wrapper.siteItem : null;
      const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
      const source = candidate && candidate.source && typeof candidate.source === 'object' ? candidate.source : null;
      const siteKey = normalizeString(source && source.siteKey) || normalizeString(item && item.siteKey);
      const siteDetail = normalizeString(source && source.siteDetail) || normalizeString(item && item.siteDetail);
      const entry = this.getPlayMatchBlockEntry(siteKey, siteDetail);
      if (!entry) return false;
      if (entry.blockAll) return true;
      const panFlag = normalizeString(candidate && candidate.panFlag)
        || normalizeString(source && source.panFlag)
        || normalizeString(wrapper && wrapper.panFlag);
      if (!panFlag) return false;
      const blockedPanFlags = Array.isArray(entry.panFlags) ? entry.panFlags : [];
      return blockedPanFlags.some((itemFlag) => normalizeString(itemFlag) === panFlag);
    },
    isPlaybackCandidateBlockedBySiteSkip(wrapper) {
      const blocked = Array.isArray(this.playBlockedSiteKeys) ? this.playBlockedSiteKeys : [];
      if (!blocked.length) return false;
      const item = wrapper && wrapper.siteItem && typeof wrapper.siteItem === 'object' ? wrapper.siteItem : null;
      const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
      const source = candidate && candidate.source && typeof candidate.source === 'object' ? candidate.source : null;
      const siteKey = normalizeString(source && source.siteKey) || normalizeString(item && item.siteKey);
      if (!siteKey) return false;
      return blocked.includes(siteKey);
    },
    buildUnifiedSmartCandidateAllowed(extraAllowed = null) {
      return (wrapper) => {
        if (this.isPlaybackCandidateBlockedBySiteSkip(wrapper)) return false;
        if (this.isPlaybackCandidateBlockedByMatchBlock(wrapper)) return false;
        if (typeof extraAllowed !== 'function') return true;
        try {
          return !!extraAllowed(wrapper);
        } catch (_error) {
          return true;
        }
      };
    },
    isPlaybackCandidate4K(wrapper) {
      const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
      if (!candidate) return false;
      const qualityKey = normalizeString(resolveCandidateQualityModeKeyForPlayback(candidate));
      if (qualityKey === '4k' || qualityKey === '4k_hdr' || qualityKey === '4k_fps') return true;
      const hay = [
        normalizeString(candidate.displayName),
        normalizeString(candidate.rawName),
        normalizeString(candidate.fileName),
        normalizeString(candidate.quality),
      ].join(' ').toLowerCase();
      return /(?:\b4k\b|2160p|2160|uhd)/i.test(hay);
    },
    isSmartPlaybackFullDetailCompleted() {
      return this.playSearchPrimaryStatus === 'completed';
    },
    buildSmartPlaybackActionAllowed(actionConstraint = null, extraAllowed = null) {
      const unifiedAllowed = this.buildUnifiedSmartCandidateAllowed(extraAllowed);
      return (wrapper) => {
        if (!unifiedAllowed(wrapper)) return false;
        if (!isPlaybackCandidateAllowedByAction(wrapper, actionConstraint, this.runtimeSettings, this.currentPlaybackContext)) return false;
        const constraint = actionConstraint && typeof actionConstraint === 'object' ? actionConstraint : null;
        const mode = normalizeString(constraint && constraint.mode);
        if (mode === 'pan' && !this.isSmartPlaybackFullDetailCompleted()) {
          return this.isPlaybackCandidate4K(wrapper);
        }
        if (mode === 'quality' && !this.isSmartPlaybackFullDetailCompleted()) {
          const currentPanFamily = normalizeString(constraint && constraint.currentPanFamily);
          if (!currentPanFamily) return true;
          const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
          const family = normalizeString(resolveCandidatePanFamilyForPlayback(candidate, this.runtimeSettings));
          return !!family && family === currentPanFamily;
        }
        return true;
      };
    },
    buildSmartPlaybackCompareCandidates(actionConstraint = null) {
      return (left, right) => {
        const constraint = actionConstraint && typeof actionConstraint === 'object' ? actionConstraint : null;
        const mode = normalizeString(constraint && constraint.mode);
        let primary = 0;
        if (mode === 'primary') {
          const currentPanFamily = normalizeString(constraint && constraint.currentPanFamily);
          const currentQualityKey = normalizeString(constraint && constraint.currentQualityKey);
          const historyPanFlag = normalizeString(constraint && constraint.historyPanFlag);
          const score = (wrapper) => {
            const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
            if (!candidate) return 0;
            const family = normalizeString(resolveCandidatePanFamilyForPlayback(candidate, this.runtimeSettings));
            const qualityKey = normalizeString(resolveCandidateQualityModeKeyForPlayback(candidate));
            const panFlag = normalizeString(candidate.panFlag)
              || normalizeString(candidate && candidate.source && candidate.source.panFlag)
              || normalizeString(wrapper && wrapper.panFlag);
            const hitPan = !!currentPanFamily && !!family && family === currentPanFamily;
            const hitQuality = !!currentQualityKey && !!qualityKey && qualityKey === currentQualityKey;
            if (hitPan && hitQuality) return 300;
            if (hitPan) return 200;
            if (hitQuality) return 100;
            if (historyPanFlag && panFlag === historyPanFlag) return 50;
            return 0;
          };
          const leftScore = score(left);
          const rightScore = score(right);
          if (leftScore !== rightScore) primary = leftScore > rightScore ? -1 : 1;
        } else {
          primary = constraint && mode !== 'default' && mode !== 'switch'
            ? comparePlaybackCandidatesForAction(left, right, constraint, this.currentPlaybackContext, this.runtimeSettings)
            : comparePlaybackCandidatesByDefaultRules(left, right, this.runtimeSettings);
        }
        if (primary !== 0) return primary;
        const fallback = comparePlaybackCandidatesByDefaultRules(left, right, this.runtimeSettings);
        if (fallback !== 0) return fallback;
        const leftLoose = !!(left && left.looseMatch);
        const rightLoose = !!(right && right.looseMatch);
        if (leftLoose !== rightLoose) return leftLoose ? 1 : -1;
        const leftOrder = normalizeInt(left && left.itemIndex);
        const rightOrder = normalizeInt(right && right.itemIndex);
        if (leftOrder !== rightOrder) return leftOrder - rightOrder;
        return normalizeString(left && left.panKey).localeCompare(normalizeString(right && right.panKey), 'zh');
      };
    },
    syncPlayerStatsForResolvedSegment({ siteResultItem, pan, segment, candidate }) {
      const namingContext = buildStatsNamingContext(
        pan,
        getPanEntrySegments(pan),
        this.displayTitle,
      );
      this.playerStatsSiteName = buildPlaybackSiteLabel(siteResultItem);
      this.playerStatsPanName = normalizeString(pan && pan.label);
      this.playerStatsPathName = namingContext.preferFile ? resolvePlayerStatsPathName({ segment, candidate }) : '';
      this.playerStatsRawFileName = pickRawFileNameForStats(segment && segment.displayName, segment && segment.rawName, namingContext);
    },
    buildPlayHistoryBaseContext() {
      const isTmdb = !!this.isTmdbMode;
      const baseContentKey = isTmdb
        ? normalizeString(this.playContentPreferenceKey)
        : (normalizeString(this.playContentPreferenceKey) || normalizeString(this.displayTitle));
      if (!baseContentKey) return {};
      return {
        contentKey: baseContentKey,
        Poster: this.detailPoster,
        Remark: isTmdb ? this.detailRemarkText : this.historyRemarkText,
        tmdbId: isTmdb ? normalizeInt(this.tmdbId) : 0,
        tmdbType: isTmdb ? normalizeString(this.tmdbType || this.searchType).toLowerCase() : '',
        tmdbSeason: 0,
      };
    },
    buildPlayHistoryWarmContext() {
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      const isTmdb = !!this.isTmdbMode;
      const baseContext = this.buildPlayHistoryBaseContext();
      if (!normalizeString(baseContext && baseContext.contentKey)) return {};
      const existingRow = findPlayHistoryRowForContext(baseContext) || null;
      const playbackGlobalEpisode = Math.max(0, normalizeInt(playback && playback.globalEpisode));
      const fallbackSiteEpisodeIndex = normalizeInt(playback && playback.itemIndex) >= 0
        ? normalizeInt(playback && playback.itemIndex) + 1
        : 0;
      const mergedContext = {
        contentKey: normalizeString(existingRow && existingRow.contentKey) || baseContext.contentKey,
        reportEnabled: isTmdb,
        siteKey: normalizeString(existingRow && existingRow.siteKey)
          || normalizeString(this.siteKey)
          || normalizeString(playback && playback.siteKey),
        siteName: normalizeString(existingRow && existingRow.siteName)
          || normalizeString(this.siteName)
          || normalizeString(playback && playback.siteName),
        spiderApi: normalizeString(existingRow && existingRow.spiderApi)
          || normalizeString(this.spiderApi)
          || normalizeString(playback && playback.spiderApi),
        siteDetail: normalizeString(existingRow && existingRow.siteDetail)
          || normalizeString(this.siteDetail)
          || normalizeString(playback && playback.siteDetail),
        Poster: normalizeString(existingRow && existingRow.Poster) || baseContext.Poster,
        Remark: normalizeString(existingRow && existingRow.Remark) || baseContext.Remark,
        tmdbId: isTmdb
          ? (normalizeInt(existingRow && existingRow.tmdbId) || baseContext.tmdbId)
          : normalizeInt(existingRow && existingRow.tmdbId),
        tmdbType: isTmdb
          ? (normalizeString(existingRow && existingRow.tmdbType) || baseContext.tmdbType)
          : normalizeString(existingRow && existingRow.tmdbType),
        tmdbSeason: normalizeInt(existingRow && existingRow.tmdbSeason),
        tmdbEpisode: normalizeInt(existingRow && existingRow.tmdbEpisode),
        globalEpisode: normalizeInt(existingRow && existingRow.globalEpisode) || playbackGlobalEpisode,
        playFlag: normalizeString(existingRow && existingRow.playFlag) || normalizeString(playback && playback.panFlag),
        siteEpisodeIndex: normalizeInt(existingRow && existingRow.siteEpisodeIndex) || fallbackSiteEpisodeIndex,
        siteEpisodeFile: normalizeString(existingRow && existingRow.siteEpisodeFile) || normalizeString(playback && playback.rawFileName),
        playbackItemId: normalizeString(existingRow && existingRow.playbackItemId),
        selectionKey: normalizeString(existingRow && existingRow.selectionKey) || normalizeString(playback && playback.selectionKey),
        preOrder: typeof (existingRow && existingRow.preOrder) === 'boolean'
          ? !!existingRow.preOrder
          : false,
      };
      return mergedContext;
    },
    async togglePreOrder() {
      if (this.preOrderToggleBusy || !this.showPreOrderButton) return;
      const context = this.buildPlayHistoryWarmContext();
      if (!normalizeString(context.contentKey) || normalizeInt(context.tmdbId) <= 0 || normalizeString(context.tmdbType) !== 'tv') return;
      this.preOrderToggleBusy = true;
      try {
        await setPlayHistoryPreOrder(context, !this.preOrderActive);
      } catch (error) {
        this.playError = error && error.message ? String(error.message) : '点映状态更新失败';
      } finally {
        this.preOrderToggleBusy = false;
      }
    },
    syncPlaybackContextFromHistoryRow(row) {
      const item = row && typeof row === 'object' ? row : null;
      if (!item) return;
      const nextGlobalEpisode = this.getHistoryRowGlobalEpisode(item);
      const nextPlayFlag = normalizeString(item.playFlag);
      const nextSelectionKey = normalizeString(item.selectionKey);
      const nextSiteEpisodeIndex = Math.max(0, normalizeInt(item.siteEpisodeIndex) - 1);
      const nextSiteEpisodeFile = normalizeString(item.siteEpisodeFile);
      patchCurrentPlaybackContext({
        globalEpisode: nextGlobalEpisode,
        panFlag: nextPlayFlag,
        selectionKey: nextSelectionKey,
        itemIndex: nextSiteEpisodeIndex,
        rawFileName: nextSiteEpisodeFile,
      });
      this.$nextTick(() => this.syncPlaybackDisplayFocus());
    },
    syncHistoryDisplayContextIfReady() {
      if (normalizeString(this.playerUrl) || this.playLoading) return;
      const row = this.playHistoryRowForMenu;
      if (!row) return;
      if (this.isTmdbMode) {
        if (this.tmdbMovieMode || this.detailLoading || !(this.detailTMDBData && typeof this.detailTMDBData === 'object')) return;
        this.syncPlaybackContextFromHistoryRow(row);
        return;
      }
      if (this.selectedSiteResultItem) {
        if (!this.siteResultDetailData || this.selectedSitePanState.loading || !this.currentPanSourceOptions.length || !this.rawListItems.length) return;
        this.syncPlaybackContextFromHistoryRow(row);
        return;
      }
      if (this.detailLoading || !this.currentPanSourceOptions.length || !this.rawListItems.length) return;
      this.syncPlaybackContextFromHistoryRow(row);
    },
    async prefetchPlayHistoryForPage() {
      const context = this.buildPlayHistoryWarmContext();
      if (!normalizeString(context.contentKey) && (!normalizeString(context.siteKey) || !normalizeString(context.siteDetail))) return;
      await warmPlayHistoryForContext(context, { limit: 50 });
    },
    parseHistoryPlaybackItemId(row) {
      const value = normalizeString(row && row.playbackItemId);
      if (!value) return { season: 0, episode: 0 };
      const match = value.match(/^tmdb_tv_\d+_s(\d{2})_e(\d{3})$/i);
      if (!match) return { season: 0, episode: 0 };
      return {
        season: normalizeInt(match[1]),
        episode: normalizeInt(match[2]),
      };
    },
    getHistoryRowGlobalEpisode(row) {
      const item = row && typeof row === 'object' ? row : null;
      if (!item) return 0;
      const direct = Math.max(0, normalizeInt(item.globalEpisode));
      if (direct > 0) return direct;
      const parsed = this.parseHistoryPlaybackItemId(item);
      const season = Math.max(0, normalizeInt(item.tmdbSeason)) || Math.max(0, normalizeInt(parsed.season));
      const episode = Math.max(0, normalizeInt(item.tmdbEpisode)) || Math.max(0, normalizeInt(parsed.episode));
      if (season > 0 && episode > 0) {
        return tmdbGlobalEpisodeNoOf(this.detailTMDBData, season, episode);
      }
      return 0;
    },
    async focusPrimaryEpisodeByGlobal(globalEpisode) {
      const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
      if (targetGlobal <= 0) return null;
      const target = tmdbSeasonEpisodeOfGlobal(this.detailTMDBData, targetGlobal);
      const season = Math.max(0, normalizeInt(target && target.season));
      const episode = Math.max(0, normalizeInt(target && target.episode));
      if (season > 0 && this.episodeSeasonRows.length > 1 && season !== this.currentEpisodeSeasonNumber) {
        this.selectedViewSeasonNumber = season;
      }
      await this.$nextTick();
      if (episode > 0 && this.episodeRangeOptions.length > 1) {
        const range = this.episodeRangeOptions.find((item) => episode >= item.start && episode <= item.end) || null;
        if (range && normalizeInt(range.start) !== this.currentEpisodeRangeStart) {
          this.selectedViewRangeStart = range.start;
        }
      }
      await this.$nextTick();
      return this.episodeButtons.find((item) => normalizeInt(item && item.text) === episode) || null;
    },
    normalizeHistoryEpisodeName(value) {
      return normalizeString(value).replace(/\s+/g, '').toLowerCase();
    },
    async resolveInitialSitePlaybackTarget(row) {
      const panOptions = Array.isArray(this.currentPanSourceOptions) ? this.currentPanSourceOptions : [];
      if (!panOptions.length) return null;
      const history = row && typeof row === 'object' ? row : null;
      const wantedPlayFlag = normalizeString(history && history.playFlag);
      const wantedSelectionKey = normalizeString(history && history.selectionKey);
      const wantedEpisodeName = this.normalizeHistoryEpisodeName(history && history.siteEpisodeFile);
      const wantedIndex = Math.max(0, normalizeInt(history && history.siteEpisodeIndex) - 1);
      const wantPanNorm = this.normalizeHistoryEpisodeName(wantedPlayFlag);
      let panKey = normalizeString(this.selectedPanSource);
      if (wantPanNorm) {
        const hit = panOptions.find((item) => {
          const key = this.normalizeHistoryEpisodeName(item && item.key);
          const label = this.normalizeHistoryEpisodeName(item && item.label);
          return !!wantPanNorm && (wantPanNorm === key || wantPanNorm === label);
        }) || null;
        if (hit) panKey = normalizeString(hit.key);
      }
      if (!panKey) panKey = normalizeString(panOptions[0] && panOptions[0].key);
      if (!panKey) return null;
      if (normalizeString(this.selectedPanSource) !== panKey) {
        this.selectedPanSource = panKey;
        await this.$nextTick();
      }
      const list = Array.isArray(this.rawListItems) ? this.rawListItems : [];
      if (!list.length) return null;
      let item = null;
      if (wantedSelectionKey) {
        item = list.find((entry) => normalizeString(entry && entry.selectionKey) === wantedSelectionKey) || null;
      }
      if (!item && wantedEpisodeName) {
        item = list.find((entry) => this.normalizeHistoryEpisodeName(entry && entry.text) === wantedEpisodeName) || null;
      }
      if (!item && wantedIndex >= 0) {
        item = list.find((entry) => normalizeInt(entry && entry.index) === wantedIndex) || null;
      }
      if (!item) item = list.find((entry) => entry && entry.kind === 'file') || null;
      if (!item) return null;
      return {
        panKey,
        itemIndex: normalizeInt(item.index),
      };
    },
    getSmartPlaybackStageConfig(preferredStage = '') {
      const stage = normalizeString(preferredStage);
      if (this.isTmdbMode && this.tmdbMovieMode) {
        return {
          stage: 'movie',
          mapping: null,
          mappingSignature: TMDB_MOVIE_RECOGNITION_SIGNATURE,
          episodeSource: this.selectedSiteSource,
          matchOptions: this.buildSmartMatchOptions(),
        };
      }
      if (stage === 'full') {
        return this.canRunFullSmartPlayback
          ? {
            stage: 'full',
            mapping: this.fullEpisodeMapping,
            mappingSignature: this.smartEpisodeMappingSignature,
            episodeSource: this.selectedSiteSource,
          }
          : null;
      }
      if (this.canRunFullSmartPlayback && !normalizeString(this.playerUrl) && this.smartPlaybackResolvedStage !== 'full') {
        return {
          stage: 'full',
          mapping: this.fullEpisodeMapping,
          mappingSignature: this.smartEpisodeMappingSignature,
          episodeSource: this.selectedSiteSource,
        };
      }
      return null;
    },
    isAutoplaySmartLaunchSuccessful() {
      return !!(
        normalizeString(this.playerUrl)
        || this.playLoading
        || normalizeInt(this.smartPlaybackPendingRunSeq) > 0
        || normalizeInt(this.smartPlaybackAttemptRunSeq) > 0
      );
    },
    computeAutoplayState() {
      const row = this.playHistoryRowForMenu;
      const intent = row ? 'history' : 'default';
      if (this.selectedSiteResultItem) {
        return { mode: 'disabled', intent, ready: false, blockedBy: 'selected-site-result' };
      }
      if (this.isTmdbMode) {
        const mode = this.tmdbMovieMode ? 'tmdb-movie' : 'tmdb-tv';
        if (this.detailLoading) return { mode, intent, ready: false, blockedBy: 'detail-loading' };
        if (!(this.detailTMDBData && typeof this.detailTMDBData === 'object')) {
          return { mode, intent, ready: false, blockedBy: 'tmdb-detail-missing' };
        }
        if (this.tmdbMovieMode && !this.getSmartPlaybackStageConfig()) {
          return { mode, intent, ready: false, blockedBy: 'stage-config-pending' };
        }
        if (!this.tmdbMovieMode && !this.episodeButtons.length) {
          return { mode, intent, ready: false, blockedBy: 'episode-buttons-empty' };
        }
        return { mode, intent, ready: true, blockedBy: '' };
      }
      if (this.detailLoading) return { mode: 'site-primary', intent, ready: false, blockedBy: 'detail-loading' };
      if (!this.currentPanSourceOptions.length) {
        return { mode: 'site-primary', intent, ready: false, blockedBy: 'pan-options-empty' };
      }
      if (!this.episodeButtons.length && !this.rawListItems.length) {
        return { mode: 'site-primary', intent, ready: false, blockedBy: 'site-items-empty' };
      }
      return { mode: 'site-primary', intent, ready: true, blockedBy: '' };
    },
    async resolveAutoplayTarget() {
      const state = this.computeAutoplayState();
      if (!state.ready) return null;
      const row = await ensurePlayHistoryRowForContext(this.buildPlayHistoryWarmContext(), { limit: 50 });
      if (state.mode === 'tmdb-movie') {
        return {
          kind: 'tmdb-movie',
          stageConfig: this.getSmartPlaybackStageConfig(),
        };
      }
      if (state.mode === 'tmdb-tv') {
        const targetGlobal = state.intent === 'history' ? this.getHistoryRowGlobalEpisode(row) : 1;
        let item = await this.focusPrimaryEpisodeByGlobal(targetGlobal);
        if (!item && state.intent === 'default') item = this.episodeButtons[0] || null;
        if (!item) return null;
        return {
          kind: 'tmdb-tv',
          item,
          globalEpisode: Math.max(1, normalizeInt(targetGlobal) || 1),
        };
      }
      const target = await this.resolveInitialSitePlaybackTarget(row);
      if (this.episodeButtons.length) {
        const episodeItem = target && normalizeInt(target.itemIndex) >= 0
          ? this.episodeButtons.find((item) => normalizeInt(item && item.itemIndex) === normalizeInt(target.itemIndex)) || null
          : null;
        const pickedEpisode = episodeItem || this.episodeButtons[0] || null;
        if (!pickedEpisode) return null;
        return {
          kind: 'site-episode',
          item: pickedEpisode,
        };
      }
      const rawFileItem = target && normalizeInt(target.itemIndex) >= 0
        ? (this.rawListItems.find((item) => item && item.kind === 'file' && normalizeInt(item.index) === normalizeInt(target.itemIndex)) || null)
        : null;
      const pickedRawItem = rawFileItem
        || (Array.isArray(this.rawListItems)
          ? this.rawListItems.find((item) => item && item.kind === 'file' && normalizeInt(item.index) >= 0) || null
          : null);
      if (!pickedRawItem) return null;
      return {
        kind: 'site-raw',
        item: pickedRawItem,
      };
    },
    async runAutoplayTarget(target) {
      if (!target || typeof target !== 'object') return false;
      if (target.kind === 'tmdb-tv') {
        this.resetSmartPlaybackRuntimeState({ stopStream: true });
        patchCurrentPlaybackContext({
          globalEpisode: Math.max(1, normalizeInt(target.globalEpisode) || 1),
        });
        this.$nextTick(() => this.syncPlaybackDisplayFocus());
        this.beginExplicitPlaybackTransition('detail');
        return !!(await this.playPrimaryEpisodeItem(target.item));
      }
      if (target.kind === 'tmdb-movie') {
        this.resetSmartPlaybackRuntimeState({ stopStream: true });
        this.beginExplicitPlaybackTransition('detail');
        await this.runSmartPlaybackWithConstraints({
          globalEpisode: 0,
          wantEpisodeInSeason: 0,
          matchOptions: target.stageConfig && target.stageConfig.matchOptions
            ? target.stageConfig.matchOptions
            : this.buildSmartMatchOptions(),
          stage: target.stageConfig && target.stageConfig.stage,
          mapping: target.stageConfig && target.stageConfig.mapping,
          mappingSignature: target.stageConfig && target.stageConfig.mappingSignature,
          episodeSource: target.stageConfig && target.stageConfig.episodeSource,
        });
        return this.isAutoplaySmartLaunchSuccessful();
      }
      if (target.kind === 'site-episode') {
        const itemIndex = normalizeInt(target.item && target.item.itemIndex);
        if (itemIndex < 0) return false;
        const selected = this.selectSiteEpisodeFile(itemIndex, {
          globalEpisode: normalizeInt(target.item && target.item.global),
        });
        if (!selected) return false;
        this.beginExplicitPlaybackTransition('play_url');
        return !!(await this.playSiteResultItemByIndex(itemIndex, normalizeInt(target.item && target.item.global)));
      }
      if (target.kind === 'site-raw') {
        const itemIndex = normalizeInt(target.item && target.item.index);
        if (itemIndex < 0) return false;
        const targetGlobalEpisode = this.getCurrentPanSegmentGlobalEpisode(itemIndex);
        const selected = this.selectSiteEpisodeFile(itemIndex, {
          globalEpisode: targetGlobalEpisode,
        });
        if (!selected) return false;
        this.beginExplicitPlaybackTransition('play_url');
        return !!(await this.playSiteResultItemByIndex(itemIndex, targetGlobalEpisode));
      }
      return false;
    },
    async tryAutoplayOnce() {
      if (this.autoplayConsumed || this.autoplayInFlight) return;
      const state = this.computeAutoplayState();
      if (!state.ready) return;
      this.autoplayInFlight = true;
      try {
        const target = await this.resolveAutoplayTarget();
        if (!target) return;
        const ok = await this.runAutoplayTarget(target);
        if (ok) this.autoplayConsumed = true;
      } finally {
        this.autoplayInFlight = false;
      }
    },
    scheduleAutoplayCheck() {
      this.$nextTick(() => {
        void this.tryAutoplayOnce();
      });
    },
    async ensureSiteHistoryResolvedMeta() {
      if (this.isTmdbMode) {
        return {
          contentKey: normalizeString(this.playContentPreferenceKey),
          tmdbId: Math.max(0, normalizeInt(this.tmdbId)),
          tmdbType: normalizeString(this.tmdbType || this.searchType).toLowerCase(),
        };
      }
      const baseTitle = normalizeString(this.playContentPreferenceKey) || normalizeString(this.displayTitle);
      const kind = this.contentKind === 'movie' ? 'movie' : 'tv';
      const cleanedTitle = stripSearchAliasMarkers(baseTitle, { contentKind: kind });
      const settings = await this.ensurePlayRuntimeSettings();
      const aggregateRules = settings && Array.isArray(settings.magicAggregateRegexRules)
        ? settings.magicAggregateRegexRules
        : [];
      const configCleanedTitle = buildSmartMatchBlockKeyword(cleanedTitle || baseTitle, aggregateRules);
      const queryTitle = normalizeSearchKey(configCleanedTitle || cleanedTitle || baseTitle);
      if (!queryTitle) return { contentKey: '', tmdbId: 0, tmdbType: '' };
      const cachedByTitle = normalizeString(this.siteHistoryResolvedByTitle);
      const cachedMeta = this.siteHistoryResolvedMeta && typeof this.siteHistoryResolvedMeta === 'object'
        ? this.siteHistoryResolvedMeta
        : null;
      if (cachedByTitle === queryTitle && cachedMeta) return { ...cachedMeta };
      let nextMeta = { contentKey: queryTitle, tmdbId: 0, tmdbType: '', tmdbPoster: '', tmdbRemark: '' };
      try {
        const payload = await apiGetJson(`/api/tmdb/resolve${buildQuery({ type: kind, title: queryTitle })}`, {
          cacheMs: 0,
          dedupe: true,
        });
        const id = Math.max(0, normalizeInt(payload && payload.id));
        if (id > 0) {
          const detail = await fetchTMDBDetailCached({ type: kind, id });
          const detailTitle = normalizeString(getTMDBDetailTitle(detail, kind));
          nextMeta = {
            contentKey: normalizeSearchKey(detailTitle || queryTitle),
            tmdbId: id,
            tmdbType: kind,
            tmdbPoster: normalizeString(getTMDBPosterPath(detail)),
            tmdbRemark: normalizeString(buildTMDBDetailTextBadge(detail, kind)),
          };
        }
      } catch (_error) {
        // keep fallback path
      }
      this.siteHistoryResolvedByTitle = queryTitle;
      this.siteHistoryResolvedMeta = { ...nextMeta };
      return nextMeta;
    },
    buildSiteModeTMDBBindingForSegment({ segment, resolvedMeta, panEntry = null } = {}) {
      const meta = resolvedMeta && typeof resolvedMeta === 'object' ? resolvedMeta : null;
      const tmdbId = Math.max(0, normalizeInt(meta && meta.tmdbId));
      const tmdbType = normalizeString(meta && meta.tmdbType).toLowerCase();
      if (tmdbId <= 0 || (tmdbType !== 'tv' && tmdbType !== 'movie')) {
        return { tmdbId: 0, tmdbType: '', tmdbSeason: 0, tmdbEpisode: 0 };
      }
      if (tmdbType === 'movie') {
        return { tmdbId, tmdbType, tmdbSeason: 0, tmdbEpisode: 0 };
      }
      const entry = panEntry && typeof panEntry === 'object'
        ? panEntry
        : (this.currentPanSourceEntry && typeof this.currentPanSourceEntry === 'object' ? this.currentPanSourceEntry : null);
      const runtimeSettings = this.playRuntimeSettings && typeof this.playRuntimeSettings === 'object'
        ? this.playRuntimeSettings
        : {};
      const targetIndex = Math.max(0, normalizeInt(segment && segment.index));
      const parsedItems = buildDirectSiteEpisodeItems(entry, runtimeSettings);
      const matched = parsedItems.find((item) => normalizeInt(item && item.itemIndex) === targetIndex) || null;
      const episode = Math.max(0, normalizeInt(matched && matched.no));
      const season = Math.max(0, normalizeInt(matched && matched.season)) || (episode > 0 ? 1 : 0);
      if (season <= 0 || episode <= 0) {
        return { tmdbId: 0, tmdbType: '', tmdbSeason: 0, tmdbEpisode: 0 };
      }
      return { tmdbId, tmdbType, tmdbSeason: season, tmdbEpisode: episode };
    },
    buildPlayHistoryPayloadForResolvedSegment({
      siteItem,
      pan,
      segment,
      selectionKey,
      globalEpisode = 0,
      contentKeyOverride = '',
      siteTMDBBinding = null,
      resolvedMeta = null,
    } = {}) {
      const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      const requestedGlobalEpisode = Math.max(0, normalizeInt(globalEpisode));
      const resolvedGlobalEpisode = requestedGlobalEpisode || Math.max(0, normalizeInt(playback && playback.globalEpisode));
      const canReportTMDBHistory = !!this.isTmdbMode;
      const nextSiteKey = normalizeString(item && item.siteKey)
        || normalizeString(this.siteKey)
        || normalizeString(playback && playback.siteKey);
      const nextSpiderApi = normalizeString(item && item.spiderApi)
        || normalizeString(this.spiderApi)
        || normalizeString(playback && playback.spiderApi);
      const nextSiteDetail = normalizeString(item && item.siteDetail)
        || normalizeString(playback && playback.siteDetail)
        || normalizeString(this.siteDetail);
      const tmdbTarget = canReportTMDBHistory && resolvedGlobalEpisode > 0
        ? tmdbSeasonEpisodeOfGlobal(this.detailTMDBData, resolvedGlobalEpisode)
        : null;
      const siteBinding = siteTMDBBinding && typeof siteTMDBBinding === 'object'
        ? siteTMDBBinding
        : { tmdbId: 0, tmdbType: '', tmdbSeason: 0, tmdbEpisode: 0 };
      const meta = resolvedMeta && typeof resolvedMeta === 'object' ? resolvedMeta : null;
      return buildPlayHistoryPayload({
        contentKey: normalizeString(contentKeyOverride)
          || normalizeString(this.playContentPreferenceKey)
          || normalizeString(this.displayTitle),
        reportEnabled: canReportTMDBHistory
          ? (this.tmdbMovieMode || resolvedGlobalEpisode > 0)
          : (!!nextSiteKey && !!nextSpiderApi && !!nextSiteDetail),
        siteKey: nextSiteKey,
        siteName: normalizeString(item && item.siteName)
          || normalizeString(this.siteName)
          || normalizeString(playback && playback.siteName),
        spiderApi: nextSpiderApi,
        siteDetail: nextSiteDetail,
        Poster: canReportTMDBHistory
          ? this.detailPoster
          : (normalizeString(meta && meta.tmdbPoster) || this.detailPoster),
        Remark: canReportTMDBHistory
          ? this.historyRemarkText
          : (normalizeString(meta && meta.tmdbRemark) || this.historyRemarkText),
        tmdbId: canReportTMDBHistory ? this.tmdbId : Math.max(0, normalizeInt(siteBinding.tmdbId)),
        tmdbType: canReportTMDBHistory
          ? (this.tmdbType || this.searchType)
          : normalizeString(siteBinding.tmdbType).toLowerCase(),
        tmdbSeason: canReportTMDBHistory
          ? normalizeInt(tmdbTarget && tmdbTarget.season)
          : Math.max(0, normalizeInt(siteBinding.tmdbSeason)),
        tmdbEpisode: canReportTMDBHistory
          ? normalizeInt(tmdbTarget && tmdbTarget.episode)
          : Math.max(0, normalizeInt(siteBinding.tmdbEpisode)),
        globalEpisode: resolvedGlobalEpisode,
        playFlag: normalizeString(pan && pan.label),
        siteEpisodeIndex: Math.max(1, normalizeInt(segment && segment.index) + 1),
        siteEpisodeFile: pickRawFileNameForStats(segment && segment.displayName, segment && segment.rawName),
        selectionKey,
        preOrder: this.preOrderActive,
      });
    },
    async applyPlayHistoryResume(reason = '') {
      const seconds = await onPlayerHistoryPlaybackStart(reason);
      if (seconds > 0) {
        await this.$nextTick();
        const art = this.$refs.artPlayerRef;
        if (art && typeof art.seekTo === 'function') {
          try {
            art.seekTo(seconds);
          } catch (_error) {}
        }
      }
      await confirmPlayerHistoryPlaybackReady(reason);
    },
    isEpisodeButtonActive(episode) {
      const item = episode && typeof episode === 'object' ? episode : null;
      if (!item) return false;
      if (this.selectedSiteResultItem) {
        return this.isCurrentProjectedPlaybackSelection(item);
      }
      const target = this.currentPrimaryPlaybackEpisodeTarget;
      if (!target) return false;
      if (this.episodeSeasonRows.length > 1 && this.currentEpisodeSeasonNumber !== normalizeInt(target.season)) {
        return false;
      }
      if (this.episodeRangeOptions.length > 1) {
        const currentRange = this.episodeRangeOptions.find((range) => range.start === this.currentEpisodeRangeStart) || null;
        const targetEpisode = normalizeInt(target.episode);
        if (!currentRange || targetEpisode < normalizeInt(currentRange.start) || targetEpisode > normalizeInt(currentRange.end)) {
          return false;
        }
      }
      return normalizeInt(item && item.text) === normalizeInt(target.episode);
    },
    isCurrentProjectedPlaybackSelection(target) {
      const playback = this.currentPlaybackContext;
      if (!this.selectedSiteResultItem) return false;
      const item = target && typeof target === 'object'
        ? target
        : { selectionKey: target };
      const currentPan = this.currentPanSourceEntry;
      const currentPanFlag = normalizeString(currentPan && currentPan.label);
      const playbackPanFlag = normalizeString(playback && playback.panFlag);
      if (!currentPanFlag || currentPanFlag !== playbackPanFlag) return false;
      const playbackItemIndex = normalizeInt(playback && playback.itemIndex);
      const itemIndex = normalizeInt(item && item.itemIndex);
      return playbackItemIndex >= 0 && itemIndex >= 0 && playbackItemIndex === itemIndex;
    },
    isCurrentTmdbMovieCandidateSelection(target) {
      const item = target && typeof target === 'object' ? target : null;
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      if (!item || !playback) return false;
      const playbackSiteKey = normalizeString(playback.siteKey);
      const playbackVideoId = normalizeString(playback.siteDetail);
      const itemSiteKey = normalizeString(item.siteKey);
      const itemVideoId = normalizeString(item.siteDetail);
      if (!playbackSiteKey || !playbackVideoId || playbackSiteKey !== itemSiteKey || playbackVideoId !== itemVideoId) return false;
      const playbackPanKey = normalizeString(playback.panKey);
      const itemPanKey = normalizeString(item.panKey);
      if (playbackPanKey && itemPanKey && playbackPanKey !== itemPanKey) return false;
      const playbackPanFlag = normalizeString(playback.panFlag);
      const itemPanFlag = normalizeString(item.panFlag);
      if (!playbackPanKey && playbackPanFlag && itemPanFlag && playbackPanFlag !== itemPanFlag) return false;
      const playbackIndex = normalizeInt(playback.itemIndex);
      const itemIndex = normalizeInt(item.itemIndex);
      if (playbackIndex >= 0 && itemIndex >= 0 && playbackIndex === itemIndex) return true;
      const playbackSelectionKey = normalizeString(playback.selectionKey);
      const itemSelectionKey = normalizeString(item.selectionKey);
      if (playbackSelectionKey && itemSelectionKey && playbackSelectionKey === itemSelectionKey) return true;
      const playbackFileIdentity = normalizeString(playback.fileIdentity);
      const itemFileIdentity = normalizeString(item.fileIdentity);
      return !!playbackFileIdentity && !!itemFileIdentity && playbackFileIdentity === itemFileIdentity;
    },
    isCurrentRawListPlaybackSelection(item) {
      const entry = item && typeof item === 'object' ? item : null;
      if (!entry || entry.kind !== 'file') return false;
      const currentPan = this.currentPanSourceEntry;
      const currentPanFlag = normalizeString(currentPan && currentPan.label);
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      const playbackPanFlag = normalizeString(playback && playback.panFlag);
      if (!currentPanFlag || currentPanFlag !== playbackPanFlag) return false;
      const playbackIndex = normalizeInt(playback && playback.itemIndex);
      const entryIndex = normalizeInt(entry.index);
      if (playbackIndex < 0 || entryIndex < 0 || playbackIndex !== entryIndex) return false;
      if (!this.rawDirModeEnabled) return true;
      const viewingPath = normalizeString(this.rawListDisplayPath);
      const playbackPath = normalizeString(this.playerStatsPathName);
      return !!viewingPath && !!playbackPath && viewingPath === playbackPath;
    },
    ensureActivePlaybackRowVisible() {
      this.$nextTick(() => {
        const root = this.$el && this.$el.querySelector('.play-episode-content');
        if (!root) return;
        const active = root.querySelector('.play-raw-list__row--active, .play-episode-btn--active');
        if (!active) return;
        const container = findScrollableEpisodeContainer(active, root);
        if (!container) return;
        ensureElementVisibleInContainer(active, container);
      });
    },
    syncPlaybackDisplayFocus() {
      const playback = this.currentPlaybackContext;
      if (this.selectedSiteResultItem) {
        const targetItem = this.projectedSiteEpisodeItems.find((item) => this.isCurrentProjectedPlaybackSelection(item)) || null;
        if (!this.rawListMode) {
          if (targetItem) {
            const season = normalizeInt(targetItem && targetItem.season);
            const no = normalizeInt(targetItem && targetItem.no);
            if (season > 0 && this.episodeSeasonRows.length > 1 && season !== this.currentEpisodeSeasonNumber) {
              this.selectedViewSeasonNumber = season;
            }
            if (no > 0 && this.projectedRangeOptions.length > 1) {
              const hit = this.projectedRangeOptions.find((item) => no >= item.start && no <= item.end) || null;
              if (hit && normalizeInt(hit.start) !== this.currentEpisodeRangeStart) {
                this.selectedViewRangeStart = hit.start;
              }
            }
          }
          this.ensureActivePlaybackRowVisible();
          return;
        }
        this.ensureActivePlaybackRowVisible();
        return;
      }
      if (this.showTmdbMovieCandidateList) {
        const hit = this.tmdbMovieCandidateItems.find((item) => this.isCurrentTmdbMovieCandidateSelection(item)) || null;
        if (hit) this.ensureActivePlaybackRowVisible();
        return;
      }
      const target = this.currentPrimaryPlaybackEpisodeTarget;
      if (!target) return;
      if (this.episodeSeasonRows.length > 1 && normalizeInt(target.season) !== this.currentEpisodeSeasonNumber) {
        this.selectedViewSeasonNumber = normalizeInt(target.season);
      }
      if (this.episodeRangeOptions.length > 1) {
        const hit = this.episodeRangeOptions.find((item) => normalizeInt(target.episode) >= item.start && normalizeInt(target.episode) <= item.end) || null;
        if (hit && normalizeInt(hit.start) !== this.currentEpisodeRangeStart) {
          this.selectedViewRangeStart = hit.start;
        }
      }
      this.ensureActivePlaybackRowVisible();
    },
    getSiteItemByIdFromSnapshot(itemId) {
      const targetId = normalizeString(itemId);
      if (!targetId) return null;
      const current = this.selectedSiteResultItem;
      if (current && normalizeString(current.id) === targetId) return current;
      const items = Array.isArray(this.siteSourceResultItems) ? this.siteSourceResultItems : [];
      return items.find((item) => normalizeString(item && item.id) === targetId) || null;
    },
    getCurrentPanSegmentGlobalEpisode(index) {
      const hit = this.getCurrentPanRecognitionCandidate(index);
      const mappedGlobalEpisode = Math.max(0, normalizeInt(hit && hit.mapping && hit.mapping.global));
      if (mappedGlobalEpisode > 0) return mappedGlobalEpisode;
      return this.getSingleSeasonFallbackGlobalEpisode(index);
    },
    getSingleSeasonFallbackGlobalEpisode(index) {
      const itemIndex = normalizeInt(index);
      if (itemIndex < 0 || !this.isTmdbMode || this.tmdbMovieMode) return 0;
      const rows = Array.isArray(this.tmdbBaseSeasonRows) ? this.tmdbBaseSeasonRows : [];
      if (rows.length !== 1) return 0;
      const episodeTotal = Math.max(0, normalizeInt(rows[0] && rows[0].episodes));
      if (episodeTotal <= 0) return 0;
      const candidateGlobal = itemIndex + 1;
      if (candidateGlobal <= 0 || candidateGlobal > episodeTotal) return 0;
      return candidateGlobal;
    },
    getCurrentPanRecognitionCandidate(index) {
      const itemIndex = normalizeInt(index);
      if (itemIndex < 0) return null;
      const data = this.playbackRecognitionData && typeof this.playbackRecognitionData === 'object'
        ? this.playbackRecognitionData
        : null;
      const items = Array.isArray(data && data.items) ? data.items : [];
      return items.find((item) => normalizeInt(item && item.itemIndex) === itemIndex) || null;
    },
    async findNextPrimaryEpisodeButton() {
      const currentGlobal = Math.max(0, normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.globalEpisode));
      if (currentGlobal <= 0) return null;
      return this.focusPrimaryEpisodeByGlobal(currentGlobal + 1);
    },
    async findPreviousPrimaryEpisodeButton() {
      const currentGlobal = Math.max(0, normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.globalEpisode));
      if (currentGlobal <= 1) return null;
      return this.focusPrimaryEpisodeByGlobal(currentGlobal - 1);
    },
    findProjectedSiteEpisodeItemByDelta(deltaRaw) {
      const delta = Number(deltaRaw) > 0 ? 1 : (Number(deltaRaw) < 0 ? -1 : 0);
      if (!delta) return null;
      const list = Array.isArray(this.projectedSiteEpisodeItems) ? this.projectedSiteEpisodeItems : [];
      if (!list.length) return null;
      const currentGlobal = Math.max(0, normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.globalEpisode));
      if (currentGlobal > 0 && currentGlobal + delta > 0) {
        const exact = list.find((item) => normalizeInt(item && item.global) === currentGlobal + delta) || null;
        if (exact) return exact;
      }
      const currentItemIndex = normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.itemIndex);
      const currentIndex = list.findIndex((item) => normalizeInt(item && item.itemIndex) === currentItemIndex);
      if (currentIndex < 0) return null;
      const targetIndex = currentIndex + delta;
      if (targetIndex < 0 || targetIndex >= list.length) return null;
      return list[targetIndex] || null;
    },
    findNextProjectedSiteEpisodeItem() {
      return this.findProjectedSiteEpisodeItemByDelta(1);
    },
    findPreviousProjectedSiteEpisodeItem() {
      return this.findProjectedSiteEpisodeItemByDelta(-1);
    },
    async playSiteListItemByIndex(itemIndex, globalEpisode = 0) {
      const targetIndex = normalizeInt(itemIndex);
      if (targetIndex < 0) return false;
      const targetGlobalEpisode = Math.max(0, normalizeInt(globalEpisode));
      const selected = this.selectSiteEpisodeFile(targetIndex, {
        globalEpisode: targetGlobalEpisode,
      });
      if (!selected) return false;
      this.beginExplicitPlaybackTransition('play_url');
      await this.playSiteResultItemByIndex(targetIndex, targetGlobalEpisode);
      return true;
    },
    async playPrimaryEpisodeByDelta(deltaRaw) {
      const delta = Number(deltaRaw) > 0 ? 1 : (Number(deltaRaw) < 0 ? -1 : 0);
      if (!delta) return false;
      const targetPrimary = delta > 0
        ? await this.findNextPrimaryEpisodeButton()
        : await this.findPreviousPrimaryEpisodeButton();
      if (!targetPrimary) return false;
      this.beginExplicitPlaybackTransition('detail');
      await this.playPrimaryEpisodeItem(targetPrimary);
      return true;
    },
    playSiteListByDelta(deltaRaw) {
      const delta = Number(deltaRaw) > 0 ? 1 : (Number(deltaRaw) < 0 ? -1 : 0);
      if (!delta) return Promise.resolve(false);
      const currentIndex = normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.itemIndex);
      const targetIndex = currentIndex + delta;
      return this.playSiteListItemByIndex(targetIndex, this.getCurrentPanSegmentGlobalEpisode(targetIndex));
    },
    async playFromCurrentContextByDelta(deltaRaw) {
      const delta = Number(deltaRaw) > 0 ? 1 : (Number(deltaRaw) < 0 ? -1 : 0);
      if (!delta) return false;
      if (this.selectedSiteResultItem) {
        if (!this.rawListMode) {
          const projectedTarget = this.findProjectedSiteEpisodeItemByDelta(delta);
          if (!projectedTarget) return false;
          return this.playSiteListItemByIndex(
            normalizeInt(projectedTarget.itemIndex),
            normalizeInt(projectedTarget.global),
          );
        }
        return this.playSiteListByDelta(delta);
      }
      if (this.isTmdbMode) {
        return this.playPrimaryEpisodeByDelta(delta);
      }
      return this.playSiteListByDelta(delta);
    },
    async playPreviousFromCurrentContext() {
      return this.playFromCurrentContextByDelta(-1);
    },
    async playNextFromCurrentContext() {
      return this.playFromCurrentContextByDelta(1);
    },
    async switchEpisodeByDelta(deltaRaw, { showBoundaryToast = true } = {}) {
      const delta = Number(deltaRaw) > 0 ? 1 : (Number(deltaRaw) < 0 ? -1 : 0);
      if (!delta) return false;
      const ok = delta > 0
        ? await this.playNextFromCurrentContext()
        : await this.playPreviousFromCurrentContext();
      if (ok || !showBoundaryToast) return ok;
      this.showPlayerActionToast(delta > 0 ? '已经是最后一集' : '已经是第一集');
      return false;
    },
    resolveCachedPlaybackTarget(globalEpisode, wantEpisodeInSeason = 0, {
      matchOptions = null,
      actionConstraint = null,
      isCandidateAllowed = null,
      mapping = null,
      mappingSignature = '',
      episodeSource = '',
      includeSelectedContext = true,
      includeBrowseContext = true,
      includeStoreScan = true,
    } = {}) {
      const unifiedAllowed = this.buildSmartPlaybackActionAllowed(actionConstraint, isCandidateAllowed);
      const signature = normalizeString(mappingSignature)
        || (this.isTmdbMode && this.tmdbMovieMode ? TMDB_MOVIE_RECOGNITION_SIGNATURE : normalizeString(this.smartEpisodeMappingSignature));
      return resolveCachedPlaybackTargetRuntime({
        matchOptions,
        globalEpisode,
        wantEpisodeInSeason,
        selectedSiteResultItem: includeSelectedContext ? this.selectedSiteResultItem : null,
        currentPanSourceEntry: includeSelectedContext ? this.currentPanSourceEntry : null,
        includeSelectedContext,
        lastResolvedPlaybackContext: this.currentPlaybackContext,
        lastBrowsePlaybackContext: playbackSessionState.lastBrowseContext,
        includeBrowseContext,
        getSiteItemById: (itemId) => this.getSiteItemByIdFromSnapshot(itemId),
        detailStore: this.siteResultDetailStore,
        collectCandidates: (siteItem, nextGlobal, nextLoose, nextMatchOptions) =>
          this.collectRecognitionCandidatesForTarget(siteItem, nextGlobal, nextLoose, {
            matchOptions: nextMatchOptions || matchOptions,
            mappingSignature: signature,
            episodeSource: normalizeString(episodeSource) || this.selectedSiteSource,
            allowResolutionModes: this.buildAllowedResolutionModes(),
          }),
        buildSelectionKey: (panKey, index) => buildSiteEpisodeSelectionKey(panKey, index),
        isCandidateAllowed: unifiedAllowed,
        compareCandidates: this.buildSmartPlaybackCompareCandidates(actionConstraint),
        includeStoreScan,
        ensureRecognitionForSiteItem: (siteItem, detail) => {
          this.cacheRecognitionForSiteResult(siteItem, detail, {
            mapping,
            mappingSignature: signature,
          });
        },
      });
    },
    async tryHistorySmartBootstrap(globalEpisode, wantEpisodeInSeason = 0, {
      matchOptions = null,
      actionConstraint = null,
      isCandidateAllowed = null,
      stage = '',
      mapping = null,
      mappingSignature = '',
      episodeSource = '',
      skipHistoryList = false,
      onCandidatesChanged = null,
      isRunStopped = null,
    } = {}) {
      const stopped = () => (typeof isRunStopped === 'function' ? !!isRunStopped() : false);
      const stageKey = normalizeString(stage) || 'default';
      const rawStageState = this.historySmartBootstrapStageDone[stageKey];
      const stageState = rawStageState && typeof rawStageState === 'object'
        ? rawStageState
        : (rawStageState ? { done: true, detailDone: true } : { done: false, detailDone: false });
      if (stageState.done && stageState.detailDone) return false;
      if (!this.isTmdbMode) {
        this.historySmartBootstrapStageDone = {
          ...this.historySmartBootstrapStageDone,
          [stageKey]: { done: true, detailDone: true },
        };
        return false;
      }

      const signature = normalizeString(mappingSignature)
        || (this.isTmdbMode && this.tmdbMovieMode ? TMDB_MOVIE_RECOGNITION_SIGNATURE : normalizeString(this.smartEpisodeMappingSignature));
      const unifiedAllowed = this.buildSmartPlaybackActionAllowed(actionConstraint, isCandidateAllowed);
      const compareCandidates = this.buildSmartPlaybackCompareCandidates(actionConstraint);
      const targetEpisodeSource = normalizeString(episodeSource) || this.selectedSiteSource;
      const targetMapping = mapping || this.smartEpisodeMapping;
      const forceDetailOnlyByStage = stageState.done && !stageState.detailDone;
      let detailRequested = !!forceDetailOnlyByStage;
      let hasHistoryContext = false;
      let hasHistoryCandidates = false;
      const notifyCandidatesChanged = () => {
        if (typeof onCandidatesChanged !== 'function') return;
        try {
          onCandidatesChanged();
        } catch (_error) {}
      };
      if (stopped()) return false;

      const resolveHistoryTarget = async (forceDetailOnly = false) => resolveHistoryBootstrapPlaybackTargetRuntime({
        matchOptions,
        historyContext: this.buildPlayHistoryWarmContext(),
        globalEpisode,
        wantEpisodeInSeason,
        skipHistoryList: forceDetailOnly || !!skipHistoryList,
        runtimeSettings: this.runtimeSettings,
        smartEpisodeMapping: targetMapping,
        episodeSource: targetEpisodeSource,
        allowResolutionModes: this.buildAllowedResolutionModes(),
        isCandidateAllowed: unifiedAllowed,
        compareCandidates,
        buildSelectionKey: (panKey, index) => buildSiteEpisodeSelectionKey(panKey, index),
        getCachedSiteResultDetail: (siteItem) => this.getCachedSiteResultDetail(siteItem),
        cacheHistoryDetail: (siteItem, payload) => {
          if (stopped()) return;
          const target = payload && typeof payload === 'object' ? payload : {};
          const detail = target.detail && typeof target.detail === 'object' ? target.detail : null;
          const cacheMeta = target.cacheMeta && typeof target.cacheMeta === 'object' ? target.cacheMeta : null;
          if (!detail) return;
          this.siteResultDetailStore = setSiteResultDetailCacheEntryRuntime({
            store: this.siteResultDetailStore,
            item: siteItem,
            detail,
            cacheMeta,
          });
          this.cacheRecognitionForSiteResult(siteItem, detail, {
            mapping: targetMapping,
            mappingSignature: signature,
          });
          notifyCandidatesChanged();
        },
        ensureRecognitionForSiteItem: (siteItem, detail) => {
          if (stopped()) return;
          this.cacheRecognitionForSiteResult(siteItem, detail, {
            mapping: targetMapping,
            mappingSignature: signature,
          });
          notifyCandidatesChanged();
        },
        collectCandidates: (siteItem, nextGlobal, nextLoose, nextMatchOptions) =>
          this.collectRecognitionCandidatesForTarget(siteItem, nextGlobal, nextLoose, {
            matchOptions: nextMatchOptions || matchOptions,
            mappingSignature: signature,
            episodeSource: targetEpisodeSource,
            allowResolutionModes: this.buildAllowedResolutionModes(),
          }),
        ensureSiteResultDetailCached: (siteItem, options) =>
          this.ensureSiteResultDetailCached(siteItem, options),
      }).catch(() => null);

      try {
        if (stopped()) return false;
        const listTarget = forceDetailOnlyByStage ? null : await resolveHistoryTarget(false);
        if (stopped()) return false;
        if (listTarget) {
          hasHistoryContext = true;
          hasHistoryCandidates = true;
          if (listTarget.fromHistoryDetail) detailRequested = true;
          notifyCandidatesChanged();
        }

        detailRequested = true;
        if (stopped()) return hasHistoryCandidates;
        const detailTarget = await resolveHistoryTarget(true);
        if (stopped()) return hasHistoryCandidates;
        if (detailTarget) {
          hasHistoryContext = true;
          hasHistoryCandidates = true;
          notifyCandidatesChanged();
        }
        return hasHistoryCandidates;
      } finally {
        const detailDone = !hasHistoryContext || !!detailRequested;
        this.historySmartBootstrapStageDone = {
          ...this.historySmartBootstrapStageDone,
          [stageKey]: { done: true, detailDone },
        };
      }
    },
    getCachedSiteResultDetail(item) {
      return getSiteResultDetailRuntime(this.siteResultDetailStore, item);
    },
    buildSiteDetailDedupeKey(item) {
      const target = item && typeof item === 'object' ? item : null;
      const siteKey = normalizeString(target && target.siteKey);
      const spiderApi = normalizeString(target && target.spiderApi);
      const siteDetail = normalizeString(target && target.siteDetail);
      if (!siteKey || !spiderApi || !siteDetail) return '';
      return `${siteKey}::${spiderApi}::${siteDetail}`;
    },
    emitSiteDetailInFlightUpdate(entry, detail) {
      const payload = detail && typeof detail === 'object' ? detail : null;
      if (!entry || !payload) return;
      const listeners = entry.listeners instanceof Set ? entry.listeners : new Set();
      listeners.forEach((listener) => {
        if (typeof listener !== 'function') return;
        try {
          listener(payload);
        } catch (_error) {}
      });
    },
    async awaitSiteDetailInFlight(entry, signal) {
      const target = entry && typeof entry === 'object' ? entry : null;
      const promise = target && target.promise && typeof target.promise.then === 'function'
        ? target.promise
        : Promise.resolve(null);
      if (!signal || typeof signal.addEventListener !== 'function') return promise;
      if (signal.aborted) return null;
      return new Promise((resolve) => {
        let settled = false;
        const finish = (value) => {
          if (settled) return;
          settled = true;
          signal.removeEventListener('abort', onAbort);
          resolve(value || null);
        };
        const onAbort = () => finish(null);
        signal.addEventListener('abort', onAbort, { once: true });
        promise.then((value) => finish(value)).catch(() => finish(null));
      });
    },
    buildPrimaryEpisodeGlobalNo(item) {
      const episodeNo = normalizeInt(item && (item.no != null ? item.no : item.text));
      const seasonNo = this.currentEpisodeSeasonNumber;
      if (episodeNo <= 0 || seasonNo <= 0) return 0;
      if (this.isTMDBEpisodeSource) {
        return tmdbGlobalEpisodeNoOf(this.detailTMDBData, seasonNo, episodeNo);
      }
      if (this.isDoubanEpisodeSource) {
        return doubanGlobalEpisodeNoOf(this.detailDoubanData, seasonNo, episodeNo);
      }
      return 0;
    },
    applySiteResultDetailUpdate(item, detail) {
      const target = item && typeof item === 'object' ? item : null;
      const nextDetail = detail && typeof detail === 'object' ? detail : null;
      const itemKey = normalizeString(target && target.id);
      if (!target || !nextDetail || !itemKey) return;
      this.siteResultDetailStore = setSiteResultDetailCacheEntryRuntime({
        store: this.siteResultDetailStore,
        item: target,
        detail: nextDetail,
      });
      if (!this.selectedSiteResultItem || normalizeString(this.selectedSiteResultItem.id) !== itemKey) return;
      this.siteResultDetailData = nextDetail;
      if (!this.isTmdbMode) {
        this.detailOverview = stripMarkupText(nextDetail && nextDetail.content);
        this.detailYear = normalizeString(nextDetail && nextDetail.year);
        this.detailRemark = '';
      }
      const panSources = this.buildPanSourcesFromDetail(nextDetail);
      if (!normalizeString(this.selectedPanSource)) {
        this.selectedPanSource = panSources[0] ? panSources[0].key : '';
      }
      const projectionOptions = this.projectionSourceOptions;
      if (projectionOptions.length && !projectionOptions.includes(this.selectedProjectionSource)) {
        this.selectedProjectionSource = projectionOptions[0];
      } else if (!projectionOptions.length && this.selectedProjectionSource !== 'TMDB') {
        this.selectedProjectionSource = 'TMDB';
      }
      if (normalizeString(this.selectedPanSource)) {
        patchLastBrowsePlaybackContext({
          itemId: itemKey,
          panKey: normalizeString(this.selectedPanSource),
        });
      }
    },
    async ensureSiteResultDetailCached(item, { onUpdate, signal } = {}) {
      const target = item && typeof item === 'object' ? item : null;
      if (!target) return null;
      const cached = this.getCachedSiteResultDetail(target);
      if (cached && cached.resolutionComplete === true) {
        if (typeof onUpdate === 'function') onUpdate(cached);
        return cached;
      }

      const dedupeKey = this.buildSiteDetailDedupeKey(target);
      const listener = typeof onUpdate === 'function' ? onUpdate : null;
      const inFlightMap = this.siteResultDetailInFlightMap && typeof this.siteResultDetailInFlightMap === 'object'
        ? this.siteResultDetailInFlightMap
        : {};
      this.siteResultDetailInFlightMap = inFlightMap;

      const existing = dedupeKey ? inFlightMap[dedupeKey] : null;
      if (existing && typeof existing === 'object' && existing.promise) {
        if (listener) {
          if (!(existing.listeners instanceof Set)) existing.listeners = new Set();
          existing.listeners.add(listener);
        }
        try {
          return await this.awaitSiteDetailInFlight(existing, signal);
        } finally {
          if (listener && existing.listeners instanceof Set) existing.listeners.delete(listener);
        }
      }

      const settings = await this.ensurePlayRuntimeSettings();
      const created = {
        listeners: new Set(listener ? [listener] : []),
        promise: null,
      };
      const run = ensureSiteResultDetailCachedRuntime({
        item: target,
        store: this.siteResultDetailStore,
        runtimeSettings: settings,
        timeoutMs: 15000,
        signal: null,
        onUpdate: (nextDetail) => {
          const payload = nextDetail && typeof nextDetail === 'object' ? nextDetail : null;
          if (!payload) return;
          this.applySiteResultDetailUpdate(target, payload);
          this.emitSiteDetailInFlightUpdate(created, payload);
        },
      }).catch(() => null).finally(() => {
        if (!dedupeKey) return;
        const current = this.siteResultDetailInFlightMap && this.siteResultDetailInFlightMap[dedupeKey];
        if (current === created) {
          const nextMap = { ...(this.siteResultDetailInFlightMap || {}) };
          delete nextMap[dedupeKey];
          this.siteResultDetailInFlightMap = nextMap;
        }
      });
      created.promise = run;
      if (dedupeKey) {
        this.siteResultDetailInFlightMap = {
          ...this.siteResultDetailInFlightMap,
          [dedupeKey]: created,
        };
      }

      try {
        return await this.awaitSiteDetailInFlight(created, signal);
      } finally {
        if (listener && created.listeners instanceof Set) created.listeners.delete(listener);
      }
    },
    cacheRecognitionForSiteResult(item, detail, {
      mapping = null,
      mappingSignature = '',
      recognitionOptions = null,
    } = {}) {
      const targetMapping = mapping && typeof mapping === 'object' ? mapping : this.smartEpisodeMapping;
      const signature = normalizeString(mappingSignature)
        || (this.isTmdbMode && this.tmdbMovieMode ? TMDB_MOVIE_RECOGNITION_SIGNATURE : buildEpisodeMappingSignature(targetMapping));
      const result = cacheRecognitionForSiteResultRuntime({
        store: this.siteResultRecognitionStore,
        item,
        detail,
        signature,
        runtimeSettings: this.runtimeSettings,
        smartEpisodeMapping: targetMapping,
        recognitionOptions,
      });
      this.siteResultRecognitionStore = result && result.store && typeof result.store === 'object'
        ? result.store
        : this.siteResultRecognitionStore;
      return result && result.byEntry && typeof result.byEntry === 'object' ? result.byEntry : {};
    },
    getRecognitionCandidatesForSiteResult(item, { mappingSignature = '' } = {}) {
      return getRecognitionCandidatesForSiteResultRuntime({
        store: this.siteResultRecognitionStore,
        item,
        signature: normalizeString(mappingSignature) || this.activeRecognitionSignature,
      });
    },
    collectRecognitionCandidatesForTarget(siteItem, globalEpisodeNo, wantEpisodeInSeason = 0, {
      matchOptions = null,
      mappingSignature = '',
      episodeSource = '',
      allowResolutionModes = null,
    } = {}) {
      return collectRecognitionCandidatesForTargetRuntime({
        store: this.siteResultRecognitionStore,
        item: siteItem,
        signature: normalizeString(mappingSignature) || this.activeRecognitionSignature,
        matchOptions: matchOptions || this.buildSmartMatchOptions(),
        globalEpisodeNo,
        wantEpisodeInSeason,
        episodeSource: normalizeString(episodeSource) || this.selectedSiteSource,
        allowResolutionModes,
      });
    },
    clearSmartPlaybackPendingState({ clearResume = true } = {}) {
      this.smartPlaybackPendingRunSeq = 0;
      this.smartPlaybackAttemptRunSeq = 0;
      if (clearResume) this.smartPlaybackResume = null;
    },
    confirmSmartPlaybackClosedLoop(runSeq) {
      const targetRunSeq = normalizeInt(runSeq);
      if (targetRunSeq <= 0 || targetRunSeq !== normalizeInt(this.smartPlaybackRunSeq)) return false;
      this.smartPlaybackConfirmedRunSeq = targetRunSeq;
      this.clearSmartPlaybackPendingState({ clearResume: true });
      if (typeof this.smartPlaybackStreamCleanup === 'function') {
        try {
          this.smartPlaybackStreamCleanup();
        } catch (_e) {}
      }
      this.smartPlaybackStreamCleanup = null;
      return true;
    },
    resumeSmartPlaybackIfPendingFailed() {
      const pendingRunSeq = normalizeInt(this.smartPlaybackPendingRunSeq);
      const confirmedRunSeq = normalizeInt(this.smartPlaybackConfirmedRunSeq);
      if (pendingRunSeq <= 0 || pendingRunSeq !== normalizeInt(this.smartPlaybackRunSeq) || pendingRunSeq === confirmedRunSeq) {
        return false;
      }
      const resume = typeof this.smartPlaybackResume === 'function' ? this.smartPlaybackResume : null;
      this.clearSmartPlaybackPendingState({ clearResume: false });
      if (resume) void resume();
      return true;
    },
    resetSmartPlaybackRuntimeState({ stopStream = false, bumpRunSeq = true, bumpManualSeq = true } = {}) {
      if (stopStream && typeof this.smartPlaybackStreamCleanup === 'function') {
        try {
          this.smartPlaybackStreamCleanup();
        } catch (_error) {}
      }
      this.smartPlaybackStreamCleanup = null;
      this.smartPlaybackResume = null;
      this.smartPlaybackPendingRunSeq = 0;
      this.smartPlaybackConfirmedRunSeq = 0;
      this.smartPlaybackAttemptRunSeq = 0;
      this.smartPlaybackActiveChannel = '';
      if (bumpManualSeq) {
        this.manualSmartBootstrapSeq = Math.max(0, normalizeInt(this.manualSmartBootstrapSeq)) + 1;
      }
      if (bumpRunSeq) this.smartPlaybackRunSeq += 1;
    },
    async playCachedResolvedTarget(payload = {}, { stage = '', preserveManualBootstrap = false } = {}) {
      this.resetSmartPlaybackRuntimeState({ stopStream: true, bumpManualSeq: !preserveManualBootstrap });
      const ok = await this.playResolvedSiteSegment(payload);
      if (ok && normalizeString(stage)) this.smartPlaybackResolvedStage = normalizeString(stage);
      return !!ok;
    },
    async playResolvedSiteSegment({
      siteItem,
      panEntry,
      segment,
      candidate,
      selectionKey,
      globalEpisode,
      sourceKind = '',
      fromHistoryPlayFlag = false,
      fromHistoryDetail = false,
      selectedGoProxyBase = '',
    }) {
      if (!siteItem || !panEntry || !segment || !normalizeString(segment.episodeUrl)) return false;
      const resolvedSourceKind = this.resolvePlaybackSourceKind({
        sourceKind,
        fromHistoryPlayFlag,
        fromHistoryDetail,
      });
      this.lastResolvedPlaybackPayload = {
        siteItem,
        panEntry,
        segment,
        candidate: candidate && typeof candidate === 'object' ? candidate : null,
        selectionKey: normalizeString(selectionKey),
        globalEpisode: Math.max(0, normalizeInt(globalEpisode)),
        sourceKind: resolvedSourceKind,
        fromHistoryPlayFlag: !!fromHistoryPlayFlag,
        fromHistoryDetail: !!fromHistoryDetail,
      };
      this.syncCurrentPlaybackContextFromPage({
        siteItem,
        panEntry,
        segment,
        candidate,
        selectionKey,
        globalEpisode,
        sourceKind: resolvedSourceKind,
        fromHistoryPlayFlag,
        fromHistoryDetail,
      });
      const siteResolvedMeta = await this.ensureSiteHistoryResolvedMeta();
      const siteTMDBBinding = this.buildSiteModeTMDBBindingForSegment({
        segment,
        resolvedMeta: siteResolvedMeta,
        panEntry,
      });
      await preparePlayHistoryContext(
        this.buildPlayHistoryPayloadForResolvedSegment({
          siteItem,
          pan: panEntry,
          segment,
          selectionKey,
          globalEpisode,
          contentKeyOverride: normalizeString(siteResolvedMeta && siteResolvedMeta.contentKey),
          siteTMDBBinding,
          resolvedMeta: siteResolvedMeta,
        }),
      );
      const seq = this.playRequestSeq + 1;
      this.playRequestSeq = seq;
      this.playLoading = true;
      this.playRequestStage = 'play_url';
      this.playError = '';
      this.playerRuntimeError = '';
      this.autoProxyRetriedSeq = 0;
      this.autoM3U8RelayRetriedSeq = 0;
      this.playerMetaReady = false;
      this.playerFirstFrameReady = false;
      this.playerPlaybackStarted = false;
      this.playerBuffering = false;
      this.m3u8RelayRetryInFlight = false;
      this.goProxyInUseBase = '';
      this.lastGoProxyCandidate = null;
      this.lastM3U8RelayCandidate = null;
      this.syncPlayerStatsForResolvedSegment({ siteResultItem: siteItem, pan: panEntry, segment, candidate });
      try {
        const settings = await this.ensurePlayRuntimeSettings();
        const resolvedDetail = this.getCachedSiteResultDetail(siteItem);
        const playbackSiteItem = {
          ...siteItem,
          detailData: resolvedDetail && typeof resolvedDetail === 'object'
            ? resolvedDetail
            : null,
        };
        const result = await executeResolvedSitePlayback({
          runtimeSettings: settings,
          bootstrapUser: this.bootstrap && this.bootstrap.user && this.bootstrap.user.username,
          siteItem: playbackSiteItem,
          panEntry,
          segment,
          selectionKey,
          apiBase: normalizeString(settings && settings.catpawrunnerApiBase),
          selectedGoProxyBase: normalizeString(selectedGoProxyBase),
        });
        if (seq !== this.playRequestSeq) return false;
        this.lastGoProxyCandidate = result && result.lastGoProxyCandidate ? result.lastGoProxyCandidate : null;
        this.lastM3U8RelayCandidate = result && result.lastM3U8RelayCandidate ? result.lastM3U8RelayCandidate : null;
        this.goProxyInUseBase = normalizeString(result && result.goProxyBase);
        this.playerUrl = normalizeString(result && result.playerUrl);
        this.playerHeaders = result && result.playerHeaders && typeof result.playerHeaders === 'object'
          ? result.playerHeaders
          : {};
        if (this.activePlayerControlAction === 'switch') {
          this.recordResolvedPayloadIntoSwitchSkipBucket({
            siteItem,
            panEntry,
            segment,
          });
        }
        this.playRequestStage = 'play_info';
        this.playError = '';
        this.playerRuntimeError = '';
        await this.$nextTick();
        const art = this.$refs.artPlayerRef;
        if (art && typeof art.tryAutoplay === 'function') {
          try {
            await art.tryAutoplay();
          } catch (_error) {}
        }
        if (normalizeString(this.smartPlaybackStage)) {
          this.smartPlaybackResolvedStage = normalizeString(this.smartPlaybackStage);
        }
        return true;
      } catch (error) {
        if (seq !== this.playRequestSeq) return false;
        this.playerUrl = '';
        this.playerHeaders = {};
        this.playRequestStage = '';
        this.clearPlayerStats();
        this.lastGoProxyCandidate = null;
        this.lastM3U8RelayCandidate = null;
        this.playError = error && error.message ? String(error.message) : '播放失败';
        return false;
      } finally {
        if (seq === this.playRequestSeq) this.playLoading = false;
      }
    },
    async playPrimaryEpisodeItem(item, stageConfig = null) {
      const globalEpisode = this.buildPrimaryEpisodeGlobalNo(item);
      const wantEpisodeInSeason = normalizeInt(item && (item.no != null ? item.no : item.text));
      if (globalEpisode <= 0) return false;
      const stage = stageConfig && typeof stageConfig === 'object' ? stageConfig : null;
      const primaryActionConstraint = this.buildPrimaryEpisodeActionConstraint(globalEpisode);
      const switchEpisodeKey = this.buildSwitchEpisodeKeyByGlobal(globalEpisode);
      const primarySkipIndex = this.buildSwitchSkipIndexForEpisode(switchEpisodeKey);
      const sharedPrimaryAllowed = (wrapper) => {
        if (this.shouldSkipSwitchCandidate(switchEpisodeKey, wrapper, primarySkipIndex)) return false;
        return true;
      };
      const cachedTarget = this.resolveCachedPlaybackTarget(globalEpisode, wantEpisodeInSeason, {
        matchOptions: this.buildSmartMatchOptions({
          trigger: 'primary',
        }),
        actionConstraint: primaryActionConstraint,
        isCandidateAllowed: sharedPrimaryAllowed,
        mapping: stage && stage.mapping,
        mappingSignature: stage && stage.mappingSignature,
        episodeSource: stage && stage.episodeSource,
      });
      if (cachedTarget) {
        const cachedOk = await this.playCachedResolvedTarget({
          ...cachedTarget,
          globalEpisode,
        }, {
          stage: stage && stage.stage,
        });
        if (cachedOk) return true;
      }
      await this.runSmartPlaybackWithConstraints({
        globalEpisode,
        wantEpisodeInSeason,
        matchOptions: this.buildSmartMatchOptions({
          trigger: 'primary',
        }),
        actionConstraint: primaryActionConstraint,
        isCandidateAllowed: sharedPrimaryAllowed,
        stage: stage && stage.stage,
        mapping: stage && stage.mapping,
        mappingSignature: stage && stage.mappingSignature,
        episodeSource: stage && stage.episodeSource,
      });
      return this.isAutoplaySmartLaunchSuccessful();
    },
    buildSmartMatchOptions(extra = {}) {
      const base = this.isTmdbMode && this.tmdbMovieMode
        ? { kind: 'movie' }
        : { kind: 'episode' };
      return {
        ...base,
        ...(extra && typeof extra === 'object' ? extra : {}),
      };
    },
    buildAllowedResolutionModes() {
      return ['strict-tmdb', 'strict-douban'];
    },
    getWantEpisodeInSeasonByGlobal(globalEpisode) {
      const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
      if (targetGlobal <= 0) return 0;
      const mapping = this.fullEpisodeMapping;
      const tmdbValue = Array.isArray(mapping && mapping.items)
        ? mapping.items.find((item) => normalizeInt(item && item.global) === targetGlobal)?.tmdb
        : null;
      return Math.max(0, normalizeInt(tmdbValue && tmdbValue.episode));
    },
    activateSmartPlaybackChannel(runSeq, callbackChannel) {
      this.smartPlaybackRunSeq = runSeq;
      this.smartPlaybackActiveChannel = callbackChannel;
      const callbackToken = this.smartPlaybackChannelToken + 1;
      this.smartPlaybackChannelToken = callbackToken;
      return callbackToken;
    },
    isSmartPlaybackCallbackActive(callbackToken, runSeq, callbackChannel) {
      return callbackToken === this.smartPlaybackChannelToken
        && runSeq === this.smartPlaybackRunSeq
        && normalizeString(this.smartPlaybackActiveChannel) === normalizeString(callbackChannel);
    },
    buildSmartPlaybackControllerCallbacks({
      runSeq,
      actionUiOnly,
      callbackChannel,
      callbackToken,
      targetSignature,
      targetEpisodeSource,
      normalizedMatchOptions,
      targetMapping,
      unifiedAllowed,
    } = {}) {
      const isActiveSmartCallback = () => this.isSmartPlaybackCallbackActive(callbackToken, runSeq, callbackChannel);
      return {
        isRunStopped: () => runSeq !== this.smartPlaybackRunSeq || this.smartPlaybackConfirmedRunSeq === runSeq,
        isCandidateAllowed: unifiedAllowed,
        buildSiteItemsFromSnapshot: (snapshot) => this.buildSiteSourceResultItemsFromSnapshot(snapshot),
        collectRecognitionCandidatesForTarget: (siteItem, payload) =>
          this.collectRecognitionCandidatesForTarget(
            siteItem,
            payload && payload.globalEpisode,
            payload && payload.wantEpisodeInSeason,
            {
              matchOptions: payload && payload.matchOptions ? payload.matchOptions : normalizedMatchOptions,
              mappingSignature: targetSignature,
              episodeSource: targetEpisodeSource,
              allowResolutionModes: this.buildAllowedResolutionModes(),
            },
          ),
        getCachedSiteResultDetail: (siteItem) => this.getCachedSiteResultDetail(siteItem),
        ensureSiteResultDetailCached: (siteItem, options) =>
          this.ensureSiteResultDetailCached(siteItem, {
            onUpdate: (nextDetail) => {
              const payload = nextDetail && typeof nextDetail === 'object' ? nextDetail : null;
              if (!payload) return;
              this.cacheRecognitionForSiteResult(siteItem, payload, {
                mapping: targetMapping,
                mappingSignature: targetSignature,
              });
              if (options && typeof options.onUpdate === 'function') options.onUpdate(payload);
            },
          }),
        buildPanSourcesFromDetail: (detail) => this.buildPanSourcesFromDetail(detail),
        buildPanSegment: (panEntry, index) => this.buildPanSegment(panEntry, index),
        buildSelectionKey: (panKey, index) => buildSiteEpisodeSelectionKey(panKey, index),
        playResolvedSiteSegment: (payload) => this.playResolvedSiteSegment(payload),
        getSearchState: (query, scope) => ({
          snapshot: getSearchSessionLaneSnapshot(query, scope, 'site'),
          status: getSearchSessionLaneStatus(query, scope, 'site'),
        }),
        setAttemptRunSeq: (value) => { this.smartPlaybackAttemptRunSeq = normalizeInt(value); },
        setPendingRunSeq: (value) => { this.smartPlaybackPendingRunSeq = normalizeInt(value); },
        setResume: (fn) => { this.smartPlaybackResume = typeof fn === 'function' ? fn : null; },
        clearPendingAttempt: () => {
          if (this.smartPlaybackPendingRunSeq === runSeq) this.smartPlaybackPendingRunSeq = 0;
          if (this.smartPlaybackAttemptRunSeq === runSeq) this.smartPlaybackAttemptRunSeq = 0;
        },
        onLoadingStateChange: (value) => {
          if (!isActiveSmartCallback()) return;
          if (!actionUiOnly) this.playLoading = !!value;
        },
        onErrorTextChange: (value) => {
          if (!isActiveSmartCallback()) return;
          const text = normalizeString(value);
          if (!actionUiOnly) {
            this.playError = text;
            return;
          }
          if (text) {
            this.closePlayerActionFlow({ toast: text });
          }
        },
        onStreamCleanupChange: (fn) => { this.smartPlaybackStreamCleanup = typeof fn === 'function' ? fn : null; },
      };
    },
    async runSmartPlaybackWithConstraints({
      globalEpisode,
      wantEpisodeInSeason = 0,
      matchOptions = null,
      actionConstraint = null,
      isCandidateAllowed = null,
      stage = '',
      mapping = null,
      mappingSignature = '',
      episodeSource = '',
      skipHistoryList = false,
      uiStatusMode = 'default',
    } = {}) {
      const normalizedMatchOptions = this.buildSmartMatchOptions(matchOptions);
      const actionUiOnly = normalizeString(uiStatusMode) === 'action';
      const callbackChannel = actionUiOnly ? 'action' : 'primary';
      const matchKind = normalizeString(normalizedMatchOptions && normalizedMatchOptions.kind).toLowerCase();
      const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
      const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
      if (matchKind !== 'movie' && targetGlobal <= 0) return;
      const unifiedAllowed = this.buildUnifiedSmartCandidateAllowed(isCandidateAllowed);
      const currentStage = normalizeString(stage) || 'full';
      let manualStageDone = !!this.manualSmartBootstrapStageDone[currentStage];
      const targetMapping = mapping && typeof mapping === 'object' ? mapping : this.smartEpisodeMapping;
      const targetSignature = normalizeString(mappingSignature)
        || (matchKind === 'movie' ? TMDB_MOVIE_RECOGNITION_SIGNATURE : buildEpisodeMappingSignature(targetMapping));
      const targetEpisodeSource = normalizeString(episodeSource) || this.selectedSiteSource;
      const failedCachedKeys = new Set();
      const markManualStageDone = () => {
        manualStageDone = true;
        this.manualSmartBootstrapStageDone = {
          ...this.manualSmartBootstrapStageDone,
          [currentStage]: true,
        };
      };
      const markHistoryStageDone = () => {
        this.historySmartBootstrapStageDone = {
          ...this.historySmartBootstrapStageDone,
          [currentStage]: { done: true, detailDone: true },
        };
      };
      let resolvedByGlobal = false;
      let globalResolveRunning = false;
      let globalResolveQueued = false;
      const globalResolveIdleWaiters = [];
      const buildCachedTargetKey = (target) => {
        const siteItem = target && target.siteItem && typeof target.siteItem === 'object' ? target.siteItem : null;
        const panEntry = target && target.panEntry && typeof target.panEntry === 'object' ? target.panEntry : null;
        const segment = target && target.segment && typeof target.segment === 'object' ? target.segment : null;
        const itemId = normalizeString(siteItem && siteItem.id);
        const panKey = normalizeString(panEntry && panEntry.key);
        const itemIndex = normalizeInt(segment && segment.index);
        if (!itemId || !panKey || itemIndex < 0) return '';
        return `${itemId}::${panKey}::${itemIndex}`;
      };
      const flushGlobalResolveIdleWaiters = () => {
        if (globalResolveRunning || globalResolveQueued) return;
        while (globalResolveIdleWaiters.length) {
          const done = globalResolveIdleWaiters.shift();
          if (typeof done === 'function') done();
        }
      };
      const waitGlobalResolveIdle = () => new Promise((resolve) => {
        if (!globalResolveRunning && !globalResolveQueued) {
          resolve();
          return;
        }
        globalResolveIdleWaiters.push(resolve);
      });
      const tryPlayCachedCandidates = async () => {
        while (!resolvedByGlobal) {
          const cachedTarget = this.resolveCachedPlaybackTarget(targetGlobal, targetLoose, {
            matchOptions: normalizedMatchOptions,
            actionConstraint,
            isCandidateAllowed: (wrapper) => {
              if (!unifiedAllowed(wrapper)) return false;
              const key = `${normalizeString(wrapper && wrapper.siteItem && wrapper.siteItem.id)}::${normalizeString(wrapper && wrapper.panKey)}::${normalizeInt(wrapper && wrapper.itemIndex)}`;
              if (!key) return true;
              return !failedCachedKeys.has(key);
            },
            mapping: targetMapping,
            mappingSignature: targetSignature,
            episodeSource: targetEpisodeSource,
            includeSelectedContext: true,
            includeBrowseContext: true,
            includeStoreScan: true,
          });
          if (!cachedTarget) return false;
          const cachedKey = buildCachedTargetKey(cachedTarget);
          if (cachedKey && failedCachedKeys.has(cachedKey)) return false;
          const cachedOk = await this.playCachedResolvedTarget({
            ...cachedTarget,
            globalEpisode: targetGlobal,
          }, {
            stage: currentStage,
            preserveManualBootstrap: true,
          });
          if (cachedOk) {
            resolvedByGlobal = true;
            return true;
          }
          if (!cachedKey) return false;
          failedCachedKeys.add(cachedKey);
        }
        return true;
      };
      const runGlobalResolvePump = async () => {
        if (globalResolveRunning || resolvedByGlobal) {
          flushGlobalResolveIdleWaiters();
          return false;
        }
        globalResolveRunning = true;
        try {
          while (!resolvedByGlobal) {
            if (!globalResolveQueued) break;
            globalResolveQueued = false;
            const ok = await tryPlayCachedCandidates();
            if (ok) return true;
          }
          return resolvedByGlobal;
        } finally {
          globalResolveRunning = false;
          if (globalResolveQueued && !resolvedByGlobal) {
            void runGlobalResolvePump();
          } else {
            flushGlobalResolveIdleWaiters();
          }
        }
      };
      const scheduleGlobalResolve = () => {
        if (resolvedByGlobal) return;
        globalResolveQueued = true;
        void runGlobalResolvePump();
      };
      const notifyCandidatesChanged = () => {
        scheduleGlobalResolve();
      };

      // Phase 1 (frontend strict serial): reuse cached candidates first.
      scheduleGlobalResolve();
      await waitGlobalResolveIdle();
      if (resolvedByGlobal) {
        markHistoryStageDone();
        return;
      }

      // Phase 2/3: history list chain -> history detail chain (only produce candidates).
      await this.tryHistorySmartBootstrap(targetGlobal, targetLoose, {
        matchOptions: normalizedMatchOptions,
        actionConstraint,
        isCandidateAllowed: unifiedAllowed,
        stage: currentStage,
        mapping: targetMapping,
        mappingSignature: targetSignature,
        episodeSource: targetEpisodeSource,
        skipHistoryList,
        onCandidatesChanged: notifyCandidatesChanged,
        isRunStopped: () => resolvedByGlobal,
      });
      await waitGlobalResolveIdle();
      if (resolvedByGlobal) {
        markHistoryStageDone();
        return;
      }

      // Phase 4: manual chain (only produce candidates).
      if (!manualStageDone) {
        const manualSeq = Math.max(0, normalizeInt(this.manualSmartBootstrapSeq)) + 1;
        this.manualSmartBootstrapSeq = manualSeq;
        const manualCompleted = await tryManualSmartBootstrapRuntime({
          isTmdbMode: this.isTmdbMode,
          tmdbType: this.tmdbType,
          searchType: this.searchType,
          tmdbId: this.tmdbId,
          globalEpisode: targetGlobal,
          wantEpisodeInSeason: targetLoose,
          matchOptions: normalizedMatchOptions,
          selectedSiteSource: this.selectedSiteSource,
          episodeSource: targetEpisodeSource,
          allowResolutionModes: this.buildAllowedResolutionModes(),
          isRunStopped: () => (
            normalizeInt(this.manualSmartBootstrapSeq) !== manualSeq
            || resolvedByGlobal
          ),
          playSearchQuery: this.playSearchQuery,
          displayTitle: this.displayTitle,
          ensureSiteResultDetailCached: (siteItem) => this.ensureSiteResultDetailCached(siteItem),
          buildSiteDetailDedupeKey: (siteItem) => this.buildSiteDetailDedupeKey(siteItem),
          setSiteResultDetailCacheEntry: (siteItem, detail, cacheMeta) => {
            this.siteResultDetailStore = setSiteResultDetailCacheEntryRuntime({
              store: this.siteResultDetailStore,
              item: siteItem,
              detail,
              cacheMeta: cacheMeta && typeof cacheMeta === 'object' ? cacheMeta : {},
            });
          },
          cacheRecognitionForSiteResult: (siteItem, detail, recognitionOptions) => {
            this.cacheRecognitionForSiteResult(siteItem, detail, {
              mapping: targetMapping,
              mappingSignature: targetSignature,
              recognitionOptions: recognitionOptions && typeof recognitionOptions === 'object'
                ? recognitionOptions
                : null,
            });
          },
          collectRecognitionCandidatesForTarget: (siteItem, nextGlobal, nextLoose, payload = {}) =>
            this.collectRecognitionCandidatesForTarget(siteItem, nextGlobal, nextLoose, {
              matchOptions: payload && payload.matchOptions ? payload.matchOptions : normalizedMatchOptions,
              mappingSignature: targetSignature,
              episodeSource: normalizeString(payload && payload.episodeSource) || targetEpisodeSource,
              allowResolutionModes: payload && Array.isArray(payload.allowResolutionModes)
                ? payload.allowResolutionModes
                : this.buildAllowedResolutionModes(),
            }),
          onCandidatesChanged: notifyCandidatesChanged,
          shouldPauseDispatch: () => globalResolveRunning || globalResolveQueued,
          waitDispatchResume: () => waitGlobalResolveIdle(),
        });
        if (manualCompleted === true) markManualStageDone();
      }
      await waitGlobalResolveIdle();
      if (resolvedByGlobal) {
        markHistoryStageDone();
        return;
      }
      this.resetSmartPlaybackRuntimeState({ stopStream: true, bumpRunSeq: false });
      const runSeq = this.smartPlaybackRunSeq + 1;
      this.smartPlaybackStage = currentStage;
      const callbackToken = this.activateSmartPlaybackChannel(runSeq, callbackChannel);
      patchCurrentPlaybackContext({ globalEpisode: targetGlobal });
      if (!actionUiOnly) {
        this.playLoading = true;
        this.playRequestStage = 'detail';
        this.playError = '';
        this.playerRuntimeError = '';
        this.playerMetaReady = false;
        this.playerFirstFrameReady = false;
        this.playerPlaybackStarted = false;
        this.playerBuffering = false;
      }
      await this.ensurePlayRuntimeSettings();
      await this.ensurePlayBlockedSiteKeysLoaded();
      await this.ensurePlayBlockedMatchIndexLoaded();
      try {
        await runSmartPlaybackController({
          runSeq,
          query: this.playSearchQuery,
          bootstrap: this.bootstrap,
          searchScope: this.effectivePlaySearchScope,
          searchDisplayModeOverride: 'sites',
          blockedSiteKeys: this.playBlockedSiteKeys,
          matchOptions: normalizedMatchOptions,
          actionConstraint,
          runtimeSettings: this.runtimeSettings,
          currentContext: this.currentPlaybackContext,
          globalEpisode: targetGlobal,
          wantEpisodeInSeason: targetLoose,
          ...this.buildSmartPlaybackControllerCallbacks({
            runSeq,
            actionUiOnly,
            callbackChannel,
            callbackToken,
            targetSignature,
            targetEpisodeSource,
            normalizedMatchOptions,
            targetMapping,
            unifiedAllowed,
          }),
        });
      } finally {
        if (this.isSmartPlaybackCallbackActive(callbackToken, runSeq, callbackChannel)) {
          this.smartPlaybackStage = 'idle';
          this.smartPlaybackActiveChannel = '';
        }
        if (!actionUiOnly && !normalizeString(this.playerUrl) && this.playRequestStage === 'detail') {
          this.playRequestStage = '';
        }
      }
    },
    async runPlayerControlSmartPlayback({ actionKey, selectedValue = '' } = {}) {
      if (!this.isPlayerInTmdbMode) {
        this.showPlayerActionToast('当前模式暂不支持画质/网盘切换');
        return;
      }
      const globalEpisode = Math.max(0, normalizeInt(this.currentPlaybackContext && this.currentPlaybackContext.globalEpisode));
      if (globalEpisode <= 0) {
        this.showPlayerActionToast('当前播放上下文未就绪');
        return;
      }
      const action = normalizeString(actionKey);
      const value = normalizeString(selectedValue);
      const suppressStatusUi = action === 'switch' || action === 'pan' || action === 'quality';
      if (action === 'switch') {
        this.recordCurrentPlaybackIntoSwitchSkipBucket(globalEpisode);
      }
      const actionTargetKey = this.buildPlayerActionTargetKey({
        globalEpisode,
        actionKey: action,
        selectedValue: value,
      });
      if (this.shouldIgnorePlayerControlAction({
        action,
        value,
        suppressStatusUi,
        actionTargetKey,
      })) return;
      if (suppressStatusUi) {
        this.startPlayerActionSearchFlow(actionTargetKey);
      }
      if (action === 'switch') {
        this.activePlayerControlAction = 'switch';
      }
      const actionConstraint = buildSmartPlaybackActionConstraint({
        actionKey: action,
        selectedValue: value,
        currentContext: this.currentPlaybackContext,
      });
      if (!actionConstraint) {
        if (suppressStatusUi) this.closePlayerActionFlow();
        return;
      }
      const skipHistoryList = action === 'switch' || action === 'pan' || action === 'quality';
      const sharedCandidateAllowed = this.buildPlayerActionCandidateAllowed(action, globalEpisode);
      const cachedTarget = this.resolveCachedPlaybackTarget(
        globalEpisode,
        this.getWantEpisodeInSeasonByGlobal(globalEpisode),
        {
          actionConstraint,
          isCandidateAllowed: (wrapper) =>
            sharedCandidateAllowed(wrapper),
          includeSelectedContext: action !== 'switch',
          includeBrowseContext: action !== 'switch',
          includeStoreScan: action === 'switch' || action === 'primary',
        },
      );
      if (cachedTarget) {
        const cachedOk = await this.playCachedResolvedTarget({
          ...cachedTarget,
          globalEpisode,
        });
        if (cachedOk) return;
        // Cached target failed: keep action flow alive and continue through full controller loop.
      }
      await this.runSmartPlaybackWithConstraints({
        globalEpisode,
        wantEpisodeInSeason: this.getWantEpisodeInSeasonByGlobal(globalEpisode),
        matchOptions: this.buildSmartMatchOptions({
          trigger: action,
          value,
        }),
        actionConstraint,
        isCandidateAllowed: sharedCandidateAllowed,
        skipHistoryList,
        uiStatusMode: suppressStatusUi ? 'action' : 'default',
      });
      if (!suppressStatusUi) return;
      // No extra success/failure probing here.
      // Action-mode finalize is driven by controller callbacks and player events.
    },
    async markCurrentPlaybackSourceWrong() {
      const keyword = normalizeString(this.playSearchQueryOriginal || this.playSearchQuery || this.displayTitle);
      const payload = this.lastResolvedPlaybackPayload && typeof this.lastResolvedPlaybackPayload === 'object'
        ? this.lastResolvedPlaybackPayload
        : null;
      const playback = this.currentPlaybackContext && typeof this.currentPlaybackContext === 'object'
        ? this.currentPlaybackContext
        : null;
      const siteItem = payload && payload.siteItem && typeof payload.siteItem === 'object'
        ? payload.siteItem
        : {
          siteKey: normalizeString(playback && playback.siteKey),
          siteName: normalizeString(playback && playback.siteName),
          spiderApi: normalizeString(playback && playback.spiderApi),
          siteDetail: normalizeString(playback && playback.siteDetail),
        };
      const panFlag = normalizeString(this.currentPlaybackContext && this.currentPlaybackContext.panFlag)
        || normalizeString(payload && payload.panEntry && payload.panEntry.label);
      const siteKey = normalizeString(siteItem && siteItem.siteKey);
      const spiderApi = normalizeString(siteItem && siteItem.spiderApi);
      const siteDetail = normalizeString(siteItem && siteItem.siteDetail);
      if (!keyword || !siteKey || !siteDetail || !panFlag) {
        this.playError = '当前片源信息不完整';
        return false;
      }
      await addSmartMatchBlockItem({
        keyword,
        siteKey,
        spiderApi,
        siteDetail,
        poster: normalizeString(this.detailPoster),
        panFlag,
        source: 'play',
      });
      clearBlockedMatchCaches();
      await this.ensurePlayBlockedMatchIndexLoaded();
      return true;
    },
    async handleWrongSourceAction() {
      if (!this.isPlayerInTmdbMode || this.playLoading) return;
      try {
        const saved = await this.markCurrentPlaybackSourceWrong();
        if (!saved) return;
      } catch (error) {
        this.playError = error && error.message ? String(error.message) : '片源错误处理失败';
        return;
      }
      await this.runPlayerControlSmartPlayback({ actionKey: 'switch' });
    },
    syncPlayerStatsForSegment({ item, pan, segment }) {
      const namingContext = buildStatsNamingContext(
        pan,
        getPanEntrySegments(pan),
        this.displayTitle,
      );
      this.playerStatsSiteName = buildPlaybackSiteLabel(item);
      this.playerStatsPanName = normalizeString(pan && pan.label);
      this.playerStatsPathName = namingContext.preferFile ? resolvePlayerStatsPathName({ segment }) : '';
      this.playerStatsRawFileName = pickRawFileNameForStats(segment && segment.displayName, segment && segment.rawName, namingContext);
    },
    clearPlayerStats() {
      this.playerStatsSiteName = '';
      this.playerStatsPanName = '';
      this.playerStatsPathName = '';
      this.playerStatsRawFileName = '';
    },
    async onGoProxySelect(base) {
      const nextBase = normalizeString(base);
      this.goProxyManualBase = nextBase;
      const candidate = this.lastGoProxyCandidate && typeof this.lastGoProxyCandidate === 'object'
        ? this.lastGoProxyCandidate
        : null;
      if (!candidate || !candidate.enabled || !normalizeString(candidate.url) || this.proxyRetryInFlight) return;
      this.goProxyInUseBase = '';
      this.proxyRetryInFlight = true;
      try {
        this.playerUrl = '';
        this.playerHeaders = {};
        this.playLoading = true;
        this.playRequestStage = 'play_info';
        this.playError = '';
        this.playerRuntimeError = '';
        await this.$nextTick();
        const out = await executeProxyRetryPlayback({
          candidate,
          runtimeSettings: this.runtimeSettings,
          selectedGoProxyBase: this.goProxyManualBase,
        });
        if (!out || !out.ok || !normalizeString(out.playerUrl)) {
          this.playError = '代理切换失败';
          return;
        }
        this.playerUrl = normalizeString(out.playerUrl);
        this.playerHeaders = out.playerHeaders && typeof out.playerHeaders === 'object'
          ? out.playerHeaders
          : {};
        this.goProxyInUseBase = normalizeString(out.goProxyBase);
        this.playError = '';
        this.playerRuntimeError = '';
      } finally {
        this.playLoading = false;
        this.playRequestStage = '';
        this.proxyRetryInFlight = false;
      }
    },
    onPlayerExtraMenuSelect(payload) {
      const key = normalizeString(payload && payload.key);
      const value = normalizeString(payload && payload.value);
      if (!key || !value) return;
      void this.runPlayerControlSmartPlayback({ actionKey: key, selectedValue: value });
    },
    onPlayerExtraAction(keyRaw) {
      const key = normalizeString(keyRaw);
      if (!key) return;
      if (key === 'wrong-source') {
        void this.handleWrongSourceAction();
        return;
      }
      void this.runPlayerControlSmartPlayback({ actionKey: key });
    },
    async playSiteResultItemByIndex(index, preferredGlobalEpisode = 0) {
      const item = this.activeSitePlaybackItem;
      const pan = this.currentPanSourceEntry;
      const segment = this.buildCurrentPanSegment(index);
      if (!item || !pan || !segment || !normalizeString(segment.episodeUrl)) return false;
      const globalEpisode = Math.max(0, normalizeInt(preferredGlobalEpisode))
        || this.getCurrentPanSegmentGlobalEpisode(index);
      this.resetSmartPlaybackRuntimeState({ stopStream: true });
      return this.playResolvedSiteSegment({
        siteItem: item,
        panEntry: pan,
        segment,
        selectionKey: buildSiteEpisodeSelectionKey(pan.key, segment.index),
        globalEpisode,
        sourceKind: (!this.isTmdbMode && !this.selectedSiteResultItem) ? 'direct-site' : 'site-detail',
      });
    },
    onPlayerError(event) {
      this.playerRuntimeError = event && event.message ? String(event.message) : '播放失败';
      this.playLoading = false;
      this.playRequestStage = '';
      this.playerBuffering = false;
      if (this.playerUiTransitionMode === 'switch') {
        const text = normalizeString(this.playerRuntimeError || this.playError) || '播放失败';
        this.closePlayerActionFlow({ toast: text, clearErrors: true });
      } else {
        this.activePlayerControlAction = '';
      }
      const candidate = this.lastGoProxyCandidate && typeof this.lastGoProxyCandidate === 'object'
        ? this.lastGoProxyCandidate
        : null;
      const canRetryWithProxy =
        !this.proxyRetryInFlight
        && normalizeInt(this.autoProxyRetriedSeq) !== normalizeInt(this.playRequestSeq)
        && candidate
        && candidate.enabled
        && hasNonEmptyHeaders(candidate.headers)
        && normalizeString(candidate.url);
      if (canRetryWithProxy) {
        this.autoProxyRetriedSeq = normalizeInt(this.playRequestSeq);
        this.proxyRetryInFlight = true;
        this.playerUrl = '';
        this.playerHeaders = {};
        this.playLoading = true;
        this.playRequestStage = 'play_info';
        this.$nextTick().then(async () => {
          try {
            const out = await executeProxyRetryPlayback({
              candidate,
              runtimeSettings: this.runtimeSettings,
              selectedGoProxyBase: this.goProxyManualBase,
            });
            if (!out || !out.ok || !normalizeString(out.playerUrl)) return;
            this.playerUrl = normalizeString(out.playerUrl);
            this.playerHeaders = out.playerHeaders && typeof out.playerHeaders === 'object'
              ? out.playerHeaders
              : {};
            this.goProxyInUseBase = normalizeString(out.goProxyBase);
            this.playError = '';
            this.playerRuntimeError = '';
          } finally {
            this.playLoading = false;
            this.playRequestStage = '';
            this.proxyRetryInFlight = false;
          }
        });
        return;
      }
      const m3u8Candidate = this.lastM3U8RelayCandidate && typeof this.lastM3U8RelayCandidate === 'object'
        ? this.lastM3U8RelayCandidate
        : null;
      const canRetryWithM3U8Relay =
        !this.m3u8RelayRetryInFlight
        && normalizeInt(this.autoM3U8RelayRetriedSeq) !== normalizeInt(this.playRequestSeq)
        && m3u8Candidate
        && m3u8Candidate.enabled
        && normalizeString(m3u8Candidate.url);
      if (canRetryWithM3U8Relay) {
        const retrySeq = normalizeInt(this.playRequestSeq);
        this.autoM3U8RelayRetriedSeq = retrySeq;
        this.m3u8RelayRetryInFlight = true;
        this.playerUrl = '';
        this.playerHeaders = {};
        this.playLoading = true;
        this.playRequestStage = 'play_info';
        this.$nextTick().then(() => {
          try {
            const out = executeM3U8RelayRetryPlayback({
              candidate: m3u8Candidate,
              runtimeSettings: this.runtimeSettings,
            });
            if (retrySeq !== normalizeInt(this.playRequestSeq)) return;
            if (!out || !out.ok || !normalizeString(out.playerUrl)) return;
            this.playerUrl = normalizeString(out.playerUrl);
            this.playerHeaders = out.playerHeaders && typeof out.playerHeaders === 'object'
              ? out.playerHeaders
              : {};
            this.playError = '';
            this.playerRuntimeError = '';
          } finally {
            if (retrySeq === normalizeInt(this.playRequestSeq)) {
              this.playLoading = false;
              this.playRequestStage = '';
              this.m3u8RelayRetryInFlight = false;
            }
          }
        });
        return;
      }
      this.resumeSmartPlaybackIfPendingFailed();
    },
    onPlayerBuffering(value) {
      this.playerBuffering = !!value;
    },
    onPlayerVideoInfo(info) {
      this.playerMetaReady = true;
      const payload = info && typeof info === 'object' ? info : {};
      const inferredQuality = inferQualityFromResolution({
        width: payload.width,
        height: payload.height,
      });
      if (!inferredQuality) return;
      const currentQuality = normalizeString(this.currentPlaybackContext && this.currentPlaybackContext.quality);
      if (!currentQuality || currentQuality === '未知') {
        patchCurrentPlaybackContext({ quality: inferredQuality });
        return;
      }
      if (!doesQualityMatchResolution(currentQuality, inferredQuality)) {
        patchCurrentPlaybackContext({ quality: inferredQuality });
      }
    },
    onPlayerTimeUpdate(info) {
      onPlayerHistoryTimeUpdate(info);
      void syncHistoryProgressIfPossible();
    },
    async onPlayerEpisodeDelta(deltaRaw) {
      if (this.autoNextInFlight || normalizeInt(this.smartPlaybackPendingRunSeq) > 0) return;
      await this.switchEpisodeByDelta(deltaRaw, { showBoundaryToast: true });
    },
    onPlayerPlaying() {
      this.finalizePlayerReadyState();
    },
    async onPlayerFirstFrame() {
      this.finalizePlayerReadyState({ markFirstFrame: true });
      await this.applyPlayHistoryResume('firstframe');
      this.confirmSmartPlaybackClosedLoop(this.smartPlaybackPendingRunSeq);
    },
    onPlayerEnded() {
      if (this.autoNextInFlight || this.playLoading || normalizeInt(this.smartPlaybackPendingRunSeq) > 0) return;
      this.autoNextInFlight = true;
      void (async () => {
        try {
          await syncHistoryProgressIfPossible({ force: true });
        } catch (_error) {}
        try {
          await this.switchEpisodeByDelta(1, { showBoundaryToast: false });
        } finally {
          window.setTimeout(() => {
            this.autoNextInFlight = false;
          }, 800);
        }
      })();
    },
    goRawDirBack() {
      if (!this.canGoRawDirBack) return;
      const current = Array.isArray(this.rawDirPath) ? this.rawDirPath : [];
      this.rawDirPath = current.slice(0, -1);
    },
    cacheRecognitionForCurrentSiteResult() {
      const item = this.selectedSiteResultItem;
      const detail = this.siteResultDetailData;
      if (!item || !detail) return;
      if (this.isTmdbMode && this.tmdbMovieMode) {
        this.cacheRecognitionForSiteResult(item, detail, {
          mapping: null,
          mappingSignature: TMDB_MOVIE_RECOGNITION_SIGNATURE,
        });
        return;
      }
      if (this.fullEpisodeMapping && this.smartEpisodeMappingSignature) {
        this.cacheRecognitionForSiteResult(item, detail, {
          mapping: this.fullEpisodeMapping,
          mappingSignature: this.smartEpisodeMappingSignature,
        });
      }
    },
    syncSmartEpisodeMapping() {
      if (!this.isTmdbMode || this.tmdbMovieMode) {
        this.smartEpisodeMapping = null;
        return;
      }
      this.smartEpisodeMapping = buildSmartEpisodeMapping({
        tmdbDetail: {
          seasons: this.tmdbBaseSeasonRows.map((item) => ({
            season: item.season,
            episodes: item.episodes,
          })),
        },
        doubanMeta: this.detailDoubanData,
      });
      this.cacheRecognitionForCurrentSiteResult();
      this.syncHistoryDisplayContextIfReady();
    },
    togglePortraitMode() {
      const entering = !this.isPortraitMode;
      if (entering) {
        this.seedPortraitPlayerAreaWidth();
        this.episodePanelHiddenBeforePortrait = this.episodePanelHidden;
        this.episodePanelHidden = true;
      } else if (this.viewportWidth >= 1024) {
        this.episodePanelHidden = !!this.episodePanelHiddenBeforePortrait;
      }
      this.portraitMode = entering;
      this.siteSourceOpen = false;
      this.panSourceOpen = false;
      this.$nextTick(() => this.syncPlayerLayoutMetrics());
    },
    toggleEpisodePanel() {
      if (this.viewportWidth < 1024) return;
      this.episodePanelHidden = !this.episodePanelHidden;
      if (this.episodePanelHidden) {
        this.siteSourceOpen = false;
        this.panSourceOpen = false;
      }
      this.$nextTick(() => this.syncPlayerLayoutMetrics());
    },
    toggleThirdPartyExpanded() {
      this.thirdPartyExpanded = !this.thirdPartyExpanded;
    },
    openWithThirdPartyPlayer(player) {
      const target = player && typeof player === 'object' ? player : null;
      if (!target || !target.scheme) return;
      const durl = normalizeString(this.playerUrl);
      if (!durl) return;
      const name = normalizeString(this.displayTitle);
      const href = convertThirdPartyUrl(target.scheme, {
        raw_url: '',
        d_url: durl,
        name,
      });
      try {
        if (this.$refs.artPlayerRef && typeof this.$refs.artPlayerRef.pause === 'function') {
          this.$refs.artPlayerRef.pause();
        }
      } catch (_error) {
        // ignore player pause failure
      }
      window.setTimeout(() => {
        try {
          window.location.href = href;
        } catch (_error) {
          try {
            window.open(href, '_self');
          } catch (_error2) {
            // ignore scheme open failure
          }
        }
      }, 0);
    },
    async ensurePlayRuntimeSettings() {
      if (this.runtimeSettings) return this.runtimeSettings;
      const baseSettings =
        this.bootstrap && this.bootstrap.settings && typeof this.bootstrap.settings === 'object'
          ? this.bootstrap.settings
          : {};
      const playBootstrap = await fetchBootstrap('play').catch(() => ({}));
      const nextSettings = {
        ...baseSettings,
        ...(playBootstrap && playBootstrap.settings && typeof playBootstrap.settings === 'object'
          ? playBootstrap.settings
          : {}),
      };
      this.runtimeSettings = nextSettings;
      return nextSettings;
    },
    async loadDetailData() {
      const seq = this.detailFetchSeq + 1;
      this.detailFetchSeq = seq;
      this.detailLoading = true;
      this.detailOverview = '';
      this.detailYear = '';
      this.detailRemark = '';
      this.detailTMDBData = null;
      this.detailDoubanData = null;
      this.smartEpisodeMapping = null;
      this.doubanLoading = false;
      this.siteResultDetailData = null;
      this.siteResultDetailLoading = false;
      this.siteResultDetailError = '';
      this.autoplayConsumed = false;
      this.autoplayInFlight = false;
      this.historySmartBootstrapStageDone = {};
      this.manualSmartBootstrapStageDone = {};
      this.smartPlaybackResolvedStage = '';
      this.applyEpisodeViewModePreference();
      this.selectedViewSeasonNumber = 0;
      this.selectedViewRangeStart = 0;

      try {
        if (this.isTmdbMode) {
          const tmdbType = normalizeString(this.tmdbType || this.searchType).toLowerCase();
          const tmdbId = Math.max(0, normalizeInt(this.tmdbId));
          if (tmdbId <= 0 || (tmdbType !== 'movie' && tmdbType !== 'tv')) {
            this.detailLoading = false;
            return;
          }
          const detail = await fetchTMDBDetailCached({
            type: tmdbType,
            id: tmdbId,
          });
          if (seq !== this.detailFetchSeq) return;
          this.detailOverview = normalizeString(detail && detail.overview);
          this.detailYear = extractYearTextFromTMDBDetail(detail);
          this.detailRemark = '';
          this.detailTMDBData = detail && typeof detail === 'object' ? detail : null;
          if (tmdbType === 'tv') {
            await this.ensureTVMetaLoaded();
            if (seq !== this.detailFetchSeq) return;
          } else {
            this.detailDoubanData = null;
            this.detailDoubanMetaKey = '';
            this.doubanLoading = false;
          }
          this.syncSmartEpisodeMapping();
          this.syncPrimarySiteSourceOptions();
          this.$nextTick(() => this.applyEpisodeViewModePreference());
          this.detailLoading = false;
          return;
        }

        const settings = await this.ensurePlayRuntimeSettings();
        const apiBase = normalizeString(settings && settings.catpawrunnerApiBase);
        const spiderApi = normalizeString(this.spiderApi);
        const siteDetail = normalizeString(this.siteDetail);
        if (!apiBase || !spiderApi || !siteDetail) {
          this.detailLoading = false;
          return;
        }
        const raw = await fetchCatResolvedDetailCached({ apiBase, spiderApi, siteDetail, timeoutMs: 15000 });
        if (seq !== this.detailFetchSeq) return;
        this.detailOverview = stripMarkupText(raw && raw.content);
        this.detailYear = normalizeString(raw && raw.year);
        this.detailRemark = '';
        this.detailTMDBData = null;
        this.siteResultDetailData = raw && typeof raw === 'object' ? raw : null;
        this.selectedPanSource = this.siteDetailPanSources[0] ? this.siteDetailPanSources[0].key : '';
        this.syncSmartEpisodeMapping();
        this.syncHistoryDisplayContextIfReady();
        this.$nextTick(() => this.applyEpisodeViewModePreference());
        this.detailLoading = false;
      } catch (_error) {
        if (seq !== this.detailFetchSeq) return;
        this.detailLoading = false;
      }
    },
    async ensureTVMetaLoaded() {
      const currentKey = String(normalizeInt(this.tmdbId));
      if (!this.isTmdbMode || this.tmdbMovieMode) {
        this.detailDoubanData = null;
        this.detailDoubanMetaKey = '';
        this.doubanLoading = false;
        this.syncSmartEpisodeMapping();
        this.syncPrimarySiteSourceOptions();
        return null;
      }
      if (
        this.detailDoubanMetaKey === currentKey
        && this.detailDoubanData
        && typeof this.detailDoubanData === 'object'
      ) {
        const seasons = buildDoubanRenderedSeasonRows(this.detailDoubanData);
        if (seasons.length || this.doubanLoading === false) {
          return this.detailDoubanData;
        }
      }
      const tmdbId = normalizeInt(this.tmdbId);
      if (tmdbId <= 0) {
        this.detailDoubanData = null;
        this.detailDoubanMetaKey = '';
        this.doubanLoading = false;
        this.syncSmartEpisodeMapping();
        this.syncPrimarySiteSourceOptions();
        return null;
      }
      this.doubanLoading = true;
      try {
        const payload = await fetchTVMetaCached({ tmdbId });
        this.detailDoubanData = normalizeTVMetaPayload(payload);
        this.detailDoubanMetaKey = currentKey;
        this.syncSmartEpisodeMapping();
        this.syncPrimarySiteSourceOptions();
        return this.detailDoubanData;
      } catch (_error) {
        this.detailDoubanData = normalizeTVMetaPayload({});
        this.detailDoubanMetaKey = currentKey;
        this.syncSmartEpisodeMapping();
        this.syncPrimarySiteSourceOptions();
        return null;
      } finally {
        this.doubanLoading = false;
      }
    },
    async loadSelectedSiteResultDetail(item) {
      const target = item && typeof item === 'object' ? item : null;
      if (!target) {
        this.siteResultDetailData = null;
        this.siteResultDetailLoading = false;
        this.siteResultDetailError = '';
        return;
      }
      const targetKey = normalizeString(target.id);
      const cachedDetail = this.getCachedSiteResultDetail(target);
      if (cachedDetail && cachedDetail.resolutionComplete === true) {
        this.cacheRecognitionForSiteResult(target, cachedDetail);
        this.applySiteResultDetailUpdate(target, cachedDetail);
        this.siteResultDetailLoading = false;
        this.siteResultDetailError = '';
        this.$nextTick(() => this.applyEpisodeViewModePreference());
        return;
      }
      this.siteResultDetailData = null;
      this.siteResultDetailLoading = true;
      this.siteResultDetailError = '';
      const settings = await this.ensurePlayRuntimeSettings();
      const apiBase = normalizeString(settings && settings.catpawrunnerApiBase);
      const spiderApi = normalizeString(target.spiderApi);
      const siteDetail = normalizeString(target.siteDetail);
      if (!apiBase || !spiderApi || !siteDetail) {
        this.siteResultDetailData = null;
        this.siteResultDetailLoading = false;
        this.siteResultDetailError = '';
        return;
      }
      try {
        const detail = await this.ensureSiteResultDetailCached(target, {
          onUpdate: (nextDetail) => {
            const payload = nextDetail && typeof nextDetail === 'object' ? nextDetail : null;
            if (!payload) return;
            if (normalizeString(this.selectedSearchResultId) !== targetKey) return;
            this.cacheRecognitionForSiteResult(target, payload);
            this.applySiteResultDetailUpdate(target, payload);
          },
        });
        if (detail && typeof detail === 'object' && normalizeString(this.selectedSearchResultId) === targetKey) {
          this.cacheRecognitionForSiteResult(target, detail);
          this.applySiteResultDetailUpdate(target, detail);
        }
        this.syncHistoryDisplayContextIfReady();
        this.$nextTick(() => this.applyEpisodeViewModePreference());
        this.$nextTick(() => this.syncPlaybackDisplayFocus());
      } catch (_error) {
        this.siteResultDetailData = null;
        this.selectedPanSource = '';
        this.siteResultDetailError = _error && _error.message ? String(_error.message) : '暂无数据';
      } finally {
        this.siteResultDetailLoading = false;
      }
    },
    selectEpisodeSeason(seasonNumber) {
      const nextSeason = normalizeInt(seasonNumber);
      if (nextSeason <= 0) return;
      this.selectedViewSeasonNumber = nextSeason;
      const seasonRow = this.episodeSeasonRows.find((item) => item.season === nextSeason) || null;
      const episodeCount = seasonRow ? normalizeInt(seasonRow.episodes) : 0;
      if (episodeCount > 50) {
        this.selectedViewRangeStart = 1;
        this.$nextTick(() => this.ensureActivePlaybackRowVisible());
        return;
      }
      this.selectedViewRangeStart = 0;
      this.$nextTick(() => this.ensureActivePlaybackRowVisible());
    },
    selectEpisodeRange(rangeStart) {
      const nextStart = normalizeInt(rangeStart);
      if (nextStart <= 0) return;
      if (!this.episodeRangeOptions.some((item) => item.start === nextStart)) return;
      this.selectedViewRangeStart = nextStart;
      this.$nextTick(() => this.ensureActivePlaybackRowVisible());
    },
    syncSiteSourceWidth() {
      const el = this.$refs.siteSourceSelectEl;
      const width = el && typeof el.getBoundingClientRect === 'function'
        ? Math.round(el.getBoundingClientRect().width)
        : 0;
      this.siteSourceSelectWidth = width > 0 ? width : 0;
    },
    bindSiteSourceWidth() {
      this.$nextTick(() => {
        this.syncSiteSourceWidth();
        if (this.siteSourceResizeObserver) {
          this.siteSourceResizeObserver.disconnect();
          this.siteSourceResizeObserver = null;
        }
        const el = this.$refs.siteSourceSelectEl;
        if (typeof ResizeObserver === 'function' && el) {
          this.siteSourceResizeObserver = new ResizeObserver(() => this.syncSiteSourceWidth());
          this.siteSourceResizeObserver.observe(el);
        }
      });
    },
    syncViewportWidth() {
      if (typeof window === 'undefined') return;
      this.viewportWidth = Math.max(0, normalizeInt(window.innerWidth));
    },
    syncPlayerLayoutMetrics() {
      this.syncViewportWidth();
      this.syncPortraitPlayerAreaWidth();
      this.syncPlayerAreaHeight();
    },
    handleViewportResize() {
      this.syncPlayerLayoutMetrics();
    },
    syncPlayerAreaHeight() {
      const el = this.$refs.playerAreaContentEl || this.$refs.playerAreaEl;
      const height = el && typeof el.getBoundingClientRect === 'function'
        ? Math.round(el.getBoundingClientRect().height)
        : 0;
      this.playerAreaHeight = height > 0 ? height : 0;
    },
    syncPortraitPlayerAreaWidth() {
      if (!this.isPortraitMode) {
        this.portraitPlayerAreaWidth = 0;
        return;
      }
      const stageEl = this.$refs.playGridEl;
      const stageRect = stageEl && typeof stageEl.getBoundingClientRect === 'function'
        ? stageEl.getBoundingClientRect()
        : null;
      const stageWidth = stageRect ? Math.max(0, Math.floor(stageRect.width)) : 0;
      const stageHeight = stageRect ? Math.max(0, Math.floor(stageRect.height)) : 0;
      if (stageWidth <= 0 || stageHeight <= 0) {
        this.portraitPlayerAreaWidth = 0;
        return;
      }
      const PORTRAIT_ASPECT = 9 / 16;
      const widthByHeight = stageHeight * PORTRAIT_ASPECT;
      const nextWidth = Math.max(0, Math.floor(Math.min(stageWidth, widthByHeight)));
      this.portraitPlayerAreaWidth = nextWidth > 0 ? nextWidth : 0;
    },
    seedPortraitPlayerAreaWidth() {
      if (typeof window === 'undefined') return;
      const viewportWidth = Math.max(0, normalizeInt(window.innerWidth));
      const viewportHeight = Math.max(0, normalizeInt(window.innerHeight));
      if (viewportWidth <= 0 || viewportHeight <= 0) return;
      const PORTRAIT_ASPECT = 9 / 16;
      const portraitStageGapTop = 10;
      const portraitStageGapBottom = 10;
      const usableHeight = Math.max(0, viewportHeight - portraitStageGapTop - portraitStageGapBottom);
      const widthByHeight = usableHeight * PORTRAIT_ASPECT;
      const nextWidth = Math.max(0, Math.floor(Math.min(viewportWidth, widthByHeight)));
      if (nextWidth > 0) this.portraitPlayerAreaWidth = nextWidth;
    },
    bindPlayerAreaHeight() {
      this.$nextTick(() => {
        this.syncPlayerLayoutMetrics();
        if (this.playerAreaResizeObserver) {
          this.playerAreaResizeObserver.disconnect();
          this.playerAreaResizeObserver = null;
        }
        const playerEl = this.$refs.playerAreaContentEl || this.$refs.playerAreaEl;
        const gridEl = this.$refs.playGridEl;
        if (typeof ResizeObserver === 'function' && (playerEl || gridEl)) {
          this.playerAreaResizeObserver = new ResizeObserver(() => this.syncPlayerLayoutMetrics());
          if (playerEl) this.playerAreaResizeObserver.observe(playerEl);
          if (gridEl && gridEl !== playerEl) this.playerAreaResizeObserver.observe(gridEl);
        }
      });
    },
    getEpisodePanelWidthForColumns(columns) {
      const count = Math.max(1, Math.round(Number(columns) || 1));
      const gridWidth = (count * this.episodeButtonWidth)
        + ((count - 1) * this.episodeButtonGap)
        + this.episodeGridSidePadding;
      return gridWidth
        + this.episodeSelectorSidePadding
        + this.episodeSelectorDesktopOffset
        + this.episodeSelectorBorderWidth;
    },
    setEpisodePanelWidth(width) {
      const grid = this.$refs.playGridEl;
      if (!grid) return;
      const next = Math.max(this.minEpisodePanelWidth, Math.min(this.maxEpisodePanelWidth, Math.round(Number(width) || 0)));
      if (!next) return;
      grid.style.setProperty('--episode-panel-width', `${next}px`);
    },
    handleEpisodePanelResizeMove(event) {
      const state = this.episodePanelResizeState;
      if (!state.dragging) return;
      const x = event && typeof event.clientX === 'number' ? event.clientX : 0;
      const delta = x - state.startX;
      this.setEpisodePanelWidth(state.startWidth - delta);
    },
    handleEpisodePanelResizeUp() {
      const state = this.episodePanelResizeState;
      if (!state.dragging) return;
      state.dragging = false;
      document.body.style.userSelect = '';
      document.body.style.cursor = '';
      window.removeEventListener('mousemove', this.handleEpisodePanelResizeMove, true);
      window.removeEventListener('mouseup', this.handleEpisodePanelResizeUp, true);
    },
    handleEpisodePanelResizeDown(event) {
      if (window.innerWidth < 768) return;
      if (event && typeof event.preventDefault === 'function') event.preventDefault();
      const panel = this.$refs.episodePanelEl;
      if (!panel) return;
      const state = this.episodePanelResizeState;
      state.dragging = true;
      state.startX = event && typeof event.clientX === 'number' ? event.clientX : 0;
      state.startWidth = panel.getBoundingClientRect().width || 308;
      document.body.style.userSelect = 'none';
      document.body.style.cursor = 'col-resize';
      window.addEventListener('mousemove', this.handleEpisodePanelResizeMove, true);
      window.addEventListener('mouseup', this.handleEpisodePanelResizeUp, true);
    },
    bindEpisodePanelResizer() {
      const resizer = this.$refs.episodePanelResizerEl;
      if (!resizer) return;
      resizer.addEventListener('mousedown', this.handleEpisodePanelResizeDown);
    },
    unbindEpisodePanelResizer() {
      const resizer = this.$refs.episodePanelResizerEl;
      if (resizer) {
        resizer.removeEventListener('mousedown', this.handleEpisodePanelResizeDown);
      }
      this.handleEpisodePanelResizeUp();
    },
    handleDocumentClick(event) {
      const target = event.target;
      const siteSelect = this.$refs.siteSourceSelectEl;
      const panSelect = this.$refs.panSourceSelectEl;
      if (siteSelect && !siteSelect.contains(target)) this.siteSourceOpen = false;
      if (panSelect && !panSelect.contains(target)) this.panSourceOpen = false;
    },
  },
  watch: {
    switchOnlyToken() {
      this.cancelActivePlaybackFlow({ stopStream: true, invalidateDetail: true, clearPlayerState: true });
      this.autoplayConsumed = false;
      this.autoplayInFlight = false;
      this.historySmartBootstrapStageDone = {};
      this.manualSmartBootstrapStageDone = {};
      this.smartPlaybackResolvedStage = '';
      this.loadPlayRuntimeAndDetail();
    },
    playSearchQueryOriginal: {
      immediate: true,
      handler() {
        this.bindPlaySearchQuerySubscription();
      },
    },
    playSearchPrimaryScope() {
      this.bindPlaySearchQuerySubscription();
    },
    detailLoading(next, prev) {
      if (prev === true && next === false) this.scheduleAutoplayCheck();
    },
    siteResultDetailLoading(next, prev) {
      if (prev === true && next === false) this.scheduleAutoplayCheck();
    },
    'episodeButtons.length'(next, prev) {
      if (normalizeInt(prev) <= 0 && normalizeInt(next) > 0) this.scheduleAutoplayCheck();
    },
    'rawListItems.length'(next, prev) {
      if (normalizeInt(prev) <= 0 && normalizeInt(next) > 0) this.scheduleAutoplayCheck();
    },
    forceRawListMode: {
      immediate: true,
      handler(next) {
        if (next) {
          this.rawListMode = true;
          return;
        }
        this.applyEpisodeViewModePreference();
      },
    },
    forceDirectSiteRawListMode: {
      immediate: true,
      handler(next) {
        if (next) {
          this.rawListMode = true;
          return;
        }
        this.applyEpisodeViewModePreference();
      },
    },
    rawDirIdentity: {
      immediate: true,
      handler(next) {
        const identity = normalizeString(next);
        if (!this.rawDirModeEnabled) {
          this.rawDirPath = [];
          this.rawDirLockedDepth = 0;
          this.lastRawDirIdentity = identity;
          return;
        }
        const tree = this.rawDirTree;
        if (!tree) return;
        const panChanged = identity !== this.lastRawDirIdentity;
        this.lastRawDirIdentity = identity;
        if (panChanged) {
          this.rawDirPath = [];
          this.rawDirLockedDepth = 0;
        }

        const tryGetNode = (path) => {
          let node = tree;
          for (let i = 0; i < path.length; i += 1) {
            const seg = normalizeString(path[i]);
            if (!seg) continue;
            if (!node.dirs || !node.dirs.has(seg)) return null;
            node = node.dirs.get(seg);
          }
          return node;
        };

        const currentPath = Array.isArray(this.rawDirPath) ? this.rawDirPath : [];
        if (currentPath.length && !tryGetNode(currentPath)) {
          this.rawDirPath = [];
          this.rawDirLockedDepth = 0;
        }

        if (!this.rawDirPath.length) {
          const nextPath = [];
          let node = tree;
          while (node) {
            const dirs = node.dirs ? Array.from(node.dirs.keys()) : [];
            const hasFiles = Array.isArray(node.files) && node.files.length > 0;
            if (hasFiles) break;
            if (dirs.length !== 1) break;
            const only = normalizeString(dirs[0]);
            if (!only) break;
            nextPath.push(only);
            node = node.dirs.get(only);
          }
          if (nextPath.length) {
            this.rawDirPath = nextPath;
            this.rawDirLockedDepth = nextPath.length;
          }
        }
      },
    },
  },
};
</script>
<style>
.play-page {
  --play-player-radius: 12px;
  --play-video-ratio-padding-top: 56.25%;
  flex: 1 1 auto;
  padding-top: calc(3rem + env(safe-area-inset-top));
  padding-bottom: calc(3.5rem + env(safe-area-inset-bottom));
}

.play-page-root {
  background: transparent;
  color: inherit;
}

.dark .play-page-root {
  background: #0f172a;
  color: #e5e7eb;
}

@media (min-width: 768px) {
  .play-page {
    margin-top: 0;
    padding-top: 0;
    padding-bottom: 0;
  }
}

.play-page__content {
  --play-main-inset: 0px;
  display: flex;
  flex-direction: column;
  gap: 0;
}

@media (min-width: 768px) {
  .play-page__content {
    --play-main-inset: 40px;
  }
}

.play-header {
  margin: 0 0 8px;
}

.play-header__title {
  min-width: 0;
  font-size: 20px;
  font-weight: 600;
  color: rgba(17, 24, 39, 1);
  margin: 0;
}

.dark .play-header__title {
  color: rgba(243, 244, 246, 1);
}

.play-page__main {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-left: var(--play-main-inset);
  padding-right: var(--play-main-inset);
}

.play-page--portrait {
  --portrait-stage-gap-top: 10px;
  --portrait-stage-gap-bottom: 10px;
  --play-player-radius: 18px;
  --play-video-ratio-padding-top: 177.7777778%;
  position: fixed;
  inset: 0;
  z-index: 80;
  width: 100vw;
  height: 100svh;
  min-height: 100svh;
  padding-top: 0 !important;
  padding-bottom: 0 !important;
  overflow: hidden;
}

@media (min-width: 768px) {
  .play-page--portrait {
    z-index: 1;
  }
}

.play-page--portrait .play-page__content {
  --play-main-inset: 0px;
  height: 100%;
  min-height: 100%;
  overflow: hidden;
}

.play-page--portrait .play-header {
  display: none;
}

.play-page--portrait .play-page__main {
  flex: 1 1 auto;
  min-height: 0;
  gap: 0;
  padding-top: var(--portrait-stage-gap-top);
  padding-bottom: var(--portrait-stage-gap-bottom);
  padding-left: 0;
  padding-right: 0;
  box-sizing: border-box;
  overflow: hidden;
}

.play-episode-toggle-wrap {
  display: none;
  gap: 8px;
}

@media (min-width: 1024px) {
  .play-episode-toggle-wrap {
    display: flex;
    align-items: center;
    justify-content: flex-end;
  }
}

.play-page--portrait .play-episode-toggle-wrap {
  display: none !important;
}

.play-episode-toggle {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 999px;
  border: 1px solid rgba(229, 231, 235, 0.5);
  background: rgba(255, 255, 255, 0.8);
  color: rgba(55, 65, 81, 1);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.06);
  backdrop-filter: blur(8px);
  transition: background-color 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
}

.play-episode-toggle:hover {
  background: #fff;
  box-shadow: 0 8px 18px rgba(0, 0, 0, 0.08);
}

.dark .play-episode-toggle {
  background: rgba(31, 41, 55, 0.8);
  border-color: rgba(55, 65, 81, 0.5);
  color: rgba(229, 231, 235, 1);
  box-shadow: none;
}

.dark .play-episode-toggle:hover {
  background: rgba(31, 41, 55, 1);
}

.play-episode-toggle__icon {
  width: 14px;
  height: 14px;
  color: rgba(107, 114, 128, 1);
  transition: transform 0.2s ease;
}

.play-episode-toggle__icon--collapsed {
  transform: rotate(180deg);
}

.dark .play-episode-toggle__icon {
  color: rgba(156, 163, 175, 1);
}

.play-episode-toggle__label {
  font-size: 12px;
  font-weight: 500;
  color: rgba(75, 85, 99, 1);
}

.dark .play-episode-toggle__label {
  color: rgba(209, 213, 219, 1);
}

.play-episode-toggle__dot {
  position: absolute;
  top: -2px;
  right: -2px;
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: rgba(74, 222, 128, 1);
}

.play-grid {
  display: grid;
  gap: 16px;
  align-items: start;
}

.play-page--portrait .play-grid {
  gap: 0;
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  grid-template-columns: minmax(0, 1fr) !important;
  place-items: center;
  overflow: hidden;
}

.play-player-area {
  border-radius: var(--play-player-radius);
  border: 1px solid transparent;
  overflow: hidden;
}

.play-page--portrait .play-player-area {
  width: 100%;
  max-width: 100%;
  margin-left: auto;
  margin-right: auto;
  min-height: 0;
  border-width: 0;
}

.dark .play-player-area {
  border-color: rgba(255, 255, 255, 0.3);
}

.play-episode-panel {
  position: relative;
  width: 100%;
  min-height: 0;
}

@media (min-width: 768px) {
  .play-episode-panel {
    min-height: 0;
    overflow: hidden;
  }
}

@media (min-width: 1024px) {
  .play-episode-panel {
    opacity: 1;
    transform: scale(1);
  }
}

.play-episode-selector {
  height: 100%;
  min-height: 0;
  padding: 0 16px;
  border-radius: 12px;
  border: 1px solid transparent;
  background: rgba(0, 0, 0, 0.1);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

@media (min-width: 768px) {
  .play-episode-selector {
    margin-left: 8px;
  }
}

.dark .play-episode-selector {
  border-color: rgba(255, 255, 255, 0.3);
  background: rgba(255, 255, 255, 0.05);
}

.play-episode-tabs {
  display: flex;
  margin: 0 -24px 4px;
  flex-shrink: 0;
}

.play-episode-body {
  display: flex;
  flex-direction: column;
  gap: 0;
  padding-bottom: 4px;
  min-height: 0;
  flex: 1 1 auto;
  overflow: hidden;
}

.play-episode-content {
  position: relative;
  display: flex;
  flex: 1 1 auto;
  min-height: 0;
  flex-direction: column;
  overflow: hidden;
}

.play-episode-loading {
  position: absolute;
  inset: 0;
  z-index: 2;
  pointer-events: none;
  flex: 1 1 auto;
  min-height: 0;
}

.play-episode-overlay {
  position: absolute;
  inset: 0;
  z-index: 2;
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
  color: rgba(107, 114, 128, 1);
}

.play-episode-overlay__inner {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  text-align: center;
}

.play-player-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
  z-index: 20;
  padding: 18px;
  background: radial-gradient(circle at 50% 45%, rgba(2, 6, 23, 0.78) 0%, rgba(2, 6, 23, 0.35) 55%, rgba(2, 6, 23, 0) 100%);
  backdrop-filter: none;
}

.play-page--portrait .play-player-overlay {
  background: transparent;
}

.play-player-overlay__panel {
  width: min(760px, 92%);
  min-height: 200px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 14px 16px;
  border-radius: 22px;
  background: transparent;
  border: 1px solid transparent;
  box-shadow: none;
  backdrop-filter: none;
}

.play-player-overlay__text {
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: center;
  text-align: center;
  gap: 0;
}

.play-player-overlay__status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: rgba(248, 250, 252, 0.96);
}

.play-player-overlay--error .play-player-overlay__status {
  color: rgba(254, 226, 226, 0.98);
}

.play-player-overlay__statusText {
  font-size: 26px;
  font-weight: 900;
  letter-spacing: 0.2px;
  line-height: 1.05;
}

.play-player-overlay__spinner {
  border-color: rgba(255, 255, 255, 0.26);
  border-top-color: rgba(255, 255, 255, 0.96);
}

.play-player-overlay__progress {
  width: min(520px, 100%);
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 14px;
}

.play-player-overlay__track {
  height: 6px;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.12);
  overflow: hidden;
}

.play-player-overlay__fill {
  height: 100%;
  width: calc(var(--play-stage-p, 0) * 100%);
  border-radius: 9999px;
  background: linear-gradient(90deg, #0ea5e9, #22c55e, #6366f1);
  box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.14) inset;
}

.play-player-overlay--error .play-player-overlay__fill {
  background: linear-gradient(90deg, rgba(248, 113, 113, 0.95), rgba(251, 146, 60, 0.95));
}

.play-picker-row {
  margin: 0 -24px 12px;
  padding: 0 24px;
}

.play-picker {
  width: 100%;
}

.play-picker .ui-selectbox__option.is-disabled {
  color: rgba(107, 114, 128, 1);
  cursor: default;
  pointer-events: none;
}

.dark .play-picker .ui-selectbox__option.is-disabled {
  color: rgba(156, 163, 175, 1);
}

.play-season-bar,
.play-range-bar {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  margin-bottom: 0;
  width: 100%;
}

.play-season-bar--with-range {
  margin-bottom: 10px;
}

.play-season-bar--tight,
.play-range-bar--tight {
  margin-bottom: 0;
}

.play-season-tabs,
.play-range-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  overflow: visible;
  padding: 2px 0;
  width: 100%;
}

.play-season-btn,
.play-range-btn {
  flex: 0 0 auto;
  max-width: 100%;
  min-width: fit-content;
  height: 28px;
  padding: 0 12px;
  border-radius: 999px;
  border: 1px solid rgba(0, 0, 0, 0.08);
  background: rgba(0, 0, 0, 0.04);
  color: rgba(107, 114, 128, 1);
  font-size: 12px;
  font-weight: 700;
  line-height: 1;
  transition: background-color 0.15s ease, border-color 0.15s ease, color 0.15s ease;
  white-space: nowrap;
}

.play-season-btn:hover,
.play-range-btn:hover {
  background: rgba(0, 0, 0, 0.06);
}

.play-season-btn--active,
.play-range-btn--active {
  background: rgba(34, 197, 94, 0.16);
  border-color: rgba(34, 197, 94, 0.4);
  color: rgba(22, 163, 74, 1);
}

.dark .play-season-btn,
.dark .play-range-btn {
  background: rgba(255, 255, 255, 0.06);
  border-color: rgba(255, 255, 255, 0.12);
  color: rgba(209, 213, 219, 1);
}

.dark .play-season-btn:hover,
.dark .play-range-btn:hover {
  background: rgba(255, 255, 255, 0.08);
}

.dark .play-season-btn--active,
.dark .play-range-btn--active {
  background: rgba(34, 197, 94, 0.18);
  border-color: rgba(34, 197, 94, 0.42);
  color: rgba(74, 222, 128, 1);
}

.play-episode-divider {
  height: 1px;
  width: 100%;
  background: rgba(0, 0, 0, 0.08);
  margin-top: 0;
  margin-bottom: 12px;
}

.dark .play-episode-divider {
  background: rgba(255, 255, 255, 0.12);
}

.play-episode-grid {
  position: relative;
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  overflow-x: hidden;
  overflow-y: auto;
  flex: 1 1 auto;
  min-height: 0;
  align-content: flex-start;
  padding: 4px 2px 16px;
  scrollbar-width: none;
  -ms-overflow-style: none;
}

.play-episode-grid::-webkit-scrollbar {
  width: 0;
  height: 0;
}

.play-raw-list {
  position: relative;
  display: flex;
  flex: 1 1 auto;
  min-height: 0;
  flex-direction: column;
  overflow: hidden;
}

.play-raw-list__path {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
  color: rgba(75, 85, 99, 1);
  font-size: 12px;
  font-weight: 500;
  line-height: 1.4;
}

.play-raw-list__pathText {
  flex: 1 1 auto;
  min-width: 0;
  white-space: normal;
  overflow-wrap: anywhere;
  word-break: break-word;
}

.play-raw-list__pathBtn {
  flex: 0 0 auto;
  min-width: 52px;
  white-space: nowrap;
}


.play-raw-list__items {
  display: flex;
  flex: 1 1 auto;
  min-height: 0;
  flex-direction: column;
  gap: 10px;
  overflow-y: auto;
  scrollbar-width: none;
  -ms-overflow-style: none;
}

.play-raw-list__items::-webkit-scrollbar {
  width: 0;
  height: 0;
}

.play-raw-list__row {
  appearance: none;
  width: 100%;
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 40px;
  padding: 10px 14px;
  border-radius: 12px;
  border: 1px solid rgba(0, 0, 0, 0.06);
  background: rgba(255, 255, 255, 0.55);
  color: rgba(31, 41, 55, 1);
  font-size: 13px;
  font-weight: 600;
  box-shadow: none;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  text-align: left;
}

.play-raw-list__row:hover {
  background: rgba(255, 255, 255, 0.72);
}

.play-raw-list__row.is-current {
  cursor: default;
}

.play-raw-list__row.is-current:hover {
  background: rgba(34, 197, 94, 0.12);
}

.play-raw-list__row--active {
  border-color: rgba(34, 197, 94, 0.42);
  background: rgba(34, 197, 94, 0.12);
  color: rgba(22, 163, 74, 1);
}

.play-raw-list__text {
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
}

.play-raw-list__empty {
  color: rgba(107, 114, 128, 1);
  font-size: 13px;
  font-weight: 600;
}

.play-episode-overlay .play-raw-list__empty {
  color: inherit;
}

.dark .play-raw-list__path {
  color: rgba(209, 213, 219, 1);
}


.dark .play-raw-list__row {
  border-color: rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.06);
  color: rgba(229, 231, 235, 1);
  box-shadow: none;
}

.dark .play-raw-list__row:hover {
  background: rgba(255, 255, 255, 0.09);
}

.dark .play-raw-list__row.is-current:hover {
  background: rgba(34, 197, 94, 0.18);
}

.dark .play-raw-list__row--active {
  border-color: rgba(34, 197, 94, 0.42);
  background: rgba(34, 197, 94, 0.18);
  color: rgba(74, 222, 128, 1);
}

.dark .play-raw-list__empty {
  color: rgba(156, 163, 175, 1);
}

.dark .play-episode-overlay {
  color: rgba(156, 163, 175, 1);
}

.play-episode-btn {
  position: relative;
  width: 44px;
  height: 44px;
  border-radius: 6px;
  border: 1px solid rgba(0, 0, 0, 0.08);
  background: rgba(229, 231, 235, 1);
  color: rgba(55, 65, 81, 1);
  font-size: 14px;
  font-weight: 500;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  transition: background-color 0.2s ease, border-color 0.2s ease, box-shadow 0.2s ease;
  white-space: nowrap;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace;
}

.play-episode-btn__text {
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.play-episode-btn__badge {
  position: absolute;
  top: -4px;
  right: -4px;
  min-width: 18px;
  height: 12px;
  padding: 0 4px;
  border-radius: 999px;
  background: rgba(34, 197, 94, 1);
  color: #fff;
  font-size: 9px;
  font-weight: 700;
  line-height: 12px;
  text-align: center;
  box-shadow: 0 4px 10px rgba(34, 197, 94, 0.24);
}

.play-episode-btn--active {
  background: rgba(34, 197, 94, 1);
  border-color: rgba(34, 197, 94, 1);
  color: #fff;
  box-shadow: 0 10px 20px rgba(34, 197, 94, 0.25);
}

.play-episode-btn:hover {
  background: rgba(209, 213, 219, 1);
  border-color: rgba(148, 163, 184, 0.7);
  box-shadow: 0 6px 14px rgba(15, 23, 42, 0.08);
}

.play-episode-btn.is-current {
  cursor: default;
}

.play-episode-btn.is-current:hover {
  background: rgba(34, 197, 94, 1);
  border-color: rgba(34, 197, 94, 1);
  box-shadow: 0 10px 20px rgba(34, 197, 94, 0.25);
}

.dark .play-episode-btn {
  background: rgba(255, 255, 255, 0.1);
  border-color: rgba(255, 255, 255, 0.12);
  color: rgba(209, 213, 219, 1);
}

.dark .play-episode-btn--active {
  background: rgba(22, 163, 74, 1);
  border-color: rgba(22, 163, 74, 1);
  color: #fff;
}

.dark .play-episode-btn:hover {
  background: rgba(255, 255, 255, 0.2);
  border-color: rgba(255, 255, 255, 0.2);
  box-shadow: none;
}

.dark .play-episode-btn.is-current:hover {
  background: rgba(22, 163, 74, 1);
  border-color: rgba(22, 163, 74, 1);
}

@media (min-width: 768px) {
  .play-episode-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(44px, 44px));
    justify-content: start;
  }
}

.episode-tab-btn {
  appearance: none;
  -webkit-appearance: none;
  flex: 1 1 0;
  padding: 12px 24px;
  border: 0;
  text-align: center;
  cursor: pointer;
  transition: color 0.2s ease, background-color 0.2s ease;
  font-weight: 500;
  color: rgba(55, 65, 81, 1);
  background: rgba(0, 0, 0, 0.05);
  box-shadow: none;
}

.episode-tab-btn:hover {
  color: rgba(22, 163, 74, 1);
}

.episode-tab-btn--active {
  color: rgba(22, 163, 74, 1);
  background: transparent;
}

.dark .episode-tab-btn {
  color: rgba(209, 213, 219, 1);
  background: rgba(255, 255, 255, 0.05);
}

.dark .episode-tab-btn:hover,
.dark .episode-tab-btn--active {
  color: rgba(74, 222, 128, 1);
}

.play-thirdparty-bar {
  width: 100%;
  border-radius: 12px;
  border: 1px solid transparent;
  background: rgba(255, 255, 255, 0.75);
  backdrop-filter: blur(8px);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.06);
}

.dark .play-thirdparty-bar {
  border-color: rgba(255, 255, 255, 0.3);
  background: rgba(255, 255, 255, 0.05);
}

.play-thirdparty-bar__inner {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 8px;
}

@media (min-width: 768px) {
  #playGrid {
    grid-template-columns: minmax(0, 1fr) var(--episode-panel-width, 314px);
  }

  #playGrid.play-grid--panel-hidden {
    grid-template-columns: minmax(0, 1fr);
  }

  #playGrid.play-grid--panel-hidden #playerArea {
    grid-column: 1 / -1 !important;
  }

  .play-page--portrait #playerArea {
    grid-column: 1 / -1 !important;
  }

  #playerArea {
    grid-column: 1 / 2 !important;
  }

  #episodePanel {
    grid-column: 2 / 3 !important;
    min-height: 0;
  }


}

.play-video-ratio {
  position: relative;
  width: 100%;
  background: #000;
  min-height: 240px;
  border-radius: var(--play-player-radius);
  overflow: hidden;
  box-shadow: 0 10px 24px rgba(15, 23, 42, 0.18);
}

.play-video-ratio::before {
  content: "";
  display: block;
  padding-top: var(--play-video-ratio-padding-top);
}

.play-video-ratio__inner {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  border-radius: inherit;
  overflow: hidden;
}

.play-player-stack {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.play-page--portrait .play-thirdparty-bar {
  display: none;
}

.play-thirdparty-btn,
.play-thirdparty-expand {
  width: 40px;
  height: 40px;
  border-radius: 12px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid rgba(0, 0, 0, 0.08);
  background: rgba(255, 255, 255, 0.6);
  color: rgba(55, 65, 81, 1);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.06);
  transition: transform 0.12s ease, background-color 0.15s ease, border-color 0.15s ease, opacity 0.15s ease;
  user-select: none;
}

.play-thirdparty-btn:hover,
.play-thirdparty-expand:hover {
  background: rgba(255, 255, 255, 0.8);
}

.play-thirdparty-btn:active,
.play-thirdparty-expand:active {
  transform: translateY(1px);
}

.play-thirdparty-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.play-thirdparty-icon {
  width: 30px;
  height: 30px;
  object-fit: contain;
}

.play-thirdparty-expand__ico {
  width: 18px;
  height: 18px;
  transition: transform 0.2s ease;
}

.play-thirdparty-expand__ico--open {
  transform: rotate(180deg);
}

.dark .play-thirdparty-btn,
.dark .play-thirdparty-expand {
  border-color: rgba(255, 255, 255, 0.14);
  background: rgba(255, 255, 255, 0.07);
  color: rgba(229, 231, 235, 1);
  box-shadow: none;
}

.dark .play-thirdparty-btn:hover,
.dark .play-thirdparty-expand:hover {
  background: rgba(255, 255, 255, 0.1);
}

@media (max-width: 767px) {
  .play-thirdparty-bar__inner {
    position: relative;
    justify-content: center;
    padding-left: 48px;
    padding-right: 48px;
  }

  .play-thirdparty-expand {
    position: absolute;
    right: 8px;
    top: 50%;
    transform: translateY(-50%);
  }
}

.episode-resizer {
  display: none;
}

.episode-resizer::before {
  content: "";
  position: absolute;
  left: 13px;
  top: 0;
  bottom: 0;
  width: 2px;
  border-radius: 999px;
  background: rgba(0, 0, 0, 0.14);
  opacity: 0;
  transition: opacity 0.15s ease, background-color 0.15s ease;
}

.episode-resizer:hover::before {
  opacity: 0.9;
}

.dark .episode-resizer::before {
  background: rgba(255, 255, 255, 0.22);
}

@media (min-width: 768px) {
  .episode-resizer {
    position: absolute;
    display: block;
    left: -14px;
    top: 10px;
    bottom: 10px;
    width: 28px;
    cursor: col-resize;
    z-index: 20;
  }
}

#episodeSelector {
  overflow-x: hidden;
  overflow-y: visible;
}

.episode-tab-header {
  position: sticky;
  top: 0;
  z-index: 20;
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  background: rgba(0, 0, 0, 0.06);
}

.dark .episode-tab-header {
  background: rgba(255, 255, 255, 0.06);
}



.play-detail__inner {
  display: flex;
  gap: 20px;
  align-items: flex-start;
}

.play-detail {
  margin-top: 16px;
  margin-left: var(--play-main-inset, 0px);
  margin-right: var(--play-main-inset, 0px);
  background: rgba(255, 255, 255, 0.8);
  border-radius: 16px;
  padding: 16px;
  border: 1px solid rgba(229, 231, 235, 0.3);
  backdrop-filter: blur(8px);
}

.play-page--portrait .play-detail {
  display: none;
}

@media (min-width: 640px) {
  .play-detail {
    padding: 24px;
  }
}

.dark .play-detail {
  background: rgba(31, 41, 55, 0.4);
  border-color: rgba(55, 65, 81, 0.3);
}

.play-detail__poster {
  flex: 0 0 200px;
}

.play-detail__posterWrap {
  position: relative;
  width: 100%;
  border-radius: 18px;
  overflow: hidden;
  background: rgba(0, 0, 0, 0.04);
  aspect-ratio: 2 / 3;
}

.play-detail__posterImg {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.play-detail__posterSkeleton {
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, rgba(0, 0, 0, 0.04) 0%, rgba(0, 0, 0, 0.08) 50%, rgba(0, 0, 0, 0.04) 100%);
  background-size: 200% 100%;
  animation: play-skeleton 1.2s ease-in-out infinite;
}


.play-detail__info {
  flex: 1 1 auto;
  min-width: 0;
}

.play-detail__titleRow {
  display: flex;
  align-items: center;
  gap: 12px;
}

.play-detail__favBtn {
  flex: 0 0 auto;
  width: 38px;
  height: 38px;
  border-radius: 9999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid rgba(148, 163, 184, 0.45);
  background: rgba(255, 255, 255, 0.72);
  color: rgba(71, 85, 105, 1);
  box-shadow: 0 8px 18px rgba(15, 23, 42, 0.08);
  transition: transform 0.15s ease, background 0.15s ease, box-shadow 0.15s ease, color 0.15s ease;
}
.play-detail__favBtn:hover {
  transform: translateY(-1px);
  background: rgba(255, 255, 255, 0.92);
  box-shadow: 0 12px 28px rgba(15, 23, 42, 0.12);
}
.play-detail__favBtn.is-active {
  border-color: rgba(236, 72, 153, 0.35);
  color: rgba(236, 72, 153, 1);
}
.dark .play-detail__favBtn {
  background: rgba(15, 23, 42, 0.55);
  border-color: rgba(255, 255, 255, 0.12);
  color: rgba(226, 232, 240, 1);
  box-shadow: none;
}
.dark .play-detail__favBtn:hover {
  background: rgba(15, 23, 42, 0.75);
}
.dark .play-detail__favBtn.is-active {
  border-color: rgba(244, 114, 182, 0.35);
  color: rgba(244, 114, 182, 1);
}

.play-detail__title {
  flex: 0 1 auto;
  min-width: 0;
  max-width: calc(100% - 54px);
  font-size: 26px;
  line-height: 1.2;
  font-weight: 800;
  color: rgba(17, 24, 39, 1);
  letter-spacing: -0.02em;
  margin: 0;
}

.play-header__titleText,
.play-detail__titleText {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.play-detail__favIcon {
  width: 16px;
  height: 16px;
}

.play-detail__meta {
  margin-top: 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
}

.play-pill {
  display: inline-flex;
  align-items: center;
  height: 28px;
  padding: 0 12px;
  border-radius: 999px;
  font-size: 13px;
  font-weight: 600;
  color: rgba(55, 65, 81, 1);
  background: rgba(0, 0, 0, 0.06);
}

.play-detail__desc {
  margin-top: 14px;
  font-size: 15px;
  line-height: 1.7;
  color: rgba(31, 41, 55, 0.92);
}


@media (max-width: 768px) {
  .play-detail__inner {
    flex-direction: column;
  }
  .play-detail__poster {
    flex-basis: auto;
    width: 220px;
  }
  .play-detail__title {
    font-size: 22px;
  }
}

@media (max-width: 520px) {
  .play-player-overlay__panel {
    width: min(520px, 92%);
    min-height: 180px;
    padding: 16px 14px 14px;
  }

  .play-player-overlay__status {
    justify-content: center;
  }

  .play-player-overlay__progress {
    width: 100%;
    margin-left: auto;
    margin-right: auto;
  }

  .play-player-overlay__text {
    padding-right: 0;
    gap: 0;
  }

  .play-player-overlay__statusText {
    font-size: 20px;
  }
}

.dark .play-detail__posterWrap {
  background: rgba(255, 255, 255, 0.06);
}

.dark .play-detail__posterSkeleton {
  background: linear-gradient(90deg, rgba(255, 255, 255, 0.06) 0%, rgba(255, 255, 255, 0.1) 50%, rgba(255, 255, 255, 0.06) 100%);
  background-size: 200% 100%;
}

.dark .play-detail__title {
  color: rgba(243, 244, 246, 1);
}

.dark .play-pill {
  color: rgba(229, 231, 235, 1);
  background: rgba(255, 255, 255, 0.08);
}

.dark .play-detail__desc {
  color: rgba(229, 231, 235, 0.9);
}


@keyframes play-skeleton {
  0% { background-position: 0% 50%; }
  100% { background-position: 200% 50%; }
}

</style>
