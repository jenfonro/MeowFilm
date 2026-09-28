import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

const ASSET_VERSION_PLACEHOLDER = '__ASSET_VERSION__';
const ASSET_VERSION_PARAM = `v=${ASSET_VERSION_PLACEHOLDER}`;

function withAssetVersion(url) {
  if (!url) return url;
  if (url.includes(ASSET_VERSION_PARAM)) return url;
  if (/^(https?:)?\/\//.test(url) || url.startsWith('data:')) return url;
  if (!/\.(css|js)(\?|#|$)/.test(url)) return url;
  const joiner = url.includes('?') ? '&' : '?';
  return `${url}${joiner}${ASSET_VERSION_PARAM}`;
}

function addAssetVersionPlugin() {
  return {
    name: 'add-asset-version',
    transformIndexHtml: {
      order: 'post',
      handler(html) {
        return html.replace(/(href|src)=([\"'])([^\"']+)\2/g, (match, attr, quote, url) => {
          const updated = withAssetVersion(url);
          return updated === url ? match : `${attr}=${quote}${updated}${quote}`;
        });
      }
    }
  };
}

export default defineConfig({
  plugins: [vue(), addAssetVersionPlugin()],
  build: {
    outDir: resolve(__dirname, 'dist'),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(__dirname, 'index.html'),
        dashboard: resolve(__dirname, 'dashboard.html')
      }
    }
  }
});
