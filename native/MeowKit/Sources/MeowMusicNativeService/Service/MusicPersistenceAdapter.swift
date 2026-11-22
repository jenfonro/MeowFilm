import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicPersistenceAdapter {
    private static let defaultMetadataPlatforms = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    private static let defaultOfficialSources = MusicAccountPlatform.allCases.map(\.rawValue)
    private static let pureScalarInfoKeys: Set<String> = [
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    ]
    private static let indexedIDKeys: Set<String> = ["songmid", "mid", "id"]

    // MARK: - Primitive Conversions

    private static func optionalText(_ value: MJ) -> String? {
        if case .string(let text) = value { return text }
        return nil
    }

    private static func optionalNonEmpty(_ value: MJ) -> String? {
        if case .string(let text) = value, !text.isEmpty { return text }
        return nil
    }

    private static func positiveDouble(_ value: MJ) -> Double? {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite && number > 0 ? number : nil
    }

    private static func finiteDouble(_ value: MJ, fallback: Double = 0) -> Double {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite ? number : fallback
    }

    private static func epochMillis(_ value: MJ, fallback: Int64 = 0) -> Int64 {
        let number = MusicModel.nativeNumber(value)
        guard number.isFinite else { return fallback }
        return Int64(number.rounded())
    }

    private static func readIDAsText(_ value: MJ) -> (text: String?, needsTypedExtra: Bool) {
        switch value {
        case .null: return (nil, false)
        case .string(let text): return (text, false)
        case .number(let number):
            guard number.isFinite else { return (nil, true) }
            if number.rounded(.towardZero) == number,
               abs(number) <= Double(MusicPersistenceListDiff.maxSafeInteger),
               let integer = Int64(exactly: number) {
                return (String(integer), true)
            }
            return (MJ.number(number).json, true)
        case .upstreamNumber(let token):
            return (token, true)
        default:
            return (nil, true)
        }
    }

    private static func mergeFields(_ base: [(String, MJ)], overrides: [(String, MJ)]) -> [(String, MJ)] {
        var result = base
        for (key, val) in overrides {
            if let idx = result.firstIndex(where: { $0.0.utf8.elementsEqual(key.utf8) }) {
                result[idx].1 = val
            } else {
                result.append((key, val))
            }
        }
        return result
    }

    // MARK: - SQLiteTreeValue <-> MJ

    static func mj(from tree: SQLiteTreeValue) throws -> MJ {
        switch tree {
        case .null: return .null
        case .boolean(let flag): return .bool(flag)
        case .number(let num): return .number(num)
        case .exactNumber(let token): return .upstreamNumber(token)
        case .text(let text): return .string(text)
        case .array(let items): return .array(try items.map(mj(from:)))
        case .object(let fields):
            return .obj(try fields.map { ($0.name, try mj(from: $0.value)) })
        }
    }

    static func tree(from value: MJ) throws -> SQLiteTreeValue {
        switch value {
        case .null: return .null
        case .bool(let flag): return .boolean(flag)
        case .number(let num): return .number(num)
        case .upstreamNumber(let token): return .exactNumber(token)
        case .string(let text): return .text(text)
        case .array(let items): return .array(try items.map(tree(from:)))
        case .object(let fields):
            return .object(try fields.map { SQLiteTreeField($0.0, try tree(from: $0.1)) })
        }
    }

    // MARK: - Source & Play Context

    private static func sourceToMJ(_ source: MusicSourceRecord?) -> MJ {
        guard let source else { return .null }
        var fields: [(String, MJ)] = []
        if let id = source.id { fields.append(("id", .string(id))) }
        if let title = source.title { fields.append(("title", .string(title))) }
        if let kind = source.kind { fields.append(("kind", .string(kind))) }
        if let platform = source.platform { fields.append(("platform", .string(platform))) }
        if let sourceType = source.sourceType { fields.append(("sourceType", .string(sourceType))) }
        if let sourceTitle = source.sourceTitle { fields.append(("sourceTitle", .string(sourceTitle))) }
        if let sourceId = source.sourceId { fields.append(("sourceId", .string(sourceId))) }
        if let playlist = source.playlist { fields.append(("playlist", .string(playlist))) }
        if let seedEntryId = source.seedEntryId { fields.append(("seedEntryId", .string(seedEntryId))) }
        if let remoteSession = source.remoteSession { fields.append(("remoteSession", .string(remoteSession))) }
        if let dailyBranch = source.dailyBranch { fields.append(("dailyBranch", .string(dailyBranch))) }
        if let searchKeyword = source.searchKeyword { fields.append(("searchKeyword", .string(searchKeyword))) }
        return fields.isEmpty ? .null : .obj(fields)
    }

    private static func sourceFromMJ(_ source: MJ) -> MusicSourceRecord? {
        guard source.isObject else { return nil }
        let rec = MusicSourceRecord(
            id: optionalText(source["id"]),
            title: optionalText(source["title"]),
            kind: optionalText(source["kind"]),
            platform: optionalText(source["platform"]),
            sourceType: optionalText(source["sourceType"]),
            sourceTitle: optionalText(source["sourceTitle"]),
            sourceId: optionalText(source["sourceId"]),
            playlist: optionalText(source["playlist"]),
            seedEntryId: optionalText(source["seedEntryId"]),
            remoteSession: optionalText(source["remoteSession"]),
            dailyBranch: optionalText(source["dailyBranch"]),
            searchKeyword: optionalText(source["searchKeyword"])
        )
        return rec.isEmpty ? nil : rec
    }

    private static func contextToMJ(_ context: MusicPlayContextRecord) -> MJ? {
        var fields: [(String, MJ)] = []
        if let v = context.collectionId { fields.append(("collectionId", .string(v))) }
        if let v = context.playlistId { fields.append(("playlistId", .string(v))) }
        if let v = context.sourceTitle { fields.append(("sourceTitle", .string(v))) }
        if let v = context.playlist { fields.append(("playlist", .string(v))) }
        if let v = context.sourceType { fields.append(("sourceType", .string(v))) }
        if let v = context.sourceId { fields.append(("sourceId", .string(v))) }
        if let v = context.sourcePlatform { fields.append(("sourcePlatform", .string(v))) }
        if let v = context.platform { fields.append(("platform", .string(v))) }
        if let v = context.sourceLabel { fields.append(("sourceLabel", .string(v))) }
        if let v = context.searchKeyword { fields.append(("searchKeyword", .string(v))) }
        if let v = context.dailyBranch { fields.append(("dailyBranch", .string(v))) }
        if let v = context.heartbeatPlaylistId { fields.append(("heartbeatPlaylistId", .string(v))) }
        if let v = context.heartbeatAccountId { fields.append(("heartbeatAccountId", .string(v))) }
        if let v = context.provider { fields.append(("provider", .string(v))) }
        if let v = context.remoteSession { fields.append(("remoteSession", .string(v))) }
        var queueSourceFields: [(String, MJ)] = []
        if let v = context.queueSourceId { queueSourceFields.append(("id", .string(v))) }
        if let v = context.queueSourceTitle { queueSourceFields.append(("title", .string(v))) }
        if let v = context.queueSourceKind { queueSourceFields.append(("kind", .string(v))) }
        if let v = context.queueSourcePlatform { queueSourceFields.append(("platform", .string(v))) }
        if !queueSourceFields.isEmpty { fields.append(("queueSource", .obj(queueSourceFields))) }
        var similarFields: [(String, MJ)] = []
        if let v = context.similarBatchId { similarFields.append(("batchId", .string(v))) }
        if let v = context.similarBasedOnSongId { similarFields.append(("basedOnSongId", .string(v))) }
        if let v = context.similarProvider { similarFields.append(("provider", .string(v))) }
        if !similarFields.isEmpty { fields.append(("similar", .obj(similarFields))) }
        return fields.isEmpty ? nil : .obj(fields)
    }

    private static func contextFromMJ(_ context: MJ) -> MusicPlayContextRecord {
        guard context.isObject else { return .empty }
        let qs = context["queueSource"]
        let sim = context["similar"]
        return MusicPlayContextRecord(
            collectionId: optionalText(context["collectionId"]),
            playlistId: optionalText(context["playlistId"]),
            sourceTitle: optionalText(context["sourceTitle"]),
            playlist: optionalText(context["playlist"]),
            sourceType: optionalText(context["sourceType"]),
            sourceId: optionalText(context["sourceId"]),
            sourcePlatform: optionalText(context["sourcePlatform"]),
            platform: optionalText(context["platform"]),
            sourceLabel: optionalText(context["sourceLabel"]),
            searchKeyword: optionalText(context["searchKeyword"]),
            dailyBranch: optionalText(context["dailyBranch"]),
            heartbeatPlaylistId: optionalText(context["heartbeatPlaylistId"]),
            heartbeatAccountId: optionalText(context["heartbeatAccountId"]),
            provider: optionalText(context["provider"]),
            remoteSession: optionalText(context["remoteSession"]),
            queueSourceId: qs.isObject ? optionalText(qs["id"]) : nil,
            queueSourceTitle: qs.isObject ? optionalText(qs["title"]) : nil,
            queueSourceKind: qs.isObject ? optionalText(qs["kind"]) : nil,
            queueSourcePlatform: qs.isObject ? optionalText(qs["platform"]) : nil,
            similarBatchId: sim.isObject ? optionalText(sim["batchId"]) : nil,
            similarBasedOnSongId: sim.isObject ? optionalNonEmpty(sim["basedOnSongId"]) : nil,
            similarProvider: sim.isObject ? optionalText(sim["provider"]) : nil
        )
    }

    // MARK: - Songs

    static func songToMJ(_ record: MusicSongRecord) throws -> MJ {
        var sources: [MJ] = []
        sources.reserveCapacity(record.sources.count)
        for source in record.sources {
            var infoFields: [(String, MJ)] = [("source", .string(source.infoSource))]
            if let songmid = source.songmid { infoFields.append(("songmid", .string(songmid))) }
            if let mid = source.mid { infoFields.append(("mid", .string(mid))) }
            if let nativeID = source.nativeID { infoFields.append(("id", .string(nativeID))) }
            if let hash = source.hash { infoFields.append(("hash", .string(hash))) }
            if let name = source.name { infoFields.append(("name", .string(name))) }
            if let singer = source.singer { infoFields.append(("singer", .string(singer))) }
            if let albumName = source.albumName { infoFields.append(("albumName", .string(albumName))) }
            if let duration = source.duration { infoFields.append(("duration", .number(duration))) }
            if let img = source.img { infoFields.append(("img", .string(img))) }
            if let qualityKnown = source.qualityKnown { infoFields.append(("qualityKnown", .bool(qualityKnown))) }
            if !source.artistNames.isEmpty { infoFields.append(("artistNames", .strings(source.artistNames))) }

            var extraSourceFields: [(String, MJ)] = []
            if let extras = source.extras {
                let decoded = try mj(from: extras)
                if decoded["musicInfo"].isObject {
                    infoFields = mergeFields(infoFields, overrides: decoded["musicInfo"].fields)
                }
                if decoded["source"].isObject {
                    extraSourceFields = decoded["source"].fields
                }
            }

            var sourceFields: [(String, MJ)] = [
                ("sourceKey", .string(source.sourceKey)),
                ("source", .string(source.platform)),
                ("musicInfo", .obj(infoFields))
            ]
            sourceFields.append(contentsOf: extraSourceFields)
            sources.append(.obj(sourceFields))
        }

        var songFields: [(String, MJ)] = [
            ("songId", .string(record.songID)),
            ("title", .string(record.title)),
            ("artist", .string(record.artist)),
            ("album", .string(record.album))
        ]
        if let duration = record.duration { songFields.append(("duration", .number(duration))) }
        if let baselineDuration = record.baselineDuration { songFields.append(("baselineDuration", .number(baselineDuration))) }
        songFields.append(("cover_url", .string(record.coverURL)))
        if !record.artistNames.isEmpty { songFields.append(("artistNames", .strings(record.artistNames))) }
        if let localFileID = record.localFileID { songFields.append(("localFileId", .string(localFileID))) }
        if let artworkSourceKey = record.artworkSourceKey { songFields.append(("artworkSourceKey", .string(artworkSourceKey))) }
        if let artworkSourceImage = record.artworkSourceImage { songFields.append(("artworkSourceImage", .string(artworkSourceImage))) }
        songFields.append(("sources", .array(sources)))
        return .obj(songFields)
    }

    static func songFromMJ(_ song: MJ, baselineDurationOverride: Double?) throws -> MusicSongRecord {
        let id = song["songId"].string
        let resolvedBaseline: Double?
        if let baselineDurationOverride, baselineDurationOverride.isFinite, baselineDurationOverride > 0 {
            resolvedBaseline = baselineDurationOverride
        } else {
            resolvedBaseline = positiveDouble(song["baselineDuration"])
        }

        let sources: [MusicSongSourceRecord] = try song["sources"].array.map { source in
            let sourceKey = source["sourceKey"].string
            let info = source["musicInfo"]
            let platform = source["source"].string.isEmpty ? info["source"].string : source["source"].string
            let infoSource = info["source"].string.isEmpty ? platform : info["source"].string

            let songmid = readIDAsText(info["songmid"])
            let mid = readIDAsText(info["mid"])
            let nativeID = readIDAsText(info["id"])

            let extraInfo = info.fields.filter { field in
                if pureScalarInfoKeys.contains(field.0) { return false }
                if indexedIDKeys.contains(field.0) {
                    switch field.0 {
                    case "songmid": return songmid.needsTypedExtra
                    case "mid": return mid.needsTypedExtra
                    case "id": return nativeID.needsTypedExtra
                    default: return false
                    }
                }
                return true
            }
            let extraSource = source.fields.filter { !["sourceKey", "source", "musicInfo"].contains($0.0) }
            let extras: SQLiteTreeValue?
            if !extraInfo.isEmpty || !extraSource.isEmpty {
                var payload: [(String, MJ)] = []
                if !extraInfo.isEmpty { payload.append(("musicInfo", .obj(extraInfo))) }
                if !extraSource.isEmpty { payload.append(("source", .obj(extraSource))) }
                extras = try tree(from: .obj(payload))
            } else {
                extras = nil
            }

            let qualityKnown: Bool?
            if case .bool(let flag) = info["qualityKnown"] {
                qualityKnown = flag
            } else {
                qualityKnown = nil
            }

            return MusicSongSourceRecord(
                sourceKey: sourceKey,
                platform: platform,
                infoSource: infoSource,
                songmid: songmid.text,
                mid: mid.text,
                nativeID: nativeID.text,
                hash: optionalText(info["hash"]),
                name: optionalText(info["name"]),
                singer: optionalText(info["singer"]),
                albumName: optionalText(info["albumName"]),
                duration: positiveDouble(info["duration"]),
                img: optionalText(info["img"]),
                qualityKnown: qualityKnown,
                artistNames: info["artistNames"].strings,
                extras: extras
            )
        }

        return MusicSongRecord(
            songID: id,
            title: song["title"].string,
            artist: song["artist"].string,
            album: song["album"].string,
            duration: positiveDouble(song["duration"]),
            baselineDuration: resolvedBaseline,
            coverURL: song["cover_url"].string,
            artistNames: song["artistNames"].strings,
            localFileID: optionalNonEmpty(song["localFileId"]),
            artworkSourceKey: optionalNonEmpty(song["artworkSourceKey"]),
            artworkSourceImage: optionalNonEmpty(song["artworkSourceImage"]),
            sources: sources
        )
    }

    // MARK: - Library Area

    static func libraryToMJ(_ record: MusicLibraryAreaRecord, scope: String = "device") -> MJ {
        let accountFields: [(String, MJ)] = record.accounts.map { ($0.platform, .string($0.accountID)) }
        let statusIssues: [MJ] = record.statusIssues.map { issue in
            var fields: [(String, MJ)] = []
            if let platform = issue.platform { fields.append(("platform", .string(platform))) }
            if let collectionID = issue.collectionID { fields.append(("collectionID", .string(collectionID))) }
            fields.append(("code", .string(issue.code)))
            return .obj(fields)
        }
        var statusFields: [(String, MJ)] = [
            ("phase", .string(record.statusPhase)),
            ("at", .number(Double(record.statusAtMs))),
            ("issues", .array(statusIssues))
        ]
        if let code = record.statusCode {
            statusFields.append(("code", .string(code)))
        }

        let collections: [MJ] = record.collections.map { col in
            let bindings: [MJ] = col.bindings.map { binding in
                var fields: [(String, MJ)] = [
                    ("platform", .string(binding.platform)),
                    ("accountId", .string(binding.accountID)),
                    ("id", .string(binding.remoteID)),
                    ("writable", .bool(binding.writable)),
                    ("baseline", .strings(binding.baseline)),
                    ("map", .obj(binding.map.map { ($0.remoteEntryID, .string($0.songID)) }))
                ]
                if let name = binding.name { fields.append(("name", .string(name))) }
                return .obj(fields)
            }
            let intents: [(String, MJ)] = col.intents.map { intent in
                var fields: [(String, MJ)] = [
                    ("present", .bool(intent.present)),
                    ("pending", .bool(intent.pending))
                ]
                if let origin = intent.origin { fields.append(("origin", .string(origin))) }
                if !intent.acknowledged.isEmpty { fields.append(("acknowledged", .strings(intent.acknowledged))) }
                return (intent.intentKey, .obj(fields))
            }
            var fields: [(String, MJ)] = [
                ("id", .string(col.id)),
                ("name", .string(col.name)),
                ("kind", .string(col.kind)),
                ("order", .strings(col.order)),
                ("revision", .n(col.revision)),
                ("manualOrder", .bool(col.manualOrder)),
                ("bindings", .array(bindings)),
                ("intents", .obj(intents))
            ]
            if col.deleted { fields.append(("deleted", .bool(true))) }
            if col.locallyCreated { fields.append(("locallyCreated", .bool(true))) }
            return .obj(fields)
        }

        let networkReferences: [MJ] = record.networkReferences.map { ref in
            let relation: MJ
            if let rel = ref.relation {
                var relFields: [(String, MJ)] = [
                    ("accountId", .string(rel.accountID)),
                    ("baseline", .bool(rel.baseline)),
                    ("relationID", .string(rel.relationID))
                ]
                if let locator = rel.locator { relFields.append(("locator", .string(locator))) }
                relation = .obj(relFields)
            } else {
                relation = .null
            }
            return .obj([
                ("id", .string(ref.id)),
                ("platform", .string(ref.platform)),
                ("remoteID", .string(ref.remoteID)),
                ("name", .string(ref.name)),
                ("creator", .string(ref.creator)),
                ("artwork", .string(ref.artwork)),
                ("count", .n(ref.count)),
                ("revision", .n(ref.revision)),
                ("present", .bool(ref.present)),
                ("pending", .bool(ref.pending)),
                ("relation", relation)
            ])
        }

        let innerLibrary: MJ = .obj([
            ("version", .n(1)),
            ("ownerId", .string(record.ownerID)),
            ("revision", .n(record.libraryRevision)),
            ("nextId", .n(record.nextID)),
            ("collections", .array(collections)),
            ("networkReferences", .array(networkReferences)),
            ("accounts", .obj(accountFields)),
            ("status", .obj(statusFields))
        ])

        var areaFields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("scope", .string(scope)),
            ("revision", .n(record.revision)),
            ("library", innerLibrary)
        ]
        if let sync = record.sync {
            areaFields.append(("sync", syncToMJ(sync)))
        }
        return .obj(areaFields)
    }

    private static func syncToMJ(_ sync: MusicSyncStateRecord) -> MJ {
        let manualSelections: [MJ] = sync.manualSelections.map {
            .obj([("platform", .string($0.platform)), ("favorites", .bool($0.favorites)), ("playlists", .bool($0.playlists))])
        }
        let autoSelections: [MJ] = sync.automaticSelections.map {
            .obj([("platform", .string($0.platform)), ("favorites", .bool($0.favorites)), ("playlists", .bool($0.playlists))])
        }
        let scopes: [(String, MJ)] = sync.scopes.map { ($0.platform, .string($0.accountScope)) }
        let issues: [MJ] = sync.issues.map { issue in
            var fields: [(String, MJ)] = []
            if let platform = issue.platform { fields.append(("platform", .string(platform))) }
            if let collectionID = issue.collectionID { fields.append(("collectionID", .string(collectionID))) }
            fields.append(("code", .string(issue.code)))
            return .obj(fields)
        }
        let autoBindings: [(String, MJ)] = sync.autoBindings.map {
            ($0.platform, .obj([("accountID", .string($0.accountID)), ("scope", .string($0.bindingScope))]))
        }
        let fences: [MJ] = sync.fences.map { fence in
            var mutationFields: [(String, MJ)] = [
                ("id", .string(fence.mutationID ?? "")),
                ("action", .string(fence.mutationAction ?? "")),
                ("directoryRevision", .string(fence.mutationDirectoryRevision ?? "")),
                ("members", .array(fence.mutationMembers.map {
                    .obj([("nativeID", .string($0.nativeID)), ("occurrenceID", .string($0.occurrenceID ?? ""))])
                }))
            ]
            if let cid = fence.mutationCollectionID { mutationFields.append(("collectionID", .string(cid))) }
            if let name = fence.mutationName { mutationFields.append(("name", .string(name))) }
            if let before = fence.before {
                mutationFields.append(("before", .obj([
                    ("id", .string(before.id)),
                    ("name", .string(before.name)),
                    ("kind", .string(before.kind)),
                    ("writable", .bool(before.writable)),
                    ("canRename", .bool(before.canRename)),
                    ("canDelete", .bool(before.canDelete)),
                    ("canOrder", .bool(before.canOrder)),
                    ("members", .array(before.members.map {
                        .obj([("nativeID", .string($0.nativeID)), ("occurrenceID", .string($0.occurrenceID ?? ""))])
                    })),
                    ("revision", .string(before.revision))
                ])))
            }
            if let sub = fence.beforeSubscription {
                var subFields: [(String, MJ)] = [
                    ("remoteID", .string(sub.remoteID)),
                    ("relationID", .string(sub.relationID)),
                    ("name", .string(sub.name)),
                    ("creator", .string(sub.creator)),
                    ("artwork", .string(sub.artwork))
                ]
                if let count = sub.count { subFields.append(("count", .n(count))) }
                mutationFields.append(("beforeSubscription", .obj(subFields)))
            }

            var fenceFields: [(String, MJ)] = [
                ("platform", .string(fence.platform)),
                ("accountID", .string(fence.accountID)),
                ("scope", .string(fence.fenceScope)),
                ("localID", .string(fence.localID)),
                ("localRevision", .n(fence.localRevision)),
                ("mutation", .obj(mutationFields))
            ]
            if let ackID = fence.acknowledgedID { fenceFields.append(("acknowledgedID", .string(ackID))) }
            if let ackName = fence.acknowledgedName { fenceFields.append(("acknowledgedName", .string(ackName))) }
            return .obj(fenceFields)
        }

        var autoFields: [(String, MJ)] = [
            ("revision", .n(sync.autoRevision)),
            ("selections", .array(autoSelections)),
            ("bindings", .obj(autoBindings)),
            ("intentRevision", .n(sync.autoIntentRevision)),
            ("settledIntentRevision", .n(sync.autoSettledIntentRevision)),
            ("relationshipPlatforms", .strings(sync.relationshipPlatforms))
        ]
        if let orderOnly = sync.autoOrderOnly { autoFields.append(("orderOnly", .string(orderOnly))) }

        return .obj([
            ("schema", .n(1)),
            ("selections", .array(manualSelections)),
            ("scopes", .obj(scopes)),
            ("fences", .array(fences)),
            ("phase", .string(sync.phase)),
            ("issues", .array(issues)),
            ("automatic", .obj(autoFields))
        ])
    }

    static func libraryFromMJ(_ value: MJ, scope: String = "device") -> MusicLibraryAreaRecord {
        let lib = value["library"]
        let status = lib["status"]

        let accounts: [MusicLibraryAccountRecord] = lib["accounts"].fields
            .compactMap { k, v in v.isString ? MusicLibraryAccountRecord(platform: k, accountID: v.string) : nil }
            .sorted { $0.platform < $1.platform }

        let statusIssues: [MusicLibraryIssueRecord] = status["issues"].array.map { issue in
            let colID = issue["collectionID"].isString ? issue["collectionID"] : issue["collectionId"]
            return MusicLibraryIssueRecord(
                platform: optionalNonEmpty(issue["platform"]),
                collectionID: optionalNonEmpty(colID),
                code: issue["code"].string
            )
        }

        let collections: [MusicCollectionRecord] = lib["collections"].array.map { col in
            let bindings: [MusicCollectionBindingRecord] = col["bindings"].array.map { binding in
                let mapEntries = binding["map"].fields
                    .compactMap { k, v in (v.isString && !v.string.isEmpty) ? MusicCollectionBindingMapEntry(remoteEntryID: k, songID: v.string) : nil }
                    .sorted { $0.remoteEntryID < $1.remoteEntryID }
                return MusicCollectionBindingRecord(
                    platform: binding["platform"].string,
                    accountID: binding["accountId"].string,
                    remoteID: binding["id"].string,
                    writable: binding["writable"].bool,
                    name: optionalText(binding["name"]),
                    baseline: binding["baseline"].strings,
                    map: mapEntries
                )
            }
            let intents: [MusicCollectionIntentRecord] = col["intents"].fields
                .map { intentKey, intent in
                    MusicCollectionIntentRecord(
                        intentKey: intentKey,
                        present: intent["present"].bool,
                        pending: intent["pending"].bool,
                        origin: optionalText(intent["origin"]),
                        acknowledged: intent["acknowledged"].strings
                    )
                }
                .sorted { $0.intentKey < $1.intentKey }

            return MusicCollectionRecord(
                id: col["id"].string,
                name: col["name"].string,
                kind: col["kind"].string,
                order: col["order"].strings,
                revision: col["revision"].int,
                manualOrder: col["manualOrder"].bool,
                deleted: col["deleted"].bool,
                locallyCreated: col["locallyCreated"].bool,
                bindings: bindings,
                intents: intents
            )
        }

        let networkReferences: [MusicNetworkReferenceRecord] = lib["networkReferences"].array.map { ref in
            let rel = ref["relation"]
            let relation: MusicNetworkReferenceRelationRecord?
            if rel.isObject && rel["accountId"].isString {
                relation = MusicNetworkReferenceRelationRecord(
                    accountID: rel["accountId"].string,
                    baseline: rel["baseline"].bool,
                    relationID: rel["relationID"].string,
                    locator: optionalText(rel["locator"])
                )
            } else {
                relation = nil
            }
            return MusicNetworkReferenceRecord(
                id: ref["id"].string,
                platform: ref["platform"].string,
                remoteID: ref["remoteID"].string,
                name: ref["name"].string,
                creator: ref["creator"].string,
                artwork: ref["artwork"].string,
                count: ref["count"].int,
                revision: ref["revision"].int,
                present: ref["present"].bool,
                pending: ref["pending"].bool,
                relation: relation
            )
        }

        let sync: MusicSyncStateRecord? = value.contains("sync") && value["sync"].isObject ? syncFromMJ(value["sync"]) : nil

        return MusicLibraryAreaRecord(
            revision: value["revision"].int,
            ownerID: lib["ownerId"].string.isEmpty ? scope : lib["ownerId"].string,
            libraryRevision: lib["revision"].int,
            nextID: max(1, lib["nextId"].int),
            accounts: accounts,
            statusPhase: status["phase"].string.isEmpty ? "idle" : status["phase"].string,
            statusAtMs: epochMillis(status["at"], fallback: 0),
            statusCode: optionalNonEmpty(status["code"]),
            statusIssues: statusIssues,
            collections: collections,
            networkReferences: networkReferences,
            sync: sync
        )
    }

    private static func syncFromMJ(_ sync: MJ) -> MusicSyncStateRecord {
        let auto = sync["automatic"]
        let manualSelections: [MusicSyncSelectionRecord] = sync["selections"].array.map {
            MusicSyncSelectionRecord(platform: $0["platform"].string, favorites: $0["favorites"].bool, playlists: $0["playlists"].bool)
        }
        let automaticSelections: [MusicSyncSelectionRecord] = auto["selections"].array.map {
            MusicSyncSelectionRecord(platform: $0["platform"].string, favorites: $0["favorites"].bool, playlists: $0["playlists"].bool)
        }
        let scopes: [MusicSyncScopeRecord] = sync["scopes"].fields
            .compactMap { k, v in v.isString ? MusicSyncScopeRecord(platform: k, accountScope: v.string) : nil }
            .sorted { $0.platform < $1.platform }
        let issues: [MusicLibraryIssueRecord] = sync["issues"].array.map { issue in
            let colID = issue["collectionID"].isString ? issue["collectionID"] : issue["collectionId"]
            return MusicLibraryIssueRecord(
                platform: optionalNonEmpty(issue["platform"]),
                collectionID: optionalNonEmpty(colID),
                code: issue["code"].string
            )
        }
        let autoBindings: [MusicSyncAutoBindingRecord] = auto["bindings"].fields
            .compactMap { k, v in
                v.isObject ? MusicSyncAutoBindingRecord(platform: k, accountID: v["accountID"].string, bindingScope: v["scope"].string) : nil
            }
            .sorted { $0.platform < $1.platform }
        let fences: [MusicSyncFenceRecord] = sync["fences"].array.map { fence in
            let mut = fence["mutation"]
            let beforeMJ = mut["before"]
            let before: MusicSyncFenceBeforeRecord?
            if beforeMJ.isObject {
                before = MusicSyncFenceBeforeRecord(
                    id: beforeMJ["id"].string,
                    name: beforeMJ["name"].string,
                    kind: beforeMJ["kind"].string,
                    writable: beforeMJ["writable"].bool,
                    canRename: beforeMJ["canRename"].bool,
                    canDelete: beforeMJ["canDelete"].bool,
                    canOrder: beforeMJ["canOrder"].bool,
                    revision: beforeMJ["revision"].string,
                    members: beforeMJ["members"].array.map {
                        MusicSyncFenceMemberRecord(nativeID: $0["nativeID"].string, occurrenceID: optionalText($0["occurrenceID"]))
                    }
                )
            } else {
                before = nil
            }
            let subMJ = mut["beforeSubscription"]
            let beforeSub: MusicSyncFenceBeforeSubscriptionRecord?
            if subMJ.isObject {
                beforeSub = MusicSyncFenceBeforeSubscriptionRecord(
                    remoteID: subMJ["remoteID"].string,
                    relationID: subMJ["relationID"].string,
                    name: subMJ["name"].string,
                    creator: subMJ["creator"].string,
                    artwork: subMJ["artwork"].string,
                    count: subMJ["count"].isInteger ? subMJ["count"].int : nil
                )
            } else {
                beforeSub = nil
            }
            return MusicSyncFenceRecord(
                platform: fence["platform"].string,
                accountID: fence["accountID"].string,
                fenceScope: fence["scope"].string,
                localID: fence["localID"].string,
                localRevision: fence["localRevision"].int,
                acknowledgedID: optionalText(fence["acknowledgedID"]),
                acknowledgedName: optionalText(fence["acknowledgedName"]),
                mutationID: optionalText(mut["id"]),
                mutationAction: optionalText(mut["action"]),
                mutationCollectionID: optionalText(mut["collectionID"]),
                mutationName: optionalText(mut["name"]),
                mutationDirectoryRevision: optionalText(mut["directoryRevision"]),
                mutationMembers: mut["members"].array.map {
                    MusicSyncFenceMemberRecord(nativeID: $0["nativeID"].string, occurrenceID: optionalText($0["occurrenceID"]))
                },
                before: before,
                beforeSubscription: beforeSub
            )
        }
        return MusicSyncStateRecord(
            phase: sync["phase"].string.isEmpty ? "idle" : sync["phase"].string,
            autoRevision: auto["revision"].int,
            autoIntentRevision: auto["intentRevision"].int,
            autoSettledIntentRevision: auto["settledIntentRevision"].int,
            autoOrderOnly: optionalNonEmpty(auto["orderOnly"]),
            manualSelections: manualSelections,
            automaticSelections: automaticSelections,
            scopes: scopes,
            issues: issues,
            autoBindings: autoBindings,
            relationshipPlatforms: auto["relationshipPlatforms"].strings,
            fences: fences
        )
    }

    // MARK: - Queue Area

    private static func queueEntryToMJ(_ entry: MusicQueueEntryRecord) -> MJ {
        var fields: [(String, MJ)] = [
            ("id", .string(entry.id)),
            ("songId", .string(entry.songID))
        ]
        if let context = contextToMJ(entry.context) {
            fields.append(("context", context))
        }
        return .obj(fields)
    }

    private static func queueEntryFromMJ(_ entry: MJ) -> MusicQueueEntryRecord {
        MusicQueueEntryRecord(
            id: entry["id"].string,
            songID: entry["songId"].string,
            context: contextFromMJ(entry["context"])
        )
    }

    static func queueToMJ(_ record: MusicQueueAreaRecord) -> MJ {
        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("entries", .array(record.entries.map(queueEntryToMJ))),
            ("currentId", record.currentID.map(MJ.string) ?? .null),
            ("priority", .strings(record.priority)),
            ("history", .strings(record.history)),
            ("source", sourceToMJ(record.source)),
            ("serial", .n(record.serial)),
            ("revision", .n(record.revision)),
            ("failed", .strings(record.failed))
        ]
        if let runID = record.runID {
            fields.append(("runId", .string(runID)))
        }
        if let similar = record.similar {
            let returnQueue: MJ
            if let ret = similar.returnQueue {
                returnQueue = .obj([
                    ("entries", .array(ret.entries.map(queueEntryToMJ))),
                    ("currentId", ret.currentID.map(MJ.string) ?? .null),
                    ("priority", .strings(ret.priority)),
                    ("history", .strings(ret.history)),
                    ("source", sourceToMJ(ret.source))
                ])
            } else {
                returnQueue = .null
            }
            let providerContext: MJ
            if similar.providerPID != nil || similar.providerSID != nil || similar.providerCount != nil {
                var ctx: [(String, MJ)] = []
                if let pid = similar.providerPID { ctx.append(("pid", .string(pid))) }
                if let sid = similar.providerSID { ctx.append(("sid", .string(sid))) }
                if let count = similar.providerCount { ctx.append(("count", .n(count))) }
                providerContext = .obj(ctx)
            } else {
                providerContext = .null
            }
            let batches: [MJ] = similar.batches.map { batch in
                .obj([
                    ("id", .string(batch.id)),
                    ("basedOnSongId", batch.basedOnSongID.map(MJ.string) ?? .null),
                    ("basedOnTitle", .string(batch.basedOnTitle)),
                    ("provider", .string(batch.provider)),
                    ("entryIds", .strings(batch.entryIDs))
                ])
            }
            fields.append(("similar", .obj([
                ("schema", .n(1)),
                ("seedSongId", similar.seedSongID.map(MJ.string) ?? .null),
                ("seedTitle", .string(similar.seedTitle)),
                ("seedEntryId", similar.seedEntryID.map(MJ.string) ?? .null),
                ("returnQueue", returnQueue),
                ("sourceIds", .strings(similar.sourceIDs)),
                ("fromHome", .bool(similar.fromHome)),
                ("provider", .string(similar.provider)),
                ("providerContext", providerContext),
                ("continue", .bool(similar.continueEnabled)),
                ("offset", .n(similar.offsetVal)),
                ("exhausted", .bool(similar.exhausted)),
                ("batches", .array(batches)),
                ("attempted", .strings(similar.attempted)),
                ("scope", .string(similar.similarScope)),
                ("status", .string(similar.status))
            ])))
        }
        return .obj(fields)
    }

    static func queueFromMJ(_ value: MJ) -> MusicQueueAreaRecord {
        let similarMJ = value["similar"]
        let similar: MusicQueueSimilarRecord?
        if similarMJ.isObject {
            let pCtx = similarMJ["providerContext"]
            let pid = (pCtx.isObject && !pCtx["pid"].id.isEmpty) ? pCtx["pid"].id : nil
            let sid = (pCtx.isObject && !pCtx["sid"].id.isEmpty) ? pCtx["sid"].id : nil
            let pCount = (pCtx.isObject && pCtx["count"].isInteger) ? pCtx["count"].int : nil
            let retMJ = similarMJ["returnQueue"]
            let returnQueue: MusicQueueSimilarReturnRecord?
            if retMJ.isObject {
                returnQueue = MusicQueueSimilarReturnRecord(
                    entries: retMJ["entries"].array.map(queueEntryFromMJ),
                    currentID: optionalNonEmpty(retMJ["currentId"]),
                    priority: retMJ["priority"].strings,
                    history: retMJ["history"].strings,
                    source: sourceFromMJ(retMJ["source"])
                )
            } else {
                returnQueue = nil
            }
            let batches: [MusicQueueSimilarBatchRecord] = similarMJ["batches"].array.map { batch in
                MusicQueueSimilarBatchRecord(
                    id: batch["id"].string,
                    basedOnSongID: optionalNonEmpty(batch["basedOnSongId"]),
                    basedOnTitle: batch["basedOnTitle"].string,
                    provider: batch["provider"].string,
                    entryIDs: batch["entryIds"].strings
                )
            }
            similar = MusicQueueSimilarRecord(
                seedSongID: optionalNonEmpty(similarMJ["seedSongId"]),
                seedTitle: similarMJ["seedTitle"].string,
                seedEntryID: optionalNonEmpty(similarMJ["seedEntryId"]),
                fromHome: similarMJ["fromHome"].bool,
                provider: similarMJ["provider"].string,
                continueEnabled: similarMJ["continue"].bool,
                offsetVal: similarMJ["offset"].int,
                exhausted: similarMJ["exhausted"].bool,
                similarScope: similarMJ["scope"].string,
                status: similarMJ["status"].string,
                providerPID: pid,
                providerSID: sid,
                providerCount: pCount,
                returnQueue: returnQueue,
                sourceIDs: similarMJ["sourceIds"].strings,
                attempted: similarMJ["attempted"].strings,
                batches: batches
            )
        } else {
            similar = nil
        }

        return MusicQueueAreaRecord(
            currentID: optionalNonEmpty(value["currentId"]),
            serial: value["serial"].int,
            revision: value["revision"].int,
            runID: optionalNonEmpty(value["runId"]),
            source: sourceFromMJ(value["source"]),
            entries: value["entries"].array.map(queueEntryFromMJ),
            priority: value["priority"].strings,
            history: value["history"].strings,
            failed: value["failed"].strings,
            similar: similar
        )
    }

    // MARK: - History Area

    static func historyToMJ(_ record: MusicHistoryAreaRecord) -> MJ {
        let playlists: [MJ] = record.playlists.map { pl in
            var fields: [(String, MJ)] = [
                ("id", .string(pl.id)),
                ("at", .number(Double(pl.playedAtMs))),
                ("title", .string(pl.title)),
                ("entries", .array(pl.entries.map(queueEntryToMJ))),
                ("currentId", pl.currentID.map(MJ.string) ?? .null),
                ("priority", .strings(pl.priority)),
                ("history", .strings(pl.history)),
                ("source", sourceToMJ(pl.source))
            ]
            if let context = contextToMJ(pl.context) {
                fields.append(("context", context))
            }
            return .obj(fields)
        }

        let days: [MJ] = record.days.map { day in
            let songs: [MJ] = day.songs.map { item in
                var fields: [(String, MJ)] = [
                    ("songId", .string(item.songID)),
                    ("at", .number(Double(item.playedAtMs)))
                ]
                if let context = contextToMJ(item.context) {
                    fields.append(("context", context))
                }
                return .obj(fields)
            }
            return .obj([
                ("date", .string(day.date)),
                ("songs", .array(songs))
            ])
        }

        return .obj([
            ("schema", .n(1)),
            ("playlists", .array(playlists)),
            ("days", .array(days))
        ])
    }

    static func historyFromMJ(_ value: MJ) -> MusicHistoryAreaRecord {
        let playlists: [MusicHistoryPlaylistRecord] = value["playlists"].array.map { pl in
            MusicHistoryPlaylistRecord(
                id: pl["id"].string,
                playedAtMs: epochMillis(pl["at"], fallback: 0),
                title: pl["title"].string,
                currentID: optionalNonEmpty(pl["currentId"]),
                source: sourceFromMJ(pl["source"]),
                context: contextFromMJ(pl["context"]),
                entries: pl["entries"].array.map(queueEntryFromMJ),
                priority: pl["priority"].strings,
                history: pl["history"].strings
            )
        }

        let days: [MusicHistoryDayRecord] = value["days"].array.map { day in
            let date = day["date"].string
            let dayID = day["id"].string.isEmpty ? date : day["id"].string
            let updatedAtMs: Int64
            if day["updatedAt"].isNumber {
                updatedAtMs = epochMillis(day["updatedAt"], fallback: 0)
            } else {
                updatedAtMs = epochMillis(day["songs"].array.first?["at"] ?? .null, fallback: 0)
            }
            let songs: [MusicHistoryDaySongRecord] = day["songs"].array.map { item in
                MusicHistoryDaySongRecord(
                    songID: item["songId"].string,
                    playedAtMs: epochMillis(item["at"], fallback: 0),
                    context: contextFromMJ(item["context"])
                )
            }
            return MusicHistoryDayRecord(date: date, dayID: dayID, updatedAtMs: updatedAtMs, songs: songs)
        }

        return MusicHistoryAreaRecord(playlists: playlists, days: days)
    }

    // MARK: - Playback Area (including Feedback Settings)

    static func playbackToMJ(_ record: MusicPlaybackAreaRecord) -> MJ {
        let qualityFields: [(String, MJ)] = [
            ("qualityOrder", .strings(record.qualityOrder)),
            ("qualityEnabled", .strings(record.qualityEnabled)),
            ("spatialOrder", .strings(record.spatialOrder)),
            ("spatialEnabled", .strings(record.spatialEnabled))
        ]
        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("revision", .n(record.revision)),
            ("mode", .n(record.mode)),
            ("qualitySettings", .obj(qualityFields)),
            ("officialSources", .strings(record.officialEnabled)),
            ("metadataPlatforms", .strings(record.metadataPlatforms)),
            ("metadataEnabled", .strings(record.metadataEnabled)),
            ("lyricsEnabled", .bool(record.lyricsEnabled)),
            ("showTranslation", .bool(record.showTranslation)),
            ("position", .obj([
                ("occurrenceId", record.positionOccurrenceID.map(MJ.string) ?? .null),
                ("seconds", .number(record.positionSeconds))
            ]))
        ]
        if let requestQuality = record.qualityRequest {
            fields.append(("requestQuality", .string(requestQuality)))
        }
        func encodeExclusions(_ rows: [MusicSourceExclusionRecord], isSource: Bool) -> [MJ] {
            rows.map { rec in
                var item: [(String, MJ)] = [
                    ("id", .string(rec.id)),
                    ("songID", .string(rec.songID)),
                    ("title", .string(rec.title)),
                    ("artist", .string(rec.artist))
                ]
                if isSource || !rec.provider.isEmpty {
                    item.append(("provider", .string(rec.provider)))
                }
                item.append(("platform", .string(rec.platform)))
                if let sourceKey = rec.sourceKey {
                    item.append(("sourceKey", .string(sourceKey)))
                }
                return .obj(item)
            }
        }
        if !record.sourceExclusions.isEmpty {
            fields.append(("sourceExclusions", .array(encodeExclusions(record.sourceExclusions, isSource: true))))
        }
        if !record.recordingExclusions.isEmpty {
            fields.append(("recordingSourceExclusions", .array(encodeExclusions(record.recordingExclusions, isSource: false))))
        }
        if !record.feedback.isEmpty {
            let platformFields: [(String, MJ)] = record.feedback.map { item in
                (item.platform, .obj([
                    ("enabled", .bool(item.enabled)),
                    ("generation", .n(item.generation))
                ]))
            }
            fields.append(("feedback", .obj([
                ("schema", .n(1)),
                ("platforms", .obj(platformFields))
            ])))
        }
        return .obj(fields)
    }

    static func playbackFromMJ(_ value: MJ) -> MusicPlaybackAreaRecord {
        let quality = value["qualitySettings"]
        let position = value["position"]

        func decodeExclusions(_ array: [MJ], kind: String) -> [MusicSourceExclusionRecord] {
            array.map { row in
                MusicSourceExclusionRecord(
                    kind: kind,
                    id: row["id"].string,
                    songID: row["songID"].string,
                    provider: row["provider"].string,
                    platform: row["platform"].string,
                    providerName: row["providerName"].string,
                    title: row["title"].string,
                    artist: row["artist"].string,
                    sourceKey: optionalText(row["sourceKey"]),
                    excludedAtMs: epochMillis(row["excludedAt"], fallback: 0)
                )
            }
        }

        let feedback: [MusicFeedbackPlatformRecord]
        let feedbackMJ = value["feedback"]
        if feedbackMJ.isObject && feedbackMJ["platforms"].isObject {
            feedback = feedbackMJ["platforms"].fields.compactMap { platform, row in
                guard row.isObject else { return nil }
                return MusicFeedbackPlatformRecord(
                    platform: platform,
                    enabled: row["enabled"].bool,
                    generation: row["generation"].int
                )
            }
        } else {
            feedback = []
        }

        return MusicPlaybackAreaRecord(
            revision: value["revision"].int,
            mode: value["mode"].isInteger ? value["mode"].int : 1,
            lyricsEnabled: value.contains("lyricsEnabled") ? value["lyricsEnabled"].bool : true,
            showTranslation: value.contains("showTranslation") ? value["showTranslation"].bool : true,
            qualityRequest: optionalNonEmpty(value["requestQuality"]),
            positionOccurrenceID: optionalNonEmpty(position["occurrenceId"]),
            positionSeconds: finiteDouble(position["seconds"], fallback: 0),
            qualityOrder: quality.contains("qualityOrder") ? quality["qualityOrder"].strings : MusicModel.qualityOrder,
            qualityEnabled: quality.contains("qualityEnabled") ? quality["qualityEnabled"].strings : MusicModel.qualityOrder,
            spatialOrder: quality.contains("spatialOrder") ? quality["spatialOrder"].strings : MusicModel.spatialOrder,
            spatialEnabled: quality.contains("spatialEnabled") ? quality["spatialEnabled"].strings : MusicModel.spatialOrder,
            officialEnabled: value.contains("officialSources") ? value["officialSources"].strings : defaultOfficialSources,
            metadataPlatforms: value.contains("metadataPlatforms") ? value["metadataPlatforms"].strings : defaultMetadataPlatforms,
            metadataEnabled: value.contains("metadataEnabled") ? value["metadataEnabled"].strings : defaultMetadataPlatforms,
            sourceExclusions: decodeExclusions(value["sourceExclusions"].array, kind: "source"),
            recordingExclusions: decodeExclusions(value["recordingSourceExclusions"].array, kind: "recording"),
            feedback: feedback
        )
    }

    // MARK: - Catalog Source Area

    static func catalogSourceToMJ(_ record: MusicCatalogSourceAreaRecord) -> MJ {
        .obj([
            ("schema", .n(1)),
            ("revision", .n(record.revision)),
            ("platform", .string(record.platform))
        ])
    }

    static func catalogSourceFromMJ(_ value: MJ) -> MusicCatalogSourceAreaRecord {
        MusicCatalogSourceAreaRecord(
            revision: value["revision"].int,
            platform: value["platform"].string.isEmpty ? "wy" : value["platform"].string
        )
    }
}

