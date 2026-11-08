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
    static func specification(_ value: MusicNativeSimilarModeRequest, heartbeatCount: Int) throws -> (path: String, data: [String: Any], crypto: String)? {
        try validate(value)
        switch value {
        case .heartbeat(let last, let fromHome, let context):
            guard let context else { return nil }
            return ("/api/playmode/intelligence/list", [
                "playlistId": context.playlistID, "startMusicId": context.seedID,
                "songId": last ?? context.seedID, "type": fromHome ? "fromPlayAll" : "fromPlayOne",
                "count": heartbeatCount
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
        try await similarMode(value, credential: credential, authorize: authorize,
                              chooseLikedSong: { $0.randomElement()! })
    }

    func similarMode(_ value: MusicNativeSimilarModeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void,
                     chooseLikedSong: @Sendable ([String]) -> String) async throws -> MusicNativeSimilarModeBatch {
        let own = epoch
        try check(own); try MusicNeteaseSimilarModeRequest.validate(value)
        try await authorize(); try check(own)
        let account = try context(credential)
        var resolved = value, memberCount = 0
        if case .heartbeat(let last, let fromHome, let supplied) = value {
            let liked = try await heartbeatLikes(account, own: own, authorize: authorize)
            memberCount = liked.ids.count
            let context = MusicSimilarHeartbeatContext(playlistID: liked.playlistID, seedID: liked.validIDs[0])
            guard supplied == nil || supplied == context else { throw MusicServiceError.targetChanged }
            let songID: String
            if fromHome {
                songID = chooseLikedSong(liked.validIDs)
                guard liked.validIDs.contains(songID) else { throw MusicServiceError.invalidRequest }
            } else {
                guard let last else { throw MusicServiceError.invalidRequest }
                songID = last
            }
            resolved = .heartbeat(lastID: songID, fromHome: fromHome, context: context)
        }
        guard let spec = try MusicNeteaseSimilarModeRequest.specification(resolved, heartbeatCount: memberCount) else {
            throw MusicServiceError.invalidRequest
        }
        try await authorize(); try check(own)
        let result = try await request(spec.path, data: spec.data, crypto: spec.crypto, context: account)
        try await authorize(); try check(own)

        let raw: [[String: Any]], source: String, sourceID: String
        let heartbeat: MusicSimilarHeartbeatContext?
        var algorithms: [String: Any] = [:], sources: [String: Any] = [:]
        var hasMore: Bool?
        switch resolved {
        case .heartbeat(_, _, let context):
            guard let context else { throw MusicServiceError.invalidRequest }
            raw = try MusicNeteaseWire.rows(result["data"])
            source = "heartbeat"; sourceID = context.playlistID; heartbeat = context
        case .songRoaming:
            let data = result["data"], body = data as? [String: Any]
            raw = try MusicNeteaseWire.rows(body?["songs"] ?? data, maximum: 1000)
            source = "similar"; sourceID = ""; heartbeat = nil
        case .queueRecommendations:
            let body = try MusicQQWire.object(result["data"])
            raw = try MusicNeteaseWire.rows(body["songList"] ?? [])
            algorithms = try Self.recommendationMap(body["algMap"])
            sources = try Self.recommendationMap(body["sourceMap"])
            if let value = body["hasMore"], !(value is NSNull) { hasMore = try MusicNeteaseWire.bool(value) }
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
            if Self.hasDisplayFields(song) { metadata[id] = song }
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
                      Self.hasDisplayFields(row) else { throw MusicServiceError.upstreamInvalid }
                metadata[id] = row
            }
        }
        var bytes = 0
        let items = try ordered.map { item -> MusicNativeTrackData in
            guard let song = metadata[item.id] else { throw MusicServiceError.targetChanged }
            guard case .object(var row) = try Self.song(song, id: item.id) else { throw MusicServiceError.upstreamInvalid }
            // Credential-free attribution for the existing feedback consumer;
            // this does not itself authorize feedback or include a media URL.
            let feedbackSource: String
            if source == "heartbeat" { feedbackSource = "aiMode" }
            else if source == "moreRcmd" {
                let value = sources[item.id]
                let mapped = try MusicQQWire.string(value == nil || value is NSNull ? "" : value,
                                                   maximum: 2000, empty: true)
                feedbackSource = mapped.isEmpty ? "moreRcmd" : mapped
            } else { feedbackSource = "" }
            row["_recommendation"] = .object([
                "songId": .string(item.id), "alg": .string(item.algorithm),
                "source": .string(source), "sourceId": .string(sourceID),
                "feedbackSource": .string(feedbackSource)
            ])
            let data = try JSONEncoder().encode(JSONValue.object(row))
            guard data.count <= 16 * 1024 else { throw MusicServiceError.limit }
            bytes += data.count
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            return .init(platform: "wy", data: data)
        }
        try await authorize(); try check(own)
        return .init(items: items, heartbeatContext: heartbeat, hasMore: hasMore)
    }

    private static func recommendationMap(_ value: Any?) throws -> [String: Any] {
        guard let value, !(value is NSNull) else { return [:] }
        if let text = value as? String {
            return try MusicQQWire.decode(Data(text.utf8))
        }
        return try MusicQQWire.object(value)
    }

    private func heartbeatLikes(_ account: MusicNeteaseContext, own: UUID,
                                authorize: @escaping @Sendable () async throws -> Void) async throws
        -> (playlistID: String, ids: [String], validIDs: [String]) {
        var offset = 0, seen = Set<String>(), favorites: [String] = []
        while true {
            try await authorize(); try check(own)
            let result = try await request("/api/user/playlist",
                data: ["uid": account.accountID, "limit": 100, "offset": offset, "includeVideo": true],
                crypto: "weapi", context: account)
            try await authorize(); try check(own)
            let rows = try MusicNeteaseWire.rows(result["playlist"], maximum: 100)
            let more = try MusicNeteaseWire.bool(result["more"])
            guard !more || !rows.isEmpty else { throw MusicServiceError.upstreamInvalid }
            for row in rows {
                let id = try MusicNeteaseWire.id(row["id"])
                guard seen.insert(id).inserted else { throw MusicServiceError.staleResult }
                if try Self.isOwnHeartbeatList(row, accountID: account.accountID) { favorites.append(id) }
            }
            guard seen.count <= 2000 else { throw MusicServiceError.limit }
            if !more { break }
            guard seen.count < 2000 else { throw MusicServiceError.limit }
            offset += rows.count
        }
        guard favorites.count == 1, let pid = favorites.first else { throw MusicServiceError.targetChanged }
        try await authorize(); try check(own)
        var detail = try await self.detail(pid, context: account)
        try await authorize(); try check(own)
        guard try Self.isOwnHeartbeatList(detail, accountID: account.accountID) else { throw MusicServiceError.targetChanged }
        if detail["trackIds"] == nil || detail["trackIds"] is NSNull {
            let tracks = try MusicNeteaseWire.rows(detail["tracks"] ?? [])
            if tracks.isEmpty {
                guard try MusicQQWire.integer(detail["trackCount"]) == 0,
                      try MusicQQWire.integer(detail["cloudTrackCount"] ?? 0) == 0 else {
                    throw MusicServiceError.upstreamInvalid
                }
            }
            detail["trackIds"] = tracks
        }
        let ids = try Self.orderedIDs(detail)
        guard !ids.isEmpty else { throw MusicServiceError.notFound }
        let members = Set(ids)
        var songs: [String: [String: Any]] = [:], received = Set<String>()
        for row in try MusicNeteaseWire.rows(detail["tracks"] ?? []) {
            let id = try MusicNeteaseWire.id(row["id"])
            guard members.contains(id), received.insert(id).inserted else { throw MusicServiceError.upstreamInvalid }
            if Self.hasDisplayFields(row) { songs[id] = row }
        }
        let missing = ids.filter { songs[$0] == nil }
        for offset in stride(from: 0, to: missing.count, by: 100) {
            try await authorize(); try check(own)
            let batch = Array(missing.dropFirst(offset).prefix(100))
            let rows = try await metadata(batch, context: account)
            try await authorize(); try check(own)
            var received = Set<String>()
            for row in rows {
                let id = try MusicNeteaseWire.id(row["id"])
                guard batch.contains(id), received.insert(id).inserted else { throw MusicServiceError.upstreamInvalid }
                if Self.hasDisplayFields(row) { songs[id] = row }
            }
            guard received.count == batch.count else { throw MusicServiceError.upstreamInvalid }
        }
        let valid = ids.filter { songs[$0] != nil }
        guard !valid.isEmpty else { throw MusicServiceError.notFound }
        return (pid, ids, valid)
    }

    private static func isOwnHeartbeatList(_ row: [String: Any], accountID: String) throws -> Bool {
        guard try MusicQQWire.integer(row["specialType"] ?? 0) == 5 else { return false }
        let owners = [row["userId"], (row["creator"] as? [String: Any])?["userId"]]
            .compactMap { $0 }.filter { !($0 is NSNull) }
        guard !owners.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return try owners.allSatisfy { try MusicNeteaseWire.id($0) == accountID }
    }
}
