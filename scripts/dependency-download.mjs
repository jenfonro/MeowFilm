import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { copyFile, link, lstat, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { pipeline } from 'node:stream/promises';

export function dependencyCacheRoot(platform = process.platform, home = homedir()) {
  return platform === 'win32' ? 'E:/Developer/cache' : join(home, 'Developer/cache');
}

async function verify(path, expected) {
  const stat = await lstat(path);
  if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Invalid dependency archive: ${path}`);
  const hash = createHash('sha256');
  for await (const bytes of createReadStream(path)) hash.update(bytes);
  if (hash.digest('hex') !== expected) throw new Error(`Dependency archive hash mismatch: ${path}`);
}

async function cached(path, expected) {
  try { await verify(path, expected); return true; }
  catch (error) { if (error.code === 'ENOENT') return false; throw error; }
}

async function publish(path, expected, write) {
  await mkdir(dirname(path), { recursive: true });
  const temporary = await mkdtemp(join(dirname(path), '.dependency-download-'));
  try {
    const file = join(temporary, 'archive');
    await write(file);
    await verify(file, expected);
    // Only complete bytes become visible. A concurrent winner is never replaced.
    try { await link(file, path); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      await verify(path, expected);
    }
  } finally { await rm(temporary, { recursive: true, force: true }); }
}

export async function downloadDependency({ url, sha256 }, {
  directory = join(dependencyCacheRoot(), 'downloads'), suffix = '', snapshot,
} = {}) {
  if (!/^[a-f0-9]{64}$/.test(sha256)) throw new Error('Invalid dependency archive SHA-256');
  // Existing private inputs remain usable without depending on shared cache retention.
  if (snapshot && await cached(snapshot, sha256)) return snapshot;
  const archive = join(directory, sha256 + suffix);
  if (!await cached(archive, sha256)) {
    await publish(archive, sha256, async file => {
      const response = await fetch(url);
      if (!response.ok) {
        await response.body?.cancel();
        throw new Error(`Dependency download failed: HTTP ${response.status} ${url}`);
      }
      await pipeline(response.body, createWriteStream(file, { flags: 'wx' }));
    });
  }
  if (snapshot) {
    // Copy into a private inode: build inputs must not make the shared archive writable.
    await publish(snapshot, sha256, file => copyFile(archive, file));
    return snapshot;
  }
  return archive;
}
