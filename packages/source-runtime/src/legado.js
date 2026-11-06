import { isDomNode } from './dom.js';
import { sourceFault, sourceObject, sourceRequire } from './errors.js';
import {
  normalizeBook, parseLiteral, parseRuleObject, readRuleString, readRuleValues,
} from './rules.js';

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

function objectOrEmpty(value) {
  return parseRuleObject(value);
}

function parseJSON(value) {
  if (typeof value !== 'string') return value;
  const text = value.trim();
  if (!text || !['{', '['].includes(text[0])) return value;
  try { return JSON.parse(text); } catch { return value; }
}

function itemObject(value) {
  const parsed = parseJSON(value);
  return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
}

function absolute(value, base) {
  if (!value) return '';
  try { return new URL(String(value), base || undefined).href; }
  catch { return String(value); }
}

export function parseLegadoRequestParts(value) {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    return { url: String(value.url ?? ''), options: { ...value } };
  }
  let text = String(value ?? '').trim();
  const options = {};
  const headerMatch = /@headers?:\s*(\{[\s\S]*\})\s*$/i.exec(text);
  if (headerMatch) {
    options.headers = headerMatch[1];
    text = text.slice(0, headerMatch.index).trim();
  }
  for (let index = text.lastIndexOf(','); index >= 0; index = text.lastIndexOf(',', index - 1)) {
    const suffix = text.slice(index + 1).trim();
    if (!suffix.startsWith('{')) continue;
    try {
      const parsed = parseLiteral(suffix);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return {
          url: text.slice(0, index).trim(),
          options: { ...options, ...parsed, headers: parsed.headers ?? options.headers },
        };
      }
    } catch { /* The comma belongs to the URL or script output. */ }
  }
  return { url: text, options };
}

function text(value) {
  if (value === null || value === undefined) return '';
  if (typeof value === 'object' && !isDomNode(value)) return JSON.stringify(value);
  return String(value);
}

function enabled(value) {
  if (typeof value === 'string') return !['', '0', 'false', 'null', 'undefined']
    .includes(value.trim().toLowerCase());
  return Boolean(value);
}

function scriptBody(value) {
  let script = String(value ?? '').trim();
  if (/^@js:/i.test(script)) script = script.slice(4);
  else if (/^<js>/i.test(script) && /<\/js>$/i.test(script)) {
    script = script.replace(/^<js>/i, '').replace(/<\/js>$/i, '');
  }
  return script.trim();
}

export class LegadoAdapter {
  constructor({ source, kernel, limits, importedProtocol }) {
    Object.assign(this, { source, kernel, limits, importedProtocol });
    this.searchRule = parseRuleObject(source.ruleSearch);
    this.exploreRule = parseRuleObject(source.ruleExplore);
    this.bookInfoRule = parseRuleObject(source.ruleBookInfo);
    this.tocRule = parseRuleObject(source.ruleToc);
    this.contentRule = parseRuleObject(source.ruleContent);
    const available = [];
    if (source.searchUrl) available.push('search');
    if (source.exploreUrl) available.push('exploreKinds', 'explore');
    if (Object.keys(this.bookInfoRule).length) available.push('bookInfo');
    if (Object.keys(this.tocRule).length) available.push('toc');
    if (this.contentRule.content) available.push('content');
    available.push('loginStatus');
    if (source.loginUrl || source.loginUi) available.push('login');
    available.push('image');
    this.actions = Object.freeze(standardActions.filter(action => available.includes(action)));
  }

  async start() {
    await this.kernel.loadSandbox('legado', '', {
      entries: [],
      actions: this.actions,
    });
  }

  bindings(payload = {}, extra = {}) {
    return {
      source: this.source,
      book: payload.book ?? {},
      chapter: payload.chapter ?? {},
      result: extra.result ?? payload.result ?? '',
      baseUrl: extra.baseUrl ?? payload.baseUrl ?? payload.url ?? this.source.bookSourceUrl ?? '',
      key: payload.keyword ?? payload.key ?? '',
      page: payload.page ?? 1,
      ...extra,
    };
  }

