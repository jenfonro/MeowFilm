const messages = Object.freeze({
  INVALID_ARGUMENT: 'Invalid source runtime argument',
  UNSUPPORTED_PROTOCOL: 'Unsupported source protocol',
  UNSUPPORTED_ACTION: 'Unsupported source action',
  UNSUPPORTED_RULE: 'Unsupported source rule',
  UNSUPPORTED_SCRIPT_ASYNC: 'Script cannot be converted to asynchronous capability calls',
  UNSUPPORTED_OPTION: 'Unsupported source option',
  UNSUPPORTED_CRYPTO: 'Unsupported source cryptography',
  UNSUPPORTED_DEPENDENCY: 'Script dependency is outside the portable runtime',
  CAPABILITY_UNAVAILABLE: 'Host capability is unavailable',
  PERMISSION_DENIED: 'Host capability denied',
  CANCELLED: 'Source operation cancelled',
  TIMEOUT: 'Source operation timed out',
  RESOURCE_LIMIT: 'Source resource limit exceeded',
  NETWORK_ERROR: 'Source network request failed',
  HTTP_ERROR: 'Source HTTP endpoint failed',
  INVALID_RESPONSE: 'Source response is invalid',
  SCRIPT_PROTOCOL_ERROR: 'Source script protocol is invalid',
  SCRIPT_EXCEPTION: 'Source script execution failed',
  STALE_SESSION: 'Source session is no longer current',
  STORAGE_ERROR: 'Source storage operation failed',
  WEB_INTERACTION_REQUIRED: 'Source requires interactive web access',
  VIRTUAL_ROUTE_NOT_FOUND: 'Virtual source route was not found',
});

export class SourceRuntimeError extends Error {
  constructor(code) {
    const known = Object.hasOwn(messages, code) ? code : 'SCRIPT_EXCEPTION';
    super(messages[known]);
    this.name = known === 'CANCELLED' ? 'AbortError' : 'SourceRuntimeError';
    this.code = known;
  }
}

export const sourceFault = code => new SourceRuntimeError(code);
export const sourceSafeError = (error, fallback = 'SCRIPT_EXCEPTION') => {
  if (error instanceof SourceRuntimeError) return error;
  if (typeof error?.code === 'string' && Object.hasOwn(messages, error.code)) {
    return sourceFault(error.code);
  }
  if (error?.name === 'AbortError') return sourceFault('CANCELLED');
  return sourceFault(fallback);
};

export function sourcePreprocessError(error) {
  const safe = sourceSafeError(error, 'SCRIPT_PROTOCOL_ERROR');
  if (safe.code !== 'SCRIPT_PROTOCOL_ERROR') return safe;
  // Acorn wraps the engine's overflow as a SyntaxError with a source location.
  // This is a resource failure, unlike an ordinary invalid source program.
  if (error?.name === 'SyntaxError' &&
      /^Not enough stack space to parse input \(\d+:\d+\)$/.test(error.message) ||
      error?.name === 'InternalError' &&
      /stack overflow|out of memory|allocation failed/.test(error.message) ||
      error?.name === 'RangeError' && error.message === 'Maximum call stack size exceeded') {
    return sourceFault('RESOURCE_LIMIT');
  }
  return safe;
}

export function sourceRequire(condition, code = 'INVALID_ARGUMENT') {
  if (!condition) throw sourceFault(code);
}

export function sourceObject(value, code = 'INVALID_ARGUMENT') {
  sourceRequire(value !== null && typeof value === 'object' && !Array.isArray(value), code);
  return value;
}

export function sourceIdentifier(value) {
  sourceRequire(typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(value));
  return value;
}

export function sourceClone(value) {
  try { return structuredClone(value); }
  catch { throw sourceFault('INVALID_ARGUMENT'); }
}
