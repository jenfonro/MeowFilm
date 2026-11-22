#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

// MARK: - NovelHomeViewController

extension NovelHomeViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public func debugApplyFilter(sourceId: String?, channelIndex: Int?, kindUrl: String?, kindIndex: Int?) {
        if let sourceId, !sourceId.isEmpty {
            discovery.selectSource(id: sourceId)
        }
        if let channelIndex, discovery.channels.indices.contains(channelIndex) {
            discovery.selectChannel(id: discovery.channels[channelIndex].id)
        }
        let items = discovery.selectedChannel?.items ?? []
        if let kindUrl, let matched = items.first(where: { $0.url == kindUrl || $0.id == kindUrl }) {
            discovery.selectKind(id: matched.id)
        } else if let kindIndex, items.indices.contains(kindIndex) {
            discovery.selectKind(id: items[kindIndex].id)
        }
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "novel.home",
            moduleId: "novel",
            route: "novel/home",
            title: "小说",
            isBusy: discovery.loading,
            isError: discovery.error != nil,
            errorText: discovery.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs = discovery.channels.enumerated().map { idx, ch in
            DebugTabItem(group: "channel", index: idx, id: ch.id, title: ch.title, selected: ch.id == discovery.selectedChannelID)
        }
        let filters = (discovery.selectedChannel?.items ?? []).enumerated().map { idx, item in
            DebugFilterItem(group: "category", index: idx, id: item.id, title: item.title, selected: item.id == discovery.selectedKindID)
        }
        let rows = renderedBooks.prefix(maxItems).enumerated().map { idx, book in
            DebugContentRow(
                index: idx,
                id: book.id,
                title: book.name,
                subtitle: [book.author, book.latestChapterTitle].filter { !$0.isEmpty }.joined(separator: " · "),
                actions: ["open"],
                meta: ["installationId": .string(book.installationId), "bookUrl": .string(book.bookUrl)]
            )
        }
        return DebugContentSnapshot(
            target: "novel.home",
            kind: "list",
            loading: discovery.loading,
            error: discovery.error,
            tabs: tabs,
            filters: filters,
            rows: rows,
            hasMore: discovery.canLoadMore,
            extra: [
                "selectedSource": discovery.selectedSource.map { .object(["id": .string($0.id), "name": .string($0.name)]) } ?? .null
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "tab.select":
            let group = request.params["group"]?.stringValue ?? "channel"
            if group == "channel" {
                if let idx = request.params["index"]?.intValue, discovery.channels.indices.contains(idx) {
                    discovery.selectChannel(id: discovery.channels[idx].id)
                    return .object(["selectedChannelId": .string(discovery.channels[idx].id)])
                }
                if let id = request.params["id"]?.stringValue {
                    guard discovery.channels.contains(where: { $0.id == id }) else {
                        throw DebugAPIError.notFound("Novel channel not found: \(id)")
                    }
                    discovery.selectChannel(id: id)
                    return .object(["selectedChannelId": .string(id)])
                }
                throw DebugAPIError.invalidArgument("Invalid channel selection")
            } else {
                let items = discovery.selectedChannel?.items ?? []
                if let idx = request.params["index"]?.intValue, items.indices.contains(idx) {
                    discovery.selectKind(id: items[idx].id)
                    return .object(["selectedKindId": .string(items[idx].id)])
                }
                if let id = request.params["id"]?.stringValue {
                    guard items.contains(where: { $0.id == id }) else {
                        throw DebugAPIError.notFound("Novel category not found: \(id)")
                    }
                    discovery.selectKind(id: id)
                    return .object(["selectedKindId": .string(id)])
                }
                throw DebugAPIError.invalidArgument("Invalid category selection")
            }
        case "list.item.invoke":
            let book: NovelBook
            if let idx = request.params["index"]?.intValue {
                guard renderedBooks.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Book index \(idx) out of range (0..<\(renderedBooks.count))")
                }
                book = renderedBooks[idx]
            } else if let id = request.params["id"]?.stringValue {
                guard let found = renderedBooks.first(where: { $0.id == id || $0.bookUrl == id }) else {
                    throw DebugAPIError.notFound("Novel book not found: \(id)")
                }
                book = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id'")
            }
            onSelect?(book)
            return .object(["openedBookId": .string(book.id), "name": .string(book.name)])
        case "list.load_more":
            discovery.loadMore()
            return .object(["triggered": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelHomeViewController")
        }
    }

    public func debugRefresh() {
        discovery.refresh()
    }
}

// MARK: - NovelDetailViewController

extension NovelDetailViewController: DebugPageInspectable, DebugPageActionable {
    public var debugBook: NovelBook { book }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "novel.book",
            moduleId: "novel",
            route: "novel/book/\(book.id)",
            title: book.name,
            isBusy: false
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let onShelf = feature.local.entries.contains {
            ($0.id == book.id || ($0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl)) && $0.onShelf
        }
        return DebugContentSnapshot(
            target: "novel.book",
            kind: "detail",
            detail: .object([
                "id": .string(book.id),
                "name": .string(book.name),
                "author": .string(book.author),
                "intro": .string(book.intro),
                "latestChapterTitle": .string(book.latestChapterTitle),
                "installationId": .string(book.installationId),
                "bookUrl": .string(book.bookUrl),
                "onShelf": .bool(onShelf)
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" {
            onRead?(book)
            return .object(["startedReading": .string(book.id)])
        }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelDetailViewController")
    }
}

// MARK: - NovelReaderViewController

extension NovelReaderViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "novel.reader",
            moduleId: "novel",
            route: "novel/reader/\(session.book.id)",
            title: session.book.name,
            isBusy: session.loading,
            isError: session.error != nil,
            errorText: session.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let currentChapterTitle = session.chapters.indices.contains(session.chapterIndex)
            ? session.chapters[session.chapterIndex].name
            : (session.content?.title ?? "")
        let pageText: String
        if pages.indices.contains(currentPage) {
            pageText = pages[currentPage].elements.compactMap { $0.text?.string }.joined(separator: "\n")
        } else {
            pageText = session.content?.blocks.compactMap(\.text).joined(separator: "\n") ?? ""
        }
        let headSnippet = String(pageText.prefix(200))
        let tailSnippet = String(pageText.suffix(120))
        let settings = session.feature.local.settings
        return DebugContentSnapshot(
            target: "novel.reader",
            kind: "reader",
            loading: session.loading,
            error: session.error,
            reader: .object([
                "bookId": .string(session.book.id),
                "bookName": .string(session.book.name),
                "sourceId": .string(session.book.installationId),
                "chapterIndex": .int(session.chapterIndex),
                "chapterTitle": .string(currentChapterTitle),
                "chapterCount": .int(session.chapters.count),
                "pageIndex": .int(currentPage),
                "pageCount": .int(pages.count),
                "headText": .string(headSnippet),
                "tailText": .string(tailSnippet),
                "rulesCount": .int(session.feature.local.rules.count),
                "awaitingSourceConfirmation": .bool(session.sourceConfirmation != nil),
                "awaitingSourceSelection": .bool(session.sourceSelection != nil),
                "settings": .object([
                    "mode": .string(settings.mode.title),
                    "fontSize": .number(settings.fontSize),
                    "lineSpacing": .number(settings.lineSpacing),
                    "conversion": .string(settings.conversion),
                    "theme": .int(settings.theme)
                ])
            ])
        )
    }

