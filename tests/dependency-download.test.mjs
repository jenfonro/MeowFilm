import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { copyFile, lstat, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dependencyCacheRoot, downloadDependency } from '../scripts/dependency-download.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const helper = new URL('../scripts/dependency-download.mjs', import.meta.url).href;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const content = Buffer.from('Locked dependency archive fixture.\n');

async function fixture(t, respond = (_request, response) => response.end(content)) {
  const temporary = join(repository, 'tmp/test-results/dependency-download');
  await mkdir(temporary, { recursive: true });
  const area = await mkdtemp(join(temporary, 'case-')), directory = join(area, 'cache/downloads');
  const server = createServer(respond);
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    await rm(area, { recursive: true, force: true });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { area, directory, specification: {
    url: `http://127.0.0.1:${server.address().port}/archive`, sha256: hash(content),
  } };
}

function childDownload(t, specification, options) {
  const child = spawn(process.execPath, ['--input-type=module', '-e',
    `import { downloadDependency } from ${JSON.stringify(helper)};
     console.log(await downloadDependency(${JSON.stringify(specification)}, ${JSON.stringify(options)}));`],
  { stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let stdout = '', stderr = '';
  child.stdout.on('data', bytes => { stdout += bytes; });
  child.stderr.on('data', bytes => { stderr += bytes; });
  return new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', code => code === 0 ? resolve(stdout.trim()) : reject(new Error(stderr)));
  });
}

test('dependency cache defaults use Developer on Unix and the E drive on Windows', () => {
  assert.equal(dependencyCacheRoot('linux', '/users/builder'), join('/users/builder', 'Developer/cache'));
  assert.equal(dependencyCacheRoot('darwin', '/Users/builder'), join('/Users/builder', 'Developer/cache'));
  assert.equal(dependencyCacheRoot('win32', 'C:/Users/builder'), 'E:/Developer/cache');
});

test('two workspaces reuse one hash-addressed download and own independent snapshots', async t => {
  let requests = 0;
  const f = await fixture(t, (_request, response) => { requests++; response.end(content); });
  const first = join(f.area, 'first/tmp/archive'), second = join(f.area, 'second/tmp/archive');
  assert.equal(await downloadDependency(f.specification, { directory: f.directory, snapshot: first }), first);
  assert.equal(await downloadDependency(f.specification, { directory: f.directory, snapshot: second }), second);
  const shared = join(f.directory, f.specification.sha256);
  assert.equal(requests, 1);
  assert.deepEqual(await readdir(f.directory), [f.specification.sha256]);
  assert.deepEqual(await readFile(first), content);
  assert.deepEqual(await readFile(second), content);
  await writeFile(first, 'private mutation');
  assert.deepEqual(await readFile(shared), content);
  assert.deepEqual(await readFile(second), content);
  await assert.rejects(downloadDependency(f.specification, { directory: f.directory, snapshot: first }), /hash mismatch/);
  await rm(shared);
  assert.equal(await downloadDependency(f.specification, { directory: f.directory, snapshot: second }), second);
  assert.equal(requests, 1, 'a private receipt snapshot survives shared cache cleanup');
});

test('different archive contents at the same URL remain isolated', async t => {
  let bytes = content;
  const f = await fixture(t, (_request, response) => response.end(bytes));
  const first = await downloadDependency(f.specification, { directory: f.directory });
  bytes = Buffer.from('another pinned release');
  const second = await downloadDependency({ ...f.specification, sha256: hash(bytes) }, { directory: f.directory });
  assert.notEqual(first, second);
  assert.deepEqual(await readFile(first), content);
  assert.deepEqual(await readFile(second), bytes);
});

test('FFmpeg shares the Apple hash-named tar.gz location', async t => {
  const f = await fixture(t);
  const directory = join(f.area, 'cache/ffmpeg/downloads');
  assert.equal(await downloadDependency(f.specification, { directory, suffix: '.tar.gz' }),
    join(directory, `${f.specification.sha256}.tar.gz`));
});

