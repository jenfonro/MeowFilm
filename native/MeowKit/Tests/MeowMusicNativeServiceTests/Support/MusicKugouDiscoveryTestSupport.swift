import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic wire excerpts. Expected identities are not built with production parsers.
enum KugouDiscoveryFixture {
    static let recommend = "/v2/special_recommend"
    static let newSongs = "/musicadservice/container/v1/newsong_publish"
    static let rankList = "/ocean/v6/rank/list"
    static let tags = "/pubsongs/v1/get_tags_by_type"
    static let rankAudio = "/openapi/kmr/v2/rank/audio"
    static let paths: Set<String> = [recommend, newSongs, rankList, tags, rankAudio]
    static func hash(_ id: Int) -> String {
        let value = String(id, radix: 16, uppercase: true)
        return String(repeating: "0", count: 32 - value.count) + value
    }
    static func playlists(_ count: Int, start: Int = 1, more: Bool = false) -> MJ {
        let rows = (0..<max(0, count)).map { offset -> MJ in
            let id = start + offset
            return .obj([("specialid", .n(900000 + id)),
                ("global_collection_id", .string("collection_3_123456_\(id)_0")),
                ("specialname", .string("Synthetic playlist \(id)")), ("songcount", .n(196)),
                ("nickname", .string("Synthetic creator")),
                ("imgurl", .string("https://imge.kugou.com/synthetic/{size}/playlist.jpg"))])
        }
        return .obj([("status", .n(1)), ("error_code", .n(0)),
            ("data", .obj([("has_next", .n(more ? 1 : 0)), ("special_list", .array(rows))]))])
    }
    static func newSongPage(_ count: Int, total: Int, start: Int = 1) -> MJ {
        let rows = (0..<max(0, count)).map { offset -> MJ in
            let id = start + offset
            return .obj([("hash", .string(hash(id).lowercased())),
                ("filename", .string("Synthetic singer - Synthetic new song \(id)")),
                ("timelength", .n(279000)), ("album_id", .n(212839391)),
                ("album_audio_id", .n(1000 + id)), ("album_name", .string("Synthetic album"))])
        }
        return .obj([("status", .n(1)), ("error_code", .n(0)), ("total", .n(total)), ("data", .array(rows))])
    }
    static func rankPage(_ count: Int, total: Int, start: Int = 1) -> MJ {
        let rows = (0..<max(0, count)).map { offset -> MJ in
            let id = start + offset
            return .obj([("audio_info", .obj([("hash_128", .string(hash(id).lowercased())),
                ("duration_128", .n(216000)), ("filesize_128", .n(3473110))])),
                ("songname", .string("Synthetic rank song \(id)")), ("author_name", .string("Synthetic singer")),
                ("album_info", .obj([("album_name", .string("Synthetic album")),
                    ("sizable_cover", .string("https://imge.kugou.com/synthetic/{size}/album.jpg"))])),
                ("album_id", .n(213380582)), ("album_audio_id", .n(2000 + id)), ("rank_cid", .n(156133))])
        }
        return .obj([("status", .n(1)), ("error_code", .n(0)), ("total", .n(total)),
            ("data", .obj([("total", .n(total)), ("songlist", .array(rows))]))])
    }
    static func ranks(cid: Int = 156133) -> MJ {
        .obj([("status", .n(1)), ("errcode", .n(0)), ("error", .string("")),
            ("data", .obj([("total", .n(1)), ("info", .array([.obj([
                ("id", .n(2)), ("rankid", .n(8888)), ("rank_cid", .n(cid)),
                ("rankname", .string("Synthetic TOP500")),
                ("imgurl", .string("https://imge.kugou.com/synthetic/{size}/rank.jpg")),
                ("update_frequency", .string("每天")), ("songinfo", .array([.obj([
                    ("name", .string("Synthetic preview")), ("author", .string("Synthetic singer"))])])),
                ("children", .array([]))
            ])]))]))])
    }
    static func tagGroups(extraEmpty: Bool = false) -> MJ {
        var value = json("""
        {"status":1,"error_code":0,"data":[
          {"parent_id":"0","tag_id":"5","tag_name":"场景","son":[
            {"parent_id":"5","tag_id":"587","tag_name":"学习"},{"parent_id":"5","tag_id":"660","tag_name":"工作"}]},
          {"parent_id":"0","tag_id":"1","tag_name":"主题","son":[
            {"parent_id":"1","tag_id":"1084","tag_name":"精选"},{"parent_id":"1","tag_id":"12","tag_name":"经典"}]},
          {"parent_id":"0","tag_id":"2","tag_name":"语种","son":[
            {"parent_id":"2","tag_id":"84","tag_name":"国语"},{"parent_id":"2","tag_id":"20","tag_name":"英语"}]},
          {"parent_id":"0","tag_id":"8","tag_name":"风格","son":[
            {"parent_id":"8","tag_id":"9","tag_name":"流行"},{"parent_id":"8","tag_id":"574","tag_name":"古风"}]},
          {"parent_id":"0","tag_id":"4","tag_name":"心情","son":[
            {"parent_id":"4","tag_id":"1107","tag_name":"怀旧"},{"parent_id":"4","tag_id":"578","tag_name":"伤感"}]},
          {"parent_id":"0","tag_id":"7","tag_name":"年代","son":[
            {"parent_id":"7","tag_id":"13","tag_name":"70后"},{"parent_id":"7","tag_id":"14","tag_name":"80后"}]}
        ]}
        """)
        if extraEmpty {
            value["data"] = .array(value["data"].array + [
                .obj([("parent_id", .string("0")), ("tag_id", .string("99")),
                      ("tag_name", .string("空组")), ("son", .array([]))])])
        }
        return value
    }
    static func failure(_ code: Int) -> MJ {
        .obj([("status", .n(0)), ("error_code", .n(code)), ("data", .obj())])
    }
    static func json(_ value: String) -> MJ { try! MJ.parse(Data(value.utf8)) }
    static func homeResponse(_ call: KugouCall) -> MJ? {
        switch call.path {
        case recommend: return playlists(call.body["pagesize"].int, more: true)
        case newSongs: return newSongPage(1, total: 1)
        case rankList: return ranks()
        case tags: return tagGroups()
        default: return nil
        }
    }
    static func destination(_ endpoint: String) -> MusicPublicBrowseDestination {
        switch endpoint {
        case tags: return .channels
        case recommend: return .channel(.kugou("playlist", .playlists), tag: "")
        case newSongs: return .channel(.kugou("newsong", .newSongs), tag: "")
        case rankList: return .channel(.kugou("chart", .rankings), tag: "")
        default: return .channel(.kugou("rank-8888", .rankings, pageCode: "156133"), tag: "")
        }
    }
    static func pathsFor(_ endpoint: String) -> [String] {
        switch endpoint {
        case recommend: return [tags, recommend]
        case rankAudio: return [rankList, rankAudio]
        default: return [endpoint]
        }
    }
}

