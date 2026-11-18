import { createSourceDownload } from './source-download.js';
import { catSourceURL } from './cat-settings.js';
import { requireValue } from './errors.js';
import { ResourceScope } from './scope.js';

/** Cat source downloads retain their redirect and encoding contract. */
export async function downloadCatSource(url, { signal, timeoutMs }) {
  catSourceURL(url);
  const scope = new ResourceScope(signal, timeoutMs);
  try {
    scope.assertOpen();
    const response = await createSourceDownload()({
      url, options: {}, signal: scope.signal, validateURL: catSourceURL,
    });
    scope.assertOpen();
    requireValue(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
    return response.body;
  } finally { scope.close(); }
}
