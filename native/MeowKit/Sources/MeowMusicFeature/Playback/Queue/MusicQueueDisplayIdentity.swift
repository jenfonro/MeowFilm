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
        guard let current, !songID.isEmpty, songID.utf8.elementsEqual(current.songID.utf8) else { return false }
        if !sourceType.isEmpty || !sourceID.isEmpty {
            return sourceType.utf8.elementsEqual(current.sourceType.utf8) &&
                sourceID.utf8.elementsEqual(current.sourceID.utf8)
        }
        return !hasContext && !current.hasContext && !occurrenceID.isEmpty &&
            occurrenceID.utf8.elementsEqual(current.occurrenceID.utf8)
    }
}
