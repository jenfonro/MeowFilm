#if os(iOS) || os(macOS)
import Foundation
import MeowPlayback

@MainActor
extension MusicViewModel {
    /// Capture at the final successful native admission, not candidate creation.
    /// Native exit delivery precedes this completion; a reentrant new music
    /// selection/control request must not be cancelled by the older handoff.
    public func mediaHandoffCompletion() -> @MainActor () -> Void {
        let epoch = lifecycle, selection = selectionID, control = playbackControlRevision
        return { [weak self] in
            guard let self, self.lifecycle == epoch, self.selectionID == selection,
                  self.playbackControlRevision == control else { return }
            self.stopForMediaHandoff()
        }
    }

    /// Relinquishes only addressed music, never a new media owner. The queue
    /// remains available for a later explicit play.
    public func stopForMediaHandoff() {
        cancelPlayPauseCommands()
        cancelRemoteQueuePreparation()
        cancelRecommendationOperation()
        navigationPlaybackIntent?.revoke(); navigationPlaybackIntent = nil
        cancelFMNavigation()
        // A native exit already retired navigation before admitting its final
        // fact. Do not retire it twice and discard that donor's pending pause.
        if nativeAttempt != nil || terminalAttempt == nil { retirePlaybackNavigation() }
        playbackState.isChangingQueue = false
        recommendations.fmShouldPlay = false
        if let primaryPlayback {
            primaryPlayback.presentation.cancelHomePlayback(primaryPlayback.intent)
            self.primaryPlayback = nil
        }

        sourcePlayback.cancel([.resetSelection, .clearRecoveryState, .stopCachedObservation])
        resolvingAttempt = nil
        playbackState.isResolving = false
        playbackState.options = []; playbackState.scriptSourceError = nil

        // Deliver the native exit under its old selection before retiring UI
        // continuations. Keep candidate/feedback ownership for accepted
        // history/progress; a later explicit selection replaces it normally.
        if let nativeAttempt { playback.stop(attempt: nativeAttempt) }
        selectionID = UUID()
        nativeAttempt = nil; pendingNativeResourceID = nil
        playbackState.phase = .idle
    }
}
#endif
