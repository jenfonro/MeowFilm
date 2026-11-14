import test from 'node:test';
import assert from 'node:assert/strict';
import { decodePanResource, normalizeCatOutput, shareIdentity } from '../src/cat-output.js';

const options = {
  settings: { providers: {}, mappings: [] },
  installationId: 'cat-output-fixture', siteId: 'site',
};

test('Cat output derives dimensions from positive integer numbers and numeric strings', () => {
  const list = [
    { video_width: 3840, video_height: 2160, fps: '60000/1001' },
    { video_meta: { width: '1920', height: '1080', frame_rate: '25' } },
    { vwidth: '2160', vheight: 3840, frameRate: 24 },
    { width: 32768, height: '16384', fps: 60 },
  ];
  const result = normalizeCatOutput({ list }, options);
  assert.deepEqual(result.list, list);
  assert.deepEqual(result._meow.media, [
    { vodIndex: 0, resolution: '3840x2160', quality: '4K', fps: 60000 / 1001 },
    { vodIndex: 1, resolution: '1920x1080', quality: '1080P', fps: 25 },
    { vodIndex: 2, resolution: '2160x3840', quality: '4K', fps: 24 },
    { vodIndex: 3, resolution: '32768x16384', quality: '4K', fps: 60 },
  ]);
  const root = { video_meta: list[0], file_name: 'episode.mkv' };
  const output = normalizeCatOutput(root, options);
  assert.deepEqual(output.video_meta, root.video_meta);
  assert.equal(output.file_name, root.file_name);
  assert.deepEqual(output._meow.media, [
    { resolution: '3840x2160', quality: '4K', fps: 60000 / 1001, filename: 'episode.mkv' },
  ]);
});

test('Cat output rejects invalid dimensions without inventing quality or losing raw fields and fps', () => {
  const invalid = [
    Infinity, -Infinity, NaN, 'Infinity', '-Infinity', 'NaN',
    true, false, [], [3840], {}, 3840n, '', '  ', null, undefined,
    0, -3840, '0', '-3840', 3840.5, '3840.5', 'not-a-number',
    Number.MAX_SAFE_INTEGER + 1, '9007199254740993',
  ];
  const list = invalid.flatMap(value => [
    { video_meta: { width: value, height: 2160, fps: '24000/1001' } },
    { video_width: 3840, video_height: value, fps: 25 },
  ]);
  const result = normalizeCatOutput({ list }, options);
  assert.deepEqual(result.list, list);
  assert.deepEqual(result._meow.media, list.map((_, vodIndex) =>
    ({ vodIndex, fps: vodIndex % 2 === 0 ? 24000 / 1001 : 25 })));
  for (const record of list) {
    const root = normalizeCatOutput(record, options);
    assert.deepEqual(root._meow.media, [{ fps: record.video_meta ? 24000 / 1001 : 25 }]);
    for (const [key, value] of Object.entries(record)) assert.deepEqual(root[key], value);
  }
});

test('Cat output keeps explicit resolution and raw metadata when dimensions are unavailable', () => {
  const input = { resolution: 'provider-original', video_meta: { width: Infinity, height: true, fps: 24 } };
  const result = normalizeCatOutput(input, options);
  assert.equal(result.resolution, input.resolution);
  assert.deepEqual(result.video_meta, input.video_meta);
  assert.deepEqual(result._meow.media, [{ resolution: 'provider-original', fps: 24 }]);
  const missing = { list: [{ video_meta: { width: 3840 } }, { video_height: 2160 }, {}] };
  assert.deepEqual(normalizeCatOutput(missing, options), missing);
});

test('Cat output preserves valid fractional, numeric and string fps with raw fields', () => {
  const cases = [
    [24, 24], [29.97, 29.97], ['60', 60], ['29.97', 29.97],
    ['60000/1001', 60000 / 1001], ['24000/1001', 24000 / 1001],
  ];
  const records = cases.flatMap(([raw, fps]) => [
    [{ fps: raw }, fps], [{ frameRate: raw }, fps], [{ video_meta: { frame_rate: raw } }, fps],
  ]);
  const list = records.map(([record]) => record);
  const result = normalizeCatOutput({ list }, options);
  assert.deepEqual(result.list, list);
  assert.deepEqual(result._meow.media, records.map(([, fps], vodIndex) => ({ vodIndex, fps })));
  for (const [record, fps] of records) {
    const root = normalizeCatOutput(record, options);
    assert.deepEqual(root._meow.media, [{ fps }]);
    for (const [key, raw] of Object.entries(record)) assert.deepEqual(root[key], raw);
  }
});

test('Cat output rejects invalid fps without losing raw fields or valid dimensions', () => {
  const invalid = [
    true, false, [], [60], {}, { fps: 60 }, 60n, null, undefined, '', '  ', 'bad',
    NaN, Infinity, -Infinity, 'NaN', 'Infinity', '-Infinity', 0, -1, '0', '-1', '60/0',
  ];
  const list = invalid.flatMap(raw => [
    { width: 1920, height: 1080, fps: raw },
    { width: 1920, height: 1080, frameRate: raw },
    { video_meta: { width: 1920, height: 1080, frame_rate: raw } },
  ]);
  const media = { resolution: '1920x1080', quality: '1080P' };
  const result = normalizeCatOutput({ list }, options);
  assert.deepEqual(result.list, list);
  assert.deepEqual(result._meow.media, list.map((_, vodIndex) => ({ vodIndex, ...media })));
  for (const record of list) {
    const root = normalizeCatOutput(record, options);
    assert.deepEqual(root._meow.media, [media]);
    for (const [key, raw] of Object.entries(record)) assert.deepEqual(root[key], raw);
  }
});

