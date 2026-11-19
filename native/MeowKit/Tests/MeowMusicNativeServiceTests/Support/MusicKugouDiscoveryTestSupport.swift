import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

enum KugouDiscoveryFixture {
    static func hash(_ id: Int) -> String {
        let value = String(id, radix: 16, uppercase: true)
        return String(repeating: "0", count: 32 - value.count) + value
    }
    static func json(_ value: String) -> MJ { try! MJ.parse(Data(value.utf8)) }
    static func collection(_ id: Int) -> RuntimeValue {
        .object(["id": .string("collection_3_123456_\(id)_0"), "title": .string("Synthetic playlist \(id)"), "kind": .string("playlist"),
            "count": .number(196), "creator": .string("Synthetic creator"),
            "cover": .string("https://imge.kugou.com/synthetic/480/playlist.jpg"), "action": .object(["type": .string("playlist"), "id": .string("collection_3_123456_\(id)_0")])])
    }
    static func channel(_ id: String, kind: String, name: String? = nil, pageCode: String = "") -> RuntimeValue {
        .object(["id": .string(id), "name": .string(name ?? id), "kind": .string("channel"),
            "channelKind": .string(kind), "pageCode": .string(pageCode)])
    }
    static func section(_ id: String, items: [RuntimeValue], layout: String, channel: RuntimeValue? = nil) -> RuntimeValue {
        var row: RuntimeObject = ["id": .string(id), "title": .string(id), "layout": .string(layout), "items": .array(items)]
        if let channel { row["action"] = .object(["type": .string("channel"), "channel": channel]) }
        return .object(row)
    }
    static func page(_ sections: [RuntimeValue], next: RuntimeValue = .null,
                     tags: [RuntimeValue] = [], selected: String = "") -> RuntimeValue {
        .object(["sections": .array(sections), "continuation": next, "tags": .array(tags), "selectedTag": .string(selected)])
    }
    static func playlists(_ count: Int, start: Int = 1, next: RuntimeValue = .null) -> RuntimeValue {
        page([section("playlists", items: (0..<count).map { collection(start + $0) }, layout: "playlist-grid")], next: next)
    }
    static func songs(_ count: Int, start: Int = 1, rank: Bool = false, next: RuntimeValue = .null,
                      title: String? = nil, duration: Double? = nil) throws -> RuntimeValue {
        let rows = try (0..<count).map { offset -> RuntimeValue in
            let id = start + offset
            let track = MusicNativePublicBrowseFixture.track(hash(id), title: title ?? "Synthetic \(rank ? "rank" : "new") song \(id)", platform: "kg")
            var info = try MJ.parse(track.data)
            info["singer"] = .string("Synthetic singer"); info["duration"] = .number(duration ?? (rank ? 216 : 279))
            let song = try MusicPlatformFixture.song(.init(platform: "kg", data: info.bytes))
            return .object(["id": .string(hash(id)), "kind": .string("song"), "title": song[music: "title"],
                "subtitle": song[music: "artist"], "cover": song[music: "cover_url"], "song": song,
                "action": .object(["type": .string("song"), "id": .string(hash(id))])])
        }
        return page([section(rank ? "rank" : "new", items: rows, layout: "song-columns")], next: next)
    }
    static func ranks() -> RuntimeValue {
        var card: RuntimeValue = .object(["id": .string("rank-8888"), "kind": .string("channel"),
            "title": .string("Synthetic TOP500"), "action": .object(["type": .string("channel"),
                "channel": channel("rank-8888", kind: "rankings", name: "Synthetic TOP500", pageCode: "156133")])])
        if case .object(var row) = card {
            row["key"] = .string("rank-8888"); row["updateFrequency"] = .string("每天")
            row["preview"] = .array([.object(["id": .string("preview"), "title": .string("Synthetic preview"),
                "artist": .string("Synthetic singer"), "rank": .number(1)])]); card = .object(row)
        }
        return page([section("ranks", items: [card], layout: "ranking", channel: channel("chart", kind: "rankings"))])
    }
    static func directory() -> RuntimeValue {
        let ids = ["5", "1", "2", "8", "4", "7"], names = ["场景", "主题", "语种", "风格", "心情", "年代"]
        let primary = [channel("feature", kind: "featured"), channel("playlist", kind: "playlists"),
            channel("chart", kind: "rankings"), channel("newsong", kind: "new-songs")]
        return .object(["items": .array(primary + zip(ids, names).map { channel("category-" + $0.0, kind: "playlists", name: $0.1) })])
    }
    static func home() throws -> RuntimeValue {
        let lists = section("playlists", items: (1...20).map(collection), layout: "playlist-rail", channel: channel("playlist", kind: "playlists"))
        let songs = try self.songs(1)[music: "sections"].musicArray
        return page([lists] + songs + ranks()[music: "sections"].musicArray, next: .object(["page": .number(2)]))
    }
    static func reply(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        switch request.operation {
        case "home": return try request.parameters[music: "continuation"][music: "page"] == .null ? home() : playlists(1, start: 100)
        case "channels": return directory()
        case "channel":
            switch request.parameters[music: "id"].musicText {
            case "chart": return ranks()
            case "newsong": return try songs(1)
            case let id where id.hasPrefix("rank-"): return try songs(1, rank: true)
            default: return playlists(1, next: .object(["page": .number(2)]))
            }
        default: throw MusicServiceError.unsupported
        }
    }
}

