import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { copyFile, mkdir, readFile, readdir, rename, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dependencyCacheRoot } from '../dependency-download.mjs';
import { root, source, cache, support, sha256, fileHash, inventory, treeHash, prepareFilmKernel } from './prepare-film-kernel.mjs';

const native = path => path.replaceAll('\\', '/');
const json = async path => JSON.parse(await readFile(path, 'utf8'));
const save = async (path, value) => {
  await mkdir(dirname(path), { recursive: true });
  const temporary = `${path}.${process.pid}.tmp`;
  await writeFile(temporary, JSON.stringify(value, null, 2) + '\n');
  await rename(temporary, path);
};
const run = (command, args, options = {}) => execFileSync(command, args, {
  cwd: root, stdio: 'inherit', windowsHide: true, ...options,
});
const text = (command, args, options = {}) => run(command, args, {
  ...options, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
}).trim();
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function find(...names) {
  for (const name of names) {
    if (existsSync(name)) return native(resolve(name));
    try {
      const found = text('where.exe', [name]).split(/\r?\n/)[0];
      if (found && existsSync(found)) return native(found);
    } catch { /* Try the next explicitly named entry point. */ }
  }
  throw new Error(`Missing film-kernel build tool: ${names.join(' / ')}`);
}

async function toolTree(directory, excluded = []) {
  // Python bytecode is an interpreter cache, not a build input; disable its creation below.
  const files = {};
  async function visit(path) {
    for (const entry of await readdir(path, { withFileTypes: true })) {
      if (entry.name === '__pycache__' || /\.(pyc|pyo)$/.test(entry.name)) continue;
      if (excluded.includes(entry.name)) continue;
      const full = join(path, entry.name);
      if (entry.isSymbolicLink()) throw new Error(`Unresolved toolchain link: ${full}`);
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile()) files[native(full.slice(directory.length + 1))] = await fileHash(full);
    }
  }
  await visit(directory);
  return Object.fromEntries(Object.entries(files).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
}

