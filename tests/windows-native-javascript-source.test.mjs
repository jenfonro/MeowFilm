import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyNativeJavaScript } from '../scripts/verify-native-javascript.mjs';

test('Native JavaScript engine uses the pinned source and license inventory', () => {
  const result = verifyNativeJavaScript();
  assert.equal(result.project, 'quickjs-ng/quickjs');
  assert.ok(result.files > 0);
});

test('Native JavaScript source verification rejects altered engine and derivation inputs', t => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const fixture = mkdtempSync(resolve(tmpdir(), 'meowfilm-native-lock-'));
  t.after(() => rmSync(fixture, { recursive: true, force: true }));
  for (const path of ['scripts/quickjs-native.json', 'LICENSES/quickjs-native-MIT.txt', 'LICENSES/Unicode-3.0.txt', 'native/JavaScript/quickjs',
    'native/JavaScript/patches']) {
    mkdirSync(dirname(resolve(fixture, path)), { recursive: true });
    cpSync(resolve(root, path), resolve(fixture, path), { recursive: true });
  }
  verifyNativeJavaScript(fixture);
  for (const path of ['native/JavaScript/quickjs/libregexp.c',
    'native/JavaScript/quickjs/libunicode-table.h', 'native/JavaScript/patches/regexp-compile-interrupt.patch',
    'LICENSES/quickjs-native-MIT.txt', 'LICENSES/Unicode-3.0.txt']) {
    const file = resolve(fixture, path), original = readFileSync(file);
    writeFileSync(file, Buffer.concat([original, Buffer.from('\n')]));
    assert.throws(() => verifyNativeJavaScript(fixture), /hash differs/);
    writeFileSync(file, original);
  }
  const file = resolve(fixture, 'scripts/quickjs-native.json');
  const manifest = JSON.parse(readFileSync(file, 'utf8'));
  for (const key of ['patch', 'unicodeLicense']) {
    const changed = { ...manifest };
    delete changed[key];
    writeFileSync(file, JSON.stringify(changed));
    assert.throws(() => verifyNativeJavaScript(fixture), key === 'patch' ? /patch manifest/ : /Unicode license manifest/);
  }
  const alteredLicense = Buffer.from('UNICODE LICENSE V3\nCopyright changed\n');
  writeFileSync(resolve(fixture, 'LICENSES/Unicode-3.0.txt'), alteredLicense);
  manifest.unicodeLicense.sha256 = createHash('sha256').update(alteredLicense).digest('hex');
  writeFileSync(file, JSON.stringify(manifest));
  assert.throws(() => verifyNativeJavaScript(fixture), /Unicode license differs from the source notice/);
});

test('Native Unicode license retains the full original table notice and copyright', () => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const table = readFileSync(resolve(root, 'native/JavaScript/quickjs/libunicode-table.h'), 'utf8');
  const notice = table.slice(table.indexOf('UNICODE LICENSE V3\n'), table.indexOf('*/', table.indexOf('UNICODE LICENSE V3\n')));
  const license = readFileSync(resolve(root, 'LICENSES/Unicode-3.0.txt'), 'utf8');
  assert.equal(license, notice);
  assert.match(license, /^UNICODE LICENSE V3\n/);
  assert.match(license, /Copyright © 1991-2026 Unicode, Inc\./);
  assert.match(license, /authorization of the copyright holder\.\n$/);
});
