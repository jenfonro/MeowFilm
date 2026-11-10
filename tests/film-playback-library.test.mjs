import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPlaybackLibrary } from '../packages/film-playback/src/library.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';

function area(t) {
  const root = fileURLToPath(new URL('../tmp/test-results/film-playback/', import.meta.url));
  fs.mkdirSync(root, { recursive: true });
  const path = fs.mkdtempSync(join(root, 'library-'));
  t.after(() => fs.rmSync(path, { recursive: true, force: true }));
  return path;
}

function library(t, dataRoot = area(t)) {
  const owner = createPlaybackLibrary({ dataRoot });
  t.after(() => owner.close());
  return { owner, dataRoot, directory: join(dataRoot, 'film-playback'),
    path: join(dataRoot, 'film-playback', 'library.json') };
}

const history = () => ({
  targetId: 'tmdb:tv:42', title: '合成剧集', sourceKey: 'cat:installation:site',
  candidateId: 'candidate:exact-file', episode: { season: 1, episode: 2, globalEpisode: 2 },
  detailId: 'detail:one', lineId: 'raw-flag', position: 12.5, duration: 600,
  paused: false, ended: false, playedAt: 1000, updatedAt: 1200,
});
const manual = () => ({
  id: 'manual:one', targetId: 'tmdb:tv:42', kind: 'site', sourceKey: 'cat:installation:site',
  detailId: 'detail:one', seasonHint: 1, enabled: true, autoDisable: true, failureCount: 0,
});
const block = (extra = {}) => ({
  targetId: 'tmdb:tv:42', sourceKey: 'cat:installation:site',
  detailId: 'detail:one', lineId: 'raw-flag', ...extra,
});
const empty = () => ({
  version: 1, preferences: defaultPreferences(), history: [], manual: [], blocks: [], previewTargets: [],
});

test('E31/E36: initial document takes defaults only from policy and exposes an immutable snapshot', t => {
  const { owner, path } = library(t);
  assert.deepEqual(owner.read(), empty());
  assert.equal(fs.existsSync(path), false);
  assert.ok(Object.isFrozen(owner.read()));
  assert.ok(Object.isFrozen(owner.read().preferences));
  assert.throws(() => owner.read().manual.push(manual()), TypeError);
});

test('E37: update publishes only a successful atomic commit, detached from any retained draft', t => {
  const { owner, path } = library(t);
  const before = owner.read();
  let held;
  const after = owner.update(draft => {
    assert.equal(owner.read(), before);
    assert.equal(fs.existsSync(path), false);
    held = draft;
    draft.blocks.push(block());
    return 'mutator return is not document state';
  });
  assert.equal(owner.read(), after);
  assert.notEqual(after, held);
  held.blocks[0].lineId = 'changed-after-commit';
  assert.deepEqual(after.blocks, [block()]);
  assert.deepEqual(JSON.parse(fs.readFileSync(path, 'utf8')), after);
});

test('E05/E19/E30/E31/E36: all five new local sections persist on close/reopen without a cache owner', t => {
  const { owner, dataRoot } = library(t);
  const preferences = defaultPreferences();
  preferences.keywords = ['DDP', 'H.265'];
  preferences.siteCleanKeywords = [];
  preferences.iosAutoPrepare = false;
  const state = owner.update(draft => {
    draft.preferences = preferences;
    draft.history.push(history());
    draft.manual.push(manual());
    draft.blocks.push(block());
    draft.previewTargets.push('tmdb:tv:42');
  });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.deepEqual(restarted.read(), state);
  assert.deepEqual(restarted.read().preferences.keywords, ['DDP', 'H.265']);
  assert.deepEqual(restarted.read().preferences.siteCleanKeywords, []);
  assert.equal(restarted.read().preferences.iosAutoPrepare, false);
  assert.deepEqual(restarted.read().previewTargets, ['tmdb:tv:42']);
});

