import Foundation
import MeowDebug
import MeowRuntime

public struct DoubanFilmSource: Sendable {
    public let descriptor = FilmSourceDescriptor(id: "douban", title: "豆瓣")
    public let categories = FilmCategory.allCases
    private let connection: @Sendable () async throws -> RuntimeHTTPClient

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.connection = connection
    }

    public func loadHome(refresh: Bool = false) async throws -> [FilmHomeSection] {
        try Task.checkCancellation()
        return try await withThrowingTaskGroup(of: (Int, FilmHomeSection).self) { group in
            for (index, section) in DoubanHomeSection.all.enumerated() {
                group.addTask {
                    let (value, cache) = try await read(kind: section.kind, query: [
                        "start": .number(0), "limit": .number(12),
                        "category": .string(section.category), "type": .string(section.type)
                    ], refresh: refresh)
                    try Task.checkCancellation()
                    let items = try Self.parseValue(value, sourceID: descriptor.id)
                    return (index, FilmHomeSection(id: section.id, title: section.title,
                                                  items: Array(items.prefix(12)), cache: cache))
                }
            }
            var sections: [Int: FilmHomeSection] = [:]
            for try await (index, section) in group {
                sections[index] = section
            }
            try Task.checkCancellation()
            return sections.sorted { $0.key < $1.key }.map(\.value)
        }
    }

    public func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int,
                             refresh: Bool = false) async throws -> FilmCategoryPage {
        let kind = category == .movie ? "movie" : "tv"
        let (value, cache) = try await read(kind: kind, query: Self.categoryQuery(category, filters: filters, offset: offset),
                                          refresh: refresh)
        try Task.checkCancellation()
        let rows = try Self.rows(value)
        let items = try Self.parseRows(rows, sourceID: descriptor.id)
        let count = rows.count
        return FilmCategoryPage(items: items, nextOffset: count < 24 ? nil : offset + count, cache: cache)
    }

    private func read(kind: String, query: RuntimeObject, refresh: Bool) async throws -> (RuntimeValue, FilmMetadataCache) {
        var parameters: RuntimeObject = [
            "provider": .string("douban"), "path": .string("/rexxar/api/v2/subject/recent_hot/" + kind),
            "query": .object(query)
        ]
        if refresh { parameters["refresh"] = .bool(true) }
        let client = try await connection()
        let result = try await client.call(module: .film, operation: "data.metadata.entry", parameters: .object(parameters))
        try Task.checkCancellation()
        guard !client.isClosed else { throw RuntimeHTTPError.closed }
        guard let fields = result.filmObject, let value = fields["value"] else {
            throw FilmHomeError.invalidResponse
        }
        return (value, FilmMetadataCache(value: fields["cache"] ?? .null))
    }

    static func categoryQuery(_ category: FilmCategory, filters: [String: String], offset: Int) -> RuntimeObject {
        let values = category.defaultFilters.merging(filters) { _, new in new }
        let requestCategory: String
        let type: String
        switch category {
        case .movie:
            requestCategory = values["category"]!
            type = values["area"]!
        case .tv:
            requestCategory = "tv"
            type = values["category"] == "tv" ? values["area"]! : values["category"]!
        case .anime:
            requestCategory = "tv"
            type = "tv_animation"
        case .show:
            requestCategory = "show"
            type = values["area"]!
        }
        return ["start": .number(Double(offset)), "limit": .number(24),
                "category": .string(requestCategory), "type": .string(type)]
    }

    static func parse(_ bytes: Data, sourceID: String = "douban") throws -> [FilmItem] {
        let decoded: RuntimeValue
        do { decoded = try RuntimeJSON.decode(bytes) }
        catch { throw FilmHomeError.invalidResponse }
        return try parseValue(decoded, sourceID: sourceID)
    }

    private static func parseValue(_ value: RuntimeValue, sourceID: String) throws -> [FilmItem] {
        try parseRows(rows(value), sourceID: sourceID)
    }

    private static func rows(_ value: RuntimeValue) throws -> [RuntimeValue] {
        guard let object = value.filmObject, let rows = object["items"]?.filmArray else {
            throw FilmHomeError.invalidResponse
        }
        return rows
    }

    private static func parseRows(_ rows: [RuntimeValue], sourceID: String) throws -> [FilmItem] {
        var items: [FilmItem] = []
        var identities = Set<String>()
        for row in rows {
            guard let fields = row.filmObject else { continue }
            let inner = fields["subject"]?.filmObject ?? fields
            let title = text(inner["title"])
            let id = scalar(inner["id"])
            guard !title.isEmpty, !id.isEmpty else { continue }
            let entry: FilmEntry
            do {
                entry = try FilmEntry(value: fields.filmValue("card"))
            } catch {
                DebugRuntime.logError(.filmCatalog, sub: "douban", event: "row.skipped", error: error,
                                      data: .object(["id": .string(id)]))
                continue
            }
            guard identities.insert(id).inserted else { continue }
            let pic = inner["pic"]?.filmObject
            let coverImage = inner["cover"]?.filmObject?["image"]?.filmObject
            let coverNormal = coverImage?["normal"]?.filmObject?["url"]
            let coverLarge = coverImage?["large"]?.filmObject?["url"]
            let address = [pic?["normal"], pic?["large"], coverNormal, coverLarge]
                .map(text).first { !$0.isEmpty } ?? entry.poster
            let posterURL = FilmPosterAddress.normalize(address)
            let largeAddress = [pic?["large"], coverLarge].map(text).first { !$0.isEmpty } ?? ""
            let largeURL = FilmPosterAddress.normalize(largeAddress)
            let scoreText = score(inner["rating"])
            let subtitleText = text(inner["card_subtitle"])
            items.append(FilmItem(
                sourceID: sourceID,
                id: id,
                title: title,
                kind: entry.kind,
                posterURL: posterURL,
                largePosterURL: largeURL == posterURL ? nil : largeURL,
                score: scoreText.isEmpty ? entry.score : scoreText,
                subtitle: subtitleText.isEmpty ? entry.subtitle : subtitleText,
                entry: entry
            ))
        }
        guard rows.isEmpty || !items.isEmpty else { throw FilmHomeError.invalidResponse }
        return items
    }

    private static func text(_ value: RuntimeValue?) -> String {
        value?.filmText?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    private static func scalar(_ value: RuntimeValue?) -> String {
        if let text = value?.filmText { return text.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard let number = value?.filmNumber, number.isFinite else { return "" }
        if number.rounded(.towardZero) == number, abs(number) <= 9_007_199_254_740_991 {
            return String(Int64(number))
        }
        return String(number)
    }

    private static func score(_ value: RuntimeValue?) -> String {
        guard let rating = value?.filmObject, let raw = rating["value"] else { return "" }
        let number: Double?
        if let value = raw.filmNumber {
            number = value
        } else if let text = raw.filmText {
            number = Double(text.trimmingCharacters(in: .whitespacesAndNewlines))
        } else {
            number = nil
        }
        guard let number, number.isFinite, number > 0 else { return "" }
        return String(format: "%.1f", locale: posixLocale, number)
    }

    private static let posixLocale = Locale(identifier: "en_US_POSIX")
}

private struct DoubanHomeSection: Sendable {
    let id: String
    let title: String
    let kind: String
    let category: String
    let type: String

    static let all: [DoubanHomeSection] = [
        .init(id: "movie", title: "热门电影", kind: "movie", category: "热门", type: "全部"),
        .init(id: "tv", title: "热门剧集", kind: "tv", category: "tv", type: "tv"),
        .init(id: "anime", title: "新番放送", kind: "tv", category: "tv", type: "tv_animation"),
        .init(id: "show", title: "热门综艺", kind: "tv", category: "show", type: "show")
    ]
}
