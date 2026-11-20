import { Writable, PassThrough } from 'node:stream';
import { providerForHost, providerEnabled } from './cat-output.js';
import { PanPlaceholders } from './pan-placeholders.js';
import { fault } from './errors.js';

/** Invoke-scoped capture returns protocol placeholders without provider I/O. */
export class PanInterceptor {
  constructor() { this.placeholders = new PanPlaceholders(); }

  hasFailure(shares) { return this.placeholders.hasFailure(shares); }

  reference(scope, value, label) { return this.placeholders.reference(scope, value, label); }

  close() { this.placeholders.close(); }

  matches(host, scope) {
    const provider = providerForHost(host.toLowerCase().replace(/\.$/, ''));
    return provider && scope?.panShares && scope.settings && providerEnabled(scope.settings, provider);
  }

  rejectConnection(host, scope) {
    if (!this.matches(host, scope)) return;
    this.placeholders.markFailure(scope.panShares);
    throw fault('PAN_INTERCEPTION_UNSUPPORTED');
  }

  request(url, options, scope) {
    if (!this.matches(url.hostname, scope)) return null;
    const provider = providerForHost(url.hostname.toLowerCase().replace(/\.$/, ''));
    const placeholders = this.placeholders;
    class ShareRequest extends Writable {
      constructor() {
        // The request remains owned until its response body finishes. Writable's
        // default auto-destroy would otherwise tear down the placeholder as
        // soon as _final() returns.
        super({ autoDestroy: false });
        this.chunks = []; this.headers = Object.fromEntries(Object.entries(options.headers ?? {})
          .map(([key, value]) => [key.toLowerCase(), value]));
        this.aborted = false; this.method = options.method ?? 'GET'; this.path = options.path ?? url.pathname + url.search;
      }
      setHeader(key, value) { this.headers[key.toLowerCase()] = value; }
      getHeader(key) { return this.headers[key.toLowerCase()]; }
      getHeaders() { return { ...this.headers }; }
      getHeaderNames() { return Object.keys(this.headers); }
      hasHeader(key) { return Object.hasOwn(this.headers, key.toLowerCase()); }
      removeHeader(key) { delete this.headers[key.toLowerCase()]; }
      flushHeaders() {}
      setTimeout(delay, callback) {
        clearTimeout(this.timer);
        this.timer = setTimeout(() => { this.emit('timeout'); callback?.(); }, delay);
        return this;
      }
      setNoDelay() { return this; }
      setSocketKeepAlive() { return this; }
      _write(chunk, encoding, done) { this.chunks.push(Buffer.from(chunk)); done(); }
      trackResponse(response) {
        this.response = response;
        let settled = false;
        const cleanup = () => {
          let failure;
          try { response.off('end', completed); } catch (error) { failure ??= error; }
          try { response.off('close', completed); } catch (error) { failure ??= error; }
          try { response.off('error', failed); } catch (error) { failure ??= error; }
          if (this.responseCleanup === cleanup) this.responseCleanup = null;
          if (failure) throw failure;
        };
        const completed = () => {
          if (settled) return;
          settled = true;
          try { cleanup(); }
          catch (error) { this.destroy(error); return; }
          this.destroy();
        };
        const failed = error => {
          if (settled) return;
          settled = true;
          try { cleanup(); } catch (cleanupError) { error ??= cleanupError; }
          this.destroy(error);
        };
        this.responseCleanup = cleanup;
        try {
          response.once('end', completed);
          response.once('close', completed);
          response.once('error', failed);
        } catch (error) {
          try { cleanup(); } catch (cleanupError) { error ??= cleanupError; }
          throw error;
        }
      }
      _final(done) {
        let body;
        try {
          const bytes = Buffer.concat(this.chunks);
          try { body = bytes.toString(); } finally { bytes.fill(0); }
        } catch (error) { done(error); return; }
        let answer;
        try { answer = placeholders.response(provider, url, body, scope); }
        catch (error) { body = ''; done(error); return; }
        body = '';
        const response = new PassThrough();
        const headers = answer.headers;
        Object.assign(response, { statusCode: 200, statusMessage: 'OK', complete: true,
          headers, rawHeaders: Object.entries(headers).flatMap(([key, value]) =>
            (Array.isArray(value) ? value : [value]).flatMap(item => [key, String(item)])),
          trailers: {}, rawTrailers: [], httpVersion: '1.1', httpVersionMajor: 1,
          httpVersionMinor: 1, aborted: false, req: this });
        this.trackResponse(response);
        queueMicrotask(() => {
          if (this.destroyed) { response.destroy(); return; }
          try { this.emit('response', response); response.end(answer.body); }
          catch (error) { response.destroy(error); }
        });
        done();
      }
      abort() {
        this.aborted = true;
        try { this.emit('abort'); }
        finally { this.destroy(); }
        return this;
      }
      _destroy(error, done) {
        let failure = error;
        const failed = next => { failure ??= next; };
        clearTimeout(this.timer);
        this.timer = null;
        try { this.responseCleanup?.(); } catch (next) { failed(next); }
        const response = this.response;
        this.responseCleanup = null;
        this.response = null;
        try { response?.destroy?.(); } catch (next) { failed(next); }
        for (const chunk of this.chunks) chunk.fill(0);
        this.chunks = [];
        this.headers = {};
        done(failure);
      }
    }
    return new ShareRequest();
  }
}