  async evaluate(code, payload, invocation, extra = {}) {
    const script = scriptBody(code);
    sourceRequire(script.length > 0, 'UNSUPPORTED_RULE');
    const library = String(this.source.jsLib ?? '').trim();
    return this.kernel.evaluateScript(
      library ? `${library}\n${script}` : script,
      this.bindings(payload, extra),
      invocation,
      {
        transform: {
          asyncRoots: ['java', 'cookie', 'cache', 'source'],
          asyncMethods: [
            'ajax', 'ajaxAll', 'connect', 'connectUrl', 'get', 'head', 'post', 'execute',
            'aJax', 'fetch', 'postForm', 'postJson',
            'downloadFile', 'importScript', 'startBrowser', 'startBrowserAwait',
            'showBrowser', 'openUrl', 'getVerificationCode',
            'webView', 'webViewGetOverrideUrl', 'webViewGetSource',
            'getCookie', 'getKey', 'setCookie', 'removeCookie', 'clearCookie',
            'getLoginInfo', 'getLoginInfoMap', 'putLoginInfo', 'removeLoginInfo',
            'getLoginHeader', 'getLoginHeaderMap', 'putLoginHeader', 'removeLoginHeader',
            'put', 'delete', 'remove',
          ],
          asyncChains: ['Jsoup.connect'],
        },
      },
    );
  }

  async render(value, payload, invocation, extra = {}) {
    if (value && typeof value === 'object') return value;
    const source = String(value ?? '');
    if (/^(?:@js:|<js>)/i.test(source.trim())) {
      return this.evaluate(source, payload, invocation, extra);
    }
    const matches = [...source.matchAll(/\{\{([\s\S]*?)\}\}/g)];
    if (!matches.length) return source;
    let output = '', cursor = 0;
    for (const match of matches) {
      output += source.slice(cursor, match.index);
      const expression = match[1].trim();
      if (expression === 'key') output += String(payload.keyword ?? payload.key ?? '');
      else if (expression === 'page') output += String(payload.page ?? 1);
      else output += text(await this.evaluate(expression, payload, invocation, extra));
      cursor = match.index + match[0].length;
    }
    return output + source.slice(cursor);
  }

  ruleContext(payload, invocation, extra = {}) {
    const bindings = this.bindings(payload, extra);
    return {
      ...bindings,
      evaluateScript: (code, next) => this.evaluate(code, payload, invocation, {
        ...bindings, ...next,
      }),
      evaluateWeb: (code, content) => this.kernel.capability('legado.web', {
        mode: 'rule',
        script: String(code),
        html: typeof content === 'string' ? content : text(content),
        url: String(bindings.baseUrl ?? ''),
      }, this.kernel.invocationContext(invocation)),
      getVariable: key => this.kernel.ruleVariableGet(key),
      setVariable: (key, value) => this.kernel.ruleVariableSet(key, value),
      maximumNodes: this.limits.maximumNodes,
      maximumParseBytes: this.limits.maximumParseBytes,
    };
  }

  async headers(payload, invocation) {
    const rendered = await this.render(this.source.header ?? {}, payload, invocation);
    return objectOrEmpty(rendered);
  }

