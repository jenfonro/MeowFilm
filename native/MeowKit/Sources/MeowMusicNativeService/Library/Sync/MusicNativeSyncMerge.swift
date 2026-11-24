// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus collection.ts: input baselines, pending-intent precedence and
// anchor merging; platform snapshots never directly replace the local library.
import Foundation
import MeowDebug
import MeowMusicFeature

extension NativeMusicService {
    func syncProtectPull(_ round: inout MusicNativeSyncRound) throws -> Bool {
        if round.request.confirmDestructive { return true }
        var blocked = false
        for selection in round.request.selections {
            let platform = selection.platform.rawValue
            guard let session = round.sessions[platform], let snapshot = round.snapshots[platform] else { continue }
            var knownBindings: [(collection: MusicCollectionRecord, binding: MusicCollectionBindingRecord)] = []
            for c in round.library.collections where !c.deleted {
                guard let kind = MusicLibrarySyncKind(rawValue: c.kind), selection.kinds.contains(kind) else { continue }
                for b in c.bindings where b.platform == platform && b.accountID == session.accountID {
                    knownBindings.append((c, b))
                }
            }
            let missing = knownBindings.filter { pair in
                !snapshot.collections.contains(where: { $0.id == pair.binding.remoteID })
            }
            if (!missing.isEmpty && missing.count == knownBindings.count) || missing.count >= 20 {
                let code = missing.count == knownBindings.count ? "emptyRemote" : "bulkRemoteDelete"
                round.issue(code, platform: selection.platform)
                blocked = true
            }
            var remoteSourceKeys = Set<String>()
            var memberKeysByCollection: [String: [(occurrenceID: String, sourceKey: String)]] = [:]
            for (_, b) in knownBindings {
                guard let remote = snapshot.collections.first(where: { $0.id == b.remoteID }),
                      memberKeysByCollection[remote.id] == nil else { continue }
                let pairs = try remote.members.map { member -> (occurrenceID: String, sourceKey: String) in
                    let key = try MusicModel.sourceKey(fromTrackData: .init(platform: platform, data: member.track))
                    remoteSourceKeys.insert(key)
                    return (member.occurrenceID, key)
                }
                memberKeysByCollection[remote.id] = pairs
            }
            let ownersBySourceKey: [String: String] = remoteSourceKeys.isEmpty ? [:] : try repositoryReader.sourceOwners(keys: remoteSourceKeys)
            for (c, b) in knownBindings {
                guard let remote = snapshot.collections.first(where: { $0.id == b.remoteID }) else { continue }
                let oldMap = Dictionary(uniqueKeysWithValues: b.map.map { ($0.remoteEntryID, $0.songID) })
                var actualSongs = Set<String>()
                var actualOccurrences = Set<String>()
                for pair in memberKeysByCollection[remote.id] ?? [] {
                    actualOccurrences.insert(pair.occurrenceID)
                    if let songID = ownersBySourceKey[pair.sourceKey], !songID.isEmpty {
                        actualSongs.insert(songID)
                    } else if let mapped = oldMap[pair.occurrenceID], !mapped.isEmpty {
                        actualSongs.insert(mapped)
                    }
                }
                var removedSongs = Set<String>()
                var unmappedRemoved = 0
                for occurrence in b.baseline {
                    if let songID = oldMap[occurrence], !songID.isEmpty {
                        if !actualSongs.contains(songID) { removedSongs.insert(songID) }
                    } else if !actualOccurrences.contains(occurrence) {
                        unmappedRemoved += 1
                    }
                }
                let removedCount = removedSongs.count + unmappedRemoved
                if (!b.baseline.isEmpty && remote.members.isEmpty) || removedCount >= 20 {
                    let code = remote.members.isEmpty ? "emptyRemote" : "bulkRemoteDelete"
                    round.issue(code, platform: selection.platform, collection: c.id)
                    blocked = true
                }
            }
        }
        if blocked {
            DebugRuntime.log(.musicLibrary, sub: "sync", event: "destructiveChangeBlocked", level: .warn, data: .object([
                "issues": .array(round.ledger.issues.map { .string($0.code) })
            ]))
        }
        return !blocked
    }

