import { createAppRuntime } from './bootstrap.mjs';
import { createMusicScriptModule } from './modules/music-script.mjs';
import { createCatScriptModule } from './modules/cat-script.mjs';
import { createSourceRuntimeModule } from './modules/source-runtime.mjs';
import { createPanRuntimeModule } from './modules/pan-runtime.mjs';
import { createTMDBDataModule, createDoubanDataModule } from './modules/metadata-data.mjs';
import { createFilmPlaybackModule } from './modules/film-playback.mjs';

export { createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule, createPanRuntimeModule };
export { createPanRuntime, startPanRuntime } from './pan-entry.mjs';
export { createNeteaseTokenModule } from './modules/netease-token.mjs';
export { createTMDBDataModule, createDoubanDataModule, createFilmPlaybackModule };
export { createAppRuntime } from './bootstrap.mjs';
export { createRuntimeClient } from './client.mjs';

/** Assembly connects invalidation explicitly; provider modules do not import film business. */
export function connectFilmPlayback(modules, options) {
  const film = createFilmPlaybackModule({ ...options, modules });
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

export function createRuntime({ dataRoot, music = {}, cat = {}, source = {}, pan, tmdb, douban, film,
  ...config } = {}) {
  return createAppRuntime({ ...config, product: 'aggregate', assemble: () => {
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ ...music, dataRoot }));
      modules.push(createCatScriptModule({ ...cat, dataRoot }));
      modules.push(createSourceRuntimeModule(source));
      if (pan !== undefined) modules.push(createPanRuntimeModule({ ...pan, dataRoot }));
      if (tmdb !== undefined) modules.push(createTMDBDataModule({ ...tmdb, dataRoot }));
      if (douban !== undefined) modules.push(createDoubanDataModule({ ...douban, dataRoot }));
      if (film !== undefined) connectFilmPlayback(modules, { ...film, dataRoot });
      return modules;
    } catch (error) {
      for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
      throw error;
    }
  } });
}
export async function startRuntime(config) { return createRuntime(config).start(); }
