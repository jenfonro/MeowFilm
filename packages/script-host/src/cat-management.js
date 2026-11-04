import { successfulManagement } from './cat-session.js';
import { fault, requireValue } from './errors.js';

const oldPaths = {
  baidu: ['/website/baidu/cookie', 'cookie'], quark: ['/website/quark/cookie', 'cookie'],
  uc: ['/website/uc/cookie', 'cookie'], '115': ['/website/115/cookie', 'cookie'],
  tianyi: ['/website/tianyi/account', 'username'], pan123: ['/website/pan123/account', 'username'],
  bili: ['/website/bili/cookie', 'cookie'], wuming: ['/website/wuming/cookie', 'cookie'],
  yunchao: ['/website/yunchao/account', 'username'],
  pan123ziyuan: ['/website/pan123ziyuan/cookie', 'cookie'],
};
const newPaths = {
  baidu: ['/website/api/credential/baidu/cookie', 'value', 'baidu'],
  quark: ['/website/api/credential/quark/cookie', 'value', 'quark'],
  uc: ['/website/api/credential/uc/cookie', 'value', 'uc'],
  '115': ['/website/api/pan115/cookie', 'value', 'pan115'],
  tianyi: ['/website/api/pan189/account', 'account', 'pan189'],
  pan123: ['/website/api/pan123/account', 'account', 'pan123'],
  bili: ['/website/api/bili/cookie', 'cookie', 'bili'],
  pan123ziyuan: ['/website/api/credential/pan123ziyuan/cookie', 'value', 'pan123ziyuan'],
};
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const managementOrigin = Symbol('cat-management-origin');
const atOrigin = (descriptor, origin) =>
  Object.defineProperty(descriptor, managementOrigin, { value: origin });
export async function detectCatManagement(session, { signal = null } = {}) {
  const origins = [...new Set([session.services.endpoint().origin,
    ...session.services.endpoints().map(endpoint => endpoint.origin)])];
  for (const origin of origins) {
    for (const [protocol, path] of [['website-api-v1', '/website/api/credentials'],
      ['website-v1', '/website/pans/list']]) {
      signal?.throwIfAborted();
      let response;
      try {
        response = await session.management(path, {}, 'GET', {
          origin, ...(signal ? { signal } : {}),
        });
      } catch (error) {
        if (signal?.aborted) throw signal.reason ?? fault('CANCELLED');
        continue;
      }
      if (!successfulManagement(response)) continue;
      const data = response.data;
      if (protocol === 'website-v1') {
        const list = Array.isArray(data) ? data : data?.list;
        if (Array.isArray(list) && list.every(item => object(item) && typeof item.key === 'string'))
          return atOrigin({ protocol, fields: {} }, origin);
      } else if (object(data) && Object.values(data).some(item => object(item) &&
        (typeof item.cookie === 'string' || typeof item.account === 'string'))) {
        return atOrigin({ protocol,
          fields: Object.fromEntries(Object.entries(data).filter(([, value]) => object(value))
            .map(([key, value]) => [key, Object.keys(value)
              .filter(field => typeof value[field] === 'string')])) }, origin);
      }
    }
  }
  return { protocol: 'unknown', fields: {} };
}

export async function syncCatManagement(session, { provider, credentials }, { signal = null } = {}) {
  requireValue(object(credentials));
  const descriptor = await detectCatManagement(session, { signal });
  const providerKey = provider === '189' ? 'tianyi' : provider;
  const rule = (descriptor.protocol === 'website-api-v1' ? newPaths : oldPaths)[providerKey];
  if (!rule || descriptor.protocol === 'unknown') return { status: 'unsupported' };
  const [path, field, descriptorKey] = rule;
  const account = ['account', 'username'].includes(field);
  const source = account ? ['username', 'password'] : ['cookie'];
  if (!source.every(name => typeof credentials[name] === 'string' && credentials[name].trim()))
    return { status: 'empty' };
  if (descriptorKey) {
    const required = account ? ['account', 'password'] : ['cookie'];
    if (!required.every(name => descriptor.fields[descriptorKey]?.includes(name))) return { status: 'unsupported' };
  } else {
    let probe;
    try {
      probe = await session.management(path, {}, 'GET',
        { origin: descriptor[managementOrigin], ...(signal ? { signal } : {}) });
    } catch (error) {
      if (signal?.aborted) throw signal.reason ?? fault('CANCELLED');
      return { status: 'unsupported' };
    }
    if (!successfulManagement(probe) || !(account
      ? object(probe.data) && source.every(name => typeof probe.data[name] === 'string')
      : typeof probe.data === 'string')) return { status: 'unsupported' };
  }
  signal?.throwIfAborted();
  const body = account ? { [field]: credentials.username, password: credentials.password }
    : { [field]: credentials.cookie };
  // The SDK requires durable writes before confirming this save. Values and
  // arbitrary source response messages are never retained in the descriptor.
  const response = await session.management(path, body, 'PUT', {
    requireWrite: true, origin: descriptor[managementOrigin], ...(signal ? { signal } : {}),
  });
  if (!successfulManagement(response)) throw fault('STORAGE_ERROR');
  return { status: 'saved', protocol: descriptor.protocol };
}
