export const normalizeRawNameForCompare = (value) =>
  String(value || '').trim().toLowerCase().replace(/\s+/g, ' ');

export const buildEpisodeMatchKey = (displayName, rawName) => {
  const name = normalizeRawNameForCompare(displayName);
  const raw = normalizeRawNameForCompare(rawName);
  if (!name && !raw) return '';
  return `${name}||${raw}`.trim();
};

export const normalizePanMockFlag = (flag) => {
  let s = typeof flag === 'string' ? flag.trim() : '';
  if (!s) return '';
  if (s.startsWith('百度原画-') && s.includes('#')) s = String(s.split('#')[0] || '').trim();
  return s;
};

export const panMockProviderFromFlag = (flag) => {
  const s = normalizePanMockFlag(flag);
  if (!s) return '';
  // pan_mock routing must use fixed flag recognition and must NOT depend on user-config aliases.
  if (!s.includes('-')) return '';
  const head = String((s.split('-')[0] || '')).trim();
  if (!head) return '';
  if (head.includes('百度')) return 'baidu';
  if (head.includes('夸父')) return 'quark';
  if (head.includes('优夕')) return 'uc';
  if (head.includes('天意')) return '189';
  if (head.includes('逸动')) return '139';
  return '';
};

export const guessPreferredPanFromFlag = (flag) => {
  const raw = typeof flag === 'string' ? flag.trim() : '';
  if (!raw) return '';
  if (raw.includes('百度')) return 'baidu';
  if (raw.includes('夸父')) return 'quark';
  return '';
};

export const parseMockPasscodeFromRawName = (rawName) => {
  let t = typeof rawName === 'string' ? rawName.trim() : '';
  if (!t) return '';
  t = t.replace(/\s*\[[^\]]*]\s*$/g, '').trim();
  t = t.replace(/^\s*\[[^\]]*]\s*/g, '').trim();
  if (t.toLowerCase().endsWith('.mp4')) t = t.slice(0, -4);
  if (t.toLowerCase().endsWith('-nopass')) t = t.slice(0, -7).trim();
  if (t.toLowerCase().endsWith('_nopass')) t = t.slice(0, -7).trim();
  t = String(t || '').trim();
  if (!t) return '';
  const lower = t.toLowerCase();
  if (lower === 'nopass' || lower === 'none' || t === '无密码') return '';
  const firstToken = String(t.split(/\s+/)[0] || '').trim();
  return firstToken || '';
};

export const extractTianyiShareCodeAndAccessCode = (flag, rawName) => {
  const label = normalizePanMockFlag(flag);
  const pass = typeof rawName === 'string' ? rawName.trim() : '';
  let shareCode = '';
  if (label) {
    const m = /天意-([A-Za-z0-9]{6,64})/.exec(label);
    if (m && m[1]) shareCode = String(m[1]).trim();
  }
  return { shareCode, accessCode: pass };
};

export const scoreEpisodeDisplayName = (textRaw, titleLower) => {
  const text = typeof textRaw === 'string' ? textRaw.trim() : '';
  if (!text) return -999;
  const lower = text.toLowerCase();
  let score = 0;
  if (/(?:ep|episode|e)\s*\d{1,5}/i.test(text) || /第\s*\d+\s*集/.test(text)) score += 5;
  if (/S\s*\d{1,2}/i.test(text) || /第\s*\d+\s*季/.test(text)) score += 4;
  if (/(2160p|4k|1080p|720p)/i.test(text)) score += 2;
  if (titleLower && lower.includes(titleLower)) score += 2;
  if (text.length < 6) score -= 3;
  if (/^[0-9]+$/.test(text) && text.length >= 10) score -= 5;
  if (/^[a-z0-9]+$/i.test(text) && text.length >= 24) score -= 5;
  if (/\.(mkv|mp4|avi|flv|mov|wmv|m4v)$/i.test(text)) score += 1;
  return score;
};
