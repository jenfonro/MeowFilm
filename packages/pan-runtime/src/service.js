import { randomUUID } from 'node:crypto';
import { PanError, requireValue } from './errors.js';
import { createTransport } from './http.js';
import { PanStore } from './store.js';
import { baidu } from './providers/baidu.js';
import { tianyi } from './providers/tianyi.js';
import { yidong } from './providers/yidong.js';
import { quark, uc, quarkTV, ucTV } from './providers/quark-uc.js';

const providers = new Map([baidu, quark, uc, tianyi, yidong, quarkTV, ucTV].map(value => [value.id, value]));
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value) &&
  [Object.prototype, null].includes(Object.getPrototypeOf(value));
const clone = value => structuredClone(value);
const integer = value => Number.isSafeInteger(value) && value >= 0;
const stable = value => JSON.stringify(value, (_key, item) => object(item)
  ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);
function fields(value, allowed, required = allowed) {
  requireValue(object(value) && Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)));
}
function credentialValue(provider, value) {
  requireValue(object(value) && Object.keys(value).every(key => Object.hasOwn(provider.credentialFields, key)));
  for (const [key, item] of Object.entries(value)) {
    requireValue(provider.credentialFields[key] === 'number'
      ? integer(item) : typeof item === 'string' && !item.includes('\0'));
    // Passwords keep their exact text; header credentials cannot contain newlines.
    if (key !== 'password' && typeof item === 'string') requireValue(!/[\r\n]/.test(item));
  }
  return clone(value);
}
function select(id) {
  requireValue(typeof id === 'string' && providers.has(id));
  return providers.get(id);
}
function failure(error) {
  return error instanceof PanError ? error : new PanError('PAN_FAILURE');
}
function capabilities(provider) {
  return {
    provider: provider.id, credentialFields: Object.keys(provider.credentialFields),
    list: typeof provider.list === 'function', prepare: typeof provider.prepare === 'function',
    modes: [...provider.modes], login: typeof provider.loginStart === 'function',
    refresh: typeof provider.refresh === 'function',
  };
}

