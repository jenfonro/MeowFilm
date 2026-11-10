// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e qq/library.go. Reuses the account instance, never anonymous QQ.
import Foundation
import MeowAccounts
import MeowMusicFeature

extension QQMusicAccountAdapter: MusicAccountSyncProvider {
    func syncRead(kinds: [MusicLibrarySyncKind], credential: AccountCredential,
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let auth = try auth(credential), first = try await directory(auth)
        var result: [MusicLibrarySyncCollection] = []
        var subscriptions: [MusicLibrarySyncSubscription] = []
        for entry in first {
            let kind: MusicLibrarySyncKind = entry.id == "favorites" ? .favorites : .playlist
            if !kinds.contains(kind) { continue }
            if !entry.owned {
                subscriptions.append(.init(remoteID: entry.id, relationID: entry.id, name: entry.title))
                continue
            }
            try await authorization.check()
            let a = try await scan(entry, auth: auth), b = try await scan(entry, auth: auth)
            guard a == b else { throw MusicServiceError.revisionConflict }
            let members = try b.map { try MusicAccountSyncWire.member($0, platform: .qq) }
            result.append(try MusicAccountSyncWire.collection(id: entry.id, name: entry.title, kind: kind,
                writable: true, canOrder: false, members: members))
        }
        try await authorization.check()
        guard try await directory(auth) == first else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let directory = try MusicAccountSyncWire.encoded(first.map { [$0.id, String($0.dirID), $0.owner, $0.title, String($0.owned)] })
        return try MusicAccountSyncWire.snapshot(result, subscriptions: subscriptions,
            accountID: MusicQQWire.id(auth["musicid"]), kinds: kinds, directory: directory)
    }

    func syncReadSubscriptions(credential: AccountCredential,
                               guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let auth = try auth(credential), first = try await directory(auth)
        try await authorization.check()
        guard try await directory(auth) == first else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let subscriptions = first.filter { !$0.owned }.map {
            MusicLibrarySyncSubscription(remoteID: $0.id, relationID: $0.id, name: $0.title)
        }
        let directory = try MusicAccountSyncWire.encoded(first.map { [$0.id, String($0.dirID), $0.owner, $0.title, String($0.owned)] })
        return try MusicAccountSyncWire.snapshot([], subscriptions: subscriptions,
            accountID: MusicQQWire.id(auth["musicid"]), kinds: [.playlist], directory: directory)
    }

