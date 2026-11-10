import { parentPort } from 'node:worker_threads';
import { createWatchman } from './watchman.js';

const sdk = createWatchman();
parentPort.on('message', async ({ id }) => {
  try { parentPort.postMessage({ id, token: await sdk.get() }); }
  catch (error) { parentPort.postMessage({ id, code: error.code }); }
});
