import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { checkTestSummary } from '../scripts/run-tests.mjs';
import { assertOwnedPath, temporaryRoot, windowsRoot } from '../scripts/windows/working-paths.mjs';

const good = { tests: 5, pass: 5, fail: 0, cancelled: 0, skipped: 0, todo: 0 };
const tap = values => Object.entries(values).map(([key, value]) => `# ${key} ${value}`).join('\n');
test('gate admits only a nonempty completely passing test summary', () => {
  assert.equal(checkTestSummary(tap(good), 'fixture'), 5);
  assert.equal(checkTestSummary(tap(good).replaceAll('\n', '\r\n'), 'fixture'), 5);
});
test('missing empty skipped cancelled and todo suites fail explicitly', () => {
  for (const values of [{}, { ...good, tests: 0, pass: 0 }, { ...good, pass: 4 },
    ...['fail', 'cancelled', 'skipped', 'todo'].map(key => ({ ...good, [key]: 1 }))]) {
    assert.throws(() => checkTestSummary(tap(values), 'fixture'));
  }
  for (const field of Object.keys(good)) {
    const values = { ...good }; delete values[field];
    assert.throws(() => checkTestSummary(tap(values), 'fixture'));
  }
});

function discoveryFixture(t, script = 'test-root') {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'test-gate-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, area), { recursive: true, force: true }));
  const root = join(area, 'repository');
  const write = (path, text) => {
    const target = join(root, path);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, text);
  };
  for (const path of [
    `scripts/${script}.mjs`, 'scripts/run-tests.mjs', 'scripts/verify-dependency-lock.mjs',
    'scripts/windows/working-paths.mjs',
  ]) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    copyFileSync(join(windowsRoot, path), join(root, path));
  }
  mkdirSync(join(root, 'tests'));
  // Support files must not be imported or counted as tests.
  write('tests/support.mjs', "throw new Error('SUPPORT_FILE_EXECUTED');\n");
  const env = { ...process.env, NODE_OPTIONS: '', NODE_PATH: '', FORCE_COLOR: '0' };
  // The fixture launches an independent CLI, not a recursive node:test run.
  delete env.NODE_TEST_CONTEXT;
  return {
    write,
    add(name, body = "test('passing fixture', () => {});", directory = 'tests') {
      write(`${directory}/${name}.test.mjs`, `
        import test, { describe } from 'node:test';
        import assert from 'node:assert/strict';
        import { writeFileSync } from 'node:fs';
        writeFileSync(new URL('./${name}.ran', import.meta.url), 'executed');
        ${body}
      `);
    },
    executed: (name, directory = 'tests') => existsSync(join(root, directory, `${name}.ran`)),
    run: (...args) => spawnSync(process.execPath, [join(root, `scripts/${script}.mjs`), ...args], {
      cwd: area, encoding: 'utf8', windowsHide: true, timeout: 20000, env,
    }),
  };
}

for (const platform of ['windows', 'ios']) {
  test(`${platform} discovery executes shared and own tests without loading the other platform`, t => {
    const f = discoveryFixture(t), other = platform === 'windows' ? 'ios' : 'windows';
    for (const name of ['shared', `${platform}-runtime`]) f.add(name);
    f.add(`${other}-runtime`, "throw new Error('OTHER_PLATFORM_EXECUTED');");
    const result = f.run('--platform', platform);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.equal(checkTestSummary(result.stdout, platform), 2);
    assert.equal(f.executed('shared'), true);
    assert.equal(f.executed(`${platform}-runtime`), true);
    assert.equal(f.executed(`${other}-runtime`), false);
  });

  test(`${platform} discovery rejects an empty selection even when the other platform has tests`, t => {
    const f = discoveryFixture(t), other = platform === 'windows' ? 'ios' : 'windows';
    f.add(`${other}-runtime`);
    const result = f.run('--platform', platform);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /no test files discovered/);
    assert.equal(f.executed(`${other}-runtime`), false);
  });
}

for (const args of [[], ['--platform', 'all']]) {
  test(`${args.length ? 'explicit all' : 'default'} discovery preserves shared and both platform tests`, t => {
    const f = discoveryFixture(t);
    const names = ['shared', 'windows-runtime', 'ios-runtime'];
    for (const name of names) f.add(name);
    const result = f.run(...args);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.equal(checkTestSummary(result.stdout, 'all'), 3);
    for (const name of names) assert.equal(f.executed(name), true);
  });
}

for (const [name, body, expectedError] of [
  ['zero tests', "describe('empty suite', () => {});", /empty, failed, skipped, cancelled or todo/],
  ['failed test', "test('failing fixture', () => assert.fail('FIXTURE_FAILURE'));", /test runner failed/],
  ['skipped test', "test.skip('skipped fixture', () => {});", /empty, failed, skipped, cancelled or todo/],
  ['todo test', "test.todo('unfinished fixture');", /empty, failed, skipped, cancelled or todo/],
  ['cancelled test', "test('cancelled fixture', { signal: AbortSignal.abort() }, () => {});", /test runner failed/],
]) {
  test(`discovered ${name} cannot satisfy the root gate`, t => {
    const f = discoveryFixture(t);
    f.add('shared', body);
    const result = f.run('--platform', 'windows');
    assert.equal(result.status, 1, result.stderr);
    assert.equal(f.executed('shared'), true);
    assert.match(result.stderr, expectedError);
    assert.doesNotMatch(result.stdout, /Root assembly\/build tests passed:/);
  });
}

