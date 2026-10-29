// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 netease.ts setStyle; api-enhanced recommend_category_*.
import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicAccountDailyStyleSave: Sendable {
    let songs: MusicQQRecommendation
    let saveConfirmed: Bool
    let selectionChanged: Bool
}
protocol MusicAccountDailyStyleProvider: Sendable {
    func dailyStyleCategories(credential: AccountCredential,
        authorize: @escaping @Sendable () async throws -> Void) async throws -> [MusicDailyStyleCategory]
    func saveDailyStyle(_ selection: MusicDailyStyleSelection, credential: AccountCredential,
        authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicAccountDailyStyleSave
}
enum MusicNeteaseDailyStyle {
    static func tags(_ value: Any?) throws -> [MusicDailyStyleTag] {
        let rows = try MusicNeteaseWire.rows(value, maximum: 300)
        let tags = try rows.map { MusicDailyStyleTag(id: try MusicNeteaseWire.id($0["tagId"]),
            name: try MusicQQWire.string($0["tagName"], maximum: 200)) }
        guard Set(tags.map(\.id)).count == tags.count else { throw MusicServiceError.upstreamInvalid }
        return tags
    }
    static func selection(_ value: Any?) throws -> MusicDailyStyleSelection {
        guard let body = value as? [String: Any] else { return .init(category: "", tags: []) }
        return .init(category: try MusicNeteaseWire.id(body["categoryId"]), tags: try tags(body["tagVOList"] ?? []))
    }
    static func categories(_ value: [String: Any]) throws -> [MusicDailyStyleCategory] {
        let body = value["data"] as? [String: Any] ?? [:]
        let rows = try MusicNeteaseWire.rows(body["categorys"], maximum: 30)
        let values = try rows.map { MusicDailyStyleCategory(id: try MusicNeteaseWire.id($0["categoryId"]),
            name: try MusicQQWire.string($0["categoryName"], maximum: 200), tags: try tags($0["tagVOList"] ?? [])) }
        guard !values.isEmpty, Set(values.map(\.id)).count == values.count else { throw MusicServiceError.upstreamInvalid }
        return values
    }
}
extension NeteaseMusicAccountAdapter: MusicAccountDailyStyleProvider {
    func dailyStyleCategories(credential: AccountCredential,
        authorize: @escaping @Sendable () async throws -> Void) async throws -> [MusicDailyStyleCategory] {
        let own = epoch
        try await authorize(); try check(own)
        let root = try await request("/api/homepage/daily/song/config/get", data: [:],
                                     crypto: "xeapi", context: context(credential))
        try await authorize(); try check(own)
        return try MusicNeteaseDailyStyle.categories(root)
    }
    func saveDailyStyle(_ selection: MusicDailyStyleSelection, credential: AccountCredential,
        authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicAccountDailyStyleSave {
        let own = epoch
        let categories = try await dailyStyleCategories(credential: credential, authorize: authorize)
        try selection.validate(in: categories)
        let context = try context(credential)
        let tagIDs = try selection.tags.map { tag -> UInt64 in
            guard let value = UInt64(tag.id), value <= 9_007_199_254_740_991 else { throw MusicServiceError.invalidRequest }
            return value
        }
        guard let category = UInt64(selection.category), category <= 9_007_199_254_740_991 else {
            throw MusicServiceError.invalidRequest
        }
        let tags = try JSONSerialization.data(withJSONObject: ["tagIds": tagIDs, "categoryId": category])
        try await authorize(); try check(own)
        var confirmed = false
        do {
            _ = try await request("/api/homepage/daily/song/tag/save",
                data: ["tags": String(decoding: tags, as: UTF8.self)], crypto: "xeapi", context: context)
            confirmed = true
        } catch {
            // No retry and no compensating write: the upstream may have saved.
            try Task.checkCancellation(); try await authorize(); try check(own)
        }
        try await authorize(); try check(own)
        let root = try await request("/api/homepage/category/daily/song/list", data: [:], crypto: "xeapi", context: context)
        try await authorize(); try check(own)
        let body = root["data"] as? [String: Any] ?? [:]
        let actual = try MusicNeteaseDailyStyle.selection(body["tags"])
        let raw = try MusicNeteaseWire.rows(body["dailySongs"], maximum: 1000)
        let rows = try await homeActionRows(raw, context: context, own: own, authorize: authorize)
        let changed = actual.category != selection.category || Set(actual.tags.map(\.id)) != Set(selection.tags.map(\.id))
        return .init(songs: .init(rows: rows, nextPage: nil, expiresAt: now().addingTimeInterval(60), dailyStyle: actual),
                     saveConfirmed: confirmed, selectionChanged: changed)
    }
}
