// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e netease/library.go; subscription uses upstream v4.41.1.
// Credentials and writes stay native; there is no implicit write retry.
import Foundation
import MeowAccounts
import MeowMusicFeature

extension NeteaseMusicAccountAdapter: MusicAccountSyncProvider {
    func syncRead(kinds: [MusicLibrarySyncKind], credential: AccountCredential,
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let context = try context(credential), first = try await directory(context)
        var result: [MusicLibrarySyncCollection] = []
        var subscriptions: [MusicLibrarySyncSubscription] = []
        for entry in first {
            let kind: MusicLibrarySyncKind = entry.owner == context.accountID && entry.special == 5 ? .favorites : .playlist
            if !kinds.contains(kind) { continue }
            if entry.owner != context.accountID {
                subscriptions.append(.init(remoteID: entry.id, relationID: entry.id, name: entry.title, count: entry.count))
                continue
            }
            if ![0, 5].contains(entry.special) { continue }
            try await authorization.check()
            let rows = try await tracks(collectionID: entry.id, credential: credential)
            let members = try rows.map { try MusicAccountSyncWire.member($0, platform: .netease) }
            result.append(try MusicAccountSyncWire.collection(id: entry.id, name: entry.title, kind: kind,
                writable: true,
                canOrder: true, members: members))
        }
        try await authorization.check()
        guard try await directory(context) == first else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let directory = try MusicAccountSyncWire.encoded(first.map { [$0.id, $0.owner, $0.title, String($0.special), $0.count.map(String.init) ?? ""] })
        return try MusicAccountSyncWire.snapshot(result, subscriptions: subscriptions,
            accountID: context.accountID, kinds: kinds, directory: directory)
    }

    func syncReadSubscriptions(credential: AccountCredential,
                               guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let context = try context(credential), first = try await directory(context)
        try await authorization.check()
        guard try await directory(context) == first else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let subscriptions = first.filter { $0.owner != context.accountID }.map {
            MusicLibrarySyncSubscription(remoteID: $0.id, relationID: $0.id, name: $0.title, count: $0.count)
        }
        let directory = try MusicAccountSyncWire.encoded(first.map { [$0.id, $0.owner, $0.title, String($0.special), $0.count.map(String.init) ?? ""] })
        return try MusicAccountSyncWire.snapshot([], subscriptions: subscriptions,
            accountID: context.accountID, kinds: [.playlist], directory: directory)
    }

    func syncWrite(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        if mutation.action.isSubscription { return try await syncSubscription(mutation, credential: credential, guard: authorization) }
        let context: MusicNeteaseContext
        let current: MusicLibrarySyncCollection?
        do {
            context = try self.context(credential)
            let snapshot = try await syncRead(kinds: [mutation.before?.kind ?? .playlist], credential: credential, guard: authorization)
            current = try MusicAccountSyncWire.preflight(mutation, snapshot: snapshot)
            if current?.kind == .favorites, [.add, .remove].contains(mutation.action), mutation.members.count != 1 {
                throw MusicServiceError.invalidRequest
            }
            for member in mutation.members { _ = try MusicNeteaseWire.id(member.nativeID) }
            try await authorization.check()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        // Everything below can have an upstream side effect. Any failure keeps
        // the domain's durable dispatched fence; only readback can settle it.
        switch mutation.action {
        case .create:
            let result = try await request("/api/playlist/create",
                data: ["name": try MusicAccountSyncWire.name(mutation.name), "privacy": 10, "type": "NORMAL"],
                crypto: "weapi", context: context)
            let id = try MusicNeteaseWire.id(result["id"] ?? (result["playlist"] as? [String: Any])?["id"])
            return .acknowledged(createdID: id, createdName: mutation.name)
        case .delete:
            _ = try await request("/api/playlist/remove", data: ["ids": "[" + current!.id + "]"], crypto: "weapi", context: context)
        case .rename:
            let body = try MusicQQWire.json(["id": current!.id, "name": MusicAccountSyncWire.name(mutation.name)])
            _ = try await request("/api/batch", data: ["/api/playlist/update/name": String(decoding: body, as: UTF8.self)], context: context)
        case .add, .remove:
            let ids = mutation.members.map(\.nativeID)
            if current!.kind == .favorites {
                _ = try await request("/api/radio/like", data: ["alg": "itembased", "trackId": ids[0],
                    "like": mutation.action == .add, "time": "3"], crypto: "weapi", context: context)
            } else {
                let json = String(decoding: try MusicAccountSyncWire.encoded(ids), as: UTF8.self)
                _ = try await request("/api/playlist/manipulate/tracks",
                    data: ["pid": current!.id, "op": mutation.action == .add ? "add" : "del", "trackIds": json, "imme": "true"], context: context)
            }
        case .order:
            let json = String(decoding: try MusicAccountSyncWire.encoded(mutation.members.map(\.nativeID)), as: UTF8.self)
            _ = try await request("/api/playlist/manipulate/tracks",
                data: ["pid": current!.id, "op": "update", "trackIds": json], context: context)
        case .subscribe, .unsubscribe: return .notDispatched(.invalidRequest)
        }
        try await authorization.check()
        return .acknowledged(createdID: nil, createdName: nil)
    }

    private func syncSubscription(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        let context: MusicNeteaseContext, request: MusicAccountHTTPRequest, readCookies: [String: String]
        let own = epoch
        do {
            try await authorization.check()
            context = try self.context(credential)
            let id = try MusicNeteaseWire.id(mutation.collectionID)
            let first = try await directory(context)
            try await authorization.check()
            guard try await directory(context) == first else { throw MusicServiceError.revisionConflict }
            let revision = MusicAccountSyncWire.hash(try MusicAccountSyncWire.encoded(first.map {
                [$0.id, $0.owner, $0.title, String($0.special), $0.count.map(String.init) ?? ""]
            }))
            let subscriptions = first.filter { $0.owner != context.accountID }.map {
                MusicLibrarySyncSubscription(remoteID: $0.id, relationID: $0.id, name: $0.title, count: $0.count)
            }
            try MusicAccountSyncWire.subscriptionPreflight(mutation, subscriptions: subscriptions,
                directoryRevision: revision, ownedIDs: first.filter { $0.owner == context.accountID }.map(\.id))
            try await authorization.check()
            try check(context, epoch: own)
            guard let tokenProvider else { throw MusicServiceError.unavailable }
            let token = try MusicNeteaseToken.validate(try await tokenProvider.token())
            try await authorization.check()
            try check(context, epoch: own)
            try ensureDevice(context)
            // Upstream's string t="1" selects subscribe without the obsolete
            // static checkToken body field; both actions require the v2 header.
            let path = "/api/playlist/" + (mutation.action == .subscribe ? "subscribe" : "unsubscribe")
            request = try eapiRequest(path, data: ["id": id], cookies: requestCookies(context), antiCheatToken: token)
            guard MusicAccountSyncRoute.permits(request) else { throw MusicServiceError.invalidRequest }
            readCookies = context.cookies
            try await authorization.check()
            try check(context, epoch: own)
            try authorization.beginDispatch()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        // From the actual send onwards, failure is unknown. The durable domain
        // fence can only be settled by a complete fresh subscription directory.
        let response = try await transport.send(request)
        try check(context, epoch: own)
        _ = try accept(response, url: request.url, context: context, readCookies: readCookies)
        try await authorization.check()
        return .acknowledged(createdID: nil, createdName: nil)
    }
}
