import { randomUUID, createHash } from 'node:crypto';
import {
  lstat, mkdir, readFile, readdir, realpath, rename, writeFile,
} from 'node:fs/promises';
import { registerHooks } from 'node:module';
import {
  basename, dirname, extname, isAbsolute, join, relative, resolve, sep,
} from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const defaultOutput = join(root, 'tmp/test-results/script-sources/acceptance.json');
const protocols = new Set(['auto', 'legado', 'yiciyuan', 'qysg', 'cat']);
const sourceOrder = [
  'search', 'exploreKinds', 'explore', 'bookInfo', 'toc',
  'content', 'loginStatus', 'login', 'image',
];
const catOrder = [
  'init', 'home', 'homeVideo', 'category', 'search',
  'detail', 'play', 'proxy', 'action', 'destroy',
];

function parseArguments(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === '--live') {
      if (result.live) throw new Error('CLI_USAGE');
      result.live = true;
      continue;
    }
    if (argument === '--probe-media') {
      if (result.probeMedia) throw new Error('CLI_USAGE');
      result.probeMedia = true;
      continue;
    }
    if (!['--kind', '--scripts', '--records', '--payload', '--output', '--protocol'].includes(argument) ||
        !argv[index + 1] || argv[index + 1].startsWith('--') ||
        Object.hasOwn(result, argument.slice(2))) {
      throw new Error('CLI_USAGE');
    }
    result[argument.slice(2)] = argv[++index];
  }
  if (!result.scripts || result.probeMedia && !result.live ||
      result.kind !== undefined && result.kind !== 'source') throw new Error('CLI_USAGE');
  result.protocol = String(result.protocol ?? 'auto').toLowerCase();
  if (!protocols.has(result.protocol)) throw new Error('CLI_USAGE');
  return result;
}

const options = parseArguments(process.argv.slice(2));
for (const name of ['scripts', 'records', 'payload', 'output']) {
  if (options[name] !== undefined && !isAbsolute(options[name])) throw new Error('CLI_USAGE');
}
const output = resolve(options.output ?? defaultOutput);
if (!isAbsolute(output)) throw new Error('CLI_USAGE');
const outputDelta = relative(join(root, 'tmp'), output);
if (!outputDelta || outputDelta === '..' || outputDelta.startsWith(`..${sep}`) ||
    isAbsolute(outputDelta)) {
  throw new Error('OUTPUT_ESCAPE');
}
const excludedSourcePaths = new Set([
  resolve(output),
  resolve(`${output}.tmp`),
]);

const report = {
  schema: 2,
  kind: 'source',
  mode: options.live ? 'live' : 'offline-init',
  runtime: options.payload ? 'payload' : 'workspace',
  protocolOverride: options.protocol,
  rows: [],
};
let writing = Promise.resolve();
function saveReport() {
  const bytes = `${JSON.stringify(report, null, 2)}\n`;
  writing = writing.then(async () => {
    await mkdir(dirname(output), { recursive: true });
    await writeFile(`${output}.tmp`, bytes);
    await rename(`${output}.tmp`, output);
  });
  return writing;
}

function inside(base, target) {
  const delta = relative(base, target);
  return delta !== '..' && !delta.startsWith(`..${sep}`) && !isAbsolute(delta);
}

async function sourceFiles(input) {
  const sourcePath = await realpath(input);
  const info = await lstat(sourcePath);
  if (info.isFile()) {
    return excludedSourcePaths.has(resolve(sourcePath))
      ? []
      : [{ path: sourcePath, name: basename(sourcePath) }];
  }
  if (!info.isDirectory()) throw new Error('INVALID_SOURCE_TYPE');
  const files = [];
  async function walk(directory, prefix = '') {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isSymbolicLink()) throw new Error('LINKED_SOURCE');
      if (entry.isDirectory()) await walk(path, name);
      else if (entry.isFile() && ['.js', '.json', '.html', '.htm'].includes(extname(entry.name).toLowerCase())) {
        const resolved = await realpath(path);
        if (!inside(sourcePath, resolved)) throw new Error('SOURCE_ESCAPE');
        if (excludedSourcePaths.has(resolve(resolved))) continue;
        files.push({ path: resolved, name });
      }
    }
  }
  await walk(sourcePath);
  return files.sort((left, right) => left.name.localeCompare(right.name));
}

