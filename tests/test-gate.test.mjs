import test from 'node:test';
import assert from 'node:assert/strict';
import { checkTestSummary } from '../scripts/run-tests.mjs';

const good = { tests: 5, pass: 5, fail: 0, cancelled: 0, skipped: 0, todo: 0 };
const tap = values => Object.entries(values).map(([key, value]) => `# ${key} ${value}`).join('\n');
test('gate admits only a nonempty completely passing test summary', () => {
  assert.equal(checkTestSummary(tap(good), 'fixture'), 5);
  assert.equal(checkTestSummary(tap(good).replaceAll('\n', '\r\n'), 'fixture'), 5);
});
test('missing empty skipped cancelled and todo suites fail explicitly', () => {
  for (const values of [{}, { ...good, tests: 0, pass: 0 }, { ...good, pass: 4 },
    ...['fail', 'cancelled', 'skipped', 'todo'].map(key => ({ ...good, [key]: 1 }))]) {
    assert.throws(() => checkTestSummary(tap(values), 'fixture'));
  }
  for (const field of Object.keys(good)) {
    const values = { ...good }; delete values[field];
    assert.throws(() => checkTestSummary(tap(values), 'fixture'));
  }
});
