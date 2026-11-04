import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

final class QQPublicPersistenceClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date: Date
    init(_ date: Date = Date(timeIntervalSince1970: 1_800_000_000)) { self.date = date }
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: Double) { lock.lock(); defer { lock.unlock() }; date.addTimeInterval(seconds) }
}

final class QQPublicPersistenceCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func add() { lock.lock(); defer { lock.unlock() }; value += 1 }
    func get() -> Int { lock.lock(); defer { lock.unlock() }; return value }
}

/// Deliberately non-cooperative wait. The production fence must reject a late
/// successful transport response, not rely only on Task.cancel().
actor QQPublicPersistenceGate {
    nonisolated let entered = XCTestExpectation(description: "QQ public native request entered")
    private var hasEntered = false
    private var released = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if !hasEntered { hasEntered = true; entered.fulfill() }
        if !released { await withCheckedContinuation { waiting.append($0) } }
    }
    func release() {
        released = true
        waiting.forEach { $0.resume() }; waiting.removeAll()
    }
}

final class QQPublicPersistenceMemory: MusicQQPublicPersisting, @unchecked Sendable {
    private let lock = NSLock()
    private var bytes: Data?
    private var readError: MusicServiceError?
    private var commitError: MusicServiceError?
    private var applyBeforeError = false
    private var cancelRead = false
    private var cancelCommit = false
    private var reads = 0
    private var commits = 0
    init(_ bytes: Data? = nil) { self.bytes = bytes }
    func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }; reads += 1
        if let readError { throw readError }
        if cancelRead {
            cancelRead = false
            withUnsafeCurrentTask { $0?.cancel() }
        }
        return bytes
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }; commits += 1
        guard bytes == expected else { throw MusicServiceError.storageConflict }
        if commitError == nil || applyBeforeError { bytes = replacement }
        if let commitError { throw commitError }
        if cancelCommit {
            cancelCommit = false
            withUnsafeCurrentTask { $0?.cancel() }
        }
    }
    func peek() -> Data? { lock.lock(); defer { lock.unlock() }; return bytes }
    func counts() -> (reads: Int, commits: Int) { lock.lock(); defer { lock.unlock() }; return (reads, commits) }
    func replace(_ bytes: Data?) { lock.lock(); defer { lock.unlock() }; self.bytes = bytes }
    func failRead(_ error: MusicServiceError?) { lock.lock(); defer { lock.unlock() }; readError = error }
    func cancelOnNextRead() { lock.lock(); defer { lock.unlock() }; cancelRead = true }
    func cancelOnNextCommit() { lock.lock(); defer { lock.unlock() }; cancelCommit = true }
    func failCommit(_ error: MusicServiceError?, afterApplying: Bool = false) {
        lock.lock(); defer { lock.unlock() }; commitError = error; applyBeforeError = afterApplying
    }
}