async function binaryInputs(tool, executables, search) {
  const files = {}, pending = [...executables];
  while (pending.length) {
    const path = native(pending.pop());
    if (files[path]) continue;
    files[path] = await fileHash(path);
    if (!/\.(exe|dll|pyd)$/i.test(path)) continue;
    const imports = text(tool.readobj, ['--coff-imports', path]);
    for (const match of imports.matchAll(/^\s+Name: ([^\r\n]+\.dll)\s*$/gmi)) {
      const local = [dirname(path), ...search].map(folder => join(folder, match[1])).find(existsSync);
      // Windows system runtime DLLs belong to the OS, not a private SDK installation.
      if (local) pending.push(local);
    }
  }
  return Object.fromEntries(Object.entries(files).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
}

async function tools(python, pkgConfig, lock) {
  const home = 'E:/Developer/tools';
  const llvm = `${home}/llvm-mingw-20261104-ucrt-x86_64`;
  const bin = `${llvm}/bin`;
  const result = { python: native(python), llvm };
  for (const [key, executable] of Object.entries({
    cc: 'x86_64-w64-mingw32-clang.exe', cxx: 'x86_64-w64-mingw32-clang++.exe',
    ar: 'llvm-ar.exe', ranlib: 'llvm-ranlib.exe', nm: 'llvm-nm.exe',
    strip: 'llvm-strip.exe', windres: 'x86_64-w64-mingw32-windres.exe', readobj: 'llvm-readobj.exe',
  })) result[key] = find(`${bin}/${executable}`);
  result.cmake = find(`${home}/cmake-4.4.5-windows-x86_64/bin/cmake.exe`, 'cmake.exe');
  result.ninja = find(`${home}/ninja/ninja.exe`, 'ninja.exe');
  result.make = find('make.exe', 'mingw32-make.exe');
  result.nasm = find('nasm.exe');
  result.git = find('git.exe');
  result.bash = find(join(dirname(dirname(result.git)), 'bin/bash.exe'));
  result.pkgconfig = pkgConfig ? find(pkgConfig) : find('pkgconf.exe', 'pkg-config.exe', 'pkg-config.bat');
  if (result.pkgconfig.endsWith('.pl')) throw new Error('Pass the official pkg-config.bat entry point or native pkgconf.exe to CMake');
  result.pkgPerl = result.pkgconfig.endsWith('.bat') ? find('perl.exe') : '';
  result.perl = result.pkgPerl;
  result.meson = find(`${home}/meson-${lock.tools.meson}/meson.py`, 'meson.exe');
  result.mesonPython = result.meson.endsWith('.py');
  const mesonCommand = result.mesonPython ? result.python : result.meson;
  const mesonArgs = result.mesonPython ? ['-B', result.meson] : [];
  const versions = {
    python: text(python, ['--version']),
    clang: text(result.cc, ['--version']),
    cmake: text(result.cmake, ['--version']),
    ninja: text(result.ninja, ['--version']),
    make: text(result.make, ['--version']),
    nasm: text(result.nasm, ['-v']),
    meson: text(mesonCommand, [...mesonArgs, '--version']),
    perl: result.perl ? text(result.perl, ['-e', 'print $^V']) : null,
    pkgconfig: text(result.pkgPerl || result.pkgconfig,
      result.pkgPerl ? [...(result.pkgconfig.endsWith('.bat') ? ['-x'] : []), result.pkgconfig, '--version'] : ['--version']),
    node: process.version,
  };
  if (versions.meson !== lock.tools.meson) throw new Error(`Meson ${lock.tools.meson} required, found ${versions.meson}`);
  if (!versions.cmake.startsWith('cmake version 4.4.5')) throw new Error('Use the prepared CMake 4.4.5 installation');
  if (!versions.python.startsWith('Python 3.14.')) throw new Error('Use the prepared Python 3.14 installation');
  if (!versions.nasm.includes(lock.tools.nasm)) throw new Error(`NASM ${lock.tools.nasm} required`);
  if (!versions.make.includes('GNU Make')) throw new Error('GNU make is required');
  if (!process.version.startsWith('v24.')) throw new Error('Node 24 is required');
  if (!text(result.cc, ['-dumpmachine']).includes('x86_64-w64-windows-gnu') &&
      !text(result.cc, ['-dumpmachine']).includes('x86_64-w64-mingw32')) throw new Error('Not the x64 LLVM-MinGW compiler');
  const installations = { [llvm]: await toolTree(llvm) };
  const cmakeShare = join(dirname(dirname(result.cmake)), 'share');
  const cmakeModules = (await readdir(cmakeShare)).find(name => /^cmake-\d/.test(name));
  if (!cmakeModules) throw new Error('CMake module installation is missing');
  const moduleDirectory = join(cmakeShare, cmakeModules, 'Modules');
  installations[native(moduleDirectory)] = await toolTree(moduleDirectory);
  const pythonPaths = JSON.parse(text(python, ['-B', '-c',
    'import json,sys,sysconfig; print(json.dumps([sys.base_prefix,sysconfig.get_path("stdlib")]))']));
  installations[native(pythonPaths[1])] = await toolTree(pythonPaths[1],
    ['site-packages', 'test', 'tests', 'idlelib', 'tkinter', 'turtledemo', 'ensurepip']);
  const pythonDlls = join(pythonPaths[0], 'DLLs');
  if (existsSync(pythonDlls)) installations[native(pythonDlls)] = await toolTree(pythonDlls);
  const mesonRoot = result.mesonPython ? dirname(result.meson) :
    text(python, ['-B', '-c', 'import mesonbuild,pathlib; print(pathlib.Path(mesonbuild.__file__).parent)']);
  installations[native(mesonRoot)] = await toolTree(mesonRoot,
    ['test cases', 'test cases common', 'tests', 'docs', 'manual', '.git']);
  const gitRoot = dirname(dirname(result.git));
  const gitBin = join(gitRoot, 'usr/bin');
  const commands = ['bash', 'sh', 'awk', 'basename', 'cat', 'chmod', 'cmp', 'cp', 'cut', 'diff',
    'dirname', 'expr', 'find', 'grep', 'gzip', 'head', 'install', 'ln', 'ls', 'mkdir', 'mv',
    'od', 'printf', 'pwd', 'realpath', 'rm', 'sed', 'sort', 'tail', 'tee', 'touch', 'tr',
    'uname', 'wc', 'xargs'].map(name => join(gitBin, `${name}.exe`)).filter(existsSync);
  const binaryFiles = [process.execPath, python, result.git, result.bash, result.cmake, result.ninja,
    result.make, result.nasm, result.pkgconfig, ...commands];
  if (result.perl) binaryFiles.push(result.perl);
  if (!result.mesonPython) binaryFiles.push(result.meson);
  // Python's runtime DLL is found beside python.exe; include non-imported stable-ABI loaders too.
  for (const entry of await readdir(pythonPaths[0]))
    if (/^(python.*\.(dll|zip)|pyvenv\.cfg)$/i.test(entry)) binaryFiles.push(join(pythonPaths[0], entry));
  if (result.pkgPerl) {
    const probe = join(cache, 'tool-probe');
    await mkdir(probe, { recursive: true });
    const module = join(probe, 'MeowFilmPkgInputs.pm'), resultFile = join(probe, 'perl-inputs.txt');
    await writeFile(module, `package MeowFilmPkgInputs;
END { open my $f, '>', $ENV{'MFK_PKG_INPUTS'} or die $!;
print $f join("\\n", sort grep { -f $_ } (values %INC, @DynaLoader::dl_shared_objects)); }
1;
`);
    const env = { ...process.env, PERL5LIB: '', PERL5OPT: `-I${native(probe)} -MMeowFilmPkgInputs`,
      MFK_PKG_INPUTS: resultFile };
    text(result.pkgPerl, ['-x', result.pkgconfig, '--version'], { env });
    for (const path of (await readFile(resultFile, 'utf8')).split(/\r?\n/))
      if (path && native(path) !== native(module)) binaryFiles.push(path);
  }
  installations.binaries = await binaryInputs(result, binaryFiles,
    [gitBin, join(gitRoot, 'mingw64/bin'), dirname(python), ...(result.perl ? [dirname(result.perl)] : [])]);
  return { ...result, versions, installations };
}

function environment(tool, prefix) {
  const env = { ...process.env };
  for (const name of ['CC', 'CXX', 'AR', 'LD', 'CL', '_CL_', 'LINK', 'CFLAGS', 'CPPFLAGS',
    'CXXFLAGS', 'LDFLAGS', 'LIB', 'INCLUDE', 'LIBRARY_PATH', 'CPATH', 'CMAKE_PREFIX_PATH',
    'PKG_CONFIG_PATH', 'PKG_CONFIG_LIBDIR', 'PYTHONPATH', 'PERL5OPT', 'PERL5LIB']) delete env[name];
  env.PATH = [...new Set([`${tool.llvm}/bin`, dirname(tool.cmake), dirname(tool.ninja),
    dirname(tool.python), dirname(tool.nasm), ...(tool.perl ? [dirname(tool.perl)] : []), dirname(tool.make),
    `${dirname(dirname(tool.git))}/usr/bin`, `${dirname(dirname(tool.git))}/bin`,
    `${process.env.SystemRoot}/System32`, process.env.SystemRoot])].join(';');
  env.PYTHONDONTWRITEBYTECODE = '1';
  env.PKG_CONFIG_PATH = `${prefix}/lib/pkgconfig`;
  env.PKG_CONFIG_LIBDIR = env.PKG_CONFIG_PATH;
  env.PKG_CONFIG_SYSTEM_INCLUDE_PATH = '';
  env.PKG_CONFIG_SYSTEM_LIBRARY_PATH = '';
  for (const [key, value] of Object.entries(tool))
    if (typeof value === 'string') env[`MFK_${key.toUpperCase()}`] = value;
  env.MFK_PKG_CONFIG = tool.pkgconfig;
  env.MFK_PKG_PERL = tool.pkgPerl;
  env.MFK_PYTHON = tool.python;
  return env;
}

const packages = ['libavformat', 'libavcodec', 'libavutil', 'libavfilter',
  'libswresample', 'libswscale', 'libplacebo', 'libass'];
const basePackages = ['libplacebo', 'libass', 'dav1d', 'libxml-2.0', 'zlib', 'bzip2', 'liblzma'];

async function checkPrefix(prefix, build, kind) {
  const files = await inventory(prefix);
  if (Object.keys(files).some(path => /\.(dll|exe)$/i.test(path))) throw new Error('Dependencies must be static-only');
  for (const name of kind === 'base' ? basePackages : packages)
    if (!files[`lib/pkgconfig/${name}.pc`]) throw new Error(`Missing dependency package: ${name}`);
  for (const name of Object.keys(files).filter(path => path.endsWith('.pc'))) {
    const content = native(await readFile(join(prefix, name), 'utf8')).toLowerCase();
    if (content.includes(native(build).toLowerCase()) || content.includes('/.cache/windows-film-kernel/'))
      throw new Error(`Private build path leaked into shared pkg-config: ${name}`);
  }
  return files;
}

function mesonNativeFile(tool, prefix) {
  const quote = value => `'${native(value).replaceAll("'", "\\'")}'`;
  const array = value => `[${value.map(quote).join(', ')}]`;
  const pkgCommand = tool.pkgPerl ? [tool.pkgPerl, ...(tool.pkgconfig.endsWith('.bat') ? ['-x'] : []),
    tool.pkgconfig] : [tool.pkgconfig];
  return `[binaries]
c = ${quote(tool.cc)}
cpp = ${quote(tool.cxx)}
ar = ${quote(tool.ar)}
strip = ${quote(tool.strip)}
windres = ${quote(tool.windres)}
pkg-config = ${array(pkgCommand)}
cmake = ${quote(tool.cmake)}
python = ${quote(tool.python)}
nasm = ${quote(tool.nasm)}

[built-in options]
c_args = ${array([`-I${prefix}/include`, '-D_WIN32_WINNT=0x0A00'])}
cpp_args = ${array([`-I${prefix}/include`, '-D_WIN32_WINNT=0x0A00'])}
c_link_args = ${array([`-L${prefix}/lib`, '-static'])}
cpp_link_args = ${array([`-L${prefix}/lib`, '-static', '-static-libgcc', '-static-libstdc++'])}
`;
}

async function dependencyBuild(jobFile) {
  const job = await json(jobFile);
  const { prefix, build, tool, identity, inputs, kind } = job;
  const env = environment(tool, prefix);
  for (const [name, path] of [['cmake.exe', tool.cmake], ['ninja.exe', tool.ninja]]) {
    const actual = native(text('where.exe', [name], { env }).split(/\r?\n/)[0]);
    if (actual.toLowerCase() !== native(path).toLowerCase())
      throw new Error(`Build environment selected an unexpected ${name}: ${actual}`);
  }
  const receiptPath = join(dirname(prefix), 'dependency-receipt.json');
  if (existsSync(receiptPath)) {
    const receipt = await json(receiptPath);
    if (receipt.identity !== identity || !same(receipt.files, await checkPrefix(prefix, build, kind)))
      throw new Error('Shared dependency receipt or installed files changed');
    return;
  }
  // Only an incomplete identity-specific prefix is writable, under its OS lock.
  await mkdir(prefix, { recursive: true });
  if (kind === 'ffmpeg') {
    const baseReceipt = await json(join(dirname(job.basePrefix), 'dependency-receipt.json'));
    if (baseReceipt.identity !== inputs.base || !same(baseReceipt.files, await checkPrefix(job.basePrefix, build, 'base')))
      throw new Error('Base dependency inputs changed before FFmpeg build');
    // Compose a new immutable prefix from already verified static products, never rebuild them.
    for (const [path, hash] of Object.entries(baseReceipt.files)) {
      const target = join(prefix, path);
      if (existsSync(target)) {
        if (await fileHash(target) !== hash) throw new Error(`Incomplete composed prefix changed: ${path}`);
      } else {
        await mkdir(dirname(target), { recursive: true });
        await copyFile(join(job.basePrefix, path), target);
      }
    }
  }
  await mkdir(join(build, 'compression-project'), { recursive: true });
  await copyFile(join(root, 'scripts/windows/build-film-kernel-compression.cmake'),
    join(build, 'compression-project/CMakeLists.txt'));
  await writeFile(join(build, 'native.ini'), mesonNativeFile(tool, prefix));
  run(tool.bash, [native(join(root, `scripts/windows/build-film-kernel-${kind === 'base' ? 'base' : 'deps'}.sh`)),
    job.sources, build, prefix, `${tool.llvm}/bin`, String(job.jobs), job.configuration],
  { env });
  const sourceInputs = kind === 'base' ? inputs.sources : { ffmpeg: inputs.source };
  for (const [name, item] of Object.entries(sourceInputs))
    if (await treeHash(join(job.sources, name)) !== item.preparedTreeSHA256)
      throw new Error(`Source inputs changed during dependency build: ${name}`);
  const files = await checkPrefix(prefix, build, kind);
  const command = tool.pkgPerl || tool.pkgconfig;
  const args = [...(tool.pkgPerl ? ['-x', tool.pkgconfig] : []), '--static', '--cflags', '--libs',
    ...(kind === 'base' ? basePackages : packages)];
  const staticFlags = text(command, args, { env });
  await save(receiptPath, { schema: 1, identity, inputs, files, staticFlags });
}

function inspectDll(tool, dll) {
  const output = text(tool.readobj, ['--file-headers', '--coff-imports', '--coff-exports', dll]);
  if (!output.includes('IMAGE_FILE_MACHINE_AMD64')) throw new Error('Film kernel is not x64 PE');
  const imports = [...output.matchAll(/^\s+Name: ([^\r\n]+\.dll)\s*$/gmi)].map(match => match[1].toLowerCase()).sort();
  const allowed = new Set(['kernel32.dll', 'user32.dll', 'gdi32.dll', 'advapi32.dll', 'bcrypt.dll',
    'ole32.dll', 'oleaut32.dll', 'shell32.dll', 'shlwapi.dll', 'ws2_32.dll', 'winmm.dll',
    'avrt.dll', 'version.dll', 'd3d11.dll', 'dxgi.dll', 'd3dcompiler_47.dll',
    'mfplat.dll', 'mf.dll', 'mfuuid.dll', 'ucrtbase.dll', 'ntdll.dll', 'comdlg32.dll']);
  if (!imports.length || imports.some(name => !allowed.has(name) && !name.startsWith('api-ms-win-')))
    throw new Error(`Unexpected non-system DLL imports: ${imports.join(', ')}`);
  for (const symbol of ['mfk_build_identity', 'mfk_create', 'mfk_release'])
    if (!output.includes(`Name: ${symbol}\n`) && !output.includes(`Name: ${symbol}\r\n`))
      throw new Error(`Missing C ABI export: ${symbol}`);
  return { imports, inspection: sha256(output) };
}

async function build(python, configuration, jobs, pkgConfig, baseOnly) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Film kernel build requires Windows x64');
  if (!['Release', 'Debug'].includes(configuration) || !Number.isInteger(jobs) || jobs < 1)
    throw new Error('Invalid film-kernel build configuration');
  process.env.PYTHONDONTWRITEBYTECODE = '1';
  const tool = await tools(python, pkgConfig, await json(join(source, 'film-kernel.lock.json')));
  const prepared = await prepareFilmKernel(python);
  const recipes = {};
  for (const name of (await readdir(join(root, 'scripts/windows'))).sort())
    if (/^(prepare-film-kernel\.mjs|build-film-kernel[.-])/.test(name))
      recipes[`scripts/windows/${name}`] = await fileHash(join(root, 'scripts/windows', name));
  recipes['scripts/dependency-download.mjs'] = await fileHash(join(root, 'scripts/dependency-download.mjs'));
  const commonInputs = {
    target: prepared.lock.target, configuration,
    configurationRecipe: sha256(mesonNativeFile.toString()),
    environmentRecipe: sha256(environment.toString()),
    tool,
  };
  const baseInputs = {
    ...commonInputs,
    sources: Object.fromEntries(Object.entries(prepared.lock.sources).filter(([name]) => name !== 'ffmpeg')),
    recipes: Object.fromEntries(['scripts/windows/build-film-kernel-base.sh',
      'scripts/windows/build-film-kernel-common.sh', 'scripts/windows/build-film-kernel-compression.cmake']
      .map(path => [path, recipes[path]])),
  };
  async function ensureDependencies(kind, inputs, basePrefix) {
    const identity = sha256(JSON.stringify(inputs));
    const prefix = native(join(dependencyCacheRoot(), 'windows-film-kernel', identity, 'prefix'));
    // Meson regeneration paths retain both the build and source components.
    // Keep the private path short; the shared receipt still binds the full hash.
    const privateBuild = native(join(cache, 'dependencies', identity.slice(0, 16)));
    const jobFile = join(cache, `${kind}-dependency-job.json`);
    await save(jobFile, { kind, identity, inputs, prefix, build: privateBuild,
      sources: native(prepared.sources), jobs, configuration, tool, basePrefix });
    run(python, ['-B', support, 'lock-run', join(dirname(prefix), 'build.lock'), process.execPath,
      fileURLToPath(import.meta.url), '--dependencies', jobFile]);
    return { identity, prefix };
  }
  const base = await ensureDependencies('base', baseInputs);
  if (baseOnly) {
    await save(join(cache, 'base-dependencies.json'), base);
    console.log(`Prepared base dependencies ${base.identity}\n${base.prefix}`);
    return;
  }
  if (existsSync(join(source, 'ffmpeg-dovi.patch')) &&
      !prepared.lock.inputs['apps/windows/native/FilmKernel/ffmpeg-dovi.patch'])
    throw new Error('ffmpeg-dovi.patch is not yet sealed in film-kernel.lock.json; base dependencies are ready');
  const inputs = {
    ...commonInputs, base: base.identity, source: prepared.lock.sources.ffmpeg,
    recipes: Object.fromEntries(['scripts/windows/build-film-kernel-deps.sh',
      'scripts/windows/build-film-kernel-common.sh'].map(path => [path, recipes[path]])),
  };
  const dependencies = sha256(JSON.stringify(inputs));
  const { prefix } = await ensureDependencies('ffmpeg', inputs, base.prefix);
  for (const [path, hash] of Object.entries(recipes))
    if (await fileHash(join(root, path)) !== hash) throw new Error(`Build recipe changed during dependency build: ${path}`);
  for (const [path, hash] of Object.entries(prepared.lock.inputs))
    if (await fileHash(join(root, path)) !== hash) throw new Error(`Source patch input changed during dependency build: ${path}`);
  const nativeInputs = await inventory(source);
  const identity = sha256(JSON.stringify({ dependencies, nativeInputs, recipes }));
  const output = join(cache, 'output');
  const dll = join(output, 'meowfilm_film_kernel.dll');
  const managed = join(output, 'FilmKernelBuild.g.cs');
  const receiptPath = join(output, 'build-receipt.json');
  if (existsSync(receiptPath)) {
    const previous = await json(receiptPath);
    if (previous.identity === identity && existsSync(dll) && existsSync(managed) && previous.dllSha256 === await fileHash(dll) &&
        previous.managedSha256 === await fileHash(managed)) {
      inspectDll(tool, dll);
      console.log(`Reused FilmKernel ${identity}`);
      return;
    }
  }
  const directory = native(join(cache, 'native', identity.slice(0, 16)));
  const env = environment(tool, prefix);
  run(tool.cmake, ['-S', native(source), '-B', directory, '-G', 'Ninja',
    `-DCMAKE_MAKE_PROGRAM=${tool.ninja}`, `-DCMAKE_C_COMPILER=${tool.cc}`,
    `-DCMAKE_CXX_COMPILER=${tool.cxx}`, `-DCMAKE_RC_COMPILER=${tool.windres}`,
    `-DCMAKE_BUILD_TYPE=${configuration}`, `-DDEPS_PREFIX=${prefix}`,
    `-DFFMPEG_PREFIX=${prefix}`, `-DPLACEBO_PREFIX=${prefix}`, `-DASS_PREFIX=${prefix}`,
    `-DCMAKE_PREFIX_PATH=${prefix}`, `-DBUILD_IDENTITY=${identity}`,
    `-DPKG_CONFIG_EXECUTABLE=${tool.pkgconfig}`, '-DPKG_CONFIG_ARGN=--static',
    '-DCMAKE_SHARED_LINKER_FLAGS=-static -static-libgcc -static-libstdc++',
    `-DCMAKE_RUNTIME_OUTPUT_DIRECTORY=${directory}/bin`], { env });
  run(tool.cmake, ['--build', directory, '--parallel', String(jobs)], { env });
  const built = join(directory, 'bin/meowfilm_film_kernel.dll');
  const pe = inspectDll(tool, built);
  if (!same(nativeInputs, await inventory(source))) throw new Error('Native inputs changed during build');
  for (const [path, hash] of Object.entries(recipes))
    if (await fileHash(join(root, path)) !== hash) throw new Error(`Build recipe changed during native build: ${path}`);
  const dllSha256 = await fileHash(built);
  await mkdir(output, { recursive: true });
  await copyFile(built, dll);
  await writeFile(managed, `// Generated by build-film-kernel.ps1; do not edit.
namespace MeowFilm.Media;
internal static class FilmKernelBuild
{
    internal const string Identity = "${identity}";
    internal const string DllSha256 = "${dllSha256}";
}
`);
  await save(receiptPath, { schema: 1, identity, dependencies, prefix, nativeInputs, inputs, recipes,
    dllSha256, managedSha256: await fileHash(managed), ...pe });
  console.log(`Built FilmKernel ${identity}\n${dll}`);
}

if (process.argv[2] === '--dependencies') await dependencyBuild(process.argv[3]);
else {
  const args = process.argv.slice(2);
  const baseOnly = args.includes('--base-only');
  const pkgConfig = args[3]?.startsWith('--') ? '' : args[3] ?? '';
  await build(args[0], args[1] ?? 'Release', Number(args[2] ?? 1), pkgConfig, baseOnly);
}
