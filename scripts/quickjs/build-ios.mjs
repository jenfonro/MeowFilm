import { createHash } from 'node:crypto';
import { availableParallelism } from 'node:os';
import { access, lstat, mkdir, readFile, writeFile, rename, rm } from 'node:fs/promises';
import { resolve, basename, dirname, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { nativeSourceInputs } from './native-inputs.mjs';
import {
  root, policy, loadedBuildInputs, commands, run, files, hashInputs, prepareSource, verifySourceInventory,
  artifact, unchanged, sdkDependencyInputs,
} from './build-native.mjs';
import { buildSourceSDK } from './build-source-sdk.mjs';
import { iosTarget, iosArtifacts, checkIOSMachO, checkIOSSymbols, IOS_CAT_WEB_VALUES } from './ios-target.mjs';

const hash = data => createHash('sha256').update(data).digest('hex');
const exists = path => access(path).then(() => true, () => false);
const loadedIOSInputs = await hashInputs([
  'scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs', 'scripts/quickjs/build-source-sdk.mjs',
]);

async function catWebValueCandidates() {
  for (const path of ['node_modules', 'node_modules/undici']) {
    const stat = await lstat(resolve(root, path));
    if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error(`Invalid Cat Web value dependency: ${path}`);
  }
  return files('node_modules/undici');
}

async function buildCatWebValues(directory) {
  const captured = new Map();
  const readInput = async path => {
    const bytes = await readFile(resolve(root, path));
    captured.set(path, hash(bytes));
    return bytes;
  };
  for (const path of ['scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs']) await readInput(path);
  const manifest = JSON.parse(await readInput('package.json'));
  const lock = JSON.parse(await readInput('package-lock.json'));
  const installed = JSON.parse(await readInput('node_modules/undici/package.json'));
  const esbuild = JSON.parse(await readInput('node_modules/esbuild/package.json'));
  const pinned = lock.packages?.['node_modules/undici'];
  const dependencyFields = ['dependencies', 'optionalDependencies', 'peerDependencies'];
  if (manifest.devDependencies?.undici !== IOS_CAT_WEB_VALUES.version
      || lock.packages?.['']?.devDependencies?.undici !== IOS_CAT_WEB_VALUES.version
      || dependencyFields.some(field => manifest[field]?.undici || lock.packages?.['']?.[field]?.undici)
      || installed.name !== 'undici' || installed.version !== IOS_CAT_WEB_VALUES.version
      || installed.license !== 'MIT' || installed.engines?.node !== '>=18.17'
      || pinned?.version !== IOS_CAT_WEB_VALUES.version || pinned.resolved !== IOS_CAT_WEB_VALUES.resolved
      || pinned.integrity !== IOS_CAT_WEB_VALUES.integrity || pinned.dev !== true || pinned.link
      || pinned.license !== 'MIT' || pinned.engines?.node !== '>=18.17'
      || [installed, pinned].some(value => dependencyFields.some(field => Object.keys(value[field] ?? {}).length))
      || esbuild.version !== policy.build.esbuild) {
    throw new Error('Cat Web values require the locked build-only Undici 6.29.1 and esbuild');
  }
  const licenseBytes = await readInput(IOS_CAT_WEB_VALUES.license.path);
  if (hash(licenseBytes) !== IOS_CAT_WEB_VALUES.license.sha256) throw new Error('Undici license differs from the locked source');
  const modulePaths = IOS_CAT_WEB_VALUES.modules.map(path => `node_modules/undici/${path}`);
  const modules = new Set(modulePaths.map(path => resolve(root, path)));
  const builtins = new Set(IOS_CAT_WEB_VALUES.builtins);
  const result = await build({
    absWorkingDir: root,
    stdin: {
      // Response initializes BodyInit before Request captures its converter.
      contents: `module.exports = {\n${['Headers', 'Response', 'Request', 'FormData'].map(name =>
        `  ${name}: require('./node_modules/undici/lib/web/fetch/${name.toLowerCase()}.js').${name},`).join('\n')}\n};`,
      resolveDir: root, loader: 'js',
    },
    bundle: true, platform: 'node', format: 'cjs', target: 'node18.20.4',
    keepNames: true, write: false, metafile: true,
    plugins: [{
      name: 'cat-web-value-closure',
      setup(plugin) {
        plugin.onResolve({ filter: /.*/ }, input => {
          if (builtins.has(input.path)) return { path: input.path, external: true };
          if (!input.path.startsWith('.')) throw new Error(`Unexpected Cat Web value import: ${input.path}`);
          const path = resolve(input.resolveDir, input.path.endsWith('.js') ? input.path : `${input.path}.js`);
          if (!modules.has(path)) throw new Error(`Cat Web value import leaves the pure closure: ${input.path}`);
          return { path };
        });
        plugin.onLoad({ filter: /.*/, namespace: 'file' }, async input => ({
          contents: await readInput(relative(root, input.path).split(sep).join('/')),
          loader: 'js', resolveDir: dirname(input.path),
        }));
      },
    }],
  });
  const actualModules = Object.keys(result.metafile.inputs).filter(path => path !== '<stdin>').sort();
  // Output imports reflect runtime dependencies; input metadata also contains esbuild's synthetic helpers.
  const actualBuiltins = [...new Set(Object.values(result.metafile.outputs).flatMap(output =>
    output.imports.filter(value => value.external).map(value => value.path)))].sort();
  if (JSON.stringify(actualModules) !== JSON.stringify([...modulePaths].sort())
      || JSON.stringify(actualBuiltins) !== JSON.stringify([...builtins].sort())
      || Object.values(result.metafile.outputs).some(output => output.imports.some(input =>
        !input.external || !builtins.has(input.path)))) {
    throw new Error('Cat Web value bundle differs from the reviewed pure closure');
  }
  const inputs = await hashInputs([...captured.keys()]);
  for (const input of inputs) {
    if (input.sha256 !== captured.get(input.path)) throw new Error(`Cat Web value input changed during build: ${input.path}`);
  }
  const path = relative(root, resolve(directory, 'cat-web-values.cjs')).split(sep).join('/');
  const bytes = result.outputFiles[0].contents;
  await writeFile(resolve(root, path), bytes);
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
    const catWebCandidates = await catWebValueCandidates();
    const before = await hashInputs([
      'package.json', 'package-lock.json', ...policy.mandatoryInputs,
      'scripts/quickjs/build-ios.mjs', 'scripts/quickjs/ios-target.mjs',
      'scripts/quickjs/build-source-sdk.mjs', 'scripts/prepare-ios-runtime.py',
      'scripts/ios-node-mobile.json', ...nativeInputs, ...sourceInputs, ...sdkCandidates, ...catWebCandidates,
      ...nativeSourceInputs(policy), ...policy.patches.map(patch => patch.path),
      ...mobile.inputs.map(input => input.path),
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
    const catWebValues = await buildCatWebValues(resolve(directory, 'build/Release'));
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
      inputs: before, sdk, catWebValues, nodeMobile: mobile.identity, nodeHeaderFiles: mobile.headers, toolchain,
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
    ].flatMap(path => ['-I', path]);
    const objectDirectory = resolve(directory, 'obj');
    await mkdir(objectDirectory, { recursive: true });
    // C/C++ source membership comes from the same gyp file as the desktop build.
    for (const targetSpec of gyp.targets) {
      for (const input of targetSpec.sources) {
        const source = input.startsWith('<(runtime_native)/')
          ? resolve(root, policy.build.cache, input.slice('<(runtime_native)/'.length))
          : input.startsWith('<(quickjs_source)/')
          ? resolve(root, policy.sources.quickjs.sourcePath, input.slice('<(quickjs_source)/'.length))
          : resolve(root, 'packages/script-host', input);
        const object = resolve(objectDirectory, `${targetSpec.target_name}-${basename(source)}.o`);
        const args = [...flags, ...headers, '-DNAPI_VERSION=8', '-DNODE_GYP_MODULE_NAME=script_host_native',
          '-DMEOW_IOS_FRAMEWORK=1', '-DZ_PREFIX', ...(source.endsWith('.cc')
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
        || JSON.stringify(afterSDK) !== JSON.stringify(sdkCandidates)
        || JSON.stringify(await catWebValueCandidates()) !== JSON.stringify(catWebCandidates)) {
      throw new Error('Native/SDK/Cat Web value membership changed during build');
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
