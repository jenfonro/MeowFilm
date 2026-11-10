import { manifestFailure as failure, manifestAddress as address, hlsAttributes,
  content, elements, localName, named, parseXML } from '@meowfilm/script-host/media-manifest';
export { compileDASHTemplate, dashTemplate, decodeManifest, manifestFormat, rewriteDASH, rewriteHLS }
  from '@meowfilm/script-host/media-manifest';

function hlsValues(line) {
  const result = {};
  hlsAttributes(line, value => value, (name, value) => { result[name] = value; });
  return result;
}

function byteRange(value) {
  // A startup plan has no preceding media segment or part to supply an implicit offset.
  const match = /^(\d+)@(\d+)$/.exec(value ?? '');
  if (!match) throw failure();
  const length = Number(match[1]), start = Number(match[2]);
  const end = start + length - 1;
  if (![length, start, end].every(Number.isSafeInteger) || length < 1 || start < 0) throw failure();
  return { start, end };
}

// Plans only startup media for an explicit short-video prefetch. The complete playlist
// is still delivered unchanged except for local media URI rewriting.
export function hlsPrefetchPlan(text) {
  if (!text.replace(/^\uFEFF/, '').startsWith('#EXTM3U')) throw failure();
  const lines = text.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const variants = [], renditions = [], sessionKeys = [];
  let pendingVariant;
  for (const line of lines) {
    const tag = line.split(':', 1)[0];
    if (tag === '#EXT-X-STREAM-INF') pendingVariant = hlsValues(line);
    else if (!line.startsWith('#') && pendingVariant) {
      variants.push({ ...pendingVariant, url: line }); pendingVariant = undefined;
    } else if (tag === '#EXT-X-MEDIA') {
      const values = hlsValues(line);
      if (['AUDIO', 'VIDEO'].includes(values.TYPE) && values.URI) renditions.push(values);
    } else if (tag === '#EXT-X-SESSION-KEY') sessionKeys.push(hlsValues(line));
  }
  function key(values) {
    if (values.METHOD === 'NONE') return null;
    if (!['AES-128', 'SAMPLE-AES', 'SAMPLE-AES-CTR'].includes(values.METHOD) ||
        values.KEYFORMAT && values.KEYFORMAT !== 'identity') throw failure('MEDIA_DRM_UNSUPPORTED');
    if (!values.URI) throw failure();
    return values.URI.startsWith('data:') ? null : { url: values.URI };
  }
  if (pendingVariant) throw failure();
  if (variants.length) {
    variants.sort((a, b) => Number(a.BANDWIDTH ?? Infinity) - Number(b.BANDWIDTH ?? Infinity));
    const selected = variants[0], documents = [selected.url];
    for (const type of ['AUDIO', 'VIDEO']) {
      const tracks = renditions.filter(track => track.TYPE === type && track['GROUP-ID'] === selected[type]);
      const selectedTrack = tracks.find(track => track.DEFAULT === 'YES') ?? tracks[0];
      if (selectedTrack) documents.push(selectedTrack.URI);
    }
    return { documents, binaries: sessionKeys.map(key).filter(Boolean) };
  }
  let currentKey, mapKey, map, range, part;
  for (const line of lines) {
    const tag = line.split(':', 1)[0];
    if (tag === '#EXT-X-KEY') currentKey = key(hlsValues(line));
    else if (tag === '#EXT-X-MAP') {
      const values = hlsValues(line);
      if (!values.URI) throw failure();
      map = { url: values.URI, ...(values.BYTERANGE ? { range: byteRange(values.BYTERANGE) } : {}) };
      mapKey = currentKey;
    } else if (tag === '#EXT-X-BYTERANGE') range = line.slice(line.indexOf(':') + 1);
    else if (tag === '#EXT-X-PART' && !part) {
      const values = hlsValues(line);
      if (!values.URI) throw failure();
      part = { documents: [], binaries: [mapKey, currentKey, map, { url: values.URI, media: true,
        ...(values.BYTERANGE ? { range: byteRange(values.BYTERANGE) } : {}) }].filter(Boolean) };
    } else if (!line.startsWith('#')) {
      const segment = { url: line, media: true, ...(range ? { range: byteRange(range) } : {}) };
      return { documents: [], binaries: [mapKey, currentKey, map, segment].filter(Boolean) };
    }
  }
  if (part) return part;
  throw failure('MEDIA_PREFETCH_UNSUPPORTED');
}

function xmlRange(value) {
  const match = /^(\d+)-(\d+)$/.exec(value ?? '');
  if (!match) throw failure();
  const start = Number(match[1]), end = Number(match[2]);
  if (![start, end].every(Number.isSafeInteger) || start < 0 || end < start) throw failure();
  return { start, end };
}

