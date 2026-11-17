import hashlib
import importlib.util
import json
from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('prepare_ios_audio_kernel', ROOT / 'scripts/prepare-ios-audio-kernel.py')
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


class AudioKernelLockTests(unittest.TestCase):
    def test_exact_windows_source_facts_without_windows_build_recipe(self):
        lock = PREPARE.read_lock()
        windows = json.loads((ROOT / 'apps/windows/native/AudioKernel/audio-kernel.lock.json').read_text())
        self.assertEqual(lock['source'], windows['source'])
        self.assertNotIn('binding', lock)
        self.assertEqual(lock['libraries'], [item for item in windows['libraries'] if item['name'] != 'avfilter'])
        for prefix in ('--enable-decoder=', '--enable-parser='):
            self.assertEqual(next(value for value in lock['configure'] if value.startswith(prefix)),
                             next(value for value in windows['configure'] if value.startswith(prefix)))
        windows_demuxers = next(value.split('=')[1] for value in windows['configure'] if value.startswith('--enable-demuxer=')).split(',')
        apple_demuxers = next(value.split('=')[1] for value in lock['configure'] if value.startswith('--enable-demuxer=')).split(',')
        self.assertEqual(set(apple_demuxers), set(windows_demuxers) | {'hls', 'mpegts'})

    def test_component_and_target_boundaries(self):
        lock = PREPARE.read_lock()
        flags = set(lock['configure'])
        self.assertTrue({'--disable-gpl', '--disable-version3', '--disable-nonfree', '--disable-avfilter',
                         '--disable-swscale', '--disable-programs', '--disable-everything', '--enable-static',
                         '--disable-shared', '--enable-protocol=http,tcp,crypto'} <= flags)
        self.assertEqual(set(lock['targets']), {'macos-arm64', 'macos-x86_64', 'ios-arm64',
                                               'ios-simulator-arm64', 'ios-simulator-x86_64'})
        self.assertEqual(len(lock['exports']), 7)
        self.assertTrue(all(value.startswith('meow_audio_decoder_') for value in lock['exports']))

    def test_cli_requires_explicit_native_target_and_rejects_unknown_target(self):
        script = ROOT / 'scripts/prepare-ios-audio-kernel.py'
        result = subprocess.run([sys.executable, '-I', '-B', str(script), '--target', 'ios-other'], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unknown locked audio target', result.stderr)
        result = subprocess.run([sys.executable, '-I', '-B', str(script), '--sources-only', '--verify'], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('sources-only cannot', result.stderr)


class AudioKernelPrepareTests(unittest.TestCase):
    def setUp(self):
        temporary_root = ROOT / '.cache/ios-audio-kernel/tests'
        temporary_root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=temporary_root)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.lock = PREPARE.read_lock()

    def test_inventory_rejects_modified_added_removed_and_linked_files(self):
        directory = self.root / 'files'
        directory.mkdir()
        file = directory / 'a'
        file.write_bytes(b'original')
        expected = PREPARE.inventory(directory)
        PREPARE.verify_inventory(directory, expected)
        file.write_bytes(b'modified')
        with self.assertRaises(ValueError):
            PREPARE.verify_inventory(directory, expected)
        file.write_bytes(b'original')
        extra = directory / 'extra'
        extra.write_bytes(b'')
        with self.assertRaises(ValueError):
            PREPARE.verify_inventory(directory, expected)
        extra.unlink()
        file.unlink()
        with self.assertRaises(ValueError):
            PREPARE.verify_inventory(directory, expected)
        file.symlink_to(self.root / 'outside')
        with self.assertRaises(ValueError):
            PREPARE.inventory(directory)

    def test_tree_hash_matches_windows_depth_first_order(self):
        (self.root / 'a').mkdir()
        (self.root / 'a/z').write_bytes(b'z')
        (self.root / 'a.x').write_bytes(b'x')
        expected = hashlib.sha256(b'a/z\0z\0a.x\0x\0').hexdigest()
        self.assertEqual(PREPARE.source_tree_hash(self.root), expected)
        (self.root / 'a/z').write_bytes(b'new')
        self.assertNotEqual(PREPARE.source_tree_hash(self.root), expected)

    def test_tree_hash_rejects_source_symlinks(self):
        (self.root / 'link').symlink_to(self.root / 'missing')
        with self.assertRaises(ValueError):
            PREPARE.source_tree_hash(self.root)

    def test_download_cache_requires_exact_archive_hash(self):
        source = {'sha256': hashlib.sha256(b'archive').hexdigest(), 'url': 'https://invalid.example/source.tar.xz'}
        downloads = self.root / 'downloads'
        downloads.mkdir()
        archive = downloads / (source['sha256'] + '.tar.xz')
        archive.write_bytes(b'archive')
        with patch.object(PREPARE, 'SHARED', self.root):
            self.assertEqual(PREPARE.archive_for(source), archive)
            archive.write_bytes(b'changed')
            with self.assertRaises(ValueError):
                PREPARE.archive_for(source)

    def test_large_download_preserves_bytes_and_rejects_hash_mismatch(self):
        chunks = [b'a' * (1024 * 1024)] * 64 + [b'tail']
        digest = hashlib.sha256()
        for chunk in chunks:
            digest.update(chunk)
        source = {'sha256': digest.hexdigest(), 'url': 'https://invalid.example/source.tar.xz'}
        response = MagicMock()
        response.__enter__.return_value = response
        response.read.side_effect = [*chunks, b'']
        with patch.object(PREPARE, 'SHARED', self.root), \
                patch.object(PREPARE.urllib.request, 'build_opener') as opener:
            opener.return_value.open.return_value = response
            archive = PREPARE.archive_for(source)
            self.assertEqual(archive.stat().st_size, sum(map(len, chunks)))
            self.assertEqual(PREPARE.digest(archive), source['sha256'])
            response.read.side_effect = [b'changed', b'']
            source['sha256'] = '0' * 64
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                PREPARE.archive_for(source)
            rejected = self.root / 'downloads' / (source['sha256'] + '.tar.xz')
            self.assertFalse(rejected.exists())
            self.assertFalse(rejected.with_suffix('.partial').exists())

    def test_failed_or_interrupted_download_removes_partial_archive(self):
        source = {'sha256': '0' * 64, 'url': 'https://invalid.example/source.tar.xz'}
        for error in (OSError('connection closed'), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__), \
                    patch.object(PREPARE, 'SHARED', self.root), \
                    patch.object(PREPARE.urllib.request, 'build_opener') as opener:
                response = MagicMock()
                response.__enter__.return_value = response
                response.read.side_effect = [b'partial', error, b'partial', error]
                opener.return_value.open.return_value = response
                with self.assertRaises(type(error)):
                    PREPARE.archive_for(source)
                rejected = self.root / 'downloads' / (source['sha256'] + '.tar.xz')
                self.assertFalse(rejected.exists())
                self.assertFalse(rejected.with_suffix('.partial').exists())
                response.__exit__.assert_called()

    def test_sdk_flags_and_version_comparison(self):
        target = self.lock['targets']['ios-simulator-arm64']
        self.assertEqual(PREPARE.flags_for(target, {'sdkPath': '/SDK'}),
                         ['-target', 'arm64-apple-ios17.0-simulator', '-isysroot', '/SDK'])
        self.assertEqual(PREPARE.version_tuple('17.0'), PREPARE.version_tuple('17.0.0'))

    def framework(self):
        target = self.lock['targets']['macos-arm64']
        path = self.root / 'CMeowAudioKernel.framework'
        (path / 'Headers').mkdir(parents=True)
        (path / 'Modules').mkdir()
        (path / 'Headers/MeowAudioKernel.h').write_bytes((PREPARE.ADAPTER / 'include/MeowAudioKernel.h').read_bytes())
        (path / 'Modules/module.modulemap').write_text('framework module CMeowAudioKernel {\n  umbrella header "MeowAudioKernel.h"\n  export *\n}\n')
        (path / 'Info.plist').write_bytes(plistlib.dumps(PREPARE.framework_metadata(target)))
        tools = {'tools': {name: name for name in ('nm', 'lipo', 'otool')}, 'sdkVersion': '26.1'}
        return path, target, tools

    def native_output(self, command, **kwargs):
        if command[0] == 'lipo':
            return 'arm64'
        if command[0] == 'nm':
            return '\n'.join('0000 (__TEXT,__text) external _' + value for value in self.lock['exports'])
        if '-hv' in command:
            return 'MH_MAGIC_64 ARM64 ALL DYLIB TWOLEVEL'
        if '-l' in command:
            return 'cmd LC_BUILD_VERSION\n cmdsize 32\n platform 1\n minos 14.0\n sdk 26.1\ncmd LC_ID_DYLIB'
        if '-L' in command:
            return '@rpath/CMeowAudioKernel.framework/CMeowAudioKernel (compatibility version 1.0.0)\n /usr/lib/libSystem.B.dylib (compatibility version 1.0.0)'
        raise AssertionError(command)

    def test_framework_checker_accepts_exact_macho_contract_using_tool_fixture(self):
        path, target, tools = self.framework()
        with patch.object(PREPARE, 'run', side_effect=self.native_output):
            PREPARE.verify_framework(path, target, tools, self.lock)

    def test_framework_checker_rejects_ffmpeg_export(self):
        path, target, tools = self.framework()
        def result(command, **kwargs):
            output = self.native_output(command)
            return output + '\n0000 (__TEXT,__text) external _avcodec_open2' if command[0] == 'nm' else output
        with patch.object(PREPARE, 'run', side_effect=result), self.assertRaisesRegex(ValueError, 'exports'):
            PREPARE.verify_framework(path, target, tools, self.lock)

    def test_framework_checker_rejects_wrong_architecture_sdk_and_external_dependency(self):
        path, target, tools = self.framework()
        for kind in ('arch', 'sdk', 'dependency', 'static', 'namespace'):
            def result(command, **kwargs):
                output = self.native_output(command)
                if kind == 'arch' and command[0] == 'lipo':
                    return 'x86_64'
                if kind == 'sdk' and '-l' in command:
                    return output.replace('sdk 26.1', 'sdk 26.0')
                if kind == 'dependency' and '-L' in command:
                    return output + '\n@rpath/libavcodec.63.dylib (compatibility version 1.0.0)'
                if kind == 'static' and '-hv' in command:
                    return output.replace('DYLIB', 'OBJECT')
                if kind == 'namespace' and '-hv' in command:
                    return output.replace('TWOLEVEL', '')
                return output
            with self.subTest(kind=kind), patch.object(PREPARE, 'run', side_effect=result), self.assertRaises(ValueError):
                PREPARE.verify_framework(path, target, tools, self.lock)

    def test_framework_checker_rejects_modified_public_header(self):
        path, target, tools = self.framework()
        (path / 'Headers/MeowAudioKernel.h').write_text('wrong ABI')
        with patch.object(PREPARE, 'run', side_effect=self.native_output), self.assertRaisesRegex(ValueError, 'header'):
            PREPARE.verify_framework(path, target, tools, self.lock)

    def test_recipe_verification_rechecks_current_sdk_and_source(self):
        target = self.lock['targets']['macos-arm64']
        sources = {'archive': self.lock['source']['sha256'], 'tree': self.lock['source']['treeSha256']}
        tools = {'sdkVersion': '26.1'}
        inputs = PREPARE.ffmpeg_inputs(target, tools, sources, self.lock)
        value = {'ffmpeg': {'inputs': inputs, 'fingerprint': PREPARE.identity(inputs)}}
        with patch.object(PREPARE, 'toolchain', return_value={'sdkVersion': '26.2'}), self.assertRaisesRegex(ValueError, 'source/SDK'):
            PREPARE.verify_target('macos-arm64', value, self.lock, sources)
        with patch.object(PREPARE, 'toolchain', return_value=tools), self.assertRaisesRegex(ValueError, 'source/SDK'):
            PREPARE.verify_target('macos-arm64', value, self.lock, {'archive': 'different', 'tree': sources['tree']})

    def test_recipe_verification_rechecks_current_adapter_and_script(self):
        target = self.lock['targets']['macos-arm64']
        tools, sources = {}, {}
        ff = PREPARE.ffmpeg_inputs(target, tools, sources, self.lock)
        receipt = {'ffmpeg': {'inputs': ff, 'fingerprint': PREPARE.identity(ff)},
                   'inputs': {'adapter': {'sources': {'changed.c': 'hash'}}}, 'fingerprint': 'old'}
        with patch.object(PREPARE, 'toolchain', return_value=tools), self.assertRaisesRegex(ValueError, 'C/lock/script'):
            PREPARE.verify_target('macos-arm64', receipt, self.lock, sources)

    def test_package_checker_rejects_missing_requested_slice_before_native_tools(self):
        targets = {'macos-arm64': {}}
        receipt = {'schema': 1, 'status': 'complete', 'lockSHA256': PREPARE.digest(PREPARE.LOCK_PATH),
                   'targets': targets, 'identity': PREPARE.identity(targets)}
        with self.assertRaisesRegex(ValueError, 'requested audio slice'):
            PREPARE.verify_packaged(receipt, self.lock, ['ios-arm64'])


if __name__ == '__main__':
    unittest.main()
