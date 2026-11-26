#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const DEFAULT_IOS_BUNDLE_ID = 'dev.meowfilm.aggregate';
const DEFAULT_IOS_DEBUG_REL = 'Library/Application Support/meowfilm-debug';

class CliError extends Error {
  constructor(message, { exitCode = 1, details } = {}) {
    super(message);
    this.exitCode = exitCode;
    this.details = details;
  }
}

function parseScalar(raw) {
  if (raw === 'true') return true;
  if (raw === 'false') return false;
  if (raw === 'null') return null;
  if (/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$/.test(raw)) {
    const n = Number(raw);
    if (Number.isFinite(n)) return n;
  }
  if ((raw.startsWith('{') && raw.endsWith('}')) || (raw.startsWith('[') && raw.endsWith(']'))) {
    try { return JSON.parse(raw); } catch { /* Keep string literal. */ }
  }
  return raw;
}

const STRING_PARAM_KEYS = new Set([
  'id', 'itemId', 'sectionId', 'attemptId', 'bookId', 'sourceId', 'sessionId',
  'resourceId', 'target', 'route', 'provider', 'quality', 'aspect', 'scale',
  'domain', 'state', 'sub', 'event', 'cat', 'opId', 'traceId', 'window',
  'query', 'group', 'key', 'verb', 'kind', 'confirm', 'section', 'tab',
  'channelKey', 'remoteId', 'platform', 'submode', 'title', 'label',
]);

function parseKeyValue(token) {
  const idx = token.indexOf('=');
  if (idx <= 0) throw new CliError(`Invalid --param value (expected key=value): ${token}`);
  const key = token.slice(0, idx);
  const raw = token.slice(idx + 1);
  if (STRING_PARAM_KEYS.has(key)) return [key, raw];
  return [key, parseScalar(raw)];
}

function parseIndexOrId(raw) {
  if (/^(?:0|[1-9][0-9]*)$/.test(raw)) return { index: Number(raw) };
  return { id: raw };
}

function parseBoolFlag(value, name) {
  if (value === true || value === 'true' || value === '1') return true;
  if (value === false || value === 'false' || value === '0') return false;
  throw new CliError(`Invalid boolean for ${name}: ${value}`);
}

function parseIntFlag(value, name, { min = 0, max = Number.MAX_SAFE_INTEGER } = {}) {
  const n = Number(value);
  if (!Number.isSafeInteger(n) || n < min || n > max) {
    throw new CliError(`Invalid integer for ${name}: ${value}`);
  }
  return n;
}

function parseJsonObject(raw, label) {
  try {
    const value = JSON.parse(raw);
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
      throw new Error('Expected JSON object');
    }
    return value;
  } catch (error) {
    throw new CliError(`Invalid JSON object for ${label}: ${error.message}`);
  }
}

function parseArgs(argv) {
  const global = {
    endpoint: null,
    target: null,
    debugDir: null,
    udid: null,
    device: null,
    bundleId: DEFAULT_IOS_BUNDLE_ID,
    jsonOutput: false,
    help: false,
  };
  const positionals = [];
  const options = new Map();
  const params = [];
  let payloadJson = null;

  const pushOption = (key, value) => {
    if (!options.has(key)) options.set(key, []);
    options.get(key).push(value);
  };

  const valueFlags = new Set([
    'endpoint', 'target', 'debug-dir', 'udid', 'device', 'bundle-id',
    'section', 'param', 'timeout', 'max-items', 'max-depth', 'visible-only',
    'domain', 'tab', 'limit', 'since-seq', 'level', 'sub', 'event', 'op',
    'trace', 'q', 'interval', 'session', 'out', 'input', 'db', 'params', 'max-rows',
    'mode', 'duration', 'kind', 'save', 'modules', 'inline-limit', 'event-limit',
    'first-chance', 'json-params', 'window',
  ]);

  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (token === '--') {
      positionals.push(...argv.slice(i + 1));
      break;
    }
    if (token === '-h' || token === '--help') {
      global.help = true;
      continue;
    }
    if (token === '-n') {
      if (i + 1 >= argv.length) throw new CliError('Missing value for -n');
      pushOption('limit', argv[++i]);
      continue;
    }
    if (!token.startsWith('--')) {
      positionals.push(token);
      continue;
    }
    const eq = token.indexOf('=');
    const key = eq >= 0 ? token.slice(2, eq) : token.slice(2);
    let value = eq >= 0 ? token.slice(eq + 1) : undefined;

    if (key === 'json') {
      if (value !== undefined) {
        payloadJson = value;
        continue;
      }
      const next = argv[i + 1];
      const cmd = positionals[0];
      if (next && ['invoke', 'action', 'wait', 'novel', 'open', 'playback'].includes(cmd) &&
          next.trim().startsWith('{') && payloadJson === null) {
        payloadJson = argv[++i];
      } else {
        global.jsonOutput = true;
      }
      continue;
    }

    if (valueFlags.has(key)) {
      if (value === undefined) {
        if (i + 1 >= argv.length) throw new CliError(`Missing value for --${key}`);
        value = argv[++i];
      }
      if (key === 'endpoint') global.endpoint = value;
      else if (key === 'target') global.target = value;
      else if (key === 'debug-dir') global.debugDir = value;
      else if (key === 'udid') global.udid = value;
      else if (key === 'device') global.device = value;
      else if (key === 'bundle-id') global.bundleId = value;
      else if (key === 'param') params.push(value);
      else pushOption(key, value);
      continue;
    }

    // Boolean switches
    pushOption(key, value === undefined ? true : value);
  }

  const getOpt = key => {
    const list = options.get(key);
    return list && list.length ? list[list.length - 1] : undefined;
  };

  return { global, command: positionals[0] ?? null, args: positionals.slice(1), getOpt, options, params, payloadJson };
}

