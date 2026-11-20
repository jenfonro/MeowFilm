import test from 'node:test';
import assert from 'node:assert/strict';
import { decodePanResource, normalizeCatOutput, panProvider, shareIdentity } from '../src/cat-output.js';

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
    'Infinity', '-Infinity', 'NaN',
    true, false, [], [3840], {}, '', '  ', null,
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
  const input = { resolution: 'provider-original', video_meta: { width: 'Infinity', height: true, fps: 24 } };
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
    true, false, [], [60], {}, { fps: 60 }, null, '', '  ', 'bad',
    'NaN', 'Infinity', '-Infinity', 0, -1, '0', '-1', '60/0',
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

test('Cat output binds captures, merges quality aliases and keeps unrelated media', () => {
  const input = { list: [{ vod_id: 'output-detail', vod_name: 'Original title',
    vod_play_from: '夸克原画#01$$$UC$$$另一个自定义线路',
    vod_play_url: [
      `第一季$${reference(quarkShare)}#预告$https://example.invalid/trailer.mp4`,
      `原始集名$${encodeURIComponent(reference(quarkShare))}`,
      `第二季$${marker(ucShare)}`,
    ].join('$$$') }] };
  const original = structuredClone(input);
  const result = normalizeCatOutput(input, panOptions);
  assert.equal(result.list[0].vod_id, input.list[0].vod_id);
  assert.equal(result.list[0].vod_name, input.list[0].vod_name);
  assert.equal(result.list[0].vod_play_from,
    `夸克-${quarkShare.shareId}$$$夸克原画#01$$$UC-${ucShare.shareId}`);
  const lines = result.list[0].vod_play_url.split('$$$');
  assert.equal(lines[1], '预告$https://example.invalid/trailer.mp4');
  assert.deepEqual(result._meow.resources.map(row =>
    [row.provider, row.vodIndex, row.lineIndex, row.episodeIndex]),
  [['quark', 0, 0, 0], ['uc', 0, 2, 0]]);
  for (const [index, resource] of result._meow.resources.entries()) {
    assert.equal(resource.kind, 'pan');
    assert.equal(resource.installationId, options.installationId);
    assert.equal(resource.siteId, options.siteId);
    assert.equal(lines[resource.lineIndex].split('$')[1], resource.id);
    assert.deepEqual(decodePanResource(resource.id), {
      ...(index === 1 ? ucShare : quarkShare),
      installationId: options.installationId, siteId: options.siteId,
    });
  }
  assert.deepEqual(input, original, 'the guest result is not mutated');
});

