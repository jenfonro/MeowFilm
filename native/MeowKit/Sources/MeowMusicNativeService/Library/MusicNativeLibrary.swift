// SPDX-License-Identifier: GPL-3.0-only
// Native port of music-core/library.js; local mutations originate in iWebPlus.
import Foundation
import MeowMusicFeature

enum MusicLibraryValidation {
    static func validateSong(_ song: MJ, id: String) throws {
        try musicRequire(UUID(uuidString: id) != nil && song["songId"].string.utf8.elementsEqual(id.utf8), .songIDConflict)
        try musicRequire(song.isObject && song["sources"].isArray, .corruptDocument)
        let clean = try MusicModel.cleanSong(song)
        var keys = Set<String>()
        for (raw, source) in zip(song["sources"].array, clean["sources"].array) {
            try musicRequire(raw["sourceKey"] == source["sourceKey"] && keys.insert(source["sourceKey"].string).inserted, .nativeIDConflict)
        }
    }
    static func validate(_ lib: MJ, owner: String, containsSong: (String) throws -> Bool) throws {
        try lib.requireFields(["version", "ownerId", "revision", "nextId", "collections", "networkReferences", "accounts", "status"])
        try musicRequire(lib["version"] == .n(2) && !owner.isEmpty && musicSame(lib["ownerId"].string, owner) &&
            lib["revision"].isInteger && lib["nextId"].isInteger && lib["nextId"].int > 0 &&
            lib["collections"].isArray && lib["networkReferences"].isArray && lib["accounts"].isObject, .corruptDocument)
        var collectionIDs = Set<Data>(), remoteBindings = Set<String>()
        for c in lib["collections"].array {
            try c.requireFields(["id", "kind", "name", "order", "revision", "manualOrder", "bindings", "intents", "deleted", "locallyCreated"])
            let id = try c["id"].text(), ids = c["order"].strings
            let validReferences = try ids.allSatisfy { try containsSong($0) }
            try musicRequire(c["name"].isString && collectionIDs.insert(Data(id.utf8)).inserted && ["favorites", "playlist"].contains(c["kind"].string) &&
                c["revision"].isInteger && c["order"].isArray && musicIDs(ids).count == ids.count &&
                validReferences && c["bindings"].isArray && c["intents"].isObject &&
                c["manualOrder"].isBool, .corruptDocument)
            try musicRequire(c["kind"].string != "favorites" || id == "favorites" && !c["deleted"].bool, .favoritesProtected)
            for key in ["deleted", "locallyCreated"] { try musicRequire(!c.contains(key) || c[key].isBool, .corruptDocument) }
            if id.hasPrefix("playlist:"), let n = Int(id.dropFirst(9)) { try musicRequire(n < lib["nextId"].int, .corruptDocument) }
            var bindings = Set<String>()
            for b in c["bindings"].array {
                try b.requireFields(["platform", "accountId", "id", "writable", "baseline", "map", "name"])
                let key = MJ.array([b["platform"], b["accountId"], b["id"]]).json
                try musicRequire(b["accountId"].isString && !b["accountId"].string.isEmpty, .corruptDocument)
                try musicRequire(b["id"].isString && !b["id"].string.isEmpty, .corruptDocument)
                try musicRequire(MusicModel.sites.contains(b["platform"].string) && lib["accounts"][b["platform"].string] == b["accountId"] &&
                    bindings.insert(key).inserted && remoteBindings.insert(key).inserted && b["baseline"].isArray &&
                    b["baseline"].array.allSatisfy { $0.isString && !$0.string.isEmpty } &&
                    Set(b["baseline"].strings).count == b["baseline"].array.count && b["map"].isObject &&
                    b["writable"] == .bool(true) && b["name"].isString, .corruptDocument)
                for (_, songID) in b["map"].fields {
                    _ = try songID.text()
                    let exists = try containsSong(songID.string)
                    try musicRequire(exists, .corruptDocument)
                }
            }
            for (id, intent) in c["intents"].fields {
                try intent.requireFields(["present", "pending", "origin", "acknowledged"])
                let exists = try containsSong(id)
                try musicRequire(exists && intent["present"].isBool &&
                    intent["pending"].isBool && ["local", "remote"].contains(intent["origin"].string), .corruptDocument)
                if intent.contains("acknowledged") {
                    try musicRequire(intent["acknowledged"].isArray && intent["acknowledged"].strings.allSatisfy(bindings.contains), .corruptDocument)
                }
            }
        }
        try musicRequire(lib["collections"].array.filter { $0["kind"].string == "favorites" }.count == 1, .favoritesProtected)
        try MusicNativeNetworkPlaylists.validate(lib)
        for (platform, id) in lib["accounts"].fields {
            try musicRequire(MusicModel.sites.contains(platform) && id.isString && !id.string.isEmpty, .corruptDocument)
        }
    }
}