extension MusicNativeLibraryReading {
    func songMJ(id: String) throws -> MJ? {
        guard let record = try song(id: id) else { return nil }
        let mj = try MusicPersistenceAdapter.songToMJ(record)
        try MusicLibraryValidation.validateSong(mj, id: id)
        return mj
    }
}

extension MusicNativeLibraryWriting {
    func saveSongMJ(_ song: MJ) throws {
        let id = song["songId"].string
        try MusicLibraryValidation.validateSong(song, id: id)
        let evidence = MusicModel.descriptor(song)
        let query = MusicRecordingIdentity.normalize(song["title"].string + " " + song["artist"].string)
        let record = try MusicPersistenceAdapter.songFromMJ(song, baselineDurationOverride: evidence.duration)
        try saveSong(record, matchKey: evidence.lookupKey, searchQuery: query)
    }

    func saveAreaMJ(_ area: String, value: MJ) throws {
        switch area {
        case "library":
            try saveLibrary(MusicPersistenceAdapter.libraryFromMJ(value))
        case "queue":
            try saveQueue(MusicPersistenceAdapter.queueFromMJ(value))
        case "history":
            try saveHistory(MusicPersistenceAdapter.historyFromMJ(value))
        case "playback":
            try savePlayback(MusicPersistenceAdapter.playbackFromMJ(value))
        case "catalogSource":
            try saveCatalogSource(MusicPersistenceAdapter.catalogSourceFromMJ(value))
        default:
            throw MusicServiceError.invalidRequest
        }
    }
}
