import { SourceRuntimeError, SourceSession } from '@meowfilm/source-runtime';
import { fault, requireValue, safeError } from './errors.js';
import { PortableSandbox } from './portable-sandbox.js';
import { WorkerSession } from './worker-session.js';

function hostError(error) {
  if (error instanceof SourceRuntimeError) return fault(error.code);
  return safeError(error);
}

export class PortableSourceSession {
  constructor({
    installationId,
    generation,
    revision,
    capabilities,
    metadata,
    onDiagnostic,
    onFailure,
    limits,
    wasmModule,
  }) {
    Object.assign(this, {
      installationId, generation, revision, capabilitiesClient: capabilities,
      metadata, onDiagnostic, onFailure, limits, wasmModule,
    });
    this.live = true;
    this.activated = false;
    this.capabilities = {};
    this.alert = null;
  }

  async start() {
    try {
      this.session = new SourceSession({
        installationId: this.installationId,
        generation: this.generation,
        source: this.revision.source,
        protocol: this.revision.protocol,
        capabilities: this.capabilitiesClient,
        metadata: {
          ...this.metadata,
          ...this.revision.metadata,
          adapterVersion: this.revision.version,
        },
        limits: {
          maximumBytes: this.limits.maxMemoryBytes,
          requestTimeoutMs: this.limits.requestTimeoutMs,
          cleanupTimeoutMs: this.limits.cleanupTimeoutMs,
        },
        onDiagnostic: event => {
          if (!this.onDiagnostic) return;
          this.onDiagnostic({
            installationId: this.installationId,
            generation: this.generation,
            protocol: this.revision.protocol,
            ...event,
          });
        },
        createSandbox: options => PortableSandbox.create({
          ...options,
          limits: this.limits,
          wasmModule: this.wasmModule,
          onFailure: error => this.fail(error),
        }),
      });
      const description = await this.session.start();
      this.capabilities = {
        source: {
          protocol: description.protocol,
          importedProtocol: description.importedProtocol,
          actions: description.actions,
        },
      };
      return this;
    } catch (error) {
      if (error.memoryLimitExceeded) this.fail(error);
      throw hostError(error);
    }
  }

  activate() {
    requireValue(this.live && this.session, 'STALE_SESSION');
    this.session.activate();
    this.activated = true;
  }

  async invoke(request, options) {
    requireValue(this.live && this.activated, 'STALE_SESSION');
    requireValue(request?.siteId === 'source');
    try {
      return await this.session.invoke(request.operation, request.payload ?? {}, options);
    } catch (error) {
      if (error.memoryLimitExceeded) this.fail(error);
      throw hostError(error);
    }
  }

  fail(error) {
    if (!this.live) return;
    const ready = this.activated;
    this.memoryLimitExceeded = Boolean(error.memoryLimitExceeded);
    this.failure = hostError(error);
    if (this.memoryLimitExceeded) this.failure.memoryLimitExceeded = true;
    this.stop().catch(() => {});
    try { this.onFailure?.(this.failure, { ready }); } catch {}
  }

  stop() {
    if (this.stopping) return this.stopping;
    this.live = false;
    this.activated = false;
    this.stopping = Promise.resolve().then(() => this.session?.close()).catch(error => {
      const failure = hostError(error);
      this.cleanupError = failure.code;
      throw failure;
    });
    this.stopping.catch(() => {});
    return this.stopping;
  }

  async drainAdapters() {
    try { await this.session?.drain(); }
    catch (error) { this.cleanupError = hostError(error).code; }
  }

  whenAdaptersIdle() {
    return this.session?.drain() ?? Promise.resolve();
  }

  stats() {
    return {
      ...this.session?.stats(),
      live: this.live,
      cleanupError: this.cleanupError ?? null,
    };
  }
}

export function createPortableSourceAdapter({
  capabilities,
  metadata = {},
} = {}) {
  requireValue(capabilities && typeof capabilities.call === 'function');
  requireValue(metadata && typeof metadata === 'object' && !Array.isArray(metadata));
  return input => new WorkerSession({
    ...input,
    protocol: 'source',
    capabilitiesClient: {
      async call(...args) {
        try { return await capabilities.call(...args); }
        catch (error) { throw hostError(error); }
      },
    },
    startParams: { sourceMetadata: metadata },
  });
}
