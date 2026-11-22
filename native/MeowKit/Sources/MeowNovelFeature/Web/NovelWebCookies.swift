#if os(iOS)
import Foundation
import MeowRuntime

/// Cookie scope comes from the source owner, not the first page's Cookie header.
/// Both snapshots are taken from WebKit, so WebKit's normalization is not a change.
struct NovelWebCookie: Equatable {
    struct Key: Hashable {
        var name: String
        var domain: String
        var hostOnly: Bool
        var path: String
    }
    var key: Key
    var value: String
    var secure: Bool
    var httpOnly: Bool
    var sameSite: String?
    var expires: Date?

    init(_ cookie: HTTPCookie) {
        key = .init(name: cookie.name, domain: cookie.domain.trimmingCharacters(in: CharacterSet(charactersIn: ".")),
            hostOnly: !cookie.domain.hasPrefix("."), path: cookie.path)
        value = cookie.value; secure = cookie.isSecure; httpOnly = cookie.isHTTPOnly
        sameSite = Self.sameSite(cookie.properties?[.sameSitePolicy] as? String)
        expires = cookie.expiresDate
    }
    init(seed: RuntimeValue) throws {
        guard let name = seed["name"].string, let value = seed["value"].string,
              let domain = seed["domain"].string, let hostOnly = seed["hostOnly"].bool,
              let path = seed["path"].string, let secure = seed["secure"].bool,
              let httpOnly = seed["httpOnly"].bool else { throw NovelError.invalidResponse }
        key = .init(name: name, domain: domain, hostOnly: hostOnly, path: path)
        self.value = value; self.secure = secure; self.httpOnly = httpOnly
        sameSite = Self.sameSite(seed["sameSite"].string)
        expires = seed["expires"].number.map { Date(timeIntervalSince1970: $0 / 1000) }
    }
    private static func sameSite(_ value: String?) -> String? {
        switch value?.lowercased() {
        case "lax": return "Lax"
        case "strict": return "Strict"
        case "none": return "None"
        default: return nil
        }
    }
    var origin: URL {
        var parts = URLComponents()
        parts.scheme = secure ? "https" : "http"; parts.host = key.domain; parts.path = key.path
        return parts.url!
    }
    func setCookie(deleting: Bool = false) -> String {
        var text = "\(key.name)=\(deleting ? "" : value); Path=\(key.path)"
        if !key.hostOnly, !key.name.hasPrefix("__Host-") { text += "; Domain=\(key.domain)" }
        if secure { text += "; Secure" }
        if httpOnly { text += "; HttpOnly" }
        if let sameSite { text += "; SameSite=\(sameSite)" }
        if deleting { text += "; Max-Age=0" }
        else if let expires {
            let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
            text += "; Expires=" + formatter.string(from: expires)
        }
        return text
    }
    func nativeCookie() throws -> HTTPCookie {
        guard let cookie = HTTPCookie.cookies(withResponseHeaderFields: ["Set-Cookie": setCookie()], for: origin).first else {
            throw NovelError.invalidResponse
        }
        return cookie
    }
    static func snapshot(_ cookies: [HTTPCookie], preserving known: [NovelWebCookie]) -> [NovelWebCookie] {
        // Foundation projects both SameSite=None and unspecified as nil. Preserve
        // known None only for an identical scope; actual Lax/Strict take priority.
        let knownNone = Set(known.filter { $0.sameSite == "None" }.map(\.key))
        return cookies.map {
            var cookie = NovelWebCookie($0)
            if cookie.sameSite == nil, knownNone.contains(cookie.key) { cookie.sameSite = "None" }
            return cookie
        }
    }
    static func changes(baseline: [NovelWebCookie], final: [NovelWebCookie]) -> [RuntimeValue] {
        var changes: [RuntimeValue] = []
        func append(_ cookie: NovelWebCookie, deleting: Bool = false) {
            changes.append(.object(["url": .string(cookie.origin.absoluteString), "setCookie": .string(cookie.setCookie(deleting: deleting))]))
        }
        let before = Dictionary(uniqueKeysWithValues: baseline.map { ($0.key, $0) })
        let after = Dictionary(uniqueKeysWithValues: final.map { ($0.key, $0) })
        // The owner's jar keys by name/domain/path. A host-only ↔ Domain
        // replacement must remove the old scope before installing the new one.
        for cookie in baseline where after[cookie.key] == nil { append(cookie, deleting: true) }
        for cookie in final where before[cookie.key] != cookie { append(cookie) }
        return changes
    }
}
#endif
