import UIKit
import MeowUI
import MeowFilmFeature
import MeowNovelFeature

/// App composition owns the relationship between domains and navigation. The
/// neutral search input and music algorithms know no film model or service.
@MainActor
enum MeowfilmSearchComposition {
    static let music = SearchTargetOption(id: "music", title: "音乐", placeholder: "搜索歌曲、歌手、歌单")
    static let novel = SearchTargetOption(id: "novel", title: "小说", placeholder: "搜索小说、作者")
    static let film = SearchTargetOption(id: "film", title: "影视", placeholder: "搜索影视")

    static func defaultTarget(on page: MeowfilmPrimaryPage) -> String {
        page == .film ? film.id : page == .novel ? novel.id : music.id
    }

    static func novelCategory(feature: NovelFeature, themeProvider: any PageThemeProviding,
                              select: @escaping (NovelBook) -> Void) -> SearchResultCategory {
        SearchResultCategory(target: novel) {
            let content = NovelSearchResultsContent(feature: feature, theme: PageTheme(colors: themeProvider.pagePalette))
            content.onSelect = select; return content
        }
    }

    static func category(service: FilmSearchService, images: ArtworkImages, themeProvider: any PageThemeProviding,
                         select: @escaping (FilmSearchContext) -> Void) -> SearchResultCategory {
        SearchResultCategory(target: film) {
            let content = FilmSearchResultsContent(service: service, images: images,
                                                  theme: PageTheme(colors: themeProvider.pagePalette))
            content.onSelect = select
            return content
        }
    }
}
