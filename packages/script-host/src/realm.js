import { describeScriptError } from './script-diagnostic.js';
import { lxCapabilities } from './lx-capabilities.js';
import { prepareLXRequest } from './lx-native-request.js';

/** Trusted bootstrap evaluated on the LX native thread, before user code. */
export const realmSource = String.raw`
(() => {
  'use strict';
  const host = globalThis.__meowHost;
  const hostArgs = globalThis.__meowHostArgs;
  const emit = globalThis.__meowEmit;
  const native = globalThis.__meowNative;
  const taskContext = globalThis.__meowTaskContext;
  const nativeTimeout = globalThis.setTimeout, nativeInterval = globalThis.setInterval;
  const nativeClearTimeout = globalThis.clearTimeout;
  const evaluate = globalThis.eval;
  for (const key of ['__meowHost','__meowHostArgs','__meowEmit','__meowNative','__meowTaskContext','__meowGuest','__meowCore',
    'setTimeout','setInterval','clearTimeout','clearInterval'])
    delete globalThis[key];
  const parse = JSON.parse.bind(JSON);
  const freeze = Object.freeze;
  const promiseResolve = Promise.resolve.bind(Promise);
  const promiseReject = Promise.reject.bind(Promise);
  const promiseThen = Function.prototype.call.bind(Promise.prototype.then);
  const defineProperty = Object.defineProperty;
  const privateThen = (promise, fulfilled, rejected) => {
    // Only host/scheduler promises enter here, never a guest callback result.
    // Avoid inherited species hooks and their later scope-0 resolving functions.
    defineProperty(promise,'constructor',{__proto__:null,value:undefined});
    promiseThen(promise,fulfilled,rejected);
  };
  const trustedErrors = new WeakMap();
  const active = new Set();
  const activeHas = active.has.bind(active), activeAdd = active.add.bind(active),
    activeDelete = active.delete.bind(active);
  let nextId = 0, handler, initialized = false, alertSent = false, started = false;
  let readyResolve, readyReject, observeScriptErrors = false;
  const ready = new Promise((resolve,reject) => { readyResolve=resolve; readyReject=reject; });
  ready.catch(() => {});
  const describeDiagnostic = ` + describeScriptError.toString() + String.raw`;
  const error = code => {
    const e = new Error(code);
    e.code = code;
    e.name = code === 'CANCELLED' ? 'AbortError' : 'Error';
    trustedErrors.set(e, code);
    return e;
  };
  const fault = error;
  const requireValue = (condition, code = 'INVALID_ARGUMENT') => { if (!condition) throw error(code); };
  const identifier = value => {
    requireValue(typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(value));
    return value;
  };
  const capabilities = ` + lxCapabilities.toString() + String.raw`;
  const prepareRequest = ` + prepareLXRequest.toString() + String.raw`;
  const failureCode = e => {
    try {
      return trustedErrors.get(e) ||
        (e && e.name === 'InternalError' && /stack overflow/i.test(e.message) ? 'RESOURCE_LIMIT' : 'SCRIPT_EXCEPTION');
    } catch (_) { return 'SCRIPT_EXCEPTION'; }
  };
  const withScope = (scope, fn) => {
    const previous = taskContext();
    taskContext(scope);
    try { return fn(); } finally { taskContext(previous); }
  };
  function diagnose(e, scope = taskContext()) {
    if (trustedErrors.has(e)) return;
    try {
      withScope(scope, () => emit('diagnostic', {
        diagnostic: describeDiagnostic(e), ...(observeScriptErrors ? {message:String(e?.message ?? '')} : {}),
      }));
    } catch (_) {}
  }
  const nativeCall = (operation, args) => {
    try { return native(operation,args); }
    catch (e) {
      const failure = error(typeof e?.code === 'string' ? e.code : 'SCRIPT_EXCEPTION');
      emit('hostError',{operation:'util',code:failure.code});
      throw failure;
    }
  };
  const hostCall = (operation, input, scope = taskContext()) => {
    try {
      const call = operation === 'storage' ? hostArgs : host;
      return call(operation,input,scope).catch(e => { throw error(e?.code || 'SCRIPT_EXCEPTION'); });
    }
    catch (e) { return promiseReject(error(e?.code || 'SCRIPT_EXCEPTION')); }
  };
  const size = n => {
    requireValue(Number.isSafeInteger(n) && n >= 0, 'RESOURCE_LIMIT');
    return n;
  };
  class ScriptBuffer extends Uint8Array {
    static from(input, encoding = 'utf8', length) {
      if (typeof input === 'string') return byteView(nativeCall('encode',[input,encoding]));
      if (input instanceof ArrayBuffer) {
        const offset = typeof encoding === 'number' ? encoding : 0;
        const count = length === undefined ? input.byteLength - offset : length;
        size(count);
        return new ScriptBuffer(input,offset,count);
      }
      if (input && input.type === 'Buffer') input = input.data;
      if (ArrayBuffer.isView(input) || Array.isArray(input)) {
        size(input.length);
        return new ScriptBuffer(input);
      }
      throw error('INVALID_ARGUMENT');
    }
    static alloc(count, fill = 0, encoding) {
      const result = new ScriptBuffer(size(count));
      if (typeof fill === 'number') result.fill(fill);
      else {
        const pattern = ScriptBuffer.from(fill,encoding);
        if (pattern.length) for (let i=0;i<count;i++) result[i]=pattern[i%pattern.length];
      }
      return result;
    }
    static allocUnsafe(count) { return ScriptBuffer.alloc(count); }
    static isBuffer(value) { return value instanceof ScriptBuffer; }
    static byteLength(value,encoding) {
      return typeof value === 'string' ? ScriptBuffer.from(value,encoding).length : value.byteLength;
    }
    static concat(items,length=items.reduce((count,item)=>count+item.length,0)) {
      const out=ScriptBuffer.alloc(length);
      let at=0;
      for (const item of items) {
        const count=Math.min(item.length,length-at);
        if (count<=0) break;
        out.set(item.subarray(0,count),at); at+=count;
      }
      return out;
    }
    toString(encoding='utf8',start=0,end=this.length) { return nativeCall('decode',[this,encoding,start,end]); }
    slice(start,end) { return this.subarray(start,end); }
    equals(other) { return this.length === other.length && this.every((value,index)=>value === other[index]); }
    toJSON() { return {type:'Buffer',data:Array.from(this)}; }
  }
  const byteView = value => {
    if (value instanceof ArrayBuffer) return new ScriptBuffer(value);
    if (ArrayBuffer.isView(value)) return new ScriptBuffer(value.buffer,value.byteOffset,value.byteLength);
    return ScriptBuffer.from(value);
  };
  const utils=freeze({
    buffer:freeze({
      from:ScriptBuffer.from.bind(ScriptBuffer),
      bufToString:(value,encoding)=>(typeof value === 'string'
        ? ScriptBuffer.from(value,'latin1') : byteView(value)).toString(encoding),
    }),
    crypto:freeze({
      md5:data=>nativeCall('md5',[byteView(data)]),
      randomBytes:n=>byteView(nativeCall('random',[size(n)])),
      aesEncrypt:(data,mode,key,iv)=>byteView(nativeCall('aes',[
        byteView(data),mode,byteView(key),iv==null?null:byteView(iv),
      ])),
      rsaEncrypt:(data,key)=>byteView(nativeCall('rsa',[
        byteView(data),typeof key === 'string'?key:byteView(key).toString(),
      ])),
    }),
    zlib:freeze(Object.fromEntries(['inflate','deflate'].map(operation=>[
      operation,data=>promiseResolve().then(()=>byteView(nativeCall(operation,[byteView(data)]))),
    ]))),
  });
  const deepFreeze=value=>{
    if (value && typeof value === 'object') {
      for (const child of Object.values(value)) deepFreeze(child);
      freeze(value);
    }
    return value;
  };
  function withCallbackScope(scope,fn) {
    // Selecting a source must not enter guest code before its execution owner.
    const callbackScope=activeHas(scope)?scope:0;
    return withScope(callbackScope,()=>taskContext(callbackScope,fn));
  }
  function callback(fn,args,scope) {
    const report=e=>emit('callbackError',{code:failureCode(e)});
    withCallbackScope(scope,()=>{
      try {
        // Absorption, species lookup and later rejection classification can
        // execute guest getters just as the callback body can.
        promiseThen(promiseResolve(fn(...args)),undefined,e=>
          withCallbackScope(scope,()=>report(e)));
      } catch (e) { report(e); }
    });
  }
  function request(url,options,fn) {
    if (typeof options === 'function') { fn=options; options={}; }
    if (typeof fn !== 'function') throw new TypeError('Callback required');
    const prepared=prepareRequest(options||{},ScriptBuffer,utils.crypto.randomBytes,error);
    const scope=taskContext(), id=++nextId;
    const requestedUrl=String(url);
    let settled=false, launched=false;
    const finish=readArgs=>{
      if (settled) return;
      settled=true;
      // Normalization and error construction can enter guest code too, before
      // fn runs. Keep the whole completion inside the trusted callback owner.
      callback(()=>{
        let args;
        try { args=readArgs(); } catch (e) { args=[e,null,null]; }
        return fn(...args);
      },[],scope);
    };
    const failed=(failure,code='SCRIPT_EXCEPTION')=>
      finish(()=>[error(failure?.code||code),null,null]);
    const complete=response=>finish(()=>{
      // Request failures cross native as a primitive, never a scope-0 Error.
      if (typeof response==='string') return [error(response),null,null];
      const raw=byteView(response.body);
      let body=prepared.encoding===null?raw:raw.toString(prepared.encoding||'utf8');
      if (prepared.encoding!==null) {
        try { body=parse(body); }
        catch { if (prepared.json===true) return [error('INVALID_JSON'),response,null]; }
      }
      response.raw=raw; response.body=body;
      return [null,response,body];
    });
    const launch=()=>{
      if (settled) return;
      withCallbackScope(scope,()=>{
        try {
          launched=true;
          // Only I/O authority is session-owned. Input conversion can enter
          // guest getters and retains the caller's execution control.
          const pending=withScope(0,()=>
            host('request',{id,url:requestedUrl,options:prepared,scopeId:scope},0));
          privateThen(pending,complete,failed);
        } catch (e) { failed(e,'NETWORK_ERROR'); }
      });
    };
    // Defer admission so immediate abort cannot contact the adapter. Retain
    // the current execution owner while registering this session-owned job.
    try {
      withScope(0,()=>privateThen(promiseResolve(),launch,e=>failed(e,'NETWORK_ERROR')));
    } catch (e) { failed(e,'NETWORK_ERROR'); }
    return ()=>{
      if (settled) return;
      if (launched) {
        try { privateThen(withScope(0,()=>host('abortRequest',id,0)),undefined,()=>{}); } catch (_) {}
      }
      finish(()=>[error('CANCELLED'),null,null]);
    };
  }
  function scopedStorage(scopeId) {
    return freeze(Object.fromEntries(['get','set','delete'].map(operation=>[
      operation,(key,value)=>{
        if (!activeHas(scopeId)) return promiseReject(error('PERMISSION_DENIED'));
        try {
          return withScope(scopeId,()=>hostCall('storage',[
            scopeId,operation,key,...(operation==='set'?[value]:[]),
          ],scopeId));
        } catch(e) { return promiseReject(error(e?.code||'PERMISSION_DENIED')); }
      },
    ])));
  }
  const timer=(fn,delay,repeat,args)=>{
    requireValue(typeof fn==='function');
    const scope=taskContext();
    const ms=Math.max(1,Math.min(Number(delay)||0,2**31-1));
    return withScope(0,()=> (repeat?nativeInterval:nativeTimeout)(()=>callback(fn,args,scope),ms));
  };
  globalThis.setTimeout=(fn,delay,...args)=>timer(fn,delay,false,args);
  globalThis.setInterval=(fn,delay,...args)=>timer(fn,delay,true,args);
  globalThis.clearTimeout=globalThis.clearInterval=id=>{
    if (Number.isSafeInteger(id) && id>0) nativeClearTimeout(id);
  };
  globalThis.queueMicrotask=fn=>{
    requireValue(typeof fn==='function');
    const scope=taskContext();
    withScope(0,()=>privateThen(promiseResolve(),()=>callback(fn,[],scope)));
  };
  globalThis.btoa=str=>ScriptBuffer.from(str,'latin1').toString('base64');
  globalThis.atob=str=>ScriptBuffer.from(str,'base64').toString('latin1');
  globalThis.TextEncoder=class { encode(str='') { return ScriptBuffer.from(String(str)); } };
  globalThis.TextDecoder=class {
    constructor(encoding='utf8') { this.encoding=encoding; }
    decode(data=[]) { return byteView(data).toString(this.encoding); }
  };
  globalThis.console=freeze(Object.fromEntries(
    ['log','info','warn','error','debug','trace','group','groupCollapsed','groupEnd',
      'clear','table','dir','dirxml','time','timeEnd','timeLog','assert','count',
      'countReset','profile','profileEnd','timeStamp'].map(key=>[key,()=>{}]),
  ));
  return freeze({
    start(config,scriptInfo,observe) {
      requireValue(!started,'SCRIPT_PROTOCOL_ERROR');
      started=true; observeScriptErrors=observe===true;
      const source=scriptInfo.rawScript;
      const lx=freeze({
        env:'desktop',version:'2.0.0',
        EVENT_NAMES:freeze({request:'request',inited:'inited',updateAlert:'updateAlert'}),
        currentScriptInfo:deepFreeze(scriptInfo),utils,request,
        on(event,fn) {
          try {
            requireValue(event==='request' && typeof fn==='function','SCRIPT_PROTOCOL_ERROR');
            handler=fn; return promiseResolve();
          } catch(e) { const rejected=promiseReject(e); rejected.catch(()=>{}); return rejected; }
        },
        send(event,data) {
          try {
            if (event==='inited') {
              requireValue(!initialized && typeof handler==='function','SCRIPT_PROTOCOL_ERROR');
              data={...data,sources:capabilities(data)};
              initialized=true;
            } else if (event==='updateAlert') {
              requireValue(!alertSent && data && typeof data.log==='string' && data.log.length<=1024 &&
                (data.updateUrl===undefined || typeof data.updateUrl==='string' && data.updateUrl.length<=1024),
                'SCRIPT_PROTOCOL_ERROR');
              alertSent=true;
            } else throw error('SCRIPT_PROTOCOL_ERROR');
            emit('send',{event,data});
            if (event==='inited') readyResolve();
            return promiseResolve();
          } catch (_) {
            emit('protocolFailure',{code:'SCRIPT_PROTOCOL_ERROR'});
            throw error('SCRIPT_PROTOCOL_ERROR');
          }
        },
      });
      Object.defineProperties(globalThis,{
        lx:{value:lx,configurable:true},
        meow:{value:freeze({config:deepFreeze(config)}),configurable:true},
        Buffer:{value:ScriptBuffer,configurable:true},
      });
      try {
        const value=evaluate(source+'\n//# sourceURL=installed-script.js');
        if (value && typeof value.then==='function') promiseResolve(value).catch(e=>{
          diagnose(e,0); readyReject(error(failureCode(e)));
          emit('startupFailure',{code:failureCode(e)});
        });
      } catch(e) { diagnose(e,0); throw error(failureCode(e)); }
      return ready;
    },
    async dispatch(id,payload) {
      activeAdd(id);
      try {
        const value=handler(payload,freeze({storage:scopedStorage(id)}));
        requireValue(value && typeof value.then==='function','SCRIPT_PROTOCOL_ERROR');
        return await value;
      } catch(e) { diagnose(e,id); throw error(failureCode(e)); }
      finally { activeDelete(id); }
    },
    retire(id) { activeDelete(id); },
  });
})()
`;