    func syncMerge(_ round: inout MusicNativeSyncRound) throws {
        var lib = round.library, collections = lib.collections
        var firstBindings = Set<String>()
        for selection in round.request.selections {
            let platform = selection.platform.rawValue, session = round.sessions[platform]!, snapshot = round.snapshots[platform]!
            for index in collections.indices {
                var c = collections[index]
                guard let kind = MusicLibrarySyncKind(rawValue: c.kind), selection.kinds.contains(kind) else { continue }
                let missing = c.bindings.filter { binding in
                    binding.platform == platform && binding.accountID == session.accountID &&
                    !snapshot.collections.contains(where: { remote in remote.id == binding.remoteID })
                }
                if c.kind == "playlist", !missing.isEmpty,
                   !round.ledger.fences.contains(where: { $0.localID == c.id }) {
                    if !c.deleted { c.revision += 1 }
                    c.deleted = true
                    let keys = Set(missing.map(MusicNativeSyncLogic.bindingKey))
                    c.bindings.removeAll { keys.contains(MusicNativeSyncLogic.bindingKey($0)) }
                    for i in c.intents.indices where !c.intents[i].acknowledged.isEmpty {
                        c.intents[i].acknowledged.removeAll { keys.contains($0) }
                    }
                    collections[index] = c
                }
            }
            for remote in snapshot.collections {
                var index = collections.firstIndex { c in c.kind == remote.kind.rawValue && c.bindings.contains {
                    $0.platform == platform && $0.accountID == session.accountID && $0.remoteID == remote.id
                } }
                if index == nil, remote.kind == .favorites { index = collections.firstIndex { $0.id == "favorites" } }
                if index == nil {
                    let matches = collections.indices.filter {
                        let c = collections[$0]
                        return !c.deleted && c.kind == "playlist" &&
                            MusicModel.norm(c.name) == MusicModel.norm(remote.name) &&
                            !round.ledger.fences.contains { $0.localID == c.id }
                    }
                    if matches.count == 1 { index = matches[0] }
                }
                if index == nil {
                    try musicRequire(lib.nextID > 0 && lib.nextID <= musicMaxSafeInteger, .corruptStorage)
                    try musicRequire(lib.nextID < musicMaxSafeInteger, .limit)
                    let id = "playlist:\(lib.nextID)"
                    try musicRequire(!collections.contains { $0.id == id }, .corruptStorage)
                    collections.append(MusicModel.freshCollection(id, remote.name))
                    lib.nextID += 1
                    index = collections.count - 1
                }
                var c = collections[index!]
                if !c.bindings.contains(where: { $0.platform == platform && $0.accountID == session.accountID && $0.remoteID == remote.id }) {
                    let binding = MusicCollectionBindingRecord(
                        platform: platform,
                        accountID: session.accountID,
                        remoteID: remote.id,
                        writable: remote.writable,
                        name: remote.name,
                        baseline: [],
                        map: []
                    )
                    c.bindings.append(binding)
                    firstBindings.insert(MusicNativeSyncLogic.bindingKey(binding))
                }
                collections[index!] = c
            }
        }
        for index in collections.indices {
            var c = collections[index]
            guard let kind = MusicLibrarySyncKind(rawValue: c.kind),
                  round.request.selections.contains(where: { $0.kinds.contains(kind) }) else { continue }
            let oldOrder = c.order
            let oldName = c.name
            var anyKnownLocalRename = false
            var remoteRenames: [String] = []
            var removed = Set<String>(), added = Set<String>(), firstImported = Set<String>(), sequences: [[String]] = []
            for bIndex in c.bindings.indices {
                var b = c.bindings[bIndex]
                let bindingKey = MusicNativeSyncLogic.bindingKey(b)
                let known = !firstBindings.contains(bindingKey)
                if known, c.kind == "playlist", b.name != c.name {
                    anyKnownLocalRename = true
                }
                guard let selection = round.request.selections.first(where: { $0.platform.rawValue == b.platform }),
                      selection.kinds.contains(kind),
                      let remote = round.snapshots[b.platform]?.collections.first(where: { $0.id == b.remoteID }) else { continue }
                let oldMap = Dictionary(uniqueKeysWithValues: b.map.map { ($0.remoteEntryID, $0.songID) })
                var mapping = oldMap, sequence: [String] = [], actualSongs = Set<String>()
                for member in remote.members {
                    let id = try registeredSongID(.init(platform: selection.platform.rawValue, data: member.track))
                    mapping[member.occurrenceID] = id; sequence.append(id); actualSongs.insert(id)
                }
                if firstBindings.contains(bindingKey) { firstImported.formUnion(actualSongs) }
                let previous = Set(b.baseline)
                if known, c.kind == "playlist", b.name == c.name, !remote.name.isEmpty, remote.name != b.name {
                    remoteRenames.append(remote.name)
                }
                func observable(_ id: String) -> Bool {
                    guard let intent = c.intent(for: id) else { return true }
                    return !intent.pending || intent.origin != "local" ||
                        intent.acknowledged.contains(bindingKey)
                }
                for native in b.baseline {
                    if let id = oldMap[native], !id.isEmpty, !actualSongs.contains(id), observable(id) {
                        removed.insert(id)
                    }
                }
                for member in remote.members {
                    if let id = mapping[member.occurrenceID],
                       known, !previous.contains(member.occurrenceID),
                       !b.baseline.contains(where: { oldMap[$0] == id }),
                       observable(id) {
                        added.insert(id)
                    }
                }
                b.baseline = remote.members.map(\.occurrenceID)
                b.map = mapping.keys.sorted().map { MusicCollectionBindingMapEntry(remoteEntryID: $0, songID: mapping[$0]!) }
                b.name = remote.name
                b.writable = remote.writable
                c.bindings[bIndex] = b
                sequences.append(MusicNativeSyncLogic.unique(sequence))
            }
            if c.kind == "playlist", !anyKnownLocalRename, !remoteRenames.isEmpty,
               remoteRenames.allSatisfy({ $0 == remoteRenames[0] }) {
                c.name = remoteRenames[0]
            }
            for id in removed.union(added) {
                c.setIntent(MusicCollectionIntentRecord(
                    intentKey: id,
                    present: !removed.contains(id),
                    pending: true,
                    origin: "remote",
                    acknowledged: []
                ))
            }
            for id in firstImported where c.intent(for: id)?.present == false {
                c.setIntent(MusicCollectionIntentRecord(
                    intentKey: id,
                    present: true,
                    pending: true,
                    origin: "remote",
                    acknowledged: []
                ))
            }
            var order = oldOrder
            if !c.manualOrder, !sequences.isEmpty {
                order = MusicNativeSyncLogic.mergeOrder(sequences.reduce([], MusicNativeSyncLogic.mergeOrder), order)
            } else { for sequence in sequences { order = MusicNativeSyncLogic.mergeOrder(order, sequence) } }
            order = order.filter { c.intent(for: $0)?.present != false }
            for intent in c.intents where intent.present && !order.contains(intent.intentKey) {
                order.append(intent.intentKey)
            }
            c.order = order
            if order != oldOrder || c.name != oldName { c.revision += 1 }
            collections[index] = c
        }
        lib.collections = collections
        round.library = lib
        try syncMergeSubscriptions(&round)
    }

