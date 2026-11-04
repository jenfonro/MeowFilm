import * as http from 'node:http';
import * as https from 'node:https';
import * as net from 'node:net';
import * as tls from 'node:tls';
import * as stream from 'node:stream';
import * as web from 'node:stream/web';
import { ECDH, X509Certificate, webcrypto } from 'node:crypto';
import { URL, URLSearchParams, Url } from 'node:url';
import { TextEncoder, TextDecoder } from 'node:util';
import { StringDecoder } from 'node:string_decoder';
import { Blob, File } from 'node:buffer';
import { Stats, Dirent } from 'node:fs';

const spec = (methods = '', fields = '', writable = '') => ({
  methods: new Set(methods.split(' ').filter(Boolean)),
  fields: new Set(fields.split(' ').filter(Boolean)),
  writable: new Set(writable.split(' ').filter(Boolean)),
});
const events = 'on once off addListener removeListener removeAllListeners prependListener prependOnceListener listenerCount eventNames setMaxListeners getMaxListeners';
const io = `${events} read write end destroy pause resume isPaused setEncoding cork uncork ref unref @@asyncIterator`;
const ioFields = 'destroyed closed errored readable readableEnded readableFlowing readableLength readableHighWaterMark readableObjectMode writable writableEnded writableFinished writableNeedDrain writableLength writableHighWaterMark writableObjectMode';
export const tlsOptionNames = Object.freeze(['servername', 'rejectUnauthorized', 'ca', 'cert', 'key', 'pfx',
  'passphrase', 'ciphers', 'minVersion', 'maxVersion', 'ALPNProtocols', 'session',
  'checkServerIdentity', 'family']);
export const httpRequestOptionNames = Object.freeze(['protocol', 'hostname', 'host', 'port', 'path',
  'method', 'headers', 'auth', 'timeout', 'agent', 'socketPath', 'fd', 'createConnection', 'lookup',
  ...tlsOptionNames]);
/** The one projection rule shared by the guest encoder, host bridge and HTTP
 * service. A managed Agent owns connection creation; its per-request callback
 * is unused metadata, never an alternate transport. */
