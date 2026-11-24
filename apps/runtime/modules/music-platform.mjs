import { createMusicPlatform, MusicPlatformError } from '@meowfilm/music-platform';
import { contracts } from '../contracts.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requireObject = value => {
  if (!object(value)) throw new MusicPlatformError('INVALID_REQUEST');
  return value;
};

export function createMusicPlatformModule({ dataRoot, tokenProvider, debug } = {}) {
  const owner = createMusicPlatform({ dataRoot, tokenProvider, debug });
  const spec = contracts['music-platform'];
  return {
    id: 'music-platform', ready: owner.ready, version: spec.version, contract: spec.contract,
    diagnostics: () => owner.diagnostics(),
    clearMemoryCache: () => owner.clearMemory(),
    methods: {
      'music.platform.request': async (input, context) => ({ ok: true, contract: spec.contract,
        value: await owner.request(requireObject(input), { signal: context?.signal }) }),
      'music.platform.session.update': async input => ({ ok: true, contract: spec.contract,
        value: await owner.sessionUpdate(requireObject(input)) }),
      'music.platform.cache.clear': input => ({ ok: true, contract: spec.contract,
        value: owner.clear(requireObject(input)) }),
      'music.platform.describe': (input = {}) => {
        requireObject(input);
        return { ok: true, contract: spec.contract, value: owner.describe() };
      },
    },
    close: () => owner.close(),
  };
}