test('a failing selected platform test propagates failure even when shared tests pass', t => {
  const f = discoveryFixture(t);
  f.add('shared');
  f.add('ios-runtime', "test('failing platform fixture', () => assert.fail('PLATFORM_FAILURE'));");
  const result = f.run('--platform', 'ios');
  assert.equal(result.status, 1, result.stderr);
  assert.equal(f.executed('shared'), true);
  assert.equal(f.executed('ios-runtime'), true);
  assert.match(result.stderr, /test runner failed/);
  assert.doesNotMatch(result.stdout, /Root assembly\/build tests passed:/);
});

test('invalid platform arguments fail before discovering or executing tests', t => {
  const f = discoveryFixture(t);
  f.add('shared');
  for (const args of [
    ['windows'], ['--platform'], ['--platform', 'android'], ['--unknown', 'windows'],
    ['--platform', 'windows', '--platform', 'ios'],
  ]) {
    const result = f.run(...args);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /Usage: node scripts\/test-root\.mjs/);
    assert.equal(f.executed('shared'), false);
  }
});

for (const shape of ['array', 'object']) {
  test(`${shape} workspace declarations execute their tests and leave unrelated package directories unread`, t => {
    const f = discoveryFixture(t, 'test-packages');
    const declarations = ['packages/declared', 'modules/*', '!modules/excluded'];
    f.write('package.json', JSON.stringify({
      workspaces: shape === 'array' ? declarations : { packages: declarations },
    }));
    for (const location of ['packages/declared', 'modules/declared']) {
      f.write(`${location}/package.json`, JSON.stringify({
        name: `@meowfilm/${location.replace('/', '-')}`, scripts: { test: 'node --test' },
      }));
      f.add('declared', undefined, `${location}/test`);
    }
    // Neither a resource directory without a manifest nor an undeclared or
    // excluded malformed manifest may be opened by workspace discovery.
    for (const location of ['packages/resource', 'packages/undeclared', 'modules/excluded']) {
      if (location !== 'packages/resource') f.write(`${location}/package.json`, 'invalid JSON');
      f.add('unrelated', "throw new Error('UNDECLARED_TEST_EXECUTED');", `${location}/test`);
    }
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.match(result.stdout, /Implementation packages: 2; failed groups: 0\./);
    for (const location of ['packages/declared', 'modules/declared'])
      assert.equal(f.executed('declared', `${location}/test`), true);
    for (const location of ['packages/resource', 'packages/undeclared', 'modules/excluded'])
      assert.equal(f.executed('unrelated', `${location}/test`), false);
  });
}

test('workspace test failures return nonzero and other declared workspaces still execute', t => {
  const f = discoveryFixture(t, 'test-packages');
  f.write('package.json', JSON.stringify({ workspaces: ['packages/*'] }));
  for (const name of ['failing', 'passing']) {
    f.write(`packages/${name}/package.json`, JSON.stringify({
      name: `@meowfilm/${name}`, scripts: { test: 'node --test' },
    }));
    f.add(name, name === 'failing' ? "test('failing fixture', () => assert.fail('WORKSPACE_FAILURE'));" : undefined,
      `packages/${name}/tests`);
  }
  const result = f.run();
  assert.equal(result.status, 1, result.stderr);
  assert.equal(f.executed('failing', 'packages/failing/tests'), true);
  assert.equal(f.executed('passing', 'packages/passing/tests'), true);
  assert.match(result.stdout, /WORKSPACE_FAILURE/);
  assert.match(result.stdout, /Implementation packages: 2; failed groups: 1\./);
  assert.match(result.stderr, /test runner failed/);
});

test('package discovery cannot satisfy the gate with no declared workspaces', t => {
  const f = discoveryFixture(t, 'test-packages');
  f.write('package.json', JSON.stringify({ workspaces: [] }));
  f.add('unrelated', undefined, 'packages/resource/test');
  const result = f.run();
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, /No implementation packages discovered/);
  assert.equal(f.executed('unrelated', 'packages/resource/test'), false);
});

test('a declared workspace without test files fails the package gate', t => {
  const f = discoveryFixture(t, 'test-packages');
  f.write('package.json', JSON.stringify({ workspaces: ['packages/empty'] }));
  f.write('packages/empty/package.json', JSON.stringify({
    name: '@meowfilm/empty', scripts: { test: 'node --test' },
  }));
  const result = f.run();
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, /has no test cases/);
  assert.match(result.stdout, /Implementation packages: 1; failed groups: 1\./);
});
