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
    private let operationAdmission = RequestAdmission(maximum: 4, perHost: 4, waitingLimit: 64)
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
    /// admitted search/lyrics call; granting access does not start HTTP.
    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore,
                transport: (any MusicAccountHTTPTransport)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() },
                requestAdmission: RequestAdmission? = nil) {
        let owned = transport == nil ? URLSessionMusicAccountTransport(admission: requestAdmission ?? RequestAdmission()) : nil
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
    func begin() async throws -> (token: UUID, lease: UUID) {
        guard !closed else { throw MusicServiceError.closed }
        let token = try persistence.capture()
        let time = clock().timeIntervalSince1970
        guard time.isFinite, (0...253_402_300_799).contains(time) else { throw MusicServiceError.invalidRequest }
        let admission = operationAdmission, registration = UUID()
        let priority = MusicRequestTrace.current?.priority ?? .normal
        let waiting = Task { try await admission.acquire(host: "qq", priority: priority) }
        do { try persistence.register(registration, token: token) { waiting.cancel() } }
        catch {
            // Registration also cancels if revoke already won. If admission
            // won first, release its physical slot before propagating failure.
            waiting.cancel()
            if let lease = try? await waiting.value { await admission.release(lease) }
            throw MusicServiceError.sanitized(error)
        }
        defer { persistence.remove(registration) }
        let lease: UUID
        do {
            lease = try await withTaskCancellationHandler { try await waiting.value } onCancel: { waiting.cancel() }
        }
        catch { throw MusicServiceError.sanitized(error) }
        do {
            try Task.checkCancellation()
            guard !closed else { throw MusicServiceError.closed }
            try persistence.check(token)
            return (token, lease)
        } catch {
            await operationAdmission.release(lease)
            throw MusicServiceError.sanitized(error)
        }
    }
    func end(_ lease: UUID) {
        // Called only after the full provider operation (including teardown)
        // returns, never just because its caller lost interest.
        Task { await operationAdmission.release(lease) }
    }
    /// Credential-free owner diagnostics; includes cancelled but unsettled I/O.
    func operationCounts() async -> (active: Int, waiting: Int) { await operationAdmission.counts }
    public func search(query: String, page: Int) async throws -> MusicNativeTrackPage {
        _ = try MusicPlatformJSON.inputText(query, maximum: 800)
        guard (1...200).contains(page) else { throw MusicServiceError.invalidRequest }
        let (own, lease) = try await begin()
        defer { end(lease) }
        let random = UInt64(UInt32.random(in: UInt32.min...UInt32.max))
        let time = UInt64(max(0, clock().timeIntervalSince1970 * 1000)) % 86_400_000
        let searchID = String((random % 20 + 1) * (1 << 54) + (random % 4_194_305) * (1 << 32) + time)
        let data = try await qq.rpc("music.search.SearchCgiService", "DoSearchForQQMusicMobile",
            ["searchid": searchID, "query": query, "search_type": 0, "num_per_page": 30, "page_num": page,
             "highlight": 0, "grp": 1, "selectors": [String: String](), "vec_selectors": [String]()],
            publicAccess: own)
        try persistence.check(own)
        let meta = try MusicQQWire.object(data["meta"]), body = try MusicQQWire.object(data["body"])
        let total = try MusicQQWire.integer(meta["sum"], maximum: 1_000_000)
        let next = try MusicQQWire.integer(meta["nextpage"], maximum: 200, minimum: -1)
        let rows = try await qq.array(body["item_song"], maximum: 30)
        guard next == -1 || next > page && !rows.isEmpty else { throw MusicServiceError.upstreamInvalid }
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
        return .init(items: items, total: total, nextPage: next == -1 ? nil : next)
    }
    public func lyrics(mid: String) async throws -> MusicNativeLyrics {
        let (own, lease) = try await begin()
        defer { end(lease) }
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
        await operationAdmission.close()
        await qq.revoke()
    }
}
