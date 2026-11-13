import { build } from 'esbuild';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, extname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(import.meta.url);
const sourceDirectory = resolve(root, 'packages/script-host/src');
const hostRequire = createRequire(resolve(sourceDirectory, 'source-native-runtime.js'));
const outputDirectory = resolve(root, 'packages/script-host/build/Release');

export async function buildSourceSDK({ directory = outputDirectory } = {}) {
  const captured = new Map();
  const readInput = async path => {
    const bytes = await readFile(resolve(root, path));
    captured.set(resolve(root, path), createHash('sha256').update(bytes).digest('hex'));
    return bytes;
  };
  await readInput(fileURLToPath(import.meta.url));
  const lockPath = 'package-lock.json';
  const lock = JSON.parse((await readInput(lockPath)).toString('utf8'));
  const versionFiles = new Set();
  async function checkDependency(path) {
    const parts = relative(root, path).split(sep);
    const index = parts.lastIndexOf('node_modules');
    if (index < 0) return;
    const length = parts[index + 1].startsWith('@') ? index + 3 : index + 2;
    const location = parts.slice(0, length).join('/');
    const versionFile = resolve(root, location, 'package.json');
    if (versionFiles.has(versionFile)) return;
    versionFiles.add(versionFile);
    const installed = JSON.parse((await readInput(versionFile)).toString('utf8'));
    const locked = lock.packages?.[location];
    if (!locked || locked.link || locked.version !== installed.version
        || locked.name !== undefined && locked.name !== installed.name) {
      throw new Error(`Source SDK dependency differs from package lock: ${location}`);
    }
  }
  await checkDependency(require.resolve('esbuild/package.json'));
  const domWorkers = new Set();
  const result = await build({
    absWorkingDir: root,
    entryPoints: [resolve(sourceDirectory, 'source-native-runtime.js')],
    bundle: true,
    platform: 'neutral',
    format: 'iife',
    globalName: '__sourceSDK',
    target: 'es2023',
    mainFields: ['module', 'main'],
    write: false,
    metafile: true,
    plugins: [{
      name: 'source-pure-dependencies',
      setup(plugin) {
        plugin.onResolve({ filter: /^(?:node:|stream$)/ }, input => {
          if (input.path === 'stream' && /[/\\]iconv-lite[/\\]/.test(input.importer)) {
            return { path: 'disabled-iconv-stream', namespace: 'source-sdk' };
          }
          throw new Error(`Node dependency in source SDK: ${input.path}`);
        });
        plugin.onResolve({ filter: /^(?:buffer|safer-buffer)$/ }, () => ({
          path: hostRequire.resolve('buffer/'),
        }));
        plugin.onResolve({ filter: /^string_decoder$/ }, () => ({
          path: hostRequire.resolve('string_decoder/'),
        }));
        plugin.onResolve({ filter: /^linkedom$/ }, input => {
          const worker = resolve(dirname(createRequire(input.importer).resolve('linkedom/package.json')), 'worker.js');
          domWorkers.add(worker);
          return { path: worker };
        });
        // Linkedom's optional require must remain absent in the Node-free SDK.
        // Installing jsdom for another module must not change this DOM implementation.
        plugin.onResolve({ filter: /^cssstyle$/ }, input => {
          if (!domWorkers.has(input.importer)) {
            throw new Error('Unexpected cssstyle import in source SDK');
          }
          return { path: input.path, external: true };
        });
        plugin.onLoad({ filter: /.*/, namespace: 'source-sdk' }, () => ({
          contents: 'module.exports = {};',
          loader: 'js',
        }));
        plugin.onLoad({ filter: /.*/, namespace: 'file' }, async input => {
          await checkDependency(input.path);
          const extension = extname(input.path);
          if (!['.js', '.cjs', '.mjs', '.json'].includes(extension)) {
            throw new Error(`Non-JavaScript source SDK input: ${input.path}`);
          }
          return {
            contents: await readInput(input.path),
            loader: extension === '.json' ? 'json' : 'js',
            resolveDir: dirname(input.path),
          };
        });
      },
    }],
  });
  const bundle = `(() => {\n${result.outputFiles[0].text}\nreturn __sourceSDK.runtime;\n})()`;
  const output = resolve(directory, 'source-sdk.js');
  const relativePath = path => {
    const result = relative(root, resolve(root, path)).split(sep).join('/');
    if (result.startsWith('../')) throw new Error('Source SDK input outside the workspace');
    return result;
  };
  const paths = [...new Set([
    ...Object.keys(result.metafile.inputs).filter(path => !path.startsWith('source-sdk:')),
    lockPath, ...versionFiles,
    fileURLToPath(import.meta.url),
  ].map(relativePath))].sort();
  const inputs = await Promise.all(paths.map(async path => {
    const sha256 = captured.get(resolve(root, path));
    const current = createHash('sha256').update(await readFile(resolve(root, path))).digest('hex');
    if (!sha256 || sha256 !== current) throw new Error(`Source SDK input changed during build: ${path}`);
    return { path, sha256 };
  }));
  await mkdir(directory, { recursive: true });
  await writeFile(output, bundle);
  return {
    path: relativePath(output),
    bytes: Buffer.byteLength(bundle),
    sha256: createHash('sha256').update(bundle).digest('hex'),
    inputs,
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = await buildSourceSDK();
  console.log(result.path);
}
