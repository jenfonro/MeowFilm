#if os(iOS) || os(macOS)
import Foundation

@MainActor
extension MusicViewModel {
    func reconcilePlaybackFeedbackSettings(accessGeneration: UUID?) async throws {
        let own = lifecycle
        guard isAvailable, !isClosed, accessGeneration == own else { throw MusicServiceError.staleAccess }
        let service = try requireService()
        // A confirmed/uncertain settings write outlives its presentation.
        // Read the native facts, not an inferred revision or the whole library.
        let next = try await Task { try await service.state() }.value
        guard isAvailable, !isClosed, lifecycle == own else { throw MusicServiceError.staleAccess }
        guard next.settingsRevision >= state.settingsRevision else { return }
        let current = state
        let navigation = next.revision == current.revision ? next : current
        // Queue facts and the presentation clock have independent owners.
        let reconciled = MusicPlaybackState(
            revision: current.revision, settingsRevision: next.settingsRevision, mode: next.mode,
            qualityEnabled: next.qualityEnabled, qualityOrder: next.qualityOrder,
            spatialEnabled: next.spatialEnabled, spatialOrder: next.spatialOrder,
            lyricsEnabled: next.lyricsEnabled, showTranslation: next.showTranslation,
            officialSources: next.officialSources,
            currentId: current.currentId, total: current.total, position: current.position,
            canNext: navigation.canNext, canPrevious: navigation.canPrevious,
            nextPreviewID: navigation.nextPreviewID, previousPreviewID: navigation.previousPreviewID,
            fmNextPreviewID: navigation.fmNextPreviewID, fmPreviousPreviewID: navigation.fmPreviousPreviewID,
            sourceCollectionID: current.sourceCollectionID, requestQuality: next.requestQuality
        )
        if reconciled != current { playbackState.state = reconciled }
    }
}
#endif