extension NativeMusicService: MusicCollectionMembershipServing {
    func collection(_ id: String, _ d: MusicDocument) throws -> MJ {
        guard let c = d.library["library"]["collections"].array.first(where: { musicSame($0["id"].string, id) && !$0["deleted"].bool }) else {
            throw MusicServiceError.notFound
        }
        return c
    }
    func collectionDTO(_ c: MJ) -> MusicCollection {
        .init(id: c["id"].string, title: c["name"].string, kind: c["kind"].string, count: c["order"].array.count, revision: c["revision"].int)
    }
    func upsert(_ input: [MJ], library: inout MJ) throws -> [String] {
        var seen = Set<String>()
        return try registerSongs(input, library: &library).filter { seen.insert($0).inserted }
    }
    func saveCollection(_ c: MJ, _ d: inout MusicDocument) {
        var library = d.library["library"], rows = library["collections"].array
        var positions: [Data: Int] = [:]
        for (offset, row) in rows.enumerated() { positions[Data(row["id"].string.utf8)] = offset }
        if let i = positions[Data(c["id"].string.utf8)] { rows[i] = c } else { rows.append(c) }
        library["collections"] = .array(rows); library["revision"] = .n(library["revision"].int + 1)
        d.library["library"] = library; d.library["revision"] = .n(d.library["revision"].int + 1)
    }
    public func collections() async throws -> [MusicCollection] {
        let state = try read(), library = state.library["library"]
        let managed: [MusicCollection] = try library["collections"].array.filter { !$0["deleted"].bool }.map { row in
            let first = try row["order"].strings.first.map { try repositorySong($0, state) } ?? .null
            return .init(id: row["id"].string, title: row["name"].string, kind: row["kind"].string,
                count: row["order"].array.count, revision: row["revision"].int,
                artwork: MusicNativePublicBrowseSupport.artwork(first["cover_url"]))
        }
        return managed + library["networkReferences"].array.filter { $0["present"].bool }.map(networkCollectionDTO)
    }
    public func tracks(in requested: MusicCollection) async throws -> [MusicRemoteTrack] {
        try musicRequire(requested.networkReference == nil, .unsupported)
        let d = try read(), c = try collection(requested.id, d)
        try musicRequire(c["revision"].int == requested.revision, .staleRevision)
        let ids = c["order"].strings
        return try ids.map { try MusicModel.track(repositorySong($0, d), .init(kind: .library, id: $0)) }
    }
    public func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        _ = try musicText(name, maximum: 600); try musicRequire(name.utf16.count <= 200)
        let result = try transaction(["library"]) { d in
            var lib = d.library["library"]
            let ids = try collectionSongIDs(refs, state: d)
            var c = MusicModel.freshCollection("playlist:\(lib["nextId"].int)", name)
            lib["nextId"] = .n(lib["nextId"].int + 1)
            c["order"] = .strings(ids); c["manualOrder"] = .bool(true); c["locallyCreated"] = .bool(true); c["revision"] = .n(1)
            c["intents"] = .obj(ids.map { ($0, .obj([("present", .bool(true)), ("pending", .bool(true)), ("origin", .string("local"))])) })
            d.library["library"] = lib; saveCollection(c, &d)
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
                guard let entry = state.queue["entries"].array.first(where: { musicSame($0["id"].string, ref.id) }) else {
                    throw MusicServiceError.notFound
                }
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
            _ = try musicText(name, maximum: 600); try musicRequire(name.utf16.count <= 200)
            c["name"] = .string(name)
        }
        let before = Set(previous.map { Data($0.utf8) }), after = Set(next.map { Data($0.utf8) })
        var intents = c["intents"]
        for id in previous + next where before.contains(Data(id.utf8)) != after.contains(Data(id.utf8)) {
            let present = after.contains(Data(id.utf8))
            // An unbound local removal must not become a future account's delete.
            if !present, c["bindings"].array.isEmpty { intents.remove(id) }
            else { intents[id] = .obj([("present", .bool(present)), ("pending", .bool(true)), ("origin", .string("local"))]) }
        }
        c["intents"] = intents; c["order"] = .strings(next); c["manualOrder"] = .bool(true)
        c["revision"] = .n(c["revision"].int + 1); saveCollection(c, &d)
        try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string, orderOnly: before == after)
    }
    public func removePlaylist(_ requested: MusicCollection) async throws {
        try musicRequire(requested.networkReference == nil, .unsupported)
        try transaction(["library"]) { d in
            var c = try collection(requested.id, d)
            try musicRequire(c["kind"].string != "favorites", .favoritesProtected)
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            c["deleted"] = .bool(true); c["revision"] = .n(requested.revision + 1); saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string)
        }
        publishAutomaticLibrarySync(.localMutation)
    }
}
