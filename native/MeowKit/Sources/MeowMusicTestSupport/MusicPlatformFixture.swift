import Foundation
import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowRuntime

public protocol MusicPlatformFixtureResponding: Sendable {
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue
}
public extension MusicPlatformFixtureResponding {
    func makeClient() throws -> MusicPlatformClient {
        try MusicPlatformFixture { try await self.respond($0) }.makeClient()
    }
}
public struct MusicPlatformFixtureRouter: MusicPlatformFixtureResponding {
    private let peers: [String: any MusicPlatformFixtureResponding]
    public init(_ peers: [String: any MusicPlatformFixtureResponding]) { self.peers = peers }
    public func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        guard let peer = peers[request.platform] else { throw MusicPlatformFixture.Failure.remote("UNSUPPORTED") }
        return try await peer.respond(request)
    }
}

/// Synthetic canonical runtime replies. Platform wire/crypto vectors belong to
/// the shared Node public-operation tests, not a second native implementation.
public final class MusicPlatformFixture: @unchecked Sendable {
    public struct Request: Sendable {
        public let transportOperation: String
        public let fields: RuntimeValue
        public var platform: String { fields[music: "platform"].musicText }
        public var operation: String { fields[music: "operation"].musicText }
        public var parameters: RuntimeValue { fields[music: "params"] }
        public var candidate: RuntimeValue { fields[music: "candidate"] }
    }
    public enum Failure: Error, Sendable { case remote(String) }
    public typealias Handler = @Sendable (Request) async throws -> RuntimeValue
    public let runtime: RuntimeHTTPClient
    private let host: String
    private let handler: Handler
    private let controlHandler: Handler?
    private let lock = NSLock()
    private var recorded: [Request] = []

    public init(controlHandler: Handler? = nil,
                handler: @escaping Handler = { _ in throw Failure.remote("UNSUPPORTED") }) throws {
        host = UUID().uuidString.lowercased() + ".music-fixture.invalid"
        self.handler = handler
        self.controlHandler = controlHandler
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MusicPlatformFixtureProtocol.self]
        runtime = try RuntimeHTTPClient(baseURL: URL(string: "http://" + host)!,
                                        configuration: configuration)
        MusicPlatformFixtureProtocol.register(host: host, fixture: self)
    }
    deinit { MusicPlatformFixtureProtocol.unregister(host: host) }
    public func makeClient() -> MusicPlatformClient { MusicPlatformClient(connection: { [self] in runtime }) }
    public func requests() -> [Request] { lock.lock(); defer { lock.unlock() }; return recorded }
    private func record(_ request: Request) {
        lock.lock(); recorded.append(request); lock.unlock()
    }
    fileprivate func reply(to request: URLRequest, body: Data) async -> RuntimeValue {
        do {
            let fields = try RuntimeJSON.decode(body)
            let value = Request(transportOperation: request.url!.lastPathComponent, fields: fields)
            record(value)
            let result: RuntimeValue
            switch value.transportOperation {
            case "session.update", "cache.clear":
                if let controlHandler { result = try await controlHandler(value) }
                else {
                    result = value.transportOperation == "session.update"
                        ? .object(["published": .bool(true)]) : .object(["cleared": .bool(true)])
                }
            case "request": result = try await handler(value)
            default: throw Failure.remote("UNSUPPORTED")
            }
            // The HTTP route sends the module envelope without another wrapper.
            // RuntimeHTTPClient consumes this one envelope.
            return .object(["ok": .bool(true), "contract": .string("music.platform.v1"),
                            "value": result])
        } catch {
            let code: String
            if case Failure.remote(let value) = error { code = value }
            else if error is CancellationError { code = "CANCELLED" }
            else if let failure = error as? MusicServiceError {
                switch failure {
                case .networkOffline, .connectionFailed: code = "NETWORK_ERROR"
                case .requestRejected: code = "UPSTREAM_ERROR"
                case .upstreamFailed: code = "UPSTREAM_HTTP"
                case .upstreamInvalid: code = "INVALID_RESPONSE"
                case .sessionExpired: code = "AUTH_EXPIRED"
                case .staleResult: code = "STALE_SESSION"
                case .storageFailed: code = "STORAGE_ERROR"
                case .commitUnconfirmed: code = "COMMIT_UNKNOWN"
                default: code = failure.rawValue
                }
            }
            else { code = "INVALID_RESPONSE" }
            return .object(["ok": .bool(false), "contract": .string("music.platform.v1"), "code": .string(code)])
        }
    }
    public static func song(_ track: MusicNativeTrackData) throws -> RuntimeValue {
        let info = try RuntimeJSON.decode(track.data)
        let id = try info[music: "songmid"].musicRequiredText()
        let sourceKey = String(decoding: try RuntimeJSON.encode(.array([
            .string(track.platform), .string(id),
            .string(track.platform == "kg" ? info[music: "hash"].musicText.lowercased() : "")
        ])), as: UTF8.self)
        return .object(["title": info[music: "name"], "artist": info[music: "singer"],
            "album": info[music: "albumName"], "duration": info[music: "duration"],
            "cover_url": info[music: "img"], "artistNames": info[music: "artistNames"],
            "sources": .array([.object(["source": .string(track.platform), "sourceKey": .string(sourceKey), "musicInfo": info])])])
    }
    public static func page(_ tracks: [MusicNativeTrackData], platform: String,
                            total: Int? = nil, page: Int = 1, limit: Int = 30,
                            continuation: RuntimeValue = .null) throws -> RuntimeValue {
        .object(["platform": .string(platform), "items": .array(try tracks.map(song)),
            "page": .number(Double(page)), "limit": .number(Double(limit)),
            "total": .number(Double(total ?? tracks.count)), "consumed": .number(Double(tracks.count)),
            "more": .bool(continuation != .null), "continuation": continuation])
    }
    public static func page(_ result: MusicNativeTrackPage, platform: String) throws -> RuntimeValue {
        try page(result.items, platform: platform, total: result.total,
            continuation: result.nextPage.map { .object(["page": .number(Double($0))]) } ?? .null)
    }
}