    func syncMapMissing(_ round: inout MusicNativeSyncRound) async throws {
        try await syncCurrent(round)
        let candidateOrderIDs = round.library.collections.flatMap { collection -> [String] in
            guard !collection.deleted, let kind = MusicLibrarySyncKind(rawValue: collection.kind),
                  round.request.selections.contains(where: { $0.kinds.contains(kind) }) else { return [] }
            return collection.order
        }
        var cachedSongs: [String: MusicSongRecord] = candidateOrderIDs.isEmpty ? [:] : try repositorySongMap(candidateOrderIDs, read())
        var attempted = Set<String>(), stopped = Set<String>()
        for c in round.library.collections where !c.deleted {
            guard let kind = MusicLibrarySyncKind(rawValue: c.kind) else { continue }
            for selection in round.request.selections where selection.kinds.contains(kind) {
                let platform = selection.platform.rawValue
                for id in c.order {
                    guard let song = cachedSongs[id] else { throw MusicServiceError.notFound }
                    if try sourceExcluded(song, provider: "official:" + platform, platform: platform) {
                        round.issue("unmapped", platform: selection.platform, collection: c.id)
                        continue
                    }
                    let excluded = try recordingSourceExclusions(song)
                    let existing = song.sources.filter { $0.platform == platform }
                    if existing.contains(where: { !excluded.contains($0.sourceKey) }) { continue }
                    guard MusicSourceDiscoveryEligibility.searchable(song) else {
                        round.issue("unmapped", platform: selection.platform, collection: c.id)
                        continue
                    }
                    let state = try await sourceDiscoveryState(song)
                    let cache = state.current(window: MusicSourceDiscoveryWindow.librarySync)
                    try await syncCurrent(round)
                    if !MusicSourceDiscoveryEligibility.needsPlatform(platform, song: song,
                        force: false, cache: cache[platform]) ||
                        state.hasCurrentCheck(platform, window: MusicSourceDiscoveryWindow.librarySync) { continue }
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
                        cachedSongs[id] = current
                        let currentPlatformExcluded = try sourceExcluded(current, provider: "official:" + platform, platform: platform)
                        let currentExcluded = try recordingSourceExclusions(current)
                        if currentPlatformExcluded || !current.sources.contains(where: {
                            $0.platform == platform && !currentExcluded.contains($0.sourceKey)
                        }) {
                            round.issue("unmapped", platform: selection.platform, collection: c.id)
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
                            round.issue("unmapped", platform: selection.platform, collection: c.id)
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
