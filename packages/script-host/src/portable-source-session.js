import { SourceRuntimeError, SourceSession } from '@meowfilm/source-runtime';
import { fault, requireValue, safeError } from './errors.js';
import { PortableSandbox } from './portable-sandbox.js';

function hostError(error) {
  if (error instanceof SourceRuntimeError) return fault(error.code);
  return safeError(error);
}

class PortableSourceSession {
  constructor({
    installationId,
    generation,
    revision,
    capabilities,
    metadata,
    onDiagnostic,
    limits,
  }) {
    Object.assign(this, {
      installationId, generation, revision, capabilitiesClient: capabilities,
      metadata, onDiagnostic, limits,
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
          maximumBytes: this.limits.maxBytes,
          requestTimeoutMs: this.limits.requestTimeoutMs,
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
      this.live = false;
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
    } catch (error) { throw hostError(error); }
  }

  async stop() {
    if (!this.live) return;
    this.live = false;
    this.activated = false;
    try { await this.session?.close(); }
    catch (error) { this.cleanupError = hostError(error).code; }
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
  return input => new PortableSourceSession({
    ...input,
    capabilities,
    metadata,
  });
}
