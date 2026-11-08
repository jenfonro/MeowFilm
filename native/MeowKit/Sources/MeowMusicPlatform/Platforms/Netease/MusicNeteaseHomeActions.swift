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
                    "/eapi/playmode/intelligence/list", "/eapi/homepage/more/rcmd/song"].contains(request.url.path)
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
            return ("/api/v1/radio/get", ["mode": mode.rawValue, "subMode": submode, "limit": 3], "eapi")
        case .ruleFM(let rule, let tab, _):
            return ("/api/tag/tab/fm/recommends",
                    ["ruleType": 1, "ruleId": rule, "tabTagId": tab, "firstSongId": "", "firstSongAlg": "", "limit": 5], "eapi")
        case .similar(let ids, _):
            return ("/api/v1/radio/get", ["limit": "3", "mode": "SIMILAR", "sourceType": "song",
                "sourceIds": "[" + ids.joined(separator: ",") + "]", "unplaySonIds": "[]"], "weapi")
        case .daily(.style):
            return ("/api/homepage/category/daily/song/list", [:], "xeapi")
        case .daily(.default), .heartbeat, .songCollection: return nil
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
        } else if case .heartbeat = action {
            let batch = try await similarMode(.heartbeat(lastID: nil, fromHome: true, context: nil),
                                              credential: credential, authorize: authorize)
            return .init(rows: try batch.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }, nextPage: nil)
        } else if let (path, data, crypto) = specification {
            let result = try await request(path, data: data, crypto: crypto, context: context)
            try await authorize(); try check(own)
            let data = result["data"]
            let body = data as? [String: Any]
            if action == .daily(branch: .style) { dailyStyle = try MusicNeteaseDailyStyle.selection(body?["tags"]) }
            let rows = body?["dailySongs"] ?? body?["songs"] ?? data
            raw = try MusicNeteaseWire.rows(rows, maximum: 1000).map {
                ($0["songInfo"] as? [String: Any]) ?? ($0["song"] as? [String: Any]) ?? $0
            }
        } else { throw MusicServiceError.invalidRequest }

        let rows = try await homeActionRows(raw, context: context, own: own, authorize: authorize)
        try await authorize(); try check(own)
        return .init(rows: rows, nextPage: nil,
                     expiresAt: action == .daily(branch: .style) ? now().addingTimeInterval(60) : nil,
                     dailyStyle: dailyStyle)
    }
    func homeActionRows(_ raw: [[String: Any]], context: MusicNeteaseContext, own: UUID,
                        authorize: @escaping @Sendable () async throws -> Void) async throws -> [JSONValue] {
        var missing = Set<String>(), ids: [String] = [], metadata: [String: [String: Any]] = [:]
        for song in raw {
            let id = try MusicNeteaseWire.id(song["id"] ?? song["songId"])
            ids.append(id)
            if let name = song["name"] as? String, !name.isEmpty { metadata[id] = song }
            else { missing.insert(id) }
        }
        if !missing.isEmpty {
            let rows = try await homeActionMetadata(ids.filter { missing.remove($0) != nil },
                                                    context: context, own: own, authorize: authorize)
            for row in rows { metadata[try MusicNeteaseWire.id(row["id"])] = row }
        }
        let rows = try ids.compactMap { id -> JSONValue? in
            guard let row = metadata[id] else { return nil }
            return try Self.song(row, id: id)
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
