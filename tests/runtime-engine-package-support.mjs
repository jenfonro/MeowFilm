import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';

export const repository = fileURLToPath(new URL('../', import.meta.url));
export const policyPath = 'scripts/quickjs-runtime.json';
export const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
export const policy = readJSON(join(repository, policyPath));
const repositoryLock = readJSON(join(repository, 'package-lock.json'));
export const engine = policy.packages.find(pkg => pkg.name === '@jitl/quickjs-wasmfile-release-sync');
export const asmjs = policy.packages.find(pkg => pkg.name === '@jitl/quickjs-asmjs-mjs-release-sync');
export const wasmPath = `node_modules/${engine.name}/${engine.wasm.path}`;
export const taskContext = policy.taskContext;
export const localLoader = taskContext.artifacts.find(artifact => artifact.path.endsWith('.mjs'));
export const localWasm = taskContext.artifacts.find(artifact => artifact.path.endsWith('.wasm'));
const payloadArtifactPath = artifact =>
  artifact.path.replace('packages/script-host/', 'node_modules/@meowfilm/script-host/');
export const localWasmPath = payloadArtifactPath(localWasm);
export const localLoaderPath = payloadArtifactPath(localLoader);
export const wasmPaths = [wasmPath, localWasmPath].sort();
export const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export const inert = 'throw new Error("Synthetic workspace code must never execute");\n';

export function fixture(t, profile) {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'runtime-engine-package-'));
  t.after(() => {
    assertOwnedPath(temporaryRoot, area);
    rmSync(area, { recursive: true });
  });
  const root = join(area, 'source');
  const write = (path, value) => {
    const target = assertOwnedPath(area, join(root, path), { allowMissing: true });
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, typeof value === 'string' || Buffer.isBuffer(value)
      ? value : `${JSON.stringify(value, null, 2)}\n`);
  };
  const manifest = { name: 'runtime-engine-fixture', version: '1.0.0', private: true, workspaces: ['packages/*'] };
  const lock = { name: manifest.name, version: manifest.version, lockfileVersion: 3, packages: { '': { ...manifest } } };
  write('package.json', manifest);
  for (const name of ['runtime-bridge', 'script-host', 'source-runtime']) {
    const location = `packages/${name}`, installed = `node_modules/@meowfilm/${name}`;
    const pkg = { name: `@meowfilm/${name}`, version: '1.0.0', type: 'module', license: 'UNLICENSED',
      exports: './src/index.js', files: ['src'],
      ...(name === 'script-host' ? { dependencies: {
        '@meowfilm/source-runtime': '1.0.0',
        'quickjs-emscripten-core': '0.32.0', [engine.name]: '0.32.0', [asmjs.name]: '0.32.0',
      } } : {}),
    };
    write(`${location}/package.json`, pkg);
    write(`${location}/src/index.js`, inert);
    lock.packages[location] = { version: pkg.version, ...(pkg.dependencies ? { dependencies: pkg.dependencies } : {}) };
    lock.packages[installed] = { resolved: location, link: true };
    mkdirSync(dirname(join(root, installed)), { recursive: true });
    symlinkSync(join(root, location), join(root, installed), process.platform === 'win32' ? 'junction' : 'dir');
  }
  // Both backends are real installed dependencies, as in script-host's manifest.
  // Profile selection must happen in packaging, never in this fixture's lock.
  // Real engine files, never a dummy WASM/asmjs or invented registry SRI.
  // Only these controlled fixture copies are altered by rejection tests.
  for (const pkg of policy.packages) {
    const location = `node_modules/${pkg.name}`;
    mkdirSync(dirname(join(root, location)), { recursive: true });
    cpSync(join(repository, location), join(root, location), { recursive: true });
    lock.packages[location] = structuredClone(repositoryLock.packages[location]);
  }
  write(policyPath, readFileSync(join(repository, policyPath)));
  write(policy.license.path, readFileSync(join(repository, policy.license.path)));
  for (const input of [taskContext.patch, taskContext.build, ...taskContext.artifacts])
    write(input.path, readFileSync(join(repository, input.path)));
  write('apps/runtime/bootstrap.mjs', inert);
  write('apps/runtime/modules/music-script.mjs', inert);
  write('apps/runtime/modules/cat-script.mjs', inert);
  write('apps/runtime/modules/source-runtime.mjs', inert);
  const saveLock = () => write('package-lock.json', lock);
  saveLock();
  const editManifest = (location, changes) =>
    write(`${location}/package.json`, { ...readJSON(join(root, location, 'package.json')), ...changes });
  const output = join(area, 'payload');
  return { root, area, output, lock, write, saveLock, editManifest,
    pack: () => packageRuntime({ root, product: 'aggregate', output, profile }) };
}
