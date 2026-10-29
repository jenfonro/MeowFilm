// SPDX-License-Identifier: GPL-3.0-only
// meowmusic cc22a7e platform/qq/{music,library,discovery,account}.go.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

struct MusicQQCollection: Sendable, Equatable {
    let id: String
    let dirID: Int
    let owner: String
    let title: String
    let owned: Bool
    var dto: JSONValue { .object(["id": .string(id), "title": .string(title), "count": .null, "owned": .bool(owned)]) }
}
struct MusicQQRecommendation: Sendable {
    let rows: [JSONValue]
    let nextPage: Int?
    let expiresAt: Date?
    let dailyStyle: MusicDailyStyleSelection?
    init(rows: [JSONValue], nextPage: Int?, expiresAt: Date? = nil, dailyStyle: MusicDailyStyleSelection? = nil) {
        self.rows = rows; self.nextPage = nextPage; self.expiresAt = expiresAt
        self.dailyStyle = dailyStyle
    }
}
extension QQMusicAccountAdapter {
    func array(_ value: Any?, maximum: Int = 2000) throws -> [Any] {
        guard let rows = value as? [Any], rows.count <= maximum else { throw MusicServiceError.upstreamInvalid }; return rows
    }
    func song(_ raw: Any) throws -> JSONValue {
        let raw = try MusicQQWire.object(raw), mid = try MusicQQWire.id(raw["mid"], mid: true)
        let id = try MusicQQWire.songID(raw["id"]), type = try MusicQQWire.integer(raw["type"], maximum: Int(Int32.max))
        let album = raw["album"] as? [String: Any] ?? [:], file = raw["file"] as? [String: Any] ?? [:]
        let singers = try array(raw["singer"], maximum: 64).map { try MusicQQWire.string(MusicQQWire.object($0)["name"], maximum: 256) }
        let artist = try MusicQQWire.string(singers.joined(separator: "、"), maximum: 1024, empty: true)
        let duration = try MusicQQWire.integer(raw["interval"] ?? 0, maximum: 604800)
        let albumMid = (album["mid"] as? String ?? "").isEmpty ? "" : try MusicQQWire.id(album["mid"], mid: true)
        var levels: [String] = []
        for (quality, key) in [("128k", "size_128mp3"), ("320k", "size_320mp3"), ("flac", "size_flac"), ("master", "size_new")] {
            let value: Any? = key == "size_new" ? (file[key] as? [Any])?.first : file[key]
            if let value, let size = try? MusicQQWire.integer(value), size > 0 { levels.append(quality) }
        }
        return .object([
            "musicId": .string(mid), "qqSongId": .string(id), "songType": .number(Double(type)),
            "name": .string(try MusicQQWire.string(raw["title"] ?? raw["name"], maximum: 1024)),
            "singer": .string(artist), "album": .string(try MusicQQWire.string(album["name"] ?? "", maximum: 1024, empty: true)),
            "albumMid": .string(albumMid), "duration": .number(Double(duration)), "types": .array(levels.map(JSONValue.string))
        ])
    }
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation {
        guard ["guess", "radar"].contains(kind), (1...200).contains(page), kind != "guess" || page == 1 else {
            throw MusicServiceError.invalidRequest
        }
        let auth = try auth(credential), data: [String: Any], raw: [Any], more: Bool
        if kind == "guess" {
            data = try await rpc("music.radioProxy.MbTrackRadioSvr", "get_radio_track",
                                 ["id": 99, "num": 5, "from": 0, "scene": 0, "song_ids": [String]()], auth: auth)
            raw = try array(data["tracks"], maximum: 100); more = false
        } else {
            data = try await rpc("music.recommend.TrackRelationServer", "GetRadarSong",
                                 ["Page": page, "ReqType": 0, "FavSongs": [String](), "EntranceSongs": [String]()], auth: auth)
            guard let flag = data["HasMore"] as? NSNumber, CFGetTypeID(flag) == CFBooleanGetTypeID() else {
                throw MusicServiceError.upstreamInvalid
            }
            more = flag.boolValue
            raw = try array(data["VecSongs"], maximum: 100).map {
                guard let track = try MusicQQWire.object($0)["Track"] else { throw MusicServiceError.upstreamInvalid }
                return track
            }
        }
        let rows = try raw.map(song)
        guard !more || !rows.isEmpty && page < 200 else { throw MusicServiceError.limit }
        return MusicQQRecommendation(rows: rows, nextPage: more ? page + 1 : nil)
    }
    func directory(_ auth: [String: Any]) async throws -> [MusicQQCollection] {
        let uid = try MusicQQWire.id(auth["musicid"])
        let data = try await rpc("music.musicasset.PlaylistBaseRead", "GetPlaylistByUin", ["uin": uid], auth: auth)
        let created = try array(data["v_playlist"])
        let finished = data["bFinish"] as? NSNumber
        guard finished?.intValue == 1, try MusicQQWire.integer(data["total"], maximum: 2000) == created.count else {
            throw MusicServiceError.upstreamInvalid
        }
        var rows = [MusicQQCollection(id: "favorites", dirID: 201, owner: uid, title: "我喜欢", owned: true)]
        var seen: [String: Bool] = [:]
        func add(_ raw: Any, owned: Bool) throws {
            let raw = try MusicQQWire.object(raw)
            let id = try MusicQQWire.id(raw["id"] ?? raw["tid"] ?? raw["dissid"])
            let dir = try MusicQQWire.id(raw["dirid"] ?? raw["dirId"])
            let owner = try MusicQQWire.id(raw["uin"])
            guard (raw["invalid"] as? NSNumber)?.intValue != 1, !owned || owner == uid else { throw MusicServiceError.targetChanged }
            if owned && dir == "201" { return }
            if let previous = seen[id] {
                if previous && !owned { return }
                throw MusicServiceError.upstreamInvalid
            }
            seen[id] = owned
            rows.append(.init(id: id, dirID: Int(dir)!, owner: owner,
                              title: try MusicQQWire.string(raw["title"] ?? raw["dirName"] ?? raw["name"] ?? raw["dissname"], maximum: 600),
                              owned: owned))
            guard rows.count <= 2000 else { throw MusicServiceError.limit }
        }
        for row in created { try add(row, owned: true) }
        let euin = try MusicQQWire.string(auth["encrypt_uin"], maximum: 256)
        var expected: Int?, read = 0
        for page in 0..<20 {
            let data = try await rpc("music.musicasset.PlaylistFavRead", "CgiGetPlaylistFavInfo",
                                     ["uin": euin, "offset": page * 100, "size": 100], auth: auth)
            let items = try array(data["v_list"], maximum: 100), failed = try array(data["v_failTids"])
            let total = try MusicQQWire.integer(data["total"], maximum: 2000), more = try MusicQQWire.integer(data["hasmore"], maximum: 1)
            guard let hide = data["hide"] as? NSNumber, CFGetTypeID(hide) == CFBooleanGetTypeID(),
                  !hide.boolValue, failed.isEmpty, expected == nil || expected == total else { throw MusicServiceError.upstreamInvalid }
            expected = total; read += items.count
            for item in items { try add(item, owned: false) }
            if more == 0 {
                guard read == total else { throw MusicServiceError.upstreamInvalid }; return rows
            }
            guard !items.isEmpty, read < total else { throw MusicServiceError.upstreamInvalid }
        }
        throw MusicServiceError.limit
    }
    func collections(credential: AccountCredential) async throws -> [MusicQQCollection] {
        let auth = try auth(credential)
        let first = try await directory(auth), second = try await directory(auth)
        guard first == second else { throw MusicServiceError.staleResult }; return second
    }
    func scan(_ entry: MusicQQCollection, auth: [String: Any]) async throws -> [JSONValue] {
        var output: [JSONValue] = [], seen = Set<String>(), expected: Int?, bytes = 0
        for page in 0..<200 {
            var params: [String: Any] = ["disstid": entry.id == "favorites" ? 0 : Int(entry.id)!,
                "dirid": entry.dirID, "tag": 1, "song_begin": page * 100, "song_num": 100,
                "userinfo": 1, "orderlist": 1]
            if entry.id == "favorites" { params["enc_host_uin"] = auth["encrypt_uin"] }
            else { params["onlysonglist"] = 0 }
            let data = try await rpc("music.srfDissInfo.DissInfo", "CgiGetDiss", params, auth: auth)
            let raw = try array(data["songlist"], maximum: 100), info = try MusicQQWire.object(data["dirinfo"])
            let total = try MusicQQWire.integer(data["total_song_num"], maximum: 20_000)
            guard try MusicQQWire.integer(data["songlist_size"], maximum: 100) == raw.count,
                  expected == nil || expected == total,
                  try MusicQQWire.id(MusicQQWire.object(info["creator"])["musicid"]) == entry.owner,
                  try (!entry.owned || MusicQQWire.id(info["dirid"] ?? info["dirId"]) == String(entry.dirID)),
                  try (entry.id == "favorites" || MusicQQWire.id(info["id"] ?? info["tid"] ?? info["dissid"]) == entry.id) else {
                throw MusicServiceError.staleResult
            }
            expected = total
            for item in raw {
                let value = try song(item)
                let mid = try MusicQQWire.id(MusicQQWire.object(item)["mid"], mid: true)
                guard seen.insert(mid).inserted else { throw MusicServiceError.upstreamInvalid }
                bytes += try MusicPlatformJSON.bytes(value)
                guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
                output.append(value)
            }
            let more = try MusicQQWire.integer(data["hasmore"], maximum: 1)
            if more == 0 {
                guard output.count == total else { throw MusicServiceError.upstreamInvalid }; return output
            }
            guard !raw.isEmpty, output.count < total else { throw MusicServiceError.upstreamInvalid }
        }
        throw MusicServiceError.limit
    }
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue] {
        let auth = try auth(credential), before = try await directory(auth)
        guard let entry = before.first(where: { $0.id == collectionID }) else { throw MusicServiceError.notFound }
        let first = try await scan(entry, auth: auth), second = try await scan(entry, auth: auth)
        let after = try await directory(auth)
        guard first == second, before == after else { throw MusicServiceError.staleResult }
        return second
    }
    func track(_ mid: String, auth: [String: Any]) async throws -> [String: Any] {
        _ = try MusicQQWire.id(mid, mid: true)
        let data = try await rpc("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo",
                                 ["ctx": 0, "client": 1, "types": [0], "modify_stamp": [0], "mids": [mid]], auth: auth)
        let rows = try array(data["tracks"], maximum: 1)
        guard rows.count == 1, try MusicQQWire.object(rows[0])["mid"] as? String == mid else { throw MusicServiceError.targetChanged }
        _ = try song(rows[0]); return try MusicQQWire.object(rows[0])
    }
    func expiration(_ raw: Any?) -> Date? {
        if let n = raw as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite, n.doubleValue > 0 {
            return Date(timeIntervalSince1970: n.doubleValue > 1e12 ? n.doubleValue / 1000 : n.doubleValue)
        }
        guard let raw = raw as? String else { return nil }
        if let n = Double(raw), n.isFinite, n > 0 { return Date(timeIntervalSince1970: n > 1e12 ? n / 1000 : n) }
        let parser = DateFormatter(); parser.locale = Locale(identifier: "en_US_POSIX")
        parser.timeZone = TimeZone(secondsFromGMT: 8 * 3600); parser.isLenient = false
        for format in ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssXXXXX"] {
            parser.dateFormat = format; if let date = parser.date(from: raw) { return date }
        }; return nil
    }
    func membership(_ auth: [String: Any]) async throws -> (Bool?, Date?) {
        let data = try await rpc("VipLogin.VipLoginInter", "vip_login_base", auth: auth)
        let identity = data["identity"] as? [String: Any] ?? [:], summary = (data["userinfo"] as? [String: Any])?["expire"]
        let products: [(Any?, Any?, Any?)] = [
            (data["svip"], summary, nil),
            (identity["HugeVip"] ?? identity["huge_vip"], identity["HugeVipEnd"] ?? identity["huge_vip_end"], identity["HugeVipStart"] ?? identity["huge_vip_start"]),
            (identity["twelve"], identity["twelveEnd"] ?? identity["twelve_end"], identity["twelveStart"] ?? identity["twelve_start"]),
            (identity["eight"], identity["eightEnd"] ?? identity["eight_end"], identity["eightStart"] ?? identity["eight_start"]),
            (identity["GroupVipFlag"] ?? identity["group_vip_flag"], identity["GroupVipEnd"] ?? identity["group_vip_end"], identity["GroupVipStart"] ?? identity["group_vip_start"]),
            (identity["CPLoverFlag"] ?? identity["cp_lover_flag"], identity["CPLoverEnd"] ?? identity["cp_lover_end"], identity["CPLoverStart"] ?? identity["cp_lover_start"]),
            (data["star"], data["starend"] ?? data["star_end"], data["starstart"] ?? data["star_start"]),
            (data["ystar"], data["ystarend"] ?? data["ystar_end"], data["ystarstart"] ?? data["ystar_start"]),
            (identity["vip"], summary, nil), (identity["ChildVip"] ?? identity["child_vip"], nil, nil),
            (identity["ExpVip"] ?? identity["exp_vip"], nil, nil), (identity["AdVipFlag"] ?? identity["ad_vip_flag"], nil, nil)
        ]
        var active = false, known = false, uncertain = false, boundary: Date?
        for (flag, end, start) in products {
            guard let flag else { continue }
            guard let number = try? MusicQQWire.integer(flag, maximum: 1) else { uncertain = true; continue }
            known = true
            guard number == 1 else { continue }
            let ends = expiration(end), begins = expiration(start)
            if (ends == nil || ends! > now()) && (begins == nil || begins! <= now()) { active = true }
            for date in [ends, begins].compactMap({ $0 }) where date > now() { boundary = min(boundary ?? date, date) }
        }
        return (active ? true : known && !uncertain ? false : nil, boundary)
    }
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String] {
        let auth = try auth(credential), (vip, _) = try await membership(auth)
        guard let vip else { throw MusicServiceError.verificationRequired }
        guard vip else { return [] }
        let value = try song(await track(mid, auth: auth))
        guard case .object(let fields) = value, case .array(let levels)? = fields["types"] else { throw MusicServiceError.upstreamInvalid }
        return try levels.map { try MusicPlatformJSON.string($0) }
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue {
        let formats = ["128k": ("M500", "mp3"), "320k": ("M800", "mp3"), "flac": ("F000", "flac"), "master": ("AI00", "flac")]
        guard let (prefix, ext) = formats[quality] else { throw MusicServiceError.qualityDisabled }
        let auth = try auth(credential), (vip, boundary) = try await membership(auth)
        guard let vip else { throw MusicServiceError.verificationRequired }
        guard vip else { throw MusicServiceError.qualityDisabled }
        let raw = try await track(mid, auth: auth), normalized = try song(raw)
        guard case .object(let fields) = normalized, case .array(let levels)? = fields["types"],
              levels.contains(.string(quality)) else { throw MusicServiceError.qualityDisabled }
        let filename = prefix + mid + mid + "." + ext
        let data = try await rpc("music.vkey.GetVkey", "UrlGetVkey", [
            "uin": auth["str_musicid"]!, "filename": [filename], "guid": try MusicQQWire.randomHex(16),
            "songmid": [mid], "songtype": [0], "ctx": 0
        ], auth: auth)
        let rows = try array(data["midurlinfo"], maximum: 1)
        guard rows.count == 1 else { throw MusicServiceError.upstreamInvalid }
        let row = try MusicQQWire.object(rows[0])
        for key in ["trial", "preview", "freeTrialInfo"] {
            // A new/unknown trial marker is not evidence of a full-track grant.
            if let value = row[key], !(value is NSNull) { throw MusicServiceError.invalidMediaCandidate }
        }
        guard row["songmid"] as? String == mid, row["filename"] as? String == filename,
              try MusicQQWire.integer(row["result"]) == 0, (row["ekey"] as? String ?? "").isEmpty else {
            throw MusicServiceError.invalidMediaCandidate
        }
        let path = try MusicQQWire.string(row["purl"], maximum: 8192)
        guard let url = URL(string: path, relativeTo: URL(string: "https://isure.stream.qqmusic.qq.com/")!)?.absoluteURL,
              url.scheme == "https", url.user == nil, url.password == nil, url.port == nil, url.fragment == nil,
              let host = url.host, host == "stream.qqmusic.qq.com" || host.hasSuffix(".stream.qqmusic.qq.com"),
              url.lastPathComponent.hasPrefix(prefix), url.pathExtension == ext else { throw MusicServiceError.invalidMediaCandidate }
        let ttl = min(120, try MusicQQWire.integer(data["expiration"], maximum: Int(Int32.max)))
        let expires = min(now().addingTimeInterval(Double(ttl)), boundary ?? .distantFuture)
        guard expires > now() else { throw MusicServiceError.invalidMediaCandidate }
        return .object(["url": .string(url.absoluteString), "expiresAt": .number(expires.timeIntervalSince1970 * 1000),
                        "qualityVerified": .bool(false), "cacheAllowed": .bool(true),
                        "licenseExpiresAt": boundary.map { .number($0.timeIntervalSince1970 * 1000) } ?? .null])
    }
}
