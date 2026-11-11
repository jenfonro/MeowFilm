// SPDX-License-Identifier: GPL-3.0-only
// Watchman Web protocol: NeteaseCloudMusicApiEnhanced v4.41.1 register_checktoken_v2 (MIT).
import { JSDOM, VirtualConsole } from 'jsdom';
import https from 'node:https';
import { validToken } from './index.js';

const toolURL = 'https://acstatic-dun.126.net/tool.min.js';
const productNumber = 'YD00000558929251';
const businessId = 'bd5d2f973ef74cd2a61325a412ae54d9';
const failure = code => Object.assign(new Error(code), { code });

// Embedded Node 18's fetch depends on WASM. Use its native HTTP transport instead.
function downloadTool() {
  return new Promise((resolve, reject) => {
    const request = https.get(toolURL, { signal: AbortSignal.timeout(10_000) }, response => {
      response.on('error', reject);
      if (response.statusCode < 200 || response.statusCode >= 300) {
        response.resume(); reject(failure('UPSTREAM_HTTP')); return;
      }
      let code = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { code += chunk; });
      response.on('end', () => resolve(code));
    });
    request.on('error', reject);
  });
}

/** Constructed only inside the disposable worker; JSDOM is not a security sandbox. */
export function createWatchman({ loadTool, createDOM } = {}) {
  let dom, instance;
  async function initialize() {
    if (instance) return instance;
    let code;
    try {
      code = await (loadTool ? loadTool() : downloadTool());
    } catch (error) {
      throw failure(error.code === 'UPSTREAM_HTTP' ? 'UPSTREAM_HTTP'
        : error.name === 'TimeoutError' || error.name === 'AbortError' ? 'TIMEOUT' : 'NETWORK_ERROR');
    }
    if (typeof code !== 'string' || !code.trim()) throw failure('UPSTREAM_FORMAT');
    const options = {
      url: 'https://music.163.com/', referrer: 'https://music.163.com/',
      runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true,
      virtualConsole: new VirtualConsole(),
    };
    dom = createDOM ? createDOM(options) : new JSDOM('<!doctype html><html><head></head><body></body></html>', options);
    const script = dom.window.document.createElement('script');
    script.textContent = code;
    dom.window.document.body.appendChild(script);
    instance = await new Promise((resolve, reject) => {
      dom.window.initWatchman({ auto: true, productNumber,
        onload: resolve, onerror: () => reject(failure('SDK_FAILURE')) });
    });
    return instance;
  }
  return {
    async get() {
      try {
        const sdk = await initialize();
        // Both callbacks must complete. The worker owner enforces the whole-request deadline.
        await new Promise(resolve => sdk.getInstance().I(resolve));
        const token = await new Promise(resolve => sdk.getToken(businessId, resolve));
        if (!validToken(token)) throw failure('UPSTREAM_FORMAT');
        return token;
      } catch (error) {
        dom?.window.close(); dom = undefined; instance = undefined;
        throw failure(['TIMEOUT', 'NETWORK_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT'].includes(error.code)
          ? error.code : 'SDK_FAILURE');
      }
    },
    close() { dom?.window.close(); dom = undefined; instance = undefined; },
  };
}
