import { createRequire } from 'node:module';

const { Request, Response, Headers, FormData } = process.platform === 'ios'
  ? createRequire(import.meta.url)('../build/Release/cat-web-values.cjs')
  : globalThis;

export { Request, Response, Headers, FormData };
