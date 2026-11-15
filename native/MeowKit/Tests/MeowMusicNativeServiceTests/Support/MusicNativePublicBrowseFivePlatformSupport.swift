import Foundation
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

enum MusicNativePublicBrowseFiveFixture {
    static let playlistID = "9007199254740993"
    static let platforms: [MusicPublicBrowsePlatform] = [.kuwo, .migu]
    static func collection(_ platform: MusicPublicBrowsePlatform) -> MusicPublicBrowseCollection {
        .init(platform: platform, nativeID: playlistID, title: "Synthetic playlist")
    }
    static func track(_ platform: MusicPublicBrowsePlatform, id: String) throws -> MusicNativeTrackData {
        var value = try MJ.parse(MusicNativeCandidateDiscoveryFixture.track(platform.rawValue, id: id,
            title: "Synthetic " + id, artist: "A、B").data)
        value["img"] = .string("https://artwork.invalid/song-" + id + ".jpg")
        if platform == .migu {
            value["copyrightId"] = .string("600" + id); value["contentId"] = .string("900" + id)
            value["audioFormats"] = .array([.obj([("aformat", .string("020010"))])])
        }
        return .init(platform: platform.rawValue, data: value.bytes)
    }
}

actor MusicNativePublicBrowseFivePeer: MusicPlatformFixtureResponding {
    let platform: MusicPublicBrowsePlatform
    let total: Int, searchTotal: Int
    let fault: String, changeFrom: Int, repeats: Bool
    private var reads = 0, active = 0
    private var sent: [MusicPlatformFixture.Request] = []
    private var gate: NativeMusicGate?
    private var heldCall: Int?, failedCall: Int?
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    private let seedPeer = NativeMusicPlatformFixture()
    init(_ platform: MusicPublicBrowsePlatform, total: Int = 65, searchTotal: Int = 1,
         fault: String = "", changeFrom: Int = 2, repeats: Bool = false) {
        self.platform = platform; self.total = total; self.searchTotal = searchTotal
        self.fault = fault; self.changeFrom = changeFrom; self.repeats = repeats
    }
    func hold(call: Int, gate: NativeMusicGate) { heldCall = call; self.gate = gate }
    func fail(call: Int) { failedCall = call }
    func calls() -> [MusicPlatformFixture.Request] { sent }
    func activeTransferCount() -> Int { active }
    func waitUntilIdle() async { if active > 0 { await withCheckedContinuation { idleWaiters.append($0) } } }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        if request.platform != platform.rawValue { return try await seedPeer.respond(request) }
        active += 1
        defer {
            active -= 1
            if active == 0 { idleWaiters.forEach { $0.resume() }; idleWaiters.removeAll() }
        }
        sent.append(request)
        if sent.count == heldCall, let gate { await gate.wait() }
        if sent.count == failedCall { throw MusicServiceError.rateLimited }
        let page = Int(request.parameters[music: "page"].musicNumber ?? 1)
        if request.operation == "search" {
            let offset = (page - 1) * 30
            let count = min(30, max(0, searchTotal - offset))
            let rows: [RuntimeValue] = (0..<count).map { index in
                let id = offset + index == 0 ? MusicNativePublicBrowseFiveFixture.playlistID : String(offset + index + 1)
                return .object(["id": .string(id), "source": .string(platform.rawValue), "name": .string("Synthetic & mix"),
                    "creator": .string("Creator"), "count": .number(Double(total)), "cover": .string("https://artwork.invalid/collection.jpg")])
            }
            return .object(["items": .array(rows), "total": .number(Double(searchTotal)),
                "continuation": offset + count < searchTotal ? .object(["page": .number(Double(page + 1))]) : .null])
        }
        guard ["playlist", "playlist.complete"].contains(request.operation) else { throw MusicServiceError.unsupported }
        reads += 1
        let changed = reads >= changeFrom
        let offset = request.operation == "playlist.complete" ? 0 : (page - 1) * 30
        let end = request.operation == "playlist.complete" ? total : min(offset + 30, total)
        let tracks = try (offset..<max(offset, end)).map { index -> MusicNativeTrackData in
            var id = repeats ? String(index % 2 + 1) : String(index + 1)
            if changed, fault == "tail", index == total - 1 { id = "999999" }
            if changed, fault == "reorder", index < 2 { id = String(2 - index) }
            return try MusicNativePublicBrowseFiveFixture.track(platform, id: id)
        }
        var value = try MusicPlatformFixture.page(tracks, platform: platform.rawValue, total: total, page: page,
            continuation: end < total ? .object(["page": .number(Double(page + 1)), "batch": .number(1), "index": .number(Double(end))]) : .null)
        guard case .object(var fields) = value else { throw MusicServiceError.upstreamInvalid }
        fields["id"] = .string(changed && fault == "wrongID" ? "canonical-99" : MusicNativePublicBrowseFiveFixture.playlistID)
        fields["name"] = .string("Synthetic playlist"); fields["creator"] = .string(changed && fault == "owner" ? "another" : "Creator")
        fields["cover"] = .string("https://artwork.invalid/collection.jpg")
        fields["sourcePositions"] = .array((offset..<max(offset, end)).map { .number(Double($0)) })
        value = .object(fields)
        return value
    }
}

actor MusicNativePublicBrowseFiveScripts: MusicScriptResolving {
    let platform: String
    let gate: NativeMusicGate?
    private var requests: [MusicScriptRequest] = []
    init(platform: String, gate: NativeMusicGate? = nil) { self.platform = platform; self.gate = gate }
    func sources() -> [MusicScriptSource] {
        [snapshot(NativeMusicFixture.installation, platform: platform),
         snapshot("22222222-2222-4222-8222-222222222222", platform: "tx")]
    }
    private func snapshot(_ id: String, platform: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: [.init(siteID: platform, title: "Synthetic", actions: ["musicUrl"], qualities: ["128k"])])
    }
    func source(installationID: String) throws -> MusicScriptSource {
        guard let value = sources().first(where: { $0.installationID == installationID }) else { throw MusicServiceError.sourceUnavailable }
        return value
    }
    func resolve(request: MusicScriptRequest) async -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if let gate { await gate.wait() }
        return .init(value: .init(url: URL(string: "https://media.invalid/synthetic.mp3")!), source: request.source)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: ""), source: request.source) }
    func calls() -> [MusicScriptRequest] { requests }
}
