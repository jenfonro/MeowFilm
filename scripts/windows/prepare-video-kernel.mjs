import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile, writeFile, mkdir, readdir, rm, copyFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative, resolve } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const kernelSource = join(root, 'apps/windows/native/VideoKernel');
export const kernelCache = join(root, '.cache/windows-video-kernel');
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export const fileHash = async path => sha256(await readFile(path));

// Fixed source patch, exact context only: no fuzz, omitted hunk or foreign path.
export function applySourcePatch(files, patch) {
  const lines = patch.split('\n'), outputs = new Map();
  let index = 0;
  while (index < lines.length && lines[index]) {
    const old = /^--- a\/(.+)$/.exec(lines[index++]);
    const next = /^\+\+\+ b\/(.+)$/.exec(lines[index++]);
    if (!old || !next || old[1] !== next[1] || !files.has(old[1]))
      throw new Error('Invalid locked FFmpeg source patch');
    const path = old[1], input = files.get(path).split('\n'), output = [];
    let cursor = 0, hunks = 0;
    while (index < lines.length && lines[index].startsWith('@@')) {
      hunks++;
      const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(lines[index++]);
      if (!hunk) throw new Error('Invalid locked FFmpeg patch hunk');
      const start = Number(hunk[1]) - 1, oldCount = Number(hunk[2] ?? 1), newCount = Number(hunk[4] ?? 1);
      if (start < cursor || start > input.length) throw new Error('Out-of-order locked source patch');
      output.push(...input.slice(cursor, start)); cursor = start;
      let removed = 0, added = 0;
      while (index < lines.length && /^[ +\-]/.test(lines[index]) &&
             !lines[index].startsWith('--- a/')) {
        const line = lines[index++], value = line.slice(1);
        if (line[0] !== '+') {
          if (input[cursor++] !== value) throw new Error('Locked FFmpeg source context mismatch');
          removed++;
        }
        if (line[0] !== '-') { output.push(value); added++; }
      }
      if (removed !== oldCount || added !== newCount) throw new Error('Incomplete locked source patch');
    }
    if (!hunks || outputs.has(path)) throw new Error('Missing or duplicate locked source patch');
    output.push(...input.slice(cursor)); outputs.set(path, output.join('\n'));
  }
  if (lines.slice(index).some(line => line !== '')) throw new Error('Trailing locked source patch data');
  if (!outputs.size) throw new Error('Empty locked FFmpeg source patch');
  return outputs;
}

async function treeFiles(directory) {
  const files = [];
  async function visit(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Locked native source cannot traverse links');
      if (entry.isDirectory()) await visit(path);
      else if (entry.isFile()) files.push(relative(directory, path).replaceAll('\\', '/'));
    }
  }
  await visit(directory);
  return files.sort();
}
export async function treeHash(directory) {
  const hash = createHash('sha256');
  for (const path of await treeFiles(directory))
    hash.update(path).update('\0').update(await readFile(join(directory, path))).update('\0');
  return hash.digest('hex');
}

async function archive(name, specification, localCopy) {
  const path = join(kernelCache, 'downloads', name);
  await mkdir(dirname(path), { recursive: true });
  if (!existsSync(path)) {
    if (localCopy && existsSync(localCopy)) await copyFile(localCopy, path);
    else {
      const response = await fetch(specification.url);
      if (!response.ok) throw new Error(`Locked ${name} download failed: HTTP ${response.status}`);
      const temporary = `${path}.partial`;
      try {
        await writeFile(temporary, new Uint8Array(await response.arrayBuffer()));
        if (await fileHash(temporary) !== specification.sha256) throw new Error(`Locked ${name} hash mismatch`);
        await copyFile(temporary, path);
      } finally { await rm(temporary, { force: true }); }
    }
  }
  if (await fileHash(path) !== specification.sha256) throw new Error(`Locked ${name} hash mismatch`);
  return path;
}
async function extract(path, output) {
  await mkdir(output, { recursive: true });
  execFileSync('tar', ['-xf', path, '--strip-components=1', '-C', output], { windowsHide: true, stdio: 'pipe' });
}

