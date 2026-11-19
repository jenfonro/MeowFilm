import assert from 'node:assert/strict';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { join } from 'node:path';

// Change an input after common packaging, then restore it immediately after
// the wrapper reads it. A final filesystem audit alone cannot catch this race.
export async function rejectRestoredPlatformSource(pack, source, output) {
  const original = fs.readFileSync(source);
  const { openSync, closeSync } = fs;
  const opened = new Map();
  let changed = false, restored = false;
  fs.openSync = (...args) => {
    const fd = openSync(...args);
    opened.set(fd, args[0]);
    return fd;
  };
  fs.closeSync = fd => {
    const path = opened.get(fd);
    opened.delete(fd);
    closeSync(fd);
    if (!changed && path === join(output, 'runtime-manifest.json')) {
      fs.writeFileSync(source, Buffer.concat([original, Buffer.from('\nexport const changedInput = true;\n')]));
      changed = true;
    } else if (changed && !restored && path === source) {
      restored = true;
      fs.writeFileSync(source, original);
    }
  };
  syncBuiltinESMExports();
  try {
    await assert.rejects(pack(), { code: 'SOURCE_CHANGED' });
    assert.equal(changed && restored, true);
    assert.deepEqual(fs.readFileSync(source), original);
    assert.equal(JSON.parse(fs.readFileSync(join(output, 'runtime-manifest.json'))).status, 'failed');
  } finally {
    fs.openSync = openSync;
    fs.closeSync = closeSync;
    syncBuiltinESMExports();
    fs.writeFileSync(source, original);
  }
}
