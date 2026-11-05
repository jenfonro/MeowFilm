// SPDX-License-Identifier: GPL-3.0-only
// Native port of music-core/library.js; local mutations originate in iWebPlus.
import Foundation
import MeowMusicFeature

enum MusicLibraryValidation {
    static func validate(_ lib: MJ, owner: String) throws {
        try lib.requireFields(["version", "ownerId", "revision", "nextId", "collections", "songs", "accounts", "status"])
        try musicRequire(lib["version"] == .n(1) && !owner.isEmpty && musicSame(lib["ownerId"].string, owner) &&
            lib["revision"].isInteger && lib["nextId"].isInteger && lib["nextId"].int > 0 &&
            lib["collections"].isArray && lib["collections"].array.count <= 2000 &&
            lib["songs"].isObject && lib["songs"].fields.count <= 20_000 && lib["accounts"].isObject, .corruptDocument)
        var nativeOwners: [String: String] = [:], collectionIDs = Set<Data>(), remoteBindings = Set<String>()
        let knownSongs = musicIDs(lib["songs"].fields.map(\.0))
        for (id, song) in lib["songs"].fields {
            try musicRequire(song["songId"].string.utf8.elementsEqual(id.utf8), .songIDConflict)
            let clean = try MusicModel.cleanSong(song)
            try musicRequire(musicSame(clean["songId"].string, id), .songIDConflict)
            for source in clean["sources"].array {
                let key = source["sourceKey"].string
                try musicRequire(nativeOwners[key] == nil || musicSame(nativeOwners[key]!, id), .nativeIDConflict)
                nativeOwners[key] = id
            }
        }
        for c in lib["collections"].array {
            try c.requireFields(["id", "kind", "name", "order", "revision", "manualOrder", "bindings", "intents", "deleted", "locallyCreated"])
            let id = try c["id"].text(), name = try c["name"].text(600), ids = c["order"].strings
            try musicRequire(name.utf16.count <= 200 && collectionIDs.insert(Data(id.utf8)).inserted && ["favorites", "playlist"].contains(c["kind"].string) &&
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
                _ = try b["accountId"].text(); _ = try b["id"].text()
                try musicRequire(MusicModel.sites.contains(b["platform"].string) && lib["accounts"][b["platform"].string] == b["accountId"] &&
                    bindings.insert(key).inserted && remoteBindings.insert(key).inserted && b["baseline"].isArray &&
                    b["baseline"].array.allSatisfy { $0.isString && !$0.string.isEmpty } &&
                    Set(b["baseline"].strings).count == b["baseline"].array.count && b["map"].isObject &&
                    b["writable"].isBool && b["name"].isString, .corruptDocument)
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
        for (platform, id) in lib["accounts"].fields { try musicRequire(MusicModel.sites.contains(platform), .corruptDocument); _ = try id.text() }
    }
    static func validateReceipt(_ receipt: MJ) throws {
        try receipt.requireFields(["batchId", "sourceId", "sourceOwnerId", "appliedAt", "appliedRevision", "afterHash"],
                                  required: ["batchId", "sourceId", "sourceOwnerId", "appliedAt", "appliedRevision", "afterHash"])
        try musicRequire(musicHex(receipt["batchId"].string) && musicHex(receipt["afterHash"].string) &&
            receipt["appliedRevision"].isInteger && receipt["appliedRevision"].int > 0 &&
            receipt["appliedAt"].isNumber && receipt["appliedAt"].number.isFinite &&
            receipt["appliedAt"].number >= 0, .corruptDocument)
        _ = try receipt["sourceId"].text()
        _ = try receipt["sourceOwnerId"].text()
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
        var songs = library["songs"], rows = songs.fields, result: [String] = []
        var positions: [Data: Int] = [:]
        for (offset, row) in rows.enumerated() { positions[Data(row.0.utf8)] = offset }
        var cleaned: [Data: MJ] = [:]
        var sourceOwner: [Data: String] = [:]
        for (id, row) in rows {
            for source in row["sources"].array {
                let key = Data(source["sourceKey"].string.utf8)
                if let owner = sourceOwner[key], !musicSame(owner, id) { throw MusicServiceError.songIDConflict }
                sourceOwner[key] = id
            }
        }
        var returned = Set<Data>()
        for raw in input {
            let songID = raw["songId"].string
            var song: MJ
            if !songID.isEmpty, let value = cleaned[Data(songID.utf8)],
               value["title"] == raw["title"], value["artist"] == raw["artist"],
               value["album"] == raw["album"], value["duration"] == raw["duration"],
               value["cover_url"] == raw["cover_url"], value["sources"] == raw["sources"] { song = value }
            else {
                song = try MusicModel.cleanSong(raw)
                if !songID.isEmpty { cleaned[Data(songID.utf8)] = song }
            }
            let key = MusicModel.nativeKey(song), firstKey: String? = key.isEmpty ? nil : key
            let keys = Set(song["sources"].array.map { $0["sourceKey"].string })
            func compatible(_ known: MJ) -> Bool {
                MusicModel.compatible(known, song) && known["sources"].array.allSatisfy { MusicModel.compatible($0, song) }
            }
            let existingIDs = Set(keys.compactMap { sourceOwner[Data($0.utf8)] })
            try musicRequire(existingIDs.count <= 1, .songIDConflict)
            if let existingID = existingIDs.first, let index = positions[Data(existingID.utf8)] {
                try musicRequire(compatible(rows[index].1), .nativeIDConflict)
                song["songId"] = rows[index].1["songId"]
            }
            var id = try song["songId"].text()
            if let index = positions[Data(id.utf8)], let firstKey,
               !compatible(rows[index].1) {
                let d = MusicModel.descriptor(song)
                let description = MJ.obj([("title", .string(d.title)), ("artist", .string(d.artist)), ("version", .string(d.version)), ("duration", .number(d.duration))])
                let fingerprint = MusicModel.fingerprint(song)
                id = "song:" + MusicModel.hash(fingerprint.isEmpty ? firstKey : fingerprint) + ":" + MusicModel.hash(MJ.array([.string(firstKey), description]).json)
                song["songId"] = .string(id)
            }
            if let index = positions[Data(id.utf8)] {
                let previous = rows[index].1
                try musicRequire(firstKey == nil || compatible(previous), .songIDConflict)
                song = try MusicModel.mergeSources(song, adding: previous["sources"].array)
                rows[index].1 = song
            } else {
                positions[Data(id.utf8)] = rows.count
                rows.append((id, song))
            }
            for source in song["sources"].array { sourceOwner[Data(source["sourceKey"].string.utf8)] = id }
            if returned.insert(Data(id.utf8)).inserted { result.append(id) }
        }
        try musicRequire(rows.count <= 20_000, .limit)
        songs = .obj(rows); library["songs"] = songs; return result
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
        return library["collections"].array.filter { !$0["deleted"].bool }.map { row in
            let first = row["order"].strings.first.map { library["songs"][$0] } ?? .null
            return .init(id: row["id"].string, title: row["name"].string, kind: row["kind"].string,
                count: row["order"].array.count, revision: row["revision"].int,
                artwork: MusicNativePublicBrowseSupport.artwork(first["cover_url"]))
        }
    }
    public func tracks(in requested: MusicCollection) async throws -> [MusicRemoteTrack] {
        let d = try read(), c = try collection(requested.id, d)
        try musicRequire(c["revision"].int == requested.revision, .staleRevision)
        return try c["order"].strings.map { try MusicModel.track(d.library["library"]["songs"][$0], .init(kind: .library, id: $0)) }
    }
    public func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        _ = try musicText(name, maximum: 600); try musicRequire(name.utf16.count <= 200)
        let result = try transaction(["library"]) { d in
            var lib = d.library["library"]
            try musicRequire(lib["collections"].array.count < 2000, .limit)
            let songs = try refs.map { try song($0, d) }, ids = try upsert(songs, library: &lib)
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
        let result = try transaction(["library"]) { d in
            var c = try collection(requested.id, d), lib = d.library["library"]
            try musicRequire(c["revision"].int == requested.revision, .revisionConflict)
            let previous = c["order"].strings
            let songs = try refs.map { refs in try refs.map { try song($0, d) } }
            var next = try songs.map { try upsert($0, library: &lib) } ?? previous
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
                if before != after { intents[id] = .obj([("present", .bool(after)), ("pending", .bool(true)), ("origin", .string("local"))]) }
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
