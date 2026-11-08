import Foundation

/// Product/feature-owned selection consumer. No player, account, writer or host.
/// One invocation supersedes the previous one; successful resolution is handed
/// to the existing playback flow and its existing actual-playback fact writer.
public actor MusicCandidateDiscoveryConsumer {
    private let provider: any MusicCandidateDiscoveryServing
    private let policy: any MusicCandidateDiscoverySelecting
    private let timing: MusicCandidateDiscoveryTiming
    private var token = UUID()
    private struct Prepared: Sendable {
        let value: MusicCandidateDiscoveryResolution
        let session: any MusicCandidateDiscoverySession
    }
    private var pending: Task<Prepared, Error>?

    public init(provider: any MusicCandidateDiscoveryServing,
                policy: any MusicCandidateDiscoverySelecting = MusicCandidateDiscoveryPolicy(),
                timing: MusicCandidateDiscoveryTiming = .init()) {
        self.provider = provider; self.policy = policy; self.timing = timing
    }
    public func cancel() {
        token = UUID(); pending?.cancel(); pending = nil
    }
    public func resolve(occurrenceID: String, expectedQueueRevision: Int,
                        mode: MusicCandidateDiscoveryMode,
                        rejectedURLs: Set<URL> = []) async throws -> MusicCandidateDiscoveryResolution {
        cancel()
        let own = token, provider = provider, policy = policy, timing = timing
        let task = Task {
            let session = try await provider.beginCandidateDiscovery(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
            do {
                let value = try await MusicCandidateDiscoveryDeadline.run(seconds: 35, timing: timing) {
                    try await Self.perform(session: session, policy: policy, timing: timing,
                        occurrenceID: occurrenceID, mode: mode, rejectedURLs: rejectedURLs)
                }
                try Task.checkCancellation()
                return Prepared(value: value, session: session)
            } catch {
                await session.close(retainingAttemptID: nil)
                throw error
            }
        }
        pending = task
        var prepared: Prepared?
        do {
            let ready = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            prepared = ready
            guard token == own, !Task.isCancelled else { throw MusicServiceError.cancelled }
            await ready.session.close(retainingAttemptID: ready.value.candidate.attemptId)
            guard token == own, !Task.isCancelled else {
                await ready.session.cancel(attemptID: ready.value.candidate.attemptId)
                throw MusicServiceError.cancelled
            }
            pending = nil
            return ready.value
        } catch {
            if let prepared { await prepared.session.close(retainingAttemptID: nil) }
            if token == own { pending = nil }
            throw error
        }
    }
    private static func perform(session: any MusicCandidateDiscoverySession,
        policy: any MusicCandidateDiscoverySelecting, timing: MusicCandidateDiscoveryTiming,
        occurrenceID: String, mode: MusicCandidateDiscoveryMode,
        rejectedURLs: Set<URL>) async throws -> MusicCandidateDiscoveryResolution {
        let value: MusicCandidateDiscoveryResolution
        switch mode {
        case .manual(let target):
            // Do not fetch unrelated capabilities/search when manually pinned.
            let attempt = UUID().uuidString.lowercased()
            do {
                let candidate = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: timing) {
                    try await session.resolve(target, attemptID: attempt)
                }
                try validate(candidate, target: target, attempt: attempt, occurrence: occurrenceID,
                             rejectedURLs: rejectedURLs)
                value = .init(candidate: candidate, target: target, attempts: 1, issues: [])
            } catch { await session.cancel(attemptID: attempt); throw error }
        case .automatic(let preferred, let force):
            // Already-known sources first. Matching (platform searches) starts
            // only after every known target failed, so a playable mapping never
            // pays for discovery. Matching itself has no durable queue effect
            // until the explicit CAS below.
            let known = try await session.snapshot(discover: false, force: false)
            var issues = known.issues, attempted: [MusicCandidateDiscoveryTarget] = []
            var winner: MusicCandidateDiscoveryResolution?
            for pass in 0..<2 {
                let snapshot: MusicCandidateDiscoverySnapshot
                if pass == 0 { snapshot = known }
                else {
                    snapshot = try await session.snapshot(discover: true, force: force)
                    issues += snapshot.issues.filter { !issues.contains($0) }
                    try await session.commitDiscovery()
                }
                let plan = try policy.plan(snapshot, preferred: preferred)
                for target in plan where !attempted.contains(where: { $0.sameRequest(as: target) }) {
                    try Task.checkCancellation()
                    // An injected policy cannot invent a target or disabled quality.
                    guard snapshot.options.contains(target.option), target.option.playback.qualities.contains(target.quality) else {
                        throw MusicServiceError.targetChanged
                    }
                    attempted.append(target)
                    let attempt = UUID().uuidString.lowercased()
                    do {
                        let candidate = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: timing) {
                            try await session.resolve(target, attemptID: attempt)
                        }
                        try validate(candidate, target: target, attempt: attempt, occurrence: occurrenceID,
                                     rejectedURLs: rejectedURLs)
                        winner = .init(candidate: candidate, target: target, attempts: attempted.count, issues: issues)
                        break
                    } catch {
                        await session.cancel(attemptID: attempt)
                        let failure = MusicServiceError.sanitized(error)
                        issues.append(.init(stage: .resolution, platform: target.selection.siteId, failure: failure))
                        // A content answer for one recording (VIP-only, removed,
                        // preview) is not a session failure: try the next target.
                        guard MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                              MusicCandidateDiscoveryFailures.isContentFailure(failure) else {
                            throw MusicCandidateDiscoveryFailure(failure: failure, issues: issues, attempts: attempted.count)
                        }
                    }
                }
                if winner != nil { break }
            }
            guard let winner else {
                // Keep the platform's own explanation when only content answers were received.
                let failures = issues.filter { $0.stage == .resolution }.map(\.failure)
                let failure = !failures.isEmpty && failures.allSatisfy(MusicCandidateDiscoveryFailures.isContentFailure)
                    ? failures[0] : .sourceUnavailable
                throw MusicCandidateDiscoveryFailure(failure: failure, issues: issues, attempts: attempted.count)
            }
            value = winner
        }
        try Task.checkCancellation()
        return value
    }
    private static func validate(_ candidate: MusicPlaybackCandidate, target: MusicCandidateDiscoveryTarget,
                                 attempt: String, occurrence: String, rejectedURLs: Set<URL>) throws {
        guard candidate.attemptId == attempt, candidate.occurrenceId == occurrence,
              candidate.resourceId == "music:" + attempt,
              target.option.revisionHash == target.selection.scriptIdentity?.revisionHash else {
            throw MusicServiceError.invalidResponse
        }
        try candidate.validateResolvedSelection(for: target.selection)
        _ = try candidate.resource()
        // A syntactically valid URL that already failed is a resolution failure,
        // not a successful candidate. Automatic mode keeps its existing bounded
        // policy; manual mode cancels this exact request without switching source.
        guard !rejectedURLs.contains(candidate.url) else { throw MusicServiceError.invalidMediaCandidate }
    }
    deinit { pending?.cancel() }
}
