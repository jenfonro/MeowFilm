import { fault, requireValue } from './errors.js';

// Protocol-specific vocabulary belongs here, not in the installation manager.
const platforms = ['wy', 'tx', 'kg', 'kw', 'mg', 'local'];
const qualities = ['128k', '192k', '320k', 'flac', 'flac24bit', 'master'];

export function lxCapabilities(data) {
  const valid = (value) => requireValue(value, 'SCRIPT_PROTOCOL_ERROR');
  valid(data && typeof data === 'object' && data.status !== false);
  valid(data.sources && typeof data.sources === 'object' && !Array.isArray(data.sources));
  valid(Object.keys(data.sources).length > 0 && Object.keys(data.sources).length <= 32);
  const result = Object.create(null);
  for (const key of platforms) {
    if (!Object.hasOwn(data.sources, key)) continue;
    const item = data.sources[key];
    valid(item && typeof item === 'object');
    if (item.type !== 'music') continue;
    valid(Array.isArray(item.actions) && item.actions.length <= 32);
    valid(Array.isArray(item.qualitys) && item.qualitys.length <= 32);
    valid(item.name === undefined || (typeof item.name === 'string' && item.name.length <= 256));
    const allowedActions = key === 'local' ? ['musicUrl', 'lyric', 'pic'] : ['musicUrl'];
    result[key] = {
      name: item.name ?? key,
      type: 'music',
      actions: allowedActions.filter((action) => item.actions.includes(action)),
      qualitys: key === 'local' ? [] : qualities.filter((q) => item.qualitys.includes(q)),
    };
  }
  valid(Object.keys(result).length > 0);
  return result;
}

export function lxPayload(capabilities, { siteId, operation, payload = {} }) {
  const capability = capabilities[siteId];
  if (!capability?.actions.includes(operation)) throw fault('CAPABILITY_UNAVAILABLE');
  if (siteId !== 'local' && operation === 'musicUrl' &&
      !capability.qualitys.includes(payload.quality)) throw fault('CAPABILITY_UNAVAILABLE');
  return {
    source: siteId,
    action: operation,
    info: { type: payload.quality, musicInfo: payload.musicInfo ?? {} },
  };
}
