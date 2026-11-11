// Only explicitly prefetched media is retained, inside its existing media lease.
// These budgets include pending chunk assembly; they never limit ordinary playback bytes.
const ownerBudget = 32 * 1024 * 1024;
const poolBudget = 64 * 1024 * 1024;
export const progressivePrefetchBytes = 1024 * 1024;

function failure(code) {
  return Object.assign(new Error(code), { code });
}

export function cacheCoverage(entry, start, end) {
  const ranges = [];
  let next = start;
  for (const range of entry?.ranges ?? []) {
    if (range.end < next) continue;
    if (range.start > next || next > end) break;
    const last = Math.min(range.end, end);
    ranges.push({ start: next, end: last, body: range.body.subarray(next - range.start, last - range.start + 1) });
    next = last + 1;
  }
  return { ranges, end: next - 1 };
}

export function cacheGaps(entry, start, end) {
  const gaps = [];
  let next = start;
  for (const range of entry?.ranges ?? []) {
    if (range.end < next) continue;
    if (range.start > end) break;
    if (range.start > next) gaps.push({ start: next, end: Math.min(end, range.start - 1) });
    next = Math.max(next, range.end + 1);
    if (next > end) break;
  }
  if (next <= end) gaps.push({ start: next, end });
  return gaps;
}

export function createMediaCachePool() {
  let allocated = 0;
  return {
    create() {
      const records = new Map(), pending = new Map();
      let allocatedOwner = 0, revoked = false;
      function reserve(bytes) {
        if (allocatedOwner + bytes > ownerBudget || allocated + bytes > poolBudget) {
          throw failure('MEDIA_PREFETCH_LIMIT');
        }
        allocated += bytes; allocatedOwner += bytes;
      }
      function unreserve(bytes) { allocated -= bytes; allocatedOwner -= bytes; }
      function binary(url) {
        const values = [...records.values()].filter(record => record.kind === 'binary' && record.url === url);
        if (!values.length) return null;
        return { headers: values[0].headers, total: values[0].total,
          ranges: values.map(record => ({ start: record.start, end: record.end, body: record.body }))
            .sort((a, b) => a.start - b.start || b.end - a.end) };
      }
      return {
        binary,
        document(key) {
          return [...records.values()].find(record => record.kind === 'document' && record.documentKey === key);
        },
        load(key, url, producer) {
          if (revoked) return Promise.reject(failure('MEDIA_CANCELLED'));
          if (records.has(key)) return Promise.resolve(records.get(key));
          if (pending.has(key)) return pending.get(key).promise;
          const promise = Promise.resolve().then(async () => {
            const chunks = [];
            let bytes = 0, charge = 0;
            try {
              const metadata = await producer(chunk => {
                if (revoked) throw failure('MEDIA_CANCELLED');
                // Keep no slice of a much larger upstream buffer. Account both these chunks
                // and the final contiguous cache buffer until assembly is finished.
                reserve(chunk.length * 2); charge += chunk.length * 2;
                chunks.push(Buffer.from(chunk)); bytes += chunk.length;
              });
              if (revoked) throw failure('MEDIA_CANCELLED');
              if (!bytes) throw failure('MEDIA_EMPTY_RESPONSE');
              if (metadata.kind === 'binary') {
                const existing = binary(url);
                if (existing && (existing.total !== metadata.total ||
                    ['etag', 'last-modified'].some(name =>
                      existing.headers[name] && metadata.headers[name] && existing.headers[name] !== metadata.headers[name]))) {
                  throw failure('MEDIA_RESOURCE_CHANGED');
                }
              }
              const body = Buffer.concat(chunks, bytes);
              unreserve(bytes); charge -= bytes;
              const record = { ...metadata, url, body };
              records.set(key, record);
              charge = 0; // The record owns the remaining bytes until close.
              return record;
            } finally {
              unreserve(charge);
              pending.delete(key);
            }
          });
          pending.set(key, { promise });
          return promise;
        },
        get bytes() {
          return [...records.values()].filter(record => record.kind === 'binary')
            .reduce((total, record) => total + record.body.length, 0);
        },
        revoke() { revoked = true; },
        async close() {
          revoked = true;
          await Promise.allSettled([...pending.values()].map(value => value.promise));
          for (const record of records.values()) unreserve(record.body.length);
          records.clear();
        },
      };
    },
  };
}

// A SegmentBase index is finite control metadata, not decoded/transcoded media.
// Resolve the first direct SIDX reference; hierarchical indexes need their own semantics.
export function firstDASHRange(body, indexStart) {
  try {
    let position = 0;
    while (position + 8 <= body.length) {
      let size = body.readUInt32BE(position), header = 8;
      if (size === 1) { size = Number(body.readBigUInt64BE(position + 8)); header = 16; }
      if (size === 0) size = body.length - position;
      if (!Number.isSafeInteger(size) || size < header || position + size > body.length) throw failure();
      if (body.toString('ascii', position + 4, position + 8) !== 'sidx') { position += size; continue; }
      const version = body[position + header];
      if (![0, 1].includes(version) || body.readUInt32BE(position + header + 8) === 0) throw failure();
      const offsetPosition = position + header + (version ? 20 : 16);
      const offset = version ? Number(body.readBigUInt64BE(offsetPosition)) : body.readUInt32BE(offsetPosition);
      const countPosition = offsetPosition + (version ? 8 : 4) + 2;
      const count = body.readUInt16BE(countPosition);
      if (!count || countPosition + 2 + count * 12 > position + size) throw failure();
      const reference = body.readUInt32BE(countPosition + 2);
      if (reference & 0x80000000 || !(reference & 0x7fffffff)) throw failure();
      const start = indexStart + position + size + offset, end = start + (reference & 0x7fffffff) - 1;
      if (![offset, start, end].every(Number.isSafeInteger) || start < 0) throw failure();
      return { start, end };
    }
  } catch { throw failure('MEDIA_PREFETCH_UNSUPPORTED'); }
  throw failure('MEDIA_PREFETCH_UNSUPPORTED');
}
