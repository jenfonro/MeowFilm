import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { runTests } from './run-tests.mjs';
import { windowsRoot } from './windows/working-paths.mjs';

const files = readdirSync(resolve(windowsRoot, 'tests')).filter(name => /\.test\.mjs$/.test(name))
  .sort().map(name => resolve(windowsRoot, 'tests', name));
console.log(`\nRoot assembly/build tests passed: ${runTests(files, { name: 'root' })}.`);
