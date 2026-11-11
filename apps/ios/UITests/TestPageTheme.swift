import Combine
import MeowUI

@MainActor
final class TestPageTheme: ObservableObject, PageThemeProviding {
    @Published private(set) var pagePalette: PagePalette
    private(set) var animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> {
        objectWillChange.eraseToAnyPublisher()
    }

    init(colorway: PageColorway = .lilac) { pagePalette = .preset(colorway) }

    func setColorway(_ value: PageColorway, animated: Bool = false) {
        guard pagePalette != .preset(value) else { return }
        animatesPageColorway = animated
        pagePalette = .preset(value)
    }
}
