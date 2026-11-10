import { isUtf8 } from 'node:buffer';

// Pure control-document mechanics; callers own registration, credentials and lifetime.
// Media, keys and initialization bytes stay binary streams.
function failure(code = 'MEDIA_MANIFEST_INVALID') {
  return Object.assign(new Error(code), { code });
}

export function decodeManifest(bytes) {
  // NodeMobile omits ICU, which fatal TextDecoder requires. Validate the original
  // bytes first, then consume only the leading BOM during ordinary UTF-8 decoding.
  if (!isUtf8(bytes)) throw failure();
  return new TextDecoder('utf-8').decode(bytes);
}

function address(value, base) {
  try {
    const url = new URL(value, base);
    if (!['http:', 'https:'].includes(url.protocol)) throw failure();
    url.hash = '';
    return url.href;
  } catch { throw failure(); }
}

export function manifestFormat(url, contentType = '', hint = '') {
  if (hint === 'binary') return null;
  const type = contentType.split(';', 1)[0].trim().toLowerCase();
  if (['hls', 'm3u8', 'application/vnd.apple.mpegurl', 'application/x-mpegurl'].includes(hint.toLowerCase()) ||
      ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl'].includes(type)) return 'hls';
  if (['dash', 'mpd', 'application/dash+xml'].includes(hint.toLowerCase()) || type === 'application/dash+xml') return 'dash';
  const path = new URL(url).pathname.toLowerCase();
  return path.endsWith('.m3u8') ? 'hls' : path.endsWith('.mpd') ? 'dash' : null;
}

export function hlsAttributes(line, rewrite, inspect) {
  const colon = line.indexOf(':');
  if (colon < 0) return line;
  // Attribute values may contain commas, equals signs and signed query strings.
  const head = line.slice(0, colon + 1), attributes = line.slice(colon + 1);
  let output = '', position = 0;
  while (position < attributes.length) {
    const match = /^(\s*([A-Za-z0-9-]+)\s*=\s*)/.exec(attributes.slice(position));
    if (!match) throw failure();
    output += match[1];
    const name = match[2];
    position += match[0].length;
    const quoted = attributes[position] === '"';
    let end = quoted ? attributes.indexOf('"', ++position) : attributes.indexOf(',', position);
    if (end < 0) end = attributes.length;
    if (quoted && end === attributes.length) throw failure();
    let value = attributes.slice(position, end);
    inspect?.(name, value);
    if (['URI', 'SERVER-URI'].includes(name)) value = rewrite(value);
    output += quoted ? `"${value}"` : value;
    position = end + (quoted ? 1 : 0);
    if (position < attributes.length) {
      if (attributes[position] !== ',') throw failure();
      output += ',';
      position++;
    }
  }
  return head + output;
}

export const hlsAttributeLine = line => /^#EXT[A-Za-z0-9-]*:\s*[A-Za-z0-9-]+\s*=/.test(line);

export function createHLSVariables(base, inheritedVariables = {}) {
  const values = new Map();
  function substitute(value) {
    return value.replace(/\{\$([^}]+)\}/g, (_, name) => {
      if (!values.has(name)) throw failure('MEDIA_MANIFEST_UNSUPPORTED');
      return values.get(name);
    });
  }
  function define(line) {
    const attributes = Object.fromEntries([...line.matchAll(/([A-Z-]+)="([^"]*)"/g)].map(match => [match[1], match[2]]));
    if (attributes.NAME && attributes.VALUE !== undefined) values.set(attributes.NAME, substitute(attributes.VALUE));
    else if (attributes.QUERYPARAM) {
      const value = new URL(base).searchParams.get(attributes.QUERYPARAM);
      if (value === null) throw failure();
      values.set(attributes.QUERYPARAM, value);
    } else if (attributes.IMPORT && Object.hasOwn(inheritedVariables, attributes.IMPORT)) {
      values.set(attributes.IMPORT, inheritedVariables[attributes.IMPORT]);
    } else throw failure('MEDIA_MANIFEST_UNSUPPORTED');
  }
  return { values, substitute, define };
}

export function rewriteHLS(text, base, register, inheritedVariables = {}) {
  if (!text.replace(/^\uFEFF/, '').startsWith('#EXTM3U')) throw failure();
  const { values: variables, substitute, define } = createHLSVariables(base, inheritedVariables);
  let variant = false;
  const lines = text.split(/\r?\n/).map(line => {
    const trimmed = line.trim();
    if (!trimmed) return line;
    if (!trimmed.startsWith('#')) {
      const format = variant ? 'hls' : undefined;
      variant = false;
      return register({ url: address(substitute(trimmed), base), format,
        ...(format === 'hls' && variables.size ? { hlsVariables: Object.fromEntries(variables) } : {}) });
    }
    const tag = trimmed.split(':', 1)[0];
    if (tag === '#EXT-X-DEFINE') {
      define(trimmed);
      return line;
    }
    if (tag === '#EXT-X-STREAM-INF') variant = true;
    const binary = ['#EXT-X-KEY', '#EXT-X-SESSION-KEY', '#EXT-X-MAP', '#EXT-X-PART', '#EXT-X-PRELOAD-HINT'].includes(tag);
    const child = ['#EXT-X-MEDIA', '#EXT-X-I-FRAME-STREAM-INF', '#EXT-X-RENDITION-REPORT', '#EXT-X-IMAGE-STREAM-INF'].includes(tag);
    // EXTINF's duration and free title are not an attribute list, even when the
    // title contains ",URI=". Keep structural detection open to unknown tags.
    if (!hlsAttributeLine(trimmed) ||
        !/[:,](?:\s*)(?:URI|SERVER-URI)\s*=/.test(trimmed)) return line;
    return hlsAttributes(trimmed, value => {
      if (value.startsWith('data:')) return value; // Inline keys do not issue network requests.
      return register({ url: address(substitute(value), base), format: binary ? 'binary' : child ? 'hls' : undefined,
        ...(child && variables.size ? { hlsVariables: Object.fromEntries(variables) } : {}) });
    });
  });
  return lines.join('\n');
}

function unescapeXML(value) {
  return value.replace(/&([^;]+);/g, (_, entity) => {
    const named = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };
    if (Object.hasOwn(named, entity)) return named[entity];
    if (!/^#(?:[0-9]+|x[0-9a-f]+)$/i.test(entity)) throw failure();
    const point = entity[1]?.toLowerCase() === 'x' ? parseInt(entity.slice(2), 16) : Number(entity.slice(1));
    if (!Number.isInteger(point) || point < 32 && ![9, 10, 13].includes(point) ||
        point > 0x10ffff || point >= 0xd800 && point <= 0xdfff || [0xfffe, 0xffff].includes(point)) throw failure();
    return String.fromCodePoint(point);
  });
}

function escapeXML(value) {
  return value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[character]);
}

