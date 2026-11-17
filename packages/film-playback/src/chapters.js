import { object, requireValue } from './identity.js';

// Only complete container labels, optionally prefixed by a chapter number.
const label = /^(?:(?:chapter\s+|章节\s*)?\d{1,3}\s*[.:\-、：]\s*)?(片头|片头曲|片尾|片尾曲|opening(?:\s+credits)?|ending(?:\s+credits)?|intro|outro|op|ed)(?:\s+\d{1,3})?$/i;
const intro = /^(?:片头|片头曲|opening(?:\s+credits)?|intro|op)$/i;

export function recognizeChapters(chapters) {
  requireValue(Array.isArray(chapters) && chapters.length <= 256);
  const segments = [];
  for (const chapter of chapters) {
    requireValue(object(chapter) && typeof chapter.title === 'string' && chapter.title.length <= 256 &&
      Number.isFinite(chapter.start) && chapter.start >= 0 &&
      Number.isFinite(chapter.end) && chapter.end > chapter.start);
    const match = label.exec(chapter.title.trim());
    if (!match) continue;
    const segment = { kind: intro.test(match[1]) ? 'intro' : 'outro', start: chapter.start, end: chapter.end };
    if (!segments.some(value => value.kind === segment.kind && value.start === segment.start && value.end === segment.end))
      segments.push(segment);
  }
  segments.sort((a, b) => a.start - b.start || a.end - b.end || a.kind.localeCompare(b.kind));
  return { segments };
}
