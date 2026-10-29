import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

enum HLSByteRange: Equatable {
    case closed(Int64, Int64), from(Int64), suffix(Int64)

    init(_ raw: String) throws {
        guard raw.utf8.count <= 96, raw.hasPrefix("bytes=") else { throw PlaybackNetworkError.rangeUnsupported }
        let parts = raw.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2 else { throw PlaybackNetworkError.rangeUnsupported }
        if parts[0].isEmpty, let count = Self.unsigned(String(parts[1])), count > 0 {
            self = .suffix(count)
        } else if let start = Self.unsigned(String(parts[0])) {
            if parts[1].isEmpty { self = .from(start) }
            else if let end = Self.unsigned(String(parts[1])), end >= start { self = .closed(start, end) }
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
    static func unsigned(_ text: String) -> Int64? {
        guard !text.isEmpty, text.utf8.count <= 19, text.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }
        return Int64(text)
    }
}

struct HLSClientRequest {
    static let maximumHeaderBytes = 16 * 1024
    let method: String
    let path: String
    let range: HLSByteRange?

    init(_ data: Data, port: UInt16) throws {
        guard data.count <= Self.maximumHeaderBytes, let text = String(data: data, encoding: .ascii),
              text.hasSuffix("\r\n\r\n") else { throw PlaybackNetworkError.invalidResponse }
        let lines = text.components(separatedBy: "\r\n").dropLast(2)
        guard let first = lines.first, lines.count <= 33 else { throw PlaybackNetworkError.invalidResponse }
        let fields = first.split(separator: " ", omittingEmptySubsequences: false)
        guard fields.count == 3, ["GET", "HEAD"].contains(String(fields[0])),
              ["HTTP/1.1", "HTTP/1.0"].contains(String(fields[2])) else { throw PlaybackNetworkError.invalidResponse }
        method = String(fields[0]); path = String(fields[1])
        guard path.utf8.count <= 256, path.hasPrefix("/"), !path.contains("?"),
              path.utf8.allSatisfy({ (45...57).contains($0) || (97...122).contains($0) }) else {
            throw PlaybackNetworkError.invalidResponse
        }
        var headers: [String: String] = [:]
        let token = Set("!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyz".utf8)
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { throw PlaybackNetworkError.invalidResponse }
            let name = line[..<colon].lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty, name.utf8.allSatisfy({ token.contains($0) }),
                  value.utf8.allSatisfy({ (32...126).contains($0) }), headers[name] == nil else {
                throw PlaybackNetworkError.invalidResponse
            }
            headers[name] = value
        }
        guard headers["host"] == "127.0.0.1:\(port)",
              headers["content-length"] == nil || headers["content-length"] == "0",
              headers["transfer-encoding"] == nil, headers["expect"] == nil,
              headers["upgrade"] == nil, headers["origin"] == nil else {
            throw PlaybackNetworkError.invalidResponse
        }
        range = try headers["range"].map(HLSByteRange.init)
        // All other local fields, including Cookie/Authorization, are ignored.
        // They cannot modify upstream headers or grant resource authority.
    }
}

struct HLSRequestPolicy {
    private let initialOrigin: PlaybackHTTPOrigin
    private var headers: [String: String]
    private(set) var permitsHeaders: Bool
    private(set) var current: URL
    private var redirects = 0

    init(plan: PlaybackNetworkPlan, resource: HLSResource) throws {
        initialOrigin = try PlaybackHTTPOrigin(plan.url); headers = plan.headers
        current = try HLSResourceRegistry.validateURL(resource.url)
        let resourceOrigin = try PlaybackHTTPOrigin(current)
        permitsHeaders = resource.permitsHeaders && resourceOrigin == initialOrigin
    }
    mutating func redirect(to proposed: URL) throws {
        guard redirects < 5 else { throw PlaybackNetworkError.redirectLimit }
        let target = try HLSResourceRegistry.validateURL(proposed)
        let before = try PlaybackHTTPOrigin(current), after = try PlaybackHTTPOrigin(target)
        guard !(before.scheme == "https" && after.scheme != "https") else { throw PlaybackNetworkError.redirectDenied }
        if before != after { permitsHeaders = false }
        current = target; redirects += 1
    }
    mutating func revoke() { headers.removeAll(); permitsHeaders = false }
    func request(method: String, range: HLSByteRange?) -> URLRequest {
        var request = URLRequest(url: current, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.httpMethod = method; request.httpShouldHandleCookies = false
        for (name, value) in headers where permitsHeaders || ["user-agent", "accept"].contains(name) {
            request.setValue(value, forHTTPHeaderField: name)
        }
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        if let range { request.setValue(range.header, forHTTPHeaderField: "Range") }
        return request
    }
}

struct HLSUpstreamResponse {
    let status: Int
    let length: Int64?
    let mime: String
    let contentRange: String?
    let discardBody: Bool

    static func parse(_ response: HTTPURLResponse, kind: HLSResourceKind, request: HLSClientRequest) throws -> Self {
        guard response.value(forHTTPHeaderField: "Content-Encoding").map({ $0.lowercased() == "identity" }) ?? true else {
            throw PlaybackNetworkError.invalidResponse
        }
        let rawLength = response.value(forHTTPHeaderField: "Content-Length")
        let length = rawLength.flatMap(HLSByteRange.unsigned)
        guard rawLength == nil || length != nil else { throw PlaybackNetworkError.invalidResponse }
        let rawType = response.value(forHTTPHeaderField: "Content-Type")?.split(separator: ";", maxSplits: 1).first.map(String.init)
        let mime = rawType?.trimmingCharacters(in: .whitespaces).lowercased() ?? "application/octet-stream"
        guard mime.utf8.count <= 128, mime.utf8.allSatisfy({ (32...126).contains($0) }) else {
            throw PlaybackNetworkError.invalidResponse
        }
        if kind == .playlist {
            guard response.statusCode == 200 else { throw PlaybackNetworkError.httpStatus(response.statusCode) }
            guard length == nil || length! <= HLSPlaylist.maximumInputBytes else { throw PlaybackNetworkError.resourceBudgetExceeded }
            guard mime != "text/html", mime != "application/json" else { throw PlaybackNetworkError.unsupportedContent }
            return .init(status: 200, length: length, mime: mime, contentRange: nil, discardBody: false)
        }
        guard !PlaybackNetworkPlan.hlsMIMEs.contains(mime), mime != "text/html",
              kind == .metadata || mime != "application/json" else { throw PlaybackNetworkError.unsupportedContent }
        let maximum: Int64 = kind == .media ? 512 * 1024 * 1024 : 64 * 1024
        guard length == nil || request.method == "HEAD" || length! <= maximum else {
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        switch response.statusCode {
        case 200:
            guard request.range == nil else { throw PlaybackNetworkError.rangeUnsupported }
            return .init(status: 200, length: length, mime: mime, contentRange: nil, discardBody: request.method == "HEAD")
        case 206:
            guard let wanted = request.range, let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  let actual = ProgressiveMediaResponse.parseContentRange(raw),
                  wanted.resolved(total: actual.total) == (actual.start...actual.end),
                  length == nil || length == actual.end - actual.start + 1 else { throw PlaybackNetworkError.invalidResponse }
            return .init(status: 206, length: actual.end - actual.start + 1, mime: mime,
                         contentRange: raw, discardBody: request.method == "HEAD")
        case 416:
            guard let wanted = request.range, let raw = response.value(forHTTPHeaderField: "Content-Range"),
                  raw.hasPrefix("bytes */"), let total = HLSByteRange.unsigned(String(raw.dropFirst(8))),
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
