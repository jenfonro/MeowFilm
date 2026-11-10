import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { normalizeNFKC } from '@meowfilm/script-host/unicode';
import { normalizeNFKCNative } from '../src/native-engine.js';

const cases = [
  ['empty', '', ''],
  ['ASCII', ' Film 01 - [DDP]\t\n', ' Film 01 - [DDP]\t\n'],
  ['fullwidth', 'ＡＢＣ１２３　［电影］', 'ABC123 [电影]'],
  ['ligatures', 'ﬀ ﬁ ﬂ ﬃ ﬄ', 'ff fi fl ffi ffl'],
  ['composition', 'Cafe\u0301 A\u030a', 'Café Å'],
  ['canonical ordering', 's\u0307\u0323', '\u1e69'],
  ['Hangul', '\u1100\u1161\u11a8 \u3131\u314f', '각 가'],
  ['astral', '😀 𠮷 𝔸𝟘', '😀 𠮷 A0'],
  ['lone surrogates', '\udc00\ud800A\u030a\udfff\ud800', '\udc00\ud800Å\udfff\ud800'],
  ['surrogates beside a pair', '\ud800\ud800\udc00\udfff', '\ud800\ud800\udc00\udfff'],
  ['NUL', 'Ａ\0e\u0301\0😀\ud800', 'A\0é\0😀\ud800'],
  ['normalized text', '电影·한글 🎬 é Å\ufe0f', '电影·한글 🎬 é Å\ufe0f'],
];

for (const [name, normalize] of [['native', normalizeNFKCNative], ['public helper', normalizeNFKC]]) {
  for (const [kind, input, expected] of cases) {
    test(`${name} NFKC preserves ${kind}`, () => {
      const prototype = String.prototype.normalize;
      assert.equal(normalize(input), expected);
      assert.equal(normalize(expected), expected, 'NFKC is idempotent');
      assert.equal(String.prototype.normalize, prototype);
    });
  }
  test(`${name} NFKC retains the complete expanded string`, () => {
    const input = '\ufdfa'.repeat(4096);
    const expected = 'صلى الله عليه وسلم'.repeat(4096);
    assert.equal(normalize(input), expected);
  });
  test(`${name} NFKC rejects non-string arguments`, () => {
    for (const value of [undefined, null, false, 1, 1n, Symbol('text'), [], {}, new String('Ａ')]) {
      assert.throws(() => normalize(value), { code: 'INVALID_ARGUMENT' });
    }
  });
}

function child(program) {
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', program], {
    encoding: 'utf8', windowsHide: true, timeout: 15_000,
    env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
  });
  assert.equal(result.error, undefined);
  assert.equal(result.signal, null, result.stderr);
  assert.equal(result.status, 0, result.stderr);
}

test('ICU-backed public NFKC does not load a native artifact', () => {
  child(`
    import assert from 'node:assert/strict';
    assert.ok(process.versions.icu);
    const prototype = String.prototype.normalize;
    process.dlopen = () => { throw new Error('Unexpected native binding load'); };
    const { normalizeNFKC } = await import(${JSON.stringify(new URL('../src/unicode.js', import.meta.url).href)});
    assert.equal(normalizeNFKC('Ａe\\u0301'), 'Aé');
    assert.equal(String.prototype.normalize, prototype);
    const failed = new Error('ICU failure');
    String.prototype.normalize = () => { throw failed; };
    assert.throws(() => normalizeNFKC('Ａ'), error => error === failed);
  `);
});

test('simulated missing ICU dispatches to real native NFKC and propagates binding failures', () => {
  // This is a Linux/desktop dispatch test, not evidence from a NodeMobile build.
  child(`
    import assert from 'node:assert/strict';
    import { createRequire } from 'node:module';
    import { fileURLToPath } from 'node:url';
    delete process.versions.icu;
    assert.equal(process.versions.icu, undefined);
    const require = createRequire(${JSON.stringify(import.meta.url)});
    const binding = require(fileURLToPath(${JSON.stringify(new URL('../build/Release/script_host_native.node', import.meta.url).href)}));
    const native = binding.normalizeNFKC;
    let calls = 0;
    binding.normalizeNFKC = value => { calls++; return native(value); };
    binding.create = () => { throw new Error('Normalization must not create a script owner'); };
    String.prototype.normalize = () => { throw new Error('ICU-less String.normalize must not be used'); };
    const prototype = String.prototype.normalize;
    const { normalizeNFKC } = await import(${JSON.stringify(new URL('../src/unicode.js', import.meta.url).href)});
    const cases = ${JSON.stringify(cases)};
    for (const [, input, expected] of cases) assert.equal(normalizeNFKC(input), expected);
    assert.equal(calls, cases.length);
    assert.equal(String.prototype.normalize, prototype);
    binding.normalizeNFKC = undefined;
    assert.throws(() => normalizeNFKC('Ａ'), { code: 'NATIVE_ABI_MISMATCH' });
    const failed = Object.assign(new Error('native failure'), { code: 'RESOURCE_LIMIT' });
    binding.normalizeNFKC = () => { throw failed; };
    assert.throws(() => normalizeNFKC('Ａ'), error => error === failed);
  `);
});
