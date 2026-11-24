#if os(iOS) || os(macOS)
import Foundation

/// Owns similar-mode, queue-recommendation, remote-queue preparation, and FM
/// session coordination state for MusicViewModel, including retaining previous
/// FM queue source leases across FM channel switches so earlier FM queue
/// entries preserve their live playback feedback origins.
@MainActor
final class MusicRecommendationCoordinator {
    var lastRecommendationFailure: UUID?
    nonisolated(unsafe) var similarObservation: Task<Void, Never>?
    var similarObservationID = UUID()
    var recommendationReadID = UUID()
    var recommendationPreviewReadID: UUID?
    nonisolated(unsafe) var queuePreviewCancellation: Task<Void, Never>?
    var recommendationQueueRevision: Int?
    var recommendationAccount: UUID?
    var recommendationCommandID = UUID()
    var recommendationReturnID: UUID?
    nonisolated(unsafe) var recommendationTask: Task<MusicSimilarModeResult, Error>?
    nonisolated(unsafe) var recommendationIntent: MusicQueueAuthorization?
    nonisolated(unsafe) var recommendationPlaybackIntent: MusicQueueAuthorization?
    nonisolated(unsafe) var remoteQueueTask: Task<MusicPlaybackState, Error>?
    nonisolated(unsafe) var remoteQueueIntent: MusicQueueAuthorization?
    var remoteQueueRequest = UUID()
    var remoteQueueSourceID: UUID?
    var remoteQueueIsFM = false
    private static let maxRetainedFMSources = 16
    nonisolated(unsafe) var fmSource: MusicRemoteQueueSource?
    nonisolated(unsafe) var retainedFMSources: [MusicRemoteQueueSource] = []
    nonisolated(unsafe) private var closingFMSources: [UUID: (token: UUID, task: Task<Void, Never>)] = [:]
    nonisolated(unsafe) var fmRevocationTask: Task<Void, Never>?
    nonisolated(unsafe) var fmAdvanceIntent: MusicQueueAuthorization?
    var fmShouldPlay = true

    func ownsFMSource(_ id: UUID) -> Bool {
        fmSource?.id == id || retainedFMSources.contains(where: { $0.id == id })
    }

    /// Transitions to a new FM channel without revoking earlier FM channel
    /// sources whose tracks remain in the queue with active feedback origins.
    func switchFMSource(to source: MusicRemoteQueueSource) {
        fmRevocationTask?.cancel(); fmRevocationTask = nil
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        if let previous = fmSource, previous.id != source.id {
            if !retainedFMSources.contains(where: { $0.id == previous.id }) {
                retainedFMSources.append(previous)
            }
        }
        retainedFMSources.removeAll { $0.id == source.id }
        fmSource = source
        trimRetainedFMSourcesToLimit()
    }

    /// Retires any retained FM sources no longer referenced by queue entries
    /// and enforces the upper bound.
    func pruneRetainedFMSources(referencedSessionIDs: Set<UUID>) {
        guard !retainedFMSources.isEmpty else { return }
        var kept: [MusicRemoteQueueSource] = []
        var dropped: [MusicRemoteQueueSource] = []
        for source in retainedFMSources {
            if referencedSessionIDs.contains(source.id) {
                kept.append(source)
            } else {
                dropped.append(source)
            }
        }
        retainedFMSources = kept
        for source in dropped {
            closeFMSourceAsync(source)
        }
        trimRetainedFMSourcesToLimit()
    }

    private func trimRetainedFMSourcesToLimit() {
        while retainedFMSources.count > Self.maxRetainedFMSources {
            closeFMSourceAsync(retainedFMSources.removeFirst())
        }
    }

    private func closeFMSourceAsync(_ source: MusicRemoteQueueSource) {
        source.revoke()
        let id = source.id
        let token = UUID()
        let previous = closingFMSources[id]?.task
        closingFMSources[id] = (token, Task { [weak self] in
            await previous?.value
            await source.close()
            if self?.closingFMSources[id]?.token == token {
                self?.closingFMSources.removeValue(forKey: id)
            }
        })
    }

    private func detachAllFMSources() -> [MusicRemoteQueueSource] {
        fmRevocationTask?.cancel(); fmRevocationTask = nil
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        var toClose = retainedFMSources
        retainedFMSources.removeAll()
        if let current = fmSource {
            fmSource = nil
            toClose.append(current)
        }
        return toClose
    }

    func revokeAllFMSourcesAsync() {
        for source in detachAllFMSources() {
            closeFMSourceAsync(source)
        }
    }

    func revokeAndCloseAllFMSources() async {
        let toClose = detachAllFMSources()
        for source in toClose { source.revoke() }
        for source in toClose { await source.close() }
        while !closingFMSources.isEmpty {
            let pending = closingFMSources.values.map(\.task)
            for task in pending { await task.value }
        }
    }

    nonisolated func cancelAllInDeinit() {
        remoteQueueIntent?.revoke()
        remoteQueueTask?.cancel()
        fmAdvanceIntent?.revoke()
        fmRevocationTask?.cancel()
        fmSource?.revoke()
        for source in retainedFMSources { source.revoke() }
        for entry in closingFMSources.values { entry.task.cancel() }
        similarObservation?.cancel()
        recommendationTask?.cancel()
        queuePreviewCancellation?.cancel()
        recommendationIntent?.revoke()
        recommendationPlaybackIntent?.revoke()
    }
}
#endif
