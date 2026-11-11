import UIKit
import Combine
import MeowUI
import MeowMusicFeature
import MeowFilmFeature

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
    private var startupErrorAcknowledged = false
    private var filmNavigation: Task<Void, Never>?
    private weak var filmPlayer: FilmPlayerViewController?

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
            startupPage()
        ], animated: false)
        navigation.willShowPage = { [weak self] page, animated in
            guard let self else { return }
            let primary = page === self.main
            self.root.setRootVisible(primary, animated: animated)
        }
        navigation.didShowPage = { [weak self] page in
            guard let self else { return }
            self.root.setRootVisible(page === self.main, animated: false)
            if !(page is FilmDetailViewController), self.filmPlayer == nil {
                self.activateFilmPage(page)
            }
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
            (self.navigation.viewControllers.first as? MeowfilmStartupViewController)?.isLoading = false
            self.showPendingError()
        }.store(in: &observations)
        if let model = assembly.music { install(model) }
        assembly.restoreFilmPlaybackUI = { [weak self] sessionID in
            guard let self, let model = assembly.filmPlayback, model.sessionID == sessionID,
                  UIApplication.shared.applicationState != .background else { return false }
            if let page = self.filmPlayer, page.filmPlaybackModel === model {
                return page.presentingViewController != nil && page.viewIfLoaded?.window != nil &&
                    !page.isBeingPresented && !page.isBeingDismissed
            }
            guard self.root.presentedViewController == nil else { return false }
            await model.open()?.value
            guard model.sessionID == sessionID, self.assembly.filmPlayback === model else { return false }
            return await withCheckedContinuation { continuation in
                let accepted = self.presentFilmPlayer(model, restoring: true) { [weak self] in
                    continuation.resume(returning: self?.filmPlayer?.presentingViewController != nil)
                }
                if !accepted { continuation.resume(returning: false) }
            }
        }
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
        guard let model = assembly.music, let service = assembly.filmSearch else { return }
        let category = MeowfilmSearchComposition.category(service: service, images: assembly.filmImages,
            themeProvider: assembly.appearance) { [weak self] in self?.showFilmSearchResult($0) }
        navigation.pushViewController(MusicSearchViewController(
            model: model, appearance: assembly.appearance, router: self,
            primaryTarget: MeowfilmSearchComposition.music, additionalCategory: category,
            initialTargetID: MeowfilmSearchComposition.defaultTarget(on: main?.selectedPage ?? .recommend)
        ), animated: true)
    }

    func showSettings() {
        let page = MeowfilmSettingsViewController(themeProvider: assembly.appearance, resetUserData: resetUserData)
        page.openMusic = { [weak self, weak page] in
            guard let self else { return }
            guard let model = assembly.music else {
                page?.showError("音乐设置暂不可用")
                return
            }
            navigation.pushViewController(MusicSettingsViewController(
                model: model, appearance: assembly.appearance, router: self
            ), animated: true)
        }
        page.openFilm = { [weak self, weak page] in
            guard let self else { return }
            guard let settings = assembly.filmSettings else {
                page?.showError("影视设置暂不可用")
                return
            }
            navigation.pushViewController(FilmSettingsViewController(
                model: settings, themeProvider: assembly.appearance
            ), animated: true)
        }
        page.openPreferences = { [weak self] in
            guard let self else { return }
            navigation.pushViewController(MusicAppearanceSettingsViewController(store: assembly.appearance), animated: true)
        }
        page.openCache = { [weak self, weak page] in
            guard let self else { return }
            guard let owner = assembly.filmSettings?.playback else {
                page?.showError("缓存管理暂不可用")
                return
            }
            navigation.pushViewController(FilmMetadataCacheSettingsViewController(
                owner: owner, themeProvider: assembly.appearance), animated: true)
        }
        navigation.pushViewController(page, animated: true)
    }

    private func startupPage() -> MeowfilmStartupViewController {
        let page = MeowfilmStartupViewController(themeProvider: assembly.appearance)
        page.isLoading = !assembly.startupFailed
        page.openSettings = { [weak self] in self?.showSettings() }
        page.didFinishAppearing = { [weak self] in self?.showPendingError() }
        return page
    }

    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) {
        guard let model = assembly.music else { return }
        if request.dailyPlatform != nil, let browser = model.publicBrowser {
            navigation.pushViewController(MusicCollectionViewController(
                model: model, recommendation: request, browser: browser,
                appearance: assembly.appearance, router: self
            ), animated: true)
            return
        }
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

    private func showFilm(_ item: FilmItem, kind: FilmPlaybackKind) {
        let item = FilmItem(sourceID: item.sourceID, id: item.id, title: item.title,
                            kind: FilmItemKind(rawValue: kind.rawValue) ?? item.kind,
                            posterURL: item.posterURL, largePosterURL: item.largePosterURL,
                            score: item.score, subtitle: item.subtitle)
        let model: FilmPlaybackModel
        do { model = try assembly.makeFilmPlayback(item: item, kind: kind) }
        catch {
            (navigation.topViewController as? PageViewController)?.showError(error.localizedDescription)
            return
        }
        let page = FilmDetailViewController(detail: FilmDetail(item: item), library: assembly.filmLibrary,
            images: assembly.filmImages, themeProvider: assembly.appearance, model: model)
        presentFilmDetail(page, model: model)
    }

    func showFilmSearchResult(_ context: FilmSearchContext) {
        do {
            let model = try assembly.makeFilmPlayback(context: context)
            let page = FilmDetailViewController(searchContext: context, library: assembly.filmLibrary,
                images: assembly.filmImages, themeProvider: assembly.appearance, model: model)
            presentFilmDetail(page, model: model)
        } catch {
            (navigation.topViewController as? PageViewController)?.showError(error.localizedDescription)
        }
    }

    private func presentFilmDetail(_ page: FilmDetailViewController, model: FilmPlaybackModel) {
        page.onSelectRelatedItem = { [weak self] in self?.showFilm($0, kind: $1) }
        page.onSelectItem = { [weak self] item in
            guard let kind = item.kind else { return }
            self?.showFilm(item, kind: kind == .movie ? .movie : .tv)
        }
        page.onActivatePlayback = { [weak self, weak page] in
            guard let self, let page, self.filmPlayer == nil else { return }
            self.activateFilmPage(page)
        }
        page.onPresentPlayback = { [weak self, weak model] in
            guard let self, let model,
                  (self.navigation.topViewController as? any FilmPlaybackPage)?.filmPlaybackModel === model else { return }
            self.presentFilmPlayer(model)
        }
        navigation.pushViewController(page, animated: true)
    }

    private func activateFilmPage(_ page: UIViewController) {
        filmNavigation?.cancel()
        let model = (page as? any FilmPlaybackPage)?.filmPlaybackModel
        filmNavigation = Task { [weak self, weak page] in
            guard let self, !Task.isCancelled else { return }
            do {
                if let model {
                    try await self.assembly.activateFilm(model)
                    try Task.checkCancellation()
                    await model.open()?.value
                } else {
                    try await self.assembly.leaveFilm()
                    try Task.checkCancellation()
                }
            } catch {
                guard !(error is CancellationError), !Task.isCancelled else { return }
                (page as? PageViewController)?.showError(error.localizedDescription)
            }
        }
    }

    @discardableResult
    private func presentFilmPlayer(_ model: FilmPlaybackModel, restoring: Bool = false,
                                   completion: (() -> Void)? = nil) -> Bool {
        let applicationState = UIApplication.shared.applicationState
        guard filmPlayer == nil, assembly.filmPlayback === model,
              root.presentedViewController == nil, !root.isBeingDismissed,
              applicationState == .active || restoring && applicationState != .background else { return false }
        let page = FilmPlayerViewController(model: model, themeProvider: assembly.appearance)
        filmPlayer = page
        page.onDismiss = { [weak self] in
            guard let self else { return }
            self.filmPlayer = nil
            if let top = self.navigation.topViewController { self.activateFilmPage(top) }
        }
        root.present(page, animated: true, completion: completion)
        return true
    }

    private func install(_ model: MusicViewModel) {
        guard main == nil, model.isAvailable, !model.isClosed else { return }
        let appearance = assembly.appearance
        let filmHome = FilmHomeModel(sources: assembly.filmSources)
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
                let page = FilmHomeViewController(model: filmHome,
                                                  images: self.assembly.filmImages,
                                                  themeProvider: appearance)
                page.onSelectPlaybackItem = { [weak self, weak page] item, category in
                    guard let category else {
                        page?.showError("缺少影片类型")
                        return
                    }
                    self?.showFilm(item, kind: category == .movie ? .movie : .tv)
                }
                page.onSelectCategory = { [weak self] model in
                    guard let self else { return }
                    let category = FilmCategoryViewController(model: model, images: self.assembly.filmImages,
                                                               themeProvider: appearance)
                    category.onSelectItem = { [weak self] in
                        self?.showFilm($0, kind: model.category == .movie ? .movie : .tv)
                    }
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
            openMenu: { [weak self] in
                guard let self, let main = self.main else { return }
                let content: MeowfilmSidebarContent = main.selectedPage == .film
                    ? .film(home: filmHome, settings: self.assembly.filmSettings)
                    : .music(source: MusicHomeSourceSettingsModel(model: model), selectedPage: main.selectedPage)
                self.root.showSidebar(content: content)
            },
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
        let retainedSettings = navigation.viewControllers.dropFirst()
        navigation.setViewControllers([page] + retainedSettings, animated: false)
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
        guard UIApplication.shared.applicationState == .active else { return }
        let top = presenter()
        let page = (top as? UINavigationController)?.topViewController ?? top
        guard let page = page as? PageViewController else { return }
        if assembly.startupFailed, !startupErrorAcknowledged {
            page.showError("本地数据不可用", acknowledged: { [weak self] in
                self?.startupErrorAcknowledged = true
            })
            return
        }
        guard let model = assembly.music, let error = model.error, presentedError != error else { return }
        if page.showError(error, acknowledged: { [weak self, weak model] in
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
    var isLoading = true { didSet { if isViewLoaded { updateLoading() } } }
    var openSettings: (() -> Void)?
    var didFinishAppearing: (() -> Void)?
    private let activity = UIActivityIndicatorView(style: .medium)

    override func viewDidLoad() {
        super.viewDidLoad()
        let settings = PageHeaderTextButton(title: "设置") { [weak self] in self?.openSettings?() }
        settings.accessibilityIdentifier = "startup.settings"
        pageHeader.setTrailing([settings])
        pageHeader.applyTheme(theme)
        activity.accessibilityIdentifier = "startup.loading"
        activity.accessibilityLabel = "加载中"
        activity.isAccessibilityElement = true
        activity.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(activity)
        NSLayoutConstraint.activate([
            activity.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            activity.centerYAnchor.constraint(equalTo: view.centerYAnchor),
        ])
        updateLoading()
    }

    override func applyTheme() {
        super.applyTheme()
        activity.color = UIColor(theme.muted)
    }

    override func pageDidFinishAppearing() {
        super.pageDidFinishAppearing()
        didFinishAppearing?()
    }

    private func updateLoading() {
        if isLoading { activity.startAnimating() }
        else { activity.stopAnimating() }
    }
}