    func syncWrite(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        if mutation.action.isSubscription { return try await syncSubscription(mutation, credential: credential, guard: authorization) }
        let auth: [String: Any]
        var entry: MusicQQCollection?
        var native: [[String: Any]] = []
        do {
            auth = try self.auth(credential)
            let kinds = [mutation.before?.kind ?? .playlist]
            let snapshot = try await syncRead(kinds: kinds, credential: credential, guard: authorization)
            _ = try MusicAccountSyncWire.preflight(mutation, snapshot: snapshot)
            if let id = mutation.collectionID {
                entry = try await directory(auth).first { $0.id == id }
                guard entry?.owned == true else { throw MusicServiceError.authRequired }
            }
            if [.add, .remove].contains(mutation.action) {
                for member in mutation.members {
                    try await authorization.check()
                    let row = try await track(member.nativeID, auth: auth)
                    let id = try MusicQQWire.songID(row["id"]), type = try MusicQQWire.integer(row["type"], maximum: Int(Int32.max))
                    if let previous = mutation.before?.members.first(where: { $0.nativeID == member.nativeID }) {
                        let old = try MusicQQWire.decode(previous.track)
                        guard old["qqSongId"] as? String == id,
                              try MusicQQWire.integer(old["songType"]) == type else { throw MusicServiceError.targetChanged }
                    }
                    native.append(["songId": Int(id)!, "songType": mutation.action == .remove ? 0 : type])
                }
                let fresh = try await syncRead(kinds: kinds, credential: credential, guard: authorization)
                _ = try MusicAccountSyncWire.preflight(mutation, snapshot: fresh)
            }
            try await authorization.check()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        let module = "music.musicasset.PlaylistBaseWrite"
        let response: [String: Any]
        switch mutation.action {
        case .create:
            response = try await syncRPC(module, "AddPlaylist", ["dirName": MusicAccountSyncWire.name(mutation.name)], auth: auth, authorization: authorization)
            try MusicQQWire.codes(response)
            let result = try MusicQQWire.object(response["result"])
            let id = try MusicQQWire.id(result["tid"])
            let name = try MusicQQWire.string(result["dirName"] ?? mutation.name, maximum: 600)
            return .acknowledged(createdID: id, createdName: name)
        case .delete:
            response = try await syncRPC(module, "DelPlaylist", ["dirId": entry!.dirID], auth: auth, authorization: authorization)
        case .rename:
            response = try await syncRPC(module, "EditPlaylist", ["dirId": entry!.dirID, "dirName": MusicAccountSyncWire.name(mutation.name)], auth: auth, authorization: authorization)
        case .add, .remove:
            response = try await syncRPC("music.musicasset.PlaylistDetailWrite", mutation.action == .add ? "AddSonglist" : "DelSonglist",
                ["dirId": entry!.dirID, "tid": entry!.id == "favorites" ? 0 : Int(entry!.id)!,
                 "bFmtUtf8": mutation.action == .add ? true as Any : 1 as Any, "v_songInfo": native], auth: auth, authorization: authorization)
        case .order:
            // preflight rejects this before mutation; no remove/re-add fallback.
            return .notDispatched(.unsupported)
        case .subscribe, .unsubscribe: return .notDispatched(.invalidRequest)
        }
        try MusicQQWire.codes(response)
        if let ret = response["retCode"], try MusicQQWire.integer(ret) != 0 { throw MusicServiceError.upstreamFailed }
        try await authorization.check()
        return .acknowledged(createdID: nil, createdName: nil)
    }
    private func syncSubscription(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        let auth: [String: Any], id: String, euin: String
        do {
            try await authorization.check()
            auth = try self.auth(credential)
            id = try MusicQQWire.id(mutation.collectionID)
            euin = try MusicQQWire.string(auth["encrypt_uin"], maximum: 256)
            let first = try await directory(auth)
            try await authorization.check()
            let second = try await directory(auth)
            guard first == second else { throw MusicServiceError.revisionConflict }
            let revision = MusicAccountSyncWire.hash(try MusicAccountSyncWire.encoded(first.map {
                [$0.id, String($0.dirID), $0.owner, $0.title, String($0.owned)]
            }))
            let subscriptions = first.filter { !$0.owned }.map {
                MusicLibrarySyncSubscription(remoteID: $0.id, relationID: $0.id, name: $0.title)
            }
            try MusicAccountSyncWire.subscriptionPreflight(mutation, subscriptions: subscriptions,
                directoryRevision: revision, ownedIDs: first.filter(\.owned).map(\.id))
            try await authorization.check()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        // QQMusicApi 2698673 modules/user.py:335–371. Public disstid/pid,
        // never an owned playlist's dirid; membership is confirmed by readback.
        let response = try await syncRPC("music.musicasset.PlaylistFavWrite",
            mutation.action == .subscribe ? "FavPlaylist" : "CancelFavPlaylist",
            ["uin": euin, "v_playlistId": [Int(id)!]], auth: auth, authorization: authorization)
        try MusicQQWire.codes(response)
        let failed = try array(response["v_failedPlaylistId"] ?? [], maximum: 2000).map { try MusicQQWire.id($0) }
        guard try MusicQQWire.integer(response["result"]) == 0,
              !failed.contains(id) else { throw MusicServiceError.upstreamFailed }
        try await authorization.check()
        return .acknowledged(createdID: nil, createdName: nil)
    }
    private func syncRPC(_ module: String, _ method: String, _ params: [String: Any],
                         auth: [String: Any], authorization: MusicAccountSyncAuthorization) async throws -> [String: Any] {
        guard publicPersistence == nil else { throw MusicServiceError.invalidRequest }
        let device = try await ensureDevice()
        // ensureDevice may suspend. Revalidate the exact account afterwards;
        // never let device recovery become an obsolete mutation's new grant.
        try await authorization.check()
        return try await rpcWith(module, method, params, comm: comm(device, auth: auth), auth: auth)
    }
}
