import { checkSource, fail, identity, object, requireValue, sourceIdentity, text } from './identity.js';

/**
 * Self-contained live source & pan-alias resolver inside the Node film owner.
 * Combines `film.video-sources` + `cat.sources` native settings with live
 * `cat` / `source` runtime instances so clients never pass installationId/generation/sources.
 */
export function createFilmSourceRegistry({ modules, nativeSettings } = {}) {
  requireValue(modules instanceof Map);

  function readConfiguredVideoSources() {
    if (!nativeSettings) return null;
    const document = nativeSettings.read('film.video-sources');
    if (document === null) return null;
    if (!object(document) || document.version !== 1 || !Array.isArray(document.sources)) {
      throw fail('CORRUPT_STORAGE');
    }
    return document.sources;
  }

  function readConfiguredCatEnabledMap() {
    if (!nativeSettings) return null;
    const document = nativeSettings.read('cat.sources');
    if (!object(document) || !Array.isArray(document.scripts)) return null;
    const map = new Map();
    for (const row of document.scripts) {
      if (object(row) && text(row.installationId)) map.set(row.installationId, row.enabled !== false);
    }
    return map;
  }

  function readPanMappings() {
    if (!nativeSettings) return [];
    const document = nativeSettings.read('cat.sources');
    if (!object(document) || !Array.isArray(document.mappings)) return [];
    return document.mappings
      .filter(row => object(row) && text(row.provider) && Array.isArray(row.aliases))
      .map(row => ({
        provider: text(row.provider).toLowerCase(),
        aliases: row.aliases.filter(alias => typeof alias === 'string' && alias.trim()).map(alias => alias.trim()),
      }));
  }

  function panMappingsInfo() {
    const mappings = readPanMappings();
    return { mappings, revision: identity('pan-mappings', mappings) };
  }

  async function readProviderInstances(engine, signal) {
    const owner = modules.get(engine);
    if (!owner) return [];
    await owner.ready;
    const method = engine === 'cat' ? 'cat.script.status' : 'source.runtime.status';
    const handler = owner.methods?.[method];
    if (typeof handler !== 'function') return [];
    const reply = await handler({}, { signal });
    if (reply?.ok !== true || !Array.isArray(reply.value?.instances)) return [];
    return reply.value.instances;
  }

  async function liveSources({ explicitSources, signal } = {}) {
    if (Array.isArray(explicitSources)) {
      return explicitSources.map((src, order) => ({ ...checkSource(src), order: src.order ?? order }));
    }
    const configured = readConfiguredVideoSources();
    const [catInstances, sourceInstances] = await Promise.all([
      readProviderInstances('cat', signal),
      readProviderInstances('source', signal),
    ]);

    if (configured !== null) {
      const catByInstall = new Map(
        catInstances
          .filter(inst => inst?.state === 'ready' && Number.isSafeInteger(inst.generation) && inst.generation > 0)
          .map(inst => [inst.installationId, inst]),
      );
      const ordered = [...configured].sort((a, b) =>
        (Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0)) ||
        (Number(a.position ?? 0) - Number(b.position ?? 0)) ||
        String(a.installationId ?? '').localeCompare(String(b.installationId ?? '')) ||
        String(a.siteKey ?? '').localeCompare(String(b.siteKey ?? '')));
      const resolved = [];
      const seenKeys = new Set();
      for (const row of ordered) {
        if (!object(row) || !text(row.installationId) || !text(row.siteKey)) continue;
        const inst = catByInstall.get(row.installationId);
        const site = inst?.sites?.find(item => item?.key === row.siteKey);
        if (!inst || !site) continue;
        const siteHasSearch = Array.isArray(site.actions) ? site.actions.includes('search') : site.searchable !== false;
        const source = checkSource({
          engine: 'cat',
          installationId: row.installationId,
          siteId: row.siteKey,
          generation: inst.generation,
          name: text(row.name) || text(site.name) || row.siteKey,
          enabled: row.enabled !== false,
          searchable: row.searchEnabled !== false && siteHasSearch,
          smartSkip: row.smartSkip === true,
          aggregateImages: row.aggregateImages === true,
          order: resolved.length,
        });
        if (seenKeys.has(source.key)) continue;
        seenKeys.add(source.key);
        resolved.push(source);
      }
      return resolved;
    }

    // Uninitialized `film.video-sources` (revision === 0) or standalone non-nativeSettings fallback:
    // enumerate ready enabled instances directly using default options.
    const catEnabled = readConfiguredCatEnabledMap();
    const resolved = [];
    for (const inst of catInstances) {
      if (inst?.state !== 'ready' || !Number.isSafeInteger(inst.generation) || inst.generation <= 0) continue;
      if (catEnabled && catEnabled.get(inst.installationId) === false) continue;
      for (const site of inst.sites ?? []) {
        if (!text(site?.key)) continue;
        const siteHasSearch = Array.isArray(site.actions) ? site.actions.includes('search') : site.searchable !== false;
        resolved.push(checkSource({
          engine: 'cat',
          installationId: inst.installationId,
          siteId: site.key,
          generation: inst.generation,
          name: text(site.name) || site.key,
          enabled: true,
          searchable: siteHasSearch,
          aggregateImages: false,
          order: resolved.length,
        }));
      }
    }
    for (const inst of sourceInstances) {
      if (inst?.state !== 'ready' || inst.protocol !== 'cat' ||
          !Number.isSafeInteger(inst.generation) || inst.generation <= 0 ||
          !Number.isSafeInteger(inst.revision) || inst.revision <= 0) continue;
      resolved.push(checkSource({
        engine: 'source',
        installationId: inst.installationId,
        siteId: 'source',
        generation: inst.generation,
        revision: inst.revision,
        name: text(inst.name) || inst.installationId,
        enabled: true,
        searchable: true,
        aggregateImages: false,
        order: resolved.length,
      }));
    }
    return resolved;
  }

  async function resolveSingleSource(input, { signal } = {}) {
    requireValue(object(input));
    if (Number.isSafeInteger(input.generation) && input.generation > 0 && text(input.installationId) && text(input.siteId)) {
      return checkSource(input);
    }
    const installationId = text(input.installationId);
    const siteId = text(input.siteKey ?? input.siteId);
    const key = text(input.key ?? input.sourceKey);
    const all = await liveSources({ signal });
    const matched = all.find(src =>
      (key && src.key === key) ||
      (installationId && siteId && src.installationId === installationId && src.siteId === siteId));
    if (matched) return matched;
    if (installationId && siteId) {
      const catInstances = await readProviderInstances('cat', signal);
      const inst = catInstances.find(row => row?.installationId === installationId &&
        row.state === 'ready' && Number.isSafeInteger(row.generation) && row.generation > 0);
      const site = inst?.sites?.find(row => row?.key === siteId);
      requireValue(inst && site, 'SOURCE_NOT_FOUND');
      return checkSource({
        engine: 'cat',
        installationId,
        siteId,
        generation: inst.generation,
        key: sourceIdentity({ engine: 'cat', installationId, siteId }),
        name: text(input.name) || text(site.name) || siteId,
        enabled: true,
        searchable: true,
      });
    }
    throw fail('SOURCE_NOT_FOUND');
  }

  return {
    liveSources,
    resolveSingleSource,
    panMappingsInfo,
  };
}
