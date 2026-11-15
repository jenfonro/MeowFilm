import Foundation
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
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
    static func client() throws -> MusicPlatformClient { try NativeMusicPlatformFixture().makeClient() }
    static func routes(primary: any MusicPlatformFixtureResponding, others: any MusicPlatformFixtureResponding) -> MusicPlatformFixtureRouter {
        .init(["tx": primary, "wy": others, "kg": others, "kw": others, "mg": others])
    }
    static func storedSong(_ track: MusicNativeTrackData) throws -> MJ {
        var value = try MusicModel.normalize(track)
        value["songId"] = .string(UUID().uuidString.lowercased())
        if value["duration"].number > 0 { value["baselineDuration"] = value["duration"] }
        return value
    }
    static func track(_ mid: String = "SyntheticMID") -> MusicNativeTrackData {
        .init(platform: "tx", data: MJ.obj([("songmid", .string(mid)),
            ("qqSongId", .string("9007199254740993")), ("name", .string("Synthetic " + mid)), ("singer", .string("Fixture")),
            ("albumName", .string("Synthetic album")), ("albumMid", .string("SyntheticAlbum")), ("duration", .n(180)),
            ("types", .array([.obj([("type", .string("128k"))]), .obj([("type", .string("flac"))])]))]).bytes)
    }
    static func song(_ mid: String = "SyntheticMID") throws -> MJ { try storedSong(track(mid)) }
    static func source(generation: Int = 1, hash: String = String(repeating: "a", count: 64),
                       state: MusicScriptSourceItem.State = .ready) -> MusicScriptSource {
        .init(installationID: installation, generation: generation, enabled: true, state: state, version: "1",
              contentHash: hash, revisionHash: hash, capabilities: [.init(siteID: "tx", title: "LX synthetic", actions: ["musicUrl", "lyric"], qualities: ["128k", "flac"])])
    }
    static func service(_ store: NativeMusicMemoryDocument = .init(), scripts: (any MusicScriptResolving)? = nil,
                        accounts: (any MusicNativeAccountProviding)? = nil,
                        platform: (any MusicPlatformFixtureResponding)? = nil,
                        sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                        clock: @escaping @Sendable () -> Date = { date },
                        monotonicClock: @escaping @Sendable () -> TimeInterval = { 0 }) async throws -> NativeMusicService {
        let peer = platform ?? (accounts as? any MusicPlatformFixtureResponding) ?? NativeMusicPlatformFixture()
        let service = NativeMusicService(document: store, scripts: scripts, accounts: accounts,
            platformClient: try peer.makeClient(), sourceDiscovery: sourceDiscovery,
            clock: clock, monotonicClock: monotonicClock, random: { 0 })
        try await service.setAccess(available: true, generation: 1); return service
    }
}
actor NativeMusicPlatformFixture: MusicPlatformFixtureResponding {
    var gate: NativeMusicGate?
    private var recorded: [MusicPlatformFixture.Request] = []
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func calls() -> [MusicPlatformFixture.Request] { recorded }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        recorded.append(request)
        if let gate { await gate.wait() }
        switch request.operation {
        case "search":
            let query = request.parameters[music: "keyword"].musicText
            return try MusicPlatformFixture.page([NativeMusicFixture.track(query)], platform: request.platform)
        case "lyrics":
            return .object(["text": .string("[00:01.000]Synthetic\n[00:02.000]next"),
                            "translation": .string("[00:01.000]合成")])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }
}

/// Ordered canonical replies through the real runtime client. Gates deliberately
/// allow late completion so consumers must isolate cancelled/access-old results.
actor NativeMusicResultFixture: MusicPlatformFixtureResponding {
    private var results: [RuntimeValue]
    private let failure: MusicServiceError?
    private let gate: NativeMusicGate?
    private let gatedCall: Int
    private var recorded: [MusicPlatformFixture.Request] = []
    private var active = 0
    private var idle: [CheckedContinuation<Void, Never>] = []
    init(_ results: [RuntimeValue] = [], failure: MusicServiceError? = nil, gate: NativeMusicGate? = nil, gatedCall: Int = 1) {
        self.results = results; self.failure = failure; self.gate = gate; self.gatedCall = gatedCall
    }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        recorded.append(request); active += 1
        defer {
            active -= 1
            if active == 0 { idle.forEach { $0.resume() }; idle.removeAll() }
        }
        if recorded.count == gatedCall, let gate { await gate.wait() }
        if let failure { throw failure }
        guard !results.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return results.removeFirst()
    }
    func calls() -> [MusicPlatformFixture.Request] { recorded }
    func waitUntilIdle() async {
        if active > 0 { await withCheckedContinuation { idle.append($0) } }
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
    func coldStart() { live = NativeMusicFixture.source(generation: 0, state: .idle) }
    func sources() -> [MusicScriptSource] { [live] }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installationID == live.installationID else { throw MusicServiceError.sourceUnavailable }; return live
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if live.state == .idle { live = NativeMusicFixture.source(generation: 7) }
        let executed = live
        if let gate { await gate.wait() }
        if let error { throw error }
        return .init(value: .init(url: URL(string: "https://media.invalid/synthetic.flac")!), source: executed)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: "[00:01.000]LX"), source: request.source) }
    func calls() -> [MusicScriptRequest] { requests }
}
actor NativeMusicAccountFixture: MusicNativeAccountProviding {
    var bound = 1
    var gate: NativeMusicGate?
    private var qualities = ["128k"]
    private var calls: [String] = []
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func logout() { bound += 1 }
    func configureQualities(_ values: [String]) { qualities = values }
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
        return [.init(installationId: "official:synthetic", generation: bound, siteId: platform.rawValue, sourceKey: sourceKey, qualities: qualities,
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
