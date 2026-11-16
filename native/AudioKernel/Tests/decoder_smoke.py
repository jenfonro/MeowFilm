#!/usr/bin/env python3
"""Run the public C ABI against a locally built library; no device/output involved."""
import ctypes as C
import io
import math
from pathlib import Path
import struct
import subprocess
import sys
import threading
import unittest
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

READ = C.CFUNCTYPE(C.c_int, C.c_void_p, C.POINTER(C.c_uint8), C.c_int)
SEEK = C.CFUNCTYPE(C.c_int64, C.c_void_p, C.c_int64, C.c_int)
INTERRUPT = C.CFUNCTYPE(C.c_int, C.c_void_p)


class Input(C.Structure):
    _fields_ = [('opaque', C.c_void_p), ('read', READ), ('seek', SEEK), ('interrupted', INTERRUPT)]


class Info(C.Structure):
    _fields_ = [('duration', C.c_double), ('sample_rate', C.c_int), ('channels', C.c_int), ('seekable', C.c_int),
                ('source_sample_rate', C.c_int), ('source_channels', C.c_int),
                ('decoded_source_frames', C.c_int64), ('complete', C.c_int)]


LIB = C.CDLL(str(Path(sys.argv.pop(1)).resolve()))
LIB.meow_audio_decoder_create.argtypes = [Input, C.c_int, C.c_char_p, C.c_char_p, C.c_char_p, C.POINTER(C.c_int)]
LIB.meow_audio_decoder_create.restype = C.c_void_p
LIB.meow_audio_decoder_info.argtypes = [C.c_void_p, C.POINTER(Info)]
LIB.meow_audio_decoder_read.argtypes = [C.c_void_p, C.POINTER(C.c_float), C.c_int, C.POINTER(C.c_double)]
LIB.meow_audio_decoder_seek.argtypes = [C.c_void_p, C.c_double]
for name in ('cancel', 'resume', 'destroy'):
    function = getattr(LIB, 'meow_audio_decoder_' + name)
    function.argtypes = [C.c_void_p]
    function.restype = None


def wav(seconds=2, rate=22050):
    data = io.BytesIO()
    with wave.open(data, 'wb') as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes(b''.join(struct.pack('<h', int(16000 * math.sin(2 * math.pi * 311 * index / rate)))
                                    for index in range(seconds * rate)))
    return data.getvalue()


class Decoder:
    def __init__(self, data, seekable=True, hint=b'wav', root=None, read_error=0, read_chunk=997):
        self.data, self.offset = data, 0
        self.block = False
        self.blocked, self.wake = threading.Event(), threading.Event()
        def read(opaque, buffer, maximum):
            if read_error:
                return read_error
            if self.block:
                self.blocked.set()
                if not self.wake.wait(5):
                    return -3
                self.block = False
                return -1
            count = min(maximum, read_chunk, len(self.data) - self.offset)
            C.memmove(buffer, self.data[self.offset:self.offset + count], count)
            self.offset += count
            return count
        def seek(opaque, offset, whence):
            whence &= ~0x20000
            if whence == 0x10000:
                return len(self.data)
            if not seekable:
                return -4
            position = offset + {0: 0, 1: self.offset, 2: len(self.data)}.get(whence, -len(self.data) - 1)
            if position < 0 or position > len(self.data):
                return -2
            self.offset = position
            return position
        self.callbacks = (READ(read), SEEK(seek), INTERRUPT(lambda opaque: 0))
        error = C.c_int()
        self.pointer = LIB.meow_audio_decoder_create(Input(None, *self.callbacks), 48000, hint, root, None, C.byref(error))
        self.error = error.value

    def close(self):
        if self.pointer:
            LIB.meow_audio_decoder_destroy(self.pointer)
            self.pointer = None

    def read(self, count=257):
        buffer, position = (C.c_float * (count * 2))(), C.c_double()
        result = LIB.meow_audio_decoder_read(self.pointer, buffer, count, C.byref(position))
        return result, position.value, list(buffer[:max(0, result) * 2])

    def all(self):
        result = []
        while True:
            count, _, block = self.read()
            if count < 0:
                raise AssertionError('fixed decoder error: ' + str(count))
            if not count:
                return result
            result.extend(block)