test('Cat output rejects unassigned opaque IDs instead of guessing a captured share from aliases', () => {
  const input = { list: [{ vod_play_from: '夸克原画#01$$$自定义线路$$$夸克极速#01',
    vod_play_url: '正片$opaque-first$$$正片$https://example.invalid/movie.mp4$$$正片$opaque-second' }] };
  assert.throws(() => normalizeCatOutput(input, panOptions), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const empty = normalizeCatOutput({ list: [{ vod_id: 'empty-detail' }] },
    { ...panOptions, operation: 'detail' });
  assert.equal(empty._meow.resources.length, 2, 'a script with no playback lines still returns captured entrances');
  const catalog = normalizeCatOutput({ list: [{ vod_id: 'catalog-card' }] },
    { ...panOptions, operation: 'search' });
  assert.equal(catalog.list[0].vod_play_url, undefined, 'catalog cards are not invented playback details');
});

test('Cat output keeps multiple captured shares separate without assigning one file to another share', () => {
  const another = { ...quarkShare, shareId: 'another-quark-share', url: 'https://pan.quark.cn/s/another-quark-share' };
  const raw = Buffer.from(JSON.stringify({ fid: marker(quarkShare), stoken: marker(another) })).toString('base64');
  assert.throws(() => normalizeCatOutput({ list: [{ vod_play_from: '夸克原画', vod_play_url: `正片$${raw}` }] },
    { ...panOptions, shares: [quarkShare, another] }), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const batch = normalizeCatOutput({ list: [
    { vod_id: 'first', vod_play_from: '普通线路', vod_play_url: '正片$opaque-first' },
    { vod_id: 'second', vod_play_from: 'UC', vod_play_url: `正片$${reference(ucShare)}` },
  ] }, panOptions);
  assert.equal(batch.list[0].vod_play_url, '正片$opaque-first');
  assert.deepEqual(batch._meow.resources.map(row => [row.vodIndex, row.provider]), [[1, 'uc']],
    'unassigned captures cannot be copied to every detail in a batch');
});

test('Cat output respects provider switches and guest extension ownership', () => {
  const input = { list: [{ vod_play_from: 'UC', vod_play_url: `第一季$${reference(quarkShare)}` }] };
  assert.throws(() => normalizeCatOutput(input, { ...panOptions, shares: [quarkShare],
    settings: { providers: { quark: false, uc: true }, mappings: [] } }),
  { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const guest = { ...input, _meow: { version: 99, guest: 'unchanged' } };
  assert.throws(() => normalizeCatOutput(guest, panOptions), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const raw = { list: [{ vod_play_from: '夸克原画#01$$$夸克极速#01',
    vod_play_url: `原画$${quarkShare.url}$$$极速$opaque-original-id` }] };
  assert.deepEqual(normalizeCatOutput(raw, options), raw, 'disabled routing preserves script play flags and IDs');
  const emptyLines = { list: [{ vod_play_from: '$$$夸克原画$$$', vod_play_url: '$$$正片$opaque$$$' }] };
  assert.deepEqual(normalizeCatOutput(emptyLines, options), emptyLines,
    'disabled routing preserves empty original line slots');
});

test('Cat aliases use case-insensitive containment with configured aliases before built-in aliases', () => {
  for (const [label, expected] of [
    ['夸克原画#01', 'quark'], ['夸克极速#01', 'quark'], ['百度原画(无限)#01', 'baidu'],
    ['UC原画#1', 'uc'], ['天意高速', 'tianyi'], ['移动云盘', 'yidong'], ['QuArK原画', 'quark'],
    ['站点私有别名-极速', 'uc'], ['站点-百度源', 'baidu'], ['不支持的网盘', undefined],
  ]) assert.equal(panProvider(label, { mappings: [{ provider: 'uc', aliases: ['', '私有别名'] }] }), expected, label);
  assert.equal(panProvider('夸克原画', { mappings: [{ provider: 'uc', aliases: ['夸克原画'] }] }), 'uc');
});

test('direct URLs identify shares independently of aliases and deduplicate without losing passwords', () => {
  const result = normalizeCatOutput({ list: [{
    vod_play_from: '夸克原画#01$$$夸克极速#01$$$自定义线路',
    vod_play_url: `原画$${quarkShare.url}$$$极速$${quarkShare.url}?pwd=synthetic-code$$$正片$${ucShare.url}`,
  }] }, { ...panOptions, shares: [] });
  assert.equal(result.list[0].vod_play_from, `夸克-${quarkShare.shareId}$$$UC-${ucShare.shareId}`);
  assert.deepEqual(result._meow.resources.map(row => decodePanResource(row.id)),
    [quarkShare, ucShare].map(share => ({ ...share, installationId: options.installationId, siteId: options.siteId })));
});

test('captured explicit empty passwords override stale passwords in the script output', () => {
  const result = normalizeCatOutput({ list: [{ vod_play_from: '夸克',
    vod_play_url: `正片$${quarkShare.url}?pwd=stale-password` }] },
  { ...panOptions, shares: [{ ...quarkShare, password: '' }] });
  assert.equal(decodePanResource(result._meow.resources[0].id).password, '');
});

test('a captured marker in a filename survives scripts that only output numeric file IDs', () => {
  const result = normalizeCatOutput({ list: [{ vod_play_from: '不透明线路',
    vod_play_url: `${marker(quarkShare)}.mp4$123456789` }] },
  { ...panOptions, shares: [quarkShare] });
  assert.equal(result.list[0].vod_play_from, `夸克-${quarkShare.shareId}`);
  assert.equal(result._meow.resources.length, 1);
});

test('malformed mismatched line arrays stay invalid rather than silently dropping data', () => {
  const input = { list: [{ vod_play_from: '夸克$$$UC', vod_play_url: '正片$opaque' }] };
  const result = normalizeCatOutput(input, panOptions);
  assert.deepEqual(result.list, input.list);
  assert.equal(result._meow.resources.length, 0);
});

test('mobile hash share URLs retain their share code and password beside ordinary episodes', () => {
  const input = { list: [{ vod_play_from: '移动原画',
    vod_play_url: '正片$https://yun.139.com/#/w/i/mobile-share?passwd=code#预告$https://example.invalid/trailer.mp4' }] };
  const result = normalizeCatOutput(input, { ...options,
    settings: { providers: { yidong: true }, mappings: [] } });
  assert.deepEqual(decodePanResource(result._meow.resources[0].id), {
    provider: 'yidong', shareId: 'mobile-share', password: 'code',
    url: 'https://caiyun.139.com/m/i?mobile-share', installationId: options.installationId, siteId: options.siteId,
  });
  assert.equal(result.list[0].vod_play_url.split('$$$')[1], '预告$https://example.invalid/trailer.mp4');
});

test('captured aliases reject a second opaque pan path after the exact share was handed off', () => {
  for (const flag of ['夸克极速#01', 'PrivateAlias极速']) {
    const input = { list: [{ vod_play_from: '夸克原画$$$' + flag,
      vod_play_url: `正片$${reference(quarkShare)}$$$正片$opaque-play-id` }] };
    assert.throws(() => normalizeCatOutput(input, { ...panOptions, shares: [quarkShare],
      settings: { providers: { quark: true }, mappings: [{ provider: 'quark', aliases: ['PrivateAlias'] }] } }),
    { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  }
  const ordinary = normalizeCatOutput({ list: [{ vod_play_from: '夸克原画$$$普通线路',
    vod_play_url: `正片$${reference(quarkShare)}$$$预告$ordinary-id` }] }, { ...panOptions, shares: [quarkShare] });
  assert.equal(ordinary.list[0].vod_play_from, `夸克-${quarkShare.shareId}$$$普通线路`);
  assert.equal(ordinary.list[0].vod_play_url.split('$$$')[1], '预告$ordinary-id');
});