function firstTemplate(value, segment) {
  const number = segment.attrs.startNumber ?? '1';
  const first = named(named(segment, 'SegmentTimeline')[0] ?? { children: [] }, 'S')[0];
  if (/\$Time(?:%[^$]*)?\$/.test(value) && !first) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
  const time = first?.attrs.t ?? '0';
  if (![number, time].every(value => /^\d+$/.test(value))) throw failure();
  return value.replace(/\$\$|\$(Number|Time|SubNumber)(?:%(0?)([1-9][0-9]?)?d)?\$/g,
    (token, name, zero, width) => token === '$$' ? '$'
      : ({ Number: number, Time: time, SubNumber: '1' })[name].padStart(Number(width ?? 0), zero ? '0' : ' '));
}

export function dashPrefetchPlan(text) {
  const document = parseXML(text), mpd = elements(document)[0], period = named(mpd, 'Period')[0];
  if (!period) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
  const tracks = [];
  function collect(node, inherited = {}) {
    const attributes = { ...inherited, ...node.attrs };
    if (localName(node) === 'Representation') tracks.push({ node, attributes });
    else for (const child of elements(node)) if (['AdaptationSet', 'Representation'].includes(localName(child))) collect(child, attributes);
  }
  collect(period);
  function kind(track) {
    const values = track.attributes;
    if (values.contentType) return values.contentType;
    if (values.mimeType?.startsWith('audio/') || /^(mp4a|opus|ac-3|ec-3)/.test(values.codecs ?? '')) return 'audio';
    if (values.mimeType?.startsWith('video/') || /^(avc[13]|hev1|hvc1|vp0[89]|av01)/.test(values.codecs ?? '')) return 'video';
    return values.mimeType?.startsWith('text/') ? 'text' : 'unknown';
  }
  tracks.sort((a, b) => Number(a.attributes.bandwidth ?? Infinity) - Number(b.attributes.bandwidth ?? Infinity));
  if (tracks.length > 1 && tracks.some(track => kind(track) === 'unknown')) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
  const selected = ['video', 'audio'].map(type => tracks.find(track => kind(track) === type)).filter(Boolean);
  if (!selected.length) {
    if (tracks.length === 1 && kind(tracks[0]) === 'unknown') selected.push(tracks[0]);
  }
  if (!selected.length) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
  const binaries = [], indexes = [];
  for (const { node } of selected) {
    const base = content(named(node, 'BaseURL')[0] ?? { children: [] });
    if (!base) throw failure();
    const segment = elements(node).find(child => ['SegmentTemplate', 'SegmentList', 'SegmentBase'].includes(localName(child)));
    if (!segment) { binaries.push({ url: base, progressive: true, media: true }); continue; }
    const mode = localName(segment), initialization = named(segment, 'Initialization')[0];
    if (mode === 'SegmentTemplate') {
      if (segment.attrs.initialization) binaries.push({ url: address(firstTemplate(segment.attrs.initialization, segment), base) });
      else if (initialization) binaries.push({ url: address(initialization.attrs.sourceURL ?? '', base),
        ...(initialization.attrs.range ? { range: xmlRange(initialization.attrs.range) } : {}) });
      if (!segment.attrs.media) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
      binaries.push({ url: address(firstTemplate(segment.attrs.media, segment), base), media: true });
    } else {
      if (initialization) binaries.push({ url: address(initialization.attrs.sourceURL ?? '', base),
        ...(initialization.attrs.range ? { range: xmlRange(initialization.attrs.range) } : {}) });
      if (mode === 'SegmentList') {
        const first = named(segment, 'SegmentURL')[0];
        if (!first) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
        if (first.attrs.index) binaries.push({ url: address(first.attrs.index, base),
          ...(first.attrs.indexRange ? { range: xmlRange(first.attrs.indexRange) } : {}) });
        binaries.push({ url: address(first.attrs.media ?? '', base), media: true,
          ...(first.attrs.mediaRange ? { range: xmlRange(first.attrs.mediaRange) } : {}) });
      } else {
        const index = named(segment, 'RepresentationIndex')[0];
        const range = index?.attrs.range ?? segment.attrs.indexRange;
        if (!range) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
        const url = address(index?.attrs.sourceURL ?? '', base);
        if (url !== base) throw failure('MEDIA_PREFETCH_UNSUPPORTED');
        indexes.push({ url, mediaUrl: base, range: xmlRange(range) });
      }
    }
  }
  return { binaries, indexes };
}
