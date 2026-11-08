import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { lstat, readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = fileURLToPath(new URL('../../', import.meta.url));
const policyPath = 'scripts/script-host-native.json';
const artifactPath = 'packages/script-host/build/Release/script_host_native.node';
const receiptPath = 'packages/script-host/build/Release/script-host-native.json';
const sdkPath = 'packages/script-host/build/Release/source-sdk.js';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

// A freshness check only. The common packager remains the authority for native
// trust, ABI, source inventory, licenses and emitted payloads.
export async function runtimeNativeNeedsBuild(root = repository) {
  try {
    const policy = JSON.parse(await readFile(resolve(root, policyPath), 'utf8'));
    const receipt = JSON.parse(await readFile(resolve(root, receiptPath), 'utf8'));
    if (receipt.schemaVersion !== 1 ||
        receipt.platform !== process.platform || receipt.arch !== process.arch ||
        receipt.abi !== policy.abi || receipt.napi !== policy.napi ||
        receipt.toolchain?.node !== process.versions.node ||
        receipt.toolchain?.nodeGyp !== policy.build.nodeGyp ||
        receipt.toolchain?.esbuild !== policy.build.esbuild ||
        !Array.isArray(receipt.inputs)) return true;
    const recorded = new Set(receipt.inputs.map(input => input.path));
    const required = [policyPath, 'package.json', 'package-lock.json',
      'scripts/quickjs/build-source-sdk.mjs', ...policy.mandatoryInputs,
      ...policy.patches.map(input => input.path), policy.nodeHeaders.archivePath,
      ...Object.values(policy.sources).flatMap(source => [source.archivePath, source.license.path])];
    async function inventory(directory) {
      for (const item of await readdir(resolve(root, directory), { withFileTypes: true })) {
        const path = `${directory}/${item.name}`;
        if (item.isDirectory()) await inventory(path);
        else if (item.isFile()) required.push(path);
        else throw new Error('Invalid native input');
      }
    }
    for (const directory of policy.inputDirectories) await inventory(directory);
    if (recorded.size !== receipt.inputs.length || required.some(path => !recorded.has(path))) return true;
    const inputs = [...receipt.inputs, receipt.artifact, receipt.sdk];
    if (receipt.artifact?.path !== artifactPath || receipt.sdk?.path !== sdkPath) return true;
    for (const input of inputs) {
      if (typeof input.path !== 'string' || input.path.split('/').some(part =>
        !part || part === '.' || part === '..' || /[\\:\u0000-\u001f]/.test(part))) return true;
      const path = resolve(root, input.path), stat = await lstat(path);
      if (!stat.isFile() || stat.isSymbolicLink() ||
          input.bytes !== undefined && input.bytes !== stat.size ||
          hash(await readFile(path)) !== input.sha256) return true;
    }
    return false;
  } catch { return true; }
}

export async function ensureRuntimeNative({ root = repository } = {}) {
  const policy = JSON.parse(await readFile(resolve(root, policyPath), 'utf8'));
  if (process.versions.node !== policy.build.node) {
    throw new Error(`Native runtime requires Node ${policy.build.node}; found ${process.versions.node}`);
  }
  if (!await runtimeNativeNeedsBuild(root)) return { built: false };
  await new Promise((resolveBuild, reject) => {
    const child = spawn(process.execPath, [resolve(root, 'scripts/quickjs/build-native.mjs')], {
      cwd: root, stdio: 'inherit', windowsHide: true,
    });
    child.once('error', reject);
    child.once('exit', (code, signal) => {
      if (code === 0) resolveBuild();
      else reject(Object.assign(new Error(`Native runtime build failed (${code === null ? `signal ${signal}` : `exit ${code}`})`),
        { exitCode: code ?? 1 }));
    });
  });
  // A successful command without a current receipt is not a prepared runtime.
  if (await runtimeNativeNeedsBuild(root)) throw new Error('Native runtime build produced no current receipt');
  return { built: true };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  ensureRuntimeNative().catch(error => {
    console.error(error.message);
    process.exitCode = error.exitCode ?? 1;
  });
}
