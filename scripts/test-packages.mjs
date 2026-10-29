import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { runTests } from './run-tests.mjs';
import { windowsRoot } from './windows/working-paths.mjs';

const packages = readdirSync(join(windowsRoot, 'packages'), { withFileTypes: true })
  .filter(entry => entry.isDirectory()).map(entry => entry.name).sort();
if (packages.length === 0) throw new Error('No implementation packages discovered');
function testFiles(directory) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return testFiles(path);
    return /\.test\.[cm]?js$/.test(entry.name) ? [path] : [];
  });
}
const failures = [];
for (const name of packages) {
  try {
    const cwd = resolve(windowsRoot, 'packages', name);
    const manifest = JSON.parse(readFileSync(resolve(cwd, 'package.json'), 'utf8'));
    if (!manifest.name?.startsWith('@meowfilm/') || !manifest.scripts?.test) {
      throw new Error(`Package ${name} must have an identity and tests`);
    }
    const files = ['test', 'tests'].flatMap(directory => testFiles(resolve(cwd, directory))).sort();
    if (files.length === 0) throw new Error(`Package ${name} has no test cases`);
    console.log(`\n=== ${manifest.name} ===`);
    // Each new package uses the built-in test runner; never execute a reference repo's scripts.
    // Discover test cases explicitly: fixture/support modules must not inflate
    // the executed-test count merely because they live below a test directory.
    runTests(files, { cwd, name });
  } catch (error) {
    failures.push({ name, message: error.message });
    console.error(`${name}: ${error.message}`);
  }
}
console.log(`\nImplementation packages: ${packages.length}; failed groups: ${failures.length}.`);
if (failures.length) process.exitCode = 1;
