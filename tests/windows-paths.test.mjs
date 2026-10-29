import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, rmdirSync, symlinkSync, lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  assertOwnedPath, windowsRoot, buildRoot, tmpRoot, workingRoot, temporaryRoot,
} from '../scripts/windows/working-paths.mjs';

function fixture(run) {
  assertOwnedPath(tmpRoot, temporaryRoot);
  const root = mkdtempSync(join(temporaryRoot, 'isolation-'));
  try { return run(root); }
  finally {
    assertOwnedPath(temporaryRoot, root);
    // fs.rm removes links themselves; it does not recursively follow them.
    rmSync(root, { recursive: true, force: true });
  }
}
const psQuote = value => "'" + value.replaceAll("'", "''") + "'";
const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');

test('Windows paths require an explicit absolute owner and reject prefix/traversal escapes', () => fixture(root => {
  assert.throws(() => assertOwnedPath(root, 'relative', { allowMissing: true }));
  assert.throws(() => assertOwnedPath(root, root));
  assert.throws(() => assertOwnedPath(root, root + '-other', { allowMissing: true }));
  assert.throws(() => assertOwnedPath(root, join(root, '..', 'other'), { allowMissing: true }));
  assert.equal(assertOwnedPath(root, join(root, 'new', 'data'), { allowMissing: true }), join(root, 'new', 'data'));
}));

test('Windows path checks reject live and dangling junction ancestors', () => fixture(root => {
  const target = join(root, 'target'), link = join(root, 'link');
  mkdirSync(target);
  symlinkSync(target, link, 'junction');
  assert.ok(lstatSync(link).isSymbolicLink());
  assert.throws(() => assertOwnedPath(root, join(link, 'new'), { allowMissing: true }));
  rmdirSync(target);
  assert.throws(() => assertOwnedPath(root, join(link, 'new'), { allowMissing: true }));
}));

test('Windows JS source and working paths do not change with cwd', () => fixture(root => {
  const module = pathToFileURL(join(windowsRoot, 'scripts/windows/working-paths.mjs')).href;
  const output = execFileSync(process.execPath, ['--input-type=module', '-e',
    `import {windowsRoot,buildRoot,tmpRoot,workingRoot} from ${JSON.stringify(module)}; console.log(JSON.stringify({windowsRoot,buildRoot,tmpRoot,workingRoot}));`],
  { cwd: root, windowsHide: true, encoding: 'utf8' });
  assert.deepEqual(JSON.parse(output), { windowsRoot, buildRoot, tmpRoot, workingRoot });
  assert.equal(buildRoot, join(windowsRoot, 'build', 'windows'));
  assert.equal(tmpRoot, join(windowsRoot, 'tmp'));
  assert.equal(workingRoot, join(tmpRoot, 'working'));
  assert.equal(temporaryRoot, join(tmpRoot, 'temporary'));
}));

test('Windows logged native failures preserve stderr and allow subsequent independent checks', () => fixture(root => {
  const log = join(root, 'synthetic.log');
  const script = `
    . ${psQuote(join(windowsRoot, 'scripts/windows/environment.ps1'))}
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsNode -Arguments @('-e',
      "console.error('synthetic-isolation-failure'); process.exitCode=7;") -Log ${psQuote(log)}
    Write-Output ('ISOLATION_RESULT ' + ([ordered]@{exit=$Exit; continued=$true; preference=$ErrorActionPreference.ToString()} | ConvertTo-Json -Compress))
  `;
  const output = execFileSync(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
    { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30_000 });
  const summary = output.split(/\r?\n/).find(line => line.startsWith('ISOLATION_RESULT '));
  assert.ok(summary);
  assert.deepEqual(JSON.parse(summary.slice('ISOLATION_RESULT '.length)), { exit: 7, continued: true, preference: 'Stop' });
  const bytes = readFileSync(log);
  const text = bytes[0] === 0xff && bytes[1] === 0xfe ? bytes.toString('utf16le') : bytes.toString('utf8');
  assert.match(text, /synthetic-isolation-failure/);
}));
