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
