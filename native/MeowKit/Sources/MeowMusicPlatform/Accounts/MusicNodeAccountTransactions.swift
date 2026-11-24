import Foundation
import MeowMusicFeature
import MeowRuntime

extension MusicNodeAccountProvider {
    private func snapshot(_ value: RuntimeValue) throws -> MusicLibrarySyncSnapshot {
        let collections = try value[music: "collections"].musicRequiredArray().map { row -> MusicLibrarySyncCollection in
            let id = try row[music: "id"].musicRequiredText(), writable = row[music: "writable"].musicBool
            guard let kind = MusicLibrarySyncKind(rawValue: try row[music: "kind"].musicRequiredText()) else {
                throw MusicServiceError.upstreamInvalid
            }
            let rawMembers = try row[music: "members"].musicRequiredArray()
            let members = try rawMembers.map { member -> MusicLibrarySyncMember in
                let track = try MusicPlatformDTO.nativeTrack(member[music: "song"])
                return .init(nativeID: try member[music: "id"].musicRequiredText(),
                    occurrenceID: try member[music: "key"].musicRequiredText(), track: track.data)
            }
            let signature = RuntimeValue.object(["id": row[music: "id"], "name": row[music: "name"],
                "kind": row[music: "kind"], "writable": row[music: "writable"], "owner": row[music: "owner"],
                "locator": row[music: "locator"], "members": .array(rawMembers.map { .object([
                    "id": $0[music: "id"], "key": $0[music: "key"], "sort": $0[music: "sort"]]) })])
            return .init(id: id, name: row[music: "name"].musicText, kind: kind, writable: writable,
                canRename: writable && kind == .playlist, canDelete: writable && kind == .playlist,
                canOrder: writable && platform != .qq, members: members,
                revision: musicSHA256Hex(try RuntimeJSON.encode(signature)))
        }
        let subscriptions = try value[music: "subscriptions"].musicRequiredArray().map { row -> MusicLibrarySyncSubscription in
            let id = try row[music: "remoteId"].musicRequiredText()
            return .init(remoteID: id, relationID: try row[music: "relationId"].musicRequiredText(),
                name: row[music: "name"].musicText, artwork: row[music: "cover"].musicText,
                count: row[music: "count"] == .null ? nil : try MusicPlatformDTO.count(row[music: "count"]))
        }
        let directory = try value[music: "directory"].musicRequiredArray()
        let facts = RuntimeValue.array(directory.map { row in .object([
            "id": row[music: "id"], "name": row[music: "name"], "kind": row[music: "kind"],
            "owned": row[music: "owned"], "writable": row[music: "writable"],
            "ownerId": row[music: "ownerId"], "count": row[music: "count"], "locator": row[music: "locator"]
        ]) })
        let owned = try directory.filter { $0[music: "owned"].musicBool }.map {
            try $0[music: "id"].musicRequiredText()
        }
        let revision = musicSHA256Hex(try RuntimeJSON.encode(facts))
        return .init(accountID: try value[music: "accountId"].musicRequiredText(), collections: collections,
                     subscriptions: subscriptions, revision: revision, ownedCollectionIDs: owned)
    }
    private func readSync(_ parameters: RuntimeObject,
                          guard authorization: MusicAccountSyncAuthorization) async throws
        -> (snapshot: MusicLibrarySyncSnapshot, value: RuntimeValue) {
        try await authorization.check()
        let value = try await call("sync.read", parameters)
        try await authorization.check()
        return (try snapshot(value), value)
    }
    func syncRead(kinds: [MusicLibrarySyncKind], guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await readSync(["kinds": .array(kinds.map { .string($0.rawValue) })], guard: authorization).snapshot
    }
    func syncReadSubscriptions(guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        try await readSync(["subscriptionsOnly": .bool(true)], guard: authorization).snapshot
    }
    func syncWrite(_ mutation: MusicLibrarySyncMutation, guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        var parameters: RuntimeObject = ["action": .string(mutation.action.rawValue)]
        do {
            var members = mutation.members
            try await authorization.check()
            if let name = mutation.name { parameters["name"] = .string(name) }
            if let id = mutation.collectionID { parameters["id"] = .string(id) }
            let read = try await readSync(mutation.action.isSubscription || mutation.action == .create
                ? ["subscriptionsOnly": .bool(true)]
                : ["kinds": .array(MusicLibrarySyncKind.allCases.map { .string($0.rawValue) })],
                guard: authorization)
            let current = read.snapshot
            if mutation.action.isSubscription {
                try MusicAccountSyncPreflight.subscription(mutation, snapshot: current)
                if mutation.action == .subscribe {
                    let target = try await call("sync.target", ["id": .string(mutation.collectionID ?? ""),
                                                               "name": .string(mutation.name ?? "")])
                    if platform == .kugou, target[music: "locator"][music: "creatorId"].musicText == current.accountID {
                        throw MusicServiceError.revisionConflict
                    }
                    parameters["remote"] = target
                } else {
                    parameters["remote"] = read.value[music: "subscriptions"].musicArray.first {
                        $0[music: "remoteId"].musicText == mutation.collectionID
                    }
                }
            } else if let row = try MusicAccountSyncPreflight.collection(mutation, snapshot: current) {
                parameters["remote"] = read.value[music: "collections"].musicArray.first {
                    $0[music: "id"].musicText == row.id
                }
                if platform == .kugou, mutation.action == .remove {
                    let selected = Set(mutation.members.map(\.nativeID))
                    members = row.members.filter { selected.contains($0.nativeID) }
                }
            }
            if case .object(let remote)? = parameters["remote"] {
                parameters["remote"] = .object(RuntimeObject(uniqueKeysWithValues: remote.filter {
                    ["id", "name", "kind", "locator", "relationId"].contains($0.key)
                }))
            }
            switch mutation.action {
            case .add, .remove, .order:
                if platform == .kugou, mutation.action != .add {
                    parameters["members"] = .array(members.map {
                        .object(["key": .string($0.occurrenceID)])
                    })
                } else {
                    parameters["ids"] = .array(members.map { .string($0.nativeID) })
                }
                if platform == .kugou, mutation.action == .add {
                    var songs: RuntimeObject = [:]
                    do {
                        for member in members { songs[member.nativeID] = try RuntimeJSON.decode(member.track) }
                    } catch is RuntimeJSONError { throw MusicServiceError.invalidRequest }
                    parameters["songs"] = .object(songs)
                }
            default: break
            }
            try await authorization.check()
            if platform == .netease, mutation.action.isSubscription {
                let value = try await call("sync.prepare", ["action": .string(mutation.action.rawValue),
                    "id": .string(mutation.collectionID ?? "")])
                parameters["preparationId"] = value[music: "preparationId"]
                try await authorization.check()
            }
        } catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        let value = try await call("sync.write", parameters, authorize: { try authorization.beginDispatch() })
        switch value[music: "status"].musicText {
        case "confirmed": return .acknowledged(createdID: value[music: "createdId"] == .null ? nil : value[music: "createdId"].musicText,
                                               createdName: value[music: "createdName"] == .null ? nil : value[music: "createdName"].musicText)
        case "notSent": return .notDispatched(MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText)))
        case "rejected": return .rejected(MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText)))
        default: throw MusicServiceError.commitUnconfirmed
        }
    }
}

