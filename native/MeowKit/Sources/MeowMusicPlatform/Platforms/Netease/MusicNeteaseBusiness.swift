// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease/{catalog,library}.go, read operations only.
import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature
import MeowJSON

struct MusicNeteaseCollection: Equatable {
    let id: String
    let owner: String
    let title: String
    let special: Int
    let count: Int?
    func dto(accountID: String) -> JSONValue {
        .object(["id": .string(id), "title": .string(title),
                 "count": count.map { .number(Double($0)) } ?? .null, "owned": .bool(owner == accountID)])
    }
}
extension NeteaseMusicAccountAdapter {
    static func song(_ raw: [String: Any]?, id: String) throws -> JSONValue {
        let id = try MusicNeteaseWire.id(id), row = raw ?? [:]
        if let value = row["id"], try MusicNeteaseWire.id(value) != id { throw MusicServiceError.targetChanged }
        let album = row["al"] as? [String: Any] ?? row["album"] as? [String: Any] ?? [:]
        let artists = try MusicNeteaseWire.rows(row["ar"] ?? row["artists"] ?? [], maximum: 64)
            .map { try MusicQQWire.string($0["name"], maximum: 256) }
        let singer = try MusicQQWire.string(artists.joined(separator: "、"), maximum: 1024, empty: true)
        let duration = try MusicQQWire.integer(row["dt"] ?? row["duration"] ?? 0, maximum: 604_800_000)
        var types: [JSONValue] = []
        for (type, key) in [("128k", "l"), ("192k", "m"), ("320k", "h"), ("flac", "sq"), ("flac24bit", "hr")] {
            if let detail = row[key] as? [String: Any],
               let size = try? MusicQQWire.integer(detail["size"]), size > 0 { types.append(.string(type)) }
        }
        let albumID: String
        if let value = album["id"], !(value is NSNull), String(describing: value) != "0" {
            albumID = try MusicNeteaseWire.id(value)
        } else { albumID = "" }
        return .object(["musicId": .string(id), "songmid": .string(id),
            "name": .string(try MusicQQWire.string(row["name"] ?? "未知歌曲", maximum: 1024)),
            "singer": .string(singer.isEmpty ? "未知歌手" : singer),
            "album": .string(try MusicQQWire.string(album["name"] ?? "", maximum: 1024, empty: true)),
            "albumId": .string(albumID), "duration": .number(Double(duration) / 1000), "types": .array(types)])
    }
    static func nextDailyBoundary(_ date: Date) -> Date {
        let seconds = date.timeIntervalSince1970, day: Double = 86_400
        let today = floor((seconds + 8 * 3600) / day) * day - 8 * 3600
        let boundary = today + 6 * 3600
        return Date(timeIntervalSince1970: boundary > seconds ? boundary : boundary + day)
    }
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation {
        let own = epoch
        try check(own)
        guard ["daily", "fm"].contains(kind), page == 1 else { throw MusicServiceError.invalidRequest }
        let context = try context(credential), hash = Data(SHA256.hash(data: credential.dataRepresentation()))
        if kind == "daily", let saved = dailyCache, saved.credentialHash == hash, saved.expires > now() {
            return saved.result
        }
        let root: [String: Any], raw: Any?
        let boundary = Self.nextDailyBoundary(now())
        if kind == "daily" {
            root = try await request("/api/v3/discovery/recommend/songs", data: ["ispush": false], crypto: "xeapi", context: context)
            raw = (root["data"] as? [String: Any])?["dailySongs"]
        } else {
            root = try await request("/api/v1/radio/get", crypto: "weapi", context: context)
            raw = root["data"]
        }
        try check(own)
        let songs = try MusicNeteaseWire.rows(raw, maximum: 500)
        var seen = Set<String>()
        let rows = try songs.map { song -> JSONValue in
            let id = try MusicNeteaseWire.id(song["id"])
            guard seen.insert(id).inserted else { throw MusicServiceError.upstreamInvalid }
            return try Self.song(song, id: id)
        }
        // FM is one actual batch, not a fabricated page/played continuation.
        let result = MusicQQRecommendation(rows: rows, nextPage: nil, expiresAt: kind == "daily" ? boundary : nil)
        if kind == "daily" {
            guard boundary > now() else { throw MusicServiceError.staleResult }
            dailyCache = .init(credentialHash: hash, expires: boundary, result: result)
        }
        return result
    }
    static func collection(_ raw: [String: Any]) throws -> MusicNeteaseCollection {
        let count: Int?
        if let value = raw["trackCount"] { count = try MusicQQWire.integer(value, maximum: 20_000) }
        else { count = nil }
        return .init(id: try MusicNeteaseWire.id(raw["id"]),
            owner: try MusicNeteaseWire.id(MusicQQWire.object(raw["creator"])["userId"]),
            title: try MusicQQWire.string(raw["name"], maximum: 600),
            special: try MusicQQWire.integer(raw["specialType"] ?? 0, maximum: Int(Int32.max)), count: count)
    }
    func directory(_ context: MusicNeteaseContext) async throws -> [MusicNeteaseCollection] {
        let own = epoch
        var all: [MusicNeteaseCollection] = [], seen = Set<String>(), favorites = 0
        for offset in stride(from: 0, to: 2000, by: 100) {
            let root = try await request("/api/user/playlist",
                data: ["uid": context.accountID, "limit": 100, "offset": offset, "includeVideo": true],
                crypto: "weapi", context: context)
            try check(own)
            let rows = try MusicNeteaseWire.rows(root["playlist"], maximum: 100)
            let more = try MusicNeteaseWire.bool(root["more"])
            guard !more || !rows.isEmpty else { throw MusicServiceError.upstreamInvalid }
            for raw in rows {
                let row = try Self.collection(raw)
                guard seen.insert(row.id).inserted else { throw MusicServiceError.staleResult }
                if row.owner == context.accountID && row.special == 5 { favorites += 1 }
                all.append(row)
            }
            if !more {
                guard favorites == 1 else { throw MusicServiceError.upstreamInvalid }
                return all
            }
        }
        // Explicit bounded failure, never a partial "complete" directory.
        throw MusicServiceError.limit
    }
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue] {
        let own = epoch, context = try context(credential)
        _ = try await profile(context)
        let first = try await directory(context), second = try await directory(context)
        try check(own)
        guard first == second else { throw MusicServiceError.staleResult }
        return second.map { $0.dto(accountID: context.accountID) }
    }
    func detail(_ id: String, context: MusicNeteaseContext) async throws -> [String: Any] {
        let root = try await request("/api/v6/playlist/detail", data: ["id": id, "n": 100000, "s": 8], context: context)
        let row = try MusicQQWire.object(root["playlist"])
        guard try MusicNeteaseWire.id(row["id"]) == id else { throw MusicServiceError.targetChanged }
        return row
    }
    static func orderedIDs(_ row: [String: Any]) throws -> [String] {
        let ids = try MusicNeteaseWire.rows(row["trackIds"], maximum: 20_000).map { try MusicNeteaseWire.id($0["id"]) }
        guard Set(ids).count == ids.count,
              try MusicQQWire.integer(row["trackCount"], maximum: 20_000) == ids.count else { throw MusicServiceError.upstreamInvalid }
        return ids
    }
    func metadata(_ ids: [String], context: MusicNeteaseContext) async throws -> [[String: Any]] {
        guard !ids.isEmpty, ids.count <= 100 else { throw MusicServiceError.invalidRequest }
        // IDs are validated decimal text, not Doubles: retain exact large IDs.
        let c = "[" + (try ids.map { "{\"id\":" + (try MusicNeteaseWire.id($0)) + "}" }).joined(separator: ",") + "]"
        let root = try await request("/api/v3/song/detail", data: ["c": c], crypto: "weapi", context: context)
        return try MusicNeteaseWire.rows(root["songs"], maximum: 100)
    }
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue] {
        let id = try MusicNeteaseWire.id(collectionID), own = epoch, context = try context(credential)
        _ = try await profile(context)
        let before = try await directory(context)
        guard let entry = before.first(where: { $0.id == id }) else {
            let after = try await directory(context)
            guard before == after else { throw MusicServiceError.staleResult }
            throw MusicServiceError.notFound
        }
        let first = try await detail(id, context: context), firstEntry = try Self.collection(first)
        guard entry.id == firstEntry.id, entry.owner == firstEntry.owner,
              entry.title == firstEntry.title, entry.special == firstEntry.special,
              entry.count == nil || entry.count == firstEntry.count else { throw MusicServiceError.staleResult }
        let ids = try Self.orderedIDs(first), members = Set(ids)
        var songs: [String: [String: Any]] = [:]
        for raw in try MusicNeteaseWire.rows(first["tracks"] ?? [], maximum: 20_000) {
            let id = try MusicNeteaseWire.id(raw["id"])
            guard members.contains(id), songs[id] == nil else { throw MusicServiceError.upstreamInvalid }
            songs[id] = raw
        }
        let missing = ids.filter { songs[$0] == nil }
        for offset in stride(from: 0, to: missing.count, by: 100) {
            let batch = Array(missing[offset..<min(offset + 100, missing.count)])
            do {
                for raw in try await metadata(batch, context: context) {
                    let id = try MusicNeteaseWire.id(raw["id"])
                    guard batch.contains(id), songs[id] == nil else { throw MusicServiceError.upstreamInvalid }
                    songs[id] = raw
                }
            } catch let error as MusicServiceError where [.unavailable, .timeout, .upstreamFailed].contains(error) {
                // A missing title is not a missing member. Authentication,
                // malformed identities, cancellation and stale binding escape.
                try check(own)
            }
        }
        let second = try await detail(id, context: context)
        guard try Self.collection(second) == firstEntry, try Self.orderedIDs(second) == ids else { throw MusicServiceError.staleResult }
        let after = try await directory(context)
        try check(own)
        guard before == after else { throw MusicServiceError.staleResult }
        var bytes = 0
        return try ids.map { id in
            let value = try Self.song(songs[id], id: id)
            bytes += try MusicPlatformJSON.bytes(value)
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            return value
        }
    }
}
