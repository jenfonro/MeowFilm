import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

enum MusicNativeCandidateDiscoveryFixture {
    static let date = NativeMusicFixture.date
    static let installation = "22222222-2222-4222-8222-222222222222"
    static func track(_ platform: String = "tx", id: String = "SeedMID", title: String = "Synthetic recording",
                      artist: String = "Fixture", duration: Int = 180) -> MusicNativeTrackData {
        var data = MJ.obj([("source", .string(platform)), ("songmid", .string(id)),
            ("name", .string(title)), ("singer", .string(artist)), ("albumName", .string("Synthetic album")),
            ("duration", .n(duration)), ("types", .array([.obj([("type", .string("128k"))]), .obj([("type", .string("flac"))])]))])
        if platform == "kg" { data["hash"] = .string(id) }
        return .init(platform: platform, data: data.bytes)
    }
    static func song() throws -> MJ { try NativeMusicFixture.storedSong(track()) }
    static func store(song: MJ? = nil) throws -> NativeMusicMemoryDocument {
        var document = try MusicDocument(nil, runID: "fixture")
        var value = try song ?? self.song()
        if value["songId"].string.isEmpty { value["songId"] = .string(UUID().uuidString.lowercased()) }
        if value["duration"].number > 0 { value["baselineDuration"] = value["duration"] }
        document.library["library"]["songs"][value["songId"].string] = value
        document.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("songId", value["songId"])])])
        document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1); document.queue["revision"] = .n(1)
        document.playback["position"] = .obj([("occurrenceId", .string("entry-1")), ("seconds", .n(23))])
        try document.validate()
        return NativeMusicMemoryDocument(try document.encode(changed: ["library", "queue", "playback"]))
    }
    static func target(_ snapshot: MusicCandidateDiscoverySnapshot, platform: String = "wy") -> MusicCandidateDiscoveryTarget? {
        snapshot.options.first { $0.playback.siteId == platform }?.target(quality: "128k")
    }
}

actor MusicNativeCandidateDiscoveryBarrier {
    private var calls = 0, released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private struct Observer {
        let count: Int
        let continuation: CheckedContinuation<Bool, Never>
        let timeout: Task<Void, Never>
    }
    private var observers: [UUID: Observer] = [:]
    func enter() async {
        calls += 1
        let ready = observers.filter { calls >= $0.value.count }.map(\.key)
        for id in ready { finish(id, reached: true) }
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func started(_ count: Int, file: StaticString = #filePath, line: UInt = #line) async {
        let reached = await wait(count, timeout: .seconds(3))
        if !reached { XCTFail("Discovery requests did not enter", file: file, line: line) }
    }
    private func wait(_ count: Int, timeout: Duration) async -> Bool {
        if calls >= count { return true }
        let id = UUID()
        return await withCheckedContinuation { continuation in
            let timer = Task {
                do { try await Task.sleep(for: timeout) } catch { return }
                self.finish(id, reached: false)
            }
            observers[id] = .init(count: count, continuation: continuation, timeout: timer)
        }
    }
    private func finish(_ id: UUID, reached: Bool) {
        guard let observer = observers.removeValue(forKey: id) else { return }
        observer.timeout.cancel()
        observer.continuation.resume(returning: reached)
    }
    func release() {
        released = true; waiters.forEach { $0.resume() }; waiters = []
    }
    func count() -> Int { calls }
}

actor MusicNativeCandidateDiscoveryQQ: MusicPlatformFixtureResponding {
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        switch request.operation {
        case "search":
            return try MusicPlatformFixture.page(await search(query: request.parameters[music: "keyword"].musicText,
                page: Int(request.parameters[music: "page"].musicNumber ?? 1),
                limit: Int(request.parameters[music: "limit"].musicNumber ?? 30)), platform: request.platform)
        case "lyrics": return .object(["text": .string(try await lyrics(mid: request.parameters[music: "musicInfo"][music: "songmid"].musicText).text)])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }

    let barrier: MusicNativeCandidateDiscoveryBarrier?
    let items: [MusicNativeTrackData]
    var queries: [String] = []
    var fail: MusicServiceError?
    init(barrier: MusicNativeCandidateDiscoveryBarrier? = nil, fail: MusicServiceError? = nil,
         items: [MusicNativeTrackData] = [MusicNativeCandidateDiscoveryFixture.track()]) {
        self.barrier = barrier; self.fail = fail; self.items = items
    }
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        queries.append(query)
        if let barrier { await barrier.enter() }
        if let fail { throw fail }
        return .init(items: items, total: items.count)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func calls() -> [String] { queries }
}

actor MusicNativeDiscoveryPlatforms: MusicPlatformFixtureResponding {
    let barrier: MusicNativeCandidateDiscoveryBarrier?
    let wyStatus: Int
    var empty: Set<String>
    var failures: [String: MusicServiceError]
    private var platforms: [String] = []
    init(barrier: MusicNativeCandidateDiscoveryBarrier? = nil, wyStatus: Int = 200,
         empty: Set<String> = [], failures: [String: MusicServiceError] = [:]) {
        self.barrier = barrier; self.wyStatus = wyStatus; self.empty = empty; self.failures = failures
    }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        guard request.operation == "search" else { throw MusicPlatformFixture.Failure.remote("UNSUPPORTED") }
        let platform = request.platform
        platforms.append(platform)
        if let barrier { await barrier.enter() }
        if let failure = failures[platform] { throw failure }
        if platform == "wy" && wyStatus != 200 { throw MusicPlatformFixture.Failure.remote("UPSTREAM_HTTP") }
        let id = platform == "kg" ? String(repeating: "A", count: 32) : "321"
        let rows = empty.contains(platform) ? [] : [MusicNativeCandidateDiscoveryFixture.track(platform, id: id)]
        return try MusicPlatformFixture.page(rows, platform: platform)
    }
    func calls() -> [String] { platforms }
}

