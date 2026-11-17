import { stringifyJSONValue, rawJSON } from '@meowfilm/runtime-bridge/json';
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { createFilmCatalog } from '../packages/film-playback/src/catalog.js';
import { createPlaybackLibrary } from '../packages/film-playback/src/library.js';
import { createFilmRulesService } from '../packages/film-playback/src/rules-service.js';
import { normalizeDetail, recognizeCandidates } from '../packages/film-playback/src/recognition.js';

const source = { key: 'site', engine: 'cat', installationId: 'installation', siteId: 'site',
  generation: 1, name: '来源' };
function setup(t) {
  const area = new URL('../tmp/test-results/film-catalog/', import.meta.url);
  mkdirSync(area, { recursive: true });
  const directory = mkdtempSync(new URL('identity-', area));
  const library = createPlaybackLibrary({ dataRoot: directory });
  const catalog = createFilmCatalog({ library });
  t.after(() => { library.close(); rmSync(directory, { recursive: true }); });
  return { catalog, library, directory };
}

test('source business identity uses the real source and typed detail, never its generation or title', t => {
  const { catalog } = setup(t);
  const target = detailId => ({ id: 'old-display', kind: 'source', title: '作品',
    source: { key: source.key, detailId } });
  const first = catalog.resolve(target(0), [source]);
  const second = catalog.resolve({ ...target(0), title: '更新标题' }, [{ ...source, generation: 2 }]);
  assert.equal(first.id, second.id);
  assert.notEqual(first.id, catalog.resolve(target('0'), [source]).id);
  assert.match(first.id, /^source:[a-f0-9]{64}$/);
});

test('an established group retains its business identity when members and ordering change or the runner restarts', t => {
  const { catalog, library, directory } = setup(t);
  const other = { ...source, key: 'other', installationId: 'other' };
  const card = members => ({ sourceKind: 'site', displayKey: 'group:作品', groupKey: '作品',
    title: '作品', aggregateItems: members.map(detailId => ({ siteKey: source.key, detailId })) });
  const first = catalog.search([card([1])], [source], 'rules')[0];
  library.update(draft => { draft.previewTargets.push(first.businessId); });
  const expanded = { ...card([2, 1]), aggregateItems: [
    { siteKey: other.key, detailId: 'new' }, ...card([2, 1]).aggregateItems,
  ] };
  assert.equal(catalog.search([expanded], [source, other], 'rules')[0].businessId, first.businessId);
  library.close();
  const restarted = createPlaybackLibrary({ dataRoot: directory });
  t.after(() => restarted.close());
  const next = createFilmCatalog({ library: restarted }).search([card([1, 2])], [source], 'new-rules')[0];
  assert.equal(next.businessId, first.businessId);
  assert.deepEqual(restarted.read().previewTargets, [first.businessId]);
});

test('metadata cards, seasons, episodes and recommendations carry their own stable coordinates', t => {
  const { catalog } = setup(t);
  const value = catalog.metadata('tmdb', '/3/tv/42', { id: 42, name: '作品',
    seasons: [{ season_number: 2, episodes: [{ episode_number: 3, name: '第三集' }] }],
    similar: { results: [{ id: 50, media_type: 'movie', title: '推荐' }] } });
  assert.equal(value.businessId, 'tmdb:tv:42');
  assert.equal(value.seasons[0].businessId, 'tmdb:tv:42:season:2');
  assert.equal(value.seasons[0].episodes[0].businessId, 'tmdb:tv:42:season:2:episode:3');
  assert.deepEqual(value.seasons[0].episodes[0].playbackTarget, {
    id: 'tmdb:tv:42', kind: 'tv', title: '作品', metadataProvider: 'tmdb', metadataId: '42', season: 2, episode: 3,
  });
  assert.equal(value.similar.results[0].businessId, 'tmdb:movie:50');
});

test('directory file identities exactly match the existing recognition owner', async t => {
  const { catalog, directory } = setup(t);
  const rules = createFilmRulesService({ dataRoot: directory });
  t.after(() => rules.close());
  const detail = normalizeDetail({ list: [{ vod_id: 0, vod_name: '作品',
    vod_play_from: '原线路', vod_play_url: '第1集$file-one#第2集$file-two' }] },
  { source: { ...source, detailId: 0 } });
  const projected = catalog.directory(source, detail);
  const candidates = await recognizeCandidates(detail, { source: { ...source, detailId: 0 },
    target: projected.playbackTarget, rules: (await rules.snapshot()).rules, completeDirectory: true });
  assert.deepEqual(projected.lines[0].episodes.map(row => row.businessId), candidates.map(row => row.id));
  assert.equal(projected.lines[0].episodes[0].playbackTarget.source.candidateId, candidates[0].id);
  assert.equal(projected.lines[0].episodes[0].playbackTarget.source.detailId, 0);
});

test('current group registration keeps unused corrupt siblings and exact extension material', t => {
  const { catalog, library } = setup(t);
  library.update(value => {
    value.groups.push(null, { id: 'unrelated', keys: null, members: 4, extension: rawJSON('9007199254740993') });
    value.history.push({ targetId: 'unrelated', extension: rawJSON('1e400') });
  });
  const target = catalog.resolve({ kind: 'group', title: '作品', members: [{ sourceKey: source.key, detailId: 0 }] }, [source]);
  assert.match(target.id, /^group:[a-f0-9]{64}$/);
  assert.equal(library.read().groups.length, 3);
  assert.equal(stringifyJSONValue(library.read().groups[1]),
    '{"id":"unrelated","keys":null,"members":4,"extension":9007199254740993}');
  assert.equal(stringifyJSONValue(library.read().history), '[{"targetId":"unrelated","extension":1e400}]');
  library.update(value => { value.groups[2].id = 'broken-selected-group'; });
  assert.throws(() => catalog.resolve({ kind: 'group', title: '作品', members: [{ sourceKey: source.key, detailId: 0 }] }, [source]),
    { code: 'CORRUPT_STORAGE' });
});

test('history projects only its recorded target and marks invalid individual materials without losing healthy rows', t => {
  const { catalog, library } = setup(t);
  const target = { id: 'tmdb:movie:42', kind: 'movie', title: '作品', metadataProvider: 'tmdb', metadataId: '42' };
  library.update(value => { value.history.push(null, { targetId: target.id, extension: rawJSON('1e400') },
    { targetId: target.id, playbackTarget: { ...target, id: 'tmdb:movie:43' } },
    { targetId: target.id, playbackTarget: target, extension: rawJSON('9007199254740993') }); });
  const rows = library.read().history.map(catalog.history);
  assert.equal(rows.length, 4);
  assert.ok(rows.slice(0, 3).every(row => row.errorCode === 'CORRUPT_STORAGE'));
  assert.equal('playbackTarget' in rows[1], false);
  assert.equal(stringifyJSONValue(rows[1].extension), '1e400');
  assert.equal(rows[3].businessId, target.id);
  assert.deepEqual(rows[3].playbackTarget, target);
  assert.equal(stringifyJSONValue(rows[3].extension), '9007199254740993');
  assert.equal(library.read().history[0], null);
});
