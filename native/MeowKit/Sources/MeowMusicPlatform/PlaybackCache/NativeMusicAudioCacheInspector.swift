import Foundation
import AudioToolbox
import MeowAudio

struct NativeMusicAudioCacheInspection: Sendable {
    let container: String
    let codec: String
    let duration: Double
    let decodedFrames: Int64
    let sampleRate: Double
    let channels: Int
    let bitrate: Double
    let bitDepth: Int?
    let lossless: Bool

    private static let aliases = ["standard": "128k", "higher": "192k", "exhigh": "320k",
                                  "lossless": "flac", "hires": "flac24bit"]
    static func supportedRequest(_ quality: String) -> Bool {
        ["128k", "192k", "320k", "flac", "flac24bit"].contains(aliases[quality] ?? quality)
    }

    func quality(for requested: String) throws -> String {
        guard Self.supportedRequest(requested),
              duration.isFinite, duration > 0,
              sampleRate.isFinite, (8_000...192_000).contains(sampleRate),
              decodedFrames > 0, (1...2).contains(channels), bitrate.isFinite, bitrate > 0 else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let requested = Self.aliases[requested] ?? requested
        if lossless {
            guard requested == "flac" || requested == "flac24bit" else {
                throw NativeMusicAudioCacheFailure.miss
            }
            return (bitDepth ?? 16) > 16 || sampleRate > 48_000 ? "flac24bit" : "flac"
        }
        guard requested.hasSuffix("k") else { throw NativeMusicAudioCacheFailure.miss }
        return requested
    }
}

struct NativeMusicAudioCacheInspector: Sendable {
    func inspect(_ file: NativeMusicAudioCacheFile) async throws -> NativeMusicAudioCacheInspection {
        guard file.size > 0, file.size <= NativeMusicAudioCacheHTTPPolicy.maximumBytes else {
            throw NativeMusicAudioCacheFailure.miss
        }
        try file.checkCancellation()
        let read: AudioFile_ReadProc = { context, offset, count, buffer, actual in
            actual.pointee = 0
            let file = Unmanaged<NativeMusicAudioCacheFile>.fromOpaque(context).takeUnretainedValue()
            do {
                actual.pointee = UInt32(try file.read(offset: offset, into: buffer, count: Int(count)))
                return noErr
            } catch { return OSStatus(-1) }
        }
        let size: AudioFile_GetSizeProc = { context in
            Unmanaged<NativeMusicAudioCacheFile>.fromOpaque(context).takeUnretainedValue().size
        }
        let context = Unmanaged.passRetained(file)
        defer { context.release() }
        var audio: AudioFileID?
        try Self.ok(AudioFileOpenWithCallbacks(context.toOpaque(), read, nil, size, nil, 0, &audio))
        guard let audio else { throw NativeMusicAudioCacheFailure.miss }
        defer { AudioFileClose(audio) }

        var fileFormat: UInt32 = 0
        try Self.property(audio, kAudioFilePropertyFileFormat, &fileFormat)
        var source = AudioStreamBasicDescription()
        try Self.property(audio, kAudioFilePropertyDataFormat, &source)

        let container: String
        switch fileFormat {
        case kAudioFileMP3Type: container = "mp3"
        case kAudioFileFLACType: container = "flac"
        case kAudioFileMPEG4Type, kAudioFileM4AType: container = "m4a"
        case kAudioFileWAVEType: container = "wav"
        default: throw NativeMusicAudioCacheFailure.miss
        }

        let codec: String, lossless: Bool
        var bitDepth: Int?
        switch source.mFormatID {
        case kAudioFormatMPEGLayer3:
            codec = "mp3"; lossless = false
        case kAudioFormatMPEG4AAC:
            codec = "aac"; lossless = false
        case kAudioFormatFLAC:
            codec = "flac"; lossless = true
            if source.mBitsPerChannel > 0 {
                bitDepth = Int(source.mBitsPerChannel)
            } else {
                switch source.mFormatFlags {
                case kAppleLosslessFormatFlag_20BitSourceData: bitDepth = 20
                case kAppleLosslessFormatFlag_24BitSourceData: bitDepth = 24
                case kAppleLosslessFormatFlag_32BitSourceData: bitDepth = 32
                default: bitDepth = 16
                }
            }
        case kAudioFormatAppleLossless:
            codec = "alac"; lossless = true
            switch source.mFormatFlags {
            case kAppleLosslessFormatFlag_16BitSourceData: bitDepth = 16
            case kAppleLosslessFormatFlag_20BitSourceData: bitDepth = 20
            case kAppleLosslessFormatFlag_24BitSourceData: bitDepth = 24
            case kAppleLosslessFormatFlag_32BitSourceData: bitDepth = 32
            default: throw NativeMusicAudioCacheFailure.miss
            }
        case kAudioFormatLinearPCM:
            codec = "pcm"; lossless = true; bitDepth = Int(source.mBitsPerChannel)
            guard source.mFormatFlags & kAudioFormatFlagIsFloat == 0 else { throw NativeMusicAudioCacheFailure.miss }
        default:
            throw NativeMusicAudioCacheFailure.miss
        }

        var audioBytes: UInt64 = 0
        _ = try? Self.property(audio, kAudioFilePropertyAudioDataByteCount, &audioBytes)
        var estimatedDuration = 0.0
        _ = try? Self.property(audio, kAudioFilePropertyEstimatedDuration, &estimatedDuration)

        let decoder = AudioDecoder(input: MusicCacheAudioInput(file))
        let stream: AudioStreamInformation
        do {
            stream = try await decoder.prepare()
            try await decoder.close()
        } catch {
            try? await decoder.close()
            throw NativeMusicAudioCacheFailure.miss
        }
        try file.checkCancellation()

        let duration = stream.duration ?? estimatedDuration
        let sampleRate = stream.sourceSampleRate > 0 ? Double(stream.sourceSampleRate) : source.mSampleRate
        let channels = stream.sourceChannels > 0 ? stream.sourceChannels : Int(source.mChannelsPerFrame)
        guard duration.isFinite, duration > 0,
              sampleRate.isFinite, (8_000...192_000).contains(sampleRate),
              (1...2).contains(channels) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let decodedFrames = max(1, Int64((duration * sampleRate).rounded()))
        let effectiveBytes = audioBytes > 0 && audioBytes <= UInt64(file.size) ? audioBytes : UInt64(file.size)
        return .init(container: container, codec: codec, duration: duration, decodedFrames: decodedFrames,
                     sampleRate: sampleRate, channels: channels,
                     bitrate: Double(effectiveBytes) * 8 / duration, bitDepth: bitDepth, lossless: lossless)
    }