actor MusicNativeCandidateDiscoveryScripts: MusicScriptResolving {
    var generation = 1, version = "1", revision = String(repeating: "a", count: 64)
    var sites = ["tx", "wy", "kg", "kw", "mg"]
    var state = MusicScriptSourceItem.State.ready
    var installations = [MusicNativeCandidateDiscoveryFixture.installation]
    var barrier: MusicNativeCandidateDiscoveryBarrier?
    var mutateAfterResolve = false
    var requests: [MusicScriptRequest] = []
    private var sourcesBarrier: MusicNativeCandidateDiscoveryBarrier?
    private var sourcesCount = 0
    func configure(generation: Int = 1, version: String = "1", revision: String = String(repeating: "a", count: 64),
                   barrier: MusicNativeCandidateDiscoveryBarrier? = nil, mutateAfterResolve: Bool = false) {
        self.generation = generation; self.version = version; self.revision = revision
        self.barrier = barrier; self.mutateAfterResolve = mutateAfterResolve
    }
    func directory(_ state: MusicScriptSourceItem.State) { self.state = state; if state != .ready { generation = 0 } }
    func supported(_ sites: [String]) { self.sites = sites }
    func installed(_ installations: [String]) { self.installations = installations }
    func holdSources(_ barrier: MusicNativeCandidateDiscoveryBarrier) { sourcesBarrier = barrier }
    func sourcesCalls() -> Int { sourcesCount }
    func sources() async -> [MusicScriptSource] {
        sourcesCount += 1
        await sourcesBarrier?.enter()
        return installations.map { snapshot($0) }
    }
    func snapshot(_ installation: String) -> MusicScriptSource {
        .init(installationID: installation, generation: generation, enabled: state != .disabled, state: state,
            version: version, contentHash: String(repeating: "a", count: 64), revisionHash: revision,
            capabilities: sites.map { .init(siteID: $0, title: "Same name", actions: ["musicUrl"], qualities: ["128k", "flac"]) })
    }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installations.contains(installationID) else { throw MusicServiceError.sourceUnavailable }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        guard state != .failed && state != .disabled else { throw MusicServiceError.sourceUnavailable }
        if state != .ready { generation = 7; state = .ready }
        let executed = snapshot(request.installationID)
        if let barrier { await barrier.enter() }
        if mutateAfterResolve { revision = String(repeating: "b", count: 64) }
        return .init(value: .init(url: URL(string: "https://media.invalid/fixture.mp3?signature=synthetic")!), source: executed)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: ""), source: request.source) }
    func calls() -> [MusicScriptRequest] { requests }
}