test('E05/F6: per-target preview switches survive restart without creating or rewriting watching history', t => {
  const { owner, dataRoot } = library(t);
  owner.update(draft => { draft.previewTargets.push('tmdb:tv:42', 'douban:movie:50'); });
  assert.deepEqual(owner.read().history, []);
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.deepEqual(restarted.read().previewTargets, ['tmdb:tv:42', 'douban:movie:50']);
  assert.deepEqual(restarted.read().history, []);
  assert.throws(() => restarted.read().previewTargets.push('new target'), TypeError);
  restarted.update(draft => { draft.history.push(history()); });
  const before = restarted.read().history;
  restarted.update(draft => {
    draft.previewTargets = draft.previewTargets.filter(targetId => targetId !== 'tmdb:tv:42');
  });
  assert.deepEqual(restarted.read().history, before);
  restarted.close();
  const reopened = createPlaybackLibrary({ dataRoot });
  t.after(() => reopened.close());
  assert.deepEqual(reopened.read().previewTargets, ['douban:movie:50']);
  assert.deepEqual(reopened.read().history, before);
});

test('E19/E31: precise blocks and both site/share manual references support durable add/edit/remove', t => {
  const { owner, dataRoot } = library(t);
  owner.update(draft => {
    draft.manual.push(manual(), {
      id: 'manual:share', targetId: 'tmdb:tv:42', kind: 'share', provider: 'quark',
      share: { url: 'https://synthetic.invalid/s/share-reference', password: 'fixture-only' },
      seasonHint: 0, enabled: false, autoDisable: false, failureCount: 3,
    });
    draft.blocks.push(block(), block({ lineId: 'another-line' }),
      block({ targetId: 'tmdb:tv:43' }), block({ detailId: 'detail:two' }));
  });
  owner.update(draft => {
    draft.manual[0].seasonHint = 2;
    draft.manual[0].enabled = false;
    draft.manual[0].failureCount = 3;
    draft.blocks = draft.blocks.filter(entry => entry.lineId !== 'another-line');
  });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.equal(restarted.read().manual[0].seasonHint, 2);
  assert.equal(restarted.read().manual[0].enabled, false);
  assert.equal(restarted.read().manual[0].failureCount, 3);
  assert.equal(restarted.read().manual[1].share.password, 'fixture-only');
  assert.deepEqual(restarted.read().blocks, [block(), block({ targetId: 'tmdb:tv:43' }),
    block({ detailId: 'detail:two' })]);
  restarted.update(draft => { draft.manual = []; draft.blocks = []; });
  assert.deepEqual(restarted.read().manual, []);
  assert.deepEqual(restarted.read().blocks, []);
});

test('E30: history preserves movie identity, exact source/candidate and progress through successive durable updates', t => {
  const { owner, dataRoot } = library(t);
  const entry = history();
  entry.targetId = 'douban:movie:50';
  entry.title = '合成电影';
  delete entry.episode;
  owner.update(draft => { draft.history.push(entry); });
  owner.update(draft => {
    const current = draft.history[0];
    current.position = 123.75;
    current.paused = true;
    current.updatedAt = 1400;
  });
  owner.update(draft => {
    const current = draft.history[0];
    current.ended = true;
    current.position = current.duration;
    current.updatedAt = 1600;
  });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  const actual = restarted.read().history[0];
  assert.equal(actual.candidateId, entry.candidateId);
  assert.equal(actual.sourceKey, entry.sourceKey);
  assert.equal(actual.episode, undefined);
  assert.equal(actual.position, 600);
  assert.equal(actual.paused, true);
  assert.equal(actual.ended, true);
  assert.equal(actual.playedAt, 1000);
  assert.equal(actual.updatedAt, 1600);
});

test('E31: manual failure state, resets and disabled flags remain exact persisted user data', t => {
  const { owner, dataRoot } = library(t);
  owner.update(draft => { draft.manual.push(manual()); });
  for (const failureCount of [1, 2, 0, 1, 2, 3]) {
    owner.update(draft => {
      draft.manual[0].failureCount = failureCount;
      draft.manual[0].enabled = failureCount < 3;
    });
  }
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.equal(restarted.read().manual[0].failureCount, 3);
  assert.equal(restarted.read().manual[0].enabled, false);
  assert.equal(restarted.read().manual[0].autoDisable, true);
});

