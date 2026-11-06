import {
  domHTML, domText, htmlToText, isDomNode, parseMarkup, queryDom,
} from './dom.js';
import { parseExpressionAt } from 'acorn';
import { sourceFault, sourceObject, sourceRequire } from './errors.js';

const toText = value => value === null || value === undefined ? '' : String(value);

function literalNode(node) {
  switch (node?.type) {
    case 'Literal':
      sourceRequire(node.regex === undefined && typeof node.value !== 'bigint', 'UNSUPPORTED_RULE');
      return node.value;
    case 'TemplateLiteral':
      sourceRequire(node.expressions.length === 0, 'UNSUPPORTED_RULE');
      return node.quasis[0]?.value?.cooked ?? '';
    case 'UnaryExpression': {
      const value = literalNode(node.argument);
      switch (node.operator) {
        case '+': return +value;
        case '-': return -value;
        case '!': return !value;
        case '~': return ~value;
        default: throw sourceFault('UNSUPPORTED_RULE');
      }
    }
    case 'ArrayExpression':
      sourceRequire(node.elements.every(Boolean), 'UNSUPPORTED_RULE');
      return node.elements.map(literalNode);
    case 'ObjectExpression': {
      const result = {};
      for (const property of node.properties) {
        sourceRequire(property.type === 'Property' && property.kind === 'init' &&
          !property.method && !property.shorthand, 'UNSUPPORTED_RULE');
        const key = property.computed
          ? literalNode(property.key)
          : property.key.type === 'Identifier' ? property.key.name : property.key.value;
        sourceRequire(typeof key === 'string' || typeof key === 'number', 'UNSUPPORTED_RULE');
        const name = String(key);
        sourceRequire(!Object.hasOwn(result, name) &&
          !['__proto__', 'prototype', 'constructor'].includes(name), 'UNSUPPORTED_RULE');
        result[name] = literalNode(property.value);
      }
      return result;
    }
    default: throw sourceFault('UNSUPPORTED_RULE');
  }
}

export function parseLiteral(value) {
  sourceRequire(typeof value === 'string');
  const input = value.trim();
  sourceRequire(input.length > 0, 'UNSUPPORTED_RULE');
  try {
    const expression = parseExpressionAt(input, 0, { ecmaVersion: 'latest' });
    sourceRequire(input.slice(expression.end).trim() === '', 'UNSUPPORTED_RULE');
    return literalNode(expression);
  } catch (error) {
    if (error?.code) throw error;
    throw sourceFault('UNSUPPORTED_RULE');
  }
}

export function parseRuleObject(value) {
  if (!value) return {};
  if (value && typeof value === 'object' && !Array.isArray(value)) return value;
  if (typeof value !== 'string') return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch {
    try {
      const parsed = parseLiteral(value);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
    } catch { return {}; }
  }
}

export function splitTopLevel(rule, operator) {
  const source = toText(rule);
  const parts = [];
  let start = 0, braces = 0, brackets = 0, quote = '';
  for (let index = 0; index < source.length; index++) {
    const character = source[index];
    if (quote) {
      if (character === '\\') index++;
      else if (character === quote) quote = '';
      continue;
    }
    if (character === '"' || character === "'" || character === '`') { quote = character; continue; }
    if (source.startsWith('{{', index)) { braces++; index++; continue; }
    if (source.startsWith('}}', index) && braces) { braces--; index++; continue; }
    if (character === '[' || character === '(') brackets++;
    else if (character === ']' || character === ')') brackets--;
    else if (!braces && !brackets && source.startsWith(operator, index)) {
      parts.push(source.slice(start, index));
      index += operator.length - 1;
      start = index + 1;
    }
  }
  if (!parts.length) return null;
  parts.push(source.slice(start));
  return parts;
}

