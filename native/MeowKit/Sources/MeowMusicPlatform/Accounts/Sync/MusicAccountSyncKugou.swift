// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e kugou/library.go. Hash is not an occurrence/fileid.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

extension KugouMusicAccountAdapter: MusicAccountSyncProvider {
    func syncRead(kinds: [MusicLibrarySyncKind], credential: AccountCredential,
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let context = try context(credential), first = try await directory(context), raw = try await paginate(nil, context: context)
        var result: [MusicLibrarySyncCollection] = []
        var subscriptions: [MusicLibrarySyncSubscription] = []
        for entry in first {
            let kind: MusicLibrarySyncKind = entry.favorite ? .favorites : .playlist
            if !kinds.contains(kind) { continue }
            guard let row = raw.rows.first(where: {
                (try? MusicKugouWire.id($0["listid"])) == entry.listID && (try? MusicKugouWire.integer($0["type"])) == entry.type
            }) else { throw MusicServiceError.revisionConflict }
            let special = [1, 2].contains((try? MusicKugouWire.integer(row["is_def"])) ?? 0) ||
                (try? MusicKugouWire.integer(row["is_default"])) == 1
            let source = try MusicKugouWire.integer(row["source"])
            let writable = entry.owned && source != 2 &&
                (entry.favorite || !special && !["我喜欢", "我喜欢的音乐", "默认收藏"].contains(entry.name))
            if !writable {
                // A subscription's public globalID is not this account's listid.
                // Special/system lists without a public identity are not guessed.
                if entry.type == 1 {
                    guard entry.globalID.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil &&
                          !entry.globalID.hasPrefix("id_") else { throw MusicServiceError.upstreamInvalid }
                    subscriptions.append(.init(remoteID: entry.globalID, relationID: entry.id, name: entry.name, count: entry.count))
                }
                continue
            }
            try await authorization.check()
            let rows = try await tracks(collectionID: entry.id, credential: credential)
            result.append(try MusicAccountSyncWire.collection(id: entry.id, name: entry.name, kind: kind,
                writable: true, canOrder: true,
                members: rows.map { try MusicAccountSyncWire.member($0, platform: .kugou) }, fingerprint: entry.fingerprint))
        }
        try await authorization.check()
        guard try await directory(context) == first,
              try await paginate(nil, context: context).fingerprint == raw.fingerprint else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        return try MusicAccountSyncWire.snapshot(result, subscriptions: subscriptions,
            accountID: context.auth!.uid, kinds: kinds, directory: raw.fingerprint)
    }

    func syncReadSubscriptions(credential: AccountCredential,
                               guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let context = try context(credential), first = try await directory(context), raw = try await paginate(nil, context: context)
        try await authorization.check()
        guard try await directory(context) == first,
              try await paginate(nil, context: context).fingerprint == raw.fingerprint else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let subscriptions = try first.filter { $0.type == 1 }.map { entry in
            guard entry.globalID.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil,
                  !entry.globalID.hasPrefix("id_") else { throw MusicServiceError.upstreamInvalid }
            return MusicLibrarySyncSubscription(remoteID: entry.globalID, relationID: entry.id, name: entry.name, count: entry.count)
        }
        return try MusicAccountSyncWire.snapshot([], subscriptions: subscriptions,
            accountID: context.auth!.uid, kinds: [.playlist], directory: raw.fingerprint)
    }