actor KugouDiscoveryPeer: MusicPlatformFixtureResponding {
    private let handler: @Sendable (MusicPlatformFixture.Request, Int) throws -> RuntimeValue
    private var sent: [MusicPlatformFixture.Request] = []
    private var gates: [String: NativeMusicGate] = [:]
    private var active = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    init(_ handler: @escaping @Sendable (MusicPlatformFixture.Request, Int) throws -> RuntimeValue = { request, _ in try KugouDiscoveryFixture.reply(request) }) { self.handler = handler }
    func hold(_ operation: String, gate: NativeMusicGate) { gates[operation] = gate }
    func calls() -> [MusicPlatformFixture.Request] { sent }
    func waitUntilIdle() async {
        if active > 0 { await withCheckedContinuation { idleWaiters.append($0) } }
    }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        sent.append(request); active += 1
        defer { active -= 1; if active == 0 { idleWaiters.forEach { $0.resume() }; idleWaiters.removeAll() } }
        let number = sent.filter { $0.operation == request.operation }.count
        if let gate = gates[request.operation] { await gate.wait() }
        return try handler(request, number)
    }
}
final class KugouSnapshotStore: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return data }
    func write(_ value: Data) { lock.lock(); data = value; lock.unlock() }
    func clear() { lock.lock(); data = nil; lock.unlock() }
}
func kugouService(_ peer: any MusicPlatformFixtureResponding, document: any MusicNativeLibraryStoring = NativeMusicTestLibrary(),
                  snapshot: (any MusicHomeSnapshotStoring)? = nil, accounts: (any MusicNativeAccountProviding)? = nil,
                  enabled: Set<MusicPublicBrowsePlatform> = [.netease, .qq, .kugou], select: Bool = true) async throws -> NativeMusicService {
    let value = NativeMusicService(database: document, accounts: accounts, platformClient: try peer.makeClient(), enabledPlatforms: enabled,
        homeSnapshot: snapshot, clock: { NativeMusicFixture.date })
    try await value.setAccess(available: true, generation: 1)
    if select { _ = try await value.selectHomeSource(.kugou, ifRevision: 0) }; return value
}
func assertMusicFailure(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                        _ body: () async throws -> Void) async {
    do { try await body(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
}
func finishKugou(_ value: NativeMusicService, _ peer: KugouDiscoveryPeer, operations: [String]? = nil,
                 file: StaticString = #filePath, line: UInt = #line) async {
    await peer.waitUntilIdle()
    if let operations { let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), operations, file: file, line: line) }
    await value.close()
}
extension MusicPublicBrowsePage {
    func kugouSection(_ suffix: String) -> MusicPublicBrowseSection? { sections.first { $0.id.hasSuffix(":" + suffix) } }
    var kugouItems: [MusicPublicBrowseItem] { sections.flatMap(\.items) }
    var kugouCollectionIDs: [String] { kugouItems.compactMap { if case .collection(let row) = $0.content { return row.nativeID }; return nil } }
}
extension MusicPublicBrowseChannel {
    static func kugou(_ id: String, _ kind: Kind, pageCode: String = "") -> Self {
        .init(id: id, title: id, kind: kind, pageCode: pageCode, platform: .kugou)
    }
}
