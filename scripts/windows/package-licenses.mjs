import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { tmpRoot } from './working-paths.mjs';

const MANIFEST = 'licenses-manifest.json';
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

// Explicit list of the reviewed files in LICENSES. No directory
// glob copying, inferred licensing or execution of application code.
const SOURCE = 'LICENSES';
export const licenseFiles = [
  'NOTICE.md', 'Apache-2.0.txt', 'GPL-3.0-only.txt', 'api-enhanced-MIT.txt', 'KuGouMusicApi-MIT.txt',
  'iwebplus-NOTICE.txt', 'meowmusic-NOTICE.txt', 'node-LICENSE.txt', 'qr-code-generator-LICENSE.txt',
  'quickjs-ng-LICENSE.txt', 'openssl-LICENSE.txt', 'zlib-LICENSE.txt',
  'Unicode-3.0.txt', 'catpawrunner-MIT.txt',
  'npm-node-compat-BSD-3-Clause.txt', 'npm-node-compat-ISC.txt', 'npm-node-compat-MIT.txt',
  'SkiaSharp-MIT.txt', 'SkiaSharp-THIRD-PARTY-NOTICES.txt',
  'GPL-2.0-only.txt', 'LGPL-2.1-only.txt', 'LGPL-3.0-only.txt',
  'FFmpeg-LICENSE.md', 'FFmpeg-IJG-NOTICE.txt', 'libxml2-MIT.txt',
  'FilmKernel-THIRD-PARTY-NOTICES.txt',
  'GCC-Runtime-Library-Exception-3.1.txt',
  'MinGW-w64-runtime-NOTICE.txt', 'winpthreads-LICENSE.txt',
  'WebView2-LICENSE.txt', 'WebView2-NOTICE.txt',
  'WindowsAppSDK-LICENSE.txt', 'WindowsAppSDK-NOTICE.txt',
  'Win2D-MIT.txt',
  'NAudio-MIT.txt', 'FFmpeg.AutoGen-MIT.txt', 'dotnet-runtime-MIT.txt', 'FFmpeg-audio-NOTICES.txt',
  'sqlite-LICENSE.md',
];
const INPUTS = licenseFiles.map(name => ({ sourcePath: `${SOURCE}/${name}`, path: name }));

function need(condition, code) {
  if (!condition) throw Object.assign(new Error(code), { code });
}
function inside(root, path) {
  const rel = relative(root, path);
  return rel !== '' && rel !== '..' && !rel.startsWith(`..${sep}`) && !isAbsolute(rel);
}

/**
 * Offline license copier for a controlled build tree. Copies the reviewed
 * LICENSES list into a fresh output directory and emits its SHA-256 manifest.
 */
export async function packageLicenses({
  root = fileURLToPath(new URL('../../', import.meta.url)), output, outputRoot = root,
} = {}) {
  need(typeof root === 'string' && isAbsolute(root), 'INVALID_ROOT');
  need(typeof outputRoot === 'string' && isAbsolute(outputRoot), 'INVALID_ROOT');
  need(typeof output === 'string' && isAbsolute(output), 'INVALID_OUTPUT');
  root = resolve(root);
  outputRoot = resolve(outputRoot);
  output = resolve(output);
  need(inside(outputRoot, output), 'OUTPUT_ESCAPE');
  need(existsSync(dirname(output)), 'OUTPUT_PARENT_MISSING');
  need(!existsSync(output), 'OUTPUT_EXISTS');

  const records = [...INPUTS].sort((a, b) => compare(a.path, b.path));
  const buffers = records.map(record => {
    const source = join(root, record.sourcePath);
    need(existsSync(source), 'MISSING_SOURCE');
    const bytes = readFileSync(source);
    need(bytes.length > 0, 'SOURCE_SIZE');
    return bytes;
  });

  mkdirSync(output);
  for (let i = 0; i < records.length; i++) {
    const target = join(output, records[i].path);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, buffers[i], { flag: 'wx', mode: 0o644 });
  }

  const manifest = {
    schema: 1, kind: 'licenses', status: 'complete', sourcePathBase: 'root', fileHashExcludes: [MANIFEST],
    files: records.map((file, i) => ({ ...file, bytes: buffers[i].length, sha256: hash(buffers[i]) })),
    redistributionPermissionVerified: false,
  };
  writeFileSync(join(output, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx', mode: 0o644 });
  return manifest;
}

async function main(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    const name = args[i];
    need(name === '--output' && args[i + 1] && !args[i + 1].startsWith('--') &&
      !Object.hasOwn(options, name.slice(2)), 'CLI_USAGE');
    options[name.slice(2)] = args[i + 1];
  }
  need(options.output, 'CLI_USAGE');
  const result = await packageLicenses({ ...options, outputRoot: tmpRoot });
  console.log(JSON.stringify({ status: result.status, files: result.files.length }));
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`License packaging failed: ${error.code ?? 'PACKAGE_FAILED'}`);
    process.exitCode = 1;
  });
}
