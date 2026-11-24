import { createHash } from 'node:crypto';
import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const files = ['Headers/sqlite3.h', 'Info.plist', 'MeowSQLite', 'Modules/module.modulemap'];

/** Both Apple writers consume the same pinned dynamic core, not libsqlite3. */
export function sqliteBuildInputs(root, target) {
  const fail = () => { throw Object.assign(new Error('SQLite build identity differs'), { code: 'STORAGE_BUILD_IDENTITY' }); };
  const read = path => {
    let current = root;
    for (const part of path.split('/')) {
      current = join(current, part);
      if (lstatSync(current).isSymbolicLink()) fail();
    }
    if (!lstatSync(current).isFile()) fail();
    return readFileSync(current);
  };
  const lockPath = 'native/SQLite/sqlite.json', script = 'scripts/prepare-ios-sqlite.py';
  const lockBytes = read(lockPath), lock = JSON.parse(lockBytes);
  const selected = target.sdk === 'iphoneos' ? 'ios-arm64'
    : target.sdk === 'iphonesimulator' ? 'ios-simulator' : target.sdk === 'macosx' ? 'macos' : null;
  if (!selected) fail();
  const base = `native/SQLite/.cache/Artifacts/${selected}`;
  const receiptPath = `${base}/sqlite-build.json`;
  const receipt = JSON.parse(read(receiptPath));
  if (typeof lock.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(lock.version)
      || receipt.version !== lock.version || receipt.target !== selected
      || receipt.lockSha256 !== hash(lockBytes) || receipt.buildScriptSha256 !== hash(read(script))
      || !/^[a-f0-9]{64}$/.test(receipt.identity)
      || !isDeepStrictEqual(receipt.compileOptions, lock.compileOptions)
      || !isDeepStrictEqual(Object.keys(receipt.files).sort(), files)
      || !/^(?:ios|macos)-(?:arm64|x86_64)(?:_(?:arm64|x86_64))?(?:-simulator)?$/.test(receipt.xcframeworkIdentifier)) fail();
  const framework = `${base}/MeowSQLite.framework`;
  const packaged = `native/SQLite/.cache/Artifacts/MeowSQLite.xcframework/${receipt.xcframeworkIdentifier}/MeowSQLite.framework`;
  const paths = [lockPath, script, 'scripts/quickjs/sqlite-inputs.mjs', receiptPath,
    ...files.map(file => `${framework}/${file}`), ...files.map(file => `${packaged}/${file}`)];
  const inputs = paths.map(path => ({ path, sha256: hash(read(path)) }));
  for (const file of files) {
    if (hash(read(`${framework}/${file}`)) !== receipt.files[file]
        || hash(read(`${packaged}/${file}`)) !== receipt.files[file]) fail();
  }
  const binary = read(`${framework}/MeowSQLite`);
  return { version: lock.version, identity: receipt.identity,
    artifact: { path: `${framework}/MeowSQLite`, bytes: binary.length, sha256: hash(binary) },
    framework, inputs };
}
