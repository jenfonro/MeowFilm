import { join } from 'node:path';
import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../../../runtime/modules/source-runtime.mjs';
import { createNeteaseTokenModule } from '../../../runtime/modules/netease-token.mjs';
import { createPanRuntimeModule } from '../../../runtime/modules/pan-runtime.mjs';
import { createTMDBDataModule, createDoubanDataModule } from '../../../runtime/modules/metadata-data.mjs';
import { connectFilmPlayback } from '../../../runtime/entry.mjs';

let stage = 'arguments';
try {
  await startIOSAppRuntime({
    product: 'aggregate',
    onStage: value => { stage = value; },
    assemble: ({ dataRoot }) => {
      const modules = [];
      try {
        stage = 'music';
        modules.push(createMusicScriptModule({ dataRoot }));
        stage = 'cat';
        modules.push(createCatScriptModule({ dataRoot: join(dataRoot, 'cat') }));
        stage = 'source';
        modules.push(createSourceRuntimeModule({ dataRoot, platform: 'ios' }));
        stage = 'pan';
        modules.push(createPanRuntimeModule({ dataRoot }));
        stage = 'tmdb';
        modules.push(createTMDBDataModule({ dataRoot }));
        stage = 'douban';
        modules.push(createDoubanDataModule({ dataRoot }));
        stage = 'film';
        connectFilmPlayback(modules, { dataRoot });
        stage = 'netease';
        modules.push(createNeteaseTokenModule());
        return modules;
      } catch (error) {
        for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
        throw error;
      }
    },
  });
} catch (error) {
  reportIOSRuntimeStartFailure(error, stage);
}
