import { build } from 'esbuild';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, extname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(import.meta.url);
const sourceDirectory = resolve(root, 'packages/script-host/src');
const outputDirectory = resolve(root, 'packages/script-host/build/Release');

export async function buildSourceSDK() {
  const captured = new Map();
  const readInput = async path => {
    const bytes = await readFile(path);
    captured.set(resolve(root, path), createHash('sha256').update(bytes).digest('hex'));
    return bytes;
  };
  await readInput(fileURLToPath(import.meta.url));
  const versionFiles = [];
  for (const [name, version] of [
    ['esbuild', '0.28.1'], ['iconv-lite', '0.6.3'], ['buffer', '6.0.3'], ['whatwg-url', '14.2.0'],
    ['string_decoder', '1.3.0'], ['safe-buffer', '5.2.1'],
  ]) {
    const versionFile = require.resolve(`${name}/package.json`);
    const installed = JSON.parse((await readInput(versionFile)).toString('utf8'));
    if (installed.version !== version) throw new Error(`Unexpected source SDK dependency: ${name}`);
    versionFiles.push(versionFile);
  }
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
          path: require.resolve('buffer/'),
        }));
        plugin.onResolve({ filter: /^string_decoder$/ }, () => ({
          path: require.resolve('string_decoder/'),
        }));
        plugin.onResolve({ filter: /^linkedom$/ }, () => ({
          path: resolve(dirname(require.resolve('linkedom/package.json')), 'worker.js'),
        }));
        plugin.onLoad({ filter: /.*/, namespace: 'source-sdk' }, () => ({
          contents: 'module.exports = {};',
          loader: 'js',
        }));
        plugin.onLoad({ filter: /.*/, namespace: 'file' }, async input => {
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
  const output = resolve(outputDirectory, 'source-sdk.js');
  const relativePath = path => {
    const result = relative(root, resolve(root, path)).split(sep).join('/');
    if (result.startsWith('../')) throw new Error('Source SDK input outside the workspace');
    return result;
  };
  const paths = [...new Set([
    ...Object.keys(result.metafile.inputs).filter(path => !path.startsWith('source-sdk:')),
    ...versionFiles,
    fileURLToPath(import.meta.url),
  ].map(relativePath))].sort();
  const inputs = await Promise.all(paths.map(async path => {
    const sha256 = captured.get(resolve(root, path));
    const current = createHash('sha256').update(await readFile(resolve(root, path))).digest('hex');
    if (!sha256 || sha256 !== current) throw new Error(`Source SDK input changed during build: ${path}`);
    return { path, sha256 };
  }));
  await mkdir(outputDirectory, { recursive: true });
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
