import Foundation
import AudioToolbox
import MeowMusicFeature

public protocol NativeMusicAudioCacheInspecting: Sendable {
    func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) async throws -> NativeMusicAudioCacheInspection
}

/// Physical measurements only. Lossy source bit depth is unknown (nil), never
/// the bit depth of our decoded PCM buffer. No branded quality is inferred.
public struct NativeMusicAudioCacheInspection: Codable, Sendable {
    public let container: String
    public let codec: String
    public let duration: Double
    public let decodedFrames: Int64
    public let sampleRate: Double
    public let channels: Int
    public let bitrate: Double
    public let bitDepth: Int?
    public let lossless: Bool
    public let complete: Bool
    public let protectedContent: Bool

    public init(container: String, codec: String, duration: Double, decodedFrames: Int64,
                sampleRate: Double, channels: Int, bitrate: Double, bitDepth: Int?,
                lossless: Bool, complete: Bool, protectedContent: Bool) {
        self.container = container; self.codec = codec; self.duration = duration; self.decodedFrames = decodedFrames
        self.sampleRate = sampleRate; self.channels = channels; self.bitrate = bitrate; self.bitDepth = bitDepth
        self.lossless = lossless; self.complete = complete; self.protectedContent = protectedContent
    }

    private static let aliases = ["standard": "128k", "higher": "192k", "exhigh": "320k",
                                  "lossless": "flac", "hires": "flac24bit"]
    static func supportedRequest(_ quality: String) -> Bool {
        ["128k", "192k", "320k", "flac", "flac24bit"].contains(aliases[quality] ?? quality)
    }

    func quality(for requested: String, expectedDuration: Double) throws -> String {
        guard Self.supportedRequest(requested), complete, !protectedContent,
              expectedDuration.isFinite, expectedDuration > 0, duration.isFinite, duration > 0,
              sampleRate.isFinite, (8_000...192_000).contains(sampleRate),
              decodedFrames > 0, (1...2).contains(channels), bitrate.isFinite, bitrate > 0,
              abs(Double(decodedFrames) / sampleRate - duration) <= 1 / sampleRate,
              MusicMediaDuration.matches(expected: expectedDuration, actual: duration) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let requested = Self.aliases[requested] ?? requested
        if lossless {
            guard (codec == "flac" && container == "flac") || (codec == "alac" && container == "m4a") ||
                    (codec == "pcm" && container == "wav"),
                  let bitDepth, [16, 20, 24, 32].contains(bitDepth) else { throw NativeMusicAudioCacheFailure.miss }
            let measured = bitDepth > 16 || sampleRate > 48_000 ? "flac24bit" : "flac"
            guard requested == measured else { throw NativeMusicAudioCacheFailure.miss }
            return measured
        }
        guard bitDepth == nil, (codec == "mp3" && container == "mp3") || (codec == "aac" && container == "m4a"),
              bitrate <= 512_000, requested.hasSuffix("k"), let nominal = Double(requested.dropLast()) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let measured = (bitrate / 1000).rounded()
        guard measured > 0, abs(measured - nominal) <= 8 else { throw NativeMusicAudioCacheFailure.miss }
        // The raw measured bitrate remains in the inspection. The quality
        // category must be canonical (127 kb/s MP3 is standard, not a new
        // unsupported "127k" policy or a claim of exactly 128000 bits/s).
        return requested
    }
}

/// Strict known-container validation plus AudioToolbox decoding of EVERY frame
/// to EOF. Opening a file, duration metadata, requestedQuality, and a successful
/// near-EOF seek are not completeness evidence.
///
/// Apple API contract: ExtAudioFileRead returns zero frames at EOF;
/// ExtAudioFileWrapAudioFileID does not own the wrapped AudioFileID.
/// AudioFileOpenWithCallbacks keeps all reads on our already-pinned descriptor.
public struct NativeMusicAudioCacheInspector: NativeMusicAudioCacheInspecting {
    public init() {}

