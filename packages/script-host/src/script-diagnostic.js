/** Fixed diagnostic vocabulary only: never return exception text or a URL. */
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
    : /api.?key|密钥|未配置|无API配置|config.*(?:required|missing|not found)|token.*missing|cookie.*(?:required|missing)|账号.*(?:为空|未|无效)|(?:没有|尚未|还未)配置.{0,40}(?:cookie|token|账号)|需要(?:登录|登陆)|请先.{0,60}(?:配置|登录|登陆|设置.*添加频道)/i.test(text) ? 'configuration'
    : /^unexpected end of JSON input$/i.test(text) ? 'empty_json'
    : /^unexpected character$|not valid JSON|unexpected token.*JSON/i.test(text) ? 'invalid_json'
    : /缺少|missing|参数|信息不完整|undefined|null|(?:标识|标记|页码).{0,24}(?:无效|失效|超出范围)/i.test(text) ? 'input'
    : /unsupported|not implemented|不支持|action not support|unknown (?:action|source)|未知(?:动作|音源)|未实现/i.test(text) ? 'unsupported'
    : /api|接口|获取|请求|HTTP|找不到|不存在|为空|解析.*失败|所有(?:音源|音质)均失败|无歌词|no.*url|not found|invalid.*response|没有.*可用|暂无(?:直播)?(?:源|资源)|response/i.test(text)
      ? 'upstream' : 'script';
  return { kind, reason, ...(code ? { code } : {}),
    ...(reference ? { reference: reference[1] } : {}),
    ...(member ? { member: member[1] } : {}),
    ...(location ? { line: Number(location[1]), column: Number(location[2] || 0) } : {}) };
}
