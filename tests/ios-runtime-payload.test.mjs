import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fixture } from './ios-native-package-support.mjs';

test('packaged iOS JavaScript resolves only its offline closure on the build host', async t => {
  const f = fixture(t);
  await f.pack();
  // This is import/storage/HTTP evidence only. iOS native invocation and true
  // NodeMobile exit run in the signed preflight and real App host.
  const entry = join(f.output, 'closure-probe.mjs');
  writeFileSync(entry, `
    import assert from 'node:assert/strict';
    import { registerHooks } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep, join } from 'node:path';
    const payload = process.cwd();
    registerHooks({ resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url.startsWith('file:')) {
        const path = relative(payload, fileURLToPath(result.url));
        if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
          throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
      }
      return result;
    } });
    const { createRuntime, createRuntimeClient } = await import('./app/entry.mjs');
    const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86', requestKey = 'a'.repeat(64);
    const runtime = createRuntime({ sessionId, requestKey, dataRoot: join(payload, 'data'), source: { platform: 'ios' } });
    let client;
    try {
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl, sessionId, requestKey });
      const described = await client.describe();
      assert.equal(described.ok, true);
      assert.deepEqual(described.value.modules.map(value => [value.id, value.version]),
        [['music', '0.8.0'], ['cat', '0.2.0'], ['source', '0.2.0']]);
      const listed = await client.call('music.script.sources.list');
      assert.equal(listed.ok, true);
      assert.equal((await client.shutdown()).ok, true);
    } finally { await runtime.close(); await client?.close(); }
    console.log('IOS_JS_CLOSURE_OK');
  `);
  try {
    const result = spawnSync(process.execPath, [entry], {
      cwd: f.output, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', timeout: 30_000,
    });
    assert.equal(result.signal, null, result.stderr);
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    assert.match(result.stdout, /IOS_JS_CLOSURE_OK/);
  } finally { rmSync(entry); }
});
