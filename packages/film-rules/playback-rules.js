// Pure playback text calculations. Loaded after film-rules.js in QuickJS.
(function () {
  'use strict';
  const processRules = globalThis.meowfilmProcess;
  const defaults = processRules({ operation: 'defaults' }).rules;
  const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
  const coordinate = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
  const isSearchProjection = value => value?.vod_id == null && value?.sourceKind === 'site' &&
    typeof value.siteKey === 'string' && !!value.siteKey.trim() && object(value.raw);
  function invalidRules() {
    throw Object.assign(new Error('Invalid film recognition rules'), { code: 'INVALID_RULES' });
  }

  function seasonNumber(text) {
    const match = /(?:^|[^a-z0-9])s(?:eason)?\s*0*(\d+)(?=e|\b|[^a-z0-9])/i.exec(text) ??
      /第\s*(\d+)\s*(?:季|部|篇)/.exec(text);
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


  function qualityKey(value) {
    if (typeof value !== 'string') return '';
    const text = value.trim().toLowerCase();
    if (/^4k(?:[· _-]?hdr)$/.test(text)) return '4k_hdr';
    if (/^4k(?:[· _-]?(?:fps|60(?:fps|帧)?))$/.test(text)) return '4k_fps';
    if (['4k', '2160p', 'uhd'].includes(text)) return '4k';
    if (['1080p', '1080i', 'fhd'].includes(text)) return '1080p';
    if (['720p', 'hd'].includes(text)) return '720p';
    return '';
  }

  // Only observed labels/metadata contribute quality. These are resource labels,
  // not a claim about the codec, HDR output or resolution of a later play URL.
  function recognizeQuality(record, texts) {
    const explicit = qualityKey(record.quality);
    if (!explicit && typeof record.quality === 'string' && record.quality.trim()) return record.quality.trim();
    const text = texts.filter(value => typeof value === 'string').join(' ');
    let quality = explicit;
    if (!quality) {
      // Callers supply filename, display name and actual path in that order.
      // A parent directory must not override a more specific file marker.
      for (const value of texts) {
        if (typeof value !== 'string') continue;
        if (/(?:^|[^a-z0-9])(?:8k|4320p)(?=$|[^a-z0-9])/i.test(value)) quality = '8K';
        else if (/(?:^|[^a-z0-9])(?:4k|2160p|uhd)(?=$|[^a-z0-9])/i.test(value)) quality = '4k';
        else if (/(?:^|[^a-z0-9])(?:1080[pi]|fhd)(?=$|[^a-z0-9])/i.test(value)) quality = '1080p';
        else if (/(?:^|[^a-z0-9])720p(?=$|[^a-z0-9])/i.test(value)) quality = '720p';
        if (quality) break;
      }
    }
    if (quality === '4k') {
      if (record.hdr === true || record.hdr !== false &&
        /(?:^|[^a-z0-9])(?:hdr(?:10\+?)?|dv)(?=$|[^a-z0-9])|dolby[ ._-]*vision|杜比视界|臻彩/i.test(text)) {
        return '4k_hdr';
      }
      if (Number(record.fps) >= 59 || record.fps === undefined &&
        /(?:^|[^a-z0-9])60\s*(?:fps|帧)(?=$|[^a-z0-9])/i.test(text)) return '4k_fps';
    }
    return quality;
  }

  const titleText = value => typeof value === 'string' ? value.normalize('NFKC').trim() : '';
  const titleKey = value => titleText(value).toLowerCase().replace(/[\p{Cf}\s.\-_,，:：;；!！?？·•/\\|]+/gu, '');
  const yearValue = value => /^(?:18|19|20|21)\d{2}$/.test(String(value)) ? Number(value) : undefined;

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
    // rawTitle is the site title before an aggregate card replaced its display
    // title with metadata. A group/display key alone is not identity evidence.
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
    const explicit = coordinate(record.season) ?? coordinate(record.season_number) ?? coordinate(record.seasonHint);
    if (explicit !== undefined) return explicit;
    return recognition.season(titleText(record.rawTitle ?? record.vod_name ?? record.title ?? record.name)) ??
      recognition.season(titleText(record.vod_remarks ?? record.remark));
  }

  function canonicalTitle(value, kind, recognition, query) {
    let title = titleText(value).replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '').trim();
    if (kind === 'movie') {
      // Feed observed digits to the shared query-tail protection, even when
      // source annotations follow a sequel number. Do not strip a movie's number.
      return titleKey(recognition.search(title, title.match(/\d+/g)?.at(-1) ?? title).text);
    }
    title = title.replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
      .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ');
    query = titleText(query).replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
      .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ').trim();
    return titleKey(recognition.search(title, query).text);
  }

  /** Smart eligibility only. Ordinary menus keep the full raw search page. */
  function matchesResult(raw, { target, rules } = {}) {
    if (!object(raw) || !target || !['movie', 'tv', 'source', 'group'].includes(target.kind)) return false;
    // Source scope is checked by the caller's exact installation/detail identity,
    // not by pretending an arbitrary script title has a metadata counterpart.
    if (target.kind === 'source') return true;
    if (isSearchProjection(raw)) raw = { ...raw, ...raw.raw,
      rawTitle: raw.raw.rawTitle ?? raw.raw.vod_name ?? raw.raw.title ?? raw.raw.name ?? raw.rawTitle };
    const kind = resultKind(raw);
    if (target.kind !== 'group' && kind !== undefined && kind !== target.kind) return false;
    const recognition = createRecognitionRules(rules);
    const rawSeason = resultSeason(raw, recognition), targetSeason = resultSeason(target, recognition);
    if (rawSeason !== undefined && targetSeason !== undefined && rawSeason !== targetSeason) return false;
    const rawYear = resultYear(raw), targetYear = resultYear(target);
    const laterSeason = target.kind === 'tv' && rawSeason > 1 && rawSeason === targetSeason;
    if (rawYear !== undefined && targetYear !== undefined && rawYear !== targetYear && !laterSeason) return false;
    const provider = raw.metadataProvider ?? (raw.tmdbId > 0 ? 'tmdb' : undefined);
    const metadataId = raw.metadataId ?? (raw.tmdbId > 0 ? raw.tmdbId
      : raw.metadataProvider !== undefined ? raw.id : undefined);
    if (provider === target.metadataProvider && metadataId !== undefined && target.metadataId !== undefined) {
      return String(metadataId) === String(target.metadataId);
    }
    const targets = resultTitles(target);
    const titles = resultTitles(raw);
    return targets.some(title => {
      const expected = canonicalTitle(title, target.kind, recognition, title);
      return expected && titles.some(value => canonicalTitle(value, target.kind, recognition, title) === expected);
    });
  }




  function metadataHints(record, kind, recognition) {
    const title = record.title ?? record.name ?? '';
    const query = kind === 'tv' ? titleText(title)
      .replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
      .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ').trim() : titleText(title);
    const rawYear = record.year ?? record.release_date?.slice(0, 4) ?? record.first_air_date?.slice(0, 4)
      ?? record.pubdate?.[0]?.slice(0, 4);
    return { title, query, year: /^\d{4}$/.test(String(rawYear)) ? Number(rawYear) : null,
      season: resultSeason(record, recognition) ?? null };
  }

  function candidateTexts(detail, target, source, recognition) {
    return detail.lines.flatMap(line => line.episodes.map(item => {
      const path = item.path ?? item.file?.path;
      const parts = typeof path === 'string' ? path.split(/[/\\]/).filter(Boolean) : [];
      const fileName = item.file?.name ?? item.fileName ?? (parts.length ? parts[parts.length - 1] : item.name);
      const parsedFile = recognition.episode(fileName);
      const parsed = parsedFile.episode === undefined ? recognition.episode(item.name) : parsedFile;
      const season = coordinate(item.season) ?? parsed.season ??
        (parts.length > 1 ? recognition.season(parts.slice(0, -1).reverse().join('/')) : undefined) ??
        coordinate(line.season) ?? coordinate(detail.season) ?? coordinate(source.manualSeasonHint);
      const episode = coordinate(item.episode) ?? parsed.episode;
      const movie = target.kind === 'movie' && (recognition.movie(fileName) || episode === undefined);
      return { fileName, ...(path === undefined ? {} : { path }),
        ...(season === undefined ? {} : { season }), ...(episode === undefined ? {} : { episode }),
        movie, eligible: target.kind !== 'movie' || movie,
        quality: recognizeQuality(item, [fileName, item.name, path]) };
    }));
  }

  globalThis.meowfilmProcess = function (request) {
    if (!request.operation.startsWith('recognition.')) return processRules(request);
    try {
      const recognition = createRecognitionRules(request.rules);
      let value;
      switch (request.operation) {
        case 'recognition.matches':
          value = request.rows.map(row => matchesResult(row, { target: request.target, rules: request.rules })); break;
        case 'recognition.candidates':
          value = candidateTexts(request.detail, request.target, request.source, recognition); break;
        case 'recognition.batch':
          value = request.requests.map(item => {
            switch (item.kind) {
              case 'episode': return recognition.episode(item.text);
              case 'season': return recognition.season(item.text) ?? null;
              case 'movie': return recognition.movie(item.text);
              case 'search': return recognition.search(item.text, item.query);
              case 'quality': return qualityKey(item.text);
              case 'metadata': return metadataHints(item.record, item.contentKind, recognition);
              case 'match': return matchesResult(item.record, { target: item.target, rules: request.rules });
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
