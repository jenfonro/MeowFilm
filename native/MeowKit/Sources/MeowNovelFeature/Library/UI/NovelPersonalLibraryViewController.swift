#if os(iOS)
import Combine
import UIKit
import MeowDebug
import MeowUI

public enum NovelPersonalSectionKind: String, Sendable {
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
public final class NovelPersonalLibraryViewController: PosterHomeViewController {
    public var onSelectBook: ((NovelBook) -> Void)?
    public var onSelectSection: ((NovelPersonalSectionKind) -> Void)?

    public let feature: NovelFeature
    var renderedFavorites: [NovelLibraryEntry] = []
    var renderedHistory: [NovelLibraryEntry] = []
    public override var usesPageHeader: Bool { false }

    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.feature = feature
        super.init(
            configuration: PosterHomeConfiguration(
                pageAccessibilityID: "library.novel",
                collectionAccessibilityID: "library.novel.sections",
                moreIdentifierPrefix: "library.novel.more"
            ),
            themeProvider: themeProvider
        )
        pageTitle = "小说"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let changes = Publishers.MergeMany([
            feature.$local.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            feature.$sources.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            feature.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(changes, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() {
        let feature = self.feature
        runPageTask(key: "novel.library.reload") { [weak self] in
            try? await feature.reloadLocal()
            self?.render()
        }
        render()
        super.pageDidBecomeVisible()
    }

    public override func didRequestRefresh() {
        let feature = self.feature
        runPageTask(key: "novel.library.reload") { [weak self] in
            do {
                try await feature.reloadLocal()
                self?.render()
            } catch {
                self?.collectionRefreshEnd()
                self?.enqueueError(error.localizedDescription)
            }
        }
    }

    public override func didTapSectionMore(sectionID: String) {
        guard let kind = NovelPersonalSectionKind(rawValue: sectionID) else { return }
        onSelectSection?(kind)
    }

    public override func didTapSectionAction(sectionID: String) {
        guard sectionID == NovelPersonalSectionKind.history.rawValue else { return }
        requestClearHistory()
    }

    public override func didSelectCard(inSection sectionID: String, cardID: String, index: Int) {
        let entries = sectionID == NovelPersonalSectionKind.favorites.rawValue ? renderedFavorites : renderedHistory
        let picked: NovelLibraryEntry?
        if entries.indices.contains(index), NovelHomeViewController.cardID(for: entries[index].book) == cardID {
            picked = entries[index]
        } else {
            picked = entries.first { NovelHomeViewController.cardID(for: $0.book) == cardID }
        }
        guard let picked else { return }
        onSelectBook?(picked.book)
    }

    public func requestClearHistory() {
        guard canPresentPageAlert, !renderedHistory.isEmpty else { return }
        presentPageConfirmation(
            title: "清空小说历史记录？",
            confirmTitle: "清空",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                let feature = self.feature
                self.runPageTask { [weak self] in
                    do {
                        try await feature.clearHistory()
                    } catch {
                        self?.enqueueError(error.localizedDescription)
                    }
                }
            }
        )
    }

    private func collectionRefreshEnd() {
        applyHomeState(
            sourceID: "library.novel",
            sections: buildSections(),
            isLoading: false,
            hasError: false,
            emptyText: "暂无记录"
        )
    }

    static func orderedShelfEntries(_ entries: [NovelLibraryEntry]) -> [NovelLibraryEntry] {
        entries.filter(\.onShelf).sorted { lhs, rhs in
            let lPin = lhs.pinnedAt != nil
            let rPin = rhs.pinnedAt != nil
            if lPin != rPin { return lPin }
            if let lp = lhs.pinnedAt, let rp = rhs.pinnedAt, lp != rp { return lp > rp }
            let lTime = lhs.readAt ?? lhs.addedAt
            let rTime = rhs.readAt ?? rhs.addedAt
            if lTime != rTime { return lTime > rTime }
            return lhs.book.name.localizedStandardCompare(rhs.book.name) == .orderedAscending
        }
    }

    static func orderedHistoryEntries(_ entries: [NovelLibraryEntry]) -> [NovelLibraryEntry] {
        entries.filter { $0.readAt != nil }.sorted { lhs, rhs in
            let lr = lhs.readAt ?? .distantPast
            let rr = rhs.readAt ?? .distantPast
            if lr != rr { return lr > rr }
            return lhs.id < rhs.id
        }
    }

    static func makeEntryCard(
        _ entry: NovelLibraryEntry,
        isHistory: Bool,
        sourceNames: [String: String],
        feature: NovelFeature
    ) -> PosterCatalogCard {
        let badge: String
        if isHistory {
            badge = entry.anchor?.chapterTitle ?? ""
        } else if entry.pinnedAt != nil {
            badge = "置顶"
        } else {
            badge = entry.anchor?.chapterTitle ?? ""
        }
        let sourceName = sourceNames[entry.book.installationId] ?? ""
        let label = !sourceName.isEmpty ? sourceName : entry.book.author
        return NovelHomeViewController.makePosterCard(
            entry.book,
            feature: feature,
            textBadge: badge,
            label: label
        )
    }

    private func buildSections() -> [PosterCatalogSection] {
        let allEntries = feature.local.entries
        renderedFavorites = Self.orderedShelfEntries(allEntries)
        renderedHistory = Self.orderedHistoryEntries(allEntries)
        let sourceNames = Dictionary(uniqueKeysWithValues: feature.sources.map { ($0.id, $0.name) })
        return [
            PosterCatalogSection(
                id: NovelPersonalSectionKind.favorites.rawValue,
                title: NovelPersonalSectionKind.favorites.title,
                hasMore: true,
                statusText: NovelPersonalSectionKind.favorites.emptyText,
                items: renderedFavorites.map { Self.makeEntryCard($0, isHistory: false, sourceNames: sourceNames, feature: feature) }
            ),
            PosterCatalogSection(
                id: NovelPersonalSectionKind.history.rawValue,
                title: NovelPersonalSectionKind.history.title,
                hasMore: true,
                actionTitle: renderedHistory.isEmpty ? nil : "清空",
                statusText: NovelPersonalSectionKind.history.emptyText,
                items: renderedHistory.map { Self.makeEntryCard($0, isHistory: true, sourceNames: sourceNames, feature: feature) }
            )
        ]
    }

    private func render() {
        guard isViewLoaded else { return }
        applyHomeState(
            sourceID: "library.novel",
            sections: buildSections(),
            isLoading: false,
            hasError: false,
            emptyText: "暂无记录"
        )
        if isPageVisible, let error = feature.error {
            feature.error = nil
            enqueueError(error)
        }
    }
}

@MainActor
public final class NovelPersonalCategoryViewController: PosterCategoryViewController {
    public var onSelectBook: ((NovelBook) -> Void)?

    public let sectionKind: NovelPersonalSectionKind
    public let feature: NovelFeature
    private lazy var clearButton = PageHeaderTextButton(title: "清空") { [weak self] in
        self?.requestClearHistory()
    }
    var renderedEntries: [NovelLibraryEntry] = []

    public init(
        kind: NovelPersonalSectionKind,
        feature: NovelFeature,
        themeProvider: any PageThemeProviding
    ) {
        self.sectionKind = kind
        self.feature = feature
        super.init(
            configuration: PosterCategoryConfiguration(
                pageAccessibilityID: "library.novel.\(kind.rawValue)",
                collectionAccessibilityID: "library.novel.\(kind.rawValue).grid",
                filterIdentifierPrefix: "library.novel.\(kind.rawValue)",
                moreButtonAccessibilityID: "library.novel.\(kind.rawValue).more",
                loadingAccessibilityID: "library.novel.\(kind.rawValue).loading",
                emptyText: kind.emptyText
            ),
            themeProvider: themeProvider
        )
        pageTitle = kind.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        clearButton.accessibilityIdentifier = "library.novel.history.clear"
        let changes = Publishers.Merge(
            feature.$local.dropFirst().map { _ -> Void in },
            feature.$sources.dropFirst().map { _ -> Void in }
        ).eraseToAnyPublisher()
        observe(changes, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() {
        render()
        super.pageDidBecomeVisible()
    }

    public override func didSelectCard(cardID: String, index: Int) {
        let entry: NovelLibraryEntry?
        if renderedEntries.indices.contains(index),
           NovelHomeViewController.cardID(for: renderedEntries[index].book) == cardID {
            entry = renderedEntries[index]
        } else {
            entry = renderedEntries.first { NovelHomeViewController.cardID(for: $0.book) == cardID }
        }
        guard let entry else { return }
        onSelectBook?(entry.book)
    }

    public override func didRequestRefresh() {
        let feature = self.feature
        runPageTask(key: "novel.category.reload") { [weak self] in
            try? await feature.reloadLocal()
            self?.render()
        }
    }

    private func requestClearHistory() {
        guard sectionKind == .history, canPresentPageAlert, !renderedEntries.isEmpty else { return }
        presentPageConfirmation(
            title: "清空小说历史记录？",
            confirmTitle: "清空",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                let feature = self.feature
                self.runPageTask { [weak self] in
                    do {
                        try await feature.clearHistory()
                    } catch {
                        self?.enqueueError(error.localizedDescription)
                    }
                }
            }
        )
    }

    private func render() {
        guard isViewLoaded else { return }
        let allEntries = feature.local.entries
        let isHistory = sectionKind == .history
        renderedEntries = isHistory
            ? NovelPersonalLibraryViewController.orderedHistoryEntries(allEntries)
            : NovelPersonalLibraryViewController.orderedShelfEntries(allEntries)
        pageHeader.setTrailing(isHistory && !renderedEntries.isEmpty ? [clearButton] : [])
        let sourceNames = Dictionary(uniqueKeysWithValues: feature.sources.map { ($0.id, $0.name) })
        let cards = renderedEntries.map {
            NovelPersonalLibraryViewController.makeEntryCard(
                $0,
                isHistory: isHistory,
                sourceNames: sourceNames,
                feature: feature
            )
        }
        applyCategoryState(
            filterRows: [],
            cards: cards,
            isLoading: false,
            hasMore: false,
            hasError: false
        )
    }
}

// MARK: - Debug Support

extension NovelPersonalLibraryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "library.novel",
            moduleId: "novel",
            route: "library/novel",
            title: "我的 · 小说",
            isBusy: false
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let allEntries = feature.local.entries
        let favs = Self.orderedShelfEntries(allEntries)
        let hists = Self.orderedHistoryEntries(allEntries)
        let favItems = favs.prefix(maxItems).enumerated().map { idx, entry in
            DebugContentItem(
                index: idx,
                id: entry.id,
                kind: "novel",
                title: entry.book.name,
                subtitle: entry.book.author.isEmpty ? nil : entry.book.author,
                badge: entry.pinnedAt != nil ? "置顶" : entry.anchor?.chapterTitle,
                coverUrl: entry.book.coverUrl.isEmpty ? nil : entry.book.coverUrl,
                actions: ["open"],
                meta: ["installationId": .string(entry.book.installationId), "bookUrl": .string(entry.book.bookUrl)]
            )
        }
        let histTake = max(0, maxItems - favItems.count)
        let histItems = hists.prefix(histTake).enumerated().map { idx, entry in
            DebugContentItem(
                index: idx,
                id: entry.id,
                kind: "novel",
                title: entry.book.name,
                subtitle: entry.book.author.isEmpty ? nil : entry.book.author,
                badge: entry.anchor?.chapterTitle,
                coverUrl: entry.book.coverUrl.isEmpty ? nil : entry.book.coverUrl,
                actions: ["open"],
                meta: ["installationId": .string(entry.book.installationId), "bookUrl": .string(entry.book.bookUrl)]
            )
        }
        return DebugContentSnapshot(
            target: "library.novel",
            kind: "sections",
            sections: [
                DebugContentSection(
                    index: 0,
                    id: NovelPersonalSectionKind.favorites.rawValue,
                    title: NovelPersonalSectionKind.favorites.title,
                    kind: "novel",
                    layout: "rail",
                    hasMore: true,
                    itemCount: favs.count,
                    items: favItems
                ),
                DebugContentSection(
                    index: 1,
                    id: NovelPersonalSectionKind.history.rawValue,
                    title: NovelPersonalSectionKind.history.title,
                    kind: "novel",
                    layout: "rail",
                    hasMore: true,
                    itemCount: hists.count,
                    items: histItems
                )
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        let allEntries = feature.local.entries
        switch request.action {
        case "section.more":
            let kind = try resolveSectionKind(request.params)
            onSelectSection?(kind)
            return .object(["openedSection": .string(kind.rawValue)])

        case "section.item.invoke":
            let kind = try resolveSectionKind(request.params)
            let list = kind == .favorites ? Self.orderedShelfEntries(allEntries) : Self.orderedHistoryEntries(allEntries)
            let entry = try Self.resolveNovelEntry(list: list, params: request.params)
            onSelectBook?(entry.book)
            return .object(["openedBookId": .string(entry.book.id), "name": .string(entry.book.name)])

        case "setting.action":
            if request.params["command"]?.stringValue == "clear_history" {
                try await feature.clearHistory()
                return .object(["executed": .bool(true), "command": .string("clear_history")])
            }
            if request.params["command"]?.stringValue == "request_clear_history" {
                requestClearHistory()
                return .object(["requested": .bool(true), "command": .string("request_clear_history")])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action on NovelPersonalLibraryViewController")

        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelPersonalLibraryViewController")
        }
    }

    public func debugRefresh() {
        let feature = self.feature
        Task { @MainActor in try? await feature.reloadLocal() }
    }

    private func resolveSectionKind(_ params: [String: DebugJSONValue]) throws -> NovelPersonalSectionKind {
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

    fileprivate static func resolveNovelEntry(list: [NovelLibraryEntry], params: [String: DebugJSONValue]) throws -> NovelLibraryEntry {
        guard !list.isEmpty else { throw DebugAPIError.invalidState("Target novel section is empty") }
        if let idx = (params["itemIndex"] ?? params["index"])?.intValue {
            guard list.indices.contains(idx) else { throw DebugAPIError.invalidArgument("itemIndex \(idx) out of range") }
            return list[idx]
        }
        if let id = (params["itemId"] ?? params["id"])?.stringValue {
            guard let found = list.first(where: { $0.id == id || $0.book.bookUrl == id || $0.book.name == id }) else {
                throw DebugAPIError.notFound("Novel entry not found: \(id)")
            }
            return found
        }
        return list[0]
    }
}

extension NovelPersonalCategoryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "library.novel",
            moduleId: "novel",
            route: "library/novel/\(sectionKind.rawValue)",
            title: sectionKind.title,
            isBusy: false
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let allEntries = feature.local.entries
        let list = sectionKind == .history
            ? NovelPersonalLibraryViewController.orderedHistoryEntries(allEntries)
            : NovelPersonalLibraryViewController.orderedShelfEntries(allEntries)
        let items = list.prefix(maxItems).enumerated().map { idx, entry in
            DebugContentItem(
                index: idx,
                id: entry.id,
                kind: "novel",
                title: entry.book.name,
                subtitle: entry.book.author.isEmpty ? nil : entry.book.author,
                badge: sectionKind == .history ? entry.anchor?.chapterTitle : (entry.pinnedAt != nil ? "置顶" : entry.anchor?.chapterTitle),
                coverUrl: entry.book.coverUrl.isEmpty ? nil : entry.book.coverUrl,
                actions: ["open"]
            )
        }
        return DebugContentSnapshot(
            target: "library.novel",
            kind: "sections",
            sections: [
                DebugContentSection(
                    index: 0,
                    id: sectionKind.rawValue,
                    title: sectionKind.title,
                    kind: "novel",
                    layout: "grid",
                    hasMore: false,
                    itemCount: list.count,
                    items: items
                )
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        let allEntries = feature.local.entries
        let list = sectionKind == .history
            ? NovelPersonalLibraryViewController.orderedHistoryEntries(allEntries)
            : NovelPersonalLibraryViewController.orderedShelfEntries(allEntries)
        switch request.action {
        case "section.item.invoke", "list.item.invoke":
            let entry = try NovelPersonalLibraryViewController.resolveNovelEntry(list: list, params: request.params)
            onSelectBook?(entry.book)
            return .object(["openedBookId": .string(entry.book.id), "name": .string(entry.book.name)])
        case "setting.action":
            if sectionKind == .history, request.params["command"]?.stringValue == "clear_history" {
                try await feature.clearHistory()
                return .object(["executed": .bool(true), "command": .string("clear_history")])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action")
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelPersonalCategoryViewController")
        }
    }

    public func debugRefresh() {
        let feature = self.feature
        Task { @MainActor in try? await feature.reloadLocal() }
    }
}
#endif
