import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { windowsRoot } from './windows/working-paths.mjs';

export function checkTestSummary(output, name) {
  const summary = field => {
    const matches = [...output.matchAll(new RegExp(`^# ${field} (\\d+)\\r?$`, 'gm'))];
    if (!matches.length) throw new Error(`${name}: missing ${field} test summary`);
    return Number(matches.at(-1)[1]);
  };
  const count = summary('tests');
  if (!count || summary('pass') !== count || summary('fail') !== 0 || summary('skipped') !== 0 ||
      summary('cancelled') !== 0 || summary('todo') !== 0) {
    throw new Error(`${name}: empty, failed, skipped, cancelled or todo tests cannot satisfy the code gate`);
  }
  return count;
}

export function runTests(files, { cwd = windowsRoot, name = 'tests' } = {}) {
  if (!files.length) throw new Error(`${name}: no test files discovered`);
  // Bound test-file workers rather than relaxing the script host's timing,
  // cancellation or resource assertions on high-core-count desktops.
  const result = spawnSync(process.execPath, ['--test', '--test-concurrency=2', '--test-reporter=tap', ...files], {
    cwd, windowsHide: true, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024, timeout: 120_000,
    env: { ...process.env, FORCE_COLOR: '0' }
  });
  process.stdout.write(result.stdout ?? '');
  process.stderr.write(result.stderr ?? '');
  if (result.status !== 0) throw new Error(`${name}: test runner failed (${result.status ?? result.signal ?? 'launch error'})`);
  return checkTestSummary(result.stdout ?? '', name);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const count = runTests(process.argv.slice(2), { name: 'selected' });
  console.log(`Selected Node tests passed: ${count}.`);
}
