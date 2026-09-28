import { reactive } from 'vue';
import { normalizecatpawrunnerApiBase, requestCatPlay } from './catpawrunner';
import { extractTianyiShareCodeAndAccessCode } from '../utils/matchCore';
import { normalizeInt, normalizeString } from './normalize';
import { normalizeHttpBase } from './urlText';

export const playbackSessionState = reactive({
  currentContext: {
    itemId: '',
    siteKey: '',
    siteName: '',
    spiderApi: '',
    siteDetail: '',
    panKey: '',
    panFlag: '',
    selectionKey: '',
    globalEpisode: 0,
    itemIndex: -1,
    sourceQuality: '',
    quality: '',
    pathName: '',
    rawFileName: '',
    fileIdentity: '',
    sourceKind: '',
    sourceSignature: '',
  },
  lastBrowseContext: {
    itemId: '',
    panKey: '',
  },
});

export const setCurrentPlaybackContext = (payload = {}) => {
  const raw = payload && typeof payload === 'object' ? payload : {};
  playbackSessionState.currentContext = {
    itemId: normalizeString(raw.itemId),
    siteKey: normalizeString(raw.siteKey),
    siteName: normalizeString(raw.siteName),
    spiderApi: normalizeString(raw.spiderApi),
    siteDetail: normalizeString(raw.siteDetail),
    panKey: normalizeString(raw.panKey),
    panFlag: normalizeString(raw.panFlag),
    selectionKey: normalizeString(raw.selectionKey),
    globalEpisode: Math.max(0, normalizeInt(raw.globalEpisode)),
    itemIndex: normalizeInt(raw.itemIndex),
    sourceQuality: normalizeString(raw.sourceQuality),
    quality: normalizeString(raw.quality),
    pathName: normalizeString(raw.pathName),
    rawFileName: normalizeString(raw.rawFileName),
    fileIdentity: normalizeString(raw.fileIdentity),
    sourceKind: normalizeString(raw.sourceKind),
    sourceSignature: normalizeString(raw.sourceSignature),
  };
  return playbackSessionState.currentContext;
};

export const patchCurrentPlaybackContext = (payload = {}) => {
  const current = playbackSessionState.currentContext && typeof playbackSessionState.currentContext === 'object'
    ? playbackSessionState.currentContext
    : {};
  return setCurrentPlaybackContext({
    ...current,
    ...(payload && typeof payload === 'object' ? payload : {}),
  });
};

export const clearCurrentPlaybackContext = () => {
  setCurrentPlaybackContext({});
};

const DEFAULT_PLAYER_QUALITY_OPTIONS = [
  { value: '4k_hdr', label: '4K·HDR' },
  { value: '4k_fps', label: '4K·60帧' },
  { value: '4k', label: '4K' },
  { value: '1080p', label: '1080P' },
  { value: '720p', label: '720P' },
];

export const setLastBrowsePlaybackContext = (payload = {}) => {
  const raw = payload && typeof payload === 'object' ? payload : {};
  playbackSessionState.lastBrowseContext = {
    itemId: normalizeString(raw.itemId),
    panKey: normalizeString(raw.panKey),
  };
  return playbackSessionState.lastBrowseContext;
};

export const patchLastBrowsePlaybackContext = (payload = {}) => {
  const current = playbackSessionState.lastBrowseContext && typeof playbackSessionState.lastBrowseContext === 'object'
    ? playbackSessionState.lastBrowseContext
    : {};
  return setLastBrowsePlaybackContext({
    ...current,
    ...(payload && typeof payload === 'object' ? payload : {}),
  });
};

export const normalizePanAliasMappingsForUi = (list) => {
  const arr = Array.isArray(list) ? list : [];
  const out = [];
  const seen = new Set();
  arr.forEach((item) => {
    const pan = normalizeString(item && item.pan);
    if (!pan) return;
    const key = pan.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    const aliases = normalizeString(item && item.aliases)
      .split(',')
      .map(normalizeString)
      .filter(Boolean);
    out.push({ pan, aliases });
  });
  return out;
};

export const resolvePanFamilyLabelForUi = (value, runtimeSettings) => {
  const hay = normalizeString(value).toLowerCase();
  if (!hay) return '';
  const mappings = normalizePanAliasMappingsForUi(runtimeSettings && runtimeSettings.smartPanAliasMappings);
  for (let i = 0; i < mappings.length; i += 1) {
    const item = mappings[i];
    const aliases = [item.pan].concat(Array.isArray(item.aliases) ? item.aliases : []);
    const hit = aliases.some((alias) => {
      const token = normalizeString(alias).toLowerCase();
      return token && hay.includes(token);
    });
    if (hit) return item.pan;
  }
  const tokens = Array.isArray(runtimeSettings && runtimeSettings.smartPanMatchTokens)
    ? runtimeSettings.smartPanMatchTokens.map(normalizeString).filter(Boolean)
    : [];
  return tokens.find((token) => hay.includes(token.toLowerCase())) || '';
};

export const groupPanSourceOptionsForUi = (options, runtimeSettings) => {
  const list = Array.isArray(options) ? options : [];
  const out = [];
  const seen = new Set();
  let hasUnknown = false;
  list.forEach((option) => {
    const label = normalizeString(option && option.label);
    const key = normalizeString(option && option.key);
    const family = resolvePanFamilyLabelForUi(`${label} ${key}`, runtimeSettings);
    const next = family || '未知';
    if (next === '未知') {
      hasUnknown = true;
      return;
    }
    const id = next.toLowerCase();
    if (seen.has(id)) return;
    seen.add(id);
    out.push({ value: next, label: next });
  });
  if (hasUnknown) out.push({ value: '未知', label: '未知' });
  return out;
};

export const normalizeQualityModeKeyForUi = (value) => {
  const raw = normalizeString(value).toLowerCase();
  if (!raw) return '';
  if (raw === '4k·hdr' || raw === '4k_hdr') return '4k_hdr';
  if (raw === '4k·60帧' || raw === '4k_fps') return '4k_fps';
  if (raw === '4k') return '4k';
  if (raw === '1080p') return '1080p';
  if (raw === '720p') return '720p';
  return '';
};

export const formatQualityLabelForUi = (value) => {
  const key = normalizeQualityModeKeyForUi(value);
  const hit = DEFAULT_PLAYER_QUALITY_OPTIONS.find((item) => item.value === key) || null;
  if (hit) return hit.label;
  return '未知';
};

export const inferQualityFromResolution = ({ width = 0, height = 0 } = {}) => {
  const w = Math.max(0, normalizeInt(width));
  const h = Math.max(0, normalizeInt(height));
  const maxEdge = Math.max(w, h);
  if (maxEdge >= 3000) return '4K';
  if (maxEdge >= 1800) return '1080P';
  if (maxEdge >= 1200) return '720P';
  return '';
};

export const doesQualityMatchResolution = (qualityValue, resolutionQuality) => {
  const currentKey = normalizeQualityModeKeyForUi(qualityValue);
  const inferredKey = normalizeQualityModeKeyForUi(resolutionQuality);
  if (!currentKey || !inferredKey) return false;
  if (currentKey === inferredKey) return true;
  if (inferredKey === '4k') return currentKey === '4k_hdr' || currentKey === '4k_fps' || currentKey === '4k';
  return false;
};

