#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

// MARK: - NovelHomeViewController

extension NovelHomeViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public func debugCategoryModel(category: String?, sectionIndex: Int?) -> NovelCategoryModel? {
        if let sectionIndex {
            guard discovery.sections.indices.contains(sectionIndex) else { return nil }
            return discovery.categoryModel(sectionID: discovery.sections[sectionIndex].id)
        }
        if let category, !category.isEmpty {
            if let direct = discovery.categoryModel(sectionID: category) { return direct }
            if let matched = discovery.sections.first(where: { $0.id == category || $0.title == category }) {
                return discovery.categoryModel(sectionID: matched.id)
            }
            return nil
        }
        for sec in discovery.sections {
            if let model = discovery.categoryModel(sectionID: sec.id) { return model }
        }
        return nil
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
        var remaining = maxItems
        let projectedSections = discovery.sections.enumerated().map { sIdx, sec -> DebugContentSection in
            let take = min(remaining, sec.books.count)
            remaining = max(0, remaining - take)
            let items = sec.books.prefix(take).enumerated().map { iIdx, book in
                DebugContentItem(
                    index: iIdx,
                    id: book.id,
                    kind: "novel",
                    title: book.name,
                    subtitle: book.author.isEmpty ? nil : book.author,
                    badge: book.latestChapterTitle.isEmpty ? nil : book.latestChapterTitle,
                    coverUrl: book.coverUrl.isEmpty ? nil : book.coverUrl,
                    actions: ["open"],
                    meta: ["installationId": .string(book.installationId), "bookUrl": .string(book.bookUrl)]
                )
            }
            return DebugContentSection(
                index: sIdx,
                id: sec.id,
                title: sec.title,
                kind: "novel",
                layout: "rail",
                hasMore: !sec.options.isEmpty,
                itemCount: sec.books.count,
                items: items
            )
        }
        return DebugContentSnapshot(
            target: "novel.home",
            kind: projectedSections.isEmpty ? "empty" : "sections",
            loading: discovery.loading,
            error: discovery.error,
            sections: projectedSections,
            extra: [
                "selectedSource": discovery.selectedSource.map { .object(["id": .string($0.id), "name": .string($0.name)]) } ?? .null
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.item.invoke":
            let sec = try resolveSection(request.params)
            let book: NovelBook
            if let iIdx = request.params["itemIndex"]?.intValue {
                guard sec.books.indices.contains(iIdx) else {
                    throw DebugAPIError.invalidArgument("itemIndex \(iIdx) out of range (0..<\(sec.books.count))")
                }
                book = sec.books[iIdx]
            } else if let id = request.params["itemId"]?.stringValue {
                guard let found = sec.books.first(where: { $0.id == id || $0.bookUrl == id || $0.name == id }) else {
                    throw DebugAPIError.notFound("Novel book not found: \(id)")
                }
                book = found
            } else if let first = sec.books.first {
                book = first
            } else {
                throw DebugAPIError.invalidState("Target novel section has no items")
            }
            onSelect?(book)
            return .object(["openedBookId": .string(book.id), "name": .string(book.name)])

        case "section.more":
            let sec = try resolveSection(request.params)
            guard let catModel = discovery.categoryModel(sectionID: sec.id) else {
                throw DebugAPIError.invalidState("Novel section does not have a category view")
            }
            onSelectCategory?(catModel)
            return .object(["openedSection": .string(catModel.sectionID), "title": .string(catModel.title)])

        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelHomeViewController")
        }
    }

    private func resolveSection(_ params: [String: DebugJSONValue]) throws -> NovelHomeSection {
        if let sId = params["sectionId"]?.stringValue {
            guard let found = discovery.sections.first(where: { $0.id == sId || $0.title == sId }) else {
                throw DebugAPIError.notFound("Novel section not found: \(sId)")
            }
            return found
        }
        let sIdx = params["sectionIndex"]?.intValue ?? 0
        guard discovery.sections.indices.contains(sIdx) else {
            throw DebugAPIError.invalidArgument("sectionIndex \(sIdx) out of range (0..<\(discovery.sections.count))")
        }
        return discovery.sections[sIdx]
    }

    public func debugRefresh() {
        discovery.refresh()
    }
}

// MARK: - NovelCategoryViewController

