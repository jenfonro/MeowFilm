import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic wire responses, not platform/credential fixtures. No live network.
enum MusicNativePublicBrowseFiveFixture {
    static let playlistID = "9007199254740993"
    static let platforms: [MusicPublicBrowsePlatform] = [.kuwo, .migu]
    static func collection(_ platform: MusicPublicBrowsePlatform) -> MusicPublicBrowseCollection {
        .init(platform: platform, nativeID: playlistID, title: "Synthetic playlist")
    }
    static func song(_ platform: MusicPublicBrowsePlatform, id: String, copyright: String? = nil) -> MJ {
        if platform == .kuwo {
            return .obj([("MUSICRID", .string("MUSIC_" + id)), ("SONGNAME", .string("Synthetic " + id)),
                ("ARTIST", .string("A&amp;B")), ("ALBUM", .string("Album")), ("ALBUMID", .string("10")),
                ("DURATION", .string("180")), ("N_MINFO", .string("level:p,bitrate:128,format:mp3,size:2Mb")),
                ("pic", .string("https://img1.kuwo.cn/synthetic.jpg"))])
        }
        return .obj([("songId", .string(id)), ("copyrightId", .string(copyright ?? ("600" + id))),
            ("songName", .string("Synthetic " + id)), ("contentId", .string("900" + id)),
            ("singerList", .array([.obj([("name", .string("A"))])])), ("albumName", .string("Album")),
            ("albumId", .string("10")), ("duration", .n(180)), ("img3", .string("/synthetic.jpg")),
            ("audioFormats", .array([.obj([("formatType", .string("PQ")), ("asize", .string("1234567")),
                ("aformat", .string("020010")), ("url", .string("https://private.invalid/do-not-retain"))])]))])
    }
    static func search(_ platform: MusicPublicBrowsePlatform, page: Int, total: Int, rows: Int? = nil) -> MJ {
        let offset = (page - 1) * 30
        let count = rows ?? min(30, max(0, total - offset))
        let rows = (0..<count).map { index -> MJ in
            let id = offset + index == 0 ? playlistID : String(offset + index + 1)
            if platform == .kuwo {
                return .obj([("playlistid", .string(id)), ("name", .string("Synthetic &amp; mix")),
                    ("nickname", .string("Creator")), ("songnum", .string("65")),
                    ("pic", .string("http://img1.kuwo.cn/synthetic.jpg"))])
            }
            return .obj([("id", .string(id)), ("name", .string("Synthetic mix")), ("userName", .string("Creator")),
                ("musicNum", .string("65")), ("musicListPicUrl", .string("/synthetic.jpg"))])
        }
        if platform == .kuwo { return .obj([("TOTAL", .string(String(total))), ("abslist", .array(rows))]) }
        return .obj([("code", .string("000000")), ("data", .obj([
            ("songListResultData", .obj([("totalCount", .string(String(total))), ("result", .array(rows))]))
        ]))])
    }
}