export const buildPlayerControlUiState = ({
  isPrimaryTmdbMode = false,
  currentContext,
  runtimeSettings,
  currentPanSourceOptions,
} = {}) => {
  const playback = currentContext && typeof currentContext === 'object' ? currentContext : {};
  const currentPanFamilyLabel =
    resolvePanFamilyLabelForUi(
      `${normalizeString(playback.panFlag)} ${normalizeString(playback.panKey)}`,
      runtimeSettings,
    ) || '未知';
  const effectiveQualityValue = normalizeString(playback.quality) || normalizeString(playback.sourceQuality);
  const currentQualityKey = normalizeQualityModeKeyForUi(effectiveQualityValue);
  const currentQualityLabel = formatQualityLabelForUi(effectiveQualityValue);

  const playerPanMenuOptions = (() => {
    if (isPrimaryTmdbMode) {
      const mappings = normalizePanAliasMappingsForUi(runtimeSettings && runtimeSettings.smartPanAliasMappings);
      const mapped = mappings.map((item) => ({ value: item.pan, label: item.pan }));
      if (mapped.length) return mapped;
      const tokens = Array.isArray(runtimeSettings && runtimeSettings.smartPanMatchTokens)
        ? runtimeSettings.smartPanMatchTokens.map(normalizeString).filter(Boolean)
        : [];
      return tokens.map((item) => ({ value: item, label: item }));
    }
    const grouped = groupPanSourceOptionsForUi(currentPanSourceOptions, runtimeSettings);
    return grouped.length ? grouped : [{ value: '未知', label: '未知' }];
  })();

  const playerQualityMenuOptions = isPrimaryTmdbMode
    ? DEFAULT_PLAYER_QUALITY_OPTIONS.slice()
    : currentQualityKey
      ? [{ value: currentQualityKey, label: currentQualityLabel }]
      : [];

  const panValue = playerPanMenuOptions.some((item) => normalizeString(item && item.value) === currentPanFamilyLabel)
    ? currentPanFamilyLabel
    : '';
  const qualityValue = playerQualityMenuOptions.some((item) => normalizeString(item && item.value) === currentQualityKey)
    ? currentQualityKey
    : '';

  return {
    currentPanFamilyLabel,
    currentQualityKey,
    currentQualityLabel,
    playerExtraMenus: [
      {
        key: 'pan',
        label: currentPanFamilyLabel || '未知',
        ariaLabel: '网盘',
        value: panValue,
        disabled: !playerPanMenuOptions.length,
        options: playerPanMenuOptions,
      },
      {
        key: 'quality',
        label: currentQualityLabel || '未知',
        ariaLabel: '画质',
        value: qualityValue,
        disabled: !playerQualityMenuOptions.length,
        options: playerQualityMenuOptions,
      },
    ],
    playerExtraActions:
      isPrimaryTmdbMode && Math.max(0, normalizeInt(playback.globalEpisode)) > 0
        ? [
            { key: 'switch', label: '换源', ariaLabel: '换源' },
            { key: 'wrong-source', label: '片源错误', ariaLabel: '片源错误' },
          ]
        : [],
  };
};

export const resolveCandidatePanFamilyForPlayback = (candidate, runtimeSettings) => {
  const target = candidate && typeof candidate === 'object' ? candidate : {};
  return resolvePanFamilyLabelForUi(
    [
      normalizeString(target.panToken),
      normalizeString(target.panFlag),
      normalizeString(target.provider),
      normalizeString(target.displayName),
    ].join(' '),
    runtimeSettings,
  ) || '';
};

export const resolveCandidateQualityModeKeyForPlayback = (candidate) => {
  const target = candidate && typeof candidate === 'object' ? candidate : {};
  const quality = normalizeString(target.quality).toUpperCase();
  const displayName = normalizeString(target.displayName);
  const hay = [
    displayName,
    normalizeString(target.rawName),
    normalizeString(target.fileName),
  ].join(' ');
  const displayMeta = (() => {
    const out = { quality: '', fps60: false };
    let rest = displayName.trim();
    while (rest.startsWith('@')) {
      const match = rest.match(/^@([^@/\\]+)/);
      if (!match || !match[1]) break;
      const token = normalizeString(match[1]).toUpperCase();
      if (token === '8K') out.quality = '8K';
      else if (token === '4K' || token === '2160P') out.quality = '4K';
      else if (token === '1080P') out.quality = '1080P';
      else if (token === '720P') out.quality = '720P';
      else if (token === '60FPS' || token === '120FPS' || token === '60帧' || token === '120帧') out.fps60 = true;
      rest = rest.slice(match[0].length).trim();
    }
    return out;
  })();
  const effectiveQuality = quality || displayMeta.quality;
  if (effectiveQuality === '4K') {
    if (/\bhdr\b/i.test(hay)) return '4k_hdr';
    if (displayMeta.fps60 || /(?:60fps|60帧|120fps|120帧|2160p60|4k60|\b60p\b|\b120p\b)/i.test(hay)) return '4k_fps';
    return '4k';
  }
  if (effectiveQuality === '1080P') return '1080p';
  if (effectiveQuality === '720P') return '720p';
  return '';
};

const SMART_PLAYBACK_RULE_KEYS = ['quality', 'pan', 'keyword'];

export const resolveOrderedSmartSourceRuleKeys = (runtimeSettings) => {
  const rows = Array.isArray(runtimeSettings && runtimeSettings.smartSourceRuleRows)
    ? runtimeSettings.smartSourceRuleRows
    : [];
  const seen = new Set();
  const normalized = rows
    .map((row, index) => {
      const key = normalizeString(row && row.key).toLowerCase();
      if (!SMART_PLAYBACK_RULE_KEYS.includes(key)) return null;
      return {
        key,
        order: normalizeInt(row && row.order) || (index + 1),
      };
    })
    .filter(Boolean)
    .sort((left, right) => left.order - right.order)
    .filter((row) => {
      if (seen.has(row.key)) return false;
      seen.add(row.key);
      return true;
    })
    .map((row) => row.key);
  SMART_PLAYBACK_RULE_KEYS.forEach((key) => {
    if (!seen.has(key)) normalized.push(key);
  });
  return normalized;
};

const buildCurrentContextPanFamily = (currentContext, runtimeSettings) => (
  resolvePanFamilyLabelForUi(
    `${normalizeString(currentContext && currentContext.panFlag)} ${normalizeString(currentContext && currentContext.panKey)}`,
    runtimeSettings,
  )
);

const buildCurrentContextQualityKey = (currentContext) => (
  normalizeQualityModeKeyForUi(
    normalizeString(currentContext && currentContext.quality) || normalizeString(currentContext && currentContext.sourceQuality),
  )
);

export const buildSmartPlaybackActionConstraint = ({
  actionKey,
  selectedValue = '',
  currentContext,
} = {}) => {
  const mode = normalizeString(actionKey).toLowerCase();
  const playback = currentContext && typeof currentContext === 'object' ? currentContext : {};
  if (mode === 'pan') {
    const panFamily = normalizeString(selectedValue);
    if (!panFamily) return null;
    return {
      mode: 'pan',
      fixedPanFamily: panFamily,
      fixedQualityKey: '',
      excludeCurrentSource: false,
      secondaryPreference: 'quality',
      currentPanFamily: buildCurrentContextPanFamily(playback, {}),
      currentQualityKey: buildCurrentContextQualityKey(playback),
    };
  }
  if (mode === 'quality') {
    const qualityKey = normalizeQualityModeKeyForUi(selectedValue);
    if (!qualityKey) return null;
    return {
      mode: 'quality',
      fixedPanFamily: '',
      fixedQualityKey: qualityKey,
      excludeCurrentSource: false,
      secondaryPreference: 'pan',
      currentPanFamily: buildCurrentContextPanFamily(playback, {}),
      currentQualityKey: buildCurrentContextQualityKey(playback),
    };
  }
  if (mode === 'switch') {
    return {
      mode: 'switch',
      fixedPanFamily: '',
      fixedQualityKey: '',
      excludeCurrentSource: true,
      secondaryPreference: '',
      currentPanFamily: buildCurrentContextPanFamily(playback, {}),
      currentQualityKey: buildCurrentContextQualityKey(playback),
    };
  }
  return {
    mode: 'default',
    fixedPanFamily: '',
    fixedQualityKey: '',
    excludeCurrentSource: false,
    secondaryPreference: '',
    currentPanFamily: buildCurrentContextPanFamily(playback, {}),
    currentQualityKey: buildCurrentContextQualityKey(playback),
  };
};

