const identityFields = new Set(['id', 'playId', 'url', 'fid', 'fileId', 'file_id', 'fs_id',
  'shareId', 'shareid', 'share_id', 'stoken', 'sToken', 's_token', 'randsk', 'shareDirFileId',
  'pdir_fid', 'pdirFid', 'pCaID', 'coID', 'filename', 'fileName', 'file_name', 'server_filename',
  'name', 'coName', 'path', 'share_fid_token', 'fid_token', 'fidToken', 'token']);

/** Cat consumes raw IDs, filename markers, URLs and JSON/base64-encoded file records. */
export function panReferenceKeys(input, lookup, { scalar = true } = {}) {
  const keys = new Set(), visited = new Set(), pending = [{ value: input, scalar }];
  let unknown = false;
  while (pending.length) {
    let { value, scalar } = pending.pop();
    if (typeof value === 'number') value = String(value);
    if (typeof value !== 'string' || visited.has(scalar + ':' + value)) continue;
    visited.add(scalar + ':' + value);
    try {
      const url = new URL(value);
      if (['http:', 'https:'].includes(url.protocol)) {
        for (const [field, part] of url.searchParams)
          if (identityFields.has(field)) pending.push({ value: part, scalar: true });
        pending.push({ value: url.pathname, scalar: false });
        continue;
      }
    } catch {}
    try {
      const record = JSON.parse(value);
      if (record && typeof record === 'object' && !Array.isArray(record)) {
        for (const [field, item] of Object.entries(record)) {
          if (identityFields.has(field)) pending.push({ value: item, scalar: true });
        }
        continue;
      }
    } catch {}
    try {
      const decoded = decodeURIComponent(value);
      if (decoded !== value) pending.push({ value: decoded, scalar });
    } catch {}
    if (scalar) for (const key of lookup(value) ?? []) keys.add(key);
    for (const marker of value.match(/meow_[a-f0-9]{24}/g) ?? []) {
      const found = lookup(marker);
      if (!found?.size) unknown = true;
      else for (const key of found) keys.add(key);
    }
    if (/^[A-Za-z0-9+/_-]+={0,2}$/.test(value)) {
      const bytes = Buffer.from(value, 'base64');
      const canonical = value.replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '');
      if (bytes.toString('base64').replace(/=+$/, '') === canonical)
        pending.push({ value: bytes.toString(), scalar });
    }
  }
  return { keys, unknown };
}