    func syncWrite(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        if mutation.action.isSubscription { return try await syncSubscription(mutation, credential: credential, guard: authorization) }
        let context: MusicKugouContext
        let route: MusicAccountSyncRoute.Kugou
        var body: [String: Any] = [:]
        do {
            context = try self.context(credential)
            let kinds = [mutation.before?.kind ?? .playlist]
            let snapshot = try await syncRead(kinds: kinds, credential: credential, guard: authorization)
            let current = try MusicAccountSyncWire.preflight(mutation, snapshot: snapshot, nameLimit: 200)
            let directory = try await paginate(nil, context: context)
            guard MusicAccountSyncWire.hash(try directory.fingerprint) == snapshot.revision else { throw MusicServiceError.revisionConflict }
            let uid = context.auth!.uid, token = context.auth!.token
            let listID = current.map { String($0.id.dropFirst(2)) } ?? ""
            switch mutation.action {
            case .create:
                route = .create
                body = ["userid": uid, "token": token, "total_ver": 0, "name": try MusicAccountSyncWire.name(mutation.name, maximum: 200),
                        "type": 0, "source": 1, "is_pri": 1, "list_create_gid": "", "from_shupinmv": 0]
            case .delete:
                route = .delete; body = ["listid": Int(listID)!, "total_ver": 0, "type": 1]
            case .rename:
                guard let version = directory.version,
                      let raw = directory.rows.first(where: { (try? MusicKugouWire.id($0["listid"])) == listID &&
                          (try? MusicKugouWire.integer($0["type"])) == 0 }) else { throw MusicServiceError.invalidResponse }
                route = .rename
                body = ["total_ver": version, "listid": Int(listID)!, "type": 0, "name": try MusicAccountSyncWire.name(mutation.name, maximum: 200)]
                for key in ["sort", "tags", "intro", "pic"] where raw[key] != nil { body[key] = raw[key] }
            case .add:
                route = .add
                var data: [[String: Any]] = []
                for member in mutation.members {
                    try await authorization.check()
                    data.append(try await syncAddition(member, context: context, guard: authorization))
                }
                body = ["listid": listID, "userid": uid, "token": token, "type": 0, "list_ver": 0,
                        "slow_upload": 1, "scene": "false;null", "data": data]
            case .remove:
                route = .remove
                let wanted = Set(mutation.members.map(\.nativeID))
                let data = try current!.members.filter { wanted.contains($0.nativeID) }.map {
                    ["fileid": Int(try MusicKugouWire.id($0.occurrenceID))!]
                }
                guard !data.isEmpty else { throw MusicServiceError.invalidRequest }
                body = ["listid": listID, "userid": uid, "token": token, "type": 0, "list_ver": 0, "data": data]
            case .order:
                route = .order
                let entries = try await self.directory(context)
                guard let entry = entries.first(where: { $0.id == current!.id }),
                      let version = try await paginate(entry, context: context).version else { throw MusicServiceError.invalidResponse }
                let data = try mutation.members.enumerated().map { index, member in
                    ["fileid": Int(try MusicKugouWire.id(member.occurrenceID))!, "sort": index]
                }
                body = ["listid": Int(listID)!, "list_ver": version, "type": 0, "data": data]
            case .subscribe, .unsubscribe: return .notDispatched(.invalidRequest)
            }
            // Details/version reads cannot authorize an obsolete write.
            let fresh = try await syncRead(kinds: kinds, credential: credential, guard: authorization)
            _ = try MusicAccountSyncWire.preflight(mutation, snapshot: fresh, nameLimit: 200)
            try await authorization.check()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        let response = try await syncRequest(route, payload: body, context: context, guard: authorization)
        if mutation.action == .create {
            let data = response["data"] as? [String: Any] ?? response
            return .acknowledged(createdID: "0:" + (try MusicKugouWire.id(data["listid"])), createdName: mutation.name)
        }
        return .acknowledged(createdID: nil, createdName: nil)
    }

    private func syncSubscription(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        let context: MusicKugouContext, route: MusicAccountSyncRoute.Kugou
        let body: [String: Any]
        do {
            context = try self.context(credential)
            let id = try MusicKugouWire.text(mutation.collectionID, maximum: 200)
            guard id.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil &&
                  !id.hasPrefix("id_") else { throw MusicServiceError.invalidRequest }
            try await syncSubscriptionPreflight(mutation, context: context, guard: authorization)
            if mutation.action == .subscribe {
                let head = try await syncRequest(.subscriptionInfo, payload: [
                    "data": [["global_collection_id": id]], "userid": context.auth!.uid, "token": context.auth!.token
                ], context: context, guard: authorization)
                let rows = try MusicKugouWire.rows(head["data"], maximum: 1)
                guard rows.count == 1, let info = rows.first,
                      try MusicKugouWire.text(info["global_collection_id"], maximum: 200) == id else {
                    throw MusicServiceError.targetChanged
                }
                let owner = try MusicKugouWire.id(info["list_create_userid"])
                let originalList = try MusicKugouWire.id(info["list_create_listid"])
                guard owner != context.auth!.uid else { throw MusicServiceError.invalidRequest }
                route = .create
                body = ["userid": context.auth!.uid, "token": context.auth!.token, "total_ver": 0,
                    "name": try MusicKugouWire.text(mutation.name, maximum: 600), "type": 1, "source": 1,
                    "is_pri": 0, "list_create_userid": owner, "list_create_listid": originalList,
                    "list_create_gid": id, "from_shupinmv": 0]
            } else {
                guard let relation = mutation.beforeSubscription?.relationID,
                      relation.range(of: "^1:[1-9][0-9]*$", options: .regularExpression) != nil else {
                    throw MusicServiceError.invalidRequest
                }
                route = .delete
                // The account's type-1 subscription locator, not the author's list.
                body = ["listid": Int(try MusicKugouWire.id(String(relation.dropFirst(2))))!, "total_ver": 0, "type": 1]
            }
            try await syncSubscriptionPreflight(mutation, context: context, guard: authorization)
            try await authorization.check()
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        _ = try await syncRequest(route, payload: body, context: context, guard: authorization)
        return .acknowledged(createdID: nil, createdName: nil)
    }
    private func syncSubscriptionPreflight(_ mutation: MusicLibrarySyncMutation, context: MusicKugouContext,
                                           guard authorization: MusicAccountSyncAuthorization) async throws {
        try await authorization.check()
        let first = try await directory(context), raw = try await paginate(nil, context: context)
        guard try await directory(context) == first,
              try await paginate(nil, context: context).fingerprint == raw.fingerprint else {
            throw MusicServiceError.revisionConflict
        }
        let subscriptions = first.filter { $0.type == 1 }.map {
            MusicLibrarySyncSubscription(remoteID: $0.globalID, relationID: $0.id, name: $0.name, count: $0.count)
        }
        try MusicAccountSyncWire.subscriptionPreflight(mutation, subscriptions: subscriptions,
            directoryRevision: MusicAccountSyncWire.hash(try raw.fingerprint), ownedIDs: first.filter(\.owned).map(\.globalID))
        try await authorization.check()
    }
}
