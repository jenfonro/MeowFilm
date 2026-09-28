import { normalizeString } from './normalize';

const FULLWIDTH_DIGITS = '０１２３４５６７８９';

const normalizeDigitsText = (value) =>
  normalizeString(value).replace(/[０-９]/g, (ch) => String(FULLWIDTH_DIGITS.indexOf(ch)));

const normalizeChineseNumberToken = (value) =>
  normalizeDigitsText(value)
    .replace(/\s+/g, '')
    .replace(/两/g, '二')
    .replace(/〇/g, '零');

export const parseChineseNumeralToInt = (raw) => {
  const token = normalizeChineseNumberToken(raw);
  if (!token) return 0;
  if (/^\d+$/.test(token)) return Number.parseInt(token, 10) || 0;

  const digit = (ch) => ({
    零: 0,
    一: 1,
    二: 2,
    三: 3,
    四: 4,
    五: 5,
    六: 6,
    七: 7,
    八: 8,
    九: 9,
  }[ch] ?? -1);

  const parseSection = (section) => {
    let total = 0;
    let num = 0;
    for (let i = 0; i < section.length; i += 1) {
      const ch = section[i];
      const d = digit(ch);
      if (d >= 0) {
        num = d;
        continue;
      }
      const unit = ch === '十' ? 10 : ch === '百' ? 100 : ch === '千' ? 1000 : 0;
      if (!unit) continue;
      if (!num) num = 1;
      total += num * unit;
      num = 0;
    }
    return total + num;
  };

  if (token.includes('万')) {
    const [left, right] = token.split('万');
    return parseSection(left || '') * 10000 + parseSection(right || '');
  }
  return parseSection(token);
};

const parseMarkerNumberToInt = (raw) => {
  return parseChineseNumeralToInt(raw);
};

const MARKER_NUMBER_PATTERN = '([0-9０-９]{1,5}|[一二三四五六七八九十百千万两零〇]{1,20})';
const SEASON_EPISODE_MARKER_RE = new RegExp(`第\\s*${MARKER_NUMBER_PATTERN}\\s*([季集话部篇])`, 'g');

export const normalizeSeasonEpisodeMarkers = (text) => {
  const raw = normalizeString(text);
  if (!raw) return '';
  return raw.replace(SEASON_EPISODE_MARKER_RE, (full, token, unit) => {
    const value = parseMarkerNumberToInt(token);
    if (value <= 0) return full;
    return `第${value}${unit}`;
  });
};
