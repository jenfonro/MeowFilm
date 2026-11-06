import fontoxpath from 'fontoxpath';
import { DOMParser } from 'linkedom';
import { sourceFault, sourceObject, sourceRequire } from './errors.js';

const { evaluateXPathToNodes, evaluateXPathToStrings } = fontoxpath;
const htmlType = 'text/html';
const semanticAttributes = new Set([
  'text', 'textnodes', 'owntext', 'html', 'outerhtml', 'all', 'href', 'src',
]);
const htmlTags = new Set([
  'a', 'article', 'aside', 'b', 'body', 'br', 'button', 'dd', 'div', 'dl', 'dt',
  'em', 'footer', 'form', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'head', 'header',
  'html', 'i', 'iframe', 'img', 'input', 'label', 'li', 'link', 'main', 'meta',
  'nav', 'ol', 'option', 'p', 'script', 'section', 'select', 'small', 'source',
  'span', 'strong', 'style', 'table', 'tbody', 'td', 'textarea', 'th', 'thead',
  'title', 'tr', 'ul', 'video',
]);
const cssIdentifier = value => String(value).replace(
  /(^-?\d)|(^-$)|[^\w\u0080-\uFFFF-]/gu,
  character => `\\${character.codePointAt(0).toString(16)} `,
);
const defaultMaximumNodes = 20_000;
const defaultMaximumParseBytes = 8 * 1024 * 1024;
const textEncoder = new TextEncoder();

function countNodeForest(roots, maximumNodes = defaultMaximumNodes) {
  sourceRequire(Number.isSafeInteger(maximumNodes) && maximumNodes > 0);
  const stack = [...roots];
  const seen = new Set();
  let count = 0;
  while (stack.length) {
    const node = stack.pop();
    if (seen.has(node)) continue;
    seen.add(node);
    sourceRequire(++count <= maximumNodes, 'RESOURCE_LIMIT');
    for (const child of node?.childNodes ?? []) stack.push(child);
  }
  return count;
}

export function countDomNodes(root, maximumNodes = defaultMaximumNodes) {
  return countNodeForest([root], maximumNodes);
}

export function parseMarkup(value, maximumNodes = defaultMaximumNodes, maximumParseBytes = defaultMaximumParseBytes) {
  const source = typeof value === 'string' ? value : String(value ?? '');
  sourceRequire(textEncoder.encode(source).length <= maximumParseBytes, 'RESOURCE_LIMIT');
  const document = new DOMParser().parseFromString(source, htmlType);
  countDomNodes(document, maximumNodes);
  return document;
}

export function isDomNode(value) {
  return Boolean(value && typeof value === 'object' && Number.isInteger(value.nodeType));
}

export function domText(node, direct = false) {
  if (!isDomNode(node)) return String(node ?? '');
  if (!direct) {
    const text = value => {
      if (!isDomNode(value)) return String(value ?? '');
      if (value.textContent !== null && value.textContent !== undefined) {
        return String(value.textContent);
      }
      if (value.nodeType === 3) return String(value.nodeValue ?? '');
      return [...(value.childNodes ?? [])].map(text).join('');
    };
    return text(node).trim();
  }
  return [...(node.childNodes ?? [])]
    .filter(child => child.nodeType === 3)
    .map(child => child.nodeValue ?? '')
    .join('')
    .trim();
}

export function domHTML(node, outer = false) {
  if (!isDomNode(node)) return String(node ?? '');
  return String(outer ? node.outerHTML ?? node.toString?.() ?? '' : node.innerHTML ?? '');
}

function normalizeSelector(input) {
  let selector = String(input ?? '').trim();
  if (selector.startsWith('@css:')) selector = selector.slice(5).trim();
  if (selector.startsWith('body&&')) selector = selector.slice(6).trim();
  const legacySelection = /((?:\.-?\d+(?::-?\d*)?|!-?\d+|:-?\d*(?::-?\d*)?))$/
    .exec(selector)?.[1] ?? '';
  const legacyBody = legacySelection ? selector.slice(0, -legacySelection.length) : selector;
  if (legacyBody.startsWith('tag.')) selector = legacyBody.slice(4) + legacySelection;
  else if (selector.startsWith('class.')) {
    selector = legacyBody.slice(6).trim().split(/[\s.]+/).filter(Boolean)
      .map(name => `.${cssIdentifier(name)}`).join('');
    selector += legacySelection;
  }
  else if (legacyBody.startsWith('id.')) selector = `#${cssIdentifier(legacyBody.slice(3))}${legacySelection}`;
  else if (selector.startsWith('text.')) {
    selector = `*:contains(${JSON.stringify(selector.slice(5))})`;
  }
  else if (selector === 'children') selector = ':scope > *';
  return selector || ':scope';
}