const hasLooseQualityMarker = (hay) => /(?:2160p|1080|720|8k|4k)/i.test(hay);

export const scoreCandidateQualityForPlayback = (candidate) => {
  const target = candidate && typeof candidate === 'object' ? candidate : {};
  const qualityKey = resolveCandidateQualityModeKeyForPlayback(target);
  const hay = [
    normalizeString(target.displayName),
    normalizeString(target.rawName),
    normalizeString(target.fileName),
    normalizeString(target.quality),
  ].join(' ');
  let base = 0;
  if (/\b8k\b/i.test(hay)) base = 50;
  else if (qualityKey === '4k_hdr' || qualityKey === '4k_fps' || qualityKey === '4k' || /(?:2160p|2160|4k|uhd)/i.test(hay)) base = 40;
  else if (qualityKey === '1080p') base = 20;
  else if (qualityKey === '720p') base = 10;
  else if (hasLooseQualityMarker(hay) || /(?:bdrip|bluray|remux|web[- ]?dl|webrip|hdr|dv|dolby|原盘|蓝光|高码|超清|高清|臻彩|杜比|ddp|dd\\+|e-?ac-?3|eac3)/i.test(hay)) base = 30;
  if (!base) return 0;
  const hasHdr = /\bhdr\b|hdr10|dolby\s*vision|\bdv\b|臻彩|杜比视界/i.test(hay);
  const hasDdp = /\bddp\b|ddp\d(?:\.\d)?|dd\+|e-?ac-?3|eac3/i.test(hay);
  let bonus = 0;
  if (hasHdr && hasDdp) bonus = 6;
  else if (hasHdr) bonus = 4;
  else if (hasDdp) bonus = 2;
  let score = base + bonus;
  if (base === 40 && score >= 50) score = 49;
  if (base === 30 && score >= 40) score = 39;
  if (base === 20 && score >= 30) score = 29;
  if (base === 10 && score >= 20) score = 19;
  return score;
};

const normalizePanMatchEntries = (runtimeSettings) => {
  const aliasMappings = normalizePanAliasMappingsForUi(runtimeSettings && runtimeSettings.smartPanAliasMappings);
  if (aliasMappings.length) return aliasMappings;
  const tokens = Array.isArray(runtimeSettings && runtimeSettings.smartPanMatchTokens)
    ? runtimeSettings.smartPanMatchTokens.map(normalizeString).filter(Boolean)
    : [];
  return tokens.map((token) => ({ pan: token, aliases: [] }));
};

export const scoreCandidatePanForPlayback = (candidate, runtimeSettings) => {
  const family = resolveCandidatePanFamilyForPlayback(candidate, runtimeSettings);
  if (!family) return 0;
  const entries = normalizePanMatchEntries(runtimeSettings);
  const index = entries.findIndex((entry) => normalizeString(entry && entry.pan) === family);
  if (index < 0) return 0;
  return (entries.length - index) * 10;
};

export const scoreCandidateKeywordForPlayback = (candidate, runtimeSettings) => {
  const tokens = Array.isArray(runtimeSettings && runtimeSettings.smartSourcePriorityTokens)
    ? runtimeSettings.smartSourcePriorityTokens.map(normalizeString).filter(Boolean)
    : [];
  if (!tokens.length) return 0;
  const target = candidate && typeof candidate === 'object' ? candidate : {};
  const hay = [
    normalizeString(target.displayName),
    normalizeString(target.rawName),
    normalizeString(target.fileName),
  ].join(' ').toLowerCase();
  let best = -1;
  tokens.forEach((token, index) => {
    const normalized = token.toLowerCase();
    if (!normalized) return;
    if (!hay.includes(normalized)) return;
    if (best === -1 || index < best) best = index;
  });
  if (best < 0) return 0;
  return (tokens.length - best) * 10;
};

const compareNumbersDesc = (left, right) => {
  if (left === right) return 0;
  return left > right ? -1 : 1;
};

export const isPlaybackCandidateAllowedByAction = (wrapper, actionConstraint, runtimeSettings, currentContext = null) => {
  const target = wrapper && typeof wrapper === 'object' ? wrapper : null;
  const constraint = actionConstraint && typeof actionConstraint === 'object' ? actionConstraint : null;
  const current = currentContext && typeof currentContext === 'object' ? currentContext : null;
  if (!target || !constraint) return true;
  const candidate = target && target.candidate && typeof target.candidate === 'object' ? target.candidate : null;
  if (!candidate) return false;
  if (constraint.mode === 'quality') {
    return resolveCandidateQualityModeKeyForPlayback(candidate) === normalizeQualityModeKeyForUi(constraint.fixedQualityKey);
  }
  if (constraint.mode === 'pan') {
    return resolveCandidatePanFamilyForPlayback(candidate, runtimeSettings) === normalizeString(constraint.fixedPanFamily);
  }
  if (constraint.mode === 'switch' && constraint.excludeCurrentSource && current) {
    const siteKey = normalizeString(candidate && candidate.source && candidate.source.siteKey)
      || normalizeString(target && target.siteItem && target.siteItem.siteKey)
      || normalizeString(target && target.siteKey);
    const siteDetail = normalizeString(candidate && candidate.source && candidate.source.siteDetail)
      || normalizeString(target && target.siteItem && target.siteItem.siteDetail)
      || normalizeString(target && target.siteDetail);
    if (!siteKey || !siteDetail) return true;
    const currentSiteKey = normalizeString(current.siteKey);
    const currentSiteDetail = normalizeString(current.siteDetail);
    if (siteKey !== currentSiteKey || siteDetail !== currentSiteDetail) return true;
    const panKey = normalizeString(target && target.panKey);
    const itemIndex = normalizeInt(target && target.itemIndex);
    const currentPanKey = normalizeString(current.panKey);
    const currentItemIndex = normalizeInt(current.itemIndex);
    if (panKey && currentPanKey && itemIndex >= 0 && currentItemIndex >= 0) {
      if (panKey === currentPanKey && itemIndex === currentItemIndex) return false;
    }
    const segmentIdentity = normalizeString(candidate && candidate.segmentIdentity)
      || normalizeString(target && target.fileIdentity);
    const currentFileIdentity = normalizeString(current.fileIdentity);
    if (!segmentIdentity || !currentFileIdentity || segmentIdentity !== currentFileIdentity) return true;
    const panFlag = normalizeString(candidate && candidate.panFlag)
      || normalizeString(candidate && candidate.source && candidate.source.panFlag)
      || normalizeString(target && target.panFlag);
    const currentPanFlag = normalizeString(current.panFlag);
    if (panFlag && currentPanFlag) return panFlag !== currentPanFlag;
    return false;
  }
  return true;
};