class DecoderTests(unittest.TestCase):
    def decoder(self, *args, **kwargs):
        value = Decoder(*args, **kwargs)
        self.addCleanup(value.close)
        self.assertTrue(value.pointer, value.error)
        return value

    def test_stereo_resampling_duration_and_fully_drained_eof(self):
        d = self.decoder(wav())
        info = Info()
        self.assertEqual(LIB.meow_audio_decoder_info(d.pointer, C.byref(info)), 0)
        self.assertEqual((info.sample_rate, info.channels, info.seekable), (48000, 2, 1))
        self.assertAlmostEqual(info.duration, 2, places=4)
        pcm = d.all()
        self.assertEqual(len(pcm), 96000 * 2)
        self.assertEqual(pcm[0::2], pcm[1::2])
        self.assertGreater(max(pcm), 0.3)
        self.assertEqual(d.read()[0], 0)
        self.assertEqual(LIB.meow_audio_decoder_info(d.pointer, C.byref(info)), 0)
        self.assertEqual((info.source_sample_rate, info.source_channels, info.decoded_source_frames, info.complete),
                         (22050, 1, 44100, 1))

    def test_actual_source_frames_are_not_reconstructed_from_resampled_output(self):
        for rate in (44100, 96000, 192000):
            data = io.BytesIO()
            with wave.open(data, 'wb') as output:
                output.setnchannels(1)
                output.setsampwidth(2)
                output.setframerate(rate)
                output.writeframes(bytes((rate + 1) * 2))
            d = self.decoder(data.getvalue())
            d.all()
            info = Info()
            self.assertEqual(LIB.meow_audio_decoder_info(d.pointer, C.byref(info)), 0)
            self.assertEqual((info.source_sample_rate, info.decoded_source_frames, info.complete),
                             (rate, rate + 1, 1))
            self.assertEqual(LIB.meow_audio_decoder_seek(d.pointer, 0.5), 0)
            self.assertEqual(LIB.meow_audio_decoder_info(d.pointer, C.byref(info)), 0)
            self.assertEqual((info.decoded_source_frames, info.complete), (0, 0))

    def test_seek_trims_to_actual_target_without_previous_pcm(self):
        d = self.decoder(wav())
        reference = d.all()
        self.assertEqual(LIB.meow_audio_decoder_seek(d.pointer, 0), 0)
        self.assertGreater(d.read()[0], 0)  # Leave an old converted block pending.
        target = 0.53713
        self.assertEqual(LIB.meow_audio_decoder_seek(d.pointer, target), 0)
        count, position, first = d.read()
        self.assertGreater(count, 0)
        self.assertGreaterEqual(position, target)
        self.assertLessEqual(position - target, 1 / 48000)
        actual = first + d.all()
        frame_index = position * 48000
        index = math.floor(frame_index) * 2
        # Resetting swr can change its initial filter transient, not its timeline.
        # Interpolation requires both observed reference samples; do not invent
        # a clamped reference after its final output frame.
        comparable = min(len(actual), len(reference) - index - 2)
        tail = actual[2048:comparable]
        fraction = frame_index - math.floor(frame_index)
        expected = [(1 - fraction) * reference[index + i] + fraction * reference[index + i + 2]
                    for i in range(2048, comparable)]
        # A reset rational-rate resampler can round the final frame differently
        # from a continuous run, by at most one output frame.
        self.assertLessEqual(abs(len(actual) - (len(reference) - index)), 2)
        self.assertEqual(len(tail), len(expected))
        self.assertLess(max(abs(a - b) for a, b in zip(tail, expected)), 0.001)

    def test_nonseekable_and_fixed_input_errors(self):
        d = self.decoder(wav(), seekable=False)
        info = Info()
        self.assertEqual(LIB.meow_audio_decoder_info(d.pointer, C.byref(info)), 0)
        self.assertEqual(info.seekable, 0)
        self.assertEqual(LIB.meow_audio_decoder_seek(d.pointer, 0.5), -4)
        for code in (-1, -2, -3, -4, -5):
            failed = Decoder(b'', read_error=code)
            self.addCleanup(failed.close)
            self.assertFalse(failed.pointer)
            self.assertEqual(failed.error, code)

    def test_cancel_wakes_blocked_owner_then_resume_preserves_byte_position(self):
        for chunk in (997, 101):
            with self.subTest(read_chunk=chunk):
                self.cancel_and_resume(chunk)

    def cancel_and_resume(self, chunk):
        data = wav(seconds=10)
        reference = self.decoder(data).all()
        d = self.decoder(data, read_chunk=chunk)
        consumed = []
        d.block = True
        stopped = []
        def worker():
            while True:
                count, _, pcm = d.read()
                consumed.extend(pcm)
                if count <= 0:
                    stopped.append(count)
                    return
        thread = threading.Thread(target=worker)
        thread.start()
        self.assertTrue(d.blocked.wait(5))
        LIB.meow_audio_decoder_cancel(d.pointer)
        d.wake.set()  # Real input wake belongs to the owner, not C.
        thread.join(5)
        self.assertFalse(thread.is_alive())
        self.assertEqual(stopped, [-1])
        LIB.meow_audio_decoder_resume(d.pointer)
        consumed.extend(d.all())
        self.assertEqual(consumed, reference)

    def test_invalid_loopback_roots_are_rejected_before_input(self):
        for root in (b'http://localhost:1234/a/', b'http://127.0.0.1:0/a/', b'http://127.0.0.1:65536/a/',
                     b'http://127.0.0.1:1234/a', b'http://127.0.0.1:1234/a/../', b'http://127.0.0.1:1234/a/%2e/'):
            d = Decoder(b'', hint=b'hls', root=root)
            self.assertFalse(d.pointer)
            self.assertEqual(d.error, -4)

    def test_hls_without_loopback_root_rejects_nested_open(self):
        playlist = (b'#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n'
                    b'http://127.0.0.1:9/route/0.wav\n#EXT-X-ENDLIST\n')
        d = Decoder(playlist, hint=b'hls')
        self.addCleanup(d.close)
        self.assertFalse(d.pointer)
        self.assertEqual(d.error, -4)

    def hls(self, segment, encrypted=False, redirect=False):
        requests = []
        audio = wav(seconds=1, rate=48000)
        key = bytes(range(16))
        if encrypted:
            audio = subprocess.run(['openssl', 'enc', '-aes-128-cbc', '-K', key.hex(), '-iv', '00' * 16],
                                   input=audio, capture_output=True, check=True).stdout
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_GET(self):
                requests.append(self.path)
                if redirect:
                    self.send_response(302)
                    self.send_header('Location', '/outside.wav')
                    self.end_headers()
                    return
                data = key if self.path == '/route/key' else audio
                self.send_response(200)
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        root = 'http://127.0.0.1:' + str(server.server_port) + '/route/'
        url = segment(root, server.server_port)
        encryption = ('#EXT-X-KEY:METHOD=AES-128,URI="' + root + 'key",IV=0x' + '00' * 16 + '\n') if encrypted else ''
        playlist = ('#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n' + encryption +
                    '#EXTINF:1,\n' + url + '\n#EXT-X-ENDLIST\n').encode()
        d = Decoder(playlist, hint=b'hls', root=root.encode())
        self.addCleanup(d.close)
        return d, requests

    def test_hls_primary_custom_io_and_scoped_segment(self):
        d, requests = self.hls(lambda root, port: root + '0.wav')
        self.assertTrue(d.pointer, d.error)
        self.assertEqual(len(d.all()), 48000 * 2)
        self.assertEqual(requests, ['/route/0.wav'])

    def test_hls_aes128_uses_ffmpeg_crypto_only_with_scoped_urls(self):
        d, requests = self.hls(lambda root, port: root + '0.wav', encrypted=True)
        self.assertTrue(d.pointer, d.error)
        self.assertEqual(len(d.all()), 48000 * 2)
        self.assertEqual(set(requests), {'/route/key', '/route/0.wav'})

    def test_hls_rejects_external_file_root_escape_and_redirect(self):
        for segment in (lambda root, port: 'http://127.0.0.1:' + str(port) + '/outside.wav',
                        lambda root, port: root + '../outside.wav', lambda root, port: root + '%2e%2e/outside.wav',
                        lambda root, port: 'http://127.0.0.1:' + str(port + 1) + '/route/0.wav',
                        lambda root, port: 'file:///etc/passwd', lambda root, port: 'http://example.invalid/audio.wav'):
            d, requests = self.hls(segment)
            self.assertFalse(d.pointer)
            self.assertEqual(requests, [])
        d, requests = self.hls(lambda root, port: root + '0.wav', redirect=True)
        self.assertFalse(d.pointer)
        self.assertEqual(requests, ['/route/0.wav'])


if __name__ == '__main__':
    unittest.main()
