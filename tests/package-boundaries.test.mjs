import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { verifyBoundaries } from '../scripts/verify-boundaries.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'package-boundaries-'));
  t.after(() => { assertOwnedPath(temporaryRoot, root); rmSync(root, { recursive: true }); });
  function write(path, content) {
    const file = assertOwnedPath(root, join(root, path), { allowMissing: true });
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, typeof content === 'string' ? content : JSON.stringify(content));
  }
  write('package.json', { workspaces: ['packages/*'] });
  write('packages/a/package.json', {
    name: '@meowfilm/a', dependencies: { '@meowfilm/b': '*' }, exports: { '.': './src/index.js' },
  });
  write('packages/b/package.json', { name: '@meowfilm/b', exports: { '.': './src/index.js' } });
  write('packages/a/src/index.js', "import '@meowfilm/b';");
  write('packages/b/src/index.js', 'export {};');
  function workspace(name, entries = { '.': './src/index.js' }, dependencies = {}) {
    const folder = name.slice(name.lastIndexOf('/') + 1);
    write(`packages/${folder}/package.json`, { name, dependencies, exports: entries });
    for (const target of Object.values(entries)) write(`packages/${folder}/${target.slice(2)}`, 'export {};');
  }
  return { root, write, workspace };
}

test('workspace declarations permit explicit cross-package dependencies', t => {
  const f = fixture(t);
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
});

test('workspace imports require a dependency declaration', t => {
  const f = fixture(t);
  f.write('packages/a/package.json', { name: '@meowfilm/a' });
  assert.throws(() => verifyBoundaries(f.root), /undeclared workspace dependency/);
});

test('relative imports cannot bypass another package interface', t => {
  const f = fixture(t);
  f.write('packages/a/src/index.js', "import '../../b/src/index.js';");
  assert.throws(() => verifyBoundaries(f.root), /escapes package boundary/);
});

test('workspace dependency cycles are reported', t => {
  const f = fixture(t);
  f.write('packages/b/package.json', {
    name: '@meowfilm/b', dependencies: { '@meowfilm/a': '*' }, exports: { '.': './src/index.js' },
  });
  assert.throws(() => verifyBoundaries(f.root), /Dependency cycle/);
});

for (const [source, error] of [
  ["import 'node:fs';", /portable runtime imports Node builtin/],
  ['Buffer.alloc(1);', /portable runtime uses host global Buffer/],
  ['globalThis.process.exit(1);', /portable runtime uses host global process/],
  ['Packages.java.lang.String;', /portable runtime uses host global Packages/],
]) {
  test(`portable source runtime rejects host dependency: ${source}`, t => {
    const f = fixture(t);
    f.write('packages/source-runtime/package.json', {
      name: '@meowfilm/source-runtime',
      dependencies: { acorn: '8.19.0' },
      exports: { '.': './src/index.js' },
    });
    f.write('packages/source-runtime/src/index.js', source);
    assert.throws(() => verifyBoundaries(f.root), error);
  });
}

test('portable source runtime may inspect guest source strings without using host globals', t => {
  const f = fixture(t);
  f.write('packages/source-runtime/package.json', {
    name: '@meowfilm/source-runtime', exports: { '.': './src/index.js' },
  });
  f.write('packages/source-runtime/src/index.js',
    "export const rejected = /\\b(?:process|Deno|Bun)\\s*\\./.test('guest');");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 3 });
});

test('shared runtime may use its own modules and data-only builtins', t => {
  const f = fixture(t);
  f.write('apps/runtime/bootstrap.mjs', "import './modules/music-script.mjs';");
  f.write('apps/runtime/modules/music-script.mjs', "import { createHash } from 'node:crypto';");
  f.write('apps/runtime/modules/source-candidates.mjs', "import { isUtf8 } from 'node:buffer';");
  f.write('apps/runtime/modules/source-library.mjs', "import { Readable } from 'node:stream';");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
});

test('metadata helpers may use neutral HTTP imports and public workspace capabilities', t => {
  const f = fixture(t);
  f.workspace('@meowfilm/script-host');
  f.write('apps/runtime/modules/metadata-data.mjs', `
    import './metadata/settings.mjs';
    import './metadata/network.mjs';
    import './metadata/tmdb.mjs';
    import './metadata/douban.mjs';
  `);
  f.write('apps/runtime/modules/metadata/settings.mjs', "import { PrivateDocuments } from '@meowfilm/script-host';");
  f.write('apps/runtime/modules/metadata/network.mjs', `
    import http from 'node:http';
    import https from 'node:https';
    import { createNodeNetwork } from '@meowfilm/script-host';
    import './settings.mjs';
  `);
  f.write('apps/runtime/modules/metadata/tmdb.mjs', 'export {};');
  f.write('apps/runtime/modules/metadata/douban.mjs', "import { isUtf8 } from 'node:buffer';");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 3 });
});

