import * as vm from 'node:vm';
import { posix as path } from 'node:path';
import { createFetch } from './node-http-realm.js';
import { discoverSourceRoutes, createLocalStartupLookup, startupWrapperTarget } from './cat-source-routes.js';
import { fault, requireValue } from './errors.js';

/** A naming context for CommonJS globals in this Worker's V8 isolate. This is
 * not a security sandbox; the loader projects the supported module/path API. */
export function createNodeRealm(session, node) {
  const modules = new Map(), core = { ...node.builtins };
  const own = Object.prototype.hasOwnProperty;
  const parse = JSON.parse, stringify = JSON.stringify;
  const nodeError = fault;
  const process = node.process;
  let booting = false;
  const sandbox = { Buffer, URL, URLSearchParams, TextEncoder, TextDecoder,
    Headers, ...node.web, FormData, AbortController, AbortSignal,
    DOMException, Event, CustomEvent, EventTarget, structuredClone,
    crypto: core.crypto.webcrypto, performance: core.perf_hooks.performance,
    atob, btoa, process, ...node.timers,
    queueMicrotask: callback => {
      const scope = session.scope();
      queueMicrotask(() => session.runCallback(callback, undefined, [], scope));
    },
  };
  // Values returned by native APIs share the native typed-array constructors.
  for (const name of ['ArrayBuffer', 'SharedArrayBuffer', 'DataView', 'Uint8Array',
    'Uint8ClampedArray', 'Uint16Array', 'Uint32Array', 'Int8Array', 'Int16Array', 'Int32Array',
    'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array']) sandbox[name] = globalThis[name];
  for (const name of ['ReadableStream', 'WritableStream', 'TransformStream',
    'CompressionStream', 'DecompressionStream', 'TextEncoderStream', 'TextDecoderStream',
    'ByteLengthQueuingStrategy', 'CountQueuingStrategy']) sandbox[name] = core['stream/web'][name];
  sandbox.console = Object.freeze(Object.fromEntries(['log', 'info', 'warn', 'error', 'debug',
    'trace', 'group', 'groupCollapsed', 'groupEnd', 'clear', 'table', 'dir', 'dirxml', 'time',
    'timeEnd', 'timeLog', 'assert', 'count', 'countReset', 'profile', 'profileEnd', 'timeStamp']
    .map(name => [name, name === 'error' ? (...args) => {
      if (!booting) return;
      const error = args.find(value => value && typeof value.message === 'string');
      const text = args.filter(value => typeof value === 'string').join(' ').slice(0, 2048);
      if (error || text) session.diagnose(error ?? new Error(text));
    } : () => {}])));
  core.console = sandbox.console;
  sandbox.global = sandbox;
  sandbox.fetch = createFetch(node);
  const context = vm.createContext(sandbox);
  const evaluate = (source, filename = '/source.js') => {
    captureBootSource(source);
    return new vm.Script(source, { filename }).runInContext(context);
  };
  class Module {
    constructor(filename = '/source.js') {
      this.filename = filename; this.id = filename; this.exports = {}; this.loaded = false;
    }
    _compile(source, filename = this.filename) {
      const compiled = evaluate('(function(exports,require,module,__filename,__dirname){\n' + source + '\n})', filename);
      compiled.call(this.exports, this.exports, name => requireModule(name, filename),
        this, filename, path.dirname(filename));
      this.loaded = true;
    }
    static createRequire(filename) {
      return name => requireModule(name, String(filename).replace(/^file:\/\//, ''));
    }
    static _load(name, parent) { return requireModule(name, parent?.filename); }
  }
  core.module = Module;
  core.vm = {
    createContext: value => vm.createContext(value ?? {}), isContext: vm.isContext,
    runInThisContext: source => evaluate(String(source)),
    runInContext: (source, target, options) => {
      captureBootSource(String(source));
      return vm.runInContext(String(source), vm.isContext(target) ? target : vm.createContext(target), options);
    },
    runInNewContext: (source, target = {}, options) => {
      captureBootSource(String(source)); return vm.runInNewContext(String(source), target, options);
    },
    Script: class {
      constructor(source, options) {
        captureBootSource(String(source)); this.script = new vm.Script(source, options);
      }
      runInThisContext(options) { return this.script.runInContext(context, options); }
      runInContext(target, options) { return this.script.runInContext(target, options); }
      runInNewContext(target, options) { return this.script.runInNewContext(target, options); }
    },
  };
  Module.builtinModules = Object.keys(core);
  function loadSdk(name, parent) {
    const data = node.source(name, parent ?? null);
    if (modules.has(data.id)) return modules.get(data.id).exports;
    const module = new Module(data.id); modules.set(data.id, module);
    try {
      if (data.json) module.exports = parse(data.source);
      else module._compile(data.source, data.id);
      return module.exports;
    } catch (error) { modules.delete(data.id); throw error; }
  }
  function requireModule(name, parent) {
    requireValue(typeof name === 'string', 'UNSUPPORTED_DEPENDENCY');
    name = name.replace(/^node:/, '');
    if (['process/', 'string_decoder/'].includes(name)) name = name.slice(0, -1);
    if (own.call(core, name)) return core[name];
    if (parent?.startsWith('/sdk/')) return loadSdk(name, parent);
    if (name.startsWith('.') || name.startsWith('/')) {
      const filename = path.resolve(parent ? path.dirname(parent) : '/', name);
      const found = [filename, filename + '.js', filename + '.json', path.join(filename, 'index.js')]
        .find(file => node.fs.existsSync(file) && node.fs.statSync(file).isFile());
      if (!found) throw nodeError('ENOENT');
      if (modules.has(found)) return modules.get(found).exports;
      const module = new Module(found); modules.set(found, module);
      try {
        const source = node.fs.readFileSync(found, 'utf8');
        if (found.endsWith('.json')) module.exports = parse(source);
        else module._compile(source, found);
        return module.exports;
      } catch (error) { modules.delete(found); throw error; }
    }
    return loadSdk(name);
  }
  process.getBuiltinModule = name => requireModule(name);
  sandbox.catServerFactory=(handler,options)=>requireModule('http').createServer(options||{},handler);
  sandbox.require=requireModule;
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
    for(const method of [].concat(methods??[])){
      const value=String(method).toUpperCase();
      if(httpMethods.includes(value))known.add(value);
    }
    // An empty set records a path without inventing a registered method.
    if(known.size||methods===undefined)routes.set(clean,known);
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
    if(!value||node.services.listeners.has(value)||!['object','function'].includes(typeof value))return false;
    try{return typeof value.hasRoute==='function';}catch{return false;}
  };
  const addApplication=(application,observe=false)=>{
    if(!routeApplication(application))return;
    if(!bootApplications.includes(application))bootApplications.push(application);
    if(!observe||applicationRoutes.has(application)||typeof application.addHook!=='function'||
      !node.services.listeners.has(application.server))return;
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
      try{session.diagnose(error);}catch{}
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
    describe(sites){
      booting=false;
      const result=sites.map(([key,site])=>{
        const target=new URL(site.api),base=target.pathname.replace(/\/$/,'');
        const port=Number(target.port||(target.protocol==='https:'?443:80));
        const application=bootApplications.find(app=>{
          try{return applicationRoutes.has(app)&&app.server.address()?.port===port;}catch{return false;}
        });
        if(!application)sourceRoutes();
        const routes=application?applicationRoutes.get(application).routes:bootRoutePaths;
        const methods={};
        for(const [path,available]of routes){
          const action=routeAction(path,base);
          if(!actionName(action)||blockedAction(action))continue;
          methods[action]=httpMethods.find(method=>available.has(method)||method===methods[action]);
        }
        if(!application)for(const pattern of bootPatternRoutes)
          for(const action of pattern.actions)
            if(actionName(action)&&!blockedAction(action)&&pattern.regex.test(base+'/'+action)&&
              !Object.hasOwn(methods,action))methods[action]=undefined;
        const declared=Array.isArray(site.actions)&&site.actions.every(actionName);
        const actions=declared?[...new Set(site.actions)]:Object.keys(methods);
        return [key,{actions,methods:Object.fromEntries(actions.map(action=>
          [action,methods[action]||'POST'])),discovery:declared?'declared':application?
          'registered-routes':actions.length?'source-routes':'opaque-http'}];
      });
      bootSources.clear();bootRoutePaths.clear();bootPatternRoutes.length=0;
      for(const app of bootApplications)applicationRoutes.get(app)?.routes.clear();
      return result;
    },
    async boot(source,options){
      try {
        booting=true;
        bootRoutePaths.clear();
        bootSources.clear();bootPatternRoutes.length=0;
        captureBootSource(source);
        Object.assign(process.env,options.env);
        const module=new Module('/source.js');
        modules.set('/source.js',module);
        Object.assign(sandbox,{module,exports:module.exports,__filename:'/source.js',__dirname:'/',
          catDartServerPort:()=>0,messageToDart:async()=>null});
        const before=new Set(Object.getOwnPropertyNames(sandbox));
        const value=evaluate(source+'\n;({exports:module.exports,start:typeof start==="function"?start:null,stop:typeof stop==="function"?stop:null,resolve(name){try{return eval(name)}catch{return null}}})');
        const addedDescriptors=Object.getOwnPropertyDescriptors(sandbox);
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
          const spider=value.exports,server=sandbox.catServerFactory(async(req,res)=>{
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
              try{session.diagnose(error);}catch{}
              res.statusCode=500;res.end(stringify({error:{code:error.code||'SCRIPT_EXCEPTION'}}));
            }
          });
          result=new Promise(resolve=>server.listen(Number(process.env.PORT),'127.0.0.1',resolve));
        }else throw nodeError('SCRIPT_PROTOCOL_ERROR');
        await Promise.resolve(result).then(async value=>{
          addApplication(value);addApplication(module.exports);
          const descriptors=Object.getOwnPropertyDescriptors(sandbox);
          for(const [key,descriptor] of Object.entries(descriptors)){
            if(before.has(key)||!own.call(descriptor,'value'))continue;
            addApplication(descriptor.value);
          }
          await Promise.all(bootApplications.filter(app=>applicationRoutes.has(app)&&
            typeof app.ready==='function').map(app=>app.ready()));
          const configured=bootApplications.map(app=>applicationRoutes.get(app)).filter(entry=>
            entry&&(entry.routes.has('/full-config')||entry.routes.has('/config')));
          if(configured.length)await Promise.any(configured.map(entry=>entry.listening));
        });
        session.ready.resolve();
      } catch(error) {
        booting=false;
        session.diagnose(error, session.root);
        throw error;
      }
    },
    async stop(){
      if (typeof stop === 'function') await stop();
    },
    close(){
      modules.clear(); bootSources.clear(); bootApplications.length = 0;
      stop = null;
      for (const key of Object.keys(sandbox)) delete sandbox[key];
    },
  };
}
