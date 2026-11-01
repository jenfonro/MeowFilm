const messages = Object.freeze({
  INVALID_ARGUMENT: 'Invalid host argument',
  NOT_FOUND: 'Installation not found',
  ALREADY_EXISTS: 'Installation already exists',
  DISABLED: 'Installation is disabled',
  HOST_CLOSED: 'Host is closed',
  STALE_SESSION: 'Session is no longer current',
  CANCELLED: 'Operation cancelled',
  TIMEOUT: 'Operation timed out',
  PERMISSION_DENIED: 'Capability denied',
  CAPABILITY_UNAVAILABLE: 'Capability unavailable',
  UNSUPPORTED_PROTOCOL: 'Unsupported script protocol',
  UNSUPPORTED_ACTION: 'Unsupported source action',
  UNSUPPORTED_RULE: 'Unsupported source rule',
  UNSUPPORTED_SCRIPT_ASYNC: 'Source script cannot be converted to asynchronous calls',
  SCRIPT_PROTOCOL_ERROR: 'Invalid script protocol',
  SCRIPT_EXCEPTION: 'Script execution failed',
  RESOURCE_LIMIT: 'Resource limit exceeded',
  NETWORK_ERROR: 'Network operation failed',
  HTTP_ERROR: 'Source HTTP endpoint failed',
  INVALID_RESPONSE: 'Source response is invalid',
  INVALID_JSON: 'Invalid JSON response',
  STORAGE_ERROR: 'Storage operation failed',
  STORAGE_CONFLICT: 'Script state changed',
  COMMIT_UNKNOWN: 'Script state commit requires reconciliation',
  CORRUPT_STORAGE: 'Script state is invalid',
  SERVICE_ERROR: 'Local service operation failed',
  SERVICE_EXISTS: 'Local service already registered',
  SERVICE_CLOSED: 'Local service is closed',
  ROLLBACK_UNAVAILABLE: 'No previous revision',
  UNSUPPORTED_OPTION: 'Unsupported protocol option',
  UNSUPPORTED_CRYPTO: 'Unsupported LX crypto operation',
  UNSUPPORTED_DEPENDENCY: 'Dependency is outside this protocol profile',
  WEB_INTERACTION_REQUIRED: 'Source requires interactive web access',
  VIRTUAL_ROUTE_NOT_FOUND: 'Virtual source route was not found',
  UNMAPPED_LOCAL_URL: 'Local URL has no current scoped endpoint',
  UPSTREAM_HTTP_ERROR: 'Script HTTP endpoint failed',
});

export class ScriptHostError extends Error {
  constructor(code) {
    super(messages[code] ?? messages.SCRIPT_EXCEPTION);
    this.name = code === 'CANCELLED' ? 'AbortError' : 'ScriptHostError';
    this.code = Object.hasOwn(messages, code) ? code : 'SCRIPT_EXCEPTION';
  }
}

export const fault = (code) => new ScriptHostError(code);
export const safeError = (error, fallback = 'SCRIPT_EXCEPTION') => {
  if (error instanceof ScriptHostError) return error;
  if (typeof error?.code === 'string' && Object.hasOwn(messages, error.code)) {
    return fault(error.code);
  }
  if (error?.name === 'AbortError') return fault('CANCELLED');
  return fault(fallback);
};
export function requireValue(condition, code = 'INVALID_ARGUMENT') {
  if (!condition) throw fault(code);
}

export function identifier(value) {
  requireValue(typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(value));
  return value;
}

export function clone(value) {
  try { return structuredClone(value); } catch { throw fault('INVALID_ARGUMENT'); }
}

export function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  // The lifecycle can fail before the caller starts awaiting initialization.
  promise.catch(() => {});
  return { promise, resolve, reject };
}
