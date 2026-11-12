import test from 'node:test';
import assert from 'node:assert/strict';
import { createCapabilityClient, SourceSession } from '../src/index.js';
import { sourcePreprocessError } from '../src/errors.js';

test('preprocessing keeps syntax errors separate from engine and Acorn resource errors', () => {
  for (const [name, message, code] of [
    ['SyntaxError', 'Unexpected token (1:2)', 'SCRIPT_PROTOCOL_ERROR'],
    ['SyntaxError', 'Not enough stack space to parse input (11:21)', 'RESOURCE_LIMIT'],
    ['SyntaxError', 'Not enough stack space to parse input is a variable', 'SCRIPT_PROTOCOL_ERROR'],
    ['InternalError', 'stack overflow', 'RESOURCE_LIMIT'],
    ['RangeError', 'Maximum call stack size exceeded', 'RESOURCE_LIMIT'],
    ['RangeError', 'unrelated range failure', 'SCRIPT_PROTOCOL_ERROR'],
  ]) {
    assert.equal(sourcePreprocessError(Object.assign(new Error(message), { name })).code, code);
  }
  for (const code of ['CANCELLED', 'TIMEOUT', 'RESOURCE_LIMIT', 'UNSUPPORTED_DEPENDENCY']) {
    assert.equal(sourcePreprocessError({ code }).code, code);
  }
});

test('complete Cat normalization and capability transformation finish before any sandbox work',
  async t => {
    for (const protocol of ['cat', 'legado']) {
      let inside = false;
      const transformed = [];
      const session = new SourceSession({
        installationId: `preprocess-${protocol}`, protocol,
        source: protocol === 'cat' ? { script: 'export default { searchContent() { return "ok"; } };' } : {
          bookSourceName: 'Preprocess', bookSourceUrl: 'https://preprocess.invalid/',
          coverDecodeJs: 'function load() { return java.get("https://preprocess.invalid/"); } load()',
        },
        capabilities: createCapabilityClient({ invoke() { assert.fail('no host calls'); } }),
        preprocess(operation) {
          assert.equal(inside, false);
          inside = true;
          try {
            const result = operation();
            assert.equal(typeof result, 'string');
            transformed.push(result);
            return result;
          } finally { inside = false; }
        },
        async createSandbox() {
          assert.equal(inside, false);
          return {
            capabilities: { actions: ['search'] },
            load(kind, script) {
              assert.equal(inside, false);
              if (protocol === 'cat') {
                assert.equal(script, transformed[0]);
                assert.doesNotMatch(script, /export default/);
                assert.match(script, /globalThis\.__meowCat/);
              }
            },
            activate() {},
            evaluate(script) {
              assert.equal(inside, false);
              assert.equal(script, transformed[0]);
              assert.match(script, /async function load/);
              assert.match(script, /await java\.get/);
              assert.match(script, /return \(await load\(\)\)/);
              return 'decoded';
            },
            close() {},
            async drain() {},
          };
        },
      });
      t.after(() => session.close());
      await session.start();
      session.activate();
      if (protocol === 'legado') {
        assert.equal((await session.invoke('image')).body, 'decoded');
      }
      assert.equal(transformed.length, 1);
      assert.equal(inside, false);
    }
  });
