#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    func stopSimilarObservation() {
        similarObservationID = UUID()
        similarObservation?.cancel(); similarObservation = nil
        playbackState.similarMode = nil; playbackState.canEnterSimilarMode = false; playbackState.isChangingSimilarMode = false
    }
    func observeSimilarMode() {
        guard similarObservation == nil, isAvailable, !isClosed,
              let provider = service as? any MusicSimilarModeServing else { return }
        let own = lifecycle, id = UUID(); similarObservationID = id
        similarObservation = Task { [weak self] in
            let events = await provider.similarModeChanges()
            for await _ in events {
                guard let self, !Task.isCancelled, self.lifecycle == own,
                      self.similarObservationID == id else { return }
                await self.loadSimilarMode()
            }
        }
    }
    public func loadSimilarMode(preview: Bool = false) async {
        guard isAvailable, !isClosed, let provider = service as? any MusicSimilarModeServing,
              let service else { return }
        observeSimilarMode()
        let own = lifecycle
        do {
            let value = try await (preview ? provider.previewSimilarMode() : provider.similarModeSnapshot())
            let canEnter = try await provider.canEnterSimilarMode()
            try check(own)
            playbackState.similarMode = value; playbackState.canEnterSimilarMode = canEnter
            // A recommendation batch may append without a UI queue action.
            // Refresh data only; notifications can never restart the player.
            if !isChangingQueue, !isNavigating, !isChangingSimilarMode {
                let next = try await service.state(), snapshot = try await service.queue()
                try check(own)
                if !isChangingQueue, !isNavigating, !isChangingSimilarMode,
                   next.revision == snapshot.revision, next.currentId == snapshot.currentId,
                   next.revision >= state.revision, next.settingsRevision >= state.settingsRevision {
                    if next.sourceCollectionID != state.sourceCollectionID || snapshot.entries.isEmpty { notices.queueNotice = nil }
                    playbackState.state = next; playbackState.queue = snapshot.entries
                    if candidate == nil { position = next.position }
                }
            }
        } catch {
            guard lifecycle == own, isAvailable, !isClosed else { return }
            let failure = MusicServiceError.sanitized(error)
            if failure.isAuthenticationFailure || [.accessUnavailable, .staleAccess, .unsupported].contains(failure) {
                playbackState.similarMode = nil; playbackState.canEnterSimilarMode = false
            }
        }
    }
    func performSimilarMode(
        _ operation: (any MusicSimilarModeServing) async throws -> MusicSimilarModeResult
    ) async throws {
        let own = try enter()
        guard !isChangingSimilarMode, !isChangingQueue, !isNavigating,
              let provider = service as? any MusicSimilarModeServing else { throw MusicServiceError.busy }
        playbackState.isChangingSimilarMode = true
        defer { if lifecycle == own { playbackState.isChangingSimilarMode = false } }
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        await provider.pauseSimilarNavigation()
        await flushPlaybackFacts(); try check(own)
        let result = try await operation(provider)
        try check(own)
        stopFM()
        switch result.action {
        case .playCurrent, .restoreOnly: invalidateSelection(stopMedia: true)
        case .keepCurrent, .togglePlayback: break
        }
        playbackState.state = result.state; notices.queueNotice = nil
        try await refreshQueue(try requireService(), epoch: own)
        playbackState.similarMode = try await provider.similarModeSnapshot()
        playbackState.canEnterSimilarMode = try await provider.canEnterSimilarMode()
        try check(own)
        switch result.action {
        case .playCurrent: await prepareCurrent()
        case .togglePlayback: await togglePlayback()
        case .keepCurrent, .restoreOnly: break
        }
    }
    public func enterSimilarMode() async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.enterSimilarMode() } }
        catch { report(error, epoch: own) }
    }
    public func exitSimilarMode() async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.exitSimilarMode() } }
        catch { report(error, epoch: own) }
    }
    public func playSimilarCandidate(snapshotID: UUID, candidateID: String) async {
        let own = lifecycle
        do {
            try await performSimilarMode {
                try await $0.playSimilarCandidate(snapshotID: snapshotID, candidateID: candidateID)
            }
        } catch { report(error, epoch: own, operation: "播放相似歌曲") }
    }
    public func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) async {
        let own = lifecycle
        guard isAvailable, !isClosed, let provider = service as? any MusicSimilarModeServing else { return }
        do {
            try await provider.setSimilarContinue(enabled, snapshotID: snapshotID)
            try check(own); await loadSimilarMode()
        } catch { report(error, epoch: own, operation: "保存推荐续播设置", kind: .settings) }
    }
    public func retrySimilarMode(snapshotID: UUID) async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.retrySimilarMode(snapshotID: snapshotID) } }
        catch { report(error, epoch: own) }
    }
}
#endif
