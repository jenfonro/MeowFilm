import Foundation
import MeowAccounts
import MeowDebug
import MeowRuntime

extension NativeMusicAccountService {
    func storePage(_ rows: [RuntimeValue], platform: MusicAccountPlatform, token: AccountSessionToken,
                   operation: String, argument: String, nextPage: Int? = nil, expiresAt: Date? = nil,
                   queueLeaseID: UUID? = nil, key: String = UUID().uuidString.lowercased()) throws -> (rows: [RuntimeValue], cursor: String?, total: Int) {
        let at = now(); pages = pages.filter { $0.value.expires > at }
        let expires = min(at.addingTimeInterval(120), expiresAt ?? .distantFuture)
        guard expires > at else { throw MusicServiceError.staleResult }
        let byteCount = try RuntimeJSON.encode(.array(rows)).count, budget = 4 * 1024 * 1024
        guard byteCount <= budget else { throw MusicServiceError.limit }
        while pages.count >= 4 || pages.values.reduce(0, { $0 + $1.byteCount }) + byteCount > budget {
            guard let oldest = pages.min(by: { $0.value.expires < $1.value.expires })?.key else { break }
            pages.removeValue(forKey: oldest)
        }
        let value = Page(platform: platform, token: token, revision: revisions[platform, default: 0],
                         operation: operation, argument: argument, rows: rows, nextPage: nextPage,
                         expires: expires, byteCount: byteCount, queueLeaseID: queueLeaseID)
        pages[key] = value
        return try slicePage(value, key: key, offset: 0)
    }

    func slicePage(_ page: Page, key: String, offset: Int) throws -> (rows: [RuntimeValue], cursor: String?, total: Int) {
        let end = min(offset + 30, page.rows.count)
        guard offset <= end else { throw MusicServiceError.invalidRequest }
        let cursor: String? = end < page.rows.count || page.nextPage != nil ? "\(key):\(end)" : nil
        let total = page.nextPage == nil ? page.rows.count : 0
        return (Array(page.rows[offset..<end]), cursor, total)
    }

    func nativeTrackPage(_ slice: (rows: [RuntimeValue], cursor: String?, total: Int),
                         platform: MusicAccountPlatform,
                         dailyStyle: MusicDailyStyleSelection? = nil) throws -> MusicNativeTrackPage {
        .init(items: try slice.rows.map { .init(platform: platform.rawValue, data: try RuntimeJSON.encode($0)) },
              total: slice.total, cursor: slice.cursor, dailyStyle: dailyStyle)
    }

