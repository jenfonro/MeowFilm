#if os(iOS)
import Combine
import UIKit
import MeowDebug
import MeowUI

public enum PersonalLibrarySectionKind: String, Sendable {
    case favorites
    case history

    public var title: String {
        switch self {
        case .favorites: return "我的收藏"
        case .history: return "历史记录"
        }
    }

    public var emptyText: String {
        switch self {
        case .favorites: return "暂无收藏"
        case .history: return "暂无历史记录"
        }
    }
}

@MainActor
public final class FilmPersonalLibraryViewController: PosterHomeViewController {
    public var onSelectItem: ((FilmItem) -> Void)?
    public var onSelectSection: ((PersonalLibrarySectionKind) -> Void)?

    public let library: FilmLibrary
    public let history: FilmHistoryModel
    private let images: ArtworkImages
    var renderedFavorites: [FilmItem] = []
    var renderedHistory: [FilmItem] = []
    public override var usesPageHeader: Bool { false }

    public init(
        library: FilmLibrary,
        history: FilmHistoryModel,
        images: ArtworkImages,
        themeProvider: any PageThemeProviding
    ) {
        self.library = library
        self.history = history
        self.images = images
        super.init(
            configuration: PosterHomeConfiguration(
                pageAccessibilityID: "library.film",
                collectionAccessibilityID: "library.film.sections",
                moreIdentifierPrefix: "library.film.more"
            ),
            themeProvider: themeProvider
        )
        pageTitle = "影视"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let changes = Publishers.MergeMany([
            library.$favoriteItems.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            library.$loadError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            history.$items.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            history.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            history.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(changes, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() {
        history.reload()
        render()
        super.pageDidBecomeVisible()
    }

    public override func pageDidBecomeHidden() {
        history.cancel()
        super.pageDidBecomeHidden()
    }

    public override func didRequestRefresh() {
        clearErrorSource("film.history")
        history.reload()
    }

    public override func didTapSectionMore(sectionID: String) {
        guard let kind = PersonalLibrarySectionKind(rawValue: sectionID) else { return }
        onSelectSection?(kind)
    }

    public override func didTapSectionAction(sectionID: String) {
        guard sectionID == PersonalLibrarySectionKind.history.rawValue else { return }
        requestClearHistory()
    }

    public override func didSelectCard(inSection sectionID: String, cardID: String, index: Int) {
        let source = sectionID == PersonalLibrarySectionKind.favorites.rawValue ? renderedFavorites : renderedHistory
        let item: FilmItem?
        if source.indices.contains(index), FilmHomeViewController.cardID(for: source[index]) == cardID {
            item = source[index]
        } else {
            item = source.first { FilmHomeViewController.cardID(for: $0) == cardID }
        }
        guard let item else { return }
        onSelectItem?(item)
    }

    public func requestClearHistory() {
        guard canPresentPageAlert, !history.items.isEmpty else { return }
        presentPageConfirmation(
            title: "清空影视历史记录？",
            confirmTitle: "清空",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                let history = self.history
                self.runPageTask { [weak self] in
                    do {
                        try await history.clearAll()
                    } catch {
                        self?.enqueueError(error.localizedDescription)
                    }
                }
            }
        )
    }

    static func makeFavoriteCard(_ item: FilmItem, images: ArtworkImages) -> PosterCatalogCard {
        let badge = item.score.isEmpty ? (item.entry?.textBadge ?? "") : item.score
        return FilmHomeViewController.makePosterCard(item, images: images, textBadge: badge, label: item.subtitle)
    }

    static func makeHistoryCard(_ item: FilmItem, images: ArtworkImages) -> PosterCatalogCard {
        let badge = item.entry?.textBadge ?? ""
        let label = item.entry?.siteLabel ?? item.subtitle
        return FilmHomeViewController.makePosterCard(item, images: images, textBadge: badge, label: label)
    }

    private func render() {
        guard isViewLoaded else { return }
        renderedFavorites = library.favoriteItems
        renderedHistory = history.items
        let sections = [
            PosterCatalogSection(
                id: PersonalLibrarySectionKind.favorites.rawValue,
                title: PersonalLibrarySectionKind.favorites.title,
                hasMore: true,
                statusText: PersonalLibrarySectionKind.favorites.emptyText,
                items: renderedFavorites.map { Self.makeFavoriteCard($0, images: images) }
            ),
            PosterCatalogSection(
                id: PersonalLibrarySectionKind.history.rawValue,
                title: PersonalLibrarySectionKind.history.title,
                hasMore: true,
                actionTitle: renderedHistory.isEmpty ? nil : "清空",
                statusText: PersonalLibrarySectionKind.history.emptyText,
                items: renderedHistory.map { Self.makeHistoryCard($0, images: images) }
            )
        ]
        applyHomeState(
            sourceID: "library.film",
            sections: sections,
            isLoading: history.isLoading && renderedHistory.isEmpty && renderedFavorites.isEmpty,
            hasError: history.error != nil || library.loadError != nil,
            emptyText: "暂无记录"
        )
        syncErrorSource(
            "film.history",
            message: history.error?.localizedDescription ?? library.loadError?.localizedDescription,
            retry: { [weak self] in self?.history.reload() }
        )
    }
}

@MainActor
public final class FilmPersonalCategoryViewController: PosterCategoryViewController {
    public var onSelectItem: ((FilmItem) -> Void)?

    public let sectionKind: PersonalLibrarySectionKind
    public let library: FilmLibrary
    public let history: FilmHistoryModel
    private let images: ArtworkImages
    private lazy var clearButton = PageHeaderTextButton(title: "清空") { [weak self] in
        self?.requestClearHistory()
    }
    var renderedItems: [FilmItem] = []

    public init(
        kind: PersonalLibrarySectionKind,
        library: FilmLibrary,
        history: FilmHistoryModel,
        images: ArtworkImages,
        themeProvider: any PageThemeProviding
    ) {
        self.sectionKind = kind
        self.library = library
        self.history = history
        self.images = images
        super.init(
            configuration: PosterCategoryConfiguration(
                pageAccessibilityID: "library.film.\(kind.rawValue)",
                collectionAccessibilityID: "library.film.\(kind.rawValue).grid",
                filterIdentifierPrefix: "library.film.\(kind.rawValue)",
                moreButtonAccessibilityID: "library.film.\(kind.rawValue).more",
                loadingAccessibilityID: "library.film.\(kind.rawValue).loading",
                emptyText: kind.emptyText
            ),
            themeProvider: themeProvider
        )
        pageTitle = kind.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        clearButton.accessibilityIdentifier = "library.film.history.clear"
        if sectionKind == .favorites {
            observe(library.$favoriteItems.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
                self?.render()
            }
        } else {
            let changes = Publishers.MergeMany([
                history.$items.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                history.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                history.$hasMore.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                history.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ]).eraseToAnyPublisher()
            observe(changes, whenVisible: true) { [weak self] in self?.render() }
        }
        render()
    }

    public override func pageDidBecomeVisible() {
        if sectionKind == .history {
            history.loadIfNeeded()
        }
        render()
        super.pageDidBecomeVisible()
    }

    public override func pageDidBecomeHidden() {
        if sectionKind == .history {
            history.cancel()
        }
        super.pageDidBecomeHidden()
    }

    public override func didSelectCard(cardID: String, index: Int) {
        let item: FilmItem?
        if renderedItems.indices.contains(index), FilmHomeViewController.cardID(for: renderedItems[index]) == cardID {
            item = renderedItems[index]
        } else {
            item = renderedItems.first { FilmHomeViewController.cardID(for: $0) == cardID }
        }
        guard let item else { return }
        onSelectItem?(item)
    }

    public override func didRequestRefresh() {
        if sectionKind == .history {
            clearErrorSource("film.history.category")
            history.reload()
        } else {
            render()
        }
    }

    public override func didRequestLoadMore() {
        if sectionKind == .history {
            clearErrorSource("film.history.category")
            history.loadMore()
        }
    }

    private func requestClearHistory() {
        guard sectionKind == .history, canPresentPageAlert, !history.items.isEmpty else { return }
        presentPageConfirmation(
            title: "清空影视历史记录？",
            confirmTitle: "清空",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                let history = self.history
                self.runPageTask { [weak self] in
                    do {
                        try await history.clearAll()
                    } catch {
                        self?.enqueueError(error.localizedDescription)
                    }
                }
            }
        )
    }

    private func render() {
        guard isViewLoaded else { return }
        switch sectionKind {
        case .favorites:
            renderedItems = library.favoriteItems
            pageHeader.setTrailing([])
            applyCategoryState(
                filterRows: [],
                cards: renderedItems.map { FilmPersonalLibraryViewController.makeFavoriteCard($0, images: images) },
                isLoading: false,
                hasMore: false,
                hasError: library.loadError != nil
            )
            syncErrorSource("film.history.category", message: library.loadError?.localizedDescription)

        case .history:
            renderedItems = history.items
            pageHeader.setTrailing(renderedItems.isEmpty ? [] : [clearButton])
            applyCategoryState(
                filterRows: [],
                cards: renderedItems.map { FilmPersonalLibraryViewController.makeHistoryCard($0, images: images) },
                isLoading: history.isLoading,
                hasMore: history.hasMore,
                hasError: history.error != nil
            )
            syncErrorSource(
                "film.history.category",
                message: history.error?.localizedDescription,
                retry: { [weak self] in self?.history.reload() }
            )
        }
    }
}

// MARK: - Debug Support

extension FilmPersonalLibraryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "library.film",
            moduleId: "film",
            route: "library/film",
            title: "我的 · 影视",
            isBusy: history.isLoading,
            isError: history.error != nil || library.loadError != nil,
            errorText: history.error?.localizedDescription ?? library.loadError?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let favItems = library.favoriteItems.prefix(maxItems).enumerated().map { idx, item in
            DebugContentItem(
                index: idx,
                id: item.targetID ?? item.id,
                kind: "film",
                title: item.title,
                subtitle: item.subtitle.isEmpty ? nil : item.subtitle,
                badge: item.score.isEmpty ? item.entry?.textBadge : item.score,
                coverUrl: item.posterURL?.absoluteString,
                actions: ["open"]
            )
        }
        let histTake = max(0, maxItems - favItems.count)
        let histItems = history.items.prefix(histTake).enumerated().map { idx, item in
            DebugContentItem(
                index: idx,
                id: item.targetID ?? item.id,
                kind: "film",
                title: item.title,
                subtitle: item.entry?.siteLabel.isEmpty == false ? item.entry?.siteLabel : nil,
                badge: item.entry?.textBadge.isEmpty == false ? item.entry?.textBadge : nil,
                coverUrl: item.posterURL?.absoluteString,
                actions: ["open"]
            )
        }
        return DebugContentSnapshot(
            target: "library.film",
            kind: "sections",
            loading: history.isLoading,
            error: debugPageStatus.errorText,
            sections: [
                DebugContentSection(
                    index: 0,
                    id: PersonalLibrarySectionKind.favorites.rawValue,
                    title: PersonalLibrarySectionKind.favorites.title,
                    kind: "film",
                    layout: "rail",
                    hasMore: true,
                    itemCount: library.favoriteItems.count,
                    items: favItems
                ),
                DebugContentSection(
                    index: 1,
                    id: PersonalLibrarySectionKind.history.rawValue,
                    title: PersonalLibrarySectionKind.history.title,
                    kind: "film",
                    layout: "rail",
                    hasMore: true,
                    itemCount: history.items.count,
                    items: histItems
                )
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.more":
            let kind = try resolveSectionKind(request.params)
            onSelectSection?(kind)
            return .object(["openedSection": .string(kind.rawValue)])

        case "section.item.invoke":
            let kind = try resolveSectionKind(request.params)
            let list = kind == .favorites ? library.favoriteItems : history.items
            let item = try Self.resolveFilmItem(list: list, params: request.params)
            onSelectItem?(item)
            return .object(["openedFilmId": .string(item.targetID ?? item.id), "title": .string(item.title)])

        case "setting.action":
            if request.params["command"]?.stringValue == "clear_history" {
                try await history.clearAll()
                return .object(["executed": .bool(true), "command": .string("clear_history")])
            }
            if request.params["command"]?.stringValue == "request_clear_history" {
                requestClearHistory()
                return .object(["requested": .bool(true), "command": .string("request_clear_history")])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action on FilmPersonalLibraryViewController")

        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPersonalLibraryViewController")
        }
    }

    public func debugRefresh() {
        history.reload()
    }

    private func resolveSectionKind(_ params: [String: DebugJSONValue]) throws -> PersonalLibrarySectionKind {
        if let sid = params["sectionId"]?.stringValue {
            if sid == "favorites" || sid == "我的收藏" { return .favorites }
            if sid == "history" || sid == "历史记录" { return .history }
            throw DebugAPIError.notFound("Section not found: \(sid)")
        }
        let idx = params["sectionIndex"]?.intValue ?? 0
        switch idx {
        case 0: return .favorites
        case 1: return .history
        default: throw DebugAPIError.invalidArgument("sectionIndex \(idx) out of range (0...1)")
        }
    }

    fileprivate static func resolveFilmItem(list: [FilmItem], params: [String: DebugJSONValue]) throws -> FilmItem {
        guard !list.isEmpty else { throw DebugAPIError.invalidState("Target film section is empty") }
        if let idx = (params["itemIndex"] ?? params["index"])?.intValue {
            guard list.indices.contains(idx) else { throw DebugAPIError.invalidArgument("itemIndex \(idx) out of range") }
            return list[idx]
        }
        if let id = (params["itemId"] ?? params["id"])?.stringValue {
            guard let found = list.first(where: { $0.id == id || $0.targetID == id || $0.title == id }) else {
                throw DebugAPIError.notFound("Film item not found: \(id)")
            }
            return found
        }
        return list[0]
    }
}

extension FilmPersonalCategoryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        let isHist = sectionKind == .history
        return DebugPageStatus(
            target: "library.film",
            moduleId: "film",
            route: "library/film/\(sectionKind.rawValue)",
            title: sectionKind.title,
            isBusy: isHist && history.isLoading,
            isError: isHist ? history.error != nil : library.loadError != nil,
            errorText: isHist ? history.error?.localizedDescription : library.loadError?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let list = sectionKind == .favorites ? library.favoriteItems : history.items
        let items = list.prefix(maxItems).enumerated().map { idx, item in
            DebugContentItem(
                index: idx,
                id: item.targetID ?? item.id,
                kind: "film",
                title: item.title,
                subtitle: item.subtitle.isEmpty ? nil : item.subtitle,
                badge: sectionKind == .history ? item.entry?.textBadge : item.score,
                coverUrl: item.posterURL?.absoluteString,
                actions: ["open"]
            )
        }
        return DebugContentSnapshot(
            target: "library.film",
            kind: "sections",
            loading: sectionKind == .history && history.isLoading,
            error: debugPageStatus.errorText,
            sections: [
                DebugContentSection(
                    index: 0,
                    id: sectionKind.rawValue,
                    title: sectionKind.title,
                    kind: "film",
                    layout: "grid",
                    hasMore: sectionKind == .history && history.hasMore,
                    itemCount: list.count,
                    items: items
                )
            ],
            hasMore: sectionKind == .history && history.hasMore
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        let list = sectionKind == .favorites ? library.favoriteItems : history.items
        switch request.action {
        case "section.item.invoke", "list.item.invoke":
            let item = try FilmPersonalLibraryViewController.resolveFilmItem(list: list, params: request.params)
            onSelectItem?(item)
            return .object(["openedFilmId": .string(item.targetID ?? item.id), "title": .string(item.title)])
        case "list.load_more":
            if sectionKind == .history { history.loadMore() }
            return .object(["triggered": .bool(sectionKind == .history)])
        case "setting.action":
            if sectionKind == .history, request.params["command"]?.stringValue == "clear_history" {
                try await history.clearAll()
                return .object(["executed": .bool(true), "command": .string("clear_history")])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action")
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPersonalCategoryViewController")
        }
    }

    public func debugRefresh() {
        if sectionKind == .history { history.reload() }
    }
}
#endif
