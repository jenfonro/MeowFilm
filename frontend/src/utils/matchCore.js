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
  // Runner emits canonical names. Legacy names remain readable for saved data;
  // name recognition alone does not establish list/play ownership.
  const head = String((s.split('-')[0] || '')).trim();
  if (head === '百度') return 'baidu';
  if (head === '夸克') return 'quark';
  if (head.toUpperCase() === 'UC') return 'uc';
  if (head === '天翼') return '189';
  if (head === '移动') return '139';
  // Preserve the old name checks used by saved history and Emby callers.
  if (s.includes('-')) {
    if (head.includes('百度')) return 'baidu';
    if (head.includes('夸父')) return 'quark';
    if (head.includes('优夕')) return 'uc';
    if (head.includes('天意')) return '189';
    if (head.includes('逸动')) return '139';
  }
  return '';
};

export const getPanShareInput = (flag, value = '') => {
  const label = normalizePanMockFlag(flag);
  const raw = String(value || '').trim();
  const valid = (id) => /^[A-Za-z0-9_-]{4,256}$/.test(id) && !/^(?:root\d*|nopass|share)$/i.test(id);
  for (const input of [raw, label]) {
    try {
      const url = new URL(input);
      if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) continue;
      const host = url.hostname.toLowerCase();
      const q = url.searchParams;
      const match = /^\/s\/([A-Za-z0-9_-]+)\/?$/.exec(url.pathname);
      let provider = '';
      let shareId = '';
      if (host === 'pan.baidu.com') {
        provider = 'baidu';
        shareId = match ? match[1].replace(/^1/, '') : q.get('surl') || '';
      } else if (host === 'pan.quark.cn') {
        provider = 'quark';
        shareId = match ? match[1] : '';
      } else if (host === 'drive.uc.cn' || host === 'fast.uc.cn') {
        provider = 'uc';
        shareId = match ? match[1] : '';
      } else if (host === 'cloud.189.cn' || host === 'h5.cloud.189.cn') {
        provider = '189';
        shareId = (/^\/t\/([A-Za-z0-9_-]+)\/?$/.exec(url.pathname) || [])[1] || q.get('code') || q.get('shareCode') || '';
      } else if (host === 'caiyun.139.com' || host === 'yun.139.com') {
        provider = '139';
        shareId = (/(?:^|#)\/(?:w\/i|m\/i)\/([A-Za-z0-9_-]+)(?:[/?]|$)/.exec(url.pathname + url.hash) || [])[1] ||
          q.get('linkID') || q.get('linkId') ||
          (/^\/m\/i\/?$/.test(url.pathname) ? url.search.slice(1).split('&')[0] : '');
      }
      if (!provider || !valid(shareId)) continue;
      const hashQuery = new URLSearchParams(url.hash.includes('?') ? url.hash.slice(url.hash.indexOf('?') + 1) : '');
      const passcode = q.get('pwd') || q.get('passcode') || q.get('accessCode') || q.get('password') || q.get('passwd') ||
        hashQuery.get('pwd') || hashQuery.get('passwd') || '';
      return { provider, shareId, passcode, url: input, key: `${provider}:${shareId}` };
    } catch (_error) {}
  }
  // Old mocked responses carry share identity in the flag and a password-only
  // value. Canonical suffixes are passwords and must never be used as share IDs.
  const legacy = /^(夸父|优夕|逸动|天意|百度原画)-([A-Za-z0-9_-]+)$/.exec(label);
  if (!legacy || !valid(legacy[2]) || /[$*:/]/.test(raw)) return null;
  const provider = panMockProviderFromFlag(label);
  const shareId = provider === 'baidu' ? legacy[2].replace(/^1/, '') : legacy[2];
  if (!valid(shareId)) return null;
  return { provider, shareId, passcode: /^nopass$/i.test(raw) ? '' : raw, url: '', key: `${provider}:${shareId}` };
};

export const guessPreferredPanFromFlag = (flag) => {
  const raw = typeof flag === 'string' ? flag.trim() : '';
  if (!raw) return '';
  if (raw.includes('百度')) return 'baidu';
  if (raw.includes('夸父') || raw.includes('夸克')) return 'quark';
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
  const canonical = /^天翼(?:-(.*))?$/.exec(label);
  if (canonical) return { shareCode: '', accessCode: canonical[1] || '' };
  const share = getPanShareInput(label);
  if (share && share.provider === '189' && share.url) return { shareCode: share.shareId, accessCode: share.passcode };
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
