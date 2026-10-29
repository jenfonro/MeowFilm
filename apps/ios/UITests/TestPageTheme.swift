import Combine
import MeowUI

@MainActor
final class TestPageTheme: ObservableObject, PageThemeProviding {
    @Published private(set) var pageColorway: PageColorway
    private(set) var animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> {
        objectWillChange.eraseToAnyPublisher()
    }

    init(colorway: PageColorway = .lilac) { pageColorway = colorway }

    func setColorway(_ value: PageColorway, animated: Bool = false) {
        guard pageColorway != value else { return }
        animatesPageColorway = animated
        pageColorway = value
    }
}
