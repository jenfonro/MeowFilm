import { Writable, PassThrough } from 'node:stream';
import { parseShare, providerForHost, shareIdentity } from './cat-output.js';

const valid = value => typeof value === 'string' && /^[A-Za-z0-9_-]{4,256}$/.test(value);
const bases = { baidu: 'https://pan.baidu.com/s/1', quark: 'https://pan.quark.cn/s/',
  uc: 'https://drive.uc.cn/s/', tianyi: 'https://cloud.189.cn/t/', yidong: 'https://caiyun.139.com/m/i?' };
function fields(url, body) {
  const result = Object.fromEntries(url.searchParams);
  try {
    const value = JSON.parse(body);
    Object.assign(result, value, value?.data, value?.body, value?.params);
  } catch { Object.assign(result, Object.fromEntries(new URLSearchParams(body))); }
  return result;
}

/** Only proven share/list flows are intercepted. This is not a provider SDK:
 * no login, token refresh, transfer, direct-link extraction or credentials. */
export class PanInterceptor {
  request(url, options, scope, open, own = value => value) {
    const provider = providerForHost(url.hostname);
    if (!provider || !scope.panShares || !scope.settings?.providers[provider]) return null;
    const path = url.pathname;
    const stage = ['quark', 'uc'].includes(provider)
      ? /\/share\/sharepage\/token$/.test(path) ? 'token' : /\/share\/sharepage\/detail$/.test(path) ? 'list' : null
      : provider === 'baidu' ? /\/share\/verify$/.test(path) ? 'token' : /\/share\/list$/.test(path) ? 'list' : null
      : provider === 'tianyi' ? /getShareInfoByCode\.action$/.test(path) ? 'token'
        : /listShareDir\.action$/.test(path) ? 'list' : null
      : /(?:getOutLinkInfo|queryOutLinkInfo)$/.test(path) ? 'token'
        : /(?:getOutLinkFileList|queryOutLinkFileList)$/.test(path) ? 'list' : null;
    if (!stage) return null;
    class ShareRequest extends Writable {
      constructor() {
        // The request remains owned until its response body finishes. Writable's
        // default auto-destroy would otherwise tear down a replayed request as
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
        this.forwardResponse = response;
        let settled = false;
        const cleanup = () => {
          let failure;
          try { response.off('end', completed); } catch (error) { failure ??= error; }
          try { response.off('close', completed); } catch (error) { failure ??= error; }
          try { response.off('error', failed); } catch (error) { failure ??= error; }
          if (this.forwardResponseCleanup === cleanup) this.forwardResponseCleanup = null;
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
        this.forwardResponseCleanup = cleanup;
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
        const data = fields(url, body);
        body = '';
        const identity = ['pwd_id', 'surl', 'shorturl', 'shareCode', 'linkID', 'linkId']
          .find(key => Object.hasOwn(data, key));
        const id = identity === undefined ? undefined : data[identity];
        const password = data.passcode ?? data.pwd ?? data.accessCode ?? data.passwd;
        let share = valid(id) ? parseShare(bases[provider] + id,
          String(password ?? '')) : null;
        if (share && stage === 'list') {
          const known = scope.panShares.get(provider + ':' + share.shareId);
          if (!known) share = null;
          else if (password === undefined) share.password = known.password;
        }
        if (!share && stage === 'list' && identity === undefined) {
          const known = [...scope.panShares.values()].filter(value => value.provider === provider);
          if (known.length === 1) share = { ...known[0],
            ...(password === undefined ? {} : { password: String(password) }) };
        }
        // A recognized API with an opaque/unknown share cannot be fabricated.
        // Replay is safe here: no network request has been sent yet.
        if (!share) {
          let client;
          try { client = own(open({ ...options, headers: this.headers })); }
          catch (error) { done(); this.destroy(error); return; }
          this.forward = client;
          const responded = response => {
            this.forwardedResponse = true;
            try {
              this.trackResponse(response);
              this.emit('response', response);
            } catch (error) {
              try { response.destroy(); } catch {}
              this.destroy(error);
            }
          };
          const failed = error => this.destroy(error);
          const closed = () => {
            if (!this.destroyed && !this.forwardedResponse) this.destroy();
          };
          this.forwardCleanup = () => {
            let failure;
            try { client.off('response', responded); } catch (error) { failure ??= error; }
            try { client.off('error', failed); } catch (error) { failure ??= error; }
            try { client.off('close', closed); } catch (error) { failure ??= error; }
            if (failure) throw failure;
          };
          try {
            client.once('response', responded);
            client.once('error', failed);
            client.once('close', closed);
            for (const chunk of this.chunks) client.write(chunk);
            client.end();
          } catch (error) {
            try { this.forwardCleanup(); } catch {}
            try { client.destroy(); } catch {}
            done(); this.destroy(error); return;
          }
          done(); return;
        }
        scope.panShares.set(provider + ':' + share.shareId, share);
        const token = 'meow_' + shareIdentity(share);
        const file = { fid: token, file_id: token, fileId: token, file_name: '网盘内容.mp4',
          fileName: '网盘内容.mp4', name: '网盘内容.mp4', file_type: 1, category: 1,
          file: true, dir: false, pdir_fid: '0', share_fid_token: token, size: 1 };
        const answer = ['quark', 'uc'].includes(provider)
          ? stage === 'token'
            ? { status: 200, code: 0, data: { stoken: token, share: { title: '网盘内容' } } }
            : { status: 200, code: 0, metadata: { _total: 1 },
              data: { list: [file], metadata: { _total: 1 }, share: { title: '网盘内容' } } }
          : provider === 'baidu' ? stage === 'token'
            ? { errno: 0, randsk: token } : { errno: 0, list: [{ ...file, fs_id: token,
              server_filename: file.file_name, isdir: 0 }], has_more: 0 }
          : provider === 'tianyi' ? stage === 'token'
            ? { res_code: 0, shareId: token, shareCode: share.shareId, shareMode: 1,
              isFolder: true, needAccessCode: 0 }
            : { res_code: 0, fileListAO: { fileList: [file], folderList: [], count: 1 } }
          : { code: '0000', message: 'success', data: stage === 'token'
            ? { linkID: share.shareId, fileList: [file], shareName: '网盘内容' }
            : { fileList: [file], folderList: [], total: 1 } };
        const response = new PassThrough();
        const headers = { 'content-type': 'application/json; charset=utf-8' };
        Object.assign(response, { statusCode: 200, statusMessage: 'OK', complete: true,
          headers, rawHeaders: Object.entries(headers).flatMap(([key, value]) => [key, value]),
          trailers: {}, rawTrailers: [], httpVersion: '1.1', httpVersionMajor: 1,
          httpVersionMinor: 1, aborted: false, req: this });
        this.trackResponse(response);
        queueMicrotask(() => {
          if (this.destroyed) { response.destroy(); return; }
          try { this.emit('response', response); response.end(JSON.stringify(answer)); }
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
        try { this.forwardCleanup?.(); } catch (next) { failed(next); }
        try { this.forwardResponseCleanup?.(); } catch (next) { failed(next); }
        const response = this.forwardResponse, forward = this.forward;
        this.forwardCleanup = null;
        this.forwardResponseCleanup = null;
        this.forwardResponse = null;
        this.forward = null;
        try { response?.destroy?.(); } catch (next) { failed(next); }
        try { forward?.destroy(); } catch (next) { failed(next); }
        for (const chunk of this.chunks) chunk.fill(0);
        this.chunks = [];
        this.headers = {};
        done(failure);
      }
    }
    return new ShareRequest();
  }
}
