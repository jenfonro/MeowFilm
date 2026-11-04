// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus b900c259: fresh heartbeat, similar-radio and queue recommendations.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

protocol MusicAccountSimilarModeProvider: Sendable {
    func similarMode(_ request: MusicNativeSimilarModeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch
}

enum MusicNeteaseSimilarModeRequest {
    static func id(_ value: String) throws {
        guard (1...16).contains(value.utf8.count), value.utf8.first != 48,
              value.utf8.allSatisfy({ (48...57).contains($0) }),
              let number = UInt64(value), number <= UInt64(MusicPlatformJSON.maximumInteger) else {
            throw MusicServiceError.invalidRequest
        }
    }
    static func validate(_ value: MusicNativeSimilarModeRequest) throws {
        switch value {
        case .heartbeat(let last, _, let context):
            if let last { try id(last) }
            if let context { try id(context.playlistID); try id(context.seedID) }
        case .songRoaming(let ids):
            guard (1...20).contains(ids.count) else { throw MusicServiceError.invalidRequest }
            try ids.forEach(id)
        case .queueRecommendations(let ids, let current):
            guard !ids.isEmpty, ids.contains(current) else { throw MusicServiceError.invalidRequest }
            try ids.forEach(id); try id(current)
        }
    }
    static func specification(_ value: MusicNativeSimilarModeRequest) throws -> (path: String, data: [String: Any], crypto: String)? {
        try validate(value)
        switch value {
        case .heartbeat(let last, let fromHome, let context):
            guard let context else { return nil }
            return ("/api/playmode/intelligence/list", [
                "playlistId": context.playlistID, "startMusicId": context.seedID,
                "songId": last ?? context.seedID, "type": fromHome ? "fromPlayAll" : "fromPlayOne",
                "count": 100
            ], "eapi")
        case .songRoaming(let ids):
            return ("/api/v1/radio/get", [
                "limit": "3", "mode": "SIMILAR", "sourceType": "song",
                "sourceIds": "[" + ids.joined(separator: ",") + "]", "unplaySonIds": "[]"
            ], "weapi")
        case .queueRecommendations(let ids, let current):
            return ("/api/homepage/more/rcmd/song", [
                "songIds": "[" + ids.joined(separator: ",") + "]",
                "currentPlaySongId": current, "source": "curlist_more_rcmd"
            ], "eapi")
        }
    }
}

extension NeteaseMusicAccountAdapter: MusicAccountSimilarModeProvider {
    func similarMode(_ value: MusicNativeSimilarModeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch {
        let own = epoch
        try check(own); try MusicNeteaseSimilarModeRequest.validate(value)
        try await authorize(); try check(own)
        let account = try context(credential)
        var resolved = value
        if case .heartbeat(let last, let fromHome, nil) = value {
            let seed = try await similarModeHeartbeatSeed(account, own: own, authorize: authorize)
            resolved = .heartbeat(lastID: last, fromHome: fromHome, context: seed)
        }
        guard let spec = try MusicNeteaseSimilarModeRequest.specification(resolved) else {
            throw MusicServiceError.invalidRequest
        }
        try await authorize(); try check(own)
        let result = try await request(spec.path, data: spec.data, crypto: spec.crypto, context: account)
        try await authorize(); try check(own)

        let raw: [[String: Any]], source: String, sourceID: String
        let heartbeat: MusicSimilarHeartbeatContext?
        var algorithms: [String: Any] = [:]
        switch resolved {
        case .heartbeat(_, _, let context):
            guard let context else { throw MusicServiceError.invalidRequest }
            raw = try MusicNeteaseWire.rows(result["data"], maximum: 100)
            source = "heartbeat"; sourceID = context.playlistID; heartbeat = context
        case .songRoaming:
            let data = result["data"], body = data as? [String: Any]
            raw = try MusicNeteaseWire.rows(body?["songs"] ?? data, maximum: 1000)
            source = "similar"; sourceID = ""; heartbeat = nil
        case .queueRecommendations:
            let body = try MusicQQWire.object(result["data"])
            raw = try MusicNeteaseWire.rows(body["songList"] ?? [], maximum: 1000)
            if let map = body["algMap"], !(map is NSNull) { algorithms = try MusicQQWire.object(map) }
            source = "moreRcmd"; sourceID = ""; heartbeat = nil
        }

        var ordered: [(id: String, algorithm: String)] = [], metadata: [String: [String: Any]] = [:]
        var missing: [String] = [], requested = Set<String>()
        for row in raw {
            let song = (row["songInfo"] as? [String: Any]) ?? (row["song"] as? [String: Any]) ?? row
            let id = try MusicNeteaseWire.id(song["id"] ?? song["songId"])
            let algorithm = source == "moreRcmd" ? algorithms[id] : row["alg"] ?? song["alg"]
            let text = try MusicQQWire.string(algorithm == nil || algorithm is NSNull ? "" : algorithm,
                                             maximum: 2000, empty: true)
            ordered.append((id, text))
            if let title = song["name"] as? String, !title.isEmpty { metadata[id] = song }
            else if requested.insert(id).inserted { missing.append(id) }
        }
        missing.removeAll { metadata[$0] != nil }
        for offset in stride(from: 0, to: missing.count, by: 100) {
            try await authorize(); try check(own)
            let ids = Array(missing.dropFirst(offset).prefix(100))
            let rows = try await self.metadata(ids, context: account)
            try await authorize(); try check(own)
            var seen = Set<String>()
            for row in rows {
                let id = try MusicNeteaseWire.id(row["id"])
                guard ids.contains(id), seen.insert(id).inserted,
                      let title = row["name"] as? String, !title.isEmpty else { throw MusicServiceError.upstreamInvalid }
                metadata[id] = row
            }
        }
        var bytes = 0
        let items = try ordered.map { item -> MusicNativeTrackData in
            guard let song = metadata[item.id] else { throw MusicServiceError.targetChanged }
            guard case .object(var row) = try Self.song(song, id: item.id) else { throw MusicServiceError.upstreamInvalid }
            let album = (song["al"] ?? song["album"]) as? [String: Any]
            if let text = album?["picUrl"] as? String,
               let url = URL(string: text), ["https", "http"].contains(url.scheme ?? ""),
               url.user == nil, url.password == nil, url.fragment == nil,
               let host = url.host, host == "music.126.net" || host.hasSuffix(".music.126.net") {
                row["img"] = .string(text)
            }
            // Credential-free attribution for the existing feedback consumer;
            // this does not itself authorize feedback or include a media URL.
            row["_recommendation"] = .object([
                "songId": .string(item.id), "alg": .string(item.algorithm),
                "source": .string(source), "sourceId": .string(sourceID)
            ])
            let data = try JSONEncoder().encode(JSONValue.object(row))
            guard data.count <= 16 * 1024 else { throw MusicServiceError.limit }
            bytes += data.count
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            return .init(platform: "wy", data: data)
        }
        try await authorize(); try check(own)
        return .init(items: items, heartbeatContext: heartbeat)
    }

    private func similarModeHeartbeatSeed(_ account: MusicNeteaseContext, own: UUID,
                                          authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicSimilarHeartbeatContext {
        try await authorize(); try check(own)
        let result = try await request("/api/user/playlist",
            data: ["uid": account.accountID, "limit": 100, "offset": 0, "includeVideo": true],
            crypto: "weapi", context: account)
        try await authorize(); try check(own)
        let lists = try MusicNeteaseWire.rows(result["playlist"], maximum: 100)
        guard let liked = lists.first(where: { (try? MusicQQWire.integer($0["specialType"] ?? 0)) == 5 }) else {
            throw MusicServiceError.notFound
        }
        let owners = [liked["userId"], (liked["creator"] as? [String: Any])?["userId"]]
            .compactMap { $0 }.filter { !($0 is NSNull) }
        guard !owners.isEmpty, try owners.allSatisfy({ try MusicNeteaseWire.id($0) == account.accountID }) else {
            throw MusicServiceError.targetChanged
        }
        let pid = try MusicNeteaseWire.id(liked["id"])
        try MusicNeteaseSimilarModeRequest.id(pid)
        let detail = try await self.detail(pid, context: account)
        try await authorize(); try check(own)
        let first = (detail["trackIds"] as? [[String: Any]])?.first?["id"] ??
            (detail["tracks"] as? [[String: Any]])?.first?["id"]
        guard let first else { throw MusicServiceError.notFound }
        let sid = try MusicNeteaseWire.id(first)
        try MusicNeteaseSimilarModeRequest.id(sid)
        return .init(playlistID: pid, seedID: sid)
    }
}