export const comparePlaybackCandidatesByDefaultRules = (left, right, runtimeSettings) => {
  const l = left && left.candidate ? left.candidate : null;
  const r = right && right.candidate ? right.candidate : null;
  if (!l || !r) return 0;
  const ordered = resolveOrderedSmartSourceRuleKeys(runtimeSettings);
  for (let i = 0; i < ordered.length; i += 1) {
    const rule = ordered[i];
    const next = rule === 'quality'
      ? compareNumbersDesc(scoreCandidateQualityForPlayback(l), scoreCandidateQualityForPlayback(r))
      : rule === 'pan'
        ? compareNumbersDesc(scoreCandidatePanForPlayback(l, runtimeSettings), scoreCandidatePanForPlayback(r, runtimeSettings))
        : compareNumbersDesc(scoreCandidateKeywordForPlayback(l, runtimeSettings), scoreCandidateKeywordForPlayback(r, runtimeSettings));
    if (next !== 0) return next;
  }
  const leftLoose = !!(left && left.looseMatch);
  const rightLoose = !!(right && right.looseMatch);
  if (leftLoose !== rightLoose) return leftLoose ? 1 : -1;
  return 0;
};

export const comparePlaybackCandidatesForAction = (left, right, actionConstraint, currentContext, runtimeSettings) => {
  const l = left && left.candidate ? left.candidate : null;
  const r = right && right.candidate ? right.candidate : null;
  const constraint = actionConstraint && typeof actionConstraint === 'object' ? actionConstraint : null;
  if (!l || !r || !constraint) return 0;
  const currentPanFamily = buildCurrentContextPanFamily(currentContext, runtimeSettings);
  const currentQualityKey = buildCurrentContextQualityKey(currentContext);
  if (constraint.mode === 'quality') {
    const leftPanCurrent = resolveCandidatePanFamilyForPlayback(l, runtimeSettings) === currentPanFamily;
    const rightPanCurrent = resolveCandidatePanFamilyForPlayback(r, runtimeSettings) === currentPanFamily;
    if (leftPanCurrent !== rightPanCurrent) return leftPanCurrent ? -1 : 1;
    return compareNumbersDesc(scoreCandidatePanForPlayback(l, runtimeSettings), scoreCandidatePanForPlayback(r, runtimeSettings));
  }
  if (constraint.mode === 'pan') {
    const leftQualityCurrent = resolveCandidateQualityModeKeyForPlayback(l) === currentQualityKey;
    const rightQualityCurrent = resolveCandidateQualityModeKeyForPlayback(r) === currentQualityKey;
    if (leftQualityCurrent !== rightQualityCurrent) return leftQualityCurrent ? -1 : 1;
    return compareNumbersDesc(scoreCandidateQualityForPlayback(l), scoreCandidateQualityForPlayback(r));
  }
  return comparePlaybackCandidatesByDefaultRules(left, right, runtimeSettings);
};

export const normalizePlayPayload = (data) => {
  if (!data) return null;
  if (typeof data === 'string') {
    const text = data.trim();
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch (_error) {
      return null;
    }
  }
  return typeof data === 'object' ? data : null;
};

export const isHttpPlayableUrl = (value) => /^https?:\/\//i.test(normalizeString(value));

export const pickFirstPlayableUrl = (payload) => {
  const direct = normalizeString(payload && payload.url);
  if (isHttpPlayableUrl(direct)) return direct;
  const arr = Array.isArray(payload && payload.url) ? payload.url : [];
  if (arr.length >= 2) {
    const left = normalizeString(arr[0]);
    const right = normalizeString(arr[1]);
    if (!isHttpPlayableUrl(left) && isHttpPlayableUrl(right)) return right;
  }
  for (let i = 0; i < arr.length; i += 1) {
    const current = normalizeString(arr[i]);
    if (isHttpPlayableUrl(current)) return current;
  }
  return '';
};

export const pickRelayResolveUrl = (payload) => {
  const direct = normalizeString(payload && payload.token);
  return isHttpPlayableUrl(direct) ? direct : '';
};

export const parseLabeledPlayUrlEntries = (payload) => {
  const arr = Array.isArray(payload && payload.url) ? payload.url : [];
  const out = [];
  for (let i = 0; i + 1 < arr.length; i += 2) {
    const label = normalizeString(arr[i]);
    const url = normalizeString(arr[i + 1]);
    if (!label || !url) continue;
    if (isHttpPlayableUrl(label) || !isHttpPlayableUrl(url)) continue;
    out.push({ label, url });
  }
  return out;
};

export const hasNonEmptyHeaders = (headers) => {
  const target = headers && typeof headers === 'object' ? headers : {};
  return Object.keys(target).some((key) => normalizeString(target[key]));
};

export const resolvePlayTargetForPlayback = ({ payload, rawHeaders }) => {
  const headers = rawHeaders && typeof rawHeaders === 'object' ? rawHeaders : {};
  const fallbackUrl = pickFirstPlayableUrl(payload);
  const relayResolveUrl = pickRelayResolveUrl(payload);
  if (!fallbackUrl) return { url: '', headers, proxySourceUrl: '', relayResolveUrl };
  if (!hasNonEmptyHeaders(headers)) return { url: fallbackUrl, headers, proxySourceUrl: '', relayResolveUrl };
  let sourceUrl = '';
  const direct = normalizeString(payload && payload.url);
  if (isHttpPlayableUrl(direct)) sourceUrl = direct;
  const labeled = parseLabeledPlayUrlEntries(payload);
  if (labeled.length) {
    const byLabel = (name) =>
      labeled.find((item) => normalizeString(item.label).toLowerCase() === String(name).trim().toLowerCase()) || null;
    const proxyRaw = byLabel('代理raw');
    const raw = byLabel('raw');
    if (proxyRaw && raw && isHttpPlayableUrl(raw.url)) {
      sourceUrl = raw.url;
    } else if (labeled.length === 1 && isHttpPlayableUrl(labeled[0].url)) {
      sourceUrl = labeled[0].url;
    }
  }
  if (!sourceUrl) sourceUrl = fallbackUrl;
  return { url: sourceUrl, headers, proxySourceUrl: sourceUrl, relayResolveUrl };
};

