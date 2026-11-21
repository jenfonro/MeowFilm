import UIKit
import MeowContracts
import MeowDebug
import MeowUI
import MeowPlayback
import MeowMusicFeature
import MeowFilmFeature
import MeowNovelFeature

@MainActor
final class MeowfilmDebugNavigator {
    private let runtime: DebugRuntime
    private weak var appDelegate: MeowfilmAppDelegate?
    private weak var window: UIWindow?
    private weak var coordinator: MeowfilmCoordinator?

    init(runtime: DebugRuntime, appDelegate: MeowfilmAppDelegate) {
        self.runtime = runtime
        self.appDelegate = appDelegate
    }

    func attach(window: UIWindow?, coordinator: MeowfilmCoordinator?) {
        self.window = window
        self.coordinator = coordinator
    }

    var assembly: MeowfilmAssembly? { appDelegate?.assembly }
    var isWindowReady: Bool { window != nil && coordinator != nil }

    // MARK: - Active Page Resolution

    private func requireCoordinator() throws -> MeowfilmCoordinator {
        guard let coordinator else {
            throw DebugAPIError.invalidState("Application coordinator is not ready yet")
        }
        return coordinator
    }

    private func presentedModalChain() -> [UIViewController] {
        guard let root = coordinator?.root else { return [] }
        var chain: [UIViewController] = []
        var cur = root.presentedViewController
        while let next = cur {
            chain.append(next)
            cur = next.presentedViewController
        }
        return chain
    }

    private func activeDialogController() -> UIViewController? {
        for vc in presentedModalChain().reversed() {
            if vc is UIAlertController || vc is ManagementDialog {
                return vc
            }
        }
        return nil
    }

    private func unwrapInspectable(_ vc: UIViewController?) -> UIViewController? {
        guard let vc else { return nil }
        if let nav = vc as? UINavigationController {
            return unwrapInspectable(nav.topViewController)
        }
        if let primary = vc as? MeowfilmPrimaryShellViewController {
            return primary.activeContentViewController ?? primary
        }
        return vc
    }

    private func activeBusinessController() -> UIViewController? {
        let modals = presentedModalChain()
        for modal in modals.reversed() {
            if modal is UIAlertController || modal is ManagementDialog {
                continue
            }
            if let unwrapped = unwrapInspectable(modal) {
                return unwrapped
            }
        }
        if let navTop = coordinator?.navigation.topViewController {
            return unwrapInspectable(navTop)
        }
        return window?.rootViewController
    }

    private func statusFor(_ vc: UIViewController?) -> DebugPageStatus {
        guard let vc else {
            return DebugPageStatus(target: "startup", moduleId: "app", route: "startup", title: "启动中", isBusy: true)
        }
        if let inspectable = vc as? any DebugPageInspectable {
            return inspectable.debugPageStatus
        }
        let typeName = String(describing: type(of: vc))
        let busy = (vc as? MeowfilmLaunchViewController) != nil || typeName.contains("Startup")
        return DebugPageStatus(
            target: busy ? "startup" : "unknown",
            moduleId: "app",
            route: typeName,
            title: vc.title ?? (vc as? PageViewController)?.pageTitle ?? typeName,
            isBusy: busy
        )
    }

    private func dismissAllModalsAndSidebar() async {
        guard let coordinator else { return }
        coordinator.root.dismissSidebarImmediately()
        coordinator.surface?.collapse()
        guard coordinator.root.presentedViewController != nil else { return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            coordinator.root.dismiss(animated: false) {
                continuation.resume()
            }
        }
    }

