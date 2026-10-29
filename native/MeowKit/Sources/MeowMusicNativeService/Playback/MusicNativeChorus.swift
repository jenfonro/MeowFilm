// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicChorusProviding {
    public func chorus(attemptID: String, actualDuration: Double) async throws -> MusicChorusPoint? {
        try check()
        guard let current = attempt, current.id == attemptID, current.ready, !current.ended,
              let owner = accounts as? any MusicNativeChorusProviding else { return nil }
        let own = epoch, d = try read(), selection = current.selection
        guard d.queue["currentId"].string == current.occurrence else { return nil }
        let song = try self.song(.init(kind: .queue, id: current.occurrence), d)
        let (source, info) = try target(song, selection.sourceKey)
        guard platformEnabled(source.platform), selection.siteId == source.platform,
              ["wy", "kg"].contains(source.platform),
              let row = song["sources"].array.first(where: { $0["sourceKey"].string == selection.sourceKey }),
              MusicModel.compatible(song, row) else { return nil }
        // Do not borrow WY's mapping for an actual QQ/KG delivery.
        let recorded = row["duration"].number > 0 ? row["duration"].number : info["duration"].number
        guard recorded.isFinite, recorded > 0, actualDuration.isFinite, actualDuration > 0,
              abs(recorded - actualDuration) <= max(3, min(recorded, actualDuration) * 0.02) else { return nil }
        let point = try await work(label: "chorus") {
            try await owner.nativeChorus(nativeID: source.nativeId, selection: selection,
                                         recordingDuration: recorded, actualDuration: actualDuration)
        }
        try check(own)
        guard attempt?.id == attemptID, attempt?.ready == true, attempt?.ended == false,
              attempt?.selection == selection,
              try read().queue["currentId"].string == current.occurrence else { throw MusicServiceError.staleResult }
        return point?.fits(recordingDuration: recorded, actualDuration: actualDuration) == true ? point : nil
    }
}