const rewriteProxyUrlToBase = (urlString, apiBase, tvUser) => {
  const raw = normalizeString(urlString);
  const base = normalizecatpawrunnerApiBase(apiBase);
  if (!raw || !base) return raw;
  try {
    const target = new URL(raw);
    const host = normalizeString(target.hostname).toLowerCase();
    const loopback = new Set(['127.0.0.1', '0.0.0.0', 'localhost']);
    const baseUrl = new URL(base);
    const sameHost = host && normalizeString(baseUrl.hostname).toLowerCase() === host;
    const needsRewrite = loopback.has(host) || (sameHost && target.port === '3006' && baseUrl.port !== '3006');
    if (!needsRewrite) return raw;
    const next = new URL(String(target.pathname || '/').replace(/^\//, ''), base);
    next.search = target.search || '';
    next.hash = target.hash || '';
    const safeUser = normalizeString(tvUser);
    if (safeUser && !next.searchParams.has('__tvuser')) next.searchParams.set('__tvuser', safeUser);
    return next.toString();
  } catch (_error) {
    return raw;
  }
};

export const rewritePlayPayloadUrls = (payload, apiBase, tvUser) => {
  if (!payload || typeof payload !== 'object') return payload;
  if (typeof payload.url === 'string') {
    return { ...payload, url: rewriteProxyUrlToBase(payload.url, apiBase, tvUser) };
  }
  if (!Array.isArray(payload.url)) return payload;
  return {
    ...payload,
    url: payload.url.map((item) => (typeof item === 'string' ? rewriteProxyUrlToBase(item, apiBase, tvUser) : item)),
  };
};

export const buildLocalProxyPlaybackUrl = ({ apiBase, sourceUrl, headers }) => {
  const base = normalizecatpawrunnerApiBase(apiBase);
  const target = normalizeString(sourceUrl);
  if (!base || !isHttpPlayableUrl(target)) return '';
  try {
    const url = new URL('proxy', base);
    url.searchParams.set('url', target);
    if (headers && typeof headers === 'object' && Object.keys(headers).length) {
      url.searchParams.set('header', JSON.stringify(headers));
    }
    return url.toString();
  } catch (_error) {
    return '';
  }
};

const joinBaseUrl = (base, relativePath) => {
  const normalizedBase = normalizeHttpBase(base);
  const rel = normalizeString(relativePath);
  if (!normalizedBase || !rel) return '';
  const baseWithSlash = normalizedBase.endsWith('/') ? normalizedBase : `${normalizedBase}/`;
  try {
    return new URL(rel.startsWith('./') ? rel : `./${rel.replace(/^\//, '')}`, baseWithSlash).toString();
  } catch (_error) {
    return '';
  }
};

export const normalizeGoProxyServers = (value) => {
  const list = Array.isArray(value) ? value : [];
  const out = [];
  const seen = new Set();
  list.forEach((item) => {
    const base = normalizeHttpBase(
      typeof item === 'string'
        ? item
        : ((item && (item.base || item.apiBase || item.api || item.url)) || '')
    );
    if (!base || seen.has(base)) return;
    const pans = item && typeof item === 'object' && item.pans && typeof item.pans === 'object' ? item.pans : {};
    const hasBaidu = Object.prototype.hasOwnProperty.call(pans, 'baidu');
    const hasQuark = Object.prototype.hasOwnProperty.call(pans, 'quark');
    let label = '';
    if (item && typeof item === 'object') {
      label = normalizeString(item.displayName || item.name);
    }
    if (!label) {
      try {
        label = new URL(base).host || base;
      } catch (_error) {
        label = base;
      }
    }
    out.push({
      base,
      label,
      pans: {
        baidu: hasBaidu ? !!pans.baidu : true,
        quark: hasQuark ? !!pans.quark : true,
      },
    });
    seen.add(base);
  });
  return out;
};

export const normalizeRelayServers = (value) => {
  const list = Array.isArray(value) ? value : [];
  const out = [];
  const seen = new Set();
  list.forEach((item) => {
    const base = normalizeHttpBase(
      typeof item === 'string'
        ? item
        : ((item && (item.base || item.apiBase || item.api || item.url)) || '')
    );
    if (!base || seen.has(base)) return;
    let label = '';
    let secret = '';
    if (item && typeof item === 'object') {
      label = normalizeString(item.displayName || item.name);
      secret = normalizeString(item.secret);
    }
    if (!label) {
      try {
        label = new URL(base).host || base;
      } catch (_error) {
        label = base;
      }
    }
    out.push({
      base,
      label,
      secret,
    });
    seen.add(base);
  });
  return out;
};

const buildRelayPlaybackUrl = ({ base, resolveUrl, secret }) => {
  const relayBase = normalizeHttpBase(base);
  const targetResolveUrl = normalizeString(resolveUrl);
  const accessSecret = normalizeString(secret);
  if (!relayBase || !isHttpPlayableUrl(targetResolveUrl) || !accessSecret) return '';
  const separator = targetResolveUrl.includes('?') ? '&' : '?';
  return `${relayBase}/${targetResolveUrl}${separator}secret=${encodeURIComponent(accessSecret)}`;
};

const pickEligibleRelayServers = ({ relayServers }) => {
  const servers = normalizeRelayServers(relayServers);
  return servers.filter((item) => item && item.base && item.secret);
};

export const maybeUseRelayForPlayback = async ({
  relayEnabled,
  relayServers,
  resolveUrl,
}) => {
  const targetResolveUrl = normalizeString(resolveUrl);
  if (!relayEnabled || !targetResolveUrl) {
    return { url: '', relayBase: '' };
  }
  const eligible = pickEligibleRelayServers({ relayServers });
  if (!eligible.length) return { url: '', relayBase: '' };
  const picked = eligible.find((item) => item && item.base && item.secret) || null;
  if (!picked) return { url: '', relayBase: '' };
  return {
    url: buildRelayPlaybackUrl({
      base: picked.base,
      resolveUrl: targetResolveUrl,
      secret: picked.secret,
    }),
    relayBase: picked.base,
  };
};

export const buildRelayM3U8PlaybackUrl = ({ base, playUrl, secret }) => {
  const relayBase = normalizeHttpBase(base);
  const targetUrl = normalizeString(playUrl);
  const accessSecret = normalizeString(secret);
  if (!relayBase || !isHttpPlayableUrl(targetUrl) || !accessSecret) return '';
  try {
    const baseWithSlash = relayBase.endsWith('/') ? relayBase : `${relayBase}/`;
    const next = new URL(`m3u8/${encodeURIComponent(targetUrl)}`, baseWithSlash);
    next.searchParams.set('secret', accessSecret);
    next.searchParams.set('__tv_fmt', 'm3u8');
    return next.toString();
  } catch (_error) {
    return '';
  }
};

export const maybeUseRelayM3U8ForPlayback = ({ playUrl, runtimeSettings } = {}) => {
  const targetUrl = normalizeString(playUrl);
  const settings = runtimeSettings && typeof runtimeSettings === 'object' ? runtimeSettings : null;
  if (!targetUrl || !isProbablyM3U8Url(targetUrl) || !settings || !settings.relayEnabled) {
    return { url: '', headers: {}, relayBase: '' };
  }
  const picked = pickEligibleRelayServers({ relayServers: settings.relayServers })[0] || null;
  if (!picked || !picked.base || !picked.secret) return { url: '', headers: {}, relayBase: '' };
  const url = buildRelayM3U8PlaybackUrl({
    base: picked.base,
    playUrl: targetUrl,
    secret: picked.secret,
  });
  if (!url) return { url: '', headers: {}, relayBase: '' };
  return {
    url,
    headers: {},
    relayBase: picked.base,
  };
};

export const registerGoProxyToken = async ({ base, url, headers }) => {
  const normalizedBase = normalizeHttpBase(base);
  const targetUrl = normalizeString(url);
  if (!normalizedBase) throw new Error('缺少 GoProxy 地址');
  if (!targetUrl) throw new Error('缺少播放地址');
  const headerList = [];
  const rawHeaders = headers && typeof headers === 'object' ? headers : {};
  Object.keys(rawHeaders).forEach((key) => {
    const normalizedKey = normalizeString(key);
    if (!normalizedKey) return;
    const value = rawHeaders[key];
    if (value == null) return;
    if (Array.isArray(value)) {
      value.forEach((item) => {
        const text = normalizeString(item);
        if (!text) return;
        headerList.push({ key: normalizedKey, value: text });
      });
      return;
    }
    const text = normalizeString(value);
    if (!text) return;
    headerList.push({ key: normalizedKey, value: text });
  });
  const registerUrl = joinBaseUrl(normalizedBase, 'register');
  if (!registerUrl) throw new Error('GoProxy 注册地址无效');
  const resp = await fetch(registerUrl, {
    method: 'POST',
    mode: 'cors',
    credentials: 'omit',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url: targetUrl, headersList: headerList }),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    throw new Error(data && (data.message || data.error) ? String(data.message || data.error) : `HTTP ${resp.status}`);
  }
  const token = normalizeString(data && data.token);
  if (!token) throw new Error('GoProxy 返回 token 无效');
  const proxyUrl = joinBaseUrl(normalizedBase, encodeURIComponent(token));
  if (!proxyUrl) throw new Error('GoProxy 代理地址无效');
  return { token, proxyUrl };
};

export const maybeUseGoProxyForPlayback = async ({
  playUrl,
  playHeaders,
  goProxyEnabled,
  goProxyServers,
  preferredPan,
  selectedBase,
}) => {
  const finalUrl = normalizeString(playUrl);
  const finalHeaders = playHeaders && typeof playHeaders === 'object' ? playHeaders : {};
  if (!goProxyEnabled || !finalUrl || !hasNonEmptyHeaders(finalHeaders)) {
    return { url: finalUrl, headers: finalHeaders, goProxyBase: '' };
  }
  const servers = normalizeGoProxyServers(goProxyServers);
  if (!servers.length) return { url: finalUrl, headers: finalHeaders, goProxyBase: '' };
  const pan = normalizeString(preferredPan).toLowerCase();
  const eligible = (pan === 'baidu' || pan === 'quark')
    ? servers.filter((item) => item && item.pans && item.pans[pan])
    : servers;
  if (!eligible.length) return { url: finalUrl, headers: finalHeaders, goProxyBase: '' };
  const manual = normalizeHttpBase(selectedBase);
  const picked = (manual && eligible.find((item) => item.base === manual)) || eligible[0];
  if (!picked || !picked.base) return { url: finalUrl, headers: finalHeaders, goProxyBase: '' };
  const { proxyUrl } = await registerGoProxyToken({
    base: picked.base,
    url: finalUrl,
    headers: finalHeaders,
  });
  const decorated = (() => {
    if (!isProbablyM3U8Url(finalUrl)) return proxyUrl;
    try {
      const next = new URL(proxyUrl);
      next.searchParams.set('__tv_fmt', 'm3u8');
      return next.toString();
    } catch (_error) {
      const sep = proxyUrl.includes('?') ? '&' : '?';
      return `${proxyUrl}${sep}__tv_fmt=m3u8`;
    }
  })();
  return {
    url: decorated,
    headers: {},
    goProxyBase: picked.base,
  };
};

export const registerCatProxyToken = async ({ apiBase, tvUser, url, headers }) => {
  const base = normalizecatpawrunnerApiBase(apiBase);
  if (!base) throw new Error('catpawrunner 接口地址未设置');
  const target = new URL('api/proxy/register', base);
  const safeUser = normalizeString(tvUser);
  const resp = await fetch(target.toString(), {
    method: 'POST',
    mode: 'cors',
    credentials: 'omit',
    headers: {
      'Content-Type': 'application/json',
      ...(safeUser ? { 'X-TV-User': safeUser } : {}),
    },
    body: JSON.stringify({
      url: normalizeString(url),
      headers: headers && typeof headers === 'object' ? headers : {},
    }),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok || !data || data.ok === false) {
    throw new Error(data && (data.message || data.error) ? String(data.message || data.error) : `HTTP ${resp.status}`);
  }
  const proxyPath = normalizeString(data && data.proxy);
  if (!proxyPath) throw new Error('catpawrunner proxy register 返回无效');
  return {
    proxyUrl: new URL(proxyPath.replace(/^\//, ''), base).toString(),
  };
};

export const resolveBuiltinPanPlayBody = ({
  provider,
  flag,
  id,
  rawName,
  panMock189AccessByShareId,
} = {}) => {
  const body = {
    flag: normalizeString(flag),
    id: normalizeString(id),
  };
  if (normalizeString(provider) !== '189') return body;
  const idRaw = normalizeString(id);
  const parts = idRaw.split('*');
  const shareId = parts.length >= 2 ? normalizeString(parts[1]) : '';
  const accessMap = panMock189AccessByShareId && typeof panMock189AccessByShareId === 'object'
    ? panMock189AccessByShareId
    : {};
  const mappedAccessCode =
    shareId && typeof accessMap[shareId] === 'string' ? normalizeString(accessMap[shareId]) : '';
  const extracted = extractTianyiShareCodeAndAccessCode(normalizeString(flag), normalizeString(rawName));
  return {
    id: idRaw,
    accessCode: mappedAccessCode || normalizeString(extracted && extracted.accessCode),
  };
};

export const requestBuiltinPanPlay = async ({
  provider,
  flag,
  id,
  rawName,
  tvUser,
  panMock189AccessByShareId,
} = {}) => {
  const routes = {
    quark: '/api/pan/quark/play',
    uc: '/api/pan/uc/play',
    baidu: '/api/pan/baidu/play',
    '139': '/api/pan/139/play',
    '189': '/api/pan/189/play',
  };
  const path = routes[normalizeString(provider)] || '';
  if (!path) throw new Error('不支持的网盘源');
  const body = resolveBuiltinPanPlayBody({
    provider,
    flag,
    id,
    rawName,
    panMock189AccessByShareId,
  });
  const username = normalizeString(tvUser);
  const headers = { 'Content-Type': 'application/json' };
  if (username) headers['X-TV-User'] = username;
  const resp = await fetch(path, {
    method: 'POST',
    credentials: 'include',
    headers,
    body: JSON.stringify(body),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok || !data || data.ok === false) {
    throw new Error(data && data.message ? String(data.message) : `HTTP ${resp.status}`);
  }
  return data;
};

export const applyPlaybackProxyChain = async ({
  apiBase,
  tvUser,
  sourceUrl,
  playUrl,
  playHeaders,
  relayResolveUrl,
  preferredPan,
  runtimeSettings,
  selectedGoProxyBase,
} = {}) => {
  let finalUrl = normalizeString(playUrl);
  let finalHeaders = playHeaders && typeof playHeaders === 'object' ? playHeaders : {};
  let goProxyBase = '';
  let relayBase = '';
  if (!finalUrl) return { url: '', headers: {}, goProxyBase: '', relayBase: '' };
  const settings = runtimeSettings && typeof runtimeSettings === 'object' ? runtimeSettings : null;
  const relayEnabled = !!(settings && settings.relayEnabled);
  const relayServers = settings && settings.relayServers;
  const goProxyEnabled = !!(settings && settings.goProxyEnabled);
  const hasRelayResolve = isHttpPlayableUrl(relayResolveUrl);
  const relayEligible = relayEnabled && hasRelayResolve && pickEligibleRelayServers({ relayServers }).some((item) => item && item.base && item.secret);
  const goProxyEligible = hasNonEmptyHeaders(finalHeaders) && goProxyEnabled && normalizeGoProxyServers(settings && settings.goProxyServers).length > 0;

  if (relayEligible && !goProxyEligible) {
    const out = await maybeUseRelayForPlayback({
      relayEnabled,
      relayServers,
      resolveUrl: relayResolveUrl,
    });
    if (out && normalizeString(out.url)) {
      finalUrl = normalizeString(out.url);
      finalHeaders = {};
      relayBase = normalizeString(out.relayBase);
    }
  } else if (!relayEligible && goProxyEligible) {
    try {
      const out = await maybeUseGoProxyForPlayback({
        playUrl: finalUrl,
        playHeaders: finalHeaders,
        goProxyEnabled: goProxyEnabled,
        goProxyServers: settings.goProxyServers,
        preferredPan,
        selectedBase: selectedGoProxyBase,
      });
      if (out && normalizeString(out.url)) {
        finalUrl = normalizeString(out.url);
        finalHeaders = out.headers && typeof out.headers === 'object' ? out.headers : {};
        goProxyBase = normalizeString(out.goProxyBase);
      }
    } catch (_error) {}
  } else if (relayEligible && goProxyEligible) {
    const out = await maybeUseRelayForPlayback({
      relayEnabled,
      relayServers,
      resolveUrl: relayResolveUrl,
    });
    if (out && normalizeString(out.url)) {
      finalUrl = normalizeString(out.url);
      finalHeaders = {};
      relayBase = normalizeString(out.relayBase);
    }
  }
  if (hasNonEmptyHeaders(finalHeaders) && isProbablyM3U8Url(finalUrl)) {
    try {
      const out = await maybeUseCatM3U8ProxyForPlayback({
        apiBase,
        tvUser,
        playUrl: finalUrl,
        playHeaders: finalHeaders,
      });
      if (out && normalizeString(out.url)) {
        finalUrl = normalizeString(out.url);
        finalHeaders = out.headers && typeof out.headers === 'object' ? out.headers : {};
      }
    } catch (_error) {}
  }
  if (hasNonEmptyHeaders(finalHeaders)) {
    try {
      const out = await registerCatProxyToken({
        apiBase,
        tvUser,
        url: normalizeString(sourceUrl) || finalUrl,
        headers: finalHeaders,
      });
      if (out && normalizeString(out.proxyUrl)) {
        finalUrl = normalizeString(out.proxyUrl);
        finalHeaders = {};
      }
    } catch (_error) {
      const localProxyUrl = buildLocalProxyPlaybackUrl({
        apiBase,
        sourceUrl: normalizeString(sourceUrl) || finalUrl,
        headers: finalHeaders,
      });
      if (localProxyUrl) {
        finalUrl = localProxyUrl;
        finalHeaders = {};
      }
    }
  }
  return { url: finalUrl, headers: finalHeaders, goProxyBase, relayBase };
};

export const executeResolvedSitePlayback = async ({
  runtimeSettings,
  bootstrapUser,
  siteItem,
  panEntry,
  segment,
  selectionKey,
  apiBase,
  selectedGoProxyBase = '',
} = {}) => {
  const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
  const pan = panEntry && typeof panEntry === 'object' ? panEntry : null;
  const targetSegment = segment && typeof segment === 'object' ? segment : null;
  const settings = runtimeSettings && typeof runtimeSettings === 'object' ? runtimeSettings : null;
  const resolvedApiBase = normalizeString(apiBase) || normalizecatpawrunnerApiBase(settings && settings.catpawrunnerApiBase);
  if (!item || !pan || !targetSegment || !normalizeString(targetSegment.episodeUrl) || !resolvedApiBase) {
    throw new Error('播放参数不完整');
  }
  const tvUser = normalizeString(bootstrapUser);
  const spiderApi = normalizeString(item && item.spiderApi);
  const siteIdMatch = /^\/([a-f0-9]{10})\/spider\//.exec(spiderApi);
  const siteId = siteIdMatch && siteIdMatch[1] ? normalizeString(siteIdMatch[1]) : '';
  const provider = normalizeString(pan && pan.provider);
  const flag = normalizeString(pan && pan.label);
  const detailData = item && item.detailData && typeof item.detailData === 'object' ? item.detailData : {};
  let raw;
  if (provider) {
    raw = await requestBuiltinPanPlay({
      provider,
      flag,
      id: targetSegment.episodeUrl,
      rawName: targetSegment.rawName,
      tvUser,
      panMock189AccessByShareId:
        detailData.panMock189AccessByShareId && typeof detailData.panMock189AccessByShareId === 'object'
          ? detailData.panMock189AccessByShareId
          : {},
    });
  } else {
    raw = await requestCatPlay({
      apiBase: resolvedApiBase,
      username: tvUser,
      payload: {
        flag,
        id: targetSegment.episodeUrl,
        siteApi: spiderApi,
        ...(siteId ? { siteId } : {}),
      },
    });
  }
  const rewritten = rewritePlayPayloadUrls(raw, resolvedApiBase, tvUser);
  const payload = normalizePlayPayload(rewritten);
  const rawHeaders = payload && payload.header && typeof payload.header === 'object' ? payload.header : {};
  const resolvedPlay = resolvePlayTargetForPlayback({ payload, rawHeaders });
  let finalUrl = normalizeString(resolvedPlay && resolvedPlay.url);
  let finalHeaders = resolvedPlay && resolvedPlay.headers && typeof resolvedPlay.headers === 'object'
    ? resolvedPlay.headers
    : {};
  if (!finalUrl) throw new Error('无可用播放地址');
  const preferredPan = (() => {
    const key = normalizeString(provider).toLowerCase();
    if (key === 'baidu') return 'baidu';
    if (key === 'quark' || key === 'uc') return 'quark';
    return '';
  })();
  const sourceUrl = normalizeString(resolvedPlay && resolvedPlay.proxySourceUrl) || finalUrl;
  const relayResolveUrl = normalizeString(resolvedPlay && resolvedPlay.relayResolveUrl);
  const proxyRetryCandidate = hasNonEmptyHeaders(finalHeaders)
    ? {
      selectionKey: normalizeString(selectionKey),
      apiBase: resolvedApiBase,
      tvUser,
      url: finalUrl,
      sourceUrl,
      relayResolveUrl,
      headers: finalHeaders,
      preferredPan,
    }
    : null;
  const lastGoProxyCandidate = {
    apiBase: resolvedApiBase,
    tvUser,
    url: proxyRetryCandidate ? proxyRetryCandidate.url : finalUrl,
    sourceUrl,
    relayResolveUrl,
    headers: proxyRetryCandidate ? proxyRetryCandidate.headers : finalHeaders,
    preferredPan,
    enabled: !!proxyRetryCandidate || hasNonEmptyHeaders(finalHeaders),
  };
  const m3u8RelayEligible =
    !!(settings && settings.relayEnabled)
    && pickEligibleRelayServers({ relayServers: settings && settings.relayServers }).length > 0;
  const lastM3U8RelayCandidate = m3u8RelayEligible && isProbablyM3U8Url(finalUrl)
    ? {
      selectionKey: normalizeString(selectionKey),
      url: finalUrl,
      enabled: true,
    }
    : null;
  return {
    playerUrl: finalUrl,
    playerHeaders: finalHeaders,
    goProxyBase: '',
    relayBase: '',
    lastGoProxyCandidate,
    lastM3U8RelayCandidate,
  };
};

export const executeProxyRetryPlayback = async ({
  candidate,
  runtimeSettings,
  selectedGoProxyBase,
} = {}) => {
  const target = candidate && typeof candidate === 'object' ? candidate : null;
  if (!target || !normalizeString(target.url)) {
    return { ok: false, playerUrl: '', playerHeaders: {}, goProxyBase: '' };
  }
  const out = await applyPlaybackProxyChain({
    apiBase: target.apiBase,
    tvUser: target.tvUser,
    sourceUrl: target.sourceUrl,
    playUrl: target.url,
    playHeaders: target.headers,
    relayResolveUrl: target.relayResolveUrl,
    preferredPan: target.preferredPan,
    runtimeSettings,
    selectedGoProxyBase,
  });
  const nextUrl = normalizeString(out && out.url);
  if (!nextUrl) {
    return { ok: false, playerUrl: '', playerHeaders: {}, goProxyBase: '' };
  }
  return {
    ok: true,
    playerUrl: nextUrl,
    playerHeaders: out && out.headers && typeof out.headers === 'object' ? out.headers : {},
    goProxyBase: normalizeString(out && out.goProxyBase),
    relayBase: normalizeString(out && out.relayBase),
  };
};

export const executeM3U8RelayRetryPlayback = ({ candidate, runtimeSettings } = {}) => {
  const target = candidate && typeof candidate === 'object' ? candidate : null;
  if (!target || !normalizeString(target.url)) {
    return { ok: false, playerUrl: '', playerHeaders: {}, relayBase: '' };
  }
  const out = maybeUseRelayM3U8ForPlayback({
    playUrl: target.url,
    runtimeSettings,
  });
  const nextUrl = normalizeString(out && out.url);
  if (!nextUrl) {
    return { ok: false, playerUrl: '', playerHeaders: {}, relayBase: '' };
  }
  return {
    ok: true,
    playerUrl: nextUrl,
    playerHeaders: out && out.headers && typeof out.headers === 'object' ? out.headers : {},
    relayBase: normalizeString(out && out.relayBase),
  };
};

export const isProbablyM3U8Url = (value) => {
  const raw = normalizeString(value);
  if (!raw) return false;
  try {
    const url = new URL(raw, window.location.href);
    const hinted = normalizeString(url.searchParams.get('__tv_fmt')).toLowerCase();
    if (hinted === 'm3u8' || hinted === 'hls') return true;
    return String(url.pathname || '').toLowerCase().endsWith('.m3u8');
  } catch (_error) {
    const noQuery = raw.split('#')[0].split('?')[0].toLowerCase();
    return noQuery.endsWith('.m3u8');
  }
};

const fetchM3U8Text = async ({ url, tvUser }) => {
  const target = normalizeString(url);
  if (!target) throw new Error('missing m3u8 url');
  const safeUser = normalizeString(tvUser);
  const resp = await fetch(target, {
    method: 'GET',
    mode: 'cors',
    credentials: 'omit',
    cache: 'no-store',
    headers: safeUser ? { 'X-TV-User': safeUser } : {},
  });
  if (!resp.ok) throw new Error(`m3u8 http ${resp.status}`);
  return await resp.text();
};

const parseM3U8FirstUrls = (text) => {
  const lines = String(text || '').split(/\r?\n/);
  let firstUri = '';
  let keyUri = '';
  for (let i = 0; i < lines.length; i += 1) {
    const current = normalizeString(lines[i]);
    if (!current) continue;
    if (current.startsWith('#')) {
      if (!keyUri && /^#EXT-X-KEY\b/i.test(current) && /URI\s*=\s*"/i.test(current)) {
        const match = /URI\s*=\s*"([^"]+)"/i.exec(current);
        if (match && match[1]) keyUri = normalizeString(match[1]);
      }
      continue;
    }
    if (!firstUri) firstUri = current;
    if (firstUri && keyUri) break;
  }
  return { firstUri, keyUri };
};

const buildProbeRequestHeaders = (rawHeaders) => {
  const out = { Range: 'bytes=0-0' };
  const headers = rawHeaders && typeof rawHeaders === 'object' ? rawHeaders : {};
  const blocked = new Set(['origin', 'referer', 'host', 'cookie', 'user-agent', 'content-length', 'content-encoding', 'accept-encoding', 'connection', 'range']);
  Object.keys(headers).forEach((key) => {
    const normalizedKey = normalizeString(key);
    if (!normalizedKey) return;
    const lower = normalizedKey.toLowerCase();
    if (blocked.has(lower) || lower.startsWith('sec-')) return;
    const value = headers[key];
    const text = Array.isArray(value) ? normalizeString(value[0]) : normalizeString(value);
    if (!text) return;
    out[normalizedKey] = text;
  });
  return out;
};

const probeFetchSmall = async (urlString, timeoutMs = 6000, requestHeaders = {}) => {
  const target = normalizeString(urlString);
  if (!target) return { ok: false, message: 'missing url' };
  const controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
  const timer = setTimeout(() => {
    try {
      if (controller) controller.abort();
    } catch (_error) {}
  }, timeoutMs);
  try {
    const resp = await fetch(target, {
      method: 'GET',
      mode: 'cors',
      credentials: 'omit',
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
      headers: buildProbeRequestHeaders(requestHeaders),
      signal: controller ? controller.signal : undefined,
    });
    if (!resp || !resp.ok) return { ok: false, message: `http ${resp && resp.status ? resp.status : 0}` };
    return { ok: resp.status === 200 || resp.status === 206, message: '' };
  } catch (error) {
    return { ok: false, message: error && error.name === 'AbortError' ? 'timeout' : (error && error.message ? String(error.message) : 'fetch failed') };
  } finally {
    clearTimeout(timer);
  }
};

export const registerCatM3U8 = async ({ apiBase, tvUser, url, headers }) => {
  const base = normalizecatpawrunnerApiBase(apiBase);
  if (!base) throw new Error('catpawrunner 接口地址未设置');
  const target = new URL('api/m3u8/register', base);
  const safeUser = normalizeString(tvUser);
  const resp = await fetch(target.toString(), {
    method: 'POST',
    mode: 'cors',
    credentials: 'omit',
    headers: {
      'Content-Type': 'application/json',
      ...(safeUser ? { 'X-TV-User': safeUser } : {}),
    },
    body: JSON.stringify({
      url: normalizeString(url),
      headers: headers && typeof headers === 'object' ? headers : {},
    }),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok || !data || data.ok === false) {
    throw new Error(data && (data.message || data.error) ? String(data.message || data.error) : `HTTP ${resp.status}`);
  }
  const indexPath = normalizeString(data && data.index);
  const proxyPath = normalizeString(data && data.proxy);
  if (!indexPath || !proxyPath) throw new Error('catpawrunner m3u8 register 返回无效');
  return {
    indexUrl: new URL(indexPath.replace(/^\//, ''), base).toString(),
    proxyUrl: new URL(proxyPath.replace(/^\//, ''), base).toString(),
  };
};

export const maybeUseCatM3U8ProxyForPlayback = async ({ apiBase, tvUser, playUrl, playHeaders }) => {
  if (!isProbablyM3U8Url(playUrl) || !apiBase) return null;
  if (!hasNonEmptyHeaders(playHeaders)) {
    try {
      await fetchM3U8Text({ url: playUrl, tvUser });
      return { url: playUrl, headers: playHeaders || {} };
    } catch (_error) {}
  }
  const { indexUrl, proxyUrl } = await registerCatM3U8({ apiBase, tvUser, url: playUrl, headers: playHeaders });
  let text = '';
  try {
    text = await fetchM3U8Text({ url: indexUrl, tvUser });
  } catch (_error) {
    return { url: proxyUrl, headers: {} };
  }
  const { firstUri, keyUri } = parseM3U8FirstUrls(text);
  if (!firstUri) return { url: proxyUrl, headers: {} };
  if (String(firstUri).toLowerCase().endsWith('.m3u8')) return { url: proxyUrl, headers: {} };
  if (keyUri) {
    const keyProbe = await probeFetchSmall(keyUri, 6000, playHeaders);
    if (!keyProbe.ok) return { url: proxyUrl, headers: {} };
  }
  const segProbe = await probeFetchSmall(firstUri, 6000, playHeaders);
  if (!segProbe.ok) return { url: proxyUrl, headers: {} };
  return { url: indexUrl, headers: {} };
};