function readActiveSessionControlUrl(sessionFile) {
  let raw;
  try {
    raw = readFileSync(sessionFile, 'utf8');
  } catch (error) {
    throw new CliError(`Cannot read active-session.json at ${sessionFile}: ${error.message}`);
  }
  let session;
  try {
    session = JSON.parse(raw);
  } catch (error) {
    throw new CliError(`Invalid JSON in ${sessionFile}: ${error.message}`);
  }
  if (!session || typeof session.controlUrl !== 'string' || !session.controlUrl) {
    throw new CliError(`Missing controlUrl in ${sessionFile}`);
  }
  return session.controlUrl.replace(/\/+$/, '');
}

function discoverEndpoint(global) {
  if (global.endpoint) {
    return global.endpoint.replace(/\/+$/, '');
  }
  if (global.debugDir && !global.target) {
    return readActiveSessionControlUrl(join(resolve(global.debugDir), 'active-session.json'));
  }
  const target = global.target ?? (process.platform === 'win32' ? 'windows' : null);
  if (target === 'windows') {
    const baseDir = global.debugDir
      ? resolve(global.debugDir)
      : process.env.LOCALAPPDATA
        ? join(process.env.LOCALAPPDATA, 'meowfilm', 'aggregate-debug')
        : null;
    if (!baseDir) {
      throw new CliError('LOCALAPPDATA is not set; specify --debug-dir or --endpoint');
    }
    return readActiveSessionControlUrl(join(baseDir, 'active-session.json'));
  }
  if (target === 'ios-sim') {
    const udid = global.udid || 'booted';
    let container;
    try {
      container = execFileSync('xcrun', ['simctl', 'get_app_container', udid, global.bundleId, 'data'], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      }).trim();
    } catch (error) {
      throw new CliError(`Failed to locate iOS simulator container (${udid}, ${global.bundleId}): ${error.stderr?.toString?.() || error.message}`);
    }
    return readActiveSessionControlUrl(join(container, DEFAULT_IOS_DEBUG_REL, 'active-session.json'));
  }
  if (target === 'ios-device') {
    throw new CliError('Online commands for --target ios-device require --endpoint http://<device-ip>:<port>');
  }
  throw new CliError('No target discovered. Provide --endpoint, --debug-dir, or --target <windows|ios-sim|ios-device>.');
}

async function requestJson(endpoint, method, path, body) {
  const url = `${endpoint}${path}`;
  const init = {
    method,
    headers: body !== undefined ? { 'content-type': 'application/json' } : {},
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  };
  let response;
  try {
    response = await fetch(url, init);
  } catch (error) {
    throw new CliError(`HTTP request failed (${method} ${url}): ${error.message}`);
  }
  const text = await response.text();
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new CliError(`Non-JSON response (${response.status}) from ${method} ${url}: ${text.slice(0, 200)}`);
  }
  if (!response.ok || parsed?.ok === false) {
    const code = parsed?.error?.code ?? `HTTP_${response.status}`;
    const message = parsed?.error?.message ?? text.slice(0, 200);
    throw new CliError(`${code}: ${message}`, { details: parsed });
  }
  return parsed;
}

async function requestBinary(endpoint, path) {
  const url = `${endpoint}${path}`;
  let response;
  try {
    response = await fetch(url, { method: 'GET' });
  } catch (error) {
    throw new CliError(`HTTP request failed (GET ${url}): ${error.message}`);
  }
  if (!response.ok) {
    const text = await response.text().catch(() => '');
    let parsed = null;
    try { parsed = JSON.parse(text); } catch { /* ignore */ }
    const code = parsed?.error?.code ?? `HTTP_${response.status}`;
    const msg = parsed?.error?.message ?? text.slice(0, 200);
    throw new CliError(`${code}: ${msg}`, { details: parsed });
  }
  const buffer = Buffer.from(await response.arrayBuffer());
  return { buffer, headers: response.headers, status: response.status };
}

function printResult(envelope, { jsonOutput, format } = {}) {
  if (jsonOutput) {
    process.stdout.write(`${JSON.stringify(envelope, null, 2)}\n`);
    return;
  }
  if (typeof format === 'function') {
    const text = format(envelope?.data ?? envelope);
    if (typeof text === 'string') {
      process.stdout.write(text.endsWith('\n') ? text : `${text}\n`);
      return;
    }
  }
  process.stdout.write(`${JSON.stringify(envelope?.data ?? envelope, null, 2)}\n`);
}

function buildParamsMap(paramPairs, jsonRaw) {
  const out = jsonRaw ? parseJsonObject(jsonRaw, '--json') : {};
  for (const item of paramPairs) {
    const [k, v] = parseKeyValue(item);
    out[k] = v;
  }
  return out;
}