  async request(template, payload, invocation, extra = {}) {
    const rendered = await this.render(template, payload, invocation, extra);
    let { url, options } = parseLegadoRequestParts(rendered);
    if (options.js) {
      const result = await this.evaluate(options.js, payload, invocation, {
        ...extra, result: url,
      });
      if (typeof result === 'string' && result) url = result;
    }
    const body = options.body === undefined
      ? undefined : await this.render(options.body, payload, invocation, extra);
    const headers = {
      ...await this.headers(payload, invocation),
      ...objectOrEmpty(options.headers),
    };
    const baseUrl = extra.baseUrl ?? payload.baseUrl ?? payload.bookUrl ??
      this.source.bookSourceUrl;
    sourceRequire(url, 'INVALID_ARGUMENT');
    if (enabled(options.webView ?? options.webview)) {
      const value = await this.kernel.capability('legado.web', {
        mode: 'request',
        url: absolute(url, baseUrl),
        ...(options.method === undefined ? {} : { method: options.method }),
        headers,
        ...(body === undefined ? {} : { body }),
        sourceRegex: options.sourceRegex ?? '',
        overrideUrlRegex: options.overrideUrlRegex ?? '',
      }, this.kernel.invocationContext(invocation));
      if (value && typeof value === 'object') {
        return {
          statusCode: Number(value.statusCode ?? value.status ?? 200),
          statusMessage: String(value.statusMessage ?? value.statusText ?? ''),
          headers: objectOrEmpty(value.headers),
          url: String(value.url ?? absolute(url, baseUrl)),
          body: value.body instanceof Uint8Array ? value.body
            : new TextEncoder().encode(String(value.data ?? value.html ?? value.body ?? '')),
          data: String(value.data ?? value.html ?? value.body ?? ''),
          encoding: String(value.encoding ?? 'utf-8'),
        };
      }
      return {
        statusCode: 200,
        statusMessage: '',
        headers: {},
        url: absolute(url, baseUrl),
        body: new TextEncoder().encode(String(value ?? '')),
        data: String(value ?? ''),
        encoding: 'utf-8',
      };
    }
    return this.kernel.http.request({
      url: absolute(url, baseUrl),
      method: options.method,
      headers,
      body,
      contentType: options.contentType,
      charset: options.charset,
      responseEncoding: options.charset,
      timeout: options.timeout ?? this.source.respondTime,
      followRedirects: options.followRedirects,
    }, this.kernel.invocationContext(invocation));
  }

  async values(rule, content, payload, invocation, extra = {}) {
    if (!rule) return [];
    return readRuleValues(rule, content, this.ruleContext(payload, invocation, extra));
  }

  async string(rule, content, payload, invocation, extra = {}, options) {
    if (!rule) return '';
    return readRuleString(rule, content, this.ruleContext(payload, invocation, extra), options);
  }

  async list(rule, content, payload, invocation, extra = {}) {
    if (rule) return this.values(rule, content, payload, invocation, extra);
    const parsed = parseJSON(content);
    return Array.isArray(parsed) ? parsed : [];
  }

  async initialized(rule, content, payload, invocation, extra = {}) {
    if (!rule?.init) return content;
    const values = await this.values(rule.init, content, payload, invocation, extra);
    if (!values.length) return content;
    return values.length === 1 ? values[0] : values;
  }

  async book(item, rule, payload, invocation, baseUrl) {
    const fallback = itemObject(item);
    const read = async (name, options) => rule[name]
      ? this.string(rule[name], item, payload, invocation, { baseUrl }, options)
      : text(fallback[name] ?? fallback[name === 'latestChapterTitle' ? 'lastChapter' : name]);
    const result = normalizeBook({
      name: await read('name'),
      author: await read('author'),
      kind: await read('kind'),
      coverUrl: await read('coverUrl', { url: true }),
      intro: await read('intro', { html: true }),
      bookUrl: await read('bookUrl', { url: true }),
      tocUrl: await read('tocUrl', { url: true }),
      wordCount: await read('wordCount'),
      latestChapterTitle: await read('lastChapter'),
      type: fallback.type,
      variable: fallback.variable,
    }, this.source, this.limits.maximumNodes, this.limits.maximumParseBytes);
    result.bookUrl = absolute(result.bookUrl || fallback.url, baseUrl);
    result.tocUrl = absolute(result.tocUrl || result.bookUrl, baseUrl);
    result.coverUrl = absolute(result.coverUrl, baseUrl);
    return result;
  }

  async books(rule, content, payload, invocation, baseUrl) {
    content = await this.initialized(rule, content, payload, invocation, { baseUrl });
    const list = await this.list(rule.bookList, content, payload, invocation, { baseUrl });
    const results = [];
    for (const item of list) {
      const book = await this.book(item, rule, payload, invocation, baseUrl);
      if (book.name || book.bookUrl) results.push(book);
    }
    return results;
  }

  async search(payload, invocation) {
    sourceRequire(typeof payload.keyword === 'string' || typeof payload.key === 'string');
    const response = await this.request(this.source.searchUrl, payload, invocation);
    return {
      books: await this.books(this.searchRule, response.data, payload, invocation, response.url),
      page: Number(payload.page ?? 1),
      url: response.url,
    };
  }

