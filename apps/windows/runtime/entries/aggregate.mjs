import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';
import { configureStorage } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
import { createAppRuntime, createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule,
  createPanRuntimeModule, createTMDBDataModule, createDoubanDataModule, connectFilmPlayback,
  createNeteaseTokenModule, connectMusicPlatform } from '../../../runtime/entry.mjs';

// Current wire identity; one App, not a selectable aggregate product.
configureStorage({ driver: createDesktopStorageDriver, initialize: false });
await startWindowsAppRuntime({
  createRuntime: async ({ dataRoot, settings, ...config }) => {
    await settings?.initialize();
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ dataRoot }));
      modules.push(createCatScriptModule({ dataRoot, ...(settings ? {
        configuration: { read: () => settings.read('cat.sources'), reload: () => settings.reload('cat.sources') }
      } : {}) }));
      modules.push(createSourceRuntimeModule({ dataRoot, platform: 'windows', configurationOwner: 'native' }));
      modules.push(createPanRuntimeModule({ dataRoot, ...(settings ? { store: settings.accounts('pan.accounts') } : {}) }));
      modules.push(createTMDBDataModule({ dataRoot, nativeSettings: settings }));
      modules.push(createDoubanDataModule({ dataRoot, nativeSettings: settings }));
      connectFilmPlayback(modules, { dataRoot, nativeSettings: settings });
      modules.push(createNeteaseTokenModule());
      connectMusicPlatform(modules, { dataRoot });
      const owners = new Map(modules.map(module => [module.id, module]));
      return createAppRuntime({ ...config, product: 'aggregate', modules,
        ...(settings ? { async settingsChanged(block) {
          if (!['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences'].includes(block))
            throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
          if (block === 'cat.sources') await owners.get('cat').settingsChanged();
          else if (block === 'pan.accounts') await owners.get('pan').settingsChanged();
          else {
            await settings.reload(block);
            if (block === 'tmdb' || block === 'douban') owners.get(block).settingsChanged();
          }
          await owners.get('film').settingsChanged(block);
        } } : {}) });
    } catch (error) {
      for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
      throw error;
    }
  },
}).catch(reportWindowsRuntimeStartFailure);
