import Foundation

/// Shared by display adapters, persistence and the credential-free image loader.
public enum MusicArtworkURL {
    public static func normalized(_ text: String) -> URL? {
        guard !text.isEmpty,
              !text.utf8.contains(where: { $0 <= 32 || $0 == 127 || $0 == 92 }),
              let parts = URLComponents(string: text.replacingOccurrences(of: "{size}", with: "480")),
              let scheme = parts.scheme?.lowercased(), ["http", "https"].contains(scheme),
              parts.fragment == nil,
              parts.port == nil || (1...65535).contains(parts.port!),
              let host = parts.host, !host.isEmpty else { return nil }
        return parts.url
    }
}
