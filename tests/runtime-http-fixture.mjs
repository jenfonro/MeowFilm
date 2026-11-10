import { randomUUID } from 'node:crypto';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';

/** A real local HTTP exchange for public module tests; no platform launcher. */
export function moduleHttp(t, module) {
  const sessionId = randomUUID();
  const runtime = createAppRuntime({ sessionId, modules: [module] });
  const ready = runtime.start().then(() => createRuntimeClient({ baseUrl: runtime.baseUrl }));
  ready.catch(() => {});
  t.after(async () => {
    const client = await ready;
    await client.close();
    await runtime.close().catch(() => {});
  });
  const prefix = module.id === 'source' ? 'source.runtime.' : `${module.id}.script.`;
  const rpc = async (name, params = {}, options) => {
    try { return await (await ready).call(prefix + name, params, options); }
    catch (error) {
      if (['CANCELLED', 'TIMEOUT', 'CLOSED'].includes(error.code)) return { ok: false, contract: module.contract, code: error.code };
      throw error;
    }
  };
  rpc.upload = async (id, revision, bytes, options) => (await ready).upload(module.id, id, revision, bytes, options);
  rpc.transport = { ready, runtime, identity: { sessionId } };
  return rpc;
}
