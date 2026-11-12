import assert from 'node:assert/strict';
import { before, test } from 'node:test';
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const directory = join(root, 'tmp/test-results/film-rules-native/raw-interrupt');
const executable = join(directory, process.platform === 'win32' ? 'probe.exe' : 'probe');
const source = join(root, 'packages/script-host/test/fixtures/native-json-interrupt.c');
const include = join(root, 'native/JavaScript/quickjs');

before(() => {
  mkdirSync(directory, { recursive: true });
  // Link the exact static core produced by the ordinary addon build.
  if (process.platform === 'win32') {
    execFileSync('cl', ['/nologo', '/O2', '/std:c11', `/I${include}`, source,
      join(root, 'packages/script-host/build/Release/quickjs.lib'),
      `/Fo${join(directory, 'probe.obj')}`, `/Fe${executable}`, 'bcrypt.lib'], { stdio: 'pipe' });
  } else {
    execFileSync(process.env.CC ?? 'cc', ['-std=c11', '-O2', '-I', include, source,
      join(root, 'packages/script-host/build/Release/quickjs.a'),
      '-lm', '-pthread', ...(process.platform === 'linux' ? ['-ldl'] : []), '-o', executable],
    { stdio: 'pipe' });
  }
});

const program = `let expression, pattern, flags;
globalThis.caught = false;
globalThis.meowfilmProcess = input => {
  if (!input.pattern) return { ok: true, caught: globalThis.caught };
  try {
    if (pattern !== input.pattern || flags !== input.flags) {
      expression = new RegExp(input.pattern, input.flags);
      pattern = input.pattern; flags = input.flags;
    }
    expression.lastIndex = 0;
    return input.remove ? { text: input.text.replace(expression, ''), caught: globalThis.caught }
      : { matched: expression.test(input.text), caught: globalThis.caught };
  } catch { globalThis.caught = true; return { caught: true }; }
};`;

const cases = {
  compilation: () => ({ pattern: '(?<=' + 'a'.repeat(1_048_576) + ')b', flags: 'i', text: 'b' }),
  prescan: () => ({ pattern: '\\k<x>'.repeat(9999) + 'a'.repeat(1_048_576) + '(?<x>b)',
    flags: 'i', text: 'b' }),
  deletion: () => ({ pattern: '(?=a{4096})a', text: 'a'.repeat(1_048_576), flags: 'ig', remove: true }),
  forward: () => ({ pattern: '^(?=(a{1048576}))' + '(?=\\1)'.repeat(9999),
    flags: 'i', text: 'a'.repeat(1_048_576) }),
  backward: () => ({ pattern: '^(?=(a{1048576}))a{1048576}' + '(?<=\\1)'.repeat(9999),
    flags: 'i', text: 'a'.repeat(1_048_576) }),
};

function frame(text) {
  const bytes = Buffer.from(text), length = Buffer.alloc(4);
  length.writeUInt32LE(bytes.length);
  return Buffer.concat([length, bytes]);
}

for (const [kind, request] of Object.entries(cases)) {
  test(`${kind} native interruption cannot enter JavaScript catch in the same raw Context`,
    { timeout: 10_000 }, t => {
      const input = request();
      const warm = kind === 'forward' || kind === 'backward' ? { ...input, text: 'b' } : {};
      const probe = { pattern: '^(a)\\1$', text: 'aa', flags: 'i' };
      const output = execFileSync(executable, {
        input: Buffer.concat([frame(program), ...[warm, input, probe].map(value => frame(JSON.stringify(value)))]),
        encoding: 'utf8', timeout: 6_000,
      });
      const result = JSON.parse(output);
      assert.equal(result.fired, 1);
      assert.equal(result.failed, true);
      assert.ok(result.elapsedMs < 5_000);
      assert.deepEqual(result.probe, { matched: true, caught: false },
        'a single interrupt pulse must bypass catch before probing the same Context');
      t.diagnostic(output.trim());
    });
}