function formatLogLine(entry) {
  const ts = entry.ts ?? '';
  const lvl = (entry.level ?? 'info').toUpperCase().padEnd(5);
  const proc = entry.proc ?? '?';
  const cat = entry.cat ?? '';
  const sub = entry.sub ?? '';
  const ev = entry.event ?? '';
  const dur = entry.durationMs !== undefined ? ` (${entry.durationMs}ms)` : '';
  const op = entry.opId ? ` op=${entry.opId}` : '';
  const err = entry.error ? ` ERR=${entry.error.code ?? entry.error.type ?? ''}:${entry.error.message ?? ''}` : '';
  return `[${entry.seq ?? '-'}] ${ts} ${lvl} ${proc}/${cat}:${sub} ${ev}${dur}${op}${err}`;
}

function extractBlobRefsFromJsonl(text) {
  const refs = new Set();
  for (const line of text.split('\n')) {
    if (!line.trim()) continue;
    try {
      const record = JSON.parse(line);
      const stack = [record];
      while (stack.length) {
        const current = stack.pop();
        if (!current || typeof current !== 'object') continue;
        if (typeof current.$blob === 'string' && current.$blob.startsWith('blobs/') && !current.$blob.includes('..')) {
          refs.add(current.$blob);
        }
        if (typeof current.relativePath === 'string' &&
            (current.relativePath.startsWith('dumps/') || current.relativePath.startsWith('screenshots/')) &&
            !current.relativePath.includes('..')) {
          refs.add(current.relativePath);
        }
        for (const value of Object.values(current)) {
          if (value && typeof value === 'object') stack.push(value);
        }
      }
    } catch { /* Skip malformed line. */ }
  }
  return refs;
}

async function pullOfflineIosDevice({ global, sessionId, outDir }) {
  if (!global.device) {
    throw new CliError('Offline pull for --target ios-device requires --device <id>');
  }
  const targetDir = resolve(outDir);
  mkdirSync(targetDir, { recursive: true });
  const remoteSource = sessionId
    ? `${DEFAULT_IOS_DEBUG_REL}/sessions/${sessionId}`
    : DEFAULT_IOS_DEBUG_REL;
  try {
    execFileSync('xcrun', [
      'devicectl', 'device', 'copy', 'from',
      '--device', global.device,
      '--domain-type', 'appDataContainer',
      '--domain-identifier', global.bundleId,
      '--source', remoteSource,
      '--destination', targetDir,
    ], { stdio: 'inherit' });
  } catch (error) {
    throw new CliError(`devicectl copy failed: ${error.message}`);
  }
  return { mode: 'devicectl', device: global.device, source: remoteSource, destination: targetDir };
}

async function pullOnlineSession({ endpoint, sessionId, outDir }) {
  const targetDir = resolve(outDir);
  mkdirSync(targetDir, { recursive: true });
  const sessionsEnv = await requestJson(endpoint, 'GET', '/debug/v1/sessions');
  const sessions = Array.isArray(sessionsEnv.data) ? sessionsEnv.data : (sessionsEnv.data?.sessions ?? []);
  let selected = sessionId
    ? sessions.find(item => item.sessionId === sessionId || item.id === sessionId)
    : sessions.find(item => item.active) ?? sessions[0];
  const resolvedId = sessionId ?? selected?.sessionId ?? selected?.id ?? null;
  const sessionQuery = resolvedId ? `?sessionId=${encodeURIComponent(resolvedId)}` : '';

  const downloaded = [];
  const missing = [];
  const tryDownload = async (relPath, { optional = false } = {}) => {
    if (typeof relPath !== 'string' || !relPath || relPath.startsWith('/') || relPath.split('/').includes('..')) {
      missing.push(String(relPath));
      return null;
    }
    const encoded = relPath.split('/').map(encodeURIComponent).join('/');
    try {
      const { buffer } = await requestBinary(endpoint, `/debug/v1/files/${encoded}${sessionQuery}`);
      const dest = join(targetDir, ...relPath.split('/'));
      mkdirSync(dirname(dest), { recursive: true });
      writeFileSync(dest, buffer);
      downloaded.push({ path: relPath, bytes: buffer.byteLength });
      return buffer;
    } catch {
      if (!optional) missing.push(relPath);
      return null;
    }
  };

  await tryDownload('session.json');

  const blobRefs = new Set();
  const knownFiles = new Set();
  if (Array.isArray(selected?.files)) {
    for (const rel of selected.files) {
      if (typeof rel === 'string' && rel) knownFiles.add(rel);
    }
  }
  const catEnv = await requestJson(endpoint, 'GET', `/debug/v1/logs/categories${sessionQuery}`);
  const categories = Array.isArray(catEnv.data)
    ? catEnv.data
    : Array.isArray(catEnv.data?.categories) ? catEnv.data.categories : [];
  for (const item of categories) {
    if (typeof item === 'string') {
      if (item) knownFiles.add(`logs/${item}.jsonl`);
      continue;
    }
    if (typeof item?.file === 'string' && item.file) {
      knownFiles.add(item.file);
      continue;
    }
    const name = item?.cat ?? item?.category ?? item?.name;
    if (name) knownFiles.add(`logs/${name}.jsonl`);
  }
  // Child stdout/stderr logs exist only where the platform captures them (Windows).
  const platformLogs = ['logs/runtime-node.stdout.log', 'logs/runtime-node.stderr.log'];
  for (const rel of platformLogs) if (!knownFiles.has(rel)) await tryDownload(rel, { optional: true });

  for (const rel of knownFiles) {
    const buf = await tryDownload(rel);
    if (buf && rel.endsWith('.jsonl')) {
      for (const ref of extractBlobRefsFromJsonl(buf.toString('utf8'))) blobRefs.add(ref);
    }
  }
  // An externalized JSON event body may itself reference folded string/binary blobs.
  for (const rel of blobRefs) {
    if (knownFiles.has(rel)) continue;
    const buf = await tryDownload(rel);
    if (!buf) continue;
    if (rel.endsWith('.json')) {
      for (const ref of extractBlobRefsFromJsonl(buf.toString('utf8'))) blobRefs.add(ref);
    }
  }
  const result = { mode: 'http', endpoint, sessionId: resolvedId, destination: targetDir, files: downloaded };
  if (missing.length) {
    throw new CliError(`PULL_INCOMPLETE: ${missing.length} referenced file(s) could not be downloaded: ${missing.join(', ')}`, {
      details: { ok: false, error: { code: 'PULL_INCOMPLETE', message: 'Referenced files could not be downloaded', details: { ...result, missing } } },
    });
  }
  return result;
}