/** One in-process owner. Neither script engines nor platform credential bridges are required. */
export function createPanService({ dataRoot, transport, now = Date.now } = {}) {
  requireValue(typeof now === 'function' && (transport === undefined ||
    typeof transport?.request === 'function' && typeof transport?.close === 'function'));
  const store = new PanStore(dataRoot);
  let document;
  try {
    document = store.read();
    fields(document, ['version', 'accounts']);
    requireValue(document.version === 1 && object(document.accounts));
    for (const [id, record] of Object.entries(document.accounts)) {
      fields(record, ['revision', 'credentials']);
      requireValue(integer(record.revision) && record.revision > 0);
      credentialValue(select(id), record.credentials);
    }
  } catch (error) {
    throw error?.code === 'STORAGE_ERROR' ? error : new PanError('CORRUPT_STORAGE');
  }
  const http = transport ?? createTransport();
  const accounts = new Map([...providers.keys()].map(id => [id, {
    revision: document.accounts[id]?.revision ?? 0,
    credentials: clone(document.accounts[id]?.credentials ?? {}),
  }]));
  const pending = new Set(), controllers = new Set(), shared = new Map(), logins = new Map(), loginStarts = new Map();
  let closed = false, closing;

  function assertCurrent(id, account, signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(accounts.get(id) === account, 'STALE_ACCOUNT');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  function accountStatus(id) {
    const account = accounts.get(id);
    const configured = Object.entries(account.credentials).filter(([, value]) => value !== '' && value !== 0)
      .map(([name]) => name);
    return { provider: id, revision: account.revision, configured: configured.length > 0, fields: configured };
  }
  function persist(id, next) {
    const nextDocument = { version: 1, accounts: { ...document.accounts,
      [id]: { revision: next.revision, credentials: clone(next.credentials) } } };
    store.write(nextDocument);
    document = nextDocument;
  }
  function replace(id, credentials) {
    const old = accounts.get(id);
    requireValue(Number.isSafeInteger(old.revision + 1), 'STORAGE_ERROR');
    const next = { revision: old.revision + 1, credentials };
    persist(id, next);
    accounts.set(id, next);
    for (const owner of controllers) if (owner.id === id && owner.account === old) owner.controller.abort();
    for (const login of logins.values()) {
      if (login.provider.id === id && login.account === old && login.status !== 'confirmed') {
        cancelLogin(login, 'cancelled');
      }
    }
    return accountStatus(id);
  }
  function track(promise) {
    pending.add(promise);
    promise.then(() => pending.delete(promise), () => pending.delete(promise));
    return promise;
  }
  function scope(id, account, external) {
    const controller = new AbortController();
    const cancel = () => controller.abort();
    const owner = { id, account, controller };
    external?.addEventListener('abort', cancel, { once: true });
    if (external?.aborted || closed) controller.abort();
    controllers.add(owner);
    return { signal: controller.signal, controller, dispose() {
      external?.removeEventListener('abort', cancel);
      controllers.delete(owner);
    } };
  }
  function context(provider, account, signal) {
    const id = provider.id;
    return {
      signal, http, now,
      get credentials() { assertCurrent(id, account, signal); return clone(account.credentials); },
      async updateCredentials(patch) {
        assertCurrent(id, account, signal);
        const credentials = credentialValue(provider, { ...account.credentials, ...patch });
        persist(id, { revision: account.revision, credentials });
        account.credentials = credentials;
      },
      async refresh() {
        requireValue(typeof provider.refresh === 'function', 'UNSUPPORTED_OPERATION');
        await together(provider, account, 'refresh', signal, async ctx => provider.refresh({}, ctx));
        assertCurrent(id, account, signal);
        return clone(account.credentials);
      },
      async prepare(file) {
        requireValue(typeof provider.prepare === 'function', 'UNSUPPORTED_OPERATION');
        const result = await together(provider, account, 'prepare:' + stable(file), signal,
          ctx => provider.prepare({ file }, ctx));
        assertCurrent(id, account, signal);
        requireValue(object(result?.file) && result.file.kind === 'stored', 'UPSTREAM_FORMAT');
        return stamp(provider.id, account, result.file);
      },
    };
  }
  function together(provider, account, key, signal, run) {
    assertCurrent(provider.id, account, signal);
    const identity = `${provider.id}:${account.revision}:${key}`;
    let work = shared.get(identity);
    // QR polls mutate one temporary cookie session. Drain a cancelled poll before reusing that session.
    if (work?.task.signal.aborted && key.startsWith('login:')) {
      return work.promise.catch(() => {}).then(() => together(provider, account, key, signal, run));
    }
    // A cancelled task may still be physically draining. New callers must not join it.
    if (work?.task.signal.aborted) work = undefined;
    if (!work) {
      const task = scope(provider.id, account);
      work = { task, waiters: new Set() };
      shared.set(identity, work);
      work.promise = track(Promise.resolve().then(() => {
        assertCurrent(provider.id, account, task.signal);
        return run(context(provider, account, task.signal));
      }).finally(() => {
        task.dispose();
        if (shared.get(identity) === work) shared.delete(identity);
      }));
      work.promise.catch(() => {});
    }
    return new Promise((resolve, reject) => {
      const waiter = {};
      work.waiters.add(waiter);
      let settled = false;
      const finish = (error, value) => {
        if (settled) return;
        settled = true;
        signal?.removeEventListener('abort', cancel);
        work.waiters.delete(waiter);
        if (error) reject(error); else resolve(clone(value));
      };
      const cancel = () => {
        finish(new PanError(closed ? 'CLOSED' : accounts.get(provider.id) !== account ? 'STALE_ACCOUNT' : 'CANCELLED'));
        if (!work.waiters.size) work.task.controller.abort();
      };
      signal?.addEventListener('abort', cancel, { once: true });
      work.promise.then(value => finish(null, value), error => finish(failure(error)));
      if (signal?.aborted) cancel();
    });
  }
  function stamp(id, account, file) {
    return { ...clone(file), provider: id, accountRevision: account.revision };
  }
  function fileInput(provider, account, file) {
    requireValue(object(file) && ['share', 'stored'].includes(file.kind) &&
      typeof file.provider === 'string' && integer(file.accountRevision));
    const base = ({ quark_tv: 'quark', uc_tv: 'uc' })[provider.id];
    requireValue(file.provider === provider.id || base === file.provider);
    const sourceAccount = accounts.get(file.provider);
    requireValue(sourceAccount?.revision === file.accountRevision, 'STALE_ACCOUNT');
    if (base) requireValue(file.kind === 'stored');
    else requireValue(account.revision === file.accountRevision, 'STALE_ACCOUNT');
    return clone(file);
  }
  function cancelLogin(login, status) {
    login.status = status;
    login.controller.abort();
    if (login.expiryTimer) clearTimeout(login.expiryTimer);
    login.expiryTimer = undefined;
    if (!login.polling && login.session) {
      login.provider.loginCancel?.(login.session);
      login.session = undefined;
    }
  }
  function pruneLogins() {
    for (const [id, login] of logins) if (now() >= login.expiresAt) {
      cancelLogin(login, 'expired');
      logins.delete(id);
    }
  }
  async function startLogin(provider, account, ctx) {
    requireValue(typeof provider.loginStart === 'function', 'UNSUPPORTED_OPERATION');
    loginStarts.get(provider.id)?.controller.abort();
    const startup = scope(provider.id, account, ctx.signal);
    loginStarts.set(provider.id, startup);
    for (const [id, old] of logins) if (old.provider.id === provider.id) {
      cancelLogin(old, 'cancelled');
      logins.delete(id);
    }
    let value;
    try {
      value = await provider.loginStart({}, context(provider, account, startup.signal));
      assertCurrent(provider.id, account, startup.signal);
      requireValue(object(value) && object(value.challenge) && Number.isSafeInteger(value.expiresAt) &&
        value.expiresAt > now() && value.session, 'UPSTREAM_FORMAT');
      const sessionId = randomUUID();
      const login = { provider, account, session: value.session, expiresAt: value.expiresAt,
        status: 'pending', controller: new AbortController() };
      // The upstream challenge lifetime is a real resource boundary, not a polling loop.
      login.expiryTimer = setTimeout(() => {
        cancelLogin(login, 'expired');
        logins.delete(sessionId);
      }, value.expiresAt - now());
      login.expiryTimer.unref?.();
      logins.set(sessionId, login);
      return { sessionId, provider: provider.id, challenge: clone(value.challenge), expiresAt: value.expiresAt,
        status: 'pending' };
    } catch (error) { if (value?.session) provider.loginCancel?.(value.session); throw error; }
    finally {
      startup.dispose();
      if (loginStarts.get(provider.id) === startup) loginStarts.delete(provider.id);
    }
  }
  async function pollLogin(login, sessionId, signal) {
    if (login.status === 'confirmed') return { sessionId, provider: login.provider.id,
      status: accounts.get(login.provider.id) === login.account ? 'confirmed' : 'cancelled',
      ...(accounts.get(login.provider.id) === login.account ? { account: accountStatus(login.provider.id) } : {}) };
    if (['cancelled', 'expired'].includes(login.status)) return { sessionId, status: login.status };
    assertCurrent(login.provider.id, login.account, signal);
    const result = await together(login.provider, login.account, 'login:' + sessionId, signal, async ctx => {
      if (['cancelled', 'expired'].includes(login.status)) return { sessionId, status: login.status };
      const operation = scope(login.provider.id, login.account, ctx.signal);
      const cancel = () => operation.controller.abort();
      login.controller.signal.addEventListener('abort', cancel, { once: true });
      if (login.controller.signal.aborted) operation.controller.abort();
      login.polling = true;
      try {
        const response = await login.provider.loginPoll(login.session,
          context(login.provider, login.account, operation.signal));
        assertCurrent(login.provider.id, login.account, operation.signal);
        requireValue(object(response) &&
          ['pending', 'scanned', 'confirmed', 'expired'].includes(response.status), 'UPSTREAM_FORMAT');
        if (response.status === 'confirmed') {
          const credentials = credentialValue(login.provider, response.credentials);
          // The completed session remains queryable; replacement revokes all *other* unfinished sessions.
          const prior = login.status;
          login.status = 'confirmed';
          try { replace(login.provider.id, credentials); }
          catch (error) { login.status = prior; throw error; }
          login.account = accounts.get(login.provider.id);
          cancelLogin(login, 'confirmed');
        } else if (response.status === 'expired') cancelLogin(login, 'expired');
        else login.status = response.status;
        return { sessionId, provider: login.provider.id, status: login.status,
          ...(login.status === 'confirmed' ? { account: accountStatus(login.provider.id) } : {}) };
      } finally {
        login.polling = false;
        operation.dispose();
        login.controller.signal.removeEventListener('abort', cancel);
        if (login.session && ['confirmed', 'cancelled', 'expired'].includes(login.status)) {
          login.provider.loginCancel?.(login.session);
          login.session = undefined;
        }
      }
    });
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    if (result.status === 'confirmed') requireValue(accounts.get(login.provider.id) === login.account, 'STALE_ACCOUNT');
    else assertCurrent(login.provider.id, login.account, signal);
    return result;
  }
  async function dispatch(operation, params, signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    pruneLogins();
    if (operation === 'providers') { fields(params, []); return { providers: [...providers.values()].map(capabilities) }; }
    if (operation === 'settings.get') {
      fields(params, []);
      return { accounts: [...providers.keys()].map(accountStatus) };
    }
    if (operation === 'settings.set' || operation === 'settings.clear') {
      fields(params, operation === 'settings.set' ? ['provider', 'credentials'] : ['provider']);
      const provider = select(params.provider);
      const credentials = operation === 'settings.set' ? credentialValue(provider, params.credentials) : {};
      return replace(provider.id, credentials);
    }
    if (operation === 'login.poll' || operation === 'login.cancel') {
      fields(params, ['sessionId']);
      requireValue(typeof params.sessionId === 'string');
      const login = logins.get(params.sessionId);
      requireValue(login, 'LOGIN_EXPIRED');
      if (operation === 'login.cancel') {
        if (login.status !== 'confirmed') cancelLogin(login, 'cancelled');
        return { sessionId: params.sessionId, status: login.status };
      }
      return pollLogin(login, params.sessionId, signal);
    }
    requireValue(['list', 'prepare', 'play', 'refresh', 'login.start'].includes(operation), 'UNSUPPORTED_OPERATION');
    const expected = ({ list: ['provider', 'share'], prepare: ['provider', 'file'],
      play: ['provider', 'file', 'mode'], refresh: ['provider'], 'login.start': ['provider'] })[operation];
    fields(params, expected);
    const provider = select(params.provider), account = accounts.get(provider.id);
    const owner = scope(provider.id, account, signal);
    let sourceOwner;
    try {
      const ctx = context(provider, account, owner.signal);
      let result;
      if (operation === 'list') {
        requireValue(typeof provider.list === 'function', 'UNSUPPORTED_OPERATION');
        fields(params.share, ['url', 'password'], ['url']);
        requireValue(typeof params.share.url === 'string' && params.share.url.length > 0 &&
          (params.share.password === undefined || typeof params.share.password === 'string'));
        result = await provider.list({ share: clone(params.share) }, ctx);
        requireValue(object(result?.share) && Array.isArray(result.files), 'UPSTREAM_FORMAT');
        result = { ...result, files: result.files.map(item => {
          requireValue(object(item) && typeof item.name === 'string' && typeof item.path === 'string' &&
            object(item.file) && item.file.kind === 'share', 'UPSTREAM_FORMAT');
          return { ...item, file: stamp(provider.id, account, item.file) };
        }) };
      } else if (operation === 'refresh') {
        await ctx.refresh();
        result = { refreshed: true, account: accountStatus(provider.id) };
      } else if (operation === 'login.start') result = await startLogin(provider, account, ctx);
      else {
        const file = fileInput(provider, account, params.file);
        if (file.provider !== provider.id) {
          sourceOwner = { id: file.provider, account: accounts.get(file.provider), controller: owner.controller };
          controllers.add(sourceOwner);
        }
        if (operation === 'prepare') result = { file: await ctx.prepare(file) };
        else {
          requireValue(provider.modes.includes(params.mode), 'UNSUPPORTED_OPERATION');
          result = await provider.play({ file, mode: params.mode }, ctx);
          let url;
          try { url = new URL(result?.url); } catch { throw new PanError('UPSTREAM_FORMAT'); }
          requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password &&
            object(result.headers) && result.mode === params.mode &&
            Object.entries(result.headers).every(([name, value]) =>
              /^[!#$%&'*+.^_`|~\w-]+$/.test(name) && typeof value === 'string' && !/[\r\n]/.test(value)),
          'UPSTREAM_FORMAT');
          if (result.file) result = { ...result, file: stamp(file.provider, accounts.get(file.provider), result.file) };
          // TV input can belong to the ordinary account; switching it while resolving invalidates the result too.
          fileInput(provider, account, file);
        }
      }
      assertCurrent(provider.id, account, owner.signal);
      return clone(result);
    } finally {
      owner.dispose();
      if (sourceOwner) controllers.delete(sourceOwner);
    }
  }
  return Object.freeze({
    invoke(operation, params = {}, { signal } = {}) {
      return track(Promise.resolve().then(() => {
        requireValue(signal === undefined || signal instanceof AbortSignal);
        return dispatch(operation, params, signal);
      }).catch(error => { throw failure(error); }));
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        for (const owner of controllers) owner.controller.abort();
        for (const login of logins.values()) cancelLogin(login, 'cancelled');
        const results = await Promise.allSettled([Promise.resolve().then(() => http.close()), ...pending]);
        logins.clear();
        shared.clear();
        await store.close();
        if (results[0].status === 'rejected') throw new PanError('PAN_FAILURE');
      })();
    },
  });
}
