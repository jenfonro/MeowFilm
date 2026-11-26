// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    func syncMergeSubscriptions(_ round: inout MusicNativeSyncRound) throws {
        var references = round.library.networkReferences
        for selection in round.request.selections where selection.playlists {
            let platform = selection.platform.rawValue, session = round.sessions[platform]!
            let subscriptions = round.snapshots[platform]!.subscriptions
            for remote in subscriptions {
                let id = MusicNativeNetworkPlaylists.id(platform: platform, remoteID: remote.remoteID)
                if !references.contains(where: { $0.id == id }) {
                    var row = try MusicNativeNetworkPlaylists.row(.init(platform: .init(accountPlatform: selection.platform),
                        nativeID: remote.remoteID, title: remote.name, creator: remote.creator,
                        artwork: MusicArtworkURL.normalized(remote.artwork), count: remote.count))
                    row.revision = round.library.libraryRevision + 1
                    row.pending = false
                    references.append(row)
                }
            }
            for index in references.indices where references[index].platform == platform {
                var row = references[index]
                let previous = row
                try musicRequire(row.relation == nil || row.relation?.accountID == session.accountID, .targetChanged)
                let remote = subscriptions.first { $0.remoteID == row.remoteID }
                let subscribed = remote != nil
                if !row.pending { row.present = subscribed }
                if row.present == subscribed { row.pending = false }
                row.relation = MusicNetworkReferenceRelationRecord(
                    accountID: session.accountID,
                    baseline: subscribed,
                    relationID: remote?.relationID ?? row.relation?.relationID ?? "",
                    locator: row.relation?.locator
                )
                if let remote {
                    row.name = remote.name
                    if !remote.creator.isEmpty { row.creator = remote.creator }
                    if !remote.artwork.isEmpty { row.artwork = MusicModel.publicURLString(remote.artwork) }
                    if let count = remote.count { row.count = count }
                }
                if row.present != previous.present || row.name != previous.name ||
                    row.creator != previous.creator || row.artwork != previous.artwork ||
                    row.count != previous.count {
                    row.revision += 1
                }
                references[index] = row
            }
        }
        round.library.networkReferences = references
    }

    func syncConfirmSubscription(_ fence: MusicNativeSyncFence, snapshot: MusicLibrarySyncSnapshot,
                                 round: inout MusicNativeSyncRound) throws {
        guard let index = round.library.networkReferences.firstIndex(where: { $0.id == fence.localID }),
              round.library.networkReferences[index].platform == fence.platform.rawValue,
              round.library.networkReferences[index].remoteID == fence.mutation.collectionID else {
            throw MusicServiceError.revisionConflict
        }
        var row = round.library.networkReferences[index]
        let remote = snapshot.subscriptions.first { $0.remoteID == row.remoteID }
        row.relation = MusicNetworkReferenceRelationRecord(
            accountID: fence.accountID,
            baseline: remote != nil,
            relationID: remote?.relationID ?? row.relation?.relationID ?? "",
            locator: row.relation?.locator
        )
        if row.revision == fence.localRevision,
           row.present == (fence.mutation.action == .subscribe) {
            row.pending = false
        }
        round.library.networkReferences[index] = row
    }

    func syncPushSubscriptions(_ round: inout MusicNativeSyncRound) async throws {
        let ids = round.library.networkReferences.map(\.id)
        for id in ids {
            try await syncCurrent(round)
            let row = round.library.networkReferences.first { $0.id == id }!
            guard row.pending,
                  let selection = round.request.selections.first(where: { $0.platform.rawValue == row.platform && $0.playlists }),
                  !round.ledger.fences.contains(where: { $0.platform == selection.platform }) else { continue }
            let session = round.sessions[selection.platform.rawValue]!, snapshot = round.snapshots[selection.platform.rawValue]!
            let remote = snapshot.subscriptions.first { $0.remoteID == row.remoteID }
            guard row.present != (remote != nil) else { continue }
            let action: MusicLibrarySyncMutation.Action = row.present ? .subscribe : .unsubscribe
            let mutation = MusicLibrarySyncMutation(
                action: action,
                collectionID: row.remoteID,
                name: action == .subscribe ? row.name : nil,
                directoryRevision: snapshot.revision,
                beforeSubscription: action == .unsubscribe ? remote : nil
            )
            _ = try await syncDispatch(mutation, localID: row.id, localRevision: row.revision,
                                       session: session, selection: selection, round: &round)
        }
    }
}
