export const musicPlatformCodes = new Set([
  'INVALID_REQUEST', 'UNSUPPORTED', 'CLOSED', 'CANCELLED', 'TIMEOUT', 'LIMIT',
  'NETWORK_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_ERROR', 'INVALID_RESPONSE',
  'AUTH_REQUIRED', 'AUTH_EXPIRED', 'INVALID_CREDENTIALS', 'FORBIDDEN',
  'RATE_LIMITED', 'VERIFICATION_REQUIRED', 'SIGNATURE_REQUIRED',
  'DEVICE_CONTEXT_REQUIRED', 'STALE_SESSION', 'NOT_FOUND', 'MEDIA_UNAVAILABLE',
  'TRIAL_ONLY', 'STORAGE_ERROR', 'CORRUPT_STORAGE', 'COMMIT_UNKNOWN',
  'REMOTE_CHANGED', 'CLOUD_RESPONSE_UNCONFIRMED',
  'QUALITY_UNAVAILABLE', 'CREDENTIALS_UNAVAILABLE', 'STORAGE_CONFLICT', 'REDIRECT_DENIED',
  'BUSY', 'QR_EXPIRED', 'SMS_SEND_UNCONFIRMED', 'DEVICE_LIMIT', 'INVALID_CODE',
  'ACCOUNT_SELECTION_REQUIRED', 'INVALID_PHONE',
]);

export class MusicPlatformError extends Error {
  constructor(code, retryAfterSeconds = 0) {
    super(code);
    this.name = 'MusicPlatformError';
    this.code = code;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}
export const fail = code => { throw new MusicPlatformError(code); };
export function errorCode(error) {
  if (error instanceof MusicPlatformError) return error.code;
  if (error?.name === 'AbortError') return 'CANCELLED';
  return 'INVALID_RESPONSE';
}
