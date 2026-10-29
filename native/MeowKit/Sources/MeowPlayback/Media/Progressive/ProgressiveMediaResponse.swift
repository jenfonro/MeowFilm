import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

struct ProgressiveReadRange: Equatable {
    let start: Int64
    let endExclusive: Int64? // nil = to EOF, NOT one unbounded HTTP request.

    init(requestedOffset: Int64, currentOffset: Int64, requestedLength: Int, toEnd: Bool) throws {
        guard requestedOffset >= 0, currentOffset >= 0, requestedLength >= 0 else {
            throw PlaybackNetworkError.invalidResponse
        }
        start = max(requestedOffset, currentOffset)
        let (end, overflow) = requestedOffset.addingReportingOverflow(Int64(requestedLength))
        guard !overflow, toEnd || start <= end else { throw PlaybackNetworkError.invalidResponse }
        endExclusive = toEnd ? nil : end
    }
}

struct ProgressiveContentInformation: Equatable {
    let length: Int64
    let mime: String
    let supportsRanges: Bool
    let validator: String?
}

struct ProgressiveMediaResponse {
    let information: ProgressiveContentInformation
    let bodyBytes: Int64

    static func parse(
        _ response: HTTPURLResponse, start: Int64, end: Int64,
        previous: ProgressiveContentInformation?, fallbackExtension: String
    ) throws -> Self {
        guard let url = response.url else { throw PlaybackNetworkError.invalidResponse }
        _ = try PlaybackHTTPOrigin(url)
        guard [200, 206, 416].contains(response.statusCode) else {
            throw PlaybackNetworkError.httpStatus(response.statusCode)
        }
        let type = (response.value(forHTTPHeaderField: "Content-Type") ?? "")
            .split(separator: ";", maxSplits: 1).first.map(String.init)?
            .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? ""
        guard !PlaybackNetworkPlan.hlsMIMEs.contains(type),
              !PlaybackNetworkPlan.hlsExtensions.contains(url.pathExtension.lowercased()) else {
            throw PlaybackNetworkError.hlsRequiresExplicitFormat
        }
        guard type != "text/html", type != "application/json", type != "text/plain" else {
            throw PlaybackNetworkError.unsupportedContent
        }
        if let encoding = response.value(forHTTPHeaderField: "Content-Encoding"),
           encoding.lowercased() != "identity" { throw PlaybackNetworkError.invalidResponse }

        let total: Int64
        let count: Int64
        let supportsRanges: Bool
        switch response.statusCode {
        case 206:
            guard let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  let range = parseContentRange(raw), range.start == start,
                  range.end == min(end, range.total - 1) else { throw PlaybackNetworkError.invalidResponse }
            total = range.total; count = range.end - range.start + 1; supportsRanges = true
            if let length = response.value(forHTTPHeaderField: "Content-Length"),
               unsigned(length) != count { throw PlaybackNetworkError.invalidResponse }
        case 200:
            guard start == 0 else {
                throw previous?.validator == nil ? PlaybackNetworkError.rangeUnsupported : .resourceChanged
            }
            guard let raw = response.value(forHTTPHeaderField: "Content-Length"),
                  let length = unsigned(raw), length > 0, length - 1 <= end else {
                // Never accept an unbounded full-file response as a Range.
                throw PlaybackNetworkError.rangeUnsupported
            }
            total = length; count = length; supportsRanges = false
        case 416:
            guard let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  raw.hasPrefix("bytes */"), let length = unsigned(String(raw.dropFirst(8))),
                  length >= 0, start >= length else { throw PlaybackNetworkError.invalidResponse }
            total = length; count = 0; supportsRanges = true
        default:
            throw PlaybackNetworkError.httpStatus(response.statusCode)
        }
        let mime = response.statusCode == 416 && type.isEmpty
            ? (previous?.mime ?? resolvedMIME(type, fileExtension: fallbackExtension))
            : resolvedMIME(type, fileExtension: fallbackExtension)
        let etag = response.value(forHTTPHeaderField: "ETag")
        let validator = (etag?.hasPrefix("\"") == true && etag?.hasSuffix("\"") == true)
            ? etag : response.value(forHTTPHeaderField: "Last-Modified")
        if let validator {
            guard validator.utf8.count <= 512, !validator.utf8.contains(where: { $0 < 32 || $0 == 127 }) else {
                throw PlaybackNetworkError.invalidResponse
            }
        }
        if let previous {
            guard total == previous.length, mime == previous.mime,
                  previous.validator == nil || validator == previous.validator || response.statusCode == 416 else {
                throw PlaybackNetworkError.resourceChanged
            }
        }
        return Self(information: .init(length: total, mime: mime, supportsRanges: supportsRanges,
                                       validator: validator), bodyBytes: count)
    }

    static func parseContentRange(_ raw: String) -> (start: Int64, end: Int64, total: Int64)? {
        guard raw.utf8.count <= 96, raw.hasPrefix("bytes ") else { return nil }
        let pieces = raw.dropFirst(6).split(separator: "/", omittingEmptySubsequences: false)
        guard pieces.count == 2 else { return nil }
        let ends = pieces[0].split(separator: "-", omittingEmptySubsequences: false)
        guard ends.count == 2, let start = unsigned(String(ends[0])), let end = unsigned(String(ends[1])),
              let total = unsigned(String(pieces[1])), end >= start, total > end else { return nil }
        return (start, end, total)
    }

    private static func unsigned(_ value: String) -> Int64? {
        guard !value.isEmpty, value.utf8.count <= 19, value.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }
        return Int64(value)
    }

    private static func resolvedMIME(_ type: String, fileExtension ext: String) -> String {
        if !type.isEmpty && !["application/octet-stream", "binary/octet-stream"].contains(type) { return type }
        return [
            "mp4": "video/mp4", "m4v": "video/mp4", "mov": "video/quicktime",
            "mp3": "audio/mpeg", "m4a": "audio/mp4", "aac": "audio/aac",
            "wav": "audio/wav", "aiff": "audio/aiff", "flac": "audio/flac",
            "caf": "audio/x-caf"
        ][ext.lowercased()] ?? "application/octet-stream"
    }

    static func isPlaylistPrefix(_ data: Data) -> Bool {
        var bytes = Array(data.prefix(16))
        if bytes.starts(with: [0xef, 0xbb, 0xbf]) { bytes.removeFirst(3) }
        return bytes.starts(with: Array("#EXTM3U".utf8))
    }
}
