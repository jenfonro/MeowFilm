import { createAppRuntime } from './bootstrap.mjs';
import { createNodeDebug, parseDebugHandshake, DEBUG_CATEGORIES } from './debug.mjs';
import { createMusicScriptModule } from './modules/music-script.mjs';
import { createCatScriptModule } from './modules/cat-script.mjs';
import { createSourceRuntimeModule } from './modules/source-runtime.mjs';
import { createPanRuntimeModule } from './modules/pan-runtime.mjs';
import { createTMDBDataModule, createDoubanDataModule } from './modules/metadata-data.mjs';
import { createFilmPlaybackModule } from './modules/film-playback.mjs';
import { createMusicPlatformModule } from './modules/music-platform.mjs';
import { createNeteaseTokenModule } from './modules/netease-token.mjs';
import { configureStandaloneStorage } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';

export { createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule, createPanRuntimeModule };
export { createPanRuntime, startPanRuntime } from './pan-entry.mjs';
export { createNeteaseTokenModule };
export { createTMDBDataModule, createDoubanDataModule, createFilmPlaybackModule };
export { createMusicPlatformModule };
export { createAppRuntime } from './bootstrap.mjs';
export { createRuntimeClient } from './client.mjs';
export { createNodeDebug, parseDebugHandshake, DEBUG_CATEGORIES };

const APP_SETTINGS_BLOCKS = new Set([
  'cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences',
]);

/** Account ownership stays with pan; Cat receives only a trusted live capability. */
export function connectPanAccounts(modules) {
  const cat = modules.find(module => module.id === 'cat');
  const pan = modules.find(module => module.id === 'pan');
  if (cat && pan) cat.ready = cat.connectAccounts(pan.accounts);
}

/** Reuses the existing token owner through its actual local module capability. */
export function connectMusicPlatform(modules, options) {
  const netease = modules.find(module => module.id === 'netease');
  const tokenProvider = netease ? { async get({ signal }) {
    const reply = await netease.methods['netease.token.get']({}, { signal });
    if (!reply.ok) throw Object.assign(new Error(reply.code), { code: reply.code });
    return reply.value.token;
  } } : undefined;
  const module = createMusicPlatformModule({ ...options, tokenProvider });
  modules.push(module);
  return module;
}

/** Assembly connects invalidation explicitly; provider modules do not import film business. */
export function connectFilmPlayback(modules, options) {
  const film = createFilmPlaybackModule({ ...options, modules });
  modules.find(module => module.id === 'cat')?.onAccountSaved?.(input => film.accountSaved(input));
  for (const module of modules) {
    const prefix = { cat: 'cat.script.', source: 'source.runtime.', pan: 'pan.',
      tmdb: 'tmdb.data.', douban: 'douban.data.' }[module.id];
    if (!prefix) continue;
    for (const [name, handler] of Object.entries(module.methods)) {
      const operation = name.slice(prefix.length);
      if (!film.observesMutation(module.id, operation)) continue;
      module.methods[name] = async (input, context) => {
        const before = await film.beforeMutation(module.id);
        let reply, failure;
        try { reply = await handler(input, context); } catch (error) { failure = error; }
        // A durable provider change may have committed even when its final
        // receipt failed. Compare actual state rather than assuming rollback.
        try { await film.afterMutation(module.id, operation, input, reply?.value, before, reply?.ok === true); }
        catch (error) { failure ??= error; }
        if (failure) throw failure;
        return reply;
      };
    }
  }
  modules.push(film);
  return film;
}

