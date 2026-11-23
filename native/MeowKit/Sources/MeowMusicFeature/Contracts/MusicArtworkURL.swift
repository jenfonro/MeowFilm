import Foundation

/// Shared by display adapters, persistence and the artwork image loader.
public enum MusicArtworkURL {
    public static func normalized(_ text: String) -> URL? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              let parts = URLComponents(string: trimmed.replacingOccurrences(of: "{size}", with: "480")),
              let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = parts.host, !host.isEmpty else { return nil }
        return parts.url
    }
}
