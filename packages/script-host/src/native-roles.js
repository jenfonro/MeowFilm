export const tlsOptionNames = Object.freeze(['servername', 'rejectUnauthorized', 'ca', 'cert', 'key', 'pfx',
  'passphrase', 'ciphers', 'minVersion', 'maxVersion', 'ALPNProtocols', 'session',
  'checkServerIdentity', 'family']);
export const httpRequestOptionNames = Object.freeze(['protocol', 'hostname', 'host', 'port', 'path',
  'method', 'headers', 'auth', 'timeout', 'agent', 'socketPath', 'fd', 'createConnection', 'lookup',
  ...tlsOptionNames]);
/** Project HTTP options without copying unrelated framework metadata. A
 * managed Agent owns connection creation, including agent:false. */
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
