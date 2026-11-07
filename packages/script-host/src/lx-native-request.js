/** Runs inside the LX thread; the shared Node adapter only handles I/O. */
export function prepareLXRequest(input, Buffer, randomBytes, fail) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw fail('INVALID_ARGUMENT');
  const options = { ...input };
  for (const key of ['method', 'headers', 'body', 'form', 'formData', 'timeout', 'json', 'encoding',
    'follow_max', 'followRedirect', 'retry']) {
    if (options[key] === undefined) delete options[key];
  }
  if ([options.body, options.form, options.formData].filter(value => value !== undefined).length > 1)
    throw fail('INVALID_ARGUMENT');
  if (options.headers !== undefined &&
    (!options.headers || typeof options.headers !== 'object' || Array.isArray(options.headers)))
    throw fail('INVALID_ARGUMENT');
  const headers = { ...options.headers };
  const contentType = (value, replace = false) => {
    const names = Object.keys(headers).filter(name => name.toLowerCase() === 'content-type');
    if (!replace && names.length) return;
    for (const name of names) delete headers[name];
    headers['content-type'] = value;
  };
  const bytes = value => {
    if (typeof value === 'string') return Buffer.from(value);
    if (value instanceof ArrayBuffer) return Buffer.from(new Uint8Array(value));
    if (ArrayBuffer.isView(value)) return Buffer.from(new Uint8Array(value.buffer, value.byteOffset, value.byteLength));
    if (value?.type === 'Buffer' && Array.isArray(value.data)) return Buffer.from(value.data);
    throw fail('INVALID_ARGUMENT');
  };
  if (options.body !== undefined) {
    const body = options.body;
    if (body && typeof body === 'object' && !ArrayBuffer.isView(body) &&
      !(body instanceof ArrayBuffer) && body.type !== 'Buffer') {
      options.body = Buffer.from(JSON.stringify(body));
      contentType('application/json');
    } else options.body = bytes(body);
  } else if (options.form !== undefined) {
    if (!options.form || typeof options.form !== 'object' || Array.isArray(options.form))
      throw fail('INVALID_ARGUMENT');
    // URLSearchParams' application/x-www-form-urlencoded escaping, including
    // replacement of lone surrogates before UTF-8 encoding.
    const encode = value => {
      let output = '';
      for (const byte of Buffer.from(String(value).toWellFormed())) {
        if (byte >= 48 && byte <= 57 || byte >= 65 && byte <= 90 || byte >= 97 && byte <= 122 ||
          byte === 42 || byte === 45 || byte === 46 || byte === 95) output += String.fromCharCode(byte);
        else output += byte === 32 ? '+' : '%' + byte.toString(16).toUpperCase().padStart(2, '0');
      }
      return output;
    };
    options.body = Buffer.from(Object.entries(options.form).map(([key, value]) =>
      encode(key) + '=' + encode(value)).join('&'));
    delete options.form;
    contentType('application/x-www-form-urlencoded');
  } else if (options.formData !== undefined) {
    if (!options.formData || typeof options.formData !== 'object' || Array.isArray(options.formData))
      throw fail('INVALID_ARGUMENT');
    const boundary = 'meowfilm-' + randomBytes(16).toString('hex');
    const chunks = [];
    const append = value => chunks.push(typeof value === 'string' ? Buffer.from(value) : value);
    const quote = value => {
      if (typeof value !== 'string' || /[\r\n"]/.test(value)) throw fail('INVALID_ARGUMENT');
      return value;
    };
    for (const [name, value] of Object.entries(options.formData)) {
      let data = value, filename, type;
      if (value && typeof value === 'object' && Object.hasOwn(value, 'value')) {
        data = value.value;
        filename = value.options?.filename ?? 'blob';
        type = value.options?.contentType ?? 'application/octet-stream';
      } else if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer || value?.type === 'Buffer') {
        filename = 'blob'; type = 'application/octet-stream';
      }
      append('--' + boundary + '\r\nContent-Disposition: form-data; name="' + quote(name) + '"');
      if (filename) append('; filename="' + quote(filename) + '"\r\nContent-Type: ' + quote(type));
      append('\r\n\r\n');
      append(filename ? bytes(data) : String(data));
      append('\r\n');
    }
    append('--' + boundary + '--\r\n');
    options.body = Buffer.concat(chunks);
    delete options.formData;
    contentType('multipart/form-data; boundary=' + boundary, true);
  }
  if (Object.keys(headers).length || options.headers !== undefined) options.headers = headers;
  return options;
}
