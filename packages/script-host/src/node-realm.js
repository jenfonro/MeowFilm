import { realmSource } from './realm.js';
import { nativeRoles, nativeConstructorRoles, httpRequestOptionNames,
  projectFields, projectHttpRequestOptions } from './native-roles.js';
import { discoverSourceRoutes, createLocalStartupLookup, startupWrapperTarget } from './cat-source-routes.js';
import { createHttpMessageClasses } from './node-http-realm.js';

/** CommonJS and Node-shaped SDK execute inside QuickJS. Native capabilities
 * are opaque JSON receipts; Function/eval/vm never leave this engine. */
export const nodeRealmSource = String.raw`
(() => {
  const rawBridge = globalThis.__meowBridge;
  const base = ` + realmSource + String.raw`;
  Object.defineProperty(globalThis,'__meowBridge',{
    value:undefined,writable:false,configurable:false
  });
  delete globalThis.lx;
  delete globalThis.meow;
  const parse = JSON.parse.bind(JSON), stringify = JSON.stringify.bind(JSON);
  const nativeHandles = new WeakMap(), nativeCache = new Map(), guestCallbacks = new Map();
  const pendingNativeReleases = new Set();
  let lastNativeCacheScan = 0, bridgeDepth = 0, releasingNativeHandles = false;
  const keyValues = new WeakMap();
  const promises = new Map(), settled = new Map(), modules = new Map(), errors = new WeakMap(), core={};
  const roleMethods = ` + JSON.stringify(Object.fromEntries(Object.entries(nativeRoles)
    .map(([key, role]) => [key, [...role.methods]]))) + String.raw`;
  const constructorRoles = ` + JSON.stringify(nativeConstructorRoles) + String.raw`;
  const httpRequestOptionNames = ` + JSON.stringify(httpRequestOptionNames) + String.raw`;
  const projectFields = ` + projectFields.toString() + String.raw`;
  const projectHttpRequestOptions = ` + projectHttpRequestOptions.toString() + String.raw`;
  const discoverSourceRoutes = ` + discoverSourceRoutes.toString() + String.raw`;
  const createLocalStartupLookup = ` + createLocalStartupLookup.toString() + String.raw`;
  const startupWrapperTarget = ` + startupWrapperTarget.toString() + String.raw`;
  const createHttpMessageClasses = ` + createHttpMessageClasses.toString() + String.raw`;
  const rolePrototypes=new Map(), handleRoles=new WeakMap(), handleStates=new WeakMap(), callbackIds=new WeakMap();
  const roleInstance=(expected,actual)=>expected===actual||(expected==='blob'&&actual==='file');
  const rootContext={id:0,values:new Map(),parent:null,origin:null,cancelled:false,boundary:true};
  rootContext.origin=rootContext;
  let asyncContext=rootContext,nextContext=0;
  const asyncContexts=new Map([[0,rootContext]]),disabledStores=new WeakSet();
  function contextActive(context){
    if(!context||typeof context!=='object')return false;
    for(let current=context;current;current=current.parent)if(current.cancelled)return false;
    return true;
  }
  function contextSnapshot(overlay=false){
    const current=contextActive(asyncContext)?asyncContext:rootContext;
    const parent=overlay&&!current.boundary?current.parent:current;
    const context={id:++nextContext,values:new Map(parent.values),parent,
      origin:parent===rootContext?null:parent.origin,cancelled:false,boundary:!overlay};
    if(overlay)context.values=new Map(current.values);
    context.origin??=context;
    return context;
  }
  function registerContext(context){
    if(context===rootContext)return 0;
    if(!contextActive(context))throw nodeError('CANCELLED');
    context.boundary=true;
    asyncContexts.set(context.id,context);
    return context.id;
  }
  function releaseContext(input){
    const id=typeof input==='object'?input?.id:Number(input);
    if(!Number.isSafeInteger(id)||id<=0)return;
    const context=typeof input==='object'?input:asyncContexts.get(id);
    if(!context||context===rootContext)return;
    context.cancelled=true;
    for(const [key,candidate]of [...asyncContexts])
      if(key!==0&&!contextActive(candidate))asyncContexts.delete(key);
    if(!contextActive(asyncContext))asyncContext=rootContext;
  }
  function forgetContext(input){
    const id=typeof input==='object'?input?.id:Number(input);
    if(!Number.isSafeInteger(id)||id<=0)return;
    asyncContexts.delete(id);
  }
  function runContext(context,fn,receiver,args){const previous=asyncContext;asyncContext=context;
    try{
      if(!contextActive(context))return undefined;
      return Reflect.apply(fn,receiver,args);
    }finally{asyncContext=contextActive(previous)?previous:rootContext;}}
  const own = Object.prototype.hasOwnProperty;
  let nextCallback = 0;
  function encode(value, seen = new Set(), frames = []) {
    if (value === undefined) return {$:'undefined'};
    if (typeof value === 'bigint') return {$:'bigint',value:String(value)};
    if (value === null || ['string','number','boolean'].includes(typeof value)) return value;
    if (keyValues.has(value)) return keyValues.get(value).wire;
    if (typeof value!=='function'&&nativeHandles.has(value)) return {$:'handle',id:nativeHandles.get(value)};
    if (typeof value === 'function') {
      let bindings=callbackIds.get(value);
      if(!bindings){bindings=new Map();callbackIds.set(value,bindings);}
      let id=bindings.get(asyncContext);
      if (!id) { id = ++nextCallback; bindings.set(asyncContext,id);guestCallbacks.set(id,{fn:value,context:asyncContext}); }
      return {$:'callback',id,context:registerContext(asyncContext)};
    }
    if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer) {
      const frame = frames.length;
      frames.push(value instanceof ArrayBuffer
        ? value.slice(0)
        : value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength));
      return {$:'bytes',frame};
    }
    if (value instanceof Error) return {$:'error',code:value.code||'SCRIPT_EXCEPTION'};
    if (value instanceof Date) return {$:'date',value:value.toISOString()};
    if (seen.has(value)) throw new TypeError('Cyclic native argument');
    seen.add(value);
    const result = Array.isArray(value) ? value.map(item => encode(item,seen,frames))
      : {$:'object',value:Object.fromEntries(Object.entries(value)
        .map(([key,item])=>[key,encode(item,seen,frames)]))};
    seen.delete(value); return result;
  }
  function nodeError(code) {
    const error = new Error(code); error.code = code;
    errors.set(error, ['UNSUPPORTED_DEPENDENCY','PERMISSION_DENIED','RESOURCE_LIMIT',
      'UNMAPPED_LOCAL_URL','STALE_SESSION','CAPABILITY_UNAVAILABLE','NETWORK_ERROR',
      'TIMEOUT','CANCELLED','SERVICE_EXISTS','SERVICE_CLOSED',
      'ERR_INVALID_PROTOCOL'].includes(code) ? code : 'SCRIPT_EXCEPTION');
    return error;
  }
  function callbackFailureCode(error) {
    return errors.get(error) || (error?.name === 'InternalError' &&
      /out of memory|allocation failed/i.test(error.message) ? 'MEMORY_LIMIT'
      : error?.name === 'InternalError' && /stack overflow/i.test(error.message)
        ? 'RESOURCE_LIMIT' : 'SCRIPT_EXCEPTION');
  }
  function bridge(input, frames = []) {
    bridgeDepth++;
    try{return rawBridge('node',stringify(input),...frames);}
    finally{bridgeDepth--;}
  }
  function flushReleasedHandles() {
    if(releasingNativeHandles||bridgeDepth!==0||!pendingNativeReleases.size)return;
    const ids=[];
    for(const id of [...pendingNativeReleases]){
      pendingNativeReleases.delete(id);
      if(!nativeCache.get(id)?.deref())ids.push(id);
    }
    if(!ids.length)return;
    releasingNativeHandles=true;
    try{rpcAt({operation:'releaseHandles',ids},rootContext);}
    catch{for(const id of ids)pendingNativeReleases.add(id);}
    finally{releasingNativeHandles=false;}
  }
  function rpcAt(input,context,frames = []) {
    if(!contextActive(context))throw nodeError('CANCELLED');
    let originContext=0;
    if(context!==rootContext){
      let parent=context.parent;
      while(parent&&parent!==rootContext&&asyncContexts.get(parent.id)!==parent)parent=parent.parent;
      originContext=parent&&parent!==rootContext?parent.id:context.id;
    }
    const packet = bridge({...input,context:registerContext(context),
      originContext},frames);
    const outer = parse(Array.isArray(packet) ? packet[0] : packet);
    const outputFrames = Array.isArray(packet) ? packet.slice(1) : [];
    if (!outer.ok) throw nodeError(outer.code);
    const response = outer.value;
    if (!response.ok) throw decode(response.value,outputFrames);
    return {...response,frames:outputFrames};
  }
  function rpc(input,frames = []) {
    flushReleasedHandles();
    if(!contextActive(asyncContext))throw nodeError('CANCELLED');
    return rpcAt(input,asyncContext,frames);
  }
  const symbolKey = symbol => symbol === Symbol.iterator ? '@@iterator'
    : symbol === Symbol.asyncIterator ? '@@asyncIterator'
    : symbol === Symbol.toStringTag ? '@@toStringTag'
    : symbol === Symbol.hasInstance ? '@@hasInstance'
    : Symbol.keyFor(symbol) === 'nodejs.util.inspect.custom' ? '@@nodejs.util.inspect.custom' : null;
  function decode(value, frames, owner) {
    if (value === null || typeof value !== 'object') return value;
    if (Array.isArray(value)) return value.map(item=>decode(item,frames));
    if (!value.$) return value;
    switch(value.$) {
      case 'undefined': return undefined;
      case 'bigint': return BigInt(value.value);
      case 'date': return new Date(value.value);
      case 'bytes':
        if(!Number.isSafeInteger(value.frame)||value.frame<0||
          !frames||value.frame>=frames.length)throw nodeError('SCRIPT_EXCEPTION');
        return globalThis.Buffer.from(frames[value.frame]);
      case 'error': return nodeError(value.code);
      case 'object': return Object.fromEntries(Object.entries(value.value)
        .map(([key,item])=>[key,decode(item,frames)]));
      case 'promise': return new Promise((resolve,reject)=>{
        promises.set(value.id,{resolve,reject});
        if (settled.has(value.id)) {
          const response=settled.get(value.id); settled.delete(value.id);
          finish(value.id,response.response,response.frames);
        }
      });
      case 'handle': return proxy(value, value.callable ? owner : undefined);
      case 'key': {
        const result=Object.create(KeyObject.prototype);
        keyValues.set(result,{wire:{$:'key',type:value.type,data:value.data},
          fields:decode(value.fields,frames)});
        return result;
      }
      case 'guestCallback': return guestCallbacks.get(value.id)?.fn;
      default: throw nodeError('SCRIPT_EXCEPTION');
    }
  }
  function keyValue(value) {
    const record=keyValues.get(value);
    if(!record)throw nodeError('ERR_INVALID_ARG_TYPE');
    return record;
  }
  function keyCall(receiver,method,args) {
    const frames=[];const response=rpc({operation:'key',method,
      key:keyValue(receiver).wire,args:encode(args,new Set(),frames)},frames);
    return decode(response.value,response.frames);
  }
  class KeyObject {
    constructor(){throw new TypeError('Illegal constructor');}
    get type(){return keyValue(this).fields.type;}
    get asymmetricKeyType(){return keyValue(this).fields.asymmetricKeyType;}
    get asymmetricKeyDetails(){return keyValue(this).fields.asymmetricKeyDetails;}
    get symmetricKeySize(){return keyValue(this).fields.symmetricKeySize;}
    export(options){return keyCall(this,'export',[options]);}
    equals(other){return keyCall(this,'equals',[other]);}
    get [Symbol.toStringTag](){return 'KeyObject';}
  }
  function releaseNative(json) {
    let packet;
    try { packet=parse(json); } catch { return; }
    for(const id of Array.isArray(packet?.collectHandles)?packet.collectHandles:[]){
      const key=String(id);
      if(!nativeCache.get(key)?.deref())pendingNativeReleases.add(key);
    }
    for(const id of Array.isArray(packet?.handles)?packet.handles:[]){
      const key=String(id),cached=nativeCache.get(key)?.deref();
      const state=cached&&handleStates.get(cached);
      if(state){
        for(const cleanup of [...state.pipes.values()]){try{cleanup();}catch{}}
        state.released=true;
      }
      nativeCache.delete(key);
      pendingNativeReleases.delete(key);
    }
    for(const id of Array.isArray(packet?.callbacks)?packet.callbacks:[]){
      const key=Number(id),entry=guestCallbacks.get(key);
      if(!entry)continue;
      guestCallbacks.delete(key);
      const bindings=callbackIds.get(entry.fn);
      if(bindings?.get(entry.context)===key)bindings.delete(entry.context);
    }
    for(const id of Array.isArray(packet?.contexts)?packet.contexts:[]){
      releaseContext(Number(id));
    }
    for(const id of Array.isArray(packet?.forgetContexts)?packet.forgetContexts:[]){
      forgetContext(Number(id));
    }
    for(const id of Array.isArray(packet?.promises)?packet.promises:[]){
      const key=String(id),pending=promises.get(key);
      promises.delete(key);settled.delete(key);
      pending?.reject(nodeError('CANCELLED'));
    }
  }
  function releaseCallbacks(callbacks) {
    const ids=[];
    for(const callback of callbacks){
      const bindings=callbackIds.get(callback);
      if(bindings)for(const id of bindings.values())ids.push(id);
    }
    if(ids.length)rpc({operation:'releaseCallbacks',ids:[...new Set(ids)]});
  }
  function proxy(meta, owner) {
    const key=String(meta.id),cached=nativeCache.get(key)?.deref();
    if (cached) return cached;
    if (nativeCache.has(key)) nativeCache.delete(key);
    const shadow = new Map();
    const state={released:false,pipes:new Map()};
    const active=()=>{if(state.released)throw nodeError('CANCELLED');};
    const call = (operation, receiver, args) => {
      active();
      if(owner&&handleStates.get(owner)?.released)throw nodeError('CANCELLED');
      if(meta.argumentFields)args=args.map((arg,index)=>{
        if(!arg||typeof arg!=='object'||Array.isArray(arg)||nativeHandles.has(arg)||
          ArrayBuffer.isView(arg)||arg instanceof ArrayBuffer||arg instanceof Date||arg instanceof Error)return arg;
        const {kind,fields,options}=meta.argumentFields;
        return kind==='http'?projectHttpRequestOptions(arg,
          agent=>agent===false||handleRoles.get(agent)==='agent'):
          projectFields(arg,kind==='urlFormat'&&index===1?options:fields);
      });
      const frames=[];
      const encodedArgs=encode(args,new Set(),frames);
      const encodedReceiver=receiver&&nativeHandles.has(receiver)
        ?encode(receiver,new Set(),frames):null;
      const response = rpc({operation,id:meta.id,args:encodedArgs,
        receiver:encodedReceiver},frames);
      for (const update of response.updates || []) {
        const buffer=args[update.index]; if (ArrayBuffer.isView(buffer)) {
          const target=new Uint8Array(buffer.buffer,buffer.byteOffset,buffer.byteLength);
          target.set(decode(update.value,response.frames).subarray(0,target.length));
        }
      }
      return decode(response.value,response.frames);
    };
    const target = meta.callable ? function(...args) {
      return call(new.target?'construct':'call',this,args);
    } : {};
    const result = new Proxy(target,{
      get(target,key,receiver) {
        if (shadow.has(key)) return shadow.get(key);
        if (key==='constructor') return meta.callable?Function:Object;
        if (meta.callable && key===Symbol.hasInstance) {
          const role=constructorRoles[meta.name];
          return role ? value=>nativeHandles.has(value)&&roleInstance(role,handleRoles.get(value))
            : Reflect.get(target,key);
        }
        if (meta.callable && ['name','length','caller','arguments','call','apply','bind'].includes(key)) return Reflect.get(target,key);
        if (key==='prototype' && meta.callable) {
          if (!shadow.has(key)) shadow.set(key,target.prototype);
          return shadow.get(key);
        }
        if(!meta.callable&&key==='pipe')return destination=>{
          active();state.pipes.get(destination)?.();
          const source=resultRef.deref();
          if(!source)throw nodeError('CANCELLED');
          if(nativeHandles.has(source)&&nativeHandles.has(destination)){
            let cleaned=false;
            const failed=error=>{if(!cleaned){cleanup();destination.destroy(error);}};
            const closed=()=>{if(!cleaned&&!source.readableEnded)
              failed(nodeError('ERR_STREAM_PREMATURE_CLOSE'));};
            const cleanup=()=>{
              if(cleaned)return;cleaned=true;state.pipes.delete(destination);
              const frames=[];try{rpc({operation:'invoke',id:meta.id,key:'unpipeTo',
                args:encode([destination],new Set(),frames)},frames);}catch{}
              try{releaseCallbacks([failed,closed]);}catch{}
            };
            state.pipes.set(destination,cleanup);
            try{
              source.once('error',failed);source.once('close',closed);
              const frames=[];if(rpc({operation:'invoke',id:meta.id,key:'pipeTo',
                args:encode([destination],new Set(),frames)},frames).value===true){
                destination.once('finish',cleanup);destination.once('close',cleanup);
                destination.emit?.('pipe',source);
                return destination;
              }
              cleanup();
            }catch(error){cleanup();throw error;}
          }
          let cleaned=false,draining=false;
          const cleanup=()=>{if(cleaned)return;cleaned=true;state.pipes.delete(destination);
            const current=resultRef.deref();
            try{current?.off('data',data);}catch{}try{current?.off('end',end);}catch{}
            try{current?.off('error',failed);}catch{}try{current?.off('close',closed);}catch{}
            try{destination.off?.('drain',drain);}catch{}
            try{destination.off?.('finish',cleanup);}catch{}try{destination.off?.('close',cleanup);}catch{}
            try{releaseCallbacks([data,end,failed,closed,drain,cleanup]);}catch{}};
          const drain=()=>{draining=false;if(!cleaned)resultRef.deref()?.resume();};
          const data=chunk=>{try{const current=resultRef.deref();if(!current)return;
            if(destination.write(chunk)===false&&!draining){
            draining=true;current.pause();destination.once('drain',drain);}}catch(error){failed(error);}};
          const end=()=>{cleanup();destination.end();};
          const failed=error=>{cleanup();destination.destroy(error);};
          const closed=()=>{cleanup();destination.destroy(nodeError('ERR_STREAM_PREMATURE_CLOSE'));};
          state.pipes.set(destination,cleanup);
          try{
            source.on('data',data);source.once('end',end);source.once('error',failed);
            source.once('close',closed);
            destination.once('finish',cleanup);destination.once('close',cleanup);
            destination.emit?.('pipe',source);
            // Match Node's Readable.pipe: attaching a data listener alone does
            // not start a paused stream. Without resume the source can end
            // empty while the destination reports finish.
            source.resume();return destination;
          }catch(error){cleanup();throw error;}};
        if(!meta.callable&&key==='unpipe')return destination=>{
          if(destination!==undefined)state.pipes.get(destination)?.();
          else for(const cleanup of [...state.pipes.values()])cleanup();
          const source=resultRef.deref();
          if(!source)throw nodeError('CANCELLED');
          source.pause();return source;};
        if(!meta.callable&&key==='emit')return (...args)=>{const emitter=shadow.get('localEmitter');
          return emitter?emitter.emit(...args):false;};
        if(key==='toJSON'&&!['url','x509'].includes(meta.role))return undefined;
        if (typeof key==='symbol') { key=symbolKey(key); if (!key) return undefined; }
        if (key==='then') return undefined;
        active();
        try {
          const response=rpc({operation:'get',id:meta.id,key});
          // Bound methods retain their receiver in their own closure, not in
          // a WeakMap value that keeps the receiver until a guest cycle GC.
          const value=decode(response.value,response.frames,resultRef.deref());
          if(meta.role==='incoming'&&['headers','rawHeaders'].includes(key))shadow.set(key,value);
          return value;
        }
        catch(error) { if(error.code==='PERMISSION_DENIED') return undefined; throw error; }
      },
      set(target,key,value) {
        if (!meta.callable) {
          active();
          const frames=[];try{rpc({operation:'set',id:meta.id,key,
            value:encode(value,new Set(),frames)},frames);return true;}
          catch(error){if(error.code!=='PERMISSION_DENIED')throw error;}
        }
        shadow.set(key,value);
        return true;
      },
      ownKeys(target) { active();return [...new Set([...Reflect.ownKeys(target),
        ...(meta.callable?[]:rpc({operation:'keys',id:meta.id}).value),...shadow.keys()])]; },
      getOwnPropertyDescriptor(target,key) {
        const local=Reflect.getOwnPropertyDescriptor(target,key);
        return local || {enumerable:true,configurable:true,writable:true,
          value:resultRef.deref()?.[key]};
      },
      getPrototypeOf(target) {
        if(meta.callable)return Function.prototype;
        if(!rolePrototypes.has(meta.role)){
          const prototype={};
          for(const key of roleMethods[meta.role]||[]){
            if(key.startsWith('@@'))continue;
            prototype[key]=function(...args){
              const id=nativeHandles.get(this);
              if(!id||handleStates.get(this)?.released)throw nodeError('CANCELLED');
              const frames=[];const response=rpc({operation:'invoke',id,key,
                args:encode(args,new Set(),frames)},frames);
              return decode(response.value,response.frames);};
          }
          rolePrototypes.set(meta.role,prototype);
        }
        return rolePrototypes.get(meta.role);
      },
    });
    const resultRef = new WeakRef(result);
    nativeHandles.set(result,meta.id); handleRoles.set(result,meta.role);handleStates.set(result,state);
    nativeCache.set(key,new WeakRef(result));
    if(nativeCache.size>=Math.max(64,lastNativeCacheScan*2)){
      for(const [id,ref] of nativeCache)if(!ref.deref()){nativeCache.delete(id);pendingNativeReleases.add(id);}
      lastNativeCacheScan=nativeCache.size;
    }
    return result;
  }
  function finish(id,response,frames=[]) {
    const promise=promises.get(id);
    if(!promise) { settled.set(id,{response,frames}); return; }
    promises.delete(id);
    if(response.ok) promise.resolve(decode(response.value,frames));
    else promise.reject(decode(response.value,frames));
  }
  const builtin = name => {
    const response=rpc({operation:'module',name});
    return decode(response.value,response.frames);
  };
  const sdkNativeModules=new Set(['fs/promises','crypto','zlib','url','util/types',
    'stream/web','http2','dns','dns/promises','net','tls','os','perf_hooks','tty',
    'worker_threads','constants']);
  function loadSdk(name,parent) {
    const data=rpc({operation:'source',name,parent:parent||null}).value;
    if(modules.has(data.id)) return modules.get(data.id).exports;
    const module={exports:{},filename:data.id,loaded:false,id:data.id};
    modules.set(data.id,module);
    if(data.json) module.exports=parse(data.source);
    else new Function('exports','require','module','__filename','__dirname',data.source)
      (module.exports,request=>requireModule(request,data.id),module,data.id,data.id.slice(0,data.id.lastIndexOf('/')));
    module.loaded=true; return module.exports;
  }
  const EventEmitter=loadSdk('events');
  const process=new EventEmitter();
  Object.assign(process,{
    env:{},argv:['node','/source.js'],execArgv:[],version:'v24.21.0',versions:{node:'24.21.0'},
    platform:'linux',arch:'wasm32',pid:0,ppid:0,exitCode:0,
    cwd:()=> '/',chdir:()=> {throw nodeError('PERMISSION_DENIED');},
    nextTick:(fn,...args)=>queueMicrotask(()=>fn(...args)),
    emitWarning:warning=>process.emit('warning',typeof warning==='string'?new Error(warning):warning),
    uptime:()=>rpc({operation:'uptime'}).value,
    hrtime(previous) { const n=Date.now()*1e6; const value=[Math.floor(n/1e9),n%1e9];
      if(!previous)return value; let seconds=value[0]-previous[0],nanoseconds=value[1]-previous[1];
      if(nanoseconds<0){seconds--;nanoseconds+=1e9;}return [seconds,nanoseconds]; },
    memoryUsage:()=>rpc({operation:'memory'}).value,
    exit:()=>{throw nodeError('PERMISSION_DENIED');},binding:()=>{throw nodeError('PERMISSION_DENIED');},
    _rawDebug:()=>{},title:'script',
  });
  Object.defineProperty(process,Symbol.toStringTag,{value:'process',configurable:true});
  process.hrtime.bigint=()=>BigInt(Date.now())*1000000n;
  globalThis.process=process; globalThis.global=globalThis;
  const buffers=loadSdk('buffer');
  {
    // The guest buffer package predates Node's base64url encoding. Its base64
    // decoder already accepts both alphabets; only the name and output differ.
    const Buffer=buffers.Buffer,proto=Buffer.prototype;
    const from=Buffer.from,isEncoding=Buffer.isEncoding,byteLength=Buffer.byteLength;
    const toString=proto.toString,write=proto.write,fill=proto.fill,lastIndexOf=proto.lastIndexOf;
    const urlEncoding=value=>typeof value==='string'&&value.length===9&&value.toLowerCase()==='base64url';
    const encoding=value=>urlEncoding(value)?'base64':value;
    Buffer.from=function(value,format,length){return from(value,encoding(format),length);};
    Buffer.isEncoding=function(value){return urlEncoding(value)||isEncoding(value);};
    Buffer.byteLength=function(value,format,mustMatch){return byteLength(value,encoding(format),mustMatch);};
    proto.toString=function(format,start,end){
      if(arguments.length===0)return toString.call(this);
      const value=toString.call(this,encoding(format),start,end);
      return urlEncoding(format)?value.replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,''):value;
    };
    proto.toLocaleString=proto.toString;
    proto.write=function(value,offset,length,format){
      return write.call(this,value,encoding(offset),encoding(length),encoding(format));
    };
    proto.fill=function(value,start,end,format){
      return fill.call(this,value,encoding(start),encoding(end),encoding(format));
    };
    proto.lastIndexOf=function(value,offset,format){
      if(typeof offset==='string'){format=offset;offset=undefined;}
      return lastIndexOf.call(this,value,offset,encoding(format));
    };
  }
  Object.defineProperty(globalThis,'Buffer',{value:buffers.Buffer,writable:true,configurable:true});
  const util=loadSdk('util');
  util.types=builtin('util/types');
  util.types.isKeyObject=value=>keyValues.has(value);
  core['util/types']=util.types;
  const path=loadSdk('path-browserify');
  path.posix=path;
  Object.assign(buffers,builtin('bufferExtra'));
  globalThis.atob=buffers.atob;globalThis.btoa=buffers.btoa;
  Object.assign(process,{execPath:'/node',getuid:()=>0,getBuiltinModule:name=>requireModule(name)});
  const output=fd=>{const stream=new EventEmitter();Object.assign(stream,{fd,isTTY:false,
    write(_chunk,_encoding,done){if(typeof _encoding==='function')_encoding();else done?.();return true;},
    ref(){return this;},unref(){return this;}});return stream;};
  const stdin=new EventEmitter();
  Object.assign(stdin,{fd:0,isTTY:false,readable:false,read:()=>null,
    pause(){return this;},resume(){return this;},setEncoding(){return this;},
    ref(){return this;},unref(){return this;}});
  process.stdin=stdin;process.stdout=output(1);process.stderr=output(2);
  let booting=false;
  const startupDiagnostic=args=>{
    if(!booting)return;
    const error=args.find(value=>value instanceof Error);
    if(error){base.diagnose(error,0);return;}
    const text=args.filter(value=>typeof value==='string').join(' ').slice(0,2048);
    if(text)base.diagnose(new Error(text),0);
  };
  globalThis.console=Object.freeze(Object.fromEntries(
    ['log','info','warn','error','debug','trace','group','groupCollapsed','groupEnd',
      'clear','table','dir','dirxml','time','timeEnd','timeLog','assert','count',
      'countReset','profile','profileEnd','timeStamp'].map(key=>
        [key,key==='error'?(...args)=>startupDiagnostic(args):()=>{}])));
  Object.assign(core,{events:EventEmitter,buffer:buffers,util,path,process,console:globalThis.console});
  const channels=new Map();
  core.diagnostics_channel={
    channel(name){if(!channels.has(name))channels.set(name,{
      name,listeners:new Set(),get hasSubscribers(){return this.listeners.size>0;},
      subscribe(fn){this.listeners.add(fn);},unsubscribe(fn){return this.listeners.delete(fn);},
      publish(value){for(const fn of this.listeners)fn(value,this.name);},
      stores:new Map(),bindStore(store,transform){this.stores.set(store,transform);},
      unbindStore(store){return this.stores.delete(store);},
      runStores(value,fn,receiver,...args){const entries=[...this.stores];let index=0;
        const run=()=>index<entries.length?(()=>{
          const [store,transform]=entries[index++];return store.run(transform?transform(value):value,run);})()
          :Reflect.apply(fn,receiver,args);this.publish(value);return run();},
    });return channels.get(name);},
    subscribe(name,fn){this.channel(name).subscribe(fn);},
    unsubscribe(name,fn){return this.channel(name).unsubscribe(fn);},
    hasSubscribers(name){return this.channel(name).hasSubscribers;},
  };
  core.async_hooks={
    AsyncLocalStorage:class {
      getStore(){return disabledStores.has(this)?undefined:asyncContext.values.get(this);}
      run(store,fn,...args){disabledStores.delete(this);const context=contextSnapshot();context.values.set(this,store);
        return runContext(context,fn,undefined,args);}
      enterWith(store){disabledStores.delete(this);const context=contextSnapshot(true);context.values.set(this,store);
        asyncContext=context;}
      disable(){disabledStores.add(this);}
      static bind(fn){const context=asyncContext;return function(...args){return runContext(context,fn,this,args);};}
      static snapshot(){const context=asyncContext;return (fn,...args)=>runContext(context,fn,undefined,args);}
    },
    AsyncResource:class {constructor(){this.trigger=asyncContext.id;this.context=contextSnapshot();}
      runInAsyncScope(fn,receiver,...args){return runContext(this.context,fn,receiver,args);}
      emitDestroy(){releaseContext(this.context);return this;}
      asyncId(){return this.context.id;}triggerAsyncId(){return this.trigger;}
      bind(fn,receiver){return (...args)=>this.runInAsyncScope(fn,receiver,...args);}
      static bind(fn){return core.async_hooks.AsyncLocalStorage.bind(fn);}},
    executionAsyncId:()=>asyncContext.id,triggerAsyncId:()=>asyncContext.id,
  };
  const then=Promise.prototype.then;
  Promise.prototype.then=function(yes,no){const context=asyncContext;
    const wrap=fn=>typeof fn==='function'?function(...args){return runContext(context,fn,this,args);}:fn;
    return Reflect.apply(then,this,[wrap(yes),wrap(no)]);};
  const querystring={
    parse(value){const result={};for(const part of String(value).split('&')){
      if(!part)continue;const [key,...rest]=part.split('=');
      const name=decodeURIComponent(key.replace(/\+/g,' ')),item=decodeURIComponent(rest.join('=').replace(/\+/g,' '));
      if(own.call(result,name))result[name]=[].concat(result[name],item);else result[name]=item;
    }return result;},
    stringify(value){return Object.entries(value).flatMap(([key,item])=>[].concat(item).map(v=>encodeURIComponent(key)+'='+encodeURIComponent(v??''))).join('&');},
    escape:encodeURIComponent,unescape:decodeURIComponent,
  };
  core.querystring=querystring;
  const assertion=(condition,message)=>{if(!condition){const error=new Error(message||'Assertion failed');error.code='ERR_ASSERTION';throw error;}};
  Object.assign(assertion,{ok:assertion,strict:assertion,
    equal:(a,b,message)=>assertion(a==b,message),strictEqual:(a,b,message)=>assertion(a===b,message),
    notStrictEqual:(a,b,message)=>assertion(a!==b,message),
    deepEqual:(a,b,message)=>assertion(stringify(a)===stringify(b),message),
    deepStrictEqual:(a,b,message)=>assertion(stringify(a)===stringify(b),message),
    fail:message=>assertion(false,message)});
  core.assert=assertion;
  const vm={
    runInThisContext:source=>{captureBootSource(source);return(0,eval)(source);},
    createContext:context=>context||{},isContext:context=>!!context&&typeof context==='object',
    runInContext:(source,context)=>{captureBootSource(source);
      return new Function('context','with(context){return eval('+stringify(source)+')}')(context);},
    runInNewContext:(source,context)=>vm.runInContext(source,context||{}),
    Script:class {constructor(source){this.source=String(source);}
      runInThisContext(){return vm.runInThisContext(this.source);}
      runInContext(context){return vm.runInContext(this.source,context);}
      runInNewContext(context){return vm.runInNewContext(this.source,context);}
    },
  };
  core.vm=vm;
  const nativeTimeout=globalThis.setTimeout,nativeInterval=globalThis.setInterval,nativeClear=globalThis.clearTimeout;
  function reportCallbackError(error){try{
    rawBridge('nodeCallbackError',stringify([errors.get(error)||'SCRIPT_EXCEPTION']));
  }catch{}}
  function timer(fn,delay,repeat,args){
    const guarded=(...values)=>{try{return fn(...values);}
      catch(error){reportCallbackError(error);}};
    const id=(repeat?nativeInterval:nativeTimeout)(guarded,delay,...args);
    return {id,ref(){return this;},unref(){return this;},hasRef:()=>true,refresh(){return this;},[Symbol.toPrimitive](){return id;}};}
  const bindAsync=fn=>{const context=asyncContext;return function(...args){
    return runContext(context,fn,this,args);};};
  globalThis.setTimeout=(fn,delay,...args)=>timer(bindAsync(fn),delay,false,args);
  globalThis.setInterval=(fn,delay,...args)=>timer(bindAsync(fn),delay,true,args);
  const microtask=globalThis.queueMicrotask;
  globalThis.queueMicrotask=fn=>microtask(bindAsync(()=>{try{return fn();}
    catch(error){reportCallbackError(error);}}));
  globalThis.clearTimeout=globalThis.clearInterval=id=>nativeClear(typeof id==='object'?id.id:id);
  globalThis.setImmediate=(fn,...args)=>globalThis.setTimeout(fn,0,...args);
  globalThis.clearImmediate=globalThis.clearTimeout;
  core.timers={setTimeout,clearTimeout,setInterval,clearInterval,setImmediate,clearImmediate};
  core['timers/promises']={
    setTimeout:(ms,value,options={})=>new Promise((resolve,reject)=>{
      let settled=false;
      const signal=options.signal;
      const finish=(callback,result)=>{
        if(settled)return;
        settled=true;clearTimeout(id);signal?.removeEventListener?.('abort',abort);callback(result);
      };
      const abort=()=>finish(reject,signal.reason||nodeError('CANCELLED'));
      const id=setTimeout(()=>finish(resolve,value),ms);
      if(signal){if(signal.aborted)abort();
        else signal.addEventListener('abort',abort,{once:true});}
    }),
    setImmediate:value=>Promise.resolve(value),
  };
  const Module=function(filename){this.filename=filename||'/source.js';this.exports={};};
  Module.prototype._compile=function(source,filename){
    captureBootSource(source);
    return new Function('exports','require','module','__filename','__dirname',source)
      (this.exports,request=>requireModule(request,filename),this,filename,path.dirname(filename));};
  Module.createRequire=filename=>request=>requireModule(request,String(filename).replace(/^file:\/\//,''));
  Module._load=(name,parent)=>requireModule(name,parent?.filename);
  Module.builtinModules=Object.keys(core);core.module=Module;
  function requireModule(name,parent){
    name=String(name).replace(/^node:/,'');
    if(name==='process/'||name==='string_decoder/')name=name.slice(0,-1);
    if(own.call(core,name))return core[name];
    if(name==='fs'){
      const module=builtin(name);
      if(typeof module.realpath==='function')module.realpath.native=module.realpath;
      if(typeof module.realpathSync==='function')module.realpathSync.native=module.realpathSync;
      core[name]=module;return module;
    }
    if(name==='stream'){
      core.stream=loadSdk('readable-stream/lib/stream.js');return core.stream;
    }
    if(name==='stream/promises')return requireModule('stream').promises;
    if(['http','https'].includes(name)){
      if(!own.call(core,name)){
        const module=builtin(name),streams=requireModule('stream');
        const {IncomingMessage,ServerResponse}=name==='https'?requireModule('http')
          :createHttpMessageClasses(streams,module.STATUS_CODES);
        module.IncomingMessage=IncomingMessage;module.ServerResponse=ServerResponse;
        const remoteProto=(role,local)=>{const sample={};for(const key of roleMethods[role]||[]){
          if(key.startsWith('@@'))continue;const fallback=local?.[key];
          sample[key]=function(...args){
            const id=nativeHandles.get(this);
            if(!id){if(typeof fallback==='function')return Reflect.apply(fallback,this,args);
              throw nodeError('PERMISSION_DENIED');}
            if(handleStates.get(this)?.released)throw nodeError('CANCELLED');
            const frames=[];const response=rpc({operation:'invoke',id,key,
              args:encode(args,new Set(),frames)},frames);
            return decode(response.value,response.frames);};}return sample;};
        if(name==='http')Object.assign(ServerResponse.prototype,remoteProto('response',ServerResponse.prototype));
        rolePrototypes.set('incoming',IncomingMessage.prototype);rolePrototypes.set('response',ServerResponse.prototype);
        Object.assign(module.Server.prototype,remoteProto('server'));
        rolePrototypes.set('server',module.Server.prototype);core[name]=module;
      }
      return core[name];
    }
    if(name==='string_decoder')return builtin(name);
    if(parent&&parent.startsWith('/sdk/')&&sdkNativeModules.has(name)){
      if(!own.call(core,name))core[name]=builtin(name);
      return core[name];
    }
    if(parent&&parent.startsWith('/sdk/'))return loadSdk(name,parent);
    if(name.startsWith('.')||name.startsWith('/')){
      const fs=requireModule('fs'),filename=path.resolve(parent?path.dirname(parent):'/',name);
      const candidates=[filename,filename+'.js',filename+'.json',path.join(filename,'index.js')];
      const found=candidates.find(file=>fs.existsSync(file)&&fs.statSync(file).isFile());
      if(!found)throw nodeError('ENOENT');
      if(modules.has(found))return modules.get(found).exports;
      const module=new Module(found);modules.set(found,module);
      const source=fs.readFileSync(found,'utf8');
      if(found.endsWith('.json'))module.exports=parse(source);else module._compile(source,found);
      return module.exports;
    }
    if(['supports-color','@opentelemetry/api'].includes(name))return loadSdk(name);
    if(!own.call(core,name))core[name]=builtin(name);
    return core[name];
  }
  const urls=requireModule('url');
  globalThis.URL=urls.URL;globalThis.URLSearchParams=urls.URLSearchParams;
  Object.assign(util,builtin('encoding'));
  globalThis.TextEncoder=util.TextEncoder;globalThis.TextDecoder=util.TextDecoder;
  globalThis.Blob=buffers.Blob;globalThis.File=buffers.File;
  const webStreams=requireModule('stream/web');
  for(const name of ['ReadableStream','WritableStream','TransformStream',
    'CompressionStream','DecompressionStream','TextEncoderStream','TextDecoderStream',
    'ByteLengthQueuingStrategy','CountQueuingStrategy'])
    if(typeof webStreams[name]==='function')globalThis[name]=webStreams[name];
  if(typeof globalThis.DOMException!=='function'){
    globalThis.DOMException=class DOMException extends Error{
      constructor(message='',name='Error'){super(String(message));this.name=String(name);}
    };
  }
  const eventStates=new WeakMap(),targetStates=new WeakMap();
  class Event {
    constructor(type,init={}){
      if(arguments.length===0)throw new TypeError('Event type is required');
      eventStates.set(this,{type:String(type),bubbles:Boolean(init?.bubbles),
        cancelable:Boolean(init?.cancelable),composed:Boolean(init?.composed),
        target:null,currentTarget:null,eventPhase:0,defaultPrevented:false,
        stopped:false,immediate:false,dispatching:false,passive:false,timeStamp:Date.now()});
    }
    get type(){return eventStates.get(this)?.type??'';}
    get bubbles(){return Boolean(eventStates.get(this)?.bubbles);}
    get cancelable(){return Boolean(eventStates.get(this)?.cancelable);}
    get composed(){return Boolean(eventStates.get(this)?.composed);}
    get target(){return eventStates.get(this)?.target??null;}
    get currentTarget(){return eventStates.get(this)?.currentTarget??null;}
    get srcElement(){return this.target;}
    get eventPhase(){return eventStates.get(this)?.eventPhase??0;}
    get defaultPrevented(){return Boolean(eventStates.get(this)?.defaultPrevented);}
    get timeStamp(){return eventStates.get(this)?.timeStamp??0;}
    get isTrusted(){return false;}
    get cancelBubble(){return Boolean(eventStates.get(this)?.stopped);}
    set cancelBubble(value){if(value)this.stopPropagation();}
    get returnValue(){return !this.defaultPrevented;}
    set returnValue(value){if(!value)this.preventDefault();}
    composedPath(){const target=this.target;return target?[target]:[];}
    stopPropagation(){const state=eventStates.get(this);if(state)state.stopped=true;}
    stopImmediatePropagation(){const state=eventStates.get(this);
      if(state){state.stopped=true;state.immediate=true;}}
    preventDefault(){const state=eventStates.get(this);
      if(state?.cancelable&&!state.passive)state.defaultPrevented=true;}
    initEvent(type,bubbles=false,cancelable=false){const state=eventStates.get(this);
      if(!state||state.dispatching)return;
      Object.assign(state,{type:String(type),bubbles:Boolean(bubbles),cancelable:Boolean(cancelable),
        composed:false,target:null,currentTarget:null,eventPhase:0,defaultPrevented:false,
        stopped:false,immediate:false});}
  }
  for(const [name,value]of Object.entries({NONE:0,CAPTURING_PHASE:1,AT_TARGET:2,BUBBLING_PHASE:3})){
    Object.defineProperty(Event,name,{value});
    Object.defineProperty(Event.prototype,name,{value});
  }
  Object.defineProperty(Event.prototype,Symbol.toStringTag,{value:'Event',configurable:true});
  class CustomEvent extends Event {
    constructor(type,init={}){super(type,init);this.detail=init?.detail??null;}
    initCustomEvent(type,bubbles=false,cancelable=false,detail=null){
      this.initEvent(type,bubbles,cancelable);this.detail=detail;}
  }
  class EventTarget {
    constructor(){targetStates.set(this,new Map());}
    addEventListener(type,callback,options={}){
      if(callback===null||callback===undefined)return;
      if(typeof callback!=='function'&&typeof callback?.handleEvent!=='function')return;
      const state=targetStates.get(this);if(!state)throw new TypeError('Invalid EventTarget');
      type=String(type);
      const capture=typeof options==='boolean'?options:Boolean(options?.capture);
      const records=state.get(type)??[];
      if(records.some(record=>record.callback===callback&&record.capture===capture))return;
      const signal=typeof options==='object'&&options?options.signal:null;
      if(signal?.aborted)return;
      const record={callback,capture,once:Boolean(typeof options==='object'&&options?.once),
        passive:Boolean(typeof options==='object'&&options?.passive),signal,abort:null};
      records.push(record);state.set(type,records);
      if(signal&&typeof signal.addEventListener==='function'){
        record.abort=()=>this.removeEventListener(type,callback,{capture});
        signal.addEventListener('abort',record.abort,{once:true});
      }
    }
    removeEventListener(type,callback,options={}){
      const state=targetStates.get(this);if(!state)return;
      type=String(type);
      const capture=typeof options==='boolean'?options:Boolean(options?.capture);
      const records=state.get(type);if(!records)return;
      const index=records.findIndex(record=>record.callback===callback&&record.capture===capture);
      if(index<0)return;
      const [record]=records.splice(index,1);
      record.signal?.removeEventListener?.('abort',record.abort);
      if(records.length===0)state.delete(type);
    }
    dispatchEvent(event){
      const listeners=targetStates.get(this),state=eventStates.get(event);
      if(!listeners)throw new TypeError('Invalid EventTarget');
      if(!state)throw new TypeError('Argument is not an Event');
      if(state.dispatching)throw new DOMException('Event is already being dispatched','InvalidStateError');
      state.dispatching=true;state.target=this;state.currentTarget=this;state.eventPhase=2;
      state.stopped=false;state.immediate=false;
      try{
        for(const record of [...(listeners.get(state.type)??[])]){
          if(!(listeners.get(state.type)??[]).includes(record))continue;
          if(record.once)this.removeEventListener(state.type,record.callback,{capture:record.capture});
          state.passive=record.passive;
          try{
            if(typeof record.callback==='function')record.callback.call(this,event);
            else record.callback.handleEvent.call(record.callback,event);
          }catch(error){base.diagnose(error,0);}
          if(state.immediate)break;
        }
      }finally{
        state.passive=false;state.currentTarget=null;state.eventPhase=0;state.dispatching=false;
      }
      return !state.defaultPrevented;
    }
  }
  Object.defineProperty(CustomEvent.prototype,Symbol.toStringTag,{value:'CustomEvent',configurable:true});
  Object.defineProperty(EventTarget.prototype,Symbol.toStringTag,{value:'EventTarget',configurable:true});
  globalThis.Event=Event;globalThis.CustomEvent=CustomEvent;globalThis.EventTarget=EventTarget;
  if(typeof globalThis.structuredClone!=='function'){
    globalThis.structuredClone=(input,options={})=>{
      const transferred=new Set();
      if(options&&options.transfer!==undefined){
        if(!Array.isArray(options.transfer))throw new TypeError('transfer must be an array');
        for(const value of options.transfer){
          if(!(value instanceof ArrayBuffer)||transferred.has(value)){
            const error=new Error('Value is not transferable');error.name='DataCloneError';throw error;
          }
          transferred.add(value);
        }
      }
      const seen=new Map();
      const clone=value=>{
        if(value===null||['undefined','string','number','boolean','bigint'].includes(typeof value))
          return value;
        if(typeof value==='symbol'||typeof value==='function'||nativeHandles.has(value)){
          const error=new Error('Value is not cloneable');error.name='DataCloneError';throw error;
        }
        if(seen.has(value))return seen.get(value);
        if(value instanceof Date)return new Date(value.getTime());
        if(value instanceof RegExp)return new RegExp(value.source,value.flags);
        if(value instanceof ArrayBuffer){
          const result=value.slice(0);seen.set(value,result);return result;
        }
        if(ArrayBuffer.isView(value)){
          if(globalThis.Buffer?.isBuffer?.(value)){
            const result=globalThis.Buffer.from(value);seen.set(value,result);return result;
          }
          const buffer=clone(value.buffer);
          const result=value instanceof DataView
            ?new DataView(buffer,value.byteOffset,value.byteLength)
            :new value.constructor(buffer,value.byteOffset,value.length);
          seen.set(value,result);return result;
        }
        if(value instanceof Map){
          const result=new Map();seen.set(value,result);
          for(const [key,item] of value)result.set(clone(key),clone(item));
          return result;
        }
        if(value instanceof Set){
          const result=new Set();seen.set(value,result);
          for(const item of value)result.add(clone(item));
          return result;
        }
        if(value instanceof Error){
          const result=new Error(value.message);seen.set(value,result);
          result.name=value.name;if(value.code!==undefined)result.code=clone(value.code);
          if(value.cause!==undefined)result.cause=clone(value.cause);
          return result;
        }
        const result=Array.isArray(value)?[]:
          Object.getPrototypeOf(value)===null?Object.create(null):{};
        seen.set(value,result);
        for(const key of Object.keys(value))result[key]=clone(value[key]);
        return result;
      };
      const result=clone(input);
      for(const value of transferred)if(typeof value.transfer==='function')value.transfer(0);
      return result;
    };
  }
  globalThis.navigator=Object.freeze({userAgent:'Node.js/24',platform:'wasm32',
    hardwareConcurrency:1,language:'en-US',languages:Object.freeze(['en-US'])});
  globalThis.performance=requireModule('perf_hooks').performance;
  if(typeof globalThis.performance.markResourceTiming!=='function')
    globalThis.performance.markResourceTiming=()=>{};
  const nodeCrypto=requireModule('crypto'),webcrypto=nodeCrypto.webcrypto;
  const nativeGetRandomValues=webcrypto.getRandomValues;
  webcrypto.getRandomValues=value=>{nativeGetRandomValues(value);return value;};
  const nativeDigest=webcrypto.subtle.digest;
  webcrypto.subtle.digest=async(...args)=>{
    const value=await nativeDigest(...args);
    return ArrayBuffer.isView(value)
      ?value.buffer.slice(value.byteOffset,value.byteOffset+value.byteLength):value;
  };
  if(typeof nodeCrypto.getRandomValues==='function'){
    const fill=nodeCrypto.getRandomValues;
    nodeCrypto.getRandomValues=value=>{fill(value);return value;};
  }
  nodeCrypto.subtle=webcrypto.subtle;
  globalThis.crypto=webcrypto;
  const AbortSignal=class extends EventTarget {
    constructor(){super();this.aborted=false;this.reason=undefined;this._onabort=null;}
    get onabort(){return this._onabort;}
    set onabort(value){if(this._onabort)this.removeEventListener('abort',this._onabort);
      this._onabort=typeof value==='function'?value:null;
      if(this._onabort)this.addEventListener('abort',this._onabort);}
    throwIfAborted(){if(this.aborted)throw this.reason;}
    static abort(reason=nodeError('CANCELLED')){const ctl=new AbortController();ctl.abort(reason);return ctl.signal;}
    static timeout(ms){const ctl=new AbortController();setTimeout(()=>ctl.abort(nodeError('TIMEOUT')),ms);return ctl.signal;}
    static any(signals){const ctl=new AbortController(),listeners=[],sources=[...signals];
      if(sources.some(signal=>!signal||typeof signal.addEventListener!=='function'))
        throw new TypeError('AbortSignal expected');
      const finish=signal=>{for(const [source,listener]of listeners)
        source.removeEventListener?.('abort',listener);
        listeners.length=0;ctl.abort(signal.reason);};
      for(const signal of sources){
        if(signal.aborted){finish(signal);break;}
        const listener=()=>finish(signal);listeners.push([signal,listener]);
        signal.addEventListener('abort',listener,{once:true});
      }
      return ctl.signal;}
  };
  const AbortController=class {constructor(){this.signal=new AbortSignal();}
    abort(reason=nodeError('CANCELLED')){if(!this.signal.aborted){this.signal.aborted=true;this.signal.reason=reason;
      this.signal.dispatchEvent(new Event('abort'));}}};
  Object.defineProperty(AbortSignal.prototype,Symbol.toStringTag,{value:'AbortSignal',configurable:true});
  Object.defineProperty(AbortController.prototype,Symbol.toStringTag,{value:'AbortController',configurable:true});
  globalThis.AbortSignal=AbortSignal;globalThis.AbortController=AbortController;
  class Headers {
    constructor(input={}){this._values=new Map();this._cookies=[];for(const [key,value] of input instanceof Headers?input.entries():Array.isArray(input)?input:Object.entries(input||{}))this.append(key,value);
      if(input instanceof Headers)this._cookies=input.getSetCookie();}
    append(key,value){key=String(key).toLowerCase();if(key==='set-cookie')this._cookies.push(...[].concat(value));
      this._values.set(key,this._values.has(key)?this._values.get(key)+', '+value:String(value));}
    set(key,value){key=String(key).toLowerCase();this._values.set(key,String(value));if(key==='set-cookie')this._cookies=[String(value)];}
    get(key){return this._values.get(String(key).toLowerCase())??null;}
    getSetCookie(){return [...this._cookies];}
    has(key){return this._values.has(String(key).toLowerCase());}
    delete(key){key=String(key).toLowerCase();if(key==='set-cookie')this._cookies=[];return this._values.delete(key);}
    entries(){return this._values.entries();}keys(){return this._values.keys();}values(){return this._values.values();}
    forEach(fn,receiver){for(const [key,value]of this._values)fn.call(receiver,value,key,this);}
    [Symbol.iterator](){return this.entries();}
  }
  class FormData {
    constructor(){this._values=[];}
    append(name,value,filename){
      name=String(name);
      const blob=value instanceof Blob||value instanceof File;
      this._values.push([name,blob?value:String(value),blob
        ?String(filename===undefined?(value instanceof File?value.name:'blob'):filename):null]);
    }
    set(name,value,filename){
      name=String(name);const at=this._values.findIndex(item=>item[0]===name);
      this.delete(name);this.append(name,value,filename);
      if(at>=0)this._values.splice(at,0,this._values.pop());
    }
    get(name){return this._values.find(item=>item[0]===String(name))?.[1]??null;}
    getAll(name){name=String(name);return this._values.filter(item=>item[0]===name).map(item=>item[1]);}
    has(name){name=String(name);return this._values.some(item=>item[0]===name);}
    delete(name){name=String(name);this._values=this._values.filter(item=>item[0]!==name);}
    entries(){return this._values.map(([name,value])=>[name,value])[Symbol.iterator]();}
    keys(){return this._values.map(item=>item[0])[Symbol.iterator]();}
    values(){return this._values.map(item=>item[1])[Symbol.iterator]();}
    forEach(fn,receiver){for(const [name,value]of this._values)fn.call(receiver,value,name,this);}
    [Symbol.iterator](){return this.entries();}
  }
  class Response {
    constructor(body=null,options={}){let stream=options.stream||null;
      if(!stream&&body&&typeof body.getReader==='function')stream=body;
      this._body=stream?null:body;this.status=options.status??200;this.statusText=options.statusText||'';
      this.headers=new Headers(options.headers);this.url=options.url||'';this.ok=this.status>=200&&this.status<300;
      this.redirected=options.redirected||false;this.type='basic';this.bodyUsed=false;this.body=stream;}
    async bytes(){if(this.bodyUsed)throw new TypeError('Body already consumed');this.bodyUsed=true;
      if(this.body){const reader=this.body.getReader(),chunks=[];
        try{for(;;){const {done,value}=await reader.read();if(done)break;chunks.push(Buffer.from(value));}
          return Buffer.concat(chunks);}finally{reader.releaseLock();}}
      if(this._body instanceof Blob)return Buffer.from(await this._body.arrayBuffer());
      if(this._body instanceof ArrayBuffer)return Buffer.from(this._body);
      if(ArrayBuffer.isView(this._body))return Buffer.from(this._body.buffer,this._body.byteOffset,this._body.byteLength);
      if(this._body&&typeof this._body.then==='function')return await this._body;
      return Buffer.from(this._body==null?'':String(this._body));}
    async text(){return Buffer.from(await this.bytes()).toString();}async json(){return parse(await this.text());}
    async arrayBuffer(){const bytes=await this.bytes();return bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength);}
    async blob(){return new Blob([await this.bytes()],{type:this.headers.get('content-type')||''});}
    clone(){if(this.bodyUsed)throw new TypeError('Body already consumed');
      if(this.body){const [one,two]=this.body.tee();this.body=one;return new Response(null,{...this,stream:two});}
      return new Response(this._body,this);}
  }
  class Request {
    constructor(input,options={}){Object.assign(this,input instanceof Request?input:{url:String(input)},options);
      this.method=String(this.method||'GET').toUpperCase();this.headers=new Headers(this.headers);}
  }
  globalThis.Headers=Headers;globalThis.Request=Request;globalThis.Response=Response;
  globalThis.FormData=FormData;
  globalThis.fetch=async(input,options={})=>{
    const original=new Request(input,options);
    let body=original.body;
    if(body instanceof Blob)body=Buffer.from(await body.arrayBuffer());
    if(body instanceof URLSearchParams){original.headers.set('content-type','application/x-www-form-urlencoded');body=body.toString();}
    if(body instanceof FormData){
      const boundary='----meowfilm-'+nodeCrypto.randomBytes(12).toString('hex'),chunks=[];
      const quote=value=>String(value).replace(/[\r\n]/g,' ').replace(/"/g,'%22');
      for(const [name,value,filename]of body._values){
        let header='--'+boundary+'\r\nContent-Disposition: form-data; name="'+quote(name)+'"';
        if(value instanceof Blob||value instanceof File){
          header+='; filename="'+quote(filename)+'"\r\nContent-Type: '+(value.type||'application/octet-stream');
          chunks.push(Buffer.from(header+'\r\n\r\n'),Buffer.from(await value.arrayBuffer()),Buffer.from('\r\n'));
        }else chunks.push(Buffer.from(header+'\r\n\r\n'+value+'\r\n'));
      }
      chunks.push(Buffer.from('--'+boundary+'--\r\n'));
      body=Buffer.concat(chunks);
      original.headers.set('content-type','multipart/form-data; boundary='+boundary);
    }
    async function send(req,body,hops){
      const u=new URL(req.url),native=requireModule(u.protocol==='https:'?'https':'http');
      if(!['http:','https:'].includes(u.protocol))throw nodeError('PERMISSION_DENIED');
      return new Promise((resolve,reject)=>{
        let abort,responded=false,settled=false,client,detachClient=()=>{},cancelBody=()=>{};
        const detachAbort=()=>req.signal?.removeEventListener('abort',abort);
        client=native.request(u.href,{method:req.method,headers:Object.fromEntries(req.headers)},res=>{
          if(settled){try{res.destroy();}catch{}return;}
          responded=true;detachClient();
          try{
            if([301,302,303,307,308].includes(res.statusCode)&&res.headers.location&&req.redirect!=='manual'){
              if(req.redirect==='error'||hops>=20){
                settled=true;detachAbort();res.destroy();client.destroy();reject(nodeError('NETWORK_ERROR'));return;
              }
              const next=new URL(res.headers.location,u.href),headers=new Headers(req.headers);
              if(next.origin!==u.origin){headers.delete('authorization');headers.delete('cookie');}
              const drop=res.statusCode===303&&req.method!=='HEAD'||[301,302].includes(res.statusCode)&&req.method==='POST';
              if(drop){headers.delete('content-type');headers.delete('content-length');}
              detachAbort();res.destroy();settled=true;
              send(new Request(next.href,{...req,url:next.href,headers,method:drop?'GET':req.method}),
                drop?undefined:body,hops+1).then(resolve,reject);return;
            }
            let source=res,decompressor=null;
            const compression=String(res.headers['content-encoding']||'');
            const decodeStream={gzip:'createGunzip',deflate:'createInflate',br:'createBrotliDecompress'}[compression];
            if(decodeStream){decompressor=requireModule('zlib')[decodeStream]();source=res.pipe(decompressor);}
            const ReadableStream=requireModule('stream/web').ReadableStream;
            let controller,bodyDone=false,cleanupBody=()=>{};
            const finishBody=(callback,input)=>{if(bodyDone)return;bodyDone=true;let failure;
              try{detachAbort();}catch(error){failure??=error;}
              try{cleanupBody();}catch(error){failure??=error;}
              try{callback(input);}catch(error){failure??=error;}
              if(failure)try{controller.error(failure);}catch{}};
            cancelBody=reason=>{if(!bodyDone)finishBody(value=>controller.error(value),reason);
              try{source.destroy();}catch{}if(source!==res)try{res.destroy();}catch{}
              try{client.destroy();}catch{}};
            const stream=new ReadableStream({
              start(value){controller=value;source.pause();
                const data=chunk=>{controller.enqueue(Buffer.from(chunk));if(controller.desiredSize<=0)source.pause();};
                const end=()=>finishBody(()=>controller.close());
                const failed=()=>cancelBody(nodeError('NETWORK_ERROR'));
                const closed=()=>cancelBody(nodeError('NETWORK_ERROR'));
                const aborted=()=>cancelBody(nodeError('NETWORK_ERROR'));
                const upstreamClosed=()=>{if(!res.complete&&!res.readableEnded)
                  cancelBody(nodeError('NETWORK_ERROR'));};
                cleanupBody=()=>{let failure;
                  for(const [target,event,listener]of [[source,'data',data],[source,'end',end],
                    [source,'error',failed],[source,'close',closed],[res,'aborted',aborted],
                    ...(source===res?[]:[[res,'error',failed],[res,'close',upstreamClosed]])])
                    try{target.off(event,listener);}catch(error){failure??=error;}
                  if(failure)throw failure;};
                try{source.on('data',data);source.once('end',end);
                  source.once('error',failed);source.once('close',closed);res.once('aborted',aborted);
                  if(source!==res){res.once('error',failed);res.once('close',upstreamClosed);}}
                catch(error){cancelBody(error);throw error;}},
              pull(){if(!bodyDone)source.resume();},cancel(reason){cancelBody(reason||nodeError('CANCELLED'));},
            });
            settled=true;
            resolve(new Response(null,{stream,status:res.statusCode,statusText:res.statusMessage,
              headers:res.headers,url:u.href,redirected:hops>0}));
          }catch(error){
            settled=true;try{detachAbort();}catch{}try{res.destroy();}catch{}
            try{client.destroy();}catch{}reject(error);
          }
        });
        const failed=error=>{if(settled)return;settled=true;
          try{detachClient();}catch{}try{detachAbort();}catch{}reject(error);};
        const closed=()=>{if(!responded)failed(nodeError('NETWORK_ERROR'));};
        detachClient=()=>{client.off('error',failed);client.off('close',closed);};
        try{
          client.once('error',failed);
          client.once('close',closed);
          if(req.signal){abort=()=>{const reason=req.signal.reason||nodeError('CANCELLED');
            if(!settled){settled=true;try{detachClient();}catch{}try{detachAbort();}catch{}
              try{client.destroy();}catch{}reject(reason);}
            else cancelBody(reason);};
            if(req.signal.aborted){abort();return;}req.signal.addEventListener('abort',abort,{once:true});}
          client.end(body);
        }catch(error){if(!settled){settled=true;
          try{detachClient();}catch{}try{detachAbort();}catch{}}
          try{client.destroy();}catch{}reject(error);}
      });
    }
    return send(original,body,0);
  };
  globalThis.catServerFactory=(handler,options)=>requireModule('http').createServer(options||{},handler);
  globalThis.require=requireModule;
  let stop,bootApplications=[];
  const bootRoutePaths=new Map(),applicationRoutes=new WeakMap(),bootSources=new Set();
  const bootPatternRoutes=[];
  const httpMethods=['POST','GET','PUT','PATCH','DELETE','HEAD','OPTIONS'];
  const actionName=value=>typeof value==='string'&&/^[A-Za-z][\w-]{0,63}$/.test(value);
  const blockedAction=value=>['config','full-config','website'].includes(value);
  const recordRoute=(routes,path,methods)=>{
    if(typeof path!=='string'||!path.startsWith('/')||path.startsWith('//')||
      path.includes('\\')||path.includes('$'+'{'))return;
    const clean=path.split(/[?#]/,1)[0].replace(/\/+$/,'')||'/';
    const known=routes.get(clean)||new Set();
    for(const method of [].concat(methods||'POST')){
      const value=String(method).toUpperCase();
      if(httpMethods.includes(value))known.add(value);
    }
    if(known.size)routes.set(clean,known);
  };
  const captureBootSource=source=>{
    if(booting&&typeof source==='string')bootSources.add(source);
  };
  const sourceRoutes=()=>{
    if(!bootSources.size)return;
    const parser=loadSdk('acorn').parse;
    for(const text of bootSources){
      const {paths,patterns}=discoverSourceRoutes(text,parser);
      for(const [path,method]of paths)recordRoute(bootRoutePaths,path,method);
      for(const pattern of patterns)bootPatternRoutes.push({...pattern,
        regex:new RegExp(pattern.source,pattern.flags)});
    }
    bootSources.clear();
  };
  const routeAction=(path,base)=>{
    const segments=path.split('/'),prefix=base.split('/');
    if(segments.length!==prefix.length+1)return null;
    try{
      if(!prefix.every((part,index)=>/^:[A-Za-z_$][\w$]*$/.test(segments[index])
        ?part.length>0:decodeURIComponent(part)===decodeURIComponent(segments[index])))return null;
      return decodeURIComponent(segments[segments.length-1]);
    }catch{return null;}
  };
  const functionText=fn=>{try{return typeof fn==='function'?Function.prototype.toString.call(fn):'';}catch{return'';}};
  const routeApplication=value=>{
    if(!value||nativeHandles.has(value)||!['object','function'].includes(typeof value))return false;
    try{return typeof value.hasRoute==='function';}catch{return false;}
  };
  const addApplication=(application,observe=false)=>{
    if(!routeApplication(application))return;
    if(!bootApplications.includes(application))bootApplications.push(application);
    if(!observe||applicationRoutes.has(application)||typeof application.addHook!=='function'||
      !nativeHandles.has(application.server))return;
    const routes=new Map(),server=application.server;
    const entry={routes,listening:null};
    entry.listening=new Promise((resolve,reject)=>{
      const finish=error=>{
        server.off('listening',listened);server.off('error',failed);
        error?reject(error):resolve();
      };
      const listened=()=>finish(),failed=error=>finish(error);
      server.once('listening',listened);server.once('error',failed);
    });
    entry.listening.catch(()=>{});
    applicationRoutes.set(application,entry);
    application.addHook('onRoute',route=>{
      if(booting)recordRoute(routes,route.url||route.path,route.method);
    });
    application.addHook('onRequest',(request,_reply,done)=>{
      // This is the script's own local-service credential, never a host token.
      // Only requests already admitted by ScriptServices reach this hook.
      if(typeof application.internalAuthSecret==='string'&&application.internalAuthSecret)
        request.headers={...request.headers,'x-internal-auth':application.internalAuthSecret};
      done();
    });
    application.addHook('onError',(_request,_reply,error,done)=>{
      try{base.diagnose(error,rpc({operation:'requestId'}).value);}catch{}
      done();
    });
  };
  core.diagnostics_channel.subscribe('fastify.initialization',value=>{
    if(booting)addApplication(value?.fastify,true);
  });
  const startupSignals=text=>{
    const factory=/\bcatServerFactory\b/.test(text);
    const listen=/(?:\.listen|\[\s*["']listen["']\s*\])\s*\(/.test(text);
    const register=/(?:\.register|\[\s*["']register["']\s*\])\s*\(/.test(text);
    const port=/process\.env\.(?:PORT|DEV_HTTP_PORT|HTTP_PORT)|process\.env\[\s*["'](?:PORT|DEV_HTTP_PORT|HTTP_PORT)["']\s*\]/.test(text);
    const bridge=/\bmessageToDart\b/.test(text),dartPort=/\bcatDartServerPort\b/.test(text);
    return {strong:factory||listen&&(register||port),
      score:(factory?24:0)+(listen?12:0)+(register?8:0)+(port?6:0)+(dartPort?2:0)+(bridge?1:0)};
  };
  const stopSignals=text=>{
    const close=/(?:\.(?:close|destroy)|\[\s*["'](?:close|destroy)["']\s*\])\s*\(/.test(text);
    if(!close&&!/closeAllConnections|\.stop\s*=\s*(?:!0|true)/.test(text))return 0;
    return (close?12:0)+(/closeAllConnections/.test(text)?8:0)+
      (/\.stop\s*=\s*(?:!0|true)/.test(text)?8:0)+(/=\s*null\b/.test(text)?2:0)+
      (/(?:clearTimeout|clearInterval)\s*\(/.test(text)?1:0);
  };
  const linkedStartup=(text,resolve,lookup)=>{
    try{
      const acorn=loadSdk('acorn');
      const target=startupWrapperTarget(acorn.parseExpressionAt(text,0,{ecmaVersion:'latest'}));
      if(!target)return false;
      if(typeof resolve==='function'){
        const fn=resolve(target);
        if(typeof fn==='function')return startupSignals(functionText(fn)).strong;
      }
      return lookup(target);
    }catch{return false;}
  };
  return {
    ...base,errorCode:error=>{
      const code=errors.get(error)||base.errorCode(error);
      return code==='MEMORY_LIMIT'?'RESOURCE_LIMIT':code;
    },
    nativeReleased(json){releaseNative(json);},
    nativeCallback(id,json,receiverJSON,fresh,...frames){
      const callback=guestCallbacks.get(Number(id));
      if(!callback)return [stringify({context:0,value:{$:'undefined'}})];
      const context=Number(fresh)?contextSnapshot():callback.context;
      const answer=(value,outFrames=[])=>contextActive(context)
        ?[stringify({context:registerContext(context),value}),...outFrames]
        :[stringify({context:0,value:{$:'undefined'}})];
      if(!contextActive(context))
        return answer({$:'undefined'});
      try{return runContext(context,()=>{
        const value=Reflect.apply(callback.fn,
          decode(parse(receiverJSON),frames),decode(parse(json),frames));
        if(value&&typeof value.then==='function'){
          const receipt=++nextCallback;
          const done=(ok,value)=>{
            if(context!==rootContext&&!contextActive(context))return;
            if(!ok)try{rawBridge('nodeCallbackError',stringify([value.code]));}catch{}
            const outFrames=[];try{rpcAt({operation:'callbackDone',id:receipt,ok,
              value:encode(value,new Set(),outFrames)},context,outFrames);}
            catch(error){rawBridge('nodeCallbackError',stringify([errors.get(error)||'SCRIPT_EXCEPTION']));}
          };
          Reflect.apply(then,Promise.resolve(value),[
            result=>done(true,result),
            error=>done(false,{code:callbackFailureCode(error)}),
          ]);
          return answer({$:'callbackPromise',id:receipt});
        }
        const outFrames=[];const encoded=encode(value,new Set(),outFrames);
        return answer(encoded,outFrames);
      },undefined,[]);
      }catch(error){rawBridge('nodeCallbackError',stringify([callbackFailureCode(error)]));
        return answer({$:'undefined'});}
    },
    nativeSettled(id,json,context,...frames){const selected=Number(context)||0;
      const target=asyncContexts.get(selected)??rootContext;
      return runContext(contextActive(target)?target:rootContext,
        finish,undefined,[id,parse(json),frames]);},
    catDescribe(json){
      booting=false;
      const sites=parse(json);
      const result=sites.map(([key,site])=>{
        const target=new URL(site.api),base=target.pathname.replace(/\/$/,'');
        const port=Number(target.port||(target.protocol==='https:'?443:80));
        const application=bootApplications.find(app=>{
          try{return applicationRoutes.has(app)&&app.server.address()?.port===port;}catch{return false;}
        });
        const declared=Array.isArray(site.actions)&&site.actions.every(actionName);
        if(!application&&!declared)sourceRoutes();
        const routes=application?applicationRoutes.get(application).routes:bootRoutePaths;
        const methods={};
        for(const [path,available]of routes){
          const action=routeAction(path,base);
          if(!actionName(action)||blockedAction(action))continue;
          methods[action]=httpMethods.find(method=>available.has(method));
        }
        if(!application)for(const pattern of bootPatternRoutes)
          for(const action of pattern.actions)
            if(actionName(action)&&!blockedAction(action)&&pattern.regex.test(base+'/'+action))
              methods[action]??=pattern.method;
        const actions=declared?[...new Set(site.actions)]:Object.keys(methods);
        return [key,{actions,methods:Object.fromEntries(actions.map(action=>
          [action,methods[action]||'POST'])),discovery:declared?'declared':application?
          'registered-routes':actions.length?'source-routes':'opaque-http'}];
      });
      bootSources.clear();bootRoutePaths.clear();bootPatternRoutes.length=0;
      for(const app of bootApplications)applicationRoutes.get(app)?.routes.clear();
      return stringify(result);
    },
    catBoot(source,json){
      try {
        booting=true;
        bootRoutePaths.clear();
        bootSources.clear();bootPatternRoutes.length=0;
        captureBootSource(source);
        const options=parse(json);
        Object.assign(process.env,options.env);
        const module=new Module('/source.js');
        modules.set('/source.js',module);
        Object.assign(globalThis,{module,exports:module.exports,__filename:'/source.js',__dirname:'/',
          catDartServerPort:()=>0,messageToDart:async()=>null});
        const before=new Set(Object.getOwnPropertyNames(globalThis));
        const value=(0,eval)(source+'\n;({exports:module.exports,start:typeof start==="function"?start:null,stop:typeof stop==="function"?stop:null,resolve(name){try{return eval(name)}catch{return null}}})');
        const addedDescriptors=Object.getOwnPropertyDescriptors(globalThis);
        const added=Object.entries(addedDescriptors).filter(([key,descriptor])=>!before.has(key)&&
          own.call(descriptor,'value')&&typeof descriptor.value==='function').map(([key,descriptor],order)=>{
            const fn=descriptor.value,text=functionText(fn);
            return {key,fn,text,order};
          });
        const ranked=added.map(meta=>({...meta,...startupSignals(meta.text)}))
          .filter(meta=>meta.strong).sort((left,right)=>right.score-left.score||left.order-right.order);
        const exported=typeof value.exports==='function'?value.exports:null;
        const ordered=added.filter(meta=>meta.fn!==exported).sort((left,right)=>left.order-right.order);
        const directActions=value.exports&&['object','function'].includes(typeof value.exports)
          ?Object.entries(Object.getOwnPropertyDescriptors(value.exports))
            .filter(([key,descriptor])=>actionName(key)&&!blockedAction(key)&&
              !['start','stop'].includes(key)&&own.call(descriptor,'value')&&
              typeof descriptor.value==='function').map(([key])=>key):[];
        const explicit=[value.exports?.start,value.start,exported]
          .find(fn=>typeof fn==='function');
        let selected=explicit?(added.find(meta=>meta.fn===explicit)||
          {fn:explicit,text:functionText(explicit),order:-1}):ranked[0];
        // Evaluated startup evidence and direct exports do not need a bundle
        // scan. Only otherwise-unresolved closure wrappers use the lazy index.
        if(!selected&&!directActions.length){
          const lookup=createLocalStartupLookup(source,loadSdk('acorn'),
            text=>startupSignals(text).strong,startupWrapperTarget);
          selected=ordered.find(meta=>linkedStartup(meta.text,value.resolve,lookup));
        }
        let anchor=selected;
        const referenced=selected&&ranked.find(meta=>meta.fn!==selected.fn&&meta.fn.name&&
          selected.text.includes(meta.fn.name+'('));
        if(referenced)anchor=referenced;
        else if(!explicit&&selected?.fn.name){
          const wrappers=added.filter(meta=>meta.fn!==selected.fn&&
            meta.text.includes(selected.fn.name+'(')).sort((left,right)=>
              Math.abs(left.order-selected.order)-Math.abs(right.order-selected.order)||left.order-right.order);
          if(wrappers[0])selected=wrappers[0];
        }
        const start=selected?.fn;
        stop=[value.exports?.stop,value.stop].find(fn=>typeof fn==='function');
        if(!stop&&anchor){
          stop=added.filter(meta=>meta.fn!==start).map(meta=>{
            let score=stopSignals(meta.text);
            if(!score)return {...meta,score:0};
            if(anchor.order>=0){
              const distance=Math.abs(meta.order-anchor.order);
              score+=(meta.order>=anchor.order?4:0)+(distance<=1?16:distance<=4?10:distance<=16?4:0);
            }
            return {...meta,score};
          }).filter(meta=>meta.score>0).sort((left,right)=>right.score-left.score||left.order-right.order)[0]?.fn;
        }
        let result;
        const directActionSet=new Set(directActions);
        if(typeof start==='function')result=start({sites:{list:[]},pans:{list:[]},color:[],...(options.config||{})});
        else if(directActions.length){
          const spider=value.exports,server=catServerFactory(async(req,res)=>{
            try {
              const url=new URL(req.url,'http://127.0.0.1'),action=url.pathname.split('/').pop();
              if(url.pathname==='/config'||url.pathname==='/full-config'){
                res.setHeader('content-type','application/json');
                res.end(stringify({video:{sites:[{key:'default',name:options.name||'Cat',api:'/api/default',
                  actions:directActions} ]}}));return;
              }
              const chunks=[];for await(const chunk of req)chunks.push(Buffer.from(chunk));
              const body=chunks.length?parse(Buffer.concat(chunks).toString()):Object.fromEntries(url.searchParams);
              const positions={init:[body.cfg??body.ext??options.config,body.extend],home:[body.filter],
                homeVod:[],homeVideo:[],
                category:[body.id??body.tid,body.page??body.pg,body.filter,body.filters??body.extend],
                search:[body.wd,body.quick,body.page??body.pg],
                detail:[Object.hasOwn(body,'id')?body.id:body.ids],
                play:[body.flag,body.id,body.flags],
                proxy:[body.segments??body.path??body,body.headers??{}]};
              if(!directActionSet.has(action)){res.statusCode=404;res.end();return;}
              const answer=await spider[action](...(positions[action]||[body]));
              res.setHeader('content-type','application/json');res.end(typeof answer==='string'?answer:stringify(answer));
            }catch(error){
              try{base.diagnose(error,rpc({operation:'requestId'}).value);}catch{}
              res.statusCode=500;res.end(stringify({error:{code:error.code||'SCRIPT_EXCEPTION'}}));
            }
          });
          result=new Promise(resolve=>server.listen(Number(process.env.PORT),'127.0.0.1',resolve));
        }else throw nodeError('SCRIPT_PROTOCOL_ERROR');
        Promise.resolve(result).then(async value=>{
          addApplication(value);addApplication(module.exports);
          const descriptors=Object.getOwnPropertyDescriptors(globalThis);
          for(const [key,descriptor] of Object.entries(descriptors)){
            if(before.has(key)||!own.call(descriptor,'value'))continue;
            addApplication(descriptor.value);
          }
          await Promise.all(bootApplications.filter(app=>applicationRoutes.has(app)&&
            typeof app.ready==='function').map(app=>app.ready()));
          const configured=bootApplications.map(app=>applicationRoutes.get(app)).filter(entry=>
            entry&&(entry.routes.has('/full-config')||entry.routes.has('/config')));
          if(configured.length)await Promise.any(configured.map(entry=>entry.listening));
        }).then(()=>rawBridge('catReady','[]'),error=>{
          booting=false;
          base.diagnose(error,0);
          rawBridge('startupFailure',stringify([errors.get(error)||base.errorCode(error)]));
        });
      } catch(error) {
        booting=false;
        base.diagnose(error,0);
        throw error;
      }
    },
    catStop(){
      let result;
      try{result=typeof stop==='function'?stop():undefined;}
      catch{rawBridge('catStopped','[]');return stringify({$:'undefined'});}
      Promise.resolve(result).then(()=>rawBridge('catStopped','[]'),()=>rawBridge('catStopped','[]'));
      return stringify({$:'undefined'});
    },
  };
})()
`;
