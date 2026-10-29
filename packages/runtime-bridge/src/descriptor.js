import { Socket } from 'node:net';
import { BridgeError, BridgePeer } from './index.js';

/** Attach to a connected native socket. Does not open a TCP listener or choose
 * an endpoint. The embedded runtime receives an AF_UNIX descriptor from Swift.
 */
export function attachConnectedStream(stream, options) {
  if (!stream || typeof stream.write !== 'function' || typeof stream.destroy !== 'function' ||
      typeof stream.on !== 'function') throw new BridgeError('INVALID_MESSAGE');
  const onClose = options?.onClose;
  if (onClose !== undefined && typeof onClose !== 'function') throw new BridgeError('INVALID_MESSAGE');
  const peer = new BridgePeer({
    ...options,
    send: bytes => new Promise((resolve, reject) => {
      if (stream.destroyed) { reject(new BridgeError('CLOSED')); return; }
      stream.write(bytes, error => error ? reject(new BridgeError('CLOSED')) : resolve());
    }),
    close: () => {
      stream.destroy();
      onClose?.();
    }
  });
  stream.on('data', bytes => {
    try { peer.receive(bytes); } catch { peer.close(); }
  });
  stream.on('error', () => peer.close());
  stream.on('end', () => peer.close());
  stream.on('close', () => peer.close());
  return peer;
}

export function createDescriptorPeer(descriptor, options) {
  if (!Number.isSafeInteger(descriptor) || descriptor < 0 || process.platform === 'win32') {
    throw new BridgeError('INVALID_MESSAGE');
  }
  // Native transfers ownership exactly once. Socket.destroy closes it.
  const socket = new Socket({ fd: descriptor, readable: true, writable: true });
  try { return attachConnectedStream(socket, options); }
  catch (error) { socket.destroy(); throw error; }
}
