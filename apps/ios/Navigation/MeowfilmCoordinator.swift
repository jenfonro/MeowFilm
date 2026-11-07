import UIKit
import Combine
import MeowUI
import MeowMusicFeature
import MeowFilmFeature
import AVKit

@MainActor
final class MeowfilmCoordinator: NSObject, MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    private let window: UIWindow
    private let assembly: MeowfilmAssembly
    private let resetUserData: @MainActor () async throws -> Void
    private let navigation: PageNavigationController
    private let root: MeowfilmRootContainerViewController
    private var observations: Set<AnyCancellable> = []
    private var main: MeowfilmPrimaryShellViewController?
    private var surface: MusicPlayerSurface?
    private var playerContent: MusicPlayerContentModel?
    private var pendingPresentation: (destination: MusicPlaybackDestination, access: UUID, occurrence: String?)?
    private weak var player: MusicPlayerViewController?
    private var accessGeneration: UUID?
    private var presentedError: String?

    init(window: UIWindow, assembly: MeowfilmAssembly,
         resetUserData: @escaping @MainActor () async throws -> Void) {
        self.window = window
        self.assembly = assembly
        self.resetUserData = resetUserData
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
            self.root.setRootVisible(primary, animated: animated)
        }
        navigation.didShowPage = { [weak self] page in
            guard let self else { return }
            self.root.setRootVisible(page === self.main, animated: false)
            self.showPendingError()
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

    func becameActive() {
        assembly.appearance.reload()
        if let pending = pendingPresentation, let model = assembly.music {
            pendingPresentation = nil
            if pending.access == model.accountAccessGeneration, pending.occurrence == model.current?.id {
                showPlayer(pending.destination)
            }
        }
        showPendingError()
    }

    func showSearch() {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicSearchViewController(
            model: model, appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showSettings() {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicSettingsViewController(
            model: model, appearance: assembly.appearance, router: self, resetUserData: resetUserData
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
        guard let model = assembly.music, let browser = model.publicBrowser else { return }
        navigation.pushViewController(MusicCollectionViewController(
            model: model,
            collection: collection,
            browser: browser,
            appearance: assembly.appearance,
            router: self
        ), animated: true)
    }

    func showLocalCollection(_ collection: MusicCollection) {
        guard let model = assembly.music else { return }
        navigation.pushViewController(MusicCollectionViewController(
            model: model, collectionID: collection.id, appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showPlayer(_ destination: MusicPlaybackDestination) {
        guard let model = assembly.music, model.isAvailable, !model.isClosed else { return }
        guard UIApplication.shared.applicationState == .active else {
            pendingPresentation = (destination, model.accountAccessGeneration, model.current?.id)
            return
        }
        if destination == .queue {
            let page = MusicQueueViewController(model: model, appearance: assembly.appearance)
            presenter().present(page, animated: true)
            return
        }
        if let player {
            if destination == .sources { player.requestSources() }
            return
        }
        guard let playerContent else { return }
        let page = MusicPlayerViewController(content: playerContent, appearance: assembly.appearance)
        page.onPresent = { [weak self] destination in self?.showPlayer(destination) }
        page.onDismiss = { [weak self] in
            self?.player = nil
            self?.primaryPlaybackPresentation.setFullPlayerPresented(false)
        }
        player = page
        primaryPlaybackPresentation.setFullPlayerPresented(true)
        primaryPlaybackPresentation.invalidate()
        presenter().present(page, animated: true) { [weak self, weak page] in
            if destination == .sources { page?.requestSources() }
            self?.showPendingError()
        }
    }

    func selectChannel(_ channel: MusicPublicBrowseChannel) {
        navigation.popToRootViewController(animated: true)
        main?.selectChannel(channel)
    }

    private func showFilm(_ item: FilmItem) {
        let page = FilmDetailViewController(detail: FilmDetail(item: item), library: assembly.filmLibrary,
            images: assembly.filmImages, themeProvider: assembly.appearance)
        page.onSelectItem = { [weak self] in self?.showFilm($0) }
        page.onPlay = { [weak self, weak page] url in
            guard let self, let page, self.navigation.topViewController === page else { return }
            self.assembly.playback.pause()
            let player = AVPlayerViewController()
            player.player = AVPlayer(url: url)
            page.present(player, animated: true) { player.player?.play() }
        }
        navigation.pushViewController(page, animated: true)
    }

    private func install(_ model: MusicViewModel) {
        guard main == nil else { return }
        let appearance = assembly.appearance
        let factories: [@MainActor () -> UIViewController] = [
            { [weak self] in
                guard let self else { return UIViewController() }
                return MusicBrowseViewController(title: "推荐", model: model, browser: model.publicBrowser,
                    request: .discovery(.home), appearance: appearance, router: self, embedded: true)
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                return MusicChannelsViewController(model: model, browser: model.publicBrowser,
                    appearance: appearance, router: self)
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                let page = FilmHomeViewController(model: FilmHomeModel(sources: self.assembly.filmSources),
                                                  images: self.assembly.filmImages,
                                                  themeProvider: appearance)
                page.onSelectItem = { [weak self] in self?.showFilm($0) }
                page.onSelectCategory = { [weak self] model in
                    guard let self else { return }
                    let category = FilmCategoryViewController(model: model, images: self.assembly.filmImages,
                                                               themeProvider: appearance)
                    category.onSelectItem = { [weak self] in self?.showFilm($0) }
                    self.navigation.pushViewController(category, animated: true)
                }
                return page
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                return MusicLibraryViewController(model: model, appearance: appearance, router: self)
            }
        ]
        let page = MeowfilmPrimaryShellViewController(
            pageFactories: factories, themeProvider: appearance,
            openMenu: { [weak self] in self?.root.showSidebar() },
            openSearch: { [weak self] in self?.showSearch() }
        )
        page.onSelectionChanged = { [weak self] selected, duration in
            self?.root.select(selected, duration: duration)
        }
        main = page
        let content = MusicPlayerContentModel(model: model, playback: assembly.playback,
                                              appearance: appearance, presentation: primaryPlaybackPresentation)
        playerContent = content
        let surface = MusicPlayerSurface(content: content)
        surface.onPresent = { [weak self] destination in self?.showPlayer(destination) }
        self.surface = surface
        root.install(surface: surface)
        navigation.setViewControllers([page], animated: false)
        accessGeneration = model.accountAccessGeneration
        model.accessChanges.receive(on: RunLoop.main).sink { [weak self, weak model] in
            guard let self, let model else { return }
            if self.accessGeneration != model.accountAccessGeneration ||
                !model.isAvailable || model.isClosed {
                self.accessGeneration = model.accountAccessGeneration
                self.pendingPresentation = nil
                self.presentedError = nil
                self.primaryPlaybackPresentation.invalidate()
                self.surface?.collapse()
                self.root.dismiss(animated: false)
            }
        }.store(in: &observations)
        model.noticeChanges.receive(on: RunLoop.main).sink { [weak self] in
            self?.showPendingError()
        }.store(in: &observations)
        model.onRequestPresentation = { [weak self, weak model] destination in
            guard let self, let model, self.assembly.music === model else { return }
            self.showPlayer(destination)
        }
    }

    private func showPendingError() {
        guard UIApplication.shared.applicationState == .active, let model = assembly.music,
              let error = model.error, presentedError != error else { return }
        let top = presenter()
        let page = (top as? UINavigationController)?.topViewController ?? top
        if let page = page as? PageViewController,
           page.showError(error, acknowledged: { [weak self, weak model] in
               if model?.error == error { model?.error = nil }
               self?.presentedError = nil
           }) {
            presentedError = error
        }
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
