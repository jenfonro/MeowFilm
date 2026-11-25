import Foundation

/// A display comparison only: no reference, lookup handle, account lease or
/// restore permission. Queue/history storage keeps its existing schema.
public struct MusicQueueDisplayIdentity: Codable, Equatable, Sendable {
    public let songID: String
    public let occurrenceID: String
    public let sourceType: String
    public let sourceID: String
    public let hasContext: Bool

    public init(songID: String, occurrenceID: String, sourceType: String,
                sourceID: String, hasContext: Bool) {
        self.songID = songID; self.occurrenceID = occurrenceID
        self.sourceType = sourceType; self.sourceID = sourceID; self.hasContext = hasContext
    }

    public func matches(_ current: MusicQueueDisplayIdentity?) -> Bool {
        guard let current, !songID.isEmpty, songID == current.songID else { return false }
        if !sourceType.isEmpty || !sourceID.isEmpty {
            return sourceType == current.sourceType && sourceID == current.sourceID
        }
        return !hasContext && !current.hasContext && !occurrenceID.isEmpty && occurrenceID == current.occurrenceID
    }
}
