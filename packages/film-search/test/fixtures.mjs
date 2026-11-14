// Fixed inputs shared by the public-behavior tests and the real reference runner.
export const rules = [String.raw`\[[^\]]*\]`, String.raw`\d+$`, '/国语/ig', '['];

export const sources = [
  { siteKey: '["install-a","shared"]', siteId: 'shared', installationId: 'install-a', generation: 7, siteName: '甲源', order: 0 },
  { siteKey: '["install-b","shared"]', siteId: 'shared', installationId: 'install-b', generation: 11, siteName: '乙源', order: 1 },
];

export const siteCases = [
  {
    name: 'season, yearbang, emoji, tail-rule probes and aliases',
    query: '星河2',
    aggregateRules: rules,
    site: sources[0],
    page: 1,
    payload: { pagecount: '3', list: [
      { vod_id: 'a', vod_name: ' ✨星河 第二季[高清] ', vod_pic: ' /poster/a ', vod_remarks: '第2季第9集' },
      { vod_id: 'b', vod_name: '星河2', vod_remarks: '更新至12集' },
      { vod_id: 'c', vod_name: '星河 年番三', vod_remarks: '12/24' },
      { vod_id: 'd', vod_name: '星河 Season 2', vod_remarks: 'S2 E10' },
      { vod_id: 'e', vod_name: '星河 第１２季', vod_remarks: '国语' },
      { vod_id: 'f', vod_name: '星河 S02 E11', vod_remarks: '全集' },
      { vod_id: 'g', vod_name: '\u200b星 河.2', vod_remarks: '第十季' },
      { vod_id: 'h', vod_name: '无关作品', vod_remarks: '年番' },
    ] },
  },
  {
    name: 'field aliases, invalid rows, zero IDs and unchanged raw text',
    query: 'alpha beta',
    aggregateRules: [],
    site: sources[1],
    page: 2,
    payload: { pageCount: 2, list: [
      { id: 0, name: ' Alpha beta ', pic: '/zero', remark: '更新至3集' },
      { id: 'x', name: 'Alpha extra beta', remark: '第2季第4集' },
      { id: 'y', name: 'A beta film', remark: '更新至5集' },
      { id: 'z', name: '0分亦保留', pic: '/z' },
      { vod_id: 'priority', id: 'ignored', vod_name: ' alpha.beta ', name: 'ignored', vod_pic: '/priority', pic: '/ignored' },
      null, { id: 'missing' }, { name: 'missing' }, { id: '', name: 'empty' },
      { vod_id: 'empty-primary', vod_name: '', name: 'not a fallback' },
    ] },
  },
  {
    name: 'punctuation, invalid rules, global reset and matching score',
    query: 'Test',
    aggregateRules: ['/[国语]/ig', '[', '/\\s+/g'],
    site: sources[0],
    page: 1,
    payload: { list: [
      { id: 'a', name: 'test' }, { id: 'b', name: 'test suffix' },
      { id: 'c', name: 'A test title' }, { id: 'd', name: 'unrelated' },
      { id: 'e', name: 'Test,·Film—国语' }, { id: 'f', name: 'Test 第三季' },
    ] },
  },
  {
    name: 'empty list',
    query: '星河', aggregateRules: [], site: sources[0], page: 1, payload: { list: [] },
  },
];

