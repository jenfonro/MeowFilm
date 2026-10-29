import Foundation
import zlib
import MeowMusicFeature

enum MusicAccountFeedbackCompression {
    static func compress(_ input: Data, gzip: Bool = false) throws -> Data {
        guard input.count <= 128 * 1024 else { throw MusicServiceError.limit }
        var stream = z_stream()
        guard deflateInit2_(&stream, Z_DEFAULT_COMPRESSION, Z_DEFLATED, gzip ? 31 : 15, 8,
                           Z_DEFAULT_STRATEGY, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw MusicServiceError.unavailable
        }
        defer { deflateEnd(&stream) }
        return try input.withUnsafeBytes { raw in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: raw.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var output = Data()
            while true {
                var chunk = [UInt8](repeating: 0, count: 4096)
                let result = chunk.withUnsafeMutableBytes { buffer -> Int32 in
                    stream.next_out = buffer.bindMemory(to: Bytef.self).baseAddress; stream.avail_out = 4096
                    return deflate(&stream, Z_FINISH)
                }
                output.append(contentsOf: chunk.prefix(4096 - Int(stream.avail_out)))
                guard output.count <= 256 * 1024 else { throw MusicServiceError.limit }
                if result == Z_STREAM_END { return output }
                guard result == Z_OK else { throw MusicServiceError.upstreamInvalid }
            }
        }
    }
    static func inflateZlib(_ input: Data, limit: Int = 65536) throws -> Data {
        guard !input.isEmpty, input.count <= 65536 else { throw MusicServiceError.limit }
        var stream = z_stream()
        guard inflateInit_(&stream, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw MusicServiceError.unavailable
        }
        defer { inflateEnd(&stream) }
        return try input.withUnsafeBytes { raw in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: raw.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var output = Data()
            while true {
                var chunk = [UInt8](repeating: 0, count: 4096)
                let result = chunk.withUnsafeMutableBytes { buffer -> Int32 in
                    stream.next_out = buffer.bindMemory(to: Bytef.self).baseAddress; stream.avail_out = 4096
                    return inflate(&stream, Z_NO_FLUSH)
                }
                let size = 4096 - Int(stream.avail_out)
                guard output.count + size <= limit else { throw MusicServiceError.limit }
                output.append(contentsOf: chunk.prefix(size))
                if result == Z_STREAM_END {
                    guard stream.avail_in == 0 else { throw MusicServiceError.upstreamInvalid }
                    return output
                }
                guard result == Z_OK, size > 0 || stream.avail_in > 0 else { throw MusicServiceError.upstreamInvalid }
            }
        }
    }
}
