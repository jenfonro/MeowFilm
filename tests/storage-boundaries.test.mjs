import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verifyStorageBoundaries } from '../scripts/verify-storage-boundaries.mjs';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-storage-boundary-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return {
    root,
    source(path, text) {
      const file = join(root, path);
      mkdirSync(join(file, '..'), { recursive: true });
      writeFileSync(file, text);
    },
  };
}

test('internal JSON file writers and opaque storage APIs fail the source boundary', t => {
  const f = fixture(t);
  f.source('apps/runtime/modules/owner.mjs', 'writeFile(path, JSON.stringify(value));');
  assert.throws(() => verifyStorageBoundaries(f.root), /direct file writer/);
});

test('third-party private-volume and explicit user-export writers remain permitted', t => {
  const f = fixture(t);
  f.source('packages/script-host/src/private-volume.js', 'api.writeFileSync(path, bytes);');
  f.source('native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelSourceEditor.swift', 'Data(text.utf8).write(to: url)');
  f.source('apps/windows/src/MeowFilm.Capabilities/Novel/Presentation/NovelSettingsView.cs',
    'File.WriteAllText(dialog.FileName, editor.Draft.Definition());');
  assert.doesNotThrow(() => verifyStorageBoundaries(f.root));
});

test('whole-document JSON payload columns fail while typed relation columns pass', t => {
  const f = fixture(t);
  f.source('packages/storage/schema/example.sql', '    json_payload TEXT NOT NULL,');
  assert.throws(() => verifyStorageBoundaries(f.root), /JSON\/document payload column/);
});