export function applyReplacement(value, rule) {
  const source = toText(rule);
  let parts = splitTopLevel(source, '##');
  if (!parts) {
    const legacy = [];
    let start = 0, quote = '', square = 0, round = 0;
    for (let index = 0; index < source.length; index++) {
      const character = source[index];
      if (quote) {
        if (character === '\\') index++;
        else if (character === quote) quote = '';
        continue;
      }
      if (character === '"' || character === "'" || character === '`') quote = character;
      else if (character === '[') square++;
      else if (character === ']') square--;
      else if (character === '(') round++;
      else if (character === ')') round--;
      else if (character === '#' && source[index - 1] !== '\\' && square === 0 && round === 0) {
        legacy.push(source.slice(start, index));
        start = index + 1;
      }
    }
    if (legacy.length >= 2) {
      legacy.push(source.slice(start));
      parts = legacy;
    }
  }
  parts ??= [source];
  if (parts.length < 2 || !parts[1]) return { rule: parts[0], value, changed: false };
  try {
    const pattern = new RegExp(parts[1], 'g');
    const replacement = parts[2] ?? '';
    if (source.trimEnd().endsWith('###')) {
      const match = pattern.exec(toText(value));
      return { rule: parts[0], value: match ? match[0].replace(pattern, replacement) : '', changed: true };
    }
    return { rule: parts[0], value: toText(value).replace(pattern, replacement), changed: true };
  } catch {
    return { rule: parts[0], value: toText(value).split(parts[1]).join(parts[2] ?? ''), changed: true };
  }
}

function parseJson(value) {
  if (value && typeof value === 'object' && !isDomNode(value)) return value;
  if (typeof value !== 'string') return null;
  const text = value.trim();
  if (!text.startsWith('{') && !text.startsWith('[')) return null;
  try { return JSON.parse(text); } catch { return null; }
}

function bracketEnd(source, start) {
  let quote = '', depth = 0;
  for (let index = start; index < source.length; index++) {
    const character = source[index];
    if (quote) {
      if (character === '\\') index++;
      else if (character === quote) quote = '';
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === '[' || character === '(') depth++;
    else if ((character === ']' || character === ')') && --depth === 0) return index;
  }
  throw sourceFault('UNSUPPORTED_RULE');
}

