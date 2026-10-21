const SHORT_DURATION = 360;
const CACHE_BYTES = 512 * 1024 * 1024;
const WINDOW_SIZE = 7;
const NORMAL_BUFFER = {
  maxBufferLength: 60,
  maxMaxBufferLength: 120,
  backBufferLength: 75,
  maxBufferSize: 200e6,
};

const sourceKey = (url, headers) => JSON.stringify([
  url,
  Object.keys(headers || {}).sort().map(key => [key, headers[key]]),
]);

// keys are episode keys in playback order, not expiring media URLs.
export const createShortVideoBuffer = ({ loadHls, detectFormat, preferNativeHls, host }) => {
  const entries = new Map();
  let keys = [];
  let next = new Set();
  let active = null;
  let focusKey = null;
  let visible = true;

  const current = (entry, media) => entries.get(entry.key) === entry && entry.media === media;
  const snapshot = (entry) => {
    const position = entry.media?.video.currentTime;
    if (Number.isFinite(position) && position >= 0) entry.position = position;
  };
  const duration = (media) => media.live || media.video.duration === Infinity
    ? Infinity : Math.max(media.manifestDuration, Number(media.video.duration) || 0);
  const short = (media) => (media.native || media.hls) && duration(media) > 0 && duration(media) < SHORT_DURATION;
  const bytes = (entry) => entry.media?.hls
    ? Math.max(entry.media.xhrBytes, entry.media.fragmentBytes) : 0;
  const listen = (target, name, callback, cleanups) => {
    target.addEventListener(name, callback);
    const remove = () => {
      target.removeEventListener(name, callback);
      cleanups.delete(remove);
    };
    cleanups.add(remove);
    return remove;
  };
  const restoreConfig = (media) => {
    if (media.hls) Object.assign(media.hls.config, media.normalConfig);
  };

  // destroy=false relinquishes ownership WITHOUT touching the video/engine.
  // In particular park(false) and clear(active) leave destruction to ArtPlayer.
  const release = (entry, destroy) => {
    const media = entry.media;
    if (!media) return;
    entry.media = null; // Invalidates pending imports and stale callbacks first.
    media.unbindHls?.();
    for (const remove of [...media.cleanups]) remove();
    if (!destroy) return;
    try { media.video.pause(); } catch (_) {}
    for (const name of ['hls', 'flv', 'dash']) {
      try {
        const result = media.customPlayer?.[name]?.destroy();
        if (result?.catch) result.catch(() => {});
      } catch (_) {}
    }
    media.video.removeAttribute('src');
    try { media.video.load(); } catch (_) {}
    media.video.remove();
  };
  const discard = (entry) => {
    entry.failed = entry.source;
    release(entry, true);
  };
  const configure = (media) => {
    if (!media.hls) return;
    if (short(media) && !media.blocked && !media.invalid) {
      Object.assign(media.hls.config, {
        maxBufferLength: duration(media),
        maxMaxBufferLength: duration(media),
        backBufferLength: Infinity,
        maxBufferSize: CACHE_BYTES,
      });
    } else restoreConfig(media);
  };
  const farthestFirst = (a, b) => {
    const focus = keys.indexOf(focusKey);
    return Math.abs(keys.indexOf(b.key) - focus) - Math.abs(keys.indexOf(a.key) - focus);
  };
  const enforceBudget = () => {
    let total = [...entries.values()].reduce((sum, entry) => sum + bytes(entry), 0);
    if (total <= CACHE_BYTES) return;
    const background = [...entries.values()].filter(entry => entry !== active && bytes(entry) > 0);
    // Consider next episodes last, even when already visited.
    background.sort((a, b) => Number(next.has(a.key)) - Number(next.has(b.key))
      || Number(b.visited) - Number(a.visited) || farthestFirst(a, b));
    for (const entry of background) {
      total -= bytes(entry);
      discard(entry);
      if (total <= CACHE_BYTES) return;
    }
    if (active?.media) {
      // Current playback is never stopped for a cache budget. Its rolling buffer
      // remains player-owned, and cannot be parked as retained session media.
      active.media.blocked = true;
      restoreConfig(active.media);
    }
  };
  const loading = (entry) => {
    const media = entry.media;
    if (!media) return;
    const background = entry !== active;
    const idle = background && (!visible || !next.has(entry.key));
    if (media.native) media.video.preload = idle ? 'none' : background && !short(media) ? 'metadata' : 'auto';
    if (!media.hls) return;
    // Load playlists first. A next episode cannot request segments until its
    // duration is known; pauseBuffering leaves the manifest request intact.
    if (media.managedStart && !media.started && media.manifestParsed && !idle) {
      media.started = true;
      media.hls.startLoad(-1);
      if (!current(entry, media)) return;
    }
    const pause = idle || (background && !short(media));
    if (pause) {
      if (media.hls.pauseBuffering) media.hls.pauseBuffering();
      // Do not abort a pending manifest: startLoad cannot re-request it.
      else if (media.manifestLoaded && !media.stopped) {
        media.hls.stopLoad();
        media.stopped = true;
      }
      media.paused = true;
    } else if (media.paused) {
      if (media.stopped) media.hls.startLoad(-1);
      else media.hls.resumeBuffering?.();
      media.stopped = false;
      media.paused = false;
    }
  };
  const update = (entry) => {
    const media = entry.media;
    if (!media) return;
    configure(media);
    if (entry !== active && (duration(media) >= SHORT_DURATION || media.invalid)) {
      discard(entry);
      return;
    }
    loading(entry);
    enforceBudget();
  };
  const error = (entry, media, message, quota = false) => {
    if (!current(entry, media)) return;
    if (quota) {
      for (const old of [...entries.values()].filter(item => item !== active).sort(farthestFirst)) {
        if (old.media) discard(old);
      }
    }
    if (!current(entry, media)) return;
    if (entry !== active) {
      discard(entry);
      return;
    }
    media.invalid = true;
    restoreConfig(media);
    media.onError?.(message);
  };
  const bindHls = (entry, media, hls) => {
    if (media.hls === hls) return;
    media.unbindHls?.();
    media.hls = hls;
    media.native = false;
    media.normalConfig = Object.fromEntries(Object.keys(NORMAL_BUFFER).map(key => [key, hls.config[key]]));
    const cleanups = new Set();
    const events = hls.constructor.Events;
    const on = (name, callback) => {
      if (!events?.[name]) return;
      hls.on(events[name], callback);
      cleanups.add(() => hls.off(events[name], callback));
    };
    const originalXhrSetup = hls.config.xhrSetup;
    const xhrSetup = function (xhr, requestUrl) {
      const result = originalXhrSetup?.call(this, xhr, requestUrl);
      if (!current(entry, media)) return result;
      let loaded = 0;
      const progress = (event) => {
        if (!current(entry, media)) return;
        const value = Number(event.loaded) || 0;
        media.xhrBytes += Math.max(0, value - loaded);
        loaded = Math.max(loaded, value);
        enforceBudget();
      };
      const removes = [];
      const finish = () => removes.forEach(remove => remove());
      removes.push(listen(xhr, 'progress', progress, cleanups));
      removes.push(listen(xhr, 'load', event => { progress(event); finish(); }, cleanups));
      removes.push(listen(xhr, 'error', finish, cleanups));
      removes.push(listen(xhr, 'abort', finish, cleanups));
      return result;
    };
    hls.config.xhrSetup = xhrSetup;
    media.unbindHls = () => {
      for (const remove of [...cleanups]) remove();
      cleanups.clear();
      if (hls.config.xhrSetup === xhrSetup) hls.config.xhrSetup = originalXhrSetup;
      if (media.managedStart) hls.config.autoStartLoad = true;
      restoreConfig(media);
    };
    const details = (value) => {
      if (!value) return;
      if (value.live) media.live = true;
      if (Number.isFinite(value.totalduration)) media.manifestDuration = value.totalduration;
    };
    const manifest = (_event, data) => {
      if (!current(entry, media)) return;
      media.manifestLoaded = true;
      details(data?.details);
      for (const level of data?.levels || []) details(level.details);
      update(entry);
    };
    on('MANIFEST_LOADED', manifest);
    on('MANIFEST_PARSED', (event, data) => {
      media.manifestParsed = true;
      manifest(event, data);
    });
    on('LEVEL_LOADED', manifest);
    on('ERROR', (_event, data) => {
      if (!data?.fatal) return;
      const message = String(data.details || data.type || 'HLS 播放失败');
      const quota = data.error?.name === 'QuotaExceededError' || /quota|bufferFullError/i.test(message);
      error(entry, media, message, quota);
    });
    on('FRAG_LOADED', (_event, data) => {
      if (!current(entry, media)) return;
      // XHR progress and fragment stats describe the SAME bytes; do not add them.
      media.fragmentBytes += (data.stats || data.part?.stats || data.frag?.stats)?.loaded || 0;
      enforceBudget();
    });
    details(hls.latestLevelDetails || hls.levels?.[hls.currentLevel]?.details);
  };
  const makeMedia = (entry, video, native) => {
    const media = {
      video, native, customPlayer: null, hls: null, attached: false,
      activated: false, activation: 0, manifestDuration: 0, live: false,
      xhrBytes: 0, fragmentBytes: 0, blocked: false, invalid: false,
      paused: false, stopped: false, cleanups: new Set(), onError: null,
    };
    entry.media = media;
    for (const event of ['loadedmetadata', 'durationchange']) {
      listen(video, event, () => { if (current(entry, media)) update(entry); }, media.cleanups);
    }
    listen(video, 'error', () => error(entry, media, '媒体播放失败'), media.cleanups);
    return media;
  };
  const makeEntry = ({ key, url, headers }) => {
    const entry = {
      key, url, headers: { ...headers }, source: sourceKey(url, headers),
      visited: false, position: 0, failed: null, media: null,
    };
    entries.set(key, entry);
    return entry;
  };
  const prewarm = (entry) => {
    if (!visible || entry.media || entry.visited || entry.failed === entry.source) return;
    const format = detectFormat(entry.url);
    const hasHeaders = Object.values(entry.headers).some(value => value != null);
    if ((format !== 'native' && format !== 'hls') || (format === 'native' && hasHeaders)) return;
    const video = host.ownerDocument.createElement('video');
    const native = format === 'native' || (
      preferNativeHls && !hasHeaders && !!video.canPlayType('application/vnd.apple.mpegurl')
    );
    video.width = 2;
    video.height = 2;
    video.autoplay = false;
    video.muted = true;
    video.defaultMuted = true;
    video.playsInline = true;
    video.controls = false;
    video.preload = native ? 'metadata' : 'auto';
    const media = makeMedia(entry, video, native);
    host.appendChild(video);
    if (native) {
      video.setAttribute('referrerpolicy', 'no-referrer');
      try {
        video.src = entry.url;
        media.attached = true;
        video.load();
      } catch (_) { discard(entry); }
      return;
    }
    const start = async () => {
      try {
        const Hls = await loadHls();
        if (!current(entry, media) || entry === active) return;
        if (!Hls?.isSupported()) {
          discard(entry);
          return;
        }
        const headers = entry.headers;
        const hls = new Hls({
          enableWorker: true,
          autoStartLoad: false,
          startPosition: 0,
          ...NORMAL_BUFFER,
          xhrSetup(xhr, requestUrl) {
            if (!xhr.readyState) xhr.open('GET', requestUrl, true);
            xhr.withCredentials = false;
            for (const [name, value] of Object.entries(headers)) {
              if (value != null) xhr.setRequestHeader(name, String(value));
            }
          },
        });
        media.managedStart = true;
        media.customPlayer = { hls };
        bindHls(entry, media, hls);
        hls.loadSource(entry.url);
        if (!current(entry, media)) return;
        hls.attachMedia(video);
        media.attached = true;
        update(entry);
      } catch (_) {
        if (current(entry, media)) discard(entry);
      }
    };
    start();
  };

  const sync = (candidates, windowKeys) => {
    keys = [...new Set(windowKeys)].slice(0, WINDOW_SIZE);
    const wanted = new Map(candidates.filter(item => keys.includes(item.key)).map(item => [item.key, item]));
    next = new Set(wanted.keys());
    for (const [key, entry] of entries) {
      if (entry === active) continue;
      if (!keys.includes(key)) {
        release(entry, true);
        entries.delete(key);
      }
    }
    for (const item of wanted.values()) {
      let entry = entries.get(item.key);
      if (entry === active) continue;
      if (!entry) entry = makeEntry(item);
      else if (entry.source !== sourceKey(item.url, item.headers)) {
        release(entry, true);
        entry.url = item.url;
        entry.headers = { ...item.headers };
        entry.source = sourceKey(item.url, item.headers);
        entry.failed = null;
      }
      prewarm(entry);
    }
    for (const entry of entries.values()) loading(entry);
    enforceBudget();
  };
  const markActive = (entry) => {
    if (active && active !== entry) {
      snapshot(active);
      release(active, false);
    }
    active = entry;
    focusKey = entry.key;
    entry.visited = true;
  };
  const adopt = ({ key, url, headers, video, customPlayer, activated, onError }) => {
    let entry = entries.get(key);
    if (!entry) entry = makeEntry({ key, url, headers });
    if (entry.media && entry.media.video !== video) release(entry, entry !== active);
    entry.url = url;
    entry.headers = { ...headers };
    entry.source = sourceKey(url, headers);
    markActive(entry);
    let media = entry.media;
    if (!media) {
      const format = detectFormat(url);
      const native = format === 'native' || (format === 'hls' && (
        customPlayer?.direct || (preferNativeHls && video.canPlayType('application/vnd.apple.mpegurl')
          && !Object.values(entry.headers).some(value => value != null))
      ));
      media = makeMedia(entry, video, !!native);
    }
    if (customPlayer) media.customPlayer = customPlayer;
    if (media.customPlayer?.hls) bindHls(entry, media, media.customPlayer.hls);
    else if (media.customPlayer?.direct && detectFormat(url) === 'hls') media.native = true;
    media.attached = !!(media.hls || video.currentSrc || video.src);
    if (activated) {
      media.activated = true;
      media.activation++;
    }
    if (onError !== undefined) media.onError = onError;
    update(entry);
  };
  const detach = (video) => {
    for (const entry of entries.values()) {
      if (entry.media?.video !== video) continue;
      snapshot(entry);
      release(entry, false);
      if (active === entry) active = null;
    }
  };
  const take = (url, headers, key) => {
    const entry = entries.get(key);
    if (!entry) return null;
    if (entry.source !== sourceKey(url, headers)) {
      if (!entry.visited) return null;
      release(entry, entry !== active);
      entry.url = url;
      entry.headers = { ...headers };
      entry.source = sourceKey(url, headers);
      entry.failed = null;
    }
    const media = entry.media;
    if (!media && !entry.visited) return null;
    if (media && !media.attached) {
      discard(entry); // An unresolved import cannot be handed to the player.
      if (!entry.visited) return null;
    }
    const restored = entry.visited;
    markActive(entry);
    const held = entry.media;
    update(entry);
    return {
      video: held?.video || null,
      customPlayer: held?.customPlayer || null,
      get activated() { return held?.activated || false; },
      restored,
      position: entry.position,
      resume(onError) {
        if (!held || !current(entry, held) || active !== entry) return;
        if (onError !== undefined) held.onError = onError;
        update(entry);
      },
      destroy() {
        if (!held || !current(entry, held)) return;
        snapshot(entry);
        discard(entry);
        if (active === entry) active = null;
      },
    };
  };
  // true: helper owns the retained media; external teardown MUST NOT destroy it.
  // false: position only, no media ownership; external teardown destroys it.
  const park = (key, { ended = false, invalid = false } = {}) => {
    const entry = entries.get(key);
    if (!entry || entry !== active) return false;
    snapshot(entry);
    if (ended) entry.position = 0;
    const media = entry.media;
    if (media) {
      try { media.video.pause(); } catch (_) {}
      if (ended) {
        try { media.video.currentTime = 0; } catch (_) {}
      }
      media.onError = null;
      enforceBudget();
    }
    active = null;
    if (!media || invalid || media.invalid || media.blocked || !media.attached || !short(media)) {
      release(entry, false);
      return false;
    }
    host.appendChild(media.video);
    loading(entry);
    return true;
  };
  // Called synchronously inside the swipe gesture, before animation/URL awaits.
  const activate = () => {
    for (const entry of entries.values()) {
      const media = entry.media;
      if (entry === active || !media?.attached || media.activated) continue;
      const { video } = media;
      const muted = video.muted;
      const activation = ++media.activation;
      try {
        video.muted = false;
        const playing = video.play();
        media.activated = true;
        Promise.resolve(playing).catch((error) => {
          if (current(entry, media) && media.activation === activation && error?.name !== 'AbortError') {
            media.activated = false;
          }
        });
      } catch (_) {
        media.activated = false;
      } finally {
        try { video.pause(); } catch (_) {}
        // Re-muting native WebKit media can discard its preloaded bytes.
        // Keep it unmuted but paused until the player takes over.
        if (!media.native) video.muted = muted;
      }
    }
  };
  const setVisible = (value) => {
    visible = !!value;
    for (const entry of entries.values()) loading(entry);
    if (visible) {
      for (const key of next) {
        const entry = entries.get(key);
        if (entry && entry !== active) prewarm(entry);
      }
    }
  };
  const clear = () => {
    for (const entry of entries.values()) release(entry, entry !== active);
    entries.clear();
    next.clear();
    keys = [];
    active = null;
    focusKey = null;
  };
  return { sync, adopt, take, park, activate, setVisible, detach, clear };
};