    func pagedRows(platform: MusicAccountPlatform, operation: String, argument: String = "",
                   cursor: String?, queueLease: MusicAccountQueueLease? = nil,
                   fetch: (_ provider: MusicNodeAccountProvider, _ token: AccountSessionToken, _ own: UUID, _ revision: Int, _ pageNumber: Int) async throws -> (rows: [RuntimeValue], nextPage: Int?, expiresAt: Date?)) async throws -> (rows: [RuntimeValue], cursor: String?, total: Int) {
        let own = epoch, accountBinding = accountEpochs[platform]
        do {
            let token: AccountSessionToken
            if let queueLease {
                guard queueLease.platform == platform else { throw MusicServiceError.invalidRequest }
                try await checkAccountQueue(queueLease)
                guard let binding = accountQueueBindings[queueLease.id], binding.lease === queueLease else {
                    throw MusicServiceError.staleResult
                }
                token = binding.credential.token
            } else {
                token = try await session(platform)
            }
            let revision = revisions[platform, default: 0]
            let credentialLease = try await accounts.credentialUpdateLease(for: token)
            defer { credentialLease.revoke() }
            guard credentialLease.phase == .signedIn else { throw MusicServiceError.authRequired }
            guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
            try await validToken(token, platform: platform, own: own, revision: revision)

            var requestedPage = 1
            var continuation: (key: String, request: UUID)?
            defer {
                if let continuation, pages[continuation.key]?.continuationRequest == continuation.request {
                    pages[continuation.key]?.continuationRequest = nil
                }
            }
            if let cursor, !cursor.isEmpty {
                let parts = cursor.split(separator: ":", omittingEmptySubsequences: false)
                guard parts.count == 2, let offset = Int(parts[1]), offset >= 0,
                      let saved = pages[String(parts[0])], saved.platform == platform, saved.token == token,
                      saved.queueLeaseID == queueLease?.id,
                      saved.revision == revisions[platform, default: 0],
                      saved.expires > now(), saved.operation == operation, saved.argument == argument else {
                    throw MusicServiceError.staleResult
                }
                if offset < saved.rows.count {
                    let key = String(parts[0])
                    try await validToken(token, platform: platform, own: own, revision: revision)
                    if let queueLease { try await checkAccountQueue(queueLease) }
                    return try slicePage(saved, key: key, offset: offset)
                }
                guard offset == saved.rows.count, let next = saved.nextPage, operation == "recommendations" else {
                    throw MusicServiceError.staleResult
                }
                if let key = saved.continuationKey {
                    guard let continued = pages[key], continued.expires > now(),
                          continued.platform == saved.platform, continued.token == saved.token,
                          continued.revision == saved.revision, continued.queueLeaseID == saved.queueLeaseID,
                          continued.operation == saved.operation, continued.argument == saved.argument else {
                        throw MusicServiceError.staleResult
                    }
                    return try slicePage(continued, key: key, offset: 0)
                }
                guard saved.continuationRequest == nil else { throw MusicServiceError.busy }
                let request = UUID(), key = String(parts[0])
                pages[key]?.continuationRequest = request
                continuation = (key, request)
                requestedPage = next
            }

            let fetched = try await fetch(provider, token, own, revision, requestedPage)
            try await validToken(token, platform: platform, own: own, revision: revision)
            if let queueLease { try await checkAccountQueue(queueLease) }
            if let continuation {
                guard let pending = pages[continuation.key],
                      pending.continuationRequest == continuation.request,
                      pending.expires > now() else { throw MusicServiceError.staleResult }
            }
            let key = UUID().uuidString.lowercased()
            let result = try storePage(fetched.rows, platform: platform, token: token, operation: operation,
                                       argument: argument, nextPage: fetched.nextPage.flatMap { $0 > requestedPage ? $0 : nil },
                                       expiresAt: fetched.expiresAt, queueLeaseID: queueLease?.id, key: key)
            if let continuation { pages[continuation.key]?.continuationKey = key }
            if queueLease == nil {
                try check(own)
                guard accountBinding == accountEpochs[platform] else { throw MusicServiceError.staleResult }
            }
            return result
        } catch {
            throw safe(error)
        }
    }

    public func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage {
        let slice = try await pagedRows(platform: platform, operation: "recommendations", argument: kind, cursor: cursor) {
            provider, token, own, revision, pageNumber in
            let value = try await self.authenticated(platform, token: token, own: own, revision: revision) {
                try await provider.recommendations(kind: kind, page: pageNumber)
            }
            return (value.rows, value.nextPage, value.expiresAt)
        }
        return try nativeTrackPage(slice, platform: platform)
    }

