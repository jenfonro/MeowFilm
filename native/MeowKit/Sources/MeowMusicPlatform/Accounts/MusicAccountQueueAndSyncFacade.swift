import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowRuntime

extension NativeMusicAccountService: MusicNativeAccountQueueProviding {
    func revokeAccountQueues(_ platform: MusicAccountPlatform? = nil) {
        let ids = accountQueueBindings.filter { platform == nil || $0.value.lease.platform == platform }.map(\.key)
        for id in ids { accountQueueBindings.removeValue(forKey: id)?.revoke() }
        let removed = Set(ids)
        pages = pages.filter { $0.value.queueLeaseID.map { !removed.contains($0) } ?? true }
    }

    public func beginAccountQueue(platform: MusicAccountPlatform) async throws -> MusicAccountQueueLease {
        do {
            try check()
            let retired = accountQueueBindings.values.filter { !$0.lease.authorization.isActive }.map(\.lease)
            for lease in retired { endAccountQueue(lease) }
            let token = try await session(platform), own = epoch, revision = revisions[platform, default: 0]
            try await validToken(token, platform: platform, own: own, revision: revision)
            let credential = try await accounts.credentialUpdateLease(for: token)
            do {
                guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
                let nativeID = try await provider.nativeID(credential.credential)
                try await validToken(token, platform: platform, own: own, revision: revision)
                let lease = MusicAccountQueueLease(platform: platform)
                accountQueueBindings[lease.id] = .init(lease: lease, credential: credential, epoch: own,
                    revision: revision, nativeID: nativeID)
                return lease
            } catch { credential.revoke(); throw error }
        } catch { throw safe(error) }
    }

    public func checkAccountQueue(_ lease: MusicAccountQueueLease) async throws {
        do {
            try lease.authorization.check()
            guard let entry = accountQueueBindings[lease.id], entry.lease === lease else {
                throw MusicServiceError.staleResult
            }
            try await validToken(entry.credential.token, platform: lease.platform,
                                 own: entry.epoch, revision: entry.revision)
            let observed = try await accounts.credentialUpdateLease(for: entry.credential.token)
            defer { observed.revoke() }
            try lease.authorization.check(); try check(entry.epoch)
            guard accountQueueBindings[lease.id]?.lease === lease,
                  observed.revision == entry.credential.revision,
                  observed.credential == entry.credential.credential else { throw MusicServiceError.staleResult }
        } catch {
            let failure = safe(error)
            if failure != .cancelled, accountQueueBindings[lease.id]?.lease === lease { endAccountQueue(lease) }
            throw failure
        }
    }

    public func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                                 cursor: String?) async throws -> MusicNativeTrackPage {
        do {
            try await checkAccountQueue(lease)
            var fields: RuntimeObject = ["platform": .string(lease.platform.rawValue),
                "cursor": cursor.map(RuntimeValue.string) ?? .null]
            switch kind {
            case .collection(let id):
                fields["operation"] = .string("tracks"); fields["collectionId"] = .string(id)
            case .recommendation(let kind):
                fields["operation"] = .string("recommendations"); fields["kind"] = .string(kind)
            }
            let value = try await business(fields, queueLease: lease)
            try await checkAccountQueue(lease)
            guard case .object(let result) = value else { throw MusicServiceError.invalidResponse }
            return try nativePage(result, platform: lease.platform)
        } catch {
            let failure = safe(error)
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }

    public func endAccountQueue(_ lease: MusicAccountQueueLease) {
        guard accountQueueBindings[lease.id]?.lease === lease else { return }
        accountQueueBindings.removeValue(forKey: lease.id)?.revoke()
        pages = pages.filter { $0.value.queueLeaseID != lease.id }
    }
}

extension NativeMusicAccountService: MusicNativeLibrarySyncProviding {
    func revokeLibrarySync(_ platform: MusicAccountPlatform? = nil) {
        let ids = librarySyncBindings.filter { platform == nil || $0.value.session.platform == platform }.map(\.key)
        for id in ids { librarySyncBindings.removeValue(forKey: id)?.revoke() }
    }

