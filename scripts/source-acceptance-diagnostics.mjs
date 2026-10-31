/** Classify only observed failures. No exception text or source-specific rules. */
export function classifySourceFailure(kind, reply, network, diagnostics) {
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
  if (diagnostics.some(event => event.reason === 'host' && event.code === 'SCRIPT_EXCEPTION'))
    return 'requires_diagnosis';
  const jsonFailure = ['empty_json', 'invalid_json'].includes(guest?.reason)
    ? network.some(event => event.scope === 'request' && Number.isInteger(event.statusCode))
      ? 'upstream_failure' : guest.reason === 'empty_json' ? 'input_required' : null
    : null;
  if (kind === 'cat' && reply.code === 'UPSTREAM_HTTP_ERROR') {
    const http = diagnostics.findLast(event => event.reason === 'http');
    if (guest?.reason === 'configuration') return 'configuration_required';
    if (guest?.reason === 'input') return 'input_required';
    if (guest?.reason === 'unsupported' || guest?.reason === 'reference') return 'script_rejected';
    if (jsonFailure) return jsonFailure;
    if (network.some(event => event.scope !== 'session' && (event.code || event.statusCode >= 400)) ||
      guest?.reason === 'upstream' || [408, 429].includes(http?.statusCode)) return 'upstream_failure';
    if ([401, 403].includes(http?.statusCode)) return 'configuration_required';
    return 'requires_diagnosis';
  }
  if (['NETWORK_ERROR', 'UPSTREAM_HTTP_ERROR', 'TIMEOUT'].includes(reply.code)) return 'upstream_failure';
  if (['INVALID_ARGUMENT', 'SCRIPT_PROTOCOL_ERROR'].includes(reply.code)) return 'script_rejected';
  if (network.some(event => event.responseReason === 'configuration') ||
    guest?.reason === 'configuration') return 'configuration_required';
  if (guest?.reason === 'unsupported' || guest?.reason === 'reference') return 'script_rejected';
  if (jsonFailure) return jsonFailure;
  if (network.some(event => event.code || event.statusCode >= 400 ||
    typeof event.businessCode === 'number' && ![0, 1, 200].includes(event.businessCode) ||
    event.responseReason === 'upstream') || guest?.reason === 'upstream') return 'upstream_failure';
  if (guest?.reason === 'input') return 'input_required';
  return 'requires_diagnosis';
}
