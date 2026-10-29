// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus recommendation.js / netease-client.js / src/netease.ts at b900c259.
import Foundation
import MeowAccounts
import MeowMusicFeature

protocol MusicNeteaseFMProvider: Sendable {
    func fm(_ action: MusicHomeAction, preview: Bool, credential: AccountCredential?,
            authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeFMResponse
}

extension NeteaseMusicAccountAdapter: MusicNeteaseFMProvider {
    func fm(_ action: MusicHomeAction, preview: Bool, credential: AccountCredential?,
            authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeFMResponse {
        let own = epoch
        try check(own)
        guard action.isPersonalFM,
              var specification = try MusicNeteaseHomeActionRequest.specification(action) else {
            throw MusicServiceError.invalidRequest
        }
        let coverOnly: Bool
        switch action {
        case .ruleFM:
            coverOnly = preview
            if preview {
                specification.0 = "/api/tag/tab/fm/cover"
                specification.1.removeValue(forKey: "limit")
            }
        case .fm(_, _, let entry):
            coverOnly = false
            if preview && entry == .channel { specification.1["limit"] = 1 }
        default: throw MusicServiceError.invalidRequest
        }
        try await authorize(); try check(own)
        // Reuse the cookie-free public request context, not a second login or
        // anonymous registration. Signed-in requests use only their exact lease.
        let context = try credential.map { try self.context($0) } ?? publicAudioRequestContext()
        let result = try await request(specification.0, data: specification.1, crypto: specification.2, context: context)
        try await authorize(); try check(own)
        _ = try MusicNeteaseHomePayload.encode(result, secrets: Array(context.cookies.values) + Array(context.device.values))
        if coverOnly {
            let data = result["data"] as? [String: Any]
            return .init(artwork: Self.fmArtwork(data?["cover"] as? String))
        }
        let data = result["data"], body = data as? [String: Any]
        let raw = try MusicNeteaseWire.rows(body?["songs"] ?? data, maximum: 100).map {
            ($0["songInfo"] as? [String: Any]) ?? ($0["song"] as? [String: Any]) ?? $0
        }
        let rows = try await homeActionRows(raw, context: context, own: own, authorize: authorize)
        let limit: Int
        switch action {
        case .fm(_, _, .channel?) where preview: limit = 1
        case .fm: limit = 3
        default: limit = 5
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let tracks = try rows.prefix(limit).map { MusicNativeTrackData(platform: "wy", data: try encoder.encode($0)) }
        let artwork: URL?
        if case .object(let first)? = rows.first, case .string(let url)? = first["img"] {
            artwork = Self.fmArtwork(url)
        } else { artwork = nil }
        try await authorize(); try check(own)
        return .init(tracks: tracks, artwork: artwork)
    }
    private static func fmArtwork(_ text: String?) -> URL? {
        guard let text, text.utf8.count <= 4096, var url = URLComponents(string: text),
              ["http", "https"].contains(url.scheme ?? ""), url.user == nil, url.password == nil,
              url.port == nil || url.port == 443, url.fragment == nil,
              let host = url.host?.lowercased(),
              host == "music.126.net" || host.hasSuffix(".music.126.net") ||
                host == "music.163.net" || host.hasSuffix(".music.163.net") else { return nil }
        url.scheme = "https"; return url.url
    }
}
