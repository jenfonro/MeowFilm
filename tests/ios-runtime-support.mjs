import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodeJSONValue, validateEndpoint } from '@meowfilm/runtime-bridge';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

export const root = fileURLToPath(new URL('../', import.meta.url));
export const contract = 'music.script.v3';

// Desktop execution of the iOS bootstrap with the desktop native engine.
// Actual iOS framework loading belongs to the App host tests.
export const simulatedIOS = `
  Object.defineProperty(globalThis, 'WebAssembly', { value: undefined, configurable: false, writable: false });
`;

export function spawnRuntime(t, { preload = simulatedIOS, argv, entry, fixture, cwd = root,
  configuration: changes = {}, sendConfiguration = true } = {}) {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'ios-runtime-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, area), { recursive: true, force: true }));
  const configuration = { sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex'),
    dataRoot: join(area, 'scripts'), ...changes };
  if (fixture !== undefined) {
    entry = join(area, 'entry.mjs');
    writeFileSync(entry, fixture);
  }
  const args = ['--import', `data:text/javascript,${encodeURIComponent(preload)}`, entry,
    ...(argv ?? ['--meow-bootstrap-fd', '3'])];
  const child = spawn(process.execPath, args, {
    cwd, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
    stdio: ['ignore', 'pipe', 'pipe', 'pipe'],
  });
  let stdout = '', stderr = '';
  child.stdout.on('data', bytes => { stdout += bytes; });
  child.stderr.on('data', bytes => { stderr += bytes; });
  const exited = new Promise(resolve => child.once('close', (code, signal) => resolve({ code, signal })));
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await exited; }
  });
  const channel = child.stdio[3];
  const closed = new Promise(resolve => channel.once('close', resolve));
  const endpoint = new Promise((resolve, reject) => {
    let pending = Buffer.alloc(0), ready = false;
    channel.on('error', () => { if (!ready) reject(new Error('BOOTSTRAP_CLOSED')); });
    channel.once('close', () => { if (!ready) reject(new Error('BOOTSTRAP_CLOSED')); });
    channel.on('data', bytes => {
      if (ready) { channel.destroy(new Error('UNEXPECTED_BOOTSTRAP_DATA')); return; }
      pending = Buffer.concat([pending, bytes]);
      const newline = pending.indexOf(10);
      if (newline === -1) return;
      try {
        assert.equal(newline, pending.length - 1);
        const value = decodeJSONValue(pending.subarray(0, newline));
        assert.deepEqual(Object.keys(value), ['baseUrl']);
        validateEndpoint(value.baseUrl);
        ready = true;
        resolve(value.baseUrl);
      } catch (error) { ready = true; reject(error); channel.destroy(); }
    });
  });
  endpoint.catch(() => {});
  if (sendConfiguration) channel.write(`${JSON.stringify(configuration)}\n`);
  return { child, area, args, configuration, channel, endpoint, closed, exited, output: () => ({ stdout, stderr }) };
}

export async function connect(runtime) {
  const baseUrl = await runtime.endpoint;
  const { sessionId, requestKey } = runtime.configuration;
  const client = createRuntimeClient({ baseUrl, sessionId, requestKey, timeout: 20000 });
  return { client, baseUrl, closed: runtime.closed };
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

export function okay(reply, expectedContract = contract) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, expectedContract);
  assert.deepEqual(Object.keys(reply).sort(), ['contract', 'ok', 'value']);
  return reply.value;
}

/** One raw upload followed by the current installation commands. */
export async function load(client) {
  const installationId = randomUUID(), sourceRevision = 1;
  okay(await client.upload('music', installationId, sourceRevision, Buffer.from(script), { basename: 'source.js' }));
  const added = okay(await client.call('music.script.sources.add', { installationId, sourceRevision })).item;
  assert.equal(added.enabled, false);
  return client.call('music.script.sources.enable', { installationId });
}