    public func debugNextPage() -> Bool {
        if currentPage + 1 < pages.count {
            showPage(currentPage + 1, animated: false)
            return true
        }
        if session.chapterIndex + 1 < session.chapters.count {
            session.jump(session.chapterIndex + 1, ratio: 0)
            return true
        }
        return false
    }

    public func debugPrevPage() -> Bool {
        if currentPage > 0 {
            showPage(currentPage - 1, animated: false)
            return true
        }
        if session.chapterIndex > 0 {
            session.jump(session.chapterIndex - 1, ratio: 0)
            return true
        }
        return false
    }
}

// MARK: - NovelSearchResultsContent

extension NovelSearchResultsContent: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "search",
            moduleId: "novel",
            route: "search/novel",
            title: "小说搜索",
            isBusy: session.loading,
            isError: session.error != nil,
            errorText: session.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let projected = rows.prefix(maxItems).enumerated().map { idx, row in
            DebugContentRow(
                index: idx,
                id: row.book.id,
                title: row.book.name,
                subtitle: "\(row.book.author) · \(row.candidates.count) 源",
                actions: ["open"]
            )
        }
        return DebugContentSnapshot(
            target: "search",
            kind: "search",
            loading: session.loading,
            error: session.error,
            rows: projected,
            hasMore: session.canLoadMore
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            let idx = request.params["index"]?.intValue ?? 0
            guard rows.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Novel search index \(idx) out of range")
            }
            onSelect?(rows[idx].book)
            return .object(["openedBookId": .string(rows[idx].book.id)])
        case "list.load_more":
            session.loadMore()
            return .object(["triggered": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelSearchResultsContent")
        }
    }
}

// MARK: - Novel Settings Controllers

extension NovelSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public func debugOpenSection(_ section: String) throws {
        let page: UIViewController
        switch section {
        case "sources":
            page = NovelSourcesViewController(feature: feature, themeProvider: themeProvider)
        case "rules":
            page = NovelRulesViewController(feature: feature, themeProvider: themeProvider)
        case "appearance":
            page = NovelAppearanceViewController(feature: feature, themeProvider: themeProvider)
        default:
            throw DebugAPIError.notFound("Unknown novel settings section: \(section)")
        }
        navigationController?.pushViewController(page, animated: false)
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.novel",
            moduleId: "novel",
            route: "settings/novel",
            title: "小说设置",
            settingsDomain: "novel",
            settingsSection: nil
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let sources = feature.sources.prefix(maxItems).map { s -> DebugJSONValue in
            .object([
                "id": .string(s.installationId),
                "name": .string(s.name),
                "enabled": .bool(s.enabled),
                "enabledExplore": .bool(s.enabledExplore),
                "errorCode": .optional(s.errorCode)
            ])
        }
        return DebugContentSnapshot(
            target: "settings.novel",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object([
                "domain": .string("novel"),
                "section": .string("root"),
                "sections": .array(["sources", "rules", "appearance"].map(DebugJSONValue.string)),
                "sources": .array(sources),
                "rulesCount": .int(feature.local.rules.count),
                "booksCount": .int(feature.local.entries.count)
            ])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelSettingsViewController")
    }
}

extension NovelSourcesViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.novel", moduleId: "novel", route: "settings/novel/sources", title: "书源管理", settingsDomain: "novel", settingsSection: "sources")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.novel", kind: "settings", settings: .object(["domain": .string("novel"), "section": .string("sources")]))
    }
}

extension NovelRulesViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.novel", moduleId: "novel", route: "settings/novel/rules", title: "替换净化", settingsDomain: "novel", settingsSection: "rules")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.novel", kind: "settings", settings: .object(["domain": .string("novel"), "section": .string("rules")]))
    }
}

extension NovelAppearanceViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.novel", moduleId: "novel", route: "settings/novel/appearance", title: "阅读排版", settingsDomain: "novel", settingsSection: "appearance")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.novel", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("novel"), "section": .string("appearance")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelAppearanceViewController")
    }
}
#endif
