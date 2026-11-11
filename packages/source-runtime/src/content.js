import { parseMarkup } from './dom.js';

const paragraphTags = new Set(['P', 'DIV', 'SECTION', 'ARTICLE', 'LI', 'UL', 'OL',
  'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE', 'TR']);
const ignoredTags = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE']);

/** Ordered native reading blocks, with image URLs bound to the page they came from. */
export function contentBlocks(value, { baseUrl = '', maximumNodes, maximumParseBytes } = {}) {
  const input = String(value ?? '');
  const blocks = [];
  let buffer = '';
  function flush() {
    for (const line of buffer.replace(/\r\n?/g, '\n').split('\n')) {
      const text = line.replace(/\u00a0/g, ' ').trim();
      if (text) blocks.push({ type: 'text', text });
    }
    buffer = '';
  }
  if (!/<\/?[A-Za-z][^>]*>/.test(input)) {
    buffer = input; flush(); return blocks;
  }
  const document = parseMarkup(`<html><body>${input}</body></html>`, maximumNodes, maximumParseBytes);
  const stack = [...document.body.childNodes].reverse().map(node => ({ node, end: false }));
  while (stack.length) {
    const { node, end } = stack.pop();
    if (node.nodeType === 3) { buffer += node.nodeValue ?? ''; continue; }
    if (node.nodeType !== 1 || ignoredTags.has(node.tagName)) continue;
    if (node.tagName === 'IMG') {
      flush();
      const raw = node.getAttribute('data-src') || node.getAttribute('data-original') ||
        node.getAttribute('src') || '';
      if (raw) {
        let src = raw;
        try { src = new URL(raw, baseUrl || undefined).href; } catch { /* Preserve source values. */ }
        blocks.push({ type: 'image', src, alt: node.getAttribute('alt') ?? '' });
      }
      continue;
    }
    if (node.tagName === 'BR' || paragraphTags.has(node.tagName)) buffer += '\n';
    if (end) continue;
    if (paragraphTags.has(node.tagName)) stack.push({ node, end: true });
    for (const child of [...node.childNodes].reverse()) stack.push({ node: child, end: false });
  }
  flush();
  return blocks;
}

export function resolveContentImages(value, { baseUrl, maximumNodes, maximumParseBytes } = {}) {
  if (!/<img\b/i.test(value)) return value;
  const document = parseMarkup(`<html><body>${value}</body></html>`, maximumNodes, maximumParseBytes);
  for (const image of document.querySelectorAll('img')) {
    for (const attribute of ['src', 'data-src', 'data-original']) {
      const input = image.getAttribute(attribute);
      if (!input) continue;
      try { image.setAttribute(attribute, new URL(input, baseUrl || undefined).href); }
      catch { /* Other source-specific image values are returned unchanged. */ }
    }
  }
  return document.body.innerHTML;
}