function decode(bytes) {
  let value;
  try { value = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); }
  catch { throw new Error('SOURCE_ENCODING'); }
  if (!value.trim() || !Buffer.from(value).equals(bytes)) throw new Error('SOURCE_ENCODING');
  return value;
}

function remoteHtmlList(value) {
  if (typeof value !== 'string') return false;
  const entries = value.trim().split(/\s+/).filter(Boolean);
  return entries.length > 0 && entries.every(entry => {
    try {
      const url = new URL(entry);
      return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password;
    } catch {
      return false;
    }
  });
}

function qysgSource(source) {
  return typeof source.html === 'string' &&
    typeof source.bookSourceName === 'string' && source.bookSourceName.trim().length > 0 &&
    typeof source.bookSourceUrl === 'string' && source.bookSourceUrl.trim().length > 0 &&
    remoteHtmlList(source.html);
}

function detectedProtocol(value, extension) {
  if (options.protocol !== 'auto') return options.protocol;
  if (extension === '.html' || extension === '.htm') return 'qysg';
  if (extension === '.js') {
    if (/\b(?:__JS_SPIDER__|__jsEvalReturn|class\s+Spider|homeContent|categoryContent|playerContent)\b/.test(value) ||
        /\bexport\s+default\b/.test(value)) return 'cat';
    throw new Error('UNSUPPORTED_PROTOCOL');
  }
  let source;
  try { source = JSON.parse(value); } catch { throw new Error('UNSUPPORTED_PROTOCOL'); }
  if (typeof source === 'string') {
    if (/<script\b/i.test(source)) return 'qysg';
    throw new Error('UNSUPPORTED_PROTOCOL');
  }
  if (!source || typeof source !== 'object' || Array.isArray(source)) throw new Error('UNSUPPORTED_PROTOCOL');
  if (typeof source.html === 'string' && /<script\b/i.test(source.html)) return 'qysg';
  if (['ruleSearch', 'ruleBookInfo', 'ruleToc', 'ruleContent'].some(key => source[key] !== undefined)) {
    return 'legado';
  }
  if (['ruleSearchList', 'ruleBookName', 'ruleChapterList', 'ruleBookContent']
    .some(key => source[key] !== undefined)) {
    return 'yiciyuan';
  }
  if (qysgSource(source)) return 'qysg';
  if (typeof source.script === 'string' || typeof source.source === 'string') return 'cat';
  throw new Error('UNSUPPORTED_PROTOCOL');
}

async function inventory(input) {
  const result = [];
  for (const file of await sourceFiles(input)) {
    const original = await readFile(file.path);
    const originalHash = createHash('sha256').update(original).digest('hex');
    const extension = extname(file.name).toLowerCase();
    if (extension !== '.json') {
      const text = decode(original);
      result.push({
        file: file.name,
        label: file.name,
        basename: basename(file.name),
        bytes: original,
        originalHash,
        path: file.path,
        protocol: detectedProtocol(text, extension),
      });
      continue;
    }
    const text = decode(original);
    let value;
    try { value = JSON.parse(text); } catch { throw new Error('SOURCE_ENCODING'); }
    const entries = Array.isArray(value) ? value : [value];
    if (!entries.length || entries.length > 4096) throw new Error('SOURCE_LIMIT');
    for (const [index, entry] of entries.entries()) {
      if (entry === null || typeof entry !== 'object' && typeof entry !== 'string') {
        throw new Error('UNSUPPORTED_PROTOCOL');
      }
      const source = `${JSON.stringify(entry)}\n`;
      const bytes = Buffer.from(source);
      const itemName = entries.length === 1 ? file.name : `${file.name}#${index + 1}`;
      const sourceName = entries.length === 1
        ? basename(file.name)
        : basename(file.name).replace(/\.json$/i, `-${index + 1}.json`);
      result.push({
        file: file.name,
        label: itemName,
        basename: sourceName,
        bytes,
        originalHash,
        path: file.path,
        protocol: detectedProtocol(source, extension),
      });
    }
  }
  if (!result.length) throw new Error('NO_SOURCES');
  return result;
}

