import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, parse, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { licenseFiles } from './package-licenses.mjs';

const inventoryName = 'meowfilm-install-files.txt';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
function need(condition, message) {
  if (!condition) throw new Error(`Windows installer: ${message}`);
}
function ordinaryAncestors(path) {
  for (let current = resolve(path); ; current = dirname(current)) {
    need(!lstatSync(current).isSymbolicLink(), 'payload/output cannot traverse a link or junction');
    if (current === parse(current).root) break;
  }
}
function safeRelative(path) {
  need(typeof path === 'string' && path.split('/').every(part =>
    part && part !== '.' && part !== '..' &&
    !/[\\:\x00-\x1f\x7f<>"|?*{}[\];]/u.test(part) && !/[. ]$/u.test(part) &&
    !/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/iu.test(part)),
  'unsafe payload path');
  return path;
}
function inventory(root, prefix = '', names = new Set()) {
  const result = [];
  for (const entry of readdirSync(join(root, prefix), { withFileTypes: true })) {
    const path = safeRelative(prefix ? `${prefix}/${entry.name}` : entry.name);
    const key = path.normalize('NFC').toLowerCase();
    need(!names.has(key), 'case/Unicode-colliding payload paths');
    names.add(key);
    const info = lstatSync(join(root, path));
    need(!info.isSymbolicLink() && (info.isFile() || info.isDirectory()), 'payload contains a link or special file');
    if (info.isDirectory()) result.push(...inventory(root, path, names));
    else {
      need(info.nlink === 1, 'payload contains a hard link');
      result.push(path);
    }
  }
  return result.sort();
}
function json(path) {
  return JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/u, ''));
}
function verifyManifest(root, name, actual) {
  const manifest = json(join(root, name));
  need(manifest.status === 'complete' && Array.isArray(manifest.files), `${name} is incomplete`);
  const records = new Set();
  for (const record of manifest.files) {
    const path = safeRelative(record.path);
    need(!records.has(path), `${name} contains duplicate files`);
    records.add(path);
    need(actual.includes(path), `${name} lists a missing file: ${path}`);
    const bytes = readFileSync(join(root, path));
    need(bytes.length === record.bytes && digest(bytes) === record.sha256, `${name} file hash differs: ${path}`);
  }
  need(actual.length === records.size + 1 && actual.every(path => path === name || records.has(path)),
    `${name} does not describe the exact inventory`);
  return manifest;
}

// This validates build content, not acceptance. A build-result status is never
// evidence that the application was installed, launched or accepted by a user.
export function prepareWindowsInstaller({ payload, output }) {
  need(isAbsolute(payload ?? '') && isAbsolute(output ?? ''), 'absolute payload and output directories are required');
  payload = resolve(payload); output = resolve(output);
  need(output !== payload && !output.startsWith(payload + '/') && !output.startsWith(payload + '\\'),
    'installer staging must be outside the payload');
  ordinaryAncestors(payload); ordinaryAncestors(output);
  const files = inventory(payload);
  need(!files.some(path => /^(?:unins\d+\.(?:exe|dat|msg)|meowfilm-install-files\.txt)$/iu.test(path)),
    'payload contains installer-owned metadata');
  const required = [
    'meowfilm.exe', 'meowfilm.dll', 'meowfilm.deps.json', 'meowfilm.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
    'runtime/node.exe', 'runtime/node-LICENSE.txt', 'runtime/runtime-manifest.json',
    'licenses/licenses-manifest.json', 'build-result.json',
  ];
  for (const path of required) {
    need(files.includes(path) && lstatSync(join(payload, path)).size > 0, `publish closure is missing ${path}`);
  }
  const result = json(join(payload, 'build-result.json'));
  need(result.application === 'meowfilm' && result.configuration === 'Release' &&
    result.selfContained === true && result.runtimeIdentifier === 'win-x64',
  'installers require a Release self-contained win-x64 publish');
  const config = json(join(payload, 'meowfilm.runtimeconfig.json')).runtimeOptions;
  const frameworks = config?.includedFrameworks;
  need(!config?.frameworks && !config?.framework && Array.isArray(frameworks) &&
    ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'].every(name => frameworks.some(item => item.name === name)) &&
    frameworks.every(item => item.version && item.version === frameworks[0].version),
  'self-contained desktop framework closure is incomplete');
  const deps = json(join(payload, 'meowfilm.deps.json'));
  const targetName = deps.runtimeTarget?.name;
  need(targetName?.endsWith('/win-x64') && deps.targets?.[targetName], 'publish dependency target must be win-x64');
  for (const library of Object.values(deps.targets[targetName])) {
    for (const group of ['runtime', 'native', 'resources']) {
      for (const [asset, properties] of Object.entries(library[group] ?? {})) {
        if (asset === '_._' || asset.endsWith('/_._')) continue;
        const name = asset.split('/').at(-1);
        const destination = group === 'resources' ? `${properties.locale}/${name}` : name;
        need(files.includes(asset) || files.includes(destination), `publish dependency is missing ${asset}`);
      }
    }
  }
  const runtimeFiles = files.filter(path => path.startsWith('runtime/')).map(path => path.slice(8));
  const runtime = verifyManifest(join(payload, 'runtime'), 'runtime-manifest.json', runtimeFiles);
  need(runtime.schemaVersion === 1 && runtime.product === 'aggregate' && runtime.platform === 'win32' &&
    runtime.transport === 'windows-http-v1' && runtime.assemblyProfile === 'common-native' &&
    Array.isArray(runtime.modules) && runtime.modules.length === 5 &&
    ['music', 'cat', 'source', 'tmdb', 'netease'].every(name => runtime.modules.includes(name)) &&
    runtimeFiles.includes(runtime.entry) && runtime.node?.sha256 && runtime.node?.licenseSha256,
  'runtime manifest does not describe the Windows application');
  for (const [path, hash] of [['node.exe', runtime.node.sha256], ['node-LICENSE.txt', runtime.node.licenseSha256]]) {
    need(digest(readFileSync(join(payload, 'runtime', path))) === hash, `bundled ${path} differs from its Node lock`);
  }
  const licenses = verifyManifest(join(payload, 'licenses'), 'licenses-manifest.json',
    files.filter(path => path.startsWith('licenses/')).map(path => path.slice(9)));
  need(licenses.files.length === licenseFiles.length &&
    licenseFiles.every(path => licenses.files.some(file => file.path === path)),
  'license closure is incomplete');
  const lines = files.map(path => {
    const windowsPath = path.replaceAll('/', '\\');
    const parent = windowsPath.includes('\\') ? `\\${windowsPath.slice(0, windowsPath.lastIndexOf('\\'))}` : '';
    return `Source: "{#PayloadDir}\\${windowsPath}"; DestDir: "{app}${parent}"; Flags: ignoreversion`;
  });
  writeFileSync(join(output, 'payload-files.iss'), `${lines.join('\n')}\n`, { flag: 'wx', encoding: 'utf8' });
  writeFileSync(join(output, inventoryName),
    `meowfilm-installer-files-v1\n${[...files, inventoryName].join('\n')}\n`, { flag: 'wx', encoding: 'utf8' });
  return { files: files.length, inventory: inventoryName };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    need(args.length === 4 && args[0] === '--payload' && args[2] === '--output',
      'usage: --payload <complete publish> --output <installer staging>');
    console.log(JSON.stringify(prepareWindowsInstaller({ payload: args[1], output: args[3] })));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
