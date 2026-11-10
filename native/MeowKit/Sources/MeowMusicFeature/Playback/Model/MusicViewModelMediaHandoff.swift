#if os(iOS) || os(macOS)
import Foundation

@MainActor
extension MusicViewModel {
    /// Relinquishes music before another media domain starts. The queue remains
    /// available for a later explicit play; pending work cannot restart it.
    public func stopForMediaHandoff() {
        cancelPlayPauseCommands()
        cancelRemoteQueuePreparation()
        cancelRecommendationOperation()
        navigationPlaybackIntent?.revoke(); navigationPlaybackIntent = nil
        cancelFMNavigation()
        retirePlaybackNavigation()
        playbackState.isChangingQueue = false
        fmShouldPlay = false
        if let primaryPlayback {
            primaryPlayback.presentation.cancelHomePlayback(primaryPlayback.intent)
            self.primaryPlayback = nil
        }

        pendingSourceSelection?.lifetime.revoke(); pendingSourceSelection = nil
        pendingSourceFailure = nil
        sourceSwitchID = UUID()
        sourceSwitchTask?.cancel(); sourceSwitchTask = nil
        sourceMediaTask?.cancel(); sourceMediaTask = nil
        sourceRecoveryID = UUID()
        sourceRecoveryTask?.cancel(); sourceRecoveryTask = nil
        sourceRecoveryWantsPlayback = nil
        resolutionTask?.cancel(); resolutionTask = nil
        candidateDiscoveryTask?.cancel(); candidateDiscoveryTask = nil
        cachedAudioLookup?.cancel(); cachedAudioLookup = nil
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        cachedAudioRecoveryTask?.cancel(); cachedAudioRecoveryTask = nil
        remoteAudioRecoveryTask?.cancel(); remoteAudioRecoveryTask = nil
        cachedAudioRecovery = nil
        remoteAudioRecoveryTried = false
        lyricTask?.cancel(); lyricTask = nil
        resolvingAttempt = nil
        playbackState.isResolving = false
        playbackState.options = []; playbackState.scriptSourceError = nil

        // Deliver the native exit under its old selection before retiring UI
        // continuations. Keep candidate/feedback ownership for accepted
        // history/progress; a later explicit selection replaces it normally.
        if let nativeAttempt { player?.stop(nativeAttempt) }
        selectionID = UUID()
        nativeAttempt = nil; pendingNativeResourceID = nil
        playbackState.phase = .idle
    }
}
#endif
