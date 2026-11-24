import { fault, identifier, requireValue } from './errors.js';

const headerPattern = /^\s*(\/\*[\s\S]*?\*\/|(?:\/\/[^\n]*\n?)+)/;
const headerFields = ['name', 'version', 'author', 'description', 'homepage'];

/** Leading LX comment metadata (`@name`, `@version`, `@author`, `@description`, `@homepage`). */
export function parseLXHeader(source) {
  const header = headerPattern.exec(source)?.[0] ?? '';
  const result = {};
  for (const field of headerFields) {
    const match = new RegExp(`(?:^|\\n)\\s*(?:\\/\\/|\\/\\*|\\*)?\\s*@${field}\\s+([^\\r\\n]*)`).exec(header);
    result[field] = match ? match[1].replaceAll('*/', '').trim() : '';
  }
  return result;
}

// A declared action is a guest request, not an extra host capability.

export function lxCapabilities(data) {
  const valid = (value) => requireValue(value, 'SCRIPT_PROTOCOL_ERROR');
  valid(data && typeof data === 'object' && data.status !== false);
  valid(data.sources && typeof data.sources === 'object' && !Array.isArray(data.sources));
  valid(Object.keys(data.sources).length > 0);
  const result = Object.create(null);
  for (const key of Object.keys(data.sources)) {
    identifier(key);
    const item = data.sources[key];
    valid(item && typeof item === 'object');
    if (item.type !== 'music') continue;
    valid(Array.isArray(item.actions));
    valid(Array.isArray(item.qualitys));
    valid(item.name === undefined || typeof item.name === 'string');
    valid(item.actions.every(action => typeof action === 'string' && identifier(action)));
    const declaredQualities = item.qualitys.map(quality => typeof quality === 'object' && quality !== null ? quality.type : quality);
    valid(declaredQualities.every(quality => typeof quality === 'string' && quality.length > 0 &&
      quality.length <= 64 && !/[\x00-\x1f\x7f]/.test(quality)));
    result[key] = {
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