test('E35/E36: preference groups can save, restart and restore individually using policy defaults', t => {
  const { owner, dataRoot } = library(t);
  owner.update(draft => {
    draft.preferences.ruleOrder.reverse();
    draft.preferences.panOrder.reverse();
    draft.preferences.keywords = ['DDP', 'H.265'];
    draft.preferences.siteCleanKeywords = ['合成净化词'];
    draft.preferences.iosAutoPrepare = false;
  });
  const customized = structuredClone(owner.read().preferences);
  owner.update(draft => { draft.preferences.keywords = defaultPreferences().keywords; });
  assert.deepEqual(owner.read().preferences, { ...customized, keywords: defaultPreferences().keywords });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.deepEqual(restarted.read().preferences, { ...customized, keywords: defaultPreferences().keywords });
  restarted.update(draft => { draft.preferences = defaultPreferences(); });
  assert.deepEqual(restarted.read().preferences, defaultPreferences());
});

test('E36/E37: invalid preferences fail their transaction, while invalid saved preferences are explicit corruption', t => {
  const { owner, path, dataRoot } = library(t);
  owner.update(() => {});
  const before = owner.read(), bytes = fs.readFileSync(path);
  assert.throws(() => owner.update(draft => { draft.preferences.ruleOrder = []; }),
    { code: 'INVALID_PREFERENCES' });
  assert.equal(owner.read(), before);
  assert.deepEqual(fs.readFileSync(path), bytes);
  const invalid = { ...empty(), preferences: { ruleOrder: [] } };
  fs.writeFileSync(path, JSON.stringify(invalid));
  assert.throws(() => createPlaybackLibrary({ dataRoot }), { code: 'CORRUPT_STORAGE' });
  assert.equal(fs.readFileSync(path, 'utf8'), JSON.stringify(invalid));
});

test('E37/E48: mutator exceptions fail without publication or disk replacement and redact their diagnostics', t => {
  const { owner, path } = library(t);
  owner.update(draft => { draft.blocks.push(block()); });
  const before = owner.read(), bytes = fs.readFileSync(path);
  const secret = 'https://synthetic.invalid/?signature=private-fixture';
  assert.throws(() => owner.update(draft => {
    draft.blocks = [];
    throw new Error(secret);
  }), error => {
    assert.equal(error.code, 'LIBRARY_UPDATE_FAILED');
    assert.equal(error.message, error.code);
    assert.equal(error.cause, undefined);
    assert.equal(error.stack.includes(secret), false);
    return true;
  });
  assert.equal(owner.read(), before);
  assert.deepEqual(fs.readFileSync(path), bytes);
});

test('E37: asynchronous and reentrant transactions cannot publish a partial draft', async t => {
  const { owner, path } = library(t);
  const before = owner.read();
  assert.throws(() => owner.update(async draft => { draft.blocks.push(block()); }), { code: 'INVALID_REQUEST' });
  assert.throws(() => owner.update(() => Promise.resolve()), { code: 'INVALID_REQUEST' });
  assert.throws(() => owner.update(() => owner.update(() => {})), { code: 'INVALID_REQUEST' });
  assert.equal(owner.read(), before);
  assert.equal(fs.existsSync(path), false);
  await Promise.resolve();
});

