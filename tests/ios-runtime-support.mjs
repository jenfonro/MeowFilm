import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { attachConnectedStream } from '@meowfilm/runtime-bridge/descriptor';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

// Shared by the iOS runtime source and packaged-payload tests; not a test file.
export const root = fileURLToPath(new URL('../', import.meta.url));
export const contract = 'music.script.v2';
const hash = value => createHash('sha256').update(value).digest('hex');

// Desktop simulation of the iOS backend selection; not iOS device evidence.
export const simulatedIOS = `
  Object.defineProperty(process, 'platform', { value: 'ios', configurable: false, writable: false });
  Object.defineProperty(globalThis, 'WebAssembly', { value: undefined, configurable: false, writable: false });
`;

export function spawnRuntime(t, { preload = simulatedIOS, argv, entry, cwd = root } = {}) {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'ios-runtime-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, area), { recursive: true, force: true }));
  const session = randomUUID();
  const child = spawn(process.execPath, [
    '--import', `data:text/javascript,${encodeURIComponent(preload)}`, entry,
    ...(argv ?? ['--meow-fd', '3', '--meow-session', session, '--meow-data', join(area, 'scripts')]),
  ], {
    cwd, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
    stdio: ['ignore', 'pipe', 'pipe', 'pipe'],
  });
  let stdout = '', stderr = '';
  child.stdout.on('data', bytes => { stdout += bytes; });
  child.stderr.on('data', bytes => { stderr += bytes; });
  const exited = new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal })));
  t.after(() => { if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL'); });
  return { child, session, area, exited, output: () => ({ stdout, stderr }) };
}

export function connect(runtime, methods = {}) {
  let closed;
  const done = new Promise(resolve => { closed = resolve; });
  const peer = attachConnectedStream(runtime.child.stdio[3], {
    session: runtime.session, timeout: 20000, methods, onClose: () => closed(),
  });
  return { peer, closed: done };
}

export function nativeStorage() {
  const calls = [], values = new Map();
  const methods = {};
  for (const operation of ['get', 'set', 'delete']) {
    methods[`platform.musicScript.storage.${operation}`] = async message => {
      calls.push({ operation, message });
      const key = JSON.stringify([message.installationId, message.siteId, message.key]);
      if (operation === 'set') values.set(key, message.value);
      if (operation === 'delete') values.delete(key);
      return { ok: true, value: values.get(key) ?? null };
    };
  }
  return { calls, methods };
}

export const script = `/**
 * @name iOS runtime fixture
 * @version 2
 */
lx.on(lx.EVENT_NAMES.request, async ({ source, action, info }, context) => {
  const count = ((await context.storage.get('count')) || 0) + 1;
  await context.storage.set('count', count);
  if (action === 'lyric') return { lyric: '[00:00.00]' + source, tlyric: '', rlyric: '' };
  return 'http://media.invalid/' + source + '/' + info.type + '?n=' + count;
});
lx.send(lx.EVENT_NAMES.inited, { sources: {
  tx: { name: 'Fixture', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] },
} });
`;

export function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, contract);
  return reply;
}

export async function stage(peer) {
  const bytes = Buffer.from(script);
  const stage = okay(await peer.call('music.script.stage.begin',
    { basename: 'source.js', length: bytes.length, sha256: hash(bytes) }));
  okay(await peer.call('music.script.stage.chunk', { token: stage.token, offset: 0, data: bytes.toString('base64') }));
  return stage.token;
}

export async function load(peer, { installationId, revision = 1, expectedRevision = 0, grant = randomUUID() }) {
  return peer.call('music.script.load', {
    token: await stage(peer), installationId, revision, expectedRevision, version: '2',
    config: {}, permissions: { storage: ['read', 'write'] }, grant,
  });
}
