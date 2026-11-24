// SPDX-License-Identifier: GPL-3.0-only
// Native port of music-core/library.js; local mutations originate in iWebPlus.
import Foundation
import MeowMusicFeature

enum MusicLibraryValidation {
    static func validateSong(_ song: MusicSongRecord, id: String) throws {
        try musicRequire(UUID(uuidString: id) != nil && song.songID.utf8.elementsEqual(id.utf8), .songIDConflict)
        _ = try MusicModel.sources(song)
    }
}

extension NativeMusicService: MusicCollectionMembershipServing {
    func collection(_ id: String, _ d: MusicDomainState) throws -> MusicCollectionRecord {
        let rows = d.library.collections.filter { musicSame($0.id, id) && !$0.deleted }
        guard let c = rows.first else { throw MusicServiceError.notFound }
        try musicRequire(rows.count == 1 && !id.isEmpty, .corruptStorage)
        try musicRequire(["favorites", "playlist"].contains(c.kind), .corruptStorage)
        try musicRequire((id == "favorites") == (c.kind == "favorites"), .favoritesProtected)
        try musicRequire((0...musicMaxSafeInteger).contains(c.revision) && c.order.allSatisfy { !$0.isEmpty }, .corruptStorage)
        try musicRequire(musicIDs(c.order).count == c.order.count, .corruptStorage)
        return c
    }

    func collectionDTO(_ c: MusicCollectionRecord) -> MusicCollection {
        .init(id: c.id, title: c.name, kind: c.kind, count: c.order.count, revision: c.revision)
    }

    func upsert(_ input: [MusicSongRecord], library: inout MusicLibraryState) throws -> [String] {
        var seen = Set<String>()
        return try registerSongs(input, library: &library).filter { seen.insert($0).inserted }
    }

    func saveCollection(_ c: MusicCollectionRecord, _ d: inout MusicDomainState) throws {
        try musicRequire((0...musicMaxSafeInteger).contains(c.revision), .limit)
        let positions = d.library.collections.indices.filter { musicSame(d.library.collections[$0].id, c.id) }
        try musicRequire(positions.count <= 1, .corruptStorage)
        if let i = positions.first { d.library.collections[i] = c } else { d.library.collections.append(c) }
        d.library.libraryRevision += 1
        d.library.revision += 1
    }

    public func collections() async throws -> [MusicCollection] {
        let state = try read()
        let rows = state.library.collections.filter { !$0.deleted }
        let identities = Dictionary(grouping: rows, by: { Data($0.id.utf8) })
        let validRows = rows.filter {
            !$0.id.isEmpty && identities[Data($0.id.utf8)]?.count == 1 && (0...musicMaxSafeInteger).contains($0.revision)
        }
        let coverSongIDs = Set(validRows.compactMap(\.order.first).filter { !$0.isEmpty })
        let coverSongs = coverSongIDs.isEmpty ? [:] : try state.songRows.songs(ids: coverSongIDs)
        let managed: [MusicCollection] = validRows.map { row in
            let first = row.order.first.flatMap { coverSongs[$0] }
            return .init(
                id: row.id,
                title: row.name,
                kind: row.kind,
                count: row.order.count,
                revision: row.revision,
                artwork: first.flatMap { MusicModel.artworkURL($0.coverURL) }
            )
        }
        let references = state.library.networkReferences.filter(\.present)
        let network = references.compactMap { try? networkCollectionDTO($0) }
        let values = managed + network
        let ids = Dictionary(grouping: values, by: { Data($0.id.utf8) })
        let visible = values.filter { ids[Data($0.id.utf8)]?.count == 1 }
        let empty = rows.isEmpty && state.library.networkReferences.allSatisfy { !$0.present }
        try musicRequire(empty || !visible.isEmpty, .corruptStorage)
        return visible
    }

    public func tracks(in requested: MusicCollection) async throws -> [MusicRemoteTrack] {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let d = try read(), c = try collection(requested.id, d)
        try musicRequire(c.revision == requested.revision, .staleRevision)
        let songsByID = try d.projectSongs(c.order)
        return try c.order.map { id in
            guard let song = songsByID[id] else { throw MusicServiceError.notFound }
            return try MusicModel.track(song, .init(kind: .library, id: id))
        }
    }