    private func presentModal(_ vc: UIViewController, from presenter: UIViewController) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            presenter.present(vc, animated: false) {
                continuation.resume()
            }
        }
    }

    private func resetToPrimaryRoot(_ page: MeowfilmPrimaryPage) async throws -> MeowfilmPrimaryShellViewController {
        let coordinator = try requireCoordinator()
        await dismissAllModalsAndSidebar()
        guard let main = coordinator.main else {
            throw DebugAPIError.invalidState("Primary shell is not initialized yet (music service still loading)")
        }
        if coordinator.navigation.viewControllers.count > 1 || coordinator.navigation.topViewController !== main {
            coordinator.navigation.setViewControllers([main], animated: false)
        }
        main.select(page, animated: false)
        coordinator.root.select(page, duration: 0)
        return main
    }

    // MARK: - Navigation (nav/open, nav/back, nav/refresh)

    func navOpen(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let target = body["target"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
              !target.isEmpty else {
            throw DebugAPIError.invalidArgument("Missing required string field 'target'")
        }
        let params = body["params"]?.objectValue ?? [:]
        let waitIdle = body["waitIdle"]?.boolValue ?? false
        let timeoutMs = body["timeoutMs"]?.intValue ?? 15000

        DebugRuntime.log(.uiNav, sub: "debug.nav", event: "open.start", data: .object([
            "target": .string(target),
            "params": .object(params)
        ]))

        switch target {
        // --- Unsupported on iOS ---
        case "novel.shelf":
            throw DebugAPIError.unsupported("iOS has no standalone novel shelf page; query ios_novel_books via storage/query or use novel/command toggle_shelf")
        case "settings.media-controls":
            throw DebugAPIError.unsupported("settings.media-controls is Windows-only")

        // --- Music Targets ---
        case "music.home":
            _ = try await resetToPrimaryRoot(.recommend)

        case "music.channels":
            let main = try await resetToPrimaryRoot(.channels)
            if let channelsVC = main.contentViewController(for: .channels) as? MusicChannelsViewController {
                let key = params["channelKey"]?.stringValue
                let idx = params["channelIndex"]?.intValue
                if key != nil || idx != nil {
                    try channelsVC.debugSelectChannel(key: key, index: idx)
                }
            }

        case "music.discovery":
            let coordinator = try requireCoordinator()
            _ = try await resetToPrimaryRoot(.recommend)
            coordinator.showSearch(animated: false, initialTargetID: MeowfilmSearchComposition.music.id)

        case "music.personal":
            _ = try await resetToPrimaryRoot(.library)

        case "music.daily":
            let coordinator = try requireCoordinator()
            guard let music = assembly?.music, let browser = music.publicBrowser else {
                throw DebugAPIError.invalidState("Music public browser is not available")
            }
            let platform = resolveAccountPlatform(params["platform"]?.stringValue, music: music)
            _ = try await resetToPrimaryRoot(.recommend)
            let page = MusicCollectionViewController(
                model: music,
                recommendation: .discovery(.daily(platform)),
                browser: browser,
                appearance: coordinator.assembly.appearance,
                router: coordinator
            )
            coordinator.navigation.pushViewController(page, animated: false)

        case "music.fm":
            let coordinator = try requireCoordinator()
            guard let music = assembly?.music, let playback = assembly?.playback else {
                throw DebugAPIError.invalidState("Music service is not ready")
            }
            if params["settings"]?.boolValue == true {
                try await openSettings(domain: "music", section: "playback")
            } else {
                if music.current == nil {
                    _ = try await music.debugPerformPlaybackCommand("start_fm", params: params, playback: playback)
                }
                if coordinator.player == nil {
                    try await openMusicPlayerModal(expanded: true)
                }
            }

        case "music.library":
            _ = try await resetToPrimaryRoot(.library)

        case "music.favorites":
            let coordinator = try requireCoordinator()
            guard let music = assembly?.music else {
                throw DebugAPIError.invalidState("Music service is not ready")
            }
            _ = try await resetToPrimaryRoot(.library)
            if let fav = music.collections.first(where: { $0.kind == "favorites" }) {
                let page = MusicCollectionViewController(
                    model: music,
                    collectionID: fav.id,
                    appearance: coordinator.assembly.appearance,
                    router: coordinator
                )
                coordinator.navigation.pushViewController(page, animated: false)
            }

        case "music.history":
            let queueVC = try await openMusicQueueModal(open: true)
            queueVC?.debugSelectTab("songs")

        case "music.archives":
            let queueVC = try await openMusicQueueModal(open: true)
            queueVC?.debugSelectTab("playlists")

        case "music.playlist":
            let coordinator = try requireCoordinator()
            guard let music = assembly?.music else {
                throw DebugAPIError.invalidState("Music service is not ready")
            }
            _ = try await resetToPrimaryRoot(.library)
            if let id = params["id"]?.stringValue, !id.isEmpty {
                guard let col = music.collections.first(where: { $0.id == id }) else {
                    throw DebugAPIError.notFound("Local playlist not found: \(id)")
                }
                let page = MusicCollectionViewController(
                    model: music,
                    collectionID: col.id,
                    appearance: coordinator.assembly.appearance,
                    router: coordinator
                )
                coordinator.navigation.pushViewController(page, animated: false)
            } else if let platRaw = params["platform"]?.stringValue,
                      let remoteId = params["remoteId"]?.stringValue,
                      !remoteId.isEmpty {
                guard let browser = music.publicBrowser else {
                    throw DebugAPIError.invalidState("Music public browser is not available")
                }
                guard let platform = parseBrowsePlatform(platRaw) else {
                    throw DebugAPIError.invalidArgument("Unknown music platform: \(platRaw)")
                }
                let kindRaw = params["kind"]?.stringValue ?? "playlist"
                let kind: MusicPublicBrowseCollection.Kind = (kindRaw == "album") ? .album : .playlist
                let title = params["title"]?.stringValue ?? remoteId
                let col = MusicPublicBrowseCollection(platform: platform, nativeID: remoteId, kind: kind, title: title)
                let page = MusicCollectionViewController(
                    model: music,
                    collection: col,
                    browser: browser,
                    appearance: coordinator.assembly.appearance,
                    router: coordinator
                )
                coordinator.navigation.pushViewController(page, animated: false)
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or ('platform' + 'remoteId') for music.playlist")
            }

        case "music.player":
            let expanded = params["expanded"]?.boolValue ?? true
            try await openMusicPlayerModal(expanded: expanded)

        case "music.queue":
            let open = params["open"]?.boolValue ?? true
            _ = try await openMusicQueueModal(open: open)

        // --- Film Targets ---
        case "film.home":
            _ = try await resetToPrimaryRoot(.film)

        case "film.data":
            let coordinator = try requireCoordinator()
            if params["directory"]?.boolValue == true,
               let detailVC = activeBusinessController() as? FilmDetailViewController {
                try detailVC.debugOpenDirectory()
            } else {
                let main = try await resetToPrimaryRoot(.film)
                guard let filmHome = main.contentViewController(for: .film) as? FilmHomeViewController else {
                    throw DebugAPIError.invalidState("FilmHomeViewController is unavailable")
                }
                guard let catModel = filmHome.debugCategoryModel(category: params["category"]?.stringValue) else {
                    throw DebugAPIError.notFound("Film category not available: \(params["category"]?.stringValue ?? "default")")
                }
                let catVC = FilmCategoryViewController(
                    model: catModel,
                    images: coordinator.assembly.filmImages,
                    themeProvider: coordinator.assembly.appearance
                )
                catVC.onSelectItem = { [weak coordinator] item in
                    coordinator?.showFilm(item, kind: catModel.category == .movie ? .movie : .tv, entrySurface: "home")
                }
                coordinator.navigation.pushViewController(catVC, animated: false)
            }

        case "film.detail":
            let coordinator = try requireCoordinator()
            if params["id"] == nil, activeBusinessController() is FilmDetailViewController {
                break
            }
            guard let id = params["id"]?.stringValue, !id.isEmpty else {
                throw DebugAPIError.invalidState("No active FilmDetailViewController; provide 'id' to open one")
            }
            let main = try await resetToPrimaryRoot(.film)
            guard let filmHome = main.contentViewController(for: .film) as? FilmHomeViewController,
                  let found = filmHome.debugFindItem(id: id) else {
                throw DebugAPIError.notFound("Film item not found in loaded home catalog: \(id)")
            }
            let kind: FilmPlaybackKind = (found.category == .movie || found.item.kind == .movie) ? .movie : .tv
            coordinator.showFilm(found.item, kind: kind, entrySurface: "home", animated: false)

        case "film.playback":
            let coordinator = try requireCoordinator()
            if coordinator.filmPlayer != nil { break }
            guard let model = coordinator.assembly.filmMediaPlayback ?? coordinator.assembly.filmPlayback else {
                throw DebugAPIError.invalidState("No active film playback session")
            }
            await dismissAllModalsAndSidebar()
            let ok = coordinator.presentFilmPlayer(model)
            if !ok {
                throw DebugAPIError.invalidState("Could not present FilmPlayerViewController in current state")
            }

        // --- Novel Targets ---
        case "novel.home", "novel.categories":
            let main = try await resetToPrimaryRoot(.novel)
            if let novelHome = main.contentViewController(for: .novel) as? NovelHomeViewController {
                novelHome.debugApplyFilter(
                    sourceId: params["sourceId"]?.stringValue,
                    channelIndex: params["channelIndex"]?.intValue,
                    kindUrl: params["kindUrl"]?.stringValue,
                    kindIndex: params["kindIndex"]?.intValue
                )
            }

        case "novel.book":
            let coordinator = try requireCoordinator()
            if params["bookId"] == nil, activeBusinessController() is NovelDetailViewController {
                break
            }
            guard let bookId = params["bookId"]?.stringValue, !bookId.isEmpty else {
                throw DebugAPIError.invalidArgument("Provide 'bookId' to open novel.book")
            }
            guard let book = findNovelBook(bookId: bookId) else {
                throw DebugAPIError.notFound("Novel book not found: \(bookId)")
            }
            _ = try await resetToPrimaryRoot(.novel)
            coordinator.showNovel(book, animated: false)

        case "novel.reader":
            let coordinator = try requireCoordinator()
            guard let novel = coordinator.assembly.novel else {
                throw DebugAPIError.invalidState("Novel feature is not initialized")
            }
            if params["bookId"] == nil, activeBusinessController() is NovelReaderViewController {
                break
            }
            let book: NovelBook
            if let bookId = params["bookId"]?.stringValue, !bookId.isEmpty {
                guard let found = findNovelBook(bookId: bookId) else {
                    throw DebugAPIError.notFound("Novel book not found: \(bookId)")
                }
                book = found
            } else if let detailVC = activeBusinessController() as? NovelDetailViewController {
                book = detailVC.debugBook
            } else if let latest = novel.local.entries.first?.book {
                book = latest
            } else {
                throw DebugAPIError.invalidState("No book available for novel.reader; provide 'bookId'")
            }
            await dismissAllModalsAndSidebar()
            let session = novel.makeReader(book: book)
            coordinator.navigation.pushViewController(
                NovelReaderViewController(session: session, themeProvider: coordinator.assembly.appearance),
                animated: false
            )

        // --- Search ---
        case "search":
            let coordinator = try requireCoordinator()
            await dismissAllModalsAndSidebar()
            let targetID = params["target"]?.stringValue
            let query = params["query"]?.stringValue
            let tab = params["tab"]?.stringValue

            var searchVC = coordinator.navigation.topViewController as? MusicSearchViewController
            if searchVC == nil {
                _ = try await resetToPrimaryRoot(.recommend)
                coordinator.showSearch(animated: false, initialTargetID: targetID)
                searchVC = coordinator.navigation.topViewController as? MusicSearchViewController
            }
            if let query, !query.isEmpty {
                searchVC?.debugSubmitImmediate(query: query, targetID: targetID)
                if let tab, let resultsVC = coordinator.navigation.topViewController as? MusicSearchResultsViewController {
                    _ = resultsVC.debugSelectTab(tab)
                }
            } else if let targetID, let searchVC {
                searchVC.searchTargetID = targetID
            }

        // --- Settings ---
        case "settings.general":
            try await openSettings(domain: "general", section: params["section"]?.stringValue)
        case "settings.music":
            try await openSettings(domain: "music", section: params["section"]?.stringValue)
        case "settings.film":
            try await openSettings(domain: "film", section: params["section"]?.stringValue)
        case "settings.novel":
            try await openSettings(domain: "novel", section: params["section"]?.stringValue)
        case "settings.cache":
            try await openSettings(domain: "general", section: "cache")

        default:
            throw DebugAPIError.notFound("Unknown navigation target: \(target)")
        }

        if waitIdle {
            _ = try await uiWait(["condition": .string("idle"), "timeoutMs": .int(timeoutMs)])
        }

        let state = uiState()
        DebugRuntime.log(.uiNav, sub: "debug.nav", event: "open.end", data: .object([
            "target": .string(target),
            "resolvedTarget": state["target"] ?? .null,
            "route": state["route"] ?? .null
        ]))
        return state
    }

    func navBack() async throws -> DebugJSONValue {
        let coordinator = try requireCoordinator()
        if coordinator.root.isSidebarVisible {
            coordinator.root.dismissSidebarImmediately()
            return uiState()
        }
        let modals = presentedModalChain()
        if let topModal = modals.last {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                topModal.dismiss(animated: false) {
                    continuation.resume()
                }
            }
            DebugRuntime.log(.uiNav, sub: "debug.nav", event: "back.dismiss_modal", data: .object([
                "modal": .string(String(describing: type(of: topModal)))
            ]))
            return uiState()
        }
        if coordinator.navigation.viewControllers.count > 1 {
            coordinator.navigation.popViewController(animated: false)
            DebugRuntime.log(.uiNav, sub: "debug.nav", event: "back.pop", data: .object([
                "stackDepth": .int(coordinator.navigation.viewControllers.count)
            ]))
        }
        return uiState()
    }

    func navRefresh() async throws -> DebugJSONValue {
        let active = activeBusinessController()
        if let refreshable = active as? any DebugPageRefreshable {
            refreshable.debugRefresh()
            DebugRuntime.log(.uiNav, sub: "debug.nav", event: "refresh", data: .object([
                "page": .string(String(describing: type(of: refreshable)))
            ]))
            return .object(["refreshed": .bool(true), "state": uiState()])
        }
        return .object(["refreshed": .bool(false), "state": uiState()])
    }

    // MARK: - Settings & Modal Helpers

    private func openSettings(domain: String, section: String?) async throws {
        if domain == "general", let section, ["window", "shortcuts"].contains(section) {
            throw DebugAPIError.unsupported("settings.general section '\(section)' is Windows-only")
        }
        let coordinator = try requireCoordinator()
        await dismissAllModalsAndSidebar()
        if let main = coordinator.main {
            coordinator.navigation.setViewControllers([main], animated: false)
        }
        let settingsRoot = coordinator.showSettings(animated: false)
        _ = settingsRoot

        switch domain {
        case "general":
            switch section {
            case nil, "", "root", "sources":
                break
            case "appearance":
                let vc = MusicAppearanceSettingsViewController(store: coordinator.assembly.appearance)
                coordinator.navigation.pushViewController(vc, animated: false)
            case "cache":
                guard let owner = coordinator.assembly.filmSettings?.playback else {
                    throw DebugAPIError.invalidState("Film cache settings owner is not ready")
                }
                let vc = FilmMetadataCacheSettingsViewController(owner: owner, themeProvider: coordinator.assembly.appearance)
                coordinator.navigation.pushViewController(vc, animated: false)
            default:
                throw DebugAPIError.notFound("Unknown general settings section: \(section ?? "")")
            }

        case "music":
            guard let model = coordinator.assembly.music else {
                throw DebugAPIError.invalidState("Music settings unavailable")
            }
            let vc = MusicSettingsViewController(model: model, appearance: coordinator.assembly.appearance, router: coordinator)
            coordinator.navigation.pushViewController(vc, animated: false)
            if let section, !section.isEmpty, section != "root" {
                if section == "cache" {
                    guard let owner = coordinator.assembly.filmSettings?.playback else {
                        throw DebugAPIError.invalidState("Cache settings owner is not ready")
                    }
                    coordinator.navigation.pushViewController(
                        FilmMetadataCacheSettingsViewController(owner: owner, themeProvider: coordinator.assembly.appearance),
                        animated: false
                    )
                } else {
                    try vc.debugOpenSection(section)
                }
            }

        case "film":
            guard let model = coordinator.assembly.filmSettings else {
                throw DebugAPIError.invalidState("Film settings unavailable")
            }
            let vc = FilmSettingsViewController(model: model, themeProvider: coordinator.assembly.appearance)
            coordinator.navigation.pushViewController(vc, animated: false)
            if let section, !section.isEmpty, section != "root" {
                try vc.debugOpenSection(section)
            }

        case "novel":
            guard let novel = coordinator.assembly.novel else {
                throw DebugAPIError.invalidState("Novel settings unavailable")
            }
            let vc = NovelSettingsViewController(feature: novel, themeProvider: coordinator.assembly.appearance)
            coordinator.navigation.pushViewController(vc, animated: false)
            if let section, !section.isEmpty, section != "root" {
                try vc.debugOpenSection(section)
            }

        default:
            throw DebugAPIError.invalidArgument("Unknown settings domain: \(domain)")
        }
    }

    private func openMusicPlayerModal(expanded: Bool) async throws {
        let coordinator = try requireCoordinator()
        if !expanded {
            if let player = coordinator.player {
                await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                    player.dismiss(animated: false) { continuation.resume() }
                }
            }
            return
        }
        guard let music = coordinator.assembly.music, music.isAvailable, !music.isClosed else {
            throw DebugAPIError.invalidState("Music service is unavailable")
        }
        guard music.current != nil else {
            throw DebugAPIError.invalidState("No active music track to display in player")
        }
        if coordinator.player != nil { return }
        await dismissAllModalsAndSidebar()
        coordinator.showPlayer(.player)
        try? await Task.sleep(nanoseconds: 80_000_000)
    }

    @discardableResult
    private func openMusicQueueModal(open: Bool) async throws -> MusicQueueViewController? {
        let coordinator = try requireCoordinator()
        if !open {
            if let queueVC = presentedModalChain().compactMap({ $0 as? MusicQueueViewController }).last {
                await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                    queueVC.dismiss(animated: false) { continuation.resume() }
                }
            }
            return nil
        }
        guard let music = coordinator.assembly.music, music.isAvailable, !music.isClosed else {
            throw DebugAPIError.invalidState("Music service is unavailable")
        }
        if let existing = presentedModalChain().compactMap({ $0 as? MusicQueueViewController }).last {
            return existing
        }
        await dismissAllModalsAndSidebar()
        let queueVC = MusicQueueViewController(model: music, appearance: coordinator.assembly.appearance)
        await presentModal(queueVC, from: coordinator.presenter())
        return queueVC
    }

    // MARK: - UI State, Content, Tree, Screenshot

    func uiState() -> DebugJSONValue {
        let activeVC = activeBusinessController()
        let status = statusFor(activeVC)

        var navStack: [DebugJSONValue] = []
        if let vcs = coordinator?.navigation.viewControllers {
            for vc in vcs {
                let s = statusFor(unwrapInspectable(vc))
                navStack.append(.object([
                    "target": .string(s.target),
                    "route": .string(s.route),
                    "title": .string(s.title)
                ]))
            }
        }
        for modal in presentedModalChain() where !(modal is UIAlertController) && !(modal is ManagementDialog) {
            let s = statusFor(unwrapInspectable(modal))
            navStack.append(.object([
                "target": .string(s.target),
                "route": .string(s.route),
                "title": .string(s.title)
            ]))
        }

        let settingsOpen = (coordinator?.navigation.viewControllers.contains { $0 is MeowfilmSettingsViewController } ?? false)
            || status.settingsDomain != nil

        let canGoBack = (coordinator?.root.isSidebarVisible == true)
            || !presentedModalChain().isEmpty
            || ((coordinator?.navigation.viewControllers.count ?? 0) > 1)

        let activeDialogJSON: DebugJSONValue
        if let dialog = activeDialogController() {
            activeDialogJSON = describeDialog(dialog)
        } else {
            activeDialogJSON = .null
        }

        var notices: [DebugJSONValue] = []
        if let music = assembly?.music {
            if let err = music.notices.error {
                notices.append(.object([
                    "id": .string("music.error"),
                    "level": .string("error"),
                    "message": .string(err)
                ]))
            }
            for receipt in music.settingsReceipts.unknown {
                notices.append(.object([
                    "id": .string(receipt.id.uuidString),
                    "level": .string("warn"),
                    "message": .string(receipt.message)
                ]))
            }
        }
        if assembly?.startupFailed == true {
            notices.append(.object([
                "id": .string("app.startupFailed"),
                "level": .string("error"),
                "message": .string("本地数据不可用")
            ]))
        }
        if let shutdownErr = assembly?.shutdownError {
            notices.append(.object([
                "id": .string("app.shutdownError"),
                "level": .string("error"),
                "message": .string(shutdownErr)
            ]))
        }

        let bounds = window?.bounds ?? .zero
        let scale = window?.screen.scale ?? UIScreen.main.scale
        let windowJSON: DebugJSONValue = .object([
            "visible": .bool(window != nil && window?.isHidden == false),
            "active": .bool(UIApplication.shared.applicationState == .active),
            "width": .number(Double(bounds.width)),
            "height": .number(Double(bounds.height)),
            "scale": .number(Double(scale))
        ])

        return .object([
            "target": .string(status.target),
            "moduleId": .string(status.moduleId),
            "route": .string(status.route),
            "title": .string(status.title),
            "settingsOpen": .bool(settingsOpen),
            "settingsDomain": .optional(status.settingsDomain),
            "settingsSection": .optional(status.settingsSection),
            "navigationStack": .array(navStack),
            "canGoBack": .bool(canGoBack),
            "canGoForward": .bool(false),
            "isBusy": .bool(status.isBusy),
            "statusText": .optional(status.statusText),
            "isError": .bool(status.isError),
            "errorText": .optional(status.errorText),
            "activeDialog": activeDialogJSON,
            "notices": .array(notices),
            "window": windowJSON
        ])
    }

    func uiContent(maxItems: Int, includeRaw: Bool) -> DebugJSONValue {
        let activeVC = activeBusinessController()
        if let inspectable = activeVC as? any DebugPageInspectable {
            return inspectable.debugContent(maxItems: maxItems, includeRaw: includeRaw).toJSON()
        }
        let status = statusFor(activeVC)
        return DebugContentSnapshot(
            target: status.target,
            kind: "empty",
            loading: status.isBusy,
            error: status.errorText
        ).toJSON()
    }

    func uiTree(maxDepth: Int, visibleOnly: Bool) -> DebugJSONValue {
        guard let window else {
            return .object(["error": .string("window not attached"), "children": .array([])])
        }
        let clampedDepth = min(max(maxDepth, 1), 24)
        var vcMap: [ObjectIdentifier: UIViewController] = [:]
        if let root = window.rootViewController {
            indexViewControllers(root, into: &vcMap)
        }
        return serializeView(window, depth: 0, maxDepth: clampedDepth, visibleOnly: visibleOnly, window: window, vcMap: vcMap)
    }

    func uiScreenshot(save: Bool) throws -> DebugBinaryResponse {
        guard let window else {
            throw DebugAPIError.invalidState("UIWindow is not ready for screenshot")
        }
        let bounds = window.bounds
        guard bounds.width > 0, bounds.height > 0 else {
            throw DebugAPIError.invalidState("UIWindow has zero bounds")
        }
        let format = UIGraphicsImageRendererFormat()
        format.scale = window.screen.scale
        format.opaque = false
        let renderer = UIGraphicsImageRenderer(bounds: bounds, format: format)
        let image = renderer.image { ctx in
            if !window.drawHierarchy(in: bounds, afterScreenUpdates: true) {
                window.layer.render(in: ctx.cgContext)
            }
        }
        guard let png = image.pngData() else {
            throw DebugAPIError.internalError("Failed to encode window screenshot to PNG")
        }
        var headers: [String: String] = [:]
        if save {
            let saved = try runtime.session.saveScreenshotPNG(png)
            headers["X-Meow-Screenshot-Path"] = saved.relativePath
            DebugRuntime.log(.uiAction, sub: "screenshot", event: "saved", data: .object([
                "path": .string(saved.relativePath),
                "relativePath": .string(saved.relativePath),
                "bytes": .int(png.count),
                "width": .int(Int(bounds.width * format.scale)),
                "height": .int(Int(bounds.height * format.scale))
            ]))
        }
        return DebugBinaryResponse(data: png, contentType: "image/png", headers: headers)
    }

    // MARK: - UI Actions

    func uiAction(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let action = body["action"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
              !action.isEmpty else {
            throw DebugAPIError.invalidArgument("Missing required string field 'action'")
        }
        let params = body["params"]?.objectValue ?? [:]
        let waitIdle = body["waitIdle"]?.boolValue ?? false
        let timeoutMs = body["timeoutMs"]?.intValue ?? 15000

        DebugRuntime.log(.uiAction, sub: "debug.action", event: action, data: .object(params))

        let result: DebugJSONValue
        switch action {
        case "dialog.action":
            result = try await performDialogAction(params)

        case "search.submit":
            guard let query = params["query"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !query.isEmpty else {
                throw DebugAPIError.invalidArgument("Missing non-empty 'query' for search.submit")
            }
            var navParams: [String: DebugJSONValue] = ["query": .string(query)]
            if let t = params["target"] { navParams["target"] = t }
            if let tab = params["tab"] { navParams["tab"] = tab }
            result = try await navOpen(["target": .string("search"), "params": .object(navParams)])

        case "setting.set", "setting.action":
            if let active = activeBusinessController() as? any DebugPageActionable {
                do {
                    result = try await active.debugPerformAction(DebugActionRequest(action: action, params: params))
                } catch let err as DebugAPIError where err.code == .invalidArgument {
                    if let fallback = try await performGlobalSettingAction(action: action, params: params) {
                        result = fallback
                    } else {
                        throw err
                    }
                }
            } else if let fallback = try await performGlobalSettingAction(action: action, params: params) {
                result = fallback
            } else {
                throw DebugAPIError.invalidState("Current page does not support '\(action)'")
            }

        default:
            guard let active = activeBusinessController() as? any DebugPageActionable else {
                throw DebugAPIError.invalidState("Current page does not support ui/action '\(action)'")
            }
            result = try await active.debugPerformAction(DebugActionRequest(action: action, params: params))
        }

        if waitIdle {
            _ = try await uiWait(["condition": .string("idle"), "timeoutMs": .int(timeoutMs)])
        }

        return .object([
            "action": .string(action),
            "result": result,
            "state": uiState()
        ])
    }

    private func performDialogAction(_ params: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        let dialogAction = params["action"]?.stringValue ?? "confirm"
        if let alert = activeDialogController() as? UIAlertController {
            let registered = DebugAlertActionRegistry.shared.actions(for: alert) ?? []
            let chosen: DebugAlertActionRegistry.RegisteredAction?
            switch dialogAction {
            case "cancel":
                chosen = registered.first(where: { $0.kind == "cancel" }) ?? registered.first
            case "confirm":
                chosen = registered.first(where: { $0.kind != "cancel" }) ?? registered.first
            case "dismiss":
                await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                    alert.dismiss(animated: false) { continuation.resume() }
                }
                return .object(["dismissed": .bool(true), "type": .string("UIAlertController")])
            case "button":
                if let idx = params["index"]?.intValue {
                    chosen = registered.first(where: { $0.index == idx })
                } else if let label = params["label"]?.stringValue {
                    chosen = registered.first(where: { $0.label == label })
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'label' for dialog button action")
                }
            default:
                throw DebugAPIError.invalidArgument("Unknown dialog action: \(dialogAction)")
            }
            if let chosen {
                chosen.handler()
                if alert.presentingViewController != nil && !alert.isBeingDismissed {
                    await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                        alert.dismiss(animated: false) { continuation.resume() }
                    }
                }
                return .object(["invoked": .string(chosen.label), "index": .int(chosen.index)])
            }
            // Fallback for alerts without registered handlers (dismiss safely without private KVC)
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                alert.dismiss(animated: false) { continuation.resume() }
            }
            return .object(["dismissed": .bool(true), "fallback": .bool(true)])
        }

        if let dialog = activeDialogController() as? ManagementDialog {
            switch dialogAction {
            case "confirm":
                dialog.debugConfirm()
                return .object(["invoked": .string(dialog.debugActionTitle), "type": .string("ManagementDialog")])
            case "cancel", "dismiss":
                dialog.debugCancel()
                return .object(["dismissed": .bool(true), "type": .string("ManagementDialog")])
            case "button":
                if let idx = params["index"]?.intValue {
                    switch idx {
                    case 0:
                        dialog.debugCancel()
                        return .object(["invoked": .string("取消"), "index": .int(0), "type": .string("ManagementDialog")])
                    case 1:
                        dialog.debugConfirm()
                        return .object(["invoked": .string(dialog.debugActionTitle), "index": .int(1), "type": .string("ManagementDialog")])
                    default:
                        throw DebugAPIError.invalidArgument("ManagementDialog button index out of range: \(idx)")
                    }
                } else if let label = params["label"]?.stringValue {
                    if label == "取消" {
                        dialog.debugCancel()
                        return .object(["invoked": .string("取消"), "index": .int(0), "type": .string("ManagementDialog")])
                    } else if label == dialog.debugActionTitle {
                        dialog.debugConfirm()
                        return .object(["invoked": .string(dialog.debugActionTitle), "index": .int(1), "type": .string("ManagementDialog")])
                    } else {
                        throw DebugAPIError.notFound("ManagementDialog button not found: \(label)")
                    }
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'label' for dialog button action")
                }
            default:
                throw DebugAPIError.invalidArgument("Unknown dialog action: \(dialogAction)")
            }
        }

        // Other presented sheet
        if dialogAction == "dismiss", let sheet = presentedModalChain().last {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                sheet.dismiss(animated: false) { continuation.resume() }
            }
            return .object(["dismissed": .bool(true), "type": .string(String(describing: type(of: sheet)))])
        }
        throw DebugAPIError.invalidState("No active dialog is currently presented")
    }

    private func performGlobalSettingAction(
        action: String,
        params: [String: DebugJSONValue]
    ) async throws -> DebugJSONValue? {
        guard let assembly else { return nil }
        let domain = params["domain"]?.stringValue ?? "general"
        if action == "setting.set" {
            guard let key = params["key"]?.stringValue, let val = params["value"] else {
                throw DebugAPIError.invalidArgument("setting.set requires 'key' and 'value'")
            }
            switch domain {
            case "general":
                switch key {
                case "colorMode":
                    guard let raw = val.stringValue, let mode = MusicColorMode(rawValue: raw) else {
                        throw DebugAPIError.invalidArgument("Invalid colorMode: \(val)")
                    }
                    let saved = await assembly.appearance.set(\.colorMode, mode)
                    return .object(["key": .string(key), "value": .string(mode.rawValue), "saved": .bool(saved)])
                case "lockLyric":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("lockLyric must be bool") }
                    let saved = await assembly.appearance.set(\.lockLyric, b)
                    return .object(["key": .string(key), "value": .bool(b), "saved": .bool(saved)])
                case "playlistView":
                    guard let raw = val.stringValue, let v = MusicPlaylistAppearance(rawValue: raw) else {
                        throw DebugAPIError.invalidArgument("Invalid playlistView: \(val)")
                    }
                    let saved = await assembly.appearance.set(\.playlistView, v)
                    return .object(["key": .string(key), "value": .string(v.rawValue), "saved": .bool(saved)])
                default:
                    return nil
                }
            case "music":
                guard let music = assembly.music else { throw DebugAPIError.invalidState("Music service unavailable") }
                switch key {
                case "requestQuality":
                    guard let q = val.stringValue else { throw DebugAPIError.invalidArgument("requestQuality must be string") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, requestQuality: q))
                    return .object(["key": .string(key), "value": .string(q)])
                case "lyricsEnabled":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("lyricsEnabled must be bool") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, lyricsEnabled: b))
                    return .object(["key": .string(key), "value": .bool(b)])
                case "showTranslation":
                    guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("showTranslation must be bool") }
                    await music.updateSettings(.init(ifRevision: music.state.settingsRevision, showTranslation: b))
                    return .object(["key": .string(key), "value": .bool(b)])
                default:
                    return nil
                }
            default:
                return nil
            }
        }
        if action == "setting.action" {
            let cmd = params["command"]?.stringValue ?? ""
            if domain == "general" && cmd == "reset_user_data" {
                guard params["confirm"]?.stringValue == "RESET_ALL_USER_DATA" else {
                    throw DebugAPIError.invalidState("reset_user_data requires confirm == 'RESET_ALL_USER_DATA'")
                }
                guard let appDelegate else { throw DebugAPIError.invalidState("AppDelegate unavailable") }
                try await appDelegate.requestUserDataReset()
                return .object(["requested": .bool(true)])
            }
            if domain == "novel" && cmd == "refresh_sources" {
                guard let novel = assembly.novel else { throw DebugAPIError.invalidState("Novel feature unavailable") }
                try await novel.refreshSources()
                return .object(["sourcesCount": .int(novel.sources.count)])
            }
        }
        return nil
    }

    // MARK: - Condition Waiting (ui/wait)

    func uiWait(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let condition = body["condition"]?.stringValue else {
            throw DebugAPIError.invalidArgument("Missing required string field 'condition'")
        }
        let params = body["params"]?.objectValue ?? [:]
        let timeoutMs = min(max(body["timeoutMs"]?.intValue ?? 15000, 100), 120_000)

        if condition == "log_event" {
            return try await waitForLogEvent(params: params, timeoutMs: timeoutMs)
        }
        let supportedConditions: Set<String> = ["idle", "route", "section_count", "row_count", "playback_state", "dialog"]
        guard supportedConditions.contains(condition) else {
            throw DebugAPIError.invalidArgument("Unknown ui/wait condition: \(condition)")
        }
        if condition == "route", params["target"]?.stringValue == nil, params["route"]?.stringValue == nil {
            throw DebugAPIError.invalidArgument("route wait condition requires 'target' or 'route'")
        }

        let deadline = Date().addingTimeInterval(Double(timeoutMs) / 1000.0)
        while true {
            if let matched = evaluateWaitCondition(condition: condition, params: params) {
                return matched
            }
            if Date() >= deadline {
                throw DebugAPIError.timeout("ui/wait timed out after \(timeoutMs)ms waiting for '\(condition)'", details: .object([
                    "condition": .string(condition),
                    "params": .object(params),
                    "lastState": uiState()
                ]))
            }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
    }

    private func evaluateWaitCondition(condition: String, params: [String: DebugJSONValue]) -> DebugJSONValue? {
        switch condition {
        case "idle":
            let state = uiState()
            let busy = state["isBusy"]?.boolValue ?? true
            let transitioning = coordinator?.navigation.transitionCoordinator != nil
            if !busy && !transitioning {
                return .object(["matched": .bool(true), "condition": .string("idle"), "state": state])
            }
            return nil

        case "route":
            let state = uiState()
            let wantTarget = params["target"]?.stringValue
            let wantRoute = params["route"]?.stringValue
            let curTarget = state["target"]?.stringValue
            let curRoute = state["route"]?.stringValue
            if let wantTarget, curTarget == wantTarget {
                return .object(["matched": .bool(true), "condition": .string("route"), "state": state])
            }
            if let wantRoute, curRoute == wantRoute {
                return .object(["matched": .bool(true), "condition": .string("route"), "state": state])
            }
            return nil

        case "section_count":
            let content = uiContent(maxItems: 200, includeRaw: false)
            let sections = content["sections"]?.arrayValue ?? []
            let minSections = params["minSections"]?.intValue ?? 1
            guard sections.count >= minSections else { return nil }
            if let sIdx = params["sectionIndex"]?.intValue {
                guard sections.indices.contains(sIdx) else { return nil }
                let minItems = params["minItems"]?.intValue ?? 1
                let count = sections[sIdx]["itemCount"]?.intValue ?? (sections[sIdx]["items"]?.arrayValue?.count ?? 0)
                guard count >= minItems else { return nil }
            }
            return .object(["matched": .bool(true), "condition": .string("section_count"), "sectionCount": .int(sections.count)])

        case "row_count":
            let content = uiContent(maxItems: 500, includeRaw: false)
            let rows = content["rows"]?.arrayValue ?? []
            let minRows = params["minRows"]?.intValue ?? 1
            guard rows.count >= minRows else { return nil }
            return .object(["matched": .bool(true), "condition": .string("row_count"), "rowCount": .int(rows.count)])

        case "playback_state":
            let domain = params["domain"]?.stringValue ?? "music"
            let wantState = params["state"]?.stringValue ?? "playing"
            let pb = playbackState()
            let actual = pb[domain]?["state"]?.stringValue
            guard actual == wantState else { return nil }
            return .object(["matched": .bool(true), "condition": .string("playback_state"), "playback": pb])

        case "dialog":
            let wantOpen = params["open"]?.boolValue ?? true
            let isOpen = activeDialogController() != nil
            guard isOpen == wantOpen else { return nil }
            return .object(["matched": .bool(true), "condition": .string("dialog"), "state": uiState()])

        default:
            return nil
        }
    }

    private func waitForLogEvent(params: [String: DebugJSONValue], timeoutMs: Int) async throws -> DebugJSONValue {
        guard let cat = params["cat"]?.stringValue, !cat.isEmpty else {
            throw DebugAPIError.invalidArgument("log_event wait requires 'cat'")
        }
        guard cat == "all" || DebugLogCategory(rawValue: cat) != nil else {
            throw DebugAPIError.invalidArgument("Unknown log category for log_event wait: \(cat)")
        }
        let wantSub = params["sub"]?.stringValue
        let wantEvent = params["event"]?.stringValue
        let sinceSeq = params["sinceSeq"]?.intValue.map(Int64.init)
        let waitStartedTS = DebugTimestamp.iso8601UTC()

        // Check already persisted events first if sinceSeq is provided
        if let sinceSeq,
           let existing = try? runtime.logger.tail(.init(
               cat: cat,
               limit: 100,
               sinceSeq: sinceSeq,
               sub: wantSub,
               event: wantEvent
           )),
           let first = existing.first {
            return .object(["matched": .bool(true), "condition": .string("log_event"), "event": first])
        }

        let logger = runtime.logger
        return try await withThrowingTaskGroup(of: DebugJSONValue.self) { group in
            let subBox = LogSubscriptionBox(logger: logger)
            group.addTask {
                try await withTaskCancellationHandler {
                    try await withCheckedThrowingContinuation { continuation in
                        subBox.start(cat: cat, sub: wantSub, event: wantEvent, sinceSeq: sinceSeq, continuation: continuation)
                    }
                } onCancel: {
                    subBox.cancelWithError(CancellationError())
                }
            }
            // Poll JSONL files so events written directly by embedded Node (`proc: "node"`) also wake waitForLogEvent.
            group.addTask {
                while !Task.isCancelled {
                    try await Task.sleep(nanoseconds: 150_000_000)
                    if let entries = try? logger.tail(.init(
                        cat: cat,
                        limit: 200,
                        sinceSeq: sinceSeq,
                        sub: wantSub,
                        event: wantEvent
                    )) {
                        let match: DebugJSONValue?
                        if sinceSeq != nil {
                            match = entries.first
                        } else {
                            match = entries.first { ($0["ts"]?.stringValue ?? "") >= waitStartedTS }
                        }
                        if let match {
                            return .object(["matched": .bool(true), "condition": .string("log_event"), "event": match])
                        }
                    }
                }
                throw CancellationError()
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeoutMs) * 1_000_000)
                subBox.cancelWithTimeout(timeoutMs: timeoutMs, cat: cat)
                throw DebugAPIError.timeout("Timed out after \(timeoutMs)ms waiting for log_event \(cat)")
            }
            defer {
                subBox.cleanup()
                group.cancelAll()
            }
            guard let first = try await group.next() else {
                throw DebugAPIError.timeout("Timed out waiting for log_event")
            }
            return first
        }
    }

    // MARK: - Playback & Novel Direct Control

    func playbackState() -> DebugJSONValue {
        let playback = assembly?.playback
        let musicJSON = assembly?.music.flatMap { m in
            playback.map { m.debugPlaybackState(playback: $0) }
        } ?? .null
        let filmModel = assembly?.filmMediaPlayback ?? assembly?.filmPlayback
        let filmJSON = filmModel?.debugPlaybackState() ?? .null
        return .object([
            "activeOwner": .optional(playback?.snapshot.owner),
            "music": musicJSON,
            "film": filmJSON
        ])
    }

    func playbackCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let domain = body["domain"]?.stringValue,
              let command = body["command"]?.stringValue else {
            throw DebugAPIError.invalidArgument("playback/command requires 'domain' and 'command'")
        }
        let params = body["params"]?.objectValue ?? [:]
        switch domain {
        case "music":
            guard let music = assembly?.music, let playback = assembly?.playback else {
                throw DebugAPIError.invalidState("Music playback is not ready")
            }
            let updated = try await music.debugPerformPlaybackCommand(command, params: params, playback: playback)
            return .object(["domain": .string("music"), "command": .string(command), "state": updated])
        case "film":
            guard let film = assembly?.filmMediaPlayback ?? assembly?.filmPlayback else {
                throw DebugAPIError.invalidState("No active film playback model")
            }
            let updated = try await film.debugPerformPlaybackCommand(command, params: params)
            return .object(["domain": .string("film"), "command": .string(command), "state": updated])
        default:
            throw DebugAPIError.invalidArgument("Unsupported playback domain: \(domain)")
        }
    }

    func novelCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let command = body["command"]?.stringValue else {
            throw DebugAPIError.invalidArgument("novel/command requires 'command'")
        }
        let params = body["params"]?.objectValue ?? [:]
        guard let novel = assembly?.novel else {
            throw DebugAPIError.invalidState("Novel feature is not ready")
        }
        DebugRuntime.log(.novel, sub: "debug.command", event: command, data: .object(params))

        switch command {
        case "open_book":
            guard let bookId = params["bookId"]?.stringValue, !bookId.isEmpty else {
                throw DebugAPIError.invalidArgument("open_book requires 'bookId'")
            }
            return try await navOpen(["target": .string("novel.book"), "params": .object(["bookId": .string(bookId)])])

        case "start_reading":
            var p: [String: DebugJSONValue] = [:]
            if let bookId = params["bookId"] { p["bookId"] = bookId }
            return try await navOpen(["target": .string("novel.reader"), "params": .object(p)])

        case "select_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            guard let idx = params["index"]?.intValue else {
                throw DebugAPIError.invalidArgument("select_chapter requires numeric 'index'")
            }
            guard reader.session.chapters.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Chapter index \(idx) out of range (0..<\(reader.session.chapters.count))")
            }
            reader.session.jump(idx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "next_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let nextIdx = reader.session.chapterIndex + 1
            guard reader.session.chapters.indices.contains(nextIdx) else {
                throw DebugAPIError.invalidState("Already at the last chapter")
            }
            reader.session.jump(nextIdx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "prev_chapter":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let prevIdx = reader.session.chapterIndex - 1
            guard reader.session.chapters.indices.contains(prevIdx) else {
                throw DebugAPIError.invalidState("Already at the first chapter")
            }
            reader.session.jump(prevIdx, ratio: 0)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "next_page":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let moved = reader.debugNextPage()
            return .object(["moved": .bool(moved), "content": reader.debugContent(maxItems: 50, includeRaw: false).toJSON()])

        case "prev_page":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let moved = reader.debugPrevPage()
            return .object(["moved": .bool(moved), "content": reader.debugContent(maxItems: 50, includeRaw: false).toJSON()])

        case "switch_source":
            guard let reader = activeBusinessController() as? NovelReaderViewController else {
                throw DebugAPIError.invalidState("NovelReaderViewController is not active")
            }
            let candidate: NovelBook
            if let bookId = params["bookId"]?.stringValue, let found = findNovelBook(bookId: bookId) {
                candidate = found
            } else if let sourceId = params["sourceId"]?.stringValue,
                      let entry = novel.local.entries.first(where: { $0.book.installationId == sourceId }) {
                candidate = entry.book
            } else if let sIdx = params["sourceIndex"]?.intValue,
                      novel.sources.indices.contains(sIdx) {
                let targetSource = novel.sources[sIdx]
                if let entry = novel.local.entries.first(where: { $0.book.installationId == targetSource.installationId }) {
                    candidate = entry.book
                } else {
                    throw DebugAPIError.notFound("No candidate book found for source index \(sIdx)")
                }
            } else {
                throw DebugAPIError.invalidArgument("Provide 'bookId', 'sourceId', or 'sourceIndex' for switch_source")
            }
            reader.session.switchSource(candidate)
            return reader.debugContent(maxItems: 50, includeRaw: false).toJSON()

        case "toggle_shelf":
            let targetBook: NovelBook
            if let bookId = params["bookId"]?.stringValue {
                guard let found = findNovelBook(bookId: bookId) else {
                    throw DebugAPIError.notFound("Novel book not found: \(bookId)")
                }
                targetBook = found
            } else if let detail = activeBusinessController() as? NovelDetailViewController {
                targetBook = detail.debugBook
            } else if let reader = activeBusinessController() as? NovelReaderViewController {
                targetBook = reader.session.book
            } else {
                throw DebugAPIError.invalidArgument("Provide 'bookId' or open a book for toggle_shelf")
            }
            let currentlyOnShelf = novel.local.entries.first(where: { $0.book.id == targetBook.id })?.onShelf ?? false
            let desired = params["present"]?.boolValue ?? !currentlyOnShelf
            try await novel.setShelf(targetBook, present: desired)
            return .object(["bookId": .string(targetBook.id), "onShelf": .bool(desired)])

        default:
            throw DebugAPIError.invalidArgument("Unsupported novel command: \(command)")
        }
    }

    // MARK: - Helper Utilities

    private func resolveAccountPlatform(_ raw: String?, music: MusicViewModel) -> MusicAccountPlatform {
        if let raw = raw?.lowercased() {
            switch raw {
            case "wy", "netease": return .netease
            case "tx", "qq": return .qq
            case "kg", "kugou": return .kugou
            default: break
            }
        }
        return .netease
    }

    private func parseBrowsePlatform(_ raw: String) -> MusicPublicBrowsePlatform? {
        switch raw.lowercased() {
        case "wy", "netease": return .netease
        case "tx", "qq": return .qq
        case "kg", "kugou": return .kugou
        case "kw", "kuwo": return .kuwo
        case "mg", "migu": return .migu
        default: return MusicPublicBrowsePlatform(rawValue: raw)
        }
    }

    private func findNovelBook(bookId: String) -> NovelBook? {
        if let novel = assembly?.novel,
           let entry = novel.local.entries.first(where: { $0.book.id == bookId || $0.book.bookUrl == bookId }) {
            return entry.book
        }
        if let home = coordinator?.novelHome,
           let found = home.discovery.books.first(where: { $0.id == bookId || $0.bookUrl == bookId }) {
            return found
        }
        return nil
    }

    private func describeDialog(_ dialog: UIViewController) -> DebugJSONValue {
        if let alert = dialog as? UIAlertController {
            let registered = DebugAlertActionRegistry.shared.actions(for: alert)
            let buttons: [DebugJSONValue]
            if let registered, !registered.isEmpty {
                buttons = registered.map { act in
                    .object([
                        "index": .int(act.index),
                        "label": .string(act.label),
                        "kind": .string(act.kind)
                    ])
                }
            } else {
                buttons = alert.actions.enumerated().map { idx, act in
                    let kind: String
                    switch act.style {
                    case .cancel: kind = "cancel"
                    case .destructive: kind = "destructive"
                    default: kind = "default"
                    }
                    return .object([
                        "index": .int(idx),
                        "label": .string(act.title ?? ""),
                        "kind": .string(kind)
                    ])
                }
            }
            return .object([
                "type": .string("UIAlertController"),
                "title": .optional(alert.title),
                "message": .optional(alert.message),
                "buttons": .array(buttons)
            ])
        }
        if let mgmt = dialog as? ManagementDialog {
            return .object([
                "type": .string("ManagementDialog"),
                "title": .string(mgmt.debugHeading),
                "message": .optional(mgmt.debugErrorText),
                "buttons": .array([
                    .object(["index": .int(0), "label": .string("取消"), "kind": .string("cancel")]),
                    .object(["index": .int(1), "label": .string(mgmt.debugActionTitle), "kind": .string("default")])
                ])
            ])
        }
        return .null
    }

    private func indexViewControllers(_ vc: UIViewController, into map: inout [ObjectIdentifier: UIViewController]) {
        if let v = vc.viewIfLoaded {
            map[ObjectIdentifier(v)] = vc
        }
        for child in vc.children {
            indexViewControllers(child, into: &map)
        }
        if let presented = vc.presentedViewController {
            indexViewControllers(presented, into: &map)
        }
    }

    private func serializeView(
        _ view: UIView,
        depth: Int,
        maxDepth: Int,
        visibleOnly: Bool,
        window: UIWindow,
        vcMap: [ObjectIdentifier: UIViewController]
    ) -> DebugJSONValue {
        let frame = view.convert(view.bounds, to: window)
        let visible = !view.isHidden && view.alpha >= 0.01
        let enabled = (view as? UIControl)?.isEnabled ?? view.isUserInteractionEnabled
        let text: String?
        if let label = view as? UILabel {
            text = label.text
        } else if let button = view as? UIButton {
            text = button.currentTitle ?? button.configuration?.title
        } else if let field = view as? UITextField {
            text = field.text
        } else if let tv = view as? UITextView {
            text = tv.text
        } else if let bar = view as? UISearchBar {
            text = bar.text
        } else {
            text = nil
        }

        var children: [DebugJSONValue] = []
        if depth < maxDepth {
            for sub in view.subviews {
                if visibleOnly && (sub.isHidden || sub.alpha < 0.01) { continue }
                children.append(serializeView(sub, depth: depth + 1, maxDepth: maxDepth, visibleOnly: visibleOnly, window: window, vcMap: vcMap))
            }
        }

        let ownerName = vcMap[ObjectIdentifier(view)].map { String(describing: type(of: $0)) }
        return .object([
            "type": .string(String(describing: type(of: view))),
            "controller": .optional(ownerName),
            "name": .optional(view.accessibilityLabel),
            "automationId": .optional(view.accessibilityIdentifier),
            "visible": .bool(visible),
            "enabled": .bool(enabled),
            "bounds": .object([
                "x": .number((Double(frame.origin.x) * 10).rounded() / 10),
                "y": .number((Double(frame.origin.y) * 10).rounded() / 10),
                "w": .number((Double(frame.size.width) * 10).rounded() / 10),
                "h": .number((Double(frame.size.height) * 10).rounded() / 10)
            ]),
            "text": .optional(text),
            "children": .array(children)
        ])
    }
}