export function projectFields(value, fields) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return value;
  return Object.fromEntries(fields
    .filter(key => Object.prototype.propertyIsEnumerable.call(value, key))
    .map(key => [key, value[key]]));
}
export function projectHttpRequestOptions(value,
  managedAgent = agent => agent === false) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return value;
  const managed = managedAgent(value.agent);
  return projectFields(value, managed
    ? httpRequestOptionNames.filter(key => key !== 'createConnection')
    : httpRequestOptionNames);
}
// This is an allow-list of public operations, not a traversal of a native
// prototype. In particular no constructor, connect, createConnection, fd,
// binding, native prototype, callback receiver or unknown native object is a
// capability merely because it was reachable from another one.
export const nativeRoles = Object.freeze({
  server: spec(`${events} listen close address getConnections closeAllConnections closeIdleConnections setTimeout ref unref`,
    'listening timeout keepAliveTimeout headersTimeout requestTimeout maxRequestsPerSocket maxHeadersCount',
    'timeout keepAliveTimeout headersTimeout requestTimeout maxRequestsPerSocket maxHeadersCount'),
  agent: spec('destroy', 'keepAlive maxSockets maxFreeSockets maxTotalSockets scheduling protocol defaultPort options'),
  request: spec(`${io} setTimeout setNoDelay setSocketKeepAlive abort setHeader getHeader getHeaders getHeaderNames hasHeader removeHeader flushHeaders`,
    `${ioFields} aborted finished headersSent socket connection method path host protocol res reusedSocket maxHeadersCount`,
    'maxHeadersCount'),
  response: spec(`${io} setTimeout setHeader appendHeader getHeader getHeaders getHeaderNames getRawHeaderNames hasHeader removeHeader flushHeaders writeHead addTrailers`,
    `${ioFields} headersSent finished statusCode statusMessage socket connection req sendDate strictContentLength`,
    'statusCode statusMessage sendDate strictContentLength'),
  incoming: spec(`${io} setTimeout`, `${ioFields} statusCode statusMessage headers headersDistinct rawHeaders trailers rawTrailers httpVersion httpVersionMajor httpVersionMinor method url complete aborted socket connection req`),
  socket: spec(`${io} setTimeout setNoDelay setKeepAlive address`,
    `${ioFields} connecting pending readyState timeout remoteAddress remotePort remoteFamily localAddress localPort localFamily bytesRead bytesWritten encrypted authorized authorizationError alpnProtocol servername`),
  tlsSocket: spec(`${io} setTimeout setNoDelay setKeepAlive address getPeerCertificate getCertificate getCipher getProtocol getSession getTLSTicket getEphemeralKeyInfo exportKeyingMaterial isSessionReused`,
    `${ioFields} connecting pending readyState timeout remoteAddress remotePort remoteFamily localAddress localPort localFamily bytesRead bytesWritten encrypted authorized authorizationError alpnProtocol servername`),
  stream: spec(io, ioFields),
  hash: spec(`${io} update digest copy`, ioFields),
  cipher: spec(`${io} update final setAutoPadding setAAD getAuthTag setAuthTag`, ioFields),
  sign: spec(`${io} update sign verify`, ioFields),
  ecdh: spec('generateKeys computeSecret getPrivateKey setPrivateKey getPublicKey setPublicKey'),
  x509: spec('checkEmail checkHost checkIP checkIssued checkPrivateKey verify toString toJSON',
    'subject subjectAltName issuer issuerCertificate infoAccess validFrom validTo validFromDate validToDate fingerprint fingerprint256 fingerprint512 keyUsage serialNumber raw publicKey ca'),
  cryptoKey: spec('', 'type extractable algorithm usages'),
  url: spec('toString toJSON', 'href origin protocol username password host hostname port pathname search searchParams hash',
    'href protocol username password host hostname port pathname search hash'),
  legacyUrl: spec('parse format resolve resolveObject',
    'protocol slashes auth host port hostname hash search query pathname path href',
    'protocol slashes auth host port hostname hash search query pathname path href'),
  certificate: spec('', 'subject issuer subjectaltname infoAccess modulus bits exponent pubkey asn1Curve nistCurve valid_from valid_to fingerprint fingerprint256 fingerprint512 serialNumber ext_key_usage raw ca issuerCertificate'),
  searchParams: spec('append delete get getAll has set sort toString entries keys values forEach @@iterator', 'size'),
  iterator: spec('next return throw @@iterator @@asyncIterator'),
  textEncoder: spec('encode encodeInto', 'encoding'),
  textDecoder: spec('decode', 'encoding fatal ignoreBOM'),
  stringDecoder: spec('write end', 'encoding'),
  stats: spec('isFile isDirectory isBlockDevice isCharacterDevice isSymbolicLink isFIFO isSocket',
    'dev mode nlink uid gid rdev blksize ino size blocks atimeMs mtimeMs ctimeMs birthtimeMs atime mtime ctime birthtime'),
  dirent: spec('isFile isDirectory isBlockDevice isCharacterDevice isSymbolicLink isFIFO isSocket', 'name'),
  fileHandle: spec('close readFile writeFile appendFile stat sync datasync truncate read write', 'fd'),
  blob: spec('arrayBuffer bytes text stream slice', 'size type'),
  file: spec('arrayBuffer bytes text stream slice', 'size type name lastModified'),
  readableWeb: spec('cancel getReader pipeTo pipeThrough tee values @@asyncIterator', 'locked'),
  writableWeb: spec('abort close getWriter', 'locked'),
  transformWeb: spec('', 'readable writable'),
  compressionWeb: spec('', 'readable writable'),
  decompressionWeb: spec('', 'readable writable'),
  textEncoderStream: spec('', 'readable writable encoding'),
  textDecoderStream: spec('', 'readable writable encoding fatal ignoreBOM'),
  reader: spec('read cancel releaseLock', 'closed'),
  writer: spec('write close abort releaseLock', 'closed ready desiredSize'),
  readController: spec('enqueue close error', 'desiredSize byobRequest'),
  writeController: spec('error', 'signal'),
  transformController: spec('enqueue error terminate', 'desiredSize'),
  byobRequest: spec('respond respondWithNewView', 'view'),
  byteLengthStrategy: spec('size', 'highWaterMark'),
  countStrategy: spec('size', 'highWaterMark'),
  http2Session: spec(`${events} request close destroy ping goaway settings setTimeout ref unref`,
    'closed destroyed connecting remoteSettings localSettings alpnProtocol originSet'),
  http2Stream: spec(`${io} close priority additionalHeaders sendTrailers setTimeout`, `${ioFields} id session sentHeaders rstCode`),
});