    public func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        _ = try musicText(name)
        let result = try transaction([.library]) { d in
            try musicRequire(d.library.nextID > 0 && d.library.nextID <= musicMaxSafeInteger, .corruptStorage)
            try musicRequire(d.library.nextID < musicMaxSafeInteger, .limit)
            let newID = "playlist:\(d.library.nextID)"
            try musicRequire(!d.library.collections.contains { $0.id == newID }, .corruptStorage)
            let ids = try collectionSongIDs(refs, state: d)
            var c = MusicModel.freshCollection(newID, name)
            d.library.nextID += 1
            c.order = ids
            c.manualOrder = true
            c.locallyCreated = true
            c.revision = 1
            c.intents = ids.map {
                MusicCollectionIntentRecord(intentKey: $0, present: true, pending: true, origin: "local", acknowledged: [])
            }.sorted { $0.intentKey < $1.intentKey }
            try saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c.id)
            return collectionDTO(c)
        }
        publishAutomaticLibrarySync(.localMutation)
        return result
    }

    public func editPlaylist(_ requested: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let result = try transaction([.library]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c.revision == requested.revision, .revisionConflict)
            var next = try refs.map { try collectionSongIDs($0, state: d) } ?? c.order
            if let order {
                try musicRequire(order.count == next.count && musicIDs(order).count == order.count && musicIDs(order) == musicIDs(next))
                next = order
            }
            try finishCollectionEdit(&c, next: next, name: name, state: &d)
            return collectionDTO(c)
        }
        publishAutomaticLibrarySync(.localMutation)
        return result
    }

    public func collectionReferences(in requested: MusicCollection) async throws -> [MusicReference] {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let d = try read(), c = try collection(requested.id, d)
        try musicRequire(c.revision == requested.revision, .staleRevision)
        return c.order.map { .init(kind: .library, id: $0) }
    }

    public func changeCollectionMembership(_ requested: MusicCollection, adding: [MusicReference],
                                           removing: [MusicReference]) async throws -> MusicCollection {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let result = try transaction([.library]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c.revision == requested.revision, .revisionConflict)
            let added = try collectionSongIDs(adding, state: d)
            let removed = Set(try collectionSongIDs(removing, state: d).map { Data($0.utf8) })
            try musicRequire(removed.isDisjoint(with: added.map { Data($0.utf8) }))
            let previous = c.order
            var next = previous.filter { !removed.contains(Data($0.utf8)) }
            var seen = Set(next.map { Data($0.utf8) })
            next += added.filter { seen.insert(Data($0.utf8)).inserted }
            guard next != previous else { return collectionDTO(c) }
            try finishCollectionEdit(&c, next: next, name: nil, state: &d)
            return collectionDTO(c)
        }
        publishAutomaticLibrarySync(.localMutation)
        return result
    }

    private func collectionSongIDs(_ refs: [MusicReference], state: MusicDomainState) throws -> [String] {
        var seen = Set<Data>()
        var ids: [String] = []
        for ref in refs {
            let id: String
            switch ref.kind {
            case .library:
                id = ref.id
            case .queue:
                let entries = state.queue.entries.filter { musicSame($0.id, ref.id) }
                guard let entry = entries.first else { throw MusicServiceError.notFound }
                try musicRequire(entries.count == 1, .corruptStorage)
                id = entry.songID
            }
            guard seen.insert(Data(id.utf8)).inserted else { continue }
            try musicRequire(UUID(uuidString: id) != nil, .songIDConflict)
            ids.append(id)
        }
        if !ids.isEmpty {
            let reader: any MusicNativeLibraryReading = activeSongWriter ?? repositoryReader
            let existing = try reader.containsSongs(ids: Set(ids))
            for id in ids {
                try musicRequire(existing.contains(id), .notFound)
            }
        }
        return ids
    }

    private func finishCollectionEdit(_ c: inout MusicCollectionRecord, next: [String], name: String?, state d: inout MusicDomainState) throws {
        let previous = c.order
        if let name {
            try musicRequire(c.kind != "favorites", .favoritesProtected)
            _ = try musicText(name)
            c.name = name
        }
        let before = Set(previous.map { Data($0.utf8) }), after = Set(next.map { Data($0.utf8) })
        let changed = before.symmetricDifference(after)
        if !changed.isEmpty {
            func intent(_ key: String, _ present: Bool) -> MusicCollectionIntentRecord {
                MusicCollectionIntentRecord(intentKey: key, present: present, pending: true, origin: "local", acknowledged: [])
            }
            var remaining = changed
            var updated: [MusicCollectionIntentRecord] = []
            for row in c.intents {
                let key = Data(row.intentKey.utf8)
                guard remaining.remove(key) != nil else { updated.append(row); continue }
                if after.contains(key) || !c.bindings.isEmpty {
                    updated.append(intent(row.intentKey, after.contains(key)))
                }
            }
            for id in previous + next where remaining.remove(Data(id.utf8)) != nil {
                let present = after.contains(Data(id.utf8))
                if present || !c.bindings.isEmpty {
                    updated.append(intent(id, present))
                }
            }
            updated.sort { $0.intentKey < $1.intentKey }
            c.intents = updated
        }
        c.order = next
        c.manualOrder = true
        c.revision += 1
        try saveCollection(c, &d)
        try recordAutomaticLibraryMutation(&d, collectionID: c.id, orderOnly: before == after)
    }

    public func removePlaylist(_ requested: MusicCollection) async throws {
        try musicRequire(requested.networkReference == nil, .unsupported)
        try transaction([.library]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c.kind != "favorites", .favoritesProtected)
            try musicRequire(c.revision == requested.revision, .revisionConflict)
            c.deleted = true
            c.revision = requested.revision + 1
            try saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c.id)
        }
        publishAutomaticLibrarySync(.localMutation)
    }
}
