import { sourceClone, sourceFault, sourceObject, sourceRequire } from './errors.js';

const legadoMarkers = ['ruleSearch', 'ruleBookInfo', 'ruleToc', 'ruleContent'];

function remoteHtmlList(value) {
  if (typeof value !== 'string') return false;
  const entries = value.trim().split(/\s+/).filter(Boolean);
  return entries.length > 0 && entries.every(entry => {
    try {
      const url = new URL(entry);
      return ['http:', 'https:'].includes(url.protocol);
    } catch {
      return false;
    }
  });
}

function qysgSource(source) {
  if (typeof source.html !== 'string') return false;
  if (/<script\b/i.test(source.html)) return true;
  return typeof source.bookSourceName === 'string' && source.bookSourceName.trim().length > 0 &&
    typeof source.bookSourceUrl === 'string' && source.bookSourceUrl.trim().length > 0 &&
    remoteHtmlList(source.html);
}

function embeddedText(input) {
  if (typeof input !== 'string') return null;
  try {
    const value = JSON.parse(input);
    return typeof value === 'string' ? value : null;
  } catch {
    return input;
  }
}

function parseSource(input) {
  let value = input;
  if (typeof value === 'string') {
    try { value = JSON.parse(value); } catch { throw sourceFault('INVALID_ARGUMENT'); }
  }
  if (Array.isArray(value)) {
    sourceRequire(value.length === 1);
    [value] = value;
  }
  return sourceClone(sourceObject(value));
}

export function detectSourceProtocol(input) {
  const raw = embeddedText(input);
  if (raw !== null) {
    const source = raw.trim();
    sourceRequire(source.length > 0);
    if (source.startsWith('<') && /<script\b/i.test(source)) return 'qysg';
    return 'cat';
  }
  const source = parseSource(input);
  if (typeof source.html === 'string' && /<script\b/i.test(source.html)) return 'qysg';
  if (legadoMarkers.some(key => source[key] !== undefined)) return 'legado';
  if (qysgSource(source)) return 'qysg';
  if (typeof source.script === 'string' || typeof source.source === 'string') return 'cat';
  throw sourceFault('UNSUPPORTED_PROTOCOL');
}

export function normalizeSourceDefinition(input, protocol) {
  const raw = embeddedText(input);
  if (raw !== null) {
    const source = raw.trim();
    sourceRequire(source.length > 0);
    if ((protocol === undefined || protocol === 'qysg') &&
        source.startsWith('<') && /<script\b/i.test(source)) {
      return { protocol: 'qysg', source: { html: raw }, importedProtocol: 'qysg' };
    }
    if (protocol === undefined || protocol === 'cat') {
      return { protocol: 'cat', source: { script: raw }, importedProtocol: 'cat' };
    }
  }
  const detected = protocol ?? detectSourceProtocol(input);
  if (detected === 'legado' || detected === 'qysg') {
    return { protocol: detected, source: parseSource(input), importedProtocol: detected };
  }
  if (detected === 'cat') {
    const source = parseSource(input);
    return {
      protocol: 'cat',
      source: {
        ...source,
        script: typeof source.script === 'string' ? source.script : source.source,
      },
      importedProtocol: 'cat',
    };
  }
  throw sourceFault('UNSUPPORTED_PROTOCOL');
}
