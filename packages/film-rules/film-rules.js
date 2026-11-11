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

  function storedRows(rows) {
    // Go strings.TrimSpace in the dashboard handler and DB, after the page's
    // JS trim. NEL is Go whitespace; BOM is not. Do not merge the two stages.
    return rows.map((row) => row.replace(
      /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g,
      '')).filter(Boolean);
  }

  function prepareSave(rules) {
    // Retain the page/handler save normalization. Encoded rows are only a
    // normalization intermediate; the Node owner persists decoded rules.
    const normalized = {
      episodeCleanRegexRules: cleanRows(rules.episodeCleanRegexRules),
      episodeRules: rules.episodeRules.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
      movieRules: rules.movieRules.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
      aggregateRegexRules: cleanRows(rules.aggregateRegexRules)
    };
    for (const group of GROUPS) normalized[group] = storedRows(normalized[group]);
    const decode = (row) => {
      const value = JSON.parse(row);
      return { pattern: normalizeRegexText(value.pattern.trim()),
        replace: value.replace, flags: value.flags || '' };
    };
    return { rules: {
      episodeCleanRegexRules: cleanRows(normalized.episodeCleanRegexRules),
      episodeRules: normalized.episodeRules.map(decode),
      movieRules: normalized.movieRules.map(decode),
      aggregateRegexRules: cleanRows(normalized.aggregateRegexRules)
    } };
  }

  function initialRules() {
    return {
      episodeCleanRegexRules: DEFAULT_EPISODE_CLEAN_RULES.slice(),
      episodeRules: DEFAULT_EPISODE_RULES.slice(1).map(row => ({ ...row })),
      movieRules: DEFAULT_MOVIE_RULES.map(row => ({ ...row })),
      aggregateRegexRules: DEFAULT_AGGREGATE_REGEX_RULES.slice()
    };
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
      } catch (error) {
        if (!(error instanceof SyntaxError)) throw error;
        cache[index] = null;
      }
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
    // Unicode 17.0 (Node 24.21.0): Extended_Pictographic | Regional_Indicator | U+2600–27BF | FE0F, without ICU.
    return String(value || '').replace(/[\u00A9\u00AE\u203C\u2049\u2122\u2139\u2194-\u2199\u21A9-\u21AA\u231A-\u231B\u2328\u23CF\u23E9-\u23F3\u23F8-\u23FA\u24C2\u25AA-\u25AB\u25B6\u25C0\u25FB-\u25FE\u2600-\u27BF\u2934-\u2935\u2B05-\u2B07\u2B1B-\u2B1C\u2B50\u2B55\u3030\u303D\u3297\u3299\uFE0F\u{1F004}\u{1F02C}-\u{1F02F}\u{1F094}-\u{1F09F}\u{1F0AF}-\u{1F0B0}\u{1F0C0}\u{1F0CF}-\u{1F0D0}\u{1F0F6}-\u{1F0FF}\u{1F170}-\u{1F171}\u{1F17E}-\u{1F17F}\u{1F18E}\u{1F191}-\u{1F19A}\u{1F1AE}-\u{1F1FF}\u{1F201}-\u{1F20F}\u{1F21A}\u{1F22F}\u{1F232}-\u{1F23A}\u{1F23C}-\u{1F23F}\u{1F249}-\u{1F25F}\u{1F266}-\u{1F321}\u{1F324}-\u{1F393}\u{1F396}-\u{1F397}\u{1F399}-\u{1F39B}\u{1F39E}-\u{1F3F0}\u{1F3F3}-\u{1F3F5}\u{1F3F7}-\u{1F3FA}\u{1F400}-\u{1F4FD}\u{1F4FF}-\u{1F53D}\u{1F549}-\u{1F54E}\u{1F550}-\u{1F567}\u{1F56F}-\u{1F570}\u{1F573}-\u{1F57A}\u{1F587}\u{1F58A}-\u{1F58D}\u{1F590}\u{1F595}-\u{1F596}\u{1F5A4}-\u{1F5A5}\u{1F5A8}\u{1F5B1}-\u{1F5B2}\u{1F5BC}\u{1F5C2}-\u{1F5C4}\u{1F5D1}-\u{1F5D3}\u{1F5DC}-\u{1F5DE}\u{1F5E1}\u{1F5E3}\u{1F5E8}\u{1F5EF}\u{1F5F3}\u{1F5FA}-\u{1F64F}\u{1F680}-\u{1F6C5}\u{1F6CB}-\u{1F6D2}\u{1F6D5}-\u{1F6E5}\u{1F6E9}\u{1F6EB}-\u{1F6F0}\u{1F6F3}-\u{1F6FF}\u{1F7DA}-\u{1F7FF}\u{1F80C}-\u{1F80F}\u{1F848}-\u{1F84F}\u{1F85A}-\u{1F85F}\u{1F888}-\u{1F88F}\u{1F8AE}-\u{1F8AF}\u{1F8BC}-\u{1F8BF}\u{1F8C2}-\u{1F8CF}\u{1F8D9}-\u{1F8FF}\u{1F90C}-\u{1F93A}\u{1F93C}-\u{1F945}\u{1F947}-\u{1F9FF}\u{1FA58}-\u{1FA5F}\u{1FA6E}-\u{1FAFF}\u{1FC00}-\u{1FFFD}]/gu, '');
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
    result.processedText = cleaned;
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
    request = { ...request, text: ruleText(request.text), query: ruleText(request.query) };
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

  function ruleText(value) {
    if (!Array.isArray(value)) return value;
    if (value.some(unit => !Number.isInteger(unit) || unit < 0 || unit > 65535)) {
      throw new Error('Invalid film rule text');
    }
    return value.map(unit => String.fromCharCode(unit)).join('');
  }

  function wireText(value) {
    if (typeof value === 'string') {
      for (let index = 0; index < value.length; index++) {
        const unit = value.charCodeAt(index);
        if (unit >= 0xd800 && unit <= 0xdbff) {
          const next = value.charCodeAt(index + 1);
          if (next >= 0xdc00 && next <= 0xdfff) { index++; continue; }
        } else if (unit < 0xdc00 || unit > 0xdfff) continue;
        return Array.from({ length: value.length }, (_, i) => value.charCodeAt(i));
      }
    } else if (Array.isArray(value)) return value.map(wireText);
    else if (value && typeof value === 'object') {
      return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, wireText(item)]));
    }
    return value;
  }

  globalThis.meowfilmProcess = function (request) {
    switch (request.operation) {
      case 'defaults': return { rules: initialRules() };
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
      case 'preview': return wireText(process(request.request, request.rules));
      default: throw new Error('Unknown film rule operation');
    }
  };
})();
