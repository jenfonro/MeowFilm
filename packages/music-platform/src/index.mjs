import { performance } from 'node:perf_hooks';
import { createClientContexts } from './clients.mjs';
import { cloneJSONValue, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { createMusicTransport } from './transport.mjs';
import { fail, errorCode, MusicPlatformError, musicPlatformCodes } from './errors.mjs';
import { input, object, protocolInteger, publicText, required, rows, scalar } from './model.mjs';
import { cookieFields, credentialSecrets, encryptedWyResponse, kgRequest, kgSalt, kgSuccess, md5, wyRequest, wySuccess, txRequest, txSuccess } from './protocols.mjs';
import { waitForFlight } from './flight.mjs';
import { xeapiRequest } from './xeapi.mjs';
import { wyProvider } from './providers/wy.mjs';
import { txProvider } from './providers/tx.mjs';
import { kgProvider } from './providers/kg.mjs';
import { kwProvider } from './providers/kw.mjs';
import { mgProvider } from './providers/mg.mjs';
import { createAccountOperations } from './accounts.mjs';
import { createMutations } from './mutations.mjs';

export { MusicPlatformError, musicPlatformCodes };
const providers = { wy: wyProvider, tx: txProvider, kg: kgProvider, kw: kwProvider, mg: mgProvider };
const writes = new Set(['dailyStyle.save', 'sync.write', 'feedback.send', 'feedback.upload', 'vip.checkIn', 'sms.send']);
const physicalCancellation = new Set(['audio', 'lyrics', 'chorus', 'account.verify', 'account.refresh', 'account.profile',
  'qr.create', 'qr.poll', 'sms.login', 'feedback.prepare', 'feedback.qualify', 'sync.prepare']);
const isAccountOperation = operation => typeof operation === 'string' &&
  (operation.startsWith('account.') || operation.startsWith('qr.') ||
    operation.startsWith('sms.') || operation.startsWith('vip.'));

/** Shared request/parse owner. Only anonymous client state and recoverable API results live here.
 * The native account owner publishes an authorized session after its durable save. */
export function createMusicPlatform({ dataRoot, debug, tokenProvider } = {}) {
  const transport = createMusicTransport({ debug });
  const clock = Date.now;
  const clients = createClientContexts({ dataRoot, transport, clock });
  const accounts = createAccountOperations(), mutations = createMutations();
  const sessions = new Map(), active = new Set(), flightEntries = new Set(), cache = new Map(), flights = new Map(), activeRefreshes = new Map();
  let cacheBytes = 0, cacheHits = 0, cacheMisses = 0, cacheEvictions = 0, cacheExpiredPurges = 0, refreshSeq = 0;
  let closed = false, closing, xeapiState = {};
  const cacheMaxEntries = 64, cacheMaxBytes = 2 * 1024 * 1024;
  function erase(key) {
    const entry = cache.get(key);
    if (entry) { cacheBytes -= entry.bytes; cache.delete(key); }
  }
  function purgeExpired(at) {
    for (const [key, entry] of cache) {
      if (entry.expires <= at) {
        erase(key);
        cacheExpiredPurges++;
      }
    }
  }
  function invalidate(platform) {
    for (const [key, entry] of cache) if (!platform || entry.platform === platform) erase(key);
    for (const [key, flight] of flights) if (!platform || flight.platform === platform) {
      flight.invalidated = true;
      flights.delete(key);
    }
    for (const entry of flightEntries) if (!platform || entry.platform === platform) entry.flight.invalidated = true;
  }
  function guardPlatform(platform) {
    if (!Object.hasOwn(providers, platform)) fail('UNSUPPORTED');
  }
  async function sessionUpdate(payload) {
    if (closed) fail('CLOSED');
    object(payload, 'INVALID_REQUEST');
    const { platform, session = null } = payload;
    guardPlatform(platform);
    if (session !== null) object(session, 'INVALID_REQUEST');
    debug?.emit({
      level: 'info',
      cat: 'music-account',
      sub: `${platform}.session`,
      event: 'session.update',
      data: { platform, hasSession: session !== null, session },
    });
    const previous = sessions.get(platform);
    const next = session === null ? null : { scope: input(session.scope), generation: session.generation,
      userId: input(session.userId), cookie: input(session.cookie) };
    if (!previous && !next) return { published: true };
    if (next && previous?.scope === next.scope && previous?.generation === next.generation &&
        previous?.cookie === next.cookie && previous?.userId === next.userId) return { published: true };
    if (next) sessions.set(platform, Object.freeze(next)); else sessions.delete(platform);
    if (platform === 'wy') xeapiState = {};
    accounts.invalidate(platform);
    mutations.invalidate(platform);
    invalidate(platform);
    const retired = [...active, ...flightEntries].filter(entry => entry.platform === platform && !entry.candidate && (!entry.committed || entry.readback));
    for (const entry of retired) entry.controller.abort(new MusicPlatformError('STALE_SESSION'));
    // A native invalidation/close acknowledgement must include physical settlement,
    // not just the HTTP subscribers which may already have relinquished their reads.
    await Promise.allSettled(retired.map(entry => entry.work));
    return { published: true };
  }
  function context(entry, params) {
    const { platform, session, controller } = entry, signal = controller.signal, keyState = xeapiState;
    const assertCurrent = () => {
      signal.throwIfAborted();
      if (!entry.candidate && (!entry.committed || entry.readback) && session !== sessions.get(platform)) fail('STALE_SESSION');
    };
    const c = {
      platform, account: entry.account, signal, clock, transport,
      client: () => { assertCurrent(); return clients.get(platform); }, errorCode,
      observe(path, value) {
        const type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
        const key = path + ':' + type;
        if (entry.observations.size < 32 || entry.observations.has(key))
          entry.observations.set(key, { stage: 'extract', path, type,
            ...(Array.isArray(value) ? { count: value.length } : {}) });
        return value;
      },
      cancel() { controller.abort(new MusicPlatformError('CANCELLED')); },
      drain() { return entry.work.catch(() => {}); },
      isCurrent() { return !signal.aborted && (entry.candidate || session === sessions.get(platform)); },
      async readback(receipt, action) {
        // The write receipt stays committed; only the optional read resumes
        // ordinary session/close ownership and physical draining.
        entry.readback = receipt;
        assertCurrent();
        const result = await action();
        assertCurrent();
        return result;
      },
      accountRequired() { if (!c.account) fail('AUTH_REQUIRED'); return c.account; },
      verifyTxFeedback: () => accounts.verifyTxFeedback(c),
      dispatched() { assertCurrent(); entry.committed = true; },
      invalidateCache() { invalidate(platform); },
      async read(recipe) { assertCurrent(); const result = await transport({ ...recipe, signal }); assertCurrent(); return result.value; },
      async response(recipe, options = {}) {
        assertCurrent();
        const result = await transport({ ...recipe, signal, dispatched: options.dispatched });
        assertCurrent(); return result;
      },
      async wy(path, data, weapi = false, options = {}) {
        assertCurrent();
        const client = clients.get('wy');
        const recipe = wyRequest(path, data, client, c.account, clock, weapi, options.token, options.vip);
        assertCurrent();
        const reply = await transport({ ...recipe,
          signal, dispatched: options.dispatched, json: !options.vip });
        assertCurrent();
        if (options.vip) reply.value = encryptedWyResponse(reply.value);
        if (!options.raw) try { wySuccess(reply.value, options); }
        catch (error) { if (error.code !== 'INVALID_RESPONSE') options.rejected?.(); throw error; }
        if (!entry.committed) await clients.acceptWy(reply.cookies, c.account, new URL(recipe.url).hostname);
        return options.response ? reply : reply.value;
      },
      async tx(module, method, data = {}, prepare = false, options = {}) {
        assertCurrent();
        const client = prepare ? await clients.prepare('tx', signal) : clients.get('tx');
        assertCurrent();
        const recipe = txRequest(module, method, data, client, c.account, clock, options);
        const reply = await transport({ ...recipe, signal, dispatched: options.dispatched });
        assertCurrent();
        try { return txSuccess(reply.value); }
        catch (error) { if (error.code !== 'INVALID_RESPONSE') options.rejected?.(); throw error; }
      },
      async kg(path, data = {}, router, body, origin, salt, options = {}) {
        assertCurrent();
        const request = kgRequest(path, data, clients.get('kg'), c.account, clock, router, body, origin, salt);
        if (options.headers) Object.assign(request.headers, options.headers);
        const reply = await transport({ ...request, signal, dispatched: options.dispatched });
        assertCurrent();
        if (reply.headers.get('ssa-code')) { options.rejected?.(); fail('VERIFICATION_REQUIRED'); }
        try { return kgSuccess(reply.value); }
        catch (error) { if (error.code !== 'INVALID_RESPONSE') options.rejected?.(); throw error; }
      },
      async kgRank(body) {
        assertCurrent();
        const recipe = kgRequest('/openapi/kmr/v2/rank/audio', {}, clients.get('kg'), c.account, clock, null, body);
        recipe.headers['kg-tid'] = '369';
        const reply = await c.response(recipe);
        if (reply.headers.get('ssa-code')) fail('VERIFICATION_REQUIRED');
        return kgSuccess(reply.value);
      },
      // This exact hash/MixSongID association is consumed only by remote
      // mutations and CSCC feedback, never by ordinary catalog reads.
      async kgMixRow(hash, mix) {
        let mapped;
        if (!mix || mix === '0') {
          const client = clients.get('kg'), cookies = cookieFields(c.accountRequired().cookie),
            time = Math.floor(clock() / 1000);
          const raw = kgSuccess(await c.read({ url: 'https://gateway.kugou.com/v2/album_audio/audio',
            method: 'POST', headers: { 'Content-Type': 'application/json;charset=utf-8',
              'x-router': 'kmr.service.kugou.com', 'Cache-Control': 'no-store',
              mid: cookies.KUGOU_API_MID ?? client.mid, dfid: cookies.dfid ?? client.dfid ?? '-', clienttime: String(time),
              'User-Agent': 'Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi',
              'kg-rc': '1', 'kg-thash': '5d816a0', 'kg-rec': '1', 'kg-rf': 'B9EDA08A64250DEFFBCADDEE00F8F25F' },
            body: JSON.stringify({ appid: 1005, clienttime: time, clientver: 20489, data: [{ hash: hash.toLowerCase() }],
              dfid: cookies.dfid ?? client.dfid ?? '-', key: md5('1005' + kgSalt + '20489' + time),
              mid: cookies.KUGOU_API_MID ?? client.mid, show_privilege: 0, show_author_alias: 0,
              show_rel_album_audio_info: 0, show_remarks: 0 }) }));
          let values = rows(raw.data);
          if (values.length === 1 && Array.isArray(values[0])) values = values[0];
          if (values.length !== 1) fail('REMOTE_CHANGED');
          mix = required(values[0].album_audio_id);
          mapped = required(values[0].audio_info?.hash ?? values[0].hash).toUpperCase();
        }
        const raw = await c.kg('/kmr/v2/audio', {}, 'openapi.kugou.com',
          { data: [{ entity_id: protocolInteger(mix) }], fields: 'base,audio_info,album_info,authors.base' },
          undefined, undefined, { headers: { 'KG-TID': '238', 'Cache-Control': 'no-store' } });
        const values = rows(raw.data), row = values[0], audio = row?.audio_info;
        if (values.length !== 1 || Number(row?.__status) !== 1 || scalar(row.base?.album_audio_id) !== mix ||
            !['hash', 'hash_320', 'hash_flac', 'hash_high'].some(key => scalar(audio?.[key]).toUpperCase() === hash) ||
            mapped && mapped !== scalar(audio?.hash).toUpperCase()) fail('REMOTE_CHANGED');
        return row;
      },
      async xeapi(path, data, options = {}) {
        assertCurrent();
        const value = await xeapiRequest(path, data, { transport, client: clients.get('wy'), account: c.account, clock, signal,
          ...options, keyState: entry.candidate ? {} : keyState,
          dispatched: options.dispatched ? () => { c.dispatched(); options.dispatched(); } : undefined });
        assertCurrent();
        try { return wySuccess(value, options); }
        catch (error) { if (error.code !== 'INVALID_RESPONSE') options.rejected?.(); throw error; }
      },
      async registerKg(device) { const client = await clients.registerKg(device, signal); assertCurrent(); return client; },
      async acceptKgAccount(before, device) { assertCurrent(); await clients.acceptKgAccount(before, device); assertCurrent(); },
      async acceptWyAccount(before, used, next) { assertCurrent(); await clients.acceptWyAccount(before, used, next); assertCurrent(); },
      async prepareTx() { const client = await clients.prepare('tx', signal); assertCurrent(); return client; },
      async token() {
        if (!tokenProvider) fail('SIGNATURE_REQUIRED');
        try { return await tokenProvider.get({ signal }); }
        catch (error) {
          if (signal.aborted) throw signal.reason;
          if (error.code === 'UPSTREAM_FORMAT') fail('INVALID_RESPONSE');
          if (['CLOSED', 'CANCELLED', 'TIMEOUT', 'NETWORK_ERROR', 'UPSTREAM_HTTP', 'INVALID_RESPONSE'].includes(error.code))
            fail(error.code);
          fail('SIGNATURE_REQUIRED');
        }
      },
      async cached(parts, action, ttl = 600000, { shouldCache } = {}) {
        const key = stringifyJSONValue([platform, entry.account?.scope ?? entry.account?.cookie ?? 'anonymous',
          entry.account?.generation ?? 0, parts]);
        assertCurrent();
        const isRefresh = Boolean(params.refresh);
        const now = clock();
        const old = cache.get(key);
        if (!isRefresh && old?.expires > now) {
          // True LRU: promote hit entry to most-recently-used position.
          cache.delete(key);
          cache.set(key, old);
          cacheHits++;
          debug?.emit({
            level: 'debug',
            cat: 'music-platform',
            sub: `${platform}.cache`,
            event: 'cache.hit',
            data: { platform, parts, bytes: old.bytes },
          });
          return cloneJSONValue(old.value);
        }
        cacheMisses++;
        if (old && old.expires <= now) {
          erase(key);
          cacheExpiredPurges++;
        }
        let flight = isRefresh ? undefined : flights.get(key);
        if (flight && !flight.controller.signal.aborted) {
          debug?.emit({
            level: 'debug',
            cat: 'music-platform',
            sub: `${platform}.cache`,
            event: 'cache.coalesced',
            data: { platform, parts },
          });
        } else {
          const flightController = new AbortController();
          const flightEntry = {
            platform, operation: entry.operation, controller: flightController, session,
            committed: false, readback: null, observations: new Map(),
            candidate: entry.candidate, account: entry.account,
          };
          const flightContext = context(flightEntry, isRefresh ? { ...params, refresh: false } : params);
          const waiters = new Set();
          let refreshSlot, refreshOrder = 0;
          if (isRefresh) {
            refreshOrder = ++refreshSeq;
            refreshSlot = activeRefreshes.get(key) ?? { latestSeq: 0, count: 0 };
            refreshSlot.latestSeq = refreshOrder;
            refreshSlot.count++;
            activeRefreshes.set(key, refreshSlot);
          }
          flight = { platform, controller: flightController, entry: flightEntry, waiters, invalidated: false, superseded: false };
          flightEntry.flight = flight;
          if (!isRefresh) flights.set(key, flight);
          flightEntries.add(flightEntry);
          flight.promise = Promise.resolve().then(() => action(flightContext))
            .then(result => {
              flightController.signal.throwIfAborted();
              if (!flightEntry.candidate && session !== sessions.get(platform)) fail('STALE_SESSION');
              // Only public reads and the existing private directory/daily cache are retained.
              const canStore = !flight.invalidated && !flight.superseded &&
                (isRefresh ? refreshSlot.latestSeq === refreshOrder : flights.get(key) === flight);
              if (canStore && (!flightEntry.account || ['daily', 'channels', 'channel-directory'].includes(parts[0])) &&
                  (!shouldCache || shouldCache(result))) {
                const bytes = Buffer.byteLength(stringifyJSONValue(result));
                const nowMs = clock();
                const explicitExpiry = typeof result?.expiresAt === 'number' ? result.expiresAt : Infinity;
                const expires = Math.min(nowMs + ttl, explicitExpiry);
                if (bytes <= cacheMaxBytes && expires > nowMs) {
                  erase(key);
                  if (cache.size >= cacheMaxEntries || cacheBytes + bytes > cacheMaxBytes) purgeExpired(nowMs);
                  while ((cache.size >= cacheMaxEntries || cacheBytes + bytes > cacheMaxBytes) && cache.size > 0) {
                    erase(cache.keys().next().value);
                    cacheEvictions++;
                  }
                  cache.set(key, { platform, bytes, value: cloneJSONValue(result), expires });
                  cacheBytes += bytes;
                  if (isRefresh) {
                    const concurrent = flights.get(key);
                    if (concurrent) concurrent.superseded = true;
                  }
                  debug?.emit({
                    level: 'debug',
                    cat: 'music-platform',
                    sub: `${platform}.cache`,
                    event: 'cache.store',
                    data: { platform, parts, bytes, cacheEntries: cache.size, cacheBytes },
                  });
                }
              }
              return result;
            })
            .finally(() => {
              flightEntries.delete(flightEntry);
              if (isRefresh) {
                refreshSlot.count--;
                if (refreshSlot.count === 0 && activeRefreshes.get(key) === refreshSlot) activeRefreshes.delete(key);
              } else if (flights.get(key) === flight) {
                flights.delete(key);
              }
            });
          flightEntry.work = flight.promise;
          flight.promise.catch(() => {});
        }
        const result = await waitForFlight(flight, signal);
        assertCurrent();
        for (const [obsKey, obs] of flight.entry.observations) {
          if (entry.observations.size < 32 || entry.observations.has(obsKey)) entry.observations.set(obsKey, obs);
        }
        return cloneJSONValue(result);
      },
    };
    return c;
  }
  function request(payload, { signal } = {}) {
    if (closed) fail('CLOSED');
    object(payload, 'INVALID_REQUEST');
    const { platform, operation, params = {}, candidate, expectedSession } = payload;
    guardPlatform(platform); object(params, 'INVALID_REQUEST');
    if (candidate !== undefined && candidate !== null) object(candidate, 'INVALID_REQUEST');
    if (expectedSession !== undefined && expectedSession !== null) object(expectedSession, 'INVALID_REQUEST');
    signal?.throwIfAborted();
    const startedAt = debug ? performance.now() : 0;
    const logCat = isAccountOperation(operation) ? 'music-account' : 'music-platform';
    const logSub = `${platform}.${operation}`;
    debug?.emit({
      level: 'debug',
      cat: logCat,
      sub: logSub,
      event: 'request.start',
      data: { platform, operation, params, candidate, expectedSession },
    });
    const action = Object.hasOwn(providers[platform], operation) ? providers[platform][operation] :
      Object.hasOwn(accounts.operations, operation) ? accounts.operations[operation] :
      Object.hasOwn(mutations.operations, operation) ? mutations.operations[operation] : undefined;
    if (typeof action !== 'function') fail('UNSUPPORTED');
    const controller = new AbortController(), session = sessions.get(platform);
    if (expectedSession !== undefined && (expectedSession === null ? session !== undefined :
      expectedSession.scope !== session?.scope || expectedSession.generation !== session?.generation)) fail('STALE_SESSION');
    const entry = { platform, operation, controller, session, committed: false, readback: null,
      observations: new Map(), candidate: candidate !== undefined,
      account: candidate === undefined ? session : candidate === null ? null :
        { userId: scalar(candidate.userId), cookie: input(candidate.cookie) } };
    const timer = setTimeout(() => controller.abort(new MusicPlatformError('TIMEOUT')),
      platform === 'wy' && operation === 'sync.prepare' ? 60000 : 25000);
    const abort = () => {
      if (!entry.committed && (writes.has(operation) || physicalCancellation.has(operation)))
        controller.abort(new MusicPlatformError('CANCELLED'));
    };
    signal?.addEventListener('abort', abort, { once: true });
    active.add(entry);
    const work = Promise.resolve().then(async () => {
      await clients.ready;
      controller.signal.throwIfAborted();
      if (closed) fail('CLOSED');
      const c = context(entry, params);
      let value = await action(c, params);
      if (Object.hasOwn(providers[platform], operation) && !writes.has(operation) &&
          entry.observations.size && value && typeof value === 'object' && !Array.isArray(value))
        value = { ...value, diagnostics: [...entry.observations.values()] };
      // Only the already projected public result is inspected for credential
      // reflection. Unconsumed upstream branches are never walked or validated.
      if (c.account && !['account.verify', 'account.profile', 'account.refresh', 'sms.login', 'qr.poll'].includes(operation)) {
        const secrets = credentialSecrets(c.account.cookie);
        if (secrets.length) {
          const pending = [value];
          while (pending.length) {
            const field = pending.pop();
            if (typeof field === 'string') publicText(field, secrets);
            else if (field && typeof field === 'object') for (const child of Object.values(field)) pending.push(child);
          }
        }
      }
      return value;
    })
      .then(value => {
        if (!entry.committed) controller.signal.throwIfAborted();
        if (entry.readback && (controller.signal.aborted || !entry.candidate && session !== sessions.get(platform))) {
          const readbackValue = controller.signal.reason?.code === 'TIMEOUT'
            ? { ...entry.readback, errorCode: entry.readback.errorCode ?? 'TIMEOUT' } : entry.readback;
          debug?.emit({
            level: 'debug',
            cat: logCat,
            sub: logSub,
            event: 'request.end',
            durationMs: debug ? performance.now() - startedAt : undefined,
            data: { platform, operation, readback: true, value: readbackValue },
          });
          return readbackValue;
        }
        debug?.emit({
          level: 'debug',
          cat: logCat,
          sub: logSub,
          event: 'request.end',
          durationMs: debug ? performance.now() - startedAt : undefined,
          data: { platform, operation, value },
        });
        return value;
      })
      .catch(error => {
        const failure = error instanceof MusicPlatformError ? error : new MusicPlatformError(errorCode(error));
        debug?.emit({
          level: 'warn',
          cat: logCat,
          sub: logSub,
          event: 'request.error',
          durationMs: debug ? performance.now() - startedAt : undefined,
          data: { platform, operation, code: failure.code, committed: entry.committed },
          error,
        });
        if (entry.readback) {
          const code = controller.signal.aborted ? errorCode(controller.signal.reason) : failure.code;
          return { ...entry.readback, errorCode: entry.readback.errorCode ??
            ((!entry.candidate && session !== sessions.get(platform)) || ['STALE_SESSION', 'CANCELLED', 'CLOSED'].includes(code)
              ? null : code) };
        }
        if (writes.has(operation) && operation !== 'sms.send')
          return { status: entry.committed ? 'unknown' : 'notSent', errorCode: failure.code };
        throw failure;
      })
      .finally(() => { clearTimeout(timer); signal?.removeEventListener('abort', abort); active.delete(entry); });
    entry.work = work;
    work.catch(() => {});
    if (!signal || writes.has(operation)) return work;
    // Catalog caller cancellation relinquishes only this subscriber, not admitted I/O.
    return new Promise((resolve, reject) => {
      const unsubscribe = () => reject(new MusicPlatformError('CANCELLED'));
      signal.addEventListener('abort', unsubscribe, { once: true });
      if (signal.aborted) unsubscribe();
      work.then(resolve, reject).finally(() => signal.removeEventListener('abort', unsubscribe)).catch(() => {});
    });
  }
  return {
    get ready() { return clients.ready; }, request, sessionUpdate,
    clear(payload = {}) {
      if (closed) fail('CLOSED');
      object(payload, 'INVALID_REQUEST');
      const { platform } = payload;
      if (platform) guardPlatform(platform);
      invalidate(platform);
      debug?.emit({ level: 'info', cat: 'music-platform', sub: platform ?? 'all', event: 'cache.clear', data: { platform: platform ?? null } });
      return { cleared: true };
    },
    clearMemory() {
      const clearedEntries = cache.size;
      const clearedBytes = cacheBytes;
      cache.clear();
      cacheBytes = 0;
      return { clearedEntries, clearedBytes };
    },
    describe() { return { execution: 'node', contract: 'music.platform.v1', platforms: Object.keys(providers) }; },
    diagnostics() {
      return {
        closed,
        activeRequests: active.size,
        inFlightCacheReads: flightEntries.size,
        cacheEntries: cache.size,
        cacheBytes,
        cacheMaxEntries,
        cacheCapacityBytes: cacheMaxBytes,
        cacheHits,
        cacheMisses,
        cacheEvictions,
        cacheExpiredPurges,
        sessions: [...sessions.keys()],
      };
    },
    close() {
      return closing ??= (async () => {
        closed = true; invalidate();
        // Dispatching an upstream mutation is not a local durable commit. Shutdown
        // cancels its transport too; the operation preserves its unknown receipt.
        const draining = [...active, ...flightEntries];
        for (const entry of draining) entry.controller.abort(new MusicPlatformError('CLOSED'));
        const clientDrain = clients.close();
        const transportDrain = transport.close();
        await Promise.allSettled(draining.map(entry => entry.work));
        await Promise.allSettled([clientDrain, transportDrain]);
        accounts.close(); mutations.close();
        sessions.clear(); xeapiState = {};
      })();
    },
  };
}
