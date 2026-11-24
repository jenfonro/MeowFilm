// RFC 6265 cookie-octet: the token also travels in the native EAPI header cookie.
export const validToken = value => typeof value === 'string' && value.length > 0 &&
  !/[^\x21\x23-\x2b\x2d-\x3a\x3c-\x5b\x5d-\x7e]/.test(value);
