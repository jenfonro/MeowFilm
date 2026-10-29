import { existsSync, globSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join, matchesGlob, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { isDeepStrictEqual } from 'node:util';

const registryOrigin = 'https://registry.npmjs.org';
const digestBytes = { sha1: 20, sha256: 32, sha384: 48, sha512: 64 };
const exactVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));

function requireValid(condition, message) {
  if (!condition) throw new Error(`Dependency lock verification failed: ${message}`);
}

function localPath(path) {
  return typeof path === 'string' && path.length > 0 && !/[\\:\u0000-\u001f\u007f]/.test(path)
    && path.split('/').every(part => part !== '' && part !== '.' && part !== '..');
}

function inside(root, path) {
  const rel = relative(root, realpathSync(path));
  return rel !== '' && rel !== '..' && !rel.startsWith(`..${sep}`)
    && !isAbsolute(rel);
}

function installedName(location) {
  const index = location.lastIndexOf('node_modules/');
  if (index < 0 || (index > 0 && location[index - 1] !== '/')) return null;
  const name = location.slice(index + 'node_modules/'.length);
  return /^(?:@[^/]+\/)?[^/@]+$/.test(name) ? name : null;
}

export function discoverWorkspaces(root, manifest) {
  const declarations = manifest.workspaces?.packages ?? manifest.workspaces ?? [];
  requireValid(Array.isArray(declarations), 'workspaces must be an array or { packages: [] }');
  const positive = [];
  let negative = [];
  for (const declaration of declarations) {
    requireValid(typeof declaration === 'string', 'invalid workspace pattern');
    const bangs = declaration.match(/^!+/)?.[0].length ?? 0;
    const pattern = declaration.slice(bangs).replaceAll('\\', '/')
      .replace(/^\.?\/+/, '').replace(/\/+$/, '');
    requireValid(pattern && !pattern.split('/').includes('..') && !pattern.includes(':'),
      `workspace pattern escapes project: ${declaration}`);
    if (bangs % 2) negative.push(pattern);
    else {
      negative = negative.filter(exclusion => !matchesGlob(pattern, exclusion));
      positive.push(pattern);
    }
  }
  const patterns = positive.filter(pattern => !negative.some(exclusion => matchesGlob(pattern, exclusion)));
  const workspaces = new Map();
  const names = new Set();
  for (const pattern of patterns) {
    for (const file of globSync(`${pattern}/package.json`, {
      cwd: root, exclude: ['**/node_modules/**', '**/.git/**'],
    })) {
      const location = dirname(file).split(sep).join('/');
      if (negative.some(exclusion => matchesGlob(location, exclusion)) || workspaces.has(location)) continue;
      requireValid(localPath(location) && inside(root, join(root, location)),
        `workspace escapes project: ${location}`);
      const workspace = readJSON(join(root, file));
      requireValid(isObject(workspace) && typeof workspace.name === 'string' && workspace.name,
        `${location}: workspace manifest needs a name`);
      requireValid(!names.has(workspace.name), `duplicate workspace name: ${workspace.name}`);
      names.add(workspace.name);
      workspaces.set(location, workspace);
    }
  }
  return workspaces;
}

function checkManifest(location, entry, manifest) {
  requireValid(isObject(entry) && !entry.link, `${location}: missing package record`);
  // npm may omit a local package's name when it is already implied by its folder.
  requireValid(entry.name === undefined || entry.name === manifest.name,
    `${location}: manifest name mismatch`);
  requireValid(entry.version === manifest.version, `${location}: manifest version mismatch`);
  for (const field of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
    requireValid((entry[field] === undefined || isObject(entry[field]))
      && (manifest[field] === undefined || isObject(manifest[field])),
    `${location}: invalid ${field}`);
    const actual = { ...entry[field] };
    const expected = { ...manifest[field] };
    // optionalDependencies override duplicate dependencies in npm's normalized manifest.
    if (field === 'dependencies') {
      for (const name of Object.keys(manifest.optionalDependencies ?? {})) {
        delete actual[name];
        delete expected[name];
      }
    }
    requireValid(isDeepStrictEqual(actual, expected), `${location}: manifest ${field} mismatch`);
  }
}

function checkResolved(location, resolved) {
  let url;
  try { url = new URL(resolved); } catch { /* Report the same policy error below. */ }
  requireValid(typeof resolved === 'string' && !/[\s\\]/.test(resolved)
    && url?.origin === registryOrigin && !url.username && !url.password
    && !url.search && !url.hash && url.pathname !== '/',
  `${location}: resolved must be a credential-free HTTPS tarball URL on ${registryOrigin}`);
  // Do not synthesize or enforce tarball filenames: aliases and published dist paths
  // need not match the node_modules directory name. npm ci verifies artifact bytes.
}

function checkIntegrity(location, integrity) {
  requireValid(typeof integrity === 'string' && integrity.trim().length > 0,
    `${location}: missing integrity`);
  for (const token of integrity.trim().split(/\s+/)) {
    const match = /^([a-z0-9]+)-([A-Za-z0-9+/]+={0,2})(?:\?[\x21-\x7e]*)?$/.exec(token);
    const size = match && Object.hasOwn(digestBytes, match[1]) && digestBytes[match[1]];
    const bytes = match && Buffer.from(match[2], 'base64');
    requireValid(size && bytes.length === size
      && bytes.toString('base64').replace(/=+$/, '') === match[2].replace(/=+$/, ''),
    `${location}: malformed or unsupported integrity`);
  }
}

