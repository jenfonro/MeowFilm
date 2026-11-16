import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { cp, link, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dependencyCacheRoot } from '../dependency-download.mjs';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const source = join(root, 'apps/windows/native/FilmKernel');
export const cache = join(root, '.cache/windows-film-kernel');
export const support = join(root, 'scripts/windows/build-film-kernel-support.py');
export const sha256 = value => createHash('sha256').update(value).digest('hex');
export const fileHash = async path => sha256(await readFile(path));

export async function inventory(directory) {
  const result = {};
  async function visit(folder) {
    for (const entry of (await readdir(folder, { withFileTypes: true })).sort((a, b) =>
      a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
      const path = join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error(`Links are not allowed in locked inputs: ${path}`);
      if (entry.isDirectory()) await visit(path);
      else if (entry.isFile()) result[relative(directory, path).replaceAll('\\', '/')] = await fileHash(path);
      else throw new Error(`Special file in locked inputs: ${path}`);
    }
  }
  await visit(directory);
  return Object.fromEntries(Object.entries(result).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
}

export async function treeHash(directory) {
  const hash = createHash('sha256');
  for (const name of Object.keys(await inventory(directory)))
    hash.update(name).update('\0').update(await readFile(join(directory, name))).update('\0');
  return hash.digest('hex');
}

// Exact, ordered unified hunks only. No external patch tool, fuzz or line-ending conversion.
export function applySourcePatch(files, patch) {
  const lines = patch.split('\n'), outputs = new Map();
  let index = 0;
  while (index < lines.length && lines[index]) {
    const old = /^--- a\/(.+)$/.exec(lines[index++]);
    const next = /^\+\+\+ b\/(.+)$/.exec(lines[index++]);
    if (!old || !next || old[1] !== next[1] || !files.has(old[1]))
      throw new Error('Invalid locked film-kernel source patch');
    const path = old[1], input = files.get(path).split('\n'), output = [];
    let cursor = 0, hunks = 0;
    while (index < lines.length && lines[index].startsWith('@@')) {
      hunks++;
      const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(lines[index++]);
      if (!hunk) throw new Error('Invalid locked source patch hunk');
      const start = Number(hunk[1]) - 1;
      if (start < cursor || start > input.length) throw new Error('Out-of-order source patch');
      output.push(...input.slice(cursor, start)); cursor = start;
      let removed = 0, added = 0;
      while (index < lines.length && /^[ +\-]/.test(lines[index]) && !lines[index].startsWith('--- a/')) {
        const line = lines[index++], value = line.slice(1);
        if (line[0] !== '+') {
          if (input[cursor++] !== value) throw new Error(`Locked source context mismatch: ${path}`);
          removed++;
        }
        if (line[0] !== '-') { output.push(value); added++; }
      }
      if (removed !== Number(hunk[2] ?? 1) || added !== Number(hunk[4] ?? 1))
        throw new Error('Incomplete source patch');
    }
    if (!hunks || outputs.has(path)) throw new Error('Missing or duplicate source patch');
    output.push(...input.slice(cursor)); outputs.set(path, output.join('\n'));
  }
  if (!outputs.size || lines.slice(index).some(line => line !== ''))
    throw new Error('Empty or trailing source patch data');
  return outputs;
}

async function download(item, name) {
  const directory = join(dependencyCacheRoot(), name === 'ffmpeg' ? 'ffmpeg/downloads' : 'windows-film-kernel/downloads');
  const archive = join(directory, `${item.sha256}.tar.gz`);
  if (existsSync(archive)) {
    if (await fileHash(archive) !== item.sha256) throw new Error(`Archive changed: ${archive}`);
    return archive;
  }
  await mkdir(directory, { recursive: true });
  const temporary = await mkdtemp(join(directory, '.download-'));
  try {
    const output = join(temporary, 'archive');
    let downloaded = false;
    for (const proxy of ['http://127.0.0.1:7890', '']) {
      try {
        execFileSync('curl', ['--fail', '--location', '--silent', '--show-error',
          '--connect-timeout', '5', '--max-time', '600', '--proxy', proxy, '--noproxy', '',
          '--output', output, item.url], { stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true });
        downloaded = true; break;
      } catch (error) {
        if (!proxy) throw error;
      }
    }
    if (!downloaded || await fileHash(output) !== item.sha256)
      throw new Error(`Locked archive hash mismatch: ${name}`);
    try { await link(output, archive); }
    catch (error) {
      if (error.code !== 'EEXIST' || await fileHash(archive) !== item.sha256) throw error;
    }
  } finally { await rm(temporary, { recursive: true, force: true }); }
  return archive;
}

export async function prepareFilmKernel(python) {
  const lock = JSON.parse(await readFile(join(source, 'film-kernel.lock.json'), 'utf8'));
  if (lock.schema !== 1 || lock.target !== 'windows-x86_64-ucrt') throw new Error('Unsupported film-kernel lock');
  const ios = JSON.parse(await readFile(join(root, 'scripts/ios-video-kernel.json'), 'utf8'));
  for (const key of ['version', 'revision', 'url', 'sha256'])
    if (lock.sources.ffmpeg[key] !== ios.sources.ffmpeg[key]) throw new Error(`FFmpeg iOS source mismatch: ${key}`);
  if (JSON.stringify(lock.provenance.ksPlayer) !== JSON.stringify(ios.sources.ksplayer))
    throw new Error('KSPlayer mechanism provenance differs from the fixed source');
  for (const [path, hash] of Object.entries(lock.inputs))
    if (await fileHash(join(root, path)) !== hash) throw new Error(`Locked patch/header/wrap changed: ${path}`);
  const identity = sha256(JSON.stringify({ sources: lock.sources, patches: lock.patches, inputs: lock.inputs }));
  const sources = join(cache, 'sources', identity.slice(0, 16));
  const marker = join(sources, 'prepared.json');
  const archives = {};
  for (const [name, item] of Object.entries(lock.sources)) archives[name] = await download(item, name);
  if (!existsSync(marker)) {
    await mkdir(dirname(sources), { recursive: true });
    const stage = await mkdtemp(join(dirname(sources), '.preparing-'));
    try {
      for (const [name, item] of Object.entries(lock.sources)) {
        const destination = join(stage, name);
        execFileSync(python, ['-B', support, 'extract', archives[name], destination, item.treeSHA256],
          { stdio: 'inherit', windowsHide: true });
        for (const [path, hash] of Object.entries(item.licenses))
          if (await fileHash(join(destination, path)) !== hash) throw new Error(`License changed: ${name}/${path}`);
      }
      for (const patch of lock.patches) {
        const destination = join(stage, patch.source);
        const files = new Map();
        for (const [path, hashes] of Object.entries(patch.files)) {
          if (await fileHash(join(destination, path)) !== hashes.upstream) throw new Error(`Upstream file changed: ${path}`);
          files.set(path, await readFile(join(destination, path), 'utf8'));
        }
        const outputs = applySourcePatch(files, await readFile(join(root, patch.path), 'utf8'));
        if (outputs.size !== files.size) throw new Error('Patch file inventory mismatch');
        for (const [path, text] of outputs) {
          if (sha256(text) !== patch.files[path].patched) throw new Error(`Patched file mismatch: ${path}`);
          await writeFile(join(destination, path), text);
        }
      }
      const header = await readFile(join(source, 'ffmpeg-private.h'));
      for (const folder of ['libavformat', 'libavutil'])
        await writeFile(join(stage, 'ffmpeg', folder, 'meowfilm-private.h'), header);
      for (const name of ['jinja', 'markupsafe', 'fast_float'])
        await cp(join(stage, name), join(stage, 'placebo/3rdparty', name), { recursive: true });
      const trees = {};
      for (const [name, item] of Object.entries(lock.sources)) {
        trees[name] = await treeHash(join(stage, name));
        if (trees[name] !== item.preparedTreeSHA256) throw new Error(`Prepared tree does not match lock: ${name}`);
      }
      await writeFile(join(stage, 'prepared.json'), JSON.stringify({ identity, trees }, null, 2) + '\n');
      // The caller holds the workspace OS lock; never overwrite existing prepared sources.
      await rename(stage, sources);
    } finally { await rm(stage, { recursive: true, force: true }); }
  }
  const prepared = JSON.parse(await readFile(marker, 'utf8'));
  if (prepared.identity !== identity) throw new Error('Invalid source preparation receipt');
  for (const name of Object.keys(lock.sources))
    if (prepared.trees[name] !== lock.sources[name].preparedTreeSHA256 ||
        await treeHash(join(sources, name)) !== lock.sources[name].preparedTreeSHA256)
      throw new Error(`Prepared source changed: ${name}`);
  return { lock, identity, prepared, sources, archives };
}

// Explicitly seal the worker's completed DOVI patch; ordinary preparation never repairs a lock.
export async function sealDoviPatch(python) {
  const lockPath = join(source, 'film-kernel.lock.json');
  const original = await readFile(lockPath);
  const lock = JSON.parse(original);
  const path = 'apps/windows/native/FilmKernel/ffmpeg-dovi.patch';
  const patch = await readFile(join(root, path), 'utf8');
  const archive = await download(lock.sources.ffmpeg, 'ffmpeg');
  await mkdir(cache, { recursive: true });
  const stage = await mkdtemp(join(cache, '.sealing-dovi-'));
  try {
    const destination = join(stage, 'ffmpeg');
    execFileSync(python, ['-B', support, 'extract', archive, destination, lock.sources.ffmpeg.treeSHA256],
      { stdio: 'inherit', windowsHide: true });
    for (const input of lock.patches.filter(item => item.source === 'ffmpeg' && item.path !== path)) {
      if (await fileHash(join(root, input.path)) !== lock.inputs[input.path])
        throw new Error('AVIO patch changed while sealing DOVI');
      const files = new Map();
      for (const [name, hashes] of Object.entries(input.files)) {
        const text = await readFile(join(destination, name), 'utf8');
        if (sha256(text) !== hashes.upstream) throw new Error(`Upstream patch input changed: ${name}`);
        files.set(name, text);
      }
      const outputs = applySourcePatch(files, await readFile(join(root, input.path), 'utf8'));
      for (const [name, text] of outputs) {
        if (sha256(text) !== input.files[name].patched) throw new Error(`AVIO output mismatch: ${name}`);
        await writeFile(join(destination, name), text);
      }
    }
    const files = new Map();
    for (const match of patch.matchAll(/^--- a\/(.+)$/gm))
      files.set(match[1], await readFile(join(destination, match[1]), 'utf8'));
    const outputs = applySourcePatch(files, patch);
    const hashes = {};
    for (const [name, text] of outputs) {
      hashes[name] = { upstream: sha256(files.get(name)), patched: sha256(text) };
      await writeFile(join(destination, name), text);
    }
    const header = await readFile(join(source, 'ffmpeg-private.h'));
    if (sha256(header) !== lock.inputs['apps/windows/native/FilmKernel/ffmpeg-private.h'])
      throw new Error('Private AVIO header changed while sealing DOVI');
    for (const folder of ['libavformat', 'libavutil'])
      await writeFile(join(destination, folder, 'meowfilm-private.h'), header);
    lock.sources.ffmpeg.preparedTreeSHA256 = await treeHash(destination);
    lock.inputs[path] = sha256(patch);
    lock.patches = [...lock.patches.filter(item => item.path !== path), { source: 'ffmpeg', path, files: hashes }];
    if (await fileHash(join(root, path)) !== lock.inputs[path] || !original.equals(await readFile(lockPath)))
      throw new Error('Patch or lock changed concurrently during DOVI sealing');
    const temporary = join(stage, 'film-kernel.lock.json');
    await writeFile(temporary, JSON.stringify(lock, null, 2) + '\n');
    await rename(temporary, lockPath);
    return { patchSHA256: lock.inputs[path], treeSHA256: lock.sources.ffmpeg.preparedTreeSHA256 };
  } finally { await rm(stage, { recursive: true, force: true }); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv[2] === '--locked') {
    const result = process.argv[3] === 'seal' ? await sealDoviPatch(process.argv[4]) :
      await prepareFilmKernel(process.argv[4]);
    console.log(JSON.stringify(result, null, 2));
  } else {
    const sealing = process.argv[2] === '--seal-dovi';
    const python = process.argv[sealing ? 3 : 2];
    if (!python) throw new Error('Usage: node prepare-film-kernel.mjs [--seal-dovi] <python-executable>');
    execFileSync(python, ['-B', support, 'lock-run', join(cache, 'build.lock'),
      process.execPath, fileURLToPath(import.meta.url), '--locked', sealing ? 'seal' : 'prepare', python],
    { stdio: 'inherit', windowsHide: true });
  }
}
