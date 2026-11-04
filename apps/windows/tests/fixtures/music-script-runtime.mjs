import { startWindowsAppRuntime } from '../../runtime/bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';

await startWindowsAppRuntime({
  product: 'music',
  argv: process.argv.slice(2),
  assemble: ({ call }) => [createMusicScriptModule({ call })],
});
