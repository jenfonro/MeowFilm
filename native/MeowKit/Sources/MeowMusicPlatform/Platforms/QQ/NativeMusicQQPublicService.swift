// SPDX-License-Identifier: GPL-3.0-only
// Anonymous platform HTTP only; device/session protocol reuses the native QQ
// adapter. Its instance is separate from all credential-bearing account actors.
import Foundation
import MeowContracts
import MeowMusicFeature
import MeowJSON
import MeowStorage

public actor NativeMusicQQPublicService: MusicPublicQQProviding {
    let qq: QQMusicAccountAdapter
    private let owned: URLSessionMusicAccountTransport?
    private let clock: @Sendable () -> Date
    nonisolated let persistence: MusicQQPublicPersistenceState
    private var closed = false
    var closedForClient: Bool { closed }
    /// Explicit volatile-only construction. Production must use storeFactory.
    /// This initializer cannot silently serve as a persistent-store fallback.
    public init(transport: (any MusicAccountHTTPTransport)? = nil, clock: @escaping @Sendable () -> Date = { Date() }) {
        let owned = transport == nil ? URLSessionMusicAccountTransport() : nil
        let state = MusicQQPublicPersistenceState(document: nil, compatibilityOpen: true)
        self.owned = owned; self.clock = clock; persistence = state
        qq = QQMusicAccountAdapter(transport: transport ?? owned!, now: clock, publicPersistence: state)
    }
    /// Passive, initially locked. The factory is first used by an explicitly
    /// requested search/lyrics call; granting access does not start HTTP.
    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore,
                transport: (any MusicAccountHTTPTransport)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        let owned = transport == nil ? URLSessionMusicAccountTransport() : nil
        let state = MusicQQPublicPersistenceState(document: MusicQQPublicPersistenceDocument(storeFactory: storeFactory))
        self.owned = owned; self.clock = clock; persistence = state
        qq = QQMusicAccountAdapter(transport: transport ?? owned!, now: clock, publicPersistence: state)
    }
    public init(persistence: any MusicQQPublicPersisting,
                transport: (any MusicAccountHTTPTransport)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        let owned = transport == nil ? URLSessionMusicAccountTransport() : nil
        let state = MusicQQPublicPersistenceState(document: persistence)
        self.owned = owned; self.clock = clock; self.persistence = state
        qq = QQMusicAccountAdapter(transport: transport ?? owned!, now: clock, publicPersistence: state)
    }
    public nonisolated func revokeAccess(generation: UInt64) {
        persistence.revoke(generation: generation)
    }
    public func setAccess(available: Bool, generation: UInt64) async throws {
        guard !closed else { throw MusicServiceError.closed }
        try await qq.setPublicAccess(available: available, generation: generation)
    }
    func captureAccess() throws -> UUID {
        guard !closed else { throw MusicServiceError.closed }
        try Task.checkCancellation()
        let token = try persistence.capture()
        try persistence.check(token)
        return token
    }
    public func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        _ = try MusicPlatformJSON.inputText(query, maximum: 800)
        _ = try MusicSearchPagination.offset(platform: .qq, page: page, limit: limit)
        let own = try captureAccess()
        let random = UInt64(UInt32.random(in: UInt32.min...UInt32.max))
        let seconds = clock().timeIntervalSince1970
        guard seconds.isFinite, (0...253_402_300_799).contains(seconds) else { throw MusicServiceError.invalidRequest }
        let time = UInt64(seconds * 1000) % 86_400_000
        let searchID = String((random % 20 + 1) * (1 << 54) + (random % 4_194_305) * (1 << 32) + time)
        let data = try await qq.rpc("music.search.SearchCgiService", "DoSearchForQQMusicMobile",
            ["searchid": searchID, "query": query, "search_type": 0, "num_per_page": limit, "page_num": page,
             "highlight": 0, "grp": 1, "selectors": [String: String](), "vec_selectors": [String]()],
            publicAccess: own)
        try persistence.check(own)
        let meta = try MusicQQWire.object(data["meta"]), body = try MusicQQWire.object(data["body"])
        let total = try MusicQQWire.integer(meta["sum"])
        let next = try MusicQQWire.integer(meta["nextpage"], minimum: -1)
        let rows = try await qq.array(body["item_song"], maximum: limit)
        let nextPage: Int?
        if next == -1 || rows.isEmpty {
            nextPage = nil
        } else {
            guard next > page else { throw MusicServiceError.upstreamInvalid }
            nextPage = page + 1
        }
        var items: [MusicNativeTrackData] = []
        for raw in rows {
            let track = try MusicQQWire.object(raw)
            guard case .object(var value) = try await qq.song(raw) else { throw MusicServiceError.upstreamInvalid }
            let file = track["file"] as? [String: Any] ?? [:]
            let mid = try MusicQQWire.id(track["mid"], mid: true)
            value["source"] = .string("tx"); value["songmid"] = .string(mid); value["songId"] = value["qqSongId"]
            value["albumId"] = value["albumMid"]; value["albumName"] = value["album"]
            let albumMid = try MusicPlatformJSON.string(value["albumMid"], maximum: 64, empty: true)
            value["img"] = .string(albumMid.isEmpty ? "" : "https://y.gtimg.cn/music/photo_new/T002R500x500M000" + albumMid + ".jpg")
            value["strMediaMid"] = .string((file["media_mid"] as? String ?? "").isEmpty ? "" : try MusicQQWire.id(file["media_mid"], mid: true))
            var types: [JSONValue] = []
            for (quality, key) in [("128k", "size_128mp3"), ("320k", "size_320mp3"), ("flac", "size_flac"), ("flac24bit", "size_hires")] {
                if let count = try? MusicQQWire.integer(file[key]), count > 0 { types.append(.object(["type": .string(quality)])) }
            }
            value["types"] = .array(types)
            items.append(.init(platform: "tx", data: try JSONEncoder().encode(JSONValue.object(value))))
        }
        try persistence.check(own)
        return .init(items: items, total: total, nextPage: nextPage)
    }
    public func lyrics(mid: String) async throws -> MusicNativeLyrics {
        let own = try captureAccess()
        let track = try await qq.publicTrack(mid, access: own)
        try persistence.check(own)
        guard try MusicQQWire.integer(track["type"]) == 1 else { throw MusicServiceError.lyricIdentityMismatch }
        let data = try await qq.rpc("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo",
            ["songMid": mid, "type": 1, "crypt": 1, "lrc_t": 0, "qrc": 1, "qrc_t": 0,
             "roma": 1, "roma_t": 0, "trans": 1, "trans_t": 0, "needSingingAnnotations": false],
            publicAccess: own)
        try persistence.check(own)
        guard try MusicQQWire.songID(data["songID"]) == MusicQQWire.songID(track["id"]) else { throw MusicServiceError.lyricIdentityMismatch }
        func field(_ key: String) throws -> String {
            guard let s = (data[key] ?? "") as? String, s.utf8.count <= 1024 * 1024 else { throw MusicServiceError.invalidLyricResponse }
            return s
        }
        let result = try MusicNativeLyrics(text: field("lyric"), translation: field("trans"),
                                          romanization: field("roma"), encoding: .qqQRC)
        try persistence.check(own)
        return result
    }
    public func close() async {
        closed = true; persistence.close(); owned?.close()
        await qq.revoke()
    }
}
