import UIKit
import Combine
import MeowUI
import MeowMusicFeature

@MainActor
final class MeowfilmCoordinator: NSObject, MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    private let window: UIWindow
    private let assembly: MeowfilmAssembly
    private let navigation: PageNavigationController
    private let root: MeowfilmRootContainerViewController
    private var observations: Set<AnyCancellable> = []
    private var main: MeowfilmPrimaryShellViewController?
    private var surface: MusicPlayerSurface?
    private weak var player: MusicPlayerViewController?
    private var accessGeneration: UUID?
    private var presentedError: String?

    init(window: UIWindow, assembly: MeowfilmAssembly) {
        self.window = window
        self.assembly = assembly
        navigation = PageNavigationController(themeProvider: assembly.appearance)
        root = MeowfilmRootContainerViewController(navigation: navigation, themeProvider: assembly.appearance)
        super.init()
    }

    func start() {
        navigation.setViewControllers([
            MeowfilmStartupViewController(themeProvider: assembly.appearance)
        ], animated: false)
        navigation.willShowPage = { [weak self] page, animated in
            guard let self else { return }
            let primary = page === self.main
            self.navigation.setNavigationBarHidden(primary, animated: animated)
            self.root.setRootVisible(primary, animated: animated)
        }
        navigation.didShowPage = { [weak self] page in
            guard let self else { return }
            self.root.setRootVisible(page === self.main, animated: false)
        }
        root.onSelectPage = { [weak self] page in
            self?.primaryPlaybackPresentation.invalidate()
            self?.surface?.collapse()
            self?.main?.select(page)
        }
        root.onOpenSettings = { [weak self] in self?.showSettings() }
        window.rootViewController = root
        window.makeKeyAndVisible()
        assembly.$music.receive(on: RunLoop.main).sink { [weak self] model in
            if let model { self?.install(model) }
        }.store(in: &observations)
        assembly.$startupFailed.receive(on: RunLoop.main).sink { [weak self] failed in
            guard let self, failed, self.main == nil else { return }
            let page = MeowfilmStartupViewController(themeProvider: self.assembly.appearance)
            page.message = "本地数据不可用"
            self.navigation.setViewControllers([page], animated: false)
        }.store(in: &observations)
        if let model = assembly.music { install(model) }
    }

    func becameActive() { assembly.appearance.reload() }

    func showSearch() {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicSearchViewController(
            model: model, appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showSettings() {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicSettingsViewController(
            model: model, appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicBrowseViewController(
            title: title, model: model, browser: model.publicBrowser, request: request,
            appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showPublicCollection(_ collection: MusicPublicBrowseCollection) {
        showPublicBrowse(.detail(collection), title: collection.title)
    }

    func showLocalCollection(_ collection: MusicCollection) {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicCollectionViewController(
            model: model, collectionID: collection.id, appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showPlayer(_ destination: MusicPlaybackDestination, sourceView: UIView?) {
        guard let model = assembly.music, model.isAvailable, !model.isClosed else { return }
        if destination == .queue {
            let page = MusicQueueViewController(model: model, appearance: assembly.appearance)
            let wrapper = PageNavigationController(themeProvider: assembly.appearance)
            wrapper.setViewControllers([page], animated: false)
            wrapper.modalPresentationStyle = .pageSheet
            wrapper.sheetPresentationController?.detents = [.medium(), .large()]
            wrapper.sheetPresentationController?.prefersGrabberVisible = true
            presenter().present(wrapper, animated: true)
            return
        }
        guard player == nil, let surface, let dock = root.dock?.surfaceHost else { return }
        let page = MusicPlayerViewController(
            model: model, playback: assembly.playback, appearance: assembly.appearance,
            surface: surface, dock: dock
        )
        page.onDismiss = { [weak self] in
            self?.player = nil
            self?.primaryPlaybackPresentation.setFullPlayerPresented(false)
        }
        player = page
        primaryPlaybackPresentation.setFullPlayerPresented(true)
        primaryPlaybackPresentation.invalidate()
        presenter().present(page, animated: true)
    }

    func selectChannel(_ channel: MusicPublicBrowseChannel) {
        navigation.popToRootViewController(animated: true)
        main?.selectChannel(channel)
    }

    private func install(_ model: MusicViewModel) {
        guard main == nil else { return }
        let appearance = assembly.appearance
        let home = MusicBrowseViewController(title: "推荐", model: model, browser: model.publicBrowser,
                                             request: .discovery(.home), appearance: appearance,
                                             router: self, embedded: true)
        let channels = MusicChannelsViewController(model: model, browser: model.publicBrowser,
                                                   appearance: appearance, router: self)
        let library = MusicLibraryViewController(model: model, appearance: appearance, router: self)
        let page = MeowfilmPrimaryShellViewController(
            home: home, channels: channels, library: library, themeProvider: appearance,
            openMenu: { [weak self] in self?.root.showSidebar() },
            openSearch: { [weak self] in self?.showSearch() }
        )
        page.onSelectionChanged = { [weak self] selected in self?.root.select(selected) }
        main = page
        let surface = MusicPlayerSurface(model: model, playback: assembly.playback,
                                         appearance: appearance, presentation: primaryPlaybackPresentation)
        surface.onPresent = { [weak self] destination in self?.showPlayer(destination, sourceView: nil) }
        self.surface = surface
        root.install(surface: surface)
        navigation.setViewControllers([page], animated: false)
        accessGeneration = model.accountAccessGeneration
        model.objectWillChange.receive(on: RunLoop.main).sink { [weak self, weak model] in
            guard let self, let model else { return }
            if self.accessGeneration != model.accountAccessGeneration ||
                !model.isAvailable || model.isClosed {
                self.accessGeneration = model.accountAccessGeneration
                self.primaryPlaybackPresentation.invalidate()
                self.surface?.collapse()
                self.root.dismiss(animated: false)
            }
            if model.showingPlayer || model.showingSources {
                let sources = model.showingSources
                model.showingPlayer = false
                model.showingSources = false
                self.showPlayer(.player, sourceView: nil)
                if sources { self.player?.requestSources() }
            }
            if let error = model.error, self.presentedError != error {
                let top = self.presenter()
                let page = (top as? UINavigationController)?.topViewController ?? top
                if let page = page as? PageViewController,
                   page.showError(error, acknowledged: { [weak self, weak model] in
                       if model?.error == error { model?.error = nil }
                       self?.presentedError = nil
                   }) {
                    self.presentedError = error
                }
            }
        }.store(in: &observations)
    }

    private func presenter() -> UIViewController {
        var page: UIViewController = root
        while let presented = page.presentedViewController { page = presented }
        return page
    }
}

@MainActor
private final class MeowfilmStartupViewController: PageViewController {
    var message: String?

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityLabel = message ?? "加载中"
        guard let message else { return }
        let label = UILabel()
        label.text = message
        label.textAlignment = .center
        label.textColor = UIColor(theme.ink)
        label.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: view.centerYAnchor),
        ])
    }
}
