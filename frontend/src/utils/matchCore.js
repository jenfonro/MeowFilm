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
      // Match the complete, URL-confirmed identity before reading the suffix:
      // Baidu share codes can themselves contain one or more "-".
      const names = { baidu: '百度', quark: '夸克', uc: 'UC', '189': '天翼', '139': '移动' };
      const flagPrefix = `${names[provider]}-${shareId}-`;
      const flagPasscode = label.startsWith(flagPrefix) ? label.slice(flagPrefix.length) : '';
      const passcode = q.get('pwd') || q.get('passcode') || q.get('accessCode') || q.get('password') || q.get('passwd') ||
        hashQuery.get('pwd') || hashQuery.get('passwd') || flagPasscode;
      return { provider, shareId, passcode, url: input, key: `${provider}:${shareId}` };
    } catch (_error) {}
  }
  // Old mocked responses carry share identity in the flag and a password-only
  // value. For canonical labels, retain URL-based ownership/identity checks;
  // a descriptive label alone must not turn a complete native list into a share.
  const legacy = /^(夸父|优夕|逸动|天意|百度原画)-([A-Za-z0-9_-]+)$/.exec(label);
  if (!legacy || !valid(legacy[2]) || /[$*:/]/.test(raw)) return null;
  const provider = panMockProviderFromFlag(label);
  const shareId = provider === 'baidu' ? legacy[2].replace(/^1/, '') : legacy[2];
  if (!valid(shareId)) return null;
  return { provider, shareId, passcode: /^nopass$/i.test(raw) ? '' : raw, url: '', key: `${provider}:${shareId}` };
};

// Explicit history/manual list calls already selected a provider. Unlike normal
// detail ownership checks, they may use the saved canonical flag alone.
export const getPanListInput = (flag, value = '') => {
  const share = getPanShareInput(flag, value);
  if (share) return share;
  const label = normalizePanMockFlag(flag);
  // The existing Baidu list parser accepts decorated saved flags as well.
  // Restore that input only here, not in normal-detail ownership detection.
  const legacyBaidu = /^[^-]*百度[^-]*-([A-Za-z0-9_-]+)(?:#.*)?$/.exec(label);
  if (legacyBaidu && label.split('-')[0].trim() !== '百度') {
    return getPanShareInput(`百度原画-${legacyBaidu[1]}`, value);
  }
  if (String(value || '').trim()) return null;
  const match = /^(百度|夸克|UC|天翼|移动)-([A-Za-z0-9_-]{6,256})$/i.exec(label);
  if (!match) return null;
  const provider = panMockProviderFromFlag(label);
  let id = match[2], password = '';
  if (provider === 'baidu') {
    // Baidu's current short code is 22 characters and may contain "-".
    // Do not blindly split its last hyphen or strip an actual leading "1".
    const coded = /^([A-Za-z0-9_-]{22})-([A-Za-z0-9]{4})$/.exec(id);
    if (coded) [, id, password] = coded;
  } else {
    const coded = /^([A-Za-z0-9_]{6,256})(?:-([A-Za-z0-9]{1,16}))?$/.exec(id);
    if (!coded) return null;
    id = coded[1];
    password = coded[2] || '';
  }
  const bases = {
    baidu: 'https://pan.baidu.com/s/1', quark: 'https://pan.quark.cn/s/',
    uc: 'https://drive.uc.cn/s/', '189': 'https://cloud.189.cn/t/', '139': 'https://caiyun.139.com/m/i?',
  };
  const url = bases[provider] + id + (password
    ? `${provider === '139' ? '&' : '?'}${provider === '189' ? 'accessCode' : 'pwd'}=${encodeURIComponent(password)}` : '');
  return getPanShareInput(label, url);
};

// Recognize the existing built-in list file formats. A canonical display name
// alone is not evidence that a script's opaque ID uses list-directory metadata.
export const isBuiltinPanPlayId = (provider, value) => {
  const id = String(value || '').trim();
  if (provider === 'baidu') {
    try {
      const encoded = decodeURIComponent(id.split(/\|\|\||\*\*\*/)[0]).replace(/-/g, '+').replace(/_/g, '/');
      const data = JSON.parse(atob(encoded));
      return !!(data && data.surl && data.shareid && data.uk && data.fs_id && !data.providerId);
    } catch (_error) { return false; }
  }
  const parts = id.split('*');
  const token = value => /^[A-Za-z0-9_-]+$/.test(value || '');
  if (provider === 'quark' || provider === 'uc') {
    return parts.length >= 4 && token(parts[0]) && !!parts[1] && token(parts[2]) && !!parts[3];
  }
  if (provider === '139') return parts.length >= 3 && token(parts[0]) && token(parts[1]);
  if (provider === '189') return parts.length >= 3 && /^\d+$/.test(parts[0]) && /^\d+$/.test(parts[1]);
  return false;
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
  const share = getPanShareInput(label, rawName);
  if (share && share.provider === '189' && share.url) return { shareCode: share.shareId, accessCode: share.passcode };
  const canonical = /^天翼-([A-Za-z0-9]{6,64})(?:-(.*))?$/.exec(label);
  if (canonical) return { shareCode: canonical[1], accessCode: canonical[2] || '' };
  // Saved details from the previous short-lived format only carried a password.
  const oldCanonical = /^天翼(?:-(.*))?$/.exec(label);
  if (oldCanonical) return { shareCode: '', accessCode: oldCanonical[1] || '' };
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
