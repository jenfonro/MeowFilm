/** Fixed diagnostic vocabulary only: never return exception text or a URL.
 *
 * Product diagnostics report stable JavaScript/Node evidence. Acceptance-only
 * text attribution lives in `scripts/source-acceptance-diagnostics.mjs`. */
export function describeScriptError(error) {
  const text = String(error?.message ?? '');
  const kind = ['Error', 'TypeError', 'ReferenceError', 'SyntaxError', 'RangeError', 'InternalError']
    .includes(error?.name) ? error.name : 'Error';
  const codeReasons = {
    ERR_INVALID_URL: 'input', ERR_INVALID_PROTOCOL: 'input',
    ERR_INVALID_ARG_TYPE: 'input', ERR_INVALID_ARG_VALUE: 'input',
    ERR_OUT_OF_RANGE: 'input', ERR_CRYPTO_INVALID_KEYLEN: 'input',
    ENOTFOUND: 'upstream', EAI_AGAIN: 'upstream', ECONNRESET: 'upstream',
    ECONNREFUSED: 'upstream', ETIMEDOUT: 'upstream', ECONNABORTED: 'upstream',
    NETWORK_ERROR: 'upstream',
    ERR_HTTP_INVALID_HEADER_VALUE: 'input', ERR_INVALID_HTTP_TOKEN: 'input',
    ERR_HTTP_INVALID_STATUS_CODE: 'input', ERR_INVALID_CHAR: 'input',
    ERR_UNESCAPED_CHARACTERS: 'input', ERR_CRYPTO_ECDH_INVALID_PUBLIC_KEY: 'input',
    Z_DATA_ERROR: 'input', Z_BUF_ERROR: 'input',
    ERR_CANCELED: 'upstream', CANCELLED: 'upstream',
  };
  const code = Object.hasOwn(codeReasons, error?.code) ? error.code : undefined;
  const reference = kind === 'ReferenceError'
    ? text.match(/['"]?([A-Za-z_$][\w$]{0,63})['"]? is not defined/) : null;
  const member = kind === 'TypeError'
    ? text.match(/(?:['"]|\.)([A-Za-z_$][\w$]{0,63})['"]?\s+(?:is not a function|of undefined)/) : null;
  const location = String(error?.stack ?? '')
    .match(/(?:installed-script|source|script-sdk)\.js:(\d+)(?::(\d+))?/);
  const reason = reference ? 'reference'
    : code ? codeReasons[code]
    : /^(?:TypeError|RangeError)$/.test(kind) ? 'input' : 'script';
  return { kind, reason, ...(code ? { code } : {}),
    ...(reference ? { reference: reference[1] } : {}),
    ...(member ? { member: member[1] } : {}),
    ...(location ? { line: Number(location[1]), column: Number(location[2] || 0) } : {}) };
}
