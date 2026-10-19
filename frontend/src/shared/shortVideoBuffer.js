const BUFFER_SECONDS = 3;
const BUFFER_BYTES = 4 * 1024 * 1024;
const LOAD_TIMEOUT = 8000;

const entryKey = (url, headers) => JSON.stringify([
  url,
  Object.keys(headers || {}).sort().map(key => [key, headers[key]]),
]);

// preferNativeHls is a boolean; loadHls resolves to the Hls constructor.
export const createShortVideoBuffer = ({ loadHls, detectFormat, preferNativeHls, host }) => {
  const pool = new Map();
  const failed = new Set();

  const createEntry = (key, url, headers) => {
    const format = detectFormat(url);
    if (format !== 'native' && format !== 'hls') return;
    const hasHeaders = Object.values(headers).some(value => value != null);
    if (format === 'native' && hasHeaders) return;

    const video = host.ownerDocument.createElement('video');
    const native = format === 'native' || (
      preferNativeHls && !hasHeaders && video.canPlayType('application/vnd.apple.mpegurl')
    );
    video.width = 2;
    video.height = 2;
    video.autoplay = false;
    video.muted = true;
    video.defaultMuted = true;
    video.playsInline = true;
    video.controls = false;
    video.preload = 'metadata';
    if (native) video.setAttribute('referrerpolicy', 'no-referrer');

    let hls = null;
    let Hls = null;
    let timer = null;
    let destroyed = false;
    let stopped = false;
    let firstFrame = false;
    let fatalListener = null;
    let xhrBytes = 0;
    let fragmentBytes = 0;
    const listeners = [];
    const current = () => !destroyed && pool.get(key) === entry;
    const bufferedSeconds = () => video.buffered.length
      ? video.buffered.end(0) - video.buffered.start(0) : 0;
    const ready = () => video.readyState >= 2 || firstFrame || bufferedSeconds() > 0;

    const cleanupPrefetch = () => {
      clearTimeout(timer);
      timer = null;
      for (const remove of listeners.splice(0)) remove();
    };
    const stop = () => {
      if (!hls || stopped) return;
      stopped = true;
      hls.stopLoad();
    };
    const destroy = () => {
      if (destroyed) return;
      destroyed = true;
      if (pool.get(key) === entry) pool.delete(key);
      cleanupPrefetch();
      if (fatalListener) hls.off(Hls.Events.ERROR, fatalListener);
      try { if (hls) hls.destroy(); } catch (_) {}
      video.removeAttribute('src');
      try { video.load(); } catch (_) {}
      video.remove();
    };
    const discard = () => {
      if (!current()) return;
      failed.add(key);
      destroy();
    };
    const listen = (target, event, callback) => {
      target.addEventListener(event, callback);
      listeners.push(() => target.removeEventListener(event, callback));
    };
    const onHls = (event, callback) => {
      hls.on(event, callback);
      listeners.push(() => hls.off(event, callback));
    };
    const checkByteLimit = () => {
      if (current() && Math.max(xhrBytes, fragmentBytes) >= BUFFER_BYTES) stop();
    };
    const resume = (onError) => {
      if (destroyed) return;
      if (!hls) {
        video.preload = 'auto';
        return;
      }
      if (fatalListener) hls.off(Hls.Events.ERROR, fatalListener);
      fatalListener = (_event, data) => {
        if (!destroyed && data && data.fatal) {
          onError(String(data.details || data.type || 'HLS 播放失败'));
        }
      };
      hls.on(Hls.Events.ERROR, fatalListener);
      Object.assign(hls.config, {
        maxBufferLength: 60,
        maxMaxBufferLength: 120,
        backBufferLength: 75,
        maxBufferSize: 200e6,
      });
      hls.startLoad(-1);
    };
    const entry = { video, customPlayer: null, ready, cleanupPrefetch, stop, resume, destroy, discard };
    pool.set(key, entry);
    host.appendChild(video);
    listen(video, 'error', discard);
    listen(video, 'loadeddata', () => { firstFrame = true; });
    timer = setTimeout(() => {
      if (!current()) return;
      stop();
      if (!ready()) discard();
    }, LOAD_TIMEOUT);

    if (native) {
      // Native HLS does not expose its manifest; duration is the available live hint.
      const checkLive = () => {
        if (format === 'hls' && video.duration === Infinity) discard();
      };
      listen(video, 'loadedmetadata', checkLive);
      listen(video, 'durationchange', checkLive);
      try {
        video.src = url;
        video.load();
      } catch (_) { discard(); }
      return;
    }

    const startHls = async () => {
      try {
        Hls = await loadHls();
        // A new entry may have the same key after sync/clear/take; keys alone are insufficient.
        if (!current()) return;
        if (!Hls || !Hls.isSupported()) {
          discard();
          return;
        }
        hls = new Hls({
          enableWorker: true,
          startPosition: 0,
          maxBufferLength: BUFFER_SECONDS,
          maxMaxBufferLength: BUFFER_SECONDS,
          maxBufferSize: BUFFER_BYTES,
          backBufferLength: 0,
          xhrSetup(xhr, requestUrl) {
            // Open before setting headers, rather than swallowing InvalidStateError.
            if (!xhr.readyState) xhr.open('GET', requestUrl, true);
            xhr.withCredentials = false;
            for (const [name, value] of Object.entries(headers)) {
              if (value != null) xhr.setRequestHeader(name, String(value));
            }
            if (!current() || stopped) return;
            let loaded = 0;
            const progress = (event) => {
              if (!current() || stopped) return;
              xhrBytes += Math.max(0, event.loaded - loaded);
              loaded = Math.max(loaded, event.loaded);
              checkByteLimit();
            };
            // Progress enforces the budget even before a large first fragment completes.
            listen(xhr, 'progress', progress);
            listen(xhr, 'load', progress);
          },
        });
        entry.customPlayer = { hls };
        onHls(Hls.Events.ERROR, discard);
        const checkLive = (_event, data) => {
          if (data.details?.live || data.levels?.some(level => level.details?.live)) discard();
        };
        onHls(Hls.Events.MANIFEST_LOADED, checkLive);
        onHls(Hls.Events.LEVEL_LOADED, checkLive);
        onHls(Hls.Events.FRAG_LOADED, (_event, data) => {
          if (!current() || stopped) return;
          fragmentBytes += (data.stats || data.part?.stats || data.frag?.stats)?.loaded || 0;
          // This fallback and XHR progress are parallel counters, not additive.
          checkByteLimit();
        });
        onHls(Hls.Events.FRAG_BUFFERED, () => {
          if (current() && bufferedSeconds() >= BUFFER_SECONDS) stop();
        });
        hls.loadSource(url);
        if (current()) hls.attachMedia(video);
      } catch (_) {
        discard();
      }
    };
    startHls();
  };

  const sync = (candidates) => {
    const next = new Map();
    for (const candidate of candidates) {
      if (next.size === 2) break;
      const headers = { ...candidate.headers };
      next.set(entryKey(candidate.url, headers), { url: candidate.url, headers });
    }
    // Evict before allocating replacements, including while loadHls is pending.
    for (const [key, entry] of pool) {
      if (!next.has(key)) entry.destroy();
    }
    for (const key of failed) {
      if (!next.has(key)) failed.delete(key);
    }
    for (const [key, { url, headers }] of next) {
      if (!pool.has(key) && !failed.has(key)) createEntry(key, url, headers);
    }
  };
  const take = (url, headers) => {
    const key = entryKey(url, headers);
    const entry = pool.get(key);
    if (!entry) return null;
    if (!entry.ready()) {
      entry.discard();
      return null;
    }
    pool.delete(key);
    entry.cleanupPrefetch();
    entry.stop();
    return {
      video: entry.video,
      customPlayer: entry.customPlayer,
      resume: entry.resume,
      destroy: entry.destroy,
    };
  };
  const clear = () => {
    for (const entry of pool.values()) entry.destroy();
    failed.clear();
  };
  return { sync, take, clear };
};
