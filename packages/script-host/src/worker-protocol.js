import { fault, safeError } from './errors.js';

export const WORKER_PROTOCOL_VERSION = 1;

const requestMethods = new Set(['start', 'activate', 'invoke', 'stop',
  'stats', 'probe', 'network', 'management.detect', 'management.sync',
  'enhanceM3U8']);
const eventNames = new Set(['activated', 'endpoints', 'capabilities', 'alert',
  'failure', 'diagnostic', 'scriptError', 'network', 'stopped', 'stats', 'settings']);

export function assertMethod(value) {
  if (!requestMethods.has(value)) throw fault('UNSUPPORTED_OPTION');
  return value;
}

export function assertEvent(value) {
  if (!eventNames.has(value)) throw fault('UNSUPPORTED_OPTION');
  return value;
}

export function request(id, method, params = null) {
  if (!Number.isSafeInteger(id) || id < 1) throw fault('INVALID_ARGUMENT');
  return { version: WORKER_PROTOCOL_VERSION, kind: 'request', id, method: assertMethod(method), params };
}

export function cancel(id, code = 'CANCELLED') {
  if (!Number.isSafeInteger(id) || id < 1) throw fault('INVALID_ARGUMENT');
  return { version: WORKER_PROTOCOL_VERSION, kind: 'cancel', id, code };
}

export function response(id, value = null) {
  if (!Number.isSafeInteger(id) || id < 1) throw fault('INVALID_ARGUMENT');
  return { version: WORKER_PROTOCOL_VERSION, kind: 'response', id, ok: true, value };
}

export function failure(id, error) {
  if (!Number.isSafeInteger(id) || id < 1) throw fault('INVALID_ARGUMENT');
  const safe = safeError(error);
  return { version: WORKER_PROTOCOL_VERSION, kind: 'response', id, ok: false, code: safe.code,
    ...(Number.isInteger(safe.statusCode) ? { statusCode: safe.statusCode } : {}) };
}

export function event(name, value = null) {
  return { version: WORKER_PROTOCOL_VERSION, kind: 'event', event: assertEvent(name), value };
}

export function validateMessage(message) {
  if (!message || typeof message !== 'object' || Array.isArray(message) ||
    message.version !== WORKER_PROTOCOL_VERSION || typeof message.kind !== 'string') {
    throw fault('SCRIPT_PROTOCOL_ERROR');
  }
  if (message.kind === 'request') {
    if (!Number.isSafeInteger(message.id) || message.id < 1) throw fault('SCRIPT_PROTOCOL_ERROR');
    assertMethod(message.method);
    return message;
  }
  if (message.kind === 'cancel') {
    if (!Number.isSafeInteger(message.id) || message.id < 1 || typeof message.code !== 'string') {
      throw fault('SCRIPT_PROTOCOL_ERROR');
    }
    return message;
  }
  if (message.kind === 'response') {
    if (!Number.isSafeInteger(message.id) || message.id < 1 || typeof message.ok !== 'boolean') {
      throw fault('SCRIPT_PROTOCOL_ERROR');
    }
    if (!message.ok && typeof message.code !== 'string') throw fault('SCRIPT_PROTOCOL_ERROR');
    return message;
  }
  if (message.kind === 'event') {
    assertEvent(message.event);
    return message;
  }
  throw fault('SCRIPT_PROTOCOL_ERROR');
}

export function errorFromResponse(message) {
  const error = fault(message.code);
  if (Number.isInteger(message.statusCode)) error.statusCode = message.statusCode;
  return error;
}
