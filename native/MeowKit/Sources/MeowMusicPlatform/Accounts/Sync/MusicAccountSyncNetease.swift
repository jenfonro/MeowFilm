// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e netease/library.go. No server, Node or implicit write retry.
import Foundation
import MeowAccounts
import MeowMusicFeature

extension NeteaseMusicAccountAdapter: MusicAccountSyncProvider {
    func syncRead(kinds: [MusicLibrarySyncKind], credential: AccountCredential,
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await authorization.check()
        let context = try context(credential), first = try await directory(context)
        var result: [MusicLibrarySyncCollection] = []
        for entry in first {
            let kind: MusicLibrarySyncKind = entry.owner == context.accountID && entry.special == 5 ? .favorites : .playlist
            if !kinds.contains(kind) { continue }
            try await authorization.check()
            let rows = try await tracks(collectionID: entry.id, credential: credential)
            let members = try rows.map { try MusicAccountSyncWire.member($0, platform: .netease) }
            result.append(try MusicAccountSyncWire.collection(id: entry.id, name: entry.title, kind: kind,
                writable: entry.owner == context.accountID && [0, 5].contains(entry.special),
                canOrder: true, members: members))
        }
        try await authorization.check()
        guard try await directory(context) == first else { throw MusicServiceError.revisionConflict }
        try await authorization.check()
        let directory = try MusicAccountSyncWire.encoded(first.map { [$0.id, $0.owner, $0.title, String($0.special), $0.count.map(String.init) ?? ""] })
        return try MusicAccountSyncWire.snapshot(result, accountID: context.accountID, kinds: kinds, directory: directory)
    }

    func syncWrite(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
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
        }
        try await authorization.check()
        return .acknowledged(createdID: nil, createdName: nil)
    }
}
