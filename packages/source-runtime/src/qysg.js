import { domHTML, domText, parseMarkup } from './dom.js';
import {
  sourceFault, sourceObject, sourceRequire, sourceSafeError,
} from './errors.js';
import { normalizeBook } from './rules.js';

const textEncoder = new TextEncoder();
const standardActions = Object.freeze([
  'search',
  'exploreKinds',
  'explore',
  'bookInfo',
  'toc',
  'content',
  'loginStatus',
  'login',
  'image',
]);
const guestEntries = Object.freeze([
  'search', 'info', 'chapter', 'content', 'getfinds', 'find',
  'login', 'getloginurl', 'imagedecrypt', 'shouldOverrideUrlLoading',
  'pay', 'gethelp',
]);
const retryableDefinitionFailures = new Set([
  'NETWORK_ERROR',
  'HTTP_ERROR',
  'INVALID_RESPONSE',
]);

function remoteHtmlCandidates(value, maximum) {
  const entries = String(value ?? '').trim().split(/\s+/).filter(Boolean);
  sourceRequire(entries.length > 0, 'SCRIPT_PROTOCOL_ERROR');
  sourceRequire(entries.length <= maximum, 'RESOURCE_LIMIT');
  return entries.map(entry => {
    let url;
    try { url = new URL(entry); }
    catch { throw sourceFault('SCRIPT_PROTOCOL_ERROR'); }
    sourceRequire(['http:', 'https:'].includes(url.protocol) &&
      !url.username && !url.password, 'SCRIPT_PROTOCOL_ERROR');
    return url.href;
  });
}

function parseValue(value) {
  if (typeof value !== 'string') return value;
  const text = value.trim();
  if (!text || !['{', '['].includes(text[0])) return value;
  try { return JSON.parse(text); } catch { return value; }
}

function listValue(value, keys = []) {
  const parsed = parseValue(value);
  if (Array.isArray(parsed)) return parsed;
  if (parsed && typeof parsed === 'object') {
    for (const key of keys) if (Array.isArray(parsed[key])) return parsed[key];
  }
  return [];
}

function absolute(value, base) {
  if (!value) return '';
  try { return new URL(String(value), base || undefined).href; }
  catch { return String(value); }
}

function bookValue(value, source, maximumNodes, baseUrl) {
  const parsed = parseValue(value);
  const input = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  const book = normalizeBook({
    ...input,
    bookUrl: input.bookUrl ?? input.url,
    tocUrl: input.tocUrl ?? input.bookUrl ?? input.url,
    latestChapterTitle: input.latestChapterTitle ?? input.lastChapter,
  }, source, maximumNodes);
  book.bookUrl = absolute(book.bookUrl, baseUrl);
  book.tocUrl = absolute(book.tocUrl, baseUrl);
  book.coverUrl = absolute(book.coverUrl, baseUrl);
  return book;
}

function chapterValue(value, index, baseUrl) {
  const parsed = parseValue(value);
  const input = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  return {
    name: String(input.name ?? input.title ?? ''),
    chapterUrl: absolute(input.chapterUrl ?? input.chapterId ?? input.url, baseUrl),
    index: Number.isSafeInteger(input.index) ? input.index : index,
    isPay: Boolean(input.isPay),
    isVip: Boolean(input.isVip),
    isVolume: Boolean(input.isVolume),
    tag: String(input.tag ?? ''),
  };
}

function sourceProgram(html, maximumNodes) {
  const document = parseMarkup(html, maximumNodes);
  const scripts = [...document.querySelectorAll('script')];
  const records = scripts.map(script => ({
    source: script.getAttribute('src') ?? '',
    type: script.getAttribute('type') ?? '',
    code: domText(script, true) || String(script.textContent ?? ''),
  }));
  for (const script of scripts) script.remove();
  return { records, document: domHTML(document, true) };
}

const bundledCryptoJs = new Set([
  'AES', 'DES', 'MD5', 'SHA1', 'SHA256', 'SHA512',
  'HmacMD5', 'HmacSHA1', 'HmacSHA256', 'HmacSHA512',
  'enc', 'lib', 'mode', 'pad',
]);

function requiresExternalCryptoJs(records) {
  for (const record of records) {
    if (record.source) continue;
    for (const match of record.code.matchAll(/\bCryptoJS\.([A-Za-z_$][\w$]*)/g)) {
      if (!bundledCryptoJs.has(match[1])) return true;
    }
  }
  return false;
}

