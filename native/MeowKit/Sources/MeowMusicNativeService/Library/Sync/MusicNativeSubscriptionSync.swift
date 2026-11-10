// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func syncMergeSubscriptions(_ round: inout MusicNativeSyncRound) throws {
        var references = round.library["networkReferences"].array
        for selection in round.request.selections where selection.playlists {
            let platform = selection.platform.rawValue, session = round.sessions[platform]!
            let subscriptions = round.snapshots[platform]!.subscriptions
            for remote in subscriptions {
                let id = MusicNativeNetworkPlaylists.id(platform: platform, remoteID: remote.remoteID)
                if !references.contains(where: { $0["id"].string == id }) {
                    try musicRequire(round.library["collections"].array.count + references.count < 2000, .limit)
                    var row = try MusicNativeNetworkPlaylists.row(.init(platform: .init(accountPlatform: selection.platform),
                        nativeID: remote.remoteID, title: remote.name, creator: remote.creator,
                        artwork: MusicArtworkURL.normalized(remote.artwork), count: remote.count))
                    row["revision"] = .n(round.library["revision"].int + 1)
                    row["pending"] = .bool(false)
                    references.append(row)
                }
            }
            for index in references.indices where references[index]["platform"].string == platform {
                var row = references[index]
                let previous = row, relation = row["relation"]
                try musicRequire(relation == .null || relation["accountId"].string == session.accountID, .targetChanged)
                let remote = subscriptions.first { $0.remoteID == row["remoteID"].string }
                let subscribed = remote != nil
                // A missing local row is never an unsubscribe. Only an explicit
                // pending local intent takes precedence over the official directory.
                if !row["pending"].bool { row["present"] = .bool(subscribed) }
                if row["present"].bool == subscribed { row["pending"] = .bool(false) }
                row["relation"] = .obj([("accountId", .string(session.accountID)),
                    ("baseline", .bool(subscribed)),
                    ("relationID", .string(remote?.relationID ?? relation["relationID"].string))])
                if let remote {
                    row["name"] = .string(remote.name)
                    if !remote.creator.isEmpty { row["creator"] = .string(remote.creator) }
                    if !remote.artwork.isEmpty { row["artwork"] = MusicModel.publicURL(.string(remote.artwork)) }
                    if let count = remote.count { row["count"] = .n(count) }
                }
                if ["present", "name", "creator", "artwork", "count"].contains(where: { row[$0] != previous[$0] }) {
                    row["revision"] = .n(row["revision"].int + 1)
                }
                references[index] = row
            }
        }
        round.library["networkReferences"] = .array(references)
    }

    func syncConfirmSubscription(_ fence: MusicNativeSyncFence, snapshot: MusicLibrarySyncSnapshot,
                                 round: inout MusicNativeSyncRound) throws {
        var references = round.library["networkReferences"].array
        guard let index = references.firstIndex(where: { $0["id"].string == fence.localID }),
              references[index]["platform"].string == fence.platform.rawValue,
              references[index]["remoteID"].string == fence.mutation.collectionID else {
            throw MusicServiceError.revisionConflict
        }
        var row = references[index]
        let remote = snapshot.subscriptions.first { $0.remoteID == row["remoteID"].string }
        row["relation"] = .obj([("accountId", .string(fence.accountID)),
            ("baseline", .bool(remote != nil)),
            ("relationID", .string(remote?.relationID ?? row["relation"]["relationID"].string))])
        if row["revision"].int == fence.localRevision,
           row["present"].bool == (fence.mutation.action == .subscribe) {
            row["pending"] = .bool(false)
        }
        references[index] = row; round.library["networkReferences"] = .array(references)
    }

    func syncPushSubscriptions(_ round: inout MusicNativeSyncRound) async throws {
        let ids = round.library["networkReferences"].array.map { $0["id"].string }
        for id in ids {
            try await syncCurrent(round)
            let row = round.library["networkReferences"].array.first { $0["id"].string == id }!
            guard row["pending"].bool,
                  let selection = round.request.selections.first(where: { $0.platform.rawValue == row["platform"].string && $0.playlists }),
                  !round.ledger.fences.contains(where: { $0.platform == selection.platform }) else { continue }
            let session = round.sessions[selection.platform.rawValue]!, snapshot = round.snapshots[selection.platform.rawValue]!
            let remote = snapshot.subscriptions.first { $0.remoteID == row["remoteID"].string }
            guard row["present"].bool != (remote != nil) else { continue }
            let action: MusicLibrarySyncMutation.Action = row["present"].bool ? .subscribe : .unsubscribe
            let mutation = MusicLibrarySyncMutation(action: action, collectionID: row["remoteID"].string,
                name: action == .subscribe ? row["name"].string : nil, directoryRevision: snapshot.revision,
                beforeSubscription: action == .unsubscribe ? remote : nil)
            _ = try await syncDispatch(mutation, local: row, session: session, selection: selection, round: &round)
        }
    }
}