/** Shared nine-module App assembly used by both iOS and Windows aggregate entries. */
export async function assembleAppModules({
  dataRoot,
  settings = null,
  debug = null,
  platform,
  sourceConfigurationOwner = 'runtime',
  onStage,
}) {
  const modules = [];
  let stage = 'music';
  const mark = value => { stage = value; onStage?.(value); };
  try {
    mark('music');
    modules.push(createMusicScriptModule({ dataRoot, debug }));
    mark('cat');
    modules.push(createCatScriptModule({
      dataRoot,
      ...(settings ? {
        configuration: { read: () => settings.read('cat.sources'), reload: () => settings.reload('cat.sources') },
      } : {}),
      debug,
    }));
    mark('source');
    modules.push(createSourceRuntimeModule({
      dataRoot, platform, configurationOwner: sourceConfigurationOwner, debug,
    }));
    mark('pan');
    modules.push(createPanRuntimeModule({
      dataRoot,
      ...(settings ? { store: settings.accounts('pan.accounts') } : {}),
      debug,
    }));
    connectPanAccounts(modules);
    mark('tmdb');
    modules.push(createTMDBDataModule({ dataRoot, nativeSettings: settings, debug }));
    mark('douban');
    modules.push(createDoubanDataModule({ dataRoot, nativeSettings: settings, debug }));
    mark('film');
    connectFilmPlayback(modules, { dataRoot, nativeSettings: settings, debug });
    mark('netease');
    modules.push(createNeteaseTokenModule({ debug }));
    mark('music-platform');
    connectMusicPlatform(modules, { dataRoot, debug });
    const owners = new Map(modules.map(module => [module.id, module]));
    return {
      modules,
      owners,
      settingsChanged: settings ? createAppSettingsChanged(owners, settings, debug, platform) : undefined,
    };
  } catch (error) {
    debug?.emit({
      level: 'error',
      cat: 'runtime-node',
      sub: `${platform}.aggregate`,
      event: 'aggregate.assembly.error',
      data: { stage, dataRoot },
      error,
    });
    await Promise.allSettled(modules.map(module => Promise.resolve().then(() => module.close())));
    throw error;
  }
}

/** Shared settings change handler across iOS and Windows aggregate entries. */
export function createAppSettingsChanged(owners, settings, debug, platform) {
  return async block => {
    if (!APP_SETTINGS_BLOCKS.has(block)) {
      throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
    }
    debug?.emit({
      level: 'info',
      cat: 'runtime-bridge',
      sub: `${platform}.settings`,
      event: 'settings.changed',
      data: { block },
    });
    if (block === 'cat.sources') await owners.get('cat').settingsChanged();
    else if (block === 'pan.accounts') await owners.get('pan').settingsChanged();
    else {
      await settings.reload(block);
      if (block === 'tmdb' || block === 'douban') owners.get(block).settingsChanged();
    }
    await owners.get('film').settingsChanged(block);
  };
}

export function createRuntime({ dataRoot, music = {}, cat = {}, source = {}, pan, tmdb, douban, film, musicPlatform,
  debug: rawDebug, ...config } = {}) {
  configureStandaloneStorage(createDesktopStorageDriver);
  const debug = rawDebug !== undefined ? createNodeDebug(rawDebug) : null;
  return createAppRuntime({ ...config, debug, product: 'aggregate', assemble: () => {
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ ...music, dataRoot, debug }));
      modules.push(createCatScriptModule({ ...cat, dataRoot, debug }));
      modules.push(createSourceRuntimeModule({ ...source, dataRoot, debug }));
      if (pan !== undefined) modules.push(createPanRuntimeModule({ ...pan, dataRoot, debug }));
      connectPanAccounts(modules);
      if (tmdb !== undefined) modules.push(createTMDBDataModule({ ...tmdb, dataRoot, debug }));
      if (douban !== undefined) modules.push(createDoubanDataModule({ ...douban, dataRoot, debug }));
      if (film !== undefined) connectFilmPlayback(modules, { ...film, dataRoot, debug });
      if (musicPlatform !== undefined) connectMusicPlatform(modules, { ...musicPlatform, dataRoot, debug });
      return modules;
    } catch (error) {
      void Promise.allSettled(modules.map(module => Promise.resolve().then(() => module.close())));
      throw error;
    }
  } });
}
export async function startRuntime(config) { return createRuntime(config).start(); }
