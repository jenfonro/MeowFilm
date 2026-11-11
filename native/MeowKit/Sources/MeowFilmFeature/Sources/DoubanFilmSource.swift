import CoreFoundation
import Foundation
import MeowRuntime

public struct DoubanFilmSource: FilmCategorySource {
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
                    let (bytes, cache) = try await read(kind: section.kind, query: [
                        "start": .number(0), "limit": .number(12),
                        "category": .string(section.category), "type": .string(section.type)
                    ], refresh: refresh)
                    try Task.checkCancellation()
                    let items = try Self.parse(bytes, sourceID: descriptor.id)
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
        let (bytes, cache) = try await read(kind: kind, query: Self.categoryQuery(category, filters: filters, offset: offset),
                                          refresh: refresh)
        try Task.checkCancellation()
        let rows = try Self.rows(bytes)
        let items = Self.parseRows(rows, sourceID: descriptor.id)
        let count = rows.count
        return FilmCategoryPage(items: items, nextOffset: count < 24 ? nil : offset + count, cache: cache)
    }

    private func read(kind: String, query: RuntimeObject, refresh: Bool) async throws -> (Data, FilmMetadataCache) {
        var parameters: RuntimeObject = [
            "provider": .string("douban"), "path": .string("/rexxar/api/v2/subject/recent_hot/" + kind),
            "query": .object(query)
        ]
        if refresh { parameters["refresh"] = .bool(true) }
        let client = try await connection()
        let result = try await client.call(module: .film, operation: "data.metadata.entry", parameters: .object(parameters))
        try Task.checkCancellation()
        guard !client.isClosed else { throw RuntimeHTTPError.closed }
        guard let fields = result.filmObject, let value = fields["value"], let cache = fields["cache"] else {
            throw FilmHomeError.invalidResponse
        }
        return (try RuntimeJSON.encode(value), try FilmMetadataCache(value: cache))
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
        parseRows(try rows(bytes), sourceID: sourceID)
    }

    private static func rows(_ bytes: Data) throws -> [Any] {
        let root: Any
        do {
            root = try JSONSerialization.jsonObject(with: bytes)
        } catch {
            throw FilmHomeError.invalidResponse
        }
        guard let object = root as? [String: Any], let rows = object["items"] as? [Any] else {
            throw FilmHomeError.invalidResponse
        }
        return rows
    }

    private static func parseRows(_ rows: [Any], sourceID: String) -> [FilmItem] {
        var items: [FilmItem] = []
        var identities = Set<String>()
        for row in rows {
            guard let row = row as? [String: Any] else { continue }
            let title = text(row["title"])
            let id = scalar(row["id"])
            guard !title.isEmpty, !id.isEmpty, identities.insert(id).inserted else { continue }
            let pic = row["pic"] as? [String: Any]
            let cover = row["cover"] as? [String: Any]
            let address = [pic?["normal"], pic?["large"], cover?["url"],
                           row["cover_url"], row["poster"], row["image"]]
                .map(text).first { !$0.isEmpty } ?? ""
            let posterURL = FilmPosterAddress.normalize(address)
            let largeURL = FilmPosterAddress.normalize(text(pic?["large"]))
            items.append(FilmItem(sourceID: sourceID, id: id, title: title, posterURL: posterURL,
                                  largePosterURL: largeURL == posterURL ? nil : largeURL,
                                  score: score(row["rating"]), subtitle: text(row["card_subtitle"])))
        }
        return items
    }

    private static func text(_ value: Any?) -> String {
        (value as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    private static func scalar(_ value: Any?) -> String {
        if let value = value as? String { return text(value) }
        guard let value = value as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() else { return "" }
        return value.stringValue
    }

    private static func score(_ value: Any?) -> String {
        guard let rating = value as? [String: Any], let value = rating["value"] else { return "" }
        let number: Double?
        if let value = value as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() {
            number = value.doubleValue
        } else if let value = value as? String {
            number = Double(value.trimmingCharacters(in: .whitespacesAndNewlines))
        } else {
            number = nil
        }
        guard let number, number.isFinite, number > 0 else { return "" }
        let formatted = String(format: "%.1f", locale: Locale(identifier: "en_US_POSIX"), number)
        return formatted.hasSuffix(".0") ? String(formatted.dropLast(2)) : formatted
    }
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
