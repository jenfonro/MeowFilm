// SPDX-License-Identifier: GPL-3.0-only
import Foundation

public struct MusicAppearanceSongIdentity: Equatable, Sendable {
    public let stableKey: String
    public init(platform: String? = nil, musicID: String? = nil, songMID: String? = nil,
                webDAVConfiguration: String? = nil, webDAVPath: String? = nil,
                deduplicationKey: String? = nil, id: String? = nil, pluginEntryPath: String? = nil,
                filePath: String? = nil, title: String = "", artist: String = "", album: String = "") {
        if let platform, !platform.isEmpty,
           let nativeID = [musicID, songMID].compactMap({ $0 }).first(where: { !$0.isEmpty }) {
            stableKey = "\(platform):\(nativeID)"
        } else if let config = webDAVConfiguration, !config.isEmpty, let path = webDAVPath, !path.isEmpty {
            stableKey = "dav:\(config):\(path)"
        } else if let key = deduplicationKey, !key.isEmpty { stableKey = key }
        else if let id { stableKey = "\(pluginEntryPath.flatMap { $0.isEmpty ? nil : $0 } ?? "local"):\(id)" }
        else if let filePath, !filePath.isEmpty { stableKey = filePath }
        else { stableKey = [title, artist, album].map(Self.normalizeMetadata).joined(separator: "|") }
    }
    /// Pass originalSource when the producer still has the original source_data
    /// identity, for byte-identical reference fallbacks/imported cover-cache keys.
    /// Otherwise use the domain songId, also when a single source later gains
    /// candidates. Never derive identity from sources.first or the currently
    /// selected playback source, queue occurrence or a temporary handle.
    public init(track: MusicRemoteTrack, originalSource: MusicNativeSource? = nil) {
        self.init(platform: originalSource?.platform, musicID: originalSource?.nativeId,
                  deduplicationKey: track.songId, title: track.title, artist: track.artist, album: track.album)
    }
    public static func == (lhs: Self, rhs: Self) -> Bool { lhs.stableKey.utf16.elementsEqual(rhs.stableKey.utf16) }
    private static func normalizeMetadata(_ value: String) -> String {
        // ECMAScript whitespace, not ICU's broader \s (which includes U+0085).
        let whitespace: Set<UInt32> = [9, 10, 11, 12, 13, 32, 160, 5760, 8192, 8193, 8194, 8195, 8196,
                                     8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288, 65279]
        var result = "", gap = false
        for scalar in value.unicodeScalars {
            if whitespace.contains(scalar.value) { gap = !result.isEmpty; continue }
            if gap { result.append(" "); gap = false }
            result.unicodeScalars.append(scalar)
        }
        return result.lowercased()
    }
}