private final class MusicPlatformFixtureProtocol: URLProtocol, @unchecked Sendable {
    private final class WeakFixture {
        weak var value: MusicPlatformFixture?
        init(_ value: MusicPlatformFixture) { self.value = value }
    }
    private static let registryLock = NSLock()
    private static var registry: [String: WeakFixture] = [:]
    static func register(host: String, fixture: MusicPlatformFixture) {
        registryLock.lock(); registry[host] = WeakFixture(fixture); registryLock.unlock()
    }
    static func unregister(host: String) {
        registryLock.lock(); registry.removeValue(forKey: host); registryLock.unlock()
    }
    private static func fixture(for host: String) -> MusicPlatformFixture? {
        registryLock.lock(); defer { registryLock.unlock() }; return registry[host]?.value
    }
    private let delivery = NSRecursiveLock()
    private var stopped = false
    private var work: Task<Void, Never>?
    override class func canInit(with request: URLRequest) -> Bool {
        request.url?.host?.hasSuffix(".music-fixture.invalid") == true
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let fixture = Self.fixture(for: request.url!.host!) else {
            client?.urlProtocol(self, didFailWithError: URLError(.resourceUnavailable)); return
        }
        let body: Data
        if let bytes = request.httpBody { body = bytes }
        else if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let size = stream.read(&buffer, maxLength: buffer.count)
                if size <= 0 { break }; data.append(contentsOf: buffer.prefix(size))
            }
            body = data
        } else { body = Data("{}".utf8) }
        delivery.lock()
        guard !stopped else { delivery.unlock(); return }
        work = Task { [self] in
            let reply = await fixture.reply(to: request, body: body)
            deliver(reply)
        }
        delivery.unlock()
    }
    private func deliver(_ value: RuntimeValue) {
        delivery.lock(); defer { delivery.unlock() }
        work = nil
        guard !stopped else { return }
        do {
            let data = try RuntimeJSON.encode(value)
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": "application/json",
                               "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {
        delivery.lock()
        stopped = true
        let retired = work; work = nil
        delivery.unlock()
        retired?.cancel()
    }
}
