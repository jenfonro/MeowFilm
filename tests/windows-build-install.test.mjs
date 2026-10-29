import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { assertOwnedPath, temporaryRoot, windowsRoot } from '../scripts/windows/working-paths.mjs';

const powershell = join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const quote = value => "'" + value.replaceAll("'", "''") + "'";
function fixture(run, { staged = true, backup = false, fresh = false } = {}) {
  const root = mkdtempSync(join(temporaryRoot, 'build-install-'));
  try {
    const tmp = join(root, 'tmp'), build = join(root, 'build', 'windows'), publish = join(tmp, 'publish');
    mkdirSync(join(tmp, 'working'), { recursive: true });
    if (!fresh) {
      mkdirSync(build, { recursive: true });
      writeFileSync(join(build, 'old.dll'), 'old');
    }
    writeFileSync(join(tmp, 'working/record.md'), 'keep');
    if (staged) {
      mkdirSync(publish);
      writeFileSync(join(publish, 'meowfilm.exe'), 'new');
      writeFileSync(join(publish, 'build-result.json'), '{}');
    }
    if (backup) mkdirSync(join(tmp, 'previous-build'));
    const script = `
      . ${quote(join(windowsRoot, 'scripts/windows/paths.ps1'))}
      . ${quote(join(windowsRoot, 'scripts/windows/install.ps1'))}
      $WindowsRoot = ${quote(root)}
      $WindowsTmpRoot = ${quote(tmp)}
      $WindowsBuildRoot = ${quote(build)}
      Install-WindowsBuild
    `;
    const result = spawnSync(powershell, ['-NoProfile', '-NonInteractive', '-Command', script],
      { cwd: temporaryRoot, windowsHide: true, encoding: 'utf8', timeout: 30_000,
        env: { ...process.env, PSModulePath: join(dirname(powershell), 'Modules') } });
    assert.ifError(result.error);
    return run(result, { root, tmp, build, publish });
  } finally {
    assertOwnedPath(temporaryRoot, root);
    rmSync(root, { recursive: true, force: true });
  }
}

test('Windows installation replaces the whole fixed build and preserves working records', () => fixture((result, paths) => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readdirSync(paths.build).sort(), ['build-result.json', 'meowfilm.exe']);
  assert.equal(readFileSync(join(paths.build, 'meowfilm.exe'), 'utf8'), 'new');
  assert.equal(readFileSync(join(paths.tmp, 'working/record.md'), 'utf8'), 'keep');
  assert.equal(existsSync(paths.publish), false);
  assert.equal(existsSync(join(paths.tmp, 'previous-build')), false);
}));

test('Windows installation leaves the current build unchanged when staging is missing', () => fixture((result, paths) => {
  assert.equal(result.status, 1);
  assert.match(result.stderr, /staging output is missing/);
  assert.deepEqual(readdirSync(paths.build), ['old.dll']);
  assert.equal(readFileSync(join(paths.build, 'old.dll'), 'utf8'), 'old');
}, { staged: false }));

test('Windows installation discards a leftover install backup and installs the new build', () => fixture((result, paths) => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readdirSync(paths.build).sort(), ['build-result.json', 'meowfilm.exe']);
  assert.equal(existsSync(join(paths.tmp, 'previous-build')), false);
}, { backup: true }));

test('Windows installation creates the platform build directory on first install', () => fixture((result, paths) => {
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readdirSync(paths.build).sort(), ['build-result.json', 'meowfilm.exe']);
}, { fresh: true }));
