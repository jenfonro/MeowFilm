// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

enum MusicNativeNetworkPlaylists {
    static func id(platform: String, remoteID: String) -> String {
        "network:" + MJ.strings([platform, remoteID]).json
    }
    static func validateIdentity(platform: String, remoteID: String) throws {
        guard let platform = MusicPublicBrowsePlatform(rawValue: platform) else { throw MusicServiceError.invalidRequest }
        if [.netease, .qq, .kuwo].contains(platform) {
            do { _ = try MusicPlatformProtocol.numericID(.string(remoteID)) }
            catch { throw MusicServiceError.invalidRequest }
        } else {
            try musicRequire(remoteID.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil &&
                (platform != .kugou || !remoteID.hasPrefix("id_")), .invalidRequest)
        }
    }
    static func row(_ collection: MusicPublicBrowseCollection) throws -> MJ {
        try musicRequire(collection.kind == .playlist, .unsupported)
        try validateIdentity(platform: collection.platform.rawValue, remoteID: collection.nativeID)
        _ = try musicText(collection.title, maximum: 600)
        _ = try MJ.string(collection.creator).text(600, empty: true)
        try musicRequire(collection.count == nil || collection.count! >= 0, .invalidRequest)
        return .obj([
            ("id", .string(id(platform: collection.platform.rawValue, remoteID: collection.nativeID))),
            ("platform", .string(collection.platform.rawValue)), ("remoteID", .string(collection.nativeID)),
            ("name", .string(collection.title)), ("creator", .string(collection.creator)),
            ("artwork", MusicModel.publicURL(.string(collection.artwork?.absoluteString ?? ""))),
            ("count", collection.count.map(MJ.n) ?? .null), ("revision", .n(1)),
            ("present", .bool(true)),
            ("pending", .bool(MusicAccountPlatform(rawValue: collection.platform.rawValue) != nil)), ("relation", .null)
        ])
    }
    static func validate(_ library: MJ) throws {
        var ids = Set(library["collections"].array.map { $0["id"].string })
        for row in library["networkReferences"].array {
            let fields = ["id", "platform", "remoteID", "name", "creator", "artwork", "count",
                          "revision", "present", "pending", "relation"]
            try row.requireFields(fields, required: fields)
            do { try validateIdentity(platform: row["platform"].string, remoteID: row["remoteID"].string) }
            catch { throw MusicServiceError.corruptDocument }
            try musicRequire(row["id"].string == id(platform: row["platform"].string, remoteID: row["remoteID"].string) &&
                ids.insert(row["id"].string).inserted && row["revision"].isInteger && row["revision"].int > 0 &&
                row["present"].isBool && row["pending"].isBool && row["name"].isString &&
                !row["name"].string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                row["name"].string.utf8.count <= 600 &&
                row["creator"].isString && row["creator"].string.utf8.count <= 600 &&
                row["artwork"].isString && row["artwork"] == MusicModel.publicURL(row["artwork"]) &&
                (row["count"] == .null || row["count"].isInteger && row["count"].int >= 0), .corruptDocument)
            let relation = row["relation"]
            if MusicAccountPlatform(rawValue: row["platform"].string) == nil {
                try musicRequire(!row["pending"].bool && relation == .null, .corruptDocument)
            }
            if relation != .null {
                try relation.requireFields(["accountId", "baseline", "relationID"], required: ["accountId", "baseline", "relationID"])
                try musicRequire(relation["accountId"].isString && !relation["accountId"].string.isEmpty &&
                    relation["accountId"] == library["accounts"][row["platform"].string] &&
                    relation["baseline"].isBool && relation["relationID"].isString &&
                    relation["relationID"].string.utf8.count <= 256 &&
                    (!relation["baseline"].bool || !relation["relationID"].string.isEmpty), .corruptDocument)
                if relation["baseline"].bool && row["platform"].string == "kg" {
                    try musicRequire(relation["relationID"].string.range(of: "^1:[1-9][0-9]*$", options: .regularExpression) != nil,
                                     .corruptDocument)
                }
            }
        }
    }
}

