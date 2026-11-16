// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

enum MusicNativeNetworkPlaylists {
    static func id(platform: String, remoteID: String) -> String {
        "network:" + MJ.strings([platform, remoteID]).json
    }
    static func row(_ collection: MusicPublicBrowseCollection) throws -> MJ {
        try musicRequire(collection.kind == .playlist, .unsupported)
        try musicRequire(!collection.nativeID.isEmpty)
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
}

extension NativeMusicService: MusicNetworkPlaylistServing {
    func networkCollectionDTO(_ row: MJ) throws -> MusicCollection {
        guard let platform = MusicPublicBrowsePlatform(rawValue: row["platform"].string),
              !row["remoteID"].string.isEmpty,
              row["id"].string == MusicNativeNetworkPlaylists.id(platform: platform.rawValue, remoteID: row["remoteID"].string),
              row["revision"].isInteger else { throw MusicServiceError.corruptDocument }
        return .init(id: row["id"].string, title: row["name"].string, kind: "network",
              count: row["count"].int, revision: row["revision"].int,
              artwork: MusicNativePublicBrowseSupport.artwork(row["artwork"]),
              networkReference: .init(platform: platform,
                                      remoteID: row["remoteID"].string, creator: row["creator"].string))
    }

    public func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection,
                                    intent: MusicQueueAuthorization) throws -> MusicCollection {
        try check(); try intent.check(); try requirePlatform(collection.platform.rawValue)
        let row = try MusicNativeNetworkPlaylists.row(collection)
        let current = try read().library["library"]["networkReferences"]
        try musicRequire(current.isArray, .corruptDocument)
        let matches = current.array.filter { $0["id"] == row["id"] }
        try musicRequire(matches.count <= 1, .corruptDocument)
        if let existing = matches.first, existing["present"].bool {
            return try intent.perform { try networkCollectionDTO(existing) }
        }
        let result = try intent.perform {
            try transaction(["library"]) { d -> (MusicCollection, Bool) in
                var library = d.library["library"], references = library["networkReferences"].array
                try musicRequire(library["networkReferences"].isArray && references.filter { $0["id"] == row["id"] }.count <= 1, .corruptDocument)
                if let index = references.firstIndex(where: { $0["id"] == row["id"] }) {
                    var existing = references[index]
                    _ = try networkCollectionDTO(existing)
                    // Repeated collection is an idempotent relationship intent;
                    // display data is refreshed by the remote detail, not renamed here.
                    if existing["present"].bool { return (try networkCollectionDTO(existing), false) }
                    existing["present"] = .bool(true)
                    existing["pending"] = .bool(MusicAccountPlatform(rawValue: existing["platform"].string) != nil)
                    existing["revision"] = .n(existing["revision"].int + 1)
                    references[index] = existing
                } else {
                    var added = row
                    // A removed unbound reference must not make an old handle current again.
                    added["revision"] = .n(library["revision"].int + 1)
                    references.append(added)
                }
                library["networkReferences"] = .array(references)
                library["revision"] = .n(library["revision"].int + 1)
                d.library["library"] = library; d.library["revision"] = .n(d.library["revision"].int + 1)
                let platform = MusicAccountPlatform(rawValue: row["platform"].string)
                let enabled = platform != nil && d.library["sync"]["automatic"]["selections"].array.contains {
                    $0["platform"] == row["platform"] && $0["playlists"].bool
                }
                if enabled, let platform {
                    try recordAutomaticLibraryMutation(&d, collectionID: row["id"].string,
                        relationshipPlatform: platform)
                }
                return (try networkCollectionDTO(references.first { $0["id"] == row["id"] }!), enabled)
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
                try musicRequire(library["networkReferences"].isArray && references.filter { $0["id"].string == requested.id }.count <= 1, .corruptDocument)
                guard let index = references.firstIndex(where: { $0["id"].string == requested.id && $0["present"].bool }) else {
                    throw MusicServiceError.notFound
                }
                var row = references[index]
                try musicRequire(try networkCollectionDTO(row) == requested, .revisionConflict)
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
                let platform = MusicAccountPlatform(rawValue: row["platform"].string)
                let enabled = platform != nil && d.library["sync"]["automatic"]["selections"].array.contains {
                    $0["platform"] == row["platform"] && $0["playlists"].bool
                }
                if enabled, let platform {
                    try recordAutomaticLibraryMutation(&d, collectionID: requested.id,
                        relationshipPlatform: platform)
                }
                return enabled
            }
        }
        if enabled { publishAutomaticLibrarySync(.localMutation) }
    }
}