    public func nativeCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        let slice = try await pagedRows(platform: platform, operation: "collections", cursor: cursor) {
            provider, token, own, revision, _ in
            let rows = try await self.authenticated(platform, token: token, own: own, revision: revision) {
                try await provider.collectionRows()
            }
            return (rows, nil, nil)
        }
        let rows = slice.rows.map { row in
            MusicAccountCollection(id: row[music: "id"].musicText, title: row[music: "title"].musicText,
                                   count: MusicPlatformDTO.optionalCount(row[music: "count"]),
                                   owned: row[music: "owned"].musicBool)
        }
        return .init(items: rows, cursor: slice.cursor)
    }

    public func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicNativeTrackPage {
        let slice = try await pagedRows(platform: platform, operation: "tracks", argument: collectionID, cursor: cursor) {
            provider, token, own, revision, _ in
            let rows = try await self.authenticated(platform, token: token, own: own, revision: revision) {
                try await provider.tracks(collectionID: collectionID)
            }
            return (rows, nil, nil)
        }
        return try nativeTrackPage(slice, platform: platform)
    }

    private func recordGrant(platform: MusicAccountPlatform, nativeID: String, token: AccountSessionToken,
                             revision: Int, credentialRevision: AccountCredentialUpdateRevision, qualities: [String]) {
        grants = grants.filter { $0.value.expires > now() }
        if grants.count >= 128, let oldest = grants.min(by: { $0.value.expires < $1.value.expires })?.key {
            grants.removeValue(forKey: oldest)
        }
        let key = GrantKey(platform: platform, nativeID: nativeID)
        if let existing = grants[key], existing.token == token, existing.revision == revision,
           existing.credentialRevision == credentialRevision, existing.qualities == qualities, existing.expires > now() {
            return
        }
        grants[key] = Grant(id: UUID(), token: token, revision: revision, credentialRevision: credentialRevision,
                            qualities: qualities, expires: now().addingTimeInterval(120))
    }

    public func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        if platform == .netease, providers[platform] != nil {
            let binding = try await nativeHomeBinding()
            if !binding.loggedIn { return [try guestStandardOption(nativeID: nativeID, sourceKey: sourceKey, binding: binding)] }
        }
        let own = epoch, binding = accountEpochs[platform]
        do {
            let token = try await session(platform)
            let revision = revisions[platform, default: 0]
            let credentialLease = try await accounts.credentialUpdateLease(for: token)
            defer { credentialLease.revoke() }
            guard credentialLease.phase == .signedIn else { throw MusicServiceError.authRequired }
            try await validToken(token, platform: platform, own: own, revision: revision)
            let values = try await sharedCapabilities(platform: platform, nativeID: nativeID, token: token,
                credentialRevision: credentialLease.revision, own: own, revision: revision)
            try await checkURLCredential(credentialLease, platform: platform, own: own, revision: revision)
            recordGrant(platform: platform, nativeID: nativeID, token: token, revision: revision,
                        credentialRevision: credentialLease.revision, qualities: values)
            try check(own)
            guard binding == accountEpochs[platform] else { throw MusicServiceError.staleResult }
            return [.init(installationId: "official:" + instance, generation: revisions[platform, default: 0],
                siteId: platform.rawValue, sourceKey: sourceKey, qualities: values,
                title: platform.title + " 官方音源", version: "1",
                contentHash: musicSHA256Hex(token.generation.uuidString))]
        } catch {
            throw safe(error)
        }
    }

    public func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        guard selection.siteId == platform.rawValue else { throw MusicServiceError.targetChanged }
        try check()
        if platform == .netease, isGuestAudioSelection(selection) {
            return try await guestStandardResolve(nativeID: nativeID, selection: selection)
        }
        guard selection.installationId == "official:" + instance,
              selection.generation == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        let own = epoch, account = accountEpochs[platform]
        do {
            let token = try await session(platform)
            let revision = revisions[platform, default: 0]
            let credentialLease = try await accounts.credentialUpdateLease(for: token)
            defer { credentialLease.revoke() }
            guard credentialLease.phase == .signedIn else { throw MusicServiceError.authRequired }
            try await validToken(token, platform: platform, own: own, revision: revision)
            guard selection.generation == revisions[platform, default: 0],
                  let grant = grants[GrantKey(platform: platform, nativeID: nativeID)],
                  grant.token == token, grant.revision == revisions[platform, default: 0],
                  grant.credentialRevision == credentialLease.revision,
                  grant.expires > now(), grant.qualities.contains(selection.quality) else {
                throw MusicServiceError.staleResult
            }
            let cached = try await resolveCachedAudio(platform: platform, mid: nativeID, quality: selection.quality,
                grant: grant, credential: credentialLease, own: own, rejectedURL: nil)
            try check(own)
            guard accountEpochs[platform] == account, revisions[platform, default: 0] == selection.generation else {
                throw MusicServiceError.staleResult
            }
            return try authorizedAudio(cached, platform: platform)
        } catch {
            throw safe(error)
        }
    }

    public func checkOfficialSource(_ platform: MusicAccountPlatform) async throws {
        let ids: [MusicAccountPlatform: String] = [.netease: "347230", .qq: "0039MnYb0qxYhV",
            .kugou: "30ece05c17d4e9ec8ba0ef9f96e7a07c"]
        guard let mid = ids[platform] else { throw MusicServiceError.unsupported }
        let own = epoch
        if platform == .netease, let provider = providers[platform] {
            let value = try await provider.resolve(mid: mid, quality: "128k")
            try check(own)
            _ = try Self.cachedAudioFromRuntime(value)
            return
        }
        let options = try await nativeOptions(platform: platform, nativeID: mid, sourceKey: platform.rawValue + ":" + mid)
        guard options.first?.qualities.contains("128k") == true,
              let grant = grants[GrantKey(platform: platform, nativeID: mid)] else { throw MusicServiceError.sourceUnavailable }
        let value = try await resolveSharedAudio(platform: platform, mid: mid, quality: "128k", grant: grant, own: own)
        try check(own)
        _ = try Self.cachedAudioFromRuntime(value)
    }

    public func candidatePlatforms() -> Set<MusicAccountPlatform> {
        guard !closed else { return [] }
        return Set(MusicAccountPlatform.allCases.filter { platform in
            guard (try? configured(platform)) != nil, !requiresExplicitRestore.contains(platform),
                  !transitions.contains(platform) else { return false }
            if platform == .netease, providers[platform] != nil { return true }
            return tokens[platform] != nil || !inspected.contains(platform)
        })
    }

    func guestAudioInstallation(_ binding: MusicNativeHomeBinding) -> String {
        "official:" + instance + ":guest:" + binding.revision.uuidString.lowercased()
    }

    func isGuestAudioSelection(_ selection: MusicPlaybackSelection) -> Bool {
        selection.installationId.hasPrefix("official:" + instance + ":guest:")
    }

    func guestRecoveryBinding(_ selection: MusicPlaybackSelection) throws -> MusicNativeHomeBinding {
        try check()
        guard let binding = homeBinding?.value, !binding.loggedIn,
              guestAudioInstallation(binding) == selection.installationId,
              selection.siteId == "wy", selection.quality == "128k",
              selection.generation == revisions[.netease, default: 0] else { throw MusicServiceError.staleResult }
        _ = try requireNativeHomeBinding(binding)
        return binding
    }

    static func cachedAudioFromRuntime(_ value: RuntimeValue) throws -> MusicCachedAudio {
        guard case .object(let fields) = value else { throw MusicServiceError.invalidMediaCandidate }
        let audio = try MusicPlatformDTO.audio(value)
        guard MusicNativeAccountURLCache.validateURL(audio.url),
              audio.expiresAt == nil || audio.expiresAt!.timeIntervalSince1970 > 0 else {
            throw MusicServiceError.invalidMediaCandidate
        }
        let full: Bool? = switch fields["full"] {
        case nil, .null?: nil
        case .bool(let b)?: b
        default: throw MusicServiceError.invalidMediaCandidate
        }
        let actualQuality: String? = switch fields["actualQuality"] {
        case nil, .null?: nil
        case .string(let s)?: s
        default: throw MusicServiceError.invalidMediaCandidate
        }
        func finiteNumber(_ name: String) throws -> Double? {
            guard let raw = fields[name], raw != .null else { return nil }
            guard case .number(let n) = raw, n.isFinite else { throw MusicServiceError.invalidMediaCandidate }
            return n
        }
        return MusicCachedAudio(url: audio.url, full: full, actualQuality: actualQuality,
            bitrate: try finiteNumber("bitrate"), sampleRate: try finiteNumber("sampleRate"),
            duration: try finiteNumber("duration"), expiresAtMs: try finiteNumber("expiresAt"))
    }

    func authorizedAudio(_ record: MusicCachedAudio, platform: MusicAccountPlatform) throws -> MusicNativeAudio {
        guard MusicNativeAccountURLCache.validateURL(record.url) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        let authorization = playbackAuthorizations[platform] ?? MusicQueueAuthorization()
        playbackAuthorizations[platform] = authorization
        try authorization.check()
        let delivery: MusicAudioDelivery? = record.actualQuality.flatMap { q in
            q.isEmpty ? nil : .init(quality: q, evidence: .officialResponse,
                                    bitrate: record.bitrate, sampleRate: record.sampleRate, duration: record.duration)
        }
        return .init(url: record.url, expiresAt: record.expiresAt,
            fileCachePolicy: .init(allowed: record.full == true),
            delivery: delivery, playbackAuthorization: authorization, duration: record.duration)
    }

    func guestStandardOption(nativeID: String, sourceKey: String,
                             binding: MusicNativeHomeBinding) throws -> MusicPlaybackOption {
        _ = try requireNativeHomeBinding(binding)
        guard !binding.loggedIn else { throw MusicServiceError.targetChanged }
        guestAudioGrants = guestAudioGrants.filter { $0.value.expires > now() }
        if guestAudioGrants.count >= 128,
           let oldest = guestAudioGrants.min(by: { $0.value.expires < $1.value.expires })?.key {
            guestAudioGrants.removeValue(forKey: oldest)
        }
        let generation = revisions[.netease, default: 0]
        guestAudioGrants[nativeID] = .init(binding: binding, generation: generation, expires: now().addingTimeInterval(120))
        return .init(installationId: guestAudioInstallation(binding), generation: generation, siteId: "wy",
            sourceKey: sourceKey, qualities: ["128k"], title: "网易云音乐 官方音源", version: "1",
            contentHash: musicSHA256Hex(binding.scope))
    }

    private func checkGuestStandard(selection: MusicPlaybackSelection,
                                    grant: GuestAudioGrant, fresh: Bool = true) throws {
        try check()
        guard selection.installationId == guestAudioInstallation(grant.binding), selection.siteId == "wy",
              selection.quality == "128k", selection.generation == revisions[.netease, default: 0],
              selection.generation == grant.generation, !grant.binding.loggedIn,
              !fresh || grant.expires > now() else { throw MusicServiceError.staleResult }
        _ = try requireNativeHomeBinding(grant.binding)
    }

    func guestStandardResolve(nativeID: String, selection: MusicPlaybackSelection,
                              rejectedURL: URL? = nil) async throws -> MusicNativeAudio {
        guard let grant = guestAudioGrants[nativeID],
              let provider = providers[.netease] else {
            throw MusicServiceError.staleResult
        }
        try checkGuestStandard(selection: selection, grant: grant)
        try await checkNativeHomeBinding(grant.binding)
        let scope = musicSHA256Hex(grant.binding.scope)
        let key = MusicNativeAccountURLCache.Key(scope: scope, platform: "wy", nativeID: nativeID, quality: "128k")
        let trace = MusicRequestTrace(.audio, platform: .netease)
        if let rejectedURL { urlCache.reject(rejectedURL, for: key, at: now()) }
        if let cached = urlCache.value(for: key, at: now()) {
            trace.record(.cacheHit)
            try checkGuestStandard(selection: selection, grant: grant)
            return try authorizedAudio(cached, platform: .netease)
        }
        let request = AudioRequestKey(cache: key, epoch: epoch, revision: grant.generation,
                                      grant: grant.binding.revision, rejectedURL: rejectedURL)
        let cachedRecord = try await audioRequests.value(for: request, lifetime: 0, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) {
            try await self.checkGuestStandard(selection: selection, grant: grant)
            let raw = try await self.run(.netease) {
                try grant.binding.authorization.check()
                return try await provider.resolve(mid: nativeID, quality: "128k")
            }
            return try Self.cachedAudioFromRuntime(raw)
        }
        try await checkNativeHomeBinding(grant.binding)
        try checkGuestStandard(selection: selection, grant: grant)
        let audio = try authorizedAudio(cachedRecord, platform: .netease)
        guard audio.expiresAt == nil || audio.expiresAt! > now(), audio.url != rejectedURL,
              !urlCache.isRejected(audio.url, for: key, at: now()) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        try grant.binding.authorization.perform { urlCache.put(cachedRecord, for: key, at: now()) }
        return audio
    }

    func recoveryToken(platform: MusicAccountPlatform,
                       selection: MusicPlaybackSelection, own: UUID? = nil,
                       expectedToken: AccountSessionToken? = nil) throws -> AccountSessionToken {
        try check(own)
        guard selection.siteId == platform.rawValue else { throw MusicServiceError.targetChanged }
        guard selection.installationId == "official:" + instance,
              selection.generation == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        _ = try configured(platform)
        guard let token = tokens[platform], expectedToken == nil || expectedToken == token else {
            throw MusicServiceError.staleResult
        }
        try checkLiveURLAccount(token, platform: platform, own: own ?? epoch, revision: selection.generation)
        return token
    }

    private func checkLiveURLAccount(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                                     own: UUID, revision: Int) throws {
        try check(own)
        guard tokens[platform] == token, validated.contains(platform),
              revisions[platform, default: 0] == revision,
              !transitions.contains(platform), !restoring.contains(platform), !localCacheRestoring.contains(platform),
              login?.platform != platform, !requiresExplicitRestore.contains(platform),
              credentialUpdates[platform] == nil else {
            throw MusicServiceError.staleResult
        }
    }

    public func nativeRecoveryOptions(platform: MusicAccountPlatform, nativeID: String,
                                      selection: MusicPlaybackSelection) async throws -> [MusicPlaybackOption] {
        do {
            if platform == .netease, isGuestAudioSelection(selection) {
                let binding = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                _ = try guestRecoveryBinding(selection)
                return [try guestStandardOption(nativeID: nativeID, sourceKey: selection.sourceKey, binding: binding)]
            }
            let token = try recoveryToken(platform: platform, selection: selection)
            let own = epoch, revision = selection.generation
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            _ = try recoveryToken(platform: platform, selection: selection, own: own, expectedToken: token)
            guard lease.phase == .signedIn else { throw MusicServiceError.staleResult }
            let key = GrantKey(platform: platform, nativeID: nativeID)
            let priorGrant = grants[key]?.id
            let values: [String]
            do {
                values = try await sharedCapabilities(platform: platform, nativeID: nativeID, token: token,
                    credentialRevision: lease.revision, own: own, revision: revision)
            } catch {
                if grants[key]?.id == priorGrant { grants.removeValue(forKey: key) }
                throw error
            }
            try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
            _ = try recoveryToken(platform: platform, selection: selection, own: own, expectedToken: token)
            let qualities = values.filter { !$0.isEmpty }
            guard qualities.count == values.count, qualities.contains(selection.quality) else {
                if grants[key]?.id == priorGrant { grants.removeValue(forKey: key) }
                throw MusicServiceError.qualityDisabled
            }
            recordGrant(platform: platform, nativeID: nativeID, token: token, revision: revision,
                        credentialRevision: lease.revision, qualities: qualities)
            return [.init(installationId: selection.installationId, generation: revision, siteId: selection.siteId,
                          sourceKey: selection.sourceKey, qualities: [selection.quality],
                          title: platform.title + " 官方音源", version: "1",
                          contentHash: musicSHA256Hex(token.generation.uuidString))]
        } catch { throw safe(error) }
    }

    func sharedCapabilities(platform: MusicAccountPlatform, nativeID: String, token: AccountSessionToken,
                            credentialRevision: AccountCredentialUpdateRevision,
                            own: UUID, revision: Int) async throws -> [String] {
        let key = CapabilityRequestKey(platform: platform, nativeID: nativeID, epoch: own, revision: revision,
            scope: try nativeRecordScope(identity: token.identity, revision: credentialRevision))
        return try await capabilityRequests.value(for: key, lifetime: 0) {
            try await self.readSharedCapabilities(platform: platform, nativeID: nativeID, token: token,
                credentialRevision: credentialRevision, own: own, revision: revision)
        }
    }

    private func readSharedCapabilities(platform: MusicAccountPlatform, nativeID: String, token: AccountSessionToken,
                                        credentialRevision: AccountCredentialUpdateRevision,
                                        own: UUID, revision: Int) async throws -> [String] {
        let lease = try await accounts.credentialUpdateLease(for: token)
        defer { lease.revoke() }
        try checkLiveURLAccount(token, platform: platform, own: own, revision: revision)
        guard lease.phase == .signedIn, lease.revision == credentialRevision else { throw MusicServiceError.staleResult }
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        let values = try await authenticated(platform, token: token, own: own, revision: revision) {
            try await provider.capabilities(mid: nativeID)
        }
        try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
        return values
    }

    public func nativeRefreshAudio(platform: MusicAccountPlatform, nativeID: String,
                                   selection: MusicPlaybackSelection, rejectedURL: URL) async throws -> MusicNativeAudio {
        do {
            if platform == .netease, isGuestAudioSelection(selection) {
                return try await guestStandardResolve(nativeID: nativeID, selection: selection, rejectedURL: rejectedURL)
            }
            let token = try recoveryToken(platform: platform, selection: selection)
            let own = epoch, revision = selection.generation
            guard let grant = grants[GrantKey(platform: platform, nativeID: nativeID)],
                  grant.token == token, grant.revision == revision else { throw MusicServiceError.staleResult }
            try checkURLGrant(grant, platform: platform, mid: nativeID, quality: selection.quality, own: own)
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            _ = try recoveryToken(platform: platform, selection: selection, own: own, expectedToken: token)
            guard lease.phase == .signedIn, grant.credentialRevision == lease.revision else { throw MusicServiceError.staleResult }
            let cached = try await resolveCachedAudio(platform: platform, mid: nativeID, quality: selection.quality,
                                                      grant: grant, credential: lease, own: own, rejectedURL: rejectedURL)
            _ = try recoveryToken(platform: platform, selection: selection, own: own, expectedToken: token)
            return try authorizedAudio(cached, platform: platform)
        } catch { throw safe(error) }
    }

    func checkURLCredential(_ lease: AccountCredentialUpdateLease, platform: MusicAccountPlatform,
                            own: UUID, revision: Int) async throws {
        try checkLiveURLAccount(lease.token, platform: platform, own: own, revision: revision)
        let observed = try await accounts.credentialUpdateLease(for: lease.token)
        defer { observed.revoke() }
        try checkLiveURLAccount(lease.token, platform: platform, own: own, revision: revision)
        guard observed.phase == .signedIn, observed.revision == lease.revision,
              observed.credential == lease.credential else { throw MusicServiceError.staleResult }
    }

    private func checkURLGrant(_ grant: Grant, platform: MusicAccountPlatform, mid: String,
                               quality: String, own: UUID) throws {
        try checkLiveURLAccount(grant.token, platform: platform, own: own, revision: grant.revision)
        guard grants[GrantKey(platform: platform, nativeID: mid)]?.id == grant.id,
              grant.expires > now(), grant.qualities.contains(quality) else { throw MusicServiceError.staleResult }
    }

    func resolveCachedAudio(platform: MusicAccountPlatform, mid: String, quality: String,
                            grant: Grant, credential: AccountCredentialUpdateLease,
                            own: UUID, rejectedURL: URL?) async throws -> MusicCachedAudio {
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        let key = MusicNativeAccountURLCache.Key(
            scope: try nativeRecordScope(identity: grant.token.identity, revision: credential.revision),
            platform: platform.rawValue, nativeID: mid, quality: quality)
        let trace = MusicRequestTrace(.audio, platform: .init(accountPlatform: platform))
        if let rejectedURL { urlCache.reject(rejectedURL, for: key, at: now()) }
        if urlCache.value(for: key, at: now()) != nil {
            try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
            try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
            if let cached = urlCache.value(for: key, at: now()) {
                trace.record(.cacheHit)
                return cached
            }
        }
        let request = AudioRequestKey(cache: key, epoch: own, revision: grant.revision,
                                      grant: grant.id, rejectedURL: rejectedURL)
        let cachedRecord = try await audioRequests.value(for: request, lifetime: 0, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) {
            let raw = try await self.resolveSharedAudio(platform: platform, mid: mid, quality: quality, grant: grant, own: own)
            return try Self.cachedAudioFromRuntime(raw)
        }
        try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        guard cachedRecord.expiresAt == nil || cachedRecord.expiresAt! > now(),
              cachedRecord.url != rejectedURL,
              !urlCache.isRejected(cachedRecord.url, for: key, at: now()) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        urlCache.put(cachedRecord, for: key, at: now())
        return cachedRecord
    }

    func resolveSharedAudio(platform: MusicAccountPlatform, mid: String, quality: String,
                            grant: Grant, own: UUID) async throws -> RuntimeValue {
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        let lease = try await accounts.credentialUpdateLease(for: grant.token)
        defer { lease.revoke() }
        guard lease.revision == grant.credentialRevision, lease.phase == .signedIn else {
            throw MusicServiceError.staleResult
        }
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        return try await authenticated(platform, token: grant.token, own: own, revision: grant.revision) {
            try await provider.resolve(mid: mid, quality: quality)
        }
    }

    public func nativeChorus(nativeID: String, selection: MusicPlaybackSelection,
                             recordingDuration: Double, actualDuration: Double, cachedOnly: Bool) async throws -> MusicChorusPoint? {
        do {
            try check()
            guard let platform = MusicAccountPlatform(rawValue: selection.siteId), platform != .qq,
                  let provider = providers[platform] else { return nil }
            let own = epoch
            let scope = try await nativeCacheScope()
            let key = MusicAccountChorusCache.Key(scope: scope.scope, platform: platform.rawValue,
                                                  nativeID: nativeID, duration: recordingDuration)
            try check(own); try scope.authorization.check()
            if let point = chorusCache.value(key, actualDuration: actualDuration, at: now()) {
                try await checkNativeCacheScope(scope)
                return try scope.authorization.perform { point }
            }
            if cachedOnly { return nil }
            if platform == .netease, isGuestAudioSelection(selection) {
                let binding = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                let point = try await run(.netease) { try await provider.chorus(nativeID: nativeID) }
                _ = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                try await checkNativeCacheScope(scope)
                guard let point, point.fits(recordingDuration: recordingDuration, actualDuration: actualDuration) else { return nil }
                return try scope.authorization.perform {
                    chorusCache.put(point, key: key, at: now()); return point
                }
            }
            let token = try recoveryToken(platform: platform, selection: selection, own: own)
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            try checkLiveURLAccount(token, platform: platform, own: own, revision: selection.generation)
            guard lease.phase == .signedIn else { throw MusicServiceError.staleResult }
            let point = try await authenticated(platform, token: token, own: own, revision: selection.generation) {
                try await provider.chorus(nativeID: nativeID)
            }
            try await checkURLCredential(lease, platform: platform, own: own, revision: selection.generation)
            _ = try recoveryToken(platform: platform, selection: selection, own: own, expectedToken: token)
            try await checkNativeCacheScope(scope)
            guard let point, point.fits(recordingDuration: recordingDuration, actualDuration: actualDuration) else { return nil }
            return try scope.authorization.perform {
                chorusCache.put(point, key: key, at: now()); return point
            }
        } catch { throw safe(error) }
    }
}
