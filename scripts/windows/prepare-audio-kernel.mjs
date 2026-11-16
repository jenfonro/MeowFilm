import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { lstat, mkdir, mkdtemp, readFile, readdir, rename, rm } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dependencyCacheRoot, downloadDependency } from '../dependency-download.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const audioSource = join(root, 'apps/windows/native/AudioKernel');
export const audioCache = join(root, '.cache/windows-audio-kernel');
export const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export const fileHash = async path => hash(await readFile(path));

async function exists(path) {
  try { await lstat(path); return true; }
  catch (error) { if (error.code === 'ENOENT') return false; throw error; }
}

export async function treeHash(directory) {
  const digest = createHash('sha256');
  async function visit(folder) {
    for (const entry of (await readdir(folder, { withFileTypes: true })).sort((a, b) =>
      a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
      const path = join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Audio source cache cannot contain links');
      if (entry.isDirectory()) await visit(path);
      else if (entry.isFile()) digest.update(relative(directory, path).replaceAll('\\', '/'))
        .update('\0').update(await readFile(path)).update('\0');
      else throw new Error('Invalid audio source cache entry');
    }
  }
  await visit(directory);
  return digest.digest('hex');
}

export async function readAudioLock() {
  const lock = JSON.parse(await readFile(join(audioSource, 'audio-kernel.lock.json'), 'utf8'));
  if (lock.schema !== 1 || lock.architecture !== 'win-x64' || lock.source.version !== '9.0.1' ||
      lock.binding.version !== '9.0.1.1' ||
      JSON.stringify(lock.libraries.map(item => [item.name, item.major])) !==
        JSON.stringify([['avutil', 61], ['swresample', 7], ['avcodec', 63], ['avformat', 63], ['avfilter', 12]]))
    throw new Error('Unsupported audio-kernel lock');
  return lock;
}

async function verifySource(source, lock) {
  if ((await readFile(join(source, 'RELEASE'), 'utf8')).trim() !== lock.source.version)
    throw new Error('Audio FFmpeg release mismatch');
  for (const library of lock.libraries) {
    const prefix = `LIB${library.name.toUpperCase()}_VERSION_`;
    const directory = join(source, `lib${library.name}`);
    const headers = await readFile(join(directory, 'version.h'), 'utf8') + '\n' +
      await readFile(join(directory, 'version_major.h'), 'utf8');
    for (const [field, expected] of [['MAJOR', library.major], ['MINOR', library.minor], ['MICRO', library.micro]]) {
      const value = new RegExp(`^#define ${prefix}${field}\\s+(\\d+)\\s*$`, 'm').exec(headers)?.[1];
      if (Number(value) !== expected) throw new Error(`Audio FFmpeg ABI mismatch: ${library.name}`);
    }
  }
  if (await fileHash(join(source, 'LICENSE.md')) !== await fileHash(join(root, 'LICENSES/FFmpeg-LICENSE.md')) ||
      await fileHash(join(source, 'COPYING.LGPLv2.1')) !== await fileHash(join(root, 'LICENSES/LGPL-2.1-only.txt')))
    throw new Error('Audio FFmpeg license texts changed');
}

export async function prepareAudioKernel() {
  const lock = await readAudioLock();
  const archive = await downloadDependency(lock.source, {
    directory: join(dependencyCacheRoot(), 'ffmpeg/downloads'), suffix: '.tar.xz',
  });
  const source = join(audioCache, 'sources', lock.source.sha256);
  if (!await exists(source)) {
    await mkdir(dirname(source), { recursive: true });
    const temporary = await mkdtemp(source + '.extract-');
    try {
      execFileSync('tar', ['-xf', archive, '--strip-components=1', '-C', temporary],
        { windowsHide: true, stdio: 'pipe' });
      await verifySource(temporary, lock);
      if (await treeHash(temporary) !== lock.source.treeSha256)
        throw new Error('Official audio FFmpeg source tree mismatch');
      await rename(temporary, source);
    } finally { await rm(temporary, { recursive: true, force: true }); }
  }
  if (await treeHash(source) !== lock.source.treeSha256)
    throw new Error('Audio FFmpeg source cache changed outside the locked recipe');
  await verifySource(source, lock);
  const prepared = { archive: lock.source.sha256, tree: lock.source.treeSha256 };
  return {
    lock, source, cache: audioCache, shared: join(dependencyCacheRoot(), 'ffmpeg/audio-kernel'),
    prepared, downloadRecipe: await fileHash(new URL('../dependency-download.mjs', import.meta.url)),
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.platform !== 'win32' || process.arch !== 'x64')
    throw new Error('Audio-kernel preparation requires Windows x64');
  prepareAudioKernel().then(result => console.log(JSON.stringify(result))).catch(error => {
    console.error(error);
    // The caller may retry without its process-local proxy only for a failed connection.
    process.exitCode = error.name === 'TypeError' && error.message === 'fetch failed' ? 75 : 1;
  });
}
