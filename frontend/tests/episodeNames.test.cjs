const { test } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('esbuild');

const compiled = buildSync({
  stdin: {
    contents: `
      export { extractRawNamesFromEpisodeUrl } from './src/shared/catpawrunner.js';
      export { buildSourceSegmentItems, buildDirectSiteEpisodeItems, buildPlaybackRecognitionData } from './src/shared/smartSourceRecognition.js';
    `,
    resolveDir: process.cwd(),
  },
  bundle: true, platform: 'node', format: 'cjs', write: false,
});
const loaded = { exports: {} };
new Function('module', 'exports', 'require', compiled.outputFiles[0].text)(loaded, loaded.exports, require);
const { extractRawNamesFromEpisodeUrl, buildSourceSegmentItems, buildDirectSiteEpisodeItems, buildPlaybackRecognitionData } = loaded.exports;

const settings = {
  magicEpisodeRules: [
    { pattern: String.raw`.*?([Ss]\d{1,2})?(?:第\s*(\d{1,4})\s*(?:集|话)|[Ee][Pp]?\s*(\d{1,4})(?:$|\D)).*?.*`, replace: '$1E$2$3', flags: 'i' },
    { pattern: String.raw`^[\s\[\]\(\){}【】._=-]*0*(\d{1,4})[\s\[\]\(\){}【】._=-]*(?:\.[A-Za-z0-9]{1,6})?\s*$`, replace: 'E$1', flags: 'i' },
  ],
  magicEpisodeCleanRegexRules: [String.raw`\[(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\])[^\]]*\]`],
};

test('media metadata locators never override numbered titles in direct or projected playback', () => {
  const entry = {
    key: 'media', label: 'arbitrary-site', provider: '',
    episodeSegments: Array.from({ length: 25 }, (_, index) => {
      const video = index === 3 ? 'tmPB4oe65DE' : 'video';
      return `节目.S01E${String(index + 1).padStart(2, '0')}$https://image.example/cover*Author*1:26:05****watch?v=${video}&list=RDWq_VrOPe5uQ`;
    }),
  };
  const segments = buildSourceSegmentItems(entry);
  assert.ok(segments.every(item => item.rawName === '' && item.fileName === ''));
  const items = buildDirectSiteEpisodeItems(entry, settings);
  assert.deepEqual(items.map(item => item.no), Array.from({ length: 25 }, (_, i) => i + 1));
  assert.ok(items.every(item => item.season === 1));
  assert.deepEqual(items.map(item => item.itemIndex), Array.from({ length: 25 }, (_, i) => i));
  const recognized = buildPlaybackRecognitionData({
    entry, runtimeSettings: settings,
    smartEpisodeMapping: { tmdbSeasons: [{ season: 1, episodeCount: 25 }], totalEpisodes: 25 },
  });
  for (let index = 0; index < 25; index += 1) {
    const matches = recognized.items.filter(item => item.itemIndex === index && item.matchKind === 'episode');
    assert.ok(matches.length > 0);
    assert.ok(matches.every(item => item.extracted.season === 1 && item.extracted.episode === index + 1));
    assert.equal(items[index].segmentIdentity, entry.episodeSegments[index]);
  }
});

test('media metadata recognition does not depend on site name, locator, or empty optional fields', () => {
  for (const fields of [
    ['https://image.example/cover', 'author', '1:20', '', '', '', 'opaque-E65'],
    ['HTTP://image.example/cover', 'author', '1:20', 'likes', 'date', 'region', 'https://media.example/E65'],
    ['https://image.example/cover', '', '', '', '', '', 'play/E65'],
  ]) {
    const id = fields.join('*');
    assert.deepEqual(extractRawNamesFromEpisodeUrl(id), []);
    assert.deepEqual(buildDirectSiteEpisodeItems({ key: 'site', episodeSegments: [`无集数标题$${id}`] }, settings), []);
  }
});

const panIds = {
  quark: name => `shareId*stoken*fid*fidToken***${name}`,
  uc: name => `shareId*stoken*fid*fidToken***${name}`,
  '139': name => `contentId*linkID***${name}`,
  baidu: name => `${Buffer.from(JSON.stringify({ shareid: '123', uk: '456', fs_id: '789', realName: name })).toString('base64')}|||${name}`,
  '189': name => `fileId*shareId*${name}`,
};
const panCases = [
  { name: 'S01E01', dir: '/', season: 1, no: 1 },
  { name: '01', dir: '/第2季', season: 2, no: 1 },
  { name: '第十二集', dir: '/第2季', season: 2, no: 12 },
  { name: '剧名 S03E07.mkv', dir: '/作品', season: 3, no: 7 },
  { name: 'EP09', dir: '/第2季', season: 2, no: 9 },
  { name: 'E12', dir: '@4K/作品/第2季', season: 2, no: 12, quality: '4K' },
  { name: '剧名 S01E01.1080p.mkv', dir: '/', season: 1, no: 1, quality: '1080P' },
  { name: 'S02E03.未知后缀', dir: '/', season: 2, no: 3 },
  { name: '宣传片', dir: '/作品', no: 0 },
  { name: '第十三集', dir: '/作品/第2季', season: 2, no: 13 },
];

for (const [provider, encode] of Object.entries(panIds)) {
  for (const sample of panCases) {
    test(`${provider} preserves filename, path, and playback ID: ${sample.name}`, () => {
      const id = encode(sample.name);
      const entry = { key: provider, provider, episodeSegments: [`${sample.dir}$${id}`] };
      assert.deepEqual(extractRawNamesFromEpisodeUrl(id), [sample.name]);
      const segment = buildSourceSegmentItems(entry)[0];
      assert.equal(segment.fileName, sample.name);
      assert.equal(segment.episodeUrl, id);
      assert.equal(segment.allowDirectoryHints, true);
      assert.equal(segment.currentPath, sample.dir.replace(/^@4K/, '').replace(/^\/|\/$/g, ''));
      const items = buildDirectSiteEpisodeItems(entry, settings);
      if (!sample.no) {
        assert.equal(items.length, 0);
        return;
      }
      assert.equal(items.length, 1);
      assert.equal(items[0].season, sample.season);
      assert.equal(items[0].no, sample.no);
      assert.equal(items[0].quality, sample.quality || '');
      assert.equal(items[0].segmentIdentity, entry.episodeSegments[0]);
    });
  }
}

test('legacy named suffixes and paths still work without file extensions', () => {
  for (const id of ['opaque***目录/S01E01', 'opaque|||目录/S01E01', 'a|b|c|目录/S01E01', 'file*share*目录/S01E01', 'https://media.example/video***目录/S01E01']) {
    assert.deepEqual(extractRawNamesFromEpisodeUrl(id), ['目录/S01E01']);
  }
  assert.equal(panIds.quark('S01E01').split('*').length, 7);
  assert.deepEqual(extractRawNamesFromEpisodeUrl(panIds.quark('S01E01')), ['S01E01']);
});
