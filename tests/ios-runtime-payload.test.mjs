import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { packageIOSRuntime } from '../scripts/package-ios-runtime.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';
import { connect, load, nativeStorage, okay, root, simulatedIOS, spawnRuntime } from './ios-runtime-support.mjs';

test('packaged iOS payload runs its manifest entry offline, self-contained, on the simulated asm.js backend',
  { timeout: 90000 }, async t => {
    mkdirSync(temporaryRoot, { recursive: true });
    const area = mkdtempSync(join(temporaryRoot, 'ios-runtime-payload-'));
    t.after(() => rmSync(assertOwnedPath(temporaryRoot, area), { recursive: true, force: true }));
    const output = join(area, 'payload');
    const manifest = await packageIOSRuntime({ root, product: 'aggregate', output });
    assert.equal(manifest.status, 'complete');
    assert.deepEqual(JSON.parse(readFileSync(join(output, 'runtime-manifest.json'), 'utf8')), manifest);
    // Every resolved file must come from this payload, never the source tree.
    const confined = `${simulatedIOS}
      import { registerHooks } from 'node:module';
      import { fileURLToPath } from 'node:url';
      import { isAbsolute, relative, sep } from 'node:path';
      const payload = ${JSON.stringify(output)};
      registerHooks({ resolve(specifier, context, next) {
        const result = next(specifier, context);
        if (result.url.startsWith('file:')) {
          const path = relative(payload, fileURLToPath(result.url));
          if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
            throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
        }
        return result;
      } });`;
    const runtime = spawnRuntime(t, { entry: join(output, manifest.entry), cwd: output, preload: confined });
    const storage = nativeStorage();
    const { peer, closed } = connect(runtime, storage.methods);
    const described = await peer.call('runtime.describe', null);
    assert.deepEqual(described.modules.map(module => [module.id, module.version]), [['music', '0.7.0'], ['cat', '0.1.0']]);
    const instance = okay(await load(peer, { installationId: 'payload' })).instance;
    const reply = okay(await peer.call('music.script.invoke', {
      installationId: 'payload', revision: 1, generation: instance.generation, siteId: 'tx',
      operation: 'musicUrl', payload: { type: '320k', musicInfo: { songmid: 'm' } },
    }));
    assert.equal(reply.value, 'http://media.invalid/tx/320k?n=1');
    assert.deepEqual(storage.calls.map(call => call.operation), ['get', 'set']);
    okay(await peer.call('music.script.unload', { installationId: 'payload', revision: 1 }));
    peer.close();
    await closed;
    assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
    assert.equal(runtime.output().stderr, '');
  });