let payloadRoot;
let hook;
let sourceModulePath = join(root, 'apps/runtime/modules/source-runtime.mjs');
let hostModulePath = join(root, 'packages/script-host/src/index.js');
if (options.payload) {
  payloadRoot = await realpath(options.payload);
  let manifest;
  try { manifest = JSON.parse(await readFile(join(payloadRoot, 'runtime-manifest.json'), 'utf8')); }
  catch { throw new Error('INCOMPLETE_RUNTIME_PAYLOAD'); }
  const required = [
    'app/modules/source-runtime.mjs',
    'node_modules/@meowfilm/script-host/src/index.js',
    'node_modules/@meowfilm/source-runtime/src/index.js',
    'app/bootstrap.mjs', 'app/client.mjs', 'app/contracts.mjs',
    'app/modules/source-candidates.mjs', 'app/modules/source-library.mjs', 'app/modules/source-web.mjs',
    'app/modules/source-rules.mjs', 'node_modules/@meowfilm/novel-rules/novel-rules.js',
    'node_modules/@meowfilm/runtime-bridge/src/index.js',
  ];
  if (manifest?.status !== 'complete' || !Array.isArray(manifest.files) || required.some(path =>
    !manifest.files.some(file => file.path === path))) {
    throw new Error('INCOMPLETE_RUNTIME_PAYLOAD');
  }
  sourceModulePath = join(payloadRoot, required[0]);
  hostModulePath = join(payloadRoot, required[1]);
  hook = registerHooks({
    resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url.startsWith('file:')) {
        const path = fileURLToPath(result.url);
        if (!inside(payloadRoot, path) && resolve(path) !== resolve(payloadRoot)) {
          throw new Error('RUNTIME_DEPENDENCY_ESCAPED');
        }
      }
      return result;
    },
  });
}

const [{ createSourceRuntimeModule }, hostExports] = await Promise.all([
  import(pathToFileURL(sourceModulePath).href),
  import(pathToFileURL(hostModulePath).href),
]);
const { createNodeNetwork, createScriptHost, ScriptHostError } = hostExports;
const modulePath = sourceModulePath;

const runtimeDirectory = dirname(dirname(modulePath));
const [{ createAppRuntime }, { createRuntimeClient }] = await Promise.all([
  import(pathToFileURL(join(runtimeDirectory, 'bootstrap.mjs')).href),
  import(pathToFileURL(join(runtimeDirectory, 'client.mjs')).href),
]);
async function openHttp(module) {
  const sessionId = randomUUID();
  let runtime, client;
  try {
    runtime = createAppRuntime({ sessionId, modules: [module] });
    await runtime.start();
    client = createRuntimeClient({ baseUrl: runtime.baseUrl });
    return { client, async close() {
      const results = await Promise.allSettled([client.close(), runtime.close()]);
      if (results.some(result => result.status === 'rejected')) throw Object.assign(new Error(), { code: 'SCRIPT_CLEANUP' });
    } };
  } catch (error) {
    await Promise.allSettled([client?.close(), runtime?.close()]);
    throw error;
  }
}

const recordsPath = options.records ? await realpath(options.records) : null;
if (recordsPath) excludedSourcePaths.add(resolve(recordsPath));
let records = {};
if (recordsPath) {
  try { records = JSON.parse(await readFile(recordsPath, 'utf8')); }
  catch { throw new Error('INVALID_RECORDS'); }
}
if (!records || typeof records !== 'object' || Array.isArray(records)) throw new Error('INVALID_RECORDS');

function safeDiagnostic(event = {}) {
  const output = {};
  for (const key of ['kind', 'reason', 'reference', 'member', 'operation', 'code']) {
    if (typeof event[key] === 'string' && /^[A-Za-z0-9_.-]{1,128}$/.test(event[key])) {
      output[key] = event[key];
    }
  }
  for (const key of ['line', 'column']) {
    if (Number.isSafeInteger(event[key]) && event[key] >= 0 && event[key] <= 10_000_000) {
      output[key] = event[key];
    }
  }
  return output;
}

