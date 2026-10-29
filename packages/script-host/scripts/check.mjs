import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('..', import.meta.url));
let count = 0;
async function walk(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) await walk(path);
    else if (/\.(mjs|js)$/.test(path)) {
      const text = await readFile(path, 'utf8');
      assert(!text.includes('\r'), `LF required: ${path}`);
      const result = spawnSync(process.execPath, ['--check', path], { encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      count++;
    }
  }
}
await walk(resolve(root, 'src'));
await walk(resolve(root, 'test'));
console.log(`Syntax/LF checks passed: ${count} JavaScript files`);