export const nativeConstructorRoles = Object.freeze({
  URL: 'url',
  URLSearchParams: 'searchParams',
  X509Certificate: 'x509',
  StringDecoder: 'stringDecoder',
  TextEncoder: 'textEncoder',
  TextDecoder: 'textDecoder',
  Blob: 'blob',
  File: 'file',
  ReadableStream: 'readableWeb',
  WritableStream: 'writableWeb',
  TransformStream: 'transformWeb',
  CompressionStream: 'compressionWeb',
  DecompressionStream: 'decompressionWeb',
  TextEncoderStream: 'textEncoderStream',
  TextDecoderStream: 'textDecoderStream',
  ByteLengthQueuingStrategy: 'byteLengthStrategy',
  CountQueuingStrategy: 'countStrategy',
  Server: 'server',
  Agent: 'agent',
});

export const nativeSymbols = new Map([
  ['@@iterator', Symbol.iterator], ['@@asyncIterator', Symbol.asyncIterator],
]);

/** Native stream endpoints that can cross the host-side pipe fast path.
 * Readable roles expose pipe/read semantics; writable roles expose write/end. */
export const pipeReadableRoles = Object.freeze(new Set([
  'stream', 'incoming', 'socket', 'tlsSocket', 'http2Stream',
]));
export const pipeWritableRoles = Object.freeze(new Set([
  'stream', 'request', 'response', 'socket', 'tlsSocket', 'http2Stream',
]));

const instanceOf = (value, constructor) =>
  typeof constructor === 'function' && value instanceof constructor;

export function classifyNative(value, tags) {
  const tagged = tags?.get(value);
  if (tagged) return tagged;
  if (instanceOf(value, URL)) return 'url';
  if (instanceOf(value, URLSearchParams)) return 'searchParams';
  if (instanceOf(value, Url)) return 'legacyUrl';
  if (instanceOf(value, https.Server)) return 'server';
  if (instanceOf(value, http.Server)) return 'server';
  if (instanceOf(value, http.ClientRequest)) return 'request';
  if (instanceOf(value, http.ServerResponse)) return 'response';
  if (instanceOf(value, http.IncomingMessage)) return 'incoming';
  if (instanceOf(value, tls.TLSSocket)) return 'tlsSocket';
  if (instanceOf(value, net.Socket)) return 'socket';
  if (instanceOf(value, http.Agent)) return 'agent';
  if (instanceOf(value, ECDH)) return 'ecdh';
  if (instanceOf(value, X509Certificate)) return 'x509';
  if (instanceOf(value, webcrypto.CryptoKey ?? globalThis.CryptoKey)) return 'cryptoKey';
  if (instanceOf(value, TextEncoder)) return 'textEncoder';
  if (instanceOf(value, TextDecoder)) return 'textDecoder';
  if (instanceOf(value, StringDecoder)) return 'stringDecoder';
  if (instanceOf(value, Stats)) return 'stats';
  if (instanceOf(value, Dirent)) return 'dirent';
  if (instanceOf(value, File)) return 'file';
  if (instanceOf(value, Blob)) return 'blob';
  if (instanceOf(value, web.ReadableStream)) return 'readableWeb';
  if (instanceOf(value, web.WritableStream)) return 'writableWeb';
  if (instanceOf(value, web.TransformStream)) return 'transformWeb';
  if (instanceOf(value, web.CompressionStream)) return 'compressionWeb';
  if (instanceOf(value, web.DecompressionStream)) return 'decompressionWeb';
  if (instanceOf(value, web.TextEncoderStream)) return 'textEncoderStream';
  if (instanceOf(value, web.TextDecoderStream)) return 'textDecoderStream';
  if (instanceOf(value, web.ReadableStreamDefaultReader) ||
    instanceOf(value, web.ReadableStreamBYOBReader)) return 'reader';
  if (instanceOf(value, web.WritableStreamDefaultWriter)) return 'writer';
  if (instanceOf(value, web.ReadableStreamDefaultController) ||
    instanceOf(value, web.ReadableByteStreamController)) return 'readController';
  if (instanceOf(value, web.WritableStreamDefaultController)) return 'writeController';
  if (instanceOf(value, web.TransformStreamDefaultController)) return 'transformController';
  if (instanceOf(value, web.ReadableStreamBYOBRequest)) return 'byobRequest';
  if (instanceOf(value, web.ByteLengthQueuingStrategy)) return 'byteLengthStrategy';
  if (instanceOf(value, web.CountQueuingStrategy)) return 'countStrategy';
  if (instanceOf(value, stream.Stream)) return 'stream';
  return null;
}
