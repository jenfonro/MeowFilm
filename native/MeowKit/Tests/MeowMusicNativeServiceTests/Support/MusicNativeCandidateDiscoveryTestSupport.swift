import Foundation
import XCTest
import MeowMusicFeature
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
    static func song() throws -> MJ { try MusicModel.normalize(track()) }
    static func store(song: MJ? = nil) throws -> NativeMusicMemoryDocument {
        var document = try MusicDocument(nil, runID: "fixture")
        let value = try song ?? self.song()
        document.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("song", value)])])
        document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1); document.queue["revision"] = .n(1)
        document.playback["position"] = .obj([("occurrenceId", .string("entry-1")), ("seconds", .n(23))])
        try document.validate()
        return NativeMusicMemoryDocument(try document.encode(changed: ["queue", "playback"]))
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

actor MusicNativeCandidateDiscoveryQQ: MusicPublicQQProviding {
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

actor MusicNativeCandidateDiscoveryHTTP: MusicNativeHTTPTransport {
    let barrier: MusicNativeCandidateDiscoveryBarrier?
    let wyStatus: Int
    var empty: Set<String>
    var failures: [String: MusicServiceError]
    private var platforms: [String] = []
    init(barrier: MusicNativeCandidateDiscoveryBarrier? = nil, wyStatus: Int = 200,
         empty: Set<String> = [], failures: [String: MusicServiceError] = [:]) {
        self.barrier = barrier; self.wyStatus = wyStatus; self.empty = empty; self.failures = failures
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        guard let platform = ["interfacepc.music.163.com": "wy", "gateway.kugou.com": "kg",
            "search.kuwo.cn": "kw", "jadeite.migu.cn": "mg"][request.url.host ?? ""] else {
            throw MusicServiceError.invalidRequest
        }
        let wy = platform == "wy"
        platforms.append(platform)
        if let barrier { await barrier.enter() }
        if let failure = failures[platform] { throw failure }
        if empty.contains(platform) {
            let body: MJ
            if wy {
                body = .obj([("code", .n(200)), ("result", .obj([("songCount", .n(0)), ("songs", .array([]))]))])
            } else if platform == "kg" {
                body = .obj([("status", .n(1)), ("error_code", .n(0)), ("data", .obj([("total", .n(0)), ("lists", .array([]))]))])
            } else if platform == "kw" {
                body = MusicNativeKuwoSearchFixture.page([], total: 0)
            } else {
                body = MusicNativeMiguSearchFixture.page([], total: 0)
            }
            return .init(status: 200, data: body.bytes)
        }
        if wy && wyStatus != 200 { return .init(status: wyStatus, data: Data()) }
        let body: MJ
        if wy {
            body = .obj([("code", .n(200)), ("result", .obj([("songCount", .n(1)), ("songs", .array([
                .obj([("id", .n(321)), ("name", .string("Synthetic recording")), ("dt", .n(180_000)),
                    ("ar", .array([.obj([("name", .string("Fixture"))])])), ("al", .obj([("id", .n(10)), ("name", .string("Album"))]))])
            ]))]))])
        } else if platform == "kg" {
            body = .obj([("status", .n(1)), ("error_code", .n(0)), ("data", .obj([("total", .n(1)), ("lists", .array([
                .obj([("FileHash", .string(String(repeating: "A", count: 32))), ("OriSongName", .string("Synthetic recording")),
                    ("SingerName", .string("Fixture")), ("Duration", .n(180)), ("FileSize", .n(1000)),
                    ("AlbumID", .string("10")), ("MixSongID", .string("11"))])
            ]))]))])
        } else if platform == "kw" {
            body = MusicNativeKuwoSearchFixture.page([MusicNativeKuwoSearchFixture.song()], total: 1)
        } else {
            body = MusicNativeMiguSearchFixture.page([[MusicNativeMiguSearchFixture.song()]], total: 1)
        }
        return .init(status: 200, data: body.bytes)
    }
    func calls() -> [String] { platforms }
}

actor MusicNativeCandidateDiscoveryScripts: MusicScriptResolving {
    var generation = 1, version = "1", revision = String(repeating: "a", count: 64)
    var sites = ["tx", "wy", "kg", "kw", "mg"]
    var installations = [MusicNativeCandidateDiscoveryFixture.installation]
    var barrier: MusicNativeCandidateDiscoveryBarrier?
    var mutateAfterResolve = false
    var requests: [MusicScriptRequest] = []
    func configure(generation: Int = 1, version: String = "1", revision: String = String(repeating: "a", count: 64),
                   barrier: MusicNativeCandidateDiscoveryBarrier? = nil, mutateAfterResolve: Bool = false) {
        self.generation = generation; self.version = version; self.revision = revision
        self.barrier = barrier; self.mutateAfterResolve = mutateAfterResolve
    }
    func supported(_ sites: [String]) { self.sites = sites }
    func installed(_ installations: [String]) { self.installations = installations }
    func sources() -> [MusicScriptSource] { installations.map { snapshot($0) } }
    func snapshot(_ installation: String) -> MusicScriptSource {
        .init(installationID: installation, generation: generation, enabled: true, ready: true,
            version: version, contentHash: String(repeating: "a", count: 64), revisionHash: revision,
            capabilities: sites.map { .init(siteID: $0, title: "Same name", actions: ["musicUrl"], qualities: ["128k", "flac"]) })
    }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installations.contains(installationID) else { throw MusicServiceError.sourceUnavailable }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicNativeAudio {
        requests.append(request)
        if let barrier { await barrier.enter() }
        if mutateAfterResolve { revision = String(repeating: "b", count: 64) }
        return .init(url: URL(string: "https://media.invalid/fixture.mp3?signature=synthetic")!)
    }
    func lyrics(request: MusicScriptRequest) -> MusicNativeLyrics { .init(text: "") }
    func calls() -> [MusicScriptRequest] { requests }
}
