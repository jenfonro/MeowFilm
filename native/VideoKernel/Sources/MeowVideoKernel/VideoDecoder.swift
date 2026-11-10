import Foundation
import CoreMedia
import CoreVideo
import CMeowVideoKernel

public enum VideoKernelFailure: Int32, Error, Sendable {
    case interrupted = 2
    case cancelled = -1, openFailed = -2, formatFailed = -3, decodeFailed = -4
    case seekFailed = -5, outputFailed = -6, timedOut = -7, transportFailed = -8, decryptionFailed = -9
}

public struct VideoStreamInfo: Sendable {
    public let duration: Double?
    public let codec: String
    public let profile: Int
    public let bitDepth: Int
    public let width: Int
    public let height: Int
    public let hasAudio: Bool
    public let sampleRate: Int
    public let audioChannels: Int
    public let pixelFormat: UInt32
    /// nil means not measured; false/true come from the actual decoded output.
    public let hardwareDecode: Bool?

    init(_ info: MVKMediaInfo) {
        duration = info.duration.isFinite && info.duration > 0 ? info.duration : nil
        codec = String(cString: mvk_codec_name(info.video_codec))
        profile = Int(info.video_profile); bitDepth = Int(info.bit_depth)
        width = Int(info.width); height = Int(info.height); hasAudio = info.has_audio != 0
        sampleRate = Int(info.sample_rate); audioChannels = Int(info.audio_channels)
        pixelFormat = UInt32(bitPattern: info.pixel_format)
        hardwareDecode = info.hardware_decode < 0 ? nil : info.hardware_decode == 1
    }
}

/// Buffers are immutable after creation and cross only the one decoding queue
/// into the sample-buffer output. No input address, key or packet is exposed.
public struct VideoSample: @unchecked Sendable {
    public enum Kind: Sendable { case video, audio }
    public let buffer: CMSampleBuffer
    public let kind: Kind
    public let endTime: Double
    public var position: Double { CMSampleBufferGetPresentationTimeStamp(buffer).seconds }
    var byteCount: Int {
        if let image = CMSampleBufferGetImageBuffer(buffer) {
            if CVPixelBufferIsPlanar(image) {
                return (0..<CVPixelBufferGetPlaneCount(image)).reduce(0) {
                    $0 + CVPixelBufferGetBytesPerRowOfPlane(image, $1) * CVPixelBufferGetHeightOfPlane(image, $1)
                }
            }
            return CVPixelBufferGetBytesPerRow(image) * CVPixelBufferGetHeight(image)
        }
        return CMSampleBufferGetTotalSampleSize(buffer)
    }
}

/// Public FFmpeg ABI owner. All open/read/seek/destroy operations are serialized;
/// interrupt/cancel are the only concurrent C calls and use atomic flags.
public final class VideoDecoder: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meow.video.decode", qos: .userInitiated)
    private let queueKey = DispatchSpecificKey<Bool>()
    private let lock = NSLock()
    private var pointer: OpaquePointer?
    private var closing = false

    public init() throws {
        guard let pointer = mvk_create() else { throw VideoKernelFailure.openFailed }
        self.pointer = pointer
        queue.setSpecific(key: queueKey, value: true)
    }

    public func open(url: URL, format: String?, clearKeys: [String: Data]) async throws -> VideoStreamInfo {
        try await perform { decoder in
            let keyString = clearKeys.sorted { $0.key < $1.key }.map {
                $0.key + "=" + $0.value.map { String(format: "%02x", $0) }.joined()
            }.joined(separator: ":")
            let status = url.absoluteString.withCString { url in
                (format ?? "").withCString { format in
                    keyString.withCString { keys in
                        mvk_open(decoder, url, format, keys)
                    }
                }
            }
            try Self.check(status)
            var info = MVKMediaInfo()
            mvk_media_info(decoder, &info)
            return VideoStreamInfo(info)
        }
    }

    public func read() async throws -> VideoSample? {
        try await perform { decoder in
            var status: Int32 = 0, kind: Int32 = 0
            var end = 0.0
            let buffer = mvk_copy_sample(decoder, &status, &kind, &end)
            if status == MVK_END { return nil }
            try Self.check(status)
            guard let buffer else { throw VideoKernelFailure.outputFailed }
            return VideoSample(buffer: buffer, kind: kind == MVK_VIDEO ? .video : .audio, endTime: end)
        }
    }

    public func seek(to position: Double) async throws {
        try await perform { decoder in try Self.check(mvk_seek(decoder, position)) }
    }

    public func info() async throws -> VideoStreamInfo {
        try await perform { decoder in
            var info = MVKMediaInfo(); mvk_media_info(decoder, &info)
            return VideoStreamInfo(info)
        }
    }

    public func interrupt() {
        lock.lock(); defer { lock.unlock() }
        if let pointer { mvk_interrupt(pointer) }
    }

    public func cancel() {
        lock.lock(); defer { lock.unlock() }
        if let pointer { mvk_cancel(pointer) }
    }

    public func close() async {
        lock.withLock {
            closing = true
            if let pointer { mvk_cancel(pointer) }
        }
        await withCheckedContinuation { continuation in
            queue.async {
                self.lock.lock()
                let pointer = self.pointer; self.pointer = nil
                self.lock.unlock()
                mvk_destroy(pointer)
                continuation.resume()
            }
        }
    }

    private func perform<T: Sendable>(_ operation: @escaping @Sendable (OpaquePointer) throws -> T) async throws -> T {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                queue.async {
                    self.lock.lock()
                    let pointer = self.closing ? nil : self.pointer
                    self.lock.unlock()
                    guard let pointer else {
                        continuation.resume(throwing: VideoKernelFailure.cancelled); return
                    }
                    do { continuation.resume(returning: try operation(pointer)) }
                    catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { self.interrupt() }
    }

    private static func check(_ code: Int32) throws {
        if code != MVK_OK { throw VideoKernelFailure(rawValue: code) ?? .decodeFailed }
    }

    deinit {
        if let pointer {
            mvk_cancel(pointer)
            if DispatchQueue.getSpecific(key: queueKey) == true { mvk_destroy(pointer) }
            else { queue.sync { mvk_destroy(pointer) } }
        }
    }
}