    public func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        do { return try await makeLibrarySyncSession(platform: platform) }
        catch { throw safe(error) }
    }

    private func makeLibrarySyncSession(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        try check()
        let expired = librarySyncBindings.filter { $0.value.expires <= now() }.map(\.key)
        for id in expired { librarySyncBindings.removeValue(forKey: id)?.revoke() }
        guard providers[platform] != nil else { throw MusicServiceError.unsupported }
        guard !librarySyncBindings.values.contains(where: { $0.session.platform == platform }) else { throw MusicServiceError.busy }
        let token = try await session(platform), own = epoch, revision = revisions[platform, default: 0]
        try await validToken(token, platform: platform, own: own, revision: revision)
        let lease = try await accounts.credentialUpdateLease(for: token)
        do {
            try await validToken(token, platform: platform, own: own, revision: revision)
            let uid = try await providers[platform]!.nativeID(lease.credential)
            try await validToken(token, platform: platform, own: own, revision: revision)
            guard !librarySyncBindings.values.contains(where: { $0.session.platform == platform }) else { throw MusicServiceError.busy }
            let identity = token.identity
            let scope = identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" + identity.accountID.uuidString.lowercased()
            let result = MusicLibrarySyncSession(id: UUID(), platform: platform, accountID: uid, scope: scope)
            let authorization = MusicAccountSyncAuthorization { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkLibrarySync(result)
            }
            librarySyncBindings[result.id] = .init(session: result, lease: lease, epoch: own, revision: revision,
                                                   expires: now().addingTimeInterval(600), authorization: authorization)
            return result
        } catch { lease.revoke(); throw safe(error) }
    }

    public func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws {
        do { try await checkLibrarySyncBinding(session) }
        catch { throw safe(error) }
    }

    private func checkLibrarySyncBinding(_ session: MusicLibrarySyncSession) async throws {
        guard let entry = librarySyncBindings[session.id], entry.session == session, entry.expires > now() else {
            throw MusicServiceError.staleResult
        }
        try entry.authorization.checkLocal()
        try await validToken(entry.lease.token, platform: session.platform, own: entry.epoch, revision: entry.revision)
        let observed = try await accounts.credentialUpdateLease(for: entry.lease.token)
        defer { observed.revoke() }
        try entry.authorization.checkLocal()
        try check(entry.epoch)
        guard observed.revision == entry.lease.revision, observed.credential == entry.lease.credential,
              librarySyncBindings[session.id]?.session == session else { throw MusicServiceError.staleResult }
    }

    public func readLibrarySync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        try await checkLibrarySync(session)
        guard let entry = librarySyncBindings[session.id],
              let provider = providers[session.platform] else { throw MusicServiceError.staleResult }
        let value = try await authenticated(session.platform, token: entry.lease.token, own: entry.epoch, revision: entry.revision) {
            try await provider.syncRead(kinds: kinds, guard: entry.authorization)
        }
        try await checkLibrarySync(session)
        return value
    }

    public func readLibrarySubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        try await checkLibrarySync(session)
        guard let entry = librarySyncBindings[session.id],
              let provider = providers[session.platform] else { throw MusicServiceError.staleResult }
        let value = try await authenticated(session.platform, token: entry.lease.token, own: entry.epoch, revision: entry.revision) {
            try await provider.syncReadSubscriptions(guard: entry.authorization)
        }
        try await checkLibrarySync(session)
        return value
    }

    public func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        let dispatch = MusicAccountSyncDispatch()
        do {
            try await checkLibrarySync(session)
            guard let entry = librarySyncBindings[session.id],
                  let provider = providers[session.platform] else { throw MusicServiceError.staleResult }
            let authorization = MusicAccountSyncAuthorization(parent: entry.authorization, dispatch: dispatch)
            return try await settle(session.platform) {
                try await provider.syncWrite(mutation, guard: authorization)
            }
        } catch {
            if dispatch.stopBeforeDispatch() { return .notDispatched(safe(error)) }
            throw MusicServiceError.commitUnconfirmed
        }
    }

    public func endLibrarySync(_ session: MusicLibrarySyncSession) {
        guard librarySyncBindings[session.id]?.session == session else { return }
        librarySyncBindings.removeValue(forKey: session.id)?.revoke()
    }
}

