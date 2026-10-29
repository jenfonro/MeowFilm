// All scripts in this file are authored fixtures. No upstream source is loaded.
export function lxScript({
  setup = '',
  handler = 'async ({source,action,info}) => ({source,action,info,config:meow.config})',
  sources = {
    kw: { name: 'Synthetic', type: 'music', actions: ['musicUrl'], qualitys: ['128k', '320k', 'flac'] },
    tx: { name: 'Synthetic', type: 'music', actions: ['musicUrl'], qualitys: ['320k'] },
    local: { name: 'Synthetic', type: 'music', actions: ['musicUrl', 'lyric', 'pic'], qualitys: [] },
  },
} = {}) {
  return `${setup}
lx.on(lx.EVENT_NAMES.request, ${handler});
lx.send(lx.EVENT_NAMES.inited, {status:true,sources:${JSON.stringify(sources)}});
`;
}

export function target(installationId = 'a', siteId = 'kw', musicInfo = {}, extra = {}) {
  return { installationId, siteId, operation: 'musicUrl',
    payload: { quality: '320k', musicInfo }, ...extra };
}

export function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

export const turn = () => new Promise(resolve => setImmediate(resolve));

export function memoryStorage() {
  const values = new Map();
  const calls = [];
  const key = input => JSON.stringify([input.installationId, input.siteId, input.key]);
  const check = input => {
    if (input.signal.aborted || !input.isCurrent()) throw Object.assign(new Error(), { code: 'CANCELLED' });
    calls.push({ installationId: input.installationId, siteId: input.siteId, operation: input.operation });
  };
  return {
    values, calls,
    adapter: {
      get(input) { check(input); return structuredClone(values.get(key(input))); },
      set(input) { check(input); values.set(key(input), structuredClone(input.value)); },
      delete(input) { check(input); values.delete(key(input)); },
    },
  };
}