export const tmdbCases = [
  {
    name: 'type plus normalized title dedupe, first match, original rank and numeric IDs',
    query: '星河2',
    payload: { results: [
      { id: 10, media_type: 'tv', name: '星河 第2季', poster_path: '/tv', backdrop_path: '/back', first_air_date: '2021-04-01' },
      { id: 11, media_type: 'tv', name: '星河', first_air_date: '2024-01-01' },
      { id: 12, media_type: 'movie', title: '星河2', release_date: '2023-01-01', poster_path: '/movie' },
      { id: 13, media_type: 'movie', title: '星河', release_date: '2025-01-01' },
      { id: 14, media_type: 'tv', original_name: '银河日记', first_air_date: '2010-01-01' },
      { id: 15, media_type: 'person', name: '星河2' },
      { id: '16', media_type: 'movie', title: 'numeric string is invalid' },
      { id: -1, media_type: 'tv', name: 'invalid' },
      { id: 17, media_type: 'tv', name: '' },
      { id: 18, media_type: 'movie', original_title: 'Origin', poster_path: '/origin' },
      null,
    ] },
  },
  {
    name: 'raw multiword token hits take precedence over canonical query scoring',
    query: '星河 第二季',
    payload: { results: [
      { id: 21, media_type: 'tv', name: '星河番外' },
      { id: 22, media_type: 'movie', title: '星河' },
      { id: 23, media_type: 'tv', name: '星河' },
      { id: 24, media_type: 'tv', name: '完全不相关' },
    ] },
  },
  {
    name: 'canonical TV scoring is capped unless exact, movies do not use alias fallback',
    query: '星河第二季',
    payload: { results: [
      { id: 21, media_type: 'tv', name: '星河番外' },
      { id: 22, media_type: 'movie', title: '星河' },
      { id: 23, media_type: 'tv', name: '星河' },
      { id: 24, media_type: 'tv', name: '完全不相关' },
    ] },
  },
  {
    name: 'movie punctuation and year do not create extra groups',
    query: 'Movie 2',
    payload: { results: [
      { id: 31, media_type: 'movie', title: 'Movie 2', release_date: '1999-01-01' },
      { id: 32, media_type: 'movie', title: 'Movie.2', release_date: '2026-01-01' },
      { id: 33, media_type: 'movie', title: 'Movie', release_date: '2000-01-01' },
      { id: 34, media_type: 'tv', name: 'Movie 2' },
    ] },
  },
  { name: 'missing results', query: '星河', payload: {} },
];

export const fallbackCases = [
  { query: ' 星河 2 ', expected: '星河' },
  { query: '星河12', expected: '星河' },
  { query: '星河123', expected: '星河1' },
  { query: '星河１２', expected: '' },
  { query: '星河 Season 2', expected: '星河 Season' },
  { query: ' 2 ', expected: '' },
  { query: '星河', expected: '' },
  { query: '', expected: '' },
];

export const detailCases = [
  {
    name: 'movie detail replaces title and images and supplies year',
    type: 'movie', today: '2026-11-08',
    payload: { original_title: 'Detail movie', poster_path: '/detail', backdrop_path: '/detail-back', release_date: '2005-08-09' },
    badge: '2005',
  },
  {
    name: 'ended TV counts only non-special seasons with episodes',
    type: 'tv', today: '2026-11-08',
    payload: { name: '终结剧集', status: 'Ended', seasons: [
      { season_number: 0, episode_count: 8 }, { season_number: 2, episode_count: 9 },
      { season_number: 1, episode_count: 10 }, { season_number: 3, episode_count: 0 },
    ] },
    badge: '共2季19集',
  },
  {
    name: 'next episode airing today counts as aired',
    type: 'tv', today: '2026-11-08',
    payload: { status: 'Returning Series', seasons: [
      { season_number: 1, episode_count: 10 }, { season_number: 2, episode_count: 10 },
    ], next_episode_to_air: { season_number: 2, episode_number: 4, air_date: '2026-11-08' } },
    badge: '更新至第2季第4集',
  },
  {
    name: 'future next episode subtracts one',
    type: 'tv', today: '2026-11-08',
    payload: { status: 'Continuing', seasons: [
      { season_number: 1, episode_count: 10 }, { season_number: 2, episode_count: 10 },
    ], next_episode_to_air: { season_number: 2, episode_number: 4, air_date: '2026-11-09' } },
    badge: '更新至第2季第3集',
  },
  {
    name: 'continuing TV without next episode uses last ordinary season',
    type: 'tv', today: '2026-11-08',
    payload: { status: 'In Production', seasons: [
      { season_number: 1, episode_count: 6 }, { season_number: 2, episode_count: 7 },
    ] },
    badge: '更新至第2季第7集',
  },
  { name: 'single ended season', type: 'tv', today: '2026-11-08', payload: { status: 'Ended', seasons: [{ season_number: 1, episode_count: 8 }] }, badge: '共8集' },
  { name: 'ended without ordinary episodes', type: 'tv', today: '2026-11-08', payload: { status: 'Ended' }, badge: '' },
  { name: 'nonempty unrecognized status', type: 'tv', today: '2026-11-08', payload: { status: 'Planned' }, badge: '持续更新' },
  { name: 'missing status', type: 'tv', today: '2026-11-08', payload: {}, badge: '' },
  { name: 'missing detail retains search item', type: 'tv', today: '2026-11-08', payload: null, badge: '' },
];