actor MusicNativePublicBrowseFiveHTTP: MusicNativeHTTPTransport {
    let platform: MusicPublicBrowsePlatform
    let total: Int
    let searchTotal: Int
    let fault: String
    let changeFrom: Int
    let repeats: Bool
    var scan = 0
    private var sent: [MusicNativeHTTPRequest] = []
    private var gate: NativeMusicGate?
    private var heldCall: Int?
    private var failedCall: Int?
    private var activeTransfers = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    init(_ platform: MusicPublicBrowsePlatform, total: Int = 65, searchTotal: Int = 1,
         fault: String = "", changeFrom: Int = 2, repeats: Bool = false) {
        self.platform = platform; self.total = total; self.searchTotal = searchTotal
        self.fault = fault; self.changeFrom = changeFrom; self.repeats = repeats
    }
    func hold(call: Int, gate: NativeMusicGate) { heldCall = call; self.gate = gate }
    func fail(call: Int) { failedCall = call }
    func calls() -> [MusicNativeHTTPRequest] { sent }
    func activeTransferCount() -> Int { activeTransfers }
    func waitUntilIdle() async {
        if activeTransfers > 0 { await withCheckedContinuation { idleWaiters.append($0) } }
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        activeTransfers += 1
        defer {
            activeTransfers -= 1
            if activeTransfers == 0 {
                idleWaiters.forEach { $0.resume() }; idleWaiters.removeAll()
            }
        }
        sent.append(request)
        let call = sent.count
        if call == heldCall, let gate { await gate.wait() } // Deliberately non-cooperative.
        if call == failedCall { return .init(status: 429, data: Data()) }
        guard MusicNativePublicBrowseRoutes.allows(request),
              let values = URLComponents(url: request.url, resolvingAgainstBaseURL: false)?.queryItems else {
            throw MusicServiceError.invalidRequest
        }
        let query = Dictionary(uniqueKeysWithValues: values.map { ($0.name, $0.value ?? "") })
        let root: MJ
        if request.url.host == "search.kuwo.cn" || request.url.host == "jadeite.migu.cn" {
            let page = platform == .kuwo ? (Int(query["pn"] ?? "") ?? -1) + 1 : Int(query["pageNo"] ?? "") ?? 0
            root = MusicNativePublicBrowseFiveFixture.search(platform, page: page, total: searchTotal)
        } else if platform == .kuwo {
            guard request.url.host == "nplserver.kuwo.cn" else { throw MusicServiceError.invalidRequest }
            let page = Int(query["pn"] ?? "") ?? -1
            if page == 0 { scan += 1 }
            let changed = scan >= changeFrom
            let id = fault == "wrongID" ? "99" : MusicNativePublicBrowseFiveFixture.playlistID
            root = .obj([("result", .string("ok")), ("id", .string(id)), ("title", .string("Synthetic playlist")),
                ("uid", .string(changed && fault == "owner" ? "another" : "creator")), ("uname", .string("Creator")),
                ("total", .n(fault == "count" && page > 0 ? total + 1 : total)),
                ("musiclist", .array(rows(offset: page * 1000, batch: 1000, changed: changed)))])
        } else if request.url.path == MusicNativePublicBrowseMigu.headPath {
            scan += 1
            root = .obj([("code", .string("000000")), ("data", .obj([
                ("musicListId", .string(fault == "wrongID" ? "99" : MusicNativePublicBrowseFiveFixture.playlistID)),
                ("title", .string("Synthetic playlist")), ("musicNum", .n(total)),
                ("ownerId", .string(scan >= changeFrom && fault == "owner" ? "another" : "creator")),
                ("ownerName", .string("Creator"))
            ]))])
        } else {
            guard request.url.path == MusicNativePublicBrowseMigu.songsPath else { throw MusicServiceError.invalidRequest }
            let page = Int(query["pageNo"] ?? "") ?? 0
            root = .obj([("code", .string("000000")), ("data", .obj([
                ("totalCount", .n(fault == "count" && page > 1 ? total + 1 : total)),
                ("publishTime", .string(fault == "published" && page > 1 ? "changed" : "unchanged")),
                ("songList", .array(rows(offset: (page - 1) * 50, batch: 50, changed: scan >= changeFrom)))
            ]))])
        }
        return .init(status: 200, data: root.bytes)
    }
    private func rows(offset: Int, batch: Int, changed: Bool) -> [MJ] {
        guard offset >= 0, offset < total else { return [] }
        if fault == "short" && offset > 0 { return [] }
        return (offset..<min(offset + batch, total)).map { index in
            var id = repeats ? String(index % 2 + 1) : String(index + 1)
            if changed && fault == "tail" && index == total - 1 { id = "999999" }
            if changed && fault == "reorder" && index < 2 { id = String(2 - index) }
            let copyright = changed && fault == "copyright" && index == total - 1 ? "changed" : "600" + id
            var raw = MusicNativePublicBrowseFiveFixture.song(platform, id: id, copyright: copyright)
            if fault == "metadata" && index == total - 1 {
                if platform == .kuwo { raw.remove("N_MINFO") } else { raw.remove("copyrightId") }
            }
            return raw
        }
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
