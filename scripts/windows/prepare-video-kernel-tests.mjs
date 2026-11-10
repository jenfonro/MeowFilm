import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { kernelCache, sha256 } from './prepare-video-kernel.mjs';

// Public FFmpeg FATE inputs, not shipped application assets or real user keys.
const fixtures = [
  ['main10.bit', 'hevc-conformance/DBLK_A_MAIN10_VIXS_3.bit',
    '96c4f1a6b824cc51433d87ebc11bffcc6c2fe3de3ed18339316803e8bbc9be17'],
  ['cenc.mp4', 'mov/mov-frag-encrypted.mp4',
    'c9604942731afc2d8b6eacf8e016fa7d1b35860b89f0e86d54dc528dfbd97b6e'],
];
await mkdir(join(kernelCache, 'fixtures'), { recursive: true });
for (const [name, source, hash] of fixtures) {
  const path = join(kernelCache, 'fixtures', name);
  if (!existsSync(path)) {
    const response = await fetch('https://fate-suite.ffmpeg.org/' + source);
    if (!response.ok) throw new Error(`FATE input download failed: HTTP ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (sha256(bytes) !== hash) throw new Error('Locked FATE input hash mismatch');
    await writeFile(path, bytes);
  }
  if (sha256(await readFile(path)) !== hash) throw new Error('Locked FATE input hash mismatch');
  console.log(`fixture=${name}; sha256=${hash}`);
}
