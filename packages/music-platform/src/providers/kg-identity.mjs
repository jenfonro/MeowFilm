import { cookieFields } from '../protocols.mjs';

export function kgIdentity(c) {
  const cookies = cookieFields(c.account?.cookie), device = c.client();
  return {
    mid: cookies.KUGOU_API_MID ?? device.mid,
    dfid: cookies.dfid ?? device.dfid ?? '-',
    uid: c.account?.userId ?? 0,
    token: cookies.token ?? '',
  };
}
