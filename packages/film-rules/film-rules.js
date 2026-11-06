// Port of the dashboard magic-settings chain and shared episodeMarkerNormalize.js.
// Reference: meowfilm-server commit 934a03e5aeda7edbcef0cfb037734fed93bfe881.
// The host supplies only JSON. This program has no platform or script-runner dependencies.
(function () {
  'use strict';

  const DEFAULT_EPISODE_RULES = [
    {
      pattern: '.*?(?:(?:[Ss](?:eason)?\\s*0*(\\d{1,2}))|(?:第\\s*0*(\\d{1,2})\\s*(?:季|部|篇))).*?(?:第\\s*0*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*0*(\\d{1,4})(?:$|\\D)).*?.*',
      replace: 'S$1$2E$3$4',
      flags: 'i'
    },
    {
      pattern: '.*?([Ss]\\d{1,2})?(?:第\\s*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*(\\d{1,4})(?:$|\\D)).*?.*',
      replace: '$1E$2$3',
      flags: 'i'
    },
    {
      pattern: '^[\\s\\[\\]\\(\\){}【】._=-]*0*(\\d{1,4})[\\s\\[\\]\\(\\){}【】._=-]*(?:\\.[A-Za-z0-9]{1,6})?\\s*$',
      replace: 'E$1',
      flags: 'i'
    }
  ];
  const DEFAULT_MOVIE_RULES = [{
    pattern: '^\\s*(?!.*(?:S\\d{1,2}\\s*E\\d{1,3}|第\\s*\\d+\\s*[集话期]|(?:^|[\\s._-])(?:EP?|E)\\s*\\d+(?:$|[\\s._-])))(?=.*\\b(?:19\\d{2}|20\\d{2})\\b).*\\.(?:mkv|mp4)\\s*$',
    replace: '',
    flags: 'i'
  }];
  const DEFAULT_EPISODE_CLEAN_RULES = [
    String.raw`\[(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\])[^\]]*\]`,
    String.raw`【(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*】)[^】]*】`,
    String.raw`\((?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\))[^)]*\)`,
    String.raw`（(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*）)[^）]*）`,
    String.raw`(?:^|[\s\[\]\(\){}【】._-])(?:4k|8k|2160p|1080p|720p)(?=$|[\s\[\]\(\){}【】._-])`,
    String.raw`高\s*码\s*(?:率|资源|直链)?|码\s*率`
  ];
  const DEFAULT_AGGREGATE_REGEX_RULES = [
    String.raw`\([^)]*\)|（[^）]*）|\[[^\]]*\]|\{[^}]*\}|【[^】]*】`,
    String.raw`(?<!新)年\s*番\s*\d+|(?<!新)年\s*番`,
    String.raw`更新\s*中|(?:更新(?:至|到)?|更(?:至|到)?|更|首\s*更)\s*(?:第\s*)?(?:EP?|E)?\s*(?:\d{1,4}|[一二三四五六七八九十百千万两零〇]{1,20})(?:\s*(?:\+|＋|多|余))?\s*(?:集|话|期)?|首\s*更`,
    String.raw`(?:HD\s*)?(?:4[kK]|8[kK])|(?:2160|1080|720)[pP]|国\s*漫|臻\s*彩|杜\s*比\s*音\s*效|已\s*刮\s*削|连\s*载\s*中|10\s*[- ]?bit`,
    String.raw`(?:19\d{2}|20\d{2})(?=\s*(?:(?:HD\s*)?(?:4[kK]|8[kK])|(?:更新|更)))`,
    String.raw`最\s*新\s*(?:一\s*集|更\s*新)`,
    String.raw`(?<=\D)\d{1,4}$`
  ];
  const GROUPS = ['episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules'];

  function normalizeRegexText(text) {
    const raw = typeof text === 'string' ? text : '';
    return raw.replace(/\\\\(?=[dDsSwWbB.()[\]{}+*?^$|\\\-_/])/g, '\\');
  }

  function normalizePatternInput(text) {
    const raw = typeof text === 'string' ? text.trim() : '';
    if (!raw) return null;
    if (raw.startsWith('/') && raw.lastIndexOf('/') > 0) {
      const last = raw.lastIndexOf('/');
      const pattern = normalizeRegexText(raw.slice(1, last).trim());
      const flags = raw.slice(last + 1).trim();
      return pattern ? { pattern, flags } : null;
    }
    return { pattern: normalizeRegexText(raw), flags: '' };
  }

  function decodeEpisodeRule(rule) {
    const raw = typeof rule === 'string' ? rule.trim() : '';
    if (!raw) return null;
    if (raw.startsWith('{') && raw.endsWith('}')) {
      try {
        const obj = JSON.parse(raw);
        if (obj && typeof obj === 'object' && typeof obj.pattern === 'string' && obj.pattern.trim()) {
          return {
            pattern: normalizeRegexText(obj.pattern.trim()),
            replace: typeof obj.replace === 'string' ? obj.replace : '',
            flags: typeof obj.flags === 'string' ? obj.flags : ''
          };
        }
      } catch (_) {}
    }
    if (raw.startsWith('/') && raw.lastIndexOf('/') > 0) {
      const last = raw.lastIndexOf('/');
      return {
        pattern: normalizeRegexText(raw.slice(1, last).trim()),
        replace: '',
        flags: raw.slice(last + 1).trim()
      };
    }
    return { pattern: normalizeRegexText(raw), replace: '', flags: '' };
  }

  function normalizeMagicRuleRow(rule) {
    return {
      pattern: normalizeRegexText(rule && typeof rule.pattern === 'string' ? rule.pattern.trim() : ''),
      replace: rule && typeof rule.replace === 'string' ? rule.replace : '',
      flags: rule && typeof rule.flags === 'string' ? rule.flags.trim() : ''
    };
  }

  function encodeEpisodeRule(rule) {
    const pattern = normalizeRegexText(rule && typeof rule.pattern === 'string' ? rule.pattern.trim() : '');
    if (!pattern) return '';
    const replace = rule && typeof rule.replace === 'string' ? rule.replace : '';
    const flags = rule && typeof rule.flags === 'string' ? rule.flags.trim() : '';
    const payload = { pattern, replace: replace || '' };
    if (flags) payload.flags = flags;
    return JSON.stringify(payload);
  }

  function cleanRows(rows) {
    return rows.map((it) => String(it || '').trim()).filter(Boolean);
  }

  function readDocument(document) {
    return {
      episodeCleanRegexRules: cleanRows(document.episodeCleanRegexRules),
      episodeRules: document.episodeRules.map(decodeEpisodeRule).filter(Boolean),
      movieRules: document.movieRules.map(decodeEpisodeRule).filter(Boolean),
      aggregateRegexRules: cleanRows(document.aggregateRegexRules)
    };
  }

  function prepareSave(rules) {
    // Keep both save-time normalizations and response decoding, as in the page.
    const document = {
      episodeCleanRegexRules: cleanRows(rules.episodeCleanRegexRules),
      episodeRules: rules.episodeRules.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
      movieRules: rules.movieRules.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
      aggregateRegexRules: cleanRows(rules.aggregateRegexRules)
    };
    return { document, rules: readDocument(document) };
  }

  function initialDocument() {
    // defaults.go seeds two episode rules. Its JSON patterns contain doubled
    // backslashes; dashboard decoding removes one level on every read.
    const seed = (row) => JSON.stringify({ ...row, pattern: row.pattern.replace(/\\/g, '\\\\') });
    return {
      episodeCleanRegexRules: DEFAULT_EPISODE_CLEAN_RULES.slice(),
      episodeRules: DEFAULT_EPISODE_RULES.slice(1).map(seed),
      movieRules: DEFAULT_MOVIE_RULES.map(seed),
      aggregateRegexRules: DEFAULT_AGGREGATE_REGEX_RULES.slice()
    };
  }

  function initialize(document) {
    const defaults = initialDocument();
    let changed = document == null;
    if (document == null) document = defaults;
    const next = {};
    for (const group of GROUPS) {
      if (!Array.isArray(document[group]) || document[group].some((row) => typeof row !== 'string')) {
        throw new Error('Invalid film rule document');
      }
      if (!document[group].length) {
        next[group] = defaults[group];
        changed = true;
      } else {
        next[group] = document[group].slice();
      }
    }
    return { document: next, rules: readDocument(next), changed };
  }

  function restore(section, draft) {
    const rules = { ...draft };
    switch (section) {
      case 'episode':
        rules.episodeCleanRegexRules = DEFAULT_EPISODE_CLEAN_RULES;
        rules.episodeRules = DEFAULT_EPISODE_RULES;
        break;
      case 'movie':
        rules.movieRules = DEFAULT_MOVIE_RULES;
        break;
      case 'search':
        rules.aggregateRegexRules = DEFAULT_AGGREGATE_REGEX_RULES;
        break;
      default: throw new Error('Unknown film rule section');
    }
    return prepareSave(rules);
  }

  const FULLWIDTH_DIGITS = '０１２３４５６７８９';
  const normalizeString = (value) => typeof value === 'string' ? value.trim() : '';

  function parseChineseNumeralToInt(raw) {
    const token = normalizeString(raw)
      .replace(/[０-９]/g, (ch) => String(FULLWIDTH_DIGITS.indexOf(ch)))
      .replace(/\s+/g, '').replace(/两/g, '二').replace(/〇/g, '零');
    if (!token) return 0;
    if (/^\d+$/.test(token)) return Number.parseInt(token, 10) || 0;
    const digit = (ch) => ({ 零: 0, 一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9 }[ch] ?? -1);
    const parseSection = (section) => {
      let total = 0;
      let num = 0;
      for (let i = 0; i < section.length; i += 1) {
        const ch = section[i];
        const d = digit(ch);
        if (d >= 0) { num = d; continue; }
        const unit = ch === '十' ? 10 : ch === '百' ? 100 : ch === '千' ? 1000 : 0;
        if (!unit) continue;
        if (!num) num = 1;
        total += num * unit;
        num = 0;
      }
      return total + num;
    };
    if (token.includes('万')) {
      const [left, right] = token.split('万');
      return parseSection(left || '') * 10000 + parseSection(right || '');
    }
    return parseSection(token);
  }

  const MARKER_RE = /第\s*([0-9０-９]{1,5}|[一二三四五六七八九十百千万两零〇]{1,20})\s*([季集话部篇])/g;
  function normalizeSeasonEpisodeMarkers(text) {
    return normalizeString(text).replace(MARKER_RE, (full, token, unit) => {
      const value = parseChineseNumeralToInt(token);
      return value <= 0 ? full : `第${value}${unit}`;
    });
  }

  function normalizeReplaceTemplate(raw) {
    return typeof raw === 'string' ? raw.replace(/\\(\d+)/g, '$$$1') : '';
  }

  // Retain the current content of each group. Compiling is lazy so rules after a
  // first hit remain unevaluated. Every use resets stateful g/y expressions.
  const caches = Object.create(null);
  function groupCache(group, rows) {
    const key = JSON.stringify(rows);
    if (!caches[group] || caches[group].key !== key) caches[group] = { key, compiled: [] };
    return caches[group].compiled;
  }
  function regexAt(cache, index, row, clean) {
    if (!(index in cache)) {
      try {
        if (clean) {
          const parsed = normalizePatternInput(row);
          if (!parsed) cache[index] = null;
          else {
            let flags = parsed.flags || 'ig';
            if (!flags.includes('g')) flags += 'g';
            cache[index] = new RegExp(parsed.pattern, flags);
          }
        } else {
          cache[index] = new RegExp(String(row.pattern || '').trim(), String(row.flags || '').trim() || 'i');
        }
      } catch (_) { cache[index] = null; }
    }
    const regex = cache[index];
    if (regex) regex.lastIndex = 0;
    return regex;
  }
  function invalid(result, group, index) {
    const text = `#${index + 1} 正则无效`;
    result.errors.push({ group, ruleIndex: index + 1, text });
    return text;
  }

  function sanitizeDisplayTitle(value) {
    return String(value || '').replace(/[\u200b\u200c\u200d\ufeff]+/g, '').replace(/\s+/g, ' ').trim();
  }
  function stripEmoji(value) {
    return String(value || '').replace(/[\p{Extended_Pictographic}\p{Regional_Indicator}\u2600-\u27BF\uFE0F]/gu, '');
  }
  function aggregateDisplay(value) {
    return stripEmoji(sanitizeDisplayTitle(value)).replace(/\s+/g, ' ').trim();
  }
  function aggregateKey(value) {
    return String(value || '').toLowerCase().replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
      .replace(/[\s.\-_,，:：;；!！?？·•/\\|]+/g, '').trim();
  }

  function cleanEpisode(input, rows, result) {
    let output = normalizeSeasonEpisodeMarkers(input);
    const cache = groupCache('episodeClean', rows);
    rows.forEach((row, index) => {
      const regex = regexAt(cache, index, row, true);
      if (!regex) {
        invalid(result, 'episodeClean', index);
        result.previews.push('正则无效');
      } else {
        output = output.replace(regex, '');
        result.previews.push(String(output || '').replace(/\s+/g, ' ').trim());
      }
    });
    return normalizeSeasonEpisodeMarkers(output.replace(/\s+/g, ' ').trim());
  }

  function processMatch(request, rules, result) {
    const episode = request.section === 'episode';
    // Previews are independent computed rows on the reference page, including
    // when the test input or the matching-rule array is empty.
    const cleaned = episode ? cleanEpisode(result.inputText, rules.episodeCleanRegexRules, result) : result.inputText;
    if (!result.inputText) {
      result.text = '请输入文件名';
      return result;
    }
    const rows = episode ? rules.episodeRules : rules.movieRules;
    result.type = 'error';
    if (!rows.length) {
      result.text = '无匹配规则';
      return result;
    }
    result.processedText = cleaned;
    const failures = [];
    const cache = groupCache(request.section, rows);
    for (let index = 0; index < rows.length; index += 1) {
      const rule = rows[index];
      const regex = regexAt(cache, index, rule, false);
      if (!regex) {
        failures.push(invalid(result, request.section, index));
        continue;
      }
      if (!regex.test(cleaned)) continue;
      regex.lastIndex = 0;
      result.matched = true;
      result.matchedRuleIndex = index + 1;
      const replace = normalizeReplaceTemplate(rule.replace || '');
      if (!replace) {
        result.type = 'success';
        result.text = `命中第 ${index + 1} 条：未设置 replace（无改写）`;
        return result;
      }
      result.rewriteApplied = true;
      result.rewrittenText = cleaned.replace(regex, replace);
      if (episode) result.rewrittenText = normalizeSeasonEpisodeMarkers(result.rewrittenText);
      if (!result.rewrittenText) {
        result.text = `命中第 ${index + 1} 条，但改写失败`;
        return result;
      }
      result.type = 'success';
      result.text = `命中第 ${index + 1} 条：${result.rewrittenText}`;
      return result;
    }
    result.text = failures.length ? `未命中（${failures.join('，')}）` : '未命中';
    return result;
  }

  function processSearch(request, rules, result) {
    const trailing = String(request.query || '').trim().match(/(\d+)\s*$/);
    result.trailingDigits = trailing ? String(trailing[1] || '') : '';
    let output = stripEmoji(sanitizeDisplayTitle(request.text));
    const rows = rules.aggregateRegexRules;
    const cache = groupCache('search', rows);
    const failures = [];
    rows.forEach((row, index) => {
      const source = String(row || '').trim();
      if (!source) {
        result.previews.push(aggregateDisplay(output));
        return;
      }
      const regex = regexAt(cache, index, source, true);
      if (!regex) {
        failures.push(invalid(result, 'search', index));
        result.previews.push('正则无效');
        return;
      }
      if (result.trailingDigits) {
        const probe1 = `x${result.trailingDigits}`;
        const probe2 = `x${result.trailingDigits}y`;
        if (probe1.replace(regex, '') === 'x' && probe2.replace(regex, '') === probe2) {
          result.previews.push(`${aggregateDisplay(output)}（跳过尾号规则）`);
          return;
        }
      }
      output = output.replace(regex, '');
      result.previews.push(aggregateDisplay(output));
    });
    result.processedText = aggregateDisplay(output);
    result.groupKey = aggregateKey(output);
    if (!result.inputText) {
      result.text = '请输入搜索结果标题';
      return result;
    }
    if (failures.length) {
      result.type = 'error';
      result.text = `清洗失败（${failures.join('，')}）`;
      return result;
    }
    result.type = 'success';
    const suffix = result.groupKey ? `（聚合Key：${result.groupKey}）` : '';
    const hint = result.trailingDigits ? `（搜索尾号保护：${result.trailingDigits}）` : '';
    const noneHint = !rows.length ? '（仅默认清洗）' : '';
    result.text = `净化后：${result.processedText}${suffix}${hint}${noneHint}`;
    return result;
  }

  function process(request, rules) {
    const input = String(request.text || '').trim();
    const result = {
      section: request.section, type: '', text: '', inputText: input, processedText: input,
      matched: false, matchedRuleIndex: null, rewriteApplied: false, rewrittenText: null,
      groupKey: '', trailingDigits: '', errors: [], previews: []
    };
    switch (request.section) {
      case 'episode':
      case 'movie': return processMatch(request, rules, result);
      case 'search': return processSearch(request, rules, result);
      default: throw new Error('Unknown film rule section');
    }
  }

  globalThis.meowfilmProcess = function (request) {
    switch (request.operation) {
      case 'initialize': return initialize(request.document);
      case 'save': return prepareSave(request.rules);
      case 'restore': return restore(request.section, request.rules);
      case 'newMatch': {
        const parsed = normalizePatternInput(request.pattern);
        return parsed ? { pattern: parsed.pattern, replace: String(request.replace || '').trim(), flags: parsed.flags || '' } : null;
      }
      case 'newClean': {
        const parsed = normalizePatternInput(request.pattern);
        return parsed ? (parsed.flags ? `/${parsed.pattern}/${parsed.flags}` : parsed.pattern) : '';
      }
      case 'process': return process(request.request, request.rules);
      default: throw new Error('Unknown film rule operation');
    }
  };
})();