    public func inspect(_ file: NativeMusicAudioCacheFile,
                        expectedDuration: Double) async throws -> NativeMusicAudioCacheInspection {
        guard expectedDuration.isFinite, expectedDuration > 0,
              file.size > 0, file.size <= NativeMusicAudioCacheHTTPPolicy.maximumBytes else {
            throw NativeMusicAudioCacheFailure.miss
        }
        try file.checkCancellation()
        let structure = try NativeMusicAudioCacheStructure(file: file).inspect()
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
        // `file` is retained throughout decoding, including every C callback.
        let context = Unmanaged.passRetained(file)
        defer { context.release() }
        var audio: AudioFileID?
        try Self.ok(AudioFileOpenWithCallbacks(context.toOpaque(), read, nil, size, nil, 0, &audio))
        guard let audio else { throw NativeMusicAudioCacheFailure.miss }
        defer { AudioFileClose(audio) }
        var source = AudioStreamBasicDescription()
        try Self.property(audio, kAudioFilePropertyDataFormat, &source)
        guard source.mSampleRate.isFinite, (8_000...192_000).contains(source.mSampleRate),
              (1...2).contains(source.mChannelsPerFrame) else { throw NativeMusicAudioCacheFailure.miss }

        let codec: String, lossless: Bool
        var bitDepth: Int?
        switch source.mFormatID {
        case kAudioFormatMPEGLayer3:
            codec = "mp3"; lossless = false
        case kAudioFormatMPEG4AAC:
            codec = "aac"; lossless = false
        case kAudioFormatFLAC:
            codec = "flac"; lossless = true; bitDepth = structure.bitDepth
            if source.mBitsPerChannel > 0, Int(source.mBitsPerChannel) != bitDepth {
                throw NativeMusicAudioCacheFailure.miss
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
            // HE-AAC variants, spatial/multichannel, DSD and unknown codecs do
            // not get relabelled as ordinary AAC/lossless.
            throw NativeMusicAudioCacheFailure.miss
        }
        guard structure.codec == codec else { throw NativeMusicAudioCacheFailure.miss }
        if let rate = structure.sampleRate, rate != source.mSampleRate { throw NativeMusicAudioCacheFailure.miss }
        if let depth = structure.bitDepth, depth != bitDepth { throw NativeMusicAudioCacheFailure.miss }
        if let channels = structure.channels, channels != Int(source.mChannelsPerFrame) { throw NativeMusicAudioCacheFailure.miss }

        var audioBytes: UInt64 = 0
        try Self.property(audio, kAudioFilePropertyAudioDataByteCount, &audioBytes)
        guard audioBytes > 0, audioBytes <= UInt64(file.size) else { throw NativeMusicAudioCacheFailure.miss }
        try Self.verifyPackets(audio, source: source, file: file, expectedBytes: audioBytes)
        var wrapped: ExtAudioFileRef?
        try Self.ok(ExtAudioFileWrapAudioFileID(audio, false, &wrapped))
        guard let wrapped else { throw NativeMusicAudioCacheFailure.miss }
        defer { ExtAudioFileDispose(wrapped) }
        var nativeFrames: Int64 = 0
        var lengthSize = UInt32(MemoryLayout<Int64>.size)
        try Self.ok(ExtAudioFileGetProperty(wrapped, kExtAudioFileProperty_FileLengthFrames, &lengthSize, &nativeFrames))
        guard nativeFrames > 0 else { throw NativeMusicAudioCacheFailure.miss }

        // No sample-rate conversion. Interleaved float PCM is only a bounded
        // decoder output format, NOT a claim about the file's source precision.
        var output = AudioStreamBasicDescription(
            mSampleRate: source.mSampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4 * source.mChannelsPerFrame, mFramesPerPacket: 1,
            mBytesPerFrame: 4 * source.mChannelsPerFrame, mChannelsPerFrame: source.mChannelsPerFrame,
            mBitsPerChannel: 32, mReserved: 0)
        try Self.ok(ExtAudioFileSetProperty(wrapped, kExtAudioFileProperty_ClientDataFormat,
                                           UInt32(MemoryLayout<AudioStreamBasicDescription>.size), &output))
        let frameCapacity: UInt32 = 4096
        let bytes = Int(frameCapacity * output.mBytesPerFrame)
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: bytes, alignment: MemoryLayout<Float>.alignment)
        defer { buffer.deallocate() }
        var decoded: Int64 = 0
        let maximumFrames = (expectedDuration + MusicMediaDuration.tolerance + 1) * source.mSampleRate
        guard maximumFrames.isFinite, maximumFrames < Double(Int64.max) else { throw NativeMusicAudioCacheFailure.miss }
        while true {
            try file.checkCancellation()
            var list = AudioBufferList(mNumberBuffers: 1,
                mBuffers: AudioBuffer(mNumberChannels: output.mChannelsPerFrame, mDataByteSize: UInt32(bytes), mData: buffer))
            var frames = frameCapacity
            try Self.ok(ExtAudioFileRead(wrapped, &frames, &list))
            guard frames <= frameCapacity, list.mNumberBuffers == 1,
                  list.mBuffers.mDataByteSize <= UInt32(bytes) else { throw NativeMusicAudioCacheFailure.miss }
            if frames == 0 { break }
            guard list.mBuffers.mNumberChannels == output.mChannelsPerFrame,
                  list.mBuffers.mDataByteSize == frames * output.mBytesPerFrame else {
                throw NativeMusicAudioCacheFailure.miss
            }
            guard Int64(frames) <= Int64(maximumFrames) - decoded else { throw NativeMusicAudioCacheFailure.miss }
            decoded += Int64(frames)
        }
        try file.checkCancellation()
        guard decoded > 0 else { throw NativeMusicAudioCacheFailure.miss }
        // Compressed packet tables can include encoder priming/padding. Bound
        // that discrepancy separately; PCM/FLAC frame totals must be exact.
        let padding: Int64 = lossless ? 0 : 4096
        guard abs(decoded - nativeFrames) <= padding else { throw NativeMusicAudioCacheFailure.miss }
        if let frames = structure.frames, abs(decoded - frames) > padding { throw NativeMusicAudioCacheFailure.miss }
        let duration = Double(decoded) / source.mSampleRate
        guard abs(duration - expectedDuration) <= MusicMediaDuration.tolerance else {
            throw NativeMusicAudioCacheFailure.miss
        }
        return .init(container: structure.container, codec: codec, duration: duration, decodedFrames: decoded,
                     sampleRate: source.mSampleRate, channels: Int(source.mChannelsPerFrame),
                     bitrate: Double(audioBytes) * 8 / duration, bitDepth: bitDepth, lossless: lossless,
                     complete: true, protectedContent: false)
    }

