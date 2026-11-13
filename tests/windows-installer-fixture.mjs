import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { prepareWindowsInstaller } from '../scripts/windows/prepare-installer.mjs';
import { licenseFiles } from '../scripts/windows/package-licenses.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const nodeVersion = JSON.parse(readFileSync(new URL('../scripts/script-host-native.json', import.meta.url), 'utf8')).build.node;

// Synthetic publish content only. No application is run, and no user's data
// directory is accepted, read or changed by these packaging fixtures.
export function installerFixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'installer payload-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, root), { recursive: true, force: true }));
  const payload = join(root, 'tmp/publish'), output = join(root, 'tmp/installer');
  mkdirSync(payload, { recursive: true }); mkdirSync(output);
  const write = (path, content = path) => {
    mkdirSync(dirname(join(payload, path)), { recursive: true });
    writeFileSync(join(payload, path), typeof content === 'object' ? JSON.stringify(content) : content);
  };
  for (const file of ['meowfilm.exe', 'meowfilm.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'PresentationFramework.dll', 'Example.dll', 'zh-Hans/Example.resources.dll', 'libvlc.dll']) write(file);
  write('meowfilm.runtimeconfig.json', { runtimeOptions: {
    includedFrameworks: ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'].map(name => ({ name, version: '10.0.0' })),
  } });
  write('meowfilm.deps.json', { runtimeTarget: { name: 'net10.0/win-x64' }, targets: {
    'net10.0/win-x64': { 'Example/1': {
      runtime: { 'lib/net10.0/Example.dll': {} },
      native: { 'runtimes/win-x64/native/libvlc.dll': {} },
      resources: { 'lib/net10.0/zh-Hans/Example.resources.dll': { locale: 'zh-Hans' } },
    } },
  } });
  write('build-result.json', { application: 'meowfilm', configuration: 'Release', selfContained: true,
    runtimeIdentifier: 'win-x64', status: 'built-not-accepted' });
  const runtimeFiles = ['node.exe', 'node-LICENSE.txt', 'app/windows/entries/aggregate.mjs', 'native/engine.node'];
  for (const file of runtimeFiles) write(`runtime/${file}`);
  const records = (prefix, files) => files.map(path => {
    const bytes = readFileSync(join(payload, prefix, path));
    return { path, bytes: bytes.length, sha256: hash(bytes) };
  });
  write('runtime/runtime-manifest.json', {
    schemaVersion: 1, status: 'complete', product: 'aggregate', platform: 'win32', transport: 'windows-http-v1',
    modules: ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease'],
    assemblyProfile: 'common-native', entry: 'app/windows/entries/aggregate.mjs', files: records('runtime', runtimeFiles),
    node: { version: nodeVersion, sha256: hash('runtime/node.exe'), licenseSha256: hash('runtime/node-LICENSE.txt') },
  });
  for (const file of licenseFiles) write(`licenses/${file}`);
  write('licenses/licenses-manifest.json', { status: 'complete', files: records('licenses', licenseFiles) });
  const change = (path, action) => {
    const current = JSON.parse(readFileSync(join(payload, path), 'utf8'));
    action(current);
    write(path, current);
  };
  return { root, payload, output, write, change, prepare: () => prepareWindowsInstaller({ payload, output }) };
}
