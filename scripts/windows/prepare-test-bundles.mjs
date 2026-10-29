import { resolve } from 'node:path';
import { mkdir } from 'node:fs/promises';
import { packageWindowsRuntime } from './package-runtime.mjs';
const output = process.argv[2];
if (!output) throw new Error('Explicit Windows test bundle root required');
await mkdir(output, { recursive: true });
await packageWindowsRuntime({ product: 'aggregate', output: resolve(output, 'aggregate') });