export async function prepareVideoKernel(sevenZip) {
  const lock = JSON.parse(await readFile(join(kernelSource, 'video-kernel.lock.json'), 'utf8'));
  if (lock.version !== 1 || lock.architecture !== 'win-x64' || lock.vlcPluginAbi !== '3_0_0f')
    throw new Error('Unsupported private video-kernel lock');
  const patch = await readFile(join(kernelSource, 'ffmpeg-controlled-avio.patch'), 'utf8');
  const header = await readFile(join(kernelSource, 'ffmpeg-private.h'));
  const preparation = sha256(JSON.stringify({ sources: lock.sources, patch: sha256(patch), header: sha256(header) }));
  const ffmpeg = join(kernelCache, 'sources/ffmpeg');
  const xml = join(kernelCache, 'sources/libxml2');
  const sdkRoot = join(kernelCache, 'sdk/vlc-3.0.24');
  const marker = join(kernelCache, 'prepared.json');
  const archives = {
    ffmpeg: await archive('ffmpeg.tar.gz', lock.sources.ffmpeg, join(root, '.cache/ios-video-kernel/downloads/ffmpeg.tar.gz')),
    xml: await archive('libxml2-2.14.6.tar.xz', lock.sources.libxml2),
    sdk: await archive('vlc-3.0.24-win64.7z', lock.sources.vlcSdk)
  };
  let prepared = existsSync(marker) ? JSON.parse(await readFile(marker, 'utf8')) : null;
  if (!prepared || prepared.preparation !== preparation) {
    // These are exclusively generated source caches, never a checkout or user data.
    for (const path of [ffmpeg, xml, sdkRoot]) await rm(path, { recursive: true, force: true });
    await extract(archives.ffmpeg, ffmpeg); await extract(archives.xml, xml);
    await mkdir(sdkRoot, { recursive: true });
    execFileSync(sevenZip, ['x', '-y', `-o${sdkRoot}`, archives.sdk,
      'vlc-3.0.24/sdk/*', 'vlc-3.0.24/libvlccore.dll'], { windowsHide: true, stdio: 'pipe' });
    const paths = [...patch.matchAll(/^--- a\/(.+)$/gm)].map(match => match[1]);
    const files = new Map(await Promise.all(paths.map(async path => [path, await readFile(join(ffmpeg, path), 'utf8')])));
    for (const [path, text] of applySourcePatch(files, patch)) await writeFile(join(ffmpeg, path), text);
    for (const directory of ['libavformat', 'libavutil']) await writeFile(join(ffmpeg, directory, 'meowfilm-private.h'), header);
    if ((await readFile(join(ffmpeg, 'RELEASE'), 'utf8')).trim() !== lock.sources.ffmpeg.version)
      throw new Error('Locked FFmpeg release does not match');
    prepared = { preparation, ffmpeg: await treeHash(ffmpeg), xml: await treeHash(xml), sdk: await treeHash(sdkRoot) };
    await writeFile(marker, JSON.stringify(prepared, null, 2) + '\n');
  } else if (await treeHash(ffmpeg) !== prepared.ffmpeg || await treeHash(xml) !== prepared.xml ||
             await treeHash(sdkRoot) !== prepared.sdk) {
    throw new Error('Prepared native source cache changed outside the locked recipe');
  }
  const sdk = join(sdkRoot, 'vlc-3.0.24/sdk');
  const core = join(sdkRoot, 'vlc-3.0.24/libvlccore.dll');
  const pluginPc = await readFile(join(sdk, 'lib/pkgconfig/vlc-plugin.pc'), 'utf8');
  if (!pluginPc.includes('Version: 3.0.24') || await fileHash(core) !== lock.vlcCoreSha256 ||
      !existsSync(join(sdk, 'include/vlc/plugins/vlc_plugin.h')))
    throw new Error('Pinned VLC SDK/core identity mismatch');
  return { lock, prepared, source: kernelSource, cache: kernelCache, ffmpeg, xml, sdk, core };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.platform !== 'win32' || process.arch !== 'x64' || !process.argv[2])
    throw new Error('Private video-kernel preparation requires Windows x64 and its SDK extractor');
  console.log(JSON.stringify(await prepareVideoKernel(process.argv[2])));
}