actor QQPublicPersistenceHTTP: MusicAccountHTTPTransport {
    struct Seen: Sendable {
        let request: MusicAccountHTTPRequest
        let document: Data?
    }
    private var requests: [Seen] = []
    private var failures: [String: Int] = [:]
    private var overrides: [String: Data] = [:]
    private var held: [String: [QQPublicPersistenceGate]] = [:]
    private let document: QQPublicPersistenceMemory?
    nonisolated let cancellations = QQPublicPersistenceCounter()
    init(document: QQPublicPersistenceMemory? = nil) { self.document = document }
    func hold(_ method: String, gate: QQPublicPersistenceGate) {
        held[method, default: []].append(gate)
    }
    func waitForCalls(_ method: String, count: Int) async throws {
        for _ in 0..<10_000 {
            var matched = 0
            for seen in requests where (try? Self.method(seen.request)) == method { matched += 1 }
            if matched >= count { return }
            await Task.yield()
        }
        throw MusicServiceError.timeout
    }
    func fail(_ method: String, status: Int?) { failures[method] = status }
    func override(_ method: String, bytes: Data?) { overrides[method] = bytes }
    func calls() -> [Seen] { requests }
    func methods() throws -> [String] { try requests.map { try Self.method($0.request) } }
    static func root(_ request: MusicAccountHTTPRequest) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any])
    }
    static func method(_ request: MusicAccountHTTPRequest) throws -> String {
        if request.url.host == "api.tencentmusic.com" { return "GetQimei" }
        let row = try XCTUnwrap(root(request)["req_0"] as? [String: Any])
        return try XCTUnwrap(row["method"] as? String)
    }
    static func reply(_ data: [String: Any]) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["code": 0, "req_0": ["code": 0, "data": data]])
    }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        requests.append(.init(request: request, document: document?.peek()))
        let method = try Self.method(request)
        if let gate = held[method]?.first {
            held[method]?.removeFirst()
            let cancellations = self.cancellations
            await withTaskCancellationHandler(operation: { await gate.wait() },
                                              onCancel: { cancellations.add() })
        }
        if let status = failures[method] { return .init(status: status, data: Data("synthetic-private-error".utf8)) }
        if let bytes = overrides[method] { return .init(status: 200, data: bytes) }
        if method == "GetQimei" {
            let data = try JSONSerialization.data(withJSONObject: ["code": 0, "data": ["q16": "synthetic-q16", "q36": "synthetic-q36"]])
            return .init(status: 200, data: try JSONSerialization.data(withJSONObject: [
                "code": 0, "data": String(decoding: data, as: UTF8.self)
            ]))
        }
        let song: [String: Any] = [
            "mid": "SyntheticMID", "id": "9007199254740993", "type": 1, "title": "Synthetic",
            "singer": [["name": "Fixture"]], "album": ["mid": "SyntheticAlbum", "name": "Album"],
            "interval": 120, "file": ["media_mid": "SyntheticMedia", "size_128mp3": 100]
        ]
        let data: [String: Any]
        switch method {
        case "GetSession": data = ["session": ["uid": "synthetic-anonymous", "sid": "synthetic-session"]]
        case "DoSearchForQQMusicMobile": data = ["meta": ["sum": 1, "nextpage": -1], "body": ["item_song": [song]]]
        case "CgiGetTrackInfo": data = ["tracks": [song]]
        case "GetPlayLyricInfo": data = ["songID": "9007199254740993", "lyric": "", "trans": "", "roma": ""]
        default: throw MusicServiceError.upstreamFailed
        }
        return .init(status: 200, data: try Self.reply(data))
    }
}

struct QQPublicPersistenceFixture {
    let clock: QQPublicPersistenceClock
    let document: QQPublicPersistenceMemory
    let http: QQPublicPersistenceHTTP
    let service: NativeMusicQQPublicService
    init(document: QQPublicPersistenceMemory = .init(), clock: QQPublicPersistenceClock = .init()) {
        self.document = document; self.clock = clock
        let http = QQPublicPersistenceHTTP(document: document)
        self.http = http
        service = NativeMusicQQPublicService(persistence: document, transport: http, clock: { clock.now() })
    }
    func enable(_ generation: UInt64 = 1) async throws {
        try await service.setAccess(available: true, generation: generation)
    }
    func search() async throws { _ = try await service.search(query: "synthetic", page: 1, limit: 30) }
    func stored() throws -> [String: Any] {
        try MusicQQPublicPersistenceCodec.decode(XCTUnwrap(document.peek()))
    }
    static func seed(_ digit: String = "a") -> [String: Any] {
        [
            "android_id": String(repeating: digit, count: 16), "open_udid": String(repeating: digit, count: 32),
            "open_udid2": String(repeating: digit, count: 32), "imei": "000000000000000",
            "brand": "vivo", "manufacturer": "vivo", "model": "V2408A", "board": "sun",
            "product": "PD2408", "device": "PD2408", "host": "comdg01150014",
            "os_ver": "15", "sdk": 35, "first_api_level": 35, "kernel": "synthetic-kernel"
        ]
    }
}

final class QQPublicPersistenceKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var keys: [String: Data] = [:]
    private var locked = false
    func setLocked(_ value: Bool) { lock.lock(); defer { lock.unlock() }; locked = value }
    func readKey(namespace: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        guard !locked else { throw EncryptedDocumentKeyError.locked }
        return keys[namespace]
    }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        guard !locked else { throw EncryptedDocumentKeyError.locked }
        if let key = keys[namespace] { return key }
        let key = Data(repeating: 0x45, count: 32) // Synthetic only; never a product key.
        keys[namespace] = key
        return key
    }
}
