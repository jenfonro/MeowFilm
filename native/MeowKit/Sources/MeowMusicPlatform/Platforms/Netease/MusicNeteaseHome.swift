// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 src/netease.ts discoveryRequest; the module-backed artist
// request maps to /api/sub/artist/new/works/song-mv/list/v2 (eapi).
import Foundation
import MeowJSON
import MeowAccounts
import MeowMusicFeature

/// Private credential-bearing side of the public home port. Only the existing
/// account owner constructs its authorization closure; no second account owner.
protocol MusicNeteaseHomeProvider: Sendable {
    func homePayload(_ request: MusicNativeHomeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data
}

enum MusicNeteaseHomeRoute {
    static func permits(_ request: MusicAccountHTTPRequest) -> Bool {
        guard request.url.scheme == "https",
              request.url.user == nil, request.url.password == nil,
              request.url.port == nil || request.url.port == 443,
              request.url.query == nil, request.url.fragment == nil, request.method == "POST",
              !request.headers.keys.contains(where: { $0.lowercased() == "x-router" }) else { return false }
        switch request.url.host {
        case "interfacepc.music.163.com":
            return ["/eapi/link/page/rcmd/resource/show", "/eapi/sub/artist/new/works/song-mv/list/v2",
                    "/eapi/tag/tab/playlists", "/eapi/vipnewcenter/app/viptab/recommend/song/list"].contains(request.url.path)
        case "music.163.com": return request.url.path == "/weapi/v3/song/detail"
        default: return false
        }
    }
}

enum MusicNeteaseHomeRequest {
    static func validate(_ request: MusicNativeHomeRequest) throws {
        switch request {
        case .artistActivity(let before):
            guard let before else { return }
            guard !before.isEmpty, before.utf8.count <= 18,
                  before.utf8.allSatisfy({ (48...57).contains($0) }) else {
                throw MusicServiceError.invalidRequest
            }
        case .songMetadata(let ids), .playlistMetadata(let ids):
            guard !ids.isEmpty, ids.count <= 100,
                  ids.allSatisfy({ !$0.isEmpty && $0.utf8.count <= 18 &&
                      $0.utf8.first != 48 && $0.utf8.allSatisfy({ (48...57).contains($0) }) }) else {
                throw MusicServiceError.invalidRequest
            }
        default: break
        }
    }

    static func specification(_ request: MusicNativeHomeRequest, now: Date) throws -> (path: String, data: [String: Any], crypto: String) {
        try validate(request)
        switch request {
        case .home(let continuation), .featured(let continuation):
            let featured: Bool
            if case .featured = request { featured = true } else { featured = false }
            let cursor = continuation?.cursor ?? "0"
            // The public initializer preserves all decimal digits and rejects
            // values that the reference's Number(cursor) cannot represent.
            guard let number = UInt64(cursor), number <= 9_007_199_254_740_991 else {
                throw MusicServiceError.invalidRequest
            }
            var data: [String: Any] = [
                "pageCode": featured ? "HOME_DISCOVERY_PAGE" : "HOME_RECOMMEND_PAGE",
                "cursor": number, "isFirstScreen": cursor == "0",
                "pageStyleType": "noCutBlock", "refresh": false
            ]
            if let order = continuation?.blockCodeOrderList, !order.isEmpty { data["blockCodeOrderList"] = order }
            if let codes = continuation?.loadedPositionCodes, !codes.isEmpty {
                data["loadedPositionCodes"] = String(decoding: try JSONEncoder().encode(codes), as: UTF8.self)
            }
            return ("/api/link/page/rcmd/resource/show", data, "eapi")
        case .artistActivity(let before):
            let timestamp: Any
            if let before { timestamp = before }
            else {
                let milliseconds = now.timeIntervalSince1970 * 1000
                guard milliseconds.isFinite, milliseconds >= 0, milliseconds <= 9_007_199_254_740_991 else {
                    throw MusicServiceError.invalidRequest
                }
                timestamp = Int64(milliseconds)
            }
            return ("/api/sub/artist/new/works/song-mv/list/v2", [
                "limit": 10, "sourceType": 1, "firstRequest": before == nil, "startTimestamp": timestamp
            ], "eapi")
        case .songMetadata(let ids):
            // Retain numeric precision inside c, which is itself a JSON string.
            let c = "[" + ids.map { "{\"id\":" + $0 + "}" }.joined(separator: ",") + "]"
            return ("/api/v3/song/detail", ["c": c], "weapi")
        case .playlistMetadata(let ids):
            return ("/api/tag/tab/playlists", ["ids": ids.joined(separator: ",")], "eapi")
        case .vipSongs:
            return ("/api/vipnewcenter/app/viptab/recommend/song/list", [:], "eapi")
        }
    }

