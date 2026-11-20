import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../../../runtime/modules/source-runtime.mjs';
import { createNeteaseTokenModule } from '../../../runtime/modules/netease-token.mjs';
import { createPanRuntimeModule } from '../../../runtime/modules/pan-runtime.mjs';
import { createTMDBDataModule, createDoubanDataModule } from '../../../runtime/modules/metadata-data.mjs';
import { connectFilmPlayback, connectMusicPlatform, connectPanAccounts } from '../../../runtime/entry.mjs';

let stage = 'arguments', owners;
try {
  await startIOSAppRuntime({
    product: 'aggregate',
    onStage: value => { stage = value; },
    assemble: ({ dataRoot, settings, debug, onStage }) => {
      const markStage = value => { stage = value; onStage?.(value); };
      const modules = [];
      try {
        markStage('music');
        modules.push(createMusicScriptModule({ dataRoot, debug }));
        markStage('cat');
        modules.push(createCatScriptModule({ dataRoot, ...(settings ? {
          configuration: { read: () => settings.read('cat.sources'), reload: () => settings.reload('cat.sources') },
        } : {}), debug }));
        markStage('source');
        modules.push(createSourceRuntimeModule({ dataRoot, platform: 'ios', debug }));
        markStage('pan');
        modules.push(createPanRuntimeModule({ dataRoot, ...(settings ? { store: settings.accounts('pan.accounts') } : {}), debug }));
        connectPanAccounts(modules);
        markStage('tmdb');
        modules.push(createTMDBDataModule({ dataRoot, nativeSettings: settings, debug }));
        markStage('douban');
        modules.push(createDoubanDataModule({ dataRoot, nativeSettings: settings, debug }));
        markStage('film');
        connectFilmPlayback(modules, { dataRoot, nativeSettings: settings, debug });
        markStage('netease');
        modules.push(createNeteaseTokenModule({ debug }));
        markStage('music-platform');
        connectMusicPlatform(modules, { dataRoot, debug });
        owners = new Map(modules.map(module => [module.id, module]));
        return modules;
      } catch (error) {
        debug?.emit({ level: 'error', cat: 'runtime-node', sub: 'ios.aggregate', event: 'aggregate.assembly.error', data: { stage, dataRoot }, error });
        for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
        throw error;
      }
    },
    async settingsChanged(block, { settings, debug }) {
      debug?.emit({ level: 'info', cat: 'runtime-bridge', sub: 'ios.settings', event: 'settings.changed', data: { block } });
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
