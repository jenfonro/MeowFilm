import { describeScriptError } from './script-diagnostic.js';

/**
 * Executed inside QuickJS. Host calls exchange JSON; guest callbacks stay in
 * this heap and are referenced by IDs, never passed as host callable objects.
 */
export const realmSource = String.raw`
(() => {
  'use strict';
  const bridge = globalThis.__meowBridge;
  delete globalThis.__meowBridge;
  const parse = JSON.parse.bind(JSON);
  const stringify = JSON.stringify.bind(JSON);
  const setPrototypeOf = Object.setPrototypeOf;
  const isView = ArrayBuffer.isView;
  const arrayFrom = Array.from.bind(Array);
  const freeze = Object.freeze;
  const trustedErrors = new WeakMap();
  const callbacks = new Map();
  const promises = new Map();
  let nextId = 0, handler, realm;
  const describeDiagnostic = ` + describeScriptError.toString() + String.raw`;
  const error = data => {
    const e = new Error(data.code);
    e.code = data.code;
    e.name = data.code === 'CANCELLED' ? 'AbortError' : 'Error';
    trustedErrors.set(e, e.code);
    return e;
  };
  const failureCode = e => trustedErrors.get(e) ||
    (e && e.name === 'InternalError' && /out of memory|allocation failed/i.test(e.message)
      ? 'MEMORY_LIMIT' : e && e.name === 'InternalError' && /stack overflow/i.test(e.message)
        ? 'RESOURCE_LIMIT' : 'SCRIPT_EXCEPTION');
  function diagnose(e, id) {
    if (trustedErrors.has(e)) return;
    try {
      call('diagnostic',id,describeDiagnostic(e),observeScriptErrors?String(e?.message??''):undefined);
    } catch (_) {}
  }
  const call = (op, ...args) => {
    // Control arguments must not inherit a guest toJSON or use its serializer.
    setPrototypeOf(args, null);
    const response = parse(bridge(op, stringify(args, (_key, value) =>
      isView(value) ? arrayFrom(value) : value)));
    if (!response.ok) throw error({code:response.code});
    return response.value;
  };
  const observeScriptErrors = call('observeScriptErrors');
  const size = n => {
    if (!Number.isSafeInteger(n) || n < 0) throw error({code:'RESOURCE_LIMIT'});
    return n;
  };
  class ScriptBuffer extends Uint8Array {
    static from(input, encoding = 'utf8', length) {
      if (typeof input === 'string') {
        return new ScriptBuffer(call('util', 'encode', [input, encoding]));
      }
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
      try {
        if (event !== 'request' || typeof handler !== 'function') throw error({code:'SCRIPT_PROTOCOL_ERROR'});
        registerHandler(handler);
        call('on',event);
        return Promise.resolve();
      }
      catch (e) {
        const rejected = Promise.reject(e); rejected.catch(() => {}); return rejected;
      }
    },
    send(event, data) { call('send',event,data); return Promise.resolve(); },
    request(url, options, callback) {
      if (typeof options === 'function') { callback = options; options = {}; }
      if (typeof callback !== 'function') throw new TypeError('Callback required');
      const id = ++nextId;
      callbacks.set(id, {fn:callback});
      try { call('request',String(url),options || {},id); }
      catch (e) { callbacks.delete(id); throw e; }
      return () => { call('abortRequest',id); };
    },
    utils,
  });
  function registerHandler(fn) { handler = fn; }
  function scopedStorage(scopeId) {
    return freeze(Object.fromEntries(['get','set','delete'].map(op => [
      op, (key,value) => new Promise((resolve,reject) => {
        const id = ++nextId;
        promises.set(id,{resolve,reject});
        try { call('storage',scopeId,op,key,value,id); }
        catch (e) { promises.delete(id); reject(e); }
      }),
    ])));
  }
  Object.defineProperties(globalThis, {
    lx:{value:lx, configurable:true},
    meow:{value:freeze({config:deepFreeze(parse(call('config')))}), configurable:true},
    Buffer:{value:ScriptBuffer, configurable:true},
  });
  const timer = (fn,delay,repeat,args) => {
    if (typeof fn !== 'function') throw error({code:'INVALID_ARGUMENT'});
    const id = ++nextId;
    callbacks.set(id,{fn,args,repeat});
    try { call('timer',id,delay || 0,repeat); }
    catch (e) { callbacks.delete(id); throw e; }
    return id;
  };
  globalThis.setTimeout = (fn,delay,...args) => timer(fn,delay,false,args);
  globalThis.setInterval = (fn,delay,...args) => timer(fn,delay,true,args);
  globalThis.clearTimeout = globalThis.clearInterval = id => { call('clearTimer',id); callbacks.delete(id); };
  globalThis.queueMicrotask = fn => {
    if (typeof fn !== 'function') throw error({code:'INVALID_ARGUMENT'});
    const id = ++nextId;
    callbacks.set(id,{fn,args:[]});
    try { call('microtask',id); }
    catch (e) { callbacks.delete(id); throw e; }
    Promise.resolve().then(() => realm.callback(id,'{}'));
  };
  globalThis.btoa = str => ScriptBuffer.from(str,'latin1').toString('base64');
  globalThis.atob = str => ScriptBuffer.from(str,'base64').toString('latin1');
  globalThis.TextEncoder = class { encode(str = '') { return ScriptBuffer.from(String(str)); } };
  globalThis.TextDecoder = class {
    constructor(encoding = 'utf8') { this.encoding = encoding; }
    decode(data = []) { return ScriptBuffer.from(data).toString(this.encoding); }
  };
  globalThis.console = freeze(Object.fromEntries(
    ['log','info','warn','error','debug','trace','group','groupCollapsed','groupEnd',
      'clear','table','dir','dirxml','time','timeEnd','timeLog','assert','count',
      'countReset','profile','profileEnd','timeStamp'].map(key => [key,() => {}]),
  ));
  return realm = freeze({
    parse,
    errorCode:failureCode,
    diagnose(error,id=0) { diagnose(error,id); return failureCode(error); },
    watchStartup(value) {
      if (value && typeof value.then === 'function') Promise.resolve(value)
        .catch(e => { diagnose(e,0); call('startupFailure',failureCode(e)); });
    },
    dispatch(id, payloadJSON) {
      try {
        const value = handler(parse(payloadJSON), freeze({storage:scopedStorage(id)}));
        if (!value || typeof value.then !== 'function') throw error({code:'SCRIPT_PROTOCOL_ERROR'});
        Promise.resolve(value).then(
          value => call('invokeResult',id,true,value),
          e => { diagnose(e,id); call('invokeResult',id,false,failureCode(e)); },
        ).catch(e => { diagnose(e,id); call('invokeResult',id,false,failureCode(e)); });
      } catch (e) { diagnose(e,id); call('invokeResult',id,false,failureCode(e)); }
    },
    callback(id, payloadJSON) {
      const item = callbacks.get(id);
      if (!item) return;
      if (!item.repeat) callbacks.delete(id);
      try {
        const input = parse(payloadJSON);
        let args = item.args;
        if (!args) {
          if (input.code) args = [error({code:input.code}),null,null];
          else {
            const raw = ScriptBuffer.from(input.body,'base64');
            const response = input.response;
            let body = input.encoding === null ? raw : raw.toString(input.encoding || 'utf8');
            if (input.encoding !== null) {
              try { body = parse(body); }
              catch { if (input.json) args = [error({code:'INVALID_JSON'}),response,null]; }
            }
            if (!args) {
              response.raw = raw; response.body = body;
              args = [null,response,body];
            }
          }
        }
        Promise.resolve(item.fn(...args)).then(
          () => call('callbackDone',id),
          e => { call('callbackFailure',id,failureCode(e)); call('callbackDone',id); },
        );
      } catch (e) { call('callbackFailure',id,failureCode(e)); call('callbackDone',id); }
    },
    storageResult(id, payloadJSON) {
      const item = promises.get(id);
      if (!item) return;
      promises.delete(id);
      const result = parse(payloadJSON);
      if (result.code) item.reject(error({code:result.code}));
      else item.resolve(result.value);
      call('callbackDone',id);
    },
    forgetCallback(id) { callbacks.delete(id); promises.delete(id); },
  });
})()
`;
