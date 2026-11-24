import Foundation
import MeowRuntime

enum FilmPosterAddress {
    static func normalize(_ address: String) -> URL? {
        var address = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.isEmpty,
              address.rangeOfCharacter(from: .controlCharacters) == nil else { return nil }
        if let reference = RuntimeHTTPClient.metadataImageURL(address) { return reference }
        if address.hasPrefix("//") { address = "https:" + address }
        guard let parts = URLComponents(string: address),
              let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = parts.host, !host.isEmpty else { return nil }
        return parts.url
    }
}