test('E37: invalid schemas, duplicate identities and unsafe history never replace the committed document', async t => {
  const cases = [
    ['version', draft => { draft.version = 2; }],
    ['foreign favorites owner', draft => { draft.favorites = []; }],
    ['foreign native rules owner', draft => { draft.rules = []; }],
    ['missing section', draft => { delete draft.manual; }],
    ['missing preview targets', draft => { delete draft.previewTargets; }],
    ['untyped preview target', draft => { draft.previewTargets.push(42); }],
    ['duplicate history target', draft => { draft.history.push(history(), history()); }],
    ['duplicate manual id', draft => { draft.manual.push(manual(), manual()); }],
    ['duplicate exact block', draft => { draft.blocks.push(block(), block()); }],
    ['negative progress', draft => { draft.history.push({ ...history(), position: -1 }); }],
    ['nonfinite progress', draft => { draft.history.push({ ...history(), position: Infinity }); }],
    ['invalid timestamp', draft => { draft.history.push({ ...history(), updatedAt: -1 }); }],
    ['missing exact line', draft => { const value = history(); delete value.lineId; draft.history.push(value); }],
    ['play URL field', draft => { draft.history.push({ ...history(), url: 'https://synthetic.invalid/video' }); }],
    ['headers field', draft => { draft.history.push({ ...history(), headers: { cookie: 'fixture-only' } }); }],
    ['playId field', draft => { draft.history.push({ ...history(), playId: 'https://synthetic.invalid/video' }); }],
    ['raw file field', draft => { draft.history.push({ ...history(), file: { token: 'fixture-only' } }); }],
    ['source credentials', draft => { draft.history.push({ ...history(), cookie: 'fixture-only' }); }],
    ['signed candidate id', draft => {
      draft.history.push({ ...history(), candidateId: 'https://synthetic.invalid/video?sign=fixture-only' });
    }],
    ['escaped signed id', draft => {
      draft.history.push({ ...history(),
        candidateId: 'candidate:https%3A%2F%2Fsynthetic.invalid%2Fvideo%3Fsign%3Dfixture-only' });
    }],
    ['signed detail id', draft => {
      draft.history.push({ ...history(), detailId: 'https://synthetic.invalid/video?signature=fixture-only' });
    }],
    ['credential URL', draft => {
      draft.history.push({ ...history(), detailId: 'https://fixture-only@synthetic.invalid/detail' });
    }],
    ['fractional count', draft => { draft.manual.push({ ...manual(), failureCount: 1.5 }); }],
    ['untyped enabled', draft => { draft.manual.push({ ...manual(), enabled: 'yes' }); }],
    ['inexact block', draft => { draft.blocks.push({ ...block(), lineId: '' }); }],
  ];
  for (const [name, mutate] of cases) {
    await t.test(name, sub => {
      const { owner, path } = library(sub);
      owner.update(() => {});
      const before = owner.read(), bytes = fs.readFileSync(path);
      assert.throws(() => owner.update(mutate), { code: 'INVALID_REQUEST' });
      assert.equal(owner.read(), before);
      assert.deepEqual(fs.readFileSync(path), bytes);
    });
  }
});

test('E37: malformed JSON, unsupported versions, invalid rows and invalid UTF-8 are explicit corruption', async t => {
  const unpublishedDraft = empty();
  delete unpublishedDraft.previewTargets;
  const cases = [
    ['JSON', Buffer.from('{')],
    ['version', Buffer.from(JSON.stringify({ ...empty(), version: 2 }))],
    ['missing rows', Buffer.from(JSON.stringify({ version: 1, preferences: defaultPreferences() }))],
    ['row schema', Buffer.from(JSON.stringify({ ...empty(), blocks: [{ targetId: 'x' }] }))],
    ['foreign owner', Buffer.from(JSON.stringify({ ...empty(), favorites: [] }))],
    ['unpublished draft missing preview targets', Buffer.from(JSON.stringify(unpublishedDraft))],
    ['UTF-8', Buffer.concat([Buffer.from('{"version":1,"x":"'), Buffer.from([0xff]), Buffer.from('"}')])],
  ];
  for (const [name, bytes] of cases) {
    await t.test(name, sub => {
      const dataRoot = area(sub), directory = join(dataRoot, 'film-playback');
      fs.mkdirSync(directory);
      const path = join(directory, 'library.json');
      fs.writeFileSync(path, bytes);
      assert.throws(() => createPlaybackLibrary({ dataRoot }), { code: 'CORRUPT_STORAGE' });
      assert.deepEqual(fs.readFileSync(path), bytes);
    });
  }
});

