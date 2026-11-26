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
        if (Array.isArray(list))
          return atOrigin({ protocol, fields: {} }, origin);
      } else if (object(data) && Object.values(data).some(item => object(item) &&
        (typeof item.cookie === 'string' || typeof item.account === 'string' ||
          typeof item.authorization === 'string'))) {
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
  const providerKey = provider === '189' ? 'tianyi' : provider === '139' ? 'yidong' : provider;
  if (descriptor.protocol === 'unknown') return { status: 'unsupported' };
  const modern = descriptor.protocol === 'website-api-v1';
  const base = (modern ? newPaths : oldPaths)[providerKey];
  const rules = base ? [base] : [];
  if (providerKey === 'tianyi') rules.push(...(modern ? [
    ['/website/api/pan189/cookie', 'cookie', 'pan189'],
    ['/website/api/credential/pan189/cookie', 'value', 'pan189'],
  ] : [['/website/tianyi/cookie', 'cookie']]));
  // These routes are optional protocol extensions. Only a real compatible GET
  // and advertised field can grant authority to send Authorization to them.
  if (providerKey === 'yidong') rules.push(...(modern ? [
    ['/website/api/pan139/authorization', 'authorization', 'pan139'],
    ['/website/api/credential/yidong/authorization', 'value', 'yidong'],
    ['/website/api/credential/139/authorization', 'value', '139'],
  ] : [['/website/yidong/authorization', 'authorization'],
    ['/website/139/authorization', 'authorization']]));
  const clear = Object.values(credentials).every(value => value === '' || value === 0);
  const saved = new Set();
  for (const [path, field, descriptorKey] of rules) {
    const account = ['account', 'username'].includes(field);
    const source = account ? ['username', 'password'] : [providerKey === 'yidong' ? 'authorization' : 'cookie'];
    const group = source.join(':');
    if (saved.has(group)) continue;
    if (!clear && !source.every(name => typeof credentials[name] === 'string' && credentials[name].trim())) continue;
    const required = account ? ['account', 'password'] : source;
    if (descriptorKey && !required.every(name => descriptor.fields[descriptorKey]?.includes(name))) continue;
    // Standard modern credential metadata declares its fixed native routes.
    // Optional extensions and legacy resolver lists still need a route probe.
    if (!descriptorKey || providerKey === 'yidong' || providerKey === 'tianyi' && !account) {
      let probe;
      try {
        probe = await session.management(path, {}, 'GET',
          { origin: descriptor[managementOrigin], ...(signal ? { signal } : {}) });
      } catch (error) {
        if (signal?.aborted) throw signal.reason ?? fault('CANCELLED');
        continue;
      }
      if (!successfulManagement(probe) || !(account
        ? object(probe.data) && source.every(name => typeof probe.data[name] === 'string')
        : typeof probe.data === 'string' || providerKey === 'yidong' &&
          object(probe.data) && typeof probe.data.authorization === 'string')) continue;
    }
    signal?.throwIfAborted();
    const body = account ? { [field]: clear ? '' : credentials.username, password: clear ? '' : credentials.password }
      : { [field]: clear ? '' : credentials[source[0]] };
    // Empty fields are an actual clear request; match CatPawRunner HTTP management receipt semantics
    // without blocking on synchronous fs flushes (scripts may short-circuit identical values or debounce writes).
    const response = await session.management(path, body, 'PUT', {
      origin: descriptor[managementOrigin], ...(signal ? { signal } : {}),
    });
    if (!successfulManagement(response) || response.sms || response.status === 'waiting' || response.skipped)
      throw fault('STORAGE_ERROR');
    saved.add(group);
  }
  return saved.size ? { status: 'saved', protocol: descriptor.protocol } : { status: 'unsupported' };
}
