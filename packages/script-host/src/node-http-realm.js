/** Executed in the guest. Framework injection needs local HTTP messages, not
 * native response handles or a second listening socket. */
export function createHttpMessageClasses(streams, statusCodes) {
  const states = new WeakMap();
  const fail = code => { const error = new Error(code); error.code = code; throw error; };
  const state = response => states.get(response) ?? fail('ERR_INVALID_THIS');
  const nameKey = name => {
    if (typeof name !== 'string' || !/^[!#$%&'*+.^_`|~\w-]+$/.test(name))
      fail('ERR_INVALID_HTTP_TOKEN');
    return name.toLowerCase();
  };
  const validateValue = value => {
    if (value === undefined) fail('ERR_HTTP_INVALID_HEADER_VALUE');
    for (const item of Array.isArray(value) ? value : [value])
      if (/[^\t\x20-\xff]/.test(String(item))) fail('ERR_INVALID_CHAR');
  };
  const mutable = response => {
    const current = state(response);
    if (response._header) fail('ERR_HTTP_HEADERS_SENT');
    return current;
  };
  function IncomingMessage(socket) {
    streams.Readable.call(this);
    this.socket = this.connection = socket;
    this.headers = {};
    this.rawHeaders = [];
    this.trailers = {};
    this.rawTrailers = [];
    this.httpVersion = '1.1';
    this.httpVersionMajor = 1;
    this.httpVersionMinor = 1;
    this.complete = this.aborted = false;
    this.method = this.url = '';
  }
  Object.setPrototypeOf(IncomingMessage.prototype, streams.Readable.prototype);
  IncomingMessage.prototype.setTimeout = function(ms, callback) {
    if (callback !== undefined) this.once('timeout', callback);
    this.socket?.setTimeout?.(ms);
    return this;
  };
  function ServerResponse(request) {
    streams.Writable.call(this, { autoDestroy: false });
    states.set(this, { headers: new Map(), removed: new Set() });
    this.req = request;
    this.socket = null;
    this.statusCode = 200;
    this.statusMessage = undefined;
    this.sendDate = true;
    this.finished = false;
    this._header = null;
    this._headerSent = this._sent100 = false;
    this._hasBody = request?.method !== 'HEAD';
    this.shouldKeepAlive = true;
    this.useChunkedEncodingByDefault = true;
  }
  Object.setPrototypeOf(ServerResponse.prototype, streams.Writable.prototype);
  Object.defineProperties(ServerResponse.prototype, {
    headersSent: { get() { return Boolean(this._header); } },
    connection: { get() { return this.socket; }, set(value) { this.socket = value; } },
  });
  Object.assign(ServerResponse.prototype, {
    setHeader(name, value) {
      const current = mutable(this), key = nameKey(name);
      validateValue(value);
      current.headers.set(key, { name, value });
      current.removed.delete(key);
      return this;
    },
    appendHeader(name, value) {
      const current = mutable(this), key = nameKey(name);
      validateValue(value);
      const previous = current.headers.get(key);
      return this.setHeader(previous?.name ?? name,
        previous ? [].concat(previous.value, value) : value);
    },
    getHeader(name) { return state(this).headers.get(nameKey(name))?.value; },
    getHeaders() {
      return Object.assign(Object.create(null), Object.fromEntries(
        [...state(this).headers].map(([key, entry]) => [key, entry.value])));
    },
    getHeaderNames() { return [...state(this).headers.keys()]; },
    getRawHeaderNames() { return [...state(this).headers.values()].map(entry => entry.name); },
    hasHeader(name) { return state(this).headers.has(nameKey(name)); },
    removeHeader(name) {
      const current = mutable(this), key = nameKey(name);
      current.headers.delete(key);
      current.removed.add(key);
    },
    writeHead(code, message, headers) {
      const current = mutable(this);
      if (!Number.isInteger(Number(code)) || Number(code) < 100 || Number(code) > 999)
        fail('ERR_HTTP_INVALID_STATUS_CODE');
      if (typeof message !== 'string') { headers = message; message = undefined; }
      const reason = message ?? this.statusMessage ?? statusCodes[code] ?? 'unknown';
      if (/[^\t\x20-\xff]/.test(reason)) fail('ERR_INVALID_CHAR');
      if (Array.isArray(headers)) {
        if (headers.length % 2) fail('ERR_INVALID_ARG_VALUE');
        const seen = new Set();
        for (let index = 0; index < headers.length; index += 2) {
          const key = nameKey(headers[index]);
          if (seen.has(key)) this.appendHeader(headers[index], headers[index + 1]);
          else this.setHeader(headers[index], headers[index + 1]);
          seen.add(key);
        }
      } else if (headers) {
        for (const [key, value] of Object.entries(headers)) this.setHeader(key, value);
      }
      this.statusCode = Number(code);
      this.statusMessage = reason;
      if (this.statusCode === 204 || this.statusCode === 304 ||
        this.statusCode >= 100 && this.statusCode < 200) this._hasBody = false;
      const entries = [...current.headers.values()].map(({ name, value }) => [name, value]);
      if (this.sendDate && !current.headers.has('date') && !current.removed.has('date'))
        entries.push(['Date', new Date().toUTCString()]);
      if (!current.headers.has('connection') && !current.removed.has('connection'))
        entries.push(['Connection', this.shouldKeepAlive ? 'keep-alive' : 'close']);
      if (this._hasBody && this.useChunkedEncodingByDefault &&
        !current.headers.has('content-length') && !current.headers.has('transfer-encoding') &&
        !current.removed.has('transfer-encoding')) entries.push(['Transfer-Encoding', 'chunked']);
      this._header = `HTTP/1.1 ${this.statusCode} ${reason}\r\n` +
        entries.flatMap(([name, value]) => [].concat(value).map(item => `${name}: ${item}\r\n`))
          .join('') + '\r\n';
      return this;
    },
    _implicitHeader() { this.writeHead(this.statusCode); },
    flushHeaders() {
      state(this);
      if (!this._header) this._implicitHeader();
      this._headerSent = true;
    },
    _write(chunk, encoding, callback) {
      try {
        this.flushHeaders();
        if (this._hasBody && this.socket) this.socket.write(chunk, encoding, callback);
        else callback();
      } catch (error) { callback(error); }
    },
    _final(callback) {
      try {
        this.flushHeaders();
        this.finished = true;
        callback();
      } catch (error) { callback(error); }
    },
    end(...args) {
      const result = streams.Writable.prototype.end.apply(this, args);
      this.finished = true;
      return result;
    },
    _destroy(error, callback) {
      const socket = this.socket;
      try {
        if (socket) {
          this.detachSocket(socket);
          socket.destroy?.();
        }
        callback(error);
      } catch (failure) { callback(failure); }
    },
    assignSocket(socket) {
      state(this);
      if (this.socket) fail('ERR_HTTP_SOCKET_ASSIGNED');
      this.socket = socket;
      socket._httpMessage = this;
      this.emit('socket', socket);
    },
    detachSocket(socket) {
      state(this);
      if (this.socket !== socket) fail('ERR_INVALID_ARG_VALUE');
      if (socket._httpMessage === this) socket._httpMessage = null;
      this.socket = null;
    },
    addTrailers(headers) {
      const current = state(this);
      current.trailers = {};
      for (const [name, value] of Object.entries(headers)) {
        const key = nameKey(name);
        validateValue(value);
        current.trailers[key] = value;
      }
    },
    setTimeout(ms, callback) {
      state(this);
      if (callback !== undefined) this.once('timeout', callback);
      this.socket?.setTimeout?.(ms);
      return this;
    },
    ref() { this.socket?.ref?.(); return this; },
    unref() { this.socket?.unref?.(); return this; },
  });
  return { IncomingMessage, ServerResponse };
}
