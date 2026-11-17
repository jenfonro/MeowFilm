import Foundation
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

// Synthetic snapshots are test data, not a production serialization port.
// Business tests exercise real SQLite rows and transactions; this wrapper only
// adds deterministic failure injection and explicit fixture inspection.
final class NativeMusicTestLibrary: MusicNativeLibraryStoring, @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private let root = FileManager.default.temporaryDirectory.appendingPathComponent("meow-music-sqlite-" + UUID().uuidString, isDirectory: true)
    private lazy var backing = makeBacking()
    private var seed: Data?
    private var initialized = false
    private var failure: MusicServiceError?
    private var reads = 0, writes = 0
    private var revisionOffset = 0, importedRevision = 0
    init(_ value: Data? = nil) { seed = value }
    deinit { try? backing.close(); try? FileManager.default.removeItem(at: root) }
    private func makeBacking() -> MusicNativeLibraryDatabase {
        let root = self.root
        return MusicNativeLibraryDatabase(urlFactory: {
            try MusicNativeLibraryDatabase.canonicalSystemBaseURL(root.deletingLastPathComponent())
                .appendingPathComponent(root.lastPathComponent, isDirectory: true)
                .appendingPathComponent(MusicNativeLibraryDatabase.filename)
        })
    }
    private func storage() throws -> MusicNativeLibraryDatabase {
        if initialized { return backing }
        if let seed {
            let raw = try MJ.parse(seed)
            try musicRequire(raw["areas"].isObject && raw["songs"].isObject && raw["revision"].isInteger && raw["areas"].fields.allSatisfy { MusicDocument.areas.contains($0.0) }, .corruptDocument)
            try backing.withTransaction { writer in
                for (id, song) in raw["songs"].fields {
                    try MusicLibraryValidation.validateSong(song, id: id)
                    try writer.saveSong(song.bytes)
                }
                for (name, data) in raw["areas"].fields { try writer.saveArea(name, data: data.bytes) }
            }
        }
        if let seed { revisionOffset = try MJ.parse(seed)["revision"].int - backing.readSnapshot().revision }
        importedRevision = try backing.readSnapshot().revision
        initialized = true; return backing
    }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        lock.lock(); defer { lock.unlock() }
        reads += 1
        let value = try storage().readSnapshot()
        return .init(revision: value.revision + revisionOffset, receipt: value.receipt, areas: value.areas)
    }
    func songData(id: String) throws -> Data? { lock.lock(); defer { lock.unlock() }; return try storage().songData(id: id) }
    func containsSong(id: String) throws -> Bool { lock.lock(); defer { lock.unlock() }; return try storage().containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { lock.lock(); defer { lock.unlock() }; return try storage().sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { lock.lock(); defer { lock.unlock() }; return try storage().candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { lock.lock(); defer { lock.unlock() }; return try storage().querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { lock.lock(); defer { lock.unlock() }; return try storage().allSongIDs() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        let database = try storage(), before = try database.readSnapshot().revision
        let value = try database.withTransaction(commit: { action in
            if let failure = self.failure { throw failure }
            try withoutActuallyEscaping(action) { try commit($0) }
        }) { try body(NativeMusicFixtureWriter(base: $0, revisionOffset: revisionOffset)) }
        if try database.readSnapshot().revision != before { writes += 1 }
        return value
    }
    // In-memory fixture lifetime is independent of a service's lifetime. Actual
    // SQLite close/reopen is covered by NativeMusicSQLiteTests.
    func close() throws {}
    func fail(_ value: MusicServiceError?) { lock.lock(); failure = value; lock.unlock() }
    func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        guard initialized else { return seed }
        let snapshot = try backing.readSnapshot()
        if snapshot.revision == importedRevision, let seed { return seed }
        if snapshot.revision == 0 && snapshot.areas.isEmpty { return nil }
        let songs = try MJ.obj(backing.allSongIDs().map { ($0, try MJ.parse(backing.songData(id: $0)!)) })
        return MJ.obj([("revision", .n(snapshot.revision + revisionOffset)), ("songs", songs),
            ("areas", .obj(try snapshot.areas.sorted { $0.key < $1.key }.map { ($0.key, try MJ.parse($0.value)) }))]).bytes
    }
    func replaceSnapshot(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }
        guard try read() == expected else { throw MusicServiceError.storageConflict }
        replace(replacement)
    }
    func replace(_ value: Data?) {
        lock.lock(); defer { lock.unlock() }
        try? backing.close(); try? FileManager.default.removeItem(at: root)
        backing = makeBacking()
        initialized = false; seed = value; revisionOffset = 0
    }
    func snapshot() -> (Data?, Int, Int) {
        lock.lock(); defer { lock.unlock() }
        return ((try? read()) ?? seed, reads, writes)
    }
}

private struct NativeMusicFixtureWriter: MusicNativeLibraryWriting {
    let base: any MusicNativeLibraryWriting
    let revisionOffset: Int
    var changedSongIDs: Set<String> { base.changedSongIDs }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { let value = try base.readSnapshot(); return .init(revision: value.revision + revisionOffset, receipt: value.receipt, areas: value.areas) }
    func songData(id: String) throws -> Data? { try base.songData(id: id) }
    func containsSong(id: String) throws -> Bool { try base.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { try base.sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { try base.candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { try base.allSongIDs() }
    func saveSong(_ data: Data) throws { try base.saveSong(data) }
    func saveArea(_ area: String, data: Data) throws { try base.saveArea(area, data: data) }
}

/// Only fixture setup/inspection materializes all songs. Production does not
/// encode a whole library or offer a replacement-document entry point.
extension MusicDocument {
    init(_ data: Data?, runID: String) throws {
        let fixture = NativeMusicFixtureRows(data)
        let snapshot = try fixture.readSnapshot()
        try self.init(snapshot: snapshot, runID: runID, songs: fixture)
        library["library"]["songs"] = try fixture.songObject()
    }
    var fixtureSnapshot: MJ { (try? MJ.parse(fixtureData())) ?? .null }
    func fixtureSong(_ id: String) throws -> MJ {
        if library["library"].contains("songs") { return library["library"]["songs"][id] }
        return try song(id)
    }
    func fixtureSongs() throws -> MJ {
        if library["library"].contains("songs") { return library["library"]["songs"] }
        return try .obj(songRows.allSongIDs().map { ($0, try song($0)) })
    }
    func fixtureData() throws -> Data {
        let songs = try MJ.obj(fixtureSongs().fields.sorted { $0.0 < $1.0 })
        var controls = library; controls["library"].remove("songs")
        return MJ.obj([("revision", .n(revision)), ("songs", songs),
            ("areas", .obj([("catalogSource", catalogSource), ("history", history), ("library", controls), ("playback", playback), ("queue", queue)]))]).bytes
    }
    mutating func encode(changed: Set<String>) throws -> Data {
        revision += 1
        var raw = try MJ.parse(fixtureData())
        raw["areas"]["playback"]["position"]["seconds"] = .n(0)
        return raw.bytes
    }
}
private final class NativeMusicFixtureRows: MusicNativeLibraryReading, @unchecked Sendable {
    let data: Data?
    init(_ data: Data?) { self.data = data }
    private func raw() throws -> MJ { try data.map { try MJ.parse($0) } ?? .obj([("revision", .n(0)), ("areas", .obj()), ("songs", .obj())]) }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        let raw = try raw()
        try musicRequire(raw["areas"].isObject && raw["songs"].isObject && raw["revision"].isInteger && raw["areas"].fields.allSatisfy { MusicDocument.areas.contains($0.0) }, .corruptDocument)
        return .init(revision: raw["revision"].int, receipt: "", areas: Dictionary(uniqueKeysWithValues: raw["areas"].fields.map { ($0.0, $0.1.bytes) }))
    }
    func songObject() throws -> MJ { try raw()["songs"] }
    func songData(id: String) throws -> Data? { let value = try songObject()[id]; return value.isObject ? value.bytes : nil }
    func containsSong(id: String) throws -> Bool { try songObject()[id].isObject }
    func sourceOwner(key: String) throws -> String? { try songObject().fields.first { $0.1["sources"].array.contains { $0["sourceKey"].string == key } }?.0 }
    func candidateIDs(lookupKey: String) throws -> [String] { try songObject().fields.filter { MusicModel.descriptor($0.1).lookupKey == lookupKey }.map(\.0) }
    func querySongIDs(query: String) throws -> [String] { try songObject().fields.filter { MusicRecordingIdentity.normalize($0.1["title"].string + " " + $0.1["artist"].string) == query }.map(\.0) }
    func allSongIDs() throws -> [String] { try songObject().fields.map(\.0) }
}

protocol NativeMusicTestLibraryWrapping: MusicNativeLibraryStoring {
    var backingLibrary: NativeMusicTestLibrary { get }
}
extension NativeMusicTestLibraryWrapping {
    func songData(id: String) throws -> Data? { try backingLibrary.songData(id: id) }
    func containsSong(id: String) throws -> Bool { try backingLibrary.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { try backingLibrary.sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { try backingLibrary.candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { try backingLibrary.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { try backingLibrary.allSongIDs() }
    func close() throws { try backingLibrary.close() }
    func read() throws -> Data? { try backingLibrary.read() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try backingLibrary.withTransaction(commit: commit, body)
    }
}
extension NativeMusicService {
    func seedRepositorySongs(_ songs: [MJ]) throws {
        try transaction(["library"]) { _ in
            for song in songs { try saveRepositorySong(song) }
        }
    }
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
    static func service(_ store: NativeMusicTestLibrary = .init(), scripts: (any MusicScriptResolving)? = nil,
                        accounts: (any MusicNativeAccountProviding)? = nil,
                        platform: (any MusicPlatformFixtureResponding)? = nil,
                        sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                        clock: @escaping @Sendable () -> Date = { date },
                        monotonicClock: @escaping @Sendable () -> TimeInterval = { 0 }) async throws -> NativeMusicService {
        let peer = platform ?? (accounts as? any MusicPlatformFixtureResponding) ?? NativeMusicPlatformFixture()
        let service = NativeMusicService(database: store, scripts: scripts, accounts: accounts,
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
