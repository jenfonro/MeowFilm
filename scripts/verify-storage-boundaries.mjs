import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const SOURCE_ROOTS = [
  'apps/runtime', 'apps/ios/Composition', 'apps/ios/Application', 'apps/ios/Navigation',
  'apps/ios/Presentation', 'apps/windows/src', 'native/MeowKit/Sources', 'packages',
];
const IGNORED = new Set(['node_modules', 'build', 'dist', '.build', '.cache']);
const OLD_DOCUMENT_NAMES = /(?:^|[\\/])(?:settings|library|accounts|private)\.json(?:$|['"`])/i;
const OLD_STORE_TYPES = /\b(?:IDocumentStore|LocalDocumentStore|CacheDocumentStore|EncryptedDocumentStore)\b/;
const JSON_WRITERS = /\b(?:writeFileSync|writeFile|appendFileSync|appendFile|createWriteStream)\s*\(/;
const DIRECT_PERSISTENCE_WRITERS = /\b(?:Data\([^\n]*\)\.write\(to:|File\.WriteAll(?:Text|Bytes)(?:Async)?\(|FileHandle\.write\(|createFile\()/;
const ALLOWED_WRITERS = new Set([
  'packages/script-host/src/private-volume.js',
  'apps/ios/Composition/MeowfilmUserDataReset.swift',
  'native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelSettingsPages.swift',
  'native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelManagementPage.swift',
  'native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelSourceEditor.swift',
  'native/MeowKit/Sources/MeowMusicFeature/Settings/SwiftUI/MusicAccountTransferState.swift',
  'apps/windows/src/MeowFilm.Capabilities/Novel/Presentation/NovelSettingsView.Rules.cs',
  'apps/windows/src/MeowFilm.Capabilities/Novel/Presentation/NovelSettingsView.cs',
]);
const COLUMN_WITH_DOCUMENT_PAYLOAD = /^\s*["']?([A-Za-z_][A-Za-z0-9_]*)["']?\s+(?:TEXT|BLOB|ANY)\b/i;
const FORBIDDEN_COLUMN = /(?:json|payload|document|serialized|raw_json)/i;

function filesUnder(root, directory) {
  const result = [];
  const walk = path => {
    for (const entry of readdirSync(path, { withFileTypes: true })) {
      if (IGNORED.has(entry.name) || ['test', 'tests', 'UITests', 'RuntimeTests'].includes(entry.name)) continue;
      const child = join(path, entry.name);
      if (entry.isDirectory()) walk(child);
      else if (/\.(?:[cm]?js|swift|cs)$/.test(entry.name)) result.push(child);
    }
  };
  if (existsSync(join(root, directory))) walk(join(root, directory));
  return result;
}

function productionFiles(root) {
  return SOURCE_ROOTS.flatMap(directory => filesUnder(root, directory));
}

function sqlFiles(root) {
  const result = [];
  const walk = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) walk(path);
      else if (entry.name.endsWith('.sql')) result.push(path);
    }
  };
  const directory = join(root, 'packages/storage/schema');
  if (existsSync(directory)) walk(directory);
  return result;
}

export function verifyStorageBoundaries(root) {
  const failures = [];
  for (const path of productionFiles(root)) {
    const name = relative(root, path).replaceAll('\\', '/');
    const text = readFileSync(path, 'utf8');
    if (OLD_STORE_TYPES.test(text)) failures.push(`${name}: legacy document store API`);
    if (OLD_DOCUMENT_NAMES.test(text)) failures.push(`${name}: internal JSON document path`);
    if ((JSON_WRITERS.test(text) || DIRECT_PERSISTENCE_WRITERS.test(text)) && !ALLOWED_WRITERS.has(name)) {
      failures.push(`${name}: direct file writer outside the approved external-file owners`);
    }
  }

  for (const path of sqlFiles(root)) {
    const name = relative(root, path).replaceAll('\\', '/');
    for (const [index, line] of readFileSync(path, 'utf8').split('\n').entries()) {
      const match = COLUMN_WITH_DOCUMENT_PAYLOAD.exec(line);
      if (match && FORBIDDEN_COLUMN.test(match[1]))
        failures.push(`${name}:${index + 1}: JSON/document payload column`);
    }
  }
  if (failures.length) throw new Error(failures.join('\n'));
  return { files: productionFiles(root).length };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(new URL('..', import.meta.url).pathname);
  const result = verifyStorageBoundaries(root);
  console.log(`Storage boundary checks passed for ${result.files} production files.`);
}