function printUsage() {
  const usage = `MeowFilm Unified Debug CLI (scripts/debug.mjs)

Usage:
  node scripts/debug.mjs [global-options] <command> [command-options]

Global options:
  --endpoint <url>              Explicit Control API URL (e.g. http://127.0.0.1:19777)
  --target <windows|ios-sim|ios-device>
  --debug-dir <path>            Explicit debug root containing active-session.json
  --udid <udid>                 iOS simulator UDID (default: booted)
  --device <id>                 iOS device identifier (for devicectl offline pull)
  --bundle-id <id>              iOS app bundle ID (default: ${DEFAULT_IOS_BUNDLE_ID})
  --json                        Output raw JSON response envelope
  -h, --help                    Show this help

Commands:
  status                                         Health + current UI state
  caps                                           Platform capabilities
  config [--modules <csv>] [--inline-limit <n>] [--event-limit <n>] [--first-chance <bool>]
  open <target> [--section <s>] [--param k=v] [--wait-idle] [--timeout <ms>]
  back | forward | refresh
  state | content [--max-items <n>] [--include-raw] | tree [--max-depth <n>] [--all] | images
  click-item <sectionIndexOrId> <itemIndexOrId> [verb] [--wait-idle] [--timeout <ms>]
  click-row <indexOrId> [verb] [--wait-idle] [--timeout <ms>]
  tab <group> <indexOrId> [--wait-idle] [--timeout <ms>]
  search <query> [--domain music|film|novel] [--tab <t>] [--wait-idle] [--timeout <ms>]
  action <actionName> [--param k=v] [--json '<obj>'] [--wait-idle] [--timeout <ms>]
  wait <condition> [--timeout <ms>] [--param k=v] [--json '<obj>']
  playback [state|play|pause|toggle|stop|next|prev|seek <sec>|rate <r>|volume <v>|mute [bool]|aspect <a>|pip <start|stop>|fullscreen [bool]|select_video_track <id>|select_audio_track <id>|select_subtitle_track <id|off>|<cmd>] [--domain music|film] [--param k=v]
  novel <command> [--param k=v] [--json '<obj>']
  logs [cat] [-n 50] [--since-seq <n>] [--level <l>] [--sub <s>] [--event <e>] [--op <id>] [--trace <id>] [--q <str>] [--follow]
       [--session <id>]
  blob <sha256> [--session <id>] [--out <file>]
  invoke <method> [--json '<obj>'|--input '<obj>'] [--timeout <ms>]
  sql "<SELECT ...>" [--db data|cache] [--params '<jsonArray>'] [--max-rows <n>]
  diag <summary|threads|gc|dump|heapsnapshot|cpu-profile|exceptions> [--mode mini|heap] [--duration <ms>] [--limit <n>] [--kind <k>]
  screenshot [--window <name>] [--out <file.png>] [--save <true|false>]
  pull [--session <id>] --out <dir>
`;
  process.stdout.write(usage);
}

