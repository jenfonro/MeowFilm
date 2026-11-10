import { sourceClone, sourceFault, sourceObject, sourceRequire } from './errors.js';

const legadoMarkers = ['ruleSearch', 'ruleBookInfo', 'ruleToc', 'ruleContent'];
const yiciyuanMarkers = ['ruleSearchList', 'ruleBookName', 'ruleChapterList', 'ruleBookContent'];

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
  if (yiciyuanMarkers.some(key => source[key] !== undefined)) return 'yiciyuan';
  if (qysgSource(source)) return 'qysg';
  if (typeof source.script === 'string' || typeof source.source === 'string') return 'cat';
  throw sourceFault('UNSUPPORTED_PROTOCOL');
}

function compact(object) {
  return Object.fromEntries(Object.entries(object).filter(([, value]) =>
    value !== undefined && value !== null && value !== ''));
}

function legacyType(value) {
  const type = String(value ?? '').trim().toLowerCase();
  if (['2', 'image', '漫画'].includes(type)) return 2;
  if (['1', 'audio', '有声'].includes(type)) return 1;
  if (['3', 'file', '文件'].includes(type)) return 3;
  return 0;
}

function legacyUrl(value) {
  if (typeof value !== 'string' || !value.trim()) return value;
  let url = value.trim()
    .replaceAll('searchKey', '{{key}}')
    .replace(/<?searchPage([+-]1)>?/g, '{{page$1}}')
    .replaceAll('searchPage', '{{page}}');
  const options = {};
  const header = /^@header:\s*(\{[\s\S]*?\})\s*/i.exec(url);
  if (header) {
    options.headers = header[1];
    url = url.slice(header[0].length);
  }
  const charset = /\|\s*(?:charset\s*=\s*)?([A-Za-z0-9._-]+)\s*$/i.exec(url);
  if (charset) {
    options.charset = charset[1];
    url = url.slice(0, charset.index);
  }
  if (!/^@js:|^<js>/i.test(url)) {
    const separator = url.indexOf('@', Math.max(0, url.indexOf('://') + 3));
    if (separator >= 0) {
      options.method = 'POST';
      options.body = url.slice(separator + 1);
      url = url.slice(0, separator);
    }
  }
  return Object.keys(options).length ? `${url},${JSON.stringify(options)}` : url;
}

function legacyRule(value) {
  if (typeof value !== 'string' || !value.trim()) return value;
  let rule = value;
  const lower = rule.toLowerCase();
  if (!lower.startsWith('@css:') && !lower.startsWith('@xpath:') &&
      !rule.startsWith('//') && !rule.startsWith('##') && !rule.startsWith(':') &&
      !lower.includes('@js:') && !lower.includes('<js>')) {
    if (rule.includes('#') && !rule.includes('##')) rule = rule.replaceAll('#', '##');
    if (rule.includes('&') && !rule.includes('&&') && !rule.includes('http') &&
        !rule.startsWith('/')) rule = rule.replaceAll('&', '&&');
    if (/^(?:\$|@json:)/i.test(rule) && rule.includes('|') && !rule.includes('||')) {
      rule = rule.replaceAll('|', '||');
    }
  }
  return rule;
}

export function importYiciyuanSource(input) {
  const source = parseSource(input);
  const type = legacyType(source.bookSourceType);
  return compact({
    bookSourceName: source.bookSourceName,
    bookSourceUrl: source.bookSourceUrl,
    bookSourceGroup: source.bookSourceGroup,
    bookSourceType: type,
    variableComment: source.variableComment,
    bookSourceComment: source.bookSourceComment,
    bookUrlPattern: source.ruleBookUrlPattern,
    customOrder: source.serialNumber,
    enabled: source.enable !== false,
    enabledExplore: Boolean(source.ruleFindUrl),
    loginUrl: source.loginUrl,
    loginUi: source.loginUi,
    loginCheckJs: source.loginCheckJs,
    coverDecodeJs: source.coverDecodeJs,
    jsLib: source.jsLib,
    enabledCookieJar: source.enabledCookieJar !== false,
    phonehttp: source.phonehttp !== false,
    header: source.httpUserAgent ? JSON.stringify({ 'User-Agent': source.httpUserAgent }) : '',
    searchUrl: legacyUrl(source.ruleSearchUrl),
    exploreUrl: legacyUrl(source.ruleFindUrl),
    ruleSearch: compact({
      bookList: legacyRule(source.ruleSearchList),
      name: legacyRule(source.ruleSearchName),
      author: legacyRule(source.ruleSearchAuthor),
      intro: legacyRule(source.ruleSearchIntroduce),
      kind: legacyRule(source.ruleSearchKind),
      lastChapter: legacyRule(source.ruleSearchLastChapter),
      bookUrl: legacyRule(source.ruleSearchNoteUrl),
      coverUrl: legacyRule(source.ruleSearchCoverUrl),
      checkKeyWord: legacyRule(source.checkKeyWord),
    }),
    ruleExplore: compact({
      bookList: legacyRule(source.ruleFindList ?? source.ruleSearchList),
      name: legacyRule(source.ruleFindName ?? source.ruleSearchName),
      author: legacyRule(source.ruleFindAuthor ?? source.ruleSearchAuthor),
      intro: legacyRule(source.ruleFindIntroduce ?? source.ruleSearchIntroduce),
      kind: legacyRule(source.ruleFindKind ?? source.ruleSearchKind),
      lastChapter: legacyRule(source.ruleFindLastChapter ?? source.ruleSearchLastChapter),
      bookUrl: legacyRule(source.ruleFindNoteUrl ?? source.ruleSearchNoteUrl),
      coverUrl: legacyRule(source.ruleFindCoverUrl ?? source.ruleSearchCoverUrl),
    }),
    ruleBookInfo: compact({
      init: legacyRule(source.ruleBookInfoInit),
      name: legacyRule(source.ruleBookName),
      author: legacyRule(source.ruleBookAuthor),
      kind: legacyRule(source.ruleBookKind),
      lastChapter: legacyRule(source.ruleBookLastChapter),
      intro: legacyRule(source.ruleIntroduce),
      coverUrl: legacyRule(source.ruleCoverUrl),
      tocUrl: legacyRule(source.ruleChapterUrl),
    }),
    ruleToc: compact({
      chapterList: legacyRule(source.ruleChapterList),
      chapterName: legacyRule(source.ruleChapterName),
      chapterUrl: legacyRule(source.ruleContentUrl || source.ruleChapterUrl),
      nextTocUrl: legacyRule(source.ruleChapterUrlNext),
      reverseToc: source.ruleChapterReverse,
    }),
    ruleContent: compact({
      content: legacyRule(source.ruleBookContent)?.replace(/^\$(?!\.)/, ''),
      title: legacyRule(source.title),
      payAction: legacyRule(source.payAction),
      nextContentUrl: legacyRule(source.ruleContentUrlNext),
      replaceRegex: legacyRule(source.ruleBookContentReplace),
      imageDecode: legacyRule(source.imageDecode ?? source.ruleBookContentDecoder),
      imageStyle: legacyRule(source.imageStyle) ?? (type === 2 ? 'FULL' : ''),
    }),
    __importedFrom: 'yiciyuan',
  });
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
  if (detected === 'yiciyuan') return {
    protocol: 'legado',
    source: importYiciyuanSource(input),
    importedProtocol: 'yiciyuan',
  };
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
