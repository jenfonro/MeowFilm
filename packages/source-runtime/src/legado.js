import { isDomNode } from './dom.js';
import { contentBlocks, resolveContentImages } from './content.js';
import { base64ToBytes } from './capabilities.js';
import { sourceObject, sourceRequire } from './errors.js';
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
  'payAction',
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
    if (this.contentRule.payAction) available.push('payAction');
    available.push('image');
    this.actions = Object.freeze(standardActions.filter(action => available.includes(action)));
  }

  async start() {
    const configured = String(this.source.jsLib ?? '').trim();
    let library = configured;
    if (configured.startsWith('{')) {
      let mapping;
      try { mapping = JSON.parse(configured); } catch { /* Inline script. */ }
      if (mapping && typeof mapping === 'object' && !Array.isArray(mapping)) {
        const chunks = [];
        for (const url of Object.values(mapping)) {
          if (typeof url !== 'string' || !/^https?:\/\//i.test(url)) continue;
          const response = await this.kernel.http.request({ url }, this.kernel.startupContext());
          sourceRequire(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
          chunks.push(response.data);
        }
        library = chunks.join('\n;\n');
      }
    }
    sourceRequire(new TextEncoder().encode(library).length <= this.limits.maximumParseBytes, 'RESOURCE_LIMIT');
    this.library = library;
    await this.kernel.loadSandbox('legado', '', {
      entries: [],
      actions: this.actions,
    });
    if (this.kernel.metadata.persistentSource) await this.kernel.restoreSourceValues();
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
      nextChapterUrl: payload.nextChapterUrl ?? '',
      ...extra,
    };
  }

  async evaluate(code, payload, invocation, extra = {}) {
    const script = scriptBody(code);
    sourceRequire(script.length > 0, 'UNSUPPORTED_RULE');
    const library = this.library;
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
      getVariable: key => this.kernel.ruleVariableGet(key, invocation.id),
      setVariable: (key, value) => this.kernel.ruleVariableSet(key, value, invocation.id),
      maximumNodes: this.limits.maximumNodes,
      maximumParseBytes: this.limits.maximumParseBytes,
    };
  }

  async headers(payload, invocation) {
    const rendered = await this.render(this.source.header ?? {}, payload, invocation);
    return objectOrEmpty(rendered);
  }

  async request(template, payload, invocation, extra = {}, requestedPages) {
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
    const method = String(options.method ?? (body === undefined ? 'GET' : 'POST')).toUpperCase();
    const bodylessGet = body === undefined && method === 'GET';
    const headers = {
      ...await this.headers(payload, invocation),
      ...objectOrEmpty(options.headers),
    };
    const baseUrl = extra.baseUrl ?? payload.baseUrl ?? payload.bookUrl ??
      this.source.bookSourceUrl;
    sourceRequire(url, 'INVALID_ARGUMENT');
    const requestUrl = absolute(url, baseUrl);
    if (requestedPages) {
      const identity = JSON.stringify([requestUrl, method, body, headers]);
      if (requestedPages.has(identity)) return null;
      requestedPages.add(identity);
    }
    if (enabled(options.webView ?? options.webview) || extra.webJs) {
      const value = await this.kernel.capability('legado.web', {
        mode: 'request',
        url: requestUrl,
        ...(options.method === undefined ? {} : { method: options.method }),
        headers,
        ...(body === undefined ? {} : { body }),
        sourceRegex: options.sourceRegex ?? '',
        overrideUrlRegex: options.overrideUrlRegex ?? '',
        ...(extra.webJs ? { script: String(extra.webJs) } : {}),
        ...(extra.sourceRegex ? { sourceRegex: String(extra.sourceRegex) } : {}),
      }, this.kernel.invocationContext(invocation));
      if (value && typeof value === 'object') {
        return {
          statusCode: Number(value.statusCode ?? value.status ?? 200),
          statusMessage: String(value.statusMessage ?? value.statusText ?? ''),
          headers: objectOrEmpty(value.headers),
          url: String(value.url ?? requestUrl),
          body: value.body instanceof Uint8Array ? value.body
            : new TextEncoder().encode(String(value.data ?? value.html ?? value.body ?? '')),
          data: String(value.data ?? value.html ?? value.body ?? ''),
          encoding: String(value.encoding ?? 'utf-8'),
          bodylessGet,
        };
      }
      return {
        statusCode: 200,
        statusMessage: '',
        headers: {},
        url: requestUrl,
        body: new TextEncoder().encode(String(value ?? '')),
        data: String(value ?? ''),
        encoding: 'utf-8',
        bodylessGet,
      };
    }
    const response = await this.kernel.http.request({
      url: requestUrl,
      method: options.method,
      headers,
      body,
      contentType: options.contentType,
      charset: options.charset,
      responseEncoding: options.charset,
      timeout: options.timeout ?? this.source.respondTime,
      followRedirects: options.followRedirects,
    }, this.kernel.invocationContext(invocation));
    return { ...response, bodylessGet };
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
    result.variable = this.kernel.variableValue(invocation.id) ?? fallback.variable ?? result.variable;
    return result;
  }

  async books(rule, content, payload, invocation, baseUrl) {
    content = await this.initialized(rule, content, payload, invocation, { baseUrl });
    const list = await this.list(rule.bookList, content, payload, invocation, { baseUrl });
    const results = [];
    const variables = this.kernel.invocations.get(invocation.id).variables;
    for (const item of list) {
      this.kernel.invocations.get(invocation.id).variables = new Map(variables);
      const book = await this.book(item, rule, payload, invocation, baseUrl);
      if (book.name || book.bookUrl) results.push(book);
    }
    this.kernel.invocations.get(invocation.id).variables = variables;
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
    book.bookUrl = bookUrl;
    book.tocUrl ||= book.bookUrl;
    if (book.tocUrl === response.url || book.tocUrl === bookUrl) book.tocHtml = response.data;
    book.variable = this.kernel.variableValue(invocation.id);
    return { book, url: response.url };
  }

  async toc(payload, invocation) {
    const book = { ...(payload.book ?? {}) };
    payload = { ...payload, book };
    let initialUrl = String(payload.tocUrl ?? book.tocUrl ?? payload.bookUrl ?? payload.url ?? '');
    sourceRequire(initialUrl);
    if (this.tocRule.preUpdateJs) {
      const updated = await this.evaluate(
        `(async () => { ${scriptBody(this.tocRule.preUpdateJs)}\n; return JSON.stringify(book); })()`,
        payload, invocation, { baseUrl: initialUrl });
      Object.assign(book, itemObject(updated));
      initialUrl = String(book.tocUrl ?? initialUrl);
    }
    const queue = [{ template: initialUrl, baseUrl: payload.bookUrl ?? this.source.bookSourceUrl }];
    const chapters = [];
    const requestedPages = new Set();
    const processedPages = new Set();
    const seenChapters = new Set();
    let firstPage = true;
    while (queue.length) {
      sourceRequire(!invocation.signal.aborted, 'CANCELLED');
      const { template, baseUrl } = queue.shift();
      const url = absolute(template, baseUrl);
      if (processedPages.has(url)) continue;
      const response = firstPage && book.tocHtml &&
          absolute(book.tocUrl ?? payload.bookUrl, payload.bookUrl) === url
        ? { data: book.tocHtml, url, bodylessGet: true }
        : await this.request(template, payload, invocation, { baseUrl }, requestedPages);
      firstPage = false;
      if (!response) continue;
      if (response.bodylessGet) {
        if (processedPages.has(response.url)) continue;
        processedPages.add(response.url);
      }
      const content = await this.initialized(this.tocRule, response.data, payload, invocation, {
        baseUrl: response.url,
      });
      const items = await this.list(this.tocRule.chapterList, content, payload, invocation, {
        baseUrl: response.url,
      });
      const baseVariables = this.kernel.invocations.get(invocation.id).variables;
      for (const item of items) {
        this.kernel.invocations.get(invocation.id).variables = new Map(baseVariables);
        const fallback = itemObject(item);
        const chapter = { index: chapters.length, bookUrl: payload.bookUrl ?? '',
          baseUrl: response.url };
        const itemPayload = { ...payload, chapter };
        const read = async key => this.tocRule[key]
          ? this.string(this.tocRule[key], item, itemPayload, invocation, { baseUrl: response.url })
          : fallback[key];
        const isVolume = enabled(await read('isVolume'));
        const isVip = enabled(await read('isVip'));
        const isPay = enabled(await read('isPay'));
        const name = this.tocRule.chapterName
          ? await this.string(this.tocRule.chapterName, item, itemPayload, invocation, { baseUrl: response.url })
          : text(fallback.name ?? fallback.title);
        const updateTime = text(await read(this.tocRule.updateTime ? 'updateTime' : 'chapterInfo') ??
          fallback.updateTime ?? fallback.time);
        let chapterUrl = this.tocRule.chapterUrl
          ? await this.string(this.tocRule.chapterUrl, item, itemPayload, invocation,
            { baseUrl: response.url })
          : text(fallback.chapterUrl ?? fallback.url ?? fallback.chapterId);
        if (!chapterUrl) chapterUrl = isVolume ? `${name}${chapter.index}` : response.url;
        else if (!isVolume || !chapterUrl.startsWith(name)) chapterUrl = absolute(chapterUrl, response.url);
        const identity = chapterUrl || `${name}:${chapters.length}`;
        if (!name && !chapterUrl || seenChapters.has(identity)) continue;
        seenChapters.add(identity);
        chapters.push({
          ...chapter,
          name,
          title: name,
          chapterUrl,
          url: chapterUrl,
          isPay,
          isVip,
          isVolume,
          updateTime,
          time: updateTime,
          tag: text(fallback.tag),
          variable: this.kernel.variableValue(invocation.id),
        });
      }
      this.kernel.invocations.get(invocation.id).variables = baseVariables;
      if (this.tocRule.nextTocUrl) {
        const next = await this.values(this.tocRule.nextTocUrl, content, payload, invocation,
          { baseUrl: response.url });
        for (const value of next.flatMap(value => typeof value === 'string' ? value.split(/\r?\n/) : [value])) {
          const template = text(value);
          const nextUrl = absolute(template, response.url);
          if (nextUrl && !processedPages.has(nextUrl) &&
              !seenChapters.has(nextUrl)) queue.push({ template, baseUrl: response.url });
        }
      }
    }
    if (enabled(this.tocRule.reverseToc ?? this.tocRule.isReverse)) chapters.reverse();
    chapters.forEach((chapter, index) => { chapter.index = index; });
    book.variable = this.kernel.variableValue(invocation.id);
    return { chapters, book };
  }

  async content(payload, invocation) {
    const firstUrl = String(payload.chapterUrl ?? payload.chapter?.chapterUrl ?? payload.chapter?.url ?? payload.url ?? '');
    sourceRequire(firstUrl);
    const chapter = { ...(payload.chapter ?? {}) };
    let title = String(chapter.title ?? chapter.name ?? '');
    if (chapter.isVolume && title && firstUrl.startsWith(title)) {
      const content = String(chapter.updateTime ?? chapter.time ?? '');
      return { title, chapter: { ...chapter, variable: this.kernel.variableValue(invocation.id) },
        chapterUrl: firstUrl, content, blocks: contentBlocks(content, this.limits), imageStyle: '', nextUrls: [] };
    }
    const catalog = payload.catalog ?? payload.book?.catalog ?? payload.book?.chapters;
    const chapterIndex = chapter.index ?? chapter.chapterIndex ?? payload.chapterIndex;
    const nextChapter = Array.isArray(catalog) && Number.isSafeInteger(chapterIndex)
      ? catalog[chapterIndex + 1] : null;
    const nextChapterUrl = absolute(payload.nextChapterUrl ?? nextChapter?.chapterUrl ?? nextChapter?.url ?? '',
      payload.bookUrl ?? firstUrl);
    payload = { ...payload, chapter, nextChapterUrl };
    const queue = [{ template: firstUrl, baseUrl: payload.bookUrl ?? this.source.bookSourceUrl }];
    const pages = [];
    const requestedPages = new Set();
    const processedPages = new Set();
    const visited = [];
    let responseUrl = firstUrl;
    while (queue.length) {
      sourceRequire(!invocation.signal.aborted, 'CANCELLED');
      const { template, baseUrl } = queue.shift();
      const url = absolute(template, baseUrl);
      if (processedPages.has(url) ||
          visited.length > 0 && url === nextChapterUrl) continue;
      const response = await this.request(template, payload, invocation, { baseUrl,
        ...(visited.length === 0 ? { webJs: this.contentRule.webJs,
          sourceRegex: this.contentRule.sourceRegex } : {}) }, requestedPages);
      if (!response) continue;
      if (visited.length && response.url === nextChapterUrl) continue;
      if (response.bodylessGet) {
        if (processedPages.has(response.url)) continue;
        processedPages.add(response.url);
      }
      if (!visited.length) responseUrl = response.url;
      visited.push(response.url);
      const content = await this.initialized(this.contentRule, response.data, payload, invocation, {
        baseUrl: response.url,
      });
      const pageTitle = await this.string(this.contentRule.title, content, payload, invocation, { baseUrl: response.url });
      if (pageTitle) title = pageTitle;
      const value = await this.string(this.contentRule.content, content, payload, invocation, {
        baseUrl: response.url,
      }, { html: true });
      if (value) pages.push(resolveContentImages(value, { ...this.limits, baseUrl: response.url }));
      if (this.contentRule.nextContentUrl) {
        const next = await this.values(this.contentRule.nextContentUrl, content, payload, invocation,
          { baseUrl: response.url });
        for (const item of next.flatMap(value => typeof value === 'string' ? value.split(/\r?\n/) : [value])) {
          const template = text(item);
          const nextUrl = absolute(template, response.url);
          if (nextUrl && nextUrl !== nextChapterUrl &&
              !processedPages.has(nextUrl)) {
            queue.push({ template, baseUrl: response.url });
          }
        }
      }
    }
    let content = pages.join('\n');
    if (this.contentRule.replaceRegex) content = await this.string(this.contentRule.replaceRegex, content,
      payload, invocation, { baseUrl: responseUrl }, { html: true });
    const blocks = contentBlocks(content, { ...this.limits, baseUrl: responseUrl });
    Object.assign(chapter, { title, name: title, chapterUrl: responseUrl, url: responseUrl,
      variable: this.kernel.variableValue(invocation.id) });
    return {
      title, chapter, chapterUrl: responseUrl, content, blocks, nextUrls: visited,
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
    let body = payload.body, url = payload.url ?? '';
    const data = /^data:image\/[A-Za-z0-9.+-]+;base64,([\s\S]*)$/.exec(url);
    if (body === undefined && data) body = base64ToBytes(data[1]);
    if (body === undefined && url) {
      const response = await this.request(url, payload, invocation, { baseUrl: payload.bookUrl ?? url });
      sourceRequire(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
      body = response.body; url = response.url;
    }
    const script = payload.kind === 'cover' ? this.source.coverDecodeJs ??
      this.source.imageDecode ?? this.source.imageDecrypt
      : this.contentRule.imageDecode ?? this.contentRule.imageDecrypt ??
        this.source.imageDecode ?? this.source.imageDecrypt ?? this.source.coverDecodeJs;
    body ??= '';
    if (!script) return { body, url };
    let value = await this.evaluate(script, payload, invocation, {
      result: body, baseUrl: url,
    });
    if (payload.kind && typeof value === 'string' && /^https?:\/\//i.test(value)) {
      const response = await this.request(value, payload, invocation, { baseUrl: url });
      sourceRequire(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
      value = response.body; url = response.url;
    }
    return { body: value, url };
  }

  async payAction(payload, invocation) {
    sourceRequire(this.contentRule.payAction, 'UNSUPPORTED_ACTION');
    sourceRequire(payload.bookUrl && (payload.chapterUrl || payload.chapter?.chapterUrl || payload.chapter?.url));
    const baseUrl = payload.chapterUrl ?? payload.chapter?.chapterUrl ?? payload.chapter?.url;
    const action = String(this.contentRule.payAction).trim();
    let value = /^(?:https?:\/\/|\/|\.)/i.test(action)
      ? absolute(await this.render(action, payload, invocation, { baseUrl }), baseUrl)
      : await this.evaluate(action, payload, invocation, { baseUrl });
    let type = enabled(value) ? 'success' : 'result';
    let url;
    if (typeof value === 'string' && /^https?:\/\//i.test(value.trim())) {
      url = value.trim(); type = 'url';
      value = await this.kernel.capability('legado.web', {
        mode: 'browser', url, headers: await this.headers(payload, invocation),
        title: String(payload.chapter?.title ?? payload.chapter?.name ?? ''),
      }, this.kernel.invocationContext(invocation));
    }
    return { type, ...(url ? { url } : {}), value: value ?? null, chapter: { ...payload.chapter,
      variable: this.kernel.variableValue(invocation.id) } };
  }

  invoke(action, payload, invocation) {
    const method = this[action];
    if (typeof method !== 'function') throw sourceFault('UNSUPPORTED_ACTION');
    sourceObject(payload);
    return method.call(this, payload, invocation);
  }
}
