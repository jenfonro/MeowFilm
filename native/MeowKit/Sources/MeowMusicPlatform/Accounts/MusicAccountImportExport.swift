import Foundation
import MeowAccounts
import MeowDebug

extension NativeMusicAccountService {
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