function splitAtSigns(rule) {
  const source = String(rule ?? '').trim();
  const parts = [];
  let start = 0, depth = 0, quote = '';
  for (let index = 0; index < source.length; index++) {
    const character = source[index];
    if (quote) {
      if (character === '\\') index++;
      else if (character === quote) quote = '';
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === '[' || character === '(') depth++;
    else if (character === ']' || character === ')') depth--;
    else if (character === '@' && source[index - 1] !== '/' && depth === 0 && index > start) {
      parts.push(source.slice(start, index));
      start = index + 1;
    }
  }
  parts.push(source.slice(start));
  return parts;
}

function selectorSegment(value) {
  const segment = value.trim();
  if (/^(?:class|id|tag|text)\./.test(segment) || /^[.#[:]/.test(segment) ||
      /^(?:children|body&&)/.test(segment)) return true;
  const match = /^([A-Za-z][\w-]*)(?:(?:\.-?\d+|!-?\d+|:-?\d*(?::-?\d*)?))?$/.exec(segment);
  return Boolean(match && htmlTags.has(match[1].toLowerCase()));
}

function splitAttribute(rule) {
  let source = String(rule ?? '').trim();
  if (source.startsWith('@css:')) source = source.slice(5).trim();
  if (source.startsWith('/') || !source.includes('@')) {
    return { selector: source, attributes: [] };
  }
  const parts = splitAtSigns(source);
  const selectors = [parts.shift()];
  while (parts.length &&
      !(parts.length === 1 && semanticAttributes.has(parts[0].trim().toLowerCase())) &&
      selectorSegment(parts[0])) {
    selectors.push(parts.shift());
  }
  return {
    selector: selectors.map(normalizeSelector).join(' '),
    attributes: parts.flatMap(value => value.split('|')).map(value => value.trim()).filter(Boolean),
  };
}

function splitSelectorList(value) {
  const input = String(value);
  const result = [];
  let start = 0, square = 0, round = 0, quote = '';
  for (let index = 0; index < input.length; index++) {
    const character = input[index];
    if (quote) {
      if (character === '\\') index++;
      else if (character === quote) quote = '';
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === '[') square++;
    else if (character === ']') square--;
    else if (character === '(') round++;
    else if (character === ')') round--;
    else if (square === 0 && round === 0 &&
        (character === ',' ||
          character === '|' && input[index - 1] !== '|' && input[index + 1] !== '|' ||
          character === '&' && input[index - 1] !== '&' && input[index + 1] !== '&')) {
      result.push(input.slice(start, index));
      start = index + 1;
    }
  }
  result.push(input.slice(start));
  return result.map(item => item.trim()).filter(Boolean);
}

function selectorPlan(input) {
  let selector = normalizeSelector(input);
  const contains = [];
  selector = selector.replace(/:contains\(\s*(?:(["'])(.*?)\1|([^)]*))\s*\)/g,
    (_match, _quote, quoted, plain) => {
      contains.push(String(quoted ?? plain ?? ''));
      return '';
    });
  let selection = null;
  const match = /^(.*?)(?::eq\((-?\d+)\)|\.(-?\d+)|!(-?\d+)|:(first|last)|:(lt|gt)\((-?\d+)\)|\.(-?\d+):(-?\d*)|:(-?\d*):(-?\d*))$/
    .exec(selector);
  if (match && match[1].trim() && !/[>\s+~]$/.test(match[1])) {
    selector = match[1];
    if (match[2] !== undefined || match[3] !== undefined) {
      selection = { kind: 'index', value: Number(match[2] ?? match[3]) };
    } else if (match[4] !== undefined) {
      selection = { kind: 'exclude', value: Number(match[4]) };
    } else if (match[5]) {
      selection = { kind: 'index', value: match[5] === 'first' ? 0 : -1 };
    } else if (match[6]) {
      selection = { kind: match[6], value: Number(match[7]) };
    } else if (match[8] !== undefined) {
      selection = {
        kind: 'slice',
        start: Number(match[8]),
        end: match[9] === '' ? null : Number(match[9]),
      };
    } else {
      selection = {
        kind: 'slice',
        start: match[10] === '' ? null : Number(match[10]),
        end: match[11] === '' ? null : Number(match[11]),
      };
    }
  }
  return { selector: selector.trim() || ':scope', contains, selection };
}

function applySelection(nodes, selection) {
  if (!selection) return nodes;
  const index = selection.value < 0 ? nodes.length + selection.value : selection.value;
  if (selection.kind === 'index') return index >= 0 && index < nodes.length ? [nodes[index]] : [];
  if (selection.kind === 'exclude') return nodes.filter((_node, at) => at !== index);
  if (selection.kind === 'lt') {
    const end = selection.value < 0 ? Math.max(0, nodes.length + selection.value) : selection.value;
    return nodes.slice(0, end);
  }
  if (selection.kind === 'gt') {
    const start = selection.value < 0 ? nodes.length + selection.value : selection.value;
    return nodes.slice(start + 1);
  }
  if (selection.kind === 'slice') {
    return nodes.slice(selection.start ?? 0, selection.end ?? nodes.length);
  }
  return nodes;
}

function queryCSSSingle(root, input) {
  const { selector, contains, selection } = selectorPlan(input);
  try {
    let queried = selector === ':scope' ? [root] : [...root.querySelectorAll(selector)];
    for (const value of contains) queried = queried.filter(node => domText(node).includes(value));
    return applySelection(queried, selection);
  } catch { throw sourceFault('UNSUPPORTED_RULE'); }
}

function queryCSS(root, input) {
  const values = splitSelectorList(input).flatMap(selector => queryCSSSingle(root, selector));
  return [...new Set(values)];
}

function queryXPath(root, expression) {
  try {
    const documentElement = root.nodeType === 9
      ? root.documentElement
      : root.ownerDocument?.documentElement;
    const defaultNamespace = documentElement?.namespaceURI ?? root.namespaceURI ?? null;
    const options = {
      namespaceResolver(prefix) {
        if (prefix === '') return defaultNamespace;
        return root.lookupNamespaceURI?.(prefix) ??
          documentElement?.lookupNamespaceURI?.(prefix) ?? null;
      },
    };
    let nodes;
    try { nodes = evaluateXPathToNodes(expression, root, null, null, options); }
    catch { return evaluateXPathToStrings(expression, root, null, null, options); }
    if (nodes.length) {
      return nodes.map(node => node?.nodeType === 2
        ? String(node.value ?? node.nodeValue ?? '')
        : node);
    }
    return evaluateXPathToStrings(expression, root, null, null, options);
  } catch { throw sourceFault('UNSUPPORTED_RULE'); }
}

export function queryDom(root, rule) {
  sourceRequire(isDomNode(root));
  const { selector, attributes } = splitAttribute(rule);
  const expression = selector.trim().replace(/^@xpath:/i, '').trim();
  const nodes = /^@xpath:/i.test(selector.trim()) ||
      expression.startsWith('//') || expression.startsWith('/')
    ? queryXPath(root, expression)
    : queryCSS(root, selector);
  if (!attributes.length) return nodes;
  return nodes.map(node => {
    if (!isDomNode(node)) return String(node ?? '');
    for (const key of attributes) {
      let value;
      const semantic = key.toLowerCase();
      if (semantic === 'text') value = domText(node);
      else if (semantic === 'textnodes' || semantic === 'owntext') value = domText(node, true);
      else if (semantic === 'html') value = domHTML(node);
      else if (semantic === 'outerhtml' || semantic === 'all') value = domHTML(node, true);
      else if (semantic === 'href') value = node.getAttribute?.('href') ?? node.href ?? '';
      else if (semantic === 'src') value = node.getAttribute?.('src') ?? node.src ?? '';
      else value = node.getAttribute?.(key) ?? '';
      if (value !== '') return value;
    }
    return '';
  });
}

export function htmlToText(value, maximumNodes = defaultMaximumNodes, maximumParseBytes = defaultMaximumParseBytes) {
  const document = parseMarkup(String(value ?? ''), maximumNodes, maximumParseBytes);
  for (const element of document.querySelectorAll('script,style,noscript')) element.remove();
  return domText(document).replace(/\u00a0/g, ' ').replace(/[ \t]+\n/g, '\n')
    .replace(/\n{3,}/g, '\n\n').trim();
}

export class DocumentRegistry {
  #next = 0;
  #nodes = new Map();
  #nodeIds = new WeakMap();
  #documents = new Map();
  #documentSizes = new Map();
  #documentBytes = new Map();
  #documentNodes = 0;
  #domBytes = 0;
  #maximumDocuments;
  #maximumNodes;
  #maximumParseBytes;

  constructor({
    maximumDocuments = 32,
    maximumNodes = 10000,
    maximumParseBytes = defaultMaximumParseBytes,
  } = {}) {
    sourceRequire(Number.isSafeInteger(maximumDocuments) && maximumDocuments > 0);
    sourceRequire(Number.isSafeInteger(maximumNodes) && maximumNodes > 0);
    sourceRequire(Number.isSafeInteger(maximumParseBytes) && maximumParseBytes > 0);
    this.#maximumDocuments = maximumDocuments;
    this.#maximumNodes = maximumNodes;
    this.#maximumParseBytes = maximumParseBytes;
  }

  #text(value) {
    const text = String(value ?? '');
    sourceRequire(textEncoder.encode(text).length <= this.#maximumParseBytes, 'RESOURCE_LIMIT');
    return text;
  }

  #measure(roots, maximumNodes, maximumParseBytes) {
    const nodes = countNodeForest(roots, maximumNodes);
    const top = new Set();
    for (const value of roots) {
      let root = value;
      while (root?.parentNode) root = root.parentNode;
      if (root) top.add(root);
    }
    let bytes = 0;
    const add = value => {
      bytes += textEncoder.encode(String(value ?? '')).length;
      sourceRequire(bytes <= maximumParseBytes, 'RESOURCE_LIMIT');
    };
    for (const root of top) add(domHTML(root, true));

    const stack = [...top];
    const seen = new Set();
    while (stack.length) {
      const node = stack.pop();
      if (!node || seen.has(node)) continue;
      seen.add(node);
      if (typeof node.value === 'string' && typeof node.getAttribute === 'function' &&
          node.getAttribute('value') !== node.value) {
        add(node.value);
      }
      for (const child of node.childNodes ?? []) stack.push(child);
    }
    return { nodes, bytes };
  }

  #store(node, documentId) {
    const existing = this.#nodeIds.get(node);
    if (existing !== undefined) return existing;
    sourceRequire(this.#nodes.size < this.#maximumNodes, 'RESOURCE_LIMIT');
    const id = ++this.#next;
    this.#nodes.set(id, { node, documentId });
    this.#nodeIds.set(node, id);
    return id;
  }

  #entry(id) {
    sourceRequire(Number.isSafeInteger(id) && id > 0);
    const entry = this.#nodes.get(id);
    if (!entry) throw sourceFault('INVALID_ARGUMENT');
    return entry;
  }

  #refresh(documentId) {
    const document = this.#documents.get(documentId);
    sourceRequire(document, 'INVALID_ARGUMENT');
    const previous = this.#documentSizes.get(documentId) ?? 0;
    const previousBytes = this.#documentBytes.get(documentId) ?? 0;
    const available = this.#maximumNodes - this.#documentNodes + previous;
    const availableBytes = this.#maximumParseBytes - this.#domBytes + previousBytes;
    const roots = [document];
    for (const entry of this.#nodes.values()) {
      if (entry.documentId === documentId) roots.push(entry.node);
    }
    const measured = this.#measure(roots, available, availableBytes);
    const next = Math.max(previous, measured.nodes);
    const nextBytes = Math.max(previousBytes, measured.bytes);
    this.#documentSizes.set(documentId, next);
    this.#documentBytes.set(documentId, nextBytes);
    this.#documentNodes += next - previous;
    this.#domBytes += nextBytes - previousBytes;
  }

  parse(markup) {
    sourceRequire(this.#documents.size < this.#maximumDocuments, 'RESOURCE_LIMIT');
    sourceRequire(this.#nodes.size < this.#maximumNodes, 'RESOURCE_LIMIT');
    const available = this.#maximumNodes - this.#documentNodes;
    const availableBytes = this.#maximumParseBytes - this.#domBytes;
    sourceRequire(available > 0, 'RESOURCE_LIMIT');
    sourceRequire(availableBytes > 0, 'RESOURCE_LIMIT');
    const document = parseMarkup(markup, available, availableBytes);
    const measured = this.#measure([document], available, availableBytes);
    const documentId = ++this.#next;
    this.#documents.set(documentId, document);
    this.#documentSizes.set(documentId, measured.nodes);
    this.#documentBytes.set(documentId, measured.bytes);
    this.#documentNodes += measured.nodes;
    this.#domBytes += measured.bytes;
    const rootId = this.#store(document, documentId);
    return { documentId, rootId };
  }

  release(documentId) {
    if (!this.#documents.delete(documentId)) return false;
    this.#documentNodes -= this.#documentSizes.get(documentId) ?? 0;
    this.#domBytes -= this.#documentBytes.get(documentId) ?? 0;
    this.#documentSizes.delete(documentId);
    this.#documentBytes.delete(documentId);
    for (const [id, entry] of this.#nodes) {
      if (entry.documentId !== documentId) continue;
      this.#nodeIds.delete(entry.node);
      this.#nodes.delete(id);
    }
    return true;
  }

  operation(name, input) {
    sourceObject(input);
    switch (name) {
      case 'parse': return this.parse(String(input.html ?? ''));
      case 'release': return this.release(input.documentId);
      case 'query': {
        const owner = this.#entry(input.nodeId);
        return queryDom(owner.node, String(input.selector ?? ''))
          .map(value => isDomNode(value)
            ? { nodeId: this.#store(value, owner.documentId), kind: 'node' }
            : { value: String(value ?? ''), kind: 'value' });
      }
      case 'text': return domText(this.#entry(input.nodeId).node, input.direct === true);
      case 'setText': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        sourceRequire('textContent' in node);
        const html = 'innerHTML' in node ? node.innerHTML : null;
        const text = node.textContent;
        node.textContent = this.#text(input.text);
        try { this.#refresh(entry.documentId); }
        catch (error) {
          if (html !== null) node.innerHTML = html;
          else node.textContent = text;
          this.#refresh(entry.documentId);
          throw error;
        }
        return true;
      }
      case 'html': return domHTML(this.#entry(input.nodeId).node, input.outer === true);
      case 'attr': {
        const node = this.#entry(input.nodeId).node;
        if (typeof node.getAttribute !== 'function') return null;
        const name = String(input.name ?? '');
        return node.hasAttribute?.(name) === false ? null : node.getAttribute(name);
      }
      case 'hasAttr':
        return Boolean(this.#entry(input.nodeId).node.hasAttribute?.(String(input.name ?? '')));
      case 'setAttr': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        sourceRequire(typeof node.setAttribute === 'function');
        const name = this.#text(input.name);
        const value = this.#text(input.value);
        const existed = node.hasAttribute(name);
        const previous = node.getAttribute(name);
        node.setAttribute(name, value);
        try { this.#refresh(entry.documentId); }
        catch (error) {
          if (existed) node.setAttribute(name, previous);
          else node.removeAttribute(name);
          this.#refresh(entry.documentId);
          throw error;
        }
        return true;
      }
      case 'removeAttr': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        const name = this.#text(input.name);
        const existed = node.hasAttribute?.(name) === true;
        const previous = node.getAttribute?.(name);
        node.removeAttribute?.(name);
        try { this.#refresh(entry.documentId); }
        catch (error) {
          if (existed) node.setAttribute?.(name, previous);
          this.#refresh(entry.documentId);
          throw error;
        }
        return true;
      }
      case 'tagName': return String(this.#entry(input.nodeId).node.tagName ?? '');
      case 'prop': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        const property = String(input.name ?? '');
        sourceRequire([
          'tagName', 'nodeName', 'nodeType', 'nodeValue',
          'textContent', 'innerHTML', 'outerHTML',
          'value', 'checked', 'selected', 'disabled', 'id', 'className',
          'href', 'src', 'name', 'type',
        ].includes(property));
        if (Object.hasOwn(input, 'value')) {
          sourceRequire(!['tagName', 'nodeName', 'nodeType', 'outerHTML'].includes(property));
          if (property === 'textContent') {
            return this.operation('setText', { nodeId: input.nodeId, text: input.value });
          }
          if (property === 'innerHTML') {
            return this.operation('setHtml', { nodeId: input.nodeId, html: input.value });
          }
          if (typeof input.value === 'string') this.#text(input.value);
          const attribute = property === 'className' ? 'class' : [
            'value', 'checked', 'selected', 'disabled', 'id', 'href', 'src', 'name', 'type',
          ].includes(property) ? property : null;
          const previous = node[property];
          const existed = attribute && node.hasAttribute?.(attribute) === true;
          const previousAttribute = attribute ? node.getAttribute?.(attribute) : null;
          node[property] = input.value;
          try { this.#refresh(entry.documentId); }
          catch (error) {
            try { node[property] = previous; } catch { /* Restore the serialized attribute below. */ }
            if (attribute) {
              if (existed) node.setAttribute?.(attribute, previousAttribute);
              else node.removeAttribute?.(attribute);
            }
            this.#refresh(entry.documentId);
            throw error;
          }
          return true;
        }
        const value = node[property];
        return value === undefined || value === null ? null
          : typeof value === 'boolean' || typeof value === 'number' ? value : String(value);
      }
      case 'matches': {
        const node = this.#entry(input.nodeId).node;
        try { return Boolean(node.matches?.(String(input.selector ?? ''))); }
        catch { throw sourceFault('UNSUPPORTED_RULE'); }
      }
      case 'closest': {
        const entry = this.#entry(input.nodeId);
        let node;
        try { node = entry.node.closest?.(String(input.selector ?? '')); }
        catch { throw sourceFault('UNSUPPORTED_RULE'); }
        return node ? { nodeId: this.#store(node, entry.documentId), kind: 'node' } : null;
      }
      case 'setHtml': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        sourceRequire('innerHTML' in node);
        const previous = node.innerHTML;
        node.innerHTML = this.#text(input.html);
        try { this.#refresh(entry.documentId); }
        catch (error) {
          node.innerHTML = previous;
          this.#refresh(entry.documentId);
          throw error;
        }
        return true;
      }
      case 'clone': {
        const owner = this.#entry(input.nodeId);
        sourceRequire(typeof owner.node.cloneNode === 'function');
        const node = owner.node.cloneNode(input.deep === true);
        const nodeId = this.#store(node, owner.documentId);
        try { this.#refresh(owner.documentId); }
        catch (error) {
          this.#nodeIds.delete(node);
          this.#nodes.delete(nodeId);
          throw error;
        }
        return { nodeId, kind: 'node' };
      }
      case 'create': {
        const owner = this.#entry(input.nodeId);
        const document = this.#documents.get(owner.documentId);
        const node = document.createElement(this.#text(input.tag ?? 'div'));
        const nodeId = this.#store(node, owner.documentId);
        try { this.#refresh(owner.documentId); }
        catch (error) {
          this.#nodeIds.delete(node);
          this.#nodes.delete(nodeId);
          throw error;
        }
        return { nodeId, kind: 'node' };
      }
      case 'createText': {
        const owner = this.#entry(input.nodeId);
        const document = this.#documents.get(owner.documentId);
        const node = document.createTextNode(this.#text(input.text));
        const nodeId = this.#store(node, owner.documentId);
        try { this.#refresh(owner.documentId); }
        catch (error) {
          this.#nodeIds.delete(node);
          this.#nodes.delete(nodeId);
          throw error;
        }
        return { nodeId, kind: 'node' };
      }
      case 'append': {
        const ownerEntry = this.#entry(input.nodeId);
        const childEntry = this.#entry(input.childId);
        sourceRequire(ownerEntry.documentId === childEntry.documentId);
        const owner = ownerEntry.node;
        const child = childEntry.node;
        const previousParent = child.parentNode;
        const previousNext = child.nextSibling;
        owner.appendChild(child);
        try { this.#refresh(ownerEntry.documentId); }
        catch (error) {
          if (previousParent) previousParent.insertBefore(child,
            previousNext?.parentNode === previousParent ? previousNext : null);
          else child.remove?.();
          this.#refresh(ownerEntry.documentId);
          throw error;
        }
        return true;
      }
      case 'replace': {
        const ownerEntry = this.#entry(input.nodeId);
        const childEntry = this.#entry(input.childId);
        sourceRequire(ownerEntry.documentId === childEntry.documentId &&
          ownerEntry.node !== childEntry.node);
        const owner = ownerEntry.node;
        const child = childEntry.node;
        const parent = owner.parentNode;
        if (!parent) return true;
        const ownerNext = owner.nextSibling;
        const childParent = child.parentNode;
        const childNext = child.nextSibling;
        parent.replaceChild(child, owner);
        try { this.#refresh(ownerEntry.documentId); }
        catch (error) {
          parent.insertBefore(owner, child);
          if (childParent) childParent.insertBefore(child,
            childNext?.parentNode === childParent ? childNext : null);
          else child.remove?.();
          if (ownerNext?.parentNode === parent && owner.nextSibling !== ownerNext) {
            parent.insertBefore(owner, ownerNext);
          }
          this.#refresh(ownerEntry.documentId);
          throw error;
        }
        return true;
      }
      case 'remove': {
        const entry = this.#entry(input.nodeId);
        entry.node.remove?.();
        this.#refresh(entry.documentId);
        return true;
      }
      case 'contains': {
        const owner = this.#entry(input.nodeId).node;
        const child = this.#entry(input.childId).node;
        return Boolean(owner.contains?.(child));
      }
      case 'parent': {
        const entry = this.#entry(input.nodeId);
        const parent = entry.node.parentNode;
        return parent ? { nodeId: this.#store(parent, entry.documentId), kind: 'node' } : null;
      }
      case 'next':
      case 'previous': {
        const entry = this.#entry(input.nodeId);
        const node = name === 'next' ? entry.node.nextElementSibling : entry.node.previousElementSibling;
        return node ? { nodeId: this.#store(node, entry.documentId), kind: 'node' } : null;
      }
      case 'children':
      case 'childNodes': {
        const entry = this.#entry(input.nodeId);
        const values = name === 'children' ? entry.node.children : entry.node.childNodes;
        return [...(values ?? [])].map(node => ({
          nodeId: this.#store(node, entry.documentId), kind: 'node',
        }));
      }
      case 'classList': {
        const entry = this.#entry(input.nodeId);
        const node = entry.node;
        const operation = String(input.operation ?? 'contains');
        const token = this.#text(input.token);
        sourceRequire(/^[^\s]+$/.test(token));
        if (operation === 'contains') return Boolean(node.classList?.contains(token));
        const existed = node.hasAttribute?.('class') === true;
        const previous = node.getAttribute?.('class');
        let result;
        if (operation === 'add') {
          node.classList?.add(token);
          result = true;
        } else if (operation === 'remove') {
          node.classList?.remove(token);
          result = true;
        } else if (operation === 'toggle') {
          result = Boolean(Object.hasOwn(input, 'force')
            ? node.classList?.toggle(token, Boolean(input.force))
            : node.classList?.toggle(token));
        } else {
          throw sourceFault('UNSUPPORTED_RULE');
        }
        try { this.#refresh(entry.documentId); }
        catch (error) {
          if (existed) node.setAttribute?.('class', previous);
          else node.removeAttribute?.('class');
          this.#refresh(entry.documentId);
          throw error;
        }
        return result;
      }
      default: throw sourceFault('UNSUPPORTED_RULE');
    }
  }

  close() {
    this.#nodes.clear();
    this.#nodeIds = new WeakMap();
    this.#documents.clear();
    this.#documentSizes.clear();
    this.#documentBytes.clear();
    this.#documentNodes = 0;
    this.#domBytes = 0;
  }
}