    static let guestActivity = Data(#"{"data":{"newWorks":[],"hasMore":false},"capability":"authentication-required"}"#.utf8)
}

extension NeteaseMusicAccountAdapter: MusicNeteaseHomeProvider {
    func homePayload(_ input: MusicNativeHomeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data {
        let own = epoch
        try check(own)
        let (path, data, crypto) = try MusicNeteaseHomeRequest.specification(input, now: now())
        let context = try context(credential)
        try await authorize(); try check(context, epoch: own)
        let membershipExpiry: Date?
        if case .vipSongs = input {
            // b900c259 discoveryRequest marks this read vip:true. A missing
            // entitlement is not an expired login and must not revoke it.
            let rights = try await membership(context)
            try check(context, epoch: own); try await authorize(); try check(context, epoch: own)
            guard !rights.qualities.isEmpty, rights.expires > now() else {
                throw MusicServiceError.verificationRequired
            }
            membershipExpiry = rights.expires
        } else { membershipExpiry = nil }
        // request() performs the existing eapi encryption, no-cookie-jar HTTP
        // and same-account Set-Cookie checks. Home never commits credentials.
        let root = try await request(path, data: data, crypto: crypto, context: context)
        try check(context, epoch: own); try await authorize(); try check(context, epoch: own)
        if let membershipExpiry, membershipExpiry <= now() { throw MusicServiceError.verificationRequired }
        return try MusicNeteaseHomePayload.encode(root, secrets: Array(context.cookies.values) + Array(context.device.values))
    }
}

enum MusicNeteaseHomePayload {
    // Home has heterogeneous nested music resources, not account responses.
    // Strip authentication envelope fields rather than exposing raw credentials
    // through this intentionally data-only boundary. Reject reflected secrets
    // in otherwise legitimate labels, URLs and nested stringified payloads.
    static let privateKeys: Set<String> = [
        "cookie", "cookies", "setcookie", "authorization", "proxyauthorization",
        "musicu", "musica", "csrf", "csrftoken", "jsessionid",
        "accesstoken", "refreshtoken", "password", "credential", "credentials",
        "deviceid", "ntesnuid", "ntesnnid", "wnmcid", "nmtid",
        "sessionkey", "secretkey"
    ]
    static func encode(_ root: [String: Any], secrets: [String]) throws -> Data {
        var remaining = 60_000
        let value = try clean(root, secrets: secrets.filter { !$0.isEmpty }, depth: 0, remaining: &remaining)
        let bytes = try JSONObject.encode(value)
        guard bytes.count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
        return bytes
    }
    static func clean(_ value: Any, secrets: [String], depth: Int, remaining: inout Int) throws -> Any {
        guard depth <= 40, remaining > 0 else { throw MusicServiceError.limit }
        remaining -= 1
        if let values = value as? [String: Any] {
            var result: [String: Any] = [:]
            for (key, child) in values {
                let normalized = key.lowercased().filter { $0.isLetter || $0.isNumber }
                if privateKeys.contains(normalized) { continue }
                _ = try MusicAccountPublicText.profile(key, secrets: secrets)
                result[key] = try clean(child, secrets: secrets, depth: depth + 1, remaining: &remaining)
            }
            return result
        }
        if let values = value as? [Any] {
            return try values.map { try clean($0, secrets: secrets, depth: depth + 1, remaining: &remaining) }
        }
        if let text = value as? String { return try MusicAccountPublicText.profile(text, secrets: secrets) }
        if value is NSNull || value is NSNumber || value is JSONNumber { return value }
        throw MusicServiceError.upstreamInvalid
    }
}
