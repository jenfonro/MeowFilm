import { createMusicPlatform } from '@meowfilm/music-platform';
import { contracts } from '../contracts.mjs';

export function createMusicPlatformModule({ dataRoot, clientStore, tokenProvider, ...options } = {}) {
  const owner = createMusicPlatform({ ...options, tokenProvider,
    dataRoot, clientStore });
  const spec = contracts['music-platform'];
  return {
    id: 'music-platform', ready: owner.ready, version: spec.version, contract: spec.contract,
    methods: {
      'music.platform.request': async (input, context) => ({ ok: true, contract: spec.contract,
        value: await owner.request(input, { signal: context?.signal }) }),
      'music.platform.session.update': async input => ({ ok: true, contract: spec.contract, value: await owner.sessionUpdate(input) }),
      'music.platform.cache.clear': input => ({ ok: true, contract: spec.contract, value: owner.clear(input) }),
      'music.platform.describe': () => ({ ok: true, contract: spec.contract, value: owner.describe() }),
    },
    close: () => owner.close(),
  };
}