test('E37: a running owner refuses to overwrite later on-disk corruption or removal', async t => {
  for (const action of ['corrupt', 'remove']) {
    await t.test(action, sub => {
      const { owner, path } = library(sub);
      owner.update(draft => { draft.blocks.push(block()); });
      const before = owner.read();
      if (action === 'corrupt') fs.writeFileSync(path, '{synthetic corruption');
      else fs.unlinkSync(path);
      assert.throws(() => owner.update(draft => { draft.blocks = []; }),
        { code: action === 'corrupt' ? 'CORRUPT_STORAGE' : 'STORAGE_CHANGED' });
      assert.equal(owner.read(), before);
      if (action === 'corrupt') assert.equal(fs.readFileSync(path, 'utf8'), '{synthetic corruption');
      else assert.equal(fs.existsSync(path), false);
    });
  }
});

test('E37: a stale second owner cannot silently clobber another successful commit', t => {
  const { owner, dataRoot } = library(t);
  const second = createPlaybackLibrary({ dataRoot });
  t.after(() => second.close());
  owner.update(draft => { draft.blocks.push(block()); });
  assert.throws(() => second.update(draft => { draft.manual.push(manual()); }), { code: 'STORAGE_CHANGED' });
  const reopened = createPlaybackLibrary({ dataRoot });
  t.after(() => reopened.close());
  assert.deepEqual(reopened.read().blocks, [block()]);
  assert.deepEqual(reopened.read().manual, []);
});

test('E37: open/write/fsync/close/rename failures preserve disk and memory and clean this transaction temporary file', async t => {
  for (const method of ['openSync', 'writeFileSync', 'fsyncSync', 'closeSync', 'renameSync']) {
    await t.test(method, sub => {
      const { owner, path, directory } = library(sub);
      owner.update(draft => { draft.manual.push(manual()); });
      const before = owner.read(), bytes = fs.readFileSync(path);
      const original = fs[method];
      let temporaryFD;
      const open = fs.openSync;
      if (method === 'closeSync') {
        sub.mock.method(fs, 'openSync', function(path, ...rest) {
          const fd = open.call(this, path, ...rest);
          if (typeof path === 'string' && path.includes('.library-')) temporaryFD = fd;
          return fd;
        });
      }
      let failed = false;
      const mocked = sub.mock.method(fs, method, function(...args) {
        const targeted = method === 'openSync' ? typeof args[0] === 'string' && args[0].includes('.library-') :
          method === 'writeFileSync' || method === 'fsyncSync' || method === 'renameSync' ? true :
          args[0] === temporaryFD;
        if (targeted && !failed) {
          failed = true;
          throw Object.assign(new Error('synthetic private storage diagnostic'), { code: 'EIO' });
        }
        return original.apply(this, args);
      });
      assert.throws(() => owner.update(draft => { draft.manual = []; draft.blocks.push(block()); }),
        { code: 'STORAGE_ERROR', message: 'STORAGE_ERROR' });
      mocked.mock.restore();
      assert.equal(failed, true);
      assert.equal(owner.read(), before);
      assert.deepEqual(fs.readFileSync(path), bytes);
      assert.deepEqual(fs.readdirSync(directory), ['library.json']);
    });
  }
});

test('E37: a failed first commit creates no successful owner state or document', t => {
  const { owner, path, directory } = library(t);
  const before = owner.read();
  const mocked = t.mock.method(fs, 'renameSync', () => {
    throw Object.assign(new Error('synthetic'), { code: 'EACCES' });
  });
  assert.throws(() => owner.update(draft => { draft.blocks.push(block()); }), { code: 'STORAGE_ERROR' });
  mocked.mock.restore();
  assert.equal(owner.read(), before);
  assert.equal(fs.existsSync(path), false);
  assert.deepEqual(fs.readdirSync(directory), []);
  owner.update(draft => { draft.blocks.push(block()); });
  assert.deepEqual(owner.read().blocks, [block()]);
});

