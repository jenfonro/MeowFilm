import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';
import { configureStorage, setStorageObserver } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
import { createAppRuntime, createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule,
  createPanRuntimeModule, createTMDBDataModule, createDoubanDataModule, connectFilmPlayback,
  createNeteaseTokenModule, connectMusicPlatform, connectPanAccounts } from '../../../runtime/entry.mjs';

// Current wire identity; one App, not a selectable aggregate product.
configureStorage({ driver: createDesktopStorageDriver, initialize: false });
await startWindowsAppRuntime({
  createRuntime: async ({ dataRoot, settings, debug, ...config }) => {
    if (debug) setStorageObserver(debug);
    const modules = [];
    try {
      await settings?.initialize();
      modules.push(createMusicScriptModule({ dataRoot, debug }));
      modules.push(createCatScriptModule({ dataRoot, ...(settings ? {
        configuration: { read: () => settings.read('cat.sources'), reload: () => settings.reload('cat.sources') }
      } : {}), debug }));
      modules.push(createSourceRuntimeModule({ dataRoot, platform: 'windows', configurationOwner: 'native', debug }));
      modules.push(createPanRuntimeModule({ dataRoot, ...(settings ? { store: settings.accounts('pan.accounts') } : {}), debug }));
      connectPanAccounts(modules);
      modules.push(createTMDBDataModule({ dataRoot, nativeSettings: settings, debug }));
      modules.push(createDoubanDataModule({ dataRoot, nativeSettings: settings, debug }));
      connectFilmPlayback(modules, { dataRoot, nativeSettings: settings, debug });
      modules.push(createNeteaseTokenModule({ debug }));
      connectMusicPlatform(modules, { dataRoot, debug });
      const owners = new Map(modules.map(module => [module.id, module]));
      return createAppRuntime({ ...config, product: 'aggregate', modules, debug,
        ...(settings ? { async settingsChanged(block) {
          if (!['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences'].includes(block))
            throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
          debug?.emit({ level: 'info', cat: 'runtime-bridge', sub: 'windows.settings', event: 'settings.changed', data: { block } });
          if (block === 'cat.sources') await owners.get('cat').settingsChanged();
          else if (block === 'pan.accounts') await owners.get('pan').settingsChanged();
          else {
            await settings.reload(block);
            if (block === 'tmdb' || block === 'douban') owners.get(block).settingsChanged();
          }
          await owners.get('film').settingsChanged(block);
        } } : {}) });
    } catch (error) {
      debug?.emit({ level: 'error', cat: 'runtime-node', sub: 'windows.aggregate', event: 'aggregate.assembly.error', data: { dataRoot }, error });
      await Promise.allSettled(modules.map(async module => { await module.close(); }));
      if (debug) setStorageObserver(null);
      throw error;
    }
  },
}).catch(reportWindowsRuntimeStartFailure);
