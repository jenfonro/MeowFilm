// Pure playback text calculations. Loaded after film-rules.js in QuickJS.
(function () {
  'use strict';
  const processRules = globalThis.meowfilmProcess;
  const defaults = processRules({ operation: 'defaults' }).rules;
  const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
  const coordinate = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
  const positiveCoordinate = value => Number.isSafeInteger(value) && value > 0 ? value : undefined;
  const isSearchProjection = value => value?.vod_id == null && value?.sourceKind === 'site' &&
    typeof value.siteKey === 'string' && !!value.siteKey.trim() && object(value.raw);

  function invalidRules() {
    throw Object.assign(new Error('Invalid film recognition rules'), { code: 'INVALID_RULES' });
  }

  function seasonNumber(text) {
    const match = /(?:^|[^a-z0-9])s(?:eason)?\s*0*(\d+)(?=e|\b|[^a-z0-9])/i.exec(text) ??
      /第\s*(\d+)\s*(?:季|部|篇)/.exec(text) ??
      /(?<!新)年\s*番\s*0*(\d+)/.exec(text);
    const value = match ? Number(match[1]) : NaN;
    return Number.isSafeInteger(value) && value >= 0 ? value : undefined;
  }

  function episodeNumber(text) {
    const match = /s\s*\d+\s*e(?:p)?\s*0*(\d+)(?!\d)/i.exec(text) ??
      /(?:^|[^a-z0-9])e(?:p)?\s*0*(\d+)(?!\d)/i.exec(text);
    const value = match ? Number(match[1]) : NaN;
    return Number.isSafeInteger(value) && value >= 0 ? value : undefined;
  }

  function createRecognitionRules(value) {
    const rules = { ...defaults, ...value };
    function process(section, text, query) {
      return processRules({ operation: 'process', rules, request: { section, text, query } });
    }
    return {
      episode(text) {
        const result = process('episode', text);
        const output = result.rewrittenText ?? result.processedText;
        const episode = episodeNumber(output);
        const season = seasonNumber(output) ?? seasonNumber(result.processedText);
        return { ...(season === undefined ? {} : { season }), ...(episode === undefined ? {} : { episode }) };
      },
      season(text) {
        return seasonNumber(process('episode', text).processedText);
      },
      titleSeason(text) {
        const raw = typeof text === 'string' ? text.trim() : '';
        if (!raw) return undefined;
        const result = process('season', raw);
        return positiveCoordinate(result.season);
      },
      movie(text) {
        return process('movie', text).matched;
      },
      search(text, query = text) {
        const result = process('search', text, query);
        if (result.errors.length) invalidRules();
        return { text: result.processedText, key: result.groupKey };
      },
    };
  }

  const qualityKey = globalThis.__meowfilmQualityKey;

  function stripDisplayMetaPrefix(value) {
    let rest = typeof value === 'string' ? value.trim() : '';
    while (rest.startsWith('@')) {
      const matched = /^@([^@/\\]+)/.exec(rest);
      if (!matched || !matched[0]) break;
      rest = rest.slice(matched[0].length).trim();
    }
    return rest;
  }

  function parseDisplayMeta(value) {
    let rest = typeof value === 'string' ? value.trim() : '';
    let quality = '';
    let hdr = false;
    let fps60 = false;
    while (rest.startsWith('@')) {
      const matched = /^@([^@/\\]+)/.exec(rest);
      if (!matched || !matched[1]) break;
      const token = matched[1].trim().toUpperCase();
      if (!quality) {
        if (token === '8K' || token === '4320P') quality = '8K';
        else if (token === '4K' || token === '2160P' || token === 'UHD') quality = '4k';
        else if (token === '1080P' || token === '1080I' || token === 'FHD') quality = '1080p';
        else if (token === '720P' || token === 'HD') quality = '720p';
      }
      if (/(?:HDR|DV|DOLBY)/i.test(token)) hdr = true;
      if (/60\s*(?:FPS|帧)/i.test(token)) fps60 = true;
      rest = rest.slice(matched[0].length).trim();
    }
    return { quality, hdr, fps60 };
  }

  function extractFolderQuality(text) {
    if (typeof text !== 'string' || !text.trim()) return '';
    if (/(?:^|[^a-z0-9])(?:8k|4320p)(?=$|[^a-z0-9])/i.test(text)) return '8K';
    if (/(?:^|[^a-z0-9])(?:4k|2160p|uhd)(?=$|[^a-z0-9])/i.test(text)) return '4k';
    if (/(?:^|[^a-z0-9])(?:1080[pi]|fhd)(?=$|[^a-z0-9])/i.test(text)) return '1080p';
    if (/(?:^|[^a-z0-9])720p(?=$|[^a-z0-9])/i.test(text)) return '720p';
    return '';
  }

  // Only observed labels/metadata contribute quality. These are resource labels,
  // not a claim about the codec, HDR output or resolution of a later play URL.
  function recognizeQuality(record, texts, fallbackQuality = '', displayMeta = null) {
    const explicit = qualityKey(record.quality);
    if (!explicit && typeof record.quality === 'string' && record.quality.trim()) return record.quality.trim();
    const text = texts.filter(value => typeof value === 'string').join(' ');
    let quality = explicit;
    if (!quality) {
      for (const value of texts) {
        if (typeof value !== 'string') continue;
        quality = extractFolderQuality(value);
        if (quality) break;
      }
    }
    if (!quality && displayMeta?.quality) quality = displayMeta.quality;
    if (!quality && fallbackQuality) quality = fallbackQuality;
    if (quality === '4k') {
      if (record.hdr === true || displayMeta?.hdr || record.hdr !== false &&
        /(?:^|[^a-z0-9])(?:hdr(?:10\+?)?|dv)(?=$|[^a-z0-9])|dolby[ ._-]*vision|杜比视界|臻彩/i.test(text)) {
        return '4k_hdr';
      }
      if (Number(record.fps) >= 59 || displayMeta?.fps60 || record.fps === undefined &&
        /(?:^|[^a-z0-9])60\s*(?:fps|帧)(?=$|[^a-z0-9])/i.test(text)) return '4k_fps';
    }
    return quality;
  }

  const DEFAULT_PAN_ALIASES = {
    yidong: ['移动', '逸动', '139', '中国移动', '移动云盘'],
    tianyi: ['天翼', '天意', '189', '天翼云', '天翼云盘'],
    quark: ['夸克', '夸父', 'quark'],
    uc: ['uc', '优夕'],
    baidu: ['百度', '百度网盘', '百度原画', '度盘', 'baidu'],
  };

  function normalizePanLabelText(label) {
    return (typeof label === 'string' ? label.trim() : '')
      .toLowerCase()
      .replace(/[\s\-_—–·|:：/\\()[\]{}<>【】（）「」『』《》、，,。.！!？?~～]+/g, '');
  }

  function buildPanMatchers(panMappings) {
    const map = new Map();
    for (const [provider, list] of Object.entries(DEFAULT_PAN_ALIASES)) {
      map.set(provider, new Set([provider, ...list.map(normalizePanLabelText)].filter(Boolean)));
    }
    if (Array.isArray(panMappings)) {
      for (const row of panMappings) {
        const provider = typeof row?.provider === 'string' ? row.provider.trim().toLowerCase() : '';
        if (!map.has(provider)) continue;
        const set = map.get(provider);
        const aliases = Array.isArray(row.aliases) ? row.aliases
          : typeof row.aliases === 'string' ? row.aliases.replaceAll('，', ',').split(',') : [];
        for (const alias of aliases) {
          const normalized = normalizePanLabelText(alias);
          if (normalized) set.add(normalized);
        }
      }
    }
    return Array.from(map.entries()).map(([provider, set]) => ({
      provider,
      probes: Array.from(set).sort((a, b) => b.length - a.length),
    }));
  }

  function detectPanProvider(explicitProvider, labels, matchers) {
    if (typeof explicitProvider === 'string') {
      const normalized = explicitProvider.trim().toLowerCase();
      if (normalized === '139') return 'yidong';
      if (normalized === '189') return 'tianyi';
      if (DEFAULT_PAN_ALIASES[normalized]) return normalized;
    }
    for (const label of labels) {
      const hay = normalizePanLabelText(label);
      if (!hay) continue;
      for (const matcher of matchers) {
        if (matcher.probes.some(probe => probe && hay.includes(probe))) return matcher.provider;
      }
    }
    return undefined;
  }

  const titleText = value => typeof value === 'string' ? value.normalize('NFKC').trim() : '';
  const titleKey = value => titleText(value).toLowerCase().replace(/[\p{Cf}\s.\-_,，:：;；!！?？·•/\\|]+/gu, '');
  const yearValue = value => /^(?:18|19|20|21)\d{2}$/.test(String(value)) ? Number(value) : undefined;

  function stripSeriesTitleMarkers(value) {
    return titleText(value)
      .replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '')
      .replace(/第\s*(?:\d+|[０-９]+|[一二三四五六七八九十百千两零〇]+)\s*(?:季|部|篇)/gi, ' ')
      .replace(/(?:^|\s)(?:\d+|[０-９]+)\s*季(?![度节])/gi, ' ')
      .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ')
      .replace(/(?<!新)年\s*番\s*(?:\d+|[０-９]+|[一二三四五六七八九十百千两零〇]+)?/gi, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  function canonicalEntry(value, kind, recognition, query) {
    const raw = titleText(value);
    if (!raw) return { keyword: '', sessionKey: '', season: null };
    const baseNoYear = raw.replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '').trim() || raw;
    if (kind === 'movie') {
      const tailDigits = baseNoYear.match(/\d+/g)?.at(-1) ?? baseNoYear;
      const cleaned = recognition.search(baseNoYear, tailDigits).text || baseNoYear;
      return { keyword: cleaned, sessionKey: titleKey(cleaned) || titleKey(baseNoYear), season: null };
    }
    const season = recognition.titleSeason(raw) ?? null;
    const stripped = stripSeriesTitleMarkers(baseNoYear) || baseNoYear;
    const yearTail = /(?:^|\D)((?:19|20)\d{2})\s*$/.exec(stripped)?.[1] ?? '';
    const effectiveQuery = query !== undefined && query !== null && String(query).trim()
      ? stripSeriesTitleMarkers(query)
      : yearTail;
    let cleaned = recognition.search(stripped, effectiveQuery).text || stripped;
    // Strip 1-2 trailing series digits (e.g., 仙逆1 / 仙逆2) unless it is a 4-digit year.
    const strippedDigits = cleaned.replace(/^(.*(?:[^A-Za-z0-9\s]|\s))\s*0*[1-9]\d?\s*$/, '$1').trim();
    if (strippedDigits) cleaned = strippedDigits;
    return {
      keyword: cleaned || stripped,
      sessionKey: titleKey(cleaned) || titleKey(stripped),
      season,
    };
  }

  function resultYear(record) {
    for (const value of [record.year, record.vod_year, record.release_date?.slice?.(0, 4),
      record.first_air_date?.slice?.(0, 4), ...(Array.isArray(record.pubdate) ? record.pubdate.map(value => value?.slice?.(0, 4)) : [])]) {
      const year = yearValue(value);
      if (year !== undefined) return year;
    }
    const name = titleText(record.rawTitle ?? record.vod_name ?? record.title ?? record.name);
    return yearValue(/[（(]\s*((?:18|19|20|21)\d{2})\s*[)）]\s*$/.exec(name)?.[1]);
  }

  function resultTitles(record) {
    const first = record.rawTitle ?? record.vod_name ?? record.title ?? record.name;
    return [first, record.originalTitle, record.original_title, record.original_name,
      ...(Array.isArray(record.aka) ? record.aka : []),
      ...(Array.isArray(record.localized_name) ? record.localized_name : [record.localized_name]),
      ...(Array.isArray(record.aliases) ? record.aliases : [])].map(titleText).filter(Boolean);
  }

  function resultKind(record) {
    const value = record.kind ?? record.contentKind ?? record.media_type ?? record.tmdbType ?? record.target_type;
    if (value === 'movie' || value === 'tv') return value;
    return ({ 电影: 'movie', 电视剧: 'tv', 剧集: 'tv' })[record.type_name];
  }

  function resultSeason(record, recognition) {
    const explicit = positiveCoordinate(record.season) ?? positiveCoordinate(record.season_number) ?? positiveCoordinate(record.seasonHint);
    if (explicit !== undefined) return explicit;
    const rawTitle = titleText(record.rawTitle ?? record.vod_name ?? record.title ?? record.name);
    const remark = titleText(record.vod_remarks ?? record.remark);
    return recognition.titleSeason(rawTitle) ?? recognition.season(rawTitle) ??
      recognition.season(remark) ?? recognition.titleSeason(remark);
  }

  function canonicalTitle(value, kind, recognition, query) {
    return canonicalEntry(value, kind, recognition, query).sessionKey;
  }

  function matchingRecord(raw) {
    return isSearchProjection(raw) ? { ...raw, ...raw.raw,
      rawTitle: raw.raw.rawTitle ?? raw.raw.vod_name ?? raw.raw.title ?? raw.raw.name ?? raw.rawTitle } : raw;
  }

  function sameContent(raw, target, recognition, contentIdentity) {
    const kind = resultKind(raw);
    if (target.kind !== 'group' && target.kind !== 'tv' && kind !== undefined && kind !== target.kind) return false;
    if (target.kind === 'tv' && kind === 'movie') return false;
    const provider = raw.metadataProvider ?? (raw.tmdbId > 0 ? 'tmdb' : undefined);
    const metadataId = raw.metadataId ?? (raw.tmdbId > 0 ? raw.tmdbId
      : raw.metadataProvider !== undefined ? raw.id : undefined);
    if (provider && provider === target.metadataProvider && metadataId !== undefined && target.metadataId !== undefined)
      return String(metadataId) === String(target.metadataId);
    if (target.metadataProvider !== undefined && contentIdentity?.kind === target.kind &&
        contentIdentity.metadataProvider === target.metadataProvider &&
        String(contentIdentity.metadataId) === String(target.metadataId)) return true;
    const targets = resultTitles(target), titles = resultTitles(raw);
    return targets.some(title => {
      const expected = canonicalTitle(title, target.kind, recognition);
      return expected && titles.some(value => canonicalTitle(value, target.kind, recognition) === expected);
    });
  }

  /** A content's source menu includes its seasons, not other search hits. */
  function belongsToContent(raw, target, recognition) {
    if (!object(raw) || !target || !['movie', 'tv', 'group'].includes(target.kind)) return false;
    raw = matchingRecord(raw);
    if (target.kind === 'movie') {
      const rawYear = resultYear(raw), targetYear = resultYear(target);
      if (rawYear !== undefined && targetYear !== undefined && rawYear !== targetYear) return false;
    }
    return sameContent(raw, target, recognition);
  }

  /** Smart eligibility additionally requires the requested season (if explicit on source) and movie year. */
  function matchesResult(raw, { target, contentIdentity, recognition, rules } = {}) {
    if (!object(raw) || !target || !['movie', 'tv', 'source', 'group'].includes(target.kind)) return false;
    if (target.kind === 'source') return true;
    raw = matchingRecord(raw);
    const engine = recognition ?? createRecognitionRules(rules);
    const rawSeason = resultSeason(raw, engine), targetSeason = positiveCoordinate(target.season) ?? resultSeason(target, engine);
    if (rawSeason !== undefined && targetSeason !== undefined && rawSeason !== targetSeason) return false;
    if (target.kind === 'movie') {
      const rawYear = resultYear(raw), targetYear = resultYear(target);
      if (rawYear !== undefined && targetYear !== undefined && rawYear !== targetYear) return false;
    }
    return sameContent(raw, target, engine, contentIdentity);
  }

  function metadataHints(record, kind, recognition) {
    const title = record.title ?? record.name ?? '';
    const entry = canonicalEntry(title, kind, recognition);
    const rawYear = record.year ?? record.release_date?.slice(0, 4) ?? record.first_air_date?.slice(0, 4)
      ?? record.pubdate?.[0]?.slice(0, 4);
    return {
      title,
      query: entry.keyword || titleText(title),
      sessionKey: entry.sessionKey,
      year: /^\d{4}$/.test(String(rawYear)) ? Number(rawYear) : null,
      season: resultSeason(record, recognition) ?? entry.season ?? null,
    };
  }

  function buildLineFolderStats(episodes, recognition) {
    const stats = new Map();
    const touch = path => {
      if (!stats.has(path)) stats.set(path, { seasons: new Set(), qualities: new Set() });
      return stats.get(path);
    };
    let dirMaxEpisode = new Map();
    const prepared = episodes.map(item => {
      const rawPath = item.path ?? item.file?.path;
      const pathParts = typeof rawPath === 'string' ? rawPath.split(/[/\\]/).map(s => s.trim()).filter(Boolean) : [];
      const displayStripped = stripDisplayMetaPrefix(item.name);
      const displayParts = typeof displayStripped === 'string' && (displayStripped.includes('/') || displayStripped.includes('\\'))
        ? displayStripped.split(/[/\\]/).map(s => s.trim()).filter(Boolean)
        : [];
      const dirs = pathParts.length > 1
        ? pathParts.slice(0, -1)
        : (item.hasListDirs && displayParts.length > 0 ? displayParts : (displayParts.length > 1 ? displayParts.slice(0, -1) : []));
      const fileName = item.file?.name ?? item.fileName ?? (pathParts.length ? pathParts[pathParts.length - 1] : stripDisplayMetaPrefix(item.name));
      const currentDir = dirs.length ? dirs[dirs.length - 1] : '';
      const currentPath = dirs.join('/');
      const parentPath = dirs.length >= 2 ? dirs.slice(0, -1).join('/') : '';
      for (let i = 0; i < dirs.length; i += 1) {
        const prefix = dirs.slice(0, i + 1).join('/');
        const node = touch(prefix);
        const segSeason = positiveCoordinate(recognition.season(dirs[i]));
        const segQuality = extractFolderQuality(dirs[i]);
        if (segSeason !== undefined) node.seasons.add(segSeason);
        if (segQuality) node.qualities.add(segQuality);
      }
      const parsedFile = recognition.episode(fileName);
      const parsedDisplay = parsedFile.episode === undefined || parsedFile.season === undefined
        ? recognition.episode(displayStripped || item.name)
        : {};
      const ep = coordinate(item.episode) ?? parsedFile.episode ?? parsedDisplay.episode;
      if (ep !== undefined && ep > 0) {
        const prev = dirMaxEpisode.get(currentPath) ?? 0;
        if (ep > prev) dirMaxEpisode.set(currentPath, ep);
      }
      return { item, rawPath, dirs, fileName, displayStripped, currentPath, parentPath, parsedFile, parsedDisplay };
    });
    const uniqueSeason = path => {
      const hit = stats.get(path);
      return hit && hit.seasons.size === 1 ? Array.from(hit.seasons)[0] : undefined;
    };
    const uniqueQuality = path => {
      const hit = stats.get(path);
      return hit && hit.qualities.size === 1 ? Array.from(hit.qualities)[0] : '';
    };
    return { prepared, uniqueSeason, uniqueQuality, dirMaxEpisode };
  }

  function candidateTexts(detail, target, source, recognition, panMappings) {
    const panMatchers = buildPanMatchers(panMappings);
    const detailTitleSeason = positiveCoordinate(detail.season)
      ?? positiveCoordinate(detail.titleSeason)
      ?? recognition.titleSeason(detail.rawTitle ?? detail.title ?? '');
    const manualSeason = positiveCoordinate(source?.manualSeasonHint);
    return detail.lines.flatMap(line => {
      const linePan = detectPanProvider(line.provider, [line.label, line.flag], panMatchers);
      const lineSeason = positiveCoordinate(line.season);
      const { prepared, uniqueSeason, uniqueQuality, dirMaxEpisode } = buildLineFolderStats(line.episodes, recognition);
      return prepared.map(entry => {
        const { item, rawPath, dirs, fileName, displayStripped, currentPath, parentPath, parsedFile, parsedDisplay } = entry;
        const displayMeta = parseDisplayMeta(item.name);
        const episode = coordinate(item.episode) ?? parsedFile.episode ?? parsedDisplay.episode;
        const currentSeason = dirs.length ? uniqueSeason(currentPath) : undefined;
        const currentQuality = dirs.length ? uniqueQuality(currentPath) : '';
        const parentSeason = parentPath ? uniqueSeason(parentPath) : undefined;
        const parentQuality = parentPath ? uniqueQuality(parentPath) : '';
        const folderSeason = currentSeason ?? (currentQuality && parentSeason !== undefined ? parentSeason : undefined);
        const folderQuality = currentQuality || (currentSeason !== undefined && parentQuality ? parentQuality : '');

        let season;
        let seasonRank = 0;
        if (positiveCoordinate(item.season) !== undefined) {
          season = item.season;
          seasonRank = 4;
        } else if (positiveCoordinate(parsedFile.season) !== undefined) {
          season = parsedFile.season;
          seasonRank = 4;
        } else if (positiveCoordinate(parsedDisplay.season) !== undefined &&
            (parsedDisplay.episode === undefined || parsedDisplay.episode === episode)) {
          season = parsedDisplay.season;
          seasonRank = 4;
        } else if (episode > 0 && folderSeason !== undefined) {
          season = folderSeason;
          seasonRank = 4;
        } else if (lineSeason !== undefined) {
          season = lineSeason;
          seasonRank = 3;
        } else if (detailTitleSeason !== undefined) {
          season = detailTitleSeason;
          seasonRank = 3;
        } else if (manualSeason !== undefined) {
          season = manualSeason;
          seasonRank = 3;
        }

        const movie = target.kind === 'movie' && (recognition.movie(fileName) || episode === undefined);
        const quality = recognizeQuality(item, [fileName, displayStripped || item.name], folderQuality, displayMeta);
        const panProvider = detectPanProvider(item.provider, [], panMatchers) ?? linePan;
        const dirMax = dirMaxEpisode.get(currentPath) ?? 0;
        return {
          fileName,
          ...(rawPath === undefined ? {} : { path: rawPath }),
          ...(season === undefined ? {} : { season }),
          ...(episode === undefined ? {} : { episode }),
          seasonRank,
          ...(dirMax > 0 ? { sourceDirMaxEpisode: dirMax } : {}),
          ...(panProvider ? { panProvider } : {}),
          movie,
          eligible: target.kind !== 'movie' || movie,
          quality,
        };
      });
    });
  }

  globalThis.meowfilmProcess = function (request) {
    if (!request.operation.startsWith('recognition.')) return processRules(request);
    try {
      const recognition = createRecognitionRules(request.rules);
      let value;
      switch (request.operation) {
        case 'recognition.canonical':
          value = canonicalEntry(request.title, request.contentKind, recognition, request.query); break;
        case 'recognition.content':
          value = request.rows.map(row => belongsToContent(row, request.target, recognition)); break;
        case 'recognition.matches':
          value = request.rows.map(row => matchesResult(row, { target: request.target,
            contentIdentity: request.contentIdentity, recognition })); break;
        case 'recognition.candidates':
          value = candidateTexts(request.detail, request.target, request.source, recognition, request.panMappings); break;
        case 'recognition.batch':
          value = request.requests.map(item => {
            switch (item.kind) {
              case 'canonical': return canonicalEntry(item.title, item.contentKind, recognition, item.query);
              case 'metadata': return metadataHints(item.record, item.contentKind, recognition);
              case 'match': return matchesResult(item.record, { target: item.target, recognition });
              case 'season': return resultSeason(item.record, recognition) ?? null;
              default: throw new Error('Unknown film recognition request');
            }
          }); break;
        default: throw new Error('Unknown film recognition operation');
      }
      return { value };
    } catch (error) {
      if (error.code === 'INVALID_RULES') return { error: error.code };
      throw error;
    }
  };
})();
