import Foundation

enum FilmPosterAddress {
    private static let proxyHost = "img.doubanio.cmliussss.net"

    static func normalize(_ address: String) -> URL? {
        var address = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.isEmpty, address.utf16.count <= 8_192,
              address.rangeOfCharacter(from: .controlCharacters) == nil else { return nil }
        if address.hasPrefix("//") { address = "https:" + address }
        guard var parts = URLComponents(string: address),
              let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              parts.user == nil, parts.password == nil, parts.fragment == nil,
              let host = parts.host?.lowercased(), !host.isEmpty,
              parts.port == nil || parts.port == (scheme == "http" ? 80 : 443) else { return nil }
        parts.scheme = "https"
        parts.port = nil
        if isDoubanHost(host) { parts.host = proxyHost }
        return parts.url
    }

    private static func isDoubanHost(_ host: String) -> Bool {
        if ["img.doubanio.com", "img.douban.com", proxyHost, "img.doubanio.cmliussss.com"].contains(host) {
            return true
        }
        for suffix in [".doubanio.com", ".douban.com"] {
            guard host.hasPrefix("img"), host.hasSuffix(suffix) else { continue }
            let index = host.dropFirst(3).dropLast(suffix.count)
            if !index.isEmpty, index.utf8.allSatisfy({ (48...57).contains($0) }) { return true }
        }
        return false
    }
}
