import Foundation
import MeowMusicFeature

/// Dedicated actor owning the active source-inspection session and bounded
/// resolved-source LRU cache off NativeMusicService's main transaction mailbox.
actor MusicNativeSourceSessionStore {
    private var currentSession: (occurrence: String, session: MusicNativeSourceInspection)?
    private var revision = UUID()
    private var results: [String: [MusicNativeResolvedSource]] = [:]
    private var order: [String] = []
    private let maxCachedSongs = 128

    func currentRevision() -> UUID { revision }

    func current() -> (occurrence: String, session: MusicNativeSourceInspection)? { currentSession }

    func installIfEmpty(occurrence: String, session: MusicNativeSourceInspection,
                        expectedRevision: UUID) -> (installed: Bool, existing: (occurrence: String, session: MusicNativeSourceInspection)?) {
        guard revision == expectedRevision else {
            return (false, currentSession)
        }
        if let existing = currentSession {
            return (false, existing)
        }
        currentSession = (occurrence, session)
        return (true, nil)
    }

    func clearCurrent(ifMatching session: MusicNativeSourceInspection) {
        if currentSession?.session === session {
            currentSession = nil
        }
    }

    func invalidateConfiguration() -> MusicNativeSourceInspection? {
        revision = UUID()
        let previous = currentSession?.session
        currentSession = nil
        return previous
    }

    func resetAll() -> MusicNativeSourceInspection? {
        revision = UUID()
        let previous = currentSession?.session
        currentSession = nil
        results.removeAll()
        order.removeAll()
        return previous
    }

    func cachedResults(songID: String) -> [MusicNativeResolvedSource] {
        results[songID] ?? []
    }

    func remember(_ result: MusicNativeResolvedSource, songID: String) {
        var values = results[songID] ?? []
        values.removeAll { $0.id == result.id }
        values.append(result)
        results[songID] = values
        order.removeAll { $0 == songID }
        order.append(songID)
        while order.count > maxCachedSongs {
            results[order.removeFirst()] = nil
        }
    }

    func forget(selection: MusicPlaybackSelection, songID: String) {
        results[songID]?.removeAll { $0.choice.selection == selection }
    }

    func forget(sourceKey: String, songID: String) {
        results[songID]?.removeAll { $0.option.sourceKey == sourceKey }
    }
}
