// SPDX-License-Identifier: GPL-3.0-only
// Native port of music-core/library.js; local mutations originate in iWebPlus.
import Foundation
import MeowMusicFeature

enum MusicLibraryValidation {
    static func validateSong(_ song: MJ, id: String) throws {
        try musicRequire(UUID(uuidString: id) != nil && song["songId"].string.utf8.elementsEqual(id.utf8), .songIDConflict)
        try musicRequire(song.isObject && song["sources"].isArray, .corruptDocument)
        _ = try MusicModel.sources(song)
    }
}

extension NativeMusicService: MusicCollectionMembershipServing {
    func collection(_ id: String, _ d: MusicDocument) throws -> MJ {
        let library = d.library["library"]
        try musicRequire(library["collections"].isArray, .corruptDocument)
        let rows = library["collections"].array.filter { musicSame($0["id"].string, id) && !$0["deleted"].bool }
        guard let c = rows.first else { throw MusicServiceError.notFound }
        try musicRequire(rows.count == 1 && !id.isEmpty, .corruptDocument)
        try musicRequire(["favorites", "playlist"].contains(c["kind"].string), .corruptDocument)
        try musicRequire((id == "favorites") == (c["kind"].string == "favorites"), .favoritesProtected)
        try musicRequire(c["revision"].isInteger && c["order"].isArray && c["order"].array.allSatisfy { $0.isString && !$0.string.isEmpty }, .corruptDocument)
        try musicRequire(musicIDs(c["order"].strings).count == c["order"].array.count, .corruptDocument)
        return c
    }
    func collectionDTO(_ c: MJ) -> MusicCollection {
        .init(id: c["id"].string, title: c["name"].string, kind: c["kind"].string, count: c["order"].array.count, revision: c["revision"].int)
    }
    func upsert(_ input: [MJ], library: inout MJ) throws -> [String] {
        var seen = Set<String>()
        return try registerSongs(input, library: &library).filter { seen.insert($0).inserted }
    }
    func saveCollection(_ c: MJ, _ d: inout MusicDocument) throws {
        var library = d.library["library"], rows = library["collections"].array
        try musicRequire(c["revision"].isInteger, .limit)
        let positions = rows.indices.filter { musicSame(rows[$0]["id"].string, c["id"].string) }
        try musicRequire(positions.count <= 1, .corruptDocument)
        if let i = positions.first { rows[i] = c } else { rows.append(c) }
        library["collections"] = .array(rows); library["revision"] = .n(library["revision"].int + 1)
        d.library["library"] = library; d.library["revision"] = .n(d.library["revision"].int + 1)
    }
    public func collections() async throws -> [MusicCollection] {
        let state = try read(), library = state.library["library"]
        let rows = library["collections"].array.filter { !$0["deleted"].bool }
        let identities = Dictionary(grouping: rows, by: { Data($0["id"].string.utf8) })
        let managed: [MusicCollection] = try rows.filter {
            !$0["id"].string.isEmpty && identities[Data($0["id"].string.utf8)]?.count == 1 && $0["revision"].isInteger
        }.map { row in
            let first = try row["order"].strings.first.flatMap { try state.songRows.songData(id: $0) }
                .map { try MJ.parse($0) } ?? .null
            return .init(id: row["id"].string, title: row["name"].string, kind: row["kind"].string,
                count: row["order"].array.count, revision: row["revision"].int,
                artwork: MusicNativePublicBrowseSupport.artwork(first["cover_url"]))
        }
        let references = library["networkReferences"].array.filter { $0["present"].bool }
        let network = references.compactMap { try? networkCollectionDTO($0) }
        let values = managed + network
        let ids = Dictionary(grouping: values, by: { Data($0.id.utf8) })
        let visible = values.filter { ids[Data($0.id.utf8)]?.count == 1 }
        let empty = library["collections"].isArray && library["networkReferences"].isArray && rows.isEmpty &&
            library["networkReferences"].array.allSatisfy { $0["present"] == .bool(false) }
        try musicRequire(empty || !visible.isEmpty, .corruptDocument)
        return visible
    }
    public func tracks(in requested: MusicCollection) async throws -> [MusicRemoteTrack] {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let d = try read(), c = try collection(requested.id, d)
        try musicRequire(c["revision"].int == requested.revision, .staleRevision)
        let ids = c["order"].strings
        return try ids.map { try MusicModel.track(repositorySong($0, d), .init(kind: .library, id: $0)) }
    }
    public func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        _ = try musicText(name)
        let result = try transaction(["library"]) { d in
            var lib = d.library["library"]
            try musicRequire(lib["nextId"].isInteger && lib["nextId"].int > 0 && lib["collections"].isArray, .corruptDocument)
            try musicRequire(lib["nextId"].int < 9_007_199_254_740_991, .limit)
            let newID = "playlist:\(lib["nextId"].int)"
            try musicRequire(!lib["collections"].array.contains { $0["id"].string == newID }, .corruptDocument)
            let ids = try collectionSongIDs(refs, state: d)
            var c = MusicModel.freshCollection("playlist:\(lib["nextId"].int)", name)
            lib["nextId"] = .n(lib["nextId"].int + 1)
            c["order"] = .strings(ids); c["manualOrder"] = .bool(true); c["locallyCreated"] = .bool(true); c["revision"] = .n(1)
            c["intents"] = .obj(ids.map { ($0, .obj([("present", .bool(true)), ("pending", .bool(true)), ("origin", .string("local"))])) })
            d.library["library"] = lib; try saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string)
            return collectionDTO(c)
        }
        publishAutomaticLibrarySync(.localMutation)
        return result
    }
    public func editPlaylist(_ requested: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let result = try transaction(["library"]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            var next = try refs.map { try collectionSongIDs($0, state: d) } ?? c["order"].strings
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
        try musicRequire(c["revision"].int == requested.revision, .staleRevision)
        return c["order"].strings.map { .init(kind: .library, id: $0) }
    }
    public func changeCollectionMembership(_ requested: MusicCollection, adding: [MusicReference],
                                           removing: [MusicReference]) async throws -> MusicCollection {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let result = try transaction(["library"]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            let added = try collectionSongIDs(adding, state: d)
            let removed = Set(try collectionSongIDs(removing, state: d).map { Data($0.utf8) })
            try musicRequire(removed.isDisjoint(with: added.map { Data($0.utf8) }))
            let previous = c["order"].strings
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
    private func collectionSongIDs(_ refs: [MusicReference], state: MusicDocument) throws -> [String] {
        var seen = Set<Data>()
        return try refs.compactMap { ref in
            let id: String
            switch ref.kind {
            case .library: id = ref.id
            case .queue:
                try musicRequire(state.queue["entries"].isArray, .corruptDocument)
                let entries = state.queue["entries"].array.filter { musicSame($0["id"].string, ref.id) }
                guard let entry = entries.first else { throw MusicServiceError.notFound }
                try musicRequire(entries.count == 1, .corruptDocument)
                id = entry["songId"].string
            }
            guard seen.insert(Data(id.utf8)).inserted else { return nil }
            try musicRequire(UUID(uuidString: id) != nil, .songIDConflict)
            let exists = try (activeSongWriter as (any MusicNativeLibraryReading)? ?? repositoryReader).containsSong(id: id)
            try musicRequire(exists, .notFound)
            return id
        }
    }
    private func finishCollectionEdit(_ c: inout MJ, next: [String], name: String?, state d: inout MusicDocument) throws {
        let previous = c["order"].strings
        if let name {
            try musicRequire(c["kind"].string != "favorites", .favoritesProtected)
            _ = try musicText(name)
            c["name"] = .string(name)
        }
        let before = Set(previous.map { Data($0.utf8) }), after = Set(next.map { Data($0.utf8) })
        let changed = before.symmetricDifference(after)
        if !changed.isEmpty {
            let intents = c["intents"], bindings = c["bindings"]
            try musicRequire(intents.isObject && bindings.isArray, .corruptDocument)
            func intent(_ present: Bool) -> MJ {
                .obj([("present", .bool(present)), ("pending", .bool(true)), ("origin", .string("local"))])
            }
            // Rebuild the ordered object once. Updating an array-backed MJ member for
            // every added song scans and copies the growing object for each song.
            var remaining = changed
            var fields: [(String, MJ)] = []
            for (id, value) in intents.fields {
                let key = Data(id.utf8)
                guard remaining.remove(key) != nil else { fields.append((id, value)); continue }
                if after.contains(key) || !bindings.array.isEmpty { fields.append((id, intent(after.contains(key)))) }
            }
            for id in previous + next where remaining.remove(Data(id.utf8)) != nil {
                let present = after.contains(Data(id.utf8))
                if present || !bindings.array.isEmpty { fields.append((id, intent(present))) }
            }
            c["intents"] = .obj(fields)
        }
        c["order"] = .strings(next); c["manualOrder"] = .bool(true)
        c["revision"] = .n(c["revision"].int + 1); try saveCollection(c, &d)
        try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string, orderOnly: before == after)
    }
    public func removePlaylist(_ requested: MusicCollection) async throws {
        try musicRequire(requested.networkReference == nil, .unsupported)
        try transaction(["library"]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c["kind"].string != "favorites", .favoritesProtected)
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            c["deleted"] = .bool(true); c["revision"] = .n(requested.revision + 1); try saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string)
        }
        publishAutomaticLibrarySync(.localMutation)
    }
}