struct KugouCall: Sendable {
    let host: String
    let path: String
    let method: String
    let query: [String: String]
    let body: MJ
    let rawBody: Data?
    let headers: [String: String]
    func header(_ name: String) -> String? {
        headers.first { $0.key.lowercased() == name.lowercased() }?.value
    }
}

actor KugouGateway: MusicNativeHTTPTransport {
    private let respond: @Sendable (KugouCall, Int) -> MJ?
    private var sent: [KugouCall] = []
    private var failures: [String] = []
    private var gates: [String: NativeMusicGate] = [:]
    private var active = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    init(_ respond: @escaping @Sendable (KugouCall, Int) -> MJ?) { self.respond = respond }
    func hold(_ path: String, gate: NativeMusicGate) { gates[path] = gate }
    func calls() -> [KugouCall] { sent }
    func paths() -> [String] { sent.map(\.path) }
    func violations() -> [String] { failures }
    func waitUntilIdle() async {
        if active > 0 { await withCheckedContinuation { idleWaiters.append($0) } }
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        active += 1
        defer {
            active -= 1
            if active == 0 { idleWaiters.forEach { $0.resume() }; idleWaiters.removeAll() }
        }
        let query = URLComponents(url: request.url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        let call = KugouCall(host: request.url.host ?? "", path: request.url.path, method: request.method,
            query: Dictionary(query.map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { _, new in new }),
            body: try request.body.map { try MJ.parse($0) } ?? .obj(), rawBody: request.body, headers: request.headers)
        sent.append(call)
        if request.url.scheme != "https" || call.host != "gateway.kugou.com" { failures.append("unexpected gateway") }
        if call.header("Cookie") != nil || call.header("Authorization") != nil { failures.append("credentials") }
        if !KugouDiscoveryFixture.paths.contains(call.path) { failures.append("unexpected path " + call.path) }
        if !MusicNativePublicBrowseRoutes.allows(request) { failures.append("route rejected " + call.path) }
        let number = sent.filter { $0.path == call.path }.count
        if let gate = gates[call.path] { await gate.wait() }
        guard let result = respond(call, number) else {
            failures.append("unanswered " + call.path); throw MusicServiceError.upstreamFailed
        }
        return .init(status: 200, data: result.bytes)
    }
}

final class KugouSnapshotStore: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return data }
    func write(_ value: Data) { lock.lock(); data = value; lock.unlock() }
    func clear() { lock.lock(); data = nil; lock.unlock() }
}

func kugouService(_ gateway: any MusicNativeHTTPTransport,
                  document: any MusicNativeDocumentStoring = NativeMusicMemoryDocument(),
                  snapshot: (any MusicHomeSnapshotStoring)? = nil,
                  accounts: (any MusicNativeAccountProviding)? = nil,
                  enabled: Set<MusicPublicBrowsePlatform> = [.netease, .qq, .kugou],
                  select: Bool = true) async throws -> NativeMusicService {
    let value = NativeMusicService(document: document, accounts: accounts, qq: NativeMusicQQFixture(),
        enabledPlatforms: enabled, homeSnapshot: snapshot, transport: gateway, clock: { NativeMusicFixture.date })
    try await value.setAccess(available: true, generation: 1)
    if select { _ = try await value.selectHomeSource(.kugou, ifRevision: 0) }
    return value
}

func assertMusicFailure(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                        _ body: () async throws -> Void) async {
    do { try await body(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
}

func finishKugou(_ value: NativeMusicService, _ gateway: KugouGateway,
                 paths: [String]? = nil, file: StaticString = #filePath, line: UInt = #line) async {
    await gateway.waitUntilIdle()
    let failures = await gateway.violations(), actual = await gateway.paths()
    XCTAssertEqual(failures, [], file: file, line: line)
    if let paths { XCTAssertEqual(actual, paths, file: file, line: line) }
    await value.close()
}

extension MusicPublicBrowsePage {
    func kugouSection(_ suffix: String) -> MusicPublicBrowseSection? {
        sections.first { $0.id.hasSuffix(":" + suffix) }
    }
    var kugouItems: [MusicPublicBrowseItem] { sections.flatMap(\.items) }
    var kugouCollectionIDs: [String] {
        kugouItems.compactMap { if case .collection(let row) = $0.content { return row.nativeID }; return nil }
    }
}
extension MusicPublicBrowseChannel {
    static func kugou(_ id: String, _ kind: Kind, pageCode: String = "") -> Self {
        .init(id: id, title: id, kind: kind, pageCode: pageCode, platform: .kugou)
    }
}
