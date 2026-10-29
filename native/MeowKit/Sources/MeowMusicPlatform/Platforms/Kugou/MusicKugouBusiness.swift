// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e kugou/{music,library}.go; no feedback, cloud writes or implicit FM play.
import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature
import MeowJSON

struct MusicKugouCollection: Sendable, Equatable {
    let id: String
    let listID: String
    let type: Int
    let owner: String
    let name: String
    let count: Int
    let globalID: String
    let favorite: Bool
    let sort: Int
    let fingerprint: Data
    var dto: JSONValue { .object(["id": .string(id), "title": .string(name), "count": .number(Double(count)), "owned": .bool(owned)]) }
    let owned: Bool
}
struct MusicKugouPages {
    var rows: [[String: Any]] = []
    var count: Int?
    var version: Int?
    var fingerprint: Data {
        get throws {
            var digest = SHA256()
            digest.update(data: try MusicKugouWire.json(["count": count.map { $0 as Any } ?? NSNull(),
                                                        "version": version.map { $0 as Any } ?? NSNull()]))
            for row in rows {
                digest.update(data: Data([10]))
                digest.update(data: try MusicKugouWire.json(row))
            }
            return Data(digest.finalize())
        }
    }
}
extension KugouMusicAccountAdapter {
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation {
        guard ["daily", "fm"].contains(kind), page == 1 else { throw MusicServiceError.invalidRequest }
        let context = try context(credential), uid = context.auth!.uid, token = context.auth!.token
        let root: [String: Any]
        if kind == "daily" {
            root = try await request(.daily, context: context, query: ["platform": "ios"])
        } else {
            let ms = Int(now().timeIntervalSince1970 * 1000)
            root = try await request(.fm, context: context, body: [
                "appid": 1005, "clienttime": ms, "mid": context.device.mid, "action": "login",
                "recommend_source_locked": 0, "song_pool_id": 0, "callerid": 0, "m_type": 1,
                "platform": "ios", "area_code": 1, "remain_songcnt": 0, "clientver": 20489, "is_overplay": 0,
                "mode": "normal", "fakem": "ca981cfc583a4c37f28d2d49000013c16a0a", "key": MusicKugouCrypto.paramsKey(ms),
                "userid": uid, "kguid": uid, "token": token
            ])
        }
        try check(context)
        let data = try MusicQQWire.object(root["data"])
        if kind == "fm", let mode = data["mode"], mode as? String != "normal" { throw MusicServiceError.upstreamInvalid }
        let rows = try MusicKugouWire.rows(data["song_list"])
        var output: [JSONValue] = [], bytes = 0
        for var raw in rows {
            // Reference explicitly excludes unavailable placeholders (no valid hash).
            guard (try? MusicKugouWire.hash(raw["hash"])) != nil else { continue }
            raw["name"] = raw["filename"]; raw["Duration"] = raw["time_length"]; raw["AlbumName"] = raw["album_name"]
            raw["cover"] = MusicKugouWire.first(raw["sizable_cover"], (raw["trans_param"] as? [String: Any])?["union_cover"])
            raw["filesize"] = MusicKugouWire.first(raw["file_size"], raw["filesize_128"])
            let value = try MusicKugouWire.publicRow(MusicKugouSong(raw).dto, context: context)
            bytes += try MusicPlatformJSON.bytes(value, maximum: 32_768)
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            output.append(value)
        }
        // cur_mark is not a verified continuation token. No auto-advance/feedback.
        return .init(rows: output, nextPage: nil)
    }
    func paginate(_ entry: MusicKugouCollection?, context: MusicKugouContext) async throws -> MusicKugouPages {
        var output = MusicKugouPages(), seen = Set<String>(), byteCount = 0
        let uid = context.auth!.uid, token = context.auth!.token, maximum = entry == nil ? 2000 : 20_000
        for page in 1...((maximum + 29) / 30 + 1) {
            try check(context)
            let root: [String: Any]
            if let entry {
                if entry.type == 0 {
                    root = try await request(.members, context: context, body: [
                        "listid": entry.listID, "userid": uid, "type": 0, "page": page, "pagesize": 30,
                        "area_code": 1, "allplatform": 1, "show_cover": 1, "token": token
                    ])
                } else {
                    guard entry.globalID.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil,
                          !entry.globalID.hasPrefix("id_") else { throw MusicServiceError.upstreamInvalid }
                    root = try await request(.subscription, context: context, query: [
                        "area_code": "1", "begin_idx": String((page - 1) * 30), "plat": "1", "type": "1", "mode": "1",
                        "personal_switch": "1", "extend_fields": "abtags,hot_cmt,popularization", "pagesize": "30",
                        "global_collection_id": entry.globalID
                    ])
                }
            } else {
                root = try await request(.directory, context: context, query: ["plat": "1", "userid": uid, "token": token],
                                         body: ["userid": uid, "token": token, "total_ver": 979, "type": 2, "page": page, "pagesize": 30])
            }
            let data = root["data"] as? [String: Any] ?? root
            let info = data["list_info"] as? [String: Any] ?? [:]
            if entry == nil || entry?.type == 0 {
                for source in [root, data] {
                    if let value = source["userid"], try MusicKugouWire.id(value) != uid { throw MusicServiceError.targetChanged }
                }
            }
            if let entry {
                if entry.type == 1 {
                    guard data["list_info"] is [String: Any] else { throw MusicServiceError.upstreamInvalid }
                    if let value = info["global_collection_id"], value as? String != entry.globalID { throw MusicServiceError.targetChanged }
                }
                for source in [data, info] {
                    if let value = source["listid"], try MusicKugouWire.id(value) != entry.listID { throw MusicServiceError.targetChanged }
                }
            }
            var count = try MusicKugouWire.first(data["count"], data["total"], info["count"], root["count"], root["total"])
                .map { try MusicKugouWire.integer($0, maximum: maximum) }
            if count == nil, entry == nil, ["list_count", "collect_count", "album_count"].contains(where: { data[$0] != nil }) {
                count = try ["list_count", "collect_count", "album_count"].reduce(0) { try $0 + MusicKugouWire.integer(data[$1], maximum: maximum) }
                guard count! <= maximum else { throw MusicServiceError.limit }
            }
            let versionValue = entry == nil ? MusicKugouWire.first(data["total_ver"], root["total_ver"])
                : MusicKugouWire.first(info["list_ver"], data["list_ver"], root["list_ver"])
            let version = try versionValue.map { try MusicKugouWire.integer($0) }
            if let count, let old = output.count, count != old { throw MusicServiceError.staleResult }
            if let version, let old = output.version, version != old { throw MusicServiceError.staleResult }
            if let count { output.count = count }; if let version { output.version = version }
            let raw = MusicKugouWire.first(data["info"], data["songs"], root["info"])
            let rows = try MusicKugouWire.rows(raw ?? (count == 0 ? [] as [Any] : NSNull()), maximum: 30)
            for row in rows {
                let identity: String
                if entry == nil { identity = try MusicKugouWire.id(row["type"], zero: true) + ":" + MusicKugouWire.id(row["listid"]) }
                else if entry!.type == 0 { identity = try MusicKugouWire.id(row["fileid"]) }
                else { identity = try MusicKugouWire.hash(MusicKugouWire.first(row["hash"], (row["audio_info"] as? [String: Any])?["hash"])) }
                guard seen.insert(identity).inserted else { throw MusicServiceError.upstreamInvalid }
                byteCount += try MusicKugouWire.json(row).count
                guard byteCount <= 4 * 1024 * 1024, output.rows.count < maximum else { throw MusicServiceError.limit }
                output.rows.append(row)
            }
            if let total = output.count {
                guard output.rows.count <= total else { throw MusicServiceError.upstreamInvalid }
                if output.rows.count == total { return output }
                guard !rows.isEmpty else { throw MusicServiceError.upstreamInvalid }
            } else if rows.isEmpty { return output }
        }
        throw MusicServiceError.limit
    }
    func directory(_ context: MusicKugouContext) async throws -> [MusicKugouCollection] {
        let pages = try await paginate(nil, context: context)
        var output: [MusicKugouCollection] = [], favoriteCount = 0
        for row in pages.rows {
            let type = try MusicKugouWire.integer(row["type"], maximum: 1)
            let source = try MusicKugouWire.integer(row["source"])
            let listID = try MusicKugouWire.id(row["listid"]), owner = try MusicKugouWire.id(row["list_create_userid"], zero: true)
            let name = try MusicKugouWire.text(row["name"], maximum: 600), count = try MusicKugouWire.integer(row["count"], maximum: 20_000)
            let special = [1, 2].contains((try? MusicKugouWire.integer(row["is_def"])) ?? 0) || (try? MusicKugouWire.integer(row["is_default"])) == 1
            let owned = type == 0 && owner == context.auth!.uid
            let favorite = owned && special && ["我喜欢", "我喜欢的音乐"].contains(name)
            if favorite { favoriteCount += 1 }
            let global = try MusicKugouWire.text(MusicKugouWire.first(row["global_collection_id"], row["list_create_gid"]) ?? "", maximum: 200, empty: true)
            let sort = (try? MusicKugouWire.integer(row["sort"])) ?? MusicPlatformJSON.maximumInteger
            var fingerprint: [String: Any] = [
                "id": "\(type):\(listID)", "owner": owner, "name": name, "count": count, "source": source,
                "globalID": global, "special": special, "favorite": favorite, "sort": sort,
                "directoryVersion": pages.version.map { $0 as Any } ?? NSNull()
            ]
            for key in ["list_ver", "tags", "intro", "pic"] { fingerprint[key] = row[key] ?? NSNull() }
            output.append(.init(id: "\(type):\(listID)", listID: listID, type: type, owner: owner, name: name, count: count,
                                globalID: global, favorite: favorite, sort: sort,
                                fingerprint: Data(SHA256.hash(data: try MusicKugouWire.json(fingerprint))), owned: owned))
        }
        guard favoriteCount <= 1 else { throw MusicServiceError.upstreamInvalid }
        return output.enumerated().sorted {
            if $0.element.type != $1.element.type { return $0.element.type < $1.element.type }
            if $0.element.sort != $1.element.sort { return $0.element.sort < $1.element.sort }
            return $0.offset < $1.offset
        }.map(\.element)
    }
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue] {
        let context = try context(credential)
        _ = try await profile(context)
        let first = try await directory(context), second = try await directory(context)
        try check(context)
        guard first == second else { throw MusicServiceError.staleResult }
        return try second.map { try MusicKugouWire.publicRow($0.dto, context: context) }
    }
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue] {
        guard collectionID.range(of: "^[01]:[1-9][0-9]{0,15}$", options: .regularExpression) != nil else { throw MusicServiceError.invalidRequest }
        let context = try context(credential)
        _ = try await profile(context)
        let first = try await directory(context)
        guard let entry = first.first(where: { $0.id == collectionID }) else { throw MusicServiceError.notFound }
        let a = try await paginate(entry, context: context), b = try await paginate(entry, context: context)
        guard a.rows.count == entry.count, b.rows.count == entry.count,
              try a.fingerprint == b.fingerprint else { throw MusicServiceError.staleResult }
        let last = try await directory(context)
        try check(context)
        guard first == last else { throw MusicServiceError.staleResult }
        let sorted = b.rows.enumerated().sorted {
            let a = (try? MusicKugouWire.integer($0.element["sort"])) ?? MusicPlatformJSON.maximumInteger
            let b = (try? MusicKugouWire.integer($1.element["sort"])) ?? MusicPlatformJSON.maximumInteger
            return a == b ? $0.offset < $1.offset : a < b
        }
        var bytes = 0
        return try sorted.map { _, raw in
            guard case .object(var fields) = try MusicKugouSong(raw).dto else { throw MusicServiceError.upstreamInvalid }
            // Preserve repeated hashes as separate list occurrences, keyed by fileid.
            if entry.type == 0 { fields["fileid"] = .string(try MusicKugouWire.id(raw["fileid"])) }
            let value = JSONValue.object(fields)
            bytes += try MusicPlatformJSON.bytes(value, maximum: 32_768)
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            return try MusicKugouWire.publicRow(value, context: context)
        }
    }
}
