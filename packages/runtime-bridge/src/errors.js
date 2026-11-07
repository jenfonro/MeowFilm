export const HTTP_PROTOCOL = 'runtime.http.v1';
const codes = new Set(['INVALID_REQUEST', 'METHOD_NOT_FOUND', 'PERMISSION_DENIED',
  'CLOSED', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']);

export class BridgeError extends Error {
  constructor(code) {
    super(codes.has(code) ? code : 'INVALID_RESPONSE');
    this.name = 'BridgeError';
    this.code = this.message;
  }
}
