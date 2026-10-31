import test from 'node:test';
import assert from 'node:assert/strict';
import { isPublicAddress } from '../src/network-boundary.js';

test('IPv6 public-address policy rejects special-use and documentation ranges', () => {
  for (const address of [
    '2001::1',
    '2001:2::1',
    '2001:10::1',
    '2001:db8::1',
    '3fff::1',
  ]) assert.equal(isPublicAddress(address), false, address);

  for (const address of [
    '2001:4860:4860::8888',
    '2606:4700:4700::1111',
    '::ffff:8.8.8.8',
  ]) assert.equal(isPublicAddress(address), true, address);

  assert.equal(isPublicAddress('::ffff:10.0.0.1'), false);
});