function jsonPathTokens(path) {
  const source = String(path).trim().replace(/^@json:/i, '');
  sourceRequire(source.startsWith('$'), 'UNSUPPORTED_RULE');
  const tokens = [];
  let index = 1;
  const identifier = () => {
    const match = /^[A-Za-z_$][\w$-]*/.exec(source.slice(index));
    sourceRequire(match, 'UNSUPPORTED_RULE');
    index += match[0].length;
    return match[0];
  };
  while (index < source.length) {
    if (source.startsWith('..', index)) {
      index += 2;
      if (source[index] === '[') tokens.push({ kind: 'recursive', key: '*' });
      else if (source[index] === '*') { index++; tokens.push({ kind: 'recursive', key: '*' }); }
      else tokens.push({ kind: 'recursive', key: identifier() });
      continue;
    }
    if (source[index] === '.') {
      index++;
      if (source[index] === '*') { index++; tokens.push({ kind: 'wildcard' }); }
      else tokens.push({ kind: 'key', key: identifier() });
      continue;
    }
    if (source[index] !== '[') throw sourceFault('UNSUPPORTED_RULE');
    const end = bracketEnd(source, index);
    const content = source.slice(index + 1, end).trim();
    index = end + 1;
    if (content === '*') tokens.push({ kind: 'wildcard' });
    else if (/^-?\d+$/.test(content)) tokens.push({ kind: 'index', index: Number(content) });
    else if (/^-?\d*\s*:\s*-?\d*$/.test(content)) {
      const [start, finish] = content.split(':').map(value => value.trim());
      tokens.push({
        kind: 'slice',
        start: start === '' ? null : Number(start),
        end: finish === '' ? null : Number(finish),
      });
    } else if (/^\?\([\s\S]*\)$/.test(content)) {
      tokens.push({ kind: 'filter', expression: content.slice(2, -1).trim() });
    } else {
      const parts = splitTopLevel(content, ',') ?? [content];
      const keys = parts.map(part => {
        const value = part.trim();
        if (/^(['"])[\s\S]*\1$/.test(value)) return parseLiteral(value);
        if (/^-?\d+$/.test(value)) return Number(value);
        throw sourceFault('UNSUPPORTED_RULE');
      });
      tokens.push({ kind: 'union', keys });
    }
  }
  return tokens;
}

function descendants(value, key, output) {
  if (!value || typeof value !== 'object') return;
  if (Array.isArray(value)) {
    for (const child of value) {
      if (key === '*') output.push(child);
      descendants(child, key, output);
    }
    return;
  }
  for (const [name, child] of Object.entries(value)) {
    if (key === '*' || name === key) output.push(child);
    descendants(child, key, output);
  }
}

function filterPath(value, path) {
  const normalized = path.trim().replace(/^@/, '$');
  try { return queryJson(value, normalized); }
  catch { return []; }
}

function filterValue(item, expression) {
  const parsed = /^(@(?:\.[A-Za-z_$][\w$-]*|\[['"][^'"]+['"]\])*)\s*(===|!==|==|!=|>=|<=|>|<|=~)?\s*([\s\S]*)$/
    .exec(expression);
  sourceRequire(parsed, 'UNSUPPORTED_RULE');
  const values = filterPath(item, parsed[1]);
  if (!parsed[2]) return values.some(Boolean);
  let expected;
  if (parsed[2] === '=~') {
    const regex = /^\/([\s\S]*)\/([dgimsuvy]*)$/.exec(parsed[3].trim());
    sourceRequire(regex, 'UNSUPPORTED_RULE');
    let pattern;
    try { pattern = new RegExp(regex[1], regex[2].replace('g', '')); }
    catch { throw sourceFault('UNSUPPORTED_RULE'); }
    return values.some(value => pattern.test(toText(value)));
  }
  try { expected = parseLiteral(parsed[3].trim()); }
  catch {
    if (parsed[3].trim() === 'true') expected = true;
    else if (parsed[3].trim() === 'false') expected = false;
    else if (parsed[3].trim() === 'null') expected = null;
    else expected = parsed[3].trim();
  }
  return values.some(value => {
    switch (parsed[2]) {
      case '===': return value === expected;
      case '!==': return value !== expected;
      case '==': return value == expected; // Protocol compatibility intentionally follows JSONPath coercion.
      case '!=': return value != expected;
      case '>=': return value >= expected;
      case '<=': return value <= expected;
      case '>': return value > expected;
      case '<': return value < expected;
      default: return false;
    }
  });
}

export function queryJson(value, path) {
  let current = [value];
  for (const token of jsonPathTokens(path)) {
    const next = [];
    for (const item of current) {
      if (token.kind === 'wildcard') {
        if (Array.isArray(item)) next.push(...item);
        else if (item && typeof item === 'object') next.push(...Object.values(item));
      } else if (token.kind === 'index') {
        if (Array.isArray(item)) {
          const index = token.index < 0 ? item.length + token.index : token.index;
          if (index >= 0 && index < item.length) next.push(item[index]);
        }
      } else if (token.kind === 'slice') {
        if (Array.isArray(item)) next.push(...item.slice(token.start ?? 0, token.end ?? item.length));
      } else if (token.kind === 'recursive') descendants(item, token.key, next);
      else if (token.kind === 'filter') {
        const values = Array.isArray(item) ? item : item && typeof item === 'object'
          ? Object.values(item) : [];
        next.push(...values.filter(child => filterValue(child, token.expression)));
      } else if (token.kind === 'union') {
        for (const key of token.keys) {
          if (typeof key === 'number' && Array.isArray(item)) {
            const index = key < 0 ? item.length + key : key;
            if (index >= 0 && index < item.length) next.push(item[index]);
          } else if (item && typeof item === 'object' && Object.hasOwn(item, key)) next.push(item[key]);
        }
      } else if (item && typeof item === 'object' && Object.hasOwn(item, token.key)) {
        next.push(item[token.key]);
      }
    }
    current = next;
  }
  return current.filter(value => value !== null && value !== undefined);
}

function regexValues(rule, content) {
  try {
    return [...toText(content).matchAll(new RegExp(rule, 'g'))]
      .map(match => ({
        value: match[0],
        groups: match.slice(1),
        namedGroups: match.groups ? { ...match.groups } : {},
        index: match.index ?? 0,
      }));
  } catch { throw sourceFault('UNSUPPORTED_RULE'); }
}

function scriptSplit(rule) {
  const source = toText(rule);
  const index = source.search(/@js:|<js>/i);
  if (index < 0) return null;
  const prefix = source.slice(0, index).trim();
  const body = source.slice(index);
  if (body.toLowerCase().startsWith('@js:')) return { prefix, script: body.slice(4), suffix: '' };
  const open = body.indexOf('>');
  const close = body.toLowerCase().indexOf('</js>');
  return {
    prefix,
    script: body.slice(open + 1, close < 0 ? undefined : close),
    suffix: close < 0 ? '' : body.slice(close + 5).trim(),
  };
}

function webScript(rule) {
  const source = toText(rule).trim();
  if (source.toLowerCase().startsWith('@webjs:')) return source.slice(7);
  if (source.toLowerCase().startsWith('<webjs>') && source.toLowerCase().endsWith('</webjs>')) {
    return source.slice(7, -8);
  }
  return null;
}

function stripDirectives(rule) {
  let source = toText(rule).trim();
  while (/^@header\s*:/i.test(source)) {
    const start = source.indexOf('{');
    sourceRequire(start >= 0, 'UNSUPPORTED_RULE');
    let depth = 0, quote = '', end = -1;
    for (let index = start; index < source.length; index++) {
      const character = source[index];
      if (quote) {
        if (character === '\\') index++;
        else if (character === quote) quote = '';
        continue;
      }
      if (character === '"' || character === "'") quote = character;
      else if (character === '{') depth++;
      else if (character === '}' && --depth === 0) { end = index + 1; break; }
    }
    sourceRequire(end > start, 'UNSUPPORTED_RULE');
    source = source.slice(end).trim();
  }
  return source;
}

function directiveObject(source, name) {
  const prefix = `@${name}:`;
  if (!source.toLowerCase().startsWith(prefix)) return null;
  const start = source.indexOf('{', prefix.length);
  sourceRequire(start >= 0, 'UNSUPPORTED_RULE');
  let depth = 0, quote = '', end = -1;
  for (let index = start; index < source.length; index++) {
    const character = source[index];
    if (quote) {
      if (character === '\\') index++;
      else if (character === quote) quote = '';
      continue;
    }
    if (character === '"' || character === "'" || character === '`') quote = character;
    else if (character === '{') depth++;
    else if (character === '}' && --depth === 0) { end = index + 1; break; }
  }
  sourceRequire(end > start, 'UNSUPPORTED_RULE');
  return {
    value: parseRuleObject(source.slice(start, end)),
    rest: source.slice(end).trim(),
  };
}

async function expandVariables(rule, content, context) {
  let source = toText(rule).trim();
  const put = directiveObject(source, 'put');
  if (put) {
    sourceRequire(typeof context.setVariable === 'function', 'UNSUPPORTED_RULE');
    for (const [key, childRule] of Object.entries(put.value)) {
      const value = await readRuleString(childRule, content, context);
      context.setVariable(key, value);
    }
    source = put.rest;
    if (!source) return { source: '', literal: false, passthrough: true };
  }
  const matches = [...source.matchAll(/@get:\{([^{}]+)\}/gi)];
  if (!matches.length) return { source, literal: false, passthrough: false };
  sourceRequire(typeof context.getVariable === 'function', 'UNSUPPORTED_RULE');
  let result = '', cursor = 0;
  for (const match of matches) {
    result += source.slice(cursor, match.index);
    result += toText(context.getVariable(match[1].trim()));
    cursor = match.index + match[0].length;
  }
  return { source: result + source.slice(cursor), literal: true, passthrough: false };
}

async function replaceTemplates(rule, content, context) {
  const source = toText(rule);
  const matches = [...source.matchAll(/\{\{([\s\S]*?)\}\}/g)];
  if (!matches.length) return source;
  let result = '', cursor = 0;
  for (const match of matches) {
    result += source.slice(cursor, match.index);
    const expression = match[1].trim();
    if (/^(?:\$|@json:|\/\/|\/|@css:)/.test(expression)) {
      result += await readRuleString(expression, content, context);
    } else {
      result += toText(await context.evaluateScript(expression, {
        result: content, baseUrl: context.baseUrl, key: context.key, page: context.page,
      }));
    }
    cursor = match.index + match[0].length;
  }
  return result + source.slice(cursor);
}

function normalizeValues(values) {
  return values.flatMap(value => Array.isArray(value) ? value : [value])
    .filter(value => value !== null && value !== undefined && value !== '');
}

function ruleValue(value) {
  if (isDomNode(value)) return domText(value);
  if (value && typeof value === 'object' && typeof value.value === 'string' &&
      Array.isArray(value.groups) && Number.isSafeInteger(value.index)) return value.value;
  return value;
}

async function singleRule(rule, content, context) {
  const expanded = (await replaceTemplates(rule, content, context)).trim();
  if (!expanded || expanded === '@') return [content];
  if (expanded.startsWith(':')) return regexValues(expanded.slice(1), content);
  if (/^https?:\/\//i.test(expanded) || expanded.startsWith('/')) {
    if (!expanded.startsWith('//')) return [expanded];
  }
  const json = parseJson(content);
  if (json !== null && (/^(?:\$|@json:)/i.test(expanded) || /^[A-Za-z_$][\w$.-]*(?:\[|$)/.test(expanded))) {
    const path = /^(?:\$|@json:)/i.test(expanded) ? expanded : `$.${expanded}`;
    return queryJson(json, path);
  }
  const root = isDomNode(content)
    ? content : parseMarkup(toText(content), context.maximumNodes, context.maximumParseBytes);
  return queryDom(root, expanded);
}

function singleStaticRule(rule, content, context) {
  const expanded = toText(rule).trim();
  if (!expanded || expanded === '@') return [content];
  if (expanded.includes('{{') || scriptSplit(expanded)) throw sourceFault('UNSUPPORTED_RULE');
  if (expanded.startsWith(':')) return regexValues(expanded.slice(1), content);
  if (/^https?:\/\//i.test(expanded) || expanded.startsWith('/')) {
    if (!expanded.startsWith('//')) return [expanded];
  }
  const json = parseJson(content);
  if (json !== null && (/^(?:\$|@json:)/i.test(expanded) || /^[A-Za-z_$][\w$.-]*(?:\[|$)/.test(expanded))) {
    const path = /^(?:\$|@json:)/i.test(expanded) ? expanded : `$.${expanded}`;
    return queryJson(json, path);
  }
  const root = isDomNode(content)
    ? content : parseMarkup(toText(content), context.maximumNodes, context.maximumParseBytes);
  return queryDom(root, expanded);
}

function readStaticRuleValuesCore(raw, content, context) {
  if (!raw) return [];
  const or = splitTopLevel(raw, '||');
  if (or) {
    for (const part of or) {
      const values = readStaticRuleValues(part, content, context);
      if (values.length) return values;
    }
    return [];
  }
  const and = splitTopLevel(raw, '&&');
  if (and) return normalizeValues(and.flatMap(part => readStaticRuleValues(part, content, context)));
  const replacement = applyReplacement('', raw);
  const values = replacement.rule ? singleStaticRule(replacement.rule, content, context) : [content];
  return normalizeValues(values.map(value => replacement.changed
    ? applyReplacement(ruleValue(value), raw).value
    : value));
}

export function readStaticRuleValues(rule, content, context = {}) {
  let raw = stripDirectives(rule);
  let reverse = false;
  if (raw.startsWith('-')) { reverse = true; raw = raw.slice(1); }
  if (raw.startsWith('+')) raw = raw.slice(1);
  const values = readStaticRuleValuesCore(raw, content, context);
  return reverse ? [...values].reverse() : values;
}

export function readStaticRuleString(rule, content, context = {}) {
  return readStaticRuleValues(rule, content, context).map(valueText).join('\n');
}

async function readRuleValuesCore(raw, content, context) {
  if (!raw) return [];
  const expandedVariables = await expandVariables(raw, content, context);
  if (expandedVariables.passthrough) return [content];
  raw = expandedVariables.source;
  if (expandedVariables.literal && !scriptSplit(raw)) return normalizeValues([raw]);
  const browserScript = webScript(raw);
  if (browserScript !== null) {
    sourceRequire(typeof context.evaluateWeb === 'function', 'WEB_INTERACTION_REQUIRED');
    return normalizeValues([await context.evaluateWeb(browserScript, content)]);
  }
  const script = scriptSplit(raw);
  if (script) {
    const initial = script.prefix
      ? expandedVariables.literal
        ? script.prefix
        : await readRuleValues(script.prefix, content, context)
      : content;
    const result = await context.evaluateScript(await replaceTemplates(script.script, content, context), {
      result: Array.isArray(initial) && initial.length === 1 ? initial[0] : initial,
      baseUrl: context.baseUrl, key: context.key, page: context.page,
      source: context.source, book: context.book, chapter: context.chapter,
    });
    return script.suffix ? readRuleValues(script.suffix, result, context) : normalizeValues([result]);
  }
  const or = splitTopLevel(raw, '||');
  if (or) {
    for (const part of or) {
      const values = await readRuleValues(part, content, context);
      if (values.length) return values;
    }
    return [];
  }
  const and = splitTopLevel(raw, '&&');
  if (and) {
    const values = [];
    for (const part of and) values.push(...await readRuleValues(part, content, context));
    return normalizeValues(values);
  }
  const replacement = applyReplacement('', raw);
  const values = replacement.rule ? await singleRule(replacement.rule, content, context) : [content];
  return normalizeValues(values.map(value => replacement.changed
    ? applyReplacement(ruleValue(value), raw).value
    : value));
}

export async function readRuleValues(rule, content, context = {}) {
  sourceRequire(typeof context.evaluateScript === 'function');
  let raw = stripDirectives(rule);
  let reverse = false;
  if (raw.startsWith('-')) { reverse = true; raw = raw.slice(1); }
  if (raw.startsWith('+')) raw = raw.slice(1);
  const values = await readRuleValuesCore(raw, content, context);
  return reverse ? [...values].reverse() : values;
}

function valueText(value) {
  value = ruleValue(value);
  if (typeof value === 'object' && value !== null) return JSON.stringify(value);
  return toText(value);
}

export async function readRuleString(rule, content, context = {}, { url = false, html = false } = {}) {
  const values = await readRuleValues(rule, content, context);
  let result = values.map(value => html && isDomNode(value) ? domHTML(value) : valueText(value)).join('\n');
  if (url && result) {
    try { result = new URL(result, context.baseUrl || context.source?.bookSourceUrl || undefined).href; }
    catch { /* Preserve non-URL values for the caller to classify. */ }
  }
  return result;
}

export function normalizeBook(book, source = {}, maximumNodes, maximumParseBytes) {
  sourceObject(book);
  return {
    name: toText(book.name).replace(/[《》]/g, '').trim(),
    author: toText(book.author).replace(/^作者[:：\s]*/, '').trim(),
    kind: toText(book.kind),
    coverUrl: toText(book.coverUrl),
    intro: htmlToText(book.intro, maximumNodes, maximumParseBytes),
    bookUrl: toText(book.bookUrl || book.url),
    tocUrl: toText(book.tocUrl || book.bookUrl || book.url),
    wordCount: toText(book.wordCount),
    latestChapterTitle: toText(book.latestChapterTitle || book.lastChapter),
    type: Number(book.type ?? source.bookSourceType ?? 0) || 0,
    origin: toText(source.bookSourceUrl),
    originName: toText(source.bookSourceName),
    variable: toText(book.variable),
  };
}