extension NativeMusicAccountService: MusicNativeHomeActionQueueProviding {
    func homeActionArgument(_ action: MusicHomeAction) throws -> String {
        let (operation, parameters) = MusicPlatformDTO.action(action)
        let descriptor = RuntimeValue.object(["operation": .string(operation),
            "params": .object(parameters)])
        return musicSHA256Hex(try RuntimeJSON.encode(descriptor))
    }

    public func homeActionPage(_ lease: MusicAccountQueueLease, action: MusicHomeAction,
                               cursor: String?) async throws -> MusicNativeTrackPage {
        do {
            guard lease.platform == .netease else { throw MusicServiceError.unsupported }
            let argument = try homeActionArgument(action)
            try await checkAccountQueue(lease)
            guard let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[lease.platform] else {
                throw MusicServiceError.unsupported
            }
            let token = entry.credential.token
            if let cursor {
                let parts = cursor.split(separator: ":", omittingEmptySubsequences: false)
                guard parts.count == 2, let offset = Int(parts[1]), offset > 0, offset % 30 == 0,
                      String(offset) == parts[1] else { throw MusicServiceError.staleResult }
                let key = String(parts[0])
                return try lease.authorization.perform {
                    try check(entry.epoch)
                    guard accountQueueBindings[lease.id]?.lease === lease,
                          let saved = pages[key], saved.queueLeaseID == lease.id,
                          saved.platform == lease.platform, saved.token == token,
                          saved.revision == entry.revision, saved.revision == revisions[lease.platform, default: 0],
                          saved.operation == "homeAction", saved.argument == argument,
                          saved.expires > now(), saved.nextPage == nil, offset < saved.rows.count,
                          case .object(let result) = try slice(saved, key: key, offset: offset) else {
                        throw MusicServiceError.staleResult
                    }
                    return try nativePage(result, platform: lease.platform)
                }
            }
            let value = try await authenticated(lease.platform, token: token, own: entry.epoch, revision: entry.revision) {
                try await provider.homeAction(action) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            guard value.nextPage == nil else {
                throw MusicServiceError.invalidResponse
            }
            return try lease.authorization.perform {
                try check(entry.epoch)
                guard accountQueueBindings[lease.id]?.lease === lease,
                      revisions[lease.platform, default: 0] == entry.revision,
                      case .object(let result) = try page(value.rows, platform: lease.platform, token: token,
                        operation: "homeAction", argument: argument, expiresAt: value.expiresAt,
                        queueLeaseID: lease.id) else { throw MusicServiceError.staleResult }
                return try nativePage(result, platform: lease.platform, dailyStyle: value.dailyStyle)
            }
        } catch {
            let failure = safe(error)
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }
}

extension NativeMusicAccountService: MusicNativeDailyStyleProviding {
    public func dailyStyleScope(_ lease: MusicAccountQueueLease) async throws -> String {
        do {
            guard lease.platform == .netease else { throw MusicServiceError.unsupported }
            try await checkAccountQueue(lease)
            return try lease.authorization.perform {
                guard let entry = accountQueueBindings[lease.id], entry.lease === lease else {
                    throw MusicServiceError.staleResult
                }
                struct Scope: Encodable {
                    let identity: AccountIdentity
                    let nativeID: String
                }
                return try musicSHA256Hex(encoding: Scope(identity: entry.credential.token.identity, nativeID: entry.nativeID))
            }
        } catch { throw safe(error) }
    }

    public func dailyStyleCategories(_ lease: MusicAccountQueueLease) async throws -> [MusicDailyStyleCategory] {
        do {
            try await checkAccountQueue(lease)
            guard lease.platform == .netease, let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[.netease] else {
                throw MusicServiceError.unsupported
            }
            let values = try await authenticated(.netease, token: entry.credential.token,
                                                 own: entry.epoch, revision: entry.revision) {
                try await provider.dailyStyleCategories { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            return values
        } catch { throw safe(error) }
    }

    public func saveDailyStyle(_ lease: MusicAccountQueueLease,
                               selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        guard dailyStyleWrite == nil else { throw MusicServiceError.busy }
        let write = UUID(); dailyStyleWrite = write
        defer { if dailyStyleWrite == write { dailyStyleWrite = nil } }
        do {
            try await checkAccountQueue(lease)
            guard lease.platform == .netease, let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[.netease] else {
                throw MusicServiceError.unsupported
            }
            let value = try await settle(.netease) {
                try await provider.saveDailyStyle(selection, authorization: lease.authorization)
            }
            let pageCurrent = (try? await checkAccountQueue(lease)) != nil
            let receipt: () throws -> MusicDailyStyleSave = {
                let rows = value.songs.expiresAt.map { $0 <= self.now() } == true ? [] : value.songs.rows
                return .init(page: .init(items: try rows.map {
                    .init(platform: "wy", data: try RuntimeJSON.encode($0))
                }, total: rows.count, cursor: nil, dailyStyle: value.songs.dailyStyle),
                    saveConfirmed: value.saveConfirmed, selectionChanged: value.selectionChanged)
            }
            if !lease.authorization.isActive || !pageCurrent { return try receipt() }
            let argument = try homeActionArgument(.daily(branch: .style))
            do {
                return try lease.authorization.perform {
                    try check(entry.epoch)
                    guard accountQueueBindings[lease.id]?.lease === lease,
                          entry.revision == revisions[.netease, default: 0] else { throw MusicServiceError.staleResult }
                    pages = pages.filter { $0.value.queueLeaseID != lease.id }
                    guard case .object(let result) = try page(value.songs.rows, platform: .netease,
                        token: entry.credential.token, operation: "homeAction", argument: argument,
                        expiresAt: value.songs.expiresAt, queueLeaseID: lease.id) else { throw MusicServiceError.invalidResponse }
                    return .init(page: try nativePage(result, platform: .netease, dailyStyle: value.songs.dailyStyle),
                        saveConfirmed: value.saveConfirmed, selectionChanged: value.selectionChanged)
                }
            } catch { return try receipt() }
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService: MusicNativeSimilarModeProviding {
    private func similarModeBinding(_ lease: MusicAccountQueueLease) throws -> MusicAccountQueueBinding {
        try check(); try lease.authorization.check()
        guard lease.platform == .netease else { throw MusicServiceError.unsupported }
        guard let entry = accountQueueBindings[lease.id], entry.lease === lease else {
            throw MusicServiceError.staleResult
        }
        try check(entry.epoch)
        guard entry.revision == revisions[lease.platform, default: 0],
              tokens[lease.platform] == entry.credential.token, validated.contains(lease.platform),
              entry.credential.phase == .signedIn, !restoring.contains(lease.platform),
              !transitions.contains(lease.platform), login?.platform != lease.platform,
              !requiresExplicitRestore.contains(lease.platform) else { throw MusicServiceError.staleResult }
        return entry
    }

    public func similarModeScope(_ lease: MusicAccountQueueLease) async throws -> String {
        do {
            _ = try similarModeBinding(lease)
            try await checkAccountQueue(lease)
            return try lease.authorization.perform {
                let entry = try similarModeBinding(lease)
                return try nativeRecordScope(identity: entry.credential.token.identity,
                                             revision: entry.credential.revision)
            }
        } catch { throw safe(error) }
    }

    public func similarModeBatch(_ lease: MusicAccountQueueLease,
                                 request: MusicNativeSimilarModeRequest) async throws -> MusicNativeSimilarModeBatch {
        do {
            _ = try similarModeBinding(lease)
            try await checkAccountQueue(lease)
            let entry = try similarModeBinding(lease)
            guard let provider = providers[lease.platform] else {
                throw MusicServiceError.unsupported
            }
            let result = try await authenticated(lease.platform, token: entry.credential.token,
                                                 own: entry.epoch, revision: entry.revision,
                                                 queueAuthorization: lease.authorization) {
                try await provider.similarMode(request) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            return try lease.authorization.perform {
                _ = try similarModeBinding(lease)
                return result
            }
        } catch {
            let failure = safe(error)
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }
}
