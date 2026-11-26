import Foundation
import MeowAccounts
import MeowDebug

public struct MusicOfflineAccountRecord: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let platform: MusicAccountPlatform
    public let userID: String
    public let displayName: String
    public let avatar: String?
    public let cookie: String
    public let credentials: [String: String]

    public init(platform: MusicAccountPlatform, userID: String, displayName: String = "",
                avatar: String? = nil, cookie: String = "", credentials: [String: String] = [:]) {
        self.platform = platform
        self.userID = userID
        self.displayName = displayName
        self.avatar = avatar
        self.cookie = cookie
        self.credentials = credentials
    }

    public var description: String { "MusicOfflineAccountRecord(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

extension NativeMusicAccountService {
    public func exportOfflineAccounts() async throws -> [MusicOfflineAccountRecord] {
        try check()
        let own = epoch
        var records: [MusicOfflineAccountRecord] = []
        for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
            let identity = try configured(platform)
            let stored: AccountCredential?
            do { stored = try await accounts.readOffline(for: identity) }
            catch AccountSessionError.invalidState { continue }
            try check(own)
            guard let stored,
                  stored.siteKey == platform.rawValue,
                  !stored.userID.isEmpty,
                  !stored.cookie.isEmpty else {
                continue
            }
            let userID = stored.userID
            let cookie = stored.cookie
            let fields = Self.parseCookieFields(cookie)
            let nickname = profiles[platform] ?? displayProfiles[platform]?.nickname ?? ""
            let avatar = displayProfiles[platform]?.avatarURL?.absoluteString
            records.append(MusicOfflineAccountRecord(
                platform: platform,
                userID: userID,
                displayName: nickname,
                avatar: avatar,
                cookie: cookie,
                credentials: fields
            ))
        }
        return records
    }

    public func replaceOfflineAccounts(_ targets: [MusicAccountPlatform: MusicOfflineAccountRecord?]) async throws {
        try check()
        let own = epoch
        for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
            guard let targetOpt = targets[platform] else { continue }
            try check(own)
            let identity = try configured(platform)
            guard !transitions.contains(platform), !restoring.contains(platform),
                  !localCacheRestoring.contains(platform), login?.platform != platform else {
                continue
            }
            let candidate = Self.buildOfflineCredential(platform: platform, record: targetOpt)
            if targetOpt != nil, candidate == nil { continue }
            transitions.insert(platform)
            defer {
                transitions.remove(platform)
                notifyAccountSession(platform)
            }
            let binding = await rotate(platform)
            guard accountEpochs[platform] == binding, epoch == own, !closed else { continue }
            pendingWrites += 1
            let token: AccountSessionToken?
            do {
                token = try await accounts.replaceOffline(for: identity, credential: candidate)
            } catch {
                pendingWrites -= 1
                continue
            }
            pendingWrites -= 1
            tokens[platform] = token
            if token != nil {
                inspected.insert(platform)
            } else {
                inspected.remove(platform)
            }
            validated.remove(platform)
            requiresExplicitRestore.remove(platform)
            restoreNativeIDs.removeValue(forKey: platform)
            profiles.removeValue(forKey: platform)
            displayProfiles.removeValue(forKey: platform)
            displayProfileDeadlines.removeValue(forKey: platform)
            notifyAccountSession(platform, committedRecord: true)
            notifyNativeHome(platform)
            await drainCacheLifecycle()
        }
    }

    private static func parseCookieFields(_ cookie: String) -> [String: String] {
        var result: [String: String] = [:]
        for part in cookie.split(separator: ";") {
            guard let eq = part.firstIndex(of: "=") else { continue }
            let key = part[..<eq].trimmingCharacters(in: .whitespacesAndNewlines)
            let value = part[part.index(after: eq)...].trimmingCharacters(in: .whitespacesAndNewlines)
            if !key.isEmpty, !value.isEmpty {
                result[key] = value
            }
        }
        return result
    }

    private static func buildOfflineCredential(
        platform: MusicAccountPlatform,
        record: MusicOfflineAccountRecord?
    ) -> AccountCredential? {
        guard let record else { return nil }
        var fields = parseCookieFields(record.cookie)
        for (k, v) in record.credentials {
            let cleanK = k.trimmingCharacters(in: .whitespacesAndNewlines)
            let cleanV = v.trimmingCharacters(in: .whitespacesAndNewlines)
            if !cleanK.isEmpty, !cleanV.isEmpty {
                fields[cleanK] = cleanV
            }
        }
        func hasControlOrNul(_ s: String) -> Bool {
            s.unicodeScalars.contains { $0.value < 0x20 || $0.value == 0x7f }
        }
        var sanitizedFields: [String: String] = [:]
        for (k, v) in fields {
            if !hasControlOrNul(k), !k.contains(";"), !k.contains("="),
               !hasControlOrNul(v), !v.contains(";") {
                sanitizedFields[k] = v
            }
        }
        let cookieString: String
        if !sanitizedFields.isEmpty {
            cookieString = sanitizedFields.keys.sorted().map { "\($0)=\(sanitizedFields[$0]!)" }.joined(separator: "; ")
        } else {
            let trimmed = record.cookie.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, !hasControlOrNul(trimmed) else { return nil }
            cookieString = trimmed
        }

        var userID = record.userID.trimmingCharacters(in: .whitespacesAndNewlines)
        if userID.isEmpty || hasControlOrNul(userID) {
            switch platform {
            case .qq:
                userID = sanitizedFields["musicid"] ?? sanitizedFields["str_musicid"] ?? ""
            case .kugou:
                userID = sanitizedFields["userid"] ?? ""
            case .netease:
                userID = "imported"
            }
        }
        if userID.isEmpty { userID = "imported" }
        return try? AccountCredential(siteKey: platform.rawValue, userID: userID, cookie: cookieString)
    }
    func revokeAccountImports(_ platform: MusicAccountPlatform? = nil) {
        for batch in transferBatches.values {
            for item in batch.items where platform == nil || item.entry.platform == platform {
                item.baseline?.revoke()
            }
        }
    }

    public func exportAccountFile() async throws -> MusicAccountFilePayload {
        do { return try await makeAccountFile() }
        catch let error as MusicAccountTransferError { throw error }
        catch { throw safe(error) }
    }

    private func makeAccountFile() async throws -> MusicAccountFilePayload {
        try check()
        let own = epoch
        let bindings = accountEpochs
        var entries: [MusicAccountTransferEntry] = []
        var leases: [AccountReplacementLease] = []
        defer { for lease in leases { lease.revoke() } }
        for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
            let binding = accountEpochs[platform]
            let lease: AccountReplacementLease
            do { lease = try await accounts.prepareReplacement(for: configured(platform)) }
            catch AccountSessionError.invalidState { continue }
            leases.append(lease)
            try check(own)
            guard binding == accountEpochs[platform], !transitions.contains(platform) else { throw MusicServiceError.staleResult }
            guard let credential = lease.credential else { continue }
            guard try await accounts.replacementIsCurrent(lease) else { throw MusicServiceError.staleResult }
            try check(own)
            entries.append(try MusicAccountTransferCodec.entry(credential, platform: platform))
        }
        guard try await accounts.replacementsAreCurrent(leases) else { throw MusicServiceError.staleResult }
        try check(own)
        guard bindings == accountEpochs else { throw MusicServiceError.staleResult }
        guard !entries.isEmpty else { throw MusicAccountTransferError.noAccounts }
        return try MusicAccountTransferCodec.encode(entries)
    }

    public func prepareAccountImport(_ payload: MusicAccountFilePayload) async throws -> MusicAccountImportPlan {
        let entries = try MusicAccountTransferCodec.decode(payload)
        try check()
        let own = epoch
        var items: [MusicAccountImportBatch.Item] = []
        do {
            for entry in entries {
                let binding = accountEpochs[entry.platform]
                let lease = try await accounts.prepareReplacement(for: configured(entry.platform))
                items.append(.init(entry: entry, baseline: lease, epoch: binding))
                try check(own)
                guard binding == accountEpochs[entry.platform] else { throw MusicServiceError.staleResult }
            }
            let batch = MusicAccountImportBatch(epoch: own, items: items)
            let plan = MusicAccountImportPlan(conflicts: items.compactMap {
                $0.baseline?.hasCredential == true ? $0.entry.platform : nil
            }, cancel: { [weak self] id in
                for item in batch.items { item.baseline?.revoke() }
                Task { await self?.releaseCancelledAccountImport(id) }
            })
            transferBatches[plan.id] = batch
            return plan
        } catch {
            for item in items { item.baseline?.revoke() }
            throw safe(error)
        }
    }

    public func discardAccountImport(_ plan: MusicAccountImportPlan) {
        plan.cancel()
        transferBatches.removeValue(forKey: plan.id)
    }

    private func releaseCancelledAccountImport(_ id: UUID) {
        if !runningTransfers.contains(id) { transferBatches.removeValue(forKey: id) }
    }

    public func importAccounts(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy) async -> MusicAccountImportSummary {
        guard let batch = transferBatches[plan.id], runningTransfers.insert(plan.id).inserted else { return .init() }
        return await withTaskCancellationHandler {
            await executeAccountImport(plan, policy: policy, batch: batch)
        } onCancel: { plan.cancel() }
    }

    private func executeAccountImport(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy,
                                      batch: MusicAccountImportBatch) async -> MusicAccountImportSummary {
        defer { plan.cancel(); transferBatches.removeValue(forKey: plan.id); runningTransfers.remove(plan.id) }
        var summary = MusicAccountImportSummary()
        for item in batch.items {
            let platform = item.entry.platform
            if Task.isCancelled || closed || epoch != batch.epoch || plan.isCancelled {
                summary.cancelled.append(platform); continue
            }
            if policy == .skip, item.baseline?.hasCredential == true {
                summary.skipped.append(platform); continue
            }
            guard let lease = item.baseline, !transitions.contains(platform),
                  !restoring.contains(platform), !localCacheRestoring.contains(platform),
                  login?.platform != platform, item.epoch == accountEpochs[platform],
                  let provider = providers[platform] else { summary.failed.append(platform); continue }
            do {
                guard try await accounts.replacementIsCurrent(lease) else { throw MusicServiceError.staleResult }
                try check(batch.epoch)
                let candidate = try MusicAccountTransferCodec.credential(item.entry)
                let value = try await run(platform) {
                    guard await provider.nativeID(candidate) == item.entry.userID else { throw MusicServiceError.targetChanged }
                    let verified = try await provider.verify(candidate)
                    guard verified.nativeID == item.entry.userID,
                          await provider.nativeID(verified.credential) == item.entry.userID else { throw MusicServiceError.targetChanged }
                    return verified
                }
                try check(batch.epoch)
                guard !plan.isCancelled, item.epoch == accountEpochs[platform], !transitions.contains(platform),
                      !restoring.contains(platform), !localCacheRestoring.contains(platform),
                      login?.platform != platform else { throw MusicServiceError.staleResult }
                var shouldScheduleFeedback = false
                transitions.insert(platform)
                defer {
                    transitions.remove(platform)
                    if shouldScheduleFeedback, !closed, validated.contains(platform) {
                        schedulePendingPlaybackFeedback()
                    }
                    notifyAccountSession(platform)
                }
                pendingWrites += 1
                let token: AccountSessionToken
                do { token = try await accounts.replaceVerified(lease, credential: value.credential) }
                catch {
                    pendingWrites -= 1
                    throw error
                }
                pendingWrites -= 1
                summary.imported.append(platform)
                notifyAccountSession(platform, committedRecord: true)
                let binding = await rotate(platform)
                guard accountEpochs[platform] == binding else { continue }
                tokens[platform] = token; inspected.insert(platform)
                requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
                if !closed, epoch == batch.epoch {
                    let published: Bool
                    do {
                        try await publishSaved(token, platform: platform, own: batch.epoch,
                            revision: revisions[platform, default: 0], binding: binding,
                            allowingTransition: true)
                        published = true
                    } catch {
                        DebugRuntime.logError(.musicAccount, sub: platform.rawValue, event: "import.publishFailed", error: error)
                        published = false
                    }
                    if published { validated.insert(platform); profiles[platform] = value.nickname }
                }
                notifyNativeHome(platform)
                await drainCacheLifecycle()
                shouldScheduleFeedback = platform == .netease && validated.contains(platform)
            } catch {
                if let identity = scopes[platform] {
                    let state = await accounts.snapshot(for: identity)
                    if state.phase == .invalidated, item.epoch == accountEpochs[platform], epoch == batch.epoch {
                        let binding = await rotate(platform)
                        guard accountEpochs[platform] == binding, epoch == batch.epoch else {
                            summary.cancelled.append(platform); continue
                        }
                        tokens.removeValue(forKey: platform)
                        requiresExplicitRestore.insert(platform)
                        notifyAccountSession(platform); notifyNativeHome(platform)
                    }
                }
                if Task.isCancelled || closed || epoch != batch.epoch || plan.isCancelled { summary.cancelled.append(platform) }
                else { summary.failed.append(platform) }
            }
        }
        return summary
    }
}
