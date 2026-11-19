#!/usr/bin/env python3
"""Prepare locked sources and an isolated dynamic Apple audio decoder framework."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import plistlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / '.cache/ios-audio-kernel'
SHARED = Path.home() / 'Developer/cache/ffmpeg'
PACKAGE = ROOT / 'native/AudioKernel/.cache'
LOCK_PATH = ROOT / 'scripts/ios-audio-kernel.json'
ADAPTER = ROOT / 'native/AudioKernel/Sources/CMeowAudioKernel'
FRAMEWORK = 'CMeowAudioKernel'


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def identity(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')
    temporary.replace(path)


def inventory(directory):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError('missing or linked artifact directory')
    result = {}
    for path in directory.rglob('*'):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError('non-regular artifact entry')
        if path.is_file():
            result[path.relative_to(directory).as_posix()] = digest(path)
    return result


def verify_inventory(directory, expected):
    if not expected or inventory(directory) != expected:
        raise ValueError('artifact file inventory/hash mismatch')


def source_tree_hash(directory):
    """Same path-NUL-bytes-NUL depth-first order as the Windows source lock."""
    value = hashlib.sha256()
    def visit(folder):
        for path in sorted(folder.iterdir(), key=lambda path: path.name):
            if path.is_symlink():
                raise ValueError('linked source input')
            if path.is_dir():
                visit(path)
            elif path.is_file():
                value.update(path.relative_to(directory).as_posix().encode())
                value.update(b'\0')
                with path.open('rb') as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b''):
                        value.update(block)
                value.update(b'\0')
            else:
                raise ValueError('non-regular source input')
    visit(directory)
    return value.hexdigest()


def run(command, cwd=None, log=None):
    if log:
        log.parent.mkdir(parents=True, exist_ok=True)
        with log.open('a') as stream:
            stream.write('\n' + ' '.join(map(str, command)) + '\n')
            stream.flush()
            subprocess.run(command, cwd=cwd, stdout=stream, stderr=stream, check=True)
    else:
        return subprocess.check_output(command, cwd=cwd, text=True).strip()


@contextmanager
def shared_lock(name):
    path = SHARED / 'locks' / (name + '.lock')
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a') as guard:
        fcntl.flock(guard, fcntl.LOCK_EX)
        yield


def read_lock():
    lock = json.loads(LOCK_PATH.read_text())
    if (lock['schema'] != 1 or lock['source']['version'] != '9.0.1' or
            [entry['name'] for entry in lock['libraries']] != ['avutil', 'swresample', 'avcodec', 'avformat']):
        raise ValueError('unsupported audio source lock')
    return lock


def archive_for(source):
    path = SHARED / 'downloads' / (source['sha256'] + '.tar.xz')
    path.parent.mkdir(parents=True, exist_ok=True)
    with shared_lock('download-' + source['sha256']):
        if not path.exists():
            partial = path.with_suffix('.partial')
            # Only this download uses a proxy. No global proxy/environment edits.
            proxies = [{'http': 'http://10.0.0.1:7890', 'https': 'http://10.0.0.1:7890'}, {}]
            if platform.system() != 'Darwin':
                proxies = [{}]
            try:
                for index, proxy in enumerate(proxies):
                    try:
                        opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxy))
                        request = urllib.request.Request(source['url'], headers={'User-Agent': 'Meowfilm-source-prepare'})
                        with opener.open(request, timeout=60) as response, partial.open('wb') as stream:
                            for block in iter(lambda: response.read(1024 * 1024), b''):
                                stream.write(block)
                        break
                    except OSError:
                        if index == len(proxies) - 1:
                            raise
                if digest(partial) != source['sha256']:
                    raise ValueError('audio source archive hash mismatch')
                partial.replace(path)
            finally:
                partial.unlink(missing_ok=True)
        if path.is_symlink() or digest(path) != source['sha256']:
            raise ValueError('audio source archive hash mismatch')
    return path


def verify_source(directory, lock):
    if directory.is_symlink() or source_tree_hash(directory) != lock['source']['treeSha256']:
        raise ValueError('official audio source tree mismatch')
    if (directory / 'RELEASE').read_text().strip() != lock['source']['version']:
        raise ValueError('audio release mismatch')
    for entry in lock['libraries']:
        headers = ''.join((directory / ('lib' + entry['name']) / file).read_text()
                          for file in ('version.h', 'version_major.h'))
        for field in ('major', 'minor', 'micro'):
            match = re.search(r'^#define LIB' + entry['name'].upper() + '_VERSION_' + field.upper() + r'\s+(\d+)\s*$', headers, re.M)
            if not match or int(match[1]) != entry[field]:
                raise ValueError('audio library ABI mismatch')
    for source, license_file in [('LICENSE.md', 'FFmpeg-LICENSE.md'), ('COPYING.LGPLv2.1', 'LGPL-2.1-only.txt')]:
        if digest(directory / source) != digest(ROOT / 'LICENSES' / license_file):
            raise ValueError('audio source license mismatch')
    return {'archive': lock['source']['sha256'], 'tree': lock['source']['treeSha256']}


def prepare_sources(lock):
    archive = archive_for(lock['source'])
    directory = SHARED / 'sources' / lock['source']['sha256']
    with shared_lock('source-' + lock['source']['sha256']):
        if not directory.exists():
            directory.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix='audio-source-', dir=directory.parent) as temporary:
                staged = Path(temporary) / 'source'
                staged.mkdir()
                with tarfile.open(archive, 'r:xz') as contents:
                    for member in contents:
                        parts = PurePosixPath(member.name).parts
                        if member.name.startswith('/') or '..' in parts or member.issym() or member.islnk():
                            raise ValueError('unsafe source archive member')
                        if member.isdir():
                            continue
                        if not member.isfile() or len(parts) < 2 or parts[0] != 'ffmpeg-' + lock['source']['version']:
                            raise ValueError('non-file or unexpected source archive member')
                        target = staged.joinpath(*parts[1:])
                        target.parent.mkdir(parents=True, exist_ok=True)
                        if target.exists():
                            raise ValueError('duplicate source archive member')
                        with contents.extractfile(member) as stream, target.open('wb') as output:
                            shutil.copyfileobj(stream, output)
                        target.chmod(0o755 if member.mode & 0o111 else 0o644)
                verify_source(staged, lock)
                staged.rename(directory)
        sources = verify_source(directory, lock)
    write_json(CACHE / 'source-receipt.json', sources)
    return directory, sources


def toolchain(target):
    sdk = Path(run(['xcrun', '--sdk', target['sdk'], '--show-sdk-path']))
    tools = {name: run(['xcrun', '--sdk', target['sdk'], '--find', name]) for name in ('clang', 'ar', 'ranlib', 'lipo', 'nm', 'otool')}
    return {'sdkPath': str(sdk), 'sdkVersion': run(['xcrun', '--sdk', target['sdk'], '--show-sdk-version']),
            'sdkSettings': {file: digest(sdk / file) for file in ('SDKSettings.json', 'SDKSettings.plist') if (sdk / file).is_file()},
            'xcode': run(['xcodebuild', '-version']), 'tools': tools,
            'toolHashes': {name: digest(Path(path)) for name, path in tools.items()},
            'clangVersion': run([tools['clang'], '--version']),
            'environment': {key: os.environ[key] for key in ('CFLAGS', 'CPPFLAGS', 'CXXFLAGS', 'ASFLAGS', 'LDFLAGS', 'ARFLAGS',
                            'CPATH', 'C_INCLUDE_PATH', 'LIBRARY_PATH', 'MACOSX_DEPLOYMENT_TARGET',
                            'IPHONEOS_DEPLOYMENT_TARGET', 'SOURCE_DATE_EPOCH', 'ZERO_AR_DATE') if key in os.environ}}


def flags_for(target, tools):
    return ['-target', target['triple'], '-isysroot', tools['sdkPath']]


def version_tuple(value):
    values = tuple(map(int, value.split('.')))
    return values + (0,) * (3 - len(values))


def ffmpeg_inputs(target, tools, sources, lock):
    return {'lockSHA256': digest(LOCK_PATH), 'prepareSHA256': digest(Path(__file__)),
            'source': sources, 'target': target, 'toolchain': tools, 'configure': lock['configure']}


def adapter_inputs():
    return {'sources': inventory(ADAPTER), 'packageSHA256': digest(ROOT / 'native/AudioKernel/Package.swift')}


def verify_libraries(prefix, receipt, lock, tools, target):
    verify_inventory(prefix, receipt['files'])
    wanted = {'lib/lib' + entry['name'] + '.a' for entry in lock['libraries']}
    actual = {file for file in receipt['files'] if file.startswith('lib/')}
    if actual != wanted or not any(file.startswith('include/') for file in receipt['files']):
        raise ValueError('incomplete audio static libraries/headers')
    for file in wanted:
        if run([tools['tools']['lipo'], '-archs', str(prefix / file)]).split() != [target['arch']]:
            raise ValueError('audio archive architecture mismatch')


def build_ffmpeg(name, target, tools, source, sources, lock):
    inputs = ffmpeg_inputs(target, tools, sources, lock)
    fingerprint = identity(inputs)
    shared = SHARED / 'artifacts' / ('apple-audio-' + fingerprint)
    build = CACHE / 'build' / name / 'ffmpeg'
    with shared_lock('build-' + fingerprint):
        if shared.exists():
            receipt = json.loads((shared / 'receipt.json').read_text())
            if receipt.get('inputs') != inputs or receipt.get('fingerprint') != fingerprint:
                raise ValueError('shared audio recipe mismatch')
            verify_libraries(shared / 'install', receipt, lock, tools, target)
            return shared / 'install', receipt
        configured = build / 'configuration.json'
        previous = json.loads(configured.read_text()) if configured.exists() else {}
        if previous != inputs and build.exists():
            shutil.rmtree(build)
        build.mkdir(parents=True, exist_ok=True)
        prefix = build / 'install'
        flags = ' '.join(flags_for(target, tools))
        command = [str(source / 'configure'), *lock['configure'], '--prefix=' + str(prefix), '--target-os=darwin',
                   '--arch=' + target['arch'], '--enable-cross-compile', '--cc=' + tools['tools']['clang'],
                   '--ar=' + tools['tools']['ar'], '--ranlib=' + tools['tools']['ranlib'],
                   '--extra-cflags=' + flags, '--extra-ldflags=' + flags]
        if target['arch'] == 'x86_64':
            command += ['--disable-x86asm']
        log = CACHE / 'logs' / (name + '-ffmpeg.log')
        if previous != inputs or not (build / 'config.h').exists():
            run(command, cwd=build, log=log)
            write_json(configured, inputs)
        configuration = (build / 'config.h').read_text() + (build / 'config_components.h').read_text()
        enabled = set(re.findall(r'^#define CONFIG_(\w+) 1$', configuration, re.M))
        required = {'AVFORMAT', 'AVCODEC', 'AVUTIL', 'SWRESAMPLE', 'HLS_DEMUXER', 'MPEGTS_DEMUXER', 'HTTP_PROTOCOL', 'TCP_PROTOCOL', 'CRYPTO_PROTOCOL'}
        if not required <= enabled or enabled & {'GPL', 'VERSION3', 'NONFREE', 'AVFILTER', 'SWSCALE', 'AVDEVICE'}:
            raise ValueError('audio FFmpeg component boundary mismatch')
        for argument, suffix in [('--enable-decoder=', '_DECODER'), ('--enable-demuxer=', '_DEMUXER'), ('--enable-parser=', '_PARSER')]:
            names = next(value[len(argument):] for value in lock['configure'] if value.startswith(argument)).split(',')
            if {item for item in enabled if item.endswith(suffix)} != {item.upper() + suffix for item in names}:
                raise ValueError('audio FFmpeg codec/format set mismatch')
        run(['make', '-j2'], cwd=build, log=log)
        run(['make', 'install'], cwd=build, log=log)
        shared.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='publish-audio-', dir=shared.parent) as temporary:
            staged = Path(temporary) / 'artifact'
            shutil.copytree(prefix / 'include', staged / 'install/include')
            (staged / 'install/lib').mkdir()
            for entry in lock['libraries']:
                filename = 'lib' + entry['name'] + '.a'
                shutil.copyfile(prefix / 'lib' / filename, staged / 'install/lib' / filename)
            receipt = {'inputs': inputs, 'fingerprint': fingerprint, 'files': inventory(staged / 'install')}
            verify_libraries(staged / 'install', receipt, lock, tools, target)
            write_json(staged / 'receipt.json', receipt)
            staged.rename(shared)
        return shared / 'install', receipt


def framework_metadata(target):
    platforms = {'macosx': 'MacOSX', 'iphoneos': 'iPhoneOS', 'iphonesimulator': 'iPhoneSimulator'}
    return {'CFBundleExecutable': FRAMEWORK, 'CFBundleIdentifier': 'app.meowfilm.audio-kernel',
            'CFBundleName': FRAMEWORK, 'CFBundlePackageType': 'FMWK', 'CFBundleVersion': '1',
            'CFBundleShortVersionString': '1.0', 'CFBundleSupportedPlatforms': [platforms[target['sdk']]],
            'MinimumOSVersion': target['minimum']}


def verify_framework(path, target, tools, lock, architectures=None):
    binary = path / FRAMEWORK
    expected_archs = sorted(architectures or [target['arch']])
    if sorted(run([tools['tools']['lipo'], '-archs', str(binary)]).split()) != expected_archs:
        raise ValueError('native framework architecture mismatch')
    if (path / 'Headers/MeowAudioKernel.h').read_bytes() != (ADAPTER / 'include/MeowAudioKernel.h').read_bytes():
        raise ValueError('native framework ABI header mismatch')
    if plistlib.loads((path / 'Info.plist').read_bytes()) != framework_metadata(target):
        raise ValueError('native framework metadata mismatch')
    module = 'framework module CMeowAudioKernel {\n  umbrella header "MeowAudioKernel.h"\n  export *\n}\n'
    if (path / 'Modules/module.modulemap').read_text() != module:
        raise ValueError('native framework module mismatch')
    for arch in expected_archs:
        # An export list makes other archive globals private externs. Apple nm
        # -g may still print those, so inspect symbol visibility explicitly.
        listing = run([tools['tools']['nm'], '-arch', arch, '-m', '-g', '-U', str(binary)])
        symbols = []
        for line in listing.splitlines():
            if 'private external' not in line:
                symbols += re.findall(r'\bexternal\s+(_\S+)', line)
        if set(symbols) != {'_' + name for name in lock['exports']} or len(symbols) != len(lock['exports']):
            raise ValueError('native framework exports exceed the C ABI')
        loads = run([tools['tools']['otool'], '-arch', arch, '-l', str(binary)])
        header = run([tools['tools']['otool'], '-arch', arch, '-hv', str(binary)])
        if not re.search(r'\bDYLIB\b', header) or not re.search(r'\bTWOLEVEL\b', header):
            raise ValueError('native framework is not a two-level dynamic library')
        wanted_platform = {'macosx': '1', 'iphoneos': '2', 'iphonesimulator': '7'}[target['sdk']]
        build_version = re.search(r'cmd LC_BUILD_VERSION\s+cmdsize \d+\s+platform (\S+)\s+minos (\S+)\s+sdk (\S+)', loads)
        platform_names = {'MACOS': '1', 'IOS': '2', 'IOSSIMULATOR': '7'}
        if (not build_version or platform_names.get(build_version[1], build_version[1]) != wanted_platform or
                version_tuple(build_version[2]) != version_tuple(target['minimum']) or
                version_tuple(build_version[3]) != version_tuple(tools['sdkVersion'])):
            raise ValueError('native framework SDK/platform/minimum mismatch')
        if 'cmd LC_ID_DYLIB' not in loads or 'cmd LC_RPATH' in loads:
            raise ValueError('native framework is not an isolated dynamic image')
        dependencies = re.findall(r'^\s*(.+?) \(compatibility version', run([tools['tools']['otool'], '-arch', arch, '-L', str(binary)]), re.M)
        allowed = {'@rpath/' + FRAMEWORK + '.framework/' + FRAMEWORK, '/usr/lib/libSystem.B.dylib', '/usr/lib/libz.1.dylib'}
        if '@rpath/' + FRAMEWORK + '.framework/' + FRAMEWORK not in dependencies or set(dependencies) - allowed:
            raise ValueError('unexpected native framework dynamic dependency')


def build_target(name, target, lock, source, sources):
    tools = toolchain(target)
    prefix, ffmpeg = build_ffmpeg(name, target, tools, source, sources, lock)
    inputs = {'ffmpeg': ffmpeg['fingerprint'], 'adapter': adapter_inputs(), 'toolchain': tools, 'target': target,
              'lockSHA256': digest(LOCK_PATH), 'prepareSHA256': digest(Path(__file__))}
    receipt_path = CACHE / 'build' / name / 'framework-receipt.json'
    output = CACHE / 'frameworks' / name / (FRAMEWORK + '.framework')
    previous = json.loads(receipt_path.read_text()) if receipt_path.exists() else {}
    if previous.get('inputs') == inputs:
        verify_inventory(output, previous['files'])
        verify_framework(output, target, tools, lock)
        return previous
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='framework-', dir=output.parent) as temporary:
        staged = Path(temporary) / (FRAMEWORK + '.framework')
        (staged / 'Headers').mkdir(parents=True)
        (staged / 'Modules').mkdir()
        shutil.copyfile(ADAPTER / 'include/MeowAudioKernel.h', staged / 'Headers/MeowAudioKernel.h')
        (staged / 'Modules/module.modulemap').write_text('framework module CMeowAudioKernel {\n  umbrella header "MeowAudioKernel.h"\n  export *\n}\n')
        (staged / 'Info.plist').write_bytes(plistlib.dumps(framework_metadata(target)))
        exports = Path(temporary) / 'exports.txt'
        exports.write_text(''.join('_' + name + '\n' for name in lock['exports']))
        # Static FFmpeg definitions resolve inside this two-level dynamic image.
        # Only the adapter API enters the App's namespace; video keeps its version.
        command = [tools['tools']['clang'], *flags_for(target, tools), '-std=c11', '-O2', '-fPIC', '-fvisibility=hidden',
                   '-dynamiclib', '-I' + str(prefix / 'include'), '-I' + str(ADAPTER / 'include'),
                   *map(str, sorted(ADAPTER.glob('*.c'))), '-Wl,-twolevel_namespace', '-Wl,-undefined,error',
                   '-Wl,-dead_strip', '-Wl,-exported_symbols_list,' + str(exports),
                   '-Wl,-install_name,@rpath/' + FRAMEWORK + '.framework/' + FRAMEWORK,
                   *[str(prefix / 'lib' / ('lib' + name + '.a')) for name in ('avformat', 'avcodec', 'swresample', 'avutil')],
                   '-lz', '-o', str(staged / FRAMEWORK)]
        run(command, log=CACHE / 'logs' / (name + '-framework.log'))
        verify_framework(staged, target, tools, lock)
        if output.exists():
            shutil.rmtree(output)
        staged.rename(output)
    receipt = {'inputs': inputs, 'fingerprint': identity(inputs), 'ffmpeg': ffmpeg, 'files': inventory(output)}
    write_json(receipt_path, receipt)
    return receipt


def verify_target(name, receipt, lock, sources):
    target = lock['targets'][name]
    tools = toolchain(target)
    ffmpeg = receipt['ffmpeg']
    expected_ff = ffmpeg_inputs(target, tools, sources, lock)
    if ffmpeg['inputs'] != expected_ff or ffmpeg['fingerprint'] != identity(expected_ff):
        raise ValueError('current source/SDK/FFmpeg recipe differs from receipt')
    inputs = {'ffmpeg': ffmpeg['fingerprint'], 'adapter': adapter_inputs(), 'toolchain': tools, 'target': target,
              'lockSHA256': digest(LOCK_PATH), 'prepareSHA256': digest(Path(__file__))}
    if receipt['inputs'] != inputs or receipt['fingerprint'] != identity(inputs):
        raise ValueError('current C/lock/script/SDK recipe differs from receipt')
    prefix = SHARED / 'artifacts' / ('apple-audio-' + ffmpeg['fingerprint']) / 'install'
    verify_libraries(prefix, ffmpeg, lock, tools, target)
    output = CACHE / 'frameworks' / name / (FRAMEWORK + '.framework')
    verify_inventory(output, receipt['files'])
    verify_framework(output, target, tools, lock)
    return tools


def verify_packaged(receipt, lock, requested=()):
    if (receipt.get('schema') != 1 or receipt.get('status') != 'complete' or
            receipt.get('lockSHA256') != digest(LOCK_PATH) or not receipt.get('targets') or
            receipt.get('identity') != identity(receipt['targets'])):
        raise ValueError('invalid audio package receipt')
    if set(requested) - set(receipt['targets']) or set(receipt['targets']) - set(lock['targets']):
        raise ValueError('requested audio slice not built or unknown slice')
    source = SHARED / 'sources' / lock['source']['sha256']
    sources = verify_source(source, lock)
    verify_inventory(PACKAGE / 'Artifacts', receipt['files'])
    groups = {}
    for name, value in receipt['targets'].items():
        tools = verify_target(name, value, lock, sources)
        groups.setdefault(lock['targets'][name]['sdk'], []).append((name, tools))
    xcframework = PACKAGE / 'Artifacts' / (FRAMEWORK + '.xcframework')
    metadata = plistlib.loads((xcframework / 'Info.plist').read_bytes())
    libraries = metadata.get('AvailableLibraries', [])
    if len(libraries) != len(groups):
        raise ValueError('XCFramework slice inventory mismatch')
    seen = set()
    for library in libraries:
        sdk = {'macos': 'macosx', 'ios': 'iphoneos'}.get(library.get('SupportedPlatform'))
        if library.get('SupportedPlatformVariant') == 'simulator':
            sdk = 'iphonesimulator' if sdk == 'iphoneos' else None
        elif library.get('SupportedPlatformVariant'):
            sdk = None
        if sdk not in groups or sdk in seen or library.get('LibraryPath') != FRAMEWORK + '.framework':
            raise ValueError('XCFramework platform mismatch')
        seen.add(sdk)
        names = groups[sdk]
        archs = sorted(lock['targets'][name]['arch'] for name, _ in names)
        if sorted(library['SupportedArchitectures']) != archs:
            raise ValueError('XCFramework architectures mismatch')
        identifier = library['LibraryIdentifier']
        if not re.fullmatch(r'[a-zA-Z0-9_-]+', identifier):
            raise ValueError('unsafe XCFramework slice path')
        verify_framework(xcframework / identifier / library['LibraryPath'], lock['targets'][names[0][0]], names[0][1], lock, archs)


def package_artifacts(lock, sources):
    available = {}
    for name in lock['targets']:
        path = CACHE / 'build' / name / 'framework-receipt.json'
        if path.exists():
            value = json.loads(path.read_text())
            # A changed recipe requires an explicit rebuild of this target.
            if value.get('inputs', {}).get('adapter') != adapter_inputs() or value.get('inputs', {}).get('prepareSHA256') != digest(Path(__file__)):
                continue
            verify_target(name, value, lock, sources)
            available[name] = value
    if not available:
        raise ValueError('no source-built audio target')
    receipt_path = PACKAGE / 'prepare-receipt.json'
    previous = json.loads(receipt_path.read_text()) if receipt_path.exists() else {}
    if previous.get('identity') == identity(available):
        verify_packaged(previous, lock)
        return previous
    PACKAGE.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='artifacts-', dir=PACKAGE) as temporary:
        temporary = Path(temporary)
        staged = temporary / 'Artifacts'
        staged.mkdir()
        command = ['xcodebuild', '-create-xcframework']
        for sdk in sorted({lock['targets'][name]['sdk'] for name in available}):
            names = [name for name in available if lock['targets'][name]['sdk'] == sdk]
            universal = temporary / sdk / (FRAMEWORK + '.framework')
            shutil.copytree(CACHE / 'frameworks' / names[0] / universal.name, universal)
            if len(names) > 1:
                run(['xcrun', 'lipo', '-create', *[str(CACHE / 'frameworks' / name / universal.name / FRAMEWORK) for name in names],
                     '-output', str(universal / FRAMEWORK)])
            verify_framework(universal, lock['targets'][names[0]], toolchain(lock['targets'][names[0]]), lock,
                             [lock['targets'][name]['arch'] for name in names])
            command += ['-framework', str(universal)]
        run([*command, '-output', str(staged / (FRAMEWORK + '.xcframework'))], log=CACHE / 'logs/xcframework.log')
        output = PACKAGE / 'Artifacts'
        if output.exists():
            shutil.rmtree(output)
        staged.rename(output)
    receipt = {'schema': 1, 'status': 'complete', 'identity': identity(available), 'lockSHA256': digest(LOCK_PATH),
               'targets': available, 'files': inventory(PACKAGE / 'Artifacts')}
    verify_packaged(receipt, lock)
    write_json(receipt_path, receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sources-only', '--source-only', action='store_true')
    parser.add_argument('--target', action='append', default=[])
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    lock = read_lock()
    if any(name not in lock['targets'] for name in args.target):
        parser.error('unknown locked audio target')
    if args.sources_only and (args.target or args.verify):
        parser.error('sources-only cannot select/verify native slices')
    if not args.sources_only and platform.system() != 'Darwin':
        parser.error('native build/verify requires Xcode/macOS; use --sources-only on Linux')
    if not args.sources_only and not args.verify and not args.target:
        parser.error('building requires explicit --target')
    CACHE.mkdir(parents=True, exist_ok=True)
    with (CACHE / 'prepare.lock').open('a') as guard:
        fcntl.flock(guard, fcntl.LOCK_EX)
        if args.verify:
            receipt = json.loads((PACKAGE / 'prepare-receipt.json').read_text())
            verify_packaged(receipt, lock, args.target)
        else:
            source, sources = prepare_sources(lock)
            if args.sources_only:
                print(json.dumps({'status': 'sources-ready', 'source': str(source), 'hashes': sources}))
                return
            for name in dict.fromkeys(args.target):
                print(json.dumps({'phase': 'building', 'target': name}), flush=True)
                build_target(name, lock['targets'][name], lock, source, sources)
            receipt = package_artifacts(lock, sources)
        print(json.dumps({'status': 'complete', 'targets': sorted(receipt['targets']), 'receipt': str(PACKAGE / 'prepare-receipt.json')}))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError, tarfile.TarError) as error:
        print('audio-kernel prepare failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