    private static func ok(_ status: OSStatus) throws {
        guard status == noErr else { throw NativeMusicAudioCacheFailure.miss }
    }
    /// Verify every encoded packet is readable as well. This prevents the
    /// decoder's priming/padding tolerance from admitting a missing final AAC
    /// packet merely because the resulting duration still looks close enough.
    private static func verifyPackets(_ audio: AudioFileID, source: AudioStreamBasicDescription,
                                      file: NativeMusicAudioCacheFile, expectedBytes: UInt64) throws {
        var totalPackets: UInt64 = 0, maximumPacket: UInt32 = 0
        try property(audio, kAudioFilePropertyAudioDataPacketCount, &totalPackets)
        try property(audio, kAudioFilePropertyPacketSizeUpperBound, &maximumPacket)
        guard totalPackets > 0, totalPackets <= UInt64(file.size),
              maximumPacket > 0, maximumPacket <= 1024 * 1024 else { throw NativeMusicAudioCacheFailure.miss }
        let capacity: UInt32 = 1024 * 1024
        let packetLimit = min(UInt32(256), capacity / maximumPacket)
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: Int(capacity), alignment: 16)
        defer { buffer.deallocate() }
        var descriptions = [AudioStreamPacketDescription](repeating: .init(), count: Int(packetLimit))
        var position: UInt64 = 0, bytes: UInt64 = 0
        while position < totalPackets {
            try file.checkCancellation()
            var count = UInt32(min(UInt64(packetLimit), totalPackets - position)), size = capacity
            let requested = count
            let status = descriptions.withUnsafeMutableBufferPointer {
                AudioFileReadPacketData(audio, false, &size,
                    source.mBytesPerPacket == 0 ? $0.baseAddress : nil, Int64(position), &count, buffer)
            }
            guard (status == noErr || status == kAudioFileEndOfFileError && position + UInt64(count) == totalPackets),
                  count > 0, count <= requested, size > 0, size <= capacity,
                  UInt64(size) <= expectedBytes - bytes else { throw NativeMusicAudioCacheFailure.miss }
            if source.mBytesPerPacket > 0 {
                guard UInt64(size) == UInt64(count) * UInt64(source.mBytesPerPacket) else {
                    throw NativeMusicAudioCacheFailure.miss
                }
            } else {
                var packetBytes: UInt64 = 0
                for item in descriptions.prefix(Int(count)) {
                    guard item.mStartOffset >= 0, item.mDataByteSize > 0,
                          UInt64(item.mStartOffset) == packetBytes,
                          UInt64(item.mDataByteSize) <= UInt64(size) - packetBytes else {
                        throw NativeMusicAudioCacheFailure.miss
                    }
                    packetBytes += UInt64(item.mDataByteSize)
                }
                guard packetBytes == UInt64(size) else { throw NativeMusicAudioCacheFailure.miss }
            }
            position += UInt64(count); bytes += UInt64(size)
        }
        guard bytes == expectedBytes else { throw NativeMusicAudioCacheFailure.miss }
    }
    private static func property<T>(_ file: AudioFileID, _ id: AudioFilePropertyID, _ value: inout T) throws {
        var size = UInt32(MemoryLayout<T>.size)
        try ok(AudioFileGetProperty(file, id, &size, &value))
        guard size == UInt32(MemoryLayout<T>.size) else { throw NativeMusicAudioCacheFailure.miss }
    }
}

