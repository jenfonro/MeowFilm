const codes = new Set([
  'INVALID_REQUEST', 'UNSUPPORTED_OPERATION', 'NOT_CONFIGURED', 'AUTH_REQUIRED',
  'AUTH_FAILED', 'CHALLENGE_REQUIRED', 'NOT_FOUND', 'UPSTREAM_HTTP',
  'UPSTREAM_FORMAT', 'UPSTREAM_ERROR', 'NETWORK_ERROR', 'TIMEOUT', 'CANCELLED',
  'CLOSED', 'STALE_ACCOUNT', 'STORAGE_ERROR', 'CORRUPT_STORAGE', 'LOGIN_EXPIRED',
  'PAN_FAILURE',
]);

export class PanError extends Error {
  constructor(code) {
    super(codes.has(code) ? code : 'PAN_FAILURE');
    this.name = 'PanError';
    this.code = this.message;
  }
}

export function requireValue(condition, code = 'INVALID_REQUEST') {
  if (!condition) throw new PanError(code);
}
