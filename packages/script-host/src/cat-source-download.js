import { createNodeNetwork } from './node-network.js';
import { catSourceURL } from './cat-settings.js';
import { requireValue } from './errors.js';
import { ResourceScope } from './scope.js';

/** Management downloads use the existing HTTP transport and source byte bound,
 * including its redirect/decompression handling, without a domain allowlist. */
export async function downloadCatSource(url, { signal, maxSourceBytes, timeoutMs }) {
  catSourceURL(url);
  const scope = new ResourceScope(signal, timeoutMs);
  try {
    scope.assertOpen();
    const response = await createNodeNetwork({ maxResponseBytes: maxSourceBytes })({
      url, options: {}, signal: scope.signal, validateURL: catSourceURL,
    });
    scope.assertOpen();
    requireValue(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
    return response.body;
  } finally { scope.close(); }
}