test('shared runtime accepts a declared public workspace subpath without a per-path allowlist', t => {
  const f = fixture(t);
  f.workspace('@meowfilm/film-playback', {
    '.': './src/index.js', './metadata-query': './src/metadata-query.js',
  });
  f.write('apps/runtime/modules/metadata/tmdb.mjs',
    "import { tmdbQuery } from '@meowfilm/film-playback/metadata-query';");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 3 });
  f.write('apps/runtime/modules/metadata/tmdb.mjs',
    "import '@meowfilm/film-playback/src/index.js';");
  assert.throws(() => verifyBoundaries(f.root), /shared runtime platform\/private import/);
});

test('comments and string literals are not treated as module imports', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/metadata/settings.mjs', `
    // import 'node:fs';
    const example = "require('node:child_process')";
    function require(condition, message) {
      if (!condition) throw new Error(message);
    }
    require(false, "not an import");
    const method = { import() {} };
    method.import();
  `);
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
});

test('literal dynamic imports and require calls are checked as real dependencies', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/metadata/settings.mjs', `
    import('node:crypto');
    require('node:crypto');
    require.resolve('node:buffer');
  `);
  assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
  f.write('apps/runtime/modules/metadata/settings.mjs', 'import(moduleName);');
  assert.throws(() => verifyBoundaries(f.root), /statically resolvable/);
});

test('metadata settings cannot use an undeclared public workspace', t => {
  const f = fixture(t);
  f.write('apps/runtime/modules/metadata/settings.mjs', "import '@meowfilm/script-host';");
  assert.throws(() => verifyBoundaries(f.root), /shared runtime platform\/private import/);
});

test('film assembly consumes its declared public business owner', t => {
  const f = fixture(t);
  f.workspace('@meowfilm/film-playback');
  f.write('apps/runtime/modules/film-playback.mjs',
    "import { createPlaybackService } from '@meowfilm/film-playback';");
  assert.deepEqual(verifyBoundaries(f.root), { packages: 3 });
});

test('source rules use only the declared pure program and public native JSON entry', t => {
  const f = fixture(t);
  f.workspace('@meowfilm/novel-rules');
  f.workspace('@meowfilm/script-host', {
    '.': './src/index.js', './native-json': './src/native-json.js',
  });
  f.write('apps/runtime/modules/source-rules.mjs', `
    import '@meowfilm/novel-rules';
    import { NativeJsonProgram } from '@meowfilm/script-host/native-json';
  `);
  assert.deepEqual(verifyBoundaries(f.root), { packages: 4 });
  f.write('apps/runtime/modules/source-rules.mjs', "import '@meowfilm/script-host/src/native-engine.js';");
  assert.throws(() => verifyBoundaries(f.root), /shared runtime platform\/private import/);
});

for (const owner of ['packages/a/src/index.js', 'apps/runtime/modules/cat-script.mjs']) {
  test(`${owner} can contain import vocabulary without declaring a dependency`, t => {
    const f = fixture(t);
    f.write(owner, `
      // import 'node:child_process';
      const source = "require('node:fs')";
      const pattern = /from\\s*'node:net'/;
      const text = \`export { value } from 'node:net';\`;
      function method() {}
      method('import', ['installationId', 'name'], () => {});
      const object = { import() {} };
      object.import('node:net');
    `);
    assert.deepEqual(verifyBoundaries(f.root), { packages: 2 });
  });
}

for (const platform of ['windows', 'ios']) {
  for (const [owner, prefix] of [['modules/music-script.mjs', '../..'], ['modules/metadata/network.mjs', '../../..']]) {
    for (const source of [
      `import '${prefix}/${platform}/runtime/bootstrap.mjs';`,
      `export * from '${prefix}/${platform}/runtime/bootstrap.mjs';`,
      `export { value } from '${prefix}/${platform}/runtime/bootstrap.mjs';`,
      `const runtime = import('${prefix}/${platform}/runtime/bootstrap.mjs');`,
      `const runtime = require('${prefix}/${platform}/runtime/bootstrap.mjs');`,
    ]) {
      test(`shared runtime ${owner} rejects ${platform} source dependency: ${source}`, t => {
        const f = fixture(t);
        f.write(`apps/runtime/${owner}`, source);
        assert.throws(() => verifyBoundaries(f.root), /shared runtime depends on platform source/);
      });
    }
  }
}

for (const owner of ['modules/music-script.mjs', 'modules/metadata/network.mjs']) {
  for (const specifier of ['node:net', 'node:fs', 'node:child_process', '@meowfilm/script-host/src/host.js']) {
    test(`shared runtime ${owner} rejects platform/private import: ${specifier}`, t => {
      const f = fixture(t);
      f.write(`apps/runtime/${owner}`, `import '${specifier}';`);
      assert.throws(() => verifyBoundaries(f.root), /shared runtime platform\/private import/);
    });
  }
}
