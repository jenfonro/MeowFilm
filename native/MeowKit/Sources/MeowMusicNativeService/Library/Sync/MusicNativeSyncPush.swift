// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func syncConfirm(_ fence: MusicNativeSyncFence, snapshot: MusicLibrarySyncSnapshot,
                     round: inout MusicNativeSyncRound) throws {
        if fence.mutation.action.isSubscription {
            try syncConfirmSubscription(fence, snapshot: snapshot, round: &round)
            return
        }
        guard let index = round.library["collections"].array.firstIndex(where: { $0["id"].string == fence.localID }) else {
            throw MusicServiceError.revisionConflict
        }
        var collections = round.library["collections"].array, c = collections[index], bindings = c["bindings"].array
        guard let target = fence.mutation.action == .create ? fence.acknowledgedID : fence.mutation.collectionID else {
            throw MusicServiceError.commitUnconfirmed
        }
        let bindingIndex = bindings.firstIndex {
            $0["platform"].string == fence.platform.rawValue && $0["accountId"].string == fence.accountID && $0["id"].string == target
        }
        if fence.mutation.action == .delete {
            if let bindingIndex {
                let key = MusicNativeSyncLogic.bindingKey(bindings[bindingIndex])
                bindings.remove(at: bindingIndex)
                for (id, var intent) in c["intents"].fields where intent.contains("acknowledged") {
                    intent["acknowledged"] = .strings(intent["acknowledged"].strings.filter { $0 != key })
                    c["intents"][id] = intent
                }
            }
        } else {
            guard let remote = snapshot.collections.first(where: { $0.id == target }) else { throw MusicServiceError.commitUnconfirmed }
            var b = bindingIndex.map { bindings[$0] } ?? .obj([("platform", .string(fence.platform.rawValue)),
                ("accountId", .string(fence.accountID)), ("id", .string(target)), ("map", .obj())])
            var mapping = b["map"]
            for member in remote.members {
                let song = try MusicModel.normalize(.init(platform: fence.platform.rawValue, data: member.track))
                let id = try upsert([song], library: &round.library)[0]
                mapping[member.occurrenceID] = .string(id)
            }
            b["map"] = mapping; b["baseline"] = .strings(remote.members.map(\.occurrenceID))
            b["name"] = .string(remote.name); b["writable"] = .bool(remote.writable)
            if let bindingIndex { bindings[bindingIndex] = b } else { bindings.append(b) }
            if fence.mutation.action == .create, c["revision"].int == fence.localRevision, let name = fence.acknowledgedName {
                c["name"] = .string(name)
            }
            if c["revision"].int == fence.localRevision {
                let members = Set(remote.members.map { mapping[$0.occurrenceID].string })
                for (id, var intent) in c["intents"].fields where members.contains(id) == intent["present"].bool {
                    intent["acknowledged"] = .strings(MusicNativeSyncLogic.unique(intent["acknowledged"].strings + [MusicNativeSyncLogic.bindingKey(b)]))
                    c["intents"][id] = intent
                }
            }
        }
        c["bindings"] = .array(bindings); collections[index] = c; round.library["collections"] = .array(collections)
    }

    func syncDispatch(_ mutation: MusicLibrarySyncMutation, local: MJ, session: MusicLibrarySyncSession,
                      selection: MusicLibrarySyncSelection, round: inout MusicNativeSyncRound) async throws -> Bool {
        try await syncCurrent(round)
        try musicRequire(round.ledger.fences.count < 512, .limit)
        var fence = MusicNativeSyncFence(platform: session.platform, accountID: session.accountID, scope: session.scope,
            localID: local["id"].string, localRevision: local["revision"].int, mutation: mutation)
        round.ledger.fences.append(fence)
        // Durable "dispatched" fence precedes the external boundary. Even an
        // ambiguous document CAS prevents calling the provider.
        do {
            try syncCheckpoint(&round)
            try await syncCurrent(round)
        } catch {
            // The provider has not been called, so this checkpoint is not an
            // unknown write, even if cancellation raced the durable fence.
            round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
            try await syncDiscardNotDispatched(fence, epoch: round.epoch)
            throw error
        }
        do {
            let result = try await round.port.writeLibrarySync(session, mutation: mutation)
            switch result {
            case .notDispatched(let reason):
                round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
                do {
                    try await syncCurrent(round)
                    round.issue(MusicNativeSyncLogic.code(reason), platform: session.platform, collection: local["id"].string)
                    try syncCheckpoint(&round)
                } catch {
                    try await syncDiscardNotDispatched(fence, epoch: round.epoch)
                    throw error
                }
                return false
            case .acknowledged(let id, let name):
                try await syncCurrent(round)
                if mutation.action == .create {
                    guard let id, !id.isEmpty,
                          round.snapshots[session.platform.rawValue]?.collections.contains(where: { $0.id == id }) == false else {
                        throw MusicServiceError.commitUnconfirmed
                    }
                    fence.acknowledgedID = id; fence.acknowledgedName = name
                    let index = round.ledger.fences.firstIndex { $0.mutation.id == mutation.id }!
                    round.ledger.fences[index] = fence
                    try syncCheckpoint(&round) // Persist the exact ID before a separate read can fail.
                }
            }
        } catch {
            try await syncCurrent(round)
            // Never retry the write, even when the server likely rejected it.
            // An interrupted/unknown request can only be settled by reading.
        }
        let snapshot = try await syncSnapshot(session, selection: selection, round: round)
        round.snapshots[session.platform.rawValue] = snapshot
        if fence.confirmed(in: snapshot) {
            try syncConfirm(fence, snapshot: snapshot, round: &round)
            round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
            try await syncCurrent(round); try syncCheckpoint(&round)
            return true
        }
        round.issue("unconfirmed", platform: session.platform, collection: local["id"].string)
        try syncCheckpoint(&round); return false
    }

    private func syncDiscardNotDispatched(_ fence: MusicNativeSyncFence, epoch own: UUID) async throws {
        // Discharge only a proven no-send operation. The old round may be
        // cancelled or its settings/local revision replaced, so never write
        // its library snapshot back. This owner task still checks the exact
        // access epoch and protection gate; it cannot reopen revoked storage.
        try await Task {
            try check(own)
            let ledger = try MusicNativeSyncLedger.load(read().library)
            guard ledger.fences.contains(where: { $0.mutation.id == fence.mutation.id }) else { return }
            try transaction(["library"]) { document in
                var ledger = try MusicNativeSyncLedger.load(document.library)
                guard let index = ledger.fences.firstIndex(where: { $0.mutation.id == fence.mutation.id }) else { return }
                let stored = ledger.fences[index]
                try musicRequire(stored.platform == fence.platform && stored.accountID == fence.accountID &&
                    stored.scope == fence.scope && stored.localID == fence.localID &&
                    stored.localRevision == fence.localRevision && stored.mutation == fence.mutation, .revisionConflict)
                ledger.fences.remove(at: index)
                document.library["sync"] = try ledger.json()
                document.library["revision"] = .n(document.library["revision"].int + 1)
            }
        }.value
    }

    func syncPush(_ round: inout MusicNativeSyncRound) async throws {
        let ids = round.library["collections"].array.map { $0["id"].string }
        for id in ids {
            for selection in round.request.selections {
                let platform = selection.platform.rawValue, session = round.sessions[platform]!
                if round.ledger.fences.contains(where: { $0.platform == selection.platform && $0.localID == id }) { continue }
                var dispatched = Set<String>()
                while true {
                    try await syncCurrent(round)
                    let c = round.library["collections"].array.first { $0["id"].string == id }!
                    guard selection.kinds.contains(MusicLibrarySyncKind(rawValue: c["kind"].string)!) else { break }
                    let bindings = c["bindings"].array.filter { $0["platform"].string == platform && $0["accountId"].string == session.accountID }
                    if bindings.isEmpty {
                        if c["deleted"].bool || c["kind"].string == "favorites" { break }
                        let mutation = MusicLibrarySyncMutation(action: .create, name: c["name"].string,
                            directoryRevision: round.snapshots[platform]!.revision)
                        let mutationKey = MusicNativeSyncLogic.mutationKey(mutation)
                        guard dispatched.insert(mutationKey).inserted else {
                            round.issue("stalled", platform: selection.platform, collection: id)
                            break
                        }
                        if !(try await syncDispatch(mutation, local: c, session: session, selection: selection, round: &round)) { break }
                        continue
                    }
                    var candidate: MusicLibrarySyncMutation?
                    for binding in bindings {
                        guard let remote = round.snapshots[platform]?.collections.first(where: { $0.id == binding["id"].string }) else { continue }
                        candidate = try syncPlan(local: c, binding: binding, remote: remote, selection: selection, round: &round)
                        if candidate != nil { break }
                    }
                    guard let candidate else { break }
                    guard dispatched.insert(MusicNativeSyncLogic.mutationKey(candidate)).inserted else {
                        round.issue("stalled", platform: selection.platform, collection: id)
                        break
                    }
                    if !(try await syncDispatch(candidate, local: c, session: session, selection: selection, round: &round)) { break }
                }
            }
        }
        try await syncPushSubscriptions(&round)
    }

    func syncPlan(local c: MJ, binding: MJ, remote: MusicLibrarySyncCollection,
                  selection: MusicLibrarySyncSelection, round: inout MusicNativeSyncRound) throws -> MusicLibrarySyncMutation? {
        func mutation(_ action: MusicLibrarySyncMutation.Action, name: String? = nil,
                      members: [MusicLibrarySyncMember] = []) -> MusicLibrarySyncMutation {
            .init(action: action, collectionID: remote.id, name: name, before: remote, members: members)
        }
        if c["deleted"].bool { return remote.canDelete ? mutation(.delete) : nil }
        if c["name"].string != remote.name, remote.canRename { return mutation(.rename, name: c["name"].string) }
        let mapping = binding["map"]
        var desired: [MusicLibrarySyncMember] = []
        for id in c["order"].strings {
            let existing = remote.members.filter { mapping[$0.occurrenceID].string == id || mapping[$0.nativeID].string == id }
            if !existing.isEmpty { desired += existing; continue }
            let song = round.library["songs"][id]
            if let source = song["sources"].array.first(where: {
                   $0["source"].string == selection.platform.rawValue
               }),
               let locator = try MusicModel.sources(song).first(where: { $0.sourceKey == source["sourceKey"].string }) {
                let data = MusicModel.sourceData(source)
                desired.append(.init(nativeID: locator.nativeId, occurrenceID: locator.nativeId, track: data.bytes))
            } else { round.issue("unmapped", platform: selection.platform, collection: c["id"].string) }
        }
        let projected = Set(desired.map(\.occurrenceID))
        for member in remote.members where !projected.contains(member.occurrenceID) {
            let id = mapping[member.occurrenceID].string
            if id.isEmpty || c["intents"][id]["present"] != .bool(false) { desired.append(member) }
        }
        let present = Set(remote.members.map(\.nativeID)), wanted = Set(desired.map(\.nativeID))
        var seen = Set<String>()
        let removals = remote.members.filter { !wanted.contains($0.nativeID) && seen.insert($0.nativeID).inserted }
        if !removals.isEmpty {
            let limit = selection.platform == .netease && remote.kind == .favorites ? 1 : 100
            return mutation(.remove, members: Array(removals.prefix(limit)))
        }
        seen = []
        let additions = desired.filter { !present.contains($0.nativeID) && seen.insert($0.nativeID).inserted }
        if !additions.isEmpty {
            let limit = selection.platform == .netease && remote.kind == .favorites ? 1 : 100
            return mutation(.add, members: Array(additions.prefix(limit)))
        }
        try musicRequire(desired.map(\.occurrenceID).sorted() == remote.members.map(\.occurrenceID).sorted(), .revisionConflict)
        func logical(_ members: [MusicLibrarySyncMember]) -> [String] {
            MusicNativeSyncLogic.unique(members.map { mapping[$0.occurrenceID].isString ? mapping[$0.occurrenceID].string : $0.nativeID })
        }
        if logical(desired) == logical(remote.members) { return nil } // Preserve A,B,A occurrences.
        if desired.map(\.occurrenceID) != remote.members.map(\.occurrenceID) {
            if !remote.canOrder { round.issue("orderUnsupported", platform: selection.platform, collection: c["id"].string); return nil }
            return mutation(.order, members: desired)
        }
        return nil
    }

    func syncAcknowledge(_ round: inout MusicNativeSyncRound) {
        var collections = round.library["collections"].array
        for index in collections.indices {
            var c = collections[index]
            let targets = round.request.selections.filter { $0.kinds.contains(MusicLibrarySyncKind(rawValue: c["kind"].string)!) }
            let bindings = c["bindings"].array.filter { b in targets.contains { $0.platform.rawValue == b["platform"].string } }
            let unresolved = round.ledger.fences.contains { $0.localID == c["id"].string }
            for (id, var intent) in c["intents"].fields {
                var confirmed: [String] = []
                for b in bindings {
                    let ids = b["baseline"].strings.map { b["map"][$0].string }
                    if ids.contains(id) == intent["present"].bool { confirmed.append(MusicNativeSyncLogic.bindingKey(b)) }
                }
                intent["acknowledged"] = .strings(MusicNativeSyncLogic.unique(intent["acknowledged"].strings + confirmed))
                let allTargets = targets.allSatisfy { selection in bindings.contains { $0["platform"].string == selection.platform.rawValue } }
                if !unresolved, !targets.isEmpty,
                   c["deleted"].bool && bindings.isEmpty || !bindings.isEmpty && allTargets && confirmed.count == bindings.count {
                    intent["pending"] = .bool(false)
                }
                c["intents"][id] = intent
            }
            collections[index] = c
        }
        round.library["collections"] = .array(collections)
    }
}