test('concurrent processes publish one complete archive without sharing writable snapshots',
  { timeout: 15000 }, async t => {
    const waiting = [], ready = Promise.withResolvers();
    const f = await fixture(t, (_request, response) => {
      response.write(content.subarray(0, 5));
      waiting.push(response);
      if (waiting.length === 2) ready.resolve();
    });
    const snapshots = ['first', 'second'].map(name => join(f.area, name, 'archive'));
    const downloads = snapshots.map(snapshot => childDownload(t, f.specification, { directory: f.directory, snapshot }));
    const completed = Promise.all(downloads);
    await Promise.race([ready.promise, completed.then(() => assert.fail('both downloads must overlap'))]);
    await assert.rejects(lstat(join(f.directory, f.specification.sha256)), { code: 'ENOENT' });
    for (const response of waiting) response.end(content.subarray(5));
    assert.deepEqual(await completed, snapshots);
    assert.deepEqual(await readFile(join(f.directory, f.specification.sha256)), content);
    assert.deepEqual(await readdir(f.directory), [f.specification.sha256]);
    await writeFile(snapshots[0], 'private mutation');
    assert.deepEqual(await readFile(snapshots[1]), content);
    assert.deepEqual(await readFile(join(f.directory, f.specification.sha256)), content);
  });

for (const failure of ['http', 'hash', 'interrupted']) {
  test(`a ${failure} failure publishes nothing and permits a later retry`, async t => {
    let fail = true;
    const f = await fixture(t, (_request, response) => {
      if (!fail) { response.end(content); return; }
      if (failure === 'http') { response.writeHead(503); response.end(); }
      else if (failure === 'hash') response.end('wrong archive');
      else { response.write(content.subarray(0, 5)); setImmediate(() => response.destroy()); }
    });
    const snapshot = join(f.area, 'workspace/archive');
    await assert.rejects(downloadDependency(f.specification, { directory: f.directory, snapshot }));
    assert.deepEqual(await readdir(f.directory), []);
    await assert.rejects(lstat(snapshot), { code: 'ENOENT' });
    fail = false;
    assert.equal(await downloadDependency(f.specification, { directory: f.directory, snapshot }), snapshot);
    assert.deepEqual(await readFile(snapshot), content);
  });
}

test('corrupt shared archives are rejected without overwriting or downloading', async t => {
  let requests = 0;
  const f = await fixture(t, (_request, response) => { requests++; response.end(content); });
  await mkdir(f.directory, { recursive: true });
  const path = join(f.directory, f.specification.sha256);
  await writeFile(path, 'damaged');
  await assert.rejects(downloadDependency(f.specification, { directory: f.directory }), /hash mismatch/);
  assert.equal(await readFile(path, 'utf8'), 'damaged');
  assert.equal(requests, 0);
});

test('a competing publication must match the pinned hash and is never overwritten', async t => {
  const ready = Promise.withResolvers();
  const f = await fixture(t, (_request, response) => {
    response.write(content.subarray(0, 5));
    ready.resolve(response);
  });
  const download = downloadDependency(f.specification, { directory: f.directory });
  const response = await ready.promise;
  const path = join(f.directory, f.specification.sha256);
  await writeFile(path, 'invalid competing publication');
  response.end(content.subarray(5));
  await assert.rejects(download, /hash mismatch/);
  assert.equal(await readFile(path, 'utf8'), 'invalid competing publication');
  assert.deepEqual(await readdir(f.directory), [f.specification.sha256]);
});

test('an existing private snapshot is verified and reused without rewriting it', async t => {
  const f = await fixture(t);
  const original = join(f.area, 'original'), snapshot = join(f.area, 'snapshot');
  await writeFile(original, content);
  await copyFile(original, snapshot);
  const before = await lstat(snapshot);
  await downloadDependency(f.specification, { directory: f.directory, snapshot });
  assert.equal((await lstat(snapshot)).mtimeMs, before.mtimeMs);
  await assert.rejects(lstat(f.directory), { code: 'ENOENT' });
});
