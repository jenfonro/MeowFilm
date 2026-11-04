/** Acceptance-only text attribution. Product diagnostics report stable
 * JavaScript/Node evidence; this layer may classify known script messages but
 * never persists their text. */
export function classifyGuestMessage(message) {
  const text = String(message ?? '');
  if (/api.?key|密钥|未配置|无API配置|config.*(?:required|missing|not found)|token.*missing|cookie.*(?:required|missing)|账号.*(?:为空|未|无效)|(?:没有|尚未|还未)配置.{0,40}(?:cookie|token|账号)|需要(?:登录|登陆)|请先.{0,60}(?:配置|登录|登陆|设置.*添加频道)/i.test(text))
    return 'configuration';
  if (/^unexpected end of JSON input$/i.test(text)) return 'empty_json';
  if (/^unexpected character$|not valid JSON|unexpected token.*JSON/i.test(text)) return 'invalid_json';
  if (/缺少|missing|参数|信息不完整|undefined|null|(?:标识|标记|页码).{0,24}(?:无效|失效|超出范围)/i.test(text))
    return 'input';
  if (/unsupported|not implemented|不支持|action not support|unknown (?:action|source)|未知(?:动作|音源)|未实现/i.test(text))
    return 'unsupported';
  if (/api|接口|获取|请求|HTTP|找不到|不存在|为空|解析.*失败|所有(?:音源|音质)均失败|无歌词|no.*url|not found|invalid.*response|没有.*可用|暂无(?:直播)?(?:源|资源)|response/i.test(text))
    return 'upstream';
  return 'script';
}

/** Raw messages exist only during this opt-in observation call. Reports keep
 * the same scoped structural fields, with text attribution owned here. */
export function sourceDiagnosticObservers(diagnostics) {
  const guestKinds = ['Error', 'TypeError', 'ReferenceError', 'SyntaxError', 'RangeError', 'InternalError'];
  return {
    onDiagnostic(event) {
      if (!guestKinds.includes(event.kind)) diagnostics.push(event);
    },
    onScriptError({ message, ...event }) {
      const classified = classifyGuestMessage(message);
      const reason = event.code || event.reason === 'reference'
        ? event.reason : classified;
      diagnostics.push({ ...event, reason });
    },
  };
}

export function classifySourceFailure(kind, reply, network, diagnostics) {
  const requestNetwork = network.filter(event => event.scope !== 'session');
  const hostGlobals = ['Buffer', 'URL', 'URLSearchParams', 'TextEncoder', 'TextDecoder',
    'fetch', 'lx', 'setTimeout', 'setInterval'];
  const guest = diagnostics.findLast(event => !['host', 'http', 'script'].includes(event.reason)) ??
    diagnostics.findLast(event => !['host', 'http'].includes(event.reason));
  const unsupported = ['UNSUPPORTED_OPTION', 'UNSUPPORTED_CRYPTO', 'CAPABILITY_UNAVAILABLE'];
  if (unsupported.includes(reply.code) ||
    diagnostics.some(event => event.reason === 'host' && unsupported.includes(event.code)) ||
    network.some(event => unsupported.includes(event.code)) ||
    guest?.reason === 'reference' && hostGlobals.includes(guest.reference)) return 'host_failure';
  if (['LIMIT', 'RESOURCE_LIMIT'].includes(reply.code) ||
    diagnostics.some(event => event.reason === 'host' && event.code === 'RESOURCE_LIMIT'))
    return 'runtime_limit';
  if (['PERMISSION_DENIED', 'UNMAPPED_LOCAL_URL'].includes(reply.code) ||
    diagnostics.some(event => event.reason === 'host' &&
      ['PERMISSION_DENIED', 'UNMAPPED_LOCAL_URL'].includes(event.code))) return 'script_rejected';
  if (diagnostics.some(event => event.reason === 'host' && event.code === 'SCRIPT_EXCEPTION' &&
    event.operation !== 'session.failed'))
    return 'requires_diagnosis';
  const jsonFailure = ['empty_json', 'invalid_json'].includes(guest?.reason)
    ? requestNetwork.some(event => Number.isInteger(event.statusCode))
      ? 'upstream_failure' : guest.reason === 'empty_json' ? 'input_required' : null
    : null;
  if (kind === 'cat' && reply.code === 'UPSTREAM_HTTP_ERROR') {
    const http = diagnostics.findLast(event => event.reason === 'http');
    if (guest?.reason === 'configuration') return 'configuration_required';
    if (guest?.reason === 'input') return 'input_required';
    if (guest?.reason === 'unsupported' || guest?.reason === 'reference') return 'script_rejected';
    if (jsonFailure) return jsonFailure;
    if (requestNetwork.some(event => event.code || event.statusCode >= 400) ||
      guest?.reason === 'upstream' || [408, 429].includes(http?.statusCode)) return 'upstream_failure';
    if ([401, 403].includes(http?.statusCode)) return 'configuration_required';
    return 'requires_diagnosis';
  }
  if (['NETWORK_ERROR', 'UPSTREAM_HTTP_ERROR'].includes(reply.code)) return 'upstream_failure';
  if (['INVALID_ARGUMENT', 'SCRIPT_PROTOCOL_ERROR'].includes(reply.code)) return 'script_rejected';
  if (requestNetwork.some(event => event.responseReason === 'configuration') ||
    guest?.reason === 'configuration') return 'configuration_required';
  if (guest?.reason === 'unsupported' || guest?.reason === 'reference') return 'script_rejected';
  if (jsonFailure) return jsonFailure;
  if (requestNetwork.some(event => event.code || event.statusCode >= 400 ||
    typeof event.businessCode === 'number' && ![0, 1, 200].includes(event.businessCode) ||
    event.responseReason === 'upstream') || guest?.reason === 'upstream') return 'upstream_failure';
  if (guest?.reason === 'input') return 'input_required';
  return 'requires_diagnosis';
}
