import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

enum HLSByteRange: Equatable {
    case closed(Int64, Int64), from(Int64), suffix(Int64)

    init(_ raw: String) throws {
        guard raw.hasPrefix("bytes=") else { throw PlaybackNetworkError.rangeUnsupported }
        let parts = raw.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2 else { throw PlaybackNetworkError.rangeUnsupported }
        if parts[0].isEmpty, let count = PlaybackNetworkPlan.unsignedInt64(String(parts[1])), count > 0 {
            self = .suffix(count)
        } else if let start = PlaybackNetworkPlan.unsignedInt64(String(parts[0])) {
            if parts[1].isEmpty { self = .from(start) }
            else if let end = PlaybackNetworkPlan.unsignedInt64(String(parts[1])), end >= start { self = .closed(start, end) }
            else { throw PlaybackNetworkError.rangeUnsupported }
        } else { throw PlaybackNetworkError.rangeUnsupported }
    }
    var header: String {
        switch self {
        case .closed(let a, let b): return "bytes=\(a)-\(b)"
        case .from(let a): return "bytes=\(a)-"
        case .suffix(let n): return "bytes=-\(n)"
        }
    }
    func resolved(total: Int64) -> ClosedRange<Int64>? {
        guard total > 0 else { return nil }
        switch self {
        case .closed(let a, let b): return a < total ? a...min(b, total - 1) : nil
        case .from(let a): return a < total ? a...(total - 1) : nil
        case .suffix(let n): return max(0, total - n)...(total - 1)
        }
    }
}

struct HLSClientRequest {
    let method: String
    let path: String
    let range: HLSByteRange?

    init(_ data: Data) throws {
        guard let text = String(data: data, encoding: .utf8),
              text.hasSuffix("\r\n\r\n") else { throw PlaybackNetworkError.invalidResponse }
        let lines = text.components(separatedBy: "\r\n").dropLast(2)
        guard let first = lines.first else { throw PlaybackNetworkError.invalidResponse }
        let fields = first.split(separator: " ", omittingEmptySubsequences: false)
        guard fields.count == 3, ["GET", "HEAD"].contains(String(fields[0])) else {
            throw PlaybackNetworkError.invalidResponse
        }
        method = String(fields[0])
        path = String(fields[1])
        var rangeHeader: String?
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            if line[..<colon].lowercased() == "range" {
                rangeHeader = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            }
        }
        range = try rangeHeader.map(HLSByteRange.init)
    }
}

typealias HLSRequestPolicy = PlaybackHTTPRequestPolicy

extension PlaybackHTTPRequestPolicy {
    init(plan: PlaybackNetworkPlan, resource: HLSResource) throws {
        try self.init(plan: plan, url: resource.url)
    }

    func request(method: String, range: HLSByteRange?) -> URLRequest {
        request(method: method, rangeHeader: range?.header)
    }
}

struct HLSUpstreamResponse {
    let status: Int
    let length: Int64?
    let mime: String
    let contentRange: String?
    let discardBody: Bool

    static func parse(_ response: HTTPURLResponse, kind: HLSResourceKind, request: HLSClientRequest) throws -> Self {
        let rawLength = response.value(forHTTPHeaderField: "Content-Length")
        let length = rawLength.flatMap(PlaybackNetworkPlan.unsignedInt64)
        let rawType = response.value(forHTTPHeaderField: "Content-Type")?.split(separator: ";", maxSplits: 1).first.map(String.init)
        let mime = rawType?.trimmingCharacters(in: .whitespaces).lowercased() ?? "application/octet-stream"
        guard mime != "text/html" else { throw PlaybackNetworkError.unsupportedContent }
        if kind == .playlist {
            guard response.statusCode == 200 else { throw PlaybackNetworkError.httpStatus(response.statusCode) }
            return .init(status: 200, length: length, mime: mime, contentRange: nil, discardBody: false)
        }
        switch response.statusCode {
        case 200:
            guard request.range == nil else { throw PlaybackNetworkError.rangeUnsupported }
            return .init(status: 200, length: length, mime: mime, contentRange: nil, discardBody: request.method == "HEAD")
        case 206:
            guard let wanted = request.range, let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  let actual = PlaybackNetworkPlan.parseContentRange(raw),
                  wanted.resolved(total: actual.total) == (actual.start...actual.end) else {
                throw PlaybackNetworkError.invalidResponse
            }
            return .init(status: 206, length: actual.end - actual.start + 1, mime: mime,
                         contentRange: raw, discardBody: request.method == "HEAD")
        case 416:
            guard let wanted = request.range, let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  raw.hasPrefix("bytes */"), let total = PlaybackNetworkPlan.unsignedInt64(String(raw.dropFirst(8))),
                  wanted.resolved(total: total) == nil else { throw PlaybackNetworkError.invalidResponse }
            return .init(status: 416, length: 0, mime: mime, contentRange: raw, discardBody: true)
        default: throw PlaybackNetworkError.httpStatus(response.statusCode)
        }
    }

    func head(chunked: Bool) -> Data {
        let reason = status == 206 ? "Partial Content" : status == 416 ? "Range Not Satisfiable" : "OK"
        var text = "HTTP/1.1 \(status) \(reason)\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Type: \(mime)\r\n"
        if let contentRange { text += "Content-Range: \(contentRange)\r\n" }
        if let length { text += "Content-Length: \(length)\r\n" }
        else if chunked { text += "Transfer-Encoding: chunked\r\n" }
        return Data((text + "\r\n").utf8)
    }
}