test('E31/E37: sensitive share document and containing directory have owner-only POSIX permissions', t => {
  const { owner, path, directory, dataRoot } = library(t);
  owner.update(draft => {
    draft.manual.push({
      id: 'manual:share', targetId: 'tmdb:movie:42', kind: 'share',
      share: { url: 'https://synthetic.invalid/s/private-share', password: 'fixture-only' },
      enabled: true, autoDisable: false, failureCount: 0,
    });
  });
  assert.equal(fs.statSync(directory).mode & 0o777, 0o700);
  assert.equal(fs.statSync(path).mode & 0o777, 0o600);
  fs.chmodSync(directory, 0o755);
  fs.chmodSync(path, 0o644);
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.equal(fs.statSync(directory).mode & 0o777, 0o700);
  assert.equal(fs.statSync(path).mode & 0o777, 0o600);
  assert.equal(restarted.read().manual[0].share.password, 'fixture-only');
});

test('E37: symlink or hardlink documents and owned symlink directories cannot redirect this owner', async t => {
  for (const kind of ['file-symlink', 'hardlink', 'directory-symlink']) {
    await t.test(kind, sub => {
      const base = area(sub), dataRoot = join(base, 'data'), other = join(base, 'other');
      fs.mkdirSync(dataRoot);
      fs.mkdirSync(other);
      const bytes = JSON.stringify(empty()), otherFile = join(other, 'library.json');
      fs.writeFileSync(otherFile, bytes);
      const directory = join(dataRoot, 'film-playback');
      if (kind === 'directory-symlink') fs.symlinkSync(other, directory);
      else {
        fs.mkdirSync(directory);
        if (kind === 'file-symlink') fs.symlinkSync(otherFile, join(directory, 'library.json'));
        else fs.linkSync(otherFile, join(directory, 'library.json'));
      }
      assert.throws(() => createPlaybackLibrary({ dataRoot }), { code: 'STORAGE_ERROR' });
      assert.equal(fs.readFileSync(otherFile, 'utf8'), bytes);
    });
  }
});

test('E37: App-owned root aliases remain valid and pin this owner to the initial canonical directory', t => {
  const base = area(t), original = join(base, 'original'), other = join(base, 'other');
  const linked = join(base, 'linked');
  fs.mkdirSync(original);
  fs.mkdirSync(other);
  fs.symlinkSync(original, linked);
  const owner = createPlaybackLibrary({ dataRoot: linked });
  t.after(() => owner.close());
  fs.unlinkSync(linked);
  fs.symlinkSync(other, linked);
  owner.update(draft => { draft.blocks.push(block()); });
  assert.equal(fs.existsSync(join(other, 'film-playback', 'library.json')), false);
  assert.deepEqual(JSON.parse(fs.readFileSync(join(original, 'film-playback', 'library.json'), 'utf8')).blocks,
    [block()]);
});

test('E37: a not-yet-created App root below a platform alias can commit without moving the trusted subtree', t => {
  const base = area(t), actual = join(base, 'actual'), linked = join(base, 'linked');
  fs.mkdirSync(actual);
  fs.symlinkSync(actual, linked);
  const dataRoot = join(linked, 'application-support', 'script-runtime');
  const owner = createPlaybackLibrary({ dataRoot });
  t.after(() => owner.close());
  owner.update(draft => { draft.blocks.push(block()); });
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.deepEqual(restarted.read().blocks, [block()]);
  assert.equal(fs.existsSync(join(actual, 'application-support', 'script-runtime', 'film-playback', 'library.json')), true);
});

test('E37: bad manual sharing references and transport secrets fail with safe errors', async t => {
  for (const share of [
    { url: 'not a URL/private-fixture' },
    { url: 'file:///private-fixture' },
    { url: 'https://private-fixture@synthetic.invalid/s/id' },
    { url: 'https://synthetic.invalid/s/id', cookie: 'private-fixture' },
  ]) {
    await t.test('invalid share', sub => {
      const { owner, path } = library(sub);
      assert.throws(() => owner.update(draft => {
        draft.manual.push({
          id: 'manual:share', targetId: 'tmdb:movie:42', kind: 'share', share,
          enabled: true, autoDisable: false, failureCount: 0,
        });
      }), error => {
        assert.equal(error.code, 'INVALID_REQUEST');
        assert.equal(error.message, 'INVALID_REQUEST');
        assert.equal(error.stack.includes('private-fixture'), false);
        return true;
      });
      assert.equal(fs.existsSync(path), false);
      assert.deepEqual(owner.read().manual, []);
    });
  }
});

