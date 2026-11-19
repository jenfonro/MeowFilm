import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../../../runtime/modules/source-runtime.mjs';
import { createNeteaseTokenModule } from '../../../runtime/modules/netease-token.mjs';
import { createPanRuntimeModule } from '../../../runtime/modules/pan-runtime.mjs';
import { createTMDBDataModule, createDoubanDataModule } from '../../../runtime/modules/metadata-data.mjs';
import { connectFilmPlayback, connectMusicPlatform } from '../../../runtime/entry.mjs';

let stage = 'arguments', owners;
try {
  await startIOSAppRuntime({
    product: 'aggregate',
    onStage: value => { stage = value; },
    assemble: ({ dataRoot, settings }) => {
      const modules = [];
      try {
        stage = 'music';
        modules.push(createMusicScriptModule({ dataRoot }));
        stage = 'cat';
        modules.push(createCatScriptModule({ dataRoot, ...(settings ? {
          configuration: { read: () => settings.read('cat.sources'), reload: () => settings.reload('cat.sources') },
        } : {}) }));
        stage = 'source';
        modules.push(createSourceRuntimeModule({ dataRoot, platform: 'ios' }));
        stage = 'pan';
        modules.push(createPanRuntimeModule({ dataRoot, ...(settings ? { store: settings.accounts('pan.accounts') } : {}) }));
        stage = 'tmdb';
        modules.push(createTMDBDataModule({ dataRoot, nativeSettings: settings }));
        stage = 'douban';
        modules.push(createDoubanDataModule({ dataRoot, nativeSettings: settings }));
        stage = 'film';
        connectFilmPlayback(modules, { dataRoot, nativeSettings: settings });
        stage = 'netease';
        modules.push(createNeteaseTokenModule());
        stage = 'music-platform';
        connectMusicPlatform(modules, { dataRoot });
        owners = new Map(modules.map(module => [module.id, module]));
        return modules;
      } catch (error) {
        for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
        throw error;
      }
    },
    async settingsChanged(block, { settings }) {
      if (block === 'cat.sources') await owners.get('cat').settingsChanged();
      else if (block === 'pan.accounts') await owners.get('pan').settingsChanged();
      else {
        await settings.reload(block);
        if (block === 'tmdb' || block === 'douban') owners.get(block).settingsChanged();
      }
      await owners.get('film').settingsChanged(block);
    },
  });
} catch (error) {
  reportIOSRuntimeStartFailure(error, stage);
}