  async exploreKinds(payload, invocation) {
    let value = await this.render(this.source.exploreUrl ?? '', {
      ...payload, page: payload.page ?? 1,
    }, invocation);
    value = parseJSON(value);
    let items;
    if (Array.isArray(value)) items = value;
    else {
      items = String(value ?? '').split(/\r?\n/).map(line => {
        const separator = line.indexOf('::');
        return separator < 0 ? null : {
          title: line.slice(0, separator), url: line.slice(separator + 2),
        };
      }).filter(Boolean);
    }
    return {
      kinds: items.map(item => ({
        title: String(item.title ?? item.name ?? ''),
        url: String(item.url ?? ''),
        style: item.style && typeof item.style === 'object' ? item.style : {},
      })).filter(item => item.title || item.url),
    };
  }

  async explore(payload, invocation) {
    const template = payload.url ?? this.source.exploreUrl;
    sourceRequire(typeof template === 'string' && template.length > 0);
    const response = await this.request(template, payload, invocation);
    return {
      books: await this.books(this.exploreRule, response.data, payload, invocation, response.url),
      page: Number(payload.page ?? 1),
      url: response.url,
    };
  }

  async bookInfo(payload, invocation) {
    const bookUrl = String(payload.bookUrl ?? payload.url ?? '');
    sourceRequire(bookUrl);
    const response = await this.request(bookUrl, payload, invocation, { baseUrl: bookUrl });
    const base = { ...(payload.book ?? {}), bookUrl };
    const content = await this.initialized(this.bookInfoRule, response.data, payload, invocation, {
      baseUrl: response.url,
    });
    const parsed = await this.book(content, this.bookInfoRule, {
      ...payload, book: base,
    }, invocation, response.url);
    const book = { ...base };
    for (const [key, value] of Object.entries(parsed)) {
      if (typeof value !== 'string' || value !== '' || book[key] === undefined || book[key] === '') {
        book[key] = value;
      }
    }
    book.bookUrl ||= bookUrl;
    book.tocUrl ||= book.bookUrl;
    return { book, url: response.url };
  }

  async toc(payload, invocation) {
    let url = String(payload.tocUrl ?? payload.book?.tocUrl ?? payload.bookUrl ?? payload.url ?? '');
    sourceRequire(url);
    const chapters = [];
    const seenPages = new Set();
    const seenChapters = new Set();
    for (let page = 0; url && page < this.limits.maximumPages; page++) {
      url = absolute(url, payload.bookUrl ?? this.source.bookSourceUrl);
      if (seenPages.has(url)) {
        url = '';
        break;
      }
      seenPages.add(url);
      const response = await this.request(url, payload, invocation, { baseUrl: url });
      const content = await this.initialized(this.tocRule, response.data, payload, invocation, {
        baseUrl: response.url,
      });
      const items = await this.list(this.tocRule.chapterList, content, payload, invocation, {
        baseUrl: response.url,
      });
      for (const item of items) {
        const fallback = itemObject(item);
        const name = this.tocRule.chapterName
          ? await this.string(this.tocRule.chapterName, item, payload, invocation, { baseUrl: response.url })
          : text(fallback.name ?? fallback.title);
        const chapterUrl = absolute(this.tocRule.chapterUrl
          ? await this.string(this.tocRule.chapterUrl, item, payload, invocation,
            { baseUrl: response.url }, { url: true })
          : fallback.chapterUrl ?? fallback.url ?? fallback.chapterId, response.url);
        const identity = chapterUrl || `${name}:${chapters.length}`;
        if (!name && !chapterUrl || seenChapters.has(identity)) continue;
        seenChapters.add(identity);
        chapters.push({
          name,
          chapterUrl,
          index: chapters.length,
          isPay: Boolean(fallback.isPay),
          isVip: Boolean(fallback.isVip),
          isVolume: Boolean(fallback.isVolume),
          tag: text(fallback.tag),
        });
      }
      url = this.tocRule.nextTocUrl
        ? await this.string(this.tocRule.nextTocUrl, content, payload, invocation,
          { baseUrl: response.url }, { url: true })
        : '';
    }
    if (url && !seenPages.has(absolute(url, payload.bookUrl ?? this.source.bookSourceUrl))) {
      throw sourceFault('RESOURCE_LIMIT');
    }
    if (enabled(this.tocRule.reverseToc ?? this.tocRule.isReverse)) chapters.reverse();
    chapters.forEach((chapter, index) => { chapter.index = index; });
    return { chapters };
  }