/// Fail-closed structural checks complement native decoders that may tolerate
/// a truncated tail or ignore encryption/external-reference container boxes.
/// A small read window is used; a 256 MiB file is never loaded into one buffer.
private final class NativeMusicAudioCacheStructure {
    struct Result {
        let container: String
        let codec: String
        var bitDepth: Int? = nil
        var sampleRate: Double? = nil
        var channels: Int? = nil
        var frames: Int64? = nil
    }
    private let file: NativeMusicAudioCacheFile
    private var window = Data()
    private var windowStart: Int64 = 0
    private var boxes = 0
    init(file: NativeMusicAudioCacheFile) { self.file = file }

    func inspect() throws -> Result {
        guard file.size >= 12 else { throw NativeMusicAudioCacheFailure.miss }
        let first = try text(0, 4)
        if first == "RIFF" { return try wav() }
        if try text(4, 4) == "ftyp" { return try mp4() }
        var start: Int64 = 0
        for _ in 0..<8 {
            guard try text(start, 3) == "ID3" else { break }
            let header = try bytes(start, 10)
            guard (2...4).contains(header[3]), header[4] != 255, header[6...9].allSatisfy({ $0 < 128 }) else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let size = header[6...9].reduce(Int64(0)) { $0 * 128 + Int64($1) }
            start += 10 + size + (header[3] == 4 && header[5] & 16 != 0 ? 10 : 0)
            guard start + 4 <= file.size else { throw NativeMusicAudioCacheFailure.miss }
        }
        if try text(start, 4) == "fLaC" { return try flac(start) }
        return try mp3(start)
    }

    private func wav() throws -> Result {
        guard try text(8, 4) == "WAVE", try uint(4, 4, little: true) + 8 == UInt64(file.size) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        var p: Int64 = 12, format: Result?, dataSize: UInt64?, alignment: UInt64 = 0
        var work = 0
        while p < file.size {
            work += 1
            guard work <= 100_000, p + 8 <= file.size else { throw NativeMusicAudioCacheFailure.miss }
            let type = try text(p, 4), size = try uint(p + 4, 4, little: true)
            let end = p + 8 + Int64(size)
            guard end <= file.size else { throw NativeMusicAudioCacheFailure.miss }
            if type == "fmt " {
                guard format == nil, size == 16 || size == 18, try uint(p + 8, 2, little: true) == 1 else {
                    throw NativeMusicAudioCacheFailure.miss
                }
                let channels = try uint(p + 10, 2, little: true), rate = try uint(p + 12, 4, little: true)
                let depth = try uint(p + 22, 2, little: true)
                alignment = try uint(p + 20, 2, little: true)
                guard (1...2).contains(channels), [16, 24, 32].contains(depth),
                      alignment == channels * depth / 8,
                      try uint(p + 16, 4, little: true) == rate * alignment else { throw NativeMusicAudioCacheFailure.miss }
                if size == 18, try uint(p + 24, 2, little: true) != 0 { throw NativeMusicAudioCacheFailure.miss }
                format = Result(container: "wav", codec: "pcm", bitDepth: Int(depth),
                                sampleRate: Double(rate), channels: Int(channels))
            } else if type == "data" {
                guard dataSize == nil, size > 0 else { throw NativeMusicAudioCacheFailure.miss }
                dataSize = size
            }
            p = end + Int64(size & 1)
        }
        guard p == file.size, var result = format, let dataSize, alignment > 0,
              dataSize % alignment == 0 else { throw NativeMusicAudioCacheFailure.miss }
        result.frames = Int64(dataSize / alignment)
        return result
    }

