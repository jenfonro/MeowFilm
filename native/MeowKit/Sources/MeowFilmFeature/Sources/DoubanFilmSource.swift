import CoreFoundation
import Foundation

public struct DoubanFilmSource: FilmSource {
    public let descriptor = FilmSourceDescriptor(id: "douban", title: "豆瓣")
    private let http: FilmHTTP

    public init() {
        http = FilmHTTP()
    }

    init(http: FilmHTTP) {
        self.http = http
    }

    public func loadHome() async throws -> [FilmHomeSection] {
        try Task.checkCancellation()
        return try await withThrowingTaskGroup(of: (Int, FilmHomeSection).self) { group in
            for (index, section) in DoubanHomeSection.all.enumerated() {
                group.addTask {
                    let bytes = try await http.readHome(section.url)
                    try Task.checkCancellation()
                    let items = try Self.parse(bytes, sourceID: descriptor.id)
                    return (index, FilmHomeSection(id: section.id, title: section.title, items: items))
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

    static func parse(_ bytes: Data, sourceID: String = "douban") throws -> [FilmItem] {
        let root: Any
        do {
            root = try JSONSerialization.jsonObject(with: bytes)
        } catch {
            throw FilmHomeError.invalidResponse
        }
        guard let object = root as? [String: Any], let rows = object["items"] as? [Any] else {
            throw FilmHomeError.invalidResponse
        }
        var items: [FilmItem] = []
        var identities = Set<String>()
        for row in rows {
            guard items.count < 12 else { break }
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

    var url: URL {
        var parts = URLComponents(string: "https://m.douban.com/rexxar/api/v2/subject/recent_hot/" + kind)!
        parts.queryItems = [
            URLQueryItem(name: "start", value: "0"), URLQueryItem(name: "limit", value: "12"),
            URLQueryItem(name: "category", value: category), URLQueryItem(name: "type", value: type)
        ]
        return parts.url!
    }
}