test('E48: close is idempotent, rejects further operations, and cannot be bypassed by a held transaction', t => {
  const { owner, dataRoot, path } = library(t);
  owner.update(draft => { draft.blocks.push(block()); });
  const bytes = fs.readFileSync(path);
  assert.throws(() => owner.update(draft => { draft.blocks = []; owner.close(); }), { code: 'CLOSED' });
  owner.close();
  assert.throws(() => owner.read(), { code: 'CLOSED' });
  assert.throws(() => owner.update(() => {}), { code: 'CLOSED' });
  assert.deepEqual(fs.readFileSync(path), bytes);
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.deepEqual(restarted.read().blocks, [block()]);
});

test('E37: a normal site detail URL remains an accurate persistent identity', t => {
  const { owner } = library(t);
  const detailId = 'https://synthetic.invalid/detail?id=42';
  owner.update(draft => { draft.blocks.push(block({ detailId })); });
  assert.equal(owner.read().blocks[0].detailId, detailId);
});

test('E19/E30: ordinary source page URLs remain valid opaque target/source/line identities', t => {
  const { owner } = library(t);
  const value = history();
  value.targetId = 'https://synthetic.invalid/detail?id=42';
  value.sourceKey = 'https://synthetic.invalid/source?id=site';
  value.lineId = 'https://synthetic.invalid/line?id=original';
  owner.update(draft => {
    draft.history.push(value);
    draft.blocks.push(block({ targetId: value.targetId, sourceKey: value.sourceKey, lineId: value.lineId }));
  });
  assert.deepEqual(owner.read().history, [value]);
  assert.equal(owner.read().blocks[0].lineId, value.lineId);
});

test('E19/E30/E31: source numeric detail and line ids retain their exact scalar identity across restart', t => {
  const { owner, dataRoot } = library(t);
  const entry = { ...history(), detailId: 42, lineId: 0 };
  delete entry.ended;
  delete entry.playedAt;
  entry.duration = undefined;
  entry.episode = { season: undefined, episode: undefined };
  owner.update(draft => {
    draft.history.push(entry);
    draft.manual.push({
      id: 'manual:numeric', targetId: entry.targetId, sourceKey: entry.sourceKey,
      detailId: 42, failureCount: 0,
    });
    draft.blocks.push(block({ detailId: 42, lineId: 0 }), block({ detailId: '42', lineId: '0' }));
  });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.equal(restarted.read().history[0].detailId, 42);
  assert.equal(restarted.read().history[0].lineId, 0);
  assert.equal(Object.hasOwn(restarted.read().history[0], 'duration'), false);
  assert.deepEqual(restarted.read().history[0].episode, {});
  assert.equal(restarted.read().manual[0].detailId, 42);
  assert.equal(restarted.read().blocks[0].lineId, 0);
  assert.equal(restarted.read().blocks[1].detailId, '42');
  assert.equal(restarted.read().blocks[1].lineId, '0');
});

test('E30/E31: optional manual display strings and millisecond clock adjustments do not restrict durable user data', t => {
  const { owner, dataRoot } = library(t);
  const entry = { ...history(), playedAt: 1200, updatedAt: 1000 };
  owner.update(draft => {
    draft.history.push(entry);
    draft.manual.push({ ...manual(), title: '', name: '' });
  });
  owner.close();
  const restarted = createPlaybackLibrary({ dataRoot });
  t.after(() => restarted.close());
  assert.equal(restarted.read().history[0].playedAt, 1200);
  assert.equal(restarted.read().history[0].updatedAt, 1000);
  assert.equal(restarted.read().manual[0].title, '');
  assert.equal(restarted.read().manual[0].name, '');
});

test('E37: relative or traversing roots are rejected without touching storage', () => {
  for (const dataRoot of ['relative', '/tmp/../other', '/tmp/\0bad']) {
    assert.throws(() => createPlaybackLibrary({ dataRoot }), { code: 'INVALID_REQUEST' });
  }
});