extension MusicNodeAccountProvider {
    private func feedbackNotSent(_ value: RuntimeValue) throws -> Never {
        let code = value[music: "errorCode"].musicText
        throw MusicAccountFeedbackNotSent.notSent(code: code)
    }
    func releaseFeedback(_ session: MusicAccountFeedbackNativeSession) async {
        _ = try? await call("feedback.release", ["sessionId": .string(session.id)])
    }
    func feedback(_ report: MusicPlaybackFeedbackReport, session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        let target = report.target
        var fields: RuntimeObject = ["event": .string(report.event == .start ? "start" : "end"),
            "duration": .number(report.duration), "listened": .number(report.listened), "mediaListened": .number(report.mediaListened),
            "position": .number(report.position), "rate": .number(report.rate), "occurredAt": .number(report.occurredAt),
            "reason": report.reason.map { .string($0.rawValue) } ?? .null,
            "target": .object(["nativeId": .string(target.nativeID), "source": .string(target.source),
                "sourceId": .string(target.sourceID), "algorithm": .string(target.algorithm),
                "mixsongId": target.mixsongID.map(RuntimeValue.string) ?? .null,
                "automatic": .bool(target.automatic), "origin": .object(["kind": .string(target.origin?.kind.rawValue ?? "")])])]
        if let session { fields["sessionId"] = .string(session.id) }
        let value = try await call("feedback.send", fields, authorize: { try authorization.checkLocal() })
        let receipt: MusicPlaybackFeedbackReceipt
        switch value[music: "status"].musicText {
        case "confirmed": receipt = .confirmed
        case "registered": receipt = .registered
        case "notSent": throw MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText))
        case "rejected": throw MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText))
        default: receipt = .unconfirmed
        }
        return .init(receipt: receipt, session: value[music: "sessionId"] == .null ? nil :
            .init(id: try value[music: "sessionId"].musicRequiredText()))
    }
    func uploadPlaybackLog(_ events: Data, authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        let value = try await call("feedback.upload", ["events": .array([try RuntimeJSON.decode(events)])],
                                   authorize: { try authorization.checkLocal() })
        if value[music: "status"] == .string("notSent") { try feedbackNotSent(value) }
        if value[music: "status"] == .string("rejected") {
            throw MusicPlatformClient.error(RuntimeHTTPError.remote(value[music: "errorCode"].musicText))
        }
        return value[music: "status"] == .string("confirmed") ? .confirmed : .unconfirmed
    }
}
