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
        case .songRoaming(let ids), .artistRoaming(let ids):
            guard (1...20).contains(ids.count) else { throw MusicServiceError.invalidRequest }
            try ids.forEach(id)
        case .songRecommendations(let seed, let offset):
            try id(seed)
            guard offset >= 0 else { throw MusicServiceError.invalidRequest }
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
        case .artistRoaming(let ids):
            return ("/api/v1/radio/get", [
                "limit": "3", "mode": "SIMILAR", "sourceType": "artist",
                "sourceIds": "[" + ids.joined(separator: ",") + "]", "unplaySonIds": "[]"
            ], "weapi")
        case .songRecommendations(let seed, let offset):
            return ("/api/homepage/scene/more/rcmd/song", [
                "currentPlaySongId": seed, "songIds": "[" + seed + "]",
                "songCount": "1", "offset": String(offset),
                "source": "curlist_more_rcmd", "extJson": "{}"
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
        var heartbeatSeed: [String: Any]?
        if case .heartbeat(let last, let fromHome, let supplied) = value {
            let liked = try await heartbeatLikes(account, own: own, authorize: authorize)
            memberCount = liked.ids.count
            if let supplied {
                guard supplied.playlistID == liked.playlistID,
                      fromHome || liked.validIDs.contains(supplied.seedID) else {
                    throw MusicServiceError.targetChanged
                }
                guard let last else { throw MusicServiceError.invalidRequest }
                resolved = .heartbeat(lastID: last, fromHome: fromHome, context: supplied)
            } else {
                let songID: String
                if let last {
                    guard fromHome || liked.validIDs.contains(last) else { throw MusicServiceError.sourceUnavailable }
                    songID = last
                } else {
                    guard fromHome else { throw MusicServiceError.invalidRequest }
                    songID = chooseLikedSong(liked.validIDs)
                    guard liked.validIDs.contains(songID) else { throw MusicServiceError.invalidRequest }
                }
                let context = MusicSimilarHeartbeatContext(playlistID: liked.playlistID, seedID: songID)
                heartbeatSeed = liked.songs[songID]
                resolved = .heartbeat(lastID: songID, fromHome: fromHome, context: context)
            }
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
        var hasMore: Bool?, offset: Int?
        switch resolved {
        case .heartbeat(_, _, let context):
            guard let context else { throw MusicServiceError.invalidRequest }
            raw = try MusicNeteaseWire.rows(result["data"])
            source = "heartbeat"; sourceID = context.playlistID; heartbeat = context
        case .songRoaming, .artistRoaming:
            let data = result["data"], body = data as? [String: Any]
            raw = try MusicNeteaseWire.rows(body?["songs"] ?? data, maximum: 1000)
            source = "similar"; sourceID = ""; heartbeat = nil
        case .songRecommendations(_, let requestedOffset):
            let body = try MusicQQWire.object(result["data"])
            raw = try MusicNeteaseWire.rows(body["songList"])
            algorithms = try Self.recommendationMap(body["algMap"])
            sources = try Self.recommendationMap(body["sourceMap"])
            let more = try MusicNeteaseWire.bool(body["hasMore"])
            let next = try MusicQQWire.integer(body["offset"])
            guard !more || next > requestedOffset else { throw MusicServiceError.upstreamInvalid }
            hasMore = more; offset = next
            source = "moreRcmd"; sourceID = ""; heartbeat = nil
        }

        var ordered: [(id: String, algorithm: String, source: String, sourceID: String)] = []
        var metadata: [String: [String: Any]] = [:]
        var missing: [String] = [], requested = Set<String>()
        for row in raw {
            let song = (row["songInfo"] as? [String: Any]) ?? (row["song"] as? [String: Any]) ?? row
            let id = try MusicNeteaseWire.id(song["id"] ?? song["songId"])
            let algorithm = source == "moreRcmd" ? algorithms[id] : row["alg"] ?? song["alg"]
            let text = try MusicQQWire.string(algorithm == nil || algorithm is NSNull ? "" : algorithm,
                                             maximum: 2000, empty: true)
            let rawSource = source == "moreRcmd" ? sources[id] : row["source"] ?? song["source"]
            let feedbackSource = try MusicQQWire.string(rawSource == nil || rawSource is NSNull ? "" : rawSource,
                                                       maximum: 2000, empty: true)
            let rawSourceID = row["sourceId"] ?? song["sourceId"]
            let feedbackSourceID: String
            if let rawSourceID, !(rawSourceID is NSNull) {
                feedbackSourceID = try (rawSourceID as? String) ?? MusicNeteaseWire.id(rawSourceID)
            } else { feedbackSourceID = "" }
            let secrets = Array(account.cookies.values) + Array(account.device.values)
            ordered.append((id, try MusicAccountPublicText.profile(text, secrets: secrets),
                try MusicAccountPublicText.profile(feedbackSource, secrets: secrets),
                try MusicAccountPublicText.profile(MusicQQWire.string(feedbackSourceID, maximum: 2048, empty: true),
                    secrets: secrets)))
            if Self.hasDisplayFields(song) { metadata[id] = song }
            else if requested.insert(id).inserted { missing.append(id) }
        }
        missing.removeAll { metadata[$0] != nil }
        if case .heartbeat(_, _, nil) = value, case .heartbeat(let selected?, _, _) = resolved, heartbeatSeed == nil,
           metadata[selected] == nil, requested.insert(selected).inserted { missing.append(selected) }
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
        var items = try ordered.map { item -> MusicNativeTrackData in
            guard let song = metadata[item.id] else { throw MusicServiceError.targetChanged }
            guard case .object(var row) = try Self.song(song, id: item.id) else { throw MusicServiceError.upstreamInvalid }
            // Credential-free attribution for the existing feedback consumer;
            // this does not itself authorize feedback or include a media URL.
            row["_recommendation"] = .object([
                "songId": .string(item.id), "alg": .string(item.algorithm),
                "source": .string(source), "sourceId": .string(sourceID),
                "feedbackSource": .string(item.source), "feedbackSourceId": .string(item.sourceID)
            ])
            if let heartbeat, case .object(var origin) = row["_recommendation"] {
                origin["seedId"] = .string(heartbeat.seedID)
                row["_recommendation"] = .object(origin)
            }
            let data = try JSONEncoder().encode(JSONValue.object(row))
            guard data.count <= 16 * 1024 else { throw MusicServiceError.limit }
            bytes += data.count
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            return .init(platform: "wy", data: data)
        }
        if case .heartbeat(_, _, nil) = value, case .heartbeat(let selected, _, _) = resolved, let selected {
            if let index = ordered.firstIndex(where: { $0.id == selected }) {
                let first = items.remove(at: index); items.insert(first, at: 0)
            } else {
                guard let song = heartbeatSeed ?? metadata[selected] else {
                    throw MusicServiceError.upstreamInvalid
                }
                guard case .object(var row) = try Self.song(song, id: selected) else { throw MusicServiceError.upstreamInvalid }
                // This locally selected favorite is in the real heartbeat scene,
                // but has no per-song algorithm/source returned by this batch.
                row["_recommendation"] = .object([
                    "songId": .string(selected), "source": .string("heartbeat"), "sourceId": .string(sourceID),
                    "seedId": .string(heartbeat!.seedID), "alg": .string(""),
                    "feedbackSource": .string(""), "feedbackSourceId": .string("")
                ])
                let data = try JSONEncoder().encode(JSONValue.object(row))
                guard data.count <= 16 * 1024, bytes + data.count <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
                items.insert(.init(platform: "wy", data: data), at: 0)
            }
        }
        try await authorize(); try check(own)
        return .init(items: items, heartbeatContext: heartbeat, hasMore: hasMore, offset: offset)
    }

    private static func recommendationMap(_ value: Any?) throws -> [String: Any] {
        guard let value, !(value is NSNull) else { return [:] }
        if let text = value as? String {
            return try MusicQQWire.decode(Data(text.utf8))
        }
        return try MusicQQWire.object(value)
    }

    private func heartbeatMembership(_ account: MusicNeteaseContext, own: UUID,
                                     authorize: @escaping @Sendable () async throws -> Void) async throws
        -> (playlistID: String, ids: [String], songs: [String: [String: Any]]) {
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
        let members = Set(ids)
        var songs: [String: [String: Any]] = [:], received = Set<String>()
        for row in try MusicNeteaseWire.rows(detail["tracks"] ?? []) {
            let id = try MusicNeteaseWire.id(row["id"])
            guard members.contains(id), received.insert(id).inserted else { throw MusicServiceError.upstreamInvalid }
            if Self.hasDisplayFields(row) { songs[id] = row }
        }
        return (pid, ids, songs)
    }

    func heartbeatHomeSeed(_ account: MusicNeteaseContext, own: UUID,
                           authorize: @escaping @Sendable () async throws -> Void) async throws -> [String: Any]? {
        let membership = try await heartbeatMembership(account, own: own, authorize: authorize)
        for id in membership.ids {
            if let song = membership.songs[id] { return song }
        }
        guard let id = membership.ids.first else { return nil }
        try await authorize(); try check(own)
        let rows = try await metadata([id], context: account)
        try await authorize(); try check(own)
        guard rows.count == 1, try MusicNeteaseWire.id(rows[0]["id"]) == id else {
            throw MusicServiceError.upstreamInvalid
        }
        guard Self.hasDisplayFields(rows[0]) else { throw MusicServiceError.notFound }
        return rows[0]
    }

    private func heartbeatLikes(_ account: MusicNeteaseContext, own: UUID,
                                authorize: @escaping @Sendable () async throws -> Void) async throws
        -> (playlistID: String, ids: [String], validIDs: [String], songs: [String: [String: Any]]) {
        let membership = try await heartbeatMembership(account, own: own, authorize: authorize)
        let ids = membership.ids
        var songs = membership.songs
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
        return (membership.playlistID, ids, valid, songs)
    }

    private static func isOwnHeartbeatList(_ row: [String: Any], accountID: String) throws -> Bool {
        guard try MusicQQWire.integer(row["specialType"] ?? 0) == 5 else { return false }
        let owners = [row["userId"], (row["creator"] as? [String: Any])?["userId"]]
            .compactMap { $0 }.filter { !($0 is NSNull) }
        guard !owners.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return try owners.allSatisfy { try MusicNeteaseWire.id($0) == accountID }
    }
}
