// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus collection.ts: input baselines, pending-intent precedence and
// anchor merging; platform snapshots never directly replace the local library.
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func syncMerge(_ round: inout MusicNativeSyncRound) throws {
        round.library["songs"] = try read().library["library"]["songs"]
        var lib = round.library, collections = lib["collections"].array
        var firstBindings = Set<String>()
        for selection in round.request.selections {
            let platform = selection.platform.rawValue, session = round.sessions[platform]!, snapshot = round.snapshots[platform]!
            for index in collections.indices {
                var c = collections[index]
                guard let kind = MusicLibrarySyncKind(rawValue: c["kind"].string), selection.kinds.contains(kind) else { continue }
                let missing = c["bindings"].array.filter { binding in
                    binding["platform"].string == platform && binding["accountId"].string == session.accountID &&
                    !snapshot.collections.contains(where: { remote in remote.id == binding["id"].string })
                }
                if c["kind"].string == "playlist", !missing.isEmpty,
                   !round.ledger.fences.contains(where: { $0.localID == c["id"].string }) {
                    if !c["deleted"].bool { c["revision"] = .n(c["revision"].int + 1) }
                    c["deleted"] = .bool(true)
                    // Only a complete selected-kind snapshot can retire a
                    // binding. Drop its acknowledgements together with it.
                    let keys = Set(missing.map(MusicNativeSyncLogic.bindingKey))
                    c["bindings"] = .array(c["bindings"].array.filter { !keys.contains(MusicNativeSyncLogic.bindingKey($0)) })
                    for (id, var intent) in c["intents"].fields where intent.contains("acknowledged") {
                        intent["acknowledged"] = .strings(intent["acknowledged"].strings.filter { !keys.contains($0) })
                        c["intents"][id] = intent
                    }
                    collections[index] = c
                }
            }
            for remote in snapshot.collections {
                var index = collections.firstIndex { c in c["kind"].string == remote.kind.rawValue && c["bindings"].array.contains {
                    $0["platform"].string == platform && $0["accountId"].string == session.accountID && $0["id"].string == remote.id
                } }
                if index == nil, remote.kind == .favorites { index = collections.firstIndex { $0["id"].string == "favorites" } }
                if index == nil {
                    let matches = collections.indices.filter {
                        let c = collections[$0]
                        return !c["deleted"].bool && c["kind"].string == "playlist" &&
                            MusicModel.norm(c["name"].string) == MusicModel.norm(remote.name) &&
                            !round.ledger.fences.contains { $0.localID == c["id"].string }
                    }
                    if matches.count == 1 { index = matches[0] }
                }
                if index == nil {
                    try musicRequire(lib["nextId"].isInteger && lib["nextId"].int > 0, .corruptDocument)
                    try musicRequire(lib["nextId"].int < 9_007_199_254_740_991, .limit)
                    let id = "playlist:\(lib["nextId"].int)"
                    try musicRequire(!collections.contains { $0["id"].string == id }, .corruptDocument)
                    collections.append(MusicModel.freshCollection(id, remote.name))
                    lib["nextId"] = .n(lib["nextId"].int + 1); index = collections.count - 1
                }
                var c = collections[index!], bindings = c["bindings"].array
                if !bindings.contains(where: { $0["platform"].string == platform && $0["accountId"].string == session.accountID && $0["id"].string == remote.id }) {
                    let binding = MJ.obj([("platform", .string(platform)), ("accountId", .string(session.accountID)),
                        ("id", .string(remote.id)), ("writable", .bool(remote.writable)), ("baseline", .array([])),
                        ("map", .obj()), ("name", .string(remote.name))])
                    bindings.append(binding); firstBindings.insert(MusicNativeSyncLogic.bindingKey(binding))
                }
                c["bindings"] = .array(bindings); collections[index!] = c
            }
        }
        for index in collections.indices {
            var c = collections[index], bindings = c["bindings"].array
            guard let kind = MusicLibrarySyncKind(rawValue: c["kind"].string),
                  round.request.selections.contains(where: { $0.kinds.contains(kind) }) else { continue }
            let oldOrder = c["order"].strings
            var removed = Set<String>(), added = Set<String>(), firstImported = Set<String>(), sequences: [[String]] = []
            for bIndex in bindings.indices {
                var b = bindings[bIndex]
                guard let selection = round.request.selections.first(where: { $0.platform.rawValue == b["platform"].string }),
                      selection.kinds.contains(kind),
                      let remote = round.snapshots[b["platform"].string]?.collections.first(where: { $0.id == b["id"].string }) else { continue }
                let oldMap = b["map"]
                var mapping = oldMap, sequence: [String] = [], actualSongs = Set<String>()
                for member in remote.members {
                    let id = try registeredSongID(.init(platform: selection.platform.rawValue, data: member.track), library: lib)
                    mapping[member.occurrenceID] = .string(id); sequence.append(id); actualSongs.insert(id)
                }
                if firstBindings.contains(MusicNativeSyncLogic.bindingKey(b)) { firstImported.formUnion(actualSongs) }
                let known = !oldMap.fields.isEmpty, previous = Set(b["baseline"].strings)
                func observable(_ id: String) -> Bool {
                    let intent = c["intents"][id]
                    return !intent["pending"].bool || intent["origin"].string != "local" ||
                        intent["acknowledged"].strings.contains(MusicNativeSyncLogic.bindingKey(b))
                }
                for native in b["baseline"].strings {
                    let id = oldMap[native].string
                    if !id.isEmpty, !actualSongs.contains(id), observable(id) { removed.insert(id) }
                }
                for member in remote.members {
                    let id = mapping[member.occurrenceID].string
                    if known, !previous.contains(member.occurrenceID),
                       !b["baseline"].strings.contains(where: { oldMap[$0].string == id }),
                       observable(id) { added.insert(id) }
                }
                if bindings.count == 1, c["name"] == b["name"], !c["locallyCreated"].bool { c["name"] = .string(remote.name) }
                b["baseline"] = .strings(remote.members.map(\.occurrenceID)); b["map"] = mapping
                b["name"] = .string(remote.name); b["writable"] = .bool(remote.writable); bindings[bIndex] = b
                sequences.append(MusicNativeSyncLogic.unique(sequence))
            }
            for id in removed.union(added) {
                c["intents"][id] = .obj([("present", .bool(!removed.contains(id))), ("pending", .bool(true)), ("origin", .string("remote"))])
            }
            // A new binding's actual members win this round's older cancellations,
            // including remote deletions collected from other bindings in either order.
            for id in firstImported where c["intents"][id]["present"] == .bool(false) {
                c["intents"][id] = .obj([("present", .bool(true)), ("pending", .bool(true)), ("origin", .string("remote"))])
            }
            var order = oldOrder
            if !c["manualOrder"].bool, !sequences.isEmpty {
                order = MusicNativeSyncLogic.mergeOrder(sequences.reduce([], MusicNativeSyncLogic.mergeOrder), order)
            } else { for sequence in sequences { order = MusicNativeSyncLogic.mergeOrder(order, sequence) } }
            order = order.filter { c["intents"][$0]["present"] != .bool(false) }
            for (id, intent) in c["intents"].fields where intent["present"].bool && !order.contains(id) { order.append(id) }
            c["order"] = .strings(order); c["bindings"] = .array(bindings)
            if order != oldOrder { c["revision"] = .n(c["revision"].int + 1) }
            collections[index] = c
        }
        lib["collections"] = .array(collections); round.library = lib
        try syncMergeSubscriptions(&round)
    }

    func syncMapMissing(_ round: inout MusicNativeSyncRound) async throws {
        try await syncCurrent(round)
        var attempted = Set<String>(), stopped = Set<String>()
        for c in round.library["collections"].array where !c["deleted"].bool {
            guard let kind = MusicLibrarySyncKind(rawValue: c["kind"].string) else { continue }
            for selection in round.request.selections where selection.kinds.contains(kind) {
                let platform = selection.platform.rawValue
                for id in c["order"].strings {
                    let song = try repositorySong(id, read())
                    let excluded = try recordingSourceExclusions(song)
                    let existing = song["sources"].array.filter { $0["source"].string == platform }
                    if existing.contains(where: { !excluded.contains($0["sourceKey"].string) }) { continue }
                    guard MusicNativeCandidateDiscoveryMerge.searchable(song) else {
                        round.issue("unmapped", platform: selection.platform, collection: c["id"].string)
                        continue
                    }
                    // Sync rechecks an empty platform after 30 days, not every round.
                    let state = try await sourceDiscoveryState(song)
                    let cache = state.current(window: MusicSourceDiscoveryWindow.librarySync)
                    try await syncCurrent(round)
                    if !MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: song,
                        force: false, cache: cache[platform]) ||
                        state.hasCurrentCheck(platform, window: MusicSourceDiscoveryWindow.librarySync) { continue }
                    // A platform failure stops new searches, not use of sources
                    // already registered in the unique song repository.
                    if stopped.contains(platform) { continue }
                    guard attempted.insert(platform + ":" + id).inserted else { continue }
                    try await syncCurrent(round)
                    let query = sourceDiscoveryQuery(song)
                    let observation = beginSourceDiscoveryObservation(platform: platform, query: query)
                    do {
                        let port = publicPlatforms
                        let result = try await work {
                            try await port.search(platform: platform, query: query, page: 1, limit: 30)
                        }
                        try await syncCurrent(round)
                        try await recordSourceDiscovery(song, platform: platform, page: result,
                            observation: finishSourceDiscoveryObservation(observation))
                        try await syncCurrent(round)
                        let current = try repositorySong(id, read())
                        if !current["sources"].array.contains(where: {
                            $0["source"].string == platform && !excluded.contains($0["sourceKey"].string)
                        }) {
                            round.issue("unmapped", platform: selection.platform, collection: c["id"].string)
                        }
                    } catch {
                        try await syncCurrent(round)
                        let failure = MusicServiceError.sanitized(error)
                        if failure != .cancelled {
                            try await recordSourceDiscovery(song, platform: platform, rows: [],
                                observation: finishSourceDiscoveryObservation(observation, failure: failure))
                        }
                        if failure.isAuthenticationFailure || failure == .verificationRequired { throw error }
                        if Self.syncPlatformSearchFailure(failure) {
                            if stopped.insert(platform).inserted { round.issue("unmapped", platform: selection.platform) }
                        } else {
                            round.issue("unmapped", platform: selection.platform, collection: c["id"].string)
                        }
                    }
                }
            }
        }
    }

    private static func syncPlatformSearchFailure(_ failure: MusicServiceError) -> Bool {
        switch failure {
        case .rateLimited, .networkOffline, .connectionFailed, .timeout, .upstreamFailed,
             .unavailable, .unsupported:
            return true
        default:
            return false
        }
    }
}