  async content(payload, invocation) {
    let url = String(payload.chapterUrl ?? payload.chapter?.chapterUrl ?? payload.url ?? '');
    sourceRequire(url);
    const pages = [];
    const seen = new Set();
    for (let page = 0; url && page < this.limits.maximumPages; page++) {
      url = absolute(url, payload.bookUrl ?? this.source.bookSourceUrl);
      if (seen.has(url)) {
        url = '';
        break;
      }
      seen.add(url);
      const response = await this.request(url, payload, invocation, { baseUrl: url });
      const content = await this.initialized(this.contentRule, response.data, payload, invocation, {
        baseUrl: response.url,
      });
      let value = await this.string(this.contentRule.content, content, payload, invocation, {
        baseUrl: response.url,
      }, { html: true });
      const replacement = this.contentRule.replaceRegex ?? this.contentRule.sourceRegex;
      if (replacement) {
        value = await this.string(replacement, value, payload, invocation, {
          baseUrl: response.url,
        }, { html: true });
      }
      if (value) pages.push(value);
      url = this.contentRule.nextContentUrl
        ? await this.string(this.contentRule.nextContentUrl, response.data, payload, invocation,
          { baseUrl: response.url }, { url: true })
        : '';
    }
    if (url && !seen.has(absolute(url, payload.bookUrl ?? this.source.bookSourceUrl))) {
      throw sourceFault('RESOURCE_LIMIT');
    }
    return {
      content: pages.join('\n'),
      type: Number(this.source.bookSourceType ?? 0) || 0,
      imageStyle: String(this.contentRule.imageStyle ?? ''),
    };
  }

  async loginStatus(payload, invocation) {
    const url = String(payload.url ?? this.source.loginUrl ?? this.source.bookSourceUrl ?? '');
    if (this.source.loginCheckJs) {
      const response = await this.request(url, payload, invocation, { baseUrl: url });
      const value = await this.evaluate(this.source.loginCheckJs, payload, invocation, {
        result: response.data, baseUrl: response.url,
      });
      const loggedIn = typeof value === 'string'
        ? !['', '0', 'false', 'null', 'undefined'].includes(value.trim().toLowerCase())
        : Boolean(value);
      return { loggedIn, value };
    }
    const cookie = url
      ? await this.kernel.capability(
        'legado.cookieGet',
        { url },
        this.kernel.invocationContext(invocation),
      )
      : '';
    return { loggedIn: Boolean(cookie), cookiePresent: Boolean(cookie) };
  }

  async login(payload, invocation) {
    const url = String(payload.url ?? this.source.loginUrl ?? this.source.bookSourceUrl ?? '');
    sourceRequire(url, 'WEB_INTERACTION_REQUIRED');
    return {
      value: await this.kernel.capability('legado.web', {
        mode: 'login', url, title: String(this.source.bookSourceName ?? ''),
      }, this.kernel.invocationContext(invocation)),
    };
  }

  async image(payload, invocation) {
    const script = this.contentRule.imageDecode ?? this.contentRule.imageDecrypt ??
      this.source.imageDecode ?? this.source.imageDecrypt ?? this.source.coverDecodeJs;
    if (!script) return { body: payload.body ?? '', url: payload.url ?? '' };
    const value = await this.evaluate(script, payload, invocation, {
      result: payload.body ?? '', baseUrl: payload.url ?? '',
    });
    return { body: value, url: payload.url ?? '' };
  }

  invoke(action, payload, invocation) {
    const method = this[action];
    if (typeof method !== 'function') throw sourceFault('UNSUPPORTED_ACTION');
    sourceObject(payload);
    return method.call(this, payload, invocation);
  }
}