    private static func ok(_ status: OSStatus) throws {
        guard status == noErr else { throw NativeMusicAudioCacheFailure.miss }
    }

    private static func property<T>(_ file: AudioFileID, _ id: AudioFilePropertyID, _ value: inout T) throws {
        var size = UInt32(MemoryLayout<T>.size)
        try ok(AudioFileGetProperty(file, id, &size, &value))
        guard size == UInt32(MemoryLayout<T>.size) else { throw NativeMusicAudioCacheFailure.miss }
    }
}

private final class MusicCacheAudioInput: AudioByteInput, @unchecked Sendable {
    let formatHint: String? = nil
    let loopbackRoot: String? = nil
    private let file: NativeMusicAudioCacheFile
    private let lock = NSLock()
    private var cancelled = false
    private var closed = false
    private var position: Int64 = 0
    init(_ file: NativeMusicAudioCacheFile) { self.file = file }
    var isReadCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled || closed }
    func read(maximumBytes: Int) throws -> Data {
        guard !isReadCancelled else { throw CancellationError() }
        try file.checkCancellation()
        let count = Int(min(Int64(maximumBytes), file.size - position))
        guard count > 0 else { return Data() }
        var bytes = Data(count: count)
        let actual = try bytes.withUnsafeMutableBytes { try file.read(offset: position, into: $0.baseAddress, count: count) }
        if actual < count { bytes.removeSubrange(actual..<count) }
        position += Int64(actual)
        return bytes
    }
    func seek(_ request: AudioByteSeek) throws -> Int64 {
        guard !isReadCancelled else { throw CancellationError() }
        try file.checkCancellation()
        switch request {
        case .size: return file.size
        case .offset(let offset, let origin):
            let base: Int64
            switch origin { case .start: base = 0; case .current: base = position; case .end: base = file.size }
            let (target, overflow) = base.addingReportingOverflow(offset)
            guard !overflow, target >= 0, target <= file.size else { throw AudioCoreError.seekFailed }
            position = target
            return target
        }
    }
    func cancelReads() { lock.lock(); cancelled = true; lock.unlock() }
    func resumeReads() { lock.lock(); if !closed { cancelled = false }; lock.unlock() }
    func close() async throws { lock.lock(); closed = true; lock.unlock() }
}
