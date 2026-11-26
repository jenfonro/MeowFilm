import UIKit
import Combine
import MeowDebug
import MeowUI
import MeowMusicFeature
import MeowFilmFeature
import MeowNovelFeature

@MainActor
final class MeowfilmCoordinator: NSObject, MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    let window: UIWindow
    let assembly: MeowfilmAssembly
    let resetUserData: @MainActor () async throws -> Void
    let navigation: PageNavigationController
    let root: MeowfilmRootContainerViewController
    private var observations: Set<AnyCancellable> = []
    private(set) var main: MeowfilmPrimaryShellViewController?
    private(set) var surface: MusicPlayerSurface?
    private(set) var playerContent: MusicPlayerContentModel?
    private var pendingPresentation: (destination: MusicPlaybackDestination, occurrence: String?)?
    private weak var channels: MusicChannelsViewController?
    private var pendingChannel: MusicPublicBrowseChannel?
    private(set) weak var novelHome: NovelHomeViewController?
    private(set) weak var player: MusicPlayerViewController?
    private weak var queueSheet: UIViewController?
    private var startupErrorAcknowledged = false
    private var filmNavigation: Task<Void, Never>?
    private(set) weak var filmPlayer: FilmPlayerViewController?

    init(window: UIWindow, assembly: MeowfilmAssembly,
         resetUserData: @escaping @MainActor () async throws -> Void) {
        self.window = window
        self.assembly = assembly
        self.resetUserData = resetUserData
        navigation = PageNavigationController()
        root = MeowfilmRootContainerViewController(navigation: navigation, themeProvider: assembly.appearance)
        super.init()
    }

    func start() {
        navigation.setViewControllers([
            startupPage()
        ], animated: false)
        navigation.willShowPage = { [weak self] page, animated in
            guard let self else { return }
            self.applyChrome(for: page, animated: animated)
        }
        navigation.didShowPage = { [weak self] page in
            guard let self else { return }
            self.applyChrome(for: page, animated: false)
            let settingsVisible = self.isSettingsBranchVisible(including: page)
            if !settingsVisible, (page as? any FilmPlaybackPage)?.filmPlaybackModel == nil, self.filmPlayer == nil {
                self.activateFilmPage(page)
            }
            self.showPendingError()
            DebugRuntime.log(.uiNav, sub: "navigation", event: "did_show", data: .object([
                "page": .string(String(describing: type(of: page))),
                "stackDepth": .int(self.navigation.viewControllers.count),
                "settingsVisible": .bool(settingsVisible)
            ]))
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
        assembly.$novel.receive(on: RunLoop.main).sink { [weak self] feature in
            guard let self, let feature else { return }
            feature.web.present = { [weak self, rules = feature.rules] request in
                guard let self, UIApplication.shared.applicationState == .active else { throw CancellationError() }
                let page = NovelWebViewController(request: request, rules: rules, themeProvider: assembly.appearance)
                return try await page.result(from: presenter())
            }
        }.store(in: &observations)
        assembly.$startupFailed.receive(on: RunLoop.main).sink { [weak self] failed in
            guard let self, failed, self.main == nil else { return }
            (self.navigation.viewControllers.first as? MeowfilmStartupViewController)?.isLoading = false
            self.showPendingError()
        }.store(in: &observations)
        if let model = assembly.music { install(model) }
        assembly.restoreFilmPlaybackUI = { [weak self] sessionID in
            guard let self, let model = assembly.filmMediaPlayback, model.sessionID == sessionID,
                  UIApplication.shared.applicationState != .background else { return false }
            if let page = self.filmPlayer, page.filmPlaybackModel === model {
                return page.presentingViewController != nil && page.viewIfLoaded?.window != nil &&
                    !page.isBeingPresented && !page.isBeingDismissed
            }
            guard self.root.presentedViewController == nil else { return false }
            await model.open()?.value
            guard model.sessionID == sessionID, self.assembly.filmMediaPlayback === model else { return false }
            return await withCheckedContinuation { continuation in
                let accepted = self.presentFilmPlayer(model, restoring: true) { [weak self] in
                    continuation.resume(returning: self?.filmPlayer?.presentingViewController != nil)
                }
                if !accepted { continuation.resume(returning: false) }
            }
        }
    }

    private func chromePolicy(for page: UIViewController) -> PageChromePolicy {
        (page as? PageViewController)?.pageChromePolicy ?? .standard
    }

    private func isSettingsBranchVisible(including page: UIViewController? = nil) -> Bool {
        if let page, chromePolicy(for: page).isSettingsBranchRoot {
            return true
        }
        return navigation.viewControllers.contains {
            chromePolicy(for: $0).isSettingsBranchRoot
        }
    }

    private func applyChrome(for page: UIViewController, animated: Bool) {
        let policy = chromePolicy(for: page)
        let settingsBranch = isSettingsBranchVisible(including: page)
        let immersiveReading = policy.isImmersiveReading || navigation.viewControllers.contains {
            chromePolicy(for: $0).isImmersiveReading
        }
        let hidesDock = immersiveReading || policy.hidesDock
        let hidesMiniPlayer = settingsBranch || immersiveReading || policy.hidesMiniPlayer || policy.hidesDock
        let showsRootNavigation = page === main && !hidesDock
        root.setRootVisible(showsRootNavigation, animated: animated)
        root.setMiniPlayerHidden(hidesMiniPlayer)
        root.setDockHidden(hidesDock)
    }

    func becameActive() {
        Task { await assembly.appearance.reload() }
        if let pending = pendingPresentation, let model = assembly.music, !model.isClosed {
            pendingPresentation = nil
            if pending.occurrence == model.current?.id {
                showPlayer(pending.destination)
            }
        }
        showPendingError()
    }

    func showSearch() {
        showSearch(animated: true, initialTargetID: nil)
    }

    func showSearch(animated: Bool, initialTargetID: String? = nil) {
        guard let model = assembly.music, let service = assembly.filmSearch else { return }
        let category = MeowfilmSearchComposition.category(service: service, images: assembly.filmImages,
            themeProvider: assembly.appearance) { [weak self] in self?.showFilmSearchResult($0) }
        var categories = [category]
        if let novel = assembly.novel {
            categories.append(MeowfilmSearchComposition.novelCategory(feature: novel, themeProvider: assembly.appearance) { [weak self] in self?.showNovel($0) })
        }
        navigation.pushViewController(MusicSearchViewController(
            model: model, appearance: assembly.appearance, router: self,
            primaryTarget: MeowfilmSearchComposition.music, additionalCategories: categories,
            initialTargetID: initialTargetID ?? MeowfilmSearchComposition.defaultTarget(on: main?.selectedPage ?? .recommend)
        ), animated: animated)
    }

    func showSettings() {
        _ = showSettings(animated: true)
    }

    func makeDataManagementViewController() -> MeowfilmDataManagementViewController {
        MeowfilmDataManagementViewController(
            themeProvider: assembly.appearance,
            transferProvider: { [weak self] in
                guard let self, self.assembly.resources != nil, !self.assembly.closed else { return nil }
                return self.assembly.settingsTransfer
            },
            resetUserData: resetUserData
        )
    }

    func makeSettingsViewController() -> MeowfilmSettingsViewController {
        let page = MeowfilmSettingsViewController(themeProvider: assembly.appearance)
        page.openMusic = { [weak self, weak page] in
            guard let self else { return }
            guard let model = assembly.music else {
                page?.enqueueError("音乐设置暂不可用")
                return
            }
            navigation.pushViewController(MusicSettingsViewController(
                model: model, appearance: assembly.appearance, router: self
            ), animated: true)
        }
        page.openFilm = { [weak self, weak page] in
            guard let self else { return }
            guard let settings = assembly.filmSettings else {
                page?.enqueueError("影视设置暂不可用")
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
                page?.enqueueError("缓存管理暂不可用")
                return
            }
            navigation.pushViewController(FilmMetadataCacheSettingsViewController(
                owner: owner, themeProvider: assembly.appearance), animated: true)
        }
        page.openDataManagement = { [weak self] in
            guard let self else { return }
            navigation.pushViewController(makeDataManagementViewController(), animated: true)
        }
        page.openNovel = { [weak self, weak page] in
            guard let self else { return }
            guard let novel = assembly.novel else { page?.enqueueError("小说设置暂不可用"); return }
            navigation.pushViewController(NovelSettingsViewController(feature: novel, themeProvider: assembly.appearance), animated: true)
        }
        return page
    }

    @discardableResult
    func showSettings(animated: Bool) -> MeowfilmSettingsViewController {
        let page = makeSettingsViewController()
        navigation.pushViewController(page, animated: animated)
        return page
    }

    func showNovel(_ book: NovelBook, animated: Bool = true) {
        guard let novel = assembly.novel else { return }
        let page = NovelDetailViewController(book: book, feature: novel, themeProvider: assembly.appearance)
        page.onRead = { [weak self, weak novel] book in
            guard let self, let novel else { return }
            let session = novel.makeReader(book: book)
            navigation.pushViewController(NovelReaderViewController(session: session, themeProvider: assembly.appearance), animated: true)
        }
        navigation.pushViewController(page, animated: animated)
    }

    func showFilmPersonalSection(_ sectionKind: PersonalLibrarySectionKind, animated: Bool = true) {
        guard let history = assembly.filmHistory else { return }
        let category = FilmPersonalCategoryViewController(
            kind: sectionKind,
            library: assembly.filmLibrary,
            history: history,
            images: assembly.filmImages,
            themeProvider: assembly.appearance
        )
        category.onSelectItem = { [weak self] item in
            self?.showFilm(item, kind: item.kind == .movie ? .movie : .tv, entrySurface: "library")
        }
        navigation.pushViewController(category, animated: animated)
    }

    func showNovelPersonalSection(_ sectionKind: NovelPersonalSectionKind, animated: Bool = true) {
        guard let novel = assembly.novel else { return }
        let category = NovelPersonalCategoryViewController(
            kind: sectionKind,
            feature: novel,
            themeProvider: assembly.appearance
        )
        category.onSelectBook = { [weak self] book in self?.showNovel(book) }
        navigation.pushViewController(category, animated: animated)
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
        if request.dailyPlatform != nil, let service = model.service {
            navigation.pushViewController(MusicCollectionViewController(
                model: model, recommendation: request, service: service,
                appearance: assembly.appearance, router: self
            ), animated: true)
            return
        }
        navigation.pushViewController(MusicBrowseViewController(
            title: title, model: model, request: request,
            appearance: assembly.appearance, router: self
        ), animated: true)
    }

    func showPublicCollection(_ collection: MusicPublicBrowseCollection) {
        guard let model = assembly.music, let service = model.service else { return }
        navigation.pushViewController(MusicCollectionViewController(
            model: model,
            collection: collection,
            service: service,
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
        guard let model = assembly.music, !model.isClosed else { return }
        guard UIApplication.shared.applicationState == .active else {
            pendingPresentation = (destination, model.current?.id)
            return
        }
        if destination == .queue {
            let host = presenter()
            guard queueSheet == nil, !host.isBeingPresented, !host.isBeingDismissed,
                  host.viewIfLoaded?.window != nil else { return }
            let page = MusicQueueViewController(model: model, appearance: assembly.appearance)
            host.present(page, animated: true)
            if page.presentingViewController != nil || host.presentedViewController === page {
                queueSheet = page
            }
            return
        }
        if let player {
            if destination == .sources { player.requestSources() }
            return
        }
        guard let playerContent else { return }
        let host = presenter()
        guard !host.isBeingPresented, !host.isBeingDismissed, host.viewIfLoaded?.window != nil else { return }
        let page = MusicPlayerViewController(content: playerContent, appearance: assembly.appearance)
        page.onPresent = { [weak self] destination in self?.showPlayer(destination) }
        page.onDismiss = { [weak self] in
            self?.player = nil
            self?.primaryPlaybackPresentation.setFullPlayerPresented(false)
        }
        player = page
        primaryPlaybackPresentation.setFullPlayerPresented(true)
        primaryPlaybackPresentation.invalidate()
        host.present(page, animated: true) { [weak self, weak page] in
            if destination == .sources { page?.requestSources() }
            self?.showPendingError()
        }
        if page.presentingViewController == nil && host.presentedViewController !== page {
            player = nil
            primaryPlaybackPresentation.setFullPlayerPresented(false)
        }
    }

    func selectChannel(_ channel: MusicPublicBrowseChannel) {
        pendingChannel = channel
        navigation.popToRootViewController(animated: true)
        main?.select(.channels)
        if let channels {
            pendingChannel = nil
            channels.select(channel)
        }
    }

    func showFilm(_ item: FilmItem, kind: FilmPlaybackKind, entrySurface: String? = nil, animated: Bool = true) {
        let resolvedKind = FilmItemKind(rawValue: kind.rawValue) ?? item.kind
        let updatedEntry = item.entry.map { base in
            FilmEntry(
                targetId: base.targetId,
                keyword: base.keyword,
                title: base.title,
                subtitle: base.subtitle,
                poster: base.poster,
                backdrop: base.backdrop,
                score: base.score,
                textBadge: base.textBadge,
                siteLabel: base.siteLabel,
                kind: resolvedKind ?? base.kind,
                year: base.year,
                season: base.season,
                episode: base.episode,
                seasonHint: base.seasonHint,
                tmdb: base.tmdb,
                douban: base.douban,
                siteEntry: base.siteEntry
            )
        }
        let item = FilmItem(sourceID: item.sourceID, id: item.id, title: item.title,
                            kind: resolvedKind,
                            posterURL: item.posterURL, largePosterURL: item.largePosterURL,
                            score: item.score, subtitle: item.subtitle,
                            entry: updatedEntry)
        let model: FilmPlaybackModel
        do { model = try assembly.makeFilmPlayback(item: item, entrySurface: entrySurface) }
        catch {
            (navigation.topViewController as? PageViewController)?.enqueueError(error.localizedDescription)
            return
        }
        let page = FilmDetailViewController(detail: FilmDetail(item: item), library: assembly.filmLibrary,
            images: assembly.filmImages, themeProvider: assembly.appearance, model: model)
        presentFilmDetail(page, model: model, animated: animated)
    }

    func showFilmSearchResult(_ context: FilmSearchContext) {
        do {
            let model = try assembly.makeFilmPlayback(context: context)
            let page = FilmDetailViewController(searchContext: context, library: assembly.filmLibrary,
                images: assembly.filmImages, themeProvider: assembly.appearance, model: model)
            presentFilmDetail(page, model: model)
        } catch {
            (navigation.topViewController as? PageViewController)?.enqueueError(error.localizedDescription)
        }
    }

    private func presentFilmDetail(_ page: FilmDetailViewController, model: FilmPlaybackModel, animated: Bool = true) {
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
        navigation.pushViewController(page, animated: animated)
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
                (page as? PageViewController)?.enqueueError(error.localizedDescription)
            }
        }
    }

    @discardableResult
    func presentFilmPlayer(_ model: FilmPlaybackModel, restoring: Bool = false,
                                   completion: (() -> Void)? = nil) -> Bool {
        let applicationState = UIApplication.shared.applicationState
        guard filmPlayer == nil,
              restoring ? assembly.filmMediaPlayback === model : assembly.filmPlayback === model,
              root.presentedViewController == nil, !root.isBeingPresented, !root.isBeingDismissed,
              root.viewIfLoaded?.window != nil,
              applicationState == .active || restoring && applicationState != .background else { return false }
        let page = FilmPlayerViewController(model: model, themeProvider: assembly.appearance)
        filmPlayer = page
        page.onDismiss = { [weak self] in
            guard let self else { return }
            self.filmPlayer = nil
            if let top = self.navigation.topViewController { self.activateFilmPage(top) }
        }
        page.present(from: root, completion: completion)
        if page.presentingViewController == nil && root.presentedViewController !== page {
            filmPlayer = nil
            return false
        }
        return true
    }

    private func install(_ model: MusicViewModel) {
        guard main == nil, !model.isClosed else { return }
        let appearance = assembly.appearance
        let filmHome = FilmHomeModel(sources: assembly.filmSources)
        let factories: [@MainActor () -> UIViewController] = [
            { [weak self] in
                guard let self else { return UIViewController() }
                return MusicBrowseViewController(title: "推荐", model: model,
                    request: .discovery(.home), appearance: appearance, router: self, embedded: true)
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                let page = MusicChannelsViewController(model: model,
                    appearance: appearance, router: self)
                self.channels = page
                if let pending = self.pendingChannel {
                    self.pendingChannel = nil
                    page.select(pending)
                }
                return page
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                let page = FilmHomeViewController(model: filmHome,
                                                  images: self.assembly.filmImages,
                                                  themeProvider: appearance)
                page.onSelectPlaybackItem = { [weak self, weak page] item, category in
                    guard let category else {
                        page?.enqueueError("缺少影片类型")
                        return
                    }
                    self?.showFilm(item, kind: category == .movie ? .movie : .tv, entrySurface: "home")
                }
                page.onSelectCategory = { [weak self] model in
                    guard let self else { return }
                    let category = FilmCategoryViewController(model: model, images: self.assembly.filmImages,
                                                               themeProvider: appearance)
                    category.onSelectItem = { [weak self] in
                        self?.showFilm($0, kind: model.category == .movie ? .movie : .tv, entrySurface: "home")
                    }
                    self.navigation.pushViewController(category, animated: true)
                }
                return page
            },
            { [weak self] in
                guard let self, let novel = self.assembly.novel else { return UIViewController() }
                let page = NovelHomeViewController(feature: novel, themeProvider: appearance)
                page.onSelect = { [weak self] in self?.showNovel($0) }
                page.onSelectCategory = { [weak self] model in
                    guard let self, let novel = self.assembly.novel else { return }
                    let category = NovelCategoryViewController(
                        model: model,
                        feature: novel,
                        themeProvider: appearance
                    )
                    category.onSelectItem = { [weak self] in self?.showNovel($0) }
                    self.navigation.pushViewController(category, animated: true)
                }
                self.novelHome = page; return page
            },
            { [weak self] in
                guard let self else { return UIViewController() }
                guard let novel = self.assembly.novel,
                      let filmHistory = self.assembly.filmHistory else {
                    return MusicLibraryViewController(model: model, appearance: appearance, router: self)
                }
                let musicLib = MusicLibraryViewController(model: model, appearance: appearance, router: self)
                let filmLib = FilmPersonalLibraryViewController(
                    library: self.assembly.filmLibrary,
                    history: filmHistory,
                    images: self.assembly.filmImages,
                    themeProvider: appearance
                )
                filmLib.onSelectItem = { [weak self] item in
                    let kind: FilmPlaybackKind = item.kind == .movie ? .movie : .tv
                    self?.showFilm(item, kind: kind, entrySurface: "library")
                }
                filmLib.onSelectSection = { [weak self] sectionKind in
                    self?.showFilmPersonalSection(sectionKind)
                }
                let novelLib = NovelPersonalLibraryViewController(feature: novel, themeProvider: appearance)
                novelLib.onSelectBook = { [weak self] book in self?.showNovel(book) }
                novelLib.onSelectSection = { [weak self] sectionKind in
                    self?.showNovelPersonalSection(sectionKind)
                }
                return MeowfilmLibraryViewController(
                    musicPage: musicLib,
                    filmPage: filmLib,
                    novelPage: novelLib,
                    themeProvider: appearance
                )
            }
        ]
        let page = MeowfilmPrimaryShellViewController(
            pageFactories: factories, themeProvider: appearance,
            openMenu: { [weak self] in
                guard let self, let main = self.main else { return }
                let content: MeowfilmSidebarContent
                switch main.selectedPage {
                case .film: content = .film(settings: self.assembly.filmSettings)
                case .novel:
                    guard let home = self.novelHome else { return }
                    content = .novel(discovery: home.discovery)
                case .library:
                    let activeTab = (main.contentViewController(for: .library) as? MeowfilmLibraryViewController)?.selectedTab ?? .music
                    switch activeTab {
                    case .music:
                        content = .music(selectedPage: .library)
                    case .film:
                        content = .film(settings: self.assembly.filmSettings)
                    case .novel:
                        if let home = self.novelHome {
                            content = .novel(discovery: home.discovery)
                        } else if let novelPage = main.contentViewController(for: .novel) as? NovelHomeViewController {
                            self.novelHome = novelPage
                            content = .novel(discovery: novelPage.discovery)
                        } else {
                            content = .music(selectedPage: .library)
                        }
                    }
                default: content = .music(selectedPage: main.selectedPage)
                }
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
        model.noticeChanges.receive(on: RunLoop.main).sink { [weak self] in
            self?.showPendingError()
        }.store(in: &observations)
        model.onRequestPresentation = { [weak self, weak model] destination in
            guard let self, let model, self.assembly.music === model else { return }
            self.showPlayer(destination)
        }
    }

    private func showPendingError() {
        guard UIApplication.shared.applicationState == .active,
              let page = topAlertHost() else { return }
        if assembly.startupFailed, !startupErrorAcknowledged {
            page.enqueueError("本地数据不可用", acknowledged: { [weak self] in
                self?.startupErrorAcknowledged = true
            })
            return
        }
        guard let model = assembly.music else { return }
        let error = model.notices.error
        page.syncErrorSource("music.notice", message: error, acknowledged: { [weak model] in
            if model?.notices.error == error { model?.notices.error = nil }
        })
    }

    private func topAlertHost() -> PageViewController? {
        let top = presenter()
        if let nav = top as? UINavigationController, let page = nav.topViewController as? PageViewController {
            return page
        }
        if top === root, let page = navigation.topViewController as? PageViewController {
            return page
        }
        return top as? PageViewController
    }

    func presenter() -> UIViewController {
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
