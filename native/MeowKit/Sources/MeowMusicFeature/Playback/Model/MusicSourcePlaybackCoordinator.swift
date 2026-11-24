#if os(iOS) || os(macOS)
import Foundation

/// Owns source resolution, handoff, recovery, and audio-cache coordination
/// state for MusicViewModel so MusicViewModel stays focused on public API
/// and cross-coordinator orchestration.
@MainActor
final class MusicSourcePlaybackCoordinator {
    struct CancelOptions: OptionSet {
        let rawValue: Int
        static let resetSelection = CancelOptions(rawValue: 1 << 0)
        static let clearRecoveryState = CancelOptions(rawValue: 1 << 1)
        static let clearIntent = CancelOptions(rawValue: 1 << 2)
        static let stopCachedObservation = CancelOptions(rawValue: 1 << 3)
    }

    var sourceIntent = MusicSourceIntent.automatic
    var sourceCommittedSettingsRevision: Int?
    nonisolated(unsafe) var pendingSourceSelection: (id: UUID, intent: MusicSourceIntent, lifetime: MusicQueueAuthorization)?
    var pendingSourceFailure: (id: UUID, fact: MusicPlaybackFact, invalidDuration: Bool)?
    // Synchronous native commit can publish a failure before returning. Defer
    // its recovery until the successful commit publishes the new intent.
    var sourceCommit: (attemptID: String, failure: MusicPlaybackFact?, invalidDuration: Bool)?
    var sourceRenewed = Set<String>()
    var sourceRecoveryID = UUID()
    nonisolated(unsafe) var sourceRecoveryTask: Task<Void, Never>?
    var sourceRecoveryWantsPlayback: Bool?
    nonisolated(unsafe) var sourceSwitchTask: Task<MusicPreparedSource, Error>?
    var sourceSwitchID = UUID()
    nonisolated(unsafe) var sourceMediaTask: Task<MusicPreparedPlayback, Error>?
    nonisolated(unsafe) var cachedAudioObservation: Task<Void, Never>?
    nonisolated(unsafe) var remoteAudioRecoveryTask: Task<Void, Never>?
    // Keep cancelled continuations until their exact attempt cleanup finishes.
    // Clearing the active pointer is not evidence that a late result drained.
    nonisolated(unsafe) var recoveryDrains: [UUID: Task<Void, Never>] = [:]
    var remoteAudioRecoveryTried = false

    var hasActiveWork: Bool {
        pendingSourceSelection != nil || sourceRecoveryTask != nil || remoteAudioRecoveryTask != nil
    }

    func cancel(_ options: CancelOptions = []) {
        sourceSwitchTask?.cancel()
        sourceMediaTask?.cancel()
        sourceRecoveryTask?.cancel()
        remoteAudioRecoveryTask?.cancel()
        if options.contains(.stopCachedObservation) {
            cachedAudioObservation?.cancel()
            cachedAudioObservation = nil
        }
        if options.contains(.resetSelection) {
            sourceSwitchID = UUID()
            sourceSwitchTask = nil
            sourceMediaTask = nil
            sourceRecoveryID = UUID()
            sourceRecoveryTask = nil
            sourceRecoveryWantsPlayback = nil
            pendingSourceSelection?.lifetime.revoke()
            pendingSourceSelection = nil
            pendingSourceFailure = nil
            remoteAudioRecoveryTask = nil
        }
        if options.contains(.clearIntent) {
            sourceIntent = .automatic
            sourceRenewed = []
        }
        if options.contains(.clearRecoveryState) {
            remoteAudioRecoveryTried = false
        }
    }

    nonisolated func cancelAllInDeinit() {
        sourceSwitchTask?.cancel()
        sourceMediaTask?.cancel()
        sourceRecoveryTask?.cancel()
        pendingSourceSelection?.lifetime.revoke()
        cachedAudioObservation?.cancel()
        remoteAudioRecoveryTask?.cancel()
        for task in recoveryDrains.values { task.cancel() }
    }
}
#endif
