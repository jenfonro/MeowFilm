import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createContext, runInContext } from 'node:vm';

// Run the existing platform-neutral program once without installing its entry
// point on Node's global object. Its expression caches retain one ruleset/group.
const context = createContext({});
const require = createRequire(import.meta.url);
runInContext(readFileSync(require.resolve('@meowfilm/film-rules'), 'utf8'), context);
const processRules = context.meowfilmProcess;
const defaults = processRules({ operation: 'initialize', document: null }).rules;
const groups = ['episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules'];

function invalidRules() {
  throw Object.assign(new Error('Invalid film recognition rules'), { code: 'INVALID_RULES' });
}

function normalizedRules(value) {
  if (value === undefined) return defaults;
  if (!value || typeof value !== 'object' || Array.isArray(value)) invalidRules();
  const rules = {};
  for (const group of groups) {
    const rows = Object.hasOwn(value, group) ? value[group] : defaults[group];
    if (!Array.isArray(rows)) invalidRules();
    const matching = group === 'episodeRules' || group === 'movieRules';
    if (rows.some(row => matching
      ? !row || typeof row !== 'object' || typeof row.pattern !== 'string' ||
        row.replace !== undefined && typeof row.replace !== 'string' ||
        row.flags !== undefined && typeof row.flags !== 'string'
      : typeof row !== 'string')) invalidRules();
    rules[group] = rows;
  }
  return rules;
}

function seasonNumber(text) {
  const match = /(?:^|[^a-z0-9])s(?:eason)?\s*0*(\d+)(?=e|\b|[^a-z0-9])/i.exec(text) ??
    /第\s*(\d+)\s*(?:季|部|篇)/.exec(text);
  const value = match ? Number(match[1]) : NaN;
  return Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

function episodeNumber(text) {
  const match = /s\s*\d+\s*e(?:p)?\s*0*(\d+)(?!\d)/i.exec(text) ??
    /(?:^|[^a-z0-9])e(?:p)?\s*0*(\d+)(?!\d)/i.exec(text);
  const value = match ? Number(match[1]) : NaN;
  return Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

export function createRecognitionRules(value) {
  const rules = normalizedRules(value);
  function process(section, text, query) {
    return processRules({ operation: 'process', rules, request: { section, text, query } });
  }
  return {
    episode(text) {
      const result = process('episode', text);
      const output = result.rewrittenText ?? result.processedText;
      const episode = episodeNumber(output);
      const season = seasonNumber(output) ?? seasonNumber(result.processedText);
      return { ...(season === undefined ? {} : { season }), ...(episode === undefined ? {} : { episode }) };
    },
    season(text) {
      return seasonNumber(process('episode', text).processedText);
    },
    movie(text) {
      return process('movie', text).matched;
    },
    search(text, query = text) {
      const result = process('search', text, query);
      if (result.errors.length) invalidRules();
      return { text: result.processedText, key: result.groupKey };
    },
  };
}

export function providerFamily(provider) {
  switch (provider) {
    case 'quark_tv': return 'quark';
    case 'uc_tv': return 'uc';
    case 'yidong':
    case 'tianyi':
    case 'quark':
    case 'uc':
    case 'baidu': return provider;
    default: return undefined;
  }
}

export function qualityKey(value) {
  if (typeof value !== 'string') return '';
  const text = value.trim().toLowerCase();
  if (/^4k(?:[· _-]?hdr)$/.test(text)) return '4k_hdr';
  if (/^4k(?:[· _-]?(?:fps|60(?:fps|帧)?))$/.test(text)) return '4k_fps';
  if (['4k', '2160p', 'uhd'].includes(text)) return '4k';
  if (['1080p', '1080i', 'fhd'].includes(text)) return '1080p';
  if (['720p', 'hd'].includes(text)) return '720p';
  return '';
}

// Only observed labels/metadata contribute quality. These are resource labels,
// not a claim about the codec, HDR output or resolution of a later play URL.
export function recognizeQuality(record, texts) {
  const explicit = qualityKey(record.quality);
  if (!explicit && typeof record.quality === 'string' && record.quality.trim()) return record.quality.trim();
  const text = texts.filter(value => typeof value === 'string').join(' ');
  let quality = explicit;
  if (!quality) {
    // Callers supply filename, display name and actual path in that order.
    // A parent directory must not override a more specific file marker.
    for (const value of texts) {
      if (typeof value !== 'string') continue;
      if (/(?:^|[^a-z0-9])(?:8k|4320p)(?=$|[^a-z0-9])/i.test(value)) quality = '8K';
      else if (/(?:^|[^a-z0-9])(?:4k|2160p|uhd)(?=$|[^a-z0-9])/i.test(value)) quality = '4k';
      else if (/(?:^|[^a-z0-9])(?:1080[pi]|fhd)(?=$|[^a-z0-9])/i.test(value)) quality = '1080p';
      else if (/(?:^|[^a-z0-9])720p(?=$|[^a-z0-9])/i.test(value)) quality = '720p';
      if (quality) break;
    }
  }
  if (quality === '4k') {
    if (record.hdr === true || record.hdr !== false &&
      /(?:^|[^a-z0-9])(?:hdr(?:10\+?)?|dv)(?=$|[^a-z0-9])|dolby[ ._-]*vision|杜比视界|臻彩/i.test(text)) {
      return '4k_hdr';
    }
    if (Number(record.fps) >= 59 || record.fps === undefined &&
      /(?:^|[^a-z0-9])60\s*(?:fps|帧)(?=$|[^a-z0-9])/i.test(text)) return '4k_fps';
  }
  return quality;
}
