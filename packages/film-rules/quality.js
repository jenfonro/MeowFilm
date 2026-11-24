// Single source of truth for film playback quality grade normalization.
// Self-contained (no outer references) so it can be imported directly in Node
// and serialized into the QuickJS rule kernel by packages/film-playback/src/rules.js.
export function qualityKey(value) {
  if (typeof value !== 'string') return '';
  let text = '';
  for (const ch of value.trim()) {
    const unit = ch.charCodeAt(0);
    text += unit >= 65 && unit <= 90 ? String.fromCharCode(unit + 32) : ch;
  }
  if (/^4k[· _-]?hdr$/.test(text)) return '4k_hdr';
  if (/^4k[· _-]?(?:fps|60(?:fps|帧)?)$/.test(text)) return '4k_fps';
  if (text === '4k' || text === '2160p' || text === 'uhd') return '4k';
  if (text === '1080p' || text === '1080i' || text === 'fhd') return '1080p';
  if (text === '720p' || text === 'hd') return '720p';
  return '';
}
