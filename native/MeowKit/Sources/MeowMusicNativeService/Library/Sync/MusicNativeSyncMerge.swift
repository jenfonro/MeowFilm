// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus collection.ts: input baselines, pending-intent precedence and
// anchor merging; platform snapshots never directly replace the local library.
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func syncMerge(_ round: inout MusicNativeSyncRound) throws {
        var lib = round.library, collections = lib["collections"].array
        for selection in round.request.selections {
            let platform = selection.platform.rawValue, session = round.sessions[platform]!, snapshot = round.snapshots[platform]!
            lib["accounts"][platform] = .string(session.accountID); round.ledger.scopes[platform] = session.scope
            for index in collections.indices {
                var c = collections[index]
                guard selection.kinds.contains(MusicLibrarySyncKind(rawValue: c["kind"].string)!) else { continue }
                let missing = c["bindings"].array.filter { binding in
                    binding["platform"].string == platform && binding["accountId"].string == session.accountID &&
                    binding["writable"].bool && !snapshot.collections.contains(where: { remote in remote.id == binding["id"].string })
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
                var index = collections.firstIndex { c in c["bindings"].array.contains {
                    $0["platform"].string == platform && $0["accountId"].string == session.accountID && $0["id"].string == remote.id
                } }
                if index == nil, remote.kind == .favorites { index = collections.firstIndex { $0["id"].string == "favorites" } }
                if index == nil {
                    let matches = collections.indices.filter {
                        let c = collections[$0]
                        return !c["deleted"].bool && c["kind"].string == "playlist" &&
                            MusicModel.norm(c["name"].string) == MusicModel.norm(remote.name) &&
                            c["creating"].fields.isEmpty && !round.ledger.fences.contains { $0.localID == c["id"].string }
                    }
                    if matches.count == 1 { index = matches[0] }
                }
                if index == nil {
                    try musicRequire(collections.count < 2000, .limit)
                    collections.append(MusicModel.freshCollection("playlist:\(lib["nextId"].int)", remote.name))
                    lib["nextId"] = .n(lib["nextId"].int + 1); index = collections.count - 1
                }
                var c = collections[index!], bindings = c["bindings"].array
                if !bindings.contains(where: { $0["platform"].string == platform && $0["accountId"].string == session.accountID && $0["id"].string == remote.id }) {
                    bindings.append(.obj([("platform", .string(platform)), ("accountId", .string(session.accountID)),
                        ("id", .string(remote.id)), ("writable", .bool(remote.writable)), ("baseline", .array([])),
                        ("map", .obj()), ("name", .string(remote.name))]))
                }
                c["bindings"] = .array(bindings); collections[index!] = c
            }
        }
        for index in collections.indices {
            var c = collections[index], bindings = c["bindings"].array
            let oldOrder = c["order"].strings
            var removed = Set<String>(), added = Set<String>(), sequences: [[String]] = []
            for bIndex in bindings.indices {
                var b = bindings[bIndex]
                guard let selection = round.request.selections.first(where: { $0.platform.rawValue == b["platform"].string }),
                      selection.kinds.contains(MusicLibrarySyncKind(rawValue: c["kind"].string)!),
                      let remote = round.snapshots[b["platform"].string]?.collections.first(where: { $0.id == b["id"].string }) else { continue }
                let oldMap = try MusicNativeSyncLogic.map(b, songs: lib["songs"])
                var mapping = oldMap, sequence: [String] = [], actualSongs = Set<String>()
                for member in remote.members {
                    let normalized = try MusicModel.normalize(.init(platform: selection.platform.rawValue, data: member.track))
                    let id = try upsert([normalized], library: &lib)[0]
                    mapping[member.occurrenceID] = .string(id); sequence.append(id); actualSongs.insert(id)
                }
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
    }

    func syncMapMissing(_ round: inout MusicNativeSyncRound) async throws {
        var attempted = Set<String>(), stopped = Set<String>()
        for c in round.library["collections"].array where !c["deleted"].bool {
            for selection in round.request.selections where selection.kinds.contains(MusicLibrarySyncKind(rawValue: c["kind"].string)!) {
                let platform = selection.platform.rawValue
                let bindings = c["bindings"].array.filter { $0["platform"].string == platform }
                if !bindings.isEmpty && bindings.allSatisfy({ !$0["writable"].bool }) { continue }
                if bindings.isEmpty, !c["locallyCreated"].bool, !c["bindings"].array.isEmpty,
                   c["bindings"].array.allSatisfy({ !$0["writable"].bool }) { continue }
                for id in c["order"].strings {
                    let song = round.library["songs"][id]
                    let existing = song["sources"].array.filter { $0["source"].string == platform }
                    if !existing.isEmpty && existing.allSatisfy(MusicNativeCandidateDiscoveryMerge.metadataComplete) {
                        continue
                    }
                    let cache = await sourceDiscoveryEntries(song)
                    try await syncCurrent(round)
                    if let cached = cache[platform], !cached.sources.isEmpty {
                        do {
                            let merged = mergeSourceDiscoveryCache(song, platform: platform, entry: cached)
                            if merged != song {
                                var match = merged
                                match["songId"] = .string(id)
                                _ = try upsert([match], library: &round.library)
                            }
                            if merged["sources"].array.contains(where: { $0["source"].string == platform }) { continue }
                        } catch {
                            // A conflicting cached locator has the same per-song
                            // outcome as a conflicting search result, not a blocked round.
                            round.issue("unmapped", platform: selection.platform, collection: c["id"].string)
                            continue
                        }
                    }
                    if !MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: song,
                        force: false, cache: cache[platform]) { continue }
                    // A platform failure stops network searches, not safe local
                    // mapping from already-known or compatible cached sources.
                    if stopped.contains(platform) { continue }
                    guard attempted.insert(platform + ":" + id).inserted else { continue }
                    try await syncCurrent(round)
                    do {
                        let query = song["title"].string + " " + song["artist"].string
                        let port = publicPlatforms
                        let result = try await work {
                            try await port.search(platform: platform, query: query, page: 1, limit: 30)
                        }
                        try await syncCurrent(round)
                        try musicRequire(result.total >= 0 && result.items.allSatisfy({ $0.platform == platform }), .upstreamInvalid)
                        let matches = try result.items.map(MusicModel.normalize).filter { candidate in
                            MusicModel.compatible(song, candidate) && song["sources"].array.allSatisfy { MusicModel.compatible($0, candidate) }
                        }
                        await recordSourceDiscovery(song, platform: platform, page: result)
                        try await syncCurrent(round)
                        if matches.count == 1 {
                            var match = matches[0]; match["songId"] = .string(id)
                            _ = try upsert([match], library: &round.library)
                        } else {
                            round.issue("unmapped", platform: selection.platform, collection: c["id"].string)
                        }
                    } catch {
                        try await syncCurrent(round)
                        let failure = MusicServiceError.sanitized(error)
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