export async function runCli(argv = process.argv.slice(2)) {
  const parsed = parseArgs(argv);
  const { global, command, args, getOpt, params, payloadJson } = parsed;

  if (global.help || !command || command === 'help') {
    printUsage();
    return 0;
  }

  if (command === 'pull' && global.target === 'ios-device' && !global.endpoint) {
    const outDir = getOpt('out');
    if (!outDir) throw new CliError('pull requires --out <dir>');
    const data = await pullOfflineIosDevice({ global, sessionId: getOpt('session'), outDir });
    printResult({ ok: true, data }, { jsonOutput: global.jsonOutput });
    return 0;
  }

  const endpoint = discoverEndpoint(global);
  const waitFlags = () => {
    const out = {};
    if (getOpt('wait-idle') !== undefined) out.waitIdle = parseBoolFlag(getOpt('wait-idle'), '--wait-idle');
    if (getOpt('timeout') !== undefined) out.timeoutMs = parseIntFlag(getOpt('timeout'), '--timeout', { min: 1 });
    return out;
  };

  switch (command) {
    case 'status': {
      const [health, state] = await Promise.all([
        requestJson(endpoint, 'GET', '/debug/v1/health'),
        requestJson(endpoint, 'GET', '/debug/v1/ui/state'),
      ]);
      const combined = { ok: true, ts: health.ts, data: { health: health.data, state: state.data } };
      printResult(combined, {
        jsonOutput: global.jsonOutput,
        format: d => [
          `Session:  ${d.health?.sessionId ?? '-'} (${d.health?.platform ?? '-'} pid=${d.health?.pid ?? '-'})`,
          `Endpoint: ${endpoint}`,
          `Runtime:  ${d.health?.runtimeConnected ? `connected (${d.health?.runtimeBaseUrl ?? ''})` : 'disconnected'}`,
          `Route:    ${d.state?.target ?? d.state?.route ?? '-'} — ${d.state?.title ?? ''}`,
          `Busy:     ${Boolean(d.state?.isBusy)}${d.state?.isError ? `  Error: ${d.state?.errorText ?? ''}` : ''}`,
        ].join('\n'),
      });
      return 0;
    }
    case 'caps': {
      const res = await requestJson(endpoint, 'GET', '/debug/v1/capabilities');
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'config': {
      const body = {};
      if (getOpt('modules') !== undefined) {
        body.modules = String(getOpt('modules')).split(',').map(s => s.trim()).filter(Boolean);
      }
      if (getOpt('inline-limit') !== undefined) {
        body.inlineLimit = parseIntFlag(getOpt('inline-limit'), '--inline-limit', { min: 0 });
      }
      if (getOpt('event-limit') !== undefined) {
        body.eventBytesLimit = parseIntFlag(getOpt('event-limit'), '--event-limit', { min: 0 });
      }
      if (getOpt('first-chance') !== undefined) {
        body.firstChanceExceptions = parseBoolFlag(getOpt('first-chance'), '--first-chance');
      }
      const res = await requestJson(endpoint, 'POST', '/debug/v1/config', body);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'open': {
      const target = args[0];
      if (!target) throw new CliError('open requires <target>');
      const extra = buildParamsMap(params, payloadJson ?? getOpt('json-params'));
      if (getOpt('section') !== undefined) extra.section = getOpt('section');
      const body = {
        target,
        ...(Object.keys(extra).length ? { params: extra } : {}),
        ...waitFlags(),
      };
      const res = await requestJson(endpoint, 'POST', '/debug/v1/nav/open', body);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'back':
    case 'forward':
    case 'refresh': {
      const res = await requestJson(endpoint, 'POST', `/debug/v1/nav/${command}`, {});
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'state': {
      const res = await requestJson(endpoint, 'GET', '/debug/v1/ui/state');
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'content': {
      const qs = new URLSearchParams();
      if (getOpt('max-items') !== undefined) qs.set('maxItems', String(parseIntFlag(getOpt('max-items'), '--max-items', { min: 1 })));
      if (getOpt('include-raw') !== undefined) qs.set('includeRaw', String(parseBoolFlag(getOpt('include-raw'), '--include-raw')));
      const suffix = qs.toString() ? `?${qs.toString()}` : '';
      const res = await requestJson(endpoint, 'GET', `/debug/v1/ui/content${suffix}`);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'tree': {
      const qs = new URLSearchParams();
      if (getOpt('max-depth') !== undefined) qs.set('maxDepth', String(parseIntFlag(getOpt('max-depth'), '--max-depth', { min: 1 })));
      if (getOpt('all') !== undefined) qs.set('visibleOnly', 'false');
      else if (getOpt('visible-only') !== undefined) qs.set('visibleOnly', String(parseBoolFlag(getOpt('visible-only'), '--visible-only')));
      const suffix = qs.toString() ? `?${qs.toString()}` : '';
      const res = await requestJson(endpoint, 'GET', `/debug/v1/ui/tree${suffix}`);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'images': {
      const res = await requestJson(endpoint, 'GET', '/debug/v1/ui/images');
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'click-item': {
      const [sectionArg, itemArg, verb] = args;
      if (sectionArg === undefined || itemArg === undefined) {
        throw new CliError('click-item requires <sectionIndexOrId> <itemIndexOrId> [verb]');
      }
      const sectionSel = parseIndexOrId(sectionArg);
      const itemSel = parseIndexOrId(itemArg);
      const actionParams = {
        ...(sectionSel.index !== undefined ? { sectionIndex: sectionSel.index } : { sectionId: sectionSel.id }),
        ...(itemSel.index !== undefined ? { itemIndex: itemSel.index } : { itemId: itemSel.id }),
        ...(verb ? { verb } : {}),
      };
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/action', {
        action: 'section.item.invoke',
        params: actionParams,
        ...waitFlags(),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'click-row': {
      const [rowArg, verb] = args;
      if (rowArg === undefined) throw new CliError('click-row requires <indexOrId> [verb]');
      const rowSel = parseIndexOrId(rowArg);
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/action', {
        action: 'list.item.invoke',
        params: { ...rowSel, ...(verb ? { verb } : {}) },
        ...waitFlags(),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'tab': {
      const [group, target] = args;
      if (!group || target === undefined) throw new CliError('tab requires <group> <indexOrId>');
      const sel = parseIndexOrId(target);
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/action', {
        action: 'tab.select',
        params: { group, ...sel },
        ...waitFlags(),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'search': {
      const query = args.join(' ');
      if (!query) throw new CliError('search requires <query>');
      const actionParams = { query };
      if (getOpt('domain') !== undefined) actionParams.target = getOpt('domain');
      if (getOpt('tab') !== undefined) actionParams.tab = getOpt('tab');
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/action', {
        action: 'search.submit',
        params: actionParams,
        ...waitFlags(),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'action': {
      const actionName = args[0];
      if (!actionName) throw new CliError('action requires <actionName>');
      const actionParams = buildParamsMap(params, payloadJson ?? getOpt('json-params'));
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/action', {
        action: actionName,
        params: actionParams,
        ...waitFlags(),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'wait': {
      const condition = args[0];
      if (!condition) throw new CliError('wait requires <condition>');
      const waitParams = buildParamsMap(params, payloadJson ?? getOpt('json-params'));
      const timeoutMs = getOpt('timeout') !== undefined
        ? parseIntFlag(getOpt('timeout'), '--timeout', { min: 1 })
        : 15000;
      const res = await requestJson(endpoint, 'POST', '/debug/v1/ui/wait', {
        condition,
        params: waitParams,
        timeoutMs,
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'playback': {
      const sub = args[0] ?? 'state';
      if (sub === 'state') {
        const res = await requestJson(endpoint, 'GET', '/debug/v1/playback/state');
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      let normalized = sub === 'prev' ? 'previous' : sub;
      const cmdParams = buildParamsMap(params, payloadJson ?? getOpt('json-params'));
      const posArg = args[1];
      if (normalized === 'pip' && posArg !== undefined) {
        const lowered = posArg.toLowerCase();
        if (['start', 'on', 'true', '1'].includes(lowered)) normalized = 'pip_start';
        else if (['stop', 'off', 'false', '0'].includes(lowered)) normalized = 'pip_stop';
        else throw new CliError(`Invalid pip action: ${posArg} (expected start|stop)`);
      } else if (normalized === 'seek' && posArg !== undefined && cmdParams.seconds === undefined) {
        const sec = Number(posArg);
        if (!Number.isFinite(sec) || sec < 0) throw new CliError(`Invalid seek seconds: ${posArg}`);
        cmdParams.seconds = sec;
      } else if (normalized === 'rate' && posArg !== undefined && cmdParams.rate === undefined) {
        const rate = Number(posArg);
        if (!Number.isFinite(rate) || rate <= 0) throw new CliError(`Invalid playback rate: ${posArg}`);
        cmdParams.rate = rate;
      } else if (normalized === 'volume' && posArg !== undefined && cmdParams.volume === undefined) {
        const vol = Number(posArg);
        if (!Number.isFinite(vol) || vol < 0 || vol > 1) throw new CliError(`Invalid volume: ${posArg}`);
        cmdParams.volume = vol;
      } else if (normalized === 'mute' && posArg !== undefined && cmdParams.muted === undefined) {
        cmdParams.muted = parseBoolFlag(posArg, 'mute');
      } else if (normalized === 'aspect' && posArg !== undefined && cmdParams.aspect === undefined) {
        cmdParams.aspect = posArg;
      } else if (normalized === 'fullscreen' && posArg !== undefined && cmdParams.enabled === undefined) {
        cmdParams.enabled = parseBoolFlag(posArg, 'fullscreen');
      } else if (normalized === 'pan' && posArg !== undefined && cmdParams.provider === undefined) {
        cmdParams.provider = posArg;
      } else if (normalized === 'quality' && posArg !== undefined && cmdParams.quality === undefined) {
        cmdParams.quality = posArg;
      } else if (normalized === 'retry' && posArg !== undefined && cmdParams.attemptId === undefined) {
        cmdParams.attemptId = posArg;
      } else if (['select_episode', 'select_source', 'select_line', 'select_candidate'].includes(normalized) && posArg !== undefined &&
                 cmdParams.index === undefined && cmdParams.id === undefined) {
        Object.assign(cmdParams, parseIndexOrId(posArg));
      } else if (normalized === 'select_season' && posArg !== undefined &&
                 cmdParams.season === undefined && cmdParams.index === undefined && cmdParams.id === undefined) {
        if (/^(?:[1-9][0-9]*)$/.test(posArg)) cmdParams.season = Number(posArg);
        else cmdParams.id = posArg;
      } else if (['select_video_track', 'select_audio_track', 'select_subtitle_track'].includes(normalized) &&
                 posArg !== undefined && cmdParams.id === undefined && cmdParams.index === undefined) {
        const lowered = posArg.toLowerCase();
        if (normalized === 'select_subtitle_track' && ['off', 'none', 'disable', '-1'].includes(lowered)) {
          cmdParams.disable = true;
          cmdParams.id = -1;
        } else if (/^-?(?:0|[1-9][0-9]*)$/.test(posArg)) {
          cmdParams.id = Number(posArg);
        } else {
          cmdParams.id = posArg;
        }
      }
      let domain = getOpt('domain');
      if (!domain) {
        const st = await requestJson(endpoint, 'GET', '/debug/v1/playback/state');
        domain = st?.data?.activeOwner;
        if (!domain) {
          if (st?.data?.film && st.data.film.state && st.data.film.state !== 'stopped') domain = 'film';
          else if (st?.data?.music && st.data.music.state && st.data.music.state !== 'stopped') domain = 'music';
        }
        if (!domain) throw new CliError('No active playback owner; specify --domain music|film');
      }
      const res = await requestJson(endpoint, 'POST', '/debug/v1/playback/command', {
        domain,
        command: normalized,
        ...(Object.keys(cmdParams).length ? { params: cmdParams } : {}),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'novel': {
      const cmd = args[0];
      if (!cmd) throw new CliError('novel requires <command>');
      const cmdParams = buildParamsMap(params, payloadJson ?? getOpt('json-params'));
      const res = await requestJson(endpoint, 'POST', '/debug/v1/novel/command', {
        command: cmd,
        ...(Object.keys(cmdParams).length ? { params: cmdParams } : {}),
      });
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'logs': {
      const cat = args[0];
      if (!cat) {
        const qs = getOpt('session') !== undefined ? `?sessionId=${encodeURIComponent(getOpt('session'))}` : '';
        const res = await requestJson(endpoint, 'GET', `/debug/v1/logs/categories${qs}`);
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      const follow = Boolean(getOpt('follow'));
      const intervalMs = getOpt('interval') !== undefined
        ? parseIntFlag(getOpt('interval'), '--interval', { min: 100, max: 60000 })
        : 500;
      let sinceSeq = getOpt('since-seq') !== undefined
        ? parseIntFlag(getOpt('since-seq'), '--since-seq', { min: 0 })
        : undefined;
      const buildQuery = () => {
        const qs = new URLSearchParams();
        qs.set('cat', cat);
        if (getOpt('limit') !== undefined) qs.set('limit', String(parseIntFlag(getOpt('limit'), '--limit', { min: 1, max: 2000 })));
        if (sinceSeq !== undefined) qs.set('sinceSeq', String(sinceSeq));
        if (getOpt('level') !== undefined) qs.set('level', String(getOpt('level')));
        if (getOpt('sub') !== undefined) qs.set('sub', String(getOpt('sub')));
        if (getOpt('event') !== undefined) qs.set('event', String(getOpt('event')));
        if (getOpt('op') !== undefined) qs.set('opId', String(getOpt('op')));
        if (getOpt('trace') !== undefined) qs.set('traceId', String(getOpt('trace')));
        if (getOpt('q') !== undefined) qs.set('q', String(getOpt('q')));
        if (getOpt('session') !== undefined) qs.set('sessionId', String(getOpt('session')));
        return `/debug/v1/logs/tail?${qs.toString()}`;
      };
      if (!follow) {
        const res = await requestJson(endpoint, 'GET', buildQuery());
        const entries = Array.isArray(res.data) ? res.data : (res.data?.events ?? res.data?.entries ?? res.data?.items ?? []);
        printResult(res, {
          jsonOutput: global.jsonOutput,
          format: () => entries.map(formatLogLine).join('\n'),
        });
        return 0;
      }
      let stopped = false;
      const onStop = () => { stopped = true; };
      process.on('SIGINT', onStop);
      process.on('SIGTERM', onStop);
      try {
        while (!stopped) {
          const res = await requestJson(endpoint, 'GET', buildQuery());
          const entries = Array.isArray(res.data) ? res.data : (res.data?.events ?? res.data?.entries ?? res.data?.items ?? []);
          for (const entry of entries) {
            if (typeof entry.seq === 'number' && (sinceSeq === undefined || entry.seq > sinceSeq)) {
              sinceSeq = entry.seq;
            }
            process.stdout.write(global.jsonOutput
              ? `${JSON.stringify(entry)}\n`
              : `${formatLogLine(entry)}\n`);
          }
          if (!stopped) await new Promise(r => setTimeout(r, intervalMs));
        }
      } finally {
        process.off('SIGINT', onStop);
        process.off('SIGTERM', onStop);
      }
      return 0;
    }
    case 'blob': {
      const sha256 = args[0];
      if (!sha256) throw new CliError('blob requires <sha256>');
      const qs = getOpt('session') ? `?sessionId=${encodeURIComponent(getOpt('session'))}` : '';
      const { buffer, headers } = await requestBinary(endpoint, `/debug/v1/blobs/${encodeURIComponent(sha256)}${qs}`);
      const outPath = getOpt('out');
      if (outPath) {
        const dest = resolve(outPath);
        mkdirSync(dirname(dest), { recursive: true });
        writeFileSync(dest, buffer);
        printResult({
          ok: true,
          data: { sha256, path: dest, bytes: buffer.byteLength, contentType: headers.get('content-type') },
        }, { jsonOutput: global.jsonOutput });
      } else if (global.jsonOutput) {
        const contentType = headers.get('content-type') ?? 'application/octet-stream';
        const isText = contentType.startsWith('text/') || contentType.includes('json');
        printResult({
          ok: true,
          data: {
            sha256,
            bytes: buffer.byteLength,
            contentType,
            ...(isText ? { text: buffer.toString('utf8') } : { base64: buffer.toString('base64') }),
          },
        }, { jsonOutput: true });
      } else {
        process.stdout.write(buffer);
      }
      return 0;
    }
    case 'invoke': {
      const method = args[0];
      if (!method) throw new CliError('invoke requires <method>');
      const rawInput = payloadJson ?? getOpt('input') ?? getOpt('json-params');
      const input = rawInput ? parseJsonObject(rawInput, '--json/--input') : {};
      const body = { method, input };
      if (getOpt('timeout') !== undefined) body.timeoutMs = parseIntFlag(getOpt('timeout'), '--timeout', { min: 1 });
      const res = await requestJson(endpoint, 'POST', '/debug/v1/runtime/invoke', body);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'sql': {
      const sql = args[0];
      if (!sql) throw new CliError('sql requires "<SELECT ...>"');
      const body = { sql };
      if (getOpt('db') !== undefined) {
        const dbName = String(getOpt('db')).trim();
        if (dbName !== 'data' && dbName !== 'cache') throw new CliError('--db must be "data" or "cache"');
        body.db = dbName;
      }
      if (getOpt('params') !== undefined) {
        try {
          const parsedParams = JSON.parse(getOpt('params'));
          if (!Array.isArray(parsedParams)) throw new Error('Expected JSON array');
          body.params = parsedParams;
        } catch (error) {
          throw new CliError(`Invalid --params JSON array: ${error.message}`);
        }
      }
      if (getOpt('max-rows') !== undefined) body.maxRows = parseIntFlag(getOpt('max-rows'), '--max-rows', { min: 1 });
      const res = await requestJson(endpoint, 'POST', '/debug/v1/storage/query', body);
      printResult(res, { jsonOutput: global.jsonOutput });
      return 0;
    }
    case 'diag': {
      const sub = args[0] ?? 'summary';
      if (sub === 'summary') {
        const res = await requestJson(endpoint, 'GET', '/debug/v1/diag/summary');
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'threads') {
        const res = await requestJson(endpoint, 'GET', '/debug/v1/diag/threads');
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'gc') {
        const res = await requestJson(endpoint, 'POST', '/debug/v1/diag/gc', {});
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'dump') {
        const body = {};
        if (getOpt('mode') !== undefined) body.mode = getOpt('mode');
        const res = await requestJson(endpoint, 'POST', '/debug/v1/diag/dump', body);
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'heapsnapshot') {
        const res = await requestJson(endpoint, 'POST', '/debug/v1/diag/node/heapsnapshot', {});
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'cpu-profile') {
        const durationMs = getOpt('duration') !== undefined
          ? parseIntFlag(getOpt('duration'), '--duration', { min: 500, max: 60000 })
          : 5000;
        const res = await requestJson(endpoint, 'POST', '/debug/v1/diag/node/cpu-profile', { durationMs });
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      if (sub === 'exceptions') {
        const qs = new URLSearchParams();
        if (getOpt('limit') !== undefined) qs.set('limit', String(parseIntFlag(getOpt('limit'), '--limit', { min: 1 })));
        if (getOpt('kind') !== undefined) qs.set('kind', String(getOpt('kind')));
        const suffix = qs.toString() ? `?${qs.toString()}` : '';
        const res = await requestJson(endpoint, 'GET', `/debug/v1/diag/exceptions${suffix}`);
        printResult(res, { jsonOutput: global.jsonOutput });
        return 0;
      }
      throw new CliError(`Unknown diag subcommand: ${sub}`);
    }
    case 'screenshot': {
      const save = getOpt('save') !== undefined ? parseBoolFlag(getOpt('save'), '--save') : true;
      const qs = new URLSearchParams({ save: String(save), format: 'png' });
      const win = getOpt('window');
      if (win !== undefined && String(win).trim() !== '') {
        qs.set('window', String(win).trim());
      }
      const { buffer, headers } = await requestBinary(endpoint, `/debug/v1/ui/screenshot?${qs.toString()}`);
      const remotePath = headers.get('x-meow-screenshot-path');
      const outPath = getOpt('out') ? resolve(getOpt('out')) : null;
      if (outPath) {
        mkdirSync(dirname(outPath), { recursive: true });
        writeFileSync(outPath, buffer);
      }
      printResult({
        ok: true,
        data: { bytes: buffer.byteLength, outPath, remotePath },
      }, {
        jsonOutput: global.jsonOutput,
        format: d => `Screenshot captured (${d.bytes} bytes)${d.outPath ? ` -> ${d.outPath}` : ''}${d.remotePath ? ` [remote: ${d.remotePath}]` : ''}`,
      });
      return 0;
    }
    case 'pull': {
      const outDir = getOpt('out');
      if (!outDir) throw new CliError('pull requires --out <dir>');
      const data = await pullOnlineSession({ endpoint, sessionId: getOpt('session'), outDir });
      printResult({ ok: true, data }, {
        jsonOutput: global.jsonOutput,
        format: d => `Pulled ${d.files.length} files from ${d.sessionId ?? 'active session'} -> ${d.destination}`,
      });
      return 0;
    }
    default:
      throw new CliError(`Unknown command: ${command}. Run with --help to see available commands.`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const cliArgv = process.argv.slice(2);
  const wantsJson = cliArgv.some((token, idx) => token === '--json' && !(idx + 1 < cliArgv.length && cliArgv[idx + 1].trim().startsWith('{')));
  runCli(cliArgv).then(
    code => { process.exitCode = code; },
    error => {
      if (error instanceof CliError) {
        if (wantsJson) {
          const payload = error.details && typeof error.details === 'object'
            ? error.details
            : { ok: false, error: { code: 'CLI_ERROR', message: error.message } };
          process.stdout.write(`${JSON.stringify(payload, null, 2)}\n`);
        } else {
          process.stderr.write(`Error: ${error.message}\n`);
        }
        process.exitCode = error.exitCode;
      } else {
        process.stderr.write(`Unexpected error: ${error?.stack ?? error}\n`);
        process.exitCode = 1;
      }
    },
  );
}
