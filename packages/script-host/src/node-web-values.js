import { loadCatWebValues } from './native-loader.js';

const { Request, Response, Headers, FormData } = loadCatWebValues();

export { Request, Response, Headers, FormData };
