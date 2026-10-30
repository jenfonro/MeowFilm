import { fault, identifier, requireValue } from './errors.js';

// A declared action is a guest request, not an extra host capability.

export function lxCapabilities(data) {
  const valid = (value) => requireValue(value, 'SCRIPT_PROTOCOL_ERROR');
  valid(data && typeof data === 'object' && data.status !== false);
  valid(data.sources && typeof data.sources === 'object' && !Array.isArray(data.sources));
  valid(Object.keys(data.sources).length > 0 && Object.keys(data.sources).length <= 32);
  const result = Object.create(null);
  for (const key of Object.keys(data.sources)) {
    identifier(key);
    const item = data.sources[key];
    valid(item && typeof item === 'object');
    if (item.type !== 'music') continue;
    valid(Array.isArray(item.actions) && item.actions.length <= 32);
    valid(Array.isArray(item.qualitys) && item.qualitys.length <= 32);
    valid(item.name === undefined || (typeof item.name === 'string' && item.name.length <= 256));
    valid(item.actions.every(action => typeof action === 'string' && identifier(action)));
    const declaredQualities = item.qualitys.map(quality => typeof quality === 'object' && quality !== null ? quality.type : quality);
    valid(declaredQualities.every(quality => typeof quality === 'string' && quality.length > 0 &&
      quality.length <= 64 && !/[\x00-\x1f\x7f]/.test(quality)));
    result[key] = {
      ...item,
      name: item.name ?? key,
      type: 'music',
      actions: [...new Set(item.actions)],
      qualitys: [...new Set(declaredQualities)],
    };
  }
  valid(Object.keys(result).length > 0);
  return result;
}

export function lxPayload(capabilities, { siteId, operation, payload = {} }) {
  const capability = capabilities[siteId];
  if (!capability?.actions.includes(operation)) throw fault('CAPABILITY_UNAVAILABLE');
  const info = { ...payload };
  if (operation === 'musicUrl') {
    info.type = payload.type ?? payload.quality ?? null;
    delete info.quality;
    info.musicInfo ??= {};
    if (siteId !== 'local' && !capability.qualitys.includes(info.type)) throw fault('CAPABILITY_UNAVAILABLE');
  }
  return {
    source: siteId,
    action: operation,
    info,
  };
}
