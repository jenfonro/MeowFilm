import Foundation

/// Shared by display adapters, persistence and the credential-free image loader.
public enum MusicArtworkURL {
    public static func normalized(_ text: String) -> URL? {
        guard !text.isEmpty, text.utf8.count <= 8192,
              !text.utf8.contains(where: { $0 <= 32 || $0 == 127 || $0 == 92 }),
              var parts = URLComponents(string: text.replacingOccurrences(of: "{size}", with: "480")),
              let scheme = parts.scheme?.lowercased(), ["http", "https"].contains(scheme),
              parts.user == nil, parts.password == nil, parts.fragment == nil,
              parts.port == nil || parts.port == 443 || (scheme == "http" && parts.port == 80),
              let host = parts.host, !host.isEmpty else { return nil }
        parts.scheme = "https"
        if parts.port == 80 { parts.port = nil }
        return parts.url
    }
}
