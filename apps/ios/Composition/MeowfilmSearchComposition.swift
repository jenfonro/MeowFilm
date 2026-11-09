import UIKit
import MeowUI
import MeowFilmFeature

/// App composition owns the relationship between domains and navigation. The
/// neutral search input and music algorithms know no film model or service.
@MainActor
enum MeowfilmSearchComposition {
    static let music = SearchTargetOption(id: "music", title: "音乐", placeholder: "搜索歌曲、歌手、歌单")
    static let film = SearchTargetOption(id: "film", title: "影视", placeholder: "搜索影视")

    static func defaultTarget(on page: MeowfilmPrimaryPage) -> String {
        page == .film ? film.id : music.id
    }

    static func category(service: FilmSearchService, images: ArtworkImages, themeProvider: any PageThemeProviding,
                         select: @escaping (FilmSearchContext) -> Void) -> SearchResultCategory {
        SearchResultCategory(target: film) {
            let content = FilmSearchResultsContent(service: service, images: images,
                                                  theme: PageTheme(palette: themeProvider.pageColorway))
            content.onSelect = select
            return content
        }
    }
}