extension NativeMusicService: MusicNetworkPlaylistServing {
    func networkCollectionDTO(_ row: MJ) -> MusicCollection {
        .init(id: row["id"].string, title: row["name"].string, kind: "network",
              count: row["count"].int, revision: row["revision"].int,
              artwork: MusicNativePublicBrowseSupport.artwork(row["artwork"]),
              networkReference: .init(platform: MusicPublicBrowsePlatform(rawValue: row["platform"].string)!,
                                      remoteID: row["remoteID"].string, creator: row["creator"].string))
    }

    public func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection,
                                    intent: MusicQueueAuthorization) throws -> MusicCollection {
        try check(); try intent.check(); try requirePlatform(collection.platform.rawValue)
        let row = try MusicNativeNetworkPlaylists.row(collection)
        if let existing = try read().library["library"]["networkReferences"].array.first(where: {
            $0["id"] == row["id"] && $0["present"].bool
        }) { return try intent.perform { networkCollectionDTO(existing) } }
        let result = try intent.perform {
            try transaction(["library"]) { d -> (MusicCollection, Bool) in
                var library = d.library["library"], references = library["networkReferences"].array
                if let index = references.firstIndex(where: { $0["id"] == row["id"] }) {
                    var existing = references[index]
                    // Repeated collection is an idempotent relationship intent;
                    // display data is refreshed by the remote detail, not renamed here.
                    if existing["present"].bool { return (networkCollectionDTO(existing), false) }
                    existing["present"] = .bool(true)
                    existing["pending"] = .bool(MusicAccountPlatform(rawValue: existing["platform"].string) != nil)
                    existing["revision"] = .n(existing["revision"].int + 1)
                    references[index] = existing
                } else {
                    try musicRequire(library["collections"].array.count + references.count < 2000, .limit)
                    var added = row
                    // A removed unbound reference must not make an old handle current again.
                    added["revision"] = .n(library["revision"].int + 1)
                    references.append(added)
                }
                library["networkReferences"] = .array(references)
                library["revision"] = .n(library["revision"].int + 1)
                d.library["library"] = library; d.library["revision"] = .n(d.library["revision"].int + 1)
                let enabled = try MusicNativeSyncLedger.load(d.library).automatic.selections.contains {
                    $0.platform.rawValue == row["platform"].string && $0.playlists
                }
                if enabled {
                    try recordAutomaticLibraryMutation(&d, collectionID: row["id"].string,
                        relationshipPlatform: MusicAccountPlatform(rawValue: row["platform"].string)!)
                }
                return (networkCollectionDTO(references.first { $0["id"] == row["id"] }!), enabled)
            }
        }
        if result.1 { publishAutomaticLibrarySync(.localMutation) }
        return result.0
    }

    public func removeNetworkPlaylist(_ requested: MusicCollection, intent: MusicQueueAuthorization) throws {
        try check(); try intent.check()
        try musicRequire(requested.networkReference != nil, .invalidRequest)
        let enabled = try intent.perform {
            try transaction(["library"]) { d -> Bool in
                var library = d.library["library"], references = library["networkReferences"].array
                guard let index = references.firstIndex(where: { $0["id"].string == requested.id && $0["present"].bool }) else {
                    throw MusicServiceError.notFound
                }
                var row = references[index]
                try musicRequire(networkCollectionDTO(row) == requested, .revisionConflict)
                if library["accounts"][row["platform"].string] == .null {
                    // Local cancellation before binding cannot unsubscribe a future account.
                    references.remove(at: index)
                } else {
                    row["present"] = .bool(false)
                    row["pending"] = .bool(true)
                    row["revision"] = .n(row["revision"].int + 1); references[index] = row
                }
                library["networkReferences"] = .array(references)
                library["revision"] = .n(library["revision"].int + 1)
                d.library["library"] = library; d.library["revision"] = .n(d.library["revision"].int + 1)
                let enabled = try MusicNativeSyncLedger.load(d.library).automatic.selections.contains {
                    $0.platform.rawValue == row["platform"].string && $0.playlists
                }
                if enabled {
                    try recordAutomaticLibraryMutation(&d, collectionID: requested.id,
                        relationshipPlatform: MusicAccountPlatform(rawValue: row["platform"].string)!)
                }
                return enabled
            }
        }
        if enabled { publishAutomaticLibrarySync(.localMutation) }
    }
}