function describe(value, action) {
  if (value === null || value === undefined || value === '') return { kind: 'empty' };
  if (action === 'play') {
    const url = typeof value === 'string' ? value : value?.url;
    if (typeof url === 'string' && /^(?:https?|meow-source):\/\//.test(url)) return { kind: 'url' };
  }
  if (Array.isArray(value)) return { kind: 'array', count: value.length };
  if (typeof value === 'object') {
    const list = value.books ?? value.chapters ?? value.kinds ?? value.list ??
      value.data?.list ?? (Array.isArray(value.data) ? value.data : undefined);
    return { kind: 'object', ...(Array.isArray(list) ? { count: list.length } : {}) };
  }
  return { kind: typeof value };
}

function networkFailure(events) {
  if (events.some(event => event.responseReason === 'configuration')) {
    return 'account_region_configuration_required';
  }
  if (events.some(event => event.code || event.statusCode >= 400 ||
      typeof event.businessCode === 'number' && ![0, 1, 200].includes(event.businessCode) ||
      event.responseReason === 'upstream')) {
    return 'upstream_failure';
  }
  return null;
}

function classify(code, network, diagnostics) {
  if (['LIMIT', 'TIMEOUT', 'RESOURCE_LIMIT'].includes(code) ||
      diagnostics.some(event => ['LIMIT', 'TIMEOUT', 'RESOURCE_LIMIT'].includes(event.code))) {
    return 'runtime_limit';
  }
  if (['SOURCE_CLEANUP', 'SCRIPT_CLEANUP'].includes(code)) return 'cleanup_failure';
  if (['CAPABILITY_UNAVAILABLE', 'PERMISSION_DENIED'].includes(code) ||
      diagnostics.some(event => event.reason === 'host' &&
        ['CAPABILITY_UNAVAILABLE', 'PERMISSION_DENIED', 'UNSUPPORTED_OPTION', 'UNSUPPORTED_CRYPTO']
          .includes(event.code))) {
    return 'host_capability_missing';
  }
  if (code === 'WEB_INTERACTION_REQUIRED' ||
      diagnostics.some(event => event.reason === 'configuration')) {
    return 'account_region_configuration_required';
  }
  if (['NETWORK_ERROR', 'HTTP_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'INVALID_RESPONSE']
    .includes(code)) {
    return networkFailure(network) ?? 'upstream_failure';
  }
  return 'engine_incompatible';
}

async function probeMedia(value) {
  const candidate = typeof value === 'string' ? { url: value } : value;
  let url;
  try { url = new URL(candidate?.url); } catch { return { state: 'invalid_url' }; }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
    return { state: 'invalid_url' };
  }
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  let reader;
  try {
    const headers = {};
    for (const [key, input] of Object.entries(candidate?.headers ?? {})) {
      if (typeof input === 'string' && !/^(?:host|content-length|transfer-encoding)$/i.test(key)) {
        headers[key] = input;
      }
    }
    const response = await fetch(url, {
      headers: { ...headers, Range: 'bytes=0-1023' },
      signal: controller.signal,
    });
    if (!response.ok) return { state: 'upstream_failure', statusCode: response.status };
    reader = response.body?.getReader();
    const chunks = [];
    let size = 0;
    while (reader && size < 1024) {
      const part = await reader.read();
      if (part.done) break;
      const bytes = part.value.subarray(0, 1024 - size);
      chunks.push(Buffer.from(bytes));
      size += bytes.length;
    }
    const bytes = Buffer.concat(chunks);
    const magic = bytes.subarray(0, 16).toString('latin1');
    const media = /^ID3|^fLaC|^OggS|^RIFF|^#EXTM3U/.test(magic) ||
      bytes.length > 1 && bytes[0] === 255 && (bytes[1] & 224) === 224 ||
      bytes.subarray(4, 8).toString() === 'ftyp';
    return { state: media ? 'media_detected' : 'unverified_content',
      statusCode: response.status, bytes: size };
  } catch {
    return { state: 'upstream_failure' };
  } finally {
    try { await reader?.cancel(); } catch { /* Abort below releases the transport. */ }
    controller.abort();
    clearTimeout(timer);
  }
}

function parseReturned(value) {
  if (typeof value !== 'string') return value;
  const source = value.trim();
  if (!source || !['{', '['].includes(source[0])) return value;
  try { return JSON.parse(source); } catch { return value; }
}

function firstList(value, keys) {
  value = parseReturned(value);
  if (Array.isArray(value)) return value;
  if (!value || typeof value !== 'object') return [];
  for (const key of keys) {
    const child = value[key];
    if (Array.isArray(child)) return child;
  }
  return [];
}

function updateSourceState(state, action, raw) {
  const value = parseReturned(raw);
  if (action === 'search' || action === 'explore') {
    const item = firstList(value, ['books', 'list', 'data'])[0];
    if (item && typeof item === 'object') {
      if (state.protocol === 'cat') state.video = item;
      else state.book = item;
    }
  } else if (action === 'exploreKinds') {
    const item = firstList(value, ['kinds', 'list', 'class'])[0];
    if (item && typeof item === 'object') state.kind = item;
  } else if (action === 'home' || action === 'homeVideo' || action === 'category') {
    const classes = firstList(value, ['class', 'classes']);
    const videos = firstList(value, ['list', 'data']);
    if (classes[0] && typeof classes[0] === 'object') state.category = classes[0];
    if (videos[0] && typeof videos[0] === 'object') state.video = videos[0];
  } else if (action === 'bookInfo') {
    if (value?.book && typeof value.book === 'object') state.book = value.book;
  } else if (action === 'toc') {
    const item = firstList(value, ['chapters', 'list'])[0];
    if (item && typeof item === 'object') state.chapter = item;
  } else if (action === 'detail') {
    const item = firstList(value, ['list', 'data'])[0];
    if (item && typeof item === 'object') {
      state.detail = item;
      const groups = String(item.vod_play_url ?? '').split('$$$');
      const flags = String(item.vod_play_from ?? '').split('$$$');
      const episode = groups[0]?.split('#')[0] ?? '';
      const separator = episode.indexOf('$');
      if (separator >= 0 && episode.slice(separator + 1)) {
        state.play = { flag: flags[0] ?? '', id: episode.slice(separator + 1) };
      }
    }
  } else if (action === 'play' && value && typeof value === 'object') {
    const url = String(value.url ?? value.data?.url ?? '');
    if (url.startsWith('meow-source://')) state.virtual = url;
  }
}

function sourceActionRecord(item, action) {
  const bucket = records.source?.[item.label] ?? records.source?.[item.file];
  const value = bucket?.actions?.[action];
  return value && typeof value === 'object' && !Array.isArray(value)
    ? structuredClone(value) : null;
}

function sourcePayload(item, action, state) {
  const explicit = sourceActionRecord(item, action);
  if (explicit) return explicit;
  if (item.protocol === 'cat') {
    switch (action) {
      case 'init':
      case 'home':
      case 'homeVideo':
      case 'destroy':
        return {};
      case 'search':
        return { keyword: '测试', page: 1, quick: false };
      case 'category': {
        const id = state.category?.type_id ?? state.category?.id;
        return id === undefined || id === null || id === ''
          ? null : { id: String(id), page: 1, filter: false, extend: {} };
      }
      case 'detail': {
        const id = state.video?.vod_id ?? state.video?.id ?? state.video?.url;
        return id === undefined || id === null || id === '' ? null : { id: String(id) };
      }
      case 'play':
        return state.play ? { ...state.play } : null;
      case 'proxy':
        return state.virtual ? { url: state.virtual } : null;
      case 'action':
        return null;
      default:
        return null;
    }
  }
  switch (action) {
    case 'search':
      return { keyword: '测试', page: 1 };
    case 'exploreKinds':
    case 'loginStatus':
    case 'login':
      return {};
    case 'explore': {
      const url = state.kind?.url;
      return typeof url === 'string' && url ? { url, page: 1 } : null;
    }
    case 'bookInfo': {
      const bookUrl = state.book?.bookUrl ?? state.book?.url;
      return typeof bookUrl === 'string' && bookUrl ? { bookUrl, book: state.book } : null;
    }
    case 'toc': {
      const tocUrl = state.book?.tocUrl ?? state.book?.bookUrl ?? state.book?.url;
      return typeof tocUrl === 'string' && tocUrl
        ? { tocUrl, bookUrl: state.book?.bookUrl ?? '', book: state.book } : null;
    }
    case 'content': {
      const chapterUrl = state.chapter?.chapterUrl ?? state.chapter?.url;
      return typeof chapterUrl === 'string' && chapterUrl
        ? { chapterUrl, chapter: state.chapter, book: state.book,
          bookUrl: state.book?.bookUrl ?? '' } : null;
    }
    case 'image':
      return null;
    default:
      return null;
  }
}

function orderedActions(item, actions) {
  const order = item.protocol === 'cat' ? catOrder : sourceOrder;
  return [...actions].sort((left, right) => {
    const a = order.indexOf(left), b = order.indexOf(right);
    return (a < 0 ? order.length : a) - (b < 0 ? order.length : b) ||
      left.localeCompare(right);
  });
}

function networkAdapter(audit) {
  const network = createNodeNetwork();
  return async input => {
    const event = {
      scope: input.scope?.requestId === null ? 'session' :
        input.scope?.requestId === undefined ? 'capability' : 'request',
      mode: options.live ? 'live' : 'blocked',
      finished: false,
    };
    audit.push(event);
    try {
      if (!options.live) throw new ScriptHostError('NETWORK_ERROR');
      const response = await network(input);
      event.statusCode = response.statusCode;
      try {
        const body = JSON.parse(Buffer.from(response.body ?? []).toString());
        event.bodyKind = 'json';
        const code = body?.code ?? body?.status ?? body?.err_code ?? body?.error_code;
        if (typeof code === 'number' && Number.isSafeInteger(code) && Math.abs(code) <= 999999) {
          event.businessCode = code;
        } else if (typeof code === 'string' && /^-?\d{1,6}$/.test(code)) {
          event.businessCode = Number(code);
        }
        const message = String(body?.message ?? body?.msg ?? body?.error ?? '');
        if (/密钥.*(?:无效|缺失|错误|过期)|(?:invalid|missing|expired).*api.?key|api.?key.*(?:required|missing|invalid)|token.*(?:expired|invalid)|(?:login|account|region).*(?:required|restricted)/i
          .test(message)) {
          event.responseReason = 'configuration';
        }
        const providerError = body?.err_code ?? body?.error_code ?? body?.errcode;
        const emptyData = body && typeof body === 'object' &&
          (body.data === null || Array.isArray(body.resource) && body.resource.length === 0 ||
            body.data && typeof body.data === 'object' &&
              Object.hasOwn(body.data, 'url') && !body.data.url);
        if (body?.success === false || body?.status === false ||
            body?.status === 0 && providerError !== undefined ||
            typeof providerError === 'number' && providerError !== 0 || emptyData) {
          event.responseReason ??= 'upstream';
        }
      } catch {
        event.bodyKind = 'non_json';
      }
      return response;
    } catch (error) {
      event.code = typeof error?.code === 'string' ? error.code : 'NETWORK_ERROR';
      throw error;
    } finally {
      event.finished = true;
    }
  };
}

function sanitizedNetwork(events) {
  return events.map(({ scope, statusCode, code, bodyKind, businessCode, responseReason }) => ({
    scope, ...(statusCode === undefined ? {} : { statusCode }),
    ...(code === undefined ? {} : { code }),
    ...(bodyKind === undefined ? {} : { bodyKind }),
    ...(businessCode === undefined ? {} : { businessCode }),
    ...(responseReason === undefined ? {} : { responseReason }),
  }));
}

async function accept(item, index) {
  const audit = [];
  const diagnostics = [];
  const row = {
    file: item.label,
    protocol: item.protocol,
    contentHash: createHash('sha256').update(item.bytes).digest('hex'),
    initialization: 'failed',
    actions: [],
  };
  const startupPending = new Set();
  let startupAuditEnd = 0;
  let startupDiagnosticsEnd = 0;
  let failureAuditStart = 0;
  let failureDiagnosticsStart = 0;
  let instance;
  let module;
  let transport;
  let rpc;
  const id = `accept-${index}-${row.contentHash.slice(0, 8)}`;
  try {
    const network = networkAdapter(audit);
    const hostFactory = input => createScriptHost({
      ...input,
      onDiagnostic: event => diagnostics.push(safeDiagnostic(event)),
    });
    module = createSourceRuntimeModule({
      hostFactory,
      network,
      platform: 'acceptance',
    });
    transport = await openHttp(module);
    rpc = (name, params) => transport.client.call(`source.runtime.${name}`, params);
    const uploaded = await transport.client.upload('source', id, 1, item.bytes, { basename: item.basename });
    if (!uploaded.ok) throw Object.assign(new Error(), { code: uploaded.code });
    const began = Date.now();
    const load = {
      sourceRevision: 1,
      installationId: id,
      revision: 1,
      expectedRevision: 0,
      protocol: item.protocol,
      version: '1.0.0',
      metadata: {},
    };
    const loaded = await rpc('load', load);
    row.initializationMs = Date.now() - began;
    if (!loaded.ok) throw Object.assign(new Error(), { code: loaded.code });
    instance = loaded.value.instance;
    if (instance?.state !== 'ready') {
      throw Object.assign(new Error(), { code: instance?.error ?? 'SOURCE_FAILURE' });
    }
    row.initialization = 'ready';
    row.importedProtocol = instance.importedProtocol ?? instance.protocol;
    row.capabilities = Object.fromEntries(Object.entries(instance.capabilities).map(([site, value]) => [
      site,
      {
        protocol: value.protocol,
        importedProtocol: value.importedProtocol,
        actions: [...value.actions],
      },
    ]));
    startupAuditEnd = audit.length;
    startupDiagnosticsEnd = diagnostics.length;
    for (const event of audit) if (!event.finished) startupPending.add(event);
    row.initializationNetwork = audit.map(event => ({
      state: event.finished ? 'completed' : 'pending',
      ...(event.statusCode === undefined ? {} : { statusCode: event.statusCode }),
      ...(event.code === undefined ? {} : { code: event.code }),
    }));
    row.initializationDiagnostics = diagnostics.map(safeDiagnostic);

    if (options.live) {
      const capability = instance.capabilities.source;
      const state = { protocol: item.protocol };
      if (!capability.actions.some(operation => !['init', 'destroy'].includes(operation))) {
        row.code = 'NO_BUSINESS_ACTIONS';
        row.failure = 'engine_incompatible';
      }
      for (const operation of orderedActions(item, capability.actions)) {
        failureAuditStart = audit.length;
        failureDiagnosticsStart = diagnostics.length;
        const action = { operation };
        const payload = sourcePayload(item, operation, state);
        if (payload === null) {
          action.state = 'not_exercised';
          row.actions.push(action);
          continue;
        }
        const before = audit.length;
        const beforeDiagnostic = diagnostics.length;
        const started = Date.now();
        const reply = await rpc('invoke', {
          installationId: id,
          revision: 1,
          generation: instance.generation,
          action: operation,
          payload,
        });
        action.elapsedMs = Date.now() - started;
        action.network = sanitizedNetwork(audit.slice(before));
        action.diagnostics = diagnostics.slice(beforeDiagnostic).map(safeDiagnostic);
        if (reply.ok) {
          action.state = 'returned';
          action.result = describe(reply.value, operation);
          updateSourceState(state, operation, reply.value);
          const mediaUrl = typeof reply.value === 'string' ? reply.value : reply.value?.url;
          if (options.probeMedia && operation === 'play' && /^https?:\/\//.test(mediaUrl)) {
            action.media = await probeMedia(reply.value);
          }
        } else {
          action.code = reply.code;
          action.state = classify(reply.code, action.network, action.diagnostics);
        }
        row.actions.push(action);
      }
    }

    row.unload = 'cleanup_failure';
    failureAuditStart = audit.length;
    failureDiagnosticsStart = diagnostics.length;
    const unloaded = await rpc('unload', {
      installationId: id,
      revision: 1,
      generation: instance.generation,
    });
    row.unload = unloaded.ok ? 'complete' : 'cleanup_failure';
    if (!unloaded.ok) row.unloadCode = unloaded.code;
  } catch (error) {
    const code = typeof error?.code === 'string' ? error.code : 'SOURCE_FAILURE';
    row.code = code;
    if (row.unload === 'cleanup_failure') row.unloadCode = code;
    row.failure = classify(code, sanitizedNetwork(audit.slice(failureAuditStart)),
      diagnostics.slice(failureDiagnosticsStart));
  } finally {
    if (module) {
      try {
        const closed = await module.close();
        row.cleanup = closed.ok ? 'complete' : 'cleanup_failure';
        if (!closed.ok) row.cleanupCode = closed.code;
      } catch {
        row.cleanup = 'cleanup_failure';
      }
    } else {
      row.cleanup = 'cleanup_failure';
    }
    try { await transport?.close(); } catch { row.cleanup = 'cleanup_failure'; }
    if (row.initialization === 'ready') {
      row.backgroundNetwork = audit.filter((event, at) =>
        (at >= startupAuditEnd && event.scope === 'session') || startupPending.has(event))
        .map(event => ({
          state: event.finished ? 'completed' : 'pending',
          ...(event.statusCode === undefined ? {} : { statusCode: event.statusCode }),
          ...(event.code === undefined ? {} : { code: event.code }),
        }));
      row.backgroundDiagnostics = diagnostics.slice(startupDiagnosticsEnd).map(safeDiagnostic);
    }
    row.initializationNetwork ??= audit.map(event => ({
      state: event.finished ? 'completed' : 'pending',
      ...(event.statusCode === undefined ? {} : { statusCode: event.statusCode }),
      ...(event.code === undefined ? {} : { code: event.code }),
    }));
    row.initializationDiagnostics ??= diagnostics.map(safeDiagnostic);
    try {
      row.sourceUnchanged = createHash('sha256').update(await readFile(item.path)).digest('hex') ===
        item.originalHash;
    } catch {
      row.sourceUnchanged = false;
    }
    report.rows.push(row);
    await saveReport();
    console.log(JSON.stringify({
      file: row.file,
      protocol: row.protocol,
      initialization: row.initialization,
      returned: row.actions.filter(action => action.state === 'returned').length,
      failed: row.actions.filter(action => action.state !== 'returned').length,
      cleanup: row.cleanup,
    }));
  }
}

const pending = new Set();
try {
  const items = await inventory(options.scripts);
  const concurrency = 2;
  for (const [index, item] of items.entries()) {
    const task = accept(item, index);
    pending.add(task);
    task.finally(() => pending.delete(task)).catch(() => {});
    if (pending.size >= concurrency) await Promise.race(pending);
  }
  await Promise.all(pending);
  report.rows.sort((left, right) => left.file.localeCompare(right.file));
  const actions = report.rows.flatMap(row => row.actions);
  const states = [
    'returned',
    'upstream_failure',
    'account_region_configuration_required',
    'host_capability_missing',
    'engine_incompatible',
    'runtime_limit',
    'cleanup_failure',
    'not_exercised',
  ];
  report.summary = {
    sources: report.rows.length,
    initialized: report.rows.filter(row => row.initialization === 'ready').length,
    declaredActions: report.rows.reduce((total, row) => total +
      Object.values(row.capabilities ?? {}).reduce((sum, value) => sum + value.actions.length, 0), 0),
    invokedActions: actions.filter(action => action.elapsedMs !== undefined).length,
    ...Object.fromEntries(states.map(state => [
      state,
      actions.filter(action => action.state === state).length +
        report.rows.filter(row => row.failure === state).length,
    ])),
    unloadFailures: report.rows.filter(row => row.unload === 'cleanup_failure').length,
    cleanupFailures: report.rows.filter(row => row.cleanup !== 'complete').length,
    changedSources: report.rows.filter(row => !row.sourceUnchanged).length,
    mediaDetected: actions.filter(action => action.media?.state === 'media_detected').length,
  };
  await saveReport();
  console.log(JSON.stringify(report.summary));
  const fatalStates = ['host_capability_missing', 'engine_incompatible', 'runtime_limit', 'cleanup_failure'];
  if (report.summary.initialized !== report.summary.sources ||
      report.summary.unloadFailures || report.summary.cleanupFailures ||
      report.summary.changedSources || fatalStates.some(state => report.summary[state]) ||
      options.live && report.summary.not_exercised) {
    process.exitCode = 1;
  }
} finally {
  await Promise.allSettled([...pending]);
  await Promise.allSettled([writing]);
  hook?.deregister();
}
