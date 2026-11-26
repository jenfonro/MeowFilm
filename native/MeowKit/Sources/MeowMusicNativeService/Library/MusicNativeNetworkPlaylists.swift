// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

enum MusicNativeNetworkPlaylists {
    static func id(platform: String, remoteID: String) -> String {
        "network:" + MusicWireCodec.canonicalStrings([platform, remoteID])
    }

    static func row(_ collection: MusicPublicBrowseCollection) throws -> MusicNetworkReferenceRecord {
        try musicRequire(collection.kind == .playlist, .unsupported)
        try musicRequire(!collection.nativeID.isEmpty)
        try musicRequire(collection.count == nil || collection.count! >= 0, .invalidRequest)
        return MusicNetworkReferenceRecord(
            id: id(platform: collection.platform.rawValue, remoteID: collection.nativeID),
            platform: collection.platform.rawValue,
            remoteID: collection.nativeID,
            name: collection.title,
            creator: collection.creator,
            artwork: MusicModel.publicURLString(collection.artwork?.absoluteString ?? ""),
            count: collection.count ?? 0,
            revision: 1,
            present: true,
            pending: MusicAccountPlatform(rawValue: collection.platform.rawValue) != nil,
            relation: nil
        )
    }
}

extension NativeMusicService {
    func networkCollectionDTO(_ row: MusicNetworkReferenceRecord) throws -> MusicCollection {
        guard let platform = MusicPublicBrowsePlatform(rawValue: row.platform),
              !row.remoteID.isEmpty else { throw MusicServiceError.corruptStorage }
        return .init(
            id: row.id,
            title: row.name,
            kind: "network",
            count: row.count,
            revision: row.revision,
            artwork: MusicModel.artworkURL(row.artwork),
            networkReference: .init(platform: platform, remoteID: row.remoteID, creator: row.creator)
        )
    }

    public func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection,
                                    intent: MusicQueueAuthorization) throws -> MusicCollection {
        try check(); try intent.check(); try requirePlatform(collection.platform.rawValue)
        let row = try MusicNativeNetworkPlaylists.row(collection)
        let result = try intent.perform {
            try transaction([.library]) { d -> (MusicCollection, Bool) in
                if let index = d.library.networkReferences.firstIndex(where: { $0.id == row.id }) {
                    var existing = d.library.networkReferences[index]
                    if existing.present { return (try networkCollectionDTO(existing), false) }
                    existing.present = true
                    existing.pending = MusicAccountPlatform(rawValue: existing.platform) != nil
                    existing.revision += 1
                    d.library.networkReferences[index] = existing
                } else {
                    var added = row
                    added.revision = d.library.libraryRevision + 1
                    d.library.networkReferences.append(added)
                }
                d.library.libraryRevision += 1
                d.library.revision += 1
                let platform = MusicAccountPlatform(rawValue: row.platform)
                let enabled = platform != nil && (d.library.sync?.automaticSelections.contains {
                    $0.platform == row.platform && $0.playlists
                } ?? false)
                if enabled, let platform {
                    try recordAutomaticLibraryMutation(&d, collectionID: row.id, relationshipPlatform: platform)
                }
                let saved = d.library.networkReferences.first { $0.id == row.id }!
                return (try networkCollectionDTO(saved), enabled)
            }
        }
        if result.1 { publishAutomaticLibrarySync(.localMutation) }
        return result.0
    }

    public func removeNetworkPlaylist(_ requested: MusicCollection, intent: MusicQueueAuthorization) throws {
        try check(); try intent.check()
        try musicRequire(requested.networkReference != nil, .invalidRequest)
        let enabled = try intent.perform {
            try transaction([.library]) { d -> Bool in
                guard let index = d.library.networkReferences.firstIndex(where: { $0.id == requested.id && $0.present }) else {
                    throw MusicServiceError.notFound
                }
                var row = d.library.networkReferences[index]
                try musicRequire(try networkCollectionDTO(row) == requested, .revisionConflict)
                if d.library.accountID(for: row.platform) == nil {
                    d.library.networkReferences.remove(at: index)
                } else {
                    row.present = false
                    row.pending = true
                    row.revision += 1
                    d.library.networkReferences[index] = row
                }
                d.library.libraryRevision += 1
                d.library.revision += 1
                let platform = MusicAccountPlatform(rawValue: row.platform)
                let enabled = platform != nil && (d.library.sync?.automaticSelections.contains {
                    $0.platform == row.platform && $0.playlists
                } ?? false)
                if enabled, let platform {
                    try recordAutomaticLibraryMutation(&d, collectionID: requested.id, relationshipPlatform: platform)
                }
                return enabled
            }
        }
        if enabled { publishAutomaticLibrarySync(.localMutation) }
    }
}
