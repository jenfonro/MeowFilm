// SPDX-License-Identifier: GPL-3.0-only
import Foundation

public struct MusicAppearanceSongIdentity: Equatable, Sendable {
    public let stableKey: String
    public init(songID: String) { stableKey = songID }
    public init(track: MusicRemoteTrack) { stableKey = track.songId }
    public static func == (lhs: Self, rhs: Self) -> Bool { lhs.stableKey.utf16.elementsEqual(rhs.stableKey.utf16) }
}
