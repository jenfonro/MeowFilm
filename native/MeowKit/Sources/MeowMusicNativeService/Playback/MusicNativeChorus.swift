// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicChorusProviding {
    public func chorus(attemptID: String, actualDuration: Double) async throws -> MusicChorusPoint? {
        try check()
        guard let current = attempt, current.id == attemptID, current.ready, !current.ended,
              let owner = accounts as? any MusicNativeChorusProviding else { return nil }
        let own = epoch, d = try read(), selection = current.selection
        guard d.queue.currentID == current.occurrence else { return nil }
        let song = try self.song(.init(kind: .queue, id: current.occurrence), d)
        let (source, row) = try target(song, selection.sourceKey)
        guard platformEnabled(source.platform), selection.siteId == source.platform,
              ["wy", "kg"].contains(source.platform) else { return nil }
        let recorded = row.resolvedDuration
        guard recorded.isFinite, recorded > 0, actualDuration.isFinite, actualDuration > 0,
              MusicMediaDuration.matches(expected: recorded, actual: actualDuration) else { return nil }
        let cachedOnly = audioCacheAttempt?.attemptID == attemptID && audioCacheAttempt?.audio == nil
        let point = try await work(label: "chorus") {
            try await owner.nativeChorus(nativeID: source.nativeId, selection: selection,
                                         recordingDuration: recorded, actualDuration: actualDuration, cachedOnly: cachedOnly)
        }
        try check(own)
        guard attempt?.id == attemptID, attempt?.ready == true, attempt?.ended == false,
              attempt?.selection == selection,
              try read().queue.currentID == current.occurrence else { throw MusicServiceError.staleResult }
        return point?.fits(recordingDuration: recorded, actualDuration: actualDuration) == true ? point : nil
    }
}