    private func flac(_ start: Int64) throws -> Result {
        var p = start + 4, last = false, first = true, result: Result?
        var work = 0
        while !last {
            work += 1
            guard work <= 100_000 else { throw NativeMusicAudioCacheFailure.miss }
            let header = try bytes(p, 4), type = header[0] & 127
            let size = Int64(header[1]) * 65536 + Int64(header[2]) * 256 + Int64(header[3])
            last = header[0] & 128 != 0
            guard p + 4 + size <= file.size, type != 127 else { throw NativeMusicAudioCacheFailure.miss }
            if first {
                guard type == 0, size == 34 else { throw NativeMusicAudioCacheFailure.miss }
                let packed = try uint(p + 14, 8)
                let rate = packed >> 44, channels = (packed >> 41 & 7) + 1, depth = (packed >> 36 & 31) + 1
                let frames = packed & 0xF_FFFF_FFFF
                guard rate > 0, frames > 0 else { throw NativeMusicAudioCacheFailure.miss }
                result = Result(container: "flac", codec: "flac", bitDepth: Int(depth), sampleRate: Double(rate),
                                channels: Int(channels), frames: Int64(frames))
                first = false
            } else if type == 0 { throw NativeMusicAudioCacheFailure.miss }
            p += 4 + size
        }
        let sync = try bytes(p, 2)
        guard p + 6 < file.size, sync[0] == 255, sync[1] & 252 == 248, let result else {
            throw NativeMusicAudioCacheFailure.miss
        }
        return result
    }