/**
 * Offline policy check; does not fetch, install, rewrite, or read node_modules.
 * Supports npm v2/v3 packages records, SHA-1/SHA-2 SRI (including multiple hashes),
 * nested installs, registry aliases, optional/peer metadata, and workspace links.
 * Bundled entries inherit the enclosing tarball's integrity, not a made-up digest.
 * Cryptographic artifact verification and dependency resolution remain npm ci's job.
 */
export function verifyDependencyLock(projectRoot = fileURLToPath(new URL('../', import.meta.url))) {
  const root = realpathSync(resolve(projectRoot));
  requireValid(!existsSync(join(root, 'npm-shrinkwrap.json')),
    'npm-shrinkwrap.json would override the root package-lock.json');
  const manifest = readJSON(join(root, 'package.json'));
  const lock = readJSON(join(root, 'package-lock.json'));
  requireValid(isObject(manifest) && isObject(lock), 'manifest and lock must be objects');
  requireValid([2, 3].includes(lock.lockfileVersion), 'expected lockfileVersion 2 or 3');
  requireValid(isObject(lock.packages), 'missing packages map');
  requireValid(lock.name === manifest.name && lock.version === manifest.version,
    'root manifest identity mismatch');
  checkManifest('<root>', lock.packages[''], manifest);
  requireValid(isDeepStrictEqual(lock.packages[''].workspaces ?? [], manifest.workspaces ?? []),
    'root workspace declarations mismatch');
  const workspaces = discoverWorkspaces(root, manifest);
  for (const [location, workspace] of workspaces) {
    for (const filename of ['package-lock.json', 'npm-shrinkwrap.json']) {
      requireValid(!existsSync(join(root, location, filename)),
        `${location}/${filename}: use the root lock only`);
    }
    checkManifest(location, lock.packages[location], workspace);
    const link = lock.packages[`node_modules/${workspace.name}`];
    requireValid(link?.link === true && link.resolved === location,
      `${location}: missing or mismatched workspace link for ${workspace.name}`);
  }

  const report = { lockfileVersion: lock.lockfileVersion, registry: [], workspaceLinks: 0, bundled: [] };
  for (const [location, entry] of Object.entries(lock.packages)) {
    requireValid(isObject(entry), `${location}: invalid package record`);
    if (location === '' || workspaces.has(location)) continue;
    requireValid(localPath(location), `${location}: invalid package location`);
    const name = installedName(location);
    requireValid(name, `${location}: undeclared workspace or invalid installed location`);
    const parentPath = location.slice(0, location.lastIndexOf('node_modules/')).replace(/\/$/, '');
    const parent = lock.packages[parentPath];
    requireValid(isObject(parent) && !parent.link
      && (parentPath === '' || workspaces.has(parentPath) || installedName(parentPath)),
    `${location}: missing installed parent`);
    requireValid(entry.link === undefined || typeof entry.link === 'boolean',
      `${location}: link must be boolean`);
    if (entry.link === true) {
      const workspace = workspaces.get(entry.resolved);
      requireValid(workspace && name === workspace.name,
        `${location}: dangling or mismatched workspace link: ${entry.resolved}`);
      report.workspaceLinks++;
      continue;
    }
    requireValid(typeof entry.version === 'string' && exactVersion.test(entry.version),
      `${location}: missing or non-exact registry version`);
    requireValid(entry.inBundle === undefined || typeof entry.inBundle === 'boolean',
      `${location}: inBundle must be boolean`);
    if (entry.inBundle === true) {
      // A top-level package cannot escape the digest requirement by setting inBundle.
      requireValid(installedName(parentPath) && (parent.inBundle === true
          || (Array.isArray(parent.bundleDependencies) && parent.bundleDependencies.length > 0)),
      `${location}: bundled package has no enclosing bundled artifact`);
      if (entry.resolved !== undefined) checkResolved(location, entry.resolved);
      if (entry.integrity !== undefined) checkIntegrity(location, entry.integrity);
      report.bundled.push({ location, name, version: entry.version, parent: parentPath });
      continue;
    }
    checkResolved(location, entry.resolved);
    checkIntegrity(location, entry.integrity);
    report.registry.push({
      location, name: entry.name ?? name, version: entry.version,
      resolved: entry.resolved, integrity: entry.integrity,
    });
  }
  return report;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  try {
    const args = process.argv.slice(2);
    const paths = args.filter(arg => arg !== '--json');
    requireValid(paths.length <= 1 && !paths.some(arg => arg.startsWith('--')),
      'usage: node scripts/verify-dependency-lock.mjs [project-root] [--json]');
    const report = verifyDependencyLock(paths[0]);
    console.log(args.includes('--json') ? JSON.stringify(report, null, 2)
      : `Dependency lock verified offline: ${report.registry.length} registry packages, ${report.workspaceLinks} workspace links, ${report.bundled.length} bundled packages.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
