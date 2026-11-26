export const contracts = Object.freeze({
  'music-platform': { contract: 'music.platform.v1', version: '0.1.0', prefix: 'music.platform.' },
  netease: { contract: 'netease.token.v1', version: '0.1.0', prefix: 'netease.token.' },
  film: { contract: 'film.playback.v1', version: '0.1.0', prefix: 'film.' },
  pan: {
    contract: 'pan.runtime.v1', version: '0.1.0', prefix: 'pan.',
    operations: ['providers', 'settings.get', 'settings.set', 'settings.clear',
      'login.start', 'login.poll', 'login.cancel', 'list', 'prepare', 'play', 'refresh'],
  },
  tmdb: { contract: 'tmdb.data.v1', version: '0.1.0', prefix: 'tmdb.data.' },
  douban: { contract: 'douban.data.v1', version: '0.1.0', prefix: 'douban.data.' },
  music: { contract: 'music.script.v3', version: '0.8.0', prefix: 'music.script.', upload: true },
  source: { contract: 'source.runtime.v2', version: '0.3.0', prefix: 'source.runtime.', upload: true },
  cat: { contract: 'cat.script.v2', version: '0.2.0', prefix: 'cat.script.', upload: true },
});
