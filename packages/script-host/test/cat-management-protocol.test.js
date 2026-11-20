import test from 'node:test';
import assert from 'node:assert/strict';
import { syncCatManagement } from '../src/cat-management.js';

test('Cat website-v1 probes only list root and consumes the selected provider', async () => {
  const calls = [];
  const session = { services: { endpoint: () => ({ origin: 'http://127.0.0.1:1234' }), endpoints: () => [] },
    async management(path, body, method, options) {
      calls.push({ path, body, method, options });
      if (path === '/website/api/credentials') return { statusCode: 404 };
      if (path === '/website/pans/list') return { success: true, data: [{ key: 'quark' }, null, 7] };
      if (path === '/website/quark/cookie') return { success: true, data: method === 'GET' ? '' : true };
      throw new Error('unexpected target');
    } };
  assert.deepEqual(await syncCatManagement(session, { provider: 'quark', credentials: { cookie: 'fixture=value' } }),
    { status: 'saved', protocol: 'website-v1' });
  assert.equal(calls.at(-1).options.requireWrite, true);
  assert.equal(calls.at(-1).method, 'PUT');
});
