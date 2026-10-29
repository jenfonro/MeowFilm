/**
 * Executed only inside our VM, not imported into the shared realm.
 * Buffer's prototype and all SDK objects are per-context. The bridge is still
 * NOT a security sandbox: host callbacks and a shared event loop remain.
 */
export const realmSource = String.raw`
(() => {
  'use strict';
  const bridge = globalThis.__meowBridge;
  delete globalThis.__meowBridge;
  const parse = JSON.parse.bind(JSON);
  const freeze = Object.freeze;
  const error = data => {
    const e = new Error(data.message || data.code);
    e.code = data.code;
    e.name = data.code === 'CANCELLED' ? 'AbortError' : 'Error';
    bridge('rememberError',[e,e.code]);
    return e;
  };
  const call = (op, ...args) => {
    try { return bridge(op, args); }
    catch (e) { throw error({code:e.code || 'SCRIPT_EXCEPTION', message:e.message}); }
  };
  const size = n => {
    if (!Number.isSafeInteger(n) || n < 0 || n > call('maxBytes')) throw error({code:'RESOURCE_LIMIT'});
    return n;
  };
  class ScriptBuffer extends Uint8Array {
    static from(input, encoding = 'utf8', length) {
      if (typeof input === 'string') return new ScriptBuffer(call('util', 'encode', [input, encoding]));
      if (input instanceof ArrayBuffer) {
        const offset = typeof encoding === 'number' ? encoding : 0;
        const count = length === undefined ? input.byteLength - offset : length;
        size(count);
        return new ScriptBuffer(input, offset, count);
      }
      if (input && input.type === 'Buffer') input = input.data;
      if (ArrayBuffer.isView(input) || Array.isArray(input)) {
        size(input.length);
        return new ScriptBuffer(input);
      }
      throw error({code:'INVALID_ARGUMENT'});
    }
    static alloc(count, fill = 0, encoding) {
      const result = new ScriptBuffer(size(count));
      if (typeof fill === 'number') result.fill(fill);
      else {
        const pattern = ScriptBuffer.from(fill, encoding);
        if (pattern.length) for (let i = 0; i < count; i++) result[i] = pattern[i % pattern.length];
      }
      return result;
    }
    static allocUnsafe(count) { return ScriptBuffer.alloc(count); }
    static isBuffer(value) { return value instanceof ScriptBuffer; }
    static byteLength(value, encoding) {
      return typeof value === 'string' ? ScriptBuffer.from(value, encoding).length : value.byteLength;
    }
    static concat(items, length = items.reduce((n, item) => n + item.length, 0)) {
      const out = ScriptBuffer.alloc(length);
      let at = 0;
      for (const item of items) {
        const count = Math.min(item.length, length - at);
        if (count <= 0) break;
        out.set(item.subarray(0, count), at); at += count;
      }
      return out;
    }
    toString(encoding = 'utf8', start = 0, end = this.length) {
      return call('util', 'decode', [this, encoding, start, end]);
    }
    slice(start, end) { return this.subarray(start, end); }
    equals(other) { return this.length === other.length && this.every((b, i) => b === other[i]); }
    toJSON() { return {type:'Buffer', data:Array.from(this)}; }
  }
  const byteView = value => {
    if (value instanceof ArrayBuffer) return new ScriptBuffer(value);
    if (ArrayBuffer.isView(value)) return new ScriptBuffer(value.buffer, value.byteOffset, value.byteLength);
    return ScriptBuffer.from(value);
  };
  const utils = freeze({
    buffer:freeze({
      from:ScriptBuffer.from.bind(ScriptBuffer),
      bufToString:(value, encoding) =>
        (typeof value === 'string' ? ScriptBuffer.from(value, 'latin1') : byteView(value)).toString(encoding),
    }),
    crypto:freeze({
      md5:data => call('util','md5',[byteView(data)]),
      randomBytes:n => ScriptBuffer.from(call('util','random',[n])),
      aesEncrypt:(data,mode,key,iv) => ScriptBuffer.from(call('util','aes',[
        byteView(data),mode,byteView(key),iv == null ? null : byteView(iv),
      ])),
      rsaEncrypt:(data,key) => ScriptBuffer.from(call('util','rsa',[
        byteView(data),typeof key === 'string' ? key : byteView(key).toString(),
      ])),
    }),
    zlib:freeze(Object.fromEntries(['inflate','deflate'].map(op => [
      op, data => Promise.resolve().then(() => ScriptBuffer.from(call('util',op,[byteView(data)]))),
    ]))),
  });
  const deepFreeze = value => {
    if (value && typeof value === 'object') {
      for (const child of Object.values(value)) deepFreeze(child);
      freeze(value);
    }
    return value;
  };
  const lx = freeze({
    env:'desktop', version:'2.0.0',
    EVENT_NAMES:freeze({request:'request',inited:'inited',updateAlert:'updateAlert'}),
    currentScriptInfo:deepFreeze(parse(call('scriptInfo'))),
    on(event, handler) {
      try { call('on',event,handler); return Promise.resolve(); }
      catch (e) {
        const rejected = Promise.reject(e); rejected.catch(() => {}); return rejected;
      }
    },
    send(event, data) { call('send',event,data); return Promise.resolve(); },
    request(url, options, callback) {
      if (typeof options === 'function') { callback = options; options = {}; }
      if (typeof callback !== 'function') throw new TypeError('Callback required');
      const id = call('request',String(url),options || {},callback);
      return () => { call('abortRequest',id); };
    },
    utils,
  });
  const storage = freeze(Object.fromEntries(['get','set','delete'].map(op => [
    op, (key,value) => Promise.resolve().then(() => call('storage',op,key,value))
      .then(data => parse(JSON.stringify(data === undefined ? null : data)),
        e => { throw error({code:e.code || 'STORAGE_ERROR'}); }),
  ])));
  Object.defineProperties(globalThis, {
    lx:{value:lx},
    meow:{value:freeze({config:deepFreeze(parse(call('config'))),storage})},
    Buffer:{value:ScriptBuffer},
  });
  globalThis.setTimeout = (fn,delay,...args) => call('timer',fn,delay,false,args);
  globalThis.setInterval = (fn,delay,...args) => call('timer',fn,delay,true,args);
  globalThis.clearTimeout = globalThis.clearInterval = id => call('clearTimer',id);
  globalThis.queueMicrotask = fn => call('microtask',fn);
  globalThis.btoa = str => ScriptBuffer.from(str,'latin1').toString('base64');
  globalThis.atob = str => ScriptBuffer.from(str,'base64').toString('latin1');
  globalThis.TextEncoder = class { encode(str = '') { return ScriptBuffer.from(String(str)); } };
  globalThis.TextDecoder = class {
    constructor(encoding = 'utf8') { this.encoding = encoding; }
    decode(data = []) { return ScriptBuffer.from(data).toString(this.encoding); }
  };
  globalThis.console = freeze(Object.fromEntries(
    ['log','info','warn','error','debug','trace'].map(key => [key,() => {}]),
  ));
  return freeze({
    parse,
    httpArguments(code, metaJSON, rawBytes, encoding, forceJSON) {
      if (code) return [error({code}),null,null];
      const raw = ScriptBuffer.from(rawBytes);
      const response = parse(metaJSON);
      let body = encoding === null ? raw : raw.toString(encoding || 'utf8');
      if (encoding !== null) {
        try { body = parse(body); }
        catch { if (forceJSON) return [error({code:'INVALID_JSON'}),response,null]; }
      }
      response.raw = raw; response.body = body;
      return [null,response,body];
    },
  });
})()
`;
