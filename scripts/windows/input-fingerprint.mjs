import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';
import { isWindowsSource } from './source-scope.mjs';
import { licenseFiles } from './package-licenses.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));

// Include the dependency graph, packaging/test scripts, build configuration and
// shipped assets/licenses, not only language extensions. Ordinary Markdown is
// evidence, but shipped legal notices remain byte-bound build inputs.
export function isWindowsBuildInput(path) {
  const legalNotice = /(?:^|\/)(?:notice|licen[cs]e|copying|copyright|third[-_]?party)(?:[._-][^/]*)?$/i.test(path);
  return (!/\.md$/i.test(path) || legalNotice) && (
    ['package.json', 'package-lock.json', '.gitattributes', '.gitignore', 'build-windows.ps1'].includes(path)
    || isWindowsSource(path)
    || path.startsWith('native/JavaScript/')
    || licenseFiles.some(name => path === `LICENSES/${name}`)
    || path === 'scripts/script-host-native.json'
    || (path.startsWith('tests/') && !path.startsWith('tests/ios-'))
  );
}

export function fingerprintInputs(directory, inventory) {
  const files = [...new Set(inventory.filter(Boolean))].filter(isWindowsBuildInput).sort();
  if (!files.length) throw new Error('No Windows code inputs found');
  const hash = createHash('sha256');
  for (const path of files)
    hash.update(path).update('\0').update(readFileSync(join(directory, path))).update('\0');
  return hash.digest('hex');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const inventory = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'],
    { cwd: root, windowsHide: true, encoding: 'utf8' }).split('\0').filter(path => path && existsSync(join(root, path)));
  console.log(fingerprintInputs(root, inventory));
}
