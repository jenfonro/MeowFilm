import { createHash } from 'node:crypto';
import { availableParallelism } from 'node:os';
import { access, lstat, mkdir, readFile, writeFile, rename, rm } from 'node:fs/promises';
import { isBuiltin } from 'node:module';
import { resolve, basename, dirname, extname, isAbsolute, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { nativeSourceInputs } from './native-inputs.mjs';
import {
  root, policy, loadedBuildInputs, commands, run, files, hashInputs, prepareSource, verifySourceInventory,
  artifact, unchanged, sdkDependencyInputs,
} from './build-native.mjs';
import { buildSourceSDK } from './build-source-sdk.mjs';
import { iosTarget, iosArtifacts, checkIOSMachO, checkIOSSymbols, IOS_CAT_WEB_VALUES } from './ios-target.mjs';
import { sqliteBuildInputs } from './sqlite-inputs.mjs';

const hash = data => createHash('sha256').update(data).digest('hex');
const exists = path => access(path).then(() => true, () => false);
const loadedIOSInputs = await hashInputs([
  'scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs', 'scripts/quickjs/build-source-sdk.mjs',
  'scripts/quickjs/sqlite-inputs.mjs', 'native/SQLite/sqlite.json', 'scripts/prepare-ios-sqlite.py',
]);

export async function buildCatWebValues(directory, projectRoot = root) {
  const captured = new Map();
  const pathOf = path => {
    const local = relative(projectRoot, resolve(projectRoot, path));
    if (!local || local === '..' || local.startsWith(`..${sep}`) || isAbsolute(local)) {
      throw new Error(`Cat Web value input outside the workspace: ${path}`);
    }
    return local.split(sep).join('/');
  };
  const readInput = async input => {
    const path = pathOf(input);
    let current = projectRoot;
    for (const part of path.split('/')) {
      current = resolve(current, part);
      const stat = await lstat(current);
      if (stat.isSymbolicLink()) throw new Error(`Symbolic link in Cat Web value input: ${path}`);
    }
    if (!(await lstat(current)).isFile()) throw new Error(`Not a regular Cat Web value input: ${path}`);
    const bytes = await readFile(current), sha256 = hash(bytes);
    if (captured.has(path) && captured.get(path) !== sha256) {
      throw new Error(`Cat Web value input changed during build: ${path}`);
    }
    captured.set(path, sha256);
    return bytes;
  };
  for (const path of ['scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs']) await readInput(path);
  const manifest = JSON.parse(await readInput('package.json'));
  const lock = JSON.parse(await readInput('package-lock.json'));
  const mobile = JSON.parse(await readInput('scripts/ios-node-mobile.json'));
  const packages = new Map();
  async function checkDependency(path) {
    const parts = pathOf(path).split('/'), index = parts.lastIndexOf('node_modules');
    if (index < 0) throw new Error(`Cat Web value dependency is not installed: ${path}`);
    const location = parts.slice(0, index + (parts[index + 1].startsWith('@') ? 3 : 2)).join('/');
    if (packages.has(location)) return packages.get(location);
    const installed = JSON.parse(await readInput(`${location}/package.json`));
    const pinned = lock.packages?.[location];
    if (!pinned || pinned.link || installed.version !== pinned.version
        || pinned.name !== undefined && installed.name !== pinned.name
        || typeof pinned.resolved !== 'string' || typeof pinned.integrity !== 'string') {
      throw new Error(`Cat Web value dependency differs from package lock: ${location}`);
    }
    packages.set(location, installed);
    return installed;
  }
  const installed = await checkDependency('node_modules/undici/package.json');
  const esbuild = await checkDependency('node_modules/esbuild/package.json');
  const declarations = ['dependencies', 'devDependencies', 'optionalDependencies'];
  if (installed.version !== IOS_CAT_WEB_VALUES.version || esbuild.version !== policy.build.esbuild
      || !declarations.some(field => manifest[field]?.undici === installed.version)
      || declarations.some(field => manifest[field]?.undici !== lock.packages?.['']?.[field]?.undici)) {
    throw new Error('Cat Web values require the version-adapted Undici and locked esbuild');
  }
  const licenseBytes = await readInput(IOS_CAT_WEB_VALUES.license.path);
  if (hash(licenseBytes) !== IOS_CAT_WEB_VALUES.license.sha256) throw new Error('Undici license differs from the locked source');
  const result = await build({
    absWorkingDir: projectRoot,
    stdin: {
      // Response initializes BodyInit before Request captures its converter.
      contents: `module.exports = {\n${['Headers', 'Response', 'Request', 'FormData'].map(name =>
        `  ${name}: require('./node_modules/undici/lib/web/fetch/${name.toLowerCase()}.js').${name},`).join('\n')}\n};`,
      resolveDir: projectRoot, loader: 'js',
    },
    bundle: true, platform: 'node', format: 'cjs', target: `node${mobile.version}`,
    keepNames: true, write: false, metafile: true, preserveSymlinks: true,
    plugins: [{
      name: 'cat-web-value-closure',
      setup(plugin) {
        plugin.onResolve({ filter: /.*/ }, input => {
          if (isBuiltin(input.path)) return { path: input.path, external: true };
        });
        plugin.onLoad({ filter: /.*/, namespace: 'file' }, async input => {
          await checkDependency(input.path);
          const extension = extname(input.path);
          if (!['.js', '.cjs', '.mjs', '.json'].includes(extension)) {
            throw new Error(`Non-JavaScript Cat Web value input: ${input.path}`);
          }
          return { contents: await readInput(input.path),
            loader: extension === '.json' ? 'json' : 'js', resolveDir: dirname(input.path) };
        });
      },
    }],
  });
  if (Object.values(result.metafile.outputs).some(output => output.imports.some(input =>
    !input.external || !isBuiltin(input.path)))) {
    throw new Error('Cat Web value bundle has an unbundled dependency');
  }
  const inputs = [];
  for (const path of [...captured.keys()].sort()) {
    await readInput(path);
    inputs.push({ path, sha256: captured.get(path) });
  }
  const path = pathOf(resolve(directory, 'cat-web-values.cjs'));
  const bytes = result.outputFiles[0].contents;
  await mkdir(directory, { recursive: true });
  await writeFile(resolve(projectRoot, path), bytes);
  return { path, bytes: bytes.length, sha256: hash(bytes), inputs,
    license: { ...IOS_CAT_WEB_VALUES.license, bytes: licenseBytes.length } };
}

export async function buildIOSNative(target) {
  target = iosTarget(target.sdk, target.arch, target.minimumOS);
  if (process.platform !== 'darwin' || process.versions.node !== policy.build.node) {
    throw new Error(`iOS native build requires macOS and Node ${policy.build.node}`);
  }
  const paths = iosArtifacts(target), directory = resolve(root, paths.directory);
  await mkdir(directory, { recursive: true });
  await mkdir(resolve(root, policy.build.cache), { recursive: true });
  // One target has one reusable output/cache. A failed build never publishes a receipt.
  const lock = resolve(directory, 'build.lock');
  await mkdir(lock);
  try {
    await run('python3', ['-I', '-B', 'scripts/prepare-ios-sqlite.py',
      '--target', target.sdk === 'iphoneos' ? 'ios-arm64' : 'ios-simulator']);
    const sqlite = sqliteBuildInputs(root, target);
    const mobile = JSON.parse(await run('python3', [
      '-I', '-B', 'scripts/prepare-ios-runtime.py', '--verify-node-mobile',
      '--sdk', target.sdk, '--arch', target.arch,
    ], root, true));
    const sourceInputs = [];
    for (const [name, source] of Object.entries(policy.sources)) {
      sourceInputs.push(...await prepareSource(name, source));
    }
    const nativeInputs = (await Promise.all(policy.inputDirectories.map(path => files(path)))).flat();
    const sdkCandidates = [
      ...await files('packages/script-host/src'), ...await files('packages/source-runtime/src'),
      ...await sdkDependencyInputs(),
    ];
    const catWebValues = await buildCatWebValues(resolve(directory, 'build/Release'));
    const before = await hashInputs([
      'package.json', 'package-lock.json', ...policy.mandatoryInputs,
      'scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs',
      'scripts/quickjs/build-source-sdk.mjs', 'scripts/prepare-ios-runtime.py',
      'scripts/ios-node-mobile.json', ...nativeInputs, ...sourceInputs, ...sdkCandidates,
      ...catWebValues.inputs.map(input => input.path),
      ...nativeSourceInputs(policy), ...policy.patches.map(patch => patch.path),
      ...mobile.inputs.map(input => input.path),
      ...sqlite.inputs.map(input => input.path),
    ]);
    const beforeMap = new Map(before.map(input => [input.path, input.sha256]));
    await unchanged([...loadedBuildInputs, ...loadedIOSInputs]);
    for (const input of mobile.inputs) {
      if (beforeMap.get(input.path) !== input.sha256) throw new Error('Node Mobile input changed');
    }
    for (const source of Object.values(policy.sources)) {
      if (beforeMap.get(source.archivePath) !== source.sha256
          || beforeMap.get(source.license.path) !== source.license.sha256) {
        throw new Error('Native source or license differs from policy');
      }
    }
    const sdk = await buildSourceSDK({ directory: resolve(directory, 'build/Release') });
    for (const input of sdk.inputs) {
      if (beforeMap.get(input.path) !== input.sha256) throw new Error(`Untracked/changed SDK input: ${input.path}`);
    }
    const sdkPath = sdk.path;
    if (sdkPath !== paths.sdk) throw new Error('Unexpected iOS SDK output');
    for (const input of catWebValues.inputs) {
      if (beforeMap.get(input.path) !== input.sha256) throw new Error(`Untracked/changed Cat Web value input: ${input.path}`);
    }
    if (catWebValues.path !== paths.catWebValues) throw new Error('Unexpected iOS Cat Web value output');
    const sdkRoot = await run('xcrun', ['--sdk', target.sdk, '--show-sdk-path'], root, true);
    const sdkVersion = await run('xcrun', ['--sdk', target.sdk, '--show-sdk-version'], root, true);
    const compiler = await run('xcrun', ['--sdk', target.sdk, '--find', 'clang'], root, true);
    const toolchain = {
      host: { platform: process.platform, arch: process.arch, node: process.versions.node },
      node: process.versions.node, esbuild: policy.build.esbuild,
      xcode: await run('xcodebuild', ['-version'], root, true), sdk: sdkVersion,
      compiler: await run(compiler, ['--version'], root, true),
      perl: await run('perl', ['-e', 'print $^V'], root, true),
    };
    const sources = Object.fromEntries(Object.entries(policy.sources).map(([name, { version, commit, url, sha256 }]) =>
      [name, { version, commit, url, sha256 }]));
    const identity = {
      schemaVersion: 1, abi: 1, napi: 8, platform: 'ios', target, sources,
      inputs: before, sdk, catWebValues, nodeMobile: mobile.identity, nodeHeaderFiles: mobile.headers, sqlite, toolchain,
    };
    const bindingIdentity = hash(JSON.stringify(identity));
    await writeFile(resolve(directory, 'binding-identity.h'), [
      `#define MEOW_BINDING_IDENTITY "${bindingIdentity}"`,
      `#define MEOW_TARGET_SDK "${target.sdk}"`,
      `#define MEOW_TARGET_ARCH "${target.arch}"`,
      `#define MEOW_MINIMUM_OS "${target.minimumOS}"`, '',
    ].join('\n'));
    const arch = target.xcframeworkArchitecture;
    const triple = `${arch}-apple-ios${target.minimumOS}${target.sdk === 'iphonesimulator' ? '-simulator' : ''}`;
    const flags = ['-target', triple, '-isysroot', sdkRoot, '-O2', '-fPIC',
      '-fvisibility=hidden', '-fno-common'];
    const opensslBuild = resolve(directory, 'openssl');
    await mkdir(opensslBuild, { recursive: true });
    const opensslTarget = target.variant === null ? 'ios64-xcrun'
      : target.xcframeworkArchitecture === 'arm64' ? 'iossimulator-arm64-xcrun' : 'iossimulator-x86_64-xcrun';
    const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
    const configure = [
      resolve(root, policy.sources.openssl.sourcePath, 'Configure'), opensslTarget,
      'no-shared', 'no-module', 'no-tests', 'no-apps', 'no-docs', 'no-legacy',
      'no-autoload-config', 'no-ssl', 'no-dtls',
      `CC=${quote(compiler)}`, `CFLAGS=${flags.map(quote).join(' ')}`,
    ];
    const config = JSON.stringify({ configure, compiler, toolchain,
      inputs: before.filter(input => input.path.startsWith(policy.sources.openssl.sourcePath + '/')) });
    const configFile = resolve(opensslBuild, 'meow-config.json');
    const environment = { CC: compiler, SDKROOT: sdkRoot };
    if (!await exists(configFile) || await readFile(configFile, 'utf8') !== config) {
      await run('perl', configure, opensslBuild, false, environment);
      await writeFile(configFile, config);
    }
    await run('make', ['-j', String(availableParallelism()), 'build_generated', 'libcrypto.a'],
      opensslBuild, false, environment);
    const gyp = JSON.parse(await readFile(resolve(root, 'packages/script-host/binding.gyp'), 'utf8'));
    const objects = [];
    const headers = [
      resolve(directory), resolve(opensslBuild, 'include'),
      resolve(root, policy.sources.openssl.sourcePath, 'include'),
      resolve(root, policy.sources.quickjs.sourcePath), resolve(root, policy.sources.zlib.sourcePath),
      resolve(root, mobile.headerDirectory),
      resolve(root, sqlite.framework, 'Headers'),
    ].flatMap(path => ['-I', path]);
    const objectDirectory = resolve(directory, 'obj');
    await mkdir(objectDirectory, { recursive: true });
    // C/C++ source membership comes from the same gyp file as the desktop build.
    for (const targetSpec of gyp.targets) {
      for (const input of [...targetSpec.sources,
        ...(targetSpec.target_name === 'script_host_native' ? ['native/sqlite-storage.cc'] : [])]) {
        const source = input.startsWith('<(runtime_native)/')
          ? resolve(root, policy.build.cache, input.slice('<(runtime_native)/'.length))
          : input.startsWith('<(quickjs_source)/')
          ? resolve(root, policy.sources.quickjs.sourcePath, input.slice('<(quickjs_source)/'.length))
          : resolve(root, 'packages/script-host', input);
        const object = resolve(objectDirectory, `${targetSpec.target_name}-${basename(source)}.o`);
        const args = [...flags, ...headers, '-DNAPI_VERSION=8', '-DNODE_GYP_MODULE_NAME=script_host_native',
          '-DMEOW_IOS_FRAMEWORK=1', '-DMEOW_APPLE_STORAGE=1', '-DZ_PREFIX', ...(source.endsWith('.cc')
            ? ['-std=c++17', '-fvisibility-inlines-hidden']
            : ['-std=c11', '-D_GNU_SOURCE', '-DQUICKJS_NG_BUILD']), '-c', source, '-o', object];
        const stamp = `${object}.json`;
        const inputIdentity = hash(JSON.stringify({
          compiler: toolchain.compiler, args,
          inputs: before.filter(input => input.path.startsWith('packages/script-host/native/')
            || input.path.startsWith(policy.sources.quickjs.sourcePath + '/')
            || input.path.startsWith(policy.build.cache + '/')
            || input.path.startsWith(mobile.headerDirectory + '/')),
          ...(source.endsWith('/addon.cc') ? { bindingIdentity } : {}),
          opensslConfig: hash(config),
        }));
        if (!await exists(object) || !await exists(stamp) || await readFile(stamp, 'utf8') !== inputIdentity) {
          await run(compiler, args);
          await writeFile(stamp, inputIdentity);
        }
        objects.push(object);
      }
    }
    const exportsFile = resolve(directory, 'exports.txt');
    await writeFile(exportsFile, policy.ios.exports.join('\n') + '\n');
    const framework = resolve(root, paths.framework);
    await mkdir(framework, { recursive: true });
    const temporary = resolve(directory, 'MeowScriptHost.partial');
    await run(compiler, [...flags, '-dynamiclib', '-std=c++17', ...objects,
      resolve(opensslBuild, 'libcrypto.a'), '-lc++',
      '-F', resolve(root, dirname(sqlite.framework)), '-framework', 'MeowSQLite',
      '-F', resolve(root, mobile.frameworkDirectory), '-framework', 'NodeMobile',
      '-Wl,-twolevel_namespace', '-Wl,-undefined,error', '-Wl,-no_implicit_dylibs',
      `-Wl,-exported_symbols_list,${exportsFile}`,
      '-Wl,-install_name,@rpath/MeowScriptHost.framework/MeowScriptHost',
      `-Wl,-map,${resolve(directory, 'link-map.txt')}`, '-o', temporary]);
    const binary = await readFile(temporary);
    const { dylibs } = checkIOSMachO(binary, target);
    const exported = (await run('xcrun', ['nm', '-gjU', temporary], root, true)).split(/\r?\n/).filter(Boolean).sort();
    // The export list localizes all QuickJS/OpenSSL/zlib and C++ helpers.
    // Undefined references must resolve to NodeMobile's exact N-API or Apple SDK libraries.
    const undefinedSymbols = await run('xcrun', ['nm', '-m', '-u', temporary], root, true);
    checkIOSSymbols(exported, undefinedSymbols, policy.ios.exports);
    await writeFile(resolve(root, paths.exports), JSON.stringify({ exported, dylibs, undefinedSymbols }, null, 2) + '\n');
    const info = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.meowfilm.script-host</string>
<key>CFBundleExecutable</key><string>MeowScriptHost</string>
<key>CFBundleName</key><string>MeowScriptHost</string>
<key>CFBundlePackageType</key><string>FMWK</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>1.0</string>
<key>MinimumOSVersion</key><string>${target.minimumOS}</string>
<key>CFBundleSupportedPlatforms</key><array><string>${target.sdk === 'iphoneos' ? 'iPhoneOS' : 'iPhoneSimulator'}</string></array>
</dict></plist>
`;
    await writeFile(resolve(root, paths.info), info);
    await unchanged(before);
    await unchanged([...loadedBuildInputs, ...loadedIOSInputs]);
    const afterNative = (await Promise.all(policy.inputDirectories.map(path => files(path)))).flat();
    const afterSDK = [
      ...await files('packages/script-host/src'), ...await files('packages/source-runtime/src'),
      ...await sdkDependencyInputs(),
    ];
    if (JSON.stringify(afterNative) !== JSON.stringify(nativeInputs)
        || JSON.stringify(afterSDK) !== JSON.stringify(sdkCandidates)) {
      throw new Error('Native/SDK membership changed during build');
    }
    const finalCatWebValues = await artifact(catWebValues.path);
    if (finalCatWebValues.bytes !== catWebValues.bytes || finalCatWebValues.sha256 !== catWebValues.sha256) {
      throw new Error('Cat Web value output changed after bundling');
    }
    for (const source of Object.values(policy.sources)) {
      await verifySourceInventory(source, sourceInputs.filter(path => path.startsWith(source.sourcePath + '/')));
    }
    await rename(temporary, resolve(root, paths.artifact));
    const receipt = {
      ...identity, arch: target.arch, bindingIdentity,
      artifact: await artifact(paths.artifact), sdk,
      frameworkInfo: await artifact(paths.info), exports: await artifact(paths.exports),
      sourceTrees: Object.fromEntries(Object.entries(policy.sources).map(([name, source]) =>
        [name, { path: source.sourcePath, files: sourceInputs.filter(path => path.startsWith(source.sourcePath + '/')) }])),
      toolchain: { ...toolchain, commands },
    };
    await unchanged(before);
    await writeFile(resolve(directory, 'build/Release/script-host-native.json.partial'), JSON.stringify(receipt, null, 2) + '\n');
    await rename(resolve(directory, 'build/Release/script-host-native.json.partial'), resolve(root, paths.receipt));
    console.log(`Built ${paths.artifact} ${receipt.artifact.sha256}`);
    return receipt;
  } finally { await rm(lock, { recursive: true }); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2), options = {};
  for (let index = 0; index < args.length; index += 2) {
    if (!['--sdk', '--arch'].includes(args[index]) || !args[index + 1] || options[args[index].slice(2)]) {
      throw new Error('Usage: node scripts/quickjs/build-ios.mjs --sdk <iphoneos|iphonesimulator> --arch <arm64|x64>');
    }
    options[args[index].slice(2)] = args[index + 1];
  }
  await buildIOSNative(iosTarget(options.sdk, options.arch));
}