const quarkShare = { provider: 'quark', shareId: 'output-quark-share', password: 'synthetic-code',
  url: 'https://pan.quark.cn/s/output-quark-share' };
const ucShare = { provider: 'uc', shareId: 'output-uc-share', password: '',
  url: 'https://drive.uc.cn/s/output-uc-share' };
const panOptions = { ...options, shares: [quarkShare, ucShare],
  settings: { providers: { quark: true, uc: true }, mappings: [] } };
const marker = share => 'meow_' + shareIdentity(share);
const reference = share => Buffer.from(JSON.stringify({ fid: marker(share), stoken: marker(share) }))
  .toString('base64');

test('Cat output binds exact captured shares independently of line names and keeps raw labels and indexes', () => {
  const input = { list: [{ vod_id: 'output-detail', vod_name: 'Original title',
    vod_play_from: '自定义线路$$$UC$$$另一个自定义线路',
    vod_play_url: [
      `第一季$${reference(quarkShare)}#预告$https://example.invalid/trailer.mp4`,
      `原始集名$${encodeURIComponent(reference(quarkShare))}`,
      `第二季$${marker(ucShare)}`,
    ].join('$$$') }] };
  const result = normalizeCatOutput(input, panOptions);
  assert.equal(result.list[0].vod_id, input.list[0].vod_id);
  assert.equal(result.list[0].vod_name, input.list[0].vod_name);
  assert.equal(result.list[0].vod_play_from, input.list[0].vod_play_from);
  const lines = result.list[0].vod_play_url.split('$$$');
  assert.equal(lines[0].split('#')[1], '预告$https://example.invalid/trailer.mp4');
  assert.deepEqual(result._meow.resources.map(row =>
    [row.provider, row.vodIndex, row.lineIndex, row.episodeIndex]),
  [['quark', 0, 0, 0], ['quark', 0, 1, 0], ['uc', 0, 2, 0]]);
  const labels = ['第一季', '原始集名', '第二季'];
  for (const [index, resource] of result._meow.resources.entries()) {
    assert.equal(resource.kind, 'pan');
    assert.equal(resource.installationId, options.installationId);
    assert.equal(resource.siteId, options.siteId);
    assert.equal(lines[index].split('#')[0], `${labels[index]}$${resource.id}`);
    assert.deepEqual(decodePanResource(resource.id), {
      ...(index === 2 ? ucShare : quarkShare),
      installationId: options.installationId, siteId: options.siteId,
    });
  }
  assert.deepEqual(input.list[0].vod_play_url, [
    `第一季$${reference(quarkShare)}#预告$https://example.invalid/trailer.mp4`,
    `原始集名$${encodeURIComponent(reference(quarkShare))}`,
    `第二季$${marker(ucShare)}`,
  ].join('$$$'), 'the guest result is not mutated');
});

test('Cat output does not bind absent or ambiguous capture markers to a line or an unrelated file', () => {
  const ambiguous = Buffer.from(JSON.stringify({ fid: marker(quarkShare), stoken: marker(ucShare) }))
    .toString('base64');
  const input = { list: [{ vod_play_from: '夸克$$$自定义线路$$$夸克',
    vod_play_url: `第一季$${ambiguous}$$$正片$https://example.invalid/movie.mp4$$$正片$opaque-file` }] };
  const result = normalizeCatOutput(input, panOptions);
  assert.deepEqual(result.list, input.list);
  assert.deepEqual(result._meow.resources, []);
  assert.equal(result._meow.captures.length, 2);
  const singleCapture = normalizeCatOutput({ list: [{ vod_play_from: '夸克',
    vod_play_url: '正片$opaque-file' }] }, { ...panOptions, shares: [quarkShare] });
  assert.equal(singleCapture.list[0].vod_play_url, '正片$opaque-file');
  assert.deepEqual(singleCapture._meow.resources, [], 'one capture alone is not file identity');
});

test('Cat output respects the captured provider switch and guest extension ownership', () => {
  const input = { list: [{ vod_play_from: 'UC', vod_play_url: `第一季$${reference(quarkShare)}` }] };
  const disabled = normalizeCatOutput(input, { ...panOptions,
    settings: { providers: { quark: false, uc: true }, mappings: [] } });
  assert.deepEqual(disabled.list, input.list);
  assert.deepEqual(disabled._meow.resources, [], 'another enabled alias cannot override captured identity');
  const guest = { ...input, _meow: { version: 99, guest: 'unchanged' } };
  assert.deepEqual(normalizeCatOutput(guest, panOptions), guest);
});

test('Cat output keeps direct share recognition by configured names without inventing unknown aliases', () => {
  const input = { list: [{ vod_play_from: '映射线路$$$自定义线路',
    vod_play_url: `第一季$${quarkShare.url}?pwd=synthetic-code$$$第二季$${ucShare.url}` }] };
  const result = normalizeCatOutput(input, { ...panOptions, shares: [],
    settings: { providers: { quark: true, uc: true },
      mappings: [{ provider: 'quark', aliases: ['映射线路'] }] } });
  assert.equal(result.list[0].vod_play_from, input.list[0].vod_play_from);
  assert.equal(result.list[0].vod_play_url.split('$$$')[1], `第二季$${ucShare.url}`);
  assert.equal(result._meow.resources.length, 1);
  assert.deepEqual(decodePanResource(result._meow.resources[0].id), {
    ...quarkShare, installationId: options.installationId, siteId: options.siteId,
  });
});
