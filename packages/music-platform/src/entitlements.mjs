// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus membership interpretation; account authorization remains native-owned.
import { optionalNumber, scalar } from './model.mjs';

const day = 86400000;
const wyLevels = ['standard', 'higher', 'exhigh', 'lossless', 'hires', 'jyeffect', 'dolby', 'sky', 'jymaster', 'vivid'];
export function unknownRights(now, errorCode = null, platform) {
  return { membership: { vip: null, tier: 'unknown', expiresAt: null }, checkedAt: now, recheckAt: 0,
    audioLevels: [], verificationDeferred: true, errorCode, retryAt: platform === 'kg' ? now + 60000 : 0 };
}
function expiry(value) {
  const numeric = optionalNumber(value);
  if (numeric !== null && numeric > 0) return numeric < 1e12 ? numeric * 1000 : numeric;
  const raw = scalar(value);
  if (!raw) return null;
  const local = /^\d{4}-\d{2}-\d{2}(?: \d{2}:\d{2}:\d{2})?$/.test(raw);
  const time = Date.parse(local ? raw.replace(' ', 'T') + (raw.length === 10 ? 'T00:00:00' : '') + '+08:00' : raw);
  return Number.isFinite(time) && time > 0 ? time : null;
}
function products(items, now, platform) {
  let known = false, uncertain = false, expired = null, membership = null;
  for (const [tier, value, endValue, startValue] of items) {
    if (value == null) continue;
    if (value !== 0 && value !== 1) { uncertain = true; continue; }
    known = true;
    const end = expiry(endValue), start = expiry(startValue);
    if (end !== null && end <= now) expired = Math.max(expired ?? 0, end);
    if (value === 1 && (start === null || start <= now) && (end === null || end > now))
      membership ??= { vip: true, tier, expiresAt: end };
  }
  membership ??= { vip: known && !uncertain ? false : null,
    tier: known && !uncertain ? 'none' : 'unknown', expiresAt: expired };
  let recheckAt = membership.vip === null ? 0 : membership.vip ?
    membership.expiresAt ?? (platform === 'kg' ? now + 60000 : now) : now + day;
  for (const [, value, end, start] of items) {
    if (platform === 'kg' ? value !== 0 && value !== 1 : value !== 1) continue;
    for (const boundary of [expiry(end), expiry(start)]) if (boundary > now && recheckAt > 0)
      recheckAt = Math.min(recheckAt, boundary);
  }
  return { membership, checkedAt: now, recheckAt, audioLevels: [], verificationDeferred: membership.vip === null,
    errorCode: null, retryAt: platform === 'kg' && membership.vip === null ? now + 60000 : 0 };
}
export function wyRights(data, now) {
  const upstream = optionalNumber(data.now) > 0 ? optionalNumber(data.now) : now;
  const values = [data.associator?.expireTime, data.musicPackage?.expireTime, data.redplus?.expireTime].map(optionalNumber);
  const remaining = values.map(value => Math.max(0, (value ?? 0) - upstream));
  const svip = remaining[2] > 0, known = values.some(value => value !== null);
  const vip = Math.max(...remaining) > 0 ? true : known ? false : null, end = vip ?
    svip ? values[2] : Math.max(values[0] ?? 0, values[1] ?? 0) :
    values.filter(value => value > 0 && value <= upstream).reduce((max, value) => Math.max(max ?? 0, value), null);
  return { membership: { vip, tier: vip ? svip ? 'svip' : 'vip' : known ? 'none' : 'unknown', expiresAt: end },
    checkedAt: now, recheckAt: vip === null ? 0 : vip ? now + Math.max(0, end - upstream) : now + day,
    audioLevels: vip ? wyLevels.slice(0, svip ? 10 : 6) : [], verificationDeferred: !known, errorCode: null, retryAt: 0 };
}
export function txRights(data, now) {
  const identity = data.identity ?? {}, end = data.userinfo?.expire;
  return products([
    ['svip', data.svip, end],
    ['huge_vip', identity.HugeVip ?? identity.huge_vip, identity.HugeVipEnd ?? identity.huge_vip_end,
      identity.HugeVipStart ?? identity.huge_vip_start],
    ...['twelve', 'eight'].map(key => [key, identity[key], identity[key + 'End'] ?? identity[key + '_end'],
      identity[key + 'Start'] ?? identity[key + '_start']]),
    ['group_vip', identity.GroupVipFlag ?? identity.group_vip_flag, identity.GroupVipEnd ?? identity.group_vip_end,
      identity.GroupVipStart ?? identity.group_vip_start],
    ['cp_lover', identity.CPLoverFlag ?? identity.cp_lover_flag, identity.CPLoverEnd ?? identity.cp_lover_end,
      identity.CPLoverStart ?? identity.cp_lover_start],
    ['star', data.star, data.starend ?? data.star_end, data.starstart ?? data.star_start],
    ['ystar', data.ystar, data.ystarend ?? data.ystar_end, data.ystarstart ?? data.ystar_start],
    ['vip', identity.vip, end], ['child_vip', identity.ChildVip ?? identity.child_vip],
    ['exp_vip', identity.ExpVip ?? identity.exp_vip], ['ad_vip', identity.AdVipFlag ?? identity.ad_vip_flag],
  ], now, 'tx');
}
export function kgRights(data, now) {
  const rows = Array.isArray(data.busi_vip) ? data.busi_vip : [];
  const names = new Set(['svip', 'tvip', 'wvip', 'qvip', 'dvip', 'evip']);
  const items = rows.map(row => (row.busi_type == null || row.busi_type === 'concept') && names.has(scalar(row.product_type)) ?
    ['concept:' + scalar(row.product_type), row.is_vip ?? -1, row.vip_end_time, row.vip_begin_time] :
    ['unknown', -1, null, null]);
  items.push(['vip', data.is_vip, data.vip_end_time, data.vip_begin_time]);
  return products(items, now, 'kg');
}
