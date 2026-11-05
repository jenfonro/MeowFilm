import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

// All values are synthetic. No Keychain, network, Node or reference checkout.
final class NativeMusicMemoryDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var value: Data?
    private var failure: MusicServiceError?
    private var reads = 0, writes = 0
    init(_ value: Data? = nil) { self.value = value }
    func read() throws -> Data? { lock.lock(); defer { lock.unlock() }; reads += 1; return value }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }
        if let failure { throw failure }
        guard value == expected else { throw MusicServiceError.storageConflict }
        value = replacement; writes += 1
    }
    func fail(_ value: MusicServiceError?) { lock.lock(); failure = value; lock.unlock() }
    func replace(_ value: Data?) { lock.lock(); self.value = value; lock.unlock() }
    func snapshot() -> (Data?, Int, Int) { lock.lock(); defer { lock.unlock() }; return (value, reads, writes) }
}
actor NativeMusicGate {
    private var waiting = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        waiting = true; starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async { if !waiting { await withCheckedContinuation { starts.append($0) } } }
    func release() { released = true; waits.forEach { $0.resume() }; waits.removeAll() }
}
final class NativeMusicFixtureClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = NativeMusicFixture.date
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: TimeInterval) { lock.lock(); value.addTimeInterval(seconds); lock.unlock() }
}
enum NativeMusicFixture {
    static let installation = "11111111-1111-4111-8111-111111111111"
    static let date = Date(timeIntervalSince1970: 1_800_000_000)
    static func track(_ mid: String = "SyntheticMID") -> MusicNativeTrackData {
        .init(platform: "tx", data: MJ.obj([("songmid", .string(mid)),
            ("qqSongId", .string("9007199254740993")), ("name", .string("Synthetic " + mid)), ("singer", .string("Fixture")),
            ("albumName", .string("Synthetic album")), ("albumMid", .string("SyntheticAlbum")), ("duration", .n(180)),
            ("types", .array([.obj([("type", .string("128k"))]), .obj([("type", .string("flac"))])]))]).bytes)
    }
    static func song(_ mid: String = "SyntheticMID") throws -> MJ { try MusicModel.normalize(track(mid)) }
    static func source(generation: Int = 1, hash: String = String(repeating: "a", count: 64)) -> MusicScriptSource {
        .init(installationID: installation, generation: generation, enabled: true, ready: true, version: "1",
              contentHash: hash, revisionHash: hash, capabilities: [.init(siteID: "tx", title: "LX synthetic", actions: ["musicUrl", "lyric"], qualities: ["128k", "flac"])])
    }
    static func importLibrary() throws -> MJ {
        let song = try song()
        var library = MusicModel.emptyLibrary; library["ownerId"] = .string("import-owner")
        library["songs"] = .obj([(song["songId"].string, song)])
        var favorites = library["collections"].array[0]; favorites["order"] = .strings([song["songId"].string])
        library["collections"] = .array([favorites]); return library
    }
    static func service(_ store: NativeMusicMemoryDocument = .init(), scripts: (any MusicScriptResolving)? = nil,
                        accounts: (any MusicNativeAccountProviding)? = nil,
                        qq: any MusicPublicQQProviding = NativeMusicQQFixture(),
                        http: any MusicNativeHTTPTransport = NativeMusicNoNetwork(),
                        sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                        clock: @escaping @Sendable () -> Date = { date }) async throws -> NativeMusicService {
        let service = NativeMusicService(document: store, scripts: scripts, accounts: accounts, qq: qq,
            sourceDiscovery: sourceDiscovery, transport: http, clock: clock, random: { 0 })
        try await service.setAccess(available: true, generation: 1); return service
    }
}
struct NativeMusicNoNetwork: MusicNativeHTTPTransport {
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse { throw MusicServiceError.unavailable }
}
actor NativeMusicQQFixture: MusicPublicQQProviding {
    var gate: NativeMusicGate?
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        if let gate { await gate.wait() }
        return .init(items: [NativeMusicFixture.track(query)], total: 1)
    }
    func lyrics(mid: String) async throws -> MusicNativeLyrics {
        if let gate { await gate.wait() }
        return .init(text: "[00:01.000]Synthetic\n[00:02.000]next", translation: "[00:01.000]合成")
    }
}
actor NativeMusicScriptFixture: MusicScriptResolving {
    var live = NativeMusicFixture.source()
    var requests: [MusicScriptRequest] = []
    var gate: NativeMusicGate?
    var error: MusicServiceError?
    func configure(generation: Int = 1, changed: Bool = false, gate: NativeMusicGate? = nil, error: MusicServiceError? = nil) {
        live = NativeMusicFixture.source(generation: generation, hash: String(repeating: changed ? "b" : "a", count: 64))
        self.gate = gate; self.error = error
    }
    func sources() -> [MusicScriptSource] { [live] }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installationID == live.installationID else { throw MusicServiceError.sourceUnavailable }; return live
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicNativeAudio {
        requests.append(request)
        if let gate { await gate.wait() }
        if let error { throw error }
        return .init(url: URL(string: "https://media.invalid/synthetic.flac")!)
    }
    func lyrics(request: MusicScriptRequest) -> MusicNativeLyrics { .init(text: "[00:01.000]LX") }
    func calls() -> [MusicScriptRequest] { requests }
}
actor NativeMusicHTTPFixture: MusicNativeHTTPTransport {
    private var responses: [Data]
    private var sent: [MusicNativeHTTPRequest] = []
    init(_ responses: [MJ]) { self.responses = responses.map(\.bytes) }
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        sent.append(request)
        guard !responses.isEmpty else { throw MusicServiceError.upstreamFailed }
        return .init(status: 200, data: responses.removeFirst())
    }
    func calls() -> [MusicNativeHTTPRequest] { sent }
}
actor NativeMusicAccountFixture: MusicNativeAccountProviding {
    var bound = 1
    var gate: NativeMusicGate?
    private var calls: [String] = []
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func logout() { bound += 1 }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage {
        let own = bound; calls.append("recommendations")
        if let gate { await gate.wait() }
        guard own == bound else { throw MusicServiceError.staleResult }
        return .init(items: [NativeMusicFixture.track()])
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) -> MusicAccountCollectionPage {
        .init(items: [.init(id: "favorites", title: "Favorites", count: 1, owned: true)], cursor: nil)
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) -> MusicNativeTrackPage {
        .init(items: [NativeMusicFixture.track()])
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        calls.append("options")
        return [.init(installationId: "official:synthetic", generation: bound, siteId: "tx", sourceKey: sourceKey, qualities: ["128k"],
                      title: "Official", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        let own = bound; calls.append("resolve")
        if let gate { await gate.wait() }
        guard own == bound && own == selection.generation else { throw MusicServiceError.staleResult }
        return .init(url: URL(string: "https://isure.stream.qqmusic.qq.com/M500Synthetic.mp3")!, expiresAt: NativeMusicFixture.date.addingTimeInterval(120))
    }
    func events() -> [String] { calls }
}
