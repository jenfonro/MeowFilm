/**
 * Generic portable guest SDK. Protocol code receives only JSON values, bounded
 * synchronous helpers and asynchronous host capabilities.
 */
export const portableRealmSource = String.raw`
(() => {
  'use strict';
  const bridge = globalThis.__meowBridge;
  delete globalThis.__meowBridge;
  const taskContext = globalThis.__meowTaskContext;
  delete globalThis.__meowTaskContext;
  const parse = JSON.parse.bind(JSON);
  const stringify = JSON.stringify.bind(JSON);
  const setPrototypeOf = Object.setPrototypeOf;
  const isView = ArrayBuffer.isView;
  const arrayFrom = Array.from.bind(Array);
  const apply = Reflect.apply;
  const promiseThen = Promise.prototype.then;
  const then = (promise, fulfilled, rejected) =>
    apply(promiseThen, promise, [fulfilled, rejected]);
  const pending = new Map();
  const callbacks = new Map();
  const trustedErrors = new WeakMap();
  let nextId = 0, protocol = '', exportsObject = null, realm;
  const documentRoots = new Map();
  const startupPromises = new Set();

  const error = code => {
    const value = new Error(code);
    value.code = code;
    value.name = code === 'CANCELLED' ? 'AbortError' : 'Error';
    trustedErrors.set(value, code);
    return value;
  };
  const errorCode = value => {
    const trusted = trustedErrors.get(value);
    if (trusted) return trusted;
    try {
      return value && value.name === 'InternalError' && /out of memory|allocation failed/i.test(value.message)
        ? 'MEMORY_LIMIT' : value && value.name === 'InternalError' && /stack overflow/i.test(value.message)
          ? 'RESOURCE_LIMIT' : 'SCRIPT_EXCEPTION';
    } catch (_) { return 'SCRIPT_EXCEPTION'; }
  };
  const call = (operation, ...args) => {
    setPrototypeOf(args, null);
    const response = parse(bridge(operation, stringify(args, (_key, value) =>
      isView(value) ? arrayFrom(value) : value)));
    if (!response.ok) throw error(response.code);
    return response.value;
  };
  const maxBytes = call('maxBytes');
  const size = value => {
    if (!Number.isSafeInteger(value) || value < 0 || value > maxBytes) throw error('RESOURCE_LIMIT');
    return value;
  };

  class ScriptBuffer extends Uint8Array {
    static from(input, encoding = 'utf8', length) {
      if (typeof input === 'string') return new ScriptBuffer(call('util', 'encode', [input, encoding]));
      if (input instanceof ArrayBuffer) {
        const offset = typeof encoding === 'number' ? encoding : 0;
        const count = length === undefined ? input.byteLength - offset : length;
        return new ScriptBuffer(input, offset, size(count));
      }
      if (input && input.type === 'Buffer') input = input.data;
      if (ArrayBuffer.isView(input) || Array.isArray(input)) return new ScriptBuffer(input);
      throw error('INVALID_ARGUMENT');
    }
    static alloc(count, fill = 0, encoding) {
      const value = new ScriptBuffer(size(count));
      if (typeof fill === 'number') value.fill(fill);
      else {
        const pattern = ScriptBuffer.from(fill, encoding);
        for (let index = 0; pattern.length && index < value.length; index++) value[index] = pattern[index % pattern.length];
      }
      return value;
    }
    static allocUnsafe(count) { return ScriptBuffer.alloc(count); }
    static isBuffer(value) { return value instanceof ScriptBuffer; }
    static byteLength(value, encoding) {
      return typeof value === 'string' ? ScriptBuffer.from(value, encoding).length : value.byteLength;
    }
    static concat(values, length = values.reduce((total, value) => total + value.length, 0)) {
      const result = ScriptBuffer.alloc(length);
      let offset = 0;
      for (const value of values) {
        const count = Math.min(value.length, length - offset);
        if (count <= 0) break;
        result.set(value.subarray(0, count), offset);
        offset += count;
      }
      return result;
    }
    static compare(left, right) {
      left = ScriptBuffer.from(left); right = ScriptBuffer.from(right);
      const count = Math.min(left.length, right.length);
      for (let index = 0; index < count; index++) {
        if (left[index] !== right[index]) return left[index] < right[index] ? -1 : 1;
      }
      return left.length === right.length ? 0 : left.length < right.length ? -1 : 1;
    }
    toString(encoding = 'utf8', start = 0, end = this.length) {
      return call('util', 'decode', [this, encoding, start, end]);
    }
    slice(start, end) { return this.subarray(start, end); }
    equals(other) { return this.length === other.length && this.every((byte, index) => byte === other[index]); }
    compare(other) { return ScriptBuffer.compare(this, other); }
    copy(target, targetStart = 0, sourceStart = 0, sourceEnd = this.length) {
      const source = this.subarray(sourceStart, sourceEnd);
      const count = Math.min(source.length, target.length - targetStart);
      if (count > 0) target.set(source.subarray(0, count), targetStart);
      return Math.max(0, count);
    }
    write(value, offset = 0, length, encoding = 'utf8') {
      if (typeof length === 'string') { encoding = length; length = undefined; }
      const bytes = ScriptBuffer.from(String(value), encoding);
      const count = Math.min(length ?? bytes.length, bytes.length, this.length - offset);
      if (count > 0) this.set(bytes.subarray(0, count), offset);
      return Math.max(0, count);
    }
    readUInt8(offset = 0) { return this[offset]; }
    readUInt16BE(offset = 0) { return new DataView(this.buffer, this.byteOffset, this.byteLength).getUint16(offset); }
    readUInt16LE(offset = 0) { return new DataView(this.buffer, this.byteOffset, this.byteLength).getUint16(offset, true); }
    readUInt32BE(offset = 0) { return new DataView(this.buffer, this.byteOffset, this.byteLength).getUint32(offset); }
    readUInt32LE(offset = 0) { return new DataView(this.buffer, this.byteOffset, this.byteLength).getUint32(offset, true); }
    writeUInt8(value, offset = 0) { this[offset] = value; return offset + 1; }
    writeUInt16BE(value, offset = 0) {
      new DataView(this.buffer, this.byteOffset, this.byteLength).setUint16(offset, value); return offset + 2;
    }
    writeUInt16LE(value, offset = 0) {
      new DataView(this.buffer, this.byteOffset, this.byteLength).setUint16(offset, value, true); return offset + 2;
    }
    writeUInt32BE(value, offset = 0) {
      new DataView(this.buffer, this.byteOffset, this.byteLength).setUint32(offset, value); return offset + 4;
    }
    writeUInt32LE(value, offset = 0) {
      new DataView(this.buffer, this.byteOffset, this.byteLength).setUint32(offset, value, true); return offset + 4;
    }
    toJSON() { return { type:'Buffer', data:Array.from(this) }; }
  }
  const byteView = value => value instanceof ArrayBuffer
    ? new ScriptBuffer(value)
    : ArrayBuffer.isView(value)
      ? new ScriptBuffer(value.buffer, value.byteOffset, value.byteLength)
      : ScriptBuffer.from(value);
  globalThis.Buffer = ScriptBuffer;
  globalThis.TextEncoder = class { encode(value = '') { return ScriptBuffer.from(String(value)); } };
  globalThis.TextDecoder = class {
    constructor(encoding = 'utf8') { this.encoding = encoding; }
    decode(value = []) { return ScriptBuffer.from(value).toString(this.encoding); }
  };
  globalThis.btoa = value => ScriptBuffer.from(String(value), 'latin1').toString('base64');
  globalThis.atob = value => ScriptBuffer.from(String(value), 'base64').toString('latin1');

  const assertTask = taskId => {
    if (taskId !== taskContext()) throw error('STALE_SESSION');
  };
  const sync = (taskId, name, input = {}) => {
    assertTask(taskId);
    return call('sync', taskId, name, input);
  };
  const decodeQuery = value => {
    try { return decodeURIComponent(String(value).replace(/\+/g, ' ')); }
    catch (_) { return String(value); }
  };
  class PortableURLSearchParams {
    constructor(input = '', changed) {
      this.__changed = changed;
      this.__pairs = [];
      this.__replace(input);
    }
    __replace(input) {
      this.__pairs = [];
      if (input instanceof PortableURLSearchParams) this.__pairs = input.__pairs.map(pair => pair.slice());
      else if (typeof input === 'string') {
        const source = input.startsWith('?') ? input.slice(1) : input;
        if (source) for (const part of source.split('&')) {
          const separator = part.indexOf('=');
          this.__pairs.push([
            decodeQuery(separator < 0 ? part : part.slice(0, separator)),
            decodeQuery(separator < 0 ? '' : part.slice(separator + 1)),
          ]);
        }
      } else if (Array.isArray(input) || input?.[Symbol.iterator]) {
        for (const pair of input) {
          if (!pair || pair.length !== 2) throw error('INVALID_ARGUMENT');
          this.__pairs.push([String(pair[0]), String(pair[1])]);
        }
      } else if (input && typeof input === 'object') {
        for (const [key, value] of Object.entries(input)) this.__pairs.push([key, String(value)]);
      }
    }
    __commit() { this.__changed?.(this.toString()); }
    append(name, value) { this.__pairs.push([String(name), String(value)]); this.__commit(); }
    delete(name, value) {
      name = String(name);
      this.__pairs = this.__pairs.filter(pair =>
        pair[0] !== name || arguments.length > 1 && pair[1] !== String(value));
      this.__commit();
    }
    get(name) { return this.__pairs.find(pair => pair[0] === String(name))?.[1] ?? null; }
    getAll(name) { return this.__pairs.filter(pair => pair[0] === String(name)).map(pair => pair[1]); }
    has(name, value) {
      name = String(name);
      return this.__pairs.some(pair => pair[0] === name &&
        (arguments.length < 2 || pair[1] === String(value)));
    }
    set(name, value) {
      name = String(name); value = String(value);
      const next = [];
      let inserted = false;
      for (const pair of this.__pairs) {
        if (pair[0] !== name) next.push(pair);
        else if (!inserted) { next.push([name, value]); inserted = true; }
      }
      if (!inserted) next.push([name, value]);
      this.__pairs = next;
      this.__commit();
    }
    sort() {
      this.__pairs = this.__pairs.map((pair, index) => ({ pair, index }))
        .sort((a, b) => a.pair[0] < b.pair[0] ? -1 : a.pair[0] > b.pair[0] ? 1 : a.index - b.index)
        .map(value => value.pair);
      this.__commit();
    }
    forEach(callback, thisArg) {
      for (const [name, value] of this.__pairs) callback.call(thisArg, value, name, this);
    }
    entries() { return this.__pairs.map(pair => pair.slice())[Symbol.iterator](); }
    keys() { return this.__pairs.map(pair => pair[0])[Symbol.iterator](); }
    values() { return this.__pairs.map(pair => pair[1])[Symbol.iterator](); }
    get size() { return this.__pairs.length; }
    toString() {
      return this.__pairs.map(([name, value]) =>
        encodeURIComponent(name).replace(/%20/g, '+') + '=' +
        encodeURIComponent(value).replace(/%20/g, '+')).join('&');
    }
    [Symbol.iterator]() { return this.entries(); }
  }
  class PortableURL {
    constructor(value, base) {
      this.__apply(sync(taskContext(), 'common.url', {
        value:String(value), ...(base === undefined ? {} : { base:String(base) }),
      }));
      this.searchParams = new PortableURLSearchParams(this.search, query => {
        this.__set('search', query ? '?' + query : '');
      });
    }
    __apply(parts) {
      for (const key of ['href','protocol','username','password','hostname','port','host',
        'origin','pathname','search','hash']) this['__' + key] = String(parts[key] ?? '');
      if (this.searchParams) this.searchParams.__replace(this.__search);
    }
    __set(name, value) {
      this.__apply(sync(taskContext(), 'common.url', {
        value:this.__href, set:{ name, value:String(value) },
      }));
    }
    get href() { return this.__href; } set href(value) {
      this.__apply(sync(taskContext(), 'common.url', { value:String(value) }));
    }
    get protocol() { return this.__protocol; } set protocol(value) { this.__set('protocol', value); }
    get username() { return this.__username; } set username(value) { this.__set('username', value); }
    get password() { return this.__password; } set password(value) { this.__set('password', value); }
    get hostname() { return this.__hostname; } set hostname(value) { this.__set('hostname', value); }
    get port() { return this.__port; } set port(value) { this.__set('port', value); }
    get host() { return this.__host; } set host(value) { this.__set('host', value); }
    get origin() { return this.__origin; }
    get pathname() { return this.__pathname; } set pathname(value) { this.__set('pathname', value); }
    get search() { return this.__search; } set search(value) { this.__set('search', value); }
    get hash() { return this.__hash; } set hash(value) { this.__set('hash', value); }
    toString() { return this.href; }
    toJSON() { return this.href; }
  }
  globalThis.URL = PortableURL;
  globalThis.URLSearchParams = PortableURLSearchParams;

  const runTask = (taskId, body) => {
    const previous = taskContext();
    taskContext(taskId);
    try { return body(); }
    finally { taskContext(previous); }
  };
  const capability = (taskId, name, input = {}) => {
    const promise = new Promise((resolve, reject) => {
      const id = ++nextId;
      pending.set(id, { resolve, reject, taskId });
      try {
        assertTask(taskId);
        call('capability', taskId, id, name, input);
      }
      catch (failure) { pending.delete(id); reject(failure); }
    });
    if (taskId === 0) {
      startupPromises.add(promise);
      then(promise, () => startupPromises.delete(promise),
        () => startupPromises.delete(promise));
    }
    return promise;
  };
  const timer = (fn, delay, repeat, args) => {
    if (typeof fn !== 'function') throw error('INVALID_ARGUMENT');
    const id = ++nextId;
    const taskId = taskContext();
    callbacks.set(id, { fn, repeat, args, taskId });
    try { call('timer', taskId, id, delay || 0, repeat); }
    catch (failure) { callbacks.delete(id); throw failure; }
    return id;
  };
  globalThis.setTimeout = (fn, delay, ...args) => timer(fn, delay, false, args);
  globalThis.setInterval = (fn, delay, ...args) => timer(fn, delay, true, args);
  globalThis.clearTimeout = globalThis.clearInterval = id => {
    callbacks.delete(id);
    call('clearTimer', id);
  };
  globalThis.queueMicrotask = fn => Promise.resolve().then(fn);
  globalThis.console = Object.freeze(Object.fromEntries(
    ['log','info','warn','error','debug','trace','group','groupCollapsed','groupEnd','clear','table','dir',
      'time','timeEnd','timeLog','assert','count','countReset'].map(name => [name, () => {}]),
  ));

  class NodeHandle {
    constructor(taskId, nodeId) { this.__taskId = taskId; this.__nodeId = nodeId; }
    get innerHTML() { return sync(this.__taskId, 'dom.html', { nodeId:this.__nodeId }); }
    set innerHTML(value) { sync(this.__taskId, 'dom.setHtml', { nodeId:this.__nodeId, html:String(value) }); }
    get outerHTML() { return sync(this.__taskId, 'dom.html', { nodeId:this.__nodeId, outer:true }); }
    get textContent() { return sync(this.__taskId, 'dom.text', { nodeId:this.__nodeId }); }
    set textContent(value) { sync(this.__taskId, 'dom.setText', { nodeId:this.__nodeId, text:String(value) }); }
    get innerText() { return this.textContent; }
    set innerText(value) { this.textContent = value; }
    get nodeType() { return sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'nodeType' }); }
    get nodeValue() { return sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'nodeValue' }); }
    set nodeValue(value) {
      sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'nodeValue', value });
    }
    get tagName() { return sync(this.__taskId, 'dom.tagName', { nodeId:this.__nodeId }); }
    get nodeName() { return sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'nodeName' }); }
    get id() { return this.getAttribute('id') || ''; }
    set id(value) { this.setAttribute('id', value); }
    get className() { return this.getAttribute('class') || ''; }
    set className(value) { this.setAttribute('class', value); }
    get value() { return sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'value' }) ?? ''; }
    set value(value) { sync(this.__taskId, 'dom.prop', { nodeId:this.__nodeId, name:'value', value:String(value) }); }
    get href() { return this.getAttribute('href') || ''; }
    set href(value) { this.setAttribute('href', value); }
    get src() { return this.getAttribute('src') || ''; }
    set src(value) { this.setAttribute('src', value); }
    get documentElement() { return this.nodeType === 9 ? this.querySelector('html') : null; }
    get head() { return this.nodeType === 9 ? this.querySelector('head') : null; }
    get body() { return this.nodeType === 9 ? this.querySelector('body') : null; }
    createElement(tag) {
      const value = sync(this.__taskId, 'dom.create', {
        nodeId:this.__nodeId, tag:String(tag),
      });
      return new NodeHandle(this.__taskId, value.nodeId);
    }
    createTextNode(text) {
      const value = sync(this.__taskId, 'dom.createText', {
        nodeId:this.__nodeId, text:String(text),
      });
      return new NodeHandle(this.__taskId, value.nodeId);
    }
    get title() {
      return this.nodeType === 9 ? this.querySelector('title')?.textContent ?? '' : '';
    }
    set title(value) {
      if (this.nodeType !== 9) return;
      const title = this.querySelector('title');
      if (title) title.textContent = value;
    }
    get children() { return nodeList(this.__taskId, 'dom.children', this.__nodeId); }
    get childNodes() { return nodeList(this.__taskId, 'dom.childNodes', this.__nodeId); }
    get firstChild() { return this.childNodes[0] || null; }
    get lastChild() {
      const children = this.childNodes;
      return children.length ? children[children.length - 1] : null;
    }
    get dataset() {
      const self = this;
      const attribute = property => 'data-' + String(property)
        .replace(/[A-Z]/g, value => '-' + value.toLowerCase());
      return new Proxy(Object.create(null), {
        get(_target, property) {
          if (property === Symbol.toStringTag) return 'DOMStringMap';
          if (property === 'then') return undefined;
          const value = self.getAttribute(attribute(property));
          return value === null ? undefined : value;
        },
        set(_target, property, value) {
          self.setAttribute(attribute(property), value);
          return true;
        },
        deleteProperty(_target, property) {
          self.removeAttribute(attribute(property));
          return true;
        },
      });
    }
    get style() {
      const self = this;
      const propertyName = property => String(property).replace(/[A-Z]/g,
        value => '-' + value.toLowerCase()).toLowerCase();
      const declarations = () => {
        const result = new Map();
        for (const entry of String(self.getAttribute('style') || '').split(';')) {
          const separator = entry.indexOf(':');
          if (separator <= 0) continue;
          result.set(entry.slice(0, separator).trim().toLowerCase(), entry.slice(separator + 1).trim());
        }
        return result;
      };
      const write = values => self.setAttribute('style',
        [...values].map(([name, value]) => name + ': ' + value).join('; '));
      const methods = {
        get cssText() { return self.getAttribute('style') || ''; },
        set cssText(value) { self.setAttribute('style', String(value)); },
        getPropertyValue(name) { return declarations().get(propertyName(name)) || ''; },
        setProperty(name, value) {
          const values = declarations();
          values.set(propertyName(name), String(value));
          write(values);
        },
        removeProperty(name) {
          const values = declarations();
          const key = propertyName(name);
          const previous = values.get(key) || '';
          values.delete(key);
          write(values);
          return previous;
        },
      };
      return new Proxy(methods, {
        get(target, property) {
          if (property in target) {
            const value = target[property];
            return typeof value === 'function' ? value.bind(target) : value;
          }
          if (property === Symbol.toStringTag) return 'CSSStyleDeclaration';
          if (property === 'then') return undefined;
          return declarations().get(propertyName(property)) || '';
        },
        set(target, property, value) {
          if (property === 'cssText') {
            target.cssText = value;
            return true;
          }
          target.setProperty(property, value);
          return true;
        },
      });
    }
    get nextElementSibling() {
      const value = sync(this.__taskId, 'dom.next', { nodeId:this.__nodeId });
      return value ? new NodeHandle(this.__taskId, value.nodeId) : null;
    }
    get previousElementSibling() {
      const value = sync(this.__taskId, 'dom.previous', { nodeId:this.__nodeId });
      return value ? new NodeHandle(this.__taskId, value.nodeId) : null;
    }
    get parentNode() {
      const value = sync(this.__taskId, 'dom.parent', { nodeId:this.__nodeId });
      return value ? new NodeHandle(this.__taskId, value.nodeId) : null;
    }
    get parentElement() { return this.parentNode; }
    get classList() {
      const self = this;
      return Object.freeze({
        contains(token) {
          return sync(self.__taskId, 'dom.classList',
            { nodeId:self.__nodeId, operation:'contains', token:String(token) });
        },
        add(...tokens) {
          for (const token of tokens) sync(self.__taskId, 'dom.classList',
            { nodeId:self.__nodeId, operation:'add', token:String(token) });
        },
        remove(...tokens) {
          for (const token of tokens) sync(self.__taskId, 'dom.classList',
            { nodeId:self.__nodeId, operation:'remove', token:String(token) });
        },
        toggle(token, force) {
          return sync(self.__taskId, 'dom.classList',
            { nodeId:self.__nodeId, operation:'toggle', token:String(token), force });
        },
      });
    }
    appendChild(child) {
      if (!(child instanceof NodeHandle)) throw error('INVALID_ARGUMENT');
      sync(this.__taskId, 'dom.append', { nodeId:this.__nodeId, childId:child.__nodeId });
      return child;
    }
    append(...children) {
      for (const child of children) {
        if (child instanceof NodeHandle) this.appendChild(child);
        else {
          const value = sync(this.__taskId, 'dom.createText',
            { nodeId:this.__nodeId, text:String(child) });
          this.appendChild(new NodeHandle(this.__taskId, value.nodeId));
        }
      }
    }
    replaceWith(value) {
      const child = value instanceof NodeHandle ? value : (() => {
        const created = sync(this.__taskId, 'dom.createText', {
          nodeId:this.__nodeId, text:String(value),
        });
        return new NodeHandle(this.__taskId, created.nodeId);
      })();
      sync(this.__taskId, 'dom.replace', {
        nodeId:this.__nodeId, childId:child.__nodeId,
      });
    }
    cloneNode(deep = false) {
      const value = sync(this.__taskId, 'dom.clone', {
        nodeId:this.__nodeId, deep:Boolean(deep),
      });
      return new NodeHandle(this.__taskId, value.nodeId);
    }
    removeChild(child) { child.remove(); return child; }
    remove() { sync(this.__taskId, 'dom.remove', { nodeId:this.__nodeId }); }
    getAttribute(name) { return sync(this.__taskId, 'dom.attr', { nodeId:this.__nodeId, name:String(name) }); }
    hasAttribute(name) {
      return sync(this.__taskId, 'dom.hasAttr', { nodeId:this.__nodeId, name:String(name) });
    }
    setAttribute(name, value) {
      sync(this.__taskId, 'dom.setAttr',
        { nodeId:this.__nodeId, name:String(name), value:String(value) });
    }
    removeAttribute(name) {
      sync(this.__taskId, 'dom.removeAttr', { nodeId:this.__nodeId, name:String(name) });
    }
    matches(selector) {
      return sync(this.__taskId, 'dom.matches', { nodeId:this.__nodeId, selector:String(selector) });
    }
    closest(selector) {
      const value = sync(this.__taskId, 'dom.closest',
        { nodeId:this.__nodeId, selector:String(selector) });
      return value ? new NodeHandle(this.__taskId, value.nodeId) : null;
    }
    querySelectorAll(selector) {
      if (![1, 9, 11].includes(this.nodeType)) return [];
      return queryNodes(this.__taskId, this.__nodeId, selector);
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    getElementById(id) { return this.querySelector('[id=' + JSON.stringify(String(id)) + ']'); }
    getElementsByTagName(name) { return this.querySelectorAll(String(name)); }
    getElementsByClassName(name) {
      return this.querySelectorAll(String(name).trim().split(/\s+/).filter(Boolean)
        .map(value => '.' + value.replace(/[^\w-]/g, character =>
          '\\' + character.codePointAt(0).toString(16) + ' ')).join(''));
    }
    getElementsByName(name) {
      return this.querySelectorAll('[name=' + JSON.stringify(String(name)) + ']');
    }
    contains(child) {
      return child instanceof NodeHandle && sync(this.__taskId, 'dom.contains',
        { nodeId:this.__nodeId, childId:child.__nodeId });
    }
    isSameNode(other) {
      return other instanceof NodeHandle &&
        other.__taskId === this.__taskId && other.__nodeId === this.__nodeId;
    }
    addEventListener() {}
    removeEventListener() {}
    dispatchEvent() { return true; }
    click() {}
    text() { return this.textContent; }
    html() { return this.innerHTML; }
    attr(name, value) {
      if (value === undefined) return this.getAttribute(name);
      this.setAttribute(name, value);
      return this;
    }
    select(selector) { return elementList(this.querySelectorAll(selector)); }
    selectFirst(selector) { return this.querySelector(selector); }
    toString() { return this.outerHTML; }
  }
  const materialize = (taskId, value) => value && value.kind === 'node'
    ? new NodeHandle(taskId, value.nodeId) : value?.value ?? '';
  const queryNodes = (taskId, nodeId, selector) =>
    sync(taskId, 'dom.query', { nodeId, selector:String(selector) }).map(value => materialize(taskId, value));
  const elementList = values => {
    values.size = () => values.length;
    values.get = index => values[index < 0 ? values.length + index : index];
    values.first = () => values[0] || null;
    values.toArray = () => values.slice();
    return values;
  };
  const nodeList = (taskId, operation, nodeId) => elementList(
    sync(taskId, operation, { nodeId }).map(value => materialize(taskId, value)),
  );

  class MiniQuery {
    constructor(taskId, values = [], previous = null) {
      this.taskId = taskId;
      this.previous = previous;
      const flatten = value => value instanceof MiniQuery ? value.values
        : Array.isArray(value) ? value.flatMap(flatten)
          : value === null || value === undefined ? [] : [value];
      this.values = flatten(values);
      for (let index = 0; index < this.values.length; index++) this[index] = this.values[index];
    }
    get length() { return this.values.length; }
    get innerHTML() { return this.values[0] instanceof NodeHandle ? this.values[0].innerHTML : ''; }
    set innerHTML(value) {
      if (this.values[0] instanceof NodeHandle) this.values[0].innerHTML = value;
    }
    get textContent() { return this.values.map(value => value?.textContent ?? String(value ?? '')).join(''); }
    get parentNode() { return this.values[0]?.parentNode ?? null; }
    get children() { return this.values[0]?.children ?? []; }
    get childNodes() { return this.values[0]?.childNodes ?? []; }
    get classList() { return this.values[0]?.classList; }
    find(selector) {
      const values = this.values.flatMap(value => {
        let own = false;
        if (value instanceof NodeHandle) {
          try { own = value.matches(selector); } catch (_) { /* Descendant query may support extensions. */ }
        }
        return [
          ...(own ? [value] : []),
          ...(typeof value?.querySelectorAll === 'function' ? value.querySelectorAll(selector) : []),
        ];
      });
      return new MiniQuery(this.taskId, values.filter((value, index) =>
        values.findIndex(other => value === other || value?.isSameNode?.(other)) === index), this);
    }
    querySelectorAll(selector) {
      return this.values.flatMap(value =>
        typeof value?.querySelectorAll === 'function' ? value.querySelectorAll(selector) : []);
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    each(callback) {
      for (let index = 0; index < this.values.length; index++) {
        const value = this.values[index];
        if (callback.call(value, index, value) === false) break;
      }
      return this;
    }
    map(callback) { return this.values.map((value, index) => callback.call(value, index, value)); }
    text(value) {
      if (arguments.length) {
        for (const item of this.values) if (item && 'textContent' in item) item.textContent =
          typeof value === 'function' ? value.call(item, this.values.indexOf(item), item.textContent) : value;
        return this;
      }
      return this.values.map(item => item?.textContent ?? String(item ?? '')).join('');
    }
    html(value) {
      if (arguments.length) {
        for (const item of this.values) if (item instanceof NodeHandle) item.innerHTML =
          typeof value === 'function' ? value.call(item, this.values.indexOf(item), item.innerHTML) : value;
        return this;
      }
      return this.values[0] instanceof NodeHandle ? this.values[0].innerHTML : '';
    }
    attr(name, value) {
      if (name && typeof name === 'object') {
        for (const [key, child] of Object.entries(name)) this.attr(key, child);
        return this;
      }
      if (arguments.length > 1) {
        for (const item of this.values) if (item instanceof NodeHandle) item.setAttribute(name,
          typeof value === 'function' ? value.call(item, this.values.indexOf(item),
            item.getAttribute(name) ?? undefined) : value);
        return this;
      }
      if (!(this.values[0] instanceof NodeHandle)) return undefined;
      return this.values[0].getAttribute(name) ?? undefined;
    }
    removeAttr(name) {
      for (const item of this.values) if (item instanceof NodeHandle) item.removeAttribute(name);
      return this;
    }
    first() { return new MiniQuery(this.taskId, this.values.slice(0, 1), this); }
    last() { return this.eq(-1); }
    eq(index) {
      const offset = index < 0 ? this.values.length + index : index;
      return new MiniQuery(this.taskId,
        offset >= 0 ? this.values.slice(offset, offset + 1) : [], this);
    }
    get(index) {
      if (index === undefined) return this.values.slice();
      return this.values[index < 0 ? this.values.length + index : index];
    }
    toArray() { return this.values.slice(); }
    remove() { for (const value of this.values) value.remove?.(); return this; }
    filter(selector) {
      const values = typeof selector === 'function'
        ? this.values.filter((value, index) => selector.call(value, index, value))
        : selector instanceof NodeHandle
          ? this.values.filter(value => value === selector || value?.isSameNode?.(selector))
          : selector instanceof MiniQuery
            ? this.values.filter(value => selector.values.some(other =>
              value === other || value?.isSameNode?.(other)))
            : this.values.filter(value => value instanceof NodeHandle && value.matches(String(selector)));
      return new MiniQuery(this.taskId, values, this);
    }
    not(selector) {
      const excluded = this.filter(selector).values;
      return new MiniQuery(this.taskId, this.values.filter(value =>
        !excluded.some(other => value === other || value?.isSameNode?.(other))), this);
    }
    slice(start, end) { return new MiniQuery(this.taskId, this.values.slice(start, end), this); }
    parent(selector) {
      const values = this.values.map(value => value?.parentElement).filter(Boolean);
      const unique = values.filter((value, index) =>
        values.findIndex(other => value.isSameNode?.(other)) === index);
      const result = new MiniQuery(this.taskId, unique, this);
      if (selector === undefined) return result;
      const filtered = result.filter(selector);
      filtered.previous = this;
      return filtered;
    }
    children(selector) {
      const result = new MiniQuery(this.taskId,
        this.values.flatMap(value => value instanceof NodeHandle ? value.children : []), this);
      if (selector === undefined) return result;
      const filtered = result.filter(selector);
      filtered.previous = this;
      return filtered;
    }
    contents() {
      return new MiniQuery(this.taskId,
        this.values.flatMap(value => value instanceof NodeHandle ? value.childNodes : []), this);
    }
    next(selector) {
      const result = new MiniQuery(this.taskId,
        this.values.map(value => value?.nextElementSibling).filter(Boolean), this);
      if (selector === undefined) return result;
      const filtered = result.filter(selector);
      filtered.previous = this;
      return filtered;
    }
    nextAll(selector) {
      const values = [];
      for (const item of this.values) {
        let value = item?.nextElementSibling;
        while (value) {
          if (!values.some(other => other.isSameNode?.(value))) values.push(value);
          value = value.nextElementSibling;
        }
      }
      const result = new MiniQuery(this.taskId, values, this);
      if (selector === undefined) return result;
      const filtered = result.filter(selector);
      filtered.previous = this;
      return filtered;
    }
    prev(selector) {
      const result = new MiniQuery(this.taskId,
        this.values.map(value => value?.previousElementSibling).filter(Boolean), this);
      if (selector === undefined) return result;
      const filtered = result.filter(selector);
      filtered.previous = this;
      return filtered;
    }
    closest(selector) {
      const values = this.values.map(value => value?.closest?.(selector)).filter(Boolean);
      return new MiniQuery(this.taskId, values.filter((value, index) =>
        values.findIndex(other => value.isSameNode?.(other)) === index), this);
    }
    has(selector) {
      return new MiniQuery(this.taskId, this.values.filter(value =>
        value instanceof NodeHandle && value.querySelector(selector)), this);
    }
    clone() {
      return new MiniQuery(this.taskId,
        this.values.map(value => value instanceof NodeHandle ? value.cloneNode(true) : value), this);
    }
    end() { return this.previous || new MiniQuery(this.taskId); }
    is(selector) { return this.filter(selector).length > 0; }
    prop(name, value) {
      const first = this.values[0];
      if (arguments.length === 1) {
        return first instanceof NodeHandle
          ? sync(first.__taskId, 'dom.prop', { nodeId:first.__nodeId, name:String(name) })
          : undefined;
      }
      for (const item of this.values) if (item instanceof NodeHandle) {
        const next = typeof value === 'function'
          ? value.call(item, this.values.indexOf(item),
            sync(item.__taskId, 'dom.prop', { nodeId:item.__nodeId, name:String(name) }))
          : value;
        sync(item.__taskId, 'dom.prop', { nodeId:item.__nodeId, name:String(name), value:next });
      }
      return this;
    }
    hasClass(name) {
      return this.values.some(value => value instanceof NodeHandle && value.classList.contains(String(name)));
    }
    addClass(value) {
      const names = String(value).split(/\s+/).filter(Boolean);
      for (const item of this.values) item.classList?.add(...names);
      return this;
    }
    removeClass(value) {
      const names = value === undefined ? null : String(value).split(/\s+/).filter(Boolean);
      for (const item of this.values) {
        if (!(item instanceof NodeHandle)) continue;
        if (names) item.classList.remove(...names);
        else item.removeAttribute('class');
      }
      return this;
    }
    toggleClass(name, force) {
      for (const item of this.values) if (item instanceof NodeHandle) {
        if (arguments.length > 1) item.classList.toggle(String(name), Boolean(force));
        else item.classList.toggle(String(name));
      }
      return this;
    }
    val(value) {
      if (!arguments.length) return this.values[0] instanceof NodeHandle ? this.values[0].value : undefined;
      for (const item of this.values) if (item instanceof NodeHandle) item.value =
        typeof value === 'function' ? value.call(item, this.values.indexOf(item), item.value) : value;
      return this;
    }
    empty() {
      for (const item of this.values) if (item instanceof NodeHandle) item.innerHTML = '';
      return this;
    }
    append(...values) {
      for (const item of this.values) if (item instanceof NodeHandle) {
        for (const value of values.flatMap(child =>
          child instanceof MiniQuery ? child.values : Array.isArray(child) ? child : [child])) item.append(value);
      }
      return this;
    }
    ready(callback) {
      if (typeof callback === 'function') callback.call(this.values[0] ?? globalThis.document);
      return this;
    }
    [Symbol.iterator]() { return this.values[Symbol.iterator](); }
  }

  function createDomScope() {
    const currentTask = () => taskContext();
    const parseNodes = (owner, html) => {
      const parsed = sync(owner, 'dom.parse', { html:String(html ?? '') });
      const root = new NodeHandle(owner, parsed.rootId);
      const body = root.querySelector('body');
      const values = body?.childNodes?.length ? body.childNodes : root.childNodes;
      return values?.length ? Array.from(values) : [root];
    };
    const ensureDocument = () => {
      const owner = currentTask();
      if (!documentRoots.has(owner)) {
        documentRoots.set(owner, protocol === 'qysg'
          ? sync(owner, 'qysg.document', {})
          : sync(owner, 'dom.parse', { html:'<!doctype html><html><body></body></html>' }));
      }
      return { owner, root:documentRoots.get(owner) };
    };
    const document = {
      createElement(tag) {
        const { owner, root } = ensureDocument();
        const value = sync(owner, 'dom.create', { nodeId:root.rootId, tag:String(tag) });
        return new NodeHandle(owner, value.nodeId);
      },
      createTextNode(text) {
        const { owner, root } = ensureDocument();
        const value = sync(owner, 'dom.createText', { nodeId:root.rootId, text:String(text) });
        return new NodeHandle(owner, value.nodeId);
      },
      querySelectorAll(selector) {
        const { owner, root } = ensureDocument();
        return queryNodes(owner, root.rootId, selector);
      },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      getElementById(id) { return this.querySelector('[id=' + JSON.stringify(String(id)) + ']'); },
      getElementsByTagName(name) { return this.querySelectorAll(String(name)); },
      getElementsByClassName(name) {
        return this.querySelectorAll(String(name).trim().split(/\s+/).filter(Boolean)
          .map(value => '.' + value.replace(/[^\w-]/g, character =>
            '\\' + character.codePointAt(0).toString(16) + ' ')).join(''));
      },
      getElementsByName(name) {
        return this.querySelectorAll('[name=' + JSON.stringify(String(name)) + ']');
      },
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent() { return true; },
      write(...values) {
        const body = this.body ?? this.documentElement;
        if (body) body.innerHTML += values.map(value => String(value ?? '')).join('');
      },
      writeln(...values) { this.write(...values, '\n'); },
      readyState:'complete',
    };
    let documentHidden = false;
    Object.defineProperties(document, {
      documentElement:{ get() { return document.querySelector('html'); } },
      head:{ get() { return document.querySelector('head'); } },
      body:{ get() { return document.querySelector('body'); } },
      title:{
        get() { return document.querySelector('title')?.textContent ?? ''; },
        set(value) {
          let title = document.querySelector('title');
          if (!title) {
            title = document.createElement('title');
            (document.head ?? document.documentElement)?.appendChild(title);
          }
          if (title) title.textContent = String(value ?? '');
        },
      },
      textContent:{
        get() {
          const { owner, root } = ensureDocument();
          return new NodeHandle(owner, root.rootId).textContent;
        },
        set(value) {
          const target = document.body ?? document.documentElement;
          if (target) target.textContent = String(value ?? '');
        },
      },
      innerText:{
        get() { return document.textContent; },
        set(value) { document.textContent = value; },
      },
      hidden:{
        get() { return documentHidden; },
        set(value) { documentHidden = Boolean(value); },
      },
    });
    document.implementation = {
      createHTMLDocument() {
        const owner = currentTask();
        const parsed = sync(owner, 'dom.parse', { html:'<!doctype html><html><head></head><body></body></html>' });
        return new NodeHandle(owner, parsed.rootId);
      },
    };
    const dollar = input => {
      if (input instanceof MiniQuery) return input;
      if (input instanceof NodeHandle) return new MiniQuery(input.__taskId, [input]);
      if (input === document) return new MiniQuery(currentTask(), [document]);
      if (typeof input === 'function') { input(); return new MiniQuery(currentTask(), [document]); }
      if (Array.isArray(input) || input && typeof input !== 'string' && input[Symbol.iterator]) {
        const values = Array.from(input);
        const owner = values.find(value => value instanceof NodeHandle)?.__taskId ?? currentTask();
        return new MiniQuery(owner, values);
      }
      if (typeof input === 'string' && input.trim().startsWith('<')) {
        const owner = currentTask();
        return new MiniQuery(owner, parseNodes(owner, input));
      }
      if (typeof input === 'string') {
        const owner = currentTask();
        return new MiniQuery(owner, document.querySelectorAll(input));
      }
      return new MiniQuery(currentTask(), []);
    };
    dollar.parseHTML = value => parseNodes(currentTask(), value);
    dollar.trim = value => String(value ?? '').trim();
    dollar.each = (value, callback) => {
      if (Array.isArray(value) || value?.[Symbol.iterator]) {
        let index = 0;
        for (const child of value) {
          if (callback.call(child, index++, child) === false) break;
        }
      } else if (value && typeof value === 'object') {
        for (const [key, child] of Object.entries(value)) {
          if (callback.call(child, key, child) === false) break;
        }
      }
      return value;
    };
    class DOMParser {
      parseFromString(value) {
        const owner = currentTask();
        const parsed = sync(owner, 'dom.parse', { html:String(value ?? '') });
        return new NodeHandle(owner, parsed.rootId);
      }
    }
    return { document, dollar, NodeHandle, MiniQuery, DOMParser };
  }

  const makeResponse = value => ({
    statusCode:Number(value?.statusCode ?? value?.status ?? 0),
    statusMessage:String(value?.statusMessage ?? value?.statusText ?? ''),
    headers:value?.headers ?? {},
    url:String(value?.url ?? ''),
    body() { return String(value?.data ?? value?.body ?? ''); },
    getBody() { return this.body(); },
    bodyBytes() { return ScriptBuffer.from(value?.bodyBase64 ?? '', 'base64'); },
    callTime() { return Number(value?.callTime ?? 0); },
    toString() { return this.body(); },
  });

  const requestObject = value => {
    if (value === undefined || value === null || value === '') return {};
    if (typeof value === 'string') {
      try { value = parse(value); } catch (_) { throw error('INVALID_ARGUMENT'); }
    }
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      throw error('INVALID_ARGUMENT');
    }
    return value;
  };

  class PortableAbortSignal {
    constructor() {
      this.aborted = false;
      this.reason = undefined;
      this.__listeners = new Set();
    }
    addEventListener(name, listener, options = {}) {
      if (name !== 'abort' || typeof listener !== 'function') return;
      if (this.aborted) {
        listener.call(this, { type:'abort', target:this });
        return;
      }
      this.__listeners.add({ listener, once:Boolean(options && options.once) });
    }
    removeEventListener(name, listener) {
      if (name !== 'abort') return;
      for (const entry of this.__listeners) {
        if (entry.listener === listener) this.__listeners.delete(entry);
      }
    }
    throwIfAborted() {
      if (this.aborted) throw this.reason || error('CANCELLED');
    }
    __abort(reason) {
      if (this.aborted) return;
      this.aborted = true;
      this.reason = reason || error('CANCELLED');
      const entries = [...this.__listeners];
      for (const entry of entries) {
        if (entry.once) this.__listeners.delete(entry);
        try { entry.listener.call(this, { type:'abort', target:this }); } catch (_) {}
      }
      if (typeof this.onabort === 'function') {
        try { this.onabort.call(this, { type:'abort', target:this }); } catch (_) {}
      }
    }
  }
  class PortableAbortController {
    constructor() { this.signal = new PortableAbortSignal(); }
    abort(reason) { this.signal.__abort(reason || error('CANCELLED')); }
  }
  PortableAbortSignal.abort = reason => {
    const controller = new PortableAbortController();
    controller.abort(reason);
    return controller.signal;
  };
  PortableAbortSignal.timeout = milliseconds => {
    const controller = new PortableAbortController();
    setTimeout(() => controller.abort(error('TIMEOUT')), Math.max(0, Number(milliseconds) || 0));
    return controller.signal;
  };
  globalThis.AbortController = PortableAbortController;
  globalThis.AbortSignal = PortableAbortSignal;

  class PortableHeaders {
    constructor(input = {}) {
      this.__values = new Map();
      const entries = input instanceof PortableHeaders ? input.entries()
        : Array.isArray(input) || input && typeof input[Symbol.iterator] === 'function'
          ? input : Object.entries(input || {});
      for (const entry of entries) {
        if (!entry || typeof entry[Symbol.iterator] !== 'function') throw error('INVALID_ARGUMENT');
        const pair = Array.from(entry);
        if (pair.length !== 2) throw error('INVALID_ARGUMENT');
        const [name, raw] = pair;
        const values = Array.isArray(raw) ? raw : [raw];
        for (const value of values) this.append(name, value);
      }
    }
    get(name) { return this.__values.get(String(name).toLowerCase()) ?? null; }
    has(name) { return this.__values.has(String(name).toLowerCase()); }
    set(name, value) { this.__values.set(String(name).toLowerCase(), String(value)); }
    append(name, value) {
      const key = String(name).toLowerCase();
      const previous = this.__values.get(key);
      this.__values.set(key, previous ? previous + ', ' + String(value) : String(value));
    }
    delete(name) { this.__values.delete(String(name).toLowerCase()); }
    entries() { return this.__values.entries(); }
    keys() { return this.__values.keys(); }
    values() { return this.__values.values(); }
    forEach(callback, thisArg) {
      for (const [name, value] of this.__values) callback.call(thisArg, value, name, this);
    }
    [Symbol.iterator]() { return this.entries(); }
    toJSON() { return Object.fromEntries(this.__values); }
  }
  class PortableResponse {
    constructor(value) {
      this.__value = value || {};
      this.status = Number(this.__value.statusCode ?? this.__value.status ?? 0);
      this.statusText = String(this.__value.statusMessage ?? this.__value.statusText ?? '');
      this.url = String(this.__value.url ?? '');
      this.headers = new PortableHeaders(this.__value.headers);
      this.ok = this.status >= 200 && this.status < 300;
      this.redirected = false;
      this.type = 'basic';
      this.bodyUsed = false;
    }
    async text() {
      this.bodyUsed = true;
      return String(this.__value.data ?? this.__value.content ?? this.__value.body ?? '');
    }
    async json() { return parse(await this.text()); }
    async arrayBuffer() {
      this.bodyUsed = true;
      const bytes = this.__value.bodyBase64
        ? ScriptBuffer.from(String(this.__value.bodyBase64), 'base64')
        : ScriptBuffer.from(String(this.__value.data ?? this.__value.content ?? this.__value.body ?? ''));
      return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    }
    clone() { return new PortableResponse(this.__value); }
  }
  const waitWithSignal = (promise, signal) => {
    if (!signal) return promise;
    if (signal.aborted) return Promise.reject(signal.reason || error('CANCELLED'));
    return new Promise((resolve, reject) => {
      const abort = () => reject(signal.reason || error('CANCELLED'));
      signal.addEventListener('abort', abort, { once:true });
      then(then(promise, resolve, reject),
        () => signal.removeEventListener('abort', abort),
        () => signal.removeEventListener('abort', abort));
    });
  };
  globalThis.Headers = PortableHeaders;
  globalThis.Response = PortableResponse;
  globalThis.fetch = async (input, options = {}) => {
    options = options || {};
    const taskId = taskContext();
    const inputUrl = String(input?.url ?? input);
    const url = protocol === 'qysg'
      ? new PortableURL(inputUrl, globalThis.location.href).href : inputUrl;
    const method = String(options.method ?? input?.method ?? 'GET').toUpperCase();
    const headers = new PortableHeaders(options.headers ?? input?.headers ?? {});
    const rawBody = options.body ?? input?.body ?? '';
    let body;
    if (rawBody instanceof PortableURLSearchParams) {
      body = rawBody.toString();
      if (!headers.has('content-type')) {
        headers.set('content-type', 'application/x-www-form-urlencoded;charset=UTF-8');
      }
    } else if (rawBody instanceof ArrayBuffer || ArrayBuffer.isView(rawBody)) {
      body = { __meowBodyBase64:byteView(rawBody).toString('base64') };
    } else {
      body = typeof rawBody === 'string' ? rawBody : String(rawBody ?? '');
    }
    const contentType = headers.get('content-type') || '';
    let request;
    if (protocol === 'qysg') {
      request = capability(taskId, 'bridge.call', {
        name:'http',
        args:[method, url, body, headers.toJSON(), options.redirect !== 'manual', contentType],
      });
    } else if (protocol === 'cat') {
      request = capability(taskId, 'cat.request', {
        url,
        options:{ method, headers:headers.toJSON(), body,
          followRedirect:options.redirect !== 'manual' },
      });
    } else {
      request = capability(taskId, 'legado.request', {
        url, method, headers:headers.toJSON(), body,
        followRedirect:options.redirect !== 'manual',
      });
    }
    return new PortableResponse(await waitWithSignal(request, options.signal ?? input?.signal));
  };

  class JsoupSelection {
    constructor(taskId, values = [], baseUrl = '', exposeIndexes = true) {
      this.__taskId = taskId;
      this.__baseUrl = values instanceof JsoupSelection
        ? values.__baseUrl : String(baseUrl ?? '');
      this.__values = values instanceof JsoupSelection ? values.__values.slice()
        : (Array.isArray(values) ? values : [values]).filter(value => value !== null && value !== undefined);
      if (exposeIndexes) {
        for (let index = 0; index < this.__values.length; index++) this[index] = this.__one(index);
      }
    }
    __one(index) {
      const value = this.__values[index < 0 ? this.__values.length + index : index];
      return value instanceof NodeHandle
        ? new JsoupSelection(this.__taskId, [value], this.__baseUrl, false) : value ?? null;
    }
    get length() { return this.__values.length; }
    size() { return this.__values.length; }
    isEmpty() { return this.__values.length === 0; }
    get(index) { return this.__one(index); }
    first() { return this.__one(0); }
    last() { return this.__one(-1); }
    select(selector) {
      return new JsoupSelection(this.__taskId, this.__values.flatMap(value =>
        value instanceof NodeHandle ? value.querySelectorAll(selector) : []), this.__baseUrl);
    }
    selectFirst(selector) { return this.select(selector).first(); }
    text() {
      return this.__values.map(value =>
        value instanceof NodeHandle ? value.textContent : String(value ?? '')).join(' ').trim();
    }
    ownText() {
      return this.__values.map(value => value instanceof NodeHandle
        ? sync(value.__taskId, 'dom.text', { nodeId:value.__nodeId, direct:true })
        : String(value ?? '')).join(' ').trim();
    }
    html() { return this.__values[0] instanceof NodeHandle ? this.__values[0].innerHTML : ''; }
    outerHtml() {
      return this.__values.map(value =>
        value instanceof NodeHandle ? value.outerHTML : String(value ?? '')).join('');
    }
    attr(name, value) {
      if (arguments.length > 1) {
        for (const item of this.__values) item instanceof NodeHandle && item.setAttribute(name, value);
        return this;
      }
      const item = this.__values[0];
      if (!(item instanceof NodeHandle)) return '';
      const key = String(name);
      if (key.toLowerCase().startsWith('abs:')) {
        const raw = item.getAttribute(key.slice(4)) ?? '';
        if (!raw) return '';
        try { return new PortableURL(raw, this.__baseUrl || undefined).href; }
        catch (_) { return raw; }
      }
      return item.getAttribute(key) ?? '';
    }
    hasAttr(name) {
      return this.__values[0] instanceof NodeHandle && this.__values[0].hasAttribute(name);
    }
    id() { return this.attr('id'); }
    className() { return this.attr('class'); }
    hasClass(name) {
      return this.__values[0] instanceof NodeHandle &&
        this.__values[0].classList.contains(String(name));
    }
    tagName() {
      return this.__values[0] instanceof NodeHandle
        ? String(this.__values[0].tagName).toLowerCase() : '';
    }
    children() {
      return new JsoupSelection(this.__taskId, this.__values.flatMap(value =>
        value instanceof NodeHandle ? value.children : []), this.__baseUrl);
    }
    parent() {
      return new JsoupSelection(this.__taskId,
        this.__values.map(value => value?.parentElement).filter(Boolean), this.__baseUrl);
    }
    parentNode() { return this.parent(); }
    nextElementSibling() {
      const value = this.__values[0];
      return value instanceof NodeHandle && value.nextElementSibling
        ? new JsoupSelection(this.__taskId, [value.nextElementSibling], this.__baseUrl) : null;
    }
    previousElementSibling() {
      const value = this.__values[0];
      return value instanceof NodeHandle && value.previousElementSibling
        ? new JsoupSelection(this.__taskId, [value.previousElementSibling], this.__baseUrl) : null;
    }
    remove() {
      for (const value of this.__values) value instanceof NodeHandle && value.remove();
      return this;
    }
    clone() {
      return new JsoupSelection(this.__taskId, this.__values.map(value =>
        value instanceof NodeHandle ? value.cloneNode(true) : value), this.__baseUrl);
    }
    equals(other) {
      const left = this.__values[0];
      const right = other instanceof JsoupSelection ? other.__values[0] : other;
      return left instanceof NodeHandle && right instanceof NodeHandle
        ? left.isSameNode(right) : left === right;
    }
    getBody() { return this.toString(); }
    toArray() { return this.__values.map((_value, index) => this.__one(index)); }
    toString() { return this.outerHtml() || this.text(); }
    [Symbol.iterator]() { return this.toArray()[Symbol.iterator](); }
  }

  const guarded = (target, path) => new Proxy(target, {
    get(value, property) {
      if (property === Symbol.toStringTag) return path;
      if (property === 'then' && !Object.prototype.hasOwnProperty.call(value, property)) return undefined;
      if (property in value) return value[property];
      throw error('UNSUPPORTED_DEPENDENCY');
    },
    set() { throw error('UNSUPPORTED_DEPENDENCY'); },
  });

  function createJava(taskId, bindings, dom) {
    let currentContent = bindings.result;
    let currentBaseUrl = String(bindings.baseUrl ?? '');
    const ruleContent = value => {
      if (value instanceof NodeHandle) {
        return {
          __meowMarkup:value.outerHTML,
          __meowNodeType:value.nodeType,
        };
      }
      if (value instanceof JsoupSelection) return { __meowMarkup:value.outerHtml() };
      return value;
    };
    const request = async (method, url, body, headers) => {
      const parts = sync(taskId, 'legado.requestParts', { value:url });
      const options = parts.options;
      const response = await capability(taskId, 'legado.request', {
        method:String(options.method ?? method ?? 'GET').toUpperCase(),
        url:parts.url,
        body:options.body ?? (body == null ? '' : body),
        headers:{ ...requestObject(options.headers), ...requestObject(headers) },
        contentType:options.contentType,
        charset:options.charset,
        timeout:options.timeout,
        followRedirect:options.followRedirect ?? options.followRedirects,
        follow_max:options.follow_max,
      });
      const type = String(options.type ?? '').toLowerCase();
      if (type === 'hex' || type === 'base64') {
        const raw = String(response?.bodyBase64 ?? response?.body ?? '');
        const data = type === 'hex'
          ? ScriptBuffer.from(raw, 'base64').toString('hex') : raw;
        return makeResponse({ ...response, data, body:data });
      }
      return makeResponse(response);
    };
    const runtimeInfo = () => sync(taskId, 'legado.runtimeInfo', {});
    const variable = key => {
      const value = sync(taskId, 'legado.variableGet', { key:String(key) });
      return value === null || value === undefined
        ? sync(taskId, 'legado.sourceGet', { key:String(key) }) : value;
    };
    const java = {
      ajax: async (url, headers) => (await request('GET', url, '', headers)).body(),
      aJax: async (url, headers) => (await request('GET', url, '', headers)).body(),
      ajaxAll: async urls => Promise.all(Array.from(urls || []).map(url => request('GET', url, '', {}))),
      get: async (value, headers) => {
        const text = String(value ?? '');
        if (headers !== undefined || value && typeof value === 'object' ||
            /^(?:https?|https?2):\/\//i.test(text) || text.startsWith('/')) {
          return request('GET', value, '', headers);
        }
        if (text === 'bookName') return String(bindings.book?.name ?? '');
        if (text === 'title') return String(bindings.chapter?.title ?? bindings.chapter?.name ?? '');
        return variable(text);
      },
      head: async (url, headers) => request('HEAD', url, '', headers),
      post: async (url, body, headers) => request('POST', url, body, headers),
      postForm: async (url, body, headers = {}) =>
        request('POST', url, body, { 'Content-Type':'application/x-www-form-urlencoded', ...headers }),
      postJson: async (url, body, headers = {}) =>
        request('POST', url, typeof body === 'string' ? body : JSON.stringify(body),
          { 'Content-Type':'application/json', ...headers }),
      fetch: async (url, options = {}) => request(
        String(options.method ?? 'GET').toUpperCase(), url, options.body ?? options.data ?? '',
        options.headers ?? {},
      ),
      connect: async (url, headers) => request('GET', url, '', headers),
      connectUrl: async (url, headers) => (await request('GET', url, '', headers)).url,
      getString: (rule, content = currentContent) =>
        sync(taskId, 'legado.ruleString', { rule:String(rule), content:ruleContent(content) }),
      getElement: (rule, content = currentContent) => java.getElements(rule, content).first() ?? '',
      getElements: (rule, content = currentContent) => new JsoupSelection(taskId,
        sync(taskId, 'legado.ruleValues', {
          rule:String(rule), content:ruleContent(content),
        }).map(value => hydrateValue(taskId, value)), currentBaseUrl),
      getStringList: (rule, content = currentContent) =>
        java.getElements(rule, content).toArray().map(String),
      setContent: (content, url = currentBaseUrl) => {
        currentContent = content;
        currentBaseUrl = String(url ?? '');
        return content;
      },
      put: (key, value) => {
        sync(taskId, 'legado.variableSet', { key:String(key), value });
        return value;
      },
      getVariable: variable,
      getCookie: async (url, name = '') => {
        const header = await capability(taskId, 'legado.cookieGet', { url:String(url) });
        if (!name) return header;
        const item = String(header).split(';').map(value => value.trim())
          .find(value => value.startsWith(String(name) + '='));
        return item ? item.slice(String(name).length + 1) : '';
      },
      clearCookie: async url => capability(taskId, 'legado.cookieClear', { url:String(url) }),
      setCookie: async (url, value) =>
        capability(taskId, 'legado.cookieSet', { url:String(url), value:String(value) }),
      removeCookie: async url => capability(taskId, 'legado.cookieClear', { url:String(url) }),
      log: () => undefined,
      toast: () => undefined,
      longToast: () => undefined,
      md5Encode: value => call('util', 'md5', [ScriptBuffer.from(String(value))]),
      md5Encode16: value => call('util', 'md5', [ScriptBuffer.from(String(value))]).slice(8, 24),
      base64Encode: value => ScriptBuffer.from(String(value)).toString('base64'),
      base64Decode: value => ScriptBuffer.from(String(value), 'base64').toString(),
      base64DecodeToString: value => ScriptBuffer.from(String(value), 'base64').toString(),
      base64DecodeToByteArray: value => ScriptBuffer.from(String(value), 'base64'),
      hexEncodeToString: value => ScriptBuffer.from(value).toString('hex'),
      hexDecodeToByteArray: value => ScriptBuffer.from(String(value), 'hex'),
      hexDecodeToString: (value, encoding = 'utf8') =>
        ScriptBuffer.from(String(value), 'hex').toString(encoding),
      strToBytes: (value, encoding = 'utf8') => ScriptBuffer.from(String(value), encoding),
      bytesToStr: (value, encoding = 'utf8') => ScriptBuffer.from(value).toString(encoding),
      digestHex: (value, algorithm = 'MD5') =>
        call('util', 'digest', [String(algorithm).toLowerCase().replace('-', ''), byteView(value)]),
      encodeURI: value => encodeURI(String(value)),
      urlEncode: value => encodeURIComponent(String(value)),
      urlDecode: value => { try { return decodeURIComponent(String(value)); } catch (_) { return String(value); } },
      randomUUID: () => {
        const bytes = ScriptBuffer.from(call('util', 'random', [16]));
        bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
        const hex = Array.from(bytes, value => value.toString(16).padStart(2,'0')).join('');
        return hex.slice(0,8)+'-'+hex.slice(8,12)+'-'+hex.slice(12,16)+'-'+hex.slice(16,20)+'-'+hex.slice(20);
      },
      startBrowser: async (url, title) => capability(taskId, 'legado.web', { mode:'browser', url:String(url), title:String(title || '') }),
      startBrowserAwait: async (url, title) => capability(taskId, 'legado.web', { mode:'browser', url:String(url), title:String(title || '') }),
      showBrowser: async (url, title) => capability(taskId, 'legado.web', { mode:'browser', url:String(url), title:String(title || '') }),
      openUrl: async url => capability(taskId, 'legado.web', { mode:'openUrl', url:String(url) }),
      getVerificationCode: async (url, headers) =>
        capability(taskId, 'legado.web', { mode:'verificationCode', url:String(url || ''), headers:headers || {} }),
      webView: async (html, url, script, sourceRegex, overrideUrlRegex, delayTime) =>
        capability(taskId, 'legado.web', { mode:'webview', html:String(html || ''), url:String(url || ''),
          script:String(script || ''), sourceRegex:String(sourceRegex || ''), overrideUrlRegex:String(overrideUrlRegex || ''),
          delayTime:Number(delayTime || 0) }),
      getWebViewUA: () => runtimeInfo().userAgent,
      androidId: () => runtimeInfo().deviceId,
      deviceID: () => runtimeInfo().deviceId,
      getAppVariant: () => runtimeInfo().appVariant,
      htmlFormat: value => sync(taskId, 'legado.htmlToText', { value:String(value ?? '') }),
      timeFormat: (value, pattern = 'yyyy-MM-dd HH:mm') => {
        const date = new Date(Number(value));
        if (!Number.isFinite(date.getTime())) return '';
        const pad = number => String(number).padStart(2, '0');
        return String(pattern)
          .replace(/yyyy/g, String(date.getFullYear()))
          .replace(/MM/g, pad(date.getMonth() + 1))
          .replace(/dd/g, pad(date.getDate()))
          .replace(/HH/g, pad(date.getHours()))
          .replace(/mm/g, pad(date.getMinutes()))
          .replace(/ss/g, pad(date.getSeconds()));
      },
      timeFormatUTC: (value, pattern = 'yyyy-MM-dd HH:mm') => {
        const date = new Date(Number(value));
        if (!Number.isFinite(date.getTime())) return '';
        const pad = number => String(number).padStart(2, '0');
        return String(pattern)
          .replace(/yyyy/g, String(date.getUTCFullYear()))
          .replace(/MM/g, pad(date.getUTCMonth() + 1))
          .replace(/dd/g, pad(date.getUTCDate()))
          .replace(/HH/g, pad(date.getUTCHours()))
          .replace(/mm/g, pad(date.getUTCMinutes()))
          .replace(/ss/g, pad(date.getUTCSeconds()));
      },
      getBookSourceUrl: () => String(bindings.source?.bookSourceUrl ?? ''),
      getFromMemory: key => sync(taskId, 'legado.variableGet', { key:String(key) }),
      putMemory: (key, value) => {
        sync(taskId, 'legado.variableSet', { key:String(key), value });
        return value;
      },
    };
    const cookie = {
      getCookie: async url => capability(taskId, 'legado.cookieGet', { url:String(url) }),
      getKey: async (url, name) => {
        const header = await cookie.getCookie(url);
        const item = String(header).split(';').map(value => value.trim()).find(value => value.startsWith(String(name)+'='));
        return item ? item.slice(String(name).length + 1) : '';
      },
      setCookie: async (url, value) => capability(taskId, 'legado.cookieSet', { url:String(url), value:String(value) }),
      removeCookie: async url => capability(taskId, 'legado.cookieClear', { url:String(url) }),
    };
    const cache = {
      get: async key => capability(taskId, 'legado.storageGet', { key:String(key) }),
      put: async (key, value) => capability(taskId, 'legado.storageSet', { key:String(key), value }),
      delete: async key => capability(taskId, 'legado.storageDelete', { key:String(key) }),
      remove: async key => capability(taskId, 'legado.storageDelete', { key:String(key) }),
      getFromMemory: key => sync(taskId, 'legado.variableGet', { key:String(key) }),
      putMemory: (key, value) => {
        sync(taskId, 'legado.variableSet', { key:String(key), value });
        return value;
      },
      deleteMemory: key => {
        sync(taskId, 'legado.variableSet', { key:String(key), value:null });
        return true;
      },
    };
    const source = Object.assign({}, bindings.source || {});
    source.getVariable = () => sync(taskId, 'legado.sourceVariableGet', {});
    source.setVariable = value =>
      sync(taskId, 'legado.sourceVariableSet', { value:String(value ?? '') });
    source.putVariable = source.setVariable;
    source.get = key => sync(taskId, 'legado.sourceGet', { key:String(key) });
    source.put = (key, value) => sync(taskId, 'legado.sourceSet', { key:String(key), value });
    source.getBookSourceUrl = () => String(source.bookSourceUrl ?? '');
    source.getLoginInfo = async () => capability(taskId, 'legado.storageGet', { key:'loginInfo' });
    source.getLoginInfoMap = async () => {
      const value = await source.getLoginInfo();
      if (value && typeof value === 'object') return value;
      try { return JSON.parse(String(value || '{}')); } catch (_) { return {}; }
    };
    source.putLoginInfo = async value =>
      capability(taskId, 'legado.storageSet', { key:'loginInfo', value });
    source.removeLoginInfo = async () =>
      capability(taskId, 'legado.storageDelete', { key:'loginInfo' });
    source.getLoginHeader = async () =>
      capability(taskId, 'legado.storageGet', { key:'loginHeader' });
    source.getLoginHeaderMap = async () => {
      const value = await source.getLoginHeader();
      if (value && typeof value === 'object') return value;
      try { return JSON.parse(String(value || '{}')); } catch (_) { return {}; }
    };
    source.putLoginHeader = async value =>
      capability(taskId, 'legado.storageSet', { key:'loginHeader', value });
    source.removeLoginHeader = async () =>
      capability(taskId, 'legado.storageDelete', { key:'loginHeader' });
    const book = Object.assign({}, bindings.book || {});
    book.getVariable = key => sync(taskId, 'legado.bookVariableGet', { key:String(key || 'custom') });
    book.setVariable = (key, value) => {
      if (value === undefined) { value = key; key = 'custom'; }
      return sync(taskId, 'legado.bookVariableSet', { key:String(key), value });
    };
    book.putVariable = book.setVariable;
    const Jsoup = {
      parse(value, baseUrl = currentBaseUrl) {
        const parsed = sync(taskId, 'dom.parse', { html:String(value ?? '') });
        return new JsoupSelection(taskId,
          [new NodeHandle(taskId, parsed.rootId)], String(baseUrl ?? ''));
      },
      connect(url) {
        const state = { url:String(url), headers:{}, body:'', method:'GET' };
        const builder = {
          header(key, value) { state.headers[String(key)] = String(value); return builder; },
          headers(value) { Object.assign(state.headers, value || {}); return builder; },
          data(value) { state.body = typeof value === 'string' ? value : JSON.stringify(value); return builder; },
          ignoreContentType() { return builder; },
          userAgent(value) { state.headers['User-Agent'] = String(value); return builder; },
          timeout() { return builder; },
          get: async () => {
            const response = await request('GET', state.url, '', state.headers);
            return Jsoup.parse(response.body(), response.url || state.url);
          },
          post: async () => {
            const response = await request('POST', state.url, state.body, state.headers);
            return Jsoup.parse(response.body(), response.url || state.url);
          },
          execute: async () => request(state.method, state.url, state.body, state.headers),
        };
        return builder;
      },
    };
    const Parser = {
      unescapeEntities(value) {
        const parsed = Jsoup.parse('<textarea>' + String(value ?? '') + '</textarea>');
        return parsed.selectFirst('textarea')?.text() ?? '';
      },
    };
    const jsoup = guarded({
      Jsoup,
      parser:guarded({ Parser }, 'org.jsoup.parser'),
      select:guarded({ Elements:JsoupSelection }, 'org.jsoup.select'),
    }, 'org.jsoup');
    const org = guarded({ jsoup }, 'org');
    const Packages = guarded({ org }, 'Packages');
    const Java = guarded({
      type(name) {
        if (String(name) === 'org.jsoup.Jsoup') return Jsoup;
        throw error('UNSUPPORTED_DEPENDENCY');
      },
    }, 'Java');
    return {
      java:guarded(java, 'java'), cookie:guarded(cookie, 'cookie'),
      cache:guarded(cache, 'cache'), source, book, chapter:bindings.chapter || {},
      org, Jsoup, Packages, Java,
    };
  }

  function createCryptoJS() {
    const fromWords = (words, sigBytes = words.length * 4) => {
      const value = {
        words:Array.from(words || [], item => Number(item) | 0),
        sigBytes:size(Number(sigBytes)),
        concat(child) {
          const combined = ScriptBuffer.concat([bytesOf(this), bytesOf(child)]);
          const next = word(combined);
          this.words = next.words;
          this.sigBytes = next.sigBytes;
          return this;
        },
        clamp() {
          const remainder = this.sigBytes % 4;
          if (remainder && this.words.length) {
            const index = this.sigBytes >>> 2;
            this.words[index] &= 0xffffffff << (32 - remainder * 8);
          }
          this.words.length = Math.ceil(this.sigBytes / 4);
          return this;
        },
        clone() { return fromWords(this.words.slice(), this.sigBytes); },
        toString(encoder) {
          const bytes = bytesOf(this);
          if (encoder === CryptoJS.enc.Base64) return bytes.toString('base64');
          if (encoder === CryptoJS.enc.Utf8) return bytes.toString('utf8');
          if (encoder === CryptoJS.enc.Latin1) return bytes.toString('latin1');
          return bytes.toString('hex');
        },
      };
      return value;
    };
    const word = bytes => {
      const input = ScriptBuffer.from(bytes);
      const words = [];
      for (let index = 0; index < input.length; index++) {
        words[index >>> 2] = (words[index >>> 2] || 0) |
          input[index] << (24 - index % 4 * 8);
      }
      return fromWords(words, input.length);
    };
    const bytesOf = value => {
      if (value && Array.isArray(value.words)) {
        const length = size(Number(value.sigBytes ?? value.words.length * 4));
        const output = ScriptBuffer.alloc(length);
        for (let index = 0; index < length; index++) {
          output[index] = value.words[index >>> 2] >>> (24 - index % 4 * 8) & 255;
        }
        return output;
      }
      return typeof value === 'string' ? ScriptBuffer.from(value) : byteView(value);
    };
    const encoder = encoding => ({
      parse:value => word(ScriptBuffer.from(String(value), encoding)),
      stringify:value => bytesOf(value).toString(encoding),
    });
    const hash = algorithm => value =>
      word(ScriptBuffer.from(call('util','digest',[algorithm,bytesOf(value)]),'hex'));
    const hmac = algorithm => (value, key) =>
      word(ScriptBuffer.from(call('util','hmac',[algorithm,bytesOf(key),bytesOf(value),'hex']),'hex'));
    const cipherParameters = bytes => ({
      ciphertext:word(bytes),
      toString(encoder) {
        return encoder?.stringify
          ? encoder.stringify(this.ciphertext)
          : this.ciphertext.toString(CryptoJS.enc.Base64);
      },
    });
    const cipherMode = (key, options = {}) => {
      const bytes = bytesOf(key);
      const mode = options.mode === CryptoJS.mode.ECB ? 'ecb' : 'cbc';
      if (![16, 24, 32].includes(bytes.length)) throw error('UNSUPPORTED_CRYPTO');
      if (options.padding && options.padding !== CryptoJS.pad.Pkcs7) throw error('UNSUPPORTED_CRYPTO');
      const iv = mode === 'ecb' ? ScriptBuffer.alloc(0) : bytesOf(options.iv ?? ScriptBuffer.alloc(16));
      if (mode === 'cbc' && iv.length !== 16) throw error('UNSUPPORTED_CRYPTO');
      return { algorithm:'aes-' + (bytes.length * 8) + '-' + mode, key:bytes, iv };
    };
    const desMode = (key, options = {}) => {
      const bytes = bytesOf(key);
      const mode = options.mode === CryptoJS.mode.CBC ? 'cbc' : 'ecb';
      if (bytes.length !== 8) throw error('UNSUPPORTED_CRYPTO');
      if (options.padding && options.padding !== CryptoJS.pad.Pkcs7) throw error('UNSUPPORTED_CRYPTO');
      const iv = mode === 'ecb' ? ScriptBuffer.alloc(0) : bytesOf(options.iv ?? ScriptBuffer.alloc(8));
      if (mode === 'cbc' && iv.length !== 8) throw error('UNSUPPORTED_CRYPTO');
      return { algorithm:'des-' + mode, key:bytes, iv };
    };
    const CryptoJS = {
      enc:{ Hex:encoder('hex'), Base64:encoder('base64'), Utf8:encoder('utf8'), Latin1:encoder('latin1') },
      lib:{ WordArray:{
        create:(value = [], sigBytes) => Array.isArray(value)
          ? fromWords(value, sigBytes) : word(value),
        random:length => word(call('util','random',[size(length)])),
      } },
      mode:{ CBC:Object.freeze({ name:'CBC' }), ECB:Object.freeze({ name:'ECB' }) },
      pad:{ Pkcs7:Object.freeze({ name:'Pkcs7' }) },
      MD5:hash('md5'),
      SHA1:hash('sha1'),
      SHA256:hash('sha256'),
      SHA512:hash('sha512'),
      HmacMD5:hmac('md5'),
      HmacSHA1:hmac('sha1'),
      HmacSHA256:hmac('sha256'),
      HmacSHA512:hmac('sha512'),
    };
    CryptoJS.AES = {
      encrypt(value, key, options) {
        const mode = cipherMode(key, options);
        return cipherParameters(call('util','aes',[bytesOf(value),mode.algorithm,mode.key,mode.iv]));
      },
      decrypt(value, key, options) {
        const mode = cipherMode(key, options);
        const input = typeof value === 'string'
          ? ScriptBuffer.from(value, 'base64') : bytesOf(value?.ciphertext ?? value);
        return word(call('util','aesDecrypt',[input,mode.algorithm,mode.key,mode.iv]));
      },
    };
    CryptoJS.DES = {
      encrypt(value, key, options) {
        const mode = desMode(key, options);
        return cipherParameters(call('util','des',[bytesOf(value),mode.algorithm,mode.key,mode.iv]));
      },
      decrypt(value, key, options) {
        const mode = desMode(key, options);
        const input = typeof value === 'string'
          ? ScriptBuffer.from(value, 'base64') : bytesOf(value?.ciphertext ?? value);
        return word(call('util','desDecrypt',[input,mode.algorithm,mode.key,mode.iv]));
      },
    };
    return CryptoJS;
  }
  const CryptoJS = createCryptoJS();
  globalThis.CryptoJS = CryptoJS;
  const sha1 = value => call('util', 'digest', ['sha1', ScriptBuffer.from(String(value ?? ''))]);
  sha1.hex = sha1;
  sha1.array = value => Array.from(ScriptBuffer.from(sha1(value), 'hex'));
  sha1.arrayBuffer = value => ScriptBuffer.from(sha1(value), 'hex').buffer;
  globalThis.sha1 = sha1;
  globalThis.crypto = Object.freeze({
    getRandomValues(value) {
      if (!ArrayBuffer.isView(value) || value instanceof DataView || value.byteLength > 65536) {
        throw error('INVALID_ARGUMENT');
      }
      new Uint8Array(value.buffer, value.byteOffset, value.byteLength)
        .set(call('util', 'random', [size(value.byteLength)]));
      return value;
    },
  });

  function createCommonScope(metadata = {}) {
    const dom = createDomScope();
    const callHandler = (name, ...args) =>
      capability(taskContext(), 'bridge.call', { name:String(name), args });
    const window = globalThis;
    window.window = window;
    window.global = window;
    window.self = window;
    window.navigator = { userAgent:'Mozilla/5.0 MeowfilmSourceRuntime/1' };
    let locationUrl;
    try { locationUrl = new PortableURL(metadata.baseUrl || 'about:blank'); }
    catch (_) { locationUrl = new PortableURL('about:blank'); }
    const location = {
      assign(value) { locationUrl.href = value; },
      replace(value) { locationUrl.href = value; },
      reload() {},
      toString() { return locationUrl.href; },
    };
    for (const name of ['href','protocol','username','password','hostname','port','host',
      'origin','pathname','search','hash']) {
      const descriptor = {
        enumerable:true,
        get() { return locationUrl[name]; },
      };
      if (name !== 'origin') descriptor.set = value => { locationUrl[name] = value; };
      Object.defineProperty(location, name, descriptor);
    }
    window.location = location;
    const performanceOrigin = Date.now();
    window.performance = Object.freeze({ now:() => Date.now() - performanceOrigin });
    window.matchMedia = query => ({
      matches:false,
      media:String(query ?? ''),
      onchange:null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent() { return true; },
    });
    window.addEventListener = () => {};
    window.removeEventListener = () => {};
    window.dispatchEvent = () => true;
    window.Event = class {
      constructor(type) { this.type = String(type); }
      preventDefault() { this.defaultPrevented = true; }
      stopPropagation() {}
    };
    window.CustomEvent = class extends window.Event {
      constructor(type, options = {}) { super(type); this.detail = options.detail; }
    };
    window.flutter_inappwebview = { callHandler };
    window.document = dom.document;
    window.DOMParser = dom.DOMParser;
    window.$ = window.jQuery = dom.dollar;
    return { ...dom, window, callHandler };
  }

  function normalizeResult(value, seen = new Set()) {
    if (value instanceof NodeHandle) {
      return {
        __meowMarkup:value.outerHTML,
        __meowNodeType:value.nodeType,
      };
    }
    if (value && typeof value === 'object') {
      if (value instanceof ScriptBuffer) return value.toJSON();
      if (seen.has(value)) throw error('SCRIPT_EXCEPTION');
      seen.add(value);
      try {
        const values = value instanceof MiniQuery ? value.toArray()
          : value instanceof JsoupSelection ? value.__values : value;
        if (Array.isArray(values)) return values.map(child => normalizeResult(child, seen));
        const output = {};
        for (const [key, child] of Object.entries(values)) {
          if (typeof child !== 'function') output[key] = normalizeResult(child, seen);
        }
        return output;
      } finally {
        seen.delete(value);
      }
    }
    if (typeof value === 'bigint') throw error('SCRIPT_EXCEPTION');
    return value;
  }

  function publishResult(id, value) {
    try { call('result', id, true, normalizeResult(value)); }
    catch (failure) { call('result', id, false, errorCode(failure)); }
  }

  function hydrateValue(taskId, value) {
    if (Array.isArray(value)) return value.map(child => hydrateValue(taskId, child));
    if (value && typeof value === 'object') {
      const keys = Object.keys(value);
      const encodedNode = typeof value.__meowMarkup === 'string' &&
        keys.every(key => key === '__meowMarkup' || key === '__meowNodeType');
      if (encodedNode) {
        const parsed = sync(taskId, 'dom.parse', { html:value.__meowMarkup });
        const root = new NodeHandle(taskId, parsed.rootId);
        if (Number.isInteger(value.__meowNodeType) && value.__meowNodeType !== 9) {
          return root.childNodes.find(node => node.nodeType === value.__meowNodeType) ?? root;
        }
        return root;
      }
      const output = {};
      for (const [key, child] of Object.entries(value)) output[key] = hydrateValue(taskId, child);
      return output;
    }
    return value;
  }

  function compileProtocol(source, names, metadata) {
    createCommonScope(metadata);
    const capture = names.map(name =>
      JSON.stringify(name)+':(typeof '+name+'==="undefined"?undefined:'+name+')').join(',');
    const module = { exports:{} };
    globalThis.module = module;
    globalThis.exports = module.exports;
    globalThis.__meowModule = module;
    try {
      const factory = new Function(source + '\n;return {' + capture +
        ',__module:globalThis.__meowModule.exports};');
      return factory();
    } finally {
      delete globalThis.__meowModule;
    }
  }

  function installCatScope() {
    const currentTask = () => taskContext();
    globalThis.req = globalThis.request = async (url, options = {}) =>
      capability(currentTask(), 'cat.request', { url:String(url), options });
    globalThis.post = async (url, options = {}) =>
      capability(currentTask(), 'cat.request', {
        url:String(url), options:{ ...options, method:'POST' },
      });
    globalThis.batchFetch = async requests => Promise.all(Array.from(requests || []).map(request =>
      globalThis.req(request.url || request, request.options || {})));
    globalThis.local = {
      get: key => capability(currentTask(), 'cat.storageGet', { key:String(key) }),
      set: (key, value) => capability(currentTask(), 'cat.storageSet', { key:String(key), value }),
      delete: key => capability(currentTask(), 'cat.storageDelete', { key:String(key) }),
    };
    const catRule = rule => {
      const parts = String(rule ?? '').split('&&').filter(Boolean);
      if (parts[0]?.toLowerCase() === 'body') parts.shift();
      let attribute = '';
      if (parts.length > 1 && /^(?:text|textnodes|html|outerhtml|all|[A-Za-z_:][\w:.-]*)$/i
        .test(parts.at(-1))) attribute = parts.pop();
      return { selector:parts.join(' ') || ':scope', attribute };
    };
    globalThis.pdfa = (html, rule) => {
      const root = html instanceof NodeHandle ? html : (() => {
        const owner = currentTask();
        const parsed = sync(owner, 'dom.parse', { html:String(html ?? '') });
        return new NodeHandle(owner, parsed.rootId);
      })();
      const { selector } = catRule(rule);
      return selector === ':scope' ? [root] : root.querySelectorAll(selector);
    };
    globalThis.pdfh = (html, rule) => {
      const { attribute } = catRule(rule);
      const values = globalThis.pdfa(html, rule);
      const value = values[0];
      if (!(value instanceof NodeHandle)) return String(value ?? '');
      if (!attribute || /^text$/i.test(attribute)) return value.textContent;
      if (/^textnodes$/i.test(attribute)) {
        return sync(value.__taskId, 'dom.text', { nodeId:value.__nodeId, direct:true });
      }
      if (/^html$/i.test(attribute)) return value.innerHTML;
      if (/^(?:outerhtml|all)$/i.test(attribute)) return value.outerHTML;
      return value.getAttribute(attribute) ?? '';
    };
    globalThis.pd = (html, rule, base) => {
      const value = globalThis.pdfh(html, rule);
      try { return new URL(value, base).href; } catch (_) { return value; }
    };
    globalThis.js2Proxy = (_dynamic, _siteType, siteKey, url, headers) =>
      sync(currentTask(), 'cat.virtualUrl', {
        siteKey:String(siteKey || ''), url:String(url || ''), headers:headers || {},
      });
    globalThis.urljoin = globalThis.joinUrl = (base, value) => {
      try { return new URL(String(value ?? ''), String(base ?? '')).href; }
      catch (_) { return String(value ?? ''); }
    };
    globalThis.md5 = value => call('util', 'md5', [ScriptBuffer.from(String(value ?? ''))]);
  }

  async function dispatchProtocol(action, payload) {
    if (!exportsObject) throw error('SCRIPT_PROTOCOL_ERROR');
    if (protocol === 'qysg') {
      const table = {
        search:[payload.keyword ?? payload.key ?? '', payload.page ?? 1],
        info:[payload.bookUrl ?? payload.url ?? ''],
        chapter:[payload.tocUrl ?? payload.url ?? '', payload.bookUrl ?? ''],
        content:[payload.chapterUrl ?? payload.url ?? '', payload.bookUrl ?? ''],
        getfinds:[],
        find:[payload.url ?? '', payload.page ?? 1],
        login:payload.arguments ?? [],
        getloginurl:[],
        imagedecrypt:[payload.url ?? '', payload.body ?? payload.image ?? ''],
        shouldOverrideUrlLoading:[payload.url ?? ''],
        pay:[payload.bookUrl ?? '', payload.url ?? ''],
        gethelp:[],
      };
      const args = table[action];
      if (!args || typeof exportsObject[action] !== 'function') throw error('CAPABILITY_UNAVAILABLE');
      return exportsObject[action](...args);
    }
    if (protocol === 'cat') {
      const spider = exportsObject.spider || exportsObject;
      const table = {
        init:['init',[payload.ext ?? payload]],
        home:['homeContent',[payload.filter ?? true]],
        homeVideo:['homeVideoContent',[]],
        category:['categoryContent',[payload.id ?? payload.tid ?? '', String(payload.page ?? 1),
          payload.filter ?? false, payload.extend ?? {}]],
        search:['searchContent',[payload.keyword ?? payload.key ?? '', payload.quick ?? false, String(payload.page ?? 1)]],
        detail:['detailContent',[Array.isArray(payload.ids) ? payload.ids : [payload.id ?? payload.url ?? '']]],
        play:['playerContent',[payload.flag ?? '', payload.id ?? payload.url ?? '', payload.vipFlags ?? []]],
        proxy:['proxy',null],
        action:['action',[payload.action ?? '', payload.value ?? null]],
        destroy:['destroy',[]],
      };
      const [method, configuredArgs] = table[action] || [];
      const fn = spider?.[method] || spider?.[action] || exportsObject[method] || exportsObject[action];
      if (typeof fn !== 'function') throw error('CAPABILITY_UNAVAILABLE');
      const args = action === 'proxy'
        ? fn.length >= 2
          ? [payload.segments ?? payload.params ?? payload, payload.headers ?? {}]
          : [payload]
        : configuredArgs;
      installCatScope();
      return fn.apply(spider, args);
    }
    throw error('UNSUPPORTED_PROTOCOL');
  }

  const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
  return realm = Object.freeze({
    errorCode,
    watchStartup(value) {
      if (value && typeof value.then === 'function') then(Promise.resolve(value),
        undefined, failure => call('startupFailure', errorCode(failure)));
    },
    load(kind, source, metadataJSON) {
      try {
        protocol = kind;
        const metadata = parse(metadataJSON);
        if (kind === 'cat') installCatScope();
        if (kind === 'qysg' && !('toc' in globalThis)) globalThis.toc = undefined;
        exportsObject = kind === 'legado'
          ? {}
          : compileProtocol(source, metadata.entries || [], metadata);
        if (kind === 'cat') {
          let spider = exportsObject.__meowCat || exportsObject.__JS_SPIDER__
            || exportsObject.__jsEvalReturn || exportsObject.spider || exportsObject.Spider || exportsObject.rule
            || exportsObject.__module?.default || exportsObject.__module;
          if (spider === exportsObject.__jsEvalReturn) spider = spider();
          if (typeof spider === 'function') {
            const methods = Object.getOwnPropertyNames(spider.prototype || {})
              .filter(name => name !== 'constructor');
            if (methods.length) spider = new spider();
            else {
              const value = spider();
              if (value && typeof value === 'object') spider = value;
            }
          }
          if (!spider || typeof spider !== 'object') throw error('SCRIPT_PROTOCOL_ERROR');
          exportsObject = { ...exportsObject, spider };
        }
        const actions = (metadata.actions || []).filter(name => {
          if (kind === 'legado') return true;
          if (kind === 'cat') {
            const methods = {
              init:'init', home:'homeContent', homeVideo:'homeVideoContent',
              category:'categoryContent', search:'searchContent', detail:'detailContent',
              play:'playerContent', proxy:'proxy', action:'action', destroy:'destroy',
            };
            return typeof exportsObject.spider?.[methods[name]] === 'function'
              || typeof exportsObject.spider?.[name] === 'function'
              || typeof exportsObject[methods[name]] === 'function'
              || typeof exportsObject[name] === 'function';
          }
          return typeof exportsObject[name] === 'function';
        });
        const announce = () => {
          const waiting = [...startupPromises];
          if (waiting.length) {
            then(Promise.allSettled(waiting), announce,
              failure => call('startupFailure', errorCode(failure)));
            return;
          }
          then(Promise.resolve(), () => {
            if (startupPromises.size) announce();
            else call('ready', { actions });
          });
        };
        announce();
      } catch (failure) {
        call('startupFailure', errorCode(failure));
      }
    },
    invoke(id, action, payloadJSON) {
      runTask(id, () => then(then(Promise.resolve(), () =>
        dispatchProtocol(action, parse(payloadJSON))),
        value => publishResult(id, value),
        failure => call('result', id, false, errorCode(failure)),
      ));
    },
    evaluate(id, source, bindingsJSON) {
      runTask(id, () => then(then(Promise.resolve(), async () => {
        const bindings = hydrateValue(id, parse(bindingsJSON));
        const dom = createDomScope();
        const built = createJava(id, bindings, dom);
        const scope = Object.assign(Object.create(null), bindings, built, {
          JavaImporter:function(){ throw error('UNSUPPORTED_DEPENDENCY'); },
          importClass:function(){ throw error('UNSUPPORTED_DEPENDENCY'); },
          importPackage:function(){ throw error('UNSUPPORTED_DEPENDENCY'); },
          Buffer:ScriptBuffer, CryptoJS, DOMParser:dom.DOMParser, console:globalThis.console,
        });
        const names = ['java','cookie','cache','source','book','chapter','org','Jsoup',
          'result','baseUrl','key','page','Packages','Java','JavaImporter',
          'importClass','importPackage'];
        const previous = new Map(names.map(name => [name, {
          own:Object.prototype.hasOwnProperty.call(globalThis, name),
          value:globalThis[name],
        }]));
        for (const name of names) if (name in scope) globalThis[name] = scope[name];
        try {
          const fn = new AsyncFunction('scope', 'with(scope){\n' + source + '\n}');
          return await fn.call(scope, scope);
        } finally {
          for (const [name, state] of previous) {
            if (state.own) globalThis[name] = state.value;
            else delete globalThis[name];
          }
        }
      }),
        value => publishResult(id, value),
        failure => call('result', id, false, errorCode(failure)),
      ));
    },
    settle(id, payloadJSON) {
      const item = pending.get(id);
      if (!item) return;
      pending.delete(id);
      const value = parse(payloadJSON);
      runTask(item.taskId, () => {
        if (value.ok) item.resolve(hydrateValue(item.taskId, value.value));
        else item.reject(error(value.code));
      });
      call('callbackDone', id);
    },
    callback(id) {
      const item = callbacks.get(id);
      if (!item) return;
      if (!item.repeat) callbacks.delete(id);
      runTask(item.taskId, () => then(then(Promise.resolve(), () => item.fn(...item.args)),
        () => call('callbackDone', id),
        failure => call('callbackFailure', id, errorCode(failure)),
      ));
    },
    forget(id) { pending.delete(id); callbacks.delete(id); },
    releaseTask(id) {
      documentRoots.delete(id);
      for (const [key, item] of pending) if (item.taskId === id) pending.delete(key);
      for (const [key, item] of callbacks) if (item.taskId === id) callbacks.delete(key);
    },
  });
})()
`;