    private func mp3(_ start: Int64) throws -> Result {
        var end = file.size
        if end - start >= 128, try text(end - 128, 3) == "TAG" { end -= 128 }
        // Unknown trailing tags (including unvalidated APE layouts) are misses,
        // not silently skipped audio. This does not restrict remote playback.
        var p = start, frames: Int64 = 0, count = 0, rate: Double?
        while p < end {
            let h = try bytes(p, 4)
            let version = h[1] >> 3 & 3, index = Int(h[2] >> 4), rateIndex = Int(h[2] >> 2 & 3)
            guard h[0] == 255, h[1] & 224 == 224, version != 1, h[1] >> 1 & 3 == 1,
                  (1...14).contains(index), rateIndex < 3 else { throw NativeMusicAudioCacheFailure.miss }
            let sampleRate = Double([44_100, 48_000, 32_000][rateIndex]) / (version == 3 ? 1 : version == 2 ? 2 : 4)
            let rates = version == 3 ? [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320] :
                [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160]
            let size = Int64(Double((version == 3 ? 144 : 72) * rates[index] * 1000) / sampleRate) + Int64(h[2] >> 1 & 1)
            guard size >= 4, p + size <= end, rate == nil || rate == sampleRate else { throw NativeMusicAudioCacheFailure.miss }
            rate = sampleRate
            var metadata = false
            if count == 0 {
                let mono = h[3] >> 6 == 3
                let side = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17)
                let at = p + 4 + (h[1] & 1 == 0 ? 2 : 0) + Int64(side)
                if at + 4 <= p + size { metadata = try ["Xing", "Info"].contains(text(at, 4)) }
            }
            if !metadata { frames += version == 3 ? 1152 : 576 }
            count += 1; p += size
        }
        guard p == end, count >= 3, let rate else { throw NativeMusicAudioCacheFailure.miss }
        return Result(container: "mp3", codec: "mp3", sampleRate: rate, frames: frames)
    }

    private func mp4() throws -> Result {
        var movie = false, payload = false, codec: String?
        let forbidden: Set<String> = ["pssh", "sinf", "schm", "schi", "tenc", "senc", "enca", "encv",
                                      "drms", "drmi", "moof", "mvex", "mfra", "sidx"]
        func walk(_ start: Int64, _ end: Int64, depth: Int, samples: Bool = false) throws {
            guard depth <= 12 else { throw NativeMusicAudioCacheFailure.miss }
            var p = start
            while p < end {
                boxes += 1
                guard boxes <= 100_000, p + 8 <= end else { throw NativeMusicAudioCacheFailure.miss }
                var size = try uint(p, 4), header: Int64 = 8
                let type = try text(p + 4, 4)
                if size == 1 { size = try uint(p + 8, 8); header = 16 }
                if size == 0 { size = UInt64(end - p) }
                guard size >= UInt64(header), size <= UInt64(end - p), !forbidden.contains(type) else {
                    throw NativeMusicAudioCacheFailure.miss
                }
                let next = p + Int64(size), body = p + header
                if depth == 0, type == "moov" {
                    guard !movie else { throw NativeMusicAudioCacheFailure.miss }; movie = true
                }
                if depth == 0, type == "mdat", size > UInt64(header) { payload = true }
                if ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "wave"].contains(type) {
                    try walk(body, next, depth: depth + 1)
                } else if type == "stsd" {
                    guard body + 8 <= next, try uint(body, 4) == 0, try uint(body + 4, 4) == 1 else {
                        throw NativeMusicAudioCacheFailure.miss
                    }
                    try walk(body + 8, next, depth: depth + 1, samples: true)
                } else if samples {
                    guard codec == nil, ["mp4a", "alac"].contains(type), body + 28 <= next,
                          try uint(body + 6, 2) == 1 else { throw NativeMusicAudioCacheFailure.miss }
                    let version = try uint(body + 8, 2)
                    guard version <= 1 else { throw NativeMusicAudioCacheFailure.miss }
                    codec = type == "mp4a" ? "aac" : "alac"
                    let children = body + (version == 1 ? 44 : 28)
                    guard children <= next else { throw NativeMusicAudioCacheFailure.miss }
                    try walk(children, next, depth: depth + 1)
                } else if type == "dref" {
                    // Exactly one self-contained data reference. Never allow
                    // QuickTime/MP4 external URLs, aliases or local file paths.
                    guard next - body == 20, try uint(body, 4) == 0, try uint(body + 4, 4) == 1,
                          try uint(body + 8, 4) == 12, try text(body + 12, 4) == "url ",
                          try uint(body + 16, 4) == 1 else { throw NativeMusicAudioCacheFailure.miss }
                }
                p = next
            }
            guard p == end else { throw NativeMusicAudioCacheFailure.miss }
        }
        try walk(0, file.size, depth: 0)
        guard movie, payload, let codec else { throw NativeMusicAudioCacheFailure.miss }
        return Result(container: "m4a", codec: codec)
    }

    private func bytes(_ offset: Int64, _ count: Int) throws -> [UInt8] {
        try file.checkCancellation()
        guard offset >= 0, count >= 0, count <= 65_536, offset <= file.size,
              Int64(count) <= file.size - offset else { throw NativeMusicAudioCacheFailure.miss }
        if offset < windowStart || offset + Int64(count) > windowStart + Int64(window.count) {
            windowStart = offset
            window = try file.read(offset: offset, count: min(65_536, Int(file.size - offset)))
        }
        let start = Int(offset - windowStart)
        return Array(window[start..<(start + count)])
    }
    private func text(_ offset: Int64, _ count: Int) throws -> String {
        String(decoding: try bytes(offset, count), as: UTF8.self)
    }
    private func uint(_ offset: Int64, _ count: Int, little: Bool = false) throws -> UInt64 {
        let value = try bytes(offset, count)
        return (little ? Array(value.reversed()) : value).reduce(0) { $0 << 8 | UInt64($1) }
    }
}
