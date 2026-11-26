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
    let runtime: DebugRuntime
    weak var appDelegate: MeowfilmAppDelegate?
    weak var window: UIWindow?
    weak var coordinator: MeowfilmCoordinator?

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

    func requireCoordinator() throws -> MeowfilmCoordinator {
        guard let coordinator else {
            throw DebugAPIError.invalidState("Application coordinator is not ready yet")
        }
        return coordinator
    }

    func presentedModalChain() -> [UIViewController] {
        guard let root = coordinator?.root else { return [] }
        var chain: [UIViewController] = []
        var cur = root.presentedViewController
        while let next = cur {
            chain.append(next)
            cur = next.presentedViewController
        }
        return chain
    }

    func isDialogController(_ vc: UIViewController) -> Bool {
        vc is UIAlertController || vc is any DebugDialogControllable
    }

    func activeDialogController() -> UIViewController? {
        for vc in presentedModalChain().reversed() {
            if isDialogController(vc) {
                return vc
            }
        }
        return nil
    }

    func unwrapInspectable(_ vc: UIViewController?) -> UIViewController? {
        guard let vc else { return nil }
        if let nav = vc as? UINavigationController {
            return unwrapInspectable(nav.topViewController)
        }
        if let primary = vc as? MeowfilmPrimaryShellViewController {
            return primary.activeContentViewController ?? primary
        }
        return vc
    }

    func activeBusinessController() -> UIViewController? {
        let modals = presentedModalChain()
        for modal in modals.reversed() {
            if isDialogController(modal) {
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

    func statusFor(_ vc: UIViewController?) -> DebugPageStatus {
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

    func dismissAllModalsAndSidebar() async {
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

    func presentModal(_ vc: UIViewController, from presenter: UIViewController) async {
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
            throw DebugAPIError.unsupported("iOS has no standalone novel shelf page; query novel_books via storage/query or use novel/command toggle_shelf")
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
            guard let music = assembly?.music, let service = music.service else {
                throw DebugAPIError.invalidState("Music public browser is not available")
            }
            let platform = resolveAccountPlatform(params["platform"]?.stringValue, music: music)
            _ = try await resetToPrimaryRoot(.recommend)
            let page = MusicCollectionViewController(
                model: music,
                recommendation: .discovery(.daily(platform)),
                service: service,
                appearance: coordinator.assembly.appearance,
                router: coordinator
            )
            coordinator.navigation.pushViewController(page, animated: false)

        case "music.fm":
            let coordinator = try requireCoordinator()
            guard let music = assembly?.music, let playback = assembly?.playback else {
                throw DebugAPIError.invalidState("Music service is not ready")
            }
            if params["picker"]?.boolValue == true {
                await dismissAllModalsAndSidebar()
                let page = MusicFMSettingsViewController(model: music, appearance: coordinator.assembly.appearance)
                await presentModal(page, from: coordinator.presenter())
            } else if params["settings"]?.boolValue == true {
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
                guard let service = music.service else {
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
                    service: service,
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
            let restoring = coordinator.assembly.filmPlayback !== model
            if restoring, let sessionID = model.sessionID, let restore = coordinator.assembly.restoreFilmPlaybackUI {
                let ok = await restore(sessionID)
                if !ok {
                    throw DebugAPIError.invalidState("Could not restore FilmPlayerViewController from PiP")
                }
            } else {
                let ok = coordinator.presentFilmPlayer(model, restoring: restoring)
                if !ok {
                    throw DebugAPIError.invalidState("Could not present FilmPlayerViewController in current state")
                }
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
        if coordinator.navigation.viewControllers.contains(where: { ($0 as? any FilmPlaybackPage)?.filmPlaybackModel != nil }) {
            try? await assembly?.leaveFilm()
        }
        var baseStack: [UIViewController] = []
        if let main = coordinator.main {
            baseStack.append(main)
        }
        let settingsRoot = coordinator.makeSettingsViewController()
        baseStack.append(settingsRoot)

        switch domain {
        case "general":
            switch section {
            case nil, "", "root", "sources":
                coordinator.navigation.setViewControllers(baseStack, animated: false)
            case "appearance":
                let vc = MusicAppearanceSettingsViewController(store: coordinator.assembly.appearance)
                coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
            case "cache":
                guard let owner = coordinator.assembly.filmSettings?.playback else {
                    throw DebugAPIError.invalidState("Film cache settings owner is not ready")
                }
                let vc = FilmMetadataCacheSettingsViewController(owner: owner, themeProvider: coordinator.assembly.appearance)
                coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
            default:
                throw DebugAPIError.notFound("Unknown general settings section: \(section ?? "")")
            }

        case "music":
            guard let model = coordinator.assembly.music else {
                throw DebugAPIError.invalidState("Music settings unavailable")
            }
            let vc = MusicSettingsViewController(model: model, appearance: coordinator.assembly.appearance, router: coordinator)
            if let section, !section.isEmpty, section != "root" {
                if section == "cache" {
                    guard let owner = coordinator.assembly.filmSettings?.playback else {
                        throw DebugAPIError.invalidState("Cache settings owner is not ready")
                    }
                    let cacheVC = FilmMetadataCacheSettingsViewController(owner: owner, themeProvider: coordinator.assembly.appearance)
                    coordinator.navigation.setViewControllers(baseStack + [vc, cacheVC], animated: false)
                } else {
                    coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
                    try vc.debugOpenSection(section)
                }
            } else {
                coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
            }

        case "film":
            guard let model = coordinator.assembly.filmSettings else {
                throw DebugAPIError.invalidState("Film settings unavailable")
            }
            let vc = FilmSettingsViewController(model: model, themeProvider: coordinator.assembly.appearance)
            coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
            if let section, !section.isEmpty, section != "root" {
                try vc.debugOpenSection(section)
            }

        case "novel":
            guard let novel = coordinator.assembly.novel else {
                throw DebugAPIError.invalidState("Novel settings unavailable")
            }
            let vc = NovelSettingsViewController(feature: novel, themeProvider: coordinator.assembly.appearance)
            coordinator.navigation.setViewControllers(baseStack + [vc], animated: false)
            if let section, !section.isEmpty, section != "root" {
                try vc.debugOpenSection(section)
            }

        default:
            throw DebugAPIError.invalidArgument("Unknown settings domain: \(domain)")
        }
        coordinator.navigation.view.layoutIfNeeded()
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
        guard let music = coordinator.assembly.music, !music.isClosed else {
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
        guard let music = coordinator.assembly.music, !music.isClosed else {
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
}
