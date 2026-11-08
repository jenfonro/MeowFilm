import Foundation

/// A read-only candidate batch for the current ordinary queue. Its token is
/// bound to the queue and account generation, not to a durable playlist.
public struct MusicQueueRecommendationSnapshot: Sendable, Identifiable {
    public let id: UUID
    public let status: MusicSimilarModeSnapshot.Status
    public let currentEntryID: String
    public let continueEnabled: Bool
    public let candidates: [MusicSimilarModeCandidate]
    public let exhausted: Bool
    public let error: MusicServiceError?
    public let canRetry: Bool
    public init(id: UUID, status: MusicSimilarModeSnapshot.Status, currentEntryID: String,
                continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], exhausted: Bool,
                error: MusicServiceError?, canRetry: Bool) {
        self.id = id; self.status = status; self.currentEntryID = currentEntryID
        self.continueEnabled = continueEnabled; self.candidates = candidates
        self.exhausted = exhausted; self.error = error; self.canRetry = canRetry
    }
}

/// Ordinary recommendations append to the existing queue; they never enter a
/// heartbeat/SIMILAR session or replace its return checkpoint. Changes and
/// observed playback facts use the existing similar-mode observation owner.
public protocol MusicQueueRecommendationServing: Sendable {
    func queueRecommendationSnapshot() async throws -> MusicQueueRecommendationSnapshot?
    func previewQueueRecommendations() async throws -> MusicQueueRecommendationSnapshot?
    func playQueueRecommendation(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult
    func setQueueRecommendationContinue(_ enabled: Bool, snapshotID: UUID) async throws
    func retryQueueRecommendations(snapshotID: UUID) async throws -> MusicSimilarModeResult
    func cancelQueueRecommendationPreview() async
}
