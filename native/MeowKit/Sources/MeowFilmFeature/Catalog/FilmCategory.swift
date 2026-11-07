import Foundation
import MeowUI

public enum FilmCategory: String, CaseIterable, Sendable {
    case movie, tv, anime, show

    public var title: String {
        switch self {
        case .movie: "电影"
        case .tv: "剧集"
        case .anime: "动漫"
        case .show: "综艺"
        }
    }

    var defaultFilters: [String: String] {
        switch self {
        case .movie: ["category": "热门", "area": "全部"]
        case .tv: ["category": "tv", "area": "tv"]
        case .anime: [:]
        case .show: ["area": "show"]
        }
    }

    func filterRows(for filters: [String: String]) -> [FilmCategoryFilter] {
        switch self {
        case .movie:
            return [
                .init(id: "category", title: "分类", options: [
                    .init(id: "热门", title: "热门电影"), .init(id: "最新", title: "最新电影"),
                    .init(id: "豆瓣高分", title: "豆瓣高分"), .init(id: "冷门佳片", title: "冷门佳片")]),
                .init(id: "area", title: "地区", options: ["全部", "华语", "欧美", "韩国", "日本"].map {
                    FilterTag(id: $0, title: $0)
                })
            ]
        case .tv:
            let category = FilmCategoryFilter(id: "category", title: "分类", options: [
                .init(id: "tv", title: "剧集"), .init(id: "tv_animation", title: "动漫"),
                .init(id: "tv_documentary", title: "纪录片")])
            // recent_hot accepts one type: animation/documentary cannot be intersected with a region.
            guard filters["category"] == "tv" else { return [category] }
            return [category, .init(id: "area", title: "地区", options: [
                .init(id: "tv", title: "全部"), .init(id: "tv_domestic", title: "国产"),
                .init(id: "tv_american", title: "欧美"), .init(id: "tv_japanese", title: "日本"),
                .init(id: "tv_korean", title: "韩国")])]
        case .anime: return []
        case .show:
            return [.init(id: "area", title: "地区", options: [
                .init(id: "show", title: "全部"), .init(id: "show_domestic", title: "国内"),
                .init(id: "show_foreign", title: "国外")])]
        }
    }
}

struct FilmCategoryFilter: Equatable {
    let id: String
    let title: String
    let options: [FilterTag]
}

public struct FilmCategoryPage: Sendable {
    public let items: [FilmItem]
    public let nextOffset: Int?

    public init(items: [FilmItem], nextOffset: Int?) {
        self.items = items
        self.nextOffset = nextOffset
    }
}

public protocol FilmCategorySource: FilmSource {
    var categories: [FilmCategory] { get }
    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int) async throws -> FilmCategoryPage
}
