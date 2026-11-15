// SPDX-License-Identifier: GPL-3.0-only
// Native port of music-core/library.js; local mutations originate in iWebPlus.
import Foundation
import MeowMusicFeature

enum MusicLibraryValidation {
    static func validate(_ lib: MJ, owner: String) throws {
        try lib.requireFields(["version", "ownerId", "revision", "nextId", "collections", "networkReferences", "songs", "accounts", "status"])
        try musicRequire(lib["version"] == .n(2) && !owner.isEmpty && musicSame(lib["ownerId"].string, owner) &&
            lib["revision"].isInteger && lib["nextId"].isInteger && lib["nextId"].int > 0 &&
            lib["collections"].isArray && lib["networkReferences"].isArray &&
            lib["songs"].isObject && lib["accounts"].isObject, .corruptDocument)
        var nativeOwners: [String: String] = [:], collectionIDs = Set<Data>(), remoteBindings = Set<String>()
        let knownSongs = musicIDs(lib["songs"].fields.map(\.0))
        for (id, song) in lib["songs"].fields {
            try musicRequire(UUID(uuidString: id) != nil &&
                song["songId"].string.utf8.elementsEqual(id.utf8), .songIDConflict)
            try musicRequire(song.isObject && song["sources"].isArray, .corruptDocument)
            let clean = try MusicModel.cleanSong(song)
            var seenSources = Set<String>()
            for (rawSource, source) in zip(song["sources"].array, clean["sources"].array) {
                let key = source["sourceKey"].string
                try musicRequire(rawSource["sourceKey"] == source["sourceKey"] &&
                    seenSources.insert(key).inserted &&
                    (nativeOwners[key] == nil || musicSame(nativeOwners[key]!, id)), .nativeIDConflict)
                nativeOwners[key] = id
            }
        }
        for c in lib["collections"].array {
            try c.requireFields(["id", "kind", "name", "order", "revision", "manualOrder", "bindings", "intents", "deleted", "locallyCreated"])
            let id = try c["id"].text(), ids = c["order"].strings
            try musicRequire(c["name"].isString && collectionIDs.insert(Data(id.utf8)).inserted && ["favorites", "playlist"].contains(c["kind"].string) &&
                c["revision"].isInteger && c["order"].isArray && musicIDs(ids).count == ids.count &&
                ids.allSatisfy { knownSongs.contains(Data($0.utf8)) } && c["bindings"].isArray && c["intents"].isObject &&
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
                    try musicRequire(knownSongs.contains(Data(songID.string.utf8)), .corruptDocument)
                }
            }
            for (id, intent) in c["intents"].fields {
                try intent.requireFields(["present", "pending", "origin", "acknowledged"])
                try musicRequire(knownSongs.contains(Data(id.utf8)) && intent["present"].isBool &&
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

extension NativeMusicService {
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
        let library = try read().library["library"]
        let managed: [MusicCollection] = library["collections"].array.filter { !$0["deleted"].bool }.map { row in
            let first = row["order"].strings.first.map { library["songs"][$0] } ?? .null
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
        let songs = try await sharedSongMetadata(ids.map { d.library["library"]["songs"][$0] })
        try musicRequire(try collection(requested.id, read())["revision"].int == requested.revision, .staleRevision)
        return try zip(ids, songs).map { try MusicModel.track($0.1, .init(kind: .library, id: $0.0)) }
    }
    public func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        _ = try musicText(name, maximum: 600); try musicRequire(name.utf16.count <= 200)
        let before = try read()
        let originals = try refs.map { try song($0, before) }
        let acquired = try await sharedSongMetadata(originals)
        let result = try transaction(["library"]) { d in
            var lib = d.library["library"]
            let ids = try upsert(acquired, library: &lib)
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
        let acquired: [MJ]?
        if let refs {
            let before = try read()
            let songs = try refs.map { try song($0, before) }
            acquired = try await sharedSongMetadata(songs)
        } else { acquired = nil }
        let result = try transaction(["library"]) { d in
            var c = try collection(requested.id, d), lib = d.library["library"]
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            let previous = c["order"].strings
            var next = try acquired.map { try upsert($0, library: &lib) } ?? previous
            if let order {
                try musicRequire(order.count == next.count && musicIDs(order).count == order.count && musicIDs(order) == musicIDs(next))
                next = order
            }
            if let name {
                try musicRequire(c["kind"].string != "favorites", .favoritesProtected)
                _ = try musicText(name, maximum: 600); try musicRequire(name.utf16.count <= 200)
                c["name"] = .string(name)
            }
            var intents = c["intents"]
            var touched = Set<Data>()
            for id in previous + next where touched.insert(Data(id.utf8)).inserted {
                let before = previous.contains { musicSame($0, id) }, after = next.contains { musicSame($0, id) }
                if before != after {
                    // An unbound local removal must not become a future account's delete.
                    if !after, c["bindings"].array.isEmpty { intents.remove(id) }
                    else { intents[id] = .obj([("present", .bool(after)), ("pending", .bool(true)), ("origin", .string("local"))]) }
                }
            }
            c["intents"] = intents; c["order"] = .strings(next); c["manualOrder"] = .bool(true); c["revision"] = .n(requested.revision + 1)
            d.library["library"] = lib; saveCollection(c, &d)
            try recordAutomaticLibraryMutation(&d, collectionID: c["id"].string,
                orderOnly: previous.sorted() == next.sorted())
            return collectionDTO(c)
        }
        publishAutomaticLibrarySync(.localMutation)
        return result
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
