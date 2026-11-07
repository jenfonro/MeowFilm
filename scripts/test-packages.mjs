import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { runTests } from './run-tests.mjs';
import { discoverWorkspaces } from './verify-dependency-lock.mjs';
import { windowsRoot } from './windows/working-paths.mjs';

const packages = [...discoverWorkspaces(windowsRoot,
  JSON.parse(readFileSync(join(windowsRoot, 'package.json'), 'utf8')))]
  .sort(([left], [right]) => left.localeCompare(right));
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
for (const [location, manifest] of packages) {
  try {
    const cwd = resolve(windowsRoot, location);
    if (!manifest.name?.startsWith('@meowfilm/') || !manifest.scripts?.test) {
      throw new Error(`Package ${location} must have an identity and tests`);
    }
    const files = ['test', 'tests'].flatMap(directory => testFiles(resolve(cwd, directory))).sort();
    if (files.length === 0) throw new Error(`Package ${location} has no test cases`);
    console.log(`\n=== ${manifest.name} ===`);
    // Each new package uses the built-in test runner; never execute a reference repo's scripts.
    // Discover test cases explicitly: fixture/support modules must not inflate
    // the executed-test count merely because they live below a test directory.
    runTests(files, { cwd, name: manifest.name });
  } catch (error) {
    failures.push({ location, message: error.message });
    console.error(`${location}: ${error.message}`);
  }
}
console.log(`\nImplementation packages: ${packages.length}; failed groups: ${failures.length}.`);
if (failures.length) process.exitCode = 1;