const nameOf = node => node.name?.split(':').at(-1);
export const localName = node => !node.namespace || node.namespace === 'urn:mpeg:dash:schema:mpd:2011' ? nameOf(node) : undefined;
export const elements = node => node.children.filter(child => child.name);
export const named = (node, name) => elements(node).filter(child => localName(child) === name);
const clone = node => structuredClone(node);

// A non-expanding XML tokenizer: no DTD, external entities, XInclude or implicit network I/O.
// Namespace prefixes and arbitrary metadata nodes survive serialization.
export function parseXML(text) {
  if (/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/.test(text)) throw failure();
  const document = { children: [], namespaces: { xml: 'http://www.w3.org/XML/1998/namespace' } }, stack = [document];
  let position = 0, nodes = 0;
  function append(child) {
    if (++nodes > 100000) throw failure('MEDIA_MANIFEST_LIMIT');
    stack.at(-1).children.push(child);
  }
  while (position < text.length) {
    if (text[position] !== '<') {
      const end = text.indexOf('<', position), next = end < 0 ? text.length : end;
      const raw = text.slice(position, next);
      if (/&(?!(?:amp|lt|gt|quot|apos|#[0-9]+|#x[0-9a-f]+);)/i.test(raw)) throw failure();
      append({ text: unescapeXML(raw) });
      position = next;
      continue;
    }
    if (text.startsWith('<!--', position) || text.startsWith('<?', position) || text.startsWith('<![CDATA[', position)) {
      const cdata = text.startsWith('<![CDATA[', position);
      const endToken = cdata ? ']]>' : text.startsWith('<!--', position) ? '-->' : '?>';
      const end = text.indexOf(endToken, position + 2);
      if (end < 0) throw failure();
      const raw = text.slice(position, end + endToken.length);
      if (/^<\?xml\b/i.test(raw) && /encoding\s*=\s*["'](?!utf-8["'])/i.test(raw)) throw failure('MEDIA_MANIFEST_UNSUPPORTED');
      append(cdata ? { text: raw.slice(9, -3) } : { raw });
      position = end + endToken.length;
      continue;
    }
    if (text.startsWith('<!', position)) throw failure('MEDIA_MANIFEST_UNSUPPORTED');
    if (text.startsWith('</', position)) {
      const match = /^<\/\s*([A-Za-z_][\w.:-]*)\s*>/.exec(text.slice(position));
      if (!match || stack.length < 2 || stack.at(-1).name !== match[1]) throw failure();
      stack.pop();
      position += match[0].length;
      continue;
    }
    const match = /^<([A-Za-z_][\w.:-]*)/.exec(text.slice(position));
    if (!match) throw failure();
    const node = { name: match[1], attrs: Object.create(null), children: [] };
    position += match[0].length;
    let closed = false;
    while (position < text.length) {
      const rest = text.slice(position);
      const end = /^\s*(\/?)>/.exec(rest);
      if (end) {
        position += end[0].length;
        closed = Boolean(end[1]);
        break;
      }
      const attr = /^\s+([A-Za-z_][\w.:-]*)\s*=\s*(["'])([^]*?)\2/.exec(rest);
      if (!attr || Object.hasOwn(node.attrs, attr[1]) || attr[3].includes('<') ||
          /&(?!(?:amp|lt|gt|quot|apos|#[0-9]+|#x[0-9a-f]+);)/i.test(attr[3])) throw failure();
      node.attrs[attr[1]] = unescapeXML(attr[3]);
      position += attr[0].length;
    }
    if (position > text.length || text[position - 1] !== '>') throw failure();
    node.namespaces = stack.at(-1).namespaces;
    for (const [name, value] of Object.entries(node.attrs)) {
      if (name === 'xmlns' || name.startsWith('xmlns:')) {
        const prefix = name === 'xmlns' ? '' : name.slice(6);
        if (prefix === 'xmlns' || prefix === 'xml' && value !== document.namespaces.xml) throw failure();
        node.namespaces = { ...node.namespaces, [prefix]: value };
      }
    }
    for (const name of [node.name, ...Object.keys(node.attrs).filter(name => name !== 'xmlns' && !name.startsWith('xmlns:'))]) {
      const parts = name.split(':');
      if (parts.length > 2 || parts.length === 2 && (!parts[1] || !node.namespaces[parts[0]])) throw failure();
    }
    node.namespace = node.namespaces[node.name.includes(':') ? node.name.split(':')[0] : ''] ?? '';
    append(node);
    if (!closed) {
      if (stack.length >= 64) throw failure('MEDIA_MANIFEST_LIMIT');
      stack.push(node);
    }
  }
  if (stack.length !== 1 || elements(document).length !== 1 || localName(elements(document)[0]) !== 'MPD' ||
      document.children.some(child => child.text?.trim())) throw failure();
  return document;
}

function serializeXML(node) {
  return [...serializeXMLChunks(node)].join('');
}

export function* serializeXMLChunks(node) {
  if (node.raw !== undefined) { yield node.raw; return; }
  if (node.text !== undefined) { yield escapeXML(node.text); return; }
  if (!node.name) {
    for (const child of node.children) yield* serializeXMLChunks(child);
    return;
  }
  const attrs = Object.entries(node.attrs).map(([name, value]) => ` ${name}="${escapeXML(value)}"`).join('');
  if (!node.children.length) { yield `<${node.name}${attrs}/>`; return; }
  yield `<${node.name}${attrs}>`;
  for (const child of node.children) yield* serializeXMLChunks(child);
  yield `</${node.name}>`;
}

export function content(node) {
  if (node.children.some(child => child.name)) throw failure();
  return node.children.map(child => child.text ?? '').join('').trim();
}

function qualified(node, name) {
  const prefix = node.name.includes(':') ? node.name.slice(0, node.name.lastIndexOf(':') + 1) : '';
  return prefix + name;
}

function inheritSegment(parent, own) {
  if (!own) return parent;
  if (!parent || localName(parent) !== localName(own)) return clone(own);
  const result = clone(parent);
  result.name = own.name;
  Object.assign(result.attrs, own.attrs);
  const overridden = new Set(elements(own).map(localName));
  result.children = result.children.filter(existing => !overridden.has(localName(existing)));
  result.children.push(...elements(own).map(clone));
  return result;
}

// Representation and bandwidth are fixed by the document; the player expands
// the remaining numeric DASH substitutions when requesting a segment.
export function dashTemplate(value, representation = {}) {
  let template = '', path = '', position = 0, slot = 0;
  while (position < value.length) {
    if (value[position] !== '$') {
      const end = value.indexOf('$', position), next = end < 0 ? value.length : end;
      template += value.slice(position, next);
      position = next;
      continue;
    }
    if (value[position + 1] === '$') {
      template += '$$';
      position += 2;
      continue;
    }
    const end = value.indexOf('$', position + 1);
    if (end < 0) throw failure();
    const match = /^(RepresentationID|Bandwidth|Number|Time|SubNumber)(%(0?)([1-9][0-9]?)?d)?$/.exec(value.slice(position + 1, end));
    if (!match || match[1] === 'RepresentationID' && match[2]) throw failure('MEDIA_MANIFEST_UNSUPPORTED');
    if (['RepresentationID', 'Bandwidth'].includes(match[1])) {
      let fixed = representation[match[1] === 'RepresentationID' ? 'id' : 'bandwidth'];
      if (fixed === undefined || match[1] === 'Bandwidth' && !/^\d+$/.test(fixed)) throw failure();
      if (match[4]) fixed = fixed.padStart(Number(match[4]), match[3] ? '0' : ' ');
      // Keep literal identifiers intact through the player's substitution pass.
      template += fixed.replace(/\$/g, () => '$$');
    } else {
      const token = value.slice(position, end + 1);
      template += token;
      path += `${slot++ ? '-' : ''}${token}`;
    }
    position = end + 1;
  }
  return { template, suffix: path ? `/${path}` : '' };
}

export function compileDASHTemplate(url, path) {
  const slots = [];
  const escape = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  let expression = '^', position = 0;
  const pattern = /\$(Number|Time|SubNumber)(?:%(0?)([1-9][0-9]?)?d)?\$/g;
  for (const match of path.matchAll(pattern)) {
    expression += escape(path.slice(position, match.index)) + (match[3] && !match[2] ? '((?:%20)*[0-9]+)' : '([0-9]+)');
    slots.push({ token: match[0], name: match[1], zero: match[2], width: Number(match[3] ?? 0) });
    position = match.index + match[0].length;
  }
  expression += escape(path.slice(position)) + '$';
  const regexp = new RegExp(expression);
  return input => {
    const match = regexp.exec(input);
    if (!match) return null;
    const values = new Map();
    for (let index = 0; index < slots.length; index++) {
      const number = match[index + 1].replace(/^(?:%20)*/, '').replace(/^0+(?=[0-9])/, '');
      if (values.has(slots[index].name) && values.get(slots[index].name) !== number) return null;
      values.set(slots[index].name, number);
    }
    let index = 0;
    // WHATWG URL lowercases hostname labels, including any DASH tokens there.
    const result = url.replace(/\$\$|\$(?:Number|Time|SubNumber)(?:%(?:0?)(?:[1-9][0-9]?)?d)?\$/gi, token => {
      if (token === '$$') return '$';
      const slot = slots[index++];
      if (slot?.token.toLowerCase() !== token.toLowerCase()) throw failure();
      return values.get(slot.name).padStart(slot.width, slot.zero ? '0' : ' ').replace(/ /g, '%20');
    });
    try { return index === slots.length ? address(result) : null; } catch { return null; }
  };
}

export function rewriteDASH(text, base, register, serialize = serializeXML) {
  const document = parseXML(text), mpd = elements(document)[0];
  const segmentNames = new Set(['SegmentTemplate', 'SegmentList', 'SegmentBase']);
  let representations = 0;

  function check(node) {
    // External XML fragments and MPD patch documents need their own merge semantics.
    // This parser does not implement those document merge protocols.
    if (Object.entries(node.attrs).some(([name, value]) => name.endsWith(':href') && value) ||
        localName(node) === 'PatchLocation' ||
        node.namespace === 'http://www.w3.org/2001/XInclude') throw failure('MEDIA_MANIFEST_UNSUPPORTED');
    if (['Laurl', 'laurl'].includes(nameOf(node))) throw failure('MEDIA_DRM_UNSUPPORTED');
    for (const child of elements(node)) check(child);
  }
  check(mpd);

  function walk(node, bases, inherited, documentBase) {
    if (node.attrs['xml:base']) {
      documentBase = address(node.attrs['xml:base'], documentBase);
      bases = bases.map(item => ({ ...item, url: address(node.attrs['xml:base'], item.url) }));
      delete node.attrs['xml:base'];
    }
    const ownBases = named(node, 'BaseURL');
    if (ownBases.length) {
      const next = [];
      for (const parent of bases) for (const child of ownBases) {
        const childBase = child.attrs['xml:base'] ? address(child.attrs['xml:base'], parent.url) : parent.url;
        next.push({ url: address(content(child), childBase), attrs: { ...parent.attrs, ...child.attrs } });
        if (next.length > 1024) throw failure('MEDIA_MANIFEST_LIMIT');
      }
      bases = next;
    }
    const segments = elements(node).filter(child => segmentNames.has(localName(child)));
    if (segments.length > 1) throw failure();
    const effective = inheritSegment(inherited, segments[0]);
    if (localName(node) === 'Representation') {
      representations++;
      const segment = effective ? clone(effective) : null;
      const routes = [];
      function route(value, templated = false, offsets = []) {
        const parsed = templated ? dashTemplate(value, node.attrs) : { template: value, suffix: '' };
        const path = `r${routes.length}${parsed.suffix}`;
        routes.push({ path, reference: parsed.template, template: templated, offsets });
        return path;
      }
      if (segment) {
        function rewriteSegment(current, offsets = []) {
          if (current.attrs['xml:base']) {
            offsets = [...offsets, current.attrs['xml:base']];
            delete current.attrs['xml:base'];
          }
          for (const [key, value] of Object.entries(current.attrs)) {
            if (localName(current) === 'SegmentTemplate' && ['media', 'initialization', 'index', 'bitstreamSwitching'].includes(key)) {
              current.attrs[key] = route(value, true, offsets);
            } else if (['sourceURL', 'media', 'index'].includes(key) &&
                ['Initialization', 'RepresentationIndex', 'BitstreamSwitching', 'SegmentURL'].includes(localName(current))) {
              current.attrs[key] = route(value, false, offsets);
            }
          }
          for (const child of elements(current)) rewriteSegment(child, offsets);
        }
        rewriteSegment(segment);
      }
      node.children = node.children.filter(child => localName(child) !== 'BaseURL' && !segmentNames.has(localName(child)));
      const rewrittenBases = bases.map(item => {
        // The empty route is the original exact BaseURL (single-file/byte-range addressing).
        const resources = [{ path: '', url: item.url }];
        for (const entry of routes) {
          const url = address(entry.reference, entry.offsets.reduce((url, offset) => address(offset, url), item.url));
          resources.push({ path: entry.path, url, template: entry.template });
        }
        const attrs = { ...item.attrs };
        delete attrs['xml:base'];
        return { name: qualified(node, 'BaseURL'), attrs, children: [{ text: register({ resources }) }] };
      });
      node.children.unshift(...rewrittenBases);
      if (segment) node.children.push(segment);
    } else if (['MPD', 'Period', 'AdaptationSet'].includes(localName(node))) {
      node.children = node.children.filter(child => localName(child) !== 'BaseURL' && !segmentNames.has(localName(child)));
    }
    for (const child of elements(node)) {
      if (['BaseURL', 'SegmentTemplate', 'SegmentList', 'SegmentBase'].includes(localName(child))) continue;
      if (['Location', 'ContentSteering'].includes(localName(child))) {
        const referenceBase = child.attrs['xml:base'] ? address(child.attrs['xml:base'], documentBase) : documentBase;
        delete child.attrs['xml:base'];
        child.children = [{ text: register({ url: address(content(child), referenceBase),
          format: localName(child) === 'Location' ? 'dash' : 'binary' }) }];
      } else if (localName(child) === 'UTCTiming') {
        const scheme = child.attrs.schemeIdUri ?? '';
        if (/^urn:mpeg:dash:utc:(?:http-(?:xsdate|iso|head):(?:2012|2014)|http-ntp:2014)$/.test(scheme)) {
          const referenceBase = child.attrs['xml:base'] ? address(child.attrs['xml:base'], documentBase) : documentBase;
          delete child.attrs['xml:base'];
          child.attrs.value = register({ url: address(child.attrs.value, referenceBase), format: 'binary' });
        } else if (!/^urn:mpeg:dash:utc:direct:(?:2012|2014)$/.test(scheme)) {
          throw failure('MEDIA_MANIFEST_UNSUPPORTED');
        }
      } else walk(child, bases, effective, documentBase);
    }
  }
  walk(mpd, [{ url: base, attrs: {} }], null, base);
  if (!representations) throw failure();
  return serialize(document);
}

export { failure as manifestFailure, address as manifestAddress };
