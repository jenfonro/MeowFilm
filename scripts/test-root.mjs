import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { runTests } from './run-tests.mjs';
import { windowsRoot } from './windows/working-paths.mjs';

const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--platform' || !['all', 'windows', 'ios'].includes(args[1]))) {
  throw new Error('Usage: node scripts/test-root.mjs [--platform all|windows|ios]');
}
const platform = args[1] ?? 'all';
// Platform-prefixed files own the wrappers and source tests; unprefixed files
// cover shared contracts and must not read either platform's source tree.
const files = readdirSync(resolve(windowsRoot, 'tests')).filter(name => /\.test\.mjs$/.test(name))
  .filter(name => {
    const owner = /^(windows|ios)-/.exec(name)?.[1];
    return platform === 'all' || !owner || owner === platform;
  })
  .sort().map(name => resolve(windowsRoot, 'tests', name));
console.log(`\nRoot assembly/build tests passed: ${runTests(files, { name: `root (${platform})` })}.`);
