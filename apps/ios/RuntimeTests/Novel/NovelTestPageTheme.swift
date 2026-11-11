#if os(iOS)
import Combine
import MeowUI

@MainActor
final class NovelTestPageTheme: PageThemeProviding {
    let pagePalette: PagePalette = .preset(.lilac)
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { Empty<Void, Never>().eraseToAnyPublisher() }
}
#endif
