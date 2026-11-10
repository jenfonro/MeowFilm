// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus home action protocols and api-enhanced module request shapes.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

protocol MusicAccountHomeActionProvider: Sendable {
    func homeAction(_ action: MusicHomeAction, credential: AccountCredential,
                    authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicQQRecommendation
}
enum MusicNeteaseHomeActionRoute {
    static func permits(_ request: MusicAccountHTTPRequest) -> Bool {
        guard request.url.scheme == "https", request.url.user == nil, request.url.password == nil,
              request.url.port == nil || request.url.port == 443, request.url.fragment == nil,
              request.url.query == nil, request.method == "POST" else { return false }
        switch request.url.host {
        case "interfacepc.music.163.com":
            return ["/eapi/v1/radio/get", "/eapi/tag/tab/fm/recommends", "/eapi/tag/tab/fm/cover",
                    "/eapi/playmode/intelligence/list", "/eapi/homepage/scene/more/rcmd/song"].contains(request.url.path)
        case "interface3.music.163.com":
            return ["/xeapi/homepage/category/daily/song/list", "/xeapi/homepage/daily/song/config/get",
                    "/xeapi/homepage/daily/song/tag/save"].contains(request.url.path)
        default: return false
        }
    }
}
enum MusicNeteaseHomeActionRequest {
    static let scenes: Set<String> = [
        "NIGHT_EMO", "EXERCISE", "SLEEP_HELP", "RELAX", "CHEERFUL", "LYRICAL", "CURE", "FOCUS",
        "SWEET", "RHYTHM_BLUES", "RAINY", "GAMES", "RAP", "K_POP", "ORIGINAL_MUSICIAL", "ELECTRONIC", "COMMUTE",
        "TAKE_SHOWER", "COFFEE_SHOP", "ROCK", "INSPIRATIONAL", "CHINESE", "ENGLISH", "YUEYU", "MANYAO", "JINGDIAN",
        "LIGHT", "GUOFENG", "FOLK", "ACG", "GUDIAN", "JAZZ", "JAPANESE", "GLOBAL", "FRANCH", "BLUE", "DANCE",
        "LATIN", "PUNK", "COUNTRY", "MUSICAL", "YINGSHI", "HOT_SONG"
    ]
    static func specification(_ action: MusicHomeAction) throws -> (String, [String: Any], String)? {
        try action.validate()
        switch action {
        case .fm(let mode, let submode, _):
            guard (mode == .scene) == !submode.isEmpty,
                  submode.isEmpty || scenes.contains(submode) else { throw MusicServiceError.invalidRequest }
            return ("/api/v1/radio/get", ["mode": mode.rawValue, "subMode": submode, "limit": "3",
                "unplaySongIds": "[]", "openAidj": "false", "aidjReqTimes": "0"], "eapi")
        case .ruleFM(let rule, let tab, _):
            return ("/api/tag/tab/fm/recommends",
                    ["ruleType": 1, "ruleId": rule, "tabTagId": tab, "firstSongId": "", "firstSongAlg": "", "limit": 5], "eapi")
        case .similar(let ids, _):
            return ("/api/v1/radio/get", ["limit": "3", "mode": "SIMILAR", "sourceType": "song",
                "sourceIds": "[" + ids.joined(separator: ",") + "]", "unplaySonIds": "[]"], "weapi")
        case .similarArtist(let ids, _):
            return ("/api/v1/radio/get", ["limit": "3", "mode": "SIMILAR", "sourceType": "artist",
                "sourceIds": "[" + ids.joined(separator: ",") + "]", "unplaySonIds": "[]"], "weapi")
        case .daily(.style):
            return ("/api/homepage/category/daily/song/list", [:], "xeapi")
        case .daily(.default), .heartbeat, .heartbeatSong, .songCollection: return nil
        }
    }
}

extension NeteaseMusicAccountAdapter: MusicAccountHomeActionProvider {
    func homeAction(_ action: MusicHomeAction, credential: AccountCredential,
                    authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicQQRecommendation {
        let own = epoch
        try check(own); try action.validate()
        let specification = try MusicNeteaseHomeActionRequest.specification(action)
        try await authorize(); try check(own)
        if action == .daily(branch: .default) {
            let result = try await recommendations(kind: "daily", page: 1, credential: credential)
            try await authorize(); try check(own); return result
        }
        let context = try context(credential)
        let raw: [[String: Any]]
        var dailyStyle: MusicDailyStyleSelection?
        if case .songCollection(let declared) = action {
            var seen = Set<String>()
            let ids = declared.filter { seen.insert($0).inserted }
            raw = try await homeActionMetadata(ids, context: context, own: own, authorize: authorize)
        } else if action.isHeartbeat {
            let selected: String?
            if case .heartbeatSong(let id) = action { selected = id } else { selected = nil }
            let batch = try await similarMode(.heartbeat(lastID: selected, fromHome: true, context: nil),
                                              credential: credential, authorize: authorize)
            return .init(rows: try batch.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }, nextPage: nil)
        } else if let (path, data, crypto) = specification {
            let result = try await request(path, data: data, crypto: crypto, context: context)
            try await authorize(); try check(own)
            let data = result["data"]
            let body = data as? [String: Any]
            if action == .daily(branch: .style) { dailyStyle = try MusicNeteaseDailyStyle.selection(body?["tags"]) }
            let rows = body?["dailySongs"] ?? body?["songs"] ?? data
            raw = try MusicNeteaseWire.rows(rows, maximum: 1000).map(Self.homeActionSong)
        } else { throw MusicServiceError.invalidRequest }

        let rows = try await homeActionRows(raw, action: action, context: context, own: own, authorize: authorize)
        try await authorize(); try check(own)
        return .init(rows: rows, nextPage: nil,
                     expiresAt: action == .daily(branch: .style) ? now().addingTimeInterval(60) : nil,
                     dailyStyle: dailyStyle)
    }
    static func homeActionSong(_ raw: [String: Any]) -> [String: Any] {
        var song = (raw["songInfo"] as? [String: Any]) ?? (raw["song"] as? [String: Any]) ?? raw
        for key in ["alg", "source", "sourceId"] {
            if let value = raw[key] { song[key] = value }
        }
        return song
    }
    func homeActionRows(_ raw: [[String: Any]], action: MusicHomeAction, context: MusicNeteaseContext, own: UUID,
                        authorize: @escaping @Sendable () async throws -> Void) async throws -> [JSONValue] {
        var missing = Set<String>(), ids: [String] = [], metadata: [String: [String: Any]] = [:]
        var origins: [(algorithm: String, source: String?, sourceID: String)] = []
        let secrets = Array(context.cookies.values) + Array(context.device.values)
        for song in raw {
            let id = try MusicNeteaseWire.id(song["id"] ?? song["songId"])
            ids.append(id)
            let value = song["alg"]
            let algorithm = try MusicQQWire.string(value == nil || value is NSNull ? "" : value, maximum: 2000, empty: true)
            let source: String?
            if let rawSource = song["source"], !(rawSource is NSNull) {
                source = try MusicAccountPublicText.profile(
                    MusicQQWire.string(rawSource, maximum: 2000, empty: true), secrets: secrets)
            } else { source = nil }
            let sourceID: String
            if let rawID = song["sourceId"], !(rawID is NSNull) {
                let text = try (rawID as? String) ?? MusicNeteaseWire.id(rawID)
                sourceID = try MusicAccountPublicText.profile(
                    MusicQQWire.string(text, maximum: 2048, empty: true), secrets: secrets)
            } else { sourceID = "" }
            origins.append((try MusicAccountPublicText.profile(algorithm, secrets: secrets), source, sourceID))
            if let name = song["name"] as? String, !name.isEmpty { metadata[id] = song }
            else { missing.insert(id) }
        }
        if !missing.isEmpty {
            let rows = try await homeActionMetadata(ids.filter { missing.remove($0) != nil },
                                                    context: context, own: own, authorize: authorize)
            for row in rows { metadata[try MusicNeteaseWire.id(row["id"])] = row }
        }
        let rows = try ids.enumerated().compactMap { index, id -> JSONValue? in
            guard let row = metadata[id] else { return nil }
            guard case .object(var value) = try Self.song(row, id: id) else { throw MusicServiceError.upstreamInvalid }
            let source: String, feedbackSource: String
            switch action {
            case .fm: source = "fm"; feedbackSource = "userfm"
            case .ruleFM: source = "fm"; feedbackSource = ""
            case .similar, .similarArtist: source = "similar"; feedbackSource = ""
            case .daily(let branch):
                source = "daily"; feedbackSource = branch == .default ? "dailySongRecommend" : ""
            default: return .object(value)
            }
            value["_recommendation"] = .object([
                "songId": .string(id), "source": .string(source), "sourceId": .string(""),
                "feedbackSource": .string(origins[index].source ?? feedbackSource),
                "feedbackSourceId": .string(origins[index].sourceID), "alg": .string(origins[index].algorithm)
            ])
            return .object(value)
        }
        try await authorize(); try check(own)
        return rows
    }
    private func homeActionMetadata(_ ids: [String], context: MusicNeteaseContext, own: UUID,
                                    authorize: @escaping @Sendable () async throws -> Void) async throws -> [[String: Any]] {
        var byID: [String: [String: Any]] = [:]
        for offset in stride(from: 0, to: ids.count, by: 100) {
            try await authorize(); try check(own)
            let batch = Array(ids.dropFirst(offset).prefix(100))
            for row in try await metadata(batch, context: context) {
                let id = try MusicNeteaseWire.id(row["id"])
                guard batch.contains(id), byID[id] == nil else { throw MusicServiceError.upstreamInvalid }
                byID[id] = row
            }
            try await authorize(); try check(own)
        }
        return ids.compactMap { byID[$0] }
    }
}
