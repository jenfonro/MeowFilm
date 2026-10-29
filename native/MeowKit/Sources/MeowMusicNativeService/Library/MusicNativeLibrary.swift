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
            for field in ["sources", "quarantinedSources"] {
                try musicRequire(!song.contains(field) || song[field].isArray, .corruptDocument)
            }
            var seen = Set<String>()
            for source in song["sources"].array + song["quarantinedSources"].array {
                let key = try MusicModel.sourceKey(MusicModel.sourceData(source))
                try musicRequire(source["sourceKey"].string.utf8.elementsEqual(key.utf8) && seen.insert(key).inserted, .nativeIDConflict)
                try MusicModel.validateNativeNumbers(MusicModel.sourceData(source))
            }
            for source in song["sources"].array {
                let key = source["sourceKey"].string
                try musicRequire(nativeOwners[key] == nil || musicSame(nativeOwners[key]!, id), .nativeIDConflict)
                try musicRequire(MusicModel.compatible(song, source), .nativeIDConflict)
                for other in song["sources"].array {
                    try musicRequire(MusicModel.compatible(source, other, sameNative: key == other["sourceKey"].string), .nativeIDConflict)
                }
                nativeOwners[key] = id
            }
        }
        for c in lib["collections"].array {
            try c.requireFields(["id", "kind", "name", "order", "revision", "manualOrder", "bindings", "intents", "deleted", "locallyCreated", "creating"])
            let id = try c["id"].text(), name = try c["name"].text(600), ids = c["order"].strings
            try musicRequire(name.utf16.count <= 200 && collectionIDs.insert(Data(id.utf8)).inserted && ["favorites", "playlist"].contains(c["kind"].string) &&
                c["revision"].isInteger && c["order"].isArray && musicIDs(ids).count == ids.count &&
                ids.allSatisfy { knownSongs.contains(Data($0.utf8)) } && c["bindings"].isArray && c["intents"].isObject &&
                c["creating"].isObject && c["manualOrder"].isBool, .corruptDocument)
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
                for (member, native) in b["map"].fields {
                    _ = try native.text()
                    // Native/occurrence -> song is the fixed iWeb v1 format.
                    // Keep early migrated song -> native maps readable; sync
                    // canonicalizes them only inside a conditional commit.
                    try musicRequire(knownSongs.contains(Data(member.utf8)) || knownSongs.contains(Data(native.string.utf8)), .corruptDocument)
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
            for (target, creation) in c["creating"].fields {
                let pair = try MJ.parse(Data(target.utf8), maximum: 4096).array
                try creation.requireFields(["before", "name"])
                try musicRequire(pair.count == 2 && lib["accounts"][pair[0].string] == pair[1] &&
                    creation["before"].isArray && creation["before"].array.allSatisfy { $0.isString && !$0.string.isEmpty }, .corruptDocument)
                _ = try creation["name"].text()
            }
        }
        try musicRequire(lib["collections"].array.filter { $0["kind"].string == "favorites" }.count == 1, .favoritesProtected)
        for (platform, id) in lib["accounts"].fields { try musicRequire(MusicModel.sites.contains(platform), .corruptDocument); _ = try id.text() }
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
        try musicRequire(input.count <= 2000, .limit)
        var songs = library["songs"], result: [String] = []
        for raw in input {
            var song = try MusicModel.cleanSong(raw)
            let key = MusicModel.nativeKey(song), firstKey: String? = key.isEmpty ? nil : key
            func compatible(_ known: MJ) -> Bool {
                MusicModel.compatible(known, song) && known["sources"].array.allSatisfy { MusicModel.compatible($0, song) }
            }
            if let existing = songs.fields.map(\.1).first(where: { row in
                firstKey != nil && (MusicModel.nativeKey(row) == firstKey || row["sources"].array.contains { $0["sourceKey"].string == firstKey })
            }) {
                try musicRequire(compatible(existing), .nativeIDConflict)
                song["songId"] = existing["songId"]
            }
            var id = try song["songId"].text()
            if songs.contains(id), let firstKey,
               !compatible(songs[id]) {
                let d = MusicModel.descriptor(song)
                let description = MJ.obj([("title", .string(d.title)), ("artist", .string(d.artist)), ("version", .string(d.version)), ("duration", .number(d.duration))])
                let fingerprint = MusicModel.fingerprint(song)
                id = "song:" + MusicModel.hash(fingerprint.isEmpty ? firstKey : fingerprint) + ":" + MusicModel.hash(MJ.array([.string(firstKey), description]).json)
                song["songId"] = .string(id)
            }
            if songs.contains(id) {
                let previous = songs[id]
                try musicRequire(firstKey == nil || compatible(previous), .songIDConflict)
                var combined = previous["sources"].array
                for source in song["sources"].array {
                    if let i = combined.firstIndex(where: { $0["sourceKey"] == source["sourceKey"] }) { combined[i] = source }
                    else { combined.append(source) }
                }
                for a in combined { for b in combined {
                    try musicRequire(MusicModel.compatible(a, b, sameNative: a["sourceKey"] == b["sourceKey"]), .nativeIDConflict)
                } }
                if previous["revision"].int > song["revision"].int { song = previous }
                else { song["sources"] = .array(combined) }
            }
            songs[id] = song
            if !result.contains(where: { musicSame($0, id) }) { result.append(id) }
        }
        try musicRequire(songs.fields.count <= 20_000, .limit); library["songs"] = songs; return result
    }
    func saveCollection(_ c: MJ, _ d: inout MusicDocument) {
        var library = d.library["library"], rows = library["collections"].array
        if let i = rows.firstIndex(where: { $0["id"] == c["id"] }) { rows[i] = c } else { rows.append(c) }
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
    func checkImport(_ d: MusicDocument, _ plan: Import) throws -> Bool {
        let receipt = d.library["receipt"]
        if receipt != .null {
            try musicRequire(receipt["batchId"].string == plan.batch, .importConflict)
            try musicRequire(receipt["afterHash"].string == d.library["library"].stable.digest &&
                receipt["appliedRevision"] == d.library["revision"], .importedTargetChanged)
            return true
        }
        // A local automatic-settings save is not user library content. It must
        // not make an otherwise empty target ineligible for read-only import.
        try musicRequire(d.library["library"].stable.digest == MusicModel.emptyLibrary.stable.digest &&
            (try MusicNativeSyncLedger.load(d.library)).fences.isEmpty, .importConflict); return false
    }
    func report(_ p: Import, applied: Bool) -> MusicImportReport {
        .init(batchId: p.batch, alreadyApplied: applied, songs: p.library["songs"].fields.count,
              collections: p.library["collections"].array.count, members: p.library["collections"].array.reduce(0) { $0 + $1["order"].array.count })
    }
    public func prepareImport(data: Data, sourceId: String, ownerId: String) async throws -> MusicImportPreview {
        try check(); _ = try musicText(sourceId); _ = try musicText(ownerId)
        var lib = try MJ.parse(data, maximum: 7 * 1024 * 1024)
        try MusicLibraryValidation.validate(lib, owner: ownerId)
        var songs = lib["songs"]
        for (id, song) in songs.fields { songs[id] = try MusicModel.cleanSong(song) }
        lib["songs"] = songs
        try MusicModel.rejectSecrets(lib)
        lib["status"] = .obj([("phase", .string("blocked")), ("at", .n(0)), ("issues", .array([.obj([("code", .string("legacy_import_requires_rebinding"))])]))])
        let batch = MJ.obj([("sourceId", .string(sourceId)), ("ownerId", .string(ownerId)), ("scope", .string("device")), ("library", lib)]).stable.digest
        let plan = Import(library: lib, sourceID: sourceId, ownerID: ownerId, batch: batch, at: now())
        let applied = try checkImport(read(), plan)
        prepared = prepared.filter { now().timeIntervalSince($0.value.at) < 600 }
        try musicRequire(prepared.count < 2, .busy)
        let id = UUID().uuidString.lowercased(); prepared[id] = plan
        return .init(importId: id, report: report(plan, applied: applied))
    }
    public func commitImport(_ preview: MusicImportPreview) async throws -> MusicImportReport {
        try check()
        guard let p = prepared[preview.importId], p.batch == preview.report.batchId, now().timeIntervalSince(p.at) < 600 else { throw MusicServiceError.importNotFound }
        let result = try transaction(["library"]) { d in
            let applied = try checkImport(d, p)
            if !applied {
                // Imported local data is NOT automatic remote-write consent.
                // Disable (do not inherit) any empty-target automatic settings
                // in the SAME CAS, preserving the import receipt/rollback.
                var ledger = try MusicNativeSyncLedger.load(d.library)
                var automatic = MusicNativeAutomaticSyncPreferences()
                automatic.revision = (ledger.automatic?.revision ?? 0) + 1
                ledger.automatic = automatic
                d.library["sync"] = try ledger.json()
                var lib = p.library; lib["ownerId"] = .string("device")
                d.library["library"] = lib; d.library["revision"] = .n(d.library["revision"].int + 1)
                d.library["receipt"] = .obj([("batchId", .string(p.batch)), ("sourceId", .string(p.sourceID)), ("legacyOwnerId", .string(p.ownerID)),
                    ("appliedAt", .number(now().timeIntervalSince1970 * 1000)), ("appliedRevision", d.library["revision"]), ("afterHash", .string(lib.stable.digest))])
            }
            return report(p, applied: applied)
        }
        automaticLibrarySyncRuntime.authorization?.revoke()
        automaticLibrarySyncRuntime.task?.cancel()
        publishAutomaticLibrarySync(.refreshed) // NOT a sync intent.
        return result
    }
    public func releaseImport(_ preview: MusicImportPreview) async throws { try check(); prepared.removeValue(forKey: preview.importId) }
    public func rollbackImport(batchId: String) async throws {
        try performImportRollback(batchId: batchId)
    }
    func performImportRollback(batchId: String) throws {
        try transaction(["library"]) { d in
            let receipt = d.library["receipt"]
            if receipt == .null && d.library["rolledBack"].string == batchId { return }
            try musicRequire(receipt["batchId"].string == batchId, .importNotFound)
            try musicRequire(receipt["afterHash"].string == d.library["library"].stable.digest &&
                receipt["appliedRevision"] == d.library["revision"], .importedTargetChanged)
            d.library = MusicModel.library; d.library["rolledBack"] = .string(batchId)
        }
    }
}