extension NovelCategoryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugBooks: [NovelBook] { renderedBooks }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "novel.data",
            moduleId: "novel",
            route: "novel/data/\(model.sectionID)",
            title: model.title,
            isBusy: model.isLoading,
            isError: model.error != nil,
            errorText: model.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let filters: [DebugFilterItem] = model.options.count > 1
            ? model.options.enumerated().map { idx, opt in
                DebugFilterItem(
                    group: "category",
                    index: idx,
                    id: opt.id,
                    title: opt.title,
                    selected: opt.id == model.selectedOptionID
                )
            }
            : []
        let items = renderedBooks.prefix(maxItems).enumerated().map { idx, book in
            DebugContentItem(
                index: idx,
                id: book.id,
                kind: "novel",
                title: book.name,
                subtitle: book.author.isEmpty ? nil : book.author,
                badge: book.latestChapterTitle.isEmpty ? nil : book.latestChapterTitle,
                coverUrl: book.coverUrl.isEmpty ? nil : book.coverUrl,
                actions: ["open"],
                meta: ["installationId": .string(book.installationId), "bookUrl": .string(book.bookUrl)]
            )
        }
        let sec = DebugContentSection(
            index: 0,
            id: model.sectionID,
            title: model.title,
            kind: "novel",
            layout: "grid",
            hasMore: model.hasMore,
            itemCount: renderedBooks.count,
            items: items
        )
        return DebugContentSnapshot(
            target: "novel.data",
            kind: "sections",
            loading: model.isLoading,
            error: model.error,
            filters: filters,
            sections: [sec],
            hasMore: model.hasMore,
            extra: [
                "selectedSource": .object(["id": .string(model.source.id), "name": .string(model.source.name)])
            ]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.item.invoke", "list.item.invoke":
            let book: NovelBook
            if let idx = (request.params["itemIndex"] ?? request.params["index"])?.intValue {
                guard renderedBooks.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Novel item index \(idx) out of range (0..<\(renderedBooks.count))")
                }
                book = renderedBooks[idx]
            } else if let id = (request.params["itemId"] ?? request.params["id"])?.stringValue {
                guard let found = renderedBooks.first(where: { $0.id == id || $0.bookUrl == id || $0.name == id }) else {
                    throw DebugAPIError.notFound("Novel book not found: \(id)")
                }
                book = found
            } else if let first = renderedBooks.first {
                book = first
            } else {
                throw DebugAPIError.invalidState("Novel category list is empty")
            }
            onSelectItem?(book)
            return .object(["openedBookId": .string(book.id), "name": .string(book.name)])

        case "tab.select":
            guard model.options.count > 1 else {
                throw DebugAPIError.invalidState("Current novel section has no selectable sub-category filters")
            }
            let value: String
            if let id = request.params["id"]?.stringValue {
                guard let found = model.options.first(where: { $0.id == id || $0.title == id || $0.url == id }) else {
                    throw DebugAPIError.notFound("Novel filter option not found: \(id)")
                }
                value = found.id
            } else if let idx = request.params["index"]?.intValue {
                guard model.options.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Filter index \(idx) out of range (0..<\(model.options.count))")
                }
                value = model.options[idx].id
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or 'index'")
            }
            _ = model.selectOption(value)
            return .object(["group": .string("category"), "selected": .string(value)])

        case "list.load_more":
            _ = model.loadMore()
            return .object(["triggered": .bool(true)])

        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelCategoryViewController")
        }
    }

    public func debugRefresh() {
        _ = model.refresh()
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
        if pages.indices.contains(currentPage), let blocks = session.content?.blocks {
            pageText = pages[currentPage].elements.compactMap { element -> String? in
                guard blocks.indices.contains(element.blockIndex),
                      let blockText = blocks[element.blockIndex].text,
                      let range = element.range else { return nil }
                let ns = blockText as NSString
                guard range.location >= 0, NSMaxRange(range) <= ns.length else { return nil }
                return ns.substring(with: range)
            }.joined(separator: "\n")
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

extension NovelSourcesViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.novel", moduleId: "novel", route: "settings/novel/sources", title: "书源管理", settingsDomain: "novel", settingsSection: "sources")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.novel", kind: "settings", rows: debugManagementRows(maxItems: maxItems), settings: .object(["domain": .string("novel"), "section": .string("sources")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeManagementRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelSourcesViewController")
    }
}

extension NovelRulesViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.novel", moduleId: "novel", route: "settings/novel/rules", title: "替换净化", settingsDomain: "novel", settingsSection: "rules")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.novel", kind: "settings", rows: debugManagementRows(maxItems: maxItems), settings: .object(["domain": .string("novel"), "section": .string("rules")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeManagementRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on NovelRulesViewController")
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