function bundledExternal(value, externalCryptoJs) {
  const source = String(value).replaceAll('&amp;', '&');
  if (/crypto-js(?:\.min)?\.js(?:$|[?#])/i.test(source)) return !externalCryptoJs;
  return /(?:jquery(?:-\d+(?:\.\d+)*)?|domino|(?:js-)?sha1)(?:\.min)?\.js(?:$|[?#])/i
    .test(source);
}

function ignoredExternal(value) {
  const source = String(value).replaceAll('&amp;', '&');
  return /^(?:https?:)?\/\/local\.adguard\.org(?:[/?#]|$)/i.test(source) ||
    /^(?:https?:)?\/\/static\.cloudflareinsights\.com\/beacon(?:\.min)?\.js(?:[/?#]|$)/i
      .test(source) ||
    /(?:^|\/)bt-stats\.js\?(?=[^#]*\bsite=)/i.test(source);
}

function executableProgram(records) {
  const externalCryptoJs = requiresExternalCryptoJs(records);
  return records.some(record => {
    if (record.type && !/^(?:text\/javascript|application\/javascript|module)$/i.test(record.type)) {
      return false;
    }
    if (record.code.trim()) return true;
    return Boolean(record.source) &&
      !ignoredExternal(record.source) &&
      !bundledExternal(record.source, externalCryptoJs);
  });
}

export class QysgAdapter {
  constructor({ source, kernel, limits }) {
    Object.assign(this, { source, kernel, limits });
    this.actions = Object.freeze([]);
    this.available = new Set();
  }

  async startupRequest(input) {
    try {
      return await this.kernel.trackAdapterTask(
        this.kernel.http.request(input, this.kernel.startupContext()),
      );
    } catch (error) {
      const failure = sourceSafeError(error, 'NETWORK_ERROR');
      if (failure.code === 'TIMEOUT' && !this.kernel.signal.aborted) {
        throw sourceFault('NETWORK_ERROR');
      }
      throw failure;
    }
  }

  async prepareProgram(program, baseUrl, emptyCode = 'SCRIPT_PROTOCOL_ERROR') {
    const chunks = [];
    let external = 0;
    let scriptBytes = 0;
    const append = value => {
      const code = String(value);
      if (!code.trim()) return;
      scriptBytes += textEncoder.encode(code).length;
      sourceRequire(scriptBytes <= this.limits.maximumBytes, 'RESOURCE_LIMIT');
      chunks.push(code);
    };
    const records = program.records;
    const externalCryptoJs = requiresExternalCryptoJs(records);
    for (const record of records) {
      if (record.source && ignoredExternal(record.source)) continue;
      if (record.type && !/^(?:text\/javascript|application\/javascript|module)$/i.test(record.type)) continue;
      if (record.type.toLowerCase() === 'module') throw sourceFault('UNSUPPORTED_DEPENDENCY');
      if (record.source) {
        if (bundledExternal(record.source, externalCryptoJs)) continue;
        sourceRequire(++external <= this.limits.maximumExternalScripts, 'RESOURCE_LIMIT');
        const response = await this.startupRequest({
          url: record.source,
          baseUrl,
        });
        if (response.statusCode < 200 || response.statusCode >= 300) {
          throw sourceFault('HTTP_ERROR');
        }
        append(response.data);
        if (/crypto-js(?:\.min)?\.js(?:$|[?#])/i.test(record.source)) {
          append(`if (module && module.exports && module.exports.DES) {
            CryptoJS = module.exports;
            globalThis.CryptoJS = CryptoJS;
          }`);
        }
      } else if (record.code.trim()) append(record.code);
    }
    sourceRequire(chunks.length > 0, emptyCode);
    const code = chunks.join('\n;\n');
    sourceRequire(textEncoder.encode(code).length <= this.limits.maximumBytes,
      'RESOURCE_LIMIT');
    return code;
  }

  async sourceDefinition() {
    const configured = String(this.source.html ?? '');
    sourceRequire(configured.length > 0);
    sourceRequire(textEncoder.encode(configured).length <= this.limits.maximumBytes,
      'RESOURCE_LIMIT');
    if (/<script\b/i.test(configured)) {
      const program = sourceProgram(configured, this.limits.maximumNodes);
      const baseUrl = String(this.source.bookSourceUrl ?? '').trim();
      return { program, baseUrl, code: await this.prepareProgram(program, baseUrl) };
    }

    const candidates = remoteHtmlCandidates(
      configured,
      this.limits.maximumSourceCandidates,
    );
    let failure = sourceFault('INVALID_RESPONSE');
    for (const url of candidates) {
      try {
        const response = await this.startupRequest({
          url,
        });
        if (response.statusCode < 200 || response.statusCode >= 300) {
          throw sourceFault('HTTP_ERROR');
        }
        sourceRequire(typeof response.data === 'string' && /<script\b/i.test(response.data),
          'INVALID_RESPONSE');
        sourceRequire(textEncoder.encode(response.data).length <= this.limits.maximumBytes,
          'RESOURCE_LIMIT');
        const program = sourceProgram(response.data, this.limits.maximumNodes);
        sourceRequire(executableProgram(program.records), 'INVALID_RESPONSE');
        const code = await this.prepareProgram(program, response.url, 'INVALID_RESPONSE');
        return { program, baseUrl: response.url, code };
      } catch (error) {
        failure = sourceSafeError(error, 'NETWORK_ERROR');
        if (!retryableDefinitionFailures.has(failure.code)) throw failure;
      }
    }
    throw failure;
  }

  async start() {
    sourceRequire(typeof this.source.html === 'string' && this.source.html.length > 0);
    const definition = await this.sourceDefinition();
    this.baseUrl = definition.baseUrl;
    this.kernel.setInitialDocument(definition.program.document);
    const descriptor = await this.kernel.loadSandbox('qysg', definition.code, {
      entries: guestEntries,
      actions: guestEntries,
      baseUrl: definition.baseUrl,
    });
    for (const action of descriptor.actions ?? []) this.available.add(action);
    const supported = {
      search: this.available.has('search'),
      exploreKinds: this.available.has('getfinds'),
      explore: this.available.has('find'),
      bookInfo: this.available.has('info'),
      toc: this.available.has('chapter'),
      content: this.available.has('content'),
      loginStatus: true,
      login: this.available.has('login') || Boolean(this.source.login),
      image: true,
    };
    this.actions = Object.freeze(standardActions.filter(action => supported[action]));
  }

  guest(action, payload, invocation) {
    sourceRequire(this.available.has(action), 'CAPABILITY_UNAVAILABLE');
    return this.kernel.invokeGuest(action, payload, invocation);
  }

  async search(payload, invocation) {
    const value = await this.guest('search', payload, invocation);
    const baseUrl = absolute(payload.baseUrl, this.baseUrl) || this.baseUrl;
    return {
      books: listValue(value, ['books', 'list'])
        .map(item => bookValue(item, this.source, this.limits.maximumNodes, baseUrl)),
      page: Number(payload.page ?? 1),
    };
  }

  async exploreKinds(_payload, invocation) {
    const value = await this.guest('getfinds', {}, invocation);
    return {
      kinds: listValue(value, ['kinds', 'list']).map(item => {
        const parsed = parseValue(item);
        const input = parsed && typeof parsed === 'object' ? parsed : {};
        return {
          title: String(input.title ?? input.name ?? ''),
          url: String(input.url ?? ''),
          type: Number(input.type ?? 0) || 0,
          js: String(input.js ?? ''),
        };
      }),
    };
  }

  async explore(payload, invocation) {
    const value = await this.guest('find', payload, invocation);
    const baseUrl = absolute(payload.url ?? payload.baseUrl, this.baseUrl) || this.baseUrl;
    return {
      books: listValue(value, ['books', 'list'])
        .map(item => bookValue(item, this.source, this.limits.maximumNodes, baseUrl)),
      page: Number(payload.page ?? 1),
    };
  }

  async bookInfo(payload, invocation) {
    const value = await this.guest('info', payload, invocation);
    const baseUrl = absolute(payload.bookUrl ?? payload.url ?? payload.book?.bookUrl ??
      payload.baseUrl, this.baseUrl) || this.baseUrl;
    return { book: bookValue(value, this.source, this.limits.maximumNodes, baseUrl) };
  }

  async toc(payload, invocation) {
    const value = await this.guest('chapter', payload, invocation);
    const bookUrl = absolute(payload.bookUrl ?? payload.book?.bookUrl, this.baseUrl) || this.baseUrl;
    const baseUrl = absolute(payload.tocUrl ?? payload.book?.tocUrl ?? payload.url ??
      payload.baseUrl, bookUrl) || bookUrl;
    const chapters = listValue(value, ['chapters', 'list'])
      .map((item, index) => chapterValue(item, index, baseUrl))
      .filter(chapter => chapter.name || chapter.chapterUrl);
    chapters.forEach((chapter, index) => { chapter.index = index; });
    return { chapters };
  }

  async content(payload, invocation) {
    const value = parseValue(await this.guest('content', payload, invocation));
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      return { ...value, content: String(value.content ?? value.data ?? '') };
    }
    return { content: String(value ?? '') };
  }

  async loginStatus(_payload, invocation) {
    let loginUrl = String(this.source.bookSourceUrl ?? '');
    if (this.available.has('getloginurl')) {
      loginUrl = String(await this.guest('getloginurl', {}, invocation) ?? loginUrl);
    }
    return { supported: Boolean(this.source.login || this.available.has('login')), loginUrl };
  }

  async login(payload, invocation) {
    if (this.available.has('login')) {
      return { value: await this.guest('login', payload, invocation) };
    }
    const status = await this.loginStatus(payload, invocation);
    sourceRequire(status.supported && status.loginUrl, 'WEB_INTERACTION_REQUIRED');
    return {
      value: await this.kernel.qysgBridge('startBrowser', [
        status.loginUrl, String(this.source.bookSourceName ?? ''), '{}',
      ], this.kernel.invocationContext(invocation)),
    };
  }

  async image(payload, invocation) {
    if (!this.available.has('imagedecrypt')) {
      return { url: String(payload.url ?? ''), body: payload.body ?? '' };
    }
    return {
      url: String(payload.url ?? ''),
      body: await this.guest('imagedecrypt', payload, invocation),
    };
  }

  invoke(action, payload, invocation) {
    const method = this[action];
    if (typeof method !== 'function') throw sourceFault('UNSUPPORTED_ACTION');
    sourceObject(payload);
    return method.call(this, payload, invocation);
  }
}