private final class LogSubscriptionBox: @unchecked Sendable {
    private let logger: DebugLogger
    private let lock = NSLock()
    private var token: UUID?
    private var continuation: CheckedContinuation<DebugJSONValue, Error>?
    private var pendingError: Error?

    init(logger: DebugLogger) {
        self.logger = logger
    }

    func start(
        cat: String,
        sub: String?,
        event: String?,
        sinceSeq: Int64?,
        continuation: CheckedContinuation<DebugJSONValue, Error>
    ) {
        lock.lock()
        if let err = pendingError {
            lock.unlock()
            continuation.resume(throwing: err)
            return
        }
        self.continuation = continuation
        lock.unlock()

        let id = logger.subscribe { [weak self] record in
            guard let self else { return }
            if cat != "all", record.cat.rawValue != cat { return }
            if let sinceSeq, record.seq <= sinceSeq { return }
            if let sub, record.sub != sub { return }
            if let event, record.event != event { return }
            self.resolve(.success(.object([
                "matched": .bool(true),
                "condition": .string("log_event"),
                "event": record.json
            ])))
        }
        lock.lock()
        if self.continuation == nil {
            lock.unlock()
            logger.unsubscribe(id)
        } else {
            self.token = id
            lock.unlock()
        }
    }

    func cancelWithTimeout(timeoutMs: Int, cat: String) {
        resolve(.failure(DebugAPIError.timeout("Timed out after \(timeoutMs)ms waiting for log_event \(cat)")))
    }

    func cancelWithError(_ error: Error) {
        resolve(.failure(error))
    }

    func cleanup() {
        lock.lock()
        let t = token
        token = nil
        lock.unlock()
        if let t { logger.unsubscribe(t) }
    }

    private func resolve(_ result: Result<DebugJSONValue, Error>) {
        lock.lock()
        guard let cont = continuation else {
            if case .failure(let err) = result, pendingError == nil {
                pendingError = err
            }
            lock.unlock()
            return
        }
        continuation = nil
        let t = token
        token = nil
        lock.unlock()
        if let t { logger.unsubscribe(t) }
        cont.resume(with: result)
    }
}
