import test from 'node:test';
import assert from 'node:assert/strict';
import { verifyNativeJavaScript } from '../scripts/verify-native-javascript.mjs';

test('Native JavaScript engine uses the pinned source and license inventory', () => {
  const result = verifyNativeJavaScript();
  assert.equal(result.project, 'quickjs-ng/quickjs');
  assert.ok(result.files > 0);
});
